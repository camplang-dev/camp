using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CapabilityArgumentOrderTests
{
	[Theory]
	[InlineData("vtableof(T: Reader), sizeof(T), typenameof(T)")]
	[InlineData("vtableof(T: Reader), typenameof(T), sizeof(T)")]
	[InlineData("sizeof(T), vtableof(T: Reader), typenameof(T)")]
	[InlineData("sizeof(T), typenameof(T), vtableof(T: Reader)")]
	[InlineData("typenameof(T), vtableof(T: Reader), sizeof(T)")]
	[InlineData("typenameof(T), sizeof(T), vtableof(T: Reader)")]
	public void Capabilities_follow_source_order_at_concrete_and_forwarded_calls(string capabilities)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			interface Reader { int read(); }
			struct Box: Reader { int value; int read(): Reader => this.value; }
			int read<T: implements Reader>(T* source, {{capabilities}}) => source.read();
			int forward<T: implements Reader>(T* source, {{capabilities}}) => read(source);
			void test() { Box box = default; read(&box); forward(&box); }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition read = SemanticCompiler.Function(compilation, "read");
		CallExpression direct = SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "test")).First();
		Assert.Equal(read.Parameters.Count, direct.Arguments.Count);
		for (int i = 1; i < read.Parameters.Count; i++)
		{
			Expression? value = direct.Arguments[i].Value;
			switch (read.Parameters[i])
			{
				case SizeOfParameterDefinition: Assert.IsType<SizeOfExpression>(value); break;
				case NameOfParameterDefinition: Assert.Equal(LiteralKind.String, Assert.IsType<LiteralExpression>(value).Kind); break;
				case VTableOfParameterDefinition: Assert.Equal("const Reader*", value?.ResolvedType); break;
			}
		}
		FunctionDefinition forward = SemanticCompiler.Function(compilation, "forward");
		CallExpression forwarded = Assert.Single(SemanticCompiler.Descendants<CallExpression>(forward), call => call.Arguments.Count == forward.Parameters.Count);
		Assert.Equal(forward.Parameters.Count, forwarded.Arguments.Count);
		for (int i = 1; i < forward.Parameters.Count; i++)
			Assert.Same(forward.Parameters[i], Assert.IsType<VariableReferenceExpression>(forwarded.Arguments[i].Value).Variable);
	}
}
