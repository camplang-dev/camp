using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly FunctionDefinition allocatorAllocMethod = CreateAllocatorAllocMethod();
	readonly FunctionDefinition allocatorFreeMethod = CreateAllocatorFreeMethod();
	readonly Dictionary<ClassDefinition, List<InterfaceImplementationLowering>> classInterfaceLowerings = [];
	readonly Dictionary<StructDefinition, List<InterfaceImplementationLowering>> structInterfaceLowerings = [];
	readonly Dictionary<ClassDefinition, VirtualClassLowering> virtualClassLowerings = [];
	readonly Dictionary<FunctionDefinition, FunctionDefinition> virtualImplementations = [];
	readonly Dictionary<FunctionDefinition, InterfaceThunkLowering> interfaceThunkLowerings = [];
	readonly Dictionary<InterfaceDefinition, StructDefinition> loweredInterfaceStructs = [];
	readonly Dictionary<InterfaceDefinition, StructDefinition> interfaceIndirectStructs = [];
	readonly List<Definition> generatedInterfaceDefinitions = [];
	const string InitNewMethodName = "op_initnew";
	const string CreateMethodName = "create";
	const string DeleteMethodName = "op_delete";
	const string DestroyMethodName = "destroy";
	Expression? currentAllocatorOverride;
	List<Statement>? currentStatementPrefix;
	List<Statement>? currentStatementSuffix;
	DeclarationTarget? currentImplicitCatchTarget;
	readonly List<CleanupScope> currentCleanupScopes = [];
	readonly List<ThrowHandler> currentThrowHandlers = [];
	FunctionDefinition? currentRewriteFunction;
	TypeDefinition? currentRewriteContainingType;
	string? currentFunctionExitLabel;
	DeclarationTarget? currentFunctionReturnTarget;
	string currentFunctionReturnType = "void";
	int generatedLocalIndex;

	public static AnalysisResult AnalyzeAndRewrite(Module module)
	{
		ArgumentNullException.ThrowIfNull(module);

		DeclarationExpansionResult expansion = BindableNodeExpander.Expand(module);
		if (expansion.Diagnostics.Count > 0)
			return new AnalysisResult(expansion.Module, expansion.Diagnostics);

		return AnalyzeAndRewriteExpanded(expansion);
	}

	internal static DeclarationExpansionResult ExpandDeclarations(Module module)
	{
		BindableNodeAnalyzer analyzer = new();
		analyzer.currentModule = module;
		analyzer.CollectTypeNames(module);
		analyzer.GenerateLifecycleMethods(module);
		analyzer.GenerateVirtualDeclarations(module);
		analyzer.GenerateInterfaceDeclarations(module);
		return new DeclarationExpansionResult(module, analyzer.diagnostics, analyzer);
	}

	public static AnalysisResult AnalyzeExpanded(DeclarationExpansionResult expansion)
	{
		return AnalyzeDeclarationsExpanded(expansion);
	}

	public static AnalysisResult AnalyzeDeclarationsExpanded(DeclarationExpansionResult expansion)
	{
		ArgumentNullException.ThrowIfNull(expansion);
		BindableNodeAnalyzer analyzer = expansion.Analyzer;
		analyzer.AnalyzeDeclarations(expansion.Module);
		analyzer.ApplyNodeRewrites(expansion.Module);
		analyzer.FillMissingResolvedTypes(expansion.Module);
		return new AnalysisResult(expansion.Module, analyzer.diagnostics);
	}

	public static AnalysisResult AnalyzeAndRewriteExpanded(DeclarationExpansionResult expansion)
	{
		ArgumentNullException.ThrowIfNull(expansion);
		BindableNodeAnalyzer analyzer = expansion.Analyzer;
		analyzer.AnalyzeDeclarations(expansion.Module);
		if (analyzer.diagnostics.Count == 0)
			analyzer.AnalyzeMethodBodies(expansion.Module);
		analyzer.ApplyNodeRewrites(expansion.Module);
		analyzer.FillMissingResolvedTypes(expansion.Module);
		AnalysisResult analysis = new(expansion.Module, analyzer.diagnostics);
		if (analysis.Diagnostics.Count > 0)
			return analysis;

		analyzer.RewriteModule(expansion.Module);
		analyzer.FillMissingResolvedTypes(expansion.Module);
		return new AnalysisResult(expansion.Module, analyzer.diagnostics);
	}

	void RewriteModule(Module module)
	{
		CompleteInterfaceDeclarations(module);
		foreach (Definition definition in module.Definitions)
			RewriteDefinition(definition);
		LowerSourceInterfaceTypes(module);
		RefreshLoweredResolvedTypes(module);
		LowerInterfaceDefinitions(module);
	}

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
				VirtualSlot slot = new(function, implementation);
				lowering.DeclaredSlots.Add(slot);
				vtableType.Fields.Add(CreateVirtualSlotField(classDefinition, function));
				if (function.Modifier == FunctionModifier.Virtual)
					function.Body = CreateVirtualDispatchBody(classDefinition, function);
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

		FunctionDefinition implementation = new()
		{
			SourceSyntax = source.SourceSyntax,
			Name = VirtualImplementationName(source),
			Symbol = VirtualImplementationSymbol(owner, source),
			Export = source.Export,
			ReturnType = CloneType(source.ReturnType),
			ResolvedType = source.ResolvedType,
			Body = source.Body
		};
		CopyParameters(source.Parameters, implementation.Parameters);
		return implementation;
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

	BlockStatement CreateVirtualDispatchBody(ClassDefinition owner, FunctionDefinition function)
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
				Member = function,
				ResolvedType = BuildVirtualSlotCallableType(owner, function)
			}
		};
		call.Arguments.Add(new ArgumentExpression
		{
			Value = new ThisExpression { ResolvedType = owner.Name },
			ResolvedType = $"{owner.Name}*"
		});
		foreach (ArgumentExpression argument in function.ArgumentsFromParameters())
			call.Arguments.Add(argument);

		BlockStatement body = new() { ResolvedType = "void" };
		if (returnType == "void")
			body.Statements.Add(new ExpressionStatement { Expression = call, ResolvedType = "void" });
		else
			body.Statements.Add(new ReturnStatement { Expression = call, ResolvedType = "void" });
		return body;
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
				Type = PointerTo(InterfaceType(interfaceDefinition)),
				ResolvedType = $"{interfaceDefinition.Name}*"
			};
			classDefinition.Fields.Insert(interfaceIndex, field);

			VariableDefinition vtable = new()
			{
				Name = InterfaceVTableName(classDefinition, interfaceDefinition),
				Symbol = InterfaceVTableName(classDefinition, interfaceDefinition),
				Type = InterfaceType(interfaceDefinition),
				ResolvedType = interfaceDefinition.Name
			};
			module.Definitions.Add(vtable);
			generatedInterfaceDefinitions.Add(vtable);

			InterfaceImplementationLowering lowering = new(classDefinition, interfaceDefinition, field, vtable, DirectEntries: interfaceIndex == 0, IsStruct: false);
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
			VariableDefinition vtable = new()
			{
				Name = InterfaceVTableName(structDefinition, interfaceDefinition),
				Symbol = InterfaceVTableName(structDefinition, interfaceDefinition),
				Type = InterfaceType(interfaceDefinition),
				ResolvedType = interfaceDefinition.Name
			};
			module.Definitions.Add(vtable);
			generatedInterfaceDefinitions.Add(vtable);

			InterfaceImplementationLowering lowering = new(structDefinition, interfaceDefinition, Field: null, vtable, DirectEntries: false, IsStruct: true);
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
				lowering.VTable.InitialValue = CreateInterfaceVTableInitializer(lowering, lowering.Interface);

			for (int i = lowerings.Count - 1; i >= 0; i--)
				InsertInterfaceVTableInitialization(classDefinition, lowerings[i]);
		}

		foreach ((StructDefinition _, List<InterfaceImplementationLowering> lowerings) in structInterfaceLowerings)
		{
			foreach (InterfaceImplementationLowering lowering in lowerings)
				lowering.VTable.InitialValue = CreateInterfaceVTableInitializer(lowering, lowering.Interface);
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
				LowerSourceInterfaceTypes(lowered);
			}
			else if (value is IList list)
			{
				LowerSourceInterfaceTypes(list);
			}
			else if (value is BindableNode child)
			{
				LowerSourceInterfaceTypes(child);
			}
		}

		SyncResolvedTypeFromLoweredType(node);
	}

	void LowerSourceInterfaceTypes(IList list)
	{
		for (int i = 0; i < list.Count; i++)
		{
			object? item = list[i];
			if (item is TypeReference type)
			{
				TypeReference lowered = LowerSourceInterfaceType(type);
				if (!ReferenceEquals(lowered, type))
					list[i] = lowered;
				LowerSourceInterfaceTypes(lowered);
			}
			else if (item is BindableNode child)
			{
				LowerSourceInterfaceTypes(child);
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
		foreach (PropertyInfo property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (property.Name == nameof(BindableNode.SourceSyntax) || property.Name == nameof(BindableNode.ResolvedType) || IsSemanticReferenceProperty(property))
				continue;

			object? value = property.GetValue(node);
			if (value is BindableNode child)
				RefreshLoweredResolvedTypes(child);
			else if (value is IList list)
			{
				foreach (object? item in list)
				{
					if (item is BindableNode childItem)
						RefreshLoweredResolvedTypes(childItem);
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
				bool isInstance = IsInstanceFunction(method.Candidates[0]);
				if (!isInstance || NeedsResolvedTypeRefresh(method.ResolvedType))
					method.ResolvedType = BuildFunctionValueType(method.Candidates[0], isInstance);
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
			Modifier = StructModifier.Fixed,
			ResolvedType = InterfaceIndirectName(interfaceDefinition)
		};
		indirect.GenericParameters.Add(generic);
		indirect.Fields.Add(new FieldDefinition
		{
			Name = "_vt",
			Symbol = "_vt",
			Type = PointerTo(InterfaceType(interfaceDefinition)),
			ResolvedType = $"{interfaceDefinition.Name}*"
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
					Value = new UnaryExpression
					{
						Operator = UnaryOperator.AddressOf,
						Operand = new NamedExpression
						{
							Name = lowering.VTable.Name,
							ResolvedType = lowering.VTable.ResolvedType
						},
						ResolvedType = lowering.Field.ResolvedType
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
			if (function.Name == name)
				return function;
		}
		return null;
	}

	static void EnsureImplementationMethodSymbol(TypeDefinition type, FunctionDefinition function)
	{
		if (string.IsNullOrWhiteSpace(function.Symbol) || function.Symbol == function.Name)
			function.Symbol = type.Name + "_" + function.Name.TrimStart('~');
	}

	static string GetImplementationMethodName(FunctionDefinition member)
	{
		if (IsDestructorFunction(member))
			return DestroyMethodName;

		return member.Modifier switch
		{
			FunctionModifier.Constructor => CreateMethodName,
			_ => member.Name
		};
	}

	static string GetInterfaceEntryName(FunctionDefinition member)
	{
		if (IsDestructorFunction(member))
			return DestroyMethodName;

		return member.Modifier switch
		{
			FunctionModifier.Constructor => CreateMethodName,
			_ => member.Name
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
		return function.Name == DeleteMethodName || IsDestructorFunction(function) ? DeleteMethodName : function.Name;
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
			PointerTypeReference { ElementType: not null } pointer => GetTypeReferenceName(pointer.ElementType) + "*",
			ConstTypeReference { Type: not null } constant => "const " + GetTypeReferenceName(constant.Type),
			VolatileTypeReference { Type: not null } vol => "volatile " + GetTypeReferenceName(vol.Type),
			EscapedTypeReference { Type: not null } escaped => "escaped " + GetTypeReferenceName(escaped.Type),
			ScopedTypeReference { Type: not null } scoped => "scoped " + GetTypeReferenceName(scoped.Type),
			UnscopedTypeReference { Type: not null } unscoped => "unscoped " + GetTypeReferenceName(unscoped.Type),
			AutoTypeReference => AutoType,
			AnyTypeReference => "any",
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

	FunctionDefinition CreateInitNewMethod(TypeDefinition type, FunctionDefinition constructor)
	{
		FunctionDefinition method = new()
		{
			SourceSyntax = constructor.SourceSyntax,
			Name = InitNewMethodName,
			Symbol = $"{type.Name}_{InitNewMethodName}",
			Export = constructor.Export,
			ReturnType = VoidType(),
			ResolvedType = "void",
			Body = constructor.Body
		};
		CopyParameters(constructor.Parameters, method.Parameters);
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
			Modifier = FunctionModifier.Static,
			ReturnType = PointerTo(CloneType(typeReference)!),
			ResolvedType = $"{type.Name}*"
		};
		CopyParameters(constructor.Parameters, method.Parameters);
		bool createWithAllocator = HasWithinParameter(method) || HasCreateWithAllocatorAttribute(type);
		if (createWithAllocator && !HasWithinParameter(method))
			method.Parameters.Add(CreateAllocatorParameter());
		method.Body = new BlockStatement
		{
			ResolvedType = "void"
		};
		BlockStatement body = method.Body;
		ParameterDefinition? allocatorParameter = GetAllocatorParameter(method);
		if (createWithAllocator)
			body.Statements.Add(CreateResolvedAllocatorLocal(allocatorParameter));
		string localName = NewGeneratedLocalName("created");
		DeclarationStatement local = CreateGeneratedLocal(localName, $"{type.Name}*", PointerTo(CloneType(typeReference)!), CreateAllocCall(typeReference, createWithAllocator ? CreateResolvedAllocatorReference() : StdDefaultAllocator(), method.SourceSyntax));
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
			Modifier = GetDeleteMethodModifier(destructor),
			ReturnType = VoidType(),
			ResolvedType = "void",
			Body = destructor.Body
		};
		CopyParameters(destructor.Parameters, method.Parameters);
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
		TypeReference typeReference = TypeReferenceFor(type);
		ParameterDefinition value = new()
		{
			Name = "value",
			Symbol = "value",
			Type = PointerTo(CloneType(typeReference)!),
			ResolvedType = $"{type.Name}*"
		};
		VariableReferenceExpression target = new()
		{
			Variable = value,
			ResolvedType = value.ResolvedType
		};

		FunctionDefinition method = new()
		{
			SourceSyntax = destructor.SourceSyntax,
			Name = DestroyMethodName,
			Symbol = $"{type.Name}_{DestroyMethodName}",
			Export = destructor.Export,
			ReturnType = VoidType(),
			ResolvedType = "void"
		};
		bool destroyWithAllocator = HasWithinParameter(destructor) || HasCreateWithAllocatorAttribute(type);
		if (destroyWithAllocator)
			method.Parameters.Add(CreateAllocatorParameter());
		method.Body = new BlockStatement
		{
			ResolvedType = "void"
		};
		BlockStatement body = method.Body;
		body.Statements.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = CreateDestructorCall(new ThisExpression { ResolvedType = type.Name }, opDelete, GetAllocatorParameter(method) is ParameterDefinition allocatorParameter ? CreateVariableReference(allocatorParameter, allocatorParameter.ResolvedType ?? "Allocator*") : null)
		});
		if (destroyWithAllocator)
			body.Statements.Add(CreateResolvedAllocatorLocal(GetAllocatorParameter(method)));
		body.Statements.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = CreateFreeCall(new ThisExpression { ResolvedType = type.Name }, destroyWithAllocator ? CreateResolvedAllocatorReference() : StdDefaultAllocator())
		});
		return method;
	}

	void RewriteDefinition(Definition definition)
	{
		switch (definition)
		{
			case VariableDefinition variable:
				variable.InitialValue = LowerExpression(variable.InitialValue);
				break;

			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
					field.InitialValue = LowerExpression(field.InitialValue);
				foreach (FunctionDefinition function in classDefinition.Functions)
					RewriteFunction(function, classDefinition);
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					field.InitialValue = LowerExpression(field.InitialValue);
				foreach (FunctionDefinition function in structDefinition.Functions)
					RewriteFunction(function, structDefinition);
				break;

			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					RewriteFunction(function, interfaceDefinition);
				break;

			case EnumDefinition enumDefinition:
				foreach (VariableDefinition value in enumDefinition.Values)
					value.InitialValue = LowerExpression(value.InitialValue);
				foreach (FunctionDefinition function in enumDefinition.Functions)
					RewriteFunction(function, enumDefinition);
				break;

			case NewtypeDefinition newtypeDefinition:
				foreach (ParameterDefinition parameter in newtypeDefinition.Parameters)
					parameter.DefaultValue = LowerExpression(parameter.DefaultValue);
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					RewriteFunction(function, newtypeDefinition);
				break;

			case ParamsDefinition paramsDefinition:
				foreach (ParameterDefinition component in paramsDefinition.Components)
					component.DefaultValue = LowerExpression(component.DefaultValue);
				foreach (FunctionDefinition function in paramsDefinition.Functions)
					RewriteFunction(function, paramsDefinition);
				break;

			case FunctionDefinition function:
				RewriteFunction(function, containingType: null);
				break;
		}
	}

	void RewriteFunction(FunctionDefinition function, TypeDefinition? containingType)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
			parameter.DefaultValue = LowerExpression(parameter.DefaultValue);

		Expression? previousAllocator = currentAllocatorOverride;
		FunctionDefinition? previousFunction = currentRewriteFunction;
		TypeDefinition? previousType = currentRewriteContainingType;
		string? previousFunctionExitLabel = currentFunctionExitLabel;
		DeclarationTarget? previousFunctionReturnTarget = currentFunctionReturnTarget;
		string previousFunctionReturnType = currentFunctionReturnType;
		currentAllocatorOverride = GetFunctionAllocatorForBody(function);
		currentRewriteFunction = function;
		currentRewriteContainingType = containingType;
		currentFunctionExitLabel = null;
		currentFunctionReturnTarget = null;
		currentFunctionReturnType = function.ResolvedType ?? "void";
		function.Body = RewriteFunctionBody(function.Body);
		if (function.Body is not null && currentFunctionExitLabel is not null)
			AppendFunctionExit(function.Body.Statements);
		currentAllocatorOverride = previousAllocator;
		currentRewriteFunction = previousFunction;
		currentRewriteContainingType = previousType;
		currentFunctionExitLabel = previousFunctionExitLabel;
		currentFunctionReturnTarget = previousFunctionReturnTarget;
		currentFunctionReturnType = previousFunctionReturnType;
	}

	BlockStatement? RewriteFunctionBody(BlockStatement? body)
	{
		if (body is null)
			return null;

		RewriteStatementList(body.Statements);
		return body;
	}

	void AppendFunctionExit(List<Statement> statements)
	{
		statements.Add(new LabelStatement { Name = currentFunctionExitLabel, ResolvedType = "void" });
		if (currentFunctionReturnTarget is not null)
		{
			statements.Add(new ReturnStatement
			{
				ResolvedType = "void",
				Expression = currentFunctionReturnType == "void" ? null : CreateVariableReference(currentFunctionReturnTarget, currentFunctionReturnType)
			});
		}
		else
		{
			statements.Add(new ReturnStatement { ResolvedType = "void" });
		}
	}

	Statement RewriteStatement(Statement statement)
	{
		switch (statement)
		{
			case BlockStatement block:
				RewriteStatementList(block.Statements);
				break;

			case ExpressionStatement expression:
				expression.Expression = LowerExpression(expression.Expression);
				if (expression.Expression is UnaryExpression { Operator: UnaryOperator.Throw } throwExpression)
					return CreateBlock(CreateThrowTransfer(throwExpression.Operand, throwExpression.SourceSyntax));
				break;

			case DeclarationStatement declaration:
				if (TryRewriteNewDeclaration(declaration, out List<Statement>? newStatements))
					return CreateBlock(newStatements);
				if (TryRewriteInitDeclaration(declaration, out List<Statement>? statements))
					return CreateBlock(statements);
				declaration.InitialValue = declaration.InitialValue is not null && ContainsUncaughtThrow(declaration.InitialValue)
					? HoistThrowingExpression(declaration.InitialValue)
					: LowerExpression(declaration.InitialValue);
				declaration.InitialValue = LowerInterfaceConversion(declaration.Target.Type, declaration.InitialValue);
				break;

			case IfStatement ifStatement:
				ifStatement.Condition = ifStatement.Condition is not null && ContainsUncaughtThrow(ifStatement.Condition)
					? HoistThrowingExpression(ifStatement.Condition)
					: LowerExpression(ifStatement.Condition);
				if (ifStatement.Body is not null)
					ifStatement.Body = RewriteStatement(ifStatement.Body);
				if (ifStatement.ElseBody is not null)
					ifStatement.ElseBody = RewriteStatement(ifStatement.ElseBody);
				break;

			case WhileStatement whileStatement:
				if (whileStatement.Condition is not null && ContainsUncaughtThrow(whileStatement.Condition))
					return RewriteWhileStatementWithThrowingCondition(whileStatement);
				whileStatement.Condition = LowerExpression(whileStatement.Condition);
				if (whileStatement.Body is not null)
					whileStatement.Body = RewriteStatement(whileStatement.Body);
				break;

			case DoWhileStatement doWhile:
				if (doWhile.Body is not null)
					doWhile.Body = RewriteStatement(doWhile.Body);
				doWhile.Condition = LowerExpression(doWhile.Condition);
				break;

			case ForStatement forStatement:
				if (forStatement.Condition.Declaration is not null)
					forStatement.Condition.Declaration = (DeclarationStatement)RewriteStatement(forStatement.Condition.Declaration);
				for (int i = 0; i < forStatement.Condition.Clauses.Count; i++)
					forStatement.Condition.Clauses[i] = LowerExpression(forStatement.Condition.Clauses[i]);
				if (forStatement.Body is not null)
					forStatement.Body = RewriteStatement(forStatement.Body);
				break;

			case ForeachStatement foreachStatement:
				foreachStatement.Source = LowerExpression(foreachStatement.Source);
				if (foreachStatement.Body is not null)
					foreachStatement.Body = RewriteStatement(foreachStatement.Body);
				break;

			case SwitchStatement switchStatement:
				switchStatement.Expression = switchStatement.Expression is not null && ContainsUncaughtThrow(switchStatement.Expression)
					? HoistThrowingExpression(switchStatement.Expression)
					: LowerExpression(switchStatement.Expression);
				RewriteStatementList(switchStatement.Statements);
				break;

			case CaseStatement caseStatement:
				caseStatement.Expression = LowerExpression(caseStatement.Expression);
				break;

			case LabelStatement:
			case GotoStatement:
				break;

			case ReturnStatement returnStatement:
				returnStatement.Expression = returnStatement.Expression is not null && ContainsUncaughtThrow(returnStatement.Expression)
					? HoistThrowingExpression(returnStatement.Expression)
					: LowerExpression(returnStatement.Expression);
				return WithPendingCleanups(returnStatement);

			case YieldStatement yieldStatement:
				yieldStatement.Expression = LowerExpression(yieldStatement.Expression);
				return WithPendingCleanups(yieldStatement);

			case DeleteStatement deleteStatement:
				return WithPendingCleanups(new ExpressionStatement
				{
					SourceSyntax = deleteStatement.SourceSyntax,
					ResolvedType = "void",
					Expression = RewriteDeleteExpression(deleteStatement.Expression)
				});

			case TryStatement tryStatement:
				return RewriteTryStatement(tryStatement);

			case CatchStatement catchStatement:
				if (catchStatement.Body is not null)
					catchStatement.Body = RewriteStatement(catchStatement.Body);
				break;

			case FinallyStatement finallyStatement:
				if (finallyStatement.Body is not null)
					finallyStatement.Body = RewriteStatement(finallyStatement.Body);
				break;

			case WithinStatement withinStatement:
				withinStatement.Allocator = LowerExpression(withinStatement.Allocator);
				if (withinStatement.Body is not null)
					withinStatement.Body = RewriteStatement(withinStatement.Body);
				break;
		}

		return statement;
	}

	void RewriteStatementList(List<Statement> statements)
	{
		CleanupScope cleanupScope = new([], RunBeforeCatch: true);
		currentCleanupScopes.Add(cleanupScope);
		for (int i = 0; i < statements.Count; i++)
		{
			if (statements[i] is FinallyStatement finallyStatement)
			{
				if (finallyStatement.Body is not null)
					cleanupScope.Statements.Add(RewriteStatement(finallyStatement.Body));
				statements.RemoveAt(i);
				i--;
				continue;
			}

			if (statements[i] is DeclarationStatement declaration && (TryRewriteNewDeclaration(declaration, out List<Statement>? rewritten) || TryRewriteInitDeclaration(declaration, out rewritten)))
			{
				statements.RemoveAt(i);
				statements.InsertRange(i, rewritten);
				i += rewritten.Count - 1;
				continue;
			}

			List<Statement>? previousPrefix = currentStatementPrefix;
			List<Statement>? previousSuffix = currentStatementSuffix;
			currentStatementPrefix = [];
			currentStatementSuffix = [];
			statements[i] = RewriteStatement(statements[i]);
			if (currentStatementPrefix.Count > 0)
			{
				statements.InsertRange(i, currentStatementPrefix);
				i += currentStatementPrefix.Count;
			}
			if (currentStatementSuffix.Count > 0)
			{
				statements.InsertRange(i + 1, currentStatementSuffix);
				i += currentStatementSuffix.Count;
			}
			currentStatementPrefix = previousPrefix;
			currentStatementSuffix = previousSuffix;
		}
		currentCleanupScopes.RemoveAt(currentCleanupScopes.Count - 1);
		if (cleanupScope.ExitLabelName is not null)
			statements.Add(new LabelStatement { Name = cleanupScope.ExitLabelName, ResolvedType = "void" });
		for (int i = cleanupScope.Statements.Count - 1; i >= 0; i--)
			statements.Add(CloneStatementForCleanup(cleanupScope.Statements[i]));
		if (cleanupScope.ReturnTarget is not null)
			statements.Add(new ReturnStatement
			{
				ResolvedType = "void",
				Expression = cleanupScope.ReturnType == "void" ? null : CreateVariableReference(cleanupScope.ReturnTarget, cleanupScope.ReturnType)
			});
	}

	static BlockStatement CreateBlock(List<Statement> statements)
	{
		BlockStatement block = new() { ResolvedType = "void" };
		block.Statements.AddRange(statements);
		return block;
	}

	bool TryRewriteNewDeclaration(DeclarationStatement declaration, out List<Statement> statements)
	{
		statements = [];
		if (declaration.InitialValue is not ConstructionExpression { Kind: ConstructionKind.New } construction || declaration.Target.Names.Count != 1)
			return false;

		for (int i = 0; i < construction.Arguments.Count; i++)
			construction.Arguments[i] = LowerArgument(construction.Arguments[i]);
		construction.ElementCount = LowerExpression(construction.ElementCount);
		LowerInitializer(construction.Initializer);

		if (construction.ElementCount is not null || construction.Type is null)
			return false;

		string typeName = construction.Type.ResolvedType ?? BaseConstructedType(construction.ResolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return false;

		FunctionDefinition? initNew = FindInitNewMethod(definition, construction.Arguments.Count);
		declaration.InitialValue = CreateAllocCall(TypeReferenceFor(definition), declaration.SourceSyntax);
		statements.Add(declaration);

		Expression target = CreateVariableReference(declaration.Target, declaration.Target.ResolvedType ?? construction.ResolvedType ?? $"{typeName}*");
		if (CreateVirtualTableAssignment(target, definition) is Expression vtableAssignment)
		{
			statements.Add(new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = vtableAssignment
			});
		}
		if (initNew is null)
			return true;

		statements.Add(new IfStatement
		{
			SourceSyntax = construction.SourceSyntax,
			ResolvedType = "void",
			Condition = new BinaryExpression
			{
				Left = target,
				Operator = BinaryOperator.NotEqual,
				Right = new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "#NULL" },
				ResolvedType = "bool"
			},
			Body = new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = CreateInitNewCall(target, initNew, construction.Arguments, construction.SourceSyntax)
			}
		});
		return true;
	}

	bool TryRewriteInitDeclaration(DeclarationStatement declaration, out List<Statement> statements)
	{
		statements = [];
		if (declaration.InitialValue is not ConstructionExpression { Kind: ConstructionKind.Init } construction || declaration.Target.Names.Count != 1)
			return false;

		for (int i = 0; i < construction.Arguments.Count; i++)
			construction.Arguments[i] = LowerArgument(construction.Arguments[i]);
		LowerInitializer(construction.Initializer);

		Expression target = CreateVariableReference(declaration.Target, declaration.Target.ResolvedType ?? construction.ResolvedType ?? ErrorType);
		CallExpression? initCall = CreateInitCallForConstruction(construction, target);
		declaration.InitialValue = construction.Initializer;

		if (initCall is null)
			return false;

		statements.Add(declaration);
		if (typeDefinitions.TryGetValue(BaseConstructedType(target.ResolvedType), out TypeDefinition? definition)
			&& CreateVirtualTableAssignment(target, definition) is Expression vtableAssignment)
		{
			statements.Add(new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = vtableAssignment
			});
		}
		statements.Add(new ExpressionStatement
		{
			SourceSyntax = construction.SourceSyntax,
			ResolvedType = "void",
			Expression = initCall
		});
		return true;
	}

	Expression? LowerExpression(Expression? expression)
	{
		switch (expression)
		{
			case null:
				return null;

			case ConstructionExpression construction:
				for (int i = 0; i < construction.Arguments.Count; i++)
					construction.Arguments[i] = LowerArgument(construction.Arguments[i]);
				construction.ElementCount = LowerExpression(construction.ElementCount);
				LowerInitializer(construction.Initializer);
				return RewriteConstruction(construction);

			case WithinExpression within:
				within.Context = LowerExpression(within.Context);
				within.Expression = LowerExpression(within.Expression);
				return within;

			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
					item.Expression = LowerExpression(item.Expression);
				break;

			case ArrayExpression array:
				for (int i = 0; i < array.Elements.Count; i++)
					array.Elements[i] = LowerExpression(array.Elements[i]) ?? array.Elements[i];
				break;

			case InitializerExpression initializer:
				LowerInitializer(initializer);
				break;

			case ParenthesizedExpression parenthesized:
				parenthesized.Expression = LowerExpression(parenthesized.Expression);
				break;

			case CastExpression cast:
				cast.Expression = LowerExpression(cast.Expression);
				if (IsInterfacePointerType(cast.Type))
					return LowerInterfaceConversion(cast.Type, cast.Expression) ?? cast;
				break;

			case LambdaExpression lambda:
				lambda.Body = RewriteFunctionBody(lambda.Body);
				break;

			case ArgumentExpression argument:
				return LowerArgument(argument);

			case CallExpression call:
				call.Target = LowerExpression(call.Target);
				LowerThrowingArguments(call);
				for (int i = 0; i < call.Arguments.Count; i++)
					call.Arguments[i] = LowerArgument(call.Arguments[i]);
				LowerCallArgumentConversions(call);
				LowerInterfaceCall(call);
				return LowerUncaughtThrowingCall(call);

			case IndexExpression index:
				if (index.Target is MemberReferenceExpression getter && IsPropertyGetterReference(getter))
					return RewritePropertyGetterCall(getter, index.Arguments);
				index.Target = LowerExpression(index.Target);
				for (int i = 0; i < index.Arguments.Count; i++)
					index.Arguments[i] = LowerArgument(index.Arguments[i]);
				break;

			case MemberExpression member:
				member.Target = LowerExpression(member.Target);
				break;

			case MemberReferenceExpression memberReference:
				memberReference.Target = LowerExpression(memberReference.Target);
				if (IsPropertyGetterReference(memberReference))
					return RewritePropertyGetterCall(memberReference, []);
				break;

			case NamelessIndexerExpression nameless:
				nameless.Target = LowerExpression(nameless.Target);
				for (int i = 0; i < nameless.Arguments.Count; i++)
					nameless.Arguments[i] = LowerArgument(nameless.Arguments[i]);
				break;

			case UnaryExpression unary:
				unary.Context = LowerExpression(unary.Context);
				unary.Operand = LowerExpression(unary.Operand);
				break;

			case PostfixUpdateExpression postfix:
				postfix.Expression = LowerExpression(postfix.Expression);
				break;

			case FinallyDeleteExpression finallyDelete:
				return RewriteFinallyDeleteExpression(finallyDelete);

			case BinaryExpression binary:
				binary.Left = LowerExpression(binary.Left);
				binary.Right = LowerExpression(binary.Right);
				break;

			case AssignmentExpression assignment:
				if (TryRewritePropertySetterAssignment(assignment, out Expression? setterCall))
					return setterCall;
				assignment.Target = LowerExpression(assignment.Target);
				assignment.Value = LowerExpression(assignment.Value);
				if (assignment.Target is VariableReferenceExpression { Variable: DeclarationTarget target })
					assignment.Value = LowerInterfaceConversion(target.Type, assignment.Value);
				break;

			case ConditionalExpression conditional:
				conditional.Condition = LowerExpression(conditional.Condition);
				conditional.WhenTrue = LowerExpression(conditional.WhenTrue);
				conditional.WhenFalse = LowerExpression(conditional.WhenFalse);
				break;

			case RangeExpression range:
				range.Start = LowerExpression(range.Start);
				range.End = LowerExpression(range.End);
				break;
		}

		return expression;
	}

	bool TryRewritePropertySetterAssignment(AssignmentExpression assignment, out Expression? rewritten)
	{
		rewritten = null;
		switch (assignment.Target)
		{
			case MemberReferenceExpression setter when IsPropertySetterReference(setter):
				rewritten = RewritePropertySetterCall(setter, [], assignment.Value);
				return true;

			case IndexExpression { Target: MemberReferenceExpression setter } index when IsPropertySetterReference(setter):
				rewritten = RewritePropertySetterCall(setter, index.Arguments, assignment.Value);
				return true;

			default:
				return false;
		}
	}

	Statement RewriteTryStatement(TryStatement tryStatement)
	{
		List<Statement> statements = [];
		List<ThrowHandler> handlers = [];
		string doneLabel = NewGeneratedLabelName("try_done");
		string finallyLabel = NewGeneratedLabelName("try_finally");

		foreach (CatchStatement catchStatement in tryStatement.Catches)
		{
			string label = NewGeneratedLabelName("catch");
			DeclarationStatement caught = new() { ResolvedType = "void" };
			caught.Target.Type = CloneType(catchStatement.Target.Type);
			caught.Target.ResolvedType = catchStatement.Target.ResolvedType;
			foreach (string name in catchStatement.Target.Names)
				caught.Target.Names.Add(name);
			statements.Add(caught);
			handlers.Add(new ThrowHandler(catchStatement.Target.ResolvedType ?? ErrorType, caught.Target, label));
		}

		List<ThrowHandler> previousHandlers = [.. currentThrowHandlers];
		currentThrowHandlers.InsertRange(0, handlers);
		bool needsFinallyLabel = false;
		bool needsDoneLabel = false;
		List<Statement> finallyCleanups = [];
		if (tryStatement.Finally?.Body is not null)
			finallyCleanups.Add(RewriteStatement(tryStatement.Finally.Body));
		CleanupScope tryCleanupScope = new(finallyCleanups, RunBeforeCatch: false);
		if (finallyCleanups.Count > 0 && currentFunctionReturnType != "void")
		{
			DeclarationStatement returnLocal = CreateGeneratedLocal(NewGeneratedLocalName("return"), currentFunctionReturnType, new NamedTypeReference { Name = currentFunctionReturnType, ResolvedType = currentFunctionReturnType }, new DefaultExpression { ResolvedType = currentFunctionReturnType });
			statements.Add(returnLocal);
			tryCleanupScope.ReturnTarget = returnLocal.Target;
			tryCleanupScope.ReturnType = currentFunctionReturnType;
		}
		currentCleanupScopes.Add(tryCleanupScope);

		if (tryStatement.Body is not null)
			statements.Add(RewriteStatement(tryStatement.Body));
		if (!EndsWithTransfer(statements))
		{
			string targetLabel = tryStatement.Finally is null ? doneLabel : finallyLabel;
			statements.Add(new GotoStatement { TargetName = targetLabel, ResolvedType = "void" });
			needsDoneLabel |= tryStatement.Finally is null;
			needsFinallyLabel |= tryStatement.Finally is not null;
		}
		currentCleanupScopes.RemoveAt(currentCleanupScopes.Count - 1);
		currentThrowHandlers.Clear();
		currentThrowHandlers.AddRange(previousHandlers);

		for (int i = 0; i < tryStatement.Catches.Count; i++)
		{
			CatchStatement catchStatement = tryStatement.Catches[i];
			ThrowHandler handler = handlers[i];
			statements.Add(new LabelStatement { Name = handler.LabelName, ResolvedType = "void" });

			List<ThrowHandler> catchPreviousHandlers = [.. currentThrowHandlers];
			currentThrowHandlers.AddRange(previousHandlers);
			if (finallyCleanups.Count > 0)
				currentCleanupScopes.Add(tryCleanupScope);
			if (catchStatement.Body is not null)
				statements.Add(RewriteStatement(catchStatement.Body));
			if (finallyCleanups.Count > 0)
				currentCleanupScopes.RemoveAt(currentCleanupScopes.Count - 1);
			currentThrowHandlers.Clear();
			currentThrowHandlers.AddRange(catchPreviousHandlers);
			if (!EndsWithTransfer(statements))
			{
				string targetLabel = tryStatement.Finally is null ? doneLabel : finallyLabel;
				statements.Add(new GotoStatement { TargetName = targetLabel, ResolvedType = "void" });
				needsDoneLabel |= tryStatement.Finally is null;
				needsFinallyLabel |= tryStatement.Finally is not null;
			}
		}

		AppendCleanupScopeExit(statements, tryCleanupScope);
		if (tryStatement.Finally is not null && needsFinallyLabel)
		{
			statements.Add(new LabelStatement { Name = finallyLabel, ResolvedType = "void" });
			foreach (Statement cleanup in finallyCleanups)
				statements.Add(CloneStatementForCleanup(cleanup));
			needsDoneLabel = true;
		}
		if (needsDoneLabel)
			statements.Add(new LabelStatement { Name = doneLabel, ResolvedType = "void" });
		return CreateBlock(statements);
	}

	static bool EndsWithTransfer(List<Statement> statements)
	{
		for (int i = statements.Count - 1; i >= 0; i--)
		{
			if (statements[i] is LabelStatement)
				continue;
			return IsTransferStatement(statements[i]);
		}
		return false;
	}

	static bool IsTransferStatement(Statement statement)
	{
		return statement switch
		{
			ReturnStatement or GotoStatement or BreakStatement or ContinueStatement => true,
			BlockStatement block => EndsWithTransfer(block.Statements),
			_ => false
		};
	}

	void AppendCleanupScopeExit(List<Statement> statements, CleanupScope cleanupScope)
	{
		if (cleanupScope.ExitLabelName is null)
			return;
		statements.Add(new LabelStatement { Name = cleanupScope.ExitLabelName, ResolvedType = "void" });
		foreach (Statement cleanup in cleanupScope.Statements)
			statements.Add(CloneStatementForCleanup(cleanup));
		if (cleanupScope.ReturnTarget is not null)
		{
			statements.Add(new ReturnStatement
			{
				ResolvedType = "void",
				Expression = cleanupScope.ReturnType == "void" ? null : CreateVariableReference(cleanupScope.ReturnTarget, cleanupScope.ReturnType)
			});
		}
		cleanupScope.ExitLabelName = null;
		cleanupScope.ReturnTarget = null;
		cleanupScope.ReturnType = "void";
	}

	Expression? RewriteFinallyDeleteExpression(FinallyDeleteExpression finallyDelete)
	{
		Expression? value = LowerExpression(finallyDelete.Expression);
		if (value is null || currentStatementPrefix is null || currentCleanupScopes.Count == 0)
			return value;

		string valueType = value.ResolvedType ?? finallyDelete.ResolvedType ?? ErrorType;
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("finally"), valueType, new NamedTypeReference { Name = valueType, ResolvedType = valueType }, value);
		currentStatementPrefix.Add(local);
		Expression reference = CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType);
		currentCleanupScopes[^1].Statements.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = RewriteDeleteExpression(CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType))
		});
		return reference;
	}

	Statement WithPendingCleanups(Statement transfer)
	{
		CleanupScope? exitScope = GetCleanupExitScope();
		if (exitScope is null || transfer is GotoStatement)
			return transfer;

		exitScope.ExitLabelName ??= NewGeneratedLabelName("cleanup");
		if (transfer is ReturnStatement returnStatement)
			return CreateCleanupReturnTransfer(returnStatement, exitScope);

		return new GotoStatement { TargetName = exitScope.ExitLabelName, ResolvedType = "void" };
	}

	Statement CreateCleanupReturnTransfer(ReturnStatement returnStatement, CleanupScope exitScope)
	{
		string returnType = currentRewriteFunction?.ResolvedType ?? "void";
		if (returnType == "void")
			return new GotoStatement { TargetName = exitScope.ExitLabelName, ResolvedType = "void" };

		exitScope.ReturnType = returnType;
		if (exitScope.ReturnTarget is null)
		{
			DeclarationStatement returnLocal = CreateGeneratedLocal(NewGeneratedLocalName("return"), returnType, new NamedTypeReference { Name = returnType, ResolvedType = returnType }, new DefaultExpression { ResolvedType = returnType });
			currentStatementPrefix?.Add(returnLocal);
			exitScope.ReturnTarget = returnLocal.Target;
		}

		return CreateBlock(
		[
			new ExpressionStatement
			{
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					Target = CreateVariableReference(exitScope.ReturnTarget, returnType),
					Operator = AssignmentOperator.Assign,
					Value = returnStatement.Expression ?? new DefaultExpression { ResolvedType = returnType },
					ResolvedType = returnType
				}
			},
			new GotoStatement { TargetName = exitScope.ExitLabelName, ResolvedType = "void" }
		]);
	}

	CleanupScope? GetCleanupExitScope()
	{
		for (int i = 0; i < currentCleanupScopes.Count; i++)
		{
			if (currentCleanupScopes[i].Statements.Count > 0)
				return currentCleanupScopes[i];
		}
		return null;
	}

	Statement RewriteWhileStatementWithThrowingCondition(WhileStatement whileStatement)
	{
		Expression condition = whileStatement.Condition!;
		List<Statement>? previousPrefix = currentStatementPrefix;
		List<Statement>? previousSuffix = currentStatementSuffix;
		List<Statement> conditionStatements = [];
		currentStatementPrefix = conditionStatements;
		currentStatementSuffix = [];
		Expression loweredCondition = HoistThrowingExpression(condition);
		conditionStatements.AddRange(currentStatementSuffix);
		currentStatementPrefix = previousPrefix;
		currentStatementSuffix = previousSuffix;

		BlockStatement body = new() { ResolvedType = "void" };
		body.Statements.AddRange(conditionStatements);
		body.Statements.Add(new IfStatement
		{
			ResolvedType = "void",
			Condition = new UnaryExpression
			{
				Operator = UnaryOperator.LogicalNot,
				Operand = loweredCondition,
				ResolvedType = "bool"
			},
			Body = new BreakStatement { ResolvedType = "void" }
		});
		if (whileStatement.Body is not null)
			body.Statements.Add(RewriteStatement(whileStatement.Body));

		whileStatement.Condition = new LiteralExpression { Kind = LiteralKind.True, Text = "true", Value = true, ResolvedType = "bool" };
		whileStatement.Body = body;
		return whileStatement;
	}

	List<Statement> GetPendingCleanups()
	{
		return GetPendingCleanups(includeCatchExitCleanups: true);
	}

	List<Statement> GetPendingCleanups(bool includeCatchExitCleanups)
	{
		List<Statement> cleanups = [];
		for (int i = currentCleanupScopes.Count - 1; i >= 0; i--)
		{
			CleanupScope cleanupScope = currentCleanupScopes[i];
			if (!includeCatchExitCleanups && !cleanupScope.RunBeforeCatch)
				continue;
			List<Statement> scope = cleanupScope.Statements;
			for (int j = scope.Count - 1; j >= 0; j--)
				cleanups.Add(CloneStatementForCleanup(scope[j]));
		}
		return cleanups;
	}

	Statement CloneStatementForCleanup(Statement statement)
	{
		return statement;
	}

	void LowerThrowingArguments(CallExpression call)
	{
		if (currentStatementPrefix is null)
			return;
		for (int i = 0; i < call.Arguments.Count; i++)
		{
			ArgumentExpression argument = call.Arguments[i];
			if (argument.Value is null || !ContainsUncaughtThrow(argument.Value))
				continue;

			string thrownType = GetExpressionThrownType(argument.Value) ?? ErrorType;
			DeclarationTarget errorTarget;
			if (TryGetImplicitHandlerTarget(thrownType, out DeclarationTarget? handlerTarget))
			{
				errorTarget = handlerTarget;
			}
			else if (TryGetFunctionThrownTarget(thrownType, out DeclarationTarget? thrownTarget))
			{
				errorTarget = thrownTarget;
			}
			else
			{
				DeclarationStatement errorLocal = CreateErrorLocal(thrownType);
				currentStatementPrefix.Add(errorLocal);
				errorTarget = errorLocal.Target;
			}
			DeclarationTarget? previousCatch = currentImplicitCatchTarget;
			currentImplicitCatchTarget = errorTarget;
			Expression? value = LowerExpressionForThrowCapture(argument.Value, errorTarget);
			currentImplicitCatchTarget = previousCatch;

			string localName = NewGeneratedLocalName("arg");
			DeclarationStatement local = CreateGeneratedLocal(localName, value?.ResolvedType ?? argument.ResolvedType ?? ErrorType, new NamedTypeReference { Name = value?.ResolvedType ?? argument.ResolvedType ?? ErrorType, ResolvedType = value?.ResolvedType ?? argument.ResolvedType ?? ErrorType }, value);
			currentStatementPrefix.Add(local);
			currentStatementPrefix.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
			argument.Value = CreateVariableReference(local.Target, local.Target.ResolvedType ?? ErrorType);
			argument.ResolvedType = argument.Value.ResolvedType;
		}
	}

	Expression HoistThrowingExpression(Expression expression)
	{
		if (currentStatementPrefix is null)
			return LowerExpression(expression) ?? expression;

		string thrownType = GetExpressionThrownType(expression) ?? ErrorType;
		DeclarationTarget errorTarget;
		if (TryGetImplicitHandlerTarget(thrownType, out DeclarationTarget? handlerTarget))
		{
			errorTarget = handlerTarget;
		}
		else if (TryGetFunctionThrownTarget(thrownType, out DeclarationTarget? thrownTarget))
		{
			errorTarget = thrownTarget;
		}
		else
		{
			DeclarationStatement errorLocal = CreateErrorLocal(thrownType);
			currentStatementPrefix.Add(errorLocal);
			errorTarget = errorLocal.Target;
		}
		DeclarationTarget? previousCatch = currentImplicitCatchTarget;
		currentImplicitCatchTarget = errorTarget;
		Expression? value = LowerExpressionForThrowCapture(expression, errorTarget);
		currentImplicitCatchTarget = previousCatch;

		string localName = NewGeneratedLocalName("value");
		string valueType = value?.ResolvedType ?? expression.ResolvedType ?? ErrorType;
		DeclarationStatement local = CreateGeneratedLocal(localName, valueType, new NamedTypeReference { Name = valueType, ResolvedType = valueType }, value);
		currentStatementPrefix.Add(local);
		currentStatementPrefix.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
		return CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType);
	}

	bool TryGetFunctionThrownTarget(string errorType, out DeclarationTarget target)
	{
		if (currentThrowHandlers.Count == 0 && GetFunctionThrownParameter(currentRewriteFunction) is ParameterDefinition thrownParameter && CanImplicitlyConvert(errorType, thrownParameter.ResolvedType ?? errorType))
		{
			target = new DeclarationTarget
			{
				ResolvedType = thrownParameter.ResolvedType ?? errorType
			};
			target.Names.Add(thrownParameter.Name ?? "error");
			return true;
		}

		target = new DeclarationTarget { ResolvedType = errorType };
		return false;
	}

	bool TryGetImplicitHandlerTarget(string errorType, out DeclarationTarget target)
	{
		foreach (ThrowHandler handler in currentThrowHandlers)
		{
			if (CanImplicitlyConvert(errorType, handler.ErrorType))
			{
				target = handler.ErrorTarget;
				return true;
			}
		}

		target = new DeclarationTarget { ResolvedType = errorType };
		return false;
	}

	Expression? LowerExpressionForThrowCapture(Expression? expression, DeclarationTarget errorTarget)
	{
		if (expression is null)
			return null;

		switch (expression)
		{
			case BinaryExpression { Operator: BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr } binary:
				return LowerShortCircuitExpressionForThrowCapture(binary, errorTarget);

			case BinaryExpression binary:
				if (CountUncaughtThrows(binary) <= 1)
					return LowerExpression(expression);
				binary.Left = LowerOperandForThrowCapture(binary.Left, errorTarget);
				binary.Right = LowerOperandForThrowCapture(binary.Right, errorTarget);
				return expression;

			case ConditionalExpression conditional:
				return LowerConditionalExpressionForThrowCapture(conditional, errorTarget);

			case ParenthesizedExpression parenthesized:
				parenthesized.Expression = LowerExpressionForThrowCapture(parenthesized.Expression, errorTarget);
				return LowerExpression(expression);

			case UnaryExpression unary when unary.Operator != UnaryOperator.Throw:
				unary.Operand = LowerOperandForThrowCapture(unary.Operand, errorTarget);
				return LowerExpression(expression);

			default:
				return LowerExpression(expression);
		}
	}

	Expression LowerShortCircuitExpressionForThrowCapture(BinaryExpression binary, DeclarationTarget errorTarget)
	{
		string valueType = binary.ResolvedType ?? "bool";
		Expression? left = LowerExpressionForThrowCapture(binary.Left, errorTarget);
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("value"), valueType, new NamedTypeReference { Name = valueType, ResolvedType = valueType }, left);
		currentStatementPrefix?.Add(local);
		currentStatementPrefix?.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));

		Expression localReference = CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType);
		List<Statement> branchStatements = [];
		List<Statement>? previousPrefix = currentStatementPrefix;
		List<Statement>? previousSuffix = currentStatementSuffix;
		currentStatementPrefix = branchStatements;
		currentStatementSuffix = [];
		Expression? right = LowerExpressionForThrowCapture(binary.Right, errorTarget);
		branchStatements.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				Target = CreateVariableReference(local.Target, valueType),
				Operator = AssignmentOperator.Assign,
				Value = right,
				ResolvedType = valueType
			}
		});
		branchStatements.AddRange(currentStatementSuffix);
		branchStatements.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
		currentStatementPrefix = previousPrefix;
		currentStatementSuffix = previousSuffix;

		currentStatementPrefix?.Add(new IfStatement
		{
			ResolvedType = "void",
			Condition = binary.Operator == BinaryOperator.LogicalAnd
				? CreateVariableReference(local.Target, valueType)
				: new UnaryExpression
				{
					Operator = UnaryOperator.LogicalNot,
					Operand = CreateVariableReference(local.Target, valueType),
					ResolvedType = "bool"
				},
			Body = CreateBlock(branchStatements)
		});
		return localReference;
	}

	Expression LowerConditionalExpressionForThrowCapture(ConditionalExpression conditional, DeclarationTarget errorTarget)
	{
		string valueType = conditional.ResolvedType ?? ErrorType;
		Expression? condition = LowerExpressionForThrowCapture(conditional.Condition, errorTarget);
		currentStatementPrefix?.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("value"), valueType, new NamedTypeReference { Name = valueType, ResolvedType = valueType }, new DefaultExpression { ResolvedType = valueType });
		currentStatementPrefix?.Add(local);

		Statement trueBranch = CreateConditionalAssignmentBranch(local.Target, valueType, conditional.WhenTrue, errorTarget);
		Statement falseBranch = CreateConditionalAssignmentBranch(local.Target, valueType, conditional.WhenFalse, errorTarget);
		currentStatementPrefix?.Add(new IfStatement
		{
			ResolvedType = "void",
			Condition = condition,
			Body = trueBranch,
			ElseBody = falseBranch
		});
		return CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType);
	}

	Statement CreateConditionalAssignmentBranch(DeclarationTarget target, string valueType, Expression? value, DeclarationTarget errorTarget)
	{
		List<Statement> statements = [];
		List<Statement>? previousPrefix = currentStatementPrefix;
		List<Statement>? previousSuffix = currentStatementSuffix;
		currentStatementPrefix = statements;
		currentStatementSuffix = [];
		Expression? lowered = LowerExpressionForThrowCapture(value, errorTarget);
		statements.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				Target = CreateVariableReference(target, valueType),
				Operator = AssignmentOperator.Assign,
				Value = lowered,
				ResolvedType = valueType
			}
		});
		statements.AddRange(currentStatementSuffix);
		statements.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
		currentStatementPrefix = previousPrefix;
		currentStatementSuffix = previousSuffix;
		return CreateBlock(statements);
	}

	Expression? LowerOperandForThrowCapture(Expression? expression, DeclarationTarget errorTarget)
	{
		if (expression is null)
			return null;
		if (!ContainsUncaughtThrow(expression))
			return LowerExpression(expression);

		DeclarationTarget? previousCatch = currentImplicitCatchTarget;
		currentImplicitCatchTarget = errorTarget;
		Expression? lowered = LowerExpressionForThrowCapture(expression, errorTarget);
		currentImplicitCatchTarget = previousCatch;

		string valueType = lowered?.ResolvedType ?? expression.ResolvedType ?? ErrorType;
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("value"), valueType, new NamedTypeReference { Name = valueType, ResolvedType = valueType }, lowered);
		currentStatementPrefix?.Add(local);
		currentStatementPrefix?.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
		return CreateVariableReference(local.Target, local.Target.ResolvedType ?? valueType);
	}

	Expression LowerUncaughtThrowingCall(CallExpression call)
	{
		if (!IsUncaughtThrowingCall(call, out string? thrownType))
			return call;

		FunctionDefinition? function = callTargets.TryGetValue(call, out FunctionDefinition? found) ? found : null;
		bool usesThrownReturn = function is not null && GetFunctionThrownReturnType(function) is not null;

		if (currentImplicitCatchTarget is not null)
		{
			if (!usesThrownReturn)
			{
				call.Arguments.Add(new ArgumentExpression { Modifier = ArgumentModifier.Catch, Value = CreateVariableReference(currentImplicitCatchTarget, currentImplicitCatchTarget.ResolvedType ?? thrownType ?? ErrorType), ResolvedType = currentImplicitCatchTarget.ResolvedType ?? thrownType ?? ErrorType });
				return call;
			}

			return new AssignmentExpression
			{
				Target = CreateVariableReference(currentImplicitCatchTarget, currentImplicitCatchTarget.ResolvedType ?? thrownType ?? ErrorType),
				Operator = AssignmentOperator.Assign,
				Value = call,
				ResolvedType = currentImplicitCatchTarget.ResolvedType ?? thrownType ?? ErrorType
			};
		}

		if (currentStatementPrefix is null || currentStatementSuffix is null)
			return call;

		DeclarationTarget errorTarget;
		if (TryGetImplicitHandlerTarget(thrownType ?? ErrorType, out DeclarationTarget? handlerTarget))
		{
			errorTarget = handlerTarget;
		}
		else if (TryGetFunctionThrownTarget(thrownType ?? ErrorType, out DeclarationTarget? thrownTarget))
		{
			errorTarget = thrownTarget;
		}
		else
		{
			DeclarationStatement errorLocal = CreateErrorLocal(thrownType ?? ErrorType);
			currentStatementPrefix.Add(errorLocal);
			errorTarget = errorLocal.Target;
		}
		Expression result = call;
		if (usesThrownReturn)
		{
			result = new AssignmentExpression
			{
				Target = CreateVariableReference(errorTarget, errorTarget.ResolvedType ?? ErrorType),
				Operator = AssignmentOperator.Assign,
				Value = call,
				ResolvedType = errorTarget.ResolvedType ?? ErrorType
			};
		}
		else
		{
			call.Arguments.Add(new ArgumentExpression { Modifier = ArgumentModifier.Catch, Value = CreateVariableReference(errorTarget, errorTarget.ResolvedType ?? ErrorType), ResolvedType = errorTarget.ResolvedType ?? ErrorType });
		}
		currentStatementSuffix.AddRange(CreateThrowCheck(errorTarget, errorTarget.ResolvedType ?? ErrorType));
		return result;
	}

	DeclarationStatement CreateErrorLocal(string errorType)
	{
		string name = NewGeneratedLocalName("error");
		return CreateGeneratedLocal(name, errorType, new NamedTypeReference { Name = errorType, ResolvedType = errorType }, new DefaultExpression { ResolvedType = errorType });
	}

	List<Statement> CreateThrowCheck(DeclarationTarget errorTarget, string errorType)
	{
		Statement transfer = TryGetMatchingHandler(errorTarget, errorType, out ThrowHandler? handler)
			? CreateBlock(CreateHandlerGoto(handler))
			: IsFunctionThrownTarget(errorTarget)
			? CreateFunctionErrorExit()
			: CreateBlock(CreateThrowTransfer(CreateVariableReference(errorTarget, errorType), null));
		return
		[
			new IfStatement
			{
				ResolvedType = "void",
				Condition = new BinaryExpression
				{
					Left = CreateVariableReference(errorTarget, errorType),
					Operator = BinaryOperator.NotEqual,
					Right = new DefaultExpression { ResolvedType = errorType },
					ResolvedType = "bool"
				},
				Body = transfer
			}
		];
	}

	bool IsFunctionThrownTarget(DeclarationTarget target)
	{
		return GetFunctionThrownParameter(currentRewriteFunction) is ParameterDefinition thrownParameter
			&& target.Names.Count == 1
			&& target.Names[0] == (thrownParameter.Name ?? "error");
	}

	Statement CreateFunctionErrorExit()
	{
		if (GetCleanupExitScope() is not null)
			return WithPendingCleanups(CreateDefaultReturn());

		currentFunctionExitLabel ??= NewGeneratedLabelName("exit");
		if (currentFunctionReturnType != "void" && currentFunctionReturnTarget is null)
		{
			DeclarationStatement returnLocal = CreateGeneratedLocal(NewGeneratedLocalName("return"), currentFunctionReturnType, new NamedTypeReference { Name = currentFunctionReturnType, ResolvedType = currentFunctionReturnType }, new DefaultExpression { ResolvedType = currentFunctionReturnType });
			currentStatementPrefix?.Add(returnLocal);
			currentFunctionReturnTarget = returnLocal.Target;
		}
		return new GotoStatement { TargetName = currentFunctionExitLabel, ResolvedType = "void" };
	}

	bool TryGetMatchingHandler(DeclarationTarget target, string errorType, out ThrowHandler handler)
	{
		foreach (ThrowHandler candidate in currentThrowHandlers)
		{
			if (ReferenceEquals(candidate.ErrorTarget, target) && CanImplicitlyConvert(errorType, candidate.ErrorType))
			{
				handler = candidate;
				return true;
			}
		}

		handler = null!;
		return false;
	}

	List<Statement> CreateHandlerGoto(ThrowHandler handler)
	{
		List<Statement> statements = GetPendingCleanups(includeCatchExitCleanups: false);
		statements.Add(new GotoStatement { TargetName = handler.LabelName, ResolvedType = "void" });
		return statements;
	}

	List<Statement> CreateThrowTransfer(Expression? value, SyntaxNode? syntax)
	{
		value = LowerExpression(value);
		string thrownType = value?.ResolvedType ?? ErrorType;
		foreach (ThrowHandler handler in currentThrowHandlers)
		{
			if (!CanImplicitlyConvert(thrownType, handler.ErrorType))
				continue;
			List<Statement> transfer =
			[
				new ExpressionStatement
				{
					ResolvedType = "void",
					Expression = new AssignmentExpression
					{
						Target = CreateVariableReference(handler.ErrorTarget, handler.ErrorType),
						Operator = AssignmentOperator.Assign,
						Value = value,
						ResolvedType = handler.ErrorType
					}
				}
			];
			transfer.AddRange(GetPendingCleanups(includeCatchExitCleanups: false));
			transfer.Add(new GotoStatement { TargetName = handler.LabelName, ResolvedType = "void" });
			return transfer;
		}

		ParameterDefinition? thrownParameter = GetFunctionThrownParameter(currentRewriteFunction);
		if (thrownParameter is not null)
		{
			return
			[
				new ExpressionStatement
				{
					ResolvedType = "void",
					Expression = new AssignmentExpression
					{
						Target = CreateVariableReference(thrownParameter, thrownParameter.ResolvedType ?? thrownType),
						Operator = AssignmentOperator.Assign,
						Value = value,
						ResolvedType = thrownParameter.ResolvedType ?? thrownType
					}
				},
				WithPendingCleanups(CreateDefaultReturn())
			];
		}

		if (GetFunctionThrownReturnType(currentRewriteFunction) is string thrownReturnType && CanImplicitlyConvert(thrownType, thrownReturnType))
		{
			return
			[
				WithPendingCleanups(new ReturnStatement
				{
					ResolvedType = "void",
					Expression = value
				})
			];
		}

		return [new ExpressionStatement { SourceSyntax = syntax, ResolvedType = "void", Expression = new UnaryExpression { Operator = UnaryOperator.Throw, Operand = value, ResolvedType = "void" } }];
	}

	ReturnStatement CreateDefaultReturn()
	{
		string returnType = currentRewriteFunction?.ResolvedType ?? "void";
		return new ReturnStatement
		{
			ResolvedType = "void",
			Expression = returnType == "void" ? null : new DefaultExpression { ResolvedType = returnType }
		};
	}

	static ParameterDefinition? GetFunctionThrownParameter(FunctionDefinition? function)
	{
		foreach (ParameterDefinition parameter in function?.Parameters ?? [])
		{
			if (parameter.Modifier == ParameterModifier.Thrown)
				return parameter;
		}
		return null;
	}

	static string? GetFunctionThrownReturnType(FunctionDefinition? function)
	{
		string? returnType = function?.ReturnType?.ResolvedType;
		return returnType is not null && returnType.StartsWith("thrown(", StringComparison.Ordinal) && returnType.EndsWith(")", StringComparison.Ordinal)
			? returnType["thrown(".Length..^1]
			: null;
	}

	bool ContainsUncaughtThrow(Expression expression)
	{
		if (IsUncaughtThrowingCall(expression, out _))
			return true;
		foreach (Expression child in EnumerateChildExpressions(expression))
		{
			if (ContainsUncaughtThrow(child))
				return true;
		}
		return false;
	}

	string? GetExpressionThrownType(Expression expression)
	{
		if (IsUncaughtThrowingCall(expression, out string? thrownType))
			return thrownType;
		foreach (Expression child in EnumerateChildExpressions(expression))
		{
			if (GetExpressionThrownType(child) is string childThrown)
				return childThrown;
		}
		return null;
	}

	int CountUncaughtThrows(Expression? expression)
	{
		if (expression is null)
			return 0;
		int count = IsUncaughtThrowingCall(expression, out _) ? 1 : 0;
		foreach (Expression child in EnumerateChildExpressions(expression))
			count += CountUncaughtThrows(child);
		return count;
	}

	bool IsUncaughtThrowingCall(Expression expression, out string? thrownType)
	{
		thrownType = null;
		if (expression is not CallExpression call || !callTargets.TryGetValue(call, out FunctionDefinition? function))
			return false;
		thrownType = GetFunctionThrownType(function);
		if (thrownType is null)
			return false;
		foreach (ArgumentExpression argument in call.Arguments)
		{
			if (argument.Modifier == ArgumentModifier.Catch)
				return false;
		}
		return true;
	}

	IEnumerable<Expression> EnumerateChildExpressions(Expression expression)
	{
		switch (expression)
		{
			case ParenthesizedExpression parenthesized when parenthesized.Expression is not null:
				yield return parenthesized.Expression;
				break;
			case CastExpression cast when cast.Expression is not null:
				yield return cast.Expression;
				break;
			case UnaryExpression unary:
				if (unary.Operand is not null)
					yield return unary.Operand;
				break;
			case BinaryExpression binary:
				if (binary.Left is not null)
					yield return binary.Left;
				if (binary.Right is not null)
					yield return binary.Right;
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
				foreach (ArgumentExpression argument in call.Arguments)
					if (argument.Value is not null)
						yield return argument.Value;
				break;
		}
	}

	bool IsPropertyGetterReference(MemberReferenceExpression member)
	{
		return member.Member is FunctionDefinition function && function.Name == "get" + member.Name;
	}

	bool IsPropertySetterReference(MemberReferenceExpression member)
	{
		return member.Member is FunctionDefinition function && function.Name == "set" + member.Name;
	}

	CallExpression RewritePropertyGetterCall(MemberReferenceExpression getter, List<ArgumentExpression> arguments)
	{
		FunctionDefinition function = (FunctionDefinition)getter.Member!;
		getter.Target = LowerExpression(getter.Target);
		getter.Name = function.Name;
		getter.ResolvedType = BuildFunctionValueType(function, isInstance: true);

		CallExpression call = new()
		{
			SourceSyntax = getter.SourceSyntax,
			Target = getter,
			ResolvedType = function.ResolvedType ?? ErrorType
		};
		for (int i = 0; i < arguments.Count; i++)
			call.Arguments.Add(LowerArgument(arguments[i]));
		return call;
	}

	CallExpression RewritePropertySetterCall(MemberReferenceExpression setter, List<ArgumentExpression> arguments, Expression? value)
	{
		FunctionDefinition function = (FunctionDefinition)setter.Member!;
		setter.Target = LowerExpression(setter.Target);
		setter.Name = function.Name;
		setter.ResolvedType = BuildFunctionValueType(function, isInstance: true);

		CallExpression call = new()
		{
			SourceSyntax = setter.SourceSyntax,
			Target = setter,
			ResolvedType = function.ResolvedType ?? "void"
		};
		for (int i = 0; i < arguments.Count; i++)
			call.Arguments.Add(LowerArgument(arguments[i]));

		Expression? loweredValue = LowerExpression(value);
		ParameterDefinition? valueParameter = function.Parameters.Count == 0 ? null : function.Parameters[^1];
		if (valueParameter?.Type is not null)
			loweredValue = LowerInterfaceConversion(valueParameter.Type, loweredValue);
		call.Arguments.Add(new ArgumentExpression
		{
			Value = loweredValue,
			ResolvedType = loweredValue?.ResolvedType ?? valueParameter?.ResolvedType ?? ErrorType
		});
		return call;
	}

	void LowerInterfaceCall(CallExpression call)
	{
		if (call.Target is not MemberReferenceExpression { Target: not null, Member: FunctionDefinition function } member)
			return;
		if (FindContainingType(function) is not InterfaceDefinition)
			return;
		if (function.Modifier == FunctionModifier.Constructor)
			return;

		Expression context = member.Target;
		call.Arguments.Insert(0, new ArgumentExpression
		{
			Value = context,
			ResolvedType = context.ResolvedType
		});
	}

	void LowerCallArgumentConversions(CallExpression call)
	{
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function))
			return;

		List<ParameterDefinition> parameters = GetCallableParameters(function.Parameters);
		for (int i = 0; i < call.Arguments.Count && i < parameters.Count; i++)
		{
			if (parameters[i].Modifier == ParameterModifier.Out)
				continue;
			call.Arguments[i].Value = LowerInterfaceConversion(parameters[i].Type, call.Arguments[i].Value);
			call.Arguments[i].ResolvedType = call.Arguments[i].Value?.ResolvedType ?? call.Arguments[i].ResolvedType;
		}
	}

	Expression? LowerInterfaceConversion(TypeReference? targetType, Expression? value)
	{
		if (targetType is not PointerTypeReference targetPointer || value is null)
			return value;
		if (!TryGetInterfaceDefinition(targetPointer.ElementType ?? targetPointer, out InterfaceDefinition? targetInterface) || targetInterface is null)
			return value;

		string sourceType = value.ResolvedType ?? "";
		if (sourceType == targetPointer.ResolvedType || TryGetPointerElementType(sourceType) == targetInterface.Name)
			return value;

		if (TryGetPointerElementType(sourceType) is string className
			&& typeDefinitions.TryGetValue(BaseTypeName(className), out TypeDefinition? typeDefinition)
			&& typeDefinition is ClassDefinition classDefinition
			&& TryFindInterfaceLowering(classDefinition, targetInterface, out InterfaceImplementationLowering? lowering)
			&& lowering is not null)
		{
			return lowering.Field is null ? value : AddressOfInterfaceField(value, lowering.Field);
		}

		if (typeDefinitions.TryGetValue(BaseTypeName(sourceType), out TypeDefinition? sourceTypeDefinition)
			&& sourceTypeDefinition is StructDefinition structDefinition
			&& TryFindInterfaceLowering(structDefinition, targetInterface, out InterfaceImplementationLowering? structLowering)
			&& structLowering is not null)
		{
			return CreateStructInterfaceConversion(value, structDefinition, targetInterface, structLowering);
		}

		if (TryGetPointerElementType(sourceType) is string sourceInterfaceName
			&& typeDefinitions.TryGetValue(BaseTypeName(sourceInterfaceName), out TypeDefinition? sourceDefinition)
			&& sourceDefinition is InterfaceDefinition sourceInterface
			&& InterfaceContainsBase(sourceInterface, targetInterface))
		{
			return new CastExpression
			{
				Type = PointerTo(PointerTo(InterfaceType(targetInterface))),
				Kind = CastKind.Type,
				Expression = value,
				ResolvedType = $"{targetInterface.Name}**"
			};
		}

		return value;
	}

	bool IsInterfacePointerType(TypeReference? type)
	{
		return type is PointerTypeReference pointer
			&& TryGetInterfaceDefinition(pointer.ElementType ?? pointer, out InterfaceDefinition? interfaceDefinition)
			&& interfaceDefinition is not null;
	}

	Expression CreateStructInterfaceConversion(Expression value, StructDefinition structDefinition, InterfaceDefinition targetInterface, InterfaceImplementationLowering lowering)
	{
		if (currentStatementPrefix is null)
			return value;

		string localName = NewGeneratedLocalName("iface");
		TypeReference indirectType = InterfaceIndirectType(targetInterface, structDefinition);
		DeclarationStatement local = CreateGeneratedLocal(localName, indirectType.ResolvedType ?? $"{InterfaceIndirectName(targetInterface)}<{structDefinition.Name}>", indirectType, initialValue: null);
		currentStatementPrefix.Add(local);

		Expression localReference = CreateVariableReference(local.Target, local.Target.ResolvedType ?? indirectType.ResolvedType ?? ErrorType);
		currentStatementPrefix.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				Target = CreateInterfaceIndirectMember(localReference, "_vt", $"{targetInterface.Name}*"),
				Operator = AssignmentOperator.Assign,
				Value = new UnaryExpression
				{
					Operator = UnaryOperator.AddressOf,
					Operand = new NamedExpression
					{
						Name = lowering.VTable.Name,
						ResolvedType = lowering.VTable.ResolvedType
					},
					ResolvedType = $"{targetInterface.Name}*"
				},
				ResolvedType = $"{targetInterface.Name}*"
			}
		});
		currentStatementPrefix.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				Target = CreateInterfaceIndirectMember(localReference, "ctx", $"{structDefinition.Name}*"),
				Operator = AssignmentOperator.Assign,
				Value = new UnaryExpression
				{
					Operator = UnaryOperator.AddressOf,
					Operand = value,
					ResolvedType = $"{structDefinition.Name}*"
				},
				ResolvedType = $"{structDefinition.Name}*"
			}
		});

		return new UnaryExpression
		{
			Operator = UnaryOperator.AddressOf,
			Operand = CreateInterfaceIndirectMember(localReference, "_vt", $"{targetInterface.Name}*"),
			ResolvedType = $"{targetInterface.Name}**"
		};
	}

	static MemberReferenceExpression CreateInterfaceIndirectMember(Expression target, string name, string type)
	{
		return new MemberReferenceExpression
		{
			Target = target,
			Name = name,
			ResolvedType = type
		};
	}

	Expression AddressOfInterfaceField(Expression instance, FieldDefinition field)
	{
		return new UnaryExpression
		{
			Operator = UnaryOperator.AddressOf,
			Operand = new MemberReferenceExpression
			{
				Target = instance,
				Name = field.Name,
				Member = field,
				ResolvedType = field.ResolvedType
			},
			ResolvedType = $"{field.ResolvedType}*"
		};
	}

	bool NeedsVirtualTableAssignment(TypeDefinition type)
	{
		return type is ClassDefinition classDefinition && virtualClassLowerings.ContainsKey(classDefinition);
	}

	Expression? CreateVirtualTableAssignment(Expression instance, TypeDefinition type)
	{
		if (type is not ClassDefinition classDefinition || !virtualClassLowerings.TryGetValue(classDefinition, out VirtualClassLowering? lowering))
			return null;

		VirtualClassLowering root = GetRootVirtualLowering(lowering);
		return new AssignmentExpression
		{
			Target = new MemberReferenceExpression
			{
				Target = instance,
				Name = VirtualTableFieldName,
				Member = root.Field,
				ResolvedType = $"{root.VTableType.Name}*"
			},
			Operator = AssignmentOperator.Assign,
			Value = CreateVirtualTablePointer(lowering, root),
			ResolvedType = $"{root.VTableType.Name}*"
		};
	}

	VirtualClassLowering GetRootVirtualLowering(VirtualClassLowering lowering)
	{
		while (lowering.BaseLowering is not null)
			lowering = lowering.BaseLowering;
		return lowering;
	}

	Expression CreateVirtualTablePointer(VirtualClassLowering target, VirtualClassLowering root)
	{
		Expression expression = new NamedExpression
		{
			Name = target.VTable?.Name ?? VirtualTableVariableName(target.Class),
			ResolvedType = target.VTableType.Name
		};
		List<string> path = [];
		for (VirtualClassLowering? current = target; current is not null && !ReferenceEquals(current, root); current = current.BaseLowering)
		{
			if (current.BaseClass is not null)
				path.Add(current.BaseClass.Name);
		}
		for (int i = path.Count - 1; i >= 0; i--)
		{
			expression = new MemberReferenceExpression
			{
				Target = expression,
				Name = path[i],
				ResolvedType = i == 0 ? root.VTableType.Name : ErrorType
			};
		}
		return new UnaryExpression
		{
			Operator = UnaryOperator.AddressOf,
			Operand = expression,
			ResolvedType = $"{root.VTableType.Name}*"
		};
	}

	bool TryFindInterfaceLowering(ClassDefinition classDefinition, InterfaceDefinition targetInterface, out InterfaceImplementationLowering? lowering)
	{
		lowering = null;
		if (!classInterfaceLowerings.TryGetValue(classDefinition, out List<InterfaceImplementationLowering>? lowerings))
			return false;

		foreach (InterfaceImplementationLowering candidate in lowerings)
		{
			if (ReferenceEquals(candidate.Interface, targetInterface) || InterfaceContainsBase(candidate.Interface, targetInterface))
			{
				lowering = candidate;
				return true;
			}
		}
		return false;
	}

	bool TryFindInterfaceLowering(StructDefinition structDefinition, InterfaceDefinition targetInterface, out InterfaceImplementationLowering? lowering)
	{
		lowering = null;
		if (!structInterfaceLowerings.TryGetValue(structDefinition, out List<InterfaceImplementationLowering>? lowerings))
			return false;

		foreach (InterfaceImplementationLowering candidate in lowerings)
		{
			if (ReferenceEquals(candidate.Interface, targetInterface) || InterfaceContainsBase(candidate.Interface, targetInterface))
			{
				lowering = candidate;
				return true;
			}
		}
		return false;
	}

	bool InterfaceContainsBase(InterfaceDefinition source, InterfaceDefinition target)
	{
		if (ReferenceEquals(source, target))
			return true;
		foreach (TypeReference baseType in source.BaseTypes)
		{
			if (TryGetInterfaceDefinition(baseType, out InterfaceDefinition? baseInterface)
				&& baseInterface is not null
				&& InterfaceContainsBase(baseInterface, target))
				return true;
		}
		return false;
	}

	ArgumentExpression LowerArgument(ArgumentExpression argument)
	{
		if (argument.Target is not null)
			LowerArgumentDeclaration(argument);

		argument.Value = LowerExpression(argument.Value);
		return argument;
	}

	void LowerArgumentDeclaration(ArgumentExpression argument)
	{
		if (currentStatementPrefix is null || argument.Target is not DeclarationTarget target)
			return;

		DeclarationStatement declaration = new()
		{
			SourceSyntax = target.SourceSyntax,
			ResolvedType = "void"
		};
		declaration.Target.Type = target.Type is AutoTypeReference ? null : CloneType(target.Type);
		declaration.Target.ResolvedType = target.ResolvedType;
		declaration.Target.SourceSyntax = target.SourceSyntax;
		foreach (string name in target.Names)
			declaration.Target.Names.Add(name);

		currentStatementPrefix.Add(declaration);
		argument.Target = null;
		argument.Type = null;
		argument.Value = CreateVariableReference(declaration.Target, declaration.Target.ResolvedType ?? argument.ResolvedType ?? ErrorType);
		argument.ResolvedType = argument.Value.ResolvedType;
	}

	void LowerInitializer(InitializerExpression? initializer)
	{
		if (initializer is null)
			return;

		foreach (InitializerItem item in initializer.Items)
			item.Expression = LowerExpression(item.Expression);
	}

	Expression RewriteConstruction(ConstructionExpression construction)
	{
		TypeReference? type = construction.Type;
		string typeName = type?.ResolvedType ?? BaseConstructedType(construction.ResolvedType);
		if (construction.ElementCount is not null && type is not null)
			return CreateArrayConstruction(type, construction.ElementCount, construction.SourceSyntax, construction.ResolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return construction;

		return construction.Kind switch
		{
			ConstructionKind.New => CreateNewExpression(definition, construction.Arguments, construction.SourceSyntax, construction.ResolvedType),
			ConstructionKind.Init => (Expression?)CreateInitCallForConstruction(construction, target: null) ?? construction,
			_ => construction
		};
	}

	Expression CreateNewExpression(TypeDefinition type, List<ArgumentExpression> arguments, SyntaxNode? syntax, string? resolvedType)
	{
		TypeReference typeReference = TypeReferenceFor(type);
		FunctionDefinition? initNew = FindInitNewMethod(type, arguments.Count);
		if (initNew is null && !NeedsVirtualTableAssignment(type))
			return CreateAllocCall(typeReference, syntax);

		string localName = NewGeneratedLocalName("created");
		NamedExpression localReference = new()
		{
			Name = localName,
			ResolvedType = resolvedType ?? $"{type.Name}*"
		};
		GroupedExpression grouped = new()
		{
			SourceSyntax = syntax,
			ResolvedType = resolvedType ?? $"{type.Name}*"
		};
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = new AssignmentExpression
			{
				Target = localReference,
				Operator = AssignmentOperator.Assign,
				Value = CreateAllocCall(typeReference, syntax),
				ResolvedType = resolvedType ?? $"{type.Name}*"
			},
			ResolvedType = resolvedType ?? $"{type.Name}*"
		});
		if (CreateVirtualTableAssignment(localReference, type) is Expression vtableAssignment)
			grouped.Items.Add(new GroupedExpressionItem
			{
				Expression = vtableAssignment,
				ResolvedType = "void"
			});
		if (initNew is not null)
			grouped.Items.Add(new GroupedExpressionItem
			{
				Expression = CreateInitNewCall(localReference, initNew, arguments, syntax),
				ResolvedType = "void"
			});
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = localReference,
			ResolvedType = resolvedType ?? $"{type.Name}*"
		});
		return grouped;
	}

	CallExpression? CreateInitCallForConstruction(ConstructionExpression construction, Expression? target)
	{
		string typeName = construction.Type?.ResolvedType ?? BaseConstructedType(construction.ResolvedType);
		if (string.IsNullOrWhiteSpace(typeName) || !typeDefinitions.TryGetValue(typeName, out TypeDefinition? definition))
			return null;

		FunctionDefinition? initNew = FindInitNewMethod(definition, construction.Arguments.Count);
		if (initNew is null)
			return null;

		return CreateInitNewCall(target, initNew, construction.Arguments, construction.SourceSyntax);
	}

	Expression RewriteDeleteExpression(Expression? expression)
	{
		if (expression is NamedExpression { Qualifiers.Count: 0, Name: "base" } && CreateBaseDeleteCall() is Expression baseDelete)
			return baseDelete;

		Expression? target = LowerExpression(expression);
		string targetType = target?.ResolvedType ?? ErrorType;
		string? elementType = TryGetPointerElementType(targetType);
		bool isPointer = elementType is not null;
		bool isArray = TryGetArrayElementType(targetType) is not null;
		bool isThisPointer = target is ThisExpression
			&& typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? thisType)
			&& thisType is ClassDefinition;
		string deletedType = isPointer ? elementType ?? ErrorType : targetType;
		FunctionDefinition? opDelete = FindDeleteMethod(deletedType);

		if (!isPointer && !isThisPointer && !isArray && opDelete is null)
			Report(target?.SourceSyntax, $"delete requires a pointer or a type with a destructor, not '{targetType}'.");
		if (target is null)
			return new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "void" };

		if (isArray)
			return CreateFreeCall(CreateArrayElementsAccess(target));

		return CreateDeleteExpression(target, opDelete, isPointer || isThisPointer);
	}

	Expression? CreateBaseDeleteCall()
	{
		if (currentRewriteContainingType is not ClassDefinition classDefinition)
			return null;
		ClassDefinition? baseClass = GetDirectBaseClass(classDefinition);
		if (baseClass is null)
			return null;

		FunctionDefinition? opDelete = FindVirtualImplementationByName(baseClass, DeleteMethodName) ?? FindDeleteMethod(baseClass.Name);
		if (opDelete is null)
			return null;

		CallExpression call = new()
		{
			ResolvedType = "void",
			Target = CreateMethodReference(opDelete, "void")
		};
		if (HasWithinParameter(opDelete))
			call.Arguments.Add(new ArgumentExpression { Value = CurrentAllocator(), ResolvedType = AllocatorType });
		return call;
	}

	CallExpression CreateAllocCall(TypeReference type, SyntaxNode? syntax)
	{
		return CreateAllocCall(type, CurrentAllocator(), syntax);
	}

	CallExpression CreateAllocCall(TypeReference type, Expression allocator, SyntaxNode? syntax, Expression? length = null)
	{
		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = $"{type.ResolvedType}*",
			Target = new MemberReferenceExpression
			{
				Target = allocator,
				Name = "alloc",
				Member = allocatorAllocMethod,
				ResolvedType = allocatorAllocMethod.ResolvedType
			}
		};
		call.TypeArguments.Add(CloneType(type)!);
		call.Arguments.Add(new ArgumentExpression { Value = length ?? NumberLiteral("1", "nuint"), ResolvedType = "nuint" });
		call.Arguments.Add(new ArgumentExpression { Value = new SizeOfExpression { Type = CloneType(type), ResolvedType = "nuint" }, ResolvedType = "nuint" });
		call.Arguments.Add(new ArgumentExpression { Modifier = ArgumentModifier.Catch, Value = new NamedExpression { Name = "_", ResolvedType = "MemoryError" }, ResolvedType = "MemoryError" });
		return call;
	}

	CallExpression CreateInitNewCall(Expression? target, FunctionDefinition initNew, List<ArgumentExpression> arguments, SyntaxNode? syntax, Expression? allocatorArgument = null)
	{
		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Target = target is null
				? CreateMethodReference(initNew, "void")
				: new MemberReferenceExpression
				{
					Target = target,
					Name = initNew.Name,
					Member = initNew,
					ResolvedType = "void"
				}
		};

		foreach (ArgumentExpression argument in arguments)
			call.Arguments.Add(argument);
		if (HasWithinParameter(initNew))
			call.Arguments.Add(new ArgumentExpression { Value = allocatorArgument ?? CurrentAllocator(), ResolvedType = AllocatorType });
		return call;
	}

	CallExpression CreateDestructorCall(Expression target, FunctionDefinition opDelete, Expression? allocatorArgument = null)
	{
		CallExpression call = new()
		{
			ResolvedType = "void",
			Target = new MemberReferenceExpression
			{
				Target = target,
				Name = opDelete.Name,
				Member = opDelete,
				ResolvedType = "void"
			}
		};
		if (HasWithinParameter(opDelete))
			call.Arguments.Add(new ArgumentExpression { Value = allocatorArgument ?? CurrentAllocator(), ResolvedType = AllocatorType });
		return call;
	}

	CallExpression CreateFreeCall(Expression target)
	{
		return CreateFreeCall(target, CurrentAllocator());
	}

	CallExpression CreateFreeCall(Expression target, Expression allocator)
	{
		return new CallExpression
		{
			ResolvedType = "void",
			Target = new MemberReferenceExpression
			{
				Target = allocator,
				Name = "free",
				Member = allocatorFreeMethod,
				ResolvedType = allocatorFreeMethod.ResolvedType
			},
			Arguments =
			{
				new ArgumentExpression { Value = target, ResolvedType = target.ResolvedType }
			}
		};
	}

	Expression CreateDeleteExpression(Expression target, FunctionDefinition? opDelete, bool deallocate)
	{
		List<Expression> operations = [];
		if (opDelete is not null)
			operations.Add(CreateDestructorCall(target, opDelete));
		if (deallocate)
			operations.Add(CreateFreeCall(target));

		if (operations.Count == 0)
			return new LiteralExpression { Kind = LiteralKind.Null, Text = "null", ResolvedType = "void" };
		if (operations.Count == 1)
			return operations[0];

		GroupedExpression grouped = new()
		{
			ResolvedType = "void"
		};
		foreach (Expression operation in operations)
			grouped.Items.Add(new GroupedExpressionItem { Expression = operation, ResolvedType = operation.ResolvedType });
		return grouped;
	}

	Expression CreateArrayConstruction(TypeReference elementType, Expression length, SyntaxNode? syntax, string? resolvedType)
	{
		GroupedExpression grouped = new()
		{
			SourceSyntax = syntax,
			ResolvedType = resolvedType ?? $"{elementType.ResolvedType}[]"
		};
		grouped.Items.Add(new GroupedExpressionItem
		{
			Name = "elements",
			ResolvedType = $"{elementType.ResolvedType}*",
			Expression = CreateAllocCall(elementType, CurrentAllocator(), syntax, length)
		});
		grouped.Items.Add(new GroupedExpressionItem
		{
			Name = "length",
			ResolvedType = "nuint",
			Expression = length
		});
		return grouped;
	}

	static Expression CreateArrayElementsAccess(Expression target)
	{
		return new MemberExpression
		{
			Target = target,
			Name = "elements",
			ResolvedType = TryGetArrayElementType(target.ResolvedType ?? "") is string elementType ? $"{elementType}*" : ErrorType
		};
	}

	static MethodReferenceExpression CreateMethodReference(FunctionDefinition function, string type)
	{
		MethodReferenceExpression reference = new()
		{
			ResolvedType = type
		};
		reference.Candidates.Add(function);
		return reference;
	}

	DeclarationStatement CreateGeneratedLocal(string name, string typeName, TypeReference type, Expression? initialValue)
	{
		DeclarationStatement declaration = new()
		{
			InitialValue = initialValue,
			ResolvedType = "void"
		};
		declaration.Target.Type = type;
		declaration.Target.ResolvedType = typeName;
		declaration.Target.Names.Add(name);
		return declaration;
	}

	static VariableReferenceExpression CreateVariableReference(BindableNode variable, string type)
	{
		return new VariableReferenceExpression
		{
			Variable = variable,
			ResolvedType = type
		};
	}

	string NewGeneratedLocalName(string prefix)
	{
		string name = $"#{prefix}{generatedLocalIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
		generatedLocalIndex++;
		return name;
	}

	string NewGeneratedLabelName(string prefix)
	{
		string name = $"__{prefix}{generatedLocalIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
		generatedLocalIndex++;
		return name;
	}

	FunctionDefinition? FindInitNewMethod(TypeDefinition type, int argumentCount)
	{
		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (function.Name == InitNewMethodName && CallableByArgumentCount(function.Parameters, argumentCount))
				return function;
		}

		return null;
	}

	FunctionDefinition? FindDeleteMethod(string typeName)
	{
		if (!typeDefinitions.TryGetValue(typeName, out TypeDefinition? type))
			return null;

		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (function.Name == DeleteMethodName)
				return function;
		}

		return null;
	}

	static IEnumerable<FunctionDefinition> GetFunctions(TypeDefinition type)
	{
		return type switch
		{
			ClassDefinition classDefinition => classDefinition.Functions,
			StructDefinition structDefinition => structDefinition.Functions,
			InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
			EnumDefinition enumDefinition => enumDefinition.Functions,
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions,
			ParamsDefinition paramsDefinition => paramsDefinition.Functions,
			_ => []
		};
	}

	static bool CallableByArgumentCount(List<ParameterDefinition> parameters, int count)
	{
		int required = 0;
		int callable = 0;
		foreach (ParameterDefinition parameter in parameters)
		{
			if (IsHiddenParameter(parameter))
				continue;

			callable++;
			if (parameter.DefaultValue is null)
				required++;
		}

		return required <= count && count <= callable;
	}

	static bool IsHiddenParameter(ParameterDefinition parameter)
	{
		return parameter.Modifier is ParameterModifier.Thrown or ParameterModifier.Within
			|| parameter is WithinParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition;
	}

	static bool HasWithinParameter(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition)
				return true;
		}

		return false;
	}

	static ParameterDefinition? GetWithinParameter(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition)
				return parameter;
		}

		return null;
	}

	static ParameterDefinition? GetAllocatorParameter(FunctionDefinition function)
	{
		if (GetWithinParameter(function) is ParameterDefinition within)
			return within;
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Name == "allocator" && parameter.ResolvedType is "Allocator*" or AllocatorType)
				return parameter;
		}
		return null;
	}

	static bool HasCreateWithAllocatorAttribute(TypeDefinition type)
	{
		foreach (AttributeConstructor attribute in type.Attributes)
		{
			if (attribute.Name.TrimStart('@') == "createWithAllocator")
				return true;
		}

		return false;
	}

	static ParameterDefinition CreateAllocatorParameter()
	{
		return new ParameterDefinition
		{
			Name = "allocator",
			Symbol = "allocator",
			Type = AllocatorPointerType(),
			ResolvedType = "Allocator*"
		};
	}

	DeclarationStatement CreateResolvedAllocatorLocal(ParameterDefinition? allocator)
	{
		Expression source = allocator is null ? StdDefaultAllocator() : CreateVariableReference(allocator, allocator.ResolvedType ?? "Allocator*");
		DeclarationStatement declaration = CreateGeneratedLocal("resolvedAllocator", "Allocator*", AllocatorPointerType(), new BinaryExpression
		{
			Left = source,
			Operator = BinaryOperator.NullCoalescing,
			Right = StdDefaultAllocator(),
			ResolvedType = "Allocator*"
		});
		declaration.Target.Names.Clear();
		declaration.Target.Names.Add("resolvedAllocator");
		return declaration;
	}

	Expression? GetFunctionAllocatorForBody(FunctionDefinition function)
	{
		if (function.Name == DeleteMethodName && GetWithinParameter(function) is ParameterDefinition deleteAllocator)
			return CreateVariableReference(deleteAllocator, deleteAllocator.ResolvedType ?? "Allocator*");
		if (function.Name == InitNewMethodName && GetWithinParameter(function) is not null)
			return CreateResolvedAllocatorReference();
		if (function.Name == InitNewMethodName || function.Name == DeleteMethodName)
			return StdDefaultAllocator();
		return null;
	}

	static NamedExpression CreateResolvedAllocatorReference()
	{
		return new NamedExpression
		{
			Name = "resolvedAllocator",
			ResolvedType = "Allocator*"
		};
	}

	static NamedExpression StdDefaultAllocator()
	{
		NamedExpression expression = new()
		{
			Name = "defaultAllocator",
			ResolvedType = "Allocator*"
		};
		expression.Qualifiers.Add("Std");
		return expression;
	}

	static void CopyParameters(List<ParameterDefinition> source, List<ParameterDefinition> target)
	{
		foreach (ParameterDefinition parameter in source)
			target.Add(CloneParameter(parameter));
	}

	static ParameterDefinition CloneParameter(ParameterDefinition parameter)
	{
		ParameterDefinition clone = parameter switch
		{
			ThisParameterDefinition => new ThisParameterDefinition(),
			WithinParameterDefinition => new WithinParameterDefinition(),
			SizeOfParameterDefinition => new SizeOfParameterDefinition(),
			VTableOfParameterDefinition vtableOf => new VTableOfParameterDefinition { InterfaceType = CloneType(vtableOf.InterfaceType) },
			_ => new ParameterDefinition()
		};
		clone.SourceSyntax = parameter.SourceSyntax;
		clone.Name = parameter.Name;
		clone.Symbol = parameter.Symbol;
		clone.Export = parameter.Export;
		clone.Extern = parameter.Extern;
		clone.Modifier = parameter.Modifier;
		clone.Type = parameter is WithinParameterDefinition && parameter.Type is null ? new AllocatorTypeReference { ResolvedType = AllocatorType } : CloneType(parameter.Type);
		clone.DefaultValue = parameter.DefaultValue;
		clone.ResolvedType = parameter is WithinParameterDefinition && parameter.ResolvedType == AllocatorType ? AllocatorType : parameter.ResolvedType;
		return clone;
	}

	static TypeReference TypeReferenceFor(TypeDefinition type)
	{
		return new TypeDefinitionReference
		{
			Name = type.Name,
			Definition = type,
			ResolvedType = type.Name
		};
	}

	static TypeReference? CloneType(TypeReference? type)
	{
		if (type is null)
			return null;

		TypeReference clone = type switch
		{
			NamedTypeReference named => CloneNamed(named),
			TypeDefinitionReference definition => CloneDefinitionReference(definition),
			GenericParameterTypeReference generic => new GenericParameterTypeReference { Name = generic.Name, Parameter = generic.Parameter },
			AllocatorTypeReference => new AllocatorTypeReference(),
			AttributedTypeReference attributed => new AttributedTypeReference { Attribute = attributed.Attribute, Type = CloneType(attributed.Type) },
			GenericTypeReference generic => CloneGeneric(generic),
			ArrayTypeReference array => new ArrayTypeReference { ElementType = CloneType(array.ElementType) },
			OptionalTypeReference optional => new OptionalTypeReference { ElementType = CloneType(optional.ElementType) },
			PointerTypeReference pointer => new PointerTypeReference { ElementType = CloneType(pointer.ElementType) },
			ConstTypeReference constant => new ConstTypeReference { Type = CloneType(constant.Type) },
			VolatileTypeReference vol => new VolatileTypeReference { Type = CloneType(vol.Type) },
			AnyTypeReference => new AnyTypeReference(),
			AutoTypeReference => new AutoTypeReference(),
			PrimitiveTypeReference primitive => new PrimitiveTypeReference { Type = primitive.Type },
			EscapedTypeReference escaped => new EscapedTypeReference { Type = CloneType(escaped.Type) },
			ScopedTypeReference scoped => CloneScoped(scoped),
			UnscopedTypeReference unscoped => CloneUnscoped(unscoped),
			CallableTypeReference callable => CloneCallable(callable),
			IterTypeReference iter => new IterTypeReference { ElementType = CloneType(iter.ElementType) },
			GroupedParamsTypeReference grouped => new GroupedParamsTypeReference { StructType = CloneType(grouped.StructType) },
			MaterializedStructTypeReference materialized => new MaterializedStructTypeReference { ParamsType = CloneType(materialized.ParamsType) },
			ThrownTypeReference thrown => new ThrownTypeReference { Type = CloneType(thrown.Type) },
			_ => new NamedTypeReference { Name = type.ResolvedType ?? ErrorType }
		};
		clone.SourceSyntax = type.SourceSyntax;
		clone.ResolvedType = type.ResolvedType;
		return clone;
	}

	static NamedTypeReference CloneNamed(NamedTypeReference named)
	{
		NamedTypeReference clone = new() { Name = named.Name };
		clone.Qualifiers.AddRange(named.Qualifiers);
		foreach (TypeReference argument in named.TypeArguments)
			clone.TypeArguments.Add(CloneType(argument)!);
		return clone;
	}

	static TypeDefinitionReference CloneDefinitionReference(TypeDefinitionReference definition)
	{
		TypeDefinitionReference clone = new() { Name = definition.Name, Definition = definition.Definition };
		foreach (TypeReference argument in definition.TypeArguments)
			clone.TypeArguments.Add(CloneType(argument)!);
		return clone;
	}

	static GenericTypeReference CloneGeneric(GenericTypeReference generic)
	{
		GenericTypeReference clone = new() { Type = CloneType(generic.Type) };
		foreach (TypeReference argument in generic.TypeArguments)
			clone.TypeArguments.Add(CloneType(argument)!);
		return clone;
	}

	static ScopedTypeReference CloneScoped(ScopedTypeReference scoped)
	{
		ScopedTypeReference clone = new() { Type = CloneType(scoped.Type) };
		clone.Anchors.AddRange(scoped.Anchors);
		return clone;
	}

	static UnscopedTypeReference CloneUnscoped(UnscopedTypeReference unscoped)
	{
		UnscopedTypeReference clone = new() { Type = CloneType(unscoped.Type) };
		clone.Anchors.AddRange(unscoped.Anchors);
		return clone;
	}

	static CallableTypeReference CloneCallable(CallableTypeReference callable)
	{
		CallableTypeReference clone = new() { Kind = callable.Kind, ReturnType = CloneType(callable.ReturnType) };
		foreach (ParameterDefinition parameter in callable.Parameters)
			clone.Parameters.Add(CloneParameter(parameter));
		return clone;
	}

	static TypeReference PointerTo(TypeReference type)
	{
		return new PointerTypeReference
		{
			ElementType = type,
			ResolvedType = $"{type.ResolvedType}*"
		};
	}

	static PrimitiveTypeReference VoidType()
	{
		return new PrimitiveTypeReference
		{
			Type = PrimitiveType.Void,
			ResolvedType = "void"
		};
	}

	static LiteralExpression NumberLiteral(string text, string resolvedType)
	{
		return new LiteralExpression
		{
			Kind = LiteralKind.Number,
			Text = text,
			Value = text,
			ResolvedType = resolvedType
		};
	}

	Expression CurrentAllocator()
	{
		return currentAllocatorOverride ?? new CurrentAllocatorExpression { ResolvedType = AllocatorType };
	}

	static TypeReference AllocatorPointerType()
	{
		return new PointerTypeReference
		{
			ElementType = new NamedTypeReference { Name = "Allocator", ResolvedType = "Allocator" },
			ResolvedType = "Allocator*"
		};
	}

	static string BaseConstructedType(string? type)
	{
		if (string.IsNullOrWhiteSpace(type))
			return "";

		return type.EndsWith("*", StringComparison.Ordinal) ? type[..^1] : type;
	}

	void Report(SyntaxNode? syntax, string message)
	{
		diagnostics.Add(new AnalysisDiagnostic(GetRange(syntax), message));
	}

	static FunctionDefinition CreateAllocatorAllocMethod()
	{
		FunctionDefinition method = new()
		{
			Name = "alloc",
			Symbol = "alloc",
			ResolvedType = "T*"
		};
		method.Parameters.Add(new ThisParameterDefinition { Name = "this", Symbol = "this", ResolvedType = "Allocator*" });
		method.Parameters.Add(new ParameterDefinition { Name = "len", Symbol = "len", ResolvedType = "nuint" });
		method.Parameters.Add(new SizeOfParameterDefinition { Name = "sizeof", Symbol = "sizeof", ResolvedType = "nuint" });
		method.Parameters.Add(new ParameterDefinition { Name = "MemoryError", Symbol = "MemoryError", Modifier = ParameterModifier.Thrown, ResolvedType = "MemoryError" });
		return method;
	}

	static FunctionDefinition CreateAllocatorFreeMethod()
	{
		FunctionDefinition method = new()
		{
			Name = "free",
			Symbol = "free",
			ResolvedType = "void"
		};
		method.Parameters.Add(new ThisParameterDefinition { Name = "this", Symbol = "this", ResolvedType = "Allocator*" });
		method.Parameters.Add(new ParameterDefinition { Name = "ptr", Symbol = "ptr", ResolvedType = "escaped void*" });
		return method;
	}

	sealed record InterfaceImplementationLowering(TypeDefinition Type, InterfaceDefinition Interface, FieldDefinition? Field, VariableDefinition VTable, bool DirectEntries, bool IsStruct);

	sealed record InterfaceThunkLowering(InterfaceImplementationLowering Implementation, InterfaceDefinition EntryInterface, FunctionDefinition Member);

	sealed record VirtualSlot(FunctionDefinition Declaration, FunctionDefinition? Implementation);

	sealed record ThrowHandler(string ErrorType, DeclarationTarget ErrorTarget, string LabelName);

	sealed record CleanupScope(List<Statement> Statements, bool RunBeforeCatch)
	{
		public string? ExitLabelName { get; set; }
		public DeclarationTarget? ReturnTarget { get; set; }
		public string ReturnType { get; set; } = "void";
	}

	sealed record VirtualClassLowering(ClassDefinition Class, ClassDefinition? BaseClass, VirtualClassLowering? BaseLowering, StructDefinition VTableType)
	{
		public FieldDefinition? Field { get; set; }
		public VariableDefinition? VTable { get; set; }
		public List<VirtualSlot> DeclaredSlots { get; } = [];
	}
}

static class BindableNodeAnalyzerRewriteParameterExtensions
{
	public static List<ArgumentExpression> ArgumentsFromParameters(this FunctionDefinition function, bool skipAllocator = false)
	{
		List<ArgumentExpression> arguments = [];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown or ParameterModifier.Within
				|| parameter is WithinParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition)
				continue;
			if (skipAllocator && parameter.Name == "allocator")
				continue;

			arguments.Add(new ArgumentExpression
			{
				Value = new VariableReferenceExpression
				{
					Variable = parameter,
					ResolvedType = parameter.ResolvedType
				},
				ResolvedType = parameter.ResolvedType
			});
		}

		return arguments;
	}
}
