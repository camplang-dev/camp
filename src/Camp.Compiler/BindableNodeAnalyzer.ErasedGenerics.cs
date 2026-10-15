using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly HashSet<CallExpression> erasedCallableResults = [];
	readonly Dictionary<CallExpression, ParameterDefinition> trailingOutResults = [];
	readonly HashSet<Statement> erasedArgumentCopyBackStatements = [];

	// TypeShape is a comparison view: it omits explicit scoped annotations and
	// their anchors. Rewrite only the named core, retaining the source declarators.
	string RewriteStorageTypeName(string type, System.Func<TypeShape, string> rewrite)
	{
		TypeShapeParser parser = new(type);
		if (!parser.TryParse(out TypeShape shape) || !parser.IsEnd) return type;
		TypeShape named = shape;
		while (named.Element is not null) named = named.Element;
		if (named.Kind != TypeShapeKind.Named) return type;
		string name = rewrite(shape);
		return type[..parser.NamedTypeStart] + name + type[(parser.NamedTypeStart + named.Name.Length)..];
	}

	// Representation substitution: omitted representation becomes void<A>;
	// an explicit integer representation becomes C<A>.
	// `erasure` maps a type parameter name to "void", a carrier name, or null when nothing is erased.
	string ErasedCallableSignatureType(string type, System.Func<string, string?> erasure, bool storage = false, bool result = false)
	{
		return RewriteStorageTypeName(type, shape => Rewrite(shape, storage, result));
		string Rewrite(TypeShape node, bool reference, bool returned)
		{
			if (node.Element is not null)
				return Rewrite(node.Element, reference && node.Kind == TypeShapeKind.Optional || node.Kind is TypeShapeKind.Pointer or TypeShapeKind.Array,
					returned && node.Kind == TypeShapeKind.Optional);
			if (erasure(node.Name) is string wrapper)
			{
				if (wrapper != "void") return $"{wrapper}<{node.Name}>";
				if (reference || returned) return $"void<{node.Name}>";
			}
			if (TryParseCallableShape(node.Name, out CallableShape callable))
			{
				string returnType = ErasedCallableSignatureType(callable.ReturnType, erasure, result: true);
				List<string> parameters = callable.Parameters.Select(text => { CallableSlot slot = ParseCallableSlot(text);
					string parameterType = ErasedCallableSignatureType(slot.Type, erasure, slot.Modifier is "in" or "out");
					return slot.Modifier.Length == 0 ? parameterType : slot.Modifier + " " + parameterType; }).ToList();
				return BuildCallableType(callable.Kind, returnType, parameters, callable.Spec, callable.CallSpec);
			}
			return node.Name;
		}
	}

	static string? ErasureWrapper(GenericParameter? parameter)
	{
		if (parameter is null) return null;
		return parameter.IsVoidRepresentation ? "void"
			: CarrierTypeIdentity.IsCarrierName(parameter.Representation) ? parameter.Representation : null;
	}

	string? FunctionErasureWrapper(FunctionDefinition function, string name) =>
		function.GenericParameters.FirstOrDefault(parameter => parameter.Name == name) is GenericParameter parameter
			? ErasureWrapper(parameter)
			: ErasureWrapper(FindContainingType(function)?.GenericParameters.FirstOrDefault(parameter => parameter.Name == name));

	bool IsFunctionErasedParameter(FunctionDefinition function, string name) =>
		function.GenericParameters.FirstOrDefault(parameter => parameter.Name == name) is GenericParameter parameter
			? IsErasedGenericParameter(parameter)
			: FindContainingType(function)?.GenericParameters.FirstOrDefault(parameter => parameter.Name == name) is GenericParameter owner && IsErasedGenericParameter(owner);

	bool NeedsErasedArgumentPacking(CallExpression call) => callTargets.TryGetValue(call, out FunctionDefinition? function)
		&& function.Parameters.Any(parameter => parameter.Modifier is ParameterModifier.In or ParameterModifier.Out
			&& (ErasedTypeIdentity.TryGetPayload(parameter.ResolvedType, out _)
				|| IsGenericPlaceholderParameter(parameter.ResolvedType ?? "")));

	bool IsErasedResultCall(CallExpression call)
	{
		if (trailingOutResults.ContainsKey(call)) return true;
		if (erasedCallableResults.Contains(call)) return true;
		if (callTargets.TryGetValue(call, out FunctionDefinition? function)) return IsMaterializedGenericReturnFunction(function);
		if (!TryGetCallableInvocationShape(call.Target?.ResolvedType, out CallableShape shape)) return false;
		string result = shape.ReturnType;
		return ErasedTypeIdentity.TryGetPayload(result, out _);
	}

	Expression CaptureCopyBackTarget(Expression target)
	{
		string pointerType = AddPointer(target.ResolvedType ?? ErrorType);
		Expression address = new UnaryExpression { SourceSyntax = target.SourceSyntax, Operator = UnaryOperator.AddressOf,
			Operand = LowerExpression(target), ResolvedType = pointerType };
		DeclarationStatement pointer = CreateGeneratedLocal(NewGeneratedLocalName("output"), pointerType, TypeReferenceForResolvedName(pointerType), address);
		currentStatementPrefix!.Add(pointer);
		return new UnaryExpression { SourceSyntax = target.SourceSyntax, Operator = UnaryOperator.PointerDereference,
			Operand = CreateVariableReference(pointer.Target, pointerType), ResolvedType = target.ResolvedType };
	}

	void QueueErasedArgumentCopyBack(Statement statement)
	{
		currentStatementSuffix!.Add(statement);
		erasedArgumentCopyBackStatements.Add(statement);
	}

	Expression CompleteErasedArgumentCopyBack(Expression expression, int suffixStart)
	{
		// Ordinary throwing calls also append suffix checks. Those must stay after
		// the source expression, especially after await completion, without hoisting
		// the awaited call into an ordinary result temporary.
		if (currentStatementPrefix is null || currentStatementSuffix is null
			|| !currentStatementSuffix.Skip(suffixStart).Any(erasedArgumentCopyBackStatements.Contains))
			return expression;
		string type = expression.ResolvedType ?? "void";
		if (expression is CallExpression call && materializedGenericReturnCalls.Contains(call) && callTargets.TryGetValue(call, out FunctionDefinition? function))
			type = function.ResolvedType ?? "void";
		else if (expression is CallExpression indirect && materializedGenericReturnCalls.Contains(indirect) && erasedCallableResults.Contains(indirect))
			type = "void";
		Expression result;
		if (type == "void")
		{
			currentStatementPrefix.Add(new ExpressionStatement { SourceSyntax = expression.SourceSyntax, Expression = expression, ResolvedType = "void" });
			result = new DefaultExpression { ResolvedType = "void" };
		}
		else
		{
			DeclarationStatement value = CreateGeneratedLocal(NewGeneratedLocalName("callResult"), type, TypeReferenceForResolvedName(type), expression);
			currentStatementPrefix.Add(value);
			result = CreateVariableReference(value.Target, type);
		}
		currentStatementPrefix.AddRange(currentStatementSuffix.Skip(suffixStart));
		foreach (Statement statement in currentStatementSuffix.Skip(suffixStart))
			erasedArgumentCopyBackStatements.Remove(statement);
		currentStatementSuffix.RemoveRange(suffixStart, currentStatementSuffix.Count - suffixStart);
		return result;
	}

	void AddErasedResultArgument(CallExpression call, DeclarationTarget target)
	{
		int index = call.Arguments.Count;
		if (callTargets.TryGetValue(call, out FunctionDefinition? pending) && !trailingOutResults.ContainsKey(call) && !materializedGenericReturnParameters.ContainsKey(pending))
			TryExpandMaterializedGenericReturn(pending);
		if (callTargets.TryGetValue(call, out FunctionDefinition? function)
			&& (trailingOutResults.TryGetValue(call, out ParameterDefinition? result) || materializedGenericReturnParameters.TryGetValue(function, out result)))
		{
			List<ParameterDefinition> parameters = GetCallableParametersForCall(function, IncludeExplicitThisArgument(call.Target, function));
			int position = parameters.IndexOf(result);
			if (position >= 0) index = System.Math.Min(index, position);
		}
		else if (erasedCallableResults.Contains(call) && TryGetCallableInvocationShape(call.Target?.ResolvedType, out CallableShape callable))
		{
			List<string> parameters = GetExpandedCallableParameterTypes(callable.Parameters);
			int position = CallableShapeService.ResultStorageIndex(parameters);
			// Delegate rewriting may already expose the lowered output slot.
			if (callable.ReturnType is "void" or "bool") position--;
			index = System.Math.Min(index, System.Math.Max(0, position));
		}
		call.Arguments.Insert(index, new ArgumentExpression
		{
			SourceSyntax = call.SourceSyntax, Modifier = ArgumentModifier.Out,
			Value = CreateVariableReference(target, target.ResolvedType ?? ErrorType), ResolvedType = target.ResolvedType
		});
		materializedGenericReturnCalls.Add(call);
	}

	List<Expression> CaptureErasedResultComponents(CallExpression call, ParamsComponentShape shape, List<Statement> statements)
	{
		string valueType = call.ResolvedType ?? shape.TypeName;
		bool expanded = TryGetParamsComponentShape(null, valueType, "value", out ParamsComponentShape payloadShape);
		DeclarationStatement storage = expanded ? CreateMaterializedGenericReturnStorage(valueType, call.SourceSyntax)
			: CreateGeneratedLocal(NewGeneratedLocalName("result"), valueType, TypeReferenceForResolvedName(valueType), null);
		statements.Add(storage);
		// Bind omitted source inputs before introducing a positional output slot.
		if (!preparedExpandedReturnCalls.Contains(call)) AddImplicitDefaultArguments(call);
		AddErasedResultArgument(call, storage.Target);
		List<Expression> components = expanded ? CreateMaterializedComponentExpressions(storage.Target, payloadShape)
			: [CreateVariableReference(storage.Target, valueType)];
		List<Statement>? previousPrefix = currentStatementPrefix;
		Expression lowered;
		try
		{
			currentStatementPrefix = statements;
			lowered = LowerExpression(call) ?? call;
		}
		finally { currentStatementPrefix = previousPrefix; }
		statements.Add(new ExpressionStatement { SourceSyntax = call.SourceSyntax, ResolvedType = "void", Expression = lowered });
		return components;
	}
	static bool IsErasedGenericParameter(GenericParameter parameter) =>
		parameter.IsVoidRepresentation;

	void ValidateRepresentationArgument(GenericParameter parameter, string type, AnalysisScope scope, SyntaxNode? syntax)
	{
		if (type == ErrorType || type.StartsWith(UnresolvedType, System.StringComparison.Ordinal))
			return;
		string operand = StripTopLevelValueQualifiers(type);
		if (ErasedTypeIdentity.TryGetPayload(operand, out string payload) || CarrierTypeIdentity.TryGetPayload(operand, out payload)) operand = payload;
		if (operand == "void")
		{
			Report(GetRange(syntax), "No-value void is not a generic type argument.");
			return;
		}
		if (IsErasedGenericParameter(parameter)) return;
		string? carrier = ErasureWrapper(parameter);
		if (carrier is not null && TryExplainInvalidCarrierOperand(carrier, operand, scope, out string? reason))
			Report(GetRange(syntax), $"Type argument '{type}' does not satisfy representation constraint '{parameter.Name}: {carrier}': {reason}");
	}

	// Substitution preserves generic identities, but storage positions must keep
	// the declaration's single-object ABI for an expanded argument.
	string SubstituteStorageType(string type, Dictionary<string, string> substitutions)
	{
		if (substitutions.Count == 0) return type;
		// An iterator protocol yields through a current slot, which is storage.
		if (type.StartsWith("iter ", StringComparison.Ordinal))
			return "iter " + CarrierTypeIdentity.CollapseNested(RewriteStorageTypeName(type["iter ".Length..].Trim(), root => SubstituteStorageShapeName(root, substitutions, storage: true)));
		// A structured type is substituted once by its shapes; substituting the result again would
		// treat a replacement such as the swapped pick<U, T> arguments as binders to replace.
		TypeShapeParser parser = new(type);
		if (parser.TryParse(out TypeShape shape) && parser.IsEnd)
		{
			TypeShape named = shape;
			while (named.Element is not null) named = named.Element;
			if (named.Kind == TypeShapeKind.Named)
				return CarrierTypeIdentity.CollapseNested(RewriteStorageTypeName(type, root => SubstituteStorageShapeName(root, substitutions, storage: false)));
		}
		return SubstituteGenericType(type, substitutions);
	}

	string SubstituteStorageShapeName(TypeShape shape, Dictionary<string, string> substitutions, bool storage)
	{
		if (shape.Element is not null)
			return SubstituteStorageShapeName(shape.Element, substitutions, storage: true);
		if (TryParseCallableShape(shape.Name, out CallableShape callable))
		{
			List<string> parameters = callable.Parameters.Select(text => { CallableSlot slot = ParseCallableSlot(text);
				string type = RewriteStorageTypeName(slot.Type, parameterShape => SubstituteStorageShapeName(parameterShape, substitutions, slot.Modifier is "in" or "out"));
				return slot.Modifier.Length == 0 ? type : slot.Modifier + " " + type; }).ToList();
			return BuildCallableType(callable.Kind, SubstituteStorageType(callable.ReturnType, substitutions), parameters, callable.Spec, callable.CallSpec);
		}
		if (substitutions.TryGetValue(shape.Name, out string? value))
		{
			if (storage && TryGetParamsComponentShape(null, value, "value", out _)) value = $"void<{value}>";
			return value;
		}
		return SubstituteGenericType(shape.Name, substitutions);
	}

	static string ReadErasedValueType(string type)
	{
		if (ErasedTypeIdentity.TryGetPayload(type, out string payload) || CarrierTypeIdentity.TryGetPayload(type, out payload))
			return payload;
		return type;
	}
}
