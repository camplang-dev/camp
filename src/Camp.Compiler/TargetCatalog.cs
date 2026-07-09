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

		try
		{
			sections.MergeFrom(target.Data);
		}
		catch (InvalidDataException ex)
		{
			error = $"{target.Path}: {ex.Message}";
			return false;
		}
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
	internal TargetDefinition(string name, string? baseName, string path, TargetSections sections, TargetVariantSelection? variantSelection = null)
	{
		Name = name;
		BaseName = baseName;
		Path = path;
		Sections = sections;
		VariantSelection = variantSelection ?? TargetVariantSelection.Default;
		Capabilities = new TargetCapabilities(this);
	}

	public string Name { get; }
	public string? BaseName { get; }
	public string Path { get; }
	internal TargetSections Sections { get; }
	public IReadOnlyDictionary<string, string> CallSpecs => Sections.CallSpecs;
	public IReadOnlyDictionary<string, string> TypeSpecs => Sections.TypeSpecs;
	public IReadOnlyDictionary<string, string> CTypes => Sections.CTypes;
	public IReadOnlyDictionary<string, string> Defines => Sections.Defines;
	public IReadOnlyDictionary<string, string> TargetCapabilities => Sections.Capabilities;
	public IReadOnlyDictionary<string, int> NaturalIntegerWidths => Sections.NaturalIntegerWidths;
	public IReadOnlyDictionary<string, int> PointerWidths => Sections.PointerWidths;
	public IReadOnlyDictionary<string, TargetVariantGroup> VariantGroups => Sections.VariantGroups;
	public TargetVariantSelection VariantSelection { get; }
	public IReadOnlySet<string> TargetOwnedDefines => Sections.TargetOwnedDefines;
	public IReadOnlyList<string> TypeSpecOrder => Sections.TypeSpecOrder;
	public IReadOnlyList<string> Includes => Sections.Includes;
	public IReadOnlyDictionary<string, string> Toolchain => Sections.Toolchain;
	public IReadOnlyDictionary<string, string> Artifact => Sections.Artifact;
	public IReadOnlyDictionary<string, string> BuildTemplates => Sections.BuildTemplates;
	public IReadOnlyDictionary<string, string> CEmitter => Sections.CEmitter;
	public IReadOnlyDictionary<string, TargetProfileBuild> Profiles => Sections.Profiles;
	public TargetCapabilities Capabilities { get; }

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

	public string? GetPrimitiveCSpelling(string name)
	{
		return Sections.CTypes.TryGetValue(name, out string? value) ? value : null;
	}

	public string GetTool(string name)
	{
		return Sections.Toolchain.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : name;
	}

	public string GetArtifactValue(string name, string defaultValue = "")
	{
		return Sections.Artifact.TryGetValue(name, out string? value) ? value : defaultValue;
	}

	public string? GetBuildTemplate(string name)
	{
		return Sections.BuildTemplates.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;
	}

	public string GetCapabilityValue(string name, string defaultValue = "")
	{
		return Sections.Capabilities.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;
	}

	public string GetCEmitterValue(string name, string defaultValue = "")
	{
		return Sections.CEmitter.TryGetValue(name, out string? value) ? value : defaultValue;
	}

	public TargetProfileBuild GetProfileBuild(string profileName)
	{
		return Sections.Profiles.TryGetValue(profileName, out TargetProfileBuild? profile) ? profile : TargetProfileBuild.Empty;
	}

	public int GetNaturalIntegerWidth(string? typeSpec)
	{
		if (typeSpec is not null && Sections.NaturalIntegerWidths.TryGetValue(typeSpec, out int width))
			return width;
		return Sections.NaturalIntegerWidths.TryGetValue("", out int defaultWidth) ? defaultWidth : 32;
	}

	public TargetVariantSelection ResolveVariantSelection(IEnumerable<string> requestedVariants)
	{
		return TargetVariantSelection.Resolve(this, requestedVariants);
	}

	public TargetDefinition WithVariantSelection(TargetVariantSelection selection)
	{
		TargetSections sections = new();
		sections.CopyFrom(Sections);
		sections.ApplyVariantOverlays(selection);
		sections.ValidateTargetMetadata();
		return new TargetDefinition(Name, BaseName, Path, sections, selection);
	}

	public string GetVariantDirectoryName()
	{
		if (VariantSelection.SelectedVariants.Count == 0)
			return Name;
		List<string> nonDefault = [];
		foreach (TargetVariantGroup group in Sections.VariantGroups.Values)
		{
			if (!VariantSelection.SelectedVariants.TryGetValue(group.Name, out string? selected))
				continue;
			if (!string.Equals(selected, group.DefaultVariantName, StringComparison.Ordinal))
				nonDefault.Add(selected);
		}
		return nonDefault.Count == 0 ? Name : Name + "_" + string.Join("_", nonDefault);
	}

	public int GetPointerWidth(string? typeSpec, bool functionPointer)
	{
		typeSpec ??= functionPointer ? Sections.DefaultFunctionPointerTypeSpec : Sections.DefaultDataPointerTypeSpec;
		if (typeSpec is not null && Sections.PointerWidths.TryGetValue(typeSpec, out int width))
			return width;
		return Sections.PointerWidths.TryGetValue("", out int defaultWidth) ? defaultWidth : 32;
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

	public TargetConversionLevel ClassifyTypeSpecConversion(TargetConversionCarrier carrier, string? source, string? target)
	{
		if (source == target)
			return carrier == TargetConversionCarrier.AbiSlot ? TargetConversionLevel.Compatible : TargetConversionLevel.Implicit;
		if (source is not null && target is not null
			&& Sections.ConversionPolicy.TryGetValue(new TargetConversionPolicyKey(carrier, source, target), out TargetConversionLevel configured))
			return configured;
		if (carrier == TargetConversionCarrier.AbiSlot)
			return TargetConversionLevel.Forbidden;
		if (CanWidenTypeSpec(source, target))
			return TargetConversionLevel.Implicit;
		if (AreTypeSpecsCompatible(source, target))
			return TargetConversionLevel.Explicit;
		return TargetConversionLevel.Forbidden;
	}
}

public enum TargetConversionCarrier
{
	DataPointer,
	FunctionPointer,
	NaturalInteger,
	AbiSlot
}

public enum TargetConversionLevel
{
	Implicit,
	Explicit,
	Unsafe,
	Fence,
	Forbidden,
	Compatible
}

public sealed class TargetCapabilities
{
	readonly TargetDefinition target;

	internal TargetCapabilities(TargetDefinition target)
	{
		this.target = target;
	}

	public string Platform => GetCapabilityValue("platform");
	public string Compiler => GetCapabilityValue("compiler");
	public string CSourceExtension => GetCapabilityValue("c_source_ext", ".c");
	public string ObjectiveCSourceExtension => GetCapabilityValue("objc_source_ext", ".m");
	public bool SupportsFrameworks => GetBooleanCapability("supports_frameworks", legacyBuildTemplate: "allow_frameworks");
	public bool SupportsFrameworkLinking => SupportsFrameworks;
	public bool SupportsObjectiveC => GetBooleanCapability("supports_objc");

	public bool HasCallSpec(string name)
	{
		return target.HasCallSpec(name);
	}

	public bool HasTypeSpec(string name)
	{
		return target.HasTypeSpec(name);
	}

	public bool IsPrimitiveUnsupported(string name)
	{
		return target.IsPrimitiveUnsupported(name);
	}

	public string? GetPrimitiveCSpelling(string name)
	{
		return target.GetPrimitiveCSpelling(name);
	}

	public string GetArtifactValue(string name, string defaultValue = "")
	{
		return target.GetArtifactValue(name, defaultValue);
	}

	public string GetTool(string name)
	{
		return target.GetTool(name);
	}

	public string? GetBuildTemplate(string name)
	{
		return target.GetBuildTemplate(name);
	}

	public string GetCapabilityValue(string name, string defaultValue = "")
	{
		return target.GetCapabilityValue(name, defaultValue);
	}

	public string GetCEmitterValue(string name, string defaultValue = "")
	{
		return target.GetCEmitterValue(name, defaultValue);
	}

	public int GetNaturalIntegerWidth(string? typeSpec)
	{
		return target.GetNaturalIntegerWidth(typeSpec);
	}

	public int GetPointerWidth(string? typeSpec, string? memoryModelName, bool functionPointer)
	{
		return target.GetPointerWidth(typeSpec, functionPointer);
	}

	public TargetConversionLevel ClassifyTypeSpecConversion(TargetConversionCarrier carrier, string? source, string? destination)
	{
		return target.ClassifyTypeSpecConversion(carrier, source, destination);
	}

	bool GetBooleanCapability(string name, string? legacyBuildTemplate = null)
	{
		string value = GetCapabilityValue(name);
		if (!string.IsNullOrWhiteSpace(value))
			return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
		return legacyBuildTemplate is not null && string.Equals(GetBuildTemplate(legacyBuildTemplate), "true", StringComparison.OrdinalIgnoreCase);
	}
}

internal sealed class TargetSections
{
	public Dictionary<string, string> CallSpecs { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, string> TypeSpecs { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, string> Capabilities { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, string> CTypes { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, string> Defines { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, int> NaturalIntegerWidths { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, int> PointerWidths { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, TargetVariantGroup> VariantGroups { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, TargetVariant> VariantsByName { get; } = new(StringComparer.Ordinal);
	public HashSet<string> TargetOwnedDefines { get; } = new(StringComparer.Ordinal);
	public List<TargetConditionalSection> ConditionalSections { get; } = [];
	public string? DefaultCodePointerTypeSpec { get; private set; }
	public string? DefaultDataPointerTypeSpec { get; private set; }
	public string? DefaultFunctionPointerTypeSpec => DefaultCodePointerTypeSpec;
	public List<string> TypeSpecOrder { get; } = [];
	public List<string> Includes { get; } = [];
	public Dictionary<string, string> Toolchain { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, string> Artifact { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, string> BuildTemplates { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, string> CEmitter { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, TargetProfileBuild> Profiles { get; } = new(StringComparer.Ordinal);
	public Dictionary<TargetConversionPolicyKey, TargetConversionLevel> ConversionPolicy { get; } = new();

	public void CopyFrom(TargetSections source)
	{
		Includes.AddRange(source.Includes);
		CopySection(source.Defines, Defines);
		CopySection(source.CallSpecs, CallSpecs);
		CopySection(source.Capabilities, Capabilities);
		CopyTypeSpecSection(source.TypeSpecs, source.TypeSpecOrder);
		CopySection(source.CTypes, CTypes);
		CopySection(source.NaturalIntegerWidths, NaturalIntegerWidths);
		CopySection(source.PointerWidths, PointerWidths);
		CopySection(source.VariantGroups, VariantGroups);
		CopySection(source.VariantsByName, VariantsByName);
		foreach (string define in source.TargetOwnedDefines)
			TargetOwnedDefines.Add(define);
		ConditionalSections.AddRange(source.ConditionalSections);
		DefaultCodePointerTypeSpec = source.DefaultCodePointerTypeSpec;
		DefaultDataPointerTypeSpec = source.DefaultDataPointerTypeSpec;
		CopySection(source.Toolchain, Toolchain);
		CopySection(source.Artifact, Artifact);
		CopySection(source.BuildTemplates, BuildTemplates);
		CopySection(source.CEmitter, CEmitter);
		CopySection(source.Profiles, Profiles);
		CopyDictionary(source.ConversionPolicy, ConversionPolicy);
	}

	public void MergeFrom(IniData data)
	{
		MergeVariantSection(data);
		foreach (SectionData section in data.Sections)
		{
			ParsedSectionName parsed = ParseSectionName(section.SectionName);
			if (parsed.Variants.Count > 0)
			{
				ValidateConditionalVariants(section.SectionName, parsed.Variants);
				RecordTargetOwnedDefines(parsed.BaseName, section);
				ConditionalSections.Add(new TargetConditionalSection(parsed.BaseName, [.. parsed.Variants], section));
				continue;
			}
			MergeSectionData(parsed.BaseName, section);
		}
		ValidateTargetMetadata();
	}

	public void ApplyVariantOverlays(TargetVariantSelection selection)
	{
		foreach (TargetConditionalSection conditional in ConditionalSections)
		{
			if (!conditional.VariantNames.All(selection.ContainsVariant))
				continue;
			MergeSectionData(conditional.SectionName, conditional.Section);
		}
	}

	void MergeSectionData(string sectionName, SectionData section)
	{
		switch (sectionName)
		{
			case "callspec":
				MergeSection(section, CallSpecs);
				break;
			case "target":
				MergeTargetSection(section);
				break;
			case "capability":
				MergeSection(section, Capabilities);
				break;
			case "define":
				RecordTargetOwnedDefines(sectionName, section);
				MergeSection(section, Defines);
				break;
			case "typespec":
				MergeTypeSpecSection(section);
				break;
			case "ctype":
				MergeSection(section, CTypes);
				break;
			case "nint":
				MergeWidthSection(section, sectionName, NaturalIntegerWidths);
				break;
			case "pointer":
				MergeWidthSection(section, sectionName, PointerWidths);
				break;
			case "memorymodel":
				throw new InvalidDataException("[memorymodel] has been replaced by [variant] and [typespec:*] default=<code>/<data>.");
			case "toolchain":
				MergeSection(section, Toolchain);
				break;
			case "artifact":
				MergeSection(section, Artifact);
				break;
			case "build":
				MergeSection(section, BuildTemplates);
				break;
			case "cemit":
				MergeSection(section, CEmitter);
				break;
			default:
				if (sectionName.StartsWith("profile.", StringComparison.Ordinal))
					MergeProfileSection(sectionName, section);
				else if (sectionName.StartsWith("conversion.", StringComparison.Ordinal))
					MergeConversionPolicySection(sectionName, section);
				break;
		}
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

	static void CopySection<T>(Dictionary<string, T> source, Dictionary<string, T> target)
	{
		foreach ((string key, T value) in source)
			target[key] = value;
	}

	static void CopyDictionary<TKey, TValue>(Dictionary<TKey, TValue> source, Dictionary<TKey, TValue> target)
		where TKey : notnull
	{
		foreach ((TKey key, TValue value) in source)
			target[key] = value;
	}

	static void MergeSection(SectionData section, Dictionary<string, string> target)
	{
		foreach (KeyData key in section.Keys)
			target[key.KeyName] = key.Value;
	}

	void MergeTargetSection(SectionData section)
	{
		string? include = section.Keys.GetKeyData("include")?.Value;
		if (string.IsNullOrWhiteSpace(include))
			return;

		foreach (string item in include.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
		{
			if (!Includes.Contains(item, StringComparer.Ordinal))
				Includes.Add(item);
		}
	}

	void MergeTypeSpecSection(SectionData section)
	{
		foreach (KeyData key in section.Keys)
		{
			if (key.KeyName == "default")
			{
				string[] parts = key.Value.Split('/', 2);
				if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
					throw new InvalidDataException("[typespec] default must use '<code>/<data>' format.");
				DefaultCodePointerTypeSpec = parts[0].Trim();
				DefaultDataPointerTypeSpec = parts[1].Trim();
				continue;
			}
			if (!TypeSpecs.ContainsKey(key.KeyName))
				TypeSpecOrder.Add(key.KeyName);
			TypeSpecs[key.KeyName] = key.Value;
		}
	}

	void MergeWidthSection(SectionData section, string sectionName, Dictionary<string, int> target)
	{
		foreach (KeyData key in section.Keys)
		{
			string keyName = key.KeyName == "default" ? "" : key.KeyName;
			string value = key.Value.Trim();
			if (!int.TryParse(value, out int width) || width is not (16 or 32 or 64))
				throw new InvalidDataException($"[{sectionName}] '{key.KeyName}' must be one of 16, 32, or 64.");
			target[keyName] = width;
		}
	}

	void MergeVariantSection(IniData data)
	{
		if (!data.Sections.ContainsSection("variant"))
			return;
		foreach (KeyData key in data.Sections.GetSectionData("variant").Keys)
		{
			string groupName = key.KeyName.Trim();
			if (!IsVariantIdentifier(groupName))
				throw new InvalidDataException($"Variant group '{groupName}' must use only ASCII letters and digits and start with a letter.");
			if (VariantGroups.ContainsKey(groupName))
				throw new InvalidDataException($"Variant group '{groupName}' is already defined.");
			List<TargetVariant> variants = [];
			string? defaultVariant = null;
			foreach (string rawPart in key.Value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
			{
				bool isDefault = rawPart.EndsWith('*');
				string variantName = isDefault ? rawPart[..^1] : rawPart;
				if (!IsVariantIdentifier(variantName))
					throw new InvalidDataException($"Variant '{variantName}' must use only ASCII letters and digits and start with a letter.");
				if (VariantsByName.ContainsKey(variantName))
					throw new InvalidDataException($"Variant '{variantName}' is already defined by another group.");
				if (isDefault)
				{
					if (defaultVariant is not null)
						throw new InvalidDataException($"Variant group '{groupName}' must have exactly one default variant.");
					defaultVariant = variantName;
				}
				TargetVariant variant = new(groupName, variantName, isDefault, VariantsByName.Count);
				variants.Add(variant);
				VariantsByName.Add(variantName, variant);
			}
			if (variants.Count == 0 || defaultVariant is null)
				throw new InvalidDataException($"Variant group '{groupName}' must have exactly one default variant.");
			VariantGroups.Add(groupName, new TargetVariantGroup(groupName, variants, defaultVariant));
		}
	}

	void MergeProfileSection(string sectionName, SectionData section)
	{
		string profileName = sectionName["profile.".Length..].Trim();
		if (profileName.Length == 0)
			throw new InvalidDataException("Profile build section names must use [profile.NAME].");
		string cflags = section.Keys.GetKeyData("cflags")?.Value ?? "";
		string ldflags = section.Keys.GetKeyData("ldflags")?.Value ?? "";
		Profiles[profileName.ToUpperInvariant()] = new TargetProfileBuild(cflags, ldflags);
	}

	void MergeConversionPolicySection(string sectionName, SectionData section)
	{
		string carrierName = sectionName["conversion.".Length..].Trim();
		TargetConversionCarrier carrier = carrierName switch
		{
			"data_pointer" => TargetConversionCarrier.DataPointer,
			"function_pointer" => TargetConversionCarrier.FunctionPointer,
			"nint" => TargetConversionCarrier.NaturalInteger,
			"abi_slot" => TargetConversionCarrier.AbiSlot,
			_ => throw new InvalidDataException($"Unsupported target conversion carrier '{carrierName}'.")
		};

		foreach (KeyData key in section.Keys)
		{
			string[] specs = key.KeyName.Split("->", 2, StringSplitOptions.TrimEntries);
			if (specs.Length != 2 || string.IsNullOrWhiteSpace(specs[0]) || string.IsNullOrWhiteSpace(specs[1]))
				throw new InvalidDataException($"Target conversion '{key.KeyName}' must use '<source>-><target>' syntax.");
			string source = specs[0];
			string target = specs[1];
			ValidateConversionPolicySpec(key.KeyName, source);
			ValidateConversionPolicySpec(key.KeyName, target);

			string levelName = key.Value.Trim();
			TargetConversionLevel level = levelName switch
			{
				"implicit" => TargetConversionLevel.Implicit,
				"explicit" => TargetConversionLevel.Explicit,
				"unsafe" => TargetConversionLevel.Unsafe,
				"fence" => TargetConversionLevel.Fence,
				"forbidden" => TargetConversionLevel.Forbidden,
				"compatible" => TargetConversionLevel.Compatible,
				_ => throw new InvalidDataException($"Target conversion policy '{key.KeyName}={levelName}' uses unknown conversion level '{levelName}'.")
			};

			if (level == TargetConversionLevel.Compatible && carrier != TargetConversionCarrier.AbiSlot)
				throw new InvalidDataException("Conversion level 'compatible' is only valid in [conversion.abi_slot].");
			if (carrier == TargetConversionCarrier.AbiSlot && level != TargetConversionLevel.Compatible && level != TargetConversionLevel.Forbidden)
				throw new InvalidDataException("[conversion.abi_slot] entries must use 'compatible' or 'forbidden'.");

			ConversionPolicy[new TargetConversionPolicyKey(carrier, source, target)] = level;
		}
	}

	static ParsedSectionName ParseSectionName(string sectionName)
	{
		string[] parts = sectionName.Split(':', StringSplitOptions.TrimEntries);
		return new ParsedSectionName(parts[0], parts.Skip(1).Where(static part => part.Length > 0).ToArray());
	}

	void ValidateConditionalVariants(string sectionName, IReadOnlyList<string> variants)
	{
		foreach (string variant in variants)
			if (!VariantsByName.ContainsKey(variant))
				throw new InvalidDataException($"Section [{sectionName}] references unknown variant '{variant}'.");
	}

	void RecordTargetOwnedDefines(string sectionName, SectionData section)
	{
		if (sectionName != "define")
			return;
		foreach (KeyData key in section.Keys)
			TargetOwnedDefines.Add(key.KeyName);
	}

	static bool IsVariantIdentifier(string value)
	{
		if (value.Length == 0 || !IsAsciiLetter(value[0]))
			return false;
		for (int i = 1; i < value.Length; i++)
			if (!IsAsciiLetter(value[i]) && !char.IsAsciiDigit(value[i]))
				return false;
		return true;
	}

	static bool IsAsciiLetter(char ch)
	{
		return ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
	}

	void ValidateConversionPolicySpec(string conversion, string spec)
	{
		if (TypeSpecs.ContainsKey(spec))
			return;
		if (CallSpecs.ContainsKey(spec))
			throw new InvalidDataException($"Target conversion '{conversion}' references callspec '{spec}'; conversion policies require typespecs.");
		throw new InvalidDataException($"Target conversion '{conversion}' references unknown typespec '{spec}'.");
	}

	public void ValidateTargetMetadata()
	{
		foreach (string key in NaturalIntegerWidths.Keys)
		{
			if (key.Length > 0 && !TypeSpecs.ContainsKey(key))
				throw new InvalidDataException($"[nint] '{key}' must name a valid target typespec.");
		}

		foreach (string key in PointerWidths.Keys)
		{
			if (key.Length > 0 && !TypeSpecs.ContainsKey(key))
				throw new InvalidDataException($"[pointer] '{key}' must name a valid target typespec.");
		}

		if (DefaultCodePointerTypeSpec is not null && !TypeSpecs.ContainsKey(DefaultCodePointerTypeSpec))
			throw new InvalidDataException($"[typespec] default code pointer typespec '{DefaultCodePointerTypeSpec}' must name a valid target typespec.");
		if (DefaultDataPointerTypeSpec is not null && !TypeSpecs.ContainsKey(DefaultDataPointerTypeSpec))
			throw new InvalidDataException($"[typespec] default data pointer typespec '{DefaultDataPointerTypeSpec}' must name a valid target typespec.");
	}
}

public sealed record TargetVariant(string GroupName, string Name, bool IsDefault, int Order);

public sealed record TargetVariantGroup(string Name, IReadOnlyList<TargetVariant> Variants, string DefaultVariantName);

public sealed class TargetVariantSelection
{
	public static TargetVariantSelection Default { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

	TargetVariantSelection(Dictionary<string, string> selectedVariants)
	{
		SelectedVariants = selectedVariants;
	}

	public IReadOnlyDictionary<string, string> SelectedVariants { get; }

	public bool ContainsVariant(string variantName)
	{
		return SelectedVariants.Values.Contains(variantName, StringComparer.Ordinal);
	}

	public static TargetVariantSelection Resolve(TargetDefinition target, IEnumerable<string> requestedVariants)
	{
		Dictionary<string, string> selected = new(StringComparer.Ordinal);
		HashSet<string> explicitlySelectedGroups = new(StringComparer.Ordinal);
		foreach (TargetVariantGroup group in target.VariantGroups.Values)
			selected[group.Name] = group.DefaultVariantName;

		foreach (string requested in requestedVariants)
		{
			if (!target.Sections.VariantsByName.TryGetValue(requested, out TargetVariant? variant))
				throw new InvalidDataException($"Variant '{requested}' is not defined by target '{target.Name}'. Available variants: {string.Join(", ", target.Sections.VariantsByName.Keys)}.");
			if (explicitlySelectedGroups.Contains(variant.GroupName) && selected.TryGetValue(variant.GroupName, out string? existing) && existing != variant.Name)
				throw new InvalidDataException($"Variants '{existing}' and '{variant.Name}' both belong to group '{variant.GroupName}'; select only one.");
			selected[variant.GroupName] = variant.Name;
			explicitlySelectedGroups.Add(variant.GroupName);
		}
		return new TargetVariantSelection(selected);
	}
}

internal sealed record TargetConditionalSection(string SectionName, IReadOnlyList<string> VariantNames, SectionData Section);

internal sealed record ParsedSectionName(string BaseName, IReadOnlyList<string> Variants);

public sealed record TargetProfileBuild(string CFlags, string LdFlags)
{
	public static TargetProfileBuild Empty { get; } = new("", "");
}

public sealed record TargetConversionPolicyKey(TargetConversionCarrier Carrier, string Source, string Target);
