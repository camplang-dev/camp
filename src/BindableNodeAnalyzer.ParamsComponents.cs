using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	enum ParamsComponentShapeKind
	{
		Nominal,
		Structural,
		Array,
		Optional,
		Delegate
	}

	sealed record ParamsComponentShape(ParamsComponentShapeKind Kind, string TypeName, List<ParamsComponent> Components);

	sealed record ParamsComponent(
		string Name,
		string Type,
		string ExpandedName,
		ParameterDefinition? SourceParameter,
		ParamsComponentShapeKind SourceKind);

	bool TryGetParamsComponentShape(TypeReference? type, string baseName, out ParamsComponentShape shape)
	{
		shape = new ParamsComponentShape(ParamsComponentShapeKind.Structural, "", []);
		if (type is null || type is MaterializedStructTypeReference)
			return false;

		type = UnwrapTypeDeclarators(type);
		switch (type)
		{
			case TypeDefinitionReference { Definition: ParamsDefinition definition }:
				return TryGetParamsDefinitionComponentShape(definition, baseName, out shape);

			case NamedTypeReference named when typeDefinitions.TryGetValue(named.Name, out TypeDefinition? definition) && definition is ParamsDefinition paramsDefinition:
				return TryGetParamsDefinitionComponentShape(paramsDefinition, baseName, out shape);

			case ArrayTypeReference { ElementType: not null } array:
			{
				string elementType = array.ElementType.ResolvedType ?? ErrorType;
				shape = new ParamsComponentShape(ParamsComponentShapeKind.Array, type.ResolvedType ?? $"{elementType}[]",
				[
					new ParamsComponent("elements", $"{elementType}*", baseName + "_elements", null, ParamsComponentShapeKind.Array),
					new ParamsComponent("length", "nuint", baseName + "_length", null, ParamsComponentShapeKind.Array)
				]);
				return true;
			}

			case OptionalTypeReference { ElementType: not null } optional:
			{
				string valueType = optional.ElementType.ResolvedType ?? ErrorType;
				shape = new ParamsComponentShape(ParamsComponentShapeKind.Optional, type.ResolvedType ?? $"{valueType}?",
				[
					new ParamsComponent("value", valueType, baseName + "_value", null, ParamsComponentShapeKind.Optional),
					new ParamsComponent("specified", "bool", baseName + "_specified", null, ParamsComponentShapeKind.Optional)
				]);
				return true;
			}

			case CallableTypeReference { Kind: CallableKind.Delegate } callable:
			{
				string returnType = callable.ReturnType?.ResolvedType ?? ErrorType;
				List<string> parameterTypes = [];
				foreach (ParameterDefinition parameter in GetCallableParameters(callable.Parameters))
					parameterTypes.Add(parameter.ResolvedType ?? ErrorType);

				string contextType = "escaped void*";
				string callType = BuildCallableType("fn", returnType, [contextType, .. parameterTypes]);
				shape = new ParamsComponentShape(ParamsComponentShapeKind.Delegate, type.ResolvedType ?? BuildCallableType("delegate", returnType, parameterTypes),
				[
					new ParamsComponent("call", callType, baseName + "_call", null, ParamsComponentShapeKind.Delegate),
					new ParamsComponent("context", contextType, baseName + "_context", null, ParamsComponentShapeKind.Delegate)
				]);
				return true;
			}

			case GroupedParamsTypeReference { StructType: not null } grouped:
				return TryGetParamsComponentShape(grouped.StructType, baseName, out shape);

			default:
				return false;
		}
	}

	bool TryGetParamsDefinitionComponentShape(ParamsDefinition definition, string baseName, out ParamsComponentShape shape)
	{
		if (definition.Components.Count == 0 && definition.UnderlyingType is not null)
			return TryGetParamsComponentShape(definition.UnderlyingType, baseName, out shape);

		List<ParamsComponent> components = [];
		foreach (ParameterDefinition component in definition.Components)
		{
			string componentName = string.IsNullOrWhiteSpace(component.Name)
				? components.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
				: component.Name;
			components.Add(new ParamsComponent(
				componentName,
				component.ResolvedType ?? ErrorType,
				GetExpandedParamsComponentName(baseName, component),
				component,
				ParamsComponentShapeKind.Nominal));
		}

		shape = new ParamsComponentShape(ParamsComponentShapeKind.Nominal, definition.ResolvedType ?? definition.Name, components);
		return components.Count > 0;
	}

	string GetExpandedParamsComponentName(string baseName, ParameterDefinition component)
	{
		bool noSuffix = HasAttribute(component.Attributes, "@nosuffix");
		string componentName = component.Name;

		if (noSuffix)
			return baseName;
		if (string.IsNullOrWhiteSpace(baseName))
			return componentName;
		if (string.IsNullOrWhiteSpace(componentName))
			return baseName;

		return baseName + "_" + componentName;
	}

	void ValidateParamsComponentShape(ParamsDefinition definition)
	{
		int noSuffixCount = 0;
		HashSet<string> componentNames = new(StringComparer.Ordinal);
		HashSet<string> expandedNames = new(StringComparer.Ordinal);
		foreach (ParameterDefinition component in definition.Components)
		{
			foreach (AttributeConstructor attribute in component.Attributes)
				AnalyzeAttribute(attribute);

			bool noSuffix = HasAttribute(component.Attributes, "@nosuffix");
			if (noSuffix)
				noSuffixCount++;

			if (!string.IsNullOrWhiteSpace(component.Name) && !componentNames.Add(component.Name))
				Report(GetNameRange(component), $"Duplicate params component name '{component.Name}'.");
		}

		if (noSuffixCount > 1)
			Report(GetNameRange(definition), $"Params declaration '{definition.Name}' may have at most one @nosuffix component.");

		if (!TryGetParamsDefinitionComponentShape(definition, "value", out ParamsComponentShape shape))
			return;

		foreach (ParamsComponent component in shape.Components)
		{
			if (string.IsNullOrWhiteSpace(component.ExpandedName))
				continue;
			if (!expandedNames.Add(component.ExpandedName))
				Report(component.SourceParameter is null ? GetNameRange(definition) : GetNameRange(component.SourceParameter), $"Params declaration '{definition.Name}' produces duplicate expanded component name '{component.ExpandedName}'.");
		}
	}

	static bool HasAttribute(List<AttributeConstructor> attributes, string name)
	{
		foreach (AttributeConstructor attribute in attributes)
		{
			if (AttributeNameEquals(attribute.Name, name))
				return true;
		}
		return false;
	}

	static bool AttributeNameEquals(string actual, string expected)
	{
		return actual == expected || actual.TrimStart('@') == expected.TrimStart('@');
	}
}
