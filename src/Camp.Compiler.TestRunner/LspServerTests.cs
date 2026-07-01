using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class LspServerTests
{
	[Fact]
	public void Lsp_server_publishes_diagnostics_and_updates_after_change()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-diagnostics");
		string file = Path.Combine(root, "main.camp");
		string broken = """
			export int main()
			{
				return ;
			}
			""";
		string fixedText = """
			export int main()
			{
				return 0;
			}
			""";
		File.WriteAllText(file, broken);
		string uri = new Uri(file).AbsoluteUri;

		lsp.Initialize(root);
		lsp.Notify("textDocument/didOpen", new
		{
			textDocument = new { uri, languageId = "camp", version = 1, text = broken }
		});
		JsonNode firstDiagnostics = lsp.ReadNotification("textDocument/publishDiagnostics");
		Assert.True(firstDiagnostics["params"]?["diagnostics"]?.AsArray().Count > 0);

		lsp.Notify("textDocument/didChange", new
		{
			textDocument = new { uri, version = 2 },
			contentChanges = new[] { new { text = fixedText } }
		});
		JsonNode secondDiagnostics = lsp.ReadNotification("textDocument/publishDiagnostics");
		Assert.Equal(0, secondDiagnostics["params"]?["diagnostics"]?.AsArray().Count);
	}

	[Fact]
	public void Lsp_server_returns_hover_and_definition_for_simple_function_symbol()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-hover-definition");
		string file = Path.Combine(root, "main.camp");
		string text = """
			/// Adds one.
			int helper(int value)
			{
				return value + 1;
			}

			export int main()
			{
				return helper(41);
			}
			""";
		File.WriteAllText(file, text);
		string uri = new Uri(file).AbsoluteUri;
		int line = 8;
		int character = 8;

		lsp.Initialize(root);
		lsp.Notify("textDocument/didOpen", new
		{
			textDocument = new { uri, languageId = "camp", version = 1, text }
		});
		lsp.ReadNotification("textDocument/publishDiagnostics");

		JsonNode hover = lsp.Request("textDocument/hover", new
		{
			textDocument = new { uri },
			position = new { line, character }
		});
		Assert.Contains("Adds one.", hover["result"]?["contents"]?["value"]?.GetValue<string>(), StringComparison.Ordinal);
		Assert.Contains("int helper(int value)", hover["result"]?["contents"]?["value"]?.GetValue<string>(), StringComparison.Ordinal);

		JsonNode definition = lsp.Request("textDocument/definition", new
		{
			textDocument = new { uri },
			position = new { line, character }
		});
		Assert.Equal(1, definition["result"]?["range"]?["start"]?["line"]?.GetValue<int>());
		Assert.Equal(4, definition["result"]?["range"]?["start"]?["character"]?.GetValue<int>());
	}

	[Fact]
	public void Lsp_server_returns_document_symbols()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-document-symbols");
		string file = Path.Combine(root, "main.camp");
		string text = """
			enum Mode
			{
				OPEN,
				CLOSED
			}

			struct Counter
			{
				int value;
				int getValue() => this.value;
			}
			""";
		File.WriteAllText(file, text);
		string uri = new Uri(file).AbsoluteUri;

		lsp.Initialize(root);
		lsp.Notify("textDocument/didOpen", new
		{
			textDocument = new { uri, languageId = "camp", version = 1, text }
		});
		lsp.ReadNotification("textDocument/publishDiagnostics");

		JsonNode symbols = lsp.Request("textDocument/documentSymbol", new
		{
			textDocument = new { uri }
		});

		JsonArray result = symbols["result"]!.AsArray();
		JsonNode mode = Assert.Single(result, symbol => symbol?["name"]?.GetValue<string>() == "Mode")!;
		JsonNode counter = Assert.Single(result, symbol => symbol?["name"]?.GetValue<string>() == "Counter")!;
		Assert.Contains(mode["children"]!.AsArray(), symbol => symbol?["name"]?.GetValue<string>() == "OPEN");
		Assert.Contains(counter["children"]!.AsArray(), symbol => symbol?["name"]?.GetValue<string>() == "value");
		Assert.Contains(counter["children"]!.AsArray(), symbol => symbol?["name"]?.GetValue<string>() == "getValue");
	}

	static string CreateTempDirectory(string name)
	{
		string directory = Path.Combine(Path.GetTempPath(), "camp-tests", name + "-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return directory;
	}

	sealed class LspProcess : IDisposable
	{
		readonly Process process;
		int nextId = 1;

		LspProcess(Process process)
		{
			this.process = process;
		}

		public static LspProcess Start()
		{
			string repo = FindRepositoryRoot();
			string server = Path.Combine(repo, "src", "camp-lsp", "bin", "Debug", "net8.0", "camp-lsp.dll");
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
			return new LspProcess(process);
		}

		public void Initialize(string root)
		{
			JsonNode response = Request("initialize", new
			{
				processId = (int?)null,
				rootUri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri,
				capabilities = new
				{
					textDocument = new
					{
						hover = new { },
						definition = new { },
						documentSymbol = new { },
						synchronization = new { }
					}
				}
			});
			Assert.NotNull(response["result"]?["capabilities"]?["hoverProvider"]);
			Assert.NotNull(response["result"]?["capabilities"]?["definitionProvider"]);
			Assert.NotNull(response["result"]?["capabilities"]?["documentSymbolProvider"]);
			Notify("initialized", new { });
		}

		public JsonNode Request(string method, object parameters)
		{
			int id = nextId++;
			Send(new { jsonrpc = "2.0", id, method, @params = parameters });
			while (true)
			{
				JsonNode message = ReadMessage();
				if (message["id"]?.GetValue<int>() == id)
					return message;
			}
		}

		public void Notify(string method, object parameters)
		{
			Send(new { jsonrpc = "2.0", method, @params = parameters });
		}

		public JsonNode ReadNotification(string method)
		{
			while (true)
			{
				JsonNode message = ReadMessage();
				if (message["method"]?.GetValue<string>() == method)
					return message;
			}
		}

		void Send(object message)
		{
			string json = JsonSerializer.Serialize(message);
			byte[] bytes = Encoding.UTF8.GetBytes(json);
			process.StandardInput.Write($"Content-Length: {bytes.Length}\r\n\r\n");
			process.StandardInput.Flush();
			process.StandardInput.BaseStream.Write(bytes);
			process.StandardInput.BaseStream.Flush();
		}

		JsonNode ReadMessage()
		{
			string header = "";
			while (!header.EndsWith("\r\n\r\n", StringComparison.Ordinal))
			{
				int value = process.StandardOutput.BaseStream.ReadByte();
				if (value < 0)
					throw new InvalidOperationException(process.StandardError.ReadToEnd());
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
					throw new InvalidOperationException(process.StandardError.ReadToEnd());
				read += count;
			}
			return JsonNode.Parse(body) ?? throw new InvalidOperationException("Invalid LSP JSON response.");
		}

		public void Dispose()
		{
			try
			{
				Request("shutdown", new { });
				Notify("exit", new { });
				if (!process.WaitForExit(2000))
					process.Kill(entireProcessTree: true);
			}
			catch
			{
				if (!process.HasExited)
					process.Kill(entireProcessTree: true);
			}
			process.Dispose();
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
	}
}
