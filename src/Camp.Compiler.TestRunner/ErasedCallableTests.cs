using Camp.Compiler;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ErasedCallableTests
{
	[Theory]
	[InlineData("scoped")]
	[InlineData("scoped(owner)")]
	[InlineData("unscoped(owner)")]
	public void Storage_substitution_preserves_written_result_lifetime(string lifetime)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			extern {{lifetime}} T* identity<T: any>(T* owner);
			void test(int* pointer) { auto result = identity(pointer); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "test")));
		Assert.Equal(lifetime + " int*", call.ResolvedType);
		Assert.Contains(lifetime + " void<T>*", call.Target!.ResolvedType);
	}

	[Fact]
	public void Async_rethrow_keeps_the_call_inside_await_until_completion()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			class Resumer { void resumeAsync(escaped once void() continuation) { continuation(); } }
			async int fail(@awaitwith Resumer* resumer, thrown int error) { throw 9; }
			async int parent(@awaitwith Resumer* resumer, thrown int error) {
				int value = await fail(resumer);
				return value;
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		UnaryExpression awaitExpression = Assert.Single(SemanticCompiler.Descendants<UnaryExpression>(SemanticCompiler.Function(compilation, "parent")),
			expression => expression.Operator == UnaryOperator.Await);
		Assert.IsType<CallExpression>(awaitExpression.Operand);
	}

	[Theory]
	[InlineData("int peek(in int? value) => value.specified ? value.value : -1; int read<T: copyable>(in T value, delegate int(in T) f, sizeof(T)) => f(value); void test() { int? value = 4; read<int?>(value, peek); }")]
	[InlineData("int seven() => 7; T produce<T: copyable>(delegate T() make, sizeof(T)) => make(); void test() { produce<int>(seven); }")]
	[InlineData("struct Point { int x; } Point make() { Point value = { 7 }; return value; } T produce<T: copyable>(delegate T() make, sizeof(T)) => make(); void test() { produce<Point>(make); }")]
	public void Incompatible_erased_callbacks_require_an_explicit_adapter(string source)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(source);
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("lambda") && diagnostic.Contains("erased calling shape"));
	}

	[Theory]
	[InlineData("fn int(in int)", "int read(in void<int> value) => value;")]
	[InlineData("fn int(in void<int>)", "int read(in int value) => value;")]
	[InlineData("fn void(out int)", "void read(out void<int> value) { value = 7; }")]
	[InlineData("fn void(out void<int>)", "void read(out int value) { value = 7; }")]
	[InlineData("fn void(int*)", "void read(void<int>* value) { }")]
	[InlineData("fn void(void<int>[])", "void read(int[] value) { }")]
	public void Nonexpanded_erased_callable_slots_are_compatible(string type, string function)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($"{function} void test() {{ {type} action = read; }}"));
	}

	[Theory]
	[InlineData("void<int> seven() => 7; int produce(delegate void<int>() make) => make(); void test() { int value = produce(seven); }")]
	[InlineData("int seven() => 7; T produce<T: copyable>(delegate T() make, sizeof(T)) => make(); void test() { int value = produce<int>(() => seven()); }")]
	[InlineData("int peek(in int? value) => value.specified ? value.value : -1; int read<T: copyable>(in T value, delegate int(in T) f, sizeof(T)) => f(value); void test() { int? value = 4; read<int?>(value, x => peek(x)); }")]
	public void Explicit_callback_alternatives_are_valid(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(source));
	}

	[Theory]
	[InlineData("struct")]
	[InlineData("sealed class")]
	public void Interface_implementations_use_callable_compatibility(string kind)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			interface Reader { int read(in void<int> value); void<int> find(int index); }
			{{kind}} Stored: Reader
			{
				int read(in int value): Reader => value;
				void<int> find(int index): Reader => index;
			}
			"""));
	}

	[Theory]
	[InlineData("void seven(out int result) { result = 7; } void<int> produce(delegate void<int>() make) => make(); void test() { produce(seven); }")]
	[InlineData("void seven(out int result) { result = 7; } void test() { delegate void<int>() make = seven; }")]
	[InlineData("void<int> seven() => 7; void test() { fn void(out int) make = seven; }")]
	public void Trailing_out_parameters_are_not_a_written_result(string source)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(source);
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("cannot convert"));
	}

	// 025 §4: the type of a reference to a generic callable is the erasure of its signature.
	[Theory]
	[InlineData("struct Slot<T: long> { T value; T get() => this.value; } enum Color: int { A } void test(Slot<Color>* s) { auto r = s.get; }", "delegate long<Color>()")]
	[InlineData("struct Slot<T: long> { T value; void set(T v) { this.value = v; } } enum Color: int { A } void test(Slot<Color>* s) { auto r = s.set; }", "delegate void(long<Color>)")]
	[InlineData("struct Slot<T: long> { T value; T get() => this.value; } void test(Slot<long<ushort>>* s) { auto r = s.get; }", "delegate long<ushort>()")]
	[InlineData("struct Plain<T> { T value; T get() => this.value; } enum Color: int { A } void test(Plain<Color>* s) { auto r = s.get; }", "delegate nint<Color>()")]
	[InlineData("struct Plain<T> { T value; T get() => this.value; } void test(Plain<int>* s) { auto r = s.get; }", "delegate nint<int>()")]
	[InlineData("extern escaped void* malloc(nuint size); extern void free(void* value); class Bag<T: copyable> { T[] items; Bag(sizeof(T)) { } T get(nuint at) => this.items[at]; void put(nuint at, in T value) { } } void test(Bag<int>* a) { auto r = a.get; auto w = a.put; }", "delegate void<int>(nuint)")]
	[InlineData("extern escaped void* malloc(nuint size); extern void free(void* value); class Bag<T: copyable> { T[] items; Bag(sizeof(T)) { } T get(nuint at) => this.items[at]; void put(nuint at, in T value) { } } void test(Bag<int?>* a) { auto r = a.get; auto w = a.put; }", "delegate void<int?>(nuint)")]
	public void Method_references_have_the_erased_signature_of_their_constraint(string source, string expected)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileDeclarations(source);
		SemanticCompiler.AssertNoDiagnostics(compilation);
		List<string?> inferred = [];
		LoweringResult lowered = BindableNodeLowerer.Lower(compilation.Compilation.DeclarationExpansion!, (phase, action) =>
		{
			action();
			if (phase == "method body analysis")
				foreach (DeclarationStatement declaration in SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "test")))
					inferred.Add(declaration.Target.ResolvedType);
		});
		Assert.Empty(lowered.Diagnostics);
		Assert.Equal(expected, inferred[0]);
	}

	[Fact]
	public void Representation_callbacks_are_nominal_carriers()
	{
		const string prelude = "enum Color: int { A, B } struct Slot<T: long> { T value; T get() => this.value; }\n";
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(prelude + """
			void test(Slot<Color>* s) {
				delegate long<Color>() exact = s.get;
				delegate Color() wanted = () => s.get();
			}
			"""));
		SemanticCompilation wrong = SemanticCompiler.CompileLowered(prelude + "void test(Slot<Color>* s) { delegate Color() wrong = s.get; }");
		Assert.Contains(wrong.Diagnostics, diagnostic => diagnostic.Contains("cannot convert"));
	}

	[Fact]
	public void Written_results_fill_a_callable_but_a_trailing_out_does_not()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			void<int> seven() => 7;
			void sevenOut(out int result) { result = 7; }
			void test() {
				delegate void<int>() made = seven;
				fn void<int>() same = seven;
				int viaOut = sevenOut();
				int viaCall = made();
			}
			"""));
	}

	[Theory]
	[InlineData("T bad<T: implements Reader>(T value) => value;", "cannot be a result")]
	[InlineData("T bad<T: implements Reader>(T* value) => *value;", "cannot be a result")]
	[InlineData("void bad<T: implements Reader>(in T value) { }", "cannot be an 'in' parameter")]
	[InlineData("void bad<T: implements Reader>(out T value) { }", "cannot be an 'out' parameter")]
	[InlineData("void bad<T: implements Reader>(T value) { }", "cannot be passed by value")]
	public void Interface_constrained_parameters_are_transported_only_by_pointer(string function, string message)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("interface Reader { int read(); }\n" + function);
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains(message) && diagnostic.Contains("'out T*'"));
	}

	[Theory]
	[InlineData("T* ok<T: implements Reader>(T* value) => value;")]
	[InlineData("const T* ok<T: implements Reader>(const T* value) => value;")]
	[InlineData("void ok<T: implements Reader>(T[] values) { }")]
	[InlineData("void ok<T: implements Reader>(T* source, out T* value) { value = source; }")]
	[InlineData("int ok<T: implements Reader>(T* value, vtableof(T: Reader)) => value.read();")]
	public void Interface_constrained_pointer_forms_are_valid(string function)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("interface Reader { int read(); }\n" + function));
	}

	[Fact]
	public void Virtual_overrides_still_require_exact_signatures()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			virtual class Base { virtual int read(in void<int> value) => value; }
			sealed class Derived: Base { override int read(in int value) => value; }
			""");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("must match an inherited virtual or abstract method"));
	}

	[Theory]
	[InlineData("fn void(int*)", "fn void(void<const int>*)")]
	[InlineData("fn void(void<int>*)", "fn void(const int*)")]
	[InlineData("fn void(in void<int?>)", "fn void(in int?)")]
	public void Erased_compatibility_does_not_discard_constness_or_expanded_storage(string source, string target)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void test({source} value) {{ {target} action = value; }}");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("cannot convert"));
	}

	[Fact]
	public void Callable_newtype_ascription_and_nested_slots_use_erased_compatibility()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			newtype fn int Compare(in void<int> left, in void<int> right);
			int compare(in int left, in int right): Compare => left - right;
			void test(fn void(fn void(in int)) nested, fn fn void(in int)() factory) {
				fn void(fn void(in void<int>)) same = nested;
				fn fn void(in void<int>)() sameFactory = factory;
			}
			"""));
	}
}
