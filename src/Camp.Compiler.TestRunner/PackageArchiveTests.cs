using System;
using System.IO;
using System.IO.Compression;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class PackageArchiveTests
{
	[Fact]
	public void Local_package_source_reads_catalog_text()
	{
		string root = TempPath("source-read");
		string packageRoot = Path.Combine(root, "demo");
		Directory.CreateDirectory(packageRoot);
		File.WriteAllText(Path.Combine(packageRoot, "versions.ini"), "[package]\nname=demo\nidentity=abc\n");

		Assert.True(PackageSourceClient.TryReadText(new PackageSourceLocation("local", root), "demo", "versions.ini", out string text, out string? error), error);

		Assert.Contains("name=demo", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Verified_extract_rejects_hash_mismatch()
	{
		string root = TempPath("hash-mismatch");
		string source = Path.Combine(root, "src");
		Directory.CreateDirectory(source);
		File.WriteAllText(Path.Combine(source, "demo.camp"), "export int value = 1;\n");
		byte[] archive = PackageArchive.CreateDeterministicZip(source, [Path.Combine(source, "demo.camp")]);

		Assert.False(PackageArchive.TryExtractVerified(archive, "0000", Path.Combine(root, "out"), out string? error));
		Assert.Contains("hash mismatch", error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Deterministic_zip_extracts_expected_tree_and_hash_is_stable()
	{
		string root = TempPath("deterministic");
		string source = Path.Combine(root, "src");
		Directory.CreateDirectory(Path.Combine(source, "nested"));
		string first = Path.Combine(source, "nested", "b.camp");
		string second = Path.Combine(source, "a.camp");
		File.WriteAllText(first, "export int b = 2;\n");
		File.WriteAllText(second, "export int a = 1;\n");

		byte[] left = PackageArchive.CreateDeterministicZip(source, [first, second]);
		byte[] right = PackageArchive.CreateDeterministicZip(source, [second, first]);
		string hash = PackageArchive.Sha256Hex(left);

		Assert.Equal(hash, PackageArchive.Sha256Hex(right));
		Assert.True(PackageArchive.TryExtractVerified(left, hash, Path.Combine(root, "out"), out string? error), error);
		Assert.True(File.Exists(Path.Combine(root, "out", "a.camp")));
		Assert.True(File.Exists(Path.Combine(root, "out", "nested", "b.camp")));
	}

	[Fact]
	public void Archive_extract_rejects_path_traversal()
	{
		string root = TempPath("traversal");
		using MemoryStream stream = new();
		using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			ZipArchiveEntry entry = archive.CreateEntry("../escape.camp");
			using StreamWriter writer = new(entry.Open());
			writer.Write("bad");
		}

		Assert.False(PackageArchive.TryExtract(stream.ToArray(), Path.Combine(root, "out"), out string? error));
		Assert.Contains("escapes the extraction directory", error, StringComparison.Ordinal);
	}

	static string TempPath(string name)
	{
		string root = Path.Combine(FindRepositoryRoot(), "tmp", "package-archive-tests", name);
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
		Directory.CreateDirectory(root);
		return root;
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
		throw new InvalidOperationException("Could not find repository root.");
	}
}
