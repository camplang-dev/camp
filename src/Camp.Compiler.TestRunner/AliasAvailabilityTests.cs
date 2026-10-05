using System;
using System.Collections.Generic;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class AliasAvailabilityTests
{
	[Theory]
	[InlineData("int f(HA a) => 0;")]
	[InlineData("int f(HA* a) => 0;")]
	[InlineData("int f(HA[] a) => 0;")]
	[InlineData("HA f() => default;")]
	[InlineData("struct Wrapper { HA field; }")]
	[InlineData("void f() { HA a = default; }")]
	[InlineData("void f() { auto a = HA(); }")]
	[InlineData("int f(HB a) => 0;")]
	[InlineData("struct Wrapper<T: any> { T* value; } int f(Wrapper<HA>* a) => 0;")]
	public void Alias_uses_require_the_target_gate(string use)
	{
		SemanticCompilation compilation = Compile(("use.camp", $$"""
			requires (FA) struct H { int x; }
			alias HA = H;
			alias HB = HA;
			{{use}}
			"""));
		Assert.Empty(compilation.ParseDiagnostics);
		Assert.Empty(compilation.BindDiagnostics);
		Assert.Contains(compilation.AnalysisDiagnostics, static diagnostic =>
			diagnostic.Message.Contains("Type 'H' requires configuration 'FA', but that requirement is not proven here", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("requires (FA) int f(HA a) => 0;")]
	[InlineData("requires (FA) int f(HB a) => 0;")]
	[InlineData("void f() { if (configured(FA)) { HA a = default; } }")]
	[InlineData("alias Choice = configured(FA): H, int; int f(Choice a) => 0;")]
	[InlineData("export int main() => 0;")]
	public void Proven_and_unused_aliases_remain_valid(string use)
	{
		SemanticCompiler.AssertNoDiagnostics(Compile(("use.camp", $$"""
			requires (FA) struct H { int x; }
			alias HA = H;
			alias HB = HA;
			{{use}}
			""")));
	}

	[Theory]
	[InlineData("", false)]
	[InlineData("requires (FA)", true)]
	public void Qualified_aliases_preserve_the_target_requirement(string requirement, bool valid)
	{
		SemanticCompilation compilation = Compile(
			("library.camp", """
				namespace Lib;
				requires (FA) export struct H { int x; }
				export alias HA = H;
				"""),
			("use.camp", $$"""
				namespace App;
				{{requirement}} int f(Lib::HA a) => 0;
				"""));
		if (valid)
			SemanticCompiler.AssertNoDiagnostics(compilation);
		else
			Assert.Contains(compilation.AnalysisDiagnostics, static diagnostic =>
				diagnostic.Message.Contains("requires configuration 'FA'", StringComparison.Ordinal));
	}

	static SemanticCompilation Compile(params (string Path, string Text)[] sources)
	{
		ConfigurationFlagSet flags = new();
		List<string> errors = [];
		Assert.True(flags.TryDeclare("FA=false", false, ConfigurationFlagOwner.Module, "test", errors));
		Assert.Empty(errors);
		return SemanticCompiler.CompileLowered(flags, sources);
	}
}
