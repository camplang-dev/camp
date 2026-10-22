using System;
using Xunit;

namespace Camp.Compiler.Tests;

public class ForeachScopeTests
{
	[Theory]
	[InlineData("foreach (int v in items) use(v); foreach (int v in items) use(v);")]
	[InlineData("foreach (int v in items) use(v); int v = 9; use(v);")]
	[InlineData("int v = 9; foreach (int v in items) use(v); use(v);")]
	[InlineData("foreach (int v in items) { foreach (int v in items) use(v); use(v); }")]
	[InlineData("foreach (int v in items) { if (v == 0) continue; break; }")]
	[InlineData("foreach (int items in items) use(items);")]
	public void Loop_local_has_its_own_scope(string body)
	{
		SemanticCompiler.AssertNoDiagnostics(Compile(body));
	}

	[Fact]
	public void Loop_local_is_unavailable_after_loop()
	{
		var compilation = Compile("foreach (int v in items) use(v); use(v);");
		Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("Symbol 'v' could not be found.", StringComparison.Ordinal));
	}

	static SemanticCompilation Compile(string body) => SemanticCompiler.CompileLowered(("foreach_scope.camp",
		"void use(int value) {} void run(int[] items) { " + body + " }"));
}
