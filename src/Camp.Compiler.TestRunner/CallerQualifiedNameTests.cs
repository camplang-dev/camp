using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CallerQualifiedNameTests
{
	[Theory]
	[InlineData("namespace Outer { string inside() => who(); }", "Outer::inside")]
	[InlineData("namespace Outer { struct Inner { static string inside() => who(); } }", "Outer::Inner.inside")]
	[InlineData("namespace Outer; string inside() => who();", "Outer::inside")]
	[InlineData("namespace Outer; namespace global { string inside() => who(); }", "inside")]
	public void Qualified_name_uses_the_declarations_namespace(string caller, string expected)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("string who(string name = caller(qualifiedname)) => name;\n" + caller);
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module));
		Assert.Equal(expected, Assert.IsType<LiteralExpression>(Assert.Single(call.Arguments).Value).Value);
	}
}
