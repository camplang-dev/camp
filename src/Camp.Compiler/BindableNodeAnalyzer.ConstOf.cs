using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void ValidateFunctionConstOfAnchors(FunctionDefinition definition)
	{
		Dictionary<string, ParameterDefinition> anchors = BuildConstOfAnchorMap(definition.Parameters);
		if ((GetExplicitThisParameter(definition) ?? definition.EffectiveThisParameter) is ThisParameterDefinition thisParameter)
			anchors["this"] = thisParameter;

		ValidateConstOfAnchors(definition.ReturnType, anchors);
		ValidateConstOfAnchors(definition.CallableAscriptionType, anchors);
		foreach (ParameterDefinition parameter in definition.Parameters)
			ValidateConstOfAnchors(parameter.Type, anchors);
	}

	void ValidateNewtypeConstOfAnchors(NewtypeDefinition definition)
	{
		Dictionary<string, ParameterDefinition> anchors = BuildConstOfAnchorMap(definition.Parameters);
		if (definition.UnderlyingType is CallableTypeReference callable)
			ValidateConstOfAnchors(callable.ReturnType, anchors);
		else if (definition.UnderlyingType is IterTypeReference iter)
			ValidateConstOfAnchors(iter.ElementType, anchors);
		else
			ValidateConstOfAnchors(definition.UnderlyingType, anchors);
		foreach (ParameterDefinition parameter in definition.Parameters)
			ValidateConstOfAnchors(parameter.Type, anchors);
	}

	void ValidateCallableConstOfAnchors(CallableTypeReference callable)
	{
		Dictionary<string, ParameterDefinition> anchors = BuildConstOfAnchorMap(callable.Parameters);
		ValidateConstOfAnchors(callable.ReturnType, anchors);
		foreach (ParameterDefinition parameter in callable.Parameters)
			ValidateConstOfAnchors(parameter.Type, anchors);
	}

	void ValidateIterConstOfAnchors(IterTypeReference iter)
	{
		Dictionary<string, ParameterDefinition> anchors = BuildConstOfAnchorMap(iter.Parameters);
		ValidateConstOfAnchors(iter.ElementType, anchors);
		foreach (ParameterDefinition parameter in iter.Parameters)
			ValidateConstOfAnchors(parameter.Type, anchors);
	}

	static Dictionary<string, ParameterDefinition> BuildConstOfAnchorMap(List<ParameterDefinition> parameters)
	{
		Dictionary<string, ParameterDefinition> anchors = new(System.StringComparer.Ordinal);
		foreach (ParameterDefinition parameter in parameters)
			if (!string.IsNullOrWhiteSpace(parameter.Name))
				anchors[parameter.Name] = parameter;
		return anchors;
	}

	void ValidateConstOfAnchors(TypeReference? type, Dictionary<string, ParameterDefinition> anchors)
	{
		if (type is null)
			return;

		switch (type)
		{
			case ConstOfTypeReference constOf:
				ValidateConstOfAnchor(constOf, anchors);
				ValidateConstOfAnchors(constOf.Type, anchors);
				break;

			case AttributedTypeReference attributed:
				ValidateConstOfAnchors(attributed.Type, anchors);
				break;

			case GenericTypeReference generic:
				ValidateConstOfAnchors(generic.Type, anchors);
				foreach (TypeReference argument in generic.TypeArguments)
					ValidateConstOfAnchors(argument, anchors);
				break;

			case ArrayTypeReference array:
				ValidateConstOfAnchors(array.ElementType, anchors);
				break;

			case FixedArrayTypeReference fixedArray:
				ValidateConstOfAnchors(fixedArray.ElementType, anchors);
				break;

			case OptionalTypeReference optional:
				ValidateConstOfAnchors(optional.ElementType, anchors);
				break;

			case PointerTypeReference pointer:
				ValidateConstOfAnchors(pointer.ElementType, anchors);
				break;

			case ConstTypeReference constant:
				ValidateConstOfAnchors(constant.Type, anchors);
				break;

			case VolatileTypeReference vol:
				ValidateConstOfAnchors(vol.Type, anchors);
				break;

			case EscapedTypeReference escaped:
				ValidateConstOfAnchors(escaped.Type, anchors);
				break;

			case ScopedTypeReference scoped:
				ValidateConstOfAnchors(scoped.Type, anchors);
				break;

			case UnscopedTypeReference unscoped:
				ValidateConstOfAnchors(unscoped.Type, anchors);
				break;

			case TargetTypeSpecTypeReference targetSpec:
				ValidateConstOfAnchors(targetSpec.Type, anchors);
				break;

			case CallableTypeReference callable:
				ValidateCallableConstOfAnchors(callable);
				break;

			case IterTypeReference iter:
				ValidateIterConstOfAnchors(iter);
				break;

			case GroupedParamsTypeReference grouped:
				ValidateConstOfAnchors(grouped.StructType, anchors);
				break;

			case MaterializedStructTypeReference materialized:
				ValidateConstOfAnchors(materialized.ParamsType, anchors);
				break;

			case ThrownTypeReference thrown:
				ValidateConstOfAnchors(thrown.Type, anchors);
				break;

			case TypeDefinitionReference definition:
				foreach (TypeReference argument in definition.TypeArguments)
					ValidateConstOfAnchors(argument, anchors);
				break;

			case NamedTypeReference named:
				foreach (TypeReference argument in named.TypeArguments)
					ValidateConstOfAnchors(argument, anchors);
				break;
		}
	}

	void ValidateConstOfAnchor(ConstOfTypeReference constOf, Dictionary<string, ParameterDefinition> anchors)
	{
		if (string.IsNullOrWhiteSpace(constOf.AnchorName) || !anchors.TryGetValue(constOf.AnchorName, out ParameterDefinition? anchor))
		{
			Report(GetRange(constOf.SourceSyntax), $"constof anchor '{constOf.AnchorName}' could not be resolved.");
			return;
		}

		constOf.Anchor = anchor;

		if (anchor.Modifier is ParameterModifier.Out or ParameterModifier.Thrown or ParameterModifier.Within
			|| anchor is WithinParameterDefinition)
		{
			Report(GetRange(constOf.SourceSyntax), $"constof anchor '{constOf.AnchorName}' must be a receiver or non-output parameter.");
			return;
		}

		if (ContainsConstOfTypeReference(anchor.Type))
		{
			Report(GetRange(constOf.SourceSyntax), $"constof anchor '{constOf.AnchorName}' cannot itself depend on constof.");
			return;
		}

		int slots = CountOrdinaryConstSlots(anchor);
		if (slots == 0)
			Report(GetRange(constOf.SourceSyntax), $"constof anchor '{constOf.AnchorName}' must have an ordinary const type slot.");
		else if (slots > 1)
			Report(GetRange(constOf.SourceSyntax), $"constof anchor '{constOf.AnchorName}' has more than one ordinary const type slot; v1 requires an unambiguous anchor.");
	}

	static bool ContainsConstOfTypeReference(TypeReference? type)
	{
		if (type is null)
			return false;

		return type switch
		{
			ConstOfTypeReference => true,
			AttributedTypeReference attributed => ContainsConstOfTypeReference(attributed.Type),
			GenericTypeReference generic => ContainsConstOfTypeReference(generic.Type) || generic.TypeArguments.Any(ContainsConstOfTypeReference),
			ArrayTypeReference array => ContainsConstOfTypeReference(array.ElementType),
			FixedArrayTypeReference fixedArray => ContainsConstOfTypeReference(fixedArray.ElementType),
			OptionalTypeReference optional => ContainsConstOfTypeReference(optional.ElementType),
			PointerTypeReference pointer => ContainsConstOfTypeReference(pointer.ElementType),
			ConstTypeReference constant => ContainsConstOfTypeReference(constant.Type),
			VolatileTypeReference vol => ContainsConstOfTypeReference(vol.Type),
			EscapedTypeReference escaped => ContainsConstOfTypeReference(escaped.Type),
			ScopedTypeReference scoped => ContainsConstOfTypeReference(scoped.Type),
			UnscopedTypeReference unscoped => ContainsConstOfTypeReference(unscoped.Type),
			TargetTypeSpecTypeReference targetSpec => ContainsConstOfTypeReference(targetSpec.Type),
			CallableTypeReference callable => ContainsConstOfTypeReference(callable.ReturnType) || callable.Parameters.Any(parameter => ContainsConstOfTypeReference(parameter.Type)),
			IterTypeReference iter => ContainsConstOfTypeReference(iter.ElementType) || iter.Parameters.Any(parameter => ContainsConstOfTypeReference(parameter.Type)),
			GroupedParamsTypeReference grouped => ContainsConstOfTypeReference(grouped.StructType),
			MaterializedStructTypeReference materialized => ContainsConstOfTypeReference(materialized.ParamsType),
			ThrownTypeReference thrown => ContainsConstOfTypeReference(thrown.Type),
			TypeDefinitionReference definition => definition.TypeArguments.Any(ContainsConstOfTypeReference),
			NamedTypeReference named => named.TypeArguments.Any(ContainsConstOfTypeReference),
			_ => false
		};
	}

	static int CountOrdinaryConstSlots(ParameterDefinition parameter)
	{
		return parameter is ThisParameterDefinition thisParameter && GetThisContract(thisParameter).IsConst
			? 1 + CountOrdinaryConstSlots(parameter.Type)
			: CountOrdinaryConstSlots(parameter.Type);
	}

	static int CountOrdinaryConstSlots(TypeReference? type)
	{
		if (type is null)
			return 0;

		return type switch
		{
			PrimitiveTypeReference { Type: PrimitiveType.String or PrimitiveType.AString or PrimitiveType.WString } => 1,
			NamedTypeReference { Name: "string" or "astring" or "wstring" } => 1,
			TypeDefinitionReference { Name: "string" or "astring" or "wstring" } => 1,
			ConstTypeReference constant => 1 + CountOrdinaryConstSlots(constant.Type),
			ConstOfTypeReference => 0,
			AttributedTypeReference attributed => CountOrdinaryConstSlots(attributed.Type),
			GenericTypeReference generic => CountOrdinaryConstSlots(generic.Type) + generic.TypeArguments.Sum(CountOrdinaryConstSlots),
			ArrayTypeReference array => CountOrdinaryConstSlots(array.ElementType),
			FixedArrayTypeReference fixedArray => CountOrdinaryConstSlots(fixedArray.ElementType),
			OptionalTypeReference optional => CountOrdinaryConstSlots(optional.ElementType),
			PointerTypeReference pointer => CountOrdinaryConstSlots(pointer.ElementType),
			VolatileTypeReference vol => CountOrdinaryConstSlots(vol.Type),
			EscapedTypeReference escaped => CountOrdinaryConstSlots(escaped.Type),
			ScopedTypeReference scoped => CountOrdinaryConstSlots(scoped.Type),
			UnscopedTypeReference unscoped => CountOrdinaryConstSlots(unscoped.Type),
			TargetTypeSpecTypeReference targetSpec => CountOrdinaryConstSlots(targetSpec.Type),
			CallableTypeReference callable => CountOrdinaryConstSlots(callable.ReturnType) + callable.Parameters.Sum(parameter => CountOrdinaryConstSlots(parameter.Type)),
			IterTypeReference iter => CountOrdinaryConstSlots(iter.ElementType) + iter.Parameters.Sum(parameter => CountOrdinaryConstSlots(parameter.Type)),
			GroupedParamsTypeReference grouped => CountOrdinaryConstSlots(grouped.StructType),
			MaterializedStructTypeReference materialized => CountOrdinaryConstSlots(materialized.ParamsType),
			ThrownTypeReference thrown => CountOrdinaryConstSlots(thrown.Type),
			TypeDefinitionReference definition => definition.TypeArguments.Sum(CountOrdinaryConstSlots),
			NamedTypeReference named => named.TypeArguments.Sum(CountOrdinaryConstSlots),
			_ => 0
		};
	}

	Dictionary<string, bool> BuildConstOfCallAnchorFacts(List<(ArgumentExpression Argument, ParameterDefinition Parameter)> arguments)
	{
		Dictionary<string, bool> anchors = new(System.StringComparer.Ordinal);
		foreach ((ArgumentExpression argument, ParameterDefinition parameter) in arguments)
		{
			if (string.IsNullOrWhiteSpace(parameter.Name) || anchors.ContainsKey(parameter.Name))
				continue;
			if (TryGetConstOfActualSlot(parameter, argument, out bool isConst))
				anchors[parameter.Name] = isConst;
		}
		return anchors;
	}

	void CheckConstOfCallArguments(List<(ArgumentExpression Argument, ParameterDefinition Parameter)> arguments, Dictionary<string, bool> anchors, SyntaxNode? fallbackSyntax)
	{
		foreach ((ArgumentExpression argument, ParameterDefinition parameter) in arguments)
			CheckConstOfCallArgument(parameter.Type, argument, anchors, fallbackSyntax);
	}

	void CheckConstOfCallArgument(TypeReference? expectedType, ArgumentExpression argument, Dictionary<string, bool> anchors, SyntaxNode? fallbackSyntax)
	{
		if (expectedType is null || !ContainsConstOfTypeReference(expectedType))
			return;
		if (!TryParseTypeShape(argument.ResolvedType ?? argument.Value?.ResolvedType ?? ErrorType, out TypeShape actualShape))
			return;
		foreach ((string anchorName, bool expectedConst, bool actualConst) in GetConstOfSlotComparisons(expectedType, actualShape, anchors))
		{
			if (expectedConst != actualConst)
			{
				SyntaxNode? syntax = GetExpressionDiagnosticSyntax(argument.Value) ?? argument.SourceSyntax;
				if (syntax is null)
					continue;
				string expectedText = expectedConst ? "const" : "mutable";
				string actualText = actualConst ? "const" : "mutable";
				Report(GetRange(syntax), $"Argument constness is {actualText}, but constof anchor '{anchorName}' requires {expectedText}.");
			}
		}
	}

	IEnumerable<(string AnchorName, bool ExpectedConst, bool ActualConst)> GetConstOfSlotComparisons(TypeReference? expectedType, TypeShape actualShape, Dictionary<string, bool> anchors)
	{
		if (expectedType is null)
			yield break;

		switch (expectedType)
		{
			case ConstOfTypeReference constOf:
				if (anchors.TryGetValue(constOf.AnchorName, out bool expectedConst))
					yield return (constOf.AnchorName, expectedConst, actualShape.Qualifiers.IsConst);
				foreach ((string anchorName, bool expected, bool actual) in GetConstOfSlotComparisons(constOf.Type, actualShape, anchors))
					yield return (anchorName, expected, actual);
				break;
			case ConstTypeReference constant:
				foreach ((string anchorName, bool expected, bool actual) in GetConstOfSlotComparisons(constant.Type, actualShape, anchors))
					yield return (anchorName, expected, actual);
				break;
			case ArrayTypeReference array when actualShape.Kind == TypeShapeKind.Array && actualShape.Element is not null:
				foreach ((string anchorName, bool expected, bool actual) in GetConstOfSlotComparisons(array.ElementType, actualShape.Element, anchors))
					yield return (anchorName, expected, actual);
				break;
			case FixedArrayTypeReference fixedArray when actualShape.Kind == TypeShapeKind.FixedArray && actualShape.Element is not null:
				foreach ((string anchorName, bool expected, bool actual) in GetConstOfSlotComparisons(fixedArray.ElementType, actualShape.Element, anchors))
					yield return (anchorName, expected, actual);
				break;
			case OptionalTypeReference optional when actualShape.Kind == TypeShapeKind.Optional && actualShape.Element is not null:
				foreach ((string anchorName, bool expected, bool actual) in GetConstOfSlotComparisons(optional.ElementType, actualShape.Element, anchors))
					yield return (anchorName, expected, actual);
				break;
			case PointerTypeReference pointer when actualShape.Kind == TypeShapeKind.Pointer && actualShape.Element is not null:
				foreach ((string anchorName, bool expected, bool actual) in GetConstOfSlotComparisons(pointer.ElementType, actualShape.Element, anchors))
					yield return (anchorName, expected, actual);
				break;
			case AttributedTypeReference attributed:
				foreach ((string anchorName, bool expected, bool actual) in GetConstOfSlotComparisons(attributed.Type, actualShape, anchors))
					yield return (anchorName, expected, actual);
				break;
			case VolatileTypeReference vol:
				foreach ((string anchorName, bool expected, bool actual) in GetConstOfSlotComparisons(vol.Type, actualShape, anchors))
					yield return (anchorName, expected, actual);
				break;
			case EscapedTypeReference escaped:
				foreach ((string anchorName, bool expected, bool actual) in GetConstOfSlotComparisons(escaped.Type, actualShape, anchors))
					yield return (anchorName, expected, actual);
				break;
			case ScopedTypeReference scoped:
				foreach ((string anchorName, bool expected, bool actual) in GetConstOfSlotComparisons(scoped.Type, actualShape, anchors))
					yield return (anchorName, expected, actual);
				break;
			case UnscopedTypeReference unscoped:
				foreach ((string anchorName, bool expected, bool actual) in GetConstOfSlotComparisons(unscoped.Type, actualShape, anchors))
					yield return (anchorName, expected, actual);
				break;
			case TargetTypeSpecTypeReference targetSpec:
				foreach ((string anchorName, bool expected, bool actual) in GetConstOfSlotComparisons(targetSpec.Type, actualShape, anchors))
					yield return (anchorName, expected, actual);
				break;
		}
	}

	bool TryGetConstOfActualSlot(ParameterDefinition parameter, ArgumentExpression argument, out bool isConst)
	{
		isConst = false;
		if (IsStringLiteralLikeExpression(argument.Value))
		{
			isConst = true;
			return true;
		}
		if (IsPrimitiveStringType(argument.ResolvedType ?? argument.Value?.ResolvedType))
		{
			isConst = true;
			return true;
		}
		if (!TryParseTypeShape(argument.ResolvedType ?? argument.Value?.ResolvedType ?? ErrorType, out TypeShape actualShape))
			return false;
		if (parameter.Type is null
			&& TryParseTypeShape(parameter.ResolvedType ?? ErrorType, out TypeShape parameterShape)
			&& TryGetConstOfActualSlot(parameterShape, actualShape, out isConst))
			return true;
		return TryGetConstOfActualSlot(parameter.Type, actualShape, out isConst);
	}

	static bool TryGetConstOfActualSlot(TypeShape parameterShape, TypeShape actualShape, out bool isConst)
	{
		isConst = false;
		if (parameterShape.Qualifiers.IsConst)
		{
			isConst = actualShape.Qualifiers.IsConst;
			return true;
		}
		if (parameterShape.Element is not null && actualShape.Element is not null)
			return TryGetConstOfActualSlot(parameterShape.Element, actualShape.Element, out isConst);
		return false;
	}

	static bool TryGetConstOfActualSlot(TypeReference? anchorType, TypeShape actualShape, out bool isConst)
	{
		isConst = false;
		if (anchorType is null)
			return false;

		switch (anchorType)
		{
			case PrimitiveTypeReference { Type: PrimitiveType.String or PrimitiveType.AString or PrimitiveType.WString }:
			case NamedTypeReference { Name: "string" or "astring" or "wstring" }:
			case TypeDefinitionReference { Name: "string" or "astring" or "wstring" }:
				isConst = true;
				return true;
			case ConstTypeReference:
				isConst = actualShape.Qualifiers.IsConst || actualShape.Element?.Qualifiers.IsConst == true;
				return true;
			case ConstOfTypeReference:
				return false;
			case ArrayTypeReference array when actualShape.Kind == TypeShapeKind.Array && actualShape.Element is not null:
				return TryGetConstOfActualSlot(array.ElementType, actualShape.Element, out isConst);
			case FixedArrayTypeReference fixedArray when actualShape.Kind == TypeShapeKind.FixedArray && actualShape.Element is not null:
				return TryGetConstOfActualSlot(fixedArray.ElementType, actualShape.Element, out isConst);
			case OptionalTypeReference optional when actualShape.Kind == TypeShapeKind.Optional && actualShape.Element is not null:
				return TryGetConstOfActualSlot(optional.ElementType, actualShape.Element, out isConst);
			case PointerTypeReference pointer when actualShape.Kind == TypeShapeKind.Pointer && actualShape.Element is not null:
				return TryGetConstOfActualSlot(pointer.ElementType, actualShape.Element, out isConst);
			case AttributedTypeReference attributed:
				return TryGetConstOfActualSlot(attributed.Type, actualShape, out isConst);
			case VolatileTypeReference vol:
				return TryGetConstOfActualSlot(vol.Type, actualShape, out isConst);
			case EscapedTypeReference escaped:
				return TryGetConstOfActualSlot(escaped.Type, actualShape, out isConst);
			case ScopedTypeReference scoped:
				return TryGetConstOfActualSlot(scoped.Type, actualShape, out isConst);
			case UnscopedTypeReference unscoped:
				return TryGetConstOfActualSlot(unscoped.Type, actualShape, out isConst);
			case TargetTypeSpecTypeReference targetSpec:
				return TryGetConstOfActualSlot(targetSpec.Type, actualShape, out isConst);
			default:
				return false;
		}
	}

	static bool IsStringLiteralLikeExpression(Expression? expression)
	{
		return expression is LiteralExpression { Kind: LiteralKind.String } or NameOfExpression;
	}

	void CheckConstOfProducedResult(TypeReference? targetType, Expression? expression, SyntaxNode? syntax, string context)
	{
		if (targetType is null || expression is null || !ContainsConstOfTypeReference(targetType))
			return;
		foreach (string anchor in GetConstOfAnchorNames(targetType).Distinct(System.StringComparer.Ordinal))
		{
			if (!ExpressionSatisfiesConstOfAnchor(expression, anchor)
				&& !ExpressionProvidesMutableConstOfSlot(targetType, expression, anchor))
					Report(GetRange(syntax), $"{context} with constof({anchor}) must be derived from '{anchor}' or use an explicit constof({anchor}) cast.");
			}
	}

	void CheckConstOfProducedResult(string? targetType, Expression? expression, SyntaxNode? syntax, string context)
	{
		if (targetType is null || expression is null || !targetType.Contains("constof(", StringComparison.Ordinal))
			return;
		foreach (string anchor in GetConstOfAnchorNames(targetType).Distinct(System.StringComparer.Ordinal))
		{
			if (!ExpressionSatisfiesConstOfAnchor(expression, anchor))
				Report(GetRange(syntax), $"{context} with constof({anchor}) must be derived from '{anchor}' or use an explicit constof({anchor}) cast.");
		}
	}

	bool ExpressionProvidesMutableConstOfSlot(TypeReference targetType, Expression expression, string anchor)
	{
		string actualType = expression.ResolvedType ?? ErrorType;
		if (actualType == ErrorType || actualType == TargetType)
			return false;
		if (!TryParseTypeShape(actualType, out TypeShape actualShape))
			return false;
		return TypeReferenceConstOfSlotIsMutable(targetType, actualShape, anchor);
	}

	static bool TypeReferenceConstOfSlotIsMutable(TypeReference? targetType, TypeShape actualShape, string anchor)
	{
		if (targetType is null)
			return false;

		switch (targetType)
		{
			case ConstOfTypeReference constOf:
				return constOf.AnchorName == anchor && !actualShape.Qualifiers.IsConst
					|| TypeReferenceConstOfSlotIsMutable(constOf.Type, actualShape, anchor);
			case AttributedTypeReference attributed:
				return TypeReferenceConstOfSlotIsMutable(attributed.Type, actualShape, anchor);
			case ConstTypeReference constant:
				return TypeReferenceConstOfSlotIsMutable(constant.Type, actualShape, anchor);
			case VolatileTypeReference vol:
				return TypeReferenceConstOfSlotIsMutable(vol.Type, actualShape, anchor);
			case EscapedTypeReference escaped:
				return TypeReferenceConstOfSlotIsMutable(escaped.Type, actualShape, anchor);
			case ScopedTypeReference scoped:
				return TypeReferenceConstOfSlotIsMutable(scoped.Type, actualShape, anchor);
			case UnscopedTypeReference unscoped:
				return TypeReferenceConstOfSlotIsMutable(unscoped.Type, actualShape, anchor);
			case TargetTypeSpecTypeReference targetSpec:
				return TypeReferenceConstOfSlotIsMutable(targetSpec.Type, actualShape, anchor);
			case PointerTypeReference pointer when actualShape.Kind == TypeShapeKind.Pointer && actualShape.Element is not null:
				return TypeReferenceConstOfSlotIsMutable(pointer.ElementType, actualShape.Element, anchor);
			case ArrayTypeReference array when actualShape.Kind == TypeShapeKind.Array && actualShape.Element is not null:
				return TypeReferenceConstOfSlotIsMutable(array.ElementType, actualShape.Element, anchor);
			case FixedArrayTypeReference fixedArray when actualShape.Kind == TypeShapeKind.FixedArray && actualShape.Element is not null:
				return TypeReferenceConstOfSlotIsMutable(fixedArray.ElementType, actualShape.Element, anchor);
			case OptionalTypeReference optional when actualShape.Kind == TypeShapeKind.Optional && actualShape.Element is not null:
				return TypeReferenceConstOfSlotIsMutable(optional.ElementType, actualShape.Element, anchor);
			default:
				return false;
		}
	}

	static IEnumerable<string> GetConstOfAnchorNames(TypeReference? type)
	{
		if (type is null)
			yield break;
		switch (type)
		{
			case ConstOfTypeReference constOf:
				yield return constOf.AnchorName;
				foreach (string anchor in GetConstOfAnchorNames(constOf.Type))
					yield return anchor;
				break;
			case AttributedTypeReference attributed:
				foreach (string anchor in GetConstOfAnchorNames(attributed.Type))
					yield return anchor;
				break;
			case GenericTypeReference generic:
				foreach (string anchor in GetConstOfAnchorNames(generic.Type))
					yield return anchor;
				foreach (TypeReference argument in generic.TypeArguments)
					foreach (string anchor in GetConstOfAnchorNames(argument))
						yield return anchor;
				break;
			case ArrayTypeReference array:
				foreach (string anchor in GetConstOfAnchorNames(array.ElementType))
					yield return anchor;
				break;
			case FixedArrayTypeReference fixedArray:
				foreach (string anchor in GetConstOfAnchorNames(fixedArray.ElementType))
					yield return anchor;
				break;
			case OptionalTypeReference optional:
				foreach (string anchor in GetConstOfAnchorNames(optional.ElementType))
					yield return anchor;
				break;
			case PointerTypeReference pointer:
				foreach (string anchor in GetConstOfAnchorNames(pointer.ElementType))
					yield return anchor;
				break;
			case ConstTypeReference constant:
				foreach (string anchor in GetConstOfAnchorNames(constant.Type))
					yield return anchor;
				break;
			case VolatileTypeReference vol:
				foreach (string anchor in GetConstOfAnchorNames(vol.Type))
					yield return anchor;
				break;
			case EscapedTypeReference escaped:
				foreach (string anchor in GetConstOfAnchorNames(escaped.Type))
					yield return anchor;
				break;
			case ScopedTypeReference scoped:
				foreach (string anchor in GetConstOfAnchorNames(scoped.Type))
					yield return anchor;
				break;
			case UnscopedTypeReference unscoped:
				foreach (string anchor in GetConstOfAnchorNames(unscoped.Type))
					yield return anchor;
				break;
			case TargetTypeSpecTypeReference targetSpec:
				foreach (string anchor in GetConstOfAnchorNames(targetSpec.Type))
					yield return anchor;
				break;
			case CallableTypeReference callable:
				foreach (string anchor in GetConstOfAnchorNames(callable.ReturnType))
					yield return anchor;
				foreach (ParameterDefinition parameter in callable.Parameters)
					foreach (string anchor in GetConstOfAnchorNames(parameter.Type))
						yield return anchor;
				break;
			case IterTypeReference iter:
				foreach (string anchor in GetConstOfAnchorNames(iter.ElementType))
					yield return anchor;
				foreach (ParameterDefinition parameter in iter.Parameters)
					foreach (string anchor in GetConstOfAnchorNames(parameter.Type))
						yield return anchor;
				break;
			case GroupedParamsTypeReference grouped:
				foreach (string anchor in GetConstOfAnchorNames(grouped.StructType))
					yield return anchor;
				break;
			case MaterializedStructTypeReference materialized:
				foreach (string anchor in GetConstOfAnchorNames(materialized.ParamsType))
					yield return anchor;
				break;
			case ThrownTypeReference thrown:
				foreach (string anchor in GetConstOfAnchorNames(thrown.Type))
					yield return anchor;
				break;
			case TypeDefinitionReference definition:
				foreach (TypeReference argument in definition.TypeArguments)
					foreach (string anchor in GetConstOfAnchorNames(argument))
						yield return anchor;
				break;
			case NamedTypeReference named:
				foreach (TypeReference argument in named.TypeArguments)
					foreach (string anchor in GetConstOfAnchorNames(argument))
						yield return anchor;
				break;
		}
	}

	static IEnumerable<string> GetConstOfAnchorNames(string type)
	{
		const string marker = "constof(";
		int index = 0;
		while (index < type.Length)
		{
			int start = type.IndexOf(marker, index, StringComparison.Ordinal);
			if (start < 0)
				yield break;
			int anchorStart = start + marker.Length;
			int anchorEnd = type.IndexOf(')', anchorStart);
			if (anchorEnd < 0)
				yield break;
			string anchor = type[anchorStart..anchorEnd].Trim();
			if (!string.IsNullOrWhiteSpace(anchor))
				yield return anchor;
			index = anchorEnd + 1;
		}
	}

	bool ExpressionSatisfiesConstOfAnchor(Expression? expression, string anchor)
	{
		expression = UnwrapParenthesizedExpression(expression);
		if (expression is not null && expressionRewrites.TryGetValue(expression, out Expression? rewrite) && !ReferenceEquals(rewrite, expression))
			return ExpressionSatisfiesConstOfAnchor(rewrite, anchor);
		return expression switch
		{
			null => false,
			CastExpression cast when TypeReferencesConstOfAnchor(cast.Type, anchor) => true,
			CastExpression cast => ExpressionSatisfiesConstOfAnchor(cast.Expression, anchor),
			VariableReferenceExpression { Variable: ParameterDefinition parameter } => parameter.Name == anchor,
			VariableReferenceExpression { Variable: DeclarationTarget target } => TypeReferencesConstOfAnchor(target.Type, anchor),
			VariableReferenceExpression { Variable: VariableDefinition variable } => TypeReferencesConstOfAnchor(variable.Type, anchor),
			NamedExpression { Qualifiers.Count: 0 } named => named.Name == anchor,
			ThisExpression => anchor == "this",
			MemberExpression member => ExpressionSatisfiesConstOfAnchor(member.Target, anchor),
			MemberReferenceExpression member => ExpressionSatisfiesConstOfAnchor(member.Target, anchor),
			IndexExpression index => ExpressionSatisfiesConstOfAnchor(index.Target, anchor),
			NamelessIndexerExpression indexer => ExpressionSatisfiesConstOfAnchor(indexer.Target, anchor),
			UnaryExpression { Operator: UnaryOperator.AddressOf or UnaryOperator.PointerDereference } unary => ExpressionSatisfiesConstOfAnchor(unary.Operand, anchor),
			AssignmentExpression assignment => ExpressionSatisfiesConstOfAnchor(assignment.Value, anchor),
			CallExpression call => CallExpressionSatisfiesConstOfAnchor(call, anchor),
			_ => false
		};
	}

	bool CallExpressionSatisfiesConstOfAnchor(CallExpression call, string anchor)
	{
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function))
			return false;
		foreach (string sourceAnchor in GetConstOfAnchorNames(function.ReturnType))
		{
			if (TryGetCallAnchorExpression(call, function, sourceAnchor, out Expression? argument)
				&& ExpressionSatisfiesConstOfAnchor(argument, anchor))
				return true;
		}
		return false;
	}

	bool TryGetCallAnchorExpression(CallExpression call, FunctionDefinition function, string sourceAnchor, out Expression? expression)
	{
		expression = null;
		if (sourceAnchor == "this")
		{
			expression = call.Target switch
			{
				MemberExpression member => member.Target,
				MemberReferenceExpression member => member.Target,
				_ => null
			};
			if (expression is not null)
				return true;
		}

		List<ParameterDefinition> parameters = function.Parameters;
		int parameterIndex = parameters.FindIndex(parameter => parameter.Name == sourceAnchor);
		if (parameterIndex < 0 || parameterIndex >= call.Arguments.Count)
			return false;
		expression = call.Arguments[parameterIndex].Value;
		return expression is not null;
	}

	static bool TypeReferencesConstOfAnchor(TypeReference? type, string anchor)
	{
		foreach (string candidate in GetConstOfAnchorNames(type))
			if (candidate == anchor)
				return true;
		return false;
	}

	string SubstituteConstOfResolvedType(TypeReference? sourceType, string resolvedType, Dictionary<string, bool> anchors, Dictionary<string, string>? genericSubstitutions = null)
	{
		if (sourceType is null || !ContainsConstOfTypeReference(sourceType))
			return resolvedType;
		TypeReference substituted = SubstituteConstOfTypeReference(sourceType, anchors);
		return SubstituteGenericType(FormatTypeReference(substituted), genericSubstitutions ?? []);
	}

	string SubstituteCallableConstOfReturnType(string callableType, string resolvedReturnType, Dictionary<string, bool> anchors)
	{
		if (!typeDefinitions.TryGetValue(BaseTypeName(callableType), out TypeDefinition? definition)
			|| definition is not NewtypeDefinition newtype)
			return resolvedReturnType;
		TypeReference? returnType = newtype.UnderlyingType switch
		{
			CallableTypeReference callable => callable.ReturnType,
			IterTypeReference iter => iter.ElementType,
			_ => null
		};
		return SubstituteConstOfResolvedType(returnType, resolvedReturnType, anchors);
	}

	static TypeReference SubstituteConstOfTypeReference(TypeReference type, Dictionary<string, bool> anchors)
	{
		return type switch
		{
			ConstOfTypeReference constOf => anchors.TryGetValue(constOf.AnchorName, out bool isConst) && isConst
				? new ConstTypeReference { Type = SubstituteConstOfTypeReference(constOf.Type ?? new PrimitiveTypeReference { Type = PrimitiveType.Void }, anchors) }
				: SubstituteConstOfTypeReference(constOf.Type ?? new PrimitiveTypeReference { Type = PrimitiveType.Void }, anchors),
			AttributedTypeReference attributed => new AttributedTypeReference { Type = attributed.Type is null ? null : SubstituteConstOfTypeReference(attributed.Type, anchors) },
			GenericTypeReference generic => CloneGenericWithConstOf(generic, anchors),
			ArrayTypeReference array => new ArrayTypeReference { ElementType = array.ElementType is null ? null : SubstituteConstOfTypeReference(array.ElementType, anchors) },
			FixedArrayTypeReference fixedArray => new FixedArrayTypeReference
			{
				ElementType = fixedArray.ElementType is null ? null : SubstituteConstOfTypeReference(fixedArray.ElementType, anchors),
				Length = fixedArray.Length
			},
			OptionalTypeReference optional => new OptionalTypeReference { ElementType = optional.ElementType is null ? null : SubstituteConstOfTypeReference(optional.ElementType, anchors) },
			PointerTypeReference pointer => new PointerTypeReference { ElementType = pointer.ElementType is null ? null : SubstituteConstOfTypeReference(pointer.ElementType, anchors) },
			ConstTypeReference constant => new ConstTypeReference { Type = constant.Type is null ? null : SubstituteConstOfTypeReference(constant.Type, anchors) },
			VolatileTypeReference vol => new VolatileTypeReference { Type = vol.Type is null ? null : SubstituteConstOfTypeReference(vol.Type, anchors) },
			EscapedTypeReference escaped => new EscapedTypeReference { Type = escaped.Type is null ? null : SubstituteConstOfTypeReference(escaped.Type, anchors) },
			ScopedTypeReference scoped => new ScopedTypeReference { Type = scoped.Type is null ? null : SubstituteConstOfTypeReference(scoped.Type, anchors) },
			UnscopedTypeReference unscoped => new UnscopedTypeReference { Type = unscoped.Type is null ? null : SubstituteConstOfTypeReference(unscoped.Type, anchors) },
			TargetTypeSpecTypeReference targetSpec => new TargetTypeSpecTypeReference
			{
				Specifier = targetSpec.Specifier,
				IsPrefix = targetSpec.IsPrefix,
				Type = targetSpec.Type is null ? null : SubstituteConstOfTypeReference(targetSpec.Type, anchors)
			},
			CallableTypeReference callable => CloneCallableWithConstOf(callable, anchors),
			IterTypeReference iter => CloneIterWithConstOf(iter, anchors),
			GroupedParamsTypeReference grouped => new GroupedParamsTypeReference { StructType = grouped.StructType is null ? null : SubstituteConstOfTypeReference(grouped.StructType, anchors) },
			MaterializedStructTypeReference materialized => new MaterializedStructTypeReference { ParamsType = materialized.ParamsType is null ? null : SubstituteConstOfTypeReference(materialized.ParamsType, anchors) },
			ThrownTypeReference thrown => new ThrownTypeReference { Type = thrown.Type is null ? null : SubstituteConstOfTypeReference(thrown.Type, anchors) },
			TypeDefinitionReference definition => CloneTypeDefinitionWithConstOf(definition, anchors),
			NamedTypeReference named => CloneNamedWithConstOf(named, anchors),
			PrimitiveTypeReference primitive => new PrimitiveTypeReference { Type = primitive.Type },
			_ => type
		};
	}

	static GenericTypeReference CloneGenericWithConstOf(GenericTypeReference generic, Dictionary<string, bool> anchors)
	{
		GenericTypeReference clone = new()
		{
			Type = generic.Type is null ? null : SubstituteConstOfTypeReference(generic.Type, anchors)
		};
		foreach (TypeReference argument in generic.TypeArguments)
			clone.TypeArguments.Add(SubstituteConstOfTypeReference(argument, anchors));
		return clone;
	}

	static TypeDefinitionReference CloneTypeDefinitionWithConstOf(TypeDefinitionReference definition, Dictionary<string, bool> anchors)
	{
		TypeDefinitionReference clone = new()
		{
			Definition = definition.Definition,
			Name = definition.Name
		};
		foreach (TypeReference argument in definition.TypeArguments)
			clone.TypeArguments.Add(SubstituteConstOfTypeReference(argument, anchors));
		return clone;
	}

	static NamedTypeReference CloneNamedWithConstOf(NamedTypeReference named, Dictionary<string, bool> anchors)
	{
		NamedTypeReference clone = new()
		{
			Name = named.Name
		};
		foreach (TypeReference argument in named.TypeArguments)
			clone.TypeArguments.Add(SubstituteConstOfTypeReference(argument, anchors));
		foreach (string qualifier in named.Qualifiers)
			clone.Qualifiers.Add(qualifier);
		return clone;
	}

	static CallableTypeReference CloneCallableWithConstOf(CallableTypeReference callable, Dictionary<string, bool> anchors)
	{
		CallableTypeReference clone = new()
		{
			Kind = callable.Kind,
			TargetSpec = callable.TargetSpec,
			CallSpec = callable.CallSpec,
			ReturnType = callable.ReturnType is null ? null : SubstituteConstOfTypeReference(callable.ReturnType, anchors)
		};
		foreach (ParameterDefinition parameter in callable.Parameters)
			clone.Parameters.Add(CloneParameterWithConstOf(parameter, anchors));
		return clone;
	}

	static IterTypeReference CloneIterWithConstOf(IterTypeReference iter, Dictionary<string, bool> anchors)
	{
		IterTypeReference clone = new()
		{
			IsAsync = iter.IsAsync,
			ElementType = iter.ElementType is null ? null : SubstituteConstOfTypeReference(iter.ElementType, anchors)
		};
		foreach (ParameterDefinition parameter in iter.Parameters)
			clone.Parameters.Add(CloneParameterWithConstOf(parameter, anchors));
		return clone;
	}

	static ParameterDefinition CloneParameterWithConstOf(ParameterDefinition parameter, Dictionary<string, bool> anchors)
	{
		ParameterDefinition clone = parameter switch
		{
			ThisParameterDefinition => new ThisParameterDefinition(),
			SizeOfParameterDefinition => new SizeOfParameterDefinition(),
			VTableOfParameterDefinition => new VTableOfParameterDefinition(),
			NameOfParameterDefinition => new NameOfParameterDefinition(),
			WithinParameterDefinition => new WithinParameterDefinition(),
			_ => new ParameterDefinition()
		};
		clone.Name = parameter.Name;
		clone.Modifier = parameter.Modifier;
		clone.Type = parameter.Type is null ? null : SubstituteConstOfTypeReference(parameter.Type, anchors);
		clone.ResolvedType = clone.Type is null ? parameter.ResolvedType : FormatTypeReference(clone.Type);
		return clone;
	}
}
