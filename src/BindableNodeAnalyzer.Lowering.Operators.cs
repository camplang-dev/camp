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

		foreach (InitializerItem item in initializer.Items)
			item.Expression = LowerExpression(item.Expression);
	}

	Expression RewriteConstruction(ConstructionExpression construction)
	{
		TypeReference? type = construction.Type;
		string typeName = type?.ResolvedType ?? BaseConstructedType(construction.ResolvedType);
		if (construction.ElementCount is not null && type is not null)
			return CreateArrayConstruction(type, construction.ElementCount, construction.SourceSyntax, construction.ResolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return construction;

		return construction.Kind switch
		{
			ConstructionKind.New => CreateNewExpression(definition, construction.Arguments, construction.SourceSyntax, construction.ResolvedType),
			ConstructionKind.Init => (Expression?)CreateInitCallForConstruction(construction, target: null) ?? construction,
			_ => construction
		};
	}

	Expression CreateNewExpression(TypeDefinition type, List<ArgumentExpression> arguments, SyntaxNode? syntax, string? resolvedType)
	{
		TypeReference typeReference = TypeReferenceFor(type);
		FunctionDefinition? initNew = FindInitNewMethod(type, arguments.Count);
		if (initNew is null && !NeedsVirtualTableAssignment(type))
			return CreateAllocCall(typeReference, syntax);

		string localName = NewGeneratedLocalName("created");
		NamedExpression localReference = new()
		{
			Name = localName,
			ResolvedType = resolvedType ?? $"{type.Name}*"
		};
		GroupedExpression grouped = new()
		{
			SourceSyntax = syntax,
			ResolvedType = resolvedType ?? $"{type.Name}*"
		};
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = new AssignmentExpression
			{
				Target = localReference,
				Operator = AssignmentOperator.Assign,
				Value = CreateAllocCall(typeReference, syntax),
				ResolvedType = resolvedType ?? $"{type.Name}*"
			},
			ResolvedType = resolvedType ?? $"{type.Name}*"
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
				Expression = CreateInitNewCall(localReference, initNew, arguments, syntax),
				ResolvedType = "void"
			});
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = localReference,
			ResolvedType = resolvedType ?? $"{type.Name}*"
		});
		return grouped;
	}

	CallExpression? CreateInitCallForConstruction(ConstructionExpression construction, Expression? target)
	{
		string typeName = construction.Type?.ResolvedType ?? BaseConstructedType(construction.ResolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return null;

		FunctionDefinition? initNew = FindInitNewMethod(definition, construction.Arguments.Count);
		if (initNew is null)
			return null;

		return CreateInitNewCall(target, initNew, construction.Arguments, construction.SourceSyntax);
	}

	Expression RewriteDeleteExpression(Expression? expression)
	{
		if (expression is NamedExpression { Qualifiers.Count: 0, Name: "base" } && CreateBaseDeleteCall() is Expression baseDelete)
			return baseDelete;

		Expression? target = LowerExpression(expression);
		string targetType = target?.ResolvedType ?? ErrorType;
		string? elementType = TryGetPointerElementType(targetType);
		bool isPointer = elementType is not null;
		bool isArray = TryGetArrayElementType(targetType) is not null;
		bool isThisPointer = target is ThisExpression
			&& typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? thisType)
			&& thisType is ClassDefinition;
		string deletedType = isPointer ? elementType ?? ErrorType : targetType;
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
			call.Arguments.Add(new ArgumentExpression { Value = CurrentAllocator(), ResolvedType = AllocatorType });
		return call;
	}

	CallExpression CreateAllocCall(TypeReference type, SyntaxNode? syntax)
	{
		return CreateAllocCall(type, CurrentAllocator(), syntax);
	}

	CallExpression CreateAllocCall(TypeReference type, Expression allocator, SyntaxNode? syntax, Expression? length = null)
	{
		MemberReferenceExpression targetReference = new()
		{
			Target = allocator,
			Name = "alloc",
			Member = allocatorAllocMethod,
			ResolvedType = allocatorAllocMethod.ResolvedType
		};
		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = $"{type.ResolvedType}*",
			Target = targetReference
		};
		call.TypeArguments.Add(CloneType(type)!);
		call.Arguments.Add(new ArgumentExpression { Value = length ?? NumberLiteral("1", "nuint"), ResolvedType = "nuint" });
		call.Arguments.Add(new ArgumentExpression { Value = new SizeOfExpression { Type = CloneType(type), ResolvedType = "nuint" }, ResolvedType = "nuint" });
		call.Arguments.Add(new ArgumentExpression { Modifier = ArgumentModifier.Catch, Value = new NamedExpression { Name = "_", ResolvedType = "MemoryError" }, ResolvedType = "MemoryError" });
		if (ShouldEmitFlattenedInstanceCalls())
			RewriteInstanceInvocation(call, targetReference, allocator, allocatorAllocMethod);
		return call;
	}

	CallExpression CreateInitNewCall(Expression? target, FunctionDefinition initNew, List<ArgumentExpression> arguments, SyntaxNode? syntax, Expression? allocatorArgument = null)
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
		ExpandParamsArguments(call.Arguments);
		if (HasWithinParameter(initNew))
			call.Arguments.Add(new ArgumentExpression { Value = allocatorArgument ?? CurrentAllocator(), ResolvedType = AllocatorType });
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
			call.Arguments.Add(new ArgumentExpression { Value = allocatorArgument ?? CurrentAllocator(), ResolvedType = AllocatorType });
		if (ShouldEmitFlattenedInstanceCalls())
			RewriteInstanceInvocation(call, targetReference, target, opDelete);
		return call;
	}

	CallExpression CreateFreeCall(Expression target)
	{
		return CreateFreeCall(target, CurrentAllocator());
	}

	CallExpression CreateFreeCall(Expression target, Expression allocator)
	{
		MemberReferenceExpression targetReference = new()
		{
			Target = allocator,
			Name = "free",
			Member = allocatorFreeMethod,
			ResolvedType = allocatorFreeMethod.ResolvedType
		};
		CallExpression call = new()
		{
			ResolvedType = "void",
			Target = targetReference,
			Arguments =
			{
				new ArgumentExpression { Value = target, ResolvedType = target.ResolvedType }
			}
		};
		if (ShouldEmitFlattenedInstanceCalls())
			RewriteInstanceInvocation(call, targetReference, allocator, allocatorFreeMethod);
		return call;
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

	static Expression CreateArrayElementsAccess(Expression target)
	{
		return new MemberExpression
		{
			Target = target,
			Name = "elements",
			ResolvedType = TryGetArrayElementType(target.ResolvedType ?? "") is string elementType ? $"{elementType}*" : ErrorType
		};
	}
}
