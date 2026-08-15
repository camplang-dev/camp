using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	bool TryRewriteInstanceInvocation(CallExpression call)
	{
		if (call.Target is not MemberReferenceExpression { Target: Expression receiver, Member: FunctionDefinition function } member)
			return false;
		if (!IsInstanceInvocationFunction(function))
			return false;
		if (IsPropertyGetterReference(member) || IsPropertySetterReference(member))
			return false;
		if (FindContainingType(function) is InterfaceDefinition)
			return false;

		RewriteInstanceInvocation(call, member, receiver, function);
		return true;
	}

	bool TryRewriteStaticMemberInvocation(CallExpression call)
	{
		if (call.Target is not MemberReferenceExpression { Member: FunctionDefinition function } member)
			return false;
		if (IsInstanceInvocationFunction(function))
			return false;
		if (IsPropertyGetterReference(member) || IsPropertySetterReference(member))
			return false;

		EnsureFlattenedFunctionSymbol(function);
		MethodReferenceExpression reference = new()
		{
			SourceSyntax = member.SourceSyntax,
			ResolvedType = BuildFunctionValueType(function, isInstance: false)
		};
		reference.Candidates.Add(function);
		call.Target = reference;
		return true;
	}

	bool IsInstanceInvocationFunction(FunctionDefinition function)
	{
		return IsInstanceFunction(function)
			|| GetExplicitThisParameter(function) is not null
			|| HasExpandedThisParameters(function.Parameters);
	}

	bool ShouldEmitFlattenedInstanceCalls()
	{
		return currentRewriteFunction is not null;
	}

	void RewriteInstanceInvocation(CallExpression call, MemberReferenceExpression member, Expression receiver, FunctionDefinition function)
	{
		call.Target = CreateFlattenedMethodReference(member, receiver, function);
		call.Arguments.InsertRange(0, CreateReceiverArguments(receiver, function));
	}

	bool TryRewriteGroupedMethodInvocation(CallExpression call, FunctionDefinition function)
	{
		if (call.Target is not GroupedExpression grouped
			|| grouped.Items.Count == 0
			|| grouped.Items[0].Expression is not MethodReferenceExpression method
			|| method.Candidates.Count != 1
			|| !ReferenceEquals(method.Candidates[0], function))
		{
			return false;
		}

		call.Target = method;
		for (int i = 1; i < grouped.Items.Count; i++)
		{
			Expression? expression = grouped.Items[i].Expression;
			if (expression is null)
				return false;
			call.Arguments.Insert(i - 1, new ArgumentExpression
			{
				SourceSyntax = expression.SourceSyntax,
				Value = expression,
				ResolvedType = expression.ResolvedType
			});
		}
		return true;
	}

	Expression RewriteInstanceMethodDelegate(MemberReferenceExpression member)
	{
		GroupedExpression grouped = new()
		{
			SourceSyntax = member.SourceSyntax,
			ResolvedType = member.ResolvedType
		};

		if (TryCreateInterfaceMethodDelegateComponents(member, out List<Expression> interfaceComponents))
		{
			foreach (Expression component in interfaceComponents)
			{
				grouped.Items.Add(new GroupedExpressionItem
				{
					Expression = component,
					ResolvedType = component.ResolvedType
				});
			}
			return grouped;
		}

		FunctionDefinition function = (FunctionDefinition)member.Member!;
		Expression receiver = member.Target!;

		if (TryCreateExpandedReceiverMethodDelegateComponents(member, function, out List<Expression> expandedReceiverComponents, arraysOnly: true))
		{
			foreach (Expression component in expandedReceiverComponents)
			{
				grouped.Items.Add(new GroupedExpressionItem
				{
					Expression = component,
					ResolvedType = component.ResolvedType
				});
			}
			return grouped;
		}

		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = CreateFlattenedMethodReference(member, receiver, function),
			ResolvedType = BuildFlattenedFunctionValueType(function, BuildFlattenedReceiverType(function, receiver.ResolvedType ?? ErrorType))
		});
		foreach (ArgumentExpression receiverArgument in CreateReceiverArguments(receiver, function))
		{
			grouped.Items.Add(new GroupedExpressionItem
			{
				Expression = receiverArgument.Value,
				ResolvedType = receiverArgument.ResolvedType
			});
		}
		return grouped;
	}

	List<ArgumentExpression> CreateReceiverArguments(Expression receiver, FunctionDefinition function)
	{
		if (GetExplicitThisParameter(function) is ThisParameterDefinition thisParameter
			&& TryGetParamsComponentShape(thisParameter.Type, thisParameter.ResolvedType, thisParameter.Name, out ParamsComponentShape shape)
			&& TryCreateReceiverComponentExpressions(receiver, shape, out List<Expression> components))
		{
			return CreateReceiverArguments(receiver, components);
		}

		if (TryGetExpandedThisParameterNames(function, out List<string> names)
			&& TryCreateReceiverComponentExpressions(receiver, names, out components))
		{
			return CreateReceiverArguments(receiver, components);
		}

		return [CreateReceiverArgument(receiver, function)];
	}

	List<ArgumentExpression> CreateReceiverArguments(Expression receiver, List<Expression> components)
	{
		List<ArgumentExpression> arguments = [];
		foreach (Expression component in components)
		{
			arguments.Add(new ArgumentExpression
			{
				SourceSyntax = receiver.SourceSyntax,
				Value = component,
				ResolvedType = component.ResolvedType
			});
		}

		return arguments;
	}

	bool TryCreateReceiverComponentExpressions(Expression receiver, ParamsComponentShape shape, out List<Expression> components)
	{
		if (TryCreateParamsComponentExpressions(receiver, out components) && components.Count == shape.Components.Count)
		{
			if (ShapeUsesPointerComponents(shape))
				components = CreateAddressOfComponents(components);
			return true;
		}

		if (receiver is ThisExpression or VariableReferenceExpression { Variable: ParameterDefinition { Name: "this" } })
		{
			if (TryCreateCurrentThisParameterComponents(shape, out components))
			{
				if (ShapeUsesPointerComponents(shape))
					components = CreateAddressOfComponents(components);
				return true;
			}
			if (TryCreateReceiverComponentExpressionsFromShape(receiver, shape, out components))
			{
				if (ShapeUsesPointerComponents(shape))
					components = CreateAddressOfComponents(components);
				return true;
			}
		}

		return false;
	}

	static bool ShapeUsesPointerComponents(ParamsComponentShape shape)
	{
		return shape.Components.Count == 2
			&& TryGetPointerElementType(shape.Components[0].Type) is string firstElementType
			&& TryGetPointerElementType(firstElementType) is not null
			&& TryGetPointerElementType(shape.Components[1].Type) == "nuint";
	}

	static List<Expression> CreateAddressOfComponents(List<Expression> components)
	{
		List<Expression> addresses = [];
		foreach (Expression component in components)
		{
			addresses.Add(new UnaryExpression
			{
				SourceSyntax = component.SourceSyntax,
				Operator = UnaryOperator.AddressOf,
				Operand = component,
				ResolvedType = AddPointer(component.ResolvedType ?? ErrorType)
			});
		}
		return addresses;
	}

	bool TryCreateReceiverComponentExpressions(Expression receiver, IReadOnlyList<string> names, out List<Expression> components)
	{
		if (names.Count == 2
			&& names[0] == "this_call"
			&& names[1] == "this_context"
			&& receiver is CallExpression iteratorFactory
			&& currentStatementPrefix is not null
			&& TryCreateIteratorFactoryProtocolComponents(iteratorFactory, receiver.SourceSyntax, currentStatementPrefix, out List<Expression>? iteratorComponents)
			&& iteratorComponents is not null)
		{
			components = iteratorComponents;
			return true;
		}

		if (TryCreateParamsComponentExpressions(receiver, out components) && components.Count == names.Count)
			return true;

		if (receiver is ThisExpression or VariableReferenceExpression { Variable: ParameterDefinition { Name: "this" } })
		{
			if (TryCreateCurrentThisParameterComponents(names, out components))
				return true;
			if (TryCreateReceiverComponentExpressionsFromNames(receiver, names, out components))
				return true;
		}

		return false;
	}

	bool TryCreateReceiverComponentExpressionsFromShape(Expression receiver, ParamsComponentShape shape, out List<Expression> components)
	{
		components = [];
		if (shape.Components.Count < 2)
			return false;

		components.Add(receiver);
		for (int i = 1; i < shape.Components.Count; i++)
		{
			components.Add(new MemberExpression
			{
				SourceSyntax = receiver.SourceSyntax,
				Target = CloneParamsExpansionExpression(receiver),
				Name = shape.Components[i].Name,
				ResolvedType = shape.Components[i].Type
			});
		}
		return true;
	}

	bool TryCreateReceiverComponentExpressionsFromNames(Expression receiver, IReadOnlyList<string> names, out List<Expression> components)
	{
		components = [];
		if (names.Count < 2 || names[0] is not ("this" or "this_call"))
			return false;

		components.Add(receiver);
		for (int i = 1; i < names.Count; i++)
		{
			string name = names[i];
			string componentName = name.StartsWith("this_", System.StringComparison.Ordinal) ? name["this_".Length..] : name;
			components.Add(new MemberExpression
			{
				SourceSyntax = receiver.SourceSyntax,
				Target = CloneParamsExpansionExpression(receiver),
				Name = componentName,
				ResolvedType = ErrorType
			});
		}
		return true;
	}

	static bool TryGetExpandedThisParameterNames(FunctionDefinition function, out List<string> names)
	{
		names = [];
		if (function.Parameters.Count == 0)
			return false;

		ParameterDefinition first = function.Parameters[0];
		if (first.Name is not ("this" or "this_call"))
			return false;

		names.Add(first.Name);
		for (int i = 1; i < function.Parameters.Count; i++)
		{
			string name = function.Parameters[i].Name;
			if (!name.StartsWith("this_", System.StringComparison.Ordinal))
				break;
			names.Add(name);
		}

		return names.Count > 1;
	}

	MethodReferenceExpression CreateFlattenedMethodReference(MemberReferenceExpression member, Expression receiver, FunctionDefinition function)
	{
		EnsureFlattenedFunctionSymbol(function);
		MethodReferenceExpression reference = new()
		{
			SourceSyntax = member.SourceSyntax,
			ResolvedType = BuildFlattenedFunctionValueType(function, BuildFlattenedReceiverType(function, receiver.ResolvedType ?? ErrorType))
		};
		reference.Candidates.Add(function);
		return reference;
	}

	ArgumentExpression CreateReceiverArgument(Expression receiver, FunctionDefinition function)
	{
		Expression value = receiver;
		string receiverValueType = GetReceiverValueType(receiver);
		string flattenedReceiverType = BuildFlattenedReceiverType(function, receiverValueType);
		string addressDecisionType = receiverValueType;
		if (receiver is VariableReferenceExpression { Variable: ParameterDefinition parameter }
			&& (parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition)
			&& TryGetPointerElementType(flattenedReceiverType) == receiverValueType)
		{
			receiver.ResolvedType = flattenedReceiverType;
			receiverValueType = flattenedReceiverType;
			addressDecisionType = flattenedReceiverType;
		}
		if (GetExplicitThisParameter(function)?.Modifier == ParameterModifier.In && TryGetPointerElementType(addressDecisionType) is null)
		{
			if (!CanTakeReceiverAddress(receiver) && currentStatementPrefix is not null)
			{
				DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("receiver"), receiverValueType, TypeReferenceForResolvedName(receiverValueType), receiver);
				currentStatementPrefix.Add(local);
				receiver = CreateVariableReference(local.Target, local.Target.ResolvedType ?? receiverValueType);
			}
			value = new UnaryExpression
			{
				SourceSyntax = receiver.SourceSyntax,
				Operator = UnaryOperator.AddressOf,
				Operand = receiver,
				ResolvedType = AddPointer(receiver.ResolvedType ?? receiverValueType)
			};
		}
		else if (TryGetPointerElementType(flattenedReceiverType) is not null && TryGetPointerElementType(addressDecisionType) is null)
		{
			if (!CanTakeReceiverAddress(receiver) && currentStatementPrefix is not null)
			{
				DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("receiver"), receiverValueType, TypeReferenceForResolvedName(receiverValueType), receiver);
				currentStatementPrefix.Add(local);
				receiver = CreateVariableReference(local.Target, local.Target.ResolvedType ?? receiverValueType);
			}
			string addressType = AddPointer(receiver.ResolvedType ?? receiverValueType);
			value = new UnaryExpression
			{
				SourceSyntax = receiver.SourceSyntax,
				Operator = UnaryOperator.AddressOf,
				Operand = receiver,
				ResolvedType = IsClassPointerUpcast(addressType, flattenedReceiverType) ? addressType : flattenedReceiverType
			};
		}
		if (value.ResolvedType is string valueType
			&& valueType != flattenedReceiverType
			&& ShouldCastFlattenedReceiver(valueType, flattenedReceiverType))
		{
			value = new CastExpression
			{
				SourceSyntax = receiver.SourceSyntax,
				Kind = CastKind.Type,
				Type = TypeReferenceForResolvedName(flattenedReceiverType),
				Expression = value,
				ResolvedType = flattenedReceiverType
			};
		}
		if (function.AbiThisType is not null
			&& !string.IsNullOrWhiteSpace(function.AbiThisType.ResolvedType)
			&& value.ResolvedType != function.AbiThisType.ResolvedType)
		{
			value = new CastExpression
			{
				SourceSyntax = receiver.SourceSyntax,
				Kind = CastKind.Type,
				Type = CloneType(function.AbiThisType),
				Expression = value,
				ResolvedType = function.AbiThisType.ResolvedType
			};
		}

		return new ArgumentExpression
		{
			SourceSyntax = receiver.SourceSyntax,
			Value = value,
			ResolvedType = value.ResolvedType
		};
	}

	static bool CanTakeReceiverAddress(Expression receiver)
	{
		return receiver switch
		{
			ThisExpression => true,
			VariableReferenceExpression => true,
			MemberReferenceExpression => true,
			MemberExpression => true,
			TypeReferenceExpression => true,
			IndexExpression => true,
			UnaryExpression { Operator: UnaryOperator.PointerDereference } => true,
			_ => false
		};
	}

	bool ShouldCastFlattenedReceiver(string valueType, string flattenedReceiverType)
	{
		if (TryGetPointerElementType(valueType) is null || TryGetPointerElementType(flattenedReceiverType) is null)
			return false;
		if (TryGetParamsComponentShape(null, valueType, "value", out ParamsComponentShape valueShape) && valueShape.Components.Count > 1)
			return false;
		if (TryGetParamsComponentShape(null, flattenedReceiverType, "value", out ParamsComponentShape flattenedShape) && flattenedShape.Components.Count > 1)
			return false;
		return CanImplicitlyConvert(valueType, flattenedReceiverType);
	}

	static string GetReceiverValueType(Expression receiver)
	{
		return receiver switch
		{
			VariableReferenceExpression { Variable: DeclarationTarget { Type.ResolvedType: string declarationType } } => declarationType,
			VariableReferenceExpression { Variable: VariableDefinition { Type.ResolvedType: string variableType } } => variableType,
			VariableReferenceExpression { Variable: ParameterDefinition { Type.ResolvedType: string parameterType } } => parameterType,
			VariableReferenceExpression { Variable.ResolvedType: string variableType } => variableType,
			MemberReferenceExpression { Member: FieldDefinition { Type.ResolvedType: string fieldType } } => fieldType,
			MemberReferenceExpression { Member.ResolvedType: string memberType } => memberType,
			_ => receiver.ResolvedType ?? ErrorType
		};
	}

	string BuildFlattenedReceiverType(FunctionDefinition function, string receiverType)
	{
		if (function.AbiThisType?.ResolvedType is string abiThisType && !string.IsNullOrWhiteSpace(abiThisType))
			return abiThisType;
		return BuildEffectiveReceiverType(receiverType, function, isPropertyGetterSyntax: false);
	}

	void EnsureFlattenedFunctionSymbol(FunctionDefinition function)
	{
		if (function.SymbolOverridden)
			return;

		if (!string.IsNullOrWhiteSpace(function.Symbol) && function.Symbol != function.Name)
			return;

		if (GetExplicitThisParameter(function) is ThisParameterDefinition thisParameter)
		{
			function.Symbol = BuildExtensionFunctionSymbol(GetCallableName(function), thisParameter.ResolvedType ?? ErrorType, function);
			return;
		}

		if (FindContainingType(function) is TypeDefinition type)
			function.Symbol = $"{EffectiveTypeSymbol(type)}_{GetCallableName(function).TrimStart('~')}";
	}

	string BuildFlattenedFunctionValueType(FunctionDefinition function, string receiverType)
	{
		Dictionary<string, string> substitutions = [];
		AddReceiverTypeGenericSubstitutions(receiverType, function, substitutions);
		List<string> parameters = [receiverType];
		foreach (ParameterDefinition parameter in GetCallableParameters(function.Parameters))
		{
			string parameterType = parameter.ResolvedType ?? ErrorType;
			if (substitutions.Count > 0)
				parameterType = SubstituteGenericType(parameterType, substitutions);
			parameters.Add(parameter.Modifier switch
			{
				ParameterModifier.In => "in " + parameterType,
				ParameterModifier.Out => "out " + parameterType,
				ParameterModifier.Thrown => "thrown " + parameterType,
				ParameterModifier.Within => "within " + parameterType,
				ParameterModifier.Upon => "upon " + parameterType,
				ParameterModifier.Prep => "prep " + parameterType,
				_ => parameterType
			});
		}

		string returnType = substitutions.Count > 0
			? SubstituteGenericType(function.ResolvedType ?? ErrorType, substitutions)
			: function.ResolvedType ?? ErrorType;
		return BuildCallableType("fn", returnType, parameters);
	}
}
