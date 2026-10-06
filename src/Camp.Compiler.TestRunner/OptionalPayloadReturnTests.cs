using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class OptionalPayloadReturnTests
{
	[Theory]
	[InlineData("int?", "item,item_specified")]
	[InlineData("int??", "item,item_value_specified,item_specified")]
	[InlineData("int???", "item,item_value_value_specified,item_value_specified,item_specified")]
	[InlineData("int[]?", "item,item_length,item_specified")]
	[InlineData("int[]??", "item,item_value_length,item_value_specified,item_specified")]
	[InlineData("Predicate?", "item,item_context,item_specified")]
	[InlineData("delegate int(int)?", "item,item_context,item_specified")]
	[InlineData("once int(int)?", "item,item_context,item_specified")]
	[InlineData("AsyncPredicate?", "item,item_context,item_specified")]
	[InlineData("iter(int)?", "item,item_context,item_specified")]
	public void Recursive_optional_names_match_across_storage_parameters_and_results(string type, string names)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype delegate int Predicate(int n);
			newtype async int AsyncPredicate(int n);
			struct Holder { {{type}} item; }
			{{type}} copy({{type}} item) => item;
			void local() { {{type}} item = default; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		string[] expected = names.Split(',');
		FunctionDefinition copy = SemanticCompiler.Function(compilation, "copy");
		Assert.Equal(expected, copy.Parameters.Where(parameter => !parameter.IsExpandedReturnComponent).Select(parameter => parameter.Name));
		Assert.Equal(expected.Skip(1).Select(name => name.Replace("item", "result")),
			copy.Parameters.Where(parameter => parameter.IsExpandedReturnComponent).Select(parameter => parameter.Name));
		Assert.Equal(expected, Assert.IsType<StructDefinition>(SemanticCompiler.Type(compilation, "Holder")).Fields.Select(field => field.Name));
		Assert.Equal(expected, SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "local")).SelectMany(declaration => declaration.Target.Names));
	}

	[Theory]
	[InlineData("int?? item, bool item_value_specified")]
	[InlineData("int??? item, bool item_value_value_specified")]
	[InlineData("int[]?? item, nuint item_value_length")]
	[InlineData("Predicate? item, void* item_context")]
	public void Recursive_optional_component_names_are_reserved(string parameters)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"newtype delegate int Predicate(int n); void take({parameters}) {{ }}");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("as a component of 'item'"));
	}

	[Theory]
	[InlineData("void local() { int?? item = default; bool item_value_specified; }")]
	[InlineData("void local() { bool item_value_specified; int?? item = default; }")]
	[InlineData("struct Holder { int?? item; bool item_value_specified; }")]
	[InlineData("int?? copy(bool result_value_specified) => default;")]
	public void Recursive_optional_names_collide_in_every_binding_scope(string source)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(source);
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("already declared"));
	}

	[Fact]
	public void Optional_of_optional_callable_requires_value_before_invocation()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			newtype delegate int Predicate(int n);
			void local(Predicate?? callback) { callback(1); }
			""");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("reach the inner optional through .value"));
	}

	[Theory]
	[InlineData("class P { } void take(P? value) { }", "copyable")]
	[InlineData("fixed struct P { int x; } void take(P? value) { }", "copyable")]
	[InlineData("void take(int[4]? value) { }", "copyable")]
	[InlineData("void take(int?[] value) { }", "void<int?>[]")]
	[InlineData("void take(char[][] value) { }", "void<char[]>[]")]
	[InlineData("newtype delegate int Predicate(int n); void take(Predicate[] value) { }", "void<Predicate>[]")]
	[InlineData("void take(int?*[] value) { }", "void<int?*>[]")]
	[InlineData("struct Holder { fixed int?*[4] value; }", "void<int?*>[]")]
	public void Optional_copyability_and_expandable_element_rules_diagnose_before_emission(string source, string message)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(source);
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains(message));
	}

	[Theory]
	[InlineData("P b = { 1 }; return b;")]
	[InlineData("P b = default; return b;")]
	[InlineData("return payload();")]
	[InlineData("P b = { 1 }; return (b);")]
	[InlineData("Holder h = default; return h.Item;")]
	public void Struct_payload_return_initializes_the_optional_presence(string body)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			struct P { int x; }
			class Holder { P getItem() { P p = { 3 }; return p; } }
			P payload() { P p = { 2 }; return p; }
			P? make() { {{body}} }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition make = SemanticCompiler.Function(compilation, "make");
		ParameterDefinition presence = Assert.Single(make.Parameters, static parameter => parameter.IsExpandedReturnComponent);
		AssignmentExpression assignment = Assert.Single(SemanticCompiler.Descendants<AssignmentExpression>(make),
			assignment => assignment.Target is VariableReferenceExpression target && ReferenceEquals(target.Variable, presence));
		Assert.True(Assert.IsType<LiteralExpression>(assignment.Value).Value is true);
	}

	[Fact]
	public void Existing_optional_return_preserves_the_presence_component()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			struct P { int x; }
			P? copy(P? value) { return value; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition copy = SemanticCompiler.Function(compilation, "copy");
		AssignmentExpression assignment = Assert.Single(SemanticCompiler.Descendants<AssignmentExpression>(copy));
		VariableReferenceExpression source = Assert.IsType<VariableReferenceExpression>(assignment.Value);
		Assert.Equal("value_specified", Assert.IsType<ParameterDefinition>(source.Variable).Name);
	}
}
