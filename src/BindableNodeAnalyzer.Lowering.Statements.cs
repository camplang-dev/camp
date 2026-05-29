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
				switchStatement.Expression = switchStatement.Expression is not null && ContainsUncaughtThrow(switchStatement.Expression)
					? HoistThrowingExpression(switchStatement.Expression)
					: LowerExpression(switchStatement.Expression);
				RewriteStatementList(switchStatement.Statements);
				break;

			case CaseStatement caseStatement:
				caseStatement.Expression = LowerExpression(caseStatement.Expression);
				break;

			case LabelStatement:
			case GotoStatement:
				break;

			case ReturnStatement returnStatement:
				returnStatement.Expression = returnStatement.Expression is not null && ContainsUncaughtThrow(returnStatement.Expression)
					? HoistThrowingExpression(returnStatement.Expression)
					: LowerExpression(returnStatement.Expression);
				return WithPendingCleanups(returnStatement);

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
				Expression? previousWithinContext = currentWithinContext;
				currentWithinContext = allocator;
				Statement rewritten = withinStatement.Body is null ? CreateBlock([]) : RewriteStatement(withinStatement.Body);
				currentWithinContext = previousWithinContext;
				return rewritten;
			}
		}

		return statement;
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

		string typeName = construction.Type.ResolvedType ?? BaseConstructedType(construction.ResolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return false;

		FunctionDefinition? initNew = FindInitNewMethod(definition, construction.Arguments.Count);
		declaration.InitialValue = CreateAllocCall(TypeReferenceFor(definition), construction.SourceSyntax ?? declaration.SourceSyntax);
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
				Expression = CreateInitNewCall(target, initNew, construction.Arguments, construction.SourceSyntax)
			}
		});
		return true;
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
			FunctionDefinition? opDelete = constructedDefinition is null ? null : FindDeleteMethod(constructedDefinition);
			if (opDelete is null && constructedDefinition is not null)
				opDelete = FindCallableDeleteMethod(constructedDefinition, declaration.SourceSyntax);
			currentCleanupScopes[^1].Statements.Add(new ExpressionStatement
			{
				SourceSyntax = declaration.SourceSyntax,
				ResolvedType = "void",
				Expression = CreateDeleteExpression(
					CreateVariableReference(declaration.Target, declaration.Target.ResolvedType ?? construction.ResolvedType ?? ErrorType),
					opDelete,
					deallocate: false)
			});
		}
		return true;
	}

}
