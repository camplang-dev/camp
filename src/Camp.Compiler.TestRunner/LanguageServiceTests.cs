using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class LanguageServiceTests
{
	[Fact]
	public void Analysis_uses_in_memory_overlay_instead_of_disk_text()
	{
		string root = CreateTempDirectory("language-service-overlay");
		string source = Path.Combine(root, "main.camp");
		File.WriteAllText(source, """
			export int main()
			{
				return 0;
			}
			""");
		CompilerRequest request = Request(root, source);

		CampAnalysisSnapshot broken = CampLanguageService.Analyze(request, [
			new CampSourceOverlay(source, """
				export int main()
				{
					return ;
				}
				""", Version: 1)
		]);
		CampAnalysisSnapshot fixedAgain = CampLanguageService.Analyze(request, [
			new CampSourceOverlay(source, """
				export int main()
				{
					return 1;
				}
				""", Version: 2)
		]);

		Assert.False(broken.Success);
		Assert.Contains(broken.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot", StringComparison.OrdinalIgnoreCase));
		Assert.True(fixedAgain.Success, string.Join(Environment.NewLine, fixedAgain.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		Assert.Contains("return 0", File.ReadAllText(source), StringComparison.Ordinal);
	}

	[Fact]
	public void Analysis_reports_zero_based_diagnostic_ranges()
	{
		string root = CreateTempDirectory("language-service-ranges");
		string source = Path.Combine(root, "main.camp");
		File.WriteAllText(source, """
			export int main()
			{
				return 0;
			}
			""");
		CompilerRequest request = Request(root, source);

		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(request, [
			new CampSourceOverlay(source, """
				export int main()
				{
					auto result = missing;
					return result;
				}
				""")
		]);

		CampSourceDiagnostic diagnostic = Assert.Single(snapshot.Diagnostics, static diagnostic => diagnostic.Message.Contains("missing", StringComparison.Ordinal));
		Assert.Equal(Path.GetFullPath(source), diagnostic.Path);
		Assert.NotNull(diagnostic.Range);
		Assert.Equal(2, diagnostic.Range!.Start.Line);
		Assert.True(diagnostic.Range.Start.Character >= 15);
	}

	[Fact]
	public void Symbol_query_finds_local_parameter_and_function_definitions()
	{
		string root = CreateTempDirectory("language-service-symbols");
		string source = Path.Combine(root, "main.camp");
		string text = """
			/// Adds one to a value.
			int helper(int value)
			{
				auto local = value;
				return local + 1;
			}

			export int main()
			{
				return helper(41);
			}
			""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		CampSymbolLocation? parameterDefinition = symbols.GetDefinition(source, PositionOf(text, "value;"));
		CampSymbolLocation? localDefinition = symbols.GetDefinition(source, PositionOf(text, "local +"));
		CampSymbolLocation? helperDefinition = symbols.GetDefinition(source, PositionOf(text, "helper(41"));
		CampHover? hover = symbols.GetHover(source, PositionOf(text, "helper(41"));

		Assert.NotNull(parameterDefinition);
		Assert.Equal(1, parameterDefinition!.Range.Start.Line);
		Assert.NotNull(localDefinition);
		Assert.Equal(3, localDefinition!.Range.Start.Line);
		Assert.NotNull(helperDefinition);
		Assert.Equal(1, helperDefinition!.Range.Start.Line);
		Assert.NotNull(hover);
		Assert.Contains("Adds one to a value.", hover!.Markdown, StringComparison.Ordinal);
		Assert.Contains("int helper(int value)", hover.Markdown, StringComparison.Ordinal);
	}

	[Fact]
	public void Symbol_query_finds_member_definitions()
	{
		string root = CreateTempDirectory("language-service-members");
		string source = Path.Combine(root, "main.camp");
		string text = """
			struct Counter
			{
				int value;
				int getValue() => this.value;
			}

			export int main()
			{
				Counter counter = default;
				return counter.value;
			}
			""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		CampSymbolLocation? classDefinition = symbols.GetDefinition(source, PositionOf(text, "Counter\n"));
		CampSymbolLocation? fieldDefinition = symbols.GetDefinition(source, PositionOf(text, "value;"));

		Assert.NotNull(classDefinition);
		Assert.Equal(0, classDefinition!.Range.Start.Line);
		Assert.NotNull(fieldDefinition);
		Assert.Equal(2, fieldDefinition!.Range.Start.Line);
	}

	[Fact]
	public void Symbol_query_maps_properties_inherited_members_interface_members_and_aliases()
	{
		string root = CreateTempDirectory("language-service-member-mapping");
		string source = Path.Combine(root, "main.camp");
		string text = """
		extern void* malloc(nuint size);
		extern void free(void* ptr);

		interface ICounter
		{
			void tick();
		}

		alias CounterAlias = Derived;

		class Counter: ICounter
		{
			int value;
			int getValue() => this.value;
			void tick() { this.value++; }
		}

		class Derived: Counter
		{
		}

		export int main()
		{
			auto derived = new Derived();
			ICounter* iface = derived;
			CounterAlias* aliasValue = derived;
			iface.tick();
			return derived.Value + aliasValue.Value;
		}
		""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		CampSymbolInfo? aliasSymbol = symbols.GetSymbolAt(source, PositionOf(text, "CounterAlias* aliasValue"));
		CampSymbolLocation? aliasDefinition = aliasSymbol?.Definition;
		CampSymbolLocation? inheritedPropertyDefinition = symbols.GetDefinition(source, PositionOf(text, "Value +"));
		CampSymbolLocation? inheritedMethodDefinition = symbols.GetDefinition(source, PositionOf(text, "Value;"));
		CampSymbolLocation? interfaceMethodDefinition = symbols.GetDefinition(source, PositionOf(text, "tick();"));

		Assert.True(aliasDefinition is not null, $"Alias symbol was {aliasSymbol?.Name ?? "<null>"} {aliasSymbol?.Kind.ToString() ?? ""}.");
		Assert.True(aliasDefinition!.Range.Start.Line == 8, $"Alias resolved to {aliasSymbol?.Name} {aliasSymbol?.Kind} at line {aliasDefinition.Range.Start.Line}.");
		Assert.NotNull(inheritedPropertyDefinition);
		Assert.Equal(13, inheritedPropertyDefinition!.Range.Start.Line);
		Assert.NotNull(inheritedMethodDefinition);
		Assert.Equal(13, inheritedMethodDefinition!.Range.Start.Line);
		Assert.NotNull(interfaceMethodDefinition);
		Assert.Equal(5, interfaceMethodDefinition!.Range.Start.Line);
	}

	[Fact]
	public void Symbol_query_maps_declaration_types_construction_types_and_member_tokens()
	{
		string root = CreateTempDirectory("language-service-type-and-member-tokens");
		string source = Path.Combine(root, "main.camp");
		string text = """
		extern void* malloc(nuint size);
		extern void free(void* ptr);

		class Form
		{
			string getText() => "";
			void setText(unscoped string value) {}
			void setBounds(int value) {}
		}

		class Button
		{
			string getText() => "";
			void setText(unscoped string value) {}
		}

		class BasicApp
		{
			Form* mainForm;
			Button* clickMeButton;

			void initialize()
			{
				this.mainForm = new Form();
				this.mainForm.Text = "Camp";
				this.mainForm.setBounds(1);
				this.clickMeButton = new Button();
			}
		}

		export int main()
		{
			BasicApp app = default;
			app.initialize();
			return 0;
		}
		""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		Assert.Equal(3, symbols.GetDefinition(source, PositionOf(text, "Form* mainForm"))?.Range.Start.Line);
		Assert.Equal(10, symbols.GetDefinition(source, PositionOf(text, "Button* clickMeButton"))?.Range.Start.Line);
		Assert.Equal(18, symbols.GetDefinition(source, PositionOf(text, "mainForm;"))?.Range.Start.Line);
		Assert.Equal(3, symbols.GetDefinition(source, PositionOf(text, "Form();"))?.Range.Start.Line);
		Assert.Equal(6, symbols.GetDefinition(source, PositionOf(text, "Text = \"Camp\""))?.Range.Start.Line);
		Assert.Equal(7, symbols.GetDefinition(source, PositionOf(text, "setBounds"))?.Range.Start.Line);
		Assert.Equal(10, symbols.GetDefinition(source, PositionOf(text, "Button();"))?.Range.Start.Line);
		Assert.Equal(16, symbols.GetDefinition(source, PositionOf(text, "BasicApp app"))?.Range.Start.Line);
		Assert.Equal(21, symbols.GetDefinition(source, PositionOf(text, "initialize();"))?.Range.Start.Line);
	}

	[Fact]
	public void Symbol_query_returns_workspace_symbols()
	{
		string root = CreateTempDirectory("language-service-workspace-symbols");
		string source = Path.Combine(root, "main.camp");
		string text = """
			alias CounterAlias = Counter;

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

			int helper() => 1;
			""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		IReadOnlyList<CampWorkspaceSymbol> all = symbols.GetWorkspaceSymbols("");
		IReadOnlyList<CampWorkspaceSymbol> filtered = symbols.GetWorkspaceSymbols("Value");

		Assert.Contains(all, static symbol => symbol.Name == "CounterAlias" && symbol.Kind == CampSymbolKind.Alias);
		Assert.Contains(all, static symbol => symbol.Name == "Mode" && symbol.Kind == CampSymbolKind.Type);
		Assert.Contains(all, static symbol => symbol.Name == "OPEN" && symbol.Kind == CampSymbolKind.EnumValue && symbol.ContainerName == "Mode");
		Assert.Contains(all, static symbol => symbol.Name == "value" && symbol.Kind == CampSymbolKind.Field && symbol.ContainerName == "Counter");
		Assert.Contains(all, static symbol => symbol.Name == "helper" && symbol.Kind == CampSymbolKind.Function);
		CampWorkspaceSymbol getValue = Assert.Single(filtered, static symbol => symbol.Name == "getValue");
		Assert.Equal(CampSymbolKind.Method, getValue.Kind);
		Assert.Equal("Counter", getValue.ContainerName);
	}

	[Fact]
	public void Symbol_query_returns_nested_document_symbols()
	{
		string root = CreateTempDirectory("language-service-document-symbols");
		string source = Path.Combine(root, "main.camp");
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

			int helper() => 1;
			""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		IReadOnlyList<CampDocumentSymbol> documentSymbols = symbols.GetDocumentSymbols(source);
		CampDocumentSymbol mode = Assert.Single(documentSymbols, static symbol => symbol.Name == "Mode");
		CampDocumentSymbol counter = Assert.Single(documentSymbols, static symbol => symbol.Name == "Counter");
		CampDocumentSymbol helper = Assert.Single(documentSymbols, static symbol => symbol.Name == "helper");

		Assert.Equal(CampSymbolKind.Type, mode.Kind);
		Assert.Contains(mode.Children, static symbol => symbol.Name == "OPEN" && symbol.Kind == CampSymbolKind.EnumValue);
		Assert.Contains(counter.Children, static symbol => symbol.Name == "value" && symbol.Kind == CampSymbolKind.Field);
		Assert.Contains(counter.Children, static symbol => symbol.Name == "getValue" && symbol.Kind == CampSymbolKind.Method);
		Assert.Equal(CampSymbolKind.Function, helper.Kind);
		Assert.Equal(0, mode.SelectionRange.Start.Line);
		Assert.Equal(6, counter.SelectionRange.Start.Line);
	}

	static CompilerRequest Request(string workingDirectory, string source)
	{
		CompilerRequest request = new()
		{
			RuntimeRoot = AppContext.BaseDirectory,
			WorkingDirectory = workingDirectory,
			TargetName = "clang-macos-x64",
			NoStdLib = true
		};
		request.Files.Add(Path.GetRelativePath(workingDirectory, source));
		return request;
	}

	static CampTextPosition PositionOf(string text, string marker)
	{
		int index = text.IndexOf(marker, StringComparison.Ordinal);
		if (index < 0)
			throw new InvalidOperationException($"Marker '{marker}' was not found.");
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

	static string CreateTempDirectory(string name)
	{
		string directory = Path.Combine(Path.GetTempPath(), "camp-tests", name + "-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return directory;
	}
}
