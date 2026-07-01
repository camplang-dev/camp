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

		foreach (string include in request.IncludeFiles)
			compilation.Files.Add(LoadSourceFile(include, request.WorkingDirectory, overlayByPath, isApiHeader: true));
		foreach (string file in request.Files)
			compilation.Files.Add(LoadSourceFile(file, request.WorkingDirectory, overlayByPath, isApiHeader: false));
		return compilation;
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
		string targetRoot = request.TargetRoot
			?? Path.GetFullPath(Path.Combine(request.RuntimeRoot, "..", "targets"));
		if (!TargetCatalog.TryLoad(targetRoot, out TargetCatalog? catalog, out _))
			return null;
		return catalog!.TryGetTarget(request.TargetName, out TargetDefinition? target) ? target : null;
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
