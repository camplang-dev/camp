using System;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ConstErasedPointerTests
{
	[Theory]
	[InlineData("int?")]
	[InlineData("int[]")]
	[InlineData("int[]?")]
	public void Const_erased_pointers_expand_in_initializers_arguments_and_returns(string payload)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			void read(const {{payload}}* value) {}
			const {{payload}}* result(const void<{{payload}}>* value) => value;
			void use(const void<{{payload}}>* value)
			{
				const {{payload}}* view = value;
				view = value;
				read(value);
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Theory]
	[InlineData("int?")]
	[InlineData("int[]")]
	[InlineData("int[]?")]
	public void Const_erased_pointers_cannot_expand_to_mutable_views(string payload)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			void use(const void<{{payload}}>* value) { {{payload}}* mutable = value; }
			""");
		Assert.NotEmpty(compilation.Diagnostics);
		Assert.All(compilation.Diagnostics, message => Assert.Contains("cannot convert", message, StringComparison.OrdinalIgnoreCase));
	}
}
