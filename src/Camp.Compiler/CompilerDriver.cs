using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Camp.Compiler;

public enum CompilerInspectMode
{
	None,
	Tokens,
	Cst,
	Ast,
	Declarations,
	Lowering
}

public sealed class CompilerRequest
{
	public List<string> Files { get; } = [];
	public List<string> IncludeFiles { get; } = [];
	public List<string> Defines { get; } = [];
	public CompilerInspectMode? Inspect { get; set; }
	public bool Xml { get; set; }
	public bool InspectApi { get; set; }
	public string TargetName { get; set; } = "clang-macos-x64";
	public string ProfileName { get; set; } = "DEBUG";
	public string? MemoryModelName { get; set; }
	public string EmitKind { get; set; } = "c99";
	public NativeBuildKind? BuildKind { get; set; }
	public string? OutDir { get; set; }
	public string? BuildDir { get; set; }
	public bool NoStdLib { get; set; }
	public string AssetRoot { get; set; } = AppContext.BaseDirectory;
	public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();
}

public sealed class CompilerResult
{
	public int ExitCode { get; set; }
	public string StdOut { get; set; } = "";
	public string StdErr { get; set; } = "";
	public List<string> GeneratedFiles { get; } = [];
}

public static class CompilerDriver
{
	public static CompilerResult Execute(CompilerRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		DriverRun run = new(request);
		return run.Execute();
	}

	sealed class DriverRun(CompilerRequest request)
	{
		readonly StringBuilder stdout = new();
		readonly StringBuilder stderr = new();
		readonly List<string> generatedFiles = [];

		public CompilerResult Execute()
		{
			int exitCode = Run();
			CompilerResult result = new()
			{
				ExitCode = exitCode,
				StdOut = Normalize(stdout.ToString()),
				StdErr = Normalize(stderr.ToString())
			};
			result.GeneratedFiles.AddRange(generatedFiles);
			return result;
		}

		int Run()
		{
			if (request.Files.Count == 0)
				return Error("At least one filename is required.");

			if (request.Files.Count > 1 && request.Files.Contains("-") || request.IncludeFiles.Count > 0 && request.Files.Contains("-"))
				return Error("Standard input may only be used by itself and cannot be combined with API headers.");
			if (request.IncludeFiles.Contains("-"))
				return Error("API headers must be read from files, not standard input.");
			if (request.Xml && request.Inspect is not (CompilerInspectMode.Declarations or CompilerInspectMode.Lowering))
				return Error("--xml can only be used with --inspect declarations or --inspect lowering.");
			if (request.InspectApi && (request.Inspect is not null || request.Xml))
				return Error("--inspect-api cannot be combined with --inspect or --xml.");
			if (request.BuildKind is not null && request.Inspect is not null)
				return Error("--build cannot be combined with --inspect.");
			if (request.BuildKind is not null && request.InspectApi)
				return Error("--build cannot be combined with --inspect-api.");

			if (!TryCreateRuntimeContext(out RuntimeContext? context))
				return 1;

			List<string> packageApiHeaders = [];
			List<string> packageLibraries = [];
			if (!request.NoStdLib)
			{
				if (!TryPreparePackage(context!, "std", request.BuildKind is not null, out string? stdApiHeader, out string? stdLibrary))
					return 1;
				if (stdApiHeader is not null)
					packageApiHeaders.Add(stdApiHeader);
				if (stdLibrary is not null)
					packageLibraries.Add(stdLibrary);
			}

			List<string> allIncludes = [.. packageApiHeaders, .. request.IncludeFiles];
			if (!TryLoadCompilation(request.Files, allIncludes, context!, out Compilation compilation))
				return 1;

			if (request.InspectApi)
				return PrintApi(compilation);

			CompilerInspectMode inspect = request.Inspect ?? CompilerInspectMode.None;
			return inspect switch
			{
				CompilerInspectMode.None => EmitDefaultOutput(compilation, packageLibraries),
				CompilerInspectMode.Tokens => PrintTokens(compilation),
				CompilerInspectMode.Cst => PrintSyntaxXml(compilation),
				CompilerInspectMode.Ast => PrintBindXml(compilation),
				CompilerInspectMode.Declarations => PrintDeclarations(compilation),
				CompilerInspectMode.Lowering => PrintLowering(compilation),
				_ => 1
			};
		}

		int Error(string message)
		{
			ErrorLine(message);
			return 1;
		}

		bool TryCreateRuntimeContext(out RuntimeContext? context)
		{
			context = null;
			string normalizedProfile = request.ProfileName.ToUpperInvariant();
			if (normalizedProfile is not "DEBUG" and not "RELEASE")
			{
				ErrorLine($"Profile '{request.ProfileName}' is not valid. Expected DEBUG or RELEASE.");
				return false;
			}

			string targetsDirectory = Path.Combine(request.AssetRoot, "targets");
			if (!TargetCatalog.TryLoad(targetsDirectory, out TargetCatalog? catalog, out string? error))
			{
				ErrorLine(error ?? $"Target directory '{targetsDirectory}' could not be loaded.");
				return false;
			}

			if (!catalog!.TryGetTarget(request.TargetName, out TargetDefinition? target))
			{
				ErrorLine($"Target '{request.TargetName}' could not be found in '{targetsDirectory}'.");
				return false;
			}

			if (target!.MemoryModels.Count > 0)
			{
				if (string.IsNullOrWhiteSpace(request.MemoryModelName))
				{
					ErrorLine($"Target '{target.Name}' requires --memory-model. Available memory models: {string.Join(", ", target.MemoryModels.Keys)}.");
					return false;
				}
				if (!target.MemoryModels.ContainsKey(request.MemoryModelName))
				{
					ErrorLine($"Memory model '{request.MemoryModelName}' is not defined by target '{target.Name}'. Available memory models: {string.Join(", ", target.MemoryModels.Keys)}.");
					return false;
				}
			}
			else if (!string.IsNullOrWhiteSpace(request.MemoryModelName))
			{
				ErrorLine($"Target '{target.Name}' does not define memory models, so --memory-model cannot be used.");
				return false;
			}

			context = new RuntimeContext(request.AssetRoot, target, normalizedProfile, string.IsNullOrWhiteSpace(request.MemoryModelName) ? null : request.MemoryModelName, [.. request.Defines]);
			return true;
		}

		bool TryLoadCompilation(List<string> filenames, List<string> includeFilenames, RuntimeContext context, out Compilation compilation)
		{
			compilation = new Compilation { Target = context.Target, ProfileName = context.ProfileName, MemoryModelName = context.MemoryModelName };
			AddPreprocessorSymbols(compilation, context);
			foreach (string filename in filenames)
			{
				if (!TryReadInput(filename, out string text, out string displayPath))
					return false;
				compilation.Files.Add(new SourceFile { Path = displayPath, Text = text });
			}
			foreach (string filename in includeFilenames)
			{
				if (!TryReadInput(filename, out string text, out string displayPath))
					return false;
				compilation.Files.Add(new SourceFile { Path = displayPath, Text = text, IsApiHeader = true });
			}
			return true;
		}

		void AddPreprocessorSymbols(Compilation compilation, RuntimeContext context)
		{
			compilation.PreprocessorSymbols.Add("TRUE");
			compilation.PreprocessorSymbols.Add(context.ProfileName);
			foreach (string symbol in context.Target.Defines.Keys)
				compilation.PreprocessorSymbols.Add(symbol);
			foreach (string symbol in context.CommandLineDefines)
				if (!string.IsNullOrWhiteSpace(symbol))
					compilation.PreprocessorSymbols.Add(symbol);
		}

		bool TryReadInput(string filename, out string text, out string displayPath)
		{
			try
			{
				if (filename == "-")
				{
					text = Console.In.ReadToEnd();
					displayPath = "-";
					return true;
				}

				string fullPath = Path.GetFullPath(filename, request.WorkingDirectory);
				text = File.ReadAllText(fullPath);
				displayPath = GetDisplayPath(fullPath);
				return true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				text = "";
				displayPath = filename;
				ErrorLine($"{filename}: {ex.Message}");
				return false;
			}
		}

		string GetDisplayPath(string fullPath)
		{
			string root = Path.GetFullPath(request.WorkingDirectory);
			string relative = Path.GetRelativePath(root, fullPath);
			return relative.StartsWith("..", StringComparison.Ordinal) ? fullPath : relative;
		}

		bool TryPreparePackage(RuntimeContext context, string packageName, bool requireNativeLibrary, out string? apiHeaderPath, out string? libraryPath)
		{
			apiHeaderPath = null;
			libraryPath = null;
			string packageDirectory = Path.Combine(context.AssetRoot, "lib", packageName);
			string sourceDirectory = Path.Combine(packageDirectory, "src");
			if (!Directory.Exists(sourceDirectory))
			{
				ErrorLine($"Package '{packageName}' source directory '{sourceDirectory}' could not be found.");
				return false;
			}

			string[] sourceFiles = Directory.GetFiles(sourceDirectory, "*.camp", SearchOption.AllDirectories)
				.OrderBy(static x => x, StringComparer.Ordinal)
				.ToArray();
			if (sourceFiles.Length == 0)
			{
				ErrorLine($"Package '{packageName}' source directory '{sourceDirectory}' does not contain any .camp files.");
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
			DateTime outputTime = File.GetLastWriteTimeUtc(outputPath);
			foreach (string sourceFile in sourceFiles)
				if (outputTime <= File.GetLastWriteTimeUtc(sourceFile))
					return false;
			return true;
		}

		bool TryBuildPackage(string packageName, IReadOnlyList<string> sourceFiles, string apiPath, string? staticLibraryPath, RuntimeContext context)
		{
			CompilerRequest packageRequest = new()
			{
				AssetRoot = request.AssetRoot,
				WorkingDirectory = request.WorkingDirectory,
				TargetName = context.Target.Name,
				ProfileName = context.ProfileName,
				MemoryModelName = context.MemoryModelName,
				NoStdLib = true
			};
			packageRequest.Files.AddRange(sourceFiles);
			if (!TryLoadCompilation(packageRequest.Files, [], context, out Compilation packageCompilation))
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
				ErrorLine($"{apiPath}: {ex.Message}");
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
				ErrorLine(diagnostic);
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
				ErrorLine(diagnostic);
			return build.Success;
		}

		int EmitDefaultOutput(Compilation compilation, IReadOnlyList<string> packageLibraries)
		{
			if (!LowerAndReport(compilation))
				return 1;

			FunctionDefinition? execEntryPoint = null;
			if (request.BuildKind is NativeBuildKind.Exec && !TryPrepareExecEntryPoint(compilation, out execEntryPoint))
				return 1;

			string buildDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(request.BuildDir) ? CCodeEmitter.GetDefaultOutputDirectory(compilation.Files) : request.BuildDir, request.WorkingDirectory);
			string outputDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(request.OutDir) ? CCodeEmitter.GetDefaultArtifactDirectory(compilation.Files) : request.OutDir, request.WorkingDirectory);
			CEmissionResult result = CCodeEmitter.Emit(compilation, new CEmissionOptions
			{
				OutputDirectory = buildDirectory,
				ProjectName = CCodeEmitter.GetProjectName(compilation.Files),
				EmitKind = request.EmitKind,
				EmitExecMainWrapper = request.BuildKind is NativeBuildKind.Exec,
				ExecEntryPoint = execEntryPoint
			});
			foreach (string diagnostic in result.Diagnostics)
				ErrorLine(diagnostic);
			if (!result.Success)
				return 1;

			foreach (string generated in result.GeneratedFiles)
			{
				generatedFiles.Add(generated);
				OutLine("generated: " + Path.GetFileName(generated));
			}

			if (request.BuildKind is NativeBuildKind.Static or NativeBuildKind.Shared && !TryEmitLibraryApiArtifacts(compilation, outputDirectory))
				return 1;

			if (request.BuildKind is null)
				return 0;

			NativeBuildResult build = NativeBuildDriver.Build(new NativeBuildOptions
			{
				Target = compilation.Target!,
				ProfileName = compilation.ProfileName,
				BuildDirectory = buildDirectory,
				OutputDirectory = outputDirectory,
				ProjectName = CCodeEmitter.GetProjectName(compilation.Files),
				Kind = request.BuildKind.Value,
				SourceFiles = result.GeneratedSourceFiles,
				Libraries = packageLibraries
			});
			foreach (string diagnostic in build.Diagnostics)
				ErrorLine(diagnostic);
			if (!build.Success)
				return 1;
			foreach (string generated in build.GeneratedFiles)
			{
				generatedFiles.Add(generated);
				OutLine("generated: " + Path.GetFileName(generated));
			}
			return 0;
		}

		bool TryPrepareExecEntryPoint(Compilation compilation, out FunctionDefinition? entryPoint)
		{
			entryPoint = null;
			List<FunctionDefinition> candidates = [];
			foreach (Definition definition in compilation.SharedModule?.Definitions ?? [])
				if (definition is FunctionDefinition { Name: "main", Export: not null } function)
					candidates.Add(function);

			if (candidates.Count != 1)
			{
				ErrorLine(candidates.Count == 0
					? "Building an executable requires exactly one exported function named 'main'."
					: "Building an executable requires exactly one exported function named 'main', but multiple were found.");
				return false;
			}

			FunctionDefinition main = candidates[0];
			bool returnsInt = main.ReturnType is PrimitiveTypeReference { Type: PrimitiveType.Int } || main.ResolvedType == "int";
			bool returnsVoid = main.ReturnType is PrimitiveTypeReference { Type: PrimitiveType.Void } || main.ResolvedType == "void";
			if (!returnsInt && !returnsVoid)
			{
				ErrorLine("Executable entry point 'main' must return int or void.");
				return false;
			}
			if (main.Parameters.Count is not (0 or 2))
			{
				ErrorLine("Executable entry point 'main' must have no parameters or one string[] parameter.");
				return false;
			}
			main.Symbol = "campmain";
			entryPoint = main;
			return true;
		}

		bool TryEmitLibraryApiArtifacts(Compilation compilation, string outputDirectory)
		{
			string projectName = CCodeEmitter.GetProjectName(compilation.Files);
			string campApiPath = Path.Combine(outputDirectory, projectName + "_api.camp");
			try
			{
				Directory.CreateDirectory(outputDirectory);
				using StreamWriter writer = new(campApiPath, append: false, Encoding.UTF8);
				BindableNodeCodeSerializer.Serialize(BuildApiOutputModule(compilation), writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
				generatedFiles.Add(campApiPath);
				OutLine("generated: " + Path.GetFileName(campApiPath));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				ErrorLine($"{campApiPath}: {ex.Message}");
				return false;
			}

			CEmissionResult apiHeader = CCodeEmitter.EmitProjectApiHeader(compilation, new CEmissionOptions
			{
				OutputDirectory = outputDirectory,
				ProjectName = projectName,
				EmitKind = request.EmitKind
			}, outputDirectory);
			foreach (string diagnostic in apiHeader.Diagnostics)
				ErrorLine(diagnostic);
			if (!apiHeader.Success)
				return false;
			foreach (string generated in apiHeader.GeneratedFiles)
			{
				generatedFiles.Add(generated);
				OutLine("generated: " + Path.GetFileName(generated));
			}
			return true;
		}

		int PrintTokens(Compilation compilation)
		{
			CompilationPipeline.Tokenize(compilation);
			foreach (Token token in compilation.Files[0].Tokens!)
				OutLine($"\"{EscapeTokenValue(token.Value)}\" {token.Class}");
			return 0;
		}

		int PrintSyntaxXml(Compilation compilation)
		{
			if (!ParseAllAndReport(compilation))
				return 1;
			PrintXmlDocument(CompilerXmlSerializer.SerializeSyntax(compilation.Files[0].SyntaxTree!));
			return 0;
		}

		int PrintBindXml(Compilation compilation)
		{
			if (!BuildAllAndReport(compilation))
				return 1;
			PrintXmlDocument(CompilerXmlSerializer.SerializeBindableNode(compilation.Files[0].BindableTree!));
			return 0;
		}

		int PrintDeclarations(Compilation compilation)
		{
			if (!ExpandDeclarationsAndReport(compilation))
				return 1;
			AnalysisResult analysis = BindableNodeAnalyzer.AnalyzeExpanded(compilation.DeclarationExpansion!, compilation.Target, compilation.MemoryModelName);
			if (!PrintAnalysisDiagnostics(compilation, analysis.Diagnostics))
				return 1;
			compilation.SharedModule = analysis.Module;
			PrintBindable(BuildOutputModule(compilation, compilation.Files[0]));
			return 0;
		}

		int PrintLowering(Compilation compilation)
		{
			if (!LowerAndReport(compilation))
				return 1;
			PrintBindable(BuildOutputModule(compilation, compilation.Files[0]));
			return 0;
		}

		int PrintApi(Compilation compilation)
		{
			if (!BuildAllAndReport(compilation))
				return 1;
			AnalysisResult analysis = BindableNodeAnalyzer.Analyze(compilation.SharedModule!, compilation.Target, compilation.MemoryModelName);
			if (!PrintAnalysisDiagnostics(compilation, analysis.Diagnostics))
				return 1;
			using StringWriter writer = new(stdout, CultureInfo.InvariantCulture);
			BindableNodeCodeSerializer.Serialize(BuildApiOutputModule(compilation), writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
			return 0;
		}

		bool ParseAllAndReport(Compilation compilation)
		{
			bool success = CompilationPipeline.Parse(compilation);
			foreach (SourceFile file in compilation.Files)
				foreach (ParseDiagnostic diagnostic in file.ParseDiagnostics)
					PrintDiagnostic(file.Path, diagnostic.Range, diagnostic.Message);
			return success;
		}

		bool BuildAllAndReport(Compilation compilation)
		{
			bool success = CompilationPipeline.BuildAst(compilation);
			foreach (SourceFile file in compilation.Files)
			{
				foreach (ParseDiagnostic diagnostic in file.ParseDiagnostics)
					PrintDiagnostic(file.Path, diagnostic.Range, diagnostic.Message);
				foreach (BindDiagnostic diagnostic in file.BindDiagnostics)
					PrintDiagnostic(file.Path, diagnostic.Range, diagnostic.Message);
			}
			return success;
		}

		bool ExpandDeclarationsAndReport(Compilation compilation)
		{
			bool success = CompilationPipeline.ExpandDeclarations(compilation);
			PrintPipelineDiagnostics(compilation);
			return success;
		}

		bool LowerAndReport(Compilation compilation)
		{
			bool success = CompilationPipeline.Lower(compilation);
			PrintPipelineDiagnostics(compilation);
			return success;
		}

		void PrintPipelineDiagnostics(Compilation compilation)
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
				foreach (AnalysisDiagnostic diagnostic in compilation.DeclarationExpansion.Diagnostics)
					PrintDiagnosticOnce(GetDiagnosticFilename(compilation, diagnostic.Range), diagnostic.Range, diagnostic.Message, printed);
			if (compilation.Lowering is not null)
				foreach (AnalysisDiagnostic diagnostic in compilation.Lowering.Diagnostics)
					PrintDiagnosticOnce(GetDiagnosticFilename(compilation, diagnostic.Range), diagnostic.Range, diagnostic.Message, printed);
		}

		bool PrintAnalysisDiagnostics(Compilation compilation, IReadOnlyList<AnalysisDiagnostic> diagnostics)
		{
			foreach (AnalysisDiagnostic diagnostic in diagnostics)
				PrintDiagnostic(GetDiagnosticFilename(compilation, diagnostic.Range), diagnostic.Range, diagnostic.Message);
			return diagnostics.Count == 0;
		}

		static Module BuildApiOutputModule(Compilation compilation)
		{
			Module output = new() { ResolvedType = compilation.SharedModule?.ResolvedType };
			foreach (SourceFile file in compilation.Files)
			{
				if (file.IsApiHeader || file.BindableTree is not Module module)
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

		static Module BuildOutputModule(Compilation compilation, SourceFile file)
		{
			if (compilation.SharedModule is null)
				return file.BindableTree!;
			Module output = new()
			{
				SourceSyntax = file.BindableTree?.SourceSyntax,
				ResolvedType = compilation.SharedModule.ResolvedType,
				ExportAs = file.BindableTree?.ExportAs
			};
			foreach (UsingDeclaration usingDeclaration in file.BindableTree?.Usings ?? [])
				output.Usings.Add(usingDeclaration);
			foreach (Definition definition in compilation.SharedModule.Definitions)
				if (compilation.DefinitionOwners.TryGetValue(definition, out SourceFile? owner) && ReferenceEquals(owner, file))
					output.Definitions.Add(definition);
			return output;
		}

		void PrintBindable(Module module)
		{
			if (request.Xml)
				PrintXmlDocument(CompilerXmlSerializer.SerializeBindableNode(module));
			else
			{
				using StringWriter writer = new(stdout, CultureInfo.InvariantCulture);
				BindableNodeCodeSerializer.Serialize(module, writer);
			}
		}

		void PrintXmlDocument(XElement root)
		{
			XDocument document = new(new XDeclaration("1.0", "utf-8", null), root);
			XmlWriterSettings settings = new() { Indent = true, OmitXmlDeclaration = false };
			using StringWriter textWriter = new(stdout, CultureInfo.InvariantCulture);
			using XmlWriter writer = XmlWriter.Create(textWriter, settings);
			document.Save(writer);
		}

		void PrintDiagnostic(string filename, TokenRange? range, string message)
		{
			if (range is TokenRange tokenRange)
				ErrorLine($"{filename}({tokenRange.StartLineNumber},{tokenRange.StartColumn}): error: {message}");
			else
				ErrorLine($"{filename}: error: {message}");
		}

		void PrintDiagnosticOnce(string filename, TokenRange? range, string message, HashSet<string> printed)
		{
			string key = range is TokenRange r ? $"{filename}:{r.StartLineNumber}:{r.StartColumn}:{message}" : $"{filename}:::${message}";
			if (printed.Add(key))
				PrintDiagnostic(filename, range, message);
		}

		static string GetDiagnosticFilename(Compilation compilation, TokenRange? range)
		{
			if (range is TokenRange tokenRange)
			{
				foreach (SourceFile file in compilation.Files)
					if (ReferenceEquals(file.Tokens, tokenRange.Sequence))
						return file.Path;
			}
			return compilation.Files.Count == 0 ? "" : compilation.Files[0].Path;
		}

		static string EscapeTokenValue(string value)
		{
			return value.Replace("\\", "\\\\", StringComparison.Ordinal)
				.Replace("\t", "\\t", StringComparison.Ordinal)
				.Replace("\r", "\\r", StringComparison.Ordinal)
				.Replace("\n", "\\n", StringComparison.Ordinal)
				.Replace("\"", "\\\"", StringComparison.Ordinal);
		}

		void OutLine(string line)
		{
			stdout.Append(line).Append('\n');
		}

		void ErrorLine(string line)
		{
			stderr.Append(line).Append('\n');
		}

		static string Normalize(string text)
		{
			return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
		}
	}

	sealed record RuntimeContext(string AssetRoot, TargetDefinition Target, string ProfileName, string? MemoryModelName, IReadOnlyList<string> CommandLineDefines);
}
