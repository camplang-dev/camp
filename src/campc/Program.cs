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
	  -t, --target <name>       Select the target to use. Defaults to clang-macos-x64.
	  -p, --profile <name>      Select DEBUG or RELEASE. Defaults to DEBUG.
	  --memory-model <name>     Select a target memory model when the target requires one.
	  -d, --define <symbols...> Define conditional compilation symbols.
	  --emit c99                Generate C99 output. Defaults to c99.
	  -b, --build <kind>        Build a native artifact: exec, static, or shared.
	  --out-dir <dir>           Write native output artifacts to this directory.
	  --build-dir <dir>         Write generated C and intermediate build files to this directory.
	  --nostdlib                Do not include the default std package.
	  --inspect tokens          Print one token per line.
	  --inspect cst             Parse and print the syntax tree as XML.
	  --inspect ast             Parse and print the bindable tree as XML.
	  --inspect declarations    Analyze declarations and print the bindable tree as Camp code.
	  --inspect lowering        Analyze, lower, and print the bindable tree as Camp code.
	  --inspect-api             Print merged exported API declarations for compiled files.
	  --xml                     Print XML for declarations/lowering inspection.
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
	CompilerRequest request = new()
	{
		Inspect = ParseInspectMode(parseResult.GetValue(inspectOption)),
		Xml = parseResult.GetValue(xmlOption),
		InspectApi = parseResult.GetValue(inspectApiOption),
		TargetName = parseResult.GetValue(targetOption) ?? "clang-macos-x64",
		ProfileName = parseResult.GetValue(profileOption) ?? "DEBUG",
		MemoryModelName = parseResult.GetValue(memoryModelOption),
		EmitKind = parseResult.GetValue(emitOption) ?? "c99",
		BuildKind = ParseBuildKind(parseResult.GetValue(buildOption)),
		OutDir = parseResult.GetValue(outDirOption),
		BuildDir = parseResult.GetValue(buildDirOption),
		NoStdLib = parseResult.GetValue(noStdLibOption),
		AssetRoot = AppContext.BaseDirectory
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
		Console.Error.WriteLine($"Build kind '{build}' is not valid. Expected exec, static, or shared.");
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

static NativeBuildKind? ParseBuildKind(string? value)
{
	return value?.Trim().ToLowerInvariant() switch
	{
		null or "" => null,
		"exec" => NativeBuildKind.Exec,
		"static" => NativeBuildKind.Static,
		"shared" => NativeBuildKind.Shared,
		_ => null
	};
}
