using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Camp.Compiler;

public enum CampProjectCommandKind
{
	Build,
	Run,
	Dump,
	LanguageService
}

public sealed class CampProjectEnvironment
{
	public required string WorkingDirectory { get; init; }
	public required string RuntimeRoot { get; init; }
	public required string RepositoryRoot { get; init; }
	public string GlobalCampPath => Path.Combine(RepositoryRoot, "lib", "global.camp");
	public string GlobalPackageRoot => Path.Combine(RepositoryRoot, "cache", "pkg");
	public string LocalPackageRoot => Path.Combine(WorkingDirectory, "cache", "pkg");

	public static CampProjectEnvironment Create(string? workingDirectory = null, string? runtimeRoot = null)
	{
		string cwd = Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory());
		string runtime = runtimeRoot ?? AppContext.BaseDirectory;
		return new CampProjectEnvironment
		{
			WorkingDirectory = cwd,
			RuntimeRoot = runtime,
			RepositoryRoot = FindRepositoryRoot(cwd, runtime)
		};
	}

	static string FindRepositoryRoot(string workingDirectory, string runtimeRoot)
	{
		foreach (string start in new[] { workingDirectory, runtimeRoot })
		{
			DirectoryInfo? directory = new(Path.GetFullPath(start));
			while (directory is not null)
			{
				if (File.Exists(Path.Combine(directory.FullName, "src", "camplang.sln")))
					return directory.FullName;
				directory = directory.Parent;
			}
		}
		return Path.GetFullPath(Path.Combine(runtimeRoot, ".."));
	}
}

public sealed class CampProjectLoadResult
{
	public required CompilerRequest Request { get; init; }
	public List<string> ProjectReferences { get; } = [];
	public List<string> ProjectReferenceApiHeaders { get; } = [];
	public List<string> ProjectReferenceSourceFiles { get; } = [];
	public List<string> Diagnostics { get; } = [];
	public bool Success => Diagnostics.Count == 0;
}

public static class CampProjectLoader
{
	public static CampProjectLoadResult Load(IReadOnlyList<string> args, CampProjectEnvironment environment, CampProjectCommandKind command = CampProjectCommandKind.LanguageService)
	{
		return Load(args, environment, command, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
	}

	static CampProjectLoadResult Load(IReadOnlyList<string> args, CampProjectEnvironment environment, CampProjectCommandKind command, HashSet<string> projectReferenceStack)
	{
		List<string> errors = [];
		string? defaultOutDir = command is CampProjectCommandKind.Build or CampProjectCommandKind.Run ? TryGetDefaultOutDirFromBuildFile(args, environment.WorkingDirectory) : null;
		string[] expandedArgs = command is CampProjectCommandKind.Build or CampProjectCommandKind.Run
			? CampResponseFileExpander.ExpandBareBuildFiles(args, environment.WorkingDirectory, errors).ToArray()
			: args.ToArray();
		if (errors.Count == 0)
		{
			ParsedCampBuildOptions cli = CampBuildOptionParser.Parse(expandedArgs, allowPositionals: true, errors);
			if (errors.Count == 0)
				return Load(cli, environment, command, errors, projectReferenceStack, defaultOutDir);
		}

		return Failed(environment, errors);
	}

	public static CampProjectLoadResult LoadBuildFile(string buildFile, CampProjectEnvironment environment, CampProjectCommandKind command = CampProjectCommandKind.LanguageService)
	{
		return LoadBuildFile(buildFile, environment, command, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
	}

	static CampProjectLoadResult LoadBuildFile(string buildFile, CampProjectEnvironment environment, CampProjectCommandKind command, HashSet<string> projectReferenceStack)
	{
		List<string> errors = [];
		string canonical = Path.GetFullPath(buildFile);
		if (projectReferenceStack.Contains(canonical))
		{
			errors.Add("Project reference cycle detected at '" + canonical + "'.");
			return Failed(environment, errors);
		}

		List<string> args = CampResponseFileExpander.Expand(["@" + buildFile], environment.WorkingDirectory, errors);
		if (errors.Count > 0)
			return Failed(environment, errors);
		projectReferenceStack.Add(canonical);
		try
		{
			return Load(args, environment, command, projectReferenceStack);
		}
		finally
		{
			projectReferenceStack.Remove(canonical);
		}
	}

	public static string? FindNearestBuildFile(string fileOrDirectory)
	{
		string start = Directory.Exists(fileOrDirectory) ? fileOrDirectory : Path.GetDirectoryName(Path.GetFullPath(fileOrDirectory)) ?? Directory.GetCurrentDirectory();
		DirectoryInfo? directory = new(start);
		while (directory is not null)
		{
			string preferred = Path.Combine(directory.FullName, directory.Name + ".campbuild");
			if (File.Exists(preferred))
				return preferred;
			string[] candidates = Directory.GetFiles(directory.FullName, "*.campbuild").OrderBy(static path => path, StringComparer.Ordinal).ToArray();
			if (candidates.Length == 1)
				return candidates[0];
			directory = directory.Parent;
		}
		return null;
	}

	static CampProjectLoadResult Load(ParsedCampBuildOptions cli, CampProjectEnvironment environment, CampProjectCommandKind command, List<string> errors, HashSet<string> projectReferenceStack, string? defaultOutDir)
	{
		CampBuildOptionBag bag = new();
		ApplyGlobalPragmas(environment, bag, errors);

		List<string> sourceFiles = ExpandSourcePatterns(cli.Positionals, cli.ExcludePatterns, environment.WorkingDirectory, errors);
		List<string> includeFiles = ExpandSourcePatterns(cli.IncludePatterns.Concat(bag.IncludePatterns).ToList(), [], environment.WorkingDirectory, errors);
		HashSet<string> pragmaFilesRead = new(StringComparer.OrdinalIgnoreCase);
		while (true)
		{
			List<string> filesToRead = sourceFiles.Concat(includeFiles).Where(pragmaFilesRead.Add).ToList();
			if (filesToRead.Count == 0)
				break;
			foreach (string file in filesToRead)
				ApplyFilePragmas(file, environment, bag, CampBuildOptionPrecedence.Local, errors);
			includeFiles = ExpandSourcePatterns(cli.IncludePatterns.Concat(bag.IncludePatterns).ToList(), [], environment.WorkingDirectory, errors);
		}

		bag.Apply(cli, CampBuildOptionPrecedence.CommandLine, "command line", errors);
		sourceFiles = ExpandSourcePatterns(cli.Positionals, bag.ExcludePatterns, environment.WorkingDirectory, errors);
		includeFiles = ExpandSourcePatterns(bag.IncludePatterns, [], environment.WorkingDirectory, errors);
		if (sourceFiles.Count == 0)
			errors.Add("At least one source file pattern is required.");
		if (command == CampProjectCommandKind.Dump && bag.HasBuildOnlyOptions)
			errors.Add("dump does not accept --framework, --artifact, --name, --subsystem, or --out-dir.");
		if (bag.SubsystemName is not null && bag.SubsystemName != "windows")
			errors.Add($"Subsystem '{bag.SubsystemName}' is not valid. Expected windows.");
		if (bag.SubsystemName is not null && bag.ArtifactSpecified && bag.ArtifactKind is not NativeBuildKind.Exec)
			errors.Add("--subsystem can only be used with --artifact exec.");

		CompilerRequest request = new()
		{
			RuntimeRoot = environment.RuntimeRoot,
			WorkingDirectory = environment.WorkingDirectory,
			TargetName = bag.TargetName ?? CompilerDefaults.TargetName,
			ProfileName = bag.ProfileName ?? "DEBUG",
			EmitKind = bag.EmitKind ?? "c99",
			Xml = bag.Xml,
			BuildKind = bag.ArtifactKind,
			InferBuildKind = command == CampProjectCommandKind.Build && !bag.ArtifactSpecified,
			EmitMetadata = bag.MetadataVisibility,
			OutDir = bag.OutDir ?? defaultOutDir,
			ProjectName = bag.ProjectName,
			SubsystemName = bag.SubsystemName,
			NoStdLib = bag.NoStdLib,
			WithinAllocationPolicy = bag.WithinAllocationPolicy
		};
		request.Defines.AddRange(bag.Defines);
		request.Variants.AddRange(bag.Variants);
		request.References.AddRange(bag.References);
		request.Frameworks.AddRange(bag.Frameworks);
		request.UsePackages.AddRange(bag.UsePackages.Select(static package => package.ToString()));
		request.UseSourceRoots.AddRange(bag.UseSources.Where(static source => !string.IsNullOrWhiteSpace(source.Path)).Select(source => Path.GetFullPath(source.Path!, environment.WorkingDirectory)));
		request.Files.AddRange(sourceFiles.Select(path => Path.GetRelativePath(environment.WorkingDirectory, path)));
		request.IncludeFiles.AddRange(includeFiles.Select(path => Path.GetRelativePath(environment.WorkingDirectory, path)));

		CampProjectLoadResult result = new() { Request = request };
		result.Diagnostics.AddRange(errors);
		foreach (string projectReference in bag.ProjectReferences)
		{
			if (TryResolveProjectReference(projectReference, environment.WorkingDirectory, out string? resolved, out string? error))
			{
				result.ProjectReferences.Add(resolved!);
				if (TryFindProjectReferenceApiHeader(resolved!, request, environment.WorkingDirectory, out string? apiHeader))
				{
					result.ProjectReferenceApiHeaders.Add(apiHeader!);
					if (command == CampProjectCommandKind.LanguageService)
						AddUnique(request.IncludeFiles, apiHeader!);
				}
				else if (command == CampProjectCommandKind.LanguageService)
					AddLanguageServiceProjectReferenceSources(result, request, resolved!, projectReferenceStack);
			}
			else
				result.Diagnostics.Add(error!);
		}
		return result;
	}

	static string? TryGetDefaultOutDirFromBuildFile(IReadOnlyList<string> args, string workingDirectory)
	{
		for (int i = 0; i < args.Count; i++)
		{
			string token = args[i];
			if (token.StartsWith("-", StringComparison.Ordinal))
			{
				i += CampResponseFileExpander.OptionValueCountForBuildRequest(token);
				continue;
			}
			string candidate = token.StartsWith("@", StringComparison.Ordinal) ? token[1..] : token;
			string fullPath = Path.GetFullPath(candidate, workingDirectory);
			if (!File.Exists(fullPath) && !Path.HasExtension(fullPath) && File.Exists(fullPath + ".campbuild"))
				fullPath += ".campbuild";
			if (File.Exists(fullPath) && Path.GetExtension(fullPath).Equals(".campbuild", StringComparison.OrdinalIgnoreCase))
				return Path.Combine(Path.GetDirectoryName(fullPath)!, "bin");
		}
		return null;
	}

	static void AddLanguageServiceProjectReferenceSources(CampProjectLoadResult result, CompilerRequest request, string buildFile, HashSet<string> projectReferenceStack)
	{
		string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(buildFile)) ?? request.WorkingDirectory;
		CampProjectLoadResult referenced = LoadBuildFile(
			buildFile,
			CampProjectEnvironment.Create(projectDirectory, request.RuntimeRoot),
			CampProjectCommandKind.LanguageService,
			projectReferenceStack);
		result.Diagnostics.AddRange(referenced.Diagnostics);
		if (!referenced.Success)
			return;

		MergeUniqueStrings(request.Defines, referenced.Request.Defines);
		MergeUniqueStrings(request.UsePackages, referenced.Request.UsePackages);
		MergeUnique(request.UseSourceRoots, referenced.Request.UseSourceRoots);
		foreach (string include in referenced.Request.IncludeFiles)
			AddUnique(request.IncludeFiles, Path.GetFullPath(include, referenced.Request.WorkingDirectory));
		foreach (string source in referenced.Request.AnalysisSourceFiles)
			AddLanguageServiceSource(result, request, Path.GetFullPath(source, referenced.Request.WorkingDirectory));
		foreach (string source in referenced.Request.Files)
			AddLanguageServiceSource(result, request, Path.GetFullPath(source, referenced.Request.WorkingDirectory));
	}

	static void AddLanguageServiceSource(CampProjectLoadResult result, CompilerRequest request, string path)
	{
		AddUnique(result.ProjectReferenceSourceFiles, path);
		AddUnique(request.AnalysisSourceFiles, path);
	}

	static void MergeUnique(List<string> destination, IEnumerable<string> values)
	{
		foreach (string value in values)
			AddUnique(destination, value);
	}

	static void MergeUniqueStrings(List<string> destination, IEnumerable<string> values)
	{
		foreach (string value in values)
		{
			if (!destination.Contains(value, StringComparer.Ordinal))
				destination.Add(value);
		}
	}

	static void AddUnique(List<string> destination, string value)
	{
		if (!destination.Any(existing => string.Equals(Path.GetFullPath(existing), Path.GetFullPath(value), StringComparison.OrdinalIgnoreCase)))
			destination.Add(value);
	}

	static bool TryFindProjectReferenceApiHeader(string buildFile, CompilerRequest consumerRequest, string workingDirectory, out string? apiHeader)
	{
		apiHeader = null;
		string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(buildFile)) ?? workingDirectory;
		string projectName = GetProjectReferenceName(buildFile, workingDirectory) ?? Path.GetFileNameWithoutExtension(buildFile);
		string profileName = string.IsNullOrWhiteSpace(consumerRequest.ProfileName) ? "DEBUG" : consumerRequest.ProfileName.ToUpperInvariant();
		string expected = Path.Combine(projectDirectory, "bin", GetArtifactDirectoryName(consumerRequest, NativeBuildKind.Static, profileName), projectName + "_api.camp");
		if (File.Exists(expected))
		{
			apiHeader = expected;
			return true;
		}

		string apiDirectory = Path.GetDirectoryName(expected)!;
		if (!Directory.Exists(apiDirectory))
			return false;
		string[] candidates = Directory.GetFiles(apiDirectory, "*_api.camp").OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
		if (candidates.Length == 1)
		{
			apiHeader = candidates[0];
			return true;
		}
		return false;
	}

	static string GetArtifactDirectoryName(CompilerRequest request, NativeBuildKind? buildKind, string profileName)
	{
		string targetName = string.IsNullOrWhiteSpace(request.TargetName) ? CompilerDefaults.TargetName : request.TargetName;
		string targetsDirectory = Path.GetFullPath(Path.Combine(request.RuntimeRoot, "..", "targets"));
		if (!TargetCatalog.TryLoad(targetsDirectory, out TargetCatalog? catalog, out _) || !catalog!.TryGetTarget(targetName, out TargetDefinition? target))
			return buildKind is NativeBuildKind.Static ? targetName + "_static_" + profileName : targetName + "_" + profileName;
		try
		{
			TargetVariantSelection selection = target!.ResolveVariantSelection(request.Variants);
			return BuildArtifactLayout.GetArtifactDirectoryName(target.WithVariantSelection(selection), buildKind, profileName);
		}
		catch (InvalidDataException)
		{
			return buildKind is NativeBuildKind.Static ? targetName + "_static_" + profileName : targetName + "_" + profileName;
		}
	}

	static string? GetProjectReferenceName(string buildFile, string workingDirectory)
	{
		List<string> errors = [];
		List<string> args = CampResponseFileExpander.Expand(["@" + buildFile], workingDirectory, errors);
		if (errors.Count > 0)
			return null;
		ParsedCampBuildOptions options = CampBuildOptionParser.Parse(args, allowPositionals: true, errors);
		return errors.Count == 0
			? options.SingleValues.LastOrDefault(static value => value.Key == "name").Value
			: null;
	}

	static CampProjectLoadResult Failed(CampProjectEnvironment environment, List<string> errors)
	{
		CampProjectLoadResult result = new()
		{
			Request = new CompilerRequest
			{
				RuntimeRoot = environment.RuntimeRoot,
				WorkingDirectory = environment.WorkingDirectory
			}
		};
		result.Diagnostics.AddRange(errors);
		return result;
	}

	static void ApplyGlobalPragmas(CampProjectEnvironment environment, CampBuildOptionBag bag, List<string> errors)
	{
		if (File.Exists(environment.GlobalCampPath))
			ApplyFilePragmas(environment.GlobalCampPath, environment, bag, CampBuildOptionPrecedence.Global, errors);
	}

	static void ApplyFilePragmas(string file, CampProjectEnvironment environment, CampBuildOptionBag bag, CampBuildOptionPrecedence precedence, List<string> errors)
	{
		foreach (CampBuildPragmaLine pragma in CampBuildPragmaReader.Read(file, environment.WorkingDirectory, errors))
			bag.Apply(CampBuildOptionParser.Parse(pragma.Tokens, allowPositionals: false, errors), precedence, pragma.SourceName, errors);
	}

	public static List<string> ExpandSourcePatterns(List<string> patterns, List<string> excludePatterns, string workingDirectory, List<string> errors)
	{
		List<string> files = [];
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (string pattern in patterns)
		{
			foreach (string path in CampGlob.Expand(pattern, workingDirectory))
			{
				if (!path.EndsWith(".camp", StringComparison.OrdinalIgnoreCase))
					continue;
				if (excludePatterns.Any(exclude => CampGlob.IsMatch(Path.GetRelativePath(workingDirectory, path), exclude)))
					continue;
				if (seen.Add(path))
					files.Add(path);
			}
		}
		return files.OrderBy(static path => path, StringComparer.Ordinal).ToList();
	}

	public static bool TryResolveProjectReference(string value, string workingDirectory, out string? buildFile, out string? error)
	{
		buildFile = null;
		error = null;
		string fullPath = Path.GetFullPath(value, workingDirectory);
		if (File.Exists(fullPath))
		{
			buildFile = fullPath;
			return true;
		}
		if (!Path.HasExtension(fullPath) && File.Exists(fullPath + ".campbuild"))
		{
			buildFile = fullPath + ".campbuild";
			return true;
		}
		if (Directory.Exists(fullPath))
		{
			string preferred = Path.Combine(fullPath, Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + ".campbuild");
			if (File.Exists(preferred))
			{
				buildFile = preferred;
				return true;
			}
			string[] candidates = Directory.GetFiles(fullPath, "*.campbuild").OrderBy(static path => path, StringComparer.Ordinal).ToArray();
			if (candidates.Length == 1)
			{
				buildFile = candidates[0];
				return true;
			}
			error = candidates.Length == 0
				? $"Project reference '{value}' does not contain a .campbuild file."
				: $"Project reference '{value}' contains multiple .campbuild files. Specify one explicitly.";
			return false;
		}
		error = $"Project reference '{value}' could not be found.";
		return false;
	}
}

sealed class CampBuildOptionBag
{
	readonly Dictionary<string, SingleValue> singleValues = new(StringComparer.Ordinal);
	public List<string> IncludePatterns { get; } = [];
	public List<string> ExcludePatterns { get; } = [];
	public List<string> Defines { get; } = [];
	public List<string> References { get; } = [];
	public List<string> Frameworks { get; } = [];
	public List<string> Variants { get; } = [];
	public List<CampPackageSourceSpec> UseSources { get; } = [];
	public List<CampPackageSpec> UsePackages { get; } = [];
	public List<string> ProjectReferences { get; } = [];
	public bool NoStdLib { get; private set; }
	public bool ArtifactSpecified { get; private set; }
	public NativeBuildKind? ArtifactKind { get; private set; }
	CampBuildOptionPrecedence? artifactPrecedence;
	string? artifactSource;
	CampBuildOptionPrecedence? variantPrecedence;

	public string? TargetName => Get("target");
	public string? ProfileName => Get("profile");
	public string? EmitKind => Get("emit");
	public string? OutDir => Get("out-dir");
	public string? ProjectName => Get("name");
	public string? SubsystemName => Get("subsystem");
	public MetadataVisibility? MetadataVisibility => Get("metadata") is string value ? ParseMetadata(value) : null;
	public WithinAllocationPolicy? WithinAllocationPolicy => Get("within") switch
	{
		"explicit" => Camp.Compiler.WithinAllocationPolicy.Explicit,
		"implicit" => Camp.Compiler.WithinAllocationPolicy.Implicit,
		_ => null
	};
	public bool Xml => Get("xml") == "true";
	public bool HasBuildOnlyOptions => Frameworks.Count > 0 || ProjectReferences.Count > 0 || ArtifactSpecified || Get("name") is not null || Get("subsystem") is not null || Get("out-dir") is not null;

	public void Apply(ParsedCampBuildOptions options, CampBuildOptionPrecedence precedence, string source, List<string> errors)
	{
		foreach ((string key, string value) in options.SingleValues)
			SetSingle(key, value, precedence, source, errors);
		IncludePatterns.AddRange(options.IncludePatterns);
		ExcludePatterns.AddRange(options.ExcludePatterns);
		Defines.AddRange(options.Defines);
		References.AddRange(options.References);
		Frameworks.AddRange(options.Frameworks);
		AddVariants(options.Variants, precedence);
		UseSources.AddRange(options.UseSources);
		UsePackages.AddRange(options.UsePackages);
		ProjectReferences.AddRange(options.ProjectReferences);
		if (options.NoStdLib)
			NoStdLib = true;
		if (options.ArtifactSpecified)
			SetArtifact(options.ArtifactKind, precedence, source, errors);
	}

	void SetArtifact(NativeBuildKind? kind, CampBuildOptionPrecedence precedence, string source, List<string> errors)
	{
		if (artifactPrecedence is CampBuildOptionPrecedence existingPrecedence)
		{
			if (existingPrecedence == precedence && ArtifactKind != kind)
				errors.Add($"{source}: --artifact conflicts with --artifact from {artifactSource}.");
			if (existingPrecedence > precedence)
				return;
		}
		ArtifactSpecified = true;
		ArtifactKind = kind;
		artifactPrecedence = precedence;
		artifactSource = source;
	}

	void SetSingle(string key, string value, CampBuildOptionPrecedence precedence, string source, List<string> errors)
	{
		if (singleValues.TryGetValue(key, out SingleValue existing))
		{
			if (existing.Precedence == precedence && existing.Value != value)
				errors.Add($"{source}: --{key} conflicts with --{key} from {existing.Source}.");
			if (existing.Precedence > precedence)
				return;
		}
		singleValues[key] = new SingleValue(value, precedence, source);
	}

	string? Get(string key) => singleValues.TryGetValue(key, out SingleValue value) ? value.Value : null;

	void AddVariants(IReadOnlyList<string> variants, CampBuildOptionPrecedence precedence)
	{
		if (variants.Count == 0)
			return;
		if (variantPrecedence is CampBuildOptionPrecedence existing && existing < precedence)
			Variants.Clear();
		if (variantPrecedence is null || variantPrecedence <= precedence)
		{
			Variants.AddRange(variants);
			variantPrecedence = precedence;
		}
	}

	static MetadataVisibility? ParseMetadata(string value)
	{
		return value.Trim().ToLowerInvariant() switch
		{
			"none" => Camp.Compiler.MetadataVisibility.None,
			"export" => Camp.Compiler.MetadataVisibility.Export,
			"public" => Camp.Compiler.MetadataVisibility.Public,
			"all" => Camp.Compiler.MetadataVisibility.All,
			_ => null
		};
	}

	readonly record struct SingleValue(string Value, CampBuildOptionPrecedence Precedence, string Source);
}

sealed class ParsedCampBuildOptions
{
	public List<string> Positionals { get; } = [];
	public List<(string Key, string Value)> SingleValues { get; } = [];
	public List<string> IncludePatterns { get; } = [];
	public List<string> ExcludePatterns { get; } = [];
	public List<string> Defines { get; } = [];
	public List<string> References { get; } = [];
	public List<string> Frameworks { get; } = [];
	public List<string> Variants { get; } = [];
	public List<CampPackageSourceSpec> UseSources { get; } = [];
	public List<CampPackageSpec> UsePackages { get; } = [];
	public List<string> ProjectReferences { get; } = [];
	public bool NoStdLib { get; set; }
	public bool ArtifactSpecified { get; set; }
	public NativeBuildKind? ArtifactKind { get; set; }
}

static class CampBuildOptionParser
{
	public static ParsedCampBuildOptions Parse(IReadOnlyList<string> tokens, bool allowPositionals, List<string> errors)
	{
		ParsedCampBuildOptions result = new();
		for (int i = 0; i < tokens.Count; i++)
		{
			string token = tokens[i];
			switch (token)
			{
				case "--target":
				case "-t":
					AddSingle(result, "target", RequiredValue(tokens, ref i, token, errors));
					break;
				case "--profile":
				case "-p":
					AddSingle(result, "profile", RequiredValue(tokens, ref i, token, errors));
					break;
				case "--memory-model":
					errors.Add("--memory-model has been replaced by --variant.");
					i += HasValue(tokens, i) ? 1 : 0;
					break;
				case "--variant":
				case "-v":
					result.Variants.AddRange(RequiredValues(tokens, ref i, token, errors));
					break;
				case "--emit":
					AddSingle(result, "emit", RequiredValue(tokens, ref i, token, errors));
					break;
				case "--metadata":
					string metadata = RequiredValue(tokens, ref i, token, errors);
					if (metadata is not ("none" or "export" or "public" or "all"))
						errors.Add("--metadata expects none, export, public, or all.");
					AddSingle(result, "metadata", metadata);
					break;
				case "--explicit-within":
					AddSingle(result, "within", "explicit");
					break;
				case "--implicit-within":
					AddSingle(result, "within", "implicit");
					break;
				case "--artifact":
					string artifact = RequiredValue(tokens, ref i, token, errors);
					result.ArtifactSpecified = true;
					result.ArtifactKind = artifact switch
					{
						"none" => null,
						"exec" => NativeBuildKind.Exec,
						"static" => NativeBuildKind.Static,
						"shared" => NativeBuildKind.Shared,
						_ => InvalidArtifact("--artifact expects exec, static, shared, or none.", errors)
					};
					break;
				case "--name":
					AddSingle(result, "name", RequiredValue(tokens, ref i, token, errors));
					break;
				case "--subsystem":
					AddSingle(result, "subsystem", RequiredValue(tokens, ref i, token, errors).ToLowerInvariant());
					break;
				case "--out-dir":
					AddSingle(result, "out-dir", CampPathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--build-dir":
					errors.Add("--build-dir has been removed. Build intermediates are written to the output artifact directory's build subdirectory.");
					i += HasValue(tokens, i) ? 1 : 0;
					break;
				case "--include":
				case "-i":
					result.IncludePatterns.Add(CampPathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--exclude":
					result.ExcludePatterns.Add(CampPathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--define":
				case "-d":
					result.Defines.Add(RequiredValue(tokens, ref i, token, errors));
					break;
				case "--reference":
				case "-r":
					result.References.AddRange(RequiredValues(tokens, ref i, token, errors).Select(CampPathArguments.NormalizeIfPathLike));
					break;
				case "--framework":
				case "-f":
					result.Frameworks.AddRange(RequiredValues(tokens, ref i, token, errors));
					break;
				case "--use":
				case "-u":
					result.UsePackages.Add(CampPackageSpec.Parse(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--project-reference":
					result.ProjectReferences.Add(CampPathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--use-source":
					string name = RequiredValue(tokens, ref i, token, errors);
					string? path = null;
					if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-", StringComparison.Ordinal))
						path = CampPathArguments.Normalize(tokens[++i]);
					result.UseSources.Add(new CampPackageSourceSpec(name, path));
					break;
				case "--nostdlib":
					result.NoStdLib = true;
					break;
				case "--xml":
					AddSingle(result, "xml", "true");
					break;
				default:
					if (token.StartsWith("-", StringComparison.Ordinal))
						errors.Add($"Unknown option '{token}'.");
					else if (allowPositionals)
						result.Positionals.Add(CampPathArguments.Normalize(token));
					else
						errors.Add($"Unexpected build pragma argument '{token}'.");
					break;
			}
		}
		return result;
	}

	static void AddSingle(ParsedCampBuildOptions options, string key, string value)
	{
		if (!string.IsNullOrEmpty(value))
			options.SingleValues.Add((key, value));
	}

	static bool HasValue(IReadOnlyList<string> tokens, int index)
	{
		return index + 1 < tokens.Count && !tokens[index + 1].StartsWith("-", StringComparison.Ordinal);
	}

	static List<string> RequiredValues(IReadOnlyList<string> tokens, ref int index, string option, List<string> errors)
	{
		List<string> values = [];
		while (index + 1 < tokens.Count && !tokens[index + 1].StartsWith("-", StringComparison.Ordinal))
			values.Add(tokens[++index]);
		if (values.Count == 0)
			errors.Add($"{option} requires at least one value.");
		return values;
	}

	static string RequiredValue(IReadOnlyList<string> tokens, ref int index, string option, List<string> errors)
	{
		if (index + 1 >= tokens.Count || tokens[index + 1].StartsWith("-", StringComparison.Ordinal))
		{
			errors.Add($"{option} requires a value.");
			return "";
		}
		return tokens[++index];
	}

	static NativeBuildKind? InvalidArtifact(string message, List<string> errors)
	{
		errors.Add(message);
		return null;
	}
}

static class CampPathArguments
{
	public static string Normalize(string value) => OperatingSystem.IsWindows() ? value.Replace('/', Path.DirectorySeparatorChar) : value;

	public static string NormalizeIfPathLike(string value) => LooksLikePath(value) ? Normalize(value) : value;

	public static bool LooksLikePath(string value)
	{
		return value.Contains("/", StringComparison.Ordinal)
			|| value.Contains("\\", StringComparison.Ordinal)
			|| value.Contains("*", StringComparison.Ordinal)
			|| value.Contains("?", StringComparison.Ordinal)
			|| value.StartsWith(".", StringComparison.Ordinal)
			|| value.EndsWith(".camp", StringComparison.OrdinalIgnoreCase)
			|| value.EndsWith(".campbuild", StringComparison.OrdinalIgnoreCase);
	}
}

static class CampBuildPragmaReader
{
	public static IEnumerable<CampBuildPragmaLine> Read(string file, string workingDirectory, List<string> errors)
	{
		string fullPath = Path.GetFullPath(file, workingDirectory);
		if (!File.Exists(fullPath))
			yield break;

		bool beforeCode = true;
		int lineNumber = 0;
		foreach (string line in File.ReadLines(fullPath))
		{
			lineNumber++;
			string trimmed = line.TrimStart();
			if (trimmed.StartsWith("#build", StringComparison.Ordinal))
			{
				if (!beforeCode)
				{
					errors.Add($"{Path.GetRelativePath(workingDirectory, fullPath)}({lineNumber},1): error: #build pragmas must appear in the file prelude before any non-comment token.");
					continue;
				}
				yield return new CampBuildPragmaLine(Split(trimmed["#build".Length..]), $"{Path.GetRelativePath(workingDirectory, fullPath)}:{lineNumber}");
				continue;
			}
			if (trimmed.StartsWith("#within", StringComparison.Ordinal))
				continue;
			if (IsPreludeTrivia(trimmed))
				continue;
			beforeCode = false;
		}
	}

	public static bool IsPreludeTrivia(string trimmed)
	{
		return trimmed.Length == 0
			|| trimmed.StartsWith("//", StringComparison.Ordinal)
			|| trimmed.StartsWith("/*", StringComparison.Ordinal)
			|| trimmed.StartsWith("*", StringComparison.Ordinal)
			|| trimmed.StartsWith("*/", StringComparison.Ordinal);
	}

	public static List<string> Split(string text)
	{
		List<string> tokens = [];
		StringBuilder current = new();
		bool inQuote = false;
		for (int i = 0; i < text.Length; i++)
		{
			char ch = text[i];
			if (inQuote)
			{
				if (ch == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\')
					current.Append(text[++i]);
				else if (ch == '"')
					inQuote = false;
				else
					current.Append(ch);
				continue;
			}
			if (char.IsWhiteSpace(ch))
			{
				if (current.Length > 0)
				{
					tokens.Add(current.ToString());
					current.Clear();
				}
			}
			else if (ch == '"')
				inQuote = true;
			else
				current.Append(ch);
		}
		if (current.Length > 0)
			tokens.Add(current.ToString());
		return tokens;
	}
}

public static class CampResponseFileExpander
{
	static readonly HashSet<string> PathValueOptions = new(StringComparer.Ordinal)
	{
		"--include",
		"-i",
		"--exclude",
		"--out-dir",
		"--build-dir",
		"--local"
	};

	public static List<string> Expand(IReadOnlyList<string> args, string workingDirectory, List<string> errors)
	{
		return Expand(args, workingDirectory, errors, []);
	}

	public static int OptionValueCountForBuildRequest(string option) => OptionValueCount(option);

	public static List<string> ExpandBareBuildFiles(IReadOnlyList<string> args, string workingDirectory, List<string> errors)
	{
		List<string> expanded = [];
		for (int i = 0; i < args.Count; i++)
		{
			string arg = args[i];
			expanded.AddRange(IsBareBuildFileArgument(args, i, workingDirectory)
				? Expand(["@" + arg], workingDirectory, errors)
				: [arg]);
		}
		return expanded;
	}

	static bool IsBareBuildFileArgument(IReadOnlyList<string> args, int index, string workingDirectory)
	{
		string arg = args[index];
		if (arg.StartsWith("-", StringComparison.Ordinal) || IsOptionValue(args, index))
			return false;
		string responseFile = ResolveResponseFile(arg, workingDirectory);
		return File.Exists(responseFile) && Path.GetExtension(responseFile).Equals(".campbuild", StringComparison.OrdinalIgnoreCase);
	}

	static bool IsOptionValue(IReadOnlyList<string> args, int index)
	{
		for (int i = index - 1; i >= 0; i--)
		{
			string token = args[i];
			if (!token.StartsWith("-", StringComparison.Ordinal))
				continue;
			return OptionValueCount(token) != 0 && index <= i + OptionValueCount(token);
		}
		return false;
	}

	static int OptionValueCount(string option)
	{
		return option is "--target" or "-t" or "--profile" or "-p" or "--variant" or "-v" or "--memory-model" or "--emit" or "--metadata" or "--artifact" or "--name" or "--subsystem" or "--out-dir" or "--build-dir" or "--include" or "-i" or "--exclude" or "--define" or "-d" or "--reference" or "-r" or "--framework" or "-f" or "--use" or "-u" or "--project-reference" or "--local"
			? 1
			: option == "--use-source" ? 2 : 0;
	}

	static List<string> Expand(IReadOnlyList<string> args, string workingDirectory, List<string> errors, HashSet<string> responseStack)
	{
		List<string> expanded = [];
		for (int i = 0; i < args.Count; i++)
		{
			string arg = args[i];
			if (!arg.StartsWith("@", StringComparison.Ordinal) || arg == "@")
			{
				expanded.Add(arg);
				continue;
			}

			string responseFile = ResolveResponseFile(arg[1..], workingDirectory);
			string canonical = Path.GetFullPath(responseFile);
			if (!responseStack.Add(canonical))
			{
				errors.Add($"Response file cycle detected at '{responseFile}'.");
				continue;
			}
			List<string> tokens = TokenizeResponseFile(responseFile, errors);
			string responseDirectory = Path.GetDirectoryName(canonical) ?? workingDirectory;
			expanded.AddRange(RebasePathArguments(Expand(tokens, responseDirectory, errors, responseStack), responseDirectory));
			responseStack.Remove(canonical);
		}
		return expanded;
	}

	static string ResolveResponseFile(string value, string workingDirectory)
	{
		string fullPath = Path.GetFullPath(value, workingDirectory);
		if (File.Exists(fullPath))
			return fullPath;
		return !Path.HasExtension(fullPath) && File.Exists(fullPath + ".campbuild") ? fullPath + ".campbuild" : fullPath;
	}

	static List<string> TokenizeResponseFile(string file, List<string> errors)
	{
		if (!File.Exists(file))
		{
			errors.Add($"Response file '{file}' could not be found.");
			return [];
		}
		return CampBuildPragmaReader.Split(File.ReadAllText(file));
	}

	static List<string> RebasePathArguments(IReadOnlyList<string> tokens, string baseDirectory)
	{
		List<string> result = [];
		for (int i = 0; i < tokens.Count; i++)
		{
			string token = tokens[i];
			result.Add(token);
			if (token.StartsWith("-", StringComparison.Ordinal))
			{
				int count = OptionValueCount(token);
				for (int value = 0; value < count && i + 1 < tokens.Count; value++)
				{
					string next = tokens[++i];
					result.Add(PathValueOptions.Contains(token) ? RebasePathValue(next, baseDirectory) : next);
				}
				continue;
			}
			result[^1] = RebaseSourcePattern(token, baseDirectory);
		}
		return result;
	}

	static string RebaseSourcePattern(string value, string baseDirectory)
	{
		return Path.IsPathRooted(value) || !CampPathArguments.LooksLikePath(value) ? value : Path.GetFullPath(value, baseDirectory);
	}

	static string RebasePathValue(string value, string baseDirectory)
	{
		if (IsDirectOutputPath(value))
		{
			string prefix = value[..^1];
			string rebased = Path.IsPathRooted(prefix) ? prefix : Path.GetFullPath(prefix, baseDirectory);
			return Path.Combine(rebased, ".");
		}
		return Path.IsPathRooted(value) ? value : Path.GetFullPath(value, baseDirectory);
	}

	static bool IsDirectOutputPath(string value)
	{
		string normalized = value.Replace('\\', '/');
		return normalized == "." || normalized.EndsWith("/.", StringComparison.Ordinal);
	}
}

public static class CampGlob
{
	public static IEnumerable<string> Expand(string pattern, string workingDirectory)
	{
		string fullPattern = Path.GetFullPath(pattern, workingDirectory);
		if (!HasWildcards(fullPattern))
			return File.Exists(fullPattern) ? [fullPattern] : [];
		string root = GetSearchRoot(fullPattern);
		if (!Directory.Exists(root))
			return [];
		Regex regex = new("^" + GlobRegex(Normalize(fullPattern)) + "$", RegexOptions.CultureInvariant);
		return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
			.Where(path => regex.IsMatch(Normalize(path)));
	}

	public static bool IsMatch(string path, string pattern)
	{
		return Regex.IsMatch(Normalize(path), "^" + GlobRegex(Normalize(pattern)) + "$", RegexOptions.CultureInvariant);
	}

	static bool HasWildcards(string pattern) => pattern.IndexOfAny(['*', '?', '[']) >= 0;

	static string GetSearchRoot(string fullPattern)
	{
		int wildcard = fullPattern.IndexOfAny(['*', '?', '[']);
		string prefix = wildcard < 0 ? fullPattern : fullPattern[..wildcard];
		string? root = Path.GetDirectoryName(prefix);
		return string.IsNullOrWhiteSpace(root) ? Directory.GetCurrentDirectory() : root;
	}

	static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

	static string GlobRegex(string pattern)
	{
		StringBuilder regex = new();
		foreach (char ch in pattern)
		{
			regex.Append(ch switch
			{
				'*' => ".*",
				'?' => ".",
				'.' => "\\.",
				'\\' => "/",
				'/' => "/",
				_ => Regex.Escape(ch.ToString())
			});
		}
		return regex.ToString();
	}
}

sealed record CampBuildPragmaLine(IReadOnlyList<string> Tokens, string SourceName);

public sealed record CampPackageSourceSpec(string Name, string? Path);

public sealed record CampPackageSpec(string Name, string? Version)
{
	public static CampPackageSpec Parse(string value)
	{
		string[] parts = value.Split('@', 2);
		return new CampPackageSpec(parts[0], parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null);
	}

	public override string ToString() => Version is null ? Name : $"{Name}@{Version}";
}

sealed record CampSemVersion(int Major, int Minor, int Patch, string? Suffix) : IComparable<CampSemVersion>
{
	public static readonly IComparer<string> Comparer = Comparer<string>.Create((left, right) => Parse(left).CompareTo(Parse(right)));

	public static CampSemVersion Parse(string value)
	{
		string[] suffixSplit = value.Split('-', 2);
		string[] parts = suffixSplit[0].Split('.');
		return new CampSemVersion(ParsePart(parts, 0), ParsePart(parts, 1), ParsePart(parts, 2), suffixSplit.Length > 1 ? suffixSplit[1] : null);
	}

	static int ParsePart(string[] parts, int index) => index < parts.Length && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;

	public int CompareTo(CampSemVersion? other)
	{
		if (other is null)
			return 1;
		int major = Major.CompareTo(other.Major);
		if (major != 0)
			return major;
		int minor = Minor.CompareTo(other.Minor);
		if (minor != 0)
			return minor;
		int patch = Patch.CompareTo(other.Patch);
		if (patch != 0)
			return patch;
		return string.Compare(Suffix, other.Suffix, StringComparison.Ordinal);
	}
}

enum CampBuildOptionPrecedence
{
	Global = 0,
	Local = 1,
	CommandLine = 2
}
