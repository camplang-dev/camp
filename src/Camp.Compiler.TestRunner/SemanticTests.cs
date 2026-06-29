using System.Collections.Generic;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class SemanticTests
{
	[Fact]
	public void Callable_shapes_preserve_callspecs_and_constof_variance()
	{
		Assert.True(CallableShapeService.TryParseCallableShape("fn _stdcall int(int)", out CallableShape source));
		Assert.True(CallableShapeService.TryParseCallableShape("fn _stdcall int(int)", out CallableShape target));

		Assert.Equal("fn", source.Kind);
		Assert.Equal("_stdcall", source.Spec);
		Assert.Equal("int", source.ReturnType);
		Assert.Equal(["int"], source.Parameters);
		Assert.True(CallableShapeService.Compatible(source, target, compareThis: false));
		Assert.True(CallableShapeService.SlotTypesCompatible("constof(source) int*", "const int*", outputPosition: true, static type => type.Replace("constof(source) ", "const ")));
	}

	[Fact]
	public void Expanded_callable_parameter_lists_include_abi_components()
	{
		List<ParameterDefinition> parameters =
		[
			new() { Name = "text", ResolvedType = "const char[]" },
			new() { Name = "transform", ResolvedType = "Transform" }
		];
		Assert.Equal(["const char*", "nuint", "fn int(void*, int)", "void*"], ExpandedFormService.GetExpandedCallableParameterTypes(parameters, TryShape));

		static bool TryShape(TypeReference? type, string? resolvedType, string baseName, out ParamsComponentShape shape)
		{
			if (resolvedType == "const char[]")
			{
				shape = new ParamsComponentShape(ParamsComponentShapeKind.Array, resolvedType, [
					new ParamsComponent("elements", "const char*", baseName + "_elements", null, ParamsComponentShapeKind.Array),
					new ParamsComponent("length", "nuint", baseName + "_length", null, ParamsComponentShapeKind.Array)
				]);
				return true;
			}
			if (resolvedType == "Transform")
			{
				shape = new ParamsComponentShape(ParamsComponentShapeKind.Delegate, resolvedType, [
					new ParamsComponent("call", "fn int(void*, int)", baseName, null, ParamsComponentShapeKind.Delegate),
					new ParamsComponent("context", "void*", baseName + "_context", null, ParamsComponentShapeKind.Delegate)
				]);
				return true;
			}
			shape = null!;
			return false;
		}
	}

	[Fact]
	public void Lowered_semantics_expose_symbols_and_generated_interface_accessors()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			interface IFace
			{
				int getValue();
			}

			@symbol("Native_add")
			int add(int left, int right) => left + right;

			class Box: IFace
			{
				int value;
				int getValue() => this.value;
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition add = SemanticCompiler.Function(compilation, "add");
		Assert.Equal("Native_add", add.Symbol);

		TypeDefinition box = SemanticCompiler.Type(compilation, "Box");
		Assert.Contains(SemanticCompiler.Descendants<FunctionDefinition>(box), static function => function.Symbol == "Box_getValue");
		Assert.Contains(SemanticCompiler.Descendants<FunctionDefinition>(compilation.Module), static function => function.Name == "getIFace" && function.Symbol == "Box_getIFace");
	}

	[Fact]
	public void Symbol_name_service_distinguishes_source_callable_and_abi_names()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			@symbol("Native_add")
			int add(overload int left, int right) => left + right;
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition add = SemanticCompiler.Function(compilation, "add");
		Assert.Equal(new DeclarationName(DeclarationNameKind.Source, "add"), SymbolNameService.SourceName(add));
		Assert.Equal(new DeclarationName(DeclarationNameKind.Callable, "addInt"), SymbolNameService.CallableName(add));
		Assert.Equal(new DeclarationName(DeclarationNameKind.Invoker, "add"), SymbolNameService.InvokerName(add));
		Assert.Equal(new DeclarationName(DeclarationNameKind.Symbol, "Native_add"), SymbolNameService.SymbolName(add));
		Assert.Contains(SymbolNameService.TopLevelSymbolNames(add, static _ => null), name => name.Value == "Native_add");
	}

	[Fact]
	public void Lowered_semantics_expose_lambda_helpers_without_golden_files()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			void run()
			{
				delegate int(int value) doubleValue = value => value * 2;
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		Assert.Contains(SemanticCompiler.Descendants<FunctionDefinition>(compilation.Module), static function => function.Symbol.Contains("lambda", System.StringComparison.OrdinalIgnoreCase));
	}
}
