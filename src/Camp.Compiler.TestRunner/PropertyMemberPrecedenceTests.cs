using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class PropertyMemberPrecedenceTests
{
	[Theory]
	[InlineData("struct Holder { int Value; int getValue() => 7; }")]
	[InlineData("struct Holder { int Value; void setValue(int next) {} }")]
	[InlineData("struct Holder { int Value; int getValue() => 7; void setValue(int next) {} }")]
	[InlineData("class Holder { int Value; int getValue() => 7; void setValue(int next) {} }")]
	[InlineData("class Base { int Value; } class Holder : Base { int getValue() => 7; void setValue(int next) {} }")]
	[InlineData("struct Holder { int Value; } int getValue(const Holder* this) => 7; void setValue(Holder* this, int next) {}")]
	public void Visible_field_reads_and_writes_never_call_accessors(string declarations)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(declarations + " int update(Holder* h) { h.Value = 5; h.Value += 1; h.Value++; ++h.Value; return h.Value; }");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		Assert.Empty(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "update")));
	}

	[Theory]
	[InlineData("class Holder { static int Value; static int getValue() => 7; static void setValue(int next) {} }")]
	[InlineData("static class Holder { static int Value; static int getValue() => 7; static void setValue(int next) {} }")]
	public void Static_fields_take_precedence(string declarations)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(declarations + " int update() { Holder.Value = 5; Holder.Value++; return Holder.Value; }");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		Assert.Empty(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "update")));
	}

	[Fact]
	public void Inherited_generic_fields_keep_their_substituted_type()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			class Base<T> { T* Value; }
			class Holder : Base<int> { int getValue() => 7; void setValue(int* next) {} }
			int* update(Holder* h) { h.Value = null; return h.Value; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		Assert.Empty(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "update")));
	}

	[Fact]
	public void Indexing_binds_the_array_field()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			struct Holder { int[] Value; int getValue(int index) => 7; void setValue(int index, int next) {} }
			int update(Holder* h) { h.Value[0] = 5; h.Value[0]++; return h.Value[0]; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		Assert.Empty(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "update")));
	}

	[Fact]
	public void Calls_bind_the_callable_field()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			struct Holder { fn int(int) Value; int getValue(int input) => 7; }
			int invoke(Holder* h) { return h.Value(3); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "invoke")));
		Assert.IsType<MemberReferenceExpression>(call.Target);
		Assert.DoesNotContain(SemanticCompiler.Descendants<MethodReferenceExpression>(call), method => method.Candidates.Any(function => function.Name == "getValue"));
	}

	[Fact]
	public void A_const_field_does_not_fall_back_to_a_setter()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("struct Holder { const int Value; void setValue(int next) {} } void update(Holder* h) { h.Value = 5; }");
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("const"));
	}
}
