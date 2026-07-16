using System;
using System.Collections.Generic;
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
		DapSession session = new(Console.OpenStandardInput(), Console.OpenStandardOutput(), new FakeDebugBackend());
		await session.Run();
		return 0;
	}
}

sealed class DapSession(Stream input, Stream output, IDebugBackend backend)
{
	readonly DapProtocol protocol = new(input, output);
	readonly IDebugBackend backend = backend;
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
		if (backendName is not "fake")
			throw new InvalidOperationException($"Debug backend '{backendName}' is not available in this build yet.");
		string project = arguments["project"]?.GetValue<string>() ?? "";
		string cwd = arguments["cwd"]?.GetValue<string>() ?? Directory.GetCurrentDirectory();
		IReadOnlyList<string> args = arguments["args"] is JsonArray array
			? array.Select(item => item?.GetValue<string>() ?? "").ToList()
			: [];
		await backend.Launch(new DebugLaunchOptions(project, cwd, args, arguments["stopOnEntry"]?.GetValue<bool>() == true));
		await Event("initialized", null);
		return null;
	}

	async Task<JsonNode?> SetBreakpoints(JsonObject arguments)
	{
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
				["line"] = bp.Line
			}).ToArray<JsonNode?>())
		};
	}

	async Task<JsonNode?> ConfigurationDone()
	{
		await backend.ConfigurationDone();
		if (backend.StopOnEntry)
			await Event("stopped", new JsonObject { ["reason"] = "entry", ["threadId"] = 1 });
		return null;
	}

	JsonNode StackTrace()
	{
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
		await backend.Continue(arguments["threadId"]?.GetValue<int>() ?? 1);
		await Event("continued", new JsonObject { ["threadId"] = 1, ["allThreadsContinued"] = true });
		return new JsonObject { ["allThreadsContinued"] = true };
	}

	async Task<JsonNode?> Pause(JsonObject arguments)
	{
		await backend.Pause(arguments["threadId"]?.GetValue<int>() ?? 1);
		await Event("stopped", new JsonObject { ["reason"] = "pause", ["threadId"] = 1 });
		return null;
	}

	async Task<JsonNode?> Step(string command, JsonObject arguments)
	{
		await backend.Step(command, arguments["threadId"]?.GetValue<int>() ?? 1);
		await Event("stopped", new JsonObject { ["reason"] = "step", ["threadId"] = 1 });
		return null;
	}

	async Task<JsonNode?> Disconnect()
	{
		await backend.Disconnect();
		running = false;
		return null;
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
			100 => [new DebugVariable("argc", "0", "int", 0)],
			200 => [new DebugVariable("answer", "42", "int", 0)],
			_ => []
		};
	}

	public DebugVariable Evaluate(string expression)
	{
		return expression switch
		{
			"answer" => new DebugVariable("answer", "42", "int", 0),
			"argc" => new DebugVariable("argc", "0", "int", 0),
			_ => new DebugVariable(expression, "<unavailable>", null, 0)
		};
	}
}

sealed record DebugLaunchOptions(string Project, string Cwd, IReadOnlyList<string> Args, bool StopOnEntry);
sealed record DebugBreakpoint(int Line, bool Verified);
sealed record DebugStackFrame(int Id, string Name, string SourcePath, int Line, int Column);
sealed record DebugScope(string Name, int Reference);
sealed record DebugVariable(string Name, string Value, string? Type, int Reference);
