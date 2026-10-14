using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class GenericInterfaceInArgumentTests
{
	[Theory]
	[InlineData("in int", "x", "scale(s, x)")]
	[InlineData("int", "x", "scale(s, &x)")]
	[InlineData("int", "5", "scale(s, &(int){5})")]
	public void Witness_dispatch_preserves_context_and_in_argument_storage(string parameter, string argument, string expected)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			interface IScale { int scale(in int x); }
			int viaGeneric<T>(T* s, {{parameter}} x, vtableof(T: IScale)) => s.scale({{argument}});
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-witness-in-");
		try
		{
			CEmissionResult emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "witness_in" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			string code = string.Join("\n", emission.GeneratedSourceFiles.Select(File.ReadAllText));
			Assert.Contains("vtableof_T_IScale->" + expected, code);
		}
		finally { directory.Delete(recursive: true); }
	}
}
