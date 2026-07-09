using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Camp.Compiler;

public sealed record CampSourceOverlay(string Path, string Text, int Version = 0);

public sealed record CampTextPosition(int Line, int Character);

public sealed record CampTextRange(CampTextPosition Start, CampTextPosition End);

public sealed record CampSourceDiagnostic(
	string Path,
	CampTextRange? Range,
	string Message,
	string? Code,
	DiagnosticSeverity Severity);

public sealed class CampAnalysisSnapshot
{
	public required Compilation Compilation { get; init; }
	public required IReadOnlyList<CampSourceDiagnostic> Diagnostics { get; init; }
	public bool Success => Diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}

public static class CampLanguageService
{
	public static CampAnalysisSnapshot Analyze(CompilerRequest request, IReadOnlyList<CampSourceOverlay>? overlays = null)
	{
		ArgumentNullException.ThrowIfNull(request);

		Compilation compilation = CreateCompilation(request, overlays ?? []);
		CompilationPipeline.Lower(compilation);
		return new CampAnalysisSnapshot
		{
			Compilation = compilation,
			Diagnostics = CollectDiagnostics(compilation)
		};
	}

	static Compilation CreateCompilation(CompilerRequest request, IReadOnlyList<CampSourceOverlay> overlays)
	{
		Compilation compilation = new()
		{
			Target = LoadTarget(request),
			ProfileName = request.ProfileName,
			DefaultWithinAllocationPolicy = request.WithinAllocationPolicy
				?? (request.BuildKind is NativeBuildKind.Static or NativeBuildKind.Shared ? WithinAllocationPolicy.Explicit : WithinAllocationPolicy.Implicit)
		};
		foreach (string define in request.Defines)
			compilation.PreprocessorSymbols.Add(define);
		if (compilation.Target is not null)
		{
			foreach (string define in compilation.Target.Defines.Keys)
				compilation.PreprocessorSymbols.Add(define);
			foreach (string define in compilation.Target.TargetOwnedDefines)
				compilation.TargetOwnedPreprocessorSymbols.Add(define);
		}

		Dictionary<string, CampSourceOverlay> overlayByPath = overlays.ToDictionary(
			overlay => Path.GetFullPath(overlay.Path, request.WorkingDirectory),
			StringComparer.OrdinalIgnoreCase);

		HashSet<string> loadedPaths = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string Path, bool IsApiHeader) include in GetAnalysisIncludeFiles(request))
			AddSourceFileIfMissing(compilation, include.Path, request.WorkingDirectory, overlayByPath, include.IsApiHeader, loadedPaths);
		foreach (string file in request.AnalysisSourceFiles)
			AddSourceFileIfMissing(compilation, file, request.WorkingDirectory, overlayByPath, isApiHeader: false, loadedPaths);
		foreach (string file in request.Files)
			AddSourceFileIfMissing(compilation, file, request.WorkingDirectory, overlayByPath, isApiHeader: false, loadedPaths);
		return compilation;
	}

	static IReadOnlyList<(string Path, bool IsApiHeader)> GetAnalysisIncludeFiles(CompilerRequest request)
	{
		List<(string Path, bool IsApiHeader)> includes = request.IncludeFiles.Select(static path => (path, true)).ToList();
		foreach (string packageSpec in request.UsePackages)
			AddAnalysisPackageIncludes(request, includes, packageSpec);
		if (!request.NoStdLib && RequestContainsPackageSourceFile(request, "std"))
		{
			foreach (string stdSource in GetAnalysisPackageSources(request, "std"))
				AddIfMissing(includes, stdSource, isApiHeader: false);
		}
		else if (!request.NoStdLib)
		{
			if (TryGetCachedPackageApiHeader(request, "std", out string? stdApiHeader))
			{
				if (!RequestContainsFile(request, stdApiHeader!))
					AddIfMissing(includes, stdApiHeader!, isApiHeader: true);
			}
			else
			{
				foreach (string stdSource in GetAnalysisPackageSources(request, "std"))
					AddIfMissing(includes, stdSource, isApiHeader: false);
			}
		}
		return includes;
	}

	static void AddAnalysisPackageIncludes(CompilerRequest request, List<(string Path, bool IsApiHeader)> includes, string packageSpec)
	{
		(string packageName, string? requestedVersion) = ParsePackageSpec(packageSpec);
		if (string.IsNullOrWhiteSpace(packageName) || string.Equals(packageName, "std", StringComparison.OrdinalIgnoreCase))
			return;
		if (TryGetCachedExternalPackageApiHeader(request, packageName, requestedVersion, out string? apiHeader))
		{
			AddIfMissing(includes, apiHeader!, isApiHeader: true);
			return;
		}
		foreach (string source in GetAnalysisExternalPackageSources(request, packageName, requestedVersion))
			AddIfMissing(includes, source, isApiHeader: false);
	}

	static bool TryGetCachedPackageApiHeader(CompilerRequest request, string packageName, out string? apiHeader)
	{
		string targetName = string.IsNullOrWhiteSpace(request.TargetName) ? CompilerDefaults.TargetName : request.TargetName;
		string profileName = string.IsNullOrWhiteSpace(request.ProfileName) ? "DEBUG" : request.ProfileName.ToUpperInvariant();
		string targetDirectory = GetTargetVariantDirectoryName(request, targetName);
		foreach (string runtimeRoot in CandidateRuntimeRoots(request.RuntimeRoot))
		{
			foreach (string cacheRoot in CandidateCompilerLibraryCacheRoots(runtimeRoot))
			{
				foreach (string artifactDirectory in CandidateArtifactDirectoryNames(targetDirectory, profileName))
				{
					apiHeader = Path.Combine(cacheRoot, packageName, "bin", artifactDirectory, packageName + "_api.camp");
					if (File.Exists(apiHeader))
						return true;
				}
			}
		}
		apiHeader = null;
		return false;
	}

	static bool TryGetCachedExternalPackageApiHeader(CompilerRequest request, string packageName, string? requestedVersion, out string? apiHeader)
	{
		string targetName = string.IsNullOrWhiteSpace(request.TargetName) ? CompilerDefaults.TargetName : request.TargetName;
		string profileName = string.IsNullOrWhiteSpace(request.ProfileName) ? "DEBUG" : request.ProfileName.ToUpperInvariant();
		string targetDirectory = GetTargetVariantDirectoryName(request, targetName);
		foreach (string artifactRoot in CandidatePackageArtifactRoots(request))
		{
			string packageRoot = Path.Combine(artifactRoot, packageName);
			if (!Directory.Exists(packageRoot))
				continue;
			foreach (string versionDirectory in CandidatePackageVersionDirectories(packageRoot, requestedVersion))
			{
				foreach (string artifactDirectory in CandidateArtifactDirectoryNames(targetDirectory, profileName))
				{
					apiHeader = Path.Combine(versionDirectory, "bin", artifactDirectory, packageName + "_api.camp");
					if (File.Exists(apiHeader))
						return true;
				}
			}
		}
		apiHeader = null;
		return false;
	}

	static IReadOnlyList<string> GetAnalysisPackageSources(CompilerRequest request, string packageName)
	{
		foreach (string runtimeRoot in CandidateRuntimeRoots(request.RuntimeRoot))
		{
			string sourceRoot = Path.Combine(runtimeRoot, "lib", packageName, "src");
			if (Directory.Exists(sourceRoot))
				return Directory.GetFiles(sourceRoot, "*.camp").Order(StringComparer.OrdinalIgnoreCase).ToList();
		}
		return [];
	}

	static IReadOnlyList<string> GetAnalysisExternalPackageSources(CompilerRequest request, string packageName, string? requestedVersion)
	{
		foreach (string sourceRoot in CandidatePackageSourceRoots(request))
		{
			string packageRoot = Path.Combine(sourceRoot, packageName);
			if (requestedVersion is null)
			{
				string unversionedSourceRoot = Path.Combine(packageRoot, "src");
				if (Directory.Exists(unversionedSourceRoot))
					return Directory.GetFiles(unversionedSourceRoot, "*.camp", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList();
			}
			if (!Directory.Exists(packageRoot))
				continue;
			foreach (string versionDirectory in CandidatePackageVersionDirectories(packageRoot, requestedVersion))
			{
				string candidate = Path.Combine(versionDirectory, "src");
				if (Directory.Exists(candidate))
					return Directory.GetFiles(candidate, "*.camp", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList();
			}
		}
		return [];
	}

	static IEnumerable<string> CandidatePackageArtifactRoots(CompilerRequest request)
	{
		string workingDirectory = Path.GetFullPath(request.WorkingDirectory);
		yield return Path.Combine(workingDirectory, "cache", "pkg");
		foreach (string runtimeRoot in CandidateRuntimeRoots(request.RuntimeRoot))
			yield return Path.GetFullPath(Path.Combine(runtimeRoot, "..", "cache", "pkg"));
	}

	static IEnumerable<string> CandidateCompilerLibraryCacheRoots(string runtimeRoot)
	{
		yield return Path.GetFullPath(Path.Combine(runtimeRoot, "..", "cache", "lib"));
		yield return Path.Combine(runtimeRoot, "cache", "lib");
	}

	static IEnumerable<string> CandidatePackageSourceRoots(CompilerRequest request)
	{
		foreach (string root in request.UseSourceRoots)
			yield return Path.GetFullPath(root, request.WorkingDirectory);
		string workingDirectory = Path.GetFullPath(request.WorkingDirectory);
		yield return Path.Combine(workingDirectory, "cache", "pkg");
		foreach (string runtimeRoot in CandidateRuntimeRoots(request.RuntimeRoot))
			yield return Path.GetFullPath(Path.Combine(runtimeRoot, "..", "cache", "pkg"));
	}

	static IEnumerable<string> CandidateArtifactDirectoryNames(string targetDirectory, string profileName)
	{
		yield return targetDirectory + "_static_" + profileName;
		yield return targetDirectory + "_" + profileName;
	}

	static IEnumerable<string> CandidatePackageVersionDirectories(string packageRoot, string? requestedVersion)
	{
		if (requestedVersion is not null)
		{
			string exact = Path.Combine(packageRoot, requestedVersion);
			if (Directory.Exists(exact))
				yield return exact;
			yield break;
		}
		string live = Path.Combine(packageRoot, "live");
		if (Directory.Exists(live))
			yield return live;
		foreach (string directory in Directory.GetDirectories(packageRoot)
			.Where(path => !string.Equals(Path.GetFileName(path), "live", StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
			yield return directory;
	}

	static (string Name, string? Version) ParsePackageSpec(string value)
	{
		string[] parts = value.Split('@', 2);
		return (parts[0], parts.Length == 2 && parts[1].Length > 0 ? parts[1] : null);
	}

	static IEnumerable<string> CandidateRuntimeRoots(string runtimeRoot)
	{
		yield return runtimeRoot;

		DirectoryInfo? directory = new(Path.GetFullPath(runtimeRoot));
		while (directory is not null)
		{
			string candidate = Path.Combine(directory.FullName, "bin");
			if (Directory.Exists(candidate))
				yield return candidate;
			if (File.Exists(Path.Combine(directory.FullName, "src", "camplang.sln")))
			{
				yield return directory.FullName;
				yield break;
			}
			directory = directory.Parent;
		}
	}

	static void AddIfMissing(List<(string Path, bool IsApiHeader)> values, string path, bool isApiHeader)
	{
		string fullPath = Path.GetFullPath(path);
		if (!values.Any(value => string.Equals(Path.GetFullPath(value.Path), fullPath, StringComparison.OrdinalIgnoreCase)))
			values.Add((fullPath, isApiHeader));
	}

	static bool RequestContainsFile(CompilerRequest request, string path)
	{
		string fullPath = Path.GetFullPath(path, request.WorkingDirectory);
		return request.Files.Any(file => string.Equals(Path.GetFullPath(file, request.WorkingDirectory), fullPath, StringComparison.OrdinalIgnoreCase));
	}

	static bool RequestContainsPackageSourceFile(CompilerRequest request, string packageName)
	{
		foreach (string runtimeRoot in CandidateRuntimeRoots(request.RuntimeRoot))
		{
			string sourceRoot = Path.GetFullPath(Path.Combine(runtimeRoot, "lib", packageName, "src"));
			if (!Directory.Exists(sourceRoot))
				continue;
			foreach (string file in request.Files)
			{
				string fullPath = Path.GetFullPath(file, request.WorkingDirectory);
				if (IsUnderDirectory(fullPath, sourceRoot))
					return true;
			}
		}
		return false;
	}

	static bool IsUnderDirectory(string path, string directory)
	{
		string relative = Path.GetRelativePath(directory, path);
		return relative.Length > 0
			&& relative != "."
			&& !relative.StartsWith("..", StringComparison.Ordinal)
			&& !Path.IsPathRooted(relative);
	}

	static void AddSourceFileIfMissing(Compilation compilation, string path, string workingDirectory, Dictionary<string, CampSourceOverlay> overlays, bool isApiHeader, HashSet<string> loadedPaths)
	{
		string fullPath = Path.GetFullPath(path, workingDirectory);
		if (!loadedPaths.Add(fullPath))
			return;
		compilation.Files.Add(LoadSourceFile(fullPath, workingDirectory, overlays, isApiHeader));
	}

	static SourceFile LoadSourceFile(string path, string workingDirectory, Dictionary<string, CampSourceOverlay> overlays, bool isApiHeader)
	{
		string fullPath = Path.GetFullPath(path, workingDirectory);
		string text = overlays.TryGetValue(fullPath, out CampSourceOverlay? overlay)
			? overlay.Text
			: File.ReadAllText(fullPath);
		return new SourceFile
		{
			Path = fullPath,
			Text = text,
			IsApiHeader = isApiHeader,
			WithinAllocationPolicyOverride = ReadWithinAllocationPolicy(text)
		};
	}

	static WithinAllocationPolicy? ReadWithinAllocationPolicy(string text)
	{
		WithinAllocationPolicy? policy = null;
		bool beforeCode = true;
		using StringReader reader = new(text);
		while (reader.ReadLine() is string line)
		{
			string trimmed = line.TrimStart();
			if (trimmed.StartsWith("#within", StringComparison.Ordinal))
			{
				if (!beforeCode)
					continue;
				List<string> parts = CampBuildPragmaReader.Split(trimmed["#within".Length..]);
				if (parts.Count == 1 && parts[0] is "explicit" or "implicit")
					policy = parts[0] == "explicit" ? WithinAllocationPolicy.Explicit : WithinAllocationPolicy.Implicit;
				continue;
			}
			if (CampBuildPragmaReader.IsPreludeTrivia(trimmed) || trimmed.StartsWith("#build", StringComparison.Ordinal))
				continue;
			beforeCode = false;
		}
		return policy;
	}

	static TargetDefinition? LoadTarget(CompilerRequest request)
	{
		foreach (string targetRoot in CandidateTargetRoots(request))
		{
			if (TargetCatalog.TryLoad(targetRoot, out TargetCatalog? catalog, out _) && catalog!.TryGetTarget(request.TargetName, out TargetDefinition? target))
			{
				try
				{
					TargetVariantSelection selection = target!.ResolveVariantSelection(request.Variants);
					return target.WithVariantSelection(selection);
				}
				catch (InvalidDataException)
				{
					return target;
				}
			}
		}
		return null;
	}

	static string GetTargetVariantDirectoryName(CompilerRequest request, string targetName)
	{
		TargetDefinition? target = LoadTarget(request);
		return target?.GetVariantDirectoryName() ?? targetName;
	}

	static IEnumerable<string> CandidateTargetRoots(CompilerRequest request)
	{
		if (!string.IsNullOrWhiteSpace(request.TargetRoot))
		{
			yield return request.TargetRoot!;
			yield break;
		}
		foreach (string runtimeRoot in CandidateRuntimeRoots(request.RuntimeRoot))
		{
			yield return Path.GetFullPath(Path.Combine(runtimeRoot, "targets"));
			yield return Path.GetFullPath(Path.Combine(runtimeRoot, "..", "targets"));
		}
	}

	static IReadOnlyList<CampSourceDiagnostic> CollectDiagnostics(Compilation compilation)
	{
		List<CampSourceDiagnostic> diagnostics = [];
		foreach (SourceFile file in compilation.Files)
		{
			diagnostics.AddRange(file.ParseDiagnostics.Select(diagnostic => ConvertDiagnostic(compilation, file, diagnostic.Range, diagnostic.Message, diagnostic.Code, diagnostic.Severity)));
			diagnostics.AddRange(file.BindDiagnostics.Select(diagnostic => ConvertDiagnostic(compilation, file, diagnostic.Range, diagnostic.Message, diagnostic.Code, diagnostic.Severity)));
		}
		if (compilation.DeclarationExpansion is not null)
			diagnostics.AddRange(compilation.DeclarationExpansion.Diagnostics.Select(diagnostic => ConvertDiagnostic(compilation, null, diagnostic.Range, diagnostic.Message, diagnostic.Code, diagnostic.Severity)));
		if (compilation.Lowering is not null)
			diagnostics.AddRange(compilation.Lowering.Diagnostics.Select(diagnostic => ConvertDiagnostic(compilation, null, diagnostic.Range, diagnostic.Message, diagnostic.Code, diagnostic.Severity)));
		return diagnostics
			.GroupBy(static diagnostic => (
				diagnostic.Path,
				StartLine: diagnostic.Range?.Start.Line ?? -1,
				StartCharacter: diagnostic.Range?.Start.Character ?? -1,
				EndLine: diagnostic.Range?.End.Line ?? -1,
				EndCharacter: diagnostic.Range?.End.Character ?? -1,
				diagnostic.Message,
				diagnostic.Code,
				diagnostic.Severity))
			.Select(static group => group.First())
			.ToList();
	}

	static CampSourceDiagnostic ConvertDiagnostic(Compilation compilation, SourceFile? fallbackFile, TokenRange? range, string message, string? code, DiagnosticSeverity severity)
	{
		SourceFile? file = fallbackFile ?? FindSourceFile(compilation, range);
		return new CampSourceDiagnostic(
			file?.Path ?? "",
			range is null ? null : ToTextRange(range.Value),
			message,
			code,
			severity);
	}

	static SourceFile? FindSourceFile(Compilation compilation, TokenRange? range)
	{
		if (range is not TokenRange tokenRange)
			return null;
		return compilation.Files.FirstOrDefault(file => ReferenceEquals(file.Tokens, tokenRange.Sequence));
	}

	public static CampTextRange ToTextRange(TokenRange range)
	{
		return new CampTextRange(
			new CampTextPosition(Math.Max(0, range.StartLineNumber - 1), Math.Max(0, range.StartColumn - 1)),
			new CampTextPosition(Math.Max(0, range.EndLineNumber - 1), Math.Max(0, range.EndColumn - 1)));
	}
}
