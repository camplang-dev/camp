using System;
using System.IO;
using System.Threading;

namespace Camp.Compiler.Tests;

public static class TestMetrics
{
	static int goldenCases;
	static int nativeCompileCases;
	static int stdRunCases;
	static int externalCampcInvocations;

	static TestMetrics()
	{
		AppDomain.CurrentDomain.ProcessExit += static (_, _) => WriteReport();
	}

	public static void RecordGoldenCase(GoldenFileTestKind kind)
	{
		Interlocked.Increment(ref goldenCases);
		if (kind is GoldenFileTestKind.CCompile or GoldenFileTestKind.StdRun)
			Interlocked.Increment(ref nativeCompileCases);
		if (kind == GoldenFileTestKind.StdRun)
			Interlocked.Increment(ref stdRunCases);
	}

	public static void RecordExternalCampcInvocation()
	{
		Interlocked.Increment(ref externalCampcInvocations);
	}

	static void WriteReport()
	{
		if (goldenCases == 0 && nativeCompileCases == 0 && stdRunCases == 0 && externalCampcInvocations == 0)
			return;
		if (Environment.GetEnvironmentVariable("CAMP_TEST_METRICS") is string value && value.Equals("0", StringComparison.OrdinalIgnoreCase))
			return;

		string summary = "Camp test metrics: "
			+ $"pid={Environment.ProcessId} "
			+ $"golden_cases={goldenCases} "
			+ $"native_compile_cases={nativeCompileCases} "
			+ $"stdrun_cases={stdRunCases} "
			+ $"external_campc_invocations={externalCampcInvocations}";
		Console.Error.WriteLine(summary);
		WriteFileReport(summary);
	}

	static void WriteFileReport(string summary)
	{
		try
		{
			string repositoryRoot = FindRepositoryRoot();
			string path = Environment.GetEnvironmentVariable("CAMP_TEST_METRICS_PATH") ?? Path.Combine(repositoryRoot, "tmp", "camp-test-metrics.txt");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.AppendAllText(path, summary + Environment.NewLine);
		}
		catch (Exception)
		{
			// Metrics are diagnostic-only and must never fail the test run.
		}
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
		return Directory.GetCurrentDirectory();
	}
}
