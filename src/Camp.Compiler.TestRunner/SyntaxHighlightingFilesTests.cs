using System;
using System.IO;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class SyntaxHighlightingFilesTests
{
	[Fact]
	public void Syntax_files_highlight_test_attributes_and_support_calls_without_reserved_keywords()
	{
		string root = FindRepositoryRoot();
		string micro = File.ReadAllText(Path.Combine(root, "extras", "camp.yaml"));
		string sublime = File.ReadAllText(Path.Combine(root, "extras", "Camp.sublime-syntax"));
		string vscode = File.ReadAllText(Path.Combine(root, "extras", "vscode-camp", "syntaxes", "camp.tmLanguage.json"));

		Assert.Contains("special.metadata.attribute.test", micro, StringComparison.Ordinal);
		Assert.Contains("@(test|testonly|skip", micro, StringComparison.Ordinal);
		Assert.Contains("assert|fail", micro, StringComparison.Ordinal);
		Assert.Contains("@(?:test|testonly|skip)", sublime, StringComparison.Ordinal);
		Assert.Contains("storage.type.annotation.camp", sublime, StringComparison.Ordinal);
		Assert.Contains("storage.type.annotation.test.camp", sublime, StringComparison.Ordinal);
		Assert.Contains("test_support_functions", sublime, StringComparison.Ordinal);
		Assert.Contains("@(?:test|testonly|skip)", vscode, StringComparison.Ordinal);
		Assert.Contains("storage.type.annotation.camp", vscode, StringComparison.Ordinal);
		Assert.Contains("storage.type.annotation.test.camp", vscode, StringComparison.Ordinal);
		Assert.Contains("support.function.test.camp", vscode, StringComparison.Ordinal);
		Assert.DoesNotContain("keyword.declaration.camp\",\n          \"match\": \"\\\\b(?:caller|sourceof)", vscode, StringComparison.Ordinal);
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
