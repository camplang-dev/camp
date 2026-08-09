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
	static readonly Lazy<bool> ClangWasiAvailability = new(ProbeClangWasiAvailable);
	static readonly Lazy<bool> EmscriptenAvailability = new(ProbeEmscriptenAvailable);

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
		ProcessResult result = RunCampc("--inspect", "lowering", "tests/Lowering/default_arguments.camp");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("replaced by subcommands", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_dir_option_reports_migration_error()
	{
		ProcessResult result = RunCampc("build", "tests/Lowering/default_arguments.camp", "--artifact", "none", "--build-dir", TempPath("removed-build-dir"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("--build-dir has been removed", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("output artifact directory's build subdirectory", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Include_option_is_rejected()
	{
		ProcessResult result = RunCampc("build", "tests/Lowering/default_arguments.camp", "--include", "api.camp");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Unknown option '--include'", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Include_short_option_is_rejected()
	{
		ProcessResult result = RunCampc("build", "tests/Lowering/default_arguments.camp", "-i", "api.camp");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Unknown option '-i'", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Help_command_prints_command_help()
	{
		ProcessResult root = RunCampc("--help");
		ProcessResult init = RunCampc("help", "init");
		ProcessResult build = RunCampc("help", "build");
		ProcessResult test = RunCampc("help", "test");

		Assert.Equal(0, root.ExitCode);
		Assert.Contains("Commands:", root.StdOut, StringComparison.Ordinal);
		Assert.Equal(0, init.ExitCode);
		Assert.Contains("--template", init.StdOut, StringComparison.Ordinal);
		Assert.Contains("--list", init.StdOut, StringComparison.Ordinal);
		Assert.Equal(0, build.ExitCode);
		Assert.Contains("--artifact", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("--subsystem", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("-f, --framework", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("-r, --reference", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("-u, --use", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("--api", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("--debug-info", build.StdOut, StringComparison.Ordinal);
		Assert.Equal(0, test.ExitCode);
		Assert.Contains("--list", test.StdOut, StringComparison.Ordinal);
		Assert.Contains("--filter", test.StdOut, StringComparison.Ordinal);
		Assert.Contains("--test-output-dir", test.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Version_command_prints_camp_version()
	{
		ProcessResult result = RunCampc("--version");

		Assert.Equal(0, result.ExitCode);
		Assert.Empty(result.StdErr);
		Assert.Matches(@"^v\d+\.\d+\.\d+(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?\+[0-9a-f]{40}\n$", result.StdOut);
	}

	[Fact]
	public void Init_validates_arguments_and_lists_templates()
	{
		string root = TempPath("init-validation");
		ResetDirectory(root);

		ProcessResult missingName = RunCampcIn(root, "init");
		ProcessResult list = RunCampcIn(root, "init", "--list");
		ProcessResult unknown = RunCampcIn(root, "init", "sample", "--template", "unknown");
		Directory.CreateDirectory(Path.Combine(root, "existing"));
		ProcessResult existing = RunCampcIn(root, "init", "existing");

		Assert.NotEqual(0, missingName.ExitCode);
		Assert.Contains("init requires a project name.", missingName.StdErr, StringComparison.Ordinal);
		AssertCommandSucceeded(list);
		foreach (string template in new[] { "app", "static", "shared", "posix-api", "windows-api", "wrapper" })
			Assert.Contains(template, list.StdOut, StringComparison.Ordinal);
		Assert.NotEqual(0, unknown.ExitCode);
		Assert.Contains("Unknown init template 'unknown'", unknown.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, existing.ExitCode);
		Assert.Contains("Directory 'existing' already exists.", existing.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Init_creates_expected_templates_and_generated_samples_build_or_run()
	{
		string root = TempPath("init-templates");
		ResetDirectory(root);
		string target = NativeTargetForHost();

		ProcessResult appInit = RunCampcIn(root, "init", "hello");
		AssertCommandSucceeded(appInit);
		Assert.True(File.Exists(Path.Combine(root, "hello", "hello.campbuild")));
		Assert.Equal("src/*.camp\n", File.ReadAllText(Path.Combine(root, "hello", "hello.campbuild")).Replace("\r\n", "\n", StringComparison.Ordinal));
		Assert.Contains("export int main(string[] args)", File.ReadAllText(Path.Combine(root, "hello", "src", "main.camp")), StringComparison.Ordinal);
		ProcessResult appRun = RunCampcIn(root, "run", Path.Combine("hello", "hello.campbuild"), "--target", target, "--out-dir", Path.Combine(root, "hello", "out"));
		AssertCommandSucceeded(appRun);
		Assert.Contains("Hello, world!", appRun.StdOut, StringComparison.Ordinal);

		ProcessResult staticInit = RunCampcIn(root, "init", "math-lib", "--template", "static");
		AssertCommandSucceeded(staticInit);
		string staticSource = File.ReadAllText(Path.Combine(root, "math-lib", "src", "main.camp"));
		Assert.Contains("namespace MathLib;", staticSource, StringComparison.Ordinal);
		Assert.Contains("void testAdd(thrown Assertion*)", staticSource, StringComparison.Ordinal);
		ProcessResult staticTest = RunCampcIn(root, "test", Path.Combine("math-lib", "math-lib.campbuild"), "--target", target, "--out-dir", Path.Combine(root, "math-lib", "out"));
		AssertCommandSucceeded(staticTest);
		Assert.Contains("passed: MathLib::testAdd", staticTest.StdOut, StringComparison.Ordinal);

		ProcessResult sharedInit = RunCampcIn(root, "init", "my-sharedlib", "--template", "shared");
		AssertCommandSucceeded(sharedInit);
		Assert.Contains("--artifact shared", File.ReadAllText(Path.Combine(root, "my-sharedlib", "my-sharedlib.campbuild")), StringComparison.Ordinal);
		Assert.Contains("export int mysharedlib_add", File.ReadAllText(Path.Combine(root, "my-sharedlib", "src", "main.camp")), StringComparison.Ordinal);
		ProcessResult sharedTest = RunCampcIn(root, "test", Path.Combine("my-sharedlib", "my-sharedlib.campbuild"), "--target", target, "--out-dir", Path.Combine(root, "my-sharedlib", "out"));
		AssertCommandSucceeded(sharedTest);
		Assert.Contains("passed: testAdd", sharedTest.StdOut, StringComparison.Ordinal);

		ProcessResult posixInit = RunCampcIn(root, "init", "posix-api", "--template", "posix-api");
		AssertCommandSucceeded(posixInit);
		Assert.False(File.Exists(Path.Combine(root, "posix-api", "src", "main.camp")));
		string posixSource = File.ReadAllText(Path.Combine(root, "posix-api", "src", "posix.camp"));
		Assert.Contains("@symbol(\"getpid\")", posixSource, StringComparison.Ordinal);
		Assert.Contains("public extern int getpid();", posixSource, StringComparison.Ordinal);
		Assert.Contains("--api ../posix-api/src/*.camp", File.ReadAllText(Path.Combine(root, "posix-api", "README.md")), StringComparison.Ordinal);
		AssertCommandSucceeded(RunCampcIn(root, "build", Path.Combine("posix-api", "posix-api.campbuild"), "--out-dir", Path.Combine(root, "posix-api", "out")));

		ProcessResult windowsInit = RunCampcIn(root, "init", "windows-api", "--template", "windows-api");
		AssertCommandSucceeded(windowsInit);
		Assert.False(File.Exists(Path.Combine(root, "windows-api", "src", "main.camp")));
		string windowsSource = File.ReadAllText(Path.Combine(root, "windows-api", "src", "windows.camp"));
		Assert.Contains("@symbol(\"GetCurrentProcessId\")", windowsSource, StringComparison.Ordinal);
		Assert.Contains("public extern uint GetCurrentProcessId();", windowsSource, StringComparison.Ordinal);
		Assert.Contains("--api ../windows-api/src/*.camp", File.ReadAllText(Path.Combine(root, "windows-api", "README.md")), StringComparison.Ordinal);
		AssertCommandSucceeded(RunCampcIn(root, "build", Path.Combine("windows-api", "windows-api.campbuild"), "--out-dir", Path.Combine(root, "windows-api", "out")));

		ProcessResult wrapperInit = RunCampcIn(root, "init", "native-pid", "--template", "wrapper");
		AssertCommandSucceeded(wrapperInit);
		string wrapperBuild = File.ReadAllText(Path.Combine(root, "native-pid", "native-pid.campbuild"));
		string wrapperSource = File.ReadAllText(Path.Combine(root, "native-pid", "src", "main.camp"));
		Assert.Contains("--artifact static", wrapperBuild, StringComparison.Ordinal);
		Assert.Contains("#if POSIX", wrapperSource, StringComparison.Ordinal);
		Assert.Contains("#elif WINDOWS", wrapperSource, StringComparison.Ordinal);
		Assert.Contains("namespace global", wrapperSource, StringComparison.Ordinal);
		Assert.Contains("extern int getpid();", wrapperSource, StringComparison.Ordinal);
		Assert.Contains("extern uint GetCurrentProcessId();", wrapperSource, StringComparison.Ordinal);
		Assert.Contains("global::getpid()", wrapperSource, StringComparison.Ordinal);
		Assert.Contains("global::GetCurrentProcessId()", wrapperSource, StringComparison.Ordinal);
		Assert.Contains("getCurrentProcessId", wrapperSource, StringComparison.Ordinal);
		ProcessResult wrapperTest = RunCampcIn(root, "test", Path.Combine("native-pid", "native-pid.campbuild"), "--target", target, "--out-dir", Path.Combine(root, "native-pid", "out"));
		AssertCommandSucceeded(wrapperTest);
		Assert.Contains("passed: NativePid::testGetCurrentProcessId", wrapperTest.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Campc_runs_from_installed_layout_outside_repository()
	{
		string repositoryRoot = FindRepositoryRoot();
		string installRoot = TempPath("installed-layout");
		if (Directory.Exists(installRoot))
			Directory.Delete(installRoot, recursive: true);
		string installBin = Path.Combine(installRoot, "bin");
		Directory.CreateDirectory(installBin);
		string sourceCampc = TestToolPaths.GetCampcPath(repositoryRoot);
		CopyDirectory(Path.GetDirectoryName(sourceCampc)!, installBin);
		CopyDirectory(Path.Combine(repositoryRoot, "lib"), Path.Combine(installRoot, "lib"));
		CopyDirectory(Path.Combine(repositoryRoot, "targets"), Path.Combine(installRoot, "targets"));
		Directory.CreateDirectory(Path.Combine(installRoot, "cache", "lib"));
		Directory.CreateDirectory(Path.Combine(installRoot, "cache", "pkg"));

		string projectRoot = Path.Combine(Path.GetTempPath(), "camp-installed-layout-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(projectRoot);
		string source = Path.Combine(projectRoot, "main.camp");
		File.WriteAllText(source, """
			export int main()
			{
				Console.writeLine("installed");
				return 0;
			}
			""");
		string installedCampc = Path.Combine(installBin, Path.GetFileName(sourceCampc));
		ProcessResult result = RunCampcFrom(installedCampc, projectRoot, "build", source, "--artifact", "none", "--out-dir", Path.Combine(projectRoot, "out"));

		Assert.Equal(0, result.ExitCode);
	}

	[Fact]
	public void Test_list_emits_manifest_and_filters_list_output()
	{
		string source = CreateTempCase("test_manifest_cli/main.camp", """
			namespace CliTests;

			/// Adds two values.
			/// @test
			void addReturnsSum(thrown Assertion* assertion)
			{
			}

			@test
			void parseValue(thrown Assertion* assertion)
			{
			}

			@test
			int invalidShape()
			{
				return 0;
			}
			""");
		string outDir = TempPath("test-manifest-cli-out");

		ProcessResult result = RunCampc(
			"test",
			source,
			"--list",
			"--filter",
			"parse^alue",
			"--sourcefile-paths",
			"absolute",
			"--out-dir",
			outDir,
			"--name",
			"test_manifest_cli");

		AssertCommandSucceeded(result);
		Assert.DoesNotContain("generated:", result.StdOut, StringComparison.Ordinal);
		Assert.Contains("CliTests::parseValue", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("CliTests::addReturnsSum", result.StdOut, StringComparison.Ordinal);

		string manifestPath = Path.Combine(outDir, ArtifactDirectoryForHost(null), "test_manifest_cli.camp-test-manifest.json");
		Assert.True(File.Exists(manifestPath), manifestPath);
		using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
		Assert.Equal("camp.test-manifest", manifest.RootElement.GetProperty("format").GetString());
		Assert.Equal("in-module", manifest.RootElement.GetProperty("mode").GetString());
		JsonElement tests = manifest.RootElement.GetProperty("tests");
		Assert.Equal(3, tests.GetArrayLength());
		JsonElement add = tests.EnumerateArray().Single(test => test.GetProperty("name").GetString() == "addReturnsSum");
		Assert.Equal("CliTests::addReturnsSum", add.GetProperty("id").GetString());
		Assert.Equal("Adds two values.", add.GetProperty("summary").GetString());
		Assert.Equal("valid", add.GetProperty("runnerSignature").GetString());
		Assert.Equal(Path.GetFullPath(source).Replace('\\', '/'), add.GetProperty("sourcefile").GetString());
		JsonElement invalid = tests.EnumerateArray().Single(test => test.GetProperty("name").GetString() == "invalidShape");
		Assert.Equal("invalid", invalid.GetProperty("runnerSignature").GetString());
	}

	[Fact]
	public void Test_command_builds_harness_executable_and_replaces_entry_point()
	{
		string source = CreateTempCase("test_harness_entry/main.camp", """
			namespace HarnessCli;

			export int main()
			{
				return 17;
			}

			struct TestFailure
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			TestFailure testFailure;

			void check(bool condition, escaped string message = sourceof(condition), escaped string sourcefile = caller(sourcefile), uint sourceline = caller(sourceline), thrown TestFailure* failure)
			{
				if (!condition)
				{
					testFailure.message = message;
					testFailure.sourcefile = sourcefile;
					testFailure.sourceline = sourceline;
					throw &testFailure;
				}
			}

			@test
			void passing(thrown TestFailure* failure)
			{
				check(1 == 1);
			}
			""");
		string outDir = TempPath("test-harness-entry-out");

		ProcessResult result = RunCampc(
			"test",
			source,
			"--nostdlib",
			"--target",
			NativeTargetForHost(),
			"--out-dir",
			outDir,
			"--name",
			"harness_entry");

		AssertCommandSucceeded(result);
		string artifactDirectory = Path.Combine(outDir, ArtifactDirectoryForHost(null));
		Assert.True(File.Exists(Path.Combine(artifactDirectory, "build", "harness_entry_test_harness.c")));
		string generatedSource = File.ReadAllText(Path.Combine(artifactDirectory, "build", "main.c"));
		Assert.Contains("campmain(void)", generatedSource, StringComparison.Ordinal);

		ProcessResult run = RunExecutable(Path.Combine(artifactDirectory, "harness_entry" + ExecutableExtensionForHost()));
		Assert.Equal(0, run.ExitCode);
		Assert.Contains("passed: HarnessCli::passing", run.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("failed:", run.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Generated_harness_reports_failures_skips_invalid_tests_and_source_capture()
	{
		string source = CreateTempCase("test_harness_outcomes/main.camp", """
			namespace HarnessCli;

			@test
			void directFailure(thrown Assertion* assertion)
			{
				assert(1 == 2);
			}

			@testonly
			void assertPositive(int value, escaped string message = sourceof(value), escaped string sourcefile = caller(sourcefile), uint sourceline = caller(sourceline), thrown Assertion* assertion)
			{
				assert(value > 0, message, sourcefile, sourceline);
			}

			@test
			void wrapperFailure(thrown Assertion* assertion)
			{
				assertPositive(0);
			}

			@skip("not yet")
			@test
			void skippedCase(thrown Assertion* assertion)
			{
				fail("should not run");
			}

			@test
			int invalidShape()
			{
				return 0;
			}
			""");
		string outDir = TempPath("test-harness-outcomes-out");

		ProcessResult result = RunCampc(
			"test",
			source,
			"--target",
			NativeTargetForHost(),
			"--out-dir",
			outDir,
			"--name",
			"harness_outcomes");

		Assert.Equal(1, result.ExitCode);
		Assert.Contains("failed: HarnessCli::directFailure", result.StdOut, StringComparison.Ordinal);
		Assert.Contains($"{RelativeSourcePath(source)}:{FindLine(source, "assert(1 == 2);")} 1 == 2", result.StdOut, StringComparison.Ordinal);
		Assert.Contains("failed: HarnessCli::wrapperFailure", result.StdOut, StringComparison.Ordinal);
		Assert.Contains($"{RelativeSourcePath(source)}:{FindLine(source, "assertPositive(0);")} 0", result.StdOut, StringComparison.Ordinal);
		Assert.Contains("skipped: HarnessCli::skippedCase", result.StdOut, StringComparison.Ordinal);
		Assert.Contains("invalid: HarnessCli::invalidShape", result.StdOut, StringComparison.Ordinal);
		Assert.Contains("test summary: 0 passed, 2 failed, 1 skipped, 1 invalid, 0 error, 4 total", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("should not run", result.StdOut, StringComparison.Ordinal);

		string resultsPath = TestResultsPath(outDir, "harness_outcomes");
		using JsonDocument results = JsonDocument.Parse(File.ReadAllText(resultsPath));
		Assert.Equal("camp.test-results", results.RootElement.GetProperty("format").GetString());
		JsonElement summary = results.RootElement.GetProperty("summary");
		Assert.Equal(2, summary.GetProperty("failed").GetInt32());
		Assert.Equal(1, summary.GetProperty("skipped").GetInt32());
		Assert.Equal(1, summary.GetProperty("invalid").GetInt32());
		JsonElement tests = results.RootElement.GetProperty("tests");
		JsonElement directFailure = tests.EnumerateArray().Single(test => test.GetProperty("id").GetString() == "HarnessCli::directFailure");
		Assert.Equal("failed", directFailure.GetProperty("outcome").GetString());
		Assert.Equal("assertion", directFailure.GetProperty("failure").GetProperty("kind").GetString());
		Assert.Equal("1 == 2", directFailure.GetProperty("failure").GetProperty("message").GetString());
		JsonElement invalid = tests.EnumerateArray().Single(test => test.GetProperty("id").GetString() == "HarnessCli::invalidShape");
		Assert.Equal("invalid", invalid.GetProperty("outcome").GetString());
		Assert.Equal("invalid-test-signature", invalid.GetProperty("failure").GetProperty("kind").GetString());
	}

	[Fact]
	public void Test_command_applies_filters_and_result_format_options()
	{
		string source = CreateTempCase("test_runner_filters/main.camp", """
			namespace FilterCli;

			@test
			void alphaPass(thrown Assertion* assertion)
			{
				assert(1 == 1);
			}

			@test
			void betaPass(thrown Assertion* assertion)
			{
				assert(2 == 2);
			}

			@skip("later")
			@test
			void skippedCase(thrown Assertion* assertion)
			{
				fail("should not run");
			}
			""");
		string jsonOutDir = TempPath("test-runner-filter-json-out");
		string jsonResultDir = TempPath("test-runner-filter-json-results");

		ProcessResult jsonOnly = RunCampc(
			"test",
			source,
			"--target",
			NativeTargetForHost(),
			"--filter",
			"FilterCli::alphaPass",
			"--test-output-dir",
			jsonResultDir,
			"--test-result-format",
			"json",
			"--out-dir",
			jsonOutDir,
			"--name",
			"filter_json");

		AssertCommandSucceeded(jsonOnly);
		Assert.DoesNotContain("generated:", jsonOnly.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("passed: FilterCli::alphaPass", jsonOnly.StdOut, StringComparison.Ordinal);
		string jsonResultsPath = Path.Combine(jsonResultDir, "filter_json.camp-test-results.json");
		Assert.True(File.Exists(jsonResultsPath), jsonResultsPath);
		using (JsonDocument jsonResults = JsonDocument.Parse(File.ReadAllText(jsonResultsPath)))
		{
			JsonElement tests = jsonResults.RootElement.GetProperty("tests");
			JsonElement selected = Assert.Single(tests.EnumerateArray());
			Assert.Equal("FilterCli::alphaPass", selected.GetProperty("id").GetString());
			Assert.Equal("passed", selected.GetProperty("outcome").GetString());
		}

		ProcessResult skippedOnly = RunCampc(
			"test",
			source,
			"--target",
			NativeTargetForHost(),
			"--filter",
			"FilterCli::skipped*",
			"--test-result-format",
			"text,json",
			"--out-dir",
			TempPath("test-runner-filter-skipped-out"),
			"--name",
			"filter_skipped");

		AssertCommandSucceeded(skippedOnly);
		Assert.Contains("skipped: FilterCli::skippedCase", skippedOnly.StdOut, StringComparison.Ordinal);
		Assert.Contains("test summary: 0 passed, 0 failed, 1 skipped, 0 invalid, 0 error, 1 total", skippedOnly.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("should not run", skippedOnly.StdOut, StringComparison.Ordinal);

		string textOnlyOut = TempPath("test-runner-filter-text-out");
		ProcessResult textOnly = RunCampc(
			"test",
			source,
			"--target",
			NativeTargetForHost(),
			"--filter",
			"FilterCli::betaPass",
			"--test-result-format",
			"text",
			"--out-dir",
			textOnlyOut,
			"--name",
			"filter_text");

		AssertCommandSucceeded(textOnly);
		Assert.Contains("passed: FilterCli::betaPass", textOnly.StdOut, StringComparison.Ordinal);
		Assert.False(File.Exists(TestResultsPath(textOnlyOut, "filter_text")));
	}

	[Fact]
	public void External_test_module_runs_against_shared_library_api_only()
	{
		string root = TempPath("external-test-module");
		string libraryRoot = Path.Combine(root, "library");
		string librarySource = Path.Combine(libraryRoot, "src");
		string testRoot = Path.Combine(root, "tests");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(testRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			namespace ExternalLib;

			export int add(int left, int right)
			{
				return left + right;
			}

			internal int hiddenValue()
			{
				return 99;
			}

			@testonly
			int testOnlyValue()
			{
				return 100;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "library.campbuild"), """
			--nostdlib
			--name external-lib
			src/*.camp
			""");
		string good = Path.Combine(testRoot, "good.camp");
		File.WriteAllText(good, """
			namespace ExternalTests;
			using ExternalLib;

			@test
			void exportedApiWorks(thrown Assertion* assertion)
			{
				assert(add(2, 3) == 5);
			}
			""");
		string bad = Path.Combine(testRoot, "bad.camp");
		File.WriteAllText(bad, """
			namespace ExternalTests;
			using ExternalLib;

			@test
			void hiddenApiIsUnavailable(thrown Assertion* assertion)
			{
				assert(hiddenValue() == 99);
			}
			""");
		string target = NativeTargetForHost();
		string goodOut = Path.Combine(testRoot, "good-bin");

		ProcessResult goodResult = RunCampc(
			"test",
			good,
			"--target",
			target,
			"--project-reference",
			libraryRoot + ":shared",
			"--out-dir",
			goodOut,
			"--name",
			"external_tests");

		AssertCommandSucceeded(goodResult);
		Assert.Contains("passed: ExternalTests::exportedApiWorks", goodResult.StdOut, StringComparison.Ordinal);
		using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(TestManifestPath(goodOut, "external_tests"))))
			Assert.Equal("external", manifest.RootElement.GetProperty("mode").GetString());
		using (JsonDocument results = JsonDocument.Parse(File.ReadAllText(TestResultsPath(goodOut, "external_tests"))))
		{
			JsonElement selected = Assert.Single(results.RootElement.GetProperty("tests").EnumerateArray());
			Assert.Equal("ExternalTests::exportedApiWorks", selected.GetProperty("id").GetString());
			Assert.Equal("passed", selected.GetProperty("outcome").GetString());
		}

		ProcessResult badResult = RunCampc(
			"test",
			bad,
			"--target",
			target,
			"--project-reference",
			libraryRoot + ":shared",
			"--out-dir",
			Path.Combine(testRoot, "bad-bin"),
			"--name",
			"external_bad");

		Assert.NotEqual(0, badResult.ExitCode);
		Assert.Contains("Symbol 'hiddenValue' could not be found.", badResult.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Cover_command_writes_json_lcov_and_excludes_tests_from_denominator()
	{
		string source = CreateTempCase("coverage_in_module/main.camp", """
			namespace CoverageCli;

			int add(int left, int right)
			{
				int sum = left + right;
				return sum;
			}

			int unused()
			{
				return 0;
			}

			@testonly
			int testHelper()
			{
				return 99;
			}

			@test
			void addWorks(thrown Assertion* assertion)
			{
				assert(add(2, 3) == 5);
			}
			""");
		string outDir = TempPath("coverage-in-module-out");
		string coverageDir = TempPath("coverage-in-module-results");

		ProcessResult result = RunCampc(
			"cover",
			source,
			"--target",
			NativeTargetForHost(),
			"--coverage-format",
			"json,lcov",
			"--coverage-output-dir",
			coverageDir,
			"--out-dir",
			outDir,
			"--name",
			"coverage_basic");

		AssertCommandSucceeded(result);
		Assert.Contains("passed: CoverageCli::addWorks", result.StdOut, StringComparison.Ordinal);
		Assert.Contains("coverage summary: 2/3 lines covered (66.7%), 1/2 functions covered (50.0%)", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("generated:", result.StdOut, StringComparison.Ordinal);

		string mapPath = Path.Combine(coverageDir, "coverage_basic.camp-coverage-map.csv");
		string map = File.ReadAllText(mapPath);
		Assert.StartsWith("v,1\n", map, StringComparison.Ordinal);
		Assert.Contains("CoverageCli::add", map, StringComparison.Ordinal);
		Assert.Contains("CoverageCli::unused", map, StringComparison.Ordinal);
		Assert.DoesNotContain("CoverageCli::addWorks", map, StringComparison.Ordinal);
		Assert.DoesNotContain("testHelper", map, StringComparison.Ordinal);
		Assert.DoesNotContain("$camp_test_support", map, StringComparison.Ordinal);

		string resultsPath = Path.Combine(coverageDir, "coverage_basic.camp-coverage-results.json");
		using JsonDocument coverage = JsonDocument.Parse(File.ReadAllText(resultsPath));
		Assert.Equal("camp.coverage-results", coverage.RootElement.GetProperty("format").GetString());
		Assert.Equal(2, coverage.RootElement.GetProperty("summary").GetProperty("function").GetProperty("total").GetInt32());
		Assert.Equal(1, coverage.RootElement.GetProperty("summary").GetProperty("function").GetProperty("covered").GetInt32());
		Assert.Equal(3, coverage.RootElement.GetProperty("summary").GetProperty("line").GetProperty("total").GetInt32());
		Assert.Equal(2, coverage.RootElement.GetProperty("summary").GetProperty("line").GetProperty("covered").GetInt32());
		JsonElement file = Assert.Single(coverage.RootElement.GetProperty("files").EnumerateArray());
		Assert.Contains(FindLine(source, "return 0;"), file.GetProperty("uncoveredLines").EnumerateArray().Select(static line => line.GetInt32()));

		string lcov = File.ReadAllText(Path.Combine(coverageDir, "lcov.info"));
		Assert.Contains("SF:" + RelativeSourcePath(source), lcov, StringComparison.Ordinal);
		Assert.Contains("FNDA:1,CoverageCli::add", lcov, StringComparison.Ordinal);
		Assert.Contains("FNDA:0,CoverageCli::unused", lcov, StringComparison.Ordinal);
	}

	[Fact]
	public void Cover_command_maps_lowered_generator_and_lambda_body_lines()
	{
		string source = CreateTempCase("coverage_lowered_bodies/main.camp", """
			namespace CoverageLowered;

			class Counter
			{
				int value;

				Counter(int seed)
				{
					this.value = seed;
					this.value = this.value + 1;
				}

				int getValue()
				{
					return this.value;
				}
			}

			struct iter int countTo(int last)
			{
				int current = 0;
				while (current < last)
				{
					current++;
					yield current;
				}
				yield break;
			}

			int sumGenerated()
			{
				int sum = 0;
				foreach (int value in countTo(3))
					sum = sum + value;
				return sum;
			}

			int apply(delegate int(int) mapper, int value)
			{
				return mapper(value);
			}

			int lambdaValue()
			{
				int offset = 2;
				return apply((int value) =>
				{
					int doubled = value * 2;
					return doubled + offset;
				}, 4);
			}

			int constructedValue()
			{
				auto counter = new Counter(4) finally delete;
				return counter.getValue();
			}

			@test
			void loweredCoverageWorks(thrown Assertion* assertion)
			{
				assert(sumGenerated() == 6);
				assert(lambdaValue() == 10);
				assert(constructedValue() == 5);
			}
			""");
		string outDir = TempPath("coverage-lowered-bodies-out");
		string coverageDir = TempPath("coverage-lowered-bodies-results");

		ProcessResult result = RunCampc(
			"cover",
			source,
			"--target",
			NativeTargetForHost(),
			"--coverage-format",
			"json",
			"--coverage-output-dir",
			coverageDir,
			"--out-dir",
			outDir,
			"--name",
			"coverage_lowered");

		AssertCommandSucceeded(result);

		string map = File.ReadAllText(Path.Combine(coverageDir, "coverage_lowered.camp-coverage-map.csv"));
		int incrementLine = FindLine(source, "current++;");
		int yieldLine = FindLine(source, "yield current;");
		int yieldBreakLine = FindLine(source, "yield break;");
		int lambdaLocalLine = FindLine(source, "int doubled = value * 2;");
		int lambdaReturnLine = FindLine(source, "return doubled + offset;");
		int constructorAssignLine = FindLine(source, "this.value = seed;");
		int constructorIncrementLine = FindLine(source, "this.value = this.value + 1;");
		AssertCoverageMapContainsLine(map, incrementLine);
		AssertCoverageMapContainsLine(map, yieldLine);
		AssertCoverageMapContainsLine(map, yieldBreakLine);
		AssertCoverageMapContainsLine(map, lambdaLocalLine);
		AssertCoverageMapContainsLine(map, lambdaReturnLine);
		AssertCoverageMapContainsLine(map, constructorAssignLine);
		AssertCoverageMapContainsLine(map, constructorIncrementLine);

		using JsonDocument coverage = JsonDocument.Parse(File.ReadAllText(Path.Combine(coverageDir, "coverage_lowered.camp-coverage-results.json")));
		JsonElement file = Assert.Single(coverage.RootElement.GetProperty("files").EnumerateArray());
		HashSet<int> uncoveredLines = file.GetProperty("uncoveredLines").EnumerateArray().Select(static line => line.GetInt32()).ToHashSet();
		Assert.DoesNotContain(incrementLine, uncoveredLines);
		Assert.DoesNotContain(yieldLine, uncoveredLines);
		Assert.DoesNotContain(yieldBreakLine, uncoveredLines);
		Assert.DoesNotContain(lambdaLocalLine, uncoveredLines);
		Assert.DoesNotContain(lambdaReturnLine, uncoveredLines);
		Assert.DoesNotContain(constructorAssignLine, uncoveredLines);
		Assert.DoesNotContain(constructorIncrementLine, uncoveredLines);
	}

	[Fact]
	public void External_cover_instruments_selected_shared_library_subject()
	{
		string root = TempPath("coverage-external-module");
		string libraryRoot = Path.Combine(root, "library");
		string librarySource = Path.Combine(libraryRoot, "src");
		string otherRoot = Path.Combine(root, "other");
		string otherSource = Path.Combine(otherRoot, "src");
		string testRoot = Path.Combine(root, "tests");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(otherSource);
		Directory.CreateDirectory(testRoot);
		string libraryCamp = Path.Combine(librarySource, "library.camp");
		File.WriteAllText(libraryCamp, """
			namespace CoverLib;

			export int add(int left, int right)
			{
				int sum = left + right;
				return sum;
			}

			export int unused()
			{
				return 0;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "library.campbuild"), """
			--nostdlib
			--name cover-lib
			src/*.camp
			""");
		File.WriteAllText(Path.Combine(otherSource, "other.camp"), """
			namespace OtherLib;

			export int value()
			{
				return 1;
			}
			""");
		File.WriteAllText(Path.Combine(otherRoot, "other.campbuild"), """
			--nostdlib
			--name other-lib
			src/*.camp
			""");
		string tests = Path.Combine(testRoot, "tests.camp");
		File.WriteAllText(tests, """
			namespace CoverTests;
			using CoverLib;

			@test
			void exportedApiWorks(thrown Assertion* assertion)
			{
				assert(add(2, 3) == 5);
			}
			""");
		string target = NativeTargetForHost();

		ProcessResult ambiguous = RunCampc(
			"cover",
			tests,
			"--target",
			target,
			"--project-reference",
			libraryRoot + ":shared",
			"--project-reference",
			otherRoot + ":shared",
			"--out-dir",
			Path.Combine(testRoot, "ambiguous-bin"),
			"--name",
			"external_ambiguous");

		Assert.NotEqual(0, ambiguous.ExitCode);
		Assert.Contains("requires --coverage-subject", ambiguous.StdErr, StringComparison.Ordinal);

		string outDir = Path.Combine(testRoot, "bin");
		ProcessResult result = RunCampc(
			"cover",
			tests,
			"--target",
			target,
			"--project-reference",
			libraryRoot + ":shared",
			"--project-reference",
			otherRoot + ":shared",
			"--coverage-subject",
			"cover-lib",
			"--coverage-format",
			"json",
			"--out-dir",
			outDir,
			"--name",
			"external_cover");

		AssertCommandSucceeded(result);
		Assert.Contains("passed: CoverTests::exportedApiWorks", result.StdOut, StringComparison.Ordinal);
		string rootGenerated = File.ReadAllText(Path.Combine(outDir, ArtifactDirectoryForHost(null), "build", "tests.c"));
		Assert.DoesNotContain("__camp_coverage", rootGenerated, StringComparison.Ordinal);
		string dependencyMap = Path.Combine(libraryRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Shared) + "_coverage", "cover-lib.camp-coverage-map.csv");
		Assert.True(File.Exists(dependencyMap), dependencyMap);
		string map = File.ReadAllText(dependencyMap);
		Assert.Contains("CoverLib::add", map, StringComparison.Ordinal);
		Assert.Contains("CoverLib::unused", map, StringComparison.Ordinal);

		using JsonDocument coverage = JsonDocument.Parse(File.ReadAllText(CoverageResultsPath(outDir, "external_cover")));
		Assert.Equal(2, coverage.RootElement.GetProperty("summary").GetProperty("function").GetProperty("total").GetInt32());
		Assert.Equal(1, coverage.RootElement.GetProperty("summary").GetProperty("function").GetProperty("covered").GetInt32());
		JsonElement file = Assert.Single(coverage.RootElement.GetProperty("files").EnumerateArray());
		Assert.Contains(RelativeSourcePath(libraryCamp), file.GetProperty("path").GetString(), StringComparison.Ordinal);
		Assert.Contains(FindLine(libraryCamp, "return 0;"), file.GetProperty("uncoveredLines").EnumerateArray().Select(static line => line.GetInt32()));
	}

	[Fact]
	public void Coverage_options_are_cover_only_and_validate_format()
	{
		string source = CreateTempCase("coverage_cli_validation/main.camp", """
			export void main()
			{
			}
			""");

		ProcessResult build = RunCampc("build", source, "--coverage-format", "json");
		ProcessResult invalid = RunCampc("cover", source, "--nostdlib", "--coverage-format", "lcov,json");

		Assert.NotEqual(0, build.ExitCode);
		Assert.Contains("can only be used with cover", build.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, invalid.ExitCode);
		Assert.Contains("--coverage-format expects json, lcov, or json,lcov", invalid.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_and_run_accept_test_result_options()
	{
		string source = CreateTempCase("test_result_options/main.camp", """
			export int main()
			{
				return 0;
			}
			""");
		string resultDir = TempPath("ignored-test-results");

		ProcessResult build = RunCampc(
			"build",
			source,
			"--nostdlib",
			"--artifact",
			"none",
			"--test-output-dir",
			resultDir,
			"--test-result-format",
			"text,json",
			"--out-dir",
			TempPath("test-result-options-build"));
		ProcessResult run = RunCampc(
			"run",
			source,
			"--nostdlib",
			"--test-output-dir",
			resultDir,
			"--test-result-format",
			"json",
			"--out-dir",
			TempPath("test-result-options-run"));

		AssertCommandSucceeded(build);
		AssertCommandSucceeded(run);
	}

	[Fact]
	public void Filter_option_is_rejected_outside_test_commands()
	{
		string source = CreateTempCase("test_filter_rejected/main.camp", """
			export void main()
			{
			}
			""");

		ProcessResult result = RunCampc("build", source, "--filter", "*");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("--filter", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_debug_info_emits_line_directives_and_debug_map()
	{
		string source = CreateTempCase("debug_info/main.camp", """
			export int helper(int value)
			{
				int local = value + 1;
				return local;
			}

			export int main()
			{
				return helper(41);
			}
			""");
		string outDir = TempPath("debug-info-out");

		ProcessResult result = RunCampc(
			"build",
			source,
			"--nostdlib",
			"--artifact",
			"none",
			"--debug-info",
			"--name",
			"debug_info",
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		string debugMap = Assert.Single(Directory.GetFiles(outDir, "*.campdebug.json", SearchOption.AllDirectories));
		string debugJson = File.ReadAllText(debugMap);
		Assert.Contains("\"format\": \"camp.debug\"", debugJson, StringComparison.Ordinal);
		Assert.Contains("\"campFunction\": \"helper\"", debugJson, StringComparison.Ordinal);
		Assert.Contains("\"campName\": \"value\"", debugJson, StringComparison.Ordinal);
		Assert.Contains("\"campName\": \"local\"", debugJson, StringComparison.Ordinal);

		string cFile = Assert.Single(Directory.GetFiles(outDir, "main.c", SearchOption.AllDirectories));
		string cText = File.ReadAllText(cFile);
		Assert.Contains("#line 1", cText, StringComparison.Ordinal);
		Assert.Contains(EscapeCString(Path.GetFullPath(source)), cText, StringComparison.Ordinal);
	}

	[Fact]
	public void Dump_lowering_prints_to_stdout()
	{
		ProcessResult result = RunCampc("dump", "lowering", "tests/Lowering/default_arguments.camp", "--nostdlib");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("addDefault", result.StdOut, StringComparison.Ordinal);
		Assert.Equal("", result.StdErr);
	}

	[Fact]
	public void Using_imports_support_qualified_types_functions_aliases_and_static_members()
	{
		string root = TempPath("using-qualified-positive");
		Directory.CreateDirectory(root);
		string library = Path.Combine(root, "library.camp");
		File.WriteAllText(library, """
			namespace Lib;

			export struct Point
			{
				int x;
				int y;
			}

			export alias PointAlias = Point;

			export int getValue() => 42;

			export static int Point.getDefault() => 7;
			""");
		string app = Path.Combine(root, "app.camp");
		File.WriteAllText(app, """
			namespace App;
			using Lib;
			using Lib as L;

			struct Holder<T: any>
			{
				T* value;
			}

			export Lib::Point makePoint()
			{
				Lib::Point p = default;
				return p;
			}

			export int readAlias(Lib::PointAlias p)
			{
				return p.y;
			}

			export int main()
			{
				Lib::Point p = default;
				L::Point q = default;
				Lib::Point* ptr = (Lib::Point*)null;
				Holder<Lib::Point> holder = default;
				const char[] name = typenameof(Lib::Point);
				return Lib::getValue() + L::getValue() + Lib::Point.getDefault() + q.x + p.y;
			}
			""");

		ProcessResult result = BuildInProcess("using-qualified-positive-out", noStdLib: true, library, app);

		AssertCommandSucceeded(result);
	}

	[Fact]
	public void Using_imports_hide_unimported_unselected_and_unaliased_symbols()
	{
		string root = TempPath("using-import-negative");
		Directory.CreateDirectory(root);
		string library = Path.Combine(root, "library.camp");
		File.WriteAllText(library, """
			namespace Lib;

			export struct Point
			{
				int x;
			}

			export int getValue() => 42;
			""");
		string noImport = Path.Combine(root, "no_import.camp");
		File.WriteAllText(noImport, """
			namespace App;
			export int main() => getValue();
			""");
		string selected = Path.Combine(root, "selected.camp");
		File.WriteAllText(selected, """
			namespace App;
			using Lib { Point };
			export int main() => getValue();
			""");
		string aliasOriginal = Path.Combine(root, "alias_original.camp");
		File.WriteAllText(aliasOriginal, """
			namespace App;
			using Lib as L;
			export int main() => Lib::getValue();
			""");

		ProcessResult noImportResult = BuildInProcess("using-no-import-out", noStdLib: true, library, noImport);
		ProcessResult selectedResult = BuildInProcess("using-selected-out", noStdLib: true, library, selected);
		ProcessResult aliasOriginalResult = BuildInProcess("using-alias-original-out", noStdLib: true, library, aliasOriginal);

		Assert.NotEqual(0, noImportResult.ExitCode);
		Assert.Contains("Symbol 'getValue' is declared in namespace 'Lib' but is not imported by this file.", noImportResult.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, selectedResult.ExitCode);
		Assert.Contains("Symbol 'getValue' is declared in namespace 'Lib' but is not imported by this file.", selectedResult.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, aliasOriginalResult.ExitCode);
		Assert.Contains("Symbol 'Lib::getValue' could not be found.", aliasOriginalResult.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Explicit_root_std_using_suppresses_implicit_std_import()
	{
		string implicitStd = CreateTempCase("using_implicit_std.camp", """
			export int main()
			{
				Console.writeLine("ok");
				return 0;
			}
			""");
		string aliasedStd = CreateTempCase("using_aliased_std.camp", """
			using Std as S;

			export int main()
			{
				S::Console.writeLine("ok");
				return 0;
			}
			""");
		string suppressedStd = CreateTempCase("using_suppressed_std.camp", """
			using Std as S;

			export int main()
			{
				Console.writeLine("missing");
				return 0;
			}
			""");
		string selectedStd = CreateTempCase("using_selected_std.camp", """
			using Std { Console };

			export int main()
			{
				List<int>* list = null;
				Console.writeLine("ok");
				return 0;
			}
			""");

		ProcessResult implicitResult = BuildInProcess("using-implicit-std-out", noStdLib: false, implicitStd);
		ProcessResult aliasedResult = BuildInProcess("using-aliased-std-out", noStdLib: false, aliasedStd);
		ProcessResult suppressedResult = BuildInProcess("using-suppressed-std-out", noStdLib: false, suppressedStd);
		ProcessResult selectedResult = BuildInProcess("using-selected-std-out", noStdLib: false, selectedStd);

		AssertCommandSucceeded(implicitResult);
		AssertCommandSucceeded(aliasedResult);
		Assert.NotEqual(0, suppressedResult.ExitCode);
		Assert.Contains("Static class 'Console' is declared in another file but is not exported.", suppressedResult.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, selectedResult.ExitCode);
		Assert.Contains("Type 'List' is declared in namespace 'Std' but is not imported by this file.", selectedResult.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Namespace_replaces_export_as_and_is_reserved()
	{
		string oldNamespace = CreateTempCase("old_namespace.camp", """
			export as OldName;

			export int main() => 0;
			""");
		string reserved = CreateTempCase("reserved_namespace.camp", """
			export int namespace() => 0;
			""");

		ProcessResult oldResult = BuildInProcess("old-namespace-out", noStdLib: true, oldNamespace);
		ProcessResult reservedResult = BuildInProcess("reserved-namespace-out", noStdLib: true, reserved);

		Assert.NotEqual(0, oldResult.ExitCode);
		Assert.Contains("Use 'namespace OldName;' instead of 'export as OldName;'.", oldResult.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, reservedResult.ExitCode);
		Assert.Contains("Function name 'namespace' is reserved.", reservedResult.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Public_visibility_spelling_is_artifact_visibility()
	{
		string source = CreateTempCase("public_visibility.camp", """
			public int helper() => 1;

			export int main()
			{
				return helper() - 1;
			}
			""");

		ProcessResult result = BuildInProcess("public-visibility-out", noStdLib: true, source);

		AssertCommandSucceeded(result);
	}

	[Fact]
	public void Enum_value_shorthand_works_in_comparisons_on_either_side()
	{
		string source = CreateTempCase("enum_comparison_shorthand.camp", """
			extern void* malloc(nuint size);
			extern void free(void* pointer);

			bool isSolid(PenStyle style = SOLID)
			{
				return style == SOLID && SOLID == style;
			}

			enum PenStyle
			{
				SOLID,
				DASH
			}

			export int main()
			{
				return isSolid() ? 0 : 1;
			}
			""");

		ProcessResult result = BuildInProcess("enum-comparison-shorthand", noStdLib: true, source);

		AssertCommandSucceeded(result);
	}

	[Fact]
	public void Invalid_finally_delete_reports_source_range()
	{
		string source = CreateTempCase("finally_delete_range.camp", """
			extern void* malloc(nuint size);
			extern void free(void* pointer);

			newtype HBRUSH: nint;

			HBRUSH createBrush() => (HBRUSH)1;

			export int main()
			{
				auto brush = createBrush() finally delete;
				return 0;
			}
			""");

		ProcessResult result = BuildInProcess("finally-delete-range", noStdLib: true, source);

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("finally_delete_range.camp(10,15): error: delete requires a pointer or a type with a destructor, not 'HBRUSH'.", Normalize(result.StdErr), StringComparison.Ordinal);
		Assert.DoesNotContain("(no line,column)", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Invalid_foreach_iterator_cleanup_delete_reports_source_range()
	{
		string source = CreateTempCase("foreach_iterator_delete_range.camp", """
			public fixed struct BrokenIter
			{
				public extern bool next(int* current);
				public extern static bool op_iter(void* ctx, int* current);
			}

			public extern BrokenIter broken();

			export int main()
			{
				foreach (auto value in broken())
				{
				}
				return 0;
			}
			""");

		ProcessResult result = BuildInProcess("foreach-iterator-delete-range", noStdLib: true, source);

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("foreach_iterator_delete_range.camp(11,2): error: delete requires a pointer or a type with a destructor, not 'BrokenIter'.", Normalize(result.StdErr), StringComparison.Ordinal);
		Assert.DoesNotContain("(no line,column)", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Async_exported_c_header_uses_completion_callback_abi()
	{
		string source = CreateTempCase("async_header.camp", """
			export extern async int loadAsync(thrown int error);
			""");
		string buildDir = TempPath("async-header-build");

		ProcessResult result = BuildInProcess("async-header-build", noStdLib: true, source);

		Assert.Equal(0, result.ExitCode);
		string artifactDirectory = Path.Combine(buildDir, ArtifactDirectoryForHost(null));
		string publicHeader = File.ReadAllText(Path.Combine(artifactDirectory, "build", "async_header.h"));
		string privateHeader = File.ReadAllText(Path.Combine(artifactDirectory, "build", "async_header_private.h"));
		Assert.Contains("void loadAsync(void (* complete)(void *context, ", publicHeader, StringComparison.Ordinal);
		Assert.Contains(" result, ", publicHeader, StringComparison.Ordinal);
		Assert.Contains(" error), void *complete_context);", publicHeader, StringComparison.Ordinal);
		Assert.Contains("void loadAsync(void (* complete)(void *context, ", privateHeader, StringComparison.Ordinal);
		Assert.Contains(" result, ", privateHeader, StringComparison.Ordinal);
		Assert.Contains(" error), void *complete_context);", privateHeader, StringComparison.Ordinal);
		Assert.DoesNotContain("int loadAsync(int *error)", publicHeader, StringComparison.Ordinal);
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

		ProcessResult result = RunCampc("build", temp, "--verbose", "--out-dir", TempPath("pragma-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: pragma_none.c", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("_api.camp", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Within_allocation_policy_uses_defaults_flags_and_file_override()
	{
		string source = CreateTempCase("within_policy.camp", """
			export extern void* malloc(nuint size);
			export extern void free(void* ptr);

			export int main()
			{
				auto bytes = new byte[1];
				delete bytes;
				return 0;
			}
			""");
		string buildPragmaSource = CreateTempCase("within_policy_build_pragma.camp", """
			#build --explicit-within

			export extern void* malloc(nuint size);
			export extern void free(void* ptr);

			export int main()
			{
				auto bytes = new byte[1];
				delete bytes;
				return 0;
			}
			""");
		string fileImplicitSource = CreateTempCase("within_policy_file_implicit.camp", """
			#within implicit

			export extern void* malloc(nuint size);
			export extern void free(void* ptr);

			export int main()
			{
				auto bytes = new byte[1];
				delete bytes;
				return 0;
			}
			""");
		string fileExplicitSource = CreateTempCase("within_policy_file_explicit.camp", """
			#within explicit

			export extern void* malloc(nuint size);
			export extern void free(void* ptr);

			export int main()
			{
				auto bytes = new byte[1];
				delete bytes;
				return 0;
			}
			""");

		ProcessResult artifactNone = BuildInProcess("within-policy-none", noStdLib: true, source);
		ProcessResult explicitNone = BuildInProcess("within-policy-explicit-none", noStdLib: true, request => request.WithinAllocationPolicy = WithinAllocationPolicy.Explicit, source);
		ProcessResult staticDefault = BuildInProcess("within-policy-static", noStdLib: true, request => request.BuildKind = NativeBuildKind.Static, source);
		ProcessResult buildPragma = RunCampc("build", buildPragmaSource, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("within-policy-build-pragma"));
		ProcessResult fileImplicit = BuildInProcess("within-policy-file-implicit", noStdLib: true, request => request.WithinAllocationPolicy = WithinAllocationPolicy.Explicit, fileImplicitSource);
		ProcessResult fileExplicit = BuildInProcess("within-policy-file-explicit", noStdLib: true, request => request.WithinAllocationPolicy = WithinAllocationPolicy.Implicit, fileExplicitSource);

		AssertCommandSucceeded(artifactNone);
		Assert.NotEqual(0, explicitNone.ExitCode);
		Assert.Contains("new requires an explicit within context", explicitNone.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, staticDefault.ExitCode);
		Assert.Contains("new requires an explicit within context", staticDefault.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, buildPragma.ExitCode);
		Assert.Contains("new requires an explicit within context", buildPragma.StdErr, StringComparison.Ordinal);
		AssertCommandSucceeded(fileImplicit);
		Assert.NotEqual(0, fileExplicit.ExitCode);
		Assert.Contains("new requires an explicit within context", fileExplicit.StdErr, StringComparison.Ordinal);
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

		ProcessResult result = RunCampc("build", "@" + buildFile, "--verbose", "--out-dir", TempPath("response-file-build"));

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

		ProcessResult result = RunCampc("build", "@" + Path.Combine(root, "sample"), "--verbose", "--out-dir", TempPath("response-file-extension-build"));

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

		ProcessResult result = RunCampc("build", buildFile, "--verbose", "--out-dir", TempPath("bare-campbuild-file-build"));

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

		ProcessResult result = RunCampc("build", Path.Combine(root, "sample"), "--verbose", "--out-dir", TempPath("bare-campbuild-extensionless-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_file_defaults_output_to_bin_artifact_directory()
	{
		string root = TempPath("campbuild-default-output");
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

		ProcessResult result = RunCampc("build", buildFile, "--target", NativeTargetForHost());

		Assert.Equal(0, result.ExitCode);
		string artifactDirectory = Path.Combine(root, "bin", ArtifactDirectoryForHost(null));
		Assert.True(File.Exists(Path.Combine(artifactDirectory, "build", "main.c")));
		Assert.True(File.Exists(Path.Combine(artifactDirectory, "build", "main.h")));
		Assert.False(File.Exists(Path.Combine(root, "bin", "build", "main.c")));
	}

	[Fact]
	public void Source_file_defaults_output_to_first_source_directory_bin()
	{
		string root = TempPath("source-default-output");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		string source = Path.Combine(sourceDirectory, "main.camp");
		File.WriteAllText(source, """
			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", source, "--nostdlib", "--artifact", "none", "--target", NativeTargetForHost());

		Assert.Equal(0, result.ExitCode);
		string artifactDirectory = Path.Combine(sourceDirectory, "bin", ArtifactDirectoryForHost(null));
		Assert.True(File.Exists(Path.Combine(artifactDirectory, "build", "main.c")));
		Assert.True(File.Exists(Path.Combine(artifactDirectory, "build", "main.h")));
	}

	[Fact]
	public void Out_dir_is_prefix_unless_direct_directory_marker_is_used()
	{
		string source = CreateTempCase("out_dir_prefix/main.camp", """
			export int main()
			{
				return 0;
			}
			""");
		string prefixOut = TempPath("out-dir-prefix");
		string directOut = TempPath("out-dir-direct");

		ProcessResult prefix = RunCampc(
			"build",
			source,
			"--nostdlib",
			"--artifact",
			"none",
			"--target",
			NativeTargetForHost(),
			"--out-dir",
			prefixOut);
		ProcessResult direct = RunCampc(
			"build",
			source,
			"--nostdlib",
			"--artifact",
			"none",
			"--target",
			NativeTargetForHost(),
			"--out-dir",
			Path.Combine(directOut, "."));

		Assert.Equal(0, prefix.ExitCode);
		Assert.Equal(0, direct.ExitCode);
		Assert.True(File.Exists(Path.Combine(prefixOut, ArtifactDirectoryForHost(null), "build", "main.c")));
		Assert.False(File.Exists(Path.Combine(prefixOut, "build", "main.c")));
		Assert.True(File.Exists(Path.Combine(directOut, "build", "main.c")));
		Assert.False(File.Exists(Path.Combine(directOut, ArtifactDirectoryForHost(null), "build", "main.c")));
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

		ProcessResult result = RunCampc("run", buildFile, "--out-dir", TempPath("run-bare-campbuild-file-build"));

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

		ProcessResult result = RunCampc("run", Path.Combine(root, "sample"), "--out-dir", TempPath("run-bare-campbuild-extensionless-build"));

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
			"--verbose",
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			TempPath("project-reference-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: project_reference_app.c", result.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Static), "sample-lib_api.camp")));
	}

	[Fact]
	public void Static_project_reference_exposes_public_but_not_internal_api()
	{
		string root = TempPath("project-reference-public-static");
		string libraryRoot = Path.Combine(root, "library");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			public int publicValue()
			{
				return 20;
			}

			internal int internalValue()
			{
				return 2;
			}

			export int exportedValue()
			{
				return 22;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "library.campbuild"), """
			--nostdlib
			--name visibility-lib
			src/*.camp
			""");
		string goodApp = Path.Combine(appRoot, "good.camp");
		File.WriteAllText(goodApp, """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return publicValue() + exportedValue() - 42;
			}
			""");
		string badApp = Path.Combine(appRoot, "bad.camp");
		File.WriteAllText(badApp, """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return internalValue();
			}
			""");
		string target = NativeTargetForHost();

		ProcessResult good = RunCampc("build", goodApp, "--target", target, "--project-reference", libraryRoot + ":static", "--out-dir", Path.Combine(appRoot, "good-bin"));
		ProcessResult bad = RunCampc("build", badApp, "--target", target, "--project-reference", libraryRoot + ":static", "--out-dir", Path.Combine(appRoot, "bad-bin"));

		AssertCommandSucceeded(good);
		Assert.NotEqual(0, bad.ExitCode);
		Assert.Contains("Symbol 'internalValue' could not be found.", bad.StdErr, StringComparison.Ordinal);
		string api = File.ReadAllText(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "visibility-lib_api.camp"));
		Assert.Contains("public extern int publicValue();", api, StringComparison.Ordinal);
		Assert.DoesNotContain("internalValue", api, StringComparison.Ordinal);
	}

	[Fact]
	public void Shared_project_reference_exposes_export_api_only()
	{
		string root = TempPath("project-reference-public-shared");
		string libraryRoot = Path.Combine(root, "library");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			public int publicValue()
			{
				return 20;
			}

			export int exportedValue()
			{
				return 22;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "library.campbuild"), """
			--nostdlib
			--name visibility-lib
			src/*.camp
			""");
		string goodApp = Path.Combine(appRoot, "good.camp");
		File.WriteAllText(goodApp, """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return exportedValue() - 22;
			}
			""");
		string badApp = Path.Combine(appRoot, "bad.camp");
		File.WriteAllText(badApp, """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return publicValue();
			}
			""");
		string target = NativeTargetForHost();

		ProcessResult good = RunCampc("build", goodApp, "--target", target, "--project-reference", libraryRoot + ":shared", "--out-dir", Path.Combine(appRoot, "good-bin"));
		ProcessResult bad = RunCampc("build", badApp, "--target", target, "--project-reference", libraryRoot + ":shared", "--out-dir", Path.Combine(appRoot, "bad-bin"));

		AssertCommandSucceeded(good);
		Assert.NotEqual(0, bad.ExitCode);
		Assert.Contains("Symbol 'publicValue' could not be found.", bad.StdErr, StringComparison.Ordinal);
		string api = File.ReadAllText(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Shared), "visibility-lib_api.camp"));
		Assert.DoesNotContain("publicValue", api, StringComparison.Ordinal);
		Assert.Contains("export extern int exportedValue();", api, StringComparison.Ordinal);
		string cApi = File.ReadAllText(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Shared), "visibility-lib_api.h"));
		Assert.DoesNotContain("publicValue", cApi, StringComparison.Ordinal);
		Assert.Contains("exportedValue", cApi, StringComparison.Ordinal);
	}

	[Fact]
	public void Project_api_header_orders_forward_typedefs_before_callable_newtypes_and_struct_layouts()
	{
		string root = TempPath("project-api-layout-order");
		string sourceRoot = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceRoot);
		File.WriteAllText(Path.Combine(sourceRoot, "library.camp"), """
			export newtype fn void PaintEvent(PaintEventArgs* e);

			export struct PaintEventArgs
			{
				Rect32 bounds;
			}

			export struct Rect32
			{
				int x;
			}
			""");
		File.WriteAllText(Path.Combine(root, "layout-lib.campbuild"), """
			--nostdlib
			--artifact static
			--name layout-lib
			src/*.camp
			""");

		string target = NativeTargetForHost();
		ProcessResult result = RunCampc("build", Path.Combine(root, "layout-lib.campbuild"), "--target", target);

		Assert.Equal(0, result.ExitCode);
		string apiHeader = File.ReadAllText(Path.Combine(root, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "layout-lib_api.h"))
			.Replace("\r\n", "\n", StringComparison.Ordinal);
		Assert.True(apiHeader.IndexOf("typedef struct PaintEventArgs PaintEventArgs;", StringComparison.Ordinal) < apiHeader.IndexOf("typedef void (* PaintEvent)", StringComparison.Ordinal));
		Assert.True(apiHeader.IndexOf("struct Rect32\n{", StringComparison.Ordinal) < apiHeader.IndexOf("struct PaintEventArgs\n{", StringComparison.Ordinal));
	}

	[Fact]
	public void Project_reference_consumes_exported_shadow_class_api()
	{
		string root = TempPath("project-reference-shadow-api");
		string libraryRoot = Path.Combine(root, "library");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			extern void* malloc(nuint size);
			extern void free(void* ptr);

			export interface IShadowValue
			{
				int read();
			}

			export class NativeShadowHost
			{
				escaped void* shadowData;

				NativeShadowHost()
				{
				}

				@getshadow
				export escaped void* getShadow(const this) => this.shadowData;

				@setshadow
				export void setShadow(escaped void* value) => this.shadowData = value;
			}

			export virtual shadow class BaseShadow: NativeShadowHost, IShadowValue
			{
				int baseValue;

				export BaseShadow(int value)
				{
					this.baseValue = value;
				}

				export int read(): IShadowValue => this.baseValue;

				export int getBase() => this.baseValue;

				export virtual int calculate() => this.baseValue;

				void cleanupBase()
				{
					delete shadow;
				}
			}

			export virtual shadow class DerivedShadow: BaseShadow
			{
				int extraValue;

				export DerivedShadow(int value, int extra)
				{
					base(value);
					this.extraValue = extra;
				}

				export override int calculate() => this.getBase() + this.extraValue;

				void cleanupDerived()
				{
					delete shadow;
				}
			}

			export DerivedShadow* makeDerived(int value, int extra) => within(default) new DerivedShadow(value, extra);
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "library.campbuild"), """
			--nostdlib
			--name shadow-lib
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				auto derived = makeDerived(10, 7);
				BaseShadow* baseView = derived;
				IShadowValue* value = derived;
				return baseView.calculate() + value.read() - 27;
			}
			""");
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc("build", app, "--target", target, "--project-reference", libraryRoot + ":static", "--out-dir", Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(result);
		string api = File.ReadAllText(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "shadow-lib_api.camp"));
		Assert.Contains("export extern shadow class BaseShadow", api, StringComparison.Ordinal);
		Assert.Contains("export extern shadow class DerivedShadow", api, StringComparison.Ordinal);
		Assert.Contains("@getshadow", api, StringComparison.Ordinal);
		Assert.Contains("@setshadow", api, StringComparison.Ordinal);
	}

	[Fact]
	public void Shadow_class_can_allocate_imported_extern_base_with_exported_constructor()
	{
		string root = TempPath("project-reference-shadow-extern-base-constructor");
		string libraryRoot = Path.Combine(root, "library");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			extern void* malloc(nuint size);
			extern void free(void* ptr);

			export class NativeShadowHost
			{
				escaped void* shadowData;

				export NativeShadowHost()
				{
				}

				@getshadow
				export escaped void* getShadow(const this) => this.shadowData;

				@setshadow
				export void setShadow(escaped void* value) => this.shadowData = value;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "library.campbuild"), """
			--nostdlib
			--name shadow-base-lib
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --artifact exec

			extern void* malloc(nuint size);
			extern void free(void* ptr);

			shadow class LocalShadow: NativeShadowHost
			{
				LocalShadow()
				{
				}

				void cleanup()
				{
					delete shadow;
				}
			}

			export int main()
			{
				auto value = new LocalShadow();
				value.cleanup();
				return 0;
			}
			""");
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc("build", app, "--target", target, "--project-reference", libraryRoot + ":static", "--out-dir", Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(result);
	}

	[Fact]
	public void Wasi_target_builds_stdlib_executable_when_available()
	{
		if (!ClangWasiAvailable())
			Assert.Skip("Clang with WASI support is required for the local WASI smoke test.");
		string source = CreateTempCase("wasi-std/main.camp", """
			export int main()
			{
				Console.writeLine("hello wasi");
				return 0;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			source,
			"--target",
			"wasm32-wasi",
			"--artifact",
			"exec",
			"--out-dir",
			TempPath("wasi-std-out"));

		AssertCommandSucceeded(result);
		Assert.True(File.Exists(Path.Combine(TempPath("wasi-std-out"), ArtifactDirectoryForTarget("wasm32-wasi", NativeBuildKind.Exec), "main.wasm")));
	}

	[Fact]
	public void Wasi_target_rejects_calls_to_unsupported_std_apis()
	{
		string source = CreateTempCase("wasi-unsupported/main.camp", """
			export int main()
			{
				sleep(1);
				return 0;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			source,
			"--target",
			"wasm32-wasi",
			"--artifact",
			"none",
			"--out-dir",
			TempPath("wasi-unsupported-out"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Function 'sleep' is not supported by the current target.", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("The current target does not support timers or thread sleeping.", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Emscripten_target_builds_stdlib_executable_when_available()
	{
		if (!EmscriptenAvailable())
			Assert.Skip("Emscripten is required for the local Emscripten smoke test.");
		string source = CreateTempCase("emscripten-std/main.camp", """
			export int main()
			{
				Console.writeLine("hello emscripten");
				return 0;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			source,
			"--target",
			"wasm32-emscripten",
			"--artifact",
			"exec",
			"--out-dir",
			TempPath("emscripten-std-out"));

		AssertCommandSucceeded(result);
		string artifact = Path.Combine(TempPath("emscripten-std-out"), ArtifactDirectoryForTarget("wasm32-emscripten", NativeBuildKind.Exec), "main.js");
		Assert.True(File.Exists(artifact));
		Assert.True(File.Exists(Path.ChangeExtension(artifact, ".wasm")));
	}

	[Fact]
	public void Emscripten_target_rejects_calls_to_unsupported_std_apis()
	{
		string source = CreateTempCase("emscripten-unsupported/main.camp", """
			export int main()
			{
				FileHandle handle = FileHandle.open("missing.txt", FileAccess.READ, FileMode.OPEN_EXISTING, catch _);
				return handle == default ? 0 : 1;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			source,
			"--target",
			"wasm32-emscripten",
			"--artifact",
			"none",
			"--out-dir",
			TempPath("emscripten-unsupported-out"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Function 'open' is not supported by the current target.", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("The current target does not support file handles.", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Project_reference_uses_root_variant_for_artifact_directory()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC variant project-reference smoke requires loaded MSVC tools.");

		string root = TempPath("project-reference-variant");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		Directory.CreateDirectory(librarySource);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			export int width()
			{
				#if UNICODE
				return 2;
				#else
				return 1;
				#endif
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			--variant ansi
			src/*.camp
			""");

		string app = CreateTempCase("project_reference_variant_app.camp", """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return width() - 2;
			}
			""");

		string target = "msvc-windows-" + MsvcEnvironment.TargetArchitecture;
		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--variant",
			"unicode",
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			TempPath("project-reference-variant-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.True(File.Exists(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "sample-lib_api.camp")));
		Assert.False(File.Exists(Path.Combine(libraryRoot, "bin", target + "_ansi_static_DEBUG", "sample-lib_api.camp")));
	}

	[Fact]
	public void Project_reference_uses_variant_cache_instead_of_referenced_project_output_directories()
	{
		string root = TempPath("project-reference-configured-directories");
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
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
			--out-dir bin
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name sample-app

			export int main()
			{
				return add(20, 22) - 42;
			}
			""");
		string target = NativeTargetForHost();
		string staticLibraryName = OperatingSystem.IsWindows() ? "sample-lib.lib" : "libsample-lib.a";
		string referenceOutputDirectory = Path.Combine(libraryRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Static));

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--verbose",
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(result);
		Assert.True(File.Exists(Path.Combine(referenceOutputDirectory, staticLibraryName)));
		Assert.True(File.Exists(Path.Combine(referenceOutputDirectory, "sample-lib_api.camp")));
		Assert.True(File.Exists(Path.Combine(referenceOutputDirectory, "build", "library.c")));
		Assert.False(File.Exists(Path.Combine(libraryRoot, "bin", staticLibraryName)));
		Assert.False(File.Exists(Path.Combine(libraryRoot, "bin", "sample-lib_api.camp")));
		Assert.False(Directory.Exists(Path.Combine(libraryRoot, "build")));
		Assert.False(Directory.Exists(Path.Combine(libraryRoot, "obj")));
		Assert.True(File.Exists(Path.Combine(appRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Exec), "sample-app" + ExecutableExtensionForHost())));
	}

	[Fact]
	public void Project_reference_skips_native_static_library_when_outputs_are_current()
	{
		string root = TempPath("project-reference-static-current");
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
		string packageName = "api-demo-project-reference-current";
		string repositoryRoot = FindRepositoryRoot();
		string cachedPackageRoot = Path.Combine(repositoryRoot, "cache", "pkg", packageName);
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		string packageSource = Path.Combine(root, "package-source", packageName, "src");
		string appRoot = Path.Combine(root, "sample-app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(packageSource);
		Directory.CreateDirectory(appRoot);
		string sourceRootArgument = Path.Combine(root, "package-source").Replace('\\', '/');
		if (Directory.Exists(cachedPackageRoot))
			Directory.Delete(cachedPackageRoot, recursive: true);
		File.WriteAllText(Path.Combine(packageSource, "api.camp"), "export newtype NativeHandle: nint;\n");
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			export int add(int left, int right)
			{
				return left + right;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), $$"""
			--nostdlib
			--name sample-lib
			--use-source local "{{sourceRootArgument}}"
			--use {{packageName}}:api
			src/*.camp
			""");
		try
		{
			string app = Path.Combine(appRoot, "app.camp");
			File.WriteAllText(app, """
				#build --nostdlib
				#build --name sample-app

				export int main()
				{
					return add(20, 22) - 42;
				}
				""");
			string target = NativeTargetForHost();
			string outDir = Path.Combine(appRoot, "bin");
			string referenceOutputDirectory = Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static));
			string libraryPath = NativeArtifactPathForTarget(target, NativeBuildKind.Static, referenceOutputDirectory, "sample-lib");

			ProcessResult first = RunCampc(
				"build",
				app,
				"--target",
				target,
				"--verbose",
				"--project-reference",
				libraryRoot + ":static",
				"--out-dir",
				outDir);

			AssertCommandSucceeded(first);
			Assert.Contains(libraryRoot + ":static: generated:", first.StdOut, StringComparison.Ordinal);
			Assert.True(File.Exists(libraryPath));
			DateTime currentOutputTime = DateTime.UtcNow.AddSeconds(5);
			foreach (string output in Directory.GetFiles(referenceOutputDirectory))
				File.SetLastWriteTimeUtc(output, currentOutputTime);
			DateTime firstLibraryWrite = File.GetLastWriteTimeUtc(libraryPath);

			ProcessResult second = RunCampc(
				"build",
				app,
				"--target",
				target,
				"--verbose",
				"--project-reference",
				libraryRoot + ":static",
				"--out-dir",
				outDir);

			AssertCommandSucceeded(second);
			Assert.DoesNotContain(libraryRoot + ":static: generated:", second.StdOut, StringComparison.Ordinal);
			Assert.Equal(firstLibraryWrite, File.GetLastWriteTimeUtc(libraryPath));
		}
		finally
		{
			if (Directory.Exists(cachedPackageRoot))
				Directory.Delete(cachedPackageRoot, recursive: true);
		}
	}

	[Fact]
	public void Project_reference_rejects_wrong_only_artifact_link_kind()
	{
		string root = TempPath("project-reference-only-kind");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "sample-app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), "export int value() => 1;\n");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			--artifact only-shared
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return value() - 1;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			TempPath("project-reference-only-kind-build"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("project reference requires shared linking but was requested as static", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Shared_project_reference_builds_copies_and_runs_on_macos()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Shared project-reference runtime smoke is currently macOS-only.");
		string root = TempPath("project-reference-shared-macos");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "sample-app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), "export int value() => 42;\n");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name sample-app

			export int main()
			{
				return value() - 42;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			"clang-macos-x64",
			"--project-reference",
			libraryRoot,
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		string libraryArtifactDirectory = Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Shared));
		string appArtifactDirectory = Path.Combine(outDir, ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Exec));
		Assert.True(File.Exists(Path.Combine(libraryArtifactDirectory, "libsample-lib.dylib")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "libsample-lib.dylib")));
		ProcessResult run = RunExecutable(Path.Combine(appArtifactDirectory, "sample-app"));
		Assert.Equal(0, run.ExitCode);
	}

	[Fact]
	public void Shared_project_reference_absorbs_static_dependency_on_macos()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Shared project-reference runtime smoke is currently macOS-only.");
		string root = TempPath("project-reference-shared-static-dep-macos");
		string aRoot = Path.Combine(root, "a");
		string bRoot = Path.Combine(root, "b");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(Path.Combine(aRoot, "src"));
		Directory.CreateDirectory(Path.Combine(bRoot, "src"));
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(aRoot, "src", "a.camp"), "export int aValue() => 20;\n");
		File.WriteAllText(Path.Combine(aRoot, "a.campbuild"), """
			--nostdlib
			--name a
			src/*.camp
			""");
		File.WriteAllText(Path.Combine(bRoot, "src", "b.camp"), "export int bValue() => aValue() + 22;\n");
		File.WriteAllText(Path.Combine(bRoot, "b.campbuild"), $$"""
			--nostdlib
			--name b
			--project-reference {{aRoot}}:static
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name shared-static-app

			export int main()
			{
				return bValue() - 42;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			"clang-macos-x64",
			"--project-reference",
			bRoot,
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		string appArtifactDirectory = Path.Combine(outDir, ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Exec));
		Assert.True(File.Exists(Path.Combine(aRoot, "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Static), "liba.a")));
		Assert.True(File.Exists(Path.Combine(bRoot, "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Shared), "libb.dylib")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "libb.dylib")));
		Assert.False(File.Exists(Path.Combine(appArtifactDirectory, "liba.a")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "shared-static-app")));
	}

	[Fact]
	public void Shared_project_reference_copies_transitive_shared_dependencies_on_macos()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Shared project-reference runtime smoke is currently macOS-only.");
		string root = TempPath("project-reference-shared-shared-dep-macos");
		string aRoot = Path.Combine(root, "a");
		string bRoot = Path.Combine(root, "b");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(Path.Combine(aRoot, "src"));
		Directory.CreateDirectory(Path.Combine(bRoot, "src"));
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(aRoot, "src", "a.camp"), "export int aValue() => 20;\n");
		File.WriteAllText(Path.Combine(aRoot, "a.campbuild"), """
			--nostdlib
			--name a
			src/*.camp
			""");
		File.WriteAllText(Path.Combine(bRoot, "src", "b.camp"), "export int bValue() => aValue() + 22;\n");
		File.WriteAllText(Path.Combine(bRoot, "b.campbuild"), $$"""
			--nostdlib
			--name b
			--project-reference {{aRoot}}
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name shared-shared-app

			export int main()
			{
				return bValue() - 42;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			"clang-macos-x64",
			"--project-reference",
			bRoot,
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		string appArtifactDirectory = Path.Combine(outDir, ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Exec));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "liba.dylib")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "libb.dylib")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "shared-shared-app")));
	}

	[Fact]
	public void Project_reference_cycles_report_direct_diagnostic()
	{
		string root = TempPath("project-reference-cycle");
		string aRoot = Path.Combine(root, "a");
		string bRoot = Path.Combine(root, "b");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(Path.Combine(aRoot, "src"));
		Directory.CreateDirectory(Path.Combine(bRoot, "src"));
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(aRoot, "src", "a.camp"), "export int a() => b();\n");
		File.WriteAllText(Path.Combine(bRoot, "src", "b.camp"), "export int b() => a();\n");
		File.WriteAllText(Path.Combine(aRoot, "a.campbuild"), $$"""
			--nostdlib
			--name a
			--project-reference {{bRoot}}:static
			src/*.camp
			""");
		File.WriteAllText(Path.Combine(bRoot, "b.campbuild"), $$"""
			--nostdlib
			--name b
			--project-reference {{aRoot}}:static
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --artifact none
			#build --name app

			export int main()
			{
				return a();
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--project-reference",
			aRoot + ":static",
			"--out-dir",
			Path.Combine(appRoot, "obj"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Project reference cycle detected", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("a.campbuild", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("b.campbuild", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("Stack overflow", result.StdErr, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Project_reference_transitive_static_libraries_flow_to_final_consumer()
	{
		string root = TempPath("project-reference-transitive");
		string aRoot = Path.Combine(root, "a");
		string bRoot = Path.Combine(root, "b");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(Path.Combine(aRoot, "src"));
		Directory.CreateDirectory(Path.Combine(bRoot, "src"));
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(aRoot, "src", "a.camp"), """
			export int aValue()
			{
				return 20;
			}
			""");
		File.WriteAllText(Path.Combine(bRoot, "src", "b.camp"), """
			export int bValue()
			{
				return aValue() + 22;
			}
			""");
		File.WriteAllText(Path.Combine(aRoot, "a.campbuild"), """
			--nostdlib
			--name a
			src/*.camp
			""");
		File.WriteAllText(Path.Combine(bRoot, "b.campbuild"), $$"""
			--nostdlib
			--name b
			--project-reference {{aRoot}}:static
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name transitive-app

			export int main()
			{
				return bValue() - 42;
			}
			""");
		string target = NativeTargetForHost();
		string outDir = Path.Combine(appRoot, "bin");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--verbose",
			"--project-reference",
			bRoot + ":static",
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		Assert.True(File.Exists(Path.Combine(aRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "a_api.camp")));
		Assert.True(File.Exists(Path.Combine(bRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "b_api.camp")));
		Assert.True(File.Exists(Path.Combine(outDir, ArtifactDirectoryForHost(NativeBuildKind.Exec), "transitive-app" + ExecutableExtensionForHost())));
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
				void retain(): IRefCount {}
				void release(): IRefCount {}
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
				string getName(): INamed => "named";
			}
			""");

		string repositoryRoot = FindRepositoryRoot();
		CompilerRequest request = new()
		{
			WorkingDirectory = repositoryRoot,
			RuntimeRoot = Path.Combine(repositoryRoot, "bin"),
			NoStdLib = true,
			InspectApi = true
		};
		request.Files.Add(Path.GetRelativePath(repositoryRoot, source));
		request.Files.Add(Path.GetRelativePath(repositoryRoot, secondSource));

		CompilerResult result = CompilerDriver.Execute(request);

		Assert.Equal(0, result.ExitCode);
		string api = result.StdOut;
		Assert.Contains("autorelease<T: implements IRefCount>", api, StringComparison.Ordinal);
		Assert.DoesNotContain("autorelease<T: IRefCount>", api, StringComparison.Ordinal);
		Assert.Contains("export extern class RefThing : IRefCount", api, StringComparison.Ordinal);
		Assert.Contains("export struct NamedRef", api, StringComparison.Ordinal);
		Assert.DoesNotContain("export struct NamedRef : INamed", api, StringComparison.Ordinal);
		Assert.DoesNotContain("using Std;", api, StringComparison.Ordinal);
	}

	[Fact]
	public void Generated_multi_namespace_api_header_is_consumable()
	{
		string root = TempPath("multi-namespace-api-consume");
		Directory.CreateDirectory(root);
		string librarySource = Path.Combine(root, "library.camp");
		File.WriteAllText(librarySource, """
			namespace ApiA;

			export struct Handle
			{
				int value;
			}

			export extern ApiB::Handle convertToB(Handle value);

			namespace ApiB
			{
				export struct Handle
				{
					int value;
				}

				export extern ApiA::Handle convertToA(Handle value);
			}

			namespace global
			{
				export struct RootHandle
				{
					int value;
				}
			}
			""");

		CompilerRequest apiRequest = new()
		{
			WorkingDirectory = root,
			RuntimeRoot = Path.Combine(FindRepositoryRoot(), "bin"),
			NoStdLib = true,
			InspectApi = true
		};
		apiRequest.Files.Add("library.camp");
		CompilerResult apiResult = CompilerDriver.Execute(apiRequest);

		Assert.Equal(0, apiResult.ExitCode);
		Assert.Contains("namespace ApiA", apiResult.StdOut, StringComparison.Ordinal);
		Assert.Contains("namespace ApiB", apiResult.StdOut, StringComparison.Ordinal);
		Assert.Contains("namespace global", apiResult.StdOut, StringComparison.Ordinal);
		Assert.Contains("export extern ApiB::Handle convertToB(Handle value);", apiResult.StdOut, StringComparison.Ordinal);
		Assert.Contains("export extern ApiA::Handle convertToA(Handle value);", apiResult.StdOut, StringComparison.Ordinal);

		string apiHeader = Path.Combine(root, "library_api.camp");
		File.WriteAllText(apiHeader, apiResult.StdOut);
		string consumerSource = Path.Combine(root, "consumer.camp");
		File.WriteAllText(consumerSource, """
			export extern void consume(ApiA::Handle a, ApiB::Handle b, global::RootHandle root);
			""");

		CompilerRequest consumerRequest = new()
		{
			WorkingDirectory = root,
			RuntimeRoot = Path.Combine(FindRepositoryRoot(), "bin"),
			NoStdLib = true,
			Inspect = CompilerInspectMode.Declarations
		};
		consumerRequest.ApiFiles.Add("library_api.camp");
		consumerRequest.Files.Add("consumer.camp");
		CompilerResult consumerResult = CompilerDriver.Execute(consumerRequest);

		Assert.Equal(0, consumerResult.ExitCode);
	}

	[Fact]
	public void Interface_object_vtable_thunks_are_emitted_with_implementing_type_source()
	{
		string root = TempPath("interface-object-vtable-thunks");
		Directory.CreateDirectory(root);
		string interfaces = Path.Combine(root, "interfaces.camp");
		File.WriteAllText(interfaces, """
			export interface IRefCount
			{
				void retain();
				void release();
			}
			""");
		string component = Path.Combine(root, "component.camp");
		File.WriteAllText(component, """
			extern void* malloc(nuint size);
			extern void free(void* pointer);

			export escaped class Component: IRefCount
			{
				nuint refct;

				export Component()
				{
				}

				export void retain(): IRefCount
				{
					this.refct++;
				}

				export void release(): IRefCount
				{
					if (this.refct > 0)
						this.refct--;
				}
			}

			export int main()
			{
				auto component = new Component() finally delete;
				IRefCount* refCount = component;
				return refCount == null ? 1 : 0;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			interfaces,
			component,
			"--nostdlib",
			"--target",
			NativeTargetForHost(),
			"--out-dir",
			TempPath("interface-object-vtable-thunks-bin"));

		Assert.Equal(0, result.ExitCode);
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
				int value(): IValue
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
				int value(): IValue
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
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--verbose",
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		Assert.Contains("generated: interface-app", result.StdOut, StringComparison.Ordinal);
		string api = File.ReadAllText(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "interfaces_api.camp"));
		Assert.Contains("export extern class Counter : IValue", api, StringComparison.Ordinal);
		Assert.Contains("export extern constof(this) IValue* getIValue();", api, StringComparison.Ordinal);
		Assert.Contains("export extern class NativeCounter : IValue", api, StringComparison.Ordinal);
		Assert.Contains("export extern class NativeDerived : NativeCounter", api, StringComparison.Ordinal);
		Assert.Contains("export struct StructCounter", api, StringComparison.Ordinal);
		Assert.DoesNotContain("export struct StructCounter : IValue", api, StringComparison.Ordinal);
		string cApi = File.ReadAllText(Path.Combine(outDir, ArtifactDirectoryForHost(NativeBuildKind.Exec), "build", "interfaces_api.h"));
		Assert.Contains("extern const IValue *Counter_IValue;", cApi, StringComparison.Ordinal);
		Assert.DoesNotContain("StructCounter_IValue", cApi, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(outDir, ArtifactDirectoryForHost(NativeBuildKind.Exec), "interface-app" + ExecutableExtensionForHost()));
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
	public void Msvc_target_loads_visual_studio_environment_when_needed()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("MSVC environment validation only applies on Windows.");
		if (!MsvcBuildToolsInstalled())
			Assert.Skip("MSVC Build Tools are not installed.");
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
			"--out-dir",
			TempPath("msvc-environment-missing-out"));

		AssertCommandSucceeded(result);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Msvc_target_uses_requested_architecture_over_loaded_visual_studio_environment()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("MSVC environment validation only applies on Windows.");
		if (!MsvcBuildToolsInstalled())
			Assert.Skip("MSVC Build Tools are not installed.");
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
			"--out-dir",
			TempPath("msvc-architecture-mismatch-out"));

		AssertCommandSucceeded(result);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Msvc_target_reports_invalid_vcvarsall_override()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("MSVC environment validation only applies on Windows.");
		string temp = CreateTempCase("msvc-invalid-vcvarsall/main.camp", """
			export int value()
			{
				return 1;
			}
			""");
		string missingVcVarsAll = Path.Combine(TempPath("missing-vs"), "vcvarsall.bat");

		ProcessResult result = RunCampc(
			new Dictionary<string, string?>
			{
				["VSCMD_ARG_TGT_ARCH"] = null,
				["Platform"] = null,
				["CAMP_VCVARSALL"] = missingVcVarsAll
			},
			"build",
			temp,
			"--nostdlib",
			"--artifact",
			"static",
			"--target",
			"msvc-windows-x64",
			"--out-dir",
			TempPath("msvc-invalid-vcvarsall-out"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("CAMP_VCVARSALL points to", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("vcvarsall.bat", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("Native build command failed", result.StdErr, StringComparison.Ordinal);
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
			"--verbose",
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(result);
		Assert.Contains("generated: sample-app.exe", result.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "sample-lib.lib")));
		Assert.True(File.Exists(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "sample-lib_api.camp")));
		Assert.True(File.Exists(Path.Combine(appRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Exec), "build", "sample_lib_api.h")));
		string cApi = File.ReadAllText(Path.Combine(appRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Exec), "build", "sample_lib_api.h"));
		Assert.DoesNotContain("__declspec(dllimport)", cApi, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(appRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Exec), "sample-app.exe"));
		Assert.Equal(0, run.ExitCode);
		Assert.Equal("", run.StdErr);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Project_reference_links_native_shared_library_with_msvc()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools are not available on PATH.");
		string root = TempPath("project-reference-msvc-shared");
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
		string appArtifactDirectory = Path.Combine(appRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Exec));
		string libraryArtifactDirectory = Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Shared));

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot,
			"--out-dir",
			Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(result);
		string importLibrary = Path.Combine(libraryArtifactDirectory, "sample-lib.lib");
		Assert.True(File.Exists(importLibrary));
		Assert.True(File.Exists(Path.Combine(libraryArtifactDirectory, "sample-lib.dll")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "sample-lib.dll")));
		string cApi = File.ReadAllText(Path.Combine(appArtifactDirectory, "build", "sample_lib_api.h"));
		Assert.Contains("__declspec(dllimport)", cApi, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(appArtifactDirectory, "sample-app.exe"));
		Assert.Equal(0, run.ExitCode);
		Assert.Equal("", run.StdErr);

		File.SetLastWriteTimeUtc(importLibrary, DateTime.UtcNow.AddHours(-1));
		ProcessResult second = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot,
			"--out-dir",
			Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(second);
		Assert.DoesNotContain(libraryRoot + ": generated:", second.StdOut, StringComparison.Ordinal);
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
		string libraryPath = Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "sample-lib.lib");
		string executablePath = Path.Combine(appRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Exec), "sample-app.exe");

		ProcessResult firstBuild = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			Path.Combine(appRoot, "bin"));

			AssertCommandSucceeded(firstBuild);
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
		File.SetLastWriteTimeUtc(libraryFile, DateTime.UtcNow.AddSeconds(5));

		ProcessResult secondBuild = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			Path.Combine(appRoot, "bin"));

			AssertCommandSucceeded(secondBuild);
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

		ProcessResult result = RunCampc("build", derived, baseFile, "--artifact", "none", "--out-dir", buildDir);

		Assert.Equal(0, result.ExitCode);
		string privateHeader = Directory.GetFiles(Path.Combine(buildDir, ArtifactDirectoryForHost(null), "build"), "*_private.h").Single();
		string header = File.ReadAllText(privateHeader);
		Assert.Contains("_Base *_vt;", header, StringComparison.Ordinal);
		Assert.DoesNotContain("_Derived *_vt;", header, StringComparison.Ordinal);
	}

	[Fact]
	public void Public_virtual_class_vtable_is_visible_across_generated_source_files()
	{
		string root = TempPath("virtual-vtable-cross-file");
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
		string source = Path.Combine(root, "src");
		Directory.CreateDirectory(source);
		File.WriteAllText(Path.Combine(source, "alloc.camp"), """
			export extern void* malloc(nuint size);
			export extern void free(void* ptr);
			""");
		File.WriteAllText(Path.Combine(source, "helper.camp"), """
			public virtual escaped class Base
			{
				public virtual ~Base()
				{
				}
			}

			public virtual escaped class Derived: Base
			{
				override ~Derived()
				{
				}
			}
			""");
		File.WriteAllText(Path.Combine(source, "main.camp"), """
			export int main()
			{
				auto value = new Derived();
				return value == null ? 1 : 0;
			}
			""");
		File.WriteAllText(Path.Combine(root, "widgets.campbuild"), """
			--nostdlib
			--name widgets
			--artifact exec
			src/*.camp
			""");

		string outDir = Path.Combine(root, "bin");
		ProcessResult result = RunCampc("build", Path.Combine(root, "widgets.campbuild"), "--target", NativeTargetForHost(), "--out-dir", outDir);

		AssertCommandSucceeded(result);
		string artifactDir = Path.Combine(outDir, ArtifactDirectoryForHost(NativeBuildKind.Exec));
		string privateHeader = File.ReadAllText(Path.Combine(artifactDir, "build", "widgets_private.h"));
		Assert.Contains("extern _Derived _Derived__vt;", privateHeader, StringComparison.Ordinal);
		string helperC = File.ReadAllText(Path.Combine(artifactDir, "build", "helper.c"));
		Assert.Contains("_Derived _Derived__vt = ", helperC, StringComparison.Ordinal);
		Assert.DoesNotContain("static _Derived _Derived__vt", helperC, StringComparison.Ordinal);
	}

	[Fact]
	public void Inherited_virtual_destructor_thunk_is_visible_across_generated_source_files()
	{
		string root = TempPath("virtual-destructor-cross-file");
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
		string source = Path.Combine(root, "src");
		Directory.CreateDirectory(source);
		File.WriteAllText(Path.Combine(source, "alloc.camp"), """
			export extern void* malloc(nuint size);
			export extern void free(void* ptr);
			""");
		File.WriteAllText(Path.Combine(source, "component.camp"), """
			export virtual escaped class Component
			{
				export Component()
				{
				}

				export virtual ~Component()
				{
				}
			}
			""");
		File.WriteAllText(Path.Combine(source, "control.camp"), """
			export virtual escaped class Control: Component
			{
				export Control()
				{
				}

				override ~Control()
				{
				}
			}
			""");
		File.WriteAllText(Path.Combine(source, "button.camp"), """
			export sealed escaped class Button: Control
			{
				export Button()
				{
				}
			}
			""");
		File.WriteAllText(Path.Combine(root, "widgets.campbuild"), """
			--nostdlib
			--name widgets
			--artifact static
			src/*.camp
			""");

		string outDir = Path.Combine(root, "bin");
		ProcessResult result = RunCampc("build", Path.Combine(root, "widgets.campbuild"), "--target", NativeTargetForHost(), "--out-dir", outDir);

		AssertCommandSucceeded(result);
		string artifactDir = Path.Combine(outDir, ArtifactDirectoryForHost(NativeBuildKind.Static));
		string privateHeader = File.ReadAllText(Path.Combine(artifactDir, "build", "widgets_private.h"));
		Assert.Contains("void Control__op_delete(Component *ctx);", privateHeader, StringComparison.Ordinal);
		string buttonC = File.ReadAllText(Path.Combine(artifactDir, "build", "button.c"));
		Assert.Contains(".op_delete = Control__op_delete", buttonC, StringComparison.Ordinal);
		string api = File.ReadAllText(Path.Combine(artifactDir, "widgets_api.camp"));
		Assert.Contains("export extern ~Component();", api, StringComparison.Ordinal);
		Assert.DoesNotContain("void ~Component", api, StringComparison.Ordinal);
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
			"--verbose",
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			TempPath("project-reference-virtual-api-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: project_reference_virtual_api_app.c", result.StdOut, StringComparison.Ordinal);
		string api = File.ReadAllText(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Static), "widgets_api.camp"));
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
		string sourceRoot = Path.Combine(root, "package-source");
		string cachedPackageRoot = Path.Combine(FindRepositoryRoot(), "cache", "pkg", "live-demo");
		if (Directory.Exists(cachedPackageRoot))
			Directory.Delete(cachedPackageRoot, recursive: true);
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

		ProcessResult first = RunCampc("build", app, "--verbose", "--out-dir", TempPath("live-use-source-build-1"));

		Assert.Equal(0, first.ExitCode);
		Assert.Contains("generated: live_use_source_app.c", first.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(cachedPackageRoot, "live", "bin", ArtifactDirectoryForHost(NativeBuildKind.Shared), "live-demo_api.camp")));
		Assert.False(Directory.Exists(Path.Combine(sourceRoot, "live-demo", "bin")));
		Assert.False(Directory.Exists(Path.Combine(sourceRoot, "live-demo", "build")));

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

		ProcessResult second = RunCampc("build", app, "--verbose", "--out-dir", TempPath("live-use-source-build-2"));

		Assert.Equal(0, second.ExitCode);
		Assert.Contains("generated: live_use_source_app.c", second.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(cachedPackageRoot, "live", "bin", ArtifactDirectoryForHost(NativeBuildKind.Shared), "live-demo_api.camp")));
	}

	[Fact]
	public void Build_accepts_imported_alias_types_in_member_calls()
	{
		string root = TempPath("imported-alias-member-call");
		string sourceRoot = Path.Combine(root, "package-source");
		string packageRoot = Path.Combine(sourceRoot, "alias-projection", "src");
		Directory.CreateDirectory(packageRoot);
		File.WriteAllText(Path.Combine(packageRoot, "api.camp"), """
			namespace AliasProjection;

			export alias WPARAM = nuint;
			export alias LPARAM = nint;

			export extern void useNative(WPARAM w, LPARAM l);
			""");

		string sourceRootArgument = sourceRoot.Replace('\\', '/');
		string app = CreateTempCase("imported_alias_member_call.camp", $$"""
			#build --nostdlib
			#build --artifact none
			#build --use-source local "{{sourceRootArgument}}"
			#build --use alias-projection:api

			using AliasProjection;

			extern void* malloc(nuint size);
			extern void free(void* ptr);

			virtual class Control
			{
				virtual bool reflect(WPARAM w, LPARAM l)
				{
					return w != 0 || l != 0;
				}
			}

			export int main()
			{
				Control* child = new Control();
				WPARAM wParam = 1;
				LPARAM lParam = 2;
				bool ok = child.reflect(wParam, lParam);
				delete child;
				return ok ? 0 : 1;
			}
			""");

		ProcessResult result = RunCampc("build", app, "--out-dir", TempPath("imported-alias-member-call-out"));

		AssertCommandSucceeded(result);
	}

	[Fact]
	public void Shared_library_api_header_uses_source_type_names_in_delegate_parameters()
	{
		string source = CreateTempCase("shared_api_delegate_parameter/main.camp", """
			namespace Win32::Forms;

			export struct Message
			{
				int value;
			}

			export interface IControlHost
			{
				nint handleWndProc(const Message* message, delegate nint(const Message*) baseWndProc) = default;
			}
			""");
		string outDir = TempPath("shared-api-delegate-parameter-out");

		ProcessResult result = RunCampc(
			"build",
			source,
			"--nostdlib",
			"--artifact",
			"shared",
			"--target",
			NativeTargetForHost(),
			"--name",
			"shared_api_delegate_parameter",
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		string apiHeader = File.ReadAllText(Path.Combine(outDir, ArtifactDirectoryForHost(NativeBuildKind.Shared), "shared_api_delegate_parameter_api.camp"));
		Assert.Contains("delegate nint(const Message*) baseWndProc", apiHeader, StringComparison.Ordinal);
		Assert.DoesNotContain("Win32FormsMessage", apiHeader, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_reports_missing_use_source_path_with_resolved_path()
	{
		string root = TempPath("missing-use-source-root");
		string missingSourceRoot = Path.Combine(root, "package-source");
		string sourceRootArgument = missingSourceRoot.Replace('\\', '/');
		string app = CreateTempCase("missing_use_source_app.camp", $$"""
			#build --nostdlib
			#build --artifact none
			#build --use-source local "{{sourceRootArgument}}"

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", app);

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains($"Package source 'local' path '{Path.GetFullPath(missingSourceRoot)}' could not be found.", result.StdErr, StringComparison.Ordinal);
		Assert.Contains($"resolved path: {Path.GetFullPath(missingSourceRoot)}", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_reports_missing_live_package_with_searched_roots()
	{
		string root = TempPath("missing-live-package");
		string sourceRoot = Path.Combine(root, "package-source");
		Directory.CreateDirectory(sourceRoot);
		string sourceRootArgument = sourceRoot.Replace('\\', '/');
		string app = CreateTempCase("missing_live_package_app.camp", $$"""
			#build --nostdlib
			#build --artifact none
			#build --use-source local "{{sourceRootArgument}}"
			#build --use missing-live:api

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", app);

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Package 'missing-live:api' could not be found.", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("Searched package source roots:", result.StdErr, StringComparison.Ordinal);
		Assert.Contains(Path.Combine(Path.GetFullPath(sourceRoot), "missing-live", "src"), result.StdErr, StringComparison.Ordinal);
		Assert.Contains("Searched installed package roots:", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Live_package_dependency_builds_shared_by_default_and_static_separately_on_macos()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Shared package runtime smoke is currently macOS-only.");
		string root = TempPath("live-package-shared-static");
		string sourceRoot = Path.Combine(root, "package-source");
		string cachedPackageRoot = Path.Combine(FindRepositoryRoot(), "cache", "pkg", "live-link-demo");
		if (Directory.Exists(cachedPackageRoot))
			Directory.Delete(cachedPackageRoot, recursive: true);
		string packageSource = Path.Combine(sourceRoot, "live-link-demo", "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(packageSource);
		Directory.CreateDirectory(appRoot);
		string sourceRootArgument = sourceRoot.Replace('\\', '/');
		File.WriteAllText(Path.Combine(packageSource, "demo.camp"), """
			export int liveValue()
			{
				return 42;
			}
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, $$"""
			#build --nostdlib
			#build --name live-package-app
			#build --use-source local "{{sourceRootArgument}}"
			#build --use live-link-demo

			export int main()
			{
				return liveValue() - 42;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");

		ProcessResult shared = RunCampc("build", app, "--target", "clang-macos-x64", "--out-dir", outDir);

		AssertCommandSucceeded(shared);
		string sharedCacheDirectory = Path.Combine(cachedPackageRoot, "live", "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Shared));
		string appArtifactDirectory = Path.Combine(outDir, ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Exec));
		Assert.True(File.Exists(Path.Combine(sharedCacheDirectory, "liblive-link-demo.dylib")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "liblive-link-demo.dylib")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "live-package-app")));

		File.WriteAllText(app, $$"""
			#build --nostdlib
			#build --name live-package-static-app
			#build --use-source local "{{sourceRootArgument}}"
			#build --use live-link-demo:static

			export int main()
			{
				return liveValue() - 42;
			}
			""");
		ProcessResult staticResult = RunCampc("build", app, "--target", "clang-macos-x64", "--out-dir", outDir);

		AssertCommandSucceeded(staticResult);
		string staticCacheDirectory = Path.Combine(cachedPackageRoot, "live", "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Static));
		Assert.True(File.Exists(Path.Combine(staticCacheDirectory, "liblive-link-demo.a")));
		Assert.True(File.Exists(Path.Combine(staticCacheDirectory, "live-link-demo_api.camp")));
		Assert.NotEqual(sharedCacheDirectory, staticCacheDirectory);
	}

	[Fact]
	public void Live_static_package_dependency_lowers_iterators_before_api_emission()
	{
		string root = TempPath("live-static-package-iterator-api");
		string sourceRoot = Path.Combine(root, "package-source");
		string cachedPackageRoot = Path.Combine(FindRepositoryRoot(), "cache", "pkg", "live-iterator-demo");
		if (Directory.Exists(cachedPackageRoot))
			Directory.Delete(cachedPackageRoot, recursive: true);
		string packageSource = Path.Combine(sourceRoot, "live-iterator-demo", "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(packageSource);
		Directory.CreateDirectory(appRoot);
		string sourceRootArgument = sourceRoot.Replace('\\', '/');
		File.WriteAllText(Path.Combine(packageSource, "demo.camp"), """
			#build --nostdlib

			namespace LiveIteratorDemo;

			public struct View
			{
				const char[] text;
			}

			public nuint viewLength(View view)
			{
				return view.text.length;
			}

			public fixed struct Writer
			{
				fixed Frame[2] stack;
			}

			public struct iter View parts(const char[] source)
			{
				yield { { source.elements, source.length } };
			}

			enum Container: byte
			{
				ARRAY,
			}

			struct Frame
			{
				Container container;
			}
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, $$"""
			#build --nostdlib
			#build --name live-iterator-app
			#build --artifact static
			#build --use-source local "{{sourceRootArgument}}"
			#build --use live-iterator-demo:static

			using LiveIteratorDemo;

			export int value()
			{
				int total = 0;
				foreach (auto view in parts("abc"))
					total += (int)viewLength(view);
				return total - 3;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc("build", app, "--target", target, "--out-dir", outDir);

		AssertCommandSucceeded(result);
		string staticCacheDirectory = Path.Combine(cachedPackageRoot, "live", "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static));
		string apiPath = Path.Combine(staticCacheDirectory, "live-iterator-demo_api.camp");
		Assert.True(File.Exists(apiPath));
		string api = File.ReadAllText(apiPath);
		Assert.DoesNotContain("op_delete", api, StringComparison.Ordinal);
		Assert.Contains("public extern void destroy();", api, StringComparison.Ordinal);
	}

	[Fact]
	public void Live_api_package_dependency_emits_headers_without_native_library()
	{
		string root = TempPath("live-package-api-only");
		string sourceRoot = Path.Combine(root, "package-source");
		string cachedPackageRoot = Path.Combine(FindRepositoryRoot(), "cache", "pkg", "api-demo");
		string packageSource = Path.Combine(sourceRoot, "api-demo", "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(packageSource);
		Directory.CreateDirectory(appRoot);
		string sourceRootArgument = sourceRoot.Replace('\\', '/');
		File.WriteAllText(Path.Combine(packageSource, "api.camp"), """
			export newtype NativeHandle: nint;
			export extern NativeHandle getNativeHandle();
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, $$"""
			#build --nostdlib
			#build --name api-package-app
			#build --use-source local "{{sourceRootArgument}}"
			#build --use api-demo:api

			export int main()
			{
				NativeHandle handle = default;
				return 0;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");
		string target = NativeTargetForHost();
		if (Directory.Exists(cachedPackageRoot))
			Directory.Delete(cachedPackageRoot, recursive: true);

		try
		{
			ProcessResult result = RunCampc("build", app, "--target", target, "--out-dir", outDir);

			AssertCommandSucceeded(result);
			string apiCacheDirectory = Path.Combine(cachedPackageRoot, "live", "bin", ArtifactDirectoryForTarget(target, DependencyLinkKind.Api));
			Assert.True(File.Exists(Path.Combine(apiCacheDirectory, "api-demo_api.camp")));
			Assert.True(File.Exists(Path.Combine(apiCacheDirectory, "api-demo_api.h")));
			Assert.True(File.Exists(Path.Combine(apiCacheDirectory, "api-demo_api.json")));
			Assert.DoesNotContain(Directory.GetFiles(apiCacheDirectory), path => Path.GetExtension(path) is ".a" or ".lib" or ".dll" or ".dylib" or ".so");
		}
		finally
		{
			if (Directory.Exists(cachedPackageRoot))
				Directory.Delete(cachedPackageRoot, recursive: true);
		}
	}

	[Fact]
	public void Response_file_use_option_does_not_consume_source_patterns()
	{
		string root = TempPath("response-use-source-pattern");
		string sourceRoot = Path.Combine(root, "package-source");
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
			--use-source local ../package-source
			--use live-demo
			src/*.camp
			""");

		ProcessResult result = RunCampc("build", "@" + buildFile, "--verbose");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Recursive_glob_matches_files_directly_under_root()
	{
		string root = TempPath("recursive-glob-root");
		string sourceRoot = Path.Combine(root, "src");
		string nestedRoot = Path.Combine(sourceRoot, "nested");
		Directory.CreateDirectory(nestedRoot);
		File.WriteAllText(Path.Combine(sourceRoot, "main.camp"), """
			export int main()
			{
				return helper() - 7;
			}
			""");
		File.WriteAllText(Path.Combine(nestedRoot, "helper.camp"), """
			export int helper()
			{
				return 7;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--name recursive_glob_root
			src/**/*.camp
			""");

		ProcessResult result = RunCampc("build", "@" + buildFile, "--verbose", "--out-dir", TempPath("recursive-glob-root-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
		Assert.Contains("generated: helper.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Api_files_contribute_build_pragmas_without_becoming_project_sources()
	{
		string api = CreateTempCase("api_pragmas_api.camp", """
			#build --nostdlib
			#build --artifact none

			export extern void includedOnly();
			""");
		string source = CreateTempCase("api_pragmas_main.camp", """
			export void main()
			{
			}
			""");

		ProcessResult result = RunCampc("build", source, "--api", api, "--verbose", "--out-dir", TempPath("api-pragma-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: api_pragmas_main.c", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("api_pragmas_api.c", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("_api.camp", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Api_files_accept_api_only_declarations()
	{
		string api = CreateTempCase("api_only_declarations_api.camp", """
			export extern void helper();

			export interface IValue
			{
				int getValue(const this);
			}

			export alias Value = int;

			export enum Color
			{
				RED,
				BLUE
			}

			export struct Point
			{
				int x;
				int y;
			}

			export inline int LIMIT = 7;
			""");
		string source = CreateTempCase("api_only_declarations_main.camp", """
			export void main()
			{
			}
			""");

		ProcessResult result = BuildWithApiInProcess("api-only-declarations-out", noStdLib: true, [source], [api]);

		AssertCommandSucceeded(result);
	}

	[Fact]
	public void Api_file_rejects_function_body()
	{
		string api = CreateTempCase("api_function_body_api.camp", """
			export int helper()
			{
				return 1;
			}
			""");
		string source = CreateTempCase("api_function_body_main.camp", """
			export void main()
			{
			}
			""");

		ProcessResult result = BuildWithApiInProcess("api-function-body-out", noStdLib: true, [source], [api]);

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Function 'helper' in API file", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("has a body", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Api_file_rejects_storage_global_variable()
	{
		string api = CreateTempCase("api_global_storage_api.camp", """
			export int state = 1;
			""");
		string source = CreateTempCase("api_global_storage_main.camp", """
			export void main()
			{
			}
			""");

		ProcessResult result = BuildWithApiInProcess("api-global-storage-out", noStdLib: true, [source], [api]);

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Variable 'state' in API file", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("requires storage", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Api_file_rejects_storage_static_field()
	{
		string api = CreateTempCase("api_static_storage_api.camp", """
			export struct Counter
			{
				export static int current = 1;
			}
			""");
		string source = CreateTempCase("api_static_storage_main.camp", """
			export void main()
			{
			}
			""");

		ProcessResult result = BuildWithApiInProcess("api-static-storage-out", noStdLib: true, [source], [api]);

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Static field 'Counter.current' in API file", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("requires storage", result.StdErr, StringComparison.Ordinal);
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

		ProcessResult result = BuildInProcess("metadata-std-filter-out", noStdLib: false, request =>
		{
			request.EmitMetadata = MetadataVisibility.Export;
			request.ProjectName = "metadata_std_filter";
		}, temp);

		Assert.Equal(0, result.ExitCode);
		string metadataPath = Path.Combine(outDir, ArtifactDirectoryForHost(null), "metadata_std_filter_api.json");
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
	public void Shared_library_api_omits_standard_library_declarations()
	{
		string temp = CreateTempCase("shared_std_filter.camp", """
			using Std;

			export int meaning()
			{
				Console.writeLine("hi");
				return 42;
			}
			""");
		string outDir = TempPath("shared-std-filter-out");
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc("build", temp, "--artifact", "shared", "--target", target, "--out-dir", outDir, "--name", "shared_std_filter");

		AssertCommandSucceeded(result);
		string artifactDirectory = Path.Combine(outDir, ArtifactDirectoryForTarget(target, NativeBuildKind.Shared));
		string campApi = File.ReadAllText(Path.Combine(artifactDirectory, "shared_std_filter_api.camp"));
		string cApi = File.ReadAllText(Path.Combine(artifactDirectory, "shared_std_filter_api.h"));
		Assert.Contains("export extern int meaning();", campApi, StringComparison.Ordinal);
		Assert.DoesNotContain("Console", campApi, StringComparison.Ordinal);
		Assert.DoesNotContain("Allocator", campApi, StringComparison.Ordinal);
		Assert.DoesNotContain("malloc", campApi, StringComparison.Ordinal);
		Assert.Contains("meaning", cApi, StringComparison.Ordinal);
		Assert.DoesNotContain("Console", cApi, StringComparison.Ordinal);
		Assert.DoesNotContain("Allocator", cApi, StringComparison.Ordinal);
		Assert.DoesNotContain("malloc", cApi, StringComparison.Ordinal);
	}

	[Fact]
	public void Api_pragmas_discovered_from_source_pragmas_contribute_build_pragmas()
	{
		string api = CreateTempCase("discovered_api_pragmas_api.camp", """
			#build --reference missing-one.a missing-two.a

			export extern void includedOnly();
			""");
		string source = CreateTempCase("discovered_api_pragmas_main.camp", $$"""
			#build --nostdlib
			#build --artifact exec
			#build --api {{api}}

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", source, "--target", NativeTargetForHost(), "--out-dir", TempPath("discovered-api-pragma-out"));
		string output = result.StdOut + result.StdErr;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("missing-one.a", output, StringComparison.Ordinal);
		Assert.Contains("missing-two.a", output, StringComparison.Ordinal);
		Assert.DoesNotContain("discovered_api_pragmas_api.c", result.StdOut, StringComparison.Ordinal);
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

		ProcessResult result = RunCampc("build", temp, "-r", "missing-one.a", "missing-two.a", "--target", NativeTargetForHost(), "--out-dir", TempPath("reference-out"));
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

		ProcessResult result = RunCampc("build", temp, "-f", "MissingOne", "MissingTwo", "--target", "clang-macos-x64", "--out-dir", TempPath("framework-out"));
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

		ProcessResult result = RunCampc("build", temp, "--target", "clang-macos-x64", "--out-dir", TempPath("framework-pragma-out"));
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
		Assert.Contains("Package 'missing-one@1.0.0' could not be found.", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_reports_missing_project_reference_with_resolved_path()
	{
		string root = TempPath("missing-project-reference");
		Directory.CreateDirectory(root);
		string app = Path.Combine(root, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --artifact none
			#build --project-reference missing

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", app);

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Project reference 'missing' could not be found.", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("Resolved path:", result.StdErr, StringComparison.Ordinal);
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

		ProcessResult result = RunCampc("build", temp, "--target", NativeTargetForHost(), "--out-dir", TempPath("reference-pragma-out"));
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

		ProcessResult result = RunCampc("build", temp, "--target", "gcc-linux-x64", "--verbose", "--out-dir", outDir);

		AssertCommandSucceeded(result);
		Assert.Contains("generated: gcc-linux-x64-smoke", result.StdOut, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(outDir, ArtifactDirectoryForTarget("gcc-linux-x64", NativeBuildKind.Exec), "gcc-linux-x64-smoke"));
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

		ProcessResult result = RunCampc("build", temp, "--target", "gcc-linux-x86", "--verbose", "--out-dir", outDir);

		AssertCommandSucceeded(result);
		Assert.Contains("generated: gcc-linux-x86-smoke", result.StdOut, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(outDir, ArtifactDirectoryForTarget("gcc-linux-x86", NativeBuildKind.Exec), "gcc-linux-x86-smoke"));
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

	[Fact]
	public void Restore_installs_packages_into_cache_pkg()
	{
		string packageName = "cache-demo-stage4";
		string repositoryRoot = FindRepositoryRoot();
		string cachePackageRoot = Path.Combine(repositoryRoot, "cache", "pkg", packageName);
		if (Directory.Exists(cachePackageRoot))
			Directory.Delete(cachePackageRoot, recursive: true);
		string oldPackageRoot = Path.Combine(repositoryRoot, "pkg", packageName);
		if (Directory.Exists(oldPackageRoot))
			Directory.Delete(oldPackageRoot, recursive: true);

		string tempRoot = TempPath("pkg-restore-cache");
		string sourceFile = Path.Combine(tempRoot, "source", packageName, "1.2.3", "src", "demo.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
		File.WriteAllText(sourceFile, "export int restoredValue() => 7;\n");
		string sourceRootArgument = Path.Combine(tempRoot, "source").Replace('\\', '/');
		string app = CreateTempCase("pkg_restore_cache.camp", $$"""
			#build --use-source local "{{sourceRootArgument}}"
			#build --use {{packageName}}@1.2.3
			""");

		ProcessResult result = RunCampc("restore", app);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains($"installed: {packageName}@1.2.3", result.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(cachePackageRoot, "1.2.3", "src", "demo.camp")));
		Assert.False(Directory.Exists(oldPackageRoot));
	}

	static ProcessResult RunCampc(params string[] arguments)
	{
		return RunCampc(null, arguments);
	}

	static ProcessResult BuildInProcess(string outputName, bool noStdLib, params string[] files)
	{
		return BuildInProcess(outputName, noStdLib, configure: null, files);
	}

	static ProcessResult BuildInProcess(string outputName, bool noStdLib, Action<CompilerRequest>? configure, params string[] files)
	{
		return BuildWithApiInProcess(outputName, noStdLib, files, [], configure);
	}

	static ProcessResult BuildWithApiInProcess(string outputName, bool noStdLib, IReadOnlyList<string> files, IReadOnlyList<string> apiFiles)
	{
		return BuildWithApiInProcess(outputName, noStdLib, files, apiFiles, configure: null);
	}

	static ProcessResult BuildWithApiInProcess(string outputName, bool noStdLib, IReadOnlyList<string> files, IReadOnlyList<string> apiFiles, Action<CompilerRequest>? configure)
	{
		using IDisposable timing = TestTiming.Measure("CommandLine in-process build " + outputName);
		string repositoryRoot = FindRepositoryRoot();
		string outputDirectory = TempPath(outputName);
		if (Directory.Exists(outputDirectory))
			Directory.Delete(outputDirectory, recursive: true);
		CompilerRequest request = new()
		{
			RuntimeRoot = Path.Combine(repositoryRoot, "bin"),
			TargetRoot = Path.Combine(repositoryRoot, "targets"),
			PackageSourceRoot = Path.Combine(repositoryRoot, "lib"),
			PackageArtifactRoot = Path.Combine(repositoryRoot, "tmp", "cli-tests-packages"),
			WorkingDirectory = repositoryRoot,
			OutDir = outputDirectory,
			NoStdLib = noStdLib,
			BuildKind = null
		};
		request.Files.AddRange(files);
		request.ApiFiles.AddRange(apiFiles);
		configure?.Invoke(request);

		CompilerResult result = CompilerDriver.Execute(request);
		return new ProcessResult(result.ExitCode, Normalize(result.StdOut), Normalize(result.StdErr));
	}

	static ProcessResult RunCampc(IReadOnlyDictionary<string, string?>? environmentVariables, params string[] arguments)
	{
		TestMetrics.RecordExternalCampcInvocation();
		using IDisposable timing = TestTiming.Measure("CommandLine campc " + string.Join(" ", arguments.Take(6)) + (arguments.Length > 6 ? " ..." : ""));
		string repositoryRoot = FindRepositoryRoot();
		ProcessStartInfo info = TestToolPaths.CreateCampcStartInfo(repositoryRoot);
		info.WorkingDirectory = repositoryRoot;
		info.RedirectStandardOutput = true;
		info.RedirectStandardError = true;
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
		using IDisposable gate = TestResourceGate.EnterCli();
		process.Start();
		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, Normalize(stdout), Normalize(stderr));
	}

	static ProcessResult RunCampcIn(string workingDirectory, params string[] arguments)
	{
		TestMetrics.RecordExternalCampcInvocation();
		using IDisposable timing = TestTiming.Measure("CommandLine campc in " + Path.GetFileName(workingDirectory) + " " + string.Join(" ", arguments.Take(6)) + (arguments.Length > 6 ? " ..." : ""));
		string repositoryRoot = FindRepositoryRoot();
		ProcessStartInfo info = TestToolPaths.CreateCampcStartInfo(repositoryRoot);
		info.WorkingDirectory = workingDirectory;
		info.RedirectStandardOutput = true;
		info.RedirectStandardError = true;
		foreach (string argument in arguments)
			info.ArgumentList.Add(argument);

		using Process process = new() { StartInfo = info };
		using IDisposable gate = TestResourceGate.EnterCli();
		process.Start();
		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, Normalize(stdout), Normalize(stderr));
	}

	static ProcessResult RunCampcFrom(string campcPath, string workingDirectory, params string[] arguments)
	{
		TestMetrics.RecordExternalCampcInvocation();
		using IDisposable timing = TestTiming.Measure("CommandLine installed campc " + string.Join(" ", arguments.Take(6)) + (arguments.Length > 6 ? " ..." : ""));
		ProcessStartInfo info = TestToolPaths.CreateStartInfoForPath(campcPath);
		info.WorkingDirectory = workingDirectory;
		info.RedirectStandardOutput = true;
		info.RedirectStandardError = true;
		foreach (string argument in arguments)
			info.ArgumentList.Add(argument);

		using Process process = new() { StartInfo = info };
		using IDisposable gate = TestResourceGate.EnterCli();
		process.Start();
		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, Normalize(stdout), Normalize(stderr));
	}

	static void CopyDirectory(string sourceDirectory, string destinationDirectory)
	{
		Directory.CreateDirectory(destinationDirectory);
		foreach (string sourceFile in Directory.GetFiles(sourceDirectory))
		{
			string destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
			File.Copy(sourceFile, destinationFile, overwrite: true);
		}
		foreach (string sourceSubdirectory in Directory.GetDirectories(sourceDirectory))
			CopyDirectory(sourceSubdirectory, Path.Combine(destinationDirectory, Path.GetFileName(sourceSubdirectory)));
	}

	static ProcessResult RunExecutable(string executable)
	{
		using IDisposable timing = TestTiming.Measure("CommandLine executable " + Path.GetFileName(executable));
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
		using IDisposable timing = TestTiming.Measure("CommandLine process " + Path.GetFileName(executable) + " " + string.Join(" ", arguments.Take(4)) + (arguments.Count > 4 ? " ..." : ""));
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

	static string ArtifactDirectoryForHost(NativeBuildKind? buildKind)
	{
		return ArtifactDirectoryForTarget(NativeTargetForHost(), buildKind);
	}

	static string ArtifactDirectoryForTarget(string targetName, NativeBuildKind? buildKind)
	{
		Assert.True(TargetCatalog.TryLoadCached(Path.Combine(FindRepositoryRoot(), "targets"), out TargetCatalog? catalog, out string? error), error);
		Assert.True(catalog!.TryGetTarget(targetName, out TargetDefinition? target));
		return BuildArtifactLayout.GetArtifactDirectoryName(target!, buildKind, "DEBUG");
	}

	static string ArtifactDirectoryForTarget(string targetName, DependencyLinkKind linkKind)
	{
		Assert.True(TargetCatalog.TryLoadCached(Path.Combine(FindRepositoryRoot(), "targets"), out TargetCatalog? catalog, out string? error), error);
		Assert.True(catalog!.TryGetTarget(targetName, out TargetDefinition? target));
		return BuildArtifactLayout.GetArtifactDirectoryName(target!, linkKind, "DEBUG");
	}

	static string NativeArtifactPathForTarget(string targetName, NativeBuildKind buildKind, string outputDirectory, string projectName)
	{
		Assert.True(TargetCatalog.TryLoadCached(Path.Combine(FindRepositoryRoot(), "targets"), out TargetCatalog? catalog, out string? error), error);
		Assert.True(catalog!.TryGetTarget(targetName, out TargetDefinition? target));
		return NativeBuildDriver.GetArtifactPath(new NativeBuildOptions
		{
			Target = target!,
			ProfileName = "DEBUG",
			BuildDirectory = Path.Combine(outputDirectory, "build"),
			OutputDirectory = outputDirectory,
			ProjectName = projectName,
			Kind = buildKind,
			SourceFiles = []
		});
	}

	static string ExecutableExtensionForHost()
	{
		return OperatingSystem.IsWindows() ? ".exe" : "";
	}

	static bool MsvcAvailable()
	{
		return OperatingSystem.IsWindows() && MsvcEnvironment.TargetArchitecture is "x64" or "x86" && ToolAvailable("cl") && ToolAvailable("lib");
	}

	static bool MsvcBuildToolsInstalled()
	{
		if (!OperatingSystem.IsWindows())
			return false;
		if (MsvcAvailable())
			return true;
		string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		if (string.IsNullOrWhiteSpace(programFilesX86))
			return false;
		string vsWhere = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
		if (!File.Exists(vsWhere))
			return false;
		ProcessResult result = RunProcess(vsWhere, ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"], FindRepositoryRoot());
		return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
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

	static bool ClangWasiAvailable()
	{
		return ClangWasiAvailability.Value;
	}

	static bool ProbeClangWasiAvailable()
	{
		string clang = "/opt/wasi-sdk/bin/clang";
		if (!File.Exists(clang))
			return false;

		string root = TempPath("clang-wasi-smoke");
		Directory.CreateDirectory(root);
		string source = Path.Combine(root, "main.c");
		string output = Path.Combine(root, "main.wasm");
		File.WriteAllText(source, "int main(void) { return 0; }\n");
		ProcessResult result = RunProcess(clang, ["--target=wasm32-wasi", source, "-o", output], root);
		return result.ExitCode == 0 && File.Exists(output);
	}

	static bool EmscriptenAvailable()
	{
		return EmscriptenAvailability.Value;
	}

	static bool ProbeEmscriptenAvailable()
	{
		string emcc = "/opt/emsdk/upstream/emscripten/emcc";
		if (!File.Exists(emcc))
			return false;

		string root = TempPath("emcc-smoke");
		Directory.CreateDirectory(root);
		string source = Path.Combine(root, "main.c");
		string output = Path.Combine(root, "main.js");
		File.WriteAllText(source, "int main(void) { return 0; }\n");
		ProcessResult result = RunProcess(emcc, [source, "-o", output], root);
		return result.ExitCode == 0 && File.Exists(output) && File.Exists(Path.ChangeExtension(output, ".wasm"));
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

	static string RelativeSourcePath(string path)
	{
		return Path.GetRelativePath(FindRepositoryRoot(), Path.GetFullPath(path)).Replace('\\', '/');
	}

	static string TestManifestPath(string outDir, string projectName)
	{
		return Path.Combine(outDir, ArtifactDirectoryForHost(null), projectName + ".camp-test-manifest.json");
	}

	static string TestResultsPath(string outDir, string projectName)
	{
		return Path.Combine(outDir, ArtifactDirectoryForHost(null), projectName + ".camp-test-results.json");
	}

	static string CoverageMapPath(string outDir, string projectName)
	{
		return Path.Combine(outDir, ArtifactDirectoryForHost(null), projectName + ".camp-coverage-map.csv");
	}

	static string CoverageResultsPath(string outDir, string projectName)
	{
		return Path.Combine(outDir, ArtifactDirectoryForHost(null), projectName + ".camp-coverage-results.json");
	}

	static int FindLine(string path, string text)
	{
		string[] lines = File.ReadAllLines(path);
		for (int i = 0; i < lines.Length; i++)
			if (lines[i].Contains(text, StringComparison.Ordinal))
				return i + 1;
		throw new InvalidOperationException($"Line containing '{text}' was not found in '{path}'.");
	}

	static void AssertCoverageMapContainsLine(string map, int line)
	{
		Assert.Contains(map.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'), row =>
		{
			string[] parts = row.Split(',');
			return parts.Length == 6
				&& parts[0] == "c"
				&& parts[2] == "l"
				&& int.TryParse(parts[4], out int rowLine)
				&& rowLine == line;
		});
	}

	static string TempPath(string name) => Path.Combine(FindRepositoryRoot(), "tmp", "cli-tests", name);

	static void ResetDirectory(string path)
	{
		if (Directory.Exists(path))
			Directory.Delete(path, recursive: true);
		Directory.CreateDirectory(path);
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

	static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

	static string EscapeCString(string text)
	{
		return text
			.Replace("\\", "\\\\", StringComparison.Ordinal)
			.Replace("\"", "\\\"", StringComparison.Ordinal);
	}

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
