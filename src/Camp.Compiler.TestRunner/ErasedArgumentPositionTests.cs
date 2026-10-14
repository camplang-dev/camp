using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ErasedArgumentPositionTests
{
	[Theory]
	[InlineData("int[]", false)]
	[InlineData("int[]", true)]
	[InlineData("int?", false)]
	[InlineData("int?", true)]
	[InlineData("delegate int()", false)]
	[InlineData("delegate int()", true)]
	public void Erased_references_do_not_consume_following_size_capabilities(string type, bool reversed)
	{
		string parameters = reversed ? "in T value, out T target" : "out T target, in T value";
		string arguments = reversed ? "source, out destination" : "out destination, source";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			void store<T>({{parameters}}, sizeof(T)) { target = value; }
			void use({{type}} source, out {{type}} destination) { store<{{type}}>({{arguments}}); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module));
		Assert.Equal(3, call.Arguments.Count);
		Assert.IsType<SizeOfExpression>(call.Arguments[2].Value);
		Assert.Equal("nuint", call.Arguments[2].ResolvedType);
	}
}
