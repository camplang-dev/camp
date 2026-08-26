using System.Collections.Generic;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class PackageModelTests
{
	[Theory]
	[InlineData("ext-json", "ext-json", null, null, null)]
	[InlineData("ext-json@1", "ext-json", "1", null, null)]
	[InlineData("ext-json@1.2", "ext-json", "1.2", null, null)]
	[InlineData("ext-json@1.2.3:static", "ext-json", "1.2.3", null, DependencyLinkKind.Static)]
	[InlineData("ext-json/1.2.3:api", "ext-json", null, "1.2.3", DependencyLinkKind.Api)]
	public void PackageDependencySpec_parses_supported_shapes(string text, string name, string? expression, string? selected, DependencyLinkKind? linkKind)
	{
		Assert.True(PackageDependencySpec.TryParse(text, out PackageDependencySpec spec, out string? error), error);
		Assert.Equal(name, spec.Name);
		Assert.Equal(expression, spec.VersionExpression?.ToString());
		Assert.Equal(selected, spec.SelectedVersion?.ToString());
		Assert.Equal(linkKind, spec.LinkKind);
		Assert.Equal(text, spec.ToString());
	}

	[Theory]
	[InlineData("ext-json@1.2.3.4")]
	[InlineData("ext-json/1.2")]
	[InlineData("ext-json@1/1.2.3")]
	[InlineData("ext-json:dynamic")]
	public void PackageDependencySpec_rejects_invalid_shapes(string text)
	{
		Assert.False(PackageDependencySpec.TryParse(text, out _, out string? error));
		Assert.False(string.IsNullOrWhiteSpace(error));
	}

	[Fact]
	public void PackageCatalog_parses_versions_and_dependencies()
	{
		string text = """
			[package]
			name=demo
			identity=abc123

			[1.2.3]
			compiler=campc/0.9.0-preview.1
			use=ext-json@1.0:api ext-ansiterm@2
			sha256=001122
			src=demo_1.2.3.zip
			""";

		Assert.True(PackageCatalog.TryParse("versions.ini", text, out PackageCatalog? catalog, out List<string> errors), string.Join('\n', errors));
		Assert.NotNull(catalog);
		Assert.Equal("demo", catalog!.PackageName);
		Assert.Equal("abc123", catalog.Identity);
		PackageCatalogVersion version = Assert.Single(catalog.Versions.Values);
		Assert.Equal("1.2.3", version.Version.ToString());
		Assert.Equal("001122", version.Sha256);
		Assert.Equal("demo_1.2.3.zip", version.SourceArchive);
		Assert.Equal(2, version.Dependencies.Count);
		Assert.Contains("[1.2.3]", catalog.Write());
		Assert.Contains("use=ext-json@1.0:api ext-ansiterm@2", catalog.Write());
	}

	[Fact]
	public void PackageLockFile_round_trips_minimal_portable_facts()
	{
		SortedDictionary<string, PackageLockEntry> packages = new()
		{
			["ext-json"] = new PackageLockEntry("ext-json", "identity-json", PackageSelectedVersion.Parse("1.2.3"), "abc")
		};
		PackageLockFile original = new(packages);

		Assert.True(PackageLockFile.TryParse("packages.ini", original.Write(), out PackageLockFile? parsed, out List<string> errors), string.Join('\n', errors));
		Assert.NotNull(parsed);
		PackageLockEntry entry = Assert.Single(parsed!.Packages.Values);
		Assert.Equal("ext-json", entry.Name);
		Assert.Equal("identity-json", entry.Identity);
		Assert.Equal("1.2.3", entry.Version.ToString());
		Assert.Equal("abc", entry.Sha256);
	}
}
