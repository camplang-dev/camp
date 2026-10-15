using System.IO;
using System.Linq;
using System.Text.Json;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ErasedBoundaryTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Generic_api_round_trip_preserves_constructed_references_and_capability_contracts(bool lower)
	{
		const string source = """
			extern escaped void* malloc(nuint size);
			export newtype fn T Map<T: int>(T value);
			export newtype delegate T Copy<T>(in T value, typenameof(T), sizeof(T));
			export U map<U: int>(U value): Map<U> => value;
			export interface Check<T: int> { bool check<V>(T value, V* other, typenameof(V)); }
			export interface Borrowed<T> { void touch(T* value); }
			export class Nested<W: int>: Borrowed<Map<W>*>
			{
				export void touch(void<Map<W>*>* value): Borrowed<Map<W>*> {}
			}
			export class Box<W: int>: Check<W>
			{
				export Box() {}
				export bool check<X>(W value, X* other, typenameof(X)): Check<W> => true;
				export V copy<V>(in V value, typenameof(V), sizeof(V)): Copy<V> => value;
			}
			""";
		// The result uses the method binder, not a coincidentally named newtype binder.
		string producerSource = source;
		SemanticCompilation producer = lower ? SemanticCompiler.CompileLowered(producerSource) : SemanticCompiler.CompileDeclarations(producerSource);
		SemanticCompiler.AssertNoDiagnostics(producer);
		using StringWriter writer = new();
		BindableNodeCodeSerializer.Serialize(producer.Compilation.Files[0].BindableTree!, writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
		string api = writer.ToString();
		Assert.Contains("Map<T: int>", api);
		Assert.Contains("Copy<T>(in T value, typenameof(T), sizeof(T))", api);
		Assert.Contains(": Check<W>", api);
		Assert.True(api.Contains("Borrowed<Map<W>*>* getBorrowed"), api);
		const string use = """
			void test(Box<short>* box) { auto mapped = map<byte>; auto copied = box.copy<int[]>; }
			""";
		foreach (bool imported in new[] { false, true })
		{
			SemanticCompilation consumer = imported ? SemanticCompiler.CompileApiConsumer(api, use, lower: false)
				: SemanticCompiler.CompileDeclarations(producerSource + use);
			SemanticCompiler.AssertNoDiagnostics(consumer);
			string?[]? inferred = null;
			LoweringResult lowered = BindableNodeLowerer.Lower(consumer.Compilation.DeclarationExpansion!, (phase, action) =>
			{
				action();
				if (phase == "method body analysis")
					inferred = SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(consumer, "test"))
						.Select(d => d.Target.ResolvedType).ToArray();
			});
			Assert.Empty(lowered.Diagnostics);
			Assert.Equal(new[] { "Map<byte>", "Copy<int[]>" }, inferred);
			const string bad = "void bad(Box<short>* box) { box.copy<Box<short>>(*box); }";
			SemanticCompilation invalid = imported ? SemanticCompiler.CompileApiConsumer(api, bad) : SemanticCompiler.CompileLowered(producerSource + bad);
			Assert.Contains(invalid.Diagnostics, d => d.Contains("copyable"));
		}
		using JsonDocument metadata = JsonDocument.Parse(MetadataJsonSerializer.Serialize(producer.Compilation, MetadataVisibility.Export));
		JsonElement[] definitions = metadata.RootElement.GetProperty("declarations").EnumerateArray().ToArray();
		JsonElement map = definitions.Single(d => d.GetProperty("name").GetString() == "Map");
		Assert.Equal("int", map.GetProperty("typeParameters")[0].GetProperty("constraint").GetString());
		JsonElement copy = definitions.Single(d => d.GetProperty("name").GetString() == "Copy");
		Assert.Equal("void", copy.GetProperty("typeParameters")[0].GetProperty("constraint").GetString());
		JsonElement[] capabilities = copy.GetProperty("parameters").EnumerateArray().Where(p => p.TryGetProperty("capability", out _)).ToArray();
		Assert.Equal(new[] { "typenameof", "sizeof" }, capabilities.Select(p => p.GetProperty("capability").GetString()));
		Assert.All(capabilities, p => Assert.Equal("T", p.GetProperty("targetType").GetString()));
		JsonElement box = definitions.Single(d => d.GetProperty("name").GetString() == "Box");
		JsonElement check = box.GetProperty("functions").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "check");
		Assert.Equal("Check<W>", lower ? check.GetProperty("interfaceImplementation").GetProperty("interface").GetString()
			: check.GetProperty("ascription").GetString());
		Assert.NotEqual(box.GetProperty("typeParameters")[0].GetProperty("id").GetString(),
			check.GetProperty("typeParameters")[0].GetProperty("id").GetString());
	}

	[Theory]
	[InlineData(false, "")]
	[InlineData(true, "")]
	[InlineData(false, "const ")]
	[InlineData(true, "const ")]
	public void Hidden_array_results_publish_opaque_pointer_and_length(bool lower, string qualifier)
	{
		string source = $$"""
			struct Secret { int value; }
			export {{qualifier}}void<Secret>[] items() => default;
			export newtype fn {{qualifier}}void<Secret>[] Items();
			""";
		SemanticCompilation compilation = lower ? SemanticCompiler.CompileLowered(source) : SemanticCompiler.CompileDeclarations(source);
		SemanticCompiler.AssertNoDiagnostics(compilation);
		using StringWriter writer = new();
		BindableNodeCodeSerializer.Serialize(compilation.Module, writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
		string api = writer.ToString();
		Assert.DoesNotContain("Secret", api);
		Assert.Contains(qualifier + "void* items(out nuint result_length)", api);
		Assert.Contains(qualifier + "void* Items(out nuint result_length)", api);
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(api));
		string metadata = MetadataJsonSerializer.Serialize(compilation.Compilation, MetadataVisibility.Export);
		Assert.DoesNotContain("Secret", metadata);
		Assert.Contains("result_length", metadata);
		Assert.Contains("void<Secret>[]", MetadataJsonSerializer.Serialize(compilation.Compilation, MetadataVisibility.All).Replace("\\u003C", "<").Replace("\\u003E", ">"));
	}

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
	public void Erased_type_parameter_storage_must_be_reassigned_after_await(bool reassign)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			class Resumer { void resumeAsync(escaped once void() continuation) { continuation(); } }
			async int tick(@awaitwith Resumer* resumer) { return 1; }
			async int run<T>(@awaitwith Resumer* resumer, in T source, sizeof(T)) {
			stackalloc T scratch = source;
			int ignored = await tick(resumer);
			{{(reassign ? "scratch = source;" : "")}}
			T* address = &scratch;
			return ignored;
			}
			""");
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
			export extern class Box<T>;
			export void<PackageValue>* value(void<PackageValue>* p) => p;
			export void<int?> optional() => 7;
			export Box<void<Secret>*>* box(Box<void<Secret>*>* p) => p;
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		using StringWriter publicWriter = new();
		BindableNodeCodeSerializer.Serialize(compilation.Module, publicWriter, new BindableNodeCodeSerializerOptions { ApiHeader = true, ApiSurface = CampApiSurfaceKind.Public });
		Assert.Contains("void<PackageValue>*", publicWriter.ToString());
		Assert.Contains("Box<void*>*", publicWriter.ToString());
		Assert.DoesNotContain("Secret", publicWriter.ToString());
		Assert.DoesNotContain("HiddenAlias", publicWriter.ToString());
		Assert.Contains("void<int?> optional()", publicWriter.ToString());
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
			export void<Secret> find(int index) { Secret s = { 7 }; return s; }
			export void array(void<Secret>[] values) { }
			export struct OpaqueView { void<Secret>[] buffer; }
			export void pointerInput(void<Secret>* value) { }
			export void pointerOutput(out void<Secret>* value) { value = default; }
			export newtype delegate void<Secret> Lookup(int index);
			export void callback(delegate void<Secret>(int index) action) { }
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
		Assert.Contains("void find(int index, void* __result)", api);
		Assert.Contains("void array(void* values, nuint values_length)", api);
		Assert.Contains("void* buffer;", api);
		Assert.Contains("nuint buffer_length;", api);
		Assert.Contains("pointerInput(void* value)", api);
		Assert.Contains("pointerOutput(out void* value)", api);
		Assert.Contains("newtype delegate void Lookup(int index, void* __result)", api);
		Assert.Contains("delegate void(int index, void* __result) action", api);
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(api));
		string metadata = MetadataJsonSerializer.Serialize(compilation.Compilation, MetadataVisibility.Export);
		Assert.DoesNotContain("Secret", metadata);
		Assert.Contains("void<Secret>", MetadataJsonSerializer.Serialize(compilation.Compilation, MetadataVisibility.All).Replace("\\u003C", "<").Replace("\\u003E", ">"));
		Assert.Contains(SemanticCompiler.CompileLowered("struct Secret { int value; } export Secret* bad(Secret* p) => p;").Diagnostics,
			diagnostic => diagnostic.Contains("exposes non-exported type"));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Hidden_carriers_project_to_plain_carriers_without_relaxing_ordinary_visibility(bool lower)
	{
		string source = """
			enum SecretKind: int { A, B }
			alias HiddenAlias = SecretKind;
			public enum PackageKind: int { C, D }
			export enum VisibleKind: int { E, F }
			export extern class Box<T>;
			export struct Handle { long<SecretKind> hidden; long<VisibleKind> shown; long<PackageKind> packaged; int<HiddenAlias>[] items; long<SecretKind>* link; }
			export long<SecretKind> make(long<SecretKind> kind, long<VisibleKind>* shown) => kind;
			export newtype delegate long<SecretKind> Pick(long<SecretKind> kind);
			export Box<long<SecretKind>>* box(Box<long<SecretKind>>* value) => value;
			""";
		SemanticCompilation compilation = lower ? SemanticCompiler.CompileLowered(source) : SemanticCompiler.CompileDeclarations(source);
		SemanticCompiler.AssertNoDiagnostics(compilation);
		using StringWriter writer = new();
		BindableNodeCodeSerializer.Serialize(compilation.Module, writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
		string api = writer.ToString();
		Assert.DoesNotContain("SecretKind", api);
		Assert.DoesNotContain("HiddenAlias", api);
		Assert.DoesNotContain("PackageKind", api);
		Assert.Contains("long hidden;", api);
		Assert.Contains("long<VisibleKind> shown;", api);
		Assert.Contains("long packaged;", api);
		Assert.Contains("int[] items;", api);
		Assert.Contains("long* link;", api);
		Assert.Contains("long make(long kind, long<VisibleKind>* shown)", api);
		Assert.Contains("newtype delegate long Pick(long kind)", api);
		Assert.Contains("Box<long>* box(Box<long>* value)", api);
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(api));
		using StringWriter publicWriter = new();
		BindableNodeCodeSerializer.Serialize(compilation.Module, publicWriter, new BindableNodeCodeSerializerOptions { ApiHeader = true, ApiSurface = CampApiSurfaceKind.Public });
		string publicApi = publicWriter.ToString();
		Assert.DoesNotContain("SecretKind", publicApi);
		Assert.Contains("long<PackageKind> packaged;", publicApi);
		SemanticCompilation consumer = SemanticCompiler.CompileLowered(publicApi);
		Assert.True(consumer.Diagnostics.Count == 0, publicApi + "\n" + string.Join("\n", consumer.Diagnostics));
		string metadata = MetadataJsonSerializer.Serialize(compilation.Compilation, MetadataVisibility.Export);
		Assert.DoesNotContain("SecretKind", metadata);
		Assert.DoesNotContain("PackageKind", metadata);
		string all = MetadataJsonSerializer.Serialize(compilation.Compilation, MetadataVisibility.All).Replace("\\u003C", "<").Replace("\\u003E", ">");
		Assert.Contains("long<SecretKind>", all);
		Assert.Contains(lower ? "int<SecretKind>[]" : "int<HiddenAlias>[]", all);
		Assert.Contains(SemanticCompiler.CompileLowered("enum SecretKind: int { A } export SecretKind bad(SecretKind kind) => kind;").Diagnostics,
			diagnostic => diagnostic.Contains("exposes non-exported type"));
	}
}
