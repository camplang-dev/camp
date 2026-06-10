using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void ExpandParamsArguments(CallExpression call)
	{
		List<ParameterDefinition>? callableParameters = callTargets.TryGetValue(call, out FunctionDefinition? function)
			? GetCallableParameters(function.Parameters, IncludeExplicitThisArgument(call.Target, function))
			: null;
		ExpandParamsArguments(call.Arguments, callableParameters);
	}

	void ExpandParamsArguments(List<ArgumentExpression> arguments)
	{
		ExpandParamsArguments(arguments, callableParameters: null);
	}

	void ExpandParamsArguments(List<ArgumentExpression> arguments, List<ParameterDefinition>? callableParameters)
	{
		for (int i = 0; i < arguments.Count; i++)
		{
			ArgumentExpression argument = arguments[i];
			if (!TryCreateParamsComponentExpressions(argument.Value, out List<Expression> components)
				&& !TryCreateTargetTypedExpandedReturnArgumentComponents(argument, out components))
			{
				if (PrimitiveStringArrayLengthAlreadyProvided(arguments, callableParameters, i)
					|| !TryCreateLiftedOptionalArgumentComponents(argument, out components)
						&& !TryCreateIteratorToProtocolArgumentComponents(argument, callableParameters, i, out components)
						&& !TryCreateFunctionToDelegateArgumentComponents(argument, callableParameters, i, out components)
						&& !TryCreatePrimitiveStringToArrayArgumentComponents(argument, callableParameters, i, out components))
					continue;
			}

			arguments.RemoveAt(i);
			for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
			{
				Expression component = components[componentIndex];
				arguments.Insert(i + componentIndex, new ArgumentExpression
				{
					SourceSyntax = argument.SourceSyntax,
					Modifier = argument.Modifier,
					Value = component,
					ResolvedType = component.ResolvedType
				});
			}
			i += components.Count - 1;
		}
	}

	bool TryCreateTargetTypedExpandedReturnArgumentComponents(ArgumentExpression argument, out List<Expression> components)
	{
		components = [];
		if (argument.Value is not CallExpression call)
			return false;
		if (!TryGetParamsComponentShape(null, argument.ResolvedType, "value", out ParamsComponentShape shape) || shape.Components.Count <= 1)
			return false;
		return TryCreateExpandedReturnCallComponents(call, shape, out components);
	}

	bool PrimitiveStringArrayLengthAlreadyProvided(List<ArgumentExpression> arguments, List<ParameterDefinition>? callableParameters, int index)
	{
		if (callableParameters is null || index + 1 >= arguments.Count || index + 1 >= callableParameters.Count)
			return false;
		if (arguments[index + 1].Modifier != ArgumentModifier.None)
			return false;
		if (IsExplicitHiddenArgument(arguments[index + 1]))
			return false;
		Expression? value = arguments[index].Value;
		if (value is null || GetPrimitiveStringElementType(value.ResolvedType ?? arguments[index].ResolvedType) is not string stringElement)
			return false;
		if (!PrimitiveStringArrayArgumentTargetMatches(callableParameters, index, stringElement))
			return false;

		string lengthType = callableParameters[index + 1].ResolvedType ?? "";
		return StripTopLevelValueQualifiers(lengthType) is "nuint" or "nint" or "uint" or "int" or "ulong" or "long" or "ushort" or "short";
	}

	bool TryCreatePrimitiveStringToArrayArgumentComponents(ArgumentExpression argument, List<ParameterDefinition>? callableParameters, int index, out List<Expression> components)
	{
		components = [];
		if (argument.Value is null || callableParameters is null || index >= callableParameters.Count)
			return false;
		if (TryGetParamsComponentShape(null, argument.Value.ResolvedType, "value", out _))
			return false;
		if (GetPrimitiveStringElementType(argument.Value.ResolvedType ?? argument.ResolvedType) is not string stringElement)
			return false;
		if (!PrimitiveStringArrayArgumentTargetMatches(callableParameters, index, stringElement))
			return false;

		Expression? length = CreateLengthExpression(argument.Value, argument.SourceSyntax);
		if (length is null)
			return false;
		length = LowerExpression(length) ?? length;

		components.Add(argument.Value);
		components.Add(length);
		return true;
	}

	bool PrimitiveStringArrayArgumentTargetMatches(List<ParameterDefinition> callableParameters, int index, string stringElement)
	{
		if (CanConvertPrimitiveStringToConstArray(stringElement switch
			{
				"wchar" => "wstring",
				"achar" => "astring",
				_ => "string"
			}, callableParameters[index].ResolvedType ?? ErrorType))
			return true;

		if (index + 1 >= callableParameters.Count)
			return false;
		if (callableParameters[index].ResolvedType is not string pointerType || TryGetPointerElementType(pointerType) is not string pointerElement)
			return false;
		if (StripTopLevelValueQualifiers(pointerElement) != stringElement || !IsConstQualified(pointerElement))
			return false;
		string lengthType = callableParameters[index + 1].ResolvedType ?? "";
		return StripTopLevelValueQualifiers(lengthType) is "nuint" or "nint" or "uint" or "int" or "ulong" or "long" or "ushort" or "short";
	}

	bool TryCreateLiftedOptionalArgumentComponents(ArgumentExpression argument, out List<Expression> components)
	{
		components = [];
		if (argument.Value is null || !TryGetParamsComponentShape(null, argument.ResolvedType, "value", out ParamsComponentShape shape))
			return false;
		if (shape.Kind != ParamsComponentShapeKind.Optional || shape.Components.Count != 2)
			return false;
		if (TryGetParamsComponentShape(null, argument.Value.ResolvedType, "value", out _))
			return false;

		components.Add(argument.Value);
		components.Add(new LiteralExpression
		{
			SourceSyntax = argument.SourceSyntax,
			Kind = LiteralKind.True,
			Text = "true",
			Value = true,
			ResolvedType = "bool"
		});
		return true;
	}

	bool TryCreateFunctionToDelegateArgumentComponents(ArgumentExpression argument, List<ParameterDefinition>? callableParameters, int index, out List<Expression> components)
	{
		components = [];
		if (argument.Value is null || callableParameters is null || index + 1 >= callableParameters.Count)
			return false;
		if (!TryGetCallableShape(argument.Value.ResolvedType, out CallableShape source) || source.Kind != "fn")
			return false;
		if (!TryGetCallableShape(callableParameters[index].ResolvedType, out CallableShape target) || target.Kind != "fn")
			return false;
		if (callableParameters[index + 1].ResolvedType != "void*")
			return false;
		if (target.ReturnType != source.ReturnType || target.Parameters.Count != source.Parameters.Count + 1 || target.Parameters[0] != "void*")
			return false;
		for (int i = 0; i < source.Parameters.Count; i++)
			if (source.Parameters[i] != target.Parameters[i + 1] && !IsGenericCallableParameterTarget(target.Parameters[i + 1]))
				return false;

		components.Add(argument.Value);
		components.Add(NullLiteral(argument.SourceSyntax));
		return true;
	}

	bool IsGenericCallableParameterTarget(string parameterType)
	{
		string type = StripTopLevelValueQualifiers(parameterType);
		if (type.StartsWith("in ", System.StringComparison.Ordinal))
			type = type[3..].TrimStart();
		if (type.StartsWith("out ", System.StringComparison.Ordinal))
			type = type[4..].TrimStart();
		if (type.StartsWith("thrown ", System.StringComparison.Ordinal))
			type = type[7..].TrimStart();
		return IsGenericPlaceholderParameter(type);
	}

	bool IsCurrentGenericParameter(string name)
	{
		if (currentRewriteFunction is null)
			return false;
		foreach (GenericParameter parameter in currentRewriteFunction.GenericParameters)
			if (parameter.Name == name)
				return true;
		if (FindContainingType(currentRewriteFunction) is TypeDefinition containingType)
			foreach (GenericParameter parameter in containingType.GenericParameters)
				if (parameter.Name == name)
					return true;
		return false;
	}

	bool IsGenericPlaceholderParameter(string name)
	{
		if (IsCurrentGenericParameter(name))
			return true;
		if (string.IsNullOrWhiteSpace(name))
			return false;
		if (TryGetPrimitiveType(name, out _) || typeDefinitions.ContainsKey(BaseTypeName(name)))
			return false;
		return char.IsUpper(name[0]);
	}

	bool TryCreateIteratorToProtocolArgumentComponents(ArgumentExpression argument, List<ParameterDefinition>? callableParameters, int index, out List<Expression> components)
	{
		components = [];
		if (argument.Value is null || callableParameters is null || index + 1 >= callableParameters.Count)
			return false;
		if (!TryGetCallableShape(callableParameters[index].ResolvedType, out CallableShape target) || target.Kind != "fn" || target.ReturnType != "bool")
			return false;
		if (callableParameters[index + 1].ResolvedType != "void*")
			return false;

		string sourceType = argument.Value.ResolvedType ?? ErrorType;
		string stateTypeName = TryGetPointerElementType(sourceType) ?? sourceType;
		if (GetTypeDefinition(stateTypeName) is not TypeDefinition stateType)
			return false;
		FunctionDefinition? adapter = null;
		foreach (FunctionDefinition function in GetFunctions(stateType))
		{
			if (function.Name == "op_iter")
			{
				adapter = function;
				break;
			}
		}
		if (adapter is null || !TryGetCallableShape(BuildFunctionValueType(adapter, isInstance: false), out CallableShape source) || !CallableShapesCompatible(source, target))
			return false;

		components.Add(CreateMethodReference(adapter, BuildFunctionValueType(adapter, isInstance: false)));
		components.Add(CreateIteratorProtocolContext(argument.Value, sourceType, stateTypeName));
		return true;
	}

	Expression CreateIteratorProtocolContext(Expression value, string sourceType, string stateTypeName)
	{
		if (TryGetPointerElementType(sourceType) is not null)
			return value;

		return new UnaryExpression
		{
			SourceSyntax = value.SourceSyntax,
			Operator = UnaryOperator.AddressOf,
			Operand = value,
			ResolvedType = AddPointer(stateTypeName)
		};
	}

	bool TryRewriteParamsAssignment(AssignmentExpression assignment, out List<Statement> statements)
	{
		statements = [];
		if (assignment.Operator != AssignmentOperator.Assign)
			return false;
		if (!TryCreateParamsComponentExpressions(assignment.Target, out List<Expression> targets))
			return false;
		if (TryRewriteExpandedReturnAssignment(assignment, targets, out statements))
			return true;
		if (!TryCreateParamsComponentExpressions(assignment.Value, out List<Expression> values))
			return false;
		if (targets.Count != values.Count)
			return false;

		for (int i = 0; i < targets.Count; i++)
		{
			statements.Add(new ExpressionStatement
			{
				SourceSyntax = assignment.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = assignment.SourceSyntax,
					Target = LowerExpression(targets[i]),
					Operator = AssignmentOperator.Assign,
					Value = LowerExpression(values[i]),
					ResolvedType = targets[i].ResolvedType
				}
			});
		}

		return statements.Count > 0;
	}

	bool TryRewriteExpandedReturnAssignment(AssignmentExpression assignment, List<Expression> targets, out List<Statement> statements)
	{
		statements = [];
		if (assignment.Value is not CallExpression call)
			return false;
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function) || !TryGetExpandedReturnShape(call, function, out ParamsComponentShape? shape))
			return false;
		if (shape.Components.Count != targets.Count)
			return false;

		if (!preparedExpandedReturnCalls.Contains(call))
		{
			for (int i = 1; i < targets.Count; i++)
			{
				call.Arguments.Add(new ArgumentExpression
				{
					SourceSyntax = assignment.SourceSyntax,
					Modifier = ArgumentModifier.Out,
					Value = LowerExpression(CloneParamsExpansionExpression(targets[i])),
					ResolvedType = shape.Components[i].Type
				});
			}
			preparedExpandedReturnCalls.Add(call);
		}

		statements.Add(new ExpressionStatement
		{
			SourceSyntax = assignment.SourceSyntax,
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				SourceSyntax = assignment.SourceSyntax,
				Target = LowerExpression(CloneParamsExpansionExpression(targets[0])),
				Operator = AssignmentOperator.Assign,
				Value = LowerExpression(call),
				ResolvedType = shape.Components[0].Type
			}
		});
		return true;
	}

	bool TryCreateParamsComponentExpressions(Expression? expression, out List<Expression> components)
	{
		components = [];
		switch (expression)
		{
			case null:
				return false;

			case ParenthesizedExpression parenthesized:
				return TryCreateParamsComponentExpressions(parenthesized.Expression, out components);

			case ThisExpression
				when currentRewriteFunction is not null
					&& GetExplicitThisParameter(currentRewriteFunction) is ThisParameterDefinition thisParameter
					&& paramsExpansions.TryGetValue(thisParameter, out List<ParamsExpansionComponent>? expansion):
				foreach (ParamsExpansionComponent component in expansion)
					components.Add(CreateVariableReference(component.Node, component.Type));
				return components.Count > 0;

			case ThisExpression thisExpression
				when currentRewriteFunction is not null
					&& TryGetParamsComponentShape(null, thisExpression.ResolvedType, "this", out ParamsComponentShape shape):
				return TryCreateCurrentThisParameterComponents(shape, out components);

			case NamedExpression named
				when TryCreateNamedParamsComponentExpressions(named, out components):
				return true;

			case VariableReferenceExpression { Variable: not null } variable
				when paramsExpansions.TryGetValue(variable.Variable, out List<ParamsExpansionComponent>? expansion):
				foreach (ParamsExpansionComponent component in expansion)
					components.Add(CreateVariableReference(component.Node, component.Type));
				return components.Count > 0;

			case VariableReferenceExpression { Variable: ParameterDefinition { Name: "this" } }
				when TryCreateCurrentThisParameterComponents(out components):
				return true;

			case MemberReferenceExpression { Member: not null } member
				when paramsExpansions.TryGetValue(member.Member, out List<ParamsExpansionComponent>? expansion):
				foreach (ParamsExpansionComponent component in expansion)
				{
					components.Add(new MemberReferenceExpression
					{
						SourceSyntax = member.SourceSyntax,
						Target = CloneParamsExpansionExpression(member.Target),
						Name = component.Name,
						Member = component.Node,
						ResolvedType = component.Type
					});
				}
				return components.Count > 0;

			case MemberReferenceExpression { Target: not null, Member: FunctionDefinition function } member
				when FindContainingType(function) is not InterfaceDefinition:
				components.Add(CreateFlattenedMethodReference(member, member.Target, function));
				components.Add(member.Target);
				return true;

			case VariableReferenceExpression variable
				when TryCreateIteratorProtocolComponentsFromExpandedCall(variable, out components):
				return true;

			case MemberReferenceExpression member
				when TryCreateIteratorProtocolComponentsFromExpandedCall(member, out components):
				return true;

			case Expression protocol
				when TryCreateIteratorProtocolComponentsFromProtocolValue(protocol, out components):
				return true;

			case MemberReferenceExpression { Target: not null } member
				when TryCreateParamsMemberComponentExpression(member, out Expression? component):
				components.Add(component);
				return true;

			case CallExpression call when TryCreateExpandedReturnCallComponents(call, out components):
				return true;

			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
				{
					if (item.Expression is null)
					{
						components.Clear();
						return false;
					}
					components.Add(item.Expression);
				}
				return components.Count > 0;

			case DefaultExpression defaultExpression:
				return TryCreateDefaultParamsComponentExpressions(defaultExpression, out components);

			case ArrayExpression array:
				return TryCreateArrayParamsComponentExpressions(array, out components);

			case LiteralExpression { Kind: LiteralKind.String } literal:
				return TryCreateStringParamsComponentExpressions(literal, out components);

			case IndexExpression index:
				return TryCreateIndexedParamsComponentExpressions(index, out components);

			case UnaryExpression { Operator: UnaryOperator.AddressOf } addressOf
				when TryCreateParamsComponentExpressions(addressOf.Operand, out List<Expression> addressed):
				foreach (Expression component in addressed)
				{
					components.Add(new UnaryExpression
					{
						SourceSyntax = addressOf.SourceSyntax,
						Operator = UnaryOperator.AddressOf,
						Operand = component,
						Context = CloneParamsExpansionExpression(addressOf.Context),
						ResolvedType = AddPointer(component.ResolvedType ?? ErrorType)
					});
				}
				return components.Count > 0;

			case UnaryExpression { Operator: UnaryOperator.PointerDereference } dereference
				when TryCreateParamsComponentExpressions(dereference.Operand, out List<Expression> dereferenced):
				foreach (Expression component in dereferenced)
				{
					components.Add(new UnaryExpression
					{
						SourceSyntax = dereference.SourceSyntax,
						Operator = UnaryOperator.PointerDereference,
						Operand = component,
						Context = CloneParamsExpansionExpression(dereference.Context),
						ResolvedType = TryGetPointerElementType(component.ResolvedType) ?? ErrorType
					});
				}
				return components.Count > 0;

			default:
				return false;
		}
	}

	bool TryCreateCurrentThisParameterComponents(out List<Expression> components)
	{
		if (TryCreateCurrentThisParameterComponents(["this", "this_length"], out components))
			return true;
		if (TryCreateCurrentThisParameterComponents(["this", "this_specified"], out components))
			return true;
		if (TryCreateCurrentThisParameterComponents(["this", "this_context"], out components))
			return true;
		if (TryCreateCurrentThisParameterComponents(["this_call", "this_context"], out components))
			return true;
		return false;
	}

	bool TryCreateCurrentThisParameterComponents(ParamsComponentShape shape, out List<Expression> components)
	{
		List<string> names = [];
		foreach (ParamsComponent component in shape.Components)
			names.Add(component.ExpandedName);
		return TryCreateCurrentThisParameterComponents(names, out components);
	}

	bool TryCreateCurrentThisParameterComponents(IReadOnlyList<string> names, out List<Expression> components)
	{
		components = [];
		if (currentRewriteFunction is null)
			return false;

		foreach (string name in names)
		{
			ParameterDefinition? parameter = null;
			foreach (ParameterDefinition candidate in currentRewriteFunction.Parameters)
				if (candidate.Name == name)
				{
					parameter = candidate;
					break;
				}
			if (parameter is null)
				return false;
			components.Add(CreateVariableReference(parameter, parameter.ResolvedType ?? ErrorType));
		}

		return components.Count > 0;
	}

	bool TryCreateIteratorProtocolComponentsFromExpandedCall(Expression expression, out List<Expression> components)
	{
		components = [];
		if (!TryGetCallableShape(expression.ResolvedType, out CallableShape callable) || callable.Kind != "fn" || callable.ReturnType != "bool" || callable.Parameters.Count < 2 || callable.Parameters[0] != "void*")
			return false;
		if (!TryFindParamsExpansionSibling(expression, "context", out Expression? context) || context is null)
			return false;

		components.Add(expression);
		components.Add(context);
		return true;
	}

	bool TryCreateIteratorProtocolComponentsFromProtocolValue(Expression expression, out List<Expression> components)
	{
		components = [];
		if (!TryGetIteratorProtocolCallType(expression.ResolvedType, out string callType))
			return false;

			switch (expression)
			{
			case ParenthesizedExpression { Expression: not null } parenthesized:
				return TryCreateIteratorProtocolComponentsFromProtocolValue(parenthesized.Expression, out components);

			case MemberReferenceExpression member:
				components.Add(new MemberReferenceExpression
				{
					SourceSyntax = member.SourceSyntax,
					Target = CloneParamsExpansionExpression(member.Target),
					Name = member.Name,
					ResolvedType = callType
				});
				components.Add(new MemberReferenceExpression
				{
					SourceSyntax = member.SourceSyntax,
					Target = CloneParamsExpansionExpression(member.Target),
					Name = member.Name + "_context",
					ResolvedType = "void*"
				});
				return true;

			case NamedExpression named:
				components.Add(new NamedExpression
				{
					SourceSyntax = named.SourceSyntax,
					Name = named.Name,
					ResolvedType = callType
				});
				components.Add(new NamedExpression
				{
					SourceSyntax = named.SourceSyntax,
					Name = named.Name + "_context",
					ResolvedType = "void*"
				});
				return true;

			case VariableReferenceExpression variable when GetReferenceName(variable.Variable) is string name:
				components.Add(new NamedExpression
				{
					SourceSyntax = variable.SourceSyntax,
					Name = name,
					ResolvedType = callType
				});
				components.Add(new NamedExpression
				{
					SourceSyntax = variable.SourceSyntax,
					Name = name + "_context",
					ResolvedType = "void*"
				});
				return true;

			default:
				return false;
		}
	}

	bool TryGetIteratorProtocolCallType(string? iterType, out string callType)
	{
		callType = "";
		if (iterType is null)
			return false;
		if (!TryGetIteratorProtocolCurrentTypes(iterType, out List<string>? currentTypes) || currentTypes is null)
			return false;

		List<string> parameters = ["void*"];
		foreach (string currentType in currentTypes)
			parameters.Add(AddPointer(currentType));
		callType = BuildCallableType("fn", "bool", parameters);
		return true;
	}

	static string? GetReferenceName(BindableNode? node)
	{
		return node switch
		{
			ParameterDefinition parameter => parameter.Name,
			FieldDefinition field => field.Name,
			VariableDefinition variable => variable.Name,
			DeclarationTarget target when target.Names.Count == 1 => target.Names[0],
			_ => null
		};
	}

	bool TryCreateNamedParamsComponentExpressions(NamedExpression named, out List<Expression> components)
	{
		components = [];
		if (named.Qualifiers.Count > 0)
			return false;

		foreach ((BindableNode node, List<ParamsExpansionComponent> expansion) in paramsExpansions)
		{
			if (!ParamsExpansionNodeMatchesName(node, named.Name))
				continue;

			foreach (ParamsExpansionComponent component in expansion)
				components.Add(CreateVariableReference(component.Node, component.Type));
			return components.Count > 0;
		}
		return false;
	}

	static bool ParamsExpansionNodeMatchesName(BindableNode node, string name)
	{
		return node switch
		{
			DeclarationTarget target => target.Names.Contains(name),
			ParameterDefinition parameter => parameter.Name == name || parameter.Symbol == name,
			Definition definition => definition.Name == name || definition.Symbol == name,
			_ => false
		};
	}

	bool TryFindParamsExpansionSibling(Expression expression, string sourceName, out Expression? sibling)
	{
		sibling = null;
		BindableNode? node = expression switch
		{
			VariableReferenceExpression variable => variable.Variable,
			MemberReferenceExpression member => member.Member,
			_ => null
		};
		if (node is null)
			return false;

		foreach (List<ParamsExpansionComponent> expansion in paramsExpansions.Values)
		{
			bool containsNode = false;
			ParamsExpansionComponent? siblingComponent = null;
			foreach (ParamsExpansionComponent component in expansion)
			{
				if (ReferenceEquals(component.Node, node))
					containsNode = true;
				if (component.SourceName == sourceName)
					siblingComponent = component;
			}
			if (!containsNode || siblingComponent is null)
				continue;

			sibling = expression is MemberReferenceExpression memberExpression
				? new MemberReferenceExpression
				{
					SourceSyntax = expression.SourceSyntax,
					Target = CloneParamsExpansionExpression(memberExpression.Target),
					Name = siblingComponent.Name,
					Member = siblingComponent.Node,
					ResolvedType = siblingComponent.Type
				}
				: CreateVariableReference(siblingComponent.Node, siblingComponent.Type);
			return true;
		}
		if (sourceName == "context")
		{
			switch (expression)
			{
				case MemberReferenceExpression member:
					sibling = new MemberReferenceExpression
					{
						SourceSyntax = expression.SourceSyntax,
						Target = CloneParamsExpansionExpression(member.Target),
						Name = member.Name + "_context",
						ResolvedType = "void*"
					};
					return true;

				case VariableReferenceExpression { Variable: ParameterDefinition parameter }:
					sibling = new NamedExpression
					{
						SourceSyntax = expression.SourceSyntax,
						Name = parameter.Name + "_context",
						ResolvedType = "void*"
					};
					return true;

				case VariableReferenceExpression { Variable: DeclarationTarget target } when target.Names.Count == 1:
					sibling = new NamedExpression
					{
						SourceSyntax = expression.SourceSyntax,
						Name = target.Names[0] + "_context",
						ResolvedType = "void*"
					};
					return true;
			}
		}
		return false;
	}

	bool TryRewriteExpandedReturn(ReturnStatement statement, out Statement rewritten)
	{
		rewritten = statement;
		if (currentRewriteFunction is null || !expandedReturnShapes.TryGetValue(currentRewriteFunction, out ParamsComponentShape? shape))
			return false;
		if (!TryCreateParamsComponentExpressions(statement.Expression, out List<Expression> components) || components.Count != shape.Components.Count)
			return false;

		List<Statement> statements = [];
		for (int i = 1; i < components.Count; i++)
		{
			ParameterDefinition parameter = currentRewriteFunction.Parameters[^ (components.Count - i)];
			statements.Add(new ExpressionStatement
			{
				SourceSyntax = statement.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = statement.SourceSyntax,
					Target = CreateVariableReference(parameter, parameter.ResolvedType ?? shape.Components[i].Type),
					Operator = AssignmentOperator.Assign,
					Value = components[i],
					ResolvedType = shape.Components[i].Type
				}
			});
		}
		statements.Add(new ReturnStatement
		{
			SourceSyntax = statement.SourceSyntax,
			ResolvedType = "void",
			Expression = components[0]
		});
		rewritten = CreateBlock(statements);
		return true;
	}

	bool TryCreateArrayParamsComponentExpressions(ArrayExpression array, out List<Expression> components)
	{
		components = [];
		string? arrayElementType = TryGetArrayElementType(array.ResolvedType) ?? TryGetPointerElementType(array.ResolvedType);
		if (arrayElementType is not null)
		{
			array.ResolvedType = AddPointer(arrayElementType);
			components.Add(array);
			components.Add(NumberLiteral(array.Elements.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), IsConstQualified(arrayElementType) ? "const nuint" : "nuint"));
			return true;
		}

		if (array.Elements.Count == 0)
			return false;

		List<List<Expression>> elementComponents = [];
		foreach (Expression element in array.Elements)
		{
			if (!TryCreateParamsComponentExpressions(element, out List<Expression> current))
				return false;
			if (elementComponents.Count > 0 && current.Count != elementComponents[0].Count)
				return false;
			elementComponents.Add(current);
		}

		if (elementComponents.Count == 0 || elementComponents[0].Count == 0)
			return false;

		for (int componentIndex = 0; componentIndex < elementComponents[0].Count; componentIndex++)
		{
			ArrayExpression componentArray = new()
			{
				SourceSyntax = array.SourceSyntax,
				ResolvedType = AddPointer(elementComponents[0][componentIndex].ResolvedType ?? ErrorType)
			};
			for (int elementIndex = 0; elementIndex < elementComponents.Count; elementIndex++)
				componentArray.Elements.Add(elementComponents[elementIndex][componentIndex]);
			components.Add(componentArray);
		}

		components.Add(NumberLiteral(array.Elements.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "nuint"));
		return true;
	}

	bool TryCreateDefaultParamsComponentExpressions(DefaultExpression defaultExpression, out List<Expression> components)
	{
		components = [];
		if (!TryGetParamsComponentShape(defaultExpression.Type, defaultExpression.ResolvedType, "value", out ParamsComponentShape shape))
			return false;

		foreach (ParamsComponent component in shape.Components)
		{
			components.Add(new DefaultExpression
			{
				SourceSyntax = defaultExpression.SourceSyntax,
				ResolvedType = component.Type
			});
		}
		return components.Count > 0;
	}

	bool TryCreateStringParamsComponentExpressions(LiteralExpression literal, out List<Expression> components)
	{
		components = [];
		string resolvedType = literal.ResolvedType ?? "";
		if (!IsStringLiteralArrayType(resolvedType, out string pointerType, out string lengthType))
			return false;

		components.Add(new LiteralExpression
		{
			SourceSyntax = literal.SourceSyntax,
			Kind = literal.Kind,
			Text = literal.Text,
			Value = literal.Value,
			ResolvedType = pointerType
		});
		components.Add(NumberLiteral(GetStringLiteralLength(literal).ToString(System.Globalization.CultureInfo.InvariantCulture), lengthType));
		return true;
	}

	static bool IsStringLiteralArrayType(string type, out string pointerType, out string lengthType)
	{
		pointerType = "";
		lengthType = "";
		if (!TryParseStringLiteralArrayType(type, out string elementType, out bool isConst))
			return false;

		pointerType = AddPointer(elementType);
		lengthType = isConst ? "const nuint" : "nuint";
		return true;
	}

	static bool TryParseStringLiteralArrayType(string type, out string elementType, out bool isConst)
	{
		elementType = "";
		isConst = false;
		if (!type.EndsWith("[]", System.StringComparison.Ordinal))
			return false;

		elementType = type[..^2].Trim();
		isConst = elementType.StartsWith("const ", System.StringComparison.Ordinal);
		string bare = isConst ? elementType["const ".Length..].Trim() : elementType;
		return bare is "char" or "wchar" or "achar";
	}

	bool TryCreateIndexedParamsComponentExpressions(IndexExpression index, out List<Expression> components)
	{
		components = [];
		if (index.Target is ArrayExpression)
			return false;
		if (index.Arguments.Count == 2
			&& TryGetArrayElementType(index.ResolvedType) is string resultElementType
			&& GetPrimitiveStringElementType(index.Target?.ResolvedType) == StripTopLevelValueQualifiers(resultElementType))
		{
			Expression? start = ReplaceParamsLengthComponentExpressions(index.Arguments[0].Value);
			Expression? count = ReplaceParamsLengthComponentExpressions(index.Arguments[1].Value);
			if (index.Target is null || start is null || count is null)
				return false;
			components.Add(new BinaryExpression
			{
				SourceSyntax = index.SourceSyntax,
				Left = index.Target,
				Operator = BinaryOperator.Add,
				Right = start,
				ResolvedType = AddPointer(resultElementType)
			});
			components.Add(count);
			return true;
		}
		if (!TryCreateParamsComponentExpressions(index.Target, out List<Expression> targetComponents) || targetComponents.Count < 2)
			return false;
		if (index.Arguments.Count == 2 && TryGetArrayElementType(index.ResolvedType) is not null)
		{
			Expression? start = ReplaceParamsLengthComponentExpressions(index.Arguments[0].Value);
			Expression? count = ReplaceParamsLengthComponentExpressions(index.Arguments[1].Value);
			if (start is null || count is null)
				return false;
			components.Add(new BinaryExpression
			{
				SourceSyntax = index.SourceSyntax,
				Left = targetComponents[0],
				Operator = BinaryOperator.Add,
				Right = start,
				ResolvedType = targetComponents[0].ResolvedType
			});
			components.Add(count);
			return true;
		}

		for (int i = 0; i < targetComponents.Count - 1; i++)
		{
			Expression targetComponent = targetComponents[i];
			IndexExpression componentIndex = new()
			{
				SourceSyntax = index.SourceSyntax,
				Target = targetComponent,
				ResolvedType = TryGetPointerElementType(targetComponent.ResolvedType) ?? ErrorType
			};
			foreach (ArgumentExpression argument in index.Arguments)
				componentIndex.Arguments.Add(CloneArgument(argument));
			components.Add(componentIndex);
		}
		return components.Count > 0;
	}

	Expression? ReplaceParamsLengthComponentExpressions(Expression? expression)
	{
		return expression switch
		{
			null => null,
			ParenthesizedExpression parenthesized => new ParenthesizedExpression
			{
				SourceSyntax = parenthesized.SourceSyntax,
				Expression = ReplaceParamsLengthComponentExpressions(parenthesized.Expression),
				ResolvedType = parenthesized.ResolvedType
			},
			MemberExpression { Name: "length", Target: not null } member
				when TryCreateParamsComponentExpressions(member.Target, out List<Expression> components) && components.Count >= 2
				=> components[^1],
			MemberExpression member => new MemberExpression
			{
				SourceSyntax = member.SourceSyntax,
				Target = ReplaceParamsLengthComponentExpressions(member.Target),
				Name = member.Name,
				ResolvedType = member.ResolvedType
			},
			MemberReferenceExpression { Name: "length", Target: not null } member
				when TryCreateParamsComponentExpressions(member.Target, out List<Expression> components) && components.Count >= 2
				=> components[^1],
			MemberReferenceExpression member => new MemberReferenceExpression
			{
				SourceSyntax = member.SourceSyntax,
				Target = ReplaceParamsLengthComponentExpressions(member.Target),
				Name = member.Name,
				Member = member.Member,
				ResolvedType = member.ResolvedType
			},
			UnaryExpression unary => new UnaryExpression
			{
				SourceSyntax = unary.SourceSyntax,
				Operator = unary.Operator,
				Operand = ReplaceParamsLengthComponentExpressions(unary.Operand),
				Context = ReplaceParamsLengthComponentExpressions(unary.Context),
				ResolvedType = unary.ResolvedType
			},
			BinaryExpression binary => new BinaryExpression
			{
				SourceSyntax = binary.SourceSyntax,
				Left = ReplaceParamsLengthComponentExpressions(binary.Left),
				Operator = binary.Operator,
				Right = ReplaceParamsLengthComponentExpressions(binary.Right),
				ResolvedType = binary.ResolvedType
			},
			ConditionalExpression conditional => new ConditionalExpression
			{
				SourceSyntax = conditional.SourceSyntax,
				Condition = ReplaceParamsLengthComponentExpressions(conditional.Condition),
				WhenTrue = ReplaceParamsLengthComponentExpressions(conditional.WhenTrue),
				WhenFalse = ReplaceParamsLengthComponentExpressions(conditional.WhenFalse),
				ResolvedType = conditional.ResolvedType
			},
			_ => expression
		};
	}

	static int GetStringLiteralLength(LiteralExpression literal)
	{
		if (literal.Value is string value)
			return value.Length;

		string text = literal.Text;
		if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0])
			text = text[1..^1];

		int length = 0;
		for (int i = 0; i < text.Length; i++)
		{
			if (text[i] == '\\' && i + 1 < text.Length)
				i++;
			length++;
		}
		return length;
	}

	ArgumentExpression CloneArgument(ArgumentExpression argument)
	{
		return new ArgumentExpression
		{
			SourceSyntax = argument.SourceSyntax,
			Name = argument.Name,
			Modifier = argument.Modifier,
			Type = CloneType(argument.Type),
			Target = argument.Target,
			Value = CloneParamsExpansionExpression(argument.Value),
			ResolvedType = argument.ResolvedType
		};
	}

	bool TryCreateParamsMemberComponentExpression(MemberReferenceExpression member, out Expression componentExpression)
	{
		componentExpression = member;
		return TryCreateParamsMemberComponentExpression(member.Target, member.Name, out componentExpression);
	}

	bool TryCreateParamsMemberComponentExpression(MemberExpression member, out Expression componentExpression)
	{
		componentExpression = member;
		return TryCreateParamsMemberComponentExpression(member.Target, member.Name, out componentExpression);
	}

	bool TryCreateParamsMemberComponentExpression(Expression? target, string name, out Expression componentExpression)
	{
		componentExpression = target ?? new MemberExpression { Name = name };
		if (!TryCreateParamsComponentExpressions(target, out List<Expression> targetComponents))
			return false;

		for (int i = targetComponents.Count - 1; i >= 0; i--)
		{
			Expression targetComponent = targetComponents[i];
			if (!IsParamsComponentNamed(targetComponent, name))
				continue;

			componentExpression = targetComponent;
			return true;
		}

		return false;
	}

	bool TryCreateExpandedReturnCallComponents(CallExpression call, out List<Expression> components)
	{
		components = [];
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function) || !TryGetExpandedReturnShape(call, function, out ParamsComponentShape? shape))
			return false;
		return TryCreateExpandedReturnCallComponents(call, shape, out components);
	}

	bool TryCreateExpandedReturnCallComponents(CallExpression call, ParamsComponentShape shape, out List<Expression> components)
	{
		components = [];
		if (currentStatementPrefix is null || shape.Components.Count == 0)
			return false;
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function))
			return false;
		if (call.Target is MemberReferenceExpression { Target: Expression receiver } member
			&& IsInstanceInvocationFunction(function)
			&& !IsPropertyGetterReference(member)
			&& !IsPropertySetterReference(member)
			&& FindContainingType(function) is not InterfaceDefinition)
		{
			RewriteInstanceInvocation(call, member, receiver, function);
		}

		List<DeclarationTarget> targets = [];
		for (int i = 0; i < shape.Components.Count; i++)
		{
			string name = NewGeneratedLocalName(shape.Components[i].Name);
			DeclarationStatement declaration = CreateGeneratedLocal(name, shape.Components[i].Type, new NamedTypeReference { Name = shape.Components[i].Type, ResolvedType = shape.Components[i].Type }, null);
			targets.Add(declaration.Target);
			currentStatementPrefix.Add(declaration);
		}

		for (int i = 1; i < targets.Count; i++)
		{
			call.Arguments.Add(new ArgumentExpression
			{
				SourceSyntax = call.SourceSyntax,
				Modifier = ArgumentModifier.Out,
				Value = CreateVariableReference(targets[i], shape.Components[i].Type),
				ResolvedType = shape.Components[i].Type
			});
		}
		currentStatementPrefix.Add(new ExpressionStatement
		{
			SourceSyntax = call.SourceSyntax,
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				SourceSyntax = call.SourceSyntax,
				Target = CreateVariableReference(targets[0], shape.Components[0].Type),
				Operator = AssignmentOperator.Assign,
				Value = LowerExpression(call),
				ResolvedType = shape.Components[0].Type
			}
		});

		for (int i = 0; i < targets.Count; i++)
			components.Add(CreateVariableReference(targets[i], shape.Components[i].Type));
		RegisterParamsExpansion(call, shape, targets);
		return true;
	}

	bool IsParamsComponentNamed(Expression expression, string name)
	{
		return expression switch
		{
			VariableReferenceExpression { Variable: not null } variable => IsParamsExpansionComponentNamed(variable.Variable, name),
			MemberReferenceExpression { Member: not null } member => IsParamsExpansionComponentNamed(member.Member, name),
			IndexExpression { Target: not null } index => IsParamsComponentNamed(index.Target, name),
			UnaryExpression unary => IsParamsComponentNamed(unary.Operand!, name),
			_ => false
		};
	}

	bool IsParamsExpansionComponentNamed(BindableNode node, string name)
	{
		foreach (List<ParamsExpansionComponent> expansion in paramsExpansions.Values)
		{
			foreach (ParamsExpansionComponent component in expansion)
			{
				if (ReferenceEquals(component.Node, node) && component.SourceName == name)
					return true;
			}
		}
		return false;
	}

	Expression? CloneParamsExpansionExpression(Expression? expression)
	{
		return expression switch
		{
			null => null,
			LiteralExpression literal => new LiteralExpression { SourceSyntax = literal.SourceSyntax, Kind = literal.Kind, Text = literal.Text, Value = literal.Value, ResolvedType = literal.ResolvedType },
			NamedExpression named => CloneNamedExpression(named),
			VariableReferenceExpression variable => new VariableReferenceExpression { SourceSyntax = variable.SourceSyntax, Variable = variable.Variable, ResolvedType = variable.ResolvedType },
			ThisExpression thisExpression => new ThisExpression { SourceSyntax = thisExpression.SourceSyntax, ResolvedType = thisExpression.ResolvedType },
			MemberReferenceExpression member => new MemberReferenceExpression
			{
				SourceSyntax = member.SourceSyntax,
				Target = CloneParamsExpansionExpression(member.Target),
				Name = member.Name,
				Member = member.Member,
				ResolvedType = member.ResolvedType
			},
			MemberExpression member => new MemberExpression
			{
				SourceSyntax = member.SourceSyntax,
				Target = CloneParamsExpansionExpression(member.Target),
				Name = member.Name,
				ResolvedType = member.ResolvedType
			},
			ParenthesizedExpression parenthesized => new ParenthesizedExpression
			{
				SourceSyntax = parenthesized.SourceSyntax,
				Expression = CloneParamsExpansionExpression(parenthesized.Expression),
				ResolvedType = parenthesized.ResolvedType
			},
			UnaryExpression unary => new UnaryExpression
			{
				SourceSyntax = unary.SourceSyntax,
				Operator = unary.Operator,
				Operand = CloneParamsExpansionExpression(unary.Operand),
				Context = CloneParamsExpansionExpression(unary.Context),
				ResolvedType = unary.ResolvedType
			},
			BinaryExpression binary => new BinaryExpression
			{
				SourceSyntax = binary.SourceSyntax,
				Left = CloneParamsExpansionExpression(binary.Left),
				Operator = binary.Operator,
				Right = CloneParamsExpansionExpression(binary.Right),
				ResolvedType = binary.ResolvedType
			},
			ConditionalExpression conditional => new ConditionalExpression
			{
				SourceSyntax = conditional.SourceSyntax,
				Condition = CloneParamsExpansionExpression(conditional.Condition),
				WhenTrue = CloneParamsExpansionExpression(conditional.WhenTrue),
				WhenFalse = CloneParamsExpansionExpression(conditional.WhenFalse),
				ResolvedType = conditional.ResolvedType
			},
			CallExpression call =>
				new CallExpression
				{
					SourceSyntax = call.SourceSyntax,
					Target = CloneParamsExpansionExpression(call.Target),
					ResolvedType = call.ResolvedType
				},
			_ => expression
		};
	}

	static NamedExpression CloneNamedExpression(NamedExpression named)
	{
		NamedExpression clone = new()
		{
			SourceSyntax = named.SourceSyntax,
			Name = named.Name,
			ResolvedType = named.ResolvedType
		};
		clone.Qualifiers.AddRange(named.Qualifiers);
		return clone;
	}
}
