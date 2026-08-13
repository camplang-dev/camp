using System;
using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ProjectLoaderTests
{
	[Fact]
	public void Project_loader_reads_campbuild_and_build_pragmas()
	{
		string root = CreateTempDirectory("project-loader-pragmas");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		string main = Path.Combine(sourceDirectory, "main.camp");
		File.WriteAllText(main, """
			#build --define LOCAL_FLAG
			#build --api api/*.camp
			#build --debug-info

			export int main()
			{
				return 0;
			}
			""");
		string apiDirectory = Path.Combine(root, "api");
		Directory.CreateDirectory(apiDirectory);
		File.WriteAllText(Path.Combine(apiDirectory, "lib.camp"), "export extern void helper();");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			src/*.camp
			""");

		CampProjectLoadResult result = CampProjectLoader.LoadBuildFile(buildFile, CreateEnvironment(root));

		Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
		Assert.Contains("LOCAL_FLAG", result.Request.Defines);
		Assert.True(result.Request.EmitDebugInfo);
		Assert.True(result.Request.NoStdLib);
		Assert.Null(result.Request.BuildKind);
		Assert.Single(result.Request.Files);
		Assert.Single(result.Request.ApiFiles);
		Assert.EndsWith(Path.Combine("src", "main.camp"), result.Request.Files[0], StringComparison.Ordinal);
		Assert.EndsWith(Path.Combine("api", "lib.camp"), result.Request.ApiFiles[0], StringComparison.Ordinal);
	}

	[Fact]
	public void Project_loader_reads_sourcefile_path_options()
	{
		string workspace = CreateTempDirectory("project-loader-sourcefile-paths");
		string app = Path.Combine(workspace, "app");
		string sourceDirectory = Path.Combine(app, "src");
		string generatedDirectory = Path.Combine(app, "generated");
		Directory.CreateDirectory(sourceDirectory);
		Directory.CreateDirectory(generatedDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), "export void main() {}");
		string buildFile = Path.Combine(app, "app.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--sourcefile-paths absolute
			--sourcefile-root src
			--sourcefile-root generated
			src/*.camp
			""");

		foreach (CampProjectCommandKind command in new[] { CampProjectCommandKind.Build, CampProjectCommandKind.Run, CampProjectCommandKind.Test, CampProjectCommandKind.Cover })
		{
			CampProjectLoadResult result = CampProjectLoader.LoadBuildFile(buildFile, CreateEnvironment(workspace), command);

			Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
			Assert.Equal(SourcefilePathMode.Absolute, result.Request.SourcefilePathMode);
			Assert.Equal(Path.GetFullPath(app), Path.GetFullPath(result.Request.SourcefileDefaultRoot!));
			Assert.Equal([
				Path.GetFullPath(sourceDirectory),
				Path.GetFullPath(generatedDirectory)
			], result.Request.SourcefileRoots.Select(path => Path.GetFullPath(path)).ToArray());
		}
	}

	[Fact]
	public void Project_loader_sets_test_and_cover_modes()
	{
		string root = CreateTempDirectory("project-loader-test-modes");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), "export void main() {}");
		string buildFile = Path.Combine(root, "app.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			src/*.camp
			""");

		CampProjectLoadResult test = CampProjectLoader.LoadBuildFile(buildFile, CreateEnvironment(root), CampProjectCommandKind.Test);
		CampProjectLoadResult cover = CampProjectLoader.LoadBuildFile(buildFile, CreateEnvironment(root), CampProjectCommandKind.Cover);
		CampProjectLoadResult build = CampProjectLoader.LoadBuildFile(buildFile, CreateEnvironment(root), CampProjectCommandKind.Build);

		Assert.True(test.Success, string.Join(Environment.NewLine, test.Diagnostics));
		Assert.Equal(CompilerCommandMode.Test, test.Request.CommandMode);
		Assert.Equal(DeclarationParticipationMode.TestModule, test.Request.DeclarationParticipationMode);
		Assert.Equal(CoverageInstrumentationMode.Disabled, test.Request.CoverageInstrumentationMode);
		Assert.False(test.Request.InferWithinPolicyBuildKind);
		Assert.Null(test.Request.WithinPolicyBuildKind);

		Assert.True(cover.Success, string.Join(Environment.NewLine, cover.Diagnostics));
		Assert.Equal(CompilerCommandMode.Cover, cover.Request.CommandMode);
		Assert.Equal(DeclarationParticipationMode.TestModule, cover.Request.DeclarationParticipationMode);
		Assert.Equal(CoverageInstrumentationMode.ProductionSubject, cover.Request.CoverageInstrumentationMode);
		Assert.False(cover.Request.InferWithinPolicyBuildKind);
		Assert.Null(cover.Request.WithinPolicyBuildKind);

		Assert.True(build.Success, string.Join(Environment.NewLine, build.Diagnostics));
		Assert.Equal(CompilerCommandMode.Build, build.Request.CommandMode);
		Assert.Equal(DeclarationParticipationMode.Production, build.Request.DeclarationParticipationMode);
		Assert.Equal(CoverageInstrumentationMode.Disabled, build.Request.CoverageInstrumentationMode);
	}

	[Fact]
	public void Project_loader_sets_within_policy_inference_for_test_cover_and_language_service()
	{
		string root = CreateTempDirectory("project-loader-within-policy");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "library.camp"), "void helper() {}");
		string inferredBuildFile = Path.Combine(root, "library.campbuild");
		File.WriteAllText(inferredBuildFile, """
			--nostdlib
			src/*.camp
			""");
		string staticBuildFile = Path.Combine(root, "static-library.campbuild");
		File.WriteAllText(staticBuildFile, """
			--nostdlib
			--artifact static
			src/*.camp
			""");
		string execBuildFile = Path.Combine(root, "exec-app.campbuild");
		File.WriteAllText(execBuildFile, """
			--nostdlib
			--artifact exec
			src/*.camp
			""");

		CampProjectLoadResult inferredTest = CampProjectLoader.LoadBuildFile(inferredBuildFile, CreateEnvironment(root), CampProjectCommandKind.Test);
		CampProjectLoadResult inferredCover = CampProjectLoader.LoadBuildFile(inferredBuildFile, CreateEnvironment(root), CampProjectCommandKind.Cover);
		CampProjectLoadResult inferredLanguageService = CampProjectLoader.LoadBuildFile(inferredBuildFile, CreateEnvironment(root), CampProjectCommandKind.LanguageService);
		CampProjectLoadResult staticTest = CampProjectLoader.LoadBuildFile(staticBuildFile, CreateEnvironment(root), CampProjectCommandKind.Test);
		CampProjectLoadResult execTest = CampProjectLoader.LoadBuildFile(execBuildFile, CreateEnvironment(root), CampProjectCommandKind.Test);

		Assert.True(inferredTest.Success, string.Join(Environment.NewLine, inferredTest.Diagnostics));
		Assert.Null(inferredTest.Request.BuildKind);
		Assert.True(inferredTest.Request.InferWithinPolicyBuildKind);
		Assert.Null(inferredTest.Request.WithinPolicyBuildKind);

		Assert.True(inferredCover.Success, string.Join(Environment.NewLine, inferredCover.Diagnostics));
		Assert.True(inferredCover.Request.InferWithinPolicyBuildKind);
		Assert.Null(inferredCover.Request.WithinPolicyBuildKind);

		Assert.True(inferredLanguageService.Success, string.Join(Environment.NewLine, inferredLanguageService.Diagnostics));
		Assert.True(inferredLanguageService.Request.InferWithinPolicyBuildKind);
		Assert.Null(inferredLanguageService.Request.WithinPolicyBuildKind);

		Assert.True(staticTest.Success, string.Join(Environment.NewLine, staticTest.Diagnostics));
		Assert.False(staticTest.Request.InferWithinPolicyBuildKind);
		Assert.Equal(NativeBuildKind.Static, staticTest.Request.WithinPolicyBuildKind);

		Assert.True(execTest.Success, string.Join(Environment.NewLine, execTest.Diagnostics));
		Assert.False(execTest.Request.InferWithinPolicyBuildKind);
		Assert.Equal(NativeBuildKind.Exec, execTest.Request.WithinPolicyBuildKind);
	}

	[Fact]
	public void Project_loader_reads_test_discovery_options()
	{
		string root = CreateTempDirectory("project-loader-test-options");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), "export void main() {}");
		string buildFile = Path.Combine(root, "app.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--list
			--ignore-leaks
			--filter MathTests::*
			--filter parse^alue
			--test-output-dir results
			--test-result-format text,json
			src/*.camp
			""");

		CampProjectLoadResult test = CampProjectLoader.LoadBuildFile(buildFile, CreateEnvironment(root), CampProjectCommandKind.Test);
		CampProjectLoadResult build = CampProjectLoader.LoadBuildFile(buildFile, CreateEnvironment(root), CampProjectCommandKind.Build);

		Assert.True(test.Success, string.Join(Environment.NewLine, test.Diagnostics));
		Assert.True(test.Request.ListTests);
		Assert.True(test.Request.IgnoreLeaks);
		Assert.Equal(["MathTests::*", "parse^alue"], test.Request.TestFilters);
		Assert.Equal(Path.GetFullPath(Path.Combine(root, "results")), Path.GetFullPath(test.Request.TestOutputDir!));
		Assert.Equal("text,json", test.Request.TestResultFormat);

		Assert.False(build.Success);
		Assert.Contains(build.Diagnostics, static diagnostic => diagnostic.Contains("--list can only be used with test or cover", StringComparison.Ordinal));
		Assert.Contains(build.Diagnostics, static diagnostic => diagnostic.Contains("--ignore-leaks can only be used with test or cover", StringComparison.Ordinal));
		Assert.Contains(build.Diagnostics, static diagnostic => diagnostic.Contains("--filter can only be used with test or cover", StringComparison.Ordinal));
	}

	[Fact]
	public void Project_loader_reads_coverage_options()
	{
		string root = CreateTempDirectory("project-loader-coverage-options");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), "export void main() {}");
		string buildFile = Path.Combine(root, "app.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--coverage-format json,lcov
			--coverage-output-dir coverage-output
			--coverage-subject self
			src/*.camp
			""");

		CampProjectLoadResult cover = CampProjectLoader.LoadBuildFile(buildFile, CreateEnvironment(root), CampProjectCommandKind.Cover);
		CampProjectLoadResult build = CampProjectLoader.LoadBuildFile(buildFile, CreateEnvironment(root), CampProjectCommandKind.Build);

		Assert.True(cover.Success, string.Join(Environment.NewLine, cover.Diagnostics));
		Assert.Equal("json,lcov", cover.Request.CoverageFormat);
		Assert.Equal(Path.GetFullPath(Path.Combine(root, "coverage-output")), Path.GetFullPath(cover.Request.CoverageOutputDir!));
		Assert.Equal(["self"], cover.Request.CoverageSubjects);

		Assert.False(build.Success);
		Assert.Contains(build.Diagnostics, static diagnostic => diagnostic.Contains("can only be used with cover", StringComparison.Ordinal));
	}

	[Fact]
	public void Project_loader_expands_sources_and_excludes()
	{
		string root = CreateTempDirectory("project-loader-globs");
		Directory.CreateDirectory(Path.Combine(root, "src"));
		File.WriteAllText(Path.Combine(root, "src", "a.camp"), "export void a() {}");
		File.WriteAllText(Path.Combine(root, "src", "b.camp"), "export void b() {}");
		File.WriteAllText(Path.Combine(root, "src", "skip.camp"), "export void skip() {}");

		CampProjectLoadResult result = CampProjectLoader.Load([
			"--nostdlib",
			"--exclude",
			"src/skip.camp",
			"src/*.camp"
		], CreateEnvironment(root));

		Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
		Assert.Equal(2, result.Request.Files.Count);
		Assert.DoesNotContain(result.Request.Files, file => file.EndsWith("skip.camp", StringComparison.Ordinal));
	}

	[Fact]
	public void Project_loader_resolves_project_references_read_only()
	{
		string root = CreateTempDirectory("project-loader-project-refs");
		string library = Path.Combine(root, "lib");
		string app = Path.Combine(root, "app");
		Directory.CreateDirectory(library);
		Directory.CreateDirectory(app);
		File.WriteAllText(Path.Combine(library, "lib.camp"), "export void helper() {}");
		string libraryBuild = Path.Combine(library, "lib.campbuild");
		File.WriteAllText(libraryBuild, """
			--nostdlib
			--artifact static
			lib.camp
			""");
		File.WriteAllText(Path.Combine(app, "main.camp"), "export void main() {}");
		string appBuild = Path.Combine(app, "app.campbuild");
		File.WriteAllText(appBuild, """
			--nostdlib
			--project-reference ../lib
			main.camp
			""");

		CampProjectLoadResult result = CampProjectLoader.LoadBuildFile(appBuild, CreateEnvironment(app));

		Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
		string reference = Assert.Single(result.ProjectReferences);
		Assert.Equal(Path.GetFullPath(libraryBuild), reference);
		Assert.Empty(result.ProjectReferenceApiHeaders);
	}

	[Fact]
	public void Project_loader_accepts_dependency_link_kind_suffixes_and_only_artifacts()
	{
		string root = CreateTempDirectory("project-loader-dependency-link-kind");
		Directory.CreateDirectory(Path.Combine(root, "lib"));
		File.WriteAllText(Path.Combine(root, "lib", "lib.camp"), "export void helper() {}");
		string libraryBuild = Path.Combine(root, "lib", "lib.campbuild");
		File.WriteAllText(libraryBuild, """
			--nostdlib
			--artifact only-static
			lib.camp
			""");
		File.WriteAllText(Path.Combine(root, "main.camp"), "export void main() {}");

		CampProjectLoadResult result = CampProjectLoader.Load([
			"--nostdlib",
			"--artifact",
			"only-shared",
			"--use",
			"demo@1.2.3:api",
			"--project-reference",
			"lib:static",
			"main.camp"
		], CreateEnvironment(root));

		Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
		Assert.Equal(NativeBuildKind.Shared, result.Request.BuildKind);
		Assert.Equal("demo@1.2.3:api", Assert.Single(result.Request.UsePackages));
		Assert.Equal(Path.GetFullPath(libraryBuild), Assert.Single(result.ProjectReferences));
	}

	[Fact]
	public void Project_loader_finds_shared_project_reference_api_header()
	{
		string root = CreateTempDirectory("project-loader-shared-ref-api");
		string library = Path.Combine(root, "lib");
		string app = Path.Combine(root, "app");
		Directory.CreateDirectory(library);
		Directory.CreateDirectory(app);
		File.WriteAllText(Path.Combine(library, "lib.camp"), "export void helper() {}");
		string libraryBuild = Path.Combine(library, "lib.campbuild");
		File.WriteAllText(libraryBuild, """
			--nostdlib
			--name lib
			lib.camp
			""");
		string sharedApiDirectory = Path.Combine(library, "bin", "clang-macos-x64_shared_DEBUG");
		Directory.CreateDirectory(sharedApiDirectory);
		string sharedApi = Path.Combine(sharedApiDirectory, "lib_api.camp");
		File.WriteAllText(sharedApi, "export extern void helper();");
		File.WriteAllText(Path.Combine(app, "main.camp"), "export void main() {}");
		string appBuild = Path.Combine(app, "app.campbuild");
		File.WriteAllText(appBuild, """
			--nostdlib
			--target clang-macos-x64
			--project-reference ../lib
			main.camp
			""");

		CampProjectLoadResult result = CampProjectLoader.LoadBuildFile(appBuild, CreateEnvironment(app), CampProjectCommandKind.LanguageService);

		Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
		Assert.Equal(Path.GetFullPath(sharedApi), Assert.Single(result.ProjectReferenceApiHeaders));
		Assert.Equal(Path.GetFullPath(sharedApi), Assert.Single(result.Request.SharedLibraryApiHeaders));
	}

	[Fact]
	public void Project_loader_reports_malformed_dependency_kind_suffixes()
	{
		string root = CreateTempDirectory("project-loader-bad-dependency-kind");
		File.WriteAllText(Path.Combine(root, "main.camp"), "export void main() {}");

		CampProjectLoadResult result = CampProjectLoader.Load([
			"--nostdlib",
			"--use",
			"demo@1.2.3:dynamic",
			"--project-reference",
			"lib:dynamic",
			"main.camp"
		], CreateEnvironment(root));

		Assert.False(result.Success);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Package dependency kind ':dynamic' is not valid. Expected :api, :static, or :shared.", StringComparison.Ordinal));
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Project reference dependency kind ':dynamic' is not valid. Expected :static or :shared.", StringComparison.Ordinal));
	}

	[Fact]
	public void Project_loader_reports_old_memory_model_spelling()
	{
		string root = CreateTempDirectory("project-loader-old-memory-model");
		File.WriteAllText(Path.Combine(root, "main.camp"), "export void main() {}");

		CampProjectLoadResult result = CampProjectLoader.Load([
			"--nostdlib",
			"--memory-model",
			"large",
			"main.camp"
		], CreateEnvironment(root));

		Assert.False(result.Success);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("--memory-model has been replaced by --variant", StringComparison.Ordinal));
	}

	[Fact]
	public void Project_loader_reports_missing_project_reference()
	{
		string root = CreateTempDirectory("project-loader-missing-ref");
		File.WriteAllText(Path.Combine(root, "main.camp"), "export void main() {}");

		CampProjectLoadResult result = CampProjectLoader.Load([
			"--nostdlib",
			"--project-reference",
			"missing",
			"main.camp"
		], CreateEnvironment(root));

		Assert.False(result.Success);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Project reference 'missing' could not be found.", StringComparison.Ordinal));
	}

	static CampProjectEnvironment CreateEnvironment(string workingDirectory)
	{
		return CampProjectEnvironment.Create(workingDirectory, AppContext.BaseDirectory);
	}

	static string CreateTempDirectory(string name)
	{
		string directory = Path.Combine(Path.GetTempPath(), "camp-tests", name + "-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return directory;
	}
}
