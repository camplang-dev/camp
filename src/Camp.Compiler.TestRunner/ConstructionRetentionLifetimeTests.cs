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

	[Fact]
	public void Escaped_callable_context_contract_is_preserved_when_read_from_a_field()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			extern escaped void* malloc(nuint size);
			newtype delegate bool Callback(escaped this, int value);
			struct Options { Callback callback; }
			class Target { Target(unscoped Callback callback) { } }
			Target* make(Options options) { return new Target(options.callback); }
			"""));
	}

	[Fact]
	public void Escaped_callable_context_field_cannot_receive_a_frame_closure()
	{
		var compilation = SemanticCompiler.CompileLowered("""
			newtype delegate int Callback(escaped this);
			struct Options { Callback callback; }
			void use() { int value = 3; Options options = default; options.callback = () => value; }
			""");
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("escaped callable context lifetime"));
	}
}
