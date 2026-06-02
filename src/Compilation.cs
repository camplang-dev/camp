using System.Collections.Generic;

namespace Camp.Compiler;

public sealed class Compilation
{
	public List<SourceFile> Files { get; } = [];
	public Module? SharedModule { get; set; }
	public DeclarationExpansionResult? DeclarationExpansion { get; set; }
	public LoweringResult? Lowering { get; set; }
	public Dictionary<Definition, SourceFile> DefinitionOwners { get; } = [];
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
		if (success)
			compilation.SharedModule = BuildSharedModule(compilation);
		return success;
	}

	public static bool ExpandDeclarations(Compilation compilation)
	{
		bool buildSuccess = BuildAst(compilation);
		if (!buildSuccess)
			return false;

		compilation.DeclarationExpansion = BindableNodeExpander.Expand(compilation.SharedModule!);
		compilation.SharedModule = compilation.DeclarationExpansion.Module;
		AssignGeneratedDefinitionOwners(compilation);
		return compilation.DeclarationExpansion.Diagnostics.Count == 0;
	}

	public static bool Lower(Compilation compilation)
	{
		bool expansionSuccess = ExpandDeclarations(compilation);
		if (!expansionSuccess)
			return false;

		compilation.Lowering = BindableNodeLowerer.Lower(compilation.DeclarationExpansion!);
		compilation.SharedModule = compilation.Lowering.Module;
		AssignGeneratedDefinitionOwners(compilation);
		return compilation.Lowering.Diagnostics.Count == 0;
	}

	static Module BuildSharedModule(Compilation compilation)
	{
		Module module = new();
		foreach (SourceFile file in compilation.Files)
		{
			if (file.BindableTree is not Module fileModule)
				continue;

			foreach (UsingDeclaration usingDeclaration in fileModule.Usings)
				module.Usings.Add(usingDeclaration);

			module.ExportAs ??= fileModule.ExportAs;

			foreach (Definition definition in fileModule.Definitions)
			{
				module.Definitions.Add(definition);
				compilation.DefinitionOwners[definition] = file;
				module.DefinitionSources[definition] = file.Tokens;
			}
		}

		return module;
	}

	static void AssignGeneratedDefinitionOwners(Compilation compilation)
	{
		if (compilation.SharedModule is null)
			return;

		Dictionary<SourceFile, HashSet<string>> ownedNames = [];
		foreach (SourceFile file in compilation.Files)
		{
			HashSet<string> names = new(System.StringComparer.Ordinal);
			foreach (Definition definition in file.BindableTree?.Definitions ?? [])
				names.Add(definition.Name);
			ownedNames[file] = names;
		}

		foreach (Definition definition in compilation.SharedModule.Definitions)
		{
			if (compilation.DefinitionOwners.ContainsKey(definition))
				continue;

			foreach ((SourceFile file, HashSet<string> names) in ownedNames)
			{
				if (GeneratedNameBelongsToFile(definition.Name, names))
				{
					compilation.DefinitionOwners[definition] = file;
					compilation.SharedModule.DefinitionSources[definition] = file.Tokens;
					break;
				}
			}
		}
	}

	static bool GeneratedNameBelongsToFile(string name, HashSet<string> ownedNames)
	{
		foreach (string ownedName in ownedNames)
		{
			if (string.IsNullOrWhiteSpace(ownedName))
				continue;

			if (name == ownedName
				|| name == "_" + ownedName
				|| name.StartsWith(ownedName + "_", System.StringComparison.Ordinal)
				|| name.StartsWith("_" + ownedName + "__", System.StringComparison.Ordinal))
				return true;
		}

		return false;
	}
}
