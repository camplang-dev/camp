using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class OptionalPayloadReturnTests
{
	[Theory]
	[InlineData("P b = { 1 }; return b;")]
	[InlineData("P b = default; return b;")]
	[InlineData("return payload();")]
	[InlineData("P b = { 1 }; return (b);")]
	[InlineData("Holder h = default; return h.Item;")]
	public void Struct_payload_return_initializes_the_optional_presence(string body)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			struct P { int x; }
			class Holder { P getItem() { P p = { 3 }; return p; } }
			P payload() { P p = { 2 }; return p; }
			P? make() { {{body}} }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition make = SemanticCompiler.Function(compilation, "make");
		ParameterDefinition presence = Assert.Single(make.Parameters, static parameter => parameter.IsExpandedReturnComponent);
		AssignmentExpression assignment = Assert.Single(SemanticCompiler.Descendants<AssignmentExpression>(make),
			assignment => assignment.Target is VariableReferenceExpression target && ReferenceEquals(target.Variable, presence));
		Assert.True(Assert.IsType<LiteralExpression>(assignment.Value).Value is true);
	}

	[Fact]
	public void Existing_optional_return_preserves_the_presence_component()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			struct P { int x; }
			P? copy(P? value) { return value; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition copy = SemanticCompiler.Function(compilation, "copy");
		AssignmentExpression assignment = Assert.Single(SemanticCompiler.Descendants<AssignmentExpression>(copy));
		VariableReferenceExpression source = Assert.IsType<VariableReferenceExpression>(assignment.Value);
		Assert.Equal("value_specified", Assert.IsType<ParameterDefinition>(source.Variable).Name);
	}
}
