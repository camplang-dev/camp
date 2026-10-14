using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ErasedOptionalFunctionPointerTests
{
	[Theory]
	[InlineData("T", "void (* look)(int arg0, void *__result)")]
	public void Declared_erased_callback_results_use_payload_storage(string result, string expected)
	{
		AssertEmitted($$"""
			{{result}} choose<T>(fn {{result}}(int) look, int index, sizeof(T)) => look(index);
			""", expected);
	}

	[Fact]
	public void Generic_callable_fields_use_the_resolved_erased_result()
	{
		AssertEmitted("struct Reader<T> { fn T(int) look; }", "void (* look)(int arg0, void *__result)");
	}

	[Fact]
	public void Lowered_interface_entries_do_not_add_result_storage_twice()
	{
		AssertEmitted("interface Reader { void<int?> find(int index); }", "void (* find)(void* ctx, int index, void *__result)");
	}

	[Fact]
	public void Ordinary_optional_callbacks_keep_their_payload_and_presence_shape()
	{
		AssertEmitted("int? choose(fn int?(int) look, int index) => look(index);", "int (* look)(int arg0, bool *specified)");
	}

	static void AssertEmitted(string source, string expected)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(source);
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-erased-fn-");
		try
		{
			CEmissionResult emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "erased_fn" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			string code = string.Join("\n", emission.GeneratedFiles.Select(File.ReadAllText));
			Assert.Contains(expected, code);
		}
		finally { directory.Delete(recursive: true); }
	}
}
