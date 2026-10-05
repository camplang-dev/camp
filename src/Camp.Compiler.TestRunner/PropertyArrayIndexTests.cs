using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class PropertyArrayIndexTests
{
	[Theory]
	[InlineData("return h.Items[0];")]
	[InlineData("return ++h.Items[0];")]
	[InlineData("return --h.Items[0];")]
	[InlineData("return h.Items[0]++;")]
	[InlineData("return h.Items[0]--;")]
	[InlineData("h.Items[0] = 2; return 0;")]
	[InlineData("h.Items[0] += 2; return 0;")]
	[InlineData("return (h.Items)[0];")]
	[InlineData("return h.Items[^1];")]
	public void Array_getter_is_called_without_the_element_index(string body)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			class Holder { int[] items; int[] getItems() => this.items; }
			int read(Holder* h) { {{body}} }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module),
			static call => call.Target is MethodReferenceExpression method
				&& method.Candidates.Any(static function => function.Name == "getItems"));
		// The receiver and the expanded return's length output are the only arguments.
		Assert.Equal(2, call.Arguments.Count);
		Assert.Contains(SemanticCompiler.Descendants<IndexExpression>(compilation.Module),
			static index => index.ResolvedType == "int");
	}
}
