using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class IteratorReceiverArgumentTests
{
	[Theory]
	[InlineData("Sink sink = default; return sink.size(\"abc\");")]
	[InlineData("return input.size(\"abc\");")]
	[InlineData("return input.size(text);")]
	[InlineData("return input.size(compact ? \":\" : \": \");")]
	[InlineData("return copy(input).size(\"abc\");")]
	public void Bound_iterator_receiver_context_preserves_following_string_length(string body)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype iter nuint Sink(char[] buffer);
			nuint size(Sink this, const char[] value) { return value.length; }
			Sink copy(Sink value) { return value; }
			nuint use(Sink input, string text, bool compact) { {{body}} }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module),
			static call => call.Target is MethodReferenceExpression method
				&& method.Candidates.Any(static function => function.Name == "size"));
		Assert.Equal(4, call.Arguments.Count);
		Assert.Contains(call.Arguments[3].Value?.ResolvedType, new[] { "nuint", "const nuint" });
	}
}
