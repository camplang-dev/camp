using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class LoopTransferTests
{
	[Theory]
	[InlineData("break;")]
	[InlineData("continue;")]
	[InlineData("switch (1) { case 1: continue; }")]
	[InlineData("while (true) { fn void() action = () => { break; }; break; }")]
	[InlineData("while (true) { fn void() action = () => { continue; }; break; }")]
	[InlineData("switch (1) { case 1: fn void() action = () => { break; }; break; }")]
	public void Ineligible_transfers_are_diagnosed_at_the_keyword(string body)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("void f() {\n" + body + "\n}");
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic =>
			diagnostic.Message.Contains("statement requires an enclosing")
			&& diagnostic.Range is TokenRange range && range.StartLineNumber == 2
			&& range.Value is "break" or "continue");
	}

	[Theory]
	[InlineData("while (true) { if (true) break; continue; }")]
	[InlineData("do { break; continue; } while (true);")]
	[InlineData("for (int i = 0; i < 2; i++) { break; continue; }")]
	[InlineData("switch (1) { case 1: { break; } default: break; }")]
	[InlineData("while (true) { switch (1) { case 1: continue; default: break; } break; }")]
	[InlineData("fn void() action = () => { while (true) { break; continue; } };")]
	public void Eligible_transfers_remain_valid(string body)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("void f() { " + body + " }"));
	}
}
