using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly HashSet<FunctionDefinition> optionalErasedReturnFunctions = [];

	bool NeedsErasedArgumentPacking(CallExpression call) => callTargets.TryGetValue(call, out FunctionDefinition? function)
		&& function.Parameters.Any(parameter => parameter.Modifier is ParameterModifier.In or ParameterModifier.Out
			&& (ErasedTypeIdentity.TryGetPayload(parameter.ResolvedType, out _)
				|| IsGenericPlaceholderParameter(parameter.ResolvedType ?? "")));

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

	bool TryPackOptionalErasedArgument(List<ArgumentExpression> arguments, int argumentIndex, List<ParameterDefinition>? parameters, int parameterIndex)
	{
		if (currentStatementPrefix is null || currentStatementSuffix is null || parameters is null
			|| parameterIndex + 1 >= parameters.Count || arguments[argumentIndex].Value is not Expression value
			|| parameters[parameterIndex].ExpandedSourceParameter is not ParameterDefinition source
			|| !TryGetOptionalPayloadType(source.ResolvedType, out string erased)) return false;
		if (!ErasedTypeIdentity.TryGetPayload(erased, out string payload))
		{
			if (!IsGenericPlaceholderParameter(erased)) return false;
			payload = parameters[parameterIndex].ResolvedType ?? ErrorType;
		}
		ArgumentExpression argument = arguments[argumentIndex];
		if (value is VariableReferenceExpression { Variable: ParameterDefinition input }
			&& TryGetOptionalPayloadType(input.ResolvedType, out string inputPayload)
			&& (ErasedTypeIdentity.TryGetPayload(inputPayload, out _) || IsGenericPlaceholderParameter(inputPayload))
			&& paramsExpansions.TryGetValue(input, out List<ParamsExpansionComponent>? direct) && direct.Count == 2)
		{
			argument.Value = CreateVariableReference(direct[0].Node, ReadErasedValueType(direct[0].Type));
			argument.ResolvedType = argument.Value.ResolvedType;
			arguments.Insert(argumentIndex + 1, new ArgumentExpression { SourceSyntax = argument.SourceSyntax, Modifier = argument.Modifier,
				Value = CreateVariableReference(direct[1].Node, direct[1].Type), ResolvedType = direct[1].Type });
			return true;
		}
		if (!TryGetParamsComponentShape(null, payload + "?", "value", out ParamsComponentShape shape)
			|| !TryCreateValueParamsComponents(value, shape, out List<Expression> components)) return false;
		bool expanded = TryGetParamsComponentShape(null, payload, "value", out ParamsComponentShape payloadShape);
		DeclarationStatement storage = expanded ? CreateMaterializedGenericReturnStorage(payload, value.SourceSyntax)
			: CreateGeneratedLocal(NewGeneratedLocalName("argument"), payload, TypeReferenceForResolvedName(payload), null);
		currentStatementPrefix.Add(storage);
		List<Expression> slots = expanded ? CreateMaterializedComponentExpressions(storage.Target, payloadShape)
			: [CreateVariableReference(storage.Target, payload)];
		bool output = source.Modifier == ParameterModifier.Out;
		for (int i = 0; i < slots.Count; i++)
		{
			Expression target = output ? CaptureCopyBackTarget(components[i]) : slots[i];
			Expression initial = output ? slots[i] : components[i];
			(output ? currentStatementSuffix : currentStatementPrefix).Add(new ExpressionStatement
			{
				SourceSyntax = value.SourceSyntax, ResolvedType = "void", Expression = new AssignmentExpression
				{ Target = LowerExpression(target), Value = LowerExpression(initial), Operator = AssignmentOperator.Assign, ResolvedType = target.ResolvedType }
			});
		}
		argument.Value = CreateVariableReference(storage.Target, storage.Target.ResolvedType ?? ErrorType);
		argument.ResolvedType = argument.Value.ResolvedType;
		DeclarationStatement flag = CreateGeneratedLocal(NewGeneratedLocalName("specified"), "bool", TypeReferenceForResolvedName("bool"), output ? null : LowerExpression(components[^1]));
		currentStatementPrefix.Add(flag);
		if (output)
			currentStatementSuffix.Add(new ExpressionStatement { ResolvedType = "void", Expression = new AssignmentExpression
			{ Target = CaptureCopyBackTarget(components[^1]), Value = CreateVariableReference(flag.Target, "bool"), Operator = AssignmentOperator.Assign, ResolvedType = "bool" } });
		arguments.Insert(argumentIndex + 1, new ArgumentExpression { SourceSyntax = argument.SourceSyntax, Modifier = argument.Modifier,
			Value = CreateVariableReference(flag.Target, "bool"), ResolvedType = "bool" });
		return true;
	}

	Expression CompleteErasedArgumentCopyBack(Expression expression, int suffixStart)
	{
		if (currentStatementPrefix is null || currentStatementSuffix is null || currentStatementSuffix.Count <= suffixStart)
			return expression;
		string type = expression.ResolvedType ?? "void";
		if (expression is CallExpression call && materializedGenericReturnCalls.Contains(call) && callTargets.TryGetValue(call, out FunctionDefinition? function))
			type = function.ResolvedType ?? "void";
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
		currentStatementSuffix.RemoveRange(suffixStart, currentStatementSuffix.Count - suffixStart);
		return result;
	}

	void AddErasedResultArgument(CallExpression call, DeclarationTarget target)
	{
		int index = call.Arguments.Count;
		if (callTargets.TryGetValue(call, out FunctionDefinition? pending) && !materializedGenericReturnParameters.ContainsKey(pending))
			TryExpandMaterializedGenericReturn(pending);
		if (callTargets.TryGetValue(call, out FunctionDefinition? function) && materializedGenericReturnParameters.TryGetValue(function, out ParameterDefinition? result))
		{
			List<ParameterDefinition> parameters = GetCallableParametersForCall(function, IncludeExplicitThisArgument(call.Target, function));
			int position = parameters.IndexOf(result);
			if (position >= 0) index = System.Math.Min(index, position);
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
		FunctionDefinition function = callTargets[call];
		bool optional = optionalErasedReturnFunctions.Contains(function)
			|| IsErasedGenericReturn(function) && TryGetOptionalPayloadType(function.ResolvedType, out _);
		string valueType = call.ResolvedType ?? shape.TypeName;
		if (optional && TryGetOptionalPayloadType(valueType, out string payload)) valueType = payload;
		bool expanded = TryGetParamsComponentShape(null, valueType, "value", out ParamsComponentShape payloadShape);
		DeclarationStatement storage = expanded ? CreateMaterializedGenericReturnStorage(valueType, call.SourceSyntax)
			: CreateGeneratedLocal(NewGeneratedLocalName("result"), valueType, TypeReferenceForResolvedName(valueType), null);
		statements.Add(storage);
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
		if (optional)
		{
			DeclarationStatement specified = CreateGeneratedLocal(NewGeneratedLocalName("specified"), "bool", TypeReferenceForResolvedName("bool"), lowered);
			statements.Add(specified);
			components.Add(CreateVariableReference(specified.Target, "bool"));
		}
		else statements.Add(new ExpressionStatement { SourceSyntax = call.SourceSyntax, ResolvedType = "void", Expression = lowered });
		return components;
	}
	static bool IsErasedGenericParameter(GenericParameter parameter) =>
		parameter.Constraint is AnyTypeReference or CopyableTypeReference || parameter.RequiresImplementation;

	void ValidateRepresentationArgument(GenericParameter parameter, string type, SyntaxNode? syntax)
	{
		if (!IsErasedGenericParameter(parameter) && TryGetParamsComponentShape(null, type, "value", out _))
			Report(GetRange(syntax), $"Expandable type argument '{type}' requires an erased generic parameter; use '{parameter.Name}: any' or '{parameter.Name}: copyable'.");
	}

	// Substitution preserves generic identities, but storage positions must keep
	// the declaration's single-object ABI for an expanded argument.
	string SubstituteStorageType(string type, Dictionary<string, string> substitutions)
	{
		if (!TryParseTypeShape(type, out TypeShape shape))
			return SubstituteGenericType(type, substitutions);
		return TypeShapeParser.Format(SubstituteStorageShape(shape, substitutions, storage: false));
	}

	TypeShape SubstituteStorageShape(TypeShape shape, Dictionary<string, string> substitutions, bool storage)
	{
		if (shape.Element is not null)
			return shape with { Element = SubstituteStorageShape(shape.Element, substitutions, storage: true) };
		if (substitutions.TryGetValue(shape.Name, out string? value))
		{
			if (storage && TryGetParamsComponentShape(null, value, "value", out _)) value = $"void<{value}>";
			if (TryParseTypeShape(value, out TypeShape replacement))
				return replacement with { Qualifiers = replacement.Qualifiers with { IsConst = replacement.Qualifiers.IsConst || shape.Qualifiers.IsConst, IsVolatile = replacement.Qualifiers.IsVolatile || shape.Qualifiers.IsVolatile } };
		}
		return shape with { Name = SubstituteGenericType(shape.Name, substitutions) };
	}

	static string ReadErasedValueType(string type)
	{
		if (ErasedTypeIdentity.TryGetPayload(type, out string payload))
			return payload;
		if (type.EndsWith('?') && ErasedTypeIdentity.TryGetPayload(type[..^1], out payload))
			return payload + "?";
		return type;
	}
}
