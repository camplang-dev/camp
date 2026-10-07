using System;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class StaticTypeQualifierTests
{
	[Theory]
	[InlineData("class", "static", "int getCatalog() => 0;")]
	[InlineData("class", "", "int getCatalog() => 0;")]
	[InlineData("class", "static", "int Catalog;")]
	[InlineData("class", "", "int Catalog;")]
	[InlineData("static class", "static", "int getCatalog() => 0;")]
	[InlineData("static class", "", "int getCatalog() => 0;")]
	[InlineData("static class", "static", "int Catalog;")]
	[InlineData("static class", "", "int Catalog;")]
	public void Type_qualified_calls_ignore_unqualified_instance_members(string typeKind, string modifier, string member)
	{
		string visibility = typeKind == "class" ? "public " : "";
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			extern void* malloc(nuint size);
			extern void free(void* pointer);
			{{visibility}}{{typeKind}} Catalog { public static int begin() => 1; }
			class Holder
			{
				{{member}}
				{{modifier}} int make() { return Catalog.begin(); }
			}
			"""));
	}

	[Theory]
	[InlineData("class")]
	[InlineData("static class")]
	public void Static_call_can_supply_its_omitted_out_result(string typeKind)
	{
		string visibility = typeKind == "class" ? "public " : "";
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			extern void* malloc(nuint size);
			extern void free(void* pointer);
			{{visibility}}{{typeKind}} Catalog { public static void begin(out int value) { value = 7; } }
			class Holder
			{
				int getCatalog() => 0;
				static int make() { return Catalog.begin(); }
			}
			"""));
	}

	[Theory]
	[InlineData("int getCatalog() => 0;", "Catalog", "Member property")]
	[InlineData("int Catalog;", "Catalog", "Member field")]
	[InlineData("int begin() => 1;", "begin()", "Member method")]
	public void Bare_instance_members_still_require_this(string member, string expression, string kind)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			class Holder { {{member}} int make() { return {{expression}}; } }
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.StartsWith(kind, StringComparison.Ordinal)
			&& diagnostic.Message.Contains("requires explicit 'this.' qualification", StringComparison.Ordinal));
	}

	[Fact]
	public void Local_receiver_still_shadows_the_type_qualifier()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			extern void* malloc(nuint size);
			extern void free(void* pointer);
			public class Catalog { public static int begin(int value) => value; }
			public class Receiver { public int begin() => 2; }
			class Holder
			{
				int getCatalog() => 0;
				static int make(Receiver* Catalog) { return Catalog.begin(); }
			}
			"""));
	}
}
