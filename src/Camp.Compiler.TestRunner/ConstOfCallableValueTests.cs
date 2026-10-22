using Xunit;

namespace Camp.Compiler.Tests;

public class ConstOfCallableValueTests
{
	const string Function = "constof(source) byte* first(const byte[] source) { return source.elements; }";
	const string Callback = "fn constof(input) byte*(const byte[] input)";

	[Theory]
	[InlineData("global")]
	[InlineData("local")]
	[InlineData("assignment")]
	[InlineData("argument")]
	[InlineData("auto")]
	public void Callable_keeps_its_dependent_signature(string surface)
	{
		string code = surface switch
		{
			"global" => Callback + " callback = first; void run(byte[] items) { byte* result = callback(items); }",
			"local" => "void run(byte[] items) { " + Callback + " callback = first; byte* result = callback(items); }",
			"assignment" => "void run(byte[] items) { " + Callback + " callback = null; callback = first; byte* result = callback(items); }",
			"argument" => "void take(" + Callback + " callback, byte[] items) { byte* result = callback(items); } void run(byte[] items) { take(first, items); }",
			_ => "void run(byte[] items) { auto callback = first; byte* result = callback(items); }"
		};
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(("constof_callable.camp", Function + code)));
	}

	[Theory]
	[InlineData("global")]
	[InlineData("local")]
	[InlineData("assignment")]
	[InlineData("argument")]
	public void Ordinary_const_result_cannot_satisfy_dependent_result(string surface)
	{
		string declaration = "const byte* fixedConst(const byte[] source) { return source.elements; }";
		string code = surface switch
		{
			"global" => Callback + " callback = fixedConst;",
			"local" => "void run() { " + Callback + " callback = fixedConst; }",
			"assignment" => "void run() { " + Callback + " callback = null; callback = fixedConst; }",
			_ => "void take(" + Callback + " callback) {} void run() { take(fixedConst); }"
		};
		var compilation = SemanticCompiler.CompileLowered(("constof_callable_invalid.camp", declaration + code));
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("does not preserve the callable's constof signature"));
	}

	[Fact]
	public void Ordinary_generic_function_value_keeps_its_constructed_result()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			T* identity<T>(T* value) { return value; }
			void run(byte* item) { auto callback = identity<byte>; byte* result = callback(item); }
			"""));
	}

	[Fact]
	public void Bound_delegate_initializer_checks_its_signature()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			struct Buffer { byte[] items; constof(this) byte* head(const this) { return this.items.elements; } }
			void run(Buffer* buffer) { delegate constof(this) byte*() callback = buffer.head; }
			"""));
	}

	[Theory]
	[InlineData("items, items", true)]
	[InlineData("readonly, readonly", true)]
	[InlineData("items, readonly", false)]
	[InlineData("readonly, items", false)]
	public void Callable_input_dependence_keeps_equality(string arguments, bool accepted)
	{
		var compilation = SemanticCompiler.CompileLowered("""
			void consume(const byte[] source, constof(source) byte[] other) {}
			void run(byte[] items, const byte[] readonly) {
			 fn void(const byte[] input, constof(input) byte[] other) callback = consume;
			""" + "callback(" + arguments + "); }");
		if (accepted) SemanticCompiler.AssertNoDiagnostics(compilation);
		else Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("Argument constness"));
	}

	[Fact]
	public void Const_anchor_still_produces_const_result()
	{
		var compilation = SemanticCompiler.CompileLowered(("constof_callable_const.camp", Function +
			"void run(const byte[] items) { " + Callback + " callback = first; byte* result = callback(items); }"));
		Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("Call result cannot convert 'const byte*' to 'byte*'"));
	}
}
