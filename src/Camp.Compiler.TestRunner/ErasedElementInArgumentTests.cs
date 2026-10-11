using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ErasedElementInArgumentTests
{
	[Theory]
	[InlineData("int", "[]", "slots[1]")]
	[InlineData("int?", "[]", "slots[1]")]
	[InlineData("int[]?", "[]", "slots[1]")]
	[InlineData("Predicate", "[]", "slots[1]")]
	[InlineData("int?", "*", "slots[1]")]
	[InlineData("int?", "[]", "(slots[1])")]
	public void Erased_elements_forward_the_storage_address_to_in_parameters(string payload, string container, string argument)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype delegate int Predicate(int value);
			int read(in void<{{payload}}> value) => 0;
			int test(void<{{payload}}>{{container}} slots) => read({{argument}});
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-erased-element-in-");
		try
		{
			CEmissionResult emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "erased_element_in" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			string code = string.Join("\n", emission.GeneratedSourceFiles.Select(File.ReadAllText));
			Assert.Contains("read(&((", code);
			Assert.Contains(")(slots))[1]", code);
			Assert.DoesNotContain("read(&(__camp_erased_storage", code);
		}
		finally { directory.Delete(recursive: true); }
	}
}
