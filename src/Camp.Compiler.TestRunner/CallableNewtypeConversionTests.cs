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

	[Theory]
	[InlineData("fn")]
	[InlineData("delegate")]
	public void Constructed_callable_newtypes_convert_by_identity_and_from_structural_values(string kind)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype {{kind}} bool Check<U: int>(U value);
			bool read(int<short> value) => value == default;
			Check<short> same(Check<short> source) => source;
			void argument(Check<short> value) {}
			bool use(Check<short> source, {{kind}} bool(int<short>) structural)
			{
				Check<short> assigned = source;
				Check<short> fromStructural = structural;
				Check<short> fromFunction = read;
				Check<short> fromLambda = (int<short> value) => value == default;
				{{kind}} bool(int<short>) viewed = source;
				argument(source);
				argument(structural);
				argument(read);
				return same(source)((short)0) && source((short)0);
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Theory]
	[InlineData("fn", "short", "byte")]
	[InlineData("delegate", "short", "byte")]
	[InlineData("fn", "ushort", "short")]
	public void Constructed_callable_newtypes_with_different_arguments_do_not_convert(string kind, string first, string second)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype {{kind}} bool Check<U: int>(U value);
			Check<{{second}}> result(Check<{{first}}> source) => source;
			void argument(Check<{{second}}> value) {}
			void use(Check<{{first}}> source) { Check<{{second}}> target = source; argument(source); }
			""");
		Assert.Equal(3, compilation.Diagnostics.Distinct().Count());
		Assert.All(compilation.Diagnostics, message => Assert.Contains("cannot convert", message, StringComparison.OrdinalIgnoreCase));
	}

	[Theory]
	[InlineData("delegate")]
	[InlineData("once")]
	public void Aggregate_fields_preserve_callable_newtype_identity(string kind)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			newtype {{kind}} int First(int value);
			newtype {{kind}} int Second(int value);
			struct Handle { Second callback; }
			void use(First source) { Handle positional = { source }; Handle named = { .callback = source }; }
			""");
		Assert.Equal(2, compilation.Diagnostics.Distinct().Count());
		Assert.All(compilation.Diagnostics, message => Assert.Contains("Initializer field cannot convert", message));
	}

	[Fact]
	public void Aggregate_fields_accept_structural_delegates_and_same_newtypes()
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered("""
			newtype delegate int Reader(int value);
			struct Handle { Reader callback; }
			void use(delegate int(int) source, Reader nominal)
			{
				Handle structural = { source };
				Handle same = { .callback = nominal };
			}
			"""));
	}
}
