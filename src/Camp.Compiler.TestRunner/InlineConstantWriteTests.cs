using System.Collections.Generic;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class InlineConstantWriteTests
{
	[Theory]
	[MemberData(nameof(InlineWrites))]
	public void Writes_reject_inline_constants(string declarations, string write, string context)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			{{declarations}}
			void test()
			{
				{{write}};
			}
			""");

		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
		Assert.All(compilation.AnalysisDiagnostics, diagnostic =>
		{
			Assert.Equal($"{context} target is an inline constant and cannot be assigned.", diagnostic.Message);
			TokenRange range = Assert.IsType<TokenRange>(diagnostic.Range);
			Assert.Equal(4, range.StartLineNumber);
		});
	}

	public static IEnumerable<object[]> InlineWrites()
	{
		(string Declarations, string Target)[] constants =
		[
			("inline int LIMIT = 10;", "LIMIT"),
			("struct Values { inline int LIMIT = 10; }", "(Values.LIMIT)"),
			("namespace Values { inline int LIMIT = 10; }", "Values::LIMIT"),
			("struct Values { inline int LIMIT = 10; }", "Values.LIMIT"),
			("static class Values { inline int LIMIT = 10; }", "Values.LIMIT")
		];
		foreach ((string declarations, string target) in constants)
		{
			yield return [declarations, $"{target} = 11", "Assignment"];
			yield return [declarations, $"{target} += 1", "Assignment"];
			yield return [declarations, $"++{target}", "Update"];
			yield return [declarations, $"--{target}", "Update"];
			yield return [declarations, $"{target}++", "Update"];
			yield return [declarations, $"{target}--", "Update"];
		}
		yield return ["inline string NAME = \"Camp\";", "NAME = \"next\"", "Assignment"];
		yield return ["struct Values { inline string NAME = \"Camp\"; }", "Values.NAME = \"next\"", "Assignment"];
		yield return ["static class Values { inline string NAME = \"Camp\"; }", "Values.NAME = \"next\"", "Assignment"];
		yield return ["inline int* POINTER = null;", "POINTER = null", "Assignment"];
		yield return ["inline fn void() CALLBACK = default;", "CALLBACK = default", "Assignment"];
	}

	[Fact]
	public void Inline_constant_reads_and_mutable_copies_remain_valid()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			inline int LIMIT = 10;
			struct Values { inline int LIMIT = 20; }
			int read()
			{
				auto value = LIMIT + Values.LIMIT;
				++value;
				value += LIMIT;
				return value;
			}
			"""));
	}

	[Fact]
	public void Inline_constant_names_still_cannot_be_reused()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			inline int LIMIT = 10;
			void test() { int LIMIT = 1; LIMIT = 2; ++LIMIT; LIMIT++; }
			""");

		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
		Assert.All(compilation.AnalysisDiagnostics, diagnostic =>
			Assert.Equal("Name 'LIMIT' conflicts with inline constant 'LIMIT'; inline constant names cannot be reused.", diagnostic.Message));
	}

	[Fact]
	public void Mutable_string_storage_remains_writable()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			string name = "Camp";
			class Holder { string name; }
			static class Values { static string name = "Camp"; }
			void test(Holder* h)
			{
				name = "next";
				h.name = "next";
				Values.name = "next";
			}
			"""));
	}
}
