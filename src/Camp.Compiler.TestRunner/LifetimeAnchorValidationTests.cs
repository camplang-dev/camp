using Xunit;

namespace Camp.Compiler.Tests;

public sealed class LifetimeAnchorValidationTests
{
	[Theory]
	[InlineData("void use(unscoped(second) const char[] first, unscoped(first) const char[] second) { }")]
	[InlineData("void use(unscoped(value) const char[] value) { }")]
	[InlineData("void use(const char[] owner, thrown unscoped(owner) int* error) { }")]
	[InlineData("void use(out const char[] owner, unscoped(owner) const char[] view) { }")]
	[InlineData("void use(within Allocator* arena, unscoped(arena) const char[] view) { }")]
	public void Invalid_relations_are_rejected_at_declaration(string source)
	{
		var result = SemanticCompiler.CompileLowered("class Allocator { } " + source);
		Assert.Contains(result.Diagnostics, message => message.Contains("Lifetime anchor") || message.Contains("Circular lifetime"));
	}

	[Theory]
	[InlineData("void use(const char[] owner, within unscoped(owner) Allocator* arena) { }")]
	[InlineData("void use(const char[] owner, out unscoped(owner) const char[] result) { result = default; }")]
	[InlineData("void use(const char[] owner, scoped(owner) const char[] view) { }")]
	public void Input_anchors_remain_valid(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("class Allocator { } " + source));
	}
}
