using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed class SourcefilePathMapper(SourcefilePathMode mode, string defaultRoot, IEnumerable<string> sourcefileRoots)
{
	readonly List<PathParts> roots = sourcefileRoots
		.Where(static root => !string.IsNullOrWhiteSpace(root))
		.Select(static root => PathParts.Parse(root))
		.ToList();
	readonly PathParts defaultRoot = PathParts.Parse(defaultRoot);
	readonly Dictionary<string, string> emitted = new(StringComparer.Ordinal);

	public SourcefilePathMapResult Map(string sourcePath)
	{
		PathParts source = PathParts.Parse(sourcePath);
		if (mode == SourcefilePathMode.Absolute)
			return SourcefilePathMapResult.Mapped(source.FormatAbsolute());

		IReadOnlyList<PathParts> candidates = roots.Count == 0 ? [defaultRoot] : roots;
		PathParts? root = null;
		foreach (PathParts candidate in candidates.OrderByDescending(static candidate => candidate.Segments.Count))
		{
			if (candidate.Contains(source))
			{
				root = candidate;
				break;
			}
		}
		if (root is null)
			return SourcefilePathMapResult.Failure($"Source file '{source.FormatAbsolute()}' is outside every --sourcefile-root.");

		string relative = root.Value.RelativePathTo(source);
		if (emitted.TryGetValue(relative, out string? previous) && !PathParts.SamePath(previous, source.FormatAbsolute()))
			return SourcefilePathMapResult.Failure($"Sourcefile path '{relative}' is produced by both '{previous}' and '{source.FormatAbsolute()}'.");
		emitted[relative] = source.FormatAbsolute();
		return SourcefilePathMapResult.Mapped(relative);
	}

	readonly record struct PathParts(string Root, List<string> Segments)
	{
		public static PathParts Parse(string path)
		{
			string text = path.Replace('\\', '/');
			string root = "";
			if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':')
			{
				root = char.ToUpperInvariant(text[0]) + ":";
				text = text.Length > 2 && text[2] == '/' ? text[3..] : text[2..];
			}
			else if (text.StartsWith("/", StringComparison.Ordinal))
			{
				root = "/";
				text = text.TrimStart('/');
			}

			List<string> segments = [];
			foreach (string raw in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
			{
				if (raw == ".")
					continue;
				if (raw == ".." && segments.Count > 0 && segments[^1] != "..")
				{
					segments.RemoveAt(segments.Count - 1);
					continue;
				}
				segments.Add(raw);
			}
			return new PathParts(root, segments);
		}

		public bool Contains(PathParts source)
		{
			if (!string.Equals(Root, source.Root, StringComparison.OrdinalIgnoreCase) || Segments.Count > source.Segments.Count)
				return false;
			for (int i = 0; i < Segments.Count; i++)
				if (!string.Equals(Segments[i], source.Segments[i], StringComparison.OrdinalIgnoreCase))
					return false;
			return true;
		}

		public string RelativePathTo(PathParts source)
		{
			if (Segments.Count == source.Segments.Count)
				return ".";
			return string.Join('/', source.Segments.Skip(Segments.Count));
		}

		public string FormatAbsolute()
		{
			string tail = string.Join('/', Segments);
			return Root switch
			{
				"" => tail,
				"/" => "/" + tail,
				_ when tail.Length == 0 => Root + "/",
				_ => Root + "/" + tail
			};
		}

		public static bool SamePath(string left, string right)
		{
			return string.Equals(Parse(left).FormatAbsolute(), Parse(right).FormatAbsolute(), StringComparison.OrdinalIgnoreCase);
		}
	}
}

public readonly record struct SourcefilePathMapResult(bool Success, string? Value, string? Diagnostic)
{
	public static SourcefilePathMapResult Mapped(string value) => new(true, value, null);
	public static SourcefilePathMapResult Failure(string diagnostic) => new(false, null, diagnostic);
}
