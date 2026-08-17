using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	Statement RewriteTryStatement(TryStatement tryStatement)
	{
		List<Statement> statements = [];
		List<ThrowHandler> handlers = [];
		string doneLabel = NewGeneratedLabelName("try_done");
		string finallyLabel = NewGeneratedLabelName("try_finally");

		foreach (CatchStatement catchStatement in tryStatement.Catches)
		{
			string label = NewGeneratedLabelName("catch");
			DeclarationStatement caught = new() { ResolvedType = "void" };
			caught.Target.Type = CloneType(catchStatement.Target.Type);
			caught.Target.ResolvedType = catchStatement.Target.ResolvedType;
			foreach (string name in catchStatement.Target.Names)
				caught.Target.Names.Add(name);
			statements.Add(caught);
			handlers.Add(new ThrowHandler(catchStatement.Target.ResolvedType ?? ErrorType, caught.Target, label));
		}

		List<ThrowHandler> previousHandlers = [.. currentThrowHandlers];
		currentThrowHandlers.InsertRange(0, handlers);
		bool needsFinallyLabel = false;
		bool needsDoneLabel = false;
		List<Statement> finallyCleanups = [];
		if (tryStatement.Finally?.Body is not null)
			finallyCleanups.Add(RewriteStatement(tryStatement.Finally.Body));
		CleanupScope tryCleanupScope = new(finallyCleanups, RunBeforeCatch: false);
		if (finallyCleanups.Count > 0 && currentFunctionReturnType != "void")
		{
			DeclarationStatement returnLocal = CreateGeneratedLocal(NewGeneratedLocalName("return"), currentFunctionReturnType, new NamedTypeReference { Name = currentFunctionReturnType, ResolvedType = currentFunctionReturnType }, new DefaultExpression { ResolvedType = currentFunctionReturnType });
			statements.Add(returnLocal);
			tryCleanupScope.ReturnTarget = returnLocal.Target;
			tryCleanupScope.ReturnType = currentFunctionReturnType;
		}
		currentCleanupScopes.Add(tryCleanupScope);

		if (tryStatement.Body is not null)
			statements.Add(RewriteStatement(tryStatement.Body));
		if (!EndsWithTransfer(statements))
		{
			string targetLabel = tryStatement.Finally is null ? doneLabel : finallyLabel;
			statements.Add(new GotoStatement { TargetName = targetLabel, ResolvedType = "void" });
			needsDoneLabel |= tryStatement.Finally is null;
			needsFinallyLabel |= tryStatement.Finally is not null;
		}
		currentCleanupScopes.RemoveAt(currentCleanupScopes.Count - 1);
		currentThrowHandlers.Clear();
		currentThrowHandlers.AddRange(previousHandlers);

		for (int i = 0; i < tryStatement.Catches.Count; i++)
		{
			CatchStatement catchStatement = tryStatement.Catches[i];
			ThrowHandler handler = handlers[i];
			statements.Add(new LabelStatement { Name = handler.LabelName, ResolvedType = "void" });

			List<ThrowHandler> catchPreviousHandlers = [.. currentThrowHandlers];
			currentThrowHandlers.AddRange(previousHandlers);
			if (finallyCleanups.Count > 0)
				currentCleanupScopes.Add(tryCleanupScope);
			if (catchStatement.Body is not null)
				statements.Add(RewriteStatement(catchStatement.Body));
			if (finallyCleanups.Count > 0)
				currentCleanupScopes.RemoveAt(currentCleanupScopes.Count - 1);
			currentThrowHandlers.Clear();
			currentThrowHandlers.AddRange(catchPreviousHandlers);
			if (!EndsWithTransfer(statements))
			{
				string targetLabel = tryStatement.Finally is null ? doneLabel : finallyLabel;
				statements.Add(new GotoStatement { TargetName = targetLabel, ResolvedType = "void" });
				needsDoneLabel |= tryStatement.Finally is null;
				needsFinallyLabel |= tryStatement.Finally is not null;
			}
		}

		AppendCleanupScopeExit(statements, tryCleanupScope);
		if (tryStatement.Finally is not null && needsFinallyLabel)
		{
			statements.Add(new LabelStatement { Name = finallyLabel, ResolvedType = "void" });
			foreach (Statement cleanup in finallyCleanups)
				statements.Add(CloneStatementForCleanup(cleanup));
			needsDoneLabel = true;
		}
		if (needsDoneLabel)
			statements.Add(new LabelStatement { Name = doneLabel, ResolvedType = "void" });
		return CreateBlock(statements);
	}

	static bool EndsWithTransfer(List<Statement> statements)
	{
		for (int i = statements.Count - 1; i >= 0; i--)
		{
			if (statements[i] is LabelStatement)
				continue;
			return IsTransferStatement(statements[i]);
		}
		return false;
	}

	static bool IsTransferStatement(Statement statement)
	{
		return statement switch
		{
			ReturnStatement or GotoStatement or BreakStatement or ContinueStatement => true,
			BlockStatement block => EndsWithTransfer(block.Statements),
			_ => false
		};
	}

	void AppendCleanupScopeExit(List<Statement> statements, CleanupScope cleanupScope)
	{
		if (cleanupScope.PreludeStatements.Count > 0)
			statements.InsertRange(0, cleanupScope.PreludeStatements);
		if (cleanupScope.ExitLabelName is null)
			return;
		statements.Add(new LabelStatement { Name = cleanupScope.ExitLabelName, ResolvedType = "void" });
		foreach (Statement cleanup in cleanupScope.Statements)
			statements.Add(CloneStatementForCleanup(cleanup));
		if (cleanupScope.ReturnTarget is not null)
		{
			statements.Add(new ReturnStatement
			{
				ResolvedType = "void",
				Expression = cleanupScope.ReturnType == "void" ? null : CreateVariableReference(cleanupScope.ReturnTarget, cleanupScope.ReturnType)
			});
		}
		cleanupScope.ExitLabelName = null;
		cleanupScope.ReturnTarget = null;
		cleanupScope.ReturnType = "void";
	}

	Expression? RewriteFinallyCleanupExpression(FinallyCleanupExpression finallyCleanup)
	{
		if (finallyCleanup.Kind == FinallyCleanupKind.Delete
			&& finallyCleanup.Expression is CallExpression call
			&& currentStatementPrefix is not null
			&& currentCleanupScopes.Count > 0
			&& TryCreateExpandedReturnCallComponents(call, out List<Expression> components)
			&& components.Count > 0)
		{
			Expression expandedReference = components[0];
			GroupedExpression grouped = new()
			{
				SourceSyntax = finallyCleanup.SourceSyntax,
				ResolvedType = finallyCleanup.ResolvedType
			};
			List<Expression> clonedComponents = [];
			foreach (Expression component in components)
			{
				Expression cloned = CloneParamsExpansionExpression(component) ?? component;
				clonedComponents.Add(cloned);
				grouped.Items.Add(new GroupedExpressionItem
				{
					SourceSyntax = cloned.SourceSyntax,
					Expression = cloned,
					ResolvedType = cloned.ResolvedType
				});
			}
			expressionRewrites[expandedReference] = grouped;
			currentCleanupScopes[^1].Statements.Add(new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = RewriteDeleteExpression(clonedComponents[0])
			});
			return expandedReference;
		}

		Expression? value = LowerExpression(finallyCleanup.Expression);
		if (value is null || currentStatementPrefix is null || currentCleanupScopes.Count == 0)
			return value;

		string valueType = value.ResolvedType ?? finallyCleanup.ResolvedType ?? ErrorType;
		CleanupScope cleanupScope = currentCleanupScopes[^1];
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("finally"), valueType, new NamedTypeReference { Name = valueType, ResolvedType = valueType }, new DefaultExpression { ResolvedType = valueType });
		cleanupScope.PreludeStatements.Add(local);
		currentStatementPrefix.Add(CreateAssignmentStatement(
			CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType),
			value,
			valueType));
		DeclarationTarget activeTarget = CreateCleanupActiveFlag(cleanupScope);
		currentStatementPrefix.Add(CreateAssignmentStatement(
			CreateVariableReference(activeTarget, "bool"),
			BoolLiteral(true),
			"bool"));
		Expression reference = CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType);
		ExpressionStatement cleanup = new()
		{
			ResolvedType = "void",
			Expression = finallyCleanup.Kind == FinallyCleanupKind.Delete
				? RewriteDeleteExpression(CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType, finallyCleanup.SourceSyntax ?? finallyCleanup.Expression?.SourceSyntax))
				: null
		};
		Statement cleanupStatement = finallyCleanup.Kind == FinallyCleanupKind.Delete
			? cleanup
			: CreateFinallyMethodCleanupStatement(finallyCleanup, local.Target, valueType);
		cleanupScope.Statements.Add(CreateGuardedCleanup(activeTarget, cleanupStatement));
		return reference;
	}

	Statement CreateFinallyMethodCleanupStatement(FinallyCleanupExpression finallyCleanup, DeclarationTarget target, string valueType)
	{
		if (finallyCleanup.CleanupFunction is not FunctionDefinition function || finallyCleanup.CleanupCall is not CallExpression analyzedCall)
			return new ExpressionStatement { ResolvedType = "void", Expression = new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "void" } };

		Expression receiver = CreateVariableReference(target, target.ResolvedType ?? valueType);
		MemberReferenceExpression member = new()
		{
			SourceSyntax = finallyCleanup.SourceSyntax,
			Target = receiver,
			Name = function.Name,
			NameRange = finallyCleanup.MethodNameRange,
			Member = function,
			ResolvedType = function.ResolvedType ?? ErrorType
		};
		member.Candidates.Add(function);
		CallExpression cleanupCall = new()
		{
			SourceSyntax = finallyCleanup.SourceSyntax,
			ResolvedType = "void",
			Target = member
		};
		foreach (ArgumentExpression argument in analyzedCall.Arguments)
			cleanupCall.Arguments.Add(argument);
		callTargets[cleanupCall] = function;
		if (callGenericSubstitutions.TryGetValue(analyzedCall, out Dictionary<string, string>? substitutions))
			callGenericSubstitutions[cleanupCall] = new Dictionary<string, string>(substitutions, StringComparer.Ordinal);

		List<Statement>? previousStatementPrefix = currentStatementPrefix;
		List<Statement> cleanupPrefix = [];
		currentStatementPrefix = cleanupPrefix;
		Expression? lowered = LowerExpression(cleanupCall);
		currentStatementPrefix = previousStatementPrefix;
		cleanupPrefix.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = lowered ?? cleanupCall
		});
		return CreateBlock(cleanupPrefix);
	}

	DeclarationTarget CreateCleanupActiveFlag(CleanupScope cleanupScope)
	{
		DeclarationStatement active = CreateGeneratedLocal(NewGeneratedLocalName("cleanupActive"), "bool", new NamedTypeReference { Name = "bool", ResolvedType = "bool" }, BoolLiteral(false));
		cleanupScope.PreludeStatements.Add(active);
		return active.Target;
	}

	Statement CreateGuardedCleanup(DeclarationTarget activeTarget, Statement cleanup)
	{
		return new IfStatement
		{
			ResolvedType = "void",
			Condition = CreateVariableReference(activeTarget, "bool"),
			Body = CreateBlock([
				cleanup,
				CreateAssignmentStatement(
					CreateVariableReference(activeTarget, "bool"),
					BoolLiteral(false),
					"bool")
			])
		};
	}

	static ExpressionStatement CreateAssignmentStatement(Expression target, Expression? value, string resolvedType)
	{
		return new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				Target = target,
				Operator = AssignmentOperator.Assign,
				Value = value,
				ResolvedType = resolvedType
			}
		};
	}

	static LiteralExpression BoolLiteral(bool value)
	{
		return new LiteralExpression
		{
			Kind = value ? LiteralKind.True : LiteralKind.False,
			Text = value ? "true" : "false",
			Value = value,
			ResolvedType = "bool"
		};
	}

	Statement WithPendingCleanups(Statement transfer)
	{
		if (transfer is BreakStatement && TryGetCurrentBreakTarget(out string? breakLabel, out int breakCleanupScopeStart))
			return CreateCleanupGotoTransfer(breakLabel, includeContinueCleanups: true, breakCleanupScopeStart);

		if (transfer is ContinueStatement && TryGetCurrentContinueTarget(out string? continueLabel, out int continueCleanupScopeStart))
			return CreateCleanupGotoTransfer(continueLabel, includeContinueCleanups: false, continueCleanupScopeStart);

		CleanupScope? exitScope = GetCleanupExitScope();
		if (exitScope is null || transfer is GotoStatement)
			return transfer;

		if (transfer is ReturnStatement returnStatement)
		{
			if (returnStatement.SkipPendingCleanups)
				return returnStatement;
			exitScope.ExitLabelName ??= NewGeneratedLabelName("cleanup");
			return CreateCleanupReturnTransfer(returnStatement, exitScope);
		}
		return transfer;
	}

	Statement CreateCleanupGotoTransfer(string targetLabel, bool includeContinueCleanups, int cleanupScopeStart)
	{
		List<Statement> statements = GetPendingCleanups(includeCatchExitCleanups: true, includeContinueCleanups, cleanupScopeStart);
		statements.Add(new GotoStatement { TargetName = targetLabel, ResolvedType = "void" });
		return statements.Count == 1 ? statements[0] : CreateBlock(statements);
	}

	bool TryGetCurrentBreakTarget(out string label, out int cleanupScopeStart)
	{
		for (int i = currentLoopTransferTargets.Count - 1; i >= 0; i--)
			if (currentLoopTransferTargets[i].BreakLabelName is string breakLabel)
			{
				label = breakLabel;
				cleanupScopeStart = currentLoopTransferTargets[i].CleanupScopeStart;
				return true;
			}

		label = "";
		cleanupScopeStart = 0;
		return false;
	}

	bool TryGetCurrentContinueTarget(out string label, out int cleanupScopeStart)
	{
		for (int i = currentLoopTransferTargets.Count - 1; i >= 0; i--)
			if (currentLoopTransferTargets[i].ContinueLabelName is string continueLabel)
			{
				label = continueLabel;
				cleanupScopeStart = currentLoopTransferTargets[i].CleanupScopeStart;
				return true;
			}

		label = "";
		cleanupScopeStart = 0;
		return false;
	}

	Statement CreateCleanupReturnTransfer(ReturnStatement returnStatement, CleanupScope exitScope)
	{
		string returnType = currentRewriteFunction?.ResolvedType ?? "void";
		if (returnType == "void")
			return new GotoStatement { TargetName = exitScope.ExitLabelName, ResolvedType = "void" };

		exitScope.ReturnType = returnType;
		if (exitScope.ReturnTarget is null)
		{
			DeclarationStatement returnLocal = CreateGeneratedLocal(NewGeneratedLocalName("return"), returnType, new NamedTypeReference { Name = returnType, ResolvedType = returnType }, new DefaultExpression { ResolvedType = returnType });
			currentStatementPrefix?.Add(returnLocal);
			exitScope.ReturnTarget = returnLocal.Target;
		}

		return CreateBlock(
		[
			new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					Target = CreateVariableReference(exitScope.ReturnTarget, returnType),
					Operator = AssignmentOperator.Assign,
					Value = returnStatement.Expression ?? new DefaultExpression { ResolvedType = returnType },
					ResolvedType = returnType
				}
			},
			new GotoStatement { TargetName = exitScope.ExitLabelName, ResolvedType = "void" }
		]);
	}

	CleanupScope? GetCleanupExitScope()
	{
		for (int i = currentCleanupScopes.Count - 1; i >= 0; i--)
		{
			if (currentCleanupScopes[i].Statements.Count > 0)
				return currentCleanupScopes[i];
		}
		return null;
	}

	Statement RewriteWhileStatementWithThrowingCondition(WhileStatement whileStatement)
	{
		Expression condition = whileStatement.Condition!;
		List<Statement>? previousPrefix = currentStatementPrefix;
		List<Statement>? previousSuffix = currentStatementSuffix;
		List<Statement> conditionStatements = [];
		currentStatementPrefix = conditionStatements;
		currentStatementSuffix = [];
		Expression loweredCondition = HoistThrowingExpression(condition);
		conditionStatements.AddRange(currentStatementSuffix);
		currentStatementPrefix = previousPrefix;
		currentStatementSuffix = previousSuffix;

		BlockStatement body = new() { ResolvedType = "void" };
		body.Statements.AddRange(conditionStatements);
		body.Statements.Add(new IfStatement
		{
			ResolvedType = "void",
			Condition = new UnaryExpression
			{
				Operator = UnaryOperator.LogicalNot,
				Operand = loweredCondition,
				ResolvedType = "bool"
			},
			Body = new BreakStatement { ResolvedType = "void" }
		});
		if (whileStatement.Body is not null)
			body.Statements.Add(whileStatement.Body);

		whileStatement.Condition = new LiteralExpression { Kind = LiteralKind.True, Text = "true", Value = true, ResolvedType = "bool" };
		string continueLabel = NewGeneratedLabelName("while_continue");
		string breakLabel = NewGeneratedLabelName("while_break");
		whileStatement.Body = RewriteLoopBody(body, continueLabel, breakLabel);
		return WrapLoopWithBreakLabel(whileStatement, breakLabel);
	}

	List<Statement> GetPendingCleanups()
	{
		return GetPendingCleanups(includeCatchExitCleanups: true);
	}

	List<Statement> GetPendingCleanups(bool includeCatchExitCleanups)
	{
		return GetPendingCleanups(includeCatchExitCleanups, includeContinueCleanups: true);
	}

	List<Statement> GetPendingCleanups(bool includeCatchExitCleanups, bool includeContinueCleanups)
	{
		return GetPendingCleanups(includeCatchExitCleanups, includeContinueCleanups, cleanupScopeStart: 0);
	}

	List<Statement> GetPendingCleanups(bool includeCatchExitCleanups, bool includeContinueCleanups, int cleanupScopeStart)
	{
		List<Statement> cleanups = [];
		int start = Math.Clamp(cleanupScopeStart, 0, currentCleanupScopes.Count);
		for (int i = currentCleanupScopes.Count - 1; i >= start; i--)
		{
			CleanupScope cleanupScope = currentCleanupScopes[i];
			if (!includeCatchExitCleanups && !cleanupScope.RunBeforeCatch)
				continue;
			if (!includeContinueCleanups && !cleanupScope.RunBeforeContinue)
				continue;
			List<Statement> scope = cleanupScope.Statements;
			for (int j = scope.Count - 1; j >= 0; j--)
				cleanups.Add(CloneStatementForCleanup(scope[j]));
		}
		return cleanups;
	}

	Statement CloneStatementForCleanup(Statement statement)
	{
		return statement;
	}

	void LowerThrowingArguments(CallExpression call)
	{
		if (currentStatementPrefix is null)
			return;
		for (int i = 0; i < call.Arguments.Count; i++)
		{
			ArgumentExpression argument = call.Arguments[i];
			if (argument.Value is null || !ContainsUncaughtThrow(argument.Value))
				continue;

			string thrownType = GetExpressionThrownType(argument.Value) ?? ErrorType;
			DeclarationTarget errorTarget;
			if (TryGetImplicitHandlerTarget(thrownType, out DeclarationTarget? handlerTarget))
			{
				errorTarget = handlerTarget;
			}
			else if (TryGetFunctionThrownTarget(thrownType, out DeclarationTarget? thrownTarget))
			{
				errorTarget = thrownTarget;
			}
			else
			{
				DeclarationStatement errorLocal = CreateErrorLocal(thrownType);
				currentStatementPrefix.Add(errorLocal);
				errorTarget = errorLocal.Target;
			}
			DeclarationTarget? previousCatch = currentImplicitCatchTarget;
			currentImplicitCatchTarget = errorTarget;
			Expression? value = LowerExpressionForThrowCapture(argument.Value, errorTarget);
			currentImplicitCatchTarget = previousCatch;

			string localName = NewGeneratedLocalName("arg");
			DeclarationStatement local = CreateGeneratedLocal(localName, value?.ResolvedType ?? argument.ResolvedType ?? ErrorType, new NamedTypeReference { Name = value?.ResolvedType ?? argument.ResolvedType ?? ErrorType, ResolvedType = value?.ResolvedType ?? argument.ResolvedType ?? ErrorType }, value);
			currentStatementPrefix.Add(local);
			currentStatementPrefix.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
			argument.Value = CreateVariableReference(local.Target, local.Target.ResolvedType ?? ErrorType);
			argument.ResolvedType = argument.Value.ResolvedType;
		}
	}

	Expression HoistThrowingExpression(Expression expression)
	{
		if (currentStatementPrefix is null)
			return LowerExpression(expression) ?? expression;

		string thrownType = GetExpressionThrownType(expression) ?? ErrorType;
		DeclarationTarget errorTarget;
		if (TryGetImplicitHandlerTarget(thrownType, out DeclarationTarget? handlerTarget))
		{
			errorTarget = handlerTarget;
		}
		else if (TryGetFunctionThrownTarget(thrownType, out DeclarationTarget? thrownTarget))
		{
			errorTarget = thrownTarget;
		}
		else
		{
			DeclarationStatement errorLocal = CreateErrorLocal(thrownType);
			currentStatementPrefix.Add(errorLocal);
			errorTarget = errorLocal.Target;
		}
		DeclarationTarget? previousCatch = currentImplicitCatchTarget;
		currentImplicitCatchTarget = errorTarget;
		Expression? value = LowerExpressionForThrowCapture(expression, errorTarget);
		currentImplicitCatchTarget = previousCatch;

		string localName = NewGeneratedLocalName("value");
		string valueType = value?.ResolvedType ?? expression.ResolvedType ?? ErrorType;
		DeclarationStatement local = CreateGeneratedLocal(localName, valueType, new NamedTypeReference { Name = valueType, ResolvedType = valueType }, value);
		currentStatementPrefix.Add(local);
		currentStatementPrefix.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
		return CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType);
	}

	bool TryGetFunctionThrownTarget(string errorType, out DeclarationTarget target)
	{
		if (currentThrowHandlers.Count == 0 && GetFunctionThrownParameter(currentRewriteFunction) is ParameterDefinition thrownParameter && CanImplicitlyConvert(errorType, thrownParameter.ResolvedType ?? errorType))
		{
			target = new DeclarationTarget
			{
				ResolvedType = thrownParameter.ResolvedType ?? errorType
			};
			target.Names.Add(thrownParameter.Name ?? "error");
			return true;
		}

		target = new DeclarationTarget { ResolvedType = errorType };
		return false;
	}

	bool TryGetImplicitHandlerTarget(string errorType, out DeclarationTarget target)
	{
		foreach (ThrowHandler handler in currentThrowHandlers)
		{
			if (CanImplicitlyConvert(errorType, handler.ErrorType))
			{
				target = handler.ErrorTarget;
				return true;
			}
		}

		target = new DeclarationTarget { ResolvedType = errorType };
		return false;
	}

	Expression? LowerExpressionForThrowCapture(Expression? expression, DeclarationTarget errorTarget)
	{
		if (expression is null)
			return null;

		switch (expression)
		{
			case BinaryExpression { Operator: BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr } binary:
				return LowerShortCircuitExpressionForThrowCapture(binary, errorTarget);

			case BinaryExpression binary:
				if (CountUncaughtThrows(binary) <= 1)
					return LowerExpression(expression);
				binary.Left = LowerOperandForThrowCapture(binary.Left, errorTarget);
				binary.Right = LowerOperandForThrowCapture(binary.Right, errorTarget);
				return expression;

			case ConditionalExpression conditional:
				return LowerConditionalExpressionForThrowCapture(conditional, errorTarget);

			case ParenthesizedExpression parenthesized:
				parenthesized.Expression = LowerExpressionForThrowCapture(parenthesized.Expression, errorTarget);
				return LowerExpression(expression);

			case UnaryExpression unary when unary.Operator != UnaryOperator.Throw:
				unary.Operand = LowerOperandForThrowCapture(unary.Operand, errorTarget);
				return LowerExpression(expression);

			default:
				return LowerExpression(expression);
		}
	}

	Expression LowerShortCircuitExpressionForThrowCapture(BinaryExpression binary, DeclarationTarget errorTarget)
	{
		string valueType = binary.ResolvedType ?? "bool";
		Expression? left = LowerExpressionForThrowCapture(binary.Left, errorTarget);
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("value"), valueType, new NamedTypeReference { Name = valueType, ResolvedType = valueType }, left);
		currentStatementPrefix?.Add(local);
		currentStatementPrefix?.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));

		Expression localReference = CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType);
		List<Statement> branchStatements = [];
		List<Statement>? previousPrefix = currentStatementPrefix;
		List<Statement>? previousSuffix = currentStatementSuffix;
		currentStatementPrefix = branchStatements;
		currentStatementSuffix = [];
		Expression? right = LowerExpressionForThrowCapture(binary.Right, errorTarget);
		branchStatements.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				Target = CreateVariableReference(local.Target, valueType),
				Operator = AssignmentOperator.Assign,
				Value = right,
				ResolvedType = valueType
			}
		});
		branchStatements.AddRange(currentStatementSuffix);
		branchStatements.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
		currentStatementPrefix = previousPrefix;
		currentStatementSuffix = previousSuffix;

		currentStatementPrefix?.Add(new IfStatement
		{
			ResolvedType = "void",
			Condition = binary.Operator == BinaryOperator.LogicalAnd
				? CreateVariableReference(local.Target, valueType)
				: new UnaryExpression
				{
					Operator = UnaryOperator.LogicalNot,
					Operand = CreateVariableReference(local.Target, valueType),
					ResolvedType = "bool"
				},
			Body = CreateBlock(branchStatements)
		});
		return localReference;
	}

	Expression LowerConditionalExpressionForThrowCapture(ConditionalExpression conditional, DeclarationTarget errorTarget)
	{
		string valueType = conditional.ResolvedType ?? ErrorType;
		Expression? condition = LowerExpressionForThrowCapture(conditional.Condition, errorTarget);
		currentStatementPrefix?.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("value"), valueType, new NamedTypeReference { Name = valueType, ResolvedType = valueType }, new DefaultExpression { ResolvedType = valueType });
		currentStatementPrefix?.Add(local);

		Statement trueBranch = CreateConditionalAssignmentBranch(local.Target, valueType, conditional.WhenTrue, errorTarget);
		Statement falseBranch = CreateConditionalAssignmentBranch(local.Target, valueType, conditional.WhenFalse, errorTarget);
		currentStatementPrefix?.Add(new IfStatement
		{
			ResolvedType = "void",
			Condition = condition,
			Body = trueBranch,
			ElseBody = falseBranch
		});
		return CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType);
	}

	Statement CreateConditionalAssignmentBranch(DeclarationTarget target, string valueType, Expression? value, DeclarationTarget errorTarget)
	{
		List<Statement> statements = [];
		List<Statement>? previousPrefix = currentStatementPrefix;
		List<Statement>? previousSuffix = currentStatementSuffix;
		currentStatementPrefix = statements;
		currentStatementSuffix = [];
		Expression? lowered = LowerExpressionForThrowCapture(value, errorTarget);
		statements.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				Target = CreateVariableReference(target, valueType),
				Operator = AssignmentOperator.Assign,
				Value = lowered,
				ResolvedType = valueType
			}
		});
		statements.AddRange(currentStatementSuffix);
		statements.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
		currentStatementPrefix = previousPrefix;
		currentStatementSuffix = previousSuffix;
		return CreateBlock(statements);
	}

	Expression? LowerOperandForThrowCapture(Expression? expression, DeclarationTarget errorTarget)
	{
		if (expression is null)
			return null;
		if (!ContainsUncaughtThrow(expression))
			return LowerExpression(expression);

		DeclarationTarget? previousCatch = currentImplicitCatchTarget;
		currentImplicitCatchTarget = errorTarget;
		Expression? lowered = LowerExpressionForThrowCapture(expression, errorTarget);
		currentImplicitCatchTarget = previousCatch;

		string valueType = lowered?.ResolvedType ?? expression.ResolvedType ?? ErrorType;
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("value"), valueType, new NamedTypeReference { Name = valueType, ResolvedType = valueType }, lowered);
		currentStatementPrefix?.Add(local);
		currentStatementPrefix?.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
		return CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType);
	}

	Expression LowerUncaughtThrowingCall(CallExpression call)
	{
		if (!IsUncaughtThrowingCall(call, out string? thrownType))
			return call;

		FunctionDefinition? function = callTargets.TryGetValue(call, out FunctionDefinition? found) ? found : null;
		bool usesThrownReturn = function is not null && GetFunctionThrownReturnType(function) is not null;

		if (currentImplicitCatchTarget is not null)
		{
			if (!usesThrownReturn)
			{
				InsertImplicitCatchArgument(call, function, new ArgumentExpression
				{
					SourceSyntax = call.SourceSyntax,
					Modifier = ArgumentModifier.Catch,
					Value = CreateVariableReference(currentImplicitCatchTarget, currentImplicitCatchTarget.ResolvedType ?? thrownType ?? ErrorType),
					ResolvedType = currentImplicitCatchTarget.ResolvedType ?? thrownType ?? ErrorType
				});
				return call;
			}

			return new AssignmentExpression
			{
				Target = CreateVariableReference(currentImplicitCatchTarget, currentImplicitCatchTarget.ResolvedType ?? thrownType ?? ErrorType),
				Operator = AssignmentOperator.Assign,
				Value = call,
				ResolvedType = currentImplicitCatchTarget.ResolvedType ?? thrownType ?? ErrorType
			};
		}

		if (currentStatementPrefix is null || currentStatementSuffix is null)
			return call;

		DeclarationTarget errorTarget;
		if (TryGetImplicitHandlerTarget(thrownType ?? ErrorType, out DeclarationTarget? handlerTarget))
		{
			errorTarget = handlerTarget;
		}
		else if (TryGetFunctionThrownTarget(thrownType ?? ErrorType, out DeclarationTarget? thrownTarget))
		{
			errorTarget = thrownTarget;
		}
		else
		{
			DeclarationStatement errorLocal = CreateErrorLocal(thrownType ?? ErrorType);
			currentStatementPrefix.Add(errorLocal);
			errorTarget = errorLocal.Target;
		}
		Expression result = call;
		if (usesThrownReturn)
		{
			result = new AssignmentExpression
			{
				Target = CreateVariableReference(errorTarget, errorTarget.ResolvedType ?? ErrorType),
				Operator = AssignmentOperator.Assign,
				Value = call,
				ResolvedType = errorTarget.ResolvedType ?? ErrorType
			};
		}
		else
		{
			InsertImplicitCatchArgument(call, function, new ArgumentExpression
			{
				SourceSyntax = call.SourceSyntax,
				Modifier = ArgumentModifier.Catch,
				Value = CreateVariableReference(errorTarget, errorTarget.ResolvedType ?? ErrorType),
				ResolvedType = errorTarget.ResolvedType ?? ErrorType
			});
		}
		currentStatementSuffix.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
		return result;
	}

	void InsertImplicitCatchArgument(CallExpression call, FunctionDefinition? function, ArgumentExpression argument)
	{
		int insertIndex = call.Arguments.Count;
		if (function is not null
			&& TryGetExpandedReturnShape(call, function, out ParamsComponentShape shape)
			&& shape.Components.Count > 1)
		{
			insertIndex = System.Math.Max(0, insertIndex - (shape.Components.Count - 1));
		}
		call.Arguments.Insert(insertIndex, argument);
	}

	DeclarationStatement CreateErrorLocal(string errorType)
	{
		string name = NewGeneratedLocalName("error");
		return CreateGeneratedLocal(name, errorType, new NamedTypeReference { Name = errorType, ResolvedType = errorType }, new DefaultExpression { ResolvedType = errorType });
	}

	List<Statement> CreateThrowCheck(DeclarationTarget errorTarget, string errorType)
	{
		Statement transfer = TryGetMatchingHandler(errorTarget, errorType, out ThrowHandler? handler)
			? CreateBlock(CreateHandlerGoto(handler))
			: IsFunctionThrownTarget(errorTarget)
			? CreateFunctionErrorExit()
			: CreateBlock(CreateThrowTransfer(CreateVariableReference(errorTarget, errorType), null));
		return
		[
			new IfStatement
			{
				ResolvedType = "void",
				Condition = new BinaryExpression
				{
					Left = CreateVariableReference(errorTarget, errorType),
					Operator = BinaryOperator.NotEqual,
					Right = new DefaultExpression { ResolvedType = errorType },
					ResolvedType = "bool"
				},
				Body = transfer
			}
		];
	}

	bool IsFunctionThrownTarget(DeclarationTarget target)
	{
		return GetFunctionThrownParameter(currentRewriteFunction) is ParameterDefinition thrownParameter
			&& target.Names.Count == 1
			&& target.Names[0] == (thrownParameter.Name ?? "error");
	}

	Statement CreateFunctionErrorExit()
	{
		if (GetCleanupExitScope() is not null)
			return WithPendingCleanups(CreateDefaultReturn());

		return CreateDefaultReturn();
	}

	bool TryGetMatchingHandler(DeclarationTarget target, string errorType, out ThrowHandler handler)
	{
		foreach (ThrowHandler candidate in currentThrowHandlers)
		{
			if (ReferenceEquals(candidate.ErrorTarget, target) && CanImplicitlyConvert(errorType, candidate.ErrorType))
			{
				handler = candidate;
				return true;
			}
		}

		handler = null!;
		return false;
	}

	List<Statement> CreateHandlerGoto(ThrowHandler handler)
	{
		List<Statement> statements = GetPendingCleanups(includeCatchExitCleanups: false);
		statements.Add(new GotoStatement { TargetName = handler.LabelName, ResolvedType = "void" });
		return statements;
	}

	List<Statement> CreateThrowTransfer(Expression? value, SyntaxNode? syntax)
	{
		value = LowerExpression(value);
		string thrownType = value?.ResolvedType ?? ErrorType;
		foreach (ThrowHandler handler in currentThrowHandlers)
		{
			if (!CanImplicitlyConvert(thrownType, handler.ErrorType))
				continue;
			List<Statement> transfer =
			[
				new ExpressionStatement
				{
					ResolvedType = "void",
					Expression = new AssignmentExpression
					{
						Target = CreateVariableReference(handler.ErrorTarget, handler.ErrorType),
						Operator = AssignmentOperator.Assign,
						Value = value,
						ResolvedType = handler.ErrorType
					}
				}
			];
			transfer.AddRange(GetPendingCleanups(includeCatchExitCleanups: false));
			transfer.Add(new GotoStatement { TargetName = handler.LabelName, ResolvedType = "void" });
			return transfer;
		}

		ParameterDefinition? thrownParameter = GetFunctionThrownParameter(currentRewriteFunction);
		if (thrownParameter is not null)
		{
			return
			[
				new ExpressionStatement
				{
					ResolvedType = "void",
					Expression = new AssignmentExpression
					{
						Target = CreateVariableReference(thrownParameter, thrownParameter.ResolvedType ?? thrownType),
						Operator = AssignmentOperator.Assign,
						Value = value,
						ResolvedType = thrownParameter.ResolvedType ?? thrownType
					}
				},
				WithPendingCleanups(CreateDefaultReturn())
			];
		}

		if (GetFunctionThrownReturnType(currentRewriteFunction) is string thrownReturnType && CanImplicitlyConvert(thrownType, thrownReturnType))
		{
			return
			[
				WithPendingCleanups(new ReturnStatement
				{
					ResolvedType = "void",
					Expression = value
				})
			];
		}

		return [new ExpressionStatement { SourceSyntax = syntax, ResolvedType = "void", Expression = new UnaryExpression { Operator = UnaryOperator.Throw, Operand = value, ResolvedType = "void" } }];
	}

	ReturnStatement CreateDefaultReturn()
	{
		string returnType = currentRewriteFunction?.ResolvedType ?? "void";
		return new ReturnStatement
		{
			ResolvedType = "void",
			Expression = returnType == "void" ? null : new DefaultExpression { ResolvedType = returnType }
		};
	}

	static ParameterDefinition? GetFunctionThrownParameter(FunctionDefinition? function)
	{
		foreach (ParameterDefinition parameter in function?.Parameters ?? [])
		{
			if (parameter.Modifier == ParameterModifier.Thrown)
				return parameter;
		}
		return null;
	}

	static string? GetFunctionThrownReturnType(FunctionDefinition? function)
	{
		string? returnType = function?.ReturnType?.ResolvedType;
		return returnType is not null && returnType.StartsWith("thrown(", StringComparison.Ordinal) && returnType.EndsWith(")", StringComparison.Ordinal)
			? returnType["thrown(".Length..^1]
			: null;
	}

	bool ContainsUncaughtThrow(Expression expression)
	{
		if (expression is UnaryExpression { Operator: UnaryOperator.Await })
			return false;
		if (IsUncaughtThrowingCall(expression, out _))
			return true;
		foreach (Expression child in EnumerateChildExpressions(expression))
		{
			if (ContainsUncaughtThrow(child))
				return true;
		}
		return false;
	}

	string? GetExpressionThrownType(Expression expression)
	{
		if (IsUncaughtThrowingCall(expression, out string? thrownType))
			return thrownType;
		foreach (Expression child in EnumerateChildExpressions(expression))
		{
			if (GetExpressionThrownType(child) is string childThrown)
				return childThrown;
		}
		return null;
	}

	int CountUncaughtThrows(Expression? expression)
	{
		if (expression is null)
			return 0;
		int count = IsUncaughtThrowingCall(expression, out _) ? 1 : 0;
		foreach (Expression child in EnumerateChildExpressions(expression))
			count += CountUncaughtThrows(child);
		return count;
	}

	bool IsUncaughtThrowingCall(Expression expression, out string? thrownType)
	{
		thrownType = null;
		if (expression is not CallExpression call || !callTargets.TryGetValue(call, out FunctionDefinition? function))
			return false;
		thrownType = GetFunctionThrownType(function);
		if (thrownType is null)
			return false;
		foreach (ArgumentExpression argument in call.Arguments)
		{
			if (argument.Modifier == ArgumentModifier.Catch)
				return false;
		}
		return true;
	}

	IEnumerable<Expression> EnumerateChildExpressions(Expression expression)
	{
		switch (expression)
		{
			case ParenthesizedExpression parenthesized when parenthesized.Expression is not null:
				yield return parenthesized.Expression;
				break;
			case CastExpression cast when cast.Expression is not null:
				yield return cast.Expression;
				break;
			case UnaryExpression unary:
				if (unary.Operand is not null)
					yield return unary.Operand;
				break;
			case BinaryExpression binary:
				if (binary.Left is not null)
					yield return binary.Left;
				if (binary.Right is not null)
					yield return binary.Right;
				break;
			case ConditionalExpression conditional:
				if (conditional.Condition is not null)
					yield return conditional.Condition;
				if (conditional.WhenTrue is not null)
					yield return conditional.WhenTrue;
				if (conditional.WhenFalse is not null)
					yield return conditional.WhenFalse;
				break;
			case CallExpression call:
				foreach (ArgumentExpression argument in call.Arguments)
					if (argument.Value is not null)
						yield return argument.Value;
				break;
		}
	}
}
