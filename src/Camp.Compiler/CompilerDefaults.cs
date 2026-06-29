using System;

namespace Camp.Compiler;

public static class CompilerDefaults
{
	public static string TargetName
	{
		get
		{
			if (OperatingSystem.IsWindows())
			{
				string? visualStudioTargetArchitecture = MsvcEnvironment.TargetArchitecture;
				if (visualStudioTargetArchitecture is "x64" or "x86")
					return "msvc-windows-" + visualStudioTargetArchitecture;
				return Environment.Is64BitOperatingSystem ? "msvc-windows-x64" : "msvc-windows-x86";
			}
			if (OperatingSystem.IsLinux())
				return "gcc-linux-x64";
			return "clang-macos-x64";
		}
	}
}

internal static class MsvcEnvironment
{
	public static string? TargetArchitecture
	{
		get
		{
			string? value = Environment.GetEnvironmentVariable("VSCMD_ARG_TGT_ARCH");
			if (!string.IsNullOrWhiteSpace(value))
				return NormalizeArchitecture(value);
			value = Environment.GetEnvironmentVariable("Platform");
			return string.IsNullOrWhiteSpace(value) ? null : NormalizeArchitecture(value);
		}
	}

	public static string? NormalizeArchitecture(string value)
	{
		return value.Trim().ToLowerInvariant() switch
		{
			"amd64" => "x64",
			"x64" => "x64",
			"x86" => "x86",
			"win32" => "x86",
			string other when other.EndsWith("_x86", StringComparison.Ordinal) => "x86",
			string other when other.EndsWith("_amd64", StringComparison.Ordinal) => "x64",
			_ => null
		};
	}
}
