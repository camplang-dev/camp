using Xunit;

namespace Camp.Compiler.Tests;

public sealed class GenericMemberLifetimeTests
{
	[Fact]
	public void Retained_generic_pointer_keeps_the_payload_capability_identity()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("extern escaped void* malloc(nuint size); class Generic<T> { T* value; Generic(unscoped T* value, sizeof(T)) { this.value = value; } }"));
	}
	[Theory]
	[InlineData("void use() { int item = 0; Generic<int> local = default; local.set(&item); }")]
	[InlineData("void use() { int item = 0; Generic<int> local = default; { local.set(&item); } }")]
	public void Local_receiver_accepts_inputs_from_its_scope_or_an_outer_scope(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("class Generic<T> { T* value; void set(unscoped T* value) { this.value = value; } } " + source));
	}

	[Fact]
	public void Outer_local_receiver_rejects_an_inner_local_input()
	{
		var result = SemanticCompiler.CompileLowered("class Generic<T> { T* value; void set(unscoped T* value) { this.value = value; } } void use() { Generic<int> local = default; { int item = 0; local.set(&item); } }");
		Assert.Contains(result.Diagnostics, message => message.Contains("Argument cannot satisfy parameter lifetime"));
	}
	[Theory]
	[InlineData("void set(Source* other) { this.source = other; }")]
	[InlineData("Generic(Source* other) { this.source = other; }")]
	public void Generic_members_reject_known_scoped_pointer_retention(string member)
	{
		var result = SemanticCompiler.CompileLowered("interface Source { int read(); } class Generic<T> { Source* source; " + member + " }");
		Assert.Contains(result.Diagnostics, message => message.Contains("cannot store a scoped pointer-bearing value in storage tied to 'this'"));
	}

	[Theory]
	[InlineData("void set(unscoped Source* other) { this.source = other; }")]
	[InlineData("void set(escaped Source* other) { this.source = other; }")]
	public void Generic_members_keep_valid_retention_contracts(string member)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("interface Source { int read(); } class Generic<T> { Source* source; " + member + " }"));
	}
}
