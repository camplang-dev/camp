using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void ExpandParamsArguments(CallExpression call)
	{
		List<ParameterDefinition>? callableParameters = callTargets.TryGetValue(call, out FunctionDefinition? function)
			? GetCallableParameters(function.Parameters)
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
			if (!TryCreateParamsComponentExpressions(argument.Value, out List<Expression> components))
			{
				if (!TryCreateLiftedOptionalArgumentComponents(argument, out components)
					&& !TryCreateFunctionToDelegateArgumentComponents(argument, callableParameters, i, out components))
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
			if (source.Parameters[i] != target.Parameters[i + 1])
				return false;

		components.Add(argument.Value);
		components.Add(NullLiteral(argument.SourceSyntax));
		return true;
	}

	bool TryRewriteParamsAssignment(AssignmentExpression assignment, out List<Statement> statements)
	{
		statements = [];
		if (assignment.Operator != AssignmentOperator.Assign)
			return false;
		if (!TryCreateParamsComponentExpressions(assignment.Target, out List<Expression> targets))
			return false;
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

	bool TryCreateParamsComponentExpressions(Expression? expression, out List<Expression> components)
	{
		components = [];
		switch (expression)
		{
			case null:
				return false;

			case ParenthesizedExpression parenthesized:
				return TryCreateParamsComponentExpressions(parenthesized.Expression, out components);

			case VariableReferenceExpression { Variable: not null } variable
				when paramsExpansions.TryGetValue(variable.Variable, out List<ParamsExpansionComponent>? expansion):
				foreach (ParamsExpansionComponent component in expansion)
					components.Add(CreateVariableReference(component.Node, component.Type));
				return components.Count > 0;

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
		if (!TryCreateParamsComponentExpressions(index.Target, out List<Expression> targetComponents) || targetComponents.Count < 2)
			return false;

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
		if (!TryCreateParamsComponentExpressions(member.Target, out List<Expression> targetComponents))
			return false;

		for (int i = targetComponents.Count - 1; i >= 0; i--)
		{
			Expression targetComponent = targetComponents[i];
			if (!IsParamsComponentNamed(targetComponent, member.Name))
				continue;

			componentExpression = targetComponent;
			return true;
		}

		return false;
	}

	bool TryCreateExpandedReturnCallComponents(CallExpression call, out List<Expression> components)
	{
		components = [];
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function) || !expandedReturnShapes.TryGetValue(function, out ParamsComponentShape? shape))
			return false;
		if (currentStatementPrefix is null || shape.Components.Count == 0)
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
