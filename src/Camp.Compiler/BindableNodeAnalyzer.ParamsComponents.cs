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
		Delegate,
		Iter
	}

	sealed record ParamsComponentShape(ParamsComponentShapeKind Kind, string TypeName, List<ParamsComponent> Components);

	sealed record ParamsComponent(
		string Name,
		string Type,
		string ExpandedName,
		ParameterDefinition? SourceParameter,
		ParamsComponentShapeKind SourceKind);

	readonly record struct ParamsNamePart(string Name, bool PreferNoSuffix);

	sealed record PendingParamsComponent(
		string Name,
		string Type,
		List<ParamsNamePart> NameParts,
		ParameterDefinition? SourceParameter,
		ParamsComponentShapeKind SourceKind);

	sealed record ParamsExpansionComponent(string SourceName, string Name, string Type, BindableNode Node);

	readonly Dictionary<BindableNode, List<ParamsExpansionComponent>> paramsExpansions = [];
	readonly Dictionary<FunctionDefinition, ParamsComponentShape> expandedReturnShapes = [];
	readonly HashSet<CallExpression> preparedExpandedReturnCalls = [];

	List<string> GetPotentialParamsComponentNames(TypeReference? type, string? resolvedType, string sourceName)
	{
		List<string> names = [];
		if (string.IsNullOrWhiteSpace(sourceName))
			return names;
		if (!TryGetParamsComponentShape(type, resolvedType, sourceName, out ParamsComponentShape shape))
			return names;

		foreach (ParamsComponent component in shape.Components)
			names.Add(component.ExpandedName);
		return names;
	}

	bool TryGetParamsComponentShape(TypeReference? type, string baseName, out ParamsComponentShape shape)
	{
		return TryGetParamsComponentShape(type, type?.ResolvedType, baseName, out shape);
	}

	bool TryGetParamsComponentShape(TypeReference? type, string? resolvedType, string baseName, out ParamsComponentShape shape)
	{
		shape = new ParamsComponentShape(ParamsComponentShapeKind.Structural, "", []);
		if (!TryBuildPendingParamsComponents(type, resolvedType, [new ParamsNamePart(baseName, false)], out List<PendingParamsComponent> pending, out ParamsComponentShapeKind kind, out string typeName))
			return false;

		shape = new ParamsComponentShape(kind, typeName, FinalizeParamsComponents(pending));
		shape = ApplyParamsValueQualifiers(shape, type, resolvedType);
		return shape.Components.Count > 0;
	}

	bool TryBuildPendingParamsComponents(
		TypeReference? type,
		string? resolvedType,
		List<ParamsNamePart> prefix,
		out List<PendingParamsComponent> components,
		out ParamsComponentShapeKind kind,
		out string typeName)
	{
		components = [];
		kind = ParamsComponentShapeKind.Structural;
		typeName = resolvedType ?? "";

		if (type is MaterializedStructTypeReference || resolvedType?.StartsWith("struct(", StringComparison.Ordinal) == true)
			return false;

		type = type is null ? null : UnwrapTypeDeclarators(type);

		switch (type)
		{
			case PointerTypeReference { ElementType: not null } pointer:
				return TryBuildPendingPointerComponents(pointer.ElementType, pointer.ElementType.ResolvedType, prefix, out components, out kind, out typeName);

			case ArrayTypeReference { ElementType: not null } array:
				kind = ParamsComponentShapeKind.Array;
				typeName = type.ResolvedType ?? resolvedType ?? ErrorType;
				AddArrayPendingComponents(array.ElementType, array.ElementType.ResolvedType, prefix, components);
				return true;

			case OptionalTypeReference { ElementType: not null } optional:
				kind = ParamsComponentShapeKind.Optional;
				typeName = type.ResolvedType ?? resolvedType ?? ErrorType;
				AddOptionalPendingComponents(optional.ElementType, optional.ElementType.ResolvedType, prefix, components);
				return true;

			case CallableTypeReference { Kind: CallableKind.Delegate } callable:
				kind = ParamsComponentShapeKind.Delegate;
				typeName = type.ResolvedType ?? resolvedType ?? ErrorType;
				AddDelegatePendingComponents(callable.ReturnType?.ResolvedType ?? ErrorType, GetExpandedDeclaredCallableParameterTypes(callable.Parameters), prefix, components, callable.TargetSpec, callable.CallSpec);
				return true;

			case IterTypeReference iter:
				kind = ParamsComponentShapeKind.Iter;
				typeName = type.ResolvedType ?? resolvedType ?? ErrorType;
				AddIteratorPendingComponents(GetIteratorProtocolParameterTypes(iter), prefix, components);
				return true;
		}

		if (!string.IsNullOrWhiteSpace(resolvedType))
		{
			string structuralResolvedType = StripLifetimeQualifiers(resolvedType);
			if (structuralResolvedType != resolvedType
				&& TryBuildPendingParamsComponents(null, structuralResolvedType, prefix, out components, out kind, out typeName))
			{
				typeName = resolvedType;
				return true;
			}

			if (typeDefinitions.TryGetValue(BaseTypeName(resolvedType), out TypeDefinition? nominalDefinition)
				&& nominalDefinition is NewtypeDefinition { UnderlyingType: not null } newtypeDefinition
				&& TryBuildPendingParamsComponents(newtypeDefinition.UnderlyingType, newtypeDefinition.UnderlyingType.ResolvedType, prefix, out components, out kind, out _)
				&& kind is ParamsComponentShapeKind.Delegate or ParamsComponentShapeKind.Iter)
			{
				if (kind == ParamsComponentShapeKind.Delegate && newtypeDefinition.UnderlyingType is CallableTypeReference delegateType)
				{
					components = [];
					AddDelegatePendingComponents(delegateType.ReturnType?.ResolvedType ?? ErrorType, GetExpandedDeclaredCallableParameterTypes(newtypeDefinition.Parameters), prefix, components, delegateType.TargetSpec, delegateType.CallSpec);
				}
				else if (kind == ParamsComponentShapeKind.Iter && newtypeDefinition.UnderlyingType is IterTypeReference iterType)
				{
					components = [];
					AddIteratorPendingComponents(GetIteratorProtocolParameterTypes(iterType), GetExpandedCallableParameterTypes(newtypeDefinition.Parameters), prefix, components);
				}
				typeName = resolvedType;
				return true;
			}

			if (TryParseTypeShape(resolvedType, out TypeShape typeShape))
			{
				if (typeShape.Kind == TypeShapeKind.Array && typeShape.Element is TypeShape arrayElement)
				{
					kind = ParamsComponentShapeKind.Array;
					typeName = resolvedType;
					AddArrayPendingComponents(null, TypeShapeParser.Format(arrayElement), prefix, components);
					return true;
				}

				if (typeShape.Kind == TypeShapeKind.Pointer && typeShape.Element is TypeShape pointerElement
					&& TryBuildPendingPointerComponents(pointerElement, prefix, out components, out kind, out typeName))
				{
					typeName = resolvedType;
					return true;
				}

				if (typeShape.Kind == TypeShapeKind.Optional && typeShape.Element is TypeShape optionalElement)
				{
					kind = ParamsComponentShapeKind.Optional;
					typeName = resolvedType;
					AddOptionalPendingComponents(null, TypeShapeParser.Format(optionalElement), prefix, components);
					return true;
				}
			}

			if (TryGetCallableShape(resolvedType, out CallableShape callable) && callable.Kind == "delegate")
			{
				kind = ParamsComponentShapeKind.Delegate;
				typeName = resolvedType;
				AddDelegatePendingComponents(callable.ReturnType, GetExpandedCallableParameterTypes(callable.Parameters), prefix, components, callable.Spec, callable.CallSpec);
				return true;
			}

			if (TryGetIteratorProtocolParameterTypes(resolvedType, out List<string>? parameterTypes) && parameterTypes is not null)
			{
				kind = ParamsComponentShapeKind.Iter;
				typeName = resolvedType;
				AddIteratorPendingComponents(parameterTypes, prefix, components);
				return true;
			}
		}

		return false;
	}

	bool TryBuildPendingParamsDefinitionComponents(
		ParamsDefinition definition,
		List<ParamsNamePart> prefix,
		out List<PendingParamsComponent> components,
		out ParamsComponentShapeKind kind,
		out string typeName)
	{
		if (definition.Components.Count == 0 && definition.UnderlyingType is not null)
			return TryBuildPendingParamsComponents(definition.UnderlyingType, definition.UnderlyingType.ResolvedType, prefix, out components, out kind, out typeName);

		components = [];
		kind = ParamsComponentShapeKind.Nominal;
		typeName = definition.ResolvedType ?? definition.Name;
		foreach (ParameterDefinition component in definition.Components)
		{
			string componentName = string.IsNullOrWhiteSpace(component.Name)
				? components.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
				: component.Name;
			List<ParamsNamePart> componentPrefix = [.. prefix, new ParamsNamePart(componentName, HasAttribute(component.Attributes, "@nosuffix"))];
			if (TryBuildPendingParamsComponents(component.Type, component.ResolvedType, componentPrefix, out List<PendingParamsComponent> nested, out _, out _))
			{
				components.AddRange(nested);
				continue;
			}

			components.Add(new PendingParamsComponent(
				componentName,
				component.ResolvedType ?? ErrorType,
				componentPrefix,
				component,
				ParamsComponentShapeKind.Nominal));
		}
		return components.Count > 0;
	}

	void AddArrayPendingComponents(TypeReference? elementType, string? elementResolvedType, List<ParamsNamePart> prefix, List<PendingParamsComponent> components)
	{
		List<ParamsNamePart> elementPrefix = [.. prefix, new ParamsNamePart("elements", true)];
		string elementPointerType = AddPointer(ResolvedParamsComponentType(elementType, elementResolvedType));
		components.Add(new PendingParamsComponent("elements", elementPointerType, elementPrefix, null, ParamsComponentShapeKind.Array));

		components.Add(new PendingParamsComponent("length", "nuint", [.. prefix, new ParamsNamePart("length", false)], null, ParamsComponentShapeKind.Array));
	}

	static string ResolvedParamsComponentType(TypeReference? type, string? resolvedType)
	{
		if (!string.IsNullOrWhiteSpace(resolvedType) && resolvedType is not UnresolvedType and not ErrorType)
			return resolvedType;
		return type switch
		{
			GenericParameterTypeReference generic => generic.Name,
			NamedTypeReference named => named.Name,
			TypeDefinitionReference definition => definition.Name,
			PrimitiveTypeReference primitive => GetPrimitiveTypeName(primitive.Type),
			_ => type?.ResolvedType is { } nested && nested is not UnresolvedType and not ErrorType ? nested : ErrorType
		};
	}

	bool TryBuildPendingPointerComponents(
		TypeReference? elementType,
		string? elementResolvedType,
		List<ParamsNamePart> prefix,
		out List<PendingParamsComponent> components,
		out ParamsComponentShapeKind kind,
		out string typeName)
	{
		typeName = AddPointer(elementResolvedType ?? elementType?.ResolvedType ?? ErrorType);
		if (!TryBuildPendingParamsComponents(elementType, elementResolvedType, prefix, out components, out kind, out _))
		{
			components = [];
			kind = ParamsComponentShapeKind.Structural;
			return false;
		}

		bool elementIsConst = HasConstValueQualifier(elementType, elementResolvedType);
		for (int i = 0; i < components.Count; i++)
		{
			if (elementIsConst)
				components[i] = components[i] with { Type = AddConstToReceiverInstance(components[i].Type) };
			string componentType = AddPointer(components[i].Type);
			components[i] = components[i] with { Type = componentType };
		}
		return true;
	}

	bool TryBuildPendingPointerComponents(
		TypeShape element,
		List<ParamsNamePart> prefix,
		out List<PendingParamsComponent> components,
		out ParamsComponentShapeKind kind,
		out string typeName)
	{
		string elementType = TypeShapeParser.Format(element);
		typeName = AddPointer(elementType);
		if (!TryBuildPendingParamsComponents(null, elementType, prefix, out components, out kind, out _))
		{
			components = [];
			kind = ParamsComponentShapeKind.Structural;
			return false;
		}

		bool elementIsConst = element.Qualifiers.IsConst;
		for (int i = 0; i < components.Count; i++)
		{
			if (elementIsConst)
				components[i] = components[i] with { Type = AddConstToReceiverInstance(components[i].Type) };
			string componentType = AddPointer(components[i].Type);
			components[i] = components[i] with { Type = componentType };
		}
		return true;
	}

	static bool HasConstValueQualifier(TypeReference? type, string? resolvedType)
	{
		if (type is ConstTypeReference or ConstOfTypeReference)
			return true;
		if (!string.IsNullOrWhiteSpace(resolvedType) && new TypeShapeParser(resolvedType).TryParse(out TypeShape shape))
			return shape.Qualifiers.IsConst;
		return false;
	}

	void AddOptionalPendingComponents(TypeReference? valueType, string? valueResolvedType, List<ParamsNamePart> prefix, List<PendingParamsComponent> components)
	{
		List<ParamsNamePart> valuePrefix = [.. prefix, new ParamsNamePart("value", true)];
		if (TryBuildPendingParamsComponents(valueType, valueResolvedType, valuePrefix, out List<PendingParamsComponent> valueComponents, out _, out _))
			components.AddRange(valueComponents);
		else
			components.Add(new PendingParamsComponent("value", valueResolvedType ?? valueType?.ResolvedType ?? ErrorType, valuePrefix, null, ParamsComponentShapeKind.Optional));

		components.Add(new PendingParamsComponent("specified", "bool", [.. prefix, new ParamsNamePart("specified", false)], null, ParamsComponentShapeKind.Optional));
	}

	void AddDelegatePendingComponents(string returnType, List<string> parameterTypes, List<ParamsNamePart> prefix, List<PendingParamsComponent> components, string? targetSpec = null, string? callSpec = null)
	{
		string contextType = "void*";
		string callReturnType = returnType;
		List<string> callParameterTypes = [contextType, .. parameterTypes];
		if (TryGetParamsComponentShape(null, returnType, "result", out ParamsComponentShape returnShape) && returnShape.Components.Count > 1)
		{
			callReturnType = returnShape.Components[0].Type;
			for (int i = 1; i < returnShape.Components.Count; i++)
				callParameterTypes.Add("out " + returnShape.Components[i].Type);
		}
		string callType = BuildCallableType("fn", callReturnType, callParameterTypes, targetSpec, callSpec);
		components.Add(new PendingParamsComponent("call", callType, [.. prefix, new ParamsNamePart("call", true)], null, ParamsComponentShapeKind.Delegate));
		components.Add(new PendingParamsComponent("context", contextType, [.. prefix, new ParamsNamePart("context", false)], null, ParamsComponentShapeKind.Delegate));
	}

	void AddIteratorPendingComponents(List<string> protocolParameterTypes, List<ParamsNamePart> prefix, List<PendingParamsComponent> components)
	{
		AddIteratorPendingComponents(protocolParameterTypes, [], prefix, components);
	}

	void AddIteratorPendingComponents(List<string> protocolParameterTypes, List<string> inputTypes, List<ParamsNamePart> prefix, List<PendingParamsComponent> components)
	{
		string contextType = "void*";
		List<string> parameterTypes = [contextType];
		parameterTypes.AddRange(protocolParameterTypes);
		parameterTypes.AddRange(inputTypes);
		string callType = BuildCallableType("fn", "bool", parameterTypes);
		components.Add(new PendingParamsComponent("call", callType, [.. prefix, new ParamsNamePart("call", true)], null, ParamsComponentShapeKind.Iter));
		components.Add(new PendingParamsComponent("context", contextType, [.. prefix, new ParamsNamePart("context", false)], null, ParamsComponentShapeKind.Iter));
	}

	List<string> GetIteratorProtocolCurrentTypes(IterTypeReference iter)
	{
		List<string> types = [];
		if (iter.Parameters.Count == 0)
		{
			types.Add(iter.ElementType?.ResolvedType ?? FormatTypeReference(iter.ElementType));
			return types;
		}

		foreach (ParameterDefinition parameter in iter.Parameters)
			if (parameter.Modifier != ParameterModifier.Thrown)
				types.Add(parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? FormatTypeReference(parameter.Type));
		return types;
	}

	List<string> GetIteratorProtocolParameterTypes(IterTypeReference iter)
	{
		List<string> types = [];
		foreach (string currentType in GetIteratorProtocolCurrentTypes(iter))
			AddIteratorProtocolCurrentParameterTypes(currentType, types);
		foreach (ParameterDefinition parameter in iter.Parameters)
			if (parameter.Modifier == ParameterModifier.Thrown)
				types.Add("thrown " + (parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? FormatTypeReference(parameter.Type)));
		return types;
	}

	List<string> GetExpandedCallableParameterTypes(List<ParameterDefinition> parameters)
	{
		List<string> types = [];
		foreach (ParameterDefinition parameter in GetCallableParameters(parameters))
		{
			if (TryGetParamsComponentShape(parameter.Type, parameter.ResolvedType, parameter.Name, out ParamsComponentShape shape))
			{
				foreach (ParamsComponent component in shape.Components)
					types.Add(component.Type);
			}
			else
			{
				string parameterType = parameter.ResolvedType ?? ErrorType;
				types.Add(parameter.Modifier switch
				{
					ParameterModifier.In => "in " + parameterType,
					ParameterModifier.Out => "out " + parameterType,
					ParameterModifier.Thrown => "thrown " + parameterType,
					ParameterModifier.Within => "within " + parameterType,
					_ => parameterType
				});
			}
		}
		return types;
	}

	List<string> GetExpandedDeclaredCallableParameterTypes(List<ParameterDefinition> parameters)
	{
		List<string> types = [];
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter is ThisParameterDefinition or SizeOfParameterDefinition or NameOfParameterDefinition or VTableOfParameterDefinition)
				continue;
			if (TryGetParamsComponentShape(parameter.Type, parameter.ResolvedType, parameter.Name, out ParamsComponentShape shape))
			{
				foreach (ParamsComponent component in shape.Components)
					types.Add(component.Type);
			}
			else
			{
				string parameterType = parameter.ResolvedType ?? ErrorType;
				types.Add(parameter.Modifier switch
				{
					ParameterModifier.In => "in " + parameterType,
					ParameterModifier.Out => "out " + parameterType,
					ParameterModifier.Thrown => "thrown " + parameterType,
					ParameterModifier.Within => "within " + parameterType,
					_ => parameterType
				});
			}
		}
		return types;
	}

	List<string> GetExpandedCallableParameterTypes(List<string> parameterTypes)
	{
		List<string> types = [];
		foreach (string parameterType in parameterTypes)
		{
			if (TryGetParamsComponentShape(null, parameterType, "arg", out ParamsComponentShape shape))
			{
				foreach (ParamsComponent component in shape.Components)
					types.Add(component.Type);
			}
			else
			{
				types.Add(parameterType);
			}
		}
		return types;
	}

	List<ParamsComponent> FinalizeParamsComponents(List<PendingParamsComponent> pending)
	{
		List<ParamsComponent> components = [];
		HashSet<string> usedNames = new(StringComparer.Ordinal);
		for (int i = pending.Count - 1; i >= 0; i--)
		{
			PendingParamsComponent pendingComponent = pending[i];
			string expandedName = ChooseParamsComponentName(pendingComponent.NameParts, usedNames);
			usedNames.Add(expandedName);
			components.Insert(0, new ParamsComponent(
				pendingComponent.Name,
				pendingComponent.Type,
				expandedName,
				pendingComponent.SourceParameter,
				pendingComponent.SourceKind));
		}
		return components;
	}

	string ChooseParamsComponentName(List<ParamsNamePart> parts, HashSet<string> usedNames)
	{
		bool[] included = new bool[parts.Count];
		Array.Fill(included, true);
		for (int i = parts.Count - 1; i >= 1; i--)
		{
			if (!parts[i].PreferNoSuffix)
				continue;

			included[i] = false;
			string candidate = JoinParamsComponentName(parts, included);
			if (usedNames.Contains(candidate))
				included[i] = true;
		}

		return JoinParamsComponentName(parts, included);
	}

	static string JoinParamsComponentName(List<ParamsNamePart> parts, bool[] included)
	{
		List<string> names = [];
		for (int i = 0; i < parts.Count; i++)
		{
			if (included[i] && !string.IsNullOrWhiteSpace(parts[i].Name))
				names.Add(parts[i].Name);
		}
		return string.Join("_", names);
	}

	TypeReference? PointerToElementType(TypeReference? elementType, string? elementResolvedType)
	{
		if (elementType is not null)
			return PointerTo(CloneType(elementType)!);

		if (string.IsNullOrWhiteSpace(elementResolvedType))
			return null;

		return new PointerTypeReference
		{
			ElementType = new NamedTypeReference { Name = elementResolvedType, ResolvedType = elementResolvedType },
			ResolvedType = AddPointer(elementResolvedType)
		};
	}

	static string AddPointer(string type)
	{
		return type + "*";
	}

	ParamsComponentShape ApplyParamsValueQualifiers(ParamsComponentShape shape, TypeReference? type, string? resolvedType)
	{
		if (!IsConstType(type) && !IsConstQualified(resolvedType))
			return shape;

		List<ParamsComponent> components = [];
		foreach (ParamsComponent component in shape.Components)
			components.Add(component with { Type = AddTopLevelConstToType(component.Type) });
		return shape with { Components = components };
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

		if (!TryGetParamsComponentShape(TypeReferenceFor(definition), definition.ResolvedType ?? definition.Name, "value", out ParamsComponentShape shape))
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
