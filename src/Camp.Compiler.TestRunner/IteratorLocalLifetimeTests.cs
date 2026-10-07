using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class IteratorLocalLifetimeTests
{
	[Theory]
	[InlineData("struct", "const char[] text", "const char[] value = text; yield (int)value.length; value = text[..]; yield (int)value.length;")]
	[InlineData("class", "const char[] text", "const char[] value = text; yield (int)value.length; value = text[..]; yield (int)value.length;")]
	[InlineData("struct", "View this", "const char[] value = this.span(); yield (int)value.length; value = this.text; yield (int)value.length;")]
	[InlineData("class", "View this", "const char[] value = this.span(); yield (int)value.length; value = this.text; yield (int)value.length;")]
	[InlineData("struct", "const int* pointer", "const int* value = pointer; yield *value; value = pointer; yield *value;")]
	[InlineData("class", "const int* pointer", "const int* value = pointer; yield *value; value = pointer; yield *value;")]
	[InlineData("struct", "View input", "View value = input; yield (int)value.text.length; value = input; yield (int)value.text.length;")]
	[InlineData("class", "View input", "View value = input; yield (int)value.text.length; value = input; yield (int)value.text.length;")]
	public void Retained_parameter_values_can_initialize_and_reassign_lifted_locals(string kind, string parameters, string body)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			extern escaped void* malloc(nuint size);
			extern void free(void* pointer);
			struct View { const char[] text; }
			const char[] span(View this) { return this.text; }
			{{kind}} iter int values({{parameters}}) { {{body}} }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Theory]
	[InlineData("struct")]
	[InlineData("class")]
	public void A_temporary_array_cannot_initialize_a_retained_local(string kind)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			{{kind}} iter int values() { int[] value = [1, 2]; yield value[0]; yield value[1]; }
			""");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("cannot store a scoped pointer-bearing value"));
	}

	[Theory]
	[InlineData("struct")]
	[InlineData("class")]
	public void Iterator_parameter_values_cannot_escape_into_ordinary_object_fields(string kind)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			struct Holder { const char[] text; }
			{{kind}} iter int values(const char[] input, Holder* destination) {
			    destination.text = input;
			    yield 1;
			}
			""");
		Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Contains("cannot store a scoped pointer-bearing value"));
	}
}
