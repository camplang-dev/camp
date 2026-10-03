using System;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class NamespaceBindingTests
{
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
