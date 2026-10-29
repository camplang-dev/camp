using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class InScalarConversionTests
{
	[Theory]
	[InlineData("int")]
	[InlineData("nint")]
	public void Unicode_code_points_fit_signed_integer_targets_in_all_value_contexts(string target)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			void take({{target}} value) { }
			{{target}} test(uchar source) {
				{{target}} value = source;
				value = source;
				take(source);
				return source;
			}
			"""));
	}

	[Theory]
	[InlineData("byte", "left * right")]
	[InlineData("byte", "right * left")]
	[InlineData("byte", "left + right")]
	[InlineData("byte", "right + left")]
	[InlineData("ushort", "left + right")]
	[InlineData("ushort", "right + left")]
	[InlineData("ushort", "left | right")]
	[InlineData("ushort", "right | left")]
	public void Narrow_unsigned_arithmetic_keeps_the_uint_common_type(string type, string expression)
	{
		var compilation = SemanticCompiler.CompileLowered($"void test({type} left, uint right) {{ auto result = {expression}; }}");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		var local = Assert.Single(SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "test")));
		Assert.Equal("uint", local.Target.ResolvedType);
	}

	[Theory]
	[InlineData("short", "left + right")]
	[InlineData("short", "right + left")]
	[InlineData("sbyte", "left * right")]
	[InlineData("int", "right | left")]
	public void Incompatible_signed_unsigned_arithmetic_requires_an_explicit_conversion(string type, string expression)
	{
		var compilation = SemanticCompiler.CompileLowered($"void test({type} left, uint right) {{ auto result = {expression}; }}");
		var diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Contains("no common type", diagnostic.Message);
	}

	[Theory]
	[InlineData("byte left, sbyte right", "left + right")]
	[InlineData("byte left, long right", "left << right")]
	public void Integer_promotions_and_shift_result_types_remain_int(string parameters, string expression)
	{
		var compilation = SemanticCompiler.CompileLowered($"void test({parameters}) {{ auto result = {expression}; }}");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		Assert.Equal("int", Assert.Single(SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "test"))).Target.ResolvedType);
	}

	[Theory]
	[InlineData("int", "uint")]
	[InlineData("uint", "int")]
	[InlineData("sbyte", "ulong")]
	[InlineData("byte", "sbyte")]
	[InlineData("short", "ushort")]
	[InlineData("short", "char")]
	[InlineData("short", "wchar")]
	[InlineData("wchar", "char")]
	[InlineData("wchar", "short")]
	[InlineData("uchar", "short")]
	[InlineData("uchar", "ushort")]
	[InlineData("char", "achar")]
	[InlineData("achar", "char")]
	[InlineData("achar", "wchar")]
	[InlineData("achar", "uchar")]
	[InlineData("wchar", "achar")]
	[InlineData("uchar", "achar")]
	[InlineData("achar", "byte")]
	[InlineData("achar", "sbyte")]
	[InlineData("int", "nuint")]
	[InlineData("uint", "nint")]
	[InlineData("nint", "nuint")]
	[InlineData("nuint", "nint")]
	[InlineData("long", "ulong")]
	[InlineData("ulong", "long")]
	public void Implicit_numeric_conversions_reject_sign_changes_and_character_targets(string source, string target)
	{
		var compilation = SemanticCompiler.CompileLowered($$"""
			void take({{target}} value) { }
			{{target}} test({{source}} source) {
				{{target}} value = source;
				value = source;
				take(source);
				return source;
			}
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Equal(4, compilation.AnalysisDiagnostics.Distinct().Count());
		Assert.All(compilation.AnalysisDiagnostics, d => Assert.Contains("cannot convert", d.Message));
	}

	[Theory]
	[InlineData("sbyte", "short")]
	[InlineData("byte", "nuint")]
	[InlineData("ushort", "nint")]
	[InlineData("uint", "long")]
	[InlineData("nint", "long")]
	[InlineData("nuint", "ulong")]
	[InlineData("wchar", "ushort")]
	[InlineData("char", "wchar")]
	[InlineData("achar", "short")]
	[InlineData("achar", "ushort")]
	[InlineData("achar", "int")]
	[InlineData("achar", "uint")]
	[InlineData("achar", "long")]
	[InlineData("achar", "ulong")]
	[InlineData("achar", "nint")]
	[InlineData("achar", "nuint")]
	[InlineData("achar", "float")]
	[InlineData("achar", "double")]
	[InlineData("long", "float")]
	[InlineData("ulong", "double")]
	public void Value_preserving_integer_widening_and_floating_conversions_remain_valid(string source, string target)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($"{target} test({source} value) => value;"));
	}

	[Theory]
	[InlineData("char", "achar")]
	[InlineData("achar", "char")]
	[InlineData("achar", "wchar")]
	[InlineData("achar", "uchar")]
	[InlineData("wchar", "achar")]
	[InlineData("uchar", "achar")]
	public void Character_encoding_conversions_remain_available_with_explicit_casts(string source, string target)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($"{target} test({source} value) => ({target})value;"));
	}

	[Theory]
	[InlineData("nuint", "value == 0")]
	[InlineData("ulong", "0 < value")]
	[InlineData("uint", "value >= 2")]
	[InlineData("uchar", "value < 128")]
	[InlineData("ulong", "value == 18446744073709551615")]
	[InlineData("ulong", "18446744073709551615 == value")]
	public void Comparisons_target_fitting_unsuffixed_literals(string type, string expression)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($"bool test({type} value) => {expression};"));
	}

	[Theory]
	[InlineData("short", "int", "short source = -1;", "&(int){source}")]
	[InlineData("short", "int", "", "&(int){source}")]
	[InlineData("in short", "int", "", "&(int){(*source)}")]
	[InlineData("int", "long", "", "&(int64_t){source}")]
	[InlineData("int", "float", "", "&(float){source}")]
	[InlineData("float", "double", "", "&(double){source}")]
	[InlineData("in int", "int", "", "consume(source)")]
	public void Scalar_in_arguments_convert_before_storage_is_forwarded(string sourceType, string destinationType, string local, string expected)
	{
		string parameter = local.Length == 0 ? sourceType + " source" : "";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			{{destinationType}} consume(in {{destinationType}} value) => value;
			{{destinationType}} forward({{parameter}}) { {{local}} return consume(source); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-in-conversion-");
		try
		{
			CEmissionResult emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "in_conversion" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			string code = string.Join("\n", emission.GeneratedSourceFiles.Select(File.ReadAllText));
			Assert.Contains(expected, code);
		}
		finally { directory.Delete(recursive: true); }
	}
}
