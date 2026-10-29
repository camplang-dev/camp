using System;
using System.Linq;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class StaticTypeQualifierTests
{
	[Theory]
	[InlineData("class Holder", "const int", "7")]
	[InlineData("struct Holder", "int", "7")]
	[InlineData("newtype Holder: long", "const Holder", "(Holder)(long)-1")]
	public void Unrelated_static_fields_do_not_enter_bare_name_lookup(string declaration, string type, string value)
	{
		var compilation = SemanticCompiler.CompileLowered($$"""
			extern escaped void* malloc(nuint size);
			extern void free(void* pointer);
			public {{declaration}} { public static {{type}} LIMIT = {{value}}; }
			public struct Other { public void test() { auto value = LIMIT; } }
			void test() { auto value = LIMIT; }
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Equal(2, compilation.AnalysisDiagnostics.Distinct().Count());
		Assert.All(compilation.AnalysisDiagnostics, diagnostic => Assert.Equal("Symbol 'LIMIT' could not be found.", diagnostic.Message));
	}

	[Fact]
	public void Own_and_qualified_static_fields_remain_visible_and_unrelated_enums_keep_their_members()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			extern escaped void* malloc(nuint size);
			extern void free(void* pointer);
			public class Holder { public static const int LIMIT = 7; public int read() => LIMIT; }
			public newtype Span: long {
				public static const Span INVALID = (Span)(long)-1;
				public static Span read() => INVALID;
			}
			enum Kind { NONE, INVALID, OTHER }
			Kind read(Kind value) {
				Kind result = INVALID;
				switch (value) { case INVALID: return result; default: return NONE; }
			}
			int qualified() => Holder.LIMIT;
			"""));
	}

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
