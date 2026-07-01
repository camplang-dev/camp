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

	[Fact]
	public void Symbol_query_finds_local_parameter_and_function_definitions()
	{
		string root = CreateTempDirectory("language-service-symbols");
		string source = Path.Combine(root, "main.camp");
		string text = """
			/// Adds one to a value.
			int helper(int value)
			{
				auto local = value;
				return local + 1;
			}

			export int main()
			{
				return helper(41);
			}
			""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		CampSymbolLocation? parameterDefinition = symbols.GetDefinition(source, PositionOf(text, "value;"));
		CampSymbolLocation? localDefinition = symbols.GetDefinition(source, PositionOf(text, "local +"));
		CampSymbolLocation? helperDefinition = symbols.GetDefinition(source, PositionOf(text, "helper(41"));
		CampHover? hover = symbols.GetHover(source, PositionOf(text, "helper(41"));

		Assert.NotNull(parameterDefinition);
		Assert.Equal(1, parameterDefinition!.Range.Start.Line);
		Assert.NotNull(localDefinition);
		Assert.Equal(3, localDefinition!.Range.Start.Line);
		Assert.NotNull(helperDefinition);
		Assert.Equal(1, helperDefinition!.Range.Start.Line);
		Assert.NotNull(hover);
		Assert.Contains("Adds one to a value.", hover!.Markdown, StringComparison.Ordinal);
		Assert.Contains("int helper(int value)", hover.Markdown, StringComparison.Ordinal);
	}

	[Fact]
	public void Symbol_query_finds_member_definitions()
	{
		string root = CreateTempDirectory("language-service-members");
		string source = Path.Combine(root, "main.camp");
		string text = """
			struct Counter
			{
				int value;
				int getValue() => this.value;
			}

			export int main()
			{
				Counter counter = default;
				return counter.value;
			}
			""";
		File.WriteAllText(source, text);
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(Request(root, source));
		Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(static diagnostic => diagnostic.Message)));
		CampSymbolQueryService symbols = new(snapshot);

		CampSymbolLocation? classDefinition = symbols.GetDefinition(source, PositionOf(text, "Counter\n"));
		CampSymbolLocation? fieldDefinition = symbols.GetDefinition(source, PositionOf(text, "value;"));

		Assert.NotNull(classDefinition);
		Assert.Equal(0, classDefinition!.Range.Start.Line);
		Assert.NotNull(fieldDefinition);
		Assert.Equal(2, fieldDefinition!.Range.Start.Line);
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

	static CampTextPosition PositionOf(string text, string marker)
	{
		int index = text.IndexOf(marker, StringComparison.Ordinal);
		if (index < 0)
			throw new InvalidOperationException($"Marker '{marker}' was not found.");
		int line = 0;
		int character = 0;
		for (int i = 0; i < index; i++)
		{
			if (text[i] == '\n')
			{
				line++;
				character = 0;
			}
			else
				character++;
		}
		return new CampTextPosition(line, character);
	}

	static string CreateTempDirectory(string name)
	{
		string directory = Path.Combine(Path.GetTempPath(), "camp-tests", name + "-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return directory;
	}
}
