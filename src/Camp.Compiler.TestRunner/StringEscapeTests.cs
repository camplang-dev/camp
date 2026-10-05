using System.Collections.Generic;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class StringEscapeTests
{
	[Theory]
	[InlineData(@"\q")]
	[InlineData(@"\1")]
	[InlineData(@"\`")]
	[InlineData(@"\x")]
	[InlineData(@"\xG")]
	[InlineData(@"\u12")]
	[InlineData(@"\u123")]
	[InlineData(@"\u123G")]
	[InlineData(@"\U00041")]
	[InlineData(@"\U00110000")]
	[InlineData(@"\UFFFFFFFF")]
	[InlineData(@"\U0000D800")]
	public void Invalid_string_escapes_are_source_diagnostics(string text)
	{
		foreach (string prefix in new[] { "", "$" })
		{
			SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void test() {{ auto value = {prefix}\"{text}\"; }}");
			Assert.Empty(compilation.ParseDiagnostics);
			BindDiagnostic diagnostic = Assert.Single(compilation.BindDiagnostics.Distinct());
			Assert.Equal("String literal contains an invalid escape sequence.", diagnostic.Message);
			Assert.Equal(1, Assert.IsType<TokenRange>(diagnostic.Range).StartLineNumber);
		}
	}

	[Theory]
	[MemberData(nameof(ValidEscapes))]
	public void String_and_constant_interpolation_decode_the_same_text(string text, string expected)
	{
		foreach (string prefix in new[] { "", "$" })
		{
			SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void test() {{ auto value = {prefix}\"{text}\"; }}");
			SemanticCompiler.AssertNoDiagnostics(compilation);
			LiteralExpression literal = Assert.Single(SemanticCompiler.Descendants<LiteralExpression>(compilation.Module));
			Assert.Equal(expected, literal.Value);
		}
	}

	public static IEnumerable<object[]> ValidEscapes()
	{
		yield return [@"\'", "'"]; yield return ["\\\"", "\""]; yield return [@"\\", "\\"];
		yield return [@"\0", "\0"]; yield return [@"\a", "\a"]; yield return [@"\b", "\b"];
		yield return [@"\e", "\u001B"]; yield return [@"\f", "\f"]; yield return [@"\n", "\n"];
		yield return [@"\r", "\r"]; yield return [@"\t", "\t"]; yield return [@"\v", "\v"];
		yield return [@"\x4", "\u0004"]; yield return [@"\x41", "A"]; yield return [@"\x041", "A"];
		yield return [@"\x0041", "A"]; yield return [@"\u0041", "A"]; yield return [@"\U00000041", "A"];
		yield return [@"\x41B", "Л"]; yield return [@"\x0041B", "AB"]; yield return [@"\u0041B", "AB"];
		yield return [@"\U00000041B", "AB"]; yield return [@"\01", "\01"];
		yield return [@"\U0001F600", "😀"]; yield return [@"\uD83D\uDE00", "😀"];
		yield return [@"\uFFFF", "\uFFFF"];
		yield return [@"\U0010FFFF", char.ConvertFromUtf32(0x10FFFF)];
		yield return ["", ""]; yield return ["tail", "tail"];
	}

	[Fact]
	public void Wide_strings_preserve_an_escaped_surrogate_code_unit()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("void test() { wstring value = \"\\uD800\"; }");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		LiteralExpression literal = Assert.Single(SemanticCompiler.Descendants<LiteralExpression>(compilation.Module));
		string value = Assert.IsType<string>(literal.Value);
		Assert.Equal(0xD800, (int)Assert.Single(value));
	}

	[Fact]
	public void Runtime_interpolation_decodes_all_text_segments_including_the_last_character()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileDeclarations("""
			string format(int number) { auto text = $"\x0041{number}\x41B"; return "done"; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		InterpolatedStringExpression interpolation = Assert.Single(SemanticCompiler.Descendants<InterpolatedStringExpression>(compilation.Module));
		Assert.Equal(new[] { "A", "Л" }, interpolation.Segments.OfType<InterpolatedStringTextSegment>().Select(segment => segment.Text));
	}
}
