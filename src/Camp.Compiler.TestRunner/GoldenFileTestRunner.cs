using System;
using System.Collections.Generic;
using System.Diagnostics;
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

		DeleteActualFiles(testCase);
	}

	static void DeleteActualFiles(GoldenFileTestCase testCase)
	{
		string? directory = Path.GetDirectoryName(testCase.CasePath);
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
			return;

		string prefix = Path.GetFileNameWithoutExtension(testCase.CasePath) + ".actual.";
		foreach (string actualPath in Directory.GetFiles(directory, prefix + "*"))
			File.Delete(actualPath);
	}

	static CompilerRequest CreateRequest(GoldenFileTestCase testCase)
	{
		if (testCase.Kind is GoldenFileTestKind.CEmit or GoldenFileTestKind.CCompile or GoldenFileTestKind.StdRun)
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
			PackageArtifactRoot = testCase.Kind == GoldenFileTestKind.StdRun
				? Path.Combine(GetBuildDirectory(testCase), "packages")
				: Path.Combine(testCase.RepositoryRoot, "tmp", "golden-packages"),
			WorkingDirectory = testCase.RepositoryRoot,
			NoStdLib = testCase.Kind is not (GoldenFileTestKind.Std or GoldenFileTestKind.StdRun),
			BuildDir = GetBuildDirectory(testCase),
			OutDir = testCase.Kind == GoldenFileTestKind.StdRun ? Path.Combine(GetBuildDirectory(testCase), "out") : null,
			BuildKind = testCase.Kind == GoldenFileTestKind.StdRun ? NativeBuildKind.Exec : null,
			Inspect = testCase.Kind switch
			{
				GoldenFileTestKind.Ast => CompilerInspectMode.Ast,
				GoldenFileTestKind.Lowering => CompilerInspectMode.Lowering,
				GoldenFileTestKind.Diagnostics => CompilerInspectMode.Lowering,
				GoldenFileTestKind.Std => CompilerInspectMode.Lowering,
				GoldenFileTestKind.StdRun => null,
				GoldenFileTestKind.Api => null,
				GoldenFileTestKind.CEmit => null,
				GoldenFileTestKind.CCompile => null,
				_ => throw new ArgumentOutOfRangeException()
			},
			InspectApi = testCase.Kind == GoldenFileTestKind.Api
		};
		ApplyCaseOptions(testCase, request);
		request.Files.Add(Path.GetRelativePath(testCase.RepositoryRoot, testCase.CasePath));
		return request;
	}

	static void ApplyCaseOptions(GoldenFileTestCase testCase, CompilerRequest request)
	{
		foreach (string line in File.ReadLines(testCase.CasePath))
		{
			string trimmed = line.Trim();
			if (!trimmed.StartsWith("// @", StringComparison.Ordinal))
				continue;
			string option = trimmed[4..].Trim();
			if (option.Equals("build shared", StringComparison.OrdinalIgnoreCase))
			{
				request.BuildKind = NativeBuildKind.Shared;
				request.OutDir = Path.Combine(GetBuildDirectory(testCase), "out");
			}
		}
	}

	static string SelectOutput(GoldenFileTestCase testCase, CompilerResult result)
	{
		return testCase.Kind switch
		{
			GoldenFileTestKind.Diagnostics => result.StdErr,
			GoldenFileTestKind.CEmit => ReadGeneratedFiles(testCase),
			GoldenFileTestKind.CCompile => CompileGeneratedC(testCase, result),
			GoldenFileTestKind.StdRun => RunGeneratedExecutable(testCase, result),
			GoldenFileTestKind.Api => result.StdOut,
			_ => result.StdOut
		};
	}

	static string RunGeneratedExecutable(GoldenFileTestCase testCase, CompilerResult result)
	{
		StringBuilder builder = new();
		if (result.ExitCode != 0)
		{
			builder.AppendLine("compiler: failed");
			if (!string.IsNullOrWhiteSpace(result.StdErr))
				builder.Append(Normalize(result.StdErr));
			if (!string.IsNullOrWhiteSpace(result.StdOut))
				builder.Append(Normalize(result.StdOut));
			return builder.ToString();
		}

		string? executable = result.GeneratedFiles
			.Where(File.Exists)
			.Where(static path => Path.GetExtension(path) is not ".c" and not ".h" and not ".o" and not ".a" and not ".camp")
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
		if (executable is null)
			return "run: no executable\n";

		ProcessResult run = RunProcess(executable, [], testCase.RepositoryRoot);
		builder.AppendLine("exit: " + run.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
		if (!string.IsNullOrWhiteSpace(run.StdOut))
			builder.Append(Normalize(run.StdOut));
		if (!string.IsNullOrWhiteSpace(run.StdErr))
			builder.Append(Normalize(run.StdErr));
		return builder.ToString();
	}

	static string CompileGeneratedC(GoldenFileTestCase testCase, CompilerResult result)
	{
		StringBuilder builder = new();
		if (result.ExitCode != 0)
		{
			builder.AppendLine("compiler: failed");
			if (!string.IsNullOrWhiteSpace(result.StdErr))
				builder.Append(Normalize(result.StdErr));
			if (!string.IsNullOrWhiteSpace(result.StdOut))
				builder.Append(Normalize(result.StdOut));
			return builder.ToString();
		}

		builder.Append(ReadGeneratedFiles(testCase));
		if (builder.Length > 0 && builder[^1] != '\n')
			builder.Append('\n');
		builder.AppendLine("// compile");

		List<string> sourceFiles = result.GeneratedFiles
			.Where(static path => Path.GetExtension(path) == ".c")
			.OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
			.ToList();
		if (sourceFiles.Count == 0)
		{
			builder.AppendLine("compile: no C source files");
			return builder.ToString();
		}

		string objectDirectory = Path.Combine(GetBuildDirectory(testCase), "obj");
		Directory.CreateDirectory(objectDirectory);
		foreach (string sourceFile in sourceFiles)
		{
			string objectFile = Path.Combine(objectDirectory, Path.GetFileNameWithoutExtension(sourceFile) + ".o");
			ProcessResult compile = RunProcess("clang", ["-std=c99", "-Werror=incompatible-pointer-types", "-c", sourceFile, "-o", objectFile], testCase.RepositoryRoot);
			if (compile.ExitCode == 0)
			{
				builder.AppendLine("compiled: " + Path.GetFileName(sourceFile));
				continue;
			}

			builder.AppendLine("compile failed: " + Path.GetFileName(sourceFile));
			builder.Append(Normalize(compile.StdOut));
			builder.Append(Normalize(compile.StdErr));
		}
		return builder.ToString();
	}

	static ProcessResult RunProcess(string executable, IReadOnlyList<string> arguments, string workingDirectory)
	{
		ProcessStartInfo startInfo = new(executable)
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (string argument in arguments)
			startInfo.ArgumentList.Add(argument);

		try
		{
			using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{executable}'.");
			string stdout = process.StandardOutput.ReadToEnd();
			string stderr = process.StandardError.ReadToEnd();
			process.WaitForExit();
			return new ProcessResult(process.ExitCode, stdout, stderr);
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return new ProcessResult(1, "", ex.Message + Environment.NewLine);
		}
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
		string folder = testCase.Kind switch
		{
			GoldenFileTestKind.CCompile => "golden-ccompile",
			GoldenFileTestKind.StdRun => "golden-stdrun",
			_ => "golden-cemit"
		};
		return Path.Combine(testCase.RepositoryRoot, "tmp", folder, caseName);
	}

	static string Normalize(string text)
	{
		text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
		if (text.Length > 0 && !text.EndsWith('\n'))
			text += "\n";
		return text;
	}

	readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}
