using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class DanglingElseTests
{
	[Theory]
	[InlineData("while (i < 4) if (i++ == 1) continue; else i++;")]
	[InlineData("for (i = 0; i < 4; i++) if (i == 1) continue; else i++;")]
	[InlineData("foreach (int value in values) if (value == 1) continue; else i++;")]
	[InlineData("do if (i++ == 1) continue; else i++; while (i < 4);")]
	[InlineData("if (i == 0) i++; else while (i < 4) if (i++ == 1) continue; else i++;")]
	public void Else_attaches_through_an_unbraced_loop_body(string body)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("void f(int[] values) { int i = 0; " + body + " }"));
	}

	[Fact]
	public void Else_attaches_to_nearest_unmatched_if()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("void f(bool a, bool b) { if (a) if (b) return; else return; }");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		IfStatement outer = Assert.Single(SemanticCompiler.Descendants<IfStatement>(SemanticCompiler.Function(compilation, "f")), statement => statement.Body is IfStatement);
		Assert.Null(outer.ElseBody);
		Assert.NotNull(Assert.IsType<IfStatement>(outer.Body).ElseBody);
	}

	[Theory]
	[InlineData("while (true) { if (true) break; } else return;")]
	[InlineData("else return;")]
	[InlineData("if (true) return; else return; else return;")]
	[InlineData("do if (true) return; while (false); else return;")]
	public void Detached_or_duplicate_else_is_located(string body)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("void f() {\n" + body + "\n}");
		Assert.Contains(compilation.BindDiagnostics, diagnostic => diagnostic.Message.Contains("else")
			&& diagnostic.Range is TokenRange range && range.StartLineNumber == 2 && range.Value == "else");
	}
}
