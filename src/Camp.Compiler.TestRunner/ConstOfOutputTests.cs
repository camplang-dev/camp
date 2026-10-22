using Xunit;

namespace Camp.Compiler.Tests;

public class ConstOfOutputTests
{
	[Theory]
	[InlineData("", "", false, true)]
	[InlineData("", "const ", false, true)]
	[InlineData("const ", "const ", false, true)]
	[InlineData("const ", "", false, false)]
	[InlineData("", "", true, true)]
	[InlineData("", "const ", true, true)]
	[InlineData("const ", "const ", true, true)]
	[InlineData("const ", "", true, false)]
	public void Output_uses_substituted_storage_conversion(string sourceConst, string outputConst, bool outputFirst, bool accepted)
	{
		string parameters = outputFirst ? "out constof(source) byte* result, const byte[] source" : "const byte[] source, out constof(source) byte* result";
		string call = outputFirst ? "first(out result, items);" : "first(items, out result);";
		var compilation = SemanticCompiler.CompileLowered(("constof_output.camp", $$"""
			void first({{parameters}}) { result = source.elements; }
			void run({{sourceConst}}byte[] items) { {{outputConst}}byte* result = null; {{call}} }
			"""));
		if (accepted) SemanticCompiler.AssertNoDiagnostics(compilation);
		else Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("Out argument cannot convert"));
	}
}
