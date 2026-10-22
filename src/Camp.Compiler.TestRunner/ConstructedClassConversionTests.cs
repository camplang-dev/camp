using Xunit;

namespace Camp.Compiler.Tests;

public class ConstructedClassConversionTests
{
	const string Types = """
		class Box<T> { int tag; }
		class Mid<T>: Box<T> { int extra; }
		class IntBox: Mid<int> {}
		class Other: Box<uint> {}
		""";

	[Theory]
	[InlineData("IntBox", "Box<int>")]
	[InlineData("IntBox", "Mid<int>")]
	[InlineData("Mid<int>", "Box<int>")]
	[InlineData("Other", "Box<uint>")]
	public void Exact_constructed_base_is_implicit(string source, string target)
	{
		SemanticCompiler.AssertNoDiagnostics(Compile(source, target));
	}

	[Theory]
	[InlineData("IntBox", "Box<uint>")]
	[InlineData("IntBox", "Mid<uint>")]
	[InlineData("Mid<int>", "Box<uint>")]
	[InlineData("Other", "Box<int>")]
	[InlineData("Box<int>", "Box<uint>")]
	public void Different_construction_is_rejected(string source, string target)
	{
		var compilation = Compile(source, target);
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("cannot convert"));
	}

	static SemanticCompilation Compile(string source, string target) => SemanticCompiler.CompileLowered(("constructed_base.camp",
		Types + $"void run({source}* source) {{ {target}* target = source; }}"));
}
