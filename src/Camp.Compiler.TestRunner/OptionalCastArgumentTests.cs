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
	public void Cast_arguments_expand_to_optional_payload_and_presence(string payload, string argument)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			int make() => 7;
			int read(in {{payload}}? value) => value.specified ? 1 : 0;
			void use() { int? empty = default; int result = read({{argument}}); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "use")),
			call => call.Arguments.Count == 2);
		Assert.Equal("bool", call.Arguments[1].Value?.ResolvedType);
		Assert.Equal(payload, call.Arguments[0].Value?.ResolvedType);
	}
}
