using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Camp.Compiler.Tests;

[CollectionDefinition("DapServer", DisableParallelization = true)]
public sealed class DapServerCollection
{
}

[Collection("DapServer")]
public sealed class DapServerTests
{
	[Fact]
	public void Dap_fake_backend_serves_basic_debug_requests()
	{
		string root = FindRepositoryRoot();
		string source = Path.Combine(Path.GetTempPath(), "camp-dap-fake-" + Guid.NewGuid().ToString("N") + ".camp");
		File.WriteAllText(source, "export int main() { return 0; }");

		using DapProcess dap = DapProcess.Start();
		JsonNode initialize = dap.Request("initialize", new { adapterID = "camp" });
		Assert.True(initialize["success"]?.GetValue<bool>());
		Assert.True(initialize["body"]?["supportsConfigurationDoneRequest"]?.GetValue<bool>());
		Assert.True(initialize["body"]?["supportsEvaluateForHovers"]?.GetValue<bool>());

		JsonNode launch = dap.Request("launch", new
		{
			project = source,
			cwd = root,
			args = new[] { "one" },
			stopOnEntry = true,
			backend = "fake"
		});
		Assert.True(launch["success"]?.GetValue<bool>());
		Assert.Equal("initialized", dap.ReadEvent("initialized")["event"]?.GetValue<string>());

		JsonNode breakpoints = dap.Request("setBreakpoints", new
		{
			source = new { path = source },
			breakpoints = new[] { new { line = 1 }, new { line = 3 } }
		});
		JsonArray resolvedBreakpoints = Assert.IsType<JsonArray>(breakpoints["body"]?["breakpoints"]);
		Assert.Equal(2, resolvedBreakpoints.Count);
		Assert.All(resolvedBreakpoints, bp => Assert.True(bp?["verified"]?.GetValue<bool>()));

		Assert.True(dap.Request("configurationDone", new { })["success"]?.GetValue<bool>());
		JsonNode stopped = dap.ReadEvent("stopped");
		Assert.Equal("entry", stopped["body"]?["reason"]?.GetValue<string>());

		JsonNode threads = dap.Request("threads", new { });
		Assert.Equal("Main Thread", threads["body"]?["threads"]?[0]?["name"]?.GetValue<string>());

		JsonNode stack = dap.Request("stackTrace", new { threadId = 1 });
		JsonNode? frame = stack["body"]?["stackFrames"]?[0];
		Assert.Equal("main", frame?["name"]?.GetValue<string>());
		Assert.Equal(Path.GetFullPath(source), frame?["source"]?["path"]?.GetValue<string>());

		JsonNode scopes = dap.Request("scopes", new { frameId = frame?["id"]?.GetValue<int>() ?? 1 });
		Assert.Equal("Parameters", scopes["body"]?["scopes"]?[0]?["name"]?.GetValue<string>());
		Assert.Equal("Locals", scopes["body"]?["scopes"]?[1]?["name"]?.GetValue<string>());

		JsonNode parameters = dap.Request("variables", new { variablesReference = 100 });
		Assert.Equal("args", parameters["body"]?["variables"]?[0]?["name"]?.GetValue<string>());
		Assert.Equal("{ elements, length }", parameters["body"]?["variables"]?[0]?["value"]?.GetValue<string>());
		int argsReference = parameters["body"]?["variables"]?[0]?["variablesReference"]?.GetValue<int>() ?? 0;
		JsonNode argsChildren = dap.Request("variables", new { variablesReference = argsReference });
		Assert.Equal("elements", argsChildren["body"]?["variables"]?[0]?["name"]?.GetValue<string>());
		Assert.Equal("length", argsChildren["body"]?["variables"]?[1]?["name"]?.GetValue<string>());

		JsonNode variables = dap.Request("variables", new { variablesReference = 200 });
		Assert.Equal("answer", variables["body"]?["variables"]?[0]?["name"]?.GetValue<string>());
		Assert.Equal("42", variables["body"]?["variables"]?[0]?["value"]?.GetValue<string>());

		JsonNode evaluation = dap.Request("evaluate", new { expression = "answer", frameId = 1, context = "hover" });
		Assert.Equal("42", evaluation["body"]?["result"]?.GetValue<string>());
		JsonNode unsupported = dap.Request("evaluate", new { expression = "missing + 1", frameId = 1, context = "watch" });
		Assert.Equal("Unsupported expression", unsupported["body"]?["result"]?.GetValue<string>());

		JsonNode continued = dap.Request("continue", new { threadId = 1 });
		Assert.True(continued["body"]?["allThreadsContinued"]?.GetValue<bool>());
		Assert.Equal("continued", dap.ReadEvent("continued")["event"]?.GetValue<string>());

		Assert.True(dap.Request("next", new { threadId = 1 })["success"]?.GetValue<bool>());
		Assert.Equal("step", dap.ReadEvent("stopped")["body"]?["reason"]?.GetValue<string>());

		Assert.True(dap.Request("disconnect", new { })["success"]?.GetValue<bool>());
	}

	[Fact]
	public void Dap_launch_reports_missing_backend_and_fake_build_failures()
	{
		using DapProcess missingBackend = DapProcess.Start();
		Assert.True(missingBackend.Request("initialize", new { adapterID = "camp" })["success"]?.GetValue<bool>());
		JsonNode unavailable = missingBackend.Request("launch", new { project = "main.camp", backend = "cdb" });
		Assert.False(unavailable["success"]?.GetValue<bool>());
		Assert.Contains("not available", unavailable["message"]?.GetValue<string>());
		missingBackend.Request("disconnect", new { });

		using DapProcess buildFailure = DapProcess.Start();
		Assert.True(buildFailure.Request("initialize", new { adapterID = "camp" })["success"]?.GetValue<bool>());
		JsonNode failed = buildFailure.Request("launch", new { project = "fail.campbuild", backend = "fake" });
		Assert.False(failed["success"]?.GetValue<bool>());
		Assert.Contains("Fake backend launch failure", failed["message"]?.GetValue<string>());
		buildFailure.Request("disconnect", new { });
	}

	[Fact]
	public void Dap_lldb_backend_launches_and_stops_on_camp_breakpoint_when_available()
	{
		if (!OperatingSystem.IsMacOS() || !CommandAvailable("lldb"))
			return;

		string root = FindRepositoryRoot();
		string temp = Path.Combine(Path.GetTempPath(), "camp-dap-lldb-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(temp);
		string source = Path.Combine(temp, "main.camp");
		File.WriteAllText(source, string.Join(Environment.NewLine,
			"export int helper(int value)",
			"{",
			"\tint local = value + 1;",
			"\treturn local;",
			"}",
			"",
			"export int main(string[] args)",
			"{",
			"\tint result = helper(41);",
			"\treturn result;",
			"}",
			""));

		using DapProcess dap = DapProcess.Start();
		Assert.True(dap.Request("initialize", new { adapterID = "camp" })["success"]?.GetValue<bool>());

		JsonNode launch = dap.Request("launch", new
		{
			project = source,
			cwd = root,
			args = Array.Empty<string>(),
			stopOnEntry = false,
			backend = "lldb"
		});
		Assert.True(launch["success"]?.GetValue<bool>(), launch["message"]?.GetValue<string>());
		dap.ReadEvent("initialized");

		JsonNode breakpoints = dap.Request("setBreakpoints", new
		{
			source = new { path = source },
			breakpoints = new[] { new { line = 4 } }
		});
		Assert.True(
			breakpoints["body"]?["breakpoints"]?[0]?["verified"]?.GetValue<bool>(),
			breakpoints["body"]?["breakpoints"]?[0]?["message"]?.GetValue<string>());

		Assert.True(dap.Request("configurationDone", new { })["success"]?.GetValue<bool>());
		JsonNode stopped = dap.ReadEvent("stopped");
		Assert.Equal("breakpoint", stopped["body"]?["reason"]?.GetValue<string>());

		JsonNode stack = dap.Request("stackTrace", new { threadId = 1 });
		JsonNode? frame = stack["body"]?["stackFrames"]?[0];
		Assert.NotNull(frame);
		Assert.Equal(Path.GetFullPath(source), frame?["source"]?["path"]?.GetValue<string>());
		Assert.InRange(frame?["line"]?.GetValue<int>() ?? 0, 3, 5);

		JsonNode lldbScopes = dap.Request("scopes", new { frameId = frame?["id"]?.GetValue<int>() ?? 1 });
		JsonNode lldbParameters = dap.Request("variables", new { variablesReference = lldbScopes["body"]?["scopes"]?[0]?["variablesReference"]?.GetValue<int>() ?? 100 });
		Assert.Equal("value", lldbParameters["body"]?["variables"]?[0]?["name"]?.GetValue<string>());
		Assert.Equal("41", lldbParameters["body"]?["variables"]?[0]?["value"]?.GetValue<string>());
		JsonNode lldbLocals = dap.Request("variables", new { variablesReference = lldbScopes["body"]?["scopes"]?[1]?["variablesReference"]?.GetValue<int>() ?? 200 });
		Assert.Equal("local", lldbLocals["body"]?["variables"]?[0]?["name"]?.GetValue<string>());
		Assert.Equal("42", lldbLocals["body"]?["variables"]?[0]?["value"]?.GetValue<string>());
		JsonNode lldbEvaluate = dap.Request("evaluate", new { expression = "local", frameId = 1, context = "watch" });
		Assert.Equal("42", lldbEvaluate["body"]?["result"]?.GetValue<string>());
		JsonNode lldbUnsupported = dap.Request("evaluate", new { expression = "local + 1", frameId = 1, context = "watch" });
		Assert.Equal("Unsupported expression", lldbUnsupported["body"]?["result"]?.GetValue<string>());

		Assert.True(dap.Request("next", new { threadId = 1 })["success"]?.GetValue<bool>());
		Assert.Equal("stopped", dap.ReadEvent("stopped")["event"]?.GetValue<string>());
		JsonNode continued = dap.Request("continue", new { threadId = 1 });
		Assert.True(continued["body"]?["allThreadsContinued"]?.GetValue<bool>());
		Assert.Equal("continued", dap.ReadEvent("continued")["event"]?.GetValue<string>());
		Assert.True(dap.Request("disconnect", new { })["success"]?.GetValue<bool>());
	}

	sealed class DapProcess : IDisposable
	{
		const int RequestTimeoutMilliseconds = 30000;
		const int ShutdownTimeoutMilliseconds = 3000;
		readonly Process process;
		readonly List<JsonNode> observedEvents = [];
		readonly StringBuilder stderr = new();
		readonly Task stderrReader;
		int nextSeq = 1;

		DapProcess(Process process)
		{
			this.process = process;
			stderrReader = Task.Run(ReadStandardError);
		}

		public static DapProcess Start()
		{
			string repo = FindRepositoryRoot();
			string server = Path.Combine(repo, "src", "camp-dap", "bin", "Debug", "net8.0", "camp-dap.dll");
			Process process = new()
			{
				StartInfo = new ProcessStartInfo("dotnet", server)
				{
					WorkingDirectory = repo,
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false
				}
			};
			process.Start();
			return new DapProcess(process);
		}

		public JsonNode Request(string command, object arguments)
		{
			int seq = nextSeq++;
			Send(new { seq, type = "request", command, arguments });
			DateTime deadline = DateTime.UtcNow.AddMilliseconds(RequestTimeoutMilliseconds);
			while (true)
			{
				JsonNode message = ReadMessage(RemainingMilliseconds(deadline));
				if (message["type"]?.GetValue<string>() == "response" && message["request_seq"]?.GetValue<int>() == seq)
					return message;
				if (message["type"]?.GetValue<string>() == "event")
					observedEvents.Add(message);
			}
		}

		public JsonNode ReadEvent(string eventName)
		{
			for (int i = 0; i < observedEvents.Count; i++)
			{
				if (observedEvents[i]["event"]?.GetValue<string>() == eventName)
				{
					JsonNode message = observedEvents[i];
					observedEvents.RemoveAt(i);
					return message;
				}
			}

			DateTime deadline = DateTime.UtcNow.AddMilliseconds(RequestTimeoutMilliseconds);
			while (true)
			{
				JsonNode message = ReadMessage(RemainingMilliseconds(deadline));
				if (message["type"]?.GetValue<string>() == "event" && message["event"]?.GetValue<string>() == eventName)
					return message;
				if (message["type"]?.GetValue<string>() == "event")
					observedEvents.Add(message);
			}
		}

		void Send(object message)
		{
			if (process.HasExited)
				throw new InvalidOperationException("DAP process exited before the request could be sent." + ErrorOutput());
			string json = JsonSerializer.Serialize(message);
			byte[] bytes = Encoding.UTF8.GetBytes(json);
			process.StandardInput.Write($"Content-Length: {bytes.Length}\r\n\r\n");
			process.StandardInput.Flush();
			process.StandardInput.BaseStream.Write(bytes);
			process.StandardInput.BaseStream.Flush();
		}

		JsonNode ReadMessage(int timeoutMilliseconds)
		{
			Task<JsonNode> read = Task.Run(ReadMessageBlocking);
			if (read.Wait(timeoutMilliseconds))
				return read.GetAwaiter().GetResult();
			TerminateProcess();
			throw new TimeoutException($"Timed out waiting for DAP response after {timeoutMilliseconds} ms." + ErrorOutput());
		}

		JsonNode ReadMessageBlocking()
		{
			string header = "";
			while (!header.EndsWith("\r\n\r\n", StringComparison.Ordinal))
			{
				int value = process.StandardOutput.BaseStream.ReadByte();
				if (value < 0)
					throw new InvalidOperationException(ErrorOutput());
				header += (char)value;
			}
			int length = 0;
			foreach (string line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
				if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
					length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
			byte[] body = new byte[length];
			int read = 0;
			while (read < length)
			{
				int count = process.StandardOutput.BaseStream.Read(body, read, length - read);
				if (count == 0)
					throw new InvalidOperationException(ErrorOutput());
				read += count;
			}
			return JsonNode.Parse(body) ?? throw new InvalidOperationException("Invalid DAP JSON response.");
		}

		public void Dispose()
		{
			try
			{
				if (!process.HasExited)
					Request("disconnect", new { });
			}
			catch
			{
				// Teardown must never strand a camp-dap child or hang the testhost.
			}
			TerminateProcess();
			process.Dispose();
		}

		void TerminateProcess()
		{
			try
			{
				if (process.WaitForExit(ShutdownTimeoutMilliseconds))
					return;
				if (!process.HasExited)
					process.Kill(entireProcessTree: true);
				process.WaitForExit(ShutdownTimeoutMilliseconds);
			}
			catch
			{
				// Best-effort cleanup for a process that may already have exited.
			}
			try
			{
				stderrReader.Wait(ShutdownTimeoutMilliseconds);
			}
			catch
			{
			}
		}

		void ReadStandardError()
		{
			try
			{
				char[] buffer = new char[1024];
				while (process.StandardError.Read(buffer, 0, buffer.Length) is int count && count > 0)
				{
					lock (stderr)
						stderr.Append(buffer, 0, count);
				}
			}
			catch
			{
			}
		}

		string ErrorOutput()
		{
			lock (stderr)
				return stderr.Length == 0 ? "" : Environment.NewLine + stderr;
		}

		static int RemainingMilliseconds(DateTime deadline)
		{
			double remaining = (deadline - DateTime.UtcNow).TotalMilliseconds;
			return remaining <= 0 ? 1 : (int)Math.Min(remaining, RequestTimeoutMilliseconds);
		}
	}

	static string FindRepositoryRoot()
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

	static bool CommandAvailable(string command)
	{
		ProcessStartInfo info = new("which", command)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		using Process? process = Process.Start(info);
		if (process is null)
			return false;
		process.WaitForExit();
		return process.ExitCode == 0;
	}
}
