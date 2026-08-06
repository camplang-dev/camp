using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	Expression LowerPreparedBufferExpressionWithMemo(PreparedBufferExpression prepared)
	{
		if (prepared.SourceSyntax is not null && preparedBufferLoweringRewrites.TryGetValue(prepared.SourceSyntax, out Expression? rewritten))
			return CloneParamsExpansionExpression(rewritten) ?? rewritten;

		Expression lowered = LowerPreparedBufferExpression(prepared);
		if (prepared.SourceSyntax is not null)
			preparedBufferLoweringRewrites[prepared.SourceSyntax] = lowered;
		return lowered;
	}

	bool TryGetImplicitPreparedCallForLowering(CallExpression call, out PreparedBufferExpression prepared)
	{
		if (implicitPreparedCalls.TryGetValue(call, out prepared!))
			return true;
		if (call.PreparedMode != PreparedCallMode.Transformed || call.PreparedResultType is null)
			return false;
		prepared = new PreparedBufferExpression
		{
			SourceSyntax = call.SourceSyntax,
			Expression = call,
			ResolvedType = call.PreparedResultType,
			ConvertedResultType = call.PreparedConvertedResultType
		};
		implicitPreparedCalls[call] = prepared;
		preparedBufferCalls[prepared] = call;
		return true;
	}

	bool TryRegisterPreparedLengthMember(MemberExpression member, out PreparedBufferExpression prepared)
	{
		prepared = null!;
		if (member.Name != "length" || member.Target is null)
			return false;
		Expression target = expressionRewrites.TryGetValue(member.Target, out Expression? rewrittenTarget)
			? rewrittenTarget
			: member.Target;
		if (target is PreparedBufferExpression preparedTarget)
			prepared = preparedTarget;
		else if (target is CallExpression call && TryGetImplicitPreparedCallForLowering(call, out PreparedBufferExpression implicitPrepared))
			prepared = implicitPrepared;
		else
			return false;
		preparedLengthMembers[member] = prepared;
		return true;
	}

	bool ContainsPreparedBufferExpression(Expression? expression)
	{
		return expression switch
		{
			null => false,
			PreparedBufferExpression => true,
			ParenthesizedExpression parenthesized => ContainsPreparedBufferExpression(parenthesized.Expression),
			CastExpression cast => ContainsPreparedBufferExpression(cast.Expression),
			ConstructionExpression construction => construction.Arguments.Any(argument => ContainsPreparedBufferExpression(argument))
				|| ContainsPreparedBufferExpression(construction.ElementCount)
				|| ContainsPreparedBufferExpression(construction.Initializer),
			WithinExpression within => ContainsPreparedBufferExpression(within.Context) || ContainsPreparedBufferExpression(within.Expression),
			ArgumentExpression argument => ContainsPreparedBufferExpression(argument.Value),
			CallExpression call => implicitPreparedCalls.ContainsKey(call) || ContainsPreparedBufferExpression(call.Target) || call.Arguments.Any(argument => ContainsPreparedBufferExpression(argument)),
			IndexExpression index => ContainsPreparedBufferExpression(index.Target) || index.Arguments.Any(argument => ContainsPreparedBufferExpression(argument)),
			MemberExpression member => ContainsPreparedBufferExpression(member.Target),
			MemberReferenceExpression member => ContainsPreparedBufferExpression(member.Target),
			NamelessIndexerExpression indexer => ContainsPreparedBufferExpression(indexer.Target) || indexer.Arguments.Any(argument => ContainsPreparedBufferExpression(argument)),
			UnaryExpression unary => ContainsPreparedBufferExpression(unary.Context) || ContainsPreparedBufferExpression(unary.Operand),
			PostfixUpdateExpression postfix => ContainsPreparedBufferExpression(postfix.Expression),
			FinallyCleanupExpression cleanup => ContainsPreparedBufferExpression(cleanup.Expression),
			BinaryExpression binary => ContainsPreparedBufferExpression(binary.Left) || ContainsPreparedBufferExpression(binary.Right),
			AssignmentExpression assignment => ContainsPreparedBufferExpression(assignment.Target) || ContainsPreparedBufferExpression(assignment.Value),
			ConditionalExpression conditional => ContainsPreparedBufferExpression(conditional.Condition)
				|| ContainsPreparedBufferExpression(conditional.WhenTrue)
				|| ContainsPreparedBufferExpression(conditional.WhenFalse),
			RangeExpression range => ContainsPreparedBufferExpression(range.Start) || ContainsPreparedBufferExpression(range.End),
			GroupedExpression grouped => grouped.Items.Any(item => ContainsPreparedBufferExpression(item.Expression)),
			ArrayExpression array => array.Elements.Any(ContainsPreparedBufferExpression),
			InitializerExpression initializer => initializer.Items.Any(item => ContainsPreparedBufferExpression(item.Expression)),
			InterpolatedStringExpression interpolation => interpolation.Segments.Any(segment => segment is InterpolatedStringExpressionSegment hole && ContainsPreparedBufferExpression(hole.Expression)),
			_ => false
		};
	}

	Expression LowerPreparedBufferExpression(PreparedBufferExpression prepared)
	{
		if (currentStatementPrefix is null)
		{
			Report(GetRange(prepared.SourceSyntax), "A prepared result cannot be used in this expression context because the compiler cannot introduce temporary storage here.");
			return prepared.Expression ?? prepared;
		}
		if (!preparedBufferCalls.TryGetValue(prepared, out CallExpression? sourceCall)
			|| !TryGetPreparedCallParameters(sourceCall, out FunctionDefinition? function, out List<ParameterDefinition>? callableParameters))
		{
			Report(GetRange(prepared.SourceSyntax), "Prepared call target was not resolved.");
			return prepared.Expression ?? prepared;
		}

		ParameterDefinition? prepParameter = callableParameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Prep);
		if (prepParameter is null)
		{
			Report(GetRange(prepared.SourceSyntax), "Prepared transformation requires a call target with a prep parameter.");
			return sourceCall;
		}

		string prepType = prepared.ResolvedType ?? prepParameter.ResolvedType ?? ErrorType;
		if (!TryParseTypeShape(prepType, out TypeShape prepShape)
			|| prepShape.Kind != TypeShapeKind.Array
			|| prepShape.Element is not TypeShape prepElement)
		{
			Report(GetRange(prepared.SourceSyntax), $"Prepared result type '{prepType}' is not a mutable array.");
			return sourceCall;
		}

		MaterializePreparedCallInputs(sourceCall);

		string elementType = TypeShapeParser.Format(prepElement);
		string lengthType = GetArrayLengthType(prepShape);
		Expression sizeCall = CreatePreparedProtocolCallForTarget(sourceCall, function, callableParameters, prepParameter, DefaultPrepBuffer(prepType, elementType, lengthType, prepared.SourceSyntax));
		sizeCall = LowerExpression(sizeCall) ?? sizeCall;

		DeclarationStatement required = CreateGeneratedLocal(
			NewGeneratedLocalName("prepRequired"),
			lengthType,
			TypeReferenceForResolvedName(lengthType),
			sizeCall);
		currentStatementPrefix.Add(required);
		Expression Required() => CreateVariableReference(required.Target, lengthType, prepared.SourceSyntax);

		TypeReference elementTypeReference = TypeReferenceForResolvedName(elementType, prepared.SourceSyntax);
		string? convertedResultType = prepared.ConvertedResultType;
		bool nullTerminate = convertedResultType is not null
			&& GetPrimitiveStringElementType(convertedResultType) == StripTopLevelValueQualifiers(elementType);
		Expression allocationLength = nullTerminate
			? Add(Required(), NumberLiteral("1", lengthType), lengthType)
			: Required();
		Expression allocation = prepared.HeapAllocated
			? CreateAllocCall(elementTypeReference, CurrentAllocator(), prepared.SourceSyntax, allocationLength)
			: CreateStackAllocCall(elementTypeReference, prepared.SourceSyntax, allocationLength);
		string elementsType = elementType + "*";
		DeclarationStatement resultElements = CreateGeneratedLocal(
			NewGeneratedLocalName("prepResult"),
			elementsType,
			PointerTo(elementTypeReference),
			allocation);
		currentStatementPrefix.Add(resultElements);
		Expression ResultElements() => CreateVariableReference(resultElements.Target, elementsType, prepared.SourceSyntax);
		Expression ResultArray() => CreateArrayView(ResultElements(), Required(), prepType, elementsType, lengthType, prepared.SourceSyntax);

		Expression writeCall = CreatePreparedProtocolCallForTarget(sourceCall, function, callableParameters, prepParameter, ResultArray());
		writeCall = LowerExpression(writeCall) ?? writeCall;
		currentStatementPrefix.Add(new ExpressionStatement
		{
			SourceSyntax = prepared.SourceSyntax,
			ResolvedType = "void",
			Expression = writeCall
		});

		if (nullTerminate)
		{
			currentStatementPrefix.Add(Assign(
				BufferIndex(ResultElements(), Required(), elementType, prepared.SourceSyntax),
				CharacterLiteral('\0', elementType, prepared.SourceSyntax),
				elementType,
				prepared.SourceSyntax));
			return new CastExpression
			{
				SourceSyntax = prepared.SourceSyntax,
				Kind = CastKind.Type,
				Type = TypeReferenceForResolvedName(convertedResultType!),
				Expression = ResultElements(),
				ResolvedType = convertedResultType
			};
		}

		DeclarationStatement resultArray = CreateGeneratedLocal(
			NewGeneratedLocalName("prepArray"),
			prepType,
			TypeReferenceForResolvedName(prepType, prepared.SourceSyntax),
			ResultArray());
		if (TryExpandParamsLocalDeclaration(resultArray, out List<Statement> resultArrayDeclarations) && resultArrayDeclarations.Count > 0)
			currentStatementPrefix.AddRange(resultArrayDeclarations);
		else
			currentStatementPrefix.Add(resultArray);
		return CreateVariableReference(resultArray.Target, prepType, prepared.SourceSyntax);
	}

	Expression LowerPreparedBufferLength(PreparedBufferExpression prepared)
	{
		if (currentStatementPrefix is null)
		{
			Report(GetRange(prepared.SourceSyntax), "Prepared length cannot be evaluated in this expression context because the compiler cannot introduce temporary storage here.");
			return NumberLiteral("0", "nuint");
		}
		if (!preparedBufferCalls.TryGetValue(prepared, out CallExpression? sourceCall)
			|| !TryGetPreparedCallParameters(sourceCall, out FunctionDefinition? function, out List<ParameterDefinition>? callableParameters))
		{
			Report(GetRange(prepared.SourceSyntax), "Prepared length target was not resolved.");
			return NumberLiteral("0", "nuint");
		}

		ParameterDefinition? prepParameter = callableParameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Prep);
		if (prepParameter is null)
		{
			Report(GetRange(prepared.SourceSyntax), "Prepared length requires a call with a prep parameter.");
			return NumberLiteral("0", "nuint");
		}

		string prepType = prepared.ResolvedType ?? prepParameter.ResolvedType ?? ErrorType;
		if (!TryParseTypeShape(prepType, out TypeShape prepShape)
			|| prepShape.Kind != TypeShapeKind.Array
			|| prepShape.Element is not TypeShape prepElement)
			return NumberLiteral("0", "nuint");

		MaterializePreparedCallInputs(sourceCall);
		string elementType = TypeShapeParser.Format(prepElement);
		string lengthType = GetArrayLengthType(prepShape);
		Expression sizeCall = CreatePreparedProtocolCallForTarget(
			sourceCall,
			function,
			callableParameters,
			prepParameter,
			DefaultPrepBuffer(prepType, elementType, lengthType, prepared.SourceSyntax));
		return LowerExpression(sizeCall) ?? sizeCall;
	}

	bool TryGetPreparedCallParameters(CallExpression sourceCall, out FunctionDefinition? function, out List<ParameterDefinition> callableParameters)
	{
		if (callTargets.TryGetValue(sourceCall, out function))
		{
			bool includeExplicitThis = IncludeExplicitThisArgumentForSourceBinding(sourceCall.Target, function);
			callableParameters = GetCallableParametersForCall(function, includeExplicitThis);
			return true;
		}

		if (callableInvocationParameters.TryGetValue(sourceCall, out List<ParameterDefinition>? foundParameters))
		{
			function = null;
			callableParameters = GetCallableParameters(foundParameters);
			return true;
		}

		function = null;
		callableParameters = [];
		return false;
	}

	void MaterializePreparedCallInputs(CallExpression call)
	{
		if (call.Target is not null
			&& expressionRewrites.TryGetValue(call.Target, out Expression? rewrittenTarget)
			&& !ReferenceEquals(rewrittenTarget, call.Target))
			call.Target = rewrittenTarget;
		switch (call.Target)
		{
			case MemberExpression member:
			{
				Expression? loweredTarget = LowerExpression(member.Target) ?? member.Target;
				member.Target = loweredTarget is TypeReferenceExpression
					? loweredTarget
					: MaterializePreparedValue(loweredTarget, "prepReceiver");
				break;
			}
			case MemberReferenceExpression member:
			{
				Expression? loweredTarget = LowerExpression(member.Target) ?? member.Target;
				member.Target = loweredTarget is TypeReferenceExpression
					? loweredTarget
					: MaterializePreparedValue(loweredTarget, "prepReceiver");
				break;
			}
			default:
				call.Target = LowerExpression(call.Target) ?? call.Target;
				if (!callTargets.ContainsKey(call))
					call.Target = MaterializePreparedValue(call.Target, "prepTarget");
				break;
		}

		foreach (ArgumentExpression argument in call.Arguments)
		{
			if (argument.Value is null || argument.Modifier is ArgumentModifier.Out or ArgumentModifier.Catch)
				continue;
			Expression lowered = LowerExpression(argument.Value) ?? argument.Value;
			if (MaterializePreparedValue(lowered, "prepArg") is Expression materialized)
			{
				argument.Value = materialized;
				argument.ResolvedType = materialized.ResolvedType ?? argument.ResolvedType;
			}
		}
	}

	Expression? MaterializePreparedValue(Expression? value, string prefix)
	{
		if (value is null)
			return null;
		if (IsRepeatableArrayLengthExpression(value))
			return value;

		string type = value.ResolvedType ?? ErrorType;
		DeclarationStatement local = CreateGeneratedLocal(
			NewGeneratedLocalName(prefix),
			type,
			TypeReferenceForResolvedName(type, value.SourceSyntax),
			value);
		local.SourceSyntax = value.SourceSyntax;
		currentStatementPrefix!.Add(local);
		return CreateVariableReference(local.Target, type, value.SourceSyntax);
	}

	CallExpression CreatePreparedProtocolCall(
		CallExpression sourceCall,
		FunctionDefinition function,
		List<ParameterDefinition> callableParameters,
		ParameterDefinition prepParameter,
		Expression prepBuffer)
	{
		CallExpression call = CloneCallExpression(sourceCall);
		call.PreparedMode = PreparedCallMode.Full;
		call.PreparedResultType = null;
		call.PreparedConvertedResultType = null;
		call.Arguments.Clear();

		Expression? interfaceContext = TryGetInterfacePreparedCallContext(sourceCall, function);
		bool[] supplied = new bool[callableParameters.Count];
		foreach (ArgumentExpression sourceArgument in sourceCall.Arguments)
		{
			if (interfaceContext is not null
				&& string.IsNullOrWhiteSpace(sourceArgument.Name)
				&& sourceArgument.Modifier == ArgumentModifier.None
				&& IsSamePreparedInterfaceContext(sourceArgument.Value, interfaceContext))
				continue;

			ArgumentExpression argument = ClonePreparedArgument(sourceArgument);
			if (TryBindCallArgumentToParameter(sourceArgument, callableParameters, supplied, sourceCall.SourceSyntax, out int parameterIndex)
				&& parameterIndex >= 0
				&& parameterIndex < callableParameters.Count)
			{
				if (ReferenceEquals(callableParameters[parameterIndex], prepParameter))
					continue;
				if (string.IsNullOrWhiteSpace(argument.Name) && !string.IsNullOrWhiteSpace(callableParameters[parameterIndex].Name))
					argument.Name = callableParameters[parameterIndex].Name;
			}
			call.Arguments.Add(argument);
		}

		call.Arguments.Add(new ArgumentExpression
		{
			SourceSyntax = prepBuffer.SourceSyntax ?? sourceCall.SourceSyntax,
			Name = string.IsNullOrWhiteSpace(prepParameter.Name) ? null : prepParameter.Name,
			Value = prepBuffer,
			ResolvedType = prepBuffer.ResolvedType
		});

		callTargets[call] = function;
		if (callGenericSubstitutions.TryGetValue(sourceCall, out Dictionary<string, string>? substitutions))
			callGenericSubstitutions[call] = new Dictionary<string, string>(substitutions, StringComparer.Ordinal);
		return call;
	}

	Expression? TryGetInterfacePreparedCallContext(CallExpression sourceCall, FunctionDefinition function)
	{
		if (FindContainingType(function) is not InterfaceDefinition)
			return null;
		if (sourceCall.Target is not MemberReferenceExpression { Target: Expression target })
			return null;
		return target is UnaryExpression { Operator: UnaryOperator.PointerDereference, Operand: Expression operand }
			? operand
			: target;
	}

	bool IsSamePreparedInterfaceContext(Expression? argument, Expression context)
	{
		if (ReferenceEquals(argument, context))
			return true;
		if (argument is VariableReferenceExpression argumentVariable
			&& context is VariableReferenceExpression contextVariable
			&& ReferenceEquals(argumentVariable.Variable, contextVariable.Variable))
			return true;
		return false;
	}

	CallExpression CreatePreparedProtocolCallForTarget(
		CallExpression sourceCall,
		FunctionDefinition? function,
		List<ParameterDefinition> callableParameters,
		ParameterDefinition prepParameter,
		Expression prepBuffer)
	{
		if (function is not null)
			return CreatePreparedProtocolCall(sourceCall, function, callableParameters, prepParameter, prepBuffer);

		CallExpression call = CloneCallExpression(sourceCall);
		call.PreparedMode = PreparedCallMode.Full;
		call.PreparedResultType = null;
		call.PreparedConvertedResultType = null;
		call.Arguments.Clear();

		bool[] supplied = new bool[callableParameters.Count];
		foreach (ArgumentExpression sourceArgument in sourceCall.Arguments)
		{
			ArgumentExpression argument = ClonePreparedArgument(sourceArgument);
			if (TryBindCallArgumentToParameter(sourceArgument, callableParameters, supplied, sourceCall.SourceSyntax, out int parameterIndex)
				&& parameterIndex >= 0
				&& parameterIndex < callableParameters.Count)
			{
				if (ReferenceEquals(callableParameters[parameterIndex], prepParameter))
					continue;
				if (string.IsNullOrWhiteSpace(argument.Name) && !string.IsNullOrWhiteSpace(callableParameters[parameterIndex].Name))
					argument.Name = callableParameters[parameterIndex].Name;
			}
			call.Arguments.Add(argument);
		}

		call.Arguments.Add(new ArgumentExpression
		{
			SourceSyntax = prepBuffer.SourceSyntax ?? sourceCall.SourceSyntax,
			Name = string.IsNullOrWhiteSpace(prepParameter.Name) ? null : prepParameter.Name,
			Value = prepBuffer,
			ResolvedType = prepBuffer.ResolvedType
		});

		callableInvocationParameters[call] = callableParameters;
		if (callGenericSubstitutions.TryGetValue(sourceCall, out Dictionary<string, string>? substitutions))
			callGenericSubstitutions[call] = new Dictionary<string, string>(substitutions, StringComparer.Ordinal);
		return call;
	}

	ArgumentExpression ClonePreparedArgument(ArgumentExpression argument)
	{
		return new ArgumentExpression
		{
			SourceSyntax = argument.SourceSyntax,
			Name = argument.Name,
			Modifier = argument.Modifier,
			Type = argument.Type,
			Target = argument.Target,
			Value = CloneParamsExpansionExpression(argument.Value),
			ResolvedType = argument.ResolvedType,
			MaterializedInitializerAddressType = argument.MaterializedInitializerAddressType,
			MaterializedInitializerAddressResultType = argument.MaterializedInitializerAddressResultType
		};
	}

	InitializerExpression DefaultPrepBuffer(string prepType, string elementType, string lengthType, SyntaxNode? syntax)
	{
		return new InitializerExpression
		{
			SourceSyntax = syntax,
			ResolvedType = prepType,
			Items =
			{
				new InitializerItem
				{
					SourceSyntax = syntax,
					Expression = NullLiteral(syntax),
					ResolvedType = elementType + "*"
				},
				new InitializerItem
				{
					SourceSyntax = syntax,
					Expression = NumberLiteral("0", lengthType),
					ResolvedType = lengthType
				}
			}
		};
	}
}
