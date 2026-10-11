using System.Linq;
using System.IO;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ErasedTypeTests
{
	[Theory]
	[InlineData("void<int>", "Int")]
	[InlineData("void<int>*", "Int")]
	[InlineData("const void<int[]>**", "IntArray")]
	[InlineData("void<int?>[]", "IntArray")]
	[InlineData("void<Widget*>", "Widget")]
	[InlineData("nint<Widget*>", "Widget")]
	[InlineData("long<Kind>*", "Kind")]
	[InlineData("byte<Kind>[]", "KindArray")]
	[InlineData("Bag<int[]>", "Bag")]
	[InlineData("void", "Void")]
	public void Annotated_types_name_the_annotated_type(string type, string expected)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"class Widget {{ }} class Bag<T: any> {{ }} enum Kind: byte {{ A }} export string name() => typenameof({type});");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-erased-name-");
		try
		{
			CEmissionResult emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "erased_name" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			string code = string.Join("\n", emission.GeneratedSourceFiles.Select(File.ReadAllText));
			Assert.Contains($"return \"{expected}\";", code);
		}
		finally { directory.Delete(recursive: true); }
	}

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
	public void Erased_results_precede_capability_slots()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			export struct Point { int x; int y; }
			export void getPoint(out void<Point> result) { Point p = { 3, 4 }; result = p; }
			export void<int?> find(int index) { return 7; }
			export void<Point>* getOpaquePoint(void<Point>* p) => p;
			export T identity<T: copyable>(in T value, sizeof(T), typenameof(T)) => value;
			export bool tryCopy<T: copyable>(in T value, out T result, sizeof(T)) { result = value; return true; }
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
			Assert.Contains("void find(int index, void *__result)", code);
			Assert.Contains("void *getOpaquePoint(void *p)", code);
			Assert.Contains("void identity(const void *value, void *__result, uintptr_t sizeof_T, const char *typenameof_T)", code);
			Assert.Contains("bool tryCopy(const void *value, void *result, uintptr_t sizeof_T)", code);
		}
		finally { directory.Delete(recursive: true); }
	}

	[Fact]
	public void Result_slot_precedes_allocator_error_and_witness_capabilities()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			T mixed<T: copyable>(in T value, within void* allocator, thrown int error, sizeof(T), typenameof(T)) => value;
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		foreach (string name in new[] { "mixed" })
		{
			FunctionDefinition function = SemanticCompiler.Function(compilation, name);
			Assert.Equal("__result", function.Parameters[1].Name);
			Assert.Equal(ParameterModifier.Out, function.Parameters[1].Modifier);
		}
	}

	[Theory]
	[InlineData("T missing<T: copyable>(in T value) => value;", "requires sizeof(T)")]
	[InlineData("T missing<T: copyable>() => default;", "requires sizeof(T)")]
	[InlineData("T missing<T: any>(in T value, sizeof(T)) => value;", "non-copying")]
	[InlineData("interface I { } void bad<T: implements I>(T value, sizeof(T)) { }", "cannot be passed by value")]
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
	[InlineData("void<int>?", "optional payload cannot be")]
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
	[InlineData("void<int>? optional = default;", "optional payload cannot be")]
	[InlineData("stackalloc void<int>? optional = default;", "optional payload cannot be")]
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
	public void Erased_signature_positions_and_pointer_wrapped_generic_arguments_bind()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			class Box<T: any> { }
			void take(void<int>* input, out void<int>* output, Box<void<int[]>*>* pointer) { output = default; }
			void<int> result() => 7;
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Theory]
	[InlineData("void add(overload void<User>* item) { }", "annotated erased types cannot select an overload")]
	[InlineData("void add(overload int<Kind> item) { }", "annotated erased types cannot select an overload")]
	[InlineData("void add(overload long<Kind>* item) { }", "annotated erased types cannot select an overload")]
	[InlineData("void initialize(void<User>* this) { }", "cannot be a receiver type")]
	[InlineData("void invert(int<Kind> this) { }", "cannot be a receiver type")]
	[InlineData("void invert(long<Kind>* this) { }", "cannot be a receiver type")]
	public void Annotated_types_cannot_select_overloads_or_receive(string declaration, string message)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"class User {{ }} enum Kind: int {{ A }} {declaration}");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains(message));
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

	const string CarrierPreamble = """
		enum SmallKind: byte { A }
		enum Kind: int { A }
		enum WideKind: long { A }
		newtype Small: ushort;
		newtype Wide: ulong;
		struct Point { int x; }
		class Widget { }
		""";

	[Theory]
	[InlineData("byte", "byte")]
	[InlineData("byte", "sbyte")]
	[InlineData("byte", "SmallKind")]
	[InlineData("ushort", "byte")]
	[InlineData("ushort", "short")]
	[InlineData("ushort", "SmallKind")]
	[InlineData("ushort", "Small")]
	[InlineData("short", "ushort")]
	[InlineData("uint", "ushort")]
	[InlineData("uint", "int")]
	[InlineData("uint", "Kind")]
	[InlineData("uint", "Small")]
	[InlineData("int", "uint")]
	[InlineData("int", "Kind")]
	[InlineData("ulong", "long")]
	[InlineData("ulong", "WideKind")]
	[InlineData("ulong", "Wide")]
	[InlineData("long", "ulong")]
	[InlineData("long", "Kind")]
	[InlineData("long", "Small")]
	[InlineData("nuint", "uint")]
	[InlineData("nuint", "byte*")]
	[InlineData("nuint", "Widget*")]
	[InlineData("nuint", "void<int>*")]
	[InlineData("nint", "int")]
	[InlineData("nint", "string")]
	[InlineData("nint", "wstring")]
	[InlineData("nuint", "astring")]
	[InlineData("nint", "Kind")]
	[InlineData("nint", "Point*")]
	[InlineData("nint", "nuint")]
	public void Carriers_accept_the_arguments_of_their_representation_constraint(string carrier, string operand)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"{CarrierPreamble} void take({carrier}<{operand}> value, {carrier}<{operand}>* pointer, {carrier}<{operand}>[] array) {{ }} newtype delegate void Callback({carrier}<{operand}> value, {carrier}<{operand}>[] array);");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Theory]
	[InlineData("byte", "short", "wider than 'byte'")]
	[InlineData("byte", "Kind", "wider than 'byte'")]
	[InlineData("byte", "Small", "wider than 'byte'")]
	[InlineData("ushort", "int", "wider than 'ushort'")]
	[InlineData("short", "uint", "wider than 'short'")]
	[InlineData("uint", "long", "wider than 'uint'")]
	[InlineData("uint", "WideKind", "wider than 'uint'")]
	[InlineData("int", "ulong", "wider than 'int'")]
	[InlineData("int", "Wide", "wider than 'int'")]
	[InlineData("ulong", "float", "does not satisfy 'T: ulong'")]
	[InlineData("long", "double", "does not satisfy 'T: long'")]
	[InlineData("long", "bool", "does not satisfy 'T: long'")]
	[InlineData("long", "char", "does not satisfy 'T: long'")]
	[InlineData("long", "Point", "does not satisfy 'T: long'")]
	[InlineData("long", "Widget*", "pointer types satisfy only 'T: nint' and 'T: nuint'")]
	[InlineData("int", "byte*", "pointer types satisfy only 'T: nint' and 'T: nuint'")]
	[InlineData("int", "string", "pointer types satisfy only 'T: nint' and 'T: nuint'")]
	[InlineData("int", "int?", "does not satisfy 'T: int'")]
	[InlineData("int", "int[]", "does not satisfy 'T: int'")]
	[InlineData("nint", "float", "does not satisfy 'T: nint'")]
	[InlineData("nint", "int[]?", "does not satisfy 'T: nint'")]
	[InlineData("nint", "int[]*", "distributive")]
	[InlineData("nint", "delegate int()", "does not satisfy 'T: nint'")]
	[InlineData("int", "void", "cannot be void")]
	[InlineData("int", "void<int>", "cannot be void<V> or another carrier")]
	[InlineData("nint", "void<int[]>", "cannot be void<V> or another carrier")]
	[InlineData("int", "byte<byte>", "cannot be void<V> or another carrier")]
	[InlineData("long", "long<int>", "cannot be void<V> or another carrier")]
	public void Carriers_reject_arguments_their_constraint_rejects(string carrier, string operand, string reason)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"{CarrierPreamble} void take({carrier}<{operand}> value) {{ }}");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("is not a valid carried type") && diagnostic.Contains(reason));
	}

	[Theory]
	[InlineData("int<byte, byte> value;", "requires exactly one carried type")]
	[InlineData("long<> value;", "requires exactly one carried type")]
	public void Carriers_require_exactly_one_operand(string declaration, string message)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void take() {{ {declaration} }}");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains(message));
	}

	[Theory]
	[InlineData("gcc-linux-x64", "nint<long>", true)]
	[InlineData("gcc-linux-x64", "nuint<ulong>", true)]
	[InlineData("gcc-linux-x64", "nint<byte*>", true)]
	[InlineData("gcc-linux-x86", "nint<long>", false)]
	[InlineData("gcc-linux-x86", "nuint<ulong>", false)]
	[InlineData("gcc-linux-x86", "nint<int>", true)]
	[InlineData("gcc-linux-x86", "nuint<byte*>", true)]
	[InlineData("gcc-linux-x86", "int<nint>", true)]
	[InlineData("gcc-linux-x86", "short<nint>", false)]
	[InlineData("gcc-linux-x64", "int<nint>", false)]
	[InlineData("gcc-linux-x64", "long<nuint>", true)]
	public void Natural_integer_carriers_follow_the_selected_target(string target, string carrier, bool valid)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredForTarget(target, $"void take({carrier} value) {{ }}");
		if (valid)
			SemanticCompiler.AssertNoDiagnostics(compilation);
		else
			Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("is not a valid carried type"));
	}

	[Theory]
	[InlineData("int<byte> b = 300;", "outside the range of type 'byte'")]
	[InlineData("short<byte> b = -1;", "outside the range of type 'byte'")]
	[InlineData("long<Kind> c = A;", null)]
	[InlineData("long<Kind> c = Kind.A;", null)]
	[InlineData("int<byte> b = 255;", null)]
	public void Carrier_initializers_are_checked_against_the_carried_type(string statement, string? message)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"{CarrierPreamble} void test() {{ {statement} }}");
		if (message is null)
			SemanticCompiler.AssertNoDiagnostics(compilation);
		else
			Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains(message));
	}

	[Theory]
	// Accepted: the view classifies these exactly as the carried or stripped types.
	[InlineData("Button* b = vb;", null)]
	[InlineData("take(vb);", null)]
	[InlineData("Kind k = lc; long<Kind> back = k;", null)]
	[InlineData("Other o = (Other)lc;", null)]
	[InlineData("Kind* c = (Kind*)plc; long* l = (long*)plc;", null)]
	[InlineData("Button* b = (unsafe Button*)nw;", null)]
	[InlineData("fn void(long<Kind>) f = takeKind;", null)]
	[InlineData("Bag<int*>* a = lv; Bag<void<int>*>* b = li;", null)]
	// Rejected: carriers behind pointers and in callable signatures stay nominal newtypes.
	[InlineData("Other o = lc;", "cannot convert 'Kind' to 'Other'")]
	[InlineData("Button* b = (Button*)nw;", "requires unsafe")]
	[InlineData("Button* b = nw;", "cannot convert 'Widget*' to 'Button*'")]
	[InlineData("Kind* c = plc;", "cannot convert 'long<Kind>*' to")]
	[InlineData("long* l = plc;", "cannot convert 'long<Kind>*' to 'long*'")]
	[InlineData("fn void(Kind) f = takeKind;", "cannot convert 'fn void(long<Kind>)' to 'fn void(Kind)'")]
	[InlineData("fn void(long<Kind>) f = takePlain;", "cannot convert 'fn void(Kind)' to 'fn void(long<Kind>)'")]
	[InlineData("W<Kind> w = wc;", "cannot convert 'W<long<Kind>>' to 'W<Kind>'")]
	[InlineData("void<Button>* p = vwp;", "cannot convert")]
	public void Conversion_view_classifies_carriers_and_stripped_storage(string statement, string? message)
	{
		const string declarations = """
			enum Kind: int { A }
			enum Other: int { B }
			class Widget { }
			class Button: Widget { }
			struct W<T: long> { T v; }
			class Bag<T: any> { }
			void takeKind(long<Kind> value) { }
			void takePlain(Kind value) { }
			void take(in void<Widget*> w) { }
			""";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			{{declarations}}
			void test(void<Button>* vb, long<Kind> lc, long<Kind>* plc, nint<Widget*> nw, Bag<void<int>*>* lv, Bag<int*>* li, W<long<Kind>> wc, void<Widget>* vwp)
			{
				{{statement}}
			}
			""");
		if (message is null)
			SemanticCompiler.AssertNoDiagnostics(compilation);
		else
			Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains(message));
	}
}
