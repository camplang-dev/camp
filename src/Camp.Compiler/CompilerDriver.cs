using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Camp.Compiler;

public enum CompilerInspectMode
{
	None,
	Tokens,
	Declarations,
	Lowering,
	Metadata
}

public enum DependencyLinkKind
{
	Static,
	Shared,
	Api
}

public sealed class CompilerRequest
{
	public List<string> Files { get; } = [];
	public List<string> ApiFiles { get; } = [];
	public List<string> AnalysisSourceFiles { get; } = [];
	public List<string> Defines { get; } = [];
	public CompilerInspectMode? Inspect { get; set; }
	public bool InspectApi { get; set; }
	public string TargetName { get; set; } = CompilerDefaults.TargetName;
	public string ProfileName { get; set; } = "DEBUG";
	public List<string> Variants { get; } = [];
	public string EmitKind { get; set; } = "c99";
	public NativeBuildKind? BuildKind { get; set; }
	public bool InferBuildKind { get; set; }
	public NativeBuildKind? WithinPolicyBuildKind { get; set; }
	public bool InferWithinPolicyBuildKind { get; set; }
	public CompilerCommandMode CommandMode { get; set; } = CompilerCommandMode.Build;
	public DeclarationParticipationMode DeclarationParticipationMode { get; set; } = DeclarationParticipationMode.Production;
	public CoverageInstrumentationMode CoverageInstrumentationMode { get; set; } = CoverageInstrumentationMode.Disabled;
	public bool EmitDebugInfo { get; set; }
	public MetadataVisibility? EmitMetadata { get; set; }
	public string? OutDir { get; set; }
	public bool OutDirIsDirect { get; set; }
	public string? ProjectName { get; set; }
	public string? SubsystemName { get; set; }
	public bool NoStdLib { get; set; }
	public WithinAllocationPolicy? WithinAllocationPolicy { get; set; }
	public SourcefilePathMode SourcefilePathMode { get; set; } = SourcefilePathMode.Relative;
	public List<string> SourcefileRoots { get; } = [];
	public string? SourcefileDefaultRoot { get; set; }
	public bool Verbose { get; set; }
	public bool ColorOutput { get; set; }
	public bool ListTests { get; set; }
	public bool IgnoreLeaks { get; set; }
	public List<string> TestFilters { get; } = [];
	public string? TestOutputDir { get; set; }
	public string? TestResultFormat { get; set; }
	public string? CoverageOutputDir { get; set; }
	public string? CoverageFormat { get; set; }
	public List<string> CoverageSubjects { get; } = [];
	public List<string> CoverageMapInputs { get; } = [];
	public List<string> References { get; } = [];
	public List<string> SharedLibraryApiHeaders { get; } = [];
	public List<string> Frameworks { get; } = [];
	public List<string> UsePackages { get; } = [];
	public List<string> UseSourceRoots { get; } = [];
	public string RuntimeRoot { get; set; } = AppContext.BaseDirectory;
	public string? TargetRoot { get; set; }
	public string? PackageSourceRoot { get; set; }
	public string? PackageArtifactRoot { get; set; }
	public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();
	public bool TimingEnabled { get; set; }
	public string? TimingOutput { get; set; }
}

public sealed class CompilerResult
{
	public int ExitCode { get; set; }
	public string StdOut { get; set; } = "";
	public string StdErr { get; set; } = "";
	public List<string> GeneratedFiles { get; } = [];
}

public static class CompilerDriver
{
	public static CompilerResult Execute(CompilerRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		DriverRun run = new(request);
		return run.Execute();
	}

	sealed class DriverRun(CompilerRequest request)
	{
		readonly StringBuilder stdout = new();
		readonly StringBuilder stderr = new();
		readonly List<string> generatedFiles = [];
		readonly Dictionary<string, BuildFileWriteStatus> generatedFileStatuses = new(StringComparer.OrdinalIgnoreCase);
		readonly BuildTiming timing = BuildTiming.Create(
			request.TimingEnabled || !string.IsNullOrWhiteSpace(request.TimingOutput),
			request.CommandMode.ToString().ToLowerInvariant(),
			string.IsNullOrWhiteSpace(request.ProjectName) ? "project" : request.ProjectName!,
			request.TargetName,
			request.BuildKind?.ToString().ToLowerInvariant() ?? (request.InferBuildKind ? "infer" : "none"),
			request.ProfileName,
			typeof(CompilerDriver).Assembly.GetName().Version?.ToString() ?? "unknown");

		public CompilerResult Execute()
		{
			int exitCode = Run();
			CompleteTiming(exitCode);
			CompilerResult result = new()
			{
				ExitCode = exitCode,
				StdOut = Normalize(stdout.ToString()),
				StdErr = Normalize(stderr.ToString())
			};
			result.GeneratedFiles.AddRange(generatedFiles);
			return result;
		}

		void CompleteTiming(int exitCode)
		{
			timing.Complete(exitCode == 0 ? "success" : "failed");
			if (!timing.Enabled)
				return;
			stderr.Append(timing.FormatText());
			if (!string.IsNullOrWhiteSpace(request.TimingOutput))
			{
				string timingPath = Path.GetFullPath(request.TimingOutput!, request.WorkingDirectory);
				try
				{
					BuildFileIO.WriteTextIfChanged(timingPath, timing.FormatJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
				{
					stderr.Append(timingPath).Append(": ").Append(ex.Message).Append('\n');
				}
			}
		}

		int Run()
		{
			if (request.Files.Count == 0)
				return Error("At least one filename is required.");

			if (request.Files.Count > 1 && request.Files.Contains("-") || request.ApiFiles.Count > 0 && request.Files.Contains("-"))
				return Error("Standard input may only be used by itself and cannot be combined with API headers.");
			if (request.ApiFiles.Contains("-"))
				return Error("API headers must be read from files, not standard input.");
			if ((request.InferBuildKind || request.InferWithinPolicyBuildKind) && !TryInferRequestedBuildKinds())
				return 1;
			ApplySubsystem();
			if (request.InspectApi && request.Inspect is not null)
				return Error("--inspect-api cannot be combined with --inspect.");
			if (request.BuildKind is not null && request.Inspect is not null)
				return Error("--artifact cannot be combined with dump commands.");
			if (request.BuildKind is not null && request.InspectApi)
				return Error("--artifact cannot be combined with --inspect-api.");
			if (GetEffectiveMetadataVisibility() != MetadataVisibility.None && (request.Inspect is not null and not CompilerInspectMode.Metadata || request.InspectApi))
				return Error("--metadata cannot be combined with non-metadata dump commands or --inspect-api.");

			RuntimeContext? context;
			using (timing.Begin("runtime context", "setup"))
			{
				if (!TryCreateRuntimeContext(out RuntimeContext? createdContext))
					return 1;
				context = createdContext;
			}
			if (!ValidateFrameworks(context!.Target))
				return 1;

			bool requireNativeLibraries = request.BuildKind is not null || request.CommandMode is CompilerCommandMode.Test or CompilerCommandMode.Cover;
			List<string> packageApiHeaders = [];
			List<string> packageLibraries = [];
			if (!request.NoStdLib)
			{
				using IDisposable _ = timing.Begin("package std" + (requireNativeLibraries ? ":static" : ":api"), "package");
				if (!TryPreparePackage(context!, "std", requireNativeLibraries, out string? stdApiHeader, out string? stdLibrary))
					return 1;
				if (stdApiHeader is not null)
					packageApiHeaders.Add(stdApiHeader);
				if (stdLibrary is not null)
					packageLibraries.Add(stdLibrary);
			}
			foreach (string package in request.UsePackages)
			{
				using IDisposable _ = timing.Begin("package " + package, "package");
				if (!TryPrepareInstalledPackage(context!, package, requireNativeLibraries, out string? packageApiHeader, out string? packageLibrary, out bool sharedDependency))
					return 1;
				if (packageApiHeader is not null)
				{
					packageApiHeaders.Add(packageApiHeader);
					if (sharedDependency)
						request.SharedLibraryApiHeaders.Add(packageApiHeader);
				}
				if (packageLibrary is not null)
					packageLibraries.Add(packageLibrary);
			}

			List<string> allApiFiles = [.. packageApiHeaders, .. request.ApiFiles];
			if (TryUseCurrentTopLevelArtifact(context!, allApiFiles, packageLibraries))
				return 0;
			Compilation compilation;
			using (timing.Begin("load sources and APIs", "compiler-phase", new Dictionary<string, string>
			{
				["files"] = $"{request.Files.Count} source, {allApiFiles.Count} api"
			}))
			{
				if (!TryLoadCompilation(request.Files, allApiFiles, context!, out compilation))
					return 1;
			}

			if (request.InspectApi)
				return PrintApi(compilation);

			CompilerInspectMode inspect = request.Inspect ?? CompilerInspectMode.None;
			return inspect switch
			{
				CompilerInspectMode.None => request.CommandMode is CompilerCommandMode.Test or CompilerCommandMode.Cover
					? EmitTestDiscoveryOutput(compilation, packageLibraries)
					: EmitDefaultOutput(compilation, packageLibraries),
				CompilerInspectMode.Tokens => PrintTokens(compilation),
				CompilerInspectMode.Declarations => PrintDeclarations(compilation),
				CompilerInspectMode.Lowering => PrintLowering(compilation),
				CompilerInspectMode.Metadata => PrintMetadata(compilation),
				_ => 1
			};
		}

		int EmitTestDiscoveryOutput(Compilation compilation, IReadOnlyList<string> packageLibraries)
		{
			if (!LowerAndReport(compilation))
				return 1;

			CampTestManifestMode mode = request.SharedLibraryApiHeaders.Count > 0
				? CampTestManifestMode.External
				: CampTestManifestMode.InModule;
			CampTestDiscoveryResult discovery = CampTestDiscovery.Discover(compilation, mode);
			if (!PrintAnalysisDiagnostics(compilation, discovery.Diagnostics))
				return 1;

			string outputDirectory = ResolveArtifactOutputDirectory(compilation);
			string buildDirectory = Path.Combine(outputDirectory, "build");
			string projectName = string.IsNullOrWhiteSpace(request.ProjectName) ? CCodeEmitter.GetProjectName(compilation.Files) : request.ProjectName!;
			string testOutputDirectory = ResolveTestOutputDirectory(outputDirectory);
			string coverageOutputDirectory = ResolveCoverageOutputDirectory(outputDirectory);
			if (!TryEmitTestManifestArtifact(discovery.Manifest, testOutputDirectory, projectName))
				return 1;

			IReadOnlyList<CampTestManifestEntry> selectedTests = CampTestFilter.Apply(discovery.Manifest.Tests, request.TestFilters);
			if (request.ListTests)
			{
				foreach (CampTestManifestEntry test in selectedTests)
					OutLine(test.Id);
				return 0;
			}

			if (!PrepareTestHarnessEntryPoint(compilation))
				return 1;

			CampCoverageMapBuilder? coverageMapBuilder = request.CoverageInstrumentationMode == CoverageInstrumentationMode.ProductionSubject
				? new CampCoverageMapBuilder(compilation)
				: null;
			CEmissionResult result = CCodeEmitter.Emit(compilation, new CEmissionOptions
			{
				OutputDirectory = buildDirectory,
				ProjectName = projectName,
				EmitKind = request.EmitKind,
				BuildKind = NativeBuildKind.Exec,
				EmitDebugInfo = request.EmitDebugInfo,
				ExposePrivateFunctionsForTestHarness = true,
				CoverageMapBuilder = coverageMapBuilder
			});
			foreach (string diagnostic in result.Diagnostics)
				ErrorLine(diagnostic);
			if (!result.Success)
				return 1;

			foreach (string generated in result.GeneratedFiles)
			{
				AddGeneratedFile(generated, result.FileStatuses.GetValueOrDefault(generated, BuildFileWriteStatus.Changed));
			}

			if (request.EmitDebugInfo && !TryEmitDebugArtifact(compilation, outputDirectory, projectName, result.DebugInfo))
				return 1;

			List<string> coverageMapPaths = [.. request.CoverageMapInputs];
			List<string> coverageRuntimeSources = [];
			if (coverageMapBuilder is not null)
			{
				if (!TryEmitCoverageBuildArtifacts(coverageMapBuilder, coverageOutputDirectory, buildDirectory, projectName, out string? coverageMapPath, out string? coverageRuntimeSource))
					return 1;
				coverageMapPaths.Add(coverageMapPath!);
				coverageRuntimeSources.Add(coverageRuntimeSource!);
			}

			CampCoverageMap? coverageMap = coverageMapBuilder?.ToMap();
			if (!TryEmitTestHarnessSource(buildDirectory, projectName, selectedTests, coverageMap, out string? harnessSource))
				return 1;

			NativeBuildOptions buildOptions = new()
			{
				Target = compilation.Target!,
				ProfileName = compilation.ProfileName,
				BuildDirectory = buildDirectory,
				OutputDirectory = outputDirectory,
				ProjectName = projectName,
				Kind = NativeBuildKind.Exec,
				SourceFiles = [.. result.GeneratedSourceFiles, .. coverageRuntimeSources, harnessSource!],
				SourceFileStatuses = BuildSourceStatuses(result),
				Libraries = packageLibraries.Concat(request.References.Select(reference => ResolveNativeReference(reference, compilation.Target!))).ToList(),
				Frameworks = request.Frameworks
			};
			NativeBuildResult build = NativeBuildDriver.Build(buildOptions);
			foreach (string diagnostic in build.Diagnostics)
				ErrorLine(diagnostic);
			if (!build.Success)
				return 1;
			foreach (string generated in build.GeneratedFiles)
			{
				AddGeneratedFile(generated, BuildFileWriteStatus.Changed);
			}
			if (!TryCopySharedRuntimeReferences(compilation.Target!, outputDirectory, packageLibraries.Concat(request.References.Select(reference => ResolveNativeReference(reference, compilation.Target!)))))
				return 1;
			if (!TryGetTestResultOutputFormat(out bool writeText, out bool writeJson))
				return 1;
			Dictionary<string, string> coverageCountPaths = request.CommandMode == CompilerCommandMode.Cover
				? CreateCoverageCountPaths(coverageMapPaths, buildDirectory)
				: [];
			CampTestResults testResults = RunTestHarness(NativeBuildDriver.GetArtifactPath(buildOptions), buildDirectory, selectedTests, coverageCountPaths);
			if (writeJson && !TryEmitTestResultsArtifact(testResults, testOutputDirectory, projectName))
				return 1;
			bool coverageSucceeded = true;
			CampCoverageResults? coverageResults = null;
			if (request.CommandMode == CompilerCommandMode.Cover)
			{
				if (!TryGetCoverageOutputFormat(out bool writeCoverageJson, out bool writeCoverageLcov))
					return 1;
				coverageSucceeded = TryEmitCoverageResultsArtifacts(coverageMapPaths, coverageCountPaths, coverageOutputDirectory, projectName, writeCoverageJson, writeCoverageLcov, out coverageResults);
			}
			if (writeText)
				stdout.Append(CampTestResultsTextFormatter.Format(testResults, request.ColorOutput));
			if (request.CommandMode == CompilerCommandMode.Cover && coverageResults is not null)
				stdout.Append(CampCoverageResultsTextFormatter.Format(coverageResults));
			return TestResultsSucceeded(testResults) && coverageSucceeded ? 0 : 1;
		}

		void ApplySubsystem()
		{
			if (request.BuildKind == NativeBuildKind.Exec && request.SubsystemName?.Equals("windows", StringComparison.OrdinalIgnoreCase) == true)
				request.BuildKind = NativeBuildKind.WinExe;
		}

		bool TryInferRequestedBuildKinds()
		{
			if (!TryInferBuildKindFromSources(out NativeBuildKind buildKind))
				return false;
			if (request.InferBuildKind)
				request.BuildKind = buildKind;
			if (request.InferWithinPolicyBuildKind)
				request.WithinPolicyBuildKind = buildKind;
			return true;
		}

		bool TryInferBuildKindFromSources(out NativeBuildKind buildKind)
		{
			foreach (string filename in request.Files)
			{
				if (filename == "-")
				{
					buildKind = NativeBuildKind.Static;
					return true;
				}
				try
				{
					string fullPath = Path.GetFullPath(filename, request.WorkingDirectory);
					string text = File.ReadAllText(fullPath);
					if (CompilerRequestPolicy.HasPublicOrExportedMain(text))
					{
						buildKind = NativeBuildKind.Exec;
						return true;
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
				{
					ErrorLine($"{filename}: {ex.Message}");
					buildKind = default;
					return false;
				}
			}

			buildKind = NativeBuildKind.Static;
			return true;
		}

		bool TryUseCurrentTopLevelArtifact(RuntimeContext context, IReadOnlyList<string> allApiFiles, IReadOnlyList<string> packageLibraries)
		{
			if (request.BuildKind is null
				|| request.CommandMode is CompilerCommandMode.Test or CompilerCommandMode.Cover
				|| request.EmitDebugInfo)
				return false;
			string projectName = GetRequestProjectName();
			string outputDirectory = ResolveArtifactOutputDirectory(context.Target, request.BuildKind, context.ProfileName);
			string buildDirectory = Path.Combine(outputDirectory, "build");
			NativeBuildOptions buildOptions = new()
			{
				Target = context.Target,
				ProfileName = context.ProfileName,
				BuildDirectory = buildDirectory,
				OutputDirectory = outputDirectory,
				ProjectName = projectName,
				Kind = request.BuildKind.Value,
				SourceFiles = []
			};
			string artifact = NativeBuildDriver.GetArtifactPath(buildOptions);
			List<string> outputs = [artifact];
			if (request.BuildKind is NativeBuildKind.Static or NativeBuildKind.Shared)
			{
				outputs.Add(Path.Combine(outputDirectory, projectName + "_api.camp"));
				outputs.Add(Path.Combine(outputDirectory, projectName + "_api.h"));
				outputs.Add(Path.Combine(outputDirectory, projectName + "_api.json"));
			}
			List<string> inputs = [];
			inputs.AddRange(ResolveInputPaths(request.Files));
			inputs.AddRange(ResolveInputPaths(allApiFiles));
			inputs.AddRange(packageLibraries);
			inputs.AddRange(ResolveNativeReferenceInputs(request.References, context.Target));
			if (File.Exists(context.Target.Path))
				inputs.Add(context.Target.Path);
			if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
				inputs.Add(Environment.ProcessPath);
			if (!OutputsAreCurrent(outputs, inputs, out string? freshnessReason))
			{
				if (request.Verbose)
					OutLine("top-level artifact: rebuilding because " + freshnessReason);
				return false;
			}
			using IDisposable _ = timing.Begin("top-level artifact freshness", "freshness", "current");
			foreach (string output in outputs)
				AddGeneratedFile(output, BuildFileWriteStatus.Unchanged);
			return true;
		}

		string GetRequestProjectName()
		{
			if (!string.IsNullOrWhiteSpace(request.ProjectName))
				return request.ProjectName!;
			string? firstSource = request.Files.FirstOrDefault(static file => file != "-");
			return string.IsNullOrWhiteSpace(firstSource) ? "camp" : Path.GetFileNameWithoutExtension(firstSource);
		}

		string ResolveArtifactOutputDirectory(TargetDefinition target, NativeBuildKind? buildKind, string profileName)
		{
			string outputPrefix = string.IsNullOrWhiteSpace(request.OutDir)
				? GetDefaultArtifactDirectoryFromRequest()
				: request.OutDir!;
			string outputRoot = Path.GetFullPath(outputPrefix, request.WorkingDirectory);
			return request.OutDirIsDirect || IsDirectOutputDirectory(request.OutDir)
				? outputRoot
				: Path.Combine(outputRoot, BuildArtifactLayout.GetArtifactDirectoryName(target, buildKind, profileName));
		}

		string GetDefaultArtifactDirectoryFromRequest()
		{
			string? firstSource = request.Files.FirstOrDefault(static file => file != "-");
			if (string.IsNullOrWhiteSpace(firstSource))
				return Path.Combine(request.WorkingDirectory, "bin");
			string full = Path.GetFullPath(firstSource, request.WorkingDirectory);
			string? directory = Path.GetDirectoryName(full);
			return Path.Combine(string.IsNullOrWhiteSpace(directory) ? request.WorkingDirectory : directory, "bin");
		}

		IEnumerable<string> ResolveInputPaths(IEnumerable<string> inputs)
		{
			foreach (string input in inputs)
			{
				if (string.IsNullOrWhiteSpace(input) || input == "-")
					continue;
				string full = Path.GetFullPath(input, request.WorkingDirectory);
				if (File.Exists(full) || Directory.Exists(full))
					yield return full;
			}
		}

		int Error(string message)
		{
			ErrorLine(message);
			return 1;
		}

		bool TryCreateRuntimeContext(out RuntimeContext? context)
		{
			context = null;
			string normalizedProfile = request.ProfileName.ToUpperInvariant();
			if (normalizedProfile is not "DEBUG" and not "RELEASE")
			{
				ErrorLine($"Profile '{request.ProfileName}' is not valid. Expected DEBUG or RELEASE.");
				return false;
			}

			string targetsDirectory = GetTargetRoot();
			if (!TargetCatalog.TryLoadCached(targetsDirectory, out TargetCatalog? catalog, out string? error))
			{
				ErrorLine(error ?? $"Target directory '{targetsDirectory}' could not be loaded.");
				if (string.IsNullOrWhiteSpace(request.TargetRoot))
					ErrorLine($"Set {CampRuntimeLayout.HomeEnvironmentVariable} to the Camp installation root if the compiler is installed outside the source repository.");
				return false;
			}

			if (!catalog!.TryGetTarget(request.TargetName, out TargetDefinition? target))
			{
				ErrorLine($"Target '{request.TargetName}' could not be found in '{targetsDirectory}'.");
				return false;
			}

			TargetVariantSelection selection;
			try
			{
				selection = target!.ResolveVariantSelection(request.Variants);
			}
			catch (InvalidDataException ex)
			{
				ErrorLine(ex.Message);
				return false;
			}
			target = target.WithVariantSelection(selection);

			foreach (string define in request.Defines)
			{
				if (target.TargetOwnedDefines.Contains(define))
				{
					ErrorLine($"Define '{define}' is owned by target '{target.Name}'; select a target variant instead.");
					return false;
				}
			}

			context = new RuntimeContext(GetPackageSourceRoot(), GetPackageArtifactRoot(), target, normalizedProfile, [.. request.Defines]);
			return true;
		}

		string GetTargetRoot()
		{
			if (!string.IsNullOrWhiteSpace(request.TargetRoot))
				return Path.GetFullPath(request.TargetRoot);
			return CampRuntimeLayout.Resolve(request.WorkingDirectory, request.RuntimeRoot).TargetDirectory;
		}

		string GetPackageSourceRoot()
		{
			if (!string.IsNullOrWhiteSpace(request.PackageSourceRoot))
				return Path.GetFullPath(request.PackageSourceRoot);
			return CampRuntimeLayout.Resolve(request.WorkingDirectory, request.RuntimeRoot).LibraryDirectory;
		}

		string GetPackageArtifactRoot()
		{
			if (!string.IsNullOrWhiteSpace(request.PackageArtifactRoot))
				return Path.GetFullPath(request.PackageArtifactRoot);
			return CampRuntimeLayout.Resolve(request.WorkingDirectory, request.RuntimeRoot).CompilerLibraryCacheDirectory;
		}

		bool TryLoadCompilation(List<string> filenames, List<string> apiFilenames, RuntimeContext context, out Compilation compilation)
		{
			return TryLoadCompilation(request, filenames, apiFilenames, context, out compilation);
		}

		bool TryLoadCompilation(CompilerRequest loadRequest, IReadOnlyList<string> filenames, IReadOnlyList<string> apiFilenames, RuntimeContext context, out Compilation compilation)
		{
			compilation = new Compilation
			{
				Target = context.Target,
				ProfileName = context.ProfileName,
				CommandMode = loadRequest.CommandMode,
				DeclarationParticipationMode = loadRequest.DeclarationParticipationMode,
				CoverageInstrumentationMode = loadRequest.CoverageInstrumentationMode,
				DefaultWithinAllocationPolicy = CompilerRequestPolicy.GetEffectiveWithinAllocationPolicy(loadRequest),
				SourcefilePathMode = loadRequest.SourcefilePathMode,
				SourcefileDefaultRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(loadRequest.SourcefileDefaultRoot) ? loadRequest.WorkingDirectory : loadRequest.SourcefileDefaultRoot)
			};
			compilation.SourcefileRoots.AddRange(loadRequest.SourcefileRoots.Select(root => Path.GetFullPath(root, loadRequest.WorkingDirectory)));
			AddPreprocessorSymbols(compilation, loadRequest, context);
			foreach (string filename in filenames)
			{
				if (!TryReadInput(loadRequest, filename, out string text, out string displayPath, out string? fullPath))
					return false;
				if (!TryReadWithinAllocationPolicy(displayPath, text, out WithinAllocationPolicy? policy))
					return false;
				compilation.Files.Add(new SourceFile { Path = displayPath, FullPath = fullPath, Text = text, WithinAllocationPolicyOverride = policy });
			}
			foreach (string filename in apiFilenames)
			{
				if (!TryReadInput(loadRequest, filename, out string text, out string displayPath, out string? fullPath))
					return false;
				if (!TryReadWithinAllocationPolicy(displayPath, text, out WithinAllocationPolicy? policy))
					return false;
				bool isGeneratedApiHeader = IsGeneratedApiHeaderPath(fullPath ?? filename);
				compilation.Files.Add(new SourceFile { Path = displayPath, FullPath = fullPath, Text = text, IsApiHeader = true, IsGeneratedApiHeader = isGeneratedApiHeader, SharedLibraryImport = IsSharedLibraryApiHeader(loadRequest, filename), WithinAllocationPolicyOverride = policy });
			}
			return true;
		}

		static bool IsGeneratedApiHeaderPath(string path)
		{
			string normalized = path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
			return normalized.EndsWith("_api.camp", StringComparison.OrdinalIgnoreCase)
				&& (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
					|| normalized.Contains("/cache/", StringComparison.OrdinalIgnoreCase));
		}

		bool IsSharedLibraryApiHeader(CompilerRequest loadRequest, string filename)
		{
			string fullPath = Path.GetFullPath(filename, loadRequest.WorkingDirectory);
			return loadRequest.SharedLibraryApiHeaders.Any(header => string.Equals(Path.GetFullPath(header, loadRequest.WorkingDirectory), fullPath, StringComparison.OrdinalIgnoreCase));
		}

		bool TryReadWithinAllocationPolicy(string displayPath, string text, out WithinAllocationPolicy? policy)
		{
			policy = null;
			bool beforeCode = true;
			bool success = true;
			using StringReader reader = new(text);
			for (int lineNumber = 1; reader.ReadLine() is string line; lineNumber++)
			{
				string trimmed = line.TrimStart();
				if (trimmed.StartsWith("#within", StringComparison.Ordinal))
				{
					if (!beforeCode)
					{
						ErrorLine($"{displayPath}({lineNumber},1): error: #within directives must appear in the file prelude before any non-comment token.");
						success = false;
						continue;
					}
					List<string> parts = CampBuildPragmaReader.Split(trimmed["#within".Length..]);
					if (parts.Count != 1 || parts[0] is not ("explicit" or "implicit"))
					{
						ErrorLine($"{displayPath}({lineNumber},1): error: #within expects explicit or implicit.");
						success = false;
						continue;
					}
					policy = parts[0] == "explicit" ? WithinAllocationPolicy.Explicit : WithinAllocationPolicy.Implicit;
					continue;
				}
				if (CampBuildPragmaReader.IsPreludeTrivia(trimmed) || trimmed.StartsWith("#build", StringComparison.Ordinal))
					continue;
				beforeCode = false;
			}
			return success;
		}

		void AddPreprocessorSymbols(Compilation compilation, CompilerRequest loadRequest, RuntimeContext context)
		{
			compilation.PreprocessorSymbols.Add("TRUE");
			compilation.PreprocessorSymbols.Add(context.ProfileName);
			foreach (string symbol in context.Target.TargetOwnedDefines)
				compilation.TargetOwnedPreprocessorSymbols.Add(symbol);
			foreach (string symbol in context.Target.Defines.Keys)
				compilation.PreprocessorSymbols.Add(symbol);
			foreach (string symbol in context.CommandLineDefines)
				if (!string.IsNullOrWhiteSpace(symbol))
					compilation.PreprocessorSymbols.Add(symbol);
			if (loadRequest.DeclarationParticipationMode == DeclarationParticipationMode.TestModule)
				compilation.PreprocessorSymbols.Add("TEST_MODULE");
		}

		bool TryReadInput(CompilerRequest loadRequest, string filename, out string text, out string displayPath, out string? fullPath)
		{
			try
			{
				if (filename == "-")
				{
					text = Console.In.ReadToEnd();
					displayPath = "-";
					fullPath = null;
					return true;
				}

				fullPath = Path.GetFullPath(filename, loadRequest.WorkingDirectory);
				text = File.ReadAllText(fullPath);
				displayPath = GetDisplayPath(loadRequest, fullPath);
				return true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				text = "";
				displayPath = filename;
				fullPath = null;
				ErrorLine($"{filename}: {ex.Message}");
				return false;
			}
		}

		static string GetDisplayPath(CompilerRequest loadRequest, string fullPath)
		{
			string root = Path.GetFullPath(loadRequest.WorkingDirectory);
			string relative = Path.GetRelativePath(root, fullPath);
			string display = relative.StartsWith("..", StringComparison.Ordinal) ? fullPath : relative;
			return display.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
		}

		bool TryPreparePackage(RuntimeContext context, string packageName, bool requireNativeLibrary, out string? apiHeaderPath, out string? libraryPath)
		{
			apiHeaderPath = null;
			libraryPath = null;
			string sourceDirectory = Path.Combine(context.PackageSourceRoot, packageName, "src");
			if (!Directory.Exists(sourceDirectory))
			{
				if (string.Equals(packageName, "std", StringComparison.OrdinalIgnoreCase))
					ErrorLine($"Camp runtime could not find the standard library at '{sourceDirectory}'. Set {CampRuntimeLayout.HomeEnvironmentVariable} to the Camp installation root.");
				else
					ErrorLine($"Package '{packageName}' source directory '{sourceDirectory}' could not be found.");
				return false;
			}

			string[] sourceFiles = Directory.GetFiles(sourceDirectory, "*.camp", SearchOption.AllDirectories)
				.OrderBy(static x => x, StringComparer.Ordinal)
				.ToArray();
			if (sourceFiles.Length == 0)
			{
				ErrorLine($"Package '{packageName}' source directory '{sourceDirectory}' does not contain any .camp files.");
				return false;
			}
			string[] nativeSourceFiles = Directory.GetFiles(sourceDirectory, "*.c", SearchOption.AllDirectories)
				.OrderBy(static x => x, StringComparer.Ordinal)
				.ToArray();
			string[] cacheSourceFiles = sourceFiles
				.Concat(nativeSourceFiles)
				.Concat(Directory.GetFiles(sourceDirectory, "*.h", SearchOption.AllDirectories))
				.Concat(GetCompilerCacheInputs(context))
				.OrderBy(static x => x, StringComparer.Ordinal)
				.ToArray();

			NativeBuildKind? packageBuildKind = requireNativeLibrary ? NativeBuildKind.Static : null;
			string packageBinDirectory = GetPackageArtifactDirectory(context.PackageArtifactRoot, packageName, version: null, context, packageBuildKind);
			string apiPath = Path.Combine(packageBinDirectory, packageName + "_api.camp");
			string cApiPath = Path.Combine(packageBinDirectory, packageName + "_api.h");
			string metadataPath = Path.Combine(packageBinDirectory, packageName + "_api.json");
			string staticLibraryPath = NativeBuildDriver.GetArtifactPath(new NativeBuildOptions
			{
				Target = context.Target,
				ProfileName = context.ProfileName,
				BuildDirectory = Path.Combine(packageBinDirectory, "build"),
				OutputDirectory = packageBinDirectory,
				ProjectName = packageName,
				Kind = NativeBuildKind.Static,
				SourceFiles = []
			});

			bool canUseCache = context.CommandLineDefines.Count == 0;
			bool apiCurrent = canUseCache && IsOutputCacheCurrent(apiPath, cacheSourceFiles) && (!requireNativeLibrary || IsOutputCacheCurrent(cApiPath, cacheSourceFiles) && IsOutputCacheCurrent(metadataPath, cacheSourceFiles));
			bool libraryCurrent = !requireNativeLibrary || canUseCache && IsOutputCacheCurrent(staticLibraryPath, cacheSourceFiles);
			if (apiCurrent && libraryCurrent)
			{
				apiHeaderPath = apiPath;
				libraryPath = requireNativeLibrary ? staticLibraryPath : null;
				return true;
			}

			if (!TryBuildPackage(packageName, sourceFiles, nativeSourceFiles, cacheSourceFiles, apiPath, requireNativeLibrary ? cApiPath : null, requireNativeLibrary ? metadataPath : null, requireNativeLibrary ? staticLibraryPath : null, requireNativeLibrary ? NativeBuildKind.Static : null, context, CampApiSurfaceKind.Public, refreshCacheOutputs: canUseCache))
				return false;

			apiHeaderPath = apiPath;
			libraryPath = requireNativeLibrary ? staticLibraryPath : null;
			return true;
		}

		bool TryPrepareInstalledPackage(RuntimeContext context, string packageSpec, bool requireNativeLibrary, out string? apiHeaderPath, out string? libraryPath, out bool sharedDependency)
		{
			apiHeaderPath = null;
			libraryPath = null;
			sharedDependency = false;
			(string packageName, string? requestedVersion, DependencyLinkKind? requestedLinkKind) = ParsePackageSpec(packageSpec);
			if (!TryFindInstalledPackage(packageName, requestedVersion, out string? sourceDirectory, out string? artifactRoot, out string? resolvedVersion))
			{
				ErrorLine(FormatPackageNotFoundDiagnostic(packageSpec, packageName, requestedVersion));
				return false;
			}

			string[] sourceFiles = Directory.GetFiles(sourceDirectory!, "*.camp", SearchOption.AllDirectories)
				.OrderBy(static x => x, StringComparer.Ordinal)
				.ToArray();
			string[] nativeSourceFiles = Directory.GetFiles(sourceDirectory!, "*.c", SearchOption.AllDirectories)
				.OrderBy(static x => x, StringComparer.Ordinal)
				.ToArray();
			string[] cacheSourceFiles = sourceFiles
				.Concat(nativeSourceFiles)
				.Concat(Directory.GetFiles(sourceDirectory!, "*.h", SearchOption.AllDirectories))
				.Concat(GetCompilerCacheInputs(context))
				.OrderBy(static x => x, StringComparer.Ordinal)
				.ToArray();

			DependencyLinkKind effectiveLinkKind = requestedLinkKind.GetValueOrDefault(DependencyLinkKind.Shared);
			bool requireNativeApiArtifacts = requireNativeLibrary;
			bool requirePackageLibrary = requireNativeLibrary && effectiveLinkKind is not DependencyLinkKind.Api;
			NativeBuildKind? packageBuildKind = effectiveLinkKind switch
			{
				DependencyLinkKind.Shared => NativeBuildKind.Shared,
				DependencyLinkKind.Static => NativeBuildKind.Static,
				DependencyLinkKind.Api => null,
				_ => throw new ArgumentOutOfRangeException(nameof(effectiveLinkKind), effectiveLinkKind, null)
			};
			sharedDependency = requirePackageLibrary && packageBuildKind == NativeBuildKind.Shared;
			string packageBinDirectory = effectiveLinkKind == DependencyLinkKind.Api
				? GetPackageArtifactDirectory(artifactRoot!, packageName, resolvedVersion!, context, effectiveLinkKind)
				: GetPackageArtifactDirectory(artifactRoot!, packageName, resolvedVersion!, context, packageBuildKind);
			string apiPath = Path.Combine(packageBinDirectory, packageName + "_api.camp");
			string cApiPath = Path.Combine(packageBinDirectory, packageName + "_api.h");
			string metadataPath = Path.Combine(packageBinDirectory, packageName + "_api.json");
			NativeBuildOptions nativeBuildOptions = new()
			{
				Target = context.Target,
				ProfileName = context.ProfileName,
				BuildDirectory = Path.Combine(packageBinDirectory, "build"),
				OutputDirectory = packageBinDirectory,
				ProjectName = packageName,
				Kind = packageBuildKind ?? NativeBuildKind.Static,
				SourceFiles = []
			};
			string? nativeLibraryPath = requirePackageLibrary ? NativeBuildDriver.GetLinkArtifactPath(nativeBuildOptions) : null;

			bool canUseCache = context.CommandLineDefines.Count == 0;
			bool apiCurrent = canUseCache && IsOutputCacheCurrent(apiPath, cacheSourceFiles) && (!requireNativeApiArtifacts || IsOutputCacheCurrent(cApiPath, cacheSourceFiles) && IsOutputCacheCurrent(metadataPath, cacheSourceFiles));
			bool libraryCurrent = !requirePackageLibrary || nativeLibraryPath is not null && canUseCache && IsOutputCacheCurrent(nativeLibraryPath, cacheSourceFiles);
			if (apiCurrent && libraryCurrent)
			{
				apiHeaderPath = apiPath;
				libraryPath = nativeLibraryPath;
				return true;
			}

			CampApiSurfaceKind apiSurface = effectiveLinkKind == DependencyLinkKind.Shared ? CampApiSurfaceKind.Export : CampApiSurfaceKind.Public;
			if (!TryBuildPackage(packageName, sourceFiles, nativeSourceFiles, cacheSourceFiles, apiPath, requireNativeApiArtifacts ? cApiPath : null, requireNativeApiArtifacts ? metadataPath : null, nativeLibraryPath, packageBuildKind, context, apiSurface, refreshCacheOutputs: canUseCache))
				return false;

			apiHeaderPath = apiPath;
			libraryPath = nativeLibraryPath;
			return true;
		}

		bool TryFindInstalledPackage(string packageName, string? requestedVersion, out string? sourceDirectory, out string? artifactRoot, out string? resolvedVersion)
		{
			sourceDirectory = null;
			artifactRoot = null;
			resolvedVersion = null;
			if (TryFindLiveSourcePackage(packageName, requestedVersion, out sourceDirectory, out artifactRoot, out resolvedVersion))
				return true;
			foreach ((string installRoot, string outputRoot) in GetInstalledPackageRoots())
			{
				string packageDirectory = Path.Combine(installRoot, packageName);
				if (!Directory.Exists(packageDirectory))
					continue;
				string? version = requestedVersion;
				if (version is null)
					version = Directory.GetDirectories(packageDirectory)
						.Select(Path.GetFileName)
						.Where(static value => !string.IsNullOrWhiteSpace(value))
						.OrderByDescending(static value => PackageVersion.Parse(value!), PackageVersion.Comparer)
						.FirstOrDefault();
				if (version is null)
					continue;
				string candidate = Path.Combine(packageDirectory, version, "src");
				if (!Directory.Exists(candidate))
					continue;
				sourceDirectory = candidate;
				artifactRoot = outputRoot;
				resolvedVersion = version;
				return true;
			}
			return false;
		}

		bool TryFindLiveSourcePackage(string packageName, string? requestedVersion, out string? sourceDirectory, out string? artifactRoot, out string? resolvedVersion)
		{
			sourceDirectory = null;
			artifactRoot = null;
			resolvedVersion = null;
			foreach (string root in request.UseSourceRoots)
			{
				string sourceRoot = Path.GetFullPath(root, request.WorkingDirectory);
				if (requestedVersion is null)
				{
					string unversioned = Path.Combine(sourceRoot, packageName, "src");
					if (Directory.Exists(unversioned))
					{
						sourceDirectory = unversioned;
						artifactRoot = Path.Combine(request.WorkingDirectory, "cache", "pkg");
						resolvedVersion = "live";
						return true;
					}
				}

				string packageDirectory = Path.Combine(sourceRoot, packageName);
				if (!Directory.Exists(packageDirectory))
					continue;
				string? version = requestedVersion;
				if (version is null)
					version = Directory.GetDirectories(packageDirectory)
						.Select(Path.GetFileName)
						.Where(static value => !string.IsNullOrWhiteSpace(value))
						.OrderByDescending(static value => PackageVersion.Parse(value!), PackageVersion.Comparer)
						.FirstOrDefault();
				if (version is null)
					continue;
				string candidate = Path.Combine(packageDirectory, version, "src");
				if (!Directory.Exists(candidate))
					continue;
				sourceDirectory = candidate;
				artifactRoot = Path.Combine(request.WorkingDirectory, "cache", "pkg");
				resolvedVersion = version;
				return true;
			}
			return false;
		}

		string FormatPackageNotFoundDiagnostic(string packageSpec, string packageName, string? requestedVersion)
		{
			StringBuilder builder = new();
			builder.Append("Package '").Append(packageSpec).Append("' could not be found.");
			if (request.UseSourceRoots.Count > 0)
			{
				builder.AppendLine();
				builder.AppendLine("Searched package source roots:");
				foreach (string root in request.UseSourceRoots)
				{
					string sourceRoot = Path.GetFullPath(root, request.WorkingDirectory);
					builder.Append("  ").Append(sourceRoot);
					if (requestedVersion is null)
						builder.Append(" (looked for ").Append(Path.Combine(sourceRoot, packageName, "src")).Append(')');
					else
						builder.Append(" (looked for ").Append(Path.Combine(sourceRoot, packageName, requestedVersion, "src")).Append(')');
					builder.AppendLine();
				}
			}
			builder.AppendLine();
			builder.AppendLine("Searched installed package roots:");
			foreach ((string installRoot, _) in GetInstalledPackageRoots())
				builder.Append("  ").Append(Path.Combine(installRoot, packageName)).AppendLine();
			builder.Append("Run 'campc restore' or 'campc pkg install ").Append(packageSpec).Append("' if this is an installed package.");
			return builder.ToString();
		}

		IEnumerable<(string InstallRoot, string ArtifactRoot)> GetInstalledPackageRoots()
		{
			string globalRoot = CampRuntimeLayout.Resolve(request.WorkingDirectory, request.RuntimeRoot).PackageCacheDirectory;
			string localRoot = Path.GetFullPath(Path.Combine(request.WorkingDirectory, "cache", "pkg"));
			yield return (globalRoot, globalRoot);
			yield return (localRoot, localRoot);
		}

		static string GetPackageArtifactDirectory(string artifactRoot, string packageName, string? version, RuntimeContext context, NativeBuildKind? buildKind)
		{
			string artifactDirectory = BuildArtifactLayout.GetArtifactDirectoryName(context.Target, buildKind, context.ProfileName);
			return version is null
				? Path.Combine(artifactRoot, packageName, "bin", artifactDirectory)
				: Path.Combine(artifactRoot, packageName, version, "bin", artifactDirectory);
		}

		static string GetPackageArtifactDirectory(string artifactRoot, string packageName, string? version, RuntimeContext context, DependencyLinkKind linkKind)
		{
			string artifactDirectory = BuildArtifactLayout.GetArtifactDirectoryName(context.Target, linkKind, context.ProfileName);
			return version is null
				? Path.Combine(artifactRoot, packageName, "bin", artifactDirectory)
				: Path.Combine(artifactRoot, packageName, version, "bin", artifactDirectory);
		}

		static (string Name, string? Version, DependencyLinkKind? LinkKind) ParsePackageSpec(string value)
		{
			DependencyLinkKind? linkKind = null;
			int colon = value.LastIndexOf(':');
			if (colon >= 0)
			{
				string suffix = value[(colon + 1)..];
				if (suffix.Equals("static", StringComparison.OrdinalIgnoreCase) || suffix.Equals("shared", StringComparison.OrdinalIgnoreCase) || suffix.Equals("api", StringComparison.OrdinalIgnoreCase))
				{
					linkKind = suffix.ToLowerInvariant() switch
					{
						"shared" => DependencyLinkKind.Shared,
						"static" => DependencyLinkKind.Static,
						"api" => DependencyLinkKind.Api,
						_ => linkKind
					};
					value = value[..colon];
				}
			}
			string[] parts = value.Split('@', 2);
			return (parts[0], parts.Length == 2 && parts[1].Length > 0 ? parts[1] : null, linkKind);
		}

		readonly record struct PackageVersion(int Major, int Minor, int Patch, string? Suffix) : IComparable<PackageVersion>
		{
			public static IComparer<PackageVersion> Comparer { get; } = Comparer<PackageVersion>.Create(static (left, right) => left.CompareTo(right));

			public static PackageVersion Parse(string value)
			{
				string[] suffixParts = value.Split('-', 2);
				string[] parts = suffixParts[0].Split('.');
				return new PackageVersion(ParsePart(parts, 0), ParsePart(parts, 1), ParsePart(parts, 2), suffixParts.Length == 2 ? suffixParts[1] : null);
			}

			public int CompareTo(PackageVersion other)
			{
				int major = Major.CompareTo(other.Major);
				if (major != 0)
					return major;
				int minor = Minor.CompareTo(other.Minor);
				if (minor != 0)
					return minor;
				int patch = Patch.CompareTo(other.Patch);
				if (patch != 0)
					return patch;
				if (Suffix is null && other.Suffix is not null)
					return 1;
				if (Suffix is not null && other.Suffix is null)
					return -1;
				return string.Compare(Suffix, other.Suffix, StringComparison.Ordinal);
			}

			static int ParsePart(string[] parts, int index)
			{
				return index < parts.Length && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;
			}
		}

		static bool IsOutputCacheCurrent(string outputPath, IReadOnlyList<string> sourceFiles)
		{
			if (!File.Exists(outputPath))
				return false;
			DateTime outputTime = File.GetLastWriteTimeUtc(outputPath);
			foreach (string sourceFile in sourceFiles)
				if (outputTime < File.GetLastWriteTimeUtc(sourceFile))
					return false;
			return true;
		}

		bool TryRefreshPackageCacheOutputs(IEnumerable<string?> outputPaths, IReadOnlyList<string> sourceFiles)
		{
			DateTime newestSourceTime = DateTime.MinValue;
			foreach (string sourceFile in sourceFiles)
			{
				DateTime sourceTime = File.GetLastWriteTimeUtc(sourceFile);
				if (sourceTime > newestSourceTime)
					newestSourceTime = sourceTime;
			}

			DateTime refreshTime = DateTime.UtcNow;
			foreach (string? outputPath in outputPaths)
			{
				if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
					continue;
				try
				{
					if (File.GetLastWriteTimeUtc(outputPath) < newestSourceTime)
						File.SetLastWriteTimeUtc(outputPath, refreshTime);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
				{
					ErrorLine($"{outputPath}: {ex.Message}");
					return false;
				}
			}
			return true;
		}

		static IEnumerable<string> GetCompilerCacheInputs(RuntimeContext context)
		{
			if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
				yield return Environment.ProcessPath;
			string? targetDirectory = Path.GetDirectoryName(context.Target.Path);
			if (!string.IsNullOrWhiteSpace(targetDirectory) && Directory.Exists(targetDirectory))
			{
				foreach (string targetFile in Directory.GetFiles(targetDirectory, "*.ini").OrderBy(static path => path, StringComparer.Ordinal))
					yield return targetFile;
			}
		}

		bool TryBuildPackage(string packageName, IReadOnlyList<string> sourceFiles, IReadOnlyList<string> nativeSourceFiles, IReadOnlyList<string> cacheSourceFiles, string apiPath, string? cApiPath, string? metadataPath, string? nativeLibraryPath, NativeBuildKind? nativeBuildKind, RuntimeContext context, CampApiSurfaceKind apiSurface, bool refreshCacheOutputs)
		{
			if (!TryGetPackageNoStdLib(sourceFiles, out bool sourceNoStdLib))
				return false;
			bool packageNoStdLib = string.Equals(packageName, "std", StringComparison.OrdinalIgnoreCase) || sourceNoStdLib;
			List<string> packageIncludes = [];
			if (!packageNoStdLib)
			{
				if (!TryPreparePackage(context, "std", requireNativeLibrary: false, out string? stdApiHeader, out _))
					return false;
				if (stdApiHeader is not null)
					packageIncludes.Add(stdApiHeader);
			}

			CompilerRequest packageRequest = new()
			{
				RuntimeRoot = request.RuntimeRoot,
				TargetRoot = request.TargetRoot,
				PackageSourceRoot = request.PackageSourceRoot,
				PackageArtifactRoot = request.PackageArtifactRoot,
				WorkingDirectory = request.WorkingDirectory,
				TargetName = context.Target.Name,
				ProfileName = context.ProfileName,
				BuildKind = nativeLibraryPath is null ? null : nativeBuildKind,
				WithinAllocationPolicy = request.WithinAllocationPolicy,
				NoStdLib = packageNoStdLib
			};
			packageRequest.Files.AddRange(sourceFiles);
			packageRequest.UseSourceRoots.AddRange(request.UseSourceRoots);
			Compilation packageCompilation;
			using (timing.Begin("package load " + packageName, "compiler-phase"))
			{
				if (!TryLoadCompilation(packageRequest, packageRequest.Files, packageIncludes, context, out packageCompilation))
					return false;
			}

			if (!ExpandDeclarationsAndReport(packageCompilation))
				return false;

			AnalysisResult analysis = BindableNodeAnalyzer.AnalyzeExpanded(packageCompilation.DeclarationExpansion!, packageCompilation.Target);
			if (!PrintAnalysisDiagnostics(packageCompilation, analysis.Diagnostics))
				return false;
			packageCompilation.SharedModule = analysis.Module;

			using (timing.Begin("package Camp API emission " + packageName, "emission"))
			{
				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(apiPath)!);
					using StringWriter writer = new(CultureInfo.InvariantCulture);
					BindableNodeCodeSerializer.Serialize(BuildApiOutputModule(packageCompilation, apiSurface), writer, new BindableNodeCodeSerializerOptions { ApiHeader = true, ApiSurface = apiSurface, ApiDefinitionsAlreadyFiltered = true, ApiReferenceDefinitions = packageCompilation.SharedModule.Definitions });
					AddGeneratedFile(apiPath, BuildFileIO.WriteTextIfChanged(apiPath, writer.ToString(), Encoding.UTF8));
					if (metadataPath is not null && !TryEmitMetadataArtifact(packageCompilation, Path.GetDirectoryName(metadataPath)!, MetadataVisibility.Export, packageName))
						return false;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
				{
					ErrorLine($"{apiPath}: {ex.Message}");
					return false;
				}
			}

			string packageArtifactDirectory = Path.GetDirectoryName(cApiPath ?? nativeLibraryPath ?? apiPath)!;
			Compilation? packageNativeCompilation = null;
			if (cApiPath is not null || nativeLibraryPath is not null)
			{
				if (!TryLoadCompilation(packageRequest, packageRequest.Files, packageIncludes, context, out packageNativeCompilation))
					return false;
				if (!LowerAndReport(packageNativeCompilation))
					return false;

				if (cApiPath is not null)
				{
					CEmissionResult apiHeader;
					using (timing.Begin("package C API emission " + packageName, "emission"))
					{
						apiHeader = CCodeEmitter.EmitProjectApiHeader(packageNativeCompilation, new CEmissionOptions
						{
							OutputDirectory = packageArtifactDirectory,
							ProjectName = packageName,
							EmitKind = request.EmitKind,
							BuildKind = nativeBuildKind,
							ApiSurface = apiSurface
						}, packageArtifactDirectory);
					}
					foreach (string diagnostic in apiHeader.Diagnostics)
						ErrorLine(diagnostic);
					if (!apiHeader.Success)
						return false;
					foreach (string generated in apiHeader.GeneratedFiles)
						AddGeneratedFile(generated, apiHeader.FileStatuses.GetValueOrDefault(generated, BuildFileWriteStatus.Changed));
				}
			}

			if (nativeLibraryPath is null)
				return !refreshCacheOutputs || TryRefreshPackageCacheOutputs([apiPath, cApiPath, metadataPath], cacheSourceFiles);

			string packageBuildDirectory = Path.Combine(packageArtifactDirectory, "build");
			CEmissionResult emission;
			using (timing.Begin("package C emission " + packageName, "emission"))
			{
				emission = CCodeEmitter.Emit(packageNativeCompilation!, new CEmissionOptions
				{
					OutputDirectory = packageBuildDirectory,
					ProjectName = packageName,
					EmitKind = "c99",
					BuildKind = nativeBuildKind
				});
			}
			foreach (string diagnostic in emission.Diagnostics)
				ErrorLine(diagnostic);
			if (!emission.Success)
				return false;

			NativeBuildResult build;
			using (timing.Begin("package native build " + packageName, "native"))
			{
				build = NativeBuildDriver.Build(new NativeBuildOptions
				{
					Target = context.Target,
					ProfileName = context.ProfileName,
					BuildDirectory = packageBuildDirectory,
					OutputDirectory = Path.GetDirectoryName(nativeLibraryPath)!,
					ProjectName = packageName,
					Kind = nativeBuildKind ?? NativeBuildKind.Static,
					SourceFiles = emission.GeneratedSourceFiles.Concat(nativeSourceFiles).ToList(),
					SourceFileStatuses = BuildSourceStatuses(emission)
				});
			}
			foreach (string diagnostic in build.Diagnostics)
				ErrorLine(diagnostic);
			if (!build.Success)
				return false;
			return !refreshCacheOutputs || TryRefreshPackageCacheOutputs([apiPath, cApiPath, metadataPath, nativeLibraryPath], cacheSourceFiles);
		}

		bool TryRefreshGeneratedOutputs(IEnumerable<string?> outputPaths)
		{
			DateTime refreshTime = DateTime.UtcNow;
			foreach (string? outputPath in outputPaths)
			{
				if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
					continue;
				try
				{
					File.SetLastWriteTimeUtc(outputPath, refreshTime);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
				{
					ErrorLine($"{outputPath}: {ex.Message}");
					return false;
				}
			}
			return true;
		}

		int EmitDefaultOutput(Compilation compilation, IReadOnlyList<string> packageLibraries)
		{
			if (!LowerAndReport(compilation))
				return 1;

			FunctionDefinition? execEntryPoint = null;
			if (request.BuildKind is NativeBuildKind.Exec or NativeBuildKind.WinExe && !TryPrepareExecEntryPoint(compilation, out execEntryPoint))
				return 1;

			string outputDirectory = ResolveArtifactOutputDirectory(compilation);
			string buildDirectory = Path.Combine(outputDirectory, "build");
			string projectName = string.IsNullOrWhiteSpace(request.ProjectName) ? CCodeEmitter.GetProjectName(compilation.Files) : request.ProjectName!;
			CampCoverageMapBuilder? coverageMapBuilder = request.CoverageInstrumentationMode == CoverageInstrumentationMode.ProductionSubject
				? new CampCoverageMapBuilder(compilation)
				: null;
			CEmissionResult result;
			using (timing.Begin("C source/header emission", "emission"))
			{
				result = CCodeEmitter.Emit(compilation, new CEmissionOptions
				{
					OutputDirectory = buildDirectory,
					ProjectName = projectName,
					EmitKind = request.EmitKind,
					BuildKind = request.BuildKind,
					EmitDebugInfo = request.EmitDebugInfo,
					EmitExecMainWrapper = request.BuildKind is NativeBuildKind.Exec or NativeBuildKind.WinExe,
					ExecEntryPoint = execEntryPoint,
					CoverageMapBuilder = coverageMapBuilder
				});
			}
			foreach (string diagnostic in result.Diagnostics)
				ErrorLine(diagnostic);
			if (!result.Success)
				return 1;

			foreach (string generated in result.GeneratedFiles)
			{
				AddGeneratedFile(generated, result.FileStatuses.GetValueOrDefault(generated, BuildFileWriteStatus.Changed));
			}

			List<string> coverageRuntimeSources = [];
			if (coverageMapBuilder is not null)
			{
				if (!TryEmitCoverageBuildArtifacts(coverageMapBuilder, ResolveCoverageOutputDirectory(outputDirectory), buildDirectory, projectName, out _, out string? coverageRuntimeSource))
					return 1;
				coverageRuntimeSources.Add(coverageRuntimeSource!);
			}

			if (request.BuildKind is NativeBuildKind.Static or NativeBuildKind.Shared && !TryEmitLibraryApiArtifacts(compilation, outputDirectory))
				return 1;

			if (request.EmitDebugInfo && !TryEmitDebugArtifact(compilation, outputDirectory, projectName, result.DebugInfo))
				return 1;

			MetadataVisibility metadataVisibility = GetEffectiveMetadataVisibility();
			if (metadataVisibility != MetadataVisibility.None && !TryEmitMetadataArtifact(compilation, outputDirectory, metadataVisibility, string.IsNullOrWhiteSpace(request.ProjectName) ? GetMetadataProjectName(compilation.Files) : request.ProjectName!))
				return 1;

			if (request.BuildKind is null)
				return 0;

			NativeBuildOptions buildOptions = new()
			{
				Target = compilation.Target!,
				ProfileName = compilation.ProfileName,
				BuildDirectory = buildDirectory,
				OutputDirectory = outputDirectory,
				ProjectName = projectName,
				Kind = request.BuildKind.Value,
				SourceFiles = [.. result.GeneratedSourceFiles, .. coverageRuntimeSources],
				SourceFileStatuses = BuildSourceStatuses(result),
				Libraries = packageLibraries.Concat(request.References.Select(reference => ResolveNativeReference(reference, compilation.Target!))).ToList(),
				Frameworks = request.Frameworks
			};
			NativeBuildResult build;
			using (timing.Begin("native build", "native"))
			{
				build = NativeBuildDriver.Build(buildOptions);
			}
			foreach (string diagnostic in build.Diagnostics)
				ErrorLine(diagnostic);
			if (!build.Success)
				return 1;
			foreach (string generated in build.GeneratedFiles)
			{
				AddGeneratedFile(generated, BuildFileWriteStatus.Changed);
			}
			if (!TryCopySharedRuntimeReferences(compilation.Target!, outputDirectory, packageLibraries.Concat(request.References.Select(reference => ResolveNativeReference(reference, compilation.Target!)))))
				return 1;
			if (!TryRefreshGeneratedOutputs(GetTopLevelFreshnessOutputs(buildOptions)))
				return 1;
			return 0;
		}

		static IEnumerable<string?> GetTopLevelFreshnessOutputs(NativeBuildOptions buildOptions)
		{
			yield return NativeBuildDriver.GetArtifactPath(buildOptions);
			if (buildOptions.Kind == NativeBuildKind.Shared)
				yield return NativeBuildDriver.GetSharedImportLibraryPath(buildOptions);
			if (buildOptions.Kind is NativeBuildKind.Static or NativeBuildKind.Shared)
			{
				yield return Path.Combine(buildOptions.OutputDirectory, buildOptions.ProjectName + "_api.camp");
				yield return Path.Combine(buildOptions.OutputDirectory, buildOptions.ProjectName + "_api.h");
				yield return Path.Combine(buildOptions.OutputDirectory, buildOptions.ProjectName + "_api.json");
			}
		}

		string ResolveArtifactOutputDirectory(Compilation compilation)
		{
			string outputPrefix = string.IsNullOrWhiteSpace(request.OutDir)
				? CCodeEmitter.GetDefaultArtifactDirectory(compilation.Files)
				: request.OutDir!;
			string outputRoot = Path.GetFullPath(outputPrefix, request.WorkingDirectory);
			if (request.OutDirIsDirect || IsDirectOutputDirectory(request.OutDir))
				return outputRoot;
			return Path.Combine(outputRoot, BuildArtifactLayout.GetArtifactDirectoryName(compilation.Target!, request.BuildKind, compilation.ProfileName));
		}

		static bool IsDirectOutputDirectory(string? value)
		{
			if (string.IsNullOrWhiteSpace(value))
				return false;
			string trimmed = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return trimmed == "." || trimmed.EndsWith(Path.DirectorySeparatorChar + ".", StringComparison.Ordinal) || trimmed.EndsWith(Path.AltDirectorySeparatorChar + ".", StringComparison.Ordinal);
		}

		string ResolveNativeReference(string reference, TargetDefinition target)
		{
			if (Path.IsPathRooted(reference) || reference.Contains(Path.DirectorySeparatorChar) || reference.Contains(Path.AltDirectorySeparatorChar))
				return Path.GetFullPath(reference, request.WorkingDirectory);

			string localPath = Path.GetFullPath(reference, request.WorkingDirectory);
			if (File.Exists(localPath))
				return localPath;

			if (Path.HasExtension(reference))
				return reference;

			string staticExtension = target.Capabilities.GetArtifactValue("static_ext", ".a");
			if (staticExtension.Equals(".lib", StringComparison.OrdinalIgnoreCase))
				return reference + ".lib";
			return "-l" + reference;
		}

		IEnumerable<string> ResolveNativeReferenceInputs(IEnumerable<string> references, TargetDefinition target)
		{
			foreach (string reference in references)
			{
				string resolved = ResolveNativeReference(reference, target);
				if (ShouldTrackNativeReferenceInput(reference, resolved))
					yield return resolved;
			}
		}

		bool ShouldTrackNativeReferenceInput(string reference, string resolved)
		{
			if (Path.IsPathRooted(resolved) || resolved.Contains(Path.DirectorySeparatorChar) || resolved.Contains(Path.AltDirectorySeparatorChar))
				return true;
			if (Path.IsPathRooted(reference) || reference.Contains(Path.DirectorySeparatorChar) || reference.Contains(Path.AltDirectorySeparatorChar))
				return true;

			string localPath = Path.GetFullPath(resolved, request.WorkingDirectory);
			return File.Exists(localPath) || Directory.Exists(localPath);
		}

		bool TryCopySharedRuntimeReferences(TargetDefinition target, string outputDirectory, IEnumerable<string> references)
		{
			string sharedExtension = target.GetArtifactValue("shared_ext", ".so");
			string sharedImportExtension = target.GetArtifactValue("shared_import_ext");
			foreach (string reference in references)
			{
				if (!Path.IsPathRooted(reference) || !File.Exists(reference))
					continue;
				string runtimeReference = reference;
				if (!Path.GetExtension(runtimeReference).Equals(sharedExtension, StringComparison.OrdinalIgnoreCase))
				{
					if (string.IsNullOrWhiteSpace(sharedImportExtension) || !Path.GetExtension(reference).Equals(sharedImportExtension, StringComparison.OrdinalIgnoreCase))
						continue;
					string siblingRuntime = Path.ChangeExtension(reference, sharedExtension);
					if (!File.Exists(siblingRuntime))
						continue;
					runtimeReference = siblingRuntime;
				}
				if (!Path.GetExtension(runtimeReference).Equals(sharedExtension, StringComparison.OrdinalIgnoreCase))
					continue;
				string destination = Path.Combine(outputDirectory, Path.GetFileName(runtimeReference));
				if (string.Equals(Path.GetFullPath(runtimeReference), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
					continue;
				try
				{
					BuildFileWriteStatus status = BuildFileIO.CopyIfChanged(runtimeReference, destination);
					AddGeneratedFile(destination, status);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
				{
					ErrorLine($"{destination}: {ex.Message}");
					return false;
				}
			}
			return true;
		}

		bool TryGetPackageNoStdLib(IReadOnlyList<string> sourceFiles, out bool noStdLib)
		{
			noStdLib = false;
			List<string> errors = [];
			foreach (string sourceFile in sourceFiles)
			{
				foreach (CampBuildPragmaLine pragma in CampBuildPragmaReader.Read(sourceFile, request.WorkingDirectory, errors))
				{
					if (pragma.Tokens.Contains("--nostdlib", StringComparer.Ordinal))
						noStdLib = true;
				}
			}
			foreach (string error in errors)
				ErrorLine(error);
			return errors.Count == 0;
		}

		bool ValidateFrameworks(TargetDefinition target)
		{
			NativeBuildKind? nativeBuildKind = request.CommandMode is CompilerCommandMode.Test or CompilerCommandMode.Cover
				? NativeBuildKind.Exec
				: request.BuildKind;
			if (request.Frameworks.Count == 0 || nativeBuildKind is null)
				return true;
			if (nativeBuildKind == NativeBuildKind.Static)
			{
				ErrorLine("--framework cannot be used with --artifact static.");
				return false;
			}
			if (!target.Capabilities.SupportsFrameworkLinking)
			{
				ErrorLine($"Target '{target.Name}' does not support framework linking.");
				return false;
			}
			foreach (string framework in request.Frameworks)
			{
				if (!NativeBuildDriver.IsValidFrameworkName(framework))
				{
					ErrorLine($"Framework name '{framework}' is not valid.");
					return false;
				}
			}
			return true;
		}

		bool TryPrepareExecEntryPoint(Compilation compilation, out FunctionDefinition? entryPoint)
		{
			entryPoint = null;
			List<FunctionDefinition> candidates = [];
			foreach (Definition definition in compilation.SharedModule is Module module ? DeclarationParticipation.ActiveTopLevelDefinitions(module) : [])
				if (definition is FunctionDefinition { Name: "main", Export: not null } function)
					candidates.Add(function);

			if (candidates.Count != 1)
			{
				ErrorLine(candidates.Count == 0
					? "Building an executable requires exactly one exported function named 'main'."
					: "Building an executable requires exactly one exported function named 'main', but multiple were found.");
				return false;
			}

			FunctionDefinition main = candidates[0];
			bool returnsInt = main.ReturnType is PrimitiveTypeReference { Type: PrimitiveType.Int } || main.ResolvedType == "int";
			bool returnsVoid = main.ReturnType is PrimitiveTypeReference { Type: PrimitiveType.Void } || main.ResolvedType == "void";
			if (!returnsInt && !returnsVoid)
			{
				ErrorLine("Executable entry point 'main' must return int or void.");
				return false;
			}
			if (main.Parameters.Count is not (0 or 2))
			{
				ErrorLine("Executable entry point 'main' must have no parameters or one string[] parameter.");
				return false;
			}
			main.Symbol = "campmain";
			entryPoint = main;
			return true;
		}

		bool PrepareTestHarnessEntryPoint(Compilation compilation)
		{
			List<FunctionDefinition> candidates = [];
			foreach (Definition definition in compilation.SharedModule is Module module ? DeclarationParticipation.ActiveTopLevelDefinitions(module) : [])
				if (definition is FunctionDefinition { Name: "main", Export: not null } function)
					candidates.Add(function);

			if (candidates.Count > 1)
			{
				ErrorLine("Test harness entry point replacement found multiple exported functions named 'main'.");
				return false;
			}
			if (candidates.Count == 1)
				candidates[0].Symbol = "campmain";
			return true;
		}

		MetadataVisibility GetEffectiveMetadataVisibility()
		{
			if (request.EmitMetadata is MetadataVisibility requested)
				return requested;
			return request.BuildKind is NativeBuildKind.Static or NativeBuildKind.Shared
				? MetadataVisibility.Export
				: MetadataVisibility.None;
		}

		bool TryEmitMetadataArtifact(Compilation compilation, string outputDirectory, MetadataVisibility visibility, string projectName)
		{
			string metadataPath = Path.Combine(outputDirectory, projectName + "_api.json");
			try
			{
				Directory.CreateDirectory(outputDirectory);
				AddGeneratedFile(metadataPath, BuildFileIO.WriteTextIfChanged(metadataPath, MetadataJsonSerializer.Serialize(compilation, visibility), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				ErrorLine($"{metadataPath}: {ex.Message}");
				return false;
			}

			return true;
		}

		bool TryEmitTestManifestArtifact(CampTestManifest manifest, string outputDirectory, string projectName)
		{
			string manifestPath = Path.Combine(outputDirectory, projectName + ".camp-test-manifest.json");
			try
			{
				Directory.CreateDirectory(outputDirectory);
				AddGeneratedFile(manifestPath, BuildFileIO.WriteTextIfChanged(manifestPath, CampTestManifestJsonSerializer.Serialize(manifest), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				ErrorLine($"{manifestPath}: {ex.Message}");
				return false;
			}

			return true;
		}

		bool TryEmitTestHarnessSource(string outputDirectory, string projectName, IReadOnlyList<CampTestManifestEntry> tests, CampCoverageMap? coverageMap, out string? harnessSource)
		{
			harnessSource = Path.Combine(outputDirectory, projectName + "_test_harness.c");
			try
			{
				Directory.CreateDirectory(outputDirectory);
				AddGeneratedFile(harnessSource, BuildFileIO.WriteTextIfChanged(harnessSource, CampTestHarnessGenerator.Generate(projectName, tests, request.IgnoreLeaks, coverageMap), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				ErrorLine($"{harnessSource}: {ex.Message}");
				harnessSource = null;
				return false;
			}

			return true;
		}

		bool TryEmitTestResultsArtifact(CampTestResults results, string outputDirectory, string projectName)
		{
			string resultsPath = Path.Combine(outputDirectory, projectName + ".camp-test-results.json");
			try
			{
				Directory.CreateDirectory(outputDirectory);
				AddGeneratedFile(resultsPath, BuildFileIO.WriteTextIfChanged(resultsPath, CampTestResultsJsonSerializer.Serialize(results), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				ErrorLine($"{resultsPath}: {ex.Message}");
				return false;
			}

			return true;
		}

		bool TryEmitCoverageBuildArtifacts(CampCoverageMapBuilder builder, string coverageOutputDirectory, string buildDirectory, string projectName, out string? coverageMapPath, out string? coverageRuntimeSource)
		{
			coverageMapPath = Path.Combine(coverageOutputDirectory, projectName + ".camp-coverage-map.csv");
			coverageRuntimeSource = Path.Combine(buildDirectory, projectName + "_coverage_runtime.c");
			try
			{
				Directory.CreateDirectory(coverageOutputDirectory);
				Directory.CreateDirectory(buildDirectory);
				CampCoverageMap map = builder.ToMap();
				BuildFileWriteStatus mapStatus = BuildFileIO.WriteTextIfChanged(coverageMapPath, CampCoverageMapCsvSerializer.Serialize(map), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				BuildFileWriteStatus runtimeStatus = BuildFileIO.WriteTextIfChanged(coverageRuntimeSource, CampCoverageRuntimeSourceGenerator.Generate(projectName, map), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				AddGeneratedFile(coverageMapPath, mapStatus);
				AddGeneratedFile(coverageRuntimeSource, runtimeStatus);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				ErrorLine($"{coverageMapPath}: {ex.Message}");
				coverageMapPath = null;
				coverageRuntimeSource = null;
				return false;
			}

			return true;
		}

		bool TryEmitCoverageResultsArtifacts(IReadOnlyList<string> coverageMapPaths, IReadOnlyDictionary<string, string> coverageCountPaths, string outputDirectory, string projectName, bool writeJson, bool writeLcov, out CampCoverageResults? results)
		{
			results = null;
			if (coverageMapPaths.Count == 0)
			{
				ErrorLine("cover did not produce a coverage map.");
				return false;
			}

			List<CampCoverageMap> maps = [];
			List<IReadOnlyDictionary<int, ulong>> countSets = [];
			foreach (string mapPath in coverageMapPaths)
			{
				if (!TryReadCoverageMap(mapPath, out CampCoverageMap? map))
					return false;
				maps.Add(map!);
				string envName = CampCoverageRuntimeSourceGenerator.EnvironmentVariableName(ProjectNameFromCoverageMapPath(mapPath));
				if (map!.Counters.Count == 0)
				{
					countSets.Add(new Dictionary<int, ulong>());
					continue;
				}
				if (!coverageCountPaths.TryGetValue(envName, out string? countPath))
				{
					ErrorLine($"coverage count path for '{envName}' was not configured.");
					return false;
				}
				if (!CampCoverageCounterFile.TryRead(countPath, out Dictionary<int, ulong> counts, out List<string> diagnostics))
				{
					foreach (string diagnostic in diagnostics)
						ErrorLine(diagnostic);
					return false;
				}
				countSets.Add(counts);
			}

			results = CampCoverageResultsFactory.Create(maps, countSets);
			try
			{
				Directory.CreateDirectory(outputDirectory);
				if (writeJson)
				{
					string jsonPath = Path.Combine(outputDirectory, projectName + ".camp-coverage-results.json");
					AddGeneratedFile(jsonPath, BuildFileIO.WriteTextIfChanged(jsonPath, CampCoverageResultsJsonSerializer.Serialize(results), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
				}
				if (writeLcov)
				{
					string lcovPath = Path.Combine(outputDirectory, "lcov.info");
					AddGeneratedFile(lcovPath, BuildFileIO.WriteTextIfChanged(lcovPath, CampCoverageLcovSerializer.Serialize(maps, countSets), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				ErrorLine($"{outputDirectory}: {ex.Message}");
				return false;
			}

			return true;
		}

		bool TryReadCoverageMap(string mapPath, out CampCoverageMap? map)
		{
			map = null;
			try
			{
				if (!CampCoverageMapCsvSerializer.TryParse(File.ReadAllText(mapPath), out CampCoverageMap parsed, out List<string> diagnostics))
				{
					foreach (string diagnostic in diagnostics)
						ErrorLine($"{mapPath}: {diagnostic}");
					return false;
				}
				map = parsed;
				return true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				ErrorLine($"{mapPath}: {ex.Message}");
				return false;
			}
		}

		string ResolveTestOutputDirectory(string outputDirectory)
		{
			if (string.IsNullOrWhiteSpace(request.TestOutputDir))
				return outputDirectory;
			return Path.GetFullPath(request.TestOutputDir!, request.WorkingDirectory);
		}

		string ResolveCoverageOutputDirectory(string outputDirectory)
		{
			if (string.IsNullOrWhiteSpace(request.CoverageOutputDir))
				return outputDirectory;
			return Path.GetFullPath(request.CoverageOutputDir!, request.WorkingDirectory);
		}

		bool TryGetTestResultOutputFormat(out bool writeText, out bool writeJson)
		{
			writeText = false;
			writeJson = false;
			string format = string.IsNullOrWhiteSpace(request.TestResultFormat) ? "text,json" : request.TestResultFormat!;
			foreach (string part in format.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				if (part.Equals("text", StringComparison.OrdinalIgnoreCase))
					writeText = true;
				else if (part.Equals("json", StringComparison.OrdinalIgnoreCase))
					writeJson = true;
				else
				{
					ErrorLine("--test-result-format expects text, json, or text,json.");
					return false;
				}
			}
			if (!writeText && !writeJson)
			{
				ErrorLine("--test-result-format expects text, json, or text,json.");
				return false;
			}
			return true;
		}

		bool TryGetCoverageOutputFormat(out bool writeJson, out bool writeLcov)
		{
			writeJson = false;
			writeLcov = false;
			string format = string.IsNullOrWhiteSpace(request.CoverageFormat) ? "json" : request.CoverageFormat!;
			foreach (string part in format.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				if (part.Equals("json", StringComparison.OrdinalIgnoreCase))
					writeJson = true;
				else if (part.Equals("lcov", StringComparison.OrdinalIgnoreCase))
					writeLcov = true;
				else
				{
					ErrorLine("--coverage-format expects json, lcov, or json,lcov.");
					return false;
				}
			}
			if (!writeJson && !writeLcov)
			{
				ErrorLine("--coverage-format expects json, lcov, or json,lcov.");
				return false;
			}
			if (writeJson && writeLcov && !string.Equals(format, "json,lcov", StringComparison.OrdinalIgnoreCase))
			{
				ErrorLine("--coverage-format expects json, lcov, or json,lcov.");
				return false;
			}
			return true;
		}

		static Dictionary<string, string> CreateCoverageCountPaths(IReadOnlyList<string> coverageMapPaths, string buildDirectory)
		{
			Dictionary<string, string> result = new(StringComparer.Ordinal);
			foreach (string mapPath in coverageMapPaths)
			{
				string projectName = ProjectNameFromCoverageMapPath(mapPath);
				string envName = CampCoverageRuntimeSourceGenerator.EnvironmentVariableName(projectName);
				result[envName] = Path.Combine(buildDirectory, projectName + ".camp-coverage-counts.csv");
			}
			return result;
		}

		static string ProjectNameFromCoverageMapPath(string mapPath)
		{
			string fileName = Path.GetFileName(mapPath);
			const string suffix = ".camp-coverage-map.csv";
			return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
				? fileName[..^suffix.Length]
				: Path.GetFileNameWithoutExtension(fileName);
		}

		CampTestResults RunTestHarness(string executablePath, string buildDirectory, IReadOnlyList<CampTestManifestEntry> selectedTests, IReadOnlyDictionary<string, string> coverageCountPaths)
		{
			string eventPath = Path.Combine(buildDirectory, Path.GetFileNameWithoutExtension(executablePath) + ".camp-test-events.tsv");
			try
			{
				if (File.Exists(eventPath))
					File.Delete(eventPath);
				foreach (string countPath in coverageCountPaths.Values)
					if (File.Exists(countPath))
						File.Delete(countPath);
				ProcessStartInfo info = new()
				{
					FileName = executablePath,
					WorkingDirectory = Path.GetDirectoryName(executablePath) ?? request.WorkingDirectory,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false
				};
				info.ArgumentList.Add(eventPath);
				foreach ((string name, string path) in coverageCountPaths)
					info.Environment[name] = path;
				using Process process = new() { StartInfo = info };
				process.Start();
				System.Threading.Tasks.Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
				System.Threading.Tasks.Task<string> stderrTask = process.StandardError.ReadToEndAsync();
				if (!process.WaitForExit(300000))
				{
					try
					{
						process.Kill(entireProcessTree: true);
					}
					catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
					{
					}
					return CampTestResultsFactory.InfrastructureError(selectedTests, "test harness timed out");
				}
				string harnessStdOut = Normalize(stdoutTask.GetAwaiter().GetResult());
				string harnessStdErr = Normalize(stderrTask.GetAwaiter().GetResult());
				if (!CampTestHarnessEventParser.TryRead(eventPath, out List<CampTestHarnessEvent> events, out List<string> diagnostics))
				{
					string message = string.Join(" ", diagnostics);
					if (!string.IsNullOrWhiteSpace(harnessStdErr))
						message = string.IsNullOrWhiteSpace(message) ? harnessStdErr.TrimEnd() : message + " " + harnessStdErr.TrimEnd();
					return CampTestResultsFactory.InfrastructureError(selectedTests, message);
				}
				CampTestResults results = CampTestResultsFactory.FromHarnessEvents(selectedTests, events, process.ExitCode, string.IsNullOrWhiteSpace(harnessStdErr) ? null : harnessStdErr.TrimEnd());
				if (process.ExitCode != 0 && TestResultsSucceeded(results))
					return CampTestResultsFactory.InfrastructureError(selectedTests, string.IsNullOrWhiteSpace(harnessStdErr) ? $"test harness exited with code {process.ExitCode}" : harnessStdErr.TrimEnd());
				_ = harnessStdOut;
				return results;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
			{
				return CampTestResultsFactory.InfrastructureError(selectedTests, ex.Message);
			}
		}

		static bool TestResultsSucceeded(CampTestResults results)
		{
			return results.Summary.Failed == 0 && results.Summary.Invalid == 0 && results.Summary.Error == 0;
		}

		bool TryEmitDebugArtifact(Compilation compilation, string outputDirectory, string projectName, IReadOnlyList<CDebugMapEntry> entries)
		{
			string debugPath = Path.Combine(outputDirectory, projectName + ".campdebug.json");
			try
			{
				Directory.CreateDirectory(outputDirectory);
				AddGeneratedFile(debugPath, BuildFileIO.WriteTextIfChanged(debugPath, CDebugMapSerializer.Serialize(compilation, projectName, outputDirectory, entries), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				ErrorLine($"{debugPath}: {ex.Message}");
				return false;
			}

			return true;
		}

		bool TryEmitLibraryApiArtifacts(Compilation compilation, string outputDirectory)
		{
			string projectName = string.IsNullOrWhiteSpace(request.ProjectName) ? CCodeEmitter.GetProjectName(compilation.Files) : request.ProjectName!;
			string campApiPath = Path.Combine(outputDirectory, projectName + "_api.camp");
			try
			{
				Directory.CreateDirectory(outputDirectory);
				using StringWriter writer = new(CultureInfo.InvariantCulture);
				CampApiSurfaceKind apiSurface = request.BuildKind == NativeBuildKind.Static ? CampApiSurfaceKind.Public : CampApiSurfaceKind.Export;
				BindableNodeCodeSerializer.Serialize(BuildApiOutputModule(compilation, apiSurface), writer, new BindableNodeCodeSerializerOptions { ApiHeader = true, ApiSurface = apiSurface, ApiDefinitionsAlreadyFiltered = true, ApiReferenceDefinitions = compilation.SharedModule!.Definitions });
				AddGeneratedFile(campApiPath, BuildFileIO.WriteTextIfChanged(campApiPath, writer.ToString(), Encoding.UTF8));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				ErrorLine($"{campApiPath}: {ex.Message}");
				return false;
			}

			CEmissionResult apiHeader = CCodeEmitter.EmitProjectApiHeader(compilation, new CEmissionOptions
			{
				OutputDirectory = outputDirectory,
				ProjectName = projectName,
				EmitKind = request.EmitKind,
				BuildKind = request.BuildKind,
				ApiSurface = request.BuildKind == NativeBuildKind.Static ? CampApiSurfaceKind.Public : CampApiSurfaceKind.Export
			}, outputDirectory);
			foreach (string diagnostic in apiHeader.Diagnostics)
				ErrorLine(diagnostic);
			if (!apiHeader.Success)
				return false;
			foreach (string generated in apiHeader.GeneratedFiles)
			{
				AddGeneratedFile(generated, apiHeader.FileStatuses.GetValueOrDefault(generated, BuildFileWriteStatus.Changed));
			}
			return true;
		}

		int PrintTokens(Compilation compilation)
		{
			CompilationPipeline.Tokenize(compilation);
			foreach (Token token in compilation.Files[0].Tokens!)
				OutLine($"\"{EscapeTokenValue(token.Value)}\" {token.Class}");
			return 0;
		}

		int PrintDeclarations(Compilation compilation)
		{
			if (!ExpandDeclarationsAndReport(compilation))
				return 1;
			AnalysisResult analysis = BindableNodeAnalyzer.AnalyzeExpanded(compilation.DeclarationExpansion!, compilation.Target);
			if (!PrintAnalysisDiagnostics(compilation, analysis.Diagnostics))
				return 1;
			compilation.SharedModule = analysis.Module;
			PrintBindable(BuildOutputModule(compilation, compilation.Files[0]));
			return 0;
		}

		int PrintLowering(Compilation compilation)
		{
			if (!LowerAndReport(compilation))
				return 1;
			PrintBindable(BuildOutputModule(compilation, compilation.Files[0]));
			return 0;
		}

		int PrintApi(Compilation compilation)
		{
			if (!BuildAllAndReport(compilation))
				return 1;
			AnalysisResult analysis = BindableNodeAnalyzer.Analyze(compilation.SharedModule!, compilation.Target);
			if (!PrintAnalysisDiagnostics(compilation, analysis.Diagnostics))
				return 1;
			compilation.SharedModule = analysis.Module;
			using StringWriter writer = new(stdout, CultureInfo.InvariantCulture);
			BindableNodeCodeSerializer.Serialize(BuildApiOutputModule(compilation, CampApiSurfaceKind.Export), writer, new BindableNodeCodeSerializerOptions { ApiHeader = true, ApiDefinitionsAlreadyFiltered = true, ApiReferenceDefinitions = compilation.SharedModule.Definitions });
			return 0;
		}

		int PrintMetadata(Compilation compilation)
		{
			if (!BuildAllAndReport(compilation))
				return 1;
			AnalysisResult analysis = BindableNodeAnalyzer.Analyze(compilation.SharedModule!, compilation.Target);
			if (!PrintAnalysisDiagnostics(compilation, analysis.Diagnostics))
				return 1;
			compilation.SharedModule = analysis.Module;
			MetadataVisibility visibility = GetEffectiveMetadataVisibility();
			if (visibility == MetadataVisibility.None)
				visibility = MetadataVisibility.Export;
			stdout.Append(MetadataJsonSerializer.Serialize(compilation, visibility));
			return 0;
		}

		bool ParseAllAndReport(Compilation compilation)
		{
			bool success = CompilationPipeline.Parse(compilation);
			foreach (SourceFile file in compilation.Files)
				foreach (ParseDiagnostic diagnostic in file.ParseDiagnostics)
					PrintDiagnostic(file.Path, diagnostic.Range, diagnostic.Message, diagnostic.Severity);
			return success;
		}

		bool BuildAllAndReport(Compilation compilation)
		{
			bool success = CompilationPipeline.BuildAst(compilation);
			foreach (SourceFile file in compilation.Files)
			{
				foreach (ParseDiagnostic diagnostic in file.ParseDiagnostics)
					PrintDiagnostic(file.Path, diagnostic.Range, diagnostic.Message, diagnostic.Severity);
				foreach (BindDiagnostic diagnostic in file.BindDiagnostics)
					PrintDiagnostic(file.Path, diagnostic.Range, diagnostic.Message, diagnostic.Severity);
			}
			return success;
		}

		bool ExpandDeclarationsAndReport(Compilation compilation)
		{
			bool success;
			using (timing.Begin("declaration expansion", "compiler-phase"))
				success = CompilationPipeline.ExpandDeclarations(compilation);
			PrintPipelineDiagnostics(compilation);
			return success;
		}

		bool LowerAndReport(Compilation compilation)
		{
			bool success = true;
			using (timing.Begin("analysis and lowering", "compiler-phase"))
			{
				bool buildSuccess;
				using (timing.Begin("parse and AST binding", "compiler-phase"))
					buildSuccess = CompilationPipeline.BuildAst(compilation);
				success &= buildSuccess;
				if (buildSuccess)
				{
					bool expansionSuccess;
					using (timing.Begin("declaration expansion", "compiler-phase"))
						expansionSuccess = CompilationPipeline.ExpandDeclarationsFromBuiltAst(compilation);
					success &= expansionSuccess;
					if (expansionSuccess)
					{
						bool lowerSuccess;
						using (timing.Begin("lowering", "compiler-phase"))
						{
							lowerSuccess = CompilationPipeline.LowerFromExpandedDeclarations(compilation, (name, action) =>
							{
								using IDisposable _ = timing.Begin(name, "compiler-phase");
								action();
							});
						}
						success &= lowerSuccess;
					}
				}
			}
			PrintPipelineDiagnostics(compilation);
			return success;
		}

		void PrintPipelineDiagnostics(Compilation compilation)
		{
			HashSet<string> printed = [];
			foreach (SourceFile file in compilation.Files)
			{
				foreach (ParseDiagnostic diagnostic in file.ParseDiagnostics)
					PrintDiagnosticOnce(file.Path, diagnostic.Range, diagnostic.Message, diagnostic.Severity, printed);
				foreach (BindDiagnostic diagnostic in file.BindDiagnostics)
					PrintDiagnosticOnce(file.Path, diagnostic.Range, diagnostic.Message, diagnostic.Severity, printed);
			}
			if (compilation.DeclarationExpansion is not null && compilation.Lowering is null)
				foreach (AnalysisDiagnostic diagnostic in compilation.DeclarationExpansion.Diagnostics)
					PrintDiagnosticOnce(GetDiagnosticFilename(compilation, diagnostic.Range), diagnostic.Range, diagnostic.Message, diagnostic.Severity, printed);
			if (compilation.Lowering is not null)
				foreach (AnalysisDiagnostic diagnostic in compilation.Lowering.Diagnostics)
					PrintDiagnosticOnce(GetDiagnosticFilename(compilation, diagnostic.Range), diagnostic.Range, diagnostic.Message, diagnostic.Severity, printed);
		}

		bool PrintAnalysisDiagnostics(Compilation compilation, IReadOnlyList<AnalysisDiagnostic> diagnostics)
		{
			foreach (AnalysisDiagnostic diagnostic in diagnostics)
				PrintDiagnostic(GetDiagnosticFilename(compilation, diagnostic.Range), diagnostic.Range, diagnostic.Message, diagnostic.Severity);
			return !diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
		}

		static Module BuildApiOutputModule(Compilation compilation, CampApiSurfaceKind apiSurface)
		{
			Module output = new() { ResolvedType = compilation.SharedModule?.ResolvedType };
			if (compilation.SharedModule is not null)
			{
				foreach (KeyValuePair<TokenSequence, WithinAllocationPolicy> policy in compilation.SharedModule.SourceWithinAllocationPolicies)
					output.SourceWithinAllocationPolicies[policy.Key] = policy.Value;
			}
			HashSet<Definition> definitions = [];
			foreach (SourceFile file in compilation.Files)
			{
				if (file.IsApiHeader || file.BindableTree is not Module module)
					continue;
				output.SourceSyntax ??= module.SourceSyntax;
				output.Namespace ??= module.Namespace;
				foreach (Definition definition in module.Definitions)
				{
					if (DeclarationParticipation.Includes(definition, compilation.SharedModule!)
						&& ShouldIncludeInApiOutput(definition, module.Definitions, apiSurface)
						&& definitions.Add(definition))
						output.Definitions.Add(definition);
				}
			}
			foreach (Definition definition in compilation.SharedModule?.Definitions ?? [])
			{
				if (!DeclarationParticipation.Includes(definition, compilation.SharedModule!)
					|| !IsApiGeneratedDefinition(definition)
					|| !IsVisibleInApiSurface(definition, apiSurface))
					continue;
				if (compilation.DefinitionOwners.TryGetValue(definition, out SourceFile? owner) && owner.IsApiHeader)
					continue;
				if (definitions.Add(definition))
					output.Definitions.Add(definition);
			}
			if (apiSurface == CampApiSurfaceKind.Public)
				AddApiDependencyDefinitions(compilation, output, definitions);
			return output;
		}

		static bool IsVisibleInApiSurface(Definition definition, CampApiSurfaceKind apiSurface)
		{
			return definition.Export is not null
				|| apiSurface == CampApiSurfaceKind.Public && definition.Public is not null;
		}

		static bool ShouldIncludeInApiOutput(Definition definition, IReadOnlyList<Definition> sourceDefinitions, CampApiSurfaceKind apiSurface)
		{
			if (IsVisibleInApiSurface(definition, apiSurface))
				return true;
			if (definition is StaticClassDefinition staticClassDefinition)
				return StaticClassHasVisibleApiMember(staticClassDefinition, sourceDefinitions, apiSurface);
			return false;
		}

		static bool StaticClassHasVisibleApiMember(StaticClassDefinition definition, IReadOnlyList<Definition> sourceDefinitions, CampApiSurfaceKind apiSurface)
		{
			return definition.Fields.Any(field => field.Modifier == FieldModifier.Static && IsVisibleInApiSurface(field, apiSurface))
				|| definition.Functions.Any(function => IsVisibleInApiSurface(function, apiSurface))
				|| sourceDefinitions.OfType<FunctionDefinition>().Any(function => function.OutOfScopeOwnerName == definition.Name && IsVisibleInApiSurface(function, apiSurface));
		}

		static void AddApiDependencyDefinitions(Compilation compilation, Module output, HashSet<Definition> definitions)
		{
			Queue<TypeDefinition> pending = new();
			foreach (Definition definition in output.Definitions)
				CollectApiDependencyTypes(definition, pending, definitions);

			while (pending.Count > 0)
			{
				TypeDefinition dependency = pending.Dequeue();
				if (compilation.SharedModule is not null && !DeclarationParticipation.Includes(dependency, compilation.SharedModule))
					continue;
				if (!definitions.Add(dependency))
					continue;
				if (compilation.DefinitionOwners.TryGetValue(dependency, out SourceFile? owner) && owner.IsApiHeader)
					continue;
				output.Definitions.Add(dependency);
				CollectApiDependencyTypes(dependency, pending, definitions);
			}
		}

		static void CollectApiDependencyTypes(Definition definition, Queue<TypeDefinition> pending, HashSet<Definition> selected)
		{
			switch (definition)
			{
				case ClassDefinition classDefinition:
					foreach (TypeReference type in classDefinition.BaseTypes)
						CollectApiDependencyTypes(type, pending, selected);
					foreach (FieldDefinition field in classDefinition.Fields)
						if (field.Modifier == FieldModifier.Static)
							CollectApiDependencyTypes(field.Type, pending, selected);
					foreach (FunctionDefinition function in classDefinition.Functions)
						if (IsVisibleInApiSurface(function, CampApiSurfaceKind.Public))
							CollectApiDependencyTypes(function, pending, selected);
					break;

				case StructDefinition structDefinition:
					foreach (TypeReference type in structDefinition.BaseTypes)
						CollectApiDependencyTypes(type, pending, selected);
					foreach (FieldDefinition field in structDefinition.Fields)
						CollectApiDependencyTypes(field.Type, pending, selected);
					foreach (FunctionDefinition function in structDefinition.Functions)
						if (IsVisibleInApiSurface(function, CampApiSurfaceKind.Public))
							CollectApiDependencyTypes(function, pending, selected);
					break;

				case InterfaceDefinition interfaceDefinition:
					foreach (TypeReference type in interfaceDefinition.BaseTypes)
						CollectApiDependencyTypes(type, pending, selected);
					foreach (FunctionDefinition function in interfaceDefinition.Functions)
						CollectApiDependencyTypes(function, pending, selected);
					break;

				case EnumDefinition enumDefinition:
					CollectApiDependencyTypes(enumDefinition.UnderlyingType, pending, selected);
					break;

				case NewtypeDefinition newtypeDefinition:
					CollectApiDependencyTypes(newtypeDefinition.UnderlyingType, pending, selected);
					foreach (FieldDefinition field in newtypeDefinition.Fields)
						CollectApiDependencyTypes(field.Type, pending, selected);
					foreach (FunctionDefinition function in newtypeDefinition.Functions)
						if (IsVisibleInApiSurface(function, CampApiSurfaceKind.Public))
							CollectApiDependencyTypes(function, pending, selected);
					break;

				case FunctionDefinition functionDefinition:
					CollectApiDependencyTypes(functionDefinition.ReturnType, pending, selected);
					CollectApiDependencyTypes(functionDefinition.CallableAscriptionType, pending, selected);
					foreach (ParameterDefinition parameter in functionDefinition.Parameters)
					{
						CollectApiDependencyTypes(parameter.Type, pending, selected);
						if (parameter is VTableOfParameterDefinition vtable)
							CollectApiDependencyTypes(vtable.InterfaceType, pending, selected);
					}
					break;

				case VariableDefinition variableDefinition:
					CollectApiDependencyTypes(variableDefinition.Type, pending, selected);
					break;
			}
		}

		static void CollectApiDependencyTypes(TypeReference? type, Queue<TypeDefinition> pending, HashSet<Definition> selected)
		{
			switch (type)
			{
				case null:
					return;

				case TypeDefinitionReference reference:
					foreach (TypeReference argument in reference.TypeArguments)
						CollectApiDependencyTypes(argument, pending, selected);
					if (reference.Definition is not null && !selected.Contains(reference.Definition))
						pending.Enqueue(reference.Definition);
					break;

				case ClassTypeReference classType:
					if (classType.Definition is not null && !selected.Contains(classType.Definition))
						pending.Enqueue(classType.Definition);
					break;

				case NamedTypeReference named:
					foreach (TypeReference argument in named.TypeArguments)
						CollectApiDependencyTypes(argument, pending, selected);
					break;

				case AttributedTypeReference attributed:
					CollectApiDependencyTypes(attributed.Type, pending, selected);
					break;

				case GenericTypeReference generic:
					CollectApiDependencyTypes(generic.Type, pending, selected);
					foreach (TypeReference argument in generic.TypeArguments)
						CollectApiDependencyTypes(argument, pending, selected);
					break;

				case ArrayTypeReference array:
					CollectApiDependencyTypes(array.ElementType, pending, selected);
					break;

				case FixedArrayTypeReference fixedArray:
					CollectApiDependencyTypes(fixedArray.ElementType, pending, selected);
					break;

				case OptionalTypeReference optional:
					CollectApiDependencyTypes(optional.ElementType, pending, selected);
					break;

				case PointerTypeReference pointer:
					CollectApiDependencyTypes(pointer.ElementType, pending, selected);
					break;

				case ConstTypeReference constType:
					CollectApiDependencyTypes(constType.Type, pending, selected);
					break;

				case ConstOfTypeReference constOf:
					CollectApiDependencyTypes(constOf.Type, pending, selected);
					break;

				case VolatileTypeReference volatileType:
					CollectApiDependencyTypes(volatileType.Type, pending, selected);
					break;

				case EscapedTypeReference escaped:
					CollectApiDependencyTypes(escaped.Type, pending, selected);
					break;

				case ScopedTypeReference scoped:
					CollectApiDependencyTypes(scoped.Type, pending, selected);
					break;

				case UnscopedTypeReference unscoped:
					CollectApiDependencyTypes(unscoped.Type, pending, selected);
					break;

				case CallableTypeReference callable:
					CollectApiDependencyTypes(callable.ReturnType, pending, selected);
					foreach (ParameterDefinition parameter in callable.Parameters)
						CollectApiDependencyTypes(parameter.Type, pending, selected);
					break;

				case TargetTypeSpecTypeReference targetSpec:
					CollectApiDependencyTypes(targetSpec.Type, pending, selected);
					break;

				case IterTypeReference iter:
					CollectApiDependencyTypes(iter.ElementType, pending, selected);
					foreach (ParameterDefinition parameter in iter.Parameters)
						CollectApiDependencyTypes(parameter.Type, pending, selected);
					break;

				case GroupedParamsTypeReference grouped:
					CollectApiDependencyTypes(grouped.StructType, pending, selected);
					break;

				case MaterializedStructTypeReference materialized:
					CollectApiDependencyTypes(materialized.ParamsType, pending, selected);
					break;

				case ThrownTypeReference thrown:
					CollectApiDependencyTypes(thrown.Type, pending, selected);
					break;
			}
		}

		static bool IsApiGeneratedDefinition(Definition definition)
		{
			return definition.GeneratedInfo?.Category == GeneratedDeclarationCategory.Iterator
				|| definition.GeneratedInfo?.Reason.StartsWith("export projection for ", StringComparison.Ordinal) == true;
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

		static string GetMetadataProjectName(IReadOnlyList<SourceFile> files)
		{
			SourceFile? first = files.FirstOrDefault(static file => !file.IsApiHeader) ?? files.FirstOrDefault();
			if (first is null || first.Path == "-")
				return "stdin";

			string path = first.Path.Replace('\\', '/');
			string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i + 2 < parts.Length; i++)
			{
				if ((parts[i] == "lib" || parts[i] == "pkg") && parts[i + 2] == "src")
					return parts[i + 1];
			}

			return CCodeEmitter.GetProjectName(files);
		}

		void PrintBindable(Module module)
		{
			using StringWriter textWriter = new(stdout, CultureInfo.InvariantCulture);
			BindableNodeCodeSerializer.Serialize(module, textWriter);
		}

		void PrintDiagnostic(string filename, TokenRange? range, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error)
		{
			string severityText = severity.ToString().ToLowerInvariant();
			if (range is TokenRange tokenRange)
				ErrorLine($"{filename}({tokenRange.StartLineNumber},{tokenRange.StartColumn}): {severityText}: {message}");
			else
				ErrorLine($"{filename}(1,1): (no line,column) {severityText}: {message}");
		}

		void PrintDiagnosticOnce(string filename, TokenRange? range, string message, DiagnosticSeverity severity, HashSet<string> printed)
		{
			string key = range is TokenRange r ? $"{filename}:{r.StartLineNumber}:{r.StartColumn}:{severity}:{message}" : $"{filename}:::{severity}:${message}";
			if (printed.Add(key))
				PrintDiagnostic(filename, range, message, severity);
		}

		static string GetDiagnosticFilename(Compilation compilation, TokenRange? range)
		{
			if (range is TokenRange tokenRange)
			{
				foreach (SourceFile file in compilation.Files)
					if (ReferenceEquals(file.Tokens, tokenRange.Sequence))
						return file.Path;
			}
			return compilation.Files.Count == 0 ? "" : compilation.Files[0].Path;
		}

		static string EscapeTokenValue(string value)
		{
			return value.Replace("\\", "\\\\", StringComparison.Ordinal)
				.Replace("\t", "\\t", StringComparison.Ordinal)
				.Replace("\r", "\\r", StringComparison.Ordinal)
				.Replace("\n", "\\n", StringComparison.Ordinal)
				.Replace("\"", "\\\"", StringComparison.Ordinal);
		}

		void OutLine(string line)
		{
			stdout.Append(line).Append('\n');
		}

		void AddGeneratedFile(string path, BuildFileWriteStatus status)
		{
			if (!generatedFiles.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
				generatedFiles.Add(path);
			generatedFileStatuses[path] = status;
			OutGenerated(path, status);
		}

		Dictionary<string, BuildFileWriteStatus> BuildSourceStatuses(CEmissionResult emission)
		{
			Dictionary<string, BuildFileWriteStatus> statuses = new(StringComparer.OrdinalIgnoreCase);
			foreach ((string path, BuildFileWriteStatus status) in emission.FileStatuses)
				statuses[path] = status;
			foreach ((string path, BuildFileWriteStatus status) in generatedFileStatuses)
				if (Path.GetExtension(path).Equals(".c", StringComparison.OrdinalIgnoreCase))
					statuses[path] = status;
			return statuses;
		}

		void OutGenerated(string path, BuildFileWriteStatus status)
		{
			if (request.Verbose)
				OutLine((status == BuildFileWriteStatus.Changed ? "generated: " : "unchanged: ") + Path.GetFileName(path));
		}

		void ErrorLine(string line)
		{
			stderr.Append(line).Append('\n');
		}

		static string Normalize(string text)
		{
			return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
		}

		static bool OutputsAreCurrent(IReadOnlyList<string> outputs, IReadOnlyList<string> inputs, out string? reason)
		{
			reason = null;
			if (outputs.Count == 0)
			{
				reason = "an output is missing";
				return false;
			}
			foreach (string output in outputs)
			{
				if (!File.Exists(output))
				{
					reason = $"{output} is missing";
					return false;
				}
			}
			DateTime oldestOutput = outputs.Select(File.GetLastWriteTimeUtc).Min();
			foreach (string input in inputs.Distinct(StringComparer.OrdinalIgnoreCase))
			{
				if (File.Exists(input))
				{
					if (oldestOutput < File.GetLastWriteTimeUtc(input))
					{
						reason = $"{input} is newer than the oldest output";
						return false;
					}
				}
				else if (Directory.Exists(input))
				{
					if (oldestOutput < Directory.GetLastWriteTimeUtc(input))
					{
						reason = $"{input} is newer than the oldest output";
						return false;
					}
				}
				else
				{
					reason = $"{input} is missing";
					return false;
				}
			}
			return true;
		}
	}

		sealed record RuntimeContext(string PackageSourceRoot, string PackageArtifactRoot, TargetDefinition Target, string ProfileName, IReadOnlyList<string> CommandLineDefines);
}
