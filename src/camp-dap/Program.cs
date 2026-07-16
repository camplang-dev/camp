using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OmniSharp.Extensions.DebugAdapter.Protocol.Models;

namespace Camp.DebugAdapter;

internal static class Program
{
	static async Task<int> Main()
	{
		DapSession session = new(Console.OpenStandardInput(), Console.OpenStandardOutput());
		await session.Run();
		return 0;
	}
}

sealed class DapSession(Stream input, Stream output)
{
	readonly DapProtocol protocol = new(input, output);
	IDebugBackend? backend;
	int nextSeq = 1;
	bool running = true;

	public async Task Run()
	{
		while (running && await protocol.ReadMessage() is JsonObject request)
			await Handle(request);
	}

	async Task Handle(JsonObject request)
	{
		int requestSeq = request["seq"]?.GetValue<int>() ?? 0;
		string command = request["command"]?.GetValue<string>() ?? "";
		JsonObject arguments = request["arguments"] as JsonObject ?? [];
		try
		{
			JsonNode? body = command switch
			{
				"initialize" => new JsonObject
				{
					["supportsConfigurationDoneRequest"] = true,
					["supportsEvaluateForHovers"] = true,
					["supportsStepBack"] = false
				},
				"launch" => await Launch(arguments),
				"setBreakpoints" => await SetBreakpoints(arguments),
				"configurationDone" => await ConfigurationDone(),
				"threads" => new JsonObject { ["threads"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "Main Thread" }) },
				"stackTrace" => StackTrace(),
				"scopes" => Scopes(arguments),
				"variables" => Variables(arguments),
				"evaluate" => Evaluate(arguments),
				"continue" => await Continue(arguments),
				"pause" => await Pause(arguments),
				"next" => await Step("next", arguments),
				"stepIn" => await Step("stepIn", arguments),
				"stepOut" => await Step("stepOut", arguments),
				"disconnect" => await Disconnect(),
				_ => throw new InvalidOperationException($"DAP command '{command}' is not supported.")
			};
			await Respond(requestSeq, command, success: true, body);
		}
		catch (Exception ex)
		{
			await Respond(requestSeq, command, success: false, null, ex.Message);
		}
	}

	async Task<JsonNode?> Launch(JsonObject arguments)
	{
		string backendName = arguments["backend"]?.GetValue<string>() ?? "fake";
		string project = arguments["project"]?.GetValue<string>() ?? "";
		string cwd = arguments["cwd"]?.GetValue<string>() ?? Directory.GetCurrentDirectory();
		IReadOnlyList<string> args = arguments["args"] is JsonArray array
			? array.Select(item => item?.GetValue<string>() ?? "").ToList()
			: [];
		backend = CreateBackend(backendName);
		await backend.Launch(new DebugLaunchOptions(project, cwd, args, arguments["stopOnEntry"]?.GetValue<bool>() == true));
		await Event("initialized", null);
		return null;
	}

	static IDebugBackend CreateBackend(string name)
	{
		return name switch
		{
			"auto" when OperatingSystem.IsMacOS() => new LldbDebugBackend(),
			"auto" when OperatingSystem.IsLinux() => new GdbDebugBackend(),
			"auto" when OperatingSystem.IsWindows() => new CdbDebugBackend(),
			"auto" => throw new InvalidOperationException("Debug backend 'auto' could not select a supported backend for this platform yet."),
			"fake" => new FakeDebugBackend(),
			"lldb" => new LldbDebugBackend(),
			"gdb" => new GdbDebugBackend(),
			"cdb" => new CdbDebugBackend(),
			_ => throw new InvalidOperationException($"Debug backend '{name}' is not available in this build yet.")
		};
	}

	async Task<JsonNode?> SetBreakpoints(JsonObject arguments)
	{
		IDebugBackend backend = RequireBackend();
		string source = arguments["source"]?["path"]?.GetValue<string>() ?? "";
		List<int> lines = [];
		if (arguments["breakpoints"] is JsonArray breakpoints)
			foreach (JsonNode? breakpoint in breakpoints)
				if (breakpoint?["line"] is JsonNode line)
					lines.Add(line.GetValue<int>());
		IReadOnlyList<DebugBreakpoint> resolved = await backend.SetBreakpoints(source, lines);
		return new JsonObject
		{
			["breakpoints"] = new JsonArray(resolved.Select(bp => new JsonObject
			{
				["verified"] = bp.Verified,
				["line"] = bp.Line,
				["message"] = bp.Message
			}).ToArray<JsonNode?>())
		};
	}

	async Task<JsonNode?> ConfigurationDone()
	{
		IDebugBackend backend = RequireBackend();
		await backend.ConfigurationDone();
		if (backend.StopOnEntry || backend.IsStopped)
			await Event("stopped", new JsonObject { ["reason"] = backend.StopOnEntry ? "entry" : "breakpoint", ["threadId"] = 1 });
		return null;
	}

	JsonNode StackTrace()
	{
		IDebugBackend backend = RequireBackend();
		IReadOnlyList<DebugStackFrame> frames = backend.GetStackTrace();
		return new JsonObject
		{
			["stackFrames"] = new JsonArray(frames.Select(frame => new JsonObject
			{
				["id"] = frame.Id,
				["name"] = frame.Name,
				["line"] = frame.Line,
				["column"] = frame.Column,
				["source"] = new JsonObject
				{
					["name"] = Path.GetFileName(frame.SourcePath),
					["path"] = frame.SourcePath
				}
			}).ToArray<JsonNode?>()),
			["totalFrames"] = frames.Count
		};
	}

	JsonNode Scopes(JsonObject arguments)
	{
		IDebugBackend backend = RequireBackend();
		int frameId = arguments["frameId"]?.GetValue<int>() ?? 1;
		IReadOnlyList<DebugScope> scopes = backend.GetScopes(frameId);
		return new JsonObject
		{
			["scopes"] = new JsonArray(scopes.Select(scope => new JsonObject
			{
				["name"] = scope.Name,
				["variablesReference"] = scope.Reference,
				["expensive"] = false
			}).ToArray<JsonNode?>())
		};
	}

	JsonNode Variables(JsonObject arguments)
	{
		IDebugBackend backend = RequireBackend();
		int reference = arguments["variablesReference"]?.GetValue<int>() ?? 0;
		IReadOnlyList<DebugVariable> variables = backend.GetVariables(reference);
		return new JsonObject
		{
			["variables"] = new JsonArray(variables.Select(variable => new JsonObject
			{
				["name"] = variable.Name,
				["value"] = variable.Value,
				["type"] = variable.Type,
				["variablesReference"] = variable.Reference
			}).ToArray<JsonNode?>())
		};
	}

	JsonNode Evaluate(JsonObject arguments)
	{
		IDebugBackend backend = RequireBackend();
		string expression = arguments["expression"]?.GetValue<string>() ?? "";
		DebugVariable variable = backend.Evaluate(expression);
		return new JsonObject
		{
			["result"] = variable.Value,
			["type"] = variable.Type,
			["variablesReference"] = variable.Reference
		};
	}

	async Task<JsonNode?> Continue(JsonObject arguments)
	{
		IDebugBackend backend = RequireBackend();
		await backend.Continue(arguments["threadId"]?.GetValue<int>() ?? 1);
		await Event("continued", new JsonObject { ["threadId"] = 1, ["allThreadsContinued"] = true });
		return new JsonObject { ["allThreadsContinued"] = true };
	}

	async Task<JsonNode?> Pause(JsonObject arguments)
	{
		IDebugBackend backend = RequireBackend();
		await backend.Pause(arguments["threadId"]?.GetValue<int>() ?? 1);
		await Event("stopped", new JsonObject { ["reason"] = "pause", ["threadId"] = 1 });
		return null;
	}

	async Task<JsonNode?> Step(string command, JsonObject arguments)
	{
		IDebugBackend backend = RequireBackend();
		await backend.Step(command, arguments["threadId"]?.GetValue<int>() ?? 1);
		await Event("stopped", new JsonObject { ["reason"] = "step", ["threadId"] = 1 });
		return null;
	}

	async Task<JsonNode?> Disconnect()
	{
		if (backend is not null)
			await backend.Disconnect();
		running = false;
		return null;
	}

	IDebugBackend RequireBackend()
	{
		return backend ?? throw new InvalidOperationException("Debug session has not been launched.");
	}

	async Task Respond(int requestSeq, string command, bool success, JsonNode? body, string? message = null)
	{
		JsonObject response = new()
		{
			["seq"] = nextSeq++,
			["type"] = "response",
			["request_seq"] = requestSeq,
			["success"] = success,
			["command"] = command
		};
		if (message is not null)
			response["message"] = message;
		if (body is not null)
			response["body"] = body;
		await protocol.WriteMessage(response);
	}

	async Task Event(string eventName, JsonObject? body)
	{
		JsonObject message = new()
		{
			["seq"] = nextSeq++,
			["type"] = "event",
			["event"] = eventName
		};
		if (body is not null)
			message["body"] = body;
		await protocol.WriteMessage(message);
	}
}

sealed class DapProtocol(Stream input, Stream output)
{
	static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

	public async Task<JsonObject?> ReadMessage()
	{
		Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
		while (true)
		{
			string? line = await ReadAsciiLine(input);
			if (line is null)
				return null;
			if (line.Length == 0)
				break;
			int separator = line.IndexOf(':');
			if (separator > 0)
				headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
		}
		if (!headers.TryGetValue("Content-Length", out string? lengthText) || !int.TryParse(lengthText, out int length))
			return null;
		byte[] payload = new byte[length];
		int read = 0;
		while (read < length)
		{
			int count = await input.ReadAsync(payload.AsMemory(read, length - read));
			if (count == 0)
				return null;
			read += count;
		}
		return JsonNode.Parse(Encoding.UTF8.GetString(payload)) as JsonObject;
	}

	public async Task WriteMessage(JsonObject message)
	{
		byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
		byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + payload.Length + "\r\n\r\n");
		await output.WriteAsync(header);
		await output.WriteAsync(payload);
		await output.FlushAsync();
	}

	static async Task<string?> ReadAsciiLine(Stream stream)
	{
		List<byte> bytes = [];
		while (true)
		{
			int value = stream.ReadByte();
			if (value < 0)
				return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
			if (value == '\n')
				return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
			bytes.Add((byte)value);
			await Task.Yield();
		}
	}
}

interface IDebugBackend
{
	bool StopOnEntry { get; }
	bool IsStopped { get; }
	Task Launch(DebugLaunchOptions options);
	Task<IReadOnlyList<DebugBreakpoint>> SetBreakpoints(string source, IReadOnlyList<int> lines);
	Task ConfigurationDone();
	Task Continue(int threadId);
	Task Pause(int threadId);
	Task Step(string command, int threadId);
	IReadOnlyList<DebugStackFrame> GetStackTrace();
	IReadOnlyList<DebugScope> GetScopes(int frameId);
	IReadOnlyList<DebugVariable> GetVariables(int reference);
	DebugVariable Evaluate(string expression);
	Task Disconnect();
}

sealed class FakeDebugBackend : IDebugBackend
{
	string source = "fake.camp";
	public bool StopOnEntry { get; private set; }
	public bool IsStopped => StopOnEntry;

	public Task Launch(DebugLaunchOptions options)
	{
		if (options.Project.Contains("fail", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Fake backend launch failure.");
		StopOnEntry = options.StopOnEntry;
		if (!string.IsNullOrWhiteSpace(options.Project))
			source = Path.GetFullPath(options.Project);
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<DebugBreakpoint>> SetBreakpoints(string source, IReadOnlyList<int> lines)
	{
		IReadOnlyList<DebugBreakpoint> breakpoints = lines.Select(line => new DebugBreakpoint(line, true)).ToList();
		return Task.FromResult(breakpoints);
	}

	public Task ConfigurationDone() => Task.CompletedTask;
	public Task Continue(int threadId) => Task.CompletedTask;
	public Task Pause(int threadId) => Task.CompletedTask;
	public Task Step(string command, int threadId) => Task.CompletedTask;
	public Task Disconnect() => Task.CompletedTask;

	public IReadOnlyList<DebugStackFrame> GetStackTrace() => [new DebugStackFrame(1, "main", source, 1, 1)];

	public IReadOnlyList<DebugScope> GetScopes(int frameId) =>
	[
		new DebugScope("Parameters", 100),
		new DebugScope("Locals", 200)
	];

	public IReadOnlyList<DebugVariable> GetVariables(int reference)
	{
		return reference switch
		{
			100 => [new DebugVariable("args", "{ elements, length }", "string[]", 300)],
			200 => [new DebugVariable("answer", "42", "int", 0)],
			300 =>
			[
				new DebugVariable("elements", "0x00000000", "string*", 0),
				new DebugVariable("length", "0", "nuint", 0)
			],
			_ => []
		};
	}

	public DebugVariable Evaluate(string expression)
	{
		return expression switch
		{
			"answer" => new DebugVariable("answer", "42", "int", 0),
			"args" => new DebugVariable("args", "{ elements, length }", "string[]", 300),
			_ => new DebugVariable(expression, "Unsupported expression", null, 0)
		};
	}
}

sealed class LldbDebugBackend : IDebugBackend
{
	readonly List<(string Source, int Line)> pendingBreakpoints = [];
	readonly List<DebugStackFrame> lastFrames = [];
	readonly Dictionary<int, IReadOnlyList<DebugVariable>> variableReferences = new();
	readonly Dictionary<string, DebugVariable> evaluateVariables = new(StringComparer.Ordinal);
	string executable = "";
	string buildDirectory = "";
	DebugMapDocument? debugMap;
	string? stoppedNativeSymbol;
	int nextVariableReference = 1000;

	public bool StopOnEntry { get; private set; }
	public bool IsStopped { get; private set; }

	public async Task Launch(DebugLaunchOptions options)
	{
		if (!OperatingSystem.IsMacOS())
			throw new InvalidOperationException("Debug backend 'lldb' is only available on macOS in this build.");
		if (!await CommandExists("lldb"))
			throw new InvalidOperationException("Debug backend 'lldb' is not available because lldb was not found on PATH.");
		if (string.IsNullOrWhiteSpace(options.Project))
			throw new InvalidOperationException("Launch requires a 'project' path.");

		StopOnEntry = options.StopOnEntry;
		buildDirectory = Path.Combine(Path.GetTempPath(), "camp-dap-lldb-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(buildDirectory);
		DebugBuildResult build = await BuildExecutable(options.Project, options.Cwd, buildDirectory);
		executable = build.Executable;
		debugMap = build.DebugMapPath is null ? null : DebugMapDocument.Load(build.DebugMapPath);
		if (StopOnEntry)
			pendingBreakpoints.Add(("", 0));
	}

	public async Task<IReadOnlyList<DebugBreakpoint>> SetBreakpoints(string source, IReadOnlyList<int> lines)
	{
		List<DebugBreakpoint> results = [];
		foreach (int line in lines)
		{
			pendingBreakpoints.Add((source, line));
			bool verified = File.Exists(source) && line > 0;
			results.Add(new DebugBreakpoint(line, verified, verified ? null : "Breakpoint source could not be verified before launch."));
		}
		await Task.CompletedTask;
		return results;
	}

	public async Task ConfigurationDone()
	{
		string output = await RunLldbBatch("run", "thread backtrace", "frame variable --show-types");
		IsStopped = output.Contains(" stopped", StringComparison.OrdinalIgnoreCase);
		UpdateFramesFromOutput(output);
		UpdateVariablesFromOutput(output);
	}

	public async Task Continue(int threadId)
	{
		await Task.CompletedTask;
		IsStopped = false;
	}

	public async Task Pause(int threadId)
	{
		await Task.CompletedTask;
		IsStopped = true;
	}

	public async Task Step(string command, int threadId)
	{
		string lldbCommand = command switch
		{
			"stepIn" => "thread step-in",
			"stepOut" => "thread step-out",
			_ => "thread step-over"
		};
		string output = await RunLldbBatch("run", lldbCommand, "thread backtrace", "frame variable --show-types");
		IsStopped = output.Contains(" stopped", StringComparison.OrdinalIgnoreCase);
		UpdateFramesFromOutput(output);
		UpdateVariablesFromOutput(output);
	}

	public IReadOnlyList<DebugStackFrame> GetStackTrace()
	{
		return lastFrames.Count == 0
			? [new DebugStackFrame(1, "main", executable, 1, 1)]
			: lastFrames;
	}

	public IReadOnlyList<DebugScope> GetScopes(int frameId) =>
	[
		new DebugScope("Parameters", 100),
		new DebugScope("Locals", 200)
	];

	public IReadOnlyList<DebugVariable> GetVariables(int reference) =>
		variableReferences.TryGetValue(reference, out IReadOnlyList<DebugVariable>? variables) ? variables : [];

	public DebugVariable Evaluate(string expression)
	{
		if (evaluateVariables.TryGetValue(expression, out DebugVariable? variable))
			return variable with { Name = expression };
		return new DebugVariable(expression, "Unsupported expression", null, 0);
	}

	public async Task Disconnect()
	{
		await Task.CompletedTask;
	}

	void UpdateFramesFromOutput(string output)
	{
		List<DebugStackFrame> frames = [];
		foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			int frameIndex = line.IndexOf("frame #", StringComparison.Ordinal);
			if (frameIndex < 0)
				continue;
			int at = line.LastIndexOf(" at ", StringComparison.Ordinal);
			if (at < 0)
				continue;
			string location = line[(at + 4)..].Trim();
			if (!TryParseLocation(location, out string? path, out int sourceLine, out int column))
				continue;
			if (!Path.IsPathRooted(path))
				path = pendingBreakpoints
					.Select(item => item.Source)
					.FirstOrDefault(source => Path.GetFileName(source).Equals(path, StringComparison.OrdinalIgnoreCase)) ?? path;
			string name = ParseFrameName(line);
			frames.Add(new DebugStackFrame(frames.Count + 1, name, path, sourceLine, column));
			if (frames.Count == 1)
				stoppedNativeSymbol = name;
		}
		if (frames.Count > 0)
		{
			lastFrames.Clear();
			lastFrames.AddRange(frames);
		}
	}

	static bool TryParseLocation(string location, out string path, out int line, out int column)
	{
		path = "";
		line = 1;
		column = 1;
		int secondColon = location.LastIndexOf(':');
		if (secondColon < 0 || !int.TryParse(location[(secondColon + 1)..], out column))
			return false;
		int firstColon = location.LastIndexOf(':', secondColon - 1);
		if (firstColon < 0 || !int.TryParse(location[(firstColon + 1)..secondColon], out line))
			return false;
		path = location[..firstColon];
		return path.Length > 0;
	}

	static string ParseFrameName(string line)
	{
		int tick = line.IndexOf('`');
		if (tick >= 0)
		{
			int end = line.IndexOf(' ', tick);
			string name = end > tick ? line[(tick + 1)..end] : line[(tick + 1)..];
			int plus = name.IndexOf('+');
			name = plus > 0 ? name[..plus] : name;
			int paren = name.IndexOf('(');
			return (paren > 0 ? name[..paren] : name).Trim();
		}
		int frame = line.IndexOf("frame #", StringComparison.Ordinal);
		return frame >= 0 ? line[frame..].Trim() : "frame";
	}

	static string CleanLldbOutput(string output)
	{
		return output.Replace("(lldb) ", "", StringComparison.Ordinal).Trim();
	}

	void UpdateVariablesFromOutput(string output)
	{
		Dictionary<string, LldbNativeVariable> nativeVariables = ParseNativeVariables(output);
		variableReferences.Clear();
		evaluateVariables.Clear();
		variableReferences[100] = [];
		variableReferences[200] = [];
		if (debugMap is null || stoppedNativeSymbol is null)
			return;
		DebugMapFunction? function = debugMap.FindFunction(stoppedNativeSymbol);
		if (function is null)
			return;

		HashSet<string> hidden = new(StringComparer.Ordinal);
		Dictionary<string, DebugVariable> parameters = new(StringComparer.Ordinal);
		Dictionary<string, DebugVariable> locals = new(StringComparer.Ordinal);
		foreach (DebugMapVariable variable in function.Variables)
		{
			if (!nativeVariables.TryGetValue(variable.NativeName, out LldbNativeVariable? native))
				continue;
			if (variable.CampName.EndsWith("_length", StringComparison.Ordinal))
			{
				string baseName = variable.CampName[..^"_length".Length];
				DebugMapVariable? baseVariable = function.Variables.FirstOrDefault(item => item.CampName == baseName);
				if (baseVariable is not null && nativeVariables.TryGetValue(baseVariable.NativeName, out LldbNativeVariable? baseNative))
				{
					int reference = nextVariableReference++;
					DebugVariable structured = new(baseName, "{ elements, length }", baseVariable.Type, reference);
					variableReferences[reference] =
					[
						new DebugVariable("elements", baseNative.Value, baseNative.Type, 0),
						new DebugVariable("length", native.Value, native.Type, 0)
					];
					AddVariable(baseVariable.Kind, structured, parameters, locals);
					evaluateVariables[baseName] = structured;
					hidden.Add(baseName);
					hidden.Add(variable.CampName);
				}
				continue;
			}
			if (hidden.Contains(variable.CampName))
				continue;
			DebugVariable debugVariable = new(variable.CampName, native.Value, variable.Type ?? native.Type, 0);
			AddVariable(variable.Kind, debugVariable, parameters, locals);
			evaluateVariables[variable.CampName] = debugVariable;
		}
		variableReferences[100] = parameters.Values.ToList();
		variableReferences[200] = locals.Values.ToList();
	}

	static void AddVariable(string kind, DebugVariable variable, Dictionary<string, DebugVariable> parameters, Dictionary<string, DebugVariable> locals)
	{
		if (kind.Equals("parameter", StringComparison.OrdinalIgnoreCase))
			parameters[variable.Name] = variable;
		else
			locals[variable.Name] = variable;
	}

	static Dictionary<string, LldbNativeVariable> ParseNativeVariables(string output)
	{
		Dictionary<string, LldbNativeVariable> variables = new(StringComparer.Ordinal);
		foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string line = rawLine.Trim();
			if (!line.StartsWith('('))
				continue;
			int close = line.IndexOf(')');
			int equals = line.IndexOf(" = ", StringComparison.Ordinal);
			if (close < 0 || equals < close)
				continue;
			string type = line[1..close].Trim();
			string name = line[(close + 1)..equals].Trim();
			string value = line[(equals + 3)..].Trim();
			if (name.Length > 0)
				variables[name] = new LldbNativeVariable(type, value);
		}
		return variables;
	}

	internal static async Task<DebugBuildResult> BuildExecutable(string project, string cwd, string outDirectory)
	{
		string campc = Path.Combine(FindRepositoryRoot(), "bin", "campc");
		if (!File.Exists(campc))
			campc = Path.Combine(FindRepositoryRoot(), "bin", "campc.dll");
		List<string> args = ["build", project, "--profile", "DEBUG", "--artifact", "exec", "--debug-info", "--out-dir", outDirectory];
		ProcessStartInfo info = new()
		{
			FileName = campc.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : campc,
			WorkingDirectory = cwd,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		if (campc.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			info.ArgumentList.Add(campc);
		foreach (string arg in args)
			info.ArgumentList.Add(arg);
		using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start campc.");
		string stdout = await process.StandardOutput.ReadToEndAsync();
		string stderr = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		if (process.ExitCode != 0)
			throw new InvalidOperationException("Camp build failed." + Environment.NewLine + stdout + stderr);
		string? executable = Directory.EnumerateFiles(outDirectory, "*", SearchOption.AllDirectories)
			.Where(path => OperatingSystem.IsWindows()
				? path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
				: !Path.HasExtension(path))
			.FirstOrDefault(path => IsExecutable(path));
		if (executable is null)
			throw new InvalidOperationException("Camp build completed but no executable artifact was found." + Environment.NewLine + stdout);
		string? debugMap = Directory.EnumerateFiles(outDirectory, "*.campdebug.json", SearchOption.AllDirectories).FirstOrDefault();
		return new DebugBuildResult(executable, debugMap);
	}

	internal static bool IsExecutable(string path)
	{
		try
		{
			return (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
		}
		catch
		{
			return true;
		}
	}

	internal static async Task<bool> CommandExists(string command)
	{
		ProcessStartInfo info = new("which", command)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start which.");
		await process.WaitForExitAsync();
		return process.ExitCode == 0;
	}

	internal static string QuoteLldbArgument(string value)
	{
		return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
	}

	async Task<string> RunLldbBatch(params string[] commands)
	{
		if (string.IsNullOrEmpty(executable))
			throw new InvalidOperationException("LLDB session has not been launched.");
		ProcessStartInfo info = new("lldb")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		info.ArgumentList.Add("--batch");
		info.ArgumentList.Add("--no-lldbinit");
		foreach ((string source, int line) in pendingBreakpoints)
		{
			info.ArgumentList.Add("--one-line");
			info.ArgumentList.Add(line == 0
				? "breakpoint set --name main"
				: $"breakpoint set --file {QuoteLldbArgument(source)} --line {line}");
		}
		foreach (string command in commands)
		{
			info.ArgumentList.Add("--one-line");
			info.ArgumentList.Add(command);
		}
		info.ArgumentList.Add(executable);
		using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start lldb.");
		string stdout = await process.StandardOutput.ReadToEndAsync();
		string stderr = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		string output = stdout + stderr;
		if (process.ExitCode != 0 && !output.Contains("Process ", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("LLDB command failed." + Environment.NewLine + output);
		return output;
	}

	internal static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "src", "camplang.sln")))
				return directory.FullName;
			directory = directory.Parent;
		}
		throw new InvalidOperationException("Could not find repository root.");
	}
}

sealed class GdbDebugBackend : IDebugBackend
{
	readonly List<(string Source, int Line)> pendingBreakpoints = [];
	readonly List<DebugStackFrame> lastFrames = [];
	readonly Dictionary<int, IReadOnlyList<DebugVariable>> variableReferences = new();
	readonly Dictionary<string, DebugVariable> evaluateVariables = new(StringComparer.Ordinal);
	string executable = "";
	string buildDirectory = "";
	DebugMapDocument? debugMap;
	string? stoppedNativeSymbol;
	int nextVariableReference = 2000;

	public bool StopOnEntry { get; private set; }
	public bool IsStopped { get; private set; }

	public async Task Launch(DebugLaunchOptions options)
	{
		if (!OperatingSystem.IsLinux())
			throw new InvalidOperationException("Debug backend 'gdb' is only available on Linux in this build.");
		if (!await LldbDebugBackend.CommandExists("gdb"))
			throw new InvalidOperationException("Debug backend 'gdb' is not available because gdb was not found on PATH.");
		if (string.IsNullOrWhiteSpace(options.Project))
			throw new InvalidOperationException("Launch requires a 'project' path.");

		StopOnEntry = options.StopOnEntry;
		buildDirectory = Path.Combine(Path.GetTempPath(), "camp-dap-gdb-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(buildDirectory);
		DebugBuildResult build = await LldbDebugBackend.BuildExecutable(options.Project, options.Cwd, buildDirectory);
		executable = build.Executable;
		debugMap = build.DebugMapPath is null ? null : DebugMapDocument.Load(build.DebugMapPath);
		if (StopOnEntry)
			pendingBreakpoints.Add(("", 0));
	}

	public async Task<IReadOnlyList<DebugBreakpoint>> SetBreakpoints(string source, IReadOnlyList<int> lines)
	{
		List<DebugBreakpoint> results = [];
		foreach (int line in lines)
		{
			pendingBreakpoints.Add((source, line));
			bool verified = File.Exists(source) && line > 0;
			results.Add(new DebugBreakpoint(line, verified, verified ? null : "Breakpoint source could not be verified before launch."));
		}
		await Task.CompletedTask;
		return results;
	}

	public async Task ConfigurationDone()
	{
		string output = await RunGdbBatch("run", "bt", "info args", "info locals");
		IsStopped = output.Contains("Breakpoint ", StringComparison.OrdinalIgnoreCase)
			|| output.Contains("Program received signal", StringComparison.OrdinalIgnoreCase);
		UpdateFramesFromOutput(output);
		UpdateVariablesFromOutput(output);
	}

	public async Task Continue(int threadId)
	{
		await Task.CompletedTask;
		IsStopped = false;
	}

	public async Task Pause(int threadId)
	{
		await Task.CompletedTask;
		IsStopped = true;
	}

	public async Task Step(string command, int threadId)
	{
		string gdbCommand = command switch
		{
			"stepIn" => "step",
			"stepOut" => "finish",
			_ => "next"
		};
		string output = await RunGdbBatch("run", gdbCommand, "bt", "info args", "info locals");
		IsStopped = output.Contains("Breakpoint ", StringComparison.OrdinalIgnoreCase)
			|| output.Contains(" at ", StringComparison.OrdinalIgnoreCase);
		UpdateFramesFromOutput(output);
		UpdateVariablesFromOutput(output);
	}

	public IReadOnlyList<DebugStackFrame> GetStackTrace()
	{
		return lastFrames.Count == 0
			? [new DebugStackFrame(1, "main", executable, 1, 1)]
			: lastFrames;
	}

	public IReadOnlyList<DebugScope> GetScopes(int frameId) =>
	[
		new DebugScope("Parameters", 100),
		new DebugScope("Locals", 200)
	];

	public IReadOnlyList<DebugVariable> GetVariables(int reference) =>
		variableReferences.TryGetValue(reference, out IReadOnlyList<DebugVariable>? variables) ? variables : [];

	public DebugVariable Evaluate(string expression)
	{
		if (evaluateVariables.TryGetValue(expression, out DebugVariable? variable))
			return variable with { Name = expression };
		return new DebugVariable(expression, "Unsupported expression", null, 0);
	}

	public async Task Disconnect()
	{
		await Task.CompletedTask;
	}

	void UpdateFramesFromOutput(string output)
	{
		List<DebugStackFrame> frames = [];
		foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string line = rawLine.Trim();
			if (!line.StartsWith('#'))
				continue;
			int at = line.LastIndexOf(" at ", StringComparison.Ordinal);
			if (at < 0)
				continue;
			string location = line[(at + 4)..].Trim();
			if (!TryParseGdbLocation(location, out string? path, out int sourceLine))
				continue;
			if (!Path.IsPathRooted(path))
				path = pendingBreakpoints
					.Select(item => item.Source)
					.FirstOrDefault(source => Path.GetFileName(source).Equals(path, StringComparison.OrdinalIgnoreCase)) ?? path;
			string name = ParseGdbFrameName(line);
			frames.Add(new DebugStackFrame(frames.Count + 1, name, path, sourceLine, 1));
			if (frames.Count == 1)
				stoppedNativeSymbol = name;
		}
		if (frames.Count > 0)
		{
			lastFrames.Clear();
			lastFrames.AddRange(frames);
		}
	}

	static bool TryParseGdbLocation(string location, out string path, out int line)
	{
		path = "";
		line = 1;
		int colon = location.LastIndexOf(':');
		if (colon < 0 || !int.TryParse(location[(colon + 1)..], out line))
			return false;
		path = location[..colon];
		return path.Length > 0;
	}

	static string ParseGdbFrameName(string line)
	{
		int inIndex = line.IndexOf(" in ", StringComparison.Ordinal);
		int start = inIndex >= 0 ? inIndex + 4 : line.IndexOf(' ') + 1;
		if (start < 0 || start >= line.Length)
			return "frame";
		int end = line.IndexOf('(', start);
		if (end < 0)
			end = line.IndexOf(" at ", start, StringComparison.Ordinal);
		if (end < 0)
			end = line.Length;
		string name = line[start..end].Trim();
		int space = name.LastIndexOf(' ');
		if (space >= 0)
			name = name[(space + 1)..];
		return name.Length == 0 ? "frame" : name;
	}

	void UpdateVariablesFromOutput(string output)
	{
		Dictionary<string, LldbNativeVariable> nativeVariables = ParseGdbVariables(output);
		variableReferences.Clear();
		evaluateVariables.Clear();
		variableReferences[100] = [];
		variableReferences[200] = [];
		if (debugMap is null || stoppedNativeSymbol is null)
			return;
		DebugMapFunction? function = debugMap.FindFunction(stoppedNativeSymbol);
		if (function is null)
			return;

		HashSet<string> hidden = new(StringComparer.Ordinal);
		Dictionary<string, DebugVariable> parameters = new(StringComparer.Ordinal);
		Dictionary<string, DebugVariable> locals = new(StringComparer.Ordinal);
		foreach (DebugMapVariable variable in function.Variables)
		{
			if (!nativeVariables.TryGetValue(variable.NativeName, out LldbNativeVariable? native))
				continue;
			if (variable.CampName.EndsWith("_length", StringComparison.Ordinal))
			{
				string baseName = variable.CampName[..^"_length".Length];
				DebugMapVariable? baseVariable = function.Variables.FirstOrDefault(item => item.CampName == baseName);
				if (baseVariable is not null && nativeVariables.TryGetValue(baseVariable.NativeName, out LldbNativeVariable? baseNative))
				{
					int reference = nextVariableReference++;
					DebugVariable structured = new(baseName, "{ elements, length }", baseVariable.Type, reference);
					variableReferences[reference] =
					[
						new DebugVariable("elements", baseNative.Value, baseNative.Type, 0),
						new DebugVariable("length", native.Value, native.Type, 0)
					];
					AddVariable(baseVariable.Kind, structured, parameters, locals);
					evaluateVariables[baseName] = structured;
					hidden.Add(baseName);
					hidden.Add(variable.CampName);
				}
				continue;
			}
			if (hidden.Contains(variable.CampName))
				continue;
			DebugVariable debugVariable = new(variable.CampName, native.Value, variable.Type ?? native.Type, 0);
			AddVariable(variable.Kind, debugVariable, parameters, locals);
			evaluateVariables[variable.CampName] = debugVariable;
		}
		variableReferences[100] = parameters.Values.ToList();
		variableReferences[200] = locals.Values.ToList();
	}

	static void AddVariable(string kind, DebugVariable variable, Dictionary<string, DebugVariable> parameters, Dictionary<string, DebugVariable> locals)
	{
		if (kind.Equals("parameter", StringComparison.OrdinalIgnoreCase))
			parameters[variable.Name] = variable;
		else
			locals[variable.Name] = variable;
	}

	static Dictionary<string, LldbNativeVariable> ParseGdbVariables(string output)
	{
		Dictionary<string, LldbNativeVariable> variables = new(StringComparer.Ordinal);
		foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string line = rawLine.Trim();
			int equals = line.IndexOf(" = ", StringComparison.Ordinal);
			if (equals <= 0 || line.StartsWith('#') || line.StartsWith("Breakpoint ", StringComparison.OrdinalIgnoreCase))
				continue;
			string name = line[..equals].Trim();
			string value = line[(equals + 3)..].Trim();
			if (name.Length > 0)
				variables[name] = new LldbNativeVariable("", value);
		}
		return variables;
	}

	async Task<string> RunGdbBatch(params string[] commands)
	{
		if (string.IsNullOrEmpty(executable))
			throw new InvalidOperationException("GDB session has not been launched.");
		ProcessStartInfo info = new("gdb")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		info.ArgumentList.Add("--batch");
		info.ArgumentList.Add("--quiet");
		foreach ((string source, int line) in pendingBreakpoints)
		{
			info.ArgumentList.Add("-ex");
			info.ArgumentList.Add(line == 0 ? "break main" : $"break {source}:{line}");
		}
		foreach (string command in commands)
		{
			info.ArgumentList.Add("-ex");
			info.ArgumentList.Add(command);
		}
		info.ArgumentList.Add(executable);
		using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start gdb.");
		string stdout = await process.StandardOutput.ReadToEndAsync();
		string stderr = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		string output = stdout + stderr;
		if (process.ExitCode != 0 && !output.Contains("Breakpoint ", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("GDB command failed." + Environment.NewLine + output);
		return output;
	}
}

sealed class CdbDebugBackend : IDebugBackend
{
	readonly List<(string Source, int Line)> pendingBreakpoints = [];
	readonly List<DebugStackFrame> lastFrames = [];
	readonly Dictionary<int, IReadOnlyList<DebugVariable>> variableReferences = new();
	readonly Dictionary<string, DebugVariable> evaluateVariables = new(StringComparer.Ordinal);
	string executable = "";
	string cdbPath = "";
	string buildDirectory = "";
	DebugMapDocument? debugMap;
	string? stoppedNativeSymbol;
	int nextVariableReference = 3000;

	public bool StopOnEntry { get; private set; }
	public bool IsStopped { get; private set; }

	public async Task Launch(DebugLaunchOptions options)
	{
		if (!OperatingSystem.IsWindows())
			throw new InvalidOperationException("Debug backend 'cdb' is only available on Windows in this build.");
		cdbPath = FindCdbPath() ?? throw new InvalidOperationException("Debug backend 'cdb' is not available because cdb.exe was not found. Install Windows Debugging Tools and ensure cdb.exe is on PATH or in the Windows Kits Debuggers folder.");
		if (string.IsNullOrWhiteSpace(options.Project))
			throw new InvalidOperationException("Launch requires a 'project' path.");

		StopOnEntry = options.StopOnEntry;
		buildDirectory = Path.Combine(Path.GetTempPath(), "camp-dap-cdb-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(buildDirectory);
		DebugBuildResult build = await LldbDebugBackend.BuildExecutable(options.Project, options.Cwd, buildDirectory);
		executable = build.Executable;
		debugMap = build.DebugMapPath is null ? null : DebugMapDocument.Load(build.DebugMapPath);
		if (StopOnEntry)
			pendingBreakpoints.Add(("", 0));
	}

	public async Task<IReadOnlyList<DebugBreakpoint>> SetBreakpoints(string source, IReadOnlyList<int> lines)
	{
		List<DebugBreakpoint> results = [];
		foreach (int line in lines)
		{
			pendingBreakpoints.Add((source, line));
			bool verified = File.Exists(source) && line > 0;
			results.Add(new DebugBreakpoint(line, verified, verified ? null : "Breakpoint source could not be verified before launch."));
		}
		await Task.CompletedTask;
		return results;
	}

	public async Task ConfigurationDone()
	{
		string output = await RunCdbBatch("g", "k", "dv");
		IsStopped = output.Contains("Breakpoint ", StringComparison.OrdinalIgnoreCase)
			|| output.Contains("breakpoint", StringComparison.OrdinalIgnoreCase);
		UpdateFramesFromOutput(output);
		UpdateVariablesFromOutput(output);
	}

	public async Task Continue(int threadId)
	{
		await Task.CompletedTask;
		IsStopped = false;
	}

	public async Task Pause(int threadId)
	{
		await Task.CompletedTask;
		IsStopped = true;
	}

	public async Task Step(string command, int threadId)
	{
		string cdbCommand = command switch
		{
			"stepIn" => "t",
			"stepOut" => "gu",
			_ => "p"
		};
		string output = await RunCdbBatch("g", cdbCommand, "k", "dv");
		IsStopped = output.Contains("Breakpoint ", StringComparison.OrdinalIgnoreCase)
			|| output.Contains("breakpoint", StringComparison.OrdinalIgnoreCase);
		UpdateFramesFromOutput(output);
		UpdateVariablesFromOutput(output);
	}

	public IReadOnlyList<DebugStackFrame> GetStackTrace()
	{
		return lastFrames.Count == 0
			? [new DebugStackFrame(1, "main", executable, 1, 1)]
			: lastFrames;
	}

	public IReadOnlyList<DebugScope> GetScopes(int frameId) =>
	[
		new DebugScope("Parameters", 100),
		new DebugScope("Locals", 200)
	];

	public IReadOnlyList<DebugVariable> GetVariables(int reference) =>
		variableReferences.TryGetValue(reference, out IReadOnlyList<DebugVariable>? variables) ? variables : [];

	public DebugVariable Evaluate(string expression)
	{
		if (evaluateVariables.TryGetValue(expression, out DebugVariable? variable))
			return variable with { Name = expression };
		return new DebugVariable(expression, "Unsupported expression", null, 0);
	}

	public async Task Disconnect()
	{
		await Task.CompletedTask;
	}

	void UpdateFramesFromOutput(string output)
	{
		List<DebugStackFrame> frames = [];
		foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string line = rawLine.Trim();
			if (!TryParseCdbFrame(line, out string? name, out string? path, out int sourceLine))
				continue;
			if (!Path.IsPathRooted(path))
				path = pendingBreakpoints
					.Select(item => item.Source)
					.FirstOrDefault(source => Path.GetFileName(source).Equals(path, StringComparison.OrdinalIgnoreCase)) ?? path;
			frames.Add(new DebugStackFrame(frames.Count + 1, name, path, sourceLine, 1));
			if (frames.Count == 1)
				stoppedNativeSymbol = name;
		}
		if (frames.Count > 0)
		{
			lastFrames.Clear();
			lastFrames.AddRange(frames);
		}
	}

	static bool TryParseCdbFrame(string line, out string name, out string path, out int sourceLine)
	{
		name = "";
		path = "";
		sourceLine = 1;
		int bracket = line.LastIndexOf('[');
		int at = line.LastIndexOf(" @ ", StringComparison.Ordinal);
		int close = line.LastIndexOf(']');
		if (bracket < 0 || at < bracket || close < at || !int.TryParse(line[(at + 3)..close].Trim(), out sourceLine))
			return false;
		path = line[(bracket + 1)..at].Trim();
		string before = line[..bracket].Trim();
		int bang = before.LastIndexOf('!');
		if (bang >= 0)
		{
			name = before[(bang + 1)..].Trim();
			int plus = name.IndexOf('+');
			if (plus > 0)
				name = name[..plus];
			int space = name.IndexOf(' ');
			if (space > 0)
				name = name[..space];
		}
		return name.Length > 0 && path.Length > 0;
	}

	void UpdateVariablesFromOutput(string output)
	{
		Dictionary<string, LldbNativeVariable> nativeVariables = ParseCdbVariables(output);
		variableReferences.Clear();
		evaluateVariables.Clear();
		variableReferences[100] = [];
		variableReferences[200] = [];
		if (debugMap is null || stoppedNativeSymbol is null)
			return;
		DebugMapFunction? function = debugMap.FindFunction(stoppedNativeSymbol);
		if (function is null)
			return;

		HashSet<string> hidden = new(StringComparer.Ordinal);
		Dictionary<string, DebugVariable> parameters = new(StringComparer.Ordinal);
		Dictionary<string, DebugVariable> locals = new(StringComparer.Ordinal);
		foreach (DebugMapVariable variable in function.Variables)
		{
			if (!nativeVariables.TryGetValue(variable.NativeName, out LldbNativeVariable? native))
				continue;
			if (variable.CampName.EndsWith("_length", StringComparison.Ordinal))
			{
				string baseName = variable.CampName[..^"_length".Length];
				DebugMapVariable? baseVariable = function.Variables.FirstOrDefault(item => item.CampName == baseName);
				if (baseVariable is not null && nativeVariables.TryGetValue(baseVariable.NativeName, out LldbNativeVariable? baseNative))
				{
					int reference = nextVariableReference++;
					DebugVariable structured = new(baseName, "{ elements, length }", baseVariable.Type, reference);
					variableReferences[reference] =
					[
						new DebugVariable("elements", baseNative.Value, baseNative.Type, 0),
						new DebugVariable("length", native.Value, native.Type, 0)
					];
					AddVariable(baseVariable.Kind, structured, parameters, locals);
					evaluateVariables[baseName] = structured;
					hidden.Add(baseName);
					hidden.Add(variable.CampName);
				}
				continue;
			}
			if (hidden.Contains(variable.CampName))
				continue;
			DebugVariable debugVariable = new(variable.CampName, native.Value, variable.Type ?? native.Type, 0);
			AddVariable(variable.Kind, debugVariable, parameters, locals);
			evaluateVariables[variable.CampName] = debugVariable;
		}
		variableReferences[100] = parameters.Values.ToList();
		variableReferences[200] = locals.Values.ToList();
	}

	static void AddVariable(string kind, DebugVariable variable, Dictionary<string, DebugVariable> parameters, Dictionary<string, DebugVariable> locals)
	{
		if (kind.Equals("parameter", StringComparison.OrdinalIgnoreCase))
			parameters[variable.Name] = variable;
		else
			locals[variable.Name] = variable;
	}

	static Dictionary<string, LldbNativeVariable> ParseCdbVariables(string output)
	{
		Dictionary<string, LldbNativeVariable> variables = new(StringComparer.Ordinal);
		foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string line = rawLine.Trim();
			int equals = line.IndexOf(" = ", StringComparison.Ordinal);
			if (equals <= 0)
				continue;
			string name = line[..equals].Trim();
			string value = NormalizeCdbValue(line[(equals + 3)..].Trim());
			if (name.Length > 0 && !name.Contains(' '))
				variables[name] = new LldbNativeVariable("", value);
		}
		return variables;
	}

	static string NormalizeCdbValue(string value)
	{
		return value.StartsWith("0n", StringComparison.Ordinal) ? value[2..] : value;
	}

	async Task<string> RunCdbBatch(params string[] commands)
	{
		if (string.IsNullOrEmpty(executable))
			throw new InvalidOperationException("CDB session has not been launched.");
		ProcessStartInfo info = new(cdbPath)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		info.ArgumentList.Add("-lines");
		info.ArgumentList.Add("-G");
		info.ArgumentList.Add("-g");
		info.ArgumentList.Add("-o");
		info.ArgumentList.Add("-c");
		info.ArgumentList.Add(BuildCdbCommand(commands));
		info.ArgumentList.Add(executable);
		using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start cdb.exe.");
		string stdout = await process.StandardOutput.ReadToEndAsync();
		string stderr = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		string output = stdout + stderr;
		if (process.ExitCode != 0 && !output.Contains("Breakpoint ", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("CDB command failed." + Environment.NewLine + output);
		return output;
	}

	string BuildCdbCommand(params string[] commands)
	{
		List<string> all = [];
		foreach ((string source, int line) in pendingBreakpoints)
			all.Add(line == 0 ? "bu main" : "bu `" + source + ":" + line + "`");
		all.AddRange(commands);
		all.Add("q");
		return string.Join("; ", all);
	}

	static string? FindCdbPath()
	{
		string? pathValue = Environment.GetEnvironmentVariable("PATH");
		if (pathValue is not null)
			foreach (string directory in pathValue.Split(Path.PathSeparator))
			{
				string candidate = Path.Combine(directory, "cdb.exe");
				if (File.Exists(candidate))
					return candidate;
			}
		string kits = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Debuggers");
		if (Directory.Exists(kits))
		{
			string? candidate = Directory.EnumerateFiles(kits, "cdb.exe", SearchOption.AllDirectories)
				.FirstOrDefault(path => path.Contains("\\x64\\", StringComparison.OrdinalIgnoreCase));
			if (candidate is not null)
				return candidate;
		}
		return null;
	}
}

sealed record DebugLaunchOptions(string Project, string Cwd, IReadOnlyList<string> Args, bool StopOnEntry);
sealed record DebugBreakpoint(int Line, bool Verified, string? Message = null);
sealed record DebugStackFrame(int Id, string Name, string SourcePath, int Line, int Column);
sealed record DebugScope(string Name, int Reference);
sealed record DebugVariable(string Name, string Value, string? Type, int Reference);
sealed record DebugBuildResult(string Executable, string? DebugMapPath);
sealed record LldbNativeVariable(string Type, string Value);

sealed class DebugMapDocument(IReadOnlyList<DebugMapFunction> functions)
{
	public DebugMapFunction? FindFunction(string nativeSymbol)
	{
		return functions.FirstOrDefault(function => function.NativeSymbol == nativeSymbol || function.CampFunction == nativeSymbol);
	}

	public static DebugMapDocument Load(string path)
	{
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
		List<DebugMapFunction> functions = [];
		if (!document.RootElement.TryGetProperty("entries", out JsonElement entries) || entries.ValueKind is not JsonValueKind.Array)
			return new DebugMapDocument(functions);
		foreach (JsonElement entry in entries.EnumerateArray())
		{
			string? kind = entry.TryGetProperty("kind", out JsonElement kindElement) ? kindElement.GetString() : null;
			if (kind != "function")
				continue;
			string? campFunction = entry.TryGetProperty("campFunction", out JsonElement campElement) ? campElement.GetString() : null;
			string? nativeSymbol = entry.TryGetProperty("nativeSymbol", out JsonElement nativeElement) ? nativeElement.GetString() : null;
			List<DebugMapVariable> variables = [];
			if (entry.TryGetProperty("variables", out JsonElement variablesElement) && variablesElement.ValueKind is JsonValueKind.Array)
			{
				foreach (JsonElement variable in variablesElement.EnumerateArray())
				{
					string campName = variable.TryGetProperty("campName", out JsonElement campNameElement) ? campNameElement.GetString() ?? "" : "";
					string nativeName = variable.TryGetProperty("nativeName", out JsonElement nativeNameElement) ? nativeNameElement.GetString() ?? "" : "";
					string? type = variable.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null;
					string variableKind = variable.TryGetProperty("kind", out JsonElement variableKindElement) ? variableKindElement.GetString() ?? "local" : "local";
					if (campName.Length > 0 && nativeName.Length > 0)
						variables.Add(new DebugMapVariable(campName, nativeName, type, variableKind));
				}
			}
			if (nativeSymbol is not null)
				functions.Add(new DebugMapFunction(campFunction, nativeSymbol, variables));
		}
		return new DebugMapDocument(functions);
	}
}

sealed record DebugMapFunction(string? CampFunction, string NativeSymbol, IReadOnlyList<DebugMapVariable> Variables);
sealed record DebugMapVariable(string CampName, string NativeName, string? Type, string Kind);
