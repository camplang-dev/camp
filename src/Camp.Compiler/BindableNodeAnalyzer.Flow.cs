using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void FlowAnalyzeFunctionBody(FunctionDefinition function, BodyScope bodyScope)
	{
		if (function.Body is null)
			return;

		FlowState state = new(function, bodyScope);
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (string.IsNullOrWhiteSpace(parameter.Name))
				continue;

			bool isAssigned = parameter.Modifier != ParameterModifier.Out;
			state.Declare(parameter.Name, isAssigned);
		}

		FlowAnalyzeStatements(function.Body.Statements, state);

		if (state.Reachable)
		{
			if (FunctionRequiresReturn(function))
				Report(GetRange(function.Body.SourceSyntax ?? function.SourceSyntax), $"Function '{function.Name}' does not return a value on all paths.");

			CheckOutParametersAssigned(function, state, function.Body.SourceSyntax ?? function.SourceSyntax);
		}
	}

	static string InferBlockReturnType(BlockStatement block, string? targetReturnType)
	{
		foreach (Statement statement in block.Statements)
		{
			if (statement is ReturnStatement returnStatement)
				return returnStatement.Expression?.ResolvedType ?? "void";
		}

		return targetReturnType ?? "void";
	}

	void FlowAnalyzeStatements(List<Statement> statements, FlowState state)
	{
		foreach (Statement statement in statements)
		{
			if (!state.Reachable && statement is not LabelStatement)
				continue;

			FlowAnalyzeStatement(statement, state);
		}
	}

	void FlowAnalyzeStatement(Statement statement, FlowState state)
	{
		switch (statement)
		{
			case BlockStatement block:
				FlowAnalyzeStatements(block.Statements, state);
				break;

			case EmptyStatement:
			case BreakStatement:
			case ContinueStatement:
			case DefaultStatement:
				break;

			case ExpressionStatement expression:
				FlowAnalyzeExpression(expression.Expression, state);
				break;

			case DeclarationStatement declaration:
				FlowAnalyzeDeclarationStatement(declaration, state);
				break;

			case IfStatement ifStatement:
				FlowAnalyzeExpression(ifStatement.Condition, state);
				FlowAnalyzeBranchingStatement(ifStatement.Body, ifStatement.ElseBody, state);
				break;

			case WhileStatement whileStatement:
				FlowAnalyzeExpression(whileStatement.Condition, state);
				FlowAnalyzeOptionalStatement(whileStatement.Body, state.Clone());
				break;

			case DoWhileStatement doWhile:
			{
				FlowState bodyState = state.Clone();
				FlowAnalyzeOptionalStatement(doWhile.Body, bodyState);
				if (bodyState.Reachable)
					FlowAnalyzeExpression(doWhile.Condition, bodyState);
				break;
			}

			case ForStatement forStatement:
				if (forStatement.Condition.Declaration is not null)
					FlowAnalyzeDeclarationStatement(forStatement.Condition.Declaration, state);
				foreach (Expression? clause in forStatement.Condition.Clauses)
					FlowAnalyzeExpression(clause, state);
				FlowAnalyzeOptionalStatement(forStatement.Body, state.Clone());
				break;

			case ForeachStatement foreachStatement:
				FlowAnalyzeExpression(foreachStatement.Source, state);
				DeclareTargets(foreachStatement.Target, state, assigned: true);
				FlowAnalyzeOptionalStatement(foreachStatement.Body, state.Clone());
				break;

			case SwitchStatement switchStatement:
				FlowAnalyzeExpression(switchStatement.Expression, state);
				FlowAnalyzeSwitchStatement(switchStatement, state);
				break;

			case CaseStatement caseStatement:
				FlowAnalyzeExpression(caseStatement.Expression, state);
				break;

			case LabelStatement:
				state.Reachable = true;
				break;

			case GotoStatement:
				state.Reachable = false;
				break;

			case ReturnStatement returnStatement:
				FlowAnalyzeExpression(returnStatement.Expression, state);
				CheckOutParametersAssigned(state.Function, state, returnStatement.SourceSyntax);
				state.Reachable = false;
				break;

			case YieldStatement yieldStatement:
				FlowAnalyzeExpression(yieldStatement.Expression, state);
				break;

			case DeleteStatement deleteStatement:
				if (!IsBaseDeleteExpression(deleteStatement.Expression))
					FlowAnalyzeExpression(deleteStatement.Expression, state);
				break;

			case TryStatement tryStatement:
				FlowAnalyzeTryStatement(tryStatement, state);
				break;

			case CatchStatement catchStatement:
				DeclareTargets(catchStatement.Target, state, assigned: true);
				FlowAnalyzeOptionalStatement(catchStatement.Body, state);
				break;

			case FinallyStatement finallyStatement:
				FlowAnalyzeOptionalStatement(finallyStatement.Body, state);
				break;

			case WithinStatement withinStatement:
				FlowAnalyzeExpression(withinStatement.Allocator, state);
				FlowAnalyzeOptionalStatement(withinStatement.Body, state);
				break;
		}
	}

	void FlowAnalyzeBranchingStatement(Statement? trueStatement, Statement? falseStatement, FlowState state)
	{
		FlowState trueState = state.Clone();
		FlowAnalyzeOptionalStatement(trueStatement, trueState);

		FlowState falseState = state.Clone();
		FlowAnalyzeOptionalStatement(falseStatement, falseState);

		state.MergeBranches(trueState, falseState);
	}

	void FlowAnalyzeSwitchStatement(SwitchStatement switchStatement, FlowState state)
	{
		bool hasDefault = false;
		List<FlowState> branchStates = [];
		List<Statement> currentStatements = [];

		foreach (Statement child in switchStatement.Statements)
		{
			if (child is CaseStatement or DefaultStatement)
			{
				if (currentStatements.Count > 0)
				{
					FlowState branch = state.Clone();
					FlowAnalyzeStatements(currentStatements, branch);
					branchStates.Add(branch);
					currentStatements.Clear();
				}

				if (child is CaseStatement caseStatement)
					FlowAnalyzeExpression(caseStatement.Expression, state);
				else
					hasDefault = true;
			}
			else
			{
				currentStatements.Add(child);
			}
		}

		if (currentStatements.Count > 0)
		{
			FlowState branch = state.Clone();
			FlowAnalyzeStatements(currentStatements, branch);
			branchStates.Add(branch);
		}

		if (!hasDefault)
			branchStates.Add(state.Clone());

		state.MergeBranches(branchStates);
	}

	void FlowAnalyzeTryStatement(TryStatement tryStatement, FlowState state)
	{
		List<string> catchTypes = [];
		foreach (CatchStatement catchStatement in tryStatement.Catches)
		{
			string? catchType = catchStatement.Target.ResolvedType;
			if (catchType is not null && catchType != ErrorType)
				catchTypes.Add(catchType);
		}

		FlowState tryState = state.Clone();
		tryState.Handlers.AddRange(catchTypes);
		FlowAnalyzeOptionalStatement(tryStatement.Body, tryState);

		List<FlowState> branches = [tryState];
		foreach (CatchStatement catchStatement in tryStatement.Catches)
		{
			FlowState catchState = state.Clone();
			DeclareTargets(catchStatement.Target, catchState, assigned: true);
			FlowAnalyzeOptionalStatement(catchStatement.Body, catchState);
			branches.Add(catchState);
		}

		state.MergeBranches(branches);

		if (tryStatement.Finally is not null)
			FlowAnalyzeStatement(tryStatement.Finally, state);
	}

	void FlowAnalyzeOptionalStatement(Statement? statement, FlowState state)
	{
		if (statement is not null && state.Reachable)
			FlowAnalyzeStatement(statement, state);
	}

	void FlowAnalyzeDeclarationStatement(DeclarationStatement declaration, FlowState state)
	{
		if (declaration.InitialValue is not null)
			FlowAnalyzeExpression(declaration.InitialValue, state);

		DeclareTargets(declaration.Target, state, declaration.InitialValue is not null || declaration.IsFixedStorage);
	}

	void DeclareTargets(DeclarationTarget target, FlowState state, bool assigned)
	{
		foreach (string name in target.Names)
		{
			if (!string.IsNullOrWhiteSpace(name) && name != "_")
				state.Declare(name, assigned);
		}
	}

	void FlowAnalyzeExpression(Expression? expression, FlowState state)
	{
		if (expression is null || !state.Reachable)
			return;

		switch (expression)
		{
			case LiteralExpression:
			case TypeReferenceExpression:
			case ThisExpression:
			case DefaultExpression:
			case MethodReferenceExpression:
			case VariableReferenceExpression:
				break;

			case NamedExpression named:
				CheckVariableRead(named, state);
				break;

			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
					FlowAnalyzeExpression(item.Expression, state);
				break;

			case ArrayExpression array:
				foreach (Expression element in array.Elements)
					FlowAnalyzeExpression(element, state);
				break;

			case InitializerExpression initializer:
				foreach (InitializerItem item in initializer.Items)
					FlowAnalyzeExpression(item.Expression, state);
				break;

			case ParenthesizedExpression parenthesized:
				FlowAnalyzeExpression(parenthesized.Expression, state);
				break;

			case CastExpression cast:
				FlowAnalyzeExpression(cast.Expression, state);
				break;

			case ConstructionExpression construction:
				FlowAnalyzeArguments(construction.Arguments, state);
				FlowAnalyzeExpression(construction.ElementCount, state);
				if (construction.Initializer is not null)
					FlowAnalyzeExpression(construction.Initializer, state);
				break;

			case WithinExpression within:
				FlowAnalyzeExpression(within.Context, state);
				FlowAnalyzeExpression(within.Expression, state);
				break;

			case LambdaExpression:
				break;

			case ArgumentExpression argument:
				FlowAnalyzeArgument(argument, state);
				break;

			case CallExpression call:
				FlowAnalyzeCallExpression(call, state);
				break;

			case IndexExpression index:
				FlowAnalyzeExpression(index.Target, state);
				FlowAnalyzeArguments(index.Arguments, state);
				break;

			case MemberExpression member:
				FlowAnalyzeExpression(member.Target, state);
				break;

			case MemberReferenceExpression member:
				FlowAnalyzeExpression(member.Target, state);
				break;

			case NamelessIndexerExpression indexer:
				FlowAnalyzeExpression(indexer.Target, state);
				FlowAnalyzeArguments(indexer.Arguments, state);
				break;

			case UnaryExpression unary:
				FlowAnalyzeUnaryExpression(unary, state);
				break;

			case PostfixUpdateExpression postfix:
				FlowAnalyzeExpression(postfix.Expression, state);
				AssignExpressionTarget(postfix.Expression, state);
				break;

			case FinallyDeleteExpression finallyDelete:
				if (!IsBaseDeleteExpression(finallyDelete.Expression))
					FlowAnalyzeExpression(finallyDelete.Expression, state);
				break;

			case BinaryExpression binary:
				FlowAnalyzeExpression(binary.Left, state);
				FlowAnalyzeExpression(binary.Right, state);
				break;

			case AssignmentExpression assignment:
				FlowAnalyzeAssignmentExpression(assignment, state);
				break;

			case ConditionalExpression conditional:
				FlowAnalyzeExpression(conditional.Condition, state);
				FlowState trueState = state.Clone();
				FlowAnalyzeExpression(conditional.WhenTrue, trueState);
				FlowState falseState = state.Clone();
				FlowAnalyzeExpression(conditional.WhenFalse, falseState);
				state.MergeBranches(trueState, falseState);
				break;

			case RangeExpression range:
				FlowAnalyzeExpression(range.Start, state);
				FlowAnalyzeExpression(range.End, state);
				break;
		}
	}

	void FlowAnalyzeUnaryExpression(UnaryExpression unary, FlowState state)
	{
		FlowAnalyzeExpression(unary.Context, state);
		FlowAnalyzeExpression(unary.Operand, state);

		if (unary.Operator == UnaryOperator.Throw)
		{
			string thrownType = unary.Operand?.ResolvedType ?? ErrorType;
			HandleThrownValue(thrownType, unary.SourceSyntax, state);
			state.Reachable = false;
		}
	}

	void FlowAnalyzeAssignmentExpression(AssignmentExpression assignment, FlowState state)
	{
		if (assignment.Operator != AssignmentOperator.Assign)
			FlowAnalyzeExpression(assignment.Target, state);

		FlowAnalyzeExpression(assignment.Value, state);
		AssignExpressionTarget(assignment.Target, state);
	}

	void FlowAnalyzeCallExpression(CallExpression call, FlowState state)
	{
		FlowAnalyzeExpression(call.Target, state);
		FlowAnalyzeArguments(call.Arguments, state);

		if (!callTargets.TryGetValue(call, out FunctionDefinition? function))
			return;

		string? thrownType = GetFunctionThrownType(function);
		if (thrownType is null)
			return;

		bool hasCatchArgument = false;
		foreach (ArgumentExpression argument in call.Arguments)
		{
			if (argument.Modifier == ArgumentModifier.Catch)
			{
				hasCatchArgument = true;
				break;
			}
		}

		if (!hasCatchArgument)
			HandleThrownValue(thrownType, call.SourceSyntax, state, exitsCurrentPath: false);
	}

	void FlowAnalyzeArguments(List<ArgumentExpression> arguments, FlowState state)
	{
		foreach (ArgumentExpression argument in arguments)
			FlowAnalyzeArgument(argument, state);

		foreach (ArgumentExpression argument in arguments)
		{
			if (argument.Target is not null)
				DeclareTargets(argument.Target, state, assigned: true);
			else if (argument.Modifier is ArgumentModifier.Out or ArgumentModifier.Catch)
				AssignExpressionTarget(argument.Value, state);
		}
	}

	void FlowAnalyzeArgument(ArgumentExpression argument, FlowState state)
	{
		if (argument.Target is not null || argument.Modifier is ArgumentModifier.Out or ArgumentModifier.Catch)
			return;

		FlowAnalyzeExpression(argument.Value, state);
	}

	void AssignExpressionTarget(Expression? target, FlowState state)
	{
		switch (target)
		{
			case NamedExpression named when !string.IsNullOrWhiteSpace(named.Name) && named.Name != "_":
				state.Assign(named.Name);
				break;

			case ParenthesizedExpression parenthesized:
				AssignExpressionTarget(parenthesized.Expression, state);
				break;

			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
					AssignExpressionTarget(item.Expression, state);
				break;

			case MemberExpression member:
				FlowAnalyzeExpression(member.Target, state);
				break;

			case MemberReferenceExpression member:
				FlowAnalyzeExpression(member.Target, state);
				break;

			case IndexExpression index:
				FlowAnalyzeExpression(index.Target, state);
				FlowAnalyzeArguments(index.Arguments, state);
				break;
		}
	}

	void CheckVariableRead(NamedExpression named, FlowState state)
	{
		if (named.Name == "_")
			return;

		if (!state.IsDeclared(named.Name) || state.IsAssigned(named.Name))
			return;

		Report(GetRange(named.SourceSyntax), $"Variable '{named.Name}' must be assigned before it is read.");
	}

	void CheckOutParametersAssigned(FunctionDefinition function, FlowState state, SyntaxNode? syntax)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier != ParameterModifier.Out || string.IsNullOrWhiteSpace(parameter.Name))
				continue;

			if (!state.IsAssigned(parameter.Name))
				Report(GetRange(syntax ?? parameter.SourceSyntax), $"Out parameter '{parameter.Name}' must be assigned before returning.");
		}
	}

	void HandleThrownValue(string thrownType, SyntaxNode? syntax, FlowState state, bool exitsCurrentPath = true)
	{
		if (thrownType == ErrorType)
			return;

		foreach (string handlerType in state.Handlers)
		{
			if (CanImplicitlyConvert(thrownType, handlerType))
				return;
		}

		string? functionThrownType = GetFunctionThrownType(state.Function);
		if (functionThrownType is not null && CanImplicitlyConvert(thrownType, functionThrownType))
			return;

		Report(GetRange(syntax), $"Thrown value of type '{thrownType}' must be caught or rethrown by a compatible thrown result.");
		if (exitsCurrentPath)
			state.Reachable = false;
	}

	bool FunctionRequiresReturn(FunctionDefinition function)
	{
		if (IsLifecycleFunction(function))
			return false;

		string returnType = function.ResolvedType ?? ErrorType;
		return returnType != "void" && !returnType.StartsWith("thrown(", StringComparison.Ordinal);
	}

	static bool IsLifecycleFunction(FunctionDefinition function)
	{
		return function.Modifier == FunctionModifier.Constructor || IsDestructorFunction(function)
			|| function.Name is InitNewMethodName or DeleteMethodName;
	}

	static bool IsConstructorLikeFunction(FunctionDefinition function)
	{
		return function.Modifier == FunctionModifier.Constructor || function.Name == InitNewMethodName;
	}

	string? GetFunctionThrownType(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier == ParameterModifier.Thrown)
				return parameter.ResolvedType ?? ErrorType;
		}

		string? returnType = function.ReturnType?.ResolvedType;
		if (returnType is not null && returnType.StartsWith("thrown(", StringComparison.Ordinal) && returnType.EndsWith(")", StringComparison.Ordinal))
			return returnType["thrown(".Length..^1];

		return null;
	}

	sealed class FlowState
	{
		readonly HashSet<string> declared;
		readonly HashSet<string> assigned;

		public FlowState(FunctionDefinition function, BodyScope bodyScope)
		{
			Function = function;
			BodyScope = bodyScope;
			declared = [];
			assigned = [];
		}

		FlowState(FunctionDefinition function, BodyScope bodyScope, HashSet<string> declared, HashSet<string> assigned, bool reachable, List<string> handlers)
		{
			Function = function;
			BodyScope = bodyScope;
			this.declared = declared;
			this.assigned = assigned;
			Reachable = reachable;
			Handlers = handlers;
		}

		public FunctionDefinition Function { get; }
		public BodyScope BodyScope { get; }
		public bool Reachable { get; set; } = true;
		public List<string> Handlers { get; } = [];

		public void Declare(string name, bool isAssigned)
		{
			declared.Add(name);
			if (isAssigned)
				assigned.Add(name);
			else
				assigned.Remove(name);
		}

		public void Assign(string name)
		{
			if (declared.Contains(name))
				assigned.Add(name);
		}

		public bool IsDeclared(string name)
		{
			return declared.Contains(name);
		}

		public bool IsAssigned(string name)
		{
			return assigned.Contains(name);
		}

		public FlowState Clone()
		{
			return new FlowState(Function, BodyScope, [.. declared], [.. assigned], Reachable, [.. Handlers]);
		}

		public void MergeBranches(FlowState first, FlowState second)
		{
			MergeBranches([first, second]);
		}

		public void MergeBranches(List<FlowState> branches)
		{
			Reachable = false;
			declared.Clear();
			assigned.Clear();

			bool firstReachable = true;
			foreach (FlowState branch in branches)
			{
				if (!branch.Reachable)
					continue;

				if (firstReachable)
				{
					foreach (string name in branch.declared)
						declared.Add(name);
					foreach (string name in branch.assigned)
						assigned.Add(name);
					firstReachable = false;
				}
				else
				{
					declared.IntersectWith(branch.declared);
					assigned.IntersectWith(branch.assigned);
				}

				Reachable = true;
			}
		}
	}
}
