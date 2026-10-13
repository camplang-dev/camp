using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class InterfaceErasedResultTests
{
	[Theory]
	[InlineData("int", "int step", "5", 2)]
	[InlineData("int", "", "", 1)]
	[InlineData("int", "int[] steps", "steps", 3)]
	[InlineData("int?", "int step", "5", 2)]
	public void Interface_result_storage_follows_authored_arguments(string result, string parameters, string arguments, int position)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			interface Source { void<{{result}}> next({{parameters}}); }
			void use(Source* source, int[] steps) { {{result}} value = source.next({{arguments}}); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "use")));
		Assert.Equal(position, call.Arguments.FindIndex(argument => argument.Modifier == ArgumentModifier.Out));
		Assert.Equal(position + 1, call.Arguments.Count);
	}
}
