using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ConstructionRetentionLifetimeTests
{
	const string Keeper = """
		extern escaped void* malloc(nuint size);
		extern void free(void* memory);
		class Keeper {
			const char[] text;
			Keeper(unscoped const char[] input) { this.text = input; }
			~Keeper() { }
		}
		""";

	[Theory]
	[InlineData("Keeper* make(const char[] input) { return new Keeper(input); }")]
	[InlineData("Keeper* make() { fixed char[8] buffer = $\"abc\"; return new Keeper(buffer[..]); }")]
	public void Heap_construction_rejects_retained_scoped_arguments(string source)
	{
		var result = SemanticCompiler.CompileLowered(Keeper + source);
		Assert.Contains(result.Diagnostics, message => message.Contains("Heap construction cannot retain a non-escaped pointer-bearing value"));
	}

	[Theory]
	[InlineData("Keeper* make(escaped const char[] input) { return new Keeper(input); }")]
	[InlineData("Keeper* make() { return new Keeper(\"literal\"); }")]
	[InlineData("void make(const char[] input) { Keeper local = Keeper(input); }")]
	[InlineData("class Reader { Reader(const char[] input) { } ~Reader() { } } Reader* make(const char[] input) { return new Reader(input); }")]
	public void Safe_construction_remains_valid(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(Keeper + source));
	}
}
