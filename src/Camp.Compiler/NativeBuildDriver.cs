using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Camp.Compiler;

public enum NativeBuildKind
{
	Exec,
	WinExe,
	Static,
	Shared
}

public sealed class NativeBuildOptions
{
	public required TargetDefinition Target { get; init; }
	public required string ProfileName { get; init; }
	public required string BuildDirectory { get; init; }
	public required string OutputDirectory { get; init; }
	public required string ProjectName { get; init; }
	public required NativeBuildKind Kind { get; init; }
	public required IReadOnlyList<string> SourceFiles { get; init; }
	public IReadOnlyList<string> Libraries { get; init; } = [];
	public IReadOnlyList<string> Frameworks { get; init; } = [];
}

public sealed class NativeBuildResult
{
	public List<string> GeneratedFiles { get; } = [];
	public List<string> RuntimeFiles { get; } = [];
	public List<string> LinkFiles { get; } = [];
	public List<string> Diagnostics { get; } = [];
	public bool Success => Diagnostics.Count == 0;
}

public static class NativeBuildDriver
{
	public static NativeBuildResult Build(NativeBuildOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		NativeBuildResult result = new();
		if (!TryResolveToolchainEnvironment(options, result, out IReadOnlyDictionary<string, string>? toolchainEnvironment))
			return result;

		Directory.CreateDirectory(options.BuildDirectory);
		Directory.CreateDirectory(options.OutputDirectory);

		if (!ValidateTemplates(options, result))
			return result;

		List<string> objects = [];
		foreach (string source in options.SourceFiles)
		{
			string objectPath = Path.Combine(options.BuildDirectory, Path.GetFileNameWithoutExtension(source) + options.Target.Capabilities.GetArtifactValue("object_ext", ".o"));
			if (!RunTemplate(options, "compile", result, toolchainEnvironment, new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["source"] = Quote(source),
				["object"] = Quote(objectPath)
			}))
				return result;
			objects.Add(objectPath);
			result.GeneratedFiles.Add(objectPath);
		}

		string output = GetArtifactPath(options);
		string? sharedImportLibrary = options.Kind == NativeBuildKind.Shared ? GetSharedImportLibraryPath(options) : null;
		if (options.Kind == NativeBuildKind.Static)
			DeleteExistingStaticArchive(output, result);
		if (!result.Success)
			return result;

		if (!RunTemplate(options, BuildTemplateName(options.Kind), result, toolchainEnvironment, new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["objects"] = string.Join(" ", objects.Select(Quote)),
			["libs"] = BuildLinkLibraries(options),
			["output"] = Quote(output),
			["import_library"] = Quote(sharedImportLibrary ?? "")
		}))
			return result;

		result.GeneratedFiles.Add(output);
		if (options.Kind == NativeBuildKind.Shared)
		{
			result.RuntimeFiles.Add(output);
			if (!string.IsNullOrWhiteSpace(sharedImportLibrary) && File.Exists(sharedImportLibrary))
			{
				result.GeneratedFiles.Add(sharedImportLibrary);
				result.LinkFiles.Add(sharedImportLibrary);
			}
			else
			{
				result.LinkFiles.Add(output);
			}
		}
		else
		{
			result.LinkFiles.Add(output);
		}
		return result;
	}

	public static bool IsValidFrameworkName(string name)
	{
		if (string.IsNullOrWhiteSpace(name) || name.StartsWith("-", StringComparison.Ordinal))
			return false;
		return name.All(static ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.');
	}

	static string BuildLinkLibraries(NativeBuildOptions options)
	{
		List<string> values = options.Libraries.Select(Quote).ToList();
		foreach (string framework in options.Frameworks)
		{
			values.Add("-framework");
			values.Add(Quote(framework));
		}
		return string.Join(" ", values);
	}

	static void DeleteExistingStaticArchive(string output, NativeBuildResult result)
	{
		try
		{
			if (File.Exists(output))
				File.Delete(output);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			result.Diagnostics.Add($"{output}: {ex.Message}");
		}
	}

	public static string GetArtifactPath(NativeBuildOptions options)
	{
		string prefix = options.Kind switch
		{
			NativeBuildKind.Exec => options.Target.Capabilities.GetArtifactValue("exec_prefix"),
			NativeBuildKind.WinExe => options.Target.Capabilities.GetArtifactValue("exec_prefix"),
			NativeBuildKind.Static => options.Target.Capabilities.GetArtifactValue("static_prefix", "lib"),
			NativeBuildKind.Shared => options.Target.Capabilities.GetArtifactValue("shared_prefix", "lib"),
			_ => ""
		};
		string extension = options.Kind switch
		{
			NativeBuildKind.Exec => options.Target.Capabilities.GetArtifactValue("exec_ext"),
			NativeBuildKind.WinExe => options.Target.Capabilities.GetArtifactValue("exec_ext"),
			NativeBuildKind.Static => options.Target.Capabilities.GetArtifactValue("static_ext", ".a"),
			NativeBuildKind.Shared => options.Target.Capabilities.GetArtifactValue("shared_ext", ".so"),
			_ => ""
		};
		return Path.Combine(options.OutputDirectory, prefix + options.ProjectName + extension);
	}

	public static string GetLinkArtifactPath(NativeBuildOptions options)
	{
		if (options.Kind == NativeBuildKind.Shared && GetSharedImportLibraryPath(options) is string importLibrary)
			return importLibrary;
		return GetArtifactPath(options);
	}

	public static string? GetSharedImportLibraryPath(NativeBuildOptions options)
	{
		string extension = options.Target.Capabilities.GetArtifactValue("shared_import_ext");
		if (string.IsNullOrWhiteSpace(extension))
			return null;
		string prefix = options.Target.Capabilities.GetArtifactValue("shared_import_prefix", options.Target.Capabilities.GetArtifactValue("shared_prefix", "lib"));
		return Path.Combine(options.OutputDirectory, prefix + options.ProjectName + extension);
	}

	static bool ValidateTemplates(NativeBuildOptions options, NativeBuildResult result)
	{
		foreach (string name in new[] { "compile", BuildTemplateName(options.Kind) })
		{
			if (options.Target.Capabilities.GetBuildTemplate(name) is null)
				result.Diagnostics.Add($"Target '{options.Target.Name}' does not define a [build] {name} template.");
		}
		return result.Success;
	}

	static bool TryResolveToolchainEnvironment(NativeBuildOptions options, NativeBuildResult result, out IReadOnlyDictionary<string, string>? environment)
	{
		environment = null;
		if (!OperatingSystem.IsWindows())
			return true;
		if (!options.Target.Toolchain.TryGetValue("msvc_arch", out string? expected) || string.IsNullOrWhiteSpace(expected))
			return true;

		expected = MsvcEnvironment.NormalizeArchitecture(expected) ?? expected.Trim();
		string? actual = MsvcEnvironment.TargetArchitecture;
		if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) && MsvcToolchainEnvironment.ToolsAreOnPath())
			return true;

		if (MsvcToolchainEnvironment.TryResolve(expected, result, out environment))
			return true;

		if (actual is not null)
			result.Diagnostics.Add($"Target '{options.Target.Name}' requires MSVC target architecture '{expected}', but the current Visual Studio environment targets '{actual}'. Run vcvarsall.bat {expected}, set CAMP_VCVARSALL, or use --target msvc-windows-{actual}.");
		return false;
	}

	static string BuildTemplateName(NativeBuildKind kind)
	{
		return kind switch
		{
			NativeBuildKind.Exec => "exec",
			NativeBuildKind.WinExe => "winexe",
			NativeBuildKind.Static => "static",
			NativeBuildKind.Shared => "shared",
			_ => throw new ArgumentOutOfRangeException(nameof(kind))
		};
	}

	static bool RunTemplate(NativeBuildOptions options, string templateName, NativeBuildResult result, IReadOnlyDictionary<string, string>? toolchainEnvironment, Dictionary<string, string> values)
	{
		string template = options.Target.Capabilities.GetBuildTemplate(templateName)!;
		TargetProfileBuild profile = options.Target.GetProfileBuild(options.ProfileName);
		values["cc"] = Quote(options.Target.Capabilities.GetTool("cc"));
		values["ar"] = Quote(options.Target.Capabilities.GetTool("ar"));
		values["ld"] = Quote(options.Target.Capabilities.GetTool("ld"));
		values["profile_cflags"] = profile.CFlags;
		values["profile_ldflags"] = profile.LdFlags;
		values["build_cflags"] = GetBuildCFlags(options);

		string command = ExpandTemplate(template, values);
		return RunCommand(command, options.BuildDirectory, result, toolchainEnvironment);
	}

	static string GetBuildCFlags(NativeBuildOptions options)
	{
		return options.Kind switch
		{
			NativeBuildKind.Shared => options.Target.Capabilities.GetCEmitterValue("shared_cflags"),
			NativeBuildKind.Static => options.Target.Capabilities.GetCEmitterValue("static_cflags"),
			NativeBuildKind.Exec => options.Target.Capabilities.GetCEmitterValue("exec_cflags"),
			NativeBuildKind.WinExe => options.Target.Capabilities.GetCEmitterValue("exec_cflags"),
			_ => ""
		};
	}

	static string ExpandTemplate(string template, IReadOnlyDictionary<string, string> values)
	{
		StringBuilder builder = new(template);
		foreach ((string key, string value) in values)
			builder.Replace("{" + key + "}", value);
		return builder.ToString();
	}

	static bool RunCommand(string command, string workingDirectory, NativeBuildResult result, IReadOnlyDictionary<string, string>? toolchainEnvironment)
	{
		const int timeoutMilliseconds = 120000;
		ProcessStartInfo info = new()
		{
			FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
			WorkingDirectory = workingDirectory,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false
		};
		if (toolchainEnvironment is not null)
		{
			foreach ((string key, string value) in toolchainEnvironment)
				info.Environment[key] = value;
		}
		if (OperatingSystem.IsWindows())
		{
			info.Arguments = "/S /C \"" + command + "\"";
		}
		else
		{
			info.ArgumentList.Add("-c");
			info.ArgumentList.Add(command);
		}

		using Process process = new() { StartInfo = info };
		try
		{
			process.Start();
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			result.Diagnostics.Add(ex.Message);
			result.Diagnostics.Add(command);
			return false;
		}

		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(timeoutMilliseconds))
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
			{
			}
			result.Diagnostics.Add($"Native build command timed out after {timeoutMilliseconds} ms: {command}");
			return false;
		}
		string stdout = stdoutTask.GetAwaiter().GetResult();
		string stderr = stderrTask.GetAwaiter().GetResult();
		if (process.ExitCode == 0)
			return true;

		result.Diagnostics.Add($"Native build command failed with exit code {process.ExitCode}: {command}");
		if (!string.IsNullOrWhiteSpace(stdout))
			result.Diagnostics.Add(stdout.TrimEnd());
		if (!string.IsNullOrWhiteSpace(stderr))
			result.Diagnostics.Add(stderr.TrimEnd());
		return false;
	}

	static string Quote(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return "\"\"";
		if (value.All(static ch => char.IsLetterOrDigit(ch) || ch is '/' or '.' or '_' or '-' or ':' or '\\'))
			return value;
		return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
	}
}

static class MsvcToolchainEnvironment
{
	const string VcToolsComponent = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";
	const int TimeoutMilliseconds = 30000;

	public static bool TryResolve(string architecture, NativeBuildResult result, out IReadOnlyDictionary<string, string>? environment)
	{
		environment = null;
		if (!TryFindVcVarsAll(result, out string? vcVarsAll))
			return false;

		ProcessStartInfo info = new()
		{
			FileName = "cmd.exe",
			Arguments = "/S /C \"\"" + vcVarsAll + "\" " + architecture + " >nul && set\"",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		using Process process = new() { StartInfo = info };
		try
		{
			process.Start();
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			result.Diagnostics.Add($"Could not run Visual Studio C++ environment setup '{vcVarsAll}': {ex.Message}");
			return false;
		}

		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		if (!process.WaitForExit(TimeoutMilliseconds))
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
			{
			}
			result.Diagnostics.Add($"Visual Studio C++ environment setup timed out after {TimeoutMilliseconds} ms: {vcVarsAll} {architecture}");
			return false;
		}
		if (process.ExitCode != 0)
		{
			result.Diagnostics.Add($"Visual Studio C++ environment setup failed with exit code {process.ExitCode}: {vcVarsAll} {architecture}");
			if (!string.IsNullOrWhiteSpace(stderr))
				result.Diagnostics.Add(stderr.TrimEnd());
			return false;
		}

		Dictionary<string, string> captured = ParseEnvironment(stdout);
		if (!captured.TryGetValue("VSCMD_ARG_TGT_ARCH", out string? targetArchitecture)
			|| !string.Equals(MsvcEnvironment.NormalizeArchitecture(targetArchitecture), architecture, StringComparison.OrdinalIgnoreCase))
		{
			result.Diagnostics.Add($"Visual Studio C++ environment setup did not produce an '{architecture}' tool environment.");
			return false;
		}

		environment = captured;
		return true;
	}

	public static bool ToolsAreOnPath()
	{
		return ToolIsOnPath("cl") && ToolIsOnPath("lib");
	}

	static bool TryFindVcVarsAll(NativeBuildResult result, out string? path)
	{
		path = null;
		string? explicitPath = Environment.GetEnvironmentVariable("CAMP_VCVARSALL");
		if (!string.IsNullOrWhiteSpace(explicitPath))
		{
			path = explicitPath.Trim();
			if (File.Exists(path))
				return true;
			result.Diagnostics.Add($"CAMP_VCVARSALL points to '{path}', but that file does not exist.");
			return false;
		}

		string? explicitInstall = Environment.GetEnvironmentVariable("CAMP_VSINSTALLDIR");
		if (!string.IsNullOrWhiteSpace(explicitInstall))
		{
			path = GetVcVarsAllPath(explicitInstall.Trim());
			if (File.Exists(path))
				return true;
			result.Diagnostics.Add($"CAMP_VSINSTALLDIR points to '{explicitInstall}', but '{path}' does not exist.");
			return false;
		}

		string? vsWhereInstall = FindWithVsWhere();
		if (!string.IsNullOrWhiteSpace(vsWhereInstall))
		{
			path = GetVcVarsAllPath(vsWhereInstall);
			if (File.Exists(path))
				return true;
		}

		foreach (string candidate in ProbeKnownInstallRoots())
		{
			path = GetVcVarsAllPath(candidate);
			if (File.Exists(path))
				return true;
		}

		path = null;
		result.Diagnostics.Add("MSVC targets require Microsoft C++ Build Tools. Install Visual Studio Build Tools with the Desktop development with C++ workload, open a matching Developer Command Prompt, or set CAMP_VCVARSALL to vcvarsall.bat.");
		return false;
	}

	static bool ToolIsOnPath(string tool)
	{
		string extension = Path.GetExtension(tool);
		IEnumerable<string> candidates = string.IsNullOrWhiteSpace(extension) ? [tool + ".exe", tool + ".bat", tool + ".cmd", tool] : [tool];
		foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			foreach (string candidate in candidates)
			{
				try
				{
					if (File.Exists(Path.Combine(directory, candidate)))
						return true;
				}
				catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
				{
				}
			}
		}
		return false;
	}

	static string GetVcVarsAllPath(string installRoot)
	{
		return Path.Combine(installRoot, "VC", "Auxiliary", "Build", "vcvarsall.bat");
	}

	static string? FindWithVsWhere()
	{
		string? explicitVsWhere = Environment.GetEnvironmentVariable("CAMP_VSWHERE");
		string? vsWhere = !string.IsNullOrWhiteSpace(explicitVsWhere)
			? explicitVsWhere.Trim()
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
		if (string.IsNullOrWhiteSpace(vsWhere) || !File.Exists(vsWhere))
			return null;

		ProcessStartInfo info = new(vsWhere)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		info.ArgumentList.Add("-latest");
		info.ArgumentList.Add("-products");
		info.ArgumentList.Add("*");
		info.ArgumentList.Add("-requires");
		info.ArgumentList.Add(VcToolsComponent);
		info.ArgumentList.Add("-property");
		info.ArgumentList.Add("installationPath");

		using Process process = new() { StartInfo = info };
		try
		{
			process.Start();
			string stdout = process.StandardOutput.ReadToEnd();
			process.StandardError.ReadToEnd();
			if (!process.WaitForExit(TimeoutMilliseconds) || process.ExitCode != 0)
				return null;
			return stdout.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return null;
		}
	}

	static IEnumerable<string> ProbeKnownInstallRoots()
	{
		string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		if (string.IsNullOrWhiteSpace(programFilesX86))
			yield break;

		foreach (string version in new[] { "2022", "2019" })
		{
			foreach (string edition in new[] { "BuildTools", "Community", "Professional", "Enterprise" })
				yield return Path.Combine(programFilesX86, "Microsoft Visual Studio", version, edition);
		}
	}

	static Dictionary<string, string> ParseEnvironment(string text)
	{
		Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
		foreach (string line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
		{
			int separator = line.IndexOf('=');
			if (separator <= 0)
				continue;
			environment[line[..separator]] = line[(separator + 1)..];
		}
		return environment;
	}
}
