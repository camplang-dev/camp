using System;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class WithinArrayInitializerTests
{
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
