using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ErasedOptionalDelegateArgumentTests
{
	[Theory]
	[InlineData("yes, 6")]
	[InlineData("index => true, 6")]
	[InlineData("index => index > offset, 6")]
	[InlineData("predicate, 6")]
	[InlineData("yes")]
	public void Expanded_delegate_arguments_preserve_optional_result_storage(string arguments)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			T? maybe<T: copyable>(delegate bool(int) look, int index = 6, sizeof(T)) => default;
			bool yes(int index) => true;
			void test() {
				int offset = 3;
				delegate bool(int) predicate = yes;
				int? hit = maybe<int>({{arguments}});
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "test")));
		Assert.Equal(5, call.Arguments.Count);
		Assert.Equal(ArgumentModifier.Out, call.Arguments[3].Modifier);
		Assert.IsType<SizeOfExpression>(call.Arguments[4].Value);
		Assert.Equal("int", call.Arguments[2].ResolvedType);
	}
}
