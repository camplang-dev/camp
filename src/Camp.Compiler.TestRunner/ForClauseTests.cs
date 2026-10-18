using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ForClauseTests
{
	[Theory]
	[InlineData(false, false, false)]
	[InlineData(false, false, true)]
	[InlineData(false, true, false)]
	[InlineData(false, true, true)]
	[InlineData(true, false, false)]
	[InlineData(true, false, true)]
	[InlineData(true, true, false)]
	[InlineData(true, true, true)]
	public void Omitted_clauses_keep_their_positions(bool initializer, bool condition, bool increment)
	{
		string source = "void f() { int i = 0; for ("
			+ (initializer ? "i = 0" : "") + ";"
			+ (condition ? "i < 3" : "") + ";"
			+ (increment ? "i++" : "") + ") { break; } }";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(source);
		SemanticCompiler.AssertNoDiagnostics(compilation);
		ForStatement loop = Assert.Single(SemanticCompiler.Descendants<ForStatement>(SemanticCompiler.Function(compilation, "f")));
		Assert.Equal(3, loop.Condition.Clauses.Count);
		Assert.Equal(initializer, loop.Condition.Clauses[0] is not null);
		Assert.Equal(condition, loop.Condition.Clauses[1] is not null);
		Assert.Equal(increment, loop.Condition.Clauses[2] is not null);
	}
}
