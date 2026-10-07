using Camp.Compiler;
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
		Assert.Contains(lifetime + " T*", call.Target!.ResolvedType);
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
	[InlineData("void seven(out int result) { result = 7; } T produce<T: copyable>(delegate T() make, sizeof(T)) => make(); void test() { int value = produce<int>(seven); }")]
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
			interface Reader { int read(in void<int> value); void<int?>? find(int index); }
			{{kind}} Stored: Reader
			{
				int read(in int value): Reader => value;
				bool find(int index, out void<int?> value): Reader { value = index; return true; }
			}
			"""));
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
