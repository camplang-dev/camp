using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class ArrayMemberCallResultTests
{
	[Theory]
	[InlineData("return scanOf(text).span;")]
	[InlineData("const char[] local = scanOf(text).span; return local;")]
	[InlineData("return scanOf(text[..]).span;")]
	[InlineData("consume(scanOf(text).span); return text;")]
	public void Captured_member_receiver_call_keeps_array_argument_components(string body)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			struct Scan { const char[] span; nuint consumed; }
			Scan scanOf(const char[] text) { Scan result = { text, 3 }; return result; }
			void consume(const char[] text) { }
			const char[] read(const char[] text) { {{body}} }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "read")),
			static call => call.Target is MethodReferenceExpression method
				&& method.Candidates.Any(static function => function.Name == "scanOf"));
		Assert.Equal(2, call.Arguments.Count);
		Assert.Equal("nuint", call.Arguments[1].Value?.ResolvedType);
	}

	[Theory]
	[InlineData("fn", 2)]
	[InlineData("delegate", 3)]
	public void Callable_receiver_keeps_its_array_argument_and_context(string kind, int argumentCount)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			struct Scan { const char[] span; }
			const char[] read({{kind}} Scan(const char[]) factory, const char[] text) { return factory(text).span; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "read")));
		Assert.Equal(argumentCount, call.Arguments.Count);
	}

	[Fact]
	public void Method_receiver_keeps_its_instance_and_array_argument()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			struct Scan { const char[] span; }
			struct Scanner { int offset; }
			Scan scanOf(Scanner* this, const char[] text) { Scan result = { text }; return result; }
			const char[] read(Scanner* scanner, const char[] text) { return scanner.scanOf(text).span; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "read")));
		Assert.Equal(3, call.Arguments.Count);
	}

	[Fact]
	public void Optional_member_receiver_keeps_array_arguments_too()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			struct Scan { int? count; }
			Scan scanOf(const char[] text) { Scan result = { (int)text.length }; return result; }
			int? read(const char[] text) { return scanOf(text).count; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
		CallExpression call = Assert.Single(SemanticCompiler.Descendants<CallExpression>(SemanticCompiler.Function(compilation, "read")));
		Assert.Equal(2, call.Arguments.Count);
	}
}
