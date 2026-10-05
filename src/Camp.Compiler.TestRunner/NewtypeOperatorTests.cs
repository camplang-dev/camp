using System.Collections.Generic;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class NewtypeOperatorTests
{
	[Theory]
	[MemberData(nameof(ForbiddenOperators))]
	public void Numeric_carriers_do_not_grant_newtype_operators(string carrier, string expression)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype Meters: {{carrier}};
			void test(Meters a, Meters b)
			{
				{{expression}};
			}
			""");
		AssertRejected(compilation);
	}

	public static IEnumerable<object[]> ForbiddenOperators()
	{
		foreach (string carrier in new[] { "int", "uint", "double" })
		{
			foreach (string op in new[] { "+", "-", "*", "/", "%", "<", "<=", ">", ">=", "&", "|", "^", "<<", ">>", "&&", "||" })
				yield return [carrier, $"auto result = a {op} b"];
			foreach (string expression in new[] { "+a", "-a", "~a", "++a", "--a", "a++", "a--" })
				yield return [carrier, $"auto result = {expression}"];
			foreach (string op in CompoundOperators)
				yield return [carrier, $"a {op} b"];
		}
	}

	static readonly string[] CompoundOperators = ["+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>="];

	[Theory]
	[MemberData(nameof(MixedOperands))]
	public void Either_newtype_operand_blocks_binary_and_compound_operations(string expression)
	{
		AssertRejected(SemanticCompiler.CompileLowered($$"""
			newtype Meters: int;
			void test(Meters a, int raw) { {{expression}}; }
			"""));
	}

	public static IEnumerable<object[]> MixedOperands()
	{
		foreach (string op in new[] { "+", "-", "*", "/", "%", "<", "<=", ">", ">=", "&", "|", "^", "<<", ">>" })
		{
			yield return [$"auto result = a {op} raw"];
			yield return [$"auto result = raw {op} a"];
		}
		foreach (string op in CompoundOperators)
		{
			yield return [$"a {op} raw"];
			yield return [$"raw {op} a"];
		}
	}

	[Theory]
	[MemberData(nameof(PropertyWrites))]
	public void Property_operators_do_not_bypass_newtype_validation(string target, string expression)
	{
		AssertRejected(SemanticCompiler.CompileLowered($$"""
			newtype Meters: int;
			class Holder
			{
				Meters getValue() => (Meters)1;
				void setValue(Meters value) { }
				Meters getItem(int index) => (Meters)1;
				void setItem(int index, Meters value) { }
			}
			void test(Holder* holder, Meters a) { {{expression.Replace("target", target)}}; }
			"""));
	}

	public static IEnumerable<object[]> PropertyWrites()
	{
		foreach (string target in new[] { "holder.Value", "holder.Item[0]" })
		{
			foreach (string op in CompoundOperators)
				yield return [target, $"target {op} a"];
			foreach (string update in new[] { "++target", "--target", "target++", "target--" })
				yield return [target, update];
		}
	}

	[Theory]
	[InlineData("int")]
	[InlineData("uint")]
	[InlineData("double")]
	[InlineData("void*")]
	public void Same_newtype_equality_and_assignment_remain_valid(string carrier)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			newtype Value: {{carrier}};
			bool test(Value a, const Value b)
			{
				Value copy = b;
				copy = a;
				return copy == b || copy != b;
			}
			"""));
	}

	[Theory]
	[InlineData("==", "Meters", "Other")]
	[InlineData("!=", "Meters", "Other")]
	[InlineData("==", "Meters", "int")]
	[InlineData("!=", "int", "Meters")]
	public void Equality_rejects_different_nominal_types_or_carriers(string op, string left, string right)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype Meters: int;
			newtype Other: int;
			bool test({{left}} a, {{right}} b) => a {{op}} b;
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Equal($"Newtype equality requires operands of the same newtype, not '{left}' and '{right}'.", Assert.Single(compilation.AnalysisDiagnostics.Distinct()).Message);
	}

	[Fact]
	public void Aliases_and_namespaces_preserve_newtype_operator_restrictions()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			namespace Units;
			newtype Meters: int;
			alias Distance = Meters;
			void test(const Distance a, Distance b) { auto sum = a + b; }
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Equal(ExpectedDiagnostic("UnitsMeters"), Assert.Single(compilation.AnalysisDiagnostics.Distinct()).Message);
	}

	[Theory]
	[InlineData("void*")]
	public void Non_numeric_newtypes_also_reject_arithmetic(string carrier)
	{
		AssertRejected(SemanticCompiler.CompileLowered($"newtype Token: {carrier}; void test(Token a, Token b) {{ auto sum = a + b; }}"), "Token");
	}

	[Fact]
	public void Callable_newtype_also_rejects_arithmetic()
	{
		AssertRejected(SemanticCompiler.CompileLowered("newtype fn int Token(); void test(Token a, Token b) { auto sum = a + b; }"), "Token");
	}

	[Theory]
	[InlineData("a == default")]
	[InlineData("a != default")]
	[InlineData("default == a")]
	[InlineData("default != a")]
	[InlineData("(default) == a")]
	[InlineData("a != (default)")]
	public void Default_equality_is_target_typed_as_the_newtype(string expression)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($"newtype Meters: int; bool test(const Meters a) => {expression};"));
	}

	[Fact]
	public void Carrier_operations_and_newtype_container_operations_remain_valid()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			newtype Meters: int;
			class Holder { Meters getValue() => (Meters)1; void setValue(Meters value) { } }
			void test(Holder* holder, Meters a, Meters b, Meters* left, Meters* right, Meters[] items)
			{
				Meters result = (Meters)((int)a + (int)b);
				result = (Meters)(-(int)result);
				holder.Value = result;
				auto same = holder.Value == result;
				auto address = &result;
				auto pointers = left == right;
				items[0] = result;
				Meters first = items[0];
			}
			"""));
	}

	static string ExpectedDiagnostic(string type) => $"Operator requires explicit conversion of newtype '{type}' to its underlying type.";

	static void AssertRejected(SemanticCompilation compilation, string type = "Meters")
	{
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Equal(ExpectedDiagnostic(type), Assert.Single(compilation.AnalysisDiagnostics.Distinct()).Message);
	}
}
