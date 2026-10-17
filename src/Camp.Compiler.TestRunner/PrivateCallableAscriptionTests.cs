using System.IO;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class PrivateCallableAscriptionTests
{
	[Fact]
	public void Export_api_and_metadata_omit_private_ascription()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			newtype fn bool Check<U: int>(U value);
			export bool zeroOf<V: int>(V value): Check<V> => value == default;
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		using StringWriter api = new();
		BindableNodeCodeSerializer.Serialize(compilation.Module, api, new BindableNodeCodeSerializerOptions { ApiHeader = true });
		Assert.DoesNotContain("Check", api.ToString());
		Assert.DoesNotContain("\"ascription\"", MetadataJsonSerializer.Serialize(compilation.Compilation, MetadataVisibility.Export));
		Assert.Contains("\"ascription\"", MetadataJsonSerializer.Serialize(compilation.Compilation, MetadataVisibility.All));
	}

	[Theory]
	[InlineData("", "fn bool(int<byte>)")]
	[InlineData("export ", "Check<byte>")]
	public void Imported_function_references_use_only_visible_nominal_types(string visibility, string expected)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileApiConsumer($$"""
			{{visibility}}newtype fn bool Check<U: int>(U value);
			export extern bool zeroOf<V: int>(V value): Check<V>;
			""", """
			bool test() { auto zero = zeroOf<byte>; return zero((byte)0); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DeclarationStatement local = Assert.Single(SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "test")));
		Assert.Equal(expected, local.Target.ResolvedType);
	}

	[Fact]
	public void Private_ascription_keeps_nominal_identity_in_its_own_file()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			newtype fn bool Check<U: int>(U value);
			bool zeroOf<V: int>(V value): Check<V> => value == default;
			bool test() { auto zero = zeroOf<byte>; return zero((byte)0); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		Assert.Equal("Check<byte>", Assert.Single(SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "test"))).Target.ResolvedType);
	}
}
