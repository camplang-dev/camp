using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
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
					RewriteFunction(function, classDefinition);
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					field.InitialValue = LowerExpression(field.InitialValue);
				foreach (FunctionDefinition function in structDefinition.Functions)
					RewriteFunction(function, structDefinition);
				break;

			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					RewriteFunction(function, interfaceDefinition);
				break;

			case EnumDefinition enumDefinition:
				foreach (VariableDefinition value in enumDefinition.Values)
					value.InitialValue = LowerExpression(value.InitialValue);
				foreach (FunctionDefinition function in enumDefinition.Functions)
					RewriteFunction(function, enumDefinition);
				break;

			case NewtypeDefinition newtypeDefinition:
				foreach (ParameterDefinition parameter in newtypeDefinition.Parameters)
					parameter.DefaultValue = LowerExpression(parameter.DefaultValue);
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					RewriteFunction(function, newtypeDefinition);
				break;

			case ParamsDefinition paramsDefinition:
				foreach (ParameterDefinition component in paramsDefinition.Components)
					component.DefaultValue = LowerExpression(component.DefaultValue);
				foreach (FunctionDefinition function in paramsDefinition.Functions)
					RewriteFunction(function, paramsDefinition);
				break;

			case FunctionDefinition function:
				RewriteFunction(function, containingType: null);
				break;
		}
	}

	void RewriteFunction(FunctionDefinition function, TypeDefinition? containingType)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
			parameter.DefaultValue = LowerExpression(parameter.DefaultValue);

		Expression? previousWithinContext = currentWithinContext;
		FunctionDefinition? previousFunction = currentRewriteFunction;
		TypeDefinition? previousType = currentRewriteContainingType;
		string? previousFunctionExitLabel = currentFunctionExitLabel;
		DeclarationTarget? previousFunctionReturnTarget = currentFunctionReturnTarget;
		string previousFunctionReturnType = currentFunctionReturnType;
		currentWithinContext = GetFunctionWithinContext(function);
		currentRewriteFunction = function;
		currentRewriteContainingType = containingType;
		currentFunctionExitLabel = null;
		currentFunctionReturnTarget = null;
		currentFunctionReturnType = function.ResolvedType ?? "void";
		InsertSizeOfFieldAssignments(function, containingType);
		InsertVTableOfFieldAssignments(function, containingType);
		InsertCreateVirtualTableAssignment(function, containingType);
		function.Body = RewriteFunctionBody(function.Body);
		if (function.Body is not null && currentFunctionExitLabel is not null)
			AppendFunctionExit(function.Body.Statements);
		currentWithinContext = previousWithinContext;
		currentRewriteFunction = previousFunction;
		currentRewriteContainingType = previousType;
		currentFunctionExitLabel = previousFunctionExitLabel;
		currentFunctionReturnTarget = previousFunctionReturnTarget;
		currentFunctionReturnType = previousFunctionReturnType;
	}

	BlockStatement? RewriteFunctionBody(BlockStatement? body)
	{
		if (body is null)
			return null;

		RewriteStatementList(body.Statements);
		return body;
	}

	void AppendFunctionExit(List<Statement> statements)
	{
		statements.Add(new LabelStatement { Name = currentFunctionExitLabel, ResolvedType = "void" });
		if (currentFunctionReturnTarget is not null)
		{
			statements.Add(new ReturnStatement
			{
				ResolvedType = "void",
				Expression = currentFunctionReturnType == "void" ? null : CreateVariableReference(currentFunctionReturnTarget, currentFunctionReturnType)
			});
		}
		else
		{
			statements.Add(new ReturnStatement { ResolvedType = "void" });
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
				if (expression.Expression is AssignmentExpression assignment && TryRewriteParamsAssignment(assignment, out List<Statement>? assignmentStatements))
					return CreateBlock(assignmentStatements);
				expression.Expression = LowerExpression(expression.Expression);
				if (expression.Expression is UnaryExpression { Operator: UnaryOperator.Throw } throwExpression)
					return CreateBlock(CreateThrowTransfer(throwExpression.Operand, throwExpression.SourceSyntax));
				break;

			case DeclarationStatement declaration:
				if (TryRewriteArrayNewPointerDeclaration(declaration, out List<Statement>? arrayPointerStatements))
					return CreateBlock(arrayPointerStatements);
				if (TryRewriteNewDeclaration(declaration, out List<Statement>? newStatements))
					return CreateBlock(newStatements);
				if (TryRewriteInitDeclaration(declaration, out List<Statement>? statements))
					return CreateBlock(statements);
				declaration.InitialValue = declaration.InitialValue is not null && ContainsUncaughtThrow(declaration.InitialValue)
					? HoistThrowingExpression(declaration.InitialValue)
					: LowerExpression(declaration.InitialValue);
				declaration.InitialValue = LowerInterfaceConversion(declaration.Target.Type, declaration.InitialValue);
				break;

			case IfStatement ifStatement:
				ifStatement.Condition = ifStatement.Condition is not null && ContainsUncaughtThrow(ifStatement.Condition)
					? HoistThrowingExpression(ifStatement.Condition)
					: LowerExpression(ifStatement.Condition);
				if (ifStatement.Body is not null)
					ifStatement.Body = RewriteStatement(ifStatement.Body);
				if (ifStatement.ElseBody is not null)
					ifStatement.ElseBody = RewriteStatement(ifStatement.ElseBody);
				break;

			case WhileStatement whileStatement:
				if (whileStatement.Condition is not null && ContainsUncaughtThrow(whileStatement.Condition))
					return RewriteWhileStatementWithThrowingCondition(whileStatement);
				whileStatement.Condition = LowerExpression(whileStatement.Condition);
				string whileContinueLabel = NewGeneratedLabelName("while_continue");
				string whileBreakLabel = NewGeneratedLabelName("while_break");
				if (whileStatement.Body is not null)
					whileStatement.Body = RewriteLoopBody(whileStatement.Body, whileContinueLabel, whileBreakLabel);
				return WrapLoopWithBreakLabel(whileStatement, whileBreakLabel);

			case DoWhileStatement doWhile:
				string doContinueLabel = NewGeneratedLabelName("do_continue");
				string doBreakLabel = NewGeneratedLabelName("do_break");
				if (doWhile.Body is not null)
					doWhile.Body = RewriteLoopBody(doWhile.Body, doContinueLabel, doBreakLabel);
				doWhile.Condition = LowerExpression(doWhile.Condition);
				return WrapLoopWithBreakLabel(doWhile, doBreakLabel);

			case ForStatement forStatement:
				if (forStatement.Condition.Declaration is not null)
					forStatement.Condition.Declaration = (DeclarationStatement)RewriteStatement(forStatement.Condition.Declaration);
				for (int i = 0; i < forStatement.Condition.Clauses.Count; i++)
					forStatement.Condition.Clauses[i] = LowerExpression(forStatement.Condition.Clauses[i]);
				string forContinueLabel = NewGeneratedLabelName("for_continue");
				string forBreakLabel = NewGeneratedLabelName("for_break");
				if (forStatement.Body is not null)
					forStatement.Body = RewriteLoopBody(forStatement.Body, forContinueLabel, forBreakLabel);
				return WrapLoopWithBreakLabel(forStatement, forBreakLabel);

			case ForeachStatement foreachStatement:
				return RewriteForeachStatement(foreachStatement);

			case SwitchStatement switchStatement:
				switchStatement.Expression = switchStatement.Expression is not null && ContainsUncaughtThrow(switchStatement.Expression)
					? HoistThrowingExpression(switchStatement.Expression)
					: LowerExpression(switchStatement.Expression);
				return RewriteSwitchStatementWithBreakLabel(switchStatement);

			case CaseStatement caseStatement:
				caseStatement.Expression = LowerExpression(caseStatement.Expression);
				break;

			case LabelStatement:
			case GotoStatement:
				break;

			case BreakStatement:
			case ContinueStatement:
				return WithPendingCleanups(statement);

			case ReturnStatement returnStatement:
				returnStatement.Expression = returnStatement.Expression is not null && ContainsUncaughtThrow(returnStatement.Expression)
					? HoistThrowingExpression(returnStatement.Expression)
					: LowerExpression(returnStatement.Expression);
				if (TryRewriteExpandedReturn(returnStatement, out Statement? expandedReturn))
					return PrependThrownParameterClear(WithPendingCleanups(expandedReturn), returnStatement.SourceSyntax);
				return PrependThrownParameterClear(WithPendingCleanups(returnStatement), returnStatement.SourceSyntax);

			case YieldStatement yieldStatement:
				yieldStatement.Expression = LowerExpression(yieldStatement.Expression);
				return WithPendingCleanups(yieldStatement);

			case DeleteStatement deleteStatement:
				return WithPendingCleanups(new ExpressionStatement
				{
					SourceSyntax = deleteStatement.SourceSyntax,
					ResolvedType = "void",
					Expression = RewriteDeleteExpression(deleteStatement.Expression)
				});

			case TryStatement tryStatement:
				return RewriteTryStatement(tryStatement);

			case CatchStatement catchStatement:
				if (catchStatement.Body is not null)
					catchStatement.Body = RewriteStatement(catchStatement.Body);
				break;

			case FinallyStatement finallyStatement:
				if (finallyStatement.Body is not null)
					finallyStatement.Body = RewriteStatement(finallyStatement.Body);
				break;

			case WithinStatement withinStatement:
			{
				Expression? allocator = LowerExpression(withinStatement.Allocator);
				DeclarationStatement? allocatorLocal = allocator is null ? null : CreateWithinContextLocal(allocator, withinStatement.SourceSyntax);
				Expression? previousWithinContext = currentWithinContext;
				currentWithinContext = allocatorLocal is null ? allocator : CreateVariableReference(allocatorLocal.Target, allocatorLocal.Target.ResolvedType ?? ErrorType);
				Statement rewritten = withinStatement.Body is null ? CreateBlock([]) : RewriteStatement(withinStatement.Body);
				currentWithinContext = previousWithinContext;
				if (allocatorLocal is null)
					return rewritten;

				List<Statement> withinStatements = [allocatorLocal, rewritten];
				return CreateBlock(withinStatements);
			}
		}

		return statement;
	}

	Statement PrependThrownParameterClear(Statement returnTransfer, SyntaxNode? syntax)
	{
		ParameterDefinition? thrownParameter = GetFunctionThrownParameter(currentRewriteFunction);
		if (thrownParameter is null)
			return returnTransfer;

		string thrownType = thrownParameter.ResolvedType ?? ErrorType;
		return CreateBlock(
		[
			new ExpressionStatement
			{
				SourceSyntax = syntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = syntax,
					Target = CreateVariableReference(thrownParameter, thrownType),
					Operator = AssignmentOperator.Assign,
					Value = new DefaultExpression { SourceSyntax = syntax, ResolvedType = thrownType },
					ResolvedType = thrownType
				}
			},
			returnTransfer
		]);
	}

	Statement RewriteForeachStatement(ForeachStatement foreachStatement)
	{
		if (iteratorForeachStates.TryGetValue(foreachStatement, out IteratorForeachStateFields? fields) && fields.IsProtocol)
			return RewriteIteratorProtocolForeachStatement(foreachStatement);
		if (foreachStatement.IteratorNext is not null)
			return RewriteIteratorForeachStatement(foreachStatement);
		string sourceType = foreachStatement.Source?.ResolvedType ?? "";
		if (TryGetIteratorProtocolCurrentTypes(sourceType, out _) || IsIteratorProtocolCallComponent(sourceType))
			return RewriteIteratorProtocolForeachStatement(foreachStatement);
		return RewriteArrayForeachStatement(foreachStatement);
	}

	bool IsIteratorProtocolCallComponent(string sourceType)
	{
		return TryGetCallableShape(sourceType, out CallableShape callable)
			&& callable.Kind == "fn"
			&& callable.ReturnType == "bool"
			&& callable.Parameters.Count >= 2
			&& callable.Parameters[0] == "void*";
	}

	Statement RewriteIteratorProtocolForeachStatement(ForeachStatement foreachStatement)
	{
		Expression? source = foreachStatement.Source;
		bool hasComponents = TryCreateParamsComponentExpressions(source, out List<Expression> components) && components.Count == 2;
		if (!hasComponents)
		{
			source = foreachStatement.Source is not null && ContainsUncaughtThrow(foreachStatement.Source)
				? HoistThrowingExpression(foreachStatement.Source)
				: LowerExpression(foreachStatement.Source);
			hasComponents = TryCreateParamsComponentExpressions(source, out components) && components.Count == 2;
		}
		if (!hasComponents)
		{
			foreachStatement.Source = source;
			if (foreachStatement.Body is not null)
				foreachStatement.Body = RewriteStatement(foreachStatement.Body);
			return foreachStatement;
		}

		Expression call = LowerExpression(components[0]) ?? components[0];
		Expression context = LowerExpression(components[1]) ?? components[1];
		if (!TryGetCallableShape(call.ResolvedType, out CallableShape callable) || callable.Parameters.Count < 2)
			return RewriteArrayForeachStatement(foreachStatement);

		string elementType = foreachStatement.Target.ResolvedType ?? TryGetPointerElementType(callable.Parameters[1]) ?? ErrorType;
		bool useLiftedState = iteratorForeachStates.TryGetValue(foreachStatement, out IteratorForeachStateFields? stateFields) && stateFields is { IsProtocol: true, ContextFieldName: not null };
		string callType = call.ResolvedType ?? BuildCallableType("fn", "bool", ["void*", AddPointer(elementType)]);
		List<Statement> statements = [];
		DeclarationStatement? callLocal = null;
		DeclarationStatement? contextLocal = null;
		DeclarationStatement? currentLocal = null;
		Expression CallReference() => useLiftedState && stateFields is not null ? ThisMemberReference(stateFields.IteratorFieldName, stateFields.IteratorType) : CreateVariableReference(callLocal!.Target, callLocal.Target.ResolvedType ?? ErrorType);
		Expression ContextReference() => useLiftedState && stateFields is { ContextFieldName: not null } ? ThisMemberReference(stateFields.ContextFieldName, "void*") : CreateVariableReference(contextLocal!.Target, contextLocal.Target.ResolvedType ?? "void*");
		Expression CurrentReference() => useLiftedState && stateFields is not null ? ThisMemberReference(stateFields.CurrentFieldName, stateFields.ElementType) : CreateVariableReference(currentLocal!.Target, elementType);
		if (useLiftedState && stateFields is not null)
		{
			statements.Add(CreateAssignmentStatement(CallReference(), call, callType, foreachStatement.SourceSyntax));
			statements.Add(CreateAssignmentStatement(ContextReference(), context, "void*", foreachStatement.SourceSyntax));
			statements.Add(CreateAssignmentStatement(CurrentReference(), new DefaultExpression { ResolvedType = elementType }, elementType, foreachStatement.SourceSyntax));
		}
		else
		{
			callLocal = CreateGeneratedLocal(NewGeneratedLocalName("foreachIterCall"), callType, TypeReferenceForResolvedName(callType), call);
			contextLocal = CreateGeneratedLocal(NewGeneratedLocalName("foreachIterContext"), context.ResolvedType ?? "void*", TypeReferenceForResolvedName(context.ResolvedType ?? "void*"), context);
			currentLocal = CreateGeneratedLocal(NewGeneratedLocalName("foreachCurrent"), elementType, TypeReferenceForResolvedName(elementType), new DefaultExpression { ResolvedType = elementType });
			statements.Add(callLocal);
			statements.Add(contextLocal);
			statements.Add(currentLocal);
		}

		DeclarationStatement loopValue = new()
		{
			SourceSyntax = foreachStatement.SourceSyntax,
			ResolvedType = "void",
			InitialValue = CurrentReference()
		};
		loopValue.Target.SourceSyntax = foreachStatement.Target.SourceSyntax;
		loopValue.Target.Type = foreachStatement.Target.Type is AutoTypeReference ? TypeReferenceForResolvedName(elementType) : CloneType(foreachStatement.Target.Type);
		loopValue.Target.ResolvedType = elementType;
		foreach (string name in foreachStatement.Target.Names)
			loopValue.Target.Names.Add(name);

		string continueLabel = NewGeneratedLabelName("foreach_continue");
		string breakLabel = NewGeneratedLabelName("foreach_break");
		BlockStatement loopBody = new() { SourceSyntax = foreachStatement.Body?.SourceSyntax ?? foreachStatement.SourceSyntax, ResolvedType = "void" };
		Statement cleanupStatement = CreateIteratorProtocolCleanupStatement(CallReference(), ContextReference(), foreachStatement.SourceSyntax);
		CleanupScope iteratorCleanupScope = new([cleanupStatement], RunBeforeCatch: true) { RunBeforeContinue = false };
		if (currentFunctionReturnType != "void" && ContainsReturnStatement(foreachStatement.Body))
		{
			DeclarationStatement returnLocal = CreateGeneratedLocal(NewGeneratedLocalName("return"), currentFunctionReturnType, TypeReferenceForResolvedName(currentFunctionReturnType), new DefaultExpression { ResolvedType = currentFunctionReturnType });
			iteratorCleanupScope.ReturnTarget = returnLocal.Target;
			iteratorCleanupScope.ReturnType = currentFunctionReturnType;
			currentStatementPrefix?.Add(returnLocal);
		}
		currentCleanupScopes.Add(iteratorCleanupScope);
		currentLoopTransferTargets.Add(new LoopTransferTarget(breakLabel, continueLabel));
		List<Statement> bodyStatements = [loopValue];
		if (foreachStatement.Body is not null)
			bodyStatements.Add(foreachStatement.Body);
		RewriteStatementList(bodyStatements);
		currentLoopTransferTargets.RemoveAt(currentLoopTransferTargets.Count - 1);
		currentCleanupScopes.RemoveAt(currentCleanupScopes.Count - 1);
		loopBody.Statements.AddRange(bodyStatements);
		loopBody.Statements.Add(new LabelStatement { Name = continueLabel, ResolvedType = "void" });

		WhileStatement loop = new()
		{
			SourceSyntax = foreachStatement.SourceSyntax,
			ResolvedType = "void",
			Condition = LowerExpression(CreateIteratorProtocolCall(CallReference(), ContextReference(), CurrentReference(), foreachStatement.SourceSyntax)),
			Body = loopBody
		};
		statements.Add(loop);
		statements.Add(cleanupStatement);
		bool hasCleanupExit = iteratorCleanupScope.ExitLabelName is not null;
		string? doneLabel = hasCleanupExit ? NewGeneratedLabelName("foreach_done") : null;
		if (doneLabel is not null)
			statements.Add(new GotoStatement { TargetName = doneLabel, ResolvedType = "void" });
		AppendCleanupScopeExit(statements, iteratorCleanupScope);
		statements.Add(new LabelStatement { Name = breakLabel, ResolvedType = "void" });
		if (doneLabel is not null)
			statements.Add(new LabelStatement { Name = doneLabel, ResolvedType = "void" });
		return CreateBlock(statements);
	}

	ExpressionStatement CreateAssignmentStatement(Expression target, Expression value, string type, SyntaxNode? syntax)
	{
		return new ExpressionStatement
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				SourceSyntax = syntax,
				Target = target,
				Operator = AssignmentOperator.Assign,
				Value = value,
				ResolvedType = type
			}
		};
	}

	CallExpression CreateIteratorProtocolCall(Expression call, Expression context, Expression current, SyntaxNode? syntax)
	{
		return new CallExpression
		{
			SourceSyntax = syntax,
			Target = call,
			ResolvedType = "bool",
			Arguments =
			{
				new ArgumentExpression { SourceSyntax = syntax, Value = context, ResolvedType = context.ResolvedType },
				new ArgumentExpression
				{
					SourceSyntax = syntax,
					Value = new UnaryExpression
					{
						SourceSyntax = syntax,
						Operator = UnaryOperator.AddressOf,
						Operand = current,
						ResolvedType = AddPointer(current.ResolvedType ?? ErrorType)
					},
					ResolvedType = AddPointer(current.ResolvedType ?? ErrorType)
				}
			}
		};
	}

	Statement CreateIteratorProtocolCleanupStatement(Expression call, Expression context, SyntaxNode? syntax)
	{
		return new ExpressionStatement
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Expression = new CallExpression
			{
				SourceSyntax = syntax,
				Target = call,
				ResolvedType = "bool",
				Arguments =
				{
					new ArgumentExpression { SourceSyntax = syntax, Value = context, ResolvedType = context.ResolvedType },
					new ArgumentExpression { SourceSyntax = syntax, Value = NullLiteral(syntax), ResolvedType = "#NULL" }
				}
			}
		};
	}

	Statement RewriteArrayForeachStatement(ForeachStatement foreachStatement)
	{
		Expression? source = foreachStatement.Source is not null && ContainsUncaughtThrow(foreachStatement.Source)
			? HoistThrowingExpression(foreachStatement.Source)
			: LowerExpression(foreachStatement.Source);
		if (!TryCreateParamsComponentExpressions(source, out List<Expression> sourceComponents) || sourceComponents.Count < 2)
		{
			foreachStatement.Source = source;
			if (foreachStatement.Body is not null)
				foreachStatement.Body = RewriteStatement(foreachStatement.Body);
			return foreachStatement;
		}

		Expression elements = LowerExpression(sourceComponents[0]) ?? sourceComponents[0];
		Expression length = LowerExpression(sourceComponents[^1]) ?? sourceComponents[^1];
		string elementPointerType = elements.ResolvedType ?? ErrorType;
		string elementType = TryGetPointerElementType(elementPointerType) ?? TryGetArrayElementType(source?.ResolvedType) ?? foreachStatement.Target.ResolvedType ?? ErrorType;

		DeclarationStatement elementsLocal = CreateGeneratedLocal(NewGeneratedLocalName("foreachElements"), elementPointerType, TypeReferenceForResolvedName(elementPointerType), elements);
		DeclarationStatement lengthLocal = CreateGeneratedLocal(NewGeneratedLocalName("foreachLength"), length.ResolvedType ?? "nuint", TypeReferenceForResolvedName(length.ResolvedType ?? "nuint"), length);
		DeclarationStatement indexLocal = CreateGeneratedLocal(NewGeneratedLocalName("foreachIndex"), "nuint", NuintType(), NumberLiteral("0", "nuint"));

		Expression indexReference = CreateVariableReference(indexLocal.Target, "nuint");
		Expression elementsReference = CreateVariableReference(elementsLocal.Target, elementPointerType);

		DeclarationStatement loopValue = new()
		{
			SourceSyntax = foreachStatement.SourceSyntax,
			ResolvedType = "void",
			InitialValue = new IndexExpression
			{
				SourceSyntax = foreachStatement.SourceSyntax,
				Target = elementsReference,
				ResolvedType = elementType
			}
		};
		loopValue.Target.SourceSyntax = foreachStatement.Target.SourceSyntax;
		loopValue.Target.Type = foreachStatement.Target.Type is AutoTypeReference ? TypeReferenceForResolvedName(elementType) : CloneType(foreachStatement.Target.Type);
		loopValue.Target.ResolvedType = elementType;
		foreach (string name in foreachStatement.Target.Names)
			loopValue.Target.Names.Add(name);
		((IndexExpression)loopValue.InitialValue).Arguments.Add(new ArgumentExpression
		{
			SourceSyntax = foreachStatement.SourceSyntax,
			Value = CreateVariableReference(indexLocal.Target, "nuint"),
			ResolvedType = "nuint"
		});

		string continueLabel = NewGeneratedLabelName("foreach_continue");
		string breakLabel = NewGeneratedLabelName("foreach_break");
		BlockStatement loopBody = new() { SourceSyntax = foreachStatement.Body?.SourceSyntax ?? foreachStatement.SourceSyntax, ResolvedType = "void" };
		currentLoopTransferTargets.Add(new LoopTransferTarget(breakLabel, continueLabel));
		List<Statement> bodyStatements = [loopValue];
		if (foreachStatement.Body is not null)
			bodyStatements.Add(foreachStatement.Body);
		RewriteStatementList(bodyStatements);
		currentLoopTransferTargets.RemoveAt(currentLoopTransferTargets.Count - 1);
		loopBody.Statements.AddRange(bodyStatements);
		loopBody.Statements.Add(new LabelStatement { Name = continueLabel, ResolvedType = "void" });
		loopBody.Statements.Add(new ExpressionStatement
		{
			SourceSyntax = foreachStatement.SourceSyntax,
			ResolvedType = "void",
			Expression = new PostfixUpdateExpression
			{
				SourceSyntax = foreachStatement.SourceSyntax,
				Expression = CreateVariableReference(indexLocal.Target, "nuint"),
				Operator = UpdateOperator.Increment,
				ResolvedType = "nuint"
			}
		});

		WhileStatement loop = new()
		{
			SourceSyntax = foreachStatement.SourceSyntax,
			ResolvedType = "void",
			Condition = new BinaryExpression
			{
				SourceSyntax = foreachStatement.SourceSyntax,
				Left = indexReference,
				Operator = BinaryOperator.LessThan,
				Right = CreateVariableReference(lengthLocal.Target, lengthLocal.Target.ResolvedType ?? "nuint"),
				ResolvedType = "bool"
			},
			Body = loopBody
		};

		return CreateBlock([elementsLocal, lengthLocal, indexLocal, loop, new LabelStatement { Name = breakLabel, ResolvedType = "void" }]);
	}

	Statement RewriteIteratorForeachStatement(ForeachStatement foreachStatement)
	{
		Expression? source = foreachStatement.Source is not null && ContainsUncaughtThrow(foreachStatement.Source)
			? HoistThrowingExpression(foreachStatement.Source)
			: LowerExpression(foreachStatement.Source);
		FunctionDefinition next = foreachStatement.IteratorNext!;
		string iteratorType = source?.ResolvedType ?? ErrorType;
		string elementType = foreachStatement.Target.ResolvedType ?? TryGetPointerElementType(GetCallableParameters(next.Parameters)[0].ResolvedType) ?? ErrorType;
		bool useLiftedState = iteratorForeachStates.TryGetValue(foreachStatement, out IteratorForeachStateFields? stateFields);
		DeclarationStatement? iteratorLocal = null;
		DeclarationStatement? currentLocal = null;
		List<Statement> setupStatements = [];
		if (useLiftedState && stateFields is not null)
		{
			iteratorType = stateFields.IteratorType;
			elementType = stateFields.ElementType;
			setupStatements.Add(new ExpressionStatement
			{
				SourceSyntax = foreachStatement.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = foreachStatement.SourceSyntax,
					Target = ThisMemberReference(stateFields.IteratorFieldName, iteratorType),
					Operator = AssignmentOperator.Assign,
					Value = source,
					ResolvedType = iteratorType
				}
			});
			setupStatements.Add(new ExpressionStatement
			{
				SourceSyntax = foreachStatement.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = foreachStatement.SourceSyntax,
					Target = ThisMemberReference(stateFields.CurrentFieldName, elementType),
					Operator = AssignmentOperator.Assign,
					Value = new DefaultExpression { ResolvedType = elementType },
					ResolvedType = elementType
				}
			});
		}
		else
		{
			iteratorLocal = CreateGeneratedLocal(NewGeneratedLocalName("foreachIter"), iteratorType, TypeReferenceForResolvedName(iteratorType), source);
			currentLocal = CreateGeneratedLocal(NewGeneratedLocalName("foreachCurrent"), elementType, TypeReferenceForResolvedName(elementType), new DefaultExpression { ResolvedType = elementType });
			setupStatements.Add(iteratorLocal);
			setupStatements.Add(currentLocal);
		}
		Expression IteratorReference()
		{
			return useLiftedState && stateFields is not null
				? ThisMemberReference(stateFields.IteratorFieldName, iteratorType)
				: CreateVariableReference(iteratorLocal!.Target, iteratorType);
		}
		Expression CurrentReference()
		{
			return useLiftedState && stateFields is not null
				? ThisMemberReference(stateFields.CurrentFieldName, elementType)
				: CreateVariableReference(currentLocal!.Target, elementType);
		}

		DeclarationStatement loopValue = new()
		{
			SourceSyntax = foreachStatement.SourceSyntax,
			ResolvedType = "void",
			InitialValue = CurrentReference()
		};
		loopValue.Target.SourceSyntax = foreachStatement.Target.SourceSyntax;
		loopValue.Target.Type = foreachStatement.Target.Type is AutoTypeReference ? TypeReferenceForResolvedName(elementType) : CloneType(foreachStatement.Target.Type);
		loopValue.Target.ResolvedType = elementType;
		foreach (string name in foreachStatement.Target.Names)
			loopValue.Target.Names.Add(name);

		string continueLabel = NewGeneratedLabelName("foreach_continue");
		string breakLabel = NewGeneratedLabelName("foreach_break");
		BlockStatement loopBody = new() { SourceSyntax = foreachStatement.Body?.SourceSyntax ?? foreachStatement.SourceSyntax, ResolvedType = "void" };
		Statement cleanupStatement = CreateIteratorCleanupStatement(IteratorReference(), iteratorType, foreachStatement.SourceSyntax);
		CleanupScope iteratorCleanupScope = new([cleanupStatement], RunBeforeCatch: true) { RunBeforeContinue = false };
		if (currentFunctionReturnType != "void" && ContainsReturnStatement(foreachStatement.Body))
		{
			DeclarationStatement returnLocal = CreateGeneratedLocal(NewGeneratedLocalName("return"), currentFunctionReturnType, TypeReferenceForResolvedName(currentFunctionReturnType), new DefaultExpression { ResolvedType = currentFunctionReturnType });
			setupStatements.Add(returnLocal);
			iteratorCleanupScope.ReturnTarget = returnLocal.Target;
			iteratorCleanupScope.ReturnType = currentFunctionReturnType;
		}
		currentCleanupScopes.Add(iteratorCleanupScope);
		currentLoopTransferTargets.Add(new LoopTransferTarget(breakLabel, continueLabel));
		List<Statement> bodyStatements = [loopValue];
		if (foreachStatement.Body is not null)
			bodyStatements.Add(foreachStatement.Body);
		RewriteStatementList(bodyStatements);
		currentLoopTransferTargets.RemoveAt(currentLoopTransferTargets.Count - 1);
		currentCleanupScopes.RemoveAt(currentCleanupScopes.Count - 1);
		loopBody.Statements.AddRange(bodyStatements);
		loopBody.Statements.Add(new LabelStatement { Name = continueLabel, ResolvedType = "void" });

		CallExpression nextCall = new()
		{
			SourceSyntax = foreachStatement.SourceSyntax,
			Target = new MemberReferenceExpression
			{
				SourceSyntax = foreachStatement.SourceSyntax,
				Target = IteratorReference(),
				Name = "next",
				Member = next,
				ResolvedType = BuildFunctionValueType(next, isInstance: true)
			},
			ResolvedType = "bool",
			Arguments =
			{
				new ArgumentExpression
				{
					SourceSyntax = foreachStatement.SourceSyntax,
					Value = new UnaryExpression
					{
						SourceSyntax = foreachStatement.SourceSyntax,
						Operator = UnaryOperator.AddressOf,
						Operand = CurrentReference(),
						ResolvedType = $"{elementType}*"
					},
					ResolvedType = $"{elementType}*"
				}
			}
		};
		WhileStatement loop = new()
		{
			SourceSyntax = foreachStatement.SourceSyntax,
			ResolvedType = "void",
			Condition = LowerExpression(nextCall),
			Body = loopBody
		};

		setupStatements.Add(loop);
		setupStatements.Add(cleanupStatement);
		bool hasCleanupExit = iteratorCleanupScope.ExitLabelName is not null;
		string? doneLabel = hasCleanupExit ? NewGeneratedLabelName("foreach_done") : null;
		if (doneLabel is not null)
			setupStatements.Add(new GotoStatement { TargetName = doneLabel, ResolvedType = "void" });
		AppendCleanupScopeExit(setupStatements, iteratorCleanupScope);
		setupStatements.Add(new LabelStatement { Name = breakLabel, ResolvedType = "void" });
		if (doneLabel is not null)
			setupStatements.Add(new LabelStatement { Name = doneLabel, ResolvedType = "void" });
		return CreateBlock(setupStatements);
	}

	Statement CreateIteratorCleanupStatement(Expression iteratorTarget, string iteratorType, SyntaxNode? syntax)
	{
		return new ExpressionStatement
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Expression = RewriteDeleteExpression(iteratorTarget)
		};
	}

	static bool ContainsReturnStatement(Statement? statement)
	{
		return statement switch
		{
			ReturnStatement returnStatement => !returnStatement.SkipPendingCleanups,
			BlockStatement block => block.Statements.Exists(ContainsReturnStatement),
			IfStatement ifStatement => ContainsReturnStatement(ifStatement.Body) || ContainsReturnStatement(ifStatement.ElseBody),
			WhileStatement whileStatement => ContainsReturnStatement(whileStatement.Body),
			DoWhileStatement doWhile => ContainsReturnStatement(doWhile.Body),
			ForStatement forStatement => ContainsReturnStatement(forStatement.Body),
			ForeachStatement foreachStatement => ContainsReturnStatement(foreachStatement.Body),
			SwitchStatement switchStatement => switchStatement.Statements.Exists(ContainsReturnStatement),
			TryStatement tryStatement => ContainsReturnStatement(tryStatement.Body)
				|| tryStatement.Catches.Exists(catchStatement => ContainsReturnStatement(catchStatement.Body))
				|| ContainsReturnStatement(tryStatement.Finally),
			CatchStatement catchStatement => ContainsReturnStatement(catchStatement.Body),
			FinallyStatement finallyStatement => ContainsReturnStatement(finallyStatement.Body),
			WithinStatement withinStatement => ContainsReturnStatement(withinStatement.Body),
			_ => false
		};
	}

	Statement RewriteLoopBody(Statement body, string continueLabel, string breakLabel)
	{
		currentLoopTransferTargets.Add(new LoopTransferTarget(breakLabel, continueLabel));
		BlockStatement block = body as BlockStatement ?? CreateBlock([body]);
		RewriteStatementList(block.Statements);
		currentLoopTransferTargets.RemoveAt(currentLoopTransferTargets.Count - 1);
		block.Statements.Add(new LabelStatement { Name = continueLabel, ResolvedType = "void" });
		return block;
	}

	Statement WrapLoopWithBreakLabel(Statement loop, string breakLabel)
	{
		return CreateBlock([loop, new LabelStatement { Name = breakLabel, ResolvedType = "void" }]);
	}

	Statement RewriteSwitchStatementWithBreakLabel(SwitchStatement switchStatement)
	{
		string breakLabel = NewGeneratedLabelName("switch_break");
		currentLoopTransferTargets.Add(new LoopTransferTarget(breakLabel, null));
		RewriteStatementList(switchStatement.Statements);
		currentLoopTransferTargets.RemoveAt(currentLoopTransferTargets.Count - 1);
		return CreateBlock([switchStatement, new LabelStatement { Name = breakLabel, ResolvedType = "void" }]);
	}

	void RewriteStatementList(List<Statement> statements)
	{
		CleanupScope cleanupScope = new([], RunBeforeCatch: true);
		currentCleanupScopes.Add(cleanupScope);
		for (int i = 0; i < statements.Count; i++)
		{
			if (statements[i] is FinallyStatement finallyStatement)
			{
				if (finallyStatement.Body is not null)
					cleanupScope.Statements.Add(RewriteStatement(finallyStatement.Body));
				statements.RemoveAt(i);
				i--;
				continue;
			}

			if (statements[i] is DeclarationStatement pointerDeclaration && TryRewriteArrayNewPointerDeclaration(pointerDeclaration, out List<Statement>? pointerRewritten))
			{
				statements.RemoveAt(i);
				statements.InsertRange(i, pointerRewritten);
				i += pointerRewritten.Count - 1;
				continue;
			}

			if (statements[i] is DeclarationStatement declaration && (TryRewriteNewDeclaration(declaration, out List<Statement>? rewritten) || TryRewriteInitDeclaration(declaration, out rewritten)))
			{
				statements.RemoveAt(i);
				statements.InsertRange(i, rewritten);
				i += rewritten.Count - 1;
				continue;
			}

			List<Statement>? previousPrefix = currentStatementPrefix;
			List<Statement>? previousSuffix = currentStatementSuffix;
			currentStatementPrefix = [];
			currentStatementSuffix = [];
			statements[i] = RewriteStatement(statements[i]);
			if (currentStatementPrefix.Count > 0)
			{
				statements.InsertRange(i, currentStatementPrefix);
				i += currentStatementPrefix.Count;
			}
			if (currentStatementSuffix.Count > 0)
			{
				statements.InsertRange(i + 1, currentStatementSuffix);
				i += currentStatementSuffix.Count;
			}
			currentStatementPrefix = previousPrefix;
			currentStatementSuffix = previousSuffix;
		}
		currentCleanupScopes.RemoveAt(currentCleanupScopes.Count - 1);
		if (cleanupScope.PreludeStatements.Count > 0)
			statements.InsertRange(0, cleanupScope.PreludeStatements);
		if (cleanupScope.ExitLabelName is not null)
			statements.Add(new LabelStatement { Name = cleanupScope.ExitLabelName, ResolvedType = "void" });
		for (int i = cleanupScope.Statements.Count - 1; i >= 0; i--)
			statements.Add(CloneStatementForCleanup(cleanupScope.Statements[i]));
		if (cleanupScope.ReturnTarget is not null)
			statements.Add(new ReturnStatement
			{
				ResolvedType = "void",
				Expression = cleanupScope.ReturnType == "void" ? null : CreateVariableReference(cleanupScope.ReturnTarget, cleanupScope.ReturnType)
			});
	}

	static BlockStatement CreateBlock(List<Statement> statements)
	{
		BlockStatement block = new() { ResolvedType = "void" };
		block.Statements.AddRange(statements);
		return block;
	}

	bool TryRewriteNewDeclaration(DeclarationStatement declaration, out List<Statement> statements)
	{
		statements = [];
		if (declaration.InitialValue is not ConstructionExpression { Kind: ConstructionKind.New } construction || declaration.Target.Names.Count != 1)
			return false;

		for (int i = 0; i < construction.Arguments.Count; i++)
			construction.Arguments[i] = LowerArgument(construction.Arguments[i]);
		construction.ElementCount = LowerExpression(construction.ElementCount);
		LowerInitializer(construction.Initializer);

		if (construction.ElementCount is not null || construction.Type is null)
			return false;

		string typeName = BaseConstructedType(construction.Type.ResolvedType ?? construction.ResolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return false;

		if (FindExternalCreateMethod(definition, construction.Arguments.Count) is FunctionDefinition create)
		{
			declaration.InitialValue = CreateCreateCall(create, construction.Type, construction.Arguments, construction.SourceSyntax ?? declaration.SourceSyntax, declaration.Target.ResolvedType ?? construction.ResolvedType);
			statements.Add(declaration);
			return true;
		}
		if (definition is ClassDefinition { Extern: not null } && FindExternInitNewMethod(definition, construction.Arguments.Count) is FunctionDefinition externInitNew)
		{
			declaration.InitialValue = CreateCreateCall(CreateExternalCreateMethod(definition, externInitNew), construction.Type, construction.Arguments, construction.SourceSyntax ?? declaration.SourceSyntax, declaration.Target.ResolvedType ?? construction.ResolvedType);
			statements.Add(declaration);
			return true;
		}

		FunctionDefinition? initNew = FindInitNewMethod(definition, construction.Arguments.Count);
		declaration.InitialValue = CreateAllocCall(construction.Type ?? TypeReferenceFor(definition), construction.SourceSyntax ?? declaration.SourceSyntax);
		statements.Add(declaration);

		Expression target = CreateVariableReference(declaration.Target, declaration.Target.ResolvedType ?? construction.ResolvedType ?? $"{typeName}*");
		if (CreateVirtualTableAssignment(target, definition) is Expression vtableAssignment)
		{
			statements.Add(new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = vtableAssignment
			});
		}
		if (initNew is null)
			return true;

		statements.Add(new IfStatement
		{
			SourceSyntax = construction.SourceSyntax,
			ResolvedType = "void",
			Condition = new BinaryExpression
			{
				Left = target,
				Operator = BinaryOperator.NotEqual,
				Right = new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "#NULL" },
				ResolvedType = "bool"
			},
			Body = new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = CreateInitNewCall(target, initNew, construction.Arguments, construction.SourceSyntax, constructedType: construction.Type)
			}
		});
		return true;
	}

	bool TryRewriteArrayNewPointerDeclaration(DeclarationStatement declaration, out List<Statement> statements)
	{
		statements = [];
		if (declaration.Target.Names.Count != 1
			|| TryGetPointerElementType(declaration.Target.ResolvedType ?? declaration.Target.Type?.ResolvedType) is null
			|| !TryUnwrapArrayNewDeclarationValue(declaration.InitialValue, out ConstructionExpression? construction, out Expression? allocator, out bool finallyDelete))
			return false;

		for (int i = 0; i < construction.Arguments.Count; i++)
			construction.Arguments[i] = LowerArgument(construction.Arguments[i]);
		construction.ElementCount = LowerExpression(construction.ElementCount);
		LowerInitializer(construction.Initializer);
		if (construction.ElementCount is null || construction.Type is null)
			return false;

		Expression? allocationAllocator = null;
		if (allocator is not null)
		{
			Expression? loweredAllocator = LowerExpression(allocator);
			if (loweredAllocator is not null)
			{
				DeclarationStatement allocatorLocal = CreateWithinContextLocal(loweredAllocator, allocator.SourceSyntax ?? declaration.SourceSyntax);
				statements.Add(allocatorLocal);
				allocationAllocator = CreateVariableReference(allocatorLocal.Target, allocatorLocal.Target.ResolvedType ?? loweredAllocator.ResolvedType ?? ErrorType);
			}
		}
		else
		{
			allocationAllocator = CurrentAllocator();
		}

		declaration.InitialValue = CreateAllocCall(construction.Type, allocationAllocator, construction.SourceSyntax ?? declaration.SourceSyntax, construction.ElementCount);
		statements.Add(declaration);

		if (finallyDelete && currentCleanupScopes.Count > 0)
		{
			CleanupScope cleanupScope = currentCleanupScopes[^1];
			DeclarationTarget activeTarget = CreateCleanupActiveFlag(cleanupScope);
			statements.Add(CreateAssignmentStatement(
				CreateVariableReference(activeTarget, "bool"),
				BoolLiteral(true),
				"bool"));
			Expression target = CreateVariableReference(declaration.Target, declaration.Target.ResolvedType ?? declaration.Target.Type?.ResolvedType ?? ErrorType);
			ExpressionStatement cleanup = new()
			{
				SourceSyntax = declaration.SourceSyntax,
				ResolvedType = "void",
				Expression = CreateFreeCall(target, allocationAllocator)
			};
			cleanupScope.Statements.Add(CreateGuardedCleanup(activeTarget, cleanup));
		}
		return true;
	}

	static bool TryUnwrapArrayNewDeclarationValue(Expression? value, out ConstructionExpression construction, out Expression? allocator, out bool finallyDelete)
	{
		allocator = null;
		finallyDelete = false;
		if (value is FinallyDeleteExpression { Expression: not null } finallyDeleteExpression)
		{
			finallyDelete = true;
			value = finallyDeleteExpression.Expression;
		}
		if (value is WithinExpression { Expression: not null } within)
		{
			allocator = within.Context;
			value = within.Expression;
		}
		if (value is ConstructionExpression { Kind: ConstructionKind.New, ElementCount: not null, Type: not null } arrayConstruction)
		{
			construction = arrayConstruction;
			return true;
		}
		construction = null!;
		return false;
	}

	bool TryRewriteInitDeclaration(DeclarationStatement declaration, out List<Statement> statements)
	{
		statements = [];
		bool finallyDelete = false;
		Expression? initialValue = declaration.InitialValue;
		if (initialValue is FinallyDeleteExpression { Expression: ConstructionExpression finallyDeleteConstruction } finallyDeleteExpression
			&& finallyDeleteConstruction.Kind == ConstructionKind.Init)
		{
			finallyDelete = true;
			initialValue = finallyDeleteConstruction;
			finallyDeleteExpression.Expression = finallyDeleteConstruction;
		}

		if (initialValue is not ConstructionExpression { Kind: ConstructionKind.Init } construction || declaration.Target.Names.Count != 1)
			return false;

		for (int i = 0; i < construction.Arguments.Count; i++)
			construction.Arguments[i] = LowerArgument(construction.Arguments[i]);
		LowerInitializer(construction.Initializer);

		TypeDefinition? constructedDefinition = null;
		string constructedTypeName = construction.Type?.ResolvedType ?? BaseConstructedType(construction.ResolvedType);
		if (!string.IsNullOrWhiteSpace(constructedTypeName))
			typeDefinitions.TryGetValue(constructedTypeName, out constructedDefinition);

		Expression target = CreateVariableReference(declaration.Target, declaration.Target.ResolvedType ?? construction.ResolvedType ?? ErrorType);
		CallExpression? initCall = CreateInitCallForConstruction(construction, target);
		if (constructedDefinition is null
			&& initCall?.Target is MemberReferenceExpression { Member: FunctionDefinition initFunction })
			constructedDefinition = FindContainingType(initFunction);
		if (constructedDefinition is null
			&& initCall?.Target is MethodReferenceExpression { Candidates.Count: > 0 } initReference)
			constructedDefinition = FindContainingType(initReference.Candidates[0]);
		if (constructedDefinition is null
			&& typeDefinitions.TryGetValue(BaseConstructedType(declaration.Target.ResolvedType), out TypeDefinition? targetDefinition))
			constructedDefinition = targetDefinition;
		if (constructedDefinition is null
			&& typeDefinitions.TryGetValue(BaseConstructedType(declaration.Target.Type?.ResolvedType), out TypeDefinition? targetTypeDefinition))
			constructedDefinition = targetTypeDefinition;
		declaration.InitialValue = construction.Initializer;

		if (initCall is null)
			return false;

		statements.Add(declaration);
		if (typeDefinitions.TryGetValue(BaseConstructedType(target.ResolvedType), out TypeDefinition? definition)
			&& CreateVirtualTableAssignment(target, definition) is Expression vtableAssignment)
		{
			statements.Add(new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = vtableAssignment
			});
		}
		statements.Add(new ExpressionStatement
		{
			SourceSyntax = construction.SourceSyntax,
			ResolvedType = "void",
			Expression = initCall
		});
		if (finallyDelete && currentCleanupScopes.Count > 0)
		{
			CleanupScope cleanupScope = currentCleanupScopes[^1];
			DeclarationTarget activeTarget = CreateCleanupActiveFlag(cleanupScope);
			statements.Add(CreateAssignmentStatement(
				CreateVariableReference(activeTarget, "bool"),
				BoolLiteral(true),
				"bool"));
			FunctionDefinition? opDelete = constructedDefinition is null ? null : FindDeleteMethod(constructedDefinition);
			if (opDelete is null && constructedDefinition is not null)
				opDelete = FindCallableDeleteMethod(constructedDefinition, declaration.SourceSyntax);
			ExpressionStatement cleanup = new()
			{
				SourceSyntax = declaration.SourceSyntax,
				ResolvedType = "void",
				Expression = CreateDeleteExpression(
					CreateVariableReference(declaration.Target, declaration.Target.ResolvedType ?? construction.ResolvedType ?? ErrorType),
					opDelete,
					deallocate: false)
			};
			cleanupScope.Statements.Add(CreateGuardedCleanup(activeTarget, cleanup));
		}
		return true;
	}

}
