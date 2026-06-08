using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	const string IteratorStateFieldName = "__state";
	string? currentIteratorStateThisType;

	void GenerateIteratorDeclarations(Module module)
	{
		foreach (Definition definition in module.Definitions.ToArray())
		{
			switch (definition)
			{
				case FunctionDefinition function:
					GenerateIteratorDeclaration(module, function, containingType: null);
					break;

				case ClassDefinition classDefinition:
					foreach (FunctionDefinition function in classDefinition.Functions.ToArray())
						GenerateIteratorDeclaration(module, function, classDefinition);
					break;

				case StructDefinition structDefinition:
					foreach (FunctionDefinition function in structDefinition.Functions.ToArray())
						GenerateIteratorDeclaration(module, function, structDefinition);
					break;

				case InterfaceDefinition interfaceDefinition:
					foreach (FunctionDefinition function in interfaceDefinition.Functions.ToArray())
						GenerateIteratorDeclaration(module, function, interfaceDefinition);
					break;

				case EnumDefinition enumDefinition:
					foreach (FunctionDefinition function in enumDefinition.Functions.ToArray())
						GenerateIteratorDeclaration(module, function, enumDefinition);
					break;

				case NewtypeDefinition newtypeDefinition:
					foreach (FunctionDefinition function in newtypeDefinition.Functions.ToArray())
						GenerateIteratorDeclaration(module, function, newtypeDefinition);
					break;
			}
		}
	}

	void GenerateIteratorDeclaration(Module module, FunctionDefinition function, TypeDefinition? containingType)
	{
		if (function.IteratorKind == IteratorKind.None)
			return;

		if (function.ReturnType is not IterTypeReference iterType)
		{
			Report(GetRange(function.SourceSyntax), "Iterator generator return type must be an iter type.");
			return;
		}

		bool invalidGeneratorParameters = false;
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier is ParameterModifier.In or ParameterModifier.Out or ParameterModifier.Thrown)
				invalidGeneratorParameters = true;
		}
		if (invalidGeneratorParameters)
			return;

		string stateName = GetIteratorStateTypeName(function, containingType);
		if (typeDefinitions.ContainsKey(stateName))
		{
			Report(GetNameRange(function), $"Iterator state type '{stateName}' is already declared.");
			return;
		}

		IteratorKind iteratorKind = function.IteratorKind;
		TypeDefinition stateType = iteratorKind == IteratorKind.Class
			? CreateIteratorClass(function, iterType, stateName)
			: CreateIteratorStruct(function, iterType, stateName);
		module.Definitions.Add(stateType);
		typeDefinitions[stateType.Name] = stateType;
		typeInfos[stateType] = new TypeAnalysisInfo(stateType);

		function.IteratorKind = IteratorKind.None;
		function.ReturnType = iteratorKind == IteratorKind.Class
			? PointerTo(TypeReferenceFor(stateType))
			: TypeReferenceFor(stateType);
		function.ResolvedType = function.ReturnType.ResolvedType;
		function.Body = CreateIteratorFactoryBody(function, stateType);
	}

	ClassDefinition CreateIteratorClass(FunctionDefinition function, IterTypeReference iterType, string stateName)
	{
		ClassDefinition state = new()
		{
			SourceSyntax = function.SourceSyntax,
			Name = stateName,
			Symbol = stateName,
			Export = function.Export,
			ResolvedType = stateName
		};
		AddIteratorStateMembers(state, function, iterType);
		return state;
	}

	StructDefinition CreateIteratorStruct(FunctionDefinition function, IterTypeReference iterType, string stateName)
	{
		StructDefinition state = new()
		{
			SourceSyntax = function.SourceSyntax,
			Name = stateName,
			Symbol = stateName,
			Export = function.Export,
			Modifier = StructModifier.Fixed,
			ResolvedType = stateName
		};
		AddIteratorStateMembers(state, function, iterType);
		return state;
	}

	void AddIteratorStateMembers(TypeDefinition state, FunctionDefinition function, IterTypeReference iterType)
	{
		AddIteratorStateFields(state, function);
		AddIteratorLiftedLocalFields(state, function);
		AddIteratorNextMethod(state, function, iterType);
		AddIteratorDestructor(state, function);
	}

	void AddIteratorStateFields(TypeDefinition state, FunctionDefinition function)
	{
		AddIteratorField(state, new FieldDefinition
		{
			SourceSyntax = function.SourceSyntax,
			Name = IteratorStateFieldName,
			Symbol = IteratorStateFieldName,
			Type = new PrimitiveTypeReference { Type = PrimitiveType.Int, ResolvedType = "int" },
			ResolvedType = "int"
		});

		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (IsHiddenParameter(parameter) || parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown)
				continue;

			AddIteratorField(state, new FieldDefinition
			{
				SourceSyntax = parameter.SourceSyntax,
				Name = parameter.Name,
				Symbol = parameter.Name,
				Type = CloneType(parameter.Type),
				ResolvedType = parameter.ResolvedType
			});
		}
	}

	void AddIteratorLiftedLocalFields(TypeDefinition state, FunctionDefinition function)
	{
		HashSet<string> names = new(StringComparer.Ordinal);
		foreach (FieldDefinition field in GetIteratorFields(state))
			if (!string.IsNullOrWhiteSpace(field.Name))
				names.Add(field.Name);

		foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(function.Body))
		{
			foreach (string name in declaration.Target.Names)
			{
				if (name == "_")
					continue;
				if (!names.Add(name))
				{
					Report(GetDeclarationTargetNameRange(declaration.Target.SourceSyntax ?? declaration.SourceSyntax, name), $"Iterator state field '{name}' is already declared.");
					continue;
				}
				if (declaration.Target.Type is AutoTypeReference)
				{
					Report(GetDeclarationTargetNameRange(declaration.Target.SourceSyntax ?? declaration.SourceSyntax, name), $"Iterator local '{name}' must have an explicit type so it can be lifted into the iterator state.");
					continue;
				}

				AddIteratorField(state, new FieldDefinition
				{
					SourceSyntax = declaration.SourceSyntax,
					Name = name,
					Symbol = name,
					Type = CloneType(declaration.Target.Type),
					ResolvedType = declaration.Target.ResolvedType ?? declaration.Target.Type?.ResolvedType ?? FormatTypeReference(declaration.Target.Type)
				});
			}
		}
	}

	void AddIteratorNextMethod(TypeDefinition state, FunctionDefinition function, IterTypeReference iterType)
	{
		FunctionDefinition next = new()
		{
			SourceSyntax = function.SourceSyntax,
			Name = "next",
			Symbol = $"{state.Name}_next",
			Export = function.Export,
			ReturnType = new PrimitiveTypeReference { Type = PrimitiveType.Bool, ResolvedType = "bool" },
			ResolvedType = "bool"
		};

		foreach (ParameterDefinition slot in GetIteratorYieldSlots(iterType))
		{
			string slotName = string.IsNullOrWhiteSpace(slot.Name) ? "current" : slot.Name;
			next.Parameters.Add(new ParameterDefinition
			{
				SourceSyntax = slot.SourceSyntax,
				Name = slotName,
				Symbol = slotName,
				Type = PointerTo(CloneType(slot.Type) ?? VoidType()),
				ResolvedType = $"{slot.ResolvedType ?? slot.Type?.ResolvedType ?? ErrorType}*"
			});
		}

		if (GetIteratorThrownSlot(iterType) is ParameterDefinition thrownSlot)
		{
			next.Parameters.Add(new ParameterDefinition
			{
				SourceSyntax = thrownSlot.SourceSyntax,
				Name = string.IsNullOrWhiteSpace(thrownSlot.Name) ? "error" : thrownSlot.Name,
				Symbol = string.IsNullOrWhiteSpace(thrownSlot.Symbol) ? "error" : thrownSlot.Symbol,
				Modifier = ParameterModifier.Thrown,
				Type = CloneType(thrownSlot.Type),
				ResolvedType = thrownSlot.ResolvedType
			});
		}

		next.Body = CreateIteratorNextBody(function, iterType, state, next.Parameters);
		AddIteratorFunction(state, next);
	}

	void AddIteratorDestructor(TypeDefinition state, FunctionDefinition sourceFunction)
	{
		FunctionDefinition destroy = new()
		{
			Name = "destroy",
			Symbol = $"{state.Name}_destroy",
			Export = state.Export,
			ReturnType = VoidType(),
			ResolvedType = "void",
			Body = new BlockStatement { ResolvedType = "void" }
		};
		foreach (Statement cleanup in GetTopLevelIteratorFinallyStatements(sourceFunction))
			destroy.Body.Statements.Add(CloneStatementForCleanup(cleanup));
		AddIteratorFunction(state, destroy);
	}

	BlockStatement CreateIteratorFactoryBody(FunctionDefinition function, TypeDefinition stateType)
	{
		InitializerExpression initializer = new() { ResolvedType = stateType.Name };
		initializer.Items.Add(new InitializerItem
		{
			Target = InitializerTargetFor(IteratorStateFieldName),
			Expression = NumberLiteral("0", "int"),
			ResolvedType = "int"
		});
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (IsHiddenParameter(parameter) || parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown)
				continue;

			initializer.Items.Add(new InitializerItem
			{
				Target = InitializerTargetFor(parameter.Name),
				Expression = new NamedExpression
				{
					SourceSyntax = parameter.SourceSyntax,
					Name = parameter.Name,
					ResolvedType = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? FormatTypeReference(parameter.Type)
				},
				ResolvedType = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? FormatTypeReference(parameter.Type)
			});
		}

		if (stateType is StructDefinition)
		{
			return new BlockStatement
			{
				ResolvedType = "void",
				Statements =
				{
					new ReturnStatement { Expression = initializer, ResolvedType = "void" }
				}
			};
		}

		string localName = NewGeneratedLocalName("iter");
		DeclarationStatement local = CreateGeneratedLocal(localName, $"{stateType.Name}*", PointerTo(TypeReferenceFor(stateType)), new ConstructionExpression
		{
			SourceSyntax = function.SourceSyntax,
			Kind = ConstructionKind.New,
			Type = TypeReferenceFor(stateType),
			ResolvedType = $"{stateType.Name}*"
		});
		Expression localReference = CreateVariableReference(local.Target, local.Target.ResolvedType ?? $"{stateType.Name}*");
		BlockStatement body = new() { ResolvedType = "void" };
		body.Statements.Add(local);
		foreach (InitializerItem item in initializer.Items)
		{
			body.Statements.Add(new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					Operator = AssignmentOperator.Assign,
					Target = new MemberReferenceExpression
					{
						Target = localReference,
						Name = item.Target?.Parts.Count > 0 ? item.Target.Parts[0].Name ?? "" : "",
						ResolvedType = item.ResolvedType ?? ErrorType
					},
					Value = item.Expression,
					ResolvedType = item.ResolvedType ?? ErrorType
				}
			});
		}
		body.Statements.Add(new ReturnStatement
		{
			Expression = CreateVariableReference(local.Target, local.Target.ResolvedType ?? $"{stateType.Name}*"),
			ResolvedType = "void"
		});
		return body;
	}

	BlockStatement CreateIteratorNextBody(FunctionDefinition function, IterTypeReference iterType, TypeDefinition state, List<ParameterDefinition> nextParameters)
	{
		List<ParameterDefinition> yieldSlots = GetIteratorYieldSlots(iterType);
		if (yieldSlots.Count != 1)
		{
			Report(GetRange(function.Body?.SourceSyntax ?? function.SourceSyntax), "Iterator generator lowering currently supports only one yielded slot.");
			return new BlockStatement
			{
				ResolvedType = "void",
				Statements =
				{
					new ReturnStatement
					{
						Expression = new LiteralExpression { Kind = LiteralKind.False, Text = "false", Value = false, ResolvedType = "bool" },
						ResolvedType = "void"
					}
				}
			};
		}

		IteratorBodyLowering lowering = new(this, function, nextParameters[0], yieldSlots[0].ResolvedType ?? ErrorType);
		string? previousIteratorStateThisType = currentIteratorStateThisType;
		currentIteratorStateThisType = $"{state.Name}*";
		try
		{
			List<Statement> rewrittenStatements = RewriteIteratorBodyStatements(function.Body, lowering);
			BlockStatement body = new() { ResolvedType = "void" };
			body.Statements.AddRange(lowering.CreateResumeDispatch());
			foreach (Statement statement in rewrittenStatements)
				body.Statements.Add(statement);
			body.Statements.AddRange(lowering.CreateCompletion());
			return body;
		}
		finally
		{
			currentIteratorStateThisType = previousIteratorStateThisType;
		}
	}

	List<Statement> RewriteIteratorBodyStatements(BlockStatement? body, IteratorBodyLowering lowering)
	{
		List<Statement> statements = [];
		if (body is null)
			return statements;

		foreach (Statement statement in body.Statements)
		{
			if (statement is FinallyStatement)
				continue;
			statements.AddRange(RewriteIteratorStatement(statement, lowering));
		}
		return statements;
	}

	List<Statement> RewriteIteratorStatement(Statement statement, IteratorBodyLowering lowering)
	{
		switch (statement)
		{
			case YieldStatement yield:
				return lowering.CreateYield(yield.Expression);

			case ReturnStatement:
				return lowering.CreateCompletion();

			case DeclarationStatement declaration:
				return RewriteIteratorDeclaration(declaration, lowering);

			case BlockStatement block:
			{
				BlockStatement rewritten = new() { SourceSyntax = block.SourceSyntax, ResolvedType = "void" };
				foreach (Statement child in block.Statements)
				{
					if (child is FinallyStatement)
						continue;
					rewritten.Statements.AddRange(RewriteIteratorStatement(child, lowering));
				}
				return [rewritten];
			}

			case IfStatement ifStatement:
				ifStatement.Condition = RewriteIteratorExpression(ifStatement.Condition, lowering);
				ifStatement.Body = RewriteIteratorOptionalStatement(ifStatement.Body, lowering);
				ifStatement.ElseBody = RewriteIteratorOptionalStatement(ifStatement.ElseBody, lowering);
				return [ifStatement];

			case WhileStatement whileStatement:
				whileStatement.Condition = RewriteIteratorExpression(whileStatement.Condition, lowering);
				whileStatement.Body = RewriteIteratorOptionalStatement(whileStatement.Body, lowering);
				return [whileStatement];

			case DoWhileStatement doWhile:
				doWhile.Body = RewriteIteratorOptionalStatement(doWhile.Body, lowering);
				doWhile.Condition = RewriteIteratorExpression(doWhile.Condition, lowering);
				return [doWhile];

			case ForStatement forStatement:
				if (forStatement.Condition.Declaration is not null)
				{
					List<Statement> declarations = RewriteIteratorDeclaration(forStatement.Condition.Declaration, lowering);
					forStatement.Condition.Declaration = null;
					forStatement.Condition.Clauses.Insert(0, null);
					for (int i = 0; i < forStatement.Condition.Clauses.Count; i++)
						forStatement.Condition.Clauses[i] = RewriteIteratorExpression(forStatement.Condition.Clauses[i], lowering);
					forStatement.Body = RewriteIteratorOptionalStatement(forStatement.Body, lowering);
					declarations.Add(forStatement);
					return declarations;
				}
				for (int i = 0; i < forStatement.Condition.Clauses.Count; i++)
					forStatement.Condition.Clauses[i] = RewriteIteratorExpression(forStatement.Condition.Clauses[i], lowering);
				forStatement.Body = RewriteIteratorOptionalStatement(forStatement.Body, lowering);
				return [forStatement];

			case ForeachStatement foreachStatement:
				foreachStatement.Source = RewriteIteratorExpression(foreachStatement.Source, lowering);
				foreachStatement.Body = RewriteIteratorOptionalStatement(foreachStatement.Body, lowering);
				return [foreachStatement];

			case SwitchStatement switchStatement:
				switchStatement.Expression = RewriteIteratorExpression(switchStatement.Expression, lowering);
				for (int i = 0; i < switchStatement.Statements.Count; i++)
				{
					List<Statement> rewritten = RewriteIteratorStatement(switchStatement.Statements[i], lowering);
					switchStatement.Statements.RemoveAt(i);
					switchStatement.Statements.InsertRange(i, rewritten);
					i += rewritten.Count - 1;
				}
				return [switchStatement];

			case CaseStatement caseStatement:
				caseStatement.Expression = RewriteIteratorExpression(caseStatement.Expression, lowering);
				return [caseStatement];

			case ExpressionStatement expression:
				expression.Expression = RewriteIteratorExpression(expression.Expression, lowering);
				return [expression];

			case DeleteStatement deleteStatement:
				deleteStatement.Expression = RewriteIteratorExpression(deleteStatement.Expression, lowering);
				return [deleteStatement];

			case TryStatement tryStatement:
				tryStatement.Body = RewriteIteratorOptionalStatement(tryStatement.Body, lowering);
				foreach (CatchStatement catchStatement in tryStatement.Catches)
					catchStatement.Body = RewriteIteratorOptionalStatement(catchStatement.Body, lowering);
				tryStatement.Finally = (FinallyStatement?)RewriteIteratorOptionalStatement(tryStatement.Finally, lowering);
				return [tryStatement];

			case CatchStatement catchStatement:
				catchStatement.Body = RewriteIteratorOptionalStatement(catchStatement.Body, lowering);
				return [catchStatement];

			case FinallyStatement:
				return [];

			case WithinStatement withinStatement:
				withinStatement.Allocator = RewriteIteratorExpression(withinStatement.Allocator, lowering);
				withinStatement.Body = RewriteIteratorOptionalStatement(withinStatement.Body, lowering);
				return [withinStatement];

			default:
				return [statement];
		}
	}

	List<Statement> RewriteIteratorDeclaration(DeclarationStatement declaration, IteratorBodyLowering lowering)
	{
		List<Statement> statements = [];
		foreach (string name in declaration.Target.Names)
		{
			if (name == "_")
				continue;

			Expression? value = RewriteIteratorExpression(declaration.InitialValue, lowering);
			string type = declaration.Target.ResolvedType ?? declaration.Target.Type?.ResolvedType ?? FormatTypeReference(declaration.Target.Type);
			value ??= new DefaultExpression { ResolvedType = type };

			statements.Add(new ExpressionStatement
			{
				SourceSyntax = declaration.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = declaration.SourceSyntax,
					Target = ThisMemberReference(name, type),
					Operator = AssignmentOperator.Assign,
					Value = value,
					ResolvedType = type
				}
			});
		}
		return statements;
	}

	Statement? RewriteIteratorOptionalStatement(Statement? statement, IteratorBodyLowering lowering)
	{
		if (statement is null)
			return null;
		List<Statement> rewritten = RewriteIteratorStatement(statement, lowering);
		return rewritten.Count == 1 ? rewritten[0] : CreateBlock(rewritten);
	}

	Expression? RewriteIteratorExpression(Expression? expression, IteratorBodyLowering lowering)
	{
		if (expression is null)
			return null;

		if (expression is NamedExpression named && named.Qualifiers.Count == 0)
		{
			if (lowering.IsLiftedName(named.Name))
				return ThisMemberReference(named.Name, lowering.GetLiftedType(named.Name));
		}

		switch (expression)
		{
			case BinaryExpression binary:
				binary.Left = RewriteIteratorExpression(binary.Left, lowering);
				binary.Right = RewriteIteratorExpression(binary.Right, lowering);
				break;
			case UnaryExpression unary:
				unary.Operand = RewriteIteratorExpression(unary.Operand, lowering);
				unary.Context = RewriteIteratorExpression(unary.Context, lowering);
				break;
			case PostfixUpdateExpression update:
				update.Expression = RewriteIteratorExpression(update.Expression, lowering);
				break;
			case ParenthesizedExpression parenthesized:
				parenthesized.Expression = RewriteIteratorExpression(parenthesized.Expression, lowering);
				break;
			case CastExpression cast:
				cast.Expression = RewriteIteratorExpression(cast.Expression, lowering);
				break;
			case AssignmentExpression assignment:
				assignment.Target = RewriteIteratorExpression(assignment.Target, lowering);
				assignment.Value = RewriteIteratorExpression(assignment.Value, lowering);
				break;
			case CallExpression call:
				call.Target = RewriteIteratorExpression(call.Target, lowering);
				foreach (ArgumentExpression argument in call.Arguments)
					argument.Value = RewriteIteratorExpression(argument.Value, lowering);
				break;
			case IndexExpression index:
				index.Target = RewriteIteratorExpression(index.Target, lowering);
				foreach (ArgumentExpression argument in index.Arguments)
					argument.Value = RewriteIteratorExpression(argument.Value, lowering);
				break;
			case MemberExpression member:
				member.Target = RewriteIteratorExpression(member.Target, lowering);
				break;
			case MemberReferenceExpression memberReference:
				memberReference.Target = RewriteIteratorExpression(memberReference.Target, lowering);
				break;
			case ConditionalExpression conditional:
				conditional.Condition = RewriteIteratorExpression(conditional.Condition, lowering);
				conditional.WhenTrue = RewriteIteratorExpression(conditional.WhenTrue, lowering);
				conditional.WhenFalse = RewriteIteratorExpression(conditional.WhenFalse, lowering);
				break;
			case ArrayExpression array:
				for (int i = 0; i < array.Elements.Count; i++)
					array.Elements[i] = RewriteIteratorExpression(array.Elements[i], lowering)!;
				break;
			case InitializerExpression initializer:
				foreach (InitializerItem item in initializer.Items)
					item.Expression = RewriteIteratorExpression(item.Expression, lowering);
				break;
		}

		return expression;
	}

	IEnumerable<DeclarationStatement> EnumerateIteratorLocalDeclarations(BlockStatement? body)
	{
		if (body is null)
			yield break;

		foreach (Statement statement in body.Statements)
			foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(statement))
				yield return declaration;
	}

	IEnumerable<DeclarationStatement> EnumerateIteratorLocalDeclarations(Statement? statement)
	{
		switch (statement)
		{
			case DeclarationStatement declaration:
				yield return declaration;
				break;
			case BlockStatement block:
				foreach (Statement child in block.Statements)
					foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(child))
						yield return declaration;
				break;
			case IfStatement ifStatement:
				foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(ifStatement.Body))
					yield return declaration;
				foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(ifStatement.ElseBody))
					yield return declaration;
				break;
			case WhileStatement whileStatement:
				foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(whileStatement.Body))
					yield return declaration;
				break;
			case DoWhileStatement doWhile:
				foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(doWhile.Body))
					yield return declaration;
				break;
			case ForStatement forStatement:
				if (forStatement.Condition.Declaration is not null)
					yield return forStatement.Condition.Declaration;
				foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(forStatement.Body))
					yield return declaration;
				break;
			case ForeachStatement foreachStatement:
				foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(foreachStatement.Body))
					yield return declaration;
				break;
			case SwitchStatement switchStatement:
				foreach (Statement child in switchStatement.Statements)
					foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(child))
						yield return declaration;
				break;
			case TryStatement tryStatement:
				foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(tryStatement.Body))
					yield return declaration;
				foreach (CatchStatement catchStatement in tryStatement.Catches)
					foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(catchStatement.Body))
						yield return declaration;
				foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(tryStatement.Finally))
					yield return declaration;
				break;
			case CatchStatement catchStatement:
				foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(catchStatement.Body))
					yield return declaration;
				break;
			case FinallyStatement finallyStatement:
				foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(finallyStatement.Body))
					yield return declaration;
				break;
			case WithinStatement withinStatement:
				foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(withinStatement.Body))
					yield return declaration;
				break;
		}
	}

	List<Statement> GetTopLevelIteratorFinallyStatements(FunctionDefinition function)
	{
		List<Statement> statements = [];
		if (function.Body is null)
			return statements;

		foreach (Statement statement in function.Body.Statements)
			if (statement is FinallyStatement { Body: not null } finallyStatement)
				statements.Add(finallyStatement.Body);
		return statements;
	}

	static IEnumerable<FieldDefinition> GetIteratorFields(TypeDefinition type)
	{
		return type switch
		{
			ClassDefinition classDefinition => classDefinition.Fields,
			StructDefinition structDefinition => structDefinition.Fields,
			_ => []
		};
	}

	MemberReferenceExpression ThisMemberReference(string name, string? resolvedType)
	{
		return new MemberReferenceExpression
		{
			Target = new ThisExpression { ResolvedType = currentIteratorStateThisType },
			Name = name,
			ResolvedType = resolvedType
		};
	}

	static void AddIteratorField(TypeDefinition type, FieldDefinition field)
	{
		switch (type)
		{
			case ClassDefinition classDefinition:
				classDefinition.Fields.Add(field);
				break;
			case StructDefinition structDefinition:
				structDefinition.Fields.Add(field);
				break;
			default:
				throw new InvalidOperationException("Iterator state type must be a class or struct.");
		}
	}

	static void AddIteratorFunction(TypeDefinition type, FunctionDefinition function)
	{
		switch (type)
		{
			case ClassDefinition classDefinition:
				classDefinition.Functions.Add(function);
				break;
			case StructDefinition structDefinition:
				structDefinition.Functions.Add(function);
				break;
			default:
				throw new InvalidOperationException("Iterator state type must be a class or struct.");
		}
	}

	static List<ParameterDefinition> GetIteratorYieldSlots(IterTypeReference iterType)
	{
		List<ParameterDefinition> slots = [];
		if (iterType.Parameters.Count == 0)
		{
			slots.Add(new ParameterDefinition
			{
				Name = "current",
				Symbol = "current",
				Type = CloneType(iterType.ElementType),
				ResolvedType = iterType.ElementType?.ResolvedType
			});
			return slots;
		}

		foreach (ParameterDefinition parameter in iterType.Parameters)
		{
			if (parameter.Modifier != ParameterModifier.Thrown)
				slots.Add(parameter);
		}
		return slots;
	}

	static ParameterDefinition? GetIteratorThrownSlot(IterTypeReference iterType)
	{
		foreach (ParameterDefinition parameter in iterType.Parameters)
			if (parameter.Modifier == ParameterModifier.Thrown)
				return parameter;
		return null;
	}

	static string GetIteratorStateTypeName(FunctionDefinition function, TypeDefinition? containingType)
	{
		string baseName = function.Name.TrimStart('~') + "Iter";
		return containingType is null ? baseName : containingType.Name + "_" + baseName;
	}

	sealed class IteratorBodyLowering
	{
		readonly BindableNodeAnalyzer analyzer;
		readonly Dictionary<string, string> liftedTypes = new(StringComparer.Ordinal);
		readonly List<Statement> cleanupStatements;
		readonly ParameterDefinition current;
		readonly string yieldedType;
		int nextState = 1;

		public IteratorBodyLowering(BindableNodeAnalyzer analyzer, FunctionDefinition function, ParameterDefinition current, string yieldedType)
		{
			this.analyzer = analyzer;
			this.current = current;
			this.yieldedType = yieldedType;
			cleanupStatements = analyzer.GetTopLevelIteratorFinallyStatements(function);
			foreach (ParameterDefinition parameter in function.Parameters)
			{
				if (!string.IsNullOrWhiteSpace(parameter.Name))
					liftedTypes[parameter.Name] = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? FormatTypeReference(parameter.Type);
			}
			foreach (DeclarationStatement declaration in analyzer.EnumerateIteratorLocalDeclarations(function.Body))
			{
				foreach (string name in declaration.Target.Names)
					if (name != "_")
						liftedTypes[name] = declaration.Target.ResolvedType ?? declaration.Target.Type?.ResolvedType ?? FormatTypeReference(declaration.Target.Type);
			}
		}

		public bool IsLiftedName(string name) => liftedTypes.ContainsKey(name);

		public string GetLiftedType(string name) => liftedTypes.TryGetValue(name, out string? type) ? type : ErrorType;

		public List<Statement> CreateResumeDispatch()
		{
			List<Statement> statements =
			[
				new IfStatement
				{
					Condition = new BinaryExpression
					{
						Left = analyzer.ThisMemberReference(IteratorStateFieldName, "int"),
						Operator = BinaryOperator.Equal,
						Right = NumberLiteral("-1", "int"),
						ResolvedType = "bool"
					},
					Body = ReturnFalse(),
					ResolvedType = "void"
				}
			];

			for (int state = 1; state < nextState; state++)
				statements.Add(CreateResumeIf(state));
			return statements;
		}

		public List<Statement> CreateYield(Expression? value)
		{
			int resumeState = nextState++;
			return
			[
				new ExpressionStatement
				{
					ResolvedType = "void",
					Expression = new AssignmentExpression
					{
						Target = new UnaryExpression
						{
							Operator = UnaryOperator.PointerDereference,
							Operand = CreateVariableReference(current, current.ResolvedType ?? ErrorType),
							ResolvedType = yieldedType
						},
						Operator = AssignmentOperator.Assign,
						Value = analyzer.RewriteIteratorExpression(value, this),
						ResolvedType = yieldedType
					}
				},
				SetState(resumeState),
				new ReturnStatement
				{
					Expression = BoolLiteral(true),
					ResolvedType = "void"
				},
				new LabelStatement { Name = ResumeLabel(resumeState), ResolvedType = "void" },
				new EmptyStatement { ResolvedType = "void" }
			];
		}

		public List<Statement> CreateCompletion()
		{
			List<Statement> statements = [];
			foreach (Statement cleanup in cleanupStatements)
				statements.Add(analyzer.CloneStatementForCleanup(cleanup));
			statements.Add(SetState(-1));
			statements.Add(ReturnFalse());
			return statements;
		}

		Statement CreateResumeIf(int state)
		{
			return new IfStatement
			{
				Condition = new BinaryExpression
				{
					Left = analyzer.ThisMemberReference(IteratorStateFieldName, "int"),
					Operator = BinaryOperator.Equal,
					Right = NumberLiteral(state.ToString(System.Globalization.CultureInfo.InvariantCulture), "int"),
					ResolvedType = "bool"
				},
				Body = new GotoStatement { TargetName = ResumeLabel(state), ResolvedType = "void" },
				ResolvedType = "void"
			};
		}

		Statement SetState(int state)
		{
			return new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					Target = analyzer.ThisMemberReference(IteratorStateFieldName, "int"),
					Operator = AssignmentOperator.Assign,
					Value = NumberLiteral(state.ToString(System.Globalization.CultureInfo.InvariantCulture), "int"),
					ResolvedType = "int"
				}
			};
		}

		static ReturnStatement ReturnFalse()
		{
			return new ReturnStatement
			{
				Expression = BoolLiteral(false),
				ResolvedType = "void"
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

		static string ResumeLabel(int state) => $"__iter_resume{state}";
	}
}
