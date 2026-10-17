using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class StringDefaultArgumentTests
{
	[Theory]
	[InlineData("measure(text, 5)", 5)]
	[InlineData("measure(text)", 1)]
	[InlineData("measure(text, bonus: 5)", 5)]
	[InlineData("measure(bonus: 5, text: text)", 5)]
	public void String_conversion_preserves_default_and_explicit_arguments(string invocation, int bonus)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			extern nuint getLength(string this);
			int measure(const char[] text, int bonus = 1) => (int)text.length + bonus;
			int test(string text) => {{invocation}};
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "test")));
		Assert.Equal(2, call.Arguments.Count);
		Assert.Equal(bonus.ToString(), Assert.IsType<LiteralExpression>(call.Arguments[1].Value).Value?.ToString());
	}
}
