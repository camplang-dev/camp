using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Camp.Compiler;

// A content-validated artifact handoff. It deliberately does not cache compiler
// analysis: it determines only whether an already-emitted native artifact can
// be used without entering the compilation pipeline.
internal sealed record CampArtifactCacheRecord(
	int SchemaVersion,
	string CompilerFingerprint,
	string RequestFingerprint,
	IReadOnlyList<CampArtifactCacheOutput> Outputs,
	IReadOnlyList<CampArtifactCacheInput> Inputs);

internal sealed record CampArtifactCacheOutput(string Name, string Path);

internal sealed record CampArtifactCacheInput(string Path, string Fingerprint);

internal static class CampArtifactCache
{
	const int SchemaVersion = 2;

	public static CampArtifactCacheRecord Create(
		CompilerRequest request,
		TargetDefinition target,
		IEnumerable<CampArtifactCacheOutput> outputs,
		IEnumerable<string> inputPaths)
	{
		List<CampArtifactCacheInput> inputs = inputPaths
			.Where(PathExists)
			.Select(Path.GetFullPath)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
			.Select(path => new CampArtifactCacheInput(path, GetPathFingerprint(path)))
			.ToList();
		List<CampArtifactCacheOutput> normalizedOutputs = outputs
			.Select(output => new CampArtifactCacheOutput(output.Name, Path.GetFullPath(output.Path)))
			.OrderBy(static output => output.Name, StringComparer.Ordinal)
			.ToList();
		return new CampArtifactCacheRecord(
			SchemaVersion,
			GetCompilerFingerprint(),
			GetRequestFingerprint(request, target),
			normalizedOutputs,
			inputs);
	}

	public static bool TryWrite(string path, CampArtifactCacheRecord record, out string? error)
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

	public static bool TryReadValidated(string path, CompilerRequest request, TargetDefinition target, out CampArtifactCacheRecord? record, out string? reason)
	{
		record = null;
		reason = null;
		try
		{
			if (!File.Exists(path))
			{
				reason = "no artifact cache record was found";
				return false;
			}
			record = JsonSerializer.Deserialize<CampArtifactCacheRecord>(File.ReadAllText(path));
			if (record is null || record.SchemaVersion != SchemaVersion)
			{
				reason = "the artifact cache record is incompatible";
				return false;
			}
			if (!string.Equals(record.CompilerFingerprint, GetCompilerFingerprint(), StringComparison.Ordinal))
			{
				reason = "the compiler changed";
				return false;
			}
			if (!string.Equals(record.RequestFingerprint, GetRequestFingerprint(request, target), StringComparison.Ordinal))
			{
				reason = "the request, target, or configuration changed";
				return false;
			}
			if (record.Outputs.Count == 0)
			{
				reason = "the record has no outputs";
				return false;
			}
			foreach (CampArtifactCacheOutput output in record.Outputs)
				if (!File.Exists(output.Path))
				{
					reason = output.Path + " is missing";
					return false;
				}
			foreach (CampArtifactCacheInput input in record.Inputs)
			{
				if (!PathExists(input.Path))
				{
					reason = input.Path + " is missing";
					return false;
				}
				if (!string.Equals(input.Fingerprint, GetPathFingerprint(input.Path), StringComparison.Ordinal))
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
			reason = "the artifact cache record could not be read: " + ex.Message;
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
		text.Append(request.CommandMode).Append('\n');
		text.Append(target.Name).Append('\n');
		text.Append(request.ProfileName).Append('\n');
		text.Append(request.EmitKind).Append('\n');
		text.Append(request.BuildKind).Append('\n');
		text.Append(request.NoStdLib).Append('\n');
		text.Append(request.IgnoreLeaks).Append('\n');
		text.Append(request.EmitDebugInfo).Append('\n');
		text.Append(request.EmitMetadata).Append('\n');
		text.Append(request.WithinAllocationPolicy).Append('\n');
		text.Append(request.SourcefilePathMode).Append('\n');
		text.Append(request.OutDirIsDirect).Append('\n');
		AppendValue(text, request.OutDir);
		AppendValue(text, request.TestOutputDir);
		AppendValue(text, request.ProjectName);
		AppendValue(text, request.SubsystemName);
		AppendValues(text, request.Files);
		AppendValues(text, request.NativeSourceFiles);
		AppendValues(text, request.ApiFiles);
		AppendValues(text, request.Variants);
		AppendValues(text, request.Defines);
		AppendValues(text, request.ConfigurationFlagDeclarations);
		AppendValues(text, request.ConfigurationFlagConfigurations);
		AppendValues(text, request.ConfigurationRequirements);
		AppendValues(text, request.UsePackages);
		AppendValues(text, request.Frameworks);
		AppendValues(text, request.References);
		AppendValues(text, request.UseSourceRoots);
		AppendValues(text, request.SourcefileRoots);
		return GetTextFingerprint(text.ToString());
	}

	static void AppendValue(StringBuilder text, string? value) => text.Append(value ?? "").Append('\n');

	static void AppendValues(StringBuilder text, IEnumerable<string> values)
	{
		foreach (string value in values)
			text.Append(value).Append('\n');
	}

	static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

	static string GetPathFingerprint(string path) => Directory.Exists(path) ? GetDirectoryFingerprint(path) : GetFileFingerprint(path);

	static string GetDirectoryFingerprint(string path)
	{
		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy(static value => value, StringComparer.Ordinal))
		{
			hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(path, file)));
			hash.AppendData([0]);
			hash.AppendData(SHA256.HashData(File.ReadAllBytes(file)));
		}
		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	static string GetFileFingerprint(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
	static string GetTextFingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
