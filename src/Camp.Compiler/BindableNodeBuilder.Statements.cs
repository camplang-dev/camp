namespace Camp.Compiler;

public sealed partial class BindableNodeBuilder
{
	BlockStatement BuildBlockFunctionBody(BlockMethodBodySyntax syntax)
	{
		BlockStatement body = new() { SourceSyntax = syntax };
		AddStatements(body.Statements, syntax.Statements);
		return body;
	}

	Statement BuildStatement(StatementSyntax syntax)
	{
		switch (syntax)
		{
			case EmptyStatementSyntax empty:
				return new EmptyStatement { SourceSyntax = empty };

			case BlockStatementSyntax block:
				return BuildBlockStatement(block);

			case ExpressionStatementSyntax expression:
				return new ExpressionStatement
				{
					SourceSyntax = expression,
					Expression = BuildExpression(expression.Expression, "Expression statement")
				};

			case DeclarationStatementStatementSyntax declaration:
				return BuildDeclarationStatement(declaration);

			case CaseStatementSyntax caseStatement:
				return new CaseStatement
				{
					SourceSyntax = caseStatement,
					Expression = BuildExpression(caseStatement.Expression, "Case statement")
				};

			case DefaultStatementSyntax defaultStatement:
				return new DefaultStatement { SourceSyntax = defaultStatement };

			case LabelStatementSyntax label:
				return new LabelStatement
				{
					SourceSyntax = label,
					Name = label.Identifier?.Value
				};

			case KeywordStatementSyntax keyword:
				return BuildKeywordStatement(keyword);

			default:
				Report(syntax, "Unsupported statement syntax.");
				return new EmptyStatement { SourceSyntax = syntax };
		}
	}

	BlockStatement BuildBlockStatement(BlockStatementSyntax syntax)
	{
		BlockStatement block = new() { SourceSyntax = syntax };
		AddStatements(block.Statements, syntax.Statements);
		return block;
	}

	void AddStatements(System.Collections.Generic.List<Statement> target, System.Collections.Generic.List<StatementSyntax>? syntaxList)
	{
		foreach (StatementSyntax syntax in syntaxList ?? [])
		{
			if (syntax is KeywordStatementSyntax keyword)
			{
				switch (keyword.Keyword?.Value)
				{
					case "while" when target.Count > 0 && target[^1] is DoWhileStatement { Condition: null } doWhile:
						AttachDoWhileCondition(doWhile, keyword);
						continue;

					case "else":
						AttachElse(target, keyword);
						continue;

					case "catch":
						AttachCatch(target, keyword);
						continue;

					case "finally":
						if (target.Count > 0 && target[^1] is TryStatement)
							AttachFinally(target, keyword);
						else
							target.Add(BuildStatement(keyword));
						continue;
				}
			}

			target.Add(BuildStatement(syntax));
		}
	}

	Statement BuildKeywordStatement(KeywordStatementSyntax syntax)
	{
		return syntax.Keyword?.Value switch
		{
			"if" => BuildIfStatement(syntax),
			"while" => BuildWhileStatement(syntax),
			"do" => BuildDoWhileStatement(syntax),
			"for" => BuildForStatement(syntax),
			"foreach" => BuildForeachStatement(syntax),
			"switch" => BuildSwitchStatement(syntax),
			"within" => BuildWithinStatement(syntax),
			"try" => new TryStatement { SourceSyntax = syntax, Body = BuildStatementBody(syntax) },
			"return" => new ReturnStatement { SourceSyntax = syntax, Expression = BuildStatementExpression(syntax, "Return statement") },
			"yield" => new YieldStatement { SourceSyntax = syntax, Expression = BuildStatementExpression(syntax, "Yield statement") },
			"delete" => new DeleteStatement { SourceSyntax = syntax, Expression = BuildStatementExpression(syntax, "Delete statement") },
			"throw" => new ExpressionStatement { SourceSyntax = syntax, Expression = new UnaryExpression { SourceSyntax = syntax, Operator = UnaryOperator.Throw, Operand = BuildStatementExpression(syntax, "Throw statement") } },
			"goto" => BuildGotoStatement(syntax),
			"break" => BuildBreakStatement(syntax),
			"continue" => BuildContinueStatement(syntax),
			"else" => BuildDetachedFollowerStatement(syntax, "'else' must follow an if statement."),
			"catch" => BuildDetachedFollowerStatement(syntax, "'catch' must follow a try statement."),
			"finally" => new FinallyStatement { SourceSyntax = syntax, Body = BuildStatementBody(syntax) },
			_ => BuildUnknownKeywordStatement(syntax)
		};
	}

	DeclarationStatement BuildDeclarationStatement(DeclarationStatementStatementSyntax syntax)
	{
		DeclarationStatement statement = new() { SourceSyntax = syntax };

		if (syntax.DeclarationStatement is null)
		{
			Report(syntax, "Declaration statement is missing a declaration.");
			return statement;
		}

		statement.Target.SourceSyntax = syntax.DeclarationStatement.Target;
		BuildDeclarationTarget(statement.Target, syntax.DeclarationStatement.Target, "Declaration statement");
		statement.InitialValue = syntax.DeclarationStatement.Assignment is null
			? null
			: BuildExpression(syntax.DeclarationStatement.Assignment.Expression, "Declaration initializer");

		return statement;
	}

	IfStatement BuildIfStatement(KeywordStatementSyntax syntax)
	{
		return new IfStatement
		{
			SourceSyntax = syntax,
			Condition = BuildConditionExpression(syntax.Condition, "If statement"),
			Body = BuildStatementBody(syntax)
		};
	}

	WhileStatement BuildWhileStatement(KeywordStatementSyntax syntax)
	{
		return new WhileStatement
		{
			SourceSyntax = syntax,
			Condition = BuildConditionExpression(syntax.Condition, "While statement"),
			Body = BuildStatementBody(syntax)
		};
	}

	DoWhileStatement BuildDoWhileStatement(KeywordStatementSyntax syntax)
	{
		return new DoWhileStatement
		{
			SourceSyntax = syntax,
			Body = BuildStatementBody(syntax)
		};
	}

	ForStatement BuildForStatement(KeywordStatementSyntax syntax)
	{
		ForStatement statement = new()
		{
			SourceSyntax = syntax,
			Body = BuildStatementBody(syntax)
		};

		statement.Condition.SourceSyntax = syntax.Condition;
		BuildForStatementCondition(statement.Condition, syntax.Condition);
		return statement;
	}

	ForeachStatement BuildForeachStatement(KeywordStatementSyntax syntax)
	{
		ForeachStatement statement = new()
		{
			SourceSyntax = syntax,
			Body = BuildStatementBody(syntax)
		};

		if (syntax.Condition is IterationStatementConditionSyntax iteration)
		{
			statement.Target.SourceSyntax = iteration.Target;
			BuildDeclarationTarget(statement.Target, iteration.Target, "Foreach statement");
			statement.Source = BuildExpression(iteration.Expression, "Foreach source");
		}
		else
		{
			Report((SyntaxNode?)syntax.Condition ?? syntax, "Foreach statement must use a declaration target followed by 'in'.");
		}

		return statement;
	}

	SwitchStatement BuildSwitchStatement(KeywordStatementSyntax syntax)
	{
		SwitchStatement statement = new()
		{
			SourceSyntax = syntax,
			Expression = BuildConditionExpression(syntax.Condition, "Switch statement")
		};

		if (syntax.BodyStatements is null)
			statement.Statements.Add(BuildStatementBody(syntax));
		else
			AddStatements(statement.Statements, syntax.BodyStatements);

		return statement;
	}

	WithinStatement BuildWithinStatement(KeywordStatementSyntax syntax)
	{
		return new WithinStatement
		{
			SourceSyntax = syntax,
			Allocator = BuildConditionExpression(syntax.Condition, "Within statement"),
			Body = BuildStatementBody(syntax)
		};
	}

	void AttachElse(System.Collections.Generic.List<Statement> target, KeywordStatementSyntax syntax)
	{
		if (target.Count == 0 || target[^1] is not IfStatement ifStatement)
		{
			Report(syntax, "'else' must follow an if statement.");
			return;
		}

		if (ifStatement.ElseBody is not null)
			Report(syntax, "If statement already has an else body.");

		ifStatement.ElseBody = BuildStatementBody(syntax);
	}

	void AttachDoWhileCondition(DoWhileStatement statement, KeywordStatementSyntax syntax)
	{
		statement.Condition = BuildConditionExpression(syntax.Condition, "Do-while statement");

		if (syntax.Body is not EmptyStatementSyntax || syntax.BodyStatements is not null)
			Report(syntax, "Do-while condition must be followed by a semicolon.");
	}

	void AttachCatch(System.Collections.Generic.List<Statement> target, KeywordStatementSyntax syntax)
	{
		if (target.Count == 0 || target[^1] is not TryStatement tryStatement)
		{
			Report(syntax, "'catch' must follow a try statement.");
			return;
		}

		if (tryStatement.Finally is not null)
			Report(syntax, "'catch' may not follow 'finally'.");

		tryStatement.Catches.Add(BuildCatchStatement(syntax));
	}

	void AttachFinally(System.Collections.Generic.List<Statement> target, KeywordStatementSyntax syntax)
	{
		if (target.Count == 0 || target[^1] is not TryStatement tryStatement)
		{
			Report(syntax, "'finally' must follow a try statement.");
			return;
		}

		if (tryStatement.Finally is not null)
			Report(syntax, "Try statement already has a finally body.");

		tryStatement.Finally = new FinallyStatement
		{
			SourceSyntax = syntax,
			Body = BuildStatementBody(syntax)
		};
	}

	CatchStatement BuildCatchStatement(KeywordStatementSyntax syntax)
	{
		CatchStatement statement = new()
		{
			SourceSyntax = syntax,
			Body = BuildStatementBody(syntax)
		};

		statement.Target.SourceSyntax = syntax.Condition;
		BuildCatchTarget(statement.Target, syntax.Condition);
		return statement;
	}

	Statement BuildStatementBody(KeywordStatementSyntax syntax)
	{
		if (syntax.BodyStatements is not null)
		{
			BlockStatement block = new() { SourceSyntax = syntax };
			AddStatements(block.Statements, syntax.BodyStatements);
			return block;
		}

		if (syntax.Body is not null)
			return BuildStatement(syntax.Body);

		return new EmptyStatement { SourceSyntax = syntax };
	}

	Expression? BuildStatementExpression(KeywordStatementSyntax syntax, string context)
	{
		if (syntax.Condition is not null)
			return BuildConditionExpression(syntax.Condition, context);

		if (syntax.Body is EmptyStatementSyntax)
			return null;

		if (syntax.Body is ExpressionStatementSyntax expression)
			return BuildExpression(expression.Expression, context);

		if (syntax.Body is not null)
			Report(syntax.Body, $"{context} must be followed by an expression.");

		return null;
	}

	BreakStatement BuildBreakStatement(KeywordStatementSyntax syntax)
	{
		if (syntax.Condition is not null || syntax.Body is not null and not EmptyStatementSyntax || syntax.BodyStatements is not null)
			Report(syntax, "Break statement may not have an expression or body.");

		return new BreakStatement { SourceSyntax = syntax };
	}

	ContinueStatement BuildContinueStatement(KeywordStatementSyntax syntax)
	{
		if (syntax.Condition is not null || syntax.Body is not null and not EmptyStatementSyntax || syntax.BodyStatements is not null)
			Report(syntax, "Continue statement may not have an expression or body.");

		return new ContinueStatement { SourceSyntax = syntax };
	}

	GotoStatement BuildGotoStatement(KeywordStatementSyntax syntax)
	{
		if (syntax.Condition is not null || syntax.BodyStatements is not null)
			Report(syntax, "Goto statement may not have a condition or body.");

		GotoStatement statement = new() { SourceSyntax = syntax };
		if (syntax.Body is EmptyStatementSyntax)
			Report(syntax, "Goto statement is missing a label.");
		else if (syntax.Body is ExpressionStatementSyntax { Expression: QualifiedNameExpressionSyntax name } && name.Qualifiers is null or { Count: 0 })
			statement.TargetName = name.Identifier?.Value;
		else if (syntax.Body is not null)
			Report(syntax.Body, "Goto statement requires a label name.");

		return statement;
	}

	Expression? BuildConditionExpression(StatementConditionSyntax? syntax, string context)
	{
		if (syntax is ClauseStatementConditionSyntax clause)
		{
			if (clause.DeclarationStatement is not null)
			{
				Report(clause.DeclarationStatement, $"{context} condition may not be a declaration.");
				return null;
			}

			if (clause.Clauses is [StatementConditionClauseSyntax first])
				return BuildExpression(first.Expression, $"{context} condition");

			Report(clause, $"{context} must have exactly one condition expression.");
			return null;
		}

		if (syntax is IterationStatementConditionSyntax)
		{
			Report(syntax, $"{context} condition may not be an iteration declaration.");
			return null;
		}

		Report((TokenRange?)null, $"{context} is missing a condition.");
		return null;
	}

	void BuildForStatementCondition(ForStatementCondition target, StatementConditionSyntax? syntax)
	{
		if (syntax is not ClauseStatementConditionSyntax clause)
		{
			if (syntax is null)
				Report((TokenRange?)null, "For statement must use semicolon-separated clauses.");
			else
				Report(syntax, "For statement must use semicolon-separated clauses.");
			return;
		}

		if (clause.DeclarationStatement is not null)
			target.Declaration = BuildDeclarationStatement(clause.DeclarationStatement);

		foreach (StatementConditionClauseSyntax item in clause.Clauses ?? [])
			target.Clauses.Add(item.Expression is null ? null : BuildExpression(item.Expression, "For statement clause"));
	}

	DeclarationStatement BuildDeclarationStatement(DeclarationStatementSyntax syntax)
	{
		DeclarationStatement statement = new() { SourceSyntax = syntax };
		statement.Target.SourceSyntax = syntax.Target;
		BuildDeclarationTarget(statement.Target, syntax.Target, "Declaration statement");
		statement.InitialValue = syntax.Assignment is null
			? null
			: BuildExpression(syntax.Assignment.Expression, "Declaration initializer");
		return statement;
	}

	void BuildCatchTarget(DeclarationTarget target, StatementConditionSyntax? syntax)
	{
		if (syntax is null)
			return;

		if (syntax is ClauseStatementConditionSyntax { DeclarationStatement: not null } clause)
		{
			BuildDeclarationTarget(target, clause.DeclarationStatement.Target, "Catch statement");
			return;
		}

		Report(syntax, "Catch statement must declare a catch target.");
	}

	void BuildDeclarationTarget(DeclarationTarget target, DeclarationTargetSyntax? syntax, string context)
	{
		if (syntax is null)
		{
			Report((TokenRange?)null, $"{context} is missing a declaration target.");
			return;
		}

		if (syntax.AutoKeyword is not null)
		{
			target.Type = new AutoTypeReference { SourceSyntax = syntax };

			if (syntax.AutoIdentifier is not null)
			{
				target.Names.Add(syntax.AutoIdentifier.Value.Value);
			}
			else
			{
				foreach (Token identifier in syntax.IdentifierList?.Identifiers ?? [])
					target.Names.Add(identifier.Value);
			}

			if (target.Names.Count == 0)
				Report(syntax, $"{context} auto declaration is missing a name.");

			return;
		}

		target.Type = syntax.Type is null ? MissingType(syntax, $"{context} is missing a type.") : BuildTypeReference(syntax.Type);

		if (syntax.Identifier is null)
			Report(syntax, $"{context} is missing a name.");
		else
			target.Names.Add(syntax.Identifier.Value.Value);
	}

	Statement BuildDetachedFollowerStatement(KeywordStatementSyntax syntax, string message)
	{
		Report(syntax, message);
		return BuildStatementBody(syntax);
	}

	Statement BuildUnknownKeywordStatement(KeywordStatementSyntax syntax)
	{
		Report(syntax, $"Unsupported statement keyword '{syntax.Keyword?.Value}'.");
		return BuildStatementBody(syntax);
	}
}
