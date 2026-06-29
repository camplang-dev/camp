using System;
using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class DiagnosticStructureTests
{
	[Fact]
	public void Representative_analysis_diagnostics_have_stable_codes()
	{
		SemanticCompilation autoDiagnostic = SemanticCompiler.CompileLowered("""
			void test()
			{
			}

			void main()
			{
				auto value = test();
			}
			""");

		Assert.Contains(autoDiagnostic.AnalysisDiagnostics, static diagnostic => diagnostic.Code == DiagnosticCodes.AutoCannotInferVoid);

		SemanticCompilation rangeDiagnostic = SemanticCompiler.CompileLowered("""
			void slice(nuint index, nuint count)
			{
			}

			void main()
			{
				slice(0..1);
			}
			""");

		Assert.Contains(rangeDiagnostic.AnalysisDiagnostics, static diagnostic => diagnostic.Code == DiagnosticCodes.RangeRequiresRangeParameter);
	}

	[Fact]
	public void Parse_and_bind_diagnostic_shapes_can_carry_codes_and_severity()
	{
		ParseDiagnostic parse = new(null, "parser message", DiagnosticCodes.InitializerAssignmentRequiresDot);
		BindDiagnostic bind = new(null, "bind message", "CAMP2001", DiagnosticSeverity.Warning);

		Assert.Equal(DiagnosticSeverity.Error, parse.Severity);
		Assert.Equal(DiagnosticCodes.InitializerAssignmentRequiresDot, parse.Code);
		Assert.Equal(DiagnosticSeverity.Warning, bind.Severity);
		Assert.Equal("CAMP2001", bind.Code);
	}

	[Fact]
	public void Console_diagnostic_output_stays_message_only_by_default()
	{
		string repositoryRoot = FindRepositoryRoot();
		string tempDirectory = Path.Combine(repositoryRoot, "tmp", "diagnostic-structure-tests");
		Directory.CreateDirectory(tempDirectory);
		string sourcePath = Path.Combine(tempDirectory, "auto_void.camp");
		File.WriteAllText(sourcePath, """
			void test()
			{
			}

			void main()
			{
				auto value = test();
			}
			""");

		CompilerResult result = CompilerDriver.Execute(CreateRequest(repositoryRoot, sourcePath));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Auto declaration cannot infer a type from a void expression.", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain(DiagnosticCodes.AutoCannotInferVoid, result.StdErr, StringComparison.Ordinal);
	}

	static CompilerRequest CreateRequest(string repositoryRoot, string sourcePath)
	{
		CompilerRequest request = new()
		{
			RuntimeRoot = Path.Combine(repositoryRoot, "bin"),
			TargetRoot = Path.Combine(repositoryRoot, "targets"),
			PackageSourceRoot = Path.Combine(repositoryRoot, "lib"),
			PackageArtifactRoot = Path.Combine(repositoryRoot, "pkg"),
			WorkingDirectory = repositoryRoot,
			TargetName = "clang-macos-x64",
			NoStdLib = true,
			Inspect = CompilerInspectMode.Lowering
		};
		request.Files.Add(Path.GetRelativePath(repositoryRoot, sourcePath));
		return request;
	}

	static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "src", "camplang.sln")))
				return directory.FullName;
			directory = directory.Parent;
		}
		throw new InvalidOperationException("Could not find repository root containing src/camplang.sln.");
	}
}
