using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Camp.Compiler;

public static class CampStandardTestSupport
{
	public const string SourcePath = "$camp_test_support.camp";

	public static void AddTo(Compilation compilation)
	{
		ArgumentNullException.ThrowIfNull(compilation);
		string? namespaceName = GetPrimarySourceNamespace(compilation.Files.Where(static file => !file.IsApiHeader));
		compilation.Files.Add(new SourceFile
		{
			Path = SourcePath,
			Text = BuildSource(namespaceName),
			WithinAllocationPolicyOverride = WithinAllocationPolicy.Implicit
		});
	}

	public static string BuildSource(string? namespaceName)
	{
		StringBuilder builder = new();
		if (!string.IsNullOrWhiteSpace(namespaceName))
		{
			builder.Append("namespace ");
			builder.Append(namespaceName);
			builder.AppendLine(";");
			builder.AppendLine();
		}
		builder.AppendLine("""
public struct Assertion
{
	escaped string message;
	escaped string sourcefile;
	uint sourceline;
}

extern void* __camp_test_malloc(nuint size);

public void assert(bool condition, escaped string message = sourceof(condition), escaped string sourcefile = caller(sourcefile), uint sourceline = caller(sourceline), thrown Assertion* assertion)
{
	if (!condition)
		fail(message, sourcefile, sourceline);
}

public void fail(escaped string message, escaped string sourcefile = caller(sourcefile), uint sourceline = caller(sourceline), thrown Assertion* assertion)
{
	Assertion* created = (Assertion*)__camp_test_malloc(sizeof(Assertion));
	if (created != null)
	{
		created.message = message;
		created.sourcefile = sourcefile;
		created.sourceline = sourceline;
	}
	throw created;
}
""");
		return builder.ToString();
	}

	static string? GetPrimarySourceNamespace(IEnumerable<SourceFile> files)
	{
		foreach (SourceFile file in files)
			if (TryReadFileNamespace(file.Text, out string? namespaceName))
				return namespaceName;
		return null;
	}

	static bool TryReadFileNamespace(string text, out string? namespaceName)
	{
		namespaceName = null;
		using StringReader reader = new(text);
		while (reader.ReadLine() is string line)
		{
			string trimmed = line.Trim();
			if (trimmed.Length == 0
				|| trimmed.StartsWith("//", StringComparison.Ordinal)
				|| trimmed.StartsWith("/*", StringComparison.Ordinal)
				|| trimmed.StartsWith("*", StringComparison.Ordinal)
				|| trimmed.StartsWith("*/", StringComparison.Ordinal)
				|| trimmed.StartsWith("#build", StringComparison.Ordinal)
				|| trimmed.StartsWith("#within", StringComparison.Ordinal))
				continue;
			if (!trimmed.StartsWith("namespace ", StringComparison.Ordinal) || !trimmed.EndsWith(";", StringComparison.Ordinal))
				return false;
			namespaceName = trimmed["namespace ".Length..^1].Trim();
			return namespaceName.Length > 0;
		}
		return false;
	}
}
