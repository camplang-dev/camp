using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ClassTypeConversionTests
{
	[Theory]
	[InlineData("classtype* bad(Control* item) { return item; }")]
	[InlineData("void bad(Control* item) { classtype* value = item; }")]
	[InlineData("void bad(Control* item) { classtype* value = default; value = item; }")]
	[InlineData("void bad(Control* item, out classtype* value) { value = item; }")]
	[InlineData("classtype* bad(Control* item, bool choose) { return choose ? this : item; }")]
	[InlineData("classtype* bad(Control* item) { return (Control*)item; }")]
	[InlineData("void take(classtype* item) {} void bad(Control* item) { this.take(item); }")]
	[InlineData("classtype* self() { return this; } classtype* bad(Control* item) { return item.self(); }")]
	[InlineData("void fill(out Control* item) { item = default; } void bad() { classtype* value = default; this.fill(out value); }")]
	public void Plain_class_values_require_an_explicit_assertion(string methods)
	{
		var compilation = SemanticCompiler.CompileLowered("class Control { int value; " + methods + " }");
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("explicit classtype cast"));
	}

	[Theory]
	[InlineData("classtype* good(Control* item) { return (classtype*)item; }")]
	[InlineData("classtype* good() { return this; }")]
	[InlineData("classtype* good(classtype* item) { classtype* value = item; value = item; return value; }")]
	[InlineData("classtype* good(classtype* item, bool choose) { return choose ? this : item; }")]
	[InlineData("classtype* good() { return null; }")]
	[InlineData("classtype* good() { return default; }")]
	[InlineData("classtype* self() { return this; } classtype* good() { return this.self(); }")]
	[InlineData("Control* good(classtype* item) { return item; }")]
	[InlineData("void good(Control* item) { classtype* value = (classtype*)item; }")]
	[InlineData("void take(classtype* item) {} void good(classtype* item) { this.take(item); }")]
	public void Safe_producers_explicit_casts_and_widening_are_accepted(string methods)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("class Control { int value; " + methods + " }"));
	}

	[Fact]
	public void Derived_declarations_cannot_implicitly_assert_their_base_pointer()
	{
		var compilation = SemanticCompiler.CompileLowered("class Control { int value; } class Button : Control { classtype* bad(Control* item) { return item; } }");
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("explicit classtype cast"));
	}
}
