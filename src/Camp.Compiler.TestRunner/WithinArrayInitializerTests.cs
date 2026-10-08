using System;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class WithinArrayInitializerTests
{
	[Theory]
	[InlineData("allocator", true)]
	[InlineData("default", true)]
	[InlineData("select(allocator)", true)]
	[InlineData("allocator", false)]
	public void Within_array_result_initializers_keep_call_and_length_output(string context, bool generic)
	{
		string function = generic ? "T[] echo<T: copyable>(T[] values, within allocator, sizeof(T)) => values;"
			: "int[] echo(int[] values, within allocator) => values;";
		string invocation = generic ? "echo<int>(values)" : "echo(values)";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(WithinAllocationPolicy.Explicit, ("within_array.camp", $$"""
			struct Allocator {}
			Allocator* select(Allocator* allocator) => allocator;
			{{function}}
			void use(Allocator* allocator, int[] values) { int[] result = within ({{context}}) {{invocation}}; }
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "use")),
			call => call.Arguments.Count == (generic ? 5 : 4));
		Assert.Equal(ArgumentModifier.Out, call.Arguments[^1].Modifier);
		if (generic) Assert.IsType<SizeOfExpression>(call.Arguments[2].Value);
	}

	[Theory]
	[InlineData("within (default) { char[] text = create(); }")]
	[InlineData("within (default) { if (true) { char[] text = create(); } }")]
	[InlineData("within (allocator) { char[] text = create(); }")]
	[InlineData("within (allocator) { within (default) { char[] text = create(); } char[] more = create(); }")]
	[InlineData("within (default) { within (allocator) { char[] text = create(); } char[] more = create(); }")]
	public void Expanded_initializers_inherit_enclosing_allocator_scopes(string body)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(WithinAllocationPolicy.Explicit, ("within_initializer.camp", $$"""
			extern escaped void* malloc(nuint size);
			extern void free(void* pointer);
			struct Allocator { escaped void* alloc(nuint size) => malloc(size); }
			escaped char[] create(within allocator) { return new char[3]; }
			void read(Allocator* allocator) { {{body}} }
			""")));
	}

	[Theory]
	[InlineData("{ char[] text = create(); }")]
	[InlineData("within (default) { char[] text = create(); } char[] outside = create();")]
	[InlineData("within (allocator) { char[] text = create(); } char[] outside = create();")]
	public void Allocator_scope_does_not_leak_to_later_initializers(string body)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(WithinAllocationPolicy.Explicit, ("within_initializer.camp", $$"""
			extern escaped void* malloc(nuint size);
			extern void free(void* pointer);
			struct Allocator { escaped void* alloc(nuint size) => malloc(size); }
			escaped char[] create(within allocator) { return new char[3]; }
			void read(Allocator* allocator) { {{body}} }
			"""));
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Contains(compilation.AnalysisDiagnostics, static diagnostic => diagnostic.Message.Contains("Call requires a within context", StringComparison.Ordinal));
	}
}
