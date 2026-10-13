using System.IO;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class StructInterfaceApiTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Struct_api_preserves_interface_conformance_before_and_after_lowering(bool lower)
	{
		const string source = """
			export interface Reader { int read(); }
			export interface Writer { void write(int value); }
			export struct Cell: Reader, Writer
			{
				int value;
				export int read(): Reader => this.value;
				export void write(int value): Writer { this.value = value; }
			}
			""";
		SemanticCompilation producer = lower ? SemanticCompiler.CompileLowered(source) : SemanticCompiler.CompileDeclarations(source);
		SemanticCompiler.AssertNoDiagnostics(producer);
		using StringWriter writer = new();
		Module surface = new();
		surface.Definitions.Add(SemanticCompiler.Type(producer, "Cell"));
		BindableNodeCodeSerializer.Serialize(surface, writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
		string api = writer.ToString();
		Assert.Contains("struct Cell : Reader, Writer", api);
		SemanticCompilation consumer = SemanticCompiler.CompileLowered("export interface Reader { int read(); } export interface Writer { void write(int value); }\n" + api + """
			int use<T: implements Reader>(T* value, vtableof(T: Reader)) => value.read();
			void test()
			{
				Cell cell = { 5 };
				Reader* reader = &cell;
				Writer* writable = &cell;
				writable.write(7);
				int value = reader.read() + use<Cell>(&cell);
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(consumer);
	}

	[Fact]
	public void Lowered_interface_parameters_retain_source_pointer_depth_in_api()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			export interface Reader { int read() = fallback; }
			export int fallback(Reader* source) => 99;
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		using StringWriter writer = new();
		BindableNodeCodeSerializer.Serialize(SemanticCompiler.Function(compilation, "fallback"), writer,
			new BindableNodeCodeSerializerOptions { ApiHeader = true });
		Assert.Contains("fallback(Reader* source)", writer.ToString());
	}
}
