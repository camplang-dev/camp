using Xunit;

namespace Camp.Compiler.Tests;

public class BoundConstOfReceiverTests
{
	[Theory]
	[InlineData("", "byte*", true)]
	[InlineData("", "const byte*", true)]
	[InlineData("const ", "const byte*", true)]
	[InlineData("const ", "byte*", false)]
	public void Extension_reference_freezes_receiver_constness(string receiverConst, string result, bool accepted)
	{
		Check("constof(this) byte* head(const byte[] this) { return this.elements; }",
			$"void run({receiverConst}byte[] items) {{ auto bound = items.head; auto copied = bound; {result} result = copied(); }}", accepted);
	}

	[Theory]
	[InlineData("", "byte*", true)]
	[InlineData("const ", "const byte*", true)]
	[InlineData("const ", "byte*", false)]
	public void Struct_reference_freezes_receiver_constness(string receiverConst, string result, bool accepted)
	{
		Check("struct Buffer { byte[] items; constof(this) byte* head(const this) { return this.items.elements; } }",
			$"void run({receiverConst}Buffer* buffer) {{ auto bound = buffer.head; {result} result = bound(); }}", accepted);
	}

	static void Check(string declarations, string body, bool accepted)
	{
		var compilation = SemanticCompiler.CompileLowered(("bound_constof.camp", declarations + body));
		if (accepted) SemanticCompiler.AssertNoDiagnostics(compilation);
		else Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("Call result cannot convert 'const byte*' to 'byte*'"));
	}

	[Theory]
	[InlineData("", "", true)]
	[InlineData("const ", "const ", true)]
	[InlineData("const ", "", false)]
	[InlineData("", "const ", false)]
	public void Bound_receiver_is_also_the_input_equality_anchor(string receiverConst, string otherConst, bool accepted)
	{
		var compilation = SemanticCompiler.CompileLowered($$"""
			constof(this) byte* select(const byte[] this, constof(this) byte[] other) { return this.elements; }
			void run({{receiverConst}}byte[] items, {{otherConst}}byte[] other) { auto bound = items.select; const byte* result = bound(other); }
			""");
		if (accepted) SemanticCompiler.AssertNoDiagnostics(compilation);
		else Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("Argument constness"));
	}

	[Fact]
	public void Remaining_argument_anchor_is_substituted_at_invocation()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			constof(source) byte* choose(const byte[] this, const byte[] source) { return source.elements; }
			void run(const byte[] receiver, byte[] source) { auto bound = receiver.choose; byte* result = bound(source); }
			"""));
	}
}
