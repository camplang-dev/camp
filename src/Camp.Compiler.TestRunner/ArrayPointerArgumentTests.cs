using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ArrayPointerArgumentTests
{
	[Theory]
	[InlineData("int", "const int", "&values")]
	[InlineData("int", "const int", "pointer")]
	[InlineData("const int", "const int", "&values")]
	[InlineData("const int", "const int", "pointer")]
	[InlineData("int", "int", "&values")]
	[InlineData("int", "int", "pointer")]
	public void Array_pointer_arguments_keep_one_address_per_component(string elementType, string parameterElementType, string argument)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			int lengthThrough({{parameterElementType}}[]* source) { return (int)(*source).length; }
			int read({{elementType}}[] values)
			{
				{{elementType}}[]* pointer = &values;
				return lengthThrough({{argument}});
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "read")),
			static call => call.Target is MethodReferenceExpression method && method.Candidates.Any(static candidate => candidate.Name == "lengthThrough"));
		Assert.Equal(2, call.Arguments.Count);
		foreach (ArgumentExpression component in call.Arguments)
		{
			if (argument == "&values")
			{
				UnaryExpression address = Assert.IsType<UnaryExpression>(component.Value);
				Assert.Equal(UnaryOperator.AddressOf, address.Operator);
				Assert.IsType<VariableReferenceExpression>(address.Operand);
			}
			else
				Assert.IsType<VariableReferenceExpression>(component.Value);
		}
	}
}
