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
			MemoryModelName = request.MemoryModelName
		};
		foreach (string define in request.Defines)
			compilation.PreprocessorSymbols.Add(define);

		Dictionary<string, CampSourceOverlay> overlayByPath = overlays.ToDictionary(
			overlay => Path.GetFullPath(overlay.Path, request.WorkingDirectory),
			StringComparer.OrdinalIgnoreCase);

		foreach ((string Path, bool IsApiHeader) include in GetAnalysisIncludeFiles(request))
			compilation.Files.Add(LoadSourceFile(include.Path, request.WorkingDirectory, overlayByPath, include.IsApiHeader));
		foreach (string file in request.Files)
			compilation.Files.Add(LoadSourceFile(file, request.WorkingDirectory, overlayByPath, isApiHeader: false));
		return compilation;
	}

	static IReadOnlyList<(string Path, bool IsApiHeader)> GetAnalysisIncludeFiles(CompilerRequest request)
	{
		List<(string Path, bool IsApiHeader)> includes = request.IncludeFiles.Select(static path => (path, true)).ToList();
		foreach (string packageSpec in request.UsePackages)
			AddAnalysisPackageIncludes(request, includes, packageSpec);
		if (!request.NoStdLib && TryGetCachedPackageApiHeader(request, "std", out string? stdApiHeader))
			AddIfMissing(includes, stdApiHeader!, isApiHeader: true);
		else if (!request.NoStdLib)
		{
			foreach (string stdSource in GetAnalysisPackageSources(request, "std"))
				AddIfMissing(includes, stdSource, isApiHeader: false);
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
		string memoryModelName = string.IsNullOrWhiteSpace(request.MemoryModelName) ? "default" : request.MemoryModelName;
		foreach (string runtimeRoot in CandidateRuntimeRoots(request.RuntimeRoot))
		{
			apiHeader = Path.Combine(runtimeRoot, "lib", packageName, targetName, memoryModelName, profileName, packageName + "_api.camp");
			if (File.Exists(apiHeader))
				return true;
		}
		apiHeader = null;
		return false;
	}

	static bool TryGetCachedExternalPackageApiHeader(CompilerRequest request, string packageName, string? requestedVersion, out string? apiHeader)
	{
		string targetName = string.IsNullOrWhiteSpace(request.TargetName) ? CompilerDefaults.TargetName : request.TargetName;
		string profileName = string.IsNullOrWhiteSpace(request.ProfileName) ? "DEBUG" : request.ProfileName.ToUpperInvariant();
		string memoryModelName = string.IsNullOrWhiteSpace(request.MemoryModelName) ? "default" : request.MemoryModelName;
		foreach (string artifactRoot in CandidatePackageArtifactRoots(request))
		{
			string packageRoot = Path.Combine(artifactRoot, packageName);
			if (!Directory.Exists(packageRoot))
				continue;
			foreach (string versionDirectory in CandidatePackageVersionDirectories(packageRoot, requestedVersion))
			{
				apiHeader = Path.Combine(versionDirectory, targetName, memoryModelName, profileName, packageName + "_api.camp");
				if (File.Exists(apiHeader))
					return true;
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
		yield return Path.Combine(workingDirectory, "bin", "pkg-source");
		yield return Path.Combine(workingDirectory, "bin", "pkg");
		foreach (string runtimeRoot in CandidateRuntimeRoots(request.RuntimeRoot))
			yield return Path.Combine(runtimeRoot, "pkg");
	}

	static IEnumerable<string> CandidatePackageSourceRoots(CompilerRequest request)
	{
		foreach (string root in request.UseSourceRoots)
			yield return Path.GetFullPath(root, request.WorkingDirectory);
		string workingDirectory = Path.GetFullPath(request.WorkingDirectory);
		yield return Path.Combine(workingDirectory, "pkg");
		foreach (string runtimeRoot in CandidateRuntimeRoots(request.RuntimeRoot))
			yield return Path.GetFullPath(Path.Combine(runtimeRoot, "..", "pkg"));
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
			IsApiHeader = isApiHeader
		};
	}

	static TargetDefinition? LoadTarget(CompilerRequest request)
	{
		foreach (string targetRoot in CandidateTargetRoots(request))
		{
			if (TargetCatalog.TryLoad(targetRoot, out TargetCatalog? catalog, out _) && catalog!.TryGetTarget(request.TargetName, out TargetDefinition? target))
				return target;
		}
		return null;
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
