using System.Collections.Generic;
using System.Linq;

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

			case ClassTypeReference classType:
				if (scope.GetContainingType() is ClassDefinition classDefinition)
				{
					classType.Definition = classDefinition;
					type.ResolvedType = classDefinition.Name;
				}
				else
				{
					Report(GetRange(type.SourceSyntax), "'classtype' is valid only inside class declarations.");
					type.ResolvedType = $"{UnresolvedType}(classtype)";
				}
				break;

			case ThisTypeReference:
				type.ResolvedType = "this";
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
				if (IsDirectFixedArrayType(array.ElementType))
					Report(GetRange(array.SourceSyntax), "Arrays whose element type is fixed-size array storage are not supported; use a pointer-to-fixed-array element type instead.");
				type.ResolvedType = FormatTypeReference(type);
				break;

			case FixedArrayTypeReference fixedArray:
				AnalyzeOptionalType(fixedArray.ElementType, scope);
				if (IsExpandedFormType(fixedArray.ElementType))
					Report(GetRange(fixedArray.SourceSyntax), $"Fixed-size arrays of expanded values are not supported; use struct({FormatTypeReference(fixedArray.ElementType)})[{FormatFixedArrayLength(fixedArray)}] instead.");
				if (TryEvaluateFixedArrayLength(fixedArray.LengthExpression, scope, out long length))
				{
					fixedArray.Length = length;
					fixedArray.LengthExpression = new LiteralExpression
					{
						SourceSyntax = fixedArray.LengthExpression?.SourceSyntax,
						Kind = LiteralKind.Number,
						Text = length.ToString(System.Globalization.CultureInfo.InvariantCulture),
						Value = length.ToString(System.Globalization.CultureInfo.InvariantCulture),
						ResolvedType = "nuint"
					};
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
				type.ResolvedType = pointer.ElementType is ClassTypeReference
					? $"{pointer.ElementType.ResolvedType}*"
					: FormatTypeReference(type);
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
				BindLifetime(escaped, "escaped", [], "explicit", scope);
				type.ResolvedType = FormatTypeReference(type);
				break;

			case ScopedTypeReference scoped:
				AnalyzeOptionalType(scoped.Type, scope);
				BindLifetime(scoped, "scoped", scoped.Anchors, scoped.Anchors.Count == 0 ? "explicit" : "explicit anchors", scope);
				type.ResolvedType = FormatTypeReference(type);
				break;

			case UnscopedTypeReference unscoped:
				AnalyzeOptionalType(unscoped.Type, scope);
				BindLifetime(unscoped, "unscoped", unscoped.Anchors, unscoped.Anchors.Count == 0 ? "explicit" : "explicit anchors", scope);
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

	static bool ContainsLifetimeAnnotation(TypeReference? type)
	{
		return type switch
		{
			null => false,
			EscapedTypeReference or ScopedTypeReference or UnscopedTypeReference => true,
			AttributedTypeReference attributed => ContainsLifetimeAnnotation(attributed.Type),
			GenericTypeReference generic => ContainsLifetimeAnnotation(generic.Type) || generic.TypeArguments.Any(ContainsLifetimeAnnotation),
			ArrayTypeReference array => ContainsLifetimeAnnotation(array.ElementType),
			FixedArrayTypeReference fixedArray => ContainsLifetimeAnnotation(fixedArray.ElementType),
			OptionalTypeReference optional => ContainsLifetimeAnnotation(optional.ElementType),
			PointerTypeReference pointer => ContainsLifetimeAnnotation(pointer.ElementType),
			ConstTypeReference constType => ContainsLifetimeAnnotation(constType.Type),
			VolatileTypeReference volatileType => ContainsLifetimeAnnotation(volatileType.Type),
			CallableTypeReference callable => ContainsLifetimeAnnotation(callable.ReturnType) || callable.Parameters.Any(static parameter => ContainsLifetimeAnnotation(parameter.Type)),
			TargetTypeSpecTypeReference targetSpec => ContainsLifetimeAnnotation(targetSpec.Type),
			IterTypeReference iter => ContainsLifetimeAnnotation(iter.ElementType) || iter.Parameters.Any(static parameter => ContainsLifetimeAnnotation(parameter.Type)),
			GroupedParamsTypeReference grouped => ContainsLifetimeAnnotation(grouped.StructType),
			MaterializedStructTypeReference materialized => ContainsLifetimeAnnotation(materialized.ParamsType),
			ThrownTypeReference thrown => ContainsLifetimeAnnotation(thrown.Type),
			TypeDefinitionReference definition => definition.TypeArguments.Any(ContainsLifetimeAnnotation),
			NamedTypeReference named => named.TypeArguments.Any(ContainsLifetimeAnnotation),
			_ => false
		};
	}

	static bool ContainsClassTypeReference(TypeReference? type)
	{
		return type switch
		{
			null => false,
			ClassTypeReference => true,
			ThisTypeReference => false,
			AttributedTypeReference attributed => ContainsClassTypeReference(attributed.Type),
			GenericTypeReference generic => ContainsClassTypeReference(generic.Type) || generic.TypeArguments.Any(ContainsClassTypeReference),
			ArrayTypeReference array => ContainsClassTypeReference(array.ElementType),
			FixedArrayTypeReference fixedArray => ContainsClassTypeReference(fixedArray.ElementType),
			OptionalTypeReference optional => ContainsClassTypeReference(optional.ElementType),
			PointerTypeReference pointer => ContainsClassTypeReference(pointer.ElementType),
			ConstTypeReference constType => ContainsClassTypeReference(constType.Type),
			VolatileTypeReference volatileType => ContainsClassTypeReference(volatileType.Type),
			CallableTypeReference callable => ContainsClassTypeReference(callable.ReturnType) || callable.Parameters.Any(static parameter => ContainsClassTypeReference(parameter.Type)),
			TargetTypeSpecTypeReference targetSpec => ContainsClassTypeReference(targetSpec.Type),
			IterTypeReference iter => ContainsClassTypeReference(iter.ElementType) || iter.Parameters.Any(static parameter => ContainsClassTypeReference(parameter.Type)),
			GroupedParamsTypeReference grouped => ContainsClassTypeReference(grouped.StructType),
			MaterializedStructTypeReference materialized => ContainsClassTypeReference(materialized.ParamsType),
			ThrownTypeReference thrown => ContainsClassTypeReference(thrown.Type),
			TypeDefinitionReference definition => definition.TypeArguments.Any(ContainsClassTypeReference),
			NamedTypeReference named => named.TypeArguments.Any(ContainsClassTypeReference),
			_ => false
		};
	}

	static bool ContainsThisTypeReference(TypeReference? type)
	{
		return type switch
		{
			null => false,
			ThisTypeReference => true,
			AttributedTypeReference attributed => ContainsThisTypeReference(attributed.Type),
			GenericTypeReference generic => ContainsThisTypeReference(generic.Type) || generic.TypeArguments.Any(ContainsThisTypeReference),
			ArrayTypeReference array => ContainsThisTypeReference(array.ElementType),
			FixedArrayTypeReference fixedArray => ContainsThisTypeReference(fixedArray.ElementType),
			OptionalTypeReference optional => ContainsThisTypeReference(optional.ElementType),
			PointerTypeReference pointer => ContainsThisTypeReference(pointer.ElementType),
			ConstTypeReference constType => ContainsThisTypeReference(constType.Type),
			VolatileTypeReference volatileType => ContainsThisTypeReference(volatileType.Type),
			EscapedTypeReference escaped => ContainsThisTypeReference(escaped.Type),
			ScopedTypeReference scoped => ContainsThisTypeReference(scoped.Type),
			UnscopedTypeReference unscoped => ContainsThisTypeReference(unscoped.Type),
			CallableTypeReference callable => ContainsThisTypeReference(callable.ReturnType) || callable.Parameters.Any(static parameter => ContainsThisTypeReference(parameter.Type)),
			TargetTypeSpecTypeReference targetSpec => ContainsThisTypeReference(targetSpec.Type),
			IterTypeReference iter => ContainsThisTypeReference(iter.ElementType) || iter.Parameters.Any(static parameter => ContainsThisTypeReference(parameter.Type)),
			GroupedParamsTypeReference grouped => ContainsThisTypeReference(grouped.StructType),
			MaterializedStructTypeReference materialized => ContainsThisTypeReference(materialized.ParamsType),
			ThrownTypeReference thrown => ContainsThisTypeReference(thrown.Type),
			TypeDefinitionReference definition => definition.TypeArguments.Any(ContainsThisTypeReference),
			NamedTypeReference named => named.TypeArguments.Any(ContainsThisTypeReference),
			_ => false
		};
	}

	bool TryGetLifetimeAnnotation(TypeReference? type, out string kind, out IReadOnlyList<string> anchors, out string? binding)
	{
		switch (type)
		{
			case EscapedTypeReference escaped:
				kind = "escaped";
				anchors = [];
				binding = escaped.LifetimeBinding;
				return true;

			case ScopedTypeReference scoped:
				kind = "scoped";
				anchors = scoped.Anchors;
				binding = scoped.LifetimeBinding;
				return true;

			case UnscopedTypeReference unscoped:
				kind = "unscoped";
				anchors = unscoped.Anchors;
				binding = unscoped.LifetimeBinding;
				return true;

			case AttributedTypeReference attributed:
				return TryGetLifetimeAnnotation(attributed.Type, out kind, out anchors, out binding);

			case GenericTypeReference generic:
				return TryGetLifetimeAnnotation(generic.Type, out kind, out anchors, out binding)
					|| TryGetLifetimeAnnotation(generic.TypeArguments, out kind, out anchors, out binding);

			case ArrayTypeReference array:
				return TryGetLifetimeAnnotation(array.ElementType, out kind, out anchors, out binding);

			case FixedArrayTypeReference fixedArray:
				return TryGetLifetimeAnnotation(fixedArray.ElementType, out kind, out anchors, out binding);

			case OptionalTypeReference optional:
				return TryGetLifetimeAnnotation(optional.ElementType, out kind, out anchors, out binding);

			case PointerTypeReference pointer:
				return TryGetLifetimeAnnotation(pointer.ElementType, out kind, out anchors, out binding);

			case ConstTypeReference constType:
				return TryGetLifetimeAnnotation(constType.Type, out kind, out anchors, out binding);

			case VolatileTypeReference volatileType:
				return TryGetLifetimeAnnotation(volatileType.Type, out kind, out anchors, out binding);

			case CallableTypeReference callable:
				return TryGetLifetimeAnnotation(callable.ReturnType, out kind, out anchors, out binding)
					|| TryGetLifetimeAnnotation(callable.Parameters.Select(static parameter => parameter.Type), out kind, out anchors, out binding);

			case TargetTypeSpecTypeReference targetSpec:
				return TryGetLifetimeAnnotation(targetSpec.Type, out kind, out anchors, out binding);

			case IterTypeReference iter:
				return TryGetLifetimeAnnotation(iter.ElementType, out kind, out anchors, out binding)
					|| TryGetLifetimeAnnotation(iter.Parameters.Select(static parameter => parameter.Type), out kind, out anchors, out binding);

			case GroupedParamsTypeReference grouped:
				return TryGetLifetimeAnnotation(grouped.StructType, out kind, out anchors, out binding);

			case MaterializedStructTypeReference materialized:
				return TryGetLifetimeAnnotation(materialized.ParamsType, out kind, out anchors, out binding);

			case ThrownTypeReference thrown:
				return TryGetLifetimeAnnotation(thrown.Type, out kind, out anchors, out binding);

			case TypeDefinitionReference definition:
				return TryGetLifetimeAnnotation(definition.TypeArguments, out kind, out anchors, out binding);

			case NamedTypeReference named:
				return TryGetLifetimeAnnotation(named.TypeArguments, out kind, out anchors, out binding);

			default:
				kind = "";
				anchors = [];
				binding = null;
				return false;
		}
	}

	bool TryGetLifetimeAnnotation(IEnumerable<TypeReference?> types, out string kind, out IReadOnlyList<string> anchors, out string? binding)
	{
		foreach (TypeReference? type in types)
			if (TryGetLifetimeAnnotation(type, out kind, out anchors, out binding))
				return true;

		kind = "";
		anchors = [];
		binding = null;
		return false;
	}

	void ValidateNoLifetimeAnnotation(TypeReference? type, SyntaxNode? syntax, string context)
	{
		if (!ContainsLifetimeAnnotation(type))
			return;

		Report(GetRange(syntax ?? type?.SourceSyntax), $"Lifetime annotations are not valid on {context}; use an explicit lifetime cast instead.");
	}

	void BindLifetime(TypeReference type, string kind, IReadOnlyList<string> anchors, string source, AnalysisScope scope)
	{
		foreach (string anchor in anchors)
		{
			if (!scope.ContainsLifetimeAnchor(anchor))
				Report(GetRange(type.SourceSyntax), $"Lifetime anchor '{anchor}' could not be resolved.");
		}

		type.LifetimeBinding = new BoundLifetime(kind, anchors, source).ToString();
	}

	void BindCastLifetime(CastExpression cast, string kind, IReadOnlyList<string> anchors, AnalysisScope scope, BodyScope? bodyScope = null)
	{
		foreach (string anchor in anchors)
		{
			if (!scope.ContainsLifetimeAnchor(anchor) && (bodyScope is null || !bodyScope.TryLookup(anchor, out _)))
				Report(GetRange(cast.SourceSyntax), $"Lifetime anchor '{anchor}' could not be resolved.");
		}

		cast.LifetimeBinding = new BoundLifetime(kind, anchors, "explicit cast").ToString();
	}

	void BindParameterLifetime(ParameterDefinition parameter, string kind, IReadOnlyList<string> anchors, string source)
	{
		parameter.LifetimeBinding = new BoundLifetime(kind, anchors, source).ToString();
	}

	void BindDefaultParameterLifetime(ParameterDefinition parameter, string source)
	{
		if (parameter.LifetimeBinding is null)
			BindParameterLifetime(parameter, "scoped", [], source);
	}

	void BindDefaultReceiverLifetime(ParameterDefinition parameter, string kind, string source)
	{
		if (parameter.LifetimeBinding is null)
			BindParameterLifetime(parameter, kind, [], source);
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

	bool TryEvaluateFixedArrayLength(Expression? expression, AnalysisScope scope, out long length)
	{
		return TryEvaluateFixedArrayLength(expression, scope, [], out length);
	}

	bool TryEvaluateFixedArrayLength(Expression? expression, AnalysisScope scope, HashSet<BindableNode> visitedSymbols, out long length)
	{
		length = 0;
		expression = UnwrapConstantExpressionSyntax(expression);
		switch (expression)
		{
			case LiteralExpression literal:
				return TryParseIntegerConstant(literal.Text, out length);

			case UnaryExpression { Operator: UnaryOperator.Plus } unary:
				return TryEvaluateFixedArrayLength(unary.Operand, scope, visitedSymbols, out length);

			case UnaryExpression { Operator: UnaryOperator.Minus } unary:
				if (!TryEvaluateFixedArrayLength(unary.Operand, scope, visitedSymbols, out long operand))
					return false;
				length = -operand;
				return true;

			case CastExpression cast:
				return TryEvaluateFixedArrayLength(cast.Expression, scope, visitedSymbols, out length);

			case NamedExpression named:
				return TryEvaluateNamedFixedArrayLength(named, scope, visitedSymbols, out length);

			case MemberExpression member:
				return TryEvaluateMemberFixedArrayLength(member, scope, visitedSymbols, out length);

			default:
				return false;
		}
	}

	bool TryEvaluateNamedFixedArrayLength(NamedExpression named, AnalysisScope scope, HashSet<BindableNode> visitedSymbols, out long length)
	{
		length = 0;
		if (named.Qualifiers.Count > 0)
			return false;
		if (scope.TryGetGenericParameter(named.Name, out _))
			return false;

		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition is VariableDefinition variable && IsDefinitionNamed(variable, named.Name) && IsConstantVariable(variable))
				return TryEvaluateConstantStorageLength(variable, variable.InitialValue, visitedSymbols, out length);
			if (definition is TypeDefinition type)
			{
				foreach (FieldDefinition field in GetTypeFields(type))
					if (field.Modifier == FieldModifier.Static && IsDefinitionNamed(field, named.Name) && IsConstantField(field))
						return TryEvaluateConstantStorageLength(field, field.InitialValue, visitedSymbols, out length);
			}
		}
		return false;
	}

	bool TryEvaluateMemberFixedArrayLength(MemberExpression member, AnalysisScope scope, HashSet<BindableNode> visitedSymbols, out long length)
	{
		length = 0;
		if (member.Target is not NamedExpression { Qualifiers.Count: 0 } target || !typeDefinitions.TryGetValue(target.Name, out TypeDefinition? type))
			return false;
		foreach (FieldDefinition field in GetTypeFields(type))
			if (field.Modifier == FieldModifier.Static && field.Name == member.Name && IsConstantField(field))
				return TryEvaluateConstantStorageLength(field, field.InitialValue, visitedSymbols, out length);
		return false;
	}

	bool TryEvaluateConstantStorageLength(BindableNode node, Expression? initializer, HashSet<BindableNode> visitedSymbols, out long length)
	{
		length = 0;
		if (!visitedSymbols.Add(node))
			return false;
		try
		{
			return initializer is not null && TryEvaluateFixedArrayLength(initializer, new AnalysisScope(), visitedSymbols, out length);
		}
		finally
		{
			visitedSymbols.Remove(node);
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

	bool IsPointerBearingType(TypeReference? type, AnalysisScope scope)
	{
		return IsPointerBearingType(type, scope, []);
	}

	bool IsPointerBearingType(TypeReference? type, AnalysisScope scope, HashSet<TypeDefinition> visited)
	{
		if (type is null)
			return false;

		type = UnwrapTypeDeclarators(type);
		return type switch
		{
			PointerTypeReference => true,
			ArrayTypeReference => true,
			FixedArrayTypeReference fixedArray => IsPointerBearingType(fixedArray.ElementType, scope, visited),
			OptionalTypeReference optional => IsPointerBearingType(optional.ElementType, scope, visited),
			CallableTypeReference { Kind: CallableKind.Delegate or CallableKind.Once or CallableKind.Async } => true,
			IterTypeReference => true,
			MaterializedStructTypeReference materialized => IsPointerBearingType(materialized.ParamsType, scope, visited),
			GroupedParamsTypeReference grouped => IsPointerBearingType(grouped.StructType, scope, visited),
			PrimitiveTypeReference { Type: PrimitiveType.String or PrimitiveType.WString or PrimitiveType.AString } => true,
			NamedTypeReference named when scope.TryGetGenericParameter(named.Name, out _) => true,
			GenericParameterTypeReference => true,
			NamedTypeReference named when typeDefinitions.TryGetValue(BaseTypeName(named.ResolvedType ?? named.Name), out TypeDefinition? definition) => IsPointerBearingTypeDefinition(definition, scope, visited),
			TypeDefinitionReference { Definition: TypeDefinition definition } => IsPointerBearingTypeDefinition(definition, scope, visited),
			_ => false
		};
	}

	bool IsPointerBearingTypeDefinition(TypeDefinition definition, AnalysisScope scope)
	{
		return IsPointerBearingTypeDefinition(definition, scope, []);
	}

	bool IsPointerBearingTypeDefinition(TypeDefinition definition, AnalysisScope scope, HashSet<TypeDefinition> visited)
	{
		return definition switch
		{
			ClassDefinition => true,
			InterfaceDefinition => true,
			StructDefinition structure => visited.Add(definition)
				&& structure.Fields.Any(field => IsPointerBearingType(field.Type, scope, visited)),
			ParamsDefinition parameters => visited.Add(definition)
				&& parameters.Components.Any(component => IsPointerBearingType(component.Type, scope, visited)),
			NewtypeDefinition newtype => visited.Add(definition)
				&& (IsPointerBearingType(newtype.UnderlyingType, scope, visited) || newtype.Parameters.Count > 0),
			_ => false
		};
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
			if (parameter is ThisParameterDefinition or SizeOfParameterDefinition or NameOfParameterDefinition or VTableOfParameterDefinition or WithinParameterDefinition
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
			ValidateGenericTypeArgumentConstraints(named, definition, scope);
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

	void ValidateGenericTypeArgumentConstraints(NamedTypeReference named, TypeDefinition definition, AnalysisScope scope)
	{
		int count = System.Math.Min(named.TypeArguments.Count, definition.GenericParameters.Count);
		for (int i = 0; i < count; i++)
		{
			GenericParameter parameter = definition.GenericParameters[i];
			TypeReference argument = named.TypeArguments[i];
			if (parameter.Constraint is CopyableTypeReference && !IsCopyableTypeArgument(argument, scope, []))
				Report(GetRange(argument.SourceSyntax ?? named.SourceSyntax), $"Type argument '{argument.ResolvedType ?? FormatTypeReference(argument)}' does not satisfy copyable constraint '{parameter.Name}: copyable'.");
		}
	}

	bool IsCopyableTypeArgument(TypeReference type, AnalysisScope scope, HashSet<string> visitedTypes)
	{
		type = UnwrapTypeDeclarators(type);
		switch (type)
		{
			case FixedArrayTypeReference:
				return false;

			case PrimitiveTypeReference:
			case PointerTypeReference:
			case ArrayTypeReference:
			case CallableTypeReference:
			case IterTypeReference:
			case OptionalTypeReference:
			case MaterializedStructTypeReference:
				return true;

			case NamedTypeReference named when scope.TryGetGenericParameter(named.Name, out GenericParameter? parameter) && parameter is not null:
				return parameter.Constraint is CopyableTypeReference;

			case GenericParameterTypeReference generic:
				return generic.Parameter?.Constraint is CopyableTypeReference;

			case NamedTypeReference named when typeDefinitions.TryGetValue(BaseTypeName(named.ResolvedType ?? named.Name), out TypeDefinition? namedDefinition) && namedDefinition is not null:
				return IsCopyableTypeDefinition(namedDefinition, visitedTypes);

			case TypeDefinitionReference { Definition: not null } reference:
				return IsCopyableTypeDefinition(reference.Definition, visitedTypes);

			default:
				return type.ResolvedType is not null
					&& typeDefinitions.TryGetValue(BaseTypeName(type.ResolvedType), out TypeDefinition? resolvedDefinition)
					&& resolvedDefinition is not null
					&& IsCopyableTypeDefinition(resolvedDefinition, visitedTypes);
		}
	}

	bool IsCopyableTypeDefinition(TypeDefinition definition, HashSet<string> visitedTypes)
	{
		if (!visitedTypes.Add(definition.Name))
			return true;
		if (definition is ClassDefinition or StructDefinition { Modifier: StructModifier.Fixed })
			return false;
		if (definition is not StructDefinition structDefinition)
			return true;

		foreach (FieldDefinition field in structDefinition.Fields)
		{
			if (field.Modifier == FieldModifier.Static || field.Type is null)
				continue;
			if (!IsCopyableStructFieldType(field.Type, visitedTypes))
				return false;
		}
		return true;
	}

	bool IsCopyableStructFieldType(TypeReference type, HashSet<string> visitedTypes)
	{
		type = UnwrapTypeDeclarators(type);
		if (type is FixedArrayTypeReference fixedArray)
			return fixedArray.ElementType is not null && IsCopyableStructFieldType(fixedArray.ElementType, visitedTypes);
		return IsCopyableTypeArgument(type, new AnalysisScope(), visitedTypes);
	}

	bool IsCopyableResolvedType(string type, BodyScope? bodyScope, HashSet<string> visitedTypes)
	{
		type = StripTopLevelValueQualifiers(type);
		if (new TypeShapeParser(type).TryParse(out TypeShape shape))
			return IsCopyableTypeShape(shape, bodyScope, visitedTypes);
		return true;
	}

	bool IsCopyableTypeShape(TypeShape shape, BodyScope? bodyScope, HashSet<string> visitedTypes)
	{
		if (shape.Kind == TypeShapeKind.FixedArray)
			return false;
		if (shape.Kind is TypeShapeKind.Pointer or TypeShapeKind.Array)
			return true;
		if (shape.Kind != TypeShapeKind.Named)
			return true;
		if (bodyScope is not null && FindBodyGenericParameter(bodyScope, shape.Name) is GenericParameter parameter)
			return parameter.Constraint is CopyableTypeReference;
		if (typeDefinitions.TryGetValue(BaseTypeName(shape.Name), out TypeDefinition? definition) && definition is not null)
			return IsCopyableTypeDefinition(definition, visitedTypes);
		return true;
	}
}
