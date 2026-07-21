using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CampTestResultDiagnosticTests
{
	[Fact]
	public void Assertion_failures_map_to_captured_source_location_diagnostics()
	{
		CampTestResults results = new(
			new CampTestResultSummary(0, 1, 0, 0, 0, 1),
			[
				new CampTestResultEntry(
					"Tests::fails",
					"fails",
					"Tests::fails",
					"tests.camp",
					3,
					"",
					"failed",
					1.25,
					new CampTestFailure("assertion", "x == y", "src/source.camp", 42))
			]);

		CampSourceDiagnostic diagnostic = Assert.Single(CampTestResultDiagnosticService.Create(results));

		Assert.Equal("src/source.camp", diagnostic.Path);
		Assert.Equal(41, diagnostic.Range?.Start.Line);
		Assert.Equal("x == y", diagnostic.Message);
		Assert.Equal("assertion", diagnostic.Code);
		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
	}
}
