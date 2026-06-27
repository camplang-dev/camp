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
}
