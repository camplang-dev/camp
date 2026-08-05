using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public static class GoldenFileTestRunner
{
	static readonly object StdRunCompilerLock = new();

	public static void Run(GoldenFileTestCase testCase)
	{
		ArgumentNullException.ThrowIfNull(testCase);
		string testName = testCase.Kind + "/" + Path.GetFileNameWithoutExtension(testCase.CasePath);
		Console.Error.WriteLine("[camp-test] golden begin " + testName);
		Console.Error.Flush();
		TestMetrics.RecordGoldenCase(testCase.Kind);
		using IDisposable timing = TestTiming.Measure("Golden " + testCase.Kind + "/" + Path.GetFileNameWithoutExtension(testCase.CasePath));
		if (testCase.Kind == GoldenFileTestKind.StdRun && OperatingSystem.IsWindows() && !MsvcAvailable())
			Assert.Skip("StdRun executable golden tests require MSVC tools on Windows.");
		if (testCase.Kind == GoldenFileTestKind.CCompile && !OperatingSystem.IsMacOS() && ExpectedCompileFailure(testCase))
			Assert.Skip("CCompile compile-failure diagnostics are host-clang dependent.");

		CompilerRequest request = CreateRequest(testCase);
		CompilerResult result = ExecuteCompiler(testCase, request);
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

	static bool ExpectedCompileFailure(GoldenFileTestCase testCase)
	{
		return File.Exists(testCase.ExpectedPath)
			&& File.ReadAllText(testCase.ExpectedPath).Contains("compile failed:", StringComparison.Ordinal);
	}

	static CompilerRequest CreateRequest(GoldenFileTestCase testCase)
	{
		if (testCase.Kind is GoldenFileTestKind.CEmit or GoldenFileTestKind.CCompile or GoldenFileTestKind.StdRun or GoldenFileTestKind.Metadata)
		{
			string outputDirectory = GetOutputDirectory(testCase);
			if (Directory.Exists(outputDirectory))
				Directory.Delete(outputDirectory, recursive: true);
		}

		CompilerRequest request = new()
		{
			RuntimeRoot = Path.Combine(testCase.RepositoryRoot, "bin"),
			TargetRoot = Path.Combine(testCase.RepositoryRoot, "targets"),
			PackageSourceRoot = Path.Combine(testCase.RepositoryRoot, "lib"),
			PackageArtifactRoot = GetPackageArtifactRoot(testCase),
			WorkingDirectory = testCase.RepositoryRoot,
			TargetName = SelectTargetName(testCase.Kind),
			NoStdLib = testCase.Kind is not (GoldenFileTestKind.Std or GoldenFileTestKind.StdRun),
			OutDir = testCase.Kind is GoldenFileTestKind.CEmit or GoldenFileTestKind.CCompile or GoldenFileTestKind.StdRun or GoldenFileTestKind.Metadata ? GetDirectOutputDirectory(testCase) : null,
			BuildKind = testCase.Kind == GoldenFileTestKind.StdRun ? NativeBuildKind.Exec : null,
			Inspect = testCase.Kind switch
			{
				GoldenFileTestKind.Ast => null,
				GoldenFileTestKind.Declarations => CompilerInspectMode.Declarations,
				GoldenFileTestKind.LoweringXml => CompilerInspectMode.Lowering,
				GoldenFileTestKind.Lowering => CompilerInspectMode.Lowering,
				GoldenFileTestKind.Diagnostics => CompilerInspectMode.Lowering,
				GoldenFileTestKind.Std => CompilerInspectMode.Lowering,
				GoldenFileTestKind.StdRun => null,
				GoldenFileTestKind.Api => null,
				GoldenFileTestKind.Metadata => null,
				GoldenFileTestKind.CEmit => null,
				GoldenFileTestKind.CCompile => null,
				_ => throw new ArgumentOutOfRangeException()
			},
			InspectApi = testCase.Kind == GoldenFileTestKind.Api,
			EmitMetadata = testCase.Kind == GoldenFileTestKind.Metadata ? MetadataVisibility.Export : null
		};
		ApplyCaseOptions(testCase, request);
		request.Files.Add(Path.GetRelativePath(testCase.RepositoryRoot, testCase.CasePath));
		return request;
	}

	static CompilerResult ExecuteCompiler(GoldenFileTestCase testCase, CompilerRequest request)
	{
		if (testCase.Kind is GoldenFileTestKind.Ast or GoldenFileTestKind.Declarations or GoldenFileTestKind.LoweringXml)
			return ExecuteXmlSnapshot(testCase, request);

		if (testCase.Kind != GoldenFileTestKind.StdRun)
			return CompilerDriver.Execute(request);

		lock (StdRunCompilerLock)
			return CompilerDriver.Execute(request);
	}

	static CompilerResult ExecuteXmlSnapshot(GoldenFileTestCase testCase, CompilerRequest request)
	{
		CompilerResult result = new();
		try
		{
			if (!TryCreateCompilation(testCase, request, out Compilation? compilation, out string? error))
				return Fail(result, error ?? "Could not create compilation.");
			if (compilation is null)
				return Fail(result, "Could not create compilation.");
			Compilation current = compilation;
			SourceFile sourceFile = current.Files[0];

			switch (testCase.Kind)
			{
				case GoldenFileTestKind.Ast:
					if (!CompilationPipeline.BuildAst(current))
						return Fail(result, "AST build failed.");
					if (sourceFile.BindableTree is null)
						return Fail(result, "AST build produced no bindable tree.");
					result.StdOut = FormatXml(CompilerXmlSerializer.SerializeBindableNode(sourceFile.BindableTree));
					break;
				case GoldenFileTestKind.Declarations:
					if (!CompilationPipeline.ExpandDeclarations(current))
						return Fail(result, "Declaration expansion failed.");
					if (current.DeclarationExpansion is null)
						return Fail(result, "Declaration expansion produced no module.");
					AnalysisResult declarationAnalysis = BindableNodeAnalyzer.AnalyzeExpanded(current.DeclarationExpansion, current.Target);
					if (declarationAnalysis.Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
						return Fail(result, "Declaration analysis failed.");
					current.SharedModule = declarationAnalysis.Module;
					result.StdOut = FormatXml(CompilerXmlSerializer.SerializeBindableNode(BuildOutputModule(current, sourceFile)));
					break;
				case GoldenFileTestKind.LoweringXml:
					if (!CompilationPipeline.Lower(current))
						return Fail(result, "Lowering failed.");
					result.StdOut = FormatXml(CompilerXmlSerializer.SerializeBindableNode(BuildOutputModule(current, sourceFile)));
					break;
			}
		}
		catch (Exception ex)
		{
			return Fail(result, ex.ToString());
		}
		return result;
	}

	static bool TryCreateCompilation(GoldenFileTestCase testCase, CompilerRequest request, out Compilation? compilation, out string? error)
	{
		compilation = null;
		error = null;
		if (!TargetCatalog.TryLoad(request.TargetRoot ?? Path.Combine(testCase.RepositoryRoot, "targets"), out TargetCatalog? catalog, out error))
			return false;
		if (!catalog!.TryGetTarget(request.TargetName, out TargetDefinition? target) || target is null)
		{
			error = $"Target '{request.TargetName}' could not be found.";
			return false;
		}
		try
		{
			target = target.WithVariantSelection(target.ResolveVariantSelection(request.Variants));
		}
		catch (InvalidDataException ex)
		{
			error = ex.Message;
			return false;
		}
		foreach (string define in request.Defines)
		{
			if (target.TargetOwnedDefines.Contains(define))
			{
				error = $"Define '{define}' is owned by target '{target.Name}'; select a target variant instead.";
				return false;
			}
		}

		compilation = new Compilation
		{
			Target = target,
			ProfileName = request.ProfileName,
			CommandMode = request.CommandMode,
			DeclarationParticipationMode = request.DeclarationParticipationMode,
			CoverageInstrumentationMode = request.CoverageInstrumentationMode
		};
		compilation.PreprocessorSymbols.Add("TRUE");
		compilation.PreprocessorSymbols.Add(request.ProfileName);
		foreach (string define in target.TargetOwnedDefines)
			compilation.TargetOwnedPreprocessorSymbols.Add(define);
		foreach (string define in target.Defines.Keys)
			compilation.PreprocessorSymbols.Add(define);
		foreach (string define in request.Defines)
			if (!string.IsNullOrWhiteSpace(define))
				compilation.PreprocessorSymbols.Add(define);
		if (request.DeclarationParticipationMode == DeclarationParticipationMode.TestModule)
			compilation.PreprocessorSymbols.Add("TEST_MODULE");

		foreach (string file in request.Files)
		{
			string fullPath = Path.GetFullPath(file, request.WorkingDirectory);
			compilation.Files.Add(new SourceFile
			{
				Path = file,
				FullPath = fullPath,
				Text = File.ReadAllText(fullPath),
				IsApiHeader = false,
				SharedLibraryImport = false
			});
		}
		return true;
	}

	static CompilerResult Fail(CompilerResult result, string message)
	{
		result.ExitCode = 1;
		result.StdErr = message + Environment.NewLine;
		return result;
	}

	static string FormatXml(XElement root)
	{
		XDocument document = new(new XDeclaration("1.0", "utf-8", null), root);
		XmlWriterSettings settings = new() { Indent = true, OmitXmlDeclaration = false };
		using StringWriter textWriter = new();
		using (XmlWriter writer = XmlWriter.Create(textWriter, settings))
			document.Save(writer);
		return textWriter.ToString();
	}

	static Module BuildOutputModule(Compilation compilation, SourceFile file)
	{
		if (compilation.SharedModule is null)
			return file.BindableTree!;
		Module output = new()
		{
			SourceSyntax = file.BindableTree?.SourceSyntax,
			ResolvedType = compilation.SharedModule.ResolvedType,
			Namespace = file.BindableTree?.Namespace
		};
		foreach (UsingDeclaration usingDeclaration in file.BindableTree?.Usings ?? [])
			output.Usings.Add(usingDeclaration);
		foreach (Definition definition in compilation.SharedModule.Definitions)
			if (DeclarationParticipation.Includes(definition, compilation.SharedModule)
				&& compilation.DefinitionOwners.TryGetValue(definition, out SourceFile? owner)
				&& ReferenceEquals(owner, file))
				output.Definitions.Add(definition);
		return output;
	}

	static string SelectTargetName(GoldenFileTestKind kind)
	{
		if (kind == GoldenFileTestKind.StdRun && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
			return CompilerDefaults.TargetName;
		return "clang-macos-x64";
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
				request.OutDir = GetDirectOutputDirectory(testCase);
			}
			else if (option.StartsWith("emit-metadata ", StringComparison.OrdinalIgnoreCase)
				&& Enum.TryParse(option["emit-metadata ".Length..], ignoreCase: true, out MetadataVisibility visibility))
				request.EmitMetadata = visibility;
			else if (option.StartsWith("target ", StringComparison.OrdinalIgnoreCase))
				request.TargetName = option["target ".Length..].Trim();
			else if (option.StartsWith("variant ", StringComparison.OrdinalIgnoreCase))
				request.Variants.AddRange(option["variant ".Length..].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
			else if (option.Equals("test-module", StringComparison.OrdinalIgnoreCase))
			{
				request.DeclarationParticipationMode = DeclarationParticipationMode.TestModule;
			}
			else if (option.Equals("cover-module", StringComparison.OrdinalIgnoreCase))
			{
				request.DeclarationParticipationMode = DeclarationParticipationMode.TestModule;
				request.CoverageInstrumentationMode = CoverageInstrumentationMode.ProductionSubject;
			}
		}
	}

	static string SelectOutput(GoldenFileTestCase testCase, CompilerResult result)
	{
		if (result.ExitCode != 0 && testCase.Kind is GoldenFileTestKind.Ast or GoldenFileTestKind.Declarations or GoldenFileTestKind.LoweringXml)
			return result.StdErr + result.StdOut;
		return testCase.Kind switch
		{
			GoldenFileTestKind.Diagnostics => result.StdErr,
			GoldenFileTestKind.CEmit => ReadGeneratedFiles(testCase),
			GoldenFileTestKind.CCompile => CompileGeneratedC(testCase, result),
			GoldenFileTestKind.StdRun => RunGeneratedExecutable(testCase, result),
			GoldenFileTestKind.Api => result.StdOut,
			GoldenFileTestKind.Metadata => ReadMetadataOutput(testCase, result),
			_ => result.StdOut
		};
	}

	static string ReadMetadataOutput(GoldenFileTestCase testCase, CompilerResult result)
	{
		if (result.ExitCode != 0)
			return result.StdErr + result.StdOut;
		string? metadataPath = result.GeneratedFiles
			.Where(static path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
			.OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
			.FirstOrDefault();
		return metadataPath is not null && File.Exists(metadataPath) ? File.ReadAllText(metadataPath) : "metadata: no output\n";
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
			.Where(static path => Path.GetExtension(path) is not ".c" and not ".h" and not ".o" and not ".a" and not ".camp" and not ".json")
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

		foreach (string sourceFile in sourceFiles)
		{
			string compiler = HostCCompiler();
			ProcessResult compile = RunProcess(compiler, HostCCompilerArguments(compiler, sourceFile), testCase.RepositoryRoot);
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
		const int timeoutMilliseconds = 30000;
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
			Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
			Task<string> stderrTask = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(timeoutMilliseconds))
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
				{
				}
				return new ProcessResult(124, "", $"Process timed out after {timeoutMilliseconds} ms: {executable}" + Environment.NewLine);
			}
			string stdout = stdoutTask.GetAwaiter().GetResult();
			string stderr = stderrTask.GetAwaiter().GetResult();
			return new ProcessResult(process.ExitCode, stdout, stderr);
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return new ProcessResult(1, "", ex.Message + Environment.NewLine);
		}
	}

	static string HostCCompiler()
	{
		if (ToolAvailable("clang"))
			return "clang";
		return "gcc";
	}

	static string[] HostCCompilerArguments(string compiler, string sourceFile)
	{
		List<string> arguments =
			[
				"-std=c11",
				"-Werror=incompatible-pointer-types",
				"-fsyntax-only",
				sourceFile
		];
		if (compiler == "clang")
		{
			arguments.Insert(2, "-Werror=typedef-redefinition");
			arguments.Insert(3, "-Werror=c23-extensions");
		}
		return [.. arguments];
	}

	static string ReadGeneratedFiles(GoldenFileTestCase testCase)
	{
		string buildDirectory = GetGeneratedBuildDirectory(testCase);
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

	static string GetOutputDirectory(GoldenFileTestCase testCase)
	{
		string caseName = Path.GetFileNameWithoutExtension(testCase.CasePath);
		string folder = testCase.Kind switch
		{
			GoldenFileTestKind.CCompile => "golden-ccompile",
			GoldenFileTestKind.StdRun => "golden-stdrun",
			GoldenFileTestKind.Metadata => "golden-metadata",
			_ => "golden-cemit"
		};
		return Path.Combine(testCase.RepositoryRoot, "tmp", folder, caseName);
	}

	static string GetGeneratedBuildDirectory(GoldenFileTestCase testCase)
	{
		return Path.Combine(GetOutputDirectory(testCase), "build");
	}

	static string GetDirectOutputDirectory(GoldenFileTestCase testCase)
	{
		return Path.Combine(GetOutputDirectory(testCase), ".");
	}

	static string GetPackageArtifactRoot(GoldenFileTestCase testCase)
	{
		if (testCase.Kind == GoldenFileTestKind.StdRun)
			return Path.Combine(testCase.RepositoryRoot, "tmp", "golden-stdrun-packages");
		return Path.Combine(testCase.RepositoryRoot, "tmp", "golden-packages");
	}

	static string Normalize(string text)
	{
		text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
		if (text.Length > 0 && !text.EndsWith('\n'))
			text += "\n";
		return text;
	}

	static bool MsvcAvailable()
	{
		return ToolAvailable("cl") && ToolAvailable("lib");
	}

	static bool ToolAvailable(string tool)
	{
		string[] extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			string trimmed = directory.Trim();
			if (File.Exists(Path.Combine(trimmed, tool)))
				return true;
			foreach (string extension in extensions)
			{
				if (File.Exists(Path.Combine(trimmed, tool + extension)))
					return true;
			}
		}
		return false;
	}

	readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}
