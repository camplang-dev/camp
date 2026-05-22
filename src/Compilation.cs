using System.Collections.Generic;

namespace Camp.Compiler;

public sealed class Compilation
{
	public List<SourceFile> Files { get; } = [];
}

public sealed class SourceFile
{
	public required string Path { get; init; }
	public required string Text { get; init; }
	public TokenSequence? Tokens { get; set; }
	public CompilationUnitSyntax? SyntaxTree { get; set; }
	public Module? BindableTree { get; set; }
	public IReadOnlyList<ParseDiagnostic> ParseDiagnostics { get; set; } = [];
	public IReadOnlyList<BindDiagnostic> BindDiagnostics { get; set; } = [];
	public DeclarationExpansionResult? DeclarationExpansion { get; set; }
	public LoweringResult? Lowering { get; set; }
}

public static class CompilationPipeline
{
	public static void Tokenize(Compilation compilation)
	{
		foreach (SourceFile file in compilation.Files)
			file.Tokens ??= new TokenSequence(CampTokenizer.Tokenize(file.Text));
	}

	public static bool Parse(Compilation compilation)
	{
		Tokenize(compilation);
		bool success = true;
		foreach (SourceFile file in compilation.Files)
		{
			file.SyntaxTree = CampParser.Parse(file.Tokens!, out IReadOnlyList<ParseDiagnostic> diagnostics);
			file.ParseDiagnostics = diagnostics;
			if (diagnostics.Count > 0)
				success = false;
		}
		return success;
	}

	public static bool BuildAst(Compilation compilation)
	{
		bool parseSuccess = Parse(compilation);
		if (!parseSuccess)
			return false;

		bool success = true;
		foreach (SourceFile file in compilation.Files)
		{
			file.BindableTree = BindableNodeBuilder.Build(file.SyntaxTree!, out IReadOnlyList<BindDiagnostic> diagnostics);
			file.BindDiagnostics = diagnostics;
			if (diagnostics.Count > 0)
				success = false;
		}
		return success;
	}

	public static bool ExpandDeclarations(Compilation compilation)
	{
		bool buildSuccess = BuildAst(compilation);
		if (!buildSuccess)
			return false;

		bool success = true;
		foreach (SourceFile file in compilation.Files)
		{
			file.DeclarationExpansion = BindableNodeExpander.Expand(file.BindableTree!);
			file.BindableTree = file.DeclarationExpansion.Module;
			if (file.DeclarationExpansion.Diagnostics.Count > 0)
				success = false;
		}
		return success;
	}

	public static bool Lower(Compilation compilation)
	{
		bool expansionSuccess = ExpandDeclarations(compilation);
		if (!expansionSuccess)
			return false;

		bool success = true;
		foreach (SourceFile file in compilation.Files)
		{
			file.Lowering = BindableNodeLowerer.Lower(file.DeclarationExpansion!);
			file.BindableTree = file.Lowering.Module;
			if (file.Lowering.Diagnostics.Count > 0)
				success = false;
		}
		return success;
	}
}
