using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	sealed record LifetimeFact(string Kind, IReadOnlyList<string> Anchors, string Source);
	sealed record LifetimeCallContext(Dictionary<string, LifetimeFact> Anchors, List<LifetimeFact> ScopedInputs, LifetimeFact? Receiver, Dictionary<string, List<LifetimeFact>> GenericInputs);

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
		if (!IsLifetimeTrackedType(type, resolvedType, isFixedStorage, typeScope))
			return null;

		return MakeLifetimeFact("scoped", string.IsNullOrWhiteSpace(name) ? null : name, "slot");
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

		string baseName = BaseTypeName(StripTopLevelValueQualifiers(resolvedType ?? ""));
		return !string.IsNullOrWhiteSpace(baseName)
			&& typeScope.GenericParameters.TryGetValue(baseName, out GenericParameter? parameter)
			&& parameter.Constraint is AnyTypeReference;
	}

	static bool IsPointerBearingResolvedType(string? type)
	{
		if (string.IsNullOrWhiteSpace(type) || type == ErrorType || type == TargetType)
			return false;

		string raw = type.Trim();
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

	static bool IsPointerBearingResolvedShape(TypeShape shape)
	{
		return shape.Kind switch
		{
			TypeShapeKind.Pointer => true,
			TypeShapeKind.Array => true,
			TypeShapeKind.FixedArray => shape.Element is not null && IsPointerBearingResolvedShape(shape.Element),
			TypeShapeKind.Optional => shape.Element is not null && IsPointerBearingResolvedShape(shape.Element),
			TypeShapeKind.Named => shape.Name is "string" or "astring" or "wstring"
				|| shape.Name.StartsWith("delegate ", System.StringComparison.Ordinal)
				|| shape.Name.StartsWith("iter ", System.StringComparison.Ordinal),
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

		string fact = parameter.LifetimeBinding ?? MakeLifetimeFact("scoped", parameter.Name, "parameter");
		parameter.SlotLifetimeFact = fact;
		parameter.ValueLifetimeFact = fact;
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
			&& (IsEscapedField(definition) || containingType is ClassDefinition { IsEscaped: true });
		string? fact = escapedInstanceField
			? MakeLifetimeFact("escaped", definition.Name, "field")
			: definition.Modifier == FieldModifier.Static
			? CreateGlobalSlotLifetimeFact(definition.Name, definition.Type, definition.ResolvedType, scope)
			: CreateDeclarationSlotLifetimeFact(definition.Name, definition.Type, definition.ResolvedType, isFixedStorage: false, scope);
		definition.SlotLifetimeFact = fact;
		definition.ValueLifetimeFact ??= fact;
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
		declaration.Target.SlotLifetimeFact = slotFact;
		declaration.Target.ValueLifetimeFact = declaration.IsFixedStorage ? slotFact : GetExpressionLifetimeFact(declaration.InitialValue) ?? slotFact;
		declaration.SlotLifetimeFact = slotFact;
		declaration.ValueLifetimeFact = declaration.Target.ValueLifetimeFact;
	}

	void ApplyExpressionLifetimeFact(Expression expression, string resolvedType, BodyScope scope, AnalysisScope typeScope)
	{
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
			CastExpression cast => cast.LifetimeBinding ?? GetExpressionLifetimeFact(cast.Expression),
			ConstructionExpression construction => IsLifetimePointerBearingResolvedType(resolvedType, scope) ? GetConstructionLifetimeFact(construction, resolvedType, scope) : null,
			WithinExpression within => GetWithinExpressionLifetimeFact(within, resolvedType, scope),
			FinallyDeleteExpression finallyDelete => GetExpressionLifetimeFact(finallyDelete.Expression),
			UnaryExpression unary => GetUnaryLifetimeFact(unary, resolvedType, scope),
			IndexExpression index => GetIndexLifetimeFact(index, resolvedType),
			NamelessIndexerExpression indexer => GetExpressionLifetimeFact(indexer.Target),
			MemberExpression member => GetMemberLifetimeFact(member, resolvedType, scope),
			CallExpression call => GetCallLifetimeFact(call, resolvedType, scope),
			LambdaExpression lambda when !LambdaHasCaptures(lambda, scope.CurrentFunction, scope.ContainingType) => MakeLifetimeFact("escaped", null, "lambda"),
			LambdaExpression lambda => TryGetResolvedTypeLifetime(lambda.ResolvedType, out string lambdaLifetime)
				? MakeLifetimeFact(lambdaLifetime, null, "lambda")
				: MakeLifetimeFact("scoped", null, "lambda"),
			AssignmentExpression assignment => GetExpressionLifetimeFact(assignment.Value),
			ConditionalExpression conditional => GetExpressionLifetimeFact(conditional.WhenTrue) ?? GetExpressionLifetimeFact(conditional.WhenFalse),
			_ => null
		};

		if (fact is not null)
			expression.ValueLifetimeFact = fact;
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
			ConstructionKind.New => GetNewConstructionLifetimeFact(construction),
			ConstructionKind.Init => GetInitConstructionLifetimeFact(construction, resolvedType, scope),
			_ => null
		};
	}

	string? GetNewConstructionLifetimeFact(ConstructionExpression construction)
	{
		FunctionDefinition? malloc = FindMallocFunction(construction.SourceSyntax);
		return GetFunctionReturnLifetimeFact(malloc, "new") ?? MakeLifetimeFact("unknown", null, "new");
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
				if (!IsLifetimePointerBearingResolvedType(parameters[i].ResolvedType, scope)
					&& GetExpressionLifetimeFact(construction.Arguments[i].Value) is null)
					continue;
				if (TryParseLifetimeFact(GetExpressionLifetimeFact(construction.Arguments[i].Value), out LifetimeFact argumentFact))
					retainedFacts.Add(argumentFact);
			}
		}

		AddInitializerRetainedLifetimeFacts(construction.Initializer, resolvedType, scope, retainedFacts);
		return CombineRetainedLifetimeFacts(retainedFacts) is LifetimeFact retained
			? FormatLifetimeFact(new LifetimeFact(retained.Kind, retained.Anchors, "init"))
			: null;
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
		if (initializer is null || !typeDefinitions.TryGetValue(BaseTypeName(resolvedType), out TypeDefinition? definition))
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
			if (TryGetInParameterAnchor(unary.Operand, scope, out string inParameterAnchor))
				return MakeLifetimeFact("scoped", inParameterAnchor, "in parameter address");
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
		return targetFact is not null ? targetFact + ":element" : null;
	}

	string? GetMemberLifetimeFact(MemberExpression member, string resolvedType, BodyScope scope)
	{
		if (!IsLifetimePointerBearingResolvedType(resolvedType, scope))
			return null;
		if (expressionRewrites.TryGetValue(member, out Expression? rewritten)
			&& rewritten is MemberReferenceExpression { Member: FieldDefinition field }
			&& IsEscapedStorage(field))
			return MakeLifetimeFact("escaped", field.Name, "field");

		string? targetFact = GetExpressionLifetimeFact(member.Target);
		return targetFact is not null ? targetFact + ":member" : MakeLifetimeFact("scoped", null, "member");
	}

	string? GetCallLifetimeFact(CallExpression call, string resolvedType, BodyScope scope)
	{
		if (!IsLifetimePointerBearingResolvedType(resolvedType, scope))
			return null;

		if (callTargets.TryGetValue(call, out FunctionDefinition? function))
		{
			LifetimeFact template = GetReturnLifetimeTemplate(function, resolvedType);
			List<ParameterDefinition> callableParameters = GetCallableParameters(function.Parameters, IncludeExplicitThisArgument(call.Target, function));
			LifetimeCallContext context = BuildLifetimeCallContext(function, call.Target, callableParameters, ZipCallArguments(call.Arguments, callableParameters), substitutions: null, scope);
			return SubstituteLifetimeTemplate(template, context, "call") is LifetimeFact fact ? FormatLifetimeFact(fact) : null;
		}

		return MakeLifetimeFact("unknown", null, "call");
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
		string structuralReturnType = StripLifetimeQualifiers(returnType);
		string mutableReturnType = StripConstFromLifetimeRefinementShape(structuralReturnType);
		if (mutableReturnType == structuralReturnType)
			return returnType;

		LifetimeFact template = GetReturnLifetimeTemplate(function, returnType);
		if (template.Kind != "scoped")
			return returnType;

		List<ParameterDefinition> callableParameters = GetCallableParameters(function.Parameters, IncludeExplicitThisArgument(callTarget, function));
		if (function is not null && IncludeExplicitThisArgument(callTarget, function) && GetExplicitThisParameter(function) is null && IsInstanceFunction(function) && FindContainingType(function) is TypeDefinition containingType)
			callableParameters.Insert(0, CreateImplicitThisParameter(containingType));

		List<(ParameterDefinition Parameter, ArgumentExpression Argument)> scopedInputs = [];
		int count = Math.Min(arguments.Count, callableParameters.Count);
		for (int i = 0; i < count; i++)
		{
			ParameterDefinition parameter = callableParameters[i];
			if (parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown or ParameterModifier.Within)
				continue;
			if (template.Anchors.Count > 0 && (string.IsNullOrWhiteSpace(parameter.Name) || !template.Anchors.Contains(parameter.Name)))
				continue;
			if (template.Anchors.Count == 0 && GetParameterLifetimeTemplate(parameter).Kind != "scoped")
				continue;
			scopedInputs.Add((parameter, arguments[i]));
		}

		if (scopedInputs.Count != 1)
			return returnType;

		string actualType = scopedInputs[0].Argument.ResolvedType ?? scopedInputs[0].Argument.Value?.ResolvedType ?? ErrorType;
		if (actualType == ErrorType || actualType == TargetType || IsConstQualified(actualType))
			return returnType;
		string actualComparisonType = TryGetArrayElementType(structuralReturnType) is not null
			? actualType
			: GetLifetimeRefinementTransportType(actualType);
		if (!CanImplicitlyConvert(actualComparisonType, structuralReturnType) || !CanImplicitlyConvert(actualComparisonType, mutableReturnType))
			return returnType;

		return mutableReturnType;
	}

	static string GetLifetimeRefinementTransportType(string type)
	{
		return TryGetArrayElementType(type) is string elementType
			? AddPointer(elementType)
			: type;
	}

	static string StripConstFromLifetimeRefinementShape(string type)
	{
		if (!new TypeShapeParser(type).TryParse(out TypeShape shape))
			return StripConstFromShape(type);

		return TypeShapeParser.Format(StripConstFromLifetimeRefinementShape(shape));
	}

	static TypeShape StripConstFromLifetimeRefinementShape(TypeShape shape)
	{
		return shape with
		{
			Qualifiers = shape.Qualifiers with { IsConst = false },
			Element = shape.Element is null ? null : StripConstFromLifetimeRefinementShape(shape.Element)
		};
	}

	LifetimeFact GetReturnLifetimeTemplate(FunctionDefinition function, string resolvedType)
	{
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

		if (IsReceiverBearingDeclaration(function))
			return new LifetimeFact("unscoped", ["this"], "return default");

		return new LifetimeFact("unscoped", [], "return default");
	}

	void CheckLifetimeCallArguments(FunctionDefinition function, Expression? callTarget, List<(ArgumentExpression Argument, ParameterDefinition Parameter)> arguments, BodyScope scope, SyntaxNode? fallbackSyntax, Dictionary<string, string>? substitutions)
	{
		if (function.Modifier == FunctionModifier.Constructor || function.Name == InitNewMethodName)
			return;

		List<ParameterDefinition> callableParameters = GetCallableParameters(function.Parameters, IncludeExplicitThisArgument(callTarget, function));
		LifetimeCallContext context = BuildLifetimeCallContext(function, callTarget, callableParameters, arguments, substitutions, scope);
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

	void CheckLifetimeParameterArgument(FunctionDefinition function, ArgumentExpression argument, ParameterDefinition parameter, LifetimeCallContext context, BodyScope scope, SyntaxNode? fallbackSyntax, Dictionary<string, string>? substitutions)
	{
		string effectiveParameterType = SubstituteGenericType(parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? ErrorType, substitutions ?? []);
		if (!IsLifetimePointerBearingResolvedType(argument.ResolvedType, scope) && !IsLifetimePointerBearingResolvedType(effectiveParameterType, scope))
			return;
		if (argument.Value is VariableReferenceExpression { Variable: ParameterDefinition argumentParameter }
			&& ReferenceEquals(argumentParameter, parameter))
			return;
		if (argument.Value is NamedExpression { Qualifiers.Count: 0 } namedArgument
			&& namedArgument.Name == parameter.Name)
			return;

		if (!TryParseLifetimeFact(GetExpressionLifetimeFact(argument.Value), out LifetimeFact actualFact))
			return;
		if (actualFact.Kind == "unknown")
			return;

		LifetimeFact template = GetParameterLifetimeTemplate(parameter);
		if (function.Modifier == FunctionModifier.Constructor && template.Kind == "unscoped" && template.Anchors.Count == 0)
			return;
		List<LifetimeFact> requiredFacts = GetRequiredArgumentLifetimeFacts(template, context);
		if (requiredFacts.Count == 0)
			return;

		foreach (LifetimeFact required in requiredFacts)
		{
			if (required.Kind == "unknown")
				continue;
			if (!ValueOutlivesFact(actualFact, required))
				Report(GetRange(GetArgumentDiagnosticSyntax(argument, fallbackSyntax)), $"Argument cannot satisfy parameter lifetime '{FormatLifetimeRequirement(template)}'.");
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
		if (parameter.LifetimeBinding is string explicitLifetime
			&& TryParseLifetimeFact(explicitLifetime, out LifetimeFact explicitFact))
			return explicitFact;
		if (TryGetResolvedTypeLifetime(effectiveType, out string lifetimeKind)
			|| TryGetResolvedTypeLifetime(parameter.ResolvedType, out lifetimeKind))
			return new LifetimeFact(lifetimeKind, [], "out type");

		if (IsReceiverBearingDeclaration(function))
			return new LifetimeFact("unscoped", ["this"], "out default");

		return new LifetimeFact("unscoped", [], "out default");
	}

	LifetimeFact GetParameterLifetimeTemplate(ParameterDefinition parameter)
	{
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
		Dictionary<string, List<LifetimeFact>> genericInputs = new(StringComparer.Ordinal);
		LifetimeFact? receiverFact = GetReceiverLifetimeFact(callTarget);
		if (receiverFact is not null)
		{
			anchors["this"] = receiverFact;
			if (IsReceiverScopedInput(function))
				scopedInputs.Add(receiverFact);
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
			if (!string.IsNullOrWhiteSpace(parameter.Name))
				anchors[parameter.Name] = actualFact;
			if (GetParameterLifetimeTemplate(parameter).Kind == "scoped")
				scopedInputs.Add(actualFact);
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

		return new LifetimeCallContext(anchors, scopedInputs, receiverFact, genericInputs);
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

	LifetimeFact? GetReceiverLifetimeFact(Expression? callTarget)
	{
		Expression? receiver = callTarget switch
		{
			MemberExpression member => member.Target,
			MemberReferenceExpression member => member.Target,
			_ => null
		};
		return TryParseLifetimeFact(GetExpressionLifetimeFact(receiver), out LifetimeFact fact) ? fact : null;
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
		return template.Kind switch
		{
			"escaped" => new LifetimeFact("escaped", [], source),
			"scoped" => SubstituteScopedTemplate(template, context, source),
			"unscoped" => SubstituteUnscopedTemplate(template, context, source),
			"unknown" => new LifetimeFact("unknown", [], source),
			_ => null
		};
	}

	static LifetimeFact SubstituteScopedTemplate(LifetimeFact template, LifetimeCallContext context, string source)
	{
		if (template.Anchors.Count > 0)
			return CombineAnchorFacts(template.Anchors, context, source, preserveUnscoped: false);
		return CombineScopedInputs(context.ScopedInputs, source);
	}

	static LifetimeFact SubstituteUnscopedTemplate(LifetimeFact template, LifetimeCallContext context, string source)
	{
		if (template.Anchors.Count > 0)
			return CombineAnchorFacts(template.Anchors, context, source, preserveUnscoped: true);
		return CombineScopedInputs(context.ScopedInputs, source);
	}

	static LifetimeFact CombineAnchorFacts(IReadOnlyList<string> anchors, LifetimeCallContext context, string source, bool preserveUnscoped)
	{
		List<LifetimeFact> facts = [];
		foreach (string anchor in anchors)
		{
			if (!context.Anchors.TryGetValue(anchor, out LifetimeFact? fact))
				return new LifetimeFact("unknown", [], source);
			facts.Add(fact);
		}
		if (facts.Count == 1)
			return NormalizeSubstitutedFact(facts[0], source, preserveUnscoped);
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
			return NormalizeSubstitutedFact(useful[0], source, preserveUnscoped: false);

		List<string> localAnchors = useful
			.Where(fact => fact.Kind == "scoped" && fact.Anchors.Count == 1)
			.Select(fact => fact.Anchors[0])
			.Distinct(StringComparer.Ordinal)
			.ToList();
		if (localAnchors.Count == 1)
			return new LifetimeFact("scoped", [localAnchors[0]], source);
		return new LifetimeFact("unknown", [], source);
	}

	static LifetimeFact NormalizeSubstitutedFact(LifetimeFact fact, string source, bool preserveUnscoped)
	{
		return fact.Kind switch
		{
			"escaped" => new LifetimeFact("escaped", [], source),
			"unscoped" when preserveUnscoped => new LifetimeFact("unscoped", fact.Anchors, source),
			"unscoped" when fact.Anchors.Count == 0 => new LifetimeFact("unscoped", [], source),
			"scoped" => new LifetimeFact("scoped", fact.Anchors, source),
			"unknown" => new LifetimeFact("unknown", [], source),
			_ => new LifetimeFact("unknown", [], source)
		};
	}

	static bool ValueOutlivesFact(LifetimeFact actual, LifetimeFact required)
	{
		if (actual.Kind == "escaped" || actual.Kind == "unknown" || required.Kind == "unknown")
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
		if (TryGetStorageNode(target, out BindableNode? storage) && storage is not null)
			storage.ValueLifetimeFact = valueFact;
	}

	void UpdateAggregateComponentLifetimeFact(Expression target, string valueFact)
	{
		if (expressionRewrites.TryGetValue(target, out Expression? rewritten) && !ReferenceEquals(rewritten, target))
		{
			UpdateAggregateComponentLifetimeFact(rewritten, valueFact);
			return;
		}

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
		if (IsInsideGenericBody(scope))
			return;
		if (!IsLifetimePointerBearingResolvedType(value.ResolvedType, scope))
			return;

		string? valueFactText = GetExpressionLifetimeFact(value);
		if (!TryParseLifetimeFact(valueFactText, out LifetimeFact valueFact))
			return;

		if (valueFact.Kind == "escaped")
			return;
		if (valueFact.Kind == "unknown")
			return;

		if (TryGetEscapedStorageTarget(target))
		{
			Report(GetRange(syntax), $"{context} cannot store a non-escaped pointer-bearing value in escaped storage.");
			return;
		}

		if (TryGetReceiverAnchoredStorageTarget(target, out string? anchor))
		{
			if (!ValueOutlivesAnchor(valueFact, anchor))
				Report(GetRange(syntax), $"{context} cannot store a scoped pointer-bearing value in storage tied to '{anchor}'.");
		}
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

		string? valueFactText = GetExpressionLifetimeFact(value);
		if (!TryParseLifetimeFact(valueFactText, out LifetimeFact valueFact))
			return;

		if (valueFact.Kind == "escaped" || valueFact.Kind == "unscoped")
			return;

		string? localAnchor = valueFact.Anchors.FirstOrDefault(anchor => IsLocalLifetimeAnchor(anchor, scope));
		if (localAnchor is not null)
			Report(GetRange(syntax), $"{context} cannot return a pointer-bearing value tied to local storage '{localAnchor}'.");
		string? inParameterAnchor = valueFact.Anchors.FirstOrDefault(anchor => IsInParameterLifetimeAnchor(anchor, scope));
		if (inParameterAnchor is not null)
			Report(GetRange(syntax), $"{context} cannot return a pointer-bearing value tied to in-parameter transport '{inParameterAnchor}'.");
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

		Report(GetRange(syntax), "Yield expression cannot yield a pointer-bearing value that does not outlive the iterator frame.");
	}

	void CheckLifetimeDeleteAgainstFree(Expression? expression, SyntaxNode? syntax, BodyScope scope)
	{
		if (!IsPointerBearingResolvedType(expression?.ResolvedType))
			return;

		FunctionDefinition? free = FindFreeFunction(syntax);
		if (free is null)
			return;

		ParameterDefinition? pointerParameter = GetPatternValueParameters(free).FirstOrDefault();
		if (pointerParameter?.LifetimeBinding is not string requiredLifetime)
			return;

		if (!TryParseLifetimeFact(requiredLifetime, out LifetimeFact requiredFact))
			return;

		if (requiredFact.Kind != "escaped")
			return;

		string? factText = GetExpressionLifetimeFact(expression);
		if (!TryParseLifetimeFact(factText, out LifetimeFact actualFact))
			return;
		if (actualFact.Kind is "escaped" or "unknown")
			return;

		if (actualFact.Kind == "scoped" && actualFact.Anchors.Any(anchor => IsLocalLifetimeAnchor(anchor, scope)))
			Report(GetRange(syntax), "Delete target cannot satisfy free parameter lifetime 'escaped'.");
	}

	bool TryGetEscapedStorageTarget(Expression target)
	{
		if (expressionRewrites.TryGetValue(target, out Expression? rewritten) && !ReferenceEquals(rewritten, target))
			return TryGetEscapedStorageTarget(rewritten);

		return target switch
		{
			VariableReferenceExpression { Variable: VariableDefinition } => true,
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
			if (TryParseLifetimeFact(GetExpressionLifetimeFact(member.Target), out LifetimeFact targetFact)
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

	static bool ValueOutlivesAnchor(LifetimeFact valueFact, string anchor)
	{
		return valueFact.Kind == "escaped"
			|| valueFact.Kind == "unscoped" && (valueFact.Anchors.Count == 0 || valueFact.Anchors.Contains(anchor));
	}

	bool IsLocalLifetimeAnchor(string anchor, BodyScope scope)
	{
		if (!scope.TryLookup(anchor, out BodySymbol symbol))
			return false;

		return symbol.Node is DeclarationStatement or DeclarationTarget or CatchStatement;
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
