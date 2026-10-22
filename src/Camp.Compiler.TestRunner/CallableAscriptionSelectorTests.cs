using Xunit;

namespace Camp.Compiler.Tests;

public class CallableAscriptionSelectorTests
{
	[Theory]
	[InlineData("const char[] key, overload int value", true, "")]
	[InlineData("const char[] key, overload int other", false, "overload selector name")]
	[InlineData("const char[] key, int value", false, "overload spelling")]
	[InlineData("const char[] name, overload int value", false, "pre-selector parameter shape")]
	[InlineData("int key, overload int value", false, "pre-selector parameter shape")]
	[InlineData("in const char[] key, overload int value", false, "pre-selector parameter shape")]
	[InlineData("overload int value, const char[] key", false, "overload selector position")]
	public void Ascription_preserves_selector_contract(string parameters, bool accepted, string diagnostic)
	{
		var compilation = SemanticCompiler.CompileLowered(("ascription_selector.camp", $$"""
			newtype fn void Setter(const char[] key, overload int value);
			void candidate({{parameters}}): Setter {}
			"""));
		if (accepted) SemanticCompiler.AssertNoDiagnostics(compilation);
		else Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("must preserve the target's " + diagnostic));
	}

	[Fact]
	public void Ordinary_target_rejects_overload_candidate()
	{
		var compilation = SemanticCompiler.CompileLowered("""
			newtype fn void Setter(const char[] key, int value);
			void candidate(const char[] key, overload int value): Setter {}
			""");
		Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("must preserve the target's overload spelling"));
	}

	[Fact]
	public void Constructed_pre_selector_type_is_substituted()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			newtype fn void Setter<T>(T* key, overload int value);
			void candidate(byte* key, overload int value): Setter<byte> {}
			"""));
	}
}
