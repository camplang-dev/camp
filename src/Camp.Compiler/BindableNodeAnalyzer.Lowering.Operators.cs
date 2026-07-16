using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void LowerArgumentDeclaration(ArgumentExpression argument)
	{
		if (currentStatementPrefix is null || argument.Target is not DeclarationTarget target)
			return;

		if (IsDiscardTarget(target))
		{
			argument.Target = null;
			argument.Type = null;
			argument.Value = CreateDiscardReference(target.ResolvedType ?? argument.ResolvedType ?? ErrorType, target.SourceSyntax);
			argument.ResolvedType = argument.Value.ResolvedType;
			return;
		}

		DeclarationStatement declaration = new()
		{
			SourceSyntax = target.SourceSyntax,
			ResolvedType = "void"
		};
		declaration.Target.Type = target.Type is AutoTypeReference ? null : CloneType(target.Type);
		declaration.Target.ResolvedType = target.ResolvedType;
		declaration.Target.SourceSyntax = target.SourceSyntax;
		foreach (string name in target.Names)
			declaration.Target.Names.Add(name);

		currentStatementPrefix.Add(declaration);
		argument.Target = null;
		argument.Type = null;
		argument.Value = CreateVariableReference(declaration.Target, declaration.Target.ResolvedType ?? argument.ResolvedType ?? ErrorType);
		argument.ResolvedType = argument.Value.ResolvedType;
	}

	VariableReferenceExpression CreateDiscardReference(string type, SyntaxNode? syntax)
	{
		DeclarationTarget target = new()
		{
			SourceSyntax = syntax,
			ResolvedType = type
		};
		target.Names.Add("__discard" + (++generatedDiscardIndex).ToString(System.Globalization.CultureInfo.InvariantCulture));
		DeclarationStatement declaration = new()
		{
			SourceSyntax = syntax,
			ResolvedType = "void"
		};
		declaration.Target.SourceSyntax = syntax;
		declaration.Target.ResolvedType = type;
		foreach (string name in target.Names)
			declaration.Target.Names.Add(name);
		currentStatementPrefix?.Add(declaration);
		return CreateVariableReference(declaration.Target, type);
	}

	static bool IsDiscardTarget(DeclarationTarget target)
	{
		return target.Names.Count == 1 && target.Names[0] == "_";
	}

	void LowerInitializer(InitializerExpression? initializer)
	{
		if (initializer is null)
			return;

		for (int i = 0; i < initializer.Items.Count; i++)
		{
			InitializerItem item = initializer.Items[i];
			item.Expression = LowerExpression(item.Expression);
			List<InitializerItem>? expandedItems;
			if (TryExpandDelegateInitializerItem(item, out expandedItems))
			{
				initializer.Items.RemoveAt(i);
				initializer.Items.InsertRange(i, expandedItems);
				i += expandedItems.Count - 1;
				continue;
			}
			if (!TryExpandInitializerItem(item, out expandedItems))
				continue;

			initializer.Items.RemoveAt(i);
			initializer.Items.InsertRange(i, expandedItems);
			i += expandedItems.Count - 1;
		}
	}

	bool TryExpandDelegateInitializerItem(InitializerItem item, out List<InitializerItem> expandedItems)
	{
		expandedItems = [];
		string? targetName = GetSingleInitializerTargetName(item.Target);
		if (targetName is null || item.Expression is null)
			return false;
		string storageType = item.TargetStorageResolvedType ?? item.TargetResolvedType ?? "";
		string semanticType = item.TargetResolvedType ?? storageType;
		if (!TryGetParamsComponentShape(null, storageType, targetName, out ParamsComponentShape storageShape)
			|| storageShape.Kind != ParamsComponentShapeKind.Delegate
			|| storageShape.Components.Count != 2)
			return false;
		if (!TryGetCallableShape(item.Expression.ResolvedType, out CallableShape source) || source.Kind != "fn")
			return false;

		TryGetParamsComponentShape(null, semanticType, targetName, out ParamsComponentShape semanticShape);
		expandedItems.Add(new InitializerItem
		{
			SourceSyntax = item.SourceSyntax,
			Target = InitializerTargetFor(storageShape.Components[0].ExpandedName),
			Expression = item.Expression,
			ResolvedType = item.Expression.ResolvedType,
			TargetResolvedType = semanticShape.Components.Count > 0 ? semanticShape.Components[0].Type : storageShape.Components[0].Type,
			TargetStorageResolvedType = storageShape.Components[0].Type
		});
		expandedItems[0].TargetStorageGenericNames.AddRange(item.TargetStorageGenericNames);
		expandedItems.Add(new InitializerItem
		{
			SourceSyntax = item.SourceSyntax,
			Target = InitializerTargetFor(storageShape.Components[1].ExpandedName),
			Expression = NullLiteral(item.SourceSyntax),
			ResolvedType = "#NULL",
			TargetResolvedType = semanticShape.Components.Count > 1 ? semanticShape.Components[1].Type : storageShape.Components[1].Type,
			TargetStorageResolvedType = storageShape.Components[1].Type
		});
		expandedItems[1].TargetStorageGenericNames.AddRange(item.TargetStorageGenericNames);
		return true;
	}

	bool TryExpandInitializerItem(InitializerItem item, out List<InitializerItem> expandedItems)
	{
		expandedItems = [];
		string? targetName = GetSingleInitializerTargetName(item.Target);
		if (targetName is null || !TryCreateParamsComponentExpressions(item.Expression, out List<Expression> components) || components.Count <= 1)
			return false;

		string? firstComponentName = GetInitializerComponentName(components[0]);
		string? remappedTargetPrefix = firstComponentName != targetName ? targetName : null;
		bool hasStorageShape = TryGetParamsComponentShape(null, item.TargetStorageResolvedType ?? item.TargetResolvedType, targetName, out ParamsComponentShape storageShape);

		for (int i = 0; i < components.Count; i++)
		{
			Expression component = components[i];
			string? componentName = GetInitializerComponentName(component);
			if (componentName is null)
				return false;
			if (i == 0 && componentName != targetName && remappedTargetPrefix is null)
				return false;

			string expandedTargetName = remappedTargetPrefix is not null
				? RemapGeneratedSafeInitializerTarget(remappedTargetPrefix, firstComponentName, componentName)
				: componentName;
			InitializerItem expanded = new()
			{
				SourceSyntax = item.SourceSyntax,
				Target = InitializerTargetFor(expandedTargetName),
				Expression = component,
				ResolvedType = component.ResolvedType,
				TargetResolvedType = component.ResolvedType,
				TargetStorageResolvedType = hasStorageShape
					? FindExpandedParamsComponent(storageShape, expandedTargetName)?.Type
					: null
			};
			expanded.TargetStorageGenericNames.AddRange(item.TargetStorageGenericNames);
			expandedItems.Add(expanded);
		}

		return expandedItems.Count > 1;
	}

	static ParamsComponent? FindExpandedParamsComponent(ParamsComponentShape shape, string expandedName)
	{
		foreach (ParamsComponent component in shape.Components)
			if (component.ExpandedName == expandedName)
				return component;
		return null;
	}

	static bool IsGeneratedSafeInitializerTarget(string name)
	{
		return name.StartsWith("__iter_", StringComparison.Ordinal) || name.StartsWith("_sizeof_", StringComparison.Ordinal) || name.StartsWith("_vtableof_", StringComparison.Ordinal);
	}

	static string RemapGeneratedSafeInitializerTarget(string targetPrefix, string? sourcePrefix, string componentName)
	{
		if (string.IsNullOrWhiteSpace(sourcePrefix) || componentName == sourcePrefix)
			return targetPrefix;
		string sourceComponentPrefix = sourcePrefix + "_";
		if (componentName.StartsWith(sourceComponentPrefix, StringComparison.Ordinal))
			return targetPrefix + componentName[sourcePrefix.Length..];
		return componentName;
	}

	static string? GetSingleInitializerTargetName(InitializerTarget? target)
	{
		return target?.Parts.Count == 1 ? target.Parts[0].Name : null;
	}

	static string? GetInitializerComponentName(Expression? expression)
	{
		return expression switch
		{
			NamedExpression named => named.Name,
			MemberReferenceExpression member => member.Name,
			VariableReferenceExpression variable => GetReferenceName(variable.Variable),
			_ => null
		};
	}

	Expression RewriteConstruction(ConstructionExpression construction)
	{
		TypeReference? type = construction.Type;
		string typeName = BaseConstructedType(type?.ResolvedType ?? construction.ResolvedType);
		if (construction.ElementCount is not null && type is not null)
			return CreateArrayConstruction(type, construction.ElementCount, construction.Kind, construction.SourceSyntax, construction.ResolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return construction;

		constructionTargets.TryGetValue(construction, out FunctionDefinition? constructorTarget);
		return construction.Kind switch
		{
			ConstructionKind.New => CreateNewExpression(definition, construction.Type, construction.Arguments, construction.SourceSyntax, construction.ResolvedType, constructorTarget),
			ConstructionKind.Init => (Expression?)CreateInitCallForConstruction(construction, target: null, constructorTarget) ?? construction,
			_ => construction
		};
	}

	Expression CreateNewExpression(TypeDefinition type, TypeReference? constructedType, List<ArgumentExpression> arguments, SyntaxNode? syntax, string? resolvedType, FunctionDefinition? constructorTarget = null)
	{
		if (type is ClassDefinition { IsShadow: true } shadowClass)
			return CreateShadowNewExpression(shadowClass, constructedType, arguments, syntax, resolvedType, constructorTarget);

		TypeReference typeReference = constructedType ?? TypeReferenceFor(type);
		if (FindExternalCreateMethod(type, arguments.Count) is FunctionDefinition create)
			return CreateCreateCall(create, constructedType, arguments, syntax, resolvedType);

		if (type is ClassDefinition { Extern: not null } && FindExternConstructorMethod(type, arguments.Count) is FunctionDefinition externConstructor)
			return CreateCreateCall(CreateExternalCreateMethod(type, externConstructor), constructedType, arguments, syntax, resolvedType);

		if (type is ClassDefinition { Extern: not null })
		{
			Report(GetRange(syntax), $"Cannot allocate extern class '{type.Name}' without an extern constructor or create method.");
			return new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = resolvedType ?? $"{type.Name}*" };
		}

		FunctionDefinition? initNew = FindInitNewMethod(type, arguments.Count, constructorTarget);
		if (initNew is null && !NeedsVirtualTableAssignment(type) && type is not ClassDefinition)
			return CreateAllocCall(typeReference, syntax);

		string localName = NewGeneratedLocalName("created");
		string localType = resolvedType ?? $"{constructedType?.ResolvedType ?? type.Name}*";
		DeclarationStatement? localDeclaration = null;
		Expression localReference;
		if (currentStatementPrefix is not null)
		{
			TypeReference localTypeReference = PointerTo(CloneType(constructedType ?? TypeReferenceFor(type)) ?? TypeReferenceFor(type));
			localDeclaration = CreateGeneratedLocal(localName, localType, localTypeReference, initialValue: null);
			currentStatementPrefix.Add(localDeclaration);
			localReference = CreateVariableReference(localDeclaration.Target, localType);
		}
		else
		{
			localReference = new NamedExpression
			{
				Name = localName,
				ResolvedType = localType
			};
		}
		if (currentStatementPrefix is not null)
		{
			currentStatementPrefix.Add(CreateAssignmentStatement(localReference, CreateAllocCall(typeReference, syntax), localType, syntax));

			BlockStatement guardBody = new() { ResolvedType = "void" };
			if (type is ClassDefinition)
				guardBody.Statements.Add(CreateZeroAllocatedInstanceStatement(localReference, typeReference, type.Name, syntax));
			if (CreateVirtualTableAssignment(localReference, type) is Expression statementVTableAssignment)
				guardBody.Statements.Add(new ExpressionStatement { ResolvedType = "void", Expression = statementVTableAssignment });
			if (initNew is not null)
				guardBody.Statements.Add(new ExpressionStatement
				{
					ResolvedType = "void",
					Expression = CreateInitNewCall(localReference, initNew, arguments, syntax, constructedType: constructedType)
				});
			if (guardBody.Statements.Count > 0)
				currentStatementPrefix.Add(CreateNotNullGuard(localReference, guardBody, syntax));

			return localReference;
		}

		GroupedExpression grouped = new()
		{
			SourceSyntax = syntax,
			ResolvedType = localType
		};
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = new AssignmentExpression
			{
				Target = localReference,
				Operator = AssignmentOperator.Assign,
				Value = CreateAllocCall(typeReference, syntax),
				ResolvedType = localType
			},
			ResolvedType = localType
		});
		if (type is ClassDefinition)
			grouped.Items.Add(new GroupedExpressionItem
			{
				Expression = CreateZeroAllocatedInstanceExpression(localReference, typeReference, type.Name, syntax),
				ResolvedType = type.Name
			});
		if (CreateVirtualTableAssignment(localReference, type) is Expression vtableAssignment)
			grouped.Items.Add(new GroupedExpressionItem
			{
				Expression = vtableAssignment,
				ResolvedType = "void"
			});
		if (initNew is not null)
			grouped.Items.Add(new GroupedExpressionItem
			{
				Expression = CreateInitNewCall(localReference, initNew, arguments, syntax, constructedType: constructedType),
				ResolvedType = "void"
			});
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = localReference,
			ResolvedType = localType
		});
		return grouped;
	}

	Expression CreateShadowNewExpression(ClassDefinition shadowClass, TypeReference? constructedType, List<ArgumentExpression> arguments, SyntaxNode? syntax, string? resolvedType, FunctionDefinition? constructorTarget = null)
	{
		TypeReference typeReference = constructedType ?? TypeReferenceFor(shadowClass);
		FunctionDefinition? initNew = FindInitNewMethod(shadowClass, arguments.Count, constructorTarget);
		List<ArgumentExpression> baseArguments = initNew is null ? arguments : [];
		Expression baseCreate = CreateShadowBaseCreateCall(shadowClass, baseArguments, syntax, resolvedType);
		string localName = NewGeneratedLocalName("created");
		string localType = resolvedType ?? $"{constructedType?.ResolvedType ?? shadowClass.Name}*";
		Expression localReference;
		if (currentStatementPrefix is not null)
		{
			DeclarationStatement localDeclaration = CreateGeneratedLocal(localName, localType, PointerTo(CloneType(typeReference) ?? TypeReferenceFor(shadowClass)), initialValue: null);
			currentStatementPrefix.Add(localDeclaration);
			localReference = CreateVariableReference(localDeclaration.Target, localType);
			currentStatementPrefix.Add(CreateAssignmentStatement(localReference, baseCreate, localType, syntax));
			BlockStatement guardBody = CreateShadowInstallBody(shadowClass, localReference, initNew, arguments, syntax, constructedType, allocationAllocator: null);
			if (guardBody.Statements.Count > 0)
				currentStatementPrefix.Add(CreateNotNullGuard(localReference, guardBody, syntax));
			return localReference;
		}

		localReference = new NamedExpression { Name = localName, ResolvedType = localType };
		GroupedExpression grouped = new() { SourceSyntax = syntax, ResolvedType = localType };
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = new AssignmentExpression
			{
				SourceSyntax = syntax,
				Target = localReference,
				Operator = AssignmentOperator.Assign,
				Value = baseCreate,
				ResolvedType = localType
			},
			ResolvedType = localType
		});
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = localReference,
			ResolvedType = localType
		});
		return grouped;
	}

	BlockStatement CreateShadowInstallBody(ClassDefinition shadowClass, Expression instance, FunctionDefinition? initNew, List<ArgumentExpression> arguments, SyntaxNode? syntax, TypeReference? constructedType, Expression? allocationAllocator)
	{
		BlockStatement guardBody = new() { ResolvedType = "void" };
		TypeReference shadowDataType = ShadowDataTypeReference(shadowClass);
		string shadowLocalType = $"{shadowDataType.ResolvedType}*";
		DeclarationStatement shadowLocal = CreateGeneratedLocal(NewGeneratedLocalName("shadow"), shadowLocalType, PointerTo(shadowDataType), CreateAllocCall(shadowDataType, allocationAllocator ?? CurrentAllocator(), syntax));
		guardBody.Statements.Add(shadowLocal);
		Expression shadowReference = CreateVariableReference(shadowLocal.Target, shadowLocalType);
		BlockStatement shadowGuard = new() { ResolvedType = "void" };
		shadowGuard.Statements.Add(CreateZeroAllocatedInstanceStatement(shadowReference, shadowDataType, shadowDataType.ResolvedType ?? ShadowDataTypeName(shadowClass), syntax));
		shadowGuard.Statements.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = CreateSetShadowCall(shadowClass, instance, shadowReference, syntax)
		});
		if (FindShadowInstanceField(shadowClass) is FieldDefinition instanceField)
			shadowGuard.Statements.Add(new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = syntax,
					Target = new MemberReferenceExpression
					{
						SourceSyntax = syntax,
						Target = shadowReference,
						Name = instanceField.Name,
						Member = instanceField,
						ResolvedType = instanceField.ResolvedType
					},
					Operator = AssignmentOperator.Assign,
					Value = new CastExpression
					{
						SourceSyntax = syntax,
						Kind = CastKind.Type,
						Type = TypeReferenceForResolvedType(instanceField.ResolvedType ?? $"{shadowClass.Name}*"),
						Expression = instance,
						ResolvedType = instanceField.ResolvedType
					},
					ResolvedType = instanceField.ResolvedType
				}
			});
		if (CreateVirtualTableAssignment(instance, shadowClass) is Expression vtableAssignment)
			shadowGuard.Statements.Add(new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = vtableAssignment
			});
		if (initNew is not null)
			shadowGuard.Statements.Add(new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = CreateInitNewCall(instance, initNew, arguments, syntax, allocatorArgument: allocationAllocator, constructedType: constructedType)
			});
		guardBody.Statements.Add(CreateNotNullGuard(shadowReference, shadowGuard, syntax));
		return guardBody;
	}

	Expression CreateShadowBaseCreateCall(ClassDefinition shadowClass, List<ArgumentExpression> arguments, SyntaxNode? syntax, string? resolvedType)
	{
		ClassDefinition? baseClass = GetDirectBaseClass(shadowClass);
		while (baseClass is not null && baseClass.IsShadow)
			baseClass = GetDirectBaseClass(baseClass);
		if (baseClass is not null)
		{
			if (FindExternalCreateMethod(baseClass, arguments.Count) is FunctionDefinition create)
				return CastShadowInstance(CreateCreateCall(create, TypeReferenceFor(baseClass), arguments, syntax, $"{baseClass.Name}*"), shadowClass, syntax, resolvedType);
			if (FindCreateMethod(baseClass, arguments.Count) is FunctionDefinition ordinaryCreate)
				return CastShadowInstance(CreateCreateCall(ordinaryCreate, TypeReferenceFor(baseClass), arguments, syntax, $"{baseClass.Name}*"), shadowClass, syntax, resolvedType);
			if (baseClass.Extern is not null && FindExternConstructorMethod(baseClass, arguments.Count) is FunctionDefinition externConstructor)
				return CastShadowInstance(CreateCreateCall(CreateExternalCreateMethod(baseClass, externConstructor), TypeReferenceFor(baseClass), arguments, syntax, $"{baseClass.Name}*"), shadowClass, syntax, resolvedType);
			if (baseClass.Extern is null)
				return CastShadowInstance(CreateNewExpression(baseClass, TypeReferenceFor(baseClass), arguments, syntax, $"{baseClass.Name}*"), shadowClass, syntax, resolvedType);
		}

		Report(GetRange(syntax), $"Shadow class '{shadowClass.Name}' cannot be allocated because its base class does not expose a compatible create method.");
		return NullLiteral(syntax);
	}

	FieldDefinition? FindShadowInstanceField(ClassDefinition shadowClass)
	{
		for (ClassDefinition? current = shadowClass; current is not null; current = GetDirectBaseClass(current) is ClassDefinition { IsShadow: true } baseShadow ? baseShadow : null)
		{
			foreach (FieldDefinition field in current.Fields)
				if (field.Name == ShadowInstanceFieldName && field.GeneratedInfo is not null)
					return field;
		}
		return null;
	}

	Expression CastShadowInstance(Expression expression, ClassDefinition shadowClass, SyntaxNode? syntax, string? resolvedType)
	{
		return new CastExpression
		{
			SourceSyntax = syntax,
			Kind = CastKind.Type,
			Unsafe = true,
			Type = PointerTo(TypeReferenceFor(shadowClass)),
			Expression = expression,
			ResolvedType = resolvedType ?? $"{shadowClass.Name}*"
		};
	}

	IfStatement CreateNotNullGuard(Expression target, Statement body, SyntaxNode? syntax)
	{
		return new IfStatement
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Condition = new BinaryExpression
			{
				Left = target,
				Operator = BinaryOperator.NotEqual,
				Right = NullLiteral(syntax),
				ResolvedType = "bool"
			},
			Body = body
		};
	}

	ExpressionStatement CreateZeroAllocatedInstanceStatement(Expression pointer, TypeReference typeReference, string resolvedType, SyntaxNode? syntax)
	{
		return new ExpressionStatement
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Expression = CreateZeroAllocatedInstanceExpression(pointer, typeReference, resolvedType, syntax)
		};
	}

	Expression CreateZeroAllocatedInstanceExpression(Expression pointer, TypeReference typeReference, string resolvedType, SyntaxNode? syntax)
	{
		return new AssignmentExpression
		{
			SourceSyntax = syntax,
			Target = new UnaryExpression
			{
				SourceSyntax = syntax,
				Operator = UnaryOperator.PointerDereference,
				Operand = pointer,
				ResolvedType = resolvedType
			},
			Operator = AssignmentOperator.Assign,
			Value = new DefaultExpression
			{
				SourceSyntax = syntax,
				Type = CloneType(typeReference),
				ResolvedType = resolvedType
			},
			ResolvedType = resolvedType
		};
	}

	CallExpression CreateCreateCall(FunctionDefinition create, TypeReference? constructedType, List<ArgumentExpression> arguments, SyntaxNode? syntax, string? resolvedType)
	{
		CallExpression createCall = new()
		{
			SourceSyntax = syntax,
			Target = new MethodReferenceExpression
			{
				SourceSyntax = syntax,
				Candidates = { create },
				ResolvedType = BuildFunctionValueType(create, isInstance: false)
			},
			ResolvedType = resolvedType ?? create.ResolvedType
		};
		foreach (ArgumentExpression argument in arguments)
			createCall.Arguments.Add(argument);
		callTargets[createCall] = create;
		AddImplicitSizeOfArguments(createCall, create, constructedType);
		AddImplicitNameOfArguments(createCall, create, constructedType);
		AddImplicitVTableOfArguments(createCall, create, constructedType);
		if (HasWithinParameter(create))
		{
			Expression? allocator = CurrentAllocator();
			createCall.Arguments.Add(new ArgumentExpression { Value = allocator ?? NullLiteral(syntax), ResolvedType = allocator?.ResolvedType ?? "#NULL" });
		}
		return createCall;
	}

	FunctionDefinition? FindCreateMethod(TypeDefinition type, int argumentCount)
	{
		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (function.Name == CreateMethodName && CallableByArgumentCount(function.Parameters, argumentCount))
				return function;
		}

		return null;
	}

	FunctionDefinition? FindExternalCreateMethod(TypeDefinition type, int argumentCount)
	{
		FunctionDefinition? create = FindCreateMethod(type, argumentCount);
		if (create is null)
			return null;

		if (type is ClassDefinition { Extern: not null } || create.Extern is not null)
			return create;

		return null;
	}

	FunctionDefinition? FindExternConstructorMethod(TypeDefinition type, int argumentCount)
	{
		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (function.Modifier == FunctionModifier.Constructor
				&& function.Extern is not null
				&& CallableByArgumentCount(function.Parameters, argumentCount))
			{
				return function;
			}
		}
		return null;
	}

	FunctionDefinition CreateExternalCreateMethod(TypeDefinition type, FunctionDefinition initNew)
	{
		TypeReference typeReference = TypeReferenceFor(type);
		FunctionDefinition create = new()
		{
			SourceSyntax = initNew.SourceSyntax,
			Name = CreateMethodName,
			Symbol = $"{type.Name}_{CreateMethodName}",
			Export = initNew.Export,
			Public = initNew.Public,
			Internal = initNew.Internal,
			Extern = initNew.Extern ?? "",
			Modifier = FunctionModifier.Static,
			ReturnType = PointerTo(CloneType(typeReference)!),
			ResolvedType = $"{type.Name}*"
		};
		CopyLifecycleParameters(initNew.Parameters, create.Parameters);
		return create;
	}

	CallExpression? CreateInitCallForConstruction(ConstructionExpression construction, Expression? target, FunctionDefinition? constructorTarget = null)
	{
		string typeName = BaseConstructedType(construction.Type?.ResolvedType ?? construction.ResolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return null;

		FunctionDefinition? initNew = FindInitNewMethod(definition, construction.Arguments.Count, constructorTarget);
		if (initNew is null)
			return null;

		return CreateInitNewCall(target, initNew, construction.Arguments, construction.SourceSyntax, constructedType: construction.Type);
	}

	Expression RewriteDeleteExpression(Expression? expression)
	{
		if (expression is NamedExpression { Qualifiers.Count: 0, Name: "base" } && CreateBaseDeleteCall() is Expression baseDelete)
			return baseDelete;

		if (expression is WithinExpression { Expression: not null } within)
		{
				bool defaultWithin = within.Context is DefaultWithinContextExpression;
				Expression? allocator = defaultWithin ? null : LowerExpression(within.Context);
				Expression? previousWithinContext = currentWithinContext;
				int previousDefaultWithinContextDepth = currentDefaultWithinContextDepth;
				currentWithinContext = defaultWithin ? null : CaptureWithinContext(allocator, within.SourceSyntax);
				if (defaultWithin)
					currentDefaultWithinContextDepth++;
				Expression result = RewriteDeleteExpression(within.Expression);
				currentWithinContext = previousWithinContext;
				currentDefaultWithinContextDepth = previousDefaultWithinContextDepth;
				return result;
			}

		Expression? target = LowerExpression(expression);
		string targetType = target?.ResolvedType ?? ErrorType;
		string? elementType = TryGetPointerElementType(targetType);
		string? primitiveStringElementType = GetPrimitiveStringElementType(targetType);
		bool isPrimitiveStringPointer = primitiveStringElementType is not null;
		bool isPointer = elementType is not null || isPrimitiveStringPointer;
		bool isArray = TryGetArrayElementType(targetType) is not null;
		bool isThisPointer = target is ThisExpression
			&& typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? thisType)
			&& thisType is ClassDefinition;
		string deletedType = elementType ?? primitiveStringElementType ?? targetType;
		FunctionDefinition? opDelete = null;
		TypeDefinition? deletedDefinition = null;
		if (typeDefinitions.TryGetValue(BaseTypeName(deletedType), out TypeDefinition? foundDeletedDefinition))
		{
			deletedDefinition = foundDeletedDefinition;
			opDelete = deletedDefinition is ClassDefinition { Extern: not null }
				? FindDestroyMethod(deletedDefinition)
				: FindDeleteMethod(deletedDefinition) ?? FindCallableDeleteMethod(deletedDefinition, target?.SourceSyntax);
		}
		else
		{
			opDelete = FindDeleteMethod(deletedType);
		}

		if (!isPointer && !isThisPointer && !isArray && opDelete is null)
			Report(target?.SourceSyntax, $"delete requires a pointer or a type with a destructor, not '{targetType}'.");
		if (deletedDefinition is ClassDefinition { Extern: not null } && opDelete is null)
			Report(target?.SourceSyntax, $"delete requires an explicit destructor for extern class '{deletedDefinition.Name}'.");
		if (target is null)
			return new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "void" };

		if (isArray)
			return CreateFreeCall(CreateArrayElementsAccess(target));

		bool deallocate = (isPointer || isThisPointer)
			&& deletedDefinition is not ClassDefinition { Extern: not null };
		return CreateDeleteExpression(target, opDelete, deallocate);
	}

	FunctionDefinition? FindCallableDeleteMethod(TypeDefinition type, SyntaxNode? referenceSyntax)
	{
		foreach (FunctionDefinition function in LookupTypeFunctions(type, DeleteMethodName, referenceSyntax))
			return function;
		foreach (FunctionDefinition function in GetFunctions(type))
			if (IsDestructorFunction(function))
				return function;
		return null;
	}

	Expression? CreateBaseDeleteCall()
	{
		if (currentRewriteContainingType is not ClassDefinition classDefinition)
			return null;
		ClassDefinition? baseClass = GetDirectBaseClass(classDefinition);
		if (baseClass is null)
			return null;

		FunctionDefinition? opDelete = FindVirtualImplementationByName(baseClass, DeleteMethodName) ?? FindDeleteMethod(baseClass.Name);
		if (opDelete is null)
			return null;

		CallExpression call = new()
		{
			ResolvedType = "void",
			Target = CreateMethodReference(opDelete, "void")
		};
		if (HasWithinParameter(opDelete))
		{
			Expression? allocator = CurrentAllocator();
			call.Arguments.Add(new ArgumentExpression { Value = allocator ?? NullLiteral(opDelete.SourceSyntax), ResolvedType = allocator?.ResolvedType ?? "#NULL" });
		}
		return call;
	}

	Expression CreateAllocCall(TypeReference type, SyntaxNode? syntax)
	{
		return CreateAllocCall(type, CurrentAllocator(), syntax);
	}

	Expression CreateStackAllocCall(TypeReference type, SyntaxNode? syntax, Expression? length = null)
	{
		Expression size = CreateAllocationSizeExpression(type, length, syntax);
		return new CastExpression
		{
			SourceSyntax = syntax,
			Kind = CastKind.Type,
			Type = PointerTo(CloneType(type)!),
			Expression = new StackAllocExpression
			{
				SourceSyntax = syntax,
				Size = size,
				ResolvedType = "void*"
			},
			ResolvedType = $"{type.ResolvedType}*"
		};
	}

	Expression CreateAllocCall(TypeReference type, Expression? allocator, SyntaxNode? syntax, Expression? length = null)
	{
		Expression size = CreateAllocationSizeExpression(type, length, syntax);
		if (allocator is CurrentAllocatorExpression currentAllocator)
			return CreateDeferredCurrentAllocatorAllocCall(type, size, currentAllocator, syntax);

		return CreateAllocCallFromByteSize(type, allocator, size, syntax);
	}

	Expression CreateAllocCallFromByteSize(TypeReference type, Expression? allocator, Expression size, SyntaxNode? syntax)
	{
		Expression allocation = allocator is null
			? CreateMallocCall(size, syntax)
			: new ConditionalExpression
			{
				SourceSyntax = syntax,
				Condition = new BinaryExpression
				{
					SourceSyntax = syntax,
					Left = allocator,
					Operator = BinaryOperator.NotEqual,
					Right = NullLiteral(syntax),
					ResolvedType = "bool"
				},
				WhenTrue = CreateAllocatorAllocCall(allocator, size, syntax),
				WhenFalse = CreateMallocCall(size, syntax),
				ResolvedType = "void*"
			};
		return new CastExpression
		{
			SourceSyntax = syntax,
			Kind = CastKind.Type,
			Type = PointerTo(CloneType(type)!),
			Expression = allocation,
			ResolvedType = $"{type.ResolvedType}*"
		};
	}

	Expression CreateDeferredCurrentAllocatorAllocCall(TypeReference type, Expression size, CurrentAllocatorExpression currentAllocator, SyntaxNode? syntax)
	{
		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = $"{type.ResolvedType}*",
			Target = currentAllocator
		};
		call.TypeArguments.Add(CloneType(type)!);
		call.Arguments.Add(new ArgumentExpression { SourceSyntax = syntax, Value = size, ResolvedType = size.ResolvedType });
		return call;
	}

	CallExpression CreateInitNewCall(Expression? target, FunctionDefinition initNew, List<ArgumentExpression> arguments, SyntaxNode? syntax, Expression? allocatorArgument = null, TypeReference? constructedType = null)
	{
		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Target = target is null
				? CreateMethodReference(initNew, "void")
				: new MemberReferenceExpression
				{
					Target = target,
					Name = initNew.Name,
					Member = initNew,
					ResolvedType = "void"
				}
		};

		foreach (ArgumentExpression argument in arguments)
			call.Arguments.Add(argument);
		callTargets[call] = initNew;
		AddImplicitDefaultArguments(call);
		ExpandParamsArguments(call);
		AddImplicitSizeOfArguments(call, initNew, constructedType);
		AddImplicitNameOfArguments(call, initNew, constructedType);
		AddImplicitVTableOfArguments(call, initNew, constructedType);
		if (HasWithinParameter(initNew))
		{
			Expression? allocator = allocatorArgument ?? CurrentAllocator();
			call.Arguments.Add(new ArgumentExpression { Value = allocator ?? NullLiteral(syntax), ResolvedType = allocator?.ResolvedType ?? "#NULL" });
		}
		if (ShouldEmitFlattenedInstanceCalls() && call.Target is MemberReferenceExpression member && target is not null)
			RewriteInstanceInvocation(call, member, target, initNew);
		return call;
	}

	CallExpression CreateDestructorCall(Expression target, FunctionDefinition opDelete, Expression? allocatorArgument = null)
	{
		MemberReferenceExpression targetReference = new()
		{
			Target = target,
			Name = opDelete.Name,
			Member = opDelete,
			ResolvedType = "void"
		};
		CallExpression call = new()
		{
			ResolvedType = "void",
			Target = targetReference
		};
		if (HasWithinParameter(opDelete))
		{
			Expression? allocator = allocatorArgument ?? CurrentAllocator();
			call.Arguments.Add(new ArgumentExpression { Value = allocator ?? NullLiteral(opDelete.SourceSyntax), ResolvedType = allocator?.ResolvedType ?? "#NULL" });
		}
		if (ShouldEmitFlattenedInstanceCalls())
			RewriteInstanceInvocation(call, targetReference, target, opDelete);
		return call;
	}

	Expression CreateFreeCall(Expression target)
	{
		return CreateFreeCall(target, CurrentAllocator());
	}

	Expression CreateFreeCall(Expression target, Expression? allocator)
	{
		Expression pointer = new CastExpression
		{
			SourceSyntax = target.SourceSyntax,
			Kind = CastKind.Type,
			Type = PointerTo(VoidType()),
			Expression = target,
			ResolvedType = "void*"
		};
		if (allocator is null)
			return CreateGlobalFreeCall(pointer, target.SourceSyntax);

		return new ConditionalExpression
		{
			SourceSyntax = target.SourceSyntax,
			Condition = new BinaryExpression
			{
				SourceSyntax = target.SourceSyntax,
				Left = allocator,
				Operator = BinaryOperator.NotEqual,
				Right = NullLiteral(target.SourceSyntax),
				ResolvedType = "bool"
			},
			WhenTrue = CreateAllocatorFreeCall(allocator, pointer, target.SourceSyntax),
			WhenFalse = CreateGlobalFreeCall(pointer, target.SourceSyntax),
			ResolvedType = "void"
		};
	}

	Expression CreateDeleteExpression(Expression target, FunctionDefinition? opDelete, bool deallocate)
	{
		List<Expression> operations = [];
		if (opDelete is not null)
			operations.Add(CreateDestructorCall(target, opDelete));
		if (deallocate)
			operations.Add(CreateFreeCall(target));

		if (operations.Count == 0)
			return new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "void" };
		if (operations.Count == 1)
			return operations[0];

		GroupedExpression grouped = new()
		{
			ResolvedType = "void"
		};
		foreach (Expression operation in operations)
			grouped.Items.Add(new GroupedExpressionItem { Expression = operation, ResolvedType = operation.ResolvedType });
		return grouped;
	}

	Expression CreateDeleteShadowExpression(SyntaxNode? syntax)
	{
		if (currentRewriteFunction is null
			|| FindContainingType(currentRewriteFunction) is not ClassDefinition { IsShadow: true } shadowClass)
			return new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "void" };

		Expression receiver = new ThisExpression
		{
			SourceSyntax = syntax,
			ResolvedType = $"{shadowClass.Name}*"
		};
		Expression shadowData = CreateGetShadowCall(shadowClass, receiver, syntax, mutable: true);
		return CreateFreeCall(shadowData);
	}

	Expression CreateGetShadowCall(ClassDefinition shadowClass, Expression receiver, SyntaxNode? syntax, bool mutable)
	{
		FunctionDefinition? hook = shadowClass.GetShadowHook ?? FindShadowHooks(shadowClass, "@getshadow").FirstOrDefault();
		if (hook is null)
			return NullLiteral(syntax);

		Expression hookReceiver = CastShadowHookReceiver(hook, receiver, syntax);
		MemberReferenceExpression targetReference = new()
		{
			SourceSyntax = syntax,
			Target = hookReceiver,
			Name = hook.Name,
			Member = hook,
			ResolvedType = hook.ResolvedType ?? "escaped void*"
		};
		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = hook.ResolvedType ?? "escaped void*",
			Target = targetReference
		};
		if (ShouldEmitFlattenedInstanceCalls())
			RewriteInstanceInvocation(call, targetReference, hookReceiver, hook);
		return new CastExpression
		{
			SourceSyntax = syntax,
			Kind = CastKind.Type,
			Type = PointerTo(mutable ? ShadowDataTypeReference(shadowClass) : ConstShadowDataTypeReference(shadowClass)),
			Expression = call,
			ResolvedType = (mutable ? "" : "const ") + ShadowDataTypeName(shadowClass) + "*"
		};
	}

	Expression CreateSetShadowCall(ClassDefinition shadowClass, Expression receiver, Expression value, SyntaxNode? syntax)
	{
		FunctionDefinition? hook = shadowClass.SetShadowHook ?? FindShadowHooks(shadowClass, "@setshadow").FirstOrDefault();
		if (hook is null)
			return new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "void" };

		Expression hookReceiver = CastShadowHookReceiver(hook, receiver, syntax);
		MemberReferenceExpression targetReference = new()
		{
			SourceSyntax = syntax,
			Target = hookReceiver,
			Name = hook.Name,
			Member = hook,
			ResolvedType = "void"
		};
		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Target = targetReference
		};
		call.Arguments.Add(new ArgumentExpression
		{
			SourceSyntax = syntax,
			Value = new CastExpression
			{
				SourceSyntax = syntax,
				Kind = CastKind.Type,
				Type = new EscapedTypeReference
				{
					Type = PointerTo(VoidType()),
					ResolvedType = "escaped void*"
				},
				Expression = value,
				ResolvedType = "escaped void*"
			},
			ResolvedType = "escaped void*"
		});
		if (ShouldEmitFlattenedInstanceCalls())
			RewriteInstanceInvocation(call, targetReference, hookReceiver, hook);
		return call;
	}

	Expression CastShadowHookReceiver(FunctionDefinition hook, Expression receiver, SyntaxNode? syntax)
	{
		ThisParameterDefinition? thisParameter = GetExplicitThisParameter(hook) ?? hook.EffectiveThisParameter;
		string receiverType = thisParameter?.ResolvedType ?? receiver.ResolvedType ?? ErrorType;
		return new CastExpression
		{
			SourceSyntax = syntax,
			Kind = CastKind.Type,
			Type = TypeReferenceForResolvedType(receiverType),
			Expression = receiver,
			ResolvedType = receiverType
		};
	}

	static string ShadowDataTypeName(ClassDefinition shadowClass)
	{
		return shadowClass.ShadowDataType?.Symbol ?? shadowClass.Symbol + "_ShadowData";
	}

	static TypeReference ShadowDataTypeReference(ClassDefinition shadowClass)
	{
		string name = ShadowDataTypeName(shadowClass);
		return shadowClass.ShadowDataType is StructDefinition shadowData
			? new TypeDefinitionReference { Name = name, Definition = shadowData, ResolvedType = name }
			: new NamedTypeReference { Name = name, ResolvedType = name };
	}

	static TypeReference ConstShadowDataTypeReference(ClassDefinition shadowClass)
	{
		return new ConstTypeReference
		{
			Type = ShadowDataTypeReference(shadowClass),
			ResolvedType = "const " + ShadowDataTypeName(shadowClass)
		};
	}

	Expression CreateArrayConstruction(TypeReference elementType, Expression length, ConstructionKind kind, SyntaxNode? syntax, string? resolvedType)
	{
		length = CaptureArrayConstructionLength(length);
		GroupedExpression grouped = new()
		{
			SourceSyntax = syntax,
			ResolvedType = resolvedType ?? $"{elementType.ResolvedType}[]"
		};
		grouped.Items.Add(new GroupedExpressionItem
		{
			Name = "elements",
			ResolvedType = $"{elementType.ResolvedType}*",
			Expression = kind == ConstructionKind.Init
				? CreateStackAllocCall(elementType, syntax, length)
				: CreateAllocCall(elementType, CurrentAllocator(), syntax, length)
		});
		grouped.Items.Add(new GroupedExpressionItem
		{
			Name = "length",
			ResolvedType = "nuint",
			Expression = length
		});
		return grouped;
	}

	Expression CaptureArrayConstructionLength(Expression length)
	{
		if (currentStatementPrefix is null || IsRepeatableArrayLengthExpression(length))
			return length;

		string type = length.ResolvedType ?? "nuint";
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("arrayLength"), type, TypeReferenceForResolvedName(type), length);
		currentStatementPrefix.Add(local);
		return CreateVariableReference(local.Target, type);
	}

	static bool IsRepeatableArrayLengthExpression(Expression? expression)
	{
		return expression switch
		{
			null => true,
			LiteralExpression or VariableReferenceExpression or ThisExpression or DefaultExpression or SizeOfExpression or VTableOfExpression => true,
			NamedExpression => true,
			MemberExpression member => IsRepeatableArrayLengthExpression(member.Target),
			ParenthesizedExpression parenthesized => IsRepeatableArrayLengthExpression(parenthesized.Expression),
			CastExpression cast => IsRepeatableArrayLengthExpression(cast.Expression),
			UnaryExpression unary => unary.Operator is not (UnaryOperator.Increment or UnaryOperator.Decrement) && IsRepeatableArrayLengthExpression(unary.Operand),
			BinaryExpression binary => IsRepeatableArrayLengthExpression(binary.Left) && IsRepeatableArrayLengthExpression(binary.Right),
			ConditionalExpression conditional => IsRepeatableArrayLengthExpression(conditional.Condition)
				&& IsRepeatableArrayLengthExpression(conditional.WhenTrue)
				&& IsRepeatableArrayLengthExpression(conditional.WhenFalse),
			_ => false
		};
	}

	Expression CreateArrayElementsAccess(Expression target)
	{
		if (TryCreateParamsComponentExpressions(target, out List<Expression> components) && components.Count > 0)
			return components[0];

		return new MemberExpression
		{
			Target = target,
			Name = "elements",
			ResolvedType = TryGetArrayElementType(target.ResolvedType ?? "") is string elementType ? $"{elementType}*" : ErrorType
		};
	}
}
