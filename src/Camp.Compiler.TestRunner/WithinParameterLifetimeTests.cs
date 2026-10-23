using Xunit;

namespace Camp.Compiler.Tests;

public sealed class WithinParameterLifetimeTests
{
	[Theory]
	[InlineData("within allocator")]
	[InlineData("within scoped allocator")]
	[InlineData("within scoped Allocator* allocator")]
	public void Scoped_within_forms_cannot_be_retained_by_receiver(string parameter)
	{
		var result = SemanticCompiler.CompileLowered("class Allocator { } class Holder { Allocator* kept; void keep(" + parameter + ") { this.kept = allocator; } }");
		Assert.Contains(result.Diagnostics, message => message.Contains("cannot store a scoped pointer-bearing value in storage tied to 'this'"));
	}

	[Theory]
	[InlineData("within unscoped allocator")]
	[InlineData("within escaped allocator")]
	public void Longer_lived_within_forms_remain_storable(string parameter)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("class Allocator { } class Holder { Allocator* kept; void keep(" + parameter + ") { this.kept = allocator; } }"));
	}

	[Theory]
	[InlineData("within unscoped(owner) allocator")]
	[InlineData("within scoped(owner) allocator")]
	[InlineData("within unscoped(owner) Allocator* allocator")]
	public void Bare_and_typed_within_parameters_accept_input_anchors(string parameter)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("class Allocator { } void use(const char[] owner, " + parameter + ") { }"));
	}

	[Fact]
	public void Bare_within_anchor_must_resolve()
	{
		var result = SemanticCompiler.CompileLowered("class Allocator { } void use(within unscoped(missing) allocator) { }");
		Assert.Contains(result.Diagnostics, message => message.Contains("Lifetime anchor 'missing' could not be resolved"));
	}
}
