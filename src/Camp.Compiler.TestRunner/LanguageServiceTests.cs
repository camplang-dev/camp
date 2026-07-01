using System;
using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class LanguageServiceTests
{
	[Fact]
	public void Analysis_uses_in_memory_overlay_instead_of_disk_text()
	{
		string root = CreateTempDirectory("language-service-overlay");
		string source = Path.Combine(root, "main.camp");
		File.WriteAllText(source, """
			export int main()
			{
				return 0;
			}
			""");
		CompilerRequest request = Request(root, source);

		CampAnalysisSnapshot broken = CampLanguageService.Analyze(request, [
			new CampSourceOverlay(source, """
				export int main()
				{
					return ;
				}
				""", Version: 1)
		]);
		CampAnalysisSnapshot fixedAgain = CampLanguageService.Analyze(request, [
			new CampSourceOverlay(source, """
				export int main()
				{
					return 1;
				}
				""", Version: 2)
		]);

		Assert.False(broken.Success);
		Assert.Contains(broken.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot", StringComparison.OrdinalIgnoreCase));
		Assert.True(fixedAgain.Success, string.Join(Environment.NewLine, fixedAgain.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		Assert.Contains("return 0", File.ReadAllText(source), StringComparison.Ordinal);
	}

	[Fact]
	public void Analysis_reports_zero_based_diagnostic_ranges()
	{
		string root = CreateTempDirectory("language-service-ranges");
		string source = Path.Combine(root, "main.camp");
		File.WriteAllText(source, """
			export int main()
			{
				return 0;
			}
			""");
		CompilerRequest request = Request(root, source);

		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(request, [
			new CampSourceOverlay(source, """
				export int main()
				{
					auto result = missing;
					return result;
				}
				""")
		]);

		CampSourceDiagnostic diagnostic = Assert.Single(snapshot.Diagnostics, static diagnostic => diagnostic.Message.Contains("missing", StringComparison.Ordinal));
		Assert.Equal(Path.GetFullPath(source), diagnostic.Path);
		Assert.NotNull(diagnostic.Range);
		Assert.Equal(2, diagnostic.Range!.Start.Line);
		Assert.True(diagnostic.Range.Start.Character >= 15);
	}

	static CompilerRequest Request(string workingDirectory, string source)
	{
		CompilerRequest request = new()
		{
			RuntimeRoot = AppContext.BaseDirectory,
			WorkingDirectory = workingDirectory,
			TargetName = "clang-macos-x64",
			NoStdLib = true
		};
		request.Files.Add(Path.GetRelativePath(workingDirectory, source));
		return request;
	}

	static string CreateTempDirectory(string name)
	{
		string directory = Path.Combine(Path.GetTempPath(), "camp-tests", name + "-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return directory;
	}
}
