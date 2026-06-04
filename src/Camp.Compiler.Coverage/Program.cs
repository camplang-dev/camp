using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;

CoverageOptions options = CoverageOptions.Parse(args);
string sourceRoot = FindSourceRoot(Directory.GetCurrentDirectory());
string repositoryRoot = Directory.GetParent(sourceRoot)?.FullName ?? sourceRoot;
string solutionPath = Path.Combine(sourceRoot, "camplang.sln");
string testProjectRoot = Path.Combine(sourceRoot, "Camp.Compiler.TestRunner");
string reportRoot = Path.Combine(Path.Combine(repositoryRoot, "tmp"), "coverage-report");

if (!options.NoTest)
{
	int testExitCode = Run("dotnet", ["test", solutionPath, "--collect:XPlat Code Coverage"], sourceRoot);
	if (testExitCode != 0)
		return testExitCode;
}

string coveragePath = FindLatestCoverageFile(testProjectRoot);
CoverageReport report = CoverageReport.Load(coveragePath);
Directory.CreateDirectory(reportRoot);

string summary = report.FormatTextSummary(coveragePath);
File.WriteAllText(Path.Combine(reportRoot, "Summary.txt"), summary);
File.WriteAllText(Path.Combine(reportRoot, "index.html"), report.FormatHtmlSummary(coveragePath));

Console.WriteLine();
Console.Write(summary);
Console.WriteLine();
Console.WriteLine("Wrote coverage report:");
Console.WriteLine("  " + Path.Combine(reportRoot, "Summary.txt"));
Console.WriteLine("  " + Path.Combine(reportRoot, "index.html"));

if (options.Open)
	OpenHtml(Path.Combine(reportRoot, "index.html"));

return 0;

static int Run(string executable, IReadOnlyList<string> arguments, string workingDirectory)
{
	ProcessStartInfo startInfo = new(executable)
	{
		WorkingDirectory = workingDirectory,
		UseShellExecute = false
	};
	foreach (string argument in arguments)
		startInfo.ArgumentList.Add(argument);

	using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{executable}'.");
	process.WaitForExit();
	return process.ExitCode;
}

static string FindSourceRoot(string start)
{
	DirectoryInfo? directory = new(Path.GetFullPath(start));
	while (directory is not null)
	{
		if (File.Exists(Path.Combine(directory.FullName, "camplang.sln")))
			return directory.FullName;
		string nested = Path.Combine(directory.FullName, "src", "camplang.sln");
		if (File.Exists(nested))
			return Path.Combine(directory.FullName, "src");
		directory = directory.Parent;
	}
	throw new InvalidOperationException("Could not find src/camplang.sln.");
}

static string FindLatestCoverageFile(string testProjectRoot)
{
	string testResults = Path.Combine(testProjectRoot, "TestResults");
	if (!Directory.Exists(testResults))
		throw new InvalidOperationException($"Coverage output directory '{testResults}' does not exist.");

	string? latest = Directory.GetFiles(testResults, "coverage.cobertura.xml", SearchOption.AllDirectories)
		.OrderByDescending(File.GetLastWriteTimeUtc)
		.FirstOrDefault();
	if (latest is null)
		throw new InvalidOperationException($"No coverage.cobertura.xml file was found under '{testResults}'.");
	return latest;
}

static void OpenHtml(string path)
{
	try
	{
		if (OperatingSystem.IsMacOS())
			Run("open", [path], Directory.GetCurrentDirectory());
		else if (OperatingSystem.IsWindows())
			Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
		else
			Run("xdg-open", [path], Directory.GetCurrentDirectory());
	}
	catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
	{
		Console.Error.WriteLine("Could not open coverage report: " + ex.Message);
	}
}

sealed class CoverageOptions
{
	public bool Open { get; private init; }
	public bool NoTest { get; private init; }

	public static CoverageOptions Parse(string[] args)
	{
		bool open = false;
		bool noTest = false;
		foreach (string arg in args)
		{
			switch (arg)
			{
				case "--open" or "-o":
					open = true;
					break;
				case "--no-test":
					noTest = true;
					break;
				default:
					throw new InvalidOperationException($"Unknown option '{arg}'. Expected --open, -o, or --no-test.");
			}
		}
		return new CoverageOptions { Open = open, NoTest = noTest };
	}
}

sealed class CoverageReport
{
	readonly List<CoverageClass> classes;

	CoverageReport(double lineRate, double branchRate, int linesCovered, int linesValid, int branchesCovered, int branchesValid, List<CoverageClass> classes)
	{
		LineRate = lineRate;
		BranchRate = branchRate;
		LinesCovered = linesCovered;
		LinesValid = linesValid;
		BranchesCovered = branchesCovered;
		BranchesValid = branchesValid;
		this.classes = classes;
	}

	public double LineRate { get; }
	public double BranchRate { get; }
	public int LinesCovered { get; }
	public int LinesValid { get; }
	public int BranchesCovered { get; }
	public int BranchesValid { get; }

	public static CoverageReport Load(string path)
	{
		XDocument document = XDocument.Load(path);
		XElement root = document.Root ?? throw new InvalidOperationException("Coverage XML is missing a root element.");
		List<CoverageClass> classes = [];
		foreach (XElement element in root.Descendants("class"))
			classes.Add(CoverageClass.Load(element));

		return new CoverageReport(
			GetDouble(root, "line-rate"),
			GetDouble(root, "branch-rate"),
			GetInt(root, "lines-covered"),
			GetInt(root, "lines-valid"),
			GetInt(root, "branches-covered"),
			GetInt(root, "branches-valid"),
			classes);
	}

	public string FormatTextSummary(string coveragePath)
	{
		StringBuilder builder = new();
		builder.AppendLine("Coverage summary");
		builder.AppendLine("================");
		builder.AppendLine("Coverage file: " + coveragePath);
		builder.AppendLine();
		builder.AppendLine($"Overall line coverage:   {Percent(LineRate)} ({LinesCovered}/{LinesValid})");
		builder.AppendLine($"Overall branch coverage: {Percent(BranchRate)} ({BranchesCovered}/{BranchesValid})");
		builder.AppendLine();
		builder.AppendLine("Compiler areas");
		builder.AppendLine("--------------");
		foreach (CoverageBucket bucket in BuildBuckets())
			builder.AppendLine($"{bucket.Name,-26} line {Percent(bucket.LineRate),8} ({bucket.LinesCovered}/{bucket.LinesValid}), branch {Percent(bucket.BranchRate),8} ({bucket.BranchesCovered}/{bucket.BranchesValid})");
		builder.AppendLine();
		builder.AppendLine("Lowest file coverage");
		builder.AppendLine("--------------------");
		foreach (CoverageClass item in classes.Where(static x => x.LinesValid > 0).OrderBy(static x => x.LineRate).ThenBy(static x => x.Name, StringComparer.Ordinal).Take(10))
			builder.AppendLine($"{item.DisplayName,-48} line {Percent(item.LineRate),8} ({item.LinesCovered}/{item.LinesValid})");
		return builder.ToString();
	}

	public string FormatHtmlSummary(string coveragePath)
	{
		StringBuilder builder = new();
		builder.AppendLine("<!doctype html>");
		builder.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><title>Camp Coverage Report</title>");
		builder.AppendLine("<style>body{font-family:-apple-system,BlinkMacSystemFont,Segoe UI,sans-serif;margin:32px;line-height:1.45;color:#1f2933}table{border-collapse:collapse;margin-top:12px;min-width:720px}th,td{border:1px solid #ccd3dd;padding:6px 10px;text-align:left}th{background:#eef2f6}.num{text-align:right}.bar{width:180px;height:10px;background:#e5e7eb;border-radius:6px;overflow:hidden}.fill{height:100%;background:#2f855a}</style>");
		builder.AppendLine("</head><body>");
		builder.AppendLine("<h1>Camp Coverage Report</h1>");
		builder.AppendLine("<p><strong>Coverage file:</strong> " + WebUtility.HtmlEncode(coveragePath) + "</p>");
		builder.AppendLine("<h2>Overall</h2>");
		builder.AppendLine("<table><tr><th>Metric</th><th>Coverage</th><th>Covered</th><th>Total</th></tr>");
		AddMetricRow(builder, "Lines", LineRate, LinesCovered, LinesValid);
		AddMetricRow(builder, "Branches", BranchRate, BranchesCovered, BranchesValid);
		builder.AppendLine("</table>");
		builder.AppendLine("<h2>Compiler Areas</h2>");
		WriteBucketTable(builder, BuildBuckets());
		builder.AppendLine("<h2>Files</h2>");
		WriteClassTable(builder, classes.Where(static x => x.LinesValid > 0).OrderBy(static x => x.DisplayName, StringComparer.Ordinal));
		builder.AppendLine("</body></html>");
		return builder.ToString();
	}

	List<CoverageBucket> BuildBuckets()
	{
		return [
			BuildBucket("CCodeEmitter", static x => x.Name.Contains("CCodeEmitter", StringComparison.Ordinal) || x.Filename.Contains("CCodeEmitter", StringComparison.Ordinal)),
			BuildBucket("CompilerDriver", static x => x.Name.Contains("CompilerDriver", StringComparison.Ordinal) || x.Filename.Contains("CompilerDriver", StringComparison.Ordinal)),
			BuildBucket("Analyzer", static x => x.Name.Contains("BindableNodeAnalyzer", StringComparison.Ordinal) || x.Filename.Contains("BindableNodeAnalyzer", StringComparison.Ordinal)),
			BuildBucket("Parser/Tokenizer", static x => x.Name.Contains("CampParser", StringComparison.Ordinal) || x.Name.Contains("CampTokenizer", StringComparison.Ordinal) || x.Filename.Contains("CampParser", StringComparison.Ordinal) || x.Filename.Contains("CampTokenizer", StringComparison.Ordinal)),
			BuildBucket("NativeBuildDriver", static x => x.Name.Contains("NativeBuildDriver", StringComparison.Ordinal) || x.Filename.Contains("NativeBuildDriver", StringComparison.Ordinal))
		];
	}

	CoverageBucket BuildBucket(string name, Func<CoverageClass, bool> predicate)
	{
		List<CoverageClass> matches = classes.Where(predicate).ToList();
		return new CoverageBucket(
			name,
			matches.Sum(static x => x.LinesCovered),
			matches.Sum(static x => x.LinesValid),
			matches.Sum(static x => x.BranchesCovered),
			matches.Sum(static x => x.BranchesValid));
	}

	static void WriteBucketTable(StringBuilder builder, IEnumerable<CoverageBucket> buckets)
	{
		builder.AppendLine("<table><tr><th>Area</th><th>Line Coverage</th><th>Lines</th><th>Branch Coverage</th><th>Branches</th></tr>");
		foreach (CoverageBucket bucket in buckets)
		{
			builder.Append("<tr><td>").Append(WebUtility.HtmlEncode(bucket.Name)).Append("</td>");
			AddCoverageCell(builder, bucket.LineRate);
			builder.Append("<td class=\"num\">").Append(bucket.LinesCovered).Append('/').Append(bucket.LinesValid).Append("</td>");
			AddCoverageCell(builder, bucket.BranchRate);
			builder.Append("<td class=\"num\">").Append(bucket.BranchesCovered).Append('/').Append(bucket.BranchesValid).AppendLine("</td></tr>");
		}
		builder.AppendLine("</table>");
	}

	static void WriteClassTable(StringBuilder builder, IEnumerable<CoverageClass> items)
	{
		builder.AppendLine("<table><tr><th>File/Class</th><th>Line Coverage</th><th>Lines</th><th>Branch Coverage</th><th>Branches</th></tr>");
		foreach (CoverageClass item in items)
		{
			builder.Append("<tr><td>").Append(WebUtility.HtmlEncode(item.DisplayName)).Append("</td>");
			AddCoverageCell(builder, item.LineRate);
			builder.Append("<td class=\"num\">").Append(item.LinesCovered).Append('/').Append(item.LinesValid).Append("</td>");
			AddCoverageCell(builder, item.BranchRate);
			builder.Append("<td class=\"num\">").Append(item.BranchesCovered).Append('/').Append(item.BranchesValid).AppendLine("</td></tr>");
		}
		builder.AppendLine("</table>");
	}

	static void AddMetricRow(StringBuilder builder, string name, double rate, int covered, int total)
	{
		builder.Append("<tr><td>").Append(name).Append("</td>");
		AddCoverageCell(builder, rate);
		builder.Append("<td class=\"num\">").Append(covered).Append("</td><td class=\"num\">").Append(total).AppendLine("</td></tr>");
	}

	static void AddCoverageCell(StringBuilder builder, double rate)
	{
		builder.Append("<td class=\"num\">").Append(Percent(rate)).Append("<div class=\"bar\"><div class=\"fill\" style=\"width:")
			.Append(Math.Clamp(rate * 100.0, 0.0, 100.0).ToString("0.##", CultureInfo.InvariantCulture))
			.Append("%\"></div></div></td>");
	}

	static string Percent(double rate)
	{
		return (rate * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%";
	}

	static int GetInt(XElement element, string name)
	{
		return int.TryParse(element.Attribute(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
	}

	static double GetDouble(XElement element, string name)
	{
		return double.TryParse(element.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0.0;
	}
}

sealed record CoverageBucket(string Name, int LinesCovered, int LinesValid, int BranchesCovered, int BranchesValid)
{
	public double LineRate => LinesValid == 0 ? 0.0 : (double)LinesCovered / LinesValid;
	public double BranchRate => BranchesValid == 0 ? 0.0 : (double)BranchesCovered / BranchesValid;
}

sealed class CoverageClass
{
	CoverageClass(string name, string filename, int linesCovered, int linesValid, int branchesCovered, int branchesValid)
	{
		Name = name;
		Filename = filename;
		LinesCovered = linesCovered;
		LinesValid = linesValid;
		BranchesCovered = branchesCovered;
		BranchesValid = branchesValid;
	}

	public string Name { get; }
	public string Filename { get; }
	public int LinesCovered { get; }
	public int LinesValid { get; }
	public int BranchesCovered { get; }
	public int BranchesValid { get; }
	public double LineRate => LinesValid == 0 ? 0.0 : (double)LinesCovered / LinesValid;
	public double BranchRate => BranchesValid == 0 ? 0.0 : (double)BranchesCovered / BranchesValid;
	public string DisplayName => string.IsNullOrWhiteSpace(Filename) ? Name : Path.GetFileName(Filename);

	public static CoverageClass Load(XElement element)
	{
		int linesValid = 0;
		int linesCovered = 0;
		int branchesValid = 0;
		int branchesCovered = 0;
		foreach (XElement line in element.Descendants("line"))
		{
			linesValid++;
			if (int.TryParse(line.Attribute("hits")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hits) && hits > 0)
				linesCovered++;

			string? conditionCoverage = line.Attribute("condition-coverage")?.Value;
			if (conditionCoverage is null)
				continue;
			int open = conditionCoverage.IndexOf('(', StringComparison.Ordinal);
			int slash = conditionCoverage.IndexOf('/', StringComparison.Ordinal);
			int close = conditionCoverage.IndexOf(')', StringComparison.Ordinal);
			if (open < 0 || slash < open || close < slash)
				continue;
			if (int.TryParse(conditionCoverage[(open + 1)..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int covered)
				&& int.TryParse(conditionCoverage[(slash + 1)..close], NumberStyles.Integer, CultureInfo.InvariantCulture, out int valid))
			{
				branchesCovered += covered;
				branchesValid += valid;
			}
		}

		return new CoverageClass(
			element.Attribute("name")?.Value ?? "",
			element.Attribute("filename")?.Value ?? "",
			linesCovered,
			linesValid,
			branchesCovered,
			branchesValid);
	}
}
