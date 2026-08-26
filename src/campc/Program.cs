using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.CommandLine;
using Camp.Compiler;

CliEnvironment environment = CliEnvironment.Create();
List<string> startupErrors = [];
string[] expandedArgs = ResponseFileExpander.Expand(args, environment.WorkingDirectory, startupErrors).ToArray();
if (startupErrors.Count > 0)
{
	foreach (string error in startupErrors)
		Console.Error.WriteLine(error);
	return 1;
}
if (IsVersionRequest(expandedArgs))
{
	Console.Out.WriteLine(GetVersionText());
	return 0;
}
RootCommand rootCommand = BuildCommandTree(environment, expandedArgs);
int exitCode = ContainsRemovedOption(expandedArgs) ? CampCli.Run(expandedArgs, environment) : rootCommand.Parse(expandedArgs).Invoke();
return exitCode;

static bool IsVersionRequest(string[] args)
{
	return args is ["--version"];
}

static string GetVersionText()
{
	if (CampBuildInfo.IsReleaseBuild)
		return StripLeadingVersionPrefix(CampBuildInfo.Version);
	return string.IsNullOrWhiteSpace(CampBuildInfo.Commit) || CampBuildInfo.Commit == "unknown"
		? CampBuildInfo.Version
		: CampBuildInfo.Version + "+" + CampBuildInfo.Commit;
}

static string StripLeadingVersionPrefix(string version)
{
	return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) && version.Length > 1 && char.IsDigit(version[1])
		? version[1..]
		: version;
}

static bool ContainsRemovedOption(string[] args)
{
	return args.Any(static arg => arg is "--inspect" or "--build" or "-b" or "--emit-metadata" or "--memory-model" or "--build-dir");
}

static RootCommand BuildCommandTree(CliEnvironment environment, string[] originalArgs)
{
	RootCommand root = new("Camp compiler");
	root.SetAction(_ => CampCli.Run(originalArgs, environment));

	Command init = new("init", "Initialize a Camp project.");
	init.Arguments.Add(new Argument<string?>("name")
	{
		Description = "Project directory/name.",
		Arity = ArgumentArity.ZeroOrOne
	});
	init.Options.Add(new Option<string?>("--template") { Description = "Template: app, static, shared, posix-api, windows-api, or wrapper." });
	init.Options.Add(new Option<bool>("--list") { Description = "List built-in templates." });
	init.SetAction(_ => CampCli.Run(originalArgs, environment));
	root.Subcommands.Add(init);

	Command build = new("build", "Compile, emit C, and optionally build a native artifact.");
	build.Arguments.Add(SourcePatternsArgument());
	AddBuildOptions(build, buildOnly: true, testRunnerOptions: false);
	build.SetAction(_ => CampCli.Run(originalArgs, environment));
	root.Subcommands.Add(build);

	Command run = new("run", "Build an executable and run it.");
	run.Arguments.Add(SourcePatternsArgument());
	AddBuildOptions(run, buildOnly: true, testRunnerOptions: false);
	run.SetAction(_ => CampCli.Run(originalArgs, environment));
	root.Subcommands.Add(run);

	Command dump = new("dump", "Print compiler intermediate output.");
	dump.Arguments.Add(new Argument<string>("kind")
	{
		Description = "Dump kind: tokens, declarations, lowering, or metadata."
	});
	dump.Arguments.Add(SourcePatternsArgument());
	AddBuildOptions(dump, buildOnly: false, testRunnerOptions: false);
	dump.SetAction(_ => CampCli.Run(originalArgs, environment));
	root.Subcommands.Add(dump);

	Command test = new("test", "Build and run Camp tests.");
	test.Arguments.Add(SourcePatternsArgument());
	AddBuildOptions(test, buildOnly: true, testRunnerOptions: true);
	test.SetAction(_ => CampCli.Run(originalArgs, environment));
	root.Subcommands.Add(test);

	Command cover = new("cover", "Build and run Camp tests with source coverage.");
	cover.Arguments.Add(SourcePatternsArgument());
	AddBuildOptions(cover, buildOnly: true, testRunnerOptions: true);
	AddCoverageOptions(cover);
	cover.SetAction(_ => CampCli.Run(originalArgs, environment));
	root.Subcommands.Add(cover);

	Command restore = new("restore", "Restore source-only package dependencies.");
	restore.Arguments.Add(SourcePatternsArgument());
	restore.Options.Add(new Option<List<string>>("--upgrade")
	{
		Description = "Upgrade all packages or one direct package dependency.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});
	restore.SetAction(_ => CampCli.Run(originalArgs, environment));
	root.Subcommands.Add(restore);

	Command pkg = new("pkg", "Manage source-only package publishing and caches.");
	AddPackageCommands(pkg, originalArgs, environment);
	root.Subcommands.Add(pkg);

	Command help = new("help", "Show help for campc or a command.");
	help.Arguments.Add(new Argument<string?>("command")
	{
		Description = "Command to describe.",
		Arity = ArgumentArity.ZeroOrOne
	});
	help.SetAction(parseResult =>
	{
		string? command = parseResult.GetValue<string?>("command");
		string[] helpArgs = string.IsNullOrWhiteSpace(command) ? ["--help"] : [command!, "--help"];
		return root.Parse(helpArgs).Invoke();
	});
	root.Subcommands.Add(help);

	return root;
}

static Argument<List<string>> SourcePatternsArgument()
{
	return new Argument<List<string>>("pattern.camp")
	{
		Description = "Source file paths or glob patterns.",
		Arity = ArgumentArity.ZeroOrMore
	};
}

static void AddBuildOptions(Command command, bool buildOnly, bool testRunnerOptions)
{
	command.Options.Add(new Option<List<string>>("--api")
	{
		Description = "Load Camp API/header files for analysis.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});
	command.Options.Add(new Option<List<string>>("--exclude")
	{
		Description = "Exclude source file patterns.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});
	command.Options.Add(new Option<string?>("--target", "-t") { Description = "Select the target." });
	command.Options.Add(new Option<string?>("--profile", "-p") { Description = "Select DEBUG or RELEASE profile." });
	command.Options.Add(new Option<List<string>>("--variant")
	{
		Description = "Select target variants.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});
	command.Options.Add(new Option<bool>("--verbose", "-v") { Description = "Print generated artifact paths." });
	command.Options.Add(new Option<bool>("--timing") { Description = "Print build timing information to stderr." });
	command.Options.Add(new Option<string?>("--timing-output") { Description = "Write build timing information as JSON." });
	command.Options.Add(new Option<List<string>>("--define", "-d")
	{
		Description = "Define conditional compilation symbols.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});
	command.Options.Add(new Option<string?>("--emit") { Description = "Select the emitter, currently c99." });
	command.Options.Add(new Option<bool>("--debug-info") { Description = "Emit Camp debug metadata and native debug line information." });
	command.Options.Add(new Option<bool>("--nostdlib") { Description = "Do not include the standard library package." });
	command.Options.Add(new Option<List<string>>("--reference", "-r")
	{
		Description = "Reference a native static library during linking.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});
	command.Options.Add(new Option<string>("--use", "-u")
	{
		Description = "Use an installed package, as pkg or pkg@version.",
		Arity = ArgumentArity.ExactlyOne,
		AllowMultipleArgumentsPerToken = false
	});
	command.Options.Add(new Option<string[]>("--use-source")
	{
		Description = "Define a package source name and optional local path.",
		Arity = new ArgumentArity(2, 2),
		AllowMultipleArgumentsPerToken = true
	});
	command.Options.Add(new Option<List<string>>("--project-reference")
	{
		Description = "Build and reference another Camp project response file.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});
	command.Options.Add(new Option<string?>("--metadata") { Description = "Emit metadata: none, export, public, or all." });
	command.Options.Add(new Option<bool>("--explicit-within") { Description = "Require source-level new/delete to use an explicit within context." });
	command.Options.Add(new Option<bool>("--implicit-within") { Description = "Allow source-level new/delete to use the default allocator without an explicit within context." });
	command.Options.Add(new Option<string?>("--sourcefile-paths") { Description = "Source capture file paths: relative or absolute." });
	command.Options.Add(new Option<List<string>>("--sourcefile-root")
	{
		Description = "Root for relative caller(sourcefile) paths.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});

	if (!buildOnly)
		return;

	command.Options.Add(new Option<string?>("--test-output-dir") { Description = "Directory for test manifest and result artifacts." });
	command.Options.Add(new Option<string?>("--test-result-format") { Description = "Test result output format: text, json, or text,json." });
	if (testRunnerOptions)
	{
		command.Options.Add(new Option<bool>("--list") { Description = "List discovered tests and stop." });
		command.Options.Add(new Option<bool>("--ignore-leaks") { Description = "Report tracked leaks without failing leak-only tests." });
		command.Options.Add(new Option<List<string>>("--filter")
		{
			Description = "Select tests by exact name or wildcard pattern.",
			Arity = ArgumentArity.ZeroOrMore,
			AllowMultipleArgumentsPerToken = true
		});
	}

	command.Options.Add(new Option<List<string>>("--framework", "-f")
	{
		Description = "Link a native framework during native builds.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});
	command.Options.Add(new Option<string?>("--artifact") { Description = "Native artifact: exec, static, shared, only-static, only-shared, or none." });
	command.Options.Add(new Option<string?>("--name") { Description = "Artifact/project name without extension." });
	command.Options.Add(new Option<string?>("--subsystem") { Description = "Native subsystem, currently windows." });
	command.Options.Add(new Option<string?>("--out-dir") { Description = "Directory for final artifacts." });
}

static void AddCoverageOptions(Command command)
{
	command.Options.Add(new Option<string?>("--coverage-format") { Description = "Coverage output format: json, lcov, or json,lcov." });
	command.Options.Add(new Option<string?>("--coverage-output-dir") { Description = "Directory for coverage map and result artifacts." });
	command.Options.Add(new Option<List<string>>("--coverage-subject")
	{
		Description = "Coverage subject: self or a shared project-reference name.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});
}

static void AddPackageCommands(Command pkg, string[] originalArgs, CliEnvironment environment)
{
	Command addGlobalSource = new("add-global-source", "Add or replace a named global package source.");
	addGlobalSource.Arguments.Add(new Argument<string>("name"));
	addGlobalSource.Arguments.Add(new Argument<string>("path-or-url"));
	addGlobalSource.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(addGlobalSource);

	Command removeGlobalSource = new("remove-global-source", "Remove a named global package source.");
	removeGlobalSource.Arguments.Add(new Argument<string>("name"));
	removeGlobalSource.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(removeGlobalSource);

	Command listGlobalSources = new("list-global-sources", "List configured global package sources.");
	listGlobalSources.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(listGlobalSources);

	Command install = new("install", "Install a source-only package into the package cache.");
	install.Arguments.Add(new Argument<string>("package"));
	install.Options.Add(new Option<bool>("--global") { Description = "Install into the compiler package root." });
	install.Options.Add(new Option<string?>("--local") { Description = "Read local package source configuration from a Camp file." });
	install.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(install);

	Command uninstall = new("uninstall", "Remove a package from the package cache.");
	uninstall.Arguments.Add(new Argument<string>("package"));
	uninstall.Options.Add(new Option<bool>("--global") { Description = "Remove from the compiler package root." });
	uninstall.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(uninstall);

	Command publish = new("publish", "Publish a source-only package archive.");
	publish.Arguments.Add(new Argument<string>("version"));
	publish.Arguments.Add(new Argument<string?>("build-file") { Arity = ArgumentArity.ZeroOrOne });
	publish.Options.Add(new Option<string?>("--name") { Description = "Package name when it cannot be inferred." });
	publish.Options.Add(new Option<string?>("--out") { Description = "Output package directory." });
	publish.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(publish);

	foreach (string removed in new[] { "add", "remove", "add-source", "remove-source", "search" })
	{
		Command compatibility = new(removed, "Removed package command.");
		compatibility.Arguments.Add(new Argument<List<string>>("args") { Arity = ArgumentArity.ZeroOrMore });
		compatibility.SetAction(_ => CampCli.Run(originalArgs, environment));
		pkg.Subcommands.Add(compatibility);
	}
}

sealed class CampCli
{
	public static int Run(string[] args, CliEnvironment environment)
	{
		if (args.Length == 0)
			return Error("A command is required. Expected init, pkg, restore, build, dump, run, test, or cover.");

		return args[0] switch
		{
			"init" => CampInit.Run(args[1..], environment),
			"build" => RunBuild(args[1..], environment),
			"run" => RunRun(args[1..], environment),
			"test" => RunBuildLike(args[1..], environment, CommandKind.Test),
			"cover" => RunBuildLike(args[1..], environment, CommandKind.Cover),
			"dump" => RunDump(args[1..], environment),
			"restore" => RunRestore(args[1..], environment),
			"pkg" => PackageCommands.Run(args[1..], environment),
			"--inspect" or "--build" or "-b" => Error("The root compiler command has been replaced by subcommands. Use 'campc dump ...' or 'campc build ...'."),
			_ when args[0].StartsWith("-", StringComparison.Ordinal) => Error($"Unknown command '{args[0]}'. Use init, pkg, restore, build, dump, run, test, or cover."),
			_ => Error($"Unknown command '{args[0]}'. Use init, pkg, restore, build, dump, run, test, or cover.")
		};
	}

	static int RunBuild(string[] args, CliEnvironment environment)
	{
		Stopwatch total = Stopwatch.StartNew();
		Stopwatch requestPreparation = Stopwatch.StartNew();
		if (!TryBuildRequest(args, environment, CommandKind.Build, out CompilerRequest? request, out List<string> errors))
			return PrintErrors(errors);
		requestPreparation.Stop();

		Stopwatch compilerBuild = Stopwatch.StartNew();
		CompilerResult result = CompilerDriver.Execute(request!);
		compilerBuild.Stop();
		total.Stop();
		Console.Out.Write(result.StdOut);
		Console.Error.Write(result.StdErr);
		WriteCliTiming(request!, CommandKind.Build, total.Elapsed, result.ExitCode, [
			new("request and project references", requestPreparation.Elapsed),
			new("compiler build", compilerBuild.Elapsed)
		]);
		return result.ExitCode;
	}

	static int RunBuildLike(string[] args, CliEnvironment environment, CommandKind command)
	{
		Stopwatch total = Stopwatch.StartNew();
		Stopwatch requestPreparation = Stopwatch.StartNew();
		if (!TryBuildRequest(args, environment, command, out CompilerRequest? request, out List<string> errors))
			return PrintErrors(errors);
		requestPreparation.Stop();

		Stopwatch compilerBuild = Stopwatch.StartNew();
		CompilerResult result = CompilerDriver.Execute(request!);
		compilerBuild.Stop();
		total.Stop();
		Console.Out.Write(result.StdOut);
		Console.Error.Write(result.StdErr);
		WriteCliTiming(request!, command, total.Elapsed, result.ExitCode, [
			new("request and project references", requestPreparation.Elapsed),
			new("compiler build", compilerBuild.Elapsed)
		]);
		return result.ExitCode;
	}

	static int RunRun(string[] args, CliEnvironment environment)
	{
		int separator = Array.IndexOf(args, "--");
		string[] buildArgs = separator >= 0 ? args[..separator] : args;
		string[] programArgs = separator >= 0 ? args[(separator + 1)..] : [];

		Stopwatch total = Stopwatch.StartNew();
		Stopwatch requestPreparation = Stopwatch.StartNew();
		if (!TryBuildRequest(buildArgs, environment, CommandKind.Run, out CompilerRequest? request, out List<string> errors))
			return PrintErrors(errors);
		requestPreparation.Stop();

		if (request!.BuildKind is not (NativeBuildKind.Exec or NativeBuildKind.WinExe))
			return Error("run requires --artifact exec.");

		Stopwatch compilerBuild = Stopwatch.StartNew();
		CompilerResult result = CompilerDriver.Execute(request);
		compilerBuild.Stop();
		Console.Error.Write(result.StdErr);
		if (result.ExitCode != 0)
		{
			total.Stop();
			Console.Out.Write(result.StdOut);
			WriteCliTiming(request, CommandKind.Run, total.Elapsed, result.ExitCode, [
				new("request and project references", requestPreparation.Elapsed),
				new("compiler build", compilerBuild.Elapsed)
			]);
			return result.ExitCode;
		}

		Stopwatch executableResolution = Stopwatch.StartNew();
		string? executable = TryGetRunExecutable(request, environment, out string? executableError);
		executableResolution.Stop();
		if (executable is null)
		{
			total.Stop();
			WriteCliTiming(request, CommandKind.Run, total.Elapsed, exitCode: 1, [
				new("request and project references", requestPreparation.Elapsed),
				new("compiler build", compilerBuild.Elapsed),
				new("resolve executable", executableResolution.Elapsed)
			]);
			return Error(executableError ?? "run could not find the generated executable.");
		}

		string extension = Path.GetExtension(executable);
		ProcessStartInfo info = new()
		{
			FileName = extension.Equals(".wasm", StringComparison.OrdinalIgnoreCase) ? "wasmtime" : extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ? "node" : executable,
			WorkingDirectory = environment.WorkingDirectory,
			UseShellExecute = false
		};
		if (extension.Equals(".wasm", StringComparison.OrdinalIgnoreCase) || extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
			info.ArgumentList.Add(executable);
		foreach (string argument in programArgs)
			info.ArgumentList.Add(argument);

		using Process process = new() { StartInfo = info };
		Stopwatch executableRun = Stopwatch.StartNew();
		try
		{
			process.Start();
			process.WaitForExit();
			executableRun.Stop();
			total.Stop();
			WriteCliTiming(request, CommandKind.Run, total.Elapsed, process.ExitCode, [
				new("request and project references", requestPreparation.Elapsed),
				new("compiler build", compilerBuild.Elapsed),
				new("resolve executable", executableResolution.Elapsed),
				new("run executable", executableRun.Elapsed)
			]);
			return process.ExitCode;
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			executableRun.Stop();
			total.Stop();
			WriteCliTiming(request, CommandKind.Run, total.Elapsed, exitCode: 1, [
				new("request and project references", requestPreparation.Elapsed),
				new("compiler build", compilerBuild.Elapsed),
				new("resolve executable", executableResolution.Elapsed),
				new("run executable", executableRun.Elapsed)
			]);
			return Error(ex.Message);
		}
	}

	static void WriteCliTiming(CompilerRequest request, CommandKind command, TimeSpan total, int exitCode, IReadOnlyList<CliTimingPhase> phases)
	{
		if (!request.TimingEnabled)
			return;
		string commandName = command.ToString().ToLowerInvariant();
		string projectName = string.IsNullOrWhiteSpace(request.ProjectName)
			? GetDefaultProjectNameFromRequest(request)
			: request.ProjectName!;
		string status = exitCode == 0 ? "success" : "failed";
		Console.Error.WriteLine($"Timing: cli {commandName} {projectName} {FormatTimingSeconds(total)} {status}");
		foreach (CliTimingPhase phase in phases)
			Console.Error.WriteLine($"  {phase.Name} {FormatTimingSeconds(phase.Elapsed)}");
	}

	static string FormatTimingSeconds(TimeSpan elapsed)
	{
		return (elapsed.TotalMilliseconds / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + "s";
	}

	readonly record struct CliTimingPhase(string Name, TimeSpan Elapsed);

	static string? TryGetRunExecutable(CompilerRequest request, CliEnvironment environment, out string? error)
	{
		error = null;
		List<string> errors = [];
		TargetDefinition? target = TryGetTargetDefinition(request, environment, errors);
		if (target is null)
		{
			error = string.Join(Environment.NewLine, errors);
			return null;
		}

		string outputPrefix = string.IsNullOrWhiteSpace(request.OutDir)
			? GetDefaultArtifactDirectoryFromRequest(request)
			: request.OutDir!;
		string outputRoot = Path.GetFullPath(outputPrefix, request.WorkingDirectory);
		string outputDirectory = request.OutDirIsDirect || IsDirectRunOutputPath(outputPrefix)
			? outputRoot
			: Path.Combine(outputRoot, BuildArtifactLayout.GetArtifactDirectoryName(target, request.BuildKind, request.ProfileName, request.CommandMode));
		string projectName = string.IsNullOrWhiteSpace(request.ProjectName)
			? GetDefaultProjectNameFromRequest(request)
			: request.ProjectName!;
		string executable = NativeBuildDriver.GetArtifactPath(new NativeBuildOptions
		{
			Target = target,
			ProfileName = request.ProfileName,
			BuildDirectory = Path.Combine(outputDirectory, "build"),
			OutputDirectory = outputDirectory,
			ProjectName = projectName,
			Kind = request.BuildKind!.Value,
			SourceFiles = []
		});
		if (!File.Exists(executable))
		{
			error = $"run could not find the generated executable: {executable}";
			return null;
		}
		return executable;
	}

	static string GetDefaultArtifactDirectoryFromRequest(CompilerRequest request)
	{
		string? firstSource = request.Files.FirstOrDefault(static file => file != "-");
		if (string.IsNullOrWhiteSpace(firstSource))
			return Path.Combine(request.WorkingDirectory, "bin");
		string full = Path.GetFullPath(firstSource, request.WorkingDirectory);
		string? directory = Path.GetDirectoryName(full);
		return Path.Combine(string.IsNullOrWhiteSpace(directory) ? request.WorkingDirectory : directory, "bin");
	}

	static string GetDefaultProjectNameFromRequest(CompilerRequest request)
	{
		string? firstSource = request.Files.FirstOrDefault(static file => file != "-");
		return string.IsNullOrWhiteSpace(firstSource)
			? "stdin"
			: SanitizeIdentifier(Path.GetFileNameWithoutExtension(firstSource));
	}

	static string SanitizeIdentifier(string value)
	{
		StringBuilder builder = new();
		foreach (char ch in value)
			builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
		return builder.ToString();
	}

	static bool IsDirectRunOutputPath(string value)
	{
		string normalized = value.Replace('\\', '/');
		return normalized == "." || normalized.EndsWith("/.", StringComparison.Ordinal);
	}

	static int RunDump(string[] args, CliEnvironment environment)
	{
		if (args.Length == 0)
			return Error("dump requires a dump kind: tokens, declarations, lowering, or metadata.");

		CompilerInspectMode? inspect = ParseDumpKind(args[0]);
		if (inspect is null)
			return Error($"Dump kind '{args[0]}' is not valid. Expected tokens, declarations, lowering, or metadata.");

		if (!TryBuildRequest(args[1..], environment, CommandKind.Dump, out CompilerRequest? request, out List<string> errors))
			return PrintErrors(errors);

		request!.Inspect = inspect;
		if (inspect == CompilerInspectMode.Metadata && request.EmitMetadata is null)
			request.EmitMetadata = MetadataVisibility.Export;

		CompilerResult result = CompilerDriver.Execute(request);
		Console.Out.Write(result.StdOut);
		Console.Error.Write(result.StdErr);
		return result.ExitCode;
	}

	static int RunRestore(string[] args, CliEnvironment environment)
	{
		if (args.Length == 0)
			return Error("restore requires at least one .camp file.");

		PrintPackagePreviewWarning();
		List<string> errors = [];
		args = ResponseFileExpander.ExpandBareBuildFiles(args, environment.WorkingDirectory, errors).ToArray();
		List<string> sourceArgs = [];
		string? upgrade = null;
		for (int i = 0; i < args.Length; i++)
		{
			if (args[i] == "--upgrade")
			{
				if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
					upgrade = args[++i];
				else
					upgrade = "";
				continue;
			}
			sourceArgs.Add(args[i]);
		}
		if (upgrade is not null && upgrade.Length > 0 && !PackageDependencySpec.TryParse(upgrade, out _, out string? upgradeError))
			errors.Add(upgradeError!);
		BuildOptionBag bag = new();
		ApplyGlobalPragmas(environment, bag, errors);
		ParsedOptions restoreOptions = CommandLineOptionParser.Parse(sourceArgs, allowPositionals: true, errors);
		bag.Apply(restoreOptions, Precedence.Local, "restore", errors);
		foreach (string file in ExpandSourcePatterns(sourceArgs, [], environment.WorkingDirectory, errors))
			ApplyFilePragmas(file, environment, bag, Precedence.Local, errors);
		if (errors.Count > 0)
			return PrintErrors(errors);

		string projectRoot = GetRestoreProjectRoot(restoreOptions.Positionals, environment.WorkingDirectory);
		return PackageCommands.Restore(bag.UsePackages, bag.UseSources, upgrade, environment, projectRoot);
	}

	static string GetRestoreProjectRoot(IReadOnlyList<string> positionals, string workingDirectory)
	{
		foreach (string positional in positionals)
		{
			string fullPath = Path.GetFullPath(positional, workingDirectory);
			if (File.Exists(fullPath))
				return Path.GetDirectoryName(fullPath)!;
		}
		return workingDirectory;
	}

	static bool TryBuildRequest(string[] args, CliEnvironment environment, CommandKind command, out CompilerRequest? request, out List<string> errors, List<string>? projectReferenceStack = null)
	{
		request = null;
		errors = [];
		string? defaultOutDir = command is CommandKind.Build or CommandKind.Run or CommandKind.Test or CommandKind.Cover ? TryGetDefaultOutDirFromBuildFile(args, environment.WorkingDirectory) : null;
		string sourcefileDefaultRoot = TryGetDefaultSourcefileRootFromBuildFile(args, environment.WorkingDirectory) ?? environment.WorkingDirectory;

		if (command is CommandKind.Build or CommandKind.Run or CommandKind.Test or CommandKind.Cover)
			args = ResponseFileExpander.ExpandBareBuildFiles(args, environment.WorkingDirectory, errors).ToArray();
		if (errors.Count > 0)
			return false;

		ParsedOptions cli = CommandLineOptionParser.Parse(args, allowPositionals: true, errors);
		if (errors.Count > 0)
			return false;

		BuildOptionBag bag = new();
		ApplyGlobalPragmas(environment, bag, errors);

		List<string> sourceFiles = ExpandSourcePatterns(cli.Positionals, cli.ExcludePatterns, environment.WorkingDirectory, errors);
		List<string> apiFiles = ExpandSourcePatterns(cli.ApiPatterns.Concat(bag.ApiPatterns).ToList(), [], environment.WorkingDirectory, errors);
		HashSet<string> pragmaFilesRead = new(StringComparer.OrdinalIgnoreCase);
		while (true)
		{
			List<string> filesToRead = sourceFiles.Concat(apiFiles).Where(pragmaFilesRead.Add).ToList();
			if (filesToRead.Count == 0)
				break;
			foreach (string file in filesToRead)
				ApplyFilePragmas(file, environment, bag, Precedence.Local, errors);
			apiFiles = ExpandSourcePatterns(cli.ApiPatterns.Concat(bag.ApiPatterns).ToList(), [], environment.WorkingDirectory, errors);
		}

		bag.Apply(cli, Precedence.CommandLine, "command line", errors);
		sourceFiles = ExpandSourcePatterns(cli.Positionals, bag.ExcludePatterns, environment.WorkingDirectory, errors);
		apiFiles = ExpandSourcePatterns(bag.ApiPatterns, [], environment.WorkingDirectory, errors);
		if (sourceFiles.Count == 0)
			errors.Add("At least one source file pattern is required.");

		if (command == CommandKind.Dump && bag.HasBuildOnlyOptions)
			errors.Add("dump does not accept --framework, --artifact, --name, --subsystem, or --out-dir.");
		if (command == CommandKind.Dump && bag.HasTestResultOptions)
			errors.Add("dump does not accept --test-output-dir or --test-result-format.");
		if (command != CommandKind.Cover && bag.HasCoverageOptions)
			errors.Add("--coverage-format, --coverage-output-dir, and --coverage-subject can only be used with cover.");
		if (command is not (CommandKind.Test or CommandKind.Cover) && bag.ListTests)
			errors.Add("--list can only be used with test or cover.");
		if (command is not (CommandKind.Test or CommandKind.Cover) && bag.TestFilters.Count > 0)
			errors.Add("--filter can only be used with test or cover.");
		if (command is not (CommandKind.Test or CommandKind.Cover) && bag.IgnoreLeaks)
			errors.Add("--ignore-leaks can only be used with test or cover.");
		if (bag.SubsystemName is not null && bag.SubsystemName != "windows")
			errors.Add($"Subsystem '{bag.SubsystemName}' is not valid. Expected windows.");
		if (bag.SubsystemName is not null && bag.ArtifactSpecified && bag.ArtifactKind is not NativeBuildKind.Exec)
			errors.Add("--subsystem can only be used with --artifact exec.");
		if (command == CommandKind.Run)
		{
			if (!bag.ArtifactSpecified)
				bag.SetArtifact(NativeBuildKind.Exec, "run default", errors);
			else if (bag.ArtifactKind is not NativeBuildKind.Exec)
				errors.Add("run requires --artifact exec.");
		}
		if (errors.Count > 0)
			return false;

		request = new CompilerRequest
		{
			RuntimeRoot = environment.RuntimeRoot,
			WorkingDirectory = environment.WorkingDirectory,
			TargetName = bag.TargetName ?? CompilerDefaults.TargetName,
			ProfileName = bag.ProfileName ?? "DEBUG",
			EmitKind = bag.EmitKind ?? "c99",
			BuildKind = bag.ArtifactKind,
			InferBuildKind = command == CommandKind.Build && !bag.ArtifactSpecified,
			WithinPolicyBuildKind = bag.ArtifactKind,
			InferWithinPolicyBuildKind = command is CommandKind.Test or CommandKind.Cover && !bag.ArtifactSpecified,
			CommandMode = GetCompilerCommandMode(command),
			DeclarationParticipationMode = command is CommandKind.Test or CommandKind.Cover ? DeclarationParticipationMode.TestModule : DeclarationParticipationMode.Production,
			CoverageInstrumentationMode = CoverageInstrumentationMode.Disabled,
			EmitDebugInfo = bag.DebugInfo,
			EmitMetadata = bag.MetadataVisibility,
			OutDir = bag.OutDir ?? defaultOutDir,
			OutDirIsDirect = bag.OutDir is not null && IsDirectRunOutputPath(bag.OutDir),
			ProjectName = bag.ProjectName,
			SubsystemName = bag.SubsystemName,
			NoStdLib = bag.NoStdLib,
			WithinAllocationPolicy = bag.WithinAllocationPolicy,
			SourcefilePathMode = bag.SourcefilePathMode,
			SourcefileDefaultRoot = sourcefileDefaultRoot,
			Verbose = bag.Verbose,
			TimingEnabled = bag.TimingEnabled,
			TimingOutput = bag.TimingOutput,
			ColorOutput = !Console.IsOutputRedirected,
			ListTests = bag.ListTests,
			IgnoreLeaks = bag.IgnoreLeaks,
			TestOutputDir = bag.TestOutputDir,
			TestResultFormat = bag.TestResultFormat,
			CoverageOutputDir = bag.CoverageOutputDir,
			CoverageFormat = bag.CoverageFormat
		};
		request.SourcefileRoots.AddRange(bag.SourcefileRoots);
		request.TestFilters.AddRange(bag.TestFilters);
		request.CoverageSubjects.AddRange(bag.CoverageSubjects);
		request.Defines.AddRange(bag.Defines);
		request.ConfigurationFlagDeclarations.AddRange(bag.ConfigurationFlagDeclarations);
		request.ConfigurationFlagConfigurations.AddRange(bag.ConfigurationFlagConfigurations);
		request.ConfigurationRequirements.AddRange(bag.ConfigurationRequirements);
		request.ConfigurationRequirementPolicy = bag.ConfigurationRequirementPolicy;
		request.Variants.AddRange(bag.Variants);
		request.References.AddRange(bag.References);
		request.Frameworks.AddRange(bag.Frameworks);
		request.UsePackages.AddRange(bag.UsePackages.Select(static package => package.ToString()));
		if (!TryAddUseSourceRoots(bag.UseSources, environment.WorkingDirectory, request.UseSourceRoots, errors))
			return false;
		request.Files.AddRange(sourceFiles.Select(path => Path.GetRelativePath(environment.WorkingDirectory, path)));
		request.ApiFiles.AddRange(apiFiles.Select(path => Path.GetRelativePath(environment.WorkingDirectory, path)));
		if (!TryBuildProjectReferences(bag.ProjectReferences, request, environment, projectReferenceStack ?? [], out List<string> projectApiHeaders, out List<string> sharedProjectApiHeaders, out List<string> projectLibraries, errors))
			return false;
		request.ApiFiles.AddRange(projectApiHeaders);
		request.SharedLibraryApiHeaders.AddRange(sharedProjectApiHeaders);
		request.References.AddRange(projectLibraries);
		if (command == CommandKind.Cover && !TryApplyRootCoverageSubject(request, bag.ProjectReferences.Count, errors))
			return false;
		return true;
	}

	static bool TryAddUseSourceRoots(IEnumerable<PackageSourceSpec> sources, string workingDirectory, List<string> destination, List<string> errors)
	{
		foreach (PackageSourceSpec source in sources)
		{
			if (string.IsNullOrWhiteSpace(source.Path))
				continue;
			destination.Add(source.Path!);
		}
		return true;
	}

	static CompilerCommandMode GetCompilerCommandMode(CommandKind command)
	{
		return command switch
		{
			CommandKind.Run => CompilerCommandMode.Run,
			CommandKind.Dump => CompilerCommandMode.Dump,
			CommandKind.Test => CompilerCommandMode.Test,
			CommandKind.Cover => CompilerCommandMode.Cover,
			_ => CompilerCommandMode.Build
		};
	}

	static bool TryBuildProjectReferences(IReadOnlyList<string> projectReferences, CompilerRequest consumerRequest, CliEnvironment environment, List<string> projectReferenceStack, out List<string> apiHeaders, out List<string> sharedApiHeaders, out List<string> libraries, List<string> errors)
	{
		apiHeaders = [];
		sharedApiHeaders = [];
		libraries = [];
		bool requireLibrary = consumerRequest.BuildKind is not null
			|| consumerRequest.InferBuildKind
			|| consumerRequest.CommandMode is CompilerCommandMode.Test or CompilerCommandMode.Cover;
		bool coverageMode = consumerRequest.CommandMode == CompilerCommandMode.Cover;
		int sharedCoverageCandidateCount = coverageMode
			? projectReferences.Select(static reference => ProjectReferenceSpec.Parse(reference)).Count(static spec => spec.LinkKind.GetValueOrDefault(DependencyLinkKind.Shared) == DependencyLinkKind.Shared)
			: 0;
		HashSet<string> matchedCoverageSubjects = new(StringComparer.Ordinal);
		if (coverageMode
			&& consumerRequest.CoverageSubjects.Count == 0
			&& sharedCoverageCandidateCount > 1)
		{
			errors.Add("External coverage with multiple shared project references requires --coverage-subject.");
			return false;
		}
		foreach (string projectReference in projectReferences)
		{
			ProjectReferenceSpec referenceSpec = ProjectReferenceSpec.Parse(projectReference);
			DependencyLinkKind effectiveLinkKind = referenceSpec.LinkKind.GetValueOrDefault(DependencyLinkKind.Shared);
			NativeBuildKind referenceBuildKind = effectiveLinkKind switch
			{
				DependencyLinkKind.Shared => NativeBuildKind.Shared,
				DependencyLinkKind.Static => NativeBuildKind.Static,
				_ => throw new ArgumentOutOfRangeException(nameof(effectiveLinkKind), effectiveLinkKind, null)
			};
			if (!TryResolveProjectReference(referenceSpec.Path, environment.WorkingDirectory, out string? buildFile, out string? error))
			{
				errors.Add(error!);
				continue;
			}
			string canonicalBuildFile = Path.GetFullPath(buildFile!);
			int cycleStart = projectReferenceStack.FindIndex(path => string.Equals(path, canonicalBuildFile, StringComparison.OrdinalIgnoreCase));
			if (cycleStart >= 0)
			{
				errors.Add("Project reference cycle detected: " + FormatProjectReferenceCycle(projectReferenceStack, canonicalBuildFile, cycleStart));
				continue;
			}

			List<string> responseErrors = [];
			List<string> projectArgs = ResponseFileExpander.Expand(["@" + canonicalBuildFile], environment.WorkingDirectory, responseErrors);
			errors.AddRange(responseErrors);
			if (responseErrors.Count > 0)
				continue;
			List<string> referenceOptionErrors = [];
			ParsedOptions referenceOptions = CommandLineOptionParser.Parse(projectArgs, allowPositionals: true, referenceOptionErrors);
			errors.AddRange(referenceOptionErrors.Select(error => $"{referenceSpec.Path}: {error}"));
			if (referenceOptionErrors.Count > 0)
				continue;
			if (referenceOptions.ArtifactRestriction is DependencyLinkKind restriction && restriction != effectiveLinkKind)
			{
				errors.Add($"{referenceSpec.Path}: project reference requires {restriction.ToString().ToLowerInvariant()} linking but was requested as {effectiveLinkKind.ToString().ToLowerInvariant()}.");
				continue;
			}

			string projectDirectory = Path.GetDirectoryName(canonicalBuildFile)!;
			string coverageSubjectName = ProjectReferenceOutputName(referenceOptions, canonicalBuildFile);
			bool instrumentForCoverage = ShouldInstrumentProjectReferenceForCoverage(consumerRequest, coverageSubjectName, effectiveLinkKind, sharedCoverageCandidateCount);
			if (consumerRequest.CoverageSubjects.Contains(coverageSubjectName, StringComparer.Ordinal))
				matchedCoverageSubjects.Add(coverageSubjectName);
			if (coverageMode && consumerRequest.CoverageSubjects.Contains(coverageSubjectName, StringComparer.Ordinal) && effectiveLinkKind != DependencyLinkKind.Shared)
			{
				errors.Add($"Coverage subject '{coverageSubjectName}' must be referenced as a shared library.");
				continue;
			}
			TargetDefinition? target = TryGetTargetDefinition(consumerRequest, environment, errors);
			string artifactDirectory = target is null
				? consumerRequest.TargetName + (instrumentForCoverage ? "_COVER" : "")
				: BuildArtifactLayout.GetArtifactDirectoryName(target, referenceBuildKind, consumerRequest.ProfileName, instrumentForCoverage ? CompilerCommandMode.Cover : CompilerCommandMode.Build);
			string projectOutputDirectory = Path.Combine(projectDirectory, "bin", artifactDirectory);
			projectArgs = RemoveProjectReferenceOverrideOptions(projectArgs);
			projectArgs.AddRange(["--target", consumerRequest.TargetName]);
			projectArgs.AddRange(["--profile", consumerRequest.ProfileName]);
			if (consumerRequest.Variants.Count > 0)
				projectArgs.AddRange(["--variant", .. consumerRequest.Variants]);
			if (consumerRequest.Verbose)
				projectArgs.Add("--verbose");
			if (consumerRequest.TimingEnabled)
				projectArgs.Add("--timing");
			projectArgs.AddRange(["--artifact", referenceBuildKind == NativeBuildKind.Shared ? "shared" : "static"]);
			projectArgs.AddRange(["--out-dir", Path.Combine(projectOutputDirectory, ".")]);

			List<string> childStack = [.. projectReferenceStack, canonicalBuildFile];
			CliEnvironment projectEnvironment = new()
			{
				WorkingDirectory = projectDirectory,
				RuntimeRoot = environment.RuntimeRoot,
				HomeDirectory = environment.HomeDirectory
			};
			if (!TryBuildRequest(projectArgs.ToArray(), projectEnvironment, CommandKind.Build, out CompilerRequest? projectRequest, out List<string> projectErrors, childStack))
			{
				foreach (string projectError in projectErrors)
					errors.Add($"{projectReference}: {projectError}");
				continue;
			}
			projectRequest!.OutDir = projectOutputDirectory;
			projectRequest.OutDirIsDirect = true;
			projectRequest.SourcefileDefaultRoot = consumerRequest.SourcefileDefaultRoot;

			if (instrumentForCoverage)
				projectRequest.CoverageInstrumentationMode = CoverageInstrumentationMode.ProductionSubject;

			if (!instrumentForCoverage && target is not null && TryGetCurrentProjectReferenceArtifacts(projectRequest, canonicalBuildFile, projectOutputDirectory, referenceBuildKind, target, environment.GlobalCampPath, requireLibrary, out string? currentApiHeader, out string? currentLibrary))
			{
				if (consumerRequest.Verbose)
					Console.Out.WriteLine($"{projectReference}: project reference {ProjectReferenceOutputName(projectRequest, canonicalBuildFile)}: current");
				apiHeaders.Add(currentApiHeader);
				if (effectiveLinkKind == DependencyLinkKind.Shared)
					sharedApiHeaders.Add(currentApiHeader);
				if (currentLibrary is not null)
					AddUniquePath(libraries, currentLibrary);
				foreach (string reference in projectRequest.References)
				{
					if (referenceBuildKind == NativeBuildKind.Static || IsSharedDependencyReference(reference, target))
						AddUniquePath(libraries, reference);
				}
				continue;
			}
			if (consumerRequest.Verbose)
				Console.Out.WriteLine($"{projectReference}: project reference {ProjectReferenceOutputName(projectRequest, canonicalBuildFile)}: rebuilding");

			CompilerResult result = CompilerDriver.Execute(projectRequest);
			WriteProjectReferenceOutput(projectReference, result.StdOut);
			string expectedApiHeader = Path.Combine(projectOutputDirectory, ProjectReferenceOutputName(projectRequest, canonicalBuildFile) + "_api.camp");
			string? apiHeader = result.GeneratedFiles.FirstOrDefault(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedApiHeader), StringComparison.OrdinalIgnoreCase));
			string? library = result.GeneratedFiles.FirstOrDefault(path => IsNativeLibrary(path, consumerRequest.TargetName, consumerRequest.RuntimeRoot, referenceBuildKind));
			string? coverageMap = result.GeneratedFiles.FirstOrDefault(static path => path.EndsWith(".camp-coverage-map.csv", StringComparison.OrdinalIgnoreCase));
			if (result.ExitCode != 0)
			{
				if (!requireLibrary && apiHeader is not null)
				{
					apiHeaders.Add(apiHeader);
					continue;
				}
				errors.Add($"{projectReference}: project reference build failed.");
				if (!string.IsNullOrWhiteSpace(result.StdErr))
					errors.Add(result.StdErr.TrimEnd());
				if (!string.IsNullOrWhiteSpace(result.StdOut))
					errors.Add(result.StdOut.TrimEnd());
				continue;
			}

			if (apiHeader is null && File.Exists(expectedApiHeader))
				apiHeader = expectedApiHeader;
			if (library is null && requireLibrary && Directory.Exists(projectOutputDirectory))
				library = Directory.EnumerateFiles(projectOutputDirectory)
					.FirstOrDefault(path => IsNativeLibrary(path, consumerRequest.TargetName, consumerRequest.RuntimeRoot, referenceBuildKind));

			if (apiHeader is null || requireLibrary && library is null)
			{
				errors.Add(requireLibrary
					? $"{referenceSpec.Path}: project reference did not produce a Camp API header and {effectiveLinkKind.ToString().ToLowerInvariant()} library."
					: $"{referenceSpec.Path}: project reference did not produce a Camp API header.");
				continue;
			}
			if (instrumentForCoverage)
			{
				if (coverageMap is null)
				{
					errors.Add($"{referenceSpec.Path}: instrumented coverage subject did not produce a coverage map.");
					continue;
				}
				AddUniquePath(consumerRequest.CoverageMapInputs, coverageMap);
			}
			apiHeaders.Add(apiHeader);
			if (effectiveLinkKind == DependencyLinkKind.Shared)
				sharedApiHeaders.Add(apiHeader);
			if (library is not null)
				AddUniquePath(libraries, library);
			foreach (string reference in projectRequest.References)
			{
				if (referenceBuildKind == NativeBuildKind.Static || target is not null && IsSharedDependencyReference(reference, target))
					AddUniquePath(libraries, reference);
			}
		}
		if (coverageMode)
		{
			foreach (string subject in consumerRequest.CoverageSubjects.Where(static subject => subject != "self"))
			{
				if (!matchedCoverageSubjects.Contains(subject))
					errors.Add($"Coverage subject '{subject}' could not be matched to a shared project reference.");
			}
		}
		return errors.Count == 0;
	}

	static bool TryGetCurrentProjectReferenceArtifacts(CompilerRequest projectRequest, string buildFile, string outputDirectory, NativeBuildKind buildKind, TargetDefinition target, string globalCampPath, bool requireLibrary, out string apiHeader, out string? library)
	{
		string projectName = ProjectReferenceOutputName(projectRequest, buildFile);
		apiHeader = Path.Combine(outputDirectory, projectName + "_api.camp");
		string cApiHeader = Path.Combine(outputDirectory, projectName + "_api.h");
		string metadata = Path.Combine(outputDirectory, projectName + "_api.json");
		NativeBuildOptions nativeOptions = new()
		{
			Target = target,
			ProfileName = projectRequest.ProfileName,
			BuildDirectory = Path.Combine(outputDirectory, "build"),
			OutputDirectory = outputDirectory,
			ProjectName = projectName,
			Kind = buildKind,
			SourceFiles = []
		};
		string nativeArtifact = NativeBuildDriver.GetArtifactPath(nativeOptions);
		library = requireLibrary ? NativeBuildDriver.GetLinkArtifactPath(nativeOptions) : null;

		List<string> outputs = [apiHeader, cApiHeader, metadata];
		if (requireLibrary)
		{
			if (library is not null && !File.Exists(library))
				return false;
			outputs.Add(nativeArtifact);
			if (library is not null && !string.Equals(nativeArtifact, library, StringComparison.OrdinalIgnoreCase))
				outputs.Add(library!);
		}

		List<string> inputs = GetProjectReferenceCacheInputs(projectRequest, buildFile, target, globalCampPath).ToList();
		return OutputsAreCurrent(outputs, inputs);
	}

	static string ProjectReferenceOutputName(CompilerRequest projectRequest, string buildFile)
	{
		if (!string.IsNullOrWhiteSpace(projectRequest.ProjectName))
			return projectRequest.ProjectName!;
		string? firstSource = projectRequest.Files.FirstOrDefault(file => !file.EndsWith("_api.camp", StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrWhiteSpace(firstSource))
			return Path.GetFileNameWithoutExtension(firstSource);
		return Path.GetFileNameWithoutExtension(buildFile);
	}

	static string ProjectReferenceOutputName(ParsedOptions projectOptions, string buildFile)
	{
		string? projectName = projectOptions.SingleValues.LastOrDefault(static value => value.Key == "name").Value;
		if (!string.IsNullOrWhiteSpace(projectName))
			return projectName!;
		string? firstSource = projectOptions.Positionals.FirstOrDefault(static file => !file.EndsWith("_api.camp", StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrWhiteSpace(firstSource) && !Glob.HasWildcards(firstSource!))
			return Path.GetFileNameWithoutExtension(firstSource);
		return Path.GetFileNameWithoutExtension(buildFile);
	}

	static bool ShouldInstrumentProjectReferenceForCoverage(CompilerRequest consumerRequest, string coverageSubjectName, DependencyLinkKind linkKind, int sharedCoverageCandidateCount)
	{
		if (consumerRequest.CommandMode != CompilerCommandMode.Cover || linkKind != DependencyLinkKind.Shared)
			return false;
		if (consumerRequest.CoverageSubjects.Count == 0)
			return sharedCoverageCandidateCount == 1;
		return consumerRequest.CoverageSubjects.Contains(coverageSubjectName, StringComparer.Ordinal);
	}

	static bool TryApplyRootCoverageSubject(CompilerRequest request, int projectReferenceCount, List<string> errors)
	{
		if (request.CommandMode != CompilerCommandMode.Cover)
			return true;
		bool explicitSelf = request.CoverageSubjects.Contains("self", StringComparer.Ordinal);
		bool explicitDependency = request.CoverageSubjects.Any(static subject => subject != "self");
		if (!explicitSelf && !explicitDependency && request.CoverageMapInputs.Count == 0)
		{
			request.CoverageInstrumentationMode = CoverageInstrumentationMode.ProductionSubject;
			return true;
		}
		if (explicitSelf)
		{
			request.CoverageInstrumentationMode = CoverageInstrumentationMode.ProductionSubject;
			return true;
		}
		if (request.CoverageMapInputs.Count > 0)
		{
			request.CoverageInstrumentationMode = CoverageInstrumentationMode.Disabled;
			return true;
		}
		if (projectReferenceCount > 0)
		{
			errors.Add("External coverage requires a shared project reference coverage subject or --coverage-subject self.");
			return false;
		}
		errors.Add("Coverage subject '" + string.Join(", ", request.CoverageSubjects) + "' could not be matched.");
		return false;
	}

	static IEnumerable<string> GetProjectReferenceCacheInputs(CompilerRequest projectRequest, string buildFile, TargetDefinition target, string globalCampPath)
	{
		yield return buildFile;
		if (File.Exists(globalCampPath))
			yield return globalCampPath;
		foreach (string input in ResolveProjectReferenceInputPaths(projectRequest, projectRequest.Files, includeDirectories: true))
			yield return input;
		foreach (string input in ResolveProjectReferenceInputPaths(projectRequest, projectRequest.ApiFiles, includeDirectories: false))
			yield return input;
		foreach (string input in projectRequest.SharedLibraryApiHeaders)
			yield return input;
		foreach (string reference in projectRequest.References)
		{
			if (Path.IsPathRooted(reference) && (File.Exists(reference) || Directory.Exists(reference)))
				yield return reference;
		}
		if (File.Exists(target.Path))
			yield return target.Path;
		if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
			yield return Environment.ProcessPath;
	}

	static IEnumerable<string> ResolveProjectReferenceInputPaths(CompilerRequest projectRequest, IEnumerable<string> paths, bool includeDirectories)
	{
		foreach (string path in paths)
		{
			string fullPath = Path.GetFullPath(path, projectRequest.WorkingDirectory);
			if (File.Exists(fullPath) || Directory.Exists(fullPath))
				yield return fullPath;
			if (includeDirectories)
			{
				string? directory = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : Directory.Exists(fullPath) ? fullPath : null;
				if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
					yield return directory;
			}
		}
	}

	static bool OutputsAreCurrent(IReadOnlyList<string> outputs, IReadOnlyList<string> inputs)
	{
		if (outputs.Count == 0 || outputs.Any(static output => !File.Exists(output)))
			return false;
		DateTime oldestOutput = outputs.Select(File.GetLastWriteTimeUtc).Min();
		foreach (string input in inputs.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (File.Exists(input))
			{
				if (oldestOutput <= File.GetLastWriteTimeUtc(input))
					return false;
			}
			else if (Directory.Exists(input))
			{
				if (oldestOutput <= Directory.GetLastWriteTimeUtc(input))
					return false;
			}
			else
			{
				return false;
			}
		}
		return true;
	}

	static void AddUniquePath(List<string> paths, string path)
	{
		if (!paths.Any(existing => string.Equals(Path.GetFullPath(existing), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
			paths.Add(path);
	}

	static TargetDefinition? TryGetTargetDefinition(CompilerRequest request, CliEnvironment environment, List<string> errors)
	{
		string targetsDirectory = Path.GetFullPath(Path.Combine(environment.RuntimeRoot, "..", "targets"));
		if (!TargetCatalog.TryLoad(targetsDirectory, out TargetCatalog? catalog, out string? error))
		{
			errors.Add(error ?? $"Target directory '{targetsDirectory}' could not be loaded.");
			return null;
		}
		if (!catalog!.TryGetTarget(request.TargetName, out TargetDefinition? target))
		{
			errors.Add($"Target '{request.TargetName}' could not be found in '{targetsDirectory}'.");
			return null;
		}
		try
		{
			TargetVariantSelection selection = target!.ResolveVariantSelection(request.Variants);
			return target.WithVariantSelection(selection);
		}
		catch (InvalidDataException ex)
		{
			errors.Add(ex.Message);
			return null;
		}
	}

	static string FormatProjectReferenceCycle(IReadOnlyList<string> stack, string repeatedBuildFile, int cycleStart)
	{
		List<string> cycle = [];
		for (int i = cycleStart; i < stack.Count; i++)
			cycle.Add(ProjectReferenceDisplayName(stack[i]));
		cycle.Add(ProjectReferenceDisplayName(repeatedBuildFile));
		return string.Join(" -> ", cycle);
	}

	static string ProjectReferenceDisplayName(string buildFile)
	{
		string directory = Path.GetDirectoryName(buildFile) ?? "";
		string fileName = Path.GetFileName(buildFile);
		return string.IsNullOrWhiteSpace(directory) ? fileName : Path.Combine(Path.GetFileName(directory), fileName);
	}

	static void WriteProjectReferenceOutput(string projectReference, string output)
	{
		if (string.IsNullOrWhiteSpace(output))
			return;
		foreach (string line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
		{
			if (!string.IsNullOrWhiteSpace(line))
				Console.Out.WriteLine($"{projectReference}: {line}");
		}
	}

	static bool TryResolveProjectReference(string value, string workingDirectory, out string? buildFile, out string? error)
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
		error = $"Project reference '{value}' could not be found. Resolved path: {fullPath}";
		return false;
	}

	static List<string> RemoveProjectReferenceOverrideOptions(IReadOnlyList<string> args)
	{
		HashSet<string> removeValueOptions = new(StringComparer.Ordinal)
		{
			"--target",
			"-t",
			"--profile",
			"-p",
			"--variant",
			"--artifact",
			"--out-dir",
			"--build-dir"
		};
		List<string> result = [];
		for (int i = 0; i < args.Count; i++)
		{
			if (removeValueOptions.Contains(args[i]))
			{
				if (args[i] is "--variant")
				{
					while (i + 1 < args.Count && IsVariantValueToken(args[i + 1]))
						i++;
				}
				else if (i + 1 < args.Count)
					i++;
				continue;
			}
			result.Add(args[i]);
		}
		return result;
	}

	static bool IsVariantValueToken(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
			return false;
		foreach (char c in value)
			if (!char.IsAsciiLetterOrDigit(c))
				return false;
		return true;
	}

	static string? TryGetDefaultOutDirFromBuildFile(IReadOnlyList<string> args, string workingDirectory)
	{
		for (int i = 0; i < args.Count; i++)
		{
			string token = args[i];
			if (token.StartsWith("-", StringComparison.Ordinal))
			{
				i += ResponseFileExpander.OptionValueCountForBuildRequest(token);
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

	static string? TryGetDefaultSourcefileRootFromBuildFile(IReadOnlyList<string> args, string workingDirectory)
	{
		string? buildFile = TryGetBuildFileArgument(args, workingDirectory);
		return buildFile is null ? null : Path.GetDirectoryName(buildFile);
	}

	static string? TryGetBuildFileArgument(IReadOnlyList<string> args, string workingDirectory)
	{
		for (int i = 0; i < args.Count; i++)
		{
			string token = args[i];
			if (token.StartsWith("-", StringComparison.Ordinal))
			{
				i += ResponseFileExpander.OptionValueCountForBuildRequest(token);
				continue;
			}
			string candidate = token.StartsWith("@", StringComparison.Ordinal) ? token[1..] : token;
			string fullPath = Path.GetFullPath(candidate, workingDirectory);
			if (!File.Exists(fullPath) && !Path.HasExtension(fullPath) && File.Exists(fullPath + ".campbuild"))
				fullPath += ".campbuild";
			if (File.Exists(fullPath) && Path.GetExtension(fullPath).Equals(".campbuild", StringComparison.OrdinalIgnoreCase))
				return fullPath;
		}
		return null;
	}

	static bool IsNativeLibrary(string path, string targetName, string runtimeRoot, NativeBuildKind kind)
	{
		string targetRoot = Path.GetFullPath(Path.Combine(runtimeRoot, "..", "targets"));
		if (!TargetCatalog.TryLoad(targetRoot, out TargetCatalog? catalog, out _) || !catalog!.TryGetTarget(targetName, out TargetDefinition? target))
			return kind == NativeBuildKind.Shared ? Path.GetExtension(path) is ".so" or ".dylib" or ".dll" : Path.GetExtension(path) is ".a" or ".lib";
		string extension = kind == NativeBuildKind.Shared
			? target!.GetArtifactValue("shared_import_ext", target!.GetArtifactValue("shared_ext", ".so"))
			: target!.GetArtifactValue("static_ext", ".a");
		return Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);
	}

	static bool IsSharedDependencyReference(string path, TargetDefinition target)
	{
		if (!Path.IsPathRooted(path))
			return false;
		string sharedExtension = target.GetArtifactValue("shared_ext", ".so");
		if (Path.GetExtension(path).Equals(sharedExtension, StringComparison.OrdinalIgnoreCase))
			return true;
		string sharedImportExtension = target.GetArtifactValue("shared_import_ext");
		if (string.IsNullOrWhiteSpace(sharedImportExtension) || !Path.GetExtension(path).Equals(sharedImportExtension, StringComparison.OrdinalIgnoreCase))
			return false;
		return File.Exists(Path.ChangeExtension(path, sharedExtension));
	}

	static void ApplyGlobalPragmas(CliEnvironment environment, BuildOptionBag bag, List<string> errors)
	{
		if (!File.Exists(environment.GlobalCampPath))
			return;
		ApplyFilePragmas(environment.GlobalCampPath, environment, bag, Precedence.Global, errors);
	}

	static void ApplyFilePragmas(string file, CliEnvironment environment, BuildOptionBag bag, Precedence precedence, List<string> errors)
	{
		foreach (PragmaLine pragma in BuildPragmaReader.Read(file, environment.WorkingDirectory, errors))
		{
			ParsedOptions parsed = CommandLineOptionParser.Parse(pragma.Tokens, allowPositionals: false, errors);
			bag.Apply(parsed, precedence, pragma.SourceName, errors);
		}
	}

	static List<string> ExpandSourcePatterns(List<string> patterns, List<string> excludePatterns, string workingDirectory, List<string> errors)
	{
		List<string> files = [];
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (string pattern in patterns)
		{
			foreach (string path in Glob.Expand(pattern, workingDirectory))
			{
				if (!path.EndsWith(".camp", StringComparison.OrdinalIgnoreCase))
					continue;
				if (excludePatterns.Any(exclude => Glob.IsMatch(Path.GetRelativePath(workingDirectory, path), exclude)))
					continue;
				if (seen.Add(path))
					files.Add(path);
			}
		}
		return files.OrderBy(static path => path, StringComparer.Ordinal).ToList();
	}

	static CompilerInspectMode? ParseDumpKind(string value)
	{
		return value.Trim().ToLowerInvariant() switch
		{
			"tokens" => CompilerInspectMode.Tokens,
			"declarations" => CompilerInspectMode.Declarations,
			"lowering" => CompilerInspectMode.Lowering,
			"metadata" => CompilerInspectMode.Metadata,
			_ => null
		};
	}

	static int PrintErrors(IEnumerable<string> errors)
	{
		foreach (string error in errors)
			Console.Error.WriteLine(error);
		return 1;
	}

	static int Error(string message)
	{
		Console.Error.WriteLine(message);
		return 1;
	}

	public static void PrintPackagePreviewWarning()
	{
		Console.Error.WriteLine("warning: package commands are experimental package infrastructure for Camp compiler development; command names and layouts may change.");
	}
}

sealed class PackageCommands
{
	public static int Run(string[] args, CliEnvironment environment)
	{
		if (args.Length == 0)
			return Error("pkg requires a package command.");
		CampCli.PrintPackagePreviewWarning();
		return args[0] switch
		{
			"add-global-source" => AddGlobalSource(args[1..], environment),
			"remove-global-source" => RemoveGlobalSource(args[1..], environment),
			"list-global-sources" => ListGlobalSources(args[1..], environment),
			"install" => InstallCommand(args[1..], environment),
			"uninstall" => Uninstall(args[1..], environment),
			"publish" => Publish(args[1..], environment),
			"add" => Error("pkg add has been removed. Edit the build file and add --use manually."),
			"remove" => Error("pkg remove has been removed. Edit the build file and remove --use manually."),
			"add-source" => Error("pkg add-source has been removed. Use pkg add-global-source for global sources or edit the build file and add --use-source manually."),
			"remove-source" => Error("pkg remove-source has been removed. Use pkg remove-global-source for global sources or edit the build file manually."),
			"search" => Error("pkg search has been removed until Camp has a package index."),
			_ => Error($"Unknown pkg command '{args[0]}'.")
		};
	}

	static int AddGlobalSource(string[] args, CliEnvironment environment)
	{
		if (args.Length < 2)
			return Error("pkg add-global-source requires <name> <path-or-url>.");
		EditBuildPragmas(environment.GlobalCampPath, line => !line.StartsWith("#build --use-source " + args[0] + " ", StringComparison.Ordinal), $"#build --use-source {args[0]} {Quote(args[1])}");
		return 0;
	}

	static int RemoveGlobalSource(string[] args, CliEnvironment environment)
	{
		if (args.Length < 1)
			return Error("pkg remove-global-source requires <name>.");
		EditBuildPragmas(environment.GlobalCampPath, line => !line.StartsWith("#build --use-source " + args[0], StringComparison.Ordinal), null);
		return 0;
	}

	static int ListGlobalSources(string[] args, CliEnvironment environment)
	{
		if (args.Length > 0)
			return Error("pkg list-global-sources does not accept arguments.");
		List<string> errors = [];
		BuildOptionBag bag = LoadEffectiveSources(environment, args, errors);
		if (errors.Count > 0)
			return PrintErrors(errors);
		foreach (PackageSourceSpec source in bag.UseSources)
		{
			if (!string.IsNullOrWhiteSpace(source.Path))
				Console.Out.WriteLine($"{source.Name} {source.Path}");
		}
		return 0;
	}

	static int InstallCommand(string[] args, CliEnvironment environment)
	{
		if (args.Length == 0)
			return Error("pkg install requires <package[@version|/version]>.");
		bool global = args.Contains("--global", StringComparer.Ordinal);
		if (!PackageDependencySpec.TryParse(args[0], out PackageDependencySpec package, out string? packageError))
			return Error(packageError!);
		List<string> errors = [];
		BuildOptionBag bag = LoadEffectiveSources(environment, args, errors);
		if (errors.Count > 0)
			return PrintErrors(errors);
		if (!Install(package, global, environment, bag.UseSources, out string message, out string? error))
			return Error(error ?? message);
		Console.Out.WriteLine(message);
		return 0;
	}

	static int Uninstall(string[] args, CliEnvironment environment)
	{
		if (args.Length == 0)
			return Error("pkg uninstall requires <pkg@ver>.");
		bool global = args.Contains("--global", StringComparer.Ordinal);
		PackageSpec package = PackageSpec.Parse(args[0]);
		string root = global ? environment.GlobalPackageRoot : environment.LocalPackageRoot;
		string packageDirectory = Path.Combine(root, package.Name);
		if (!Directory.Exists(packageDirectory))
			return 0;
		if (package.Version is null)
			Directory.Delete(packageDirectory, recursive: true);
		else
		{
			string versionDirectory = Path.Combine(packageDirectory, package.Version);
			if (Directory.Exists(versionDirectory))
				Directory.Delete(versionDirectory, recursive: true);
		}
		return 0;
	}

	static int Publish(string[] args, CliEnvironment environment)
	{
		if (args.Length == 0)
			return Error("pkg publish requires <version|+major|+minor|+patch>.");
		if (!TryParsePublishArgs(args, environment, out PublishRequest? request, out string? error))
			return Error(error!);
		if (!TryLoadPackageBuildInputs(request!, environment, out PublishInputs? inputs, out error))
			return Error(error!);
		if (!TrySelectPublishVersion(request!.Version, inputs!.OutputDirectory, inputs.PackageName, out PackageSelectedVersion version, out error))
			return Error(error!);
		if (!TryPublish(inputs, version, out string? archiveName, out string? hash, out error))
			return Error(error!);
		Console.Out.WriteLine($"published: {inputs.PackageName}@{version}");
		Console.Out.WriteLine($"archive: {archiveName}");
		Console.Out.WriteLine($"sha256: {hash}");
		return 0;
	}

	public static int Restore(IReadOnlyList<PackageSpec> packages, IReadOnlyList<PackageSourceSpec> sources, string? upgrade, CliEnvironment environment, string projectRoot)
	{
		if (packages.Count == 0)
			return 0;

		string lockPath = Path.Combine(projectRoot, "packages.ini");
		PackageLockFile? existingLock = null;
		if (File.Exists(lockPath))
		{
			if (!PackageLockFile.TryParse(lockPath, File.ReadAllText(lockPath), out existingLock, out List<string> lockErrors))
				return PrintErrors(lockErrors);
		}

		Dictionary<string, ResolvedPackage> resolved = new(StringComparer.Ordinal);
		Dictionary<string, PackageCatalog> catalogs = new(StringComparer.Ordinal);
		List<PackageSourceLocation> locations = sources
			.Where(static source => !string.IsNullOrWhiteSpace(source.Path))
			.Select(static source => new PackageSourceLocation(source.Name, source.Path!))
			.ToList();
		if (locations.Count == 0)
			return Error("No package sources are configured. Add --use-source to the build file or use 'campc pkg add-global-source'.");

		string? upgradeName = null;
		PackageVersionExpression? upgradeExpression = null;
		bool upgradeAll = upgrade == "";
		if (!string.IsNullOrWhiteSpace(upgrade))
		{
			PackageDependencySpec upgradeSpec = PackageDependencySpec.Parse(upgrade);
			upgradeName = upgradeSpec.Name;
			upgradeExpression = upgradeSpec.VersionExpression ?? (upgradeSpec.SelectedVersion is PackageSelectedVersion selected ? new PackageVersionExpression(selected.Major, selected.Minor, selected.Patch) : null);
		}

		foreach (PackageSpec package in packages)
		{
			PackageDependencySpec dependency = PackageDependencySpec.Parse(package.ToString());
			if (upgradeName is not null && dependency.Name.Equals(upgradeName, StringComparison.Ordinal))
				dependency = dependency with { VersionExpression = upgradeExpression ?? dependency.VersionExpression, SelectedVersion = null };
			if (!TryResolveDependency(dependency, direct: true, out string? error))
				return Error(error!);
		}

		SortedDictionary<string, PackageLockEntry> lockEntries = new(StringComparer.Ordinal);
		foreach (ResolvedPackage package in resolved.Values.OrderBy(static package => package.Name, StringComparer.Ordinal))
		{
			if (!InstallResolvedPackage(package, projectRoot, out string? error))
				return Error(error!);
			lockEntries[package.Name] = new PackageLockEntry(package.Name, package.Identity, package.Version, package.CatalogVersion.Sha256);
			Console.Out.WriteLine($"installed: {package.Name}@{package.Version}");
		}
		File.WriteAllText(lockPath, new PackageLockFile(lockEntries).Write(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		return 0;

		bool TryResolveDependency(PackageDependencySpec dependency, bool direct, out string? error)
		{
			error = null;
			if (resolved.TryGetValue(dependency.Name, out ResolvedPackage? existing))
			{
				if (!Matches(dependency, existing.Version))
				{
					error = $"Package dependency conflict for '{dependency.Name}'. Selected {existing.Version}, but dependency requires {FormatVersionRequirement(dependency)}.";
					return false;
				}
				return true;
			}

			bool shouldUpgrade = upgradeAll || direct && upgradeName is not null && dependency.Name.Equals(upgradeName, StringComparison.Ordinal);
			if (!shouldUpgrade
				&& existingLock?.Packages.TryGetValue(dependency.Name, out PackageLockEntry? locked) == true
				&& Matches(dependency, locked.Version))
			{
				if (!TryFindCatalogVersion(dependency.Name, locked.Identity, locked.Version, out PackageCatalog? lockedCatalog, out PackageCatalogVersion? lockedVersion, out PackageSourceLocation? lockedSource, out error))
					return false;
				ResolvedPackage lockedPackage = new(dependency.Name, locked.Identity, locked.Version, lockedCatalog!, lockedVersion!, lockedSource!);
				resolved[dependency.Name] = lockedPackage;
				foreach (PackageDependencySpec transitive in lockedVersion!.Dependencies)
					if (!TryResolveDependency(transitive, direct: false, out error))
						return false;
				return true;
			}

			if (!TryFindBestCatalogVersion(dependency, out PackageCatalog? catalog, out PackageCatalogVersion? version, out PackageSourceLocation? source, out error))
				return false;
			ResolvedPackage package = new(dependency.Name, catalog!.Identity, version!.Version, catalog, version, source!);
			resolved[dependency.Name] = package;
			foreach (PackageDependencySpec transitive in version.Dependencies)
				if (!TryResolveDependency(transitive, direct: false, out error))
					return false;
			return true;
		}

		bool TryFindCatalogVersion(string packageName, string identity, PackageSelectedVersion version, out PackageCatalog? catalog, out PackageCatalogVersion? catalogVersion, out PackageSourceLocation? source, out string? error)
		{
			catalog = null;
			catalogVersion = null;
			source = null;
			error = null;
			foreach (PackageSourceLocation location in locations)
			{
				if (!TryLoadCatalog(packageName, location, out PackageCatalog? candidate, out error))
					continue;
				if (!candidate!.Identity.Equals(identity, StringComparison.Ordinal))
					continue;
				if (candidate.Versions.TryGetValue(version, out PackageCatalogVersion? selected))
				{
					catalog = candidate;
					catalogVersion = selected;
					source = location;
					return true;
				}
			}
			error = $"Package '{packageName}/{version}' is locked but not installed and could not be found in configured package sources. Add a package source or update the lock with 'campc restore --upgrade {packageName}'.";
			return false;
		}

		bool TryFindBestCatalogVersion(PackageDependencySpec dependency, out PackageCatalog? catalog, out PackageCatalogVersion? version, out PackageSourceLocation? source, out string? error)
		{
			catalog = null;
			version = null;
			source = null;
			error = null;
			string? identity = null;
			foreach (PackageSourceLocation location in locations)
			{
				if (!TryLoadCatalog(dependency.Name, location, out PackageCatalog? candidate, out string? catalogError))
				{
					error ??= catalogError;
					continue;
				}
				if (identity is not null && !identity.Equals(candidate!.Identity, StringComparison.Ordinal))
				{
					error = $"Package '{dependency.Name}' has conflicting identities in configured package sources: '{identity}' and '{candidate.Identity}'.";
					return false;
				}
				identity = candidate!.Identity;
				foreach (PackageCatalogVersion item in candidate.Versions.Values.Reverse())
				{
					if (!Matches(dependency, item.Version))
						continue;
					if (version is null || item.Version.CompareTo(version.Version) > 0)
					{
						catalog = candidate;
						version = item;
						source = location;
					}
				}
			}
			if (version is not null)
				return true;
			error = $"Package '{dependency}' could not be found in configured package sources.";
			return false;
		}

		bool TryLoadCatalog(string packageName, PackageSourceLocation source, out PackageCatalog? catalog, out string? error)
		{
			string key = source.Name + ":" + packageName;
			if (catalogs.TryGetValue(key, out catalog))
			{
				error = null;
				return true;
			}
			if (!PackageSourceClient.TryReadText(source, packageName, "versions.ini", out string text, out error))
				return false;
			if (!PackageCatalog.TryParse(source.Name + ":" + packageName + "/versions.ini", text, out catalog, out List<string> errors))
			{
				error = string.Join(Environment.NewLine, errors);
				return false;
			}
			if (!catalog!.PackageName.Equals(packageName, StringComparison.Ordinal))
			{
				error = $"Package source '{source.Name}' catalog for '{packageName}' declares package name '{catalog.PackageName}'.";
				return false;
			}
			catalogs[key] = catalog;
			return true;
		}
	}

	static bool Matches(PackageDependencySpec dependency, PackageSelectedVersion version)
	{
		if (dependency.SelectedVersion is not null)
			return dependency.SelectedVersion == version;
		return dependency.VersionExpression is null || dependency.VersionExpression.Matches(version);
	}

	static string FormatVersionRequirement(PackageDependencySpec dependency)
	{
		if (dependency.SelectedVersion is not null)
			return "/" + dependency.SelectedVersion;
		if (dependency.VersionExpression is not null)
			return "@" + dependency.VersionExpression;
		return "any version";
	}

	static bool InstallResolvedPackage(ResolvedPackage package, string projectRoot, out string? error)
	{
		return InstallResolvedPackageToRoot(package, Path.Combine(projectRoot, "cache", "pkg"), out error);
	}

	static bool InstallResolvedPackageToRoot(ResolvedPackage package, string targetRoot, out string? error)
	{
		string targetDirectory = Path.Combine(targetRoot, package.Name, package.Version.ToString());
		if (Directory.Exists(Path.Combine(targetDirectory, "src")))
		{
			error = null;
			return true;
		}
		if (!PackageSourceClient.TryReadBytes(package.Source, package.Name, package.CatalogVersion.SourceArchive, out byte[] archive, out error))
			return false;
		string tempDirectory = Path.Combine(targetRoot, ".tmp-" + package.Name + "-" + Guid.NewGuid().ToString("N"));
		if (!PackageArchive.TryExtractVerified(archive, package.CatalogVersion.Sha256, tempDirectory, out error))
			return false;
		if (Directory.Exists(targetDirectory))
			Directory.Delete(targetDirectory, recursive: true);
		Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
		Directory.Move(tempDirectory, targetDirectory);
		return true;
	}

	sealed record ResolvedPackage(string Name, string Identity, PackageSelectedVersion Version, PackageCatalog Catalog, PackageCatalogVersion CatalogVersion, PackageSourceLocation Source);

	public static bool Install(PackageDependencySpec package, bool global, CliEnvironment environment, IReadOnlyList<PackageSourceSpec> sources, out string message, out string? error)
	{
		error = null;
		message = "";
		List<PackageSourceLocation> locations = sources
			.Where(static source => !string.IsNullOrWhiteSpace(source.Path))
			.Select(static source => new PackageSourceLocation(source.Name, source.Path!))
			.ToList();
		if (locations.Count == 0)
		{
			error = "No package sources are configured. Use 'campc pkg add-global-source' or add --use-source to a local build configuration.";
			return false;
		}
		if (!TryResolveSinglePackage(package, locations, out ResolvedPackage? resolved, out error))
			return false;
		string targetRoot = global ? environment.GlobalPackageRoot : environment.LocalPackageRoot;
		if (!InstallResolvedPackageToRoot(resolved!, targetRoot, out error))
			return false;
		message = $"installed: {resolved!.Name}@{resolved.Version}";
		return true;
	}

	public static bool IsInstalled(PackageSpec package, string root)
	{
		string packageDirectory = Path.Combine(root, package.Name);
		if (!Directory.Exists(packageDirectory))
			return false;
		if (package.Version is null)
			return Directory.GetDirectories(packageDirectory).Length > 0;
		return Directory.Exists(Path.Combine(packageDirectory, package.Version));
	}

	static bool TryResolveSinglePackage(PackageDependencySpec dependency, IReadOnlyList<PackageSourceLocation> locations, out ResolvedPackage? resolved, out string? error)
	{
		resolved = null;
		error = null;
		string? identity = null;
		foreach (PackageSourceLocation location in locations)
		{
			if (!PackageSourceClient.TryReadText(location, dependency.Name, "versions.ini", out string text, out string? readError))
			{
				error ??= readError;
				continue;
			}
			if (!PackageCatalog.TryParse(location.Name + ":" + dependency.Name + "/versions.ini", text, out PackageCatalog? catalog, out List<string> catalogErrors))
			{
				error = string.Join(Environment.NewLine, catalogErrors);
				return false;
			}
			if (!catalog!.PackageName.Equals(dependency.Name, StringComparison.Ordinal))
			{
				error = $"Package source '{location.Name}' catalog for '{dependency.Name}' declares package name '{catalog.PackageName}'.";
				return false;
			}
			if (identity is not null && !identity.Equals(catalog.Identity, StringComparison.Ordinal))
			{
				error = $"Package '{dependency.Name}' has conflicting identities in configured package sources: '{identity}' and '{catalog.Identity}'.";
				return false;
			}
			identity = catalog.Identity;
			foreach (PackageCatalogVersion version in catalog.Versions.Values.Reverse())
			{
				if (!Matches(dependency, version.Version))
					continue;
				if (resolved is null || version.Version.CompareTo(resolved.Version) > 0)
					resolved = new ResolvedPackage(dependency.Name, catalog.Identity, version.Version, catalog, version, location);
			}
		}
		if (resolved is not null)
			return true;
		error = $"Package '{dependency}' could not be found in configured package sources.";
		return false;
	}

	static bool TryParsePublishArgs(string[] args, CliEnvironment environment, out PublishRequest? request, out string? error)
	{
		request = null;
		error = null;
		string version = args[0];
		string? buildFile = null;
		string? name = null;
		string? outputDirectory = null;
		for (int i = 1; i < args.Length; i++)
		{
			string arg = args[i];
			if (arg == "--name")
			{
				if (i + 1 >= args.Length)
				{
					error = "pkg publish --name requires a value.";
					return false;
				}
				name = args[++i];
				continue;
			}
			if (arg == "--out")
			{
				if (i + 1 >= args.Length)
				{
					error = "pkg publish --out requires a value.";
					return false;
				}
				outputDirectory = Path.GetFullPath(args[++i], environment.WorkingDirectory);
				continue;
			}
			if (arg.StartsWith("-", StringComparison.Ordinal))
			{
				error = $"pkg publish option '{arg}' is not valid.";
				return false;
			}
			if (buildFile is not null)
			{
				error = "pkg publish accepts at most one build file.";
				return false;
			}
			buildFile = arg;
		}
		if (buildFile is null)
		{
			string[] candidates = Directory.GetFiles(environment.WorkingDirectory, "*.campbuild").OrderBy(static path => path, StringComparer.Ordinal).ToArray();
			if (candidates.Length == 0)
			{
				error = "pkg publish requires a build file when the current directory does not contain one.";
				return false;
			}
			if (candidates.Length > 1)
			{
				error = "pkg publish requires an explicit build file when the current directory contains multiple .campbuild files.";
				return false;
			}
			buildFile = candidates[0];
		}
		else
		{
			string fullPath = Path.GetFullPath(buildFile, environment.WorkingDirectory);
			if (!File.Exists(fullPath) && !Path.HasExtension(fullPath) && File.Exists(fullPath + ".campbuild"))
				fullPath += ".campbuild";
			buildFile = fullPath;
		}
		if (!File.Exists(buildFile))
		{
			error = $"Build file '{buildFile}' could not be found.";
			return false;
		}
		request = new PublishRequest(version, buildFile, name, outputDirectory);
		return true;
	}

	static bool TryLoadPackageBuildInputs(PublishRequest request, CliEnvironment environment, out PublishInputs? inputs, out string? error)
	{
		inputs = null;
		error = null;
		List<string> errors = [];
		string projectRoot = Path.GetDirectoryName(request.BuildFile)!;
		string[] expanded = ResponseFileExpander.ExpandBareBuildFiles([request.BuildFile], environment.WorkingDirectory, errors).ToArray();
		ParsedOptions options = CommandLineOptionParser.Parse(expanded, allowPositionals: true, errors);
		BuildOptionBag bag = new();
		bag.Apply(options, Precedence.Local, request.BuildFile, errors);
		List<string> sourceFiles = ExpandPackageSourcePatterns(options.Positionals, bag.ExcludePatterns, projectRoot, errors);
		if (sourceFiles.Count == 0)
			errors.Add("pkg publish requires at least one source file in the selected build file.");
		if (errors.Count > 0)
		{
			error = string.Join(Environment.NewLine, errors);
			return false;
		}
		string packageName = request.Name ?? bag.ProjectName ?? Path.GetFileNameWithoutExtension(request.BuildFile);
		if (!PackageDependencySpec.TryParse(packageName, out PackageDependencySpec parsedName, out string? packageNameError) || parsedName.Name != packageName || parsedName.VersionExpression is not null || parsedName.SelectedVersion is not null || parsedName.LinkKind is not null)
		{
			error = $"Package name '{packageName}' is not valid: {packageNameError ?? "package names cannot include versions or dependency kinds."}";
			return false;
		}
		string outputDirectory = request.OutputDirectory ?? Path.Combine(projectRoot, "pub", packageName);
		List<string> packageFiles = CollectPackageFiles(projectRoot, request.BuildFile, sourceFiles);
		inputs = new PublishInputs(packageName, projectRoot, outputDirectory, request.BuildFile, packageFiles, bag.UsePackages);
		return true;
	}

	static List<string> ExpandPackageSourcePatterns(List<string> patterns, List<string> excludePatterns, string projectRoot, List<string> errors)
	{
		List<string> files = [];
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (string pattern in patterns)
		{
			foreach (string path in Glob.Expand(pattern, projectRoot))
			{
				if (!path.EndsWith(".camp", StringComparison.OrdinalIgnoreCase))
					continue;
				if (excludePatterns.Any(exclude => Glob.IsMatch(Path.GetRelativePath(projectRoot, path), exclude)))
					continue;
				if (seen.Add(path))
					files.Add(path);
			}
		}
		return files.OrderBy(static path => path, StringComparer.Ordinal).ToList();
	}

	static List<string> CollectPackageFiles(string projectRoot, string buildFile, IReadOnlyList<string> sourceFiles)
	{
		HashSet<string> files = new(StringComparer.OrdinalIgnoreCase)
		{
			Path.GetFullPath(buildFile)
		};
		foreach (string source in sourceFiles)
		{
			files.Add(Path.GetFullPath(source));
			string directory = Path.GetDirectoryName(source)!;
			foreach (string support in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
				.Where(static path => path.EndsWith(".c", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".h", StringComparison.OrdinalIgnoreCase)))
				files.Add(Path.GetFullPath(support));
		}
		foreach (string rootFile in Directory.GetFiles(projectRoot, "*", SearchOption.TopDirectoryOnly))
		{
			string name = Path.GetFileName(rootFile);
			if (name.StartsWith("README", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase))
				files.Add(Path.GetFullPath(rootFile));
		}
		return files
			.Where(file => IsUnderDirectory(file, projectRoot))
			.Where(file => !IsUnderExcludedPackageDirectory(file, projectRoot))
			.OrderBy(static file => file, StringComparer.Ordinal)
			.ToList();
	}

	static bool IsUnderDirectory(string path, string directory)
	{
		string relative = Path.GetRelativePath(directory, path);
		return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
	}

	static bool IsUnderExcludedPackageDirectory(string path, string projectRoot)
	{
		string relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
		return relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
			|| relative.StartsWith("cache/", StringComparison.OrdinalIgnoreCase)
			|| relative.StartsWith("pub/", StringComparison.OrdinalIgnoreCase)
			|| relative.Equals("packages.ini", StringComparison.OrdinalIgnoreCase);
	}

	static bool TrySelectPublishVersion(string value, string outputDirectory, string packageName, out PackageSelectedVersion version, out string? error)
	{
		version = new PackageSelectedVersion(0, 0, 0);
		error = null;
		SortedDictionary<PackageSelectedVersion, PackageCatalogVersion> existing = [];
		string catalogPath = Path.Combine(outputDirectory, "versions.ini");
		if (File.Exists(catalogPath))
		{
			if (!PackageCatalog.TryParse(catalogPath, File.ReadAllText(catalogPath), out PackageCatalog? catalog, out List<string> errors))
			{
				error = string.Join(Environment.NewLine, errors);
				return false;
			}
			if (!catalog!.PackageName.Equals(packageName, StringComparison.Ordinal))
			{
				error = $"Existing catalog '{catalogPath}' declares package '{catalog.PackageName}', not '{packageName}'.";
				return false;
			}
			existing = catalog.Versions;
		}
		PackageSelectedVersion? latest = existing.Keys.Count == 0 ? null : existing.Keys.Max(PackageSelectedVersion.Comparer);
		if (value is not ("+major" or "+minor" or "+patch") && !PackageSelectedVersion.TryParse(value, out _, out string? versionError))
		{
			error = versionError;
			return false;
		}
		version = value switch
		{
			"+major" => latest is null ? new PackageSelectedVersion(1, 0, 0) : new PackageSelectedVersion(latest.Major + 1, 0, 0),
			"+minor" => latest is null ? new PackageSelectedVersion(0, 1, 0) : new PackageSelectedVersion(latest.Major, latest.Minor + 1, 0),
			"+patch" => latest is null ? new PackageSelectedVersion(0, 0, 1) : new PackageSelectedVersion(latest.Major, latest.Minor, latest.Patch + 1),
			_ => PackageSelectedVersion.Parse(value)
		};
		if (existing.ContainsKey(version))
		{
			error = $"Package version '{packageName}@{version}' already exists in '{catalogPath}'.";
			return false;
		}
		return true;
	}

	static bool TryPublish(PublishInputs inputs, PackageSelectedVersion version, out string? archiveName, out string? hash, out string? error)
	{
		archiveName = null;
		hash = null;
		error = null;
		Directory.CreateDirectory(inputs.OutputDirectory);
		archiveName = inputs.PackageName + "_" + version + ".zip";
		string archivePath = Path.Combine(inputs.OutputDirectory, archiveName);
		if (File.Exists(archivePath))
		{
			error = $"Package archive '{archivePath}' already exists.";
			return false;
		}
		byte[] archive = PackageArchive.CreateDeterministicZip(inputs.ProjectRoot, inputs.Files);
		hash = PackageArchive.Sha256Hex(archive);
		File.WriteAllBytes(archivePath, archive);

		string catalogPath = Path.Combine(inputs.OutputDirectory, "versions.ini");
		PackageCatalog catalog;
		if (File.Exists(catalogPath))
		{
			if (!PackageCatalog.TryParse(catalogPath, File.ReadAllText(catalogPath), out PackageCatalog? parsed, out List<string> errors))
			{
				error = string.Join(Environment.NewLine, errors);
				return false;
			}
			catalog = parsed!;
		}
		else
		{
			catalog = new PackageCatalog(inputs.PackageName, inputs.PackageName, new SortedDictionary<PackageSelectedVersion, PackageCatalogVersion>(PackageSelectedVersion.Comparer));
		}
		if (!catalog.PackageName.Equals(inputs.PackageName, StringComparison.Ordinal))
		{
			error = $"Existing catalog '{catalogPath}' declares package '{catalog.PackageName}', not '{inputs.PackageName}'.";
			return false;
		}
		catalog.Versions[version] = new PackageCatalogVersion(
			version,
			hash,
			archiveName,
			"campc/" + StripLeadingVersionPrefix(CampBuildInfo.Version),
			inputs.Dependencies.Select(static package => PackageDependencySpec.Parse(package.ToString())).ToList());
		File.WriteAllText(catalogPath, catalog.Write(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		return true;
	}

	static string StripLeadingVersionPrefix(string version)
	{
		return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) && version.Length > 1 && char.IsDigit(version[1])
			? version[1..]
			: version;
	}

	sealed record PublishRequest(string Version, string BuildFile, string? Name, string? OutputDirectory);
	sealed record PublishInputs(string PackageName, string ProjectRoot, string OutputDirectory, string BuildFile, IReadOnlyList<string> Files, IReadOnlyList<PackageSpec> Dependencies);

	static BuildOptionBag LoadEffectiveSources(CliEnvironment environment, string[] args, List<string> errors)
	{
		BuildOptionBag bag = new();
		foreach (PragmaLine pragma in BuildPragmaReader.Read(environment.GlobalCampPath, environment.WorkingDirectory, errors))
			bag.Apply(CommandLineOptionParser.Parse(pragma.Tokens, allowPositionals: false, errors), Precedence.Global, pragma.SourceName, errors);
		if (ReadOptionValue(args, "--local") is string localFile)
		{
			string fullPath = Path.GetFullPath(localFile, environment.WorkingDirectory);
			foreach (PragmaLine pragma in BuildPragmaReader.Read(fullPath, environment.WorkingDirectory, errors))
				bag.Apply(CommandLineOptionParser.Parse(pragma.Tokens, allowPositionals: false, errors), Precedence.Local, pragma.SourceName, errors);
		}
		return bag;
	}

	static bool TrySelectBuildFile(string[] args, CliEnvironment environment, out string? file, out string? error)
	{
		file = null;
		error = null;
		if (args.Contains("--global", StringComparer.Ordinal))
		{
			file = environment.GlobalCampPath;
			return true;
		}
		int local = Array.IndexOf(args, "--local");
		if (local >= 0)
		{
			if (local + 1 >= args.Length)
			{
				error = "--local requires <file.camp>.";
				return false;
			}
			file = Path.GetFullPath(args[local + 1], environment.WorkingDirectory);
			return true;
		}
		error = "Specify --global or --local <file.camp>.";
		return false;
	}

	static string? ReadOptionValue(string[] args, string name)
	{
		for (int i = 0; i + 1 < args.Length; i++)
			if (args[i] == name)
				return args[i + 1];
		return null;
	}

	static void EditBuildPragmas(string file, Func<string, bool> keep, string? addLine)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		List<string> lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
		lines = lines.Where(line => !line.TrimStart().StartsWith("#build ", StringComparison.Ordinal) || keep(line.Trim())).ToList();
		if (addLine is not null)
			lines.Insert(0, addLine);
		File.WriteAllLines(file, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}

	static void CopyDirectory(string source, string target)
	{
		if (Directory.Exists(target))
			Directory.Delete(target, recursive: true);
		foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
			Directory.CreateDirectory(directory.Replace(source, target, StringComparison.Ordinal));
		Directory.CreateDirectory(target);
		foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
		{
			string destination = file.Replace(source, target, StringComparison.Ordinal);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Copy(file, destination, overwrite: true);
		}
	}

	static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;
	static int PrintErrors(IEnumerable<string> errors) { foreach (string error in errors) Console.Error.WriteLine(error); return 1; }
	static int Error(string message) { Console.Error.WriteLine(message); return 1; }
}

sealed class BuildOptionBag
{
	readonly Dictionary<string, SingleValue> singleValues = new(StringComparer.Ordinal);
	public List<string> ApiPatterns { get; } = [];
	public List<string> ExcludePatterns { get; } = [];
	public List<string> Defines { get; } = [];
	public List<string> ConfigurationFlagDeclarations { get; } = [];
	public List<string> ConfigurationFlagConfigurations { get; } = [];
	public List<string> ConfigurationRequirements { get; } = [];
	public List<string> References { get; } = [];
	public List<string> Frameworks { get; } = [];
	public List<string> Variants { get; } = [];
	public List<PackageSourceSpec> UseSources { get; } = [];
	public List<PackageSpec> UsePackages { get; } = [];
	public List<string> ProjectReferences { get; } = [];
	public List<string> SourcefileRoots { get; } = [];
	public List<string> TestFilters { get; } = [];
	public List<string> CoverageSubjects { get; } = [];
	public bool NoStdLib { get; private set; }
	public bool ArtifactSpecified { get; private set; }
	public NativeBuildKind? ArtifactKind { get; private set; }
	public DependencyLinkKind? ArtifactRestriction { get; private set; }
	Precedence? artifactPrecedence;
	string? artifactSource;
	Precedence? variantPrecedence;

	public string? TargetName => Get("target");
	public string? ProfileName => Get("profile");
	public string? EmitKind => Get("emit");
	public string? OutDir => Get("out-dir");
	public string? ProjectName => Get("name");
	public string? SubsystemName => Get("subsystem");
	public MetadataVisibility? MetadataVisibility => Get("metadata") is string value ? ParseMetadata(value) : null;
	public string? TestOutputDir => Get("test-output-dir");
	public string? TestResultFormat => Get("test-result-format");
	public string? CoverageOutputDir => Get("coverage-output-dir");
	public string? CoverageFormat => Get("coverage-format");
	public bool ListTests => Get("list") == "true";
	public bool IgnoreLeaks => Get("ignore-leaks") == "true";
	public bool Verbose => Get("verbose") == "true";
	public bool TimingEnabled => Get("timing") == "true" || Environment.GetEnvironmentVariable("CAMP_TIMING") is string timing && timing is not "" and not "0" and not "false" and not "FALSE";
	public string? TimingOutput => Get("timing-output");
	public SourcefilePathMode SourcefilePathMode => Get("sourcefile-paths") switch
	{
		"absolute" => SourcefilePathMode.Absolute,
		_ => SourcefilePathMode.Relative
	};
	public WithinAllocationPolicy? WithinAllocationPolicy => Get("within") switch
	{
		"explicit" => Camp.Compiler.WithinAllocationPolicy.Explicit,
		"implicit" => Camp.Compiler.WithinAllocationPolicy.Implicit,
		_ => null
	};
	public ConfigurationRequirementPolicy? ConfigurationRequirementPolicy => Get("require-policy") switch
	{
		"explicit" => Camp.Compiler.ConfigurationRequirementPolicy.Explicit,
		"implicit" => Camp.Compiler.ConfigurationRequirementPolicy.Implicit,
		_ => null
	};
	public bool DebugInfo => Get("debug-info") == "true";
	public bool HasBuildOnlyOptions => Frameworks.Count > 0 || ProjectReferences.Count > 0 || ArtifactSpecified || Get("name") is not null || Get("subsystem") is not null || Get("out-dir") is not null || DebugInfo || TimingEnabled || TimingOutput is not null;
	public bool HasTestResultOptions => Get("test-output-dir") is not null || Get("test-result-format") is not null;
	public bool HasCoverageOptions => Get("coverage-output-dir") is not null || Get("coverage-format") is not null || CoverageSubjects.Count > 0;

	public void Apply(ParsedOptions options, Precedence precedence, string source, List<string> errors)
	{
		foreach ((string key, string value) in options.SingleValues)
			SetSingle(key, value, precedence, source, errors);
		foreach (string pattern in options.ApiPatterns)
			ApiPatterns.Add(pattern);
		foreach (string pattern in options.ExcludePatterns)
			ExcludePatterns.Add(pattern);
		Defines.AddRange(options.Defines);
		ConfigurationFlagDeclarations.AddRange(options.ConfigurationFlagDeclarations);
		ConfigurationFlagConfigurations.AddRange(options.ConfigurationFlagConfigurations);
		ConfigurationRequirements.AddRange(options.ConfigurationRequirements);
		References.AddRange(options.References);
		Frameworks.AddRange(options.Frameworks);
		SourcefileRoots.AddRange(options.SourcefileRoots);
		TestFilters.AddRange(options.TestFilters);
		CoverageSubjects.AddRange(options.CoverageSubjects);
		AddVariants(options.Variants, precedence);
		UseSources.AddRange(options.UseSources);
		UsePackages.AddRange(options.UsePackages);
		ProjectReferences.AddRange(options.ProjectReferences);
		if (options.NoStdLib)
			NoStdLib = true;
		if (options.ArtifactSpecified)
			SetArtifact(options.ArtifactKind, options.ArtifactRestriction, precedence, source, errors);
	}

	public void SetArtifact(NativeBuildKind? kind, string source, List<string> errors)
	{
		SetArtifact(kind, null, Precedence.CommandLine, source, errors);
	}

	void SetArtifact(NativeBuildKind? kind, DependencyLinkKind? restriction, Precedence precedence, string source, List<string> errors)
	{
		if (artifactPrecedence is Precedence existingPrecedence)
		{
			if (existingPrecedence == precedence && ArtifactKind != kind)
				errors.Add($"{source}: --artifact conflicts with --artifact from {artifactSource}.");
			if (existingPrecedence > precedence)
				return;
		}
		ArtifactSpecified = true;
		ArtifactKind = kind;
		ArtifactRestriction = restriction;
		artifactPrecedence = precedence;
		artifactSource = source;
	}

	void SetSingle(string key, string value, Precedence precedence, string source, List<string> errors)
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

	void AddVariants(IReadOnlyList<string> variants, Precedence precedence)
	{
		if (variants.Count == 0)
			return;
		if (variantPrecedence is Precedence existing && existing < precedence)
			Variants.Clear();
		if (variantPrecedence is null || variantPrecedence <= precedence)
		{
			Variants.AddRange(variants);
			variantPrecedence = precedence;
		}
	}

	string? Get(string key) => singleValues.TryGetValue(key, out SingleValue value) ? value.Value : null;

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

	readonly record struct SingleValue(string Value, Precedence Precedence, string Source);
}

sealed class ParsedOptions
{
	public List<string> Positionals { get; } = [];
	public List<(string Key, string Value)> SingleValues { get; } = [];
	public List<string> ApiPatterns { get; } = [];
	public List<string> ExcludePatterns { get; } = [];
	public List<string> Defines { get; } = [];
	public List<string> ConfigurationFlagDeclarations { get; } = [];
	public List<string> ConfigurationFlagConfigurations { get; } = [];
	public List<string> ConfigurationRequirements { get; } = [];
	public List<string> References { get; } = [];
	public List<string> Frameworks { get; } = [];
	public List<string> Variants { get; } = [];
	public List<PackageSourceSpec> UseSources { get; } = [];
	public List<PackageSpec> UsePackages { get; } = [];
	public List<string> ProjectReferences { get; } = [];
	public List<string> SourcefileRoots { get; } = [];
	public List<string> TestFilters { get; } = [];
	public List<string> CoverageSubjects { get; } = [];
	public bool NoStdLib { get; set; }
	public bool ArtifactSpecified { get; set; }
	public NativeBuildKind? ArtifactKind { get; set; }
	public DependencyLinkKind? ArtifactRestriction { get; set; }
}

static class CommandLineOptionParser
{
	public static ParsedOptions Parse(IReadOnlyList<string> tokens, bool allowPositionals, List<string> errors)
	{
		ParsedOptions result = new();
		for (int i = 0; i < tokens.Count; i++)
		{
			string token = tokens[i];
			switch (token)
			{
				case "--inspect":
					errors.Add("--inspect has been replaced by 'dump <kind>'.");
					i += HasValue(tokens, i) ? 1 : 0;
					break;
				case "--build":
				case "-b":
					errors.Add("--build/-b has been replaced by --artifact.");
					i += HasValue(tokens, i) ? 1 : 0;
					break;
				case "--emit-metadata":
					errors.Add("--emit-metadata has been replaced by --metadata.");
					i += HasValue(tokens, i) ? 1 : 0;
					break;
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
					result.Variants.AddRange(RequiredValues(tokens, ref i, token, errors));
					break;
				case "--verbose":
				case "-v":
					AddSingle(result, "verbose", "true");
					break;
				case "--timing":
					AddSingle(result, "timing", "true");
					break;
				case "--timing-output":
					AddSingle(result, "timing-output", PathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--emit":
					AddSingle(result, "emit", RequiredValue(tokens, ref i, token, errors));
					break;
				case "--debug-info":
					AddSingle(result, "debug-info", "true");
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
				case "--explicit-require":
					AddSingle(result, "require-policy", "explicit");
					break;
				case "--implicit-require":
					AddSingle(result, "require-policy", "implicit");
					break;
				case "--artifact":
					string artifact = RequiredValue(tokens, ref i, token, errors);
					result.ArtifactSpecified = true;
					result.ArtifactRestriction = artifact switch
					{
						"only-static" => DependencyLinkKind.Static,
						"only-shared" => DependencyLinkKind.Shared,
						_ => null
					};
					result.ArtifactKind = artifact switch
					{
						"none" => null,
						"exec" => NativeBuildKind.Exec,
						"static" => NativeBuildKind.Static,
						"shared" => NativeBuildKind.Shared,
						"only-static" => NativeBuildKind.Static,
						"only-shared" => NativeBuildKind.Shared,
						"winexe" => InvalidArtifact("winexe has been removed. Use --artifact exec --subsystem windows.", errors),
						_ => InvalidArtifact("--artifact expects exec, static, shared, only-static, only-shared, or none.", errors)
					};
					break;
				case "--name":
					AddSingle(result, "name", RequiredValue(tokens, ref i, token, errors));
					break;
				case "--subsystem":
					AddSingle(result, "subsystem", RequiredValue(tokens, ref i, token, errors).ToLowerInvariant());
					break;
				case "--out-dir":
					AddSingle(result, "out-dir", PathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--sourcefile-paths":
					string sourcefilePaths = RequiredValue(tokens, ref i, token, errors);
					if (sourcefilePaths is not ("relative" or "absolute"))
						errors.Add("--sourcefile-paths expects relative or absolute.");
					AddSingle(result, "sourcefile-paths", sourcefilePaths);
					break;
				case "--sourcefile-root":
					result.SourcefileRoots.Add(PathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--test-output-dir":
					AddSingle(result, "test-output-dir", PathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--test-result-format":
					string testResultFormat = RequiredValue(tokens, ref i, token, errors);
					if (testResultFormat is not ("text" or "json" or "text,json"))
						errors.Add("--test-result-format expects text, json, or text,json.");
					AddSingle(result, "test-result-format", testResultFormat);
					break;
				case "--coverage-output-dir":
					AddSingle(result, "coverage-output-dir", PathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--coverage-format":
					string coverageFormat = RequiredValue(tokens, ref i, token, errors);
					if (coverageFormat is not ("json" or "lcov" or "json,lcov"))
						errors.Add("--coverage-format expects json, lcov, or json,lcov.");
					AddSingle(result, "coverage-format", coverageFormat);
					break;
				case "--coverage-subject":
					result.CoverageSubjects.Add(RequiredValue(tokens, ref i, token, errors));
					break;
				case "--list":
					AddSingle(result, "list", "true");
					break;
				case "--ignore-leaks":
					AddSingle(result, "ignore-leaks", "true");
					break;
				case "--filter":
					result.TestFilters.AddRange(RequiredValues(tokens, ref i, token, errors));
					break;
				case "--build-dir":
					errors.Add("--build-dir has been removed. Build intermediates are written to the output artifact directory's build subdirectory.");
					i += HasValue(tokens, i) ? 1 : 0;
					break;
				case "--api":
					result.ApiPatterns.Add(PathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--exclude":
					result.ExcludePatterns.Add(PathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--define":
					result.Defines.Add(RequiredValue(tokens, ref i, token, errors));
					break;
				case "--declare":
				case "-d":
					result.ConfigurationFlagDeclarations.Add(RequiredValue(tokens, ref i, token, errors));
					break;
				case "--configure":
				case "-c":
					result.ConfigurationFlagConfigurations.Add(RequiredValue(tokens, ref i, token, errors));
					break;
				case "--require":
					result.ConfigurationRequirements.Add(RequiredValue(tokens, ref i, token, errors));
					break;
				case "--reference":
				case "-r":
					result.References.AddRange(RequiredValues(tokens, ref i, token, errors).Select(PathArguments.NormalizeIfPathLike));
					break;
				case "--framework":
				case "-f":
					result.Frameworks.AddRange(RequiredValues(tokens, ref i, token, errors));
					break;
				case "--use":
				case "-u":
					result.UsePackages.Add(PackageSpec.Parse(RequiredValue(tokens, ref i, token, errors), errors));
					break;
				case "--project-reference":
					string projectReference = PathArguments.Normalize(RequiredValue(tokens, ref i, token, errors));
					ProjectReferenceSpec.Parse(projectReference, errors);
					result.ProjectReferences.Add(projectReference);
					break;
				case "--use-source":
					string name = RequiredValue(tokens, ref i, token, errors);
					string? path = null;
					if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-", StringComparison.Ordinal))
						path = PathArguments.Normalize(tokens[++i]);
					result.UseSources.Add(new PackageSourceSpec(name, path));
					break;
				case "--nostdlib":
					result.NoStdLib = true;
					break;
				default:
					if (token.StartsWith("-", StringComparison.Ordinal))
						errors.Add($"Unknown option '{token}'.");
					else if (allowPositionals)
						result.Positionals.Add(PathArguments.Normalize(token));
					else
						errors.Add($"Unexpected build pragma argument '{token}'.");
					break;
			}
		}
		return result;
	}

	static bool HasValue(IReadOnlyList<string> tokens, int index) => index + 1 < tokens.Count && !tokens[index + 1].StartsWith("-", StringComparison.Ordinal);
	static void AddSingle(ParsedOptions options, string key, string value) { if (!string.IsNullOrEmpty(value)) options.SingleValues.Add((key, value)); }
	static List<string> RequiredValues(IReadOnlyList<string> tokens, ref int index, string option, List<string> errors)
	{
		List<string> values = [];
		while (index + 1 < tokens.Count && !tokens[index + 1].StartsWith("-", StringComparison.Ordinal))
		{
			index++;
			values.Add(tokens[index]);
		}
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
	static NativeBuildKind? InvalidArtifact(string message, List<string> errors) { errors.Add(message); return null; }
}

static class PathArguments
{
	public static string Normalize(string value)
	{
		return OperatingSystem.IsWindows()
			? value.Replace('/', Path.DirectorySeparatorChar)
			: value;
	}

	public static string NormalizeIfPathLike(string value)
	{
		return LooksLikePath(value) ? Normalize(value) : value;
	}

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

static class BuildPragmaReader
{
	public static IEnumerable<PragmaLine> Read(string file, string workingDirectory, List<string> errors)
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
				yield return new PragmaLine(Split(trimmed["#build".Length..]), $"{Path.GetRelativePath(workingDirectory, fullPath)}:{lineNumber}");
				continue;
			}
			if (trimmed.StartsWith("#within", StringComparison.Ordinal))
				continue;
			if (IsPreludeTrivia(trimmed))
				continue;
			beforeCode = false;
		}
	}

	static bool IsPreludeTrivia(string trimmed)
	{
		return trimmed.Length == 0
			|| trimmed.StartsWith("//", StringComparison.Ordinal)
			|| trimmed.StartsWith("/*", StringComparison.Ordinal)
			|| trimmed.StartsWith("*", StringComparison.Ordinal)
			|| trimmed.StartsWith("*/", StringComparison.Ordinal);
	}

	static List<string> Split(string text)
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

static class ResponseFileExpander
{
	static readonly HashSet<string> PathValueOptions = new(StringComparer.Ordinal)
	{
		"--api",
		"--exclude",
		"--out-dir",
		"--build-dir",
		"--sourcefile-root",
		"--test-output-dir",
		"--coverage-output-dir",
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
			if (token is "--reference" or "-r" or "--framework" or "-f")
				return true;
			return index - i <= OptionValueCount(token);
		}
		return false;
	}

	static int OptionValueCount(string option)
	{
		return option switch
		{
			"--target" or "-t" or "--profile" or "-p" or "--variant" or "--memory-model" or "--emit" or "--metadata" or "--artifact" or "--name" or "--subsystem" or "--out-dir" or "--build-dir" or "--sourcefile-paths" or "--sourcefile-root" or "--test-output-dir" or "--test-result-format" or "--coverage-output-dir" or "--coverage-format" or "--coverage-subject" or "--filter" or "--api" or "--exclude" or "--define" or "-d" or "--use" or "-u" or "--project-reference" => 1,
			"--use-source" => 2,
			_ => 0
		};
	}

	static List<string> Expand(IReadOnlyList<string> args, string workingDirectory, List<string> errors, HashSet<string> responseStack)
	{
		List<string> expanded = [];
		foreach (string arg in args)
		{
			if (!arg.StartsWith('@') || arg == "@")
			{
				expanded.Add(arg);
				continue;
			}

			string responseFile = ResolveResponseFile(arg[1..], workingDirectory);
			if (!File.Exists(responseFile))
			{
				errors.Add($"Response file '{arg[1..]}' could not be found.");
				continue;
			}
			if (!responseStack.Add(responseFile))
			{
				errors.Add($"Response file '{responseFile}' includes itself recursively.");
				continue;
			}

			string responseDirectory = Path.GetDirectoryName(responseFile)!;
			List<string> tokens = TokenizeResponseFile(responseFile, errors);
			expanded.AddRange(RebasePathArguments(Expand(tokens, responseDirectory, errors, responseStack), responseDirectory));
			responseStack.Remove(responseFile);
		}
		return expanded;
	}

	static string ResolveResponseFile(string value, string workingDirectory)
	{
		string candidate = Path.GetFullPath(value, workingDirectory);
		if (File.Exists(candidate) || Path.HasExtension(candidate))
			return candidate;
		string campbuild = candidate + ".campbuild";
		if (File.Exists(campbuild))
			return campbuild;
		return candidate;
	}

	static List<string> TokenizeResponseFile(string file, List<string> errors)
	{
		try
		{
			return Split(File.ReadAllText(file));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			errors.Add($"{file}: {ex.Message}");
			return [];
		}
	}

	static List<string> RebasePathArguments(IReadOnlyList<string> tokens, string baseDirectory)
	{
		List<string> result = [];
		for (int i = 0; i < tokens.Count; i++)
		{
			string token = tokens[i];
			result.Add(token);

			if (token is "--reference" or "-r" or "--framework" or "-f")
			{
				while (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-", StringComparison.Ordinal))
					result.Add(RebaseReferenceLikeValue(tokens[++i], baseDirectory));
				continue;
			}

			if (token == "--project-reference")
			{
				while (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-", StringComparison.Ordinal))
					result.Add(RebaseProjectReferenceValue(tokens[++i], baseDirectory));
				continue;
			}

			if (token == "--use-source")
			{
				if (i + 1 < tokens.Count)
					result.Add(tokens[++i]);
				if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-", StringComparison.Ordinal))
					result.Add(RebasePathValue(tokens[++i], baseDirectory));
				continue;
			}

			if (PathValueOptions.Contains(token) && i + 1 < tokens.Count)
			{
				result.Add(RebasePathValue(tokens[++i], baseDirectory));
				continue;
			}

			if (!token.StartsWith("-", StringComparison.Ordinal))
				result[^1] = RebaseSourcePattern(token, baseDirectory);
		}
		return result;
	}

	static string RebaseSourcePattern(string value, string baseDirectory)
	{
		value = PathArguments.Normalize(value);
		if (Path.IsPathRooted(value) || !PathArguments.LooksLikePath(value))
			return value;
		return Path.GetFullPath(value, baseDirectory);
	}

	static string RebaseReferenceLikeValue(string value, string baseDirectory)
	{
		value = PathArguments.NormalizeIfPathLike(value);
		if (Path.IsPathRooted(value) || !PathArguments.LooksLikePath(value))
			return value;
		if (!value.Contains(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) && !value.Contains(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal) && !value.StartsWith(".", StringComparison.Ordinal))
			return value;
		return Path.GetFullPath(value, baseDirectory);
	}

	static string RebasePathValue(string value, string baseDirectory)
	{
		value = PathArguments.Normalize(value);
		if (IsDirectOutputPath(value))
		{
			string prefix = value[..^1];
			string rebased = Path.IsPathRooted(prefix) ? prefix : Path.GetFullPath(prefix, baseDirectory);
			return Path.Combine(rebased, ".");
		}
		return Path.IsPathRooted(value) ? value : Path.GetFullPath(value, baseDirectory);
	}

	static string RebaseProjectReferenceValue(string value, string baseDirectory)
	{
		ProjectReferenceSpec spec = ProjectReferenceSpec.Parse(PathArguments.Normalize(value));
		string rebased = RebasePathValue(spec.Path, baseDirectory);
		return spec.LinkKind is null ? rebased : rebased + ":" + spec.LinkKind.ToString()!.ToLowerInvariant();
	}

	static bool IsDirectOutputPath(string value)
	{
		string normalized = value.Replace('\\', '/');
		return normalized == "." || normalized.EndsWith("/.", StringComparison.Ordinal);
	}

	static List<string> Split(string text)
	{
		List<string> tokens = [];
		StringBuilder current = new();
		bool inQuote = false;
		bool atTokenStart = true;
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
			if (ch == '#' && atTokenStart)
			{
				while (i < text.Length && text[i] is not '\r' and not '\n')
					i++;
				atTokenStart = true;
				continue;
			}
			if (char.IsWhiteSpace(ch))
			{
				if (current.Length > 0)
				{
					tokens.Add(current.ToString());
					current.Clear();
				}
				atTokenStart = true;
			}
			else if (ch == '"')
			{
				inQuote = true;
				atTokenStart = false;
			}
			else
			{
				current.Append(ch);
				atTokenStart = false;
			}
		}
		if (current.Length > 0)
			tokens.Add(current.ToString());
		return tokens;
	}
}

static class Glob
{
	public static IEnumerable<string> Expand(string pattern, string workingDirectory)
	{
		string fullPattern = Path.GetFullPath(pattern, workingDirectory);
		if (!HasWildcards(pattern))
		{
			if (File.Exists(fullPattern))
				yield return fullPattern;
			yield break;
		}

		string root = GetSearchRoot(fullPattern);
		if (!Directory.Exists(root))
			yield break;
		string relativePattern = Normalize(Path.GetRelativePath(root, fullPattern));
		foreach (string file in Directory.GetFiles(root, "*.camp", SearchOption.AllDirectories))
			if (IsMatch(Normalize(Path.GetRelativePath(root, file)), relativePattern))
				yield return file;
	}

	public static bool IsMatch(string path, string pattern)
	{
		return Regex.IsMatch(Normalize(path), "^" + GlobRegex(Normalize(pattern)) + "$", RegexOptions.CultureInvariant);
	}

	public static bool HasWildcards(string pattern) => pattern.IndexOfAny(['*', '?', '[']) >= 0;
	static string GetSearchRoot(string fullPattern)
	{
		int wildcard = fullPattern.IndexOfAny(['*', '?', '[']);
		string prefix = wildcard < 0 ? fullPattern : fullPattern[..wildcard];
		string? directory = Directory.Exists(prefix) ? prefix : Path.GetDirectoryName(prefix);
		while (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
			directory = Path.GetDirectoryName(directory);
		return string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : directory;
	}
	static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
	static string GlobRegex(string pattern)
	{
		return Regex.Escape(pattern)
			.Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
			.Replace("\\*\\*", ".*", StringComparison.Ordinal)
			.Replace("\\*", "[^/]*", StringComparison.Ordinal)
			.Replace("\\?", "[^/]", StringComparison.Ordinal);
	}
}

sealed record PragmaLine(IReadOnlyList<string> Tokens, string SourceName);
sealed record PackageSourceSpec(string Name, string? Path);

sealed record PackageSpec(string Name, string? Version, DependencyLinkKind? LinkKind = null)
{
	public static PackageSpec Parse(string value, List<string>? errors = null)
	{
		DependencyLinkKind? linkKind = null;
		int colon = value.LastIndexOf(':');
		if (colon >= 0)
		{
			string suffix = value[(colon + 1)..];
			if (suffix.Equals("static", StringComparison.OrdinalIgnoreCase) || suffix.Equals("shared", StringComparison.OrdinalIgnoreCase) || suffix.Equals("api", StringComparison.OrdinalIgnoreCase))
			{
				linkKind = suffix.ToLowerInvariant() switch
				{
					"shared" => DependencyLinkKind.Shared,
					"static" => DependencyLinkKind.Static,
					"api" => DependencyLinkKind.Api,
					_ => linkKind
				};
				value = value[..colon];
			}
			else if (!string.IsNullOrWhiteSpace(suffix))
			{
				errors?.Add($"Package dependency kind ':{suffix}' is not valid. Expected :api, :static, or :shared.");
			}
		}
		string[] parts = value.Split('@', 2);
		return new PackageSpec(parts[0], parts.Length == 2 && parts[1].Length > 0 ? parts[1] : null, linkKind);
	}
	public override string ToString()
	{
		string identity = Version is null ? Name : Name + "@" + Version;
		return LinkKind is null ? identity : identity + ":" + LinkKind.ToString()!.ToLowerInvariant();
	}
}

sealed record ProjectReferenceSpec(string Path, DependencyLinkKind? LinkKind)
{
	public static ProjectReferenceSpec Parse(string value, List<string>? errors = null)
	{
		int colon = value.LastIndexOf(':');
		if (colon >= 0)
		{
			string suffix = value[(colon + 1)..];
			if (suffix.Equals("static", StringComparison.OrdinalIgnoreCase) || suffix.Equals("shared", StringComparison.OrdinalIgnoreCase))
				return new ProjectReferenceSpec(value[..colon], suffix.ToLowerInvariant() switch
				{
					"shared" => DependencyLinkKind.Shared,
					"static" => DependencyLinkKind.Static,
					_ => null
				});
			if (LooksLikeDependencyKindSuffix(value, colon))
				errors?.Add($"Project reference dependency kind ':{suffix}' is not valid. Expected :static or :shared.");
		}
		return new ProjectReferenceSpec(value, null);
	}

	static bool LooksLikeDependencyKindSuffix(string value, int colon)
	{
		if (colon == 1 && char.IsAsciiLetter(value[0]))
			return false;
		string suffix = value[(colon + 1)..];
		return suffix.Length > 0 && suffix.IndexOfAny([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar, '/', '\\']) < 0;
	}
}

sealed record SemVersion(int Major, int Minor, int Patch, string? Suffix) : IComparable<SemVersion>
{
	public static IComparer<SemVersion> Comparer { get; } = Comparer<SemVersion>.Create(static (left, right) => left.CompareTo(right));
	public static SemVersion Parse(string value)
	{
		string[] suffixParts = value.Split('-', 2);
		string[] parts = suffixParts[0].Split('.');
		return new SemVersion(ParsePart(parts, 0), ParsePart(parts, 1), ParsePart(parts, 2), suffixParts.Length == 2 ? suffixParts[1] : null);
	}
	public int CompareTo(SemVersion? other)
	{
		if (other is null)
			return 1;
		int major = Major.CompareTo(other.Major);
		if (major != 0) return major;
		int minor = Minor.CompareTo(other.Minor);
		if (minor != 0) return minor;
		int patch = Patch.CompareTo(other.Patch);
		if (patch != 0) return patch;
		if (Suffix is null && other.Suffix is not null) return 1;
		if (Suffix is not null && other.Suffix is null) return -1;
		return string.Compare(Suffix, other.Suffix, StringComparison.Ordinal);
	}
	static int ParsePart(string[] parts, int index) => index < parts.Length && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;
}

sealed class CliEnvironment
{
	public required string WorkingDirectory { get; init; }
	public required string RuntimeRoot { get; init; }
	public required string HomeDirectory { get; init; }
	public string GlobalCampPath => Path.Combine(HomeDirectory, "lib", "global.camp");
	public string GlobalPackageRoot => Path.Combine(HomeDirectory, "cache", "pkg");
	public string LocalPackageRoot => Path.Combine(WorkingDirectory, "cache", "pkg");

	public static CliEnvironment Create()
	{
		string workingDirectory = Directory.GetCurrentDirectory();
		CampRuntimeLayout layout = CampRuntimeLayout.Resolve(workingDirectory);
		return new CliEnvironment
		{
			WorkingDirectory = workingDirectory,
			RuntimeRoot = layout.BinDirectory,
			HomeDirectory = layout.HomeDirectory
		};
	}
}

enum CommandKind
{
	Build,
	Run,
	Dump,
	Test,
	Cover
}

enum Precedence
{
	Global = 0,
	Local = 1,
	CommandLine = 2
}
