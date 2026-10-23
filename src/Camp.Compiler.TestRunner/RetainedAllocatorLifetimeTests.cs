using Xunit;

namespace Camp.Compiler.Tests;

public sealed class RetainedAllocatorLifetimeTests
{
	const string Declarations = """
		extern escaped void* malloc(nuint size);
		extern void free(void* memory);
		class Allocator {
			escaped void* alloc(nuint size) => malloc(size);
			void free(void* memory) { }
		}
		class Owner {
			int value;
			Owner(within this.allocator) { this.value = 1; }
			~Owner() { }
		}
		""";

	[Theory]
	[InlineData("Owner* make(Allocator* arena) { return within (arena) new Owner(); }")]
	[InlineData("Owner* make(Allocator* arena) { return within (arena) Owner.create(); }")]
	[InlineData("void use() { Owner result; { Allocator arena = default; result = within (&arena) Owner(); } }")]
	public void Retained_allocator_must_outlive_constructed_storage(string source)
	{
		var result = SemanticCompiler.CompileLowered(Declarations + source);
		Assert.Contains(result.Diagnostics, message => message.Contains("Heap construction cannot retain") || message.Contains("outlives its lifetime anchor"));
	}

	[Theory]
	[InlineData("Owner* make(escaped Allocator* arena) { return within (arena) new Owner(); }")]
	[InlineData("Owner* make(escaped Allocator* arena) { return within (arena) Owner.create(); }")]
	[InlineData("void use(Allocator* arena) { Owner local = within (arena) Owner(); }")]
	[InlineData("void use() { Allocator arena = default; Owner local = within (&arena) Owner(); }")]
	public void Compatible_allocator_lifetimes_remain_valid(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(Declarations + source));
	}
}
