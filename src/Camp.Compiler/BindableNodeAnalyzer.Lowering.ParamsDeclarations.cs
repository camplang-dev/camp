using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void ExpandParamsDeclarations(Module module)
	{
		for (int i = 0; i < module.Definitions.Count; i++)
		{
			switch (module.Definitions[i])
			{
				case VariableDefinition variable:
					if (TryExpandParamsVariable(variable, out List<VariableDefinition>? variables))
					{
						module.Definitions.RemoveAt(i);
						module.Definitions.InsertRange(i, variables);
						i += variables.Count - 1;
					}
					break;

				case ClassDefinition classDefinition:
					ExpandParamsFields(classDefinition.Fields);
					foreach (FunctionDefinition function in classDefinition.Functions)
						ExpandParamsFunctionDeclarations(function);
					break;

				case StructDefinition structDefinition:
					ExpandParamsFields(structDefinition.Fields);
					foreach (FunctionDefinition function in structDefinition.Functions)
						ExpandParamsFunctionDeclarations(function);
					break;

				case InterfaceDefinition interfaceDefinition:
					foreach (FunctionDefinition function in interfaceDefinition.Functions)
						ExpandParamsFunctionDeclarations(function);
					break;

				case EnumDefinition enumDefinition:
					foreach (FunctionDefinition function in enumDefinition.Functions)
						ExpandParamsFunctionDeclarations(function);
					break;

				case NewtypeDefinition newtypeDefinition:
					foreach (FunctionDefinition function in newtypeDefinition.Functions)
						ExpandParamsFunctionDeclarations(function);
					break;

				case ParamsDefinition paramsDefinition:
					foreach (FunctionDefinition function in paramsDefinition.Functions)
						ExpandParamsFunctionDeclarations(function);
					break;

				case FunctionDefinition function:
					ExpandParamsFunctionDeclarations(function);
					break;
			}
		}
	}

	void ExpandParamsFunctionDeclarations(FunctionDefinition function)
	{
		ExpandParamsReturn(function);
		ExpandParamsParameters(function, function.Parameters);
		if (function.Body is not null)
		{
			FunctionDefinition? previousFunction = currentRewriteFunction;
			TypeDefinition? previousType = currentRewriteContainingType;
			currentRewriteFunction = function;
			currentRewriteContainingType = FindContainingType(function);
			ExpandParamsLocalDeclarations(function.Body.Statements);
			currentRewriteFunction = previousFunction;
			currentRewriteContainingType = previousType;
		}
	}

	void ExpandParamsReturn(FunctionDefinition function)
	{
		if (TryExpandMaterializedGenericReturn(function))
			return;
		if (function.ReturnType is null || !TryGetParamsComponentShape(function.ReturnType, function.ResolvedType, "result", out ParamsComponentShape shape))
			return;
		if (shape.Components.Count == 0)
			return;

		expandedReturnShapes[function] = shape;
		ParamsComponent first = shape.Components[0];
		function.ReturnType = new NamedTypeReference
		{
			Name = GetTypeReferenceName(function.ReturnType) ?? first.Type,
			ResolvedType = first.Type,
			SourceSyntax = function.ReturnType.SourceSyntax
		};
		function.ResolvedType = first.Type;

		for (int i = 1; i < shape.Components.Count; i++)
		{
			ParamsComponent component = shape.Components[i];
			function.Parameters.Add(new ParameterDefinition
			{
				SourceSyntax = function.SourceSyntax,
				Name = component.ExpandedName,
				Symbol = component.ExpandedName,
				Public = function.Public,
				Internal = function.Internal,
				Modifier = ParameterModifier.Out,
				Type = new NamedTypeReference { Name = component.Type, ResolvedType = component.Type },
				ResolvedType = component.Type
			});
		}
	}

	bool TryExpandMaterializedGenericReturn(FunctionDefinition function)
	{
		if (function.ReturnType is null || !IsErasedGenericReturn(function))
			return false;

		ParameterDefinition result = new()
		{
			SourceSyntax = function.SourceSyntax,
			Name = "__result",
			Symbol = "__result",
			Public = function.Public,
			Internal = function.Internal,
			Modifier = ParameterModifier.Out,
			Type = function.ReturnType,
			ResolvedType = function.ResolvedType
		};
		materializedGenericReturnParameters[function] = result;
		function.Parameters.Add(result);
		function.ReturnType = new PrimitiveTypeReference { Type = PrimitiveType.Void, ResolvedType = "void" };
		function.ResolvedType = "void";
		return true;
	}

	bool IsErasedGenericReturn(FunctionDefinition function)
	{
		string returnType = StripTopLevelValueQualifiers(function.ResolvedType ?? function.ReturnType?.ResolvedType ?? "");
		if (string.IsNullOrWhiteSpace(returnType))
			return false;

		foreach (GenericParameter parameter in function.GenericParameters)
			if (parameter.Name == returnType && parameter.Constraint is AnyTypeReference or CopyableTypeReference)
				return true;
		if (FindContainingType(function) is TypeDefinition containingType)
			foreach (GenericParameter parameter in containingType.GenericParameters)
				if (parameter.Name == returnType && parameter.Constraint is AnyTypeReference or CopyableTypeReference)
					return true;
		return false;
	}

	void ExpandParamsParameters(FunctionDefinition function, List<ParameterDefinition> parameters)
	{
		for (int i = 0; i < parameters.Count; i++)
		{
			ParameterDefinition parameter = parameters[i];
			if (parameter is WithinParameterDefinition or SizeOfParameterDefinition or NameOfParameterDefinition or VTableOfParameterDefinition
				|| parameter is ThisParameterDefinition && !ShouldExpandThisParameter(function, parameter))
				continue;
			if (!TryGetParamsComponentShape(parameter.Type, parameter.ResolvedType, parameter.Name, out ParamsComponentShape shape))
				continue;

			List<ParameterDefinition> components = [];
			for (int componentIndex = 0; componentIndex < shape.Components.Count; componentIndex++)
				components.Add(CreateExpandedParameter(parameter, shape.Components[componentIndex], componentIndex == 0));
			RegisterParamsExpansion(parameter, shape, components);

			parameters.RemoveAt(i);
			parameters.InsertRange(i, components);
			i += components.Count - 1;
		}
	}

	bool ShouldExpandThisParameter(FunctionDefinition function, ParameterDefinition parameter)
	{
		if (parameter.SourceSyntax is null)
			return false;
		if (generatedIteratorFactories.Contains(function) && TryGetParamsComponentShape(parameter.Type, parameter.ResolvedType, parameter.Name, out _))
			return true;
		if (TryGetParamsComponentShape(parameter.Type, parameter.ResolvedType, parameter.Name, out ParamsComponentShape shape)
			&& shape.Components.Count > 1
			&& shape.Kind != ParamsComponentShapeKind.Array)
			return true;
		string type = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? "";
		if (!typeDefinitions.TryGetValue(BaseTypeName(type), out TypeDefinition? definition))
			return false;
		return definition is NewtypeDefinition { UnderlyingType: CallableTypeReference { Kind: CallableKind.Delegate or CallableKind.Once } or IterTypeReference };
	}

	void ExpandParamsFields(List<FieldDefinition> fields)
	{
		for (int i = 0; i < fields.Count; i++)
		{
			FieldDefinition field = fields[i];
			if (!TryGetParamsComponentShape(field.Type, field.ResolvedType, field.Name, out ParamsComponentShape shape))
				continue;

			List<FieldDefinition> components = [];
			List<Expression?> initialValues = GetParamsComponentInitialValues(field.InitialValue, shape, deferCurrentAllocator: false);
			for (int componentIndex = 0; componentIndex < shape.Components.Count; componentIndex++)
				components.Add(CreateExpandedField(field, shape.Components[componentIndex], initialValues[componentIndex]));
			RegisterParamsExpansion(field, shape, components);

			fields.RemoveAt(i);
			fields.InsertRange(i, components);
			i += components.Count - 1;
		}
	}

	void ExpandParamsLocalDeclarations(List<Statement> statements)
	{
		for (int i = 0; i < statements.Count; i++)
		{
			switch (statements[i])
			{
				case BlockStatement block:
					ExpandParamsLocalDeclarations(block.Statements);
					break;

				case DeclarationStatement declaration when TryExpandParamsDeconstruction(declaration, out List<Statement>? declarations):
					statements.RemoveAt(i);
					statements.InsertRange(i, declarations);
					i += declarations.Count - 1;
					break;

				case DeclarationStatement declaration when TryExpandParamsLocalDeclaration(declaration, out List<Statement>? declarations):
					statements.RemoveAt(i);
					statements.InsertRange(i, declarations);
					i += declarations.Count - 1;
					break;

				case IfStatement ifStatement:
					ExpandParamsNestedStatement(ifStatement.Body);
					ExpandParamsNestedStatement(ifStatement.ElseBody);
					break;

				case WhileStatement whileStatement:
					ExpandParamsNestedStatement(whileStatement.Body);
					break;

				case DoWhileStatement doWhileStatement:
					ExpandParamsNestedStatement(doWhileStatement.Body);
					break;

				case ForStatement forStatement:
					if (forStatement.Condition.Declaration is not null && TryExpandParamsLocalDeclaration(forStatement.Condition.Declaration, out _))
						Report(GetRange(forStatement.Condition.Declaration.SourceSyntax), "Params-typed for-loop declarations cannot be expanded in this phase.");
					ExpandParamsNestedStatement(forStatement.Body);
					break;

				case ForeachStatement foreachStatement:
					ExpandParamsNestedStatement(foreachStatement.Body);
					break;

				case SwitchStatement switchStatement:
					ExpandParamsLocalDeclarations(switchStatement.Statements);
					break;

				case TryStatement tryStatement:
					ExpandParamsNestedStatement(tryStatement.Body);
					foreach (CatchStatement catchStatement in tryStatement.Catches)
						ExpandParamsNestedStatement(catchStatement.Body);
					ExpandParamsNestedStatement(tryStatement.Finally);
					break;

				case CatchStatement catchStatement:
					ExpandParamsNestedStatement(catchStatement.Body);
					break;

				case FinallyStatement finallyStatement:
					ExpandParamsNestedStatement(finallyStatement.Body);
					break;

				case WithinStatement withinStatement:
					ExpandParamsNestedStatement(withinStatement.Body);
					break;
			}
		}
	}

	void ExpandParamsNestedStatement(Statement? statement)
	{
		if (statement is BlockStatement block)
			ExpandParamsLocalDeclarations(block.Statements);
		else if (statement is not null)
			ExpandParamsLocalDeclarations([statement]);
	}

	bool TryExpandParamsDeconstruction(DeclarationStatement declaration, out List<Statement> declarations)
	{
		declarations = [];
		if (declaration.Target.Names.Count <= 1)
			return false;
		if (!TryCreateParamsComponentExpressions(declaration.InitialValue, out List<Expression> components))
			return false;
		if (components.Count != declaration.Target.Names.Count)
		{
			Report(GetRange(declaration.SourceSyntax), $"Deconstruction declares {declaration.Target.Names.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} target(s), but the initializer has {components.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} component(s).");
			return false;
		}

		for (int i = 0; i < declaration.Target.Names.Count; i++)
		{
			DeclarationStatement componentDeclaration = new()
			{
				SourceSyntax = declaration.SourceSyntax,
				InitialValue = components[i],
				ResolvedType = declaration.ResolvedType
			};
			componentDeclaration.Target.SourceSyntax = declaration.Target.SourceSyntax;
			componentDeclaration.Target.Type = declaration.Target.Type is AutoTypeReference ? null : CloneType(declaration.Target.Type);
			componentDeclaration.Target.ResolvedType = components[i].ResolvedType;
			componentDeclaration.Target.Names.Add(declaration.Target.Names[i]);
			declarations.Add(componentDeclaration);
		}

		return true;
	}

	bool TryExpandParamsVariable(VariableDefinition variable, out List<VariableDefinition> variables)
	{
		variables = [];
		if (!TryGetParamsComponentShape(variable.Type, variable.ResolvedType, variable.Name, out ParamsComponentShape shape))
			return false;

		List<Expression?> initialValues = GetParamsComponentInitialValues(variable.InitialValue, shape, deferCurrentAllocator: false);
		for (int componentIndex = 0; componentIndex < shape.Components.Count; componentIndex++)
			variables.Add(CreateExpandedVariable(variable, shape.Components[componentIndex], initialValues[componentIndex]));
		RegisterParamsExpansion(variable, shape, variables);
		return variables.Count > 0;
	}

	bool TryExpandParamsLocalDeclaration(DeclarationStatement declaration, out List<Statement> declarations)
	{
		declarations = [];
		if (declaration.Target.Names.Count != 1)
			return false;

		string name = declaration.Target.Names[0];
		if (!TryGetParamsComponentShape(declaration.Target.Type, declaration.Target.ResolvedType, name, out ParamsComponentShape shape))
			return false;

		declaration.InitialValue = NormalizeExpandedReturnPropertyGetter(declaration.InitialValue);
		bool finallyCleanup = false;
		Expression? initialValue = declaration.InitialValue;
		if (initialValue is FinallyCleanupExpression { Kind: FinallyCleanupKind.Delete, Expression: not null } finallyCleanupExpression)
		{
			finallyCleanup = true;
			initialValue = NormalizeExpandedReturnPropertyGetter(finallyCleanupExpression.Expression);
		}
		bool materializedGenericReturnInitializer = initialValue is CallExpression initialCall
			&& callTargets.TryGetValue(initialCall, out FunctionDefinition? initialFunction)
			&& IsMaterializedGenericReturnFunction(initialFunction);
		if (!materializedGenericReturnInitializer)
			CaptureParamsArrayConstructionLength(initialValue, shape, declarations);
		if (initialValue is LambdaExpression lambda)
			PrepareLambdaContextLocal(lambda, declarations);
		List<Expression?> initialValues = TryCreateIteratorFactoryProtocolInitialValues(initialValue, shape, declarations, declaration.SourceSyntax, out List<Expression?>? iteratorProtocolInitialValues)
			? iteratorProtocolInitialValues!
			: materializedGenericReturnInitializer
			? CreateNullInitialValues(shape)
			: TryCreateTargetTypedExpandedReceiverInitialValues(initialValue, shape, declarations, out List<Expression?>? expandedReceiverInitialValues)
			? expandedReceiverInitialValues!
			: GetParamsComponentInitialValues(initialValue, shape, deferCurrentAllocator: true);
		List<DeclarationTarget> targets = [];
		for (int componentIndex = 0; componentIndex < shape.Components.Count; componentIndex++)
		{
			DeclarationStatement componentDeclaration = CreateExpandedDeclaration(declaration, shape.Components[componentIndex], initialValues[componentIndex]);
			declarations.Add(componentDeclaration);
			targets.Add(componentDeclaration.Target);
		}
		if (materializedGenericReturnInitializer && initialValue is CallExpression materializedCall)
		{
			AppendMaterializedGenericReturnAssignments(materializedCall, shape, targets, declarations, declaration.SourceSyntax);
		}
		else if (initialValue is CallExpression call
			&& callTargets.TryGetValue(call, out FunctionDefinition? function)
			&& (TryGetExpandedReturnShape(call, function, out ParamsComponentShape? callShape)
				|| TryUseTargetShapeForGenericExpandedReturn(function, shape, out callShape))
			&& callShape.Components.Count == shape.Components.Count)
		{
			AddImplicitDefaultArguments(call);
			ExpandParamsArguments(call);
			AddImplicitSizeOfArguments(call);
			AddImplicitNameOfArguments(call);
			AddImplicitWithinArgument(call);
			AddImplicitVTableOfArguments(call);
			((DeclarationStatement)declarations[0]).InitialValue = null;
			for (int i = 1; i < targets.Count; i++)
			{
				call.Arguments.Add(new ArgumentExpression
				{
					SourceSyntax = declaration.SourceSyntax,
					Modifier = ArgumentModifier.Out,
					Value = CreateVariableReference(targets[i], shape.Components[i].Type),
					ResolvedType = shape.Components[i].Type
				});
			}
			preparedExpandedReturnCalls.Add(call);
			declarations.Add(new ExpressionStatement
			{
				SourceSyntax = declaration.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = declaration.SourceSyntax,
					Target = CreateVariableReference(targets[0], shape.Components[0].Type),
					Operator = AssignmentOperator.Assign,
					Value = call,
					ResolvedType = shape.Components[0].Type
				}
			});
		}
		else if (initialValue is CallExpression callableCall
			&& TryGetCallableExpandedReturnShape(callableCall, shape, out ParamsComponentShape callableReturnShape)
			&& callableReturnShape.Components.Count == shape.Components.Count)
		{
			ExpandParamsArguments(callableCall);
			((DeclarationStatement)declarations[0]).InitialValue = null;
			for (int i = 1; i < targets.Count; i++)
			{
				callableCall.Arguments.Add(new ArgumentExpression
				{
					SourceSyntax = declaration.SourceSyntax,
					Modifier = ArgumentModifier.Out,
					Value = CreateVariableReference(targets[i], shape.Components[i].Type),
					ResolvedType = shape.Components[i].Type
				});
			}
			declarations.Add(new ExpressionStatement
			{
				SourceSyntax = declaration.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = declaration.SourceSyntax,
					Target = CreateVariableReference(targets[0], shape.Components[0].Type),
					Operator = AssignmentOperator.Assign,
					Value = callableCall,
					ResolvedType = shape.Components[0].Type
				}
			});
		}
		RegisterParamsExpansion(declaration.Target, shape, targets);
		if (finallyCleanup && targets.Count > 0)
		{
			Expression target = CreateVariableReference(targets[0], shape.Components[0].Type);
			declarations.Add(new ExpressionStatement
			{
				SourceSyntax = declaration.SourceSyntax,
				ResolvedType = "void",
				Expression = new FinallyCleanupExpression
				{
					SourceSyntax = declaration.SourceSyntax,
					Expression = target,
					ResolvedType = target.ResolvedType
				}
			});
		}
		return declarations.Count > 0;
	}

	bool TryCreateTargetTypedExpandedReceiverInitialValues(Expression? initialValue, ParamsComponentShape shape, List<Statement> declarations, out List<Expression?>? initialValues)
	{
		initialValues = null;
		Expression? rewrittenInitialValue = initialValue is not null && expressionRewrites.TryGetValue(initialValue, out Expression? rewrite)
			? rewrite
			: initialValue;
		if (rewrittenInitialValue is not MemberReferenceExpression { Target: not null, Member: FunctionDefinition function } memberReference
			|| FindContainingType(function) is InterfaceDefinition)
		{
			return false;
		}

		List<Statement>? previousPrefix = currentStatementPrefix;
		currentStatementPrefix = declarations;
		try
		{
			if (!TryCreateExpandedReceiverMethodDelegateComponents(memberReference, function, out List<Expression> targetTypedComponents, shape)
				|| targetTypedComponents.Count != shape.Components.Count)
			{
				return false;
			}

			initialValues = [];
			foreach (Expression component in targetTypedComponents)
				initialValues.Add(component);
			return true;
		}
		finally
		{
			currentStatementPrefix = previousPrefix;
		}
	}

	bool TryCreateIteratorFactoryProtocolInitialValues(Expression? initialValue, ParamsComponentShape shape, List<Statement> declarations, SyntaxNode? syntax, out List<Expression?>? initialValues)
	{
		initialValues = null;
		if (shape.Kind != ParamsComponentShapeKind.Iter
			|| shape.Components.Count != 2
			|| initialValue is not CallExpression call
			|| !TryCreateIteratorFactoryProtocolComponents(call, syntax, declarations, out List<Expression>? components)
			|| components is null
			|| components.Count != 2)
			return false;

		initialValues = [components[0], components[1]];
		return true;
	}

	bool TryCreateIteratorFactoryProtocolComponents(CallExpression call, SyntaxNode? syntax, List<Statement> declarations, out List<Expression>? components)
	{
		components = null;
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function)
			|| !generatedIteratorFactories.Contains(function))
			return false;

		AddImplicitDefaultArguments(call);
		ExpandParamsArguments(call);
		AddImplicitSizeOfArguments(call);
		AddImplicitNameOfArguments(call);
		AddImplicitWithinArgument(call);
		AddImplicitVTableOfArguments(call);

		string stateType = function.ResolvedType ?? function.ReturnType?.ResolvedType ?? call.ResolvedType ?? ErrorType;
		if (callGenericSubstitutions.TryGetValue(call, out Dictionary<string, string>? substitutions))
			stateType = SubstituteGenericType(stateType, substitutions);
		string stateTypeName = TryGetPointerElementType(stateType) ?? stateType;
		if (GetTypeDefinition(stateTypeName) is not TypeDefinition state)
			return false;

		FunctionDefinition? adapter = null;
		foreach (FunctionDefinition candidate in GetFunctions(state))
		{
			if (candidate.Name == "op_iter")
			{
				adapter = candidate;
				break;
			}
		}
		if (adapter is null)
			return false;

		string localName = NewGeneratedLocalName("iterState");
		DeclarationStatement local = CreateGeneratedLocal(localName, stateType, TypeReferenceForResolvedName(stateType), call);
		local.SourceSyntax = syntax;
		declarations.Add(local);

		Expression context = CreateVariableReference(local.Target, stateType);
		if (TryGetPointerElementType(stateType) is null)
		{
			context = new UnaryExpression
			{
				SourceSyntax = syntax,
				Operator = UnaryOperator.AddressOf,
				Operand = context,
				ResolvedType = AddPointer(stateType)
			};
		}

		context = new CastExpression
		{
			SourceSyntax = syntax,
			Kind = CastKind.Type,
			Type = TypeReferenceForResolvedName("void*"),
			Expression = context,
			ResolvedType = "void*"
		};

		components =
		[
			CreateMethodReference(adapter, BuildFunctionValueType(adapter, isInstance: false)),
			context
		];
		return true;
	}

	void CaptureParamsArrayConstructionLength(Expression? initialValue, ParamsComponentShape shape, List<Statement> declarations)
	{
		if (shape.Kind != ParamsComponentShapeKind.Array
			|| shape.Components.Count != 2
			|| !TryGetParamsArrayConstruction(initialValue, out ConstructionExpression construction)
			|| construction.ElementCount is null
			|| IsRepeatableArrayLengthExpression(construction.ElementCount))
			return;

		string type = construction.ElementCount.ResolvedType ?? "nuint";
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("arrayLength"), type, TypeReferenceForResolvedName(type), construction.ElementCount);
		declarations.Add(local);
		construction.ElementCount = CreateVariableReference(local.Target, type);
	}

	static bool TryGetParamsArrayConstruction(Expression? initialValue, out ConstructionExpression construction)
	{
		if (initialValue is FinallyCleanupExpression { Kind: FinallyCleanupKind.Delete, Expression: not null } finallyCleanup)
			initialValue = finallyCleanup.Expression;
		if (initialValue is WithinExpression { Expression: not null } within)
			initialValue = within.Expression;
		if (initialValue is ConstructionExpression { ElementCount: not null, Type: not null } arrayConstruction)
		{
			construction = arrayConstruction;
			return true;
		}

		construction = null!;
		return false;
	}

	List<Expression?> CreateNullInitialValues(ParamsComponentShape shape)
	{
		List<Expression?> values = [];
		for (int i = 0; i < shape.Components.Count; i++)
			values.Add(null);
		return values;
	}

	void AppendMaterializedGenericReturnAssignments(CallExpression call, ParamsComponentShape shape, List<DeclarationTarget> targets, List<Statement> statements, SyntaxNode? sourceSyntax)
	{
		DeclarationStatement storage = CreateMaterializedGenericReturnStorage(call.ResolvedType ?? shape.TypeName, call.SourceSyntax);
		statements.Add(storage);
		call.Arguments.Add(new ArgumentExpression
		{
			SourceSyntax = sourceSyntax,
			Modifier = ArgumentModifier.Out,
			Value = CreateVariableReference(storage.Target, storage.Target.ResolvedType ?? ErrorType),
			ResolvedType = storage.Target.ResolvedType ?? ErrorType
		});
		statements.Add(new ExpressionStatement
		{
			SourceSyntax = sourceSyntax,
			ResolvedType = "void",
			Expression = LowerExpression(call)
		});
		List<Expression> components = CreateMaterializedComponentExpressions(storage.Target, shape);
		for (int i = 0; i < targets.Count && i < components.Count; i++)
		{
			statements.Add(new ExpressionStatement
			{
				SourceSyntax = sourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = sourceSyntax,
					Target = CreateVariableReference(targets[i], shape.Components[i].Type),
					Operator = AssignmentOperator.Assign,
					Value = components[i],
					ResolvedType = shape.Components[i].Type
				}
			});
		}
	}

	bool TryUseTargetShapeForGenericExpandedReturn(FunctionDefinition function, ParamsComponentShape targetShape, out ParamsComponentShape shape)
	{
		shape = targetShape;
		string returnType = StripTopLevelValueQualifiers(function.ResolvedType ?? function.ReturnType?.ResolvedType ?? "");
		return IsGenericPlaceholderParameter(returnType) && targetShape.Components.Count > 1;
	}

	Expression? NormalizeExpandedReturnPropertyGetter(Expression? expression)
	{
		switch (expression)
		{
			case MemberExpression member
				when expressionRewrites.TryGetValue(member, out Expression? rewritten)
				&& rewritten is MemberReferenceExpression getter
				&& IsExpandedReturnPropertyGetter(getter, out _):
				return RewritePropertyGetterCall(getter, []);

			case MemberReferenceExpression getter when IsExpandedReturnPropertyGetter(getter, out FunctionDefinition? function):
				return RewritePropertyGetterCall(getter, []);

			case IndexExpression { Target: MemberExpression member } index
				when TryCreateMaterializedGenericPropertyGetterReference(member, out MemberReferenceExpression? getter):
			{
				CallExpression call = RewritePropertyGetterCall(getter, index.Arguments);
				call.ResolvedType = index.ResolvedType ?? call.ResolvedType;
				return call;
			}

			case IndexExpression { Target: MemberExpression member } index
				when expressionRewrites.TryGetValue(member, out Expression? rewritten)
					&& rewritten is MemberReferenceExpression getter
					&& IsExpandedReturnPropertyGetter(getter, out _):
			{
				CallExpression call = RewritePropertyGetterCall(getter, index.Arguments);
				call.ResolvedType = index.ResolvedType ?? call.ResolvedType;
				return call;
			}

			case IndexExpression { Target: MemberReferenceExpression getter } index when IsExpandedReturnPropertyGetter(getter, out FunctionDefinition? function):
			{
				CallExpression call = RewritePropertyGetterCall(getter, index.Arguments);
				call.ResolvedType = index.ResolvedType ?? call.ResolvedType;
				return call;
			}

			default:
				return expression;
		}
	}

	bool TryCreateMaterializedGenericPropertyGetterReference(MemberExpression member, out MemberReferenceExpression getter)
	{
		getter = null!;
		string targetType = member.Target?.ResolvedType ?? ErrorType;
		TypeDefinition? type = GetTypeDefinition(targetType);
		if (type is null && TryGetPointerElementType(targetType) is string pointedType)
			type = GetTypeDefinition(pointedType);
		List<FunctionDefinition> getters = type is null ? [] : LookupPropertyGetters(type, member.Name, member.SourceSyntax);
		getters.AddRange(LookupExtensionFunctions(targetType, "get" + member.Name, member.SourceSyntax));
		foreach (FunctionDefinition candidate in getters)
		{
			if (!IsMaterializedGenericReturnFunction(candidate))
				continue;
			getter = CreateMemberReference(member, member.Target, member.ResolvedType ?? candidate.ResolvedType ?? ErrorType, candidate);
			return true;
		}
		return false;
	}

	bool IsMaterializedGenericReturnFunction(FunctionDefinition function)
	{
		return materializedGenericReturnParameters.ContainsKey(function) || IsErasedGenericReturn(function);
	}

	bool IsExpandedReturnPropertyGetter(MemberReferenceExpression getter, out FunctionDefinition function)
	{
		function = null!;
		if (!IsPropertyGetterReference(getter) || getter.Member is not FunctionDefinition candidate)
			return false;
		if (materializedGenericReturnParameters.ContainsKey(candidate))
		{
			function = candidate;
			return true;
		}
		string returnType = StripTopLevelValueQualifiers(candidate.ResolvedType ?? candidate.ReturnType?.ResolvedType ?? "");
		if (!TryGetExpandedReturnShape(candidate, out _) && !IsGenericPlaceholderParameter(returnType))
			return false;

		function = candidate;
		return true;
	}

	bool TryGetExpandedReturnShape(FunctionDefinition function, out ParamsComponentShape shape)
	{
		if (expandedReturnShapes.TryGetValue(function, out shape!))
			return true;
		return TryGetParamsComponentShape(function.ReturnType, function.ResolvedType, "result", out shape);
	}

	bool TryGetExpandedReturnShape(CallExpression call, FunctionDefinition function, out ParamsComponentShape shape)
	{
		if (TryGetExpandedReturnShape(function, out shape))
		{
			string functionReturnType = StripTopLevelValueQualifiers(function.ResolvedType ?? function.ReturnType?.ResolvedType ?? "");
			if (!IsGenericPlaceholderParameter(functionReturnType))
				return true;
		}
		if (callGenericSubstitutions.TryGetValue(call, out Dictionary<string, string>? substitutions)
			&& !string.IsNullOrWhiteSpace(function.ResolvedType)
			&& TryGetParamsComponentShape(null, SubstituteGenericType(function.ResolvedType, substitutions), "result", out shape))
			return true;
		return !string.IsNullOrWhiteSpace(call.ResolvedType)
			&& TryGetParamsComponentShape(null, call.ResolvedType, "result", out shape);
	}

	ParameterDefinition CreateExpandedParameter(ParameterDefinition source, ParamsComponent component, bool inheritDefaultValue)
	{
		string? sourceTypeName = component.Name == "call" ? GetTypeReferenceName(source.Type) : null;
		return new ParameterDefinition
		{
			SourceSyntax = source.SourceSyntax,
			Name = component.ExpandedName,
			Symbol = component.ExpandedName,
			Public = source.Public,
			Internal = source.Internal,
			Modifier = source.Modifier,
			Type = sourceTypeName is null ? null : new NamedTypeReference { Name = sourceTypeName, ResolvedType = component.Type, SourceSyntax = source.Type?.SourceSyntax },
			ResolvedType = component.Type,
			DefaultValue = component.SourceParameter?.DefaultValue ?? (inheritDefaultValue ? source.DefaultValue : null),
			SlotLifetimeFact = source.SlotLifetimeFact,
			ValueLifetimeFact = source.ValueLifetimeFact
		};
	}

	FieldDefinition CreateExpandedField(FieldDefinition source, ParamsComponent component, Expression? initialValue)
	{
		string symbol = component.ExpandedName;
		if (!string.IsNullOrWhiteSpace(source.Symbol) && source.Symbol != source.Name)
			symbol = component.ExpandedName == source.Name ? source.Symbol : source.Symbol + "_" + component.Name;

		return new FieldDefinition
		{
			SourceSyntax = source.SourceSyntax,
			Name = component.ExpandedName,
			Symbol = symbol,
			Export = source.Export,
			Public = source.Public,
			Internal = source.Internal,
			Extern = source.Extern,
			Modifier = source.Modifier,
			ResolvedType = component.Type,
			InitialValue = initialValue,
			SlotLifetimeFact = source.SlotLifetimeFact,
			ValueLifetimeFact = source.ValueLifetimeFact
		};
	}

	VariableDefinition CreateExpandedVariable(VariableDefinition source, ParamsComponent component, Expression? initialValue)
	{
		return new VariableDefinition
		{
			SourceSyntax = source.SourceSyntax,
			Name = component.ExpandedName,
			Symbol = component.ExpandedName,
			Export = source.Export,
			Public = source.Public,
			Internal = source.Internal,
			Extern = source.Extern,
			ResolvedType = component.Type,
			InitialValue = initialValue,
			SlotLifetimeFact = source.SlotLifetimeFact,
			ValueLifetimeFact = source.ValueLifetimeFact
		};
	}

	DeclarationStatement CreateExpandedDeclaration(DeclarationStatement source, ParamsComponent component, Expression? initialValue)
	{
		DeclarationStatement declaration = new()
		{
			SourceSyntax = source.SourceSyntax,
			InitialValue = initialValue,
			ResolvedType = source.ResolvedType,
			SlotLifetimeFact = source.SlotLifetimeFact ?? source.Target.SlotLifetimeFact,
			ValueLifetimeFact = source.ValueLifetimeFact ?? source.Target.ValueLifetimeFact
		};
		declaration.Target.SourceSyntax = source.Target.SourceSyntax;
		declaration.Target.ResolvedType = component.Type;
		declaration.Target.SlotLifetimeFact = declaration.SlotLifetimeFact;
		declaration.Target.ValueLifetimeFact = declaration.ValueLifetimeFact;
		declaration.Target.Names.Add(component.ExpandedName);
		return declaration;
	}

	List<Expression?> GetParamsComponentInitialValues(Expression? initialValue, ParamsComponentShape shape, bool deferCurrentAllocator)
	{
		List<Expression?> values = [];
		Expression? allocator = null;
		Expression? arrayInitialValue = initialValue;
		if (arrayInitialValue is FinallyCleanupExpression { Kind: FinallyCleanupKind.Delete, Expression: not null } finallyCleanup)
			arrayInitialValue = finallyCleanup.Expression;
		if (arrayInitialValue is WithinExpression { Expression: not null } within)
		{
			allocator = within.Context is DefaultWithinContextExpression ? null : within.Context;
			arrayInitialValue = within.Expression;
		}
		if (arrayInitialValue is ConstructionExpression { ElementCount: not null, Type: not null } construction
			&& shape.Kind == ParamsComponentShapeKind.Array
			&& shape.Components.Count == 2)
		{
			values.Add(construction.Kind == ConstructionKind.Init
				? CreateStackAllocCall(construction.Type, construction.SourceSyntax, construction.ElementCount)
				: CreateAllocCall(
					construction.Type,
					allocator ?? (deferCurrentAllocator ? new CurrentAllocatorExpression { SourceSyntax = construction.SourceSyntax, ResolvedType = "Allocator*" } : null),
					construction.SourceSyntax,
					construction.ElementCount));
			values.Add(construction.ElementCount);
			return values;
		}

		Expression? rewrittenInitialValue = initialValue is not null && expressionRewrites.TryGetValue(initialValue, out Expression? rewrite)
			? rewrite
			: initialValue;
		if (rewrittenInitialValue is MemberReferenceExpression { Target: not null, Member: FunctionDefinition function } memberReference
			&& FindContainingType(function) is not InterfaceDefinition
			&& TryCreateExpandedReceiverMethodDelegateComponents(memberReference, function, out List<Expression> targetTypedComponents, shape)
			&& targetTypedComponents.Count == shape.Components.Count)
		{
			values.AddRange(targetTypedComponents);
			return values;
		}

		if (initialValue is not null && TryCreateParamsComponentExpressions(initialValue, out List<Expression> components) && components.Count == shape.Components.Count)
		{
			values.AddRange(components);
			return values;
		}

		if (initialValue is not null && TryCreatePrimitiveStringArrayInitialValues(initialValue, shape, out values))
			return values;

		if (initialValue is MemberExpression { Target: not null } member
			&& TryCreateSourceMemberParamsComponentExpressions(member, shape.TypeName, out components)
			&& components.Count == shape.Components.Count)
		{
			values.AddRange(components);
			return values;
		}

		if (initialValue is MemberReferenceExpression { Target: not null } sourceMemberReference
			&& TryCreateSourceMemberParamsComponentExpressions(sourceMemberReference, shape.TypeName, out components)
			&& components.Count == shape.Components.Count)
		{
			values.AddRange(components);
			return values;
		}

		if (initialValue is not null && shape.Kind == ParamsComponentShapeKind.Optional && shape.Components.Count == 2)
		{
			values.Add(initialValue);
			values.Add(new LiteralExpression
			{
				Kind = LiteralKind.True,
				Text = "true",
				Value = true,
				ResolvedType = "bool"
			});
			return values;
		}

		for (int i = 0; i < shape.Components.Count; i++)
			values.Add(null);
		return values;
	}

	bool TryCreatePrimitiveStringArrayInitialValues(Expression initialValue, ParamsComponentShape shape, out List<Expression?> values)
	{
		values = [];
		if (shape.Kind != ParamsComponentShapeKind.Array
			|| shape.Components.Count != 2
			|| GetPrimitiveStringElementType(initialValue.ResolvedType) is not string stringElement
			|| TryGetPointerElementType(shape.Components[0].Type) is not string pointerElement
			|| StripTopLevelValueQualifiers(pointerElement) != stringElement
			|| !IsConstQualified(pointerElement)
			|| !IsIntegralTypeName(shape.Components[1].Type))
			return false;

		Expression? length = CreateLengthExpression(initialValue, initialValue.SourceSyntax);
		if (length is null)
			return false;

		values.Add(initialValue);
		values.Add(LowerExpression(length) ?? length);
		return true;
	}

	void RegisterParamsExpansion<T>(BindableNode source, ParamsComponentShape shape, List<T> components)
		where T : BindableNode
	{
		List<ParamsExpansionComponent> expansion = [];
		for (int i = 0; i < components.Count && i < shape.Components.Count; i++)
		{
			BindableNode component = components[i];
			string name = component switch
			{
				ParameterDefinition parameter => parameter.Name,
				FieldDefinition field => field.Name,
				VariableDefinition variable => variable.Name,
				DeclarationTarget target => target.Names.Count == 1 ? target.Names[0] : "",
				_ => ""
			};
			expansion.Add(new ParamsExpansionComponent(shape.Components[i].Name, name, component.ResolvedType ?? ErrorType, component));
		}

		if (expansion.Count > 0)
		{
			paramsExpansions[source] = expansion;
			if (source is CallExpression call && call.SourceSyntax is SyntaxNode sourceSyntax)
				expandedReturnParamsExpansionsBySyntax[sourceSyntax] = expansion;
		}
	}
}
