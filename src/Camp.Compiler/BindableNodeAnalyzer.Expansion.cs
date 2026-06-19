using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void GenerateVirtualDeclarations(Module module)
	{
		foreach (Definition definition in module.Definitions.ToArray())
		{
			if (definition is ClassDefinition classDefinition && IsVirtualClassParticipant(classDefinition))
				GenerateVirtualClassDeclarations(module, classDefinition);
		}
	}

	void GenerateVirtualClassDeclarations(Module module, ClassDefinition classDefinition)
	{
		ClassDefinition? baseClass = GetDirectBaseClass(classDefinition);
		VirtualClassLowering? baseLowering = baseClass is not null && virtualClassLowerings.TryGetValue(baseClass, out VirtualClassLowering? foundBase)
			? foundBase
			: null;

		StructDefinition vtableType = new()
		{
			Name = VirtualTableTypeName(classDefinition),
			Symbol = VirtualTableTypeName(classDefinition),
			ResolvedType = VirtualTableTypeName(classDefinition)
		};
		if (baseLowering is not null)
		{
			vtableType.Fields.Add(new FieldDefinition
			{
				Name = baseClass!.Name,
				Symbol = baseClass.Name,
				Type = TypeReferenceFor(baseLowering.VTableType),
				ResolvedType = baseLowering.VTableType.Name
			});
		}

		VirtualClassLowering lowering = new(classDefinition, baseClass, baseLowering, vtableType);
		virtualClassLowerings[classDefinition] = lowering;

		if (baseLowering is null)
		{
			FieldDefinition field = new()
			{
				Name = VirtualTableFieldName,
				Symbol = VirtualTableFieldName,
				Type = PointerTo(TypeReferenceFor(vtableType)),
				ResolvedType = $"{vtableType.Name}*"
			};
			classDefinition.Fields.Insert(0, field);
			lowering.Field = field;
		}

		List<FunctionDefinition> generated = [];
		foreach (FunctionDefinition function in classDefinition.Functions.ToArray())
		{
			if (!IsVirtualMethodDeclaration(function))
				continue;

			FunctionDefinition? implementation = CreateVirtualImplementationMethod(classDefinition, function);
			if (implementation is not null)
			{
				generated.Add(implementation);
				virtualImplementations[function] = implementation;
			}

			if (function.Modifier is FunctionModifier.Virtual or FunctionModifier.Abstract)
			{
				FieldDefinition slotField = CreateVirtualSlotField(classDefinition, function);
				VirtualSlot slot = new(function, implementation, slotField);
				lowering.DeclaredSlots.Add(slot);
				vtableType.Fields.Add(slotField);
				function.Body = CreateVirtualDispatchBody(classDefinition, function, slotField);
			}
			else if (function.Modifier is FunctionModifier.Override or FunctionModifier.Sealed)
			{
				function.Body = null;
			}
		}
		classDefinition.Functions.AddRange(generated);

		VariableDefinition vtable = new()
		{
			Name = VirtualTableVariableName(classDefinition),
			Symbol = VirtualTableVariableName(classDefinition),
			Type = TypeReferenceFor(vtableType),
			ResolvedType = vtableType.Name
		};
		lowering.VTable = vtable;

		module.Definitions.Add(vtableType);
		module.Definitions.Add(vtable);
		vtable.InitialValue = CreateVirtualVTableInitializer(lowering);
	}

	static bool IsVirtualClassParticipant(ClassDefinition classDefinition)
	{
		return classDefinition.Modifier is ClassModifier.Virtual or ClassModifier.Abstract or ClassModifier.Sealed;
	}

	static bool IsVirtualMethodDeclaration(FunctionDefinition function)
	{
		if (IsDestructorFunction(function))
			return false;

		return function.Modifier is FunctionModifier.Virtual or FunctionModifier.Abstract or FunctionModifier.Override or FunctionModifier.Sealed;
	}

	FunctionDefinition? CreateVirtualImplementationMethod(ClassDefinition owner, FunctionDefinition source)
	{
		if (source.Body is null)
			return null;

		ClassDefinition? abiOwner = GetVirtualImplementationAbiOwner(owner, source);
		FunctionDefinition implementation = new()
		{
			SourceSyntax = source.SourceSyntax,
			Name = VirtualImplementationName(source),
			Symbol = VirtualImplementationSymbol(owner, source),
			Export = source.Export,
			Public = source.Public,
			ReturnType = CloneType(source.ReturnType),
			ResolvedType = source.ResolvedType,
			Body = source.Body
		};
		CopyParameters(source.Parameters, implementation.Parameters);
		if (abiOwner is not null && !ReferenceEquals(abiOwner, owner))
		{
			implementation.AbiThisType = PointerTo(TypeReferenceFor(abiOwner));
			implementation.AbiThisType.ResolvedType = $"{abiOwner.Name}*";
			implementation.ImplementationThisType = PointerTo(TypeReferenceFor(owner));
			implementation.ImplementationThisType.ResolvedType = $"{owner.Name}*";
		}
		return implementation;
	}

	ClassDefinition? GetVirtualImplementationAbiOwner(ClassDefinition owner, FunctionDefinition source)
	{
		if (source.Modifier is not (FunctionModifier.Override or FunctionModifier.Sealed))
			return owner;

		for (ClassDefinition? current = GetDirectBaseClass(owner); current is not null; current = GetDirectBaseClass(current))
		{
			foreach (FunctionDefinition candidate in current.Functions)
			{
				if (VirtualSlotName(candidate) != VirtualSlotName(source))
					continue;
				if (candidate.Modifier is FunctionModifier.Virtual or FunctionModifier.Abstract)
					return current;
			}
		}
		return owner;
	}

	FieldDefinition CreateVirtualSlotField(ClassDefinition owner, FunctionDefinition function)
	{
		CallableTypeReference callable = new()
		{
			Kind = CallableKind.Function,
			ReturnType = CloneType(function.ReturnType) ?? VoidType(),
			ResolvedType = BuildVirtualSlotCallableType(owner, function)
		};
		callable.Parameters.Add(new ParameterDefinition
		{
			Name = "ctx",
			Symbol = "ctx",
			Type = PointerTo(TypeReferenceFor(owner)),
			ResolvedType = $"{owner.Name}*"
		});
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter is ThisParameterDefinition)
				continue;
			callable.Parameters.Add(CloneParameter(parameter));
		}

		return new FieldDefinition
		{
			Name = VirtualSlotName(function),
			Symbol = VirtualSlotName(function),
			Type = callable,
			ResolvedType = callable.ResolvedType
		};
	}

	BlockStatement CreateVirtualDispatchBody(ClassDefinition owner, FunctionDefinition function, FieldDefinition slotField)
	{
		string returnType = GetFunctionReturnTypeName(function);
		Expression vtableTarget = new MemberReferenceExpression
		{
			Target = new ThisExpression { ResolvedType = owner.Name },
			Name = VirtualTableFieldName,
			ResolvedType = GetRootVirtualTableFieldType(owner)
		};
		if (virtualClassLowerings.TryGetValue(owner, out VirtualClassLowering? lowering) && lowering.BaseLowering is not null)
		{
			vtableTarget = new CastExpression
			{
				Type = PointerTo(TypeReferenceFor(lowering.VTableType)),
				Kind = CastKind.Type,
				Expression = vtableTarget,
				ResolvedType = $"{lowering.VTableType.Name}*"
			};
		}

		CallExpression call = new()
		{
			ResolvedType = returnType,
			Target = new MemberReferenceExpression
			{
				Target = vtableTarget,
				Name = VirtualSlotName(function),
				Member = slotField,
				ResolvedType = slotField.ResolvedType
			}
		};
		call.Arguments.Add(new ArgumentExpression
		{
			Value = new ThisExpression { ResolvedType = $"{owner.Name}*" },
			ResolvedType = $"{owner.Name}*"
		});
		foreach (ArgumentExpression argument in CreateVirtualDispatchParameterArguments(function))
			call.Arguments.Add(argument);

		BlockStatement body = new() { ResolvedType = "void" };
		if (returnType == "void")
			body.Statements.Add(new ExpressionStatement { Expression = call, ResolvedType = "void" });
		else
			body.Statements.Add(new ReturnStatement { Expression = call, ResolvedType = "void" });
		return body;
	}

	List<ArgumentExpression> CreateVirtualDispatchParameterArguments(FunctionDefinition function)
	{
		List<ArgumentExpression> arguments = [];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter is ThisParameterDefinition or VTableOfParameterDefinition)
				continue;

			arguments.Add(new ArgumentExpression
			{
				SourceSyntax = parameter.SourceSyntax,
				Value = new VariableReferenceExpression
				{
					SourceSyntax = parameter.SourceSyntax,
					Variable = parameter,
					ResolvedType = parameter.ResolvedType
				},
				ResolvedType = parameter.ResolvedType
			});
		}
		return arguments;
	}

	InitializerExpression CreateVirtualVTableInitializer(VirtualClassLowering lowering)
	{
		InitializerExpression initializer = new() { ResolvedType = lowering.VTableType.Name };
		if (lowering.BaseLowering is not null && lowering.BaseClass is not null)
		{
			initializer.Items.Add(new InitializerItem
			{
				Target = InitializerTargetFor(lowering.BaseClass.Name),
				Expression = CreateBaseVirtualInitializer(lowering, lowering.BaseLowering),
				ResolvedType = lowering.BaseLowering.VTableType.Name
			});
		}

		foreach (VirtualSlot slot in lowering.DeclaredSlots)
		{
			initializer.Items.Add(new InitializerItem
			{
				Target = InitializerTargetFor(VirtualSlotName(slot.Declaration)),
				Expression = slot.Implementation is null
					? new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "#NULL" }
					: CreateMethodReference(slot.Implementation, BuildVirtualSlotCallableType(lowering.Class, slot.Declaration)),
				ResolvedType = BuildVirtualSlotCallableType(lowering.Class, slot.Declaration)
			});
		}
		return initializer;
	}

	InitializerExpression CreateBaseVirtualInitializer(VirtualClassLowering derived, VirtualClassLowering baseLowering)
	{
		InitializerExpression initializer = new() { ResolvedType = baseLowering.VTableType.Name };
		if (baseLowering.BaseLowering is not null && baseLowering.BaseClass is not null)
		{
			initializer.Items.Add(new InitializerItem
			{
				Target = InitializerTargetFor(baseLowering.BaseClass.Name),
				Expression = CreateBaseVirtualInitializer(derived, baseLowering.BaseLowering),
				ResolvedType = baseLowering.BaseLowering.VTableType.Name
			});
		}

		foreach (VirtualSlot slot in baseLowering.DeclaredSlots)
		{
			FunctionDefinition? implementation = FindClosestVirtualImplementation(derived.Class, slot.Declaration);
			initializer.Items.Add(new InitializerItem
			{
				Target = InitializerTargetFor(VirtualSlotName(slot.Declaration)),
				Expression = implementation is null
					? new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "#NULL" }
					: CreateMethodReference(implementation, BuildVirtualSlotCallableType(baseLowering.Class, slot.Declaration)),
				ResolvedType = BuildVirtualSlotCallableType(baseLowering.Class, slot.Declaration)
			});
		}
		return initializer;
	}

	void GenerateInterfaceDeclarations(Module module)
	{
		Dictionary<string, InterfaceDefinition> interfaces = [];
		foreach (Definition definition in module.Definitions)
		{
			if (definition is InterfaceDefinition interfaceDefinition && !string.IsNullOrWhiteSpace(interfaceDefinition.Name))
				interfaces[interfaceDefinition.Name] = interfaceDefinition;
		}

		foreach (Definition definition in module.Definitions.ToArray())
		{
			if (definition is ClassDefinition classDefinition)
				GenerateClassInterfaceDeclarations(module, classDefinition, interfaces);
			else if (definition is StructDefinition structDefinition)
				GenerateStructInterfaceDeclarations(module, structDefinition, interfaces);
		}
	}

	void GenerateClassInterfaceDeclarations(Module module, ClassDefinition classDefinition, Dictionary<string, InterfaceDefinition> interfaces)
	{
		List<InterfaceImplementationLowering> implementations = [];
		int interfaceIndex = classDefinition.Fields.Count > 0 && classDefinition.Fields[0].Name == VirtualTableFieldName ? 1 : 0;
		foreach (TypeReference baseType in classDefinition.BaseTypes)
		{
			if (!TryGetDirectInterface(baseType, interfaces, out InterfaceDefinition? interfaceDefinition) || interfaceDefinition is null)
				continue;

			FieldDefinition field = new()
			{
				Name = InterfaceFieldName(interfaceDefinition),
				Symbol = InterfaceFieldName(interfaceDefinition),
				Type = PointerTo(new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name }),
				ResolvedType = "const " + interfaceDefinition.Name + "*"
			};
			classDefinition.Fields.Insert(interfaceIndex, field);

			VariableDefinition vtableStorage = new()
			{
				Name = InterfaceVTableName(classDefinition, interfaceDefinition) + "__storage",
				Symbol = InterfaceVTableName(classDefinition, interfaceDefinition) + "__storage",
				Type = new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name },
				ResolvedType = "const " + interfaceDefinition.Name
			};
			module.Definitions.Add(vtableStorage);
			generatedInterfaceDefinitions.Add(vtableStorage);

			VariableDefinition vtable = new()
			{
				Name = InterfaceVTableName(classDefinition, interfaceDefinition),
				Symbol = InterfaceVTableName(classDefinition, interfaceDefinition),
				Export = classDefinition.Export is not null && interfaceDefinition.Export is not null ? "export" : null,
				Public = (classDefinition.Export is null || interfaceDefinition.Export is null) && IsExternallyVisible(classDefinition) && IsExternallyVisible(interfaceDefinition) ? "public" : null,
				Type = PointerTo(new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name }),
				ResolvedType = "const " + interfaceDefinition.Name + "*",
				InitialValue = new UnaryExpression
				{
					Operator = UnaryOperator.AddressOf,
					Operand = new VariableReferenceExpression { Variable = vtableStorage, ResolvedType = vtableStorage.ResolvedType },
					ResolvedType = "const " + interfaceDefinition.Name + "*"
				}
			};
			module.Definitions.Add(vtable);
			generatedInterfaceDefinitions.Add(vtable);

			InterfaceImplementationLowering lowering = new(classDefinition, interfaceDefinition, field, vtable, vtableStorage, DirectEntries: false, IsStruct: false);
			implementations.Add(lowering);
			GenerateInterfaceThunks(module, lowering, interfaceDefinition, interfaces);
			interfaceIndex++;
		}

		if (implementations.Count > 0)
		{
			EnsureInterfaceInitNewMethod(classDefinition);
			classInterfaceLowerings[classDefinition] = implementations;
		}
	}

	void GenerateStructInterfaceDeclarations(Module module, StructDefinition structDefinition, Dictionary<string, InterfaceDefinition> interfaces)
	{
		List<InterfaceImplementationLowering> implementations = [];
		foreach (TypeReference baseType in structDefinition.BaseTypes)
		{
			if (!TryGetDirectInterface(baseType, interfaces, out InterfaceDefinition? interfaceDefinition) || interfaceDefinition is null)
				continue;

			EnsureInterfaceIndirectStruct(module, interfaceDefinition);
			VariableDefinition vtableStorage = new()
			{
				Name = InterfaceVTableName(structDefinition, interfaceDefinition) + "__storage",
				Symbol = InterfaceVTableName(structDefinition, interfaceDefinition) + "__storage",
				Type = new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name },
				ResolvedType = "const " + interfaceDefinition.Name
			};
			module.Definitions.Add(vtableStorage);
			generatedInterfaceDefinitions.Add(vtableStorage);

			VariableDefinition vtable = new()
			{
				Name = InterfaceVTableName(structDefinition, interfaceDefinition),
				Symbol = InterfaceVTableName(structDefinition, interfaceDefinition),
				Export = structDefinition.Export is not null && interfaceDefinition.Export is not null ? "export" : null,
				Public = (structDefinition.Export is null || interfaceDefinition.Export is null) && IsExternallyVisible(structDefinition) && IsExternallyVisible(interfaceDefinition) ? "public" : null,
				Type = PointerTo(new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name }),
				ResolvedType = "const " + interfaceDefinition.Name + "*",
				InitialValue = new UnaryExpression
				{
					Operator = UnaryOperator.AddressOf,
					Operand = new VariableReferenceExpression { Variable = vtableStorage, ResolvedType = vtableStorage.ResolvedType },
					ResolvedType = "const " + interfaceDefinition.Name + "*"
				}
			};
			module.Definitions.Add(vtable);
			generatedInterfaceDefinitions.Add(vtable);

			InterfaceImplementationLowering lowering = new(structDefinition, interfaceDefinition, Field: null, vtable, vtableStorage, DirectEntries: false, IsStruct: true);
			implementations.Add(lowering);
			GenerateInterfaceThunks(module, lowering, interfaceDefinition, interfaces);
		}

		if (implementations.Count > 0)
			structInterfaceLowerings[structDefinition] = implementations;
	}

	void EnsureInterfaceInitNewMethod(ClassDefinition classDefinition)
	{
		foreach (FunctionDefinition function in classDefinition.Functions)
		{
			if (function.Name == InitNewMethodName || function.Modifier == FunctionModifier.Constructor)
				return;
		}

		classDefinition.Functions.Add(new FunctionDefinition
		{
			Name = InitNewMethodName,
			Symbol = $"{classDefinition.Name}_{InitNewMethodName}",
			ReturnType = VoidType(),
			ResolvedType = "void",
			Body = new BlockStatement { ResolvedType = "void" }
		});
	}

	void GenerateInterfaceThunks(Module module, InterfaceImplementationLowering lowering, InterfaceDefinition interfaceDefinition, Dictionary<string, InterfaceDefinition> interfaces)
	{
		foreach (InterfaceDefinition implementedInterface in GetInterfaceAndBaseInterfaces(interfaceDefinition, interfaces))
		{
			foreach (FunctionDefinition member in implementedInterface.Functions)
			{
				if (lowering.DirectEntries)
					continue;

				FunctionDefinition thunk = CreateInterfaceThunkDeclaration(lowering, implementedInterface, member);
				module.Definitions.Add(thunk);
				generatedInterfaceDefinitions.Add(thunk);
				interfaceThunkLowerings[thunk] = new InterfaceThunkLowering(lowering, implementedInterface, member);
			}
		}
	}

	FunctionDefinition CreateInterfaceThunkDeclaration(InterfaceImplementationLowering lowering, InterfaceDefinition entryInterface, FunctionDefinition member)
	{
		FunctionDefinition thunk = new()
		{
			Name = InterfaceThunkName(lowering.Type, entryInterface, member),
			Symbol = InterfaceThunkName(lowering.Type, entryInterface, member),
			ReturnType = CloneType(member.ReturnType) ?? VoidType(),
			ResolvedType = GetInterfaceEntryReturnType(member, lowering.Type)
		};
		thunk.Parameters.Add(new ParameterDefinition
		{
			Name = "ctx",
			Symbol = "ctx",
			Type = InterfaceInstanceType(entryInterface),
			ResolvedType = $"{entryInterface.Name}**"
		});
		foreach (ParameterDefinition parameter in member.Parameters)
		{
			if (parameter is ThisParameterDefinition)
				continue;
			thunk.Parameters.Add(CloneParameter(parameter));
		}
		return thunk;
	}

	void CompleteInterfaceDeclarations(Module module)
	{
		foreach ((ClassDefinition classDefinition, List<InterfaceImplementationLowering> lowerings) in classInterfaceLowerings)
		{
			foreach (InterfaceImplementationLowering lowering in lowerings)
				lowering.VTableStorage.InitialValue = CreateInterfaceVTableInitializer(lowering, lowering.Interface);

			for (int i = lowerings.Count - 1; i >= 0; i--)
				InsertInterfaceVTableInitialization(classDefinition, lowerings[i]);
		}

		foreach ((StructDefinition _, List<InterfaceImplementationLowering> lowerings) in structInterfaceLowerings)
		{
			foreach (InterfaceImplementationLowering lowering in lowerings)
				lowering.VTableStorage.InitialValue = CreateInterfaceVTableInitializer(lowering, lowering.Interface);
		}

		foreach ((FunctionDefinition thunk, InterfaceThunkLowering lowering) in interfaceThunkLowerings)
			thunk.Body = CreateInterfaceThunkBody(thunk, lowering);
	}

	void LowerInterfaceDefinitions(Module module)
	{
		for (int i = 0; i < module.Definitions.Count; i++)
		{
			if (module.Definitions[i] is InterfaceDefinition interfaceDefinition)
				module.Definitions[i] = LowerInterfaceDefinition(interfaceDefinition);
			else if (module.Definitions[i] is ClassDefinition classDefinition)
				RemoveLoweredInterfaceBaseTypes(classDefinition);
			else if (module.Definitions[i] is StructDefinition structDefinition)
				RemoveLoweredInterfaceBaseTypes(structDefinition);
		}
	}

	void LowerSourceInterfaceTypes(BindableNode node)
	{
		LowerSourceInterfaceTypes(node, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance));
	}

	void LowerSourceInterfaceTypes(BindableNode node, HashSet<BindableNode> visited)
	{
		if (!visited.Add(node))
			return;

		foreach (PropertyInfo property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (property.Name == nameof(BindableNode.SourceSyntax) || property.Name == nameof(BindableNode.ResolvedType) || IsSemanticReferenceProperty(property))
				continue;

			object? value = property.GetValue(node);
			if (value is null)
				continue;

			if (value is TypeReference type)
			{
				TypeReference lowered = LowerSourceInterfaceType(type);
				if (!ReferenceEquals(lowered, type) && property.CanWrite)
					property.SetValue(node, lowered);
				LowerSourceInterfaceTypes(lowered, visited);
			}
			else if (value is IList list)
			{
				LowerSourceInterfaceTypes(list, visited);
			}
			else if (value is BindableNode child)
			{
				LowerSourceInterfaceTypes(child, visited);
			}
		}

		SyncResolvedTypeFromLoweredType(node);
	}

	void LowerSourceInterfaceTypes(IList list, HashSet<BindableNode> visited)
	{
		for (int i = 0; i < list.Count; i++)
		{
			object? item = list[i];
			if (item is TypeReference type)
			{
				TypeReference lowered = LowerSourceInterfaceType(type);
				if (!ReferenceEquals(lowered, type))
					list[i] = lowered;
				LowerSourceInterfaceTypes(lowered, visited);
			}
			else if (item is BindableNode child)
			{
				LowerSourceInterfaceTypes(child, visited);
			}
		}
	}

	TypeReference LowerSourceInterfaceType(TypeReference type)
	{
		if (type is PointerTypeReference pointer
			&& (pointer.SourceSyntax is not null || pointer.ElementType?.SourceSyntax is not null)
			&& TryGetInterfaceDefinition(pointer.ElementType ?? pointer, out InterfaceDefinition? interfaceDefinition)
			&& interfaceDefinition is not null)
		{
			TypeReference lowered = PointerTo(PointerTo(InterfaceType(interfaceDefinition)));
			lowered.SourceSyntax = type.SourceSyntax;
			lowered.ResolvedType = $"{interfaceDefinition.Name}**";
			return lowered;
		}

		return type;
	}

	static void SyncResolvedTypeFromLoweredType(BindableNode node)
	{
		switch (node)
		{
			case SizeOfParameterDefinition:
				node.ResolvedType = "nuint";
				break;

			case VTableOfParameterDefinition vtableOf:
				node.ResolvedType = VTablePointerType(vtableOf.InterfaceType);
				break;

			case ParameterDefinition parameter when parameter.Type is not null:
				parameter.ResolvedType = parameter.Type.ResolvedType;
				break;

			case FieldDefinition field when field.Type is not null:
				field.ResolvedType = field.Type.ResolvedType;
				break;

			case VariableDefinition variable when variable.Type is not null:
				variable.ResolvedType = variable.Type.ResolvedType;
				break;

			case DeclarationTarget target when target.Type is not null && target.Type is not AutoTypeReference:
				target.ResolvedType = target.Type.ResolvedType;
				break;
		}
	}

	void RefreshLoweredResolvedTypes(BindableNode node)
	{
		RefreshLoweredResolvedTypes(node, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance));
	}

	void RefreshLoweredResolvedTypes(BindableNode node, HashSet<BindableNode> visited)
	{
		if (!visited.Add(node))
			return;

		foreach (PropertyInfo property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (property.Name == nameof(BindableNode.SourceSyntax) || property.Name == nameof(BindableNode.ResolvedType) || IsSemanticReferenceProperty(property))
				continue;

			object? value = property.GetValue(node);
			if (value is BindableNode child)
				RefreshLoweredResolvedTypes(child, visited);
			else if (value is IList list)
			{
				foreach (object? item in list)
				{
					if (item is BindableNode childItem)
						RefreshLoweredResolvedTypes(childItem, visited);
				}
			}
		}

		SyncResolvedTypeFromLoweredType(node);
		switch (node)
		{
			case VariableReferenceExpression variable:
				variable.ResolvedType = variable.Variable?.ResolvedType ?? variable.ResolvedType;
				break;

			case MethodReferenceExpression method when method.Candidates.Count == 1:
				if (NeedsResolvedTypeRefresh(method.ResolvedType))
				{
					bool isInstance = IsInstanceFunction(method.Candidates[0]);
					method.ResolvedType = BuildFunctionValueType(method.Candidates[0], isInstance);
				}
				break;

			case MemberReferenceExpression { Member: FunctionDefinition function } member:
				member.ResolvedType = function.ResolvedType ?? member.ResolvedType;
				break;

			case CallExpression call:
				call.ResolvedType = GetLoweredCallReturnType(call) ?? call.ResolvedType;
				break;
		}
	}

	static bool NeedsResolvedTypeRefresh(string? resolvedType)
	{
		return string.IsNullOrWhiteSpace(resolvedType)
			|| resolvedType.Contains(ErrorType, StringComparison.Ordinal)
			|| resolvedType.Contains(UnresolvedType, StringComparison.Ordinal);
	}

	string? GetLoweredCallReturnType(CallExpression call)
	{
		FunctionDefinition? function = call.Target switch
		{
			MemberReferenceExpression { Member: FunctionDefinition memberFunction } => memberFunction,
			MethodReferenceExpression { Candidates.Count: 1 } method => method.Candidates[0],
			_ => null
		};
		if (function is null)
			return null;

		return SubstituteGenericReturnType(function.ResolvedType, call.TypeArguments);
	}

	void RemoveLoweredInterfaceBaseTypes(ClassDefinition classDefinition)
	{
		for (int i = classDefinition.BaseTypes.Count - 1; i >= 0; i--)
		{
			if (TryGetInterfaceDefinition(classDefinition.BaseTypes[i], out _))
				classDefinition.BaseTypes.RemoveAt(i);
		}
	}

	void RemoveLoweredInterfaceBaseTypes(StructDefinition structDefinition)
	{
		for (int i = structDefinition.BaseTypes.Count - 1; i >= 0; i--)
		{
			if (TryGetInterfaceDefinition(structDefinition.BaseTypes[i], out _))
				structDefinition.BaseTypes.RemoveAt(i);
		}
	}

	StructDefinition LowerInterfaceDefinition(InterfaceDefinition definition)
	{
		if (loweredInterfaceStructs.TryGetValue(definition, out StructDefinition? existing))
			return existing;

		StructDefinition lowered = new()
		{
			SourceSyntax = definition.SourceSyntax,
			Name = definition.Name,
			Symbol = definition.Symbol,
			Export = definition.Export,
			Public = definition.Public,
			Extern = definition.Extern,
			ResolvedType = definition.ResolvedType ?? definition.Name
		};
		foreach (GenericParameter parameter in definition.GenericParameters)
			lowered.GenericParameters.Add(parameter);

		foreach (TypeReference baseType in definition.BaseTypes)
		{
			if (TryGetInterfaceDefinition(baseType, out InterfaceDefinition? baseInterface) && baseInterface is not null)
			{
				lowered.Fields.Add(new FieldDefinition
				{
					Name = baseInterface.Name,
					Symbol = baseInterface.Name,
					Type = InterfaceType(baseInterface),
					ResolvedType = baseInterface.Name
				});
			}
		}

		foreach (FunctionDefinition member in definition.Functions)
			lowered.Fields.Add(CreateInterfaceVTableEntryField(definition, member));

		loweredInterfaceStructs[definition] = lowered;
		return lowered;
	}

	StructDefinition EnsureInterfaceIndirectStruct(Module module, InterfaceDefinition interfaceDefinition)
	{
		if (interfaceIndirectStructs.TryGetValue(interfaceDefinition, out StructDefinition? existing))
			return existing;

		GenericParameter generic = new()
		{
			Name = "U",
			ResolvedType = "U",
			Constraint = PointerTo(VoidType())
		};
		generic.Constraint.ResolvedType = "void*";

		StructDefinition indirect = new()
		{
			Name = InterfaceIndirectName(interfaceDefinition),
			Symbol = InterfaceIndirectName(interfaceDefinition),
			Export = interfaceDefinition.Export,
			Public = interfaceDefinition.Public,
			Modifier = StructModifier.Fixed,
			ResolvedType = InterfaceIndirectName(interfaceDefinition)
		};
		indirect.GenericParameters.Add(generic);
		indirect.Fields.Add(new FieldDefinition
		{
			Name = "_vt",
			Symbol = "_vt",
			Type = PointerTo(new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name }),
			ResolvedType = "const " + interfaceDefinition.Name + "*"
		});
		indirect.Fields.Add(new FieldDefinition
		{
			Name = "ctx",
			Symbol = "ctx",
			Type = PointerTo(new GenericParameterTypeReference { Name = generic.Name, Parameter = generic, ResolvedType = generic.Name }),
			ResolvedType = $"{generic.Name}*"
		});

		interfaceIndirectStructs[interfaceDefinition] = indirect;
		module.Definitions.Add(indirect);
		generatedInterfaceDefinitions.Add(indirect);
		return indirect;
	}

	FieldDefinition CreateInterfaceVTableEntryField(InterfaceDefinition owner, FunctionDefinition member)
	{
		CallableTypeReference callable = new()
		{
			Kind = CallableKind.Function,
			ReturnType = CreateInterfaceEntryReturnType(member, owner),
			ResolvedType = BuildInterfaceEntryCallableType(owner, member)
		};
		if (member.Modifier != FunctionModifier.Constructor)
			callable.Parameters.Add(new ParameterDefinition
			{
				Name = "ctx",
				Symbol = "ctx",
				Type = InterfaceInstanceType(owner),
				ResolvedType = $"{owner.Name}**"
			});
		foreach (ParameterDefinition parameter in member.Parameters)
		{
			if (parameter is ThisParameterDefinition)
				continue;
			callable.Parameters.Add(CloneParameter(parameter));
		}
		return new FieldDefinition
		{
			Name = GetInterfaceEntryName(member),
			Symbol = GetInterfaceEntryName(member),
			Type = callable,
			ResolvedType = callable.ResolvedType
		};
	}

	InitializerExpression CreateInterfaceVTableInitializer(InterfaceImplementationLowering lowering, InterfaceDefinition interfaceDefinition)
	{
		InitializerExpression initializer = new()
		{
			ResolvedType = interfaceDefinition.Name
		};
		foreach (TypeReference baseType in interfaceDefinition.BaseTypes)
		{
			if (TryGetInterfaceDefinition(baseType, out InterfaceDefinition? baseInterface) && baseInterface is not null)
			{
				initializer.Items.Add(new InitializerItem
				{
					Target = InitializerTargetFor(baseInterface.Name),
					Expression = CreateInterfaceVTableInitializer(lowering, baseInterface),
					ResolvedType = baseInterface.Name
				});
			}
		}

		foreach (FunctionDefinition member in interfaceDefinition.Functions)
		{
			initializer.Items.Add(new InitializerItem
			{
				Target = InitializerTargetFor(GetInterfaceEntryName(member)),
				Expression = CreateInterfaceVTableEntryReference(lowering, interfaceDefinition, member),
				ResolvedType = BuildInterfaceEntryCallableType(interfaceDefinition, member)
			});
		}
		return initializer;
	}

	Expression CreateInterfaceVTableEntryReference(InterfaceImplementationLowering lowering, InterfaceDefinition interfaceDefinition, FunctionDefinition member)
	{
		if (lowering.DirectEntries && FindImplementationMethod(lowering.Type, member) is FunctionDefinition implementation)
		{
			EnsureImplementationMethodSymbol(lowering.Type, implementation);
			MethodReferenceExpression reference = new()
			{
				ResolvedType = BuildInterfaceEntryCallableType(interfaceDefinition, member)
			};
			reference.Candidates.Add(implementation);
			return reference;
		}

		MethodReferenceExpression thunk = new()
		{
			ResolvedType = BuildInterfaceEntryCallableType(interfaceDefinition, member)
		};
		if (TryFindGeneratedFunction(InterfaceThunkName(lowering.Type, interfaceDefinition, member), out FunctionDefinition? thunkFunction) && thunkFunction is not null)
			thunk.Candidates.Add(thunkFunction);
		return thunk;
	}

	BlockStatement CreateInterfaceThunkBody(FunctionDefinition thunk, InterfaceThunkLowering lowering)
	{
		BlockStatement body = new()
		{
			ResolvedType = "void"
		};
		ParameterDefinition ctx = thunk.Parameters[0];
		DeclarationStatement? indirect = null;
		DeclarationStatement instance;
		if (lowering.Implementation.IsStruct)
		{
			indirect = CreateGeneratedLocal("indirect", $"{InterfaceIndirectName(lowering.EntryInterface)}<{lowering.Implementation.Type.Name}>*", PointerTo(InterfaceIndirectType(lowering.EntryInterface, lowering.Implementation.Type)), CreateInterfaceInstanceFixup(lowering, ctx));
			indirect.Target.Names.Clear();
			indirect.Target.Names.Add("indirect");
			body.Statements.Add(indirect);
			instance = CreateGeneratedLocal("instance", $"{lowering.Implementation.Type.Name}*", PointerTo(InterfaceType(lowering.Implementation.Type)), new MemberReferenceExpression
			{
				Target = CreateVariableReference(indirect.Target, indirect.Target.ResolvedType ?? $"{InterfaceIndirectName(lowering.EntryInterface)}<{lowering.Implementation.Type.Name}>*"),
				Name = "ctx",
				ResolvedType = $"{lowering.Implementation.Type.Name}*"
			});
		}
		else
		{
			instance = CreateGeneratedLocal("instance", $"{lowering.Implementation.Type.Name}*", PointerTo(InterfaceType(lowering.Implementation.Type)), CreateInterfaceInstanceFixup(lowering, ctx));
		}
		instance.Target.Names.Clear();
		instance.Target.Names.Add("instance");
		body.Statements.Add(instance);

		CallExpression call = new()
		{
			ResolvedType = GetInterfaceEntryReturnType(lowering.Member, lowering.Implementation.Type),
			Target = new MemberReferenceExpression
			{
				Target = CreateVariableReference(instance.Target, instance.Target.ResolvedType ?? $"{lowering.Implementation.Type.Name}*"),
				Name = GetImplementationMethodName(lowering.Member),
				Member = FindImplementationMethod(lowering.Implementation.Type, lowering.Member),
				ResolvedType = GetInterfaceEntryReturnType(lowering.Member, lowering.Implementation.Type)
			}
		};
		foreach (ParameterDefinition parameter in thunk.Parameters)
		{
			if (parameter == ctx)
				continue;
			call.Arguments.Add(new ArgumentExpression
			{
				Value = CreateVariableReference(parameter, parameter.ResolvedType ?? ErrorType),
				ResolvedType = parameter.ResolvedType ?? ErrorType
			});
		}

		if (call.ResolvedType == "void")
			body.Statements.Add(new ExpressionStatement { Expression = call, ResolvedType = "void" });
		else
			body.Statements.Add(new ReturnStatement { Expression = call, ResolvedType = "void" });
		return body;
	}

	Expression CreateInterfaceInstanceFixup(InterfaceThunkLowering lowering, ParameterDefinition ctx)
	{
		if (lowering.Implementation.IsStruct)
		{
			return new CastExpression
			{
				Type = PointerTo(InterfaceIndirectType(lowering.EntryInterface, lowering.Implementation.Type)),
				Kind = CastKind.Type,
				ResolvedType = $"{InterfaceIndirectName(lowering.EntryInterface)}<{lowering.Implementation.Type.Name}>*",
				Expression = CreateVariableReference(ctx, ctx.ResolvedType ?? $"{lowering.EntryInterface.Name}**")
			};
		}

		if (lowering.Implementation.Field is null)
			return CreateVariableReference(ctx, ctx.ResolvedType ?? $"{lowering.EntryInterface.Name}**");

		return new CastExpression
		{
			Type = PointerTo(InterfaceType(lowering.Implementation.Type)),
			Kind = CastKind.Type,
			ResolvedType = $"{lowering.Implementation.Type.Name}*",
			Expression = new BinaryExpression
			{
				Left = new CastExpression
				{
					Type = PointerTo(new PrimitiveTypeReference { Type = PrimitiveType.Byte, ResolvedType = "byte" }),
					Kind = CastKind.Type,
					ResolvedType = "byte*",
					Expression = CreateVariableReference(ctx, ctx.ResolvedType ?? $"{lowering.EntryInterface.Name}**")
				},
				Operator = BinaryOperator.Subtract,
				Right = new CallExpression
				{
					Target = new NamedExpression { Name = "offsetof", ResolvedType = "fn nuint()" },
					ResolvedType = "nuint",
					Arguments =
					{
						new ArgumentExpression
						{
							Value = new MemberExpression
							{
								Target = new TypeReferenceExpression { Type = InterfaceType(lowering.Implementation.Type), ResolvedType = lowering.Implementation.Type.Name },
								Name = lowering.Implementation.Field.Name,
								ResolvedType = lowering.Implementation.Field.ResolvedType
							},
							ResolvedType = "nuint"
						}
					}
				},
				ResolvedType = "byte*"
			}
		};
	}

	void InsertInterfaceVTableInitialization(ClassDefinition classDefinition, InterfaceImplementationLowering lowering)
	{
		foreach (FunctionDefinition function in classDefinition.Functions)
		{
			if (function.Name != InitNewMethodName || function.Body is null)
				continue;

			function.Body.Statements.Insert(0, new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					Target = new MemberReferenceExpression
					{
						Target = new ThisExpression { ResolvedType = classDefinition.Name },
						Name = lowering.Field!.Name,
						Member = lowering.Field,
						ResolvedType = lowering.Field.ResolvedType
					},
					Operator = AssignmentOperator.Assign,
					Value = new VariableReferenceExpression
					{
						Variable = lowering.VTable,
						ResolvedType = lowering.VTable.ResolvedType
					},
					ResolvedType = lowering.Field.ResolvedType
				}
			});
		}
	}

	bool TryGetDirectInterface(TypeReference type, Dictionary<string, InterfaceDefinition> interfaces, out InterfaceDefinition? interfaceDefinition)
	{
		string? name = type switch
		{
			NamedTypeReference named when named.Qualifiers.Count == 0 => named.Name,
			TypeDefinitionReference { Definition: InterfaceDefinition definition } => definition.Name,
			_ => null
		};
		if (name is not null && interfaces.TryGetValue(name, out interfaceDefinition))
			return true;

		interfaceDefinition = null;
		return false;
	}

	bool TryGetInterfaceDefinition(TypeReference type, out InterfaceDefinition? interfaceDefinition)
	{
		if (type is TypeDefinitionReference { Definition: InterfaceDefinition definition })
		{
			interfaceDefinition = definition;
			return true;
		}
		if (type is NamedTypeReference named && typeDefinitions.TryGetValue(named.Name, out TypeDefinition? typeDefinition) && typeDefinition is InterfaceDefinition namedInterface)
		{
			interfaceDefinition = namedInterface;
			return true;
		}

		interfaceDefinition = null;
		return false;
	}

	IEnumerable<InterfaceDefinition> GetInterfaceAndBaseInterfaces(InterfaceDefinition definition, Dictionary<string, InterfaceDefinition> interfaces)
	{
		HashSet<InterfaceDefinition> seen = [];
		foreach (InterfaceDefinition baseInterface in GetBaseInterfacesForGeneration(definition, interfaces, seen))
			yield return baseInterface;
		if (seen.Add(definition))
			yield return definition;
	}

	IEnumerable<InterfaceDefinition> GetBaseInterfacesForGeneration(InterfaceDefinition definition, Dictionary<string, InterfaceDefinition> interfaces, HashSet<InterfaceDefinition> seen)
	{
		foreach (TypeReference baseType in definition.BaseTypes)
		{
			if (!TryGetDirectInterface(baseType, interfaces, out InterfaceDefinition? baseInterface) || baseInterface is null || !seen.Add(baseInterface))
				continue;

			foreach (InterfaceDefinition inherited in GetBaseInterfacesForGeneration(baseInterface, interfaces, seen))
				yield return inherited;
			yield return baseInterface;
		}
	}

	FunctionDefinition? FindImplementationMethod(TypeDefinition type, FunctionDefinition interfaceMember)
	{
		string name = GetImplementationMethodName(interfaceMember);
		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (GetCallableName(function) == name || function.Name == name)
				return function;
		}
		return null;
	}

	static void EnsureImplementationMethodSymbol(TypeDefinition type, FunctionDefinition function)
	{
		if (function.SymbolOverridden)
			return;

		if (string.IsNullOrWhiteSpace(function.Symbol) || function.Symbol == function.Name)
			function.Symbol = type.Name + "_" + GetCallableName(function).TrimStart('~');
	}

	static string GetImplementationMethodName(FunctionDefinition member)
	{
		if (IsDestructorFunction(member))
			return DestroyMethodName;

		return member.Modifier switch
		{
			FunctionModifier.Constructor => CreateMethodName,
			_ => GetCallableName(member)
		};
	}

	static string GetInterfaceEntryName(FunctionDefinition member)
	{
		if (IsDestructorFunction(member))
			return DestroyMethodName;

		return member.Modifier switch
		{
			FunctionModifier.Constructor => CreateMethodName,
			_ => GetCallableName(member)
		};
	}

	static string GetInterfaceEntryReturnType(FunctionDefinition member, TypeDefinition implementation)
	{
		return member.Modifier == FunctionModifier.Constructor ? $"{implementation.Name}*" : member.ResolvedType ?? member.ReturnType?.ResolvedType ?? ErrorType;
	}

	TypeReference CreateInterfaceEntryReturnType(FunctionDefinition member, InterfaceDefinition owner)
	{
		if (member.Modifier == FunctionModifier.Constructor)
			return PointerTo(new AnyTypeReference { ResolvedType = "any" });
		return CloneType(member.ReturnType) ?? VoidType();
	}

	static string BuildInterfaceEntryCallableType(InterfaceDefinition owner, FunctionDefinition member)
	{
		List<string> parameters = [];
		if (member.Modifier != FunctionModifier.Constructor)
			parameters.Add($"{owner.Name}**");
		foreach (ParameterDefinition parameter in member.Parameters)
		{
			if (parameter is ThisParameterDefinition)
				continue;
			parameters.Add(parameter.ResolvedType ?? ErrorType);
		}
		string returnType = member.Modifier == FunctionModifier.Constructor ? "any*" : member.ResolvedType ?? ErrorType;
		return $"fn {returnType}({string.Join(", ", parameters)})";
	}

	static string InterfaceFieldName(InterfaceDefinition interfaceDefinition)
	{
		return "_vt_" + interfaceDefinition.Name;
	}

	static string InterfaceVTableName(TypeDefinition type, InterfaceDefinition interfaceDefinition)
	{
		return type.Name + "_" + interfaceDefinition.Name;
	}

	static string InterfaceIndirectName(InterfaceDefinition interfaceDefinition)
	{
		return interfaceDefinition.Name + "_Indirect";
	}

	static string InterfaceThunkName(TypeDefinition type, InterfaceDefinition interfaceDefinition, FunctionDefinition member)
	{
		return type.Name + "_" + interfaceDefinition.Name + "_" + GetInterfaceEntryName(member);
	}

	const string VirtualTableFieldName = "_vt";

	static string VirtualTableTypeName(TypeDefinition type)
	{
		return "_" + type.Name;
	}

	static string VirtualTableVariableName(TypeDefinition type)
	{
		return "_" + type.Name + "__vt";
	}

	static string VirtualImplementationName(FunctionDefinition function)
	{
		return "_" + VirtualSlotName(function);
	}

	static string VirtualImplementationSymbol(TypeDefinition type, FunctionDefinition function)
	{
		return type.Name + "__" + VirtualSlotName(function);
	}

	static string VirtualSlotName(FunctionDefinition function)
	{
		return function.Name == DeleteMethodName || IsDestructorFunction(function) ? DeleteMethodName : GetCallableName(function);
	}

	static string BuildVirtualSlotCallableType(TypeDefinition owner, FunctionDefinition function)
	{
		List<string> parameters = [$"{owner.Name}*"];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter is ThisParameterDefinition)
				continue;
			parameters.Add(parameter.ResolvedType ?? ErrorType);
		}
		return $"fn {GetFunctionReturnTypeName(function)}({string.Join(", ", parameters)})";
	}

	static string GetFunctionReturnTypeName(FunctionDefinition function)
	{
		return function.ResolvedType ?? GetTypeReferenceName(function.ReturnType) ?? "void";
	}

	static string? GetTypeReferenceName(TypeReference? type)
	{
		return type switch
		{
			null => null,
			PrimitiveTypeReference primitive => GetPrimitiveTypeName(primitive.Type),
			NamedTypeReference named => BuildNamedTypeSourceName(named),
			TypeDefinitionReference definition => definition.Name,
			GenericParameterTypeReference generic => generic.Name,
			GenericTypeReference { Type: not null } generic => GetTypeReferenceName(generic.Type),
			ArrayTypeReference { ElementType: not null } array => GetTypeReferenceName(array.ElementType) + "[]",
			OptionalTypeReference { ElementType: not null } optional => GetTypeReferenceName(optional.ElementType) + "?",
			PointerTypeReference { ElementType: not null } pointer => GetTypeReferenceName(pointer.ElementType) + "*",
			ConstTypeReference { Type: not null } constant => "const " + GetTypeReferenceName(constant.Type),
			VolatileTypeReference { Type: not null } vol => "volatile " + GetTypeReferenceName(vol.Type),
			EscapedTypeReference { Type: not null } escaped => "escaped " + GetTypeReferenceName(escaped.Type),
			ScopedTypeReference { Type: not null } scoped => "scoped " + GetTypeReferenceName(scoped.Type),
			UnscopedTypeReference { Type: not null } unscoped => "unscoped " + GetTypeReferenceName(unscoped.Type),
			AutoTypeReference => AutoType,
			AnyTypeReference => "any",
			CopyableTypeReference => "copyable",
			_ => type.ResolvedType
		};
	}

	string GetRootVirtualTableFieldType(ClassDefinition owner)
	{
		ClassDefinition root = owner;
		while (GetDirectBaseClass(root) is ClassDefinition baseClass && virtualClassLowerings.ContainsKey(baseClass))
			root = baseClass;
		return $"{VirtualTableTypeName(root)}*";
	}

	FunctionDefinition? FindClosestVirtualImplementation(ClassDefinition owner, FunctionDefinition slotDeclaration)
	{
		foreach (ClassDefinition candidate in EnumerateClassAndBases(owner))
		{
			foreach (FunctionDefinition function in candidate.Functions)
			{
				if (!virtualImplementations.TryGetValue(function, out FunctionDefinition? implementation))
					continue;
				if (VirtualSlotName(function) == VirtualSlotName(slotDeclaration))
					return implementation;
			}
		}
		return null;
	}

	IEnumerable<ClassDefinition> EnumerateClassAndBases(ClassDefinition owner)
	{
		for (ClassDefinition? current = owner; current is not null; current = GetDirectBaseClass(current))
			yield return current;
	}

	static InitializerTarget InitializerTargetFor(string name)
	{
		InitializerTarget target = new() { ResolvedType = TargetType };
		target.Parts.Add(new InitializerTargetPart { Name = name, ResolvedType = TargetType });
		return target;
	}

	bool TryFindGeneratedFunction(string name, out FunctionDefinition? function)
	{
		function = null;
		foreach (Definition definition in generatedInterfaceDefinitions)
		{
			if (definition is FunctionDefinition candidate && candidate.Name == name)
			{
				function = candidate;
				return true;
			}
		}
		return false;
	}

	static TypeReference InterfaceType(TypeDefinition type)
	{
		return new TypeDefinitionReference
		{
			Name = type.Name,
			Definition = type,
			ResolvedType = type.Name
		};
	}

	static TypeReference InterfaceInstanceType(InterfaceDefinition interfaceDefinition)
	{
		return PointerTo(PointerTo(InterfaceType(interfaceDefinition)));
	}

	static TypeReference InterfaceIndirectType(InterfaceDefinition interfaceDefinition, TypeDefinition implementation)
	{
		TypeDefinitionReference reference = new()
		{
			Name = InterfaceIndirectName(interfaceDefinition),
			ResolvedType = $"{InterfaceIndirectName(interfaceDefinition)}<{implementation.Name}>"
		};
		reference.TypeArguments.Add(InterfaceType(implementation));
		return reference;
	}

	void GenerateLifecycleMethods(Module module)
	{
		foreach (Definition definition in module.Definitions)
			GenerateLifecycleMethods(definition);
	}

	void GenerateLifecycleMethods(Definition definition)
	{
		switch (definition)
		{
			case ClassDefinition classDefinition:
				GenerateLifecycleMethods(classDefinition, classDefinition.Functions);
				break;

			case StructDefinition structDefinition:
				GenerateLifecycleMethods(structDefinition, structDefinition.Functions);
				break;
		}
	}

	void GenerateLifecycleMethods(TypeDefinition type, List<FunctionDefinition> functions)
	{
		if (type is ClassDefinition classDefinition
			&& classDefinition.Export is not null
			&& classDefinition.Modifier != ClassModifier.Abstract
			&& !HasConstructor(functions))
		{
			functions.Add(CreateImplicitExportedParameterlessConstructor(classDefinition));
		}

		List<FunctionDefinition> generated = [];
		foreach (FunctionDefinition function in functions.ToArray())
		{
			if (function.Modifier == FunctionModifier.Constructor)
			{
				FunctionDefinition initNew = CreateInitNewMethod(type, function);
				generated.Add(initNew);
				if (type is not ClassDefinition { Modifier: ClassModifier.Abstract })
				{
					FunctionDefinition create = CreateCreateMethod(type, function, initNew);
					generated.Add(create);
				}
				function.Body = null;
			}
			else if (IsDestructorFunction(function))
			{
				FunctionDefinition opDelete = CreateDeleteMethod(type, function);
				generated.Add(opDelete);
				if (function.Modifier is not FunctionModifier.Override and not FunctionModifier.Sealed)
				{
					FunctionDefinition destroy = CreateDestroyMethod(type, function, opDelete);
					generated.Add(destroy);
				}
				function.Body = null;
			}
		}

		functions.AddRange(generated);
	}

	static bool HasConstructor(List<FunctionDefinition> functions)
	{
		foreach (FunctionDefinition function in functions)
			if (function.Modifier == FunctionModifier.Constructor)
				return true;
		return false;
	}

	FunctionDefinition CreateImplicitExportedParameterlessConstructor(ClassDefinition classDefinition)
	{
		return new FunctionDefinition
		{
			SourceSyntax = classDefinition.SourceSyntax,
			Name = classDefinition.Name,
			Symbol = classDefinition.Name,
			Export = "export",
			Modifier = FunctionModifier.Constructor,
			ReturnType = TypeReferenceFor(classDefinition),
			ResolvedType = classDefinition.Name,
			Body = new BlockStatement
			{
				SourceSyntax = classDefinition.SourceSyntax,
				ResolvedType = "void"
			}
		};
	}

	FunctionDefinition CreateInitNewMethod(TypeDefinition type, FunctionDefinition constructor)
	{
		FunctionDefinition method = new()
		{
			SourceSyntax = constructor.SourceSyntax,
			Name = InitNewMethodName,
			Symbol = $"{type.Name}_{InitNewMethodName}",
			Export = constructor.Export,
			Public = constructor.Public,
			Extern = constructor.Extern,
			ReturnType = VoidType(),
			ResolvedType = "void",
			Body = constructor.Body
		};
		CopyLifecycleParameters(constructor.Parameters, method.Parameters);
		if (HasWithinParameter(method) && method.Body is BlockStatement block)
			block.Statements.Insert(0, CreateResolvedAllocatorLocal(GetWithinParameter(method)));
		return method;
	}

	FunctionDefinition CreateCreateMethod(TypeDefinition type, FunctionDefinition constructor, FunctionDefinition initNew)
	{
		TypeReference typeReference = TypeReferenceFor(type);
		FunctionDefinition method = new()
		{
			SourceSyntax = constructor.SourceSyntax,
			Name = CreateMethodName,
			Symbol = $"{type.Name}_{CreateMethodName}",
			Export = constructor.Export,
			Public = constructor.Public,
			Extern = constructor.Extern,
			Modifier = FunctionModifier.Static,
			ReturnType = PointerTo(CloneType(typeReference)!),
			ResolvedType = $"{type.Name}*"
		};
		CopyLifecycleParameters(constructor.Parameters, method.Parameters);
		bool createWithAllocator = HasWithinParameter(method) || HasCreateWithAllocatorAttribute(type);
		if (createWithAllocator && !HasWithinParameter(method))
			method.Parameters.Add(CreateAllocatorParameter());
		if (method.Extern is not null)
			return method;

		method.Body = new BlockStatement
		{
			ResolvedType = "void"
		};
		BlockStatement body = method.Body;
		ParameterDefinition? allocatorParameter = GetAllocatorParameter(method);
		DeclarationStatement? resolvedAllocatorLocal = null;
		if (createWithAllocator)
		{
			resolvedAllocatorLocal = CreateResolvedAllocatorLocal(allocatorParameter);
			body.Statements.Add(resolvedAllocatorLocal);
		}
		string localName = NewGeneratedLocalName("created");
		Expression? allocationAllocator = resolvedAllocatorLocal is null
			? null
			: CreateVariableReference(resolvedAllocatorLocal.Target, resolvedAllocatorLocal.Target.ResolvedType ?? allocatorParameter?.ResolvedType ?? "Allocator*");
		DeclarationStatement local = CreateGeneratedLocal(localName, $"{type.Name}*", PointerTo(CloneType(typeReference)!), CreateAllocCall(typeReference, allocationAllocator, method.SourceSyntax));
		body.Statements.Add(local);
		IfStatement guard = new()
		{
			ResolvedType = "void",
			Condition = new BinaryExpression
			{
				Left = CreateVariableReference(local.Target, $"{type.Name}*"),
				Operator = BinaryOperator.NotEqual,
				Right = new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "#NULL" },
				ResolvedType = "bool"
			},
			Body = new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = CreateInitNewCall(CreateVariableReference(local.Target, $"{type.Name}*"), initNew, method.ArgumentsFromParameters(skipAllocator: !HasWithinParameter(initNew)), method.SourceSyntax, allocatorParameter is null ? null : CreateVariableReference(allocatorParameter, allocatorParameter.ResolvedType ?? "Allocator*"))
			}
		};
		body.Statements.Add(guard);
		body.Statements.Add(new ReturnStatement
		{
			Expression = CreateVariableReference(local.Target, $"{type.Name}*"),
			ResolvedType = "void"
		});
		return method;
	}

	FunctionDefinition CreateDeleteMethod(TypeDefinition type, FunctionDefinition destructor)
	{
		FunctionDefinition method = new()
		{
			SourceSyntax = destructor.SourceSyntax,
			Name = DeleteMethodName,
			Symbol = $"{type.Name}_op_delete",
			Export = destructor.Export,
			Public = destructor.Public,
			Extern = destructor.Extern,
			Modifier = GetDeleteMethodModifier(destructor),
			ReturnType = VoidType(),
			ResolvedType = "void",
			Body = destructor.Body
		};
		CopyLifecycleParameters(destructor.Parameters, method.Parameters);
		return method;
	}

	static FunctionModifier GetDeleteMethodModifier(FunctionDefinition destructor)
	{
		return destructor.Modifier is FunctionModifier.Abstract
			or FunctionModifier.Virtual
			or FunctionModifier.Override
			or FunctionModifier.Sealed
			? destructor.Modifier
			: FunctionModifier.None;
	}

	FunctionDefinition CreateDestroyMethod(TypeDefinition type, FunctionDefinition destructor, FunctionDefinition opDelete)
	{
		FunctionDefinition method = new()
		{
			SourceSyntax = destructor.SourceSyntax,
			Name = DestroyMethodName,
			Symbol = $"{type.Name}_{DestroyMethodName}",
			Export = destructor.Export,
			Public = destructor.Public,
			Extern = destructor.Extern,
			ReturnType = VoidType(),
			ResolvedType = "void"
		};
		bool destroyWithAllocator = HasWithinParameter(destructor) || HasCreateWithAllocatorAttribute(type);
		if (destroyWithAllocator)
			method.Parameters.Add(CreateAllocatorParameter());
		if (method.Extern is not null)
			return method;

		method.Body = new BlockStatement
		{
			ResolvedType = "void"
		};
		BlockStatement body = method.Body;
		ThisExpression target = new() { SourceSyntax = destructor.SourceSyntax, ResolvedType = $"{type.Name}*" };
		body.Statements.Add(new ExpressionStatement
		{
			SourceSyntax = destructor.SourceSyntax,
			ResolvedType = "void",
			Expression = CreateDestructorCall(target, opDelete, GetAllocatorParameter(method) is ParameterDefinition allocatorParameter ? CreateVariableReference(allocatorParameter, allocatorParameter.ResolvedType ?? "Allocator*") : null)
		});
		DeclarationStatement? resolvedAllocatorLocal = null;
		if (destroyWithAllocator)
		{
			resolvedAllocatorLocal = CreateResolvedAllocatorLocal(GetAllocatorParameter(method));
			body.Statements.Add(resolvedAllocatorLocal);
		}
		body.Statements.Add(new ExpressionStatement
		{
			SourceSyntax = destructor.SourceSyntax,
			ResolvedType = "void",
			Expression = CreateFreeCall(
				new ThisExpression { SourceSyntax = destructor.SourceSyntax, ResolvedType = $"{type.Name}*" },
				resolvedAllocatorLocal is null ? null : CreateVariableReference(resolvedAllocatorLocal.Target, resolvedAllocatorLocal.Target.ResolvedType ?? GetAllocatorParameter(method)?.ResolvedType ?? "Allocator*"))
		});
		return method;
	}
}
