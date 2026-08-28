using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class StdlibCampTests
{
	static readonly Lazy<IReadOnlyList<StdlibCampTestCase>> DiscoveredTests = new(DiscoverTests);
	static readonly ConcurrentDictionary<string, Lazy<StdlibHarnessExecution>> Executions = new(StringComparer.Ordinal);

	[Theory]
	[MemberData(nameof(GetCases))]
	public void Standard_library_camp_test_passes(StdlibCampTestCase testCase)
	{
		if (!string.IsNullOrWhiteSpace(testCase.DiscoveryFailure))
			throw new Xunit.Sdk.XunitException(testCase.DiscoveryFailure);
		if (testCase.Skipped)
			Assert.Skip(string.IsNullOrWhiteSpace(testCase.SkipReason) ? "Skipped by Camp @skip." : testCase.SkipReason);

		StdlibHarnessExecution execution = Executions.GetOrAdd(ExecutionKey(), static _ => new Lazy<StdlibHarnessExecution>(RunHarness)).Value;
		if (!string.IsNullOrWhiteSpace(execution.InfrastructureFailure))
			throw new Xunit.Sdk.XunitException(execution.InfrastructureFailure);
		if (!execution.ResultsById.TryGetValue(testCase.Id, out CampTestResultEntry? result))
			throw new Xunit.Sdk.XunitException($"Stdlib Camp test '{testCase.Id}' did not appear in {execution.ResultsPath}.");
		if (result.Outcome == "skipped")
			Assert.Skip(result.Failure?.Message ?? testCase.SkipReason ?? "Skipped by Camp test harness.");
		if (result.Outcome != "passed")
			throw new Xunit.Sdk.XunitException(FormatFailure(testCase, result, execution));
	}

	public static IEnumerable<object[]> GetCases()
	{
		foreach (StdlibCampTestCase testCase in DiscoveredTests.Value)
			yield return [testCase];
	}

	static IReadOnlyList<StdlibCampTestCase> DiscoverTests()
	{
		CampProjectLoadResult load = LoadStdlib(CampProjectCommandKind.Test, "discovery");
		if (!load.Success)
			return [DiscoveryFailure("Stdlib Camp test discovery request failed:" + Environment.NewLine + string.Join(Environment.NewLine, load.Diagnostics))];

		load.Request.ListTests = true;
		load.Request.TestResultFormat = "json";
		CompilerResult result = CompilerDriver.Execute(load.Request);
		if (result.ExitCode != 0)
			return [DiscoveryFailure("Stdlib Camp test discovery failed:" + Environment.NewLine + result.StdErr + result.StdOut)];
		string? manifestPath = result.GeneratedFiles.FirstOrDefault(static file => file.EndsWith(".camp-test-manifest.json", StringComparison.OrdinalIgnoreCase));
		if (manifestPath is null || !File.Exists(manifestPath))
			return [DiscoveryFailure("Stdlib Camp test discovery did not emit a test manifest.")];
		if (!CampTestManifestJsonSerializer.TryParse(File.ReadAllText(manifestPath), out CampTestManifest? manifest, out List<string> diagnostics))
			return [DiscoveryFailure("Stdlib Camp test manifest could not be parsed:" + Environment.NewLine + string.Join(Environment.NewLine, diagnostics))];
		if (manifest.Tests.Count == 0)
			return [DiscoveryFailure("Stdlib Camp test discovery found no tests.")];
		return manifest.Tests
			.OrderBy(static test => test.QualifiedName, StringComparer.Ordinal)
			.Select(static test => new StdlibCampTestCase
			{
				Id = test.Id,
				Name = test.Name,
				QualifiedName = test.QualifiedName,
				Sourcefile = test.Sourcefile,
				Sourceline = test.Sourceline,
				Summary = test.Summary,
				Skipped = test.Skipped,
				SkipReason = test.SkipReason
			})
			.ToList();
	}

	static StdlibHarnessExecution RunHarness()
	{
		using IDisposable gate = TestResourceGate.EnterNative();
		CampProjectLoadResult load = LoadStdlib(CampProjectCommandKind.Test, "execution");
		if (!load.Success)
			return StdlibHarnessExecution.Failed("Stdlib Camp test request failed:" + Environment.NewLine + string.Join(Environment.NewLine, load.Diagnostics));

		load.Request.TestResultFormat = "json";
		CompilerResult result = CompilerDriver.Execute(load.Request);
		string? resultsPath = result.GeneratedFiles.FirstOrDefault(static file => file.EndsWith(".camp-test-results.json", StringComparison.OrdinalIgnoreCase));
		if (resultsPath is null || !File.Exists(resultsPath))
		{
			string message = "Stdlib Camp test execution did not emit test results.";
			if (!string.IsNullOrWhiteSpace(result.StdErr + result.StdOut))
				message += Environment.NewLine + result.StdErr + result.StdOut;
			return StdlibHarnessExecution.Failed(message);
		}
		if (!CampTestResultsJsonSerializer.TryParse(File.ReadAllText(resultsPath), out CampTestResults results, out List<string> diagnostics))
			return StdlibHarnessExecution.Failed("Stdlib Camp test results could not be parsed:" + Environment.NewLine + string.Join(Environment.NewLine, diagnostics), resultsPath);
		if (result.ExitCode != 0 && results.Summary.Total == 0)
			return StdlibHarnessExecution.Failed("Stdlib Camp test execution failed before running tests:" + Environment.NewLine + result.StdErr + result.StdOut, resultsPath);
		return new StdlibHarnessExecution(
			results.Tests.ToDictionary(static test => test.Id, StringComparer.Ordinal),
			resultsPath,
			Path.GetDirectoryName(resultsPath) ?? "",
			null);
	}

	static CampProjectLoadResult LoadStdlib(CampProjectCommandKind command, string outputName)
	{
		string repositoryRoot = FindRepositoryRoot();
		string stdRoot = Path.Combine(repositoryRoot, "lib", "std");
		CampProjectEnvironment environment = CampProjectEnvironment.Create(stdRoot, Path.Combine(repositoryRoot, "bin"));
		CampProjectLoadResult load = CampProjectLoader.LoadBuildFile(Path.Combine(stdRoot, "std.campbuild"), environment, command);
		if (load.Success)
			load.Request.OutDir = Path.Combine(repositoryRoot, "tmp", "stdlib-camp-tests", outputName);
		return load;
	}

	static StdlibCampTestCase DiscoveryFailure(string message)
	{
		return new StdlibCampTestCase
		{
			Id = "stdlib-discovery-failure",
			Name = "stdlib-discovery-failure",
			QualifiedName = "stdlib-discovery-failure",
			Sourcefile = "",
			Sourceline = 0,
			Summary = "",
			Skipped = false,
			SkipReason = null,
			DiscoveryFailure = message
		};
	}

	static string FormatFailure(StdlibCampTestCase testCase, CampTestResultEntry result, StdlibHarnessExecution execution)
	{
		List<string> lines =
		[
			$"Stdlib Camp test '{testCase.QualifiedName}' reported outcome '{result.Outcome}'.",
			$"Declared at {testCase.Sourcefile}:{testCase.Sourceline}.",
			$"Results: {execution.ResultsPath}",
			$"Artifacts: {execution.ArtifactDirectory}"
		];
		if (result.Failure is not null)
		{
			lines.Add($"Failure: {result.Failure.Kind}: {result.Failure.Message}");
			if (!string.IsNullOrWhiteSpace(result.Failure.Sourcefile))
				lines.Add($"Failure location: {result.Failure.Sourcefile}:{result.Failure.Sourceline}");
		}
		if (result.Memory is not null && result.Memory.LiveAllocations > 0)
			lines.Add($"Memory: {result.Memory.LiveAllocations} live allocations, {result.Memory.LiveBytes} live bytes.");
		return string.Join(Environment.NewLine, lines);
	}

	static string ExecutionKey()
	{
		return OperatingSystem.IsWindows() ? "windows-default" : OperatingSystem.IsLinux() ? "linux-default" : OperatingSystem.IsMacOS() ? "macos-default" : "default";
	}

	static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "src", "camplang.sln")))
				return directory.FullName;
			directory = directory.Parent;
		}
		throw new InvalidOperationException("Could not find repository root containing src/camplang.sln.");
	}

	sealed record StdlibHarnessExecution(
		IReadOnlyDictionary<string, CampTestResultEntry> ResultsById,
		string ResultsPath,
		string ArtifactDirectory,
		string? InfrastructureFailure)
	{
		public static StdlibHarnessExecution Failed(string message, string? resultsPath = null)
		{
			return new StdlibHarnessExecution(new Dictionary<string, CampTestResultEntry>(StringComparer.Ordinal), resultsPath ?? "", "", message);
		}
	}
}
