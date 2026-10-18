using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class SourceCaptureDiagnosticTests
{
	[Theory]
	[InlineData("sourceof(text, text)", "exactly one positional argument")]
	[InlineData("sourceof(text + text)", "single unqualified parameter name")]
	[InlineData("caller(sourcefile, 1)", "exactly one positional argument")]
	[InlineData("caller(1 + 2)", "single unqualified selector name")]
	public void Malformed_capture_defaults_have_source_positions(string value, string message)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileDeclarations("\nextern string bad(string text, string again = " + value + ");");
		BindDiagnostic diagnostic = Assert.Single(compilation.BindDiagnostics);
		Assert.Contains(message, diagnostic.Message);
		TokenRange range = Assert.IsType<TokenRange>(diagnostic.Range);
		Assert.Equal(2, range.StartLineNumber);
		Assert.True(range.StartColumn > 1);
	}

	[Fact]
	public void Self_capture_is_rejected_and_other_parameters_remain_valid()
	{
		SemanticCompilation invalid = SemanticCompiler.CompileLowered("extern string bad(string text = sourceof(text));");
		Assert.Contains(invalid.Diagnostics, message => message.Contains("does not name a parameter"));
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("extern string good(string text, string again = sourceof(text));"));
	}
}
