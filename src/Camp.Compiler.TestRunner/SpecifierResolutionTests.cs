using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class SpecifierResolutionTests
{
	[Theory]
	[InlineData("nint", true)]
	[InlineData("nuint", true)]
	[InlineData("string", true)]
	[InlineData("wstring", true)]
	[InlineData("astring", true)]
	[InlineData("void*", true)]
	[InlineData("int[]", true)]
	[InlineData("int?", true)]
	[InlineData("fn*", true)]
	[InlineData("fn int()", true)]
	[InlineData("delegate int()", true)]
	[InlineData("async int()", true)]
	[InlineData("once int()", true)]
	[InlineData("int", false)]
	[InlineData("bool", false)]
	[InlineData("byte", false)]
	[InlineData("untyped", false)]
	[InlineData("int[2]", false)]
	[InlineData("Named", false)]
	[InlineData("PlainClass", false)]
	[InlineData("PlainEnum", false)]
	[InlineData("Scalar", false)]
	[InlineData("Natural", false)]
	[InlineData("Callback", false)]
	[InlineData("Generic<int>", false)]
	public void Eligibility_depends_on_the_written_carrier(string carrier, bool eligible)
	{
		AnalysisResult result = Analyze($"struct Named {{ }} class PlainClass {{ }} enum PlainEnum {{ ONE }} newtype Scalar: nint; alias Natural = nint; newtype fn int Callback(); class Generic<T> {{ }} extern void f({carrier} _targettype value);");
		Assert.Equal(eligible, result.Success);
		if (!eligible) Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot be applied", StringComparison.Ordinal));
	}

	[Fact]
	public void Generic_parameters_and_legacy_callable_prefix_typespecs_are_rejected()
	{
		Assert.Contains(Analyze("extern void f<T>(T _targettype value);").Diagnostics,
			d => d.Message.Contains("cannot be applied", StringComparison.Ordinal));
		Assert.Contains(Analyze("extern void f(fn _far _pascal nint() callback);").Diagnostics,
			d => d.Message.Contains("callspec position", StringComparison.Ordinal));
	}

	[Fact]
	public void Placement_and_cardinality_include_globals_fields_locals_and_identical_duplicates()
	{
		AnalysisResult invalid = Analyze("""
			_targetcall int global;
			struct Holder { _targetcall int field; }
			void f() { _targetcall int local = 0; }
			extern _targetcall _targetcall void duplicate();
			extern void g(byte* _targettype _targettype pointer,
				fn _targetcall _targetcall int() _targettype _targettype callback,
				fn _targettype int() wrongCall, byte* _targetcall wrongType);
			""");
		Assert.Equal(2, invalid.Diagnostics.Count(d => d.Message.Contains("Leading specifiers", StringComparison.Ordinal)));
		Assert.Contains(SemanticCompiler.CompileLowered("void f() { _targetcall int local = 0; }").AnalysisDiagnostics,
			d => d.Message.Contains("Leading specifiers", StringComparison.Ordinal));
		Assert.True(Analyze("_targetcall class _rect { } static _targetcall class Helpers { }").Success);
		Assert.Contains(Analyze("static _targettype class Helpers { }").Diagnostics,
			d => d.Message.Contains("callspec position", StringComparison.Ordinal));
		Assert.Equal(4, invalid.Diagnostics.Count(d => d.Message.Contains("multiple", StringComparison.Ordinal)));
		Assert.Contains(invalid.Diagnostics, d => d.Message.Contains("callspec position", StringComparison.Ordinal));
		Assert.Contains(invalid.Diagnostics, d => d.Message.Contains("typespec position", StringComparison.Ordinal));
		Assert.All(invalid.Diagnostics.Where(d => d.Message.Contains("multiple", StringComparison.Ordinal)), d => Assert.NotNull(d.Range));
		Assert.True(Analyze("extern void f(byte* _targettype * _targettype nested);").Success);
	}

	[Fact]
	public void Aliases_validate_inactive_alternatives_categories_fallbacks_and_cycles()
	{
		AnalysisResult valid = Analyze("alias _first = configured(OS_WIN32): _stdcall, _targetcall; alias _next = _first; alias _pointer = configured(OS_WIN16): _far, _targettype; extern _next void f(byte* _pointer p);");
		Assert.True(valid.Success, string.Join("\n", valid.Diagnostics));
		foreach ((string source, string diagnostic) in new[]
		{
			("alias _mixed = configured(OS_WIN32): _targetcall, _far;", "same kind"),
			("alias _mixed = configured(OS_WIN32): _far, _targetcall;", "same kind"),
			("alias _wrong = nint;", "must target"),
			("struct _rect { } alias _wrong = _rect;", "must target"),
			("alias ordinary = _targettype;", "cannot target a specifier"),
			("alias _wrong = _targetcall, configured(OS_WIN32): _targetcall;", "final alternative"),
			("alias _wrong = _targetcall, _targetcall;", "final alternative"),
			("alias _wrong = configured(OS_WIN32): _targetcall;", "no fallback"),
			("alias _first = _second; alias _second = _first;", "alias cycle")
		})
			Assert.Contains(Analyze(source).Diagnostics, d => d.Message.Contains(diagnostic, StringComparison.Ordinal));
	}

	[Fact]
	public void Catalog_known_names_are_reserved_in_all_declarations_but_not_native_attributes()
	{
		const string source = """
			struct _far { }
			extern void _stdcall();
			int _pascal;
			struct Holder { int _near; }
			void f(int _cdecl) { int _msabi = 0; }
			alias _huge = _targettype;
			""";
		AnalysisResult result = Analyze(source);
		foreach (string name in new[] { "_far", "_stdcall", "_pascal", "_near", "_cdecl", "_huge" })
			Assert.Contains(result.Diagnostics, d => d.Message.Contains($"name '{name}' is reserved", StringComparison.Ordinal));
		Assert.Contains(SemanticCompiler.CompileLowered("void f() { int _msabi = 0; }").AnalysisDiagnostics,
			d => d.Message.Contains("name '_msabi' is reserved", StringComparison.Ordinal));
		Assert.Contains(SemanticCompiler.CompileLowered("void f() { fn int(int) callback = _msabi => _msabi; }").AnalysisDiagnostics,
			d => d.Message.Contains("name '_msabi' is reserved", StringComparison.Ordinal));
		AnalysisResult symbols = Analyze("""
			@symbol("class") extern void operation();
			@symbol("_targetcall") extern void defaultCall();
			export struct Data { @symbol("_far") int far; @symbol("_targettype") int ordinary; }
			void _ordinary(int _value) { int _local = 0; }
			""");
		Assert.True(symbols.Success, string.Join("\n", symbols.Diagnostics));
		Assert.Contains(Analyze("@symbol(\"typedef\") extern void f();").Diagnostics, d => d.Message.Contains("reserved", StringComparison.Ordinal));
		Assert.Contains(Analyze("struct Data { @symbol(\"same\") int first; @symbol(\"same\") int second; }").Diagnostics,
			d => d.Message.Contains("Duplicate native member name", StringComparison.Ordinal));
	}

	[Fact]
	public void Source_serialization_preserves_callable_positions_names_defaults_and_native_attributes()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileDeclarations("""
			export newtype fn _targetcall int Callback(int* _targettype _p = null) _targettype;
			export extern void apply(fn _targetcall int(int) _targettype callback, int* value = null);
			@symbol("_targetcall") export extern int operation();
			export struct Data { @symbol("class") int field; }
			export _targetcall class Surface { }
			static _targetcall class Helpers { export static void f() { } }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		SemanticCompiler.Function(compilation, "apply").Parameters[1].Name = "_value";
		using StringWriter writer = new();
		BindableNodeCodeSerializer.Serialize(compilation.Module, writer, new BindableNodeCodeSerializerOptions { ApiHeader = true });
		string api = writer.ToString();
		Assert.Contains("fn _targetcall int(", api, StringComparison.Ordinal);
		Assert.Contains(") _targettype callback", api, StringComparison.Ordinal);
		Assert.Contains("int* _targettype _value = null", api, StringComparison.Ordinal);
		Assert.Contains("Callback(int* _targettype _p = null) _targettype", api, StringComparison.Ordinal);
		Assert.Contains("@symbol(\"_targetcall\")", api, StringComparison.Ordinal);
		Assert.Contains("int operation()", api, StringComparison.Ordinal);
		Assert.Contains("@symbol(\"class\")", api, StringComparison.Ordinal);
		Assert.Contains("extern _targetcall class Surface", api, StringComparison.Ordinal);
		Assert.Contains("static _targetcall class Helpers", api, StringComparison.Ordinal);
		SemanticCompilation roundTrip = SemanticCompiler.CompileDeclarations(api);
		SemanticCompiler.AssertNoDiagnostics(roundTrip);
		Assert.Equal("_value", SemanticCompiler.Function(roundTrip, "apply").Parameters[1].Name);
		foreach (SemanticCompilation snapshot in new[] { compilation, roundTrip })
		{
			using JsonDocument metadata = JsonDocument.Parse(MetadataJsonSerializer.Serialize(snapshot.Compilation, MetadataVisibility.All));
			JsonElement[] declarations = metadata.RootElement.GetProperty("declarations").EnumerateArray().ToArray();
			JsonElement callback = declarations.Single(d => d.GetProperty("name").GetString() == "Callback");
			Assert.Equal("_targetcall", callback.GetProperty("callspec").GetString());
			Assert.Equal("_targettype", callback.GetProperty("targetspec").GetString());
			JsonElement parameter = callback.GetProperty("parameters")[0];
			Assert.Equal("_p", parameter.GetProperty("name").GetString());
			Assert.Equal("null", parameter.GetProperty("defaultValue").GetString());
			Assert.Equal("_targetcall", declarations.Single(d => d.GetProperty("name").GetString() == "Surface").GetProperty("declarationCallspec").GetString());
			Assert.Equal("_targetcall", declarations.Single(d => d.GetProperty("name").GetString() == "Helpers").GetProperty("declarationCallspec").GetString());
		}
	}

	[Fact]
	public void Unknown_parameter_specifiers_hint_at_name_disambiguation_and_known_unavailable_specs_require_proof()
	{
		AnalysisResult unknown = Analyze("extern void f(int* _value = null);");
		Assert.Contains(unknown.Diagnostics, d => d.Message.Contains("fill its typespec slot", StringComparison.Ordinal));
		AnalysisResult unavailable = Analyze("extern void f(byte* _far p);", "clang-macos-x64");
		Assert.Contains(unavailable.Diagnostics, d => d.Message.Contains("not proven", StringComparison.Ordinal));
		Assert.DoesNotContain(unavailable.Diagnostics, d => d.Message.Contains("not defined", StringComparison.Ordinal));
		AnalysisResult guarded = Analyze("requires (OS_WIN16) extern _pascal void f(byte* _far p);", "clang-macos-x64");
		Assert.True(guarded.Success, string.Join("\n", guarded.Diagnostics));
		AnalysisResult flowGuarded = Analyze("void f() { if (configured(OS_WIN16)) { byte* _far p = null; } }", "clang-macos-x64", lower: true);
		Assert.True(flowGuarded.Success, string.Join("\n", flowGuarded.Diagnostics));
	}

	static AnalysisResult Analyze(string source, string targetName = "msvc-windows-x86", bool lower = false)
	{
		CompilationUnitSyntax syntax = CampParser.Parse(new TokenSequence(CampTokenizer.Tokenize(source)), out IReadOnlyList<ParseDiagnostic> parseDiagnostics);
		Assert.Empty(parseDiagnostics);
		Module module = BindableNodeBuilder.Build(syntax, out IReadOnlyList<BindDiagnostic> bindDiagnostics);
		Assert.Empty(bindDiagnostics);
		Assert.True(TargetCatalog.TryLoad(Path.Combine(RepositoryRoot(), "targets"), out TargetCatalog? catalog, out string? error), error);
		Assert.True(catalog!.TryGetTarget(targetName, out TargetDefinition? target));
		if (!lower) return BindableNodeAnalyzer.Analyze(module, target);
		LoweringResult lowering = BindableNodeLowerer.Lower(BindableNodeExpander.Expand(module, target));
		return new AnalysisResult(lowering.Module, lowering.Diagnostics);
	}

	internal static string RepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "camplang.sln"))) directory = directory.Parent;
		return directory!.FullName;
	}
}
