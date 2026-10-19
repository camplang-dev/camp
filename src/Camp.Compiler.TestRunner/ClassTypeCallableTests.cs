using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ClassTypeCallableTests
{
	[Theory]
	[InlineData("void use(fn classtype*() source) {}")]
	[InlineData("fn classtype*() make() { return default; }")]
	[InlineData("void run() { fn classtype*() source = default; }")]
	[InlineData("void use(fn void(classtype*) sink) {}")]
	[InlineData("void use(delegate classtype*() source) {}")]
	[InlineData("void use(once classtype*() source) {}")]
	[InlineData("void use(fn fn classtype*()() source) {}")]
	[InlineData("void run() { fn const classtype*() source = default; }")]
	public void Callable_ABI_types_contain_the_concrete_owner(string method)
	{
		var compilation = SemanticCompiler.CompileLowered("class Control { int value; " + method + " }");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-class-callable-");
		try
		{
			CEmissionResult emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "class_callable" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			Assert.DoesNotContain("classtype", string.Join("\n", emission.GeneratedFiles.Select(File.ReadAllText)));
		}
		finally { directory.Delete(recursive: true); }
	}

	[Fact]
	public void Inherited_callable_results_rebind_only_the_class_relative_positions()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			class Control { int value; fn classtype*(Control*) make() { return default; } }
			class Button : Control {}
			void check(Button* button) { fn Button*(Control*) source = button.make(); }
			"""));
	}
}
