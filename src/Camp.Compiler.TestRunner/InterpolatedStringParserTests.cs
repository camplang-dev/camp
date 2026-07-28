using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class InterpolatedStringParserTests
{
	[Fact]
	public void Parser_builds_interpolated_string_segments()
	{
		CompilationUnitSyntax syntax = Parse("""
			void main()
			{
				auto value = $"before {format({ .x = items[0], .y = getValue(n => n + 1) })} after {{brace}}";
			}
			""", out IReadOnlyList<ParseDiagnostic> diagnostics);

		Assert.Empty(diagnostics);
		InterpolatedStringExpressionSyntax interpolation = SyntaxNodeTraversal.Children(syntax)
			.SelectMany(Flatten)
			.OfType<InterpolatedStringExpressionSyntax>()
			.Single();

		Assert.Collection(
			interpolation.Segments,
			segment => Assert.Equal("before ", Assert.IsType<InterpolatedStringTextSegmentSyntax>(segment).Text),
			segment => Assert.NotNull(Assert.IsType<InterpolatedStringExpressionSegmentSyntax>(segment).Expression),
			segment => Assert.Equal(" after {brace}", Assert.IsType<InterpolatedStringTextSegmentSyntax>(segment).Text));
	}

	[Theory]
	[InlineData("""void main() { auto value = $"{}"; }""", "Interpolation hole must contain an expression.")]
	[InlineData("""void main() { auto value = $"value }"; }""", "Unmatched '}' in interpolated string.")]
	[InlineData("""void main() { auto value = $"value {1 + 2"; }""", "Unterminated interpolation hole.")]
	[InlineData("void main() { auto value = $\"value {1}\n\"; }", "Unterminated interpolated string.")]
	public void Parser_reports_interpolated_string_syntax_errors(string source, string message)
	{
		Parse(source, out IReadOnlyList<ParseDiagnostic> diagnostics);

		Assert.Contains(diagnostics, diagnostic => diagnostic.Message == message);
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
