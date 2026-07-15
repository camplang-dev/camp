using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Camp.Compiler.Tests;

public static class TestTiming
{
	const int DefaultSlowCount = 20;
	static readonly ConcurrentBag<Entry> Entries = [];

	static TestTiming()
	{
		AppDomain.CurrentDomain.ProcessExit += static (_, _) => WriteReport();
	}

	public static IDisposable Measure(string name)
	{
		return new Measurement(name);
	}

	public static void Record(string name, TimeSpan elapsed)
	{
		Entries.Add(new Entry(name, elapsed));
	}

	static void WriteReport()
	{
		if (Entries.IsEmpty)
			return;
		if (Environment.GetEnvironmentVariable("CAMP_TEST_TIMING") is string value && value.Equals("0", StringComparison.OrdinalIgnoreCase))
			return;

		int count = DefaultSlowCount;
		if (int.TryParse(Environment.GetEnvironmentVariable("CAMP_TEST_TIMING_TOP"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int requested) && requested > 0)
			count = requested;

		Console.Error.WriteLine();
		Console.Error.WriteLine("Slowest Camp test operations:");
		foreach (Entry entry in Entries.OrderByDescending(static entry => entry.Elapsed).Take(count))
			Console.Error.WriteLine(entry.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s " + entry.Name);
		WriteFileReport(count);
	}

	static void WriteFileReport(int count)
	{
		try
		{
			string repositoryRoot = FindRepositoryRoot();
			string path = Environment.GetEnvironmentVariable("CAMP_TEST_TIMING_PATH") ?? Path.Combine(repositoryRoot, "tmp", "camp-test-timing.txt");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllLines(path,
				[
					"Slowest Camp test operations:",
					.. Entries.OrderByDescending(static entry => entry.Elapsed)
						.Take(count)
						.Select(static entry => entry.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s " + entry.Name)
				]);
		}
		catch (Exception)
		{
			// Timing output is diagnostic-only and must never fail the test run.
		}
	}

	static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "src", "camplang.sln")))
				return directory.FullName;
			directory = directory.Parent;
		}
		return Directory.GetCurrentDirectory();
	}

	readonly record struct Entry(string Name, TimeSpan Elapsed);

	sealed class Measurement(string name) : IDisposable
	{
		readonly Stopwatch stopwatch = Stopwatch.StartNew();
		bool disposed;

		public void Dispose()
		{
			if (disposed)
				return;
			disposed = true;
			stopwatch.Stop();
			Record(name, stopwatch.Elapsed);
		}
	}
}
