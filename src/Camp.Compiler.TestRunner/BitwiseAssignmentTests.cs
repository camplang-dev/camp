using System.Collections.Generic;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class BitwiseAssignmentTests
{
	[Theory]
	[MemberData(nameof(BooleanWrites))]
	public void Bitwise_assignment_rejects_boolean_storage_and_properties(string declarations, string target, string op)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			{{declarations}}
			void test(Holder* holder)
			{
				bool flag = true;
				fixed bool[1] flags = [true];
				{{target}} {{op}} false;
			}
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		AnalysisDiagnostic diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Equal("Bitwise operators require integral operands, not 'bool' and 'bool'.", diagnostic.Message);
		TokenRange range = Assert.IsType<TokenRange>(diagnostic.Range);
		Assert.Equal(6, range.StartLineNumber);
		Assert.Equal(2, range.StartColumn);
	}

	public static IEnumerable<object[]> BooleanWrites()
	{
		(string Declarations, string Target)[] targets =
		[
			("class Holder { }", "flag"),
			("class Holder { }", "flags[0]"),
			("class Holder { bool value; }", "holder.value"),
			("class Holder { bool getFlag() => true; void setFlag(bool value) { } }", "holder.Flag"),
			("class Holder { bool getFlag(int index) => true; void setFlag(int index, bool value) { } }", "holder.Flag[0]"),
			("class Holder { } bool getFlag(const Holder* this) => true; void setFlag(Holder* this, bool value) { }", "holder.Flag"),
			("class Holder { } static class Values { static bool getFlag() => true; static void setFlag(bool value) { } }", "Values.Flag")
		];
		foreach ((string declarations, string target) in targets)
			foreach (string op in new[] { "&=", "|=", "^=" })
				yield return [declarations, target, op];
	}

	[Theory]
	[InlineData("bool", "true", "&=", "1")]
	[InlineData("int", "1", "|=", "true")]
	[InlineData("bool", "true", "<<=", "1")]
	[InlineData("bool", "true", ">>=", "1")]
	[InlineData("double", "1.5", "^=", "2.5")]
	[InlineData("double", "1.5", "<<=", "1")]
	public void Both_compound_operands_must_be_integral(string type, string initial, string op, string value)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void test() {{ {type} target = {initial}; target {op} {value}; }}");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.StartsWith("Bitwise operators require integral operands"));
	}

	[Theory]
	[InlineData("byte")]
	[InlineData("sbyte")]
	[InlineData("short")]
	[InlineData("ushort")]
	[InlineData("int")]
	[InlineData("uint")]
	[InlineData("long")]
	[InlineData("ulong")]
	[InlineData("nint")]
	[InlineData("nuint")]
	public void Integral_compound_assignments_remain_valid(string type)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			class Holder { {{type}} getValue() => 1; void setValue({{type}} value) { } }
			void test(Holder* holder)
			{
				{{type}} value = 1;
				value &= 1; value |= 2; value ^= 3; value <<= 1; value >>= 1;
				holder.Value &= 1; holder.Value |= 2; holder.Value ^= 3;
			}
			"""));
	}

	[Fact]
	public void Ordinary_boolean_assignment_and_logical_operators_remain_valid()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			class Holder { bool getFlag() => true; void setFlag(bool value) { } }
			void test(Holder* holder) { bool flag = true; flag = false; holder.Flag = flag && !false || true; }
			"""));
	}

	[Theory]
	[InlineData("&")]
	[InlineData("|")]
	[InlineData("^")]
	public void Plain_bitwise_operators_keep_the_same_boolean_operand_diagnostic(string op)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void test() {{ bool left = true; bool right = false; auto result = left {op} right; }}");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		AnalysisDiagnostic diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Equal("Bitwise operators require integral operands, not 'bool' and 'bool'.", diagnostic.Message);
	}

	[Fact]
	public void Enum_bitwise_assignments_remain_valid()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			enum Bits { NONE = 0, A = 1, B = 2 }
			void test() { Bits mask = A; mask |= B; mask &= A; mask ^= B; }
			"""));
	}
}
