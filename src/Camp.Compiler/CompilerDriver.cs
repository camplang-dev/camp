using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Camp.Compiler;

public enum CompilerInspectMode
{
	None,
	Tokens,
	Cst,
	Ast,
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
	public List<string> IncludeFiles { get; } = [];
	public List<string> AnalysisSourceFiles { get; } = [];
	public List<string> Defines { get; } = [];
	public CompilerInspectMode? Inspect { get; set; }
	public bool Xml { get; set; }
	public bool InspectApi { get; set; }
	public string TargetName { get; set; } = CompilerDefaults.TargetName;
	public string ProfileName { get; set; } = "DEBUG";
	public List<string> Variants { get; } = [];
	public string EmitKind { get; set; } = "c99";
	public NativeBuildKind? BuildKind { get; set; }
	public bool InferBuildKind { get; set; }
	public MetadataVisibility? EmitMetadata { get; set; }
	public string? OutDir { get; set; }
	public string? ProjectName { get; set; }
	public string? SubsystemName { get; set; }
	public bool NoStdLib { get; set; }
	public WithinAllocationPolicy? WithinAllocationPolicy { get; set; }
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

		public CompilerResult Execute()
		{
			int exitCode = Run();
			CompilerResult result = new()
			{
				ExitCode = exitCode,
				StdOut = Normalize(stdout.ToString()),
				StdErr = Normalize(stderr.ToString())
			};
			result.GeneratedFiles.AddRange(generatedFiles);
			return result;
		}

		int Run()
		{
			if (request.Files.Count == 0)
				return Error("At least one filename is required.");

			if (request.Files.Count > 1 && request.Files.Contains("-") || request.IncludeFiles.Count > 0 && request.Files.Contains("-"))
				return Error("Standard input may only be used by itself and cannot be combined with API headers.");
			if (request.IncludeFiles.Contains("-"))
				return Error("API headers must be read from files, not standard input.");
			if (request.InferBuildKind && !TryInferBuildKind())
				return 1;
			ApplySubsystem();
			if (request.Xml && request.Inspect is not (CompilerInspectMode.Declarations or CompilerInspectMode.Lowering))
				return Error("--xml can only be used with --inspect declarations or --inspect lowering.");
			if (request.InspectApi && (request.Inspect is not null || request.Xml))
				return Error("--inspect-api cannot be combined with --inspect or --xml.");
			if (request.BuildKind is not null && request.Inspect is not null)
				return Error("--artifact cannot be combined with dump commands.");
			if (request.BuildKind is not null && request.InspectApi)
				return Error("--artifact cannot be combined with --inspect-api.");
			if (GetEffectiveMetadataVisibility() != MetadataVisibility.None && (request.Inspect is not null and not CompilerInspectMode.Metadata || request.InspectApi || request.Xml))
				return Error("--metadata cannot be combined with non-metadata dump commands, --inspect-api, or --xml.");

			if (!TryCreateRuntimeContext(out RuntimeContext? context))
				return 1;
			if (!ValidateFrameworks(context!.Target))
				return 1;

			List<string> packageApiHeaders = [];
			List<string> packageLibraries = [];
			if (!request.NoStdLib)
			{
				if (!TryPreparePackage(context!, "std", request.BuildKind is not null, out string? stdApiHeader, out string? stdLibrary))
					return 1;
				if (stdApiHeader is not null)
					packageApiHeaders.Add(stdApiHeader);
				if (stdLibrary is not null)
					packageLibraries.Add(stdLibrary);
			}
			foreach (string package in request.UsePackages)
			{
				if (!TryPrepareInstalledPackage(context!, package, request.BuildKind is not null, out string? packageApiHeader, out string? packageLibrary, out bool sharedDependency))
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

			List<string> allIncludes = [.. packageApiHeaders, .. request.IncludeFiles];
			if (!TryLoadCompilation(request.Files, allIncludes, context!, out Compilation compilation))
				return 1;

			if (request.InspectApi)
				return PrintApi(compilation);

			CompilerInspectMode inspect = request.Inspect ?? CompilerInspectMode.None;
			return inspect switch
			{
				CompilerInspectMode.None => EmitDefaultOutput(compilation, packageLibraries),
				CompilerInspectMode.Tokens => PrintTokens(compilation),
				CompilerInspectMode.Cst => PrintSyntaxXml(compilation),
				CompilerInspectMode.Ast => PrintBindXml(compilation),
				CompilerInspectMode.Declarations => PrintDeclarations(compilation),
				CompilerInspectMode.Lowering => PrintLowering(compilation),
				CompilerInspectMode.Metadata => PrintMetadata(compilation),
				_ => 1
			};
		}

		void ApplySubsystem()
		{
			if (request.BuildKind == NativeBuildKind.Exec && request.SubsystemName?.Equals("windows", StringComparison.OrdinalIgnoreCase) == true)
				request.BuildKind = NativeBuildKind.WinExe;
		}

		bool TryInferBuildKind()
		{
			foreach (string filename in request.Files)
			{
				if (filename == "-")
				{
					request.BuildKind = NativeBuildKind.Static;
					return true;
				}

				try
				{
					string fullPath = Path.GetFullPath(filename, request.WorkingDirectory);
					string text = File.ReadAllText(fullPath);
					if (HasPublicOrExportedMain(text))
					{
						request.BuildKind = NativeBuildKind.Exec;
						return true;
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
				{
					ErrorLine($"{filename}: {ex.Message}");
					return false;
				}
			}

			request.BuildKind = NativeBuildKind.Static;
			return true;
		}

		WithinAllocationPolicy GetEffectiveWithinAllocationPolicy()
		{
			if (request.WithinAllocationPolicy is WithinAllocationPolicy policy)
				return policy;
			return request.BuildKind is NativeBuildKind.Static or NativeBuildKind.Shared
				? WithinAllocationPolicy.Explicit
				: WithinAllocationPolicy.Implicit;
		}

		static bool HasPublicOrExportedMain(string text)
		{
			for (int i = 0; i < text.Length;)
			{
				int found = text.IndexOf("main", i, StringComparison.Ordinal);
				if (found < 0)
					return false;
				i = found + 4;
				if (!IsIdentifierBoundary(text, found - 1) || !IsIdentifierBoundary(text, found + 4))
					continue;
				int j = found + 4;
				while (j < text.Length && char.IsWhiteSpace(text[j]))
					j++;
				if (j >= text.Length || text[j] != '(')
					continue;
				string prefix = text[..found];
				int visibilityIndex = Math.Max(prefix.LastIndexOf("export", StringComparison.Ordinal), prefix.LastIndexOf("internal", StringComparison.Ordinal));
				if (visibilityIndex >= 0 && found - visibilityIndex < 256 && IsIdentifierBoundary(text, visibilityIndex - 1))
					return true;
			}
			return false;
		}

		static bool IsIdentifierBoundary(string text, int index)
		{
			return index < 0 || index >= text.Length || !(char.IsLetterOrDigit(text[index]) || text[index] == '_');
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
			if (!TargetCatalog.TryLoad(targetsDirectory, out TargetCatalog? catalog, out string? error))
			{
				ErrorLine(error ?? $"Target directory '{targetsDirectory}' could not be loaded.");
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
			return Path.GetFullPath(string.IsNullOrWhiteSpace(request.TargetRoot)
				? Path.Combine(request.RuntimeRoot, "..", "targets")
				: request.TargetRoot);
		}

		string GetPackageSourceRoot()
		{
			return Path.GetFullPath(string.IsNullOrWhiteSpace(request.PackageSourceRoot)
				? Path.Combine(request.RuntimeRoot, "..", "lib")
				: request.PackageSourceRoot);
		}

		string GetPackageArtifactRoot()
		{
			return Path.GetFullPath(string.IsNullOrWhiteSpace(request.PackageArtifactRoot)
				? Path.Combine(request.RuntimeRoot, "..", "cache", "lib")
				: request.PackageArtifactRoot);
		}

		bool TryLoadCompilation(List<string> filenames, List<string> includeFilenames, RuntimeContext context, out Compilation compilation)
		{
			compilation = new Compilation
			{
				Target = context.Target,
				ProfileName = context.ProfileName,
				DefaultWithinAllocationPolicy = GetEffectiveWithinAllocationPolicy()
			};
			AddPreprocessorSymbols(compilation, context);
			foreach (string filename in filenames)
			{
				if (!TryReadInput(filename, out string text, out string displayPath))
					return false;
				if (!TryReadWithinAllocationPolicy(displayPath, text, out WithinAllocationPolicy? policy))
					return false;
				compilation.Files.Add(new SourceFile { Path = displayPath, Text = text, WithinAllocationPolicyOverride = policy });
			}
			foreach (string filename in includeFilenames)
			{
				if (!TryReadInput(filename, out string text, out string displayPath))
					return false;
				if (!TryReadWithinAllocationPolicy(displayPath, text, out WithinAllocationPolicy? policy))
					return false;
				compilation.Files.Add(new SourceFile { Path = displayPath, Text = text, IsApiHeader = true, SharedLibraryImport = IsSharedLibraryApiHeader(filename), WithinAllocationPolicyOverride = policy });
			}
			return true;
		}

		bool IsSharedLibraryApiHeader(string filename)
		{
			string fullPath = Path.GetFullPath(filename, request.WorkingDirectory);
			return request.SharedLibraryApiHeaders.Any(header => string.Equals(Path.GetFullPath(header, request.WorkingDirectory), fullPath, StringComparison.OrdinalIgnoreCase));
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

		void AddPreprocessorSymbols(Compilation compilation, RuntimeContext context)
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
		}

		bool TryReadInput(string filename, out string text, out string displayPath)
		{
			try
			{
				if (filename == "-")
				{
					text = Console.In.ReadToEnd();
					displayPath = "-";
					return true;
				}

				string fullPath = Path.GetFullPath(filename, request.WorkingDirectory);
				text = File.ReadAllText(fullPath);
				displayPath = GetDisplayPath(fullPath);
				return true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				text = "";
				displayPath = filename;
				ErrorLine($"{filename}: {ex.Message}");
				return false;
			}
		}

		string GetDisplayPath(string fullPath)
		{
			string root = Path.GetFullPath(request.WorkingDirectory);
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

			if (!TryBuildPackage(packageName, sourceFiles, nativeSourceFiles, apiPath, requireNativeLibrary ? cApiPath : null, requireNativeLibrary ? metadataPath : null, requireNativeLibrary ? staticLibraryPath : null, requireNativeLibrary ? NativeBuildKind.Static : null, context))
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
				ErrorLine($"Package '{packageSpec}' is not installed. Run 'campc restore' or 'campc pkg install {packageSpec}'.");
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

			if (!TryBuildPackage(packageName, sourceFiles, nativeSourceFiles, apiPath, requireNativeApiArtifacts ? cApiPath : null, requireNativeApiArtifacts ? metadataPath : null, nativeLibraryPath, packageBuildKind, context))
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

		IEnumerable<(string InstallRoot, string ArtifactRoot)> GetInstalledPackageRoots()
		{
			string globalRoot = Path.GetFullPath(Path.Combine(request.RuntimeRoot, "..", "cache", "pkg"));
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
				if (outputTime <= File.GetLastWriteTimeUtc(sourceFile))
					return false;
			return true;
		}

		static IEnumerable<string> GetCompilerCacheInputs(RuntimeContext context)
		{
			string assemblyPath = typeof(CompilerDriver).Assembly.Location;
			if (!string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath))
				yield return assemblyPath;
			string? targetDirectory = Path.GetDirectoryName(context.Target.Path);
			if (!string.IsNullOrWhiteSpace(targetDirectory) && Directory.Exists(targetDirectory))
			{
				foreach (string targetFile in Directory.GetFiles(targetDirectory, "*.ini").OrderBy(static path => path, StringComparer.Ordinal))
					yield return targetFile;
			}
		}

		bool TryBuildPackage(string packageName, IReadOnlyList<string> sourceFiles, IReadOnlyList<string> nativeSourceFiles, string apiPath, string? cApiPath, string? metadataPath, string? nativeLibraryPath, NativeBuildKind? nativeBuildKind, RuntimeContext context)
		{
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
				NoStdLib = true
			};
			packageRequest.Files.AddRange(sourceFiles);
			packageRequest.UseSourceRoots.AddRange(request.UseSourceRoots);
			if (!TryLoadCompilation(packageRequest.Files, [], context, out Compilation packageCompilation))
				return false;

			if (!BuildAllAndReport(packageCompilation))
				return false;

			AnalysisResult analysis = BindableNodeAnalyzer.Analyze(packageCompilation.SharedModule!, packageCompilation.Target);
			if (!PrintAnalysisDiagnostics(packageCompilation, analysis.Diagnostics))
				return false;
			packageCompilation.SharedModule = analysis.Module;

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(apiPath)!);
				using StreamWriter writer = new(apiPath, append: false, Encoding.UTF8);
				BindableNodeCodeSerializer.Serialize(BuildApiOutputModule(packageCompilation), writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
				if (metadataPath is not null && !TryEmitMetadataArtifact(packageCompilation, Path.GetDirectoryName(metadataPath)!, MetadataVisibility.Export, packageName))
					return false;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				ErrorLine($"{apiPath}: {ex.Message}");
				return false;
			}

			string packageArtifactDirectory = Path.GetDirectoryName(cApiPath ?? nativeLibraryPath ?? apiPath)!;
			if (cApiPath is not null || nativeLibraryPath is not null)
			{
				if (!LowerAndReport(packageCompilation))
					return false;

				if (cApiPath is not null)
				{
					CEmissionResult apiHeader = CCodeEmitter.EmitProjectApiHeader(packageCompilation, new CEmissionOptions
					{
						OutputDirectory = packageArtifactDirectory,
						ProjectName = packageName,
						EmitKind = request.EmitKind,
						BuildKind = nativeBuildKind
					}, packageArtifactDirectory);
					foreach (string diagnostic in apiHeader.Diagnostics)
						ErrorLine(diagnostic);
					if (!apiHeader.Success)
						return false;
				}
			}

			if (nativeLibraryPath is null)
				return true;

			string packageBuildDirectory = Path.Combine(packageArtifactDirectory, "build");
			CEmissionResult emission = CCodeEmitter.Emit(packageCompilation, new CEmissionOptions
			{
				OutputDirectory = packageBuildDirectory,
				ProjectName = packageName,
				EmitKind = "c99",
				BuildKind = nativeBuildKind
			});
			foreach (string diagnostic in emission.Diagnostics)
				ErrorLine(diagnostic);
			if (!emission.Success)
				return false;

			NativeBuildResult build = NativeBuildDriver.Build(new NativeBuildOptions
			{
				Target = context.Target,
				ProfileName = context.ProfileName,
				BuildDirectory = packageBuildDirectory,
				OutputDirectory = Path.GetDirectoryName(nativeLibraryPath)!,
				ProjectName = packageName,
				Kind = nativeBuildKind ?? NativeBuildKind.Static,
				SourceFiles = emission.GeneratedSourceFiles.Concat(nativeSourceFiles).ToList()
			});
			foreach (string diagnostic in build.Diagnostics)
				ErrorLine(diagnostic);
			return build.Success;
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
			CEmissionResult result = CCodeEmitter.Emit(compilation, new CEmissionOptions
			{
				OutputDirectory = buildDirectory,
				ProjectName = projectName,
				EmitKind = request.EmitKind,
				BuildKind = request.BuildKind,
				EmitExecMainWrapper = request.BuildKind is NativeBuildKind.Exec or NativeBuildKind.WinExe,
				ExecEntryPoint = execEntryPoint
			});
			foreach (string diagnostic in result.Diagnostics)
				ErrorLine(diagnostic);
			if (!result.Success)
				return 1;

			foreach (string generated in result.GeneratedFiles)
			{
				generatedFiles.Add(generated);
				OutLine("generated: " + Path.GetFileName(generated));
			}

			if (request.BuildKind is NativeBuildKind.Static or NativeBuildKind.Shared && !TryEmitLibraryApiArtifacts(compilation, outputDirectory))
				return 1;

			MetadataVisibility metadataVisibility = GetEffectiveMetadataVisibility();
			if (metadataVisibility != MetadataVisibility.None && !TryEmitMetadataArtifact(compilation, outputDirectory, metadataVisibility, string.IsNullOrWhiteSpace(request.ProjectName) ? GetMetadataProjectName(compilation.Files) : request.ProjectName!))
				return 1;

			if (request.BuildKind is null)
				return 0;

			NativeBuildResult build = NativeBuildDriver.Build(new NativeBuildOptions
			{
				Target = compilation.Target!,
				ProfileName = compilation.ProfileName,
				BuildDirectory = buildDirectory,
				OutputDirectory = outputDirectory,
				ProjectName = projectName,
				Kind = request.BuildKind.Value,
				SourceFiles = result.GeneratedSourceFiles,
				Libraries = packageLibraries.Concat(request.References.Select(reference => ResolveNativeReference(reference, compilation.Target!))).ToList(),
				Frameworks = request.Frameworks
			});
			foreach (string diagnostic in build.Diagnostics)
				ErrorLine(diagnostic);
			if (!build.Success)
				return 1;
			foreach (string generated in build.GeneratedFiles)
			{
				generatedFiles.Add(generated);
				OutLine("generated: " + Path.GetFileName(generated));
			}
			if (!TryCopySharedRuntimeReferences(compilation.Target!, outputDirectory, packageLibraries.Concat(request.References.Select(reference => ResolveNativeReference(reference, compilation.Target!)))))
				return 1;
			return 0;
		}

		string ResolveArtifactOutputDirectory(Compilation compilation)
		{
			string outputPrefix = string.IsNullOrWhiteSpace(request.OutDir)
				? CCodeEmitter.GetDefaultArtifactDirectory(compilation.Files)
				: request.OutDir!;
			string outputRoot = Path.GetFullPath(outputPrefix, request.WorkingDirectory);
			if (IsDirectOutputDirectory(request.OutDir))
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
					File.Copy(runtimeReference, destination, overwrite: true);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
				{
					ErrorLine($"{destination}: {ex.Message}");
					return false;
				}
				generatedFiles.Add(destination);
				OutLine("generated: " + Path.GetFileName(destination));
			}
			return true;
		}

		bool ValidateFrameworks(TargetDefinition target)
		{
			if (request.Frameworks.Count == 0 || request.BuildKind is null)
				return true;
			if (request.BuildKind == NativeBuildKind.Static)
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
			foreach (Definition definition in compilation.SharedModule?.Definitions ?? [])
				if (definition is FunctionDefinition { Name: "main" } function && (function.Export is not null || function.Internal is not null))
					candidates.Add(function);

			if (candidates.Count != 1)
			{
				ErrorLine(candidates.Count == 0
					? "Building an executable requires exactly one public or exported function named 'main'."
					: "Building an executable requires exactly one public or exported function named 'main', but multiple were found.");
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
				File.WriteAllText(metadataPath, MetadataJsonSerializer.Serialize(compilation, visibility), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				ErrorLine($"{metadataPath}: {ex.Message}");
				return false;
			}

			generatedFiles.Add(metadataPath);
			OutLine("generated: " + Path.GetFileName(metadataPath));
			return true;
		}

		bool TryEmitLibraryApiArtifacts(Compilation compilation, string outputDirectory)
		{
			string projectName = string.IsNullOrWhiteSpace(request.ProjectName) ? CCodeEmitter.GetProjectName(compilation.Files) : request.ProjectName!;
			string campApiPath = Path.Combine(outputDirectory, projectName + "_api.camp");
			try
			{
				Directory.CreateDirectory(outputDirectory);
				using StreamWriter writer = new(campApiPath, append: false, Encoding.UTF8);
				BindableNodeCodeSerializer.Serialize(BuildApiOutputModule(compilation), writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
				generatedFiles.Add(campApiPath);
				OutLine("generated: " + Path.GetFileName(campApiPath));
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
				BuildKind = request.BuildKind
			}, outputDirectory);
			foreach (string diagnostic in apiHeader.Diagnostics)
				ErrorLine(diagnostic);
			if (!apiHeader.Success)
				return false;
			foreach (string generated in apiHeader.GeneratedFiles)
			{
				generatedFiles.Add(generated);
				OutLine("generated: " + Path.GetFileName(generated));
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

		int PrintSyntaxXml(Compilation compilation)
		{
			if (!ParseAllAndReport(compilation))
				return 1;
			PrintXmlDocument(CompilerXmlSerializer.SerializeSyntax(compilation.Files[0].SyntaxTree!));
			return 0;
		}

		int PrintBindXml(Compilation compilation)
		{
			if (!BuildAllAndReport(compilation))
				return 1;
			PrintXmlDocument(CompilerXmlSerializer.SerializeBindableNode(compilation.Files[0].BindableTree!));
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
			using StringWriter writer = new(stdout, CultureInfo.InvariantCulture);
			BindableNodeCodeSerializer.Serialize(BuildApiOutputModule(compilation), writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
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
			bool success = CompilationPipeline.ExpandDeclarations(compilation);
			PrintPipelineDiagnostics(compilation);
			return success;
		}

		bool LowerAndReport(Compilation compilation)
		{
			bool success = CompilationPipeline.Lower(compilation);
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

		static Module BuildApiOutputModule(Compilation compilation)
		{
			Module output = new() { ResolvedType = compilation.SharedModule?.ResolvedType };
			HashSet<string> usingKeys = [];
			foreach (SourceFile file in compilation.Files)
			{
				if (file.IsApiHeader || file.BindableTree is not Module module)
					continue;
				output.SourceSyntax ??= module.SourceSyntax;
				output.Namespace ??= module.Namespace;
				foreach (UsingDeclaration usingDeclaration in module.Usings)
				{
					if (usingKeys.Add(UsingDeclarationKey(usingDeclaration)))
						output.Usings.Add(usingDeclaration);
				}
				foreach (Definition definition in module.Definitions)
					output.Definitions.Add(definition);
			}
			return output;
		}

		static string UsingDeclarationKey(UsingDeclaration usingDeclaration)
		{
			return string.Join('\u001f',
				usingDeclaration.Name ?? "",
				usingDeclaration.Alias ?? "",
				string.Join('\u001e', usingDeclaration.SelectedNames));
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
				if (compilation.DefinitionOwners.TryGetValue(definition, out SourceFile? owner) && ReferenceEquals(owner, file))
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
			if (request.Xml)
				PrintXmlDocument(CompilerXmlSerializer.SerializeBindableNode(module));
			else
			{
				using StringWriter writer = new(stdout, CultureInfo.InvariantCulture);
				BindableNodeCodeSerializer.Serialize(module, writer);
			}
		}

		void PrintXmlDocument(XElement root)
		{
			XDocument document = new(new XDeclaration("1.0", "utf-8", null), root);
			XmlWriterSettings settings = new() { Indent = true, OmitXmlDeclaration = false };
			using StringWriter textWriter = new(stdout, CultureInfo.InvariantCulture);
			using XmlWriter writer = XmlWriter.Create(textWriter, settings);
			document.Save(writer);
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

		void ErrorLine(string line)
		{
			stderr.Append(line).Append('\n');
		}

		static string Normalize(string text)
		{
			return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
		}
	}

		sealed record RuntimeContext(string PackageSourceRoot, string PackageArtifactRoot, TargetDefinition Target, string ProfileName, IReadOnlyList<string> CommandLineDefines);
}
