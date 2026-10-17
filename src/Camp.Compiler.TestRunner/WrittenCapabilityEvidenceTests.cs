using Xunit;

namespace Camp.Compiler.Tests;

public sealed class WrittenCapabilityEvidenceTests
{
	[Theory]
	[InlineData("sizeof(T), sizeof(U)", "sizeof_U, sizeof_T")]
	[InlineData("typenameof(T), typenameof(U)", "typenameof_U, typenameof_T")]
	[InlineData("vtableof(T: Counter), vtableof(U: Counter)", "vtableof_U_Counter, vtableof_T_Counter")]
	[InlineData("sizeof(T), typenameof(T), vtableof(T: Counter), sizeof(U), typenameof(U), vtableof(U: Counter)", "sizeof_U, typenameof_U, vtableof_U_Counter, sizeof_T, typenameof_T, vtableof_T_Counter")]
	public void Swapped_named_evidence_uses_the_callers_operand(string slots, string evidence)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			interface Counter { int read(); }
			int pair<T, U>(T* first, U* second, {{slots}}) => 0;
			int swapped<T, U>(T* a, U* b, {{slots}}) => pair<U, T>(b, a, {{evidence}});
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Fact]
	public void Wrong_operand_is_still_rejected()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			void pair<T, U>(sizeof(T), sizeof(U)) { }
			void wrong<T, U>(sizeof(T), sizeof(U)) { pair<U, T>(sizeof_T, sizeof_U); }
			""");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("requires evidence for exact operand"));
	}
}
