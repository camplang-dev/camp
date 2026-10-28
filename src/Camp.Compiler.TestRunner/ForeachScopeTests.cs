using System;
using Xunit;

namespace Camp.Compiler.Tests;

public class ForeachScopeTests
{
	[Theory]
	[InlineData("foreach (int v in items) use(v); foreach (int v in items) use(v);")]
	[InlineData("foreach (int v in items) use(v); int v = 9; use(v);")]
	[InlineData("foreach (int v in items) { if (v == 0) continue; break; }")]
	[InlineData("{ int v = 1; use(v); } { int v = 2; use(v); }")]
	public void Loop_local_has_its_own_scope(string body)
	{
		SemanticCompiler.AssertNoDiagnostics(Compile(body));
	}

	[Theory]
	[InlineData("int v = 9; { int v = 2; use(v); }", "v")]
	[InlineData("int items = 1;", "items")]
	[InlineData("int v = 9; foreach (int v in items) use(v);", "v")]
	[InlineData("foreach (int v in items) { foreach (int v in items) use(v); }", "v")]
	[InlineData("foreach (int items in items) use(items);", "items")]
	[InlineData("int i = 1; for (int i = 0; i < 2; i++) use(i);", "i")]
	[InlineData("for (int i = 0; i < 2; i++) { int i = 5; use(i); }", "i")]
	[InlineData("int v = 9; try { } catch (int v) { use(v); }", "v")]
	[InlineData("delegate int(int) callback = (int x) => { int x = 1; return x; };", "x")]
	[InlineData("int v = 1; delegate int(int) callback = (int x) => { int v = 2; return v; };", "v")]
	public void Local_cannot_shadow_visible_local_or_parameter(string body, string name)
	{
		var compilation = Compile(body);
		Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message == $"Symbol '{name}' is already declared in this scope.");
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
