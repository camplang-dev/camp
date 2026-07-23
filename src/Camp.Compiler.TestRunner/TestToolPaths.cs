using System;
using System.Diagnostics;
using System.IO;

namespace Camp.Compiler.Tests;

static class TestToolPaths
{
	public static ProcessStartInfo CreateCampcStartInfo(string repositoryRoot)
	{
		return CreateStartInfo(GetCampcPath(repositoryRoot), GetCampcPath(repositoryRoot));
	}

	public static string GetCampcPath(string repositoryRoot)
	{
		string? overridePath = Environment.GetEnvironmentVariable("CAMP_TEST_CAMPC");
		return Path.GetFullPath(string.IsNullOrWhiteSpace(overridePath) ? Path.Combine(repositoryRoot, "bin", OperatingSystem.IsWindows() ? "campc.exe" : "campc") : overridePath);
	}

	public static ProcessStartInfo CreateStartInfoForPath(string path)
	{
		return CreateStartInfo(path, path);
	}

	public static ProcessStartInfo CreateLspStartInfo(string repositoryRoot)
	{
		return CreateStartInfo(Environment.GetEnvironmentVariable("CAMP_TEST_LSP"), Path.Combine(repositoryRoot, "src", "camp-lsp", "bin", "Debug", "net10.0", "camp-lsp.dll"));
	}

	public static ProcessStartInfo CreateDapStartInfo(string repositoryRoot)
	{
		return CreateStartInfo(Environment.GetEnvironmentVariable("CAMP_TEST_DAP"), Path.Combine(repositoryRoot, "src", "camp-dap", "bin", "Debug", "net10.0", "camp-dap.dll"));
	}

	static ProcessStartInfo CreateStartInfo(string? overridePath, string defaultPath)
	{
		string path = Path.GetFullPath(string.IsNullOrWhiteSpace(overridePath) ? defaultPath : overridePath);
		ProcessStartInfo info = new()
		{
			FileName = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : path,
			UseShellExecute = false
		};
		if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			info.ArgumentList.Add(path);
		return info;
	}
}
