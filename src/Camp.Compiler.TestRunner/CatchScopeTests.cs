using System;
using Xunit;

namespace Camp.Compiler.Tests;

public class CatchScopeTests
{
	const string Declarations = """
		enum Fault { OK = 0, BAD }
		enum Other { OK = 0, BAD }
		int checked(thrown Fault error) { throw BAD; }
		void use(int value) { }
		""";

	[Theory]
	[InlineData("try { checked(); } catch (Fault fault) { } use((int)fault);")]
	[InlineData("try { checked(); } catch (Fault fault) { } finally { use((int)fault); }")]
	[InlineData("try { checked(); } catch (Fault fault) { } catch (Other other) { use((int)fault); }")]
	[InlineData("try { use((int)fault); checked(); } catch (Fault fault) { }")]
	public void Catch_local_is_unavailable_outside_its_handler(string body)
	{
		var compilation = SemanticCompiler.CompileLowered(("catch_scope.camp", Declarations + "void run() { " + body + " }"));
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("Symbol 'fault' could not be found.", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("try { checked(); } catch (Fault fault) { use((int)fault); }")]
	[InlineData("try { checked(); } catch (Fault fault) { } int fault = 3; use(fault);")]
	[InlineData("try { checked(); } catch (Fault fault) { } try { checked(); } catch (Fault fault) { use((int)fault); }")]
	[InlineData("try { checked(); } catch (Fault fault) { } catch (Other fault) { use((int)fault); }")]
	[InlineData("int fault = 3; try { checked(); } catch (Fault caught) { use((int)caught); } use(fault);")]
	[InlineData("while (true) { try { checked(); } catch (Fault fault) { if (fault == BAD) break; continue; } }")]
	public void Handler_has_its_own_local_and_preserves_enclosing_context(string body)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(("catch_scope.camp", Declarations + "void run() { " + body + " }")));
	}

	[Fact]
	public void Catch_local_cannot_shadow_an_enclosing_local()
	{
		var compilation = SemanticCompiler.CompileLowered(("catch_scope.camp", Declarations +
			"void run() { int fault = 3; try { checked(); } catch (Fault fault) { use((int)fault); } }"));
		Assert.Contains(compilation.AnalysisDiagnostics, d => d.Message == "Symbol 'fault' is already declared in this scope.");
	}
}
