using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void AnalyzeOptionalFunctionBody(BlockStatement? body, AnalysisScope scope)
	{
		if (body is null)
			return;

		body.ResolvedType = "void";
		foreach (Statement statement in body.Statements)
			AnalyzeStatement(statement, scope);
	}

	void AnalyzeStatement(Statement statement, AnalysisScope scope)
	{
		statement.ResolvedType = "void";

		switch (statement)
		{
			case BlockStatement block:
				foreach (Statement child in block.Statements)
					AnalyzeStatement(child, scope);
				break;

			case ExpressionStatement expression:
				AnalyzeOptionalExpression(expression.Expression, scope);
				break;

			case DeclarationStatement declaration:
				AnalyzeDeclarationTarget(declaration.Target, scope);
				AnalyzeOptionalExpression(declaration.InitialValue, scope);
				break;

			case IfStatement ifStatement:
				AnalyzeOptionalExpression(ifStatement.Condition, scope);
				AnalyzeOptionalStatement(ifStatement.Body, scope);
				AnalyzeOptionalStatement(ifStatement.ElseBody, scope);
				break;

			case WhileStatement whileStatement:
				AnalyzeOptionalExpression(whileStatement.Condition, scope);
				AnalyzeOptionalStatement(whileStatement.Body, scope);
				break;

			case DoWhileStatement doWhile:
				AnalyzeOptionalStatement(doWhile.Body, scope);
				AnalyzeOptionalExpression(doWhile.Condition, scope);
				break;

			case ForStatement forStatement:
				AnalyzeForStatementCondition(forStatement.Condition, scope);
				AnalyzeOptionalStatement(forStatement.Body, scope);
				break;

			case ForeachStatement foreachStatement:
				AnalyzeDeclarationTarget(foreachStatement.Target, scope);
				AnalyzeOptionalExpression(foreachStatement.Source, scope);
				AnalyzeOptionalStatement(foreachStatement.Body, scope);
				break;

			case SwitchStatement switchStatement:
				AnalyzeOptionalExpression(switchStatement.Expression, scope);
				foreach (Statement child in switchStatement.Statements)
					AnalyzeStatement(child, scope);
				break;

			case CaseStatement caseStatement:
				AnalyzeOptionalExpression(caseStatement.Expression, scope);
				break;

			case LabelStatement labelStatement:
				CheckName(labelStatement.Name, GetLabelNameRange(labelStatement.SourceSyntax), "label");
				break;

			case GotoStatement gotoStatement:
				CheckName(gotoStatement.TargetName, GetGotoTargetNameRange(gotoStatement.SourceSyntax), "label");
				break;

			case ReturnStatement returnStatement:
				AnalyzeOptionalExpression(returnStatement.Expression, scope);
				break;

			case YieldStatement yieldStatement:
				AnalyzeOptionalExpression(yieldStatement.Expression, scope);
				break;

			case DeleteStatement deleteStatement:
				AnalyzeOptionalExpression(deleteStatement.Expression, scope);
				break;

			case TryStatement tryStatement:
				AnalyzeOptionalStatement(tryStatement.Body, scope);
				foreach (CatchStatement catchStatement in tryStatement.Catches)
					AnalyzeStatement(catchStatement, scope);
				AnalyzeOptionalStatement(tryStatement.Finally, scope);
				break;

			case CatchStatement catchStatement:
				AnalyzeDeclarationTarget(catchStatement.Target, scope);
				AnalyzeOptionalStatement(catchStatement.Body, scope);
				break;

			case FinallyStatement finallyStatement:
				AnalyzeOptionalStatement(finallyStatement.Body, scope);
				break;

			case WithinStatement withinStatement:
				AnalyzeOptionalExpression(withinStatement.Allocator, scope);
				AnalyzeOptionalStatement(withinStatement.Body, scope);
				break;
		}
	}

	void AnalyzeOptionalStatement(Statement? statement, AnalysisScope scope)
	{
		if (statement is not null)
			AnalyzeStatement(statement, scope);
	}

	void AnalyzeDeclarationTarget(DeclarationTarget target, AnalysisScope scope)
	{
		AnalyzeOptionalType(target.Type, scope);
		target.ResolvedType = target.Type?.ResolvedType ?? ErrorType;

		foreach (string name in target.Names)
		{
			if (name == "_")
				continue;
			CheckName(name, GetDeclarationTargetNameRange(target.SourceSyntax, name), "local");
		}
	}

	void AnalyzeForStatementCondition(ForStatementCondition condition, AnalysisScope scope)
	{
		condition.ResolvedType = "bool";
		if (condition.Declaration is not null)
			AnalyzeStatement(condition.Declaration, scope);

		foreach (Expression? clause in condition.Clauses)
			AnalyzeOptionalExpression(clause, scope);
	}

	void AnalyzeOptionalExpression(Expression? expression, AnalysisScope scope)
	{
		if (expression is not null)
			AnalyzeExpression(expression, scope);
	}

	void AnalyzeExpression(Expression expression, AnalysisScope scope)
	{
		expression.ResolvedType = UnresolvedType;

		switch (expression)
		{
			case DefaultExpression defaultExpression:
				if (defaultExpression.Type is not null)
					AnalyzeType(defaultExpression.Type, scope);
				expression.ResolvedType = defaultExpression.Type?.ResolvedType ?? TargetType;
				break;

			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
				{
					item.ResolvedType = UnresolvedType;
				AnalyzeOptionalExpression(item.Expression, scope);
				}
				break;

			case ArrayExpression array:
				foreach (Expression element in array.Elements)
					AnalyzeExpression(element, scope);
				break;

			case InitializerExpression initializer:
				foreach (InitializerItem item in initializer.Items)
				{
					item.ResolvedType = UnresolvedType;
					if (item.Target is not null)
						AnalyzeInitializerTarget(item.Target, scope);
					AnalyzeOptionalExpression(item.Expression, scope);
				}
				break;

			case ParenthesizedExpression parenthesized:
				AnalyzeOptionalExpression(parenthesized.Expression, scope);
				expression.ResolvedType = parenthesized.Expression?.ResolvedType ?? UnresolvedType;
				break;

			case CastExpression cast:
				if (cast.Type is not null)
					AnalyzeType(cast.Type, scope);
				AnalyzeOptionalExpression(cast.Expression, scope);
				expression.ResolvedType = cast.Type?.ResolvedType ?? ErrorType;
				break;

			case ConstructionExpression construction:
				if (construction.Type is not null)
					AnalyzeType(construction.Type, scope);
				foreach (ArgumentExpression argument in construction.Arguments)
					AnalyzeExpression(argument, scope);
				AnalyzeOptionalExpression(construction.ElementCount, scope);
				AnalyzeOptionalExpression(construction.Initializer, scope);
				expression.ResolvedType = construction.Type?.ResolvedType ?? TargetType;
				break;

			case WithinExpression within:
				AnalyzeOptionalExpression(within.Context, scope);
				AnalyzeOptionalExpression(within.Expression, scope);
				expression.ResolvedType = within.Expression?.ResolvedType ?? UnresolvedType;
				break;

			case SizeOfExpression sizeOf:
				if (sizeOf.Type is not null)
					AnalyzeType(sizeOf.Type, scope);
				expression.ResolvedType = "nuint";
				break;

			case VTableOfExpression vtableOf:
				if (vtableOf.Type is not null)
					AnalyzeType(vtableOf.Type, scope);
				if (vtableOf.InterfaceType is not null)
					AnalyzeType(vtableOf.InterfaceType, scope);
				expression.ResolvedType = VTableType;
				break;

			case LambdaExpression lambda:
				foreach (LambdaParameter parameter in lambda.Parameters)
				{
					parameter.ResolvedType = parameter.Parameter?.ResolvedType ?? TargetType;
					CheckName(GetLambdaParameterSymbolName(parameter), GetLambdaParameterNameRange(parameter.SourceSyntax), "lambda parameter");
					if (parameter.Parameter is not null)
						AnalyzeParameterDefinition(parameter.Parameter, scope);
				}
				AnalyzeOptionalFunctionBody(lambda.Body, scope);
				break;

			case ArgumentExpression argument:
				if (argument.Type is not null)
					AnalyzeType(argument.Type, scope);
				if (argument.Target is not null)
					AnalyzeDeclarationTarget(argument.Target, scope);
				AnalyzeOptionalExpression(argument.Value, scope);
				expression.ResolvedType = argument.Type?.ResolvedType ?? argument.Target?.ResolvedType ?? argument.Value?.ResolvedType ?? UnresolvedType;
				break;

			case CallExpression call:
				AnalyzeOptionalExpression(call.Target, scope);
				AnalyzeTypeList(call.TypeArguments, scope);
				foreach (ArgumentExpression argument in call.Arguments)
					AnalyzeExpression(argument, scope);
				break;

			case IndexExpression index:
				AnalyzeOptionalExpression(index.Target, scope);
				foreach (ArgumentExpression argument in index.Arguments)
					AnalyzeExpression(argument, scope);
				break;

			case MemberExpression member:
				AnalyzeOptionalExpression(member.Target, scope);
				break;

			case MemberReferenceExpression member:
				AnalyzeOptionalExpression(member.Target, scope);
				break;

			case NamelessIndexerExpression indexer:
				AnalyzeOptionalExpression(indexer.Target, scope);
				foreach (ArgumentExpression argument in indexer.Arguments)
					AnalyzeExpression(argument, scope);
				break;

			case UnaryExpression unary:
				AnalyzeOptionalExpression(unary.Operand, scope);
				AnalyzeOptionalExpression(unary.Context, scope);
				break;

			case PostfixUpdateExpression postfix:
				AnalyzeOptionalExpression(postfix.Expression, scope);
				break;

			case FinallyDeleteExpression finallyDelete:
				AnalyzeOptionalExpression(finallyDelete.Expression, scope);
				break;

			case BinaryExpression binary:
				AnalyzeOptionalExpression(binary.Left, scope);
				AnalyzeOptionalExpression(binary.Right, scope);
				break;

			case AssignmentExpression assignment:
				AnalyzeOptionalExpression(assignment.Target, scope);
				AnalyzeOptionalExpression(assignment.Value, scope);
				expression.ResolvedType = assignment.Target?.ResolvedType ?? assignment.Value?.ResolvedType ?? UnresolvedType;
				break;

			case ConditionalExpression conditional:
				AnalyzeOptionalExpression(conditional.Condition, scope);
				AnalyzeOptionalExpression(conditional.WhenTrue, scope);
				AnalyzeOptionalExpression(conditional.WhenFalse, scope);
				break;

			case RangeExpression range:
				AnalyzeOptionalExpression(range.Start, scope);
				AnalyzeOptionalExpression(range.End, scope);
				expression.ResolvedType = RangeType;
				break;
		}
	}

	void AnalyzeInitializerTarget(InitializerTarget target, AnalysisScope scope)
	{
		target.ResolvedType = TargetType;
		foreach (InitializerTargetPart part in target.Parts)
		{
			part.ResolvedType = TargetType;
			foreach (ArgumentExpression argument in part.Arguments)
				AnalyzeExpression(argument, scope);
		}
	}
}
