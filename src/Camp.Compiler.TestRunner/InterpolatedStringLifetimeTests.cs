using Xunit;

namespace Camp.Compiler.Tests;

public sealed class InterpolatedStringLifetimeTests
{
	const string Declarations = """
		extern escaped void* malloc(nuint size);
		struct Count
		{
			prep char[] toString()
			{
				if (buffer.length > 0) buffer[0] = 'x';
				return 1;
			}
		}
		""";

	[Theory]
	[InlineData("string bad(Count count) { return $\"a{count}\"; }")]
	[InlineData("string bad(Count count) { string local = $\"a{count}\"; return local; }")]
	[InlineData("string bad(Count count) { auto local = $\"a{count}\"; auto copy = local; return copy; }")]
	[InlineData("char[] bad(Count count) { char[] local = $\"a{count}\"; return local; }")]
	[InlineData("const char[] bad(Count count) { const char[] local = $\"a{count}\"; return local; }")]
	[InlineData("string bad(Count count, bool choose) { string local = choose ? $\"a{count}\" : $\"b{count}\"; return local; }")]
	[InlineData("string bad(Count count, bool choose) { string local = choose ? $\"constant\" : $\"a{count}\"; return local; }")]
	[InlineData("string bad(Count count, bool choose) { string local = choose ? \"constant\" : $\"a{count}\"; return local; }")]
	[InlineData("string bad(Count count, bool choose) { string local = choose ? $\"a{count}\" : $\"constant\"; return local; }")]
	[InlineData("string bad(Count count, bool choose) { string local = choose ? $\"a{count}\" : \"constant\"; return local; }")]
	public void Implicit_scoped_interpolation_cannot_be_returned(string source)
	{
		var compilation = SemanticCompiler.CompileLowered(Declarations + source);
		Assert.NotEmpty(compilation.Diagnostics);
		Assert.All(compilation.Diagnostics, diagnostic => Assert.Contains("cannot return stackalloc-backed storage", diagnostic));
	}

	[Theory]
	[InlineData("string good(Count count) { return within(default) new $\"a{count}\"; }")]
	[InlineData("string good(Count count) { string local = within(default) new $\"a{count}\"; return local; }")]
	[InlineData("void good(Count count) { string local = $\"a{count}\"; }")]
	[InlineData("string good() { string local = $\"a{\"b\"}\"; return local; }")]
	[InlineData("string good() { return $\"a{\"b\"}\"; }")]
	[InlineData("string good(bool choose) { string local = choose ? $\"a\" : $\"b\"; return local; }")]
	[InlineData("string good(Count count, bool choose) { return choose ? (within(default) new $\"a{count}\") : $\"constant\"; }")]
	[InlineData("struct Holder { string kept; } void good(Count count) { Holder local = default; local.kept = $\"a{count}\"; }")]
	public void Allocated_constant_and_local_results_remain_valid(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(Declarations + source));
	}
}
