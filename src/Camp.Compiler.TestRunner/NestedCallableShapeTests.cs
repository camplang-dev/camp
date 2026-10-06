using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class NestedCallableShapeTests
{
	[Theory]
	[InlineData("fn fn int(int)(bool)", "fn int(int)", new[] { "bool" })]
	[InlineData("fn fn int(int)()", "fn int(int)", new string[] { })]
	[InlineData("delegate delegate int(int)(bool)", "delegate int(int)", new[] { "bool" })]
	[InlineData("fn fn int(fn int(int))(fn int(int), int)", "fn int(fn int(int))", new[] { "fn int(int)", "int" })]
	[InlineData("fn int(fn int(int))", "int", new[] { "fn int(int)" })]
	[InlineData("fn int(int)", "int", new[] { "int" })]
	public void Callable_result_signature_is_separate_from_outer_parameters(string type, string returnType, string[] parameters)
	{
		Assert.True(CallableShapeService.TryParseCallableShape(type, out CallableShape shape));
		Assert.Equal(returnType, shape.ReturnType);
		Assert.Equal(parameters, shape.Parameters);
	}

	[Fact]
	public void Nested_result_specs_do_not_replace_outer_specs()
	{
		Assert.True(CallableShapeService.TryParseCallableShape("fn _cdecl fn _stdcall int(int)(bool)", out CallableShape shape));
		Assert.Equal("_cdecl", shape.Spec);
		Assert.Equal("fn _stdcall int(int)", shape.ReturnType);
		Assert.Equal(new[] { "bool" }, shape.Parameters);
	}

	[Theory]
	[InlineData("Handler")]
	[InlineData("int?")]
	[InlineData("int[]")]
	public void Direct_function_argument_with_expanded_result_supplies_delegate_context(string resultType)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype delegate int Handler(int value);
			{{resultType}} make() { return default; }
			int use(delegate {{resultType}}() choose) { {{resultType}} result = choose(); return 0; }
			int check() { return use(make); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module),
			static call => call.Target is MethodReferenceExpression method
				&& method.Candidates.Any(static function => function.Name == "use"));
		Assert.Equal(2, call.Arguments.Count);
		Assert.Equal(LiteralKind.Null, Assert.IsType<LiteralExpression>(call.Arguments[1].Value).Kind);
	}
}
