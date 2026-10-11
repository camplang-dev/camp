using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	// Value-newtype stand-ins for carriers that survive the conversion view. They are
	// registered for conversion classification only and never reach emission or metadata.
	readonly Dictionary<string, string> carrierNewtypeNames = new(StringComparer.Ordinal);

	bool IsCarrierGeneric(GenericTypeReference generic, out string carrier)
	{
		carrier = "";
		if (generic.Type is not PrimitiveTypeReference primitive)
			return false;
		carrier = GetPrimitiveTypeName(primitive.Type);
		return CarrierTypeIdentity.IsCarrierName(carrier);
	}

	void AnalyzeCarrierType(GenericTypeReference generic, string carrier, AnalysisScope scope)
	{
		if (generic.Type is not null)
			AnalyzeOptionalType(generic.Type, scope);
		foreach (TypeReference argument in generic.TypeArguments)
			AnalyzeOptionalType(argument, scope);
		if (generic.TypeArguments.Count != 1)
		{
			Report(GetRange(generic.SourceSyntax), $"{carrier}<V> requires exactly one carried type.");
			generic.ResolvedType = ErrorType;
			return;
		}

		TypeReference operand = generic.TypeArguments[0];
		string payload = operand.ResolvedType ?? ErrorType;
		generic.ResolvedType = $"{carrier}<{payload}>";
		if (payload == ErrorType || payload.StartsWith(UnresolvedType, StringComparison.Ordinal))
			return;
		if (!TryExplainInvalidCarrierOperand(carrier, payload, scope, out string? reason))
			return;
		Report(GetRange(operand.SourceSyntax ?? generic.SourceSyntax), $"'{payload}' is not a valid carried type for {carrier}<V>: {reason}");
	}

	// Mirrors the argument rules of a representation constraint `T: C`. The reason names
	// the constraint that the operand fails.
	bool TryExplainInvalidCarrierOperand(string carrier, string payload, AnalysisScope scope, out string? reason)
	{
		reason = null;
		string constraint = $"T: {carrier}";
		string bare = StripTopLevelValueQualifiers(payload);
		if (!TryParseTypeShape(bare, out TypeShape shape))
		{
			reason = $"it does not satisfy '{constraint}'.";
			return true;
		}

		if (shape.Kind == TypeShapeKind.Pointer)
		{
			if (carrier is not ("nint" or "nuint"))
			{
				reason = $"pointer types satisfy only 'T: nint' and 'T: nuint', not '{constraint}'.";
				return true;
			}
			if (shape.Element is not null && TryGetParamsComponentShape(null, bare, "value", out _))
			{
				reason = $"a pointer to an expanded type is distributive and does not satisfy '{constraint}'.";
				return true;
			}
			if (GetObjectPointerWidth(shape) > GetCarrierWidth(carrier, shape.TargetSpec))
			{
				reason = $"the pointer is wider than '{carrier}' on the selected target, so it does not satisfy '{constraint}'.";
				return true;
			}
			return false;
		}

		if (shape.Kind != TypeShapeKind.Named)
		{
			reason = $"it does not satisfy '{constraint}'; array, optional and raw function pointer types cannot be carried.";
			return true;
		}

		if (ErasedTypeIdentity.TryGetPayload(bare, out _) || CarrierTypeIdentity.TryGetPayload(bare, out _))
		{
			reason = "a carrier operand cannot be void<V> or another carrier.";
			return true;
		}
		if (shape.Name == "void")
		{
			reason = "a carrier operand cannot be void.";
			return true;
		}
		if (TryGetParamsComponentShape(null, bare, "value", out _))
		{
			reason = $"an expanded type does not satisfy '{constraint}'; use void<V> storage instead.";
			return true;
		}

		int carrierWidth = GetCarrierWidth(carrier, shape.TargetSpec);
		if (scope.TryGetGenericParameter(shape.Name, out GenericParameter? generic) && generic is not null)
		{
			if (generic.RequiresImplementation || generic.Constraint is not PrimitiveTypeReference { Type: PrimitiveType primitiveConstraint } || !IsIntegralPrimitive(primitiveConstraint)
				|| GetCarrierWidth(GetPrimitiveTypeName(primitiveConstraint), null) > carrierWidth)
				reason = $"type parameter '{generic.Name}' does not satisfy '{constraint}'; its constraint must be an integer representation constraint no wider than '{carrier}'.";
			return reason is not null;
		}

		if (TryGetIntegerOperandWidth(shape, out int operandWidth))
		{
			if (operandWidth > carrierWidth)
				reason = $"it is wider than '{carrier}' and does not satisfy '{constraint}'.";
			return reason is not null;
		}

		reason = $"it does not satisfy '{constraint}'; carried types are integers, enums, value newtypes with an integer underlying type, or pointers for nint and nuint.";
		return true;
	}

	bool TryGetIntegerOperandWidth(TypeShape shape, out int width)
	{
		width = 0;
		string name = shape.Name;
		if (IsIntegerWidthName(name))
		{
			width = GetCarrierWidth(name, shape.TargetSpec);
			return true;
		}
		if (!TryGetTypeDefinitionByResolvedName(name, out TypeDefinition? definition) || definition is null)
			return false;
		string? underlying = definition switch
		{
			EnumDefinition enumDefinition => enumDefinition.UnderlyingType is null ? "int" : GetDeclaredUnderlyingName(enumDefinition.UnderlyingType),
			NewtypeDefinition { UnderlyingType: not null } newtype => GetDeclaredUnderlyingName(newtype.UnderlyingType),
			_ => null
		};
		if (underlying is null || !IsIntegerWidthName(StripTopLevelValueQualifiers(underlying)))
			return false;
		width = GetCarrierWidth(StripTopLevelValueQualifiers(underlying), shape.TargetSpec);
		return true;
	}

	// A callable signature can be checked before the declaration that owns the underlying type is
	// analyzed, so a primitive written in source is read directly.
	string? GetDeclaredUnderlyingName(TypeReference underlying) =>
		underlying.ResolvedType ?? (underlying is PrimitiveTypeReference primitive ? GetPrimitiveTypeName(primitive.Type) : null);

	static bool IsIntegerWidthName(string name) => name is "byte" or "sbyte" or "ushort" or "short" or "uint" or "int" or "ulong" or "long" or "nuint" or "nint";

	int GetCarrierWidth(string name, string? targetSpec)
	{
		return name switch
		{
			"byte" or "sbyte" => 8,
			"ushort" or "short" => 16,
			"uint" or "int" => 32,
			"ulong" or "long" => 64,
			_ => selectedTarget?.Capabilities.GetNaturalIntegerWidth(targetSpec) ?? 32
		};
	}

	// True when the innermost element of a type is `void<V>` or a carrier, however many
	// pointer, array or optional layers wrap it.
	bool IsAnnotatedErasedBase(string? type)
	{
		if (type is null || !TryParseTypeShape(StripTopLevelValueQualifiers(type), out TypeShape shape))
			return false;
		while (shape.Element is not null)
			shape = shape.Element;
		return shape.Kind == TypeShapeKind.Named
			&& (ErasedTypeIdentity.TryGetPayload(shape.Name, out _) || CarrierTypeIdentity.TryGetPayload(shape.Name, out _));
	}

	// An lvalue of carrier storage reads as V; its address keeps the written carrier.
	bool TryGetCarrierStorageType(Expression? expression, out string storageType)
	{
		storageType = "";
		if (expression is not null && expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(expression, rewritten))
			return TryGetCarrierStorageType(rewritten, out storageType);
		string? candidate = expression switch
		{
			ParenthesizedExpression parenthesized => TryGetCarrierStorageType(parenthesized.Expression, out storageType) ? storageType : null,
			VariableReferenceExpression variable => variable.Variable?.ResolvedType,
			MemberReferenceExpression { Member: FieldDefinition field } => field.ResolvedType,
			UnaryExpression { Operator: UnaryOperator.PointerDereference } unary => TryGetPointerElementType(unary.Operand?.ResolvedType),
			IndexExpression { Arguments.Count: 1 } index => TryGetArrayElementType(index.Target?.ResolvedType) ?? TryGetPointerElementType(index.Target?.ResolvedType),
			_ => null
		};
		if (!CarrierTypeIdentity.TryGetPayload(candidate, out _))
			return false;
		storageType = candidate!;
		return true;
	}

	// The conversion view rewrites a written type for classification only. It never
	// replaces a canonical type: symbols, metadata and generic identity keep the written form.
	string ConversionView(string type)
	{
		if (!type.Contains('<', StringComparison.Ordinal))
			return type;
		if (!TryParseTypeShape(type, out TypeShape shape))
			return type;
		TypeShape viewed = ViewTopLevel(shape);
		viewed = ViewShape(viewed, resultPosition: false);
		string text = TypeShapeParser.Format(viewed);
		return text == TypeShapeParser.Format(shape) ? type : text;
	}

	TypeShape ViewTopLevel(TypeShape shape)
	{
		if (shape.Kind == TypeShapeKind.Named && CarrierTypeIdentity.TryGetPayload(shape.Name, out string payload)
			&& TryParseTypeShape(payload, out TypeShape payloadShape))
			return MergeQualifiers(payloadShape, shape);
		return shape;
	}

	static TypeShape MergeQualifiers(TypeShape inner, TypeShape outer)
	{
		return inner with
		{
			Qualifiers = new TypeQualifiers(
				inner.Qualifiers.IsConst || outer.Qualifiers.IsConst,
				inner.Qualifiers.IsVolatile || outer.Qualifiers.IsVolatile,
				outer.Qualifiers.Lifetime > inner.Qualifiers.Lifetime ? outer.Qualifiers.Lifetime : inner.Qualifiers.Lifetime),
			TargetSpec = inner.TargetSpec ?? outer.TargetSpec
		};
	}

	TypeShape ViewShape(TypeShape shape, bool resultPosition)
	{
		if (shape.Element is not null)
			return shape with { Element = ViewShape(shape.Element, resultPosition: false) };
		if (shape.Kind != TypeShapeKind.Named)
			return shape;

		if (ErasedTypeIdentity.TryGetPayload(shape.Name, out string erasedPayload) && TryParseTypeShape(erasedPayload, out TypeShape erasedShape))
		{
			TypeShape viewedPayload = ViewShape(erasedShape, resultPosition: false);
			if (resultPosition || TryGetParamsComponentShape(null, TypeShapeParser.Format(erasedShape), "value", out _))
				return shape with { Name = $"void<{TypeShapeParser.Format(viewedPayload)}>" };
			return MergeQualifiers(viewedPayload, shape);
		}

		if (CarrierTypeIdentity.TryGetCarrier(shape.Name, out string carrier, out string carried) && TryParseTypeShape(carried, out TypeShape carriedShape))
			return shape with { Name = GetCarrierNewtypeName(carrier, TypeShapeParser.Format(ViewShape(carriedShape, resultPosition: false))) };

		if (TryParseCallableShape(shape.Name, out CallableShape callable))
			return shape with { Name = ViewCallable(callable) };

		return shape with { Name = ViewGenericArguments(shape.Name) };
	}

	string ViewCallable(CallableShape callable)
	{
		callable = ViewCallableShape(callable);
		return BuildCallableType(callable.Kind, callable.ReturnType, callable.Parameters, callable.Spec, callable.CallSpec);
	}

	// Callable matching compares source shapes in the conversion view. A `void<V>` result is
	// protected at every depth, so a trailing `out V` never stands in for it.
	CallableShape ViewCallableShape(CallableShape callable)
	{
		string result = callable.ReturnType;
		if (TryParseTypeShape(result, out TypeShape resultShape))
			result = TypeShapeParser.Format(ViewShape(resultShape, resultPosition: true));

		List<string> parameters = [];
		foreach (string text in callable.Parameters)
		{
			CallableSlot slot = ParseCallableSlot(text);
			if (slot.Modifier is "within" or "thrown"
				|| slot.Type.StartsWith("sizeof(", StringComparison.Ordinal) || slot.Type.StartsWith("typenameof(", StringComparison.Ordinal)
				|| slot.Type.StartsWith("vtableof(", StringComparison.Ordinal)
				|| !TryParseTypeShape(slot.Type, out TypeShape slotShape))
			{
				parameters.Add(text);
				continue;
			}
			string viewed = TypeShapeParser.Format(ViewShape(slotShape, resultPosition: false));
			parameters.Add(slot.Modifier.Length == 0 ? viewed : slot.Modifier + " " + viewed);
		}
		return callable with { ReturnType = result, Parameters = parameters };
	}

	string ViewGenericArguments(string name)
	{
		int start = name.IndexOf('<', StringComparison.Ordinal);
		if (start <= 0 || !name.EndsWith('>'))
			return name;
		List<string> arguments = CallableShapeService.SplitCallableParameterTypes(name[(start + 1)..^1]);
		List<string> viewed = [];
		foreach (string argument in arguments)
		{
			if (!TryParseTypeShape(argument, out TypeShape argumentShape))
			{
				viewed.Add(argument);
				continue;
			}
			viewed.Add(TypeShapeParser.Format(ViewShape(argumentShape, resultPosition: false)));
		}
		return $"{name[..start]}<{string.Join(", ", viewed)}>";
	}

	string GetCarrierNewtypeName(string carrier, string carried)
	{
		string key = $"{carrier}<{carried}>";
		if (carrierNewtypeNames.TryGetValue(key, out string? existing))
			return existing;

		string sanitized = new([.. carried.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')]);
		string name = $"{char.ToUpperInvariant(carrier[0])}{carrier[1..]}CarrierOf{sanitized}";
		while (typeDefinitions.ContainsKey(name))
			name += "_";
		carrierNewtypeNames[key] = name;
		TryGetPrimitiveType(carrier, out PrimitiveType primitive);
		typeDefinitions[name] = new NewtypeDefinition
		{
			Name = name,
			UnderlyingType = new PrimitiveTypeReference { Type = primitive, ResolvedType = carrier }
		};
		return name;
	}
}
