using System;

namespace Camp.Compiler;

public static class CompilerDefaults
{
	public static string TargetName
	{
		get
		{
			if (OperatingSystem.IsWindows())
				return Environment.Is64BitOperatingSystem ? "msvc-windows-x64" : "msvc-windows-x86";
			return "clang-macos-x64";
		}
	}
}
