using Xunit;

namespace Camp.Compiler.Tests;

public sealed class BoundMethodLifetimeTests
{
	const string Declarations = "struct Counter { int n; int get() => this.n; } void take(escaped delegate int() callback) { } ";

	[Theory]
	[InlineData("void use() { Counter c = default; take(c.get); }")]
	[InlineData("void use(Counter* counter) { take(counter.get); }")]
	[InlineData("escaped delegate int() use(Counter* counter) { return counter.get; }")]
	[InlineData("void use() { Counter c = default; escaped delegate int() callback = c.get; }")]
	[InlineData("void use(Counter* counter) { auto callback = counter.get; take(callback); }")]
	public void Bound_context_cannot_escape_its_receiver(string source)
	{
		var result = SemanticCompiler.CompileLowered(Declarations + source);
		Assert.Contains(result.Diagnostics, message => message.Contains("lifetime") || message.Contains("local storage"));
	}

	[Theory]
	[InlineData("void use(escaped Counter* counter) { take(counter.get); }")]
	[InlineData("escaped delegate int() use(escaped Counter* counter) { return counter.get; }")]
	[InlineData("void use() { Counter c = default; delegate int() callback = c.get; int value = callback(); }")]
	public void Receiver_lifetime_is_preserved_for_valid_contexts(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(Declarations + source));
	}
}
