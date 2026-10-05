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
