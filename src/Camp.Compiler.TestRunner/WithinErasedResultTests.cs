using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class WithinErasedResultTests
{
	[Theory]
	[InlineData("int[]", "allocator")]
	[InlineData("int[]", "default")]
	[InlineData("int[]", "select(allocator)")]
	[InlineData("int?", "allocator")]
	[InlineData("delegate int()", "allocator")]
	public void Expanded_erased_results_emit_a_call_with_whole_result_storage(string type, string context)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(WithinAllocationPolicy.Explicit, ("within_result.camp", $$"""
			struct Allocator {}
			Allocator* select(Allocator* allocator) => allocator;
			T keep<T>(in T value, within allocator, sizeof(T)) => value;
			void use(Allocator* allocator, {{type}} source) { {{type}} result = within ({{context}}) keep<{{type}}>(source); }
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "use")), call => call.Arguments.Count == 4);
		Assert.Equal(4, call.Arguments.Count);
		Assert.Equal(ArgumentModifier.Out, call.Arguments[1].Modifier);
		Assert.IsType<SizeOfExpression>(call.Arguments[2].Value);
	}
}
