using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ErasedPointerReturnTests
{
	[Theory]
	[InlineData("int?", false)]
	[InlineData("int?", true)]
	[InlineData("int[]", false)]
	[InlineData("int[]", true)]
	[InlineData("int[]?", true)]
	[InlineData("delegate int()", true)]
	public void Returned_erased_pointers_address_every_storage_component(string payload, bool expressionBody)
	{
		string body = expressionBody ? "=> (slot);" : "{ return slot; }";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			{{payload}}* view(void<{{payload}}>* slot) {{body}}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition function = SemanticCompiler.Function(compilation, "view");
		AssignmentExpression[] assignments = SemanticCompiler.Descendants<AssignmentExpression>(function).ToArray();
		Assert.NotEmpty(assignments);
		Assert.All(assignments, assignment => Assert.IsType<UnaryExpression>(assignment.Value));
		Assert.All(assignments, assignment => Assert.Equal(UnaryOperator.AddressOf, ((UnaryExpression)assignment.Value!).Operator));
	}
}
