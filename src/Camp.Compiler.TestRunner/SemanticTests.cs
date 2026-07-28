using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class SemanticTests
{
	[Fact]
	public void Interpolated_strings_bind_to_formatter_protocol_targets()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			newtype delegate nuint TextFormatter(const this, char[] buffer = default);

			class Value
			{
				int number;

				nuint format(overload char[] buffer) : TextFormatter
				{
					return 1;
				}
			}

			void write(overload TextFormatter value)
			{
			}

			void main()
			{
				Value* value = null;
				auto inferred = $"value {value}";
				TextFormatter explicitTarget = $"value {value}";
				write($"value {value}");
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		IReadOnlyList<InterpolatedStringExpression> interpolations = SemanticCompiler.Descendants<InterpolatedStringExpression>(compilation.Module);
		Assert.All(interpolations, static interpolation => Assert.Equal("TextFormatter", interpolation.ResolvedType));
		Assert.All(interpolations, static interpolation => Assert.Equal("TextFormatter", interpolation.FormatterType));
	}

	[Fact]
	public void Interpolated_string_overload_selector_does_not_inspect_holes()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			newtype delegate nuint TextFormatter(const this, char[] buffer = default);
			newtype delegate nuint OtherFormatter(const this, char[] buffer = default);

			nuint format(in int this, overload char[] buffer) : TextFormatter
			{
				return 1;
			}

			void write(overload TextFormatter value)
			{
			}

			void write(overload OtherFormatter value)
			{
			}

			void main()
			{
				write($"value {42}");
			}
			""");

		Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Contains("multiple formatter-shaped overloads", StringComparison.Ordinal));
	}

	[Fact]
	public void Interpolated_string_reports_missing_formatter_for_first_runtime_hole()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			struct Value
			{
				int number;
			}

			void main()
			{
				Value value = { .number = 1 };
				auto text = $"value {value}";
			}
			""");

		Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Contains("does not establish a UTF-8 formatter type", StringComparison.Ordinal));
	}

	[Fact]
	public void Abi_surface_exposes_exported_symbols_and_expanded_parameters()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			export interface IFace
			{
				int getValue();
			}

			export struct Pair
			{
				int left;
				int right;
			}

			export enum Mode
			{
				ONE,
				TWO
			}

			export newtype Id: int;

			extern void* malloc(nuint size);

			export class Box: IFace
			{
				int value;
				int getValue(): IFace => this.value;
			}

			export inline uint MAGIC = 42;
			export int globalCount;

			@symbol("Native_sum")
			export int sum(const int[] values)
			{
				return (int)values.length;
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		AbiSurface surface = AbiSurface.Build(compilation.Compilation);

		Assert.Contains(surface.Types, static type => type is { Name: "IFace", Kind: AbiDeclarationKind.Interface, Visibility: AbiVisibility.Export });
		Assert.Contains(surface.Types, static type => type is { Name: "Box", Kind: AbiDeclarationKind.Class, Visibility: AbiVisibility.Export });
		Assert.Contains(surface.Types, static type => type is { Name: "Pair", Kind: AbiDeclarationKind.Struct, Visibility: AbiVisibility.Export });
		Assert.Contains(surface.Types, static type => type is { Name: "Mode", Kind: AbiDeclarationKind.Enum, Visibility: AbiVisibility.Export });
		Assert.Contains(surface.Types, static type => type is { Name: "Id", Kind: AbiDeclarationKind.Newtype, Visibility: AbiVisibility.Export });
		Assert.Contains(surface.Variables, static variable => variable is { Name: "MAGIC", Kind: AbiDeclarationKind.Constant, Type: "uint", Visibility: AbiVisibility.Export });
		Assert.Contains(surface.Variables, static variable => variable is { Name: "globalCount", Kind: AbiDeclarationKind.Variable, Type: "int", Visibility: AbiVisibility.Export });

		AbiFunction sum = Assert.Single(surface.Functions, static function => function.Name == "sum");
		Assert.Equal("Native_sum", sum.Symbol);
		Assert.Equal("int", sum.ReturnType);
		Assert.Equal(["const int*", "nuint"], sum.ExpandedParameterTypes);
		Assert.Contains(surface.Functions, static function => function.Symbol == "Box_getIFace");
	}

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
			export interface IFace
			{
				int getValue();
			}

			@symbol("Native_add")
			int add(int left, int right) => left + right;

			class Box: IFace
			{
				int value;
				int getValue(): IFace => this.value;
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
	public void Interface_implementation_markers_inherit_callable_newtype_from_slot()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			newtype delegate int CounterReader();

			interface ICounterReader
			{
				int readCount(): CounterReader;
			}

			class Counter: ICounterReader
			{
				int readCount(): ICounterReader => 1;
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		TypeDefinition counter = SemanticCompiler.Type(compilation, "Counter");
		FunctionDefinition readCount = SemanticCompiler.Method(counter, "readCount");
		Assert.Equal("CounterReader", readCount.CallableAscriptionNewtype?.Name);
		Assert.Equal("readCount", readCount.InterfaceImplementationMember?.Name);
	}

	[Fact]
	public void Derived_classes_inherit_base_interface_implementations()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			interface IRefCount
			{
				void retain();
				void release();
			}

			virtual class Component: IRefCount
			{
				void retain(): IRefCount {}
				void release(): IRefCount {}
			}

			sealed class Button: Component
			{
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
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
	public void Declaration_expansion_exposes_generated_declarations_without_lowering_helpers()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileDeclarations("""
			extern void* malloc(nuint size);

			export class Box
			{
				int value;
			}

			void run()
			{
				delegate int(int value) doubleValue = value => value * 2;
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		TypeDefinition box = SemanticCompiler.Type(compilation, "Box");
		Assert.Contains(SemanticCompiler.Descendants<FunctionDefinition>(box), static function => function.Name == "create");
		Assert.DoesNotContain(SemanticCompiler.Descendants<FunctionDefinition>(compilation.Module), static function => function.Symbol.Contains("lambda", System.StringComparison.OrdinalIgnoreCase));
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

	[Fact]
	public void Generated_declarations_record_category_and_reason()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			export interface IFace
			{
				int getValue();
			}

			extern void* malloc(nuint size);

			export class Box: IFace
			{
				int value;
				int getValue(): IFace => this.value;
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		TypeDefinition box = SemanticCompiler.Type(compilation, "Box");
		FunctionDefinition create = SemanticCompiler.Method(box, "create");
		Assert.Equal(GeneratedDeclarationCategory.Lifecycle, create.GeneratedInfo?.Category);
		Assert.Equal("constructor create helper", create.GeneratedInfo?.Reason);
		Assert.Equal(GeneratedDeclarationCategory.Lifecycle, create.Provenance?.Category);
		Assert.Equal("Box", create.Provenance?.SourceSymbol);

		FunctionDefinition accessor = SemanticCompiler.Descendants<FunctionDefinition>(compilation.Module).Single(function => function.Symbol == "Box_getIFace");
		Assert.Equal(GeneratedDeclarationCategory.Interface, accessor.GeneratedInfo?.Category);
		Assert.Equal("interface accessor", accessor.Provenance?.GeneratedReason);

		FieldDefinition interfaceField = SemanticCompiler.Descendants<FieldDefinition>(box).Single(field => field.Symbol == "_vt_IFace");
		Assert.Equal(GeneratedDeclarationCategory.Interface, interfaceField.GeneratedInfo?.Category);
		Assert.Equal(GeneratedDeclarationCategory.Interface, interfaceField.Provenance?.Category);
	}

	[Fact]
	public void Source_capture_defaults_bind_serialize_and_report_metadata()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			export extern void assert(bool condition, string expression = sourceof(condition), string file = caller(sourcefile), uint line = caller(sourceline), string where = caller(qualifiedname));
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition assert = SemanticCompiler.Function(compilation, "assert");
		Assert.IsType<SourceOfExpression>(assert.Parameters[1].DefaultValue);
		Assert.IsType<CallerSourceCaptureExpression>(assert.Parameters[2].DefaultValue);
		Assert.IsType<CallerSourceCaptureExpression>(assert.Parameters[3].DefaultValue);
		Assert.IsType<CallerSourceCaptureExpression>(assert.Parameters[4].DefaultValue);

		using StringWriter apiWriter = new();
		BindableNodeCodeSerializer.Serialize(assert, apiWriter, new BindableNodeCodeSerializerOptions { ApiHeader = true });
		string api = apiWriter.ToString();
		Assert.Contains("string expression = sourceof(condition)", api, System.StringComparison.Ordinal);
		Assert.Contains("string file = caller(sourcefile)", api, System.StringComparison.Ordinal);
		Assert.Contains("uint line = caller(sourceline)", api, System.StringComparison.Ordinal);
		Assert.Contains("string where = caller(qualifiedname)", api, System.StringComparison.Ordinal);

		using JsonDocument metadata = JsonDocument.Parse(MetadataJsonSerializer.Serialize(compilation.Compilation, MetadataVisibility.Export));
		JsonElement parameters = metadata.RootElement.GetProperty("declarations")[0].GetProperty("parameters");
		Assert.Equal("sourceof", parameters[1].GetProperty("defaultExpression").GetProperty("kind").GetString());
		Assert.Equal("condition", parameters[1].GetProperty("defaultExpression").GetProperty("argument").GetString());
		Assert.Equal("caller", parameters[2].GetProperty("defaultExpression").GetProperty("kind").GetString());
		Assert.Equal("sourcefile", parameters[2].GetProperty("defaultExpression").GetProperty("selector").GetString());
		Assert.Equal("sourceline", parameters[3].GetProperty("defaultExpression").GetProperty("selector").GetString());
		Assert.Equal("qualifiedname", parameters[4].GetProperty("defaultExpression").GetProperty("selector").GetString());
	}

	[Fact]
	public void Source_capture_defaults_reject_invalid_default_forms()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileDeclarations("""
			extern void unknownCaller(string value = caller(column));
			extern void tooManyCaller(string value = caller(sourcefile, sourceline));
			extern void expressionSourceOf(int left, int right, string value = sourceof(left + right));
			""");

		Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Contains("caller(...) selector must be one of sourceline, sourcefile, propertyname, functionname, or qualifiedname.", System.StringComparison.Ordinal));
		Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Contains("caller(...) requires exactly one positional argument.", System.StringComparison.Ordinal));
		Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Contains("sourceof(...) requires a single unqualified parameter name.", System.StringComparison.Ordinal));

		SemanticCompilation unknownParameterCompilation = SemanticCompiler.CompileLowered("""
			extern void unknownSourceOf(string value = sourceof(missing));
			""");
		Assert.Contains(unknownParameterCompilation.Diagnostics, static diagnostic => diagnostic.Contains("sourceof(...) argument 'missing' does not name a parameter in this signature.", System.StringComparison.Ordinal));
	}

	[Fact]
	public void Source_capture_defaults_substitute_direct_call_values()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			namespace Tools;

			extern void capture(bool condition, bool optional = true, string expression = sourceof(condition), string omitted = sourceof(optional), uint line = caller(sourceline), string file = caller(sourcefile), string function = caller(functionname), string qualified = caller(qualifiedname));
			extern void captureProperty(string key = caller(propertyname));

			void run()
			{
				capture(1 + 2 == 3);
			}

			class Box
			{
				bool getReady()
				{
					captureProperty();
					return true;
				}
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition run = SemanticCompiler.Function(compilation, "run");
		CallExpression capture = SemanticCompiler.Descendants<CallExpression>(run).Single(static call => call.Arguments.Count == 8);
		Assert.Equal("1 + 2 == 3", Assert.IsType<LiteralExpression>(capture.Arguments[2].Value).Value);
		Assert.Equal("", Assert.IsType<LiteralExpression>(capture.Arguments[3].Value).Value);
		Assert.Equal((GetCallLine(capture) ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.IsType<LiteralExpression>(capture.Arguments[4].Value).Text);
		Assert.Equal("semantic_test.camp", Assert.IsType<LiteralExpression>(capture.Arguments[5].Value).Value);
		Assert.Equal("run", Assert.IsType<LiteralExpression>(capture.Arguments[6].Value).Value);
		Assert.Equal("Tools::run", Assert.IsType<LiteralExpression>(capture.Arguments[7].Value).Value);

		TypeDefinition box = SemanticCompiler.Type(compilation, "Box");
		FunctionDefinition getter = SemanticCompiler.Method(box, "getReady");
		CallExpression propertyCapture = SemanticCompiler.Descendants<CallExpression>(getter).Single(static call => call.Arguments.Count == 1);
		Assert.Equal("Ready", Assert.IsType<LiteralExpression>(propertyCapture.Arguments[0].Value).Value);

		static int? GetCallLine(CallExpression call)
		{
			if (call.SourceSyntax is null)
				return null;
			return call.SourceSyntax.GetType()
				.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
				.Select(property => property.GetValue(call.SourceSyntax))
				.OfType<Token>()
				.Select(static token => token.LineNumber)
				.FirstOrDefault();
		}
	}

	[Fact]
	public void Source_capture_defaults_use_visible_caller_names()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			namespace Tools;

			extern void* malloc(nuint size);
			extern void free(void* pointer);
			extern void capture(string function = caller(functionname), string qualified = caller(qualifiedname));

			void top()
			{
				capture();
			}

			int add(overload int left, int right)
			{
				capture();
				return left + right;
			}

			class Box
			{
				Box()
				{
					capture();
				}

				~Box()
				{
					capture();
				}

				void method()
				{
					capture();
				}

				static void staticMethod()
				{
					capture();
				}
			}

			static void Box.outOfScope()
			{
				capture();
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		HashSet<string> observed = SemanticCompiler.Descendants<CallExpression>(compilation.Module)
			.Where(static call => call.Arguments.Count >= 2
				&& call.Arguments[0].Value is LiteralExpression { Value: string }
				&& call.Arguments[1].Value is LiteralExpression { Value: string })
			.Select(static call => StringArgument(call.Arguments[0]) + "|" + StringArgument(call.Arguments[1]))
			.ToHashSet(StringComparer.Ordinal);

		Assert.Contains("top|Tools::top", observed);
		Assert.Contains("addInt|Tools::addInt", observed);
		Assert.Contains("create|Tools::Box.create", observed);
		Assert.Contains("destroy|Tools::Box.destroy", observed);
		Assert.Contains("method|Tools::Box.method", observed);
		Assert.Contains("staticMethod|Tools::Box.staticMethod", observed);
		Assert.Contains("outOfScope|Tools::Box.outOfScope", observed);

		static string StringArgument(ArgumentExpression argument)
		{
			return Assert.IsType<string>(Assert.IsType<LiteralExpression>(argument.Value).Value);
		}
	}

	[Fact]
	public void Source_capture_propertyname_not_supplied_outside_property_body()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			extern void captureProperty(string key = caller(propertyname));

			void run()
			{
				captureProperty();
			}
			""");

		Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Contains("default caller(propertyname) is not supplied outside a property accessor body.", System.StringComparison.Ordinal));
	}

	[Fact]
	public void Source_capture_defaults_work_through_callable_newtype_invocation()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			namespace Tools;

			newtype fn void CaptureFn(int value, string expression = sourceof(value), string where = caller(qualifiedname));
			newtype delegate void CaptureDelegate(int value, string expression = sourceof(value), string where = caller(qualifiedname));

			void captureImpl(int value, string expression, string where) : CaptureFn
			{
			}

			class Recorder
			{
				void record(int value, string expression, string where) : CaptureDelegate
				{
				}
			}

			void run(Recorder* recorder)
			{
				CaptureFn capture = captureImpl;
				capture(4 + 5);
				CaptureDelegate bound = recorder.record;
				bound(6 + 7);
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition run = SemanticCompiler.Function(compilation, "run");
		HashSet<string> observed = SemanticCompiler.Descendants<CallExpression>(run)
			.Where(static call => call.Arguments.Count >= 3
				&& call.Arguments[^2].Value is LiteralExpression { Value: string }
				&& call.Arguments[^1].Value is LiteralExpression { Value: string })
			.Select(static call => Assert.IsType<string>(Assert.IsType<LiteralExpression>(call.Arguments[^2].Value).Value)
				+ "|"
				+ Assert.IsType<string>(Assert.IsType<LiteralExpression>(call.Arguments[^1].Value).Value))
			.ToHashSet(StringComparer.Ordinal);
		Assert.Contains("4 + 5|Tools::run", observed);
		Assert.Contains("6 + 7|Tools::run", observed);
	}

	[Fact]
	public void Source_capture_defaults_follow_interface_and_concrete_surfaces()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			namespace Tools;

			interface IRecorder
			{
				void record(string where = caller(qualifiedname));
			}

			class Recorder: IRecorder
			{
				void record(string where = "concrete"): IRecorder
				{
				}
			}

			void run(Recorder* concrete, IRecorder* view)
			{
				concrete.record();
				view.record();
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition run = SemanticCompiler.Function(compilation, "run");
		HashSet<string> values = SemanticCompiler.Descendants<CallExpression>(run)
			.Where(static call => call.Arguments.Count >= 1 && call.Arguments[^1].Value is LiteralExpression { Value: string })
			.Select(static call => Assert.IsType<string>(Assert.IsType<LiteralExpression>(call.Arguments[^1].Value).Value))
			.ToHashSet(StringComparer.Ordinal);
		Assert.Contains("concrete", values);
		Assert.Contains("Tools::run", values);
	}

	[Fact]
	public void Source_capture_defaults_from_api_header_capture_consumer_callsite()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered(
			("api.camp", """
				namespace Lib;

				export extern void capture(int value, string expression = sourceof(value), string file = caller(sourcefile), string where = caller(qualifiedname));
				"""),
			("consumer.camp", """
				namespace App;
				using Lib;

				void run()
				{
					capture(10 + 5);
				}
				"""));

		SemanticCompiler.AssertNoDiagnostics(compilation);
		FunctionDefinition run = SemanticCompiler.Function(compilation, "run");
		CallExpression call = SemanticCompiler.Descendants<CallExpression>(run).Single(static call => call.Arguments.Count == 4);
		Assert.Equal("10 + 5", Assert.IsType<LiteralExpression>(call.Arguments[1].Value).Value);
		Assert.Equal("consumer.camp", Assert.IsType<LiteralExpression>(call.Arguments[2].Value).Value);
		Assert.Equal("App::run", Assert.IsType<LiteralExpression>(call.Arguments[3].Value).Value);
	}

	[Fact]
	public void Lowered_generated_locals_record_provenance()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered("""
			void run()
			{
				int[] values = [1, 2, 3];
				foreach (int value in values)
				{
				}
			}
			""");

		SemanticCompiler.AssertNoDiagnostics(compilation);
		Assert.Contains(SemanticCompiler.Descendants<DeclarationStatement>(compilation.Module), static declaration => declaration.Provenance?.GeneratedReason == "generated local");
	}
}
