using System;
using System.IO;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public static class GoldenFileTestRunner
{
	public static void Run(GoldenFileTestCase testCase)
	{
		ArgumentNullException.ThrowIfNull(testCase);

		CompilerResult result = CompilerDriver.Execute(CreateRequest(testCase));
		string actual = Normalize(SelectOutput(testCase.Kind, result));
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
		CompilerRequest request = new()
		{
			AssetRoot = Path.Combine(testCase.RepositoryRoot, "src", "assets"),
			WorkingDirectory = testCase.RepositoryRoot,
			NoStdLib = true,
			Inspect = testCase.Kind switch
			{
				GoldenFileTestKind.Ast => CompilerInspectMode.Ast,
				GoldenFileTestKind.Lowering => CompilerInspectMode.Lowering,
				GoldenFileTestKind.Diagnostics => CompilerInspectMode.Lowering,
				_ => throw new ArgumentOutOfRangeException()
			}
		};
		request.Files.Add(Path.GetRelativePath(testCase.RepositoryRoot, testCase.CasePath));
		return request;
	}

	static string SelectOutput(GoldenFileTestKind kind, CompilerResult result)
	{
		return kind == GoldenFileTestKind.Diagnostics ? result.StdErr : result.StdOut;
	}

	static string Normalize(string text)
	{
		text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
		if (text.Length > 0 && !text.EndsWith('\n'))
			text += "\n";
		return text;
	}
}
