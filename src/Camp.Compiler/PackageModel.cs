using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Camp.Compiler;

public sealed record PackageDependencySpec(string Name, PackageVersionExpression? VersionExpression, PackageSelectedVersion? SelectedVersion, DependencyLinkKind? LinkKind)
{
	public bool HasExactSelectedVersion => SelectedVersion is not null;

	public static bool TryParse(string text, out PackageDependencySpec spec, out string? error)
	{
		spec = new PackageDependencySpec("", null, null, null);
		error = null;
		if (string.IsNullOrWhiteSpace(text))
		{
			error = "Package spec cannot be empty.";
			return false;
		}

		string value = text.Trim();
		DependencyLinkKind? linkKind = null;
		int colon = value.LastIndexOf(':');
		if (colon >= 0)
		{
			string suffix = value[(colon + 1)..];
			if (suffix.Equals("static", StringComparison.OrdinalIgnoreCase) || suffix.Equals("shared", StringComparison.OrdinalIgnoreCase) || suffix.Equals("api", StringComparison.OrdinalIgnoreCase))
			{
				linkKind = suffix.ToLowerInvariant() switch
				{
					"static" => DependencyLinkKind.Static,
					"shared" => DependencyLinkKind.Shared,
					"api" => DependencyLinkKind.Api,
					_ => null
				};
				value = value[..colon];
			}
			else if (!string.IsNullOrWhiteSpace(suffix))
			{
				error = $"Package dependency kind ':{suffix}' is not valid. Expected :api, :static, or :shared.";
				return false;
			}
		}

		if (value.Contains('@') && value.Contains('/'))
		{
			error = $"Package spec '{text}' may not contain both '@' and '/'.";
			return false;
		}

		string name;
		PackageVersionExpression? versionExpression = null;
		PackageSelectedVersion? selectedVersion = null;
		int selectedSeparator = value.IndexOf('/');
		int expressionSeparator = value.IndexOf('@');
		if (selectedSeparator >= 0)
		{
			name = value[..selectedSeparator];
			string versionText = value[(selectedSeparator + 1)..];
			if (!PackageSelectedVersion.TryParse(versionText, out selectedVersion, out error))
				return false;
		}
		else if (expressionSeparator >= 0)
		{
			name = value[..expressionSeparator];
			string expressionText = value[(expressionSeparator + 1)..];
			if (!PackageVersionExpression.TryParse(expressionText, out versionExpression, out error))
				return false;
		}
		else
		{
			name = value;
		}

		if (!IsValidPackageName(name))
		{
			error = $"Package name '{name}' is not valid.";
			return false;
		}

		spec = new PackageDependencySpec(name, versionExpression, selectedVersion, linkKind);
		return true;
	}

	public static PackageDependencySpec Parse(string text)
	{
		if (!TryParse(text, out PackageDependencySpec spec, out string? error))
			throw new FormatException(error);
		return spec;
	}

	public override string ToString()
	{
		string value = Name;
		if (SelectedVersion is not null)
			value += "/" + SelectedVersion;
		else if (VersionExpression is not null)
			value += "@" + VersionExpression;
		if (LinkKind is not null)
			value += ":" + LinkKind.ToString()!.ToLowerInvariant();
		return value;
	}

	static bool IsValidPackageName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return false;
		foreach (char ch in name)
			if (!(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'))
				return false;
		return true;
	}
}

public sealed record PackageVersionExpression(int? Major, int? Minor, int? Patch)
{
	public static bool TryParse(string text, out PackageVersionExpression? expression, out string? error)
	{
		expression = null;
		error = null;
		if (string.IsNullOrWhiteSpace(text))
		{
			error = "Package version expression cannot be empty.";
			return false;
		}
		string[] parts = text.Split('.');
		if (parts.Length is < 1 or > 3)
		{
			error = $"Package version expression '{text}' is not valid.";
			return false;
		}
		int? major = ParsePart(parts, 0, text, ref error);
		int? minor = parts.Length >= 2 ? ParsePart(parts, 1, text, ref error) : null;
		int? patch = parts.Length >= 3 ? ParsePart(parts, 2, text, ref error) : null;
		if (error is not null)
			return false;
		expression = new PackageVersionExpression(major, minor, patch);
		return true;
	}

	public bool Matches(PackageSelectedVersion version)
	{
		return (Major is null || version.Major == Major)
			&& (Minor is null || version.Minor == Minor)
			&& (Patch is null || version.Patch == Patch);
	}

	public override string ToString()
	{
		if (Major is null)
			return "";
		if (Minor is null)
			return Major.Value.ToString(CultureInfo.InvariantCulture);
		if (Patch is null)
			return Major.Value.ToString(CultureInfo.InvariantCulture) + "." + Minor.Value.ToString(CultureInfo.InvariantCulture);
		return Major.Value.ToString(CultureInfo.InvariantCulture) + "." + Minor.Value.ToString(CultureInfo.InvariantCulture) + "." + Patch.Value.ToString(CultureInfo.InvariantCulture);
	}

	static int? ParsePart(string[] parts, int index, string original, ref string? error)
	{
		if (index >= parts.Length || string.IsNullOrWhiteSpace(parts[index]) || !int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
		{
			error = $"Package version expression '{original}' is not valid.";
			return null;
		}
		return value;
	}
}

public sealed record PackageSelectedVersion(int Major, int Minor, int Patch) : IComparable<PackageSelectedVersion>
{
	public static IComparer<PackageSelectedVersion> Comparer { get; } = Comparer<PackageSelectedVersion>.Create(static (left, right) => left.CompareTo(right));

	public static bool TryParse(string text, out PackageSelectedVersion? version, out string? error)
	{
		version = null;
		error = null;
		if (string.IsNullOrWhiteSpace(text))
		{
			error = "Package version cannot be empty.";
			return false;
		}
		string[] parts = text.Split('.');
		if (parts.Length != 3
			|| !TryPart(parts[0], out int major)
			|| !TryPart(parts[1], out int minor)
			|| !TryPart(parts[2], out int patch))
		{
			error = $"Package version '{text}' must have three numeric components.";
			return false;
		}
		version = new PackageSelectedVersion(major, minor, patch);
		return true;
	}

	public static PackageSelectedVersion Parse(string text)
	{
		if (!TryParse(text, out PackageSelectedVersion? version, out string? error))
			throw new FormatException(error);
		return version!;
	}

	public int CompareTo(PackageSelectedVersion? other)
	{
		if (other is null)
			return 1;
		int major = Major.CompareTo(other.Major);
		if (major != 0)
			return major;
		int minor = Minor.CompareTo(other.Minor);
		if (minor != 0)
			return minor;
		return Patch.CompareTo(other.Patch);
	}

	public override string ToString() =>
		Major.ToString(CultureInfo.InvariantCulture) + "." + Minor.ToString(CultureInfo.InvariantCulture) + "." + Patch.ToString(CultureInfo.InvariantCulture);

	static bool TryPart(string text, out int value)
	{
		return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
	}
}

public sealed record PackageCatalog(string PackageName, string Identity, SortedDictionary<PackageSelectedVersion, PackageCatalogVersion> Versions)
{
	public static bool TryParse(string path, string text, out PackageCatalog? catalog, out List<string> errors)
	{
		catalog = null;
		errors = [];
		IniDocument ini = IniDocument.Parse(path, text, errors);
		if (errors.Count > 0)
			return false;

		if (!ini.Sections.TryGetValue("package", out Dictionary<string, string>? packageSection))
		{
			errors.Add($"{path}: missing [package] section.");
			return false;
		}
		string? name = Required(path, "package", packageSection, "name", errors);
		string? identity = Required(path, "package", packageSection, "identity", errors);
		SortedDictionary<PackageSelectedVersion, PackageCatalogVersion> versions = new(PackageSelectedVersion.Comparer);
		foreach ((string sectionName, Dictionary<string, string> section) in ini.Sections)
		{
			if (sectionName.Equals("package", StringComparison.OrdinalIgnoreCase))
				continue;
			if (!PackageSelectedVersion.TryParse(sectionName, out PackageSelectedVersion? version, out string? versionError))
			{
				errors.Add($"{path}: section [{sectionName}]: {versionError}");
				continue;
			}
			string? sha256 = Required(path, sectionName, section, "sha256", errors);
			string? src = Required(path, sectionName, section, "src", errors);
			IReadOnlyList<PackageDependencySpec> dependencies = ParseDependencies(path, sectionName, section.GetValueOrDefault("use"), errors);
			if (sha256 is not null && src is not null)
			{
				versions[version!] = new PackageCatalogVersion(version!, sha256, src, section.GetValueOrDefault("compiler"), dependencies);
			}
		}
		if (name is null || identity is null || errors.Count > 0)
			return false;
		catalog = new PackageCatalog(name, identity, versions);
		return true;
	}

	public string Write()
	{
		StringBuilder builder = new();
		builder.AppendLine("[package]");
		builder.Append("name=").AppendLine(PackageName);
		builder.Append("identity=").AppendLine(Identity);
		foreach ((PackageSelectedVersion version, PackageCatalogVersion item) in Versions)
		{
			builder.AppendLine();
			builder.Append('[').Append(version).AppendLine("]");
			if (!string.IsNullOrWhiteSpace(item.Compiler))
				builder.Append("compiler=").AppendLine(item.Compiler);
			if (item.Dependencies.Count > 0)
				builder.Append("use=").AppendLine(string.Join(' ', item.Dependencies.Select(static dependency => dependency.ToString())));
			builder.Append("sha256=").AppendLine(item.Sha256);
			builder.Append("src=").AppendLine(item.SourceArchive);
		}
		return builder.ToString();
	}

	static string? Required(string path, string section, Dictionary<string, string> values, string key, List<string> errors)
	{
		if (values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
			return value;
		errors.Add($"{path}: section [{section}]: missing required key '{key}'.");
		return null;
	}

	static IReadOnlyList<PackageDependencySpec> ParseDependencies(string path, string section, string? value, List<string> errors)
	{
		if (string.IsNullOrWhiteSpace(value))
			return [];
		List<PackageDependencySpec> result = [];
		foreach (string item in value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (PackageDependencySpec.TryParse(item, out PackageDependencySpec dependency, out string? error))
				result.Add(dependency);
			else
				errors.Add($"{path}: section [{section}]: invalid use dependency '{item}': {error}");
		}
		return result;
	}
}

public sealed record PackageCatalogVersion(PackageSelectedVersion Version, string Sha256, string SourceArchive, string? Compiler, IReadOnlyList<PackageDependencySpec> Dependencies);

public sealed record PackageLockFile(SortedDictionary<string, PackageLockEntry> Packages)
{
	public static bool TryParse(string path, string text, out PackageLockFile? lockFile, out List<string> errors)
	{
		lockFile = null;
		errors = [];
		IniDocument ini = IniDocument.Parse(path, text, errors);
		if (errors.Count > 0)
			return false;

		SortedDictionary<string, PackageLockEntry> packages = new(StringComparer.Ordinal);
		foreach ((string name, Dictionary<string, string> section) in ini.Sections)
		{
			string? identity = Required(path, name, section, "identity", errors);
			string? versionText = Required(path, name, section, "version", errors);
			string? sha256 = Required(path, name, section, "sha256", errors);
			if (versionText is not null && !PackageSelectedVersion.TryParse(versionText, out PackageSelectedVersion? version, out string? versionError))
			{
				errors.Add($"{path}: section [{name}]: {versionError}");
				continue;
			}
			if (identity is not null && versionText is not null && sha256 is not null)
				packages[name] = new PackageLockEntry(name, identity, PackageSelectedVersion.Parse(versionText), sha256);
		}
		if (errors.Count > 0)
			return false;
		lockFile = new PackageLockFile(packages);
		return true;
	}

	public string Write()
	{
		StringBuilder builder = new();
		foreach ((string name, PackageLockEntry package) in Packages)
		{
			builder.Append('[').Append(name).AppendLine("]");
			builder.Append("identity=").AppendLine(package.Identity);
			builder.Append("version=").AppendLine(package.Version.ToString());
			builder.Append("sha256=").AppendLine(package.Sha256);
			builder.AppendLine();
		}
		return builder.ToString();
	}

	static string? Required(string path, string section, Dictionary<string, string> values, string key, List<string> errors)
	{
		if (values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
			return value;
		errors.Add($"{path}: section [{section}]: missing required key '{key}'.");
		return null;
	}
}

public sealed record PackageLockEntry(string Name, string Identity, PackageSelectedVersion Version, string Sha256);

sealed class IniDocument
{
	public Dictionary<string, Dictionary<string, string>> Sections { get; } = new(StringComparer.OrdinalIgnoreCase);

	public static IniDocument Parse(string path, string text, List<string> errors)
	{
		IniDocument document = new();
		Dictionary<string, string>? current = null;
		string currentName = "";
		using StringReader reader = new(text);
		int lineNumber = 0;
		while (reader.ReadLine() is string line)
		{
			lineNumber++;
			string trimmed = line.Trim();
			if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
				continue;
			if (trimmed.StartsWith("[", StringComparison.Ordinal))
			{
				if (!trimmed.EndsWith("]", StringComparison.Ordinal) || trimmed.Length <= 2)
				{
					errors.Add($"{path}({lineNumber}): invalid section header.");
					continue;
				}
				currentName = trimmed[1..^1].Trim();
				if (document.Sections.ContainsKey(currentName))
					errors.Add($"{path}({lineNumber}): duplicate section [{currentName}].");
				current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				document.Sections[currentName] = current;
				continue;
			}
			if (current is null)
			{
				errors.Add($"{path}({lineNumber}): key/value pair appears before any section.");
				continue;
			}
			int equals = trimmed.IndexOf('=');
			if (equals <= 0)
			{
				errors.Add($"{path}({lineNumber}): expected key=value.");
				continue;
			}
			string key = trimmed[..equals].Trim();
			string value = trimmed[(equals + 1)..].Trim();
			if (current.ContainsKey(key))
				errors.Add($"{path}({lineNumber}): duplicate key '{key}' in section [{currentName}].");
			current[key] = value;
		}
		return document;
	}
}
