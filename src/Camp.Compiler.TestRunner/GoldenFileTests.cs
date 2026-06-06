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
		foreach (string casePath in Directory.GetFiles(casesRoot, "*.camp", SearchOption.AllDirectories)
			.Where(static path => !Path.GetFileName(path).Contains(".expected.", StringComparison.Ordinal) && !Path.GetFileName(path).Contains(".actual.", StringComparison.Ordinal))
			.OrderBy(static path => path, StringComparer.Ordinal))
		{
			yield return [new GoldenFileTestCase
			{
				RepositoryRoot = repositoryRoot,
				CasePath = casePath,
				Kind = GetKind(casesRoot, casePath)
			}];
		}
	}

	static GoldenFileTestKind GetKind(string casesRoot, string casePath)
	{
		string relative = Path.GetRelativePath(casesRoot, casePath);
		string folder = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
		return folder switch
		{
			"Ast" => GoldenFileTestKind.Ast,
			"Lowering" => GoldenFileTestKind.Lowering,
			"Diagnostics" => GoldenFileTestKind.Diagnostics,
			"CEmit" => GoldenFileTestKind.CEmit,
			"CCompile" => GoldenFileTestKind.CCompile,
			"Std" => GoldenFileTestKind.Std,
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
