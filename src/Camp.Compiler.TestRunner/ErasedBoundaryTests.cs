using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ErasedBoundaryTests
{
	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void Stack_storage_must_be_reassigned_after_suspension(bool async, bool reassign)
	{
		string source = async ? """
			class Resumer { void resumeAsync(escaped once void() continuation) { continuation(); } }
			async int tick(@awaitwith Resumer* resumer) { return 1; }
			async int run(@awaitwith Resumer* resumer) {
			stackalloc void<int?> scratch = 2;
			int ignored = await tick(resumer);
			REASSIGN
			return scratch.value;
			}
			""" : """
			struct iter int values() {
			stackalloc void<int?> scratch = 2;
			yield scratch.value;
			REASSIGN
			yield scratch.value;
			}
			""";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(source.Replace("REASSIGN", reassign ? "scratch = 4;" : ""));
		if (reassign) SemanticCompiler.AssertNoDiagnostics(compilation);
		else Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("unassigned after suspension"));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Stack_address_cannot_be_reused_from_retained_state(bool async)
	{
		string source = async ? """
			class Resumer { void resumeAsync(escaped once void() continuation) { continuation(); } }
			async int tick(@awaitwith Resumer* resumer) { return 1; }
			async int run(@awaitwith Resumer* resumer) {
			stackalloc void<int?> scratch = 2;
			void<int?>* saved = &scratch;
			int ignored = await tick(resumer);
			return saved.value;
			}
			""" : """
			struct iter int values() {
			stackalloc void<int?> scratch = 2;
			void<int?>* saved = &scratch;
			yield 1;
			yield saved.value;
			}
			""";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(source);
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("unassigned after suspension") || diagnostic.Contains("stackalloc"));
	}

	[Fact]
	public void Loop_backedge_does_not_reuse_stack_value_from_previous_resume()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			struct iter int values(int count) {
			stackalloc void<int?> scratch = 2;
			while (count-- > 0) { yield scratch.value; }
			}
			""");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("unassigned after suspension"));
	}

	[Fact]
	public void Stack_storage_is_not_part_of_exported_iterator_state()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			export struct iter int values() {
			stackalloc void<int?> scratch = 2;
			yield scratch.value;
			{ scratch = 4; }
			yield scratch.value;
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		StructDefinition state = compilation.Module.Definitions.OfType<StructDefinition>().Single(type => type.Name == "valuesIter");
		Assert.DoesNotContain(state.Fields, field => field.Name.Contains("scratch"));
	}

	[Fact]
	public void Accessible_annotations_and_generic_identities_remain_typed()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			public struct PackageValue { int value; }
			struct Secret { int value; }
			alias HiddenAlias = Secret;
			export void<HiddenAlias>* aliasValue(void<HiddenAlias>* p) => p;
			extern escaped void* malloc(nuint size);
			extern void free(void* pointer);
			export extern class Box<T: any>;
			export void<PackageValue>* value(void<PackageValue>* p) => p;
			export void<int?>? optional() => 7;
			export Box<void<Secret>*>* box(Box<void<Secret>*>* p) => p;
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		using StringWriter publicWriter = new();
		BindableNodeCodeSerializer.Serialize(compilation.Module, publicWriter, new BindableNodeCodeSerializerOptions { ApiHeader = true, ApiSurface = CampApiSurfaceKind.Public });
		Assert.Contains("void<PackageValue>*", publicWriter.ToString());
		Assert.Contains("Box<void*>*", publicWriter.ToString());
		Assert.DoesNotContain("Secret", publicWriter.ToString());
		Assert.DoesNotContain("HiddenAlias", publicWriter.ToString());
		Assert.Contains("void<int?>? optional()", publicWriter.ToString());
		SemanticCompilation consumer = SemanticCompiler.CompileLowered(publicWriter.ToString());
		Assert.True(consumer.Diagnostics.Count == 0, publicWriter.ToString() + "\n" + string.Join("\n", consumer.Diagnostics));
		using StringWriter exportWriter = new();
		BindableNodeCodeSerializer.Serialize(compilation.Module, exportWriter, new BindableNodeCodeSerializerOptions { ApiHeader = true });
		Assert.DoesNotContain("PackageValue", exportWriter.ToString());
		Assert.DoesNotContain("Secret", exportWriter.ToString());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Hidden_annotations_project_without_relaxing_ordinary_visibility(bool lower)
	{
		string source = """
			struct Secret { int value; }
			export void<Secret>* identity(void<Secret>* p) => p;
			export const void<Secret>* readOnly(const void<Secret>* p) => p;
			export int read(in void<Secret> p) => p.value;
			export void fill(out void<Secret> p) { Secret s = { 7 }; p = s; }
			export void<Secret>? find(int index) { if (index != 0) return default; Secret s = { 7 }; return s; }
			export void array(void<Secret>[] values) { }
			export struct OpaqueView { void<Secret>[] buffer; }
			export void optionalInput(in void<Secret>? value) { }
			export void optionalOutput(out void<Secret>? value) { value = default; }
			export newtype delegate void<Secret>? Lookup(int index);
			export void callback(delegate void<Secret>?(int index) action) { }
			""";
		SemanticCompilation compilation = lower ? SemanticCompiler.CompileLowered(source) : SemanticCompiler.CompileDeclarations(source);
		SemanticCompiler.AssertNoDiagnostics(compilation);
		using StringWriter writer = new();
		BindableNodeCodeSerializer.Serialize(compilation.Module, writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
		string api = writer.ToString();
		Assert.DoesNotContain("Secret", api);
		Assert.Contains("void* identity(void* p)", api);
		Assert.Contains("const void* readOnly(const void* p)", api);
		Assert.Contains("int read(const void* p)", api);
		Assert.Contains("void fill(void* p)", api);
		Assert.Contains("bool find(int index, void* __result)", api);
		Assert.Contains("void array(void* values, nuint values_length)", api);
		Assert.Contains("void* buffer;", api);
		Assert.Contains("nuint buffer_length;", api);
		Assert.Contains("optionalInput(const void* value, in bool value_specified)", api);
		Assert.Contains("optionalOutput(void* value, out bool value_specified)", api);
		Assert.Contains("newtype delegate bool Lookup(int index, void* __result)", api);
		Assert.Contains("delegate bool(int index, void* __result) action", api);
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(api));
		string metadata = MetadataJsonSerializer.Serialize(compilation.Compilation, MetadataVisibility.Export);
		Assert.DoesNotContain("Secret", metadata);
		Assert.Contains("void<Secret>", MetadataJsonSerializer.Serialize(compilation.Compilation, MetadataVisibility.All).Replace("\\u003C", "<").Replace("\\u003E", ">"));
		Assert.Contains(SemanticCompiler.CompileLowered("struct Secret { int value; } export Secret* bad(Secret* p) => p;").Diagnostics,
			diagnostic => diagnostic.Contains("exposes non-exported type"));
	}
}
