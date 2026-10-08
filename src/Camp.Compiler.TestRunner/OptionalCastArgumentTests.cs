using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class OptionalCastArgumentTests
{
	[Theory]
	[InlineData("int", "(int?)7")]
	[InlineData("long", "(long?)7")]
	[InlineData("int", "(int?)make()")]
	[InlineData("int", "(int?)empty")]
	[InlineData("int?", "(int??)7")]
	public void Cast_arguments_keep_optional_payload_and_presence_storage(string payload, string argument)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			int make() => 7;
			int read(in void<{{payload}}>? value) => value.specified ? 1 : 0;
			void use() { int? empty = default; int result = read({{argument}}); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "use")),
			call => call.Arguments.Count == 2);
		Assert.Equal("bool", call.Arguments[1].Value?.ResolvedType);
		Assert.IsType<VariableReferenceExpression>(call.Arguments[0].Value);
		Assert.IsType<VariableReferenceExpression>(call.Arguments[1].Value);
	}
}
