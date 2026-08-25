using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

static class CampInit
{
	static readonly IReadOnlyList<InitTemplate> Templates =
	[
		new("app", "Executable app", GenerateApp, static context => Success(context, "app", "campc run")),
		new("static", "Static library", GenerateStatic, static context => Success(context, "static library", "campc build", "campc test")),
		new("shared", "Shared library", GenerateShared, static context => Success(context, "shared library", "campc build", "campc test")),
		new("posix-api", "POSIX API wrapper", GeneratePosixApi, static context => Success(context, "POSIX API wrapper", "campc build")),
		new("windows-api", "Windows API wrapper", GenerateWindowsApi, static context => Success(context, "Windows API wrapper", "campc build")),
		new("wrapper", "Portable native wrapper", GenerateWrapper, static context => Success(context, "wrapper library", "campc build", "campc test"))
	];

	public static int Run(string[] args, CliEnvironment environment)
	{
		string? name = null;
		string templateName = "app";
		bool list = false;

		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i];
			switch (arg)
			{
				case "--list":
					list = true;
					break;
				case "--template":
					if (i + 1 >= args.Length)
						return Error("--template requires a value.");
					templateName = args[++i];
					break;
				default:
					if (arg.StartsWith("-", StringComparison.Ordinal))
						return Error($"Unknown init option '{arg}'.");
					if (name is not null)
						return Error($"Unexpected init argument '{arg}'.");
					name = arg;
					break;
			}
		}

		if (list)
		{
			foreach (InitTemplate item in Templates)
				Console.Out.WriteLine($"{item.Name,-12} {item.Description}");
			return 0;
		}

		if (string.IsNullOrWhiteSpace(name))
			return Error("init requires a project name.");
		if (!TryValidateName(name!, out string? nameError))
			return Error(nameError!);
		if (!TryGetTemplate(templateName, out InitTemplate? selectedTemplate))
			return Error($"Unknown init template '{templateName}'. Expected {TemplateNameList()}.");

		string destination = Path.Combine(environment.WorkingDirectory, name!);
		if (Directory.Exists(destination))
			return Error($"Directory '{name}' already exists.");
		if (File.Exists(destination))
			return Error($"Directory '{name}' already exists.");

		InitContext context = new(name!, destination);
		IReadOnlyList<GeneratedFile> files = selectedTemplate!.Generate(context);
		foreach (GeneratedFile file in files)
		{
			if (File.Exists(file.Path) || Directory.Exists(file.Path))
				return Error($"Refusing to overwrite existing file '{RelativeForMessage(environment.WorkingDirectory, file.Path)}'.");
		}

		try
		{
			foreach (GeneratedFile file in files)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
				File.WriteAllText(file.Path, NormalizeLineEndings(file.Content), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return Error(ex.Message);
		}

		Console.Out.Write(selectedTemplate.SuccessMessage(context));
		return 0;
	}

	static bool TryGetTemplate(string name, out InitTemplate? template)
	{
		template = Templates.FirstOrDefault(item => item.Name.Equals(name, StringComparison.Ordinal));
		return template is not null;
	}

	static bool TryValidateName(string name, out string? error)
	{
		error = null;
		if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
		{
			error = "init requires a project name.";
			return false;
		}
		if (Path.IsPathRooted(name) || name.Contains('/') || name.Contains('\\'))
		{
			error = "Project name must be a simple directory name, not a path.";
			return false;
		}
		if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			error = $"Project name '{name}' contains invalid filename characters.";
			return false;
		}
		return true;
	}

	static IReadOnlyList<GeneratedFile> GenerateApp(InitContext context)
	{
		return CommonFiles(context, "src/main.camp", $$"""
			export int main(string[] args)
			{
				Console.writeLine("Hello, world!");
				return 0;
			}
			""", $$"""
			# {{context.ProjectName}}

			This is an executable Camp project.

			## Files

			- `{{context.BuildFileName}}`: project build file.
			- `src/main.camp`: executable entry point.

			## Commands

			```sh
			campc run {{context.BuildFileName}}
			campc build {{context.BuildFileName}}
			```
			""");
	}

	static IReadOnlyList<GeneratedFile> GenerateStatic(InitContext context)
	{
		string namespaceName = PascalName(context.ProjectName);
		return CommonFiles(context, "src/main.camp", $$"""
			namespace {{namespaceName}};

			export int add(int a, int b)
			{
				return a + b;
			}

			@test
			void testAdd(thrown Assertion*)
			{
				assert(add(1, 2) == 3);
			}
			""", $$"""
			# {{context.ProjectName}}

			This is a static library project that exports `{{namespaceName}}.add`.

			## Files

			- `{{context.BuildFileName}}`: project build file.
			- `src/main.camp`: library source and starter test.

			## Commands

			```sh
			campc build {{context.BuildFileName}}
			campc test {{context.BuildFileName}}
			```
			""");
	}

	static IReadOnlyList<GeneratedFile> GenerateShared(InitContext context)
	{
		string prefix = ExportPrefix(context.ProjectName);
		return CommonFiles(context, "src/main.camp", $$"""
			export int {{prefix}}_add(int a, int b)
			{
				return a + b;
			}

			@test
			void testAdd(thrown Assertion*)
			{
				assert({{prefix}}_add(1, 2) == 3);
			}
			""", $$"""
			# {{context.ProjectName}}

			This project builds a shared library. The starter export uses an ABI-shaped flat function name.

			## Files

			- `{{context.BuildFileName}}`: project build file.
			- `src/main.camp`: shared library source and starter test.

			## Commands

			```sh
			campc build {{context.BuildFileName}}
			campc test {{context.BuildFileName}}
			```
			""", "--artifact shared\nsrc/*.camp\n");
	}

	static IReadOnlyList<GeneratedFile> GeneratePosixApi(InitContext context)
	{
		return CommonFiles(context, "src/posix.camp", """
			namespace Posix;

			@symbol("getpid")
			public extern int getpid();
			""", $$"""
			# {{context.ProjectName}}

			This is an API-only wrapper around a POSIX function.

			`public extern` declares a native symbol provided by the platform, not by Camp code in this project.
			`--artifact none` avoids producing a native library for declarations with no Camp implementation.

			## Files

			- `{{context.BuildFileName}}`: project build file.
			- `src/posix.camp`: POSIX API declarations.

			## Commands

			```sh
			campc build {{context.BuildFileName}}
			```

			## Using This API Wrapper

			Add the wrapper source as API input:

			```text
			--api ../{{context.ProjectName}}/src/*.camp
			src/*.camp
			```

			```camp
			export int main(string[] args)
			{
				Console.writeLine(Posix.getpid());
				return 0;
			}
			```
			""", "--artifact none\nsrc/*.camp\n");
	}

	static IReadOnlyList<GeneratedFile> GenerateWindowsApi(InitContext context)
	{
		return CommonFiles(context, "src/windows.camp", """
			namespace Windows;

			@symbol("GetCurrentProcessId")
			public extern uint GetCurrentProcessId();
			""", $$"""
			# {{context.ProjectName}}

			This is an API-only wrapper around a Windows API function.

			`public extern` declares a native symbol provided by Windows, not by Camp code in this project.
			`--artifact none` avoids producing a native library for declarations with no Camp implementation.

			## Files

			- `{{context.BuildFileName}}`: project build file.
			- `src/windows.camp`: Windows API declarations.

			## Commands

			```sh
			campc build {{context.BuildFileName}}
			```

			## Using This API Wrapper

			Add the wrapper source as API input:

			```text
			--api ../{{context.ProjectName}}/src/*.camp
			src/*.camp
			```

			```camp
			export int main(string[] args)
			{
				Console.writeLine(Windows.GetCurrentProcessId());
				return 0;
			}
			```
			""", "--artifact none\nsrc/*.camp\n");
	}

	static IReadOnlyList<GeneratedFile> GenerateWrapper(InitContext context)
	{
		string namespaceName = PascalName(context.ProjectName);
		return CommonFiles(context, "src/main.camp", $$"""
			namespace {{namespaceName}};

			namespace global
			{
				@require(SUBSYSTEM_POSIX)
				extern int getpid();

				@require(OS_WIN32)
				extern uint GetCurrentProcessId();
			}

			export int getCurrentProcessId()
			{
				if (configured(SUBSYSTEM_POSIX))
					return global::getpid();
				if (configured(OS_WIN32))
					return (int)global::GetCurrentProcessId();
				return -1;
			}

			@test
			void testGetCurrentProcessId(thrown Assertion*)
			{
				assert(getCurrentProcessId() > 0);
			}
			""", $$"""
			# {{context.ProjectName}}

			This is a static library that wraps platform-specific native APIs behind one portable Camp function.

			## Files

			- `{{context.BuildFileName}}`: project build file.
			- `src/main.camp`: portable wrapper source and starter test.

			## Commands

			```sh
			campc build {{context.BuildFileName}}
			campc test {{context.BuildFileName}}
			```

			Choose an appropriate target when building or testing so either `POSIX` or `WINDOWS` is defined by the selected target.
			""", "--artifact static\nsrc/*.camp\n");
	}

	static IReadOnlyList<GeneratedFile> CommonFiles(InitContext context, string sourceRelativePath, string source, string readme, string buildFile = "src/*.camp\n")
	{
		return
		[
			new(Path.Combine(context.Destination, context.BuildFileName), buildFile),
			new(Path.Combine(context.Destination, sourceRelativePath), source),
			new(Path.Combine(context.Destination, "README.md"), readme),
			new(Path.Combine(context.Destination, ".gitignore"), """
				bin/
				obj/
				cache/
				*.tmp
				*.log
				""")
		];
	}

	static string Success(InitContext context, string description, params string[] commands)
	{
		StringBuilder builder = new();
		builder.Append(CultureInvariant($"Created Camp {description} project in {context.Destination}"));
		builder.AppendLine();
		builder.AppendLine();
		builder.AppendLine("Next steps:");
		builder.AppendLine(CultureInvariant($"  cd {context.ProjectName}"));
		foreach (string command in commands)
			builder.AppendLine(CultureInvariant($"  {command} {context.BuildFileName}"));
		return builder.ToString();
	}

	static string PascalName(string name)
	{
		StringBuilder builder = new();
		bool nextUpper = true;
		foreach (char ch in name)
		{
			if (char.IsAsciiLetterOrDigit(ch))
			{
				if (builder.Length == 0 && char.IsAsciiDigit(ch))
					builder.Append('D');
				builder.Append(nextUpper ? char.ToUpperInvariant(ch) : ch);
				nextUpper = false;
			}
			else
				nextUpper = true;
		}
		return builder.Length == 0 ? "Project" : builder.ToString();
	}

	static string ExportPrefix(string name)
	{
		StringBuilder builder = new();
		foreach (char ch in name)
			if (char.IsAsciiLetterOrDigit(ch))
				builder.Append(char.ToLowerInvariant(ch));
		if (builder.Length == 0 || !(char.IsAsciiLetter(builder[0]) || builder[0] == '_'))
			builder.Insert(0, "camp_");
		return builder.ToString();
	}

	static string TemplateNameList() => string.Join(", ", Templates.Select(static template => template.Name)) + ".";

	static string RelativeForMessage(string root, string path)
	{
		return Path.GetRelativePath(root, path).Replace('\\', '/');
	}

	static string NormalizeLineEndings(string text)
	{
		return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
	}

	static int Error(string message)
	{
		Console.Error.WriteLine(message);
		return 1;
	}

	static string CultureInvariant(FormattableString value) => FormattableString.Invariant(value);

	sealed record InitTemplate(string Name, string Description, Func<InitContext, IReadOnlyList<GeneratedFile>> Generate, Func<InitContext, string> SuccessMessage);
	sealed record InitContext(string ProjectName, string Destination)
	{
		public string BuildFileName => ProjectName + ".campbuild";
	}
	sealed record GeneratedFile(string Path, string Content);
}
