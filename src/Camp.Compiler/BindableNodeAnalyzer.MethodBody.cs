using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly Dictionary<Expression, bool> expressionConstants = [];
	readonly Dictionary<CallExpression, FunctionDefinition> callTargets = [];
	readonly Dictionary<ConstructionExpression, FunctionDefinition> constructionTargets = [];
	readonly Dictionary<CallExpression, List<ParameterDefinition>> callableInvocationParameters = [];
	readonly Dictionary<CallExpression, Dictionary<string, string>> callGenericSubstitutions = [];
	readonly Dictionary<FunctionDefinition, Dictionary<string, LabelStatement>> functionLabels = [];

	void AnalyzeMethodBody(FunctionDefinition function, AnalysisScope typeAndMethodScope, TypeDefinition? containingType)
	{
		if (function.Body is null)
			return;

		BodyScope scope = new(null, function, containingType);
		scope.CurrentFunctionReturnType = IsLifecycleFunction(function) ? "void" : function.ResolvedType ?? ErrorType;
		scope.CurrentFunctionSourceReturnType = IsLifecycleFunction(function) ? "void" : FormatTypeReference(function.ReturnType);
		scope.CurrentIteratorElementType = function.IteratorKind == IteratorKind.None ? null : GetIteratorElementType(function.ReturnType);

		foreach (ParameterDefinition parameter in function.Parameters)
		{
			InitializeParameterLifetimeFacts(parameter, typeAndMethodScope);
			if (!string.IsNullOrWhiteSpace(parameter.Name))
				RegisterBodySymbol(scope, parameter.Name, parameter.ResolvedType ?? ErrorType, parameter, parameter.Type, parameter.ResolvedType);
		}

		function.Body.ResolvedType = "void";
		BodyAnalyzeBlock(function.Body.Statements, scope, typeAndMethodScope);
		CollectAsyncAwaitSites(function);
		BindFunctionLabels(function);
		ValidateBaseConstructorInvocation(function, containingType);
		FlowAnalyzeFunctionBody(function, scope);
	}

	void CollectAsyncAwaitSites(FunctionDefinition function)
	{
		function.AwaitSites.Clear();
		if (!function.IsAsync || function.Body is null)
			return;
		foreach (Statement statement in function.Body.Statements)
			CollectAsyncAwaitSites(statement, function.AwaitSites);
	}

	void CollectAsyncAwaitSites(Statement? statement, List<UnaryExpression> sites)
	{
		switch (statement)
		{
			case null:
				return;
			case BlockStatement block:
				foreach (Statement child in block.Statements)
					CollectAsyncAwaitSites(child, sites);
				break;
			case ExpressionStatement expression:
				CollectAsyncAwaitSites(expression.Expression, sites);
				break;
			case DeclarationStatement declaration:
				CollectAsyncAwaitSites(declaration.InitialValue, sites);
				break;
			case IfStatement ifStatement:
				CollectAsyncAwaitSites(ifStatement.Condition, sites);
				CollectAsyncAwaitSites(ifStatement.Body, sites);
				CollectAsyncAwaitSites(ifStatement.ElseBody, sites);
				break;
			case WhileStatement whileStatement:
				CollectAsyncAwaitSites(whileStatement.Condition, sites);
				CollectAsyncAwaitSites(whileStatement.Body, sites);
				break;
			case DoWhileStatement doWhile:
				CollectAsyncAwaitSites(doWhile.Body, sites);
				CollectAsyncAwaitSites(doWhile.Condition, sites);
				break;
			case ForStatement forStatement:
				CollectAsyncAwaitSites(forStatement.Condition.Declaration, sites);
				foreach (Expression? clause in forStatement.Condition.Clauses)
					CollectAsyncAwaitSites(clause, sites);
				CollectAsyncAwaitSites(forStatement.Body, sites);
				break;
			case ForeachStatement foreachStatement:
				CollectAsyncAwaitSites(foreachStatement.Source, sites);
				CollectAsyncAwaitSites(foreachStatement.Body, sites);
				break;
			case SwitchStatement switchStatement:
				CollectAsyncAwaitSites(switchStatement.Expression, sites);
				foreach (Statement child in switchStatement.Statements)
					CollectAsyncAwaitSites(child, sites);
				break;
			case CaseStatement caseStatement:
				CollectAsyncAwaitSites(caseStatement.Expression, sites);
				break;
			case ReturnStatement returnStatement:
				CollectAsyncAwaitSites(returnStatement.Expression, sites);
				break;
			case YieldStatement yieldStatement:
				CollectAsyncAwaitSites(yieldStatement.Expression, sites);
				break;
			case DeleteStatement deleteStatement:
				CollectAsyncAwaitSites(deleteStatement.Expression, sites);
				break;
			case TryStatement tryStatement:
				CollectAsyncAwaitSites(tryStatement.Body, sites);
				foreach (CatchStatement catchStatement in tryStatement.Catches)
					CollectAsyncAwaitSites(catchStatement, sites);
				CollectAsyncAwaitSites(tryStatement.Finally, sites);
				break;
			case CatchStatement catchStatement:
				CollectAsyncAwaitSites(catchStatement.Body, sites);
				break;
			case FinallyStatement finallyStatement:
				CollectAsyncAwaitSites(finallyStatement.Body, sites);
				break;
			case WithinStatement withinStatement:
				CollectAsyncAwaitSites(withinStatement.Allocator, sites);
				CollectAsyncAwaitSites(withinStatement.Body, sites);
				break;
		}
	}

	void CollectAsyncAwaitSites(Expression? expression, List<UnaryExpression> sites)
	{
		switch (expression)
		{
			case null:
				return;
			case UnaryExpression { Operator: UnaryOperator.Await } awaitExpression:
				sites.Add(awaitExpression);
				CollectAsyncAwaitSites(awaitExpression.Operand, sites);
				CollectAsyncAwaitSites(awaitExpression.Context, sites);
				break;
			case UnaryExpression unary:
				CollectAsyncAwaitSites(unary.Operand, sites);
				CollectAsyncAwaitSites(unary.Context, sites);
				break;
			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
					CollectAsyncAwaitSites(item.Expression, sites);
				break;
			case ArrayExpression array:
				foreach (Expression element in array.Elements)
					CollectAsyncAwaitSites(element, sites);
				break;
			case InitializerExpression initializer:
				foreach (InitializerItem item in initializer.Items)
					CollectAsyncAwaitSites(item.Expression, sites);
				break;
			case ParenthesizedExpression parenthesized:
				CollectAsyncAwaitSites(parenthesized.Expression, sites);
				break;
			case CastExpression cast:
				CollectAsyncAwaitSites(cast.Expression, sites);
				break;
			case ConstructionExpression construction:
				foreach (ArgumentExpression argument in construction.Arguments)
					CollectAsyncAwaitSites(argument, sites);
				CollectAsyncAwaitSites(construction.ElementCount, sites);
				CollectAsyncAwaitSites(construction.Initializer, sites);
				break;
			case WithinExpression within:
				CollectAsyncAwaitSites(within.Context, sites);
				CollectAsyncAwaitSites(within.Expression, sites);
				break;
			case LambdaExpression lambda:
				CollectAsyncAwaitSites(lambda.Body, sites);
				break;
			case ArgumentExpression argument:
				CollectAsyncAwaitSites(argument.Value, sites);
				break;
			case CallExpression call:
				CollectAsyncAwaitSites(call.Target, sites);
				foreach (ArgumentExpression argument in call.Arguments)
					CollectAsyncAwaitSites(argument, sites);
				break;
			case IndexExpression index:
				CollectAsyncAwaitSites(index.Target, sites);
				foreach (ArgumentExpression argument in index.Arguments)
					CollectAsyncAwaitSites(argument, sites);
				break;
			case MemberExpression member:
				CollectAsyncAwaitSites(member.Target, sites);
				break;
			case MemberReferenceExpression member:
				CollectAsyncAwaitSites(member.Target, sites);
				break;
			case NamelessIndexerExpression indexer:
				CollectAsyncAwaitSites(indexer.Target, sites);
				foreach (ArgumentExpression argument in indexer.Arguments)
					CollectAsyncAwaitSites(argument, sites);
				break;
			case PostfixUpdateExpression postfix:
				CollectAsyncAwaitSites(postfix.Expression, sites);
				break;
			case FinallyDeleteExpression finallyDelete:
				CollectAsyncAwaitSites(finallyDelete.Expression, sites);
				break;
			case BinaryExpression binary:
				CollectAsyncAwaitSites(binary.Left, sites);
				CollectAsyncAwaitSites(binary.Right, sites);
				break;
			case AssignmentExpression assignment:
				CollectAsyncAwaitSites(assignment.Target, sites);
				CollectAsyncAwaitSites(assignment.Value, sites);
				break;
			case ConditionalExpression conditional:
				CollectAsyncAwaitSites(conditional.Condition, sites);
				CollectAsyncAwaitSites(conditional.WhenTrue, sites);
				CollectAsyncAwaitSites(conditional.WhenFalse, sites);
				break;
			case RangeExpression range:
				CollectAsyncAwaitSites(range.Start, sites);
				CollectAsyncAwaitSites(range.End, sites);
				break;
		}
	}

	void AnalyzeConstantExpression(Expression? expression, AnalysisScope typeScope, string context, string? targetType = null)
	{
		if (expression is null)
			return;

		BodyScope scope = new(null, new FunctionDefinition { Name = "#constant", ResolvedType = ErrorType }, containingType: null);
		BodyAnalyzeExpression(expression, scope, typeScope, targetType);
		if (!IsConstant(expression))
			Report(GetRange(expression.SourceSyntax), $"{context} must be a constant expression.");
	}

	void BodyAnalyzeBlock(List<Statement> statements, BodyScope scope, AnalysisScope typeScope)
	{
		BodyScope blockScope = new(scope, scope.CurrentFunction, scope.ContainingType)
		{
			CurrentFunctionReturnType = scope.CurrentFunctionReturnType,
			CurrentFunctionSourceReturnType = scope.CurrentFunctionSourceReturnType,
			CurrentIteratorElementType = scope.CurrentIteratorElementType
		};

		foreach (Statement statement in statements)
			BodyAnalyzeStatement(statement, blockScope, typeScope);
	}

	void BodyAnalyzeStatement(Statement statement, BodyScope scope, AnalysisScope typeScope)
	{
		statement.ResolvedType = "void";

		switch (statement)
		{
			case BlockStatement block:
				BodyAnalyzeBlock(block.Statements, scope, typeScope);
				break;

			case EmptyStatement:
			case BreakStatement:
			case ContinueStatement:
			case DefaultStatement:
				statement.ResolvedType = "void";
				break;

			case ExpressionStatement expression:
				BodyAnalyzeExpression(expression.Expression, scope, typeScope);
				break;

			case DeclarationStatement declaration:
				BodyAnalyzeDeclarationStatement(declaration, scope, typeScope);
				break;

			case IfStatement ifStatement:
				RequireExpressionType("bool", BodyAnalyzeExpression(ifStatement.Condition, scope, typeScope), ifStatement.Condition?.SourceSyntax, "If condition");
				BodyAnalyzeOptionalStatement(ifStatement.Body, scope, typeScope);
				BodyAnalyzeOptionalStatement(ifStatement.ElseBody, scope, typeScope);
				break;

			case WhileStatement whileStatement:
				RequireExpressionType("bool", BodyAnalyzeExpression(whileStatement.Condition, scope, typeScope), whileStatement.Condition?.SourceSyntax, "While condition");
				BodyAnalyzeOptionalStatement(whileStatement.Body, scope, typeScope);
				break;

			case DoWhileStatement doWhile:
				BodyAnalyzeOptionalStatement(doWhile.Body, scope, typeScope);
				RequireExpressionType("bool", BodyAnalyzeExpression(doWhile.Condition, scope, typeScope), doWhile.Condition?.SourceSyntax, "Do-while condition");
				break;

			case ForStatement forStatement:
				BodyAnalyzeForCondition(forStatement.Condition, scope, typeScope);
				BodyAnalyzeOptionalStatement(forStatement.Body, scope, typeScope);
				break;

			case ForeachStatement foreachStatement:
				BodyAnalyzeForeachStatement(foreachStatement, scope, typeScope);
				break;

			case SwitchStatement switchStatement:
				BodyAnalyzeSwitchStatement(switchStatement, scope, typeScope);
				switchStatement.ResolvedType = "void";
				break;

			case CaseStatement caseStatement:
				BodyAnalyzeExpression(caseStatement.Expression, scope, typeScope);
				if (!IsConstant(caseStatement.Expression))
					Report(GetRange(caseStatement.SourceSyntax), "Switch case expressions must be constant.");
				caseStatement.ResolvedType = "void";
				break;

			case LabelStatement:
				break;

			case GotoStatement:
				break;

			case ReturnStatement returnStatement:
			{
				bool returnsThis = scope.CurrentFunction.ReturnType is ThisTypeReference;
				string returnTargetSourceType = returnsThis ? scope.CurrentFunctionReturnType : scope.CurrentFunctionSourceReturnType ?? scope.CurrentFunctionReturnType;
				string returnType = returnStatement.Expression is null ? "void" : BodyAnalyzeExpression(returnStatement.Expression, scope, typeScope, returnTargetSourceType);
				if (returnsThis && !IsValidThisReturnExpression(returnStatement.Expression))
					Report(GetRange(returnStatement.Expression?.SourceSyntax ?? returnStatement.SourceSyntax), "A method returning 'this' must return 'this' or a chain of 'this'-returning instance calls on 'this'.");
				if (IsDirectCapturingLambda(returnStatement.Expression, scope) && !IsEscapedDelegateLambdaTarget(scope.CurrentFunctionReturnType))
					Report(GetRange(returnStatement.Expression?.SourceSyntax ?? returnStatement.SourceSyntax), "Capturing scoped lambdas cannot be returned.");
				if (RequiresAnyGenericCopy(scope.CurrentFunctionReturnType, returnStatement.Expression, scope))
					ReportAnyGenericCopy(returnStatement.Expression?.SourceSyntax ?? returnStatement.SourceSyntax);
				bool fixedArraySpanEscape = EscapesLocalFixedArraySpan(scope.CurrentFunctionReturnType, returnStatement.Expression);
				if (fixedArraySpanEscape)
					Report(GetRange(returnStatement.Expression?.SourceSyntax ?? returnStatement.SourceSyntax), "Cannot return a span view to local fixed-size array storage.");
				string returnTargetType = GetLifetimeStructuralTargetType(returnTargetSourceType, returnStatement.Expression);
				CheckAssignable(returnTargetType, returnType, returnStatement.Expression?.SourceSyntax ?? returnStatement.SourceSyntax, "Return expression");
				CheckConstOfProducedResult(scope.CurrentFunction.ReturnType, returnStatement.Expression, returnStatement.Expression?.SourceSyntax ?? returnStatement.SourceSyntax, "Return expression");
				if (scope.CurrentFunctionSourceReturnType is string sourceReturnType
					&& sourceReturnType != FormatTypeReference(scope.CurrentFunction.ReturnType))
					CheckConstOfProducedResult(sourceReturnType, returnStatement.Expression, returnStatement.Expression?.SourceSyntax ?? returnStatement.SourceSyntax, "Return expression");
				if (!fixedArraySpanEscape)
					CheckLifetimeResult(returnStatement.Expression, returnStatement.Expression?.SourceSyntax ?? returnStatement.SourceSyntax, scope, "Return expression");
				break;
			}

			case YieldStatement yieldStatement:
			{
				string expected = scope.CurrentIteratorElementType ?? ErrorType;
				if (scope.CurrentIteratorElementType is null)
					Report(GetRange(yieldStatement.SourceSyntax), "Yield statements may only appear in iterator functions.");
				string yieldedType = BodyAnalyzeExpression(yieldStatement.Expression, scope, typeScope, expected);
				bool fixedArraySpanEscape = EscapesLocalFixedArraySpan(expected, yieldStatement.Expression);
				if (fixedArraySpanEscape)
					Report(GetRange(yieldStatement.Expression?.SourceSyntax ?? yieldStatement.SourceSyntax), "Cannot yield a span view to local fixed-size array storage.");
				string yieldTargetType = GetLifetimeStructuralTargetType(expected, yieldStatement.Expression);
				CheckAssignable(yieldTargetType, yieldedType, yieldStatement.Expression?.SourceSyntax ?? yieldStatement.SourceSyntax, "Yield expression");
				if (!fixedArraySpanEscape)
					CheckLifetimeResult(yieldStatement.Expression, yieldStatement.Expression?.SourceSyntax ?? yieldStatement.SourceSyntax, scope, "Yield expression");
				CheckLifetimeYield(yieldStatement.Expression, yieldStatement.Expression?.SourceSyntax ?? yieldStatement.SourceSyntax, scope);
				break;
			}

			case DeleteStatement deleteStatement:
				if (IsBaseDeleteExpression(deleteStatement.Expression))
					AnalyzeBaseDeleteExpression(deleteStatement.Expression, scope);
				else
				{
					string deleteType = BodyAnalyzeExpression(deleteStatement.Expression, scope, typeScope);
					ValidateExternClassDelete(deleteStatement.Expression, deleteType);
					CheckLifetimeDeleteAgainstFree(deleteStatement.Expression, deleteStatement.Expression?.SourceSyntax ?? deleteStatement.SourceSyntax, scope);
				}
				break;

			case TryStatement tryStatement:
				BodyAnalyzeOptionalStatement(tryStatement.Body, scope, typeScope);
				foreach (CatchStatement catchStatement in tryStatement.Catches)
					BodyAnalyzeStatement(catchStatement, scope, typeScope);
				BodyAnalyzeOptionalStatement(tryStatement.Finally, scope, typeScope);
				break;

			case CatchStatement catchStatement:
				BodyAnalyzeDeclarationTarget(catchStatement.Target, scope, typeScope, targetType: ErrorType);
				BodyAnalyzeOptionalStatement(catchStatement.Body, scope, typeScope);
				break;

			case FinallyStatement finallyStatement:
				BodyAnalyzeOptionalStatement(finallyStatement.Body, scope, typeScope);
				break;

			case WithinStatement withinStatement:
				BodyAnalyzeExpression(withinStatement.Allocator, scope, typeScope);
				BodyAnalyzeOptionalStatement(withinStatement.Body, scope, typeScope);
				break;
		}
	}

	bool IsValidThisReturnExpression(Expression? expression)
	{
		expression = UnwrapParenthesizedExpression(expression);
		if (expression is ThisExpression)
			return true;

		return expression is CallExpression call
			&& callTargets.TryGetValue(call, out FunctionDefinition? function)
			&& function.ReturnType is ThisTypeReference
			&& IsThisReturnCallTarget(call.Target);
	}

	bool IsThisReturnCallTarget(Expression? target)
	{
		target = UnwrapParenthesizedExpression(target);
		return target switch
		{
			MemberExpression member => IsValidThisReturnExpression(member.Target),
			MemberReferenceExpression member => IsValidThisReturnExpression(member.Target),
			NamedExpression => true,
			_ => false
		};
	}

	static Expression? UnwrapParenthesizedExpression(Expression? expression)
	{
		while (expression is ParenthesizedExpression parenthesized)
			expression = parenthesized.Expression;
		return expression;
	}

	void BodyAnalyzeOptionalStatement(Statement? statement, BodyScope scope, AnalysisScope typeScope)
	{
		if (statement is not null)
			BodyAnalyzeStatement(statement, scope, typeScope);
	}

	void BindFunctionLabels(FunctionDefinition function)
	{
		if (function.Body is null)
			return;

		functionLabels[function] = BindStatementLabels(function.Body.Statements);
	}

	Dictionary<string, LabelStatement> BindStatementLabels(List<Statement> statements)
	{
		Dictionary<string, LabelStatement> labels = new(StringComparer.Ordinal);
		List<GotoStatement> gotos = [];
		CollectFunctionLabels(statements, labels, gotos);

		foreach (GotoStatement gotoStatement in gotos)
		{
			if (string.IsNullOrWhiteSpace(gotoStatement.TargetName))
				continue;

			if (labels.TryGetValue(gotoStatement.TargetName, out LabelStatement? label))
				gotoStatement.Target = label;
			else
				Report(GetGotoTargetNameRange(gotoStatement.SourceSyntax), $"Label '{gotoStatement.TargetName}' could not be found in this function.");
		}

		return labels;
	}

	void CollectFunctionLabels(List<Statement> statements, Dictionary<string, LabelStatement> labels, List<GotoStatement> gotos)
	{
		foreach (Statement statement in statements)
			CollectFunctionLabels(statement, labels, gotos);
	}

	void CollectFunctionLabels(Statement? statement, Dictionary<string, LabelStatement> labels, List<GotoStatement> gotos)
	{
		switch (statement)
		{
			case null:
				break;

			case BlockStatement block:
				CollectFunctionLabels(block.Statements, labels, gotos);
				break;

			case LabelStatement label:
				if (string.IsNullOrWhiteSpace(label.Name))
					break;
				if (!labels.TryAdd(label.Name, label))
					Report(GetLabelNameRange(label.SourceSyntax), $"Duplicate label '{label.Name}'.");
				break;

			case GotoStatement gotoStatement:
				gotos.Add(gotoStatement);
				break;

			case IfStatement ifStatement:
				CollectFunctionLabels(ifStatement.Body, labels, gotos);
				CollectFunctionLabels(ifStatement.ElseBody, labels, gotos);
				break;

			case WhileStatement whileStatement:
				CollectFunctionLabels(whileStatement.Body, labels, gotos);
				break;

			case DoWhileStatement doWhile:
				CollectFunctionLabels(doWhile.Body, labels, gotos);
				break;

			case ForStatement forStatement:
				CollectFunctionLabels(forStatement.Body, labels, gotos);
				break;

			case ForeachStatement foreachStatement:
				CollectFunctionLabels(foreachStatement.Body, labels, gotos);
				break;

			case SwitchStatement switchStatement:
				CollectFunctionLabels(switchStatement.Statements, labels, gotos);
				break;

			case TryStatement tryStatement:
				CollectFunctionLabels(tryStatement.Body, labels, gotos);
				foreach (CatchStatement catchStatement in tryStatement.Catches)
					CollectFunctionLabels(catchStatement.Body, labels, gotos);
				CollectFunctionLabels(tryStatement.Finally, labels, gotos);
				break;

			case CatchStatement catchStatement:
				CollectFunctionLabels(catchStatement.Body, labels, gotos);
				break;

			case FinallyStatement finallyStatement:
				CollectFunctionLabels(finallyStatement.Body, labels, gotos);
				break;

			case WithinStatement withinStatement:
				CollectFunctionLabels(withinStatement.Body, labels, gotos);
				break;
		}
	}

	static bool IsBaseDeleteExpression(Expression? expression)
	{
		return expression is NamedExpression { Qualifiers.Count: 0, Name: "base" };
	}

	void AnalyzeBaseDeleteExpression(Expression? expression, BodyScope scope)
	{
		if (scope.ContainingType is not ClassDefinition containingClass)
		{
			Report(GetRange(expression?.SourceSyntax), "delete base is valid only in a class method.");
			return;
		}

		ClassDefinition? baseClass = GetDirectBaseClass(containingClass);
		if (baseClass is null)
		{
			Report(GetRange(expression?.SourceSyntax), $"Class '{containingClass.Name}' does not have a base class.");
			return;
		}

		if (expression is not null)
			expression.ResolvedType = baseClass.Name;
	}

	void BodyAnalyzeDeclarationStatement(DeclarationStatement declaration, BodyScope scope, AnalysisScope typeScope)
	{
		AnalyzeOptionalType(declaration.Target.Type, typeScope);
		ValidateNoLifetimeAnnotation(declaration.Target.Type, declaration.Target.Type?.SourceSyntax ?? declaration.Target.SourceSyntax ?? declaration.SourceSyntax, "local variable types");
		TryRewriteOmittedOutDeconstruction(declaration, scope, typeScope);
		string targetType = declaration.Target.Type is AutoTypeReference or null
			? TargetType
			: declaration.Target.Type.ResolvedType ?? ErrorType;
		string initialTargetType = declaration.InitialValue is LambdaExpression && declaration.Target.Type is not null and not AutoTypeReference
			? FormatLambdaTargetType(declaration.Target.Type)
			: targetType;
		string initialType = declaration.InitialValue is null ? TargetType : BodyAnalyzeExpression(declaration.InitialValue, scope, typeScope, initialTargetType);
		if (declaration.Target.Type is AutoTypeReference
			&& TryGetImplicitIteratorProtocolType(declaration.InitialValue, initialType, out string iteratorProtocolType))
			initialType = iteratorProtocolType;
		if (declaration.InitialValue is not null
			&& declaration.Target.Type is AutoTypeReference or null
			&& initialType == "void")
		{
			Report(GetRange(declaration.InitialValue.SourceSyntax ?? declaration.SourceSyntax), "Auto declaration cannot infer a type from a void expression.", DiagnosticCodes.AutoCannotInferVoid);
		}
		if (declaration.InitialValue is InitializerExpression
			&& declaration.Target.Type is AutoTypeReference or null
			&& declaration.Target.Names.Count == 1)
		{
			Report(GetRange(declaration.InitialValue.SourceSyntax ?? declaration.SourceSyntax), "Initializer expression requires a target type.");
		}
		if (declaration.InitialValue is not null
			&& declaration.Target.Type is AutoTypeReference or null
			&& TryGetFixedArrayShape(initialType, out _, out _))
		{
			Report(GetRange(declaration.InitialValue.SourceSyntax ?? declaration.SourceSyntax), "Fixed-size arrays cannot be inferred by value; declare a span, pointer, or fixed storage target explicitly.");
		}
		if (declaration.Target.Names.Count > 1 && TryAnalyzeDeconstructionTarget(declaration.Target, initialType, scope))
			return;

		BodyAnalyzeDeclarationTarget(declaration.Target, scope, typeScope, initialType);
		ValidateFixedStorageMarker(declaration.Target.Type, declaration.IsFixedStorage, declaration.Target.Type?.SourceSyntax ?? declaration.Target.SourceSyntax ?? declaration.SourceSyntax);
		ValidateNoDirectExternClassType(declaration.Target.Type, declaration.Target.Type?.SourceSyntax ?? declaration.Target.SourceSyntax ?? declaration.SourceSyntax, "local variable storage");
		ValidateNoExternClassArrayElement(declaration.Target.Type, declaration.Target.Type?.SourceSyntax ?? declaration.Target.SourceSyntax ?? declaration.SourceSyntax);

		if (declaration.InitialValue is not null && IsDirectFixedArrayType(declaration.Target.Type) && !IsValidFixedStorageInitializer(declaration.Target.Type, declaration.InitialValue))
			Report(GetRange(declaration.InitialValue.SourceSyntax ?? declaration.SourceSyntax), "Fixed-size arrays cannot be copied by value; initialize them with an array literal, string literal, or default.");
		else if (declaration.InitialValue is not null && RequiresAnyGenericCopy(declaration.Target.ResolvedType ?? ErrorType, declaration.InitialValue, scope))
			ReportAnyGenericCopy(declaration.InitialValue.SourceSyntax ?? declaration.SourceSyntax);
		else if (declaration.InitialValue is not null && !IsValidFixedStorageInitializer(declaration.Target.Type, declaration.InitialValue))
		{
			CheckAssignable(declaration.Target.ResolvedType ?? ErrorType, initialType, declaration.InitialValue.SourceSyntax, "Declaration initializer");
			if (ContainsConstOfTypeReference(declaration.Target.Type))
				CheckConstOfProducedResult(declaration.Target.Type, declaration.InitialValue, declaration.InitialValue.SourceSyntax ?? declaration.SourceSyntax, "Declaration initializer");
		}

		InitializeLocalLifetimeFacts(declaration, typeScope);
	}

	bool RequiresAnyGenericCopy(string targetType, Expression? value, BodyScope scope)
	{
		if (value is null or DefaultExpression)
			return false;
		string type = StripTopLevelValueQualifiers(targetType);
		return FindBodyGenericParameter(scope, type) is GenericParameter { Constraint: AnyTypeReference };
	}

	bool RequiresAnyGenericDefaultFillSizeOf(string targetType, Expression? value, BodyScope scope)
	{
		if (value is not DefaultExpression)
			return false;
		string type = BaseTypeName(StripTopLevelValueQualifiers(targetType));
		return FindBodyGenericParameter(scope, type) is GenericParameter { Constraint: AnyTypeReference }
			&& !HasSizeOfCapability(scope, type);
	}

	void ReportAnyGenericCopy(SyntaxNode? syntax)
	{
		Report(GetRange(syntax), "T: any is non-copying. Use T: copyable plus sizeof(T) for generic value-copy operations.");
	}

	void ReportAnyGenericDefaultFillNeedsSizeOf(SyntaxNode? syntax)
	{
		Report(GetRange(syntax), "Default-filling erased generic storage requires sizeof(T).");
	}

	static bool IsValidFixedStorageInitializer(TypeReference? targetType, Expression value)
	{
		return IsDirectFixedArrayType(targetType)
			&& value is ArrayExpression or DefaultExpression or LiteralExpression { Kind: LiteralKind.String } or NameOfExpression;
	}

	static bool IsValidFixedStorageAssignmentValue(string targetType, Expression? value)
	{
		return value is ArrayExpression or DefaultExpression
			|| value is LiteralExpression { Kind: LiteralKind.String } && IsFixedCharacterArrayType(targetType)
			|| value is NameOfExpression && IsFixedCharacterArrayType(targetType);
	}

	bool TryGetImplicitIteratorProtocolType(Expression? expression, string initialType, out string iteratorProtocolType)
	{
		iteratorProtocolType = "";
		if (expression is not CallExpression call
			|| !callTargets.TryGetValue(call, out FunctionDefinition? function)
			|| !generatedIteratorFactories.Contains(function))
			return false;

		string stateType = TryGetPointerElementType(initialType) ?? initialType;
		if (!TryFindIteratorNextMethod(stateType, out _, out string elementType) || string.IsNullOrWhiteSpace(elementType) || elementType == ErrorType)
			return false;

		iteratorProtocolType = "iter " + elementType;
		return true;
	}

	bool IsDirectCapturingLambda(Expression? expression, BodyScope scope)
	{
		return expression is LambdaExpression lambda
			&& LambdaHasCaptures(lambda, scope.CurrentFunction, scope.ContainingType);
	}

	bool EscapesLocalFixedArraySpan(string targetType, Expression? expression)
	{
		if (!TryParseTypeShape(targetType, out TypeShape targetShape) || targetShape.Kind != TypeShapeKind.Array)
			return false;
		return ReferencesLocalFixedArrayStorage(expression);
	}

	bool ReferencesLocalFixedArrayStorage(Expression? expression)
	{
		if (expression is null)
			return false;
		if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(rewritten, expression))
			return ReferencesLocalFixedArrayStorage(rewritten);

		return expression switch
		{
			ParenthesizedExpression parenthesized => ReferencesLocalFixedArrayStorage(parenthesized.Expression),
			CastExpression cast => ReferencesLocalFixedArrayStorage(cast.Expression),
			VariableReferenceExpression { Variable: DeclarationTarget target } => TryGetFixedArrayShape(target.ResolvedType, out _, out _),
			IndexExpression index when TryParseTypeShape(index.ResolvedType, out TypeShape indexShape) && indexShape.Kind == TypeShapeKind.Array => ReferencesLocalFixedArrayStorage(index.Target),
			_ => false
		};
	}

	bool TryAnalyzeDeconstructionTarget(DeclarationTarget target, string initialType, BodyScope scope)
	{
		if (target.Names.Count <= 1)
			return false;

		Report(GetRange(target.SourceSyntax), "Declaration deconstruction is only supported for omitted out/async/iter result slots.");
		return true;
	}

	bool TryRewriteOmittedOutDeconstruction(DeclarationStatement declaration, BodyScope scope, AnalysisScope typeScope)
	{
		if (declaration.Target.Names.Count <= 1 || declaration.InitialValue is not CallExpression call)
			return false;

		FunctionDefinition? function = ResolveCallTargetAllowingOmittedOut(call, declaration.Target.Names.Count, scope, typeScope);
		if (function is null)
			return false;

		List<ParameterDefinition> callableParameters = GetCallableParameters(function.Parameters);
		int providedCount = call.Arguments.Count;
		List<ParameterDefinition> omittedOut = [];
		for (int i = providedCount; i < callableParameters.Count; i++)
		{
			if (callableParameters[i].Modifier != ParameterModifier.Out)
				return false;
			omittedOut.Add(callableParameters[i]);
		}

		bool hasReturnValue = (function.ResolvedType ?? "void") != "void";
		int expectedNames = omittedOut.Count + (hasReturnValue ? 1 : 0);
		if (expectedNames != declaration.Target.Names.Count)
			return false;

		int nameIndex = hasReturnValue ? 1 : 0;
		foreach (ParameterDefinition parameter in omittedOut)
		{
			string name = declaration.Target.Names[nameIndex++];
			DeclarationTarget target = new()
			{
				SourceSyntax = declaration.Target.SourceSyntax,
				Type = CloneType(parameter.Type),
				ResolvedType = parameter.ResolvedType
			};
			target.Names.Add(name);
			call.Arguments.Add(new ArgumentExpression
			{
				SourceSyntax = declaration.SourceSyntax,
				Modifier = ArgumentModifier.Out,
				Target = target,
				ResolvedType = parameter.ResolvedType
			});
		}

		if (hasReturnValue)
		{
			string returnName = declaration.Target.Names[0];
			declaration.Target.Names.Clear();
			declaration.Target.Names.Add(returnName);
			return true;
		}

		Report(GetRange(declaration.SourceSyntax), "Void calls with only omitted out values cannot be bound with a declaration target yet.");
		return false;
	}

	FunctionDefinition? ResolveCallTargetAllowingOmittedOut(CallExpression call, int resultCount, BodyScope scope, AnalysisScope typeScope)
	{
		List<FunctionDefinition> candidates = call.Target switch
		{
			NamedExpression named when named.Qualifiers.Count == 0 => LookupFunctions(named.Name, scope),
			MemberExpression member => LookupMemberFunctions(BodyAnalyzeExpression(member.Target, scope, typeScope), member.Name, member.SourceSyntax),
			MemberReferenceExpression { Member: FunctionDefinition function } => [function],
			MethodReferenceExpression method => method.Candidates,
			_ => []
		};

		List<FunctionDefinition> matches = [];
		foreach (FunctionDefinition function in candidates)
		{
			EnsureFunctionSignatureAnalyzed(function, typeScope);
			if (CanBindOmittedOutDeconstruction(function, call.Arguments.Count, resultCount))
				matches.Add(function);
		}

		if (matches.Count == 1)
			return matches[0];
		if (matches.Count > 1)
			Report(GetRange(call.Target?.SourceSyntax ?? call.SourceSyntax), "Multiple candidates found for omitted out deconstruction.");
		return null;
	}

	static bool CanBindOmittedOutDeconstruction(FunctionDefinition function, int providedArgumentCount, int resultCount)
	{
		List<ParameterDefinition> callableParameters = GetCallableParameters(function.Parameters);
		if (providedArgumentCount > callableParameters.Count)
			return false;

		int omittedOutCount = 0;
		for (int i = providedArgumentCount; i < callableParameters.Count; i++)
		{
			if (callableParameters[i].Modifier != ParameterModifier.Out)
				return false;
			omittedOutCount++;
		}

		bool hasReturnValue = (function.ResolvedType ?? "void") != "void";
		return omittedOutCount + (hasReturnValue ? 1 : 0) == resultCount;
	}

	static DeclarationTarget CreateDeconstructedTarget(DeclarationTarget source, string name, string type)
	{
		DeclarationTarget target = new()
		{
			SourceSyntax = source.SourceSyntax,
			ResolvedType = type
		};
		target.Names.Add(name);
		return target;
	}

	void BodyAnalyzeDeclarationTarget(DeclarationTarget target, BodyScope scope, AnalysisScope typeScope, string targetType, bool allowDiscard = false)
	{
		AnalyzeOptionalType(target.Type, typeScope);

		if (target.Type is AutoTypeReference)
			target.ResolvedType = targetType == TargetType ? ErrorType : targetType;
		else
			target.ResolvedType = target.Type?.ResolvedType ?? ErrorType;

		foreach (string name in target.Names)
		{
			if (string.IsNullOrWhiteSpace(name))
				continue;
			if (name == "_")
			{
				if (!allowDiscard)
					Report(GetDeclarationTargetNameRange(target.SourceSyntax, name), "Discard '_' may not be used as a declaration name.");
				continue;
			}

			RegisterBodySymbol(scope, name, target.ResolvedType ?? ErrorType, target, target.Type, target.ResolvedType, target.SourceSyntax);
		}
	}

	void RegisterBodySymbol(BodyScope scope, string name, string type, BindableNode node, TypeReference? sourceType, string? resolvedType, SyntaxNode? syntax = null)
	{
		if (scope.TryLookupComponent(name, out string? componentOwner))
			Report(GetDeclarationTargetNameRange(syntax ?? node.SourceSyntax, name), $"Symbol '{name}' is already declared in this scope as a component of '{componentOwner}'.");

		if (scope.Symbols.ContainsKey(name))
			Report(GetDeclarationTargetNameRange(syntax ?? node.SourceSyntax, name), $"Symbol '{name}' is already declared in this scope.");
		else
			scope.Symbols[name] = new BodySymbol(name, type, node);

		if (!TryGetParamsComponentShape(sourceType, resolvedType, name, out ParamsComponentShape componentShape))
			return;

		foreach (ParamsComponent component in componentShape.Components)
			RegisterComponentBodySymbol(scope, name, component.ExpandedName, component.ExpandedName, component.Type, node, syntax);
	}

	void RegisterComponentBodySymbol(BodyScope scope, string ownerName, string componentName, string expandedName, string componentType, BindableNode node, SyntaxNode? syntax)
	{
		if (componentName == ownerName)
			return;
		if (scope.ComponentSymbolTypes.TryGetValue(componentName, out BodyComponentSymbol existing)
			&& existing.Owner == ownerName
			&& existing.ExpandedName == expandedName)
			return;

		if (scope.Symbols.ContainsKey(componentName) || scope.TryLookupComponent(componentName, out _))
			Report(GetDeclarationTargetNameRange(syntax ?? node.SourceSyntax, ownerName), $"Symbol '{componentName}' is already declared in this scope as a component of '{ownerName}'.");
		else
		{
			scope.ComponentSymbols[componentName] = ownerName;
			scope.ComponentSymbolTypes[componentName] = new BodyComponentSymbol(componentName, expandedName, componentType, ownerName);
		}
	}

	void BodyAnalyzeForCondition(ForStatementCondition condition, BodyScope scope, AnalysisScope typeScope)
	{
		condition.ResolvedType = "bool";
		if (condition.Declaration is not null)
			BodyAnalyzeStatement(condition.Declaration, scope, typeScope);

		for (int i = 0; i < condition.Clauses.Count; i++)
		{
			Expression? clause = condition.Clauses[i];
			string clauseType = BodyAnalyzeExpression(clause, scope, typeScope);
			int conditionClauseIndex = condition.Declaration is null ? 1 : 0;
			if (i == conditionClauseIndex && clause is not null)
				RequireExpressionType("bool", clauseType, clause.SourceSyntax, "For condition");
		}
	}

	void BodyAnalyzeForeachStatement(ForeachStatement statement, BodyScope scope, AnalysisScope typeScope)
	{
		string sourceType = BodyAnalyzeExpression(statement.Source, scope, typeScope);
		if (TryGetArrayElementType(sourceType) is not null)
			RequireGenericArrayElementStride(sourceType, scope, statement.Source?.SourceSyntax, "enumerate T[]");
		string elementType = GetForeachElementType(statement, sourceType, statement.Source?.SourceSyntax);
		if (statement.Target.Names.Count != 1)
			Report(GetRange(statement.Target.SourceSyntax ?? statement.SourceSyntax), "Foreach statement must declare exactly one loop variable.");
		BodyAnalyzeDeclarationTarget(statement.Target, scope, typeScope, elementType);
		BodyAnalyzeOptionalStatement(statement.Body, scope, typeScope);
	}

	void BodyAnalyzeSwitchStatement(SwitchStatement statement, BodyScope scope, AnalysisScope typeScope)
	{
		string switchType = BodyAnalyzeExpression(statement.Expression, scope, typeScope);
		if (!IsSwitchableType(switchType))
			Report(GetRange(statement.Expression?.SourceSyntax ?? statement.SourceSyntax), "Switch expression type must be numeric, an enum, or a newtype with numeric underlying type.");

		foreach (Statement child in statement.Statements)
		{
			if (child is CaseStatement caseStatement)
			{
				BodyAnalyzeExpression(caseStatement.Expression, scope, typeScope, switchType);
				if (!IsConstant(caseStatement.Expression))
					Report(GetRange(caseStatement.SourceSyntax), "Switch case expressions must be constant.");
				string caseType = caseStatement.Expression?.ResolvedType ?? ErrorType;
				if (!CanImplicitlyConvert(caseType, switchType))
					Report(GetRange(caseStatement.Expression?.SourceSyntax ?? caseStatement.SourceSyntax), $"Switch case type '{caseType}' is not compatible with switch type '{switchType}'.");
				caseStatement.ResolvedType = "void";
			}
			else if (child is DefaultStatement defaultStatement)
			{
				defaultStatement.ResolvedType = "void";
			}
			else
				BodyAnalyzeStatement(child, scope, typeScope);
		}
	}

	string BodyAnalyzeExpression(Expression? expression, BodyScope scope, AnalysisScope typeScope, string? targetType = null)
	{
		if (expression is null)
			return ErrorType;

		string type = expression switch
		{
			LiteralExpression literal => BodyAnalyzeLiteralExpression(literal, targetType),
			NamedExpression named => BodyAnalyzeNamedExpression(named, scope, targetType),
			VariableReferenceExpression variable => BodyAnalyzeVariableReferenceExpression(variable),
			MethodReferenceExpression method => BodyAnalyzeMethodReference(method),
			TypeReferenceExpression typeReference => typeReference.Type?.ResolvedType ?? ErrorType,
			ThisExpression thisExpression => BodyAnalyzeThisExpression(thisExpression, scope),
			DefaultExpression defaultExpression => BodyAnalyzeDefaultExpression(defaultExpression, typeScope, targetType),
			GroupedExpression grouped => BodyAnalyzeGroupedExpression(grouped, scope, typeScope),
			ArrayExpression array => BodyAnalyzeArrayExpression(array, scope, typeScope, targetType),
			InitializerExpression initializer => BodyAnalyzeInitializerExpression(initializer, scope, typeScope, targetType),
			ParenthesizedExpression parenthesized => BodyAnalyzeExpression(parenthesized.Expression, scope, typeScope, targetType),
			CastExpression cast => BodyAnalyzeCastExpression(cast, scope, typeScope),
			ConstructionExpression construction => BodyAnalyzeConstructionExpression(construction, scope, typeScope, targetType),
			WithinExpression within => BodyAnalyzeWithinExpression(within, scope, typeScope, targetType),
			SizeOfExpression sizeOf => BodyAnalyzeSizeOfExpression(sizeOf, typeScope),
			VTableOfExpression vtableOf => BodyAnalyzeVTableOfExpression(vtableOf, typeScope),
			NameOfExpression nameOf => BodyAnalyzeNameOfExpression(nameOf, scope, typeScope, targetType),
			SymbolOfExpression symbolOf => BodyAnalyzeSymbolOfExpression(symbolOf),
			LambdaExpression lambda => BodyAnalyzeLambdaExpression(lambda, scope, typeScope, targetType),
			ArgumentExpression argument => BodyAnalyzeArgumentExpression(argument, scope, typeScope, targetType),
			CallExpression call => BodyAnalyzeCallExpression(call, scope, typeScope, targetType),
			IndexExpression index => BodyAnalyzeIndexExpression(index, scope, typeScope),
			MemberExpression member => BodyAnalyzeMemberExpression(member, scope, typeScope, targetType),
			MemberReferenceExpression member => member.ResolvedType ?? ErrorType,
			NamelessIndexerExpression indexer => BodyAnalyzeIndexExpression(indexer.Target, indexer.Arguments, scope, typeScope),
			UnaryExpression unary => BodyAnalyzeUnaryExpression(unary, scope, typeScope, targetType),
			PostfixUpdateExpression postfix => BodyAnalyzePostfixUpdateExpression(postfix, scope, typeScope),
			FinallyDeleteExpression finallyDelete => BodyAnalyzeExpression(finallyDelete.Expression, scope, typeScope, targetType),
			BinaryExpression binary => BodyAnalyzeBinaryExpression(binary, scope, typeScope),
			AssignmentExpression assignment => BodyAnalyzeAssignmentExpression(assignment, scope, typeScope),
			ConditionalExpression conditional => BodyAnalyzeConditionalExpression(conditional, scope, typeScope, targetType),
			RangeExpression range => BodyAnalyzeRangeExpression(range, scope, typeScope),
			_ => ErrorType
		};

		expression.ResolvedType = type;
		ApplyExpressionLifetimeFact(expression, type, scope, typeScope);
		return type;
	}

	static string BodyAnalyzeVariableReferenceExpression(VariableReferenceExpression variable)
	{
		variable.ResolvedType = variable.Variable?.ResolvedType ?? ErrorType;
		variable.SlotLifetimeFact = variable.Variable?.SlotLifetimeFact;
		variable.ValueLifetimeFact = variable.Variable?.ValueLifetimeFact ?? variable.Variable?.SlotLifetimeFact;
		return variable.ResolvedType;
	}

	string BodyAnalyzeSymbolOfExpression(SymbolOfExpression expression)
	{
		Report(GetRange(expression.SourceSyntax), "symbolof(...) may only be used in metadata attribute arguments.");
		return ErrorType;
	}

	string BodyAnalyzeLiteralExpression(LiteralExpression literal, string? targetType)
	{
		expressionConstants[literal] = true;
		return literal.Kind switch
		{
			LiteralKind.True or LiteralKind.False => "bool",
			LiteralKind.Null => "#NULL",
			LiteralKind.String => GetStringLiteralType(literal, targetType),
			LiteralKind.Character => "char",
			LiteralKind.Number => GetNumberLiteralType(literal.Text, targetType),
			_ => ErrorType
		};
	}

	string BodyAnalyzeNamedExpression(NamedExpression named, BodyScope scope, string? targetType)
	{
		if (IsDiscardExpression(named))
		{
			Report(GetRange(named.SourceSyntax), "Discard '_' is write-only and cannot be read.");
			return ErrorType;
		}

		if (TryResolveTargetTypedEnumValue(named, targetType, out string enumType))
			return enumType;

		if (named.Qualifiers.Count > 0)
		{
			string qualifiedName = string.Join("::", named.Qualifiers) + "::" + named.Name;
			Report(GetRange(named.SourceSyntax), $"Symbol '{qualifiedName}' could not be found.");
			return ErrorType;
		}

		if (scope.TryLookup(named.Name, out BodySymbol symbol))
		{
			named.ResolvedType = symbol.Type;
			expressionConstants[named] = symbol.IsConstant;
			expressionRewrites[named] = new VariableReferenceExpression
			{
				SourceSyntax = named.SourceSyntax,
				Variable = symbol.Node,
				ResolvedType = symbol.Type,
				SlotLifetimeFact = symbol.Node.SlotLifetimeFact,
				ValueLifetimeFact = symbol.Node.ValueLifetimeFact ?? symbol.Node.SlotLifetimeFact
			};
			return symbol.Type;
		}

		if (LookupGlobalStorageSymbol(named.Name, named.SourceSyntax) is BodySymbol globalSymbol)
		{
			string type = globalSymbol.Type;
			named.ResolvedType = type;
			expressionConstants[named] = globalSymbol.IsConstant;
			expressionRewrites[named] = new VariableReferenceExpression
			{
				SourceSyntax = named.SourceSyntax,
				Variable = globalSymbol.Node,
				ResolvedType = type,
				SlotLifetimeFact = globalSymbol.Node.SlotLifetimeFact,
				ValueLifetimeFact = globalSymbol.Node.ValueLifetimeFact ?? globalSymbol.Node.SlotLifetimeFact
			};
			return type;
		}

		List<FunctionDefinition> functions = LookupFunctions(named.Name, scope);
		if (functions.Count > 0)
		{
			if (functions.Count > 1)
			{
				if (IsOverloadFamily(functions))
					return ReportOverloadFamilyAsValue(named.SourceSyntax, named.Name);
				return ReportMultipleCandidates(named.SourceSyntax, named.Name);
			}

			MethodReferenceExpression method = new()
			{
				SourceSyntax = named.SourceSyntax,
				ResolvedType = BuildFunctionValueType(functions[0], IsInstanceFunction(functions[0]), allowCallableAscription: !IsReceiverBearingDeclaration(functions[0]))
			};
			method.Candidates.Add(functions[0]);
			expressionRewrites[named] = method;
			return method.ResolvedType;
		}

		if (TryGetUnqualifiedInstanceMember(scope.ContainingType, named.Name, named.SourceSyntax, out string memberKind))
		{
			Report(GetRange(named.SourceSyntax), $"{memberKind} '{named.Name}' requires explicit 'this.' qualification.");
			return ErrorType;
		}

		if (TryResolveAlias(named.Name, AliasTargetKind.Type, named.SourceSyntax, out AliasDefinition? alias))
		{
			if (TryGetPrimitiveType(alias!.ResolvedTargetName, out PrimitiveType primitive))
			{
				PrimitiveTypeReference primitiveReference = new()
				{
					SourceSyntax = named.SourceSyntax,
					Type = primitive,
					ResolvedType = alias.ResolvedTargetName
				};
				TypeReferenceExpression primitiveExpression = new()
				{
					SourceSyntax = named.SourceSyntax,
					Type = primitiveReference,
					ResolvedType = primitiveReference.ResolvedType
				};
				expressionRewrites[named] = primitiveExpression;
				return primitiveExpression.ResolvedType;
			}

			if (typeDefinitions.TryGetValue(alias.ResolvedTargetName, out TypeDefinition? aliasType))
			{
				TypeDefinitionReference aliasReference = new()
				{
					SourceSyntax = named.SourceSyntax,
					Name = aliasType.Name,
					Definition = aliasType,
					ResolvedType = aliasType.ResolvedType ?? aliasType.Name
				};
				TypeReferenceExpression aliasExpression = new()
				{
					SourceSyntax = named.SourceSyntax,
					Type = aliasReference,
					ResolvedType = aliasReference.ResolvedType
				};
				expressionRewrites[named] = aliasExpression;
				return aliasExpression.ResolvedType;
			}
		}

		if (typeDefinitions.TryGetValue(named.Name, out TypeDefinition? typeDefinition))
		{
			if (!IsDefinitionVisible(typeDefinition, named.SourceSyntax))
			{
				ReportNotExported(typeDefinition, named.SourceSyntax, "Type");
				return ErrorType;
			}

			TypeDefinitionReference typeReference = new()
			{
				SourceSyntax = named.SourceSyntax,
				Name = typeDefinition.Name,
				Definition = typeDefinition,
				ResolvedType = typeDefinition.ResolvedType ?? typeDefinition.Name
			};
			TypeReferenceExpression expression = new()
			{
				SourceSyntax = named.SourceSyntax,
				Type = typeReference,
				ResolvedType = typeReference.ResolvedType
			};
			expressionRewrites[named] = expression;
			return expression.ResolvedType;
		}

		if (LookupHiddenGlobalSymbol(named.Name, named.SourceSyntax) is Definition hidden)
		{
			ReportNotExported(hidden, named.SourceSyntax, hidden is TypeDefinition ? "Type" : "Symbol");
			return ErrorType;
		}

		Report(GetRange(named.SourceSyntax), $"Symbol '{named.Name}' could not be found.");
		return ErrorType;
	}

	bool TryResolveTargetTypedEnumValue(NamedExpression named, string? targetType, out string enumType)
	{
		enumType = ErrorType;
		if (named.Qualifiers.Count > 0 || string.IsNullOrWhiteSpace(named.Name))
			return false;

		if (!TryGetTargetEnumDefinition(targetType, out EnumDefinition? enumDefinition))
			return false;

		foreach (VariableDefinition value in enumDefinition!.Values)
		{
			if (value.Name != named.Name)
				continue;

			enumType = enumDefinition.Name;
			named.ResolvedType = enumType;
			expressionConstants[named] = true;
			expressionRewrites[named] = new VariableReferenceExpression
			{
				SourceSyntax = named.SourceSyntax,
				Variable = value,
				ResolvedType = enumType
			};
			return true;
		}

		return false;
	}

	bool TryGetTargetEnumDefinition(string? targetType, out EnumDefinition? enumDefinition)
	{
		enumDefinition = null;
		string? enumType = GetEnumTargetTypeName(targetType);
		if (enumType is null)
			return false;

		if (!typeDefinitions.TryGetValue(BaseTypeName(enumType), out TypeDefinition? type) || type is not EnumDefinition found)
			return false;

		enumDefinition = found;
		return true;
	}

	string? GetEnumTargetTypeName(string? targetType)
	{
		if (string.IsNullOrWhiteSpace(targetType) || targetType == TargetType || targetType == ErrorType)
			return null;

		string direct = BaseTypeName(targetType);
		if (typeDefinitions.TryGetValue(direct, out TypeDefinition? directType) && directType is EnumDefinition)
			return direct;

		if (TryParseTypeShape(targetType, out TypeShape shape) && shape.Kind == TypeShapeKind.Optional && shape.Element is not null)
		{
			string elementType = BaseTypeName(TypeShapeParser.Format(shape.Element));
			if (typeDefinitions.TryGetValue(elementType, out TypeDefinition? optionalElement) && optionalElement is EnumDefinition)
				return elementType;
		}

		return null;
	}

	string BodyAnalyzeMethodReference(MethodReferenceExpression method)
	{
		if (method.Candidates.Count == 0)
			return ErrorType;

		if (method.Candidates.Count > 1)
		{
			Report(GetRange(method.SourceSyntax), "Multiple member candidates found.");
			return ErrorType;
		}

		if (!string.IsNullOrWhiteSpace(method.ResolvedType))
			return method.ResolvedType;
		return BuildFunctionValueType(method.Candidates[0], IsInstanceFunction(method.Candidates[0]));
	}

	string BodyAnalyzeThisExpression(ThisExpression expression, BodyScope scope)
	{
		if (scope.ContainingType is null)
		{
			if (GetExplicitThisParameter(scope.CurrentFunction) is ThisParameterDefinition thisParameter)
				return thisParameter.ResolvedType ?? ErrorType;

			Report(GetRange(expression.SourceSyntax), "'this' is not available in this context.");
			return ErrorType;
		}

		string receiverType = $"{scope.ContainingType.Name}*";
		return BuildEffectiveReceiverType(receiverType, scope.CurrentFunction, IsPropertyGetterFunction(scope.CurrentFunction));
	}

	string BodyAnalyzeDefaultExpression(DefaultExpression expression, AnalysisScope typeScope, string? targetType)
	{
		if (expression.Type is not null)
		{
			AnalyzeType(expression.Type, typeScope);
			expressionConstants[expression] = true;
			return expression.Type.ResolvedType ?? ErrorType;
		}

		expressionConstants[expression] = true;
		return targetType ?? TargetType;
	}

	string BodyAnalyzeGroupedExpression(GroupedExpression grouped, BodyScope scope, AnalysisScope typeScope)
	{
		foreach (GroupedExpressionItem item in grouped.Items)
		{
			string itemType = BodyAnalyzeExpression(item.Expression, scope, typeScope);
			item.ResolvedType = itemType;
		}

		Report(GetRange(grouped.SourceSyntax), "Anonymous grouped values are no longer supported.");
		return ErrorType;
	}

	string BodyAnalyzeArrayExpression(ArrayExpression array, BodyScope scope, AnalysisScope typeScope, string? targetType)
	{
		bool pointerTarget = false;
		bool fixedTarget = TryGetFixedArrayShape(targetType, out string fixedElementType, out long fixedLength);
		if (fixedTarget && array.Elements.Count > fixedLength)
			Report(GetRange(array.SourceSyntax), $"Too many initializer values for {targetType}.");

		string? elementTarget = fixedTarget ? fixedElementType : TryGetArrayElementType(targetType);
		if (elementTarget is null)
		{
			elementTarget = TryGetPointerElementType(targetType);
			pointerTarget = elementTarget is not null;
		}
		List<string> elementTypes = [];
		foreach (Expression element in array.Elements)
		{
			string actual = BodyAnalyzeExpression(element, scope, typeScope, elementTarget);
			if (elementTarget is null && element is ArrayExpression && TryGetArrayElementType(actual) is string nestedElement)
			{
				actual = $"{nestedElement}*";
				element.ResolvedType = actual;
			}
			else if (element is not ArrayExpression && TryGetArrayElementType(actual) is not null)
			{
				Report(GetRange(element.SourceSyntax ?? array.SourceSyntax), "Array values may not be used as array literal elements; use '.elements' to make the pointer conversion explicit.");
				actual = ErrorType;
			}
			elementTypes.Add(actual);
		}

		string elementType = elementTarget ?? BestType(elementTypes);
		foreach (string actual in elementTypes)
			CheckAssignable(elementType, actual, array.SourceSyntax, "Array element");

		if (fixedTarget)
			return targetType ?? ErrorType;
		return pointerTarget ? $"{elementType}*" : $"{elementType}[]";
	}

	string BodyAnalyzeInitializerExpression(InitializerExpression initializer, BodyScope scope, AnalysisScope typeScope, string? targetType = null)
	{
		if (TryGetParamsComponentShape(null, targetType, "value", out ParamsComponentShape shape))
		{
			initializer.ResolvedType = targetType;
			AnalyzeExpandedInitializerExpression(initializer, shape, scope, typeScope);
			return targetType ?? TargetType;
		}

		foreach (InitializerItem item in initializer.Items)
		{
			if (item.Target is not null)
				BodyAnalyzeInitializerTarget(item.Target, scope, typeScope);
			item.ResolvedType = BodyAnalyzeExpression(item.Expression, scope, typeScope);
		}

		return targetType ?? TargetType;
	}

	void AnalyzeExpandedInitializerExpression(InitializerExpression initializer, ParamsComponentShape shape, BodyScope scope, AnalysisScope typeScope)
	{
		bool hasNamed = false;
		bool hasPositional = false;
		HashSet<string> seen = [];
		int positionalIndex = 0;

		foreach (InitializerItem item in initializer.Items)
		{
			string? targetName = GetSingleInitializerTargetName(item.Target);
			if (targetName is null)
			{
				hasPositional = true;
				if (hasNamed)
					Report(GetRange(item.Expression?.SourceSyntax ?? item.SourceSyntax), "Expanded initializer cannot mix named and positional components.");
				if (positionalIndex >= shape.Components.Count)
				{
					Report(GetRange(item.Expression?.SourceSyntax ?? item.SourceSyntax), $"Expanded initializer for '{shape.TypeName}' has too many components.");
					item.ResolvedType = BodyAnalyzeExpression(item.Expression, scope, typeScope);
					positionalIndex++;
					continue;
				}

				ParamsComponent component = shape.Components[positionalIndex++];
				item.ResolvedType = BodyAnalyzeExpression(item.Expression, scope, typeScope, component.Type);
				CheckAssignable(component.Type, item.ResolvedType ?? ErrorType, item.Expression?.SourceSyntax ?? item.SourceSyntax, "Initializer component");
				continue;
			}

			hasNamed = true;
			if (hasPositional)
				Report(GetRange(GetInitializerItemDiagnosticSyntax(item)), "Expanded initializer cannot mix named and positional components.");
			if (!seen.Add(targetName))
				Report(GetRange(GetInitializerItemDiagnosticSyntax(item)), $"Initializer component '{targetName}' is specified more than once.");
			ParamsComponent? namedComponent = FindParamsComponent(shape, targetName);
			if (namedComponent is null)
			{
				Report(GetRange(GetInitializerItemDiagnosticSyntax(item)), $"Expanded initializer for '{shape.TypeName}' has no component named '{targetName}'.");
				item.ResolvedType = BodyAnalyzeExpression(item.Expression, scope, typeScope);
				continue;
			}

			BodyAnalyzeInitializerTarget(item.Target!, scope, typeScope);
			item.ResolvedType = BodyAnalyzeExpression(item.Expression, scope, typeScope, namedComponent.Type);
			CheckAssignable(namedComponent.Type, item.ResolvedType ?? ErrorType, item.Expression?.SourceSyntax ?? item.SourceSyntax, "Initializer component");
		}

		if (hasPositional && positionalIndex < shape.Components.Count)
			Report(GetRange(initializer.SourceSyntax), $"Expanded initializer for '{shape.TypeName}' is missing component '{shape.Components[positionalIndex].Name}'.");
		if (hasNamed)
		{
			foreach (ParamsComponent component in shape.Components)
				if (!seen.Contains(component.Name))
					Report(GetRange(initializer.SourceSyntax), $"Expanded initializer for '{shape.TypeName}' is missing component '{component.Name}'.");
		}
	}

	SyntaxNode? GetInitializerItemDiagnosticSyntax(InitializerItem item)
	{
		return item.Expression?.SourceSyntax ?? item.Target?.SourceSyntax ?? item.SourceSyntax;
	}

	void BodyAnalyzeInitializerTarget(InitializerTarget target, BodyScope scope, AnalysisScope typeScope)
	{
		target.ResolvedType = TargetType;
		foreach (InitializerTargetPart part in target.Parts)
		{
			part.ResolvedType = TargetType;
			foreach (ArgumentExpression argument in part.Arguments)
				BodyAnalyzeArgumentExpression(argument, scope, typeScope);
		}
	}

	string BodyAnalyzeCastExpression(CastExpression cast, BodyScope scope, AnalysisScope typeScope)
	{
		if (cast.Type is not null)
			AnalyzeType(cast.Type, typeScope);
		string targetType = cast.Type?.ResolvedType ?? ErrorType;
		string structuralTargetType = ContainsLifetimeAnnotation(cast.Type) && !TryGetCallableShape(targetType, out _)
			? StripLifetimeQualifiers(targetType)
			: targetType;
		string? expressionTargetType = cast.Expression is InitializerExpression or ArrayExpression ? structuralTargetType : null;
		string sourceType = BodyAnalyzeExpression(cast.Expression, scope, typeScope, expressionTargetType);
		if (cast.LifetimeCastKind is not null)
		{
			if (cast.LifetimeCastKind == "unscoped" && cast.LifetimeCastAnchors.Count == 0)
				Report(GetRange(cast.SourceSyntax), "unscoped lifetime casts require an explicit anchor.");
			BindCastLifetime(cast, cast.LifetimeCastKind, cast.LifetimeCastAnchors, typeScope, scope);
		}
		else if (TryGetLifetimeAnnotation(cast.Type, out string lifetimeKind, out IReadOnlyList<string> lifetimeAnchors, out string? lifetimeBinding))
		{
			if (lifetimeKind == "unscoped" && lifetimeAnchors.Count == 0)
				Report(GetRange(cast.SourceSyntax), "unscoped lifetime casts require an explicit anchor.");
			cast.LifetimeBinding = lifetimeBinding;
		}

		if ((cast.Type is null && cast.Kind != CastKind.Type) || cast.LifetimeCastKind is not null)
			targetType = sourceType;
		else
		{
			ConversionClassification conversion = ClassifyConversion(sourceType, structuralTargetType);
			if (!cast.Unsafe && conversion.Level == ConversionLevel.Unsafe)
			{
				Report(GetRange(cast.SourceSyntax), conversion.Diagnostic ?? $"Cast from '{sourceType}' to '{targetType}' requires unsafe.");
			}
			else if (conversion.Level is ConversionLevel.FenceRequired or ConversionLevel.ReconstructRequired or ConversionLevel.Forbidden
				|| !cast.Unsafe && conversion.Level is not ConversionLevel.Implicit and not ConversionLevel.Explicit)
			{
				if (TryAnalyzeExplicitNumericLiteralNewtypeCast(cast.Expression, targetType, out bool literalCastAllowed, out string? literalCastDiagnostic))
				{
					if (!literalCastAllowed && literalCastDiagnostic is not null)
						Report(GetRange(cast.Expression?.SourceSyntax ?? cast.SourceSyntax), literalCastDiagnostic);
				}
				else
				{
					Report(GetRange(cast.SourceSyntax), conversion.Diagnostic ?? $"Invalid cast from '{sourceType}' to '{targetType}'.");
				}
			}
			else if (cast.Unsafe && conversion.IsOrdinary)
			{
				Warn(cast.SourceSyntax, "unsafe is not required for this cast; the conversion is ordinary explicit.");
			}
		}

		expressionConstants[cast] = IsConstant(cast.Expression);
		return targetType;
	}

	string BodyAnalyzeConstructionExpression(ConstructionExpression construction, BodyScope scope, AnalysisScope typeScope, string? targetExpressionType)
	{
		if (construction.Type is not null)
			AnalyzeType(construction.Type, typeScope);

		string targetType = construction.Type?.ResolvedType ?? TargetType;
		if (construction.Kind == ConstructionKind.Init
			&& typeDefinitions.TryGetValue(BaseConstructedType(targetType), out TypeDefinition? constructedDefinition)
			&& constructedDefinition is ClassDefinition { Extern: not null })
		{
			Report(GetRange(construction.SourceSyntax), $"Cannot use init for incomplete type '{targetType}'. Use new instead.");
		}
		FunctionDefinition? constructor = constructionTargets.TryGetValue(construction, out FunctionDefinition? existingConstructor)
			? existingConstructor
			: LookupConstructor(targetType, construction.Arguments.Count);
		FunctionDefinition? create = construction.Kind == ConstructionKind.New ? LookupCreateMethod(targetType, construction.Arguments.Count) : null;
		if (construction.Kind == ConstructionKind.New
			&& typeDefinitions.TryGetValue(BaseConstructedType(targetType), out TypeDefinition? newDefinition)
			&& newDefinition is ClassDefinition { Extern: not null }
			&& constructor is null
			&& create is null)
		{
			Report(GetRange(construction.SourceSyntax), $"Cannot allocate extern class '{targetType}' without an extern constructor or create method.");
		}
		if (constructor is not null)
			constructionTargets[construction] = constructor;
		AnalyzeCallArguments(construction.Arguments, constructor?.Parameters ?? create?.Parameters ?? [], scope, typeScope, construction.SourceSyntax);
		BodyAnalyzeExpression(construction.ElementCount, scope, typeScope, "nuint");
		if (construction.Initializer is not null)
			BodyAnalyzeInitializerExpression(construction.Initializer, scope, typeScope, targetType);
		if (construction.ElementCount is not null)
		{
			if (scope.CurrentIteratorElementType is not null && construction.Kind == ConstructionKind.Init)
				Report(GetRange(construction.SourceSyntax), "Iterator generator bodies cannot use init array construction; use fixed storage or new instead.");
			if (scope.CurrentFunction.IsAsync && construction.Kind == ConstructionKind.Init)
				Report(GetRange(construction.SourceSyntax), "Async bodies cannot use init array construction because declaration-scope storage cannot cross suspension; use fixed storage or new instead.");
			if (construction.Type?.ResolvedType is string elementType)
			{
				string arrayType = $"{elementType}[]";
				RequireGenericArrayElementStride(arrayType, scope, construction.SourceSyntax, $"{construction.Kind.ToString().ToLowerInvariant()} T[]");
			}
			if (TryGetPointerElementType(targetExpressionType) is string pointerElement
				&& (pointerElement == "void" || CanImplicitlyConvert(construction.Type?.ResolvedType ?? ErrorType, pointerElement)))
			{
				return targetExpressionType!;
			}
			return $"{targetType}[]";
		}
		return construction.Kind == ConstructionKind.New ? $"{targetType}*" : targetType;
	}

	string BodyAnalyzeWithinExpression(WithinExpression within, BodyScope scope, AnalysisScope typeScope, string? targetType)
	{
		string contextType = BodyAnalyzeExpression(within.Context, scope, typeScope, targetType);
		if (within.Expression is null)
			return contextType;
		return BodyAnalyzeExpression(within.Expression, scope, typeScope, targetType);
	}

	string BodyAnalyzeSizeOfExpression(SizeOfExpression sizeOf, AnalysisScope typeScope)
	{
		if (sizeOf.Type is not null)
			AnalyzeType(sizeOf.Type, typeScope);
		return "nuint";
	}

	string BodyAnalyzeVTableOfExpression(VTableOfExpression vtableOf, AnalysisScope typeScope)
	{
		return BodyAnalyzeVTableOfExpressionCore(vtableOf, typeScope);
	}

	string BodyAnalyzeLambdaExpression(LambdaExpression lambda, BodyScope scope, AnalysisScope typeScope, string? targetType)
	{
		CallableShape? targetShape = TryGetLambdaCallableShape(targetType, out CallableShape callableTarget, out bool targetIsEscaped) ? callableTarget : null;
		if (targetShape is CallableShape targetCallable && targetCallable.Kind is not ("fn" or "delegate" or "once"))
			Report(GetRange(lambda.SourceSyntax), "Lambdas can target only fn, delegate, once callable types.");
		string lambdaSourceReturnType = targetShape is CallableShape lambdaTarget
			? LambdaSourceReturnType(lambdaTarget.ReturnType, lambda)
			: TargetType;
		BodyScope lambdaScope = new(scope, scope.CurrentFunction, scope.ContainingType)
		{
			CurrentFunctionReturnType = targetShape?.ReturnType ?? TargetType,
			CurrentFunctionSourceReturnType = lambdaSourceReturnType,
			CurrentIteratorElementType = null
		};

		if (targetShape is CallableShape shape && shape.Parameters.Count != lambda.Parameters.Count)
			Report(GetRange(lambda.SourceSyntax), $"Lambda parameter count '{lambda.Parameters.Count.ToString(CultureInfo.InvariantCulture)}' does not match target callable parameter count '{shape.Parameters.Count.ToString(CultureInfo.InvariantCulture)}'.");

		for (int i = 0; i < lambda.Parameters.Count; i++)
		{
			LambdaParameter parameter = lambda.Parameters[i];
			if (parameter.Parameter is not null)
				AnalyzeParameterDefinition(parameter.Parameter, typeScope);

			string parameterSlot = parameter.Parameter is not null
				? GetLambdaParameterCallableSlot(parameter.Parameter)
				: (targetShape is CallableShape target && i < target.Parameters.Count ? target.Parameters[i] : TargetType);
			string parameterType = StripCallableParameterSlotModifier(parameterSlot);
			parameter.ResolvedType = parameterType;
			if (parameter.Parameter is null && parameterSlot == TargetType)
				Report(GetRange(lambda.SourceSyntax), $"Lambda parameter '{GetLambdaParameterSymbolName(parameter) ?? i.ToString(CultureInfo.InvariantCulture)}' requires a target callable type or an explicit parameter type.");

			if (targetShape is CallableShape expected
				&& i < expected.Parameters.Count
				&& parameterSlot != TargetType
				&& !CallableLambdaInputSlotCompatible(parameterSlot, expected.Parameters[i]))
				Report(GetRange(parameter.Parameter?.Type?.SourceSyntax ?? parameter.SourceSyntax ?? lambda.SourceSyntax), $"Lambda parameter type '{parameterSlot}' does not match target parameter type '{expected.Parameters[i]}'.");

			string? parameterName = GetLambdaParameterSymbolName(parameter);
			if (!string.IsNullOrWhiteSpace(parameterName))
				RegisterBodySymbol(lambdaScope, parameterName, parameter.ResolvedType, parameter, parameter.Parameter?.Type, parameter.ResolvedType, parameter.SourceSyntax);
		}

		string returnType = "void";
		if (lambda.Body is BlockStatement block)
		{
			ValidateLambdaConstOfAnchors(lambda, block);
			RewriteVoidLambdaExpressionBody(block, targetShape?.ReturnType);
			BodyAnalyzeBlock(block.Statements, lambdaScope, typeScope);
			BindStatementLabels(block.Statements);
			block.ResolvedType = "void";
			returnType = InferBlockReturnType(block, targetShape?.ReturnType);
		}
		List<LambdaCapture> captures = CollectLambdaCaptures(lambda, scope.CurrentFunction, scope.ContainingType, reportUnsupported: true);
		bool hasCaptures = captures.Count > 0;
		if (hasCaptures && targetShape is CallableShape { Kind: "fn" })
			Report(GetRange(lambda.SourceSyntax), "Capturing lambdas require a delegate target.");
		if (targetIsEscaped)
			ValidateEscapedLambdaCaptures(lambda, captures, scope);

		List<string> parameterTypes = [];
		for (int i = 0; i < lambda.Parameters.Count; i++)
		{
			LambdaParameter parameter = lambda.Parameters[i];
			parameterTypes.Add(parameter.Parameter is not null
				? GetLambdaParameterCallableSlot(parameter)
				: targetShape is CallableShape target && i < target.Parameters.Count ? target.Parameters[i] : parameter.ResolvedType ?? ErrorType);
		}

		string inferredKind = hasCaptures ? "delegate" : "fn";
		string inferredType = BuildCallableType(inferredKind, targetShape?.ReturnType ?? returnType, parameterTypes);
		if (targetType is not null
			&& TryGetLambdaCallableShape(targetType, out CallableShape expectedShape, out bool expectedEscaped)
			&& expectedShape.Kind is "fn" or "delegate" or "once"
			&& CallableShapesCompatibleWithConstOfVariance(new CallableShape(expectedShape.Kind, expectedShape.Spec, expectedShape.CallSpec, returnType, parameterTypes, expectedShape.This), expectedShape))
			return targetType;

		return inferredType;
	}

	void ValidateLambdaConstOfAnchors(LambdaExpression lambda, BlockStatement block)
	{
		HashSet<string> anchors = new(StringComparer.Ordinal);
		foreach (LambdaParameter parameter in lambda.Parameters)
		{
			string? name = GetLambdaParameterSymbolName(parameter);
			if (!string.IsNullOrWhiteSpace(name))
				anchors.Add(name);
		}

		foreach ((Statement? statement, Expression? expression) in LambdaStatementChildren(block))
		{
			if (statement is not null)
				ValidateLambdaConstOfAnchors(lambda, statement, anchors);
			if (expression is not null)
				ValidateLambdaConstOfAnchors(lambda, expression, anchors);
		}
	}

	void ValidateLambdaConstOfAnchors(LambdaExpression lambda, Statement statement, HashSet<string> anchors)
	{
		foreach ((Statement? childStatement, Expression? childExpression) in LambdaStatementChildren(statement))
		{
			if (childStatement is not null)
				ValidateLambdaConstOfAnchors(lambda, childStatement, anchors);
			if (childExpression is not null)
				ValidateLambdaConstOfAnchors(lambda, childExpression, anchors);
		}
	}

	void ValidateLambdaConstOfAnchors(LambdaExpression lambda, Expression expression, HashSet<string> anchors)
	{
		switch (expression)
		{
			case CastExpression cast:
				ValidateLambdaConstOfAnchors(lambda, cast.Type, anchors);
				break;
		}

		foreach (Expression child in LambdaExpressionChildren(expression))
			ValidateLambdaConstOfAnchors(lambda, child, anchors);
	}

	void ValidateLambdaConstOfAnchors(LambdaExpression lambda, TypeReference? type, HashSet<string> anchors)
	{
		switch (type)
		{
			case null:
				return;
			case ConstOfTypeReference constOf:
				if (constOf.AnchorName != "this" && !anchors.Contains(constOf.AnchorName))
					Report(GetRange(constOf.SourceSyntax ?? lambda.SourceSyntax), $"constof anchor '{constOf.AnchorName}' is not valid in this lambda; constof anchors inside lambdas must name lambda parameters.");
				ValidateLambdaConstOfAnchors(lambda, constOf.Type, anchors);
				break;
			case PointerTypeReference pointer:
				ValidateLambdaConstOfAnchors(lambda, pointer.ElementType, anchors);
				break;
			case ArrayTypeReference array:
				ValidateLambdaConstOfAnchors(lambda, array.ElementType, anchors);
				break;
			case FixedArrayTypeReference fixedArray:
				ValidateLambdaConstOfAnchors(lambda, fixedArray.ElementType, anchors);
				break;
			case OptionalTypeReference optional:
				ValidateLambdaConstOfAnchors(lambda, optional.ElementType, anchors);
				break;
			case ConstTypeReference constant:
				ValidateLambdaConstOfAnchors(lambda, constant.Type, anchors);
				break;
			case VolatileTypeReference vol:
				ValidateLambdaConstOfAnchors(lambda, vol.Type, anchors);
				break;
			case EscapedTypeReference escaped:
				ValidateLambdaConstOfAnchors(lambda, escaped.Type, anchors);
				break;
			case ScopedTypeReference scoped:
				ValidateLambdaConstOfAnchors(lambda, scoped.Type, anchors);
				break;
			case UnscopedTypeReference unscoped:
				ValidateLambdaConstOfAnchors(lambda, unscoped.Type, anchors);
				break;
			case GenericTypeReference generic:
				ValidateLambdaConstOfAnchors(lambda, generic.Type, anchors);
				foreach (TypeReference argument in generic.TypeArguments)
					ValidateLambdaConstOfAnchors(lambda, argument, anchors);
				break;
			case CallableTypeReference callable:
				ValidateLambdaConstOfAnchors(lambda, callable.ReturnType, anchors);
				foreach (ParameterDefinition parameter in callable.Parameters)
					ValidateLambdaConstOfAnchors(lambda, parameter.Type, anchors);
				break;
			case IterTypeReference iter:
				ValidateLambdaConstOfAnchors(lambda, iter.ElementType, anchors);
				foreach (ParameterDefinition parameter in iter.Parameters)
					ValidateLambdaConstOfAnchors(lambda, parameter.Type, anchors);
				break;
			case TargetTypeSpecTypeReference targetSpec:
				ValidateLambdaConstOfAnchors(lambda, targetSpec.Type, anchors);
				break;
			case GroupedParamsTypeReference grouped:
				ValidateLambdaConstOfAnchors(lambda, grouped.StructType, anchors);
				break;
			case MaterializedStructTypeReference materialized:
				ValidateLambdaConstOfAnchors(lambda, materialized.ParamsType, anchors);
				break;
			case ThrownTypeReference thrown:
				ValidateLambdaConstOfAnchors(lambda, thrown.Type, anchors);
				break;
		}
	}

	static bool CallableLambdaInputSlotCompatible(string source, string target)
	{
		CallableSlot sourceSlot = ParseCallableSlot(source);
		CallableSlot targetSlot = ParseCallableSlot(target);
		return sourceSlot.Modifier == targetSlot.Modifier
			&& CallableSlotTypesCompatible(sourceSlot.Type, targetSlot.Type, outputPosition: sourceSlot.Modifier == "out");
	}

	static string LambdaSourceReturnType(string returnType, LambdaExpression lambda)
	{
		string result = returnType;
		for (int i = 0; i < lambda.Parameters.Count; i++)
		{
			string? name = GetLambdaParameterSymbolName(lambda.Parameters[i]);
			if (!string.IsNullOrWhiteSpace(name))
				result = result.Replace("constof(#" + i.ToString(CultureInfo.InvariantCulture) + ")", "constof(" + name + ")", StringComparison.Ordinal);
		}
		return result;
	}

	string FormatLambdaTargetType(TypeReference type)
	{
		if (type is NamedTypeReference named && !string.IsNullOrWhiteSpace(named.Name))
			return named.TypeArguments.Count == 0
				? named.Name
				: named.Name + "<" + string.Join(", ", named.TypeArguments.Select(FormatTypeReference)) + ">";
		return FormatTypeReference(type);
	}

	static void RewriteVoidLambdaExpressionBody(BlockStatement block, string? targetReturnType)
	{
		if (targetReturnType != "void")
			return;
		if (block.Statements is not [ReturnStatement { Expression: not null } returnStatement])
			return;
		block.Statements[0] = new ExpressionStatement
		{
			SourceSyntax = returnStatement.SourceSyntax,
			Expression = returnStatement.Expression,
			ResolvedType = "void"
		};
	}

	static string GetLambdaParameterCallableSlot(LambdaParameter parameter)
	{
		if (parameter.Parameter is not null)
			return GetLambdaParameterCallableSlot(parameter.Parameter);
		return parameter.ResolvedType ?? ErrorType;
	}

	static string GetLambdaParameterCallableSlot(ParameterDefinition parameter)
	{
		string type = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? ErrorType;
		return parameter.Modifier switch
		{
			ParameterModifier.In => "in " + type,
			ParameterModifier.Out => "out " + type,
			ParameterModifier.Thrown => "thrown " + type,
			ParameterModifier.Within => "within " + type,
			ParameterModifier.Upon => "upon " + type,
			_ => type
		};
	}

	static string StripCallableParameterSlotModifier(string slot)
	{
		foreach (string prefix in new[] { "in ", "out ", "thrown ", "within " })
		{
			if (slot.StartsWith(prefix, StringComparison.Ordinal))
				return slot[prefix.Length..].Trim();
		}
		return slot;
	}

	string BodyAnalyzeArgumentExpression(ArgumentExpression argument, BodyScope scope, AnalysisScope typeScope, string? targetType = null)
	{
		if (argument.Type is not null)
			AnalyzeType(argument.Type, typeScope);

		if (argument.Target is not null)
		{
			if (argument.Modifier is not ArgumentModifier.Out and not ArgumentModifier.Catch)
				Report(GetRange(argument.SourceSyntax), "Argument declarations may only be used with 'out' or 'catch'.");

			BodyAnalyzeDeclarationTarget(argument.Target, scope, typeScope, targetType ?? TargetType, allowDiscard: argument.Modifier is ArgumentModifier.Out or ArgumentModifier.Catch);
			argument.ResolvedType = argument.Target.ResolvedType ?? ErrorType;
			return argument.ResolvedType;
		}

		if (argument.Modifier is ArgumentModifier.Out or ArgumentModifier.Catch && IsDiscardExpression(argument.Value))
		{
			argument.ResolvedType = argument.Type?.ResolvedType ?? targetType ?? ErrorType;
			argument.Value!.ResolvedType = argument.ResolvedType;
			return argument.ResolvedType;
		}

		if (argument.Value is WithinExpression { Expression: null } within)
		{
			string contextType = BodyAnalyzeExpression(within.Context, scope, typeScope, targetType);
			argument.ResolvedType = argument.Type?.ResolvedType ?? contextType;
			return argument.ResolvedType;
		}

		string valueType = BodyAnalyzeExpression(argument.Value, scope, typeScope, argument.Type?.ResolvedType ?? targetType);
		argument.ResolvedType = argument.Type?.ResolvedType ?? valueType;
		return argument.ResolvedType;
	}

	string BodyAnalyzeCallExpression(CallExpression call, BodyScope scope, AnalysisScope typeScope, string? targetType)
	{
		NormalizeGenericCallSyntax(call);
		foreach (TypeReference argument in call.TypeArguments)
			AnalyzeType(argument, typeScope);

		FunctionDefinition? function = callTargets.TryGetValue(call, out FunctionDefinition? existingTarget)
			? existingTarget
			: ResolveCallTarget(call.Target, scope, typeScope, call.Arguments);
		Dictionary<string, string> genericSubstitutions = [];
		HashSet<string> genericParameterNames = [];
		if (function is not null)
		{
			EnsureFunctionSignatureAnalyzed(function, typeScope);
			callTargets[call] = function;
			foreach (GenericParameter parameter in function.GenericParameters)
				genericParameterNames.Add(parameter.Name);
			if (FindContainingType(function) is TypeDefinition containingType)
			{
				foreach (GenericParameter parameter in containingType.GenericParameters)
					genericParameterNames.Add(parameter.Name);
			}
			AddExplicitGenericSubstitutions(function, call.TypeArguments, genericSubstitutions);
			AddReceiverGenericSubstitutions(call.Target, function, genericSubstitutions);
		}
		else if (TryAnalyzeCallableInvocation(call, scope, typeScope, targetType, out string callableReturnType))
		{
			return callableReturnType;
		}
		else if (call.Target is MemberExpression member && TryAnalyzePropertyIndexer(member, call.Arguments, scope, typeScope, out string propertyCallType))
		{
			if (expressionRewrites.TryGetValue(member, out Expression? rewritten)
				&& rewritten is MemberReferenceExpression propertyReference
				&& propertyReference.Member is FunctionDefinition propertyFunction
				&& IsPropertyGetterReference(propertyReference))
			{
				callTargets[call] = propertyFunction;
				callGenericSubstitutions[call] = new Dictionary<string, string>(StringComparer.Ordinal);
			}
			if (targetType is not null)
				CheckAssignable(targetType, propertyCallType, call.SourceSyntax, "Call result");
			return propertyCallType;
		}
		else if (IsRawFunctionPointerType(call.Target?.ResolvedType))
		{
			Report(GetRange(call.Target?.SourceSyntax ?? call.SourceSyntax), "Raw function pointer 'fn*' is not callable; cast it to a concrete fn type first.");
		}
		List<ParameterDefinition> analysisParameters = function is null ? [] : GetAsyncAwareCallParameters(function, call, IncludeExplicitThisArgument(call.Target, function));
		Dictionary<string, bool> constOfAnchors = AnalyzeCallArguments(call.Arguments, analysisParameters, scope, typeScope, call.SourceSyntax ?? GetExpressionDiagnosticSyntax(call.Target), IncludeExplicitThisArgument(call.Target, function), genericSubstitutions, genericParameterNames, function, call.Target);
		if (function is not null)
			AddReceiverConstOfAnchorFact(call.Target, function, constOfAnchors);
		if (function is not null)
			ValidateGenericCallSubstitutionConstraints(function, genericSubstitutions, scope, call.SourceSyntax ?? GetExpressionDiagnosticSyntax(call.Target));
		if (function is not null)
			callGenericSubstitutions[call] = new Dictionary<string, string>(genericSubstitutions, StringComparer.Ordinal);

		string returnType = SubstituteGenericReturnType(function?.ResolvedType, call.TypeArguments, genericSubstitutions);
		if (function is not null)
			returnType = SubstituteConstOfResolvedType(function.ReturnType, returnType, constOfAnchors, genericSubstitutions);
		if (function is not null)
			returnType = RefineClassTypeCallReturn(function, call.Target, returnType);
		if (function is not null)
			returnType = RefineThisTypeCallReturn(function, call.Target, returnType);
		if (function is not null)
			returnType = RefineCallReturnTypeFromLifetimeArguments(function, call.Target, call.Arguments, returnType);
		if (targetType is not null)
			CheckAssignable(targetType, returnType, call.SourceSyntax, "Call result");
		return returnType;
	}

	List<ParameterDefinition> GetAsyncAwareCallParameters(FunctionDefinition function, CallExpression call, bool includeExplicitThis)
	{
		List<ParameterDefinition> parameters = [.. function.Parameters];
		int visibleCount = function.IsAsync
			? parameters.Count(static parameter => parameter.Modifier != ParameterModifier.Thrown)
			: parameters.Count;
		if (includeExplicitThis && GetExplicitThisParameter(function) is null && IsInstanceFunction(function))
			visibleCount++;
		if (function.IsAsync && call.Arguments.Count > visibleCount)
			parameters.AddRange(CreateAsyncCompletionSourceParameters(function));
		return parameters;
	}

	static List<ParameterDefinition> CreateAsyncCompletionSourceParameters(FunctionDefinition function)
	{
		List<string> completionParameters = [];
		string returnType = function.ResolvedType ?? "void";
		if (returnType != "void")
			completionParameters.Add(returnType);
		if (function.Parameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Thrown) is ParameterDefinition thrown)
			completionParameters.Add(thrown.ResolvedType ?? thrown.Type?.ResolvedType ?? ErrorType);
		completionParameters.Insert(0, "void*");
		string callableType = CallableShapeService.BuildCallableType("fn", "void", completionParameters);
		return
		[
			new ParameterDefinition
			{
				Name = "complete",
				Symbol = "complete",
				ResolvedType = callableType,
				Type = new NamedTypeReference { Name = callableType, ResolvedType = callableType }
			},
			new ParameterDefinition
			{
				Name = "complete_context",
				Symbol = "complete_context",
				ResolvedType = "void*",
				Type = new PointerTypeReference { ElementType = new PrimitiveTypeReference { Type = PrimitiveType.Void, ResolvedType = "void" }, ResolvedType = "void*" }
			}
		];
	}

	void ValidateGenericCallSubstitutionConstraints(FunctionDefinition function, Dictionary<string, string> substitutions, BodyScope scope, SyntaxNode? syntax)
	{
		HashSet<string> checkedNames = [];
		foreach (GenericParameter parameter in function.GenericParameters)
			ValidateGenericCallSubstitutionConstraint(function, parameter, substitutions, scope, syntax, checkedNames);
		if (FindContainingType(function) is TypeDefinition containingType)
			foreach (GenericParameter parameter in containingType.GenericParameters)
				ValidateGenericCallSubstitutionConstraint(function, parameter, substitutions, scope, syntax, checkedNames);
	}

	void ValidateGenericCallSubstitutionConstraint(FunctionDefinition function, GenericParameter parameter, Dictionary<string, string> substitutions, BodyScope scope, SyntaxNode? syntax, HashSet<string> checkedNames)
	{
		if (!checkedNames.Add(parameter.Name) || parameter.Constraint is not CopyableTypeReference)
			return;
		if (!substitutions.TryGetValue(parameter.Name, out string? substitutedType) || string.IsNullOrWhiteSpace(substitutedType))
			return;
		if (!IsCopyableResolvedType(substitutedType, scope, []))
			Report(GetRange(syntax), $"Type argument '{substitutedType}' does not satisfy copyable constraint '{parameter.Name}: copyable' for call to '{function.Name}'.");
	}

	static void NormalizeGenericCallSyntax(CallExpression call)
	{
		if (call.Target is not CallExpression { Arguments.Count: 0, TypeArguments.Count: > 0 } genericTarget)
			return;

		call.Target = genericTarget.Target;
		foreach (TypeReference argument in genericTarget.TypeArguments)
			call.TypeArguments.Add(argument);
	}

	bool TryAnalyzeCallableInvocation(CallExpression call, BodyScope scope, AnalysisScope typeScope, string? targetType, out string returnType)
	{
		returnType = ErrorType;
		string callableType = call.Target?.ResolvedType ?? ErrorType;
		if (!TryGetCallableShape(callableType, out CallableShape callable))
			return false;

		List<ParameterDefinition> parameters = TryGetCallableNewtypeParameters(callableType, call.Arguments, out List<ParameterDefinition>? newtypeParameters)
			? newtypeParameters!
			: CreateStructuralCallableParameters(callable.Parameters);

		callableInvocationParameters[call] = parameters;
		Dictionary<string, bool> constOfAnchors = AnalyzeCallArguments(call.Arguments, parameters, scope, typeScope, call.SourceSyntax ?? GetExpressionDiagnosticSyntax(call.Target));
		returnType = SubstituteCallableConstOfReturnType(callableType, callable.ReturnType, constOfAnchors);
		if (targetType is not null)
			CheckAssignable(targetType, returnType, call.SourceSyntax, "Call result");
		return true;
	}

	static bool IsRawFunctionPointerType(string? type)
	{
		return type == "fn*" || type?.StartsWith("fn* ", StringComparison.Ordinal) == true;
	}

	void AddReceiverConstOfAnchorFact(Expression? target, FunctionDefinition function, Dictionary<string, bool> anchors)
	{
		Expression? receiver = target switch
		{
			MemberExpression member => member.Target,
			MemberReferenceExpression member => member.Target,
			_ => null
		};
		if (receiver is null || GetEffectiveThisParameter(function) is not ParameterDefinition thisParameter || string.IsNullOrWhiteSpace(thisParameter.Name))
			return;
		ArgumentExpression receiverArgument = new()
		{
			SourceSyntax = receiver.SourceSyntax,
			Value = receiver,
			ResolvedType = receiver.ResolvedType
		};
		if (TryGetConstOfActualSlot(thisParameter, receiverArgument, out bool isConst))
			anchors[thisParameter.Name] = isConst;
	}

	void AddReceiverConstOfAnchorFact(string receiverType, FunctionDefinition function, Dictionary<string, bool> anchors, SyntaxNode? syntax = null)
	{
		if (GetEffectiveThisParameter(function) is not ParameterDefinition thisParameter || string.IsNullOrWhiteSpace(thisParameter.Name))
			return;
		ArgumentExpression receiverArgument = new()
		{
			SourceSyntax = syntax,
			ResolvedType = receiverType
		};
		if (TryGetConstOfActualSlot(thisParameter, receiverArgument, out bool isConst))
			anchors[thisParameter.Name] = isConst;
	}

	bool TryGetCallableNewtypeParameters(string callableType, List<ArgumentExpression> arguments, out List<ParameterDefinition>? parameters)
	{
		parameters = null;
		if (!typeDefinitions.TryGetValue(BaseTypeName(callableType), out TypeDefinition? definition)
			|| definition is not NewtypeDefinition newtypeDefinition
			|| GetCallableNewtypeFamily(newtypeDefinition) is not ("fn" or "delegate" or "iter"))
			return false;

		parameters = [];
		List<ParameterDefinition> protocolParameters = [];
		if (GetCallableNewtypeFamily(newtypeDefinition) == "iter"
			&& newtypeDefinition.UnderlyingType?.ResolvedType is string underlyingType
			&& TryGetIteratorProtocolParameterTypes(underlyingType, out List<string>? iteratorParameters)
			&& iteratorParameters is not null)
			protocolParameters.AddRange(CreateStructuralCallableParameters(iteratorParameters));
		parameters.AddRange(protocolParameters);
		foreach (ParameterDefinition parameter in newtypeDefinition.Parameters)
			parameters.Add(CloneParameter(parameter));
		if (arguments.Count > parameters.Count || HasExplicitHiddenArgument(arguments))
		{
			parameters = [.. protocolParameters];
			parameters.AddRange(CreateStructuralCallableParameters(GetExpandedCallableParameterTypes(newtypeDefinition.Parameters)));
		}
		return true;
	}

	static List<ParameterDefinition> CreateStructuralCallableParameters(List<string> parameterTypes)
	{
		List<ParameterDefinition> parameters = [];
		foreach (string parameterType in parameterTypes)
		{
			string typeName = parameterType;
			ParameterModifier modifier = ParameterModifier.None;
			if (typeName.StartsWith("in ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.In;
				typeName = typeName[3..].TrimStart();
			}
			else if (typeName.StartsWith("out ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.Out;
				typeName = typeName[4..].TrimStart();
			}
			else if (typeName.StartsWith("thrown ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.Thrown;
				typeName = typeName[7..].TrimStart();
			}
			else if (typeName.StartsWith("within ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.Within;
				typeName = typeName[7..].TrimStart();
			}
			else if (typeName.StartsWith("upon ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.Upon;
				typeName = typeName[5..].TrimStart();
			}
			parameters.Add(new ParameterDefinition
			{
				Modifier = modifier,
				ResolvedType = typeName,
				Type = new NamedTypeReference { Name = typeName, ResolvedType = typeName }
			});
		}
		return parameters;
	}

	static string SubstituteGenericReturnType(string? returnType, List<TypeReference> typeArguments, Dictionary<string, string>? substitutions = null)
	{
		if (returnType is null)
			return ErrorType;
		if (substitutions is { Count: > 0 })
			return SubstituteGenericType(returnType, substitutions);
		if (typeArguments.Count == 0)
			return returnType;

		string firstType = typeArguments[0].ResolvedType ?? ErrorType;
		return returnType.Replace("T", firstType, StringComparison.Ordinal);
	}

	static void AddExplicitGenericSubstitutions(FunctionDefinition function, List<TypeReference> typeArguments, Dictionary<string, string> substitutions)
	{
		int count = Math.Min(function.GenericParameters.Count, typeArguments.Count);
		for (int i = 0; i < count; i++)
			substitutions[function.GenericParameters[i].Name] = typeArguments[i].ResolvedType ?? ErrorType;
	}

	void AddReceiverGenericSubstitutions(Expression? target, FunctionDefinition function, Dictionary<string, string> substitutions)
	{
		string? receiverType = target switch
		{
			MemberExpression member => member.Target?.ResolvedType,
			MemberReferenceExpression member => member.Target?.ResolvedType,
			_ => null
		};
		if (receiverType is null)
			return;
		if (target is MemberExpression memberExpression
			&& TryGetImplicitIteratorProtocolType(memberExpression.Target, receiverType, out string iteratorProtocolType))
			receiverType = iteratorProtocolType;

		AddReceiverTypeGenericSubstitutions(receiverType, function, substitutions);
	}

	void AddReceiverTypeGenericSubstitutions(string receiverType, FunctionDefinition function, Dictionary<string, string> substitutions)
	{
		if (FindContainingType(function) is not TypeDefinition containingType)
		{
			if (function.GenericParameters.Count == 0)
				return;

			ThisParameterDefinition? thisParameter = GetExplicitThisParameter(function);
			if (thisParameter is not null)
			{
				InferGenericSubstitutions(
					thisParameter.ResolvedType ?? ErrorType,
					receiverType,
					substitutions,
					GetFunctionGenericParameterNames(function));
				return;
			}

			ParameterDefinition? firstParam = function.Parameters.Count > 0 ? function.Parameters[0] : null;
			if (firstParam?.Name == "this")
			{
				InferGenericSubstitutions(
					firstParam.ResolvedType ?? ErrorType,
					receiverType,
					substitutions,
					GetFunctionGenericParameterNames(function));
			}
			return;
		}

		if (containingType.GenericParameters.Count == 0)
			return;

		List<string> typeArguments = ExtractConstructedTypeArguments(receiverType);
		int count = Math.Min(containingType.GenericParameters.Count, typeArguments.Count);
		for (int i = 0; i < count; i++)
			substitutions[containingType.GenericParameters[i].Name] = typeArguments[i];
	}

	HashSet<string> GetFunctionGenericParameterNames(FunctionDefinition function)
	{
		HashSet<string> genericParameterNames = [];
		foreach (GenericParameter parameter in function.GenericParameters)
			genericParameterNames.Add(parameter.Name);
		if (FindContainingType(function) is TypeDefinition containingType)
			foreach (GenericParameter parameter in containingType.GenericParameters)
				genericParameterNames.Add(parameter.Name);
		return genericParameterNames;
	}

	static List<string> ExtractConstructedTypeArguments(string type)
	{
		type = StripTopLevelValueQualifiers(type.Trim());
		while (true)
		{
			if (type.EndsWith("[]", StringComparison.Ordinal))
			{
				type = type[..^2].TrimEnd();
				continue;
			}
			if (type.EndsWith("*", StringComparison.Ordinal) || type.EndsWith("?", StringComparison.Ordinal))
			{
				type = type[..^1].TrimEnd();
				continue;
			}
			break;
		}

		int start = type.IndexOf('<', StringComparison.Ordinal);
		if (start < 0)
			return [];

		int depth = 0;
		for (int i = start; i < type.Length; i++)
		{
			if (type[i] == '<')
				depth++;
			else if (type[i] == '>' && --depth == 0)
				return SplitGenericArgumentList(type[(start + 1)..i]);
		}

		return [];
	}

	static List<string> SplitGenericArgumentList(string text)
	{
		List<string> arguments = [];
		int start = 0;
		int genericDepth = 0;
		int parenDepth = 0;
		for (int i = 0; i <= text.Length; i++)
		{
			char ch = i < text.Length ? text[i] : ',';
			if (ch == '<')
				genericDepth++;
			else if (ch == '>' && genericDepth > 0)
				genericDepth--;
			else if (ch == '(')
				parenDepth++;
			else if (ch == ')' && parenDepth > 0)
				parenDepth--;
			else if (ch == ',' && genericDepth == 0 && parenDepth == 0)
			{
				string argument = text[start..i].Trim();
				if (argument.Length > 0)
					arguments.Add(argument);
				start = i + 1;
			}
		}
		return arguments;
	}

	bool IncludeExplicitThisArgument(Expression? target, FunctionDefinition? function)
	{
		if (target is NamedExpression { Qualifiers.Count: 0, Name: "base" })
			return false;
		return function is not null
			&& (GetExplicitThisParameter(function) is not null || HasExpandedThisParameters(function.Parameters) || IsInstanceFunction(function))
			&& target is not MemberExpression and not MemberReferenceExpression;
	}

	FunctionDefinition? ResolveCallTarget(Expression? target, BodyScope scope, AnalysisScope typeScope, List<ArgumentExpression>? arguments = null)
	{
		int argumentCount = arguments?.Count ?? 0;
		switch (target)
		{
			case NamedExpression named:
			{
				if (named.Qualifiers.Count == 0 && named.Name == "base")
					return ResolveBaseConstructorCall(named, scope, argumentCount);

				if (named.Qualifiers.Count == 0 && scope.TryLookup(named.Name, out _))
				{
					BodyAnalyzeExpression(named, scope, typeScope);
					return null;
				}

				List<FunctionDefinition> functions = LookupFunctions(named.Name, scope);
				if (functions.Count > 1 && TrySelectOverload(named.Name, functions, arguments ?? [], scope, typeScope, named.SourceSyntax) is FunctionDefinition selectedNamed)
				{
					EnsureFunctionSignatureAnalyzed(selectedNamed, typeScope);
					named.ResolvedType = BuildFunctionValueType(selectedNamed, IsInstanceFunction(selectedNamed), allowCallableAscription: !IsReceiverBearingDeclaration(selectedNamed));
					expressionRewrites[named] = CreateMethodReference(selectedNamed, named.ResolvedType, named.SourceSyntax);
					return selectedNamed;
				}
				if (functions.Count == 1)
				{
					EnsureFunctionSignatureAnalyzed(functions[0], typeScope);
					BodyAnalyzeExpression(named, scope, typeScope);
					return functions[0];
				}
				if (functions.Count > 1)
					Report(GetRange(named.SourceSyntax), $"Multiple candidates found for call target '{named.Name}'.");
				else if (named.Qualifiers.Count == 0 && TryGetUnqualifiedInstanceMember(scope.ContainingType, named.Name, named.SourceSyntax, out string memberKind))
					Report(GetRange(named.SourceSyntax), $"{memberKind} '{named.Name}' requires explicit 'this.' qualification.");
				else
					BodyAnalyzeNamedExpression(named, scope, targetType: null);
				return null;
			}

			case MemberExpression member:
			{
				if (member.Target is NamedExpression { Qualifiers.Count: 0, Name: "base" })
					return ResolveBaseMemberCallTarget(member, scope, typeScope, arguments ?? []);

				string targetType = TryAnalyzeMemberTypeTarget(member.Target, scope, out string typeTarget)
					? typeTarget
					: BodyAnalyzeExpression(member.Target, scope, typeScope);
				string lookupTargetType = TryGetImplicitIteratorProtocolType(member.Target, targetType, out string iteratorProtocolType)
					? iteratorProtocolType
					: targetType;
				bool isTypeTarget = IsTypeReferenceExpression(member.Target);
				List<FunctionDefinition> functions = isTypeTarget
					? LookupStaticMemberFunctions(lookupTargetType, member.Name, member.SourceSyntax)
					: LookupMemberFunctions(lookupTargetType, member.Name, member.SourceSyntax);
				if (functions.Count == 0)
					functions = LookupGenericConstraintMemberFunctions(lookupTargetType, member.Name, scope, member.SourceSyntax);
				if (functions.Count > 1 && TrySelectOverload(member.Name, functions, arguments ?? [], scope, typeScope, member.SourceSyntax) is FunctionDefinition selectedMember)
				{
					member.ResolvedType = selectedMember.ResolvedType ?? ErrorType;
					expressionRewrites[member] = CreateMemberReference(member, member.Target, BuildFunctionValueType(selectedMember, isInstance: true, allowCallableAscription: !IsTypeReferenceExpression(member.Target)), selectedMember);
					return selectedMember;
				}
				if (functions.Count == 1)
				{
					member.ResolvedType = functions[0].ResolvedType ?? ErrorType;
					expressionRewrites[member] = CreateMemberReference(member, member.Target, BuildFunctionValueType(functions[0], isInstance: true, allowCallableAscription: !IsTypeReferenceExpression(member.Target)), functions[0]);
					return functions[0];
				}
				if (functions.Count > 1)
					Report(GetRange(member.SourceSyntax), $"Multiple candidates found for member call '{member.Name}'.");
				else if (GetTypeDefinition(lookupTargetType) is TypeDefinition receiverType && HasMemberFunctionWithIncompatibleReceiver(receiverType, lookupTargetType, member.Name, member.SourceSyntax))
					Report(GetRange(member.SourceSyntax), ReceiverIncompatibilityMessage("Member", member.Name, lookupTargetType));
				else
					BodyAnalyzeMemberExpression(member, scope, typeScope);
				return null;
			}

			case MemberReferenceExpression { Member: FunctionDefinition function } member:
				EnsureFunctionSignatureAnalyzed(function, typeScope);
				member.ResolvedType = function.ResolvedType ?? ErrorType;
				BodyAnalyzeExpression(member.Target, scope, typeScope);
				return function;

			case MethodReferenceExpression method when method.Candidates.Count == 1:
				EnsureFunctionSignatureAnalyzed(method.Candidates[0], typeScope);
				method.ResolvedType = BuildFunctionValueType(method.Candidates[0], IsInstanceFunction(method.Candidates[0]));
				return method.Candidates[0];

			default:
				BodyAnalyzeExpression(target, scope, typeScope);
				return null;
		}
	}

	FunctionDefinition? ResolveBaseMemberCallTarget(MemberExpression member, BodyScope scope, AnalysisScope typeScope, List<ArgumentExpression> arguments)
	{
		if (scope.ContainingType is not ClassDefinition containingClass)
		{
			Report(GetRange(member.SourceSyntax), "base member access is valid only in a class method.");
			return null;
		}

		ClassDefinition? baseClass = GetDirectBaseClass(containingClass);
		if (baseClass is null)
		{
			Report(GetRange(member.SourceSyntax), $"Class '{containingClass.Name}' does not have a base class.");
			return null;
		}

		List<FunctionDefinition> baseCandidates = LookupTypeFunctions(baseClass, member.Name, member.SourceSyntax);
		FunctionDefinition? baseDeclaration = baseCandidates.Count switch
		{
			1 => baseCandidates[0],
			> 1 => TrySelectOverload(member.Name, baseCandidates, arguments, scope, typeScope, member.SourceSyntax),
			_ => null
		};

		FunctionDefinition? implementation = baseDeclaration is null
			? null
			: FindVirtualImplementationByName(baseClass, VirtualSlotName(baseDeclaration));
		if (implementation is null)
		{
			Report(GetRange(member.SourceSyntax), $"Base implementation '{member.Name}' could not be found.");
			return null;
		}

		EnsureFunctionSignatureAnalyzed(implementation, typeScope);
		Expression baseReceiver = new CastExpression
		{
			SourceSyntax = member.SourceSyntax,
			Kind = CastKind.Type,
			Type = PointerTo(TypeReferenceFor(baseClass)),
			Expression = new ThisExpression { SourceSyntax = member.Target?.SourceSyntax, ResolvedType = $"{containingClass.Name}*" },
			ResolvedType = $"{baseClass.Name}*"
		};
		MemberReferenceExpression reference = CreateMemberReference(member, baseReceiver, BuildFunctionValueType(implementation, isInstance: true), implementation);
		expressionRewrites[member] = reference;
		return implementation;
	}

	Dictionary<string, bool> AnalyzeCallArguments(List<ArgumentExpression> arguments, List<ParameterDefinition> parameters, BodyScope scope, AnalysisScope typeScope, SyntaxNode? fallbackSyntax = null, bool includeExplicitThis = false, Dictionary<string, string>? genericSubstitutions = null, HashSet<string>? genericParameterNames = null, FunctionDefinition? function = null, Expression? callTarget = null)
	{
		genericSubstitutions ??= [];
		genericParameterNames ??= [];
		List<ParameterDefinition> callableParameters = GetCallableParameters(parameters, includeExplicitThis);
		if (function is not null && includeExplicitThis && GetExplicitThisParameter(function) is null && IsInstanceFunction(function) && FindContainingType(function) is TypeDefinition containingType)
			callableParameters.Insert(0, CreateImplicitThisParameter(containingType));
		if (arguments.Count > callableParameters.Count || HasExplicitHiddenArgument(arguments))
			AddExplicitHiddenParameters(parameters, callableParameters);
		AnalyzeRangeAwareArguments(arguments, callableParameters, GetRangeReceiver(callTarget), scope, typeScope, fallbackSyntax);
		List<(ArgumentExpression Argument, ParameterDefinition Parameter)> analyzedLifetimeArguments = [];
		List<(ArgumentExpression Argument, ParameterDefinition Parameter)> analyzedConstOfArguments = [];
		Dictionary<string, bool> constOfAnchorsInProgress = new(System.StringComparer.Ordinal);
		bool[] suppliedParameters = new bool[callableParameters.Count];
		bool tooManyArguments = false;
		for (int i = 0; i < arguments.Count; i++)
		{
			ParameterDefinition? parameter = TryBindCallArgumentToParameter(arguments[i], callableParameters, suppliedParameters, fallbackSyntax, out int parameterIndex)
				? callableParameters[parameterIndex]
				: null;
			if (parameter is null && string.IsNullOrWhiteSpace(arguments[i].Name))
				tooManyArguments = true;
			if (parameter is not null && parameter.ResolvedType is null)
				AnalyzeParameterDefinition(parameter, typeScope);

			ParameterDefinition? analysisParameter = parameter;
			int consumedExpandedComponents = 0;
			if (parameter is not null
				&& TryGetLambdaTargetExpandedDelegateParameter(parameter, arguments[i], out ParameterDefinition? sourceParameter, out int componentCount))
			{
				analysisParameter = sourceParameter;
				consumedExpandedComponents = componentCount - 1;
			}

			string expected = SubstituteGenericType(analysisParameter?.ResolvedType ?? ErrorType, genericSubstitutions);
			string analysisTarget = ContainsUnboundGenericParameter(expected, genericSubstitutions, genericParameterNames) ? TargetType : expected;
			string actual = arguments[i].ResolvedType == ErrorType
				? ErrorType
				: BodyAnalyzeArgumentExpression(arguments[i], scope, typeScope, analysisTarget);
			if (analysisParameter is not null)
			{
				InferGenericSubstitutions(analysisParameter.ResolvedType ?? ErrorType, actual, genericSubstitutions, genericParameterNames);
				expected = SubstituteGenericType(analysisParameter.ResolvedType ?? ErrorType, genericSubstitutions);
				if (TryGetConstOfActualSlot(analysisParameter, arguments[i], out bool anchorIsConst) && !string.IsNullOrWhiteSpace(analysisParameter.Name))
					constOfAnchorsInProgress[analysisParameter.Name] = anchorIsConst;
				expected = SubstituteConstOfResolvedType(analysisParameter.Type, expected, constOfAnchorsInProgress, genericSubstitutions);
			}
			if (analysisParameter is not null)
				{
					ParameterDefinition lifetimeParameter = TryGetExpandedSourceParameter(analysisParameter, out ParameterDefinition? expandedSourceParameter, out _)
						? expandedSourceParameter
						: analysisParameter;
					if (analysisParameter.Modifier == ParameterModifier.Out && arguments[i].Modifier != ArgumentModifier.Out)
						Report(GetRange(arguments[i].SourceSyntax ?? fallbackSyntax), "Out parameters require an 'out' argument.");
					if (analysisParameter.Modifier != ParameterModifier.Out && arguments[i].Modifier == ArgumentModifier.Out)
						Report(GetRange(arguments[i].SourceSyntax ?? fallbackSyntax), "Only out parameters may use an 'out' argument.");
					if (analysisParameter.Modifier == ParameterModifier.Thrown && arguments[i].Modifier != ArgumentModifier.Catch)
						Report(GetRange(arguments[i].SourceSyntax ?? fallbackSyntax), "Thrown parameters require a 'catch' argument.");
					if (analysisParameter.Modifier != ParameterModifier.Thrown && arguments[i].Modifier == ArgumentModifier.Catch)
						Report(GetRange(arguments[i].SourceSyntax ?? fallbackSyntax), "Only thrown parameters may use a 'catch' argument.");
					SyntaxNode? argumentSyntax = GetArgumentDiagnosticSyntax(arguments[i], fallbackSyntax);
					if (analysisParameter.Modifier == ParameterModifier.Out)
						CheckCallArgumentAssignable(actual, expected, argumentSyntax, "Out argument", function, genericSubstitutions, genericParameterNames);
					else
					{
						string structuralExpected = GetLifetimeStructuralTargetType(expected, arguments[i].Value);
						bool deferredEscapedLambda = arguments[i].Value is LambdaExpression
							&& ((TryGetLambdaCallableShape(expected, out _, out bool expectedEscapedLambda) && expectedEscapedLambda)
								|| (TryGetLambdaCallableShape(actual, out _, out bool actualEscapedLambda) && actualEscapedLambda));
						if (!deferredEscapedLambda)
							CheckCallArgumentAssignable(structuralExpected, actual, argumentSyntax, "Argument", function, genericSubstitutions, genericParameterNames);
						if (CanLiftToOptional(actual, expected))
							arguments[i].ResolvedType = expected;
					}
					analyzedLifetimeArguments.Add((arguments[i], lifetimeParameter));
					analyzedConstOfArguments.Add((arguments[i], analysisParameter));
				}
			if (ArrayLiteralConsumesLengthParameter(arguments[i], parameter, callableParameters, parameterIndex)
				|| PrimitiveStringConsumesLengthParameter(arguments[i], parameter, callableParameters, parameterIndex, fallbackSyntax))
				MarkSuppliedParameter(suppliedParameters, parameterIndex + 1);
			for (int componentIndex = 1; componentIndex <= consumedExpandedComponents; componentIndex++)
				MarkSuppliedParameter(suppliedParameters, parameterIndex + componentIndex);
		}

		if (parameters.Count > 0 && HasMissingRequiredCallArgument(callableParameters, suppliedParameters))
			Report(GetRange(fallbackSyntax ?? (arguments.Count > 0 ? arguments[^1].SourceSyntax : null)), "Call is missing required arguments.");
		if (parameters.Count > 0 && tooManyArguments)
			Report(GetRange(arguments[^1].SourceSyntax ?? fallbackSyntax), "Call has too many arguments.");
		if (function is not null)
			CheckLifetimeCallArguments(function, callTarget, analyzedLifetimeArguments, scope, fallbackSyntax, genericSubstitutions);
		Dictionary<string, bool> constOfAnchors = BuildConstOfCallAnchorFacts(analyzedConstOfArguments);
		CheckConstOfCallArguments(analyzedConstOfArguments, constOfAnchors, fallbackSyntax);
		return constOfAnchors;
	}

	bool TryBindCallArgumentToParameter(ArgumentExpression argument, List<ParameterDefinition> callableParameters, bool[] suppliedParameters, SyntaxNode? fallbackSyntax, out int parameterIndex)
	{
		parameterIndex = -1;
		if (!string.IsNullOrWhiteSpace(argument.Name))
		{
			parameterIndex = callableParameters.FindIndex(parameter => parameter.Name == argument.Name);
			if (parameterIndex < 0)
			{
				Report(GetRange(argument.SourceSyntax ?? fallbackSyntax), $"No parameter named '{argument.Name}' exists for this call.");
				return false;
			}
			if (suppliedParameters[parameterIndex])
			{
				Report(GetRange(argument.SourceSyntax ?? fallbackSyntax), $"Argument '{argument.Name}' was already supplied.");
				return false;
			}
			suppliedParameters[parameterIndex] = true;
			return true;
		}

		if (argument.Value is SizeOfExpression)
		{
			parameterIndex = FindNextUnsuppliedParameter(callableParameters, suppliedParameters, static parameter => parameter is SizeOfParameterDefinition);
			if (parameterIndex >= 0)
			{
				suppliedParameters[parameterIndex] = true;
				return true;
			}
		}
		else if (argument.Value is NameOfExpression)
		{
			parameterIndex = FindNextUnsuppliedParameter(callableParameters, suppliedParameters, static parameter => parameter is NameOfParameterDefinition);
			if (parameterIndex >= 0)
			{
				suppliedParameters[parameterIndex] = true;
				return true;
			}
		}
		else if (argument.Value is VTableOfExpression)
		{
			parameterIndex = FindNextUnsuppliedParameter(callableParameters, suppliedParameters, static parameter => parameter is VTableOfParameterDefinition);
			if (parameterIndex >= 0)
			{
				suppliedParameters[parameterIndex] = true;
				return true;
			}
		}
		else if (argument.Modifier == ArgumentModifier.Catch)
		{
			parameterIndex = FindNextUnsuppliedParameter(callableParameters, suppliedParameters, static parameter => parameter.Modifier == ParameterModifier.Thrown);
			if (parameterIndex >= 0)
			{
				suppliedParameters[parameterIndex] = true;
				return true;
			}
		}
		else if (argument.Value is WithinExpression { Expression: null })
		{
			parameterIndex = FindNextUnsuppliedParameter(callableParameters, suppliedParameters, IsWithinParameter);
			if (parameterIndex >= 0)
			{
				suppliedParameters[parameterIndex] = true;
				return true;
			}
		}
		else if (IsNullArgumentExpression(argument))
		{
			parameterIndex = FindNextUnsuppliedParameter(callableParameters, suppliedParameters, parameter => IsWithinParameter(parameter) || IsUponParameter(parameter));
			if (parameterIndex >= 0)
			{
				suppliedParameters[parameterIndex] = true;
				return true;
			}
		}
		else if (GetGeneratedHiddenForwardingArgumentName(argument) is string generatedHiddenName
			&& TryBindGeneratedHiddenForwardingArgument(generatedHiddenName, callableParameters, suppliedParameters, out parameterIndex))
		{
			return true;
		}

		parameterIndex = FindNextUnsuppliedPositionalParameter(callableParameters, suppliedParameters);
		if (parameterIndex < 0)
			parameterIndex = FindNextExplicitlySuppliedHiddenParameter(callableParameters, suppliedParameters);
		if (parameterIndex < 0)
			return false;
		suppliedParameters[parameterIndex] = true;
		return true;
	}

	static int FindNextUnsuppliedPositionalParameter(List<ParameterDefinition> callableParameters, bool[] suppliedParameters)
	{
		return FindNextUnsuppliedParameter(callableParameters, suppliedParameters, static parameter => !IsImplicitOnlyCallParameter(parameter) && parameter.Modifier is not ParameterModifier.Thrown && !IsWithinParameter(parameter));
	}

	static int FindNextExplicitlySuppliedHiddenParameter(List<ParameterDefinition> callableParameters, bool[] suppliedParameters)
	{
		return FindNextUnsuppliedParameter(callableParameters, suppliedParameters, IsGeneratedHiddenForwardingParameter);
	}

	static bool TryBindGeneratedHiddenForwardingArgument(string name, List<ParameterDefinition> callableParameters, bool[] suppliedParameters, out int parameterIndex)
	{
		parameterIndex = FindNextUnsuppliedParameter(
			callableParameters,
			suppliedParameters,
			parameter => IsGeneratedHiddenForwardingParameter(parameter) && parameter.Name == name);
		if (parameterIndex < 0)
			return false;
		suppliedParameters[parameterIndex] = true;
		return true;
	}

	static string? GetGeneratedHiddenForwardingArgumentName(ArgumentExpression argument)
	{
		return argument.Value switch
		{
			NamedExpression { Qualifiers.Count: 0 } named => named.Name,
			VariableReferenceExpression { Variable: Definition definition } => definition.Name,
			_ => null
		};
	}

	static bool IsGeneratedHiddenForwardingArgumentFor(ParameterDefinition parameter, ArgumentExpression argument)
	{
		return IsGeneratedHiddenForwardingParameter(parameter)
			&& (GetGeneratedHiddenForwardingArgumentName(argument) == parameter.Name
				|| parameter is SizeOfParameterDefinition
					&& argument.Value is MemberExpression member
					&& member.Name == "_" + parameter.Name
				|| parameter is SizeOfParameterDefinition
					&& argument.Value is MemberReferenceExpression memberReference
					&& memberReference.Name == "_" + parameter.Name);
	}

	static bool IsNullArgumentExpression(ArgumentExpression argument)
	{
		return argument.Value is LiteralExpression { Kind: LiteralKind.Null }
			|| argument.Value is NamedExpression { Qualifiers.Count: 0, Name: "null" };
	}

	static int FindNextUnsuppliedParameter(List<ParameterDefinition> callableParameters, bool[] suppliedParameters, Func<ParameterDefinition, bool> predicate)
	{
		for (int i = 0; i < callableParameters.Count; i++)
			if (!suppliedParameters[i] && predicate(callableParameters[i]))
				return i;
		return -1;
	}

	static bool HasMissingRequiredCallArgument(List<ParameterDefinition> callableParameters, bool[] suppliedParameters)
	{
		for (int i = 0; i < callableParameters.Count; i++)
		{
			ParameterDefinition parameter = callableParameters[i];
			if (suppliedParameters[i]
				|| parameter.DefaultValue is not null
				|| IsImplicitOnlyCallParameter(parameter)
				|| parameter.Modifier is ParameterModifier.Thrown or ParameterModifier.Within or ParameterModifier.Upon
				|| parameter is WithinParameterDefinition)
				continue;
			return true;
		}
		return false;
	}

	static bool IsImplicitOnlyCallParameter(ParameterDefinition parameter)
	{
		return parameter is SizeOfParameterDefinition or NameOfParameterDefinition or VTableOfParameterDefinition;
	}

	static bool IsGeneratedHiddenForwardingParameter(ParameterDefinition parameter)
	{
		return IsImplicitOnlyCallParameter(parameter)
			|| parameter.Modifier is ParameterModifier.Thrown or ParameterModifier.Within or ParameterModifier.Upon
			|| parameter is WithinParameterDefinition;
	}

	static void MarkSuppliedParameter(bool[] suppliedParameters, int parameterIndex)
	{
		if (parameterIndex >= 0 && parameterIndex < suppliedParameters.Length)
			suppliedParameters[parameterIndex] = true;
	}

	bool TryGetLambdaTargetExpandedDelegateParameter(ParameterDefinition componentParameter, ArgumentExpression argument, out ParameterDefinition sourceParameter, out int componentCount)
	{
		sourceParameter = componentParameter;
		componentCount = 0;
		if (argument.Value is not LambdaExpression)
			return false;

		if (!TryGetExpandedSourceParameter(componentParameter, out sourceParameter, out componentCount))
			return false;
		if (!TryGetParamsComponentShape(sourceParameter.Type, sourceParameter.ResolvedType, sourceParameter.Name, out ParamsComponentShape shape)
			|| shape.Kind != ParamsComponentShapeKind.Delegate)
			return false;

		return componentCount > 1;
	}

	bool TryGetExpandedSourceParameter(ParameterDefinition componentParameter, out ParameterDefinition sourceParameter, out int componentCount)
	{
		sourceParameter = componentParameter;
		componentCount = 0;
		foreach ((BindableNode source, List<ParamsExpansionComponent> expansion) in paramsExpansions)
		{
			if (source is not ParameterDefinition parameter || expansion.Count == 0)
				continue;
			if (!ReferenceEquals(expansion[0].Node, componentParameter))
				continue;

			sourceParameter = parameter;
			componentCount = expansion.Count;
			return componentCount > 1;
		}

		return false;
	}

	SyntaxNode? GetArgumentDiagnosticSyntax(ArgumentExpression argument, SyntaxNode? fallbackSyntax)
	{
		return GetExpressionDiagnosticSyntax(argument.Value) ?? argument.SourceSyntax ?? fallbackSyntax;
	}

	SyntaxNode? GetExpressionDiagnosticSyntax(Expression? expression)
	{
		return expression switch
		{
			null => null,
			_ when expression.SourceSyntax is not null => expression.SourceSyntax,
			ArgumentExpression argument => argument.SourceSyntax ?? GetExpressionDiagnosticSyntax(argument.Value),
			ParenthesizedExpression parenthesized => GetExpressionDiagnosticSyntax(parenthesized.Expression),
			CastExpression cast => GetExpressionDiagnosticSyntax(cast.Expression),
			WithinExpression within => GetExpressionDiagnosticSyntax(within.Expression) ?? GetExpressionDiagnosticSyntax(within.Context),
			FinallyDeleteExpression finallyDelete => GetExpressionDiagnosticSyntax(finallyDelete.Expression),
			UnaryExpression unary => GetExpressionDiagnosticSyntax(unary.Operand) ?? GetExpressionDiagnosticSyntax(unary.Context),
			PostfixUpdateExpression postfix => GetExpressionDiagnosticSyntax(postfix.Expression),
			BinaryExpression binary => GetExpressionDiagnosticSyntax(binary.Left) ?? GetExpressionDiagnosticSyntax(binary.Right),
			ConditionalExpression conditional => GetExpressionDiagnosticSyntax(conditional.Condition) ?? GetExpressionDiagnosticSyntax(conditional.WhenTrue) ?? GetExpressionDiagnosticSyntax(conditional.WhenFalse),
			RangeExpression range => GetExpressionDiagnosticSyntax(range.Start) ?? GetExpressionDiagnosticSyntax(range.End),
			CallExpression call => GetExpressionDiagnosticSyntax(call.Target) ?? GetArgumentListDiagnosticSyntax(call.Arguments),
			IndexExpression index => GetExpressionDiagnosticSyntax(index.Target) ?? GetArgumentListDiagnosticSyntax(index.Arguments),
			MemberExpression member => GetExpressionDiagnosticSyntax(member.Target),
			MemberReferenceExpression member => GetExpressionDiagnosticSyntax(member.Target),
			NamelessIndexerExpression indexer => GetExpressionDiagnosticSyntax(indexer.Target) ?? GetArgumentListDiagnosticSyntax(indexer.Arguments),
			GroupedExpression grouped => GetGroupedExpressionDiagnosticSyntax(grouped),
			ArrayExpression array => GetExpressionListDiagnosticSyntax(array.Elements),
			InitializerExpression initializer => GetInitializerDiagnosticSyntax(initializer),
			ConstructionExpression construction => GetArgumentListDiagnosticSyntax(construction.Arguments) ?? GetExpressionDiagnosticSyntax(construction.ElementCount) ?? GetExpressionDiagnosticSyntax(construction.Initializer),
			_ => null
		};
	}

	SyntaxNode? GetArgumentListDiagnosticSyntax(List<ArgumentExpression> arguments)
	{
		foreach (ArgumentExpression argument in arguments)
			if (GetArgumentDiagnosticSyntax(argument, null) is SyntaxNode syntax)
				return syntax;
		return null;
	}

	SyntaxNode? GetExpressionListDiagnosticSyntax(List<Expression> expressions)
	{
		foreach (Expression expression in expressions)
			if (GetExpressionDiagnosticSyntax(expression) is SyntaxNode syntax)
				return syntax;
		return null;
	}

	SyntaxNode? GetGroupedExpressionDiagnosticSyntax(GroupedExpression grouped)
	{
		foreach (GroupedExpressionItem item in grouped.Items)
			if (GetExpressionDiagnosticSyntax(item.Expression) is SyntaxNode syntax)
				return syntax;
		return null;
	}

	SyntaxNode? GetInitializerDiagnosticSyntax(InitializerExpression initializer)
	{
		foreach (InitializerItem item in initializer.Items)
			if (GetInitializerItemDiagnosticSyntax(item) is SyntaxNode syntax)
				return syntax;
		return null;
	}

	void CheckCallArgumentAssignable(
		string expected,
		string actual,
		SyntaxNode? syntax,
		string context,
		FunctionDefinition? function,
		Dictionary<string, string>? substitutions,
		HashSet<string>? genericParameterNames)
	{
		if (expected == ErrorType || actual == ErrorType || expected == TargetType || actual == TargetType)
			return;
		if (CanAssignToType(expected, actual))
			return;

		if (function is not null
			&& genericParameterNames is { Count: > 0 }
			&& ContainsUnboundGenericParameter(expected, substitutions ?? [], genericParameterNames))
		{
			Report(GetRange(syntax), $"{context} cannot convert '{actual}' to '{expected}' because type arguments for '{function.Name}' cannot be inferred; specify them explicitly.");
			return;
		}

		Report(GetRange(syntax), $"{context} cannot convert '{actual}' to '{expected}'.");
	}

	static ThisParameterDefinition CreateImplicitThisParameter(TypeDefinition containingType)
	{
		ThisParameterDefinition parameter = new()
		{
			Name = "this",
			Symbol = "this",
			Type = PointerTo(TypeReferenceFor(containingType)),
			ResolvedType = containingType.Name + "*"
		};
		return parameter;
	}

	static bool ArrayLiteralConsumesLengthParameter(ArgumentExpression argument, ParameterDefinition? parameter, List<ParameterDefinition> callableParameters, int parameterIndex)
	{
		if (argument.Value is not ArrayExpression || parameter is null || parameterIndex + 1 >= callableParameters.Count)
			return false;
		if (argument.Modifier != ArgumentModifier.None)
			return false;
		if (TryGetPointerElementType(parameter.ResolvedType) is null)
			return false;

		string lengthType = callableParameters[parameterIndex + 1].ResolvedType ?? "";
		return StripTopLevelValueQualifiers(lengthType) is "nuint" or "nint" or "uint" or "int" or "ulong" or "long" or "ushort" or "short";
	}

	bool PrimitiveStringConsumesLengthParameter(ArgumentExpression argument, ParameterDefinition? parameter, List<ParameterDefinition> callableParameters, int parameterIndex, SyntaxNode? fallbackSyntax)
	{
		if (argument.Value is null || parameter is null || parameterIndex + 1 >= callableParameters.Count)
			return false;
		string actual = argument.Value.ResolvedType ?? argument.ResolvedType ?? ErrorType;
		if (GetPrimitiveStringElementType(actual) is not string stringElement)
			return false;
		if (parameter.ResolvedType is not string pointerType || TryGetPointerElementType(pointerType) is not string pointerElement)
			return false;
		if (StripTopLevelValueQualifiers(pointerElement) != stringElement || !IsConstQualified(pointerElement))
			return false;
		string lengthType = callableParameters[parameterIndex + 1].ResolvedType ?? "";
		if (StripTopLevelValueQualifiers(lengthType) is not ("nuint" or "nint" or "uint" or "int" or "ulong" or "long" or "ushort" or "short"))
			return false;

		if (CreateLengthExpression(argument.Value, argument.SourceSyntax ?? fallbackSyntax) is null)
			Report(GetRange(argument.SourceSyntax ?? fallbackSyntax), $"Cannot implicitly convert '{actual}' to 'const {stringElement}[]' because no accessible Length property or getLength() method was found.");
		return true;
	}

	static bool HasExplicitHiddenArgument(List<ArgumentExpression> arguments)
	{
		foreach (ArgumentExpression argument in arguments)
			if (IsExplicitHiddenArgument(argument))
				return true;
		return false;
	}

	static bool IsExplicitHiddenArgument(ArgumentExpression argument)
	{
		return argument.Modifier == ArgumentModifier.Catch
			|| argument.Value is WithinExpression { Expression: null }
			|| argument.Value is SizeOfExpression
			|| argument.Value is NameOfExpression
			|| argument.Value is VTableOfExpression;
	}

	void InferGenericSubstitutions(string pattern, string actual, Dictionary<string, string> substitutions, HashSet<string> genericParameterNames)
	{
		if (string.IsNullOrWhiteSpace(pattern) || actual == ErrorType)
			return;

		if (TryGetCallableShape(pattern, out CallableShape patternCallable)
			&& TryGetCallableShape(actual, out CallableShape actualCallable))
		{
			InferGenericCallableSubstitutions(patternCallable, actualCallable, substitutions, genericParameterNames);
			return;
		}

		if (new TypeShapeParser(pattern).TryParse(out TypeShape patternShape) && new TypeShapeParser(actual).TryParse(out TypeShape actualShape))
		{
			InferGenericSubstitutions(patternShape, actualShape, substitutions, genericParameterNames);
			return;
		}

		if (genericParameterNames.Contains(pattern))
		{
			if (!substitutions.ContainsKey(pattern))
				substitutions[pattern] = actual;
			return;
		}

		foreach (string name in ExtractGenericNames(pattern, genericParameterNames))
		{
			if (!substitutions.ContainsKey(name) && pattern == name)
				substitutions[name] = actual;
		}
	}

	void InferGenericSubstitutions(TypeShape pattern, TypeShape actual, Dictionary<string, string> substitutions, HashSet<string> genericParameterNames)
	{
		if (pattern.Kind == TypeShapeKind.Named && genericParameterNames.Contains(pattern.Name))
		{
			if (!substitutions.ContainsKey(pattern.Name))
				substitutions[pattern.Name] = TypeShapeParser.Format(actual);
			return;
		}

		if (pattern.Kind != actual.Kind)
			return;

		if (pattern.Kind == TypeShapeKind.Named)
		{
			if (BaseTypeName(pattern.Name) != BaseTypeName(actual.Name))
				return;

			List<string> patternArguments = ExtractConstructedTypeArguments(pattern.Name);
			List<string> actualArguments = ExtractConstructedTypeArguments(actual.Name);
			if (patternArguments.Count != actualArguments.Count)
				return;

			for (int i = 0; i < patternArguments.Count; i++)
				InferGenericSubstitutions(patternArguments[i], actualArguments[i], substitutions, genericParameterNames);
			return;
		}

		if (pattern.Element is not null && actual.Element is not null)
			InferGenericSubstitutions(pattern.Element, actual.Element, substitutions, genericParameterNames);
	}

	void InferGenericCallableSubstitutions(CallableShape pattern, CallableShape actual, Dictionary<string, string> substitutions, HashSet<string> genericParameterNames)
	{
		pattern = ExpandCallableShape(pattern);
		actual = ExpandCallableShape(actual);
		if (pattern.Kind != actual.Kind || pattern.Parameters.Count != actual.Parameters.Count)
			return;

		InferGenericSubstitutions(pattern.ReturnType, actual.ReturnType, substitutions, genericParameterNames);
		for (int i = 0; i < pattern.Parameters.Count; i++)
			InferGenericSubstitutions(pattern.Parameters[i], actual.Parameters[i], substitutions, genericParameterNames);
	}

	static bool ContainsUnboundGenericParameter(string type, Dictionary<string, string> substitutions, HashSet<string> genericParameterNames)
	{
		foreach (string name in ExtractGenericNames(type, genericParameterNames))
		{
			if (!substitutions.ContainsKey(name))
				return true;
		}
		return false;
	}

	static string SubstituteGenericType(string type, Dictionary<string, string> substitutions)
	{
		foreach ((string name, string replacement) in substitutions)
			type = ReplaceTypeIdentifier(type, name, replacement);
		return type;
	}

	static IEnumerable<string> ExtractGenericNames(string type, HashSet<string> genericParameterNames)
	{
		int start = -1;
		for (int i = 0; i <= type.Length; i++)
		{
			bool identifier = i < type.Length && IsIdentifierPart(type[i]);
			if (identifier && start < 0)
				start = i;
			else if (!identifier && start >= 0)
			{
				string token = type[start..i];
				if (genericParameterNames.Contains(token))
					yield return token;
				start = -1;
			}
		}
	}

	static string ReplaceTypeIdentifier(string type, string name, string replacement)
	{
		List<string> parts = [];
		int start = 0;
		for (int i = 0; i < type.Length;)
		{
			if (IsIdentifierStart(type[i]))
			{
				int tokenStart = i++;
				while (i < type.Length && IsIdentifierPart(type[i]))
					i++;
				if (type[tokenStart..i] == name)
				{
					parts.Add(type[start..tokenStart]);
					parts.Add(replacement);
					start = i;
				}
			}
			else
			{
				i++;
			}
		}
		parts.Add(type[start..]);
		return string.Concat(parts);
	}

	static bool IsIdentifier(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || !IsIdentifierStart(value[0]))
			return false;
		for (int i = 1; i < value.Length; i++)
		{
			if (!IsIdentifierPart(value[i]))
				return false;
		}
		return true;
	}

	static bool IsIdentifierStart(char c)
	{
		return char.IsLetter(c) || c == '_';
	}

	static bool IsIdentifierPart(char c)
	{
		return char.IsLetterOrDigit(c) || c == '_';
	}

	static void AddExplicitHiddenParameters(List<ParameterDefinition> parameters, List<ParameterDefinition> callableParameters)
	{
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter.Modifier is ParameterModifier.Thrown or ParameterModifier.Within
				|| parameter is WithinParameterDefinition
					or SizeOfParameterDefinition
					or NameOfParameterDefinition
					or VTableOfParameterDefinition)
			{
				if (callableParameters.Contains(parameter))
					continue;
				callableParameters.Add(parameter);
			}
		}
	}

	int FindArgumentIndexForCallableParameter(List<ArgumentExpression> arguments, List<ParameterDefinition> callableParameters, int parameterIndex)
	{
		int currentParameter = 0;
		for (int argumentIndex = 0; argumentIndex < arguments.Count; argumentIndex++)
		{
			if (currentParameter >= parameterIndex)
				return argumentIndex;
			int consumed = CountCallableParametersSatisfiedByArgument(arguments[argumentIndex], callableParameters, currentParameter);
			if (parameterIndex < currentParameter + consumed)
				return argumentIndex;
			currentParameter += consumed;
		}
		return arguments.Count;
	}

	void EnsureFunctionSignatureAnalyzed(FunctionDefinition function, AnalysisScope scope)
	{
		if (function.ResolvedType is null)
		{
			if (function.Modifier == FunctionModifier.Constructor)
				function.ResolvedType = FindContainingType(function)?.Name ?? ConstructorType;
			else if (IsDestructorFunction(function))
				function.ResolvedType = "void";
			else
				function.ResolvedType = AnalyzeOptionalType(function.ReturnType, scope) ?? ErrorType;
		}

		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.ResolvedType is null)
				AnalyzeParameterDefinition(parameter, scope);
		}
	}

	void ValidateBaseConstructorInvocation(FunctionDefinition function, TypeDefinition? containingType)
	{
		if (!IsConstructorLikeFunction(function) || function.Body is null)
			return;

		Expression? firstAction = GetFirstConstructorAction(function.Body);
		CallExpression? firstBaseCall = firstAction is CallExpression firstCall && IsBaseConstructorCall(firstCall) ? firstCall : null;
		bool hasBaseCall = false;

		foreach (CallExpression baseCall in EnumerateBaseConstructorCalls(function.Body))
		{
			hasBaseCall = true;
			if (!ReferenceEquals(baseCall, firstBaseCall))
				Report(GetRange(baseCall.Target?.SourceSyntax ?? baseCall.SourceSyntax), "base(...) must be the first constructor action and may not be conditional or delayed.");
		}

		if (containingType is not ClassDefinition containingClass)
			return;

		ClassDefinition? baseClass = GetDirectBaseClass(containingClass);
		if (baseClass is null || hasBaseCall)
			return;

		if (IsGeneratedBaseInitCall(firstAction, baseClass))
			return;

		FunctionDefinition? baseInitNew = FindGeneratedInitNewMethod(baseClass);
		if (baseInitNew is not null)
		{
			InsertImplicitBaseConstructorCall(function.Body, containingClass, baseClass, baseInitNew);
			return;
		}

		if (TryGetAccessibleParameterlessConstructor(baseClass, out FunctionDefinition? parameterlessConstructor))
		{
			if (parameterlessConstructor is not null)
				InsertImplicitBaseConstructorCall(function.Body, containingClass, baseClass, parameterlessConstructor);
			return;
		}

		Report(GetRange(function.Body.SourceSyntax ?? function.SourceSyntax), $"Constructor for class '{containingClass.Name}' must invoke a base constructor because base class '{baseClass.Name}' has no accessible parameterless constructor.");
	}

	bool IsGeneratedBaseInitCall(Expression? expression, ClassDefinition baseClass)
	{
		return expression is CallExpression { Target: MemberReferenceExpression { Member: FunctionDefinition member } }
			&& member.Name == InitNewMethodName
			&& ReferenceEquals(FindContainingType(member), baseClass);
	}

	void InsertImplicitBaseConstructorCall(BlockStatement body, ClassDefinition containingClass, ClassDefinition baseClass, FunctionDefinition constructor)
	{
		FunctionDefinition? initNew = FindGeneratedInitNewMethod(FindContainingType(constructor));
		CallExpression call = new()
		{
			Target = CreateBaseInitNewReference(constructor, initNew, containingClass, baseClass),
			ResolvedType = "void"
		};
		if (initNew is not null)
			callTargets[call] = initNew;

		body.Statements.Insert(0, new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = call
		});
	}

	MemberReferenceExpression CreateBaseInitNewReference(FunctionDefinition constructor, FunctionDefinition? initNew, ClassDefinition containingClass, ClassDefinition baseClass)
	{
		FunctionDefinition target = initNew ?? constructor;
		MemberReferenceExpression reference = new()
		{
			Target = CreateBaseThisReceiver(containingClass, baseClass, constructor.SourceSyntax),
			Name = target.Name,
			Member = target,
			ResolvedType = BuildFunctionValueType(target, isInstance: true)
		};
		reference.Candidates.Add(target);
		return reference;
	}

	static CastExpression CreateBaseThisReceiver(ClassDefinition containingClass, ClassDefinition baseClass, SyntaxNode? syntax)
	{
		return new CastExpression
		{
			SourceSyntax = syntax,
			Kind = CastKind.Type,
			Type = PointerTo(TypeReferenceFor(baseClass)),
			Expression = new ThisExpression { SourceSyntax = syntax, ResolvedType = $"{containingClass.Name}*" },
			ResolvedType = $"{baseClass.Name}*"
		};
	}

	static Expression? GetFirstConstructorAction(BlockStatement body)
	{
		foreach (Statement statement in body.Statements)
		{
			if (statement is EmptyStatement)
				continue;

			return statement is ExpressionStatement expressionStatement ? expressionStatement.Expression : null;
		}

		return null;
	}

	static bool IsBaseConstructorCall(CallExpression call)
	{
		return call.Target is NamedExpression { Qualifiers: { Count: 0 }, Name: "base" };
	}

	static IEnumerable<CallExpression> EnumerateBaseConstructorCalls(BlockStatement body)
	{
		foreach (Statement statement in body.Statements)
		{
			foreach (CallExpression call in EnumerateBaseConstructorCalls(statement))
				yield return call;
		}
	}

	static IEnumerable<CallExpression> EnumerateBaseConstructorCalls(Statement? statement)
	{
		switch (statement)
		{
			case null:
			case EmptyStatement:
			case BreakStatement:
			case ContinueStatement:
			case DefaultStatement:
				yield break;

			case BlockStatement block:
				foreach (Statement child in block.Statements)
				{
					foreach (CallExpression call in EnumerateBaseConstructorCalls(child))
						yield return call;
				}
				yield break;

			case ExpressionStatement expression:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(expression.Expression))
					yield return call;
				yield break;

			case DeclarationStatement declaration:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(declaration.InitialValue))
					yield return call;
				yield break;

			case IfStatement ifStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(ifStatement.Condition))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(ifStatement.Body))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(ifStatement.ElseBody))
					yield return call;
				yield break;

			case WhileStatement whileStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(whileStatement.Condition))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(whileStatement.Body))
					yield return call;
				yield break;

			case DoWhileStatement doWhile:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(doWhile.Body))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(doWhile.Condition))
					yield return call;
				yield break;

			case ForStatement forStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(forStatement.Condition.Declaration))
					yield return call;
				foreach (Expression? clause in forStatement.Condition.Clauses)
				{
					foreach (CallExpression call in EnumerateBaseConstructorCalls(clause))
						yield return call;
				}
				foreach (CallExpression call in EnumerateBaseConstructorCalls(forStatement.Body))
					yield return call;
				yield break;

			case ForeachStatement foreachStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(foreachStatement.Source))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(foreachStatement.Body))
					yield return call;
				yield break;

			case SwitchStatement switchStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(switchStatement.Expression))
					yield return call;
				foreach (Statement child in switchStatement.Statements)
				{
					foreach (CallExpression call in EnumerateBaseConstructorCalls(child))
						yield return call;
				}
				yield break;

			case CaseStatement caseStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(caseStatement.Expression))
					yield return call;
				yield break;

			case ReturnStatement returnStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(returnStatement.Expression))
					yield return call;
				yield break;

			case YieldStatement yieldStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(yieldStatement.Expression))
					yield return call;
				yield break;

			case DeleteStatement deleteStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(deleteStatement.Expression))
					yield return call;
				yield break;

			case TryStatement tryStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(tryStatement.Body))
					yield return call;
				foreach (CatchStatement catchStatement in tryStatement.Catches)
				{
					foreach (CallExpression call in EnumerateBaseConstructorCalls(catchStatement))
						yield return call;
				}
				foreach (CallExpression call in EnumerateBaseConstructorCalls(tryStatement.Finally))
					yield return call;
				yield break;

			case CatchStatement catchStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(catchStatement.Body))
					yield return call;
				yield break;

			case FinallyStatement finallyStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(finallyStatement.Body))
					yield return call;
				yield break;

			case WithinStatement withinStatement:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(withinStatement.Allocator))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(withinStatement.Body))
					yield return call;
				yield break;
		}
	}

	static IEnumerable<CallExpression> EnumerateBaseConstructorCalls(Expression? expression)
	{
		switch (expression)
		{
			case null:
				yield break;

			case CallExpression call:
				if (IsBaseConstructorCall(call))
					yield return call;
				foreach (ArgumentExpression argument in call.Arguments)
				{
					foreach (CallExpression child in EnumerateBaseConstructorCalls(argument))
						yield return child;
				}
				yield break;

			case ArgumentExpression argument:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(argument.Value))
					yield return call;
				yield break;

			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
				{
					foreach (CallExpression call in EnumerateBaseConstructorCalls(item.Expression))
						yield return call;
				}
				yield break;

			case ArrayExpression array:
				foreach (Expression element in array.Elements)
				{
					foreach (CallExpression call in EnumerateBaseConstructorCalls(element))
						yield return call;
				}
				yield break;

			case InitializerExpression initializer:
				foreach (InitializerItem item in initializer.Items)
				{
					foreach (CallExpression call in EnumerateBaseConstructorCalls(item.Expression))
						yield return call;
				}
				yield break;

			case ParenthesizedExpression parenthesized:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(parenthesized.Expression))
					yield return call;
				yield break;

			case CastExpression cast:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(cast.Expression))
					yield return call;
				yield break;

			case ConstructionExpression construction:
				foreach (ArgumentExpression argument in construction.Arguments)
				{
					foreach (CallExpression call in EnumerateBaseConstructorCalls(argument))
						yield return call;
				}
				foreach (CallExpression call in EnumerateBaseConstructorCalls(construction.ElementCount))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(construction.Initializer))
					yield return call;
				yield break;

			case WithinExpression within:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(within.Context))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(within.Expression))
					yield return call;
				yield break;

			case LambdaExpression:
				yield break;

			case IndexExpression index:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(index.Target))
					yield return call;
				foreach (ArgumentExpression argument in index.Arguments)
				{
					foreach (CallExpression call in EnumerateBaseConstructorCalls(argument))
						yield return call;
				}
				yield break;

			case MemberExpression member:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(member.Target))
					yield return call;
				yield break;

			case NamelessIndexerExpression nameless:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(nameless.Target))
					yield return call;
				foreach (ArgumentExpression argument in nameless.Arguments)
				{
					foreach (CallExpression call in EnumerateBaseConstructorCalls(argument))
						yield return call;
				}
				yield break;

			case UnaryExpression unary:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(unary.Context))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(unary.Operand))
					yield return call;
				yield break;

			case PostfixUpdateExpression postfix:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(postfix.Expression))
					yield return call;
				yield break;

			case FinallyDeleteExpression finallyDelete:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(finallyDelete.Expression))
					yield return call;
				yield break;

			case BinaryExpression binary:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(binary.Left))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(binary.Right))
					yield return call;
				yield break;

			case AssignmentExpression assignment:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(assignment.Target))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(assignment.Value))
					yield return call;
				yield break;

			case ConditionalExpression conditional:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(conditional.Condition))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(conditional.WhenTrue))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(conditional.WhenFalse))
					yield return call;
				yield break;

			case RangeExpression range:
				foreach (CallExpression call in EnumerateBaseConstructorCalls(range.Start))
					yield return call;
				foreach (CallExpression call in EnumerateBaseConstructorCalls(range.End))
					yield return call;
				yield break;
		}
	}

	FunctionDefinition? ResolveBaseConstructorCall(NamedExpression target, BodyScope scope, int argumentCount)
	{
		if (!IsConstructorLikeFunction(scope.CurrentFunction) || scope.ContainingType is not ClassDefinition containingClass)
		{
			Report(GetRange(target.SourceSyntax), "base(...) is valid only in a class constructor.");
			target.ResolvedType = ErrorType;
			return null;
		}

		ClassDefinition? baseClass = GetDirectBaseClass(containingClass);
		if (baseClass is null)
		{
			Report(GetRange(target.SourceSyntax), $"Class '{containingClass.Name}' does not have a base class constructor to invoke.");
			target.ResolvedType = ErrorType;
			return null;
		}

		FunctionDefinition? constructor = LookupConstructor(baseClass.Name, argumentCount);
		if (constructor is null)
		{
			Report(GetRange(target.SourceSyntax), $"No accessible constructor on base class '{baseClass.Name}' accepts {argumentCount.ToString(CultureInfo.InvariantCulture)} argument(s).");
			target.ResolvedType = ErrorType;
			return null;
		}

		FunctionDefinition? initNew = FindGeneratedInitNewMethod(baseClass);
		target.ResolvedType = "void";
		MemberReferenceExpression reference = CreateBaseInitNewReference(constructor, initNew, containingClass, baseClass);
		reference.SourceSyntax = target.SourceSyntax;
		expressionRewrites[target] = reference;
		return initNew ?? constructor;
	}

	string BodyAnalyzeIndexExpression(IndexExpression index, BodyScope scope, AnalysisScope typeScope)
	{
		return BodyAnalyzeIndexExpression(index.Target, index.Arguments, scope, typeScope);
	}

	string BodyAnalyzeIndexExpression(Expression? target, List<ArgumentExpression> arguments, BodyScope scope, AnalysisScope typeScope)
	{
		if (target is MemberExpression member && TryAnalyzePropertyIndexer(member, arguments, scope, typeScope, out string propertyType))
			return propertyType;

		string targetType = BodyAnalyzeExpression(target, scope, typeScope);
		if (targetType == ErrorType)
			return ErrorType;

		if (arguments is [{ Value: RangeExpression range }])
			return BodyAnalyzeArrayRangeIndexExpression(target, targetType, arguments, range, scope, typeScope);

		foreach (ArgumentExpression argument in arguments)
		{
			if (argument.Value is UnaryExpression { Operator: UnaryOperator.FromEnd } fromEnd)
			{
				SyntaxNode? syntax = fromEnd.SourceSyntax ?? fromEnd.Operand?.SourceSyntax ?? argument.SourceSyntax;
				if (TryGetPointerElementType(targetType) is not null && TryGetArrayElementType(targetType) is null)
				{
					Report(GetRange(syntax), "^ from-end indexing is not valid on plain pointer values.");
				}
				else
				{
					Expression? length = CreateLengthExpression(target, syntax);
					if (length is null)
						Report(GetRange(syntax), "^ from-end syntax requires the receiver to expose a length (.length, .Length, or getLength()).");
					else
					{
						argument.Value = CreateFromEndExpression(fromEnd, length);
						argument.ResolvedType = "nuint";
					}
				}
			}
			BodyAnalyzeArgumentExpression(argument, scope, typeScope, "nuint");
		}

		if (TryGetArrayElementType(targetType) is string elementType)
		{
			RequireGenericArrayElementStride(targetType, scope, target?.SourceSyntax, "index T[]");
			return elementType;
		}

		if (TryGetPointerElementType(targetType) is string pointerElementType)
			return pointerElementType;

		if (GetPrimitiveStringElementType(targetType) is string stringElementType)
			return stringElementType;

		Report(GetRange(target?.SourceSyntax), $"Type '{targetType}' is not indexable.");
		return ErrorType;
	}

	string BodyAnalyzeArrayRangeIndexExpression(Expression? target, string targetType, List<ArgumentExpression> arguments, RangeExpression range, BodyScope scope, AnalysisScope typeScope)
	{
		ArgumentExpression argument = arguments[0];
		BodyAnalyzeRangeExpression(range, scope, typeScope);
		string? arrayElementType = TryGetArrayElementType(targetType);
		string? stringElementType = GetPrimitiveStringElementType(targetType);
		if (arrayElementType is null && stringElementType is null)
		{
			if (TryGetPointerElementType(targetType) is string pointerElementType && TryGetArrayElementType(pointerElementType) is not null)
				Report(GetRange(range.SourceSyntax ?? argument.SourceSyntax ?? target?.SourceSyntax), "Range indexing is only valid on array values; dereference the array pointer before applying the range.");
			else
				Report(GetRange(range.SourceSyntax ?? argument.SourceSyntax ?? target?.SourceSyntax), "Range indexing is only valid on array or string values.");
			argument.Value = ErrorExpression(ErrorType, range.SourceSyntax ?? argument.SourceSyntax);
			argument.ResolvedType = ErrorType;
			return ErrorType;
		}
		if (arrayElementType is not null)
			RequireGenericArrayElementStride(targetType, scope, range.SourceSyntax ?? argument.SourceSyntax ?? target?.SourceSyntax, "slice T[]");

		Expression? length = CreateLengthExpression(target, range.SourceSyntax ?? argument.SourceSyntax ?? target?.SourceSyntax);
		if (length is null)
		{
			Report(GetRange(range.SourceSyntax ?? argument.SourceSyntax ?? target?.SourceSyntax), "Range indexing requires the array receiver to expose a length (.length, .Length, or getLength()).");
			argument.Value = ErrorExpression(ErrorType, range.SourceSyntax ?? argument.SourceSyntax);
			argument.ResolvedType = ErrorType;
			return ErrorType;
		}

		Expression start = ClampBoundary(CreateBoundaryExpression(range.Start, length, defaultToLength: false, range.SourceSyntax), length, range.SourceSyntax);
		Expression end = ClampBoundary(CreateBoundaryExpression(range.End, length, defaultToLength: true, range.SourceSyntax), length, range.SourceSyntax);
		Expression count = CreateRangeCountExpression(start, end, range.SourceSyntax);

		argument.Value = start;
		argument.ResolvedType = "nuint";
		arguments.Insert(1, new ArgumentExpression
		{
			SourceSyntax = argument.SourceSyntax,
			Value = count,
			ResolvedType = "nuint"
		});
		if (stringElementType is not null)
			return $"const {stringElementType}[]";
		if (TryGetFixedArrayShape(targetType, out string fixedElementType, out _))
			return $"{fixedElementType}[]";
		return targetType;
	}

	string BodyAnalyzeMemberExpression(MemberExpression member, BodyScope scope, AnalysisScope typeScope, string? targetCallableType = null)
	{
		string targetType = TryAnalyzeMemberTypeTarget(member.Target, scope, out string typeTarget)
			? typeTarget
			: BodyAnalyzeExpression(member.Target, scope, typeScope);
		string lookupTargetType = TryGetImplicitIteratorProtocolType(member.Target, targetType, out string iteratorProtocolType)
			? iteratorProtocolType
			: targetType;
		if (TryAnalyzeFixedArrayPointerComponentMember(member, targetType))
			return ErrorType;
		if (TryAnalyzeFixedArrayComponentMember(member, targetType, out string fixedComponentType))
			return fixedComponentType;
		if (TryAnalyzeParamsPointerComponentMember(member, targetType, out string componentType))
			return componentType;
		if (TryAnalyzeParamsComponentMember(member, targetType, out string valueComponentType))
			return valueComponentType;

		bool isTypeTarget = IsTypeReferenceExpression(member.Target);
		List<BodySymbol> members = isTypeTarget
			? LookupStaticMemberSymbols(lookupTargetType, member.Name, member.SourceSyntax)
			: LookupMemberSymbols(lookupTargetType, member.Name, member.SourceSyntax);
		if (members.Count == 0)
			members = LookupGenericConstraintMemberSymbols(lookupTargetType, member.Name, scope, member.SourceSyntax);
		if (members.Count == 0)
		{
			if (TryReportFixedArrayPointerReceiverMember(member, lookupTargetType))
				return ErrorType;
			if (GetTypeDefinition(lookupTargetType) is TypeDefinition type && HasPropertyGetterWithIncompatibleReceiver(type, lookupTargetType, member.Name, member.SourceSyntax))
			{
				Report(GetRange(member.SourceSyntax), PropertyReceiverIncompatibilityMessage(member.Name, lookupTargetType, "getter"));
				return ErrorType;
			}
			if (GetTypeDefinition(lookupTargetType) is TypeDefinition setterType && LookupPropertySetters(setterType, member.Name, member.SourceSyntax).Count > 0)
			{
				Report(GetRange(member.SourceSyntax), $"Property '{member.Name}' is not readable on type '{lookupTargetType}'.");
				return ErrorType;
			}
			if (GetTypeDefinition(lookupTargetType) is TypeDefinition hiddenType && LookupHiddenMember(hiddenType, member.Name, member.SourceSyntax) is Definition hiddenMember)
			{
				ReportMemberNotExported(hiddenMember, member.SourceSyntax);
				return ErrorType;
			}

			Report(GetRange(member.SourceSyntax), $"Member '{member.Name}' could not be found on type '{lookupTargetType}'.");
			return ErrorType;
		}

		if (members.Count > 1)
			{
				List<FunctionDefinition> overloadMembers = [];
				foreach (BodySymbol candidate in members)
					if (candidate.Node is FunctionDefinition overloadFunction)
						overloadMembers.Add(overloadFunction);
			if (overloadMembers.Count == members.Count && IsOverloadFamily(overloadMembers))
				return ReportOverloadFamilyAsValue(member.SourceSyntax, member.Name);
			return ReportMultipleCandidates(member.SourceSyntax, member.Name);
		}

		BodySymbol selected = members[0];
			string memberType = selected.Type;
			if (!isTypeTarget
				&& selected.Node is FunctionDefinition function
				&& TryGetCallableShape(targetCallableType, out CallableShape targetShape)
				&& targetShape.This.HasThis)
			{
				if (BoundMethodReferenceCanSatisfyThisContract(member.Target, lookupTargetType, function, targetShape.This, member.SourceSyntax))
					memberType = targetCallableType!;
			}
			if (!isTypeTarget
				&& selected.Node is FunctionDefinition delegateFunction
				&& IsDelegateCallableType(memberType)
				&& !CanUseReceiverAsDelegateContext(lookupTargetType, delegateFunction))
			{
				Report(GetRange(member.SourceSyntax), $"Type '{lookupTargetType}' cannot be the receiver of a delegate; delegate receivers must be single pointer values.");
				memberType = ErrorType;
			}

		expressionConstants[member] = selected.IsConstant;
		if (isTypeTarget && selected.Node is FieldDefinition staticField)
		{
			expressionRewrites[member] = new VariableReferenceExpression
			{
				SourceSyntax = member.SourceSyntax,
				Variable = staticField,
				ResolvedType = memberType
			};
			return memberType;
		}

		MemberReferenceExpression reference = CreateMemberReference(member, member.Target, memberType, selected.Node);
		if (selected.Node is FieldDefinition field)
			reference.Name = field.Name;
			expressionRewrites[member] = reference;
			return memberType;
		}

		bool IsDelegateCallableType(string? type)
		{
			return TryGetCallableShape(type, out CallableShape shape) && shape.Kind == "delegate";
		}

		bool CanUseReceiverAsDelegateContext(string targetType, FunctionDefinition function)
		{
			if (TryGetParamsComponentShape(null, targetType, "value", out _))
				return false;
			if (TryGetPointerElementType(targetType) is not null || IsPrimitiveStringType(targetType))
				return true;
			if (GetExplicitThisParameter(function)?.Modifier == ParameterModifier.In)
				return true;
			return typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? definition)
				&& definition is ClassDefinition or StructDefinition or NewtypeDefinition;
		}

		bool TryReportFixedArrayPointerReceiverMember(MemberExpression member, string targetType)
		{
			if (!TryGetFixedArrayShape(StripTopLevelConstForReceiver(targetType), out _, out _))
				return false;

			foreach (Definition definition in currentModule?.Definitions ?? [])
			{
				if (definition is not FunctionDefinition function
					|| (function.Name != member.Name && GetCallableName(function) != member.Name)
					|| !IsDefinitionVisible(function, member.SourceSyntax)
					|| GetExplicitThisParameter(function) is null && !HasExpandedThisParameters(function.Parameters))
					continue;

				if (IsFixedArrayPointerReceiverMismatch(targetType, function, IsPropertyGetterFunction(function)))
				{
					Report(GetRange(member.SourceSyntax), $"Member '{member.Name}' exists, but fixed-size array storage does not implicitly become a pointer receiver; use an explicit pointer or span expression.");
					return true;
				}
			}

			return false;
		}

		bool TryAnalyzeFixedArrayPointerComponentMember(MemberExpression member, string targetType)
		{
			if (TryGetPointerElementType(targetType) is not string pointedType
				|| !TryGetFixedArrayShape(pointedType, out _, out _))
				return false;

			if (member.Name is not ("length" or "elements"))
				return false;

			Report(GetRange(member.SourceSyntax), $"Fixed-size array pointer member '{member.Name}' requires explicit dereference; use (*value).{member.Name}.");
			return true;
		}

		bool TryAnalyzeFixedArrayComponentMember(MemberExpression member, string targetType, out string componentType)
		{
			componentType = ErrorType;
			if (!TryGetFixedArrayShape(targetType, out string elementType, out long length))
				return false;

			if (member.Name == "length")
			{
				componentType = "nuint";
				expressionConstants[member] = true;
				expressionRewrites[member] = NumberLiteral(length.ToString(System.Globalization.CultureInfo.InvariantCulture), "nuint");
				return true;
			}

			if (member.Name == "elements")
			{
				componentType = elementType + "*";
				expressionRewrites[member] = new CastExpression
				{
					SourceSyntax = member.SourceSyntax,
					Type = TypeReferenceForResolvedName(componentType),
					Expression = member.Target,
					ResolvedType = componentType
				};
				return true;
			}

			return false;
		}

		bool TryAnalyzeParamsPointerComponentMember(MemberExpression member, string targetType, out string componentType)
	{
		componentType = ErrorType;
		if (TryGetPointerElementType(targetType) is not string pointedType
			|| !TryGetParamsComponentShape(null, pointedType, "value", out ParamsComponentShape shape))
			return false;

		ParamsComponent? component = FindParamsComponent(shape, member.Name);
		if (component is null)
			return false;

		componentType = component.Type;
		UnaryExpression dereferencedTarget = new()
		{
			SourceSyntax = member.Target?.SourceSyntax ?? member.SourceSyntax,
			Operator = UnaryOperator.PointerDereference,
			Operand = member.Target,
			ResolvedType = pointedType
		};
		expressionRewrites[member] = new MemberExpression
		{
			SourceSyntax = member.SourceSyntax,
			Target = dereferencedTarget,
			Name = member.Name,
			ResolvedType = componentType
		};
		return true;
	}

	bool TryAnalyzeParamsComponentMember(MemberExpression member, string targetType, out string componentType)
	{
		componentType = ErrorType;
		if (TryGetPointerElementType(targetType) is string pointedType
			&& TryGetParamsComponentShape(null, pointedType, "value", out _))
			return false;

		if (!TryGetParamsComponentShape(null, targetType, "value", out ParamsComponentShape shape))
			return false;

		ParamsComponent? component = FindParamsComponent(shape, member.Name);
		if (component is null)
			return false;

		componentType = component.Type;
		return true;
	}

	bool BoundMethodReferenceCanSatisfyThisContract(Expression? receiver, string receiverType, FunctionDefinition function, ThisContract contract, SyntaxNode? syntax)
	{
		ThisContract methodContract = GetThisContract(GetEffectiveThisParameter(function));
		if (contract.IsConst && !methodContract.IsConst)
		{
			Report(GetRange(syntax), $"Method reference '{GetCallableName(function)}' cannot satisfy callable target because the target requires const this.");
			return false;
		}
		if (contract.IsVolatile && !methodContract.IsVolatile)
		{
			Report(GetRange(syntax), $"Method reference '{GetCallableName(function)}' cannot satisfy callable target because the target requires volatile this.");
			return false;
		}
		if (contract.IsEscaped && !ReceiverExpressionSatisfiesEscapedThis(receiver, receiverType))
		{
			Report(GetRange(syntax), $"Method reference '{GetCallableName(function)}' cannot satisfy callable target because the receiver does not satisfy escaped this.");
			return false;
		}
		return true;
	}

	bool ReceiverExpressionSatisfiesEscapedThis(Expression? receiver, string receiverType)
	{
		if (TryParseLifetimeFact(GetExpressionLifetimeFact(receiver), out LifetimeFact fact))
		{
			if (fact.Kind == "escaped")
				return true;
		}
		if (receiverType.TrimStart().StartsWith("escaped ", StringComparison.Ordinal))
			return true;

		string receiverDefinitionName = BaseTypeName(StripLifetimeQualifiers(TryGetPointerElementType(receiverType) ?? receiverType));
		return typeDefinitions.TryGetValue(receiverDefinitionName, out TypeDefinition? definition)
			&& definition is ClassDefinition { IsEscaped: true } or ClassDefinition { Extern: not null } or InterfaceDefinition { IsEscaped: true };
	}

	bool IsTypeReferenceExpression(Expression? expression)
	{
		return expression is not null
			&& expressionRewrites.TryGetValue(expression, out Expression? rewrittenTarget)
			&& rewrittenTarget is TypeReferenceExpression;
	}

	string BodyAnalyzeUnaryExpression(UnaryExpression unary, BodyScope scope, AnalysisScope typeScope, string? targetType)
	{
		if (unary.Operator == UnaryOperator.Postpone)
			return BodyAnalyzePostponeExpression(unary, scope, typeScope, targetType);

		string? operandTargetType = unary.Operator == UnaryOperator.Throw
			? GetFunctionThrownParameter(scope.CurrentFunction)?.ResolvedType ?? GetFunctionThrownReturnType(scope.CurrentFunction)
			: targetType;
		string operandType = BodyAnalyzeExpression(unary.Operand, scope, typeScope, operandTargetType);
		if (unary.Context is not null)
			BodyAnalyzeExpression(unary.Context, scope, typeScope);

		expressionConstants[unary] = IsConstant(unary.Operand);
		switch (unary.Operator)
		{
			case UnaryOperator.LogicalNot:
				RequireExpressionType("bool", operandType, unary.Operand?.SourceSyntax, "Logical not operand");
				return "bool";

			case UnaryOperator.Await:
				if (scope.CurrentFunction?.IsAsync != true)
					Report(GetRange(unary.SourceSyntax), "await may be used only inside an async function or async lambda.");
				if (!IsAwaitable(unary.Operand, scope, typeScope))
				{
					Report(GetRange(unary.SourceSyntax), GetAwaitableDiagnostic(unary.Operand, scope, typeScope));
					return ErrorType;
				}
				return GetAwaitedType(unary.Operand, scope, typeScope);

			case UnaryOperator.Postpone:
				return unary.ResolvedType ?? ErrorType;

			case UnaryOperator.AddressOf:
				if (unary.Operand is IndexExpression index && TryGetIndexedAddressType(index.Target?.ResolvedType, out string indexedAddressType))
				{
					if (index.Target?.ResolvedType is string indexedType && TryGetArrayElementType(indexedType) is not null)
						RequireGenericArrayElementStride(indexedType, scope, unary.SourceSyntax ?? index.SourceSyntax, "take the address of an element of T[]");
					return indexedAddressType;
				}
				if (IsStorageAccessThroughConstReceiver(unary.Operand))
					return $"{AddTopLevelConstToType(operandType)}*";
				return $"{operandType}*";

			case UnaryOperator.PointerDereference:
				return TryGetPointerElementType(operandType) ?? ErrorType;

			case UnaryOperator.Plus:
			case UnaryOperator.Minus:
			case UnaryOperator.BitwiseNot:
				if (!IsNumericType(operandType))
					Report(GetRange(unary.Operand?.SourceSyntax), $"Unary operator requires a numeric operand, not '{operandType}'.");
				return PromoteInteger(operandType);

			default:
				return operandType;
		}
	}

	string BodyAnalyzePostponeExpression(UnaryExpression postpone, BodyScope scope, AnalysisScope typeScope, string? targetType)
	{
		if (postpone.Operand is not CallExpression call)
		{
			Report(GetRange(postpone.SourceSyntax), "postpone must be followed directly by a method-call expression.");
			postpone.ResolvedType = ErrorType;
			return ErrorType;
		}

		NormalizeGenericCallSyntax(call);
		foreach (TypeReference argument in call.TypeArguments)
			AnalyzeType(argument, typeScope);

		FunctionDefinition? function = callTargets.TryGetValue(call, out FunctionDefinition? existingTarget)
			? existingTarget
			: ResolveCallTarget(call.Target, scope, typeScope, call.Arguments);
		if (function is null)
		{
			Report(GetRange(call.SourceSyntax ?? postpone.SourceSyntax), "Postponed call target could not be resolved.");
			postpone.ResolvedType = ErrorType;
			return ErrorType;
		}

		EnsureFunctionSignatureAnalyzed(function, typeScope);
		callTargets[call] = function;
		bool includeThis = IncludeExplicitThisArgument(call.Target, function);
		List<ParameterDefinition> parameters = GetAsyncAwareCallParameters(function, call, includeThis);
		List<ParameterDefinition> callableParameters = GetCallableParameters(parameters, includeThis);
		if (includeThis && GetExplicitThisParameter(function) is null && IsInstanceFunction(function) && FindContainingType(function) is TypeDefinition containingType)
			callableParameters.Insert(0, CreateImplicitThisParameter(containingType));

		if (call.Arguments.Any(static argument => !string.IsNullOrWhiteSpace(argument.Name)))
		{
			Report(GetRange(call.SourceSyntax ?? postpone.SourceSyntax), "Named postponed call slots are not implemented yet; use positional arguments for now.");
			postpone.ResolvedType = ErrorType;
			return ErrorType;
		}
		if (call.Arguments.Count > callableParameters.Count)
		{
			Report(GetRange(call.SourceSyntax ?? postpone.SourceSyntax), "Postponed call has too many supplied arguments.");
			postpone.ResolvedType = ErrorType;
			return ErrorType;
		}

		List<LambdaParameter> lambdaParameters = [];
		List<string> lambdaParameterSlots = [];
		for (int i = call.Arguments.Count; i < callableParameters.Count; i++)
		{
			ParameterDefinition parameter = callableParameters[i];
			if (parameter.ResolvedType is null)
				AnalyzeParameterDefinition(parameter, typeScope);
			ParameterDefinition lambdaParameterDefinition = new()
			{
				SourceSyntax = parameter.SourceSyntax ?? call.SourceSyntax ?? postpone.SourceSyntax,
				Name = string.IsNullOrWhiteSpace(parameter.Name) ? "arg" + (i - call.Arguments.Count).ToString(CultureInfo.InvariantCulture) : parameter.Name,
				Symbol = string.IsNullOrWhiteSpace(parameter.Symbol) ? parameter.Name : parameter.Symbol,
				Modifier = parameter.Modifier,
				Type = CloneType(parameter.Type) ?? TypeReferenceForResolvedName(parameter.ResolvedType ?? ErrorType),
				ResolvedType = parameter.ResolvedType ?? ErrorType
			};
			LambdaParameter lambdaParameter = new()
			{
				SourceSyntax = parameter.SourceSyntax ?? call.SourceSyntax ?? postpone.SourceSyntax,
				Parameter = lambdaParameterDefinition,
				ResolvedType = lambdaParameterDefinition.ResolvedType
			};
			lambdaParameters.Add(lambdaParameter);
			lambdaParameterSlots.Add(GetLambdaParameterCallableSlot(lambdaParameterDefinition));
			call.Arguments.Add(new ArgumentExpression
			{
				SourceSyntax = lambdaParameter.SourceSyntax,
				Modifier = parameter.Modifier switch
				{
					ParameterModifier.Out => ArgumentModifier.Out,
					ParameterModifier.Thrown => ArgumentModifier.Catch,
					_ => ArgumentModifier.None
				},
				Value = CreateVariableReference(lambdaParameter, lambdaParameterDefinition.ResolvedType ?? ErrorType),
				ResolvedType = lambdaParameterDefinition.ResolvedType ?? ErrorType
			});
		}

		string returnType = function.ResolvedType ?? "void";
		string onceType = BuildCallableType("once", returnType, lambdaParameterSlots);
		LambdaExpression lambda = new()
		{
			SourceSyntax = postpone.SourceSyntax,
			Body = new BlockStatement { SourceSyntax = postpone.SourceSyntax },
			ResolvedType = onceType
		};
		foreach (LambdaParameter parameter in lambdaParameters)
			lambda.Parameters.Add(parameter);
		if (returnType == "void")
			lambda.Body.Statements.Add(new ExpressionStatement { SourceSyntax = call.SourceSyntax, Expression = call });
		else
			lambda.Body.Statements.Add(new ReturnStatement { SourceSyntax = call.SourceSyntax, Expression = call });

		string analyzedType = BodyAnalyzeLambdaExpression(lambda, scope, typeScope, onceType);
		expressionRewrites[postpone] = lambda;
		postpone.ResolvedType = analyzedType;
		return analyzedType;
	}

	string BodyAnalyzePostfixUpdateExpression(PostfixUpdateExpression postfix, BodyScope scope, AnalysisScope typeScope)
	{
		string operandType = BodyAnalyzeExpression(postfix.Expression, scope, typeScope);
		RequireMutableWriteTarget(postfix.Expression, operandType, postfix.Expression?.SourceSyntax, "Update target", scope);
		if (!IsNumericType(operandType))
			Report(GetRange(postfix.Expression?.SourceSyntax), $"Update operator requires a numeric operand, not '{operandType}'.");
		return operandType;
	}

	string BodyAnalyzeBinaryExpression(BinaryExpression binary, BodyScope scope, AnalysisScope typeScope)
	{
		string left;
		string right;
		if (IsComparisonOperator(binary.Operator) && binary.Left is NamedExpression && binary.Right is not null)
		{
			right = BodyAnalyzeExpression(binary.Right, scope, typeScope);
			left = BodyAnalyzeExpression(binary.Left, scope, typeScope, IsEnumTargetType(right) ? right : null);
		}
		else
		{
			left = BodyAnalyzeExpression(binary.Left, scope, typeScope);
			right = BodyAnalyzeExpression(binary.Right, scope, typeScope, IsEnumTargetType(left) ? left : null);
		}
		expressionConstants[binary] = IsConstant(binary.Left) && IsConstant(binary.Right);

		return binary.Operator switch
		{
			BinaryOperator.LogicalOr or BinaryOperator.LogicalAnd => AnalyzeBooleanBinary(binary, left, right),
			BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual => AnalyzeComparisonBinary(binary, left, right),
			BinaryOperator.BitwiseOr or BinaryOperator.BitwiseXor or BinaryOperator.BitwiseAnd or BinaryOperator.LeftShift or BinaryOperator.RightShift => AnalyzeIntegralBinary(binary, left, right),
			BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo => AnalyzeArithmeticBinary(binary, left, right),
			BinaryOperator.NullCoalescing => left,
			_ => ErrorType
		};
	}

	static bool IsComparisonOperator(BinaryOperator op)
	{
		return op is BinaryOperator.Equal
			or BinaryOperator.NotEqual
			or BinaryOperator.LessThan
			or BinaryOperator.LessThanOrEqual
			or BinaryOperator.GreaterThan
			or BinaryOperator.GreaterThanOrEqual;
	}

	bool IsEnumTargetType(string? type)
	{
		return GetEnumTargetTypeName(type) is not null;
	}

	string BodyAnalyzeAssignmentExpression(AssignmentExpression assignment, BodyScope scope, AnalysisScope typeScope)
	{
		if (TryAnalyzePropertyAssignment(assignment, scope, typeScope, out string propertyType))
			return propertyType;

		bool discardTarget = IsDiscardExpression(assignment.Target);
		string targetType = discardTarget ? TargetType : BodyAnalyzeExpression(assignment.Target, scope, typeScope);
		string valueType = BodyAnalyzeExpression(assignment.Value, scope, typeScope, discardTarget ? null : targetType);
		if (discardTarget)
		{
			assignment.Target!.ResolvedType = valueType;
			assignment.ResolvedType = valueType;
			return valueType;
		}
		RequireMutableWriteTarget(assignment.Target, targetType, assignment.Target?.SourceSyntax, "Assignment target", scope);
		if (IsDirectCapturingLambda(assignment.Value, scope) && IsEscapingLambdaAssignmentTarget(assignment.Target) && !IsEscapedDelegateLambdaTarget(targetType))
			Report(GetRange(assignment.Value?.SourceSyntax ?? assignment.SourceSyntax), "Capturing scoped lambdas cannot be assigned to global variables or fields.");
		if (TryGetFixedArrayShape(targetType, out _, out _))
		{
			if (!IsValidFixedStorageAssignmentValue(targetType, assignment.Value))
				Report(GetRange(assignment.Value?.SourceSyntax ?? assignment.SourceSyntax), "Fixed-size arrays cannot be copied by value; assign from an array literal, string literal, or default.");
		}
		else if (RequiresAnyGenericCopy(targetType, assignment.Value, scope))
		{
			ReportAnyGenericCopy(assignment.Value?.SourceSyntax ?? assignment.SourceSyntax);
		}
		else if (RequiresAnyGenericDefaultFillSizeOf(targetType, assignment.Value, scope))
		{
			ReportAnyGenericDefaultFillNeedsSizeOf(assignment.Value?.SourceSyntax ?? assignment.SourceSyntax);
		}
		else
		{
			CheckAssignable(targetType, valueType, assignment.Value?.SourceSyntax, "Assignment");
			if (TryGetAssignmentTargetConstOfType(assignment.Target, out TypeReference? constOfType))
				CheckConstOfProducedResult(constOfType, assignment.Value, assignment.Value?.SourceSyntax ?? assignment.SourceSyntax, "Assignment");
			CheckLifetimeAssignment(assignment.Target, assignment.Value, assignment.Value?.SourceSyntax ?? assignment.SourceSyntax, scope, "Assignment");
		}
		UpdateAssignmentLifetimeFact(assignment.Target, GetExpressionLifetimeFact(assignment.Value));
		return targetType;
	}

	bool TryGetAssignmentTargetConstOfType(Expression? target, out TypeReference? type)
	{
		if (target is not null && expressionRewrites.TryGetValue(target, out Expression? rewrite))
			target = rewrite;
		type = target switch
		{
			VariableReferenceExpression { Variable: ParameterDefinition parameter } => parameter.Type,
			VariableReferenceExpression { Variable: VariableDefinition variable } => variable.Type,
			VariableReferenceExpression { Variable: DeclarationTarget declaration } => declaration.Type,
			MemberReferenceExpression { Member: FieldDefinition field } => field.Type,
			MemberReferenceExpression { Member: ParameterDefinition parameter } => parameter.Type,
			MemberReferenceExpression { Member: VariableDefinition variable } => variable.Type,
			_ => null
		};
		return ContainsConstOfTypeReference(type);
	}

	bool IsEscapingLambdaAssignmentTarget(Expression? target)
	{
		if (target is null)
			return false;
		Expression resolved = expressionRewrites.TryGetValue(target, out Expression? rewrite) ? rewrite : target;
		return resolved switch
		{
			VariableReferenceExpression { Variable: VariableDefinition } => true,
			MemberReferenceExpression { Member: FieldDefinition } => true,
			_ => false
		};
	}

	static bool IsDiscardExpression(Expression? expression)
	{
		return expression is NamedExpression { Qualifiers.Count: 0, Name: "_" };
	}

	bool TryAnalyzeMemberTypeTarget(Expression? target, BodyScope scope, out string type)
	{
		type = ErrorType;
		if (target is not NamedExpression { Qualifiers.Count: 0 } named)
			return false;
		if (scope.TryLookup(named.Name, out _) || LookupGlobalStorageSymbol(named.Name, named.SourceSyntax) is not null)
			return false;

		string typeName = named.Name;
		if (TryResolveAlias(named.Name, AliasTargetKind.Type, named.SourceSyntax, out AliasDefinition? alias))
			typeName = alias!.ResolvedTargetName;
		if (!typeDefinitions.TryGetValue(typeName, out TypeDefinition? typeDefinition))
			return false;
		if (!IsDefinitionVisible(typeDefinition, named.SourceSyntax))
			return false;

		TypeDefinitionReference typeReference = new()
		{
			SourceSyntax = named.SourceSyntax,
			Name = typeDefinition.Name,
			Definition = typeDefinition,
			ResolvedType = typeDefinition.ResolvedType ?? typeDefinition.Name
		};
		TypeReferenceExpression expression = new()
		{
			SourceSyntax = named.SourceSyntax,
			Type = typeReference,
			ResolvedType = typeReference.ResolvedType
		};
		expressionRewrites[named] = expression;
		type = expression.ResolvedType ?? ErrorType;
		return true;
	}

	bool IsParamsComponentMemberReference(Expression? expression)
	{
		if (expression is not MemberExpression member)
			return false;
		if (!expressionRewrites.TryGetValue(member, out Expression? rewritten))
			return false;
		if (rewritten is not MemberReferenceExpression { Member: ParameterDefinition })
			return false;

		string targetType = member.Target?.ResolvedType ?? "";
		return GetTypeDefinition(targetType) is ParamsDefinition;
	}

	bool TryAnalyzePropertyAssignment(AssignmentExpression assignment, BodyScope scope, AnalysisScope typeScope, out string propertyType)
	{
		propertyType = ErrorType;

		switch (assignment.Target)
		{
			case MemberExpression member:
				return TryAnalyzePropertySetter(member, [], assignment.Value, scope, typeScope, out propertyType);

			case IndexExpression { Target: MemberExpression member } index:
				return TryAnalyzePropertySetter(member, index.Arguments, assignment.Value, scope, typeScope, out propertyType);

			default:
				return false;
		}
	}

	string BodyAnalyzeConditionalExpression(ConditionalExpression conditional, BodyScope scope, AnalysisScope typeScope, string? targetType)
	{
		string conditionType = BodyAnalyzeExpression(conditional.Condition, scope, typeScope);
		RequireExpressionType("bool", conditionType, conditional.Condition?.SourceSyntax, "Conditional expression condition");
		string trueType = BodyAnalyzeExpression(conditional.WhenTrue, scope, typeScope, targetType);
		string falseType = BodyAnalyzeExpression(conditional.WhenFalse, scope, typeScope, targetType);
		expressionConstants[conditional] = IsConstant(conditional.Condition) && IsConstant(conditional.WhenTrue) && IsConstant(conditional.WhenFalse);
		if (targetType is not null && targetType != TargetType)
		{
			CheckAssignable(targetType, trueType, conditional.WhenTrue?.SourceSyntax, "Conditional expression");
			CheckAssignable(targetType, falseType, conditional.WhenFalse?.SourceSyntax, "Conditional expression");
			return targetType;
		}
		return BestType([trueType, falseType]);
	}

	string BodyAnalyzeRangeExpression(RangeExpression range, BodyScope scope, AnalysisScope typeScope)
	{
		BodyAnalyzeExpression(range.Start, scope, typeScope, "nuint");
		BodyAnalyzeExpression(range.End, scope, typeScope, "nuint");
		expressionConstants[range] = false;
		return RangeType;
	}

	string AnalyzeBooleanBinary(BinaryExpression binary, string left, string right)
	{
		RequireExpressionType("bool", left, binary.Left?.SourceSyntax, "Logical operator left operand");
		RequireExpressionType("bool", right, binary.Right?.SourceSyntax, "Logical operator right operand");
		return "bool";
	}

	string AnalyzeComparisonBinary(BinaryExpression binary, string left, string right)
	{
		if (!CanImplicitlyConvert(left, right) && !CanImplicitlyConvert(right, left))
			Report(GetRange(binary.SourceSyntax), $"Cannot compare '{left}' and '{right}'.");
		return "bool";
	}

	string AnalyzeIntegralBinary(BinaryExpression binary, string left, string right)
	{
		if (!IsIntegralType(left) || !IsIntegralType(right))
			Report(GetRange(binary.SourceSyntax), $"Bitwise operators require integral operands, not '{left}' and '{right}'.");
		return UsualArithmeticConversion(left, right);
	}

	string AnalyzeArithmeticBinary(BinaryExpression binary, string left, string right)
	{
		if (left == ErrorType || right == ErrorType)
			return ErrorType;

		if (!IsNumericType(left) || !IsNumericType(right))
			Report(GetRange(binary.SourceSyntax), $"Arithmetic operators require numeric operands, not '{left}' and '{right}'.");
		return UsualArithmeticConversion(left, right);
	}


	sealed class BodyScope(BodyScope? parent, FunctionDefinition currentFunction, TypeDefinition? containingType)
	{
		public BodyScope? Parent { get; } = parent;
		public FunctionDefinition CurrentFunction { get; } = currentFunction;
		public TypeDefinition? ContainingType { get; } = containingType;
		public string CurrentFunctionReturnType { get; set; } = ErrorType;
		public string? CurrentFunctionSourceReturnType { get; set; }
		public string? CurrentIteratorElementType { get; set; }
		public Dictionary<string, BodySymbol> Symbols { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, BodySymbol> MemberSymbols { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, string> ComponentSymbols { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, BodyComponentSymbol> ComponentSymbolTypes { get; } = new(StringComparer.Ordinal);

		public bool TryLookup(string name, out BodySymbol symbol)
		{
			if (Symbols.TryGetValue(name, out symbol))
				return true;

			if (Parent is not null)
				return Parent.TryLookup(name, out symbol);

			symbol = default;
			return false;
		}

		public bool TryLookupComponent(string name, out string owner)
		{
			if (ComponentSymbols.TryGetValue(name, out owner!))
				return true;

			if (Parent is not null)
				return Parent.TryLookupComponent(name, out owner);

			owner = "";
			return false;
		}

		public bool TryLookupComponentSymbol(string name, out BodyComponentSymbol symbol)
		{
			if (ComponentSymbolTypes.TryGetValue(name, out symbol))
				return true;

			if (Parent is not null)
				return Parent.TryLookupComponentSymbol(name, out symbol);

			symbol = default;
			return false;
		}
	}

	readonly record struct BodySymbol(string Name, string Type, BindableNode Node, bool IsConstant = false)
	{
		public bool IsConstant { get; } = IsConstant || Node is VariableDefinition variable && IsConstantVariable(variable) || Node is FieldDefinition field && IsConstantField(field);
	}

	readonly record struct BodyComponentSymbol(string Name, string ExpandedName, string Type, string Owner)
	{
	}
}
