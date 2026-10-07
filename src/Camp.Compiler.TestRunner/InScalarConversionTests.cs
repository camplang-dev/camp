using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class InScalarConversionTests
{
	[Theory]
	[InlineData("short", "int", "short source = -1;", "&(int){source}")]
	[InlineData("short", "int", "", "&(int){source}")]
	[InlineData("in short", "int", "", "&(int){(*source)}")]
	[InlineData("int", "long", "", "&(int64_t){source}")]
	[InlineData("int", "float", "", "&(float){source}")]
	[InlineData("float", "double", "", "&(double){source}")]
	[InlineData("in int", "int", "", "consume(source)")]
	public void Scalar_in_arguments_convert_before_storage_is_forwarded(string sourceType, string destinationType, string local, string expected)
	{
		string parameter = local.Length == 0 ? sourceType + " source" : "";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			{{destinationType}} consume(in {{destinationType}} value) => value;
			{{destinationType}} forward({{parameter}}) { {{local}} return consume(source); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-in-conversion-");
		try
		{
			CEmissionResult emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "in_conversion" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			string code = string.Join("\n", emission.GeneratedSourceFiles.Select(File.ReadAllText));
			Assert.Contains(expected, code);
		}
		finally { directory.Delete(recursive: true); }
	}
}
