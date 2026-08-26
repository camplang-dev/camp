using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public enum WithinAllocationPolicy
{
	Implicit,
	Explicit
}

public sealed class Compilation
{
	public List<SourceFile> Files { get; } = [];
	public Module? SharedModule { get; set; }
	public DeclarationExpansionResult? DeclarationExpansion { get; set; }
	public LoweringResult? Lowering { get; set; }
	public Dictionary<Definition, SourceFile> DefinitionOwners { get; } = [];
	public TargetDefinition? Target { get; set; }
	public string ProfileName { get; set; } = "DEBUG";
	public CompilerCommandMode CommandMode { get; set; } = CompilerCommandMode.Build;
	public DeclarationParticipationMode DeclarationParticipationMode { get; set; } = DeclarationParticipationMode.Production;
	public CoverageInstrumentationMode CoverageInstrumentationMode { get; set; } = CoverageInstrumentationMode.Disabled;
	public SourcefilePathMode SourcefilePathMode { get; set; } = SourcefilePathMode.Relative;
	public string SourcefileDefaultRoot { get; set; } = "";
	public List<string> SourcefileRoots { get; } = [];
	public HashSet<string> PreprocessorSymbols { get; } = new(System.StringComparer.Ordinal);
	public HashSet<string> TargetOwnedPreprocessorSymbols { get; } = new(System.StringComparer.Ordinal);
	public ConfigurationFlagSet ConfigurationFlags { get; set; } = new();
	public WithinAllocationPolicy DefaultWithinAllocationPolicy { get; set; } = WithinAllocationPolicy.Implicit;
}

public sealed class SourceFile
{
	public required string Path { get; init; }
	public string? FullPath { get; init; }
	public required string Text { get; init; }
	public bool IsApiHeader { get; init; }
	public bool IsGeneratedApiHeader { get; init; }
	public bool SharedLibraryImport { get; init; }
	public WithinAllocationPolicy? WithinAllocationPolicyOverride { get; set; }
	public TokenSequence? Tokens { get; set; }
	public IReadOnlyList<ParseDiagnostic> PreprocessDiagnostics { get; set; } = [];
	public CompilationUnitSyntax? SyntaxTree { get; set; }
	public Module? BindableTree { get; set; }
	public List<AttributeConstructor> FileMetadataAttributes { get; } = [];
	public List<SourceRequirement> SourceRequirements { get; } = [];
	public IReadOnlyList<ParseDiagnostic> ParseDiagnostics { get; set; } = [];
	public IReadOnlyList<BindDiagnostic> BindDiagnostics { get; set; } = [];
}

public static class CompilationPipeline
{
	public static void Tokenize(Compilation compilation)
	{
		foreach (SourceFile file in compilation.Files)
		{
			if (file.Tokens is not null)
				continue;
			TokenSequence rawTokens = new(CampTokenizer.Tokenize(file.Text));
			PreprocessResult result = CampPreprocessor.Process(rawTokens, compilation.PreprocessorSymbols, compilation.TargetOwnedPreprocessorSymbols);
			file.Tokens = new TokenSequence(result.Tokens);
			file.PreprocessDiagnostics = result.Diagnostics;
		}
	}

	public static bool Parse(Compilation compilation)
	{
		Tokenize(compilation);
		bool success = true;
		foreach (SourceFile file in compilation.Files)
		{
			if (file.SyntaxTree is not null)
			{
				if (file.ParseDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
					success = false;
				continue;
			}
			file.SyntaxTree = CampParser.Parse(file.Tokens!, out IReadOnlyList<ParseDiagnostic> diagnostics, CampParserOptions.FromTarget(compilation.Target));
			file.ParseDiagnostics = [.. file.PreprocessDiagnostics, .. diagnostics];
			if (file.ParseDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
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
			BindResult bindResult = BindableNodeBuilder.Build(file.SyntaxTree!);
			file.BindableTree = bindResult.Module;
			file.FileMetadataAttributes.Clear();
			file.FileMetadataAttributes.AddRange(bindResult.FileMetadataAttributes);
			file.SourceRequirements.Clear();
			file.SourceRequirements.AddRange(bindResult.SourceRequirements);
			IReadOnlyList<BindDiagnostic> diagnostics = bindResult.Diagnostics;
			IReadOnlyList<BindDiagnostic> fileMetadataDiagnostics = ValidateFileMetadataAttributes(compilation, file);
			IReadOnlyList<BindDiagnostic> docDiagnostics = DocCommentTranslator.Apply(file);
			IReadOnlyList<BindDiagnostic> apiDiagnostics = ApiHeaderValidator.Validate(file);
			file.BindDiagnostics = [.. diagnostics, .. fileMetadataDiagnostics, .. docDiagnostics, .. apiDiagnostics];
			if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
				success = false;
			if (fileMetadataDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
				success = false;
			if (docDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
				success = false;
			if (apiDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
				success = false;
		}
		if (success)
			compilation.SharedModule = BuildSharedModule(compilation);
		return success;
	}

	static IReadOnlyList<BindDiagnostic> ValidateFileMetadataAttributes(Compilation compilation, SourceFile file)
	{
		List<BindDiagnostic> diagnostics = [];
		bool hasCategory = false;
		foreach (AttributeConstructor attribute in file.FileMetadataAttributes)
		{
			if (AttributeNameEquals(attribute.Name, "@require"))
			{
				diagnostics.Add(new BindDiagnostic(GetRange(attribute.SourceSyntax), "@require is no longer supported; use 'requires (CONDITION);' for file-wide requirements."));
				continue;
			}

			if (!AttributeNameEquals(attribute.Name, "@category"))
			{
				diagnostics.Add(new BindDiagnostic(GetRange(attribute.SourceSyntax), $"Attribute '{attribute.Name}' cannot be used as a file metadata attribute."));
				continue;
			}

			if (hasCategory)
				diagnostics.Add(new BindDiagnostic(GetRange(attribute.SourceSyntax), "File already has a default category."));
			hasCategory = true;

			List<ArgumentExpression> positional = attribute.Arguments.Where(static argument => string.IsNullOrWhiteSpace(argument.Name)).ToList();
			if (positional.Count != 1 || attribute.Arguments.Count != 1 || positional[0].Value is not LiteralExpression { Kind: LiteralKind.String })
				diagnostics.Add(new BindDiagnostic(GetRange(attribute.SourceSyntax), "File metadata attribute @category requires one string argument."));
		}
		return diagnostics;
	}

	public static bool ExpandDeclarations(Compilation compilation)
	{
		bool buildSuccess = BuildAst(compilation);
		if (!buildSuccess)
			return false;

		return ExpandDeclarationsFromBuiltAst(compilation);
	}

	public static bool ExpandDeclarationsFromBuiltAst(Compilation compilation)
	{
		compilation.DeclarationExpansion = BindableNodeExpander.Expand(compilation.SharedModule!, compilation.Target, compilation.ConfigurationFlags);
		compilation.SharedModule = compilation.DeclarationExpansion.Module;
		AssignGeneratedDefinitionOwners(compilation);
		return compilation.DeclarationExpansion.Success;
	}

	public static bool Lower(Compilation compilation)
	{
		bool expansionSuccess = ExpandDeclarations(compilation);
		if (!expansionSuccess)
			return false;

		return LowerFromExpandedDeclarations(compilation);
	}

	public static bool LowerFromExpandedDeclarations(Compilation compilation)
	{
		return LowerFromExpandedDeclarations(compilation, measure: null);
	}

	public static bool LowerFromExpandedDeclarations(Compilation compilation, Action<string, Action>? measure)
	{
		compilation.Lowering = BindableNodeLowerer.Lower(compilation.DeclarationExpansion!, measure);
		compilation.SharedModule = compilation.Lowering.Module;
		AssignGeneratedDefinitionOwners(compilation);
		return compilation.Lowering.Success;
	}

	static Module BuildSharedModule(Compilation compilation)
	{
		Module module = new();
		module.SourcefilePathMode = compilation.SourcefilePathMode;
		module.SourcefileDefaultRoot = compilation.SourcefileDefaultRoot;
		module.DeclarationParticipationMode = compilation.DeclarationParticipationMode;
		module.ConfigurationFlags = compilation.ConfigurationFlags;
		module.SourcefileRoots.AddRange(compilation.SourcefileRoots);
		foreach (SourceFile file in compilation.Files)
		{
			if (file.BindableTree is not Module fileModule)
				continue;
			if (file.Tokens is not null)
			{
				module.SourceFiles[file.Tokens] = file;
				module.SourceWithinAllocationPolicies[file.Tokens] = file.WithinAllocationPolicyOverride ?? compilation.DefaultWithinAllocationPolicy;
				module.SourceNamespaces[file.Tokens] = fileModule.Namespace;
			}

			foreach (UsingDeclaration usingDeclaration in fileModule.Usings)
				module.Usings.Add(usingDeclaration);
			foreach (ExportProjectionDefinition projection in fileModule.ExportProjections)
				module.ExportProjections.Add(projection);

			module.Namespace ??= fileModule.Namespace;

			foreach (Definition definition in fileModule.Definitions)
			{
				MarkApiHeaderDefinition(definition, file.IsApiHeader);
				module.Definitions.Add(definition);
				compilation.DefinitionOwners[definition] = file;
				module.DefinitionSources[definition] = file.Tokens;
			}
		}

		return module;
	}

	static void MarkApiHeaderDefinition(Definition definition, bool isApiHeader)
	{
		definition.IsApiHeader = isApiHeader;
		switch (definition)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
					MarkApiHeaderDefinition(field, isApiHeader);
				foreach (FunctionDefinition function in classDefinition.Functions)
					MarkApiHeaderDefinition(function, isApiHeader);
				break;

			case StaticClassDefinition staticClassDefinition:
				foreach (FieldDefinition field in staticClassDefinition.Fields)
					MarkApiHeaderDefinition(field, isApiHeader);
				foreach (FunctionDefinition function in staticClassDefinition.Functions)
					MarkApiHeaderDefinition(function, isApiHeader);
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					MarkApiHeaderDefinition(field, isApiHeader);
				foreach (FunctionDefinition function in structDefinition.Functions)
					MarkApiHeaderDefinition(function, isApiHeader);
				break;

			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					MarkApiHeaderDefinition(function, isApiHeader);
				break;

			case EnumDefinition enumDefinition:
				foreach (VariableDefinition value in enumDefinition.Values)
					MarkApiHeaderDefinition(value, isApiHeader);
				foreach (FunctionDefinition function in enumDefinition.Functions)
					MarkApiHeaderDefinition(function, isApiHeader);
				break;

			case NewtypeDefinition newtypeDefinition:
				foreach (FieldDefinition field in newtypeDefinition.Fields)
					MarkApiHeaderDefinition(field, isApiHeader);
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					MarkApiHeaderDefinition(function, isApiHeader);
				break;

			case ParamsDefinition paramsDefinition:
				foreach (FunctionDefinition function in paramsDefinition.Functions)
					MarkApiHeaderDefinition(function, isApiHeader);
				break;
		}
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
			if (TryAssignGeneratedDefinitionOwnerFromProvenance(compilation, definition))
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

	static bool TryAssignGeneratedDefinitionOwnerFromProvenance(Compilation compilation, Definition definition)
	{
		if (definition.Provenance?.SourceSyntax is not SyntaxNode syntax)
			return false;
		if (!TryGetRange(syntax, out TokenRange range))
			return false;

		foreach (SourceFile file in compilation.Files)
		{
			if (!ReferenceEquals(file.Tokens, range.Sequence))
				continue;
			compilation.DefinitionOwners[definition] = file;
			compilation.SharedModule!.DefinitionSources[definition] = file.Tokens;
			return true;
		}
		return false;
	}

	static bool TryGetRange(SyntaxNode syntax, out TokenRange range)
	{
		return SyntaxNodeTraversal.TryGetRange(syntax, out range);
	}

	static TokenRange? GetRange(SyntaxNode? syntax)
	{
		return syntax is not null && SyntaxNodeTraversal.TryGetRange(syntax, out TokenRange range) ? range : null;
	}

	static bool AttributeNameEquals(string actual, string expected)
	{
		return actual == expected || actual.TrimStart('@') == expected.TrimStart('@');
	}

	static bool GeneratedNameBelongsToFile(string name, HashSet<string> ownedNames)
	{
		foreach (string ownedName in ownedNames)
		{
			if (string.IsNullOrWhiteSpace(ownedName))
				continue;

			if (name == ownedName
				|| name == "_" + ownedName
				|| name.StartsWith(ownedName + "Iter", System.StringComparison.Ordinal)
				|| name.StartsWith(ownedName + "_", System.StringComparison.Ordinal)
				|| name.StartsWith("_" + ownedName + "__", System.StringComparison.Ordinal))
				return true;
		}

		return false;
	}
}
