using System;
using System.IO;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class TargetCapabilityTests
{
	[Fact]
	public void Target_capabilities_expose_framework_specs_and_widths()
	{
		TargetCatalog catalog = LoadCatalog();
		Assert.True(catalog.TryGetTarget("clang-macos-x64", out TargetDefinition? macos));
		Assert.True(catalog.TryGetTarget("msvc-windows-x86", out TargetDefinition? windowsX86));
		Assert.True(catalog.TryGetTarget("gcc-linux-x64", out TargetDefinition? linuxX64));
		Assert.True(catalog.TryGetTarget("gcc-linux-x86", out TargetDefinition? linuxX86));

		Assert.Equal("macos", macos!.Capabilities.Platform);
		Assert.Equal("clang", macos.Capabilities.Compiler);
		Assert.Equal(".c", macos.Capabilities.CSourceExtension);
		Assert.Equal(".m", macos.Capabilities.ObjectiveCSourceExtension);
		Assert.True(macos.Capabilities.SupportsFrameworks);
		Assert.True(macos!.Capabilities.SupportsFrameworkLinking);
		Assert.True(macos.Capabilities.SupportsObjectiveC);

		Assert.Equal("windows", windowsX86!.Capabilities.Platform);
		Assert.Equal("msvc", windowsX86.Capabilities.Compiler);
		Assert.Equal(".c", windowsX86.Capabilities.CSourceExtension);
		Assert.False(windowsX86.Capabilities.SupportsFrameworks);
		Assert.False(windowsX86!.Capabilities.SupportsFrameworkLinking);
		Assert.False(windowsX86.Capabilities.SupportsObjectiveC);

		Assert.True(macos.Capabilities.HasCallSpec("_msabi"));
		Assert.False(macos.Capabilities.HasTypeSpec("_far"));
		Assert.True(windowsX86.Capabilities.HasCallSpec("_winapi"));
		Assert.True(windowsX86.Capabilities.HasTypeSpec("_far"));

		Assert.Equal("__declspec(dllexport)", windowsX86.Capabilities.GetCEmitterValue("dll_export_prefix"));
		Assert.Equal(32, windowsX86.Capabilities.GetNaturalIntegerWidth(null));
		Assert.Equal(32, macos.Capabilities.GetPointerWidth(null, null, functionPointer: false));

		Assert.Equal("linux", linuxX64!.Capabilities.Platform);
		Assert.Equal("gcc", linuxX64.Capabilities.Compiler);
		Assert.Equal(".c", linuxX64.Capabilities.CSourceExtension);
		Assert.False(linuxX64.Capabilities.SupportsFrameworks);
		Assert.False(linuxX64!.Capabilities.SupportsFrameworkLinking);
		Assert.False(linuxX64.Capabilities.SupportsObjectiveC);
		Assert.Equal("linux", linuxX86!.Capabilities.Platform);
		Assert.Equal("gcc", linuxX86.Capabilities.Compiler);
		Assert.Equal(".c", linuxX86.Capabilities.CSourceExtension);
		Assert.False(linuxX86.Capabilities.SupportsFrameworks);
		Assert.False(linuxX86!.Capabilities.SupportsFrameworkLinking);
		Assert.False(linuxX86.Capabilities.SupportsObjectiveC);
		Assert.Equal("gcc", linuxX64.Capabilities.GetTool("cc"));
		Assert.Equal("gcc", linuxX86.Capabilities.GetTool("cc"));
		Assert.Equal(64, linuxX64.Capabilities.GetNaturalIntegerWidth(null));
		Assert.Equal(64, linuxX64.Capabilities.GetPointerWidth(null, null, functionPointer: false));
		Assert.Equal(32, linuxX86.Capabilities.GetNaturalIntegerWidth(null));
		Assert.Equal(32, linuxX86.Capabilities.GetPointerWidth(null, null, functionPointer: false));
		Assert.Equal(".so", linuxX64.Capabilities.GetArtifactValue("shared_ext"));
		Assert.Equal(".so", linuxX86.Capabilities.GetArtifactValue("shared_ext"));
	}

	[Fact]
	public void Artifact_directory_names_include_target_variants_library_kind_and_profile()
	{
		TargetCatalog catalog = LoadCatalog();
		Assert.True(catalog.TryGetTarget("msvc-windows-x86", out TargetDefinition? windowsX86));
		Assert.True(catalog.TryGetTarget("clang-macos-x64", out TargetDefinition? macos));

		TargetDefinition ansiWindows = windowsX86!.WithVariantSelection(windowsX86.ResolveVariantSelection(["ansi"]));
		TargetDefinition unicodeWindows = windowsX86.WithVariantSelection(windowsX86.ResolveVariantSelection(["unicode"]));
		TargetDefinition macosTarget = macos!;

		Assert.Equal("msvc-windows-x86_ansi_static_DEBUG", BuildArtifactLayout.GetArtifactDirectoryName(ansiWindows, NativeBuildKind.Static, "debug"));
		Assert.Equal("msvc-windows-x86_shared_RELEASE", BuildArtifactLayout.GetArtifactDirectoryName(unicodeWindows, NativeBuildKind.Shared, "release"));
		Assert.Equal("msvc-windows-x86_ansi_DEBUG", BuildArtifactLayout.GetArtifactDirectoryName(ansiWindows, NativeBuildKind.Exec, "DEBUG"));
		Assert.Equal("clang-macos-x64_DEBUG", BuildArtifactLayout.GetArtifactDirectoryName(macosTarget, NativeBuildKind.Exec, "DEBUG"));
		Assert.Equal("clang-macos-x64_DEBUG_TEST", BuildArtifactLayout.GetArtifactDirectoryName(macosTarget, NativeBuildKind.Exec, "DEBUG", CompilerCommandMode.Test));
		Assert.Equal("clang-macos-x64_DEBUG_COVER", BuildArtifactLayout.GetArtifactDirectoryName(macosTarget, NativeBuildKind.Exec, "DEBUG", CompilerCommandMode.Cover));
		Assert.Equal("clang-macos-x64_shared_DEBUG_COVER", BuildArtifactLayout.GetArtifactDirectoryName(macosTarget, NativeBuildKind.Shared, "DEBUG", CompilerCommandMode.Cover));
		Assert.Equal("clang-macos-x64_static_DEBUG", BuildArtifactLayout.GetArtifactDirectoryName(macosTarget, NativeBuildKind.Static, ""));
	}

	[Fact]
	public void Target_conversion_policy_classifies_typespec_edges()
	{
		TargetCatalog catalog = LoadCatalog();
		Assert.True(catalog.TryGetTarget("msvc-win16-x86", out TargetDefinition? win16));

		Assert.Equal(TargetConversionLevel.Implicit, win16!.Capabilities.ClassifyTypeSpecConversion(TargetConversionCarrier.DataPointer, "_near", "_far"));
		Assert.Equal(TargetConversionLevel.Unsafe, win16.Capabilities.ClassifyTypeSpecConversion(TargetConversionCarrier.DataPointer, "_far", "_near"));
		Assert.Equal(TargetConversionLevel.Explicit, win16.Capabilities.ClassifyTypeSpecConversion(TargetConversionCarrier.FunctionPointer, "_near", "_far"));
		Assert.Equal(TargetConversionLevel.Unsafe, win16.Capabilities.ClassifyTypeSpecConversion(TargetConversionCarrier.NaturalInteger, "_huge", "_near"));
		Assert.Equal(TargetConversionLevel.Forbidden, win16.Capabilities.ClassifyTypeSpecConversion(TargetConversionCarrier.AbiSlot, "_near", "_far"));
		Assert.Equal(TargetConversionLevel.Forbidden, win16.Capabilities.ClassifyTypeSpecConversion(TargetConversionCarrier.AbiSlot, "_huge", "_near"));
	}

	[Fact]
	public void Target_conversion_policy_rejects_invalid_entries()
	{
		string root = Path.Combine(Path.GetTempPath(), "camp-target-policy-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WriteTarget(root, "missing.ini", """
[target]
name=missing

[typespec]
_near=__near

[conversion.data_pointer]
_near->_missing=implicit
""");
			Assert.False(TargetCatalog.TryLoad(root, out _, out string? error));
			Assert.Contains("Target conversion '_near->_missing' references unknown typespec '_missing'.", error);

			Directory.Delete(root, recursive: true);
			Directory.CreateDirectory(root);
			WriteTarget(root, "level.ini", """
[target]
name=level

[typespec]
_near=__near
_far=__far

[conversion.data_pointer]
_near->_far=maybe
""");
			Assert.False(TargetCatalog.TryLoad(root, out _, out error));
			Assert.Contains("uses unknown conversion level 'maybe'", error);

			Directory.Delete(root, recursive: true);
			Directory.CreateDirectory(root);
			WriteTarget(root, "compatible.ini", """
[target]
name=compatible

[typespec]
_near=__near
_far=__far

[conversion.data_pointer]
_near->_far=compatible
""");
			Assert.False(TargetCatalog.TryLoad(root, out _, out error));
			Assert.Contains("Conversion level 'compatible' is only valid in [conversion.abi_slot].", error);

			Directory.Delete(root, recursive: true);
			Directory.CreateDirectory(root);
			WriteTarget(root, "callspec.ini", """
[target]
name=callspec

[callspec]
_stdcall=__stdcall

[typespec]
_near=__near

[conversion.data_pointer]
_stdcall->_near=implicit
""");
			Assert.False(TargetCatalog.TryLoad(root, out _, out error));
			Assert.Contains("references callspec '_stdcall'; conversion policies require typespecs.", error);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Target_ini_parser_rejects_duplicate_sections_duplicate_keys_and_keys_outside_sections()
	{
		string root = Path.Combine(Path.GetTempPath(), "camp-target-ini-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WriteTarget(root, "duplicate-section.ini", """
[target]
name=duplicate-section

[target]
base=c99
""");
			Assert.False(TargetCatalog.TryLoad(root, out _, out string? error));
			Assert.Contains("Duplicate section [target].", error);

			Directory.Delete(root, recursive: true);
			Directory.CreateDirectory(root);
			WriteTarget(root, "duplicate-key.ini", """
[target]
name=duplicate-key
name=other
""");
			Assert.False(TargetCatalog.TryLoad(root, out _, out error));
			Assert.Contains("Duplicate key 'name' in section [target].", error);

			Directory.Delete(root, recursive: true);
			Directory.CreateDirectory(root);
			WriteTarget(root, "outside-section.ini", """
name=outside-section

[target]
name=outside-section
""");
			Assert.False(TargetCatalog.TryLoad(root, out _, out error));
			Assert.Contains("Keys must appear inside a section.", error);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Target_conversion_policy_allows_explicit_abi_slot_compatibility()
	{
		string root = Path.Combine(Path.GetTempPath(), "camp-target-policy-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WriteTarget(root, "abi.ini", """
[target]
name=abi

[typespec]
_small=
_large=

[conversion.abi_slot]
_small->_large=compatible
""");
			Assert.True(TargetCatalog.TryLoad(root, out TargetCatalog? catalog, out string? error), error);
			Assert.True(catalog!.TryGetTarget("abi", out TargetDefinition? target));
			Assert.Equal(TargetConversionLevel.Compatible, target!.Capabilities.ClassifyTypeSpecConversion(TargetConversionCarrier.AbiSlot, "_small", "_large"));
			Assert.Equal(TargetConversionLevel.Forbidden, target.Capabilities.ClassifyTypeSpecConversion(TargetConversionCarrier.AbiSlot, "_large", "_small"));
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Target_catalog_cache_reuses_and_invalidates_when_target_files_change()
	{
		string root = Path.Combine(Path.GetTempPath(), "camp-target-cache-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WriteTarget(root, "cache.ini", """
[target]
name=cacheA
""");

			int startHits = TargetCatalog.CacheHits;
			int startMisses = TargetCatalog.CacheMisses;

			Assert.True(TargetCatalog.TryLoadCached(root, out TargetCatalog? first, out string? error), error);
			Assert.True(first!.TryGetTarget("cacheA", out _));
			Assert.True(TargetCatalog.TryLoadCached(root, out TargetCatalog? second, out error), error);
			Assert.Same(first, second);
			Assert.True(TargetCatalog.CacheMisses >= startMisses + 1);
			Assert.True(TargetCatalog.CacheHits >= startHits + 1);
			int missesAfterReuse = TargetCatalog.CacheMisses;

			WriteTarget(root, "cache.ini", """
[target]
name=cacheBLonger
""");
			Assert.True(TargetCatalog.TryLoadCached(root, out TargetCatalog? third, out error), error);
			Assert.NotSame(first, third);
			Assert.True(third!.TryGetTarget("cacheBLonger", out _));
			Assert.True(TargetCatalog.CacheMisses >= missesAfterReuse + 1);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	static TargetCatalog LoadCatalog()
	{
		string targetsDirectory = Path.Combine(FindRepositoryRoot(), "targets");
		if (!TargetCatalog.TryLoadCached(targetsDirectory, out TargetCatalog? catalog, out string? error))
			throw new InvalidOperationException(error ?? "Target catalog could not be loaded.");
		return catalog!;
	}

	static void WriteTarget(string root, string name, string content)
	{
		File.WriteAllText(Path.Combine(root, name), content.Replace("\r\n", "\n"));
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
		throw new InvalidOperationException("Could not find repository root containing src/camplang.sln.");
	}
}
