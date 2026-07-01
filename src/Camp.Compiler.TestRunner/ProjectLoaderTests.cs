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
			#build --include api/*.camp

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
		Assert.True(result.Request.NoStdLib);
		Assert.Null(result.Request.BuildKind);
		Assert.Single(result.Request.Files);
		Assert.Single(result.Request.IncludeFiles);
		Assert.EndsWith(Path.Combine("src", "main.camp"), result.Request.Files[0], StringComparison.Ordinal);
		Assert.EndsWith(Path.Combine("api", "lib.camp"), result.Request.IncludeFiles[0], StringComparison.Ordinal);
	}

	[Fact]
	public void Project_loader_expands_includes_and_excludes()
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
