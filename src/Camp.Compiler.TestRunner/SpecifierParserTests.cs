using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class SpecifierParserTests
{
	[Fact]
	public void Grammar_and_occurrence_ranges_use_only_the_written_source()
	{
		const string source = """
			_rect /* before result */ _far _get();
			_rect _get();
			fn _a() a;
			fn _call nint() _type callback;
			_targetcall _targetcall int duplicated();
			byte* _far * _near nested;
			alias _choice = _targetcall;
			alias _qualified = Space::_call;
			""";
		CompilationUnitSyntax tree = Parse(source);
		MemberDeclarationSyntax[] members = Flatten(tree).OfType<MemberDeclarationSyntax>().ToArray();
		Assert.Equal("_rect", members[0].CallSpec?.Value);
		Assert.Equal("_far", Assert.IsType<QualifiedNameTypeSyntax>(members[0].Type).Identifier?.Value);
		Assert.Equal("_get", members[0].Identifier?.Value);
		Assert.Null(members[1].CallSpec);
		Assert.Equal("_rect", Assert.IsType<QualifiedNameTypeSyntax>(members[1].Type).Identifier?.Value);
		CallableTypeSyntax[] callables = Flatten(tree).OfType<CallableTypeSyntax>().ToArray();
		Assert.Null(callables[0].CallSpec);
		Assert.Equal("_a", Assert.IsType<QualifiedNameTypeSyntax>(callables[0].ReturnType).Identifier?.Value);
		Assert.Equal("_call", callables[1].CallSpec?.Value);
		Assert.Equal("_type", callables[1].TargetSpec?.Value);
		Assert.Single(members[4].AdditionalCallSpecs!);
		SpecifierSyntax[] occurrences = Flatten(tree).OfType<SpecifierSyntax>().ToArray();
		Assert.Equal(8, occurrences.Length);
		Assert.All(occurrences, occurrence => Assert.NotNull(occurrence.Range));
		Assert.Equal(occurrences.Length, occurrences.Select(occurrence => occurrence.Range).Distinct().Count());
		AliasDeclarationSyntax qualified = Flatten(tree).OfType<AliasDeclarationSyntax>().Last();
		Assert.Single(Assert.Single(qualified.TargetCandidates!).TargetName!.Qualifiers!);
		CampParser.Parse(new TokenSequence(CampTokenizer.Tokenize("_Far int x;")), out var diagnostics);
		Assert.NotEmpty(diagnostics);
	}

	[Theory]
	[InlineData("_far", true)]
	[InlineData("_my_api", true)]
	[InlineData("_x1", true)]
	[InlineData("_Far", false)]
	[InlineData("__far", false)]
	[InlineData("_far_", false)]
	[InlineData("_1x", false)]
	[InlineData("far", false)]
	[InlineData("_fär", false)]
	public void Postfix_recognition_uses_exact_ascii_spelling(string name, bool specifier)
	{
		CompilationUnitSyntax tree = CampParser.Parse(new TokenSequence(CampTokenizer.Tokenize($"byte* {name} value;")), out _);
		Assert.Equal(specifier, Flatten(tree).OfType<SpecifierSyntax>().Any());
	}

	[Fact]
	public void Parameters_disambiguate_anonymous_names_and_defaults_by_written_carrier()
	{
		CompilationUnitSyntax tree = Parse("""
			void f(int* _value, int* _value = null, int* _targettype _value = null,
				int _ordinary, int[2] _fixed, Named _named, fn int() _callback,
				fn int() _targettype _callback = null);
			newtype delegate _targetcall bool Callback(int* _targettype _value = null) _targettype;
			int* _local;
			""");
		ValueParameterSyntax[] parameters = Flatten(tree).OfType<ValueParameterSyntax>().ToArray();
		Assert.Null(parameters[0].Identifier);
		Assert.Null(parameters[1].Identifier);
		Assert.NotNull(parameters[1].DefaultValue);
		Assert.Equal("_value", parameters[2].Identifier?.Value);
		Assert.Equal("_ordinary", parameters[3].Identifier?.Value);
		Assert.Equal("_fixed", parameters[4].Identifier?.Value);
		Assert.Equal("_named", parameters[5].Identifier?.Value);
		Assert.Null(parameters[6].Identifier);
		Assert.Equal("_callback", parameters[7].Identifier?.Value);
		Assert.Equal("_value", parameters[8].Identifier?.Value);
		Assert.Equal("_local", Flatten(tree).OfType<MemberDeclarationSyntax>().Last().Identifier?.Value);
		Assert.Equal(2, Flatten(tree).OfType<SpecifierSyntax>().Count(spec => spec.ParameterNameAmbiguous && spec.Value == "_value"));
	}

	[Theory]
	[InlineData("fn")]
	[InlineData("delegate")]
	[InlineData("async")]
	[InlineData("once")]
	public void Callable_newtype_specs_surround_the_result_and_declaration_parameter_list(string keyword)
	{
		CompilationUnitSyntax tree = Parse($"newtype {keyword} _targetcall int Callback(int* _targettype _value = null) _targettype;");
		TypeDeclarationSyntax declaration = Assert.Single(Flatten(tree).OfType<TypeDeclarationSyntax>());
		CallableTypeSyntax callable = Assert.IsType<CallableTypeSyntax>(declaration.Type);
		Assert.Equal("_targetcall", callable.CallSpec?.Value);
		Assert.Equal("_targettype", callable.TargetSpec?.Value);
		Assert.Equal("_value", Assert.IsType<ValueParameterSyntax>(Assert.Single(declaration.ParameterList!.Parameters!)).Identifier?.Value);
		Token[] tokens = SyntaxNodeTraversal.Tokens(tree).ToArray();
		Assert.Equal(tokens.Select(token => token.Index).Order(), tokens.Select(token => token.Index));
	}

	static CompilationUnitSyntax Parse(string source)
	{
		CompilationUnitSyntax tree = CampParser.Parse(new TokenSequence(CampTokenizer.Tokenize(source)), out IReadOnlyList<ParseDiagnostic> diagnostics);
		Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics) + "\n" + CompilerXmlSerializer.SerializeSyntax(tree));
		return tree;
	}

	static IEnumerable<SyntaxNode> Flatten(SyntaxNode node)
	{
		yield return node;
		foreach (SyntaxNode child in SyntaxNodeTraversal.Children(node))
			foreach (SyntaxNode descendant in Flatten(child)) yield return descendant;
	}
}
