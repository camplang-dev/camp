using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	string? AnalyzeOptionalType(TypeReference? type, AnalysisScope scope)
	{
		if (type is null)
			return null;

		AnalyzeType(type, scope);
		return type.ResolvedType;
	}

	void AnalyzeTypeList(List<TypeReference> types, AnalysisScope scope)
	{
		foreach (TypeReference type in types)
			AnalyzeType(type, scope);
	}

	void AnalyzeType(TypeReference type, AnalysisScope scope)
	{
		switch (type)
		{
			case TypeDefinitionReference definition:
				AnalyzeTypeList(definition.TypeArguments, scope);
				type.ResolvedType = AddTypeArguments(definition.Name, definition.TypeArguments);
				break;

			case GenericParameterTypeReference genericParameter:
				type.ResolvedType = genericParameter.Name;
				break;

			case AllocatorTypeReference:
				type.ResolvedType = AllocatorType;
				break;

			case NamedTypeReference named:
				foreach (TypeReference argument in named.TypeArguments)
					AnalyzeType(argument, scope);

				type.ResolvedType = ResolveNamedType(named, scope);
				ValidateGenericArgumentUse(named);
				break;

			case AttributedTypeReference attributed:
				AnalyzeOptionalType(attributed.Type, scope);
				if (attributed.Attribute is not null)
					AnalyzeAttribute(attributed.Attribute);
				type.ResolvedType = attributed.Type?.ResolvedType ?? ErrorType;
				break;

			case GenericTypeReference generic:
				AnalyzeOptionalType(generic.Type, scope);
				AnalyzeTypeList(generic.TypeArguments, scope);
				type.ResolvedType = FormatTypeReference(type);
				break;

			case ArrayTypeReference array:
				AnalyzeOptionalType(array.ElementType, scope);
				if (IsExpandedFormType(array.ElementType))
					Report(GetRange(array.SourceSyntax), $"Arrays of expanded values are not supported; use struct({FormatTypeReference(array.ElementType)})[] instead.");
				type.ResolvedType = FormatTypeReference(type);
				break;

			case OptionalTypeReference optional:
				AnalyzeOptionalType(optional.ElementType, scope);
				if (optional.ElementType is OptionalTypeReference)
					Report(GetRange(optional.SourceSyntax), "Optional values may not directly contain another optional; use struct(T?)? if materialized nesting is required.");
				type.ResolvedType = FormatTypeReference(type);
				break;

			case PointerTypeReference pointer:
				AnalyzeOptionalType(pointer.ElementType, scope);
				type.ResolvedType = FormatTypeReference(type);
				break;

			case ConstTypeReference constType:
				AnalyzeOptionalType(constType.Type, scope);
				type.ResolvedType = FormatTypeReference(type);
				break;

			case VolatileTypeReference volatileType:
				AnalyzeOptionalType(volatileType.Type, scope);
				type.ResolvedType = FormatTypeReference(type);
				break;

			case AnyTypeReference:
				type.ResolvedType = "any";
				break;

			case AutoTypeReference:
				type.ResolvedType = AutoType;
				break;

			case PrimitiveTypeReference primitive:
				type.ResolvedType = GetPrimitiveTypeName(primitive.Type);
				if (selectedTarget?.IsPrimitiveUnsupported(type.ResolvedType) == true)
					Report(GetRange(type.SourceSyntax), $"Primitive type '{type.ResolvedType}' is not supported by target '{selectedTarget.Name}'.");
				break;

			case EscapedTypeReference escaped:
				AnalyzeOptionalType(escaped.Type, scope);
				type.ResolvedType = FormatTypeReference(type);
				break;

			case ScopedTypeReference scoped:
				AnalyzeOptionalType(scoped.Type, scope);
				type.ResolvedType = FormatTypeReference(type);
				break;

			case UnscopedTypeReference unscoped:
				AnalyzeOptionalType(unscoped.Type, scope);
				type.ResolvedType = FormatTypeReference(type);
				break;

			case TargetTypeSpecTypeReference targetSpec:
				AnalyzeOptionalType(targetSpec.Type, scope);
				ValidateTargetTypeSpec(targetSpec);
				type.ResolvedType = FormatTypeReference(type);
				break;

			case CallableTypeReference callable:
				ValidateCallableSpec(callable);
				AnalyzeOptionalType(callable.ReturnType, scope);
				foreach (ParameterDefinition parameter in callable.Parameters)
					AnalyzeParameterDefinition(parameter, scope);
				type.ResolvedType = FormatTypeReference(type);
				break;

			case IterTypeReference iter:
				AnalyzeOptionalType(iter.ElementType, scope);
				ValidateIteratorType(iter, scope);
				type.ResolvedType = FormatTypeReference(type);
				break;

			case GroupedParamsTypeReference grouped:
				AnalyzeOptionalType(grouped.StructType, scope);
				Report(GetRange(grouped.SourceSyntax), "params(T) type syntax is no longer supported; use an expanded built-in form or struct(T).");
				type.ResolvedType = FormatTypeReference(type);
				break;

			case MaterializedStructTypeReference materialized:
				AnalyzeOptionalType(materialized.ParamsType, scope);
				if (!IsExpandedFormType(materialized.ParamsType))
					Report(GetRange(materialized.SourceSyntax), "struct(T) materialization requires an expanded array, optional, or delegate type.");
				type.ResolvedType = FormatTypeReference(type);
				break;

			case ThrownTypeReference thrown:
				AnalyzeOptionalType(thrown.Type, scope);
				type.ResolvedType = FormatTypeReference(type);
				break;

			default:
				type.ResolvedType = ErrorType;
				break;
		}
	}

	static bool IsExpandedFormType(TypeReference? type)
	{
		if (type is null)
			return false;

		type = UnwrapTypeDeclarators(type);
		return type is ArrayTypeReference
			or OptionalTypeReference
			or CallableTypeReference { Kind: CallableKind.Delegate or CallableKind.Once or CallableKind.Async };
	}

	void ValidateTargetCallSpec(string? callSpec, SyntaxNode? syntax)
	{
		if (string.IsNullOrWhiteSpace(callSpec))
			return;

		if (selectedTarget is null || !selectedTarget.HasCallSpec(callSpec))
			Report(GetRange(syntax), $"Callspec '{callSpec}' is not defined by target '{selectedTarget?.Name ?? "#NONE"}'.");
	}

	void ValidateCallableSpec(CallableTypeReference callable)
	{
		if (string.IsNullOrWhiteSpace(callable.CallSpec) && string.IsNullOrWhiteSpace(callable.TargetSpec))
			return;

		string? normalizedCallSpec = null;
		string? normalizedTargetSpec = null;
		ClassifyCallableSpec(callable.CallSpec, callable.SourceSyntax, ref normalizedCallSpec, ref normalizedTargetSpec);
		ClassifyCallableSpec(callable.TargetSpec, callable.SourceSyntax, ref normalizedCallSpec, ref normalizedTargetSpec);

		callable.CallSpec = normalizedCallSpec;
		callable.TargetSpec = normalizedTargetSpec;
	}

	void ClassifyCallableSpec(string? spec, SyntaxNode? syntax, ref string? callSpec, ref string? targetSpec)
	{
		if (string.IsNullOrWhiteSpace(spec))
			return;

		if (selectedTarget?.HasCallSpec(spec) == true)
		{
			if (callSpec is not null && callSpec != spec)
				Report(GetRange(syntax), $"Callable type has multiple callspecs: '{callSpec}' and '{spec}'.");
			callSpec = spec;
			return;
		}

		if (selectedTarget?.HasTypeSpec(spec) == true)
		{
			if (targetSpec is not null && targetSpec != spec)
				Report(GetRange(syntax), $"Callable type has multiple target typespecs: '{targetSpec}' and '{spec}'.");
			targetSpec = spec;
			return;
		}

		Report(GetRange(syntax), $"Callspec or typespec '{spec}' is not defined by target '{selectedTarget?.Name ?? "#NONE"}'.");
	}

	void ValidateIteratorType(IterTypeReference iter, AnalysisScope scope)
	{
		if (iter.Parameters.Count == 0)
		{
			if (iter.ElementType is null)
				Report(GetRange(iter.SourceSyntax), "Iter type is missing a yielded type.");
			return;
		}

		int yieldedCount = 0;
		int thrownCount = 0;
		foreach (ParameterDefinition parameter in iter.Parameters)
		{
			if (parameter is ThisParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition or WithinParameterDefinition
				|| parameter.Modifier is ParameterModifier.In or ParameterModifier.Out or ParameterModifier.Within)
			{
				Report(GetIteratorSlotRange(parameter), "Iterator result slots may only be yielded value slots or a thrown slot.");
				continue;
			}

			AnalyzeParameterDefinition(parameter, scope);
			if (parameter.Modifier == ParameterModifier.Thrown)
			{
				thrownCount++;
				if (yieldedCount == 0)
					Report(GetIteratorSlotRange(parameter), "Iterator thrown slot must follow at least one yielded value slot.");
				if (thrownCount > 1)
					Report(GetIteratorSlotRange(parameter), "Iterator type may declare at most one thrown slot.");
			}
			else
			{
				if (thrownCount > 0)
					Report(GetIteratorSlotRange(parameter), "Iterator yielded value slots must appear before the thrown slot.");
				yieldedCount++;
			}
		}

		if (yieldedCount == 0)
			Report(GetRange(iter.SourceSyntax), "Iter type is missing a yielded type.");
	}

	static TokenRange? GetIteratorSlotRange(ParameterDefinition parameter)
	{
		return GetNameRange(parameter) ?? GetRange(parameter.SourceSyntax);
	}

	void ValidateTargetTypeSpec(TargetTypeSpecTypeReference typeSpec)
	{
		if (string.IsNullOrWhiteSpace(typeSpec.Specifier))
			return;

		if (selectedTarget is null || !selectedTarget.HasTypeSpec(typeSpec.Specifier))
		{
			Report(GetRange(typeSpec.SourceSyntax), $"Typespec '{typeSpec.Specifier}' is not defined by target '{selectedTarget?.Name ?? "#NONE"}'.");
			return;
		}

		if (typeSpec.IsPrefix)
		{
			Report(GetRange(typeSpec.SourceSyntax), $"Typespec '{typeSpec.Specifier}' must appear after the type form it modifies.");
			return;
		}

		TypeReference? inner = typeSpec.Type;
		if (inner is PrimitiveTypeReference { Type: PrimitiveType.NInt or PrimitiveType.NUInt or PrimitiveType.String or PrimitiveType.WString or PrimitiveType.AString })
			return;

		inner = UnwrapTypeDeclarators(inner ?? typeSpec);
		if (inner is PointerTypeReference or ArrayTypeReference or OptionalTypeReference or CallableTypeReference or GenericTypeReference)
			return;

		Report(GetRange(typeSpec.SourceSyntax), $"Typespec '{typeSpec.Specifier}' cannot be applied to type '{FormatTypeReference(typeSpec.Type)}'.");
	}

	string ResolveNamedType(NamedTypeReference named, AnalysisScope scope)
	{
		string sourceName = BuildNamedTypeSourceName(named);

		if (named.Qualifiers.Count == 0 && selectedTarget is not null && selectedTarget.HasTypeSpec(named.Name))
		{
			Report(GetRange(named.SourceSyntax), $"Typespec '{named.Name}' must appear after the type form it modifies.");
			return $"{UnresolvedType}({sourceName})";
		}

		if (named.Qualifiers.Count == 0 && scope.TryGetGenericParameter(named.Name, out GenericParameter? genericParameter))
		{
			string resolvedType = AddTypeArguments(named.Name, named.TypeArguments);
			typeRewrites[named] = new GenericParameterTypeReference
			{
				SourceSyntax = named.SourceSyntax,
				Name = named.Name,
				Parameter = genericParameter,
				ResolvedType = resolvedType
			};
			return resolvedType;
		}

		if (named.Qualifiers.Count == 0 && typeDefinitions.TryGetValue(named.Name, out TypeDefinition? definition))
		{
			if (!IsDefinitionVisible(definition, named.SourceSyntax))
			{
				ReportNotExported(definition, named.SourceSyntax, "Type");
				return $"{UnresolvedType}({sourceName})";
			}

			ValidateGenericArity(named, definition);
			string resolvedType = AddTypeArguments(named.Name, named.TypeArguments);
			TypeDefinitionReference reference = new()
			{
				SourceSyntax = named.SourceSyntax,
				Name = named.Name,
				Definition = definition,
				ResolvedType = resolvedType
			};
			foreach (TypeReference argument in named.TypeArguments)
				reference.TypeArguments.Add(argument);
			typeRewrites[named] = reference;
			return resolvedType;
		}

		if (named.Name == "<missing>")
			return MissingTypeName;

		Report(GetRange(named.SourceSyntax), $"Unknown type '{sourceName}'.");
		return $"{UnresolvedType}({sourceName})";
	}
}
