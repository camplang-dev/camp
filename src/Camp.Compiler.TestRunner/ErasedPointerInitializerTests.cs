using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ErasedPointerInitializerTests
{
	[Theory]
	[InlineData("int?", "value", "", false)]
	[InlineData("int?", "value", "", true)]
	[InlineData("int[]", "elements", "", false)]
	[InlineData("int[]", "elements", "", true)]
	[InlineData("int?", "value", "const ", true)]
	public void Brace_fields_expand_erased_storage_into_component_addresses(string payload, string firstMember, string qualifier, bool named)
	{
		string item = named ? ".where = source" : "source";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			struct Handle { {{qualifier}}{{payload}}* where; }
			void use({{qualifier}}void<{{payload}}>* source) { Handle handle = { {{item}} }; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-erased-pointer-initializer-");
		try
		{
			CEmissionResult emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "pointer_initializer" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			string code = string.Join("\n", emission.GeneratedSourceFiles.Select(File.ReadAllText));
			Assert.Contains("." + firstMember, code);
			Assert.DoesNotContain(".where_specified = true", code);
		}
		finally { directory.Delete(recursive: true); }
	}
}
