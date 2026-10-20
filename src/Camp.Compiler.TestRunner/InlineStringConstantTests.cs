using Xunit;

namespace Camp.Compiler.Tests;

public sealed class InlineStringConstantTests
{
	[Theory]
	[InlineData("", "$\"{PREFIX}-x\"")]
	[InlineData("namespace Words;", "$\"{PREFIX}-x\"")]
	[InlineData("", "$\"{(PREFIX)}-x\"")]
	[InlineData("", "$\"{$\"{PREFIX}\"}-x\"")]
	public void Inline_text_holes_fold_in_their_declaring_file(string prelude, string expression)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(prelude + """
			inline string PREFIX = $"a{"bc"}";
			""" + "inline string BOTH = " + expression + ";"));
	}

	[Theory]
	[InlineData("using Words;", "PREFIX")]
	[InlineData("using Words as W;", "W::PREFIX")]
	public void Imported_inline_text_uses_the_holes_enclosing_imports(string import, string reference)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(
			("words.camp", "namespace Words; public inline string PREFIX = \"abc\";"),
			("use.camp", import + "inline string BOTH = $\"{" + reference + "}-x\";")));
	}

	[Fact]
	public void A_hole_does_not_gain_access_to_another_files_private_constant()
	{
		var compilation = SemanticCompiler.CompileLowered(
			("private.camp", "inline string PREFIX = \"abc\";"),
			("use.camp", "inline string BOTH = $\"{PREFIX}-x\";"));
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("another file but is not exported"));
	}
}
