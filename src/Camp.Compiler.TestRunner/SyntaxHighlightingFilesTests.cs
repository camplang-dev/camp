using System;
using System.IO;
using System.IO.Compression;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class SyntaxHighlightingFilesTests
{
	[Fact]
	public void Syntax_files_highlight_test_attributes_and_support_calls_without_reserved_keywords()
	{
		string root = FindRepositoryRoot();
		string micro = File.ReadAllText(Path.Combine(root, "extras", "editors", "micro", "camp.yaml"));
		string sublime = File.ReadAllText(Path.Combine(root, "extras", "editors", "sublime", "Camp.sublime-syntax"));
		string vscode = File.ReadAllText(Path.Combine(root, "extras", "vscode-camp", "syntaxes", "camp.tmLanguage.json"));
		string vscodeVsix = Path.Combine(root, "extras", "editors", "vscode", "vscode-camp.vsix");
		string vimSyntax = File.ReadAllText(Path.Combine(root, "extras", "editors", "vim", "pack", "camp", "start", "camp", "syntax", "camp.vim"));
		string vimDetect = File.ReadAllText(Path.Combine(root, "extras", "editors", "vim", "pack", "camp", "start", "camp", "ftdetect", "camp.vim"));

		Assert.Contains("special.metadata.attribute.test", micro, StringComparison.Ordinal);
		Assert.DoesNotContain("(?", micro, StringComparison.Ordinal);
		Assert.Contains("@(test|testonly|skip", micro, StringComparison.Ordinal);
		Assert.Contains("assert|fail", micro, StringComparison.Ordinal);
		Assert.Contains("@(?:test|testonly|skip)", sublime, StringComparison.Ordinal);
		Assert.Contains("storage.type.annotation.camp", sublime, StringComparison.Ordinal);
		Assert.Contains("storage.type.annotation.test.camp", sublime, StringComparison.Ordinal);
		Assert.Contains("test_support_functions", sublime, StringComparison.Ordinal);
		Assert.Contains("@(?:test|testonly|skip)", vscode, StringComparison.Ordinal);
		Assert.Contains("string.quoted.double.interpolated.camp", vscode, StringComparison.Ordinal);
		Assert.Contains("storage.type.annotation.camp", vscode, StringComparison.Ordinal);
		Assert.Contains("storage.type.annotation.test.camp", vscode, StringComparison.Ordinal);
		Assert.Contains("support.function.test.camp", vscode, StringComparison.Ordinal);
		Assert.DoesNotContain("keyword.declaration.camp\",\n          \"match\": \"\\\\b(?:caller|sourceof)", vscode, StringComparison.Ordinal);
		AssertVsixContains(vscodeVsix, "extension/syntaxes/camp.tmLanguage.json", "string.quoted.double.interpolated.camp");
		Assert.Contains("syntax keyword campTestAttribute test testonly skip", vimSyntax, StringComparison.Ordinal);
		Assert.Contains("syntax region campInterpolatedString", vimSyntax, StringComparison.Ordinal);
		Assert.Contains("*.camp", vimDetect, StringComparison.Ordinal);
		Assert.Contains("*.campbuild", vimDetect, StringComparison.Ordinal);
	}

	static void AssertVsixContains(string vsixPath, string entryName, string expectedText)
	{
		using ZipArchive archive = ZipFile.OpenRead(vsixPath);
		ZipArchiveEntry entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"VSIX entry '{entryName}' was not found.");
		using StreamReader reader = new(entry.Open());
		string text = reader.ReadToEnd();
		Assert.Contains(expectedText, text, StringComparison.Ordinal);
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
		throw new InvalidOperationException("Could not find repository root.");
	}
}
