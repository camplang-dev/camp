using System.Linq;
using System.IO;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ErasedTypeTests
{
	[Theory]
	[InlineData("copyable", "")]
	[InlineData("copyable", ", sizeof(T)")]
	[InlineData("any", "")]
	[InlineData("any", ", sizeof(T)")]
	public void Erased_generic_inputs_require_references(string constraint, string capabilities)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void take<T: {constraint}>(T value{capabilities}) {{ }}");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("use 'in T'"));
	}

	[Theory]
	[InlineData("int?")]
	[InlineData("int[]")]
	[InlineData("int[]*")]
	[InlineData("delegate int()")]
	public void Representation_generics_reject_expandable_arguments(string type)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"class Box<T> {{ }} void take(Box<{type}>* p) {{ }}");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("requires an erased generic parameter"));
	}

	[Fact]
	public void Erased_results_precede_capability_slots_and_return_presence()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			export struct Point { int x; int y; }
			export void getPoint(out void<Point> result) { Point p = { 3, 4 }; result = p; }
			export void<int?>? find(int index) { if (index == 0) return 7; return default; }
			export void<Point>* getOpaquePoint(void<Point>* p) => p;
			export T identity<T: copyable>(in T value, sizeof(T), typenameof(T)) => value;
			export T? maybe<T: copyable>(in T value, sizeof(T)) => value;
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-erased-results-");
		try
		{
			CEmissionResult emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "erased_results" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			string code = string.Join("\n", emission.GeneratedFiles.Where(path => Path.GetExtension(path) is ".c" or ".h").Select(File.ReadAllText));
			Assert.Contains("void getPoint(void *result)", code);
			Assert.Contains("bool find(int index, void *__result)", code);
			Assert.Contains("void *getOpaquePoint(void *p)", code);
			Assert.Contains("void identity(const void *value, void *__result, uintptr_t sizeof_T, const char *typenameof_T)", code);
			Assert.Contains("bool maybe(const void *value, void *__result, uintptr_t sizeof_T)", code);
		}
		finally { directory.Delete(recursive: true); }
	}

	[Fact]
	public void Result_slot_precedes_allocator_error_and_witness_capabilities()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			interface Reader { int read(); }
			T mixed<T: copyable>(in T value, within void* allocator, thrown int error, sizeof(T), typenameof(T)) => value;
			T witnessed<T: implements Reader>(T* value, sizeof(T), vtableof(T: Reader)) => default;
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		foreach (string name in new[] { "mixed", "witnessed" })
		{
			FunctionDefinition function = SemanticCompiler.Function(compilation, name);
			Assert.Equal("__result", function.Parameters[1].Name);
			Assert.Equal(ParameterModifier.Out, function.Parameters[1].Modifier);
		}
	}

	[Theory]
	[InlineData("T? missing<T: copyable>(in T value) => value;", "requires sizeof(T)")]
	[InlineData("T? missing<T: copyable>() => default;", "requires sizeof(T)")]
	[InlineData("T? missing<T: any>(in T value, sizeof(T)) => value;", "non-copying")]
	[InlineData("interface I { } void bad<T: implements I>(T value, sizeof(T)) { }", "use 'in T'")]
	public void Erased_operations_keep_capability_requirements(string source, string message)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(source);
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains(message));
	}

	[Fact]
	public void Native_boundary_erases_annotations_and_preserves_input_constness()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			alias Integer = int;
			export struct Point { int x; }
			export void<Integer>* identity(void<Integer>* p) => p;
			export const void<Point>* readonlyIdentity(const void<Point>* p) => p;
			export void<int>** deeper(void<int>** p) => p;
			export void array(void<int?>[] values) { }
			export int input(in void<int> value) => value;
			export void output(out void<int> value) { value = 2; }
			export int receiver(void<int>* this) => *this;
			export void callback(fn void(in void<int> input, out void<int> output) action) { }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-erased-abi-");
		try
		{
			CEmissionResult emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "erased_types_abi" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			Assert.NotEmpty(emission.GeneratedSourceFiles);
			string code = string.Join("\n", emission.GeneratedFiles.Where(path => Path.GetExtension(path) is ".c" or ".h").Select(File.ReadAllText));
			Assert.Contains("void *identity(void *p)", code);
			Assert.Contains("const void *readonlyIdentity(const void *p)", code);
			Assert.Contains("void **deeper(void **p)", code);
			Assert.Contains("void array(void *values, uintptr_t values_length)", code);
			Assert.Contains("int input(const void *value)", code);
			Assert.Contains("void output(void *value)", code);
			Assert.Contains("int receiver(void *this)", code);
			Assert.DoesNotContain("Void_receiver", code);
			Assert.Contains("void callback(void (* action)(const void *input, void *output))", code);
		}
		finally { directory.Delete(recursive: true); }
	}
	[Theory]
	[InlineData("void<int>*")]
	[InlineData("const void<int[]>***")]
	[InlineData("void<int?>[]")]
	[InlineData("delegate void(in void<int> x, out void<int> y)")]
	public void Written_storage_positions_retain_annotation(string type)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void take({type} p) {{ }}");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		Assert.Contains(SemanticCompiler.Function(compilation, "take").Parameters, parameter => parameter.ResolvedType?.Contains("void<") == true);
	}

	[Theory]
	[InlineData("void<void>*", "cannot contain void")]
	[InlineData("void<const void>*", "cannot contain void")]
	[InlineData("void<void<int>>*", "another erased type")]
	[InlineData("void<int>", "storage and is not valid")]
	[InlineData("void<int>?", "storage and is not valid")]
	[InlineData("delegate void<int>()", "storage and is not valid")]
	[InlineData("struct(int[])*", "use void<V> instead")]
	public void Invalid_written_types_are_diagnosed(string type, string message)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void take({type} p) {{ }}");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains(message));
	}

	[Theory]
	[InlineData("int? value = 1; void<int?>* p = &value;", "cannot convert")]
	[InlineData("const void<int>* p = default; int* q = p;", "cannot convert")]
	[InlineData("void* p = default; void<int>* q = p;", "cannot convert")]
	[InlineData("void<int> ordinary = 1;", "storage and is not valid")]
	[InlineData("void<int>? optional = default;", "storage and is not valid")]
	[InlineData("stackalloc void<int>? optional = default;", "storage and is not valid")]
	[InlineData("fixed void<int>[2] fixedLocal = default;", "storage and is not valid")]
	[InlineData("int* p = default; void<int[]>* q = (void<int[]>*)p;", "cast through 'void*'")]
	[InlineData("void<int>* p = default; void<long>* q = p;", "cannot convert")]
	[InlineData("const void<int>[] p = default; int[] q = p;", "cannot convert")]
	public void Erased_pointer_conversions_preserve_fences_and_constness(string body, string message)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void test() {{ {body} }}");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains(message));
	}

	[Fact]
	public void Scalar_pointer_conversions_are_bidirectional_at_multiple_depths()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			void test(int** ordinary) {
				void<int>** erased = ordinary;
				int** back = erased;
				const void<int>* readOnly = *erased;
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Fact]
	public void Optional_signature_positions_and_pointer_wrapped_generic_arguments_bind()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			class Box<T: any> { }
			void take(in void<int>? input, out void<int>? output, Box<void<int[]>*>* pointer) { output = default; }
			void<int>? result() => default;
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Fact]
	public void Erased_receivers_keep_normal_symbol_collision_checks()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			int same(void<int>* this) => *this;
			int same(void<long>* this) => 0;
			""");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("symbol", System.StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void Explicit_generic_calls_reject_bare_erased_arguments()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			void take<T: any>() { }
			void test() { take<void<int>>(); }
			""");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("a by-value generic argument"));
	}

	[Fact]
	public void Disjoint_erased_casts_are_explicit_and_generic_pointer_families_need_a_fence()
	{
		SemanticCompilation allowed = SemanticCompiler.CompileLowered("""
			void test(void<int>* source) { void<long>* target = (void<long>*)source; }
			""");
		SemanticCompiler.AssertNoDiagnostics(allowed);
		SemanticCompilation forbidden = SemanticCompiler.CompileLowered("""
			void test<T: any>(T* source) { void<int>* target = (void<int>*)source; }
			""");
		Assert.Contains(forbidden.Diagnostics, diagnostic => diagnostic.Contains("cast through 'void*'"));
	}
}
