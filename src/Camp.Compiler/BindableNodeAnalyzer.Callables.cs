using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	static string BuildCallableType(string kind, string returnType, List<string> parameters, string? targetSpec = null, string? callSpec = null)
	{
		string specs = "";
		if (!string.IsNullOrWhiteSpace(targetSpec))
			specs += " " + targetSpec;
		if (!string.IsNullOrWhiteSpace(callSpec))
			specs += " " + callSpec;
		return $"{kind}{specs} {returnType}({string.Join(", ", parameters)})";
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
			string underlyingType = newtypeDefinition.UnderlyingType.ResolvedType ?? ErrorType;
				if (TryParseCallableShape(underlyingType, out shape))
				{
					ThisContract thisContract = GetCallableNewtypeThisContract(newtypeDefinition);
					if (newtypeDefinition.Parameters.Count > 0)
						shape = new CallableShape(shape.Kind, shape.Spec, shape.CallSpec, shape.ReturnType, [.. GetCallableParameterTypeNames(newtypeDefinition.Parameters)], thisContract);
					else
						shape = shape with { This = thisContract };
					return true;
				}

			if (newtypeDefinition.UnderlyingType is IterTypeReference
				&& TryGetIteratorProtocolParameterTypes(underlyingType, out List<string>? iterParameters)
				&& iterParameters is not null)
			{
				List<string> parameters = [.. iterParameters];
				parameters.AddRange(GetExpandedCallableParameterTypes(newtypeDefinition.Parameters));
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
		shape = default;
		string kind;
		string remainder;
		if (type.StartsWith("fn ", StringComparison.Ordinal))
		{
			kind = "fn";
			remainder = type["fn ".Length..];
		}
		else if (type.StartsWith("delegate ", StringComparison.Ordinal))
		{
			kind = "delegate";
			remainder = type["delegate ".Length..];
		}
		else if (type.StartsWith("async ", StringComparison.Ordinal))
		{
			kind = "async";
			remainder = type["async ".Length..];
		}
		else if (type.StartsWith("once ", StringComparison.Ordinal))
		{
			kind = "once";
			remainder = type["once ".Length..];
		}
		else
		{
			return false;
		}

		int open = remainder.IndexOf('(', StringComparison.Ordinal);
		int close = remainder.LastIndexOf(')');
		if (open < 0 || close < open)
			return false;

		string signaturePrefix = remainder[..open].Trim();
		List<string> specs = SplitCallableSpecs(ref signaturePrefix);

		string returnType = signaturePrefix;
		string parametersText = remainder[(open + 1)..close].Trim();
		shape = new CallableShape(kind, specs.Count > 0 ? specs[0] : null, specs.Count > 1 ? specs[1] : null, returnType, SplitCallableParameterTypes(parametersText));
		return true;
	}

	IEnumerable<string> GetCallableParameterTypeNames(IEnumerable<ParameterDefinition> parameters)
	{
		return GetParameterTypeNames(GetCallableParameters([.. parameters]));
	}

	static List<string> SplitCallableSpecs(ref string text)
	{
		List<string> specs = [];
		while (true)
		{
			int space = text.IndexOf(' ');
			if (space <= 0)
				return specs;

			string candidate = text[..space];
			if (!candidate.StartsWith("_", StringComparison.Ordinal))
				return specs;

			specs.Add(candidate);
			text = text[(space + 1)..].TrimStart();
		}
	}

	static List<string> SplitCallableParameterTypes(string parametersText)
	{
		List<string> parameters = [];
		if (string.IsNullOrWhiteSpace(parametersText))
			return parameters;

		int start = 0;
		int depth = 0;
		for (int i = 0; i < parametersText.Length; i++)
		{
			char c = parametersText[i];
			if (c is '(' or '<' or '[')
				depth++;
			else if (c is ')' or '>' or ']')
				depth--;
			else if (c == ',' && depth == 0)
			{
				parameters.Add(parametersText[start..i].Trim());
				start = i + 1;
			}
		}

		parameters.Add(parametersText[start..].Trim());
		return parameters;
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

		if (source.Parameters.Count != target.Parameters.Count)
			return false;
		if (source.Spec != target.Spec
			|| source.CallSpec != target.CallSpec
			|| compareThis && source.This != target.This)
			return false;
		if (!CallableSlotTypesCompatible(source.ReturnType, target.ReturnType, outputPosition: true))
			return false;

		for (int i = 0; i < source.Parameters.Count; i++)
		{
			CallableSlot sourceSlot = ParseCallableSlot(source.Parameters[i]);
			CallableSlot targetSlot = ParseCallableSlot(target.Parameters[i]);
			if (sourceSlot.Modifier != targetSlot.Modifier)
				return false;
			if (sourceSlot.Modifier == "thrown")
			{
				if (sourceSlot.Type != targetSlot.Type)
					return false;
				continue;
			}

			bool outputPosition = sourceSlot.Modifier == "out";
			if (!CallableSlotTypesCompatible(sourceSlot.Type, targetSlot.Type, outputPosition))
				return false;
		}

		return true;
	}

	static bool CallableSlotTypesCompatible(string source, string target, bool outputPosition)
	{
		if (source == target)
			return true;

		string erasedSource = EraseConstOfQualifiers(source);
		string erasedTarget = EraseConstOfQualifiers(target);
		if (source != erasedSource && erasedSource == target)
			return outputPosition;
		if (target != erasedTarget && source == erasedTarget)
			return !outputPosition;
		return false;
	}

	readonly record struct CallableSlot(string Modifier, string Type);

	static CallableSlot ParseCallableSlot(string text)
	{
		foreach (string modifier in new[] { "out", "in", "thrown", "within" })
		{
			string prefix = modifier + " ";
			if (text.StartsWith(prefix, StringComparison.Ordinal))
				return new CallableSlot(modifier, text[prefix.Length..]);
		}
		return new CallableSlot("", text);
	}

	bool CallableShapesCompatible(CallableShape source, CallableShape target, bool compareThis)
	{
		source = ExpandCallableShape(source);
		target = ExpandCallableShape(target);

		if (source.Parameters.Count != target.Parameters.Count)
			return false;

		for (int i = 0; i < source.Parameters.Count; i++)
		{
			if (source.Parameters[i] != target.Parameters[i])
				return false;
		}

		return source.Spec == target.Spec
			&& source.CallSpec == target.CallSpec
			&& source.ReturnType == target.ReturnType
			&& (!compareThis || source.This == target.This);
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

	readonly record struct CallableShape(string Kind, string? Spec, string? CallSpec, string ReturnType, List<string> Parameters, ThisContract This = default)
	{
	}

	readonly record struct ThisContract(bool HasThis, bool IsConst, bool IsVolatile, string Lifetime)
	{
		public bool IsEscaped => Lifetime == "escaped";
		public bool IsDefault => !HasThis;
	}

	static ThisContract GetThisContract(ThisParameterDefinition? parameter)
	{
		if (parameter is null)
			return default;

		bool isConst = parameter.Modifier == ParameterModifier.In;
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
