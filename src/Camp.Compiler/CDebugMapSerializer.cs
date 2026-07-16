using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Camp.Compiler;

public sealed record CDebugSourceRange(
	string File,
	int StartLine,
	int StartColumn,
	int EndLine,
	int EndColumn);

public sealed record CDebugGeneratedRange(
	string File,
	int Line,
	string? Function);

public sealed record CDebugVariableMap(
	string CampName,
	string NativeName,
	string? Type,
	string Kind);

public sealed record CDebugMapEntry(
	string Kind,
	CDebugSourceRange? Source,
	CDebugGeneratedRange Generated,
	string? CampFunction,
	string? NativeSymbol,
	bool GeneratedRegion,
	IReadOnlyList<CDebugVariableMap> Variables);

public static class CDebugMapSerializer
{
	static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public static string Serialize(Compilation compilation, string projectName, string outputDirectory, IReadOnlyList<CDebugMapEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(compilation);
		ArgumentNullException.ThrowIfNull(projectName);
		ArgumentNullException.ThrowIfNull(outputDirectory);
		ArgumentNullException.ThrowIfNull(entries);

		object document = new
		{
			format = "camp.debug",
			version = 1,
			project = projectName,
			target = compilation.Target?.Name,
			profile = compilation.ProfileName,
			outputDirectory = Path.GetFullPath(outputDirectory),
			entries = entries.Select(static entry => new
			{
				kind = entry.Kind,
				source = entry.Source,
				generated = entry.Generated,
				campFunction = entry.CampFunction,
				nativeSymbol = entry.NativeSymbol,
				generatedRegion = entry.GeneratedRegion,
				variables = entry.Variables.Count == 0 ? null : entry.Variables
			}).ToList()
		};
		return JsonSerializer.Serialize(document, Options) + Environment.NewLine;
	}
}
