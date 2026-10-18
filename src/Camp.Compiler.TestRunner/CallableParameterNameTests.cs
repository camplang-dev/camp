using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CallableParameterNameTests
{
	[Theory]
	[InlineData("fn", "arg0")]
	[InlineData("delegate", "arg1")]
	[InlineData("once", "arg1")]
	public void Written_names_survive_local_parameter_field_and_auto_surfaces(string kind, string unnamed)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			int weigh(int value, int weight) => value * weight;
			struct Holder { {{kind}} int(int, int scale) callback; }
			int parameter({{kind}} int(int, int scale) callback) => callback(scale: 3, {{unnamed}}: 7);
			int field(Holder* holder) => holder.callback(scale: 3, {{unnamed}}: 7);
			int local()
			{
				{{kind}} int(int, int scale) callback = (int value, int weight) => value * weight;
				auto copy = callback;
				return copy(scale: 3, {{unnamed}}: 7);
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}
}
