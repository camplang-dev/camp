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
}

public sealed class NativeBuildResult
{
	public List<string> GeneratedFiles { get; } = [];
	public List<string> Diagnostics { get; } = [];
	public bool Success => Diagnostics.Count == 0;
}

public static class NativeBuildDriver
{
	public static NativeBuildResult Build(NativeBuildOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		NativeBuildResult result = new();
		Directory.CreateDirectory(options.BuildDirectory);
		Directory.CreateDirectory(options.OutputDirectory);

		if (!ValidateTemplates(options, result))
			return result;

		List<string> objects = [];
		foreach (string source in options.SourceFiles)
		{
			string objectPath = Path.Combine(options.BuildDirectory, Path.GetFileNameWithoutExtension(source) + options.Target.GetArtifactValue("object_ext", ".o"));
			if (!RunTemplate(options, "compile", result, new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["source"] = Quote(source),
				["object"] = Quote(objectPath)
			}))
				return result;
			objects.Add(objectPath);
			result.GeneratedFiles.Add(objectPath);
		}

		string output = GetArtifactPath(options);
		if (options.Kind == NativeBuildKind.Static)
			DeleteExistingStaticArchive(output, result);
		if (!result.Success)
			return result;

		if (!RunTemplate(options, BuildTemplateName(options.Kind), result, new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["objects"] = string.Join(" ", objects.Select(Quote)),
			["libs"] = string.Join(" ", options.Libraries.Select(Quote)),
			["output"] = Quote(output)
		}))
			return result;

		result.GeneratedFiles.Add(output);
		return result;
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
			NativeBuildKind.Exec => options.Target.GetArtifactValue("exec_prefix"),
			NativeBuildKind.WinExe => options.Target.GetArtifactValue("exec_prefix"),
			NativeBuildKind.Static => options.Target.GetArtifactValue("static_prefix", "lib"),
			NativeBuildKind.Shared => options.Target.GetArtifactValue("shared_prefix", "lib"),
			_ => ""
		};
		string extension = options.Kind switch
		{
			NativeBuildKind.Exec => options.Target.GetArtifactValue("exec_ext"),
			NativeBuildKind.WinExe => options.Target.GetArtifactValue("exec_ext"),
			NativeBuildKind.Static => options.Target.GetArtifactValue("static_ext", ".a"),
			NativeBuildKind.Shared => options.Target.GetArtifactValue("shared_ext", ".so"),
			_ => ""
		};
		return Path.Combine(options.OutputDirectory, prefix + options.ProjectName + extension);
	}

	static bool ValidateTemplates(NativeBuildOptions options, NativeBuildResult result)
	{
		foreach (string name in new[] { "compile", BuildTemplateName(options.Kind) })
		{
			if (options.Target.GetBuildTemplate(name) is null)
				result.Diagnostics.Add($"Target '{options.Target.Name}' does not define a [build] {name} template.");
		}
		return result.Success;
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

	static bool RunTemplate(NativeBuildOptions options, string templateName, NativeBuildResult result, Dictionary<string, string> values)
	{
		string template = options.Target.GetBuildTemplate(templateName)!;
		TargetProfileBuild profile = options.Target.GetProfileBuild(options.ProfileName);
		values["cc"] = Quote(options.Target.GetTool("cc"));
		values["ar"] = Quote(options.Target.GetTool("ar"));
		values["ld"] = Quote(options.Target.GetTool("ld"));
		values["profile_cflags"] = profile.CFlags;
		values["profile_ldflags"] = profile.LdFlags;
		values["build_cflags"] = GetBuildCFlags(options);

		string command = ExpandTemplate(template, values);
		return RunCommand(command, options.BuildDirectory, result);
	}

	static string GetBuildCFlags(NativeBuildOptions options)
	{
		return options.Kind switch
		{
			NativeBuildKind.Shared => options.Target.GetCEmitterValue("shared_cflags"),
			NativeBuildKind.Static => options.Target.GetCEmitterValue("static_cflags"),
			NativeBuildKind.Exec => options.Target.GetCEmitterValue("exec_cflags"),
			NativeBuildKind.WinExe => options.Target.GetCEmitterValue("exec_cflags"),
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

	static bool RunCommand(string command, string workingDirectory, NativeBuildResult result)
	{
		const int timeoutMilliseconds = 30000;
		ProcessStartInfo info = new()
		{
			FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
			WorkingDirectory = workingDirectory,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false
		};
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
