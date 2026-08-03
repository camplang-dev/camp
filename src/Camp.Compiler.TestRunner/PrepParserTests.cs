using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class PrepParserTests
{
	[Fact]
	public void Parser_treats_prep_as_a_contextual_parameter_modifier()
	{
		CompilationUnitSyntax syntax = Parse("""
			nuint format(prep char[] buffer = default);
			void consume(prep ident);
			""", out IReadOnlyList<ParseDiagnostic> diagnostics);

		Assert.Empty(diagnostics);
		List<ValueParameterSyntax> parameters = Flatten(syntax).OfType<ValueParameterSyntax>().ToList();
		ParameterDeclaratorSyntax declarator = Assert.Single(parameters[0].Declarators ?? []);
		Assert.Equal("prep", declarator.Keyword?.Value);
		Assert.NotNull(parameters[0].DefaultValue);
		Assert.Null(parameters[1].Declarators);
		Assert.Equal("prep", Assert.IsType<QualifiedNameTypeSyntax>(parameters[1].Type).Identifier?.Value);
		Assert.Equal("ident", parameters[1].Identifier?.Value);
	}

	[Fact]
	public void Parser_builds_parenthesized_new_prepared_allocation_prefixes()
	{
		CompilationUnitSyntax syntax = Parse("""
			void main()
			{
				char[] heapText = (new) value.format();
				char[] arenaText = within (arena) (new) value.format() finally delete;
			}
			""", out IReadOnlyList<ParseDiagnostic> diagnostics);

		Assert.Empty(diagnostics);
		List<UnaryPrefixSyntax> prefixes = Flatten(syntax).OfType<UnaryPrefixSyntax>().Where(prefix => prefix.NewKeyword is not null).ToList();
		Assert.Equal(2, prefixes.Count);
		Assert.All(prefixes, prefix => Assert.Equal("new", prefix.NewKeyword?.Value));
		Assert.All(prefixes, prefix => Assert.NotNull(prefix.OpenParenToken));
		Assert.All(prefixes, prefix => Assert.NotNull(prefix.CloseParenToken));
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
