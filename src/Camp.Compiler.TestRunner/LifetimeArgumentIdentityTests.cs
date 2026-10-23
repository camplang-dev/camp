using Xunit;

namespace Camp.Compiler.Tests;

public sealed class LifetimeArgumentIdentityTests
{
	const string Store = "void store(const char[] owner, unscoped(owner) const char[] view) { } ";

	[Theory]
	[InlineData("void use(const char[] owner, const char[] view) { store(owner, view); }")]
	[InlineData("void use(const char[] alpha, const char[] beta) { store(alpha, beta); }")]
	[InlineData("void use(const char[] owner, const char[] view) { store(owner, (view)); }")]
	public void Parameter_names_do_not_prove_argument_lifetimes(string source)
	{
		var result = SemanticCompiler.CompileLowered(Store + source);
		Assert.Contains(result.Diagnostics, message => message.Contains("Argument cannot satisfy parameter lifetime 'unscoped(owner)'"));
	}

	[Theory]
	[InlineData("void use(const char[] owner) { store(owner, owner); }")]
	[InlineData("void use(const char[] owner) { auto copy = owner; store(owner, copy); }")]
	[InlineData("void use(const char[] owner, escaped const char[] view) { store(owner, view); }")]
	[InlineData("void use(const char[] owner, unscoped(owner) const char[] view) { store(owner, view); }")]
	public void Actual_argument_relations_satisfy_the_contract(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(Store + source));
	}
}
