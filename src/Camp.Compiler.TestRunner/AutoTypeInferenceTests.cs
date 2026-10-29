using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class AutoTypeInferenceTests
{
	[Theory]
	[InlineData("tn(default)", "tn<T>", "tn")]
	[InlineData("tn((default))", "tn<T>", "tn")]
	[InlineData("two(default, default)", "two<T>", "two")]
	public void Default_without_a_concrete_inference_source_fails_at_the_called_name(string invocation, string method, string name)
	{
		var compilation = SemanticCompiler.CompileLowered($$"""
			void tn<T>(in T value, sizeof(T), typenameof(T)) { }
			void two<T>(in T a, in T b, sizeof(T)) { }
			void test() { {{invocation}}; }
			""");
		var diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Contains($"The type arguments for method '{method}' cannot be inferred", diagnostic.Message);
		Assert.Equal(name, Assert.IsType<TokenRange>(diagnostic.Range).Value);
	}

	[Theory]
	[InlineData("tn<int>(default)")]
	[InlineData("two(5, default)")]
	[InlineData("two(default, 5)")]
	[InlineData("two<int>(default, default)")]
	public void Default_uses_an_explicit_or_independently_inferred_type(string invocation)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			void tn<T>(in T value, sizeof(T), typenameof(T)) { }
			void two<T>(in T a, in T b, sizeof(T)) { }
			void test() { {{invocation}}; }
			"""));
	}

	[Theory]
	[InlineData("l, i")]
	[InlineData("i, l")]
	[InlineData("l, 2")]
	[InlineData("2, l")]
	[InlineData("a: i, b: l")]
	[InlineData("b: l, a: i")]
	public void Generic_value_arguments_infer_a_common_type(string arguments)
	{
		var compilation = SemanticCompiler.CompileLowered($$"""
			T two<T>(in T a, in T b, sizeof(T)) => a;
			void test(int i, long l) { long result = two({{arguments}}); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		var call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "test")));
		Assert.Equal("long", call.ResolvedType);
	}

	[Theory]
	[InlineData("u, i")]
	[InlineData("i, u")]
	public void Incompatible_generic_value_arguments_fail_inference_at_the_call_name(string arguments)
	{
		var compilation = SemanticCompiler.CompileLowered($$"""
			void two<T>(in T a, in T b, sizeof(T)) { }
			void test(int i, uint u) { two({{arguments}}); }
			""");
		var diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Contains("The type arguments for method 'two<T>' cannot be inferred", diagnostic.Message);
		Assert.Equal("two", Assert.IsType<TokenRange>(diagnostic.Range).Value);
	}

	[Theory]
	[InlineData("c ? u : 0", "uint")]
	[InlineData("c ? 0 : u", "uint")]
	[InlineData("[u, 2]", "uint[]")]
	[InlineData("[2, u]", "uint[]")]
	[InlineData("c ? i : l", "long")]
	[InlineData("c ? l : i", "long")]
	[InlineData("[i, u, l]", "long[]")]
	[InlineData("[u, i, l]", "long[]")]
	[InlineData("[l, u, i]", "long[]")]
	[InlineData("[i, d]", "double[]")]
	[InlineData("[d, i]", "double[]")]
	public void Common_expression_type_is_independent_of_operand_order(string expression, string expected)
	{
		var compilation = SemanticCompiler.CompileDeclarations($"void test(uint u, int i, long l, double d, bool c) {{ auto value = {expression}; }}");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		string? inferred = null;
		var lowered = BindableNodeLowerer.Lower(compilation.Compilation.DeclarationExpansion!, (phase, action) =>
		{
			action();
			if (phase == "method body analysis")
				inferred = Assert.Single(SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "test"))).Target.ResolvedType;
		});
		Assert.Empty(lowered.Diagnostics);
		Assert.Equal(expected, inferred);
	}

	[Theory]
	[InlineData("c ? u : i")]
	[InlineData("c ? i : u")]
	[InlineData("[i, u]")]
	[InlineData("[u, i]")]
	public void Incompatible_common_operands_report_at_the_operand(string expression)
	{
		var compilation = SemanticCompiler.CompileLowered($"void test(uint u, int i, bool c) {{ auto value = {expression}; }}");
		var diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Contains("no common type", diagnostic.Message);
		Assert.NotNull(diagnostic.Range);
	}

	[Fact]
	public void Erased_generic_inference_preserves_source_types_and_erased_optional_storage()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileDeclarations("""
			extern escaped void* malloc(nuint size);
			extern void free(void* value);
			class Items<T> {
				T[] items;
				Items(sizeof(T)) { }
				T getItem(nuint index) => this.items[index];
				T[] take(nuint start, nuint count) => this.items[start..start + count];
				bool tryGetItem(int index, out T result) { result = this.items[(nuint)index]; return true; }
			}
			void test(Items<int?>* items) {
				auto first = items.Item[0];
				auto section = items.take(0, 2);
				auto get = items.getItem;
				auto lookup = items.tryGetItem;
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		System.Collections.Generic.Dictionary<string, string?> inferred = [];
		LoweringResult lowered = BindableNodeLowerer.Lower(compilation.Compilation.DeclarationExpansion!, (phase, action) =>
		{
			action();
			if (phase == "method body analysis")
				foreach (DeclarationStatement declaration in SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "test")))
					inferred[declaration.Target.Names.Single()] = declaration.Target.ResolvedType;
		});
		Assert.Empty(lowered.Diagnostics);
		Assert.Equal("int?", inferred["first"]);
		Assert.Equal("void<int?>[]", inferred["section"]);
		Assert.Equal("delegate void<int?>(nuint)", inferred["get"]);
		Assert.Equal("delegate bool(int, out void<int?>)", inferred["lookup"]);
	}

	[Theory]
	[InlineData("auto value;", "auto")]
	[InlineData("auto value = null;", "null")]
	[InlineData("auto value = default;", "default")]
	[InlineData("auto value = [];", "[")]
	[InlineData("auto value = ([]);", "(")]
	[InlineData("auto value = (null);", "(")]
	[InlineData("auto value = ((default));", "(")]
	[InlineData("auto value = true ? null : null;", "true")]
	[InlineData("auto value = true ? default : default;", "true")]
	public void Untyped_auto_initializers_report_a_source_located_inference_error(string declaration, string rangeText)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			void test()
			{
				{{declaration}}
			}
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		AnalysisDiagnostic diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Equal(DiagnosticCodes.AutoCannotInferType, diagnostic.Code);
		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Equal("The type of this expression cannot be inferred; specify a type.", diagnostic.Message);
		TokenRange range = Assert.IsType<TokenRange>(diagnostic.Range);
		Assert.Equal(3, range.StartLineNumber);
		Assert.Equal(rangeText, range.Value);
	}

	[Theory]
	[InlineData("int value; int* pointer = null; int reset = default;")]
	[InlineData("auto value = 42; auto flag = true; auto text = \"Camp\";")]
	[InlineData("int original = default; auto value = original;")]
	[InlineData("auto pointer = (int*)null;")]
	[InlineData("auto value = (42);")]
	[InlineData("int* pointer = null; auto value = true ? pointer : null;")]
	[InlineData("auto values = [1, 2, 3];")]
	[InlineData("int[] values = []; int* pointer = [];")]
	public void Concrete_inference_and_explicit_targets_remain_valid(string declarations)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($"void test() {{ {declarations} }}"));
	}

	[Theory]
	[InlineData("auto value = missingName;", "Symbol 'missingName' could not be found.")]
	[InlineData("auto value = doNothing();", "Auto declaration cannot infer a type from a void expression.")]
	[InlineData("auto value = { 1, 2 };", "Initializer expression requires a target type.")]
	public void Existing_initializer_errors_do_not_gain_a_secondary_inference_error(string declaration, string expected)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			void doNothing() { }
			void test() { {{declaration}} }
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
		Assert.All(compilation.AnalysisDiagnostics, diagnostic =>
		{
			Assert.Contains(expected, diagnostic.Message);
			Assert.NotEqual(DiagnosticCodes.AutoCannotInferType, diagnostic.Code);
		});
	}

	[Fact]
	public void An_uninferable_local_is_registered_as_an_error_type_for_later_uses()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			void test()
			{
				auto value = null;
				value = null;
			}
			""");
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
		Assert.All(compilation.AnalysisDiagnostics, diagnostic => Assert.Equal(DiagnosticCodes.AutoCannotInferType, diagnostic.Code));
	}
}
