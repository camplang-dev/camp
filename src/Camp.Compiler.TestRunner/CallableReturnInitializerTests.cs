using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CallableReturnInitializerTests
{
	[Theory]
	[InlineData("fn", "Handler")]
	[InlineData("delegate", "Handler")]
	[InlineData("fn", "int[]")]
	[InlineData("delegate", "int[]")]
	[InlineData("fn", "int?")]
	[InlineData("delegate", "int?")]
	public void Expanded_local_initializer_invokes_callable_once_with_one_result_component(string kind, string resultType)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype delegate int Handler(int value);
			void use({{kind}} {{resultType}}() choose) { {{resultType}} result = choose(); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module),
			static call => call.Target is VariableReferenceExpression { Variable: ParameterDefinition { Name: "choose" } });
		Assert.Equal(kind == "fn" ? 1 : 2, call.Arguments.Count);
	}
}
