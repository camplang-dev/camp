using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class AscribedCallableEmissionTests
{
	[Theory]
	[InlineData("in void<int>", "in int", "value")]
	[InlineData("in int", "in void<int>", "value")]
	[InlineData("void<int>*", "int*", "*value")]
	[InlineData("void<int>[]", "int[]", "value[0]")]
	public void Compatible_ascribed_symbols_initialize_the_nominal_carrier(string carrier, string input, string result)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype fn int Reader({{carrier}} value);
			int read({{input}} value): Reader => {{result}};
			void use() { Reader callback = read; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-ascribed-callable-");
		try
		{
			CEmissionResult emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "ascribed_callable" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			Assert.Contains("Reader callback = (Reader)read", string.Join("\n", emission.GeneratedFiles.Select(File.ReadAllText)));
		}
		finally { directory.Delete(recursive: true); }
	}
}
