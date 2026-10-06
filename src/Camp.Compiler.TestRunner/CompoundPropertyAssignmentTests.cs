using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CompoundPropertyAssignmentTests
{
	[Theory]
	[InlineData("+=", BinaryOperator.Add)]
	[InlineData("-=", BinaryOperator.Subtract)]
	[InlineData("*=", BinaryOperator.Multiply)]
	[InlineData("/=", BinaryOperator.Divide)]
	[InlineData("%=", BinaryOperator.Modulo)]
	[InlineData("&=", BinaryOperator.BitwiseAnd)]
	[InlineData("|=", BinaryOperator.BitwiseOr)]
	[InlineData("^=", BinaryOperator.BitwiseXor)]
	[InlineData("<<=", BinaryOperator.LeftShift)]
	[InlineData(">>=", BinaryOperator.RightShift)]
	public void Compound_property_write_reads_and_applies_the_operator(string op, BinaryOperator expected)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			class Holder { int value; int getMask() => this.value; void setMask(int value) { this.value = value; } }
			int update(Holder* h) { return h.Mask {{op}} 2; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		Assert.Single(SemanticCompiler.Descendants<BinaryExpression>(compilation.Module), binary => binary.Operator == expected);
		Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module), static call => Calls(call, "getMask"));
		Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module), static call => Calls(call, "setMask"));
	}

	[Theory]
	[InlineData("class Holder { int value; int getMask(int index) => this.value; void setMask(int index, int value) { this.value = value; } }", "h.Mask[0]")]
	[InlineData("class Holder { int value; int getMask() => this.value; void setMask(int value) { this.value = value; } }", "(h.Mask)")]
	[InlineData("class Holder { static int getMask() => 7; static void setMask(int value) { } }", "Holder.Mask")]
	[InlineData("class Holder { int value; } int getMask(const Holder* this) => this.value; void setMask(Holder* this, int value) { this.value = value; }", "h.Mask")]
	[InlineData("class Holder { int value; int getMask(int[] indices) => this.value; void setMask(int[] indices, int value) { this.value = value; } }", "h.Mask[default]")]
	public void Compound_writes_support_property_accessor_surfaces(string declarations, string target)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			{{declarations}}
			void update(Holder* h) { {{target}} ^= 1; }
			"""));
	}

	[Theory]
	[InlineData("h.Mask")]
	[InlineData("h.Mask[0]")]
	public void Compound_write_requires_a_getter(string target)
	{
		string parameters = target.Contains('[') ? "int index, int value" : "int value";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			class Holder { void setMask({{parameters}}) { } }
			void update(Holder* h) { {{target}} ^= 1; }
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Contains(compilation.AnalysisDiagnostics, static diagnostic => diagnostic.Message.Contains("not readable"));
	}

	static bool Calls(CallExpression call, string name)
	{
		return call.Target is MethodReferenceExpression method && method.Candidates.Any(function => function.Name == name);
	}
}
