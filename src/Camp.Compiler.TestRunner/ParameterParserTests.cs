using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ParameterParserTests
{
	[Theory]
	[InlineData("within allocator", typeof(WithinParameterSyntax))]
	[InlineData("within scoped allocator", typeof(WithinParameterSyntax))]
	[InlineData("within this.allocator", typeof(WithinParameterSyntax))]
	[InlineData("within Allocator* allocator", typeof(ValueParameterSyntax))]
	[InlineData("within @testname Allocator* allocator", typeof(ValueParameterSyntax))]
	[InlineData("sizeof(int)", typeof(SizeOfParameterSyntax))]
	[InlineData("typenameof(int)", typeof(NameOfParameterSyntax))]
	[InlineData("vtableof(Owner: Face)", typeof(VTableOfParameterSyntax))]
	[InlineData("int value", typeof(ValueParameterSyntax))]
	public void Leading_attributes_preserve_parameter_kind_and_source_tokens(string parameter, System.Type expectedType)
	{
		TokenSequence tokens = new(CampTokenizer.Tokenize($"void f(@symbol(\"a\") @testname {parameter});"));
		CompilationUnitSyntax syntax = CampParser.Parse(tokens, out IReadOnlyList<ParseDiagnostic> diagnostics);
		Assert.Empty(diagnostics);
		ParameterSyntax parsed = Assert.Single(Flatten(syntax).OfType<ParameterSyntax>());
		Assert.IsType(expectedType, parsed);
		Assert.Equal(parameter.Contains("@testname") ? 3 : 2, Flatten(parsed).OfType<AttributeSyntax>().Count());
		Assert.Equal("@symbol", SyntaxNodeTraversal.Tokens(parsed).First().Value);
	}

	[Theory]
	[InlineData("within allocator")]
	[InlineData("within this.allocator")]
	[InlineData("within @second Allocator* allocator")]
	[InlineData("sizeof(int)")]
	[InlineData("typenameof(int)")]
	[InlineData("vtableof(Owner: Face)")]
	[InlineData("int value")]
	public void Binding_preserves_prefix_attributes_in_source_order(string parameter)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileDeclarations($"void f(@first {parameter}) {{ }}");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		ParameterDefinition parsed = Assert.Single(SemanticCompiler.Function(compilation, "f").Parameters);
		Assert.Equal(parameter.Contains("@second") ? new[] { "@first", "@second" } : new[] { "@first" },
			parsed.Attributes.Select(attribute => attribute.Name));
	}

	[Theory]
	[InlineData("within allocator")]
	[InlineData("within Allocator* allocator")]
	public void Attributed_allocator_parameters_reach_semantic_validation(string parameter)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/within_attribute.camp", $$"""
			struct Assertion { escaped string message; escaped string sourcefile; uint sourceline; }
			class Allocator { }
			@factorytest
			void factory(@testname {{parameter}}, thrown Assertion* assertion) { }
			"""));
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
		Assert.All(compilation.AnalysisDiagnostics, diagnostic =>
			Assert.Equal("@testname is not valid on a within parameter.", diagnostic.Message));
		ParameterDefinition allocator = SemanticCompiler.Function(compilation, "factory").Parameters[0];
		Assert.Single(allocator.Attributes);
		Assert.Equal(ParameterModifier.Within, allocator.Modifier);
	}

	static IEnumerable<SyntaxNode> Flatten(SyntaxNode node)
	{
		yield return node;
		foreach (SyntaxNode child in SyntaxNodeTraversal.Children(node))
			foreach (SyntaxNode descendant in Flatten(child))
				yield return descendant;
	}
}
