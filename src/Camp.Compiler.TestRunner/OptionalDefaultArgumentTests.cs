using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class OptionalDefaultArgumentTests
{
	[Theory]
	[InlineData("int?")]
	[InlineData("int*?")]
	[InlineData("int[]?")]
	[InlineData("delegate int()?")]
	[InlineData("Maybe?")]
	public void Null_is_rejected_for_optional_declarations_assignments_arguments_and_results(string type)
	{
		var compilation = SemanticCompiler.CompileLowered($$"""
			alias Maybe = int;
			void take({{type}} value) { }
			{{type}} give() { return null; }
			void test() { {{type}} value = null; value = null; take(null); }
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Equal(4, compilation.AnalysisDiagnostics.Distinct().Count());
		Assert.All(compilation.AnalysisDiagnostics, diagnostic =>
		{
			Assert.Contains("cannot convert '#NULL'", diagnostic.Message);
			Assert.Equal("null", Assert.IsType<TokenRange>(diagnostic.Range).Value);
		});
	}

	[Fact]
	public void Default_optionals_and_null_pointer_destinations_remain_valid()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			int? give() => default;
			void take(int? value) { }
			void test() {
				int? value = default; value = default; take(default);
				int* pointer = null; int?* distributed = null;
				fn int() callback = null; string text = null;
			}
			"""));
	}

	[Theory]
	[InlineData("7", true)]
	[InlineData("-3", true)]
	[InlineData("default", false)]
	public void Omitted_optional_default_passes_its_presence_component(string value, bool specified)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			int opt(int? value = {{value}}) { return value.specified ? value.value : -1; }
			int call() { return opt(); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module));
		Assert.Equal(2, call.Arguments.Count);
		Expression presence = call.Arguments[1].Value!;
		Assert.Equal("bool", presence.ResolvedType);
		if (specified)
			Assert.True(Assert.IsType<LiteralExpression>(presence).Value is true);
		else
			Assert.IsType<DefaultExpression>(presence);
	}

	[Theory]
	[InlineData("opt(2)")]
	[InlineData("opt(tail: 9)")]
	[InlineData("opt(prefix: 4, tail: 9)")]
	public void Optional_default_keeps_following_argument_positions(string invocation)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			int opt(int prefix = 1, int? value = 7, int tail = 5) { return prefix + value.value + tail; }
			int call() { return {{invocation}}; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module));
		Assert.Equal(4, call.Arguments.Count);
		Assert.True(Assert.IsType<LiteralExpression>(call.Arguments[2].Value).Value is true);
	}
}
