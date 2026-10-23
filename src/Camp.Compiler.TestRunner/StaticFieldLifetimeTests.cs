using Xunit;

namespace Camp.Compiler.Tests;

public sealed class StaticFieldLifetimeTests
{
	[Theory]
	[InlineData("last = input;")]
	[InlineData("Holder.last = input;")]
	[InlineData("fixed char[8] buffer = $\"abc\"; last = buffer[..];")]
	public void Static_fields_reject_scoped_values(string body)
	{
		var result = SemanticCompiler.CompileLowered("class Holder { static const char[] last; static void store(const char[] input) { " + body + " } }");
		Assert.Contains(result.Diagnostics, message => message.Contains("cannot store a non-escaped pointer-bearing value in escaped storage"));
	}

	[Fact]
	public void Static_fields_accept_escaped_values()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("class Holder { static const char[] last; static void store(escaped const char[] input) { last = input; Holder.last = \"safe\"; } }"));
	}
}
