using System;
using System.Collections.Generic;
using System.Numerics;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	enum ConversionLevel
	{
		Implicit,
		Explicit,
		Unsafe,
		FenceRequired,
		ReconstructRequired,
		Forbidden
	}

	enum ConversionReason
	{
		None,
		ConstRemoval,
		VolatileRemoval,
		PhysicalDepthChange,
		FamilyCrossing,
		ClassDowncast,
		ClassSidecast,
		UnrelatedClass,
		InterfaceSlotFabrication,
		FunctionPointerInteger,
		TargetSpecPolicy,
		FunctionDataCrossing,
		CallableSignature,
		CallableLifetime,
		DelegateInvariant,
		MultivalueReconstruct,
		GenericInvariant,
		Invalid
	}

	readonly record struct ConversionClassification(ConversionLevel Level, ConversionReason Reason, string? Diagnostic = null)
	{
		public bool IsOrdinary => Level is ConversionLevel.Implicit or ConversionLevel.Explicit;
	}

	void RequireExpressionType(string expected, string actual, SyntaxNode? syntax, string context)
	{
		if (!CanImplicitlyConvert(actual, expected))
			Report(GetRange(syntax), $"{context} must be '{expected}', not '{actual}'.");
	}

	void CheckAssignable(string expected, string actual, SyntaxNode? syntax, string context)
	{
		if (expected == ErrorType || actual == ErrorType || expected == TargetType || actual == TargetType)
			return;

		if (!CanAssignToType(expected, actual))
			Report(GetRange(syntax), $"{context} cannot convert '{actual}' to '{expected}'.");
	}

	bool CanAssignToType(string expected, string actual)
	{
		return CanImplicitlyConvert(actual, expected)
			|| IsConstQualified(expected) && StripConstFromShape(expected) == actual;
	}

	void RequireMutableWriteTarget(string targetType, SyntaxNode? syntax, string context)
	{
		if (targetType == ErrorType || targetType == TargetType)
			return;

		if (IsConstQualified(targetType) || IsConstFixedArrayStorageType(targetType))
			Report(GetRange(syntax), $"{context} is const and cannot be assigned.");
	}

	void RequireMutableWriteTarget(Expression? target, string targetType, SyntaxNode? syntax, string context, BodyScope scope)
	{
		if (IsStorageAccessThroughConstReceiver(target))
		{
			Report(GetRange(syntax), $"{context} is const and cannot be assigned.");
			return;
		}
		if (target is MemberReferenceExpression { Member: FieldDefinition or ParameterDefinition or VariableDefinition } && IsMutableStorageType(targetType))
			return;
		if (target is MemberExpression member
			&& expressionRewrites.TryGetValue(member, out Expression? rewrite)
			&& rewrite is MemberReferenceExpression { Member: FieldDefinition or ParameterDefinition or VariableDefinition }
			&& IsMutableStorageType(targetType))
			return;

		if (target is IndexExpression index)
		{
			RequireMutableIndexedWriteTarget(index, syntax, context, scope);
			return;
		}

		RequireMutableWriteTarget(targetType, syntax, context);
	}

	bool IsStorageAccessThroughConstReceiver(Expression? expression)
	{
		if (expression is MemberExpression member
			&& expressionRewrites.TryGetValue(member, out Expression? rewrite)
			&& !ReferenceEquals(rewrite, expression))
			return IsStorageAccessThroughConstReceiver(rewrite);

		return expression is MemberReferenceExpression { Target: not null, Member: FieldDefinition or ParameterDefinition or VariableDefinition } memberReference
			&& IsConstReceiverType(memberReference.Target.ResolvedType);
	}

	static bool IsMutableStorageType(string type)
	{
		return IsPrimitiveStringType(type);
	}

	void RequireMutableIndexedWriteTarget(IndexExpression index, SyntaxNode? syntax, string context, BodyScope scope)
	{
		string indexedType = index.Target?.ResolvedType ?? ErrorType;
		if (indexedType == ErrorType || indexedType == TargetType)
			return;

		if (TryGetArrayElementType(indexedType) is not null)
		{
			RequireGenericArrayElementStride(indexedType, scope, syntax, "mutate T[]");
			RequireGenericArrayMutableElement(indexedType, scope, syntax);
			if (IsConstQualified(indexedType) || IsConstFixedArrayStorageType(indexedType))
				Report(GetRange(syntax), $"{context} is const and cannot be assigned.");
			return;
		}

		if (GetPrimitiveStringElementType(indexedType) is not null)
		{
			Report(GetRange(syntax), $"{context} is const and cannot be assigned.");
			return;
		}

		RequireMutableWriteTarget(index.ResolvedType ?? ErrorType, syntax, context);
	}

	static bool IsConstFixedArrayStorageType(string type)
	{
		if (!TryGetFixedArrayShape(type, out string elementType, out _))
			return false;
		return IsConstQualified(elementType) || IsConstFixedArrayStorageType(elementType);
	}

	bool CanImplicitlyConvert(string source, string target)
	{
		return ClassifyConversion(source, target).Level == ConversionLevel.Implicit;
	}

	ConversionClassification ClassifyConversion(string source, string target)
	{
		if (TryClassifyConstructedTypeRewrite(source, target, out ConversionClassification constructedRewrite))
			return constructedRewrite;

		if (TryClassifyCallableConversion(source, target, out ConversionClassification callable))
			return callable;

		if (CanImplicitlyConvertCore(source, target))
			return new ConversionClassification(ConversionLevel.Implicit, ConversionReason.None);

		if (TryClassifyRawCarrierOrPointerConversion(source, target, out ConversionClassification rawOrPointer))
			return rawOrPointer;

		if (CanExplicitlyConvertCore(source, target))
			return new ConversionClassification(ConversionLevel.Explicit, ConversionReason.None);

		return new ConversionClassification(ConversionLevel.Forbidden, ConversionReason.Invalid);
	}

	bool TryClassifyConstructedTypeRewrite(string source, string target, out ConversionClassification classification)
	{
		classification = default;
		if (!TryParseTypeShape(source, out TypeShape sourceShape) || !TryParseTypeShape(target, out TypeShape targetShape))
			return false;

		if (sourceShape.Kind == TypeShapeKind.Array && targetShape.Kind == TypeShapeKind.Array
			&& !TypeShapesSameIgnoringValueQualifiers(sourceShape.Element, targetShape.Element))
		{
			classification = new ConversionClassification(
				ConversionLevel.ReconstructRequired,
				ConversionReason.MultivalueReconstruct,
				$"Array casts cannot change element type from '{TypeShapeParser.Format(sourceShape.Element)}' to '{TypeShapeParser.Format(targetShape.Element)}'; reconstruct the array with explicit elements and length.");
			return true;
		}

		if (sourceShape.Kind == TypeShapeKind.Optional && targetShape.Kind == TypeShapeKind.Optional
			&& sourceShape.Element is not null && targetShape.Element is not null)
		{
			ConversionClassification payload = ClassifyConversion(TypeShapeParser.Format(sourceShape.Element), TypeShapeParser.Format(targetShape.Element));
			classification = payload.Level switch
			{
				ConversionLevel.Implicit => new ConversionClassification(ConversionLevel.Implicit, ConversionReason.None),
				ConversionLevel.Explicit => new ConversionClassification(ConversionLevel.Explicit, ConversionReason.None),
				ConversionLevel.Unsafe => new ConversionClassification(ConversionLevel.Unsafe, payload.Reason, payload.Diagnostic),
				_ => new ConversionClassification(
					ConversionLevel.ReconstructRequired,
					ConversionReason.MultivalueReconstruct,
					"Optional payload conversion would require reconstruction; rebuild the optional value.")
			};
			return true;
		}

		if (IsConstructedGenericArgumentRewrite(sourceShape, targetShape))
		{
			classification = new ConversionClassification(
				ConversionLevel.ReconstructRequired,
				ConversionReason.GenericInvariant,
				$"Generic arguments are invariant; value conversions do not rewrite '{DescribeConstructedGenericShape(targetShape)}' arguments.");
			return true;
		}

		return false;
	}

	bool CanImplicitlyConvertCore(string source, string target)
	{
		if (source == target || source == ErrorType || target == ErrorType || target == TargetType)
			return true;

		string erasedConstOfSource = EraseConstOfQualifiers(source);
		string erasedConstOfTarget = EraseConstOfQualifiers(target);
		if ((erasedConstOfSource != source || erasedConstOfTarget != target)
			&& CanImplicitlyConvert(erasedConstOfSource, erasedConstOfTarget))
			return true;

		string structuralSource = StripLifetimeQualifiers(source);
		string structuralTarget = StripLifetimeQualifiers(target);
		if ((structuralSource != source || structuralTarget != target) && CanImplicitlyConvert(structuralSource, structuralTarget))
			return true;

		if (source == "#NULL" && TryParseTypeShape(target, out TypeShape nullTarget) && (nullTarget.IsPointer || nullTarget.IsOptional))
			return true;

		if (source == "#NULL" && TryGetCallableShape(target, out _))
			return true;

		if (source == "#NULL" && IsPrimitiveStringType(target))
			return true;

		if (CanConvertPrimitiveStringToPointer(source, target))
			return true;

		if (CanConvertPrimitiveStringToConstArray(source, target))
			return true;

		if (CanConvertIteratorStateToProtocol(source, target))
			return true;

		if (CanConvertToClassTypeContract(source, target))
			return true;

		if (source == "#NULL" && target == AllocatorType)
			return true;

		if (CanLiftToOptional(source, target))
			return true;

		if (source == AllocatorType && target == "Allocator*")
			return true;

		if (source == "Allocator*" && target == AllocatorType)
			return true;

		if (IsUntypedPointerType(target) && (IsObjectPointerType(source) || TryGetCallableShape(source, out _)))
			return true;

		if (TryGetCallableShape(source, out CallableShape sourceCallable) && TryGetCallableShape(target, out CallableShape targetCallable))
			return CallableShapesCompatibleWithConstOfVariance(sourceCallable, targetCallable);

		if (CanCopyConstValue(source, target))
			return true;

		if (CanCopyTopLevelConstValue(source, target))
			return true;

		if (IsClassToInterfaceConversion(source, target) || IsStructToInterfaceConversion(source, target) || IsInterfaceUpcast(source, target))
			return true;

		if (IsLoweredInterfacePointerConversion(source, target))
			return true;

		if (IsNewtypeOrEnumBoundary(source, target))
			return false;

		if (TryParseTypeShape(source, out TypeShape sourceShape) && TryParseTypeShape(target, out TypeShape targetShape))
			return CanImplicitlyConvertShape(sourceShape, targetShape);

		return IsNumericType(source) && IsNumericType(target) && NumericRank(source) <= NumericRank(target);
	}

	static string EraseConstOfQualifiers(string type)
	{
		type = System.Text.RegularExpressions.Regex.Replace(type, @"\bconstof\s*\([^)]*\)", "const");
		type = System.Text.RegularExpressions.Regex.Replace(type, @"\bconstof\s+", "const ");
		const string prefix = "constof(";
		int start = type.IndexOf(prefix, StringComparison.Ordinal);
		if (start < 0)
			return type;

		System.Text.StringBuilder builder = new(type.Length);
		int index = 0;
		while (start >= 0)
		{
			builder.Append(type, index, start - index);
			int close = type.IndexOf(')', start + prefix.Length);
			if (close < 0)
				return type;
			builder.Append("const");
			index = close + 1;
			start = type.IndexOf(prefix, index, StringComparison.Ordinal);
		}
		builder.Append(type, index, type.Length - index);
		return builder.ToString();
	}

	bool CanConvertIteratorStateToProtocol(string source, string target)
	{
		if (!TryGetIteratorProtocolCurrentTypes(target, out List<string>? targetCurrentTypes) || targetCurrentTypes is null)
			return false;

		string stateType = TryGetPointerElementType(source) ?? source;
		if (!TryFindIteratorNextMethod(stateType, out _, out string elementType))
			return false;

		return targetCurrentTypes.Count == 1 && CanImplicitlyConvert(elementType, targetCurrentTypes[0]);
	}

	static bool CanConvertToClassTypeContract(string source, string target)
	{
		if (TryGetPointerElementType(target) is not string targetElement || targetElement != "classtype")
			return false;
		return TryGetPointerElementType(source) is not null;
	}

	bool CanLiftToOptional(string source, string target)
	{
		if (!TryParseTypeShape(target, out TypeShape targetShape) || targetShape.Kind != TypeShapeKind.Optional || targetShape.Element is null)
			return false;
		string targetElement = TypeShapeParser.Format(targetShape.Element);
		return CanImplicitlyConvert(source, targetElement);
	}

	bool CanCopyConstValue(string source, string target)
	{
		if (!TryParseTypeShape(source, out TypeShape sourceShape) || !TryParseTypeShape(target, out TypeShape targetShape))
			return false;
		if (sourceShape.Kind != TypeShapeKind.Named || targetShape.Kind != TypeShapeKind.Named)
			return false;
		if (sourceShape.Name != targetShape.Name || IsPrimitiveStringType(source) || IsPrimitiveStringType(target))
			return false;

		return sourceShape.Qualifiers.IsConst && !targetShape.Qualifiers.IsConst;
	}

	static bool CanCopyTopLevelConstValue(string source, string target)
	{
		if (!IsConstQualifiedShape(source))
			return false;

		return StripConstFromShape(source) == target;
	}

	bool CanConvertStructuralGroupedToNominalParams(string source, string target)
	{
		return false;
	}

	readonly record struct GroupedTypeComponent(string? Name, string Type);

	static bool TryGetStructuralGroupedComponents(string source, out List<GroupedTypeComponent> components)
	{
		components = [];
		if (!source.StartsWith("(", StringComparison.Ordinal) || !source.EndsWith(")", StringComparison.Ordinal))
			return false;

		string text = source[1..^1].Trim();
		if (string.IsNullOrWhiteSpace(text))
			return false;

		foreach (string part in SplitTopLevelGroupedComponents(text))
		{
			string component = part.Trim();
			string? name = null;
			string type = component;
			int colon = FindTopLevelColon(component);
			if (colon >= 0)
			{
				name = component[..colon].Trim();
				type = component[(colon + 1)..].Trim();
			}

			if (string.IsNullOrWhiteSpace(type))
				return false;
			components.Add(new GroupedTypeComponent(string.IsNullOrWhiteSpace(name) ? null : name, type));
		}

		return components.Count > 0;
	}

	static List<string> SplitTopLevelGroupedComponents(string text)
	{
		List<string> components = [];
		int start = 0;
		int depth = 0;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (c is '(' or '<' or '[')
				depth++;
			else if (c is ')' or '>' or ']')
				depth--;
			else if (c == ',' && depth == 0)
			{
				components.Add(text[start..i]);
				start = i + 1;
			}
		}
		components.Add(text[start..]);
		return components;
	}

	static int FindTopLevelColon(string text)
	{
		int depth = 0;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (c is '(' or '<' or '[')
				depth++;
			else if (c is ')' or '>' or ']')
				depth--;
			else if (c == ':' && depth == 0)
				return i;
		}
		return -1;
	}

	bool IsClassToInterfaceConversion(string source, string target)
	{
		string? sourceElement = TryGetPointerElementType(source);
		string? targetElement = TryGetPointerElementType(target);
		if (sourceElement is null || targetElement is null)
			return false;

		return typeDefinitions.TryGetValue(BaseTypeName(sourceElement), out TypeDefinition? sourceType)
			&& sourceType is ClassDefinition classDefinition
			&& typeDefinitions.TryGetValue(BaseTypeName(targetElement), out TypeDefinition? targetType)
			&& targetType is InterfaceDefinition interfaceDefinition
			&& ClassImplementsInterface(classDefinition, interfaceDefinition);
	}

	bool IsStructToInterfaceConversion(string source, string target)
	{
		string? targetElement = TryGetPointerElementType(target);
		if (targetElement is null)
			return false;

		return typeDefinitions.TryGetValue(BaseTypeName(source), out TypeDefinition? sourceType)
			&& sourceType is StructDefinition structDefinition
			&& typeDefinitions.TryGetValue(BaseTypeName(targetElement), out TypeDefinition? targetType)
			&& targetType is InterfaceDefinition interfaceDefinition
			&& TypeImplementsInterface(structDefinition, interfaceDefinition);
	}

	bool IsInterfaceUpcast(string source, string target)
	{
		string? sourceElement = TryGetPointerElementType(source);
		string? targetElement = TryGetPointerElementType(target);
		if (sourceElement is null || targetElement is null)
			return false;

		return typeDefinitions.TryGetValue(BaseTypeName(sourceElement), out TypeDefinition? sourceType)
			&& sourceType is InterfaceDefinition sourceInterface
			&& typeDefinitions.TryGetValue(BaseTypeName(targetElement), out TypeDefinition? targetType)
			&& targetType is InterfaceDefinition targetInterface
			&& InterfaceInheritsFrom(sourceInterface, targetInterface);
	}

	bool IsLoweredInterfacePointerConversion(string source, string target)
	{
		string? targetElement = TryGetPointerElementType(target);
		if (targetElement is null)
			return false;
		return typeDefinitions.TryGetValue(BaseTypeName(targetElement), out TypeDefinition? targetType)
			&& targetType is InterfaceDefinition
			&& source == targetElement + "**";
	}

	bool ClassImplementsInterface(ClassDefinition classDefinition, InterfaceDefinition interfaceDefinition)
	{
		return TypeImplementsInterface(classDefinition, interfaceDefinition);
	}

	bool TypeImplementsInterface(TypeDefinition typeDefinition, InterfaceDefinition interfaceDefinition)
	{
		foreach (InterfaceDefinition implemented in GetImplementedInterfaces(typeDefinition))
		{
			if (ReferenceEquals(implemented, interfaceDefinition))
				return true;
		}
		return false;
	}

	bool InterfaceInheritsFrom(InterfaceDefinition source, InterfaceDefinition target)
	{
		foreach (InterfaceDefinition baseInterface in GetBaseInterfaces(source))
		{
			if (ReferenceEquals(baseInterface, target))
				return true;
		}
		return false;
	}

	bool CanExplicitlyConvert(string source, string target)
	{
		ConversionLevel level = ClassifyConversion(source, target).Level;
		return level is ConversionLevel.Implicit or ConversionLevel.Explicit;
	}

	bool CanExplicitlyConvertCore(string source, string target)
	{
		if (CanImplicitlyConvertCore(source, target))
			return true;

		if (TryGetNewtypeUnderlyingType(source, out string? sourceUnderlying))
			return sourceUnderlying == target;

		if (TryGetNewtypeUnderlyingType(target, out string? targetUnderlying))
			return targetUnderlying == source;

		if (IsNumericType(source) && IsNumericType(target))
			return true;

		if (CanExplicitlyConvertPrimitiveStringPointer(source, target))
			return true;

		if (CanExplicitlyConvertCallable(source, target))
			return true;

		if (TryParseTypeShape(source, out TypeShape explicitSourceShape)
			&& TryParseTypeShape(target, out TypeShape explicitTargetShape)
			&& (CanExplicitlyConvertTargetSpecShape(explicitSourceShape, explicitTargetShape)
				|| CanExplicitlyConvertPointerNaturalInteger(explicitSourceShape, explicitTargetShape)
				|| CanExplicitlyConvertUntypedPointer(explicitSourceShape, explicitTargetShape)
				|| CanExplicitlyConvertConstShape(explicitSourceShape, explicitTargetShape)))
			return true;

		if (TryParseTypeShape(source, out TypeShape untypedSourceShape)
			&& CanExplicitlyConvertUntypedPointerToCallable(untypedSourceShape, target))
			return true;

		return TryParseTypeShape(source, out TypeShape sourceShape)
			&& TryParseTypeShape(target, out TypeShape targetShape)
			&& sourceShape.IsPointer
			&& targetShape.IsPointer;
	}

	bool TryClassifyRawCarrierOrPointerConversion(string source, string target, out ConversionClassification classification)
	{
		classification = default;
		bool sourceIsCallable = TryGetCallableShape(source, out CallableShape sourceCallable);
		bool targetIsCallable = TryGetCallableShape(target, out CallableShape targetCallable);
		bool sourceParsed = TryParseTypeShape(source, out TypeShape sourceShape);
		bool targetParsed = TryParseTypeShape(target, out TypeShape targetShape);
		if (!sourceParsed && !sourceIsCallable || !targetParsed && !targetIsCallable)
			return false;

		if (sourceParsed && targetParsed && TryClassifyTargetSpecPolicy(sourceShape, targetShape, out classification))
			return true;

		if (sourceIsCallable && sourceCallable.Kind != "fn" && (IsUntypedScalarShape(targetShape) || IsRawFunctionPointerShape(targetShape) || IsNaturalIntegerShape(targetShape))
			|| targetIsCallable && targetCallable.Kind != "fn" && (IsUntypedScalarShape(sourceShape) || IsRawFunctionPointerShape(sourceShape) || IsNaturalIntegerShape(sourceShape)))
		{
			classification = new ConversionClassification(
				ConversionLevel.ReconstructRequired,
				ConversionReason.DelegateInvariant,
				"Delegate values cannot be cast to 'fn*'; rebuild the delegate or cast its call component.");
			return true;
		}

		if (IsUntypedScalarShape(sourceShape) && IsRepresentableRawCarrierTarget(targetShape, target))
		{
			classification = new ConversionClassification(ConversionLevel.Explicit, ConversionReason.None);
			return true;
		}
		if (IsUntypedScalarShape(targetShape) && IsRepresentableRawCarrierTarget(sourceShape, source))
		{
			classification = new ConversionClassification(ConversionLevel.Explicit, ConversionReason.None);
			return true;
		}

		if (IsRawFunctionPointerShape(sourceShape) || IsRawFunctionPointerShape(targetShape))
		{
			if (IsNaturalIntegerShape(sourceShape) || IsNaturalIntegerShape(targetShape))
			{
				classification = new ConversionClassification(
					ConversionLevel.Forbidden,
					ConversionReason.FunctionPointerInteger,
					"Function pointer values do not portably convert to natural integers; use 'fn*' or 'untyped' as the raw carrier.");
				return true;
			}
			if (targetShape.Kind == TypeShapeKind.Pointer || sourceShape.Kind == TypeShapeKind.Pointer
				|| sourceIsCallable && targetShape.Kind == TypeShapeKind.Pointer
				|| sourceShape.Kind == TypeShapeKind.Pointer && targetIsCallable)
			{
				classification = new ConversionClassification(
					ConversionLevel.Unsafe,
					ConversionReason.FunctionDataCrossing,
					"Function pointer to data pointer conversion requires unsafe; use 'fn*' to erase only the function signature.");
				return true;
			}
			if (sourceIsCallable && IsRawFunctionPointerShape(targetShape)
				|| IsRawFunctionPointerShape(sourceShape) && targetIsCallable
				|| IsRawFunctionPointerShape(sourceShape) && IsRawFunctionPointerShape(targetShape))
			{
				classification = new ConversionClassification(ConversionLevel.Explicit, ConversionReason.None);
				return true;
			}
		}

		if (sourceIsCallable && (IsNaturalIntegerShape(targetShape) || targetShape.Kind == TypeShapeKind.Pointer)
			|| targetIsCallable && (IsNaturalIntegerShape(sourceShape) || sourceShape.Kind == TypeShapeKind.Pointer))
		{
			ConversionLevel level = sourceShape.Kind == TypeShapeKind.Pointer || targetShape.Kind == TypeShapeKind.Pointer
				? ConversionLevel.Unsafe
				: ConversionLevel.Forbidden;
			classification = new ConversionClassification(
				level,
				level == ConversionLevel.Unsafe ? ConversionReason.FunctionDataCrossing : ConversionReason.FunctionPointerInteger,
				level == ConversionLevel.Unsafe
					? "Function pointer to data pointer conversion requires unsafe; use 'fn*' to erase only the function signature."
					: "Function pointer values do not portably convert to natural integers; use 'fn*' or 'untyped' as the raw carrier.");
			return true;
		}

		if (sourceShape.Kind == TypeShapeKind.Pointer && IsNaturalIntegerShape(targetShape)
			|| IsNaturalIntegerShape(sourceShape) && targetShape.Kind == TypeShapeKind.Pointer)
		{
			classification = CanExplicitlyConvertPointerNaturalInteger(sourceShape, targetShape)
				? new ConversionClassification(ConversionLevel.Explicit, ConversionReason.None)
				: new ConversionClassification(ConversionLevel.Forbidden, ConversionReason.Invalid);
			return true;
		}

		if (sourceShape.Kind == TypeShapeKind.Array && targetShape.Kind == TypeShapeKind.Array
			&& !TypeShapesSameIgnoringLifetime(sourceShape, targetShape))
		{
			if (!TypeShapesSameIgnoringValueQualifiers(sourceShape.Element, targetShape.Element))
			{
				classification = new ConversionClassification(
					ConversionLevel.ReconstructRequired,
					ConversionReason.MultivalueReconstruct,
					$"Array casts cannot change element type from '{TypeShapeParser.Format(sourceShape.Element)}' to '{TypeShapeParser.Format(targetShape.Element)}'; reconstruct the array with explicit elements and length.");
				return true;
			}
			if (ContainsQualifierRemoval(sourceShape, targetShape, static qualifiers => qualifiers.IsConst))
			{
				classification = new ConversionClassification(
					ConversionLevel.Unsafe,
					ConversionReason.ConstRemoval,
					"Cast removes const from array element view; write 'unsafe' to acknowledge mutable access.");
				return true;
			}
			if (ContainsQualifierRemoval(sourceShape, targetShape, static qualifiers => qualifiers.IsVolatile))
			{
				classification = new ConversionClassification(
					ConversionLevel.Unsafe,
					ConversionReason.VolatileRemoval,
					"Cast removes volatile from array element view; write 'unsafe' to acknowledge volatile access removal.");
				return true;
			}
			classification = new ConversionClassification(
				ConversionLevel.Explicit,
				ConversionReason.None);
			return true;
		}

		if (sourceShape.Kind != TypeShapeKind.Pointer || targetShape.Kind != TypeShapeKind.Pointer)
			return false;

		int sourceDepth = PhysicalPointerDepth(sourceShape);
		int targetDepth = PhysicalPointerDepth(targetShape);
		if (sourceDepth != targetDepth)
		{
			classification = new ConversionClassification(
				ConversionLevel.Unsafe,
				ConversionReason.PhysicalDepthChange,
				"Cast changes pointer indirection depth; write 'unsafe' or use a matching-depth fence.");
			return true;
		}

		if (ContainsQualifierRemoval(sourceShape, targetShape, static qualifiers => qualifiers.IsConst))
		{
			classification = new ConversionClassification(
				ConversionLevel.Unsafe,
				ConversionReason.ConstRemoval,
				"Cast removes const; write '(unsafe T*)' to acknowledge mutable access.");
			return true;
		}
		if (ContainsQualifierRemoval(sourceShape, targetShape, static qualifiers => qualifiers.IsVolatile))
		{
			classification = new ConversionClassification(
				ConversionLevel.Unsafe,
				ConversionReason.VolatileRemoval,
				"Cast removes volatile; write 'unsafe' to acknowledge volatile access removal.");
			return true;
		}

		TypeShape sourceBase = InnermostPointerElement(sourceShape);
		TypeShape targetBase = InnermostPointerElement(targetShape);
		if (IsVoidShape(sourceBase) || IsVoidShape(targetBase))
		{
			classification = new ConversionClassification(ConversionLevel.Explicit, ConversionReason.None);
			return true;
		}

		if (IsClassPointerElement(sourceBase) && IsClassPointerElement(targetBase))
		{
			if (IsConstructedGenericArgumentRewrite(sourceBase, targetBase))
			{
				classification = new ConversionClassification(
					ConversionLevel.FenceRequired,
					ConversionReason.GenericInvariant,
					$"Generic arguments are invariant; value conversions do not rewrite '{DescribeConstructedGenericShape(targetBase)}' arguments.");
				return true;
			}

			classification = ClassifyClassPointerConversion(sourceBase, targetBase);
			return true;
		}

		PointerFamily sourceFamily = GetPointerFamily(sourceBase);
		PointerFamily targetFamily = GetPointerFamily(targetBase);
		if (IsGenericTargetSpecRewrite(sourceBase, targetBase))
		{
			classification = new ConversionClassification(
				ConversionLevel.ReconstructRequired,
				ConversionReason.FamilyCrossing,
				"Typespec value conversions do not apply to generic arguments; reconstruct the value.");
			return true;
		}
		if (sourceFamily == targetFamily && sourceFamily is PointerFamily.Primitive or PointerFamily.Struct or PointerFamily.Interface or PointerFamily.Unknown)
		{
			classification = new ConversionClassification(ConversionLevel.Explicit, ConversionReason.None);
			return true;
		}

		if (sourceFamily == PointerFamily.Class && targetFamily == PointerFamily.Interface && !IsClassToInterfaceConversion(source, target)
			|| sourceFamily == PointerFamily.Interface && targetFamily == PointerFamily.Class)
		{
			classification = new ConversionClassification(
				ConversionLevel.FenceRequired,
				ConversionReason.InterfaceSlotFabrication,
				$"Cast would invent an interface slot for '{BaseTypeName(TypeShapeParser.Format(targetBase))}'; use an unsafe raw fence or an explicit conversion helper.");
			return true;
		}

		classification = new ConversionClassification(
			ConversionLevel.FenceRequired,
			ConversionReason.FamilyCrossing,
			$"Cannot directly cast '{source}' to '{target}'; cast through 'void*' to erase the data-pointer family.");
		return true;
	}

	bool TryClassifyTargetSpecPolicy(TypeShape sourceShape, TypeShape targetShape, out ConversionClassification classification)
	{
		classification = default;
		if (selectedTarget is null || sourceShape.TargetSpec == targetShape.TargetSpec)
			return false;

		TargetConversionCarrier? carrier = GetDirectTargetSpecCarrier(sourceShape, targetShape);
		if (carrier is null)
			return false;

		TargetConversionLevel level = selectedTarget.ClassifyTypeSpecConversion(carrier.Value, sourceShape.TargetSpec, targetShape.TargetSpec);
		classification = level switch
		{
			TargetConversionLevel.Implicit => new ConversionClassification(ConversionLevel.Implicit, ConversionReason.TargetSpecPolicy),
			TargetConversionLevel.Explicit => new ConversionClassification(ConversionLevel.Explicit, ConversionReason.TargetSpecPolicy),
			TargetConversionLevel.Unsafe => new ConversionClassification(
				ConversionLevel.Unsafe,
				ConversionReason.TargetSpecPolicy,
				$"Target '{selectedTarget.Name}' requires unsafe for '{DescribeTargetSpec(sourceShape.TargetSpec)}' to '{DescribeTargetSpec(targetShape.TargetSpec)}' {DescribeTargetSpecCarrier(carrier.Value)} conversion."),
			TargetConversionLevel.Fence => new ConversionClassification(
				ConversionLevel.FenceRequired,
				ConversionReason.TargetSpecPolicy,
				$"Target does not define a typed conversion from '{DescribeTargetSpec(sourceShape.TargetSpec)}' to '{DescribeTargetSpec(targetShape.TargetSpec)}'; use 'untyped' for a raw escape."),
			_ => new ConversionClassification(
				ConversionLevel.Forbidden,
				ConversionReason.TargetSpecPolicy,
				$"Target '{selectedTarget.Name}' forbids '{DescribeTargetSpec(sourceShape.TargetSpec)}' to '{DescribeTargetSpec(targetShape.TargetSpec)}' {DescribeTargetSpecCarrier(carrier.Value)} conversion.")
		};
		return true;
	}

	static TargetConversionCarrier? GetDirectTargetSpecCarrier(TypeShape sourceShape, TypeShape targetShape)
	{
		if (sourceShape.Kind == TypeShapeKind.Pointer && targetShape.Kind == TypeShapeKind.Pointer
			|| sourceShape.Kind == TypeShapeKind.Array && targetShape.Kind == TypeShapeKind.Array
			|| sourceShape.Kind == TypeShapeKind.FixedArray && targetShape.Kind == TypeShapeKind.Array)
			return TargetConversionCarrier.DataPointer;
		if (sourceShape.Kind == TypeShapeKind.RawFunctionPointer && targetShape.Kind == TypeShapeKind.RawFunctionPointer)
			return TargetConversionCarrier.FunctionPointer;
		if (IsNaturalIntegerShape(sourceShape) && IsNaturalIntegerShape(targetShape))
			return TargetConversionCarrier.NaturalInteger;
		return null;
	}

	static string DescribeTargetSpec(string? spec)
	{
		return spec ?? "default";
	}

	static string DescribeTargetSpecCarrier(TargetConversionCarrier carrier)
	{
		return carrier switch
		{
			TargetConversionCarrier.DataPointer => "data-pointer",
			TargetConversionCarrier.FunctionPointer => "function-pointer",
			TargetConversionCarrier.NaturalInteger => "natural-integer",
			_ => "ABI-slot"
		};
	}

	enum PointerFamily
	{
		Primitive,
		Struct,
		Class,
		Interface,
		Void,
		Unknown
	}

	bool IsRepresentableRawCarrierTarget(TypeShape shape, string type)
	{
		return shape.Kind == TypeShapeKind.Pointer
			|| shape.Kind == TypeShapeKind.RawFunctionPointer
			|| IsNaturalIntegerShape(shape)
			|| IsNumericType(type)
			|| TryGetCallableShape(type, out _);
	}

	bool TryClassifyCallableConversion(string source, string target, out ConversionClassification classification)
	{
		classification = default;
		if (!TryGetCallableShape(StripCallableLifetimeQualifiers(source), out CallableShape sourceCallable)
			|| !TryGetCallableShape(StripCallableLifetimeQualifiers(target), out CallableShape targetCallable))
			return false;

		sourceCallable = ExpandCallableShape(sourceCallable);
		targetCallable = ExpandCallableShape(targetCallable);

		if (sourceCallable.Kind != targetCallable.Kind
			&& !(sourceCallable.Kind == "fn" && targetCallable.Kind == "delegate")
			&& !(sourceCallable.Kind == "delegate" && targetCallable.Kind == "once"))
		{
			classification = new ConversionClassification(
				sourceCallable.Kind == "delegate" || targetCallable.Kind == "delegate" ? ConversionLevel.ReconstructRequired : ConversionLevel.Forbidden,
				sourceCallable.Kind == "delegate" || targetCallable.Kind == "delegate" ? ConversionReason.DelegateInvariant : ConversionReason.CallableSignature,
				sourceCallable.Kind == "delegate" || targetCallable.Kind == "delegate"
					? "Delegate values cannot change callable carrier; rebuild the delegate or cast its call component."
					: null);
			return true;
		}

		if (CallableShapesAbiSlotCompatible(sourceCallable, targetCallable, out bool lifetimeOnlyDifference))
		{
			classification = lifetimeOnlyDifference
				? new ConversionClassification(
					ConversionLevel.Unsafe,
					ConversionReason.CallableLifetime,
					"Cast changes callable lifetime contract; write 'unsafe' to acknowledge the hidden context/result lifetime change.")
				: new ConversionClassification(ConversionLevel.Implicit, ConversionReason.None);
			return true;
		}

		if (sourceCallable.Kind == "delegate" || targetCallable.Kind == "delegate")
		{
			classification = new ConversionClassification(
				ConversionLevel.ReconstructRequired,
				ConversionReason.DelegateInvariant,
				"Delegate values cannot change callable signature; rebuild the delegate or cast its call component.");
			return true;
		}

		bool fenceRequired = CallableSignatureNeedsFence(sourceCallable, targetCallable);
		classification = new ConversionClassification(
			fenceRequired ? ConversionLevel.FenceRequired : ConversionLevel.Unsafe,
			ConversionReason.CallableSignature,
			"Callable signatures are not ABI-slot compatible; use an unsafe cast or an 'fn*' fence.");
		return true;
	}

	static string StripCallableLifetimeQualifiers(string type)
	{
		string normalized = type.Trim();
		bool changed;
		do
		{
			changed = false;
			foreach (string lifetime in new[] { "escaped", "scoped", "unscoped" })
			{
				string leading = lifetime + " ";
				if (normalized.StartsWith(leading, StringComparison.Ordinal))
				{
					normalized = normalized[leading.Length..].Trim();
					changed = true;
				}

				string trailing = " " + lifetime;
				if (normalized.EndsWith(trailing, StringComparison.Ordinal))
				{
					normalized = normalized[..^trailing.Length].Trim();
					changed = true;
				}
			}
		}
		while (changed);
		return normalized;
	}

	bool CallableSignatureNeedsFence(CallableShape source, CallableShape target)
	{
		if (source.CallSpec != target.CallSpec || !CallableSpecsAbiSlotCompatible(source.Spec, target.Spec))
			return true;
		if (CallableSlotNeedsFence(source.ReturnType, target.ReturnType))
			return true;
		for (int i = 0; i < Math.Min(source.Parameters.Count, target.Parameters.Count); i++)
		{
			CallableSlot sourceSlot = ParseCallableSlot(source.Parameters[i]);
			CallableSlot targetSlot = ParseCallableSlot(target.Parameters[i]);
			if (sourceSlot.Modifier != targetSlot.Modifier)
				continue;
			if (CallableSlotNeedsFence(sourceSlot.Type, targetSlot.Type))
				return true;
		}
		return false;
	}

	bool CallableSlotNeedsFence(string source, string target)
	{
		if (!TryParseTypeShape(source, out TypeShape sourceShape) || !TryParseTypeShape(target, out TypeShape targetShape))
			return false;
		return CallableTypeShapeNeedsFence(sourceShape, targetShape);
	}

	bool CallableTypeShapeNeedsFence(TypeShape source, TypeShape target)
	{
		if (source.TargetSpec != target.TargetSpec && !CallableSpecsAbiSlotCompatible(source.TargetSpec, target.TargetSpec))
			return true;
		if (source.Element is null || target.Element is null)
			return false;
		return CallableTypeShapeNeedsFence(source.Element, target.Element);
	}

	bool CallableShapesAbiSlotCompatible(CallableShape source, CallableShape target, out bool lifetimeOnlyDifference)
	{
		lifetimeOnlyDifference = false;
		if (source.Parameters.Count != target.Parameters.Count)
			return false;
		if (source.CallSpec != target.CallSpec)
			return false;
		if (!CallableSpecsAbiSlotCompatible(source.Spec, target.Spec))
			return false;
		if (!ThisContractsAbiSlotCompatible(source.This, target.This, ref lifetimeOnlyDifference))
			return false;
		if (!CallableSlotAbiCompatible(source.ReturnType, target.ReturnType, outputPosition: true, ref lifetimeOnlyDifference))
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
			if (!CallableSlotAbiCompatible(sourceSlot.Type, targetSlot.Type, outputPosition, ref lifetimeOnlyDifference))
				return false;
		}

		return true;
	}

	bool CallableSpecsAbiSlotCompatible(string? sourceSpec, string? targetSpec)
	{
		if (sourceSpec == targetSpec)
			return true;
		if (selectedTarget is null)
			return false;
		return selectedTarget.ClassifyTypeSpecConversion(TargetConversionCarrier.AbiSlot, sourceSpec, targetSpec) == TargetConversionLevel.Compatible;
	}

	bool CallableSlotAbiCompatible(string source, string target, bool outputPosition, ref bool lifetimeOnlyDifference)
	{
		if (CallableSlotTypesCompatible(source, target, outputPosition))
			return true;
		if (CallableSlotTypesSameIgnoringLifetime(source, target, outputPosition))
		{
			lifetimeOnlyDifference = true;
			return true;
		}
		if (!TryParseTypeShape(source, out TypeShape sourceShape) || !TryParseTypeShape(target, out TypeShape targetShape))
			return false;
		return CallableTypeShapeAbiCompatible(sourceShape, targetShape, outputPosition, ref lifetimeOnlyDifference);
	}

	bool CallableTypeShapeAbiCompatible(TypeShape source, TypeShape target, bool outputPosition, ref bool lifetimeOnlyDifference)
	{
		if (TypeShapeParser.Format(source) == TypeShapeParser.Format(target))
			return true;
		if (source.Kind != target.Kind || source.Name != target.Name || source.Length != target.Length)
			return false;
		if (source.TargetSpec != target.TargetSpec
			&& selectedTarget?.ClassifyTypeSpecConversion(TargetConversionCarrier.AbiSlot, source.TargetSpec, target.TargetSpec) != TargetConversionLevel.Compatible)
			return false;
		if (!CallableQualifiersCompatible(source.Qualifiers, target.Qualifiers, outputPosition, ref lifetimeOnlyDifference))
			return false;
		if (source.Element is null || target.Element is null)
			return source.Element is null && target.Element is null;
		return CallableTypeShapeAbiCompatible(source.Element, target.Element, outputPosition, ref lifetimeOnlyDifference);
	}

	static bool CallableQualifiersCompatible(TypeQualifiers source, TypeQualifiers target, bool outputPosition, ref bool lifetimeOnlyDifference)
	{
		if (source.IsVolatile != target.IsVolatile)
			return false;
		if (source.IsConst != target.IsConst)
		{
			if (outputPosition && source.IsConst && !target.IsConst)
				return false;
			if (!outputPosition && !source.IsConst && target.IsConst)
				return false;
		}
		if (source.Lifetime != target.Lifetime)
			lifetimeOnlyDifference = true;
		return true;
	}

	static bool CallableSlotTypesSameIgnoringLifetime(string source, string target, bool outputPosition)
	{
		if (!TryParseTypeShapeStatic(source, out TypeShape sourceShape) || !TryParseTypeShapeStatic(target, out TypeShape targetShape))
			return false;
		sourceShape = StripLifetimeQualifiers(sourceShape);
		targetShape = StripLifetimeQualifiers(targetShape);
		return TypeShapeParser.Format(sourceShape) == TypeShapeParser.Format(targetShape);
	}

	static bool TryParseTypeShapeStatic(string? type, out TypeShape shape)
	{
		TypeShapeParser parser = new(type ?? "");
		return parser.TryParse(out shape) && parser.IsEnd;
	}

	static bool ThisContractsAbiSlotCompatible(ThisContract source, ThisContract target, ref bool lifetimeOnlyDifference)
	{
		if (source == target)
			return true;
		if (source.HasThis != target.HasThis || source.IsConst != target.IsConst || source.IsVolatile != target.IsVolatile)
			return false;
		if (source.Lifetime != target.Lifetime)
		{
			lifetimeOnlyDifference = true;
			return true;
		}
		return false;
	}

	static bool IsUntypedScalarShape(TypeShape shape)
	{
		return shape.Kind == TypeShapeKind.Named && shape.Name == "untyped";
	}

	static bool IsRawFunctionPointerShape(TypeShape shape)
	{
		return shape.Kind == TypeShapeKind.RawFunctionPointer;
	}

	static bool IsVoidShape(TypeShape shape)
	{
		return shape.Kind == TypeShapeKind.Named && shape.Name == "void";
	}

	static int PhysicalPointerDepth(TypeShape shape)
	{
		int depth = 0;
		while (shape.Kind == TypeShapeKind.Pointer && shape.Element is not null)
		{
			depth++;
			shape = shape.Element;
		}
		return depth;
	}

	static TypeShape InnermostPointerElement(TypeShape shape)
	{
		while (shape.Kind == TypeShapeKind.Pointer && shape.Element is not null)
			shape = shape.Element;
		return shape;
	}

	static bool ContainsQualifierRemoval(TypeShape source, TypeShape target, Func<TypeQualifiers, bool> hasQualifier)
	{
		if (hasQualifier(source.Qualifiers) && !hasQualifier(target.Qualifiers))
			return true;
		if (source.Kind != target.Kind || source.Element is null || target.Element is null)
			return false;
		return ContainsQualifierRemoval(source.Element, target.Element, hasQualifier);
	}

	static bool TypeShapesSameIgnoringAllQualifiers(TypeShape? source, TypeShape? target)
	{
		if (source is null || target is null)
			return source is null && target is null;
		if (source.Kind != target.Kind
			|| source.Name != target.Name
			|| source.TargetSpec != target.TargetSpec
			|| source.Length != target.Length)
			return false;
		return TypeShapesSameIgnoringAllQualifiers(source.Element, target.Element);
	}

	static bool TypeShapesSameIgnoringValueQualifiers(TypeShape? source, TypeShape? target)
	{
		if (source is null || target is null)
			return source is null && target is null;
		return TypeShapeParser.Format(StripValueQualifiers(source)) == TypeShapeParser.Format(StripValueQualifiers(target));
	}

	static TypeShape StripValueQualifiers(TypeShape shape)
	{
		return shape with
		{
			Qualifiers = TypeQualifiers.None,
			Element = shape.Element is null ? null : StripValueQualifiers(shape.Element)
		};
	}

	static bool IsConstructedGenericArgumentRewrite(TypeShape source, TypeShape target)
	{
		if (source.Kind != target.Kind || source.Kind != TypeShapeKind.Named)
			return false;
		if (source.Name == target.Name)
			return false;
		if (BaseTypeName(source.Name) != BaseTypeName(target.Name))
			return false;
		return ExtractConstructedTypeArguments(source.Name).Count > 0
			|| ExtractConstructedTypeArguments(target.Name).Count > 0;
	}

	static string DescribeConstructedGenericShape(TypeShape shape)
	{
		string name = BaseTypeName(shape.Name);
		List<string> arguments = ExtractConstructedTypeArguments(shape.Name);
		if (arguments.Count == 0)
			return name;
		return $"{name}<{string.Join(", ", arguments.ConvertAll(static _ => "T"))}>";
	}

	bool IsClassPointerElement(TypeShape shape)
	{
		return shape.Kind == TypeShapeKind.Named
			&& typeDefinitions.TryGetValue(BaseTypeName(shape.Name), out TypeDefinition? definition)
			&& definition is ClassDefinition;
	}

	PointerFamily GetPointerFamily(TypeShape shape)
	{
		if (IsVoidShape(shape))
			return PointerFamily.Void;
		if (shape.Kind != TypeShapeKind.Named)
			return PointerFamily.Unknown;
		string name = BaseTypeName(shape.Name);
		if (IsNumericType(name) || name is "bool" or "char" or "wchar" or "achar" or "void" or "untyped")
			return PointerFamily.Primitive;
		if (typeDefinitions.TryGetValue(name, out TypeDefinition? definition))
		{
			return definition switch
			{
				StructDefinition => PointerFamily.Struct,
				ClassDefinition => PointerFamily.Class,
				InterfaceDefinition => PointerFamily.Interface,
				_ => PointerFamily.Unknown
			};
		}
		return PointerFamily.Unknown;
	}

	bool IsGenericTargetSpecRewrite(TypeShape source, TypeShape target)
	{
		if (source.Kind != TypeShapeKind.Named || target.Kind != TypeShapeKind.Named)
			return false;
		if (source.Name == target.Name || BaseTypeName(source.Name) != BaseTypeName(target.Name))
			return false;
		if (!source.Name.Contains('<', StringComparison.Ordinal) || !target.Name.Contains('<', StringComparison.Ordinal))
			return false;
		if (selectedTarget is null)
			return ContainsPotentialTargetSpec(source.Name) || ContainsPotentialTargetSpec(target.Name);
		foreach (string spec in selectedTarget.TypeSpecOrder)
		{
			if (source.Name.Contains(spec, StringComparison.Ordinal) || target.Name.Contains(spec, StringComparison.Ordinal))
				return true;
		}
		return false;
	}

	static bool ContainsPotentialTargetSpec(string name)
	{
		return name.Contains('_', StringComparison.Ordinal);
	}

	ConversionClassification ClassifyClassPointerConversion(TypeShape source, TypeShape target)
	{
		string sourceName = BaseTypeName(source.Name);
		string targetName = BaseTypeName(target.Name);
		if (sourceName == targetName)
			return new ConversionClassification(ConversionLevel.Explicit, ConversionReason.None);
		if (IsDerivedClassType(source, target))
			return new ConversionClassification(ConversionLevel.Implicit, ConversionReason.None);
		if (IsDerivedClassType(target, source))
			return new ConversionClassification(
				ConversionLevel.Unsafe,
				ConversionReason.ClassDowncast,
				$"Cast from base class '{sourceName}' to derived class '{targetName}' requires unsafe.");
		if (ClassesShareConstructedBase(sourceName, targetName))
			return new ConversionClassification(
				ConversionLevel.Unsafe,
				ConversionReason.ClassSidecast,
				$"Cast between class pointers '{sourceName}' and '{targetName}' requires unsafe.");
		return new ConversionClassification(
			ConversionLevel.FenceRequired,
			ConversionReason.UnrelatedClass,
			$"Classes '{sourceName}' and '{targetName}' do not share a constructed base; use a raw fence before casting.");
	}

	bool ClassesShareConstructedBase(string leftName, string rightName)
	{
		if (!typeDefinitions.TryGetValue(BaseTypeName(leftName), out TypeDefinition? leftType)
			|| leftType is not ClassDefinition leftClass
			|| !typeDefinitions.TryGetValue(BaseTypeName(rightName), out TypeDefinition? rightType)
			|| rightType is not ClassDefinition rightClass)
			return false;

		HashSet<ClassDefinition> leftBases = [];
		for (ClassDefinition? current = leftClass; current is not null; current = GetDirectBaseClass(current))
			leftBases.Add(current);
		for (ClassDefinition? current = rightClass; current is not null; current = GetDirectBaseClass(current))
			if (leftBases.Contains(current))
				return true;
		return false;
	}

	bool TryAnalyzeExplicitNumericLiteralNewtypeCast(Expression? expression, string targetType, out bool allowed, out string? diagnostic)
	{
		allowed = false;
		diagnostic = null;
		if (!TryGetNewtypeUnderlyingType(targetType, out string? underlyingType) || underlyingType is null)
			return false;
		if (!TryParseIntegerLiteralValue(expression, out BigInteger value, out string literalText))
			return false;
		if (!TryGetIntegerTypeBounds(underlyingType, out BigInteger min, out BigInteger max))
			return false;

		if (value >= min && value <= max)
		{
			allowed = true;
			return true;
		}

		diagnostic = $"Numeric literal '{literalText}' is outside the range of underlying type '{underlyingType}' for newtype '{targetType}'.";
		return true;
	}

	static bool TryParseIntegerLiteralValue(Expression? expression, out BigInteger value, out string literalText)
	{
		value = BigInteger.Zero;
		literalText = "";
		if (expression is UnaryExpression { Operator: UnaryOperator.Minus, Operand: LiteralExpression { Kind: LiteralKind.Number } literal })
		{
			literalText = "-" + literal.Text;
			if (!TryParseIntegerLiteralMagnitude(literal.Text, out value))
				return false;
			value = -value;
			return true;
		}
		if (expression is UnaryExpression { Operator: UnaryOperator.Plus, Operand: LiteralExpression { Kind: LiteralKind.Number } plusLiteral })
		{
			literalText = "+" + plusLiteral.Text;
			return TryParseIntegerLiteralMagnitude(plusLiteral.Text, out value);
		}
		if (expression is not LiteralExpression { Kind: LiteralKind.Number } number)
			return false;

		literalText = number.Text;
		return TryParseIntegerLiteralMagnitude(number.Text, out value);
	}

	static bool TryParseIntegerLiteralMagnitude(string text, out BigInteger magnitude)
	{
		magnitude = BigInteger.Zero;
		if (string.IsNullOrWhiteSpace(text)
			|| text.Contains('.', StringComparison.Ordinal)
			|| text.Contains('p', StringComparison.OrdinalIgnoreCase))
			return false;

		string coreText = text;
		if (coreText.EndsWith("u", StringComparison.OrdinalIgnoreCase))
			coreText = coreText[..^1];
		else if (coreText.EndsWith("l", StringComparison.OrdinalIgnoreCase))
			return false;

		int radix = 10;
		int start = 0;
		if (coreText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			radix = 16;
			start = 2;
		}
		else if (coreText.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
		{
			radix = 2;
			start = 2;
		}
		else if (coreText.Contains('e', StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		if (start >= coreText.Length)
			return false;

		for (int i = start; i < coreText.Length; i++)
		{
			char ch = coreText[i];
			if (ch == '_')
				continue;

			int digit = ch switch
			{
				>= '0' and <= '9' => ch - '0',
				>= 'a' and <= 'f' => ch - 'a' + 10,
				>= 'A' and <= 'F' => ch - 'A' + 10,
				_ => -1
			};
			if (digit < 0 || digit >= radix)
				return false;

			magnitude = magnitude * radix + digit;
		}

		return true;
	}

	bool TryGetIntegerTypeBounds(string type, out BigInteger min, out BigInteger max)
	{
		min = BigInteger.Zero;
		max = BigInteger.Zero;
		type = StripTopLevelValueQualifiers(type);
		string? targetSpec = null;
		if (TryParseTypeShape(type, out TypeShape shape) && shape.Kind == TypeShapeKind.Named)
		{
			type = shape.Name;
			targetSpec = shape.TargetSpec;
		}

		return type switch
		{
			"byte" or "char" or "achar" => UnsignedBounds(8, out min, out max),
			"sbyte" => SignedBounds(8, out min, out max),
			"ushort" or "wchar" => UnsignedBounds(16, out min, out max),
			"short" => SignedBounds(16, out min, out max),
			"uint" or "uchar" => UnsignedBounds(32, out min, out max),
			"int" => SignedBounds(32, out min, out max),
			"ulong" => UnsignedBounds(64, out min, out max),
			"long" => SignedBounds(64, out min, out max),
			"nuint" => UnsignedBounds(selectedTarget?.Capabilities.GetNaturalIntegerWidth(targetSpec) ?? 32, out min, out max),
			"nint" => SignedBounds(selectedTarget?.Capabilities.GetNaturalIntegerWidth(targetSpec) ?? 32, out min, out max),
			_ => false
		};
	}

	static bool SignedBounds(int bits, out BigInteger min, out BigInteger max)
	{
		max = (BigInteger.One << (bits - 1)) - BigInteger.One;
		min = -(BigInteger.One << (bits - 1));
		return true;
	}

	static bool UnsignedBounds(int bits, out BigInteger min, out BigInteger max)
	{
		min = BigInteger.Zero;
		max = (BigInteger.One << bits) - BigInteger.One;
		return true;
	}

	bool CanExplicitlyConvertCallable(string source, string target)
	{
		if (!TryGetCallableShape(source, out CallableShape sourceCallable)
			|| !TryGetCallableShape(target, out CallableShape targetCallable))
			return false;

		return sourceCallable.Kind == targetCallable.Kind
			&& sourceCallable.ReturnType == targetCallable.ReturnType
			&& sourceCallable.Parameters.Count == targetCallable.Parameters.Count;
	}

	bool IsNewtypeOrEnumBoundary(string source, string target)
	{
		return TryGetUnderlyingNumericType(source, out _) || TryGetUnderlyingNumericType(target, out _);
	}

	string GetForeachElementType(string sourceType, bool isAwaited, SyntaxNode? syntax)
	{
		if (TryGetArrayElementType(sourceType) is string arrayElement)
			return isAwaited ? ReportType(syntax, "await foreach requires an async iter source, not an array.") : arrayElement;

		if (TryGetIteratorProtocolCurrentTypes(sourceType, out List<string>? currentTypes) && currentTypes is { Count: 1 })
			return isAwaited ? ReportType(syntax, "await foreach requires an async iter source, not an iter source.") : currentTypes[0];

		if (sourceType.StartsWith("async iter ", StringComparison.Ordinal) || sourceType.StartsWith("async iter(", StringComparison.Ordinal))
			return ReportType(syntax, isAwaited ? "Async iterator foreach is not implemented yet." : "foreach requires an array or iterator state source.");

		Report(GetRange(syntax), $"Foreach source type '{sourceType}' is not iterable.");
		return ErrorType;
	}

	string GetForeachElementType(ForeachStatement statement, string sourceType, SyntaxNode? syntax)
	{
		if (TryGetArrayElementType(sourceType) is string arrayElement)
			return statement.IsAwaited ? ReportType(syntax, "await foreach requires an async iter source, not an array.") : arrayElement;

		if (TryFindIteratorNextMethod(sourceType, out FunctionDefinition? next, out string elementType))
		{
			if (statement.IsAwaited)
				return ReportType(syntax, "await foreach over iterator states is not implemented yet.");
			statement.IteratorNext = next;
			return elementType;
		}

		return GetForeachElementType(sourceType, statement.IsAwaited, syntax);
	}

	bool TryFindIteratorNextMethod(string sourceType, out FunctionDefinition? next, out string elementType)
	{
		next = null;
		elementType = ErrorType;
		if (GetTypeDefinition(sourceType) is not TypeDefinition type)
			return false;

		List<string> typeArguments = ExtractConstructedTypeArguments(sourceType);
		Dictionary<string, string> substitutions = [];
		int count = Math.Min(type.GenericParameters.Count, typeArguments.Count);
		for (int i = 0; i < count; i++)
			substitutions[type.GenericParameters[i].Name] = typeArguments[i];

		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (function.Name != "next" || function.ResolvedType != "bool")
				continue;

			List<ParameterDefinition> parameters = GetCallableParameters(function.Parameters);
			if (parameters.Count == 0)
				continue;

			string? yieldedType = TryGetPointerElementType(parameters[0].ResolvedType);
			if (yieldedType is null)
				continue;

			next = function;
			elementType = substitutions.Count > 0
				? SubstituteGenericType(yieldedType, substitutions)
				: yieldedType;
			return true;
		}

		return false;
	}

	bool IsAwaitable(Expression? expression, BodyScope scope, AnalysisScope typeScope)
	{
		if (!TryGetAwaitedFunction(expression, scope, typeScope, out FunctionDefinition? function))
			return false;
		if (function is null)
			return false;
		return function.IsAsync || HasAwaitableCallback(function.Parameters);
	}

	string GetAwaitableDiagnostic(Expression? expression, BodyScope scope, AnalysisScope typeScope)
	{
		if (expression is UnaryExpression)
			return "Await operand must be a direct call expression; prefix operators between await and the call are not allowed.";

		if (expression is not CallExpression and not MemberExpression)
			return "Await operand must be a call expression.";

		if (expression is CallExpression { Target: UnaryExpression })
			return "Await operand must be a direct call expression; prefix operators between await and the call are not allowed.";

		if (TryGetAwaitedFunction(expression, scope, typeScope, out FunctionDefinition? function))
		{
			if (function is null)
				return "Await target is not awaitable.";
			if (function.IsAsync)
				return "Await target is awaitable.";
			if (function.Parameters is not [.., ParameterDefinition last]
				|| !TryGetCallableTypeReference(last.Type, out CallableTypeReference? callback)
				|| callback is null)
				return "Awaited call is missing the final once completion callback parameter.";
			if (callback.Kind != CallableKind.Once)
				return "Awaited completion callback must be a once callable.";
			if (callback.ReturnType is not PrimitiveTypeReference { Type: PrimitiveType.Void })
				return "Awaited completion callback must return void.";

			int successSlots = 0;
			int thrownSlots = 0;
			foreach (ParameterDefinition parameter in callback.Parameters)
			{
				if (parameter.Modifier == ParameterModifier.Out)
					return "Awaited completion callback may not contain out parameters.";
				if (parameter.Modifier == ParameterModifier.Thrown)
					thrownSlots++;
				else
					successSlots++;
			}
			if (thrownSlots > 1)
				return "Awaited completion callback may contain at most one thrown parameter.";
			if (successSlots > 1)
				return "Awaited completion callback may contain at most one non-error result parameter; multi-result await is not supported.";
		}

		return "Await target is not awaitable.";
	}

	string GetAwaitedType(Expression? expression, BodyScope scope, AnalysisScope typeScope)
	{
		if (TryGetAwaitedFunction(expression, scope, typeScope, out FunctionDefinition? function))
		{
			if (function is null)
				return ErrorType;
			if (!function.IsAsync && TryGetAwaitableCallbackSuccessType(function.Parameters, out string successType))
				return successType;
			return function.ResolvedType == "void" ? "void" : function.ResolvedType ?? ErrorType;
		}

		return ErrorType;
	}

	bool TryGetAwaitedFunction(Expression? expression, BodyScope scope, AnalysisScope typeScope, out FunctionDefinition? function)
	{
		function = null;
		if (expression is CallExpression call)
		{
			function = ResolveCallTarget(call.Target, scope, typeScope, call.Arguments);
			if (function is not null)
				return true;
			if (call.Target is MemberExpression callMember
				&& expressionRewrites.TryGetValue(callMember, out Expression? callMemberRewrite)
				&& callMemberRewrite is MemberReferenceExpression callMemberReference
				&& callMemberReference.Member is FunctionDefinition propertyGetter
				&& IsPropertyGetterReference(callMemberReference))
			{
				function = propertyGetter;
				return true;
			}
			return false;
		}

		if (expression is MemberExpression member
			&& expressionRewrites.TryGetValue(member, out Expression? rewritten)
			&& rewritten is MemberReferenceExpression reference
			&& reference.Member is FunctionDefinition getter
			&& IsPropertyGetterReference(reference))
		{
			function = getter;
			return true;
		}

		return false;
	}

	static bool HasAwaitableCallback(List<ParameterDefinition> parameters)
	{
		if (parameters is not [.., ParameterDefinition last]
			|| !TryGetCallableTypeReference(last.Type, out CallableTypeReference? callback)
			|| callback is not { Kind: CallableKind.Once, ReturnType: PrimitiveTypeReference { Type: PrimitiveType.Void } })
			return false;

		int successSlots = 0;
		int thrownSlots = 0;
		foreach (ParameterDefinition parameter in callback.Parameters)
		{
			if (parameter.Modifier == ParameterModifier.Out)
				return false;
			if (parameter.Modifier == ParameterModifier.Thrown)
				thrownSlots++;
			else
				successSlots++;
		}
		return successSlots <= 1 && thrownSlots <= 1;
	}

	static bool TryGetAwaitableCallbackSuccessType(List<ParameterDefinition> parameters, out string successType)
	{
		successType = "void";
		if (parameters is not [.., ParameterDefinition last]
			|| !TryGetCallableTypeReference(last.Type, out CallableTypeReference? callback)
			|| callback is not { Kind: CallableKind.Once, ReturnType: PrimitiveTypeReference { Type: PrimitiveType.Void } })
			return false;
		foreach (ParameterDefinition parameter in callback.Parameters)
		{
			if (parameter.Modifier is ParameterModifier.Thrown)
				continue;
			if (parameter.Modifier is ParameterModifier.Out)
				return false;
			if (successType != "void")
				return false;
			successType = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? ErrorType;
		}
		return true;
	}

	bool IsSwitchableType(string type)
	{
		return IsNumericType(type) || IsEnumType(type) || TryGetUnderlyingNumericType(type, out _);
	}

	bool IsConstant(Expression? expression)
	{
		return expression is not null && expressionConstants.TryGetValue(expression, out bool isConstant) && isConstant;
	}

	void AddTypeMembersToScope(BodyScope scope, TypeDefinition type)
	{
		switch (type)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
					scope.MemberSymbols[field.Name] = new BodySymbol(field.Name, field.ResolvedType ?? ErrorType, field);
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					scope.MemberSymbols[field.Name] = new BodySymbol(field.Name, field.ResolvedType ?? ErrorType, field);
				break;
		}
	}

	bool TryGetUnqualifiedInstanceMember(TypeDefinition? type, string name, SyntaxNode? referenceSyntax, out string memberKind)
	{
		memberKind = "";
		if (type is null)
			return false;

		foreach (TypeDefinition candidateType in EnumerateTypeAndBases(type))
		{
			foreach (FieldDefinition field in GetTypeFields(candidateType))
			{
				if (field.Name == name)
				{
					memberKind = "Member field";
					return true;
				}

				if (TryGetParamsComponentShape(field.Type, field.ResolvedType, field.Name, out ParamsComponentShape shape))
				{
					foreach (ParamsComponent component in shape.Components)
					{
						if (component.ExpandedName == name)
						{
							memberKind = "Member field";
							return true;
						}
					}
				}
			}

			foreach (FunctionDefinition function in LookupTypeFunctions(candidateType, name, referenceSyntax))
			{
				if (!IsInstanceFunction(function))
					continue;
				memberKind = "Member method";
				return true;
			}

			foreach (FunctionDefinition getter in LookupTypeFunctions(candidateType, "get" + name, referenceSyntax))
			{
				if (!IsInstanceFunction(getter))
					continue;
				memberKind = "Member property";
				return true;
			}
		}

		return false;
	}

	bool CanExplicitlyConvertPrimitiveStringPointer(string source, string target)
	{
		return CanConvertPrimitiveStringToPointer(source, target)
			|| CanConvertPrimitiveStringToPointer(target, source)
			|| IsPrimitiveStringVoidPointerPair(source, target)
			|| IsPrimitiveStringPointerPair(source, target);
	}

	bool IsPrimitiveStringVoidPointerPair(string source, string target)
	{
		return StripTopLevelValueQualifiers(source) == "void*" && IsPrimitiveStringType(target)
			|| IsPrimitiveStringType(source) && StripTopLevelValueQualifiers(target) == "void*";
	}

	bool IsPrimitiveStringPointerPair(string source, string target)
	{
		return TryGetPrimitiveStringPointerElement(source, out string sourceElement)
			&& TryGetPrimitiveStringPointerElement(target, out string targetElement)
			&& sourceElement == targetElement;
	}

	bool TryGetPrimitiveStringPointerElement(string type, out string element)
	{
		element = StripTopLevelValueQualifiers(type) switch
		{
			"string" => "char",
			"wstring" => "wchar",
			"astring" => "achar",
			_ => ""
		};
		if (element.Length > 0)
			return true;

		if (!TryParseTypeShape(type, out TypeShape shape) || shape.Kind != TypeShapeKind.Pointer || shape.Element is null)
			return false;
		element = StripTopLevelValueQualifiers(TypeShapeParser.Format(shape.Element));
		return element is "char" or "wchar" or "achar";
	}

	List<FunctionDefinition> LookupFunctions(string name, BodyScope scope)
	{
		if (TryResolveAlias(name, AliasTargetKind.Callable, scope.CurrentFunction.SourceSyntax, out AliasDefinition? alias))
			name = alias!.ResolvedTargetName;

		List<FunctionDefinition> functions = [];
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition is FunctionDefinition function && IsCallableTopLevelFunctionNamed(function, name) && IsDefinitionVisible(function, scope.CurrentFunction.SourceSyntax))
				functions.Add(function);
		}
		foreach (TypeDefinition type in typeDefinitions.Values)
		{
			foreach (FunctionDefinition function in GetTypeFunctions(type))
			{
				if (IsTypeFunctionSymbolNamed(type, function, name) && IsMemberVisible(function, type, scope.CurrentFunction.SourceSyntax))
					functions.Add(function);
			}
		}

		return functions;
	}

	static bool IsCallableTopLevelFunctionNamed(FunctionDefinition function, string name)
	{
		return IsFunctionNamed(function, name);
	}

	IEnumerable<TypeDefinition> EnumerateTypeAndBases(TypeDefinition type)
	{
		if (type is ClassDefinition classDefinition)
		{
			foreach (ClassDefinition candidate in EnumerateClassAndBases(classDefinition))
				yield return candidate;
			yield break;
		}

		yield return type;
	}

	static IEnumerable<FieldDefinition> GetTypeFields(TypeDefinition type)
	{
		return type switch
		{
			ClassDefinition classDefinition => classDefinition.Fields,
			StructDefinition structDefinition => structDefinition.Fields,
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Fields,
			_ => []
		};
	}

	static bool IsFunctionNamed(FunctionDefinition function, string name)
	{
		return function.Name == name || GetCallableName(function) == name || function.Symbol == name;
	}

	static bool IsDefinitionNamed(Definition definition, string name)
	{
		return definition.Name == name || definition.Symbol == name;
	}

	static bool IsTypeFunctionSymbolNamed(TypeDefinition type, FunctionDefinition function, string name)
	{
		return (!string.IsNullOrWhiteSpace(function.Symbol) && function.Symbol != function.Name && function.Symbol == name)
			|| (!function.SymbolOverridden && $"{type.Name}_{GetCallableName(function).TrimStart('~')}" == name);
	}

	BodySymbol? LookupGlobalStorageSymbol(string name, SyntaxNode? referenceSyntax)
	{
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition is VariableDefinition variable && IsDefinitionNamed(variable, name) && IsDefinitionVisible(variable, referenceSyntax))
				return new BodySymbol(name, variable.ResolvedType ?? variable.Type?.ResolvedType ?? ErrorType, variable, IsConstantVariable(variable));
			if (definition is TypeDefinition type)
			{
				foreach (FieldDefinition field in GetTypeFields(type))
				{
					if (field.Modifier == FieldModifier.Static && IsDefinitionNamed(field, name) && IsMemberVisible(field, type, referenceSyntax))
						return new BodySymbol(name, field.ResolvedType ?? field.Type?.ResolvedType ?? ErrorType, field, IsConstantField(field));
				}
			}
		}

		return null;
	}

	Definition? LookupHiddenGlobalSymbol(string name, SyntaxNode? referenceSyntax)
	{
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (IsDefinitionNamed(definition, name) && !IsDefinitionVisible(definition, referenceSyntax))
				return definition;
			if (definition is TypeDefinition type)
			{
				foreach (FieldDefinition field in GetTypeFields(type))
					if (field.Modifier == FieldModifier.Static && IsDefinitionNamed(field, name) && !IsMemberVisible(field, type, referenceSyntax))
						return field;
			}
		}

		return null;
	}

	List<FunctionDefinition> LookupTypeFunctions(TypeDefinition type, string name, SyntaxNode? referenceSyntax)
	{
		List<FunctionDefinition> functions = [];
		if (type is ClassDefinition classDefinition)
		{
			foreach (ClassDefinition candidateClass in EnumerateClassAndBases(classDefinition))
			{
				foreach (FunctionDefinition function in candidateClass.Functions)
				{
					if ((function.Name == name || GetCallableName(function) == name) && !IsBodylessVirtualOverrideDeclaration(function) && IsMemberVisible(function, candidateClass, referenceSyntax))
						functions.Add(function);
				}

				if (functions.Count > 0)
					return functions;
			}

			return functions;
		}

		IEnumerable<FunctionDefinition> candidates = type switch
		{
			StructDefinition structDefinition => structDefinition.Functions,
			InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
			EnumDefinition enumDefinition => enumDefinition.Functions,
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions,
			ParamsDefinition paramsDefinition => paramsDefinition.Functions,
			_ => []
		};

		foreach (FunctionDefinition function in candidates)
		{
			if ((function.Name == name || GetCallableName(function) == name) && IsMemberVisible(function, type, referenceSyntax))
				functions.Add(function);
		}

		return functions;
	}

	FunctionDefinition? LookupHiddenTypeFunction(TypeDefinition type, string name, SyntaxNode? referenceSyntax)
	{
		if (type is ClassDefinition classDefinition)
		{
			foreach (ClassDefinition candidateClass in EnumerateClassAndBases(classDefinition))
			{
				foreach (FunctionDefinition function in candidateClass.Functions)
				{
					if ((function.Name == name || GetCallableName(function) == name) && !IsBodylessVirtualOverrideDeclaration(function) && !IsMemberVisible(function, candidateClass, referenceSyntax))
						return function;
				}
			}
			return null;
		}

		foreach (FunctionDefinition function in GetTypeFunctions(type))
		{
			if ((function.Name == name || GetCallableName(function) == name) && !IsMemberVisible(function, type, referenceSyntax))
				return function;
		}

		return null;
	}

	Definition? LookupHiddenMember(TypeDefinition type, string name, SyntaxNode? referenceSyntax)
	{
		if (LookupHiddenTypeFunction(type, name, referenceSyntax) is FunctionDefinition hiddenFunction)
			return hiddenFunction;

		switch (type)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
				{
					if (field.Name == name && !IsMemberVisible(field, classDefinition, referenceSyntax))
						return field;
				}
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
				{
					if (field.Name == name && !IsMemberVisible(field, structDefinition, referenceSyntax))
						return field;
				}
				break;

			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in GetInterfaceMembers(interfaceDefinition))
				{
					if ((GetSignatureName(function) == name || function.Name == name) && !IsMemberVisible(function, interfaceDefinition, referenceSyntax))
						return function;
				}
				break;
		}

		return null;
	}

	bool IsBodylessVirtualOverrideDeclaration(FunctionDefinition function)
	{
		return function.Body is null
			&& function.Modifier is FunctionModifier.Override or FunctionModifier.Sealed
			&& virtualImplementations.ContainsKey(function);
	}

	List<FunctionDefinition> LookupMemberFunctions(string targetType, string name, SyntaxNode? referenceSyntax)
	{
		if (TryGetPointerElementType(targetType) is string interfaceElement
			&& typeDefinitions.TryGetValue(BaseTypeName(interfaceElement), out TypeDefinition? interfaceType)
			&& interfaceType is InterfaceDefinition interfaceDefinition)
		{
			List<FunctionDefinition> functions = [];
			foreach (FunctionDefinition function in GetInterfaceMembers(interfaceDefinition))
			{
				if ((GetSignatureName(function) == name || function.Name == name) && IsMemberVisible(function, interfaceDefinition, referenceSyntax))
				{
					if (ReceiverCanCallFunction(targetType, function, IsPropertyGetterFunction(function)))
						functions.Add(function);
				}
			}
			return functions;
		}

		if (!typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? type))
			return LookupExtensionFunctions(targetType, name, referenceSyntax);

		List<FunctionDefinition> callable = [];
		foreach (FunctionDefinition function in LookupTypeFunctions(type, name, referenceSyntax))
		{
			if (ReceiverCanCallFunction(targetType, function, IsPropertyGetterFunction(function)))
				callable.Add(function);
		}
		AddExtensionMemberFunctions(callable, targetType, name, referenceSyntax);
		return callable;
	}

	List<FunctionDefinition> LookupStaticMemberFunctions(string targetType, string name, SyntaxNode? referenceSyntax)
	{
		if (!typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? type))
			return [];

		List<FunctionDefinition> functions = [];
		foreach (FunctionDefinition function in LookupTypeFunctions(type, name, referenceSyntax))
			if (function.Modifier == FunctionModifier.Static)
				functions.Add(function);
		return functions;
	}

	List<FunctionDefinition> LookupGenericConstraintMemberFunctions(string targetType, string name, BodyScope scope, SyntaxNode? referenceSyntax)
	{
		if (!TryGetGenericConstraintInterface(targetType, scope, out InterfaceDefinition? interfaceDefinition) || interfaceDefinition is null)
			return [];

		List<FunctionDefinition> functions = [];
		foreach (FunctionDefinition function in GetInterfaceMembers(interfaceDefinition))
		{
			if ((GetSignatureName(function) == name || function.Name == name) && IsMemberVisible(function, interfaceDefinition, referenceSyntax))
				functions.Add(function);
		}
		return functions;
	}

	List<BodySymbol> LookupMemberSymbols(string targetType, string name, SyntaxNode? referenceSyntax)
	{
		List<BodySymbol> members = [];
		if (TryGetParamsComponentShape(null, targetType, "value", out ParamsComponentShape expandedShape))
		{
			foreach (ParamsComponent component in expandedShape.Components)
				if (component.Name == name)
					members.Add(new BodySymbol(name, component.Type, new ParameterDefinition
					{
						Name = component.Name,
						Symbol = component.ExpandedName,
						ResolvedType = component.Type
					}));
			if (members.Count > 0)
				return members;
		}

		if (TryGetPointerElementType(targetType) is string interfaceElement
			&& typeDefinitions.TryGetValue(BaseTypeName(interfaceElement), out TypeDefinition? interfaceType)
			&& interfaceType is InterfaceDefinition interfaceDefinition)
		{
			foreach (FunctionDefinition function in GetInterfaceMembers(interfaceDefinition))
			{
				if ((GetSignatureName(function) == name || function.Name == name) && IsMemberVisible(function, interfaceDefinition, referenceSyntax))
					members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: true, allowCallableAscription: true), function));
				if (function.Name == "get" + name && function.Parameters.Count == 0 && IsMemberVisible(function, interfaceDefinition, referenceSyntax))
					members.Add(new BodySymbol(name, GetGetterMemberType(targetType, function, referenceSyntax), function));
			}
			return members;
		}

		if (!typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? type))
		{
			foreach (FunctionDefinition function in LookupExtensionFunctions(targetType, name, referenceSyntax))
				members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: true, allowCallableAscription: true), function));
			foreach (FunctionDefinition getter in LookupExtensionFunctions(targetType, "get" + name, referenceSyntax))
			{
				if (CanCallWithArgumentCount(getter.Parameters, 0))
					members.Add(new BodySymbol(name, GetGetterMemberType(targetType, getter, referenceSyntax), getter));
			}
			return members;
		}

		switch (type)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
				{
					if (field.Name == name && IsMemberVisible(field, classDefinition, referenceSyntax))
						members.Add(new BodySymbol(name, field.ResolvedType ?? ErrorType, field));
					AddExpandedFieldMemberSymbol(members, field, name, classDefinition, referenceSyntax);
				}
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
				{
					if (field.Name == name && IsMemberVisible(field, structDefinition, referenceSyntax))
						members.Add(new BodySymbol(name, field.ResolvedType ?? ErrorType, field));
					AddExpandedFieldMemberSymbol(members, field, name, structDefinition, referenceSyntax);
				}
				break;

			case EnumDefinition enumDefinition:
				foreach (VariableDefinition value in enumDefinition.Values)
				{
					if (value.Name == name)
						members.Add(new BodySymbol(name, value.ResolvedType ?? enumDefinition.Name, value, IsConstant: true));
				}
				break;

			case ParamsDefinition paramsDefinition:
				foreach (ParameterDefinition component in paramsDefinition.Components)
				{
					if (component.Name == name)
						members.Add(new BodySymbol(name, component.ResolvedType ?? ErrorType, component));
				}
				break;
		}

		foreach (FunctionDefinition function in LookupTypeFunctions(type, name, referenceSyntax))
		{
			if (ReceiverCanCallFunction(targetType, function, IsPropertyGetterFunction(function)))
				members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: true, allowCallableAscription: true), function));
		}
		foreach (FunctionDefinition function in LookupExtensionFunctions(targetType, name, referenceSyntax))
			members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: true, allowCallableAscription: true), function));

		foreach (FunctionDefinition getter in LookupTypeFunctions(type, "get" + name, referenceSyntax))
		{
			if (getter.Parameters.Count == 0 && ReceiverCanCallFunction(targetType, getter, isPropertyGetterSyntax: true))
				members.Add(new BodySymbol(name, GetGetterMemberType(targetType, getter, referenceSyntax), getter));
		}
		foreach (FunctionDefinition getter in LookupExtensionFunctions(targetType, "get" + name, referenceSyntax))
		{
			if (CanCallWithArgumentCount(getter.Parameters, 0))
				members.Add(new BodySymbol(name, GetGetterMemberType(targetType, getter, referenceSyntax), getter));
		}

		return members;
	}

	string GetGetterMemberType(string targetType, FunctionDefinition getter, SyntaxNode? syntax)
	{
		Dictionary<string, bool> constOfAnchors = [];
		AddReceiverConstOfAnchorFact(targetType, getter, constOfAnchors, syntax);
		return SubstituteConstOfResolvedType(getter.ReturnType, getter.ResolvedType ?? ErrorType, constOfAnchors);
	}

	List<BodySymbol> LookupStaticMemberSymbols(string targetType, string name, SyntaxNode? referenceSyntax)
	{
		List<BodySymbol> members = [];
		if (!typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? type))
			return members;

		foreach (FieldDefinition field in GetTypeFields(type))
		{
			if (field.Modifier == FieldModifier.Static && field.Name == name && IsMemberVisible(field, type, referenceSyntax))
				members.Add(new BodySymbol(name, field.ResolvedType ?? ErrorType, field, IsConstantField(field)));
		}

		if (type is EnumDefinition enumDefinition)
		{
			foreach (VariableDefinition value in enumDefinition.Values)
			{
				if (value.Name == name)
					members.Add(new BodySymbol(name, value.ResolvedType ?? enumDefinition.Name, value, IsConstant: true));
			}
		}

		foreach (FunctionDefinition function in LookupTypeFunctions(type, name, referenceSyntax))
		{
			if (function.Modifier == FunctionModifier.Static)
				members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: false, allowCallableAscription: true), function));
		}
		foreach (FunctionDefinition getter in LookupTypeFunctions(type, "get" + name, referenceSyntax))
		{
			if (getter.Modifier == FunctionModifier.Static && getter.Parameters.Count == 0)
				members.Add(new BodySymbol(name, getter.ResolvedType ?? ErrorType, getter));
		}

		return members;
	}

	List<BodySymbol> LookupGenericConstraintMemberSymbols(string targetType, string name, BodyScope scope, SyntaxNode? referenceSyntax)
	{
		if (!TryGetGenericConstraintInterface(targetType, scope, out InterfaceDefinition? interfaceDefinition) || interfaceDefinition is null)
			return [];

		List<BodySymbol> members = [];
		foreach (FunctionDefinition function in GetInterfaceMembers(interfaceDefinition))
		{
			if ((GetSignatureName(function) == name || function.Name == name) && IsMemberVisible(function, interfaceDefinition, referenceSyntax))
				members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: true, allowCallableAscription: true), function));
		}
		return members;
	}

	bool TryGetGenericConstraintInterface(string targetType, BodyScope scope, out InterfaceDefinition? interfaceDefinition)
	{
		interfaceDefinition = null;
		string genericName = TryGetPointerElementType(targetType) ?? BaseTypeName(targetType);
		GenericParameter? parameter = FindBodyGenericParameter(scope, genericName);
		if (parameter is null || !parameter.RequiresImplementation || parameter.Constraint is null)
			return false;

		if (!TryGetInterfaceDefinition(parameter.Constraint, out interfaceDefinition) || interfaceDefinition is null)
			return false;

		return true;
	}

	static GenericParameter? FindBodyGenericParameter(BodyScope scope, string name)
	{
		foreach (GenericParameter parameter in scope.CurrentFunction.GenericParameters)
			if (parameter.Name == name)
				return parameter;
		if (scope.ContainingType is not null)
			foreach (GenericParameter parameter in scope.ContainingType.GenericParameters)
				if (parameter.Name == name)
					return parameter;
		return null;
	}

	void AddExpandedFieldMemberSymbol(List<BodySymbol> members, FieldDefinition field, string name, TypeDefinition owner, SyntaxNode? referenceSyntax)
	{
		if (!IsMemberVisible(field, owner, referenceSyntax))
			return;
		if (!TryGetParamsComponentShape(field.Type, field.ResolvedType, field.Name, out ParamsComponentShape shape))
			return;

		foreach (ParamsComponent component in shape.Components)
		{
			if (component.ExpandedName == field.Name || component.ExpandedName != name)
				continue;

			FieldDefinition componentField = new()
			{
				SourceSyntax = field.SourceSyntax,
				Name = component.ExpandedName,
				Symbol = component.ExpandedName,
				Export = field.Export,
				Public = field.Public,
				Extern = field.Extern,
				Modifier = field.Modifier,
				ResolvedType = component.Type
			};
			members.Add(new BodySymbol(name, component.Type, componentField));
			return;
		}
	}

	void AddExtensionMemberFunctions(List<FunctionDefinition> target, string targetType, string name, SyntaxNode? referenceSyntax)
	{
		foreach (FunctionDefinition function in LookupExtensionFunctions(targetType, name, referenceSyntax))
			target.Add(function);
	}

	List<FunctionDefinition> LookupExtensionFunctions(string targetType, string name, SyntaxNode? referenceSyntax)
	{
		List<FunctionDefinition> exactReceiverFunctions = [];
		List<FunctionDefinition> incompatibleExactReceiverFunctions = [];
		List<FunctionDefinition> exactPrimitiveStringFunctions = [];
		List<FunctionDefinition> functions = [];
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition is not FunctionDefinition function || (function.Name != name && GetCallableName(function) != name) || !IsDefinitionVisible(function, referenceSyntax))
				continue;
			if (GetExplicitThisParameter(function) is null && !HasExpandedThisParameters(function.Parameters))
				continue;
			bool canCall = ReceiverCanCallFunction(targetType, function, IsPropertyGetterFunction(function));
			if (IsConcreteReceiverShapeMatch(targetType, function, IsPropertyGetterFunction(function)))
			{
				if (canCall)
					exactReceiverFunctions.Add(function);
				else
					incompatibleExactReceiverFunctions.Add(function);
				continue;
			}
			if (!canCall)
				continue;

			if (RequiresPrimitiveStringSpanLength(targetType, function, IsPropertyGetterFunction(function))
				&& !PrimitiveStringHasLengthProvider(targetType, referenceSyntax))
				Report(GetRange(referenceSyntax), $"Cannot implicitly convert '{targetType}' to '{PrimitiveStringConstArrayType(targetType)}' because no accessible Length property or getLength() method was found.");

			if (IsExactReceiver(targetType, function, IsPropertyGetterFunction(function)))
				exactReceiverFunctions.Add(function);
			else if (IsExactPrimitiveStringReceiver(targetType, function, IsPropertyGetterFunction(function)))
				exactPrimitiveStringFunctions.Add(function);
			else
				functions.Add(function);
		}
		if (exactReceiverFunctions.Count > 0)
			return exactReceiverFunctions;
		if (incompatibleExactReceiverFunctions.Count > 0)
		{
			Report(GetRange(referenceSyntax), ReceiverIncompatibilityMessage("Member", name, targetType));
			return incompatibleExactReceiverFunctions;
		}
		return exactPrimitiveStringFunctions.Count > 0 ? exactPrimitiveStringFunctions : functions;
	}

	bool IsFixedArrayPointerReceiverMismatch(string targetType, FunctionDefinition function, bool isPropertyGetterSyntax)
	{
		string receiverType = BuildEffectiveReceiverType(targetType, function, isPropertyGetterSyntax);
		return WouldDecayFixedArrayStorageToPointerReceiver(StripTopLevelConstForReceiver(targetType), receiverType);
	}

	bool IsExactReceiver(string targetType, FunctionDefinition function, bool isPropertyGetterSyntax)
	{
		string receiverType = BuildEffectiveReceiverType(targetType, function, isPropertyGetterSyntax);
		return StripTopLevelValueQualifiers(receiverType) == StripTopLevelValueQualifiers(targetType);
	}

	bool IsConcreteReceiverShapeMatch(string targetType, FunctionDefinition function, bool isPropertyGetterSyntax)
	{
		string receiverType = BuildEffectiveReceiverType(targetType, function, isPropertyGetterSyntax);
		if (!TryParseTypeShape(targetType, out TypeShape targetShape) || !TryParseTypeShape(receiverType, out TypeShape receiverShape))
			return false;
		if (ContainsGenericPlaceholder(receiverShape))
			return false;
		return SameReceiverShapeIgnoringQualifiers(targetShape, receiverShape);
	}

	bool ContainsGenericPlaceholder(TypeShape shape)
	{
		if (shape.Kind == TypeShapeKind.Named && IsGenericPlaceholderParameter(shape.Name))
			return true;
		return shape.Element is not null && ContainsGenericPlaceholder(shape.Element);
	}

	static bool SameReceiverShapeIgnoringQualifiers(TypeShape left, TypeShape right)
	{
		if (left.Kind != right.Kind)
			return false;
		if (left.Kind == TypeShapeKind.Named)
			return left.Name == right.Name;
		return left.Element is not null && right.Element is not null && SameReceiverShapeIgnoringQualifiers(left.Element, right.Element);
	}

	bool RequiresPrimitiveStringSpanLength(string targetType, FunctionDefinition function, bool isPropertyGetterSyntax)
	{
		if (!IsPrimitiveStringType(targetType))
			return false;
		string receiverType = BuildEffectiveReceiverType(targetType, function, isPropertyGetterSyntax);
		return CanConvertPrimitiveStringToConstArray(targetType, receiverType)
			&& StripTopLevelValueQualifiers(receiverType) != StripTopLevelValueQualifiers(targetType);
	}

	bool PrimitiveStringHasLengthProvider(string targetType, SyntaxNode? referenceSyntax)
	{
		foreach (FunctionDefinition function in LookupExtensionFunctions(targetType, "getLength", referenceSyntax))
			if (ReceiverCanCallFunction(targetType, function, IsPropertyGetterFunction(function)))
				return true;
		return false;
	}

	string PrimitiveStringConstArrayType(string type)
	{
		string element = GetPrimitiveStringElementType(type) ?? "char";
		return $"const {element}[]";
	}

	bool IsExactPrimitiveStringReceiver(string targetType, FunctionDefinition function, bool isPropertyGetterSyntax)
	{
		if (!IsPrimitiveStringType(targetType))
			return false;
		string receiverType = BuildEffectiveReceiverType(targetType, function, isPropertyGetterSyntax);
		return StripTopLevelValueQualifiers(receiverType) == StripTopLevelValueQualifiers(targetType);
	}

	bool TryAnalyzePropertyIndexer(MemberExpression member, List<ArgumentExpression> arguments, BodyScope scope, AnalysisScope typeScope, out string propertyType)
	{
		propertyType = ErrorType;
		string targetType = BodyAnalyzeExpression(member.Target, scope, typeScope);
		TypeDefinition? type = GetTypeDefinition(targetType);

		List<FunctionDefinition> getters = type is null ? [] : LookupPropertyGetters(type, member.Name, member.SourceSyntax);
		getters.AddRange(LookupExtensionFunctions(targetType, "get" + member.Name, member.SourceSyntax));
		if (getters.Count > 1 && TrySelectOverload("get" + member.Name, getters, arguments, scope, typeScope, member.SourceSyntax) is FunctionDefinition selectedGetter)
			getters = [selectedGetter];
		bool getterReceiverMismatch = false;
		foreach (FunctionDefinition getter in getters)
		{
			if (!ReceiverCanCallFunction(targetType, getter, isPropertyGetterSyntax: true))
			{
				getterReceiverMismatch = true;
				continue;
			}

			if (CanCallWithArgumentCount(getter.Parameters, HasRangeArgument(arguments) ? arguments.Count + 1 : arguments.Count))
			{
				Dictionary<string, string> genericSubstitutions = [];
				AddReceiverTypeGenericSubstitutions(targetType, getter, genericSubstitutions);
				Dictionary<string, bool> constOfAnchors = AnalyzeCallArguments(arguments, getter.Parameters, scope, typeScope, member.SourceSyntax, genericSubstitutions: genericSubstitutions, genericParameterNames: GetFunctionGenericParameterNames(getter), callTarget: member);
				AddReceiverConstOfAnchorFact(targetType, getter, constOfAnchors, member.Target?.SourceSyntax);
				member.ResolvedType = SubstituteGenericType(getter.ResolvedType ?? ErrorType, genericSubstitutions);
				member.ResolvedType = SubstituteConstOfResolvedType(getter.ReturnType, member.ResolvedType, constOfAnchors, genericSubstitutions);
				expressionRewrites[member] = CreateMemberReference(member, member.Target, member.ResolvedType, getter);
				propertyType = member.ResolvedType;
				return true;
			}
		}

		if (getterReceiverMismatch)
		{
			Report(GetRange(member.SourceSyntax), PropertyReceiverIncompatibilityMessage(member.Name, targetType, "getter"));
			foreach (ArgumentExpression argument in arguments)
				BodyAnalyzeArgumentExpression(argument, scope, typeScope);
			return true;
		}

		List<FunctionDefinition> setters = type is null ? [] : LookupPropertySetters(type, member.Name, member.SourceSyntax);
		setters.AddRange(LookupExtensionFunctions(targetType, "set" + member.Name, member.SourceSyntax));
		if (setters.Count > 0)
		{
			Report(GetRange(member.SourceSyntax), $"Property '{member.Name}' is not readable on type '{targetType}'.");
			foreach (ArgumentExpression argument in arguments)
				BodyAnalyzeArgumentExpression(argument, scope, typeScope);
			return true;
		}

		return false;
	}

	bool TryAnalyzePropertySetter(MemberExpression member, List<ArgumentExpression> arguments, Expression? value, BodyScope scope, AnalysisScope typeScope, out string propertyType)
	{
		propertyType = ErrorType;
		string targetType = BodyAnalyzeExpression(member.Target, scope, typeScope);
		TypeDefinition? type = GetTypeDefinition(targetType);

		List<FunctionDefinition> setters = type is null ? [] : LookupPropertySetters(type, member.Name, member.SourceSyntax);
		setters.AddRange(LookupExtensionFunctions(targetType, "set" + member.Name, member.SourceSyntax));
		List<ArgumentExpression> logicalArguments = [.. arguments];
		if (value is not null)
			logicalArguments.Add(new ArgumentExpression { SourceSyntax = value.SourceSyntax, Value = value });
		if (setters.Count > 1 && TrySelectOverload("set" + member.Name, setters, logicalArguments, scope, typeScope, member.SourceSyntax) is FunctionDefinition selectedSetter)
			setters = [selectedSetter];
		bool setterReceiverMismatch = false;
		foreach (FunctionDefinition setter in setters)
		{
			if (!ReceiverCanCallFunction(targetType, setter, isPropertyGetterSyntax: false))
			{
				setterReceiverMismatch = true;
				continue;
			}

			List<ParameterDefinition> callableParameters = GetCallableParameters(setter.Parameters);
			if (callableParameters.Count == 0)
				continue;

			int valueParameterIndex = GetPropertySetterValueParameterStart(callableParameters);
			int setterArgumentCount = valueParameterIndex;
			if (CountRequiredParametersForPropertySetter(callableParameters, valueParameterIndex) > arguments.Count)
				continue;
			if (setterArgumentCount != arguments.Count)
				continue;

			Dictionary<string, string> genericSubstitutions = [];
			AddReceiverTypeGenericSubstitutions(targetType, setter, genericSubstitutions);
			HashSet<string> genericParameterNames = GetFunctionGenericParameterNames(setter);
			for (int i = 0; i < arguments.Count; i++)
			{
				string expected = SubstituteGenericType(callableParameters[i].ResolvedType ?? ErrorType, genericSubstitutions);
				string actual = BodyAnalyzeArgumentExpression(arguments[i], scope, typeScope, expected);
				InferGenericSubstitutions(callableParameters[i].ResolvedType ?? ErrorType, actual, genericSubstitutions, genericParameterNames);
				expected = SubstituteGenericType(callableParameters[i].ResolvedType ?? ErrorType, genericSubstitutions);
				CheckAssignable(expected, actual, arguments[i].SourceSyntax, "Argument");
			}

			string expectedValueType = SubstituteGenericType(callableParameters[valueParameterIndex].ResolvedType ?? ErrorType, genericSubstitutions);
			string actualValueType = BodyAnalyzeExpression(value, scope, typeScope, expectedValueType);
			InferGenericSubstitutions(callableParameters[valueParameterIndex].ResolvedType ?? ErrorType, actualValueType, genericSubstitutions, genericParameterNames);
			expectedValueType = SubstituteGenericType(callableParameters[valueParameterIndex].ResolvedType ?? ErrorType, genericSubstitutions);
			CheckAssignable(expectedValueType, actualValueType, value?.SourceSyntax, "Assignment");

			member.ResolvedType = expectedValueType;
			expressionRewrites[member] = CreateMemberReference(member, member.Target, expectedValueType, setter);
			propertyType = expectedValueType;
			return true;
		}

		if (setterReceiverMismatch)
		{
			Report(GetRange(member.SourceSyntax), PropertyReceiverIncompatibilityMessage(member.Name, targetType, "setter"));
			if (value is not null)
				BodyAnalyzeExpression(value, scope, typeScope);
			foreach (ArgumentExpression argument in arguments)
				BodyAnalyzeArgumentExpression(argument, scope, typeScope);
			return true;
		}

		List<FunctionDefinition> getters = type is null ? [] : LookupPropertyGetters(type, member.Name, member.SourceSyntax);
		getters.AddRange(LookupExtensionFunctions(targetType, "get" + member.Name, member.SourceSyntax));
		if (setters.Count == 0 && getters.Count == 0)
			return false;

		Report(GetRange(member.SourceSyntax), $"Property '{member.Name}' is not writable on type '{targetType}'.");
		if (value is not null)
			BodyAnalyzeExpression(value, scope, typeScope);
		foreach (ArgumentExpression argument in arguments)
			BodyAnalyzeArgumentExpression(argument, scope, typeScope);
		return true;
	}

	bool ReceiverCanCallFunction(string targetType, FunctionDefinition function, bool isPropertyGetterSyntax)
	{
		string receiverType = BuildEffectiveReceiverType(targetType, function, isPropertyGetterSyntax);
		string actualType = StripLifetimeQualifiers(StripTopLevelConstForReceiver(targetType));
		receiverType = StripLifetimeQualifiers(receiverType);
		if (WouldDecayFixedArrayStorageToPointerReceiver(actualType, receiverType))
			return false;
		if (CanImplicitlyConvert(actualType, receiverType))
			return true;
		if (CanGenericCallableReceiverMatch(actualType, receiverType))
			return true;
		if (CanGenericReceiverMatch(actualType, receiverType))
			return true;
		return TryGetPointerElementType(receiverType) is string receiverElement
			&& TryGetPointerElementType(actualType) is null
			&& BaseTypeName(receiverElement) == BaseTypeName(actualType);
	}

	static bool WouldDecayFixedArrayStorageToPointerReceiver(string actualType, string receiverType)
	{
		return TryGetFixedArrayShape(actualType, out _, out _)
			&& TryGetPointerElementType(receiverType) is not null;
	}

	bool CanGenericCallableReceiverMatch(string actualType, string receiverType)
	{
		if (!TryGetCallableShape(actualType, out CallableShape actual) || !TryGetCallableShape(receiverType, out CallableShape receiver))
			return false;

		actual = ExpandCallableShape(actual);
		receiver = ExpandCallableShape(receiver);
		if (actual.Kind != receiver.Kind
			|| actual.Spec != receiver.Spec
			|| actual.CallSpec != receiver.CallSpec
			|| actual.Parameters.Count != receiver.Parameters.Count
			|| actual.This != receiver.This)
			return false;
		if (!CanGenericCallableSlotMatch(actual.ReturnType, receiver.ReturnType))
			return false;

		for (int i = 0; i < actual.Parameters.Count; i++)
			if (!CanGenericCallableSlotMatch(actual.Parameters[i], receiver.Parameters[i]))
				return false;

		return true;
	}

	bool CanGenericCallableSlotMatch(string actualType, string receiverType)
	{
		return actualType == receiverType
			|| TryParseTypeShape(actualType, out TypeShape actualShape)
				&& TryParseTypeShape(receiverType, out TypeShape receiverShape)
				&& CanGenericReceiverMatch(actualShape, receiverShape, protectedByConstTarget: false, pointerDepth: 0);
	}

	bool CanGenericReceiverMatch(string actualType, string receiverType)
	{
		if (!TryParseTypeShape(actualType, out TypeShape actualShape) || !TryParseTypeShape(receiverType, out TypeShape receiverShape))
			return false;

		return CanGenericReceiverMatch(actualShape, receiverShape, protectedByConstTarget: false, pointerDepth: 0);
	}

	bool CanGenericReceiverMatch(TypeShape actual, TypeShape receiver, bool protectedByConstTarget, int pointerDepth)
	{
		if (!TargetSpecsCanImplicitlyConvert(actual.TargetSpec, receiver.TargetSpec))
			return false;
		if (!QualifiersCanConvert(actual.Qualifiers, receiver.Qualifiers, protectedByConstTarget, pointerDepth))
			return false;

		if (receiver.Kind == TypeShapeKind.Named && IsGenericPlaceholderParameter(receiver.Name))
			return actual.Kind == TypeShapeKind.Named;

		if (actual.Kind != receiver.Kind)
			return false;
		if (actual.Kind == TypeShapeKind.Named)
			return actual.Name == receiver.Name || CanGenericNamedReceiverMatch(actual.Name, receiver.Name);
		if (actual.Element is null || receiver.Element is null)
			return false;

		bool childProtected = protectedByConstTarget || receiver.Qualifiers.IsConst;
		int childPointerDepth = actual.Kind == TypeShapeKind.Pointer ? pointerDepth + 1 : pointerDepth;
		return CanGenericReceiverMatch(actual.Element, receiver.Element, childProtected, childPointerDepth);
	}

	bool CanGenericNamedReceiverMatch(string actualName, string receiverName)
	{
		if (BaseTypeName(actualName) != BaseTypeName(receiverName))
			return false;

		List<string> actualArguments = ExtractConstructedTypeArguments(actualName);
		List<string> receiverArguments = ExtractConstructedTypeArguments(receiverName);
		if (actualArguments.Count == 0 || actualArguments.Count != receiverArguments.Count)
			return false;

		for (int i = 0; i < actualArguments.Count; i++)
		{
			if (!TryParseTypeShape(actualArguments[i], out TypeShape actualArgument)
				|| !TryParseTypeShape(receiverArguments[i], out TypeShape receiverArgument)
				|| !CanGenericReceiverMatch(actualArgument, receiverArgument, protectedByConstTarget: false, pointerDepth: 0))
				return false;
		}

		return true;
	}

	string BuildEffectiveReceiverType(string targetType, FunctionDefinition function, bool isPropertyGetterSyntax)
	{
		ThisParameterDefinition? explicitThis = GetExplicitThisParameter(function) ?? function.EffectiveThisParameter;
		TypeDefinition? owner = FindContainingType(function);
		string receiverType = owner is null
			? explicitThis?.ResolvedType ?? targetType
			: BuildOwnedReceiverType(targetType, owner);

		if (explicitThis is not null)
			return ApplyThisDeclarators(receiverType, explicitThis);

		return isPropertyGetterSyntax ? AddConstToReceiverInstance(receiverType) : receiverType;
	}

	static string BuildOwnedReceiverType(string targetType, TypeDefinition owner)
	{
		if (TryGetPointerElementType(targetType) is string elementType && BaseTypeName(elementType) == owner.Name)
			return $"{StripTopLevelValueQualifiers(elementType)}*";
		if (BaseTypeName(targetType) == owner.Name)
			return $"{StripTopLevelValueQualifiers(targetType)}*";
		return TryGetPointerElementType(targetType) is not null ? $"{owner.Name}*" : owner.Name;
	}

	static bool IsPropertyGetterFunction(FunctionDefinition function)
	{
		return function.Name.StartsWith("get", StringComparison.Ordinal)
			&& function.Name.Length > "get".Length
			&& function.ResolvedType != "void";
	}

	static string ReceiverIncompatibilityMessage(string subject, string name, string targetType)
	{
		string message = $"{subject} '{name}' exists on type '{targetType}', but its this parameter is not compatible with that receiver.";
		if (IsPropertyGetterName(name))
			message += " Getter-accessor-compatible methods use 'const this' by default when they omit an explicit receiver; declare an explicit 'this' parameter to override that default.";
		return message;
	}

	static string PropertyReceiverIncompatibilityMessage(string name, string targetType, string accessorKind)
	{
		string message = $"Property '{name}' exists on type '{targetType}', but its {accessorKind}'s this parameter is not compatible with that receiver.";
		if (accessorKind == "getter")
			message += " Getter-accessor-compatible methods use 'const this' by default when they omit an explicit receiver; declare an explicit 'this' parameter to override that default.";
		return message;
	}

	static bool IsPropertyGetterName(string name)
	{
		return name.StartsWith("get", StringComparison.Ordinal)
			&& name.Length > "get".Length;
	}

	static ThisParameterDefinition? GetExplicitThisParameter(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter is ThisParameterDefinition thisParameter)
				return thisParameter;
		}

		return null;
	}

	static ThisParameterDefinition? GetEffectiveThisParameter(FunctionDefinition function)
	{
		return GetExplicitThisParameter(function) ?? function.EffectiveThisParameter;
	}

	static string ApplyThisDeclarators(string receiverType, ThisParameterDefinition thisParameter)
	{
		if (HasInternalThisDeclarator(thisParameter, "const"))
			receiverType = AddConstToReceiverInstance(receiverType);

		if (thisParameter.SourceSyntax is not ThisParameterSyntax { Declarators: not null } syntax)
			return receiverType;

		string result = receiverType;
		foreach (TypeDeclaratorSyntax declarator in syntax.Declarators)
		{
			result = declarator.Keyword?.Value switch
			{
				"const" => AddConstToReceiverInstance(result),
				"volatile" => AddTopLevelVolatileToReceiverInstance(result),
				"escaped" => AddTopLevelLifetimeToReceiver(result, "escaped"),
				"scoped" => AddTopLevelLifetimeToReceiver(result, "scoped"),
				"unscoped" => AddTopLevelLifetimeToReceiver(result, "unscoped"),
				_ => result
			};
		}

		return result;
	}

	FunctionDefinition? LookupConstructor(string targetType, int argumentCount)
	{
		if (!typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? type))
			return null;

		IEnumerable<FunctionDefinition> functions = type switch
		{
			ClassDefinition classDefinition => classDefinition.Functions,
			StructDefinition structDefinition => structDefinition.Functions,
			_ => []
		};

		foreach (FunctionDefinition function in functions)
		{
			if (function.Modifier == FunctionModifier.Constructor && CanCallWithArgumentCount(function.Parameters, argumentCount))
				return function;
		}

		return null;
	}

	FunctionDefinition? LookupCreateMethod(string targetType, int argumentCount)
	{
		if (!typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? type))
			return null;

		foreach (FunctionDefinition function in GetTypeFunctions(type))
		{
			if (function.Name == CreateMethodName && CanCallWithArgumentCount(function.Parameters, argumentCount))
				return function;
		}

		return null;
	}

	void ValidateExternClassDelete(Expression? expression, string targetType)
	{
		string deletedType = TryGetPointerElementType(targetType) ?? targetType;
		if (!typeDefinitions.TryGetValue(BaseTypeName(deletedType), out TypeDefinition? definition)
			|| definition is not ClassDefinition { Extern: not null } externClass)
		{
			return;
		}

		foreach (FunctionDefinition function in GetTypeFunctions(externClass))
		{
			if (function.Name == DeleteMethodName || IsDestructorFunction(function))
				return;
		}

		Report(GetRange(expression?.SourceSyntax), $"delete requires an explicit destructor for extern class '{externClass.Name}'.");
	}

	static FunctionDefinition? FindGeneratedInitNewMethod(TypeDefinition? type)
	{
		if (type is null)
			return null;

		foreach (FunctionDefinition function in GetTypeFunctions(type))
		{
			if (function.Name == InitNewMethodName)
				return function;
		}
		return null;
	}

	FunctionDefinition? FindVirtualImplementationByName(ClassDefinition owner, string name)
	{
		string slotName = name == DeleteMethodName ? DeleteMethodName : name;
		foreach (ClassDefinition candidate in EnumerateClassAndBases(owner))
		{
			foreach (FunctionDefinition function in candidate.Functions)
			{
				if (!virtualImplementations.TryGetValue(function, out FunctionDefinition? implementation))
					continue;
				if (VirtualSlotName(function) == slotName)
					return implementation;
			}
		}
		return null;
	}

	ClassDefinition? GetDirectBaseClass(TypeDefinition definition)
	{
		foreach (TypeDefinition baseType in GetDirectBaseClasses(definition))
		{
			if (baseType is ClassDefinition baseClass)
				return baseClass;
		}

		return null;
	}

	bool HasAccessibleParameterlessConstructor(ClassDefinition definition)
	{
		return TryGetAccessibleParameterlessConstructor(definition, out _);
	}

	bool TryGetAccessibleParameterlessConstructor(ClassDefinition definition, out FunctionDefinition? constructor)
	{
		constructor = null;
		bool hasConstructor = false;
		foreach (FunctionDefinition function in definition.Functions)
		{
			if (function.Modifier != FunctionModifier.Constructor)
				continue;

			hasConstructor = true;
			if (CountRequiredParameters(function.Parameters) == 0)
			{
				constructor = function;
				return true;
			}
		}

		if (!hasConstructor)
		{
			constructor = null;
			return true;
		}

		return false;
	}

	TypeDefinition? FindContainingType(FunctionDefinition function)
	{
		foreach (TypeDefinition type in typeDefinitions.Values)
		{
			foreach (FunctionDefinition candidate in GetTypeFunctions(type))
			{
				if (ReferenceEquals(candidate, function))
					return type;
			}
		}

		return null;
	}

	static IEnumerable<FunctionDefinition> GetTypeFunctions(TypeDefinition type)
	{
		return type switch
		{
			ClassDefinition classDefinition => classDefinition.Functions,
			StructDefinition structDefinition => structDefinition.Functions,
			InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
			EnumDefinition enumDefinition => enumDefinition.Functions,
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions,
			ParamsDefinition paramsDefinition => paramsDefinition.Functions,
			_ => []
		};
	}

	static int CountRequiredParameters(List<ParameterDefinition> parameters, bool includeExplicitThis = false)
	{
		int count = 0;
		int expandedThisCount = includeExplicitThis ? 0 : CountExpandedThisParameters(parameters);
		int index = 0;
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter.DefaultValue is null
				&& parameter.Modifier is not ParameterModifier.Thrown and not ParameterModifier.Within
				&& (includeExplicitThis || parameter is not ThisParameterDefinition)
				&& index >= expandedThisCount
				&& parameter is not WithinParameterDefinition and not SizeOfParameterDefinition and not NameOfParameterDefinition and not VTableOfParameterDefinition)
				count++;
			index++;
		}
		return count;
	}

	static int CountCallableParameters(List<ParameterDefinition> parameters, bool includeExplicitThis = false)
	{
		int count = 0;
		int expandedThisCount = includeExplicitThis ? 0 : CountExpandedThisParameters(parameters);
		int index = 0;
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter.Modifier is not ParameterModifier.Thrown and not ParameterModifier.Within
				&& (includeExplicitThis || parameter is not ThisParameterDefinition)
				&& index >= expandedThisCount
				&& parameter is not WithinParameterDefinition)
				count++;
			index++;
		}
		return count;
	}

	static bool CanCallWithArgumentCount(List<ParameterDefinition> parameters, int argumentCount, bool includeExplicitThis = false)
	{
		return CountRequiredParameters(parameters, includeExplicitThis) <= argumentCount && argumentCount <= CountCallableParameters(parameters, includeExplicitThis);
	}

	static List<ParameterDefinition> GetCallableParameters(List<ParameterDefinition> parameters, bool includeExplicitThis = false)
	{
		List<ParameterDefinition> callable = [];
		int expandedThisCount = includeExplicitThis ? 0 : CountExpandedThisParameters(parameters);
		int index = 0;
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter.Modifier is ParameterModifier.Thrown or ParameterModifier.Within
				|| !includeExplicitThis && parameter is ThisParameterDefinition
				|| index < expandedThisCount
				|| parameter is WithinParameterDefinition)
			{
				index++;
				continue;
			}

			callable.Add(parameter);
			index++;
		}
		return callable;
	}

	static bool HasExpandedThisParameters(List<ParameterDefinition> parameters)
	{
		return CountExpandedThisParameters(parameters) > 0;
	}

	static int CountExpandedThisParameters(List<ParameterDefinition> parameters)
	{
		if (parameters.Count < 2 || parameters[0] is ThisParameterDefinition || parameters[0].Name != "this")
			return 0;
		if (!parameters[1].Name.StartsWith("this_", StringComparison.Ordinal))
			return 0;

		int count = 1;
		while (count < parameters.Count && parameters[count].Name.StartsWith("this_", StringComparison.Ordinal))
			count++;
		return count;
	}

	static int GetPropertySetterValueParameterStart(List<ParameterDefinition> parameters)
	{
		if (parameters.Count >= 2)
		{
			string previousName = parameters[^2].Name;
			string lastName = parameters[^1].Name;
			if (lastName == previousName + "_context"
				|| lastName == previousName + "_length"
				|| lastName == previousName + "_specified")
				return parameters.Count - 2;
		}
		return parameters.Count - 1;
	}

	static int CountRequiredParametersForPropertySetter(List<ParameterDefinition> parameters, int valueParameterIndex)
	{
		if (parameters.Count == 0)
			return 0;

		int count = 0;
		for (int i = 0; i < valueParameterIndex; i++)
		{
			ParameterDefinition parameter = parameters[i];
			if (parameter.DefaultValue is null
				&& parameter.Modifier is not ParameterModifier.Out and not ParameterModifier.Thrown
				&& parameter is not ThisParameterDefinition and not WithinParameterDefinition and not SizeOfParameterDefinition and not NameOfParameterDefinition and not VTableOfParameterDefinition)
				count++;
		}

		return count;
	}

	List<FunctionDefinition> LookupPropertyGetters(TypeDefinition type, string name, SyntaxNode? referenceSyntax)
	{
		return LookupTypeFunctions(type, "get" + name, referenceSyntax);
	}

	List<FunctionDefinition> LookupPropertySetters(TypeDefinition type, string name, SyntaxNode? referenceSyntax)
	{
		return LookupTypeFunctions(type, "set" + name, referenceSyntax);
	}

	bool HasPropertyGetterWithIncompatibleReceiver(TypeDefinition type, string targetType, string name, SyntaxNode? referenceSyntax)
	{
		foreach (FunctionDefinition getter in LookupPropertyGetters(type, name, referenceSyntax))
		{
			if (!ReceiverCanCallFunction(targetType, getter, isPropertyGetterSyntax: true))
				return true;
		}

		return false;
	}

	bool HasMemberFunctionWithIncompatibleReceiver(TypeDefinition type, string targetType, string name, SyntaxNode? referenceSyntax)
	{
		foreach (FunctionDefinition function in LookupTypeFunctions(type, name, referenceSyntax))
		{
			if (IsMemberVisible(function, type, referenceSyntax) && !ReceiverCanCallFunction(targetType, function, IsPropertyGetterFunction(function)))
				return true;
		}

		return false;
	}

	TypeDefinition? GetTypeDefinition(string typeName)
	{
		return typeDefinitions.TryGetValue(BaseTypeName(typeName), out TypeDefinition? type) ? type : null;
	}

	string ReportMultipleCandidates(SyntaxNode? syntax, string name)
	{
		Report(GetRange(syntax), $"Multiple member candidates found for '{name}'.");
		return ErrorType;
	}

	string ReportOverloadFamilyAsValue(SyntaxNode? syntax, string name)
	{
		Report(GetRange(syntax), $"`{name}` names an overload family, not a callable value. Select a concrete full callable name or write an explicit wrapper.");
		return ErrorType;
	}

	string ReportType(SyntaxNode? syntax, string message)
	{
		Report(GetRange(syntax), message);
		return ErrorType;
	}

	static string? TryGetArrayElementType(string? type)
	{
		return new TypeShapeParser(type ?? "").TryParse(out TypeShape shape) && shape.Kind is TypeShapeKind.Array or TypeShapeKind.FixedArray
			? TypeShapeParser.Format(shape.Element)
			: null;
	}

	static string? TryGetPointerElementType(string? type)
	{
		return new TypeShapeParser(StripLifetimeQualifiers(type ?? "")).TryParse(out TypeShape shape) && shape.Kind == TypeShapeKind.Pointer
			? TypeShapeParser.Format(shape.Element)
			: null;
	}

	string? GetIteratorElementType(TypeReference? type)
	{
		if (type is IterTypeReference { ElementType: not null } iter)
			return iter.ElementType.ResolvedType;
		if (type is IterTypeReference { Parameters.Count: > 0 } parameterIter)
		{
			foreach (ParameterDefinition parameter in parameterIter.Parameters)
				if (parameter.Modifier != ParameterModifier.Thrown)
					return parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? FormatTypeReference(parameter.Type);
		}

		string? resolved = type?.ResolvedType;
		if (resolved is null)
			return null;
		if (resolved.StartsWith("iter ", StringComparison.Ordinal))
			return resolved["iter ".Length..];
		if (resolved.StartsWith("async iter ", StringComparison.Ordinal))
			return resolved["async iter ".Length..];
		if (resolved.StartsWith("iter(", StringComparison.Ordinal))
			return ExtractFirstIteratorSlotType(resolved, "iter(");
		if (resolved.StartsWith("async iter(", StringComparison.Ordinal))
			return ExtractFirstIteratorSlotType(resolved, "async iter(");
		return null;
	}

	string? GetIteratorThrownType(TypeReference? type)
	{
		if (type is IterTypeReference iter)
		{
			foreach (ParameterDefinition parameter in iter.Parameters)
				if (parameter.Modifier == ParameterModifier.Thrown)
					return parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? FormatTypeReference(parameter.Type);
			return null;
		}

		string? resolved = type?.ResolvedType;
		if (resolved is null)
			return null;
		if (TryGetIteratorProtocolSlots(resolved, out _, out string? thrownType))
			return thrownType;
		return null;
	}

	static string? ExtractFirstIteratorSlotType(string resolved, string prefix)
	{
		string slots = resolved[prefix.Length..];
		if (slots.EndsWith(")", StringComparison.Ordinal))
			slots = slots[..^1];
		int comma = slots.IndexOf(',', StringComparison.Ordinal);
		string first = comma < 0 ? slots : slots[..comma];
		return first.Trim();
	}

	string BestType(List<string> types)
	{
		if (types.Count == 0)
			return ErrorType;

		for (int i = 0; i < types.Count; i++)
			types[i] = EraseConstOfQualifiers(types[i]);
		string best = types[0];
		foreach (string type in types)
		{
			if (CanImplicitlyConvert(best, type))
				best = type;
			else if (!CanImplicitlyConvert(type, best))
				return ErrorType;
		}

		return best;
	}

	static string GetNumberLiteralType(string text, string? targetType)
	{
		targetType = targetType is null ? null : StripTopLevelValueQualifiers(targetType);
		if (targetType is not null && IsNumericTypeName(targetType))
			return targetType;

		return text.Contains('.', StringComparison.Ordinal) ? "double" : "int";
	}

	static string PromoteInteger(string type)
	{
		type = StripTopLevelValueQualifiers(type);
		return type is "byte" or "sbyte" or "ushort" or "short" or "char" or "wchar" or "achar" or "uchar"
			? "int"
			: type;
	}

	static string UsualArithmeticConversion(string left, string right)
	{
		left = PromoteInteger(left);
		right = PromoteInteger(right);
		return NumericRank(left) >= NumericRank(right) ? left : right;
	}

	static bool IsNumericTypeName(string type)
	{
		type = StripTopLevelValueQualifiers(type);
		if (new TypeShapeParser(type).TryParse(out TypeShape shape) && shape.Kind == TypeShapeKind.Named)
			type = shape.Name;
		return IsIntegralTypeName(type) || type is "float" or "double";
	}

	bool IsNumericType(string type)
	{
		return IsNumericTypeName(type) || TryGetUnderlyingNumericType(type, out _);
	}

	bool IsIntegralType(string type)
	{
		return IsIntegralTypeName(type) || IsEnumType(type) || TryGetUnderlyingNumericType(type, out string? underlying) && underlying is not null && IsIntegralTypeName(underlying);
	}

	static bool IsIntegralTypeName(string type)
	{
		type = StripTopLevelValueQualifiers(type);
		if (new TypeShapeParser(type).TryParse(out TypeShape shape) && shape.Kind == TypeShapeKind.Named)
			type = shape.Name;
		return type is "byte" or "sbyte" or "ushort" or "short" or "uint" or "int" or "ulong" or "long" or "nuint" or "nint" or "char" or "wchar" or "achar" or "uchar";
	}

	static int NumericRank(string type)
	{
		type = StripTopLevelValueQualifiers(type);
		if (new TypeShapeParser(type).TryParse(out TypeShape shape) && shape.Kind == TypeShapeKind.Named)
			type = shape.Name;
		return type switch
		{
			"byte" or "sbyte" => 1,
			"ushort" or "short" or "char" or "wchar" or "achar" or "uchar" => 2,
			"uint" or "int" => 3,
			"nuint" or "nint" => 4,
			"ulong" or "long" => 5,
			"float" => 6,
			"double" => 7,
			_ => 100
		};
	}

	bool IsEnumType(string type)
	{
		return typeDefinitions.TryGetValue(BaseTypeName(type), out TypeDefinition? definition) && definition is EnumDefinition;
	}

	bool TryGetUnderlyingNumericType(string type, out string? underlying)
	{
		underlying = null;
		if (!typeDefinitions.TryGetValue(BaseTypeName(type), out TypeDefinition? definition))
			return false;

		TypeReference? underlyingType = definition switch
		{
			EnumDefinition enumDefinition => enumDefinition.UnderlyingType ?? new PrimitiveTypeReference { Type = PrimitiveType.Int },
			NewtypeDefinition newtypeDefinition => newtypeDefinition.UnderlyingType,
			_ => null
		};

		underlying = underlyingType?.ResolvedType;
		if (underlying is null && underlyingType is PrimitiveTypeReference primitive)
			underlying = GetPrimitiveTypeName(primitive.Type);
		return underlying is not null && IsNumericTypeName(underlying);
	}

	bool TryGetNewtypeUnderlyingType(string type, out string? underlying)
	{
		underlying = null;
		if (!typeDefinitions.TryGetValue(BaseTypeName(type), out TypeDefinition? definition) || definition is not NewtypeDefinition newtypeDefinition)
			return false;

		underlying = newtypeDefinition.UnderlyingType?.ResolvedType;
		if (underlying is null && newtypeDefinition.UnderlyingType is PrimitiveTypeReference primitive)
			underlying = GetPrimitiveTypeName(primitive.Type);

		return underlying is not null;
	}

	static bool IsConstantVariable(VariableDefinition variable)
	{
		return variable.IsInline || IsConstType(variable.Type) || IsConstQualified(variable.Type?.ResolvedType);
	}

	static bool IsConstantField(FieldDefinition field)
	{
		return field.IsInline || IsConstType(field.Type) || IsConstQualified(field.Type?.ResolvedType);
	}

	static bool IsConstType(TypeReference? type)
	{
		return type switch
		{
			ConstTypeReference => true,
			ConstOfTypeReference => true,
			AttributedTypeReference attributed => IsConstType(attributed.Type),
			_ => false
		};
	}

	static bool IsConstCharPointerType(string? type)
	{
		return type == "const char*";
	}

	bool CanConvertPrimitiveStringToPointer(string source, string target)
	{
		string sourceElement = StripTopLevelValueQualifiers(source) switch
		{
			"string" => "char",
			"wstring" => "wchar",
			"astring" => "achar",
			_ => ""
		};
		if (sourceElement.Length == 0)
			return false;
		if (!TryParseTypeShape(target, out TypeShape targetShape) || targetShape.Kind != TypeShapeKind.Pointer || targetShape.Element is null)
			return false;
		string targetElement = TypeShapeParser.Format(targetShape.Element);
		string unqualifiedTargetElement = StripTopLevelValueQualifiers(targetElement);
		if (unqualifiedTargetElement != sourceElement)
			return false;
		return IsConstQualified(targetElement);
	}

	bool CanConvertPrimitiveStringToConstArray(string source, string target)
	{
		string sourceElement = GetPrimitiveStringElementType(source) ?? "";
		if (sourceElement.Length == 0)
			return false;
		if (TryGetArrayElementType(target) is not string targetElement)
			return false;
		string unqualifiedTargetElement = StripTopLevelValueQualifiers(targetElement);
		if (unqualifiedTargetElement != sourceElement)
			return false;
		return IsConstQualified(targetElement);
	}

	string GetStringLiteralType(LiteralExpression literal, string? targetType)
	{
		if (targetType is null || targetType == TargetType || targetType == AutoType)
			return "string";

		if (IsStringLiteralTargetType(targetType))
			return targetType;

		if (IsFixedCharacterArrayType(targetType))
		{
			ValidateFixedCharacterArrayStringLiteral(literal, targetType);
			return targetType;
		}

		Report(GetRange(literal.SourceSyntax), $"String literal cannot implicitly convert to mutable type '{targetType}'.");
		return ErrorType;
	}

	static bool IsFixedCharacterArrayType(string? type)
	{
		return TryGetFixedArrayShape(type, out string elementType, out _)
			&& StripTopLevelValueQualifiers(elementType) is "char" or "achar" or "wchar";
	}

	void ValidateFixedCharacterArrayStringLiteral(LiteralExpression literal, string targetType)
	{
		if (!TryGetFixedArrayShape(targetType, out _, out long length))
			return;

		string value = literal.Value as string ?? "";
		if (value.Length > length)
			Report(GetRange(literal.SourceSyntax), $"String literal contains too many code units for {targetType}.");
	}

	static bool IsStringLiteralTargetType(string? type)
	{
		type = StripLifetimeQualifiers(type ?? "");
		return type is "string"
			or "wstring"
			or "astring"
			or "const string"
			or "const wstring"
			or "const astring"
			or "const char*"
			or "const wchar*"
			or "const achar*"
			or "const char[]"
			or "const wchar[]"
			or "const achar[]";
	}

	static bool IsPrimitiveStringType(string? type)
	{
		type = StripTopLevelValueQualifiers(type ?? "");
		return type is "string" or "wstring" or "astring";
	}

	static string? GetPrimitiveStringElementType(string? type)
	{
		type = StripTopLevelValueQualifiers(type ?? "");
		return type switch
		{
			"string" => "char",
			"wstring" => "wchar",
			"astring" => "achar",
			_ => null
		};
	}

	string BuildFunctionValueType(FunctionDefinition function, bool isInstance, bool allowCallableAscription = false)
	{
		if (allowCallableAscription && TryGetCallableAscriptionReferenceType(function, isInstance, out string ascribedType))
			return ascribedType;

		string kind = isInstance ? "delegate" : "fn";
		List<string> parameters = [];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (isInstance && parameter is ThisParameterDefinition)
				continue;

			string parameterType = parameter.ResolvedType ?? ErrorType;
			parameters.Add(parameter.Modifier switch
			{
				ParameterModifier.In => "in " + parameterType,
				ParameterModifier.Out => "out " + parameterType,
				ParameterModifier.Thrown => "thrown " + parameterType,
				ParameterModifier.Within => "within " + parameterType,
				ParameterModifier.Upon => "upon " + parameterType,
				_ => parameterType
			});
		}

		return $"{kind}{FormatCallSpec(function.CallSpec)} {function.ResolvedType ?? ErrorType}({string.Join(", ", parameters)})";
	}

	bool TryGetCallableAscriptionReferenceType(FunctionDefinition function, bool isInstanceReference, out string ascribedType)
	{
		ascribedType = "";
		if (function.CallableAscriptionNewtype is not NewtypeDefinition newtypeDefinition)
			return false;

		string family = GetCallableNewtypeFamily(newtypeDefinition);
		bool receiverBearing = IsReceiverBearingDeclaration(function);
		if (!isInstanceReference && !receiverBearing && family == "fn")
		{
			ascribedType = newtypeDefinition.Name;
			return true;
		}
		if (isInstanceReference && receiverBearing && family is "delegate" or "iter")
		{
			ascribedType = newtypeDefinition.Name;
			return true;
		}
		return false;
	}

	MemberReferenceExpression CreateMemberReference(MemberExpression member, Expression? target, string type, BindableNode node)
	{
		MemberReferenceExpression reference = new()
		{
			SourceSyntax = member.SourceSyntax,
			Target = target,
			Name = member.Name,
			Member = node,
			ResolvedType = type
		};
		if (node is FunctionDefinition function)
			reference.Candidates.Add(function);
		return reference;
	}
}
