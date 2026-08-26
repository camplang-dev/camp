using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CompilerDriverOptionTests
{
	[Fact]
	public void Variants_control_target_owned_defines()
	{
		string ansiSource = CreateTempCase("variant_driver_ansi.camp", """
			@symbol(configured(UNICODE) ? "widthW" : "widthA")
			export extern int width();
			""");
		string unicodeSource = CreateTempCase("variant_driver_unicode.camp", """
			@symbol(configured(UNICODE) ? "widthW" : "widthA")
			export extern int width();
			""");

		CompilerResult ansi = Execute(ansiSource, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.Variants.Add("ansi");
			request.NoStdLib = true;
		});
		CompilerResult unicode = Execute(unicodeSource, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.Variants.Add("unicode");
			request.NoStdLib = true;
		});

		Assert.Equal(0, ansi.ExitCode);
		Assert.Equal(0, unicode.ExitCode);

		string root = Path.Combine(FindRepositoryRoot(), "tmp", "driver-option-tests");
		string ansiHeader = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "variant_driver_ansi"), "variant_driver_ansi.h", SearchOption.AllDirectories).Single());
		string unicodeHeader = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "variant_driver_unicode"), "variant_driver_unicode.h", SearchOption.AllDirectories).Single());
		Assert.Contains("widthA", ansiHeader, StringComparison.Ordinal);
		Assert.DoesNotContain("widthW", ansiHeader, StringComparison.Ordinal);
		Assert.Contains("widthW", unicodeHeader, StringComparison.Ordinal);
		Assert.DoesNotContain("widthA", unicodeHeader, StringComparison.Ordinal);
	}

	[Fact]
	public void Variants_reject_unknown_and_same_group_values()
	{
		string source = CreateTempCase("variant_driver_diagnostics.camp", "export int main() => 0;\n");

		CompilerResult unknown = Execute(source, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.Variants.Add("foobar");
			request.NoStdLib = true;
		});
		CompilerResult conflict = Execute(source, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.Variants.Add("unicode");
			request.Variants.Add("ansi");
			request.NoStdLib = true;
		});

		Assert.NotEqual(0, unknown.ExitCode);
		Assert.Contains("Variant 'foobar' is not defined by target 'msvc-windows-x64'", unknown.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, conflict.ExitCode);
		Assert.Contains("both belong to group 'charwidth'", conflict.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Target_owned_define_is_rejected_from_request_and_source_define_errors()
	{
		string normal = CreateTempCase("variant_driver_define.camp", "export int main() => 0;\n");
		string sourceDefine = CreateTempCase("variant_driver_source_define.camp", """
			#define UNICODE

			export int main() => 0;
			""");

		CompilerResult cliLike = Execute(normal, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.Defines.Add("UNICODE");
			request.NoStdLib = true;
		});
		CompilerResult source = Execute(sourceDefine, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.NoStdLib = true;
		});

		Assert.NotEqual(0, cliLike.ExitCode);
		Assert.Contains("Define 'UNICODE' is owned by target", cliLike.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, source.ExitCode);
		Assert.Contains("#define is no longer supported", source.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Configuration_flags_validate_declarations_and_configurations()
	{
		string source = CreateTempCase("configuration_flags.camp", "export int main() => 0;\n");

		CompilerResult ok = Execute(source, request =>
		{
			request.NoStdLib = true;
			request.ConfigurationFlagDeclarations.Add("APP_TRACE");
			request.ConfigurationFlagConfigurations.Add("APP_TRACE");
		});
		CompilerResult unknown = Execute(source, request =>
		{
			request.NoStdLib = true;
			request.ConfigurationFlagConfigurations.Add("APP_TRACE");
		});
		CompilerResult duplicateDeclare = Execute(source, request =>
		{
			request.NoStdLib = true;
			request.ConfigurationFlagDeclarations.Add("APP_TRACE");
			request.ConfigurationFlagDeclarations.Add("APP_TRACE=true");
		});
		CompilerResult duplicateConfigure = Execute(source, request =>
		{
			request.NoStdLib = true;
			request.ConfigurationFlagDeclarations.Add("APP_TRACE");
			request.ConfigurationFlagConfigurations.Add("APP_TRACE");
			request.ConfigurationFlagConfigurations.Add("APP_TRACE=false");
		});

		Assert.Equal(0, ok.ExitCode);
		Assert.NotEqual(0, unknown.ExitCode);
		Assert.Contains("must be declared before it can be configured", unknown.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, duplicateDeclare.ExitCode);
		Assert.Contains("already declared", duplicateDeclare.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, duplicateConfigure.ExitCode);
		Assert.Contains("already configured", duplicateConfigure.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Target_owned_configuration_flags_are_not_configurable_from_request()
	{
		string source = CreateTempCase("configuration_flags_target_owned.camp", "export int main() => 0;\n");

		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.NoStdLib = true;
			request.ConfigurationFlagConfigurations.Add("UNICODE=false");
		});

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("owned by the selected target", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Compiler_owned_true_false_flags_bind_and_cannot_be_user_owned()
	{
		string source = CreateTempCase("configuration_flags_true_false.camp", """
			requires (TRUE)
			void trueMethod()
			{
			}

			requires (FALSE)
			void falseMethod()
			{
			}

			export int main()
			{
				if (configured(TRUE) && !configured(FALSE))
					return 0;
				return 1;
			}
			""");

		CompilerResult ok = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});
		CompilerResult declareTrue = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.ConfigurationFlagDeclarations.Add("TRUE");
		});
		CompilerResult configureFalse = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.ConfigurationFlagConfigurations.Add("FALSE=true");
		});

		Assert.Equal(0, ok.ExitCode);
		Assert.Contains("trueMethod", ok.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("falseMethod", ok.StdOut, StringComparison.Ordinal);
		Assert.NotEqual(0, declareTrue.ExitCode);
		Assert.Contains("owned by the compiler and cannot be declared", declareTrue.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, configureFalse.ExitCode);
		Assert.Contains("owned by the compiler and cannot be configured", configureFalse.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Configuration_requirements_and_configured_intrinsic_bind()
	{
		string source = CreateTempCase("configuration_requirements.camp", """
			requires (OS_LINUX || OS_WIN32);

			requires (OS_LINUX || OS_WIN32)
			void platformMethod()
			{
			}

			export int main()
			{
				if (configured(OS_LINUX || OS_WIN32))
					return 1;
				if (configured(!OS_LINUX && (OS_WIN32 ^ OS_WIN64)))
					return 2;
				return 0;
			}
			""");

		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
		});

		Assert.Equal(0, result.ExitCode);
	}

	[Fact]
	public void Requires_is_contextual_and_not_statement_syntax()
	{
		string contextual = CreateTempCase("requires_contextual_keyword.camp", """
			class requires
			{
				int value;
			}

			int useRequires(int requires)
			{
				int requiresLocal = requires;
				return requiresLocal;
			}

			export int main()
			{
				requires value = { .value = useRequires(1) };
				return value.value;
			}
			""");
		string invalidStatement = CreateTempCase("requires_invalid_statement.camp", """
			export int main()
			{
				requires (TRUE)
				{
					return 0;
				}
				return 1;
			}
			""");

		CompilerResult contextualResult = Execute(contextual, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
		});
		CompilerResult invalidResult = Execute(invalidStatement, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
		});

		Assert.Equal(0, contextualResult.ExitCode);
		Assert.NotEqual(0, invalidResult.ExitCode);
		Assert.Contains("requires_invalid_statement.camp(5,10): error: Expected '}'.", invalidResult.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Configuration_requirement_diagnostics_reject_invalid_placement()
	{
		string source = CreateTempCase("configuration_requirement_placement_diagnostics.camp", """
			requires (OS_LINUX)
			class Problem
			{
				requires (OS_LINUX)
				Problem()
				{
				}
			}
			""");

		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("requires is not valid on constructors or destructors", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Legacy_require_attribute_reports_migration_diagnostics()
	{
		string declarationSource = CreateTempCase("legacy_require_attribute_declaration.camp", """
			@require(OS_LINUX)
			void oldDeclaration()
			{
			}
			""");
		string fileSource = CreateTempCase("legacy_require_attribute_file.camp", """
			@require(OS_LINUX);

			void oldFileDefault()
			{
			}
			""");

		CompilerResult declarationResult = Execute(declarationSource, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});
		CompilerResult fileResult = Execute(fileSource, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});

		Assert.NotEqual(0, declarationResult.ExitCode);
		Assert.Contains("@require is no longer supported; use 'requires (CONDITION)' before a declaration.", declarationResult.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, fileResult.ExitCode);
		Assert.Contains("@require is no longer supported; use 'requires (CONDITION);' for file-wide requirements.", fileResult.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Requirement_satisfiability_warnings_are_bounded_and_skip_exact_constants()
	{
		string constants = CreateTempCase("requirement_satisfiability_constants.camp", """
			requires (TRUE)
			void always()
			{
			}

			requires (FALSE)
			void never()
			{
			}
			""");
		string contradictions = CreateTempCase("requirement_satisfiability_contradictions.camp", """
			requires (APP_A && !APP_A)
			void direct()
			{
			}

			requires (APP_A)
			{
				requires (!APP_A)
				void nested()
				{
				}
			}

			requires (APP_A || APP_B)
			void satisfiable()
			{
			}
			""");

		CompilerResult constantsResult = Execute(constants, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
		});
		CompilerResult contradictionResult = Execute(contradictions, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.ConfigurationFlagDeclarations.Add("APP_A=false");
			request.ConfigurationFlagDeclarations.Add("APP_B=false");
		});

		Assert.Equal(0, constantsResult.ExitCode);
		Assert.DoesNotContain("cannot be satisfied", constantsResult.StdErr, StringComparison.Ordinal);
		Assert.Equal(0, contradictionResult.ExitCode);
		Assert.Contains("Requirement 'APP_A && !APP_A' cannot be satisfied.", contradictionResult.StdErr, StringComparison.Ordinal);
		Assert.Contains("Requirement 'APP_A && !APP_A' cannot be satisfied.", contradictionResult.StdErr, StringComparison.Ordinal);
		Assert.DoesNotContain("APP_A || APP_B' cannot be satisfied", contradictionResult.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Configuration_requirement_diagnostics_reject_unknown_and_bare_flags()
	{
		string source = CreateTempCase("configuration_requirement_expression_diagnostics.camp", """
			export int main()
			{
				if (configured(UNKNOWN_FLAG))
					return 1;
				if (OS_LINUX)
					return 2;
				return 0;
			}
			""");

		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
		});

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Unknown configuration flag 'UNKNOWN_FLAG'", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("Configuration flag 'OS_LINUX' can only be queried with configured(...)", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Effective_requirements_filter_declarations_and_file_defaults()
	{
		string source = CreateTempCase("effective_requirements.camp", """
			requires (OS_LINUX);

			void fileDefaultIncluded()
			{
			}

			requires (SUPPORTS_FILES)
			void strengthenedIncluded()
			{
			}

			requires (OS_WIN32)
			void strengthenedOmitted()
			{
			}
			""");

		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("fileDefaultIncluded", result.StdOut, StringComparison.Ordinal);
		Assert.Contains("strengthenedIncluded", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("strengthenedOmitted", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Testonly_uses_requirement_participation_for_production_filtering()
	{
		string source = CreateTempCase("effective_requirements_testonly.camp", """
			@testonly
			void helper()
			{
			}

			void normal()
			{
			}
			""");

		CompilerResult production = Execute(source, request =>
		{
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});
		CompilerResult testModule = Execute(source, request =>
		{
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
			request.DeclarationParticipationMode = DeclarationParticipationMode.TestModule;
		});

		Assert.Equal(0, production.ExitCode);
		Assert.Contains("normal", production.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("helper", production.StdOut, StringComparison.Ordinal);
		Assert.Equal(0, testModule.ExitCode);
		Assert.Contains("normal", testModule.StdOut, StringComparison.Ordinal);
		Assert.Contains("helper", testModule.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public void Requirements_validate_access_and_configured_flow()
	{
		string guarded = CreateTempCase("requirement_guarded_access.camp", """
			requires (OS_WIN32)
			void windowsOnly()
			{
			}

			export int main()
			{
				if (configured(OS_WIN32))
					windowsOnly();
				while (configured(OS_WIN32) && true)
				{
					windowsOnly();
					break;
				}
				return 0;
			}
			""");
		string unguarded = CreateTempCase("requirement_unguarded_access.camp", """
			requires (OS_WIN32)
			void windowsOnly()
			{
			}

			export int main()
			{
				windowsOnly();
				return 0;
			}
			""");
		string signature = CreateTempCase("requirement_signature_access.camp", """
			requires (OS_WIN32)
			struct WinValue
			{
			}

			void bad(WinValue value)
			{
			}

			requires (OS_WIN32)
			void good(WinValue value)
			{
			}
			""");
		string exhaustiveReturn = CreateTempCase("requirement_exhaustive_return.camp", """
			requires (OS_LINUX)
			int linuxValue()
			{
				return 1;
			}

			requires (OS_WIN32)
			int windowsValue()
			{
				return 2;
			}

			requires (OS_LINUX || OS_WIN32)
			int selectedValue()
			{
				if (configured(OS_LINUX))
					return linuxValue();
				else if (configured(OS_WIN32))
					return windowsValue();
			}
			""");

		CompilerResult guardedResult = Execute(guarded, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
		});
		CompilerResult unguardedResult = Execute(unguarded, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
		});
		CompilerResult signatureResult = Execute(signature, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});
		CompilerResult exhaustiveResult = Execute(exhaustiveReturn, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
		});

		Assert.Equal(0, guardedResult.ExitCode);
		Assert.Equal(0, exhaustiveResult.ExitCode);
		Assert.NotEqual(0, unguardedResult.ExitCode);
		Assert.Contains("requires configuration 'OS_WIN32'", unguardedResult.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, signatureResult.ExitCode);
		Assert.Contains("Type 'WinValue' requires configuration 'OS_WIN32'", signatureResult.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Requirements_filter_selected_type_shape()
	{
		string source = CreateTempCase("requirement_type_shape.camp", """
			requires (OS_WIN32)
			interface IWindows
			{
				void ping();
			}

			struct Shape
			{
				int always;

				requires (OS_WIN32)
				int windowsOnly;
			}

			class Control: IWindows
			{
				void ping(): IWindows
				{
				}
			}

			virtual class VBase
			{
				virtual int alwaysValue()
				{
					return 1;
				}

				requires (OS_WIN32)
				virtual int windowsValue()
				{
					return 2;
				}
			}

			export int main()
			{
				Shape shape = { .always = 1 };
				return shape.always;
			}
			""");

		string repositoryRoot = FindRepositoryRoot();
		string outDir = Path.Combine(repositoryRoot, "tmp", "driver-option-tests", "requirement_type_shape_output");
		if (Directory.Exists(outDir))
			Directory.Delete(outDir, recursive: true);
		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.OutDir = outDir;
		});

		Assert.Equal(0, result.ExitCode);
		string privateHeader = File.ReadAllText(Path.Combine(outDir, "gcc-linux-x64_DEBUG", "build", "requirement_type_shape_private.h"));
		Assert.Contains("int32_t always;", privateHeader, StringComparison.Ordinal);
		Assert.DoesNotContain("windowsOnly", privateHeader, StringComparison.Ordinal);
		Assert.DoesNotContain("IWindows", privateHeader, StringComparison.Ordinal);
		Assert.Contains("alwaysValue", privateHeader, StringComparison.Ordinal);
		Assert.DoesNotContain("windowsValue", privateHeader, StringComparison.Ordinal);
	}

	[Fact]
	public void Requirements_validate_abstract_and_override_availability()
	{
		string conditionalAbstract = CreateTempCase("requirement_conditional_abstract.camp", """
			abstract class Base
			{
				requires (OS_WIN32)
				abstract int draw();
			}

			virtual class Derived: Base
			{
			}
			""");
		string narrowOverride = CreateTempCase("requirement_narrow_override.camp", """
			virtual class Base
			{
				virtual int draw()
				{
					return 1;
				}
			}

			virtual class Derived: Base
			{
				requires (OS_WIN32)
				override int draw()
				{
					return 2;
				}
			}
			""");

		CompilerResult linuxResult = Execute(conditionalAbstract, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});
		CompilerResult windowsResult = Execute(conditionalAbstract, request =>
		{
			request.TargetName = "msvc-windows-x64";
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});
		CompilerResult overrideResult = Execute(narrowOverride, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});

		Assert.Equal(0, linuxResult.ExitCode);
		Assert.NotEqual(0, windowsResult.ExitCode);
		Assert.Contains("must use override to implement inherited abstract member 'draw()'", windowsResult.StdErr, StringComparison.Ordinal);
		Assert.NotEqual(0, overrideResult.ExitCode);
		Assert.Contains("must be at least as available as inherited member", overrideResult.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Requirements_select_conditional_aliases_and_symbols()
	{
		string source = CreateTempCase("requirement_conditional_alias_symbol.camp", """
			alias NUMBER =
				configured(APP_WIDE): long,
				configured(APP_WIDE): short,
				int;

			@symbol(configured(APP_RENAMED) ? "renamed_value" : "default_value")
			export NUMBER getValue()
			{
				return 1;
			}
			""");

		string repositoryRoot = FindRepositoryRoot();
		string narrowOut = Path.Combine(repositoryRoot, "tmp", "driver-option-tests", "requirement_conditional_alias_symbol_narrow");
		string wideOut = Path.Combine(repositoryRoot, "tmp", "driver-option-tests", "requirement_conditional_alias_symbol_wide");
		if (Directory.Exists(narrowOut))
			Directory.Delete(narrowOut, recursive: true);
		if (Directory.Exists(wideOut))
			Directory.Delete(wideOut, recursive: true);

		CompilerResult narrow = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.ConfigurationFlagDeclarations.Add("APP_WIDE=false");
			request.ConfigurationFlagDeclarations.Add("APP_RENAMED=false");
			request.OutDir = narrowOut;
		});
		CompilerResult wide = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.ConfigurationFlagDeclarations.Add("APP_WIDE=false");
			request.ConfigurationFlagDeclarations.Add("APP_RENAMED=false");
			request.ConfigurationFlagConfigurations.Add("APP_WIDE");
			request.ConfigurationFlagConfigurations.Add("APP_RENAMED");
			request.OutDir = wideOut;
		});

		Assert.Equal(0, narrow.ExitCode);
		Assert.Equal(0, wide.ExitCode);
		string narrowHeader = File.ReadAllText(Path.Combine(narrowOut, "gcc-linux-x64_DEBUG", "build", "requirement_conditional_alias_symbol.h"));
		string wideHeader = File.ReadAllText(Path.Combine(wideOut, "gcc-linux-x64_DEBUG", "build", "requirement_conditional_alias_symbol.h"));
		Assert.Contains("int32_t default_value(void);", narrowHeader, StringComparison.Ordinal);
		Assert.Contains("int64_t renamed_value(void);", wideHeader, StringComparison.Ordinal);
		Assert.DoesNotContain("int16_t renamed_value", wideHeader, StringComparison.Ordinal);
	}

	[Fact]
	public void Requirement_outputs_use_source_syntax_and_stable_metadata_field()
	{
		string source = CreateTempCase("requirement_output_api_metadata.camp", """
			requires (OS_LINUX)
			export int linuxOnly()
			{
				return 1;
			}
			""");
		string repositoryRoot = FindRepositoryRoot();
		string outDir = Path.Combine(repositoryRoot, "tmp", "driver-option-tests", "requirement_output_api_metadata");
		if (Directory.Exists(outDir))
			Directory.Delete(outDir, recursive: true);

		CompilerResult api = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.InspectApi = true;
		});
		CompilerResult metadata = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.EmitMetadata = MetadataVisibility.Export;
			request.OutDir = outDir;
			request.ProjectName = "requirement_output_api_metadata";
		});

		Assert.Equal(0, api.ExitCode);
		Assert.Contains("requires (OS_LINUX)", api.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("@require", api.StdOut, StringComparison.Ordinal);
		Assert.Equal(0, metadata.ExitCode);
		string metadataPath = Path.Combine(outDir, "gcc-linux-x64_DEBUG", "requirement_output_api_metadata_api.json");
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(metadataPath));
		JsonElement declaration = document.RootElement.GetProperty("declarations").EnumerateArray().Single(static item => item.GetProperty("name").GetString() == "linuxOnly");
		Assert.Equal("OS_LINUX", declaration.GetProperty("require").GetString());
	}

	[Fact]
	public void Requirements_reject_conditional_string_attributes_outside_symbol()
	{
		string source = CreateTempCase("requirement_conditional_attribute_diagnostic.camp", """
			@category(configured(APP_DOCS) ? "A" : "B")
			export int value()
			{
				return 0;
			}
			""");

		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.ConfigurationFlagDeclarations.Add("APP_DOCS=false");
			request.Inspect = CompilerInspectMode.Declarations;
		});

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("Conditional string attribute expressions are only supported by @symbol.", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public void Requirements_enforce_command_line_requirements()
	{
		string source = CreateTempCase("requirement_command_line.camp", "export int main() => 0;\n");

		CompilerResult targetSatisfied = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.ConfigurationRequirements.Add("OS_LINUX && SUPPORTS_FILES");
			request.Inspect = CompilerInspectMode.Declarations;
		});
		CompilerResult targetRejected = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.ConfigurationRequirements.Add("OS_WIN32");
			request.Inspect = CompilerInspectMode.Declarations;
		});
		CompilerResult moduleConfigured = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.ConfigurationFlagDeclarations.Add("APP_FEATURE=false");
			request.ConfigurationFlagConfigurations.Add("APP_FEATURE");
			request.ConfigurationRequirements.Add("APP_FEATURE");
			request.Inspect = CompilerInspectMode.Declarations;
		});

		Assert.Equal(0, targetSatisfied.ExitCode);
		Assert.NotEqual(0, targetRejected.ExitCode);
		Assert.Contains("Configuration requirement 'OS_WIN32' is not satisfied", targetRejected.StdErr, StringComparison.Ordinal);
		Assert.Equal(0, moduleConfigured.ExitCode);
	}

	[Fact]
	public void Requirements_reject_legacy_preprocessor_conditionals_without_hiding_source()
	{
		string source = CreateTempCase("requirement_legacy_preprocessor.camp", """
			#if OS_WIN32
			this is not hidden anymore
			#endif

			export int main() => 0;
			""");

		CompilerResult result = Execute(source, request =>
		{
			request.TargetName = "gcc-linux-x64";
			request.NoStdLib = true;
			request.Inspect = CompilerInspectMode.Declarations;
		});

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("#if is no longer supported", result.StdErr, StringComparison.Ordinal);
		Assert.Contains("requirement_legacy_preprocessor.camp(2,9): error: Expected ';', method body, or assignment.", result.StdErr, StringComparison.Ordinal);
	}

	static CompilerResult Execute(string sourcePath, Action<CompilerRequest> configure)
	{
		string repositoryRoot = FindRepositoryRoot();
		CompilerRequest request = new()
		{
			RuntimeRoot = Path.Combine(repositoryRoot, "bin"),
			TargetRoot = Path.Combine(repositoryRoot, "targets"),
			PackageSourceRoot = Path.Combine(repositoryRoot, "lib"),
			PackageArtifactRoot = Path.Combine(repositoryRoot, "tmp", "driver-option-packages"),
			WorkingDirectory = Path.GetDirectoryName(sourcePath) ?? repositoryRoot,
			OutDir = Path.Combine(repositoryRoot, "tmp", "driver-option-tests", Path.GetFileNameWithoutExtension(sourcePath), ".")
		};
		request.Files.Add(sourcePath);
		configure(request);
		return CompilerDriver.Execute(request);
	}

	static string CreateTempCase(string name, string text)
	{
		string path = Path.Combine(FindRepositoryRoot(), "tmp", "driver-option-tests", name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal));
		return path;
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
