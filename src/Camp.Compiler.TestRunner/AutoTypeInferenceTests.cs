using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class AutoTypeInferenceTests
{
	[Theory]
	[InlineData("auto value;", "auto")]
	[InlineData("auto value = null;", "null")]
	[InlineData("auto value = default;", "default")]
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
