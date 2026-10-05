using System.Collections.Generic;
using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class IntegerLiteralRangeTests
{
	[Theory]
	[MemberData(nameof(OutOfRangeLiterals))]
	public void Integer_literals_must_fit_their_primitive_target(string type, string literal)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			void test()
			{
				{{type}} value = {{literal}};
			}
			""");

		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		AnalysisDiagnostic diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Contains("is outside the range of type", diagnostic.Message);
		TokenRange range = Assert.IsType<TokenRange>(diagnostic.Range);
		Assert.Equal(3, range.StartLineNumber);
	}

	public static IEnumerable<object[]> OutOfRangeLiterals()
	{
		yield return ["byte", "256"];
		yield return ["sbyte", "128"];
		yield return ["short", "32768"];
		yield return ["ushort", "65536"];
		yield return ["int", "2147483648"];
		yield return ["uint", "4294967296"];
		yield return ["long", "9223372036854775808"];
		yield return ["ulong", "18446744073709551616"];
		yield return ["nint", "9223372036854775808"];
		yield return ["nuint", "18446744073709551616"];
		yield return ["const byte", "256"];
		yield return ["byte", "0x100"];
		yield return ["byte", "256u"];
		yield return ["byte", "256L"];
		yield return ["ulong", "18446744073709551616UL"];
		yield return ["byte", "(256)"];
		yield return ["byte", "+256"];
		yield return ["sbyte", "-129"];
		yield return ["short", "-32769"];
		yield return ["int", "-2147483649"];
		yield return ["long", "-9223372036854775809"];
		yield return ["uint", "-1"];
		yield return ["ulong", "-(1)"];
		yield return ["int", "(-((2147483649)))"];
	}

	[Theory]
	[InlineData("byte", "255")]
	[InlineData("sbyte", "127")]
	[InlineData("sbyte", "-128")]
	[InlineData("sbyte", "-5")]
	[InlineData("sbyte", "-(128)")]
	[InlineData("sbyte", "(-((128)))")]
	[InlineData("sbyte", "+(-128)")]
	[InlineData("sbyte", "+127")]
	[InlineData("const sbyte", "-128")]
	[InlineData("short", "32767")]
	[InlineData("short", "-32768")]
	[InlineData("short", "-0x8000")]
	[InlineData("short", "-32768L")]
	[InlineData("ushort", "65535")]
	[InlineData("int", "2147483647")]
	[InlineData("uint", "4294967295")]
	[InlineData("long", "9223372036854775807")]
	[InlineData("ulong", "18446744073709551615")]
	[InlineData("nint", "2147483647")]
	[InlineData("nuint", "4294967295")]
	[InlineData("int", "-2147483648")]
	[InlineData("long", "-(9223372036854775808)")]
	[InlineData("ulong", "0xFFFFFFFFFFFFFFFFUL")]
	[InlineData("double", "18446744073709551616")]
	public void Valid_integer_boundaries_and_floating_targets_remain_accepted(string type, string literal)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($"void test() {{ {type} value = {literal}; }}"));
	}

	[Theory]
	[InlineData("sbyte value = 0; value = -128;")]
	[InlineData("return -128;")]
	[InlineData("take(-128);")]
	[InlineData("fixed sbyte[2] values = [-128, -5];")]
	[InlineData("Holder value = { .value = -128 };")]
	[InlineData("sbyte value = true ? -128 : -5;")]
	public void Signed_literals_retain_the_target_type_in_every_target_typed_use(string body)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($$"""
			sbyte globalValue = -128;
			struct Holder { sbyte value; }
			static class Values { static short value = -32768; }
			void take(sbyte value = -128) { }
			sbyte test()
			{
				{{body}}
				return 0;
			}
			"""));
	}

	[Theory]
	[InlineData("sbyte", "-value")]
	[InlineData("sbyte", "+value")]
	[InlineData("short", "-value")]
	[InlineData("short", "~value")]
	public void Unary_operations_on_narrow_integer_values_still_promote(string type, string expression)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void test() {{ {type} value = 1; {type} result = {expression}; }}");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		AnalysisDiagnostic diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Equal($"Declaration initializer cannot convert 'int' to '{type}'.", diagnostic.Message);
	}

	[Theory]
	[InlineData("byte value = 0; value = 256;")]
	[InlineData("return 256;")]
	[InlineData("take(256);")]
	[InlineData("fixed byte[1] values = [256];")]
	[InlineData("Holder value = { .value = 256 };")]
	[InlineData("byte value = true ? 255 : 256;")]
	public void Range_checks_apply_to_every_target_typed_use(string body)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			struct Holder { byte value; }
			void take(byte value) { }
			byte test()
			{
				{{body}}
				return 0;
			}
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
		Assert.All(compilation.AnalysisDiagnostics, diagnostic =>
			Assert.Equal("Integer literal '256' is outside the range of type 'byte'.", diagnostic.Message));
	}

	[Fact]
	public void Global_static_and_parameter_default_literals_are_range_checked()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			byte globalValue = 256;
			static class Values { static uint value = 4294967296; }
			void test(short value = 32768) { }
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Equal(3, compilation.AnalysisDiagnostics.Distinct().Count());
		Assert.All(compilation.AnalysisDiagnostics, diagnostic => Assert.Contains("is outside the range of type", diagnostic.Message));
	}

	[Theory]
	[InlineData("msvc-windows-x86", "2147483647", "-2147483648", "4294967295", "2147483648", "4294967296")]
	[InlineData("gcc-linux-x64", "9223372036854775807", "-9223372036854775808", "18446744073709551615", "9223372036854775808", "18446744073709551616")]
	public void Natural_integer_literal_bounds_follow_the_selected_target(string targetName, string signedMax, string signedMin, string unsignedMax, string signedOverflow, string unsignedOverflow)
	{
		CompilationUnitSyntax syntax = CampParser.Parse(new TokenSequence(CampTokenizer.Tokenize($$"""
			void test()
			{
				nint signedMax = {{signedMax}};
				nint signedMin = {{signedMin}};
				nuint unsignedMax = {{unsignedMax}};
				nint tooLarge = {{signedOverflow}};
				nuint tooWide = {{unsignedOverflow}};
			}
			""")), out IReadOnlyList<ParseDiagnostic> parseDiagnostics);
		Assert.Empty(parseDiagnostics);
		Module module = BindableNodeBuilder.Build(syntax, out IReadOnlyList<BindDiagnostic> bindDiagnostics);
		Assert.Empty(bindDiagnostics);
		Assert.True(TargetCatalog.TryLoadCached(Path.Combine(SpecifierResolutionTests.RepositoryRoot(), "targets"), out TargetCatalog? catalog, out string? error), error);
		Assert.True(catalog!.TryGetTarget(targetName, out TargetDefinition? target));
		LoweringResult result = BindableNodeLowerer.Lower(BindableNodeExpander.Expand(module, target));
		Assert.Equal(2, result.Diagnostics.Count);
		Assert.All(result.Diagnostics, diagnostic => Assert.Contains("is outside the range of type", diagnostic.Message));
	}

	[Fact]
	public void Explicit_numeric_casts_and_newtype_literal_casts_keep_their_conversion_rules()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			newtype Handle: ulong;
			void test()
			{
				byte narrowed = (byte)256;
				Handle handle = (Handle)18446744073709551615;
			}
			"""));
	}
}
