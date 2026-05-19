using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly Dictionary<FunctionDefinition, FunctionDefinition> initNewMethods = [];
	readonly Dictionary<FunctionDefinition, FunctionDefinition> deleteMethods = [];
	readonly FunctionDefinition allocatorAllocMethod = CreateAllocatorAllocMethod();
	readonly FunctionDefinition allocatorFreeMethod = CreateAllocatorFreeMethod();
	Expression? currentAllocatorOverride;
	int generatedLocalIndex;

	public static AnalysisResult AnalyzeAndRewrite(Module module)
	{
		ArgumentNullException.ThrowIfNull(module);

		BindableNodeAnalyzer analyzer = new();
		analyzer.AnalyzeModule(module);
		analyzer.FillMissingResolvedTypes(module);
		if (analyzer.diagnostics.Count == 0)
		{
			analyzer.RewriteModule(module);
			analyzer.FillMissingResolvedTypes(module);
		}
		return new AnalysisResult(module, analyzer.diagnostics);
	}

	void RewriteModule(Module module)
	{
		foreach (Definition definition in module.Definitions)
			GenerateLifecycleMethods(definition);

		foreach (Definition definition in module.Definitions)
			RewriteDefinition(definition);
	}

	void GenerateLifecycleMethods(Definition definition)
	{
		switch (definition)
		{
			case ClassDefinition classDefinition:
				GenerateLifecycleMethods(classDefinition, classDefinition.Functions);
				break;

			case StructDefinition structDefinition:
				GenerateLifecycleMethods(structDefinition, structDefinition.Functions);
				break;
		}
	}

	void GenerateLifecycleMethods(TypeDefinition type, List<FunctionDefinition> functions)
	{
		List<FunctionDefinition> generated = [];
		foreach (FunctionDefinition function in functions.ToArray())
		{
			switch (function.Modifier)
			{
				case FunctionModifier.Constructor:
				{
					FunctionDefinition initNew = CreateInitNewMethod(type, function);
					FunctionDefinition create = CreateCreateMethod(type, function, initNew);
					initNewMethods[function] = initNew;
					generated.Add(initNew);
					generated.Add(create);
					function.Body = null;
					break;
				}

				case FunctionModifier.Destructor:
				{
					FunctionDefinition opDelete = CreateDeleteMethod(type, function);
					FunctionDefinition destroy = CreateDestroyMethod(type, function, opDelete);
					deleteMethods[function] = opDelete;
					generated.Add(opDelete);
					generated.Add(destroy);
					function.Body = null;
					break;
				}
			}
		}

		functions.AddRange(generated);
	}

	FunctionDefinition CreateInitNewMethod(TypeDefinition type, FunctionDefinition constructor)
	{
		FunctionDefinition method = new()
		{
			Name = $"{type.Name}_op_initnew",
			Symbol = $"{type.Name}_op_initnew",
			Export = constructor.Export,
			ReturnType = VoidType(),
			ResolvedType = "void",
			Body = constructor.Body
		};
		CopyParameters(constructor.Parameters, method.Parameters);
		if (HasWithinParameter(method) && method.Body is BlockFunctionBody block)
			block.Statements.Insert(0, CreateResolvedAllocatorLocal(GetWithinParameter(method)));
		return method;
	}

	FunctionDefinition CreateCreateMethod(TypeDefinition type, FunctionDefinition constructor, FunctionDefinition initNew)
	{
		TypeReference typeReference = TypeReferenceFor(type);
		FunctionDefinition method = new()
		{
			Name = $"{type.Name}_create",
			Symbol = $"{type.Name}_create",
			Export = constructor.Export,
			Modifier = FunctionModifier.Static,
			ReturnType = PointerTo(CloneType(typeReference)!),
			ResolvedType = $"{type.Name}*"
		};
		CopyParameters(constructor.Parameters, method.Parameters);
		bool createWithAllocator = HasWithinParameter(method) || HasCreateWithAllocatorAttribute(type);
		if (createWithAllocator && !HasWithinParameter(method))
			method.Parameters.Add(CreateAllocatorParameter());
		method.Body = new BlockFunctionBody
		{
			ResolvedType = "void"
		};
		BlockFunctionBody body = (BlockFunctionBody)method.Body;
		ParameterDefinition? allocatorParameter = GetAllocatorParameter(method);
		if (createWithAllocator)
			body.Statements.Add(CreateResolvedAllocatorLocal(allocatorParameter));
		string localName = NewGeneratedLocalName("created");
		DeclarationStatement local = CreateGeneratedLocal(localName, $"{type.Name}*", PointerTo(CloneType(typeReference)!), CreateAllocCall(typeReference, createWithAllocator ? CreateResolvedAllocatorReference() : StdDefaultAllocator(), method.SourceSyntax));
		body.Statements.Add(local);
		IfStatement guard = new()
		{
			ResolvedType = "void",
			Condition = new BinaryExpression
			{
				Left = CreateVariableReference(local.Target, $"{type.Name}*"),
				Operator = BinaryOperator.NotEqual,
				Right = new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "#NULL" },
				ResolvedType = "bool"
			},
			Body = new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = CreateInitNewCall(CreateVariableReference(local.Target, $"{type.Name}*"), initNew, method.ArgumentsFromParameters(skipAllocator: !HasWithinParameter(initNew)), method.SourceSyntax, allocatorParameter is null ? null : CreateVariableReference(allocatorParameter, allocatorParameter.ResolvedType ?? "Allocator*"))
			}
		};
		body.Statements.Add(guard);
		body.Statements.Add(new ReturnStatement
		{
			Expression = CreateVariableReference(local.Target, $"{type.Name}*"),
			ResolvedType = "void"
		});
		return method;
	}

	FunctionDefinition CreateDeleteMethod(TypeDefinition type, FunctionDefinition destructor)
	{
		FunctionDefinition method = new()
		{
			Name = $"{type.Name}_op_delete",
			Symbol = $"{type.Name}_op_delete",
			Export = destructor.Export,
			ReturnType = VoidType(),
			ResolvedType = "void",
			Body = destructor.Body
		};
		CopyParameters(destructor.Parameters, method.Parameters);
		return method;
	}

	FunctionDefinition CreateDestroyMethod(TypeDefinition type, FunctionDefinition destructor, FunctionDefinition opDelete)
	{
		TypeReference typeReference = TypeReferenceFor(type);
		ParameterDefinition value = new()
		{
			Name = "value",
			Symbol = "value",
			Type = PointerTo(CloneType(typeReference)!),
			ResolvedType = $"{type.Name}*"
		};
		VariableReferenceExpression target = new()
		{
			Variable = value,
			ResolvedType = value.ResolvedType
		};

		FunctionDefinition method = new()
		{
			Name = $"{type.Name}_destroy",
			Symbol = $"{type.Name}_destroy",
			Export = destructor.Export,
			ReturnType = VoidType(),
			ResolvedType = "void"
		};
		bool destroyWithAllocator = HasWithinParameter(destructor) || HasCreateWithAllocatorAttribute(type);
		if (destroyWithAllocator)
			method.Parameters.Add(CreateAllocatorParameter());
		method.Body = new BlockFunctionBody
		{
			ResolvedType = "void"
		};
		BlockFunctionBody body = (BlockFunctionBody)method.Body;
		body.Statements.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = CreateDestructorCall(new ThisExpression { ResolvedType = type.Name }, opDelete, GetAllocatorParameter(method) is ParameterDefinition allocatorParameter ? CreateVariableReference(allocatorParameter, allocatorParameter.ResolvedType ?? "Allocator*") : null)
		});
		if (destroyWithAllocator)
			body.Statements.Add(CreateResolvedAllocatorLocal(GetAllocatorParameter(method)));
		body.Statements.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = CreateFreeCall(new ThisExpression { ResolvedType = type.Name }, destroyWithAllocator ? CreateResolvedAllocatorReference() : StdDefaultAllocator())
		});
		return method;
	}

	void RewriteDefinition(Definition definition)
	{
		switch (definition)
		{
			case VariableDefinition variable:
				variable.InitialValue = LowerExpression(variable.InitialValue);
				break;

			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
					field.InitialValue = LowerExpression(field.InitialValue);
				foreach (FunctionDefinition function in classDefinition.Functions)
					RewriteFunction(function);
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					field.InitialValue = LowerExpression(field.InitialValue);
				foreach (FunctionDefinition function in structDefinition.Functions)
					RewriteFunction(function);
				break;

			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					RewriteFunction(function);
				break;

			case EnumDefinition enumDefinition:
				foreach (VariableDefinition value in enumDefinition.Values)
					value.InitialValue = LowerExpression(value.InitialValue);
				foreach (FunctionDefinition function in enumDefinition.Functions)
					RewriteFunction(function);
				break;

			case NewtypeDefinition newtypeDefinition:
				foreach (ParameterDefinition parameter in newtypeDefinition.Parameters)
					parameter.DefaultValue = LowerExpression(parameter.DefaultValue);
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					RewriteFunction(function);
				break;

			case ParamsDefinition paramsDefinition:
				foreach (ParameterDefinition component in paramsDefinition.Components)
					component.DefaultValue = LowerExpression(component.DefaultValue);
				foreach (FunctionDefinition function in paramsDefinition.Functions)
					RewriteFunction(function);
				break;

			case FunctionDefinition function:
				RewriteFunction(function);
				break;
		}
	}

	void RewriteFunction(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
			parameter.DefaultValue = LowerExpression(parameter.DefaultValue);

		Expression? previousAllocator = currentAllocatorOverride;
		currentAllocatorOverride = GetFunctionAllocatorForBody(function);
		function.Body = RewriteFunctionBody(function.Body);
		currentAllocatorOverride = previousAllocator;
	}

	FunctionBody? RewriteFunctionBody(FunctionBody? body)
	{
		switch (body)
		{
			case null:
				return null;

			case BlockFunctionBody block:
				RewriteStatementList(block.Statements);
				return block;

			case ExpressionFunctionBody expression:
				expression.Expression = LowerExpression(expression.Expression);
				return expression;

			default:
				return body;
		}
	}

	Statement RewriteStatement(Statement statement)
	{
		switch (statement)
		{
			case BlockStatement block:
				RewriteStatementList(block.Statements);
				break;

			case ExpressionStatement expression:
				expression.Expression = LowerExpression(expression.Expression);
				break;

			case DeclarationStatement declaration:
				if (TryRewriteInitDeclaration(declaration, out List<Statement>? statements))
					return new BlockStatement { Statements = { statements[0], statements[1] }, ResolvedType = "void" };
				declaration.InitialValue = LowerExpression(declaration.InitialValue);
				break;

			case IfStatement ifStatement:
				ifStatement.Condition = LowerExpression(ifStatement.Condition);
				if (ifStatement.Body is not null)
					ifStatement.Body = RewriteStatement(ifStatement.Body);
				if (ifStatement.ElseBody is not null)
					ifStatement.ElseBody = RewriteStatement(ifStatement.ElseBody);
				break;

			case WhileStatement whileStatement:
				whileStatement.Condition = LowerExpression(whileStatement.Condition);
				if (whileStatement.Body is not null)
					whileStatement.Body = RewriteStatement(whileStatement.Body);
				break;

			case DoWhileStatement doWhile:
				if (doWhile.Body is not null)
					doWhile.Body = RewriteStatement(doWhile.Body);
				doWhile.Condition = LowerExpression(doWhile.Condition);
				break;

			case ForStatement forStatement:
				if (forStatement.Condition.Declaration is not null)
					forStatement.Condition.Declaration = (DeclarationStatement)RewriteStatement(forStatement.Condition.Declaration);
				for (int i = 0; i < forStatement.Condition.Clauses.Count; i++)
					forStatement.Condition.Clauses[i] = LowerExpression(forStatement.Condition.Clauses[i]);
				if (forStatement.Body is not null)
					forStatement.Body = RewriteStatement(forStatement.Body);
				break;

			case ForeachStatement foreachStatement:
				foreachStatement.Source = LowerExpression(foreachStatement.Source);
				if (foreachStatement.Body is not null)
					foreachStatement.Body = RewriteStatement(foreachStatement.Body);
				break;

			case SwitchStatement switchStatement:
				switchStatement.Expression = LowerExpression(switchStatement.Expression);
				RewriteStatementList(switchStatement.Statements);
				break;

			case CaseStatement caseStatement:
				caseStatement.Expression = LowerExpression(caseStatement.Expression);
				break;

			case ReturnStatement returnStatement:
				returnStatement.Expression = LowerExpression(returnStatement.Expression);
				break;

			case YieldStatement yieldStatement:
				yieldStatement.Expression = LowerExpression(yieldStatement.Expression);
				break;

			case DeleteStatement deleteStatement:
				return new ExpressionStatement
				{
					SourceSyntax = deleteStatement.SourceSyntax,
					ResolvedType = "void",
					Expression = RewriteDeleteExpression(deleteStatement.Expression)
				};

			case TryStatement tryStatement:
				if (tryStatement.Body is not null)
					tryStatement.Body = RewriteStatement(tryStatement.Body);
				for (int i = 0; i < tryStatement.Catches.Count; i++)
					tryStatement.Catches[i] = (CatchStatement)RewriteStatement(tryStatement.Catches[i]);
				if (tryStatement.Finally is not null)
					tryStatement.Finally = (FinallyStatement)RewriteStatement(tryStatement.Finally);
				break;

			case CatchStatement catchStatement:
				if (catchStatement.Body is not null)
					catchStatement.Body = RewriteStatement(catchStatement.Body);
				break;

			case FinallyStatement finallyStatement:
				if (finallyStatement.Body is not null)
					finallyStatement.Body = RewriteStatement(finallyStatement.Body);
				break;

			case WithinStatement withinStatement:
				withinStatement.Allocator = LowerExpression(withinStatement.Allocator);
				if (withinStatement.Body is not null)
					withinStatement.Body = RewriteStatement(withinStatement.Body);
				break;
		}

		return statement;
	}

	void RewriteStatementList(List<Statement> statements)
	{
		for (int i = 0; i < statements.Count; i++)
		{
			if (statements[i] is DeclarationStatement declaration && TryRewriteInitDeclaration(declaration, out List<Statement>? rewritten))
			{
				statements.RemoveAt(i);
				statements.InsertRange(i, rewritten);
				i += rewritten.Count - 1;
				continue;
			}

			statements[i] = RewriteStatement(statements[i]);
		}
	}

	bool TryRewriteInitDeclaration(DeclarationStatement declaration, out List<Statement> statements)
	{
		statements = [];
		if (declaration.InitialValue is not ConstructionExpression { Kind: ConstructionKind.Init } construction || declaration.Target.Names.Count != 1)
			return false;

		for (int i = 0; i < construction.Arguments.Count; i++)
			construction.Arguments[i] = LowerArgument(construction.Arguments[i]);
		LowerInitializer(construction.Initializer);

		Expression target = CreateVariableReference(declaration.Target, declaration.Target.ResolvedType ?? construction.ResolvedType ?? ErrorType);
		CallExpression? initCall = CreateInitCallForConstruction(construction, target);
		declaration.InitialValue = construction.Initializer;

		if (initCall is null)
			return false;

		statements.Add(declaration);
		statements.Add(new ExpressionStatement
		{
			SourceSyntax = construction.SourceSyntax,
			ResolvedType = "void",
			Expression = initCall
		});
		return true;
	}

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
				within.Context = LowerExpression(within.Context);
				within.Expression = LowerExpression(within.Expression);
				return within;

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
				break;

			case LambdaExpression lambda:
				lambda.Body = RewriteFunctionBody(lambda.Body);
				break;

			case ArgumentExpression argument:
				return LowerArgument(argument);

			case CallExpression call:
				call.Target = LowerExpression(call.Target);
				for (int i = 0; i < call.Arguments.Count; i++)
					call.Arguments[i] = LowerArgument(call.Arguments[i]);
				break;

			case IndexExpression index:
				index.Target = LowerExpression(index.Target);
				for (int i = 0; i < index.Arguments.Count; i++)
					index.Arguments[i] = LowerArgument(index.Arguments[i]);
				break;

			case MemberExpression member:
				member.Target = LowerExpression(member.Target);
				break;

			case MemberReferenceExpression memberReference:
				memberReference.Target = LowerExpression(memberReference.Target);
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

			case FinallyDeleteExpression finallyDelete:
				finallyDelete.Expression = LowerExpression(finallyDelete.Expression);
				break;

			case BinaryExpression binary:
				binary.Left = LowerExpression(binary.Left);
				binary.Right = LowerExpression(binary.Right);
				break;

			case AssignmentExpression assignment:
				assignment.Target = LowerExpression(assignment.Target);
				assignment.Value = LowerExpression(assignment.Value);
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

	ArgumentExpression LowerArgument(ArgumentExpression argument)
	{
		argument.Value = LowerExpression(argument.Value);
		return argument;
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

		FunctionDefinition? constructor = FindConstructor(definition, construction.Arguments.Count);
		return construction.Kind switch
		{
			ConstructionKind.New => CreateCreateCall(definition, constructor, construction.Arguments, construction.SourceSyntax, construction.ResolvedType),
				ConstructionKind.Init => (Expression?)CreateInitCallForConstruction(construction, target: null) ?? construction,
			_ => construction
		};
	}

	Expression CreateCreateCall(TypeDefinition type, FunctionDefinition? constructor, List<ArgumentExpression> arguments, SyntaxNode? syntax, string? resolvedType)
	{
		FunctionDefinition? create = constructor is null ? null : FindGeneratedCreate(type, constructor);
		if (create is null)
			return CreateAllocCall(TypeReferenceFor(type), syntax);

		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = resolvedType ?? $"{type.Name}*",
			Target = CreateMethodReference(create, create.ResolvedType ?? $"{type.Name}*")
		};
		foreach (ArgumentExpression argument in arguments)
			call.Arguments.Add(argument);
		if (HasWithinParameter(create))
			call.Arguments.Add(new ArgumentExpression { Value = CurrentAllocator(), ResolvedType = AllocatorType });
		return call;
	}

	CallExpression? CreateInitCallForConstruction(ConstructionExpression construction, Expression? target)
	{
		string typeName = construction.Type?.ResolvedType ?? BaseConstructedType(construction.ResolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return null;

		FunctionDefinition? constructor = FindConstructor(definition, construction.Arguments.Count);
		if (constructor is null || !initNewMethods.TryGetValue(constructor, out FunctionDefinition? initNew))
			return null;

		return CreateInitNewCall(target, initNew, construction.Arguments, construction.SourceSyntax);
	}

	Expression RewriteDeleteExpression(Expression? expression)
	{
		Expression? target = LowerExpression(expression);
		string targetType = target?.ResolvedType ?? ErrorType;
		string? elementType = TryGetPointerElementType(targetType);
		bool isPointer = elementType is not null;
		bool isArray = TryGetArrayElementType(targetType) is not null;
		string deletedType = isPointer ? elementType ?? ErrorType : targetType;
		FunctionDefinition? destructor = FindDestructor(deletedType);

		if (!isPointer && !isArray && destructor is null)
			Report(target?.SourceSyntax, $"delete requires a pointer or a type with a destructor, not '{targetType}'.");

		FunctionDefinition? opDelete = null;
		if (destructor is not null && deleteMethods.TryGetValue(destructor, out FunctionDefinition? generatedDelete))
			opDelete = generatedDelete;
		if (target is null)
			return new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "void" };

		if (isArray)
			return CreateFreeCall(CreateArrayElementsAccess(target));

		return CreateDeleteExpression(target, opDelete, isPointer);
	}

	CallExpression CreateAllocCall(TypeReference type, SyntaxNode? syntax)
	{
		return CreateAllocCall(type, CurrentAllocator(), syntax);
	}

	CallExpression CreateAllocCall(TypeReference type, Expression allocator, SyntaxNode? syntax, Expression? length = null)
	{
		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = $"{type.ResolvedType}*",
			Target = new MemberReferenceExpression
			{
				Target = allocator,
				Name = "alloc",
				Member = allocatorAllocMethod,
				ResolvedType = allocatorAllocMethod.ResolvedType
			}
		};
		call.TypeArguments.Add(CloneType(type)!);
		call.Arguments.Add(new ArgumentExpression { Value = length ?? NumberLiteral("1", "nuint"), ResolvedType = "nuint" });
		call.Arguments.Add(new ArgumentExpression { Value = new SizeOfExpression { Type = CloneType(type), ResolvedType = "nuint" }, ResolvedType = "nuint" });
		call.Arguments.Add(new ArgumentExpression { Modifier = ArgumentModifier.Catch, Value = new NamedExpression { Name = "_", ResolvedType = "MemoryError" }, ResolvedType = "MemoryError" });
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
		if (HasWithinParameter(initNew))
			call.Arguments.Add(new ArgumentExpression { Value = allocatorArgument ?? CurrentAllocator(), ResolvedType = AllocatorType });
		return call;
	}

	CallExpression CreateDestructorCall(Expression target, FunctionDefinition opDelete, Expression? allocatorArgument = null)
	{
		CallExpression call = new()
		{
			ResolvedType = "void",
			Target = new MemberReferenceExpression
			{
				Target = target,
				Name = opDelete.Name,
				Member = opDelete,
				ResolvedType = "void"
			}
		};
		if (HasWithinParameter(opDelete))
			call.Arguments.Add(new ArgumentExpression { Value = allocatorArgument ?? CurrentAllocator(), ResolvedType = AllocatorType });
		return call;
	}

	CallExpression CreateFreeCall(Expression target)
	{
		return CreateFreeCall(target, CurrentAllocator());
	}

	CallExpression CreateFreeCall(Expression target, Expression allocator)
	{
		return new CallExpression
		{
			ResolvedType = "void",
			Target = new MemberReferenceExpression
			{
				Target = allocator,
				Name = "free",
				Member = allocatorFreeMethod,
				ResolvedType = allocatorFreeMethod.ResolvedType
			},
			Arguments =
			{
				new ArgumentExpression { Value = target, ResolvedType = target.ResolvedType }
			}
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

	static Expression CreateArrayElementsAccess(Expression target)
	{
		return new MemberExpression
		{
			Target = target,
			Name = "elements",
			ResolvedType = TryGetArrayElementType(target.ResolvedType ?? "") is string elementType ? $"{elementType}*" : ErrorType
		};
	}

	FunctionDefinition? FindGeneratedCreate(TypeDefinition type, FunctionDefinition constructor)
	{
		string name = $"{type.Name}_create";
		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (function.Name == name && function.Export == constructor.Export && CallableByArgumentCount(function.Parameters, CountCallableParameters(constructor.Parameters)))
				return function;
		}

		return null;
	}

	static MethodReferenceExpression CreateMethodReference(FunctionDefinition function, string type)
	{
		MethodReferenceExpression reference = new()
		{
			ResolvedType = type
		};
		reference.Candidates.Add(function);
		return reference;
	}

	DeclarationStatement CreateGeneratedLocal(string name, string typeName, TypeReference type, Expression? initialValue)
	{
		DeclarationStatement declaration = new()
		{
			InitialValue = initialValue,
			ResolvedType = "void"
		};
		declaration.Target.Type = type;
		declaration.Target.ResolvedType = typeName;
		declaration.Target.Names.Add(name);
		return declaration;
	}

	static VariableReferenceExpression CreateVariableReference(BindableNode variable, string type)
	{
		return new VariableReferenceExpression
		{
			Variable = variable,
			ResolvedType = type
		};
	}

	string NewGeneratedLocalName(string prefix)
	{
		string name = $"#{prefix}{generatedLocalIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
		generatedLocalIndex++;
		return name;
	}

	FunctionDefinition? FindConstructor(TypeDefinition type, int argumentCount)
	{
		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (function.Modifier == FunctionModifier.Constructor && CallableByArgumentCount(function.Parameters, argumentCount))
				return function;
		}

		return null;
	}

	FunctionDefinition? FindDestructor(string typeName)
	{
		if (!typeDefinitions.TryGetValue(typeName, out TypeDefinition? type))
			return null;

		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (function.Modifier == FunctionModifier.Destructor)
				return function;
		}

		return null;
	}

	static IEnumerable<FunctionDefinition> GetFunctions(TypeDefinition type)
	{
		return type switch
		{
			ClassDefinition classDefinition => classDefinition.Functions,
			StructDefinition structDefinition => structDefinition.Functions,
			InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
			EnumDefinition enumDefinition => enumDefinition.Functions,
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions,
			ParamsDefinition paramsDefinition => paramsDefinition.Functions,
			_ => []
		};
	}

	static bool CallableByArgumentCount(List<ParameterDefinition> parameters, int count)
	{
		int required = 0;
		int callable = 0;
		foreach (ParameterDefinition parameter in parameters)
		{
			if (IsHiddenParameter(parameter))
				continue;

			callable++;
			if (parameter.DefaultValue is null)
				required++;
		}

		return required <= count && count <= callable;
	}

	static bool IsHiddenParameter(ParameterDefinition parameter)
	{
		return parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown or ParameterModifier.Within
			|| parameter is WithinParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition;
	}

	static bool HasWithinParameter(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition)
				return true;
		}

		return false;
	}

	static ParameterDefinition? GetWithinParameter(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition)
				return parameter;
		}

		return null;
	}

	static ParameterDefinition? GetAllocatorParameter(FunctionDefinition function)
	{
		if (GetWithinParameter(function) is ParameterDefinition within)
			return within;
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Name == "allocator" && parameter.ResolvedType is "Allocator*" or AllocatorType)
				return parameter;
		}
		return null;
	}

	static bool HasCreateWithAllocatorAttribute(TypeDefinition type)
	{
		foreach (AttributeConstructor attribute in type.Attributes)
		{
			if (attribute.Name.TrimStart('@') == "createWithAllocator")
				return true;
		}

		return false;
	}

	static ParameterDefinition CreateAllocatorParameter()
	{
		return new ParameterDefinition
		{
			Name = "allocator",
			Symbol = "allocator",
			Type = AllocatorPointerType(),
			ResolvedType = "Allocator*"
		};
	}

	DeclarationStatement CreateResolvedAllocatorLocal(ParameterDefinition? allocator)
	{
		Expression source = allocator is null ? StdDefaultAllocator() : CreateVariableReference(allocator, allocator.ResolvedType ?? "Allocator*");
		DeclarationStatement declaration = CreateGeneratedLocal("resolvedAllocator", "Allocator*", AllocatorPointerType(), new BinaryExpression
		{
			Left = source,
			Operator = BinaryOperator.NullCoalescing,
			Right = StdDefaultAllocator(),
			ResolvedType = "Allocator*"
		});
		declaration.Target.Names.Clear();
		declaration.Target.Names.Add("resolvedAllocator");
		return declaration;
	}

	Expression? GetFunctionAllocatorForBody(FunctionDefinition function)
	{
		if (function.Name.EndsWith("op_delete", StringComparison.Ordinal) && GetWithinParameter(function) is ParameterDefinition deleteAllocator)
			return CreateVariableReference(deleteAllocator, deleteAllocator.ResolvedType ?? "Allocator*");
		if (function.Name.EndsWith("op_initnew", StringComparison.Ordinal) && GetWithinParameter(function) is not null)
			return CreateResolvedAllocatorReference();
		if (function.Name.EndsWith("op_initnew", StringComparison.Ordinal) || function.Name.EndsWith("op_delete", StringComparison.Ordinal))
			return StdDefaultAllocator();
		return null;
	}

	static NamedExpression CreateResolvedAllocatorReference()
	{
		return new NamedExpression
		{
			Name = "resolvedAllocator",
			ResolvedType = "Allocator*"
		};
	}

	static NamedExpression StdDefaultAllocator()
	{
		NamedExpression expression = new()
		{
			Name = "defaultAllocator",
			ResolvedType = "Allocator*"
		};
		expression.Qualifiers.Add("Std");
		return expression;
	}

	static void CopyParameters(List<ParameterDefinition> source, List<ParameterDefinition> target)
	{
		foreach (ParameterDefinition parameter in source)
			target.Add(CloneParameter(parameter));
	}

	static ParameterDefinition CloneParameter(ParameterDefinition parameter)
	{
		ParameterDefinition clone = parameter switch
		{
			ThisParameterDefinition => new ThisParameterDefinition(),
			WithinParameterDefinition => new WithinParameterDefinition(),
			SizeOfParameterDefinition => new SizeOfParameterDefinition(),
			VTableOfParameterDefinition vtableOf => new VTableOfParameterDefinition { InterfaceType = CloneType(vtableOf.InterfaceType) },
			_ => new ParameterDefinition()
		};
		clone.SourceSyntax = parameter.SourceSyntax;
		clone.Name = parameter.Name;
		clone.Symbol = parameter.Symbol;
		clone.Export = parameter.Export;
		clone.Extern = parameter.Extern;
		clone.Modifier = parameter.Modifier;
		clone.Type = parameter is WithinParameterDefinition && parameter.Type is null ? new AllocatorTypeReference { ResolvedType = AllocatorType } : CloneType(parameter.Type);
		clone.DefaultValue = parameter.DefaultValue;
		clone.ResolvedType = parameter is WithinParameterDefinition && parameter.ResolvedType == AllocatorType ? AllocatorType : parameter.ResolvedType;
		return clone;
	}

	static TypeReference TypeReferenceFor(TypeDefinition type)
	{
		return new TypeDefinitionReference
		{
			Name = type.Name,
			Definition = type,
			ResolvedType = type.Name
		};
	}

	static TypeReference? CloneType(TypeReference? type)
	{
		if (type is null)
			return null;

		TypeReference clone = type switch
		{
			NamedTypeReference named => CloneNamed(named),
			TypeDefinitionReference definition => CloneDefinitionReference(definition),
			GenericParameterTypeReference generic => new GenericParameterTypeReference { Name = generic.Name, Parameter = generic.Parameter },
			AllocatorTypeReference => new AllocatorTypeReference(),
			AttributedTypeReference attributed => new AttributedTypeReference { Attribute = attributed.Attribute, Type = CloneType(attributed.Type) },
			GenericTypeReference generic => CloneGeneric(generic),
			ArrayTypeReference array => new ArrayTypeReference { ElementType = CloneType(array.ElementType) },
			OptionalTypeReference optional => new OptionalTypeReference { ElementType = CloneType(optional.ElementType) },
			PointerTypeReference pointer => new PointerTypeReference { ElementType = CloneType(pointer.ElementType) },
			ConstTypeReference constant => new ConstTypeReference { Type = CloneType(constant.Type) },
			VolatileTypeReference vol => new VolatileTypeReference { Type = CloneType(vol.Type) },
			AnyTypeReference => new AnyTypeReference(),
			AutoTypeReference => new AutoTypeReference(),
			PrimitiveTypeReference primitive => new PrimitiveTypeReference { Type = primitive.Type },
			EscapedTypeReference escaped => new EscapedTypeReference { Type = CloneType(escaped.Type) },
			ScopedTypeReference scoped => CloneScoped(scoped),
			UnscopedTypeReference unscoped => CloneUnscoped(unscoped),
			CallableTypeReference callable => CloneCallable(callable),
			IterTypeReference iter => new IterTypeReference { ElementType = CloneType(iter.ElementType) },
			GroupedParamsTypeReference grouped => new GroupedParamsTypeReference { StructType = CloneType(grouped.StructType) },
			MaterializedStructTypeReference materialized => new MaterializedStructTypeReference { ParamsType = CloneType(materialized.ParamsType) },
			ThrownTypeReference thrown => new ThrownTypeReference { Type = CloneType(thrown.Type) },
			_ => new NamedTypeReference { Name = type.ResolvedType ?? ErrorType }
		};
		clone.SourceSyntax = type.SourceSyntax;
		clone.ResolvedType = type.ResolvedType;
		return clone;
	}

	static NamedTypeReference CloneNamed(NamedTypeReference named)
	{
		NamedTypeReference clone = new() { Name = named.Name };
		clone.Qualifiers.AddRange(named.Qualifiers);
		foreach (TypeReference argument in named.TypeArguments)
			clone.TypeArguments.Add(CloneType(argument)!);
		return clone;
	}

	static TypeDefinitionReference CloneDefinitionReference(TypeDefinitionReference definition)
	{
		TypeDefinitionReference clone = new() { Name = definition.Name, Definition = definition.Definition };
		foreach (TypeReference argument in definition.TypeArguments)
			clone.TypeArguments.Add(CloneType(argument)!);
		return clone;
	}

	static GenericTypeReference CloneGeneric(GenericTypeReference generic)
	{
		GenericTypeReference clone = new() { Type = CloneType(generic.Type) };
		foreach (TypeReference argument in generic.TypeArguments)
			clone.TypeArguments.Add(CloneType(argument)!);
		return clone;
	}

	static ScopedTypeReference CloneScoped(ScopedTypeReference scoped)
	{
		ScopedTypeReference clone = new() { Type = CloneType(scoped.Type) };
		clone.Anchors.AddRange(scoped.Anchors);
		return clone;
	}

	static UnscopedTypeReference CloneUnscoped(UnscopedTypeReference unscoped)
	{
		UnscopedTypeReference clone = new() { Type = CloneType(unscoped.Type) };
		clone.Anchors.AddRange(unscoped.Anchors);
		return clone;
	}

	static CallableTypeReference CloneCallable(CallableTypeReference callable)
	{
		CallableTypeReference clone = new() { Kind = callable.Kind, ReturnType = CloneType(callable.ReturnType) };
		foreach (ParameterDefinition parameter in callable.Parameters)
			clone.Parameters.Add(CloneParameter(parameter));
		return clone;
	}

	static TypeReference PointerTo(TypeReference type)
	{
		return new PointerTypeReference
		{
			ElementType = type,
			ResolvedType = $"{type.ResolvedType}*"
		};
	}

	static PrimitiveTypeReference VoidType()
	{
		return new PrimitiveTypeReference
		{
			Type = PrimitiveType.Void,
			ResolvedType = "void"
		};
	}

	static LiteralExpression NumberLiteral(string text, string resolvedType)
	{
		return new LiteralExpression
		{
			Kind = LiteralKind.Number,
			Text = text,
			Value = text,
			ResolvedType = resolvedType
		};
	}

	Expression CurrentAllocator()
	{
		return currentAllocatorOverride ?? new CurrentAllocatorExpression { ResolvedType = AllocatorType };
	}

	static TypeReference AllocatorPointerType()
	{
		return new PointerTypeReference
		{
			ElementType = new NamedTypeReference { Name = "Allocator", ResolvedType = "Allocator" },
			ResolvedType = "Allocator*"
		};
	}

	static string BaseConstructedType(string? type)
	{
		if (string.IsNullOrWhiteSpace(type))
			return "";

		return type.EndsWith("*", StringComparison.Ordinal) ? type[..^1] : type;
	}

	void Report(SyntaxNode? syntax, string message)
	{
		diagnostics.Add(new AnalysisDiagnostic(GetRange(syntax), message));
	}

	static FunctionDefinition CreateAllocatorAllocMethod()
	{
		FunctionDefinition method = new()
		{
			Name = "alloc",
			Symbol = "alloc",
			ResolvedType = "T*"
		};
		method.Parameters.Add(new ThisParameterDefinition { Name = "this", Symbol = "this", ResolvedType = "Allocator*" });
		method.Parameters.Add(new ParameterDefinition { Name = "len", Symbol = "len", ResolvedType = "nuint" });
		method.Parameters.Add(new SizeOfParameterDefinition { Name = "sizeof", Symbol = "sizeof", ResolvedType = "nuint" });
		method.Parameters.Add(new ParameterDefinition { Name = "MemoryError", Symbol = "MemoryError", Modifier = ParameterModifier.Thrown, ResolvedType = "MemoryError" });
		return method;
	}

	static FunctionDefinition CreateAllocatorFreeMethod()
	{
		FunctionDefinition method = new()
		{
			Name = "free",
			Symbol = "free",
			ResolvedType = "void"
		};
		method.Parameters.Add(new ThisParameterDefinition { Name = "this", Symbol = "this", ResolvedType = "Allocator*" });
		method.Parameters.Add(new ParameterDefinition { Name = "ptr", Symbol = "ptr", ResolvedType = "escaped void*" });
		return method;
	}
}

static class BindableNodeAnalyzerRewriteParameterExtensions
{
	public static List<ArgumentExpression> ArgumentsFromParameters(this FunctionDefinition function, bool skipAllocator = false)
	{
		List<ArgumentExpression> arguments = [];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown or ParameterModifier.Within
				|| parameter is WithinParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition)
				continue;
			if (skipAllocator && parameter.Name == "allocator")
				continue;

			arguments.Add(new ArgumentExpression
			{
				Value = new VariableReferenceExpression
				{
					Variable = parameter,
					ResolvedType = parameter.ResolvedType
				},
				ResolvedType = parameter.ResolvedType
			});
		}

		return arguments;
	}
}
