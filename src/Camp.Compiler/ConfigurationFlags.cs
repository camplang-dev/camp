using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public enum ConfigurationRequirementPolicy
{
	Explicit,
	Implicit
}

public enum ConfigurationFlagOwner
{
	Target,
	Module
}

public sealed record ConfigurationFlagDeclaration(string Name, bool AmbientValue, ConfigurationFlagOwner Owner, string Source);

public sealed record ConfigurationFlagConfiguration(string Name, bool Value, ConfigurationFlagOwner Owner, string Source);

public sealed class ConfigurationFlagSet
{
	readonly Dictionary<string, ConfigurationFlagDeclaration> declarations = new(StringComparer.Ordinal);
	readonly Dictionary<string, ConfigurationFlagConfiguration> configurations = new(StringComparer.Ordinal);

	public IReadOnlyDictionary<string, ConfigurationFlagDeclaration> Declarations => declarations;
	public IReadOnlyDictionary<string, ConfigurationFlagConfiguration> Configurations => configurations;

	public bool TryDeclare(string text, bool defaultAmbientValue, ConfigurationFlagOwner owner, string source, List<string> errors)
	{
		if (!ConfigurationFlagSyntax.TryParseAssignment(text, defaultAmbientValue, out string name, out bool ambientValue, out string? error))
		{
			errors.Add(error!);
			return false;
		}
		if (declarations.TryGetValue(name, out ConfigurationFlagDeclaration? existing))
		{
			errors.Add($"Configuration flag '{name}' is already declared by {existing.Source}.");
			return false;
		}
		declarations.Add(name, new ConfigurationFlagDeclaration(name, ambientValue, owner, source));
		return true;
	}

	public bool TryConfigure(string text, bool defaultValue, ConfigurationFlagOwner owner, string source, bool allowTargetOwned, List<string> errors)
	{
		if (!ConfigurationFlagSyntax.TryParseAssignment(text, defaultValue, out string name, out bool value, out string? error))
		{
			errors.Add(error!);
			return false;
		}
		if (!declarations.TryGetValue(name, out ConfigurationFlagDeclaration? declaration))
		{
			errors.Add($"Configuration flag '{name}' must be declared before it can be configured.");
			return false;
		}
		if (declaration.Owner == ConfigurationFlagOwner.Target && !allowTargetOwned)
		{
			errors.Add($"Configuration flag '{name}' is owned by the selected target and cannot be configured from this request.");
			return false;
		}
		if (configurations.TryGetValue(name, out ConfigurationFlagConfiguration? existing))
		{
			errors.Add($"Configuration flag '{name}' is already configured by {existing.Source}.");
			return false;
		}
		configurations.Add(name, new ConfigurationFlagConfiguration(name, value, owner, source));
		return true;
	}

	public bool IsConfiguredTrue(string name)
	{
		if (configurations.TryGetValue(name, out ConfigurationFlagConfiguration? configuration))
			return configuration.Value;
		return declarations.TryGetValue(name, out ConfigurationFlagDeclaration? declaration) && declaration.AmbientValue;
	}

	public IEnumerable<string> TrueFlags() =>
		declarations.Keys.Where(IsConfiguredTrue);
}

public static class ConfigurationFlagSyntax
{
	public static bool TryParseAssignment(string text, bool defaultValue, out string name, out bool value, out string? error)
	{
		name = "";
		value = defaultValue;
		error = null;
		if (string.IsNullOrWhiteSpace(text))
		{
			error = "Configuration flag names cannot be empty.";
			return false;
		}
		string trimmed = text.Trim();
		string[] parts = trimmed.Split('=', 2, StringSplitOptions.TrimEntries);
		name = parts[0];
		if (!IsValidName(name))
		{
			error = $"Configuration flag '{name}' is not a valid identifier.";
			return false;
		}
		if (parts.Length == 1)
			return true;
		if (parts[1] == "true")
		{
			value = true;
			return true;
		}
		if (parts[1] == "false")
		{
			value = false;
			return true;
		}
		error = $"Configuration flag '{name}' must use true or false.";
		return false;
	}

	public static bool IsValidName(string value)
	{
		if (value.Length == 0 || !IsIdentifierStart(value[0]))
			return false;
		for (int i = 1; i < value.Length; i++)
			if (!IsIdentifierPart(value[i]))
				return false;
		return true;
	}

	static bool IsIdentifierStart(char ch) =>
		ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

	static bool IsIdentifierPart(char ch) =>
		IsIdentifierStart(ch) || ch is >= '0' and <= '9';
}
