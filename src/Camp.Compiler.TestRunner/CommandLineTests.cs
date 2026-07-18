using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CommandLineTests
{
	static readonly Lazy<bool> ClangWasiAvailability = new(ProbeClangWasiAvailable);
	static readonly Lazy<bool> EmscriptenAvailability = new(ProbeEmscriptenAvailable);

	public CommandLineTests()
	{
		if (GoldenFilterActive())
			Assert.Skip("Command-line tests are skipped when CAMP_TEST_KIND or CAMP_TEST_CASE targets golden tests.");
	}

	[Fact]
	public void Root_command_requires_subcommand()
	{
		ProcessResult result = RunCampc();

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("A command is required", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Old_inspect_option_reports_migration_error()
	{
		ProcessResult result = RunCampc("--inspect", "lowering", "Tests/Lowering/default_arguments.camp");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("replaced by subcommands", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_dir_option_reports_migration_error()
	{
		ProcessResult result = RunCampc("build", "Tests/Lowering/default_arguments.camp", "--artifact", "none", "--build-dir", TempPath("removed-build-dir"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("--build-dir has been removed", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("output artifact directory's build subdirectory", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Help_command_prints_command_help()
	{
		ProcessResult root = RunCampc("--help");
		ProcessResult build = RunCampc("help", "build");

		Assert.Equal(0, root.ExitCode);
		Assert.Contains("Commands:", root.StdOut, StringComparison.Ordinal);
		Assert.Equal(0, build.ExitCode);
		Assert.Contains("--artifact", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("--subsystem", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("-f, --framework", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("-r, --reference", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("-u, --use", build.StdOut, StringComparison.Ordinal);
		Assert.Contains("--debug-info", build.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_debug_info_emits_line_directives_and_debug_map()
	{
		string source = CreateTempCase("debug_info/main.camp", """
			export int helper(int value)
			{
				int local = value + 1;
				return local;
			}

			export int main()
			{
				return helper(41);
			}
			""");
		string outDir = TempPath("debug-info-out");

		ProcessResult result = RunCampc(
			"build",
			source,
			"--nostdlib",
			"--artifact",
			"none",
			"--debug-info",
			"--name",
			"debug_info",
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		string debugMap = Assert.Single(Directory.GetFiles(outDir, "*.campdebug.json", SearchOption.AllDirectories));
		string debugJson = File.ReadAllText(debugMap);
		Assert.Contains("\"format\": \"camp.debug\"", debugJson, StringComparison.Ordinal);
		Assert.Contains("\"campFunction\": \"helper\"", debugJson, StringComparison.Ordinal);
		Assert.Contains("\"campName\": \"value\"", debugJson, StringComparison.Ordinal);
		Assert.Contains("\"campName\": \"local\"", debugJson, StringComparison.Ordinal);

		string cFile = Assert.Single(Directory.GetFiles(outDir, "main.c", SearchOption.AllDirectories));
		string cText = File.ReadAllText(cFile);
		Assert.Contains("#line 1", cText, StringComparison.Ordinal);
		Assert.Contains(EscapeCString(Path.GetFullPath(source)), cText, StringComparison.Ordinal);
	}

	[Fact]
	public void Dump_lowering_prints_to_stdout()
	{
		ProcessResult result = RunCampc("dump", "lowering", "Tests/Lowering/default_arguments.camp", "--nostdlib");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("addDefault", result.StdOut, StringComparison.Ordinal);
		Assert.Equal("", result.StdErr);
	}

	[Fact]
	public void Using_imports_support_qualified_types_functions_aliases_and_static_members()
	{
		string root = TempPath("using-qualified-positive");
		Directory.CreateDirectory(root);
		string library = Path.Combine(root, "library.camp");
		File.WriteAllText(library, """
			namespace Lib;

			export struct Point
			{
				int x;
				int y;
			}

			export alias PointAlias = Point;

			export int getValue() => 42;

			export static int Point.getDefault() => 7;
			""");
		string app = Path.Combine(root, "app.camp");
		File.WriteAllText(app, """
			namespace App;
			using Lib;
			using Lib as L;

			struct Holder<T: any>
			{
				T* value;
			}

			export Lib::Point makePoint()
			{
				Lib::Point p = default;
				return p;
			}

			export int readAlias(Lib::PointAlias p)
			{
				return p.y;
			}

			export int main()
			{
				Lib::Point p = default;
				L::Point q = default;
				Lib::Point* ptr = (Lib::Point*)null;
				Holder<Lib::Point> holder = default;
				const char[] name = typenameof(Lib::Point);
				return Lib::getValue() + L::getValue() + Lib::Point.getDefault() + q.x + p.y;
			}
			""");

		ProcessResult result = RunCampc("build", library, app, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("using-qualified-positive-out"));

		AssertCommandSucceeded(result);
	}

	[Fact]
	public void Using_imports_hide_unimported_unselected_and_unaliased_symbols()
	{
		string root = TempPath("using-import-negative");
		Directory.CreateDirectory(root);
		string library = Path.Combine(root, "library.camp");
		File.WriteAllText(library, """
			namespace Lib;

			export struct Point
			{
				int x;
			}

			export int getValue() => 42;
			""");
		string noImport = Path.Combine(root, "no_import.camp");
		File.WriteAllText(noImport, """
			namespace App;
			export int main() => getValue();
			""");
		string selected = Path.Combine(root, "selected.camp");
		File.WriteAllText(selected, """
			namespace App;
			using Lib { Point };
			export int main() => getValue();
			""");
		string aliasOriginal = Path.Combine(root, "alias_original.camp");
		File.WriteAllText(aliasOriginal, """
			namespace App;
			using Lib as L;
			export int main() => Lib::getValue();
			""");

		ProcessResult noImportResult = RunCampc("build", library, noImport, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("using-no-import-out"));
		ProcessResult selectedResult = RunCampc("build", library, selected, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("using-selected-out"));
		ProcessResult aliasOriginalResult = RunCampc("build", library, aliasOriginal, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("using-alias-original-out"));

		Assert.NotEqual(0, noImportResult.ExitCode);
		Assert.Contains("Symbol 'getValue' is declared in namespace 'Lib' but is not imported by this file.", noImportResult.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, selectedResult.ExitCode);
		Assert.Contains("Symbol 'getValue' is declared in namespace 'Lib' but is not imported by this file.", selectedResult.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, aliasOriginalResult.ExitCode);
		Assert.Contains("Symbol 'Lib::getValue' could not be found.", aliasOriginalResult.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Explicit_root_std_using_suppresses_implicit_std_import()
	{
		string implicitStd = CreateTempCase("using_implicit_std.camp", """
			export int main()
			{
				Console.writeLine("ok");
				return 0;
			}
			""");
		string aliasedStd = CreateTempCase("using_aliased_std.camp", """
			using Std as S;

			export int main()
			{
				S::Console.writeLine("ok");
				return 0;
			}
			""");
		string suppressedStd = CreateTempCase("using_suppressed_std.camp", """
			using Std as S;

			export int main()
			{
				Console.writeLine("missing");
				return 0;
			}
			""");
		string selectedStd = CreateTempCase("using_selected_std.camp", """
			using Std { Console };

			export int main()
			{
				List<int>* list = null;
				Console.writeLine("ok");
				return 0;
			}
			""");

		ProcessResult implicitResult = RunCampc("build", implicitStd, "--artifact", "none", "--out-dir", TempPath("using-implicit-std-out"));
		ProcessResult aliasedResult = RunCampc("build", aliasedStd, "--artifact", "none", "--out-dir", TempPath("using-aliased-std-out"));
		ProcessResult suppressedResult = RunCampc("build", suppressedStd, "--artifact", "none", "--out-dir", TempPath("using-suppressed-std-out"));
		ProcessResult selectedResult = RunCampc("build", selectedStd, "--artifact", "none", "--out-dir", TempPath("using-selected-std-out"));

		AssertCommandSucceeded(implicitResult);
		AssertCommandSucceeded(aliasedResult);
		Assert.NotEqual(0, suppressedResult.ExitCode);
		Assert.Contains("Type 'Console' is declared in namespace 'Std' but is not imported by this file.", suppressedResult.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, selectedResult.ExitCode);
		Assert.Contains("Type 'List' is declared in namespace 'Std' but is not imported by this file.", selectedResult.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Namespace_replaces_export_as_and_is_reserved()
	{
		string oldNamespace = CreateTempCase("old_namespace.camp", """
			export as OldName;

			export int main() => 0;
			""");
		string reserved = CreateTempCase("reserved_namespace.camp", """
			export int namespace() => 0;
			""");

		ProcessResult oldResult = RunCampc("build", oldNamespace, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("old-namespace-out"));
		ProcessResult reservedResult = RunCampc("build", reserved, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("reserved-namespace-out"));

		Assert.NotEqual(0, oldResult.ExitCode);
		Assert.Contains("Use 'namespace OldName;' instead of 'export as OldName;'.", oldResult.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, reservedResult.ExitCode);
		Assert.Contains("Function name 'namespace' is reserved.", reservedResult.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Public_visibility_spelling_is_artifact_visibility()
	{
		string source = CreateTempCase("public_visibility.camp", """
			public int helper() => 1;

			export int main()
			{
				return helper() - 1;
			}
			""");

		ProcessResult result = RunCampc("build", source, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("public-visibility-out"));

		AssertCommandSucceeded(result);
	}

	[Fact]
	public void Enum_value_shorthand_works_in_comparisons_on_either_side()
	{
		string source = CreateTempCase("enum_comparison_shorthand.camp", """
			extern void* malloc(nuint size);
			extern void free(void* pointer);

			bool isSolid(PenStyle style = SOLID)
			{
				return style == SOLID && SOLID == style;
			}

			enum PenStyle
			{
				SOLID,
				DASH
			}

			export int main()
			{
				return isSolid() ? 0 : 1;
			}
			""");

		ProcessResult result = RunCampc("build", source, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("enum-comparison-shorthand"));

		AssertCommandSucceeded(result);
	}

	[Fact]
	public void Invalid_finally_delete_reports_source_range()
	{
		string source = CreateTempCase("finally_delete_range.camp", """
			extern void* malloc(nuint size);
			extern void free(void* pointer);

			newtype HBRUSH: nint;

			HBRUSH createBrush() => (HBRUSH)1;

			export int main()
			{
				auto brush = createBrush() finally delete;
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", source, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("finally-delete-range"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("finally_delete_range.camp(10,15): error: delete requires a pointer or a type with a destructor, not 'HBRUSH'.", Normalize(result.StdErr), StringComparison.Ordinal);
		Assert.DoesNotContain("(no line,column)", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Invalid_foreach_iterator_cleanup_delete_reports_source_range()
	{
		string source = CreateTempCase("foreach_iterator_delete_range.camp", """
			public fixed struct BrokenIter
			{
				public extern bool next(int* current);
				public extern static bool op_iter(void* ctx, int* current);
			}

			public extern BrokenIter broken();

			export int main()
			{
				foreach (auto value in broken())
				{
				}
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", source, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("foreach-iterator-delete-range"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("foreach_iterator_delete_range.camp(11,2): error: delete requires a pointer or a type with a destructor, not 'BrokenIter'.", Normalize(result.StdErr), StringComparison.Ordinal);
		Assert.DoesNotContain("(no line,column)", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Async_exported_c_header_uses_completion_callback_abi()
	{
		string source = CreateTempCase("async_header.camp", """
			export extern async int loadAsync(thrown int error);
			""");
		string buildDir = TempPath("async-header-build");

		ProcessResult result = RunCampc("build", source, "--nostdlib", "--artifact", "none", "--out-dir", buildDir);

		Assert.Equal(0, result.ExitCode);
		string artifactDirectory = Path.Combine(buildDir, ArtifactDirectoryForHost(null));
		string publicHeader = File.ReadAllText(Path.Combine(artifactDirectory, "build", "async_header.h"));
		string privateHeader = File.ReadAllText(Path.Combine(artifactDirectory, "build", "async_header_private.h"));
		Assert.Contains("void loadAsync(void (* complete)(void *context, ", publicHeader, StringComparison.Ordinal);
		Assert.Contains(" result, ", publicHeader, StringComparison.Ordinal);
		Assert.Contains(" error), void *complete_context);", publicHeader, StringComparison.Ordinal);
		Assert.Contains("void loadAsync(void (* complete)(void *context, ", privateHeader, StringComparison.Ordinal);
		Assert.Contains(" result, ", privateHeader, StringComparison.Ordinal);
		Assert.Contains(" error), void *complete_context);", privateHeader, StringComparison.Ordinal);
		Assert.DoesNotContain("int loadAsync(int *error)", publicHeader, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_pragmas_contribute_default_options()
	{
		string temp = CreateTempCase("pragma_none.camp", """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "--out-dir", TempPath("pragma-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: pragma_none.c", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("_api.camp", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Within_allocation_policy_uses_defaults_flags_and_file_override()
	{
		string source = CreateTempCase("within_policy.camp", """
			export extern void* malloc(nuint size);
			export extern void free(void* ptr);

			export int main()
			{
				auto bytes = new byte[1];
				delete bytes;
				return 0;
			}
			""");
		string buildPragmaSource = CreateTempCase("within_policy_build_pragma.camp", """
			#build --explicit-within

			export extern void* malloc(nuint size);
			export extern void free(void* ptr);

			export int main()
			{
				auto bytes = new byte[1];
				delete bytes;
				return 0;
			}
			""");
		string fileImplicitSource = CreateTempCase("within_policy_file_implicit.camp", """
			#within implicit

			export extern void* malloc(nuint size);
			export extern void free(void* ptr);

			export int main()
			{
				auto bytes = new byte[1];
				delete bytes;
				return 0;
			}
			""");
		string fileExplicitSource = CreateTempCase("within_policy_file_explicit.camp", """
			#within explicit

			export extern void* malloc(nuint size);
			export extern void free(void* ptr);

			export int main()
			{
				auto bytes = new byte[1];
				delete bytes;
				return 0;
			}
			""");

		ProcessResult artifactNone = RunCampc("build", source, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("within-policy-none"));
		ProcessResult explicitNone = RunCampc("build", source, "--nostdlib", "--artifact", "none", "--explicit-within", "--out-dir", TempPath("within-policy-explicit-none"));
		ProcessResult staticDefault = RunCampc("build", source, "--nostdlib", "--artifact", "static", "--out-dir", TempPath("within-policy-static"));
		ProcessResult buildPragma = RunCampc("build", buildPragmaSource, "--nostdlib", "--artifact", "none", "--out-dir", TempPath("within-policy-build-pragma"));
		ProcessResult fileImplicit = RunCampc("build", fileImplicitSource, "--nostdlib", "--artifact", "none", "--explicit-within", "--out-dir", TempPath("within-policy-file-implicit"));
		ProcessResult fileExplicit = RunCampc("build", fileExplicitSource, "--nostdlib", "--artifact", "none", "--implicit-within", "--out-dir", TempPath("within-policy-file-explicit"));

		AssertCommandSucceeded(artifactNone);
		Assert.NotEqual(0, explicitNone.ExitCode);
		Assert.Contains("new requires an explicit within context", explicitNone.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, staticDefault.ExitCode);
		Assert.Contains("new requires an explicit within context", staticDefault.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, buildPragma.ExitCode);
		Assert.Contains("new requires an explicit within context", buildPragma.StdErr, StringComparison.Ordinal);
		AssertCommandSucceeded(fileImplicit);
		Assert.NotEqual(0, fileExplicit.ExitCode);
		Assert.Contains("new requires an explicit within context", fileExplicit.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Response_file_expands_build_arguments_relative_to_response_file()
	{
		string root = TempPath("response-file-project");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--name sample
			src/*.camp
			""");

		ProcessResult result = RunCampc("build", "@" + buildFile, "--out-dir", TempPath("response-file-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("_api.camp", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Response_file_name_can_omit_campbuild_extension()
	{
		string root = TempPath("response-file-extension");
		Directory.CreateDirectory(root);
		string source = Path.Combine(root, "main.camp");
		File.WriteAllText(source, """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, $"""
			--nostdlib
			--artifact none
			--name sample_extension
			"{source}"
			""");

		ProcessResult result = RunCampc("build", "@" + Path.Combine(root, "sample"), "--out-dir", TempPath("response-file-extension-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_treats_bare_campbuild_file_as_response_file()
	{
		string root = TempPath("bare-campbuild-file");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--name bare_sample
			src/*.camp
			""");

		ProcessResult result = RunCampc("build", buildFile, "--out-dir", TempPath("bare-campbuild-file-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_treats_extensionless_bare_campbuild_name_as_response_file()
	{
		string root = TempPath("bare-campbuild-extensionless");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--name bare_sample_extensionless
			src/*.camp
			""");

		ProcessResult result = RunCampc("build", Path.Combine(root, "sample"), "--out-dir", TempPath("bare-campbuild-extensionless-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_file_defaults_output_to_bin_artifact_directory()
	{
		string root = TempPath("campbuild-default-output");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--name sample
			src/*.camp
			""");

		ProcessResult result = RunCampc("build", buildFile, "--target", NativeTargetForHost());

		Assert.Equal(0, result.ExitCode);
		string artifactDirectory = Path.Combine(root, "bin", ArtifactDirectoryForHost(null));
		Assert.True(File.Exists(Path.Combine(artifactDirectory, "build", "main.c")));
		Assert.True(File.Exists(Path.Combine(artifactDirectory, "build", "main.h")));
		Assert.False(File.Exists(Path.Combine(root, "bin", "build", "main.c")));
	}

	[Fact]
	public void Source_file_defaults_output_to_first_source_directory_bin()
	{
		string root = TempPath("source-default-output");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		string source = Path.Combine(sourceDirectory, "main.camp");
		File.WriteAllText(source, """
			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", source, "--nostdlib", "--artifact", "none", "--target", NativeTargetForHost());

		Assert.Equal(0, result.ExitCode);
		string artifactDirectory = Path.Combine(sourceDirectory, "bin", ArtifactDirectoryForHost(null));
		Assert.True(File.Exists(Path.Combine(artifactDirectory, "build", "main.c")));
		Assert.True(File.Exists(Path.Combine(artifactDirectory, "build", "main.h")));
	}

	[Fact]
	public void Out_dir_is_prefix_unless_direct_directory_marker_is_used()
	{
		string source = CreateTempCase("out_dir_prefix/main.camp", """
			export int main()
			{
				return 0;
			}
			""");
		string prefixOut = TempPath("out-dir-prefix");
		string directOut = TempPath("out-dir-direct");

		ProcessResult prefix = RunCampc(
			"build",
			source,
			"--nostdlib",
			"--artifact",
			"none",
			"--target",
			NativeTargetForHost(),
			"--out-dir",
			prefixOut);
		ProcessResult direct = RunCampc(
			"build",
			source,
			"--nostdlib",
			"--artifact",
			"none",
			"--target",
			NativeTargetForHost(),
			"--out-dir",
			Path.Combine(directOut, "."));

		Assert.Equal(0, prefix.ExitCode);
		Assert.Equal(0, direct.ExitCode);
		Assert.True(File.Exists(Path.Combine(prefixOut, ArtifactDirectoryForHost(null), "build", "main.c")));
		Assert.False(File.Exists(Path.Combine(prefixOut, "build", "main.c")));
		Assert.True(File.Exists(Path.Combine(directOut, "build", "main.c")));
		Assert.False(File.Exists(Path.Combine(directOut, ArtifactDirectoryForHost(null), "build", "main.c")));
	}

	[Fact]
	public void Run_treats_bare_campbuild_file_as_response_file()
	{
		string root = TempPath("run-bare-campbuild-file");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact static
			--name run_bare_sample
			src/*.camp
			""");

		ProcessResult result = RunCampc("run", buildFile, "--out-dir", TempPath("run-bare-campbuild-file-build"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("run requires --artifact exec", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("At least one source file pattern is required", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Run_treats_extensionless_bare_campbuild_name_as_response_file()
	{
		string root = TempPath("run-bare-campbuild-extensionless");
		string sourceDirectory = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceDirectory);
		File.WriteAllText(Path.Combine(sourceDirectory, "main.camp"), """
			export int main()
			{
				return 0;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact static
			--name run_bare_sample_extensionless
			src/*.camp
			""");

		ProcessResult result = RunCampc("run", Path.Combine(root, "sample"), "--out-dir", TempPath("run-bare-campbuild-extensionless-build"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("run requires --artifact exec", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("At least one source file pattern is required", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Project_reference_builds_static_library_and_includes_api()
	{
		string root = TempPath("project-reference");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		Directory.CreateDirectory(librarySource);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			export int add(int left, int right)
			{
				return left + right;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			src/*.camp
			""");

		string app = CreateTempCase("project_reference_app.camp", """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return add(1, 2) - 3;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			"clang-macos-x64",
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			TempPath("project-reference-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: project_reference_app.c", result.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Static), "sample-lib_api.camp")));
	}

	[Fact]
	public void Static_project_reference_exposes_public_but_not_internal_api()
	{
		string root = TempPath("project-reference-public-static");
		string libraryRoot = Path.Combine(root, "library");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			public int publicValue()
			{
				return 20;
			}

			internal int internalValue()
			{
				return 2;
			}

			export int exportedValue()
			{
				return 22;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "library.campbuild"), """
			--nostdlib
			--name visibility-lib
			src/*.camp
			""");
		string goodApp = Path.Combine(appRoot, "good.camp");
		File.WriteAllText(goodApp, """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return publicValue() + exportedValue() - 42;
			}
			""");
		string badApp = Path.Combine(appRoot, "bad.camp");
		File.WriteAllText(badApp, """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return internalValue();
			}
			""");
		string target = NativeTargetForHost();

		ProcessResult good = RunCampc("build", goodApp, "--target", target, "--project-reference", libraryRoot + ":static", "--out-dir", Path.Combine(appRoot, "good-bin"));
		ProcessResult bad = RunCampc("build", badApp, "--target", target, "--project-reference", libraryRoot + ":static", "--out-dir", Path.Combine(appRoot, "bad-bin"));

		AssertCommandSucceeded(good);
		Assert.NotEqual(0, bad.ExitCode);
		Assert.Contains("Symbol 'internalValue' could not be found.", bad.StdErr, StringComparison.Ordinal);
		string api = File.ReadAllText(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "visibility-lib_api.camp"));
		Assert.Contains("public extern int publicValue();", api, StringComparison.Ordinal);
		Assert.DoesNotContain("internalValue", api, StringComparison.Ordinal);
	}

	[Fact]
	public void Shared_project_reference_exposes_export_api_only()
	{
		string root = TempPath("project-reference-public-shared");
		string libraryRoot = Path.Combine(root, "library");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			public int publicValue()
			{
				return 20;
			}

			export int exportedValue()
			{
				return 22;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "library.campbuild"), """
			--nostdlib
			--name visibility-lib
			src/*.camp
			""");
		string goodApp = Path.Combine(appRoot, "good.camp");
		File.WriteAllText(goodApp, """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return exportedValue() - 22;
			}
			""");
		string badApp = Path.Combine(appRoot, "bad.camp");
		File.WriteAllText(badApp, """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return publicValue();
			}
			""");
		string target = NativeTargetForHost();

		ProcessResult good = RunCampc("build", goodApp, "--target", target, "--project-reference", libraryRoot + ":shared", "--out-dir", Path.Combine(appRoot, "good-bin"));
		ProcessResult bad = RunCampc("build", badApp, "--target", target, "--project-reference", libraryRoot + ":shared", "--out-dir", Path.Combine(appRoot, "bad-bin"));

		AssertCommandSucceeded(good);
		Assert.NotEqual(0, bad.ExitCode);
		Assert.Contains("Symbol 'publicValue' could not be found.", bad.StdErr, StringComparison.Ordinal);
		string api = File.ReadAllText(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Shared), "visibility-lib_api.camp"));
		Assert.DoesNotContain("publicValue", api, StringComparison.Ordinal);
		Assert.Contains("export extern int exportedValue();", api, StringComparison.Ordinal);
		string cApi = File.ReadAllText(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Shared), "visibility-lib_api.h"));
		Assert.DoesNotContain("publicValue", cApi, StringComparison.Ordinal);
		Assert.Contains("exportedValue", cApi, StringComparison.Ordinal);
	}

	[Fact]
	public void Project_api_header_orders_forward_typedefs_before_callable_newtypes_and_struct_layouts()
	{
		string root = TempPath("project-api-layout-order");
		string sourceRoot = Path.Combine(root, "src");
		Directory.CreateDirectory(sourceRoot);
		File.WriteAllText(Path.Combine(sourceRoot, "library.camp"), """
			export newtype fn void PaintEvent(PaintEventArgs* e);

			export struct PaintEventArgs
			{
				Rect32 bounds;
			}

			export struct Rect32
			{
				int x;
			}
			""");
		File.WriteAllText(Path.Combine(root, "layout-lib.campbuild"), """
			--nostdlib
			--artifact static
			--name layout-lib
			src/*.camp
			""");

		string target = NativeTargetForHost();
		ProcessResult result = RunCampc("build", Path.Combine(root, "layout-lib.campbuild"), "--target", target);

		Assert.Equal(0, result.ExitCode);
		string apiHeader = File.ReadAllText(Path.Combine(root, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "layout-lib_api.h"))
			.Replace("\r\n", "\n", StringComparison.Ordinal);
		Assert.True(apiHeader.IndexOf("typedef struct PaintEventArgs PaintEventArgs;", StringComparison.Ordinal) < apiHeader.IndexOf("typedef void (* PaintEvent)", StringComparison.Ordinal));
		Assert.True(apiHeader.IndexOf("struct Rect32\n{", StringComparison.Ordinal) < apiHeader.IndexOf("struct PaintEventArgs\n{", StringComparison.Ordinal));
	}

	[Fact]
	public void Project_reference_consumes_exported_shadow_class_api()
	{
		string root = TempPath("project-reference-shadow-api");
		string libraryRoot = Path.Combine(root, "library");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			extern void* malloc(nuint size);
			extern void free(void* ptr);

			export interface IShadowValue
			{
				int read();
			}

			export class NativeShadowHost
			{
				escaped void* shadowData;

				NativeShadowHost()
				{
				}

				@getshadow
				export escaped void* getShadow(const this) => this.shadowData;

				@setshadow
				export void setShadow(escaped void* value) => this.shadowData = value;
			}

			export virtual shadow class BaseShadow: NativeShadowHost, IShadowValue
			{
				int baseValue;

				export BaseShadow(int value)
				{
					this.baseValue = value;
				}

				export int read(): IShadowValue => this.baseValue;

				export int getBase() => this.baseValue;

				export virtual int calculate() => this.baseValue;

				void cleanupBase()
				{
					delete shadow;
				}
			}

			export virtual shadow class DerivedShadow: BaseShadow
			{
				int extraValue;

				export DerivedShadow(int value, int extra)
				{
					base(value);
					this.extraValue = extra;
				}

				export override int calculate() => this.getBase() + this.extraValue;

				void cleanupDerived()
				{
					delete shadow;
				}
			}

			export DerivedShadow* makeDerived(int value, int extra) => within(default) new DerivedShadow(value, extra);
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "library.campbuild"), """
			--nostdlib
			--name shadow-lib
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				auto derived = makeDerived(10, 7);
				BaseShadow* baseView = derived;
				IShadowValue* value = derived;
				return baseView.calculate() + value.read() - 27;
			}
			""");
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc("build", app, "--target", target, "--project-reference", libraryRoot + ":static", "--out-dir", Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(result);
		string api = File.ReadAllText(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "shadow-lib_api.camp"));
		Assert.Contains("export extern shadow class BaseShadow", api, StringComparison.Ordinal);
		Assert.Contains("export extern shadow class DerivedShadow", api, StringComparison.Ordinal);
		Assert.Contains("@getshadow", api, StringComparison.Ordinal);
		Assert.Contains("@setshadow", api, StringComparison.Ordinal);
	}

	[Fact]
	public void Shadow_class_can_allocate_imported_extern_base_with_exported_constructor()
	{
		string root = TempPath("project-reference-shadow-extern-base-constructor");
		string libraryRoot = Path.Combine(root, "library");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			extern void* malloc(nuint size);
			extern void free(void* ptr);

			export class NativeShadowHost
			{
				escaped void* shadowData;

				export NativeShadowHost()
				{
				}

				@getshadow
				export escaped void* getShadow(const this) => this.shadowData;

				@setshadow
				export void setShadow(escaped void* value) => this.shadowData = value;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "library.campbuild"), """
			--nostdlib
			--name shadow-base-lib
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --artifact exec

			extern void* malloc(nuint size);
			extern void free(void* ptr);

			shadow class LocalShadow: NativeShadowHost
			{
				LocalShadow()
				{
				}

				void cleanup()
				{
					delete shadow;
				}
			}

			export int main()
			{
				auto value = new LocalShadow();
				value.cleanup();
				return 0;
			}
			""");
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc("build", app, "--target", target, "--project-reference", libraryRoot + ":static", "--out-dir", Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(result);
	}

	[Fact]
	public void Wasi_target_builds_stdlib_executable_when_available()
	{
		if (!ClangWasiAvailable())
			Assert.Skip("Clang with WASI support is required for the local WASI smoke test.");
		string source = CreateTempCase("wasi-std/main.camp", """
			export int main()
			{
				Console.writeLine("hello wasi");
				return 0;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			source,
			"--target",
			"wasm32-wasi",
			"--artifact",
			"exec",
			"--out-dir",
			TempPath("wasi-std-out"));

		AssertCommandSucceeded(result);
		Assert.True(File.Exists(Path.Combine(TempPath("wasi-std-out"), ArtifactDirectoryForTarget("wasm32-wasi", NativeBuildKind.Exec), "main.wasm")));
	}

	[Fact]
	public void Wasi_target_rejects_calls_to_unsupported_std_apis()
	{
		string source = CreateTempCase("wasi-unsupported/main.camp", """
			export int main()
			{
				sleep(1);
				return 0;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			source,
			"--target",
			"wasm32-wasi",
			"--artifact",
			"none",
			"--out-dir",
			TempPath("wasi-unsupported-out"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Function 'sleep' is not supported by the current target.", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("The current target does not support timers or thread sleeping.", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Emscripten_target_builds_stdlib_executable_when_available()
	{
		if (!EmscriptenAvailable())
			Assert.Skip("Emscripten is required for the local Emscripten smoke test.");
		string source = CreateTempCase("emscripten-std/main.camp", """
			export int main()
			{
				Console.writeLine("hello emscripten");
				return 0;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			source,
			"--target",
			"wasm32-emscripten",
			"--artifact",
			"exec",
			"--out-dir",
			TempPath("emscripten-std-out"));

		AssertCommandSucceeded(result);
		string artifact = Path.Combine(TempPath("emscripten-std-out"), ArtifactDirectoryForTarget("wasm32-emscripten", NativeBuildKind.Exec), "main.js");
		Assert.True(File.Exists(artifact));
		Assert.True(File.Exists(Path.ChangeExtension(artifact, ".wasm")));
	}

	[Fact]
	public void Emscripten_target_rejects_calls_to_unsupported_std_apis()
	{
		string source = CreateTempCase("emscripten-unsupported/main.camp", """
			export int main()
			{
				FileHandle handle = FileHandle.open("missing.txt", FileAccess.READ, FileMode.OPEN_EXISTING, catch _);
				return handle == default ? 0 : 1;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			source,
			"--target",
			"wasm32-emscripten",
			"--artifact",
			"none",
			"--out-dir",
			TempPath("emscripten-unsupported-out"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Function 'open' is not supported by the current target.", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("The current target does not support file handles.", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Project_reference_uses_root_variant_for_artifact_directory()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC variant project-reference smoke requires loaded MSVC tools.");

		string root = TempPath("project-reference-variant");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		Directory.CreateDirectory(librarySource);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			export int width()
			{
				#if UNICODE
				return 2;
				#else
				return 1;
				#endif
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			--variant ansi
			src/*.camp
			""");

		string app = CreateTempCase("project_reference_variant_app.camp", """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return width() - 2;
			}
			""");

		string target = "msvc-windows-" + MsvcEnvironment.TargetArchitecture;
		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--variant",
			"unicode",
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			TempPath("project-reference-variant-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.True(File.Exists(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "sample-lib_api.camp")));
		Assert.False(File.Exists(Path.Combine(libraryRoot, "bin", target + "_ansi_static_DEBUG", "sample-lib_api.camp")));
	}

	[Fact]
	public void Project_reference_uses_variant_cache_instead_of_referenced_project_output_directories()
	{
		string root = TempPath("project-reference-configured-directories");
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "sample-app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			export int add(int left, int right)
			{
				return left + right;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			--out-dir bin
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name sample-app

			export int main()
			{
				return add(20, 22) - 42;
			}
			""");
		string target = NativeTargetForHost();
		string staticLibraryName = OperatingSystem.IsWindows() ? "sample-lib.lib" : "libsample-lib.a";
		string referenceOutputDirectory = Path.Combine(libraryRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Static));

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(result);
		Assert.True(File.Exists(Path.Combine(referenceOutputDirectory, staticLibraryName)));
		Assert.True(File.Exists(Path.Combine(referenceOutputDirectory, "sample-lib_api.camp")));
		Assert.True(File.Exists(Path.Combine(referenceOutputDirectory, "build", "library.c")));
		Assert.False(File.Exists(Path.Combine(libraryRoot, "bin", staticLibraryName)));
		Assert.False(File.Exists(Path.Combine(libraryRoot, "bin", "sample-lib_api.camp")));
		Assert.False(Directory.Exists(Path.Combine(libraryRoot, "build")));
		Assert.False(Directory.Exists(Path.Combine(libraryRoot, "obj")));
		Assert.True(File.Exists(Path.Combine(appRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Exec), "sample-app" + ExecutableExtensionForHost())));
	}

	[Fact]
	public void Project_reference_skips_native_static_library_when_outputs_are_current()
	{
		string root = TempPath("project-reference-static-current");
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
		string packageName = "api-demo-project-reference-current";
		string repositoryRoot = FindRepositoryRoot();
		string cachedPackageRoot = Path.Combine(repositoryRoot, "cache", "pkg", packageName);
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		string packageSource = Path.Combine(root, "package-source", packageName, "src");
		string appRoot = Path.Combine(root, "sample-app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(packageSource);
		Directory.CreateDirectory(appRoot);
		string sourceRootArgument = Path.Combine(root, "package-source").Replace('\\', '/');
		if (Directory.Exists(cachedPackageRoot))
			Directory.Delete(cachedPackageRoot, recursive: true);
		File.WriteAllText(Path.Combine(packageSource, "api.camp"), "export newtype NativeHandle: nint;\n");
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			export int add(int left, int right)
			{
				return left + right;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), $$"""
			--nostdlib
			--name sample-lib
			--use-source local "{{sourceRootArgument}}"
			--use {{packageName}}:api
			src/*.camp
			""");
		try
		{
			string app = Path.Combine(appRoot, "app.camp");
			File.WriteAllText(app, """
				#build --nostdlib
				#build --name sample-app

				export int main()
				{
					return add(20, 22) - 42;
				}
				""");
			string target = NativeTargetForHost();
			string outDir = Path.Combine(appRoot, "bin");
			string referenceOutputDirectory = Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static));
			string libraryPath = NativeArtifactPathForTarget(target, NativeBuildKind.Static, referenceOutputDirectory, "sample-lib");

			ProcessResult first = RunCampc(
				"build",
				app,
				"--target",
				target,
				"--project-reference",
				libraryRoot + ":static",
				"--out-dir",
				outDir);

			AssertCommandSucceeded(first);
			Assert.Contains(libraryRoot + ":static: generated:", first.StdOut, StringComparison.Ordinal);
			Assert.True(File.Exists(libraryPath));
			DateTime firstLibraryWrite = File.GetLastWriteTimeUtc(libraryPath);

			ProcessResult second = RunCampc(
				"build",
				app,
				"--target",
				target,
				"--project-reference",
				libraryRoot + ":static",
				"--out-dir",
				outDir);

			AssertCommandSucceeded(second);
			Assert.DoesNotContain(libraryRoot + ":static: generated:", second.StdOut, StringComparison.Ordinal);
			Assert.Equal(firstLibraryWrite, File.GetLastWriteTimeUtc(libraryPath));
		}
		finally
		{
			if (Directory.Exists(cachedPackageRoot))
				Directory.Delete(cachedPackageRoot, recursive: true);
		}
	}

	[Fact]
	public void Project_reference_rejects_wrong_only_artifact_link_kind()
	{
		string root = TempPath("project-reference-only-kind");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "sample-app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), "export int value() => 1;\n");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			--artifact only-shared
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return value() - 1;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			TempPath("project-reference-only-kind-build"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("project reference requires shared linking but was requested as static", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Shared_project_reference_builds_copies_and_runs_on_macos()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Shared project-reference runtime smoke is currently macOS-only.");
		string root = TempPath("project-reference-shared-macos");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "sample-app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), "export int value() => 42;\n");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name sample-app

			export int main()
			{
				return value() - 42;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			"clang-macos-x64",
			"--project-reference",
			libraryRoot,
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		string libraryArtifactDirectory = Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Shared));
		string appArtifactDirectory = Path.Combine(outDir, ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Exec));
		Assert.True(File.Exists(Path.Combine(libraryArtifactDirectory, "libsample-lib.dylib")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "libsample-lib.dylib")));
		ProcessResult run = RunExecutable(Path.Combine(appArtifactDirectory, "sample-app"));
		Assert.Equal(0, run.ExitCode);
	}

	[Fact]
	public void Shared_project_reference_absorbs_static_dependency_on_macos()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Shared project-reference runtime smoke is currently macOS-only.");
		string root = TempPath("project-reference-shared-static-dep-macos");
		string aRoot = Path.Combine(root, "a");
		string bRoot = Path.Combine(root, "b");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(Path.Combine(aRoot, "src"));
		Directory.CreateDirectory(Path.Combine(bRoot, "src"));
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(aRoot, "src", "a.camp"), "export int aValue() => 20;\n");
		File.WriteAllText(Path.Combine(aRoot, "a.campbuild"), """
			--nostdlib
			--name a
			src/*.camp
			""");
		File.WriteAllText(Path.Combine(bRoot, "src", "b.camp"), "export int bValue() => aValue() + 22;\n");
		File.WriteAllText(Path.Combine(bRoot, "b.campbuild"), $$"""
			--nostdlib
			--name b
			--project-reference {{aRoot}}:static
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name shared-static-app

			export int main()
			{
				return bValue() - 42;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			"clang-macos-x64",
			"--project-reference",
			bRoot,
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		string appArtifactDirectory = Path.Combine(outDir, ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Exec));
		Assert.True(File.Exists(Path.Combine(aRoot, "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Static), "liba.a")));
		Assert.True(File.Exists(Path.Combine(bRoot, "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Shared), "libb.dylib")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "libb.dylib")));
		Assert.False(File.Exists(Path.Combine(appArtifactDirectory, "liba.a")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "shared-static-app")));
	}

	[Fact]
	public void Shared_project_reference_copies_transitive_shared_dependencies_on_macos()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Shared project-reference runtime smoke is currently macOS-only.");
		string root = TempPath("project-reference-shared-shared-dep-macos");
		string aRoot = Path.Combine(root, "a");
		string bRoot = Path.Combine(root, "b");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(Path.Combine(aRoot, "src"));
		Directory.CreateDirectory(Path.Combine(bRoot, "src"));
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(aRoot, "src", "a.camp"), "export int aValue() => 20;\n");
		File.WriteAllText(Path.Combine(aRoot, "a.campbuild"), """
			--nostdlib
			--name a
			src/*.camp
			""");
		File.WriteAllText(Path.Combine(bRoot, "src", "b.camp"), "export int bValue() => aValue() + 22;\n");
		File.WriteAllText(Path.Combine(bRoot, "b.campbuild"), $$"""
			--nostdlib
			--name b
			--project-reference {{aRoot}}
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name shared-shared-app

			export int main()
			{
				return bValue() - 42;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			"clang-macos-x64",
			"--project-reference",
			bRoot,
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		string appArtifactDirectory = Path.Combine(outDir, ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Exec));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "liba.dylib")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "libb.dylib")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "shared-shared-app")));
	}

	[Fact]
	public void Project_reference_cycles_report_direct_diagnostic()
	{
		string root = TempPath("project-reference-cycle");
		string aRoot = Path.Combine(root, "a");
		string bRoot = Path.Combine(root, "b");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(Path.Combine(aRoot, "src"));
		Directory.CreateDirectory(Path.Combine(bRoot, "src"));
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(aRoot, "src", "a.camp"), "export int a() => b();\n");
		File.WriteAllText(Path.Combine(bRoot, "src", "b.camp"), "export int b() => a();\n");
		File.WriteAllText(Path.Combine(aRoot, "a.campbuild"), $$"""
			--nostdlib
			--name a
			--project-reference {{bRoot}}:static
			src/*.camp
			""");
		File.WriteAllText(Path.Combine(bRoot, "b.campbuild"), $$"""
			--nostdlib
			--name b
			--project-reference {{aRoot}}:static
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --artifact none
			#build --name app

			export int main()
			{
				return a();
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--project-reference",
			aRoot + ":static",
			"--out-dir",
			Path.Combine(appRoot, "obj"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Project reference cycle detected", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("a.campbuild", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("b.campbuild", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("Stack overflow", result.StdErr, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Project_reference_transitive_static_libraries_flow_to_final_consumer()
	{
		string root = TempPath("project-reference-transitive");
		string aRoot = Path.Combine(root, "a");
		string bRoot = Path.Combine(root, "b");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(Path.Combine(aRoot, "src"));
		Directory.CreateDirectory(Path.Combine(bRoot, "src"));
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(aRoot, "src", "a.camp"), """
			export int aValue()
			{
				return 20;
			}
			""");
		File.WriteAllText(Path.Combine(bRoot, "src", "b.camp"), """
			export int bValue()
			{
				return aValue() + 22;
			}
			""");
		File.WriteAllText(Path.Combine(aRoot, "a.campbuild"), """
			--nostdlib
			--name a
			src/*.camp
			""");
		File.WriteAllText(Path.Combine(bRoot, "b.campbuild"), $$"""
			--nostdlib
			--name b
			--project-reference {{aRoot}}:static
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name transitive-app

			export int main()
			{
				return bValue() - 42;
			}
			""");
		string target = NativeTargetForHost();
		string outDir = Path.Combine(appRoot, "bin");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			bRoot + ":static",
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		Assert.True(File.Exists(Path.Combine(aRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "a_api.camp")));
		Assert.True(File.Exists(Path.Combine(bRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "b_api.camp")));
		Assert.True(File.Exists(Path.Combine(outDir, ArtifactDirectoryForHost(NativeBuildKind.Exec), "transitive-app" + ExecutableExtensionForHost())));
	}

	[Fact]
	public void Library_api_preserves_implements_generic_constraints()
	{
		string root = TempPath("implements-api");
		Directory.CreateDirectory(root);
		string source = Path.Combine(root, "refcount.camp");
		File.WriteAllText(source, """
			using Std;

			extern void* malloc(nuint size);
			extern void free(void* pointer);

			export escaped interface IRefCount
			{
				void retain();
				void release();
			}

			export escaped T* autorelease<T: implements IRefCount>(
				escaped T* this,
				vtableof(T: IRefCount))
			{
				return this;
			}

			export class RefThing: IRefCount
			{
				void retain(): IRefCount {}
				void release(): IRefCount {}
			}
			""");
		string secondSource = Path.Combine(root, "consumer.camp");
		File.WriteAllText(secondSource, """
			using Std;

			export interface INamed
			{
				string getName();
			}

			export struct NamedRef: INamed
			{
				string getName(): INamed => "named";
			}
			""");

		string repositoryRoot = FindRepositoryRoot();
		CompilerRequest request = new()
		{
			WorkingDirectory = repositoryRoot,
			RuntimeRoot = Path.Combine(repositoryRoot, "bin"),
			NoStdLib = true,
			InspectApi = true
		};
		request.Files.Add(Path.GetRelativePath(repositoryRoot, source));
		request.Files.Add(Path.GetRelativePath(repositoryRoot, secondSource));

		CompilerResult result = CompilerDriver.Execute(request);

		Assert.Equal(0, result.ExitCode);
		string api = result.StdOut;
		Assert.Contains("autorelease<T: implements IRefCount>", api, StringComparison.Ordinal);
		Assert.DoesNotContain("autorelease<T: IRefCount>", api, StringComparison.Ordinal);
		Assert.Contains("export extern class RefThing : IRefCount", api, StringComparison.Ordinal);
		Assert.Contains("export struct NamedRef", api, StringComparison.Ordinal);
		Assert.DoesNotContain("export struct NamedRef : INamed", api, StringComparison.Ordinal);
		Assert.Equal(1, CountOccurrences(api, "using Std;"));
	}

	[Fact]
	public void Interface_object_vtable_thunks_are_emitted_with_implementing_type_source()
	{
		string root = TempPath("interface-object-vtable-thunks");
		Directory.CreateDirectory(root);
		string interfaces = Path.Combine(root, "interfaces.camp");
		File.WriteAllText(interfaces, """
			export interface IRefCount
			{
				void retain();
				void release();
			}
			""");
		string component = Path.Combine(root, "component.camp");
		File.WriteAllText(component, """
			extern void* malloc(nuint size);
			extern void free(void* pointer);

			export escaped class Component: IRefCount
			{
				nuint refct;

				export Component()
				{
				}

				export void retain(): IRefCount
				{
					this.refct++;
				}

				export void release(): IRefCount
				{
					if (this.refct > 0)
						this.refct--;
				}
			}

			export int main()
			{
				auto component = new Component() finally delete;
				IRefCount* refCount = component;
				return refCount == null ? 1 : 0;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			interfaces,
			component,
			"--nostdlib",
			"--target",
			NativeTargetForHost(),
			"--out-dir",
			TempPath("interface-object-vtable-thunks-bin"));

		Assert.Equal(0, result.ExitCode);
	}

	[Fact]
	public void Project_reference_consumes_exported_interface_accessors_and_vtables()
	{
		string root = TempPath("project-reference-interface-accessors");
		string libraryRoot = Path.Combine(root, "interfaces");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "interfaces.camp"), """
			extern void* malloc(nuint size);
			extern void free(void* pointer);

			export interface IValue
			{
				export int value();
			}

			export class Counter: IValue
			{
				int value(): IValue
				{
					return 1;
				}
			}

			export extern class NativeCounter: IValue
			{
			}

			export extern class NativeDerived: NativeCounter
			{
			}

			export struct StructCounter: IValue
			{
				int value(): IValue
				{
					return 2;
				}
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "interfaces.campbuild"), """
			--nostdlib
			--name interfaces
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name interface-app

			int readValue(IValue* value)
			{
				return value.value();
			}

			int readGeneric<T: implements IValue>(T* value, vtableof(T: IValue))
			{
				return value.value();
			}

			export int main()
			{
				auto counter = new Counter() finally delete;
				int total = 0;
				IValue* assigned = counter;
				total += readValue(assigned);
				total += readValue((IValue*)counter);
				total += readValue(counter.IValue);
				total += readValue(counter.getIValue());
				total += readGeneric<Counter>(counter);
				return total == 5 ? 0 : total;
			}
		""");
		string outDir = Path.Combine(appRoot, "bin");
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			outDir);

		AssertCommandSucceeded(result);
		Assert.Contains("generated: interface-app", result.StdOut, StringComparison.Ordinal);
		string api = File.ReadAllText(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "interfaces_api.camp"));
		Assert.Contains("export extern class Counter : IValue", api, StringComparison.Ordinal);
		Assert.Contains("export extern constof(this) IValue* getIValue();", api, StringComparison.Ordinal);
		Assert.Contains("export extern class NativeCounter : IValue", api, StringComparison.Ordinal);
		Assert.Contains("export extern class NativeDerived : NativeCounter", api, StringComparison.Ordinal);
		Assert.Contains("export struct StructCounter", api, StringComparison.Ordinal);
		Assert.DoesNotContain("export struct StructCounter : IValue", api, StringComparison.Ordinal);
		string cApi = File.ReadAllText(Path.Combine(outDir, ArtifactDirectoryForHost(NativeBuildKind.Exec), "build", "interfaces_api.h"));
		Assert.Contains("extern const IValue *Counter_IValue;", cApi, StringComparison.Ordinal);
		Assert.DoesNotContain("StructCounter_IValue", cApi, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(outDir, ArtifactDirectoryForHost(NativeBuildKind.Exec), "interface-app" + ExecutableExtensionForHost()));
		Assert.Equal(0, run.ExitCode);
		Assert.Equal("", run.StdErr);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Default_windows_target_follows_visual_studio_environment()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("MSVC default target selection only applies on Windows.");
		string temp = CreateTempCase("msvc-default-target/main.camp", """
			#if WIN32
			export int selectedTarget()
			{
				return 86;
			}
			#endif

			#if WIN64
			export int selectedTarget()
			{
				return 64;
			}
			#endif
			""");

		ProcessResult x86 = RunCampc(
			new Dictionary<string, string?> { ["VSCMD_ARG_TGT_ARCH"] = "x86", ["Platform"] = null },
			"dump",
			"declarations",
			temp,
			"--nostdlib");
		ProcessResult x64 = RunCampc(
			new Dictionary<string, string?> { ["VSCMD_ARG_TGT_ARCH"] = "x64", ["Platform"] = null },
			"dump",
			"declarations",
			temp,
			"--nostdlib");

		Assert.Equal(0, x86.ExitCode);
		Assert.Contains("return 86", x86.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("return 64", x86.StdOut, StringComparison.Ordinal);
		Assert.Equal(0, x64.ExitCode);
		Assert.Contains("return 64", x64.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("return 86", x64.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Msvc_target_requires_loaded_visual_studio_environment()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("MSVC environment validation only applies on Windows.");
		string temp = CreateTempCase("msvc-environment-missing/main.camp", """
			export int value()
			{
				return 1;
			}
			""");

		ProcessResult result = RunCampc(
			new Dictionary<string, string?> { ["VSCMD_ARG_TGT_ARCH"] = null, ["Platform"] = null },
			"build",
			temp,
			"--nostdlib",
			"--artifact",
			"static",
			"--target",
			"msvc-windows-x64",
			"--out-dir",
			TempPath("msvc-environment-missing-out"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Target 'msvc-windows-x64' requires a Visual Studio C++ environment", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("Native build command failed", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Msvc_target_architecture_must_match_visual_studio_environment()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("MSVC environment validation only applies on Windows.");
		string temp = CreateTempCase("msvc-architecture-mismatch/main.camp", """
			export int value()
			{
				return 1;
			}
			""");

		ProcessResult result = RunCampc(
			new Dictionary<string, string?> { ["VSCMD_ARG_TGT_ARCH"] = "x86" },
			"build",
			temp,
			"--nostdlib",
			"--artifact",
			"static",
			"--target",
			"msvc-windows-x64",
			"--out-dir",
			TempPath("msvc-architecture-mismatch-out"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Target 'msvc-windows-x64' requires MSVC target architecture 'x64'", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("current Visual Studio environment targets 'x86'", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Project_reference_links_native_static_library_with_msvc()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools are not available on PATH.");
		string root = TempPath("project-reference-msvc");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "sample-app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			export int add(int left, int right)
			{
				return left + right;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "sample-app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name sample-app

			export int main()
			{
				return add(20, 22) - 42;
			}
			""");
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(result);
		Assert.Contains("generated: sample-app.exe", result.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "sample-lib.lib")));
		Assert.True(File.Exists(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "sample-lib_api.camp")));
		Assert.True(File.Exists(Path.Combine(appRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Exec), "build", "sample_lib_api.h")));
		string cApi = File.ReadAllText(Path.Combine(appRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Exec), "build", "sample_lib_api.h"));
		Assert.DoesNotContain("__declspec(dllimport)", cApi, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(appRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Exec), "sample-app.exe"));
		Assert.Equal(0, run.ExitCode);
		Assert.Equal("", run.StdErr);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Project_reference_links_native_shared_library_with_msvc()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools are not available on PATH.");
		string root = TempPath("project-reference-msvc-shared");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "sample-app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		File.WriteAllText(Path.Combine(librarySource, "library.camp"), """
			export int add(int left, int right)
			{
				return left + right;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "sample-app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name sample-app

			export int main()
			{
				return add(20, 22) - 42;
			}
			""");
		string target = NativeTargetForHost();
		string appArtifactDirectory = Path.Combine(appRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Exec));
		string libraryArtifactDirectory = Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Shared));

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot,
			"--out-dir",
			Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(result);
		string importLibrary = Path.Combine(libraryArtifactDirectory, "sample-lib.lib");
		Assert.True(File.Exists(importLibrary));
		Assert.True(File.Exists(Path.Combine(libraryArtifactDirectory, "sample-lib.dll")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "sample-lib.dll")));
		string cApi = File.ReadAllText(Path.Combine(appArtifactDirectory, "build", "sample_lib_api.h"));
		Assert.Contains("__declspec(dllimport)", cApi, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(appArtifactDirectory, "sample-app.exe"));
		Assert.Equal(0, run.ExitCode);
		Assert.Equal("", run.StdErr);

		File.SetLastWriteTimeUtc(importLibrary, DateTime.UtcNow.AddHours(-1));
		ProcessResult second = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot,
			"--out-dir",
			Path.Combine(appRoot, "bin"));

		AssertCommandSucceeded(second);
		Assert.DoesNotContain(libraryRoot + ": generated:", second.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	[Trait("Category", "MsvcCompile")]
	public void Project_reference_rebuilds_native_static_library_when_source_changes()
	{
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools are not available on PATH.");
		string root = TempPath("project-reference-msvc-rebuild");
		string libraryRoot = Path.Combine(root, "sample-lib");
		string librarySource = Path.Combine(libraryRoot, "src");
		string appRoot = Path.Combine(root, "sample-app");
		Directory.CreateDirectory(librarySource);
		Directory.CreateDirectory(appRoot);
		string libraryFile = Path.Combine(librarySource, "library.camp");
		File.WriteAllText(libraryFile, """
			export int getValue()
			{
				return 1;
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "sample-lib.campbuild"), """
			--nostdlib
			--name sample-lib
			src/*.camp
			""");
		string app = Path.Combine(appRoot, "sample-app.camp");
		File.WriteAllText(app, """
			#build --nostdlib
			#build --name sample-app

			export int main()
			{
				return getValue();
			}
		""");
		string target = NativeTargetForHost();
		string libraryPath = Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static), "sample-lib.lib");
		string executablePath = Path.Combine(appRoot, "bin", ArtifactDirectoryForHost(NativeBuildKind.Exec), "sample-app.exe");

		ProcessResult firstBuild = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			Path.Combine(appRoot, "bin"));

			AssertCommandSucceeded(firstBuild);
			Assert.True(File.Exists(libraryPath));
		DateTime firstLibraryWrite = File.GetLastWriteTimeUtc(libraryPath);
		ProcessResult firstRun = RunExecutable(executablePath);
		Assert.Equal(1, firstRun.ExitCode);

		File.WriteAllText(libraryFile, """
			export int getValue()
			{
				return 2;
			}
			""");

		ProcessResult secondBuild = RunCampc(
			"build",
			app,
			"--target",
			target,
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			Path.Combine(appRoot, "bin"));

			AssertCommandSucceeded(secondBuild);
			Assert.True(File.GetLastWriteTimeUtc(libraryPath) >= firstLibraryWrite);
		ProcessResult secondRun = RunExecutable(executablePath);
		Assert.Equal(2, secondRun.ExitCode);
	}

	[Fact]
	public void Virtual_base_layout_is_lowered_before_out_of_order_derived_class()
	{
		string root = TempPath("virtual-out-of-order");
		Directory.CreateDirectory(root);
		string derived = Path.Combine(root, "derived.camp");
		string baseFile = Path.Combine(root, "base.camp");
		File.WriteAllText(derived, """
			export sealed class Derived: Base
			{
				export override int value()
				{
					return 2;
				}
			}
			""");
		File.WriteAllText(baseFile, """
			export virtual class Base
			{
				export virtual int value()
				{
					return 1;
				}
			}
			""");
		string buildDir = TempPath("virtual-out-of-order-build");

		ProcessResult result = RunCampc("build", derived, baseFile, "--artifact", "none", "--out-dir", buildDir);

		Assert.Equal(0, result.ExitCode);
		string privateHeader = Directory.GetFiles(Path.Combine(buildDir, ArtifactDirectoryForHost(null), "build"), "*_private.h").Single();
		string header = File.ReadAllText(privateHeader);
		Assert.Contains("_Base *_vt;", header, StringComparison.Ordinal);
		Assert.DoesNotContain("_Derived *_vt;", header, StringComparison.Ordinal);
	}

	[Fact]
	public void Inherited_virtual_destructor_thunk_is_visible_across_generated_source_files()
	{
		string root = TempPath("virtual-destructor-cross-file");
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
		string source = Path.Combine(root, "src");
		Directory.CreateDirectory(source);
		File.WriteAllText(Path.Combine(source, "alloc.camp"), """
			export extern void* malloc(nuint size);
			export extern void free(void* ptr);
			""");
		File.WriteAllText(Path.Combine(source, "component.camp"), """
			export virtual escaped class Component
			{
				export Component()
				{
				}

				export virtual ~Component()
				{
				}
			}
			""");
		File.WriteAllText(Path.Combine(source, "control.camp"), """
			export virtual escaped class Control: Component
			{
				export Control()
				{
				}

				override ~Control()
				{
				}
			}
			""");
		File.WriteAllText(Path.Combine(source, "button.camp"), """
			export sealed escaped class Button: Control
			{
				export Button()
				{
				}
			}
			""");
		File.WriteAllText(Path.Combine(root, "widgets.campbuild"), """
			--nostdlib
			--name widgets
			--artifact static
			src/*.camp
			""");

		string outDir = Path.Combine(root, "bin");
		ProcessResult result = RunCampc("build", Path.Combine(root, "widgets.campbuild"), "--target", NativeTargetForHost(), "--out-dir", outDir);

		AssertCommandSucceeded(result);
		string artifactDir = Path.Combine(outDir, ArtifactDirectoryForHost(NativeBuildKind.Static));
		string privateHeader = File.ReadAllText(Path.Combine(artifactDir, "build", "widgets_private.h"));
		Assert.Contains("void Control__op_delete(Component *ctx);", privateHeader, StringComparison.Ordinal);
		string buttonC = File.ReadAllText(Path.Combine(artifactDir, "build", "button.c"));
		Assert.Contains(".op_delete = Control__op_delete", buttonC, StringComparison.Ordinal);
		string api = File.ReadAllText(Path.Combine(artifactDir, "widgets_api.camp"));
		Assert.Contains("export extern ~Component();", api, StringComparison.Ordinal);
		Assert.DoesNotContain("void ~Component", api, StringComparison.Ordinal);
	}

	[Fact]
	public void Project_reference_api_uses_inherited_virtual_surface_for_overrides()
	{
		string root = TempPath("project-reference-virtual-api");
		string libraryRoot = Path.Combine(root, "widgets");
		string librarySource = Path.Combine(libraryRoot, "src");
		Directory.CreateDirectory(librarySource);
		File.WriteAllText(Path.Combine(librarySource, "alloc.camp"), """
			export extern void* malloc(nuint size);
			export extern void free(void* ptr);
			""");
		File.WriteAllText(Path.Combine(librarySource, "button.camp"), """
			export sealed escaped class Button: Control
			{
				export override int value()
				{
					return 2;
				}
			}
			""");
		File.WriteAllText(Path.Combine(librarySource, "control.camp"), """
			export virtual escaped class Control
			{
				export virtual int value()
				{
					return 1;
				}
			}
			""");
		File.WriteAllText(Path.Combine(libraryRoot, "widgets.campbuild"), """
			--nostdlib
			--name widgets
			src/*.camp
			""");
		string app = CreateTempCase("project_reference_virtual_api_app.camp", """
			#build --nostdlib
			#build --artifact none

			export int readButton(Button* button)
			{
				Control* control = button;
				return control.value() - 2;
			}
			""");

		ProcessResult result = RunCampc(
			"build",
			app,
			"--target",
			"clang-macos-x64",
			"--project-reference",
			libraryRoot + ":static",
			"--out-dir",
			TempPath("project-reference-virtual-api-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: project_reference_virtual_api_app.c", result.StdOut, StringComparison.Ordinal);
		string api = File.ReadAllText(Path.Combine(libraryRoot, "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Static), "widgets_api.camp"));
		Assert.Contains("export escaped extern class Control", api, StringComparison.Ordinal);
		Assert.Contains("export escaped extern class Button : Control", api, StringComparison.Ordinal);
		Assert.Contains("export extern int value();", api, StringComparison.Ordinal);
		Assert.DoesNotContain("virtual class", api, StringComparison.Ordinal);
		Assert.DoesNotContain("sealed escaped class", api, StringComparison.Ordinal);
		Assert.DoesNotContain("virtual int value", api, StringComparison.Ordinal);
		Assert.DoesNotContain("override int value", api, StringComparison.Ordinal);
		Assert.Equal(1, CountOccurrences(api, "int value();"));
	}

	[Fact]
	public void Use_source_resolves_live_unversioned_package_sources()
	{
		string root = TempPath("live-use-source");
		string sourceRoot = Path.Combine(root, "package-source");
		string cachedPackageRoot = Path.Combine(FindRepositoryRoot(), "cache", "pkg", "live-demo");
		if (Directory.Exists(cachedPackageRoot))
			Directory.Delete(cachedPackageRoot, recursive: true);
		string packageSource = Path.Combine(sourceRoot, "live-demo", "src");
		Directory.CreateDirectory(packageSource);
		string packageFile = Path.Combine(packageSource, "demo.camp");
		string sourceRootArgument = sourceRoot.Replace('\\', '/');
		File.WriteAllText(packageFile, """
			export int liveValue()
			{
				return 1;
			}
			""");
		string app = CreateTempCase("live_use_source_app.camp", $$"""
			#build --nostdlib
			#build --artifact none
			#build --use-source local "{{sourceRootArgument}}"
			#build --use live-demo

			export int main()
			{
				return liveValue() - 1;
			}
			""");

		ProcessResult first = RunCampc("build", app, "--out-dir", TempPath("live-use-source-build-1"));

		Assert.Equal(0, first.ExitCode);
		Assert.Contains("generated: live_use_source_app.c", first.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(cachedPackageRoot, "live", "bin", ArtifactDirectoryForHost(NativeBuildKind.Shared), "live-demo_api.camp")));
		Assert.False(Directory.Exists(Path.Combine(sourceRoot, "live-demo", "bin")));
		Assert.False(Directory.Exists(Path.Combine(sourceRoot, "live-demo", "build")));

		File.WriteAllText(packageFile, """
			export int liveChanged()
			{
				return 2;
			}
			""");
		File.SetLastWriteTimeUtc(packageFile, DateTime.UtcNow.AddSeconds(5));
		File.WriteAllText(app, $$"""
			#build --nostdlib
			#build --artifact none
			#build --use-source local "{{sourceRootArgument}}"
			#build --use live-demo

			export int main()
			{
				return liveChanged() - 2;
			}
			""");

		ProcessResult second = RunCampc("build", app, "--out-dir", TempPath("live-use-source-build-2"));

		Assert.Equal(0, second.ExitCode);
		Assert.Contains("generated: live_use_source_app.c", second.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(cachedPackageRoot, "live", "bin", ArtifactDirectoryForHost(NativeBuildKind.Shared), "live-demo_api.camp")));
	}

	[Fact]
	public void Live_package_dependency_builds_shared_by_default_and_static_separately_on_macos()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Shared package runtime smoke is currently macOS-only.");
		string root = TempPath("live-package-shared-static");
		string sourceRoot = Path.Combine(root, "package-source");
		string cachedPackageRoot = Path.Combine(FindRepositoryRoot(), "cache", "pkg", "live-link-demo");
		if (Directory.Exists(cachedPackageRoot))
			Directory.Delete(cachedPackageRoot, recursive: true);
		string packageSource = Path.Combine(sourceRoot, "live-link-demo", "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(packageSource);
		Directory.CreateDirectory(appRoot);
		string sourceRootArgument = sourceRoot.Replace('\\', '/');
		File.WriteAllText(Path.Combine(packageSource, "demo.camp"), """
			export int liveValue()
			{
				return 42;
			}
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, $$"""
			#build --nostdlib
			#build --name live-package-app
			#build --use-source local "{{sourceRootArgument}}"
			#build --use live-link-demo

			export int main()
			{
				return liveValue() - 42;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");

		ProcessResult shared = RunCampc("build", app, "--target", "clang-macos-x64", "--out-dir", outDir);

		AssertCommandSucceeded(shared);
		string sharedCacheDirectory = Path.Combine(cachedPackageRoot, "live", "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Shared));
		string appArtifactDirectory = Path.Combine(outDir, ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Exec));
		Assert.True(File.Exists(Path.Combine(sharedCacheDirectory, "liblive-link-demo.dylib")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "liblive-link-demo.dylib")));
		Assert.True(File.Exists(Path.Combine(appArtifactDirectory, "live-package-app")));

		File.WriteAllText(app, $$"""
			#build --nostdlib
			#build --name live-package-static-app
			#build --use-source local "{{sourceRootArgument}}"
			#build --use live-link-demo:static

			export int main()
			{
				return liveValue() - 42;
			}
			""");
		ProcessResult staticResult = RunCampc("build", app, "--target", "clang-macos-x64", "--out-dir", outDir);

		AssertCommandSucceeded(staticResult);
		string staticCacheDirectory = Path.Combine(cachedPackageRoot, "live", "bin", ArtifactDirectoryForTarget("clang-macos-x64", NativeBuildKind.Static));
		Assert.True(File.Exists(Path.Combine(staticCacheDirectory, "liblive-link-demo.a")));
		Assert.True(File.Exists(Path.Combine(staticCacheDirectory, "live-link-demo_api.camp")));
		Assert.NotEqual(sharedCacheDirectory, staticCacheDirectory);
	}

	[Fact]
	public void Live_static_package_dependency_lowers_iterators_before_api_emission()
	{
		string root = TempPath("live-static-package-iterator-api");
		string sourceRoot = Path.Combine(root, "package-source");
		string cachedPackageRoot = Path.Combine(FindRepositoryRoot(), "cache", "pkg", "live-iterator-demo");
		if (Directory.Exists(cachedPackageRoot))
			Directory.Delete(cachedPackageRoot, recursive: true);
		string packageSource = Path.Combine(sourceRoot, "live-iterator-demo", "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(packageSource);
		Directory.CreateDirectory(appRoot);
		string sourceRootArgument = sourceRoot.Replace('\\', '/');
		File.WriteAllText(Path.Combine(packageSource, "demo.camp"), """
			#build --nostdlib

			namespace LiveIteratorDemo;

			public struct View
			{
				const char[] text;
			}

			public nuint viewLength(View view)
			{
				return view.text.length;
			}

			public fixed struct Writer
			{
				fixed Frame[2] stack;
			}

			public struct iter View parts(const char[] source)
			{
				yield { { source.elements, source.length } };
			}

			enum Container: byte
			{
				ARRAY,
			}

			struct Frame
			{
				Container container;
			}
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, $$"""
			#build --nostdlib
			#build --name live-iterator-app
			#build --artifact static
			#build --use-source local "{{sourceRootArgument}}"
			#build --use live-iterator-demo:static

			using LiveIteratorDemo;

			export int value()
			{
				int total = 0;
				foreach (auto view in parts("abc"))
					total += (int)viewLength(view);
				return total - 3;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc("build", app, "--target", target, "--out-dir", outDir);

		AssertCommandSucceeded(result);
		string staticCacheDirectory = Path.Combine(cachedPackageRoot, "live", "bin", ArtifactDirectoryForTarget(target, NativeBuildKind.Static));
		string apiPath = Path.Combine(staticCacheDirectory, "live-iterator-demo_api.camp");
		Assert.True(File.Exists(apiPath));
		string api = File.ReadAllText(apiPath);
		Assert.DoesNotContain("op_delete", api, StringComparison.Ordinal);
		Assert.Contains("public extern void destroy();", api, StringComparison.Ordinal);
	}

	[Fact]
	public void Live_api_package_dependency_emits_headers_without_native_library()
	{
		string root = TempPath("live-package-api-only");
		string sourceRoot = Path.Combine(root, "package-source");
		string cachedPackageRoot = Path.Combine(FindRepositoryRoot(), "cache", "pkg", "api-demo");
		string packageSource = Path.Combine(sourceRoot, "api-demo", "src");
		string appRoot = Path.Combine(root, "app");
		Directory.CreateDirectory(packageSource);
		Directory.CreateDirectory(appRoot);
		string sourceRootArgument = sourceRoot.Replace('\\', '/');
		File.WriteAllText(Path.Combine(packageSource, "api.camp"), """
			export newtype NativeHandle: nint;
			export extern NativeHandle getNativeHandle();
			""");
		string app = Path.Combine(appRoot, "app.camp");
		File.WriteAllText(app, $$"""
			#build --nostdlib
			#build --name api-package-app
			#build --use-source local "{{sourceRootArgument}}"
			#build --use api-demo:api

			export int main()
			{
				NativeHandle handle = default;
				return 0;
			}
			""");
		string outDir = Path.Combine(appRoot, "bin");
		string target = NativeTargetForHost();
		if (Directory.Exists(cachedPackageRoot))
			Directory.Delete(cachedPackageRoot, recursive: true);

		try
		{
			ProcessResult result = RunCampc("build", app, "--target", target, "--out-dir", outDir);

			AssertCommandSucceeded(result);
			string apiCacheDirectory = Path.Combine(cachedPackageRoot, "live", "bin", ArtifactDirectoryForTarget(target, DependencyLinkKind.Api));
			Assert.True(File.Exists(Path.Combine(apiCacheDirectory, "api-demo_api.camp")));
			Assert.True(File.Exists(Path.Combine(apiCacheDirectory, "api-demo_api.h")));
			Assert.True(File.Exists(Path.Combine(apiCacheDirectory, "api-demo_api.json")));
			Assert.DoesNotContain(Directory.GetFiles(apiCacheDirectory), path => Path.GetExtension(path) is ".a" or ".lib" or ".dll" or ".dylib" or ".so");
		}
		finally
		{
			if (Directory.Exists(cachedPackageRoot))
				Directory.Delete(cachedPackageRoot, recursive: true);
		}
	}

	[Fact]
	public void Response_file_use_option_does_not_consume_source_patterns()
	{
		string root = TempPath("response-use-source-pattern");
		string sourceRoot = Path.Combine(root, "package-source");
		string packageSource = Path.Combine(sourceRoot, "live-demo", "src");
		string appRoot = Path.Combine(root, "app");
		string appSource = Path.Combine(appRoot, "src");
		Directory.CreateDirectory(packageSource);
		Directory.CreateDirectory(appSource);
		File.WriteAllText(Path.Combine(packageSource, "demo.camp"), """
			export int liveValue()
			{
				return 42;
			}
			""");
		File.WriteAllText(Path.Combine(appSource, "main.camp"), """
			export int main()
			{
				return liveValue() - 42;
			}
			""");
		string buildFile = Path.Combine(appRoot, "app.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--name app
			--use-source local ../package-source
			--use live-demo
			src/*.camp
			""");

		ProcessResult result = RunCampc("build", "@" + buildFile);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Recursive_glob_matches_files_directly_under_root()
	{
		string root = TempPath("recursive-glob-root");
		string sourceRoot = Path.Combine(root, "src");
		string nestedRoot = Path.Combine(sourceRoot, "nested");
		Directory.CreateDirectory(nestedRoot);
		File.WriteAllText(Path.Combine(sourceRoot, "main.camp"), """
			export int main()
			{
				return helper() - 7;
			}
			""");
		File.WriteAllText(Path.Combine(nestedRoot, "helper.camp"), """
			export int helper()
			{
				return 7;
			}
			""");
		string buildFile = Path.Combine(root, "sample.campbuild");
		File.WriteAllText(buildFile, """
			--nostdlib
			--artifact none
			--name recursive_glob_root
			src/**/*.camp
			""");

		ProcessResult result = RunCampc("build", "@" + buildFile, "--out-dir", TempPath("recursive-glob-root-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: main.c", result.StdOut, StringComparison.Ordinal);
		Assert.Contains("generated: helper.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Include_files_contribute_build_pragmas_without_becoming_project_sources()
	{
		string api = CreateTempCase("include_pragmas_api.camp", """
			#build --nostdlib
			#build --artifact none

			export extern void includedOnly();
			""");
		string source = CreateTempCase("include_pragmas_main.camp", """
			export void main()
			{
			}
			""");

		ProcessResult result = RunCampc("build", source, "-i", api, "--out-dir", TempPath("include-pragma-build"));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("generated: include_pragmas_main.c", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("include_pragmas_api.c", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("_api.camp", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Metadata_omits_standard_library_declarations()
	{
		string temp = CreateTempCase("metadata_std_filter.camp", """
			using Std;

			export int meaning()
			{
				Console.writeLine("hi");
				return 42;
			}
			""");
		string outDir = TempPath("metadata-std-filter-out");

		ProcessResult result = RunCampc("build", temp, "--artifact", "none", "--metadata", "export", "--out-dir", outDir, "--name", "metadata_std_filter");

		Assert.Equal(0, result.ExitCode);
		string metadataPath = Path.Combine(outDir, ArtifactDirectoryForHost(null), "metadata_std_filter_api.json");
		using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
		string[] declarationNames = metadata.RootElement.GetProperty("declarations")
			.EnumerateArray()
			.Select(static declaration => declaration.GetProperty("name").GetString() ?? "")
			.ToArray();
		Assert.Contains("meaning", declarationNames);
		Assert.DoesNotContain("Console", declarationNames);
		Assert.DoesNotContain("Allocator", declarationNames);
		Assert.DoesNotContain("malloc", declarationNames);
	}

	[Fact]
	public void Shared_library_api_omits_standard_library_declarations()
	{
		string temp = CreateTempCase("shared_std_filter.camp", """
			using Std;

			export int meaning()
			{
				Console.writeLine("hi");
				return 42;
			}
			""");
		string outDir = TempPath("shared-std-filter-out");
		string target = NativeTargetForHost();

		ProcessResult result = RunCampc("build", temp, "--artifact", "shared", "--target", target, "--out-dir", outDir, "--name", "shared_std_filter");

		AssertCommandSucceeded(result);
		string artifactDirectory = Path.Combine(outDir, ArtifactDirectoryForTarget(target, NativeBuildKind.Shared));
		string campApi = File.ReadAllText(Path.Combine(artifactDirectory, "shared_std_filter_api.camp"));
		string cApi = File.ReadAllText(Path.Combine(artifactDirectory, "shared_std_filter_api.h"));
		Assert.Contains("export extern int meaning();", campApi, StringComparison.Ordinal);
		Assert.DoesNotContain("Console", campApi, StringComparison.Ordinal);
		Assert.DoesNotContain("Allocator", campApi, StringComparison.Ordinal);
		Assert.DoesNotContain("malloc", campApi, StringComparison.Ordinal);
		Assert.Contains("meaning", cApi, StringComparison.Ordinal);
		Assert.DoesNotContain("Console", cApi, StringComparison.Ordinal);
		Assert.DoesNotContain("Allocator", cApi, StringComparison.Ordinal);
		Assert.DoesNotContain("malloc", cApi, StringComparison.Ordinal);
	}

	[Fact]
	public void Include_pragmas_discovered_from_source_pragmas_contribute_build_pragmas()
	{
		string api = CreateTempCase("discovered_include_pragmas_api.camp", """
			#build --reference missing-one.a missing-two.a

			export extern void includedOnly();
			""");
		string source = CreateTempCase("discovered_include_pragmas_main.camp", $$"""
			#build --nostdlib
			#build --artifact exec
			#build --include {{api}}

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", source, "--target", NativeTargetForHost(), "--out-dir", TempPath("discovered-include-pragma-out"));
		string output = result.StdOut + result.StdErr;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("missing-one.a", output, StringComparison.Ordinal);
		Assert.Contains("missing-two.a", output, StringComparison.Ordinal);
		Assert.DoesNotContain("discovered_include_pragmas_api.c", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Run_rejects_non_exec_artifact_before_building()
	{
		string temp = CreateTempCase("run_static.camp", """
			#build --nostdlib

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("run", temp, "--artifact", "static");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("run requires --artifact exec", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_pragma_rejects_unknown_subsystem()
	{
		string temp = CreateTempCase("subsystem_foobar.camp", """
			#build --nostdlib
			#build --artifact exec
			#build --subsystem foobar

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp);

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Subsystem 'foobar' is not valid. Expected windows.", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("generated:", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Reference_alias_accepts_multiple_values()
	{
		string temp = CreateTempCase("reference_alias.camp", """
			#build --nostdlib
			#build --artifact exec

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "-r", "missing-one.a", "missing-two.a", "--target", NativeTargetForHost(), "--out-dir", TempPath("reference-out"));
		string output = result.StdOut + result.StdErr;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("missing-one.a", output, StringComparison.Ordinal);
		Assert.Contains("missing-two.a", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Framework_alias_accepts_multiple_values()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Framework linker flag shape is only valid on macOS targets.");
		string temp = CreateTempCase("framework_alias.camp", """
			#build --nostdlib
			#build --artifact exec

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "-f", "MissingOne", "MissingTwo", "--target", "clang-macos-x64", "--out-dir", TempPath("framework-out"));
		string output = result.StdOut + result.StdErr;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("-framework MissingOne", output, StringComparison.Ordinal);
		Assert.Contains("-framework MissingTwo", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_pragmas_allow_multiple_frameworks_after_switch()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Framework linker flag shape is only valid on macOS targets.");
		string temp = CreateTempCase("framework_pragma_multi.camp", """
			#build --nostdlib
			#build --artifact exec
			#build --framework MissingOne MissingTwo

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "--target", "clang-macos-x64", "--out-dir", TempPath("framework-pragma-out"));
		string output = result.StdOut + result.StdErr;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("-framework MissingOne", output, StringComparison.Ordinal);
		Assert.Contains("-framework MissingTwo", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Frameworks_are_rejected_on_targets_that_do_not_allow_them()
	{
		string temp = CreateTempCase("framework_unsupported_target.camp", """
			#build --nostdlib
			#build --artifact exec

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "--target", "msvc-windows-x64", "--framework", "Cocoa");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Target 'msvc-windows-x64' does not support framework linking.", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("Native build command", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Frameworks_are_rejected_for_static_artifacts()
	{
		string temp = CreateTempCase("framework_static.camp", """
			#build --nostdlib
			#build --artifact static

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "--target", "clang-macos-x64", "--framework", "Cocoa");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("--framework cannot be used with --artifact static.", result.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("Native build command", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Use_alias_accepts_multiple_values()
	{
		string temp = CreateTempCase("use_alias.camp", """
			#build --nostdlib
			#build --artifact none

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "-u", "missing-one@1.0.0", "missing-two@1.0.0");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Package 'missing-one@1.0.0' is not installed.", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_pragmas_allow_multiple_references_after_switch()
	{
		string temp = CreateTempCase("reference_pragma_multi.camp", """
			#build --nostdlib
			#build --artifact exec
			#build --reference missing-one.a missing-two.a

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "--target", NativeTargetForHost(), "--out-dir", TempPath("reference-pragma-out"));
		string output = result.StdOut + result.StdErr;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("missing-one.a", output, StringComparison.Ordinal);
		Assert.Contains("missing-two.a", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Windows_subsystem_uses_windows_executable_template()
	{
		string temp = CreateTempCase("subsystem_windows.camp", """
			#build --nostdlib

			export int main()
			{
				return 0;
			}
			""");

		ProcessResult result = RunCampc("build", temp, "--artifact", "exec", "--subsystem", "windows", "--target", "clang-macos-x64");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("does not define a [build] winexe template", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Gcc_linux_x64_target_builds_executable_on_linux()
	{
		if (!OperatingSystem.IsLinux())
			Assert.Skip("Linux GCC target smoke tests only run on Linux.");
		if (!GccCanLink("-m64"))
			Assert.Skip("gcc -m64 cannot link a native executable on this host.");

		string temp = CreateTempCase("gcc_linux_x64.camp", """
			#build --nostdlib
			#build --artifact exec
			#build --name gcc-linux-x64-smoke

			export int main()
			{
				return 0;
			}
			""");
		string outDir = TempPath("gcc-linux-x64-out");

		ProcessResult result = RunCampc("build", temp, "--target", "gcc-linux-x64", "--out-dir", outDir);

		AssertCommandSucceeded(result);
		Assert.Contains("generated: gcc-linux-x64-smoke", result.StdOut, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(outDir, ArtifactDirectoryForTarget("gcc-linux-x64", NativeBuildKind.Exec), "gcc-linux-x64-smoke"));
		Assert.Equal(0, run.ExitCode);
	}

	[Fact]
	public void Gcc_linux_x86_target_builds_executable_on_linux_when_multilib_is_available()
	{
		if (!OperatingSystem.IsLinux())
			Assert.Skip("Linux GCC target smoke tests only run on Linux.");
		if (!GccCanLink("-m32"))
			Assert.Skip("gcc -m32 cannot link a native executable on this host.");

		string temp = CreateTempCase("gcc_linux_x86.camp", """
			#build --nostdlib
			#build --artifact exec
			#build --name gcc-linux-x86-smoke

			export int main()
			{
				return 0;
			}
			""");
		string outDir = TempPath("gcc-linux-x86-out");

		ProcessResult result = RunCampc("build", temp, "--target", "gcc-linux-x86", "--out-dir", outDir);

		AssertCommandSucceeded(result);
		Assert.Contains("generated: gcc-linux-x86-smoke", result.StdOut, StringComparison.Ordinal);
		ProcessResult run = RunExecutable(Path.Combine(outDir, ArtifactDirectoryForTarget("gcc-linux-x86", NativeBuildKind.Exec), "gcc-linux-x86-smoke"));
		Assert.Equal(0, run.ExitCode);
	}

	[Fact]
	public void Package_source_can_be_added_and_searched_locally()
	{
		string tempRoot = TempPath("pkg-search");
		Directory.CreateDirectory(Path.Combine(tempRoot, "source", "demo", "1.2.3", "src"));
		File.WriteAllText(Path.Combine(tempRoot, "source", "demo", "1.2.3", "src", "demo.camp"), "export int value = 1;\n");
		string localFile = Path.Combine(tempRoot, "local.camp");
		File.WriteAllText(localFile, "// local package config\n");

		ProcessResult add = RunCampc("pkg", "add-source", "local", Path.Combine(tempRoot, "source"), "--local", localFile);
		ProcessResult search = RunCampc("pkg", "search", "demo", "--local", localFile);

		Assert.Equal(0, add.ExitCode);
		Assert.Equal(0, search.ExitCode);
		Assert.Contains("local: demo@1.2.3", search.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Restore_installs_packages_into_cache_pkg()
	{
		string packageName = "cache-demo-stage4";
		string repositoryRoot = FindRepositoryRoot();
		string cachePackageRoot = Path.Combine(repositoryRoot, "cache", "pkg", packageName);
		if (Directory.Exists(cachePackageRoot))
			Directory.Delete(cachePackageRoot, recursive: true);
		string oldPackageRoot = Path.Combine(repositoryRoot, "pkg", packageName);
		if (Directory.Exists(oldPackageRoot))
			Directory.Delete(oldPackageRoot, recursive: true);

		string tempRoot = TempPath("pkg-restore-cache");
		string sourceFile = Path.Combine(tempRoot, "source", packageName, "1.2.3", "src", "demo.camp");
		Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
		File.WriteAllText(sourceFile, "export int restoredValue() => 7;\n");
		string sourceRootArgument = Path.Combine(tempRoot, "source").Replace('\\', '/');
		string app = CreateTempCase("pkg_restore_cache.camp", $$"""
			#build --use-source local "{{sourceRootArgument}}"
			#build --use {{packageName}}@1.2.3
			""");

		ProcessResult result = RunCampc("restore", app);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains($"installed: {packageName}@1.2.3", result.StdOut, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(cachePackageRoot, "1.2.3", "src", "demo.camp")));
		Assert.False(Directory.Exists(oldPackageRoot));
	}

	static ProcessResult RunCampc(params string[] arguments)
	{
		return RunCampc(null, arguments);
	}

	static ProcessResult RunCampc(IReadOnlyDictionary<string, string?>? environmentVariables, params string[] arguments)
	{
		using IDisposable timing = TestTiming.Measure("CommandLine campc " + string.Join(" ", arguments.Take(6)) + (arguments.Length > 6 ? " ..." : ""));
		string repositoryRoot = FindRepositoryRoot();
		string executable = Path.Combine(repositoryRoot, "bin", OperatingSystem.IsWindows() ? "campc.exe" : "campc");
		ProcessStartInfo info = new()
		{
			FileName = executable,
			WorkingDirectory = repositoryRoot,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		foreach (string argument in arguments)
			info.ArgumentList.Add(argument);
		if (environmentVariables is not null)
		{
			foreach ((string key, string? value) in environmentVariables)
			{
				if (value is null)
					info.Environment.Remove(key);
				else
					info.Environment[key] = value;
			}
		}

		using Process process = new() { StartInfo = info };
		process.Start();
		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, Normalize(stdout), Normalize(stderr));
	}

	static ProcessResult RunExecutable(string executable)
	{
		using IDisposable timing = TestTiming.Measure("CommandLine executable " + Path.GetFileName(executable));
		ProcessStartInfo info = new()
		{
			FileName = executable,
			WorkingDirectory = FindRepositoryRoot(),
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		using Process process = new() { StartInfo = info };
		process.Start();
		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, Normalize(stdout), Normalize(stderr));
	}

	static ProcessResult RunProcess(string executable, IReadOnlyList<string> arguments, string workingDirectory)
	{
		using IDisposable timing = TestTiming.Measure("CommandLine process " + Path.GetFileName(executable) + " " + string.Join(" ", arguments.Take(4)) + (arguments.Count > 4 ? " ..." : ""));
		ProcessStartInfo info = new(executable)
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		foreach (string argument in arguments)
			info.ArgumentList.Add(argument);

		using Process process = new() { StartInfo = info };
		process.Start();
		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, Normalize(stdout), Normalize(stderr));
	}

	static void AssertCommandSucceeded(ProcessResult result)
	{
		Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}.\nSTDOUT:\n{result.StdOut}\nSTDERR:\n{result.StdErr}");
	}

	static string NativeTargetForHost()
	{
		if (OperatingSystem.IsLinux())
			return "gcc-linux-x64";
		if (!OperatingSystem.IsWindows())
			return "clang-macos-x64";
		if (!MsvcAvailable())
			Assert.Skip("MSVC tools and target architecture are not available.");
		return CompilerDefaults.TargetName;
	}

	static string ArtifactDirectoryForHost(NativeBuildKind? buildKind)
	{
		return ArtifactDirectoryForTarget(NativeTargetForHost(), buildKind);
	}

	static string ArtifactDirectoryForTarget(string targetName, NativeBuildKind? buildKind)
	{
		Assert.True(TargetCatalog.TryLoad(Path.Combine(FindRepositoryRoot(), "targets"), out TargetCatalog? catalog, out string? error), error);
		Assert.True(catalog!.TryGetTarget(targetName, out TargetDefinition? target));
		return BuildArtifactLayout.GetArtifactDirectoryName(target!, buildKind, "DEBUG");
	}

	static string ArtifactDirectoryForTarget(string targetName, DependencyLinkKind linkKind)
	{
		Assert.True(TargetCatalog.TryLoad(Path.Combine(FindRepositoryRoot(), "targets"), out TargetCatalog? catalog, out string? error), error);
		Assert.True(catalog!.TryGetTarget(targetName, out TargetDefinition? target));
		return BuildArtifactLayout.GetArtifactDirectoryName(target!, linkKind, "DEBUG");
	}

	static string NativeArtifactPathForTarget(string targetName, NativeBuildKind buildKind, string outputDirectory, string projectName)
	{
		Assert.True(TargetCatalog.TryLoad(Path.Combine(FindRepositoryRoot(), "targets"), out TargetCatalog? catalog, out string? error), error);
		Assert.True(catalog!.TryGetTarget(targetName, out TargetDefinition? target));
		return NativeBuildDriver.GetArtifactPath(new NativeBuildOptions
		{
			Target = target!,
			ProfileName = "DEBUG",
			BuildDirectory = Path.Combine(outputDirectory, "build"),
			OutputDirectory = outputDirectory,
			ProjectName = projectName,
			Kind = buildKind,
			SourceFiles = []
		});
	}

	static string ExecutableExtensionForHost()
	{
		return OperatingSystem.IsWindows() ? ".exe" : "";
	}

	static bool MsvcAvailable()
	{
		return OperatingSystem.IsWindows() && MsvcEnvironment.TargetArchitecture is "x64" or "x86" && ToolAvailable("cl") && ToolAvailable("lib");
	}

	static bool GccCanLink(string architectureFlag)
	{
		if (!ToolAvailable("gcc"))
			return false;

		string root = TempPath("gcc-link-smoke-" + architectureFlag.TrimStart('-'));
		Directory.CreateDirectory(root);
		string source = Path.Combine(root, "main.c");
		string output = Path.Combine(root, "main");
		File.WriteAllText(source, "int main(void) { return 0; }\n");
		ProcessResult result = RunProcess("gcc", [architectureFlag, source, "-o", output], root);
		return result.ExitCode == 0 && File.Exists(output);
	}

	static bool ClangWasiAvailable()
	{
		return ClangWasiAvailability.Value;
	}

	static bool ProbeClangWasiAvailable()
	{
		string clang = "/opt/wasi-sdk/bin/clang";
		if (!File.Exists(clang))
			return false;

		string root = TempPath("clang-wasi-smoke");
		Directory.CreateDirectory(root);
		string source = Path.Combine(root, "main.c");
		string output = Path.Combine(root, "main.wasm");
		File.WriteAllText(source, "int main(void) { return 0; }\n");
		ProcessResult result = RunProcess(clang, ["--target=wasm32-wasi", source, "-o", output], root);
		return result.ExitCode == 0 && File.Exists(output);
	}

	static bool EmscriptenAvailable()
	{
		return EmscriptenAvailability.Value;
	}

	static bool ProbeEmscriptenAvailable()
	{
		string emcc = "/opt/emsdk/upstream/emscripten/emcc";
		if (!File.Exists(emcc))
			return false;

		string root = TempPath("emcc-smoke");
		Directory.CreateDirectory(root);
		string source = Path.Combine(root, "main.c");
		string output = Path.Combine(root, "main.js");
		File.WriteAllText(source, "int main(void) { return 0; }\n");
		ProcessResult result = RunProcess(emcc, [source, "-o", output], root);
		return result.ExitCode == 0 && File.Exists(output) && File.Exists(Path.ChangeExtension(output, ".wasm"));
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
		return false;
	}

	static IEnumerable<string> GetPathValues()
	{
		foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
		{
			if (entry.Key is string key && key.Equals("PATH", StringComparison.OrdinalIgnoreCase) && entry.Value is string value)
				yield return value;
		}
	}

	static string CreateTempCase(string name, string text)
	{
		string path = TempPath(name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal));
		return path;
	}

	static string TempPath(string name) => Path.Combine(FindRepositoryRoot(), "tmp", "cli-tests", name);

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

	static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

	static string EscapeCString(string text)
	{
		return text
			.Replace("\\", "\\\\", StringComparison.Ordinal)
			.Replace("\"", "\\\"", StringComparison.Ordinal);
	}

	static int CountOccurrences(string text, string value)
	{
		int count = 0;
		int index = 0;
		while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += value.Length;
		}
		return count;
	}

	static bool GoldenFilterActive()
	{
		return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAMP_TEST_KIND"))
			|| !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAMP_TEST_CASE"));
	}

	readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}
