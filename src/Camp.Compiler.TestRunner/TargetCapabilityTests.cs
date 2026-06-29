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

		Assert.True(macos!.Capabilities.SupportsFrameworkLinking);
		Assert.False(windowsX86!.Capabilities.SupportsFrameworkLinking);

		Assert.True(macos.Capabilities.HasCallSpec("_msabi"));
		Assert.False(macos.Capabilities.HasTypeSpec("_far"));
		Assert.True(windowsX86.Capabilities.HasCallSpec("_winapi"));
		Assert.True(windowsX86.Capabilities.HasTypeSpec("_far"));

		Assert.Equal("__declspec(dllexport)", windowsX86.Capabilities.GetCEmitterValue("dll_export_prefix"));
		Assert.Equal(32, windowsX86.Capabilities.GetNaturalIntegerWidth(null));
		Assert.Equal(32, macos.Capabilities.GetPointerWidth(null, null, functionPointer: false));

		Assert.False(linuxX64!.Capabilities.SupportsFrameworkLinking);
		Assert.False(linuxX86!.Capabilities.SupportsFrameworkLinking);
		Assert.Equal("gcc", linuxX64.Capabilities.GetTool("cc"));
		Assert.Equal("gcc", linuxX86.Capabilities.GetTool("cc"));
		Assert.Equal(64, linuxX64.Capabilities.GetNaturalIntegerWidth(null));
		Assert.Equal(64, linuxX64.Capabilities.GetPointerWidth(null, null, functionPointer: false));
		Assert.Equal(32, linuxX86.Capabilities.GetNaturalIntegerWidth(null));
		Assert.Equal(32, linuxX86.Capabilities.GetPointerWidth(null, null, functionPointer: false));
		Assert.Equal(".so", linuxX64.Capabilities.GetArtifactValue("shared_ext"));
		Assert.Equal(".so", linuxX86.Capabilities.GetArtifactValue("shared_ext"));
	}

	static TargetCatalog LoadCatalog()
	{
		string targetsDirectory = Path.Combine(FindRepositoryRoot(), "targets");
		if (!TargetCatalog.TryLoad(targetsDirectory, out TargetCatalog? catalog, out string? error))
			throw new InvalidOperationException(error ?? "Target catalog could not be loaded.");
		return catalog!;
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
