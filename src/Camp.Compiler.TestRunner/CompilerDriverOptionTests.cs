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

	[Fact]
	public void Configuration_flags_validate_declarations_and_configurations()
	{
		string source = CreateTempCase("configuration_flags.camp", "export int main() => 0;\n");

		CompilerResult ok = Execute(source, request =>
		{
			request.NoStdLib = true;
			request.ConfigurationFlagDeclarations.Add("APP_TRACE");
			request.ConfigurationFlagConfigurations.Add("APP_TRACE");
		});
		CompilerResult unknown = Execute(source, request =>
		{
			request.NoStdLib = true;
			request.ConfigurationFlagConfigurations.Add("APP_TRACE");
		});
		CompilerResult duplicateDeclare = Execute(source, request =>
		{
			request.NoStdLib = true;
			request.ConfigurationFlagDeclarations.Add("APP_TRACE");
			request.ConfigurationFlagDeclarations.Add("APP_TRACE=true");
		});
		CompilerResult duplicateConfigure = Execute(source, request =>
		{
			request.NoStdLib = true;
			request.ConfigurationFlagDeclarations.Add("APP_TRACE");
			request.ConfigurationFlagConfigurations.Add("APP_TRACE");
			request.ConfigurationFlagConfigurations.Add("APP_TRACE=false");
		});

		Assert.Equal(0, ok.ExitCode);
		Assert.NotEqual(0, unknown.ExitCode);
		Assert.Contains("must be declared before it can be configured", unknown.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, duplicateDeclare.ExitCode);
		Assert.Contains("already declared", duplicateDeclare.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, duplicateConfigure.ExitCode);
		Assert.Contains("already configured", duplicateConfigure.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Target_owned_configuration_flags_are_not_configurable_from_request()
	{
		string source = CreateTempCase("configuration_flags_target_owned.camp", "export int main() => 0;\n");

		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.NoStdLib = true;
			request.ConfigurationFlagConfigurations.Add("UNICODE=false");
		});

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("owned by the selected target", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Configuration_requirements_and_configured_intrinsic_bind()
	{
		string source = CreateTempCase("configuration_requirements.camp", """
			@require(OS_LINUX || OS_WIN32);

			@require(OS_LINUX || OS_WIN32)
			void platformMethod()
			{
			}

			export int main()
			{
				if (configured(OS_LINUX || OS_WIN32))
					return 1;
				if (configured(!OS_LINUX && (OS_WIN32 ^ OS_WIN64)))
					return 2;
				return 0;
			}
			""");

		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
		});

		Assert.Equal(0, result.ExitCode);
	}

	[Fact]
	public void Configuration_requirement_diagnostics_reject_invalid_placement()
	{
		string source = CreateTempCase("configuration_requirement_placement_diagnostics.camp", """
			@require(OS_LINUX)
			enum Problem
			{
				@require(OS_LINUX)
				BAD
			}
			""");

		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("@require is not valid on enum values", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Configuration_requirement_diagnostics_reject_unknown_and_bare_flags()
	{
		string source = CreateTempCase("configuration_requirement_expression_diagnostics.camp", """
			export int main()
			{
				if (configured(UNKNOWN_FLAG))
					return 1;
				if (OS_LINUX)
					return 2;
				return 0;
			}
			""");

		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
		});

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Unknown configuration flag 'UNKNOWN_FLAG'", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("Configuration flag 'OS_LINUX' can only be queried with configured(...)", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Effective_requirements_filter_declarations_and_file_defaults()
	{
		string source = CreateTempCase("effective_requirements.camp", """
			@require(OS_WIN32);

			void fileDefaultOmitted()
			{
			}

			@require(OS_LINUX)
			void declarationOverrideIncluded()
			{
			}

			@require(OS_WIN32)
			class WindowsOnly
			{
				void childAlsoOmitted()
				{
				}
			}
			""");

		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("declarationOverrideIncluded", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("fileDefaultOmitted", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("WindowsOnly", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("childAlsoOmitted", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Testonly_uses_requirement_participation_for_production_filtering()
	{
		string source = CreateTempCase("effective_requirements_testonly.camp", """
			@testonly
			void helper()
			{
			}

			void normal()
			{
			}
			""");

		CompilerResult production = Execute(source, request =>
		{
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});
		CompilerResult testModule = Execute(source, request =>
		{
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
			request.DeclarationParticipationMode = DeclarationParticipationMode.TestModule;
		});

		Assert.Equal(0, production.ExitCode);
		Assert.Contains("normal", production.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("helper", production.StdOut, StringComparison.Ordinal);
		Assert.Equal(0, testModule.ExitCode);
		Assert.Contains("normal", testModule.StdOut, StringComparison.Ordinal);
		Assert.Contains("helper", testModule.StdOut, StringComparison.Ordinal);
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
