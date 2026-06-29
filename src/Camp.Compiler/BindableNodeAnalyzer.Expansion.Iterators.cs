using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	const string IteratorStateFieldName = "__state";
	string? currentIteratorStateThisType;
	readonly HashSet<FunctionDefinition> generatedIteratorFactories = [];
	int iteratorForeachStateIndex;
	readonly Dictionary<ForeachStatement, IteratorForeachStateFields> iteratorForeachStates = [];
	readonly Dictionary<string, Dictionary<string, FieldDefinition>> iteratorStateFields = new(StringComparer.Ordinal);
	readonly Dictionary<string, Dictionary<string, FieldDefinition>> iteratorStateFieldsBySourceName = new(StringComparer.Ordinal);

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

		AnalyzeIteratorGeneratorReturnType(function, containingType);
		if (function.ReturnType is not IterTypeReference iterType)
		{
			Report(GetRange(function.SourceSyntax), "Iterator generator return type must be an iter type.");
			return;
		}

		EnsureIteratorSizeOfParameters(function);

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
			? CreateIteratorClass(function, containingType, iterType, stateName)
			: CreateIteratorStruct(function, containingType, iterType, stateName);
		module.Definitions.Add(stateType);
		typeDefinitions[stateType.Name] = stateType;
		typeInfos[stateType] = new TypeAnalysisInfo(stateType);

		function.IteratorKind = IteratorKind.None;
		TypeReference stateReference = CreateIteratorStateReference(stateType, function, containingType);
		function.ReturnType = iteratorKind == IteratorKind.Class
			? PointerTo(stateReference)
			: stateReference;
		function.ResolvedType = function.ReturnType.ResolvedType;
		function.Body = CreateIteratorFactoryBody(function, stateType, stateReference);
		generatedIteratorFactories.Add(function);
	}

	void EnsureIteratorSizeOfParameters(FunctionDefinition function)
	{
		foreach (GenericParameter parameter in function.GenericParameters)
		{
			if (parameter.Constraint is not AnyTypeReference and not CopyableTypeReference)
				continue;
			if (FindSizeOfParameter(function, parameter.Name) is not null)
				continue;

			GenericParameterTypeReference type = new()
			{
				SourceSyntax = parameter.SourceSyntax,
				Name = parameter.Name,
				Parameter = parameter,
				ResolvedType = parameter.Name
			};
			string name = SizeOfParameterName(type);
			function.Parameters.Add(new SizeOfParameterDefinition
			{
				SourceSyntax = parameter.SourceSyntax,
				Name = name,
				Symbol = name,
				Type = type,
				ResolvedType = "nuint"
			});
		}
	}

	void AnalyzeIteratorGeneratorReturnType(FunctionDefinition function, TypeDefinition? containingType)
	{
		if (function.ReturnType is null)
			return;

		AnalysisScope scope = new();
		if (containingType is not null)
			foreach (GenericParameter parameter in containingType.GenericParameters)
				scope.GenericParameters[parameter.Name] = parameter;
		foreach (GenericParameter parameter in function.GenericParameters)
			scope.GenericParameters[parameter.Name] = parameter;
		AnalyzeType(function.ReturnType, scope);
	}

	ClassDefinition CreateIteratorClass(FunctionDefinition function, TypeDefinition? containingType, IterTypeReference iterType, string stateName)
	{
		ClassDefinition state = new()
		{
			SourceSyntax = function.SourceSyntax,
			Name = stateName,
			Symbol = stateName,
			Export = function.Export,
			Public = function.Public,
			ResolvedType = stateName
		};
		AddIteratorGenericParameters(state, function, containingType);
		AddIteratorStateMembers(state, function, iterType);
		return state;
	}

	StructDefinition CreateIteratorStruct(FunctionDefinition function, TypeDefinition? containingType, IterTypeReference iterType, string stateName)
	{
		StructDefinition state = new()
		{
			SourceSyntax = function.SourceSyntax,
			Name = stateName,
			Symbol = stateName,
			Export = function.Export,
			Public = function.Public,
			Modifier = StructModifier.Fixed,
			ResolvedType = stateName
		};
		AddIteratorGenericParameters(state, function, containingType);
		AddIteratorStateMembers(state, function, iterType);
		return state;
	}

	static void AddIteratorGenericParameters(TypeDefinition state, FunctionDefinition function, TypeDefinition? containingType)
	{
		if (containingType is not null)
			foreach (GenericParameter parameter in containingType.GenericParameters)
				state.GenericParameters.Add(parameter);
		foreach (GenericParameter parameter in function.GenericParameters)
			state.GenericParameters.Add(parameter);
	}

	TypeReference CreateIteratorStateReference(TypeDefinition state, FunctionDefinition function, TypeDefinition? containingType)
	{
		TypeDefinitionReference reference = new()
		{
			Name = state.Name,
			Definition = state,
			ResolvedType = state.Name
		};
		if (containingType is not null)
			foreach (GenericParameter parameter in containingType.GenericParameters)
				reference.TypeArguments.Add(new GenericParameterTypeReference { Name = parameter.Name, Parameter = parameter, ResolvedType = parameter.Name });
		foreach (GenericParameter parameter in function.GenericParameters)
			reference.TypeArguments.Add(new GenericParameterTypeReference { Name = parameter.Name, Parameter = parameter, ResolvedType = parameter.Name });
		reference.ResolvedType = AddTypeArguments(state.Name, reference.TypeArguments);
		return reference;
	}

	void AddIteratorStateMembers(TypeDefinition state, FunctionDefinition function, IterTypeReference iterType)
	{
		AddIteratorStateFields(state, function);
		AddIteratorLiftedLocalFields(state, function);
		AddIteratorNextMethod(state, function, iterType);
		AddIteratorDestructor(state, function);
		AddIteratorProtocolAdapter(state, iterType);
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
			string sourceName = IteratorStateFieldSourceName(parameter);
			string fieldName = IteratorStateFieldNameFor(parameter);
			string parameterType = IteratorStateFieldType(parameter);

			AddIteratorField(state, new FieldDefinition
			{
				SourceSyntax = parameter.SourceSyntax,
				Name = fieldName,
				Symbol = fieldName,
				Type = IteratorStateFieldTypeReference(parameter, parameterType),
				ResolvedType = parameterType
			}, sourceName);
		}
	}

	string IteratorStateFieldSourceName(ParameterDefinition parameter)
	{
		return parameter switch
		{
			SizeOfParameterDefinition sizeOf => SizeOfParameterName(sizeOf.Type),
			VTableOfParameterDefinition vtableOf => VTableOfParameterName(vtableOf.Type, vtableOf.InterfaceType),
			_ => parameter.Name
		};
	}

	string IteratorStateFieldNameFor(ParameterDefinition parameter)
	{
		string sourceName = IteratorStateFieldSourceName(parameter);
		if (parameter is ThisParameterDefinition)
			return "__iter_this";
		if (parameter is SizeOfParameterDefinition)
			return "_" + sourceName;
		if (parameter is VTableOfParameterDefinition)
			return "_" + sourceName;
		return IsReservedGeneratedFieldName(sourceName) ? "__iter_" + sourceName : sourceName;
	}

	static bool IsReservedGeneratedFieldName(string name)
	{
		return string.IsNullOrWhiteSpace(name)
			|| ReservedWords.Contains(name)
			|| CReservedWords.Contains(name);
	}

	string IteratorStateFieldType(ParameterDefinition parameter)
	{
		string resolvedType = ResolvedTypeForIteratorExpansion(parameter.Type, parameter.ResolvedType);
		return parameter switch
		{
			SizeOfParameterDefinition => "nuint",
			VTableOfParameterDefinition vtableOf => VTableOfParameterType(vtableOf),
			_ => ContainsLifetimeAnnotation(parameter.Type) ? StripLifetimeQualifiers(resolvedType) : resolvedType
		};
	}

	TypeReference IteratorStateFieldTypeReference(ParameterDefinition parameter, string resolvedType)
	{
		return parameter switch
		{
			SizeOfParameterDefinition => NuintType(),
			VTableOfParameterDefinition => TypeReferenceForResolvedName(resolvedType),
			_ when ContainsLifetimeAnnotation(parameter.Type) => TypeReferenceForIteratorField(resolvedType),
			_ => CloneType(parameter.Type) ?? TypeReferenceForResolvedName(resolvedType)
		};
	}

	void AddIteratorLiftedLocalFields(TypeDefinition state, FunctionDefinition function)
	{
		HashSet<string> names = new(StringComparer.Ordinal);
		foreach (FieldDefinition field in GetIteratorFields(state))
			if (!string.IsNullOrWhiteSpace(field.Name))
				names.Add(field.Name);

		foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(function.Body))
		{
			if (declaration.InitialValue is ConstructionExpression { Kind: ConstructionKind.Init, ElementCount: not null } initArray)
				Report(GetRange(initArray.SourceSyntax ?? declaration.InitialValue.SourceSyntax ?? declaration.SourceSyntax), "Iterator generator bodies cannot use init array construction; use fixed storage or new instead.");
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
					IsFixedStorage = declaration.IsFixedStorage,
					Type = CloneType(declaration.Target.Type),
					ResolvedType = declaration.Target.ResolvedType ?? declaration.Target.Type?.ResolvedType ?? FormatTypeReference(declaration.Target.Type)
				});
			}
		}

		foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(function.Body))
		{
			if (!TryCreateIteratorForeachState(function, foreachStatement, out IteratorForeachStateFields? fields) || fields is null)
				continue;

			iteratorForeachStates[foreachStatement] = fields;
			AddIteratorField(state, new FieldDefinition
			{
				SourceSyntax = foreachStatement.SourceSyntax,
				Name = fields.IteratorFieldName,
				Symbol = fields.IteratorFieldName,
				Type = TypeReferenceForIteratorField(fields.IteratorType),
				ResolvedType = fields.IteratorType
			});
			if (fields is { IsArray: true, LengthFieldName: not null, IndexFieldName: not null })
			{
				AddIteratorField(state, new FieldDefinition
				{
					SourceSyntax = foreachStatement.SourceSyntax,
					Name = fields.LengthFieldName,
					Symbol = fields.LengthFieldName,
					Type = NuintType(),
					ResolvedType = "nuint"
				});
				AddIteratorField(state, new FieldDefinition
				{
					SourceSyntax = foreachStatement.SourceSyntax,
					Name = fields.IndexFieldName,
					Symbol = fields.IndexFieldName,
					Type = NuintType(),
					ResolvedType = "nuint"
				});
				continue;
			}
			if (fields is { IsProtocol: true, ContextFieldName: not null })
			{
				AddIteratorField(state, new FieldDefinition
				{
					SourceSyntax = foreachStatement.SourceSyntax,
					Name = fields.ContextFieldName,
					Symbol = fields.ContextFieldName,
					Type = PointerTo(VoidType()),
					ResolvedType = "void*"
				});
			}
			AddIteratorField(state, new FieldDefinition
			{
				SourceSyntax = foreachStatement.SourceSyntax,
				Name = fields.CurrentFieldName,
				Symbol = fields.CurrentFieldName,
				Type = TypeReferenceForResolvedName(fields.ElementType),
				ResolvedType = fields.ElementType
			});
		}
	}

	bool TryCreateIteratorForeachState(FunctionDefinition function, ForeachStatement foreachStatement, out IteratorForeachStateFields? fields)
	{
		fields = null;
		if (TryCreateIteratorProtocolForeachState(function, foreachStatement, out fields))
			return true;
		string arraySourceType = ResolveIteratorProtocolForeachSourceType(function, foreachStatement.Source);
		if (TryGetArrayElementType(arraySourceType) is string arrayElementType)
		{
			int arrayIndex = iteratorForeachStateIndex++;
			fields = IteratorForeachStateFields.ForArray(
				$"__foreachElements{arrayIndex}",
				$"__foreachLength{arrayIndex}",
				$"__foreachIndex{arrayIndex}",
				AddPointer(arrayElementType),
				arrayElementType);
			return true;
		}
		if (!TryResolveIteratorForeachSourceType(foreachStatement.Source, out string sourceType))
			return false;
		if (!TryFindIteratorNextMethod(sourceType, out FunctionDefinition? next, out string elementType) || next is null)
			return false;

		int index = iteratorForeachStateIndex++;
		fields = new IteratorForeachStateFields($"__foreachIter{index}", $"__foreachCurrent{index}", sourceType, elementType);
		return true;
	}

	bool TryCreateIteratorProtocolForeachState(FunctionDefinition function, ForeachStatement foreachStatement, out IteratorForeachStateFields? fields)
	{
		fields = null;
		string sourceType = ResolveIteratorProtocolForeachSourceType(function, foreachStatement.Source);
		if (!TryGetIteratorProtocolCurrentTypes(sourceType, out List<string>? currentTypes) || currentTypes is not { Count: 1 })
			return false;

		string elementType = foreachStatement.Target.ResolvedType ?? currentTypes[0];
		string callType = BuildCallableType("fn", "bool", ["void*", AddPointer(elementType)]);
		int index = iteratorForeachStateIndex++;
		fields = IteratorForeachStateFields.ForProtocol(
			$"__foreachIterCall{index}",
			$"__foreachIterContext{index}",
			$"__foreachCurrent{index}",
			callType,
			elementType);
		return true;
	}

	static string ResolveIteratorProtocolForeachSourceType(FunctionDefinition function, Expression? source)
	{
		string? sourceType = source?.ResolvedType;
		if (!string.IsNullOrWhiteSpace(sourceType) && sourceType != UnresolvedType && sourceType != ErrorType)
			return sourceType;

		if (source is ThisExpression)
		{
			foreach (ParameterDefinition parameter in function.Parameters)
			{
				if (parameter is ThisParameterDefinition || parameter.Name == "this")
					return parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? FormatTypeReference(parameter.Type);
			}
		}

		if (source is NamedExpression { Qualifiers.Count: 0 } named)
		{
			foreach (ParameterDefinition parameter in function.Parameters)
			{
				if (parameter.Name == named.Name)
					return parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? FormatTypeReference(parameter.Type);
			}
		}

		return sourceType ?? ErrorType;
	}

	bool TryResolveIteratorForeachSourceType(Expression? source, out string sourceType)
	{
		sourceType = ErrorType;
		if (source is CallExpression { Target: NamedExpression { Qualifiers.Count: 0 } named })
		{
			foreach (Definition definition in currentModule?.Definitions ?? [])
			{
				if (definition is FunctionDefinition function && function.Name == named.Name)
				{
					sourceType = function.ResolvedType ?? function.ReturnType?.ResolvedType ?? ErrorType;
					return sourceType != ErrorType;
				}
			}
		}
		return false;
	}

	TypeReference TypeReferenceForIteratorField(string type)
	{
		if (TryGetCallableShape(type, out CallableShape callable))
		{
			CallableTypeReference reference = new()
			{
				Kind = callable.Kind switch
				{
					"delegate" => CallableKind.Delegate,
					"once" => CallableKind.Once,
					"async" => CallableKind.Async,
					_ => CallableKind.Function
				},
				ReturnType = TypeReferenceForIteratorField(callable.ReturnType),
				ResolvedType = type
			};
			for (int i = 0; i < callable.Parameters.Count; i++)
			{
				string parameterType = callable.Parameters[i];
				ParameterModifier modifier = ParameterModifier.None;
				if (parameterType.StartsWith("in ", StringComparison.Ordinal))
				{
					modifier = ParameterModifier.In;
					parameterType = parameterType[3..].TrimStart();
				}
				else if (parameterType.StartsWith("out ", StringComparison.Ordinal))
				{
					modifier = ParameterModifier.Out;
					parameterType = parameterType[4..].TrimStart();
				}
				else if (parameterType.StartsWith("thrown ", StringComparison.Ordinal))
				{
					modifier = ParameterModifier.Thrown;
					parameterType = parameterType[7..].TrimStart();
				}
				else if (parameterType.StartsWith("within ", StringComparison.Ordinal))
				{
					modifier = ParameterModifier.Within;
					parameterType = parameterType[7..].TrimStart();
				}
				reference.Parameters.Add(new ParameterDefinition
				{
					Name = "arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
					Symbol = "arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
					Modifier = modifier,
					Type = TypeReferenceForIteratorField(parameterType),
					ResolvedType = parameterType
				});
			}
			return reference;
		}
		if (TryGetPointerElementType(type) is string elementType)
		{
			TypeReference pointer = PointerTo(TypeReferenceForResolvedName(elementType));
			pointer.ResolvedType = type;
			return pointer;
		}
		return TypeReferenceForResolvedName(type);
	}

	void AddIteratorNextMethod(TypeDefinition state, FunctionDefinition function, IterTypeReference iterType)
	{
		FunctionDefinition next = new()
		{
			SourceSyntax = function.SourceSyntax,
			Name = "next",
			Symbol = $"{state.Name}_next",
			Export = function.Export,
			Public = function.Public,
			ReturnType = new PrimitiveTypeReference { Type = PrimitiveType.Bool, ResolvedType = "bool" },
			ResolvedType = "bool"
		};

		foreach (ParameterDefinition slot in GetIteratorYieldSlots(iterType))
		{
			string slotName = string.IsNullOrWhiteSpace(slot.Name) ? "current" : slot.Name;
			string slotType = slot.ResolvedType ?? slot.Type?.ResolvedType ?? FormatTypeReference(slot.Type);
			next.Parameters.Add(new ParameterDefinition
			{
				SourceSyntax = slot.SourceSyntax,
				Name = slotName,
				Symbol = slotName,
				Type = PointerTo(CloneType(slot.Type) ?? VoidType()),
				ResolvedType = $"{slotType}*"
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
		string? previousIteratorStateThisType = currentIteratorStateThisType;
		currentIteratorStateThisType = $"{state.Name}*";
		FunctionDefinition opDelete = new()
		{
			Name = DeleteMethodName,
			Symbol = $"{state.Name}_{DeleteMethodName}",
			Export = state.Export,
			Public = state.Public,
			ReturnType = VoidType(),
			ResolvedType = "void",
			Body = new BlockStatement { ResolvedType = "void" }
		};
		BlockStatement cleanupBody = new() { ResolvedType = "void" };
		foreach (IteratorForeachStateFields fields in GetIteratorForeachStateFields(sourceFunction))
			cleanupBody.Statements.Add(CreateIteratorForeachCleanup(sourceFunction, fields));
		IteratorBodyLowering cleanupLowering = new(this, sourceFunction, new ParameterDefinition { Name = "current", Symbol = "current", ResolvedType = "void*" }, ErrorType);
		foreach (Statement cleanup in GetTopLevelIteratorFinallyStatements(sourceFunction))
		{
			foreach (Statement rewrittenCleanup in RewriteIteratorStatement(CloneStatementForCleanup(cleanup), cleanupLowering))
				cleanupBody.Statements.Add(rewrittenCleanup);
		}
		cleanupBody.Statements.Add(SetIteratorStateExpression(-1));
		opDelete.Body.Statements.Add(new IfStatement
		{
			ResolvedType = "void",
			Condition = new BinaryExpression
			{
				Left = ThisMemberReference(IteratorStateFieldName, "int"),
				Operator = BinaryOperator.NotEqual,
				Right = NumberLiteral("-1", "int"),
				ResolvedType = "bool"
			},
			Body = cleanupBody
		});
		AddIteratorFunction(state, opDelete);

		FunctionDefinition destroy = new()
		{
			Name = "destroy",
			Symbol = $"{state.Name}_destroy",
			Export = state.Export,
			Public = state.Public,
			ReturnType = VoidType(),
			ResolvedType = "void",
			Body = new BlockStatement
			{
				ResolvedType = "void",
				Statements =
				{
					new ExpressionStatement
					{
						ResolvedType = "void",
						Expression = CreateIteratorInstanceCall(opDelete)
					}
				}
			}
		};
		AddIteratorFunction(state, destroy);
		currentIteratorStateThisType = previousIteratorStateThisType;
	}

	Statement CreateIteratorForeachCleanup(FunctionDefinition sourceFunction, IteratorForeachStateFields fields)
	{
		if (fields.IsArray)
			return new BlockStatement { SourceSyntax = sourceFunction.SourceSyntax, ResolvedType = "void" };

		if (fields is { IsProtocol: true, ContextFieldName: not null })
		{
			return new ExpressionStatement
			{
				SourceSyntax = sourceFunction.SourceSyntax,
				ResolvedType = "void",
				Expression = new CallExpression
				{
					SourceSyntax = sourceFunction.SourceSyntax,
					Target = ThisMemberReference(fields.IteratorFieldName, fields.IteratorType),
					ResolvedType = "bool",
					Arguments =
					{
						new ArgumentExpression
						{
							SourceSyntax = sourceFunction.SourceSyntax,
							Value = ThisMemberReference(fields.ContextFieldName, "void*"),
							ResolvedType = "void*"
						},
						new ArgumentExpression
						{
							SourceSyntax = sourceFunction.SourceSyntax,
							Value = NullLiteral(sourceFunction.SourceSyntax),
							ResolvedType = "#NULL"
						}
					}
				}
			};
		}

		return new DeleteStatement
		{
			SourceSyntax = sourceFunction.SourceSyntax,
			ResolvedType = "void",
			Expression = ThisMemberReference(fields.IteratorFieldName, fields.IteratorType)
		};
	}

	void AddIteratorProtocolAdapter(TypeDefinition state, IterTypeReference iterType)
	{
		List<ParameterDefinition> slots = GetIteratorYieldSlots(iterType);
		if (slots.Count != 1)
			return;

		string slotType = slots[0].ResolvedType ?? slots[0].Type?.ResolvedType ?? FormatTypeReference(slots[0].Type);
		FunctionDefinition? next = null;
		FunctionDefinition? opDelete = null;
		foreach (FunctionDefinition function in GetFunctions(state))
		{
			if (function.Name == "next")
				next = function;
			else if (function.Name == DeleteMethodName)
				opDelete = function;
		}
		if (next is null || opDelete is null)
			return;

		ParameterDefinition context = new()
		{
			Name = "ctx",
			Symbol = "ctx",
			Type = PointerTo(VoidType()),
			ResolvedType = "void*"
		};
		List<ParameterDefinition> currentParameters = CreateIteratorProtocolCurrentParameters(slots[0], slotType);
		ParameterDefinition currentArgument = CreateIteratorProtocolCurrentArgument(slots[0], slotType, currentParameters);
		TypeReference statePointerType = PointerTo(TypeReferenceFor(state));
		string statePointerResolvedType = AddPointer(state.Name);
		DeclarationStatement stateLocal = CreateGeneratedLocal("state", statePointerResolvedType, statePointerType, new CastExpression
		{
			Type = CloneType(statePointerType),
			Expression = CreateVariableReference(context, "void*"),
			ResolvedType = statePointerResolvedType
		});
		Expression stateReference = CreateVariableReference(stateLocal.Target, statePointerResolvedType);
		FunctionDefinition adapter = new()
		{
			Name = "op_iter",
			Symbol = $"{state.Name}_iter",
			Modifier = FunctionModifier.Static,
			Export = state.Export,
			Public = state.Public,
			ReturnType = new PrimitiveTypeReference { Type = PrimitiveType.Bool, ResolvedType = "bool" },
			ResolvedType = "bool",
			Body = new BlockStatement
			{
				ResolvedType = "void",
				Statements =
				{
					stateLocal,
					new IfStatement
					{
						ResolvedType = "void",
						Condition = new BinaryExpression
						{
							Left = CreateVariableReference(currentParameters[0], currentParameters[0].ResolvedType ?? ErrorType),
							Operator = BinaryOperator.Equal,
							Right = NullLiteral(),
							ResolvedType = "bool"
						},
						Body = new BlockStatement
						{
							ResolvedType = "void",
							Statements =
							{
								CreateIteratorAdapterCleanup(state, stateReference, opDelete),
								new ReturnStatement
								{
									Expression = new LiteralExpression { Kind = LiteralKind.False, Text = "false", Value = false, ResolvedType = "bool" },
									ResolvedType = "void"
								}
							}
						}
					},
					new ReturnStatement
					{
						ResolvedType = "void",
						Expression = new CallExpression
						{
							Target = new MemberReferenceExpression
							{
								Target = CreateVariableReference(stateLocal.Target, statePointerResolvedType),
								Name = "next",
								Member = next,
								ResolvedType = BuildFunctionValueType(next, isInstance: true)
							},
							Arguments =
							{
								new ArgumentExpression
								{
									Value = CreateVariableReference(currentArgument, currentArgument.ResolvedType ?? ErrorType),
									ResolvedType = currentArgument.ResolvedType
								}
							},
							ResolvedType = "bool"
						}
					}
				}
			}
		};
		adapter.Parameters.Add(context);
		adapter.Parameters.AddRange(currentParameters);
		AddIteratorFunction(state, adapter);
	}

	ParameterDefinition CreateIteratorProtocolCurrentArgument(ParameterDefinition slot, string slotType, List<ParameterDefinition> currentParameters)
	{
		ParameterDefinition current = new()
		{
			SourceSyntax = slot.SourceSyntax,
			Name = "current",
			Symbol = "current",
			Type = PointerTo(CloneType(slot.Type) ?? TypeReferenceForResolvedName(slotType)),
			ResolvedType = AddPointer(slotType)
		};
		if (TryGetParamsComponentShape(current.Type, current.ResolvedType, current.Name, out ParamsComponentShape shape) && shape.Components.Count == currentParameters.Count)
			RegisterParamsExpansion(current, shape, currentParameters);
		return current;
	}

	List<ParameterDefinition> CreateIteratorProtocolCurrentParameters(ParameterDefinition slot, string slotType)
	{
		List<ParameterDefinition> parameters = [];
		if (TryGetParamsComponentShape(slot.Type, slot.ResolvedType ?? slotType, "current", out ParamsComponentShape shape) && shape.Components.Count > 1)
		{
			foreach (ParamsComponent component in shape.Components)
			{
				parameters.Add(new ParameterDefinition
				{
					SourceSyntax = slot.SourceSyntax,
					Name = component.ExpandedName,
					Symbol = component.ExpandedName,
					Type = PointerTo(TypeReferenceForResolvedName(component.Type)),
					ResolvedType = AddPointer(component.Type)
				});
			}
			return parameters;
		}

		parameters.Add(new ParameterDefinition
		{
			SourceSyntax = slot.SourceSyntax,
			Name = "current",
			Symbol = "current",
			Type = PointerTo(CloneType(slot.Type) ?? TypeReferenceForResolvedName(slotType)),
			ResolvedType = AddPointer(slotType)
		});
		return parameters;
	}

	Statement CreateIteratorAdapterCleanup(TypeDefinition state, Expression stateReference, FunctionDefinition opDelete)
	{
		if (state is ClassDefinition)
		{
			return new DeleteStatement
			{
				ResolvedType = "void",
				Expression = stateReference
			};
		}

		return new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = new CallExpression
			{
				Target = new MemberReferenceExpression
				{
					Target = stateReference,
					Name = DeleteMethodName,
					Member = opDelete,
					ResolvedType = BuildFunctionValueType(opDelete, isInstance: true)
				},
				ResolvedType = "void"
			}
		};
	}

	ExpressionStatement SetIteratorStateExpression(int state)
	{
		return new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				Target = ThisMemberReference(IteratorStateFieldName, "int"),
				Operator = AssignmentOperator.Assign,
				Value = NumberLiteral(state.ToString(System.Globalization.CultureInfo.InvariantCulture), "int"),
				ResolvedType = "int"
			}
		};
	}

	CallExpression CreateIteratorInstanceCall(FunctionDefinition function)
	{
		return new CallExpression
		{
			ResolvedType = function.ResolvedType ?? "void",
			Target = new MemberReferenceExpression
			{
				Target = new ThisExpression { ResolvedType = currentIteratorStateThisType },
				Name = function.Name,
				Member = function,
				ResolvedType = BuildFunctionValueType(function, isInstance: true)
			}
		};
	}

	BlockStatement CreateIteratorFactoryBody(FunctionDefinition function, TypeDefinition stateType, TypeReference stateReference)
	{
		string stateResolvedType = stateReference.ResolvedType ?? stateType.Name;
		InitializerExpression initializer = new() { ResolvedType = stateResolvedType };
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
			string sourceName = IteratorStateFieldSourceName(parameter);
			string parameterType = IteratorStateFieldType(parameter);
			FieldDefinition? field = TryGetIteratorStateField(stateType, sourceName);

			initializer.Items.Add(new InitializerItem
			{
				Target = InitializerTargetFor(field?.Name ?? IteratorStateFieldNameFor(parameter)),
				Expression = new VariableReferenceExpression
				{
					SourceSyntax = parameter.SourceSyntax,
					Variable = parameter,
					ResolvedType = parameterType
				},
				ResolvedType = parameterType
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
		DeclarationStatement local = CreateGeneratedLocal(localName, $"{stateResolvedType}*", PointerTo(CloneType(stateReference) ?? TypeReferenceFor(stateType)), new ConstructionExpression
		{
			SourceSyntax = function.SourceSyntax,
			Kind = ConstructionKind.New,
			Type = CloneType(stateReference),
			ResolvedType = $"{stateResolvedType}*"
		});
		Expression localReference = CreateVariableReference(local.Target, local.Target.ResolvedType ?? $"{stateResolvedType}*");
		BlockStatement body = new() { ResolvedType = "void" };
		body.Statements.Add(local);
		BlockStatement guardBody = new() { ResolvedType = "void" };
		foreach (InitializerItem item in initializer.Items)
		{
			guardBody.Statements.Add(new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					Operator = AssignmentOperator.Assign,
					Target = new MemberReferenceExpression
					{
						Target = localReference,
						Name = item.Target?.Parts.Count > 0 ? item.Target.Parts[0].Name ?? "" : "",
						Member = TryGetIteratorStateField(stateType, item.Target?.Parts.Count > 0 ? item.Target.Parts[0].Name ?? "" : ""),
						ResolvedType = item.ResolvedType ?? ErrorType
					},
					Value = item.Expression,
					ResolvedType = item.ResolvedType ?? ErrorType
				}
			});
		}
		body.Statements.Add(CreateNotNullGuard(localReference, guardBody, function.SourceSyntax));
		body.Statements.Add(new ReturnStatement
		{
			Expression = CreateVariableReference(local.Target, local.Target.ResolvedType ?? $"{stateResolvedType}*"),
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
				ValidateIteratorYieldLifetime(lowering.Function, yield.Expression, yield.Expression?.SourceSyntax ?? yield.SourceSyntax);
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

	void ValidateIteratorYieldLifetime(FunctionDefinition function, Expression? expression, SyntaxNode? syntax)
	{
		if (expression is null)
			return;
		string yieldedType = function.ReturnType is IterTypeReference iter ? iter.ElementType?.ResolvedType ?? iter.ResolvedType ?? ErrorType : function.ResolvedType ?? ErrorType;
		if (!IsPointerBearingResolvedType(yieldedType))
			return;

		HashSet<string> scopedParameters = new(StringComparer.Ordinal);
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (string.IsNullOrWhiteSpace(parameter.Name))
				continue;
			string? lifetimeBinding = parameter.LifetimeBinding;
			if (lifetimeBinding is null
				&& TryGetLifetimeAnnotation(parameter.Type, out string lifetimeKind, out IReadOnlyList<string> lifetimeAnchors, out string? annotatedLifetime))
				lifetimeBinding = annotatedLifetime ?? new BoundLifetime(lifetimeKind, lifetimeAnchors, "explicit parameter").ToString();
			if (lifetimeBinding is null || lifetimeBinding.StartsWith("scoped", StringComparison.Ordinal))
				scopedParameters.Add(parameter.Name);
		}

		HashSet<string> scopedLocals = new(StringComparer.Ordinal);
		foreach (DeclarationStatement declaration in EnumerateIteratorLocalDeclarations(function.Body))
		{
			if (declaration.Target.Names.Count != 1)
				continue;
			string name = declaration.Target.Names[0];
			if (declaration.InitialValue is ConstructionExpression { Kind: ConstructionKind.Init, ElementCount: not null })
				scopedLocals.Add(name);
		}

		if (ReferencesAnyName(expression, scopedLocals))
		{
			Report(GetRange(syntax ?? expression.SourceSyntax), "Yield expression cannot yield a pointer-bearing value tied to declaration-scope local storage.");
			return;
		}

		if (ReferencesAnyName(expression, scopedParameters))
			Report(GetRange(syntax ?? expression.SourceSyntax), "Yield expression cannot yield a pointer-bearing value that does not outlive the iterator frame.");
	}

	static bool ReferencesAnyName(Expression? expression, HashSet<string> names)
	{
		if (expression is null || names.Count == 0)
			return false;

		switch (expression)
		{
			case NamedExpression named:
				return names.Contains(named.Name);
			case VariableReferenceExpression variable:
				string? referenceName = GetReferenceName(variable.Variable);
				return !string.IsNullOrWhiteSpace(referenceName) && names.Contains(referenceName);
			case ParenthesizedExpression parenthesized:
				return ReferencesAnyName(parenthesized.Expression, names);
			case CastExpression cast:
				return ReferencesAnyName(cast.Expression, names);
			case UnaryExpression unary:
				return ReferencesAnyName(unary.Operand, names);
			case BinaryExpression binary:
				return ReferencesAnyName(binary.Left, names) || ReferencesAnyName(binary.Right, names);
			case ConditionalExpression conditional:
				return ReferencesAnyName(conditional.Condition, names)
					|| ReferencesAnyName(conditional.WhenTrue, names)
					|| ReferencesAnyName(conditional.WhenFalse, names);
			case AssignmentExpression assignment:
				return ReferencesAnyName(assignment.Target, names) || ReferencesAnyName(assignment.Value, names);
			case CallExpression call:
				return ReferencesAnyName(call.Target, names) || AnyArgumentReferencesName(call.Arguments, names);
			case IndexExpression index:
				return ReferencesAnyName(index.Target, names) || AnyArgumentReferencesName(index.Arguments, names);
			case MemberExpression member:
				return ReferencesAnyName(member.Target, names);
			case MemberReferenceExpression member:
				return ReferencesAnyName(member.Target, names);
			case NamelessIndexerExpression indexer:
				return ReferencesAnyName(indexer.Target, names) || AnyArgumentReferencesName(indexer.Arguments, names);
			case ArrayExpression array:
				foreach (Expression element in array.Elements)
					if (ReferencesAnyName(element, names))
						return true;
				return false;
			case InitializerExpression initializer:
				foreach (InitializerItem item in initializer.Items)
					if (ReferencesAnyName(item.Expression, names))
						return true;
				return false;
			case FinallyDeleteExpression finallyDelete:
				return ReferencesAnyName(finallyDelete.Expression, names);
			case WithinExpression within:
				return ReferencesAnyName(within.Context, names) || ReferencesAnyName(within.Expression, names);
			default:
				return false;
		}
	}

	static bool AnyArgumentReferencesName(List<ArgumentExpression> arguments, HashSet<string> names)
	{
		foreach (ArgumentExpression argument in arguments)
			if (ReferencesAnyName(argument.Value, names))
				return true;
		return false;
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

		if (expression is ThisExpression
			&& currentIteratorStateThisType is not null
			&& iteratorStateFieldsBySourceName.TryGetValue(BaseTypeName(currentIteratorStateThisType), out Dictionary<string, FieldDefinition>? stateFields)
			&& stateFields.TryGetValue("this", out FieldDefinition? thisField))
		{
			return ThisMemberReference("this", thisField.ResolvedType);
		}

		if (expression is SizeOfExpression sizeOf
			&& IsGenericSizeOf(sizeOf, out _)
			&& ThisMemberReference(SizeOfParameterName(sizeOf.Type), "nuint") is { Member: FieldDefinition } sizeOfField)
		{
			return sizeOfField;
		}

		if (expression is VTableOfExpression vtableOf
			&& ThisMemberReference(VTableOfParameterName(vtableOf.Type, vtableOf.InterfaceType), VTableOfParameterType(new VTableOfParameterDefinition { Type = vtableOf.Type, InterfaceType = vtableOf.InterfaceType })) is { Member: FieldDefinition } vtableOfField)
		{
			return vtableOfField;
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
			case LambdaExpression lambda:
				RewriteIteratorLambdaBody(lambda.Body, lowering);
				break;
		}

		return expression;
	}

	void RewriteIteratorLambdaBody(BlockStatement? body, IteratorBodyLowering lowering)
	{
		if (body is null)
			return;
		foreach (Statement statement in body.Statements)
			RewriteIteratorLambdaStatement(statement, lowering);
	}

	void RewriteIteratorLambdaStatement(Statement? statement, IteratorBodyLowering lowering)
	{
		switch (statement)
		{
			case null:
				return;
			case BlockStatement block:
				foreach (Statement child in block.Statements)
					RewriteIteratorLambdaStatement(child, lowering);
				break;
			case DeclarationStatement declaration:
				declaration.InitialValue = RewriteIteratorExpression(declaration.InitialValue, lowering);
				break;
			case ExpressionStatement expression:
				expression.Expression = RewriteIteratorExpression(expression.Expression, lowering);
				break;
			case IfStatement ifStatement:
				ifStatement.Condition = RewriteIteratorExpression(ifStatement.Condition, lowering);
				RewriteIteratorLambdaStatement(ifStatement.Body, lowering);
				RewriteIteratorLambdaStatement(ifStatement.ElseBody, lowering);
				break;
			case WhileStatement whileStatement:
				whileStatement.Condition = RewriteIteratorExpression(whileStatement.Condition, lowering);
				RewriteIteratorLambdaStatement(whileStatement.Body, lowering);
				break;
			case DoWhileStatement doWhile:
				RewriteIteratorLambdaStatement(doWhile.Body, lowering);
				doWhile.Condition = RewriteIteratorExpression(doWhile.Condition, lowering);
				break;
			case ForStatement forStatement:
				RewriteIteratorLambdaStatement(forStatement.Condition.Declaration, lowering);
				for (int i = 0; i < forStatement.Condition.Clauses.Count; i++)
					forStatement.Condition.Clauses[i] = RewriteIteratorExpression(forStatement.Condition.Clauses[i], lowering);
				RewriteIteratorLambdaStatement(forStatement.Body, lowering);
				break;
			case ForeachStatement foreachStatement:
				foreachStatement.Source = RewriteIteratorExpression(foreachStatement.Source, lowering);
				RewriteIteratorLambdaStatement(foreachStatement.Body, lowering);
				break;
			case SwitchStatement switchStatement:
				switchStatement.Expression = RewriteIteratorExpression(switchStatement.Expression, lowering);
				foreach (Statement child in switchStatement.Statements)
					RewriteIteratorLambdaStatement(child, lowering);
				break;
			case CaseStatement caseStatement:
				caseStatement.Expression = RewriteIteratorExpression(caseStatement.Expression, lowering);
				break;
			case ReturnStatement returnStatement:
				returnStatement.Expression = RewriteIteratorExpression(returnStatement.Expression, lowering);
				break;
			case YieldStatement yieldStatement:
				yieldStatement.Expression = RewriteIteratorExpression(yieldStatement.Expression, lowering);
				break;
			case DeleteStatement deleteStatement:
				deleteStatement.Expression = RewriteIteratorExpression(deleteStatement.Expression, lowering);
				break;
			case TryStatement tryStatement:
				RewriteIteratorLambdaStatement(tryStatement.Body, lowering);
				foreach (CatchStatement catchStatement in tryStatement.Catches)
					RewriteIteratorLambdaStatement(catchStatement, lowering);
				RewriteIteratorLambdaStatement(tryStatement.Finally, lowering);
				break;
			case CatchStatement catchStatement:
				RewriteIteratorLambdaStatement(catchStatement.Body, lowering);
				break;
			case FinallyStatement finallyStatement:
				RewriteIteratorLambdaStatement(finallyStatement.Body, lowering);
				break;
			case WithinStatement withinStatement:
				withinStatement.Allocator = RewriteIteratorExpression(withinStatement.Allocator, lowering);
				RewriteIteratorLambdaStatement(withinStatement.Body, lowering);
				break;
		}
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

	IEnumerable<ForeachStatement> EnumerateIteratorForeachStatements(BlockStatement? body)
	{
		if (body is null)
			yield break;

		foreach (Statement statement in body.Statements)
			foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(statement))
				yield return foreachStatement;
	}

	IEnumerable<ForeachStatement> EnumerateIteratorForeachStatements(Statement? statement)
	{
		switch (statement)
		{
			case ForeachStatement foreachStatement:
				yield return foreachStatement;
				foreach (ForeachStatement child in EnumerateIteratorForeachStatements(foreachStatement.Body))
					yield return child;
				break;
			case BlockStatement block:
				foreach (Statement child in block.Statements)
					foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(child))
						yield return foreachStatement;
				break;
			case IfStatement ifStatement:
				foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(ifStatement.Body))
					yield return foreachStatement;
				foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(ifStatement.ElseBody))
					yield return foreachStatement;
				break;
			case WhileStatement whileStatement:
				foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(whileStatement.Body))
					yield return foreachStatement;
				break;
			case DoWhileStatement doWhile:
				foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(doWhile.Body))
					yield return foreachStatement;
				break;
			case ForStatement forStatement:
				foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(forStatement.Body))
					yield return foreachStatement;
				break;
			case SwitchStatement switchStatement:
				foreach (Statement child in switchStatement.Statements)
					foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(child))
						yield return foreachStatement;
				break;
			case TryStatement tryStatement:
				foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(tryStatement.Body))
					yield return foreachStatement;
				foreach (CatchStatement catchStatement in tryStatement.Catches)
					foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(catchStatement.Body))
						yield return foreachStatement;
				foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(tryStatement.Finally))
					yield return foreachStatement;
				break;
			case CatchStatement catchStatement:
				foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(catchStatement.Body))
					yield return foreachStatement;
				break;
			case FinallyStatement finallyStatement:
				foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(finallyStatement.Body))
					yield return foreachStatement;
				break;
			case WithinStatement withinStatement:
				foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(withinStatement.Body))
					yield return foreachStatement;
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

	IEnumerable<IteratorForeachStateFields> GetIteratorForeachStateFields(FunctionDefinition function)
	{
		foreach (ForeachStatement foreachStatement in EnumerateIteratorForeachStatements(function.Body))
			if (iteratorForeachStates.TryGetValue(foreachStatement, out IteratorForeachStateFields? fields))
				yield return fields;
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
		string? thisType = currentIteratorStateThisType;
		if (thisType is null && currentRewriteContainingType is not null)
			thisType = $"{currentRewriteContainingType.Name}*";

		FieldDefinition? field = null;
		if (!string.IsNullOrWhiteSpace(thisType)
			&& iteratorStateFieldsBySourceName.TryGetValue(BaseTypeName(thisType), out Dictionary<string, FieldDefinition>? fields))
		{
			fields.TryGetValue(name, out field);
		}

		return new MemberReferenceExpression
		{
			Target = new ThisExpression { ResolvedType = thisType },
			Name = name,
			Member = field,
			ResolvedType = resolvedType
		};
	}

	FieldDefinition? TryGetIteratorStateField(TypeDefinition type, string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return null;
		if (iteratorStateFields.TryGetValue(type.Name, out Dictionary<string, FieldDefinition>? fields)
			&& fields.TryGetValue(name, out FieldDefinition? field))
			return field;
		if (iteratorStateFieldsBySourceName.TryGetValue(type.Name, out Dictionary<string, FieldDefinition>? sourceFields)
			&& sourceFields.TryGetValue(name, out field))
			return field;
		return null;
	}

	void AddIteratorField(TypeDefinition type, FieldDefinition field, string? sourceName = null)
	{
		generatedDeclarations.Mark(field, GeneratedDeclarationCategory.Iterator, "iterator state field", type);
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

		if (!iteratorStateFields.TryGetValue(type.Name, out Dictionary<string, FieldDefinition>? fields))
		{
			fields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
			iteratorStateFields[type.Name] = fields;
		}
		if (!string.IsNullOrWhiteSpace(field.Name))
			fields[field.Name] = field;

		sourceName ??= field.Name;
		if (!iteratorStateFieldsBySourceName.TryGetValue(type.Name, out Dictionary<string, FieldDefinition>? sourceFields))
		{
			sourceFields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
			iteratorStateFieldsBySourceName[type.Name] = sourceFields;
		}
		if (!string.IsNullOrWhiteSpace(sourceName))
			sourceFields[sourceName] = field;
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

	List<ParameterDefinition> GetIteratorYieldSlots(IterTypeReference iterType)
	{
		List<ParameterDefinition> slots = [];
		if (iterType.Parameters.Count == 0)
		{
			slots.Add(new ParameterDefinition
			{
				Name = "current",
				Symbol = "current",
				Type = CloneType(iterType.ElementType),
				ResolvedType = iterType.ElementType?.ResolvedType ?? FormatTypeReference(iterType.ElementType)
			});
			return slots;
		}

		foreach (ParameterDefinition parameter in iterType.Parameters)
		{
			if (parameter.Modifier != ParameterModifier.Thrown)
			{
				parameter.ResolvedType ??= parameter.Type?.ResolvedType ?? FormatTypeReference(parameter.Type);
				slots.Add(parameter);
			}
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

	string ResolvedTypeForIteratorExpansion(TypeReference? type, string? resolvedType)
	{
		if (!string.IsNullOrWhiteSpace(resolvedType) && resolvedType != UnresolvedType && resolvedType != ErrorType)
			return resolvedType;
		return type switch
		{
			null => ErrorType,
			AttributedTypeReference attributed => ResolvedTypeForIteratorExpansion(attributed.Type, attributed.ResolvedType),
			ConstTypeReference constant => "const " + ResolvedTypeForIteratorExpansion(constant.Type, constant.ResolvedType),
			ConstOfTypeReference constOf => "const " + ResolvedTypeForIteratorExpansion(constOf.Type, constOf.ResolvedType),
			PointerTypeReference pointer => AddPointer(ResolvedTypeForIteratorExpansion(pointer.ElementType, pointer.ElementType?.ResolvedType)),
			ArrayTypeReference array => ResolvedTypeForIteratorExpansion(array.ElementType, array.ElementType?.ResolvedType) + "[]",
			OptionalTypeReference optional => ResolvedTypeForIteratorExpansion(optional.ElementType, optional.ElementType?.ResolvedType) + "?",
			PrimitiveTypeReference primitive => GetPrimitiveTypeName(primitive.Type),
			CallableTypeReference callable => BuildCallableType(GetCallableKindName(callable.Kind), ResolvedTypeForIteratorExpansion(callable.ReturnType, callable.ReturnType?.ResolvedType), GetIteratorExpansionParameterTypes(callable.Parameters)),
			IterTypeReference iter => ResolvedIteratorTypeForExpansion(iter),
			NamedTypeReference named => IsInvalidResolvedType(named.ResolvedType) ? named.Name : named.ResolvedType ?? named.Name,
			TypeDefinitionReference definition => IsInvalidResolvedType(definition.ResolvedType) ? definition.Name : definition.ResolvedType ?? definition.Name,
			GenericParameterTypeReference generic => IsInvalidResolvedType(generic.ResolvedType) ? generic.Name : generic.ResolvedType ?? generic.Name,
			_ => type.ResolvedType ?? FormatTypeReference(type)
		};
	}

	static bool IsInvalidResolvedType(string? type)
	{
		return type is null or "" or UnresolvedType or ErrorType;
	}

	List<string> GetIteratorExpansionParameterTypes(List<ParameterDefinition> parameters)
	{
		List<string> types = [];
		foreach (ParameterDefinition parameter in parameters)
			types.Add(ResolvedTypeForIteratorExpansion(parameter.Type, parameter.ResolvedType));
		return types;
	}

	string ResolvedIteratorTypeForExpansion(IterTypeReference iter)
	{
		if (iter.Parameters.Count == 0)
			return $"{(iter.IsAsync ? "async iter" : "iter")} {ResolvedTypeForIteratorExpansion(iter.ElementType, iter.ElementType?.ResolvedType)}";

		List<string> slots = [];
		foreach (ParameterDefinition parameter in iter.Parameters)
		{
			string type = ResolvedTypeForIteratorExpansion(parameter.Type, parameter.ResolvedType);
			slots.Add(parameter.Modifier == ParameterModifier.Thrown ? $"thrown {type}" : type);
		}
		return $"{(iter.IsAsync ? "async iter" : "iter")}({string.Join(", ", slots)})";
	}

	sealed record IteratorForeachStateFields(string IteratorFieldName, string CurrentFieldName, string IteratorType, string ElementType)
	{
		public bool IsArray { get; init; }
		public bool IsProtocol { get; init; }
		public string? LengthFieldName { get; init; }
		public string? IndexFieldName { get; init; }
		public string? ContextFieldName { get; init; }

		public static IteratorForeachStateFields ForArray(string elementsFieldName, string lengthFieldName, string indexFieldName, string elementPointerType, string elementType)
		{
			return new IteratorForeachStateFields(elementsFieldName, "", elementPointerType, elementType)
			{
				IsArray = true,
				LengthFieldName = lengthFieldName,
				IndexFieldName = indexFieldName
			};
		}

		public static IteratorForeachStateFields ForProtocol(string callFieldName, string contextFieldName, string currentFieldName, string callType, string elementType)
		{
			return new IteratorForeachStateFields(callFieldName, currentFieldName, callType, elementType)
			{
				IsProtocol = true,
				ContextFieldName = contextFieldName
			};
		}
	}

	sealed class IteratorBodyLowering
	{
		readonly BindableNodeAnalyzer analyzer;
		readonly Dictionary<string, string> liftedTypes = new(StringComparer.Ordinal);
		readonly List<Statement> cleanupStatements;
		readonly ParameterDefinition current;
		readonly string yieldedType;
		int nextState = 1;

		public FunctionDefinition Function { get; }

		public IteratorBodyLowering(BindableNodeAnalyzer analyzer, FunctionDefinition function, ParameterDefinition current, string yieldedType)
		{
			this.analyzer = analyzer;
			Function = function;
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
					SkipPendingCleanups = true,
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
