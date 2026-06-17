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
				if (TryApplyGenericTypeArguments(generic.Type, generic.TypeArguments, out TypeReference? appliedGenericType) && appliedGenericType is not null)
				{
					AnalyzeOptionalType(appliedGenericType, scope);
					type.ResolvedType = appliedGenericType.ResolvedType ?? FormatTypeReference(appliedGenericType);
					typeRewrites[generic] = appliedGenericType;
				}
				else
				{
					AnalyzeOptionalType(generic.Type, scope);
					AnalyzeTypeList(generic.TypeArguments, scope);
					type.ResolvedType = FormatTypeReference(type);
				}
				break;

			case ArrayTypeReference array:
				AnalyzeOptionalType(array.ElementType, scope);
				if (IsExpandedFormType(array.ElementType))
					Report(GetRange(array.SourceSyntax), $"Arrays of expanded values are not supported; use struct({FormatTypeReference(array.ElementType)})[] instead.");
				type.ResolvedType = FormatTypeReference(type);
				break;

			case FixedArrayTypeReference fixedArray:
				AnalyzeOptionalType(fixedArray.ElementType, scope);
				if (IsExpandedFormType(fixedArray.ElementType))
					Report(GetRange(fixedArray.SourceSyntax), $"Fixed-size arrays of expanded values are not supported; use struct({FormatTypeReference(fixedArray.ElementType)})[{FormatFixedArrayLength(fixedArray)}] instead.");
				if (TryEvaluateFixedArrayLength(fixedArray.LengthExpression, scope, out long length))
				{
					fixedArray.Length = length;
					if (length < 0)
						Report(GetRange(fixedArray.LengthExpression?.SourceSyntax ?? fixedArray.SourceSyntax), "Fixed-size array length cannot be negative.");
				}
				else
				{
					Report(GetRange(fixedArray.LengthExpression?.SourceSyntax ?? fixedArray.SourceSyntax), "Fixed-size array length must be a compile-time integer constant.");
				}
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

			case CopyableTypeReference:
				type.ResolvedType = "copyable";
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
				ValidateNoDirectFixedArrayType(callable.ReturnType, callable.ReturnType?.SourceSyntax ?? callable.SourceSyntax, "a callable return type");
				foreach (ParameterDefinition parameter in callable.Parameters)
				{
					AnalyzeParameterDefinition(parameter, scope);
					ValidateNoDirectFixedArrayType(parameter.Type, parameter.Type?.SourceSyntax ?? parameter.SourceSyntax, "a callable parameter type");
				}
				type.ResolvedType = FormatTypeReference(type);
				break;

			case IterTypeReference iter:
				AnalyzeOptionalType(iter.ElementType, scope);
				ValidateNoDirectFixedArrayType(iter.ElementType, iter.ElementType?.SourceSyntax ?? iter.SourceSyntax, "an iterator yield type");
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

	static bool TryApplyGenericTypeArguments(TypeReference? type, List<TypeReference> arguments, out TypeReference? appliedType)
	{
		appliedType = type;
		if (type is null || arguments.Count == 0)
			return false;

		if (type is NamedTypeReference named)
		{
			if (named.TypeArguments.Count > 0)
				return false;

			foreach (TypeReference argument in arguments)
				named.TypeArguments.Add(argument);
			arguments.Clear();
			return true;
		}

		TypeReference? inner = type switch
		{
			AttributedTypeReference attributed => attributed.Type,
			ConstTypeReference constant => constant.Type,
			VolatileTypeReference vol => vol.Type,
			EscapedTypeReference escaped => escaped.Type,
			ScopedTypeReference scoped => scoped.Type,
			UnscopedTypeReference unscoped => unscoped.Type,
			TargetTypeSpecTypeReference targetSpec => targetSpec.Type,
			_ => null
		};

		return inner is not null && TryApplyGenericTypeArguments(inner, arguments, out _);
	}

	static bool TryEvaluateFixedArrayLength(Expression? expression, AnalysisScope scope, out long length)
	{
		length = 0;
		expression = UnwrapConstantExpressionSyntax(expression);
		switch (expression)
		{
			case LiteralExpression literal:
				return TryParseIntegerConstant(literal.Text, out length);

			case UnaryExpression { Operator: UnaryOperator.Plus } unary:
				return TryEvaluateFixedArrayLength(unary.Operand, scope, out length);

			case UnaryExpression { Operator: UnaryOperator.Minus } unary:
				if (!TryEvaluateFixedArrayLength(unary.Operand, scope, out long operand))
					return false;
				length = -operand;
				return true;

			case CastExpression cast:
				return TryEvaluateFixedArrayLength(cast.Expression, scope, out length);

			default:
				return false;
		}
	}

	static Expression? UnwrapConstantExpressionSyntax(Expression? expression)
	{
		while (expression is GroupedExpression grouped)
		{
			if (grouped.Items.Count != 1 || grouped.Items[0].Name is not null)
				return expression;
			expression = grouped.Items[0].Expression;
		}
		return expression;
	}

	static bool TryParseIntegerConstant(string text, out long value)
	{
		value = 0;
		if (string.IsNullOrWhiteSpace(text))
			return false;

		text = text.Replace("_", "", System.StringComparison.Ordinal).Trim();
		if (text.Length == 0)
			return false;

		while (text.Length > 0 && char.IsLetter(text[^1]) && text[^1] is not 'x' and not 'X')
			text = text[..^1];

		if (text.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
			return long.TryParse(text[2..], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out value);

		return long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
	}

	static bool IsExpandedFormType(TypeReference? type)
	{
		if (type is null)
			return false;

		type = UnwrapTypeDeclarators(type);
		return type is ArrayTypeReference
			or OptionalTypeReference
			or CallableTypeReference { Kind: CallableKind.Delegate or CallableKind.Once or CallableKind.Async }
			or IterTypeReference;
	}

	static bool IsDirectFixedArrayType(TypeReference? type)
	{
		return type is not null && UnwrapTypeDeclarators(type) is FixedArrayTypeReference;
	}

	static FixedArrayTypeReference? AsDirectFixedArrayType(TypeReference? type)
	{
		return type is null ? null : UnwrapTypeDeclarators(type) as FixedArrayTypeReference;
	}

	void ValidateFixedStorageMarker(TypeReference? type, bool isFixedStorage, SyntaxNode? syntax)
	{
		bool isFixedArray = IsDirectFixedArrayType(type);
		if (isFixedStorage && !isFixedArray)
			Report(GetRange(syntax), "'fixed' is only valid on fixed-size array storage declarations.");
		else if (!isFixedStorage && isFixedArray)
			Report(GetRange(syntax), "Fixed-size array storage declarations must be marked 'fixed'.");
	}

	void ValidateNoDirectFixedArrayType(TypeReference? type, SyntaxNode? syntax, string usage)
	{
		if (IsDirectFixedArrayType(type))
			Report(GetRange(syntax), $"Fixed-size array storage types are not valid as {usage}; use a pointer to the fixed array or a span array instead.");
	}

	void ValidateTargetCallSpec(string? callSpec, SyntaxNode? syntax)
	{
		if (string.IsNullOrWhiteSpace(callSpec))
			return;

		callSpec = ResolveCallSpecAlias(callSpec, syntax);
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

		spec = TryResolveAlias(spec, AliasTargetKind.CallSpec, syntax, out AliasDefinition? callAlias)
			? callAlias!.ResolvedTargetName
			: TryResolveAlias(spec, AliasTargetKind.TypeSpec, syntax, out AliasDefinition? typeAlias)
				? typeAlias!.ResolvedTargetName
				: spec;

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

		typeSpec.Specifier = ResolveTypeSpecAlias(typeSpec.Specifier, typeSpec.SourceSyntax);
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

		if (named.Qualifiers.Count == 0 && named.TypeArguments.Count == 0 && TryResolveAlias(named.Name, AliasTargetKind.Type, named.SourceSyntax, out AliasDefinition? alias))
		{
			if (TryGetPrimitiveType(alias!.ResolvedTargetName, out PrimitiveType primitive))
			{
				PrimitiveTypeReference primitiveReference = new()
				{
					SourceSyntax = named.SourceSyntax,
					Type = primitive,
					ResolvedType = alias.ResolvedTargetName
				};
				typeRewrites[named] = primitiveReference;
				return alias.ResolvedTargetName;
			}

			if (typeDefinitions.TryGetValue(alias.ResolvedTargetName, out TypeDefinition? aliasType))
			{
				TypeDefinitionReference reference = new()
				{
					SourceSyntax = named.SourceSyntax,
					Name = aliasType.Name,
					Definition = aliasType,
					ResolvedType = aliasType.Name
				};
				typeRewrites[named] = reference;
				return aliasType.Name;
			}
		}

		if (named.Qualifiers.Count == 0 && named.TypeArguments.Count > 0 && aliasDefinitions.ContainsKey(named.Name))
			Report(GetRange(named.SourceSyntax), $"Alias '{named.Name}' cannot be used with generic type arguments.");

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
