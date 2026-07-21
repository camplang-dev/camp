using System.Collections.Generic;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CampCoverageDecorationTests
{
	[Fact]
	public void Decorations_mark_only_executable_lines_as_covered_or_uncovered()
	{
		CampCoverageMap map = new(
			new Dictionary<int, string> { [1] = "src/main.camp" },
			new Dictionary<int, string> { [1] = "main" },
			[
				new CampCoverageCounter(0, CampCoverageCounterKind.Function, 1, 1, 1),
				new CampCoverageCounter(1, CampCoverageCounterKind.Line, 1, 3, 1),
				new CampCoverageCounter(2, CampCoverageCounterKind.Line, 1, 5, 1)
			]);
		CampCoverageResults results = new(
			new CampCoverageMetric(1, 2),
			new CampCoverageMetric(1, 1),
			[
				new CampCoverageFileResult(
					"src/main.camp",
					new CampCoverageMetric(1, 2),
					new CampCoverageMetric(1, 1),
					[5])
			]);

		IReadOnlyList<CampCoverageLineDecoration> decorations = CampCoverageDecorationService.Create(map, results);

		Assert.Equal(2, decorations.Count);
		Assert.Contains(decorations, static decoration => decoration.Line == 3 && decoration.Kind == CampCoverageLineDecorationKind.CoveredExecutableLine);
		Assert.Contains(decorations, static decoration => decoration.Line == 5 && decoration.Kind == CampCoverageLineDecorationKind.UncoveredExecutableLine);
		Assert.DoesNotContain(decorations, static decoration => decoration.Line == 1);
	}

	[Fact]
	public void Coverage_result_import_reports_invalid_files()
	{
		string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "camp-tests", "coverage-decoration-" + System.Guid.NewGuid().ToString("N"));
		System.IO.Directory.CreateDirectory(root);
		string map = System.IO.Path.Combine(root, "map.csv");
		string results = System.IO.Path.Combine(root, "results.json");
		System.IO.File.WriteAllText(map, "bad\n");
		System.IO.File.WriteAllText(results, "{}");

		bool success = CampCoverageDecorationService.TryCreateFromFiles(map, results, out _, out List<string> diagnostics);

		Assert.False(success);
		Assert.Contains(diagnostics, static diagnostic => diagnostic.Contains("coverage map", System.StringComparison.Ordinal));
	}
}
