using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed partial class CompilerDriverOptionTests
{
	[Fact]
	public void Target_defaults_round_trip_aliases_callables_and_native_consumers()
	{
		string producer = CreateTempCase("target_defaults_producer.camp", """
			export alias _someapi = configured(OS_WIN32): _stdcall, _targetcall;
			export alias _someptr = configured(OS_WIN16): _far, _targettype;
			export alias _defaultcall = _targetcall;
			alias _chain = _defaultcall;
			export _someapi int platformOperation(int value) => value + 1;
			public _chain int implementation(int value) => value + 2;
			export implementation as projectedOperation;
			export _targetcall int ordinary(int value) => value;
			export newtype fn _targetcall int Callback(int value) _targettype;
			export _targetcall int ascribed(int value): Callback => value;
			export byte* _someptr pointerIdentity(byte* _someptr value) => value;
			export fn* _targettype codeIdentity(fn* _targettype value) => value;
			export nuint _targettype integerIdentity(nuint _targettype value) => value;
			export string _targettype stringIdentity(string _targettype value) => value;
			export int[] _targettype arrayIdentity(int[] _targettype value) => value;
			export int apply(fn _targetcall int(int) _targettype callback, int value) => callback(value);
			export extern void optionalParameters(int* _targettype _value = null,
				int* _targettype = null, fn _targetcall int(int) _targettype _callback = null);
			@symbol("_targetcall") export int nativeOperation(int value) => value + 3;
			@symbol("class") export int reservedOperation(int value) => value + 4;
			export struct NativeData
			{
				@symbol("_targettype") int nativeField;
				@symbol("class") int keywordField;
			}
			export int readData(NativeData* data) => data.nativeField + data.keywordField;
			""");
		string consumer = CreateTempCase("target_defaults_consumer.camp", """
			export int main()
			{
				fn int(int) plain = ordinary;
				fn _targetcall int(int) _targettype explicitDefault = plain;
				plain = explicitDefault;
				byte* plainPointer = null;
				byte* _targettype explicitPointer = pointerIdentity(plainPointer);
				plainPointer = explicitPointer;
				fn* raw = (fn*)ordinary;
				fn* _targettype explicitCode = codeIdentity(raw);
				raw = explicitCode;
				fn int(int) recovered = (fn int(int))raw;
				nuint count = 3;
				nuint _targettype explicitCount = integerIdentity(count);
				count = explicitCount;
				string text = "abc";
				string _targettype explicitText = stringIdentity(text);
				text = explicitText;
				int[] values = default;
				int[] _targettype explicitArray = arrayIdentity(values);
				values = explicitArray;
				delegate _targetcall int(int) _targettype closure = value => value + 1;
				delegate int(int) plainClosure = closure;
				closure = plainClosure;
				NativeData data = default;
				data.nativeField = 7;
				data.keywordField = 8;
				return platformOperation(1) == 2 && projectedOperation(1) == 3
					&& apply(plain, 4) == 4 && closure(4) == 5 && count == 3
					&& values.length == 0 && plainPointer == null && recovered(6) == 6
					&& nativeOperation(1) == 4 && reservedOperation(1) == 5 && readData(&data) == 15 ? 0 : 1;
			}
			""");
		foreach (string target in new[] { "gcc-linux-x64", "clang-macos-x64", "msvc-windows-x86", NativeTargetForHost() }.Distinct())
		{
			string output = Path.Combine(FindRepositoryRoot(), "tmp", "driver-option-tests", "target-defaults", target);
			CompilerResult api = Execute(producer, request =>
			{
				request.TargetName = target;
				request.NoStdLib = true;
				request.InspectApi = true;
			});
			Assert.True(api.ExitCode == 0, api.StdErr);
			Assert.Contains("_targetcall", api.StdOut, StringComparison.Ordinal);
			Assert.Contains("_targettype", api.StdOut, StringComparison.Ordinal);
			Assert.Contains("projectedOperation", api.StdOut, StringComparison.Ordinal);
			Assert.Contains("int* _targettype _value = null", api.StdOut, StringComparison.Ordinal);
			Assert.Contains("_callback = null", api.StdOut, StringComparison.Ordinal);
			Assert.Contains("@symbol(\"_targetcall\")", api.StdOut, StringComparison.Ordinal);
			Assert.Contains("@symbol(\"class\")", api.StdOut, StringComparison.Ordinal);
			Assert.Contains("int nativeField", api.StdOut, StringComparison.Ordinal);
			string apiFile = CreateTempCase("target_defaults_" + target + "_api.camp", api.StdOut);
			CompilerResult library = Execute(producer, request =>
			{
				request.TargetName = target;
				request.NoStdLib = true;
				request.OutDir = output;
				request.EmitMetadata = MetadataVisibility.Export;
				if (target == NativeTargetForHost())
					request.BuildKind = NativeBuildKind.Static;
			});
			Assert.True(library.ExitCode == 0, library.StdErr);
			string emitted = string.Join("\n", library.GeneratedFiles.Where(path => Path.GetExtension(path) is ".c" or ".h").Select(File.ReadAllText));
			Assert.NotEmpty(emitted);
			Assert.DoesNotMatch(@"\b_target(?:call|type)\s+[A-Za-z_*]", emitted);
			Assert.Contains("_targetcall(", emitted, StringComparison.Ordinal);
			Assert.Contains("class(", emitted, StringComparison.Ordinal);
			Assert.Contains("data->_targettype", emitted, StringComparison.Ordinal);
			Assert.Contains("data->class", emitted, StringComparison.Ordinal);
			if (target == "msvc-windows-x86")
				Assert.Contains("__stdcall", emitted, StringComparison.Ordinal);
			string metadata = File.ReadAllText(Directory.GetFiles(output, "*_api.json", SearchOption.AllDirectories).Single());
			Assert.Contains("_targetcall", metadata, StringComparison.Ordinal);
			Assert.Contains("_targettype", metadata, StringComparison.Ordinal);
			using (JsonDocument document = JsonDocument.Parse(metadata))
			{
				JsonElement ordinary = document.RootElement.GetProperty("declarations").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "ordinary");
				Assert.Equal("_targetcall", ordinary.GetProperty("callspec").GetString());
				JsonElement pointer = document.RootElement.GetProperty("declarations").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "pointerIdentity");
				Assert.Contains("_targettype", pointer.GetProperty("returnType").GetString(), StringComparison.Ordinal);
				JsonElement[] declarations = document.RootElement.GetProperty("declarations").EnumerateArray().ToArray();
				JsonElement callback = declarations.Single(d => d.GetProperty("name").GetString() == "Callback");
				Assert.Equal("_targetcall", callback.GetProperty("callspec").GetString());
				Assert.Equal("_targettype", callback.GetProperty("targetspec").GetString());
				JsonElement native = declarations.Single(d => d.GetProperty("name").GetString() == "nativeOperation");
				Assert.Equal("_targetcall", native.GetProperty("symbol").GetString());
				JsonElement data = declarations.Single(d => d.GetProperty("name").GetString() == "NativeData");
				JsonElement field = data.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "nativeField");
				Assert.Equal("_targettype", field.GetProperty("symbol").GetString());
				JsonElement optional = declarations.Single(d => d.GetProperty("name").GetString() == "optionalParameters");
				Assert.Equal("_value", optional.GetProperty("parameters")[0].GetProperty("name").GetString());
			}

			CompilerResult app = Execute(consumer, request =>
			{
				request.TargetName = target;
				request.NoStdLib = true;
				request.ApiFiles.Add(apiFile);
				request.OutDir = Path.Combine(output, "consumer");
				if (target == NativeTargetForHost())
				{
					request.BuildKind = NativeBuildKind.Exec;
					request.References.Add(Directory.GetFiles(output, OperatingSystem.IsWindows() ? "*.lib" : "*.a", SearchOption.AllDirectories).Single());
				}
			});
			Assert.True(app.ExitCode == 0, app.StdErr);
			if (target == NativeTargetForHost())
			{
				string executable = Directory.GetFiles(Path.Combine(output, "consumer"), "target_defaults_consumer" + (OperatingSystem.IsWindows() ? ".exe" : ""), SearchOption.AllDirectories).Single();
				using Process process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })!;
				if (!process.WaitForExit(30000))
				{
					process.Kill(entireProcessTree: true);
					Assert.Fail("Target-default consumer did not finish within 30 seconds.");
				}
				Assert.Equal(0, process.ExitCode);
			}
		}
	}

	[Fact]
	public void Target_defaults_follow_carriers_variants_and_target_free_analysis()
	{
		const string source = """
			alias _call = _targetcall;
			alias _type = _targettype;
			export extern _call int operation(nuint _type count);
			export extern void carriers(byte* _targettype data, fn* _targettype code,
				fn _targetcall int(int) _targettype callback, int[] _type values,
				string _type text, nint _type number, byte* _type * _type nested,
				byte* _type const qualified);
			""";
		{
			CompilationUnitSyntax syntax = CampParser.Parse(new TokenSequence(CampTokenizer.Tokenize(source)), out var parseDiagnostics);
			Assert.Empty(parseDiagnostics);
			Module module = BindableNodeBuilder.Build(syntax, out var bindDiagnostics);
			Assert.Empty(bindDiagnostics);
			AnalysisResult analysis = BindableNodeAnalyzer.Analyze(module);
			Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics));
			Assert.Equal("_targetcall", module.Definitions.OfType<FunctionDefinition>().Single(f => f.Name == "operation").CallSpec);
			Assert.All(module.Definitions.OfType<FunctionDefinition>().SelectMany(f => f.Parameters), parameter =>
				Assert.DoesNotContain("_target", parameter.ResolvedType ?? "", StringComparison.Ordinal));
		}

		string root = Path.Combine(FindRepositoryRoot(), "tmp", "driver-option-tests", "target-default-catalog");
		Directory.CreateDirectory(root);
		File.WriteAllText(Path.Combine(root, "distinct.ini"), """
			[target]
			name=distinct
			[typespec]
			_code=CODE
			_data=DATA
			[variant]
			memory=normal* swapped
			[typespec:normal]
			default=_code/_data
			[typespec:swapped]
			default=_data/_code
			[pointer]
			default=64
			_code=16
			_data=32
			[nint]
			default=64
			_code=16
			_data=32
			""");
		Assert.True(TargetCatalog.TryLoad(root, out TargetCatalog? custom, out string? error), error);
		Assert.True(custom!.TryGetTarget("distinct", out TargetDefinition? distinct));
		Assert.True(TargetCatalog.TryLoad(Path.Combine(FindRepositoryRoot(), "targets"), out TargetCatalog? shipped, out error), error);
		Assert.True(shipped!.TryGetTarget("msvc-win16-x86", out TargetDefinition? segmented));
		foreach ((TargetDefinition target, string[] variants) in new[] { (distinct!, new[] { "normal", "swapped" }), (segmented!, new[] { "medium", "compact" }) })
		{
			foreach (string variant in variants)
			{
				TargetDefinition selected = target.WithVariantSelection(target.ResolveVariantSelection([variant]));
				Assert.Equal(selected.GetPointerWidth(null, true), selected.GetPointerWidth("_targettype", true));
				Assert.Equal(selected.GetPointerWidth(null, false), selected.GetPointerWidth("_targettype", false));
				Assert.Equal(selected.GetNaturalIntegerWidth(null), selected.GetNaturalIntegerWidth("_targettype"));
				if (target.Name == "distinct")
				{
					Assert.Equal(variant == "normal" ? 16 : 32, selected.GetPointerWidth("_targettype", true));
					Assert.Equal(variant == "normal" ? 32 : 16, selected.GetPointerWidth("_targettype", false));
					Assert.Equal(64, selected.GetNaturalIntegerWidth("_targettype"));
				}
				Assert.DoesNotContain("_targettype", selected.TypeSpecOrder);
				foreach (TargetConversionCarrier carrier in Enum.GetValues<TargetConversionCarrier>())
				{
					Assert.Equal(selected.ClassifyTypeSpecConversion(carrier, null, null), selected.ClassifyTypeSpecConversion(carrier, "_targettype", null));
					Assert.Equal(selected.ClassifyTypeSpecConversion(carrier, null, null), selected.ClassifyTypeSpecConversion(carrier, null, "_targettype"));
				}
				string[] emissions = new string[2];
				for (int explicitSpec = 0; explicitSpec < 2; explicitSpec++)
				{
					// Identical names make the whole generated header comparable, including nested carriers.
					string text = source[source.IndexOf("export extern", StringComparison.Ordinal)..]
						.Replace(" _call", " _targetcall", StringComparison.Ordinal)
						.Replace(" _type", " _targettype", StringComparison.Ordinal);
					if (explicitSpec == 0)
						text = text.Replace(" _targetcall", "", StringComparison.Ordinal).Replace(" _targettype", "", StringComparison.Ordinal);
					string path = CreateTempCase("target_defaults_carriers.camp", text);
					CompilerResult result = Execute(path, request =>
					{
						request.NoStdLib = true;
						request.TargetName = target.Name;
						request.TargetRoot = target.Name == "distinct" ? root : Path.Combine(FindRepositoryRoot(), "targets");
						request.Variants.Add(variant);
						request.OutDir = Path.Combine(root, "output", target.Name, variant, explicitSpec.ToString());
					});
					Assert.True(result.ExitCode == 0, result.StdErr);
					emissions[explicitSpec] = File.ReadAllText(result.GeneratedFiles.Single(p => Path.GetFileName(p) == "target_defaults_carriers.h"));
				}
				Assert.Equal(emissions[0], emissions[1]);
				Assert.DoesNotContain("_targettype", emissions[1], StringComparison.Ordinal);
			}
		}
	}

	[Fact]
	public void Target_defaults_preserve_explicit_interface_intent_and_diagnostics()
	{
		string positive = CreateTempCase("target_defaults_interfaces.camp", """
			export alias _defaultcall = _targetcall;
			export interface DefaultFace { _targetcall int value(); }
			export interface ForeignFace { _stdcall int value(); }
			struct Explicit: DefaultFace { _defaultcall int value(): DefaultFace => 1; }
			struct Omitted: ForeignFace { int value(): ForeignFace => 2; }
			virtual class Base { virtual _targetcall int value() => 1; }
			virtual class OmittedOverride: Base { override int value() => 2; }
			virtual class ExplicitOverride: Base { override _defaultcall int value() => 3; }
			export int main() => 0;
			""");
		CompilerResult valid = Execute(positive, request => { request.TargetName = "msvc-windows-x86"; request.NoStdLib = true; });
		Assert.True(valid.ExitCode == 0, valid.StdErr);
		string code = string.Join("\n", valid.GeneratedFiles.Where(p => Path.GetExtension(p) is ".c" or ".h").Select(File.ReadAllText));
		Assert.Contains("__stdcall", code, StringComparison.Ordinal);
		Assert.DoesNotContain("_targetcall", code, StringComparison.Ordinal);
		CompilerResult api = Execute(positive, request => { request.TargetName = "msvc-windows-x86"; request.NoStdLib = true; request.InspectApi = true; });
		Assert.True(api.ExitCode == 0, api.StdErr);
		Assert.Contains("_targetcall", api.StdOut, StringComparison.Ordinal);
		string apiFile = CreateTempCase("target_defaults_interfaces_api.camp", api.StdOut);
		string imported = CreateTempCase("target_defaults_interface_consumer.camp", "struct Consumer: ForeignFace { _defaultcall int value(): ForeignFace => 0; }");
		CompilerResult mismatch = Execute(imported, request =>
		{
			request.TargetName = "msvc-windows-x86";
			request.NoStdLib = true;
			request.ApiFiles.Add(apiFile);
		});
		Assert.NotEqual(0, mismatch.ExitCode);
		Assert.Contains("requires callspec '_stdcall'", mismatch.StdErr, StringComparison.Ordinal);

		(string Source, string Diagnostic)[] rejected =
		[
			("interface F { _stdcall int f(); } struct S: F { _targetcall int f(): F => 0; }", "requires callspec '_stdcall'"),
			("alias _d = _targetcall; interface F { _stdcall int f(); } struct S: F { _d int f(): F => 0; }", "requires callspec '_stdcall'"),
			("extern void f(fn* _targetcall value);", "typespec position"),
			("alias _d = _targetcall; extern void f(byte* _d value);", "typespec position"),
			("extern void f(nint _targetcall value);", "typespec position"),
			("extern void f(untyped _targettype value);", "cannot be applied to type 'untyped'"),
			("extern void f(int _targettype value);", "cannot be applied to type 'int'"),
			("_targettype byte* value;", "callspec position"),
			("extern void f(fn _targetcall _stdcall int() value);", "multiple callspecs"),
			("extern void f(fn int() _targettype _far value);", "multiple target typespecs"),
			("extern void f(byte* _far _targettype value);", "multiple target typespecs"),
			("alias _targetcall = _stdcall;", "name '_targetcall' is reserved"),
			("struct _targettype { int value; }", "name '_targettype' is reserved"),
			("extern void f(byte* _unknownspec value);", "is not defined by target")
		];
		for (int i = 0; i < rejected.Length; i++)
		{
			string path = CreateTempCase("target_defaults_invalid_" + i + ".camp", rejected[i].Source);
			CompilerResult result = Execute(path, request => { request.TargetName = "msvc-windows-x86"; request.NoStdLib = true; });
			Assert.NotEqual(0, result.ExitCode);
			Assert.Contains(rejected[i].Diagnostic, result.StdErr, StringComparison.Ordinal);
		}

		string directory = Path.Combine(FindRepositoryRoot(), "tmp", "driver-option-tests", "target-default-reservations");
		Directory.CreateDirectory(directory);
		foreach (string entry in new[] { "[callspec]\n_targetcall=", "[declare.callspec]\n_targettype=TRUE", "[typespec]\n_targettype=", "[declare.typespec]\n_targetcall=TRUE", "[pointer]\n_targettype=32", "[nint]\n_targettype=32", "[typespec]\ndefault=_targettype/_targettype", "[conversion.data_pointer]\n_targettype->_far=implicit", "[variant]\nmemory=small* large\n[typespec:large]\n_targettype=" })
		{
			File.WriteAllText(Path.Combine(directory, "invalid.ini"), "[target]\nname=invalid\n" + entry + "\n");
			Assert.False(TargetCatalog.TryLoad(directory, out _, out string? error));
			Assert.Contains("compiler-defined", error, StringComparison.Ordinal);
		}
	}
}
