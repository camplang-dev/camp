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
	public void Symbol_query_returns_signature_help_and_clean_hover_docs()
	{
		string root = CreateTempDirectory("language-service-signature-help");
		string source = Path.Combine(root, "main.camp");
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
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		CampHover? hover = symbols.GetHover(source, PositionOf(text, "add(4"));
		CampSignatureHelp? signatureHelp = symbols.GetSignatureHelp(source, PositionOf(text, "5);"));

		Assert.NotNull(hover);
		Assert.DoesNotContain("@summary", hover!.Markdown, StringComparison.Ordinal);
		Assert.DoesNotContain("@summary", hover.Markdown, StringComparison.Ordinal);
		Assert.Contains("Adds two values.", hover.Markdown, StringComparison.Ordinal);
		Assert.DoesNotContain("Parameters:", hover.Markdown, StringComparison.Ordinal);
		Assert.DoesNotContain("`left`: The first value.", hover.Markdown, StringComparison.Ordinal);
		Assert.DoesNotContain("`right`: The second value.", hover.Markdown, StringComparison.Ordinal);
		Assert.NotNull(signatureHelp);
		Assert.Equal(1, signatureHelp!.ActiveParameter);
		CampSignatureInformation signature = Assert.Single(signatureHelp.Signatures);
		Assert.Contains("int add(int left, int right = 1)", signature.Label, StringComparison.Ordinal);
		Assert.Equal(["int left", "int right"], signature.Parameters.Select(static parameter => parameter.Label).ToArray());
		Assert.Equal("The second value.", signature.Parameters[1].Documentation);
	}

	[Fact]
	public void Symbol_query_hides_expanded_component_parameters_in_signature_help()
	{
		string root = CreateTempDirectory("language-service-signature-expanded-components");
		string source = Path.Combine(root, "main.camp");
		string text = """
			newtype delegate void Handler(int value);

			extern void wire(Handler handler);

			export void main()
			{
				wire(default);
			}
			""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		CampHover? hover = symbols.GetHover(source, PositionOf(text, "wire(default"));
		CampSignatureHelp? signatureHelp = symbols.GetSignatureHelp(source, PositionOf(text, "default);"));

		Assert.NotNull(hover);
		Assert.DoesNotContain("extern", hover!.Markdown, StringComparison.Ordinal);
		Assert.DoesNotContain("handler_context", hover.Markdown, StringComparison.Ordinal);
		Assert.Contains("void wire(Handler handler)", hover.Markdown, StringComparison.Ordinal);
		Assert.NotNull(signatureHelp);
		CampSignatureInformation signature = Assert.Single(signatureHelp!.Signatures);
		Assert.DoesNotContain("extern", signature.Label, StringComparison.Ordinal);
		Assert.DoesNotContain("handler_context", signature.Label, StringComparison.Ordinal);
		Assert.Contains("void wire(Handler handler)", signature.Label, StringComparison.Ordinal);
		CampParameterHelp parameter = Assert.Single(signature.Parameters);
		Assert.Equal("Handler handler", parameter.Label);
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
	public void Symbol_query_maps_static_extension_and_overload_calls()
	{
		string root = CreateTempDirectory("language-service-call-tokens");
		string source = Path.Combine(root, "main.camp");
		string text = """
		class Application
		{
			static int run(int value) => value;
		}

		struct Box
		{
			int value;
		}

		int read(Box* this) => this.value;
		int choose(overload int value) => 1;
		int choose(overload string value) => 2;

		export int main()
		{
			Box box = default;
			box.value = 5;
			int one = Application.run(1);
			int two = box.read();
			int three = choose(3);
			return one + two + three;
		}
		""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		Assert.Equal(0, symbols.GetDefinition(source, PositionOf(text, "Application.run"))?.Range.Start.Line);
		Assert.Equal(2, symbols.GetDefinition(source, PositionOf(text, "run(1"))?.Range.Start.Line);
		Assert.Equal(10, symbols.GetDefinition(source, PositionOf(text, "read();"))?.Range.Start.Line);
		Assert.Equal(11, symbols.GetDefinition(source, PositionOf(text, "choose(3"))?.Range.Start.Line);
	}

	[Fact]
	public void Symbol_query_maps_callable_generic_constant_enum_and_component_references()
	{
		string root = CreateTempDirectory("language-service-mixed-symbols");
		string source = Path.Combine(root, "main.camp");
		string text = """
		extern void* malloc(nuint size);
		extern void free(void* ptr);

		newtype delegate void Handler();

		enum Mode
		{
			OPEN,
			CLOSED
		}

		class Holder<T: copyable>
		{
		}

		class Button
		{
			static const int LIMIT = 7;
			void click() {}
		}

		nuint lengthOf(int[] items)
		{
			return items.length;
		}

		export int main()
		{
			auto button = new Button();
			Handler handler = button.click;
			Holder<int>* holder = default;
			Mode mode = Mode.OPEN;
			int limit = Button.LIMIT;
			return (int)(lengthOf([1, 2, 3]) + limit);
		}
		""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		Assert.Equal(3, symbols.GetDefinition(source, PositionOf(text, "Handler handler"))?.Range.Start.Line);
		Assert.Equal(18, symbols.GetDefinition(source, PositionOf(text, "click;"))?.Range.Start.Line);
		Assert.Equal(11, symbols.GetDefinition(source, PositionOf(text, "Holder<int>"))?.Range.Start.Line);
		Assert.Equal(7, symbols.GetDefinition(source, PositionOf(text, "OPEN;"))?.Range.Start.Line);
		Assert.Equal(17, symbols.GetDefinition(source, PositionOf(text, "LIMIT;"))?.Range.Start.Line);

		CampSymbolInfo? lengthSymbol = symbols.GetSymbolAt(source, PositionOf(text, "length;"));
		Assert.NotNull(lengthSymbol);
		Assert.Equal("length", lengthSymbol!.Name);
		Assert.Equal("nuint", lengthSymbol.Type);
	}

	[Fact]
	public void Analysis_loads_used_package_api_headers_for_project_reference_api_headers()
	{
		string root = CreateTempDirectory("language-service-project-reference-package");
		string appRoot = Path.Combine(root, "app");
		string formsRoot = Path.Combine(root, "win32-forms");
		string source = Path.Combine(appRoot, "src", "main.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(source)!);
		string packageApi = Path.Combine(appRoot, "bin", "pkg-source", "ext-win32", "live", "msvc-windows-x64", "default", "DEBUG", "ext-win32_api.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(packageApi)!);
		File.WriteAllText(packageApi, """
			export as Win32;

			export newtype HWND: nint;
			export newtype fn _winapi nint WNDPROC(HWND handle);
			""");
		string formsApi = Path.Combine(formsRoot, "bin", "msvc-windows-x64", "default", "DEBUG", "win32-forms_api.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(formsApi)!);
		File.WriteAllText(formsApi, """
			export as Win32::Forms;
			using Win32;

			export escaped extern class Form
			{
				export extern Form();
				export extern HWND getHandle();
			}

			export escaped extern class Application
			{
				export extern static int run(HWND handle);
			}
			""");
		string formsBuild = Path.Combine(formsRoot, "win32-forms.campbuild");
		File.WriteAllText(formsBuild, """
			--artifact static
			--name win32-forms
			src/*.camp
			""");
		string text = """
			using Win32::Forms;

			export int main()
			{
				auto form = new Form();
				return Application.run(form.Handle);
			}
			""";
		File.WriteAllText(source, text);
		string appBuild = Path.Combine(appRoot, "app.campbuild");
		File.WriteAllText(appBuild, """
			--artifact exec
			--target msvc-windows-x64
			--use ext-win32
			--project-reference ../win32-forms
			--nostdlib
			src/*.camp
			""");

		CampProjectLoadResult result = CampProjectLoader.LoadBuildFile(appBuild, CampProjectEnvironment.Create(appRoot), CampProjectCommandKind.LanguageService);
		Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
		result.Request.IncludeFiles.AddRange(result.ProjectReferenceApiHeaders);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(result.Request);
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		CampHover? hover = symbols.GetHover(source, PositionOf(text, "run(form"));
		CampSymbolLocation? runDefinition = symbols.GetDefinition(source, PositionOf(text, "run(form"));
		CampSymbolLocation? handleDefinition = symbols.GetDefinition(source, PositionOf(text, "Handle"));

		Assert.NotNull(hover);
		Assert.Contains("run", hover!.Markdown, StringComparison.Ordinal);
		Assert.NotNull(runDefinition);
		Assert.Equal(Path.GetFullPath(formsApi), runDefinition!.Path);
		Assert.NotNull(handleDefinition);
		Assert.Equal(Path.GetFullPath(formsApi), handleDefinition!.Path);
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
