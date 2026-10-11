using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CallableOptionalParameterTests
{
	[Theory]
	[InlineData("delegate", "int?", 3)]
	[InlineData("once", "int?", 3)]
	[InlineData("delegate", "int[]?", 4)]
	[InlineData("once", "int[]?", 4)]
	public void Context_callables_preserve_optional_components(string kind, string type, int count)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void use({kind} int({type}) measure, {type} value) {{ measure(value); }}");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module));
		Assert.Equal(count, call.Arguments.Count);
	}

	[Theory]
	[InlineData("int?", 2, false)]
	[InlineData("int?", 2, true)]
	[InlineData("int[]?", 3, false)]
	[InlineData("int[]?", 3, true)]
	[InlineData("delegate int()?", 3, false)]
	[InlineData("delegate int()?", 3, true)]
	public void Function_pointer_calls_preserve_optional_components(string type, int count, bool named)
	{
		string declaration = named ? $"newtype fn int Measure({type} value);" : "";
		string callable = named ? "Measure" : $"fn int({type})";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"{declaration} void use({callable} measure, {type} value) {{ measure(value); }}");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module));
		Assert.Equal(count, call.Arguments.Count);
	}
}
