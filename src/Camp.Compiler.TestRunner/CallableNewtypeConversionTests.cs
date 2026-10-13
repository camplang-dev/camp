using System;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CallableNewtypeConversionTests
{
	[Theory]
	[InlineData("fn", "First", "Second")]
	[InlineData("delegate", "First", "Second")]
	[InlineData("once", "First", "Second")]
	[InlineData("fn", "First<int>", "Second<int>")]
	[InlineData("delegate", "First<int>", "Second<int>")]
	public void Distinct_callable_newtypes_do_not_convert_implicitly(string kind, string first, string second)
	{
		string generic = first.Contains('<') ? "<T: int>" : "";
		string input = generic.Length == 0 ? "int" : "T";
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype {{kind}} int First{{generic}}({{input}} value);
			newtype {{kind}} int Second{{generic}}({{input}} value);
			{{second}} result({{first}} source) => source;
			void argument({{second}} value) {}
			void use({{first}} source) { {{second}} target = source; argument(source); }
			""");
		Assert.Equal(3, compilation.Diagnostics.Distinct().Count());
		Assert.All(compilation.Diagnostics, message => Assert.Contains("cannot convert", message, StringComparison.OrdinalIgnoreCase));
	}

	[Theory]
	[InlineData("fn")]
	[InlineData("delegate")]
	public void Matching_structural_callables_and_same_nominal_values_convert(string kind)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype {{kind}} int Reader(int value);
			alias AlsoReader = Reader;
			int read(int value) => value;
			void use({{kind}} int(int) source, Reader nominal)
			{
				Reader fromSource = source;
				Reader fromFunction = read;
				Reader fromLambda = (int value) => value;
				AlsoReader fromNominal = nominal;
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}
}
