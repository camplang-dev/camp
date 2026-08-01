using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	Expression LowerPreparedBufferExpression(PreparedBufferExpression prepared)
	{
		if (currentStatementPrefix is null)
		{
			Report(GetRange(prepared.SourceSyntax), "prep cannot be used in this expression context because the compiler cannot introduce temporary storage here.");
			return prepared.Expression ?? prepared;
		}
		if (!preparedBufferCalls.TryGetValue(prepared, out CallExpression? sourceCall)
			|| !callTargets.TryGetValue(sourceCall, out FunctionDefinition? function))
		{
			Report(GetRange(prepared.SourceSyntax), "prep target was not resolved.");
			return prepared.Expression ?? prepared;
		}

		bool includeExplicitThis = IncludeExplicitThisArgument(sourceCall.Target, function);
		List<ParameterDefinition> callableParameters = GetCallableParametersForCall(function, includeExplicitThis);
		ParameterDefinition? prepParameter = callableParameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Prep);
		if (prepParameter is null)
		{
			Report(GetRange(prepared.SourceSyntax), "prep requires a call or property getter target with a prep parameter.");
			return sourceCall;
		}

		string prepType = prepared.ResolvedType ?? prepParameter.ResolvedType ?? ErrorType;
		if (!TryParseTypeShape(prepType, out TypeShape prepShape)
			|| prepShape.Kind != TypeShapeKind.Array
			|| prepShape.Element is not TypeShape prepElement)
		{
			Report(GetRange(prepared.SourceSyntax), $"prep result type '{prepType}' is not a mutable array.");
			return sourceCall;
		}

		MaterializePreparedCallInputs(sourceCall);

		string elementType = TypeShapeParser.Format(prepElement);
		string lengthType = GetArrayLengthType(prepShape);
		Expression sizeCall = CreatePreparedProtocolCall(sourceCall, function, callableParameters, prepParameter, DefaultPrepBuffer(prepType, elementType, lengthType, prepared.SourceSyntax));
		sizeCall = LowerExpression(sizeCall) ?? sizeCall;

		DeclarationStatement required = CreateGeneratedLocal(
			NewGeneratedLocalName("prepRequired"),
			lengthType,
			TypeReferenceForResolvedName(lengthType),
			sizeCall);
		currentStatementPrefix.Add(required);
		Expression Required() => CreateVariableReference(required.Target, lengthType, prepared.SourceSyntax);

		TypeReference elementTypeReference = TypeReferenceForResolvedName(elementType, prepared.SourceSyntax);
		Expression allocation = prepared.HeapAllocated
			? CreateAllocCall(elementTypeReference, CurrentAllocator(), prepared.SourceSyntax, Required())
			: CreateStackAllocCall(elementTypeReference, prepared.SourceSyntax, Required());
		string elementsType = elementType + "*";
		DeclarationStatement resultElements = CreateGeneratedLocal(
			NewGeneratedLocalName("prepResult"),
			elementsType,
			PointerTo(elementTypeReference),
			allocation);
		currentStatementPrefix.Add(resultElements);
		Expression ResultElements() => CreateVariableReference(resultElements.Target, elementsType, prepared.SourceSyntax);
		Expression ResultArray() => CreateArrayView(ResultElements(), Required(), prepType, elementsType, lengthType, prepared.SourceSyntax);

		Expression writeCall = CreatePreparedProtocolCall(sourceCall, function, callableParameters, prepParameter, ResultArray());
		writeCall = LowerExpression(writeCall) ?? writeCall;
		currentStatementPrefix.Add(new ExpressionStatement
		{
			SourceSyntax = prepared.SourceSyntax,
			ResolvedType = "void",
			Expression = writeCall
		});

		return ResultArray();
	}

	void MaterializePreparedCallInputs(CallExpression call)
	{
		switch (call.Target)
		{
			case MemberExpression member:
				member.Target = MaterializePreparedValue(LowerExpression(member.Target) ?? member.Target, "prepReceiver");
				break;
			case MemberReferenceExpression member:
				member.Target = MaterializePreparedValue(LowerExpression(member.Target) ?? member.Target, "prepReceiver");
				break;
			default:
				call.Target = LowerExpression(call.Target) ?? call.Target;
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
		call.Arguments.Clear();

		List<ParameterDefinition> sourceParameters = [.. callableParameters.Where(static parameter => parameter.Modifier != ParameterModifier.Prep)];
		bool[] supplied = new bool[sourceParameters.Count];
		foreach (ArgumentExpression sourceArgument in sourceCall.Arguments)
		{
			ArgumentExpression argument = ClonePreparedArgument(sourceArgument);
			if (TryBindCallArgumentToParameter(sourceArgument, sourceParameters, supplied, sourceCall.SourceSyntax, out int parameterIndex)
				&& parameterIndex >= 0
				&& parameterIndex < sourceParameters.Count
				&& string.IsNullOrWhiteSpace(argument.Name)
				&& !string.IsNullOrWhiteSpace(sourceParameters[parameterIndex].Name))
			{
				argument.Name = sourceParameters[parameterIndex].Name;
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
