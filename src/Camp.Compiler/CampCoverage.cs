using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Camp.Compiler;

public enum CampCoverageCounterKind
{
	Line,
	Function
}

public sealed record CampCoverageCounter(int Id, CampCoverageCounterKind Kind, int FileId, int Line, int NameId);

public sealed record CampCoverageMap(
	IReadOnlyDictionary<int, string> Files,
	IReadOnlyDictionary<int, string> Names,
	IReadOnlyList<CampCoverageCounter> Counters);

public sealed record CampCoverageMetric(int Covered, int Total)
{
	public double Percent => Total == 0 ? 100.0 : Math.Round((double)Covered * 100.0 / Total, 1);
}

public sealed record CampCoverageFileResult(
	string Path,
	CampCoverageMetric Line,
	CampCoverageMetric Function,
	IReadOnlyList<int> UncoveredLines);

public sealed record CampCoverageResults(
	CampCoverageMetric Line,
	CampCoverageMetric Function,
	IReadOnlyList<CampCoverageFileResult> Files);

public sealed class CampCoverageMapBuilder
{
	readonly Compilation compilation;
	readonly Module module;
	readonly DeclarationParticipation participation;
	readonly SourcefilePathMapper sourcefilePathMapper;
	readonly Dictionary<FunctionDefinition, TypeDefinition> containingTypes;
	readonly Dictionary<string, int> fileIds = new(StringComparer.Ordinal);
	readonly Dictionary<int, string> filesById = [];
	readonly Dictionary<string, int> nameIds = new(StringComparer.Ordinal);
	readonly Dictionary<int, string> namesById = [];
	readonly List<CampCoverageCounter> counters = [];
	readonly Dictionary<FunctionDefinition, int> functionCounters = new(ReferenceEqualityComparer.Instance);
	readonly Dictionary<LineCounterKey, int> lineCounters = [];
	readonly HashSet<FunctionDefinition> instrumentableFunctions = new(ReferenceEqualityComparer.Instance);
	readonly HashSet<FunctionDefinition> nonInstrumentableFunctions = new(ReferenceEqualityComparer.Instance);

	public CampCoverageMapBuilder(Compilation compilation)
	{
		this.compilation = compilation;
		module = compilation.SharedModule ?? new Module();
		participation = new DeclarationParticipation(module);
		sourcefilePathMapper = new SourcefilePathMapper(module.SourcefilePathMode, module.SourcefileDefaultRoot, module.SourcefileRoots);
		containingTypes = BuildContainingTypeMap(module);
	}

	public int CounterCount => counters.Count;

	public bool TryAddFunctionCounter(FunctionDefinition function, out int counterId, out string? diagnostic)
	{
		if (functionCounters.TryGetValue(function, out counterId))
		{
			diagnostic = null;
			return true;
		}
		if (!IsInstrumentableFunction(function, out SourceFile? _, out TokenRange range, out diagnostic))
			return false;

		int fileId = GetFileId(function, range, out diagnostic);
		if (fileId == 0)
			return false;
		int nameId = GetNameId(GetQualifiedFunctionName(function));
		counterId = counters.Count;
		counters.Add(new CampCoverageCounter(counterId, CampCoverageCounterKind.Function, fileId, range.StartLineNumber, nameId));
		functionCounters[function] = counterId;
		diagnostic = null;
		return true;
	}

	public bool TryAddLineCounter(FunctionDefinition function, Statement statement, out int counterId, out string? diagnostic)
	{
		counterId = 0;
		if (!IsExecutableLineStatement(statement))
		{
			diagnostic = null;
			return false;
		}
		if (!IsInstrumentableLineFunction(function, out FunctionDefinition coverageFunction, out diagnostic))
			return false;
		if (!CCodeEmitter.TryGetNodeSourceRange(statement, out TokenRange range))
		{
			diagnostic = null;
			return false;
		}
		if (!TryGetSourceFile(range, out SourceFile? file) || file!.IsApiHeader || IsGeneratedSource(file))
		{
			diagnostic = null;
			return false;
		}

		int fileId = GetFileId(file!, range, out diagnostic);
		if (fileId == 0)
			return false;
		int nameId = GetNameId(GetQualifiedFunctionName(coverageFunction));
		LineCounterKey key = new(coverageFunction, fileId, range.StartLineNumber, nameId);
		if (lineCounters.TryGetValue(key, out counterId))
			return true;

		counterId = counters.Count;
		counters.Add(new CampCoverageCounter(counterId, CampCoverageCounterKind.Line, fileId, range.StartLineNumber, nameId));
		lineCounters[key] = counterId;
		diagnostic = null;
		return true;
	}

	bool IsInstrumentableLineFunction(FunctionDefinition function, out FunctionDefinition coverageFunction, out string? diagnostic)
	{
		coverageFunction = GetCoverageSourceFunction(function);
		if (ReferenceEquals(coverageFunction, function))
			return IsInstrumentableFunction(function, out _, out _, out diagnostic);

		diagnostic = null;
		if (compilation.CoverageInstrumentationMode == CoverageInstrumentationMode.Disabled
			|| function.Body is null
			|| function.Extern is not null
			|| function.IsAsync
			|| participation.IsTestOnly(coverageFunction)
			|| UnsupportedAvailability.IsUnsupported(coverageFunction))
		{
			return false;
		}
		return true;
	}

	public CampCoverageMap ToMap()
	{
		return new CampCoverageMap(
			filesById.OrderBy(static pair => pair.Key).ToDictionary(static pair => pair.Key, static pair => pair.Value),
			namesById.OrderBy(static pair => pair.Key).ToDictionary(static pair => pair.Key, static pair => pair.Value),
			[.. counters]);
	}

	bool IsInstrumentableFunction(FunctionDefinition function, out SourceFile? file, out TokenRange range, out string? diagnostic)
	{
		file = null;
		range = default;
		diagnostic = null;
		if (instrumentableFunctions.Contains(function))
		{
			CCodeEmitter.TryGetNodeSourceRange(function, out range);
			TryGetSourceFile(range, out file);
			return true;
		}
		if (nonInstrumentableFunctions.Contains(function))
			return false;

		if (compilation.CoverageInstrumentationMode == CoverageInstrumentationMode.Disabled
			|| function.Body is null
			|| function.Extern is not null
			|| function.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor
			|| function.Name.StartsWith("~", StringComparison.Ordinal)
			|| function.IsAsync
			|| function.IteratorKind != IteratorKind.None
			|| function.GeneratedInfo is not null
			|| function.Provenance?.Category is GeneratedDeclarationCategory category && category != GeneratedDeclarationCategory.None
			|| participation.IsTestOnly(function)
			|| UnsupportedAvailability.IsUnsupported(function)
			|| !CCodeEmitter.TryGetNodeSourceRange(function, out range)
			|| !TryGetSourceFile(range, out file)
			|| file!.IsApiHeader
			|| IsGeneratedSource(file))
		{
			nonInstrumentableFunctions.Add(function);
			return false;
		}

		instrumentableFunctions.Add(function);
		return true;
	}

	bool TryGetSourceFile(TokenRange range, out SourceFile? file)
	{
		if (module.SourceFiles.TryGetValue(range.Sequence, out file))
			return true;
		file = compilation.Files.FirstOrDefault(candidate => ReferenceEquals(candidate.Tokens, range.Sequence));
		return file is not null;
	}

	int GetFileId(FunctionDefinition function, TokenRange range, out string? diagnostic)
	{
		if (!TryGetSourceFile(range, out SourceFile? file))
		{
			diagnostic = null;
			return 0;
		}
		return GetFileId(file!, range, out diagnostic);
	}

	int GetFileId(SourceFile file, TokenRange range, out string? diagnostic)
	{
		string physicalPath = string.IsNullOrWhiteSpace(file.FullPath) ? file.Path : file.FullPath!;
		SourcefilePathMapResult mapResult = sourcefilePathMapper.Map(physicalPath);
		if (!mapResult.Success)
		{
			diagnostic = mapResult.Diagnostic ?? $"Source file '{physicalPath}' could not be mapped for coverage.";
			return 0;
		}

		string mappedPath = mapResult.Value ?? file.Path;
		if (fileIds.TryGetValue(mappedPath, out int existing))
		{
			diagnostic = null;
			return existing;
		}

		int id = fileIds.Count + 1;
		fileIds[mappedPath] = id;
		filesById[id] = mappedPath;
		diagnostic = null;
		_ = range;
		return id;
	}

	int GetNameId(string name)
	{
		if (nameIds.TryGetValue(name, out int existing))
			return existing;
		int id = nameIds.Count + 1;
		nameIds[name] = id;
		namesById[id] = name;
		return id;
	}

	string GetQualifiedFunctionName(FunctionDefinition function)
	{
		function = GetCoverageSourceFunction(function);
		string name = GetVisibleFunctionName(function);
		string? namespaceName = function.Namespace;
		if (string.IsNullOrWhiteSpace(namespaceName)
			&& !function.NamespaceAssigned
			&& CCodeEmitter.TryGetNodeSourceRange(function, out TokenRange range)
			&& module.SourceNamespaces.TryGetValue(range.Sequence, out string? sourceNamespace))
			namespaceName = sourceNamespace;
		string qualified = containingTypes.TryGetValue(function, out TypeDefinition? type)
			? (string.IsNullOrWhiteSpace(type.Name) ? name : type.Name + "." + name)
			: name;
		return string.IsNullOrWhiteSpace(namespaceName) ? qualified : namespaceName + "::" + qualified;
	}

	FunctionDefinition GetCoverageSourceFunction(FunctionDefinition function)
	{
		if (function.GeneratedInfo?.Category == GeneratedDeclarationCategory.Iterator
			&& containingTypes.TryGetValue(function, out TypeDefinition? stateType)
			&& stateType.GeneratedInfo?.Category == GeneratedDeclarationCategory.Iterator
			&& stateType.GeneratedInfo.Source is FunctionDefinition sourceFunction)
		{
			return sourceFunction;
		}
		if (function.GeneratedInfo is { Category: GeneratedDeclarationCategory.Lifecycle, Source: FunctionDefinition constructor }
			&& function.Name == "op_initnew"
			&& constructor.Modifier == FunctionModifier.Constructor)
		{
			return constructor;
		}
		return function;
	}

	static string GetVisibleFunctionName(FunctionDefinition function)
	{
		if (function.Modifier == FunctionModifier.Constructor)
			return "create";
		if (function.Modifier == FunctionModifier.Destructor || function.Name.StartsWith("~", StringComparison.Ordinal))
			return "destroy";
		return SymbolNameService.CallableName(function).Value.TrimStart('~');
	}

	static bool IsGeneratedSource(SourceFile file)
	{
		return file.Path.StartsWith("$", StringComparison.Ordinal);
	}

	static bool IsExecutableLineStatement(Statement statement)
	{
		return statement is not EmptyStatement
			and not BlockStatement
			and not CaseStatement
			and not DefaultStatement
			and not LabelStatement;
	}

	static Dictionary<FunctionDefinition, TypeDefinition> BuildContainingTypeMap(Module module)
	{
		Dictionary<FunctionDefinition, TypeDefinition> result = new(ReferenceEqualityComparer.Instance);
		foreach (Definition definition in module.Definitions)
		{
			if (definition is not TypeDefinition type)
				continue;
			foreach (FunctionDefinition function in GetTypeFunctions(type))
				result[function] = type;
		}
		return result;
	}

	static IEnumerable<FunctionDefinition> GetTypeFunctions(TypeDefinition type)
	{
		return type switch
		{
			ClassDefinition classDefinition => classDefinition.Functions,
			StructDefinition structDefinition => structDefinition.Functions,
			InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
			EnumDefinition enumDefinition => enumDefinition.Functions,
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions,
			ParamsDefinition paramsDefinition => paramsDefinition.Functions,
			_ => []
		};
	}

	readonly record struct LineCounterKey(FunctionDefinition Function, int FileId, int Line, int NameId);
}

public static class CampCoverageMapCsvSerializer
{
	public static string Serialize(CampCoverageMap map)
	{
		StringBuilder builder = new();
		builder.AppendLine("v,1");
		foreach ((int id, string path) in map.Files.OrderBy(static pair => pair.Key))
			WriteRow(builder, "p", id.ToString(CultureInfo.InvariantCulture), path);
		foreach ((int id, string name) in map.Names.OrderBy(static pair => pair.Key))
			WriteRow(builder, "n", id.ToString(CultureInfo.InvariantCulture), name);
		foreach (CampCoverageCounter counter in map.Counters.OrderBy(static counter => counter.Id))
			WriteRow(
				builder,
				"c",
				counter.Id.ToString(CultureInfo.InvariantCulture),
				counter.Kind == CampCoverageCounterKind.Function ? "f" : "l",
				counter.FileId.ToString(CultureInfo.InvariantCulture),
				counter.Line.ToString(CultureInfo.InvariantCulture),
				counter.NameId.ToString(CultureInfo.InvariantCulture));
		return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	public static bool TryParse(string text, out CampCoverageMap map, out List<string> diagnostics)
	{
		Dictionary<int, string> files = [];
		Dictionary<int, string> names = [];
		List<CampCoverageCounter> counters = [];
		diagnostics = [];
		int version = 0;
		int lineNumber = 0;
		foreach (string rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
		{
			lineNumber++;
			if (rawLine.Length == 0)
				continue;
			List<string> row = ParseRow(rawLine, diagnostics, lineNumber);
			if (row.Count == 0)
				continue;
			switch (row[0])
			{
				case "v":
					if (row.Count != 2 || !int.TryParse(row[1], NumberStyles.None, CultureInfo.InvariantCulture, out version) || version != 1)
						diagnostics.Add($"coverage map line {lineNumber}: expected v,1.");
					break;
				case "p":
					if (row.Count != 3 || !TryParseNonNegative(row[1], out int fileId))
					{
						diagnostics.Add($"coverage map line {lineNumber}: invalid source path row.");
						break;
					}
					files[fileId] = row[2];
					break;
				case "n":
					if (row.Count != 3 || !TryParseNonNegative(row[1], out int nameId))
					{
						diagnostics.Add($"coverage map line {lineNumber}: invalid function name row.");
						break;
					}
					names[nameId] = row[2];
					break;
				case "c":
					if (row.Count != 6
						|| !TryParseNonNegative(row[1], out int counterId)
						|| row[2] is not ("l" or "f")
						|| !TryParseNonNegative(row[3], out int counterFileId)
						|| !TryParseNonNegative(row[4], out int sourceLine)
						|| !TryParseNonNegative(row[5], out int counterNameId))
					{
						diagnostics.Add($"coverage map line {lineNumber}: invalid counter row.");
						break;
					}
					counters.Add(new CampCoverageCounter(counterId, row[2] == "f" ? CampCoverageCounterKind.Function : CampCoverageCounterKind.Line, counterFileId, sourceLine, counterNameId));
					break;
				default:
					diagnostics.Add($"coverage map line {lineNumber}: unknown row kind '{row[0]}'.");
					break;
			}
		}
		if (version != 1)
			diagnostics.Add("coverage map version row is missing.");
		map = new CampCoverageMap(files, names, counters);
		return diagnostics.Count == 0;
	}

	static void WriteRow(StringBuilder builder, params string[] fields)
	{
		for (int i = 0; i < fields.Length; i++)
		{
			if (i > 0)
				builder.Append(',');
			WriteField(builder, fields[i]);
		}
		builder.AppendLine();
	}

	static void WriteField(StringBuilder builder, string field)
	{
		bool quote = field.Contains(',', StringComparison.Ordinal)
			|| field.Contains('"', StringComparison.Ordinal)
			|| field.Contains('\n', StringComparison.Ordinal)
			|| field.Contains('\r', StringComparison.Ordinal);
		if (!quote)
		{
			builder.Append(field);
			return;
		}
		builder.Append('"');
		builder.Append(field.Replace("\"", "\"\"", StringComparison.Ordinal));
		builder.Append('"');
	}

	static List<string> ParseRow(string line, List<string> diagnostics, int lineNumber)
	{
		List<string> fields = [];
		StringBuilder current = new();
		bool quoted = false;
		for (int i = 0; i < line.Length; i++)
		{
			char ch = line[i];
			if (quoted)
			{
				if (ch == '"')
				{
					if (i + 1 < line.Length && line[i + 1] == '"')
					{
						current.Append('"');
						i++;
					}
					else
						quoted = false;
				}
				else
					current.Append(ch);
				continue;
			}
			if (ch == ',')
			{
				fields.Add(current.ToString());
				current.Clear();
			}
			else if (ch == '"' && current.Length == 0)
				quoted = true;
			else
				current.Append(ch);
		}
		if (quoted)
			diagnostics.Add($"coverage map line {lineNumber}: unterminated quoted field.");
		fields.Add(current.ToString());
		return fields;
	}

	static bool TryParseNonNegative(string value, out int parsed)
	{
		return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;
	}
}

public static class CampCoverageRuntimeSourceGenerator
{
	public static string EnvironmentVariableName(string projectName)
	{
		string sanitized = SanitizeForEnvironment(projectName).ToUpperInvariant();
		return "CAMP_COVERAGE_COUNTS_" + sanitized;
	}

	public static string CounterSymbol(string projectName) => "__camp_coverage_" + SanitizeForC(projectName) + "_counters";
	public static string TouchSymbol(string projectName) => "__camp_coverage_" + SanitizeForC(projectName) + "_touch";

	public static string Generate(string projectName, int counterCount)
	{
		string counterSymbol = CounterSymbol(projectName);
		string touchSymbol = TouchSymbol(projectName);
		string envName = EnvironmentVariableName(projectName);
		string count = Math.Max(1, counterCount).ToString(CultureInfo.InvariantCulture);
		string exposedCount = counterCount.ToString(CultureInfo.InvariantCulture);
		return $$"""
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

uint64_t {{counterSymbol}}[{{count}}] = {0};
static const unsigned int __camp_coverage_counter_count = {{exposedCount}}u;

static void __camp_coverage_write_file(const char *path)
{
	if (path == 0 || path[0] == 0)
		return;
	FILE *file = fopen(path, "wb");
	if (file == 0)
		return;
	fprintf(file, "v,1\n");
	for (unsigned int i = 0; i < __camp_coverage_counter_count; i++)
		fprintf(file, "r,%u,%llu\n", i, (unsigned long long){{counterSymbol}}[i]);
	fclose(file);
}

static void __camp_coverage_write_at_exit(void)
{
	__camp_coverage_write_file(getenv("{{envName}}"));
}

void {{touchSymbol}}(void)
{
	static int registered = 0;
	if (!registered)
	{
		registered = 1;
		atexit(__camp_coverage_write_at_exit);
	}
}
""".Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	static string SanitizeForC(string value)
	{
		StringBuilder builder = new();
		foreach (char ch in value)
			builder.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
		return builder.Length == 0 ? "camp" : builder.ToString();
	}

	static string SanitizeForEnvironment(string value)
	{
		StringBuilder builder = new();
		foreach (char ch in value)
			builder.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
		return builder.Length == 0 ? "CAMP" : builder.ToString();
	}
}

public static class CampCoverageCounterFile
{
	public static bool TryRead(string path, out Dictionary<int, ulong> counts, out List<string> diagnostics)
	{
		counts = [];
		diagnostics = [];
		if (!File.Exists(path))
		{
			diagnostics.Add($"coverage count file '{path}' was not produced.");
			return false;
		}

		int version = 0;
		int lineNumber = 0;
		foreach (string line in File.ReadAllLines(path))
		{
			lineNumber++;
			if (string.IsNullOrWhiteSpace(line))
				continue;
			string[] parts = line.Split(',');
			if (parts.Length == 2 && parts[0] == "v")
			{
				if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out version) || version != 1)
					diagnostics.Add($"coverage count line {lineNumber}: expected v,1.");
				continue;
			}
			if (parts.Length == 3 && parts[0] == "r"
				&& int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int id)
				&& ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong count))
			{
				counts[id] = count;
				continue;
			}
			diagnostics.Add($"coverage count line {lineNumber}: invalid row.");
		}
		if (version != 1)
			diagnostics.Add("coverage count version row is missing.");
		return diagnostics.Count == 0;
	}
}

public static class CampCoverageResultsFactory
{
	public static CampCoverageResults Create(IReadOnlyList<CampCoverageMap> maps, IReadOnlyList<IReadOnlyDictionary<int, ulong>> countSets)
	{
		List<CounterResult> counters = [];
		for (int mapIndex = 0; mapIndex < maps.Count; mapIndex++)
		{
			CampCoverageMap map = maps[mapIndex];
			IReadOnlyDictionary<int, ulong> counts = mapIndex < countSets.Count ? countSets[mapIndex] : new Dictionary<int, ulong>();
			foreach (CampCoverageCounter counter in map.Counters)
			{
				ulong count = counts.TryGetValue(counter.Id, out ulong value) ? value : 0;
				string file = map.Files.TryGetValue(counter.FileId, out string? path) ? path : "";
				string name = map.Names.TryGetValue(counter.NameId, out string? functionName) ? functionName : "";
				counters.Add(new CounterResult(counter, file, name, count));
			}
		}

		List<CounterResult> lineCounters = counters.Where(static counter => counter.Counter.Kind == CampCoverageCounterKind.Line).ToList();
		List<CounterResult> functionCounters = counters.Where(static counter => counter.Counter.Kind == CampCoverageCounterKind.Function).ToList();
		List<CampCoverageFileResult> files = [];
		foreach (IGrouping<string, CounterResult> group in lineCounters.GroupBy(static counter => counter.File, StringComparer.Ordinal).OrderBy(static group => group.Key, StringComparer.Ordinal))
		{
			List<CounterResult> fileLineCounters = group.ToList();
			List<CounterResult> fileFunctionCounters = functionCounters.Where(counter => counter.File == group.Key).ToList();
			files.Add(new CampCoverageFileResult(
				group.Key,
				CreateMetric(fileLineCounters),
				CreateMetric(fileFunctionCounters),
				fileLineCounters.Where(static counter => counter.Count == 0).Select(static counter => counter.Counter.Line).Distinct().OrderBy(static line => line).ToList()));
		}

		foreach (IGrouping<string, CounterResult> group in functionCounters.GroupBy(static counter => counter.File, StringComparer.Ordinal).Where(group => files.All(file => file.Path != group.Key)).OrderBy(static group => group.Key, StringComparer.Ordinal))
		{
			List<CounterResult> fileFunctionCounters = group.ToList();
			files.Add(new CampCoverageFileResult(group.Key, new CampCoverageMetric(0, 0), CreateMetric(fileFunctionCounters), []));
		}

		return new CampCoverageResults(CreateMetric(lineCounters), CreateMetric(functionCounters), files);
	}

	static CampCoverageMetric CreateMetric(IReadOnlyList<CounterResult> counters)
	{
		return new CampCoverageMetric(counters.Count(static counter => counter.Count > 0), counters.Count);
	}

	readonly record struct CounterResult(CampCoverageCounter Counter, string File, string Name, ulong Count);
}

public static class CampCoverageResultsJsonSerializer
{
	public static string Serialize(CampCoverageResults results)
	{
		using MemoryStream stream = new();
		using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
		{
			json.WriteStartObject();
			json.WriteString("format", "camp.coverage-results");
			json.WriteNumber("version", 1);
			json.WriteStartObject("summary");
			WriteMetric(json, "line", results.Line);
			WriteMetric(json, "function", results.Function);
			json.WriteEndObject();
			json.WriteStartArray("files");
			foreach (CampCoverageFileResult file in results.Files)
			{
				json.WriteStartObject();
				json.WriteString("path", file.Path);
				WriteMetric(json, "line", file.Line);
				WriteMetric(json, "function", file.Function);
				json.WriteStartArray("uncoveredLines");
				foreach (int line in file.UncoveredLines)
					json.WriteNumberValue(line);
				json.WriteEndArray();
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}
		string text = Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
		return text.EndsWith('\n') ? text : text + "\n";
	}

	public static bool TryParse(string text, out CampCoverageResults results, out List<string> diagnostics)
	{
		diagnostics = [];
		results = new CampCoverageResults(new CampCoverageMetric(0, 0), new CampCoverageMetric(0, 0), []);
		try
		{
			using JsonDocument document = JsonDocument.Parse(text);
			JsonElement root = document.RootElement;
			if (!root.TryGetProperty("format", out JsonElement format) || format.GetString() != "camp.coverage-results")
				diagnostics.Add("coverage results format must be camp.coverage-results.");
			if (!root.TryGetProperty("version", out JsonElement version) || version.GetInt32() != 1)
				diagnostics.Add("coverage results version must be 1.");
			CampCoverageMetric line = ReadMetric(root, "summary", "line");
			CampCoverageMetric function = ReadMetric(root, "summary", "function");
			List<CampCoverageFileResult> files = [];
			if (root.TryGetProperty("files", out JsonElement filesElement) && filesElement.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement file in filesElement.EnumerateArray())
				{
					List<int> uncoveredLines = [];
					if (file.TryGetProperty("uncoveredLines", out JsonElement uncoveredElement) && uncoveredElement.ValueKind == JsonValueKind.Array)
						foreach (JsonElement uncoveredLine in uncoveredElement.EnumerateArray())
							if (uncoveredLine.TryGetInt32(out int sourceLine))
								uncoveredLines.Add(sourceLine);
					files.Add(new CampCoverageFileResult(
						GetString(file, "path"),
						ReadMetric(file, null, "line"),
						ReadMetric(file, null, "function"),
						uncoveredLines));
				}
			}
			else
				diagnostics.Add("coverage results must contain a files array.");
			results = new CampCoverageResults(line, function, files);
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
		{
			diagnostics.Add("coverage results could not be parsed: " + ex.Message);
		}
		return diagnostics.Count == 0;
	}

	static CampCoverageMetric ReadMetric(JsonElement element, string? containerName, string metricName)
	{
		JsonElement metricRoot = element;
		if (containerName is not null)
		{
			if (!element.TryGetProperty(containerName, out metricRoot))
				return new CampCoverageMetric(0, 0);
		}
		if (!metricRoot.TryGetProperty(metricName, out JsonElement metric))
			return new CampCoverageMetric(0, 0);
		return new CampCoverageMetric(GetInt(metric, "covered"), GetInt(metric, "total"));
	}

	static string GetString(JsonElement element, string name)
	{
		return element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
			? property.GetString() ?? ""
			: "";
	}

	static int GetInt(JsonElement element, string name)
	{
		return element.TryGetProperty(name, out JsonElement property) && property.TryGetInt32(out int value) ? value : 0;
	}

	static void WriteMetric(Utf8JsonWriter json, string name, CampCoverageMetric metric)
	{
		json.WriteStartObject(name);
		json.WriteNumber("covered", metric.Covered);
		json.WriteNumber("total", metric.Total);
		json.WriteNumber("percent", metric.Percent);
		json.WriteEndObject();
	}
}

public enum CampCoverageLineDecorationKind
{
	CoveredExecutableLine,
	UncoveredExecutableLine
}

public sealed record CampCoverageLineDecoration(string Path, int Line, CampCoverageLineDecorationKind Kind);

public static class CampCoverageDecorationService
{
	public static bool TryCreateFromFiles(string coverageMapPath, string coverageResultsPath, out IReadOnlyList<CampCoverageLineDecoration> decorations, out List<string> diagnostics)
	{
		decorations = [];
		diagnostics = [];
		try
		{
			if (!CampCoverageMapCsvSerializer.TryParse(File.ReadAllText(coverageMapPath), out CampCoverageMap map, out List<string> mapDiagnostics))
			{
				diagnostics.AddRange(mapDiagnostics.Select(diagnostic => $"{coverageMapPath}: {diagnostic}"));
				return false;
			}
			if (!CampCoverageResultsJsonSerializer.TryParse(File.ReadAllText(coverageResultsPath), out CampCoverageResults results, out List<string> resultDiagnostics))
			{
				diagnostics.AddRange(resultDiagnostics.Select(diagnostic => $"{coverageResultsPath}: {diagnostic}"));
				return false;
			}
			decorations = Create(map, results);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			diagnostics.Add(ex.Message);
			return false;
		}
	}

	public static IReadOnlyList<CampCoverageLineDecoration> Create(CampCoverageMap map, CampCoverageResults results)
	{
		Dictionary<string, HashSet<int>> uncoveredByPath = results.Files.ToDictionary(
			static file => file.Path,
			static file => file.UncoveredLines.ToHashSet(),
			StringComparer.Ordinal);
		List<CampCoverageLineDecoration> decorations = [];
		foreach (IGrouping<(int FileId, int Line), CampCoverageCounter> group in map.Counters
			.Where(static counter => counter.Kind == CampCoverageCounterKind.Line)
			.GroupBy(static counter => (counter.FileId, counter.Line))
			.OrderBy(static group => group.Key.FileId)
			.ThenBy(static group => group.Key.Line))
		{
			if (!map.Files.TryGetValue(group.Key.FileId, out string? path))
				continue;
			bool uncovered = uncoveredByPath.TryGetValue(path, out HashSet<int>? lines) && lines.Contains(group.Key.Line);
			decorations.Add(new CampCoverageLineDecoration(
				path,
				group.Key.Line,
				uncovered ? CampCoverageLineDecorationKind.UncoveredExecutableLine : CampCoverageLineDecorationKind.CoveredExecutableLine));
		}
		return decorations;
	}
}

public static class CampCoverageLcovSerializer
{
	public static string Serialize(IReadOnlyList<CampCoverageMap> maps, IReadOnlyList<IReadOnlyDictionary<int, ulong>> countSets)
	{
		StringBuilder builder = new();
		for (int mapIndex = 0; mapIndex < maps.Count; mapIndex++)
		{
			CampCoverageMap map = maps[mapIndex];
			IReadOnlyDictionary<int, ulong> counts = mapIndex < countSets.Count ? countSets[mapIndex] : new Dictionary<int, ulong>();
			foreach ((int fileId, string path) in map.Files.OrderBy(static pair => pair.Key))
			{
				List<CampCoverageCounter> fileFunctions = map.Counters
					.Where(counter => counter.FileId == fileId && counter.Kind == CampCoverageCounterKind.Function)
					.OrderBy(static counter => counter.Line)
					.ThenBy(static counter => counter.NameId)
					.ToList();
				List<CampCoverageCounter> fileLines = map.Counters
					.Where(counter => counter.FileId == fileId && counter.Kind == CampCoverageCounterKind.Line)
					.OrderBy(static counter => counter.Line)
					.ThenBy(static counter => counter.Id)
					.ToList();
				builder.AppendLine("TN:");
				builder.Append("SF:").AppendLine(path);
				foreach (CampCoverageCounter function in fileFunctions)
				{
					string name = map.Names.TryGetValue(function.NameId, out string? value) ? value : "";
					builder.Append("FN:").Append(function.Line.ToString(CultureInfo.InvariantCulture)).Append(',').AppendLine(name);
				}
				foreach (CampCoverageCounter function in fileFunctions)
				{
					string name = map.Names.TryGetValue(function.NameId, out string? functionName) ? functionName : "";
					ulong count = counts.TryGetValue(function.Id, out ulong functionCount) ? functionCount : 0;
					builder.Append("FNDA:").Append(count.ToString(CultureInfo.InvariantCulture)).Append(',').AppendLine(name);
				}
				builder.Append("FNF:").AppendLine(fileFunctions.Count.ToString(CultureInfo.InvariantCulture));
				builder.Append("FNH:").AppendLine(fileFunctions.Count(function => counts.TryGetValue(function.Id, out ulong value) && value > 0).ToString(CultureInfo.InvariantCulture));
				foreach (IGrouping<int, CampCoverageCounter> lineGroup in fileLines.GroupBy(static counter => counter.Line).OrderBy(static group => group.Key))
				{
					ulong count = 0;
					foreach (CampCoverageCounter counter in lineGroup)
						if (counts.TryGetValue(counter.Id, out ulong value))
							count += value;
					builder.Append("DA:").Append(lineGroup.Key.ToString(CultureInfo.InvariantCulture)).Append(',').AppendLine(count.ToString(CultureInfo.InvariantCulture));
				}
				builder.Append("LF:").AppendLine(fileLines.Count.ToString(CultureInfo.InvariantCulture));
				builder.Append("LH:").AppendLine(fileLines.Count(counter => counts.TryGetValue(counter.Id, out ulong value) && value > 0).ToString(CultureInfo.InvariantCulture));
				builder.AppendLine("end_of_record");
			}
		}
		return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
	}
}
