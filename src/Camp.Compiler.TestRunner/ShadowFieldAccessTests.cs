using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ShadowFieldAccessTests
{
	const string Types = """
		extern escaped void* malloc(nuint size);
		extern void free(void* value);
		class Host {
			escaped void* shadowData;
			int hostValue;
			static int shared;
			Host() { }
			@getshadow escaped void* getShadow(const this) => this.shadowData;
			@setshadow void setShadow(escaped void* value) => this.shadowData = value;
			int readBase() => this.hostValue;
		}
		shadow class View: Host {
			int value;
			View() { }
			void cleanup() { delete shadow; }
		}
		shadow class Derived: View {
			Derived() { base(); }
			void cleanupDerived() { delete shadow; }
		}
		void output(out int value) { value = 1; }
		""";

	[Theory]
	[InlineData("View*", "int value = view.hostValue;")]
	[InlineData("const View*", "int value = view.hostValue;")]
	[InlineData("Derived*", "int value = view.hostValue;")]
	[InlineData("View*", "view.hostValue = 1;")]
	[InlineData("View*", "view.hostValue += 1;")]
	[InlineData("View*", "view.hostValue++;")]
	[InlineData("View*", "++view.hostValue;")]
	[InlineData("View*", "view.hostValue--;")]
	[InlineData("View*", "--view.hostValue;")]
	[InlineData("View*", "int* address = &view.hostValue;")]
	[InlineData("View*", "output(out view.hostValue);")]
	public void Non_shadow_base_fields_are_inaccessible_through_shadow_receivers(string receiver, string body)
	{
		var compilation = SemanticCompiler.CompileLowered(Types + $"void test({receiver} view) {{ {body} }}");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		var diagnostic = Assert.Single(compilation.AnalysisDiagnostics.Distinct());
		Assert.Equal("Field 'hostValue' is declared in non-shadow base class 'Host' and is not accessible to shadow classes.", diagnostic.Message);
		Assert.Equal("hostValue", Assert.IsType<TokenRange>(diagnostic.Range).Value);
	}

	[Fact]
	public void This_cannot_access_non_shadow_base_fields()
	{
		var compilation = SemanticCompiler.CompileLowered(Types + """
			shadow class Other: View {
				Other() { base(); }
				int read() => this.hostValue;
				void write() { this.hostValue = 1; }
				void cleanupOther() { delete shadow; }
			}
			""");
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Equal(2, compilation.AnalysisDiagnostics.Distinct().Count());
		Assert.All(compilation.AnalysisDiagnostics, d => Assert.Contains("declared in non-shadow base class 'Host'", d.Message));
	}

	[Fact]
	public void Shadow_fields_base_methods_and_base_typed_storage_remain_accessible()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(Types + """
			int test(Derived* view) {
				view.value = 2;
				Host* host = view;
				host.hostValue = 3;
				Host.shared = 4;
				return view.value + view.readBase() + host.hostValue + Host.shared;
			}
			"""));
	}
}
