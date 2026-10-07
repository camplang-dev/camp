using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class WithinErasedResultTests
{
	[Theory]
	[InlineData("allocator")]
	[InlineData("default")]
	public void Optional_erased_results_keep_storage_and_allocator_slots(string context)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(WithinAllocationPolicy.Explicit, ("within_result.camp", $$"""
			struct Allocator {}
			Allocator* select(Allocator* allocator) => allocator;
			T? keep<T: copyable>(in T value, within allocator, sizeof(T)) => value;
			void use(Allocator* allocator) { int? result = within ({{context}}) keep<int>(5); }
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "use")));
		Assert.Equal(4, call.Arguments.Count);
		Assert.Equal(ArgumentModifier.Out, call.Arguments[1].Modifier);
		Assert.IsType<SizeOfExpression>(call.Arguments[2].Value);
	}
}
