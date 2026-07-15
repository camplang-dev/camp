using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class MsvcCompileTests
{
	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Hello_executable_runs()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools are not available on PATH.");
		string source = WriteCase("hello", """
			using Std;

			export int main()
			{
				Console.writeLine("Hello from MSVC test");
				return 0;
			}
			""");

		CompilerResult result = Compile(source, NativeBuildKind.Exec);
		AssertSuccess(result);
		ProcessResult run = Run(FindArtifact(result, ".exe"));
		Assert.Equal(0, run.ExitCode);
		Assert.Contains("Hello from MSVC test", run.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Std_file_and_time_smoke_runs()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools are not available on PATH.");
		string source = WriteCase("std_file_time", """
			using Std;
			using Std::Time;

			int timerTicks;
			int asyncDone;

			export int main()
			{
				IoError error = default;
				FileOptions options = default;
				FileHandle writer = FileHandle.open("tmp/msvc-test-file.txt", FileAccess.WRITE, FileMode.CREATE_OR_TRUNCATE, options, catch error);
				if (error != default)
					return 1;
				const byte[] bytes = [(byte)'o', (byte)'k'];
				writer.write(bytes, catch error);
				if (error != default)
					return 2;
				writer.close();

				FileHandle reader = FileHandle.open("tmp/msvc-test-file.txt", FileAccess.READ, FileMode.OPEN_EXISTING, options, catch error);
				if (error != default)
					return 3;
				if (reader.getLength(catch error) != 2 || error != default)
					return 4;
				reader.close();

				Instant now = Instant.utcNow();
				string text = now.format.copyString() finally delete;
				if (text.Length == 0)
					return 5;

				sleep(1);
				sleepAsync(50, () => { asyncDone = 1; });
				if (asyncDone != 0)
					return 6;

				TimerHandle handle = startTimer(5, new delegate h => {
					timerTicks++;
					if (timerTicks >= 2)
						stopTimer(h);
				});
				if (handle == default)
					return 7;
				sleep(80);
				if (asyncDone != 1)
					return 8;
				if (timerTicks < 2)
					return 9;

				nint signedValue = 1;
				nuint unsignedValue = 10;
				void* pointerValue = null;
				if (atomicExchange(&signedValue, 2) != 1 || signedValue != 2)
					return 10;
				if (atomicCompareExchange(&signedValue, 3, 4) != 2 || signedValue != 2)
					return 11;
				if (atomicCompareExchange(&signedValue, 2, 5) != 2 || signedValue != 5)
					return 12;
				if (atomicExchange(&unsignedValue, 11) != 10 || unsignedValue != 11)
					return 13;
				if (atomicCompareExchange(&unsignedValue, 12, 13) != 11 || unsignedValue != 11)
					return 14;
				if (atomicCompareExchange(&unsignedValue, 11, 14) != 11 || unsignedValue != 14)
					return 15;
				if (atomicExchange(&pointerValue, (void*)(nint)20) != null || pointerValue != (void*)(nint)20)
					return 16;
				if (atomicCompareExchange(&pointerValue, (void*)(nint)21, (void*)(nint)22) != (void*)(nint)20 || pointerValue != (void*)(nint)20)
					return 17;
				if (atomicCompareExchange(&pointerValue, (void*)(nint)20, (void*)(nint)23) != (void*)(nint)20 || pointerValue != (void*)(nint)23)
					return 18;
				return 0;
			}
			""");

		CompilerResult result = Compile(source, NativeBuildKind.Exec);
		AssertSuccess(result);
		string timingSource = File.ReadAllText(FindGeneratedPackageSource("std_timing.c"));
		Assert.Contains("static uint32_t __stdcall timingTimerThread", timingSource, StringComparison.Ordinal);
		ProcessResult run = Run(FindArtifact(result, ".exe"));
		Assert.Equal(0, run.ExitCode);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Windows_subsystem_executable_builds()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools are not available on PATH.");
		string source = WriteCase("winexe", """
			export int main()
			{
				return 0;
			}
			""");

		CompilerResult result = Compile(source, NativeBuildKind.WinExe);
		AssertSuccess(result);
		Assert.EndsWith(".exe", FindArtifact(result, ".exe"), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Static_and_shared_libraries_build()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools are not available on PATH.");
		string source = WriteCase("library", """
			export int add(int a, int b) => a + b;
			internal int helper(int x) => x;
			""");

		CompilerResult staticResult = Compile(source, NativeBuildKind.Static, "library-static");
		AssertSuccess(staticResult);
		Assert.EndsWith(".lib", FindArtifact(staticResult, ".lib"), StringComparison.OrdinalIgnoreCase);

		CompilerResult sharedResult = Compile(source, NativeBuildKind.Shared, "library-shared");
		AssertSuccess(sharedResult);
		Assert.EndsWith(".dll", FindArtifact(sharedResult, ".dll"), StringComparison.OrdinalIgnoreCase);
		string header = File.ReadAllText(FindGeneratedFile(sharedResult, "library.h"));
		Assert.Contains("__declspec(dllexport) int32_t add", header, StringComparison.Ordinal);
		Assert.DoesNotContain("__declspec(dllexport) int32_t helper", header, StringComparison.Ordinal);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Calling_conventions_build_in_valid_msvc_positions()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools are not available on PATH.");
		string source = WriteCase("callspec", """
			export newtype fn _stdcall int Callback(int value);

			export _stdcall int exportedCall(int value)
			{
				return value + 1;
			}

			export int callCallback(Callback callback, int value)
			{
				return callback(value);
			}
			""");

		CompilerResult result = Compile(source, NativeBuildKind.Shared);
		AssertSuccess(result);
		string privateHeader = File.ReadAllText(FindGeneratedFile(result, "callspec_private.h"));
		Assert.Contains("typedef int32_t (__stdcall * Callback)(int32_t value);", privateHeader, StringComparison.Ordinal);
		Assert.Contains("int32_t __stdcall exportedCall", privateHeader, StringComparison.Ordinal);
	}

	static bool MsvcAvailable()
	{
		return OperatingSystem.IsWindows() && ToolAvailable("cl") && ToolAvailable("lib");
	}

	static bool ToolAvailable(string tool)
	{
		string[] extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string directory in GetPathValues().SelectMany(static value => value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)))
		{
			string trimmed = directory.Trim();
			if (File.Exists(Path.Combine(trimmed, tool)))
				return true;
			foreach (string extension in extensions)
			{
				if (File.Exists(Path.Combine(trimmed, tool + extension)))
					return true;
			}
		}
		return CommandResolves(tool);
	}

	static IEnumerable<string> GetPathValues()
	{
		foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
		{
			if (entry.Key is string key && key.Equals("PATH", StringComparison.OrdinalIgnoreCase) && entry.Value is string value)
				yield return value;
		}
	}

	static bool CommandResolves(string tool)
	{
		try
		{
			ProcessStartInfo startInfo = new("cmd.exe")
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				Arguments = "/S /C \"where " + tool + "\""
			};
			using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException();
			Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
			Task<string> stderrTask = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(3000))
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
				{
				}
				return false;
			}
			stdoutTask.GetAwaiter().GetResult();
			stderrTask.GetAwaiter().GetResult();
			return process.ExitCode == 0;
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}

	static CompilerResult Compile(string source, NativeBuildKind kind, string? caseName = null)
	{
		string repositoryRoot = FindRepositoryRoot();
		string name = caseName ?? Path.GetFileNameWithoutExtension(source);
		string root = GetCaseRoot(name);
		CompilerRequest request = new()
		{
			RuntimeRoot = Path.Combine(repositoryRoot, "bin"),
			TargetRoot = Path.Combine(repositoryRoot, "targets"),
			PackageSourceRoot = Path.Combine(repositoryRoot, "lib"),
			PackageArtifactRoot = Path.Combine(repositoryRoot, "tmp", "msvc-tests", "packages"),
			WorkingDirectory = repositoryRoot,
			TargetName = CompilerDefaults.TargetName,
			BuildKind = kind,
			OutDir = Path.Combine(root, "out")
		};
		request.Files.Add(source);
		return CompilerDriver.Execute(request);
	}

	static string WriteCase(string name, string text)
	{
		string root = GetCaseRoot(name);
		Directory.CreateDirectory(root);
		string path = Path.Combine(root, name + ".camp");
		File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal));
		return path;
	}

	static string GetCaseRoot(string name)
	{
		return Path.Combine(FindRepositoryRoot(), "tmp", "msvc-tests", name);
	}

	static string FindArtifact(CompilerResult result, string extension)
	{
		string? artifact = result.GeneratedFiles.FirstOrDefault(path => Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase));
		Assert.False(string.IsNullOrWhiteSpace(artifact), "Expected generated artifact with extension " + extension + ".");
		return artifact!;
	}

	static string FindGeneratedFile(CompilerResult result, string fileName)
	{
		string? generated = result.GeneratedFiles.FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
		Assert.False(string.IsNullOrWhiteSpace(generated), "Expected generated file " + fileName + ".");
		return generated!;
	}

	static string FindGeneratedPackageSource(string fileName)
	{
		string packageRoot = Path.Combine(FindRepositoryRoot(), "tmp", "msvc-tests", "packages");
		string? source = Directory.Exists(packageRoot)
			? Directory.GetFiles(packageRoot, fileName, SearchOption.AllDirectories).FirstOrDefault()
			: null;
		Assert.False(string.IsNullOrWhiteSpace(source), "Expected generated package source " + fileName + ".");
		return source!;
	}

	static void AssertSuccess(CompilerResult result)
	{
		Assert.Equal(0, result.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(result.StdErr), result.StdErr);
	}

	static ProcessResult Run(string executable)
	{
		ProcessStartInfo startInfo = new(executable)
		{
			WorkingDirectory = FindRepositoryRoot(),
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start " + executable);
		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(10000))
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
			{
			}
			Assert.Fail("Process timed out: " + executable);
		}
		string stdout = stdoutTask.GetAwaiter().GetResult();
		string stderr = stderrTask.GetAwaiter().GetResult();
		return new ProcessResult(process.ExitCode, stdout, stderr);
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

	readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}
