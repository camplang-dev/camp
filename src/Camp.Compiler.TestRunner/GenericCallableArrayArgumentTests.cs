using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class GenericCallableArrayArgumentTests
{
	[Theory]
	[InlineData("fn", false)]
	[InlineData("fn", true)]
	[InlineData("delegate", false)]
	[InlineData("delegate", true)]
	[InlineData("once", false)]
	[InlineData("once", true)]
	public void Generic_array_arguments_keep_pointer_and_length_slots(string kind, bool slice)
	{
		string value = slice ? "values[1..]" : "values";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			int across<T>(T[] values, {{kind}} int(T[]) look, sizeof(T)) => look({{value}});
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "across")));
		Assert.Equal(kind == "fn" ? 2 : 3, call.Arguments.Count);
		Assert.Equal("nuint", call.Arguments[^1].ResolvedType);
	}
}
