using Xunit;

namespace Camp.Compiler.Tests;

public sealed class PreparedResultLifetimeTests
{
	const string Declarations = """
		extern escaped void* malloc(nuint size);
		struct Holder { char[] kept; }
		nuint render(int value, prep char[] buffer = default) { return (nuint)value; }
		""";

	[Theory]
	[InlineData("holder.kept = render(3);")]
	[InlineData("char[] text = render(3); holder.kept = text;")]
	[InlineData("auto text = render(3); auto copy = text; holder.kept = copy;")]
	[InlineData("holder.kept = choose ? render(3) : render(2);")]
	[InlineData("fn nuint(int, prep char[]) writer = render; holder.kept = writer(3);")]
	[InlineData("delegate nuint(int, prep char[]) writer = render; holder.kept = writer(3);")]
	public void Scoped_prepared_results_cannot_escape_into_parameter_fields(string body)
	{
		var compilation = SemanticCompiler.CompileLowered(Declarations + " void store(Holder* holder, bool choose) { " + body + " }");
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("Assignment cannot store a scoped pointer-bearing value"));
	}

	[Theory]
	[InlineData("void use() { Holder local = default; local.kept = render(3); }")]
	[InlineData("void use(Holder* holder) { holder.kept = (new) render(3); }")]
	[InlineData("char[] use() { return (new) render(3); }")]
	public void Local_retention_and_allocated_results_remain_valid(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(Declarations + source));
	}

	[Theory]
	[InlineData("for (int i = 0; i < 2; i++) { char[] text = (new) render(3); }")]
	[InlineData("char[] text = default; for (int i = 0; i < 2; i++) { text = (new) render(3); }")]
	[InlineData("while (repeat) { char[] text = (new) render(3); repeat = false; }")]
	public void Heap_prepared_results_are_allowed_inside_loops(string body)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(Declarations + " void use(bool repeat) { " + body + " }"));
	}

	[Fact]
	public void Implicit_prepared_results_still_require_storage_outside_loops()
	{
		var compilation = SemanticCompiler.CompileLowered(Declarations + " void use() { for (int i = 0; i < 2; i++) { char[] text = render(3); } }");
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("implicit dynamic stack allocation inside a loop"));
	}
}
