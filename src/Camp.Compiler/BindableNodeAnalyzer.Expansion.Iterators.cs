using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	const string IteratorStateFieldName = "__state";

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
		AddIteratorNextMethod(state, function, iterType);
		AddIteratorDestructor(state);
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

	void AddIteratorNextMethod(TypeDefinition state, FunctionDefinition function, IterTypeReference iterType)
	{
		FunctionDefinition next = new()
		{
			SourceSyntax = function.SourceSyntax,
			Name = "next",
			Symbol = $"{state.Name}_next",
			Export = function.Export,
			ReturnType = new PrimitiveTypeReference { Type = PrimitiveType.Bool, ResolvedType = "bool" },
			ResolvedType = "bool",
			Body = CreateIteratorNextBody(function, iterType, state)
		};

		foreach (ParameterDefinition slot in GetIteratorYieldSlots(iterType))
		{
			string slotName = string.IsNullOrWhiteSpace(slot.Name) ? "current" : slot.Name;
			next.Parameters.Add(new ParameterDefinition
			{
				SourceSyntax = slot.SourceSyntax,
				Name = slotName,
				Symbol = slotName,
				Modifier = ParameterModifier.Out,
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

		AddIteratorFunction(state, next);
	}

	void AddIteratorDestructor(TypeDefinition state)
	{
		AddIteratorFunction(state, new FunctionDefinition
		{
			Name = "destroy",
			Symbol = $"{state.Name}_destroy",
			Export = state.Export,
			ReturnType = VoidType(),
			ResolvedType = "void",
			Body = new BlockStatement { ResolvedType = "void" }
		});
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
					ResolvedType = parameter.ResolvedType
				},
				ResolvedType = parameter.ResolvedType
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
						ResolvedType = item.ResolvedType
					},
					Value = item.Expression,
					ResolvedType = item.ResolvedType
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

	BlockStatement CreateIteratorNextBody(FunctionDefinition function, IterTypeReference iterType, TypeDefinition state)
	{
		YieldStatement? yield = TryGetSingleYield(function.Body);
		if (yield is null || GetIteratorYieldSlots(iterType).Count != 1)
		{
			Report(GetRange(function.Body?.SourceSyntax ?? function.SourceSyntax), "Iterator generator lowering currently supports only a single yield statement with one yielded slot.");
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

		ParameterDefinition current = new()
		{
			Name = "current",
			Symbol = "current",
			Modifier = ParameterModifier.Out,
			Type = PointerTo(CloneType(GetIteratorYieldSlots(iterType)[0].Type) ?? VoidType()),
			ResolvedType = $"{GetIteratorYieldSlots(iterType)[0].ResolvedType ?? ErrorType}*"
		};
		Expression stateField = ThisMemberReference(IteratorStateFieldName, "int");
		Expression currentTarget = new UnaryExpression
		{
			Operator = UnaryOperator.PointerDereference,
			Operand = CreateVariableReference(current, current.ResolvedType ?? ErrorType),
			ResolvedType = GetIteratorYieldSlots(iterType)[0].ResolvedType
		};
		return new BlockStatement
		{
			ResolvedType = "void",
			Statements =
			{
				new IfStatement
				{
					Condition = new BinaryExpression
					{
						Left = stateField,
						Operator = BinaryOperator.NotEqual,
						Right = NumberLiteral("0", "int"),
						ResolvedType = "bool"
					},
					Body = new ReturnStatement
					{
						Expression = new LiteralExpression { Kind = LiteralKind.False, Text = "false", Value = false, ResolvedType = "bool" },
						ResolvedType = "void"
					},
					ResolvedType = "void"
				},
				new ExpressionStatement
				{
					Expression = new AssignmentExpression
					{
						Target = currentTarget,
						Operator = AssignmentOperator.Assign,
						Value = RewriteIteratorYieldExpression(yield.Expression, function.Parameters),
						ResolvedType = GetIteratorYieldSlots(iterType)[0].ResolvedType
					},
					ResolvedType = "void"
				},
				new ExpressionStatement
				{
					Expression = new AssignmentExpression
					{
						Target = ThisMemberReference(IteratorStateFieldName, "int"),
						Operator = AssignmentOperator.Assign,
						Value = NumberLiteral("1", "int"),
						ResolvedType = "int"
					},
					ResolvedType = "void"
				},
				new ReturnStatement
				{
					Expression = new LiteralExpression { Kind = LiteralKind.True, Text = "true", Value = true, ResolvedType = "bool" },
					ResolvedType = "void"
				}
			}
		};
	}

	YieldStatement? TryGetSingleYield(BlockStatement? body)
	{
		if (body?.Statements.Count != 1 || body.Statements[0] is not YieldStatement yield)
			return null;
		return yield;
	}

	Expression? RewriteIteratorYieldExpression(Expression? expression, List<ParameterDefinition> parameters)
	{
		if (expression is null)
			return null;

		if (expression is NamedExpression named && named.Qualifiers.Count == 0)
		{
			foreach (ParameterDefinition parameter in parameters)
			{
				if (parameter.Name == named.Name)
					return ThisMemberReference(parameter.Name, parameter.ResolvedType ?? ErrorType);
			}
		}

		switch (expression)
		{
			case BinaryExpression binary:
				binary.Left = RewriteIteratorYieldExpression(binary.Left, parameters);
				binary.Right = RewriteIteratorYieldExpression(binary.Right, parameters);
				break;
			case UnaryExpression unary:
				unary.Operand = RewriteIteratorYieldExpression(unary.Operand, parameters);
				break;
			case ParenthesizedExpression parenthesized:
				parenthesized.Expression = RewriteIteratorYieldExpression(parenthesized.Expression, parameters);
				break;
			case CastExpression cast:
				cast.Expression = RewriteIteratorYieldExpression(cast.Expression, parameters);
				break;
		}

		return expression;
	}

	MemberReferenceExpression ThisMemberReference(string name, string? resolvedType)
	{
		return new MemberReferenceExpression
		{
			Target = new ThisExpression(),
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
}
