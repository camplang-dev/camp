using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Camp.Compiler.Tests;

[CollectionDefinition("LspServer", DisableParallelization = true)]
public sealed class LspServerCollection;

[Collection("LspServer")]
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

		lsp.ClearObservedNotifications();
		Thread.Sleep(1000);
		lsp.Request("textDocument/hover", new
		{
			textDocument = new { uri },
			position = new { line = 0, character = 11 }
		});

		foreach (JsonNode diagnostics in lsp.ObservedNotifications("textDocument/publishDiagnostics"))
			Assert.Equal(0, diagnostics["params"]?["diagnostics"]?.AsArray().Count);
	}

	[Fact]
	public void Lsp_server_does_not_republish_identical_diagnostics()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-diagnostics-identical");
		string file = Path.Combine(root, "main.camp");
		string text = """
			export int main()
			{
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
		JsonNode firstDiagnostics = lsp.ReadNotification("textDocument/publishDiagnostics");
		Assert.Equal(0, firstDiagnostics["params"]?["diagnostics"]?.AsArray().Count);
		lsp.ClearObservedNotifications();

		lsp.Notify("textDocument/didSave", new
		{
			textDocument = new { uri }
		});
		JsonNode hover = lsp.Request("textDocument/hover", new
		{
			textDocument = new { uri },
			position = new { line = 0, character = 11 }
		});

		Assert.NotNull(hover["result"]);
		Assert.Equal(0, lsp.CountObservedNotifications("textDocument/publishDiagnostics"));
	}

	[Fact]
	public void Lsp_server_writes_per_process_trace_file()
	{
		string root = CreateTempDirectory("lsp-trace");
		string traceDirectory = Path.Combine(root, "traces");
		string file = Path.Combine(root, "main.camp");
		string text = """
			export int main()
			{
				return 0;
			}
			""";
		File.WriteAllText(file, text);
		string uri = new Uri(file).AbsoluteUri;

		using (LspProcess lsp = LspProcess.Start(traceDirectory))
		{
			lsp.Initialize(root);
			lsp.Notify("textDocument/didOpen", new
			{
				textDocument = new { uri, languageId = "camp", version = 1, text }
			});
			lsp.ReadNotification("textDocument/publishDiagnostics");
			lsp.Request("textDocument/completion", new
			{
				textDocument = new { uri },
				position = new { line = 1, character = 4 }
			});
			lsp.Request("textDocument/documentSymbol", new
			{
				textDocument = new { uri }
			});
			lsp.Request("textDocument/documentSymbol", new
			{
				textDocument = new { uri }
			});
		}

		string traceFile = Assert.Single(Directory.GetFiles(traceDirectory, "camp-lsp-*.jsonl"));
		string trace = File.ReadAllText(traceFile);
		Assert.Contains("\"event\":\"server.start\"", trace, StringComparison.Ordinal);
		Assert.Contains("\"event\":\"analysis.complete\"", trace, StringComparison.Ordinal);
		Assert.Contains("\"event\":\"query.completion\"", trace, StringComparison.Ordinal);
		Assert.Contains("\"event\":\"query.documentSymbols\"", trace, StringComparison.Ordinal);
		Assert.Equal(1, CountOccurrences(trace, "\"event\":\"queryService.build\""));
		Assert.Contains("\"event\":\"diagnostics.publish\"", trace, StringComparison.Ordinal);
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
	public void Lsp_completion_returns_override_snippets_after_override_keyword()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-override-completion");
		string file = Path.Combine(root, "main.camp");
		string valid = """
			virtual class Base
			{
				export virtual int compute(overload int value)
				{
					return 0;
				}
			}

			sealed class Derived: Base
			{
			}
			""";
		string broken = """
			virtual class Base
			{
				export virtual int compute(overload int value)
				{
					return 0;
				}
			}

			sealed class Derived: Base
			{
				override /*caret*/
			}
			""".Replace("/*caret*/", " ", StringComparison.Ordinal);
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

		CampTextPosition completionPosition = PositionAfter(broken, "override ");
		JsonNode completion = lsp.Request("textDocument/completion", new
		{
			textDocument = new { uri },
			position = new { line = completionPosition.Line, character = completionPosition.Character },
			context = new { triggerKind = 2, triggerCharacter = " " }
		});

		JsonNode item = Assert.Single(CompletionItems(completion), item => item?["label"]?.GetValue<string>() == "compute")!;
		Assert.Equal(2, item["kind"]?.GetValue<int>());
		Assert.Equal(2, item["insertTextFormat"]?.GetValue<int>());
		Assert.Contains("override int compute(overload int value)", item["insertText"]?.GetValue<string>(), StringComparison.Ordinal);
		Assert.DoesNotContain("export", item["insertText"]?.GetValue<string>(), StringComparison.Ordinal);
		Assert.DoesNotContain("virtual", item["insertText"]?.GetValue<string>(), StringComparison.Ordinal);
		Assert.Contains("$0", item["insertText"]?.GetValue<string>(), StringComparison.Ordinal);

		string sealedBroken = broken.Replace("override ", "sealed ", StringComparison.Ordinal);
		lsp.Notify("textDocument/didChange", new
		{
			textDocument = new { uri, version = 3 },
			contentChanges = new[] { new { text = sealedBroken } }
		});
		CampTextPosition sealedPosition = PositionAfterLast(sealedBroken, "sealed ");
		JsonNode sealedCompletion = lsp.Request("textDocument/completion", new
		{
			textDocument = new { uri },
			position = new { line = sealedPosition.Line, character = sealedPosition.Character },
			context = new { triggerKind = 2, triggerCharacter = " " }
		});

		JsonNode sealedItem = Assert.Single(CompletionItems(sealedCompletion), item => item?["label"]?.GetValue<string>() == "compute")!;
		Assert.Equal(2, sealedItem["insertTextFormat"]?.GetValue<int>());
		Assert.Contains("sealed int compute(overload int value)", sealedItem["insertText"]?.GetValue<string>(), StringComparison.Ordinal);
		Assert.DoesNotContain("export", sealedItem["insertText"]?.GetValue<string>(), StringComparison.Ordinal);
		Assert.DoesNotContain("virtual", sealedItem["insertText"]?.GetValue<string>(), StringComparison.Ordinal);
		Assert.DoesNotContain("sealed sealed", sealedItem["insertText"]?.GetValue<string>(), StringComparison.Ordinal);
	}

	[Fact]
	public void Lsp_completion_uses_lexical_fallback_before_first_successful_snapshot()
	{
		using LspProcess lsp = LspProcess.Start();
		string root = CreateTempDirectory("lsp-completion-lexical-fallback");
		string file = Path.Combine(root, "main.camp");
		string text = """
			int helperValue() => 1;

			export int main()
			{
				int localThing = helperValue();
				hel
				return ;
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

		CampTextPosition completionPosition = PositionAfter(text, "hel");
		JsonNode completion = lsp.Request("textDocument/completion", new
		{
			textDocument = new { uri },
			position = new { line = completionPosition.Line, character = completionPosition.Character }
		});

		JsonArray completionItems = CompletionItems(completion);
		Assert.Contains(completionItems, item => item?["label"]?.GetValue<string>() == "helperValue");
		Assert.DoesNotContain(completionItems, item => item?["label"]?.GetValue<string>() == "return");
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

	static int CountOccurrences(string text, string value)
	{
		int count = 0;
		for (int index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
			count++;
		return count;
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
		const int RequestTimeoutMilliseconds = 10000;
		const int ShutdownTimeoutMilliseconds = 3000;
		readonly Process process;
		readonly List<JsonNode> observedNotifications = [];
		readonly StringBuilder stderr = new();
		readonly Task stderrReader;
		int nextId = 1;

		LspProcess(Process process)
		{
			this.process = process;
			stderrReader = Task.Run(ReadStandardError);
		}

		public static LspProcess Start(string? traceDirectory = null)
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
			if (traceDirectory is not null)
				process.StartInfo.Environment["CAMP_LSP_TRACE_DIR"] = traceDirectory;
			else
				process.StartInfo.Environment["CAMP_LSP_TRACE"] = "0";
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
			DateTime deadline = DateTime.UtcNow.AddMilliseconds(RequestTimeoutMilliseconds);
			while (true)
			{
				JsonNode message = ReadMessage(RemainingMilliseconds(deadline));
				if (message["id"]?.GetValue<int>() == id)
					return message;
				if (message["method"] is not null)
					observedNotifications.Add(message);
			}
		}

		public void Notify(string method, object parameters)
		{
			Send(new { jsonrpc = "2.0", method, @params = parameters });
		}

		public JsonNode ReadNotification(string method)
		{
			for (int i = 0; i < observedNotifications.Count; i++)
			{
				if (observedNotifications[i]["method"]?.GetValue<string>() == method)
				{
					JsonNode message = observedNotifications[i];
					observedNotifications.RemoveAt(i);
					return message;
				}
			}

			DateTime deadline = DateTime.UtcNow.AddMilliseconds(RequestTimeoutMilliseconds);
			while (true)
			{
				JsonNode message = ReadMessage(RemainingMilliseconds(deadline));
				if (message["method"]?.GetValue<string>() == method)
					return message;
				if (message["method"] is not null)
					observedNotifications.Add(message);
			}
		}

		public void ClearObservedNotifications()
		{
			observedNotifications.Clear();
		}

		public int CountObservedNotifications(string method)
		{
			return observedNotifications.Count(message => message["method"]?.GetValue<string>() == method);
		}

		public JsonNode[] ObservedNotifications(string method)
		{
			return observedNotifications
				.Where(message => message["method"]?.GetValue<string>() == method)
				.ToArray();
		}

		void Send(object message)
		{
			if (process.HasExited)
				throw new InvalidOperationException("LSP process exited before the request could be sent." + ErrorOutput());
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
			throw new TimeoutException($"Timed out waiting for LSP response after {timeoutMilliseconds} ms." + ErrorOutput());
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
			return JsonNode.Parse(body) ?? throw new InvalidOperationException("Invalid LSP JSON response.");
		}

		public void Dispose()
		{
			try
			{
				Request("shutdown", new { });
				Notify("exit", new { });
			}
			catch
			{
				// Teardown must never strand a camp-lsp child or hang the testhost.
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
