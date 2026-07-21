using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Camp.Compiler;

public sealed record CampTestResults(CampTestResultSummary Summary, IReadOnlyList<CampTestResultEntry> Tests);

public sealed record CampTestResultSummary(int Passed, int Failed, int Skipped, int Invalid, int Error, int Total);

public sealed record CampTestResultEntry(
	string Id,
	string Name,
	string QualifiedName,
	string Sourcefile,
	int Sourceline,
	string Summary,
	string Outcome,
	double DurationMs,
	CampTestFailure? Failure);

public sealed record CampTestFailure(string Kind, string Message, string Sourcefile, int Sourceline);

internal sealed record CampTestHarnessEvent(int Index, string Outcome, double DurationMs, CampTestFailure? Failure);

public static class CampTestResultsFactory
{
	internal static CampTestResults FromHarnessEvents(IReadOnlyList<CampTestManifestEntry> selectedTests, IReadOnlyList<CampTestHarnessEvent> events, int harnessExitCode, string? harnessError)
	{
		Dictionary<int, CampTestHarnessEvent> byIndex = [];
		foreach (CampTestHarnessEvent harnessEvent in events)
			if (!byIndex.ContainsKey(harnessEvent.Index))
				byIndex[harnessEvent.Index] = harnessEvent;

		List<CampTestResultEntry> results = [];
		for (int i = 0; i < selectedTests.Count; i++)
		{
			CampTestManifestEntry test = selectedTests[i];
			if (!byIndex.TryGetValue(i, out CampTestHarnessEvent? harnessEvent))
			{
				results.Add(CreateErrorResult(test, harnessError ?? (harnessExitCode == 0
					? "test harness did not report a result"
					: $"test harness exited with code {harnessExitCode} before reporting a result")));
				continue;
			}

			results.Add(harnessEvent.Outcome switch
			{
				"passed" => CreateResult(test, "passed", harnessEvent.DurationMs, failure: null),
				"skipped" => CreateResult(test, "skipped", harnessEvent.DurationMs, failure: null),
				"invalid" => CreateResult(test, "invalid", harnessEvent.DurationMs, new CampTestFailure(
					"invalid-test-signature",
					"built-in tests must have the signature void name(thrown Assertion*)",
					test.Sourcefile,
					test.Sourceline)),
				"failed" => CreateResult(test, "failed", harnessEvent.DurationMs, harnessEvent.Failure ?? new CampTestFailure(
					"assertion",
					"",
					test.Sourcefile,
					test.Sourceline)),
				_ => CreateErrorResult(test, "test harness reported unknown outcome '" + harnessEvent.Outcome + "'")
			});
		}

		return new CampTestResults(CreateSummary(results), results);
	}

	internal static CampTestResults InfrastructureError(IReadOnlyList<CampTestManifestEntry> selectedTests, string message)
	{
		if (selectedTests.Count == 0)
			return new CampTestResults(new CampTestResultSummary(0, 0, 0, 0, 1, 1), []);
		List<CampTestResultEntry> results = selectedTests.Select(test => CreateErrorResult(test, message)).ToList();
		return new CampTestResults(CreateSummary(results), results);
	}

	static CampTestResultEntry CreateResult(CampTestManifestEntry test, string outcome, double durationMs, CampTestFailure? failure)
	{
		return new CampTestResultEntry(
			test.Id,
			test.Name,
			test.QualifiedName,
			test.Sourcefile,
			test.Sourceline,
			test.Summary,
			outcome,
			durationMs,
			failure);
	}

	static CampTestResultEntry CreateErrorResult(CampTestManifestEntry test, string message)
	{
		return CreateResult(test, "error", 0, new CampTestFailure("test-runner-error", message, test.Sourcefile, test.Sourceline));
	}

	static CampTestResultSummary CreateSummary(IReadOnlyList<CampTestResultEntry> results)
	{
		return new CampTestResultSummary(
			results.Count(static result => result.Outcome == "passed"),
			results.Count(static result => result.Outcome == "failed"),
			results.Count(static result => result.Outcome == "skipped"),
			results.Count(static result => result.Outcome == "invalid"),
			results.Count(static result => result.Outcome == "error"),
			results.Count);
	}
}

public static class CampTestResultsJsonSerializer
{
	public static string Serialize(CampTestResults results)
	{
		ArgumentNullException.ThrowIfNull(results);
		using MemoryStream stream = new();
		using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
		{
			json.WriteStartObject();
			json.WriteString("format", "camp.test-results");
			json.WriteNumber("version", 1);
			json.WriteStartObject("summary");
			json.WriteNumber("passed", results.Summary.Passed);
			json.WriteNumber("failed", results.Summary.Failed);
			json.WriteNumber("skipped", results.Summary.Skipped);
			json.WriteNumber("invalid", results.Summary.Invalid);
			json.WriteNumber("error", results.Summary.Error);
			json.WriteNumber("total", results.Summary.Total);
			json.WriteEndObject();
			json.WriteStartArray("tests");
			foreach (CampTestResultEntry test in results.Tests)
			{
				json.WriteStartObject();
				json.WriteString("id", test.Id);
				json.WriteString("name", test.Name);
				json.WriteString("qualifiedName", test.QualifiedName);
				json.WriteString("sourcefile", test.Sourcefile);
				json.WriteNumber("sourceline", test.Sourceline);
				json.WriteString("summary", test.Summary);
				json.WriteString("outcome", test.Outcome);
				json.WriteNumber("durationMs", test.DurationMs);
				if (test.Failure is null)
				{
					json.WriteNull("failure");
				}
				else
				{
					json.WriteStartObject("failure");
					json.WriteString("kind", test.Failure.Kind);
					json.WriteString("message", test.Failure.Message);
					json.WriteString("sourcefile", test.Failure.Sourcefile);
					json.WriteNumber("sourceline", test.Failure.Sourceline);
					json.WriteEndObject();
				}
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}
		string text = Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
		return text.EndsWith('\n') ? text : text + "\n";
	}

	public static bool TryParse(string text, out CampTestResults results, out List<string> diagnostics)
	{
		diagnostics = [];
		results = new CampTestResults(new CampTestResultSummary(0, 0, 0, 0, 0, 0), []);
		try
		{
			using JsonDocument document = JsonDocument.Parse(text);
			JsonElement root = document.RootElement;
			if (!root.TryGetProperty("format", out JsonElement format) || format.GetString() != "camp.test-results")
				diagnostics.Add("test results format must be camp.test-results.");
			if (!root.TryGetProperty("version", out JsonElement version) || version.GetInt32() != 1)
				diagnostics.Add("test results version must be 1.");
			List<CampTestResultEntry> tests = [];
			if (root.TryGetProperty("tests", out JsonElement testsElement) && testsElement.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement test in testsElement.EnumerateArray())
				{
					CampTestFailure? failure = null;
					if (test.TryGetProperty("failure", out JsonElement failureElement) && failureElement.ValueKind == JsonValueKind.Object)
					{
						failure = new CampTestFailure(
							GetString(failureElement, "kind"),
							GetString(failureElement, "message"),
							GetString(failureElement, "sourcefile"),
							GetInt(failureElement, "sourceline"));
					}
					tests.Add(new CampTestResultEntry(
						GetString(test, "id"),
						GetString(test, "name"),
						GetString(test, "qualifiedName"),
						GetString(test, "sourcefile"),
						GetInt(test, "sourceline"),
						GetString(test, "summary"),
						GetString(test, "outcome"),
						GetDouble(test, "durationMs"),
						failure));
				}
			}
			else
				diagnostics.Add("test results must contain a tests array.");
			results = new CampTestResults(CreateSummary(tests), tests);
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
		{
			diagnostics.Add("test results could not be parsed: " + ex.Message);
		}
		return diagnostics.Count == 0;
	}

	static CampTestResultSummary CreateSummary(IReadOnlyList<CampTestResultEntry> tests)
	{
		return new CampTestResultSummary(
			tests.Count(static test => test.Outcome == "passed"),
			tests.Count(static test => test.Outcome == "failed"),
			tests.Count(static test => test.Outcome == "skipped"),
			tests.Count(static test => test.Outcome == "invalid"),
			tests.Count(static test => test.Outcome == "error"),
			tests.Count);
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

	static double GetDouble(JsonElement element, string name)
	{
		return element.TryGetProperty(name, out JsonElement property) && property.TryGetDouble(out double value) ? value : 0;
	}
}

public static class CampTestResultDiagnosticService
{
	public static bool TryCreateFromFile(string path, out IReadOnlyList<CampSourceDiagnostic> diagnostics, out List<string> importDiagnostics)
	{
		diagnostics = [];
		importDiagnostics = [];
		try
		{
			if (!CampTestResultsJsonSerializer.TryParse(File.ReadAllText(path), out CampTestResults results, out importDiagnostics))
				return false;
			diagnostics = Create(results);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			importDiagnostics.Add($"{path}: {ex.Message}");
			return false;
		}
	}

	public static IReadOnlyList<CampSourceDiagnostic> Create(CampTestResults results)
	{
		List<CampSourceDiagnostic> diagnostics = [];
		foreach (CampTestResultEntry test in results.Tests)
		{
			if (test.Failure is not CampTestFailure failure)
				continue;
			string path = string.IsNullOrWhiteSpace(failure.Sourcefile) ? test.Sourcefile : failure.Sourcefile;
			int line = Math.Max(0, (failure.Sourceline == 0 ? test.Sourceline : failure.Sourceline) - 1);
			diagnostics.Add(new CampSourceDiagnostic(
				path,
				new CampTextRange(new CampTextPosition(line, 0), new CampTextPosition(line, 1)),
				string.IsNullOrWhiteSpace(failure.Message) ? test.Outcome : failure.Message,
				failure.Kind,
				DiagnosticSeverity.Error));
		}
		return diagnostics;
	}
}

public static class CampTestResultsTextFormatter
{
	const string Green = "\u001b[32m";
	const string Red = "\u001b[31m";
	const string Reset = "\u001b[0m";

	public static string Format(CampTestResults results, bool color = false)
	{
		ArgumentNullException.ThrowIfNull(results);
		StringBuilder builder = new();
		if (results.Tests.Count == 0)
		{
			builder.AppendLine("camp test: no selected tests");
		}
		else
		{
			foreach (CampTestResultEntry test in results.Tests)
			{
				builder.Append(ColorOutcome(test.Outcome, color));
				builder.Append(": ");
				builder.AppendLine(test.Id);
				if (test.Failure is not null)
				{
					builder.Append("  at ");
					builder.Append(test.Failure.Sourcefile);
					builder.Append(':');
					builder.Append(test.Failure.Sourceline.ToString(CultureInfo.InvariantCulture));
					if (!string.IsNullOrWhiteSpace(test.Failure.Message))
					{
						builder.Append(' ');
						builder.Append(test.Failure.Message);
					}
					builder.AppendLine();
				}
			}
		}
		builder.Append("test summary: ");
		builder.Append(results.Summary.Passed.ToString(CultureInfo.InvariantCulture));
		builder.Append(" passed, ");
		string failed = results.Summary.Failed.ToString(CultureInfo.InvariantCulture) + " failed";
		builder.Append(color && results.Summary.Failed > 0 ? Red + failed + Reset : failed);
		builder.Append(", ");
		builder.Append(results.Summary.Skipped.ToString(CultureInfo.InvariantCulture));
		builder.Append(" skipped, ");
		builder.Append(results.Summary.Invalid.ToString(CultureInfo.InvariantCulture));
		builder.Append(" invalid, ");
		builder.Append(results.Summary.Error.ToString(CultureInfo.InvariantCulture));
		builder.Append(" error, ");
		builder.Append(results.Summary.Total.ToString(CultureInfo.InvariantCulture));
		builder.AppendLine(" total");
		return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	static string ColorOutcome(string outcome, bool color)
	{
		if (!color)
			return outcome;
		return outcome switch
		{
			"passed" => Green + outcome + Reset,
			"failed" => Red + outcome + Reset,
			_ => outcome
		};
	}
}

public static class CampCoverageResultsTextFormatter
{
	public static string Format(CampCoverageResults results)
	{
		ArgumentNullException.ThrowIfNull(results);
		StringBuilder builder = new();
		builder.Append("coverage summary: ");
		AppendMetric(builder, "lines", results.Line);
		builder.Append(", ");
		AppendMetric(builder, "functions", results.Function);
		builder.AppendLine();
		return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	static void AppendMetric(StringBuilder builder, string label, CampCoverageMetric metric)
	{
		builder.Append(metric.Covered.ToString(CultureInfo.InvariantCulture));
		builder.Append('/');
		builder.Append(metric.Total.ToString(CultureInfo.InvariantCulture));
		builder.Append(' ');
		builder.Append(label);
		builder.Append(" covered (");
		builder.Append(metric.Percent.ToString("0.0", CultureInfo.InvariantCulture));
		builder.Append("%)");
	}
}

internal static class CampTestHarnessEventParser
{
	public static bool TryRead(string path, out List<CampTestHarnessEvent> events, out List<string> diagnostics)
	{
		events = [];
		diagnostics = [];
		if (!File.Exists(path))
		{
			diagnostics.Add($"Test harness event file '{path}' was not created.");
			return false;
		}

		int lineNumber = 0;
		foreach (string line in File.ReadLines(path))
		{
			lineNumber++;
			if (line.Length == 0)
				continue;
			string[] parts = line.Split('\t');
			if (parts.Length < 3)
			{
				diagnostics.Add($"{path}({lineNumber}): invalid test harness event row.");
				continue;
			}
			if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
			{
				diagnostics.Add($"{path}({lineNumber}): invalid test harness event index.");
				continue;
			}
			if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double durationMs))
			{
				diagnostics.Add($"{path}({lineNumber}): invalid test harness event duration.");
				continue;
			}
			CampTestFailure? failure = null;
			if (parts[0] == "failed")
			{
				if (parts.Length != 6 || !int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out int sourceline))
				{
					diagnostics.Add($"{path}({lineNumber}): invalid failed test harness event row.");
					continue;
				}
				failure = new CampTestFailure("assertion", Unescape(parts[3]), Unescape(parts[4]), sourceline);
			}
			else if (parts.Length != 3)
			{
				diagnostics.Add($"{path}({lineNumber}): invalid test harness event row.");
				continue;
			}
			events.Add(new CampTestHarnessEvent(index, parts[0], durationMs, failure));
		}
		return diagnostics.Count == 0;
	}

	static string Unescape(string value)
	{
		StringBuilder builder = new();
		for (int i = 0; i < value.Length; i++)
		{
			char c = value[i];
			if (c != '\\' || i + 1 >= value.Length)
			{
				builder.Append(c);
				continue;
			}
			char escaped = value[++i];
			builder.Append(escaped switch
			{
				'n' => '\n',
				'r' => '\r',
				't' => '\t',
				'\\' => '\\',
				_ => escaped
			});
		}
		return builder.ToString();
	}
}
