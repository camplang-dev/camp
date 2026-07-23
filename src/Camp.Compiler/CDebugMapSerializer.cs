using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

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
	public static string Serialize(Compilation compilation, string projectName, string outputDirectory, IReadOnlyList<CDebugMapEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(compilation);
		ArgumentNullException.ThrowIfNull(projectName);
		ArgumentNullException.ThrowIfNull(outputDirectory);
		ArgumentNullException.ThrowIfNull(entries);

		using MemoryStream stream = new();
		using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
		{
			writer.WriteStartObject();
			writer.WriteString("format", "camp.debug");
			writer.WriteNumber("version", 1);
			writer.WriteString("project", projectName);
			WriteStringIfNotNull(writer, "target", compilation.Target?.Name);
			WriteStringIfNotNull(writer, "profile", compilation.ProfileName);
			writer.WriteString("outputDirectory", Path.GetFullPath(outputDirectory));
			writer.WritePropertyName("entries");
			writer.WriteStartArray();
			foreach (CDebugMapEntry entry in entries)
				WriteEntry(writer, entry);
			writer.WriteEndArray();
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine;
	}

	static void WriteEntry(Utf8JsonWriter writer, CDebugMapEntry entry)
	{
		writer.WriteStartObject();
		writer.WriteString("kind", entry.Kind);
		if (entry.Source is not null)
			WriteSourceRange(writer, "source", entry.Source);
		WriteGeneratedRange(writer, "generated", entry.Generated);
		WriteStringIfNotNull(writer, "campFunction", entry.CampFunction);
		WriteStringIfNotNull(writer, "nativeSymbol", entry.NativeSymbol);
		writer.WriteBoolean("generatedRegion", entry.GeneratedRegion);
		if (entry.Variables.Count > 0)
		{
			writer.WritePropertyName("variables");
			writer.WriteStartArray();
			foreach (CDebugVariableMap variable in entry.Variables)
				WriteVariable(writer, variable);
			writer.WriteEndArray();
		}
		writer.WriteEndObject();
	}

	static void WriteSourceRange(Utf8JsonWriter writer, string propertyName, CDebugSourceRange range)
	{
		writer.WritePropertyName(propertyName);
		writer.WriteStartObject();
		writer.WriteString("file", range.File);
		writer.WriteNumber("startLine", range.StartLine);
		writer.WriteNumber("startColumn", range.StartColumn);
		writer.WriteNumber("endLine", range.EndLine);
		writer.WriteNumber("endColumn", range.EndColumn);
		writer.WriteEndObject();
	}

	static void WriteGeneratedRange(Utf8JsonWriter writer, string propertyName, CDebugGeneratedRange range)
	{
		writer.WritePropertyName(propertyName);
		writer.WriteStartObject();
		writer.WriteString("file", range.File);
		writer.WriteNumber("line", range.Line);
		WriteStringIfNotNull(writer, "function", range.Function);
		writer.WriteEndObject();
	}

	static void WriteVariable(Utf8JsonWriter writer, CDebugVariableMap variable)
	{
		writer.WriteStartObject();
		writer.WriteString("campName", variable.CampName);
		writer.WriteString("nativeName", variable.NativeName);
		WriteStringIfNotNull(writer, "type", variable.Type);
		writer.WriteString("kind", variable.Kind);
		writer.WriteEndObject();
	}

	static void WriteStringIfNotNull(Utf8JsonWriter writer, string propertyName, string? value)
	{
		if (value is not null)
			writer.WriteString(propertyName, value);
	}
}
