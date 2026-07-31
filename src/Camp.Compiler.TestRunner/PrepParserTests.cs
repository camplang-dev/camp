using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class PrepParserTests
{
	[Fact]
	public void Parser_builds_prep_parameter_modifier()
	{
		CompilationUnitSyntax syntax = Parse("""
			nuint format(prep char[] buffer = default);
			""", out IReadOnlyList<ParseDiagnostic> diagnostics);

		Assert.Empty(diagnostics);
		ValueParameterSyntax parameter = Flatten(syntax).OfType<ValueParameterSyntax>().Single();
		ParameterDeclaratorSyntax declarator = Assert.Single(parameter.Declarators ?? []);
		Assert.Equal("prep", declarator.Keyword?.Value);
		Assert.NotNull(parameter.DefaultValue);
	}

	[Fact]
	public void Parser_builds_prep_and_prep_new_expression_prefixes()
	{
		CompilationUnitSyntax syntax = Parse("""
			void main()
			{
				char[] stackText = prep value.format();
				char[] heapText = prep new value.format();
				char[] arenaText = within (arena) prep new value.format();
			}
			""", out IReadOnlyList<ParseDiagnostic> diagnostics);

		Assert.Empty(diagnostics);
		List<UnaryPrefixSyntax> prefixes = Flatten(syntax).OfType<UnaryPrefixSyntax>().Where(prefix => prefix.OperatorOrKeyword?.Value == "prep").ToList();
		Assert.Equal(3, prefixes.Count);
		Assert.Null(prefixes[0].NewKeyword);
		Assert.Equal("new", prefixes[1].NewKeyword?.Value);
		Assert.Equal("new", prefixes[2].NewKeyword?.Value);
	}

	static CompilationUnitSyntax Parse(string source, out IReadOnlyList<ParseDiagnostic> diagnostics)
	{
		TokenSequence tokens = new(CampTokenizer.Tokenize(source));
		return CampParser.Parse(tokens, out diagnostics);
	}

	static IEnumerable<SyntaxNode> Flatten(SyntaxNode node)
	{
		yield return node;
		foreach (SyntaxNode child in SyntaxNodeTraversal.Children(node))
			foreach (SyntaxNode descendant in Flatten(child))
				yield return descendant;
	}
}
