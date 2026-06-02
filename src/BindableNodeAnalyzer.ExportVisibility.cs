using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void AnalyzeExportVisibility(Module module)
	{
		foreach (Definition definition in module.Definitions)
			AnalyzeExportVisibility(definition, containingTypeExported: false);
	}

	void AnalyzeExportVisibility(Definition definition, bool containingTypeExported)
	{
		bool exported = definition.Export is not null || containingTypeExported;
		if (!exported)
			return;

		switch (definition)
		{
			case TypeDefinition typeDefinition:
				foreach (TypeReference consumedType in GetVisibleTypes(typeDefinition))
					CheckExportedTypeUse(typeDefinition, consumedType);
				AnalyzeExportedMembers(typeDefinition);
				break;

			case VariableDefinition variable:
				CheckExportedTypeUse(variable, variable.Type);
				break;

			case FunctionDefinition function:
				foreach (TypeReference consumedType in GetVisibleTypes(function))
					CheckExportedTypeUse(function, consumedType);
				break;
		}
	}

	void AnalyzeExportedMembers(TypeDefinition typeDefinition)
	{
		switch (typeDefinition)
		{
			case ClassDefinition classDefinition:
				foreach (FunctionDefinition function in classDefinition.Functions)
					AnalyzeExportVisibility(function, containingTypeExported: false);
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					AnalyzeExportVisibility(field, containingTypeExported: typeDefinition.Export is not null);
				foreach (FunctionDefinition function in structDefinition.Functions)
					AnalyzeExportVisibility(function, containingTypeExported: false);
				break;

			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					AnalyzeExportVisibility(function, containingTypeExported: typeDefinition.Export is not null);
				break;
		}
	}

	IEnumerable<TypeReference> GetVisibleTypes(TypeDefinition typeDefinition)
	{
		switch (typeDefinition)
		{
			case ClassDefinition classDefinition:
				foreach (TypeReference type in classDefinition.BaseTypes)
					yield return type;
				break;

			case StructDefinition structDefinition:
				foreach (TypeReference type in structDefinition.BaseTypes)
					yield return type;
				foreach (FieldDefinition field in structDefinition.Fields)
				{
					if (field.Type is not null)
						yield return field.Type;
				}
				break;

			case InterfaceDefinition interfaceDefinition:
				foreach (TypeReference type in interfaceDefinition.BaseTypes)
					yield return type;
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
				{
					foreach (TypeReference type in GetVisibleTypes(function))
						yield return type;
				}
				break;

			case EnumDefinition enumDefinition:
				if (enumDefinition.UnderlyingType is not null)
					yield return enumDefinition.UnderlyingType;
				break;

			case NewtypeDefinition newtypeDefinition:
				if (newtypeDefinition.UnderlyingType is not null)
					yield return newtypeDefinition.UnderlyingType;
				foreach (ParameterDefinition parameter in newtypeDefinition.Parameters)
				{
					if (parameter.Type is not null)
						yield return parameter.Type;
				}
				break;

			case ParamsDefinition paramsDefinition:
				if (paramsDefinition.UnderlyingType is not null)
					yield return paramsDefinition.UnderlyingType;
				foreach (ParameterDefinition parameter in paramsDefinition.Components)
				{
					if (parameter.Type is not null)
						yield return parameter.Type;
				}
				break;
		}
	}

	static IEnumerable<TypeReference> GetVisibleTypes(FunctionDefinition function)
	{
		if (function.ReturnType is not null)
			yield return function.ReturnType;

		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Type is not null)
				yield return parameter.Type;

			if (parameter is VTableOfParameterDefinition { InterfaceType: not null } vtableOf)
				yield return vtableOf.InterfaceType;
		}
	}

	void CheckExportedTypeUse(Definition exportedDeclaration, TypeReference? type)
	{
		if (type is null)
			return;

		foreach (NamedTypeReference named in GetNamedTypes(type))
		{
			if (!TryGetNamedTypeDefinition(named, out TypeDefinition? definition))
				continue;
			if (definition is null)
				continue;

			if (definition is { Export: null })
				Report(GetRange(named.SourceSyntax), $"Exported declaration '{exportedDeclaration.Name}' exposes non-exported type '{definition.Name}'.");
		}
	}

	static IEnumerable<NamedTypeReference> GetNamedTypes(TypeReference type)
	{
		switch (type)
		{
			case NamedTypeReference named:
				yield return named;
				foreach (TypeReference argument in named.TypeArguments)
				{
					foreach (NamedTypeReference child in GetNamedTypes(argument))
						yield return child;
				}
				break;

			case GenericTypeReference generic:
				if (generic.Type is not null)
				{
					foreach (NamedTypeReference child in GetNamedTypes(generic.Type))
						yield return child;
				}
				foreach (TypeReference argument in generic.TypeArguments)
				{
					foreach (NamedTypeReference child in GetNamedTypes(argument))
						yield return child;
				}
				break;

			case AttributedTypeReference { Type: not null } attributed:
				foreach (NamedTypeReference child in GetNamedTypes(attributed.Type))
					yield return child;
				break;

			case ArrayTypeReference { ElementType: not null } array:
				foreach (NamedTypeReference child in GetNamedTypes(array.ElementType))
					yield return child;
				break;

			case OptionalTypeReference { ElementType: not null } optional:
				foreach (NamedTypeReference child in GetNamedTypes(optional.ElementType))
					yield return child;
				break;

			case PointerTypeReference { ElementType: not null } pointer:
				foreach (NamedTypeReference child in GetNamedTypes(pointer.ElementType))
					yield return child;
				break;

			case ConstTypeReference { Type: not null } constType:
				foreach (NamedTypeReference child in GetNamedTypes(constType.Type))
					yield return child;
				break;

			case VolatileTypeReference { Type: not null } volatileType:
				foreach (NamedTypeReference child in GetNamedTypes(volatileType.Type))
					yield return child;
				break;

			case EscapedTypeReference { Type: not null } escapedType:
				foreach (NamedTypeReference child in GetNamedTypes(escapedType.Type))
					yield return child;
				break;

			case ScopedTypeReference { Type: not null } scopedType:
				foreach (NamedTypeReference child in GetNamedTypes(scopedType.Type))
					yield return child;
				break;

			case UnscopedTypeReference { Type: not null } unscopedType:
				foreach (NamedTypeReference child in GetNamedTypes(unscopedType.Type))
					yield return child;
				break;

			case CallableTypeReference callable:
				if (callable.ReturnType is not null)
				{
					foreach (NamedTypeReference child in GetNamedTypes(callable.ReturnType))
						yield return child;
				}
				foreach (ParameterDefinition parameter in callable.Parameters)
				{
					if (parameter.Type is null)
						continue;
					foreach (NamedTypeReference child in GetNamedTypes(parameter.Type))
						yield return child;
				}
				break;

			case IterTypeReference { ElementType: not null } iter:
				foreach (NamedTypeReference child in GetNamedTypes(iter.ElementType))
					yield return child;
				break;

			case GroupedParamsTypeReference { StructType: not null } grouped:
				foreach (NamedTypeReference child in GetNamedTypes(grouped.StructType))
					yield return child;
				break;

			case MaterializedStructTypeReference { ParamsType: not null } materialized:
				foreach (NamedTypeReference child in GetNamedTypes(materialized.ParamsType))
					yield return child;
				break;

			case ThrownTypeReference { Type: not null } thrown:
				foreach (NamedTypeReference child in GetNamedTypes(thrown.Type))
					yield return child;
				break;
		}
	}
}
