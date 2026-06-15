using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	Expression LowerLambdaExpression(LambdaExpression lambda)
	{
		if (expressionRewrites.TryGetValue(lambda, out Expression? rewritten)
			&& !ReferenceEquals(rewritten, lambda))
			return rewritten;

		if (!TryGetCallableShape(lambda.ResolvedType, out CallableShape shape) || shape.Kind is not ("fn" or "delegate"))
		{
			Report(GetRange(lambda.SourceSyntax), "Lambda lowering supports only fn or delegate targets.");
			return lambda;
		}

		if (LambdaCapturesUnsupportedValues(lambda))
			return lambda;

		bool delegateTarget = shape.Kind == "delegate";
		FunctionDefinition function = CreateLambdaFunction(lambda, shape, delegateTarget);
		int parameterOffset = delegateTarget ? 1 : 0;
		RewriteLambdaParameterReferences(function.Body, lambda.Parameters, function.Parameters, parameterOffset);
		RewriteFunction(function, containingType: null);
		RewriteLambdaParameterReferences(function.Body, lambda.Parameters, function.Parameters, parameterOffset);
		generatedLambdaDefinitions.Add(function);
		Expression result = delegateTarget
			? CreateDelegateLambdaInitializer(lambda, function)
			: CreateMethodReference(function, lambda.ResolvedType ?? BuildFunctionValueType(function, isInstance: false));
		expressionRewrites[lambda] = result;
		return result;
	}

	FunctionDefinition CreateLambdaFunction(LambdaExpression lambda, CallableShape shape, bool includeDelegateContext)
	{
		string owner = currentRewriteFunction is null ? "lambda" : GetCallableName(currentRewriteFunction).TrimStart('~');
		string name = owner + "_lambda" + generatedLambdaDefinitions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
		FunctionDefinition function = new()
		{
			SourceSyntax = lambda.SourceSyntax,
			Name = name,
			Symbol = name,
			ResolvedType = shape.ReturnType,
			ReturnType = TypeReferenceForResolvedName(shape.ReturnType),
			Body = lambda.Body
		};
		if (currentRewriteFunction is not null)
		{
			foreach (GenericParameter parameter in currentRewriteFunction.GenericParameters)
				function.GenericParameters.Add(CloneGenericParameter(parameter));
		}
		if (includeDelegateContext)
		{
			function.Parameters.Add(new ParameterDefinition
			{
				SourceSyntax = lambda.SourceSyntax,
				Name = "context",
				Symbol = "context",
				Type = TypeReferenceForResolvedName("void*"),
				ResolvedType = "void*"
			});
		}
		for (int i = 0; i < lambda.Parameters.Count; i++)
			function.Parameters.Add(CreateLambdaFunctionParameter(lambda.Parameters[i], shape.Parameters[i], i));
		return function;
	}

	InitializerExpression CreateDelegateLambdaInitializer(LambdaExpression lambda, FunctionDefinition function)
	{
		InitializerExpression initializer = new()
		{
			SourceSyntax = lambda.SourceSyntax,
			ResolvedType = lambda.ResolvedType
		};
		initializer.Items.Add(new InitializerItem
		{
			SourceSyntax = lambda.SourceSyntax,
			Expression = CreateMethodReference(function, BuildFunctionValueType(function, isInstance: false))
		});
		initializer.Items.Add(new InitializerItem
		{
			SourceSyntax = lambda.SourceSyntax,
			Expression = new LiteralExpression
			{
				SourceSyntax = lambda.SourceSyntax,
				Kind = LiteralKind.Null,
				Text = "null",
				ResolvedType = "#NULL"
			}
		});
		return initializer;
	}

	static GenericParameter CloneGenericParameter(GenericParameter parameter)
	{
		return new GenericParameter
		{
			SourceSyntax = parameter.SourceSyntax,
			Name = parameter.Name,
			RequiresImplementation = parameter.RequiresImplementation,
			Constraint = CloneType(parameter.Constraint),
			ResolvedType = parameter.ResolvedType
		};
	}

	static ParameterDefinition CreateLambdaFunctionParameter(LambdaParameter parameter, string parameterType, int index)
	{
		ParameterDefinition source = parameter.Parameter ?? new ParameterDefinition();
		string name = GetLambdaParameterSymbolName(parameter) ?? "arg" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
		return new ParameterDefinition
		{
			SourceSyntax = parameter.SourceSyntax,
			Name = name,
			Symbol = name,
			Modifier = source.Modifier,
			Type = source.Type is null ? TypeReferenceForResolvedName(parameterType) : CloneType(source.Type),
			ResolvedType = parameterType
		};
	}

	bool LambdaCapturesUnsupportedValues(LambdaExpression lambda)
	{
		HashSet<BindableNode> localNodes = [];
		foreach (LambdaParameter parameter in lambda.Parameters)
			localNodes.Add(parameter);
		CollectLambdaLocalNodes(lambda.Body, localNodes);
		return LambdaCapturesUnsupportedValues(lambda.Body, localNodes);
	}

	static void CollectLambdaLocalNodes(Statement? statement, HashSet<BindableNode> localNodes)
	{
		switch (statement)
		{
			case null:
				return;
			case BlockStatement block:
				foreach (Statement child in block.Statements)
					CollectLambdaLocalNodes(child, localNodes);
				break;
			case DeclarationStatement declaration:
				localNodes.Add(declaration.Target);
				CollectLambdaLocalNodes(declaration.InitialValue, localNodes);
				break;
			case ForStatement forStatement:
				CollectLambdaLocalNodes(forStatement.Condition.Declaration, localNodes);
				foreach (Expression? clause in forStatement.Condition.Clauses)
					CollectLambdaLocalNodes(clause, localNodes);
				CollectLambdaLocalNodes(forStatement.Body, localNodes);
				break;
			case ForeachStatement foreachStatement:
				localNodes.Add(foreachStatement.Target);
				CollectLambdaLocalNodes(foreachStatement.Source, localNodes);
				CollectLambdaLocalNodes(foreachStatement.Body, localNodes);
				break;
			case CatchStatement catchStatement:
				localNodes.Add(catchStatement.Target);
				CollectLambdaLocalNodes(catchStatement.Body, localNodes);
				break;
			default:
				foreach ((Statement? childStatement, Expression? childExpression) in LambdaStatementChildren(statement))
				{
					if (childStatement is not null)
						CollectLambdaLocalNodes(childStatement, localNodes);
					if (childExpression is not null)
						CollectLambdaLocalNodes(childExpression, localNodes);
				}
				break;
		}
	}

	static void CollectLambdaLocalNodes(Expression? expression, HashSet<BindableNode> localNodes)
	{
		if (expression is null)
			return;
		foreach (Expression child in LambdaExpressionChildren(expression))
			CollectLambdaLocalNodes(child, localNodes);
	}

	bool LambdaCapturesUnsupportedValues(BindableNode? node, HashSet<BindableNode> localNodes)
	{
		switch (node)
		{
			case null:
				return false;
			case ThisExpression thisExpression:
				Report(GetRange(thisExpression.SourceSyntax), "Lambda captures are not implemented yet; 'this' cannot be referenced by a Stage 1 lambda.");
				return true;
			case VariableReferenceExpression { Variable: BindableNode variable } reference
				when !IsLambdaLocalOrGlobal(variable, localNodes):
				Report(GetRange(reference.SourceSyntax), "Lambda captures are not implemented yet; captured local values cannot be referenced by a Stage 1 lambda.");
				return true;
			case NamedExpression named
				when TryGetRewrittenVariable(named, out BindableNode variable) && !IsLambdaLocalOrGlobal(variable, localNodes):
				Report(GetRange(named.SourceSyntax), "Lambda captures are not implemented yet; captured local values cannot be referenced by a Stage 1 lambda.");
				return true;
		}

		bool captured = false;
		if (node is Statement statement)
		{
			foreach ((Statement? childStatement, Expression? childExpression) in LambdaStatementChildren(statement))
			{
				if (childStatement is not null)
					captured |= LambdaCapturesUnsupportedValues(childStatement, localNodes);
				if (childExpression is not null)
					captured |= LambdaCapturesUnsupportedValues(childExpression, localNodes);
			}
		}
		else if (node is Expression expression)
		{
			foreach (Expression childExpression in LambdaExpressionChildren(expression))
				captured |= LambdaCapturesUnsupportedValues(childExpression, localNodes);
		}
		return captured;
	}

	static void RewriteLambdaParameterReferences(Statement? statement, List<LambdaParameter> lambdaParameters, List<ParameterDefinition> functionParameters, int functionParameterOffset)
	{
		if (statement is null)
			return;
		foreach ((Statement? childStatement, Expression? childExpression) in LambdaStatementChildren(statement))
		{
			RewriteLambdaParameterReferences(childStatement, lambdaParameters, functionParameters, functionParameterOffset);
			RewriteLambdaParameterReferences(childExpression, lambdaParameters, functionParameters, functionParameterOffset);
		}
	}

	static void RewriteLambdaParameterReferences(Expression? expression, List<LambdaParameter> lambdaParameters, List<ParameterDefinition> functionParameters, int functionParameterOffset)
	{
		if (expression is null)
			return;
		if (expression is VariableReferenceExpression variable)
		{
			for (int i = 0; i < lambdaParameters.Count; i++)
			{
				if (!ReferenceEquals(variable.Variable, lambdaParameters[i]))
					continue;
				int functionParameterIndex = i + functionParameterOffset;
				if (functionParameterIndex >= functionParameters.Count)
					break;
				variable.Variable = functionParameters[functionParameterIndex];
				variable.ResolvedType = functionParameters[functionParameterIndex].ResolvedType;
				break;
			}
		}
		foreach (Expression child in LambdaExpressionChildren(expression))
			RewriteLambdaParameterReferences(child, lambdaParameters, functionParameters, functionParameterOffset);
	}

	static IEnumerable<(Statement? Statement, Expression? Expression)> LambdaStatementChildren(Statement statement)
	{
		switch (statement)
		{
			case BlockStatement block:
				foreach (Statement child in block.Statements)
					yield return (child, null);
				break;
			case ExpressionStatement expression:
				yield return (null, expression.Expression);
				break;
			case DeclarationStatement declaration:
				yield return (null, declaration.InitialValue);
				break;
			case IfStatement ifStatement:
				yield return (null, ifStatement.Condition);
				yield return (ifStatement.Body, null);
				yield return (ifStatement.ElseBody, null);
				break;
			case WhileStatement whileStatement:
				yield return (null, whileStatement.Condition);
				yield return (whileStatement.Body, null);
				break;
			case DoWhileStatement doWhile:
				yield return (doWhile.Body, null);
				yield return (null, doWhile.Condition);
				break;
			case ForStatement forStatement:
				yield return (forStatement.Condition.Declaration, null);
				foreach (Expression? clause in forStatement.Condition.Clauses)
					yield return (null, clause);
				yield return (forStatement.Body, null);
				break;
			case ForeachStatement foreachStatement:
				yield return (null, foreachStatement.Source);
				yield return (foreachStatement.Body, null);
				break;
			case SwitchStatement switchStatement:
				yield return (null, switchStatement.Expression);
				foreach (Statement child in switchStatement.Statements)
					yield return (child, null);
				break;
			case CaseStatement caseStatement:
				yield return (null, caseStatement.Expression);
				break;
			case ReturnStatement returnStatement:
				yield return (null, returnStatement.Expression);
				break;
			case YieldStatement yieldStatement:
				yield return (null, yieldStatement.Expression);
				break;
			case DeleteStatement deleteStatement:
				yield return (null, deleteStatement.Expression);
				break;
			case TryStatement tryStatement:
				yield return (tryStatement.Body, null);
				foreach (CatchStatement catchStatement in tryStatement.Catches)
					yield return (catchStatement, null);
				yield return (tryStatement.Finally, null);
				break;
			case CatchStatement catchStatement:
				yield return (catchStatement.Body, null);
				break;
			case FinallyStatement finallyStatement:
				yield return (finallyStatement.Body, null);
				break;
			case WithinStatement withinStatement:
				yield return (null, withinStatement.Allocator);
				yield return (withinStatement.Body, null);
				break;
		}
	}

	static IEnumerable<Expression> LambdaExpressionChildren(Expression expression)
	{
		switch (expression)
		{
			case ParenthesizedExpression parenthesized when parenthesized.Expression is not null:
				yield return parenthesized.Expression;
				break;
			case CastExpression cast when cast.Expression is not null:
				yield return cast.Expression;
				break;
			case WithinExpression within:
				if (within.Context is not null)
					yield return within.Context;
				if (within.Expression is not null)
					yield return within.Expression;
				break;
			case UnaryExpression unary:
				if (unary.Operand is not null)
					yield return unary.Operand;
				if (unary.Context is not null)
					yield return unary.Context;
				break;
			case PostfixUpdateExpression postfix when postfix.Expression is not null:
				yield return postfix.Expression;
				break;
			case BinaryExpression binary:
				if (binary.Left is not null)
					yield return binary.Left;
				if (binary.Right is not null)
					yield return binary.Right;
				break;
			case AssignmentExpression assignment:
				if (assignment.Target is not null)
					yield return assignment.Target;
				if (assignment.Value is not null)
					yield return assignment.Value;
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
				if (call.Target is not null)
					yield return call.Target;
				foreach (ArgumentExpression argument in call.Arguments)
					if (argument.Value is not null)
						yield return argument.Value;
				break;
			case IndexExpression index:
				if (index.Target is not null)
					yield return index.Target;
				foreach (ArgumentExpression argument in index.Arguments)
					if (argument.Value is not null)
						yield return argument.Value;
				break;
			case MemberExpression member when member.Target is not null:
				yield return member.Target;
				break;
			case MemberReferenceExpression member when member.Target is not null:
				yield return member.Target;
				break;
			case NamelessIndexerExpression indexer:
				if (indexer.Target is not null)
					yield return indexer.Target;
				foreach (ArgumentExpression argument in indexer.Arguments)
					if (argument.Value is not null)
						yield return argument.Value;
				break;
			case RangeExpression range:
				if (range.Start is not null)
					yield return range.Start;
				if (range.End is not null)
					yield return range.End;
				break;
			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
					if (item.Expression is not null)
						yield return item.Expression;
				break;
			case ArrayExpression array:
				foreach (Expression element in array.Elements)
					yield return element;
				break;
			case InitializerExpression initializer:
				foreach (InitializerItem item in initializer.Items)
					if (item.Expression is not null)
						yield return item.Expression;
				break;
			case ConstructionExpression construction:
				if (construction.ElementCount is not null)
					yield return construction.ElementCount;
				if (construction.Initializer is not null)
					yield return construction.Initializer;
				foreach (ArgumentExpression argument in construction.Arguments)
					if (argument.Value is not null)
						yield return argument.Value;
				break;
			case FinallyDeleteExpression finallyDelete when finallyDelete.Expression is not null:
				yield return finallyDelete.Expression;
				break;
			case ArgumentExpression argument when argument.Value is not null:
				yield return argument.Value;
				break;
		}
	}

	static bool IsLambdaLocalOrGlobal(BindableNode variable, HashSet<BindableNode> localNodes)
	{
		return localNodes.Contains(variable)
			|| variable is FunctionDefinition
			|| variable is VariableDefinition
			|| variable is FieldDefinition { Modifier: FieldModifier.Static };
	}

	bool TryGetRewrittenVariable(NamedExpression named, out BindableNode variable)
	{
		if (expressionRewrites.TryGetValue(named, out Expression? rewrite)
			&& rewrite is VariableReferenceExpression { Variable: BindableNode rewrittenVariable })
		{
			variable = rewrittenVariable;
			return true;
		}

		variable = null!;
		return false;
	}
}
