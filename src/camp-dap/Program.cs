using System;
using System.Collections.Concurrent;
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
		await EmitOutputEvents(backend);
		if (backend.StopOnEntry || backend.IsStopped)
			await Event("stopped", new JsonObject { ["reason"] = backend.StopOnEntry ? "entry" : "breakpoint", ["threadId"] = 1 });
		else if (backend.HasTerminated)
			await Event("terminated", null);
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
		await EmitOutputEvents(backend);
		if (backend.IsStopped)
			await Event("stopped", new JsonObject { ["reason"] = "breakpoint", ["threadId"] = 1 });
		else if (backend.HasTerminated)
			await Event("terminated", null);
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
		await EmitOutputEvents(backend);
		if (backend.IsStopped)
			await Event("stopped", new JsonObject { ["reason"] = "step", ["threadId"] = 1 });
		else if (backend.HasTerminated)
			await Event("terminated", null);
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

	async Task EmitOutputEvents(IDebugBackend backend)
	{
		foreach (DebugOutputEvent outputEvent in backend.DrainOutputEvents())
		{
			await Event("output", new JsonObject
			{
				["category"] = outputEvent.Category,
				["output"] = outputEvent.Output
			});
		}
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
	bool HasTerminated { get; }
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
	IReadOnlyList<DebugOutputEvent> DrainOutputEvents();
	Task Disconnect();
}

sealed class FakeDebugBackend : IDebugBackend
{
	string source = "fake.camp";
	bool terminateOnConfigurationDone;
	public bool StopOnEntry { get; private set; }
	public bool IsStopped { get; private set; }
	public bool HasTerminated { get; private set; }

	public Task Launch(DebugLaunchOptions options)
	{
		if (options.Project.Contains("fail", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Fake backend launch failure.");
		StopOnEntry = options.StopOnEntry;
		terminateOnConfigurationDone = options.Project.Contains("terminate", StringComparison.OrdinalIgnoreCase);
		if (!string.IsNullOrWhiteSpace(options.Project))
			source = Path.GetFullPath(options.Project);
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<DebugBreakpoint>> SetBreakpoints(string source, IReadOnlyList<int> lines)
	{
		IReadOnlyList<DebugBreakpoint> breakpoints = lines.Select(line => new DebugBreakpoint(line, true)).ToList();
		return Task.FromResult(breakpoints);
	}

	public Task ConfigurationDone()
	{
		IsStopped = StopOnEntry;
		HasTerminated = !StopOnEntry && terminateOnConfigurationDone;
		return Task.CompletedTask;
	}
	public Task Continue(int threadId)
	{
		IsStopped = false;
		return Task.CompletedTask;
	}
	public Task Pause(int threadId) => Task.CompletedTask;
	public Task Step(string command, int threadId)
	{
		IsStopped = true;
		return Task.CompletedTask;
	}
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
			100 => [new DebugVariable("args", "string[] length=0", "string[]", 300)],
			200 =>
			[
				new DebugVariable("answer", "42", "int", 0),
				new DebugVariable("handler", "delegate void(int) { call, context }", "delegate void(int)", 301),
				new DebugVariable("state", "iterator state 0x0000000000001234", "countToIter*", 0),
				new DebugVariable("lambdaContext", "lambda context 0x0000000000005678", "main_lambdaContext0*", 0)
			],
			300 =>
			[
				new DebugVariable("elements", "null", "string*", 0),
				new DebugVariable("length", "0", "nuint", 0)
			],
			301 =>
			[
				new DebugVariable("call", "target", "fn void(void*, int)", 0),
				new DebugVariable("context", "null", "void*", 0)
			],
			_ => []
		};
	}

	public DebugVariable Evaluate(string expression)
	{
		return expression switch
			{
				"answer" => new DebugVariable("answer", "42", "int", 0),
				"args" => new DebugVariable("args", "string[] length=0", "string[]", 300),
				"handler" => new DebugVariable("handler", "delegate void(int) { call, context }", "delegate void(int)", 301),
				"state" => new DebugVariable("state", "iterator state 0x0000000000001234", "countToIter*", 0),
				"lambdaContext" => new DebugVariable("lambdaContext", "lambda context 0x0000000000005678", "main_lambdaContext0*", 0),
				_ => new DebugVariable(expression, "Unsupported expression", null, 0)
			};
	}

	public IReadOnlyList<DebugOutputEvent> DrainOutputEvents() => [];
}

sealed class LldbDebugBackend : IDebugBackend
{
	readonly List<(string Source, int Line)> pendingBreakpoints = [];
	readonly List<DebugStackFrame> lastFrames = [];
	readonly Dictionary<int, IReadOnlyList<DebugVariable>> variableReferences = new();
	readonly Dictionary<string, DebugVariable> evaluateVariables = new(StringComparer.Ordinal);
	string executable = "";
	string buildDirectory = "";
	string stdoutPath = "";
	string stderrPath = "";
	long stdoutOffset;
	long stderrOffset;
	Process? lldbProcess;
	readonly ConcurrentQueue<string> lldbOutput = new();
	DebugMapDocument? debugMap;
	string? stoppedNativeSymbol;
	int nextVariableReference = 1000;

	public bool StopOnEntry { get; private set; }
	public bool IsStopped { get; private set; }
	public bool HasTerminated { get; private set; }

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
		stdoutPath = Path.Combine(buildDirectory, "stdout.txt");
		stderrPath = Path.Combine(buildDirectory, "stderr.txt");
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
		await StartLldbSession();
		foreach ((string source, int line) in pendingBreakpoints)
		{
			await RunLldbCommand(line == 0
				? "breakpoint set --name main"
				: $"breakpoint set --file {QuoteLldbArgument(source)} --line {line} --move-to-nearest-code false");
		}
		string output = await RunLldbExecutionCommand("run");
		await RefreshStoppedState(output);
	}

	public async Task Continue(int threadId)
	{
		string output = await RunLldbExecutionCommand("continue");
		await RefreshStoppedState(output);
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
		string output = await RunLldbExecutionCommand(lldbCommand);
		await RefreshStoppedState(output);
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

	public IReadOnlyList<DebugOutputEvent> DrainOutputEvents()
	{
		List<DebugOutputEvent> events = [];
		DrainOutputFile(stdoutPath, "stdout", ref stdoutOffset, events);
		DrainOutputFile(stderrPath, "stderr", ref stderrOffset, events);
		return events;
	}

	public async Task Disconnect()
	{
		if (lldbProcess is null)
			return;
		try
		{
			if (!lldbProcess.HasExited)
			{
				await lldbProcess.StandardInput.WriteLineAsync("quit");
				Task exited = lldbProcess.WaitForExitAsync();
				if (await Task.WhenAny(exited, Task.Delay(1000)) != exited && !lldbProcess.HasExited)
					lldbProcess.Kill(entireProcessTree: true);
			}
		}
		finally
		{
			lldbProcess.Dispose();
			lldbProcess = null;
		}
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
				frames.Add(new DebugStackFrame(frames.Count + 1, debugMap?.GetDisplayName(name) ?? name, path, sourceLine, column));
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

		DebugVariableMapper.Update(function, nativeVariables, variableReferences, evaluateVariables, ref nextVariableReference, parametersReference: 100, localsReference: 200);
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

	async Task StartLldbSession()
	{
		if (lldbProcess is not null)
			return;
		if (string.IsNullOrEmpty(executable))
			throw new InvalidOperationException("LLDB session has not been launched.");
		ProcessStartInfo info = new("lldb")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		info.ArgumentList.Add("--no-lldbinit");
		info.ArgumentList.Add(executable);
		lldbProcess = Process.Start(info) ?? throw new InvalidOperationException("Could not start lldb.");
		lldbProcess.OutputDataReceived += (_, e) =>
		{
			if (e.Data is not null)
				lldbOutput.Enqueue(e.Data + Environment.NewLine);
		};
		lldbProcess.ErrorDataReceived += (_, e) =>
		{
			if (e.Data is not null)
				lldbOutput.Enqueue(e.Data + Environment.NewLine);
		};
		lldbProcess.BeginOutputReadLine();
		lldbProcess.BeginErrorReadLine();
		await ReadLldbUntilQuiet(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150));
		if (!string.IsNullOrEmpty(stdoutPath))
			await RunLldbCommand($"settings set target.output-path {QuoteLldbArgument(stdoutPath)}");
		if (!string.IsNullOrEmpty(stderrPath))
			await RunLldbCommand($"settings set target.error-path {QuoteLldbArgument(stderrPath)}");
	}

	async Task RefreshStoppedState(string executionOutput)
	{
		IsStopped = executionOutput.Contains(" stopped", StringComparison.OrdinalIgnoreCase);
		HasTerminated = executionOutput.Contains(" exited", StringComparison.OrdinalIgnoreCase)
			|| executionOutput.Contains("exited with status", StringComparison.OrdinalIgnoreCase);
		if (!IsStopped)
		{
			if (HasTerminated)
				lastFrames.Clear();
			return;
		}

		string stateOutput = executionOutput
			+ await RunLldbCommand("thread backtrace")
			+ await RunLldbCommand("frame variable --show-types");
		UpdateFramesFromOutput(stateOutput);
		UpdateVariablesFromOutput(stateOutput);
	}

	async Task<string> RunLldbExecutionCommand(string command)
	{
		await StartLldbSession();
		DrainQueuedLldbOutput();
		await lldbProcess!.StandardInput.WriteLineAsync(command);
		await lldbProcess.StandardInput.FlushAsync();
		return await ReadLldbUntilExecutionStops(TimeSpan.FromSeconds(20));
	}

	async Task<string> RunLldbCommand(string command)
	{
		await StartLldbSession();
		DrainQueuedLldbOutput();
		await lldbProcess!.StandardInput.WriteLineAsync(command);
		await lldbProcess.StandardInput.FlushAsync();
		return await ReadLldbUntilQuiet(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(150));
	}

	async Task<string> ReadLldbUntilExecutionStops(TimeSpan timeout)
	{
		StringBuilder output = new();
		DateTime deadline = DateTime.UtcNow + timeout;
		DateTime lastRead = DateTime.UtcNow;
		bool sawStopOrExit = false;
		while (DateTime.UtcNow < deadline)
		{
			string chunk = await ReadLldbChunk(TimeSpan.FromMilliseconds(100));
			if (chunk.Length > 0)
			{
				output.Append(chunk);
				lastRead = DateTime.UtcNow;
				string text = output.ToString();
				sawStopOrExit = text.Contains(" stopped", StringComparison.OrdinalIgnoreCase)
					|| text.Contains(" exited", StringComparison.OrdinalIgnoreCase)
					|| text.Contains("exited with status", StringComparison.OrdinalIgnoreCase);
				continue;
			}
			if (sawStopOrExit && DateTime.UtcNow - lastRead >= TimeSpan.FromMilliseconds(250))
				break;
			if (lldbProcess?.HasExited == true)
				break;
		}
		return output.ToString();
	}

	async Task<string> ReadLldbUntilQuiet(TimeSpan timeout, TimeSpan quietPeriod)
	{
		StringBuilder output = new();
		DateTime deadline = DateTime.UtcNow + timeout;
		DateTime lastRead = DateTime.UtcNow;
		bool sawOutput = false;
		while (DateTime.UtcNow < deadline)
		{
			string chunk = await ReadLldbChunk(TimeSpan.FromMilliseconds(100));
			if (chunk.Length > 0)
			{
				output.Append(chunk);
				lastRead = DateTime.UtcNow;
				sawOutput = true;
				continue;
			}
			if (sawOutput && DateTime.UtcNow - lastRead >= quietPeriod)
				break;
			if (lldbProcess?.HasExited == true)
				break;
		}
		return output.ToString();
	}

	async Task<string> ReadLldbChunk(TimeSpan timeout)
	{
		if (lldbOutput.TryDequeue(out string? chunk))
			return chunk;
		await Task.Delay(timeout);
		StringBuilder builder = new();
		while (lldbOutput.TryDequeue(out chunk))
			builder.Append(chunk);
		return builder.ToString();
	}

	string DrainQueuedLldbOutput()
	{
		StringBuilder builder = new();
		while (lldbOutput.TryDequeue(out string? chunk))
		{
			builder.Append(chunk);
		}
		return builder.ToString();
	}

	internal static async Task<DebugBuildResult> BuildExecutable(string project, string cwd, string outDirectory)
	{
		string campc = Path.Combine(FindRepositoryRoot(), "bin", "campc");
		if (!File.Exists(campc))
			campc = Path.Combine(FindRepositoryRoot(), "bin", "campc.dll");
		DateTime buildStart = DateTime.UtcNow;
		List<string> args = ["build", project, "--profile", "DEBUG", "--artifact", "exec", "--debug-info"];
		if (!ProjectDeclaresOutDir(project, cwd))
			args.AddRange(["--out-dir", outDirectory]);
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
		string? executable = FindGeneratedExecutable(outDirectory, project, cwd, buildStart);
		if (executable is null)
			throw new InvalidOperationException("Camp build completed but no executable artifact was found." + Environment.NewLine + stdout);
		string? debugMap = FindGeneratedDebugMap(outDirectory, project, cwd, buildStart);
		return new DebugBuildResult(executable, debugMap);
	}

	static bool ProjectDeclaresOutDir(string project, string cwd)
	{
		string path = Path.GetFullPath(project, cwd);
		if (!File.Exists(path) || !path.EndsWith(".campbuild", StringComparison.OrdinalIgnoreCase))
			return false;
		foreach (string line in File.ReadLines(path))
			if (line.Split('#', 2)[0].Contains("--out-dir", StringComparison.Ordinal))
				return true;
		return false;
	}

	static string? FindGeneratedExecutable(string outDirectory, string project, string cwd, DateTime buildStart)
	{
		return CandidateBuildSearchRoots(outDirectory, project, cwd)
			.SelectMany(root => Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) : [])
			.Where(IsExecutable)
			.Where(path => File.GetLastWriteTimeUtc(path) >= buildStart.AddSeconds(-5))
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
	}

	static string? FindGeneratedDebugMap(string outDirectory, string project, string cwd, DateTime buildStart)
	{
		return CandidateBuildSearchRoots(outDirectory, project, cwd)
			.SelectMany(root => Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.campdebug.json", SearchOption.AllDirectories) : [])
			.Where(path => File.GetLastWriteTimeUtc(path) >= buildStart.AddSeconds(-5))
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
	}

	static IEnumerable<string> CandidateBuildSearchRoots(string outDirectory, string project, string cwd)
	{
		yield return outDirectory;
		string projectPath = Path.GetFullPath(project, cwd);
		yield return File.Exists(projectPath) ? Path.GetDirectoryName(projectPath) ?? cwd : cwd;
	}

	internal static bool IsExecutable(string path)
	{
		if (OperatingSystem.IsWindows())
			return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
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

	static void DrainOutputFile(string path, string category, ref long offset, List<DebugOutputEvent> events)
	{
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
			return;
		using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		if (stream.Length <= offset)
			return;
		stream.Position = offset;
		byte[] bytes = new byte[stream.Length - offset];
		int read = stream.Read(bytes, 0, bytes.Length);
		offset = stream.Position;
		if (read > 0)
			events.Add(new DebugOutputEvent(category, Encoding.UTF8.GetString(bytes, 0, read)));
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
	public bool HasTerminated { get; private set; }

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
		HasTerminated = !IsStopped;
		UpdateFramesFromOutput(output);
		UpdateVariablesFromOutput(output);
	}

	public async Task Continue(int threadId)
	{
		await Task.CompletedTask;
		IsStopped = false;
		HasTerminated = false;
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
		HasTerminated = !IsStopped;
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

	public IReadOnlyList<DebugOutputEvent> DrainOutputEvents() => [];

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
				frames.Add(new DebugStackFrame(frames.Count + 1, debugMap?.GetDisplayName(name) ?? name, path, sourceLine, 1));
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

		DebugVariableMapper.Update(function, nativeVariables, variableReferences, evaluateVariables, ref nextVariableReference, parametersReference: 100, localsReference: 200);
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
	readonly List<DebugOutputEvent> pendingOutputEvents = [];
	readonly ConcurrentQueue<string> cdbOutput = new();
	string executable = "";
	string cdbPath = "";
	string buildDirectory = "";
	Process? cdbProcess;
	DebugMapDocument? debugMap;
	string? stoppedNativeSymbol;
	int nextVariableReference = 3000;

	public bool StopOnEntry { get; private set; }
	public bool IsStopped { get; private set; }
	public bool HasTerminated { get; private set; }

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
		await StartCdbSession();
		foreach ((string source, int line) in pendingBreakpoints)
			await RunCdbCommand(BuildCdbBreakpointCommand(source, line));
		string output = await RunCdbExecutionCommand("g");
		await RefreshStoppedState(output);
	}

	public async Task Continue(int threadId)
	{
		string output = await RunCdbExecutionCommand("g");
		await RefreshStoppedState(output);
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
		string output = await RunCdbExecutionCommand(cdbCommand);
		await RefreshStoppedState(output);
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

	public IReadOnlyList<DebugOutputEvent> DrainOutputEvents()
	{
		if (pendingOutputEvents.Count == 0)
			return [];
		List<DebugOutputEvent> events = [.. pendingOutputEvents];
		pendingOutputEvents.Clear();
		return events;
	}

	public async Task Disconnect()
	{
		if (cdbProcess is null)
			return;
		try
		{
			if (!cdbProcess.HasExited)
			{
				await cdbProcess.StandardInput.WriteLineAsync("q");
				Task exited = cdbProcess.WaitForExitAsync();
				if (await Task.WhenAny(exited, Task.Delay(1000)) != exited && !cdbProcess.HasExited)
					cdbProcess.Kill(entireProcessTree: true);
			}
		}
		finally
		{
			cdbProcess.Dispose();
			cdbProcess = null;
		}
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
			frames.Add(new DebugStackFrame(frames.Count + 1, debugMap?.GetDisplayName(name) ?? name, path, sourceLine, 1));
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

		DebugVariableMapper.Update(function, nativeVariables, variableReferences, evaluateVariables, ref nextVariableReference, parametersReference: 100, localsReference: 200);
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

	async Task StartCdbSession()
	{
		if (cdbProcess is not null)
			return;
		if (string.IsNullOrEmpty(executable))
			throw new InvalidOperationException("CDB session has not been launched.");
		ProcessStartInfo info = new(cdbPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		info.ArgumentList.Add("-lines");
		info.ArgumentList.Add("-y");
		info.ArgumentList.Add(Path.GetDirectoryName(executable) ?? ".");
		info.ArgumentList.Add("-o");
		info.ArgumentList.Add(executable);
		cdbProcess = Process.Start(info) ?? throw new InvalidOperationException("Could not start cdb.exe.");
		_ = Task.Run(() => ReadCdbStream(cdbProcess.StandardOutput.BaseStream));
		_ = Task.Run(() => ReadCdbStream(cdbProcess.StandardError.BaseStream));
		await ReadCdbUntilQuiet(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));
	}

	async Task RefreshStoppedState(string executionOutput)
	{
		IsStopped = IsCdbStoppedOutput(executionOutput);
		HasTerminated = IsCdbTerminatedOutput(executionOutput);
		QueueDebuggeeOutput(executionOutput);
		if (!IsStopped)
		{
			if (HasTerminated)
				lastFrames.Clear();
			return;
		}

		string stateOutput = executionOutput
			+ await RunCdbCommand("k")
			+ await RunCdbCommand("dv");
		UpdateFramesFromOutput(stateOutput);
		UpdateVariablesFromOutput(stateOutput);
	}

	async Task<string> RunCdbExecutionCommand(string command)
	{
		await StartCdbSession();
		DrainQueuedCdbOutput();
		await cdbProcess!.StandardInput.WriteLineAsync(command);
		await cdbProcess.StandardInput.FlushAsync();
		string output = await ReadCdbUntilExecutionStops(TimeSpan.FromSeconds(30));
		for (int i = 0; i < 4 && IsCdbLoaderBreakpoint(output); i++)
		{
			DrainQueuedCdbOutput();
			await cdbProcess.StandardInput.WriteLineAsync("g");
			await cdbProcess.StandardInput.FlushAsync();
			output = await ReadCdbUntilExecutionStops(TimeSpan.FromSeconds(30));
		}
		return output;
	}

	async Task<string> RunCdbCommand(string command)
	{
		await StartCdbSession();
		DrainQueuedCdbOutput();
		await cdbProcess!.StandardInput.WriteLineAsync(command);
		await cdbProcess.StandardInput.FlushAsync();
		return await ReadCdbUntilQuiet(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));
	}

	async Task<string> ReadCdbUntilExecutionStops(TimeSpan timeout)
	{
		StringBuilder output = new();
		DateTime deadline = DateTime.UtcNow + timeout;
		DateTime lastRead = DateTime.UtcNow;
		bool sawStopOrExit = false;
		while (DateTime.UtcNow < deadline)
		{
			string chunk = await ReadCdbChunk(TimeSpan.FromMilliseconds(100));
			if (chunk.Length > 0)
			{
				output.Append(chunk);
				lastRead = DateTime.UtcNow;
				string text = output.ToString();
				sawStopOrExit = IsCdbStoppedOutput(text) || IsCdbTerminatedOutput(text);
				continue;
			}
			if (sawStopOrExit && DateTime.UtcNow - lastRead >= TimeSpan.FromMilliseconds(300))
				break;
			if (cdbProcess?.HasExited == true)
				break;
		}
		return output.ToString();
	}

	async Task<string> ReadCdbUntilQuiet(TimeSpan timeout, TimeSpan quietPeriod)
	{
		StringBuilder output = new();
		DateTime deadline = DateTime.UtcNow + timeout;
		DateTime lastRead = DateTime.UtcNow;
		bool sawOutput = false;
		while (DateTime.UtcNow < deadline)
		{
			string chunk = await ReadCdbChunk(TimeSpan.FromMilliseconds(100));
			if (chunk.Length > 0)
			{
				output.Append(chunk);
				lastRead = DateTime.UtcNow;
				sawOutput = true;
				continue;
			}
			if (sawOutput && DateTime.UtcNow - lastRead >= quietPeriod)
				break;
			if (cdbProcess?.HasExited == true)
				break;
		}
		return output.ToString();
	}

	async Task<string> ReadCdbChunk(TimeSpan timeout)
	{
		if (cdbOutput.TryDequeue(out string? chunk))
			return chunk;
		await Task.Delay(timeout);
		StringBuilder builder = new();
		while (cdbOutput.TryDequeue(out chunk))
			builder.Append(chunk);
		return builder.ToString();
	}

	string DrainQueuedCdbOutput()
	{
		StringBuilder builder = new();
		while (cdbOutput.TryDequeue(out string? chunk))
			builder.Append(chunk);
		return builder.ToString();
	}

	async Task ReadCdbStream(Stream stream)
	{
		byte[] buffer = new byte[4096];
		try
		{
			while (true)
			{
				int count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
				if (count <= 0)
					break;
				cdbOutput.Enqueue(Encoding.UTF8.GetString(buffer, 0, count));
			}
		}
		catch
		{
		}
	}

	string BuildCdbBreakpointCommand(string source, int line)
	{
		string module = Path.GetFileNameWithoutExtension(executable);
		if (line == 0)
			return "bu " + module + "!" + (debugMap?.FindFunction("main")?.NativeSymbol ?? "main");
		return "bu `" + source + ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture) + "`";
	}

	static bool IsCdbStoppedOutput(string output)
	{
		return output.Contains("Breakpoint ", StringComparison.OrdinalIgnoreCase)
			|| output.Contains("breakpoint", StringComparison.OrdinalIgnoreCase)
			|| output.Contains("Access violation", StringComparison.OrdinalIgnoreCase);
	}

	static bool IsCdbLoaderBreakpoint(string output)
	{
		return output.Contains("WOW64 breakpoint", StringComparison.OrdinalIgnoreCase)
			|| output.Contains("LdrInitShimEngineDynamic", StringComparison.OrdinalIgnoreCase)
			|| output.Contains("LdrpDoDebuggerBreak", StringComparison.OrdinalIgnoreCase)
			|| (output.Contains("first chance", StringComparison.OrdinalIgnoreCase)
				&& output.Contains("int", StringComparison.OrdinalIgnoreCase)
				&& output.Contains("ntdll", StringComparison.OrdinalIgnoreCase));
	}

	static bool IsCdbTerminatedOutput(string output)
	{
		return output.Contains("quit:", StringComparison.OrdinalIgnoreCase)
			|| output.Contains("exited with code", StringComparison.OrdinalIgnoreCase)
			|| output.Contains("Debuggee is not connected", StringComparison.OrdinalIgnoreCase);
	}

	void QueueDebuggeeOutput(string output)
	{
		StringBuilder builder = new();
		foreach (string rawLine in output.Split('\n'))
		{
			string line = rawLine.TrimEnd('\r');
			if (line.Length == 0)
				continue;
			if (line.StartsWith("0:", StringComparison.Ordinal)
				|| line.StartsWith("Microsoft ", StringComparison.Ordinal)
				|| line.StartsWith("CommandLine:", StringComparison.Ordinal)
				|| line.StartsWith("Symbol search path", StringComparison.Ordinal)
				|| line.StartsWith("Executable search path", StringComparison.Ordinal)
				|| line.StartsWith("ModLoad:", StringComparison.Ordinal)
				|| line.StartsWith("Breakpoint ", StringComparison.OrdinalIgnoreCase)
				|| line.Contains("first chance", StringComparison.OrdinalIgnoreCase)
				|| line.Contains("First chance exceptions", StringComparison.OrdinalIgnoreCase)
				|| line.Contains("WOW64 breakpoint", StringComparison.OrdinalIgnoreCase)
				|| line.Contains("LdrInitShimEngineDynamic", StringComparison.OrdinalIgnoreCase)
				|| line.Contains("LdrpDoDebuggerBreak", StringComparison.OrdinalIgnoreCase)
				|| line.Contains("Debug session time:", StringComparison.OrdinalIgnoreCase)
				|| line.Contains("cdb: Reading initial command", StringComparison.OrdinalIgnoreCase)
				|| line.Contains("Windows ", StringComparison.OrdinalIgnoreCase)
				|| line.Contains("Copyright ", StringComparison.OrdinalIgnoreCase)
				|| line.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (line.Contains("!main", StringComparison.Ordinal)
				|| line.Contains("!campmain", StringComparison.Ordinal)
				|| line.Contains("!thing", StringComparison.Ordinal)
				|| line.Contains("!Ldr", StringComparison.Ordinal)
				|| line.Contains(" int     3", StringComparison.OrdinalIgnoreCase)
				|| line.Contains(" [", StringComparison.Ordinal))
			{
				continue;
			}
			builder.AppendLine(line);
		}
		if (builder.Length > 0)
			pendingOutputEvents.Add(new DebugOutputEvent("stdout", builder.ToString()));
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
sealed record DebugOutputEvent(string Category, string Output);
sealed record DebugBuildResult(string Executable, string? DebugMapPath);
sealed record LldbNativeVariable(string Type, string Value);

static class DebugVariableMapper
{
	public static void Update(
		DebugMapFunction function,
		Dictionary<string, LldbNativeVariable> nativeVariables,
		Dictionary<int, IReadOnlyList<DebugVariable>> variableReferences,
		Dictionary<string, DebugVariable> evaluateVariables,
		ref int nextVariableReference,
		int parametersReference,
		int localsReference)
	{
		HashSet<string> hidden = new(StringComparer.Ordinal);
		Dictionary<string, DebugVariable> parameters = new(StringComparer.Ordinal);
		Dictionary<string, DebugVariable> locals = new(StringComparer.Ordinal);
		foreach (DebugMapVariable variable in function.Variables)
		{
			if (!nativeVariables.TryGetValue(variable.NativeName, out LldbNativeVariable? native))
				continue;
			if (IsSyntheticVariable(variable.CampName))
				continue;
			if (TryAddSpan(variable, function, nativeVariables, variableReferences, evaluateVariables, hidden, parameters, locals, ref nextVariableReference))
				continue;
			if (TryAddCallable(variable, function, nativeVariables, variableReferences, evaluateVariables, hidden, parameters, locals, ref nextVariableReference))
				continue;
			if (hidden.Contains(variable.CampName))
				continue;
			DebugVariable debugVariable = FormatScalar(variable.CampName, variable.Type ?? native.Type, native.Value);
			AddVariable(variable.Kind, debugVariable, parameters, locals);
			evaluateVariables[variable.CampName] = debugVariable;
		}
		variableReferences[parametersReference] = parameters.Values.ToList();
		variableReferences[localsReference] = locals.Values.ToList();
	}

	static bool TryAddSpan(
		DebugMapVariable variable,
		DebugMapFunction function,
		Dictionary<string, LldbNativeVariable> nativeVariables,
		Dictionary<int, IReadOnlyList<DebugVariable>> variableReferences,
		Dictionary<string, DebugVariable> evaluateVariables,
		HashSet<string> hidden,
		Dictionary<string, DebugVariable> parameters,
		Dictionary<string, DebugVariable> locals,
		ref int nextVariableReference)
	{
		if (!variable.CampName.EndsWith("_length", StringComparison.Ordinal))
			return false;
		string baseName = variable.CampName[..^"_length".Length];
		DebugMapVariable? baseVariable = function.Variables.FirstOrDefault(item => item.CampName == baseName);
		if (baseVariable is null
			|| !nativeVariables.TryGetValue(baseVariable.NativeName, out LldbNativeVariable? elements)
			|| !nativeVariables.TryGetValue(variable.NativeName, out LldbNativeVariable? length))
			return true;

		int reference = nextVariableReference++;
		string type = FormatSpanType(baseVariable.Type ?? elements.Type);
		DebugVariable structured = new(baseName, type + " length=" + length.Value, type, reference);
		variableReferences[reference] =
		[
			new DebugVariable("elements", FormatPointerValue(elements.Value), elements.Type, 0),
			new DebugVariable("length", length.Value, variable.Type ?? length.Type, 0)
		];
		AddVariable(baseVariable.Kind, structured, parameters, locals);
		evaluateVariables[baseName] = structured;
		hidden.Add(baseName);
		hidden.Add(variable.CampName);
		return true;
	}

	static bool TryAddCallable(
		DebugMapVariable variable,
		DebugMapFunction function,
		Dictionary<string, LldbNativeVariable> nativeVariables,
		Dictionary<int, IReadOnlyList<DebugVariable>> variableReferences,
		Dictionary<string, DebugVariable> evaluateVariables,
		HashSet<string> hidden,
		Dictionary<string, DebugVariable> parameters,
		Dictionary<string, DebugVariable> locals,
		ref int nextVariableReference)
	{
		if (!variable.CampName.EndsWith("_context", StringComparison.Ordinal))
			return false;
		string baseName = variable.CampName[..^"_context".Length];
		DebugMapVariable? callVariable = function.Variables.FirstOrDefault(item => item.CampName == baseName);
		if (callVariable is null
			|| !nativeVariables.TryGetValue(callVariable.NativeName, out LldbNativeVariable? call)
			|| !nativeVariables.TryGetValue(variable.NativeName, out LldbNativeVariable? context))
			return true;

		int reference = nextVariableReference++;
		string type = FormatCallableType(callVariable.Type ?? call.Type);
		DebugVariable structured = new(baseName, type + " { call, context }", type, reference);
		variableReferences[reference] =
		[
			new DebugVariable("call", call.Value, callVariable.Type ?? call.Type, 0),
			new DebugVariable("context", FormatPointerValue(context.Value), variable.Type ?? context.Type, 0)
		];
		AddVariable(callVariable.Kind, structured, parameters, locals);
		evaluateVariables[baseName] = structured;
		hidden.Add(baseName);
		hidden.Add(variable.CampName);
		return true;
	}

	static DebugVariable FormatScalar(string name, string? type, string value)
	{
		string displayType = type ?? "";
		if (IsLambdaContext(name, displayType))
			return new DebugVariable(name, "lambda context " + FormatPointerValue(value), displayType, 0);
		if (IsIteratorState(displayType))
			return new DebugVariable(name, "iterator state " + FormatPointerValue(value), displayType, 0);
		if (IsStringType(displayType))
			return new DebugVariable(name, "string " + FormatPointerValue(value), displayType, 0);
		if (displayType.StartsWith("fn ", StringComparison.Ordinal) || displayType.StartsWith("iter ", StringComparison.Ordinal))
			return new DebugVariable(name, "callable " + value, displayType, 0);
		if (displayType.EndsWith('*'))
			return new DebugVariable(name, FormatPointerValue(value), displayType, 0);
		return new DebugVariable(name, value, type, 0);
	}

	static string FormatSpanType(string type)
	{
		return type.EndsWith('*') ? type[..^1] + "[]" : type;
	}

	static string FormatCallableType(string type)
	{
		return type.StartsWith("fn ", StringComparison.Ordinal) ? "delegate " + type[3..] : type;
	}

	static string FormatPointerValue(string value)
	{
		return value is "0x0" or "0x00000000" or "0x0000000000000000" or "0" ? "null" : value;
	}

	static bool IsStringType(string type)
	{
		return type is "string" or "astring" or "wstring";
	}

	static bool IsIteratorState(string type)
	{
		return type.EndsWith("Iter*", StringComparison.Ordinal) || type.Contains("Iter*", StringComparison.Ordinal);
	}

	static bool IsLambdaContext(string name, string type)
	{
		return name.Contains("lambdaContext", StringComparison.OrdinalIgnoreCase)
			|| type.Contains("lambdaContext", StringComparison.OrdinalIgnoreCase);
	}

	static bool IsSyntheticVariable(string name)
	{
		return name.StartsWith('#');
	}

	static void AddVariable(string kind, DebugVariable variable, Dictionary<string, DebugVariable> parameters, Dictionary<string, DebugVariable> locals)
	{
		if (kind.Equals("parameter", StringComparison.OrdinalIgnoreCase))
			parameters[variable.Name] = variable;
		else
			locals[variable.Name] = variable;
	}
}

sealed class DebugMapDocument(IReadOnlyList<DebugMapFunction> functions)
{
	public DebugMapFunction? FindFunction(string nativeSymbol)
	{
		return functions.FirstOrDefault(function => function.NativeSymbol == nativeSymbol || function.CampFunction == nativeSymbol);
	}

	public string GetDisplayName(string nativeSymbol)
	{
		DebugMapFunction? function = FindFunction(nativeSymbol);
		return function?.DisplayName ?? nativeSymbol;
	}

	public DebugMapFunction? FindFunctionForSourceLine(string sourcePath, int line)
	{
		string fullPath = Path.GetFullPath(sourcePath);
		return functions
			.Where(function => function.SourcePath is not null
				&& Path.GetFullPath(function.SourcePath).Equals(fullPath, StringComparison.OrdinalIgnoreCase)
				&& function.SourceStartLine <= line)
			.OrderByDescending(function => function.SourceStartLine)
			.FirstOrDefault();
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
			string? sourcePath = null;
			int sourceStartLine = 0;
			if (entry.TryGetProperty("source", out JsonElement sourceElement) && sourceElement.ValueKind is JsonValueKind.Object)
			{
				sourcePath = sourceElement.TryGetProperty("file", out JsonElement fileElement) ? fileElement.GetString() : null;
				sourceStartLine = sourceElement.TryGetProperty("startLine", out JsonElement startLineElement) ? startLineElement.GetInt32() : 0;
			}
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
				functions.Add(new DebugMapFunction(campFunction, nativeSymbol, sourcePath, sourceStartLine, variables));
		}
		return new DebugMapDocument(functions);
	}

}

sealed record DebugMapFunction(string? CampFunction, string NativeSymbol, string? SourcePath, int SourceStartLine, IReadOnlyList<DebugMapVariable> Variables)
{
	public string DisplayName
	{
		get
		{
			if (NativeSymbol.Contains("_lambda", StringComparison.Ordinal))
			{
				string owner = NativeSymbol[..NativeSymbol.IndexOf("_lambda", StringComparison.Ordinal)];
				return owner.Length == 0 ? "lambda" : "lambda in " + owner;
			}
			if (NativeSymbol.EndsWith("Iter_next", StringComparison.Ordinal) || CampFunction == "next")
				return "iterator next";
			if (CampFunction == "op_iter")
				return "iterator call";
			if (CampFunction == "op_delete")
				return "iterator delete";
			if (CampFunction == "destroy")
				return "destroy";
			return CampFunction ?? NativeSymbol;
		}
	}
}

sealed record DebugMapVariable(string CampName, string NativeName, string? Type, string Kind);
