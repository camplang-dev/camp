using Xunit;

namespace Camp.Compiler.Tests;

public class ConstructedClassConversionTests
{
	const string Types = """
		class Box<T> {
			int tag;
			int read() { return this.tag; }
			int explicit(const this) { return this.tag; }
			void accept(T* value) { }
			T* getItem() { return null; }
		}
		class Mid<T>: Box<T> { int extra; }
		class IntBox: Mid<int> {}
		class Other: Box<uint> {}
		""";

	[Theory]
	[InlineData("IntBox", "Box<int>")]
	[InlineData("IntBox", "Mid<int>")]
	[InlineData("Mid<int>", "Box<int>")]
	[InlineData("Other", "Box<uint>")]
	public void Exact_constructed_base_is_implicit(string source, string target)
	{
		SemanticCompiler.AssertNoDiagnostics(Compile(source, target));
	}

	[Theory]
	[InlineData("IntBox", "Box<uint>")]
	[InlineData("IntBox", "Mid<uint>")]
	[InlineData("Mid<int>", "Box<uint>")]
	[InlineData("Other", "Box<int>")]
	[InlineData("Box<int>", "Box<uint>")]
	public void Different_construction_is_rejected(string source, string target)
	{
		var compilation = Compile(source, target);
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("cannot convert"));
	}

	[Theory]
	[InlineData("void use(IntBox* value) { value.read(); value.explicit(); }")]
	[InlineData("class Leaf: Mid<int> { int inside() { return this.read(); } }")]
	[InlineData("void use() { IntBox local = IntBox(); local.read(); }")]
	[InlineData("void use(const IntBox* value) { value.explicit(); }")]
	[InlineData("void use(IntBox* value, int* item) { value.accept(item); int* result = value.getItem(); }")]
	[InlineData("void use(IntBox* value) { delegate int() reader = value.read; reader(); }")]
	[InlineData("class Swapped<A, B>: Box<B> { } void use(Swapped<uint, int>* value, int* item) { value.accept(item); }")]
	[InlineData("class Forwarded<U>: Mid<U> { void inside(U* item) { this.accept(item); } }")]
	public void Inherited_members_use_the_declaring_base_construction(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(Types + source));
	}

	[Theory]
	[InlineData("void use(const IntBox* value) { value.read(); }")]
	[InlineData("void use(IntBox* value, uint* item) { value.accept(item); }")]
	[InlineData("void use(IntBox* value) { uint* result = value.getItem(); }")]
	[InlineData("void use(IntBox** value) { value.read(); }")]
	public void Inherited_members_preserve_qualifiers_arguments_and_pointer_depth(string source)
	{
		var compilation = SemanticCompiler.CompileLowered(Types + source);
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
	}

	static SemanticCompilation Compile(string source, string target) => SemanticCompiler.CompileLowered(("constructed_base.camp",
		Types + $"void run({source}* source) {{ {target}* target = source; }}"));
}
