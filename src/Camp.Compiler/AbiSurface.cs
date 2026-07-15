using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public enum AbiVisibility
{
	Private,
	Internal,
	Export
}

public enum AbiDeclarationKind
{
	Class,
	Struct,
	Interface,
	Newtype,
	Enum,
	Type,
	Function,
	Variable,
	Constant,
	StaticField
}

public sealed record AbiParameter(string Name, string Symbol, string Type);

public sealed record AbiFunction(
	string Name,
	string Symbol,
	AbiVisibility Visibility,
	string ReturnType,
	IReadOnlyList<AbiParameter> Parameters,
	IReadOnlyList<string> ExpandedParameterTypes,
	FunctionDefinition Definition);

public sealed record AbiVariable(
	string Name,
	string Symbol,
	AbiVisibility Visibility,
	AbiDeclarationKind Kind,
	string Type,
	bool IsInline,
	BindableNode Definition);

public sealed record AbiType(
	string Name,
	string Symbol,
	AbiVisibility Visibility,
	AbiDeclarationKind Kind,
	TypeDefinition Definition);

public sealed class AbiSurface
{
	AbiSurface(List<AbiType> types, List<AbiFunction> functions, List<AbiVariable> variables)
	{
		Types = types;
		Functions = functions;
		Variables = variables;
	}

	public IReadOnlyList<AbiType> Types { get; }
	public IReadOnlyList<AbiFunction> Functions { get; }
	public IReadOnlyList<AbiVariable> Variables { get; }
	public IEnumerable<AbiVariable> ExportedVariables => Variables.Where(static variable => variable.Visibility == AbiVisibility.Export);

	public static AbiSurface Build(Compilation compilation)
	{
		ArgumentNullException.ThrowIfNull(compilation);
		Module module = compilation.SharedModule ?? new Module();
		List<Definition> definitions = module.Definitions.ToList();
		Dictionary<FunctionDefinition, TypeDefinition> containingTypes = BuildContainingTypeMap(definitions);
		List<AbiType> types = [];
		List<AbiFunction> functions = [];
		List<AbiVariable> variables = [];

		foreach (TypeDefinition type in definitions.OfType<TypeDefinition>())
		{
			types.Add(new AbiType(type.Name, Symbol(type), Visibility(type), TypeKind(type), type));
			foreach (FunctionDefinition function in TypeFunctions(type))
				functions.Add(BuildFunction(function, containingTypes));
			foreach (FieldDefinition field in TypeFields(type).Where(static field => field.Modifier == FieldModifier.Static))
				variables.Add(BuildField(field));
		}

		foreach (FunctionDefinition function in definitions.OfType<FunctionDefinition>())
			functions.Add(BuildFunction(function, containingTypes));
		foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>())
			variables.Add(BuildVariable(variable));

		return new AbiSurface(types, functions, variables);
	}

	static AbiFunction BuildFunction(FunctionDefinition function, Dictionary<FunctionDefinition, TypeDefinition> containingTypes)
	{
		List<AbiParameter> parameters = function.Parameters
			.Select(static parameter => new AbiParameter(parameter.Name, Symbol(parameter), ParameterType(parameter)))
			.ToList();
		return new AbiFunction(
			function.Name,
			FunctionSymbol(function, containingTypes),
			Visibility(function),
			function.ResolvedType ?? "#ERROR",
			parameters,
			ExpandedFormService.GetExpandedCallableParameterTypes(function.Parameters, TryGetShape),
			function);
	}

	static AbiVariable BuildVariable(VariableDefinition variable)
	{
		return new AbiVariable(variable.Name, Symbol(variable), Visibility(variable), variable.IsInline ? AbiDeclarationKind.Constant : AbiDeclarationKind.Variable, variable.ResolvedType ?? "#ERROR", variable.IsInline, variable);
	}

	static AbiVariable BuildField(FieldDefinition field)
	{
		return new AbiVariable(field.Name, Symbol(field), Visibility(field), AbiDeclarationKind.StaticField, field.ResolvedType ?? "#ERROR", field.IsInline, field);
	}

	static Dictionary<FunctionDefinition, TypeDefinition> BuildContainingTypeMap(IEnumerable<Definition> definitions)
	{
		Dictionary<FunctionDefinition, TypeDefinition> map = [];
		foreach (TypeDefinition type in definitions.OfType<TypeDefinition>())
			foreach (FunctionDefinition function in TypeFunctions(type))
				map[function] = type;
		return map;
	}

	static IEnumerable<FunctionDefinition> TypeFunctions(TypeDefinition type)
	{
		return type switch
		{
			ClassDefinition definition => definition.Functions,
			StructDefinition definition => definition.Functions,
			InterfaceDefinition definition => definition.Functions,
			NewtypeDefinition definition => definition.Functions,
			EnumDefinition definition => definition.Functions,
			_ => []
		};
	}

	static IEnumerable<FieldDefinition> TypeFields(TypeDefinition type)
	{
		return type switch
		{
			ClassDefinition definition => definition.Fields,
			StructDefinition definition => definition.Fields,
			NewtypeDefinition definition => definition.Fields,
			_ => []
		};
	}

	static AbiDeclarationKind TypeKind(TypeDefinition type)
	{
		if (type.GeneratedInfo?.Category == GeneratedDeclarationCategory.Interface
			|| type.Provenance?.Category == GeneratedDeclarationCategory.Interface
			|| type is StructDefinition { SourceInterface: not null })
			return AbiDeclarationKind.Interface;
		return type switch
		{
			ClassDefinition => AbiDeclarationKind.Class,
			StructDefinition => AbiDeclarationKind.Struct,
			InterfaceDefinition => AbiDeclarationKind.Interface,
			NewtypeDefinition => AbiDeclarationKind.Newtype,
			EnumDefinition => AbiDeclarationKind.Enum,
			_ => AbiDeclarationKind.Type
		};
	}

	static string FunctionSymbol(FunctionDefinition function, Dictionary<FunctionDefinition, TypeDefinition> containingTypes)
	{
		if (function.SymbolOverridden && !string.IsNullOrWhiteSpace(function.Symbol))
			return function.Symbol;
		if (!string.IsNullOrWhiteSpace(function.Symbol) && function.Symbol != function.Name)
			return function.Symbol;
		if (containingTypes.TryGetValue(function, out TypeDefinition? type))
			return Symbol(type) + "_" + BindableNodeAnalyzer.GetCallableName(function).TrimStart('~');
		return Symbol(function);
	}

	static string Symbol(Definition definition)
	{
		return string.IsNullOrWhiteSpace(definition.Symbol) ? definition.Name : definition.Symbol;
	}

	static string Symbol(FieldDefinition field)
	{
		return string.IsNullOrWhiteSpace(field.Symbol) ? field.Name : field.Symbol;
	}

	static string Symbol(ParameterDefinition parameter)
	{
		return string.IsNullOrWhiteSpace(parameter.Symbol) ? parameter.Name : parameter.Symbol;
	}

	static AbiVisibility Visibility(Definition definition)
	{
		if (definition.Export is not null)
			return AbiVisibility.Export;
		if (definition.Internal is not null)
			return AbiVisibility.Internal;
		return AbiVisibility.Private;
	}

	static AbiVisibility Visibility(FieldDefinition field)
	{
		if (field.Export is not null)
			return AbiVisibility.Export;
		if (field.Internal is not null)
			return AbiVisibility.Internal;
		return AbiVisibility.Private;
	}

	static string ParameterType(ParameterDefinition parameter)
	{
		string type = parameter.ResolvedType ?? "#ERROR";
		return parameter.Modifier switch
		{
			ParameterModifier.In => "in " + type,
			ParameterModifier.Out => "out " + type,
			ParameterModifier.Thrown => "thrown " + type,
			ParameterModifier.Within => "within " + type,
			ParameterModifier.Upon => "upon " + type,
			_ => type
		};
	}

	static bool TryGetShape(TypeReference? type, string? resolvedType, string baseName, out ParamsComponentShape shape)
	{
		string name = resolvedType ?? "";
		if (name.EndsWith("[]", StringComparison.Ordinal))
		{
			string elementType = name[..^2];
			shape = new ParamsComponentShape(ParamsComponentShapeKind.Array, name, [
				new ParamsComponent("elements", elementType + "*", baseName + "_elements", null, ParamsComponentShapeKind.Array),
				new ParamsComponent("length", "nuint", baseName + "_length", null, ParamsComponentShapeKind.Array)
			]);
			return true;
		}
		shape = null!;
		return false;
	}
}
