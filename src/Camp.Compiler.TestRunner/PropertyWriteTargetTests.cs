using System.Collections.Generic;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class PropertyWriteTargetTests
{
	[Theory]
	[MemberData(nameof(GetterOnlyWrites))]
	public void Writes_reject_getter_only_properties(string declarations, string target, string write)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			{{declarations}}
			void test(Holder* h)
			{
				{{write}};
			}
			""");

		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
		Assert.All(compilation.AnalysisDiagnostics, diagnostic =>
		{
			Assert.StartsWith("Property 'Total' is not writable on type '", diagnostic.Message);
			TokenRange range = Assert.IsType<TokenRange>(diagnostic.Range);
			Assert.Equal(4, range.StartLineNumber);
			Assert.Equal(2 + write.IndexOf(target) + target.IndexOf('.') + 1, range.StartColumn);
		});
	}

	public static IEnumerable<object[]> GetterOnlyWrites()
	{
		(string Declarations, string Target)[] properties =
		[
			("class Holder { int getTotal() => 1; }", "h.Total"),
			("class Holder { int getTotal() => 1; }", "(h.Total)"),
			("class Holder { int getTotal(int index) => index; }", "h.Total[0]"),
			("class Holder { } int getTotal(const Holder* this) => 1;", "h.Total"),
			("class Holder { static int getTotal() => 1; }", "Holder.Total"),
			("class Holder { } static class Values { static int getTotal() => 1; }", "Values.Total")
		];
		foreach ((string declarations, string target) in properties)
		{
			yield return [declarations, target, $"++{target}"];
			yield return [declarations, target, $"--{target}"];
			yield return [declarations, target, $"{target}++"];
			yield return [declarations, target, $"{target}--"];
		}
		foreach (string write in new[] { "h.Total = 2", "h.Total += 2" })
			yield return ["class Holder { int getTotal() => 1; }", "h.Total", write];
	}

	[Fact]
	public void Getter_only_property_remains_readable()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			class Holder { int getTotal() => 1; }
			int read(Holder* h) => h.Total;
			"""));
	}

	[Fact]
	public void Getter_only_array_property_can_mutate_its_elements()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			class Holder { int[] items; int[] getItems() => this.items; }
			void update(Holder* h) { ++h.Items[0]; h.Items[0]++; }
			"""));
	}
}
