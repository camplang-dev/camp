using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Camp.Compiler;

// A deliberately small, file-content validated handoff between a normal test
// build and --run-only. It is not a semantic cache: a normal test still owns
// parsing, binding, lowering, and native emission.
internal sealed record CampTestRunCacheRecord(
	int SchemaVersion,
	string CompilerFingerprint,
	string RequestFingerprint,
	string ExecutablePath,
	string ManifestPath,
	IReadOnlyList<CampTestRunCacheInput> Inputs);

internal sealed record CampTestRunCacheInput(string Path, string Fingerprint);

internal static class CampTestRunCache
{
	const int SchemaVersion = 1;

	public static CampTestRunCacheRecord Create(
		CompilerRequest request,
		TargetDefinition target,
		string executablePath,
		string manifestPath,
		IEnumerable<string> inputPaths)
	{
		List<CampTestRunCacheInput> inputs = inputPaths
			.Where(File.Exists)
			.Select(Path.GetFullPath)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
			.Select(path => new CampTestRunCacheInput(path, GetFileFingerprint(path)))
			.ToList();
		return new CampTestRunCacheRecord(
			SchemaVersion,
			GetCompilerFingerprint(),
			GetRequestFingerprint(request, target),
			Path.GetFullPath(executablePath),
			Path.GetFullPath(manifestPath),
			inputs);
	}

	public static bool TryWrite(string path, CampTestRunCacheRecord record, out string? error)
	{
		error = null;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
			string text = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }) + "\n";
			BuildFileIO.WriteTextIfChanged(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			error = ex.Message;
			return false;
		}
	}

	public static bool TryReadValidated(string path, CompilerRequest request, TargetDefinition target, out CampTestRunCacheRecord? record, out string? reason)
	{
		record = null;
		reason = null;
		try
		{
			if (!File.Exists(path))
			{
				reason = "no reusable test build was found";
				return false;
			}
			record = JsonSerializer.Deserialize<CampTestRunCacheRecord>(File.ReadAllText(path));
			if (record is null || record.SchemaVersion != SchemaVersion)
			{
				reason = "the reusable test build has an incompatible cache record";
				return false;
			}
			if (!string.Equals(record.CompilerFingerprint, GetCompilerFingerprint(), StringComparison.Ordinal))
			{
				reason = "the compiler executable changed";
				return false;
			}
			if (!string.Equals(record.RequestFingerprint, GetRequestFingerprint(request, target), StringComparison.Ordinal))
			{
				reason = "the target, configuration, or test-build options changed";
				return false;
			}
			if (!File.Exists(record.ExecutablePath) || !File.Exists(record.ManifestPath))
			{
				reason = "the reusable test executable or manifest is missing";
				return false;
			}
			foreach (CampTestRunCacheInput input in record.Inputs)
			{
				if (!File.Exists(input.Path))
				{
					reason = input.Path + " is missing";
					return false;
				}
				if (!string.Equals(input.Fingerprint, GetFileFingerprint(input.Path), StringComparison.Ordinal))
				{
					reason = input.Path + " changed";
					return false;
				}
			}
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
		{
			record = null;
			reason = "the reusable test build could not be read: " + ex.Message;
			return false;
		}
	}

	static string GetCompilerFingerprint()
	{
		string assemblyPath = typeof(CompilerDriver).Assembly.Location;
		return File.Exists(assemblyPath) ? GetFileFingerprint(assemblyPath) : "unknown";
	}

	static string GetRequestFingerprint(CompilerRequest request, TargetDefinition target)
	{
		StringBuilder text = new();
		text.Append(target.Name).Append('\n');
		text.Append(request.ProfileName).Append('\n');
		text.Append(request.EmitKind).Append('\n');
		text.Append(request.NoStdLib).Append('\n');
		text.Append(request.IgnoreLeaks).Append('\n');
		text.Append(request.WithinAllocationPolicy).Append('\n');
		text.Append(request.SourcefilePathMode).Append('\n');
		AppendValues(text, request.Variants);
		AppendValues(text, request.Defines);
		AppendValues(text, request.ConfigurationFlagDeclarations);
		AppendValues(text, request.ConfigurationFlagConfigurations);
		AppendValues(text, request.ConfigurationRequirements);
		AppendValues(text, request.UsePackages);
		AppendValues(text, request.Frameworks);
		AppendValues(text, request.References);
		return GetTextFingerprint(text.ToString());
	}

	static void AppendValues(StringBuilder text, IEnumerable<string> values)
	{
		foreach (string value in values)
			text.Append(value).Append('\n');
	}

	static string GetFileFingerprint(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
	static string GetTextFingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
