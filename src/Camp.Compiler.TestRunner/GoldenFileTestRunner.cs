using System;
using System.IO;
using System.Linq;
using System.Text;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public static class GoldenFileTestRunner
{
	public static void Run(GoldenFileTestCase testCase)
	{
		ArgumentNullException.ThrowIfNull(testCase);

		CompilerResult result = CompilerDriver.Execute(CreateRequest(testCase));
		string actual = Normalize(SelectOutput(testCase, result));
		File.WriteAllText(testCase.ActualPath, actual);

		if (!File.Exists(testCase.ExpectedPath))
		{
			File.WriteAllText(testCase.ExpectedPath, "");
			Assert.Fail($"Missing golden file. Created empty expected file at '{testCase.ExpectedPath}' and wrote actual output to '{testCase.ActualPath}'.");
		}

		string expected = Normalize(File.ReadAllText(testCase.ExpectedPath));
		if (expected != actual)
			Assert.Fail($"Golden file mismatch. Expected: '{testCase.ExpectedPath}'. Actual: '{testCase.ActualPath}'.");

		File.Delete(testCase.ActualPath);
	}

	static CompilerRequest CreateRequest(GoldenFileTestCase testCase)
	{
		if (testCase.Kind == GoldenFileTestKind.CEmit)
		{
			string buildDirectory = GetBuildDirectory(testCase);
			if (Directory.Exists(buildDirectory))
				Directory.Delete(buildDirectory, recursive: true);
		}

		CompilerRequest request = new()
		{
			RuntimeRoot = Path.Combine(testCase.RepositoryRoot, "bin"),
			TargetRoot = Path.Combine(testCase.RepositoryRoot, "targets"),
			PackageSourceRoot = Path.Combine(testCase.RepositoryRoot, "lib"),
			PackageArtifactRoot = Path.Combine(testCase.RepositoryRoot, "tmp", "golden-packages"),
			WorkingDirectory = testCase.RepositoryRoot,
			NoStdLib = true,
			BuildDir = GetBuildDirectory(testCase),
			Inspect = testCase.Kind switch
			{
				GoldenFileTestKind.Ast => CompilerInspectMode.Ast,
				GoldenFileTestKind.Lowering => CompilerInspectMode.Lowering,
				GoldenFileTestKind.Diagnostics => CompilerInspectMode.Lowering,
				GoldenFileTestKind.CEmit => null,
				_ => throw new ArgumentOutOfRangeException()
			}
		};
		request.Files.Add(Path.GetRelativePath(testCase.RepositoryRoot, testCase.CasePath));
		return request;
	}

	static string SelectOutput(GoldenFileTestCase testCase, CompilerResult result)
	{
		return testCase.Kind switch
		{
			GoldenFileTestKind.Diagnostics => result.StdErr,
			GoldenFileTestKind.CEmit => ReadGeneratedFiles(testCase),
			_ => result.StdOut
		};
	}

	static string ReadGeneratedFiles(GoldenFileTestCase testCase)
	{
		string buildDirectory = GetBuildDirectory(testCase);
		if (!Directory.Exists(buildDirectory))
			return "";
		StringBuilder builder = new();
		foreach (string file in Directory.GetFiles(buildDirectory)
			.Where(static path => Path.GetExtension(path) is ".c" or ".h")
			.OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal))
		{
			builder.Append("// file: ").Append(Path.GetFileName(file)).Append('\n');
			builder.Append(Normalize(File.ReadAllText(file)));
			if (builder.Length == 0 || builder[^1] != '\n')
				builder.Append('\n');
		}
		return builder.ToString();
	}

	static string GetBuildDirectory(GoldenFileTestCase testCase)
	{
		string caseName = Path.GetFileNameWithoutExtension(testCase.CasePath);
		return Path.Combine(testCase.RepositoryRoot, "tmp", "golden-cemit", caseName);
	}

	static string Normalize(string text)
	{
		text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
		if (text.Length > 0 && !text.EndsWith('\n'))
			text += "\n";
		return text;
	}
}
