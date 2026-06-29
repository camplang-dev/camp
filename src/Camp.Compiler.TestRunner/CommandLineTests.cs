using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CommandLineTests
{
	public CommandLineTests()
	{
		if (GoldenFilterActive())
			Assert.Skip("Command-line tests are skipped when CAMP_TEST_KIND or CAMP_TEST_CASE targets golden tests.");
	}

	[Fact]
	public void Root_command_requires_subcommand()
	{
		ProcessResult result = RunCampc();

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("A command is required", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Old_inspect_option_reports_migration_error()
	{
		ProcessResult result = RunCampc("--inspect", "lowering", "Tests/Lowering/default_arguments.camp");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("replaced by subcommands", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Help_command_prints_command_help()
	{
		ProcessResult root = RunCampc("--help");
		ProcessResult build = RunCampc("help", "build");

		Assert.Equal(0, root.ExitCode);
		Assert.Contains("Commands:", root.StdOut, StringComparison.Ordinal);
		Assert.Equal(0, build.ExitCode);
		Assert.Contains("--artifact", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("--subsystem", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("-f, --framework", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("-r, --reference", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("-u, --use", build.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Dump_lowering_prints_to_stdout()
	{
		ProcessResult result = RunCampc("dump", "lowering", "Tests/Lowering/default_arguments.camp", "--nostdlib");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("addDefault", result.StdOut, StringComparison.Ordinal);
		Assert.Equal("", result.StdErr);
	}

	[Fact]
	public void Build_pragmas_contribute_default_options()
	{
		string temp = CreateTempCase("pragma_none.camp", """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "--build-dir", TempPath("pragma-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: pragma_none.c", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("_api.camp", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Response_file_expands_build_arguments_relative_to_response_file()
	{
		string root = TempPath("response-file-project");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--name sample
			src/*.camp
			""");

		ProcessResult result = RunCampc("build", "@" + buildFile, "--build-dir", TempPath("response-file-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("_api.camp", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Response_file_name_can_omit_campbuild_extension()
	{
		string root = TempPath("response-file-extension");
		Directory.CreateDirectory(root);
		string source = Path.Combine(root, "main.camp");
		File.WriteAllText(source, """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, $"""
			--nostdlib
			--artifact none
			--name sample_extension
			"{source}"
			""");

		ProcessResult result = RunCampc("build", "@" + Path.Combine(root, "sample"), "--build-dir", TempPath("response-file-extension-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_treats_bare_campbuild_file_as_response_file()
	{
		string root = TempPath("bare-campbuild-file");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--name bare_sample
			src/*.camp
			""");

		ProcessResult result = RunCampc("build", buildFile, "--build-dir", TempPath("bare-campbuild-file-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_treats_extensionless_bare_campbuild_name_as_response_file()
	{
		string root = TempPath("bare-campbuild-extensionless");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--name bare_sample_extensionless
			src/*.camp
			""");

		ProcessResult result = RunCampc("build", Path.Combine(root, "sample"), "--build-dir", TempPath("bare-campbuild-extensionless-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Run_treats_bare_campbuild_file_as_response_file()
	{
		string root = TempPath("run-bare-campbuild-file");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact static
			--name run_bare_sample
			src/*.camp
			""");

		ProcessResult result = RunCampc("run", buildFile, "--build-dir", TempPath("run-bare-campbuild-file-build"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("run requires --artifact exec", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("At least one source file pattern is required", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Run_treats_extensionless_bare_campbuild_name_as_response_file()
	{
		string root = TempPath("run-bare-campbuild-extensionless");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact static
			--name run_bare_sample_extensionless
			src/*.camp
			""");

		ProcessResult result = RunCampc("run", Path.Combine(root, "sample"), "--build-dir", TempPath("run-bare-campbuild-extensionless-build"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("run requires --artifact exec", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("At least one source file pattern is required", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Project_reference_builds_static_library_and_includes_api()
	{
		string root = TempPath("project-reference");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		Directory.CreateDirectory(librarySource);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			export int add(int left, int right)
			{
				return left + right;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			src/*.camp
			""");

		string app = CreateTempCase("project_reference_app.camp", """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return add(1, 2) - 3;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			"clang-macos-x64",
			"--project-reference",
			libraryRoot,
			"--build-dir",
			TempPath("project-reference-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: project_reference_app.c", result.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(libraryRoot, "bin", "clang-macos-x64", "default", "DEBUG", "sample-lib_api.camp")));
	}

	[Fact]
	public void Library_api_preserves_implements_generic_constraints()
	{
		string root = TempPath("implements-api");
		Directory.CreateDirectory(root);
		string source = Path.Combine(root, "refcount.camp");
		File.WriteAllText(source, """
			using Std;

			extern void* malloc(nuint size);
			extern void free(void* pointer);

			export escaped interface IRefCount
			{
				void retain();
				void release();
			}

			export escaped T* autorelease<T: implements IRefCount>(
				escaped T* this,
				vtableof(T: IRefCount))
			{
				return this;
			}

			export class RefThing: IRefCount
			{
				void retain() {}
				void release() {}
			}
			""");
		string secondSource = Path.Combine(root, "consumer.camp");
		File.WriteAllText(secondSource, """
			using Std;

			export interface INamed
			{
				string getName();
			}

			export struct NamedRef: INamed
			{
				string getName() => "named";
			}
			""");

		string outDir = TempPath("implements-api-out");
		ProcessResult result = RunCampc(
			"build",
			source,
			secondSource,
			"--nostdlib",
			"--artifact",
			"static",
			"--name",
			"refcount",
			"--out-dir",
			outDir,
			"--build-dir",
			TempPath("implements-api-build"));

		Assert.Equal(0, result.ExitCode);
		string api = File.ReadAllText(Path.Combine(outDir, "refcount_api.camp"));
		Assert.Contains("autorelease<T: implements IRefCount>", api, StringComparison.Ordinal);
		Assert.DoesNotContain("autorelease<T: IRefCount>", api, StringComparison.Ordinal);
		Assert.Contains("export extern class RefThing : IRefCount", api, StringComparison.Ordinal);
		Assert.Contains("export struct NamedRef", api, StringComparison.Ordinal);
		Assert.DoesNotContain("export struct NamedRef : INamed", api, StringComparison.Ordinal);
		Assert.Equal(1, CountOccurrences(api, "using Std;"));
	}

	[Fact]
	public void Project_reference_consumes_exported_interface_accessors_and_vtables()
	{
		string root = TempPath("project-reference-interface-accessors");
		string libraryRoot = Path.Combine(root, "interfaces");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "interfaces.camp"), """
			extern void* malloc(nuint size);
			extern void free(void* pointer);

			export interface IValue
			{
				export int value();
			}

			export class Counter: IValue
			{
				int value()
				{
					return 1;
				}
			}

			export extern class NativeCounter: IValue
			{
			}

			export extern class NativeDerived: NativeCounter
			{
			}

			export struct StructCounter: IValue
			{
				int value()
				{
					return 2;
				}
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "interfaces.campbuild"), """
			--nostdlib
			--name interfaces
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name interface-app

			int readValue(IValue* value)
			{
				return value.value();
			}

			int readGeneric<T: implements IValue>(T* value, vtableof(T: IValue))
			{
				return value.value();
			}

			export int main()
			{
				auto counter = new Counter() finally delete;
				int total = 0;
				IValue* assigned = counter;
				total += readValue(assigned);
				total += readValue((IValue*)counter);
				total += readValue(counter.IValue);
				total += readValue(counter.getIValue());
				total += readGeneric<Counter>(counter);
				return total == 5 ? 0 : total;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");
		string buildDir = Path.Combine(appRoot, "obj");
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot,
			"--build-dir",
			buildDir,
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		Assert.Contains("generated: interface-app", result.StdOut, StringComparison.Ordinal);
		string api = File.ReadAllText(Path.Combine(libraryRoot, "bin", target, "default", "DEBUG", "interfaces_api.camp"));
		Assert.Contains("export extern class Counter : IValue", api, StringComparison.Ordinal);
		Assert.Contains("export extern constof(this) IValue* getIValue();", api, StringComparison.Ordinal);
		Assert.Contains("export extern class NativeCounter : IValue", api, StringComparison.Ordinal);
		Assert.Contains("export extern class NativeDerived : NativeCounter", api, StringComparison.Ordinal);
		Assert.Contains("export struct StructCounter", api, StringComparison.Ordinal);
		Assert.DoesNotContain("export struct StructCounter : IValue", api, StringComparison.Ordinal);
		string cApi = File.ReadAllText(Path.Combine(buildDir, "interfaces_api.h"));
		Assert.Contains("extern const IValue *Counter_IValue;", cApi, StringComparison.Ordinal);
		Assert.DoesNotContain("StructCounter_IValue", cApi, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(outDir, "interface-app" + ExecutableExtensionForHost()));
		Assert.Equal(0, run.ExitCode);
		Assert.Equal("", run.StdErr);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Default_windows_target_follows_visual_studio_environment()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("MSVC default target selection only applies on Windows.");
		string temp = CreateTempCase("msvc-default-target/main.camp", """
			#if WIN32
			export int selectedTarget()
			{
				return 86;
			}
			#endif

			#if WIN64
			export int selectedTarget()
			{
				return 64;
			}
			#endif
			""");

		ProcessResult x86 = RunCampc(
			new Dictionary<string, string?> { ["VSCMD_ARG_TGT_ARCH"] = "x86", ["Platform"] = null },
			"dump",
			"declarations",
			temp,
			"--nostdlib");
		ProcessResult x64 = RunCampc(
			new Dictionary<string, string?> { ["VSCMD_ARG_TGT_ARCH"] = "x64", ["Platform"] = null },
			"dump",
			"declarations",
			temp,
			"--nostdlib");

		Assert.Equal(0, x86.ExitCode);
		Assert.Contains("return 86", x86.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("return 64", x86.StdOut, StringComparison.Ordinal);
		Assert.Equal(0, x64.ExitCode);
		Assert.Contains("return 64", x64.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("return 86", x64.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Msvc_target_requires_loaded_visual_studio_environment()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("MSVC environment validation only applies on Windows.");
		string temp = CreateTempCase("msvc-environment-missing/main.camp", """
			export int value()
			{
				return 1;
			}
			""");

		ProcessResult result = RunCampc(
			new Dictionary<string, string?> { ["VSCMD_ARG_TGT_ARCH"] = null, ["Platform"] = null },
			"build",
			temp,
			"--nostdlib",
			"--artifact",
			"static",
			"--target",
			"msvc-windows-x64",
			"--build-dir",
			TempPath("msvc-environment-missing-build"),
			"--out-dir",
			TempPath("msvc-environment-missing-out"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Target 'msvc-windows-x64' requires a Visual Studio C++ environment", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("Native build command failed", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Msvc_target_architecture_must_match_visual_studio_environment()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("MSVC environment validation only applies on Windows.");
		string temp = CreateTempCase("msvc-architecture-mismatch/main.camp", """
			export int value()
			{
				return 1;
			}
			""");

		ProcessResult result = RunCampc(
			new Dictionary<string, string?> { ["VSCMD_ARG_TGT_ARCH"] = "x86" },
			"build",
			temp,
			"--nostdlib",
			"--artifact",
			"static",
			"--target",
			"msvc-windows-x64",
			"--build-dir",
			TempPath("msvc-architecture-mismatch-build"),
			"--out-dir",
			TempPath("msvc-architecture-mismatch-out"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Target 'msvc-windows-x64' requires MSVC target architecture 'x64'", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("current Visual Studio environment targets 'x86'", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Project_reference_links_native_static_library_with_msvc()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools are not available on PATH.");
		string root = TempPath("project-reference-msvc");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "sample-app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			export int add(int left, int right)
			{
				return left + right;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "sample-app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name sample-app

			export int main()
			{
				return add(20, 22) - 42;
			}
			""");
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot,
			"--build-dir",
			Path.Combine(appRoot, "obj"),
			"--out-dir",
			Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(result);
		Assert.Contains("generated: sample-app.exe", result.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(libraryRoot, "bin", target, "default", "DEBUG", "sample-lib.lib")));
		Assert.True(File.Exists(Path.Combine(libraryRoot, "bin", target, "default", "DEBUG", "sample-lib_api.camp")));
		Assert.True(File.Exists(Path.Combine(appRoot, "obj", "sample_lib_api.h")));
		ProcessResult run = RunExecutable(Path.Combine(appRoot, "bin", "sample-app.exe"));
		Assert.Equal(0, run.ExitCode);
		Assert.Equal("", run.StdErr);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Project_reference_rebuilds_native_static_library_when_source_changes()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools are not available on PATH.");
		string root = TempPath("project-reference-msvc-rebuild");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "sample-app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		string libraryFile = Path.Combine(librarySource, "library.camp");
		File.WriteAllText(libraryFile, """
			export int getValue()
			{
				return 1;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "sample-app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name sample-app

			export int main()
			{
				return getValue();
			}
			""");
		string target = NativeTargetForHost();
		string libraryPath = Path.Combine(libraryRoot, "bin", target, "default", "DEBUG", "sample-lib.lib");
		string executablePath = Path.Combine(appRoot, "bin", "sample-app.exe");

		ProcessResult firstBuild = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot,
			"--build-dir",
			Path.Combine(appRoot, "obj"),
			"--out-dir",
			Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(firstBuild);
		Assert.Contains($"{libraryRoot}: generated: sample-lib.lib", firstBuild.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(libraryPath));
		DateTime firstLibraryWrite = File.GetLastWriteTimeUtc(libraryPath);
		ProcessResult firstRun = RunExecutable(executablePath);
		Assert.Equal(1, firstRun.ExitCode);

		File.WriteAllText(libraryFile, """
			export int getValue()
			{
				return 2;
			}
			""");

		ProcessResult secondBuild = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot,
			"--build-dir",
			Path.Combine(appRoot, "obj"),
			"--out-dir",
			Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(secondBuild);
		Assert.Contains($"{libraryRoot}: generated: sample-lib.lib", secondBuild.StdOut, StringComparison.Ordinal);
		Assert.True(File.GetLastWriteTimeUtc(libraryPath) >= firstLibraryWrite);
		ProcessResult secondRun = RunExecutable(executablePath);
		Assert.Equal(2, secondRun.ExitCode);
	}

	[Fact]
	public void Virtual_base_layout_is_lowered_before_out_of_order_derived_class()
	{
		string root = TempPath("virtual-out-of-order");
		Directory.CreateDirectory(root);
		string derived = Path.Combine(root, "derived.camp");
		string baseFile = Path.Combine(root, "base.camp");
		File.WriteAllText(derived, """
			export sealed class Derived: Base
			{
				export override int value()
				{
					return 2;
				}
			}
			""");
		File.WriteAllText(baseFile, """
			export virtual class Base
			{
				export virtual int value()
				{
					return 1;
				}
			}
			""");
		string buildDir = TempPath("virtual-out-of-order-build");

		ProcessResult result = RunCampc("build", derived, baseFile, "--artifact", "none", "--build-dir", buildDir);

		Assert.Equal(0, result.ExitCode);
		string privateHeader = Directory.GetFiles(buildDir, "*_private.h").Single();
		string header = File.ReadAllText(privateHeader);
		Assert.Contains("_Base *_vt;", header, StringComparison.Ordinal);
		Assert.DoesNotContain("_Derived *_vt;", header, StringComparison.Ordinal);
	}

	[Fact]
	public void Project_reference_api_uses_inherited_virtual_surface_for_overrides()
	{
		string root = TempPath("project-reference-virtual-api");
		string libraryRoot = Path.Combine(root, "widgets");
		string librarySource = Path.Combine(libraryRoot, "src");
		Directory.CreateDirectory(librarySource);
		File.WriteAllText(Path.Combine(librarySource, "alloc.camp"), """
			export extern void* malloc(nuint size);
			export extern void free(void* ptr);
			""");
		File.WriteAllText(Path.Combine(librarySource, "button.camp"), """
			export sealed escaped class Button: Control
			{
				export override int value()
				{
					return 2;
				}
			}
			""");
		File.WriteAllText(Path.Combine(librarySource, "control.camp"), """
			export virtual escaped class Control
			{
				export virtual int value()
				{
					return 1;
				}
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "widgets.campbuild"), """
			--nostdlib
			--name widgets
			src/*.camp
			""");
		string app = CreateTempCase("project_reference_virtual_api_app.camp", """
			#build --nostdlib
			#build --artifact none

			export int readButton(Button* button)
			{
				Control* control = button;
				return control.value() - 2;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			"clang-macos-x64",
			"--project-reference",
			libraryRoot,
			"--build-dir",
			TempPath("project-reference-virtual-api-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: project_reference_virtual_api_app.c", result.StdOut, StringComparison.Ordinal);
		string api = File.ReadAllText(Path.Combine(libraryRoot, "bin", "clang-macos-x64", "default", "DEBUG", "widgets_api.camp"));
		Assert.Contains("export escaped extern class Control", api, StringComparison.Ordinal);
		Assert.Contains("export escaped extern class Button : Control", api, StringComparison.Ordinal);
		Assert.Contains("export extern int value();", api, StringComparison.Ordinal);
		Assert.DoesNotContain("virtual class", api, StringComparison.Ordinal);
		Assert.DoesNotContain("sealed escaped class", api, StringComparison.Ordinal);
		Assert.DoesNotContain("virtual int value", api, StringComparison.Ordinal);
		Assert.DoesNotContain("override int value", api, StringComparison.Ordinal);
		Assert.Equal(1, CountOccurrences(api, "int value();"));
	}

	[Fact]
	public void Use_source_resolves_live_unversioned_package_sources()
	{
		string root = TempPath("live-use-source");
		string sourceRoot = Path.Combine(root, "packages");
		string packageSource = Path.Combine(sourceRoot, "live-demo", "src");
		Directory.CreateDirectory(packageSource);
		string packageFile = Path.Combine(packageSource, "demo.camp");
		string sourceRootArgument = sourceRoot.Replace('\\', '/');
		File.WriteAllText(packageFile, """
			export int liveValue()
			{
				return 1;
			}
			""");
		string app = CreateTempCase("live_use_source_app.camp", $$"""
			#build --nostdlib
			#build --artifact none
			#build --use-source local "{{sourceRootArgument}}"
			#build --use live-demo

			export int main()
			{
				return liveValue() - 1;
			}
			""");

		ProcessResult first = RunCampc("build", app, "--build-dir", TempPath("live-use-source-build-1"));

		Assert.Equal(0, first.ExitCode);
		Assert.Contains("generated: live_use_source_app.c", first.StdOut, StringComparison.Ordinal);

		File.WriteAllText(packageFile, """
			export int liveChanged()
			{
				return 2;
			}
			""");
		File.SetLastWriteTimeUtc(packageFile, DateTime.UtcNow.AddSeconds(5));
		File.WriteAllText(app, $$"""
			#build --nostdlib
			#build --artifact none
			#build --use-source local "{{sourceRootArgument}}"
			#build --use live-demo

			export int main()
			{
				return liveChanged() - 2;
			}
			""");

		ProcessResult second = RunCampc("build", app, "--build-dir", TempPath("live-use-source-build-2"));

		Assert.Equal(0, second.ExitCode);
		Assert.Contains("generated: live_use_source_app.c", second.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Response_file_use_option_does_not_consume_source_patterns()
	{
		string root = TempPath("response-use-source-pattern");
		string sourceRoot = Path.Combine(root, "packages");
		string packageSource = Path.Combine(sourceRoot, "live-demo", "src");
		string appRoot = Path.Combine(root, "app");
		string appSource = Path.Combine(appRoot, "src");
		Directory.CreateDirectory(packageSource);
		Directory.CreateDirectory(appSource);
		File.WriteAllText(Path.Combine(packageSource, "demo.camp"), """
			export int liveValue()
			{
				return 42;
			}
			""");
		File.WriteAllText(Path.Combine(appSource, "main.camp"), """
			export int main()
			{
				return liveValue() - 42;
			}
			""");
		string buildFile = Path.Combine(appRoot, "app.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--name app
			--use-source local ../packages
			--use live-demo
			src/*.camp
			""");

		ProcessResult result = RunCampc("build", "@" + buildFile);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Include_files_contribute_build_pragmas_without_becoming_project_sources()
	{
		string api = CreateTempCase("include_pragmas_api.camp", """
			#build --nostdlib
			#build --artifact none

			export extern void includedOnly();
			""");
		string source = CreateTempCase("include_pragmas_main.camp", """
			export void main()
			{
			}
			""");

		ProcessResult result = RunCampc("build", source, "-i", api, "--build-dir", TempPath("include-pragma-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: include_pragmas_main.c", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("include_pragmas_api.c", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("_api.camp", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Metadata_omits_standard_library_declarations()
	{
		string temp = CreateTempCase("metadata_std_filter.camp", """
			using Std;

			export int meaning()
			{
				Console.writeLine("hi");
				return 42;
			}
			""");
		string outDir = TempPath("metadata-std-filter-out");

		ProcessResult result = RunCampc("build", temp, "--artifact", "none", "--metadata", "export", "--out-dir", outDir, "--name", "metadata_std_filter");

		Assert.Equal(0, result.ExitCode);
		string metadataPath = Path.Combine(outDir, "metadata_std_filter_api.json");
		using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
		string[] declarationNames = metadata.RootElement.GetProperty("declarations")
			.EnumerateArray()
			.Select(static declaration => declaration.GetProperty("name").GetString() ?? "")
			.ToArray();
		Assert.Contains("meaning", declarationNames);
		Assert.DoesNotContain("Console", declarationNames);
		Assert.DoesNotContain("Allocator", declarationNames);
		Assert.DoesNotContain("malloc", declarationNames);
	}

	[Fact]
	public void Include_pragmas_discovered_from_source_pragmas_contribute_build_pragmas()
	{
		string api = CreateTempCase("discovered_include_pragmas_api.camp", """
			#build --reference missing-one.a missing-two.a

			export extern void includedOnly();
			""");
		string source = CreateTempCase("discovered_include_pragmas_main.camp", $$"""
			#build --nostdlib
			#build --artifact exec
			#build --include {{api}}

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", source, "--target", NativeTargetForHost(), "--build-dir", TempPath("discovered-include-pragma-build"), "--out-dir", TempPath("discovered-include-pragma-out"));
		string output = result.StdOut + result.StdErr;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("missing-one.a", output, StringComparison.Ordinal);
		Assert.Contains("missing-two.a", output, StringComparison.Ordinal);
		Assert.DoesNotContain("discovered_include_pragmas_api.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Run_rejects_non_exec_artifact_before_building()
	{
		string temp = CreateTempCase("run_static.camp", """
			#build --nostdlib

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("run", temp, "--artifact", "static");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("run requires --artifact exec", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_pragma_rejects_unknown_subsystem()
	{
		string temp = CreateTempCase("subsystem_foobar.camp", """
			#build --nostdlib
			#build --artifact exec
			#build --subsystem foobar

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp);

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Subsystem 'foobar' is not valid. Expected windows.", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("generated:", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Reference_alias_accepts_multiple_values()
	{
		string temp = CreateTempCase("reference_alias.camp", """
			#build --nostdlib
			#build --artifact exec

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "-r", "missing-one.a", "missing-two.a", "--target", NativeTargetForHost(), "--build-dir", TempPath("reference-build"), "--out-dir", TempPath("reference-out"));
		string output = result.StdOut + result.StdErr;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("missing-one.a", output, StringComparison.Ordinal);
		Assert.Contains("missing-two.a", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Framework_alias_accepts_multiple_values()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Framework linker flag shape is only valid on macOS targets.");
		string temp = CreateTempCase("framework_alias.camp", """
			#build --nostdlib
			#build --artifact exec

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "-f", "MissingOne", "MissingTwo", "--target", "clang-macos-x64", "--build-dir", TempPath("framework-build"), "--out-dir", TempPath("framework-out"));
		string output = result.StdOut + result.StdErr;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("-framework MissingOne", output, StringComparison.Ordinal);
		Assert.Contains("-framework MissingTwo", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_pragmas_allow_multiple_frameworks_after_switch()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Framework linker flag shape is only valid on macOS targets.");
		string temp = CreateTempCase("framework_pragma_multi.camp", """
			#build --nostdlib
			#build --artifact exec
			#build --framework MissingOne MissingTwo

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "--target", "clang-macos-x64", "--build-dir", TempPath("framework-pragma-build"), "--out-dir", TempPath("framework-pragma-out"));
		string output = result.StdOut + result.StdErr;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("-framework MissingOne", output, StringComparison.Ordinal);
		Assert.Contains("-framework MissingTwo", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Frameworks_are_rejected_on_targets_that_do_not_allow_them()
	{
		string temp = CreateTempCase("framework_unsupported_target.camp", """
			#build --nostdlib
			#build --artifact exec

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "--target", "msvc-windows-x64", "--framework", "Cocoa");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Target 'msvc-windows-x64' does not support framework linking.", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("Native build command", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Frameworks_are_rejected_for_static_artifacts()
	{
		string temp = CreateTempCase("framework_static.camp", """
			#build --nostdlib
			#build --artifact static

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "--target", "clang-macos-x64", "--framework", "Cocoa");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("--framework cannot be used with --artifact static.", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("Native build command", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Use_alias_accepts_multiple_values()
	{
		string temp = CreateTempCase("use_alias.camp", """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "-u", "missing-one@1.0.0", "missing-two@1.0.0");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Package 'missing-one@1.0.0' is not installed.", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_pragmas_allow_multiple_references_after_switch()
	{
		string temp = CreateTempCase("reference_pragma_multi.camp", """
			#build --nostdlib
			#build --artifact exec
			#build --reference missing-one.a missing-two.a

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "--target", NativeTargetForHost(), "--build-dir", TempPath("reference-pragma-build"), "--out-dir", TempPath("reference-pragma-out"));
		string output = result.StdOut + result.StdErr;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("missing-one.a", output, StringComparison.Ordinal);
		Assert.Contains("missing-two.a", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Windows_subsystem_uses_windows_executable_template()
	{
		string temp = CreateTempCase("subsystem_windows.camp", """
			#build --nostdlib

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "--artifact", "exec", "--subsystem", "windows", "--target", "clang-macos-x64");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("does not define a [build] winexe template", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Gcc_linux_x64_target_builds_executable_on_linux()
	{
		if (!OperatingSystem.IsLinux())
			Assert.Skip("Linux GCC target smoke tests only run on Linux.");
		if (!GccCanLink("-m64"))
			Assert.Skip("gcc -m64 cannot link a native executable on this host.");

		string temp = CreateTempCase("gcc_linux_x64.camp", """
			#build --nostdlib
			#build --artifact exec
			#build --name gcc-linux-x64-smoke

			export int main()
			{
				return 0;
			}
			""");
		string outDir = TempPath("gcc-linux-x64-out");

		ProcessResult result = RunCampc("build", temp, "--target", "gcc-linux-x64", "--build-dir", TempPath("gcc-linux-x64-build"), "--out-dir", outDir);

		AssertCommandSucceeded(result);
		Assert.Contains("generated: gcc-linux-x64-smoke", result.StdOut, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(outDir, "gcc-linux-x64-smoke"));
		Assert.Equal(0, run.ExitCode);
	}

	[Fact]
	public void Gcc_linux_x86_target_builds_executable_on_linux_when_multilib_is_available()
	{
		if (!OperatingSystem.IsLinux())
			Assert.Skip("Linux GCC target smoke tests only run on Linux.");
		if (!GccCanLink("-m32"))
			Assert.Skip("gcc -m32 cannot link a native executable on this host.");

		string temp = CreateTempCase("gcc_linux_x86.camp", """
			#build --nostdlib
			#build --artifact exec
			#build --name gcc-linux-x86-smoke

			export int main()
			{
				return 0;
			}
			""");
		string outDir = TempPath("gcc-linux-x86-out");

		ProcessResult result = RunCampc("build", temp, "--target", "gcc-linux-x86", "--build-dir", TempPath("gcc-linux-x86-build"), "--out-dir", outDir);

		AssertCommandSucceeded(result);
		Assert.Contains("generated: gcc-linux-x86-smoke", result.StdOut, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(outDir, "gcc-linux-x86-smoke"));
		Assert.Equal(0, run.ExitCode);
	}

	[Fact]
	public void Package_source_can_be_added_and_searched_locally()
	{
		string tempRoot = TempPath("pkg-search");
		Directory.CreateDirectory(Path.Combine(tempRoot, "source", "demo", "1.2.3", "src"));
		File.WriteAllText(Path.Combine(tempRoot, "source", "demo", "1.2.3", "src", "demo.camp"), "export int value = 1;\n");
		string localFile = Path.Combine(tempRoot, "local.camp");
		File.WriteAllText(localFile, "// local package config\n");

		ProcessResult add = RunCampc("pkg", "add-source", "local", Path.Combine(tempRoot, "source"), "--local", localFile);
		ProcessResult search = RunCampc("pkg", "search", "demo", "--local", localFile);

		Assert.Equal(0, add.ExitCode);
		Assert.Equal(0, search.ExitCode);
		Assert.Contains("local: demo@1.2.3", search.StdOut, StringComparison.Ordinal);
	}

	static ProcessResult RunCampc(params string[] arguments)
	{
		return RunCampc(null, arguments);
	}

	static ProcessResult RunCampc(IReadOnlyDictionary<string, string?>? environmentVariables, params string[] arguments)
	{
		string repositoryRoot = FindRepositoryRoot();
		string executable = Path.Combine(repositoryRoot, "bin", OperatingSystem.IsWindows() ? "campc.exe" : "campc");
		ProcessStartInfo info = new()
		{
			FileName = executable,
			WorkingDirectory = repositoryRoot,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		foreach (string argument in arguments)
			info.ArgumentList.Add(argument);
		if (environmentVariables is not null)
		{
			foreach ((string key, string? value) in environmentVariables)
			{
				if (value is null)
					info.Environment.Remove(key);
				else
					info.Environment[key] = value;
			}
		}

		using Process process = new() { StartInfo = info };
		process.Start();
		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, Normalize(stdout), Normalize(stderr));
	}

	static ProcessResult RunExecutable(string executable)
	{
		ProcessStartInfo info = new()
		{
			FileName = executable,
			WorkingDirectory = FindRepositoryRoot(),
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		using Process process = new() { StartInfo = info };
		process.Start();
		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, Normalize(stdout), Normalize(stderr));
	}

	static ProcessResult RunProcess(string executable, IReadOnlyList<string> arguments, string workingDirectory)
	{
		ProcessStartInfo info = new(executable)
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		foreach (string argument in arguments)
			info.ArgumentList.Add(argument);

		using Process process = new() { StartInfo = info };
		process.Start();
		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, Normalize(stdout), Normalize(stderr));
	}

	static void AssertCommandSucceeded(ProcessResult result)
	{
		Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}.\nSTDOUT:\n{result.StdOut}\nSTDERR:\n{result.StdErr}");
	}

	static string NativeTargetForHost()
	{
		if (OperatingSystem.IsLinux())
			return "gcc-linux-x64";
		if (!OperatingSystem.IsWindows())
			return "clang-macos-x64";
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools and target architecture are not available.");
		return CompilerDefaults.TargetName;
	}

	static string ExecutableExtensionForHost()
	{
		return OperatingSystem.IsWindows() ? ".exe" : "";
	}

	static bool MsvcAvailable()
	{
		return OperatingSystem.IsWindows() && MsvcEnvironment.TargetArchitecture is "x64" or "x86" && ToolAvailable("cl") && ToolAvailable("lib");
	}

	static bool GccCanLink(string architectureFlag)
	{
		if (!ToolAvailable("gcc"))
			return false;

		string root = TempPath("gcc-link-smoke-" + architectureFlag.TrimStart('-'));
		Directory.CreateDirectory(root);
		string source = Path.Combine(root, "main.c");
		string output = Path.Combine(root, "main");
		File.WriteAllText(source, "int main(void) { return 0; }\n");
		ProcessResult result = RunProcess("gcc", [architectureFlag, source, "-o", output], root);
		return result.ExitCode == 0 && File.Exists(output);
	}

	static bool ToolAvailable(string tool)
	{
		string[] extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string directory in GetPathValues().SelectMany(static value => value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)))
		{
			string trimmed = directory.Trim();
			if (File.Exists(Path.Combine(trimmed, tool)))
				return true;
			foreach (string extension in extensions)
			{
				if (File.Exists(Path.Combine(trimmed, tool + extension)))
					return true;
			}
		}
		return false;
	}

	static IEnumerable<string> GetPathValues()
	{
		foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
		{
			if (entry.Key is string key && key.Equals("PATH", StringComparison.OrdinalIgnoreCase) && entry.Value is string value)
				yield return value;
		}
	}

	static string CreateTempCase(string name, string text)
	{
		string path = TempPath(name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal));
		return path;
	}

	static string TempPath(string name) => Path.Combine(FindRepositoryRoot(), "tmp", "cli-tests", name);

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

	static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

	static int CountOccurrences(string text, string value)
	{
		int count = 0;
		int index = 0;
		while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += value.Length;
		}
		return count;
	}

	static bool GoldenFilterActive()
	{
		return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAMP_TEST_KIND"))
			|| !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAMP_TEST_CASE"));
	}

	readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}
