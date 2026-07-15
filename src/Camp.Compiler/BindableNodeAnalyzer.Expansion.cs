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
		if (virtualClassLowerings.ContainsKey(classDefinition))
			return;

		ClassDefinition? baseClass = GetDirectBaseClass(classDefinition);
		if (baseClass is not null && IsVirtualClassParticipant(baseClass) && !virtualClassLowerings.ContainsKey(baseClass))
			GenerateVirtualClassDeclarations(module, baseClass);

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

		VariableDefinition vtable = generatedDeclarations.Variable(GeneratedDeclarationCategory.VirtualDispatch, "virtual class vtable", classDefinition);
		vtable.Name = VirtualTableVariableName(classDefinition);
		vtable.Symbol = VirtualTableVariableName(classDefinition);
		vtable.Type = TypeReferenceFor(vtableType);
		vtable.ResolvedType = vtableType.Name;
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
		FunctionDefinition implementation = generatedDeclarations.Function(GeneratedDeclarationCategory.VirtualDispatch, "virtual implementation thunk", source);
		implementation.SourceSyntax = source.SourceSyntax;
		implementation.Name = VirtualImplementationName(source);
		implementation.Symbol = VirtualImplementationSymbol(owner, source);
		implementation.Export = source.Export;
		implementation.Internal = source.Internal;
		implementation.ReturnType = CloneType(source.ReturnType);
		implementation.ResolvedType = source.ResolvedType;
		implementation.Body = source.Body;
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
		if (!generatedClassInterfaceDeclarations.Add(classDefinition))
			return;

		ClassDefinition? baseClass = GetDirectBaseClass(classDefinition);
		if (baseClass is not null)
			GenerateClassInterfaceDeclarations(module, baseClass, interfaces);

		List<InterfaceImplementationLowering> implementations = [];
		int interfaceIndex = classDefinition.Fields.Count > 0 && classDefinition.Fields[0].Name == VirtualTableFieldName ? 1 : 0;
		foreach (TypeReference baseType in classDefinition.BaseTypes)
		{
			if (!TryGetDirectInterface(baseType, interfaces, out InterfaceDefinition? interfaceDefinition) || interfaceDefinition is null)
				continue;

			if (classDefinition.Extern is not null)
			{
				VariableDefinition externVTable = new()
				{
					Name = InterfaceVTableName(classDefinition, interfaceDefinition),
					Symbol = InterfaceVTableName(classDefinition, interfaceDefinition),
					Export = classDefinition.Export is not null && interfaceDefinition.Export is not null ? "export" : null,
					Internal = (classDefinition.Export is null || interfaceDefinition.Export is null) && IsExternallyVisible(classDefinition) && IsExternallyVisible(interfaceDefinition) ? "internal" : null,
					Extern = "extern",
					Type = PointerTo(new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name }),
					ResolvedType = "const " + interfaceDefinition.Name + "*"
				};
				module.Definitions.Add(externVTable);
				generatedInterfaceDefinitions.Add(externVTable);

				VariableDefinition externStoragePlaceholder = new()
				{
					Name = InterfaceVTableName(classDefinition, interfaceDefinition) + "__storage",
					Symbol = InterfaceVTableName(classDefinition, interfaceDefinition) + "__storage",
					Type = new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name },
					ResolvedType = "const " + interfaceDefinition.Name
				};
					InterfaceImplementationLowering externLowering = new(classDefinition, interfaceDefinition, Field: null, externVTable, externStoragePlaceholder, ObjectVTableStorage: null, DirectEntries: false, IsStruct: false, IsExternClass: true);
				implementations.Add(externLowering);
				if (FindImportedInterfaceAccessor(classDefinition, interfaceDefinition) is null)
					classDefinition.Functions.Add(CreateInterfaceAccessorDeclaration(classDefinition, externLowering));
				continue;
			}

			FieldDefinition field = generatedDeclarations.Field(GeneratedDeclarationCategory.Interface, "interface vtable field", classDefinition);
			field.Name = InterfaceFieldName(interfaceDefinition);
			field.Symbol = InterfaceFieldName(interfaceDefinition);
			field.Type = PointerTo(new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name });
			field.ResolvedType = "const " + interfaceDefinition.Name + "*";
			classDefinition.Fields.Insert(interfaceIndex, field);

			VariableDefinition vtableStorage = generatedDeclarations.Variable(GeneratedDeclarationCategory.Interface, "interface vtable storage", classDefinition);
			vtableStorage.Name = InterfaceVTableName(classDefinition, interfaceDefinition) + "__storage";
			vtableStorage.Symbol = InterfaceVTableName(classDefinition, interfaceDefinition) + "__storage";
			vtableStorage.Type = new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name };
			vtableStorage.ResolvedType = "const " + interfaceDefinition.Name;
			module.Definitions.Add(vtableStorage);
			generatedInterfaceDefinitions.Add(vtableStorage);

			VariableDefinition objectVTableStorage = generatedDeclarations.Variable(GeneratedDeclarationCategory.Interface, "interface object vtable storage", classDefinition);
			objectVTableStorage.Name = InterfaceVTableName(classDefinition, interfaceDefinition) + "__object_storage";
			objectVTableStorage.Symbol = InterfaceVTableName(classDefinition, interfaceDefinition) + "__object_storage";
			objectVTableStorage.Type = new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name };
			objectVTableStorage.ResolvedType = "const " + interfaceDefinition.Name;
			module.Definitions.Add(objectVTableStorage);
			generatedInterfaceDefinitions.Add(objectVTableStorage);

			VariableDefinition vtable = generatedDeclarations.Variable(GeneratedDeclarationCategory.Interface, "interface vtable export", classDefinition);
			vtable.Name = InterfaceVTableName(classDefinition, interfaceDefinition);
			vtable.Symbol = InterfaceVTableName(classDefinition, interfaceDefinition);
			vtable.Export = classDefinition.Export is not null && interfaceDefinition.Export is not null ? "export" : null;
			vtable.Internal = (classDefinition.Export is null || interfaceDefinition.Export is null) && IsExternallyVisible(classDefinition) && IsExternallyVisible(interfaceDefinition) ? "internal" : null;
			vtable.Type = PointerTo(new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name });
			vtable.ResolvedType = "const " + interfaceDefinition.Name + "*";
			vtable.InitialValue = new UnaryExpression
			{
				Operator = UnaryOperator.AddressOf,
				Operand = new VariableReferenceExpression { Variable = vtableStorage, ResolvedType = vtableStorage.ResolvedType },
				ResolvedType = "const " + interfaceDefinition.Name + "*"
			};
			module.Definitions.Add(vtable);
			generatedInterfaceDefinitions.Add(vtable);

			InterfaceImplementationLowering lowering = new(classDefinition, interfaceDefinition, field, vtable, vtableStorage, objectVTableStorage, DirectEntries: false, IsStruct: false);
			implementations.Add(lowering);
			classDefinition.Functions.Add(CreateInterfaceAccessorDeclaration(classDefinition, lowering));
			GenerateInterfaceThunks(module, lowering, interfaceDefinition, interfaces);
			interfaceIndex++;
		}

		if (implementations.Count > 0)
		{
			if (classDefinition.Extern is null)
				EnsureInterfaceInitNewMethod(classDefinition);
			classInterfaceLowerings[classDefinition] = implementations;
		}

		EnsureInheritedInitNewMethod(classDefinition, baseClass);
	}

	FunctionDefinition CreateInterfaceAccessorDeclaration(ClassDefinition classDefinition, InterfaceImplementationLowering lowering)
	{
		InterfaceDefinition interfaceDefinition = lowering.Interface;
		TypeReference sourceReturnType = PointerTo(new ConstOfTypeReference
		{
			AnchorName = "this",
			Type = InterfaceType(interfaceDefinition),
			ResolvedType = "const " + interfaceDefinition.Name
		});
		FunctionDefinition accessor = generatedDeclarations.Function(GeneratedDeclarationCategory.Interface, "interface accessor", classDefinition);
		accessor.Name = InterfaceAccessorName(interfaceDefinition);
		accessor.Symbol = EffectiveTypeSymbol(classDefinition) + "_" + InterfaceAccessorName(interfaceDefinition);
		accessor.Export = classDefinition.Export is not null && interfaceDefinition.Export is not null ? "export" : null;
		accessor.Internal = (classDefinition.Export is null || interfaceDefinition.Export is null) && IsExternallyVisible(classDefinition) && IsExternallyVisible(interfaceDefinition) ? "internal" : null;
		accessor.Extern = lowering.IsExternClass ? "extern" : null;
		accessor.ReturnType = sourceReturnType;
		accessor.ResolvedType = $"{interfaceDefinition.Name}**";
		accessor.EffectiveThisParameter = new ThisParameterDefinition
		{
			Name = "this",
			Symbol = "this",
			ResolvedType = "const " + classDefinition.Name + "*"
		};
		accessor.EffectiveThisParameter.Attributes.Add(new AttributeConstructor { Name = "const" });
		return accessor;
	}

	FunctionDefinition? FindImportedInterfaceAccessor(ClassDefinition classDefinition, InterfaceDefinition interfaceDefinition)
	{
		string accessorName = InterfaceAccessorName(interfaceDefinition);
		string accessorSymbol = EffectiveTypeSymbol(classDefinition) + "_" + accessorName;
		foreach (FunctionDefinition function in classDefinition.Functions)
		{
			if (!function.IsApiHeader || function.Extern is null)
				continue;
			if (function.Name == accessorName)
				return function;
		}
		return null;
	}

	void EnsureInheritedInitNewMethod(ClassDefinition classDefinition, ClassDefinition? baseClass)
	{
		if (baseClass is null || FindGeneratedInitNewMethod(baseClass) is not FunctionDefinition baseInitNew)
			return;

		EnsureInterfaceInitNewMethod(classDefinition);
		FunctionDefinition? initNew = FindGeneratedInitNewMethod(classDefinition);
		if (initNew?.Body is null || initNew.Body.Statements.Count > 0)
			return;

		CallExpression call = new()
		{
			Target = CreateBaseInitNewReference(baseInitNew, baseInitNew, classDefinition, baseClass),
			ResolvedType = "void"
		};
		callTargets[call] = baseInitNew;
		initNew.Body.Statements.Add(new ExpressionStatement
		{
			Expression = call,
			ResolvedType = "void"
		});
	}

	void GenerateStructInterfaceDeclarations(Module module, StructDefinition structDefinition, Dictionary<string, InterfaceDefinition> interfaces)
	{
		List<InterfaceImplementationLowering> implementations = [];
		foreach (TypeReference baseType in structDefinition.BaseTypes)
		{
			if (!TryGetDirectInterface(baseType, interfaces, out InterfaceDefinition? interfaceDefinition) || interfaceDefinition is null)
				continue;

			EnsureInterfaceIndirectStruct(module, interfaceDefinition);
			VariableDefinition vtableStorage = generatedDeclarations.Variable(GeneratedDeclarationCategory.Interface, "interface vtable storage", structDefinition);
			vtableStorage.Name = InterfaceVTableName(structDefinition, interfaceDefinition) + "__storage";
			vtableStorage.Symbol = InterfaceVTableName(structDefinition, interfaceDefinition) + "__storage";
			vtableStorage.Type = new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name };
			vtableStorage.ResolvedType = "const " + interfaceDefinition.Name;
			module.Definitions.Add(vtableStorage);
			generatedInterfaceDefinitions.Add(vtableStorage);

			VariableDefinition vtable = generatedDeclarations.Variable(GeneratedDeclarationCategory.Interface, "interface vtable export", structDefinition);
			vtable.Name = InterfaceVTableName(structDefinition, interfaceDefinition);
			vtable.Symbol = InterfaceVTableName(structDefinition, interfaceDefinition);
			vtable.Type = PointerTo(new ConstTypeReference { Type = InterfaceType(interfaceDefinition), ResolvedType = "const " + interfaceDefinition.Name });
			vtable.ResolvedType = "const " + interfaceDefinition.Name + "*";
			vtable.InitialValue = new UnaryExpression
			{
				Operator = UnaryOperator.AddressOf,
				Operand = new VariableReferenceExpression { Variable = vtableStorage, ResolvedType = vtableStorage.ResolvedType },
				ResolvedType = "const " + interfaceDefinition.Name + "*"
			};
			module.Definitions.Add(vtable);
			generatedInterfaceDefinitions.Add(vtable);

			InterfaceImplementationLowering lowering = new(structDefinition, interfaceDefinition, Field: null, vtable, vtableStorage, ObjectVTableStorage: null, DirectEntries: false, IsStruct: true);
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

		FunctionDefinition initNew = generatedDeclarations.Function(GeneratedDeclarationCategory.Interface, "interface init-new helper", classDefinition);
		initNew.Name = InitNewMethodName;
		initNew.Symbol = $"{classDefinition.Name}_{InitNewMethodName}";
		initNew.ReturnType = VoidType();
		initNew.ResolvedType = "void";
		initNew.Body = new BlockStatement { ResolvedType = "void" };
		classDefinition.Functions.Add(initNew);
	}

	void GenerateInterfaceThunks(Module module, InterfaceImplementationLowering lowering, InterfaceDefinition interfaceDefinition, Dictionary<string, InterfaceDefinition> interfaces)
	{
		foreach (InterfaceDefinition implementedInterface in GetInterfaceAndBaseInterfaces(interfaceDefinition, interfaces))
		{
			foreach (FunctionDefinition member in implementedInterface.Functions)
			{
				if (lowering.DirectEntries)
					continue;
				if (member.InterfaceSlotInitializer is not null && FindImplementationMethod(lowering.Type, member) is null)
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
		FunctionDefinition thunk = generatedDeclarations.Function(GeneratedDeclarationCategory.Interface, "interface thunk", member);
		thunk.Name = InterfaceThunkName(lowering.Type, entryInterface, member);
		thunk.Symbol = InterfaceThunkName(lowering.Type, entryInterface, member);
		thunk.ReturnType = member.Modifier == FunctionModifier.Constructor
			? new AnyTypeReference { ResolvedType = "any" }
			: EraseConstOfTypeReference(member.ReturnType) ?? VoidType();
		thunk.ResolvedType = member.Modifier == FunctionModifier.Constructor
			? "any"
			: GetInterfaceEntryReturnType(member, lowering.Type);
		if (member.Modifier != FunctionModifier.Constructor)
		{
			thunk.Parameters.Add(new ParameterDefinition
			{
				Name = "ctx",
				Symbol = "ctx",
				Type = InterfaceInstanceType(entryInterface),
				ResolvedType = $"{entryInterface.Name}**"
			});
		}
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
				{
				if (!lowering.IsExternClass)
					{
						lowering.VTableStorage.InitialValue = CreateInterfaceVTableInitializer(lowering, lowering.Interface, directEntries: true);
						if (lowering.ObjectVTableStorage is not null)
							lowering.ObjectVTableStorage.InitialValue = CreateInterfaceVTableInitializer(lowering, lowering.Interface, directEntries: false);
				}
				RefreshInterfaceAccessorAbiType(classDefinition, lowering.Interface);
				}

			for (int i = lowerings.Count - 1; i >= 0; i--)
			{
				if (!lowerings[i].IsExternClass)
					InsertInterfaceVTableInitialization(classDefinition, lowerings[i]);
			}
		}

		foreach ((StructDefinition _, List<InterfaceImplementationLowering> lowerings) in structInterfaceLowerings)
		{
			foreach (InterfaceImplementationLowering lowering in lowerings)
				lowering.VTableStorage.InitialValue = CreateInterfaceVTableInitializer(lowering, lowering.Interface, directEntries: false);
		}

		foreach ((FunctionDefinition thunk, InterfaceThunkLowering lowering) in interfaceThunkLowerings)
			thunk.Body = CreateInterfaceThunkBody(thunk, lowering);
	}

	void RefreshInterfaceAccessorAbiType(ClassDefinition classDefinition, InterfaceDefinition interfaceDefinition)
	{
		string accessorName = InterfaceAccessorName(interfaceDefinition);
		foreach (FunctionDefinition function in classDefinition.Functions)
		{
			if (function.Name != accessorName || function.SourceSyntax is not null && !function.IsApiHeader)
				continue;

			function.ResolvedType = $"{interfaceDefinition.Name}**";
			if (function.ReturnType is not null)
				function.ReturnType.ResolvedType = $"{interfaceDefinition.Name}**";
			if (function.Body is null && function.Extern is null && classInterfaceLowerings.TryGetValue(classDefinition, out List<InterfaceImplementationLowering>? lowerings))
			{
			foreach (InterfaceImplementationLowering lowering in lowerings)
				{
					if (!ReferenceEquals(lowering.Interface, interfaceDefinition))
						continue;
					if (lowering.Field is null)
						continue;
					function.Body = new BlockStatement
					{
						ResolvedType = "void",
						Statements =
						{
							new ReturnStatement
							{
								Expression = new CastExpression
								{
									Kind = CastKind.Type,
									Type = InterfaceInstanceType(interfaceDefinition),
									Expression = AddressOfInterfaceField(new ThisExpression { ResolvedType = classDefinition.Name }, lowering.Field!),
									ResolvedType = $"{interfaceDefinition.Name}**"
								},
								ResolvedType = "void"
							}
						}
				};
					break;
				}
			}
		}
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

			case FunctionDefinition function when function.ReturnType is not null:
				function.ResolvedType = function.ReturnType.ResolvedType;
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
		{
			if (!NeedsResolvedTypeRefresh(call.ResolvedType))
				return call.ResolvedType;
			if (call.Target is not null && TryGetCallableShape(call.Target.ResolvedType, out CallableShape callable))
				return callable.ReturnType;
			return null;
		}

		if (!NeedsResolvedTypeRefresh(call.ResolvedType) && !IsInterfacePointerType(function.ReturnType))
			return call.ResolvedType;
		return SubstituteGenericReturnType(function.ResolvedType, call.TypeArguments);
	}

	void RemoveLoweredInterfaceBaseTypes(ClassDefinition classDefinition)
	{
		for (int i = classDefinition.BaseTypes.Count - 1; i >= 0; i--)
		{
			if (TryGetInterfaceDefinition(classDefinition.BaseTypes[i], out _))
			{
				classDefinition.LoweredInterfaceBaseTypes.Insert(0, classDefinition.BaseTypes[i]);
				classDefinition.BaseTypes.RemoveAt(i);
			}
		}
	}

	void RemoveLoweredInterfaceBaseTypes(StructDefinition structDefinition)
	{
		for (int i = structDefinition.BaseTypes.Count - 1; i >= 0; i--)
		{
			if (TryGetInterfaceDefinition(structDefinition.BaseTypes[i], out _))
			{
				structDefinition.LoweredInterfaceBaseTypes.Insert(0, structDefinition.BaseTypes[i]);
				structDefinition.BaseTypes.RemoveAt(i);
			}
		}
	}

	StructDefinition LowerInterfaceDefinition(InterfaceDefinition definition)
	{
		if (loweredInterfaceStructs.TryGetValue(definition, out StructDefinition? existing))
			return existing;

		StructDefinition lowered = new()
		{
			SourceSyntax = definition.SourceSyntax,
			SourceInterface = definition,
			Name = definition.Name,
			Symbol = definition.Symbol,
			Export = definition.Export,
			Internal = definition.Internal,
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
			Internal = interfaceDefinition.Internal,
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

	InitializerExpression CreateInterfaceVTableInitializer(InterfaceImplementationLowering lowering, InterfaceDefinition interfaceDefinition, bool directEntries)
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
					Expression = CreateInterfaceVTableInitializer(lowering, baseInterface, directEntries),
					ResolvedType = baseInterface.Name
				});
			}
		}

		foreach (FunctionDefinition member in interfaceDefinition.Functions)
		{
			initializer.Items.Add(new InitializerItem
			{
				Target = InitializerTargetFor(GetInterfaceEntryName(member)),
					Expression = CreateInterfaceVTableEntryReference(lowering, interfaceDefinition, member, directEntries),
				ResolvedType = BuildInterfaceEntryCallableType(interfaceDefinition, member)
			});
		}
		return initializer;
	}

	Expression CreateInterfaceVTableEntryReference(InterfaceImplementationLowering lowering, InterfaceDefinition interfaceDefinition, FunctionDefinition member, bool directEntries)
	{
		FunctionDefinition? implementation = FindImplementationMethod(lowering.Type, member);
		if (directEntries && implementation is not null)
		{
			EnsureImplementationMethodSymbol(lowering.Type, implementation);
			MethodReferenceExpression reference = new()
			{
				ResolvedType = BuildFlattenedFunctionValueType(implementation, $"{lowering.Type.Name}*")
			};
			reference.Candidates.Add(implementation);
			return reference;
		}

		if (implementation is null && member.InterfaceSlotInitializer is not null)
		{
			if (member.InterfaceSlotInitializerKind == InterfaceSlotInitializerKind.Null)
			{
				return new LiteralExpression
				{
					Kind = LiteralKind.Null,
					Text = "null",
					ResolvedType = BuildInterfaceEntryCallableType(interfaceDefinition, member)
				};
			}

			if (member.InterfaceSlotInitializerKind == InterfaceSlotInitializerKind.Function && member.InterfaceSlotInitializerTarget is not null)
			{
				MethodReferenceExpression reference = new()
				{
					ResolvedType = BuildInterfaceEntryCallableType(interfaceDefinition, member)
				};
				reference.Candidates.Add(member.InterfaceSlotInitializerTarget);
				return reference;
			}
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
		if (lowering.Member.Modifier == FunctionModifier.Constructor)
		{
			FunctionDefinition? constructorImplementation = FindImplementationMethod(lowering.Implementation.Type, lowering.Member);
			if (constructorImplementation is not null)
				EnsureImplementationMethodSymbol(lowering.Implementation.Type, constructorImplementation);

			CallExpression constructorCall = new()
			{
				ResolvedType = GetInterfaceEntryReturnType(lowering.Member, lowering.Implementation.Type),
				Target = new MethodReferenceExpression
				{
					ResolvedType = BuildInterfaceEntryCallableType(lowering.EntryInterface, lowering.Member)
				}
			};
			if (constructorImplementation is not null && constructorCall.Target is MethodReferenceExpression reference)
				reference.Candidates.Add(constructorImplementation);

			foreach (ParameterDefinition parameter in thunk.Parameters)
			{
				constructorCall.Arguments.Add(new ArgumentExpression
				{
					Value = CreateVariableReference(parameter, parameter.ResolvedType ?? ErrorType),
					ResolvedType = parameter.ResolvedType ?? ErrorType
				});
			}
			if (constructorImplementation is not null)
			{
				callTargets[constructorCall] = constructorImplementation;
				ExpandParamsArguments(constructorCall);
				LowerCallArgumentConversions(constructorCall);
			}

			body.Statements.Add(new ReturnStatement { Expression = constructorCall, ResolvedType = "void" });
			return body;
		}

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

		FunctionDefinition? memberImplementation = FindImplementationMethod(lowering.Implementation.Type, lowering.Member);
		if (memberImplementation is not null && IsInterfaceLifecycleMember(lowering.Member))
			EnsureImplementationMethodSymbol(lowering.Implementation.Type, memberImplementation);

		CallExpression call = new()
		{
			ResolvedType = GetInterfaceEntryReturnType(lowering.Member, lowering.Implementation.Type),
			Target = IsDestructorFunction(lowering.Member)
				? new MethodReferenceExpression
				{
					ResolvedType = GetInterfaceEntryReturnType(lowering.Member, lowering.Implementation.Type)
				}
				: new MemberReferenceExpression
			{
				Target = CreateVariableReference(instance.Target, instance.Target.ResolvedType ?? $"{lowering.Implementation.Type.Name}*"),
				Name = GetImplementationMethodName(lowering.Member),
				Member = memberImplementation,
				ResolvedType = GetInterfaceEntryReturnType(lowering.Member, lowering.Implementation.Type)
			}
		};
		if (IsDestructorFunction(lowering.Member) && call.Target is MethodReferenceExpression destructorReference && memberImplementation is not null)
		{
			destructorReference.Candidates.Add(memberImplementation);
			call.Arguments.Add(new ArgumentExpression
			{
				Value = CreateVariableReference(instance.Target, instance.Target.ResolvedType ?? $"{lowering.Implementation.Type.Name}*"),
				ResolvedType = instance.Target.ResolvedType ?? $"{lowering.Implementation.Type.Name}*"
			});
		}
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
		if (call.Target is MemberReferenceExpression { Member: FunctionDefinition implementation })
		{
			EnsureImplementationMethodSymbol(lowering.Implementation.Type, implementation);
			callTargets[call] = implementation;
			ExpandParamsArguments(call);
			LowerCallArgumentConversions(call);
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
					Value = CreateObjectInterfaceVTableReference(lowering),
					ResolvedType = lowering.Field.ResolvedType
				}
				});
			}
		}

	Expression CreateObjectInterfaceVTableReference(InterfaceImplementationLowering lowering)
	{
		if (lowering.ObjectVTableStorage is null)
		{
			return new VariableReferenceExpression
			{
				Variable = lowering.VTable,
				ResolvedType = lowering.VTable.ResolvedType
			};
		}

		return new UnaryExpression
		{
			Operator = UnaryOperator.AddressOf,
			Operand = new VariableReferenceExpression
			{
				Variable = lowering.ObjectVTableStorage,
				ResolvedType = lowering.ObjectVTableStorage.ResolvedType
			},
			ResolvedType = lowering.VTable.ResolvedType
		};
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
		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (IsInterfaceLifecycleMember(interfaceMember) && BuildMethodSignature(function).Equals(BuildMethodSignature(interfaceMember)))
				return function;

			if (IsMarkedInterfaceImplementation(function, interfaceMember))
				return function;
		}
		return null;
	}

	bool IsMarkedInterfaceImplementation(FunctionDefinition function, FunctionDefinition interfaceMember)
	{
		if (function.InterfaceImplementationMember is not null)
		{
			if (function.InterfaceImplementationMember == interfaceMember)
				return true;
			return GetCallableName(function.InterfaceImplementationMember) == GetCallableName(interfaceMember)
				&& MethodSignatureCompatibleWithConstOfVariance(BuildMethodSignature(function.InterfaceImplementationMember), BuildMethodSignature(interfaceMember), compareName: function.InterfaceImplementationSlotName is null);
		}

		if (function.CallableAscriptionType is null || FindContainingType(interfaceMember) is not InterfaceDefinition interfaceDefinition)
			return false;
		string targetType = BaseTypeName(function.CallableAscriptionType.ResolvedType ?? GetTypeReferenceName(function.CallableAscriptionType) ?? ErrorType);
		if (targetType != interfaceDefinition.Name)
			return false;
		string slotName = function.InterfaceImplementationSlotName ?? GetCallableName(function);
		return slotName == GetCallableName(interfaceMember)
			&& MethodSignatureCompatibleWithConstOfVariance(BuildMethodSignature(function), BuildMethodSignature(interfaceMember), compareName: function.InterfaceImplementationSlotName is null);
	}

	static void EnsureImplementationMethodSymbol(TypeDefinition type, FunctionDefinition function)
	{
		if (function.SymbolOverridden)
			return;

		if (function.Modifier == FunctionModifier.Constructor || IsDestructorFunction(function))
		{
			function.Symbol = EffectiveTypeSymbol(type) + "_" + GetImplementationMethodName(function);
			return;
		}

		if (string.IsNullOrWhiteSpace(function.Symbol) || function.Symbol == function.Name)
			function.Symbol = EffectiveTypeSymbol(type) + "_" + GetImplementationMethodName(function);
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
			return new AnyTypeReference { ResolvedType = "any" };
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
		string returnType = member.Modifier == FunctionModifier.Constructor ? "any" : member.ResolvedType ?? ErrorType;
		return $"fn {returnType}({string.Join(", ", parameters)})";
	}

	static string InterfaceFieldName(InterfaceDefinition interfaceDefinition)
	{
		return "_vt_" + interfaceDefinition.Name;
	}

	static string InterfaceVTableName(TypeDefinition type, InterfaceDefinition interfaceDefinition)
	{
		return EffectiveTypeSymbol(type) + "_" + EffectiveTypeSymbol(interfaceDefinition);
	}

	static string InterfaceAccessorName(InterfaceDefinition interfaceDefinition)
	{
		return "get" + interfaceDefinition.Name;
	}

	static string InterfaceIndirectName(InterfaceDefinition interfaceDefinition)
	{
		return interfaceDefinition.Name + "_Indirect";
	}

	static string InterfaceThunkName(TypeDefinition type, InterfaceDefinition interfaceDefinition, FunctionDefinition member)
	{
		return EffectiveTypeSymbol(type) + "_" + EffectiveTypeSymbol(interfaceDefinition) + "_" + GetInterfaceEntryName(member);
	}

	const string VirtualTableFieldName = "_vt";

	static string VirtualTableTypeName(TypeDefinition type)
	{
		return "_" + EffectiveTypeSymbol(type);
	}

	static string VirtualTableVariableName(TypeDefinition type)
	{
		return "_" + EffectiveTypeSymbol(type) + "__vt";
	}

	static string VirtualImplementationName(FunctionDefinition function)
	{
		return "_" + VirtualSlotName(function);
	}

	static string VirtualImplementationSymbol(TypeDefinition type, FunctionDefinition function)
	{
		return EffectiveTypeSymbol(type) + "__" + VirtualSlotName(function);
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
			ConstOfTypeReference { Type: not null } constOf => "const " + GetTypeReferenceName(constOf.Type),
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

	TypeReference? EraseConstOfTypeReference(TypeReference? type)
	{
		if (type is null)
			return null;

		Dictionary<string, bool> anchors = [];
		foreach (string anchor in GetConstOfAnchorNames(type))
			anchors[anchor] = true;
		return SubstituteConstOfTypeReference(type, anchors);
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
			&& classDefinition.Extern is null
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
				if (type is ClassDefinition { Extern: not null })
				{
					if (function.Extern is not null)
						generated.Add(CreateCreateMethod(type, function, function));
				}
				else
				{
					FunctionDefinition initNew = CreateInitNewMethod(type, function);
					generated.Add(initNew);
					if (type is not ClassDefinition { Modifier: ClassModifier.Abstract })
					{
						FunctionDefinition create = CreateCreateMethod(type, function, initNew);
						generated.Add(create);
				}
				}
				function.Body = null;
			}
			else if (IsDestructorFunction(function))
			{
				if (type is ClassDefinition { Extern: not null })
				{
					if (function.Extern is not null)
						generated.Add(CreateExternDestroyMethod(type, function));
					function.Body = null;
					continue;
				}

				FunctionDefinition opDelete = CreateDeleteMethod(type, function);
				generated.Add(opDelete);
				if (type is not ClassDefinition { Extern: not null }
					&& function.Modifier is not FunctionModifier.Override and not FunctionModifier.Sealed)
				{
					FunctionDefinition destroy = CreateDestroyMethod(type, function, opDelete);
					generated.Add(destroy);
				}
				function.Body = null;
			}
		}

		if (type is ClassDefinition exportedClass
			&& exportedClass.Export is not null
			&& exportedClass.Extern is null
			&& exportedClass.Modifier != ClassModifier.Abstract
			&& !HasDestructor(functions)
			&& !HasDestroyMethod(functions)
			&& !HasDestroyMethod(generated))
		{
			generated.Add(CreateImplicitExportedDestroyMethod(exportedClass));
		}

		functions.AddRange(generated);
	}

	static bool HasDestructor(List<FunctionDefinition> functions)
	{
		foreach (FunctionDefinition function in functions)
			if (IsDestructorFunction(function))
				return true;
		return false;
	}

	static bool HasDestroyMethod(List<FunctionDefinition> functions)
	{
		foreach (FunctionDefinition function in functions)
			if (function.Name == DestroyMethodName)
				return true;
		return false;
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
		FunctionDefinition method = generatedDeclarations.Function(GeneratedDeclarationCategory.Lifecycle, "implicit exported parameterless constructor", classDefinition);
		method.SourceSyntax = classDefinition.SourceSyntax;
		method.Name = classDefinition.Name;
		method.Symbol = EffectiveTypeSymbol(classDefinition);
		method.Export = "export";
		method.Modifier = FunctionModifier.Constructor;
		method.ReturnType = TypeReferenceFor(classDefinition);
		method.ResolvedType = classDefinition.Name;
		method.Body = new BlockStatement
		{
			SourceSyntax = classDefinition.SourceSyntax,
			ResolvedType = "void"
		};
		return method;
	}

	FunctionDefinition CreateInitNewMethod(TypeDefinition type, FunctionDefinition constructor)
	{
		FunctionDefinition method = generatedDeclarations.Function(GeneratedDeclarationCategory.Lifecycle, "constructor init-new helper", constructor);
		method.SourceSyntax = constructor.SourceSyntax;
		method.Name = InitNewMethodName;
		method.Symbol = $"{EffectiveTypeSymbol(type)}_{InitNewMethodName}";
		method.Export = constructor.Export;
		method.Internal = constructor.Internal;
		method.Extern = constructor.Extern;
		method.ReturnType = VoidType();
		method.ResolvedType = "void";
		method.Body = constructor.Body;
		CopyLifecycleParameters(constructor.Parameters, method.Parameters);
		if (HasWithinParameter(method) && method.Body is BlockStatement block)
			block.Statements.Insert(0, CreateResolvedAllocatorLocal(GetWithinParameter(method)));
		return method;
	}

	FunctionDefinition CreateCreateMethod(TypeDefinition type, FunctionDefinition constructor, FunctionDefinition initNew)
	{
		TypeReference typeReference = TypeReferenceFor(type);
		FunctionDefinition method = generatedDeclarations.Function(GeneratedDeclarationCategory.Lifecycle, "constructor create helper", constructor);
		method.SourceSyntax = constructor.SourceSyntax;
		method.Name = CreateMethodName;
		method.Symbol = $"{EffectiveTypeSymbol(type)}_{CreateMethodName}";
		method.Export = constructor.Export;
		method.Internal = constructor.Internal;
		method.Extern = constructor.Extern;
		method.Modifier = FunctionModifier.Static;
		method.ReturnType = PointerTo(CloneType(typeReference)!);
		method.ResolvedType = $"{type.Name}*";
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
		BlockStatement guardBody = new() { ResolvedType = "void" };
		if (type is ClassDefinition)
			guardBody.Statements.Add(CreateZeroAllocatedInstanceStatement(CreateVariableReference(local.Target, $"{type.Name}*"), typeReference, type.Name, method.SourceSyntax));
		guardBody.Statements.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = CreateInitNewCall(CreateVariableReference(local.Target, $"{type.Name}*"), initNew, method.ArgumentsFromParameters(skipAllocator: !HasWithinParameter(initNew)), method.SourceSyntax, allocatorParameter is null ? null : CreateVariableReference(allocatorParameter, allocatorParameter.ResolvedType ?? "Allocator*"))
		});
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
			Body = guardBody
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
		FunctionDefinition method = generatedDeclarations.Function(GeneratedDeclarationCategory.Lifecycle, "destructor delete helper", destructor);
		method.SourceSyntax = destructor.SourceSyntax;
		method.Name = DeleteMethodName;
		method.Symbol = $"{EffectiveTypeSymbol(type)}_op_delete";
		method.Export = destructor.Export;
		method.Internal = destructor.Internal;
		method.Extern = destructor.Extern;
		method.Modifier = GetDeleteMethodModifier(destructor);
		method.ReturnType = VoidType();
		method.ResolvedType = "void";
		method.Body = destructor.Body;
		CopyLifecycleParameters(destructor.Parameters, method.Parameters);
		return method;
	}

	FunctionDefinition CreateExternDestroyMethod(TypeDefinition type, FunctionDefinition destructor)
	{
		FunctionDefinition method = generatedDeclarations.Function(GeneratedDeclarationCategory.Lifecycle, "extern destructor destroy helper", destructor);
		method.SourceSyntax = destructor.SourceSyntax;
		method.Name = DestroyMethodName;
		method.Symbol = $"{EffectiveTypeSymbol(type)}_{DestroyMethodName}";
		method.Export = destructor.Export;
		method.Internal = destructor.Internal;
		method.Extern = destructor.Extern;
		method.ReturnType = VoidType();
		method.ResolvedType = "void";
		CopyLifecycleParameters(destructor.Parameters, method.Parameters);
		return method;
	}

	FunctionDefinition CreateImplicitExportedDestroyMethod(ClassDefinition type)
	{
		FunctionDefinition method = generatedDeclarations.Function(GeneratedDeclarationCategory.Lifecycle, "implicit exported destroy helper", type);
		method.Name = DestroyMethodName;
		method.Symbol = $"{EffectiveTypeSymbol(type)}_{DestroyMethodName}";
		method.Export = type.Export;
		method.Internal = type.Internal;
		method.ReturnType = VoidType();
		method.ResolvedType = "void";
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
		FunctionDefinition method = generatedDeclarations.Function(GeneratedDeclarationCategory.Lifecycle, "destructor destroy helper", destructor);
		method.SourceSyntax = destructor.SourceSyntax;
		method.Name = DestroyMethodName;
		method.Symbol = $"{EffectiveTypeSymbol(type)}_{DestroyMethodName}";
		method.Export = destructor.Export;
		method.Internal = destructor.Internal;
		method.Extern = destructor.Extern;
		method.ReturnType = VoidType();
		method.ResolvedType = "void";
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
