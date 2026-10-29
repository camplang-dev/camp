using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class EnumStorageComparisonTests
{
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Configuration_flags_do_not_hide_target_enum_members(bool enabled)
	{
		ConfigurationFlagSet flags = new();
		Assert.True(flags.TryDeclare($"X64={enabled.ToString().ToLowerInvariant()}", false,
			ConfigurationFlagOwner.Target, "test", new()));
		var compilation = SemanticCompiler.CompileLowered(flags, ("test.camp", """
			enum Arch { UNKNOWN, X64 }
			Arch current() => X64;
			void accept(Arch value) { }
			void test(Arch value) {
				Arch copy = X64;
				copy = X64;
				accept(X64);
				bool equal = value == X64;
				switch (value) { case X64: break; default: break; }
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Fact]
	public void Ordinary_local_and_parameter_lookup_wins_over_target_enum_members()
	{
		var compilation = SemanticCompiler.CompileLowered("""
			enum Kind { A, B }
			Kind parameter(Kind A) { Kind value = A; return value; }
			Kind local() { Kind A = Kind.B; Kind value = A; return value; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		var parameter = SemanticCompiler.Function(compilation, "parameter");
		var parameterValue = Assert.Single(SemanticCompiler.Descendants<DeclarationStatement>(parameter));
		Assert.Same(parameter.Parameters[0], Assert.IsType<VariableReferenceExpression>(parameterValue.InitialValue).Variable);
		var locals = SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "local")).ToList();
		Assert.Same(locals[0].Target, Assert.IsType<VariableReferenceExpression>(locals[1].InitialValue).Variable);
	}

	[Theory]
	[InlineData("void test() { int A = 5; Kind value = A; }", "cannot convert 'int' to 'Kind'")]
	[InlineData("void test(int A) { Kind value = A; }", "cannot convert 'int' to 'Kind'")]
	[InlineData("int A = 5; void test() { Kind value = A; }", "cannot convert 'int' to 'Kind'")]
	[InlineData("class Holder { int A; void test() { Kind value = A; } }", "requires explicit 'this.'")]
	public void Ordinary_declarations_cannot_be_bypassed_by_target_enum_lookup(string source, string message)
	{
		var compilation = SemanticCompiler.CompileLowered("enum Kind { A, B } " + source);
		var diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Contains(message, diagnostic.Message);
		Assert.Equal("A", Assert.IsType<TokenRange>(diagnostic.Range).Value);
	}

	[Theory]
	[InlineData("globalColor == RED")]
	[InlineData("RED == globalColor")]
	[InlineData("globalColor != GREEN")]
	[InlineData("GREEN != globalColor")]
	[InlineData("Colors::value == RED")]
	[InlineData("RED == Colors::value")]
	[InlineData("(globalColor) == RED")]
	[InlineData("localColor == RED")]
	public void Comparisons_target_enum_members_from_local_and_global_storage(string comparison)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			enum Color: int { RED, GREEN }
			Color globalColor;
			namespace Colors { Color value; }
			bool test() { Color localColor = RED; return {{comparison}}; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}
}
