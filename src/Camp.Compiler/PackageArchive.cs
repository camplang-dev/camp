using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Camp.Compiler;

public sealed record PackageSourceLocation(string Name, string Root)
{
	public bool IsHttp => Uri.TryCreate(Root, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https";
}

public static class PackageSourceClient
{
	static readonly HttpClient Http = new();

	public static bool TryResolvePackageUri(PackageSourceLocation source, string packageName, string relativePath, out Uri uri, out string? error)
	{
		error = null;
		string normalizedRelative = relativePath.Replace('\\', '/').TrimStart('/');
		if (source.IsHttp)
		{
			string root = source.Root.EndsWith("/", StringComparison.Ordinal) ? source.Root : source.Root + "/";
			uri = new Uri(new Uri(root), packageName.Trim('/') + "/" + normalizedRelative);
			return true;
		}

		string packageRoot = Path.GetFullPath(Path.Combine(source.Root, packageName));
		string fullPath = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
		if (!fullPath.StartsWith(packageRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !string.Equals(fullPath, packageRoot, StringComparison.Ordinal))
		{
			uri = new Uri(packageRoot);
			error = $"Package source path '{relativePath}' escapes package '{packageName}'.";
			return false;
		}
		uri = new Uri(fullPath);
		return true;
	}

	public static bool TryReadText(PackageSourceLocation source, string packageName, string relativePath, out string text, out string? error)
	{
		text = "";
		if (!TryResolvePackageUri(source, packageName, relativePath, out Uri uri, out error))
			return false;
		try
		{
			text = source.IsHttp ? Http.GetStringAsync(uri).GetAwaiter().GetResult() : File.ReadAllText(uri.LocalPath);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
		{
			error = $"Could not read package source '{source.Name}' item '{uri}': {ex.Message}";
			return false;
		}
	}

	public static bool TryReadBytes(PackageSourceLocation source, string packageName, string relativePath, out byte[] bytes, out string? error)
	{
		bytes = [];
		if (!TryResolvePackageUri(source, packageName, relativePath, out Uri uri, out error))
			return false;
		try
		{
			bytes = source.IsHttp ? Http.GetByteArrayAsync(uri).GetAwaiter().GetResult() : File.ReadAllBytes(uri.LocalPath);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
		{
			error = $"Could not read package source '{source.Name}' item '{uri}': {ex.Message}";
			return false;
		}
	}
}

public static class PackageArchive
{
	static readonly DateTimeOffset StableTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

	public static string Sha256Hex(byte[] bytes)
	{
		return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
	}

	public static string Sha256Hex(string path)
	{
		using FileStream stream = File.OpenRead(path);
		return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
	}

	public static bool TryExtractVerified(byte[] archiveBytes, string expectedSha256, string destinationDirectory, out string? error)
	{
		error = null;
		string actual = Sha256Hex(archiveBytes);
		if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
		{
			error = $"Package archive hash mismatch. Expected {expectedSha256}, got {actual}.";
			return false;
		}
		return TryExtract(archiveBytes, destinationDirectory, out error);
	}

	public static bool TryExtract(byte[] archiveBytes, string destinationDirectory, out string? error)
	{
		error = null;
		string destinationRoot = Path.GetFullPath(destinationDirectory);
		try
		{
			if (Directory.Exists(destinationRoot))
				Directory.Delete(destinationRoot, recursive: true);
			Directory.CreateDirectory(destinationRoot);
			using MemoryStream stream = new(archiveBytes);
			using ZipArchive archive = new(stream, ZipArchiveMode.Read);
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				string normalizedName = entry.FullName.Replace('\\', '/');
				if (string.IsNullOrWhiteSpace(normalizedName))
					continue;
				string targetPath = Path.GetFullPath(Path.Combine(destinationRoot, normalizedName));
				if (!targetPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !string.Equals(targetPath, destinationRoot, StringComparison.Ordinal))
				{
					error = $"Package archive entry '{entry.FullName}' escapes the extraction directory.";
					return false;
				}
				if (normalizedName.EndsWith("/", StringComparison.Ordinal))
				{
					Directory.CreateDirectory(targetPath);
					continue;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
				entry.ExtractToFile(targetPath, overwrite: true);
			}
			return true;
		}
		catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			error = $"Could not extract package archive: {ex.Message}";
			return false;
		}
	}

	public static byte[] CreateDeterministicZip(string sourceRoot, IEnumerable<string> filePaths)
	{
		string root = Path.GetFullPath(sourceRoot);
		using MemoryStream stream = new();
		using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (string file in filePaths.Select(Path.GetFullPath).Order(StringComparer.Ordinal))
			{
				string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
				if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
					throw new InvalidOperationException($"Package archive input '{file}' is outside '{root}'.");
				ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
				entry.LastWriteTime = StableTimestamp;
				using Stream entryStream = entry.Open();
				using FileStream fileStream = File.OpenRead(file);
				fileStream.CopyTo(entryStream);
			}
		}
		return stream.ToArray();
	}
}
