using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ForLoopScopeTests
{
	[Fact]
	public void Sibling_loops_can_reuse_their_counter_name()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			int sum() {
				int total = 0;
				for (int i = 0; i < 3; i++) total += i;
				for (int i = 10; i < 12; i++) total += i;
				return total;
			}
			"""));
	}

	[Fact]
	public void Counter_is_not_visible_after_the_loop()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("int f() { for (int index = 0; index < 3; index++) {} return index; }");
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message == "Symbol 'index' could not be found." && diagnostic.Range is not null);
	}

	[Theory]
	[InlineData("for (int i = 0; i < 3; i++) { if (i == 2) return i; }")]
	[InlineData("for (int i = 0; i < 3; i++) { for (int j = i; j < 3; j++) { if (j == 2) return i; } }")]
	[InlineData("for (int i = 0; i < 3; i++) {} int i = 4; return i;")]
	public void Loop_scope_preserves_function_context_and_outer_visibility(string body)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("int f() { " + body + " return 0; }"));
	}
}
