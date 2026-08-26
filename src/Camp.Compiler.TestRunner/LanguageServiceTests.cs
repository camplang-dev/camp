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
	public void Analysis_reports_source_capture_default_diagnostics()
	{
		string root = CreateTempDirectory("language-service-source-capture");
		string source = Path.Combine(root, "main.camp");
		File.WriteAllText(source, """
			extern void captureProperty(string key = caller(propertyname));

			export void main()
			{
				captureProperty();
			}
			""");
		CompilerRequest request = Request(root, source);

		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(request);

		CampSourceDiagnostic diagnostic = Assert.Single(snapshot.Diagnostics, static diagnostic => diagnostic.Message.Contains("default caller(propertyname) is not supplied outside a property accessor body.", StringComparison.Ordinal));
		Assert.Equal(Path.GetFullPath(source), diagnostic.Path);
		Assert.NotNull(diagnostic.Range);
		Assert.Equal(4, diagnostic.Range!.Start.Line);
	}

	[Fact]
	public void Analysis_accepts_prep_declarations_and_default_transformed_calls()
	{
		string root = CreateTempDirectory("language-service-prep");
		string source = Path.Combine(root, "main.camp");
		string text = """
			prep char[] writeValue()
			{
				if (buffer.length > 0)
					buffer[0] = 'x';
				return 1;
			}

			struct Label
			{
				prep char[] getText(this) => 0;
			}

			export int main()
			{
				char[] text = writeValue();
				Label label = default;
				_ = label;
				return text.length == 1 ? 0 : 1;
			}
			""";
		File.WriteAllText(source, text);
		CompilerRequest request = Request(root, source);

		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(request);

		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);
		CampHover? callHover = symbols.GetHover(source, PositionOf(text, "writeValue();"));
		CampHover? resultHover = symbols.GetHover(source, PositionOf(text, "text.length"));
		CampSignatureHelp? signatureHelp = symbols.GetSignatureHelp(source, PositionAfterLast(text, "writeValue("));
		string completionText = text.Replace("_ = label;", "label.", StringComparison.Ordinal);
		IReadOnlyList<CampCompletionItem> completions = symbols.GetCompletions(source, PositionAfter(completionText, "label."), completionText);
		Assert.NotNull(callHover);
		Assert.Contains("prep char[] writeValue()", callHover!.Markdown, StringComparison.Ordinal);
		Assert.NotNull(resultHover);
		Assert.Contains("Type: `char[]`", resultHover!.Markdown, StringComparison.Ordinal);
		Assert.NotNull(signatureHelp);
		Assert.Contains("prep char[] writeValue()", Assert.Single(signatureHelp!.Signatures).Label, StringComparison.Ordinal);
		Assert.Contains(completions, static item => item.Label == "getText");
		Assert.DoesNotContain(completions, static item => item.Label == "Text");
	}

	[Fact]
	public void Test_discovery_snapshot_exposes_manifest_records_and_runner_diagnostics()
	{
		string root = CreateTempDirectory("language-service-test-discovery");
		string source = Path.Combine(root, "main.camp");
		string text = """
			namespace EditorTests;

			struct Assertion
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			/// Valid case.
			/// @test
			void validCase(thrown Assertion* assertion)
			{
			}

			/// Invalid case.
			@test
			int invalidCase()
			{
				return 0;
			}

			namespace BlockTests
			{
				/// Block case.
				@test
				void blockCase(thrown EditorTests::Assertion* assertion)
				{
				}
			}

			namespace global
			{
				/// Root case.
				@test
				void rootCase(thrown EditorTests::Assertion* assertion)
				{
				}
			}
			""";
		File.WriteAllText(source, text);
		CompilerRequest request = Request(root, source);
		request.SourcefileDefaultRoot = root;

		CampTestDiscoverySnapshot snapshot = CampLanguageService.DiscoverTests(request);

		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		Assert.Equal(4, snapshot.Tests.Count);
		CampDiscoveredTest valid = snapshot.Tests.Single(static test => test.Name == "validCase");
		Assert.Equal("EditorTests::validCase", valid.Id);
		Assert.Equal("Valid case.", valid.Summary);
		Assert.Equal("valid", valid.RunnerSignature);
		Assert.Equal(Path.GetFullPath(source), valid.Path);
		Assert.True(valid.Range.Start.Line >= 4);

		CampDiscoveredTest invalid = snapshot.Tests.Single(static test => test.Name == "invalidCase");
		Assert.Equal("invalid", invalid.RunnerSignature);
		CampDiscoveredTest block = snapshot.Tests.Single(static test => test.Name == "blockCase");
		Assert.Equal("BlockTests::blockCase", block.Id);
		Assert.Equal("valid", block.RunnerSignature);
		CampDiscoveredTest rootCase = snapshot.Tests.Single(static test => test.Name == "rootCase");
		Assert.Equal("rootCase", rootCase.Id);
		Assert.Equal("valid", rootCase.RunnerSignature);
		CampSourceDiagnostic diagnostic = Assert.Single(snapshot.Diagnostics, static diagnostic => diagnostic.Code == "CAMPTEST001");
		Assert.Equal(Path.GetFullPath(source), diagnostic.Path);
		Assert.Equal(invalid.Range.Start.Line, diagnostic.Range?.Start.Line);
		Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
		Assert.Contains("void name(thrown TYPE*) or void name(within Allocator* allocator, thrown TYPE*)", diagnostic.Message, StringComparison.Ordinal);
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
		CampHover? parameterHover = symbols.GetHover(source, PositionOf(text, "left +"));
		CampSignatureHelp? signatureHelp = symbols.GetSignatureHelp(source, PositionOf(text, "5);"));

		Assert.NotNull(hover);
		Assert.DoesNotContain("@summary", hover!.Markdown, StringComparison.Ordinal);
		Assert.DoesNotContain("@summary", hover.Markdown, StringComparison.Ordinal);
		Assert.Contains("Adds two values.", hover.Markdown, StringComparison.Ordinal);
		Assert.DoesNotContain("Parameters:", hover.Markdown, StringComparison.Ordinal);
		Assert.DoesNotContain("`left`: The first value.", hover.Markdown, StringComparison.Ordinal);
		Assert.DoesNotContain("`right`: The second value.", hover.Markdown, StringComparison.Ordinal);
		Assert.NotNull(parameterHover);
		Assert.Contains("The first value.", parameterHover!.Markdown, StringComparison.Ordinal);
		Assert.DoesNotContain("Adds two values.", parameterHover.Markdown, StringComparison.Ordinal);
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
	public void Symbol_query_hides_expanded_return_parameters_in_display_signatures()
	{
		FunctionDefinition function = new()
		{
			Name = "getName",
			Symbol = "getName",
			ReturnType = new ArrayTypeReference
			{
				ElementType = new ConstTypeReference
				{
					Type = new PrimitiveTypeReference { Type = PrimitiveType.Char, ResolvedType = "char" },
					ResolvedType = "const char"
				},
				ResolvedType = "const char[]"
			},
			ResolvedType = "const char[]"
		};
		function.Parameters.Add(new ParameterDefinition
		{
			Name = "index",
			Symbol = "index",
			Type = new PrimitiveTypeReference { Type = PrimitiveType.Int, ResolvedType = "int" },
			ResolvedType = "int"
		});
		function.Parameters.Add(new ParameterDefinition
		{
			Name = "result_length",
			Symbol = "result_length",
			Modifier = ParameterModifier.Out,
			Type = new PrimitiveTypeReference { Type = PrimitiveType.NUInt, ResolvedType = "nuint" },
			ResolvedType = "nuint"
		});

		string? signature = CampSymbolQueryService.FormatSignatureForLanguageService(function);

		Assert.Contains("const char[] getName(int index)", signature, StringComparison.Ordinal);
		Assert.DoesNotContain("result_length", signature, StringComparison.Ordinal);
	}

	[Fact]
	public void Symbol_query_displays_canonical_prep_as_return_position()
	{
		FunctionDefinition function = new()
		{
			Name = "writeValue",
			Symbol = "writeValue",
			ReturnType = new PrimitiveTypeReference { Type = PrimitiveType.NUInt, ResolvedType = "nuint" },
			ResolvedType = "nuint"
		};
		function.Parameters.Add(new ParameterDefinition
		{
			Name = "buffer",
			Symbol = "buffer",
			Modifier = ParameterModifier.Prep,
			Type = new ArrayTypeReference
			{
				ElementType = new PrimitiveTypeReference { Type = PrimitiveType.Char, ResolvedType = "char" },
				ResolvedType = "char[]"
			},
			DefaultValue = new DefaultExpression(),
			ResolvedType = "char[]"
		});

		string? signature = CampSymbolQueryService.FormatSignatureForLanguageService(function);

		Assert.Equal("prep char[] writeValue();", signature);
	}

	[Fact]
	public void Symbol_query_keeps_noncanonical_prep_in_lowered_position()
	{
		FunctionDefinition function = new()
		{
			Name = "render",
			Symbol = "render",
			ReturnType = new PrimitiveTypeReference { Type = PrimitiveType.NUInt, ResolvedType = "nuint" },
			ResolvedType = "nuint"
		};
		function.Parameters.Add(new ParameterDefinition
		{
			Name = "style",
			Symbol = "style",
			Type = new PrimitiveTypeReference { Type = PrimitiveType.Int, ResolvedType = "int" },
			ResolvedType = "int"
		});
		function.Parameters.Add(new ParameterDefinition
		{
			Name = "buffer",
			Symbol = "buffer",
			Modifier = ParameterModifier.Prep,
			Type = new ArrayTypeReference
			{
				ElementType = new PrimitiveTypeReference { Type = PrimitiveType.Char, ResolvedType = "char" },
				ResolvedType = "char[]"
			},
			DefaultValue = new DefaultExpression(),
			ResolvedType = "char[]"
		});
		function.Parameters.Add(new ParameterDefinition
		{
			Name = "suffix",
			Symbol = "suffix",
			Type = new PrimitiveTypeReference { Type = PrimitiveType.Bool, ResolvedType = "bool" },
			DefaultValue = new DefaultExpression(),
			ResolvedType = "bool"
		});

		string? signature = CampSymbolQueryService.FormatSignatureForLanguageService(function);

		Assert.Contains("nuint render(int style, prep char[] buffer = default, bool suffix = default)", signature, StringComparison.Ordinal);
		Assert.DoesNotContain("prep char[] render", signature, StringComparison.Ordinal);
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
	public void Analysis_accepts_this_member_access_in_instance_iterator_generators()
	{
		string root = CreateTempDirectory("language-service-iterator-this");
		string source = Path.Combine(root, "main.camp");
		string text = """
			extern void* malloc(nuint size);
			extern void free(void* ptr);

			struct Range
			{
				int first;
				int last;

				struct iter int values()
				{
					yield this.first;
					yield this.last;
				}
			}

			class Source
			{
				int seed;

				class iter int values(escaped this)
				{
					yield this.seed;
					yield break;
				}
			}

			export int main()
			{
				Range range = {.first = 1, .last = 2};
				auto rangeValues = range.values();
				auto source = new Source();
				auto sourceValues = source.values();
				delete source;
				return 0;
			}
			""";
		File.WriteAllText(source, text);

		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));

		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
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
			void tick(): ICounter { this.value++; }
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
	public void Symbol_query_returns_declaration_based_references()
	{
		string root = CreateTempDirectory("language-service-references");
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
			void add(int amount)
			{
				this.value = this.value + amount;
			}
		}

		struct Box<T: copyable>
		{
			T* value;
		}

		int helper(Counter* counter, int amount)
		{
			counter.add(amount);
			CounterAlias* aliasCounter = counter;
			Mode mode = Mode.OPEN;
			int local = amount + aliasCounter.Value + counter.value;
			local = helperValue(local);
			return local;
		}

		int helperValue(int value) => value;

		export int main()
		{
			Counter counter = Counter();
			Box<Counter> boxed = default;
			return helper(&counter, 2);
		}
		""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		Assert.Equal([29, 29, 30], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "local = amount"), includeDeclaration: false)));
		Assert.Equal([28, 29, 29, 30], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "local = amount"), includeDeclaration: true)));
		Assert.Equal([25, 28], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "amount)\n{\n\tcounter"), includeDeclaration: false)));
		Assert.Equal([23, 25, 28], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "amount)\n{\n\tcounter"), includeDeclaration: true)));
		Assert.Equal([39], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "helper(&"), includeDeclaration: false)));
		Assert.Equal([23, 39], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "helper(&"), includeDeclaration: true)));
		Assert.Equal([11, 14, 14, 28], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "value;"), includeDeclaration: false)));
		Assert.Equal([10, 11, 14, 14, 28], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "value;"), includeDeclaration: true)));
		Assert.Equal([25], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "add(amount"), includeDeclaration: false)));
		Assert.Equal([12, 25], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "add(amount"), includeDeclaration: true)));
		Assert.Equal([27], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "OPEN;"), includeDeclaration: false)));
		Assert.Equal([4, 27], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "OPEN;"), includeDeclaration: true)));
		Assert.Equal([26], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "CounterAlias*"), includeDeclaration: false)));
		Assert.Equal([0, 26], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "CounterAlias*"), includeDeclaration: true)));
		Assert.Equal([11, 28], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "Value +"), includeDeclaration: true)));
		Assert.Equal([23, 37, 38], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "Counter\n{"), includeDeclaration: false)));
		Assert.Equal([8, 23, 37, 38], ReferenceLines(symbols.GetReferences(source, PositionOf(text, "Counter\n{"), includeDeclaration: true)));
		Assert.Empty(symbols.GetReferences(source, PositionOf(text, "CLOSED"), includeDeclaration: false));
	}

	[Fact]
	public void Analysis_loads_used_package_api_headers_for_project_reference_api_headers()
	{
		string root = CreateTempDirectory("language-service-project-reference-package");
		string appRoot = Path.Combine(root, "app");
		string formsRoot = Path.Combine(root, "win32-forms");
		string source = Path.Combine(appRoot, "src", "main.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(source)!);
		string packageApi = Path.Combine(appRoot, "cache", "pkg", "ext-win32", "1.0.0", "bin", "msvc-windows-x64_static_DEBUG", "ext-win32_api.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(packageApi)!);
		File.WriteAllText(Path.Combine(appRoot, "packages.ini"), """
			[ext-win32]
			identity=ext-win32
			version=1.0.0
			sha256=0000000000000000000000000000000000000000000000000000000000000000
			""");
		File.WriteAllText(packageApi, """
			namespace Win32;

			export newtype HWND: nint;
			export newtype fn _winapi nint WNDPROC(HWND handle);
			""");
		string formsApi = Path.Combine(formsRoot, "bin", "msvc-windows-x64_static_DEBUG", "win32-forms_api.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(formsApi)!);
		File.WriteAllText(formsApi, """
			namespace Win32::Forms;
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
			--use ext-win32:static
			--project-reference ../win32-forms:static
			--nostdlib
			src/*.camp
			""");

		CampProjectLoadResult result = CampProjectLoader.LoadBuildFile(appBuild, CampProjectEnvironment.Create(appRoot), CampProjectCommandKind.LanguageService);
		Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
		result.Request.ApiFiles.AddRange(result.ProjectReferenceApiHeaders);
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
	public void Analysis_uses_cached_standard_api_docs_for_loose_files()
	{
		string root = CreateTempDirectory("language-service-std-api-docs");
		string runtimeRoot = Path.Combine(root, "runtime");
		string appRoot = Path.Combine(root, "app");
		string source = Path.Combine(appRoot, "timer.camp");
		Directory.CreateDirectory(appRoot);
		string stdApi = Path.Combine(runtimeRoot, "cache", "lib", "std", "bin", "clang-macos-x64_static_DEBUG", "std_api.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(stdApi)!);
		File.WriteAllText(stdApi, """
			namespace Std;

			@summary("Completes after approximately the requested duration.")
			export extern void sleepAsync(nuint timeoutMs);
			""");
		string text = """
			using Std;

			export void main()
			{
				sleepAsync(10);
			}
			""";
		File.WriteAllText(source, text);
		CompilerRequest request = RequestWithStd(runtimeRoot, appRoot, source);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(request);
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		CampHover? hover = symbols.GetHover(source, PositionOf(text, "sleepAsync"));

		Assert.NotNull(hover);
		Assert.Contains("Completes after approximately the requested duration.", hover!.Markdown, StringComparison.Ordinal);
	}

	[Fact]
	public void Symbol_query_returns_imported_static_type_member_completions()
	{
		string root = CreateTempDirectory("language-service-static-std-completion");
		string runtimeRoot = Path.Combine(root, "runtime");
		string appRoot = Path.Combine(root, "app");
		string source = Path.Combine(appRoot, "main.camp");
		Directory.CreateDirectory(appRoot);
		string stdApi = Path.Combine(runtimeRoot, "cache", "lib", "std", "bin", "clang-macos-x64_static_DEBUG", "std_api.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(stdApi)!);
		File.WriteAllText(stdApi, """
			namespace Std;

			export extern class Console
			{
				export extern static void write(overload string value);
				export extern static void writeLine(overload string value);
				export extern void instanceOnly();
			}
			""");
		string valid = """
			using Std;

			export void main()
			{
				Console.writeLine("Hello");
			}
			""";
		string currentText = """
			using Std;

			export void main()
			{
				Console.
			}
			""";
		File.WriteAllText(source, valid);
		CompilerRequest request = RequestWithStd(runtimeRoot, appRoot, source);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(request);
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		IReadOnlyList<CampCompletionItem> completions = symbols.GetCompletions(source, PositionAfter(currentText, "Console."), currentText);

		Assert.Contains(completions, static item => item.Label == "write" && item.Kind == CampSymbolKind.Method);
		Assert.Contains(completions, static item => item.Label == "writeLine" && item.Kind == CampSymbolKind.Method);
		Assert.DoesNotContain(completions, static item => item.Label == "instanceOnly");
	}

	[Fact]
	public void Symbol_query_filters_out_of_scope_static_type_members_by_owner()
	{
		string root = CreateTempDirectory("language-service-static-extension-completion");
		string source = Path.Combine(root, "main.camp");
		string text = """
			newtype HPEN : nint;
			newtype HBRUSH : nint;
			newtype HWND : nint;
			newtype HFONT : nint;

			static HPEN HPEN.create(overload int value, int color = 0) => default;
			static HPEN HPEN.create(overload string value) => default;
			static HBRUSH HBRUSH.create(int style) => default;
			static HWND HWND.create() => default;
			static HFONT HFONT.create(int size) => default;
			HFONT create() => default;

			export void main()
			{
				auto pen = HPEN.create(1);
			}
			""";
		string currentText = """
			newtype HPEN : nint;
			newtype HBRUSH : nint;
			newtype HWND : nint;
			newtype HFONT : nint;

			static HPEN HPEN.create(overload int value, int color = 0) => default;
			static HPEN HPEN.create(overload string value) => default;
			static HBRUSH HBRUSH.create(int style) => default;
			static HWND HWND.create() => default;
			static HFONT HFONT.create(int size) => default;
			HFONT create() => default;

			export void main()
			{
				HPEN.
				auto pen = HPEN.create(1);
			}
			""";
		string brokenCallText = """
			newtype HPEN : nint;
			newtype HBRUSH : nint;
			newtype HWND : nint;
			newtype HFONT : nint;

			static HPEN HPEN.create(overload int value, int color = 0) => default;
			static HPEN HPEN.create(overload string value) => default;
			static HBRUSH HBRUSH.create(int style) => default;
			static HWND HWND.create() => default;
			static HFONT HFONT.create(int size) => default;
			HFONT create() => default;

			export void main()
			{
				auto pen = HPEN.create(
			}
			""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		IReadOnlyList<CampCompletionItem> completions = symbols.GetCompletions(source, PositionAfter(currentText, "HPEN."), currentText);
		CampSignatureHelp? signatureHelp = symbols.GetSignatureHelp(source, PositionAfter(text, "HPEN.create("), text);
		CampSignatureHelp? brokenSignatureHelp = symbols.GetSignatureHelp(source, PositionAfter(brokenCallText, "HPEN.create("), brokenCallText);

		CampCompletionItem completion = Assert.Single(completions, static item => item.Label == "create" && item.Kind == CampSymbolKind.Method);
		Assert.Contains("2 overloads", completion.Detail, StringComparison.Ordinal);
		Assert.DoesNotContain(completions, static item => item.Detail?.Contains("HBRUSH.create", StringComparison.Ordinal) == true);
		Assert.NotNull(signatureHelp);
		Assert.Equal(2, signatureHelp!.Signatures.Count);
		Assert.All(signatureHelp.Signatures, static signature =>
		{
			Assert.Contains("HPEN.create", signature.Label, StringComparison.Ordinal);
			Assert.DoesNotContain("HBRUSH.create", signature.Label, StringComparison.Ordinal);
			Assert.DoesNotContain("HWND.create", signature.Label, StringComparison.Ordinal);
			Assert.DoesNotContain("HFONT.create", signature.Label, StringComparison.Ordinal);
		});
		Assert.NotNull(brokenSignatureHelp);
		Assert.Equal(2, brokenSignatureHelp!.Signatures.Count);
		Assert.All(brokenSignatureHelp.Signatures, static signature =>
		{
			Assert.Contains("HPEN.create", signature.Label, StringComparison.Ordinal);
			Assert.DoesNotContain("HBRUSH.create", signature.Label, StringComparison.Ordinal);
			Assert.DoesNotContain("HWND.create", signature.Label, StringComparison.Ordinal);
			Assert.DoesNotContain("HFONT.create", signature.Label, StringComparison.Ordinal);
		});
	}

	[Fact]
	public void Symbol_query_handles_late_overload_selectors()
	{
		string root = CreateTempDirectory("language-service-late-overload-selectors");
		string source = Path.Combine(root, "main.camp");
		string text = """
			class JsonArray
			{
				void setElement(@index nuint index, overload int value)
				{
				}
				void setElement(@index nuint index, overload bool value)
				{
				}
				void setElement(@index nuint index, overload string value)
				{
				}
			}

			virtual class Base
			{
				export virtual int compute(int level, overload int value)
				{
					return value;
				}
			}

			sealed class Derived: Base
			{
			}

			void main(JsonArray* json)
			{
				json.setElement(0, true);
				json.ElementString[1] = null;
			}
			""";
		string completionText = text.Replace("json.setElement(0, true);", "json.", StringComparison.Ordinal);
		string overrideText = text.Replace("sealed class Derived: Base\n{\n}", "sealed class Derived: Base\n{\n\toverride \n}", StringComparison.Ordinal);
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		IReadOnlyList<CampCompletionItem> completions = symbols.GetCompletions(source, PositionAfter(completionText, "json."), completionText);
		CampSignatureHelp? signatureHelp = symbols.GetSignatureHelp(source, PositionOf(text, "true"));
		CampHover? hover = symbols.GetHover(source, PositionOf(text, "setElement(0"));
		IReadOnlyList<CampCompletionItem> overrideCompletions = symbols.GetCompletions(source, PositionAfter(overrideText, "override "), overrideText, requireFinallyForWhitespaceTrigger: true);

		Assert.Contains(completions, static item => item.Label == "ElementString" && item.Kind == CampSymbolKind.Property);
		CampCompletionItem setElement = Assert.Single(completions, static item => item.Label == "setElement" && item.Kind == CampSymbolKind.Method);
		Assert.Contains("3 overloads", setElement.Detail, StringComparison.Ordinal);
		Assert.NotNull(signatureHelp);
		CampSignatureInformation signature = Assert.Single(signatureHelp!.Signatures);
		Assert.Equal(1, signatureHelp.ActiveParameter);
		Assert.Contains("void setElement(@index nuint index, overload bool value)", signature.Label, StringComparison.Ordinal);
		Assert.NotNull(hover);
		Assert.Contains("void setElement(@index nuint index, overload bool value)", hover!.Markdown, StringComparison.Ordinal);
		CampCompletionItem compute = Assert.Single(overrideCompletions, static item => item.Label == "compute");
		Assert.Contains("int compute(int level, overload int value)", compute.InsertText, StringComparison.Ordinal);
	}

	[Fact]
	public void Symbol_query_returns_signature_help_for_member_chain_extension_call()
	{
		string root = CreateTempDirectory("language-service-member-chain-signature-help");
		string source = Path.Combine(root, "main.camp");
		string text = """
			newtype HDC : nint;
			newtype HPEN : nint;

			struct Env
			{
				HDC hdc;
			}

			void selectObject(HDC this, overload HPEN pen) {}
			void selectObject(HDC this, overload int mode) {}

			export void main()
			{
				Env e = Env();
				e.hdc.selectObject((HPEN)0);
			}
			""";
		string currentText = """
			newtype HDC : nint;
			newtype HPEN : nint;

			struct Env
			{
				HDC hdc;
			}

			void selectObject(HDC this, overload HPEN pen) {}
			void selectObject(HDC this, overload int mode) {}

			export void main()
			{
				Env e = Env();
				e.hdc.selectObject(
			}
			""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		CampSignatureHelp? signatureHelp = symbols.GetSignatureHelp(source, PositionAfter(currentText, "e.hdc.selectObject("), currentText);

		Assert.NotNull(signatureHelp);
		Assert.Equal(2, signatureHelp!.Signatures.Count);
		Assert.All(signatureHelp.Signatures, static signature =>
		{
			Assert.Contains("selectObject", signature.Label, StringComparison.Ordinal);
			Assert.DoesNotContain("HBRUSH", signature.Label, StringComparison.Ordinal);
		});
		Assert.Contains(signatureHelp.Signatures, static signature => signature.Label.Contains("HPEN pen", StringComparison.Ordinal));
		Assert.Contains(signatureHelp.Signatures, static signature => signature.Label.Contains("int mode", StringComparison.Ordinal));
	}

	[Fact]
	public void Symbol_query_returns_finally_completion_on_whitespace_trigger()
	{
		string root = CreateTempDirectory("language-service-finally-completion");
		string source = Path.Combine(root, "main.camp");
		string text = """
			void dispose() {}

			export void main()
			{
				dispose();
				int other = 2;
			}
			""";
		string currentText = """
			void dispose() {}

			export void main()
			{
				dispose() finally 
				int other = 2; 
			}
			""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		IReadOnlyList<CampCompletionItem> finallyCompletions = symbols.GetCompletions(source, PositionAfter(currentText, "finally "), currentText, requireFinallyForWhitespaceTrigger: true);
		IReadOnlyList<CampCompletionItem> ordinarySpaceCompletions = symbols.GetCompletions(source, PositionAfter(currentText, "int other = "), currentText, requireFinallyForWhitespaceTrigger: true);
		IReadOnlyList<CampCompletionItem> manualSpaceCompletions = symbols.GetCompletions(source, PositionAfter(currentText, "int other = "), currentText);

		Assert.Contains(finallyCompletions, static item => item.Label == "delete" && item.Kind == CampSymbolKind.Keyword);
		Assert.Contains(finallyCompletions, static item => item.Label == "dispose" && item.Kind == CampSymbolKind.Function);
		Assert.Empty(ordinarySpaceCompletions);
		Assert.Contains(manualSpaceCompletions, static item => item.Label == "dispose" && item.Kind == CampSymbolKind.Function);
	}

	[Fact]
	public void Symbol_query_returns_override_snippets_after_override_keyword()
	{
		string root = CreateTempDirectory("language-service-override-completion");
		string source = Path.Combine(root, "main.camp");
		string text = """
			interface Ignored
			{
				void interfaceOnly();
			}

			virtual class Base
			{
				export virtual int compute(overload int value)
				{
					return 0;
				}
				export virtual int compute(overload string value)
				{
					return 0;
				}
				virtual void reset(int value)
				{
				}
				static void helper()
				{
				}
			}

			sealed class Derived: Base
			{
				override void reset(int value)
				{
				}
			}
			""";
		string currentText = """
			interface Ignored
			{
				void interfaceOnly();
			}

			virtual class Base
			{
				export virtual int compute(overload int value)
				{
					return 0;
				}
				export virtual int compute(overload string value)
				{
					return 0;
				}
				virtual void reset(int value)
				{
				}
				static void helper()
				{
				}
			}

			sealed class Derived: Base
			{
				override /*caret*/
			}
			""".Replace("/*caret*/", " ", StringComparison.Ordinal);
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		IReadOnlyList<CampCompletionItem> completions = symbols.GetCompletions(source, PositionAfter(currentText, "override "), currentText, requireFinallyForWhitespaceTrigger: true);
		string sealedText = currentText.Replace("override ", "sealed ", StringComparison.Ordinal);
		IReadOnlyList<CampCompletionItem> sealedCompletions = symbols.GetCompletions(source, PositionAfterLast(sealedText, "sealed "), sealedText, requireFinallyForWhitespaceTrigger: true);

		Assert.Equal(2, completions.Count);
		Assert.Equal(2, completions.Count(static item => item.Label == "compute"));
		Assert.Contains(completions, static item => item.Detail?.Contains("int value", StringComparison.Ordinal) == true);
		Assert.Contains(completions, static item => item.Detail?.Contains("string value", StringComparison.Ordinal) == true);
		CampCompletionItem compute = Assert.Single(completions, static item => item.Detail?.Contains("int value", StringComparison.Ordinal) == true);
		Assert.True(compute.IsSnippet);
		Assert.Contains("int compute(overload int value)", compute.InsertText, StringComparison.Ordinal);
		Assert.DoesNotContain("override", compute.InsertText, StringComparison.Ordinal);
		Assert.DoesNotContain("export", compute.InsertText, StringComparison.Ordinal);
		Assert.DoesNotContain("virtual", compute.InsertText, StringComparison.Ordinal);
		Assert.Contains("$0", compute.InsertText, StringComparison.Ordinal);
		CampCompletionItem sealedCompute = Assert.Single(sealedCompletions, static item => item.Detail?.Contains("int value", StringComparison.Ordinal) == true);
		Assert.Contains("int compute(overload int value)", sealedCompute.InsertText, StringComparison.Ordinal);
		Assert.DoesNotContain("sealed", sealedCompute.InsertText, StringComparison.Ordinal);
		Assert.DoesNotContain("export", sealedCompute.InsertText, StringComparison.Ordinal);
		Assert.DoesNotContain("virtual", sealedCompute.InsertText, StringComparison.Ordinal);
		Assert.DoesNotContain(completions, static item => item.Label is "interfaceOnly" or "helper" or "reset");
	}

	[Fact]
	public void Analysis_does_not_auto_include_standard_api_when_opening_standard_api_header()
	{
		string root = CreateTempDirectory("language-service-open-std-api");
		string runtimeRoot = Path.Combine(root, "runtime");
		string stdApi = Path.Combine(runtimeRoot, "cache", "lib", "std", "bin", "clang-macos-x64_static_DEBUG", "std_api.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(stdApi)!);
		File.WriteAllText(stdApi, """
			namespace Std;

			export newtype TimerHandle: nint;
			""");
		CompilerRequest request = RequestWithStd(runtimeRoot, Path.GetDirectoryName(stdApi)!, stdApi);

		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(request);

		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		Assert.DoesNotContain(snapshot.Diagnostics, static diagnostic => diagnostic.Message.Contains("TimerHandle", StringComparison.Ordinal));
	}

	[Fact]
	public void Analysis_uses_standard_sources_when_opening_standard_source_file()
	{
		string root = CreateTempDirectory("language-service-open-std-source");
		string runtimeRoot = Path.Combine(root, "runtime");
		string stdApi = Path.Combine(runtimeRoot, "cache", "lib", "std", "bin", "clang-macos-x64_static_DEBUG", "std_api.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(stdApi)!);
		File.WriteAllText(stdApi, """
			namespace Std;

			export newtype TimerHandle: nint;
			""");
		string stdSource = Path.Combine(runtimeRoot, "lib", "std", "src", "std_timing.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(stdSource)!);
		File.WriteAllText(stdSource, """
			namespace Std;

			export newtype TimerHandle: nint;
			""");
		CompilerRequest request = RequestWithStd(runtimeRoot, Path.GetDirectoryName(stdSource)!, stdSource);

		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(request);

		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		Assert.DoesNotContain(snapshot.Diagnostics, static diagnostic => diagnostic.Message.Contains("TimerHandle", StringComparison.Ordinal));
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

	[Fact]
	public void Symbol_query_returns_basic_semantic_completions()
	{
		string root = CreateTempDirectory("language-service-completion");
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
				void setValue(int value) => this.value = value;
			}

			int helper(int value) => value + 1;

			export int main()
			{
				Counter counter = default;
				int[] values = [1, 2, 3];
				int local = 1;
				counter.value = helper(local);
				nuint count = values.length;
				Mode mode = Mode.OPEN;
				return local;
			}
			""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		IReadOnlyList<CampCompletionItem> scopeCompletions = symbols.GetCompletions(source, PositionOf(text, "helper(local"));
		IReadOnlyList<CampCompletionItem> memberCompletions = symbols.GetCompletions(source, PositionAfter(text, "counter."));
		IReadOnlyList<CampCompletionItem> componentCompletions = symbols.GetCompletions(source, PositionAfter(text, "values."));
		IReadOnlyList<CampCompletionItem> enumCompletions = symbols.GetCompletions(source, PositionAfter(text, "Mode."));
		CampHover? componentHover = symbols.GetHover(source, PositionOf(text, "length;"));

		Assert.Contains(scopeCompletions, static item => item.Label == "local" && item.Kind == CampSymbolKind.Variable);
		Assert.Contains(scopeCompletions, static item => item.Label == "helper" && item.Kind == CampSymbolKind.Function);
		Assert.Contains(scopeCompletions, static item => item.Label == "return" && item.Kind == CampSymbolKind.Keyword);
		Assert.Contains(memberCompletions, static item => item.Label == "value" && item.Kind == CampSymbolKind.Field);
		Assert.Contains(memberCompletions, static item => item.Label == "getValue" && item.Kind == CampSymbolKind.Method);
		Assert.Contains(memberCompletions, static item => item.Label == "Value" && item.Kind == CampSymbolKind.Property && item.Detail == "Property: int");
		Assert.Contains(componentCompletions, static item => item.Label == "length" && item.Kind == CampSymbolKind.Component && item.Detail == "Component: nuint");
		Assert.Contains(componentCompletions, static item => item.Label == "elements" && item.Kind == CampSymbolKind.Component);
		Assert.NotNull(componentHover);
		Assert.Contains("**Component** `length`", componentHover!.Markdown, StringComparison.Ordinal);
		Assert.Contains(enumCompletions, static item => item.Label == "OPEN" && item.Kind == CampSymbolKind.EnumValue);
		Assert.Contains(enumCompletions, static item => item.Label == "CLOSED" && item.Kind == CampSymbolKind.EnumValue);
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

	static CompilerRequest RequestWithStd(string runtimeRoot, string workingDirectory, string source)
	{
		CompilerRequest request = new()
		{
			RuntimeRoot = runtimeRoot,
			WorkingDirectory = workingDirectory,
			TargetName = "clang-macos-x64"
		};
		request.Files.Add(Path.GetRelativePath(workingDirectory, source));
		return request;
	}

	static CampTextPosition PositionOf(string text, string marker)
	{
		int index = text.IndexOf(marker, StringComparison.Ordinal);
		if (index < 0 && marker.Contains('\n', StringComparison.Ordinal))
			index = text.IndexOf(marker.Replace("\n", "\r\n", StringComparison.Ordinal), StringComparison.Ordinal);
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

	static CampTextPosition PositionAfter(string text, string marker)
	{
		CampTextPosition position = PositionOf(text, marker);
		return new CampTextPosition(position.Line, position.Character + marker.Length);
	}

	static CampTextPosition PositionAfterLast(string text, string marker)
	{
		int index = text.LastIndexOf(marker, StringComparison.Ordinal);
		if (index < 0)
			throw new InvalidOperationException($"Marker '{marker}' was not found.");
		int line = 0;
		int character = 0;
		for (int i = 0; i < index + marker.Length; i++)
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

	static int[] ReferenceLines(IReadOnlyList<CampReference> references)
	{
		return references.Select(static reference => reference.Range.Start.Line).ToArray();
	}

	static string CreateTempDirectory(string name)
	{
		string directory = Path.Combine(Path.GetTempPath(), "camp-tests", name + "-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return directory;
	}
}
