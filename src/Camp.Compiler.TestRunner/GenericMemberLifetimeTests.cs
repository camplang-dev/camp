using Xunit;

namespace Camp.Compiler.Tests;

public sealed class GenericMemberLifetimeTests
{
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
