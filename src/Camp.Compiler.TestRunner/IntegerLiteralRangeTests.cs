using System.Collections.Generic;
using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class IntegerLiteralRangeTests
{
	[Fact]
	public void Long_literal_constant_chains_do_not_recompute_each_subtree()
	{
		string sum = string.Join(" + ", Enumerable.Repeat("1", 32));
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($"inline uint SUM = {sum}; void test() {{ uint value = {sum}; }}"));
	}

	[Theory]
	[InlineData("int", "65536 * 65536", "int", "*")]
	[InlineData("auto", "2147483647 + 1", "int", "+")]
	[InlineData("long", "2147483647 + 1", "int", "+")]
	[InlineData("uint", "65536 * 65535", "int", "*")]
	[InlineData("int", "(2147483647 + 1) - 1", "int", "+")]
	[InlineData("int", "-(-2147483647 - 1)", "int", "-")]
	[InlineData("int", "(-2147483647 - 1) / -1", "int", "/")]
	[InlineData("int", "(-2147483647 - 1) % -1", "int", "%")]
	[InlineData("long", "9223372036854775807L + 1", "long", "+")]
	[InlineData("long", "1 << 40", "int", "<<")]
	[InlineData("long", "1 << 32", "int", "<<")]
	[InlineData("int", "1 << -1", "int", "<<")]
	[InlineData("long", "1L << 64", "long", "<<")]
	[InlineData("int", "1 << 31", "int", "<<")]
	[InlineData("int", "-2 << 1", "int", "<<")]
	public void Constant_integer_operations_are_checked_in_their_natural_type(string target, string expression, string natural, string token)
	{
		var compilation = SemanticCompiler.CompileLowered($"void test() {{ {target} value = {expression}; }}");
		var diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Contains($"type '{natural}'", diagnostic.Message);
		Assert.True(diagnostic.Message.Contains("constant operation") || diagnostic.Message.Contains("Constant"));
		Assert.Equal(token, Assert.IsType<TokenRange>(diagnostic.Range).Value);
	}

	[Theory]
	[InlineData("inline int PAGE = 4096; void test() { int value = PAGE * 1048576; }")]
	[InlineData("inline int PAGE = 4096; inline long VALUE = PAGE * 1048576;")]
	[InlineData("inline uint VALUE = 65536 * 65535;")]
	[InlineData("inline int VALUE = (2147483647 + 1) - 1;")]
	[InlineData("inline long VALUE = 1 << 40;")]
	[InlineData("struct Holder { static inline int PAGE = 4096; static inline long VALUE = PAGE * 1048576; }")]
	public void Named_and_inline_constants_do_not_hide_natural_overflow(string source)
	{
		var compilation = SemanticCompiler.CompileLowered(source);
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("type 'int'"));
	}

	[Theory]
	[InlineData("long", "65536L * 65536")]
	[InlineData("long", "1L << 40")]
	[InlineData("uint", "65536u * 65535")]
	[InlineData("uint", "1u << 31")]
	[InlineData("ulong", "1UL << 63")]
	[InlineData("uint", "4294967295u + 1")]
	[InlineData("uint", "~0u")]
	[InlineData("long", "-9223372036854775808L")]
	public void Wider_operands_and_unsigned_constant_arithmetic_remain_valid(string target, string expression)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($"void test() {{ {target} value = {expression}; }}"));
	}

	[Fact]
	public void Unsigned_inline_constant_arithmetic_wraps_in_its_fixed_type()
	{
		var compilation = SemanticCompiler.CompileLowered("inline uint WRAPPED = 4294967295u + 1; inline uint MASK = ~0u;");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		var constants = compilation.Module.Definitions.OfType<VariableDefinition>().ToArray();
		Assert.Equal(System.Numerics.BigInteger.Zero, Assert.IsType<ConstantValue.Integer>(constants[0].ConstantValue).Value);
		Assert.Equal(new System.Numerics.BigInteger(uint.MaxValue), Assert.IsType<ConstantValue.Integer>(constants[1].ConstantValue).Value);
	}

	[Fact]
	public void Literal_shaped_constants_take_targets_in_all_value_contexts()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			inline uint BUDGET = 256 * 1024 * 1024;
			struct Opts { int mode; nuint budget; }
			void arg(uint value) { }
			void consume(Opts value) { }
			uint ret() { return 4 * 1024; }
			void test(bool f) {
				uint a = 256 * 1024 * 1024; nuint b = 64 * 1024;
				ulong c = 1024 * 1024; uint d = (1 + 2);
				uint v = 7; v = 2 * 8; v += 4 * 4;
				arg(8 * 8); consume({1, 256 * 1024 * 1024});
				uint x = f ? 1 : (5 + 45);
				float h = 0.5 * 3; float w = 0.1 + 0.2;
			}
			"""));
	}

	[Theory]
	[InlineData("value < (f ? 1 : 2)", "bool")]
	[InlineData("(f ? 1 : 2) < value", "bool")]
	[InlineData("value == (f ? 1 : 2)", "bool")]
	[InlineData("(f ? 1 : 2) == value", "bool")]
	[InlineData("value < 2 * 3", "bool")]
	[InlineData("2 * 3 < value", "bool")]
	[InlineData("(f ? 2 : 3) * value", "uint")]
	[InlineData("value * (f ? 2 : 3)", "uint")]
	[InlineData("(2 * 3) + value", "uint")]
	[InlineData("value + (2 * 3)", "uint")]
	public void Integer_literal_shapes_take_the_other_operand_type(string expression, string expected)
	{
		var compilation = SemanticCompiler.CompileLowered($"void test(bool f, uint value) {{ auto result = {expression}; }}");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		var local = Assert.Single(SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "test")));
		Assert.Equal(expected, local.Target.ResolvedType);
	}

	[Theory]
	[InlineData("byte", "f ? 1 : 300", "300")]
	[InlineData("uint", "f ? -1 : 2", "-")]
	[InlineData("byte", "f ? 1 : (200 + 100)", "+")]
	public void Literal_shape_range_failures_point_to_the_bad_value(string target, string expression, string token)
	{
		var compilation = SemanticCompiler.CompileLowered($"void test(bool f) {{ {target} value = {expression}; }}");
		var diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Contains("outside the range", diagnostic.Message);
		Assert.Equal(token, Assert.IsType<TokenRange>(diagnostic.Range).Value);
	}

	[Theory]
	[InlineData("uint", "(f ? 1 : 5) + (g ? 10 : 20)")]
	[InlineData("uint", "1 << (uint)n")]
	[InlineData("nuint", "PAGE * 4")]
	[InlineData("uint", "1L + 2")]
	[InlineData("float", "a * 0.1")]
	[InlineData("int", "0.5 * 3")]
	public void Fixed_or_runtime_numeric_expressions_keep_their_natural_types(string target, string expression)
	{
		var compilation = SemanticCompiler.CompileLowered($$"""
			inline int PAGE = 4096;
			void test(bool f, bool g, nuint n, float a) { {{target}} value = {{expression}}; }
			""");
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("cannot convert"));
	}

	[Fact]
	public void Literal_constant_conversion_wraps_the_natural_operation()
	{
		var compilation = SemanticCompiler.CompileLowered("float value() => 100000000.0 + 1.0 - 100000000.0;");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		var cast = Assert.Single(SemanticCompiler.Descendants<CastExpression>(SemanticCompiler.Function(compilation, "value")));
		Assert.Equal("float", cast.ResolvedType);
		Assert.Equal("double", Assert.IsType<BinaryExpression>(cast.Expression).ResolvedType);
		Assert.All(SemanticCompiler.Descendants<LiteralExpression>(cast), literal => Assert.Equal("double", literal.ResolvedType));
	}

	[Theory]
	[InlineData("5uu", "uu")]
	[InlineData("1.5u", "u")]
	[InlineData("5uf", "uf")]
	[InlineData("5ud", "ud")]
	[InlineData("5ll", "ll")]
	[InlineData("5lul", "lul")]
	[InlineData("1.5l", "l")]
	[InlineData("1.5fd", "fd")]
	[InlineData("5ULU", "ULU")]
	[InlineData("0x1uu", "uu")]
	public void Malformed_suffixes_are_diagnosed_at_the_numeric_token(string literal, string suffix)
	{
		CampParser.Parse(new TokenSequence(CampTokenizer.Tokenize($"void test() {{ auto value = {literal}; }}")), out var diagnostics);
		var diagnostic = Assert.Single(diagnostics);
		Assert.Contains($"invalid suffix '{suffix}'", diagnostic.Message);
		Assert.Equal(literal, Assert.IsType<TokenRange>(diagnostic.Range).Value);
	}

	[Theory]
	[InlineData("5u")]
	[InlineData("5L")]
	[InlineData("5uL")]
	[InlineData("5Lu")]
	[InlineData("5f")]
	[InlineData("5D")]
	[InlineData("1.5F")]
	[InlineData("1.5d")]
	[InlineData("0xdead")]
	[InlineData("0x1fUL")]
	public void Valid_suffixes_and_hexadecimal_digits_remain_valid(string literal)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered($"void test() {{ auto value = {literal}; }}"));
	}

	[Theory]
	[InlineData("4 + value")]
	[InlineData("value + 4")]
	[InlineData("4 - value")]
	[InlineData("value - 4")]
	[InlineData("4 * value")]
	[InlineData("value * 4")]
	[InlineData("4 / value")]
	[InlineData("4 % value")]
	[InlineData("4 | value")]
	[InlineData("value | 4")]
	[InlineData("4 & value")]
	[InlineData("value & 4")]
	[InlineData("4 ^ value")]
	[InlineData("value ^ 4")]
	[InlineData("(-(-4)) + value")]
	public void Fitting_literal_operands_take_the_unsigned_operand_type_in_either_order(string expression)
	{
		var compilation = SemanticCompiler.CompileLowered($"void test(uint value) {{ auto result = {expression}; }}");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		var local = Assert.Single(SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "test")));
		Assert.Equal("uint", local.Target.ResolvedType);
	}

	public static IEnumerable<object[]> FloatingIntegerTargets()
	{
		foreach (string target in new[] { "sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "nint", "nuint", "char", "achar", "wchar", "uchar" })
			foreach (string literal in new[] { "1.5", "-1.5", "1.5f", "1.5d" })
				yield return [target, literal];
	}

	[Theory]
	[MemberData(nameof(FloatingIntegerTargets))]
	public void Floating_literals_never_take_integer_targets(string target, string literal)
	{
		var compilation = SemanticCompiler.CompileLowered($$"""
			void take({{target}} value) { }
			{{target}} give() { return {{literal}}; }
			void test() { {{target}} value = {{literal}}; value = {{literal}}; take({{literal}}); }
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Equal(4, compilation.AnalysisDiagnostics.Distinct().Count());
		Assert.All(compilation.AnalysisDiagnostics, diagnostic =>
		{
			Assert.Contains("cannot convert", diagnostic.Message);
			Assert.NotNull(diagnostic.Range);
		});
	}

	[Fact]
	public void Floating_targets_integer_to_float_literals_and_explicit_integer_casts_remain_valid()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			float take(float value) => value;
			float give() => 1.5;
			void test() {
				float x = 1.5; double y = 2.75; x = -3.5; take(4.5);
				float integer = 3; double other = 4;
				int cast = (int)1.5; uint unsignedValue = (uint)2.75;
			}
			"""));
	}

	[Theory]
	[InlineData("double", "2.5d", "2.5")]
	[InlineData("float", "1f", "1.0f")]
	[InlineData("double", "2d", "2.0")]
	[InlineData("float", "1F", "1.0f")]
	[InlineData("double", "2D", "2.0")]
	[InlineData("int", "0x1f", "0x1f")]
	[InlineData("int", "0x2d", "0x2d")]
	public void Floating_suffixes_have_valid_c_spellings_and_leave_hex_digits_intact(string type, string literal, string expected)
	{
		var compilation = SemanticCompiler.CompileLowered($"export {type} value() => {literal};");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DirectoryInfo directory = Directory.CreateTempSubdirectory("camp-number-literal-");
		try
		{
			var emission = CCodeEmitter.Emit(compilation.Compilation,
				new CEmissionOptions { OutputDirectory = directory.FullName, ProjectName = "literal" });
			Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
			Assert.Contains($"return {expected};", string.Join("\n", emission.GeneratedSourceFiles.Select(File.ReadAllText)));
		}
		finally { directory.Delete(recursive: true); }
	}

	[Theory]
	[InlineData("5u", "uint")]
	[InlineData("4294967296u", "ulong")]
	[InlineData("5L", "long")]
	[InlineData("5UL", "ulong")]
	[InlineData("5lu", "ulong")]
	[InlineData("0x10u", "uint")]
	[InlineData("1.5f", "float")]
	[InlineData("-5L", "long")]
	public void Numeric_suffixes_determine_literal_type(string expression, string expected)
	{
		var compilation = SemanticCompiler.CompileLowered($"void test() {{ auto value = {expression}; }}");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		var local = Assert.Single(SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "test")));
		Assert.Equal(expected, local.Target.ResolvedType);
	}

	[Theory]
	[InlineData("byte", "5u")]
	[InlineData("byte", "256u")]
	[InlineData("byte", "256L")]
	[InlineData("int", "5L")]
	[InlineData("uint", "5L")]
	[InlineData("int", "1.5f")]
	public void Suffixed_literals_use_ordinary_implicit_conversions(string target, string expression)
	{
		var compilation = SemanticCompiler.CompileLowered($"void test() {{ {target} value = {expression}; }}");
		Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("Declaration initializer cannot convert"));
	}

	[Theory]
	[InlineData("2147483647", "int")]
	[InlineData("2147483648", "long")]
	[InlineData("5000000000", "long")]
	[InlineData("1 + 5000000000", "long")]
	[InlineData("0xFFFFFFFF", "long")]
	[InlineData("0xFFFFFFFF + 1", "long")]
	[InlineData("4294967296", "long")]
	[InlineData("-2147483648", "int")]
	[InlineData("-2147483649", "long")]
	[InlineData("-(9223372036854775808)", "long")]
	public void Untargeted_integer_literals_choose_int_or_long(string expression, string expected)
	{
		var compilation = SemanticCompiler.CompileLowered($"void test() {{ auto value = {expression}; }}");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		var local = Assert.Single(SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "test")));
		Assert.Equal(expected, local.Target.ResolvedType);
	}

	[Theory]
	[InlineData("9223372036854775808")]
	[InlineData("-9223372036854775809")]
	[InlineData("0x10000000000000000")]
	public void Untargeted_integer_literals_must_fit_long(string expression)
	{
		var compilation = SemanticCompiler.CompileLowered($"void test() {{ auto value = {expression}; }}");
		Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message.Contains("outside the range of type 'long'"));
	}

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
	[InlineData("long", "-32768L")]
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
