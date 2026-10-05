using System.Collections.Generic;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class MutableWriteTargetTests
{
	[Theory]
	[MemberData(nameof(ConstUpdateCases))]
	public void Updates_reject_const_storage(string setup, string update)
	{
		SemanticCompilation compilation = CompileUpdate(setup, update);

		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		int targetColumn = update.Contains('.')
			? 3 + update.IndexOf('.')
			: update.StartsWith("++") || update.StartsWith("--") ? 4 : 2;
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
		Assert.All(compilation.AnalysisDiagnostics, diagnostic =>
		{
			Assert.Equal("Update target is const and cannot be assigned.", diagnostic.Message);
			TokenRange range = Assert.IsType<TokenRange>(diagnostic.Range);
			Assert.Equal(5, range.StartLineNumber);
			Assert.Equal(targetColumn, range.StartColumn);
		});
	}

	[Theory]
	[MemberData(nameof(MutableUpdateCases))]
	public void Updates_accept_mutable_storage(string setup, string update)
	{
		SemanticCompiler.AssertNoDiagnostics(CompileUpdate(setup, update));
	}

	public static IEnumerable<object[]> ConstUpdateCases() => UpdateCases("const ");
	public static IEnumerable<object[]> MutableUpdateCases() => UpdateCases("");

	[Theory]
	[MemberData(nameof(ConstArrayWriteCases))]
	public void Array_writes_reject_const_elements(string setup, string write, string context)
	{
		SemanticCompilation compilation = CompileArrayWrite("const ", setup, write);

		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
		Assert.All(compilation.AnalysisDiagnostics, diagnostic =>
		{
			Assert.Equal($"{context} target is const and cannot be assigned.", diagnostic.Message);
			TokenRange range = Assert.IsType<TokenRange>(diagnostic.Range);
			Assert.Equal(5, range.StartLineNumber);
		});
	}

	[Theory]
	[MemberData(nameof(MutableArrayWriteCases))]
	public void Array_writes_accept_mutable_elements(string setup, string write)
	{
		SemanticCompiler.AssertNoDiagnostics(CompileArrayWrite("", setup, write));
	}

	[Fact]
	public void Const_array_elements_remain_readable()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			int read(const int[] items)
			{
				auto view = items[1..];
				return items[0] + view[0];
			}
			"""));
	}

	[Fact]
	public void Array_of_const_pointees_can_replace_pointer_elements()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			void replace(const int*[] items, const int* next)
			{
				items[0] = next;
			}
			"""));
	}

	[Fact]
	public void Generic_mutable_array_write_remains_valid()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			void write<T: copyable>(T[] items, in T value, sizeof(T))
			{
				items[0] = value;
			}
			"""));
	}

	[Fact]
	public void Generic_const_array_write_keeps_its_existing_diagnostic()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			void write<T: copyable>(const T[] items, in T value, sizeof(T))
			{
				items[0] = value;
			}
			""");

		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
		Assert.All(compilation.AnalysisDiagnostics, diagnostic =>
			Assert.Equal("Cannot mutate an element through const T[].", diagnostic.Message));
	}

	public static IEnumerable<object[]> ConstArrayWriteCases() => ArrayWriteCases(true);
	public static IEnumerable<object[]> MutableArrayWriteCases() => ArrayWriteCases(false);

	static IEnumerable<object[]> ArrayWriteCases(bool includeContext)
	{
		(string Setup, string Target)[] targets =
		[
			("", "items[0]"),
			("", "items[1..][0]"),
			("auto view = items[1..];", "view[0]")
		];
		foreach ((string setup, string target) in targets)
		{
			(string Write, string Context)[] writes =
			[
				($"{target} = 7", "Assignment"),
				($"{target} += 7", "Assignment"),
				($"++{target}", "Update"),
				($"--{target}", "Update"),
				($"{target}++", "Update"),
				($"{target}--", "Update")
			];
			foreach ((string write, string context) in writes)
				yield return includeContext ? [setup, write, context] : [setup, write];
		}
	}

	static SemanticCompilation CompileArrayWrite(string qualifier, string setup, string write)
	{
		return SemanticCompiler.CompileLowered($$"""
			void test({{qualifier}}int[] items)
			{
				{{setup}}

				{{write}};
			}
			""");
	}

	static IEnumerable<object[]> UpdateCases(string qualifier)
	{
		(string Setup, string Target)[] targets =
		[
			($"{qualifier}int value = 1;", "value"),
			($"{qualifier}Box box = default;", "box.value"),
			($"int value = 1; {qualifier}int* view = &value;", "*view"),
			($"Box box = default; {qualifier}Box* view = &box;", "view.value")
		];
		foreach ((string setup, string target) in targets)
		{
			yield return [setup, $"++{target}"];
			yield return [setup, $"--{target}"];
			string postfixTarget = target == "*view" ? "(*view)" : target;
			yield return [setup, $"{postfixTarget}++"];
			yield return [setup, $"{postfixTarget}--"];
		}
	}

	static SemanticCompilation CompileUpdate(string setup, string update)
	{
		return SemanticCompiler.CompileLowered($$"""
			struct Box { int value; }
			void test()
			{
				{{setup}}
				{{update}};
			}
			""");
	}
}
