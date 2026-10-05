using System.Collections.Generic;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class UpdateOperandTests
{
	[Theory]
	[MemberData(nameof(InvalidUpdates))]
	public void Prefix_and_postfix_updates_reject_non_numeric_operands(string type, string update)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			struct Box { int value; }
			void test({{type}} value)
			{
				{{update}};
			}
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		AnalysisDiagnostic diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Equal($"Update operator requires a numeric operand, not '{type}'.", diagnostic.Message);
		TokenRange range = Assert.IsType<TokenRange>(diagnostic.Range);
		Assert.Equal(4, range.StartLineNumber);
		Assert.Equal(update.StartsWith("++") || update.StartsWith("--") ? 4 : 2, range.StartColumn);
	}

	public static IEnumerable<object[]> InvalidUpdates()
	{
		foreach (string type in new[] { "bool", "int*", "Box", "int[]", "int?", "fn int()" })
			foreach (string update in new[] { "++value", "--value", "value++", "value--" })
				yield return [type, update];
	}

	[Theory]
	[InlineData("++holder.Flag")]
	[InlineData("--holder.Flag")]
	[InlineData("holder.Flag++")]
	[InlineData("holder.Flag--")]
	public void Boolean_property_updates_are_rejected(string update)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			class Holder { bool getFlag() => true; void setFlag(bool value) { } }
			void test(Holder* holder) { {{update}}; }
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Equal("Update operator requires a numeric operand, not 'bool'.", Assert.Single(compilation.AnalysisDiagnostics.Distinct()).Message);
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
	[InlineData("float")]
	[InlineData("double")]
	[InlineData("achar")]
	[InlineData("char")]
	[InlineData("wchar")]
	[InlineData("uchar")]
	public void Numeric_updates_remain_valid(string type)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($"void test({type} value) {{ ++value; --value; value++; value--; }}"));
	}

	[Fact]
	public void Enum_updates_remain_valid()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("enum Count { ZERO, ONE } void test(Count value) { ++value; --value; value++; value--; }"));
	}
}
