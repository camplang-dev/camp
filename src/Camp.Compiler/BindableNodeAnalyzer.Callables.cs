using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	static string BuildCallableType(string kind, string returnType, List<string> parameters, string? targetSpec = null, string? callSpec = null)
	{
		return CallableShapeService.BuildCallableType(kind, returnType, parameters, targetSpec, callSpec);
	}

	bool TryGetCallableShape(string? type, out CallableShape shape)
	{
		shape = default;
		if (type is null)
			return false;

		if (TryGetIteratorProtocolParameterTypes(type, out List<string>? iteratorParameters) && iteratorParameters is not null)
		{
			shape = new CallableShape("iter", null, null, "bool", iteratorParameters);
			return true;
		}

		if (TryParseCallableShape(type, out shape))
			return true;

		if (typeDefinitions.TryGetValue(BaseTypeName(type), out TypeDefinition? definition)
			&& definition is NewtypeDefinition { UnderlyingType: not null } newtypeDefinition)
		{
			Dictionary<string, string> substitutions = [];
			AddConstructedTypeGenericSubstitutions(type, substitutions);
			string underlyingType = newtypeDefinition.UnderlyingType.ResolvedType ?? ErrorType;
			if (TryParseCallableShape(underlyingType, out shape))
			{
				ThisContract thisContract = GetCallableNewtypeThisContract(newtypeDefinition);
				if (newtypeDefinition.Parameters.Count > 0)
					shape = new CallableShape(shape.Kind, shape.Spec, shape.CallSpec, SubstituteGenericType(shape.ReturnType, substitutions), [.. GetCallableParameterTypeNames(newtypeDefinition.Parameters).Select(parameter => SubstituteGenericType(parameter, substitutions))], thisContract);
				else
					shape = shape with { ReturnType = SubstituteGenericType(shape.ReturnType, substitutions), This = thisContract };
				return true;
			}

			if (newtypeDefinition.UnderlyingType is IterTypeReference
				&& TryGetIteratorProtocolParameterTypes(underlyingType, out List<string>? iterParameters)
				&& iterParameters is not null)
			{
				List<string> parameters = [.. iterParameters.Select(parameter => SubstituteGenericType(parameter, substitutions))];
				parameters.AddRange(GetExpandedCallableParameterTypes(newtypeDefinition.Parameters).Select(parameter => SubstituteGenericType(parameter, substitutions)));
				shape = new CallableShape("iter", null, null, "bool", parameters, GetCallableNewtypeThisContract(newtypeDefinition));
				return true;
			}
		}

		return false;
	}

	static bool TryGetIteratorProtocolParameterTypes(string type, out List<string>? parameterTypes)
	{
		parameterTypes = null;
		if (!TryGetIteratorProtocolSlots(type, out List<string>? currentTypes, out string? thrownType) || currentTypes is null)
			return false;

		parameterTypes = [];
		foreach (string currentType in currentTypes)
			AddIteratorProtocolCurrentParameterTypes(currentType, parameterTypes);
		if (!string.IsNullOrWhiteSpace(thrownType))
			parameterTypes.Add("thrown " + thrownType);
		return true;
	}

	static void AddIteratorProtocolCurrentParameterTypes(string currentType, List<string> parameterTypes)
	{
		if (TryGetArrayElementType(currentType) is string elementType)
		{
			parameterTypes.Add(AddPointer(AddPointer(elementType)));
			parameterTypes.Add(AddPointer("nuint"));
			return;
		}

		parameterTypes.Add(AddPointer(currentType));
	}

	static bool TryGetIteratorProtocolCurrentTypes(string type, out List<string>? currentTypes)
	{
		return TryGetIteratorProtocolSlots(type, out currentTypes, out _);
	}

	static bool TryGetIteratorProtocolSlots(string type, out List<string>? currentTypes, out string? thrownType)
	{
		currentTypes = null;
		thrownType = null;
		if (type.StartsWith("iter ", StringComparison.Ordinal))
		{
			currentTypes = [type["iter ".Length..].Trim()];
			return currentTypes[0].Length > 0;
		}
		if (type.StartsWith("iter(", StringComparison.Ordinal))
		{
			currentTypes = [];
			string slots = type["iter(".Length..];
			if (slots.EndsWith(")", StringComparison.Ordinal))
				slots = slots[..^1];
			foreach (string slot in SplitCallableParameterTypes(slots))
			{
				if (slot.StartsWith("thrown ", StringComparison.Ordinal))
				{
					thrownType = slot["thrown ".Length..].Trim();
					continue;
				}
				if (slot.Length > 0)
					currentTypes.Add(slot);
			}
			return currentTypes.Count > 0;
		}
		return false;
	}

	static bool TryParseCallableShape(string type, out CallableShape shape)
	{
		return CallableShapeService.TryParseCallableShape(type, out shape);
	}

	IEnumerable<string> GetCallableParameterTypeNames(IEnumerable<ParameterDefinition> parameters)
	{
		return GetParameterTypeNames(GetCallableParameters([.. parameters]));
	}

	static List<string> SplitCallableSpecs(ref string text)
	{
		return CallableShapeService.SplitCallableSpecs(ref text);
	}

	static List<string> SplitCallableParameterTypes(string parametersText)
	{
		return CallableShapeService.SplitCallableParameterTypes(parametersText);
	}

	bool CallableShapesCompatible(CallableShape source, CallableShape target)
	{
		return CallableShapesCompatible(source, target, compareThis: true);
	}

	bool CallableShapesCompatibleIgnoringThis(CallableShape source, CallableShape target)
	{
		return CallableShapesCompatible(source, target, compareThis: false);
	}

	bool CallableShapesCompatibleWithConstOfVariance(CallableShape source, CallableShape target, bool compareThis = true, bool expandParams = true)
	{
		if (expandParams)
		{
			source = ExpandCallableShape(source);
			target = ExpandCallableShape(target);
		}

		return CallableShapeService.CompatibleWithConstOfVariance(source, target, compareThis, EraseConstOfQualifiers);
	}

	static bool CallableSlotTypesCompatible(string source, string target, bool outputPosition)
	{
		return CallableShapeService.SlotTypesCompatible(source, target, outputPosition, EraseConstOfQualifiers);
	}

	static CallableSlot ParseCallableSlot(string text)
	{
		return CallableShapeService.ParseCallableSlot(text);
	}

	bool CallableShapesCompatible(CallableShape source, CallableShape target, bool compareThis)
	{
		source = ExpandCallableShape(source);
		target = ExpandCallableShape(target);

		return CallableShapeService.Compatible(source, target, compareThis);
	}

	CallableShape ExpandCallableShape(CallableShape shape)
	{
		return new CallableShape(shape.Kind, shape.Spec, shape.CallSpec, shape.ReturnType, GetExpandedCallableParameterTypes(shape.Parameters), shape.This);
	}

	CallableShape BuildFunctionSourceCallableShape(FunctionDefinition function, bool isInstance, string? kindOverride = null)
	{
		Dictionary<string, string> anchors = BuildSignatureAnchorMap(function.Parameters);
		if ((GetExplicitThisParameter(function) ?? function.EffectiveThisParameter) is ThisParameterDefinition)
			anchors["this"] = "this";

		string kind = kindOverride ?? (isInstance ? "delegate" : "fn");
		List<string> parameters = [];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (isInstance && parameter is ThisParameterDefinition)
				continue;
			parameters.Add(FormatSignatureParameter(parameter, anchors));
		}

		return new CallableShape(kind, null, function.CallSpec, FormatSignatureTypeReference(function.ReturnType, anchors), parameters, GetThisContract(GetExplicitThisParameter(function) ?? function.EffectiveThisParameter));
	}

	bool TryBuildNewtypeSourceCallableShape(NewtypeDefinition definition, out CallableShape shape)
	{
		shape = default;
		Dictionary<string, string> anchors = BuildSignatureAnchorMap(definition.Parameters);
		if (GetCallableNewtypeThisParameter(definition) is not null)
			anchors["this"] = "this";
		ThisContract thisContract = GetCallableNewtypeThisContract(definition);

		if (definition.UnderlyingType is CallableTypeReference callable)
		{
			shape = new CallableShape(GetCallableKindName(callable.Kind), callable.TargetSpec, callable.CallSpec, FormatSignatureTypeReference(callable.ReturnType, anchors), [.. definition.Parameters.Where(parameter => parameter is not ThisParameterDefinition).Select(parameter => FormatSignatureParameter(parameter, anchors))], thisContract);
			return true;
		}

		if (definition.UnderlyingType is IterTypeReference iter)
		{
			List<string> parameters = GetSourceIteratorProtocolParameterTypes(iter, anchors);
			parameters.AddRange(definition.Parameters.Where(parameter => parameter is not ThisParameterDefinition).Select(parameter => FormatSignatureParameter(parameter, anchors)));
			shape = new CallableShape(iter.IsAsync ? "async iter" : "iter", null, null, "bool", parameters, thisContract);
			return true;
		}

		return false;
	}

	CallableShape BuildInterfaceSourceSlotCallableShape(InterfaceDefinition owner, FunctionDefinition member)
	{
		Dictionary<string, string> anchors = BuildSignatureAnchorMap(member.Parameters);
		List<string> parameters = [$"{owner.Name}*"];
		foreach (ParameterDefinition parameter in member.Parameters)
		{
			if (parameter is ThisParameterDefinition)
				continue;
			parameters.Add(FormatSignatureParameter(parameter, anchors));
		}
		return new CallableShape("fn", null, member.CallSpec, FormatSignatureTypeReference(member.ReturnType, anchors), parameters);
	}

	static Dictionary<string, string> BuildSignatureAnchorMap(List<ParameterDefinition> parameters)
	{
		Dictionary<string, string> anchors = new(StringComparer.Ordinal);
		int index = 0;
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter is ThisParameterDefinition)
				anchors["this"] = "this";
			if (!string.IsNullOrWhiteSpace(parameter.Name))
				anchors[parameter.Name] = "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
			index++;
		}
		return anchors;
	}

	static string FormatSignatureParameter(ParameterDefinition parameter, Dictionary<string, string> anchors)
	{
		string type = parameter.Type is null ? parameter.ResolvedType ?? ErrorType : FormatSignatureTypeReference(parameter.Type, anchors);
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

	static List<string> GetSourceIteratorProtocolParameterTypes(IterTypeReference iter, Dictionary<string, string> anchors)
	{
		List<string> parameters = [];
		if (iter.Parameters.Count == 0)
		{
			AddIteratorProtocolCurrentParameterTypes(FormatSignatureTypeReference(iter.ElementType, anchors), parameters);
			return parameters;
		}

		foreach (ParameterDefinition parameter in iter.Parameters)
		{
			string type = parameter.Type is null ? parameter.ResolvedType ?? ErrorType : FormatSignatureTypeReference(parameter.Type, anchors);
			if (parameter.Modifier == ParameterModifier.Thrown)
				parameters.Add("thrown " + type);
			else
				AddIteratorProtocolCurrentParameterTypes(type, parameters);
		}
		return parameters;
	}

	static string FormatSignatureTypeReference(TypeReference? type, Dictionary<string, string> anchors)
	{
		return type switch
		{
			null => ErrorType,
			ConstOfTypeReference constOf => FormatTypeDeclarator("constof(" + (anchors.TryGetValue(constOf.AnchorName, out string? mapped) ? mapped : constOf.AnchorName) + ")", constOf.Type is null ? null : new RawFormattedTypeReference(FormatSignatureTypeReference(constOf.Type, anchors))),
			AttributedTypeReference attributed => FormatSignatureTypeReference(attributed.Type, anchors),
			GenericTypeReference generic => generic.TypeArguments.Count == 0
				? FormatSignatureTypeReference(generic.Type, anchors)
				: $"{FormatSignatureTypeReference(generic.Type, anchors)}<{string.Join(", ", generic.TypeArguments.Select(argument => FormatSignatureTypeReference(argument, anchors)))}>",
			ArrayTypeReference array => $"{FormatSignatureTypeReference(array.ElementType, anchors)}[]",
			FixedArrayTypeReference fixedArray => $"{FormatSignatureTypeReference(fixedArray.ElementType, anchors)}[{FormatFixedArrayLength(fixedArray)}]",
			OptionalTypeReference optional => $"{FormatSignatureTypeReference(optional.ElementType, anchors)}?",
			PointerTypeReference pointer => $"{FormatSignatureTypeReference(pointer.ElementType, anchors)}*",
			ConstTypeReference constant => FormatTypeDeclarator("const", constant.Type is null ? null : new RawFormattedTypeReference(FormatSignatureTypeReference(constant.Type, anchors))),
			VolatileTypeReference vol => FormatTypeDeclarator("volatile", vol.Type is null ? null : new RawFormattedTypeReference(FormatSignatureTypeReference(vol.Type, anchors))),
			EscapedTypeReference escaped => FormatTypeDeclarator("escaped", escaped.Type is null ? null : new RawFormattedTypeReference(FormatSignatureTypeReference(escaped.Type, anchors))),
			ScopedTypeReference scoped => FormatTypeDeclarator(BuildAnchoredDeclarator("scoped", scoped.Anchors), scoped.Type is null ? null : new RawFormattedTypeReference(FormatSignatureTypeReference(scoped.Type, anchors))),
			UnscopedTypeReference unscoped => FormatTypeDeclarator(BuildAnchoredDeclarator("unscoped", unscoped.Anchors), unscoped.Type is null ? null : new RawFormattedTypeReference(FormatSignatureTypeReference(unscoped.Type, anchors))),
			TargetTypeSpecTypeReference targetSpec => $"{FormatSignatureTypeReference(targetSpec.Type, anchors)} {targetSpec.Specifier}",
			CallableTypeReference callable => $"{GetCallableKindName(callable.Kind)}{FormatCallSpec(callable.TargetSpec)}{FormatCallSpec(callable.CallSpec)} {FormatSignatureTypeReference(callable.ReturnType, anchors)}({string.Join(", ", callable.Parameters.Select(parameter => FormatSignatureParameter(parameter, anchors)))})",
			IterTypeReference iter => $"{(iter.IsAsync ? "async iter" : "iter")}({string.Join(", ", iter.Parameters.Select(parameter => FormatSignatureParameter(parameter, anchors)))})",
			GroupedParamsTypeReference grouped => $"params({FormatSignatureTypeReference(grouped.StructType, anchors)})",
			MaterializedStructTypeReference materialized => $"struct({FormatSignatureTypeReference(materialized.ParamsType, anchors)})",
			ThrownTypeReference thrown => $"thrown({FormatSignatureTypeReference(thrown.Type, anchors)})",
			_ => type.ResolvedType ?? FormatTypeReference(type)
		};
	}

	sealed class RawFormattedTypeReference : TypeReference
	{
		public RawFormattedTypeReference(string text)
		{
			ResolvedType = text;
		}
	}

	static string? GetLambdaParameterSymbolName(LambdaParameter parameter)
	{
		return parameter.Name ?? parameter.Parameter?.Name;
	}

	static ThisContract GetThisContract(ThisParameterDefinition? parameter)
	{
		if (parameter is null)
			return default;

		bool isConst = parameter.Modifier == ParameterModifier.In || HasInternalThisDeclarator(parameter, "const") || IsConstQualified(parameter.ResolvedType ?? parameter.Type?.ResolvedType);
		bool isVolatile = false;
		string lifetime = "";
		if (parameter.SourceSyntax is ThisParameterSyntax { Declarators: not null } syntax)
		{
			foreach (TypeDeclaratorSyntax declarator in syntax.Declarators)
			{
				switch (declarator.Keyword?.Value)
				{
					case "const":
						isConst = true;
						break;
					case "volatile":
						isVolatile = true;
						break;
					case "escaped":
					case "scoped":
					case "unscoped":
						lifetime = declarator.Keyword.Value.Value;
						break;
				}
			}
		}

		return new ThisContract(true, isConst, isVolatile, lifetime);
	}

	static bool HasInternalThisDeclarator(ThisParameterDefinition parameter, string name)
	{
		foreach (AttributeConstructor attribute in parameter.Attributes)
			if (attribute.Name == name)
				return true;
		return false;
	}

	static ThisParameterDefinition? GetCallableNewtypeThisParameter(NewtypeDefinition definition)
	{
		return definition.Parameters.Count > 0 && definition.Parameters[0] is ThisParameterDefinition thisParameter
			? thisParameter
			: null;
	}

	static ThisContract GetCallableNewtypeThisContract(NewtypeDefinition definition)
	{
		return GetThisContract(GetCallableNewtypeThisParameter(definition));
	}

	static bool HasEscapedThisContract(FunctionDefinition function)
	{
		return GetThisContract(GetExplicitThisParameter(function) ?? function.EffectiveThisParameter).IsEscaped;
	}

	bool IsInstanceFunction(FunctionDefinition function)
	{
		return function.Modifier != FunctionModifier.Static
			&& function.Modifier != FunctionModifier.Constructor
			&& !IsDestructorFunction(function)
			&& FindContainingType(function) is not null;
	}

	bool IsReceiverBearingDeclaration(FunctionDefinition function)
	{
		if (GetExplicitThisParameter(function) is not null)
			return true;

		return function.Modifier != FunctionModifier.Static
			&& function.Modifier != FunctionModifier.Constructor
			&& !IsDestructorFunction(function)
			&& FindContainingType(function) is not null;
	}

	static string GetCallableNewtypeFamily(NewtypeDefinition definition)
	{
		return definition.UnderlyingType switch
		{
			CallableTypeReference callable => callable.Kind switch
			{
				CallableKind.Function => "fn",
				CallableKind.Delegate => "delegate",
				CallableKind.Async => "async",
				CallableKind.Once => "once",
				_ => "value"
			},
			IterTypeReference iter => iter.IsAsync ? "async iter" : "iter",
			_ => "value"
		};
	}

	static string BaseTypeName(string type)
	{
		if (new TypeShapeParser(type).TryParse(out TypeShape shape))
		{
			while (shape.Element is not null)
				shape = shape.Element;

			type = shape.Name;
		}
		else
		{
			type = StripConst(type);
		}

		int genericStart = type.IndexOf('<', StringComparison.Ordinal);
		if (genericStart >= 0)
			type = type[..genericStart];

		while (type.EndsWith("*", StringComparison.Ordinal) || type.EndsWith("[]", StringComparison.Ordinal) || type.EndsWith("?", StringComparison.Ordinal))
		{
			if (type.EndsWith("[]", StringComparison.Ordinal))
				type = type[..^2];
			else
				type = type[..^1];
		}

		return type;
	}

	static bool IsConstQualified(string? type)
	{
		return IsConstQualifiedShape(type);
	}

	static string StripConst(string type)
	{
		return StripConstFromShape(type);
	}
}
