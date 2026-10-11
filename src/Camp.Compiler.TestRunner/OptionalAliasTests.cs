using Xunit;

namespace Camp.Compiler.Tests;

public sealed class OptionalAliasTests
{
	[Theory]
	[InlineData("int")]
	[InlineData("long")]
	[InlineData("Color")]
	[InlineData("Value")]
	[InlineData("Handle")]
	public void Aliases_to_copyable_types_work_in_optional_and_generic_positions(string payload)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			enum Color: int { RED }
			struct Value { int count; }
			newtype Handle: int;
			alias Payload = {{payload}};
			alias Nested = Payload;
			namespace Types { alias Qualified = {{payload}}; }
			struct Holder<T: copyable> { T* item; }
			struct OptionalHolder { Payload? item; }
			Payload? copy(Payload? item) => item;
			void take(Nested? item, Types::Qualified? other) { }
			void test() { Payload? item = default; Holder<Payload> holder; take(copy(item), default); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Theory]
	[InlineData("class Value { }")]
	[InlineData("fixed struct Value { int count; }")]
	public void Aliases_do_not_make_non_copyable_payloads_copyable(string declaration)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			{{declaration}}
			alias Payload = Value;
			void take(Payload? item) { }
			""");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("Optional payloads must be copyable"));
	}
}
