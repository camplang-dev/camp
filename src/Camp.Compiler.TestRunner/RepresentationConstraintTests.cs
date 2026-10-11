using Xunit;

namespace Camp.Compiler.Tests;

public sealed class RepresentationConstraintTests
{
	const string Types = """
		enum SmallCode: byte { READY }
		enum WideCode: ulong { READY }
		newtype Small: ushort;
		newtype Wide: ulong;
		struct Point { int x; }
		""";

	[Theory]
	[InlineData("int", "ulong")]
	[InlineData("int", "float")]
	[InlineData("int", "string")]
	[InlineData("int", "int*")]
	[InlineData("int", "WideCode")]
	[InlineData("int", "Wide")]
	[InlineData("long", "Point")]
	[InlineData("long", "bool")]
	[InlineData("byte", "short")]
	[InlineData("nint", "float")]
	public void Invalid_arguments_are_rejected_at_type_instantiation_and_calls(string carrier, string argument)
	{
		foreach (string use in new[] { $"Slot<{argument}> item;", $"take<{argument}>(default);", $"{argument} item = default; take(item);" })
		{
			SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
				{{Types}}
				struct Slot<T: {{carrier}}> { T value; }
				void take<T: {{carrier}}>(T value) { }
				void test() { {{use}} }
				""");
			Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains($"representation constraint 'T: {carrier}'"));
		}
	}

	[Theory]
	[InlineData("int", "byte")]
	[InlineData("int", "uint")]
	[InlineData("int", "SmallCode")]
	[InlineData("int", "Small")]
	[InlineData("int", "int<SmallCode>")]
	[InlineData("long", "ulong")]
	[InlineData("nint", "int*")]
	[InlineData("nint", "string")]
	[InlineData("nint", "wstring")]
	[InlineData("nuint", "astring")]
	[InlineData("nuint", "Point*")]
	public void Compatible_arguments_are_accepted(string carrier, string argument)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			{{Types}}
			struct Slot<T: {{carrier}}> { T value; }
			void take<T: {{carrier}}>(T value) { }
			void test() { Slot<{{argument}}> item; {{argument}} value = default; take(value); take<{{argument}}>(value); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Fact]
	public void Carrier_arguments_use_storage_width_but_inferred_calls_read_the_carried_value()
	{
		foreach (string use in new[] { "Slot<long<SmallCode>> item;", "take<long<SmallCode>>(default);" })
		{
			SemanticCompilation invalid = SemanticCompiler.CompileLowered($$"""
				{{Types}}
				struct Slot<T: int> { T value; }
				void take<T: int>(T value) { }
				void test() { {{use}} }
				""");
			Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Contains("representation constraint 'T: int'"));
		}
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			{{Types}}
			void take<T: int>(T value) { }
			void test() { long<SmallCode> item = READY; take(item); }
			"""));
	}

	[Theory]
	[InlineData("gcc-linux-x64", "long", true)]
	[InlineData("gcc-linux-x86", "long", false)]
	[InlineData("gcc-linux-x86", "int*", true)]
	public void Default_constraint_uses_the_selected_natural_integer_width(string target, string argument, bool valid)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredForTarget(target,
			$"struct Slot<T> {{ T value; }} void test() {{ Slot<{argument}> item; }}");
		if (valid) SemanticCompiler.AssertNoDiagnostics(compilation);
		else Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("representation constraint 'T: nint'"));
	}

	[Theory]
	[InlineData("int", "short", true)]
	[InlineData("short", "int", false)]
	[InlineData("int", "any", false)]
	public void Forwarded_parameters_must_prove_the_representation_constraint(string required, string supplied, bool valid)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			struct Slot<T: {{required}}> { T value; }
			void take<T: {{required}}>(T* value) { }
			void forward<U: {{supplied}}>(U* value) { Slot<U> item; take(value); }
			""");
		if (valid) SemanticCompiler.AssertNoDiagnostics(compilation);
		else Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains($"representation constraint 'T: {required}'"));
	}
}
