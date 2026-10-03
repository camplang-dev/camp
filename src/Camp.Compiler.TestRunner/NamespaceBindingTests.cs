using System;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class NamespaceBindingTests
{
	[Theory]
	[InlineData("id(Util::twice(3))")]
	[InlineData("id(Geometry::Metrics::twice(3))")]
	[InlineData("id(global::twice(3))")]
	[InlineData("id(M::twice(3))")]
	[InlineData("id(Util::marker)")]
	[InlineData("id(Util::Tools.get())")]
	[InlineData("enumId(Util::Choice.Second)")]
	[InlineData("id((Util::twice(3)))")]
	[InlineData("id(value: Util::twice(3))")]
	[InlineData("id(value: id(Util::twice(3)))")]
	public void Qualified_call_arguments_bind_across_files(string expression)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(
			("declarations.camp", """
				namespace Util
				{
					public int twice(int value) => value * 2;
					public enum Choice { First, Second }
					public inline int marker = 7;
					static class Tools { public static int get() => 9; }
				}
				namespace Geometry::Metrics { public int twice(int value) => value * 2; }
				namespace global { public int twice(int value) => value * 2; }
				"""),
			("use.camp", $$"""
				using Geometry::Metrics as M;
				namespace App;
				int id(int value) => value;
				Util::Choice enumId(Util::Choice value) => value;
				void test() { auto value = {{expression}}; }
				"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Theory]
	[InlineData("Util", "int value = Util::twice(1);")]
	[InlineData("Geometry::Metrics", "int value = Geometry::Metrics::twice(1);")]
	[InlineData("global", "int value = global::twice(1);")]
	[InlineData("Util", "Util::Box value = default;")]
	[InlineData("Util", "auto value = Util::Box();")]
	[InlineData("Util", "Util::Choice value = Util::Choice.Second;")]
	[InlineData("Util", "int value = Util::marker;")]
	[InlineData("Util", "int value = Util::Tools.get();")]
	public void Qualified_names_across_files_do_not_require_imports(string namespaceName, string statement)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(
			("declarations.camp", $$"""
				namespace {{namespaceName}}
				{
					public int twice(int value) => value * 2;
					public struct Box { int side; }
					public enum Choice { First, Second }
					public inline int marker = 7;
					static class Tools { public static int get() => 9; }
				}
				"""),
			("use.camp", $$"""
				namespace App;
				void test() { {{statement}} }
				"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Theory]
	[InlineData("int hidden() => 1;", "int value = Util::hidden();")]
	[InlineData("struct Hidden { }", "Util::Hidden value = default;")]
	[InlineData("requires (FALSE) public int hidden() => 1;", "int value = Util::hidden();")]
	[InlineData("requires (FALSE) public struct Hidden { }", "Util::Hidden value = default;")]
	[InlineData("public int visible() => 1;", "int value = visible();")]
	[InlineData("public struct Visible { }", "Visible value = default;")]
	public void Qualification_does_not_expose_file_local_or_unimported_unqualified_names(string declaration, string statement)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(
			("declarations.camp", $$"""
				namespace Util { {{declaration}} }
				"""),
			("use.camp", $$"""
				void test() { {{statement}} }
				"""));
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
	}

	[Theory]
	[InlineData("M", true)]
	[InlineData("Geometry::Metrics", true)]
	[InlineData("Metrics", false)]
	public void Namespace_alias_does_not_import_the_final_namespace_segment(string qualifier, bool valid)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(
			("declarations.camp", """
				namespace Geometry::Metrics { public int twice(int value) => value * 2; }
				"""),
			("use.camp", $$"""
				using Geometry::Metrics as M;
				void test() { int value = {{qualifier}}::twice(1); }
				"""));
		if (valid)
			SemanticCompiler.AssertNoDiagnostics(compilation);
		else
			Assert.NotEmpty(compilation.AnalysisDiagnostics);
	}

	[Theory]
	[InlineData("public struct Box { int side; }", "Box value = default;")]
	[InlineData("public struct Box { int side; }", "auto value = Box();")]
	[InlineData("public enum Box { One, Two }", "Box value = default;")]
	[InlineData("public enum Box { One, Two }", "auto value = Box.One;")]
	[InlineData("static class Box { public static int get() => 1; }", "int value = Box.get();")]
	public void Overlapping_imported_type_names_are_ambiguous(string declaration, string statement)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(
			("types.camp", $$"""
				namespace Left { {{declaration}} }
				namespace Right { {{declaration}} }
				"""),
			("use.camp", $$"""
				using Left;
				using Right;
				void test() { {{statement}} }
				"""));

		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("ambiguous", StringComparison.Ordinal)
			&& diagnostic.Message.Contains("Box", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("using Left; using Right;", "Left::Box box = default;", "")]
	[InlineData("using Left;", "Box box = default;", "")]
	[InlineData("using Left { Box }; using Right { Other };", "Box box = default;", "")]
	[InlineData("using Left as L; using Right as R;", "L::Box box = default;", "")]
	[InlineData("using Left; using Right;", "Box box = default;", "namespace Left;")]
	public void Explicit_or_local_type_selection_is_not_ambiguous(string imports, string statement, string localNamespace)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(
			("types.camp", """
				namespace Left { public struct Box { int side; } }
				namespace Right { public struct Box { int side; } public struct Other { } }
				"""),
			("use.camp", $$"""
				{{localNamespace}}
				{{imports}}
				void test() { {{statement}} }
				"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Fact]
	public void Local_generic_parameter_shadows_overlapping_imported_types()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(
			("types.camp", """
				namespace Left { public struct Box { } }
				namespace Right { public struct Box { } }
				"""),
			("use.camp", """
				using Left;
				using Right;
				void test<Box: copyable>(Box* value) { }
				"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Fact]
	public void Qualified_enum_and_target_typed_values_remain_unambiguous()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(
			("types.camp", """
				namespace Left { public enum Box { One, Two } }
				namespace Right { public enum Box { One, Two } }
				"""),
			("use.camp", """
				using Left;
				using Right;
				void test()
				{
					Left::Box left = One;
					Right::Box right = Right::Box.Two;
				}
				"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Theory]
	[InlineData("B::Box b = a;", "Declaration initializer")]
	[InlineData("B::Box b = default; b = a;", "Assignment")]
	[InlineData("consume(a);", "Argument")]
	[InlineData("B::Box* b = &a;", "Declaration initializer")]
	[InlineData("B::Box[] b = values;", "Declaration initializer")]
	[InlineData("B::Box? b = a;", "Declaration initializer")]
	[InlineData("const B::Box b = a;", "Declaration initializer")]
	public void Same_named_structs_in_different_namespaces_are_not_interchangeable(string statement, string context)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			namespace A { public struct Box { int side; } }
			namespace B { public struct Box { int side; } }
			void consume(B::Box value) { }
			void test(A::Box[] values)
			{
				A::Box a = default;
				{{statement}}
			}
			""");

		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.StartsWith(context, StringComparison.Ordinal)
			&& diagnostic.Message.Contains("cannot convert", StringComparison.Ordinal));
	}

	[Fact]
	public void Same_namespace_and_qualified_struct_spellings_remain_compatible()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			namespace A
			{
				struct Box { int side; }
				Box copy(A::Box value) => value;
			}
			namespace B
			{
				struct Box { int side; }
				Box copy(B::Box value) => value;
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}
}
