using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class PropertyUpdateTests
{
	[Theory]
	[InlineData("++h.Value")]
	[InlineData("--h.Value")]
	[InlineData("h.Value++")]
	[InlineData("h.Value--")]
	[InlineData("(h.Value)++")]
	[InlineData("++h.Item[0]")]
	[InlineData("h.Item[0]--")]
	public void Updates_bind_one_getter_and_one_setter(string expression)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			struct Holder {
				int value;
				int getValue() => this.value;
				void setValue(int next) { this.value = next; }
				int getItem(int index) => this.value;
				void setItem(int index, int next) { this.value = next; }
			}
			int update(Holder* h) { return {{expression}}; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition update = SemanticCompiler.Function(compilation, "update");
		CallExpression[] calls = SemanticCompiler.Descendants<CallExpression>(update).ToArray();
		Assert.Equal(2, calls.Length);
		Assert.Single(calls, call => Calls(call, expression.Contains("Item") ? "getItem" : "getValue"));
		Assert.Single(calls, call => Calls(call, expression.Contains("Item") ? "setItem" : "setValue"));
	}

	[Theory]
	[InlineData("byte")]
	[InlineData("int")]
	[InlineData("uint")]
	[InlineData("double")]
	[InlineData("char")]
	[InlineData("Count")]
	public void Updates_preserve_the_numeric_property_type(string type)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			enum Count { ZERO, ONE }
			struct Holder { {{type}} getValue() => default; void setValue({{type}} next) {} }
			void update(Holder* h) { ++h.Value; --h.Value; h.Value++; h.Value--; }
			"""));
	}

	static bool Calls(CallExpression call, string name) => call.Target is MethodReferenceExpression method
		&& method.Candidates.Any(function => function.Name == name);
}
