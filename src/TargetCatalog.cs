using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using IniParser;
using IniParser.Model;
using IniParser.Model.Configuration;
using IniParser.Parser;

namespace Camp.Compiler;

public sealed class TargetCatalog
{
	readonly Dictionary<string, TargetDefinition> targets;

	TargetCatalog(Dictionary<string, TargetDefinition> targets)
	{
		this.targets = targets;
	}

	public IReadOnlyDictionary<string, TargetDefinition> Targets => targets;

	public bool TryGetTarget(string name, out TargetDefinition? target)
	{
		return targets.TryGetValue(name, out target);
	}

	public static bool TryLoad(string targetsDirectory, out TargetCatalog? catalog, out string? error)
	{
		catalog = null;
		error = null;
		if (!Directory.Exists(targetsDirectory))
		{
			error = $"Target directory '{targetsDirectory}' could not be found.";
			return false;
		}

		if (!TryLoadRawTargets(targetsDirectory, out Dictionary<string, RawTargetDefinition> rawTargets, out error))
			return false;

		Dictionary<string, TargetDefinition> resolvedTargets = new(StringComparer.Ordinal);
		foreach (RawTargetDefinition target in rawTargets.Values)
		{
			if (!TryResolveTarget(target, rawTargets, resolvedTargets, [], out error))
				return false;
		}

		catalog = new TargetCatalog(resolvedTargets);
		return true;
	}

	static bool TryLoadRawTargets(string targetsDirectory, out Dictionary<string, RawTargetDefinition> targets, out string? error)
	{
		targets = new Dictionary<string, RawTargetDefinition>(StringComparer.Ordinal);
		error = null;

		FileIniDataParser parser = new(new IniDataParser(new IniParserConfiguration
		{
			AllowDuplicateKeys = false,
			AllowDuplicateSections = false,
			AllowKeysWithoutSection = false,
			ThrowExceptionsOnError = true
		}));

		foreach (string filename in Directory.GetFiles(targetsDirectory, "*.ini", SearchOption.AllDirectories).OrderBy(static x => x, StringComparer.Ordinal))
		{
			IniData data;
			try
			{
				data = parser.ReadFile(filename, Encoding.UTF8);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or IniParser.Exceptions.ParsingException)
			{
				error = $"{filename}: {ex.Message}";
				return false;
			}

			if (!TryGetTargetName(data, out string? targetName))
			{
				error = $"{filename}: Target file is missing [target] name.";
				return false;
			}

			if (targets.TryGetValue(targetName!, out RawTargetDefinition? existing))
			{
				error = $"Target '{targetName}' is declared by both '{existing.Path}' and '{filename}'.";
				return false;
			}

			targets.Add(targetName!, new RawTargetDefinition(targetName!, TryGetTargetBase(data), filename, data));
		}

		if (targets.Count == 0)
		{
			error = $"Target directory '{targetsDirectory}' does not contain any target INI files.";
			return false;
		}

		return true;
	}

	static bool TryResolveTarget(RawTargetDefinition target, Dictionary<string, RawTargetDefinition> rawTargets, Dictionary<string, TargetDefinition> resolvedTargets, HashSet<string> resolving, out string? error)
	{
		error = null;
		if (resolvedTargets.ContainsKey(target.Name))
			return true;

		if (!resolving.Add(target.Name))
		{
			error = $"Target '{target.Name}' has a circular base target chain.";
			return false;
		}

		TargetSections sections = new();
		if (!string.IsNullOrWhiteSpace(target.BaseName))
		{
			if (!rawTargets.TryGetValue(target.BaseName, out RawTargetDefinition? baseTarget))
			{
				error = $"{target.Path}: Base target '{target.BaseName}' could not be found.";
				return false;
			}

			if (!TryResolveTarget(baseTarget, rawTargets, resolvedTargets, resolving, out error))
				return false;

			sections.CopyFrom(resolvedTargets[baseTarget.Name].Sections);
		}

		sections.MergeFrom(target.Data);
		resolving.Remove(target.Name);
		resolvedTargets[target.Name] = new TargetDefinition(target.Name, target.BaseName, target.Path, sections);
		return true;
	}

	static bool TryGetTargetName(IniData data, out string? name)
	{
		name = null;
		if (!data.Sections.ContainsSection("target"))
			return false;

		name = data.Sections.GetSectionData("target").Keys.GetKeyData("name")?.Value?.Trim();
		return !string.IsNullOrWhiteSpace(name);
	}

	static string? TryGetTargetBase(IniData data)
	{
		if (!data.Sections.ContainsSection("target"))
			return null;

		string? baseName = data.Sections.GetSectionData("target").Keys.GetKeyData("base")?.Value?.Trim();
		return string.IsNullOrWhiteSpace(baseName) ? null : baseName;
	}

	sealed record RawTargetDefinition(string Name, string? BaseName, string Path, IniData Data);
}

public sealed class TargetDefinition
{
	internal TargetDefinition(string name, string? baseName, string path, TargetSections sections)
	{
		Name = name;
		BaseName = baseName;
		Path = path;
		Sections = sections;
	}

	public string Name { get; }
	public string? BaseName { get; }
	public string Path { get; }
	internal TargetSections Sections { get; }
	public IReadOnlyDictionary<string, string> CallSpecs => Sections.CallSpecs;
	public IReadOnlyDictionary<string, string> TypeSpecs => Sections.TypeSpecs;
	public IReadOnlyDictionary<string, string> CTypes => Sections.CTypes;
	public IReadOnlyList<string> TypeSpecOrder => Sections.TypeSpecOrder;

	public bool HasCallSpec(string name)
	{
		return Sections.CallSpecs.ContainsKey(name);
	}

	public bool HasTypeSpec(string name)
	{
		return Sections.TypeSpecs.ContainsKey(name);
	}

	public bool IsPrimitiveUnsupported(string name)
	{
		return Sections.CTypes.TryGetValue(name, out string? value) && value == "<unsupported>";
	}

	public bool CanWidenTypeSpec(string? source, string? target)
	{
		if (source == target)
			return true;
		if (target is null)
			return false;
		if (source is null)
			return HasTypeSpec(target);
		int sourceIndex = Sections.TypeSpecOrder.IndexOf(source);
		int targetIndex = Sections.TypeSpecOrder.IndexOf(target);
		return sourceIndex >= 0 && targetIndex >= 0 && sourceIndex <= targetIndex;
	}

	public bool AreTypeSpecsCompatible(string? source, string? target)
	{
		if (source == target)
			return true;
		if (source is null)
			return target is not null && HasTypeSpec(target);
		if (target is null)
			return HasTypeSpec(source);
		return HasTypeSpec(source) && HasTypeSpec(target);
	}
}

internal sealed class TargetSections
{
	public Dictionary<string, string> CallSpecs { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, string> TypeSpecs { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, string> CTypes { get; } = new(StringComparer.Ordinal);
	public List<string> TypeSpecOrder { get; } = [];

	public void CopyFrom(TargetSections source)
	{
		CopySection(source.CallSpecs, CallSpecs);
		CopyTypeSpecSection(source.TypeSpecs, source.TypeSpecOrder);
		CopySection(source.CTypes, CTypes);
	}

	public void MergeFrom(IniData data)
	{
		MergeSection(data, "callspec", CallSpecs);
		MergeTypeSpecSection(data);
		MergeSection(data, "ctype", CTypes);
	}

	void CopyTypeSpecSection(Dictionary<string, string> source, List<string> order)
	{
		foreach (string key in order)
		{
			if (!TypeSpecs.ContainsKey(key))
				TypeSpecOrder.Add(key);
			TypeSpecs[key] = source[key];
		}
	}

	static void CopySection(Dictionary<string, string> source, Dictionary<string, string> target)
	{
		foreach ((string key, string value) in source)
			target[key] = value;
	}

	static void MergeSection(IniData data, string sectionName, Dictionary<string, string> target)
	{
		if (!data.Sections.ContainsSection(sectionName))
			return;

		foreach (KeyData key in data.Sections.GetSectionData(sectionName).Keys)
			target[key.KeyName] = key.Value;
	}

	void MergeTypeSpecSection(IniData data)
	{
		if (!data.Sections.ContainsSection("typespec"))
			return;

		foreach (KeyData key in data.Sections.GetSectionData("typespec").Keys)
		{
			if (!TypeSpecs.ContainsKey(key.KeyName))
				TypeSpecOrder.Add(key.KeyName);
			TypeSpecs[key.KeyName] = key.Value;
		}
	}
}
