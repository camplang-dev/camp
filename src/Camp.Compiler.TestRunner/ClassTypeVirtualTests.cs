using System.Linq;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ClassTypeVirtualTests
{
	[Fact]
	public void Virtual_results_and_out_slots_rebind_to_the_static_receiver()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			abstract class Root {
				abstract classtype* clone();
				abstract void peer(out classtype* value);
			}
			virtual class Middle : Root {
				override classtype* clone() { return this; }
				override void peer(out classtype* value) { value = this; }
			}
			sealed class Leaf : Middle {
				sealed classtype* clone() { return this; }
				sealed void peer(out classtype* value) { value = this; }
			}
			void check(Root* root, Middle* middle, Leaf* leaf) {
				Root* r = root.clone(); root.peer(out r);
				Middle* m = middle.clone(); middle.peer(out m);
				Leaf* l = leaf.clone(); leaf.peer(out l);
			}
			"""));
	}

	[Theory]
	[InlineData("abstract void compare(classtype* value);", "only in return types or out parameter types")]
	[InlineData("abstract fn classtype*() factory();", "nested callable types")]
	[InlineData("abstract void peer(out fn classtype*() value);", "nested callable types")]
	[InlineData("abstract iter classtype* all();", "nested callable types")]
	public void Invalid_virtual_positions_report_the_source_contract(string method, string message)
	{
		var compilation = SemanticCompiler.CompileLowered("abstract class Root { " + method + " }");
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains(message));
		Assert.DoesNotContain(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("valid only inside class") || diagnostic.Message.Contains("fields or static fields"));
	}

	[Fact]
	public void An_override_must_preserve_the_class_relative_contract()
	{
		var compilation = SemanticCompiler.CompileLowered("abstract class Root { abstract classtype* clone(); } sealed class Leaf : Root { override Root* clone() { return this; } }");
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("must match an inherited"));
	}
}
