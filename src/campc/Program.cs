using System;
using System.Collections.Generic;
using System.CommandLine;
using Camp.Compiler;

Argument<List<string>> filesArgument = new("files")
{
	Description = "One or more source files to read, or '-' to read from standard input.",
	Arity = ArgumentArity.OneOrMore
};

Option<string?> inspectOption = new("--inspect")
{
	Description = "Print an intermediate compiler representation. Allowed values: tokens, cst, ast, declarations, lowering."
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
	DefaultValueFactory = _ => CompilerDefaults.TargetName
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
	Description = "Build a native artifact: exec, winexe, static, or shared."
};

Option<string?> emitMetadataOption = new("--emit-metadata")
{
	Description = "Write source-level declaration metadata JSON to the given file."
};

Option<string> metadataVisibilityOption = new("--metadata-visibility")
{
	Description = "Select metadata visibility: export, public, private, or all.",
	DefaultValueFactory = _ => "export"
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
	Description = "Camp compiler"
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
rootCommand.Options.Add(emitMetadataOption);
rootCommand.Options.Add(metadataVisibilityOption);
rootCommand.Options.Add(outDirOption);
rootCommand.Options.Add(buildDirOption);
rootCommand.Options.Add(noStdLibOption);
rootCommand.SetAction(parseResult =>
{
	MetadataVisibility? metadataVisibility = ParseMetadataVisibility(parseResult.GetValue(metadataVisibilityOption));
	CompilerRequest request = new()
	{
		Inspect = ParseInspectMode(parseResult.GetValue(inspectOption)),
		Xml = parseResult.GetValue(xmlOption),
		InspectApi = parseResult.GetValue(inspectApiOption),
		TargetName = parseResult.GetValue(targetOption) ?? CompilerDefaults.TargetName,
		ProfileName = parseResult.GetValue(profileOption) ?? "DEBUG",
		MemoryModelName = parseResult.GetValue(memoryModelOption),
		EmitKind = parseResult.GetValue(emitOption) ?? "c99",
		BuildKind = ParseBuildKind(parseResult.GetValue(buildOption)),
		EmitMetadataPath = parseResult.GetValue(emitMetadataOption),
		MetadataVisibility = metadataVisibility ?? MetadataVisibility.Export,
		OutDir = parseResult.GetValue(outDirOption),
		BuildDir = parseResult.GetValue(buildDirOption),
		NoStdLib = parseResult.GetValue(noStdLibOption),
		RuntimeRoot = AppContext.BaseDirectory
	};

	request.Files.AddRange(parseResult.GetValue(filesArgument) ?? []);
	request.IncludeFiles.AddRange(parseResult.GetValue(includeOption) ?? []);
	request.Defines.AddRange(parseResult.GetValue(defineOption) ?? []);

	if (parseResult.GetValue(inspectOption) is string inspect && request.Inspect is null)
	{
		Console.Error.WriteLine($"Inspect mode '{inspect}' is not valid. Expected tokens, cst, ast, declarations, or lowering.");
		return 1;
	}

	if (parseResult.GetValue(buildOption) is string build && request.BuildKind is null)
	{
		Console.Error.WriteLine($"Build kind '{build}' is not valid. Expected exec, winexe, static, or shared.");
		return 1;
	}

	if (metadataVisibility is null)
	{
		Console.Error.WriteLine($"Metadata visibility '{parseResult.GetValue(metadataVisibilityOption)}' is not valid. Expected export, public, private, or all.");
		return 1;
	}

	CompilerResult result = CompilerDriver.Execute(request);
	Console.Out.Write(result.StdOut);
	Console.Error.Write(result.StdErr);
	return result.ExitCode;
});

return rootCommand.Parse(args).Invoke();

static CompilerInspectMode? ParseInspectMode(string? value)
{
	return value?.Trim().ToLowerInvariant() switch
	{
		null or "" => null,
		"tokens" => CompilerInspectMode.Tokens,
		"cst" => CompilerInspectMode.Cst,
		"ast" => CompilerInspectMode.Ast,
		"declarations" => CompilerInspectMode.Declarations,
		"lowering" => CompilerInspectMode.Lowering,
		_ => null
	};
}

static MetadataVisibility? ParseMetadataVisibility(string? value)
{
	return value?.Trim().ToLowerInvariant() switch
	{
		null or "" => MetadataVisibility.Export,
		"export" => MetadataVisibility.Export,
		"public" => MetadataVisibility.Public,
		"private" => MetadataVisibility.Private,
		"all" => MetadataVisibility.All,
		_ => null
	};
}

static NativeBuildKind? ParseBuildKind(string? value)
{
	return value?.Trim().ToLowerInvariant() switch
	{
		null or "" => null,
		"exec" => NativeBuildKind.Exec,
		"winexe" => NativeBuildKind.WinExe,
		"static" => NativeBuildKind.Static,
		"shared" => NativeBuildKind.Shared,
		_ => null
	};
}
