using System;
using System.Collections.Generic;
using System.Globalization;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly Dictionary<Expression, bool> expressionConstants = [];
	readonly Dictionary<CallExpression, FunctionDefinition> callTargets = [];
	readonly Dictionary<CallExpression, Dictionary<string, string>> callGenericSubstitutions = [];
	readonly Dictionary<FunctionDefinition, Dictionary<string, LabelStatement>> functionLabels = [];

	void AnalyzeMethodBody(FunctionDefinition function, AnalysisScope typeAndMethodScope, TypeDefinition? containingType)
	{
		if (function.Body is null)
			return;

		BodyScope scope = new(null, function, containingType);
		scope.CurrentFunctionReturnType = IsLifecycleFunction(function) ? "void" : function.ResolvedType ?? ErrorType;
		scope.CurrentIteratorElementType = function.IteratorKind == IteratorKind.None ? null : GetIteratorElementType(function.ReturnType);

		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (!string.IsNullOrWhiteSpace(parameter.Name))
				RegisterBodySymbol(scope, parameter.Name, parameter.ResolvedType ?? ErrorType, parameter, parameter.Type, parameter.ResolvedType);
		}

		function.Body.ResolvedType = "void";
		BodyAnalyzeBlock(function.Body.Statements, scope, typeAndMethodScope);
		BindFunctionLabels(function);
		ValidateBaseConstructorInvocation(function, containingType);
		FlowAnalyzeFunctionBody(function, scope);
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
				break;

			case CaseStatement caseStatement:
				BodyAnalyzeExpression(caseStatement.Expression, scope, typeScope);
				if (!IsConstant(caseStatement.Expression))
					Report(GetRange(caseStatement.SourceSyntax), "Switch case expressions must be constant.");
				break;

			case LabelStatement:
				break;

			case GotoStatement:
				break;

			case ReturnStatement returnStatement:
			{
				string returnType = returnStatement.Expression is null ? "void" : BodyAnalyzeExpression(returnStatement.Expression, scope, typeScope, scope.CurrentFunctionReturnType);
				CheckAssignable(scope.CurrentFunctionReturnType, returnType, returnStatement.Expression?.SourceSyntax ?? returnStatement.SourceSyntax, "Return expression");
				break;
			}

			case YieldStatement yieldStatement:
			{
				string expected = scope.CurrentIteratorElementType ?? ErrorType;
				if (scope.CurrentIteratorElementType is null)
					Report(GetRange(yieldStatement.SourceSyntax), "Yield statements may only appear in iterator functions.");
				string yieldedType = BodyAnalyzeExpression(yieldStatement.Expression, scope, typeScope, expected);
				CheckAssignable(expected, yieldedType, yieldStatement.Expression?.SourceSyntax ?? yieldStatement.SourceSyntax, "Yield expression");
				break;
			}

			case DeleteStatement deleteStatement:
				if (IsBaseDeleteExpression(deleteStatement.Expression))
					AnalyzeBaseDeleteExpression(deleteStatement.Expression, scope);
				else
					BodyAnalyzeExpression(deleteStatement.Expression, scope, typeScope);
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
		TryRewriteOmittedOutDeconstruction(declaration, scope, typeScope);
		string targetType = declaration.Target.Type is AutoTypeReference or null
			? TargetType
			: declaration.Target.Type.ResolvedType ?? ErrorType;
		string initialType = declaration.InitialValue is null ? TargetType : BodyAnalyzeExpression(declaration.InitialValue, scope, typeScope, targetType);
		if (declaration.Target.Names.Count > 1 && TryAnalyzeDeconstructionTarget(declaration.Target, initialType, scope))
			return;

		BodyAnalyzeDeclarationTarget(declaration.Target, scope, typeScope, initialType);

		if (declaration.InitialValue is not null)
			CheckAssignable(declaration.Target.ResolvedType ?? ErrorType, initialType, declaration.InitialValue.SourceSyntax, "Declaration initializer");
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
			VariableReferenceExpression variable => variable.Variable?.ResolvedType ?? ErrorType,
			MethodReferenceExpression method => BodyAnalyzeMethodReference(method),
			TypeReferenceExpression typeReference => typeReference.Type?.ResolvedType ?? ErrorType,
			ThisExpression thisExpression => BodyAnalyzeThisExpression(thisExpression, scope),
			DefaultExpression defaultExpression => BodyAnalyzeDefaultExpression(defaultExpression, typeScope, targetType),
			GroupedExpression grouped => BodyAnalyzeGroupedExpression(grouped, scope, typeScope),
			ArrayExpression array => BodyAnalyzeArrayExpression(array, scope, typeScope, targetType),
			InitializerExpression initializer => BodyAnalyzeInitializerExpression(initializer, scope, typeScope, targetType),
			ParenthesizedExpression parenthesized => BodyAnalyzeExpression(parenthesized.Expression, scope, typeScope, targetType),
			CastExpression cast => BodyAnalyzeCastExpression(cast, scope, typeScope),
			ConstructionExpression construction => BodyAnalyzeConstructionExpression(construction, scope, typeScope),
			WithinExpression within => BodyAnalyzeWithinExpression(within, scope, typeScope, targetType),
			SizeOfExpression sizeOf => BodyAnalyzeSizeOfExpression(sizeOf, typeScope),
			VTableOfExpression vtableOf => BodyAnalyzeVTableOfExpression(vtableOf, typeScope),
			LambdaExpression lambda => BodyAnalyzeLambdaExpression(lambda, scope, typeScope, targetType),
			ArgumentExpression argument => BodyAnalyzeArgumentExpression(argument, scope, typeScope, targetType),
			CallExpression call => BodyAnalyzeCallExpression(call, scope, typeScope, targetType),
			IndexExpression index => BodyAnalyzeIndexExpression(index, scope, typeScope),
			MemberExpression member => BodyAnalyzeMemberExpression(member, scope, typeScope),
			MemberReferenceExpression member => member.ResolvedType ?? ErrorType,
			NamelessIndexerExpression indexer => BodyAnalyzeIndexExpression(indexer.Target, indexer.Arguments, scope, typeScope),
			UnaryExpression unary => BodyAnalyzeUnaryExpression(unary, scope, typeScope, targetType),
			PostfixUpdateExpression postfix => BodyAnalyzePostfixUpdateExpression(postfix, scope, typeScope),
			FinallyDeleteExpression finallyDelete => BodyAnalyzeExpression(finallyDelete.Expression, scope, typeScope),
			BinaryExpression binary => BodyAnalyzeBinaryExpression(binary, scope, typeScope),
			AssignmentExpression assignment => BodyAnalyzeAssignmentExpression(assignment, scope, typeScope),
			ConditionalExpression conditional => BodyAnalyzeConditionalExpression(conditional, scope, typeScope, targetType),
			RangeExpression range => BodyAnalyzeRangeExpression(range, scope, typeScope),
			_ => ErrorType
		};

		expression.ResolvedType = type;
		return type;
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
				ResolvedType = symbol.Type
			};
			return symbol.Type;
		}

		if (LookupGlobalVariable(named.Name, named.SourceSyntax) is VariableDefinition variable)
		{
			string type = variable.ResolvedType ?? variable.Type?.ResolvedType ?? ErrorType;
			named.ResolvedType = type;
			expressionConstants[named] = IsConstantVariable(variable);
			expressionRewrites[named] = new VariableReferenceExpression
			{
				SourceSyntax = named.SourceSyntax,
				Variable = variable,
				ResolvedType = type
			};
			return type;
		}

		List<FunctionDefinition> functions = LookupFunctions(named.Name, scope);
		if (functions.Count > 0)
		{
			if (functions.Count > 1)
				return ReportMultipleCandidates(named.SourceSyntax, named.Name);

			MethodReferenceExpression method = new()
			{
				SourceSyntax = named.SourceSyntax,
				ResolvedType = BuildFunctionValueType(functions[0], IsInstanceFunction(functions[0]))
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
		string? elementTarget = TryGetArrayElementType(targetType);
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

		return pointerTarget ? $"{elementType}*" : $"{elementType}[]";
	}

	string BodyAnalyzeInitializerExpression(InitializerExpression initializer, BodyScope scope, AnalysisScope typeScope, string? targetType = null)
	{
		foreach (InitializerItem item in initializer.Items)
		{
			if (item.Target is not null)
				BodyAnalyzeInitializerTarget(item.Target, scope, typeScope);
			item.ResolvedType = BodyAnalyzeExpression(item.Expression, scope, typeScope);
		}

		return targetType ?? TargetType;
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
		string sourceType = BodyAnalyzeExpression(cast.Expression, scope, typeScope);
		if (cast.Type is not null)
			AnalyzeType(cast.Type, typeScope);

		string targetType = cast.Type?.ResolvedType ?? ErrorType;
		if (cast.Type is null && cast.Kind != CastKind.Type)
			targetType = sourceType;
		else if (!CanExplicitlyConvert(sourceType, targetType))
			Report(GetRange(cast.SourceSyntax), $"Invalid cast from '{sourceType}' to '{targetType}'.");

		expressionConstants[cast] = IsConstant(cast.Expression);
		return targetType;
	}

	string BodyAnalyzeConstructionExpression(ConstructionExpression construction, BodyScope scope, AnalysisScope typeScope)
	{
		if (construction.Type is not null)
			AnalyzeType(construction.Type, typeScope);

		string targetType = construction.Type?.ResolvedType ?? TargetType;
		FunctionDefinition? constructor = LookupConstructor(targetType, construction.Arguments.Count);
		AnalyzeCallArguments(construction.Arguments, constructor?.Parameters ?? [], scope, typeScope, construction.SourceSyntax);
		BodyAnalyzeExpression(construction.ElementCount, scope, typeScope, "nuint");
		if (construction.Initializer is not null)
			BodyAnalyzeInitializerExpression(construction.Initializer, scope, typeScope, targetType);
		if (construction.ElementCount is not null)
			return $"{targetType}[]";
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
		CallableShape? targetShape = TryGetCallableShape(targetType, out CallableShape callableTarget) ? callableTarget : null;
		BodyScope lambdaScope = new(scope, scope.CurrentFunction, scope.ContainingType)
		{
			CurrentFunctionReturnType = targetShape?.ReturnType ?? TargetType,
			CurrentIteratorElementType = null
		};

		if (targetShape is CallableShape shape && shape.Parameters.Count != lambda.Parameters.Count)
			Report(GetRange(lambda.SourceSyntax), $"Lambda parameter count '{lambda.Parameters.Count.ToString(CultureInfo.InvariantCulture)}' does not match target callable parameter count '{shape.Parameters.Count.ToString(CultureInfo.InvariantCulture)}'.");

		for (int i = 0; i < lambda.Parameters.Count; i++)
		{
			LambdaParameter parameter = lambda.Parameters[i];
			if (parameter.Parameter is not null)
				AnalyzeParameterDefinition(parameter.Parameter, typeScope);

			string parameterType = parameter.Parameter?.ResolvedType
				?? (targetShape is CallableShape target && i < target.Parameters.Count ? target.Parameters[i] : TargetType);
			parameter.ResolvedType = parameterType;

			if (targetShape is CallableShape expected && i < expected.Parameters.Count && parameterType != TargetType && parameterType != expected.Parameters[i])
				Report(GetRange(parameter.SourceSyntax), $"Lambda parameter type '{parameterType}' does not match target parameter type '{expected.Parameters[i]}'.");

			string? parameterName = GetLambdaParameterSymbolName(parameter);
			if (!string.IsNullOrWhiteSpace(parameterName))
				lambdaScope.Symbols[parameterName] = new BodySymbol(parameterName, parameter.ResolvedType, parameter);
		}

		string returnType = "void";
		if (lambda.Body is BlockStatement block)
		{
			BodyAnalyzeBlock(block.Statements, lambdaScope, typeScope);
			BindStatementLabels(block.Statements);
			block.ResolvedType = "void";
			returnType = InferBlockReturnType(block, targetShape?.ReturnType);
		}

		List<string> parameterTypes = [];
		foreach (LambdaParameter parameter in lambda.Parameters)
			parameterTypes.Add(parameter.ResolvedType ?? ErrorType);

		string inferredType = BuildCallableType("fn", targetShape?.ReturnType ?? returnType, parameterTypes);
		if (targetType is not null && TryGetCallableShape(targetType, out CallableShape expectedShape) && CallableShapesCompatible(new CallableShape("fn", null, null, returnType, parameterTypes), expectedShape))
			return targetType;

		return inferredType;
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

		FunctionDefinition? function = ResolveCallTarget(call.Target, scope, typeScope, call.Arguments.Count);
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
		AnalyzeCallArguments(call.Arguments, function?.Parameters ?? [], scope, typeScope, call.SourceSyntax ?? call.Target?.SourceSyntax, IncludeExplicitThisArgument(call.Target, function), genericSubstitutions, genericParameterNames, function, call.Target);
		if (function is not null)
			callGenericSubstitutions[call] = new Dictionary<string, string>(genericSubstitutions, StringComparer.Ordinal);

		string returnType = SubstituteGenericReturnType(function?.ResolvedType, call.TypeArguments, genericSubstitutions);
		if (targetType is not null)
			CheckAssignable(targetType, returnType, call.SourceSyntax, "Call result");
		return returnType;
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

		List<ParameterDefinition> parameters = [];
		foreach (string parameterType in callable.Parameters)
		{
			parameters.Add(new ParameterDefinition
			{
				ResolvedType = parameterType,
				Type = new NamedTypeReference { Name = parameterType, ResolvedType = parameterType }
			});
		}

		AnalyzeCallArguments(call.Arguments, parameters, scope, typeScope, call.SourceSyntax ?? call.Target?.SourceSyntax);
		returnType = callable.ReturnType;
		if (targetType is not null)
			CheckAssignable(targetType, returnType, call.SourceSyntax, "Call result");
		return true;
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

		AddReceiverTypeGenericSubstitutions(receiverType, function, substitutions);
	}

	void AddReceiverTypeGenericSubstitutions(string receiverType, FunctionDefinition function, Dictionary<string, string> substitutions)
	{
		if (FindContainingType(function) is not TypeDefinition containingType || containingType.GenericParameters.Count == 0)
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
		if (new TypeShapeParser(type).TryParse(out TypeShape shape))
		{
			while (shape.Kind is TypeShapeKind.Pointer or TypeShapeKind.Array or TypeShapeKind.Optional)
				shape = shape.Element ?? shape;
			type = shape.Name;
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
		return function is not null
			&& (GetExplicitThisParameter(function) is not null || IsInstanceFunction(function))
			&& target is not MemberExpression and not MemberReferenceExpression;
	}

	FunctionDefinition? ResolveCallTarget(Expression? target, BodyScope scope, AnalysisScope typeScope, int argumentCount = 0)
	{
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
					return ResolveBaseMemberCallTarget(member, scope, typeScope, argumentCount);

				string targetType = BodyAnalyzeExpression(member.Target, scope, typeScope);
				List<FunctionDefinition> functions = LookupMemberFunctions(targetType, member.Name, member.SourceSyntax);
				if (functions.Count == 0)
					functions = LookupGenericConstraintMemberFunctions(targetType, member.Name, scope, member.SourceSyntax);
				if (functions.Count == 1)
				{
					member.ResolvedType = functions[0].ResolvedType ?? ErrorType;
					expressionRewrites[member] = CreateMemberReference(member, member.Target, BuildFunctionValueType(functions[0], isInstance: true), functions[0]);
					return functions[0];
				}
				if (functions.Count > 1)
					Report(GetRange(member.SourceSyntax), $"Multiple candidates found for member call '{member.Name}'.");
				else if (GetTypeDefinition(targetType) is TypeDefinition receiverType && HasMemberFunctionWithIncompatibleReceiver(receiverType, targetType, member.Name, member.SourceSyntax))
					Report(GetRange(member.SourceSyntax), $"Member '{member.Name}' exists on type '{targetType}', but its this parameter is not compatible with that receiver.");
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

	FunctionDefinition? ResolveBaseMemberCallTarget(MemberExpression member, BodyScope scope, AnalysisScope typeScope, int argumentCount)
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

		FunctionDefinition? implementation = FindVirtualImplementationByName(baseClass, member.Name);
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

	void AnalyzeCallArguments(List<ArgumentExpression> arguments, List<ParameterDefinition> parameters, BodyScope scope, AnalysisScope typeScope, SyntaxNode? fallbackSyntax = null, bool includeExplicitThis = false, Dictionary<string, string>? genericSubstitutions = null, HashSet<string>? genericParameterNames = null, FunctionDefinition? function = null, Expression? callTarget = null)
	{
		genericSubstitutions ??= [];
		genericParameterNames ??= [];
		List<ParameterDefinition> callableParameters = GetCallableParameters(parameters, includeExplicitThis);
		if (function is not null && includeExplicitThis && GetExplicitThisParameter(function) is null && IsInstanceFunction(function) && FindContainingType(function) is TypeDefinition containingType)
			callableParameters.Insert(0, CreateImplicitThisParameter(containingType));
		if (arguments.Count > callableParameters.Count || HasExplicitHiddenArgument(arguments))
			AddExplicitHiddenParameters(parameters, callableParameters);
		AnalyzeRangeAwareArguments(arguments, callableParameters, GetRangeReceiver(callTarget), scope, typeScope, fallbackSyntax);
		int parameterIndex = 0;
		for (int i = 0; i < arguments.Count; i++)
		{
			ParameterDefinition? parameter = parameterIndex < callableParameters.Count ? callableParameters[parameterIndex] : null;
			while (parameter is SizeOfParameterDefinition && IsExplicitHiddenArgument(arguments[i]))
			{
				parameterIndex++;
				parameter = parameterIndex < callableParameters.Count ? callableParameters[parameterIndex] : null;
			}
			if (parameter is not null && parameter.ResolvedType is null)
				AnalyzeParameterDefinition(parameter, typeScope);

			string expected = SubstituteGenericType(parameter?.ResolvedType ?? ErrorType, genericSubstitutions);
			string analysisTarget = ContainsUnboundGenericParameter(expected, genericSubstitutions, genericParameterNames) ? TargetType : expected;
			string actual = arguments[i].ResolvedType == ErrorType
				? ErrorType
				: BodyAnalyzeArgumentExpression(arguments[i], scope, typeScope, analysisTarget);
			if (parameter is not null)
			{
				InferGenericSubstitutions(parameter.ResolvedType ?? ErrorType, actual, genericSubstitutions, genericParameterNames);
				expected = SubstituteGenericType(parameter.ResolvedType ?? ErrorType, genericSubstitutions);
			}
			if (parameter is not null)
				{
					if (parameter.Modifier == ParameterModifier.Out && arguments[i].Modifier != ArgumentModifier.Out)
						Report(GetRange(arguments[i].SourceSyntax ?? fallbackSyntax), "Out parameters require an 'out' argument.");
					if (parameter.Modifier != ParameterModifier.Out && arguments[i].Modifier == ArgumentModifier.Out)
						Report(GetRange(arguments[i].SourceSyntax ?? fallbackSyntax), "Only out parameters may use an 'out' argument.");
					if (parameter.Modifier == ParameterModifier.Thrown && arguments[i].Modifier != ArgumentModifier.Catch)
						Report(GetRange(arguments[i].SourceSyntax ?? fallbackSyntax), "Thrown parameters require a 'catch' argument.");
					if (parameter.Modifier != ParameterModifier.Thrown && arguments[i].Modifier == ArgumentModifier.Catch)
						Report(GetRange(arguments[i].SourceSyntax ?? fallbackSyntax), "Only thrown parameters may use a 'catch' argument.");
					if (parameter.Modifier == ParameterModifier.Out)
						CheckAssignable(actual, expected, arguments[i].SourceSyntax ?? fallbackSyntax, "Out argument");
					else
					{
						CheckAssignable(expected, actual, arguments[i].SourceSyntax ?? fallbackSyntax, "Argument");
						if (CanLiftToOptional(actual, expected))
							arguments[i].ResolvedType = expected;
					}
				}
			if (ArrayLiteralConsumesLengthParameter(arguments[i], parameter, callableParameters, parameterIndex))
				parameterIndex++;
			parameterIndex++;
		}

		if (parameters.Count > 0 && parameterIndex < CountRequiredParameters(callableParameters, includeExplicitThis: true))
			Report(GetRange((arguments.Count > 0 ? arguments[^1].SourceSyntax : null) ?? fallbackSyntax), "Call is missing required arguments.");
		if (parameters.Count > 0 && arguments.Count > callableParameters.Count)
			Report(GetRange(arguments[^1].SourceSyntax ?? fallbackSyntax), "Call has too many arguments.");
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

	static bool HasExplicitHiddenArgument(List<ArgumentExpression> arguments)
	{
		foreach (ArgumentExpression argument in arguments)
			if (IsExplicitHiddenArgument(argument))
				return true;
		return false;
	}

	static bool IsExplicitHiddenArgument(ArgumentExpression argument)
	{
		return argument.Modifier == ArgumentModifier.Catch || argument.Value is WithinExpression { Expression: null };
	}

	static void InferGenericSubstitutions(string pattern, string actual, Dictionary<string, string> substitutions, HashSet<string> genericParameterNames)
	{
		if (string.IsNullOrWhiteSpace(pattern) || actual == ErrorType)
			return;

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
			if (parameter.Modifier is ParameterModifier.Thrown or ParameterModifier.Within || parameter is WithinParameterDefinition)
				callableParameters.Add(parameter);
		}
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

		if (TryGetAccessibleParameterlessConstructor(baseClass, out FunctionDefinition? parameterlessConstructor))
		{
			if (parameterlessConstructor is not null)
				InsertImplicitBaseConstructorCall(function.Body, parameterlessConstructor);
			return;
		}

		Report(GetRange(function.Body.SourceSyntax ?? function.SourceSyntax), $"Constructor for class '{containingClass.Name}' must invoke a base constructor because base class '{baseClass.Name}' has no accessible parameterless constructor.");
	}

	void InsertImplicitBaseConstructorCall(BlockStatement body, FunctionDefinition constructor)
	{
		FunctionDefinition? initNew = FindGeneratedInitNewMethod(FindContainingType(constructor));
		CallExpression call = new()
		{
			Target = CreateBaseInitNewReference(constructor, initNew),
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

	static MethodReferenceExpression CreateBaseInitNewReference(FunctionDefinition constructor, FunctionDefinition? initNew)
	{
		MethodReferenceExpression reference = new()
		{
			ResolvedType = "void"
		};
		reference.Candidates.Add(initNew ?? constructor);
		return reference;
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
		MethodReferenceExpression reference = CreateBaseInitNewReference(constructor, initNew);
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
			return BodyAnalyzeArrayRangeIndexExpression(target, targetType, arguments, range);

		foreach (ArgumentExpression argument in arguments)
		{
			if (argument.Value is UnaryExpression { Operator: UnaryOperator.FromEnd } fromEnd)
			{
				SyntaxNode? syntax = fromEnd.SourceSyntax ?? fromEnd.Operand?.SourceSyntax ?? argument.SourceSyntax;
				Expression? length = CreateLengthExpression(target, syntax);
				if (length is null)
					Report(GetRange(syntax), "^ from-end syntax requires the receiver to expose a length (.length, .Length, or getLength()).");
				else
				{
					argument.Value = CreateFromEndExpression(fromEnd, length);
					argument.ResolvedType = "nuint";
				}
			}
			BodyAnalyzeArgumentExpression(argument, scope, typeScope, "nuint");
		}

		if (TryGetArrayElementType(targetType) is string elementType)
			return elementType;

		if (TryGetPointerElementType(targetType) is string pointerElementType)
			return pointerElementType;

		if (GetPrimitiveStringElementType(targetType) is string stringElementType)
			return stringElementType;

		Report(GetRange(target?.SourceSyntax), $"Type '{targetType}' is not indexable.");
		return ErrorType;
	}

	string BodyAnalyzeArrayRangeIndexExpression(Expression? target, string targetType, List<ArgumentExpression> arguments, RangeExpression range)
	{
		ArgumentExpression argument = arguments[0];
		if (TryGetArrayElementType(targetType) is null)
		{
			if (TryGetPointerElementType(targetType) is string pointerElementType && TryGetArrayElementType(pointerElementType) is not null)
				Report(GetRange(range.SourceSyntax ?? argument.SourceSyntax ?? target?.SourceSyntax), "Range indexing is only valid on array values; dereference the array pointer before applying the range.");
			else
				Report(GetRange(range.SourceSyntax ?? argument.SourceSyntax ?? target?.SourceSyntax), "Range indexing is only valid on array values.");
			argument.Value = ErrorExpression(ErrorType, range.SourceSyntax ?? argument.SourceSyntax);
			argument.ResolvedType = ErrorType;
			return ErrorType;
		}

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
		return targetType;
	}

	string BodyAnalyzeMemberExpression(MemberExpression member, BodyScope scope, AnalysisScope typeScope)
	{
		string targetType = BodyAnalyzeExpression(member.Target, scope, typeScope);
		List<BodySymbol> members = LookupMemberSymbols(targetType, member.Name, member.SourceSyntax);
		if (members.Count == 0)
			members = LookupGenericConstraintMemberSymbols(targetType, member.Name, scope, member.SourceSyntax);
		if (members.Count == 0)
		{
			if (GetTypeDefinition(targetType) is TypeDefinition type && HasPropertyGetterWithIncompatibleReceiver(type, targetType, member.Name, member.SourceSyntax))
			{
				Report(GetRange(member.SourceSyntax), $"Property '{member.Name}' exists on type '{targetType}', but its getter's this parameter is not compatible with that receiver.");
				return ErrorType;
			}
			if (GetTypeDefinition(targetType) is TypeDefinition setterType && LookupPropertySetters(setterType, member.Name, member.SourceSyntax).Count > 0)
			{
				Report(GetRange(member.SourceSyntax), $"Property '{member.Name}' is not readable on type '{targetType}'.");
				return ErrorType;
			}
			if (GetTypeDefinition(targetType) is TypeDefinition hiddenType && LookupHiddenMember(hiddenType, member.Name, member.SourceSyntax) is Definition hiddenMember)
			{
				ReportMemberNotExported(hiddenMember, member.SourceSyntax);
				return ErrorType;
			}

			Report(GetRange(member.SourceSyntax), $"Member '{member.Name}' could not be found on type '{targetType}'.");
			return ErrorType;
		}

		if (members.Count > 1)
			return ReportMultipleCandidates(member.SourceSyntax, member.Name);

		BodySymbol selected = members[0];
		string memberType = IsConstReceiverType(targetType) && selected.Node is FieldDefinition or ParameterDefinition
			? AddTopLevelConstToType(selected.Type)
			: selected.Type;

		expressionConstants[member] = selected.IsConstant;
		MemberReferenceExpression reference = CreateMemberReference(member, member.Target, memberType, selected.Node);
		if (selected.Node is FieldDefinition field)
			reference.Name = field.Name;
		expressionRewrites[member] = reference;
		return memberType;
	}

	string BodyAnalyzeUnaryExpression(UnaryExpression unary, BodyScope scope, AnalysisScope typeScope, string? targetType)
	{
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
				if (!IsAwaitable(unary.Operand, scope, typeScope))
					Report(GetRange(unary.SourceSyntax), "Await target is not awaitable.");
				return GetAwaitedType(unary.Operand, scope, typeScope);

			case UnaryOperator.AddressOf:
				if (unary.Operand is IndexExpression index && TryGetIndexedAddressType(index.Target?.ResolvedType, out string indexedAddressType))
					return indexedAddressType;
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

	string BodyAnalyzePostfixUpdateExpression(PostfixUpdateExpression postfix, BodyScope scope, AnalysisScope typeScope)
	{
		string operandType = BodyAnalyzeExpression(postfix.Expression, scope, typeScope);
		RequireMutableWriteTarget(postfix.Expression, operandType, postfix.Expression?.SourceSyntax, "Update target");
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
		RequireMutableWriteTarget(assignment.Target, targetType, assignment.Target?.SourceSyntax, "Assignment target");
		CheckAssignable(targetType, valueType, assignment.Value?.SourceSyntax, "Assignment");
		return targetType;
	}

	static bool IsDiscardExpression(Expression? expression)
	{
		return expression is NamedExpression { Qualifiers.Count: 0, Name: "_" };
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
		return targetType ?? BestType([trueType, falseType]);
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
		public bool IsConstant { get; } = IsConstant || Node is VariableDefinition variable && IsConstantVariable(variable) || Node is FieldDefinition { Modifier: FieldModifier.Static };
	}

	readonly record struct BodyComponentSymbol(string Name, string ExpandedName, string Type, string Owner)
	{
	}
}
