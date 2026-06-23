using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void ExpandParamsArguments(CallExpression call)
	{
		List<ParameterDefinition>? callableParameters;
		if (callTargets.TryGetValue(call, out FunctionDefinition? function))
		{
			bool includeExplicitThis = IncludeExplicitThisArgument(call.Target, function);
			if (!includeExplicitThis
				&& IsInstanceFunction(function)
				&& call.Target is not MemberExpression and not MemberReferenceExpression
				&& call.Arguments.Count > GetCallableParameters(function.Parameters, includeExplicitThis: false).Count)
			{
				includeExplicitThis = true;
			}
			callableParameters = GetCallableParametersForCall(function, includeExplicitThis);
			Dictionary<string, string> substitutions = callGenericSubstitutions.TryGetValue(call, out Dictionary<string, string>? existing)
				? new Dictionary<string, string>(existing, System.StringComparer.Ordinal)
				: [];
			AddFunctionTypeArgumentSubstitutions(function, call.TypeArguments, substitutions);
			if (includeExplicitThis && call.Arguments.Count > 0 && call.Arguments[0].Value?.ResolvedType is string receiverType)
				AddReceiverTypeGenericSubstitutions(receiverType, function, substitutions);
			if (substitutions.Count == 0 && TryGetCallableShape(call.Target?.ResolvedType, out CallableShape callableShape) && callableShape.Parameters.Count > 0)
			{
				AddConstructedTypeGenericSubstitutions(callableShape.Parameters[0], substitutions);
				if (substitutions.Count == 0)
					AddSingleReceiverTypeArgumentSubstitutions(callableShape.Parameters[0], callableParameters, substitutions);
			}
			if (substitutions.Count > 0)
				callableParameters = SubstituteCallableParameterTypes(callableParameters, substitutions);
		}
		else
		{
			callableParameters = GetCallableParametersForExpression(call.Target);
			if (callableParameters is not null
				&& TryGetCallableShape(call.Target?.ResolvedType, out CallableShape callableShape)
				&& callableShape.Parameters.Count > 0)
			{
				Dictionary<string, string> substitutions = [];
				AddSingleReceiverTypeArgumentSubstitutions(callableShape.Parameters[0], callableParameters, substitutions);
				if (substitutions.Count > 0)
					callableParameters = SubstituteCallableParameterTypes(callableParameters, substitutions);
			}
		}
		ExpandParamsArguments(call.Arguments, callableParameters);
	}

	void AddConstructedTypeGenericSubstitutions(string constructedType, Dictionary<string, string> substitutions)
	{
		string baseName = BaseConstructedType(constructedType);
		if (!typeDefinitions.TryGetValue(baseName, out TypeDefinition? definition) || definition.GenericParameters.Count == 0)
			return;

		List<string> typeArguments = ExtractConstructedTypeArgumentsPreservingTypeText(constructedType);
		int count = System.Math.Min(definition.GenericParameters.Count, typeArguments.Count);
		for (int i = 0; i < count; i++)
			substitutions[definition.GenericParameters[i].Name] = typeArguments[i];
	}

	void AddSingleReceiverTypeArgumentSubstitutions(string constructedType, List<ParameterDefinition> parameters, Dictionary<string, string> substitutions)
	{
		List<string> typeArguments = ExtractConstructedTypeArgumentsPreservingTypeText(constructedType);
		if (typeArguments.Count != 1)
			return;

		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter.Type is NamedTypeReference named && IsGenericPlaceholderParameter(named.Name))
				substitutions[named.Name] = typeArguments[0];
			else if (IsGenericPlaceholderParameter(StripTopLevelValueQualifiers(parameter.ResolvedType ?? "")))
				substitutions[StripTopLevelValueQualifiers(parameter.ResolvedType ?? "")] = typeArguments[0];
		}
	}

	static List<string> ExtractConstructedTypeArgumentsPreservingTypeText(string type)
	{
		type = StripTopLevelValueQualifiers(type.Trim());
		while (true)
		{
			if (type.EndsWith("[]", System.StringComparison.Ordinal))
			{
				type = type[..^2].TrimEnd();
				continue;
			}
			if (type.EndsWith("*", System.StringComparison.Ordinal) || type.EndsWith("?", System.StringComparison.Ordinal))
			{
				type = type[..^1].TrimEnd();
				continue;
			}
			break;
		}
		return ExtractConstructedTypeArguments(type);
	}

	static List<ParameterDefinition> SubstituteCallableParameterTypes(List<ParameterDefinition> parameters, Dictionary<string, string> substitutions)
	{
		List<ParameterDefinition> substituted = [];
		foreach (ParameterDefinition parameter in parameters)
		{
			ParameterDefinition copy = parameter switch
			{
				ThisParameterDefinition => new ThisParameterDefinition(),
				_ => new ParameterDefinition()
			};
			copy.SourceSyntax = parameter.SourceSyntax;
			copy.Name = parameter.Name;
			copy.Symbol = parameter.Symbol;
			copy.Modifier = parameter.Modifier;
			copy.Type = parameter.Type;
			copy.ResolvedType = SubstituteGenericType(parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? ErrorType, substitutions);
			copy.DefaultValue = parameter.DefaultValue;
			substituted.Add(copy);
		}
		return substituted;
	}

	List<ParameterDefinition> GetCallableParametersForCall(FunctionDefinition function, bool includeExplicitThis)
	{
		List<ParameterDefinition> parameters = [];
		if (includeExplicitThis
			&& GetExplicitThisParameter(function) is null
			&& IsInstanceFunction(function)
			&& FindContainingType(function) is TypeDefinition containingType)
		{
			parameters.Add(new ThisParameterDefinition
			{
				Name = "this",
				Symbol = "this",
				Type = new PointerTypeReference { ElementType = new TypeDefinitionReference { Definition = containingType, Name = containingType.Name, ResolvedType = containingType.Name }, ResolvedType = containingType.Name + "*" },
				ResolvedType = containingType.Name + "*"
			});
		}
		parameters.AddRange(GetCallableParameters(function.Parameters, includeExplicitThis));
		return ExpandExplicitThisArrayParameters(parameters);
	}

	List<ParameterDefinition> ExpandExplicitThisArrayParameters(List<ParameterDefinition> parameters)
	{
		List<ParameterDefinition> expanded = [];
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter is ThisParameterDefinition && TryGetParamsComponentShape(parameter.Type, parameter.ResolvedType, parameter.Name, out ParamsComponentShape shape) && shape.Kind == ParamsComponentShapeKind.Array && shape.Components.Count == 2)
			{
				expanded.Add(new ThisParameterDefinition
				{
					SourceSyntax = parameter.SourceSyntax,
					Name = parameter.Name,
					Symbol = parameter.Symbol,
					Type = parameter.Type,
					ResolvedType = shape.Components[0].Type
				});
				expanded.Add(new ParameterDefinition
				{
					SourceSyntax = parameter.SourceSyntax,
					Name = shape.Components[1].ExpandedName,
					Symbol = shape.Components[1].ExpandedName,
					ResolvedType = shape.Components[1].Type
				});
				continue;
			}

			expanded.Add(parameter);
		}

		return expanded;
	}

	List<ParameterDefinition>? GetCallableParametersForExpression(Expression? expression)
	{
		if (!TryGetCallableShape(expression?.ResolvedType, out CallableShape shape))
			return null;

		List<ParameterDefinition> parameters = [];
		foreach (string parameterType in shape.Parameters)
			parameters.Add(CreateCallableShapeParameter(parameterType));
		return parameters;
	}

	static ParameterDefinition CreateCallableShapeParameter(string parameterType)
	{
		string typeName = parameterType.Trim();
		ParameterModifier modifier = ParameterModifier.None;
		if (typeName.StartsWith("in ", System.StringComparison.Ordinal))
		{
			modifier = ParameterModifier.In;
			typeName = typeName[3..].TrimStart();
		}
		else if (typeName.StartsWith("out ", System.StringComparison.Ordinal))
		{
			modifier = ParameterModifier.Out;
			typeName = typeName[4..].TrimStart();
		}
		else if (typeName.StartsWith("thrown ", System.StringComparison.Ordinal))
		{
			modifier = ParameterModifier.Thrown;
			typeName = typeName[7..].TrimStart();
		}
		else if (typeName.StartsWith("within ", System.StringComparison.Ordinal))
		{
			modifier = ParameterModifier.Within;
			typeName = typeName[7..].TrimStart();
		}

		return new ParameterDefinition
		{
			Modifier = modifier,
			ResolvedType = typeName,
			Type = new NamedTypeReference { Name = typeName, ResolvedType = typeName }
		};
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
			if (TryMaterializeGenericReturnInArgument(argument, callableParameters, i))
				continue;
			if (TryMaterializeExpandedGenericInArgument(argument, callableParameters, i))
				continue;
			if (ExpandedArgumentComponentAlreadyProvided(arguments, callableParameters, i))
				continue;
			if (TryCreateParamsPointerArgumentComponents(argument, callableParameters, i, out List<Expression> pointerComponents))
			{
				arguments.RemoveAt(i);
				for (int componentIndex = 0; componentIndex < pointerComponents.Count; componentIndex++)
				{
					Expression component = pointerComponents[componentIndex];
					arguments.Insert(i + componentIndex, new ArgumentExpression
					{
						SourceSyntax = argument.SourceSyntax,
						Modifier = argument.Modifier,
						Value = component,
						ResolvedType = component.ResolvedType
					});
				}
				i += pointerComponents.Count - 1;
				continue;
			}

			bool expectsExpandedComponents = ExpectsExpandedArgumentComponents(callableParameters, i);
			if ((!expectsExpandedComponents || !TryCreateParamsComponentExpressions(argument.Value, out List<Expression> components))
				&& (!expectsExpandedComponents || !TryCreateTargetTypedExpandedReturnArgumentComponents(argument, out components)))
			{
				if (PrimitiveStringArrayLengthAlreadyProvided(arguments, callableParameters, i)
					|| !TryCreateLiftedOptionalArgumentComponents(argument, out components)
						&& !TryCreateIteratorToProtocolArgumentComponents(argument, callableParameters, i, out components)
						&& !TryCreateFunctionToDelegateArgumentComponents(argument, callableParameters, i, out components)
						&& !TryCreatePrimitiveStringToArrayArgumentComponents(argument, callableParameters, i, out components)
						&& !TryCreateSourceLevelExpandedArgumentComponents(argument, callableParameters, i, out components))
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
		CollapseDuplicateExpandedThisComponents(arguments, callableParameters);
	}

	bool ExpectsExpandedArgumentComponents(List<ParameterDefinition>? callableParameters, int index)
	{
		if (callableParameters is null)
			return true;
		if (index < callableParameters.Count
			&& TryGetParamsComponentShape(callableParameters[index].Type, callableParameters[index].ResolvedType, callableParameters[index].Name, out ParamsComponentShape parameterShape)
			&& parameterShape.Components.Count > 1)
			return true;
		if (index + 1 >= callableParameters.Count)
			return false;

		string firstName = callableParameters[index].Name;
		string secondName = callableParameters[index + 1].Name;
		if (secondName == firstName + "_length"
			|| secondName == firstName + "_context"
			|| secondName == firstName + "_specified")
			return true;
		if (firstName == "this" && secondName is "this_length" or "this_context" or "this_specified")
			return true;
		if (firstName == "this_call" && secondName == "this_context")
			return true;
		if (IsExpandedArrayComponentPair(callableParameters, index))
			return true;
		return IsPointerToExpandedComponentPair(callableParameters, index);
	}

	bool TryCreateParamsPointerArgumentComponents(ArgumentExpression argument, List<ParameterDefinition>? callableParameters, int index, out List<Expression> components)
	{
		components = [];
		if (argument.Value is null || callableParameters is null || index + 1 >= callableParameters.Count)
			return false;
		string firstParameterType = callableParameters[index].ResolvedType ?? callableParameters[index].Type?.ResolvedType ?? "";
		string secondParameterType = callableParameters[index + 1].ResolvedType ?? callableParameters[index + 1].Type?.ResolvedType ?? "";
		if (TryGetPointerElementType(firstParameterType) is not string firstElementType
			|| TryGetPointerElementType(firstElementType) is null
			|| TryGetPointerElementType(secondParameterType) != "nuint")
			return false;
		if (!TryGetParamsComponentShape(null, argument.Value.ResolvedType, "value", out ParamsComponentShape shape) || shape.Components.Count != 2)
			return false;
		if (!TryCreateParamsComponentExpressions(argument.Value, out List<Expression> valueComponents) || valueComponents.Count != 2)
			return false;

		for (int i = 0; i < valueComponents.Count; i++)
		{
			Expression valueComponent = valueComponents[i];
			string parameterType = i == 0 ? firstParameterType : secondParameterType;
			if (valueComponent.ResolvedType == parameterType)
			{
				components.Add(valueComponent);
				continue;
			}
			components.Add(new UnaryExpression
			{
				SourceSyntax = valueComponent.SourceSyntax,
				Operator = UnaryOperator.AddressOf,
				Operand = valueComponent,
				ResolvedType = AddPointer(valueComponent.ResolvedType ?? shape.Components[i].Type)
			});
		}
		return true;
	}

	static void CollapseDuplicateExpandedThisComponents(List<ArgumentExpression> arguments, List<ParameterDefinition>? callableParameters)
	{
		if (callableParameters is null || callableParameters.Count < 2 || arguments.Count < 3)
			return;

		string firstName = callableParameters[0].Name;
		string secondName = callableParameters[1].Name;
		if (firstName is not ("this" or "this_call") || secondName != "this_context")
			return;
		if (ExpressionReferencesSameValue(arguments[1].Value, arguments[2].Value)
			|| IsProvidedComponent(arguments[1].Value, secondName) && IsProvidedComponent(arguments[2].Value, secondName))
		{
			arguments.RemoveAt(2);
		}
	}

	bool ExpandedArgumentComponentAlreadyProvided(List<ArgumentExpression> arguments, List<ParameterDefinition>? callableParameters, int index)
	{
		if (callableParameters is null || index + 1 >= callableParameters.Count || index + 1 >= arguments.Count)
			return false;
		if (IsPointerToExpandedComponentPair(callableParameters, index))
			return false;

		string firstName = callableParameters[index].Name;
		string secondName = callableParameters[index + 1].Name;
		if (firstName == "this" && secondName == "this_length")
			return IsProvidedLengthComponent(arguments[index].Value, arguments[index + 1].Value, secondName);
		if (firstName == "this" && secondName == "this_context")
			return IsProvidedComponent(arguments[index + 1].Value, secondName);
		if (firstName == "this" && secondName == "this_specified")
			return IsProvidedComponent(arguments[index + 1].Value, secondName);
		if (firstName == "this_call" && secondName == "this_context")
			return IsProvidedComponent(arguments[index + 1].Value, secondName);

		return false;
	}

	static bool IsPointerToExpandedComponentPair(List<ParameterDefinition> callableParameters, int index)
	{
		if (index + 1 >= callableParameters.Count)
			return false;
		string firstParameterType = callableParameters[index].ResolvedType ?? callableParameters[index].Type?.ResolvedType ?? "";
		string secondParameterType = callableParameters[index + 1].ResolvedType ?? callableParameters[index + 1].Type?.ResolvedType ?? "";
		return TryGetPointerElementType(firstParameterType) is string firstElementType
			&& TryGetPointerElementType(firstElementType) is not null
			&& TryGetPointerElementType(secondParameterType) == "nuint";
	}

	static bool IsExpandedArrayComponentPair(List<ParameterDefinition> callableParameters, int index)
	{
		if (index + 1 >= callableParameters.Count)
			return false;
		string firstParameterType = callableParameters[index].ResolvedType ?? callableParameters[index].Type?.ResolvedType ?? "";
		string secondParameterType = callableParameters[index + 1].ResolvedType ?? callableParameters[index + 1].Type?.ResolvedType ?? "";
		return TryGetPointerElementType(firstParameterType) is string elementType
			&& !IsFixedArrayTypeName(elementType)
			&& StripTopLevelValueQualifiers(secondParameterType) == "nuint";
	}

	static bool IsFixedArrayTypeName(string type)
	{
		type = StripTopLevelValueQualifiers(type.Trim());
		if (!type.EndsWith("]", System.StringComparison.Ordinal))
			return false;
		int open = type.LastIndexOf('[');
		if (open < 0 || open + 1 >= type.Length - 1)
			return false;
		for (int i = open + 1; i < type.Length - 1; i++)
		{
			if (!char.IsDigit(type[i]))
				return false;
		}
		return true;
	}

	static bool IsProvidedLengthComponent(Expression? value, Expression? next, string componentName)
	{
		if (next is VariableReferenceExpression { Variable: ParameterDefinition parameter } && parameter.Name == componentName)
			return true;
		if (next is NamedExpression named && named.Name == componentName)
			return true;
		if (next is MemberExpression { Name: "length" } member && ExpressionReferencesSameValue(value, member.Target))
			return true;
		return false;
	}

	static bool IsProvidedComponent(Expression? next, string componentName)
	{
		if (next is VariableReferenceExpression variable && GetReferenceName(variable.Variable) is string referenceName)
		{
			if (referenceName == componentName)
				return true;
			string suffix = componentName.StartsWith("this_", System.StringComparison.Ordinal)
				? componentName["this_".Length..]
				: componentName;
			if (suffix == "context" && referenceName.Contains("context", System.StringComparison.OrdinalIgnoreCase))
				return true;
		}
		if (next is VariableReferenceExpression { Variable: ParameterDefinition parameter } && parameter.Name == componentName)
			return true;
		if (next is NamedExpression named && named.Name == componentName)
			return true;
		if (next is MemberExpression member)
		{
			string suffix = componentName.StartsWith("this_", System.StringComparison.Ordinal)
				? componentName["this_".Length..]
				: componentName;
			return member.Name == suffix;
		}
		return false;
	}

	static bool ExpressionReferencesSameValue(Expression? left, Expression? right)
	{
		if (ReferenceEquals(left, right))
			return true;
		return left switch
		{
			ThisExpression when right is ThisExpression => true,
			VariableReferenceExpression leftVariable when right is VariableReferenceExpression rightVariable
				=> ReferenceEquals(leftVariable.Variable, rightVariable.Variable),
			NamedExpression leftNamed when right is NamedExpression rightNamed
				=> leftNamed.Name == rightNamed.Name,
			_ => false
		};
	}

	bool TryCreateSourceLevelExpandedArgumentComponents(ArgumentExpression argument, List<ParameterDefinition>? callableParameters, int index, out List<Expression> components)
	{
		components = [];
		if (callableParameters is null || argument.Value is null || index + 1 >= callableParameters.Count)
			return false;
		if (argument.Value is not ThisExpression and not VariableReferenceExpression { Variable: ParameterDefinition { Name: "this" } })
			return false;

		string firstName = callableParameters[index].Name;
		string secondName = callableParameters[index + 1].Name;
		if (firstName == "this" && secondName.StartsWith("this_", System.StringComparison.Ordinal))
			return TryCreateSourceLevelExpandedArgumentComponents(argument.Value, firstName, secondName, out components);

		return false;
	}

	bool TryCreateSourceLevelExpandedArgumentComponents(Expression value, string firstName, string secondName, out List<Expression> components)
	{
		components = [];
		if (firstName == "this" && secondName == "this_length")
		{
			components.Add(value);
			components.Add(new MemberExpression
			{
				SourceSyntax = value.SourceSyntax,
				Target = CloneParamsExpansionExpression(value),
				Name = "length",
				ResolvedType = "nuint"
			});
			return true;
		}

		return false;
	}

	bool TryMaterializeGenericReturnInArgument(ArgumentExpression argument, List<ParameterDefinition>? callableParameters, int index)
	{
		if (currentStatementPrefix is null
			|| callableParameters is null
			|| index >= callableParameters.Count
			|| callableParameters[index].Modifier != ParameterModifier.In
			|| argument.Value is not CallExpression call
			|| !callTargets.TryGetValue(call, out FunctionDefinition? function)
			|| !IsMaterializedGenericReturnFunction(function))
			return false;

		string expectedType = callableParameters[index].ResolvedType ?? callableParameters[index].Type?.ResolvedType ?? "";
		if (!IsGenericPlaceholderParameter(StripTopLevelValueQualifiers(expectedType)))
			return false;

		string resultType = call.ResolvedType ?? argument.ResolvedType ?? expectedType;
		if (string.IsNullOrWhiteSpace(resultType) || resultType == "void")
			resultType = expectedType;

		DeclarationStatement storage = CreateGeneratedLocal(NewGeneratedLocalName("value"), resultType, TypeReferenceForResolvedName(resultType), null);
		currentStatementPrefix.Add(storage);
		call.Arguments.Add(new ArgumentExpression
		{
			SourceSyntax = argument.SourceSyntax,
			Modifier = ArgumentModifier.Out,
			Value = CreateVariableReference(storage.Target, resultType),
			ResolvedType = resultType
		});
		currentStatementPrefix.Add(new ExpressionStatement
		{
			SourceSyntax = argument.SourceSyntax,
			ResolvedType = "void",
			Expression = call
		});
		argument.Value = CreateVariableReference(storage.Target, resultType);
		argument.ResolvedType = resultType;
		return true;
	}

	bool TryMaterializeExpandedGenericInArgument(ArgumentExpression argument, List<ParameterDefinition>? callableParameters, int index)
	{
		if (currentStatementPrefix is null
			|| callableParameters is null
			|| index >= callableParameters.Count
			|| IsExpandedParameterComponentStart(callableParameters, index)
			|| !IsMaterializedExpandedStorageParameter(callableParameters[index])
			|| argument.Value is null)
			return false;

		string parameterType = callableParameters[index].ResolvedType ?? callableParameters[index].Type?.ResolvedType ?? "";
		string resultType = IsGenericPlaceholderParameter(StripTopLevelValueQualifiers(parameterType))
			? argument.ResolvedType ?? argument.Value.ResolvedType ?? ""
			: parameterType;
		if ((!TryCreateParamsComponentExpressions(argument.Value, out List<Expression> components) || components.Count <= 1)
			&& !TryCreatePrimitiveStringMaterializedComponents(argument, resultType, out components))
			return false;
		if (string.IsNullOrWhiteSpace(resultType) || !TryGetParamsComponentShape(null, resultType, "value", out ParamsComponentShape shape) || shape.Components.Count != components.Count)
			return false;

		DeclarationStatement storage = CreateMaterializedGenericReturnStorage(resultType, argument.SourceSyntax);
		currentStatementPrefix.Add(storage);
		Expression storageTarget = CreateVariableReference(storage.Target, storage.Target.ResolvedType ?? ErrorType);
		for (int componentIndex = 0; componentIndex < shape.Components.Count; componentIndex++)
		{
			currentStatementPrefix.Add(new ExpressionStatement
			{
				SourceSyntax = argument.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = argument.SourceSyntax,
					Target = new MemberExpression
					{
						SourceSyntax = argument.SourceSyntax,
						Target = CloneParamsExpansionExpression(storageTarget),
						Name = shape.Components[componentIndex].Name,
						ResolvedType = shape.Components[componentIndex].Type
					},
					Operator = AssignmentOperator.Assign,
					Value = LowerExpression(components[componentIndex]),
					ResolvedType = shape.Components[componentIndex].Type
				}
			});
		}

		argument.Value = CreateVariableReference(storage.Target, storage.Target.ResolvedType ?? ErrorType);
		argument.ResolvedType = storage.Target.ResolvedType ?? resultType;
		return true;
	}

	static bool IsExpandedParameterComponentStart(List<ParameterDefinition> callableParameters, int index)
	{
		if (index + 1 >= callableParameters.Count)
			return false;

		string name = callableParameters[index].Name;
		if (string.IsNullOrWhiteSpace(name))
			return false;

		string nextName = callableParameters[index + 1].Name;
		return nextName == name + "_length"
			|| nextName == name + "_specified"
			|| nextName == name + "_context";
	}

	bool TryCreatePrimitiveStringMaterializedComponents(ArgumentExpression argument, string resultType, out List<Expression> components)
	{
		components = [];
		if (argument.Value is null || GetPrimitiveStringElementType(argument.Value.ResolvedType ?? argument.ResolvedType) is not string stringElement)
			return false;
		string stringType = stringElement switch
		{
			"wchar" => "wstring",
			"achar" => "astring",
			_ => "string"
		};
		if (!CanConvertPrimitiveStringToConstArray(stringType, resultType))
			return false;

		Expression? length = CreateLengthExpression(argument.Value, argument.SourceSyntax);
		if (length is null)
			return false;
		length = LowerExpression(length) ?? length;
		components.Add(argument.Value);
		components.Add(length);
		return true;
	}

	bool IsMaterializedExpandedStorageParameter(ParameterDefinition parameter)
	{
		if (parameter.Modifier == ParameterModifier.Out || parameter.Modifier == ParameterModifier.Thrown)
			return false;
		if (parameter.Type is NamedTypeReference named && IsGenericPlaceholderParameter(named.Name))
			return true;
		string type = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? "";
		if (IsGenericPlaceholderParameter(StripTopLevelValueQualifiers(type)))
			return true;
		return parameter.Modifier == ParameterModifier.In
			&& TryGetParamsComponentShape(null, type, "value", out ParamsComponentShape shape)
			&& shape.Components.Count > 1;
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
		string actualLengthType = arguments[index + 1].Value?.ResolvedType ?? arguments[index + 1].ResolvedType ?? "";
		if (StripTopLevelValueQualifiers(actualLengthType) is not ("nuint" or "nint" or "uint" or "int" or "ulong" or "long" or "ushort" or "short"))
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
		if (GetTypeDefinition(stateTypeName) is not TypeDefinition stateDefinition)
			return false;
		FunctionDefinition? adapter = null;
		foreach (FunctionDefinition function in GetFunctions(stateDefinition))
		{
			if (function.Name == "op_iter")
			{
				adapter = function;
				break;
			}
		}
		if (adapter is null)
			return false;

		string adapterType = BuildFunctionValueType(adapter, isInstance: false);
		Dictionary<string, string> substitutions = GetConstructedTypeSubstitutions(stateDefinition, stateTypeName, sourceType);
		if (substitutions.Count > 0)
			adapterType = SubstituteGenericType(adapterType, substitutions);
		if (!TryGetCallableShape(adapterType, out CallableShape source) || !CallableShapesCompatible(source, target))
			return false;

		components.Add(CreateMethodReference(adapter, callableParameters[index].ResolvedType ?? adapterType));
		components.Add(CreateIteratorProtocolContext(argument.Value, sourceType, stateTypeName));
		return true;
	}

	static Dictionary<string, string> GetConstructedTypeSubstitutions(TypeDefinition definition, string constructedType, string stateTypeName)
	{
		Dictionary<string, string> substitutions = [];
		List<string> typeArguments = ExtractConstructedTypeArguments(constructedType);
		if (typeArguments.Count == 0 && constructedType != stateTypeName)
			typeArguments = ExtractConstructedTypeArguments(stateTypeName);
		int count = definition.GenericParameters.Count < typeArguments.Count ? definition.GenericParameters.Count : typeArguments.Count;
		for (int i = 0; i < count; i++)
			substitutions[definition.GenericParameters[i].Name] = typeArguments[i];
		return substitutions;
	}

	Expression CreateIteratorProtocolContext(Expression value, string sourceType, string stateTypeName)
	{
		if (TryGetPointerElementType(sourceType) is not null)
			return value;

		if (!CanTakeReceiverAddress(value) && currentStatementPrefix is not null)
		{
			DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("iterState"), stateTypeName, TypeReferenceForResolvedName(stateTypeName), value);
			currentStatementPrefix.Add(local);
			value = CreateVariableReference(local.Target, local.Target.ResolvedType ?? stateTypeName);
		}

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
		if (TryGetFixedArrayShape(assignment.Target?.ResolvedType, out _, out _))
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
			string? valueLifetimeFact = GetExpressionLifetimeFact(values[i]);
			UpdateAssignmentLifetimeFact(targets[i], valueLifetimeFact);
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
					ResolvedType = targets[i].ResolvedType,
					ValueLifetimeFact = valueLifetimeFact
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
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function)
			|| materializedGenericReturnParameters.ContainsKey(function)
			|| !TryGetExpandedReturnShape(call, function, out ParamsComponentShape? shape))
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
		if (expression is not null
			&& expressionRewrites.TryGetValue(expression, out Expression? rewritten)
			&& !ReferenceEquals(rewritten, expression))
		{
			return TryCreateParamsComponentExpressions(rewritten, out components);
		}
		switch (expression)
		{
			case null:
				return false;

			case ParenthesizedExpression parenthesized:
				return TryCreateParamsComponentExpressions(parenthesized.Expression, out components);

			case CastExpression { LifetimeCastKind: not null } cast:
				if (!TryCreateParamsComponentExpressions(cast.Expression, out components))
					return false;
				foreach (Expression component in components)
					component.ValueLifetimeFact = cast.LifetimeBinding ?? component.ValueLifetimeFact;
				return true;

			case CastExpression { Type: not null } cast when ContainsLifetimeAnnotation(cast.Type):
				if (!TryCreateParamsComponentExpressions(cast.Expression, out components))
					return false;
				foreach (Expression component in components)
					component.ValueLifetimeFact = cast.LifetimeBinding ?? component.ValueLifetimeFact;
				return true;

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

			case IndexExpression index:
				return TryCreateIndexedParamsComponentExpressions(index, out components);

			case Expression fixedArray
				when TryCreateFixedArrayParamsComponentExpressions(fixedArray, out components):
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
				components.Add(CreateDelegateContextExpression(member.Target, function));
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

			case InitializerExpression initializer:
				return TryCreateInitializerParamsComponentExpressions(initializer, out components);

			case LambdaExpression lambda
				when TryGetCallableShape(lambda.ResolvedType, out CallableShape callable) && callable.Kind == "delegate":
				return TryCreateParamsComponentExpressions(LowerLambdaExpression(lambda), out components);

			case ArrayExpression array:
				return TryCreateArrayParamsComponentExpressions(array, out components);

			case LiteralExpression { Kind: LiteralKind.String } literal:
				return TryCreateStringParamsComponentExpressions(literal, out components);

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

	bool TryCreateInitializerParamsComponentExpressions(InitializerExpression initializer, out List<Expression> components)
	{
		components = [];
		if (!TryGetParamsComponentShape(null, initializer.ResolvedType, "value", out ParamsComponentShape shape))
			return false;

		bool hasNamed = false;
		bool hasPositional = false;
		Dictionary<string, Expression> named = [];
		foreach (InitializerItem item in initializer.Items)
		{
			if (item.Expression is null)
				return false;
			string? targetName = GetSingleInitializerTargetName(item.Target);
			if (targetName is null)
			{
				hasPositional = true;
				if (hasNamed || components.Count >= shape.Components.Count)
					return false;
				components.Add(LowerExpression(item.Expression) ?? item.Expression);
				continue;
			}

			hasNamed = true;
			if (hasPositional || named.ContainsKey(targetName))
				return false;
			if (FindParamsComponent(shape, targetName) is null)
				return false;
			named[targetName] = LowerExpression(item.Expression) ?? item.Expression;
		}

		if (hasNamed)
		{
			components.Clear();
			foreach (ParamsComponent component in shape.Components)
			{
				if (!named.TryGetValue(component.Name, out Expression? expression))
					return false;
				components.Add(expression);
			}
		}

		return components.Count == shape.Components.Count;
	}

	bool TryCreateFixedArrayParamsComponentExpressions(Expression expression, out List<Expression> components)
	{
		components = [];
		if (!TryGetFixedArrayShape(expression.ResolvedType, out string elementType, out long length))
			return false;

		string pointerType = elementType + "*";
		components.Add(new CastExpression
		{
			SourceSyntax = expression.SourceSyntax,
			Type = TypeReferenceForResolvedName(pointerType),
			Expression = CloneParamsExpansionExpression(expression),
			ResolvedType = pointerType
		});
		components.Add(NumberLiteral(length.ToString(System.Globalization.CultureInfo.InvariantCulture), "nuint"));
		return true;
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

	Expression CreateDelegateContextExpression(Expression target, FunctionDefinition? function = null)
	{
		Expression context;
		if (function is not null && GetExplicitThisParameter(function)?.Modifier == ParameterModifier.In)
		{
			Expression operand = CloneParamsExpansionExpression(target) ?? target;
			context = new UnaryExpression
			{
				SourceSyntax = target.SourceSyntax,
				Operator = UnaryOperator.AddressOf,
				Operand = operand,
				ResolvedType = AddPointer(operand.ResolvedType ?? target.ResolvedType ?? ErrorType)
			};
		}
		else
		{
			context = function is null
				? CloneParamsExpansionExpression(target) ?? target
				: CreateReceiverArgument(target, function).Value ?? target;
		}
		if (TryGetPointerElementType(context.ResolvedType) is null)
		{
			context = new UnaryExpression
			{
				SourceSyntax = target.SourceSyntax,
				Operator = UnaryOperator.AddressOf,
				Operand = context,
				ResolvedType = AddPointer(context.ResolvedType ?? ErrorType)
			};
		}
		return new CastExpression
		{
			SourceSyntax = target.SourceSyntax,
			Kind = CastKind.Type,
			Type = TypeReferenceForResolvedName("void*"),
			Expression = context,
			ResolvedType = "void*"
		};
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
		if (!TryGetIteratorProtocolParameterTypes(iterType, out List<string>? parameterTypes) || parameterTypes is null)
			return false;

		List<string> parameters = ["void*", .. parameterTypes];
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
		if (TryRewriteMaterializedGenericReturn(statement, out rewritten))
			return true;
		if (currentRewriteFunction is null || !expandedReturnShapes.TryGetValue(currentRewriteFunction, out ParamsComponentShape? shape))
			return false;
		if (TryRewriteExpandedReturnCall(statement, shape, out rewritten))
			return true;
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

	bool TryRewriteExpandedReturnCall(ReturnStatement statement, ParamsComponentShape shape, out Statement rewritten)
	{
		rewritten = statement;
		if (statement.Expression is not CallExpression call)
			return false;
		if ((!TryGetExpandedReturnCallShape(call, out ParamsComponentShape? callShape) || callShape.Components.Count != shape.Components.Count)
			&& !CallableInvocationMatchesExpandedReturnShape(call, shape))
		{
			return false;
		}

		if (callTargets.TryGetValue(call, out FunctionDefinition? function))
		{
			AddImplicitDefaultArguments(call);
			ExpandParamsArguments(call);
			AddImplicitSizeOfArguments(call);
			AddImplicitNameOfArguments(call);
			AddImplicitWithinArgument(call);
			AddImplicitVTableOfArguments(call);
			if (call.Target is MemberReferenceExpression { Target: Expression receiver } member
				&& IsInstanceInvocationFunction(function)
				&& !IsPropertyGetterReference(member)
				&& !IsPropertySetterReference(member)
				&& FindContainingType(function) is not InterfaceDefinition)
			{
				RewriteInstanceInvocation(call, member, receiver, function);
			}
		}

		for (int i = 1; i < shape.Components.Count; i++)
		{
			ParameterDefinition parameter = currentRewriteFunction!.Parameters[^ (shape.Components.Count - i)];
			call.Arguments.Add(new ArgumentExpression
			{
				SourceSyntax = statement.SourceSyntax,
				Modifier = ArgumentModifier.Out,
				Value = CreateVariableReference(parameter, parameter.ResolvedType ?? shape.Components[i].Type),
				ResolvedType = shape.Components[i].Type
			});
		}
		preparedExpandedReturnCalls.Add(call);
		call.ResolvedType = shape.Components[0].Type;
		rewritten = new ReturnStatement
		{
			SourceSyntax = statement.SourceSyntax,
			ResolvedType = "void",
			Expression = call
		};
		return true;
	}

	bool TryGetExpandedReturnCallShape(CallExpression call, out ParamsComponentShape shape)
	{
		if (callTargets.TryGetValue(call, out FunctionDefinition? function)
			&& TryGetExpandedReturnShape(call, function, out shape))
		{
			return true;
		}
		return TryGetParamsComponentShape(null, call.ResolvedType, "result", out shape);
	}

	bool CallableInvocationMatchesExpandedReturnShape(CallExpression call, ParamsComponentShape shape)
	{
		if (shape.Components.Count <= 1
			|| !TryGetCallableShape(call.Target?.ResolvedType, out CallableShape callable)
			|| callable.ReturnType != shape.Components[0].Type
			|| callable.Parameters.Count < shape.Components.Count - 1)
		{
			return false;
		}

		int outStart = callable.Parameters.Count - (shape.Components.Count - 1);
		for (int i = 1; i < shape.Components.Count; i++)
		{
			string expected = "out " + shape.Components[i].Type;
			if (callable.Parameters[outStart + i - 1] != expected)
				return false;
		}
		return true;
	}

	bool TryRewriteMaterializedGenericReturn(ReturnStatement statement, out Statement rewritten)
	{
		rewritten = statement;
		if (currentRewriteFunction is null
			|| !materializedGenericReturnParameters.TryGetValue(currentRewriteFunction, out ParameterDefinition? parameter)
			|| statement.Expression is null)
			return false;

		List<Statement> statements =
		[
			new ExpressionStatement
			{
				SourceSyntax = statement.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = statement.SourceSyntax,
					Target = CreateVariableReference(parameter, parameter.ResolvedType ?? ErrorType),
					Operator = AssignmentOperator.Assign,
					Value = LowerExpression(statement.Expression),
					ResolvedType = parameter.ResolvedType ?? ErrorType
				}
			},
			new ReturnStatement
			{
				SourceSyntax = statement.SourceSyntax,
				ResolvedType = "void"
			}
		];
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
		if (index.Target is MemberExpression propertyMember
			&& TryCreateMaterializedGenericPropertyGetterReference(propertyMember, out MemberReferenceExpression? propertyGetter))
		{
			CallExpression call = RewritePropertyGetterCall(propertyGetter, index.Arguments);
			call.ResolvedType = index.ResolvedType ?? call.ResolvedType;
			return TryCreateParamsComponentExpressions(call, out components);
		}
		if (index.Target is MemberExpression member
			&& expressionRewrites.TryGetValue(member, out Expression? rewritten)
			&& rewritten is MemberReferenceExpression getter
			&& IsPropertyGetterReference(getter))
		{
			CallExpression call = RewritePropertyGetterCall(getter, index.Arguments);
			call.ResolvedType = index.ResolvedType ?? call.ResolvedType;
			return TryCreateParamsComponentExpressions(call, out components);
		}
		if (TryGetFixedArrayShape(index.ResolvedType, out _, out _))
			return TryCreateFixedArrayParamsComponentExpressions(index, out components);
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
			Expression offset = CreateArraySlicePointerOffset(index.Target?.ResolvedType, start, index.SourceSyntax);
			components.Add(new BinaryExpression
			{
				SourceSyntax = index.SourceSyntax,
				Left = targetComponents[0],
				Operator = BinaryOperator.Add,
				Right = offset,
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

	Expression CreateArraySlicePointerOffset(string? arrayType, Expression start, SyntaxNode? syntax)
	{
		string? elementType = TryGetArrayElementType(arrayType);
		if (elementType is null || !IsGenericPlaceholderParameter(StripTopLevelValueQualifiers(elementType)))
			return start;

		return new BinaryExpression
		{
			SourceSyntax = syntax,
			Left = start,
			Operator = BinaryOperator.Multiply,
			Right = LowerSizeOfExpression(new SizeOfExpression
			{
				SourceSyntax = syntax,
				Type = TypeReferenceForResolvedName(elementType),
				ResolvedType = "nuint"
			}),
			ResolvedType = "nuint"
		};
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
		if (callTargets.TryGetValue(call, out FunctionDefinition? function))
		{
			if (!TryGetExpandedReturnShape(call, function, out ParamsComponentShape? functionShape))
				return false;
			return TryCreateExpandedReturnCallComponents(call, functionShape, out components);
		}

		if (!TryGetParamsComponentShape(null, call.ResolvedType, "result", out ParamsComponentShape shape) || shape.Components.Count <= 1)
			return false;
		return TryCreateExpandedReturnCallComponents(call, shape, out components);
	}

	bool TryCreateExpandedReturnCallComponents(CallExpression call, ParamsComponentShape shape, out List<Expression> components)
	{
		components = [];
		if (TryCreateMaterializedGenericReturnCallComponents(call, shape, out components))
			return true;
		if (currentStatementPrefix is null || shape.Components.Count == 0)
			return false;
		callTargets.TryGetValue(call, out FunctionDefinition? function);
		if (function is not null
			&& call.Target is MemberReferenceExpression { Target: Expression receiver } member
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

	bool TryCreateMaterializedGenericReturnCallComponents(CallExpression call, ParamsComponentShape shape, out List<Expression> components)
	{
		components = [];
		if (currentStatementPrefix is null
			|| !callTargets.TryGetValue(call, out FunctionDefinition? function)
			|| !IsMaterializedGenericReturnFunction(function))
			return false;

		if (call.Target is MemberReferenceExpression { Target: Expression receiver } member
			&& IsInstanceInvocationFunction(function)
			&& !IsPropertyGetterReference(member)
			&& !IsPropertySetterReference(member)
			&& FindContainingType(function) is not InterfaceDefinition)
		{
			RewriteInstanceInvocation(call, member, receiver, function);
		}

		DeclarationStatement storage = CreateMaterializedGenericReturnStorage(call.ResolvedType ?? shape.TypeName, call.SourceSyntax);
		currentStatementPrefix.Add(storage);
		call.Arguments.Add(new ArgumentExpression
		{
			SourceSyntax = call.SourceSyntax,
			Modifier = ArgumentModifier.Out,
			Value = CreateVariableReference(storage.Target, storage.Target.ResolvedType ?? ErrorType),
			ResolvedType = storage.Target.ResolvedType ?? ErrorType
		});
		currentStatementPrefix.Add(new ExpressionStatement
		{
			SourceSyntax = call.SourceSyntax,
			ResolvedType = "void",
			Expression = LowerExpression(call)
		});
		components.AddRange(CreateMaterializedComponentExpressions(storage.Target, shape));
		return components.Count > 0;
	}

	DeclarationStatement CreateMaterializedGenericReturnStorage(string expandedType, SyntaxNode? sourceSyntax)
	{
		string typeName = $"struct({expandedType})";
		TypeReference type = new MaterializedStructTypeReference
		{
			SourceSyntax = sourceSyntax,
			ResolvedType = typeName,
			ParamsType = TypeReferenceForResolvedName(expandedType)
		};
		return CreateGeneratedLocal(NewGeneratedLocalName("result"), typeName, type, null);
	}

	List<Expression> CreateMaterializedComponentExpressions(DeclarationTarget storage, ParamsComponentShape shape)
	{
		List<Expression> components = [];
		Expression target = CreateVariableReference(storage, storage.ResolvedType ?? ErrorType);
		foreach (ParamsComponent component in shape.Components)
		{
			components.Add(new MemberExpression
			{
				SourceSyntax = storage.SourceSyntax,
				Target = target,
				Name = component.Name,
				ResolvedType = component.Type
			});
		}
		return components;
	}

	bool IsParamsComponentNamed(Expression expression, string name)
	{
		return expression switch
		{
			VariableReferenceExpression { Variable: not null } variable => IsParamsExpansionComponentNamed(variable.Variable, name),
			MemberReferenceExpression { Member: not null } member => IsParamsExpansionComponentNamed(member.Member, name),
			MemberExpression member => member.Name == name,
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
		if (expression is not null
			&& expressionRewrites.TryGetValue(expression, out Expression? rewritten)
			&& !ReferenceEquals(rewritten, expression))
			return CloneParamsExpansionExpression(rewritten);

		return expression switch
		{
			null => null,
			LiteralExpression literal => new LiteralExpression { SourceSyntax = literal.SourceSyntax, Kind = literal.Kind, Text = literal.Text, Value = literal.Value, ResolvedType = literal.ResolvedType },
			NamedExpression named => CloneNamedExpression(named),
			VariableReferenceExpression variable => new VariableReferenceExpression { SourceSyntax = variable.SourceSyntax, Variable = variable.Variable, ResolvedType = variable.ResolvedType, SlotLifetimeFact = variable.SlotLifetimeFact, ValueLifetimeFact = variable.ValueLifetimeFact },
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
			CallExpression call => CloneCallExpression(call),
			_ => expression
		};
	}

	CallExpression CloneCallExpression(CallExpression call)
	{
		CallExpression clone = new()
		{
			SourceSyntax = call.SourceSyntax,
			Target = CloneParamsExpansionExpression(call.Target),
			ResolvedType = call.ResolvedType
		};
		clone.TypeArguments.AddRange(call.TypeArguments);
		foreach (ArgumentExpression argument in call.Arguments)
		{
			clone.Arguments.Add(new ArgumentExpression
			{
				SourceSyntax = argument.SourceSyntax,
				Name = argument.Name,
				Modifier = argument.Modifier,
				Type = argument.Type,
				Target = argument.Target,
				Value = CloneParamsExpansionExpression(argument.Value),
				ResolvedType = argument.ResolvedType
			});
		}
		if (callTargets.TryGetValue(call, out FunctionDefinition? target))
			callTargets[clone] = target;
		if (callableInvocationParameters.TryGetValue(call, out List<ParameterDefinition>? parameters))
			callableInvocationParameters[clone] = parameters;
		if (callGenericSubstitutions.TryGetValue(call, out Dictionary<string, string>? substitutions))
			callGenericSubstitutions[clone] = new Dictionary<string, string>(substitutions, System.StringComparer.Ordinal);
		return clone;
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
