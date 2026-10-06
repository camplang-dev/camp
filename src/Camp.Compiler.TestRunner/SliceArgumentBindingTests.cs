using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class SliceArgumentBindingTests
{
	[Theory]
	[InlineData("context")]
	[InlineData("otherContext")]
	[InlineData("CONTEXT")]
	[InlineData("contextId")]
	[InlineData("ctx")]
	[InlineData("declaration")]
	public void Scalar_argument_name_does_not_hide_the_following_slice(string name)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			struct Picker {
			    bool pick(uint id, uint[] atoms, uint* count) { *count = (uint)atoms.length; return id != 0; }
			    bool check(uint {{name}}) {
			        fixed uint[16] atoms = default;
			        uint count = 0;
			        return this.pick({{name}}, atoms[..], &count);
			    }
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(compilation.Module),
			static call => call.Target is MethodReferenceExpression method
				&& method.Candidates.Any(static function => function.Name == "pick"));
		Assert.Equal(5, call.Arguments.Count);
		Assert.Equal("nuint", call.Arguments[3].Value?.ResolvedType);
		Assert.DoesNotContain(call.Arguments, static argument => argument.Value is IndexExpression);
	}
}
