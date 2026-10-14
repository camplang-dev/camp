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
	[InlineData("short", "float")]
	[InlineData("int", "double")]
	[InlineData("int", "string")]
	[InlineData("int", "int*")]
	[InlineData("int", "WideCode")]
	[InlineData("int", "Wide")]
	[InlineData("long", "Point")]
	[InlineData("byte", "short")]
	[InlineData("nint", "double")]
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
	[InlineData("int", "float")]
	[InlineData("long", "bool")]
	[InlineData("long", "double")]
	[InlineData("ulong", "float")]
	[InlineData("nint", "float")]
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
	public void Carrier_arguments_are_replaced_by_the_destination_representation_and_inferred_calls_read_the_carried_value()
	{
		// The destination validates the carried type, not the old carrier: long<SmallCode> becomes int<SmallCode>.
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			{{Types}}
			struct Slot<T: int> { T value; }
			void take<T: int>(T value) { }
			void test() { Slot<long<SmallCode>> item; take<long<SmallCode>>(default); }
			"""));
		SemanticCompilation invalid = SemanticCompiler.CompileLowered($$"""
			{{Types}}
			void take<T: int>(T value) { }
			void test() { take<long<WideCode>>(default); }
			""");
		Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Contains("representation constraint 'T: int'"));
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
	public void Explicit_nint_uses_the_selected_natural_integer_width(string target, string argument, bool valid)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredForTarget(target,
			$"struct Slot<T: nint> {{ T value; }} void test() {{ Slot<{argument}> item; }}");
		if (valid) SemanticCompiler.AssertNoDiagnostics(compilation);
		else Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("representation constraint 'T: nint'"));
	}

	[Theory]
	[InlineData("int", ": int", true, "")]
	[InlineData("int", ": short", false, "representation-changing transport")]
	[InlineData("int", ": long", false, "representation constraint 'T: int'")]
	[InlineData("int", "", false, "representation constraint 'T: int'")]
	public void Forwarded_parameters_must_prove_the_representation_constraint(string required, string supplied, bool valid, string message)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			struct Slot<T: {{required}}> { T value; }
			void take<T: {{required}}>(T* value) { }
			void forward<U{{supplied}}>(U* value) { Slot<U> item; take(value); }
			""");
		if (valid) SemanticCompiler.AssertNoDiagnostics(compilation);
		else Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains(message));
	}
}
