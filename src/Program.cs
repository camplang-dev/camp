using System;
using System.Collections;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Camp.Compiler;

Argument<List<string>> filesArgument = new("files")
{
	Description = "One or more source files to read, or '-' to read from standard input.",
	Arity = ArgumentArity.OneOrMore
};

Option<InspectMode?> inspectOption = new("--inspect")
{
	Description = "Print an intermediate compiler representation."
};

Option<bool> xmlOption = new("--xml")
{
	Description = "Print the rewritten bindable tree as XML when used with --inspect declarations or --inspect lowering."
};

Option<List<string>> includeOption = new("--include", "-i")
{
	Description = "Include one or more Camp API header files in the compilation.",
	Arity = ArgumentArity.ZeroOrMore,
	AllowMultipleArgumentsPerToken = true
};

Option<bool> inspectApiOption = new("--inspect-api")
{
	Description = "Print merged exported API declarations for the non-header input files as Camp code."
};

Option<string> targetOption = new("--target", "-t")
{
	Description = "Select the target to use.",
	DefaultValueFactory = _ => "clang-macos-x64"
};

Option<string> profileOption = new("--profile", "-p")
{
	Description = "Select the build profile to use: DEBUG or RELEASE.",
	DefaultValueFactory = _ => "DEBUG"
};

Option<string?> memoryModelOption = new("--memory-model")
{
	Description = "Select the target memory model to use when required by the target."
};

Option<List<string>> defineOption = new("--define", "-d")
{
	Description = "Define one or more conditional compilation symbols.",
	Arity = ArgumentArity.ZeroOrMore,
	AllowMultipleArgumentsPerToken = true
};

Option<string> emitOption = new("--emit")
{
	Description = "Select the output emitter to use. Defaults to c99.",
	DefaultValueFactory = _ => "c99"
};

Option<string?> buildOption = new("--build", "-b")
{
	Description = "Build a native artifact: exec, static, or shared."
};

Option<string?> outDirOption = new("--out-dir")
{
	Description = "Write native output artifacts to this directory."
};

Option<string?> buildDirOption = new("--build-dir")
{
	Description = "Write generated C and intermediate build files to this directory."
};

Option<bool> noStdLibOption = new("--nostdlib")
{
	Description = "Do not include the default standard library package."
};

RootCommand rootCommand = new("Camp compiler")
{
	Description = """
	Usage:
	  campc <files...> [options]

	Arguments:
	  <files...>  One or more source files to read, or '-' to read from standard input.

	Options:
	  -i, --include <files...>  Include API header files in the compilation.
	  -t, --target <name>      Select the target to use. Defaults to clang-macos-x64.
	  -p, --profile <name>     Select DEBUG or RELEASE. Defaults to DEBUG.
	  --memory-model <name>    Select a target memory model when the target requires one.
	  -d, --define <symbols...> Define conditional compilation symbols.
	  --emit c99              Generate C99 output. Defaults to c99.
	  -b, --build <kind>      Build a native artifact: exec, static, or shared.
	  --out-dir <dir>         Write native output artifacts to this directory.
	  --build-dir <dir>       Write generated C and intermediate build files to this directory.
	  --nostdlib              Do not include the default std package.
	  --inspect tokens        Print one token per line.
	  --inspect cst           Parse and print the syntax tree as XML.
	  --inspect ast           Parse and print the bindable tree as XML.
	  --inspect declarations  Analyze declarations and print the bindable tree as Camp code.
	  --inspect lowering      Analyze, lower, and print the bindable tree as Camp code.
	  --inspect-api           Print merged exported API declarations for compiled files.
	  --xml                   Print XML for declarations/lowering inspection.
	"""
};
rootCommand.Arguments.Add(filesArgument);
rootCommand.Options.Add(inspectOption);
rootCommand.Options.Add(xmlOption);
rootCommand.Options.Add(includeOption);
rootCommand.Options.Add(inspectApiOption);
rootCommand.Options.Add(targetOption);
rootCommand.Options.Add(profileOption);
rootCommand.Options.Add(memoryModelOption);
rootCommand.Options.Add(defineOption);
rootCommand.Options.Add(emitOption);
rootCommand.Options.Add(buildOption);
rootCommand.Options.Add(outDirOption);
rootCommand.Options.Add(buildDirOption);
rootCommand.Options.Add(noStdLibOption);
rootCommand.SetAction(parseResult =>
{
	List<string>? filenames = parseResult.GetValue(filesArgument);
	List<string>? includeFilenames = parseResult.GetValue(includeOption);
	InspectMode? inspect = parseResult.GetValue(inspectOption);
	bool inspectApi = parseResult.GetValue(inspectApiOption);
	bool printXml = parseResult.GetValue(xmlOption);
	string targetName = parseResult.GetValue(targetOption) ?? "clang-macos-x64";
	string profileName = parseResult.GetValue(profileOption) ?? "DEBUG";
	string? memoryModelName = parseResult.GetValue(memoryModelOption);
	List<string>? defineNames = parseResult.GetValue(defineOption);
	string emitKind = parseResult.GetValue(emitOption) ?? "c99";
	string? buildKind = parseResult.GetValue(buildOption);
	string? outDir = parseResult.GetValue(outDirOption);
	string? buildDir = parseResult.GetValue(buildDirOption);
	bool noStdLib = parseResult.GetValue(noStdLibOption);

	return Run(filenames, includeFilenames, inspect, inspectApi, printXml, targetName, profileName, memoryModelName, defineNames, emitKind, buildKind, outDir, buildDir, noStdLib);
});

return rootCommand.Parse(args).Invoke();

static int Run(List<string>? filenames, List<string>? includeFilenames, InspectMode? inspect, bool inspectApi, bool printXml, string targetName, string profileName, string? memoryModelName, List<string>? defineNames, string emitKind, string? buildKind, string? outDir, string? buildDir, bool noStdLib)
{
	if (filenames is null || filenames.Count == 0)
	{
		Console.Error.WriteLine("At least one filename is required.");
		return 1;
	}

	includeFilenames ??= [];
	if (filenames.Count > 1 && filenames.Contains("-") || includeFilenames.Count > 0 && filenames.Contains("-"))
	{
		Console.Error.WriteLine("Standard input may only be used by itself and cannot be combined with API headers.");
		return 1;
	}

	if (includeFilenames.Contains("-"))
	{
		Console.Error.WriteLine("API headers must be read from files, not standard input.");
		return 1;
	}

	if (printXml && inspect is not (InspectMode.Declarations or InspectMode.Lowering))
	{
		Console.Error.WriteLine("--xml can only be used with --inspect declarations or --inspect lowering.");
		return 1;
	}

	if (inspectApi && (inspect is not null || printXml))
	{
		Console.Error.WriteLine("--inspect-api cannot be combined with --inspect or --xml.");
		return 1;
	}

	if (!TryParseNativeBuildKind(buildKind, out NativeBuildKind? nativeBuildKind))
		return 1;

	if (nativeBuildKind is not null && inspect is not null)
	{
		Console.Error.WriteLine("--build cannot be combined with --inspect.");
		return 1;
	}

	if (nativeBuildKind is not null && inspectApi)
	{
		Console.Error.WriteLine("--build cannot be combined with --inspect-api.");
		return 1;
	}

	defineNames ??= [];
	if (!TryCreateRuntimeContext(targetName, profileName, memoryModelName, defineNames, out RuntimeContext? context))
		return 1;

	List<string> packageApiHeaders = [];
	List<string> packageLibraries = [];
	string? stdApiHeader = null;
	string? stdLibrary = null;
	if (!noStdLib && !TryPreparePackage(context!, "std", nativeBuildKind is not null, out stdApiHeader, out stdLibrary))
		return 1;
	if (!noStdLib && stdApiHeader is not null)
		packageApiHeaders.Add(stdApiHeader);
	if (!noStdLib && stdLibrary is not null)
		packageLibraries.Add(stdLibrary);

	List<string> allIncludeFilenames = [.. packageApiHeaders, .. includeFilenames];
	if (!TryLoadCompilation(filenames, allIncludeFilenames, context!, out Compilation compilation))
		return 1;

	if (inspectApi)
		return PrintApi(compilation);

	inspect ??= InspectMode.None;
	return inspect switch
	{
		InspectMode.None => EmitDefaultOutput(compilation, emitKind, nativeBuildKind, outDir, buildDir, packageLibraries),
		InspectMode.Tokens => PrintTokens(compilation),
		InspectMode.Cst => PrintSyntaxXml(compilation),
		InspectMode.Ast => PrintBindXml(compilation),
		InspectMode.Declarations => PrintDeclarations(compilation, printXml),
		InspectMode.Lowering => PrintLowering(compilation, printXml),
		_ => 1
	};
}

static bool TryParseNativeBuildKind(string? value, out NativeBuildKind? kind)
{
	kind = null;
	if (string.IsNullOrWhiteSpace(value))
		return true;
	switch (value.Trim().ToLowerInvariant())
	{
		case "exec":
			kind = NativeBuildKind.Exec;
			return true;
		case "static":
			kind = NativeBuildKind.Static;
			return true;
		case "shared":
			kind = NativeBuildKind.Shared;
			return true;
		default:
			Console.Error.WriteLine($"Build kind '{value}' is not valid. Expected exec, static, or shared.");
			return false;
	}
}

static bool TryLoadCompilation(List<string> filenames, List<string> includeFilenames, RuntimeContext context, out Compilation compilation)
{
	compilation = new Compilation { Target = context.Target, ProfileName = context.ProfileName, MemoryModelName = context.MemoryModelName };
	AddPreprocessorSymbols(compilation, context);
	foreach (string filename in filenames)
	{
		if (!TryReadInput(filename, out string text))
			return false;
		compilation.Files.Add(new SourceFile { Path = filename, Text = text });
	}
	foreach (string filename in includeFilenames)
	{
		if (!TryReadInput(filename, out string text))
			return false;
		compilation.Files.Add(new SourceFile { Path = filename, Text = text, IsApiHeader = true });
	}
	return true;
}

static void AddPreprocessorSymbols(Compilation compilation, RuntimeContext context)
{
	compilation.PreprocessorSymbols.Add("TRUE");
	compilation.PreprocessorSymbols.Add(context.ProfileName);
	foreach (string symbol in context.Target.Defines.Keys)
		compilation.PreprocessorSymbols.Add(symbol);
	foreach (string symbol in context.CommandLineDefines)
	{
		if (!string.IsNullOrWhiteSpace(symbol))
			compilation.PreprocessorSymbols.Add(symbol);
	}
}

static bool TryReadInput(string filename, out string text)
{
	try
	{
		text = filename == "-" ? Console.In.ReadToEnd() : File.ReadAllText(filename);
		return true;
	}
	catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
	{
		Console.Error.WriteLine($"{filename}: {ex.Message}");
		text = "";
		return false;
	}
}

static bool TryCreateRuntimeContext(string targetName, string profileName, string? memoryModelName, IReadOnlyList<string> defineNames, out RuntimeContext? context)
{
	context = null;
	string executableDirectory = AppContext.BaseDirectory;
	string normalizedProfile = profileName.ToUpperInvariant();
	if (normalizedProfile is not "DEBUG" and not "RELEASE")
	{
		Console.Error.WriteLine($"Profile '{profileName}' is not valid. Expected DEBUG or RELEASE.");
		return false;
	}

	if (!TargetCatalog.TryLoad(Path.Combine(executableDirectory, "targets"), out TargetCatalog? catalog, out string? error))
	{
		Console.Error.WriteLine(error);
		return false;
	}

	if (!catalog!.TryGetTarget(targetName, out TargetDefinition? target))
	{
		Console.Error.WriteLine($"Target '{targetName}' could not be found in '{Path.Combine(executableDirectory, "targets")}'.");
		return false;
	}

	if (target!.MemoryModels.Count > 0)
	{
		if (string.IsNullOrWhiteSpace(memoryModelName))
		{
			Console.Error.WriteLine($"Target '{target.Name}' requires --memory-model. Available memory models: {string.Join(", ", target.MemoryModels.Keys)}.");
			return false;
		}
		if (!target.MemoryModels.ContainsKey(memoryModelName))
		{
			Console.Error.WriteLine($"Memory model '{memoryModelName}' is not defined by target '{target.Name}'. Available memory models: {string.Join(", ", target.MemoryModels.Keys)}.");
			return false;
		}
	}
	else if (!string.IsNullOrWhiteSpace(memoryModelName))
	{
		Console.Error.WriteLine($"Target '{target.Name}' does not define memory models, so --memory-model cannot be used.");
		return false;
	}

	context = new RuntimeContext(executableDirectory, target, normalizedProfile, string.IsNullOrWhiteSpace(memoryModelName) ? null : memoryModelName, [.. defineNames]);
	return true;
}

static bool TryPreparePackage(RuntimeContext context, string packageName, bool requireNativeLibrary, out string? apiHeaderPath, out string? libraryPath)
{
	apiHeaderPath = null;
	libraryPath = null;
	string packageDirectory = Path.Combine(context.ExecutableDirectory, "lib", packageName);
	string sourceDirectory = Path.Combine(packageDirectory, "src");
	if (!Directory.Exists(sourceDirectory))
	{
		Console.Error.WriteLine($"Package '{packageName}' source directory '{sourceDirectory}' could not be found.");
		return false;
	}

	string[] sourceFiles = Directory.GetFiles(sourceDirectory, "*.camp", SearchOption.AllDirectories)
		.OrderBy(static x => x, StringComparer.Ordinal)
		.ToArray();
	if (sourceFiles.Length == 0)
	{
		Console.Error.WriteLine($"Package '{packageName}' source directory '{sourceDirectory}' does not contain any .camp files.");
		return false;
	}

	string packageBinDirectory = Path.Combine(packageDirectory, "bin", context.Target.Name, context.MemoryModelName ?? "default", context.ProfileName);
	string apiPath = Path.Combine(packageBinDirectory, packageName + "_api.camp");
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
	bool apiCurrent = canUseCache && IsOutputCacheCurrent(apiPath, sourceFiles);
	bool libraryCurrent = !requireNativeLibrary || canUseCache && IsOutputCacheCurrent(staticLibraryPath, sourceFiles);
	if (apiCurrent && libraryCurrent)
	{
		apiHeaderPath = apiPath;
		libraryPath = requireNativeLibrary ? staticLibraryPath : null;
		return true;
	}

	if (!TryBuildPackage(packageName, sourceFiles, apiPath, requireNativeLibrary ? staticLibraryPath : null, context))
		return false;

	apiHeaderPath = apiPath;
	libraryPath = requireNativeLibrary ? staticLibraryPath : null;
	return true;
}

static bool IsOutputCacheCurrent(string outputPath, IReadOnlyList<string> sourceFiles)
{
	if (!File.Exists(outputPath))
		return false;

	DateTime apiTime = File.GetLastWriteTimeUtc(outputPath);
	foreach (string sourceFile in sourceFiles)
	{
		if (apiTime <= File.GetLastWriteTimeUtc(sourceFile))
			return false;
	}

	return true;
}

static bool TryBuildPackage(string packageName, IReadOnlyList<string> sourceFiles, string apiPath, string? staticLibraryPath, RuntimeContext context)
{
	if (!TryLoadCompilation([.. sourceFiles], [], context, out Compilation packageCompilation))
		return false;

	if (!BuildAllAndReport(packageCompilation))
		return false;

	AnalysisResult analysis = BindableNodeAnalyzer.Analyze(packageCompilation.SharedModule!, packageCompilation.Target, packageCompilation.MemoryModelName);
	if (!PrintAnalysisDiagnostics(packageCompilation, analysis.Diagnostics))
		return false;
	packageCompilation.SharedModule = analysis.Module;

	try
	{
		Directory.CreateDirectory(Path.GetDirectoryName(apiPath)!);
		using StreamWriter writer = new(apiPath, append: false, Encoding.UTF8);
		BindableNodeCodeSerializer.Serialize(BuildApiOutputModule(packageCompilation), writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
	}
	catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
	{
		Console.Error.WriteLine($"{apiPath}: {ex.Message}");
		return false;
	}

	if (staticLibraryPath is null)
		return true;

	if (!LowerAndReport(packageCompilation))
		return false;

	string packageBuildDirectory = Path.Combine(Path.GetDirectoryName(staticLibraryPath)!, "build");
	CEmissionResult emission = CCodeEmitter.Emit(packageCompilation, new CEmissionOptions
	{
		OutputDirectory = packageBuildDirectory,
		ProjectName = packageName,
		EmitKind = "c99"
	});
	foreach (string diagnostic in emission.Diagnostics)
		Console.Error.WriteLine(diagnostic);
	if (!emission.Success)
		return false;

	NativeBuildResult build = NativeBuildDriver.Build(new NativeBuildOptions
	{
		Target = context.Target,
		ProfileName = context.ProfileName,
		BuildDirectory = packageBuildDirectory,
		OutputDirectory = Path.GetDirectoryName(staticLibraryPath)!,
		ProjectName = packageName,
		Kind = NativeBuildKind.Static,
		SourceFiles = emission.GeneratedSourceFiles
	});
	foreach (string diagnostic in build.Diagnostics)
		Console.Error.WriteLine(diagnostic);
	return build.Success;
}

static int EmitDefaultOutput(Compilation compilation, string emitKind, NativeBuildKind? nativeBuildKind, string? outDir, string? buildDir, IReadOnlyList<string> packageLibraries)
{
	if (!LowerAndReport(compilation))
		return 1;

	FunctionDefinition? execEntryPoint = null;
	if (nativeBuildKind is NativeBuildKind.Exec && !TryPrepareExecEntryPoint(compilation, out execEntryPoint))
		return 1;

	string buildDirectory = string.IsNullOrWhiteSpace(buildDir)
		? CCodeEmitter.GetDefaultOutputDirectory(compilation.Files)
		: buildDir;
	string outputDirectory = string.IsNullOrWhiteSpace(outDir)
		? CCodeEmitter.GetDefaultArtifactDirectory(compilation.Files)
		: outDir;
	buildDirectory = Path.GetFullPath(buildDirectory);
	outputDirectory = Path.GetFullPath(outputDirectory);
	CEmissionResult result = CCodeEmitter.Emit(compilation, new CEmissionOptions
	{
		OutputDirectory = buildDirectory,
		ProjectName = CCodeEmitter.GetProjectName(compilation.Files),
		EmitKind = emitKind,
		EmitExecMainWrapper = nativeBuildKind is NativeBuildKind.Exec,
		ExecEntryPoint = execEntryPoint
	});
	foreach (string diagnostic in result.Diagnostics)
		Console.Error.WriteLine(diagnostic);
	if (!result.Success)
		return 1;

	foreach (string generated in result.GeneratedFiles)
		Console.Out.WriteLine("generated: " + Path.GetFileName(generated));

	if (nativeBuildKind is NativeBuildKind.Static or NativeBuildKind.Shared)
	{
		if (!TryEmitLibraryApiArtifacts(compilation, emitKind, outputDirectory))
			return 1;
	}

	if (nativeBuildKind is null)
		return 0;

	NativeBuildResult build = NativeBuildDriver.Build(new NativeBuildOptions
	{
		Target = compilation.Target!,
		ProfileName = compilation.ProfileName,
		BuildDirectory = buildDirectory,
		OutputDirectory = outputDirectory,
		ProjectName = CCodeEmitter.GetProjectName(compilation.Files),
		Kind = nativeBuildKind.Value,
		SourceFiles = result.GeneratedSourceFiles,
		Libraries = packageLibraries
	});
	foreach (string diagnostic in build.Diagnostics)
		Console.Error.WriteLine(diagnostic);
	if (!build.Success)
		return 1;
	foreach (string generated in build.GeneratedFiles)
		Console.Out.WriteLine("generated: " + Path.GetFileName(generated));
	return 0;
}

static bool TryPrepareExecEntryPoint(Compilation compilation, out FunctionDefinition? entryPoint)
{
	entryPoint = null;
	List<FunctionDefinition> candidates = [];
	foreach (Definition definition in compilation.SharedModule?.Definitions ?? [])
	{
		if (definition is FunctionDefinition { Name: "main", Export: not null } function)
			candidates.Add(function);
	}
	if (candidates.Count != 1)
	{
		Console.Error.WriteLine(candidates.Count == 0
			? "Building an executable requires exactly one exported function named 'main'."
			: "Building an executable requires exactly one exported function named 'main', but multiple were found.");
		return false;
	}

	FunctionDefinition main = candidates[0];
	bool returnsInt = main.ReturnType is PrimitiveTypeReference { Type: PrimitiveType.Int } || main.ResolvedType == "int";
	bool returnsVoid = main.ReturnType is PrimitiveTypeReference { Type: PrimitiveType.Void } || main.ResolvedType == "void";
	if (!returnsInt && !returnsVoid)
	{
		Console.Error.WriteLine("Executable entry point 'main' must return int or void.");
		return false;
	}

	if (main.Parameters.Count is not (0 or 2))
	{
		Console.Error.WriteLine("Executable entry point 'main' must have no parameters or one string[] parameter.");
		return false;
	}

	if (main.Parameters.Count == 2)
	{
		bool firstLooksLikeStringElements = main.Parameters[0].ResolvedType is string first && first.Contains("string", StringComparison.Ordinal) && first.Contains("*", StringComparison.Ordinal);
		bool secondLooksLikeLength = main.Parameters[1].ResolvedType is "nuint" or "const nuint" || main.Parameters[1].Type is PrimitiveTypeReference { Type: PrimitiveType.NUInt };
		if (!firstLooksLikeStringElements || !secondLooksLikeLength)
		{
			Console.Error.WriteLine("Executable entry point 'main' must have no parameters or one string[] parameter.");
			return false;
		}
	}

	main.Symbol = "campmain";
	entryPoint = main;
	return true;
}

static bool TryEmitLibraryApiArtifacts(Compilation compilation, string emitKind, string outputDirectory)
{
	string projectName = CCodeEmitter.GetProjectName(compilation.Files);
	string campApiPath = Path.Combine(outputDirectory, projectName + "_api.camp");
	try
	{
		Directory.CreateDirectory(outputDirectory);
		using StreamWriter writer = new(campApiPath, append: false, Encoding.UTF8);
		BindableNodeCodeSerializer.Serialize(BuildApiOutputModule(compilation), writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
		Console.Out.WriteLine("generated: " + Path.GetFileName(campApiPath));
	}
	catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
	{
		Console.Error.WriteLine($"{campApiPath}: {ex.Message}");
		return false;
	}

	CEmissionResult apiHeader = CCodeEmitter.EmitProjectApiHeader(compilation, new CEmissionOptions
	{
		OutputDirectory = outputDirectory,
		ProjectName = projectName,
		EmitKind = emitKind
	}, outputDirectory);
	foreach (string diagnostic in apiHeader.Diagnostics)
		Console.Error.WriteLine(diagnostic);
	if (!apiHeader.Success)
		return false;
	foreach (string generated in apiHeader.GeneratedFiles)
		Console.Out.WriteLine("generated: " + Path.GetFileName(generated));
	return true;
}

static int PrintTokens(Compilation compilation)
{
	CompilationPipeline.Tokenize(compilation);
	PrintTokenLines(compilation.Files[0].Tokens!);
	return 0;
}

static int PrintSyntaxXml(Compilation compilation)
{
	if (!ParseAllAndReport(compilation))
		return 1;

	PrintXmlDocument(SerializeSyntax(compilation.Files[0].SyntaxTree!));
	return 0;
}

static int PrintBindXml(Compilation compilation)
{
	if (!BuildAllAndReport(compilation))
		return 1;

	PrintXmlDocument(SerializeBindableNode(compilation.Files[0].BindableTree!));
	return 0;
}

static int PrintDeclarations(Compilation compilation, bool printXml)
{
	if (!ExpandDeclarationsAndReport(compilation))
		return 1;

	AnalysisResult analysis = BindableNodeAnalyzer.AnalyzeExpanded(compilation.DeclarationExpansion!, compilation.Target, compilation.MemoryModelName);
	if (!PrintAnalysisDiagnostics(compilation, analysis.Diagnostics))
		return 1;
	compilation.SharedModule = analysis.Module;

	PrintBindable(BuildOutputModule(compilation, compilation.Files[0]), printXml);
	return 0;
}

static int PrintLowering(Compilation compilation, bool printXml)
{
	if (!LowerAndReport(compilation))
		return 1;

	PrintBindable(BuildOutputModule(compilation, compilation.Files[0]), printXml);
	return 0;
}

static int PrintApi(Compilation compilation)
{
	if (!BuildAllAndReport(compilation))
		return 1;

	AnalysisResult analysis = BindableNodeAnalyzer.Analyze(compilation.SharedModule!, compilation.Target, compilation.MemoryModelName);
	if (!PrintAnalysisDiagnostics(compilation, analysis.Diagnostics))
		return 1;

	BindableNodeCodeSerializer.Serialize(BuildApiOutputModule(compilation), Console.Out, new BindableNodeCodeSerializerOptions { ApiHeader = true });
	return 0;
}

static Camp.Compiler.Module BuildApiOutputModule(Compilation compilation)
{
	Camp.Compiler.Module output = new()
	{
		ResolvedType = compilation.SharedModule?.ResolvedType
	};

	foreach (SourceFile file in compilation.Files)
	{
		if (file.IsApiHeader || file.BindableTree is not Camp.Compiler.Module module)
			continue;

		output.SourceSyntax ??= module.SourceSyntax;
		output.ExportAs ??= module.ExportAs;
		foreach (UsingDeclaration usingDeclaration in module.Usings)
			output.Usings.Add(usingDeclaration);
		foreach (Definition definition in module.Definitions)
			output.Definitions.Add(definition);
	}

	return output;
}

static Camp.Compiler.Module BuildOutputModule(Compilation compilation, SourceFile file)
{
	if (compilation.SharedModule is null)
		return file.BindableTree!;

	Camp.Compiler.Module output = new()
	{
		SourceSyntax = file.BindableTree?.SourceSyntax,
		ResolvedType = compilation.SharedModule.ResolvedType,
		ExportAs = file.BindableTree?.ExportAs
	};

	foreach (UsingDeclaration usingDeclaration in file.BindableTree?.Usings ?? [])
		output.Usings.Add(usingDeclaration);

	foreach (Definition definition in compilation.SharedModule.Definitions)
	{
		if (compilation.DefinitionOwners.TryGetValue(definition, out SourceFile? owner) && ReferenceEquals(owner, file))
			output.Definitions.Add(definition);
	}

	return output;
}

static bool ParseAllAndReport(Compilation compilation)
{
	bool success = CompilationPipeline.Parse(compilation);
	foreach (SourceFile file in compilation.Files)
	{
		foreach (ParseDiagnostic diagnostic in file.ParseDiagnostics)
			PrintDiagnostic(file.Path, diagnostic);
	}
	return success;
}

static bool BuildAllAndReport(Compilation compilation)
{
	bool success = CompilationPipeline.BuildAst(compilation);
	foreach (SourceFile file in compilation.Files)
	{
		foreach (ParseDiagnostic diagnostic in file.ParseDiagnostics)
			PrintDiagnostic(file.Path, diagnostic);
	}
	if (!success)
	{
		foreach (SourceFile file in compilation.Files)
		{
			foreach (BindDiagnostic diagnostic in file.BindDiagnostics)
				PrintBindDiagnostic(file.Path, diagnostic);
		}
		return false;
	}

	foreach (SourceFile file in compilation.Files)
	{
		foreach (BindDiagnostic diagnostic in file.BindDiagnostics)
			PrintBindDiagnostic(file.Path, diagnostic);
	}
	return true;
}

static bool ExpandDeclarationsAndReport(Compilation compilation)
{
	bool success = CompilationPipeline.ExpandDeclarations(compilation);
	PrintPipelineDiagnostics(compilation);
	return success;
}

static bool LowerAndReport(Compilation compilation)
{
	bool success = CompilationPipeline.Lower(compilation);
	PrintPipelineDiagnostics(compilation);
	return success;
}

static void PrintPipelineDiagnostics(Compilation compilation)
{
	HashSet<string> printed = [];
	foreach (SourceFile file in compilation.Files)
	{
		foreach (ParseDiagnostic diagnostic in file.ParseDiagnostics)
			PrintDiagnosticOnce(file.Path, diagnostic.Range, diagnostic.Message, printed);
		foreach (BindDiagnostic diagnostic in file.BindDiagnostics)
			PrintDiagnosticOnce(file.Path, diagnostic.Range, diagnostic.Message, printed);
	}

	if (compilation.DeclarationExpansion is not null && compilation.Lowering is null)
	{
		foreach (AnalysisDiagnostic diagnostic in compilation.DeclarationExpansion.Diagnostics)
			PrintDiagnosticOnce(GetDiagnosticFilename(compilation, diagnostic.Range), diagnostic.Range, diagnostic.Message, printed);
	}
	if (compilation.Lowering is not null)
	{
		foreach (AnalysisDiagnostic diagnostic in compilation.Lowering.Diagnostics)
			PrintDiagnosticOnce(GetDiagnosticFilename(compilation, diagnostic.Range), diagnostic.Range, diagnostic.Message, printed);
	}
}

static void PrintDiagnosticOnce(string filename, TokenRange? range, string message, HashSet<string> printed)
{
	string key = range is TokenRange r
		? $"{filename}:{r.StartLineNumber}:{r.StartColumn}:{message}"
		: $"{filename}:::${message}";
	if (!printed.Add(key))
		return;

	if (range is TokenRange tokenRange)
		Console.Error.WriteLine($"{filename}({tokenRange.StartLineNumber},{tokenRange.StartColumn}): error: {message}");
	else
		Console.Error.WriteLine($"{filename}: error: {message}");
}

static bool PrintAnalysisDiagnostics(Compilation compilation, IReadOnlyList<AnalysisDiagnostic> diagnostics)
{
	foreach (AnalysisDiagnostic diagnostic in diagnostics)
		PrintAnalysisDiagnostic(GetDiagnosticFilename(compilation, diagnostic.Range), diagnostic);
	return diagnostics.Count == 0;
}

static string GetDiagnosticFilename(Compilation compilation, TokenRange? range)
{
	if (range is TokenRange tokenRange)
	{
		foreach (SourceFile file in compilation.Files)
		{
			if (ReferenceEquals(file.Tokens, tokenRange.Sequence))
				return file.Path;
		}
	}

	return compilation.Files.Count == 0 ? "" : compilation.Files[0].Path;
}

static void PrintBindable(Camp.Compiler.Module module, bool printXml)
{
	if (printXml)
		PrintXmlDocument(SerializeBindableNode(module));
	else
		BindableNodeCodeSerializer.Serialize(module, Console.Out);
}

static void PrintXmlDocument(XElement root)
{
	XDocument document = new(new XDeclaration("1.0", "utf-8", null), root);
	XmlWriterSettings settings = new()
	{
		Indent = true,
		OmitXmlDeclaration = false
	};

	using XmlWriter writer = XmlWriter.Create(Console.Out, settings);
	document.Save(writer);
}

static void PrintDiagnostic(string filename, ParseDiagnostic diagnostic)
{
	if (diagnostic.Range is TokenRange range)
		Console.Error.WriteLine($"{filename}({range.StartLineNumber},{range.StartColumn}): error: {diagnostic.Message}");
	else
		Console.Error.WriteLine($"{filename}: error: {diagnostic.Message}");
}

static void PrintBindDiagnostic(string filename, BindDiagnostic diagnostic)
{
	if (diagnostic.Range is TokenRange range)
		Console.Error.WriteLine($"{filename}({range.StartLineNumber},{range.StartColumn}): error: {diagnostic.Message}");
	else
		Console.Error.WriteLine($"{filename}: error: {diagnostic.Message}");
}

static void PrintAnalysisDiagnostic(string filename, AnalysisDiagnostic diagnostic)
{
	if (diagnostic.Range is TokenRange range)
		Console.Error.WriteLine($"{filename}({range.StartLineNumber},{range.StartColumn}): error: {diagnostic.Message}");
	else
		Console.Error.WriteLine($"{filename}: error: {diagnostic.Message}");
}

static void PrintTokenLines(IEnumerable<Token> tokens)
{
	foreach (Token token in tokens)
	{
		WriteSubtle("\"");
		WriteEscapedTokenValue(token.Value);
		WriteSubtle("\" ");
		WriteColored(token.Class.ToString(), GetTokenColor(token.Class));
		Console.Out.WriteLine();
	}

	ResetColor();
}

static void WriteEscapedTokenValue(string value)
{
	foreach (char c in value)
	{
		switch (c)
		{
			case '\t':
				WriteColored("\\t", ConsoleColor.Blue);
				break;

			case '\r':
				WriteColored("\\r", ConsoleColor.Blue);
				break;

			case '\n':
				WriteColored("\\n", ConsoleColor.Blue);
				break;

			default:
				WriteColored(c.ToString(), ConsoleColor.Magenta);
				break;
		}
	}
}

static ConsoleColor GetTokenColor(TokenClass tokenClass)
{
	return tokenClass switch
	{
		TokenClass.Identifier => ConsoleColor.Blue,
		TokenClass.AttributeIdentifier => ConsoleColor.Green,
		TokenClass.Number => ConsoleColor.Magenta,
		TokenClass.String => ConsoleColor.Red,
		TokenClass.Symbol => ConsoleColor.DarkGray,
		TokenClass.LineComment or TokenClass.BlockComment => ConsoleColor.Green,
		TokenClass.Whitespace or TokenClass.NewLine => ConsoleColor.Gray,
		TokenClass.Invalid => ConsoleColor.Red,
		_ => ConsoleColor.Gray
	};
}

static void WriteSubtle(string value)
{
	WriteColored(value, ConsoleColor.Gray);
}

static void WriteColored(string value, ConsoleColor color)
{
	if (!Console.IsOutputRedirected)
		Console.ForegroundColor = color;

	Console.Out.Write(value);
}

static void ResetColor()
{
	if (!Console.IsOutputRedirected)
		Console.ResetColor();
}

static XElement SerializeSyntax(SyntaxNode syntax, string? elementName = null)
{
	Type type = syntax.GetType();
	string typeName = GetXmlName(type.Name);
	XElement element = new(elementName ?? typeName);

	if (elementName is not null && elementName != typeName)
		element.SetAttributeValue("Type", typeName);

	foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
	{
		object? value = property.GetValue(syntax);
		if (value is null)
			continue;

		if (IsTokenType(property.PropertyType) && value is Token token)
		{
			element.SetAttributeValue(property.Name, token.Value);
		}
		else if (IsTokenRangeType(property.PropertyType) && value is TokenRange range)
		{
			element.SetAttributeValue(property.Name, range.Value);
		}
		else if (IsListType(property.PropertyType) && value is IEnumerable items)
		{
			element.Add(SerializeList(property.Name, items));
		}
		else if (value is SyntaxNode childSyntax)
		{
			element.Add(SerializeSyntax(childSyntax, property.Name));
		}
		else
		{
			element.SetAttributeValue(property.Name, Convert.ToString(value, CultureInfo.InvariantCulture));
		}
	}

	return element;
}

static XElement SerializeList(string name, IEnumerable items)
{
	XElement element = new(name);

	foreach (object? item in items)
	{
		if (item is null)
			continue;

		switch (item)
		{
			case SyntaxNode syntax:
				element.Add(SerializeSyntax(syntax));
				break;

			case Token token:
				element.Add(new XElement("Token", new XAttribute("Value", token.Value)));
				break;

			case TokenRange range:
				element.Add(new XElement("TokenRange", new XAttribute("Value", range.Value)));
				break;

			default:
				element.Add(new XElement("Value", Convert.ToString(item, CultureInfo.InvariantCulture)));
				break;
		}
	}

	return element;
}

static XElement SerializeBindableNode(BindableNode node, string? elementName = null)
{
	Type type = node.GetType();
	string typeName = GetXmlName(type.Name);
	XElement element = new(elementName ?? typeName);

	if (elementName is not null && elementName != typeName)
		element.SetAttributeValue("Type", typeName);

	foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
	{
		if (property.Name == nameof(BindableNode.SourceSyntax))
			continue;

		object? value = property.GetValue(node);
		if (value is null)
			continue;

		if (IsSemanticReferenceProperty(property))
		{
			SerializeSemanticReference(element, property.Name, value);
		}
		else if (IsListType(property.PropertyType) && value is IEnumerable items)
		{
			XElement? list = SerializeBindableList(property.Name, items);
			if (list is not null)
				element.Add(list);
		}
		else if (value is BindableNode childNode)
		{
			element.Add(SerializeBindableNode(childNode, property.Name));
		}
		else if ((property.Name == "Modifier" || property.Name == "IteratorKind") && IsDefaultEnumValue(value))
		{
			continue;
		}
		else if (value is false)
		{
			continue;
		}
		else
		{
			element.SetAttributeValue(property.Name, Convert.ToString(value, CultureInfo.InvariantCulture));
		}
	}

	return element;
}

static bool IsSemanticReferenceProperty(PropertyInfo property)
{
	return property.DeclaringType == typeof(VariableReferenceExpression) && property.Name == nameof(VariableReferenceExpression.Variable)
		|| property.DeclaringType == typeof(TypeDefinitionReference) && property.Name == nameof(TypeDefinitionReference.Definition)
		|| property.DeclaringType == typeof(GenericParameterTypeReference) && property.Name == nameof(GenericParameterTypeReference.Parameter)
		|| property.DeclaringType == typeof(GotoStatement) && property.Name == nameof(GotoStatement.Target)
		|| property.DeclaringType == typeof(MethodReferenceExpression) && property.Name == nameof(MethodReferenceExpression.Candidates)
		|| property.DeclaringType == typeof(MemberReferenceExpression) && property.Name == nameof(MemberReferenceExpression.Member)
		|| property.DeclaringType == typeof(MemberReferenceExpression) && property.Name == nameof(MemberReferenceExpression.Candidates);
}

static void SerializeSemanticReference(XElement element, string name, object value)
{
	switch (value)
	{
		case Definition definition:
			element.SetAttributeValue(name, definition.Name);
			break;

		case GenericParameter parameter:
			element.SetAttributeValue(name, parameter.Name);
			break;

		case BindableNode node:
			element.SetAttributeValue(name, GetSemanticReferenceName(node));
			break;

		case IEnumerable<FunctionDefinition> functions:
		{
			XElement candidates = new(name);
			foreach (FunctionDefinition function in functions)
				candidates.Add(new XElement("Function", new XAttribute("Name", function.Name), new XAttribute("ResolvedType", function.ResolvedType ?? "")));
			if (candidates.HasElements)
				element.Add(candidates);
			break;
		}
	}
}

static string GetSemanticReferenceName(BindableNode node)
{
	return node switch
	{
		ParameterDefinition parameter => parameter.Name,
		Definition definition => definition.Name,
		DeclarationTarget target => string.Join(", ", target.Names),
		LambdaParameter parameter => parameter.Name ?? parameter.Parameter?.Name ?? "",
		LabelStatement label => label.Name ?? "",
		_ => node.GetType().Name
	};
}

static XElement? SerializeBindableList(string name, IEnumerable items)
{
	XElement element = new(name);
	bool hasItems = false;

	foreach (object? item in items)
	{
		if (item is null)
			continue;

		hasItems = true;

		if (item is BindableNode node)
			element.Add(SerializeBindableNode(node));
		else
			element.Add(new XElement("Value", Convert.ToString(item, CultureInfo.InvariantCulture)));
	}

	return hasItems ? element : null;
}

static bool IsDefaultEnumValue(object value)
{
	return value.GetType().IsEnum && Convert.ToInt64(value, CultureInfo.InvariantCulture) == 0;
}

static bool IsListType(Type type)
{
	return type != typeof(string)
		&& type != typeof(Token)
		&& type != typeof(TokenRange)
		&& type != typeof(Token?)
		&& type != typeof(TokenRange?)
		&& typeof(IEnumerable).IsAssignableFrom(type);
}

static bool IsTokenType(Type type)
{
	return type == typeof(Token) || type == typeof(Token?);
}

static bool IsTokenRangeType(Type type)
{
	return type == typeof(TokenRange) || type == typeof(TokenRange?);
}

static string GetXmlName(string typeName)
{
	return typeName.EndsWith("Syntax", StringComparison.Ordinal)
		? typeName[..^"Syntax".Length]
		: typeName;
}

sealed record RuntimeContext(string ExecutableDirectory, TargetDefinition Target, string ProfileName, string? MemoryModelName, IReadOnlyList<string> CommandLineDefines);

enum InspectMode
{
	None,
	Tokens,
	Cst,
	Ast,
	Declarations,
	Lowering
}
