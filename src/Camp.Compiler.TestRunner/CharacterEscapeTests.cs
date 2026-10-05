using System.Collections.Generic;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CharacterEscapeTests
{
	[Theory]
	[InlineData(@"\q")]
	[InlineData(@"\1")]
	[InlineData(@"\`")]
	[InlineData(@"\x")]
	[InlineData(@"\u12")]
	[InlineData(@"\u123")]
	[InlineData(@"\U00041")]
	[InlineData(@"\U00110000")]
	[InlineData(@"\UFFFFFFFF")]
	[InlineData(@"\uD800")]
	[InlineData(@"\xDFFF")]
	[InlineData(@"\U0000D800")]
	[InlineData(@"\uD83D\uDE00")]
	[InlineData(@"\x00410")]
	[InlineData("")]
	[InlineData("ab")]
	public void Invalid_character_escapes_are_reported_at_the_literal(string text)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void test() {{ auto value = '{text}'; }}");
		Assert.Empty(compilation.ParseDiagnostics);
		BindDiagnostic diagnostic = Assert.Single(compilation.BindDiagnostics.Distinct());
		Assert.Equal("Character literal must contain exactly one Unicode scalar value.", diagnostic.Message);
		Assert.Equal(28, Assert.IsType<TokenRange>(diagnostic.Range).StartColumn);
	}

	[Theory]
	[MemberData(nameof(ValidEscapes))]
	public void Valid_character_escapes_decode_to_the_expected_scalar(string text, int scalar)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($"void test() {{ auto value = '{text}'; }}");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		LiteralExpression literal = Assert.Single(SemanticCompiler.Descendants<LiteralExpression>(compilation.Module));
		Assert.Equal(scalar, literal.CodePoint);
	}

	public static IEnumerable<object[]> ValidEscapes()
	{
		yield return [@"\'", 39]; yield return ["\\\"", 34]; yield return [@"\\", 92];
		yield return [@"\0", 0]; yield return [@"\a", 7]; yield return [@"\b", 8];
		yield return [@"\e", 27]; yield return [@"\f", 12]; yield return [@"\n", 10];
		yield return [@"\r", 13]; yield return [@"\t", 9]; yield return [@"\v", 11];
		yield return [@"\x4", 4]; yield return [@"\x41", 65]; yield return [@"\x041", 65];
		yield return [@"\x0041", 65]; yield return [@"\u0041", 65]; yield return [@"\U00000041", 65];
		yield return [@"\xAbCd", 0xABCD]; yield return [@"\uFFFF", 0xFFFF];
		yield return [@"\U0001F600", 0x1F600]; yield return [@"\U0010FFFF", 0x10FFFF];
		yield return ["😀", 0x1F600];
	}
}
