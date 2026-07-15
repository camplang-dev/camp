using System;
using System.IO;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CompilerDriverOptionTests
{
	[Fact]
	public void Variants_control_target_owned_defines()
	{
		string source = CreateTempCase("variant_driver.camp", """
			#if UNICODE
			export inline int WIDTH = 2;
			#else
			export inline int WIDTH = 1;
			#endif
			""");

		CompilerResult ansi = Execute(source, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.Variants.Add("ansi");
			request.Inspect = CompilerInspectMode.Declarations;
			request.NoStdLib = true;
		});
		CompilerResult unicode = Execute(source, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.Variants.Add("unicode");
			request.Inspect = CompilerInspectMode.Declarations;
			request.NoStdLib = true;
		});

		Assert.Equal(0, ansi.ExitCode);
		Assert.Contains("WIDTH = 1", ansi.StdOut, StringComparison.Ordinal);
		Assert.Equal(0, unicode.ExitCode);
		Assert.Contains("WIDTH = 2", unicode.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Variants_reject_unknown_and_same_group_values()
	{
		string source = CreateTempCase("variant_driver_diagnostics.camp", "export int main() => 0;\n");

		CompilerResult unknown = Execute(source, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.Variants.Add("foobar");
			request.NoStdLib = true;
		});
		CompilerResult conflict = Execute(source, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.Variants.Add("unicode");
			request.Variants.Add("ansi");
			request.NoStdLib = true;
		});

		Assert.NotEqual(0, unknown.ExitCode);
		Assert.Contains("Variant 'foobar' is not defined by target 'msvc-windows-x64'", unknown.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, conflict.ExitCode);
		Assert.Contains("both belong to group 'charwidth'", conflict.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Target_owned_define_is_rejected_from_request_and_warns_in_source()
	{
		string normal = CreateTempCase("variant_driver_define.camp", "export int main() => 0;\n");
		string sourceDefine = CreateTempCase("variant_driver_source_define.camp", """
			#define UNICODE

			export int main() => 0;
			""");

		CompilerResult cliLike = Execute(normal, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.Defines.Add("UNICODE");
			request.NoStdLib = true;
		});
		CompilerResult source = Execute(sourceDefine, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.NoStdLib = true;
		});

		Assert.NotEqual(0, cliLike.ExitCode);
		Assert.Contains("Define 'UNICODE' is owned by target", cliLike.StdErr, StringComparison.Ordinal);
		Assert.Equal(0, source.ExitCode);
		Assert.Contains("warning: Preprocessor symbol 'UNICODE' is owned by the selected target", source.StdErr, StringComparison.Ordinal);
	}

	static CompilerResult Execute(string sourcePath, Action<CompilerRequest> configure)
	{
		string repositoryRoot = FindRepositoryRoot();
		CompilerRequest request = new()
		{
			RuntimeRoot = Path.Combine(repositoryRoot, "bin"),
			TargetRoot = Path.Combine(repositoryRoot, "targets"),
			PackageSourceRoot = Path.Combine(repositoryRoot, "lib"),
			PackageArtifactRoot = Path.Combine(repositoryRoot, "tmp", "driver-option-packages"),
			WorkingDirectory = Path.GetDirectoryName(sourcePath) ?? repositoryRoot,
			OutDir = Path.Combine(repositoryRoot, "tmp", "driver-option-tests", Path.GetFileNameWithoutExtension(sourcePath), ".")
		};
		request.Files.Add(sourcePath);
		configure(request);
		return CompilerDriver.Execute(request);
	}

	static string CreateTempCase(string name, string text)
	{
		string path = Path.Combine(FindRepositoryRoot(), "tmp", "driver-option-tests", name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal));
		return path;
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
