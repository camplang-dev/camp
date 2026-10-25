using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	sealed record LifetimeFact(string Kind, IReadOnlyList<string> Anchors, string Source);
	sealed record LifetimeCallContext(Dictionary<string, LifetimeFact> Anchors, List<LifetimeFact> ScopedInputs, LifetimeFact? Receiver, Dictionary<string, List<LifetimeFact>> GenericInputs,
		Dictionary<string, LifetimeFact> RetainedAnchors, List<LifetimeFact> RetainedInputs);

	static string MakeLifetimeFact(string kind, string? anchor, string source)
	{
		return new BoundLifetime(kind, string.IsNullOrWhiteSpace(anchor) ? [] : [anchor], source).ToString();
	}

	static string FormatLifetimeFact(LifetimeFact fact)
	{
		return new BoundLifetime(fact.Kind, fact.Anchors, fact.Source).ToString();
	}

	static bool TryParseLifetimeFact(string? text, out LifetimeFact fact)
	{
		fact = new LifetimeFact("", [], "");
		if (string.IsNullOrWhiteSpace(text))
			return false;

		string source = "";
		string body = text;
		int sourceIndex = text.IndexOf(':');
		if (sourceIndex >= 0)
		{
			body = text[..sourceIndex];
			source = text[(sourceIndex + 1)..];
		}

		string kind = body;
		List<string> anchors = [];
		int anchorStart = body.IndexOf('(');
		if (anchorStart >= 0 && body.EndsWith(')'))
		{
			kind = body[..anchorStart];
			string anchorText = body[(anchorStart + 1)..^1];
			anchors.AddRange(anchorText.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries));
		}

		if (string.IsNullOrWhiteSpace(kind))
			return false;

		fact = new LifetimeFact(kind, anchors, source);
		return true;
	}

	string? CreateDeclarationSlotLifetimeFact(string? name, TypeReference? type, string? resolvedType, bool isFixedStorage, AnalysisScope typeScope)
	{
		if (IsEscapedValueType(resolvedType))
			return MakeLifetimeFact("escaped", null, "escaped type");
		if (!IsLifetimeTrackedType(type, resolvedType, isFixedStorage, typeScope))
			return null;

		return MakeLifetimeFact("scoped", string.IsNullOrWhiteSpace(name) ? null : name, "slot");
	}

	string? CreateDeclarationValueLifetimeFact(TypeReference? type, string? resolvedType, bool isFixedStorage, AnalysisScope typeScope)
	{
		if (IsEscapedValueType(resolvedType))
			return MakeLifetimeFact("escaped", null, "escaped type");
		if (IsEscapedCallableTarget(resolvedType)
			|| TryGetCallableShape(resolvedType, out CallableShape callable) && callable.This.IsEscaped && callable.Kind is "delegate" or "once" or "async")
			return MakeLifetimeFact("escaped", null, "callable context contract");
		if (IsEscapedFunctionPointerType(type, resolvedType, isFixedStorage, typeScope))
			return MakeLifetimeFact("escaped", null, "function pointer");

		return null;
	}

	bool IsEscapedFunctionPointerType(TypeReference? type, string? resolvedType, bool isFixedStorage, AnalysisScope typeScope)
	{
		if (isFixedStorage)
			return false;

		if (IsFunctionPointerTypeReference(type))
			return true;

		return IsFunctionPointerResolvedType(resolvedType);
	}

	bool IsFunctionPointerTypeReference(TypeReference? type)
	{
		if (type is null)
			return false;

		type = UnwrapTypeDeclarators(type);
		if (type is CallableTypeReference { Kind: CallableKind.Function } or RawFunctionPointerTypeReference)
			return true;

		if (type is NamedTypeReference named)
		{
			string name = BaseTypeName(named.ResolvedType ?? named.Name);
			return typeDefinitions.TryGetValue(name, out TypeDefinition? definition)
				&& definition is NewtypeDefinition { UnderlyingType: not null } newtype
				&& IsFunctionPointerTypeReference(newtype.UnderlyingType);
		}

		if (type is TypeDefinitionReference { Definition: NewtypeDefinition { UnderlyingType: not null } newtypeDefinition })
			return IsFunctionPointerTypeReference(newtypeDefinition.UnderlyingType);

		return false;
	}

	bool IsFunctionPointerResolvedType(string? type)
	{
		if (string.IsNullOrWhiteSpace(type))
			return false;

		string stripped = StripTopLevelValueQualifiers(type);
		if (IsRawFunctionPointerType(stripped))
			return true;

		return TryGetCallableShape(stripped, out CallableShape shape) && shape.Kind == "fn";
	}

	string? CreateGlobalSlotLifetimeFact(string? name, TypeReference? type, string? resolvedType, AnalysisScope typeScope)
	{
		if (!IsLifetimeTrackedType(type, resolvedType, isFixedStorage: false, typeScope))
			return null;

		return MakeLifetimeFact("escaped", string.IsNullOrWhiteSpace(name) ? null : name, "slot");
	}

	bool IsLifetimeTrackedType(TypeReference? type, string? resolvedType, bool isFixedStorage, AnalysisScope typeScope)
	{
		if (isFixedStorage || IsDirectFixedArrayType(type))
			return true;

		if (type is not null && IsPointerBearingType(type, typeScope))
			return true;

		if (IsPointerBearingResolvedType(resolvedType))
			return true;

		if (IsEscapedFunctionPointerType(type, resolvedType, isFixedStorage: false, typeScope))
			return true;

		string baseName = BaseTypeName(StripTopLevelValueQualifiers(resolvedType ?? ""));
		return !string.IsNullOrWhiteSpace(baseName)
			&& typeScope.GenericParameters.TryGetValue(baseName, out GenericParameter? parameter)
			&& parameter.IsVoidRepresentation;
	}

	bool IsPointerBearingResolvedType(string? type)
	{
		if (string.IsNullOrWhiteSpace(type) || type == ErrorType || type == TargetType)
			return false;

		string raw = type.Trim();
		if (IsFunctionPointerResolvedType(raw))
			return true;

		if (raw.StartsWith("delegate ", System.StringComparison.Ordinal)
			|| raw.StartsWith("iter ", System.StringComparison.Ordinal)
			|| raw is "string" or "astring" or "wstring")
			return true;

		string normalized = StripTopLevelValueQualifiers(type);
		TypeShapeParser parser = new(normalized);
		if (parser.TryParse(out TypeShape shape) && parser.IsEnd)
			return IsPointerBearingResolvedShape(shape);

		return false;
	}

	bool IsPointerBearingResolvedShape(TypeShape shape)
	{
		return shape.Kind switch
		{
			TypeShapeKind.Pointer => true,
			TypeShapeKind.Array => true,
			TypeShapeKind.FixedArray => shape.Element is not null && IsPointerBearingResolvedShape(shape.Element),
			TypeShapeKind.Optional => shape.Element is not null && IsPointerBearingResolvedShape(shape.Element),
			TypeShapeKind.Named => shape.Name is "string" or "astring" or "wstring"
				|| IsFunctionPointerResolvedType(shape.Name)
				|| shape.Name.StartsWith("delegate ", System.StringComparison.Ordinal)
				|| shape.Name.StartsWith("iter ", System.StringComparison.Ordinal),
			TypeShapeKind.RawFunctionPointer => true,
			_ => false
		};
	}

	bool IsLifetimePointerBearingResolvedType(string? type, BodyScope scope)
	{
		if (IsPointerBearingResolvedType(type))
			return true;
		string baseName = BaseTypeName(StripTopLevelValueQualifiers(type ?? ""));
		if (string.IsNullOrWhiteSpace(baseName))
			return false;
		if (FindBodyGenericParameter(scope, baseName) is not null)
			return true;
		return typeDefinitions.TryGetValue(baseName, out TypeDefinition? definition)
			&& IsPointerBearingTypeDefinition(definition, new AnalysisScope());
	}

	void InitializeParameterLifetimeFacts(ParameterDefinition parameter, AnalysisScope typeScope)
	{
		if (!IsLifetimeTrackedType(parameter.Type, parameter.ResolvedType, isFixedStorage: false, typeScope))
			return;

		string fact = IsEscapedValueType(parameter.ResolvedType) ? MakeLifetimeFact("escaped", null, "escaped type")
			: parameter.LifetimeBinding ?? MakeLifetimeFact("scoped", parameter.Name, "parameter");
		if (!string.IsNullOrWhiteSpace(parameter.Name)
			&& TryParseLifetimeFact(fact, out LifetimeFact parsed)
			&& parsed.Kind == "scoped" && parsed.Anchors.Count == 0)
			fact = FormatLifetimeFact(new LifetimeFact(parsed.Kind, [parameter.Name], parsed.Source));
		parameter.SlotLifetimeFact = fact;
		parameter.ValueLifetimeFact = CreateDeclarationValueLifetimeFact(parameter.Type, parameter.ResolvedType, isFixedStorage: false, typeScope) ?? fact;
	}

	void InitializeVariableLifetimeFacts(VariableDefinition definition, AnalysisScope scope)
	{
		string? fact = CreateGlobalSlotLifetimeFact(definition.Name, definition.Type, definition.ResolvedType, scope);
		definition.SlotLifetimeFact = fact;
		definition.ValueLifetimeFact ??= fact;
	}

	void InitializeFieldLifetimeFacts(FieldDefinition definition, AnalysisScope scope, TypeDefinition? containingType)
	{
		bool escapedInstanceField = definition.Modifier != FieldModifier.Static
			&& IsLifetimeTrackedType(definition.Type, definition.ResolvedType, isFixedStorage: false, scope)
			&& (IsEscapedField(definition) || containingType is ClassDefinition { IsEscaped: true } or ClassDefinition { IsShadow: true });
		string? fact = escapedInstanceField
			? MakeLifetimeFact("escaped", definition.Name, "field")
			: definition.Modifier == FieldModifier.Static
			? CreateGlobalSlotLifetimeFact(definition.Name, definition.Type, definition.ResolvedType, scope)
			: CreateDeclarationSlotLifetimeFact(definition.Name, definition.Type, definition.ResolvedType, isFixedStorage: false, scope);
		definition.SlotLifetimeFact = fact;
		definition.ValueLifetimeFact ??= CreateDeclarationValueLifetimeFact(definition.Type, definition.ResolvedType, isFixedStorage: false, scope) ?? fact;
	}

	static bool IsEscapedField(FieldDefinition definition)
	{
		return definition.LifetimeBinding is not null
			|| definition.Type?.LifetimeBinding?.StartsWith("escaped", StringComparison.Ordinal) == true;
	}

	void InitializeLocalLifetimeFacts(DeclarationStatement declaration, AnalysisScope typeScope)
	{
		string? name = declaration.Target.Names.Count == 1 ? declaration.Target.Names[0] : null;
		string? slotFact = CreateDeclarationSlotLifetimeFact(name, declaration.Target.Type, declaration.Target.ResolvedType, declaration.IsFixedStorage, typeScope);
		string? initialValueFact = GetExpressionLifetimeFact(declaration.InitialValue);
		if (declaration.InitialValue is not null
			&& expressionRewrites.TryGetValue(declaration.InitialValue, out Expression? rewrittenInitialValue)
			&& !ReferenceEquals(rewrittenInitialValue, declaration.InitialValue))
		{
			initialValueFact = GetExpressionLifetimeFact(rewrittenInitialValue) ?? initialValueFact;
		}
		if (declaration.IsStackAllocStorage && !string.IsNullOrWhiteSpace(name))
			initialValueFact = MakeLifetimeFact("scoped", name, "stackalloc slot");
		else if (initialValueFact is not null
			&& !string.IsNullOrWhiteSpace(name)
			&& TryParseLifetimeFact(initialValueFact, out LifetimeFact parsedInitialValueFact)
			&& IsStackAllocLifetimeFact(parsedInitialValueFact))
		{
			initialValueFact = FormatLifetimeFact(new LifetimeFact("scoped", [name], parsedInitialValueFact.Source));
		}
		declaration.Target.SlotLifetimeFact = slotFact;
		declaration.Target.ValueLifetimeFact = declaration.IsFixedStorage
			? slotFact
			: initialValueFact
				?? CreateDeclarationValueLifetimeFact(declaration.Target.Type, declaration.Target.ResolvedType, declaration.IsFixedStorage, typeScope)
				?? slotFact;
		declaration.SlotLifetimeFact = slotFact;
		declaration.ValueLifetimeFact = declaration.Target.ValueLifetimeFact;
	}

	void ApplyExpressionLifetimeFact(Expression expression, string resolvedType, BodyScope scope, AnalysisScope typeScope)
	{
		if (IsEscapedValueType(resolvedType))
		{
			expression.ValueLifetimeFact = MakeLifetimeFact("escaped", null, "escaped type");
			return;
		}
		if (expressionRewrites.TryGetValue(expression, out Expression? rewritten)
			&& !ReferenceEquals(rewritten, expression))
		{
			if (GetExpressionLifetimeFact(rewritten) is string rewrittenFact)
				expression.ValueLifetimeFact = rewrittenFact;
			else if (rewritten is MethodReferenceExpression)
				expression.ValueLifetimeFact = MakeLifetimeFact("escaped", null, "function pointer");
			else if (rewritten is VariableReferenceExpression variableReference)
				expression.ValueLifetimeFact = GetStorageValueLifetimeFact(variableReference.Variable);
			else if (rewritten is MemberReferenceExpression { Member: FieldDefinition field } fieldReference
				&& GetFieldReferenceLifetimeFact(fieldReference, field, resolvedType, scope) is string fieldFact)
				expression.ValueLifetimeFact = fieldFact;
			else if (rewritten is MemberReferenceExpression { Member: FunctionDefinition, Target: not null } methodReference)
				expression.ValueLifetimeFact = GetBoundMethodLifetimeFact(methodReference.Target, scope);
			return;
		}

		string? fact = expression switch
		{
			VariableReferenceExpression variable => GetStorageValueLifetimeFact(variable.Variable),
			NamedExpression named => GetNamedExpressionLifetimeFact(named),
			ThisExpression => scope.CurrentFunction.ReceiverLifetimeBinding ?? MakeLifetimeFact("scoped", "this", "receiver"),
			LiteralExpression literal => GetLiteralLifetimeFact(literal),
			DefaultExpression => IsLifetimePointerBearingResolvedType(resolvedType, scope) ? "default" : null,
			ArrayExpression => MakeLifetimeFact("scoped", null, "array literal"),
			InitializerExpression initializer => GetInitializerLifetimeFact(initializer, resolvedType, scope),
			ParenthesizedExpression parenthesized => GetExpressionLifetimeFact(parenthesized.Expression),
			CastExpression { LifetimeCastKind: not null } cast => cast.LifetimeBinding,
			CastExpression cast => cast.LifetimeBinding
				?? (IsFunctionPointerResolvedType(resolvedType) ? MakeLifetimeFact("escaped", null, "function pointer cast") : null)
				?? GetExpressionLifetimeFact(cast.Expression),
			PreparedBufferExpression prepared => prepared.HeapAllocated
				? MakeLifetimeFact("escaped", null, "new")
				: MakeLifetimeFact("scoped", null, "stackalloc"),
			InterpolatedStringExpression interpolation => interpolation.HeapAllocated
				? MakeLifetimeFact("escaped", null, "new")
				: interpolation.StackAllocated
					? MakeLifetimeFact("scoped", null, "stackalloc")
					: IsLifetimePointerBearingResolvedType(resolvedType, scope)
						? MakeLifetimeFact("scoped", null, "stackalloc interpolation")
						: null,
			ConstructionExpression construction => IsLifetimePointerBearingResolvedType(resolvedType, scope) ? GetConstructionLifetimeFact(construction, resolvedType, scope) : null,
			WithinExpression within => GetWithinExpressionLifetimeFact(within, resolvedType, scope),
			FinallyCleanupExpression finallyCleanup => GetExpressionLifetimeFact(finallyCleanup.Expression),
			UnaryExpression unary => GetUnaryLifetimeFact(unary, resolvedType, scope),
			IndexExpression index => GetIndexLifetimeFact(index, resolvedType),
			MemberExpression member => GetMemberLifetimeFact(member, resolvedType, scope),
			MemberReferenceExpression { Member: FieldDefinition field } member => GetFieldReferenceLifetimeFact(member, field, resolvedType, scope),
			MemberReferenceExpression { Member: FunctionDefinition, Target: not null } member => GetBoundMethodLifetimeFact(member.Target, scope),
			MethodReferenceExpression => MakeLifetimeFact("escaped", null, "function pointer"),
			CallExpression call => GetCallLifetimeFact(call, resolvedType, scope),
			LambdaExpression lambda when !LambdaHasCaptures(lambda, scope.CurrentFunction, scope.ContainingType) => MakeLifetimeFact("escaped", null, "lambda"),
			LambdaExpression lambda => TryGetResolvedTypeLifetime(lambda.ResolvedType, out string lambdaLifetime)
				? MakeLifetimeFact(lambdaLifetime, null, "lambda")
				: MakeLifetimeFact("scoped", null, "lambda"),
			AssignmentExpression assignment => GetExpressionLifetimeFact(assignment.Value),
			ConditionalExpression conditional => GetConditionalLifetimeFact(conditional),
			_ => null
		};

		if (fact is not null)
			expression.ValueLifetimeFact = fact;
	}

	string GetBoundMethodLifetimeFact(Expression receiver, BodyScope scope)
	{
		if (receiver is ThisExpression)
		{
			if (TryParseLifetimeFact(GetExpressionLifetimeFact(receiver) ?? scope.CurrentFunction.ReceiverLifetimeBinding, out LifetimeFact thisFact))
				return FormatLifetimeFact(thisFact.Kind == "scoped" && thisFact.Anchors.Count == 0 ? thisFact with { Anchors = ["this"] } : thisFact);
			return MakeLifetimeFact("scoped", "this", "receiver");
		}
		if ((TryGetPointerElementType(receiver.ResolvedType) is not null || IsPrimitiveStringType(receiver.ResolvedType ?? "")
			|| TryGetArrayElementType(receiver.ResolvedType) is not null)
			&& GetExpressionLifetimeFact(receiver) is string fact)
			return fact;
		if (expressionRewrites.TryGetValue(receiver, out Expression? rewrittenReceiver) && !ReferenceEquals(receiver, rewrittenReceiver))
			return GetBoundMethodLifetimeFact(rewrittenReceiver, scope);
		if (receiver is MemberReferenceExpression { Member: FieldDefinition { Modifier: not FieldModifier.Static }, Target: not null } fieldReceiver)
			return GetBoundMethodLifetimeFact(fieldReceiver.Target, scope);
		if (receiver is IndexExpression { Target: Expression indexedStorage } && TryGetArrayElementType(indexedStorage.ResolvedType) is not null)
			return GetBoundMethodLifetimeFact(indexedStorage, scope);
		if (TryGetStorageNode(receiver, out BindableNode? storage))
		{
			if (storage is ParameterDefinition parameter)
				return MakeLifetimeFact("scoped", parameter.Name, parameter.Modifier == ParameterModifier.In ? "in parameter address:bound receiver" : "parameter address:bound receiver");
			return storage?.SlotLifetimeFact ?? MakeLifetimeFact("scoped", GetStorageLifetimeAnchor(storage), "bound receiver");
		}
		return MakeLifetimeFact("scoped", null, "bound receiver");
	}

	void CheckCallableContextLifetime(Expression expression, string? targetType)
	{
		bool escapedReceiver = TryGetCallableShape(targetType, out CallableShape callable) && callable.This.IsEscaped
			&& callable.Kind is "delegate" or "once" or "async";
		if (expression.ResolvedType == ErrorType || (!IsEscapedCallableTarget(targetType) && !escapedReceiver)
			|| expression is LambdaExpression && !escapedReceiver)
			return;
		if (expression.SourceSyntax is not null && diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
			&& Equals(diagnostic.Range, GetRange(expression.SourceSyntax))))
			return;
		if (TryParseLifetimeFact(GetExpressionLifetimeFact(expression), out LifetimeFact fact)
			&& fact.Kind is not ("escaped" or "null" or "default"))
			Report(GetRange(expression.SourceSyntax), "Expression cannot satisfy escaped callable context lifetime.");
	}

	string? GetConditionalLifetimeFact(ConditionalExpression conditional)
	{
		string? first = GetExpressionLifetimeFact(conditional.WhenTrue);
		string? second = GetExpressionLifetimeFact(conditional.WhenFalse);
		// Either arm can select activation-local storage, including when the other
		// arm is an escaped constant or an allocated interpolation.
		if (TryParseLifetimeFact(first, out LifetimeFact firstFact) && IsStackAllocLifetimeFact(firstFact))
			return first;
		if (TryParseLifetimeFact(second, out LifetimeFact secondFact) && IsStackAllocLifetimeFact(secondFact))
			return second;
		return first ?? second;
	}

	string GetLifetimeStructuralTargetType(string targetType, Expression? expression)
	{
		if (GetExpressionLifetimeFact(expression) is null)
			return targetType;
		return TryGetResolvedTypeLifetime(targetType, out _) ? StripLifetimeQualifiers(targetType) : targetType;
	}

	string? GetNamedExpressionLifetimeFact(NamedExpression named)
	{
		if (expressionRewrites.TryGetValue(named, out Expression? rewritten) && !ReferenceEquals(rewritten, named))
			return GetExpressionLifetimeFact(rewritten);

		return named.ValueLifetimeFact;
	}

	static string? GetStorageValueLifetimeFact(BindableNode? storage)
	{
		return storage?.ValueLifetimeFact ?? storage?.SlotLifetimeFact;
	}

	string? GetExpressionLifetimeFact(Expression? expression)
	{
		if (expression is null)
			return null;
		if (expression.ValueLifetimeFact is not null)
			return expression.ValueLifetimeFact;
		if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(rewritten, expression))
			return GetExpressionLifetimeFact(rewritten);
		return null;
	}

	static string? GetLiteralLifetimeFact(LiteralExpression literal)
	{
		return literal.Kind switch
		{
			LiteralKind.Null => "null",
			LiteralKind.String => MakeLifetimeFact("escaped", null, "literal"),
			_ => null
		};
	}

	string? GetConstructionLifetimeFact(ConstructionExpression construction, string resolvedType, BodyScope scope)
	{
		return construction.Kind switch
		{
			ConstructionKind.New when IsGenericConstructedType(construction, resolvedType, scope) => MakeLifetimeFact("escaped", null, "new"),
			ConstructionKind.New => GetNewConstructionLifetimeFact(construction, resolvedType, scope),
			ConstructionKind.StackAlloc => MakeLifetimeFact("scoped", null, "stackalloc"),
			ConstructionKind.Init or ConstructionKind.Selected => GetInitConstructionLifetimeFact(construction, resolvedType, scope),
			_ => null
		};
	}

	// An interface constructor allocates through its witness, so `new T()` always yields an escaped instance.
	static bool IsGenericConstructedType(ConstructionExpression construction, string resolvedType, BodyScope scope)
	{
		string typeName = TryGetPointerElementType(resolvedType) ?? resolvedType;
		foreach (GenericParameter parameter in scope.CurrentFunction.GenericParameters)
			if (parameter.Name == typeName)
				return true;
		return false;
	}

	string? GetNewConstructionLifetimeFact(ConstructionExpression construction, string resolvedType, BodyScope scope)
	{
		foreach (LifetimeFact retained in GetConstructionRetainedLifetimeFacts(construction, resolvedType, scope, heap: true))
			if (retained.Kind is not ("escaped" or "null" or "default"))
			{
				Report(GetRange(construction.SourceSyntax), "Heap construction cannot retain a non-escaped pointer-bearing value.");
				break;
			}
		return MakeLifetimeFact("escaped", null, "new");
	}

	string? GetWithinExpressionLifetimeFact(WithinExpression within, string resolvedType, BodyScope scope)
	{
		if (within.Expression is ConstructionExpression { Kind: ConstructionKind.New }
			&& IsLifetimePointerBearingResolvedType(resolvedType, scope))
		{
			FunctionDefinition? alloc = FindAllocatorPatternMethod(within.Context?.ResolvedType, "alloc", IsSingleIntegerValueParameter, within.SourceSyntax);
			if (GetFunctionReturnLifetimeFact(alloc, "new") is string allocationFact)
				return allocationFact;
		}

		return GetExpressionLifetimeFact(within.Expression);
	}

	string? GetFunctionReturnLifetimeFact(FunctionDefinition? function, string source)
	{
		if (function?.ReturnType?.LifetimeBinding is string explicitReturn)
			return explicitReturn + ":" + source;
		if (TryGetResolvedTypeLifetime(function?.ReturnType?.ResolvedType ?? function?.ResolvedType ?? "", out string lifetimeKind))
			return MakeLifetimeFact(lifetimeKind, null, source);
		return null;
	}

	string? GetInitConstructionLifetimeFact(ConstructionExpression construction, string resolvedType, BodyScope scope)
	{
		return CombineRetainedLifetimeFacts(GetConstructionRetainedLifetimeFacts(construction, resolvedType, scope)) is LifetimeFact retained
			? FormatLifetimeFact(new LifetimeFact(retained.Kind, retained.Anchors, "init"))
			: null;
	}

	List<LifetimeFact> GetConstructionRetainedLifetimeFacts(ConstructionExpression construction, string resolvedType, BodyScope scope, bool heap = false)
	{
		List<LifetimeFact> retainedFacts = [];
		if (!constructionTargets.TryGetValue(construction, out FunctionDefinition? constructor))
			constructor = FindConstructionInitNew(construction, resolvedType);
		if (constructor is not null)
		{
			List<ParameterDefinition> parameters = GetCallableParameters(constructor.Parameters, false);
			if (constructor.Modifier == FunctionModifier.Constructor)
			{
				if (FindConstructionInitNew(construction, resolvedType) is FunctionDefinition initNew)
					parameters = GetCallableParameters(initNew.Parameters, false);
			}
			int count = Math.Min(parameters.Count, construction.Arguments.Count);
			for (int i = 0; i < count; i++)
			{
				// Scoped inputs cannot be retained by a constructor's source contract.
				if (heap && GetParameterLifetimeTemplate(parameters[i]).Kind == "scoped")
					continue;
				if (!IsLifetimePointerBearingResolvedType(parameters[i].ResolvedType, scope)
					&& GetExpressionLifetimeFact(construction.Arguments[i].Value) is null)
					continue;
				if (TryParseLifetimeFact(GetExpressionLifetimeFact(construction.Arguments[i].Value), out LifetimeFact argumentFact))
					retainedFacts.Add(argumentFact);
			}
		}

		AddInitializerRetainedLifetimeFacts(construction.Initializer, resolvedType, scope, retainedFacts);
		if (ConstructionRetainsAllocator(construction, resolvedType))
			retainedFacts.Add(TryParseLifetimeFact(scope.CurrentWithinContextLifetimeFact, out LifetimeFact allocator)
				? allocator : new LifetimeFact("escaped", [], "default allocator"));
		return retainedFacts;
	}

	bool ConstructionRetainsAllocator(ConstructionExpression construction, string resolvedType)
	{
		string name = BaseConstructedType(construction.Type?.ResolvedType ?? resolvedType);
		HashSet<ClassDefinition> seen = [];
		if (typeDefinitions.TryGetValue(name, out TypeDefinition? definition))
			for (ClassDefinition? current = definition as ClassDefinition; current is not null && seen.Add(current); current = GetDirectBaseClass(current))
				if (LifecycleAllocatorPolicy.RetainsAllocator(current.Functions))
					return true;
		return false;
	}

	string? GetInitializerLifetimeFact(InitializerExpression initializer, string resolvedType, BodyScope scope)
	{
		List<LifetimeFact> retainedFacts = [];
		AddInitializerRetainedLifetimeFacts(initializer, resolvedType, scope, retainedFacts);
		return CombineRetainedLifetimeFacts(retainedFacts) is LifetimeFact retained
			? FormatLifetimeFact(new LifetimeFact(retained.Kind, retained.Anchors, "initializer"))
			: null;
	}

	FunctionDefinition? FindConstructionInitNew(ConstructionExpression construction, string resolvedType)
	{
		string typeName = BaseConstructedType(construction.Type?.ResolvedType ?? resolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return null;
		return FindInitNewMethod(definition, construction.Arguments.Count)
			?? GetTypeFunctions(definition).FirstOrDefault(function => function.Name == InitNewMethodName);
	}

	void AddInitializerRetainedLifetimeFacts(InitializerExpression? initializer, string resolvedType, BodyScope scope, List<LifetimeFact> retainedFacts)
	{
		if (initializer is null || !typeDefinitions.TryGetValue(BaseConstructedType(resolvedType), out TypeDefinition? definition))
			return;

		List<FieldDefinition> fields = definition switch
		{
			StructDefinition structure => structure.Fields,
			ClassDefinition classDefinition => classDefinition.Fields,
			_ => []
		};
		int positionalIndex = 0;
		foreach (InitializerItem item in initializer.Items)
		{
			FieldDefinition? field = null;
			string? targetName = GetSingleInitializerTargetName(item.Target);
			if (targetName is not null)
				field = fields.FirstOrDefault(candidate => candidate.Name == targetName);
			else if (positionalIndex < fields.Count)
				field = fields[positionalIndex++];

			if (field is null || !IsPointerBearingType(field.Type, new AnalysisScope()))
				continue;
			if (TryParseLifetimeFact(GetExpressionLifetimeFact(item.Expression), out LifetimeFact fact))
				retainedFacts.Add(fact);
		}
	}

	static LifetimeFact? CombineRetainedLifetimeFacts(List<LifetimeFact> facts)
	{
		List<LifetimeFact> relevant = facts
			.Where(fact => fact.Kind is not ("default" or "null" or "unknown"))
			.ToList();
		if (relevant.Count == 0)
			return null;
		if (relevant.All(fact => fact.Kind == "escaped"))
			return new LifetimeFact("escaped", [], "retained");

		List<string> scopedAnchors = relevant
			.Where(fact => fact.Kind == "scoped")
			.SelectMany(fact => fact.Anchors)
			.Distinct(StringComparer.Ordinal)
			.ToList();
		if (scopedAnchors.Count > 0 || relevant.Any(fact => fact.Kind == "scoped"))
			return new LifetimeFact("scoped", scopedAnchors, "retained");

		List<string> unscopedAnchors = relevant
			.Where(fact => fact.Kind == "unscoped")
			.SelectMany(fact => fact.Anchors)
			.Distinct(StringComparer.Ordinal)
			.ToList();
		return new LifetimeFact("unscoped", unscopedAnchors, "retained");
	}

	string? GetUnaryLifetimeFact(UnaryExpression unary, string resolvedType, BodyScope scope)
	{
		if (unary.Operator == UnaryOperator.AddressOf)
		{
			if (unary.Operand is not null && GetEscapedConversionStorageFact(unary.Operand) is string storageFact
				&& TryParseLifetimeFact(storageFact, out LifetimeFact addressedStorage) && addressedStorage.Kind == "escaped"
				&& TryGetPointerElementType(unary.Operand.ResolvedType) is null)
				return storageFact;
			if (TryGetInParameterAnchor(unary.Operand, scope, out string inParameterAnchor))
				return MakeLifetimeFact("scoped", inParameterAnchor, "in parameter address");
			if (unary.Operand is not null && TryGetStorageNode(unary.Operand, out BindableNode? parameterStorage)
				&& parameterStorage is ParameterDefinition parameter)
				return MakeLifetimeFact("scoped", parameter.Name, "parameter address");
			if (unary.Operand is not null && TryGetStorageNode(unary.Operand, out BindableNode? addressStorage)
				&& addressStorage is DeclarationTarget localStorage)
				return IsEscapedValueType(localStorage.ResolvedType) ? MakeLifetimeFact("scoped", GetStorageLifetimeAnchor(localStorage), "address-of")
					: localStorage.SlotLifetimeFact ?? MakeLifetimeFact("scoped", GetStorageLifetimeAnchor(localStorage), "address-of");
			if (GetExpressionLifetimeFact(unary.Operand) is string operandFact)
				return operandFact;
			if (unary.Operand is not null
				&& TryGetStorageNode(unary.Operand, out BindableNode? storage)
				&& GetStorageLifetimeAnchor(storage) is string anchor)
				return MakeLifetimeFact("scoped", anchor, "address-of");
			return MakeLifetimeFact("scoped", null, "address-of");
		}
		if (IsLifetimePointerBearingResolvedType(resolvedType, scope))
			return GetExpressionLifetimeFact(unary.Operand);
		return null;
	}

	bool TryGetInParameterAnchor(Expression? expression, BodyScope scope, out string anchor)
	{
		anchor = "";
		if (expression is null)
			return false;
		if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(rewritten, expression))
			return TryGetInParameterAnchor(rewritten, scope, out anchor);

		ParameterDefinition? parameter = null;
		switch (expression)
		{
			case VariableReferenceExpression { Variable: ParameterDefinition candidate }:
				parameter = candidate;
				break;
			case NamedExpression { Qualifiers.Count: 0 } named when scope.TryLookup(named.Name, out BodySymbol symbol):
				parameter = symbol.Node as ParameterDefinition;
				break;
			case ParenthesizedExpression parenthesized:
				return TryGetInParameterAnchor(parenthesized.Expression, scope, out anchor);
		}
		if (parameter is not { Modifier: ParameterModifier.In } || string.IsNullOrWhiteSpace(parameter.Name))
			return false;
		anchor = parameter.Name;
		return true;
	}

	string? GetIndexLifetimeFact(IndexExpression index, string resolvedType)
	{
		string? targetFact = GetExpressionLifetimeFact(index.Target);
		if (TryGetArrayElementType(resolvedType) is not null)
			return targetFact is not null ? targetFact + ":slice" : MakeLifetimeFact("scoped", null, "slice");
		// A view's element is retained by the view. Inline fixed storage instead
		// belongs to its containing slot and must keep that address lifetime.
		string source = TryGetArrayElementType(index.Target?.ResolvedType) is not null ? ":retained element" : ":element";
		return targetFact is not null ? targetFact + source : null;
	}

	// Prebound field reads still need the receiver's lifetime, including generated iterator state reads.
	string? GetFieldReferenceLifetimeFact(MemberReferenceExpression reference, FieldDefinition field, string resolvedType, BodyScope scope)
	{
		bool inlineStorage = TryGetFixedArrayShape(resolvedType, out _, out _);
		if (!inlineStorage && !IsLifetimePointerBearingResolvedType(resolvedType, scope))
			return null;
		if (IsEscapedStorage(field))
			return MakeLifetimeFact("escaped", field.Name, "field");

		// A fixed field is physical storage in its owner, even if its elements
		// contain no pointers. Its views borrow the owner's address, not its content.
		string? targetFact = inlineStorage && reference.Target is not null
			? GetBoundMethodLifetimeFact(reference.Target, scope) : GetExpressionLifetimeFact(reference.Target);
		if (targetFact is null && reference.Target is ThisExpression
			&& scope.ContainingType?.GeneratedInfo is { Category: GeneratedDeclarationCategory.Iterator })
			targetFact = MakeLifetimeFact("scoped", "this", "iterator state");
		return targetFact is not null ? targetFact + ":member" : MakeLifetimeFact("scoped", null, "member");
	}

	string? GetMemberLifetimeFact(MemberExpression member, string resolvedType, BodyScope scope)
	{
		bool inlineStorage = TryGetFixedArrayShape(resolvedType, out _, out _);
		if (!inlineStorage && !IsLifetimePointerBearingResolvedType(resolvedType, scope))
			return null;
		if (expressionRewrites.TryGetValue(member, out Expression? rewritten)
			&& rewritten is MemberReferenceExpression { Member: FieldDefinition field }
			&& IsEscapedStorage(field))
			return MakeLifetimeFact("escaped", field.Name, "field");

		string? targetFact = inlineStorage && member.Target is not null
			? GetBoundMethodLifetimeFact(member.Target, scope) : GetExpressionLifetimeFact(member.Target);
		return targetFact is not null ? targetFact + ":member" : MakeLifetimeFact("scoped", null, "member");
	}

	string? GetCallLifetimeFact(CallExpression call, string resolvedType, BodyScope scope)
	{
		if (!IsLifetimePointerBearingResolvedType(resolvedType, scope))
			return null;

		if (callTargets.TryGetValue(call, out FunctionDefinition? function))
		{
			if (generatedCreateHelpers.Contains(function) && FindContainingType(function) is TypeDefinition owner)
			{
				ConstructionExpression allocation = new()
				{
					Kind = ConstructionKind.New,
					Type = TypeReferenceFor(owner),
					SourceSyntax = call.SourceSyntax
				};
				allocation.Arguments.AddRange(call.Arguments);
				if (function.GeneratedInfo?.Source is FunctionDefinition constructor)
					constructionTargets[allocation] = constructor;
				return GetNewConstructionLifetimeFact(allocation, resolvedType, scope);
			}
			if (function.Modifier == FunctionModifier.Constructor || function.Name == InitNewMethodName)
				return GetConstructorCallLifetimeFact(call, resolvedType, scope);

			LifetimeFact template = GetReturnLifetimeTemplate(function, resolvedType);
			List<ParameterDefinition> callableParameters = GetCallableParameters(function.Parameters, IncludeExplicitThisArgument(call.Target, function));
			LifetimeCallContext context = BuildLifetimeCallContext(function, call.Target, callableParameters, ZipCallArguments(call.Arguments, callableParameters), substitutions: null, scope);
			if (ReturnsReceiverItself(function))
				context = context with { RetainedAnchors = context.Anchors, RetainedInputs = context.ScopedInputs };
			return SubstituteLifetimeTemplate(template, context, "call") is LifetimeFact fact ? FormatLifetimeFact(fact) : null;
		}

		return MakeLifetimeFact("unknown", null, "call");
	}

	// The lifetime of the values a local struct holds, fixed by what it was initialized from.
	LifetimeFact? GetLocalValueReceiverContentFact(Expression? callTarget)
	{
		Expression? receiver = callTarget switch
		{
			MemberExpression member => member.Target,
			MemberReferenceExpression member => member.Target,
			_ => null
		};
		while (receiver is not null)
		{
			if (expressionRewrites.TryGetValue(receiver, out Expression? rewritten) && !ReferenceEquals(rewritten, receiver))
				receiver = rewritten;
			else if (receiver is ParenthesizedExpression parenthesized)
				receiver = parenthesized.Expression;
			else
				break;
		}
		if (receiver is null || TryGetPointerElementType(receiver.ResolvedType) is not null || IsEscapedValueType(receiver.ResolvedType))
			return null;
		bool localRooted = receiver switch
		{
			VariableReferenceExpression { Variable: DeclarationTarget } => true,
			MemberReferenceExpression { Member: FieldDefinition { Modifier: not FieldModifier.Static } } member => IsLocalValueRoot(member.Target),
			_ => false
		};
		if (!localRooted)
			return null;
		// A field used only to select a method may be prebound without a read fact.
		// Its inline value still retains the content of the containing local.
		while (GetExpressionLifetimeFact(receiver) is null
			&& receiver is MemberReferenceExpression { Member: FieldDefinition { Modifier: not FieldModifier.Static }, Target: Expression owner })
			receiver = owner;
		return TryParseLifetimeFact(GetExpressionLifetimeFact(receiver), out LifetimeFact fact) ? fact : null;
	}

	bool IsLocalValueRoot(Expression? expression)
	{
		while (expression is not null)
		{
			if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(rewritten, expression))
				expression = rewritten;
			else if (expression is ParenthesizedExpression parenthesized)
				expression = parenthesized.Expression;
			else
				break;
		}
		return expression switch
		{
			VariableReferenceExpression { Variable: DeclarationTarget } => TryGetPointerElementType(expression.ResolvedType) is null,
			MemberReferenceExpression { Member: FieldDefinition { Modifier: not FieldModifier.Static }, Target: Expression target } => TryGetPointerElementType(target.ResolvedType) is null && IsLocalValueRoot(target),
			_ => false
		};
	}

	string? GetConstructorCallLifetimeFact(CallExpression call, string resolvedType, BodyScope scope)
	{
		if (!IsLifetimePointerBearingResolvedType(resolvedType, scope))
			return null;

		List<LifetimeFact> retainedFacts = [];
		foreach (ArgumentExpression argument in call.Arguments)
		{
			if (argument.Value is null)
				continue;
			if (argument.Value is UnaryExpression { Operator: UnaryOperator.AddressOf })
				continue;
			if (!IsLifetimePointerBearingResolvedType(argument.Value.ResolvedType ?? argument.ResolvedType ?? ErrorType, scope)
				&& GetExpressionLifetimeFact(argument.Value) is null)
				continue;
			if (TryParseLifetimeFact(GetExpressionLifetimeFact(argument.Value), out LifetimeFact argumentFact))
				retainedFacts.Add(argumentFact);
		}

		return CombineRetainedLifetimeFacts(retainedFacts) is LifetimeFact retained
			? FormatLifetimeFact(new LifetimeFact(retained.Kind, retained.Anchors, "init"))
			: null;
	}

	static List<(ArgumentExpression Argument, ParameterDefinition Parameter)> ZipCallArguments(List<ArgumentExpression> arguments, List<ParameterDefinition> parameters)
	{
		List<(ArgumentExpression Argument, ParameterDefinition Parameter)> pairs = [];
		int count = Math.Min(arguments.Count, parameters.Count);
		for (int i = 0; i < count; i++)
			pairs.Add((arguments[i], parameters[i]));
		return pairs;
	}

	string RefineCallReturnTypeFromLifetimeArguments(FunctionDefinition function, Expression? callTarget, List<ArgumentExpression> arguments, string returnType)
	{
		return returnType;
	}

	LifetimeFact GetReturnLifetimeTemplate(FunctionDefinition function, string resolvedType)
	{
		if (IsEscapedValueType(resolvedType))
			return new LifetimeFact("escaped", [], "escaped type");
		if (function.ReturnType?.LifetimeBinding is string explicitReturn
			&& TryParseLifetimeFact(explicitReturn, out LifetimeFact explicitFact))
			return explicitFact;
		if (TryGetLifetimeAnnotation(function.ReturnType, out string explicitKind, out IReadOnlyList<string> explicitAnchors, out string? explicitBinding))
		{
			if (explicitBinding is not null && TryParseLifetimeFact(explicitBinding, out LifetimeFact nestedExplicitFact))
				return nestedExplicitFact;
			return new LifetimeFact(explicitKind, explicitAnchors, "return type");
		}
		if (TryGetResolvedTypeLifetime(function.ReturnType?.ResolvedType ?? resolvedType, out string lifetimeKind))
			return new LifetimeFact(lifetimeKind, [], "return type");

		// A generated create helper allocates, so its result is an escaped instance.
		if (generatedCreateHelpers.Contains(function))
			return new LifetimeFact("escaped", [], "create");

		if (IsReceiverBearingDeclaration(function))
			return new LifetimeFact("unscoped", ["this"], "return default");

		return new LifetimeFact("unscoped", [], "return default");
	}

	void CheckLifetimeCallArguments(FunctionDefinition function, Expression? callTarget, List<(ArgumentExpression Argument, ParameterDefinition Parameter)> arguments, BodyScope scope, SyntaxNode? fallbackSyntax, Dictionary<string, string>? substitutions)
	{
		if (GetWithinParameter(function) is ParameterDefinition allocatorParameter
			&& IsEscapedValueType(SubstituteGenericType(allocatorParameter.ResolvedType ?? ErrorType, substitutions ?? []))
			&& TryParseLifetimeFact(scope.CurrentWithinContextLifetimeFact, out LifetimeFact allocatorFact)
			&& allocatorFact.Kind is not ("escaped" or "null" or "default"))
			Report(GetRange(fallbackSyntax), "A within parameter of an escaped type requires escaped allocator storage.");
		if (function.Modifier == FunctionModifier.Constructor || function.Name == InitNewMethodName)
			return;

		List<ParameterDefinition> callableParameters = GetCallableParameters(function.Parameters, IncludeExplicitThisArgument(callTarget, function));
		LifetimeCallContext context = BuildLifetimeCallContext(function, callTarget, callableParameters, arguments, substitutions, scope);
		if (RequiresEscapedReceiver(function) && context.Receiver is LifetimeFact receiver
			&& receiver.Kind is not ("escaped" or "null" or "default" or "unknown"))
		{
			string? anchor = receiver.Anchors.FirstOrDefault();
			string description = anchor is null ? "receiver" : IsLocalLifetimeAnchor(anchor, scope)
				? $"receiver from local '{anchor}'" : IsParameterLifetimeAnchor(anchor, scope)
				? $"receiver from parameter '{anchor}'" : $"receiver '{anchor}'";
			Report(GetRange(fallbackSyntax), $"Cannot call '{function.Name}': it requires an escaped receiver, but the {description} is not escaped. Pass an escaped receiver, such as a 'new' allocation, or remove 'escaped this' from the method if it does not retain its receiver.");
		}
		CheckLifetimeReceiverRetention(function, context, scope, fallbackSyntax);
		foreach ((ArgumentExpression argument, ParameterDefinition parameter) in arguments)
		{
			if (parameter.Modifier == ParameterModifier.Out)
			{
				ApplyOutParameterLifetime(function, argument, parameter, context, scope, fallbackSyntax, substitutions);
				continue;
			}
			if (parameter.Modifier == ParameterModifier.Thrown || parameter.Modifier == ParameterModifier.Within)
				continue;

			CheckLifetimeParameterArgument(function, argument, parameter, context, scope, fallbackSyntax, substitutions);
		}
	}

	// `unscoped(destination) this` says the receiver is retained by `destination`, so it must outlive what is passed there.
	void CheckLifetimeReceiverRetention(FunctionDefinition function, LifetimeCallContext context, BodyScope scope, SyntaxNode? syntax)
	{
		if (context.Receiver is not LifetimeFact actual
			|| !TryParseLifetimeFact(function.ReceiverLifetimeBinding, out LifetimeFact template)
			|| template is not { Kind: "unscoped", Source: "explicit receiver" } || template.Anchors.Count == 0)
			return;

		foreach (string anchor in template.Anchors)
		{
			if (!context.Anchors.TryGetValue(anchor, out LifetimeFact? required) || required.Kind == "unknown")
				continue;
			if (ValueOutlivesFact(actual, required) || LocalLifetimeOutlives(actual, required, scope))
				continue;
			if (NonLocalLifetimeOutlivesLocal(actual, required, scope))
				continue;

			string name = function.Name;
			string? local = actual.Anchors.FirstOrDefault(candidate => IsLocalLifetimeAnchor(candidate, scope));
			string reason = local is not null
				? $"'{local}' is a local that ends before the value passed for '{anchor}'"
				: $"the receiver does not outlive the value passed for '{anchor}'";
			string remedy = local is not null
				? $"Declare '{local}' where it outlives the destination, or pass a destination that does not outlive it."
				: "Pass a receiver that outlives the destination, or a destination that does not outlive the receiver.";
			Report(GetRange(syntax), $"Cannot call '{name}': it requires the receiver to outlive '{anchor}' (`unscoped({anchor}) this`), but {reason}. {remedy}");
			return;
		}
	}

	void CheckLifetimeParameterArgument(FunctionDefinition function, ArgumentExpression argument, ParameterDefinition parameter, LifetimeCallContext context, BodyScope scope, SyntaxNode? fallbackSyntax, Dictionary<string, string>? substitutions)
	{
		if (argument.ResolvedType == ErrorType || argument.Value?.ResolvedType == ErrorType)
			return;
		string effectiveParameterType = SubstituteGenericType(parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? ErrorType, substitutions ?? []);
		if (!IsLifetimePointerBearingResolvedType(argument.ResolvedType, scope) && !IsLifetimePointerBearingResolvedType(effectiveParameterType, scope))
			return;
		if (argument.Value is VariableReferenceExpression { Variable: ParameterDefinition argumentParameter }
			&& ReferenceEquals(argumentParameter, parameter))
			return;
		if (!TryParseLifetimeFact(GetExpressionLifetimeFact(argument.Value), out LifetimeFact actualFact))
			return;
		if (actualFact.Kind == "unknown")
			return;

		LifetimeFact template = GetParameterLifetimeTemplate(parameter, effectiveParameterType);
		if (template.Kind == "escaped" && HasFailedEscapedTypeConversion(argument.Value, effectiveParameterType))
			return;
		if (function.Modifier == FunctionModifier.Constructor && template.Kind == "unscoped" && template.Anchors.Count == 0)
			return;
		List<LifetimeFact> requiredFacts = GetRequiredArgumentLifetimeFacts(template, context);
		if (requiredFacts.Count == 0)
			return;

		foreach (LifetimeFact required in requiredFacts)
		{
			if (required.Kind == "unknown")
				continue;
			if (ValueOutlivesFact(actualFact, required) || LocalLifetimeOutlives(actualFact, required, scope)
				|| NonLocalLifetimeOutlivesLocal(actualFact, required, scope))
				continue;
			string detail = "";
			if (template is { Kind: "unscoped", Anchors.Count: > 0 })
			{
				string source = SpannedExpressionText(argument.Value!) ?? parameter.Name;
				string destination = required.Anchors.FirstOrDefault() ?? template.Anchors[0];
				string? local = actualFact.Anchors.FirstOrDefault(anchor => IsLocalLifetimeAnchor(anchor, scope));
				detail = $" Argument '{source}' for '{parameter.Name}' does not outlive destination '{destination}'. "
					+ (local is not null
						? $"Declare local '{local}' where it outlives the destination, use a shorter-lived destination, or copy the data."
						: $"Relate the source to the destination with `unscoped({destination})`, use a shorter-lived destination, or copy the data.");
			}
			Report(GetRange(GetArgumentDiagnosticSyntax(argument, fallbackSyntax)), $"Argument cannot satisfy parameter lifetime '{FormatLifetimeRequirement(template)}'.{detail}");
		}
	}

	void ApplyOutParameterLifetime(FunctionDefinition function, ArgumentExpression argument, ParameterDefinition parameter, LifetimeCallContext context, BodyScope scope, SyntaxNode? fallbackSyntax, Dictionary<string, string>? substitutions)
	{
		string outType = SubstituteGenericType(parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? ErrorType, substitutions ?? []);
		if (!IsLifetimePointerBearingResolvedType(outType, scope))
			return;

		LifetimeFact template = GetOutParameterLifetimeTemplate(function, parameter, outType);
		LifetimeFact? substituted = TryGetGenericOutInputLifetime(parameter, context, substitutions, out LifetimeFact genericInput)
			? genericInput
			: SubstituteLifetimeTemplate(template, context, "out");
		if (substituted is null)
			return;

		string fact = FormatLifetimeFact(substituted);
		if (argument.Target is not null)
			argument.Target.ValueLifetimeFact = fact;
		UpdateAssignmentLifetimeFact(argument.Value, fact);
		if (argument.Target is not null)
			argument.ValueLifetimeFact = fact;

		if (TryGetEscapedOutTarget(argument, out Expression? targetExpression) && substituted.Kind != "escaped" && substituted.Kind != "unknown")
			Report(GetRange(GetArgumentDiagnosticSyntax(argument, fallbackSyntax)), "Out argument cannot store a non-escaped pointer-bearing value in escaped storage.");
		else if (targetExpression is not null && TryGetReceiverAnchoredStorageTarget(targetExpression, out string anchor) && !ValueOutlivesAnchor(substituted, anchor))
			Report(GetRange(GetArgumentDiagnosticSyntax(argument, fallbackSyntax)), $"Out argument cannot store a scoped pointer-bearing value in storage tied to '{anchor}'.");
	}

	LifetimeFact GetOutParameterLifetimeTemplate(FunctionDefinition function, ParameterDefinition parameter, string effectiveType)
	{
		if (IsEscapedValueType(effectiveType))
			return new LifetimeFact("escaped", [], "escaped type");
		if (parameter.LifetimeBinding is string explicitLifetime
			&& TryParseLifetimeFact(explicitLifetime, out LifetimeFact explicitFact) && explicitFact.Source != "default parameter")
			return explicitFact;
		if (TryGetResolvedTypeLifetime(effectiveType, out string lifetimeKind)
			|| TryGetResolvedTypeLifetime(parameter.ResolvedType, out lifetimeKind))
			return new LifetimeFact(lifetimeKind, [], "out type");

		if (IsReceiverBearingDeclaration(function))
			return new LifetimeFact("unscoped", ["this"], "out default");

		return new LifetimeFact("unscoped", [], "out default");
	}

	LifetimeFact GetParameterLifetimeTemplate(ParameterDefinition parameter, string? effectiveType = null)
	{
		if (IsEscapedValueType(effectiveType ?? parameter.ResolvedType))
			return new LifetimeFact("escaped", [], "escaped type");
		if (parameter.LifetimeBinding is string explicitLifetime
			&& TryParseLifetimeFact(explicitLifetime, out LifetimeFact explicitFact))
			return explicitFact;
		if (TryGetLifetimeAnnotation(parameter.Type, out string explicitKind, out IReadOnlyList<string> explicitAnchors, out string? explicitBinding))
		{
			if (explicitBinding is not null && TryParseLifetimeFact(explicitBinding, out LifetimeFact nestedExplicitFact))
				return nestedExplicitFact;
			return new LifetimeFact(explicitKind, explicitAnchors, "parameter type");
		}
		if (TryGetResolvedTypeLifetime(parameter.ResolvedType, out string lifetimeKind))
			return new LifetimeFact(lifetimeKind, [], "parameter type");

		return new LifetimeFact("scoped", string.IsNullOrWhiteSpace(parameter.Name) ? [] : [parameter.Name], "parameter default");
	}

	static bool TryGetResolvedTypeLifetime(string? resolvedType, out string lifetimeKind)
	{
		lifetimeKind = "";
		if (string.IsNullOrWhiteSpace(resolvedType))
			return false;

		string type = resolvedType.Trim();
		foreach (string keyword in new[] { "escaped", "unscoped", "scoped" })
		{
			if (type.StartsWith(keyword + " ", StringComparison.Ordinal)
				|| type.StartsWith(keyword + "(", StringComparison.Ordinal)
				|| type.EndsWith(" " + keyword, StringComparison.Ordinal))
			{
				lifetimeKind = keyword;
				return true;
			}
		}

		return false;
	}

	LifetimeCallContext BuildLifetimeCallContext(FunctionDefinition function, Expression? callTarget, List<ParameterDefinition> callableParameters, List<(ArgumentExpression Argument, ParameterDefinition Parameter)> analyzedArguments, Dictionary<string, string>? substitutions, BodyScope scope)
	{
		Dictionary<string, LifetimeFact> anchors = new(StringComparer.Ordinal);
		List<LifetimeFact> scopedInputs = [];
		Dictionary<string, LifetimeFact> retainedAnchors = new(StringComparer.Ordinal);
		List<LifetimeFact> retainedInputs = [];
		Dictionary<string, List<LifetimeFact>> genericInputs = new(StringComparer.Ordinal);
		LifetimeFact? receiverFact = GetReceiverLifetimeFact(callTarget, scope);
		if (receiverFact is not null)
		{
			anchors["this"] = receiverFact;
			LifetimeFact content = GetLocalValueReceiverContentFact(callTarget) ?? receiverFact;
			retainedAnchors["this"] = content;
			if (IsReceiverScopedInput(function))
			{
				scopedInputs.Add(receiverFact);
				retainedInputs.Add(content);
			}
		}

		List<ArgumentExpression> arguments = analyzedArguments.Select(pair => pair.Argument).ToList();
		int count = Math.Min(arguments.Count, callableParameters.Count);
		for (int i = 0; i < count; i++)
		{
			ParameterDefinition parameter = callableParameters[i];
			ArgumentExpression argument = arguments[i];
			if (parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown or ParameterModifier.Within)
				continue;
			if (!TryGetArgumentLifetimeFact(argument, out LifetimeFact actualFact))
				continue;
			LifetimeFact retained = GetPointerArgumentContentFact(argument, actualFact, scope) ?? actualFact;
			if (!string.IsNullOrWhiteSpace(parameter.Name))
			{
				anchors[parameter.Name] = actualFact;
				retainedAnchors[parameter.Name] = retained;
			}
			if (GetParameterLifetimeTemplate(parameter).Kind == "scoped")
			{
				scopedInputs.Add(actualFact);
				retainedInputs.Add(retained);
			}
		}

		foreach ((ArgumentExpression argument, ParameterDefinition parameter) in analyzedArguments)
		{
			if (parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown or ParameterModifier.Within)
				continue;
			string parameterType = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? ErrorType;
			if (!TryGetExactGenericPlaceholder(parameterType, substitutions, out string genericName))
				continue;
			if (!TryGetArgumentLifetimeFact(argument, out LifetimeFact actualFact)
				|| actualFact.Kind is "unknown" or "null" or "default")
			{
				if (!substitutions!.TryGetValue(genericName, out string? substitutedType)
					|| !IsLifetimePointerBearingResolvedType(substitutedType, scope))
					continue;
				actualFact = new LifetimeFact("scoped", [], "generic input");
			}
			if (!genericInputs.TryGetValue(genericName, out List<LifetimeFact>? facts))
			{
				facts = [];
				genericInputs[genericName] = facts;
			}
			facts.Add(actualFact);
		}

		return new LifetimeCallContext(anchors, scopedInputs, receiverFact, genericInputs, retainedAnchors, retainedInputs);
	}

	LifetimeFact? GetPointerArgumentContentFact(ArgumentExpression argument, LifetimeFact address, BodyScope scope)
	{
		string? element = TryGetPointerElementType(argument.Value?.ResolvedType ?? argument.ResolvedType);
		if (element is null || !HasRetainedPointerContent(element, scope)
			|| address is not { Kind: "scoped", Anchors.Count: 1 }
			|| !scope.TryLookup(address.Anchors[0], out BodySymbol symbol)
			|| symbol.Node is not DeclarationTarget local
			|| StripTopLevelValueQualifiers(StripLifetimeQualifiers(local.ResolvedType ?? "")) != StripTopLevelValueQualifiers(StripLifetimeQualifiers(element)))
			return null;
		return TryParseLifetimeFact(local.ValueLifetimeFact, out LifetimeFact content) ? content : null;
	}

	bool HasRetainedPointerContent(string type, BodyScope scope)
	{
		// Only infer content for aggregates whose physical local value is known.
		// Opaque foreign instances and expanded/raw pointer transports keep their address facts.
		return TryParseTypeShape(type, out TypeShape shape) && shape.Kind == TypeShapeKind.Named
			&& TryGetTypeDefinitionByResolvedName(type, out TypeDefinition? definition)
			&& definition is StructDefinition or ClassDefinition { Extern: null }
			&& IsLifetimePointerBearingResolvedType(type, scope);
	}

	bool TryGetArgumentLifetimeFact(ArgumentExpression argument, out LifetimeFact fact)
	{
		return TryParseLifetimeFact(GetExpressionLifetimeFact(argument.Value), out fact)
			|| TryParseLifetimeFact(argument.ValueLifetimeFact, out fact)
			|| TryParseLifetimeFact(argument.Target?.ValueLifetimeFact, out fact);
	}

	static bool TryGetGenericOutInputLifetime(ParameterDefinition parameter, LifetimeCallContext context, Dictionary<string, string>? substitutions, out LifetimeFact fact)
	{
		fact = new LifetimeFact("", [], "");
		string parameterType = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? ErrorType;
		if (!TryGetExactGenericPlaceholder(parameterType, substitutions, out string genericName))
			return false;
		if (!context.GenericInputs.TryGetValue(genericName, out List<LifetimeFact>? facts) || facts.Count == 0)
			return false;

		fact = CombineScopedInputs(facts, "out generic");
		return true;
	}

	static bool TryGetExactGenericPlaceholder(string type, Dictionary<string, string>? substitutions, out string genericName)
	{
		genericName = "";
		if (substitutions is null || substitutions.Count == 0)
			return false;

		string normalized = StripTopLevelValueQualifiers(StripLifetimeQualifiers(type)).Trim();
		if (!substitutions.ContainsKey(normalized))
			return false;

		genericName = normalized;
		return true;
	}

	bool IsReceiverScopedInput(FunctionDefinition function)
	{
		if (!IsReceiverBearingDeclaration(function))
			return false;
		if (function.ReceiverLifetimeBinding is string receiverLifetime
			&& TryParseLifetimeFact(receiverLifetime, out LifetimeFact receiverFact))
			return receiverFact.Kind == "scoped";
		return true;
	}

	LifetimeFact? GetReceiverLifetimeFact(Expression? callTarget, BodyScope scope)
	{
		Expression? receiver = callTarget switch
		{
			MemberExpression member => member.Target,
			MemberReferenceExpression member => member.Target,
			_ => null
		};
		return receiver is not null && TryParseLifetimeFact(GetBoundMethodLifetimeFact(receiver, scope), out LifetimeFact fact) ? fact : null;
	}

	static bool LocalLifetimeOutlives(LifetimeFact actual, LifetimeFact required, BodyScope scope)
	{
		if (actual.Kind != "scoped" || required.Kind != "scoped" || actual.Anchors.Count == 0 || required.Anchors.Count == 0)
			return false;
		foreach (string actualAnchor in actual.Anchors)
		{
			BodyScope? actualScope = FindLocalAnchorScope(actualAnchor, scope);
			if (actualScope is null)
				return false;
			foreach (string requiredAnchor in required.Anchors)
			{
				BodyScope? requiredScope = FindLocalAnchorScope(requiredAnchor, scope);
				while (requiredScope is not null && !ReferenceEquals(requiredScope, actualScope))
					requiredScope = requiredScope.Parent;
				if (requiredScope is null)
					return false;
			}
		}
		return true;
	}

	bool NonLocalLifetimeOutlivesLocal(LifetimeFact actual, LifetimeFact required, BodyScope scope)
	{
		// Parameters and other non-local inputs survive every local of this activation.
		return required.Kind == "scoped" && required.Anchors.Count > 0
			&& required.Anchors.All(anchor => IsLocalLifetimeAnchor(anchor, scope))
			&& !actual.Anchors.Any(anchor => IsLocalLifetimeAnchor(anchor, scope));
	}

	static BodyScope? FindLocalAnchorScope(string anchor, BodyScope scope)
	{
		for (BodyScope? current = scope; current is not null; current = current.Parent)
			if (current.Symbols.TryGetValue(anchor, out BodySymbol symbol))
				return symbol.Node is DeclarationTarget or DeclarationStatement ? current : null;
		return null;
	}

	static List<LifetimeFact> GetRequiredArgumentLifetimeFacts(LifetimeFact template, LifetimeCallContext context)
	{
		if (template.Kind == "escaped")
			return [new LifetimeFact("escaped", [], "parameter requirement")];
		if (template.Kind != "unscoped")
			return [];
		if (template.Anchors.Count == 0)
			return [.. context.ScopedInputs];

		List<LifetimeFact> required = [];
		foreach (string anchor in template.Anchors)
		{
			if (context.Anchors.TryGetValue(anchor, out LifetimeFact? fact))
				required.Add(fact);
			else
				required.Add(new LifetimeFact("unknown", [], "missing anchor"));
		}
		return required;
	}

	static LifetimeFact? SubstituteLifetimeTemplate(LifetimeFact template, LifetimeCallContext context, string source)
	{
		// Results and out values use retained content for unscoped relations;
		// scoped relations and input retention checks still use storage addresses.
		if (template.Kind == "unscoped")
			context = context with { Anchors = context.RetainedAnchors, ScopedInputs = context.RetainedInputs };
		else if (template.Kind == "scoped")
			source += ":address relation";
		return template.Kind switch
		{
			"escaped" => new LifetimeFact("escaped", [], source),
			"scoped" or "unscoped" => SubstituteAnchoredTemplate(template, context, source),
			"unknown" => new LifetimeFact("unknown", [], source),
			_ => null
		};
	}

	static LifetimeFact SubstituteAnchoredTemplate(LifetimeFact template, LifetimeCallContext context, string source)
	{
		if (template.Anchors.Count > 0)
			return CombineAnchorFacts(template.Anchors, context, source);
		return CombineScopedInputs(context.ScopedInputs, source);
	}

	static LifetimeFact CombineAnchorFacts(IReadOnlyList<string> anchors, LifetimeCallContext context, string source)
	{
		List<LifetimeFact> facts = [];
		foreach (string anchor in anchors)
		{
			if (!context.Anchors.TryGetValue(anchor, out LifetimeFact? fact))
				return new LifetimeFact("unknown", [], source);
			facts.Add(fact);
		}
		if (facts.Count == 1)
			return NormalizeSubstitutedFact(facts[0], source);
		return CombineScopedInputs(facts, source);
	}

	static LifetimeFact CombineScopedInputs(IReadOnlyList<LifetimeFact> facts, string source)
	{
		List<LifetimeFact> useful = facts.Where(fact => fact.Kind != "null" && fact.Kind != "default").ToList();
		if (useful.Count == 0)
			return new LifetimeFact("unscoped", [], source);
		if (useful.All(fact => fact.Kind == "escaped"))
			return new LifetimeFact("escaped", [], source);
		if (useful.Count == 1)
			return NormalizeSubstitutedFact(useful[0], source);

		List<string> localAnchors = useful
			.Where(fact => fact.Kind == "scoped" && fact.Anchors.Count == 1)
			.Select(fact => fact.Anchors[0])
			.Distinct(StringComparer.Ordinal)
			.ToList();
		if (localAnchors.Count == 1)
			return new LifetimeFact("scoped", [localAnchors[0]], source);
		return new LifetimeFact("unknown", [], source);
	}

	static LifetimeFact NormalizeSubstitutedFact(LifetimeFact fact, string source)
	{
		return fact.Kind switch
		{
			"escaped" => new LifetimeFact("escaped", [], source),
			// A value that points into something an anchor retains outlives that anchor too.
			"unscoped" => new LifetimeFact("unscoped", fact.Anchors, source),
			"scoped" => new LifetimeFact("scoped", fact.Anchors, source),
			"unknown" => new LifetimeFact("unknown", [], source),
			_ => new LifetimeFact("unknown", [], source)
		};
	}

	static bool ValueOutlivesFact(LifetimeFact actual, LifetimeFact required)
	{
		if (actual.Kind is "escaped" or "unknown" or "null" or "default" || required.Kind == "unknown")
			return true;
		if (required.Kind == "escaped")
			return actual.Kind == "escaped";
		if (required.Anchors.Count == 0)
			return actual.Kind is "escaped" or "unscoped";
		if (actual.Kind == "unscoped" && actual.Anchors.Count == 0)
			return true;
		foreach (string anchor in required.Anchors)
		{
			if (actual.Anchors.Contains(anchor))
				continue;
			return false;
		}
		return actual.Kind is "scoped" or "unscoped";
	}

	static string FormatLifetimeRequirement(LifetimeFact template)
	{
		if (template.Anchors.Count == 0)
			return template.Kind;
		return $"{template.Kind}({string.Join(", ", template.Anchors)})";
	}

	bool TryGetEscapedOutTarget(ArgumentExpression argument, out Expression? targetExpression)
	{
		targetExpression = argument.Value;
		if (targetExpression is null)
			return false;
		return TryGetEscapedStorageTarget(targetExpression);
	}

	void UpdateAssignmentLifetimeFact(Expression? target, string? valueFact)
	{
		if (valueFact is null || target is null)
			return;

		UpdateAggregateComponentLifetimeFact(target, valueFact);
		// An instance field is declared once but stored per instance, so its value fact belongs to the
		// aggregate (updated above), never to the shared field definition.
		if (TryGetStorageNode(target, out BindableNode? storage) && storage is not null
			&& storage is not FieldDefinition { Modifier: not FieldModifier.Static })
			storage.ValueLifetimeFact = valueFact;
	}

	void UpdateAggregateComponentLifetimeFact(Expression target, string valueFact)
	{
		if (expressionRewrites.TryGetValue(target, out Expression? rewritten) && !ReferenceEquals(rewritten, target))
		{
			UpdateAggregateComponentLifetimeFact(rewritten, valueFact);
			return;
		}

		if (!TryParseLifetimeFact(valueFact, out LifetimeFact parsedValueFact)
			|| parsedValueFact.Kind is "escaped" or "unknown" or "default" or "null")
			return;

		if (!TryGetAggregateComponentLifetimeTarget(target, out Expression? aggregate, out string? componentType) || aggregate is null)
			return;
		if (!IsPointerBearingResolvedType(componentType))
			return;
		if (TryGetStorageNode(aggregate, out BindableNode? aggregateStorage) && aggregateStorage is not null)
			aggregateStorage.ValueLifetimeFact = valueFact;
	}

	bool TryGetAggregateComponentLifetimeTarget(Expression target, out Expression? aggregate, out string? componentType)
	{
		aggregate = null;
		componentType = null;
		if (target is MemberReferenceExpression { Target: Expression referenceTarget, Member: ParameterDefinition } member)
		{
			aggregate = referenceTarget;
			componentType = member.ResolvedType ?? member.Member?.ResolvedType;
			return true;
		}

		if (target is MemberReferenceExpression { Target: Expression fieldTarget, Member: FieldDefinition } fieldMember)
		{
			aggregate = fieldTarget;
			componentType = fieldMember.ResolvedType ?? fieldMember.Member?.ResolvedType;
			return true;
		}

		if (target is MemberExpression { Target: Expression memberTarget } sourceMember
			&& TryGetParamsComponentShape(null, memberTarget.ResolvedType, "value", out ParamsComponentShape shape)
			&& FindParamsComponent(shape, sourceMember.Name) is ParamsComponent component)
		{
			aggregate = memberTarget;
			componentType = component.Type;
			return true;
		}

		return false;
	}

	bool TryGetStorageNode(Expression target, out BindableNode? storage)
	{
		if (expressionRewrites.TryGetValue(target, out Expression? rewritten) && !ReferenceEquals(rewritten, target))
			return TryGetStorageNode(rewritten, out storage);

		switch (target)
		{
			case VariableReferenceExpression variable:
				storage = variable.Variable;
				return storage is not null;

			case MemberReferenceExpression { Member: not null } member:
				storage = member.Member;
				return true;

			case ParenthesizedExpression parenthesized when parenthesized.Expression is not null:
				return TryGetStorageNode(parenthesized.Expression, out storage);

			default:
				storage = null;
				return false;
		}
	}

	void CheckLifetimeAssignment(Expression? target, Expression? value, SyntaxNode? syntax, BodyScope scope, string context)
	{
		if (target is null || value is null)
			return;
		if (value is LambdaExpression)
			return;
		if (IsGeneratedShadowLifecycleRewrite())
			return;
		if (ReferencesGeneratedShadowStorage(target) || ReferencesGeneratedShadowStorage(value))
			return;
		if (IsGeneratedShadowDataExpression(target) || IsGeneratedShadowDataExpression(value))
			return;
		if (!IsLifetimePointerBearingResolvedType(value.ResolvedType, scope))
			return;

		string? valueFactText = GetExpressionLifetimeFact(value);
		if (!TryParseLifetimeFact(valueFactText, out LifetimeFact valueFact))
			return;
		if (IsInsideGenericBody(scope)
			&& FindBodyGenericParameter(scope, StripTopLevelValueQualifiers(value.ResolvedType ?? "")) is not null
			&& !IsInParameterTransportFact(valueFact, scope))
			return;

		if (valueFact.Kind == "escaped")
			return;
		if (valueFact.Kind == "unknown")
			return;
		if (valueFact.Kind is "default" or "null")
			return;
		CheckLocalStorageLifetime(target, valueFact, syntax, scope, context);

		if (TryGetEscapedStorageTarget(target))
		{
			if (!HasFailedEscapedTypeConversion(value, target.ResolvedType ?? ErrorType))
				Report(GetRange(syntax), $"{context} cannot store a non-escaped pointer-bearing value in escaped storage.");
			return;
		}
		if (CheckOutParameterFieldLifetime(target, value, valueFact, syntax, scope))
			return;

		if (TryGetReceiverAnchoredStorageTarget(target, out string? anchor))
		{
			if (IsLocalLifetimeAnchor(anchor, scope))
				return;
			// A lifted local can retain a value already tied to the same iterator
			// state. Activation-local stack storage still cannot enter that state.
			if (target is MemberReferenceExpression { Target: ThisExpression, Member: FieldDefinition field }
				&& scope.ContainingType?.GeneratedInfo is { Category: GeneratedDeclarationCategory.Iterator }
				&& iteratorLiftedLocalFields.Contains(field)
				&& valueFact.Kind is "scoped" or "unscoped"
				&& valueFact.Anchors.Count > 0 && valueFact.Anchors.All(valueAnchor => valueAnchor == anchor)
				&& !IsStackAllocLifetimeFact(valueFact))
				return;
			if (!ValueOutlivesAnchor(valueFact, anchor))
			{
				string detail = valueFact.Source.Contains(":retained element", StringComparison.Ordinal) && valueFact.Anchors.Count == 1
					? $" The element is retained by '{valueFact.Anchors[0]}', which is not related to '{anchor}'. Declare the source `unscoped({anchor})`, or copy the element's data."
					: "";
				Report(GetRange(syntax), $"{context} cannot store a scoped pointer-bearing value in storage tied to '{anchor}'.{detail}");
			}
		}
	}

	bool CheckOutParameterFieldLifetime(Expression target, Expression value, LifetimeFact valueFact, SyntaxNode? syntax, BodyScope scope)
	{
		ParameterDefinition? FindOutRoot(Expression? expression, bool field = false)
		{
			if (expression is null)
				return null;
			if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(expression, rewritten))
				return FindOutRoot(rewritten, field);
			return expression switch
			{
				VariableReferenceExpression { Variable: ParameterDefinition { Modifier: ParameterModifier.Out } parameter } when field => parameter,
				NamedExpression { Qualifiers.Count: 0 } named when field && scope.TryLookup(named.Name, out BodySymbol symbol)
					&& symbol.Node is ParameterDefinition { Modifier: ParameterModifier.Out } parameter => parameter,
				MemberReferenceExpression { Member: FieldDefinition { Modifier: not FieldModifier.Static }, Target: Expression owner }
					when TryGetPointerElementType(owner.ResolvedType) is null => FindOutRoot(owner, true),
				ParenthesizedExpression parenthesized => FindOutRoot(parenthesized.Expression, field),
				IndexExpression index when TryGetFixedArrayShape(index.Target?.ResolvedType, out _, out _) => FindOutRoot(index.Target, true),
				_ => null
			};
		}
		if (FindOutRoot(target) is not ParameterDefinition parameter)
			return false;
		LifetimeFact required = GetOutParameterLifetimeTemplate(scope.CurrentFunction, parameter, parameter.ResolvedType ?? ErrorType);
		LifetimeFact actual = valueFact;
		bool receiverStorage = PointsIntoReceiverMemory(value) is not null;
		if (actual.Kind == "scoped" && actual.Anchors.Count == 0 && (actual.Source.Contains("default receiver", StringComparison.Ordinal) || receiverStorage))
			actual = actual with { Anchors = ["this"] };
		bool addressRelation = required.Kind == "unscoped" && actual.Source.Contains(":address relation", StringComparison.Ordinal);
		bool pointsIntoReceiver = required.Kind == "unscoped" && required.Anchors.Contains("this") && receiverStorage;
		bool satisfies = ValueOutlivesFact(actual, required);
		if (required is { Kind: "unscoped", Anchors.Count: 0 } && actual.Kind == "scoped")
			satisfies = actual.Anchors.Count > 0 && actual.Anchors.All(anchor => IsParameterLifetimeAnchor(anchor, scope));
		if (!satisfies || pointsIntoReceiver || addressRelation)
		{
			string source = actual.Anchors.FirstOrDefault() ?? "the source";
			string annotation = pointsIntoReceiver || addressRelation ? $"scoped({source})" : $"unscoped({source})";
			string remedy = source == "this" || IsParameterLifetimeAnchor(source, scope)
				? $"Declare it `out {annotation} {StripLifetimeQualifiers(parameter.ResolvedType ?? ErrorType)} {parameter.Name}`, or store data that satisfies its declared relation."
				: "Use storage that outlives the output, or copy the data.";
			Report(GetRange(syntax), $"Cannot store a value tied to '{source}' in '{SpannedExpressionText(target)}': out parameter '{parameter.Name}' requires '{FormatLifetimeRequirement(required)}'. "
				+ remedy);
		}
		return true;
	}

	void CheckLocalStorageLifetime(Expression target, LifetimeFact valueFact, SyntaxNode? syntax, BodyScope scope, string context)
	{
		if (expressionRewrites.TryGetValue(target, out Expression? rewritten) && !ReferenceEquals(rewritten, target))
		{
			CheckLocalStorageLifetime(rewritten, valueFact, syntax, scope, context);
			return;
		}
		Expression? owner = target switch
		{
			MemberReferenceExpression member => member.Target,
			IndexExpression index => index.Target,
			ParenthesizedExpression parenthesized => parenthesized.Expression,
			_ => null
		};
		if (owner is not null)
		{
			CheckLocalStorageLifetime(owner, valueFact, syntax, scope, context);
			return;
		}
		if (target is not VariableReferenceExpression { Variable: DeclarationTarget storage })
			return;
		BodyScope? targetScope = null;
		for (BodyScope? candidate = scope; candidate is not null; candidate = candidate.Parent)
			if (candidate.Symbols.Values.Any(symbol => ReferenceEquals(symbol.Node, storage)))
			{
				targetScope = candidate;
				break;
			}
		if (targetScope is null)
			return;
		foreach (string anchor in valueFact.Anchors)
		{
			BodyScope? anchorScope = scope;
			while (anchorScope is not null && !anchorScope.Symbols.ContainsKey(anchor))
				anchorScope = anchorScope.Parent;
			if (anchorScope is null || ReferenceEquals(anchorScope, targetScope))
				continue;
			for (BodyScope? parent = anchorScope.Parent; parent is not null; parent = parent.Parent)
				if (ReferenceEquals(parent, targetScope))
				{
					Report(GetRange(syntax), $"{context} cannot store a value in storage that outlives its lifetime anchor '{anchor}'.");
					return;
				}
		}
	}

	bool IsInParameterTransportFact(LifetimeFact fact, BodyScope scope)
	{
		return fact.Source.Contains("in parameter address", StringComparison.Ordinal)
			&& fact.Anchors.Any(anchor => IsInParameterLifetimeAnchor(anchor, scope));
	}

	bool IsGeneratedShadowDataExpression(Expression? expression)
	{
		string? type = expression?.ResolvedType;
		if (string.IsNullOrWhiteSpace(type))
			return false;
		if (TryGetPointerElementType(type) is string element)
			type = element;
		type = BaseTypeName(StripTopLevelValueQualifiers(type));
		if (type.EndsWith("ShadowData", StringComparison.Ordinal)
			|| type.EndsWith("_ShadowData", StringComparison.Ordinal))
			return true;
		return TryGetTypeDefinitionByResolvedName(type, out TypeDefinition? definition)
			&& definition?.GeneratedInfo is { Category: GeneratedDeclarationCategory.Lifecycle, Reason: "shadow data" };
	}

	bool IsGeneratedShadowLifecycleRewrite()
	{
		if (currentRewriteFunction?.GeneratedInfo is not { Category: GeneratedDeclarationCategory.Lifecycle } info)
			return false;
		return info.Source switch
		{
			ClassDefinition { IsShadow: true } => true,
			FunctionDefinition function => FindContainingType(function) is ClassDefinition { IsShadow: true },
			_ => false
		};
	}

	bool ReferencesGeneratedShadowStorage(Expression? expression)
	{
		return expression switch
		{
			MemberReferenceExpression { Member: FieldDefinition { GeneratedInfo.Reason: "shadow instance pointer" } } => true,
			MemberReferenceExpression member => ReferencesGeneratedShadowStorage(member.Target),
			UnaryExpression unary => ReferencesGeneratedShadowStorage(unary.Operand),
			CastExpression cast => ReferencesGeneratedShadowStorage(cast.Expression),
			ParenthesizedExpression parenthesized => ReferencesGeneratedShadowStorage(parenthesized.Expression),
			_ => false
		};
	}

	static bool IsInsideGenericBody(BodyScope scope)
	{
		return scope.CurrentFunction.GenericParameters.Count > 0
			|| scope.ContainingType?.GenericParameters.Count > 0;
	}

	void CheckLifetimeResult(Expression? value, SyntaxNode? syntax, BodyScope scope, string context)
	{
		if (!IsLifetimePointerBearingResolvedType(value?.ResolvedType, scope))
			return;
		CheckResultPointsIntoPointerInput(value, syntax, scope);

		string? valueFactText = GetExpressionLifetimeFact(value);
		if (!TryParseLifetimeFact(valueFactText, out LifetimeFact valueFact))
			return;

		if (valueFact.Kind == "escaped" || valueFact.Kind == "unscoped")
			return;

		if (IsStackAllocLifetimeFact(valueFact))
		{
			if (TryGetErasedGenericValueParameter(scope, value?.ResolvedType, out _))
				return;
			Report(GetRange(syntax), $"{context} cannot return stackalloc-backed storage.");
			return;
		}

		if (valueFact.Kind == "scoped" && valueFact.Source == "array literal")
		{
			Report(GetRange(syntax), $"{context} cannot return a view backed by a local array literal.");
			return;
		}

		string? localAnchor = valueFact.Anchors.FirstOrDefault(anchor => IsLocalLifetimeAnchor(anchor, scope));
		if (localAnchor is not null && FindScopedThisCall(value) is CallExpression scopedThisCall && SpannedExpressionText(scopedThisCall) is string callText)
			Report(GetRange(syntax), $"Cannot return the result of '{callText}'. Its declared lifetime is `scoped(this)`: it points into '{localAnchor}', "
				+ "a local that ends when this function returns. Return a copy, or take the value from storage that outlives the function.");
		else if (localAnchor is not null && FindPointerContentCall(value, scope) is { } pointerCall)
			Report(GetRange(syntax), $"Cannot return the result of '{SpannedExpressionText(pointerCall.Call)}'. It is derived from '{pointerCall.Local}', "
				+ $"which retains local storage ('{localAnchor}'). Return a copy, or build '{pointerCall.Local}' from storage that outlives this function.");
		else if (localAnchor is not null)
			Report(GetRange(syntax), $"{context} cannot return a pointer-bearing value tied to local storage '{localAnchor}'."
				+ (valueFact.Source.StartsWith("out", StringComparison.Ordinal) ? " The value came from an out argument. Return a copy, or fill it from data that outlives this function." : ""));
		string? inParameterAnchor = IsInParameterTransportFact(valueFact, scope)
			? valueFact.Anchors.FirstOrDefault(anchor => IsInParameterLifetimeAnchor(anchor, scope)) : null;
		if (inParameterAnchor is not null)
			Report(GetRange(syntax), $"{context} cannot return a pointer-bearing value tied to in-parameter transport '{inParameterAnchor}'.");
		else if (valueFact.Source.Contains("parameter address", StringComparison.Ordinal))
			Report(GetRange(syntax), $"{context} cannot return a callable tied to parameter storage.");
	}

	void CheckResultPointsIntoPointerInput(Expression? value, SyntaxNode? syntax, BodyScope scope)
	{
		for (BodyScope? enclosing = scope; enclosing is not null; enclosing = enclosing.Parent)
			if (enclosing.Lambda is not null)
				return;
		LifetimeFact template = GetReturnLifetimeTemplate(scope.CurrentFunction, value?.ResolvedType ?? ErrorType);
		if (template.Kind != "unscoped" || scope.CurrentFunction.GeneratedInfo is not null)
			return;
		ParameterDefinition? FindStorageRoot(Expression? expression)
		{
			if (expression is null)
				return null;
			if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(expression, rewritten))
				return FindStorageRoot(rewritten);
			return expression switch
			{
				VariableReferenceExpression { Variable: ParameterDefinition parameter } when TryGetPointerElementType(parameter.ResolvedType) is not null => parameter,
				NamedExpression { Qualifiers.Count: 0 } named when scope.TryLookup(named.Name, out BodySymbol symbol)
					&& symbol.Node is ParameterDefinition parameter && TryGetPointerElementType(parameter.ResolvedType) is not null => parameter,
				MemberReferenceExpression { Member: FieldDefinition { Modifier: not FieldModifier.Static }, Target: Expression owner }
					when owner is NamedExpression or VariableReferenceExpression { Variable: ParameterDefinition } || TryGetPointerElementType(owner.ResolvedType) is null => FindStorageRoot(owner),
				ParenthesizedExpression parenthesized => FindStorageRoot(parenthesized.Expression),
				UnaryExpression { Operator: UnaryOperator.PointerDereference } dereference => FindStorageRoot(dereference.Operand),
				_ => null
			};
		}
		ParameterDefinition? FindAddress(Expression? expression)
		{
			if (expression is null)
				return null;
			if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(expression, rewritten))
				return FindAddress(rewritten);
			return expression switch
			{
				// Erased-pointer expansion also creates &*value. Only source-written
				// addresses borrow pointee storage; ABI transport preserves its input fact.
				UnaryExpression { Operator: UnaryOperator.AddressOf, Operand: not (NamedExpression or VariableReferenceExpression), SourceSyntax: UnaryPrefixSyntax prefix } address
					when prefix.OperatorOrKeyword?.Value == "&" => FindStorageRoot(address.Operand),
				IndexExpression index when TryGetArrayElementType(index.ResolvedType) is not null && TryGetFixedArrayShape(index.Target?.ResolvedType, out _, out _) => FindStorageRoot(index.Target),
				InitializerExpression initializer => initializer.Items.Select(item => FindAddress(item.Expression)).FirstOrDefault(parameter => parameter is not null),
				ParenthesizedExpression parenthesized => FindAddress(parenthesized.Expression),
				CastExpression { LifetimeCastKind: null } cast => FindAddress(cast.Expression),
				_ => null
			};
		}
		ParameterDefinition? input = FindAddress(value);
		if (input is null && TryGetPointerElementType(value?.ResolvedType) is not null
			&& TryParseLifetimeFact(GetExpressionLifetimeFact(value), out LifetimeFact fact) && fact.Kind == "scoped")
			input = scope.CurrentFunction.Parameters.FirstOrDefault(parameter => fact.Anchors.Contains(parameter.Name)
				&& TryGetPointerElementType(parameter.ResolvedType) is string element
				&& (fact.Source.Contains(":address relation", StringComparison.Ordinal)
					|| !fact.Source.Contains(":member", StringComparison.Ordinal)
						&& StripLifetimeQualifiers(value?.ResolvedType ?? "") == StripLifetimeQualifiers(parameter.ResolvedType ?? "")
						&& HasRetainedPointerContent(element, scope)));
		if (input is not null
			&& !IsEscapedStorage(input) && (template.Anchors.Count == 0 || template.Anchors.Contains(input.Name)))
			Report(GetRange(syntax), $"Result points into pointer parameter '{input.Name}', rather than data it retains. Declare the result `scoped({input.Name})`, or return retained data or a copy.");
	}

	(CallExpression Call, string Local)? FindPointerContentCall(Expression? value, BodyScope scope)
	{
		if (value is null)
			return null;
		if (expressionRewrites.TryGetValue(value, out Expression? rewritten) && !ReferenceEquals(value, rewritten))
			return FindPointerContentCall(rewritten, scope);
		if (value is CallExpression call && callTargets.TryGetValue(call, out FunctionDefinition? function)
			&& GetReturnLifetimeTemplate(function, call.ResolvedType ?? ErrorType).Kind == "unscoped")
			foreach (ArgumentExpression argument in call.Arguments)
				if (TryGetArgumentLifetimeFact(argument, out LifetimeFact address)
					&& GetPointerArgumentContentFact(argument, address, scope) is not null)
					return (call, address.Anchors[0]);
		if (value is InitializerExpression initializer)
			foreach (InitializerItem item in initializer.Items)
				if (FindPointerContentCall(item.Expression, scope) is { } found)
					return found;
		return value is ParenthesizedExpression parenthesized ? FindPointerContentCall(parenthesized.Expression, scope) : null;
	}

	// `this` and `classtype*` results name the receiver itself, so they keep the receiver's own lifetime.
	static bool ReturnsReceiverItself(FunctionDefinition function)
	{
		TypeReference? type = function.ReturnType;
		while (type is PointerTypeReference pointer)
			type = pointer.ElementType;
		return type is ThisTypeReference or ClassTypeReference;
	}

	// A default method result is `unscoped(this)`: something the receiver retains. A result that points into the
	// receiver's own memory ends with the receiver, so the signature has to say `scoped(this)`.
	void CheckDefaultResultPointsIntoReceiver(Expression? value, SyntaxNode? syntax, BodyScope scope)
	{
		FunctionDefinition function = scope.CurrentFunction;
		// A lambda body returns its own result, not the enclosing method's.
		for (BodyScope? enclosing = scope; enclosing is not null; enclosing = enclosing.Parent)
			if (enclosing.Lambda is not null)
				return;
		if (value is null || function.GeneratedInfo is not null || !IsReceiverBearingDeclaration(function) || ReturnsReceiverItself(function)
			|| GetExplicitThisParameter(function) is not null)
			return;
		if (!TryParseLifetimeFact(function.ReceiverLifetimeBinding, out LifetimeFact receiver) || receiver.Kind == "escaped")
			return;
		if (!IsLifetimePointerBearingResolvedType(value.ResolvedType, scope))
			return;
		LifetimeFact template = GetReturnLifetimeTemplate(function, value.ResolvedType ?? ErrorType);
		if (template is not { Kind: "unscoped", Source: "return default" } || template.Anchors.Count != 1 || template.Anchors[0] != "this")
			return;
		if (PointsIntoReceiverMemory(value) is not Expression offending)
			return;

		string expressionText = SpannedExpressionText(offending) ?? "the result";
		string declaration = (function.ReturnType is not null ? FormatTypeReference(function.ReturnType) : "") + " " + function.Name + (GetCallableParameters(function.Parameters).Count == 0 ? "()" : "(...)");
		Report(GetRange(syntax), $"Method '{function.Name}' returns `{expressionText}`, which points into the receiver's own memory, but its result has the default lifetime `unscoped(this)`. "
			+ "The default means 'something the receiver retains'. Memory inside the receiver ends when the receiver does. "
			+ $"Declare the result `scoped(this)`: `scoped(this) {declaration.Trim()}`.");
	}

	// A unary or index expression's own syntax covers only its operator, so the text spans its operands too.
	static string? SpannedExpressionText(Expression expression)
	{
		TokenRange? span = null;
		void Include(Expression? part)
		{
			if (part is null)
				return;
			if (GetFullSourceRange(part.SourceSyntax) is TokenRange range)
			{
				if (span is not TokenRange current)
					span = range;
				else if (ReferenceEquals(current.Sequence, range.Sequence))
				{
					int start = Math.Min(current.Index, range.Index);
					int end = Math.Max(current.Index + current.Count, range.Index + range.Count);
					span = new TokenRange(current.Sequence, start, end - start);
				}
			}
			switch (part)
			{
				case UnaryExpression unary: Include(unary.Operand); break;
				case IndexExpression index:
					Include(index.Target);
					foreach (ArgumentExpression argument in index.Arguments)
						Include(argument.Value);
					break;
				case MemberReferenceExpression member: Include(member.Target); break;
				case MemberExpression member: Include(member.Target); break;
				case ParenthesizedExpression parenthesized: Include(parenthesized.Expression); break;
				case CastExpression cast: Include(cast.Expression); break;
				case CallExpression call:
					Include(call.Target);
					foreach (ArgumentExpression argument in call.Arguments)
						Include(argument.Value);
					break;
			}
		}
		Include(expression);
		return span is TokenRange result ? NormalizeSourceCaptureText(result) : null;
	}

	Expression? PointsIntoReceiverMemory(Expression? expression)
	{
		if (expression is null)
			return null;
		if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(rewritten, expression))
			return PointsIntoReceiverMemory(rewritten) is not null ? expression : null;

		switch (expression)
		{
			case ParenthesizedExpression parenthesized:
				return PointsIntoReceiverMemory(parenthesized.Expression) is not null ? expression : null;
			case CastExpression { LifetimeCastKind: null } cast:
				return PointsIntoReceiverMemory(cast.Expression) is not null ? expression : null;
			case ConditionalExpression conditional:
				return PointsIntoReceiverMemory(conditional.WhenTrue) ?? PointsIntoReceiverMemory(conditional.WhenFalse);
			case ThisExpression:
				return expression;
			case UnaryExpression { Operator: UnaryOperator.AddressOf } address:
				return IsInlineReceiverStorage(address.Operand) ? expression : null;
			case IndexExpression index when TryParseTypeShape(index.ResolvedType, out TypeShape shape) && shape.Kind == TypeShapeKind.Array:
				return TryGetFixedArrayShape(index.Target?.ResolvedType, out _, out _) && IsInlineReceiverStorage(index.Target) ? expression : null;
			case CallExpression call when callTargets.TryGetValue(call, out FunctionDefinition? callee)
				&& GetReturnLifetimeTemplate(callee, call.ResolvedType ?? ErrorType) is { Kind: "scoped", Anchors: [ "this" ] }:
			{
				Expression? receiver = call.Target switch
				{
					MemberReferenceExpression member => member.Target,
					MemberExpression member => member.Target,
					_ => null
				};
				return receiver is ThisExpression || IsInlineReceiverStorage(receiver) ? expression : null;
			}
			default:
				return null;
		}
	}

	// True when the expression names storage that lives inside the receiver itself, not storage the receiver points at.
	bool IsInlineReceiverStorage(Expression? expression)
	{
		if (expression is null)
			return false;
		if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(rewritten, expression))
			return IsInlineReceiverStorage(rewritten);

		return expression switch
		{
			ParenthesizedExpression parenthesized => IsInlineReceiverStorage(parenthesized.Expression),
			MemberReferenceExpression { Member: FieldDefinition { Modifier: not FieldModifier.Static }, Target: ThisExpression } => true,
			MemberReferenceExpression { Member: FieldDefinition { Modifier: not FieldModifier.Static }, Target: Expression target }
				=> TryGetPointerElementType(target.ResolvedType) is null && IsInlineReceiverStorage(target),
			IndexExpression { Target: Expression target } index when !(TryParseTypeShape(index.ResolvedType, out TypeShape shape) && shape.Kind == TypeShapeKind.Array)
				=> TryGetFixedArrayShape(target.ResolvedType, out _, out _) && IsInlineReceiverStorage(target),
			UnaryExpression { Operator: UnaryOperator.PointerDereference, Operand: ThisExpression } => true,
			_ => false
		};
	}

	// The call, directly or inside an initializer, whose declared `scoped(this)` result ties the value to its receiver.
	CallExpression? FindScopedThisCall(Expression? expression)
	{
		if (expression is null)
			return null;
		if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(rewritten, expression))
			return FindScopedThisCall(rewritten);
		return expression switch
		{
			ParenthesizedExpression parenthesized => FindScopedThisCall(parenthesized.Expression),
			CallExpression call when callTargets.TryGetValue(call, out FunctionDefinition? callee)
				&& GetReturnLifetimeTemplate(callee, call.ResolvedType ?? ErrorType) is { Kind: "scoped", Anchors: ["this"] } => call,
			InitializerExpression initializer => initializer.Items.Select(item => FindScopedThisCall(item.Expression)).FirstOrDefault(found => found is not null),
			_ => null
		};
	}

	void CheckLifetimeYield(Expression? value, SyntaxNode? syntax, BodyScope scope)
	{
		if (!IsLifetimePointerBearingResolvedType(value?.ResolvedType, scope))
			return;

		string? valueFactText = GetExpressionLifetimeFact(value);
		if (!TryParseLifetimeFact(valueFactText, out LifetimeFact valueFact))
			return;

		if (valueFact.Kind is "escaped" or "unscoped" or "unknown")
			return;

		if (IsStackAllocLifetimeFact(valueFact))
		{
			Report(GetRange(syntax), "Yield expression cannot yield stackalloc-backed storage.");
			return;
		}

		Report(GetRange(syntax), "Yield expression cannot yield a pointer-bearing value that does not outlive the iterator frame.");
	}

	// Only an escaped pointer may be freed. Stackalloc cleanup deletes skip this check and the free.
	void CheckLifetimeDeleteAgainstFree(Expression? expression, SyntaxNode? syntax, BodyScope scope)
	{
		if (!IsPointerBearingResolvedType(expression?.ResolvedType) || expression is ThisExpression)
			return;

		if (TryParseLifetimeFact(GetExpressionLifetimeFact(expression), out LifetimeFact fact) && fact.Kind is "escaped" or "null" or "default")
			return;
		Report(GetRange(syntax), "Delete target must be an escaped pointer.");
	}

	// Constructors and destructors reach interface implementations only through a vtableof capability. The destroy
	// slot frees the object, so the target must be provably escaped rather than merely unknown.
	bool ValidateCapabilityDeleteTarget(Expression? expression, string deleteType, BodyScope scope)
	{
		if (expression is WithinExpression { Expression: not null } within)
		{
			expression = within.Expression;
			deleteType = expression.ResolvedType ?? deleteType;
		}
		if (expression is null || TryGetPointerElementType(deleteType) is not string elementType)
			return false;

		SyntaxNode? syntax = expression.SourceSyntax;
		if (TryGetInterfacePointerDefinition(deleteType, out InterfaceDefinition? interfaceDefinition) && interfaceDefinition is not null)
		{
			Report(GetRange(syntax), $"Cannot delete interface pointer '{deleteType}'; interface destructors are available only through a generic 'vtableof' capability.");
			return true;
		}

		if (FindWitnessDestructor(scope.CurrentFunction, elementType, scope.ContainingType) is null)
			return false;
		if (!TryParseLifetimeFact(GetExpressionLifetimeFact(expression), out LifetimeFact fact) || fact.Kind != "escaped")
			Report(GetRange(syntax), "Delete through a 'vtableof' destructor requires an escaped target; the destroy slot frees the object and may not receive scoped, stack, or unproven storage.");
		return true;
	}

	bool IsStackAllocBackedExpression(Expression? expression)
	{
		return TryParseLifetimeFact(GetExpressionLifetimeFact(expression), out LifetimeFact fact)
			&& IsStackAllocLifetimeFact(fact);
	}

	static bool IsStackAllocLifetimeFact(LifetimeFact fact)
	{
		return fact.Source.Contains("stackalloc", StringComparison.Ordinal);
	}

	bool TryGetEscapedStorageTarget(Expression target)
	{
		if (expressionRewrites.TryGetValue(target, out Expression? rewritten) && !ReferenceEquals(rewritten, target))
			return TryGetEscapedStorageTarget(rewritten);

		return target switch
		{
			VariableReferenceExpression { Variable: VariableDefinition } => true,
			VariableReferenceExpression { Variable: FieldDefinition { Modifier: FieldModifier.Static } } => true,
			MemberReferenceExpression { Member: FieldDefinition { Modifier: FieldModifier.Static } } => true,
			MemberReferenceExpression { Member: FieldDefinition field } when IsEscapedStorage(field) => true,
			_ => false
		};
	}

	static bool IsEscapedStorage(BindableNode? storage)
	{
		return storage?.SlotLifetimeFact?.StartsWith("escaped", StringComparison.Ordinal) == true
			|| storage?.ValueLifetimeFact?.StartsWith("escaped", StringComparison.Ordinal) == true;
	}

	bool TryGetReceiverAnchoredStorageTarget(Expression target, out string anchor)
	{
		anchor = "";
		if (expressionRewrites.TryGetValue(target, out Expression? rewritten) && !ReferenceEquals(rewritten, target))
			return TryGetReceiverAnchoredStorageTarget(rewritten, out anchor);

		if (target is MemberReferenceExpression { Target: not null, Member: FieldDefinition { Modifier: not FieldModifier.Static } } member)
		{
			if (TryParseLifetimeFact(GetStorageLifetimeFact(member.Target) ?? GetExpressionLifetimeFact(member.Target), out LifetimeFact targetFact)
				&& targetFact.Anchors.Count == 1)
			{
				anchor = targetFact.Anchors[0];
				return true;
			}
			anchor = "this";
			return true;
		}

		if (target is IndexExpression index && GetExpressionLifetimeFact(index.Target) is string targetFactText
			&& TryParseLifetimeFact(targetFactText, out LifetimeFact indexedTargetFact)
			&& indexedTargetFact.Anchors.Count == 1)
		{
			anchor = indexedTargetFact.Anchors[0];
			return true;
		}

		return false;
	}

	string? GetStorageLifetimeFact(Expression? expression)
	{
		if (expression is null)
			return null;
		if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(rewritten, expression))
			return GetStorageLifetimeFact(rewritten);
		if (TryGetStorageNode(expression, out BindableNode? storage) && storage is not null)
			return storage.SlotLifetimeFact ?? storage.ValueLifetimeFact;
		return expression switch
		{
			ParenthesizedExpression parenthesized => GetStorageLifetimeFact(parenthesized.Expression),
			MemberReferenceExpression { Target: not null } member => GetStorageLifetimeFact(member.Target),
			MemberExpression { Target: not null } member => GetStorageLifetimeFact(member.Target),
			IndexExpression { Target: not null } index => GetStorageLifetimeFact(index.Target),
			_ => null
		};
	}

	static bool ValueOutlivesAnchor(LifetimeFact valueFact, string anchor)
	{
		return valueFact.Kind == "escaped"
			|| valueFact.Kind == "unscoped" && (valueFact.Anchors.Count == 0 || valueFact.Anchors.Contains(anchor))
			|| valueFact.Kind == "scoped" && valueFact.Anchors.Count == 1 && valueFact.Anchors[0] == anchor
				&& valueFact.Source.Contains(":retained element", StringComparison.Ordinal);
	}

	bool IsLocalLifetimeAnchor(string anchor, BodyScope scope)
	{
		if (!scope.TryLookup(anchor, out BodySymbol symbol))
			return false;

		return symbol.Node is DeclarationStatement or DeclarationTarget or CatchStatement;
	}

	bool IsParameterLifetimeAnchor(string anchor, BodyScope scope)
	{
		return anchor != "this"
			&& scope.TryLookup(anchor, out BodySymbol symbol)
			&& symbol.Node is ParameterDefinition;
	}

	bool IsInParameterLifetimeAnchor(string anchor, BodyScope scope)
	{
		if (!scope.TryLookup(anchor, out BodySymbol symbol))
			return false;

		return symbol.Node is ParameterDefinition { Modifier: ParameterModifier.In };
	}

	static string? GetStorageLifetimeAnchor(BindableNode? storage)
	{
		return storage switch
		{
			DeclarationStatement { Target.Names.Count: 1 } declaration => declaration.Target.Names[0],
			DeclarationTarget { Names.Count: 1 } target => target.Names[0],
			VariableDefinition variable => variable.Name,
			FieldDefinition field => field.Name,
			ParameterDefinition parameter => parameter.Name,
			_ => null
		};
	}
}
