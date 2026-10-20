using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CatchStorageTests
{
	[Theory]
	[InlineData("int", "long")]
	[InlineData("int", "int")]
	[InlineData("uint", "ulong")]
	[InlineData("short", "int")]
	public void Catch_target_accepts_every_thrown_value(string thrown, string target)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			int call(thrown {{thrown}} error) { throw 1; }
			int run() { {{target}} error = 0; return call(catch error); }
			"""));
	}

	[Theory]
	[InlineData("long", "int")]
	[InlineData("ulong", "uint")]
	[InlineData("int", "short")]
	public void Narrow_catch_target_is_diagnosed(string thrown, string target)
	{
		var compilation = SemanticCompiler.CompileLowered($$"""
			int call(thrown {{thrown}} error) { throw 1; }
			int run() { {{target}} error = 0; return call(catch error); }
			""");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains($"Catch argument cannot convert '{thrown}' to '{target}'."));
	}
}
