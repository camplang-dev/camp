using System;
using System.Collections.Generic;

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
			if (!TryExpandInitializerItem(item, out List<InitializerItem>? expandedItems))
				continue;

			initializer.Items.RemoveAt(i);
			initializer.Items.InsertRange(i, expandedItems);
			i += expandedItems.Count - 1;
		}
	}

	bool TryExpandInitializerItem(InitializerItem item, out List<InitializerItem> expandedItems)
	{
		expandedItems = [];
		string? targetName = GetSingleInitializerTargetName(item.Target);
		if (targetName is null || !TryCreateParamsComponentExpressions(item.Expression, out List<Expression> components) || components.Count <= 1)
			return false;

		for (int i = 0; i < components.Count; i++)
		{
			Expression component = components[i];
			string? componentName = GetInitializerComponentName(component);
			if (componentName is null)
				return false;
			if (i == 0 && componentName != targetName)
				return false;

			expandedItems.Add(new InitializerItem
			{
				SourceSyntax = item.SourceSyntax,
				Target = InitializerTargetFor(componentName),
				Expression = component,
				ResolvedType = component.ResolvedType
			});
		}

		return expandedItems.Count > 1;
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
			return CreateArrayConstruction(type, construction.ElementCount, construction.SourceSyntax, construction.ResolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return construction;

		return construction.Kind switch
		{
			ConstructionKind.New => CreateNewExpression(definition, construction.Type, construction.Arguments, construction.SourceSyntax, construction.ResolvedType),
			ConstructionKind.Init => (Expression?)CreateInitCallForConstruction(construction, target: null) ?? construction,
			_ => construction
		};
	}

	Expression CreateNewExpression(TypeDefinition type, TypeReference? constructedType, List<ArgumentExpression> arguments, SyntaxNode? syntax, string? resolvedType)
	{
		TypeReference typeReference = constructedType ?? TypeReferenceFor(type);
		if (type is ClassDefinition { Extern: not null } && FindCreateMethod(type, arguments.Count) is FunctionDefinition create)
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
				ResolvedType = resolvedType ?? $"{constructedType?.ResolvedType ?? type.Name}*"
			};
			foreach (ArgumentExpression argument in arguments)
				createCall.Arguments.Add(argument);
			callTargets[createCall] = create;
			AddImplicitSizeOfArguments(createCall, create, constructedType);
			AddImplicitVTableOfArguments(createCall, create, constructedType);
			if (HasWithinParameter(create))
			{
				Expression? allocator = CurrentAllocator();
				createCall.Arguments.Add(new ArgumentExpression { Value = allocator ?? NullLiteral(syntax), ResolvedType = allocator?.ResolvedType ?? "#NULL" });
			}
			return createCall;
		}

		FunctionDefinition? initNew = FindInitNewMethod(type, arguments.Count);
		if (initNew is null && !NeedsVirtualTableAssignment(type))
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

	FunctionDefinition? FindCreateMethod(TypeDefinition type, int argumentCount)
	{
		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (function.Name == CreateMethodName && CallableByArgumentCount(function.Parameters, argumentCount))
				return function;
		}

		return null;
	}

	CallExpression? CreateInitCallForConstruction(ConstructionExpression construction, Expression? target)
	{
		string typeName = BaseConstructedType(construction.Type?.ResolvedType ?? construction.ResolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return null;

		FunctionDefinition? initNew = FindInitNewMethod(definition, construction.Arguments.Count);
		if (initNew is null)
			return null;

		return CreateInitNewCall(target, initNew, construction.Arguments, construction.SourceSyntax, constructedType: construction.Type);
	}

	Expression RewriteDeleteExpression(Expression? expression)
	{
		if (expression is NamedExpression { Qualifiers.Count: 0, Name: "base" } && CreateBaseDeleteCall() is Expression baseDelete)
			return baseDelete;

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
		FunctionDefinition? opDelete = FindDeleteMethod(deletedType);
		if (opDelete is null
			&& typeDefinitions.TryGetValue(BaseTypeName(deletedType), out TypeDefinition? deletedDefinition))
			opDelete = FindCallableDeleteMethod(deletedDefinition, target?.SourceSyntax);

		if (!isPointer && !isThisPointer && !isArray && opDelete is null)
			Report(target?.SourceSyntax, $"delete requires a pointer or a type with a destructor, not '{targetType}'.");
		if (target is null)
			return new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "void" };

		if (isArray)
			return CreateFreeCall(CreateArrayElementsAccess(target));

		return CreateDeleteExpression(target, opDelete, isPointer || isThisPointer);
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

	Expression CreateArrayConstruction(TypeReference elementType, Expression length, SyntaxNode? syntax, string? resolvedType)
	{
		GroupedExpression grouped = new()
		{
			SourceSyntax = syntax,
			ResolvedType = resolvedType ?? $"{elementType.ResolvedType}[]"
		};
		grouped.Items.Add(new GroupedExpressionItem
		{
			Name = "elements",
			ResolvedType = $"{elementType.ResolvedType}*",
			Expression = CreateAllocCall(elementType, CurrentAllocator(), syntax, length)
		});
		grouped.Items.Add(new GroupedExpressionItem
		{
			Name = "length",
			ResolvedType = "nuint",
			Expression = length
		});
		return grouped;
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
