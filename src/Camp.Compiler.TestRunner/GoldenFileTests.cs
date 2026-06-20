using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class GoldenFileTests
{
	[Theory]
	[MemberData(nameof(GetCases))]
	public void Golden_output_matches_expected(GoldenFileTestCase testCase)
	{
		GoldenFileTestRunner.Run(testCase);
	}

	public static IEnumerable<object[]> GetCases()
	{
		string repositoryRoot = FindRepositoryRoot();
		string casesRoot = Path.Combine(repositoryRoot, "tests");
		string? kindFilter = Environment.GetEnvironmentVariable("CAMP_TEST_KIND");
		string? caseFilter = Environment.GetEnvironmentVariable("CAMP_TEST_CASE");
		foreach (string casePath in Directory.GetFiles(casesRoot, "*.camp", SearchOption.AllDirectories)
			.Where(static path => !Path.GetFileName(path).Contains(".expected.", StringComparison.Ordinal) && !Path.GetFileName(path).Contains(".actual.", StringComparison.Ordinal))
			.OrderBy(static path => path, StringComparer.Ordinal))
		{
			GoldenFileTestKind kind = GetKind(casesRoot, casePath);
			string relativePath = Path.GetRelativePath(casesRoot, casePath).Replace('\\', '/');
			if (!MatchesFilter(kind.ToString(), kindFilter) || !MatchesCaseFilter(relativePath, caseFilter))
				continue;

			yield return [new GoldenFileTestCase
			{
				RepositoryRoot = repositoryRoot,
				CasePath = casePath,
				Kind = kind
			}];
		}
	}

	static bool MatchesFilter(string value, string? filter)
	{
		if (string.IsNullOrWhiteSpace(filter))
			return true;
		return filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Any(item => value.Equals(item, StringComparison.OrdinalIgnoreCase));
	}

	static bool MatchesCaseFilter(string relativePath, string? filter)
	{
		if (string.IsNullOrWhiteSpace(filter))
			return true;
		string extensionless = Path.ChangeExtension(relativePath, null)?.Replace('\\', '/') ?? relativePath;
		return filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Any(item => extensionless.Contains(item.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
	}

	static GoldenFileTestKind GetKind(string casesRoot, string casePath)
	{
		string relative = Path.GetRelativePath(casesRoot, casePath);
		string folder = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
		return folder switch
		{
			"Ast" => GoldenFileTestKind.Ast,
			"Declarations" => GoldenFileTestKind.Declarations,
			"Lowering" => GoldenFileTestKind.Lowering,
			"Diagnostics" => GoldenFileTestKind.Diagnostics,
			"CEmit" => GoldenFileTestKind.CEmit,
			"CCompile" => GoldenFileTestKind.CCompile,
			"Api" => GoldenFileTestKind.Api,
			"Metadata" => GoldenFileTestKind.Metadata,
			"Std" => GoldenFileTestKind.Std,
			"StdRun" => GoldenFileTestKind.StdRun,
			_ => throw new InvalidOperationException($"Test case '{casePath}' is not under a supported tests kind folder.")
		};
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
}
