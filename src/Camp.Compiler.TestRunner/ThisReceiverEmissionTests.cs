using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ThisReceiverEmissionTests
{
	[Theory]
	[InlineData("struct View { int value; } int read(View this) { return this.value; }", "this.value")]
	[InlineData("struct View { int value; } int read(View* this) { return this.value; }", "this->value")]
	[InlineData("struct View { int value; } int read(in View this) { return this.value; }", "(*this).value")]
	[InlineData("class View { int value; int read() { return this.value; } }", "this->value")]
	[InlineData("class View { int value; int read(escaped this) { return this.value; } }", "this->value")]
	[InlineData("virtual class View { int value; virtual int read(escaped this) { return this.value; } }", "this->value")]
	public void Member_access_matches_the_concrete_receiver_storage(string source, string memberAccess)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(source);
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-this-receiver-");
		try
		{
			CEmissionResult emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "receiver" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			string code = string.Join("\n", emission.GeneratedFiles.Where(static path => Path.GetExtension(path) == ".c").Select(File.ReadAllText));
			string receivers = string.Join("\n", SemanticCompiler.Descendants<FunctionDefinition>(compilation.Module)
				.SelectMany(static function => function.Parameters.Select(parameter => $"{function.Name}: {parameter.Name}/{parameter.Symbol} {parameter.ResolvedType} {parameter.Type?.GetType().Name} {parameter.Type?.ResolvedType}")));
			Assert.True(code.Contains("return " + memberAccess + ";"), code + "\n" + receivers);
			if (source.Contains("virtual"))
				Assert.Contains("this->_vt->read(this)", code);
		}
		finally { directory.Delete(recursive: true); }
	}
}
