using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	sealed record LambdaCapture(BindableNode Variable, string Name, string Type, FieldDefinition Field, SyntaxNode? SourceSyntax, bool IsThis = false);
	sealed record LambdaContextInfo(StructDefinition Definition, List<LambdaCapture> Captures, bool IsEscaped)
	{
		public DeclarationTarget? LocalTarget { get; set; }
	}

	readonly Dictionary<LambdaExpression, LambdaContextInfo> lambdaContexts = [];

	bool TryGetLambdaCallableShape(string? type, out CallableShape shape, out bool isEscaped)
	{
		isEscaped = false;
		if (type is null)
		{
			shape = default;
			return false;
		}

		string normalized = type.Trim();
		if (normalized.StartsWith("escaped ", StringComparison.Ordinal))
		{
			isEscaped = true;
			normalized = normalized["escaped ".Length..].Trim();
		}
		if (normalized.EndsWith(" escaped", StringComparison.Ordinal))
		{
			isEscaped = true;
			normalized = normalized[..^" escaped".Length].Trim();
		}
		if (normalized.StartsWith("scoped ", StringComparison.Ordinal))
			normalized = normalized["scoped ".Length..].Trim();
		if (normalized.EndsWith(" scoped", StringComparison.Ordinal))
			normalized = normalized[..^" scoped".Length].Trim();
		if (normalized.StartsWith("unscoped ", StringComparison.Ordinal))
			normalized = normalized["unscoped ".Length..].Trim();
		if (normalized.EndsWith(" unscoped", StringComparison.Ordinal))
			normalized = normalized[..^" unscoped".Length].Trim();

		return TryGetCallableShape(normalized, out shape);
	}

	bool IsEscapedDelegateLambdaTarget(string? type)
	{
		return TryGetLambdaCallableShape(type, out CallableShape shape, out bool isEscaped)
			&& isEscaped
			&& shape.Kind == "delegate";
	}

	Expression LowerLambdaExpression(LambdaExpression lambda)
	{
		if (expressionRewrites.TryGetValue(lambda, out Expression? rewritten)
			&& !ReferenceEquals(rewritten, lambda))
			return rewritten;

		if (!TryGetLambdaCallableShape(lambda.ResolvedType, out CallableShape shape, out bool isEscaped) || shape.Kind is not ("fn" or "delegate"))
		{
			Report(GetRange(lambda.SourceSyntax), "Lambda lowering supports only fn or delegate targets.");
			return lambda;
		}
		List<LambdaCapture> captures = CollectLambdaCaptures(lambda, currentRewriteFunction, FindCurrentRewriteContainingType(), reportUnsupported: true);
		if (captures.Count > 0 && shape.Kind != "delegate")
		{
			Report(GetRange(lambda.SourceSyntax), "Capturing lambdas require a delegate target.");
			return lambda;
		}

		bool delegateTarget = shape.Kind == "delegate";
		LambdaContextInfo? contextInfo = null;
		if (captures.Count > 0)
		{
			contextInfo = GetOrCreateLambdaContext(lambda, captures, isEscaped);
			EnsureLambdaContextLocal(lambda, contextInfo, currentStatementPrefix);
			if (contextInfo.LocalTarget is null)
				return lambda;
		}

		FunctionDefinition function = CreateLambdaFunction(lambda, shape, delegateTarget);
		ExpandParamsFunctionDeclarations(function);
		int parameterOffset = delegateTarget ? 1 : 0;
		RewriteLambdaParameterReferences(function.Body, lambda.Parameters, function.Parameters, parameterOffset);
		if (contextInfo is not null)
			RewriteLambdaCaptureReferences(function, contextInfo);
		RewriteFunction(function, containingType: null);
		RewriteLambdaParameterReferences(function.Body, lambda.Parameters, function.Parameters, parameterOffset);
		generatedLambdaDefinitions.Add(function);
		Expression result = delegateTarget
			? CreateDelegateLambdaInitializer(lambda, function, contextInfo)
			: CreateMethodReference(function, lambda.ResolvedType ?? BuildFunctionValueType(function, isInstance: false));
		expressionRewrites[lambda] = result;
		return result;
	}

	FunctionDefinition CreateLambdaFunction(LambdaExpression lambda, CallableShape shape, bool includeDelegateContext)
	{
		string owner = GetLambdaOwnerName();
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

	InitializerExpression CreateDelegateLambdaInitializer(LambdaExpression lambda, FunctionDefinition function, LambdaContextInfo? contextInfo)
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
			Expression = contextInfo?.LocalTarget is null
				? NullLiteral(lambda.SourceSyntax)
				: contextInfo.IsEscaped
					? CreateVariableReference(contextInfo.LocalTarget, contextInfo.LocalTarget.ResolvedType ?? contextInfo.Definition.Name + "*")
					: new UnaryExpression
				{
					SourceSyntax = lambda.SourceSyntax,
					Operator = UnaryOperator.AddressOf,
					Operand = CreateVariableReference(contextInfo.LocalTarget, contextInfo.Definition.Name),
					ResolvedType = contextInfo.Definition.Name + "*"
				}
		});
		return initializer;
	}

	LambdaContextInfo GetOrCreateLambdaContext(LambdaExpression lambda, List<LambdaCapture> captures, bool isEscaped)
	{
		if (lambdaContexts.TryGetValue(lambda, out LambdaContextInfo? context))
			return context;

		string owner = GetLambdaOwnerName();
		string name = owner + "_lambdaContext" + generatedLambdaContextDefinitions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
		StructDefinition definition = new()
		{
			SourceSyntax = lambda.SourceSyntax,
			Name = name,
			Symbol = name,
			ResolvedType = name
		};

		List<LambdaCapture> contextCaptures = [];
		for (int i = 0; i < captures.Count; i++)
		{
			LambdaCapture capture = captures[i];
			string fieldName = "capture" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + capture.Name;
			FieldDefinition field = new()
			{
				SourceSyntax = lambda.SourceSyntax,
				Name = fieldName,
				Symbol = fieldName,
				Type = isEscaped ? TypeReferenceForResolvedName(capture.Type) : PointerTo(TypeReferenceForResolvedName(capture.Type)),
				ResolvedType = isEscaped ? capture.Type : AddPointer(capture.Type)
			};
			definition.Fields.Add(field);
			contextCaptures.Add(capture with { Field = field });
		}

		context = new LambdaContextInfo(definition, contextCaptures, isEscaped);
		lambdaContexts[lambda] = context;
		generatedLambdaContextDefinitions.Add(definition);
		return context;
	}

	string GetLambdaOwnerName()
	{
		if (currentRewriteFunction is null)
			return "lambda";
		string functionName = GetCallableName(currentRewriteFunction).TrimStart('~');
		return FindCurrentRewriteContainingType() is TypeDefinition owner
			? owner.Name + "_" + functionName
			: functionName;
	}

	void EnsureLambdaContextLocal(LambdaExpression lambda, LambdaContextInfo context, List<Statement>? statements)
	{
		if (context.LocalTarget is not null)
			return;
		if (statements is null)
		{
			Report(GetRange(lambda.SourceSyntax), "Captured lambda context cannot be created in this expression position yet.");
			return;
		}

		string typeName = context.IsEscaped ? context.Definition.Name + "*" : context.Definition.Name;
		TypeReference localType = context.IsEscaped ? PointerTo(TypeReferenceFor(context.Definition)) : TypeReferenceFor(context.Definition);
		Expression? initialValue = context.IsEscaped ? CreateAllocCall(TypeReferenceFor(context.Definition), lambda.SourceSyntax) : null;
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("lambdaContext"), typeName, localType, initialValue);
		context.LocalTarget = local.Target;
		statements.Add(local);
		foreach (LambdaCapture capture in context.Captures)
		{
			statements.Add(CreateAssignmentStatement(
				new MemberReferenceExpression
				{
					SourceSyntax = lambda.SourceSyntax,
					Target = CreateVariableReference(local.Target, typeName),
					Name = capture.Field.Name,
					Member = capture.Field,
					ResolvedType = capture.Field.ResolvedType
				},
				context.IsEscaped
					? CreateVariableReference(capture.Variable, capture.Type)
					: new UnaryExpression
					{
						SourceSyntax = lambda.SourceSyntax,
						Operator = UnaryOperator.AddressOf,
						Operand = CreateVariableReference(capture.Variable, capture.Type),
						ResolvedType = AddPointer(capture.Type)
					},
				capture.Field.ResolvedType ?? ErrorType,
				lambda.SourceSyntax));
		}
	}

	void PrepareLambdaContextLocal(LambdaExpression lambda, List<Statement> statements)
	{
		if (!TryGetLambdaCallableShape(lambda.ResolvedType, out CallableShape shape, out bool isEscaped) || shape.Kind != "delegate")
			return;
		List<LambdaCapture> captures = CollectLambdaCaptures(lambda, currentRewriteFunction, FindCurrentRewriteContainingType(), reportUnsupported: true);
		if (captures.Count == 0)
			return;
		LambdaContextInfo context = GetOrCreateLambdaContext(lambda, captures, isEscaped);
		EnsureLambdaContextLocal(lambda, context, statements);
	}

	void RewriteLambdaCaptureReferences(FunctionDefinition function, LambdaContextInfo context)
	{
		if (function.Body is null || function.Parameters.Count == 0)
			return;
		ParameterDefinition contextParameter = function.Parameters[0];
		string localName = "lambdaContext";
		DeclarationStatement contextLocal = CreateGeneratedLocal(
			localName,
			context.Definition.Name + "*",
			PointerTo(TypeReferenceFor(context.Definition)),
			new CastExpression
			{
				SourceSyntax = function.SourceSyntax,
				Type = PointerTo(TypeReferenceFor(context.Definition)),
				Expression = CreateVariableReference(contextParameter, contextParameter.ResolvedType ?? "void*"),
				ResolvedType = context.Definition.Name + "*"
			});
		function.Body.Statements.Insert(0, contextLocal);
		RewriteLambdaCaptureReferences(function.Body, context, contextLocal.Target);
	}

	void RewriteLambdaCaptureReferences(Statement? statement, LambdaContextInfo context, DeclarationTarget contextLocal)
	{
		if (statement is null)
			return;
		switch (statement)
		{
			case BlockStatement block:
				foreach (Statement child in block.Statements)
					RewriteLambdaCaptureReferences(child, context, contextLocal);
				break;
			case ExpressionStatement expression:
				expression.Expression = RewriteLambdaCaptureReferences(expression.Expression, context, contextLocal);
				break;
			case DeclarationStatement declaration:
				declaration.InitialValue = RewriteLambdaCaptureReferences(declaration.InitialValue, context, contextLocal);
				break;
			case IfStatement ifStatement:
				ifStatement.Condition = RewriteLambdaCaptureReferences(ifStatement.Condition, context, contextLocal);
				RewriteLambdaCaptureReferences(ifStatement.Body, context, contextLocal);
				RewriteLambdaCaptureReferences(ifStatement.ElseBody, context, contextLocal);
				break;
			case WhileStatement whileStatement:
				whileStatement.Condition = RewriteLambdaCaptureReferences(whileStatement.Condition, context, contextLocal);
				RewriteLambdaCaptureReferences(whileStatement.Body, context, contextLocal);
				break;
			case DoWhileStatement doWhile:
				RewriteLambdaCaptureReferences(doWhile.Body, context, contextLocal);
				doWhile.Condition = RewriteLambdaCaptureReferences(doWhile.Condition, context, contextLocal);
				break;
			case ForStatement forStatement:
				RewriteLambdaCaptureReferences(forStatement.Condition.Declaration, context, contextLocal);
				for (int i = 0; i < forStatement.Condition.Clauses.Count; i++)
					forStatement.Condition.Clauses[i] = RewriteLambdaCaptureReferences(forStatement.Condition.Clauses[i], context, contextLocal);
				RewriteLambdaCaptureReferences(forStatement.Body, context, contextLocal);
				break;
			case ForeachStatement foreachStatement:
				foreachStatement.Source = RewriteLambdaCaptureReferences(foreachStatement.Source, context, contextLocal);
				RewriteLambdaCaptureReferences(foreachStatement.Body, context, contextLocal);
				break;
			case SwitchStatement switchStatement:
				switchStatement.Expression = RewriteLambdaCaptureReferences(switchStatement.Expression, context, contextLocal);
				foreach (Statement child in switchStatement.Statements)
					RewriteLambdaCaptureReferences(child, context, contextLocal);
				break;
			case CaseStatement caseStatement:
				caseStatement.Expression = RewriteLambdaCaptureReferences(caseStatement.Expression, context, contextLocal);
				break;
			case ReturnStatement returnStatement:
				returnStatement.Expression = RewriteLambdaCaptureReferences(returnStatement.Expression, context, contextLocal);
				break;
			case YieldStatement yieldStatement:
				yieldStatement.Expression = RewriteLambdaCaptureReferences(yieldStatement.Expression, context, contextLocal);
				break;
			case DeleteStatement deleteStatement:
				deleteStatement.Expression = RewriteLambdaCaptureReferences(deleteStatement.Expression, context, contextLocal);
				break;
			case TryStatement tryStatement:
				RewriteLambdaCaptureReferences(tryStatement.Body, context, contextLocal);
				foreach (CatchStatement catchStatement in tryStatement.Catches)
					RewriteLambdaCaptureReferences(catchStatement, context, contextLocal);
				RewriteLambdaCaptureReferences(tryStatement.Finally, context, contextLocal);
				break;
			case CatchStatement catchStatement:
				RewriteLambdaCaptureReferences(catchStatement.Body, context, contextLocal);
				break;
			case FinallyStatement finallyStatement:
				RewriteLambdaCaptureReferences(finallyStatement.Body, context, contextLocal);
				break;
			case WithinStatement withinStatement:
				withinStatement.Allocator = RewriteLambdaCaptureReferences(withinStatement.Allocator, context, contextLocal);
				RewriteLambdaCaptureReferences(withinStatement.Body, context, contextLocal);
				break;
		}
	}

	Expression? RewriteLambdaCaptureReferences(Expression? expression, LambdaContextInfo context, DeclarationTarget contextLocal)
	{
		if (expression is null)
			return null;
		if (expression is ThisExpression
			&& FindThisCapture(context) is LambdaCapture thisCapture)
			return CreateCapturedValueReference(thisCapture, contextLocal, expression.SourceSyntax);

		if (expression is MemberExpression memberExpression
			&& TryCreateCapturedParamsComponentReference(memberExpression.Target, memberExpression.Name, context, contextLocal, expression.SourceSyntax, out Expression? sourceComponentReference))
			return sourceComponentReference;

		if (expression is MemberReferenceExpression memberReference
			&& TryCreateCapturedParamsComponentReference(memberReference.Target, memberReference.Name, context, contextLocal, expression.SourceSyntax, out Expression? componentReference))
			return componentReference;

		if (TryGetCapturedVariable(expression, out BindableNode? captured)
			&& FindCapture(context, captured) is LambdaCapture capture)
			return CreateCapturedValueReference(capture, contextLocal, expression.SourceSyntax);

		switch (expression)
		{
			case ParenthesizedExpression parenthesized:
				parenthesized.Expression = RewriteLambdaCaptureReferences(parenthesized.Expression, context, contextLocal);
				break;
			case CastExpression cast:
				cast.Expression = RewriteLambdaCaptureReferences(cast.Expression, context, contextLocal);
				break;
			case WithinExpression within:
				within.Context = RewriteLambdaCaptureReferences(within.Context, context, contextLocal);
				within.Expression = RewriteLambdaCaptureReferences(within.Expression, context, contextLocal);
				break;
			case UnaryExpression unary:
				unary.Operand = RewriteLambdaCaptureReferences(unary.Operand, context, contextLocal);
				unary.Context = RewriteLambdaCaptureReferences(unary.Context, context, contextLocal);
				break;
			case PostfixUpdateExpression postfix:
				postfix.Expression = RewriteLambdaCaptureReferences(postfix.Expression, context, contextLocal);
				break;
			case BinaryExpression binary:
				binary.Left = RewriteLambdaCaptureReferences(binary.Left, context, contextLocal);
				binary.Right = RewriteLambdaCaptureReferences(binary.Right, context, contextLocal);
				break;
			case AssignmentExpression assignment:
				assignment.Target = RewriteLambdaCaptureReferences(assignment.Target, context, contextLocal);
				assignment.Value = RewriteLambdaCaptureReferences(assignment.Value, context, contextLocal);
				break;
			case ConditionalExpression conditional:
				conditional.Condition = RewriteLambdaCaptureReferences(conditional.Condition, context, contextLocal);
				conditional.WhenTrue = RewriteLambdaCaptureReferences(conditional.WhenTrue, context, contextLocal);
				conditional.WhenFalse = RewriteLambdaCaptureReferences(conditional.WhenFalse, context, contextLocal);
				break;
			case CallExpression call:
				call.Target = RewriteLambdaCaptureReferences(call.Target, context, contextLocal);
				foreach (ArgumentExpression argument in call.Arguments)
					argument.Value = RewriteLambdaCaptureReferences(argument.Value, context, contextLocal);
				break;
			case IndexExpression index:
				if (TryCreateCapturedParamsComponentReference(index.Target, "elements", context, contextLocal, index.Target?.SourceSyntax ?? index.SourceSyntax, out Expression? elementsReference))
					index.Target = elementsReference;
				else
					index.Target = RewriteLambdaCaptureReferences(index.Target, context, contextLocal);
				foreach (ArgumentExpression argument in index.Arguments)
					argument.Value = RewriteLambdaCaptureReferences(argument.Value, context, contextLocal);
				break;
			case MemberExpression member:
				member.Target = RewriteLambdaCaptureReferences(member.Target, context, contextLocal);
				break;
			case MemberReferenceExpression member:
				member.Target = RewriteLambdaCaptureReferences(member.Target, context, contextLocal);
				break;
			case NamelessIndexerExpression indexer:
				indexer.Target = RewriteLambdaCaptureReferences(indexer.Target, context, contextLocal);
				foreach (ArgumentExpression argument in indexer.Arguments)
					argument.Value = RewriteLambdaCaptureReferences(argument.Value, context, contextLocal);
				break;
			case RangeExpression range:
				range.Start = RewriteLambdaCaptureReferences(range.Start, context, contextLocal);
				range.End = RewriteLambdaCaptureReferences(range.End, context, contextLocal);
				break;
			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
					item.Expression = RewriteLambdaCaptureReferences(item.Expression, context, contextLocal);
				break;
			case ArrayExpression array:
				for (int i = 0; i < array.Elements.Count; i++)
					array.Elements[i] = RewriteLambdaCaptureReferences(array.Elements[i], context, contextLocal) ?? array.Elements[i];
				break;
			case InitializerExpression initializer:
				foreach (InitializerItem item in initializer.Items)
					item.Expression = RewriteLambdaCaptureReferences(item.Expression, context, contextLocal);
				break;
			case ConstructionExpression construction:
				construction.ElementCount = RewriteLambdaCaptureReferences(construction.ElementCount, context, contextLocal);
				if (construction.Initializer is not null)
					construction.Initializer = (InitializerExpression?)RewriteLambdaCaptureReferences(construction.Initializer, context, contextLocal);
				foreach (ArgumentExpression argument in construction.Arguments)
					argument.Value = RewriteLambdaCaptureReferences(argument.Value, context, contextLocal);
				break;
			case FinallyDeleteExpression finallyDelete:
				finallyDelete.Expression = RewriteLambdaCaptureReferences(finallyDelete.Expression, context, contextLocal);
				break;
			case ArgumentExpression argument:
				argument.Value = RewriteLambdaCaptureReferences(argument.Value, context, contextLocal);
				break;
		}
		return expression;
	}

	bool TryCreateCapturedParamsComponentReference(Expression? source, string componentName, LambdaContextInfo context, DeclarationTarget contextLocal, SyntaxNode? syntax, out Expression? reference)
	{
		reference = null;
		if (source is null || !TryGetCapturedVariable(source, out BindableNode? variable))
			return false;
		if (!paramsExpansions.TryGetValue(variable, out List<ParamsExpansionComponent>? expansion))
			return false;
		foreach (ParamsExpansionComponent component in expansion)
		{
			if (component.SourceName != componentName)
				continue;
			if (FindCapture(context, component.Node) is not LambdaCapture capture)
				return false;
			reference = CreateCapturedValueReference(capture, contextLocal, syntax);
			return true;
		}
		return false;
	}

	Expression CreateCapturedValueReference(LambdaCapture capture, DeclarationTarget contextLocal, SyntaxNode? syntax)
	{
		MemberReferenceExpression fieldReference = new()
		{
			SourceSyntax = syntax,
			Target = CreateVariableReference(contextLocal, contextLocal.ResolvedType ?? ErrorType),
			Name = capture.Field.Name,
			Member = capture.Field,
			ResolvedType = capture.Field.ResolvedType
		};
		if (capture.Field.ResolvedType == capture.Type)
			return fieldReference;

		return new ParenthesizedExpression
		{
			SourceSyntax = syntax,
			ResolvedType = capture.Type,
			Expression = new UnaryExpression
			{
				SourceSyntax = syntax,
				Operator = UnaryOperator.PointerDereference,
				Operand = fieldReference,
				ResolvedType = capture.Type
			},
		};
	}

	static LambdaCapture? FindCapture(LambdaContextInfo context, BindableNode variable)
	{
		foreach (LambdaCapture capture in context.Captures)
			if (ReferenceEquals(capture.Variable, variable))
				return capture;
		return null;
	}

	static LambdaCapture? FindThisCapture(LambdaContextInfo context)
	{
		foreach (LambdaCapture capture in context.Captures)
			if (capture.IsThis)
				return capture;
		return null;
	}

	bool TryGetCapturedVariable(Expression expression, out BindableNode variable)
	{
		switch (expression)
		{
			case VariableReferenceExpression { Variable: BindableNode direct }:
				variable = direct;
				return true;
			case NamedExpression named when TryGetRewrittenVariable(named, out BindableNode rewritten):
				variable = rewritten;
				return true;
			default:
				variable = null!;
				return false;
		}
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
		ParameterModifier modifier = source.Modifier;
		string resolvedType = parameterType;
		if (source.Modifier == ParameterModifier.None)
		{
			if (resolvedType.StartsWith("in ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.In;
				resolvedType = resolvedType["in ".Length..].Trim();
			}
			else if (resolvedType.StartsWith("out ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.Out;
				resolvedType = resolvedType["out ".Length..].Trim();
			}
			else if (resolvedType.StartsWith("thrown ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.Thrown;
				resolvedType = resolvedType["thrown ".Length..].Trim();
			}
			else if (resolvedType.StartsWith("within ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.Within;
				resolvedType = resolvedType["within ".Length..].Trim();
			}
		}
		return new ParameterDefinition
		{
			SourceSyntax = parameter.SourceSyntax,
			Name = name,
			Symbol = name,
			Modifier = modifier,
			Type = source.Type is null ? TypeReferenceForResolvedName(resolvedType) : CloneType(source.Type),
			ResolvedType = resolvedType
		};
	}

	bool LambdaHasCaptures(LambdaExpression lambda)
	{
		return CollectLambdaCaptures(lambda, currentRewriteFunction, FindCurrentRewriteContainingType(), reportUnsupported: false).Count > 0;
	}

	TypeDefinition? FindCurrentRewriteContainingType()
	{
		return currentRewriteFunction is null ? null : FindContainingType(currentRewriteFunction);
	}

	bool LambdaHasCaptures(LambdaExpression lambda, FunctionDefinition? currentFunction, TypeDefinition? containingType)
	{
		return CollectLambdaCaptures(lambda, currentFunction, containingType, reportUnsupported: false).Count > 0;
	}

	List<LambdaCapture> CollectLambdaCaptures(LambdaExpression lambda, FunctionDefinition? currentFunction, TypeDefinition? containingType, bool reportUnsupported)
	{
		HashSet<BindableNode> localNodes = [];
		foreach (LambdaParameter parameter in lambda.Parameters)
			localNodes.Add(parameter);
		CollectLambdaLocalNodes(lambda.Body, localNodes);
		Dictionary<BindableNode, LambdaCapture> captures = new(ReferenceEqualityComparer.Instance);
		BindableNode? thisCaptureNode = CreateLambdaThisCaptureNode(currentFunction, containingType, lambda.SourceSyntax, reportUnsupported);
		CollectLambdaCaptures(lambda.Body, localNodes, captures, thisCaptureNode, reportUnsupported);
		return [.. captures.Values];
	}

	void ValidateEscapedLambdaCaptures(LambdaExpression lambda, List<LambdaCapture> captures, BodyScope scope)
	{
		foreach (LambdaCapture capture in captures)
		{
			string? factText = GetStorageValueLifetimeFact(capture.Variable);
			if (capture.IsThis)
			{
				if (!TryParseLifetimeFact(factText, out LifetimeFact thisFact) || thisFact.Kind != "escaped")
					Report(GetRange(capture.SourceSyntax ?? lambda.SourceSyntax), "Escaped delegate lambdas cannot capture 'this' unless the receiver is escaped.");
				continue;
			}

			if (!IsLifetimePointerBearingResolvedType(capture.Type, scope))
				continue;
			if (!TryParseLifetimeFact(factText, out LifetimeFact fact) || fact.Kind != "escaped")
				Report(GetRange(capture.SourceSyntax ?? lambda.SourceSyntax), $"Escaped delegate lambdas cannot capture '{capture.Name}' because its value is not escaped.");
		}

		ValidateEscapedLambdaReadOnlyCaptures(lambda.Body, captures);
	}

	void ValidateEscapedLambdaReadOnlyCaptures(Statement? statement, List<LambdaCapture> captures)
	{
		if (statement is null)
			return;

		switch (statement)
		{
			case ExpressionStatement expression:
				ValidateEscapedLambdaReadOnlyCaptures(expression.Expression, captures);
				break;
			case DeclarationStatement declaration:
				ValidateEscapedLambdaReadOnlyCaptures(declaration.InitialValue, captures);
				break;
			default:
				foreach ((Statement? childStatement, Expression? childExpression) in LambdaStatementChildren(statement))
				{
					ValidateEscapedLambdaReadOnlyCaptures(childStatement, captures);
					ValidateEscapedLambdaReadOnlyCaptures(childExpression, captures);
				}
				break;
		}
	}

	void ValidateEscapedLambdaReadOnlyCaptures(Expression? expression, List<LambdaCapture> captures)
	{
		if (expression is null)
			return;

		switch (expression)
		{
			case AssignmentExpression assignment:
				if (TryFindCapturedMutationRoot(assignment.Target, captures, out LambdaCapture? capture))
					Report(GetRange(assignment.Target?.SourceSyntax ?? assignment.SourceSyntax), $"Captured value '{capture.Name}' is read-only inside an escaped delegate lambda.");
				ValidateEscapedLambdaReadOnlyCaptures(assignment.Value, captures);
				return;

			case PostfixUpdateExpression postfix:
				if (TryFindCapturedMutationRoot(postfix.Expression, captures, out LambdaCapture? postfixCapture))
					Report(GetRange(postfix.Expression?.SourceSyntax ?? postfix.SourceSyntax), $"Captured value '{postfixCapture.Name}' is read-only inside an escaped delegate lambda.");
				return;

			case UnaryExpression { Operator: UnaryOperator.Increment or UnaryOperator.Decrement } unary:
				if (TryFindCapturedMutationRoot(unary.Operand, captures, out LambdaCapture? unaryCapture))
					Report(GetRange(unary.Operand?.SourceSyntax ?? unary.SourceSyntax), $"Captured value '{unaryCapture.Name}' is read-only inside an escaped delegate lambda.");
				return;
		}

		foreach (Expression child in LambdaExpressionChildren(expression))
			ValidateEscapedLambdaReadOnlyCaptures(child, captures);
	}

	bool TryFindCapturedMutationRoot(Expression? expression, List<LambdaCapture> captures, out LambdaCapture capture)
	{
		capture = null!;
		if (expression is null)
			return false;
		if (TryGetCapturedVariable(expression, out BindableNode? variable)
			&& FindCapture(captures, variable) is LambdaCapture direct)
		{
			capture = direct;
			return true;
		}

		return expression switch
		{
			ParenthesizedExpression parenthesized => TryFindCapturedMutationRoot(parenthesized.Expression, captures, out capture),
			MemberExpression member => TryFindCapturedMutationRoot(member.Target, captures, out capture),
			MemberReferenceExpression member => TryFindCapturedMutationRoot(member.Target, captures, out capture),
			IndexExpression index => TryFindCapturedMutationRoot(index.Target, captures, out capture),
			NamelessIndexerExpression indexer => TryFindCapturedMutationRoot(indexer.Target, captures, out capture),
			UnaryExpression { Operator: UnaryOperator.PointerDereference } unary => TryFindCapturedMutationRoot(unary.Operand, captures, out capture),
			_ => false
		};
	}

	static LambdaCapture? FindCapture(List<LambdaCapture> captures, BindableNode variable)
	{
		foreach (LambdaCapture capture in captures)
			if (ReferenceEquals(capture.Variable, variable))
				return capture;
		return null;
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

	void CollectLambdaCaptures(BindableNode? node, HashSet<BindableNode> localNodes, Dictionary<BindableNode, LambdaCapture> captures, BindableNode? thisCaptureNode, bool reportUnsupported)
	{
		switch (node)
		{
			case null:
				return;
			case ThisExpression thisExpression:
				if (thisCaptureNode is null)
				{
					if (reportUnsupported)
						Report(GetRange(thisExpression.SourceSyntax), "'this' is not available in this lambda context.");
					return;
				}
				AddLambdaCapture(thisCaptureNode, thisExpression.SourceSyntax, captures, reportUnsupported, isThis: true);
				return;
			case VariableReferenceExpression { Variable: BindableNode variable } reference
				when !IsLambdaLocalOrGlobal(variable, localNodes):
				AddLambdaCapture(variable, reference.SourceSyntax, captures, reportUnsupported);
				return;
			case NamedExpression named
				when TryGetRewrittenVariable(named, out BindableNode variable) && !IsLambdaLocalOrGlobal(variable, localNodes):
				AddLambdaCapture(variable, named.SourceSyntax, captures, reportUnsupported);
				return;
		}

		if (node is Statement statement)
		{
			foreach ((Statement? childStatement, Expression? childExpression) in LambdaStatementChildren(statement))
			{
				if (childStatement is not null)
					CollectLambdaCaptures(childStatement, localNodes, captures, thisCaptureNode, reportUnsupported);
				if (childExpression is not null)
					CollectLambdaCaptures(childExpression, localNodes, captures, thisCaptureNode, reportUnsupported);
			}
		}
		else if (node is Expression expression)
		{
			foreach (Expression childExpression in LambdaExpressionChildren(expression))
				CollectLambdaCaptures(childExpression, localNodes, captures, thisCaptureNode, reportUnsupported);
		}
	}

	void AddLambdaCapture(BindableNode variable, SyntaxNode? syntax, Dictionary<BindableNode, LambdaCapture> captures, bool reportUnsupported, bool isThis = false)
	{
		if (!isThis && paramsExpansions.TryGetValue(variable, out List<ParamsExpansionComponent>? expansion))
		{
			foreach (ParamsExpansionComponent component in expansion)
				AddSingleLambdaCapture(component.Node, component.Name, component.Type, syntax, captures, reportUnsupported);
			return;
		}

		AddSingleLambdaCapture(variable, isThis ? "this" : GetCaptureName(variable), GetCaptureType(variable), syntax, captures, reportUnsupported, isThis);
	}

	void AddSingleLambdaCapture(BindableNode variable, string name, string type, SyntaxNode? syntax, Dictionary<BindableNode, LambdaCapture> captures, bool reportUnsupported, bool isThis = false)
	{
		if (captures.ContainsKey(variable))
			return;
		if (string.IsNullOrWhiteSpace(type) || type == ErrorType || type == TargetType)
		{
			if (reportUnsupported)
				Report(GetRange(syntax), "Lambda capture has an unresolved type.");
			return;
		}
		captures[variable] = new LambdaCapture(variable, name, type, new FieldDefinition(), syntax, isThis);
	}

	BindableNode? CreateLambdaThisCaptureNode(FunctionDefinition? currentFunction, TypeDefinition? containingType, SyntaxNode? syntax, bool reportUnsupported)
	{
		if (currentFunction is null)
			return null;

		if (GetExplicitThisParameter(currentFunction) is ThisParameterDefinition explicitThis)
			return explicitThis;

		if (containingType is null)
			return null;

		string receiverType = BuildEffectiveReceiverType($"{containingType.Name}*", currentFunction, IsPropertyGetterFunction(currentFunction));
		if (string.IsNullOrWhiteSpace(receiverType) || receiverType == ErrorType)
		{
			if (reportUnsupported)
				Report(GetRange(syntax), "Lambda capture has an unresolved this type.");
			return null;
		}

		return new ThisParameterDefinition
		{
			SourceSyntax = syntax,
			Name = "this",
			Symbol = "this",
			Type = TypeReferenceForResolvedName(receiverType),
			ResolvedType = receiverType,
			SlotLifetimeFact = currentFunction.ReceiverLifetimeBinding ?? MakeLifetimeFact("scoped", "this", "receiver"),
			ValueLifetimeFact = currentFunction.ReceiverLifetimeBinding ?? MakeLifetimeFact("scoped", "this", "receiver")
		};
	}

	static string GetCaptureName(BindableNode variable)
	{
		string name = variable switch
		{
			ParameterDefinition parameter => parameter.Name,
			DeclarationTarget target when target.Names.Count > 0 => target.Names[0],
			VariableDefinition variableDefinition => variableDefinition.Name,
			FieldDefinition field => field.Name,
			_ => "value"
		};
		return string.IsNullOrWhiteSpace(name) ? "value" : SanitizeCaptureName(name);
	}

	static string SanitizeCaptureName(string name)
	{
		System.Text.StringBuilder builder = new();
		foreach (char c in name)
			builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
		return builder.Length == 0 ? "value" : builder.ToString();
	}

	static string GetCaptureType(BindableNode variable)
	{
		return variable switch
		{
			ParameterDefinition parameter => parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? ErrorType,
			DeclarationTarget target => target.ResolvedType ?? target.Type?.ResolvedType ?? ErrorType,
			VariableDefinition variableDefinition => variableDefinition.ResolvedType ?? variableDefinition.Type?.ResolvedType ?? ErrorType,
			FieldDefinition field => field.ResolvedType ?? field.Type?.ResolvedType ?? ErrorType,
			_ => variable.ResolvedType ?? ErrorType
		};
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
