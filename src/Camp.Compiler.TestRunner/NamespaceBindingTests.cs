using System;
using System.Linq;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class NamespaceBindingTests
{
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
