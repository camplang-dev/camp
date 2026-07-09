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
RootCommand rootCommand = BuildCommandTree(environment, expandedArgs);
int exitCode = ContainsRemovedOption(expandedArgs) ? CampCli.Run(expandedArgs, environment) : rootCommand.Parse(expandedArgs).Invoke();
return exitCode;

static bool ContainsRemovedOption(string[] args)
{
	return args.Any(static arg => arg is "--inspect" or "--build" or "-b" or "--emit-metadata" or "--memory-model" or "--build-dir");
}

static RootCommand BuildCommandTree(CliEnvironment environment, string[] originalArgs)
{
	RootCommand root = new("Camp compiler");
	root.SetAction(_ => CampCli.Run(originalArgs, environment));

	Command init = new("init", "Initialize a Camp project.");
	init.SetAction(_ => CampCli.Run(originalArgs, environment));
	root.Subcommands.Add(init);

	Command build = new("build", "Compile, emit C, and optionally build a native artifact.");
	build.Arguments.Add(SourcePatternsArgument());
	AddBuildOptions(build, buildOnly: true);
	build.SetAction(_ => CampCli.Run(originalArgs, environment));
	root.Subcommands.Add(build);

	Command run = new("run", "Build an executable and run it.");
	run.Arguments.Add(SourcePatternsArgument());
	AddBuildOptions(run, buildOnly: true);
	run.SetAction(_ => CampCli.Run(originalArgs, environment));
	root.Subcommands.Add(run);

	Command dump = new("dump", "Print compiler intermediate output.");
	dump.Arguments.Add(new Argument<string>("kind")
	{
		Description = "Dump kind: tokens, cst, ast, declarations, lowering, or metadata."
	});
	dump.Arguments.Add(SourcePatternsArgument());
	AddBuildOptions(dump, buildOnly: false);
	dump.SetAction(_ => CampCli.Run(originalArgs, environment));
	root.Subcommands.Add(dump);

	Command restore = new("restore", "Install missing packages used by source files.");
	restore.Arguments.Add(SourcePatternsArgument());
	restore.SetAction(_ => CampCli.Run(originalArgs, environment));
	root.Subcommands.Add(restore);

	Command pkg = new("pkg", "Manage package sources and package dependencies.");
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

static void AddBuildOptions(Command command, bool buildOnly)
{
	command.Options.Add(new Option<List<string>>("--include", "-i")
	{
		Description = "Include Camp API header files or source patterns.",
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
	command.Options.Add(new Option<List<string>>("--variant", "-v")
	{
		Description = "Select target variants.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});
	command.Options.Add(new Option<List<string>>("--define", "-d")
	{
		Description = "Define conditional compilation symbols.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});
	command.Options.Add(new Option<string?>("--emit") { Description = "Select the emitter, currently c99." });
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
	command.Options.Add(new Option<string>("--project-reference")
	{
		Description = "Build and reference another Camp project response file.",
		Arity = ArgumentArity.ExactlyOne,
		AllowMultipleArgumentsPerToken = false
	});
	command.Options.Add(new Option<string?>("--metadata") { Description = "Emit metadata: none, export, public, or all." });
	command.Options.Add(new Option<bool>("--explicit-within") { Description = "Require source-level new/delete to use an explicit within context." });
	command.Options.Add(new Option<bool>("--implicit-within") { Description = "Allow source-level new/delete to use the default allocator without an explicit within context." });
	command.Options.Add(new Option<bool>("--xml") { Description = "Use XML output for declarations or lowering dumps." });

	if (!buildOnly)
		return;

	command.Options.Add(new Option<List<string>>("--framework", "-f")
	{
		Description = "Link a native framework during native builds.",
		Arity = ArgumentArity.ZeroOrMore,
		AllowMultipleArgumentsPerToken = true
	});
	command.Options.Add(new Option<string?>("--artifact") { Description = "Native artifact: exec, static, shared, or none." });
	command.Options.Add(new Option<string?>("--name") { Description = "Artifact/project name without extension." });
	command.Options.Add(new Option<string?>("--subsystem") { Description = "Native subsystem, currently windows." });
	command.Options.Add(new Option<string?>("--out-dir") { Description = "Directory for final artifacts." });
}

static void AddPackageCommands(Command pkg, string[] originalArgs, CliEnvironment environment)
{
	Command addSource = new("add-source", "Add a package source to global.camp or a source file.");
	addSource.Arguments.Add(new Argument<string>("name"));
	addSource.Arguments.Add(new Argument<string>("local-folder"));
	addSource.Options.Add(new Option<string?>("--local") { Description = "Source file to edit." });
	addSource.Options.Add(new Option<bool>("--global") { Description = "Edit lib/global.camp." });
	addSource.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(addSource);

	Command removeSource = new("remove-source", "Remove a package source.");
	removeSource.Arguments.Add(new Argument<string>("name"));
	removeSource.Options.Add(new Option<string?>("--local") { Description = "Source file to edit." });
	removeSource.Options.Add(new Option<bool>("--global") { Description = "Edit lib/global.camp." });
	removeSource.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(removeSource);

	Command search = new("search", "Search configured package sources.");
	search.Arguments.Add(new Argument<string>("pkg"));
	search.Options.Add(new Option<string?>("--source") { Description = "Restrict search to a named source." });
	search.Options.Add(new Option<string?>("--local") { Description = "Also read sources from a local file." });
	search.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(search);

	Command install = new("install", "Install a package from configured sources.");
	install.Arguments.Add(new Argument<string>("pkg@ver"));
	install.Options.Add(new Option<bool>("--global") { Description = "Install into the compiler package root." });
	install.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(install);

	Command uninstall = new("uninstall", "Uninstall a package.");
	uninstall.Arguments.Add(new Argument<string>("pkg@ver"));
	uninstall.Options.Add(new Option<bool>("--global") { Description = "Remove from the compiler package root." });
	uninstall.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(uninstall);

	Command add = new("add", "Add a package use pragma to a source file.");
	add.Arguments.Add(new Argument<string>("pkg@ver"));
	add.Arguments.Add(new Argument<string>("file.camp"));
	add.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(add);

	Command remove = new("remove", "Remove a package use pragma from a source file.");
	remove.Arguments.Add(new Argument<string>("pkg"));
	remove.Arguments.Add(new Argument<string>("file.camp"));
	remove.SetAction(_ => CampCli.Run(originalArgs, environment));
	pkg.Subcommands.Add(remove);
}

sealed class CampCli
{
	public static int Run(string[] args, CliEnvironment environment)
	{
		if (args.Length == 0)
			return Error("A command is required. Expected init, pkg, restore, build, dump, or run.");

		return args[0] switch
		{
			"init" => Error("init is not implemented yet."),
			"build" => RunBuild(args[1..], environment),
			"run" => RunRun(args[1..], environment),
			"dump" => RunDump(args[1..], environment),
			"restore" => RunRestore(args[1..], environment),
			"pkg" => PackageCommands.Run(args[1..], environment),
			"--inspect" or "--build" or "-b" => Error("The root compiler command has been replaced by subcommands. Use 'campc dump ...' or 'campc build ...'."),
			_ when args[0].StartsWith("-", StringComparison.Ordinal) => Error($"Unknown command '{args[0]}'. Use init, pkg, restore, build, dump, or run."),
			_ => Error($"Unknown command '{args[0]}'. Use init, pkg, restore, build, dump, or run.")
		};
	}

	static int RunBuild(string[] args, CliEnvironment environment)
	{
		if (!TryBuildRequest(args, environment, CommandKind.Build, out CompilerRequest? request, out List<string> errors))
			return PrintErrors(errors);

		CompilerResult result = CompilerDriver.Execute(request!);
		Console.Out.Write(result.StdOut);
		Console.Error.Write(result.StdErr);
		return result.ExitCode;
	}

	static int RunRun(string[] args, CliEnvironment environment)
	{
		int separator = Array.IndexOf(args, "--");
		string[] buildArgs = separator >= 0 ? args[..separator] : args;
		string[] programArgs = separator >= 0 ? args[(separator + 1)..] : [];

		if (!TryBuildRequest(buildArgs, environment, CommandKind.Run, out CompilerRequest? request, out List<string> errors))
			return PrintErrors(errors);

		if (request!.BuildKind is not (NativeBuildKind.Exec or NativeBuildKind.WinExe))
			return Error("run requires --artifact exec.");

		CompilerResult result = CompilerDriver.Execute(request);
		Console.Error.Write(result.StdErr);
		if (result.ExitCode != 0)
		{
			Console.Out.Write(result.StdOut);
			return result.ExitCode;
		}

		string? executable = result.GeneratedFiles
			.Where(File.Exists)
			.Where(static path => Path.GetExtension(path) is not ".c" and not ".h" and not ".o" and not ".obj" and not ".a" and not ".lib" and not ".camp" and not ".json")
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
		if (executable is null)
			return Error("run could not find the generated executable.");

		ProcessStartInfo info = new()
		{
			FileName = executable,
			WorkingDirectory = environment.WorkingDirectory,
			UseShellExecute = false
		};
		foreach (string argument in programArgs)
			info.ArgumentList.Add(argument);

		using Process process = new() { StartInfo = info };
		try
		{
			process.Start();
			process.WaitForExit();
			return process.ExitCode;
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return Error(ex.Message);
		}
	}

	static int RunDump(string[] args, CliEnvironment environment)
	{
		if (args.Length == 0)
			return Error("dump requires a dump kind: tokens, cst, ast, declarations, lowering, or metadata.");

		CompilerInspectMode? inspect = ParseDumpKind(args[0]);
		if (inspect is null)
			return Error($"Dump kind '{args[0]}' is not valid. Expected tokens, cst, ast, declarations, lowering, or metadata.");

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

		List<string> errors = [];
		BuildOptionBag bag = new();
		ApplyGlobalPragmas(environment, bag, errors);
		foreach (string file in ExpandSourcePatterns(args.ToList(), [], environment.WorkingDirectory, errors))
			ApplyFilePragmas(file, environment, bag, Precedence.Local, errors);
		if (errors.Count > 0)
			return PrintErrors(errors);

		foreach (PackageSpec package in bag.UsePackages)
		{
			if (PackageCommands.IsInstalled(package, environment.GlobalPackageRoot) || PackageCommands.IsInstalled(package, environment.LocalPackageRoot))
				continue;
			if (!PackageCommands.Install(package, global: false, environment, bag.UseSources, out string message, out string? error))
				return Error(error ?? message);
			Console.Out.WriteLine(message);
		}
		return 0;
	}

	static bool TryBuildRequest(string[] args, CliEnvironment environment, CommandKind command, out CompilerRequest? request, out List<string> errors, List<string>? projectReferenceStack = null)
	{
		request = null;
		errors = [];
		string? defaultOutDir = command is CommandKind.Build or CommandKind.Run ? TryGetDefaultOutDirFromBuildFile(args, environment.WorkingDirectory) : null;

		if (command is CommandKind.Build or CommandKind.Run)
			args = ResponseFileExpander.ExpandBareBuildFiles(args, environment.WorkingDirectory, errors).ToArray();
		if (errors.Count > 0)
			return false;

		ParsedOptions cli = CommandLineOptionParser.Parse(args, allowPositionals: true, errors);
		if (errors.Count > 0)
			return false;

		BuildOptionBag bag = new();
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
				ApplyFilePragmas(file, environment, bag, Precedence.Local, errors);
			includeFiles = ExpandSourcePatterns(cli.IncludePatterns.Concat(bag.IncludePatterns).ToList(), [], environment.WorkingDirectory, errors);
		}

		bag.Apply(cli, Precedence.CommandLine, "command line", errors);
		sourceFiles = ExpandSourcePatterns(cli.Positionals, bag.ExcludePatterns, environment.WorkingDirectory, errors);
		includeFiles = ExpandSourcePatterns(bag.IncludePatterns, [], environment.WorkingDirectory, errors);
		if (sourceFiles.Count == 0)
			errors.Add("At least one source file pattern is required.");

		if (command == CommandKind.Dump && bag.HasBuildOnlyOptions)
			errors.Add("dump does not accept --framework, --artifact, --name, --subsystem, or --out-dir.");
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
			Xml = bag.Xml,
			BuildKind = bag.ArtifactKind,
			InferBuildKind = command == CommandKind.Build && !bag.ArtifactSpecified,
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
		if (!TryBuildProjectReferences(bag.ProjectReferences, request, environment, projectReferenceStack ?? [], out List<string> projectApiHeaders, out List<string> projectLibraries, errors))
			return false;
		foreach (string projectApiHeader in projectApiHeaders)
			includeFiles.Add(projectApiHeader);
		request.References.AddRange(projectLibraries);
		request.Files.AddRange(sourceFiles.Select(path => Path.GetRelativePath(environment.WorkingDirectory, path)));
		request.IncludeFiles.AddRange(includeFiles.Select(path => Path.GetRelativePath(environment.WorkingDirectory, path)));
		return true;
	}

	static bool TryBuildProjectReferences(IReadOnlyList<string> projectReferences, CompilerRequest consumerRequest, CliEnvironment environment, List<string> projectReferenceStack, out List<string> apiHeaders, out List<string> libraries, List<string> errors)
	{
		apiHeaders = [];
		libraries = [];
		bool requireLibrary = consumerRequest.BuildKind is not null;
		foreach (string projectReference in projectReferences)
		{
			if (!TryResolveProjectReference(projectReference, environment.WorkingDirectory, out string? buildFile, out string? error))
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

			string projectDirectory = Path.GetDirectoryName(canonicalBuildFile)!;
			TargetDefinition? target = TryGetTargetDefinition(consumerRequest, environment, errors);
			string artifactDirectory = target is null
				? consumerRequest.TargetName
				: BuildArtifactLayout.GetArtifactDirectoryName(target, NativeBuildKind.Static, consumerRequest.ProfileName);
			string projectOutputDirectory = Path.Combine(projectDirectory, "bin", artifactDirectory);
			projectArgs = RemoveProjectReferenceOverrideOptions(projectArgs);
			projectArgs.AddRange(["--target", consumerRequest.TargetName]);
			projectArgs.AddRange(["--profile", consumerRequest.ProfileName]);
			if (consumerRequest.Variants.Count > 0)
				projectArgs.AddRange(["--variant", .. consumerRequest.Variants]);
			projectArgs.AddRange(["--artifact", "static"]);
			projectArgs.AddRange(["--out-dir", Path.Combine(projectOutputDirectory, ".")]);

			List<string> childStack = [.. projectReferenceStack, canonicalBuildFile];
			if (!TryBuildRequest(projectArgs.ToArray(), environment, CommandKind.Build, out CompilerRequest? projectRequest, out List<string> projectErrors, childStack))
			{
				foreach (string projectError in projectErrors)
					errors.Add($"{projectReference}: {projectError}");
				continue;
			}

			CompilerResult result = CompilerDriver.Execute(projectRequest!);
			WriteProjectReferenceOutput(projectReference, result.StdOut);
			string? apiHeader = result.GeneratedFiles.FirstOrDefault(static path => path.EndsWith("_api.camp", StringComparison.OrdinalIgnoreCase));
			string? library = result.GeneratedFiles.FirstOrDefault(path => IsStaticLibrary(path, consumerRequest.TargetName, consumerRequest.RuntimeRoot));
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

			if (apiHeader is null || requireLibrary && library is null)
			{
				errors.Add(requireLibrary
					? $"{projectReference}: project reference did not produce a Camp API header and static library."
					: $"{projectReference}: project reference did not produce a Camp API header.");
				continue;
			}
			apiHeaders.Add(apiHeader);
			if (library is not null)
				AddUniquePath(libraries, library);
			foreach (string reference in projectRequest!.References)
				AddUniquePath(libraries, reference);
		}
		return errors.Count == 0;
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
		error = $"Project reference '{value}' could not be found.";
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
			"-v",
			"--artifact",
			"--out-dir",
			"--build-dir"
		};
		List<string> result = [];
		for (int i = 0; i < args.Count; i++)
		{
			if (removeValueOptions.Contains(args[i]))
			{
				if (args[i] is "--variant" or "-v")
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

	static bool IsStaticLibrary(string path, string targetName, string runtimeRoot)
	{
		string targetRoot = Path.GetFullPath(Path.Combine(runtimeRoot, "..", "targets"));
		if (!TargetCatalog.TryLoad(targetRoot, out TargetCatalog? catalog, out _) || !catalog!.TryGetTarget(targetName, out TargetDefinition? target))
			return Path.GetExtension(path) is ".a" or ".lib";
		string extension = target!.GetArtifactValue("static_ext", ".a");
		return Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);
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
			"cst" => CompilerInspectMode.Cst,
			"ast" => CompilerInspectMode.Ast,
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
}

sealed class PackageCommands
{
	public static int Run(string[] args, CliEnvironment environment)
	{
		if (args.Length == 0)
			return Error("pkg requires a package command.");
		return args[0] switch
		{
			"add-source" => AddSource(args[1..], environment),
			"remove-source" => RemoveSource(args[1..], environment),
			"search" => Search(args[1..], environment),
			"install" => InstallCommand(args[1..], environment),
			"uninstall" => Uninstall(args[1..], environment),
			"add" => AddPackage(args[1..], environment),
			"remove" => RemovePackage(args[1..], environment),
			_ => Error($"Unknown pkg command '{args[0]}'.")
		};
	}

	static int AddSource(string[] args, CliEnvironment environment)
	{
		if (args.Length < 2)
			return Error("pkg add-source requires <name> <local-folder>.");
		if (!TrySelectBuildFile(args[2..], environment, out string? file, out string? error))
			return Error(error!);
		EditBuildPragmas(file!, line => !line.StartsWith("#build --use-source " + args[0] + " ", StringComparison.Ordinal), $"#build --use-source {args[0]} {Quote(args[1])}");
		return 0;
	}

	static int RemoveSource(string[] args, CliEnvironment environment)
	{
		if (args.Length < 1)
			return Error("pkg remove-source requires <name>.");
		if (!TrySelectBuildFile(args[1..], environment, out string? file, out string? error))
			return Error(error!);
		EditBuildPragmas(file!, line => !line.StartsWith("#build --use-source " + args[0], StringComparison.Ordinal), null);
		return 0;
	}

	static int Search(string[] args, CliEnvironment environment)
	{
		if (args.Length == 0)
			return Error("pkg search requires <pkg>.");
		List<string> errors = [];
		BuildOptionBag bag = LoadEffectiveSources(environment, args.Skip(1).ToArray(), errors);
		if (errors.Count > 0)
			return PrintErrors(errors);
		string packageName = args[0];
		string? sourceFilter = ReadOptionValue(args, "--source");
		foreach (PackageSourceSpec source in bag.UseSources)
		{
			if (sourceFilter is not null && !source.Name.Equals(sourceFilter, StringComparison.Ordinal))
				continue;
			if (string.IsNullOrWhiteSpace(source.Path))
				continue;
			string packageDirectory = Path.Combine(source.Path!, packageName);
			if (!Directory.Exists(packageDirectory))
				continue;
			foreach (string version in Directory.GetDirectories(packageDirectory).Select(Path.GetFileName).Where(static value => value is not null).Cast<string>().OrderBy(static value => SemVersion.Parse(value), SemVersion.Comparer))
				Console.Out.WriteLine($"{source.Name}: {packageName}@{version}");
		}
		return 0;
	}

	static int InstallCommand(string[] args, CliEnvironment environment)
	{
		if (args.Length == 0)
			return Error("pkg install requires <pkg@ver>.");
		bool global = args.Contains("--global", StringComparer.Ordinal);
		PackageSpec package = PackageSpec.Parse(args[0]);
		List<string> errors = [];
		BuildOptionBag bag = LoadEffectiveSources(environment, [], errors);
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

	static int AddPackage(string[] args, CliEnvironment environment)
	{
		if (args.Length < 2)
			return Error("pkg add requires <pkg@ver> <file.camp>.");
		PackageSpec package = PackageSpec.Parse(args[0]);
		string file = Path.GetFullPath(args[1], environment.WorkingDirectory);
		EditBuildPragmas(file, line => !line.StartsWith("#build --use " + package.Name, StringComparison.Ordinal), "#build --use " + package);
		return 0;
	}

	static int RemovePackage(string[] args, CliEnvironment environment)
	{
		if (args.Length < 2)
			return Error("pkg remove requires <pkg> <file.camp>.");
		string file = Path.GetFullPath(args[1], environment.WorkingDirectory);
		EditBuildPragmas(file, line => !line.StartsWith("#build --use " + args[0], StringComparison.Ordinal), null);
		return 0;
	}

	public static bool Install(PackageSpec package, bool global, CliEnvironment environment, IReadOnlyList<PackageSourceSpec> sources, out string message, out string? error)
	{
		error = null;
		message = "";
		foreach (PackageSourceSpec source in sources)
		{
			if (string.IsNullOrWhiteSpace(source.Path))
				continue;
			string packageDirectory = Path.Combine(source.Path!, package.Name);
			if (!Directory.Exists(packageDirectory))
				continue;
			string? version = package.Version ?? Directory.GetDirectories(packageDirectory).Select(Path.GetFileName).Where(static value => value is not null).Cast<string>().OrderByDescending(static value => SemVersion.Parse(value), SemVersion.Comparer).FirstOrDefault();
			if (version is null)
				continue;
			string sourceDirectory = Path.Combine(packageDirectory, version);
			if (!Directory.Exists(Path.Combine(sourceDirectory, "src")))
				continue;
			string targetRoot = global ? environment.GlobalPackageRoot : environment.LocalPackageRoot;
			string targetDirectory = Path.Combine(targetRoot, package.Name, version);
			CopyDirectory(sourceDirectory, targetDirectory);
			message = $"installed: {package.Name}@{version}";
			return true;
		}
		error = $"Package '{package}' could not be found in configured package sources.";
		return false;
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
	public List<string> IncludePatterns { get; } = [];
	public List<string> ExcludePatterns { get; } = [];
	public List<string> Defines { get; } = [];
	public List<string> References { get; } = [];
	public List<string> Frameworks { get; } = [];
	public List<string> Variants { get; } = [];
	public List<PackageSourceSpec> UseSources { get; } = [];
	public List<PackageSpec> UsePackages { get; } = [];
	public List<string> ProjectReferences { get; } = [];
	public bool NoStdLib { get; private set; }
	public bool ArtifactSpecified { get; private set; }
	public NativeBuildKind? ArtifactKind { get; private set; }
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
	public WithinAllocationPolicy? WithinAllocationPolicy => Get("within") switch
	{
		"explicit" => Camp.Compiler.WithinAllocationPolicy.Explicit,
		"implicit" => Camp.Compiler.WithinAllocationPolicy.Implicit,
		_ => null
	};
	public bool Xml => Get("xml") == "true";
	public bool HasBuildOnlyOptions => Frameworks.Count > 0 || ProjectReferences.Count > 0 || ArtifactSpecified || Get("name") is not null || Get("subsystem") is not null || Get("out-dir") is not null;

	public void Apply(ParsedOptions options, Precedence precedence, string source, List<string> errors)
	{
		foreach ((string key, string value) in options.SingleValues)
			SetSingle(key, value, precedence, source, errors);
		foreach (string pattern in options.IncludePatterns)
			IncludePatterns.Add(pattern);
		foreach (string pattern in options.ExcludePatterns)
			ExcludePatterns.Add(pattern);
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

	public void SetArtifact(NativeBuildKind? kind, string source, List<string> errors)
	{
		SetArtifact(kind, Precedence.CommandLine, source, errors);
	}

	void SetArtifact(NativeBuildKind? kind, Precedence precedence, string source, List<string> errors)
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
	public List<string> IncludePatterns { get; } = [];
	public List<string> ExcludePatterns { get; } = [];
	public List<string> Defines { get; } = [];
	public List<string> References { get; } = [];
	public List<string> Frameworks { get; } = [];
	public List<string> Variants { get; } = [];
	public List<PackageSourceSpec> UseSources { get; } = [];
	public List<PackageSpec> UsePackages { get; } = [];
	public List<string> ProjectReferences { get; } = [];
	public bool NoStdLib { get; set; }
	public bool ArtifactSpecified { get; set; }
	public NativeBuildKind? ArtifactKind { get; set; }
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
						"winexe" => InvalidArtifact("winexe has been removed. Use --artifact exec --subsystem windows.", errors),
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
					AddSingle(result, "out-dir", PathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--build-dir":
					errors.Add("--build-dir has been removed. Build intermediates are written to the output artifact directory's build subdirectory.");
					i += HasValue(tokens, i) ? 1 : 0;
					break;
				case "--include":
				case "-i":
					result.IncludePatterns.Add(PathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--exclude":
					result.ExcludePatterns.Add(PathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--define":
				case "-d":
					result.Defines.Add(RequiredValue(tokens, ref i, token, errors));
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
					result.UsePackages.Add(PackageSpec.Parse(RequiredValue(tokens, ref i, token, errors)));
					break;
				case "--project-reference":
					result.ProjectReferences.Add(PathArguments.Normalize(RequiredValue(tokens, ref i, token, errors)));
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
				case "--xml":
					AddSingle(result, "xml", "true");
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
			"--target" or "-t" or "--profile" or "-p" or "--variant" or "-v" or "--memory-model" or "--emit" or "--metadata" or "--artifact" or "--name" or "--subsystem" or "--out-dir" or "--build-dir" or "--include" or "-i" or "--exclude" or "--define" or "-d" or "--use" or "-u" or "--project-reference" => 1,
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
					result.Add(RebasePathValue(tokens[++i], baseDirectory));
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

	static bool HasWildcards(string pattern) => pattern.IndexOfAny(['*', '?', '[']) >= 0;
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

sealed record PackageSpec(string Name, string? Version)
{
	public static PackageSpec Parse(string value)
	{
		string[] parts = value.Split('@', 2);
		return new PackageSpec(parts[0], parts.Length == 2 && parts[1].Length > 0 ? parts[1] : null);
	}
	public override string ToString() => Version is null ? Name : Name + "@" + Version;
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
	public required string RepositoryRoot { get; init; }
	public string GlobalCampPath => Path.Combine(RepositoryRoot, "lib", "global.camp");
	public string GlobalPackageRoot => Path.Combine(RepositoryRoot, "cache", "pkg");
	public string LocalPackageRoot => Path.Combine(WorkingDirectory, "cache", "pkg");

	public static CliEnvironment Create()
	{
		string workingDirectory = Directory.GetCurrentDirectory();
		string runtimeRoot = AppContext.BaseDirectory;
		string repositoryRoot = FindRepositoryRoot(workingDirectory) ?? FindRepositoryRoot(runtimeRoot) ?? Path.GetFullPath(Path.Combine(runtimeRoot, ".."));
		return new CliEnvironment
		{
			WorkingDirectory = workingDirectory,
			RuntimeRoot = Path.Combine(repositoryRoot, "bin"),
			RepositoryRoot = repositoryRoot
		};
	}

	static string? FindRepositoryRoot(string start)
	{
		DirectoryInfo? directory = new DirectoryInfo(Path.GetFullPath(start));
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "src", "camplang.sln")) && Directory.Exists(Path.Combine(directory.FullName, "lib", "std", "src")))
				return directory.FullName;
			directory = directory.Parent;
		}
		return null;
	}
}

enum CommandKind
{
	Build,
	Run,
	Dump
}

enum Precedence
{
	Global = 0,
	Local = 1,
	CommandLine = 2
}
