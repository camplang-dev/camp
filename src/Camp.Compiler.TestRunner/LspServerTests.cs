using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
	public void Lsp_server_publishes_only_latest_diagnostics_after_rapid_changes()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-diagnostics-rapid-changes");
		string file = Path.Combine(root, "main.camp");
		string valid = """
			export int main()
			{
				return 0;
			}
			""";
		string broken = """
			export int main()
			{
				return ;
			}
			""";
		File.WriteAllText(file, valid);
		string uri = new Uri(file).AbsoluteUri;

		lsp.Initialize(root);
		lsp.Notify("textDocument/didOpen", new
		{
			textDocument = new { uri, languageId = "camp", version = 1, text = valid }
		});
		JsonNode firstDiagnostics = lsp.ReadNotification("textDocument/publishDiagnostics");
		Assert.Equal(0, firstDiagnostics["params"]?["diagnostics"]?.AsArray().Count);

		lsp.Notify("textDocument/didChange", new
		{
			textDocument = new { uri, version = 2 },
			contentChanges = new[] { new { text = broken } }
		});
		lsp.Notify("textDocument/didChange", new
		{
			textDocument = new { uri, version = 3 },
			contentChanges = new[] { new { text = valid } }
		});

		JsonNode latestDiagnostics = lsp.ReadNotification("textDocument/publishDiagnostics");
		Assert.Equal(0, latestDiagnostics["params"]?["diagnostics"]?.AsArray().Count);
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
	public void Lsp_server_returns_signature_help_for_call_expression()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-signature-help");
		string file = Path.Combine(root, "main.camp");
		string text = """
			/// Adds two values.
			/// - left: The first value.
			/// - right: The second value.
			int add(int left, int right = 1)
			{
				return left + right;
			}

			export int main()
			{
				return add(4, 5);
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

		JsonNode signatureHelp = lsp.Request("textDocument/signatureHelp", new
		{
			textDocument = new { uri },
			position = new { line = 10, character = 15 }
		});

		JsonNode result = signatureHelp["result"]!;
		JsonNode signature = Assert.Single(result["signatures"]!.AsArray())!;
		int? activeParameter = result["activeParameter"]?.GetValue<int>() ?? signature["activeParameter"]?.GetValue<int>();
		Assert.Equal(1, activeParameter);
		Assert.Contains("int add(int left, int right = 1)", signature["label"]?.GetValue<string>(), StringComparison.Ordinal);
		Assert.Equal("int left", signature["parameters"]?[0]?["label"]?.GetValue<string>());
		Assert.Equal("int right", signature["parameters"]?[1]?["label"]?.GetValue<string>());
		Assert.Contains("The second value.", signature["parameters"]?[1]?["documentation"]?["value"]?.GetValue<string>(), StringComparison.Ordinal);
	}

	[Fact]
	public void Lsp_server_returns_basic_completion_items()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-completion");
		string file = Path.Combine(root, "main.camp");
		string text = """
			struct Counter
			{
				int value;
				int getValue() => this.value;
				void setValue(int value) => this.value = value;
			}

			int helper() => 1;

			export int main()
			{
				Counter counter = default;
				int local = 1;
				counter.value = helper();
				return local;
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

		JsonNode scopeCompletion = lsp.Request("textDocument/completion", new
		{
			textDocument = new { uri },
			position = new { line = PositionAfter(text, "counter.value = helper()").Line, character = PositionAfter(text, "counter.value = helper()").Character }
		});
		JsonNode memberCompletion = lsp.Request("textDocument/completion", new
		{
			textDocument = new { uri },
			position = new { line = PositionAfter(text, "counter.").Line, character = PositionAfter(text, "counter.").Character }
		});

		JsonArray scopeItems = CompletionItems(scopeCompletion);
		JsonArray memberItems = CompletionItems(memberCompletion);
		Assert.Contains(scopeItems, item => item?["label"]?.GetValue<string>() == "local");
		Assert.Contains(scopeItems, item => item?["label"]?.GetValue<string>() == "helper");
		Assert.Contains(memberItems, item => item?["label"]?.GetValue<string>() == "value");
		Assert.Contains(memberItems, item => item?["label"]?.GetValue<string>() == "getValue");
		Assert.Contains(memberItems, item => item?["label"]?.GetValue<string>() == "Value" && item?["kind"]?.GetValue<int>() == 10);
	}

	[Fact]
	public void Lsp_completion_and_signature_help_use_last_good_snapshot_while_typing_broken_code()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-completion-broken-edit");
		string file = Path.Combine(root, "main.camp");
		string valid = """
			struct Counter
			{
				int value;
				int getValue(int offset = 0) => this.value + offset;
			}

			export int main()
			{
				Counter counter = default;
				int local = counter.getValue();
				return local;
			}
			""";
		string broken = """
			struct Counter
			{
				int value;
				int getValue(int offset = 0) => this.value + offset;
			}

			export int main()
			{
				Counter counter = default;
				counter.
				counter.getValue(
				return 0;
			}
			""";
		File.WriteAllText(file, valid);
		string uri = new Uri(file).AbsoluteUri;

		lsp.Initialize(root);
		lsp.Notify("textDocument/didOpen", new
		{
			textDocument = new { uri, languageId = "camp", version = 1, text = valid }
		});
		lsp.ReadNotification("textDocument/publishDiagnostics");
		lsp.Notify("textDocument/didChange", new
		{
			textDocument = new { uri, version = 2 },
			contentChanges = new[] { new { text = broken } }
		});

		CampTextPosition completionPosition = PositionAfter(broken, "counter.");
		CampTextPosition signaturePosition = PositionAfter(broken, "counter.getValue(");
		JsonNode completion = lsp.Request("textDocument/completion", new
		{
			textDocument = new { uri },
			position = new { line = completionPosition.Line, character = completionPosition.Character }
		});
		JsonNode signatureHelp = lsp.Request("textDocument/signatureHelp", new
		{
			textDocument = new { uri },
			position = new { line = signaturePosition.Line, character = signaturePosition.Character }
		});

		JsonArray completionItems = CompletionItems(completion);
		Assert.Contains(completionItems, item => item?["label"]?.GetValue<string>() == "value");
		Assert.Contains(completionItems, item => item?["label"]?.GetValue<string>() == "getValue");
		JsonNode signature = Assert.Single(signatureHelp["result"]?["signatures"]?.AsArray()!)!;
		Assert.Contains("int getValue(int offset = 0)", signature["label"]?.GetValue<string>(), StringComparison.Ordinal);
	}

	[Fact]
	public void Lsp_member_completion_handles_this_and_hides_lifecycle_helpers_while_typing()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-this-completion-broken-edit");
		string file = Path.Combine(root, "main.camp");
		string valid = """
			using Std;

			class EventLoop
			{
				int state;

				EventLoop()
				{
					this.state = 1;
				}

				void resumeAsync()
				{
					this.state = 2;
				}

				void stop()
				{
				}
			}

			export int main()
			{
				auto loop = new EventLoop();
				loop.resumeAsync();
				return 0;
			}
			""";
		string broken = """
			using Std;

			class EventLoop
			{
				int state;

				EventLoop()
				{
					this.state = 1;
				}

				void resumeAsync()
				{
					this.
					sleep(
				}

				void stop()
				{
				}
			}

			export int main()
			{
				auto loop = new EventLoop();
				loop.resumeAsync();
				return 0;
			}
			""";
		File.WriteAllText(file, valid);
		string uri = new Uri(file).AbsoluteUri;

		lsp.Initialize(root);
		lsp.Notify("textDocument/didOpen", new
		{
			textDocument = new { uri, languageId = "camp", version = 1, text = valid }
		});
		lsp.ReadNotification("textDocument/publishDiagnostics");
		lsp.Notify("textDocument/didChange", new
		{
			textDocument = new { uri, version = 2 },
			contentChanges = new[] { new { text = broken } }
		});

		CampTextPosition thisCompletionPosition = PositionAfterLast(broken, "this.");
		CampTextPosition signaturePosition = PositionAfter(broken, "sleep(");
		JsonNode completion = lsp.Request("textDocument/completion", new
		{
			textDocument = new { uri },
			position = new { line = thisCompletionPosition.Line, character = thisCompletionPosition.Character }
		});
		JsonNode signatureHelp = lsp.Request("textDocument/signatureHelp", new
		{
			textDocument = new { uri },
			position = new { line = signaturePosition.Line, character = signaturePosition.Character }
		});

		JsonArray completionItems = CompletionItems(completion);
		Assert.Contains(completionItems, item => item?["label"]?.GetValue<string>() == "state");
		Assert.Contains(completionItems, item => item?["label"]?.GetValue<string>() == "resumeAsync");
		Assert.Contains(completionItems, item => item?["label"]?.GetValue<string>() == "stop");
		Assert.DoesNotContain(completionItems, item => item?["label"]?.GetValue<string>() is "EventLoop" or "create" or "destroy" or "op_initnew" or "op_delete");
		JsonNode signature = Assert.Single(signatureHelp["result"]?["signatures"]?.AsArray()!)!;
		Assert.Contains("void sleep(nuint timeoutMs)", signature["label"]?.GetValue<string>(), StringComparison.Ordinal);
	}

	[Fact]
	public void Lsp_server_returns_references_for_source_backed_symbols()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-references");
		string file = Path.Combine(root, "main.camp");
		string text = """
			struct Counter
			{
				int value;
			}

			int helper(int value)
			{
				int local = value;
				Counter counter = default;
				counter.value = local;
				return counter.value;
			}

			export int main()
			{
				return helper(41);
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

		CampTextPosition helperPosition = PositionOf(text, "helper(41");
		CampTextPosition localPosition = PositionOf(text, "local;");
		CampTextPosition fieldPosition = PositionOf(text, "value;");
		JsonNode withoutDeclaration = lsp.Request("textDocument/references", new
		{
			textDocument = new { uri },
			position = new { line = helperPosition.Line, character = helperPosition.Character },
			context = new { includeDeclaration = false }
		});
		JsonNode withDeclaration = lsp.Request("textDocument/references", new
		{
			textDocument = new { uri },
			position = new { line = helperPosition.Line, character = helperPosition.Character },
			context = new { includeDeclaration = true }
		});
		JsonNode localReferences = lsp.Request("textDocument/references", new
		{
			textDocument = new { uri },
			position = new { line = localPosition.Line, character = localPosition.Character },
			context = new { includeDeclaration = true }
		});
		JsonNode memberReferences = lsp.Request("textDocument/references", new
		{
			textDocument = new { uri },
			position = new { line = fieldPosition.Line, character = fieldPosition.Character },
			context = new { includeDeclaration = true }
		});
		JsonNode unsupported = lsp.Request("textDocument/references", new
		{
			textDocument = new { uri },
			position = new { line = 11, character = 0 },
			context = new { includeDeclaration = true }
		});

		JsonArray withoutDeclarationResult = withoutDeclaration["result"]!.AsArray();
		JsonNode call = Assert.Single(withoutDeclarationResult)!;
		Assert.Equal(15, call["range"]?["start"]?["line"]?.GetValue<int>());
		JsonArray withDeclarationResult = withDeclaration["result"]!.AsArray();
		Assert.Equal(2, withDeclarationResult.Count);
		Assert.Contains(withDeclarationResult, location => location?["range"]?["start"]?["line"]?.GetValue<int>() == 5);
		Assert.Contains(withDeclarationResult, location => location?["range"]?["start"]?["line"]?.GetValue<int>() == 15);
		Assert.Equal([7, 9], ReferenceLines(localReferences));
		Assert.Equal([2, 9, 10], ReferenceLines(memberReferences));
		Assert.Empty(unsupported["result"]!.AsArray());
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

	[Fact]
	public void Lsp_server_returns_workspace_symbols()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-workspace-symbols");
		string file = Path.Combine(root, "main.camp");
		string text = """
			alias CounterAlias = Counter;

			struct Counter
			{
				int value;
				int getValue() => this.value;
			}

			int helper() => 1;
			""";
		File.WriteAllText(file, text);
		string uri = new Uri(file).AbsoluteUri;

		lsp.Initialize(root);
		lsp.Notify("textDocument/didOpen", new
		{
			textDocument = new { uri, languageId = "camp", version = 1, text }
		});
		lsp.ReadNotification("textDocument/publishDiagnostics");

		JsonNode symbols = lsp.Request("workspace/symbol", new
		{
			query = "Value"
		});

		JsonArray result = symbols["result"]!.AsArray();
		JsonNode getValue = Assert.Single(result, symbol => symbol?["name"]?.GetValue<string>() == "getValue")!;
		Assert.Equal("Counter", getValue["containerName"]?.GetValue<string>());
		Assert.Equal(5, getValue["location"]?["range"]?["start"]?["line"]?.GetValue<int>());
	}

	[Fact]
	public void Lsp_loose_file_includes_standard_library()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-loose-std");
		string file = Path.Combine(root, "main.camp");
		string text = """
			using Std;

			export int main()
			{
				Console.writeLine("Hello");
				return 0;
			}
			""";
		File.WriteAllText(file, text);
		string uri = new Uri(file).AbsoluteUri;

		lsp.Initialize(root);
		lsp.Notify("textDocument/didOpen", new
		{
			textDocument = new { uri, languageId = "camp", version = 1, text }
		});

		JsonNode diagnostics = lsp.ReadNotification("textDocument/publishDiagnostics");
		Assert.Empty(diagnostics["params"]?["diagnostics"]?.AsArray()!);
	}

	[Fact]
	public void Lsp_build_file_includes_existing_project_reference_api_headers_for_src_files()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-project-reference-src");
		string appRoot = Path.Combine(root, "app");
		string appSource = Path.Combine(appRoot, "src");
		string libraryRoot = Path.Combine(root, "library");
		string libraryBin = Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(CompilerDefaults.TargetName, NativeBuildKind.Static));
		Directory.CreateDirectory(appSource);
		Directory.CreateDirectory(libraryBin);
		File.WriteAllText(Path.Combine(libraryRoot, "library.campbuild"), """
			--artifact static
			--name library
			src/*.camp
			""");
		File.WriteAllText(Path.Combine(libraryBin, "library_api.camp"), """
			export extern class SharedWindow
			{
			}
			""");
		File.WriteAllText(Path.Combine(appRoot, "app.campbuild"), """
			--artifact exec
			--name app
			src/*.camp
			--project-reference ../library:static
			""");
		string file = Path.Combine(appSource, "main.camp");
		string text = """
			export int main()
			{
				SharedWindow* window = default;
				return window == default ? 0 : 1;
			}
			""";
		File.WriteAllText(file, text);
		string uri = new Uri(file).AbsoluteUri;

		lsp.Initialize(appRoot);
		lsp.Notify("textDocument/didOpen", new
		{
			textDocument = new { uri, languageId = "camp", version = 1, text }
		});

		JsonNode diagnostics = lsp.ReadNotification("textDocument/publishDiagnostics");
		Assert.Empty(diagnostics["params"]?["diagnostics"]?.AsArray()!);

		CampTextPosition sharedWindowPosition = PositionOf(text, "SharedWindow*");
		JsonNode references = lsp.Request("textDocument/references", new
		{
			textDocument = new { uri },
			position = new { line = sharedWindowPosition.Line, character = sharedWindowPosition.Character },
			context = new { includeDeclaration = true }
		});
		JsonArray result = references["result"]!.AsArray();
		Assert.Contains(result, location => location?["uri"]?.GetValue<string>().EndsWith("library_api.camp", StringComparison.Ordinal) == true);
		Assert.Contains(result, location => location?["uri"]?.GetValue<string>().EndsWith("main.camp", StringComparison.Ordinal) == true);
	}

	[Fact]
	public void Lsp_build_file_includes_project_reference_sources_when_api_header_is_missing()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-project-reference-source-fallback");
		string appRoot = Path.Combine(root, "app");
		string appSource = Path.Combine(appRoot, "src");
		string libraryRoot = Path.Combine(root, "library");
		string librarySource = Path.Combine(libraryRoot, "src");
		Directory.CreateDirectory(appSource);
		Directory.CreateDirectory(librarySource);
		File.WriteAllText(Path.Combine(libraryRoot, "library.campbuild"), """
			--artifact static
			--name library
			--nostdlib
			src/*.camp
			""");
		File.WriteAllText(Path.Combine(librarySource, "form.camp"), """
			export class Form
			{
			}

			export class Control
			{
			}
			""");
		File.WriteAllText(Path.Combine(appRoot, "app.campbuild"), """
			--artifact exec
			--name app
			--nostdlib
			src/*.camp
			--project-reference ../library:static
			""");
		string file = Path.Combine(appSource, "main.camp");
		string text = """
			export int main()
			{
				Form* form = default;
				Control* control = default;
				return form == default && control == default ? 0 : 1;
			}
			""";
		File.WriteAllText(file, text);
		string uri = new Uri(file).AbsoluteUri;

		lsp.Initialize(appRoot);
		lsp.Notify("textDocument/didOpen", new
		{
			textDocument = new { uri, languageId = "camp", version = 1, text }
		});

		JsonNode diagnostics = lsp.ReadNotification("textDocument/publishDiagnostics");
		Assert.Empty(diagnostics["params"]?["diagnostics"]?.AsArray()!);
	}

	[Fact]
	public void Lsp_build_file_can_disable_standard_library()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-build-nostd");
		string file = Path.Combine(root, "main.camp");
		File.WriteAllText(Path.Combine(root, "app.campbuild"), """
			main.camp
			--nostdlib
			--artifact none
			""");
		string text = """
			using Std;

			export int main()
			{
				Console.writeLine("Hello");
				return 0;
			}
			""";
		File.WriteAllText(file, text);
		string uri = new Uri(file).AbsoluteUri;

		lsp.Initialize(root);
		lsp.Notify("textDocument/didOpen", new
		{
			textDocument = new { uri, languageId = "camp", version = 1, text }
		});

		JsonNode diagnostics = lsp.ReadNotification("textDocument/publishDiagnostics");
		Assert.NotEmpty(diagnostics["params"]?["diagnostics"]?.AsArray()!);
	}

	static string CreateTempDirectory(string name)
	{
		string directory = Path.Combine(Path.GetTempPath(), "camp-tests", name + "-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return directory;
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

	static JsonArray CompletionItems(JsonNode response)
	{
		JsonNode? result = response["result"];
		if (result is JsonArray array)
			return array;
		return result?["items"]?.AsArray() ?? throw new InvalidOperationException("Completion response did not contain items.");
	}

	static CampTextPosition PositionAfter(string text, string marker)
	{
		int index = text.IndexOf(marker, StringComparison.Ordinal);
		if (index < 0)
			throw new InvalidOperationException($"Marker '{marker}' was not found.");
		return PositionAfterIndex(text, index + marker.Length);
	}

	static CampTextPosition PositionOf(string text, string marker)
	{
		int index = text.IndexOf(marker, StringComparison.Ordinal);
		if (index < 0)
			throw new InvalidOperationException($"Marker '{marker}' was not found.");
		return PositionAfterIndex(text, index);
	}

	static int[] ReferenceLines(JsonNode response)
	{
		return response["result"]!.AsArray()
			.Select(static location => location?["range"]?["start"]?["line"]?.GetValue<int>() ?? -1)
			.ToArray();
	}

	static CampTextPosition PositionAfterLast(string text, string marker)
	{
		int index = text.LastIndexOf(marker, StringComparison.Ordinal);
		if (index < 0)
			throw new InvalidOperationException($"Marker '{marker}' was not found.");
		return PositionAfterIndex(text, index + marker.Length);
	}

	static CampTextPosition PositionAfterIndex(string text, int index)
	{
		int line = 0;
		int character = 0;
		for (int i = 0; i < index; i++)
		{
			if (text[i] == '\n')
			{
				line++;
				character = 0;
			}
			else
				character++;
		}
		return new CampTextPosition(line, character);
	}

	static string ArtifactDirectoryForTarget(string targetName, NativeBuildKind? buildKind)
	{
		Assert.True(TargetCatalog.TryLoad(Path.Combine(FindRepositoryRoot(), "targets"), out TargetCatalog? catalog, out string? error), error);
		Assert.True(catalog!.TryGetTarget(targetName, out TargetDefinition? target));
		return BuildArtifactLayout.GetArtifactDirectoryName(target!, buildKind, "DEBUG");
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
						signatureHelp = new { },
						definition = new { },
						references = new { },
						documentSymbol = new { },
						synchronization = new { }
					},
					workspace = new
					{
						symbol = new { }
					}
				}
			});
			Assert.NotNull(response["result"]?["capabilities"]?["hoverProvider"]);
			Assert.NotNull(response["result"]?["capabilities"]?["completionProvider"]);
			Assert.NotNull(response["result"]?["capabilities"]?["signatureHelpProvider"]);
			Assert.NotNull(response["result"]?["capabilities"]?["definitionProvider"]);
			Assert.NotNull(response["result"]?["capabilities"]?["referencesProvider"]);
			Assert.NotNull(response["result"]?["capabilities"]?["documentSymbolProvider"]);
			Assert.NotNull(response["result"]?["capabilities"]?["workspaceSymbolProvider"]);
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
