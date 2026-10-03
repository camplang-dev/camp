using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ArgumentParserTests
{
	[Theory]
	[InlineData("Util::twice(3)")]
	[InlineData("Geometry::Metrics::twice(3)")]
	[InlineData("global::twice(3)")]
	[InlineData("Util::Choice.Second")]
	[InlineData("(Util::twice(3))")]
	[InlineData("value: Util::twice(3)")]
	[InlineData("value: (Util::twice(3))")]
	public void Qualified_arguments_are_not_mistaken_for_named_arguments(string argument)
	{
		TokenSequence tokens = new(CampTokenizer.Tokenize($"void test() {{ id({argument}); }}"));
		CompilationUnitSyntax syntax = CampParser.Parse(tokens, out IReadOnlyList<ParseDiagnostic> diagnostics);
		Assert.Empty(diagnostics);
		ArgumentSyntax outerArgument = Flatten(syntax).OfType<ArgumentSyntax>().First();
		Assert.Equal(argument.StartsWith("value:") ? "value" : null, outerArgument.Identifier?.Value);
		Assert.NotNull(outerArgument.Expression);
	}

	[Fact]
	public void Ordinary_named_arguments_still_parse()
	{
		TokenSequence tokens = new(CampTokenizer.Tokenize("void test() { id(value: 3); }"));
		CompilationUnitSyntax syntax = CampParser.Parse(tokens, out IReadOnlyList<ParseDiagnostic> diagnostics);
		Assert.Empty(diagnostics);
		Assert.Equal("value", Assert.Single(Flatten(syntax).OfType<ArgumentSyntax>()).Identifier?.Value);
	}

	static IEnumerable<SyntaxNode> Flatten(SyntaxNode node)
	{
		yield return node;
		foreach (SyntaxNode child in SyntaxNodeTraversal.Children(node))
			foreach (SyntaxNode descendant in Flatten(child))
				yield return descendant;
	}
}
