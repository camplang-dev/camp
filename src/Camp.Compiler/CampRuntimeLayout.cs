using System;
using System.IO;

namespace Camp.Compiler;

public sealed class CampRuntimeLayout
{
	public const string HomeEnvironmentVariable = "CAMP_HOME";

	CampRuntimeLayout(string homeDirectory, string binDirectory, string? repositoryRoot)
	{
		HomeDirectory = homeDirectory;
		BinDirectory = binDirectory;
		RepositoryRoot = repositoryRoot;
	}

	public string HomeDirectory { get; }
	public string BinDirectory { get; }
	public string? RepositoryRoot { get; }
	public string LibraryDirectory => Path.Combine(HomeDirectory, "lib");
	public string TargetDirectory => Path.Combine(HomeDirectory, "targets");
	public string CompilerLibraryCacheDirectory => Path.Combine(HomeDirectory, "cache", "lib");
	public string PackageCacheDirectory => Path.Combine(HomeDirectory, "cache", "pkg");
	public string BaseCampBuildPath => Path.Combine(HomeDirectory, "base.campbuild");
	public string GlobalCampBuildPath => Path.Combine(HomeDirectory, "global.campbuild");

	public static CampRuntimeLayout Resolve(string? workingDirectory = null, string? runtimeBaseDirectory = null)
	{
		string? homeOverride = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
		if (!string.IsNullOrWhiteSpace(homeOverride))
		{
			string home = Path.GetFullPath(homeOverride);
			return new CampRuntimeLayout(home, Path.Combine(home, "bin"), FindRepositoryRoot(home));
		}

		string runtimeDirectory = Path.GetFullPath(runtimeBaseDirectory ?? AppContext.BaseDirectory);
		if (IsBinDirectory(runtimeDirectory))
		{
			string home = Directory.GetParent(TrimTrailingSeparators(runtimeDirectory))?.FullName ?? Path.GetFullPath(Path.Combine(runtimeDirectory, ".."));
			return new CampRuntimeLayout(home, runtimeDirectory, FindRepositoryRoot(home));
		}

		string? repositoryRoot = FindRepositoryRoot(workingDirectory) ?? FindRepositoryRoot(runtimeDirectory);
		if (repositoryRoot is not null)
			return new CampRuntimeLayout(repositoryRoot, Path.Combine(repositoryRoot, "bin"), repositoryRoot);

		string fallbackHome = Path.GetFullPath(Path.Combine(runtimeDirectory, ".."));
		return new CampRuntimeLayout(fallbackHome, Path.Combine(fallbackHome, "bin"), null);
	}

	public bool TryValidateRequiredInputs(out string? error)
	{
		if (!Directory.Exists(LibraryDirectory))
		{
			error = $"Camp runtime could not find the standard library at '{LibraryDirectory}'. Set {HomeEnvironmentVariable} to the Camp installation root.";
			return false;
		}
		if (!Directory.Exists(TargetDirectory))
		{
			error = $"Camp runtime could not find target definitions at '{TargetDirectory}'. Set {HomeEnvironmentVariable} to the Camp installation root.";
			return false;
		}
		error = null;
		return true;
	}

	public static string? FindRepositoryRoot(string? start)
	{
		if (string.IsNullOrWhiteSpace(start))
			return null;

		DirectoryInfo? directory = new(Path.GetFullPath(start));
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "src", "camplang.sln")) && Directory.Exists(Path.Combine(directory.FullName, "lib", "std", "src")))
				return directory.FullName;
			directory = directory.Parent;
		}
		return null;
	}

	static bool IsBinDirectory(string path)
	{
		return string.Equals(Path.GetFileName(TrimTrailingSeparators(path)), "bin", StringComparison.OrdinalIgnoreCase);
	}

	static string TrimTrailingSeparators(string path)
	{
		return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	}
}
