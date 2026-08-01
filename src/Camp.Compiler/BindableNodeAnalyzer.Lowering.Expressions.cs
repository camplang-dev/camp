using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	Expression? LowerExpression(Expression? expression)
	{
		switch (expression)
		{
			case null:
				return null;

			case ConstructionExpression construction:
				for (int i = 0; i < construction.Arguments.Count; i++)
					construction.Arguments[i] = LowerArgument(construction.Arguments[i]);
				construction.ElementCount = LowerExpression(construction.ElementCount);
				LowerInitializer(construction.Initializer);
				return RewriteConstruction(construction);

				case WithinExpression within:
					bool defaultWithin = within.Context is DefaultWithinContextExpression;
					within.Context = defaultWithin ? CreateDefaultWithinArgument(within.Context?.SourceSyntax) : LowerExpression(within.Context);
					if (within.Expression is null)
						return within.Context;
					Expression? previousWithinContext = currentWithinContext;
					int previousDefaultWithinContextDepth = currentDefaultWithinContextDepth;
					currentWithinContext = defaultWithin ? null : CaptureWithinContext(within.Context, within.SourceSyntax);
					if (defaultWithin)
						currentDefaultWithinContextDepth++;
					Expression? lowered = LowerExpression(within.Expression);
					currentWithinContext = previousWithinContext;
					currentDefaultWithinContextDepth = previousDefaultWithinContextDepth;
					return lowered ?? within.Expression;

			case PreparedBufferExpression prepared:
				return LowerPreparedBufferExpressionWithMemo(prepared);

			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
					item.Expression = LowerExpression(item.Expression);
				break;

			case ArrayExpression array:
				for (int i = 0; i < array.Elements.Count; i++)
					array.Elements[i] = LowerExpression(array.Elements[i]) ?? array.Elements[i];
				break;

			case InitializerExpression initializer:
				LowerInitializer(initializer);
				break;

			case ParenthesizedExpression parenthesized:
				parenthesized.Expression = LowerExpression(parenthesized.Expression);
				break;

			case CastExpression cast:
				cast.Expression = LowerExpression(cast.Expression);
				if (IsInterfacePointerType(cast.Type))
					return LowerInterfaceConversion(cast.Type, cast.Expression) ?? cast;
				break;

			case SizeOfExpression sizeOf:
				return LowerSizeOfExpression(sizeOf);

			case VTableOfExpression vtableOf:
				return LowerVTableOfExpression(vtableOf);

			case NameOfExpression nameOf:
				return LowerNameOfExpression(nameOf);

			case CurrentAllocatorExpression currentAllocator:
				return CurrentAllocator() ?? NullLiteral(currentAllocator.SourceSyntax);

			case InterpolatedStringExpression interpolation:
				return LowerInterpolatedStringExpression(interpolation);

			case LambdaExpression lambda:
				return LowerLambdaExpression(lambda);

			case ArgumentExpression argument:
				return LowerArgument(argument);

			case CallExpression call:
				if (call.Target is CurrentAllocatorExpression currentAllocatorTarget && call.TypeArguments.Count == 1 && call.Arguments.Count == 1)
				{
					call.Arguments[0] = LowerArgument(call.Arguments[0]);
					return CreateAllocCallFromByteSize(call.TypeArguments[0], CurrentAllocator(), call.Arguments[0].Value ?? NumberLiteral("0", "nuint"), call.SourceSyntax ?? currentAllocatorTarget.SourceSyntax);
				}
				if (call.Target is MemberReferenceExpression callMemberTarget)
				{
					callMemberTarget.Target = LowerReceiverExpression(callMemberTarget.Target);
					if (TryCreateParamsComponentExpressions(callMemberTarget, out List<Expression> callTargetComponents) && callTargetComponents.Count == 1)
						call.Target = callTargetComponents[0];
					else if (!TryCreateParamsComponentExpressions(callMemberTarget, out _) && TryCreateParamsMemberComponentExpression(callMemberTarget, out Expression componentTarget))
						call.Target = componentTarget;
				}
				else
					call.Target = LowerExpression(call.Target);
				AddImplicitDefaultArguments(call);
				LowerThrowingArguments(call);
				AddImplicitSizeOfArguments(call);
				AddImplicitNameOfArguments(call);
				AddImplicitWithinArgument(call);
				AddImplicitVTableOfArguments(call);
				NormalizeWithinArgumentOrder(call);
				for (int i = 0; i < call.Arguments.Count; i++)
					call.Arguments[i] = LowerArgument(call.Arguments[i]);
				bool loweredInterfaceCall = LowerInterfaceCall(call);
				bool flattenedInstanceCall = loweredInterfaceCall || TryRewriteInstanceInvocation(call);
				if (!flattenedInstanceCall)
				{
					TryRewriteStaticMemberInvocation(call);
					TryRewriteIteratorProtocolInvocation(call);
					TryRewriteDelegateInvocation(call);
				}
				ExpandParamsArguments(call);
				LowerCallArgumentConversions(call);
				if (TryRewriteScalarMaterializedGenericReturnCall(call, out Expression? materialized))
					return materialized;
				return MaterializeReceiverCall(LowerUncaughtThrowingCall(call));

			case IndexExpression index:
				if (index.Target is MemberReferenceExpression getter && IsPropertyGetterReference(getter))
				{
					CallExpression call = RewritePropertyGetterCall(getter, index.Arguments);
					call.ResolvedType = index.ResolvedType ?? call.ResolvedType;
					return LowerExpression(call);
				}
				if (TryCreateParamsComponentExpressions(index, out List<Expression> indexedComponents) && indexedComponents.Count == 1)
					return LowerExpression(indexedComponents[0]);
				index.Target = LowerExpression(index.Target);
				for (int i = 0; i < index.Arguments.Count; i++)
					index.Arguments[i] = LowerArgument(index.Arguments[i]);
				break;

			case MemberExpression member:
				if (TryRewriteMaterializedGenericIndexedMemberAccess(member, out Expression indexedMember))
					return LowerExpression(indexedMember);
				if (TryCreateParamsMemberComponentExpression(member, out Expression earlyMemberComponent))
					return LowerExpression(earlyMemberComponent);
				member.Target = LowerExpression(member.Target);
				if (TryCreateParamsMemberComponentExpression(member, out Expression memberComponent))
					return LowerExpression(memberComponent);
				break;

			case MemberReferenceExpression memberReference:
				if (TryCreateParamsMemberComponentExpression(memberReference, out Expression earlyParamsComponent))
					return LowerExpression(earlyParamsComponent);
				memberReference.Target = LowerExpression(memberReference.Target);
				if (IsPropertyGetterReference(memberReference))
					return LowerExpression(RewritePropertyGetterCall(memberReference, []));
				if (TryCreateParamsMemberComponentExpression(memberReference, out Expression paramsComponent))
					return LowerExpression(paramsComponent);
				if (memberReference is { Target: not null, Member: FunctionDefinition function } && FindContainingType(function) is not InterfaceDefinition)
					return RewriteInstanceMethodDelegate(memberReference);
				break;

			case NamelessIndexerExpression nameless:
				nameless.Target = LowerExpression(nameless.Target);
				for (int i = 0; i < nameless.Arguments.Count; i++)
					nameless.Arguments[i] = LowerArgument(nameless.Arguments[i]);
				break;

			case UnaryExpression unary:
				unary.Context = LowerExpression(unary.Context);
				unary.Operand = LowerExpression(unary.Operand);
				break;

			case PostfixUpdateExpression postfix:
				postfix.Expression = LowerExpression(postfix.Expression);
				break;

			case FinallyCleanupExpression finallyCleanup:
				return RewriteFinallyCleanupExpression(finallyCleanup);

			case BinaryExpression binary:
				binary.Left = LowerScalarExpression(binary.Left);
				binary.Right = LowerScalarExpression(binary.Right);
				break;

			case AssignmentExpression assignment:
				if (IsDiscardExpression(assignment.Target))
				{
					assignment.Value = LowerExpression(assignment.Value);
					assignment.Target = CreateDiscardReference(assignment.Value?.ResolvedType ?? assignment.Target?.ResolvedType ?? ErrorType, assignment.Target?.SourceSyntax);
					break;
				}
				if (TryRewritePropertySetterAssignment(assignment, out Expression? setterCall))
					return setterCall;
				if (TryRewriteInitAssignment(assignment, out Expression? initCall))
					return initCall;
				assignment.Target = LowerExpression(assignment.Target);
				assignment.Value = LowerExpression(assignment.Value);
				if (assignment.Target is VariableReferenceExpression { Variable: DeclarationTarget target })
					assignment.Value = LowerInterfaceConversion(target.Type, assignment.Value);
				break;

			case ConditionalExpression conditional:
				conditional.Condition = LowerExpression(conditional.Condition);
				conditional.WhenTrue = LowerExpression(conditional.WhenTrue);
				conditional.WhenFalse = LowerExpression(conditional.WhenFalse);
				break;

			case RangeExpression range:
				range.Start = LowerExpression(range.Start);
				range.End = LowerExpression(range.End);
				break;
		}

		return expression;
	}

	Expression? LowerReceiverExpression(Expression? expression)
	{
		bool previous = loweringReceiverExpression;
		try
		{
			loweringReceiverExpression = true;
			return LowerExpression(expression);
		}
		finally
		{
			loweringReceiverExpression = previous;
		}
	}

	Expression MaterializeReceiverCall(Expression expression)
	{
		if (!loweringReceiverExpression
			|| currentStatementPrefix is null
			|| expression is not CallExpression
			|| expression.ResolvedType is not string type
			|| type == "void"
			|| IsExpandedReturnReceiverCall((CallExpression)expression)
			|| (TryGetParamsComponentShape(null, type, "receiver", out ParamsComponentShape shape) && shape.Components.Count > 1)
			|| CanTakeReceiverAddress(expression))
		{
			return expression;
		}

		DeclarationStatement local = CreateGeneratedLocal(
			NewGeneratedLocalName("receiver"),
			type,
			TypeReferenceForResolvedName(type),
			expression);
		currentStatementPrefix.Add(local);
		return CreateVariableReference(local.Target, type, expression.SourceSyntax);
	}

	bool IsExpandedReturnReceiverCall(CallExpression call)
	{
		return callTargets.TryGetValue(call, out FunctionDefinition? function)
			&& TryGetExpandedReturnShape(call, function, out ParamsComponentShape? shape)
			&& shape.Components.Count > 1;
	}

	Expression? LowerScalarExpression(Expression? expression)
	{
		Expression? lowered = LowerExpression(expression);
		if (lowered is not null
			&& TryCreateParamsComponentExpressions(lowered, out List<Expression> components)
			&& components.Count > 1)
			return LowerExpression(components[0]);
		return lowered;
	}

	bool TryRewriteMaterializedGenericIndexedMemberAccess(MemberExpression member, out Expression expression)
	{
		expression = member;
		if (currentStatementPrefix is null || member.Target is not IndexExpression index)
			return false;
		if (!TryGetMaterializedGenericIndexGetter(index, out MemberReferenceExpression? getter) || getter is null)
			return false;

		CallExpression call = RewritePropertyGetterCall(getter, index.Arguments);
		call.ResolvedType = index.ResolvedType ?? call.ResolvedType;
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function) || !IsMaterializedGenericReturnFunction(function))
			return false;

		string resultType = index.ResolvedType ?? materializedGenericReturnParameters.GetValueOrDefault(function)?.ResolvedType ?? ErrorType;
		DeclarationStatement storage = CreateMaterializedGenericReturnStorage(resultType, index.SourceSyntax ?? member.SourceSyntax);
		currentStatementPrefix.Add(storage);
		call.Arguments.Add(new ArgumentExpression
		{
			SourceSyntax = index.SourceSyntax ?? member.SourceSyntax,
			Modifier = ArgumentModifier.Out,
			Value = CreateVariableReference(storage.Target, storage.Target.ResolvedType ?? ErrorType),
			ResolvedType = storage.Target.ResolvedType ?? ErrorType
		});
		currentStatementPrefix.Add(new ExpressionStatement
		{
			SourceSyntax = index.SourceSyntax ?? member.SourceSyntax,
			ResolvedType = "void",
			Expression = LowerExpression(call)
		});
		expression = new MemberExpression
		{
			SourceSyntax = member.SourceSyntax,
			Target = CreateVariableReference(storage.Target, storage.Target.ResolvedType ?? ErrorType),
			Name = member.Name,
			ResolvedType = member.ResolvedType
		};
		return true;
	}

	bool TryGetMaterializedGenericIndexGetter(IndexExpression index, out MemberReferenceExpression? getter)
	{
		getter = null;
		if (index.Target is MemberExpression propertyMember
			&& TryCreateMaterializedGenericPropertyGetterReference(propertyMember, out getter))
			return true;
		if (index.Target is MemberExpression member
			&& expressionRewrites.TryGetValue(member, out Expression? rewritten)
			&& rewritten is MemberReferenceExpression rewrittenGetter
			&& IsPropertyGetterReference(rewrittenGetter))
		{
			getter = rewrittenGetter;
			return true;
		}
		return false;
	}

	bool TryRewriteScalarMaterializedGenericReturnCall(CallExpression call, out Expression expression)
	{
		expression = call;
		if (currentStatementPrefix is null
			|| !callTargets.TryGetValue(call, out FunctionDefinition? function)
			|| !IsMaterializedGenericReturnFunction(function))
			return false;

		string resultType = call.ResolvedType ?? function.ReturnType?.ResolvedType ?? ErrorType;
		if (TryGetParamsComponentShape(null, resultType, "value", out ParamsComponentShape shape) && shape.Components.Count > 1)
			return false;

		DeclarationStatement storage = CreateGeneratedLocal(NewGeneratedLocalName("value"), resultType, TypeReferenceForResolvedName(resultType), null);
		currentStatementPrefix.Add(storage);
		call.Arguments.Add(new ArgumentExpression
		{
			SourceSyntax = call.SourceSyntax,
			Modifier = ArgumentModifier.Out,
			Value = CreateVariableReference(storage.Target, resultType),
			ResolvedType = resultType
		});
		currentStatementPrefix.Add(new ExpressionStatement
		{
			SourceSyntax = call.SourceSyntax,
			ResolvedType = "void",
			Expression = call
		});
		expression = CreateVariableReference(storage.Target, resultType);
		return true;
	}

	bool TryRewriteInitAssignment(AssignmentExpression assignment, out Expression? expression)
	{
		expression = null;
		if (assignment.Operator != AssignmentOperator.Assign
			|| assignment.Target is null
			|| assignment.Value is not ConstructionExpression { Kind: ConstructionKind.Init } construction)
			return false;

		for (int i = 0; i < construction.Arguments.Count; i++)
			construction.Arguments[i] = LowerArgument(construction.Arguments[i]);
		LowerInitializer(construction.Initializer);

		Expression target = LowerExpression(assignment.Target) ?? assignment.Target;
		Expression targetAddress = new UnaryExpression
		{
			SourceSyntax = assignment.Target.SourceSyntax,
			Operator = UnaryOperator.AddressOf,
			Operand = target,
			ResolvedType = AddPointer(target.ResolvedType ?? ErrorType)
		};
		constructionTargets.TryGetValue(construction, out FunctionDefinition? constructorTarget);
		CallExpression? initCall = CreateInitCallForConstruction(construction, targetAddress, constructorTarget);
		if (initCall is null)
			return false;

		if (typeDefinitions.TryGetValue(BaseConstructedType(target.ResolvedType), out TypeDefinition? definition)
			&& CreateVirtualTableAssignment(target, definition) is Expression vtableAssignment)
		{
			GroupedExpression grouped = new()
			{
				SourceSyntax = assignment.SourceSyntax,
				ResolvedType = "void"
			};
			grouped.Items.Add(new GroupedExpressionItem
			{
				Expression = vtableAssignment,
				ResolvedType = "void"
			});
			grouped.Items.Add(new GroupedExpressionItem
			{
				Expression = initCall,
				ResolvedType = "void"
			});
			expression = grouped;
			return true;
		}

		expression = initCall;
		return true;
	}

	bool TryRewriteDelegateInvocation(CallExpression call)
	{
		if (!TryCreateParamsComponentExpressions(call.Target, out List<Expression> components) || components.Count != 2)
		{
			if (!TryCreateDelegateComponentsFromExpandedCallTarget(call.Target, out components))
				return false;
		}
		if (components.Count != 2)
			return false;
		if (!TryGetCallableShape(components[0].ResolvedType, out CallableShape callable) || callable.Kind != "fn")
			return false;
		if (callable.Parameters.Count > 0 && call.Arguments.Count > 0 && call.Arguments[0].Value?.ResolvedType == callable.Parameters[0])
			return false;

		call.Target = components[0];
		call.Arguments.Insert(0, new ArgumentExpression
		{
			SourceSyntax = components[1].SourceSyntax,
			Value = components[1],
			ResolvedType = components[1].ResolvedType
		});
		call.ResolvedType = callable.ReturnType;
		return true;
	}

	bool TryCreateDelegateComponentsFromExpandedCallTarget(Expression? target, out List<Expression> components)
	{
		components = [];
		if (target is null
			|| !TryGetCallableShape(target.ResolvedType, out CallableShape callable)
			|| callable.Kind != "fn"
			|| callable.Parameters.Count == 0
			|| callable.Parameters[0] != "void*"
			|| !TryFindParamsExpansionSiblingFromExpansion(target, "context", out Expression? context)
			|| context is null)
		{
			return false;
		}

		components.Add(target);
		components.Add(context);
		return true;
	}

	bool TryRewriteIteratorProtocolInvocation(CallExpression call)
	{
		if (!TryCreateParamsComponentExpressions(call.Target, out List<Expression> components) || components.Count != 2)
			return false;
		if (!TryGetCallableShape(components[0].ResolvedType, out CallableShape callable) || callable.Kind != "fn" || callable.ReturnType != "bool" || callable.Parameters.Count < 2 || callable.Parameters[0] != "void*")
			return false;
		if (call.Arguments.Count > 0 && call.Arguments[0].Value?.ResolvedType == callable.Parameters[0])
			return false;

		call.Target = components[0];
		call.Arguments.Insert(0, new ArgumentExpression
		{
			SourceSyntax = components[1].SourceSyntax,
			Value = components[1],
			ResolvedType = components[1].ResolvedType
		});
		call.ResolvedType = callable.ReturnType;
		return true;
	}

	void AddImplicitDefaultArguments(CallExpression call)
	{
		FunctionDefinition? function = callTargets.TryGetValue(call, out FunctionDefinition? foundFunction) ? foundFunction : null;
		bool includeExplicitThis = function is not null && IncludeExplicitThisArgument(call.Target, function);
		List<ParameterDefinition> callableParameters = function is not null
			? GetCallableParametersForCall(function, includeExplicitThis)
			: callableInvocationParameters.TryGetValue(call, out List<ParameterDefinition>? foundParameters) ? GetCallableParameters(foundParameters) : [];
		if (callableParameters.Count == 0)
			return;
		if (HasProvidedExpandedComponentArgument(call.Arguments, callableParameters))
			callableParameters = ExpandCallableParametersForDefaultBinding(callableParameters);
		if (function is not null && (call.Arguments.Count > callableParameters.Count || HasExplicitHiddenArgument(call.Arguments) || HasWithinParameter(function) && call.Arguments.Exists(IsNullArgumentExpression)))
			AddExplicitHiddenParameters(function.Parameters, callableParameters);

		bool[] suppliedParameters = new bool[callableParameters.Count];
		ArgumentExpression?[] argumentsByParameter = new ArgumentExpression?[callableParameters.Count];
		HashSet<ArgumentExpression> boundArguments = [];
		foreach (ArgumentExpression argument in call.Arguments.ToArray())
		{
			if (!TryBindCallArgumentToParameter(argument, callableParameters, suppliedParameters, call.SourceSyntax, out int parameterIndex))
				continue;
			boundArguments.Add(argument);
			if (parameterIndex >= 0 && parameterIndex < argumentsByParameter.Length)
			{
				argumentsByParameter[parameterIndex] = argument;
				int consumedParameters = CountCallableParametersSatisfiedByArgument(argument, callableParameters, parameterIndex);
				for (int componentIndex = 1; componentIndex < consumedParameters; componentIndex++)
					MarkSuppliedParameter(suppliedParameters, parameterIndex + componentIndex);
			}
		}

		Dictionary<string, string> suppliedSourceText = BuildSuppliedArgumentSourceText(callableParameters, argumentsByParameter);
		List<ArgumentExpression> orderedArguments = [];
		for (int parameterIndex = 0; parameterIndex < callableParameters.Count; parameterIndex++)
		{
			ParameterDefinition parameter = callableParameters[parameterIndex];
			if (parameter is SizeOfParameterDefinition)
				continue;
			if (argumentsByParameter[parameterIndex] is ArgumentExpression supplied
				&& !(IsExplicitHiddenArgument(supplied) && IsImplicitOnlyCallParameter(parameter)))
			{
				orderedArguments.Add(supplied);
				int consumedParameters = CountCallableParametersSatisfiedByArgument(supplied, callableParameters, parameterIndex);
				parameterIndex += consumedParameters - 1;
				continue;
			}
			if (IsUponParameter(parameter) && GetFunctionUponParameter(currentRewriteFunction) is ParameterDefinition currentUpon)
			{
				orderedArguments.Add(new ArgumentExpression
				{
					SourceSyntax = call.SourceSyntax ?? parameter.SourceSyntax,
					Value = CreateVariableReference(currentUpon, currentUpon.ResolvedType ?? parameter.ResolvedType ?? ErrorType),
					ResolvedType = currentUpon.ResolvedType ?? parameter.ResolvedType
				});
				continue;
			}
			if (parameter.DefaultValue is null)
				continue;
			if (function is not null
				&& parameter.DefaultValue is NameOfExpression { Text: string text }
				&& text.Trim() == "classtype"
				&& TryGetClassTypeCallSiteName(call.Target, function, out string classTypeName))
			{
				Expression value = NameOfStringLiteral(BuildNameOfTypeValue(classTypeName), call.SourceSyntax ?? parameter.SourceSyntax);
				orderedArguments.Add(new ArgumentExpression
				{
					SourceSyntax = call.SourceSyntax ?? parameter.SourceSyntax,
					Value = value,
					ResolvedType = "string"
				});
				continue;
			}
			if (parameter.DefaultValue is UnaryExpression { Operator: UnaryOperator.FromEnd }
				&& parameterIndex > 0
				&& HasAttribute(callableParameters[parameterIndex - 1].Attributes, "@range"))
			{
				Expression? length = CreateLengthExpression(GetRangeReceiver(call.Target), call.SourceSyntax ?? parameter.SourceSyntax);
				if (length is null)
				{
					Report(GetRange(call.SourceSyntax ?? parameter.SourceSyntax), "Range count default requires an accessible length field, length property, or getLength() method.");
					continue;
				}

				Expression count = orderedArguments.Count > 0 && orderedArguments[^1].Value is Expression index
					? CreateRangeCountExpression(index, CreateFromEndExpression((UnaryExpression)parameter.DefaultValue, length), parameter.DefaultValue.SourceSyntax)
					: CreateFromEndExpression((UnaryExpression)parameter.DefaultValue, length);
				orderedArguments.Add(new ArgumentExpression
				{
					SourceSyntax = call.SourceSyntax ?? parameter.SourceSyntax,
					Value = count,
					ResolvedType = "nuint"
				});
				continue;
			}

			if (!TryCreateDefaultArgumentExpression(parameter.DefaultValue, parameter, call, suppliedSourceText, out Expression? defaultValue))
				continue;
			string? defaultType = defaultValue?.ResolvedType;
			orderedArguments.Add(new ArgumentExpression
			{
				SourceSyntax = call.SourceSyntax ?? parameter.SourceSyntax,
				Value = defaultValue,
				ResolvedType = IsInvalidDefaultArgumentType(defaultType) ? parameter.ResolvedType : defaultType ?? parameter.ResolvedType
			});
		}

		foreach (ArgumentExpression argument in call.Arguments)
			if (!orderedArguments.Contains(argument) && !boundArguments.Contains(argument))
				orderedArguments.Add(argument);
		call.Arguments.Clear();
		call.Arguments.AddRange(orderedArguments);
	}

	Dictionary<string, string> BuildSuppliedArgumentSourceText(List<ParameterDefinition> callableParameters, ArgumentExpression?[] argumentsByParameter)
	{
		Dictionary<string, string> result = new(StringComparer.Ordinal);
		for (int i = 0; i < callableParameters.Count && i < argumentsByParameter.Length; i++)
		{
			if (string.IsNullOrWhiteSpace(callableParameters[i].Name) || argumentsByParameter[i] is not ArgumentExpression argument)
				continue;
			result[callableParameters[i].Name!] = FormatSourceOfArgument(argument);
		}
		return result;
	}

	bool TryCreateDefaultArgumentExpression(Expression expression, ParameterDefinition parameter, CallExpression call, Dictionary<string, string> suppliedSourceText, out Expression? result)
	{
		result = expression switch
		{
			CallerSourceCaptureExpression caller => CreateCallerSourceCaptureArgument(caller, parameter, call),
			SourceOfExpression sourceOf => NameOfStringLiteral(suppliedSourceText.TryGetValue(sourceOf.ArgumentName, out string? text) ? text : "", call.SourceSyntax ?? parameter.SourceSyntax),
			ParenthesizedExpression parenthesized => TryCreateDefaultArgumentExpression(parenthesized.Expression ?? NullLiteral(), parameter, call, suppliedSourceText, out Expression? innerParenthesized)
				? new ParenthesizedExpression { SourceSyntax = parenthesized.SourceSyntax, Expression = innerParenthesized, ResolvedType = parenthesized.ResolvedType }
				: null,
			CastExpression cast => TryCreateDefaultArgumentExpression(cast.Expression ?? NullLiteral(), parameter, call, suppliedSourceText, out Expression? innerCast)
				? new CastExpression { SourceSyntax = cast.SourceSyntax, Kind = cast.Kind, Type = CloneType(cast.Type), Expression = innerCast, ResolvedType = cast.ResolvedType }
				: null,
			UnaryExpression unary => TryCreateDefaultArgumentExpression(unary.Operand ?? NullLiteral(), parameter, call, suppliedSourceText, out Expression? innerUnary)
				? new UnaryExpression { SourceSyntax = unary.SourceSyntax, Operator = unary.Operator, Context = CloneDefaultArgumentExpression(unary.Context), Operand = innerUnary, ResolvedType = unary.ResolvedType }
				: null,
			BinaryExpression binary => TryCreateDefaultArgumentExpression(binary.Left ?? NullLiteral(), parameter, call, suppliedSourceText, out Expression? left)
				&& TryCreateDefaultArgumentExpression(binary.Right ?? NullLiteral(), parameter, call, suppliedSourceText, out Expression? right)
				? new BinaryExpression { SourceSyntax = binary.SourceSyntax, Operator = binary.Operator, Left = left, Right = right, ResolvedType = binary.ResolvedType }
				: null,
			ConditionalExpression conditional => TryCreateDefaultArgumentExpression(conditional.Condition ?? NullLiteral(), parameter, call, suppliedSourceText, out Expression? condition)
				&& TryCreateDefaultArgumentExpression(conditional.WhenTrue ?? NullLiteral(), parameter, call, suppliedSourceText, out Expression? whenTrue)
				&& TryCreateDefaultArgumentExpression(conditional.WhenFalse ?? NullLiteral(), parameter, call, suppliedSourceText, out Expression? whenFalse)
				? new ConditionalExpression { SourceSyntax = conditional.SourceSyntax, Condition = condition, WhenTrue = whenTrue, WhenFalse = whenFalse, ResolvedType = conditional.ResolvedType }
				: null,
			_ => CloneDefaultArgumentExpression(expression)
		};
		return result is not null;
	}

	Expression? CreateCallerSourceCaptureArgument(CallerSourceCaptureExpression caller, ParameterDefinition parameter, CallExpression call)
	{
		SyntaxNode? syntax = call.SourceSyntax ?? parameter.SourceSyntax;
		return caller.Selector switch
		{
			CallerSourceCaptureSelector.SourceLine => NumberLiteral(GetCallSourceLine(call).ToString(System.Globalization.CultureInfo.InvariantCulture), "uint"),
			CallerSourceCaptureSelector.SourceFile => TryGetCallSourceFile(call, out string? sourceFile)
				? NameOfStringLiteral(sourceFile!, syntax)
				: null,
			CallerSourceCaptureSelector.FunctionName => NameOfStringLiteral(GetCurrentVisibleCallableName(), syntax),
			CallerSourceCaptureSelector.QualifiedName => NameOfStringLiteral(GetCurrentQualifiedCallableName(call), syntax),
			CallerSourceCaptureSelector.PropertyName => TryGetCurrentPropertyName(out string? propertyName)
				? NameOfStringLiteral(propertyName!, syntax)
				: ReportNotSuppliedCallerPropertyName(parameter, call),
			_ => null
		};
	}

	Expression? ReportNotSuppliedCallerPropertyName(ParameterDefinition parameter, CallExpression call)
	{
		Report(GetRange(call.SourceSyntax ?? call.Target?.SourceSyntax ?? parameter.SourceSyntax), $"Parameter '{parameter.Name}' default caller(propertyname) is not supplied outside a property accessor body.");
		return null;
	}

	int GetCallSourceLine(CallExpression call)
	{
		return (GetRange(call.SourceSyntax ?? call.Target?.SourceSyntax) ?? GetRange(call.Target?.SourceSyntax))?.StartLineNumber ?? 1;
	}

	bool TryGetCallSourceFile(CallExpression call, out string? sourceFile)
	{
		sourceFile = null;
		TokenRange? range = GetRange(call.SourceSyntax ?? call.Target?.SourceSyntax);
		if (range is not TokenRange tokenRange || currentModule is null || !currentModule.SourceFiles.TryGetValue(tokenRange.Sequence, out SourceFile? file))
			return false;
		string physicalPath = string.IsNullOrWhiteSpace(file.FullPath) ? file.Path : file.FullPath!;
		sourcefilePathMapper ??= new SourcefilePathMapper(currentModule.SourcefilePathMode, currentModule.SourcefileDefaultRoot, currentModule.SourcefileRoots);
		SourcefilePathMapResult result = sourcefilePathMapper.Map(physicalPath);
		if (result.Success)
		{
			sourceFile = result.Value;
			return true;
		}
		Report(GetRange(call.SourceSyntax ?? call.Target?.SourceSyntax), result.Diagnostic ?? "Could not map caller(sourcefile).");
		return false;
	}

	string GetCurrentVisibleCallableName()
	{
		FunctionDefinition? function = GetCurrentSourceCaptureFunction();
		if (function is null)
			return "";
		if (function.Modifier == FunctionModifier.Constructor)
			return CreateMethodName;
		if (IsDestructorFunction(function))
			return DestroyMethodName;
		return GetCallableName(function).TrimStart('~');
	}

	string GetCurrentQualifiedCallableName(CallExpression call)
	{
		FunctionDefinition? function = GetCurrentSourceCaptureFunction();
		if (function is null)
			return "";
		string name = GetCurrentVisibleCallableName();
		string prefix = "";
		TypeDefinition? containingType = FindContainingType(function) ?? (currentRewriteFunction is null ? null : FindContainingType(currentRewriteFunction));
		if (function.OutOfScopeOwnerName is not null)
			prefix = function.OutOfScopeOwnerName + ".";
		else if (containingType is not null)
			prefix = containingType.Name + ".";
		string? ns = function.Namespace ?? containingType?.Namespace;
		if (currentModule is not null
			&& currentModule.DefinitionSources.TryGetValue(function, out TokenSequence? source)
			&& source is not null
			&& currentModule.SourceNamespaces.TryGetValue(source, out string? foundNamespace))
			ns = foundNamespace;
		return (string.IsNullOrWhiteSpace(ns) ? "" : ns + "::") + prefix + name;
	}

	FunctionDefinition? GetCurrentSourceCaptureFunction()
	{
		if (currentRewriteFunction?.GeneratedInfo is { Category: GeneratedDeclarationCategory.Lifecycle, Source: FunctionDefinition source }
			&& (source.Modifier == FunctionModifier.Constructor || IsDestructorFunction(source)))
			return source;
		return currentRewriteFunction;
	}

	bool TryGetCurrentPropertyName(out string? propertyName)
	{
		propertyName = null;
		if (currentRewriteFunction is null)
			return false;
		string name = currentRewriteFunction.Name;
		if (name.StartsWith("get", StringComparison.Ordinal) && IsPropertyGetterFunction(currentRewriteFunction))
			propertyName = name["get".Length..];
		else if (name.StartsWith("set", StringComparison.Ordinal) && name.Length > "set".Length)
			propertyName = name["set".Length..];
		return !string.IsNullOrWhiteSpace(propertyName);
	}

	static string FormatSourceOfArgument(ArgumentExpression argument)
	{
		TokenRange? range = argument.SourceSyntax is ArgumentSyntax syntax && syntax.Expression is not null
			? GetFullSourceRange(syntax.Expression)
			: GetRange(argument.Value?.SourceSyntax ?? argument.SourceSyntax);
		return range is TokenRange tokenRange ? NormalizeSourceCaptureText(tokenRange) : "";
	}

	static TokenRange? GetFullSourceRange(SyntaxNode? syntax)
	{
		return SyntaxNodeTraversal.TryGetRange(syntax, out TokenRange range) ? range : null;
	}

	static string NormalizeSourceCaptureText(TokenRange range)
	{
		System.Text.StringBuilder builder = new();
		TokenValue? previous = null;
		for (int i = 0; i < range.Count; i++)
		{
			TokenValue token = range.Sequence.Values[range.Index + i];
			if (token.Class is TokenClass.LineComment or TokenClass.BlockComment)
				continue;
			if (token.Class is TokenClass.Whitespace or TokenClass.NewLine)
			{
				if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
					builder.Append(' ');
				continue;
			}
			if (previous is TokenValue prior && NeedsSourceCaptureSpace(prior, token) && builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
				builder.Append(' ');
			builder.Append(token.Value);
			previous = token;
		}
		return builder.ToString().Trim();
	}

	static bool NeedsSourceCaptureSpace(TokenValue previous, TokenValue current)
	{
		bool previousWord = previous.Class is TokenClass.Identifier or TokenClass.Number;
		bool currentWord = current.Class is TokenClass.Identifier or TokenClass.Number;
		return previousWord && currentWord;
	}

	bool HasProvidedExpandedComponentArgument(List<ArgumentExpression> arguments, List<ParameterDefinition> callableParameters)
	{
		int argumentIndex = 0;
		foreach (ParameterDefinition parameter in callableParameters)
		{
			if (argumentIndex >= arguments.Count)
				return false;
			if (TryGetParamsComponentShape(parameter.Type, parameter.ResolvedType, parameter.Name, out ParamsComponentShape shape)
				&& shape.Components.Count > 1
				&& argumentIndex + 1 < arguments.Count
				&& IsProvidedExpandedSecondComponent(arguments[argumentIndex].Value, arguments[argumentIndex + 1].Value, shape.Components[1]))
			{
				return true;
			}
			argumentIndex++;
		}

		return false;
	}

	bool IsProvidedExpandedSecondComponent(Expression? value, Expression? next, ParamsComponent component)
	{
		return component.SourceKind switch
		{
			ParamsComponentShapeKind.Array => IsProvidedLengthComponent(value, next, component.ExpandedName),
			ParamsComponentShapeKind.Delegate or ParamsComponentShapeKind.Iter => IsProvidedContextComponent(next, component.ExpandedName),
			ParamsComponentShapeKind.Optional => IsProvidedComponent(next, component.ExpandedName),
			_ => false
		};
	}

	List<ParameterDefinition> ExpandCallableParametersForDefaultBinding(List<ParameterDefinition> callableParameters)
	{
		List<ParameterDefinition> expanded = [];
		foreach (ParameterDefinition parameter in callableParameters)
		{
			if (!TryGetParamsComponentShape(parameter.Type, parameter.ResolvedType, parameter.Name, out ParamsComponentShape shape)
				|| shape.Components.Count <= 1)
			{
				expanded.Add(parameter);
				continue;
			}

			foreach (ParamsComponent component in shape.Components)
			{
				expanded.Add(new ParameterDefinition
				{
					SourceSyntax = parameter.SourceSyntax,
					Name = component.ExpandedName,
					Symbol = component.ExpandedName,
					Modifier = parameter.Modifier,
					Type = TypeReferenceForResolvedName(component.Type),
					ResolvedType = component.Type
				});
			}
		}
		return expanded;
	}

	static ParameterDefinition? GetFunctionUponParameter(FunctionDefinition? function)
	{
		if (function is null)
			return null;
		foreach (ParameterDefinition parameter in function.Parameters)
			if (IsUponParameter(parameter))
				return parameter;
		return null;
	}

	int CountCallableParametersSatisfiedByArgument(ArgumentExpression argument, List<ParameterDefinition> callableParameters, int parameterIndex)
	{
		if (!TryGetArgumentParamsComponentShape(argument, out ParamsComponentShape shape) || shape.Components.Count <= 1)
		{
			if (parameterIndex + 1 < callableParameters.Count
				&& ParametersLookLikeDelegateCallContextPair(callableParameters, parameterIndex)
				&& ArgumentCouldExpandToDelegate(argument))
				return 2;

			if (parameterIndex + 1 < callableParameters.Count
				&& ParametersLookLikeExpandedArrayComponents(callableParameters, parameterIndex)
				&& ArgumentCouldExpandToArray(argument))
				return 2;

			return 1;
		}
		if (parameterIndex + shape.Components.Count > callableParameters.Count)
			return 1;

		if (shape.Kind == ParamsComponentShapeKind.Array
			&& shape.Components.Count == 2
			&& ParameterLooksLikeArrayElementComponent(callableParameters[parameterIndex])
			&& ParameterLooksLikeArrayLengthComponent(callableParameters[parameterIndex + 1]))
			return 2;

		for (int i = 0; i < shape.Components.Count; i++)
		{
			string componentType = shape.Components[i].Type;
			string parameterType = callableParameters[parameterIndex + i].ResolvedType ?? ErrorType;
			if (componentType != parameterType)
				return 1;
		}

		return shape.Components.Count;
	}

	bool ParametersLookLikeDelegateCallContextPair(List<ParameterDefinition> callableParameters, int parameterIndex)
	{
		if (!TryGetCallableShape(callableParameters[parameterIndex].ResolvedType, out CallableShape shape)
			|| shape.Kind != "fn" || shape.Parameters.Count == 0 || shape.Parameters[0] != "void*")
			return false;

		if (callableParameters[parameterIndex + 1].ResolvedType != "void*")
			return false;

		if (callableParameters[parameterIndex + 1].Name != callableParameters[parameterIndex].Name + "_context")
			return false;

		return true;
	}

	bool ArgumentCouldExpandToDelegate(ArgumentExpression argument)
	{
		if (argument.Value is null)
			return false;

		string type = argument.Value.ResolvedType ?? argument.ResolvedType ?? "";
		if (TryGetCallableShape(type, out CallableShape shape) && shape.Kind == "fn")
			return true;

		if (TryGetParamsComponentShape(null, type, "value", out ParamsComponentShape componentShape)
			&& componentShape.Kind == ParamsComponentShapeKind.Delegate)
			return true;

		return false;
	}

	bool ParametersLookLikeExpandedArrayComponents(List<ParameterDefinition> callableParameters, int parameterIndex)
	{
		return ParameterLooksLikeArrayElementComponent(callableParameters[parameterIndex])
			&& ParameterLooksLikeArrayLengthComponent(callableParameters[parameterIndex + 1]);
	}

	bool ArgumentCouldExpandToArray(ArgumentExpression argument)
	{
		string type = argument.Value?.ResolvedType ?? argument.ResolvedType ?? "";
		if (string.IsNullOrWhiteSpace(type))
			return false;

		if (TryGetParamsComponentShape(null, type, "value", out ParamsComponentShape shape)
			&& shape.Kind == ParamsComponentShapeKind.Array
			&& shape.Components.Count == 2)
			return true;

		return false;
	}

	bool ParameterLooksLikeArrayElementComponent(ParameterDefinition parameter)
	{
		string type = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? "";
		type = StripTopLevelValueQualifiers(type);
		return type.EndsWith("*", StringComparison.Ordinal) || IsGenericPlaceholderParameter(type);
	}

	static bool ParameterLooksLikeArrayLengthComponent(ParameterDefinition parameter)
	{
		string type = StripTopLevelValueQualifiers(parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? "");
		return type is "nuint" or "nint" or "uint" or "int" or "ulong" or "long" or "ushort" or "short";
	}

	bool TryGetArgumentParamsComponentShape(ArgumentExpression argument, out ParamsComponentShape shape)
	{
		if (TryGetParamsComponentShape(null, argument.ResolvedType, "value", out shape))
			return true;
		if (argument.Value is not null && TryGetParamsComponentShape(null, argument.Value.ResolvedType, "value", out shape))
			return true;

		shape = new ParamsComponentShape(ParamsComponentShapeKind.Array, "", []);
		return false;
	}

	Expression? CloneDefaultArgumentExpression(Expression? expression)
	{
		if (expression is not null && expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(rewritten, expression))
			return CloneDefaultArgumentExpression(rewritten);

		return expression switch
		{
			null => null,
			LiteralExpression literal => new LiteralExpression { SourceSyntax = literal.SourceSyntax, Kind = literal.Kind, Text = literal.Text, Value = literal.Value, ResolvedType = literal.ResolvedType },
			DefaultExpression defaultExpression => new DefaultExpression { SourceSyntax = defaultExpression.SourceSyntax, ResolvedType = defaultExpression.ResolvedType },
			DefaultWithinContextExpression defaultWithin => new DefaultWithinContextExpression { SourceSyntax = defaultWithin.SourceSyntax, ResolvedType = defaultWithin.ResolvedType },
			NamedExpression named => CloneNamedExpression(named),
			VariableReferenceExpression variable => new VariableReferenceExpression { SourceSyntax = variable.SourceSyntax, Variable = variable.Variable, ResolvedType = variable.ResolvedType },
			TypeReferenceExpression type => new TypeReferenceExpression { SourceSyntax = type.SourceSyntax, Type = CloneType(type.Type), ResolvedType = type.ResolvedType },
			CallerSourceCaptureExpression caller => new CallerSourceCaptureExpression { SourceSyntax = caller.SourceSyntax, Selector = caller.Selector, ResolvedType = caller.ResolvedType },
			SourceOfExpression sourceOf => new SourceOfExpression { SourceSyntax = sourceOf.SourceSyntax, ArgumentName = sourceOf.ArgumentName, ResolvedType = sourceOf.ResolvedType },
			ParenthesizedExpression parenthesized => new ParenthesizedExpression { SourceSyntax = parenthesized.SourceSyntax, Expression = CloneDefaultArgumentExpression(parenthesized.Expression), ResolvedType = parenthesized.ResolvedType },
			CastExpression cast => new CastExpression { SourceSyntax = cast.SourceSyntax, Kind = cast.Kind, Type = CloneType(cast.Type), Expression = CloneDefaultArgumentExpression(cast.Expression), ResolvedType = cast.ResolvedType },
			UnaryExpression unary => new UnaryExpression { SourceSyntax = unary.SourceSyntax, Operator = unary.Operator, Operand = CloneDefaultArgumentExpression(unary.Operand), ResolvedType = unary.ResolvedType },
			BinaryExpression binary => new BinaryExpression { SourceSyntax = binary.SourceSyntax, Operator = binary.Operator, Left = CloneDefaultArgumentExpression(binary.Left), Right = CloneDefaultArgumentExpression(binary.Right), ResolvedType = binary.ResolvedType },
			MemberExpression member => new MemberExpression { SourceSyntax = member.SourceSyntax, Target = CloneDefaultArgumentExpression(member.Target), Name = member.Name, ResolvedType = member.ResolvedType },
			MemberReferenceExpression member => new MemberReferenceExpression { SourceSyntax = member.SourceSyntax, Target = CloneDefaultArgumentExpression(member.Target), Name = member.Name, Member = member.Member, ResolvedType = member.ResolvedType },
			_ => expression
		};
	}

	static bool IsInvalidDefaultArgumentType(string? type)
	{
		return string.IsNullOrWhiteSpace(type)
			|| type == ErrorType
			|| type == UnresolvedType
			|| type.StartsWith(UnresolvedType + "(", StringComparison.Ordinal);
	}

	bool TryRewritePropertySetterAssignment(AssignmentExpression assignment, out Expression? rewritten)
	{
		rewritten = null;
		Expression? target = assignment.Target is null ? null : RewriteExpression(assignment.Target);
		switch (target)
		{
			case MemberReferenceExpression setter when IsPropertySetterReference(setter):
				rewritten = RewritePropertySetterAssignment(setter, [], assignment);
				return true;

			case IndexExpression { Target: MemberReferenceExpression setter } index when IsPropertySetterReference(setter):
				rewritten = RewritePropertySetterAssignment(setter, index.Arguments, assignment);
				return true;

			case IndexExpression { Target: not null } index when RewriteExpression(index.Target) is MemberReferenceExpression setter && IsPropertySetterReference(setter):
				rewritten = RewritePropertySetterAssignment(setter, index.Arguments, assignment);
				return true;

			default:
				return false;
		}
	}

	bool TryRewritePropertySetterAssignmentStatement(AssignmentExpression assignment, out Expression? rewritten)
	{
		rewritten = null;
		Expression? target = assignment.Target is null ? null : RewriteExpression(assignment.Target);
		switch (target)
		{
			case MemberReferenceExpression setter when IsPropertySetterReference(setter):
				rewritten = RewritePropertySetterCall(setter, [], assignment.Value);
				return true;

			case IndexExpression { Target: MemberReferenceExpression setter } index when IsPropertySetterReference(setter):
				rewritten = RewritePropertySetterCall(setter, index.Arguments, assignment.Value);
				return true;

			case IndexExpression { Target: not null } index when RewriteExpression(index.Target) is MemberReferenceExpression setter && IsPropertySetterReference(setter):
				rewritten = RewritePropertySetterCall(setter, index.Arguments, assignment.Value);
				return true;

			default:
				return false;
		}
	}

	bool TryRewriteDiscardedPropertySetterAssignment(Expression? expression, out Expression? rewritten)
	{
		rewritten = null;
		while (expression is ParenthesizedExpression parenthesized)
			expression = parenthesized.Expression;

		return expression is AssignmentExpression assignment
			&& TryRewritePropertySetterAssignmentStatement(assignment, out rewritten);
	}

	Expression RewritePropertySetterAssignment(MemberReferenceExpression setter, List<ArgumentExpression> arguments, AssignmentExpression assignment)
	{
		if (currentStatementPrefix is null || assignment.Value is null)
			return RewritePropertySetterCall(setter, arguments, assignment.Value);

		string valueType = assignment.Value.ResolvedType ?? assignment.ResolvedType ?? ErrorType;
		Expression loweredValue = LowerExpression(assignment.Value) ?? assignment.Value;
		DeclarationStatement valueLocal = CreateGeneratedLocal(NewGeneratedLocalName("propertyValue"), valueType, TypeReferenceForResolvedName(valueType), loweredValue);
		currentStatementPrefix.Add(valueLocal);

		VariableReferenceExpression valueReference = CreateVariableReference(valueLocal.Target, valueType);
		Expression setterCall = RewritePropertySetterCall(setter, arguments, valueReference);
		GroupedExpression grouped = new()
		{
			SourceSyntax = assignment.SourceSyntax,
			ResolvedType = assignment.ResolvedType ?? valueType
		};
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = setterCall,
			ResolvedType = setterCall.ResolvedType
		});
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = CreateVariableReference(valueLocal.Target, valueType),
			ResolvedType = valueType
		});
		return grouped;
	}

}
