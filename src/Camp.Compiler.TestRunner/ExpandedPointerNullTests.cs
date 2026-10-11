using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ExpandedPointerNullTests
{
	[Theory]
	[InlineData("int?*")]
	[InlineData("int[]?*")]
	[InlineData("int[]*")]
	[InlineData("delegate int()?*")]
	[InlineData("int?**")]
	public void Null_initializers_default_every_pointer_component(string type)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			{{type}} globalPointer = null;
			{{type}} make() => null;
			void test() { {{type}} pointer = (null); pointer = null; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		DeclarationStatement[] declarations = SemanticCompiler.Descendants<DeclarationStatement>(SemanticCompiler.Function(compilation, "test")).ToArray();
		Assert.True(declarations.Length >= 2);
		Assert.All(declarations, declaration =>
		{
			DefaultExpression initializer = Assert.IsType<DefaultExpression>(declaration.InitialValue);
			Assert.Equal(declaration.Target.ResolvedType, initializer.ResolvedType);
			Assert.EndsWith("*", initializer.ResolvedType);
		});
	}
}
