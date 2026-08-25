using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void AnalyzeModule(Module module)
	{
		RunAnalyzerPass(AnalyzerPass.DeclarationAnalysis, module);
		if (!diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
		{
			RunAnalyzerPass(AnalyzerPass.MethodBodyAnalysis, module);
			if (!diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
				ValidateProductionDeclarationDependencies(module);
		}
		RunAnalyzerPass(AnalyzerPass.NodeRewriteApplication, module);
	}

	void ValidateProductionDeclarationDependencies(Module module)
	{
		DeclarationParticipation participation = new(module);
		diagnostics.AddRange(participation.ValidateProductionDependencies());
	}

	void AnalyzeDeclarations(Module module)
	{
		currentModule = module;
		module.ResolvedType = ModuleType;
		BindRequirementAttributes(module);
		ApplyEffectiveRequirements(module);
		CollectTypeNames(module);
		CollectStaticClassNames(module);
		CollectAliasNames(module);
		ResolveAliases();
		AnalyzeNewtypeSignatures(module);
		AddRetainedAllocatorFields(module);

		foreach (UsingDeclaration usingDeclaration in module.Usings)
		{
			usingDeclaration.ResolvedType = UsingType;
			CheckName(usingDeclaration.Alias, GetAliasRange(usingDeclaration.SourceSyntax), "using alias");
		}

		foreach (Definition definition in ActiveDefinitions(module))
			AnalyzeDefinition(definition, new AnalysisScope());

		ValidateTestAttributePlacements(module);
		ValidateDocAttributePlacements(module);
		ValidateRequirementAttributePlacements(module);
		ValidateTopLevelOverloadFamilies(module);
		AnalyzeInlineConstantsAndEnumValues(module);
		AnalyzeGlobalInitializers(module);
		AnalyzeInheritance();
		ValidateShadowClasses();
		AnalyzeExportProjections(module);
		ValidateDuplicateTopLevelSymbols(module);
		AnalyzeInterfaceSlotInitializers(module);
		AnalyzeImplementations();
		AnalyzeExportVisibility(module);
	}

	void AddRetainedAllocatorFields(Module module)
	{
		foreach (Definition definition in ActiveDefinitions(module))
		{
			switch (definition)
			{
				case ClassDefinition classDefinition:
					AddRetainedAllocatorFields(classDefinition);
					break;
				case StructDefinition structDefinition:
					ValidateNonClassRetainedAllocatorParameters(structDefinition.Functions, "Struct constructors may not retain allocator parameters.");
					break;
				case InterfaceDefinition interfaceDefinition:
					ValidateNonClassRetainedAllocatorParameters(interfaceDefinition.Functions, "Interface constructors may not retain allocator parameters.");
					break;
				case StaticClassDefinition staticClassDefinition:
					ValidateNonClassRetainedAllocatorParameters(staticClassDefinition.Functions, "Static class declarations may not retain allocator parameters.");
					break;
			}
		}
	}

	void AddRetainedAllocatorFields(ClassDefinition classDefinition)
	{
		ParameterDefinition? retained = null;
		foreach (FunctionDefinition function in classDefinition.Functions)
		{
			foreach (ParameterDefinition parameter in GetRetainedAllocatorParameters(function))
			{
				if (function.Modifier != FunctionModifier.Constructor)
				{
					Report(GetRange(parameter.SourceSyntax), "Only class constructors may retain allocator parameters.");
					continue;
				}
				if (classDefinition.Extern is not null)
				{
					Report(GetRange(parameter.SourceSyntax), "Extern class constructors may not retain allocator parameters.");
					continue;
				}
				if (classDefinition.IsShadow)
				{
					Report(GetRange(parameter.SourceSyntax), "Shadow class constructors may not retain allocator parameters.");
					continue;
				}
				if (parameter.SourceSyntax is WithinParameterSyntax { LifetimeKeyword: not null })
					Report(GetRange(parameter.SourceSyntax), "Retained allocator parameters may not declare an explicit lifetime.");
				if (HasDirectClassBase(classDefinition))
					Report(GetRange(parameter.SourceSyntax), "Derived class constructors may not retain allocator parameters.");
				if (retained is not null)
				{
					Report(GetRange(parameter.SourceSyntax), "A class may declare only one retained allocator parameter.");
					continue;
				}
				retained = parameter;

				string fieldName = parameter.RetainedAllocatorFieldName ?? parameter.Name;
				if (parameter.RetainedAllocatorField is FieldDefinition existingRetainedField)
				{
					if (!classDefinition.Fields.Contains(existingRetainedField))
						classDefinition.Fields.Add(existingRetainedField);
					continue;
				}
				FieldDefinition? existingField = classDefinition.Fields.FirstOrDefault(field => field.Name == fieldName);
				if (existingField is not null)
				{
					if (existingField.GeneratedInfo?.Source == parameter)
					{
						parameter.RetainedAllocatorField = existingField;
						continue;
					}
					Report(GetRange(parameter.SourceSyntax), $"Retained allocator field '{fieldName}' conflicts with an existing field.");
					continue;
				}

				FieldDefinition field = new()
				{
					SourceSyntax = parameter.SourceSyntax,
					Name = fieldName,
					Symbol = fieldName,
					Type = new AllocatorTypeReference { ResolvedType = AllocatorType },
					ResolvedType = AllocatorType,
					GeneratedInfo = new GeneratedDeclarationInfo(GeneratedDeclarationCategory.Lifecycle, "retained allocator field", parameter)
				};
				parameter.RetainedAllocatorField = field;
				classDefinition.Fields.Add(field);
			}
		}
	}

	static IEnumerable<ParameterDefinition> GetRetainedAllocatorParameters(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
			if (parameter.RetainsAllocator)
				yield return parameter;
	}

	bool HasDirectClassBase(ClassDefinition definition)
	{
		foreach (TypeReference baseType in definition.BaseTypes)
			if (TryGetNamedTypeDefinition(baseType, out TypeDefinition? baseDefinition) && baseDefinition is ClassDefinition)
				return true;
		return false;
	}

	void ValidateNonClassRetainedAllocatorParameters(IEnumerable<FunctionDefinition> functions, string message)
	{
		foreach (FunctionDefinition function in functions)
			foreach (ParameterDefinition parameter in GetRetainedAllocatorParameters(function))
				Report(GetRange(parameter.SourceSyntax), message);
	}

	void ValidateClassRetainedAllocatorParameters(ClassDefinition definition)
	{
		bool retainsAllocator = LifecycleAllocatorPolicy.RetainsAllocator(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
		{
			if (retainsAllocator && IsDestructorFunction(function) && HasWithinParameter(function))
				Report(GetRange(GetWithinParameter(function)?.SourceSyntax ?? function.SourceSyntax), "Retained allocator classes may not declare destructor within parameters.");

			foreach (ParameterDefinition parameter in GetRetainedAllocatorParameters(function))
			{
				if (parameter.Modifier != ParameterModifier.Within && parameter is not WithinParameterDefinition)
					Report(GetRange(parameter.SourceSyntax), "Retained allocator parameters must be within parameters.");
				if (parameter.SourceSyntax is WithinParameterSyntax { LifetimeKeyword: not null })
					Report(GetRange(parameter.SourceSyntax), "Retained allocator parameters may not declare an explicit lifetime.");
				if (GetDirectBaseClass(definition) is not null)
					Report(GetRange(parameter.SourceSyntax), "Derived class constructors may not retain allocator parameters.");
			}
		}
	}

	void ValidateClassAllocatorLifecycleShape(ClassDefinition definition)
	{
		if (definition.Extern is not null)
			return;

		bool retainsAllocator = LifecycleAllocatorPolicy.RetainsAllocator(definition.Functions);
		List<FunctionDefinition> sourceConstructors = [.. definition.Functions.Where(static function => function.Modifier == FunctionModifier.Constructor && function.GeneratedInfo is null)];
		List<FunctionDefinition> sourceDestructors = [.. definition.Functions.Where(function => IsDestructorFunction(function) && function.GeneratedInfo is null)];

		if (!retainsAllocator)
		{
			bool anyConstructorWithin = sourceConstructors.Any(HasWithinParameter);
			bool anyDestructorWithin = sourceDestructors.Any(HasWithinParameter);
			foreach (FunctionDefinition destructor in sourceDestructors)
			{
				if (anyConstructorWithin && !HasWithinParameter(destructor))
					Report(GetRange(destructor.SourceSyntax), "Destructor must declare a within allocator because an explicit constructor declares within.");
			}
			if (anyDestructorWithin)
			{
				foreach (FunctionDefinition constructor in sourceConstructors)
					if (!HasWithinParameter(constructor))
						Report(GetRange(constructor.SourceSyntax), "Constructor must declare a within allocator because the explicit destructor declares within.");
			}
		}

		ClassDefinition? baseClass = GetDirectBaseClass(definition);
		if (baseClass is null || !BaseConstructorsRequireWithin(baseClass))
			return;

		foreach (FunctionDefinition constructor in sourceConstructors)
		{
			if (!HasWithinParameter(constructor))
				Report(GetRange(constructor.SourceSyntax), $"Constructor for derived class '{definition.Name}' must declare within because base class '{baseClass.Name}' has a within constructor.");
		}
	}

	static bool BaseConstructorsRequireWithin(ClassDefinition baseClass)
	{
		foreach (FunctionDefinition function in baseClass.Functions)
			if (function.Modifier == FunctionModifier.Constructor && HasWithinParameter(function))
				return true;
		return false;
	}

	void CollectTypeNames(Module module)
	{
		foreach (Definition definition in ActiveDefinitions(module))
		{
			if (definition is not TypeDefinition typeDefinition)
				continue;

			CheckName(typeDefinition.Name, GetNameRange(typeDefinition), "type");

			if (string.IsNullOrWhiteSpace(typeDefinition.Name))
				continue;

			string qualifiedKey = DefinitionLookupKey(typeDefinition);
			if (qualifiedTypeDefinitions.TryGetValue(qualifiedKey, out TypeDefinition? existing))
			{
				if (!ReferenceEquals(existing, typeDefinition) && DuplicateDefinitionsParticipate(existing, typeDefinition))
					Report(GetNameRange(typeDefinition), $"Duplicate type name '{typeDefinition.Name}'.");
			}
			else if (!qualifiedTypeDefinitions.TryAdd(qualifiedKey, typeDefinition))
				Report(GetNameRange(typeDefinition), $"Duplicate type name '{typeDefinition.Name}'.");
			else
			{
				allTypeDefinitions.Add(typeDefinition);
				typeInfos[typeDefinition] = new TypeAnalysisInfo(typeDefinition);
				if (!typeDefinitions.ContainsKey(typeDefinition.Name))
					typeDefinitions[typeDefinition.Name] = typeDefinition;
				else
					typeDefinitions.Remove(typeDefinition.Name);
				typeDefinitions[SymbolNameService.DefaultTypeSymbol(GetDefinitionNamespace(typeDefinition), typeDefinition.Name)] = typeDefinition;
			}
		}
	}

	bool DuplicateDefinitionsParticipate(Definition left, Definition right)
	{
		if (currentModule is null)
			return true;
		DeclarationParticipation participation = new(currentModule);
		return participation.Includes(left, currentModule.DeclarationParticipationMode)
			&& participation.Includes(right, currentModule.DeclarationParticipationMode);
	}

	void CollectStaticClassNames(Module module)
	{
		foreach (Definition definition in ActiveDefinitions(module))
		{
			if (definition is not StaticClassDefinition staticClassDefinition)
				continue;

			CheckName(staticClassDefinition.Name, GetNameRange(staticClassDefinition), "static class");

			if (string.IsNullOrWhiteSpace(staticClassDefinition.Name))
				continue;

			string qualifiedKey = DefinitionLookupKey(staticClassDefinition);
			if (qualifiedTypeDefinitions.ContainsKey(qualifiedKey))
			{
				Report(GetNameRange(staticClassDefinition), $"Static class name '{staticClassDefinition.Name}' conflicts with a type name.");
				continue;
			}

			if (qualifiedStaticClassDefinitions.TryGetValue(qualifiedKey, out StaticClassDefinition? existing))
			{
				if (!ReferenceEquals(existing, staticClassDefinition))
					Report(GetNameRange(staticClassDefinition), $"Duplicate static class name '{staticClassDefinition.Name}'.");
			}
			else if (!qualifiedStaticClassDefinitions.TryAdd(qualifiedKey, staticClassDefinition))
				Report(GetNameRange(staticClassDefinition), $"Duplicate static class name '{staticClassDefinition.Name}'.");
			else
			{
				allStaticClassDefinitions.Add(staticClassDefinition);
				if (!staticClassDefinitions.ContainsKey(staticClassDefinition.Name))
					staticClassDefinitions[staticClassDefinition.Name] = staticClassDefinition;
				else
					staticClassDefinitions.Remove(staticClassDefinition.Name);
			}
		}
	}

	void AnalyzeNewtypeSignatures(Module module)
	{
		AnalysisScope scope = new();
		foreach (Definition definition in ActiveDefinitions(module))
			if (definition is NewtypeDefinition newtypeDefinition)
				AnalyzeNewtypeSignature(newtypeDefinition, scope);
	}

	void AnalyzeDefinition(Definition definition, AnalysisScope parentScope)
	{
		Definition? previousAnalysisDefinition = currentAnalysisDefinition;
		currentAnalysisDefinition = definition;
		try
		{
			AnalyzeAttributes(definition.Attributes);
			if (definition is not FunctionDefinition)
				ValidateUnsupportedAttributePlacement(definition);

			switch (definition)
			{
				case ClassDefinition classDefinition:
					AnalyzeClassDefinition(classDefinition, parentScope);
					break;

				case StaticClassDefinition staticClassDefinition:
					AnalyzeStaticClassDefinition(staticClassDefinition, parentScope);
					break;

				case StructDefinition structDefinition:
					AnalyzeStructDefinition(structDefinition, parentScope);
					break;

				case InterfaceDefinition interfaceDefinition:
					AnalyzeInterfaceDefinition(interfaceDefinition, parentScope);
					break;

				case EnumDefinition enumDefinition:
					AnalyzeEnumDefinition(enumDefinition, parentScope);
					break;

				case NewtypeDefinition newtypeDefinition:
					AnalyzeNewtypeDefinition(newtypeDefinition, parentScope);
					break;

				case ParamsDefinition paramsDefinition:
					AnalyzeParamsDefinition(paramsDefinition, parentScope);
					break;

				case AliasDefinition aliasDefinition:
					AnalyzeAliasDefinition(aliasDefinition);
					break;

				case VariableDefinition variableDefinition:
					AnalyzeVariableDefinition(variableDefinition, parentScope);
					break;

				case FunctionDefinition functionDefinition:
					AnalyzeFunctionDefinition(functionDefinition, parentScope, containingType: null);
					break;
			}
		}
		finally
		{
			currentAnalysisDefinition = previousAnalysisDefinition;
		}
	}

	void CollectAliasNames(Module module)
	{
		foreach (Definition definition in ActiveDefinitions(module))
		{
			if (definition is not AliasDefinition alias)
				continue;

			CheckName(alias.Name, GetNameRange(alias), "alias");
			if (string.IsNullOrWhiteSpace(alias.Name))
				continue;

			if (aliasDefinitions.TryGetValue(alias.Name, out AliasDefinition? existing))
			{
				if (!ReferenceEquals(existing, alias))
					Report(GetNameRange(alias), $"Duplicate alias name '{alias.Name}'.");
			}
			else if (!aliasDefinitions.TryAdd(alias.Name, alias))
				Report(GetNameRange(alias), $"Duplicate alias name '{alias.Name}'.");
		}
	}

	void ResolveAliases()
	{
		Dictionary<AliasDefinition, AliasDefinition> resolving = [];
		foreach (AliasDefinition alias in aliasDefinitions.Values)
			ResolveAlias(alias, resolving);
	}

	bool ResolveAlias(AliasDefinition alias, Dictionary<AliasDefinition, AliasDefinition> resolving)
	{
		if (alias.TargetKind != AliasTargetKind.Unresolved)
			return true;
		if (resolving.ContainsKey(alias))
		{
			Report(GetNameRange(alias), $"Alias '{alias.Name}' cannot reference itself through an alias cycle.");
			alias.TargetKind = AliasTargetKind.Type;
			alias.ResolvedTargetName = ErrorType;
			return false;
		}

		resolving.Add(alias, alias);
		bool success = ResolveAliasTarget(alias, resolving);
		resolving.Remove(alias);
		return success;
	}

	bool ResolveAliasTarget(AliasDefinition alias, Dictionary<AliasDefinition, AliasDefinition> resolving)
	{
		string target = BuildAliasTargetName(alias);
		if (target == alias.Name && alias.TargetQualifiers.Count == 0)
		{
			Report(GetNameRange(alias), $"Alias '{alias.Name}' cannot reference itself.");
			alias.TargetKind = AliasTargetKind.Type;
			alias.ResolvedTargetName = ErrorType;
			return false;
		}

		if (alias.TargetQualifiers.Count == 0 && aliasDefinitions.TryGetValue(alias.TargetName, out AliasDefinition? targetAlias))
		{
			if (!IsDefinitionVisible(targetAlias, alias.SourceSyntax))
				ReportNotExported(targetAlias, alias.SourceSyntax, "Alias");
			ResolveAlias(targetAlias, resolving);
			alias.TargetKind = targetAlias.TargetKind;
			alias.ResolvedTargetName = targetAlias.ResolvedTargetName;
			return true;
		}

		if (alias.TargetQualifiers.Count == 0 && TryGetPrimitiveType(alias.TargetName, out _))
		{
			alias.TargetKind = AliasTargetKind.Type;
			alias.ResolvedTargetName = alias.TargetName;
			return true;
		}

		if (alias.TargetQualifiers.Count == 0 && typeDefinitions.TryGetValue(alias.TargetName, out TypeDefinition? type))
		{
			if (!IsDefinitionVisible(type, alias.SourceSyntax))
				ReportNotExported(type, alias.SourceSyntax, "Type");
			alias.TargetKind = AliasTargetKind.Type;
			alias.ResolvedTargetName = type.Name;
			return true;
		}

		if (alias.TargetQualifiers.Count == 0 && selectedTarget?.Capabilities.HasCallSpec(alias.TargetName) == true)
		{
			alias.TargetKind = AliasTargetKind.CallSpec;
			alias.ResolvedTargetName = alias.TargetName;
			return true;
		}

		if (alias.TargetQualifiers.Count == 0 && selectedTarget?.Capabilities.HasTypeSpec(alias.TargetName) == true)
		{
			alias.TargetKind = AliasTargetKind.TypeSpec;
			alias.ResolvedTargetName = alias.TargetName;
			return true;
		}

		if (TryResolveCallableAliasTarget(alias, target, out string resolvedCallable))
		{
			alias.TargetKind = AliasTargetKind.Callable;
			alias.ResolvedTargetName = resolvedCallable;
			return true;
		}

		Report(GetNameRange(alias), $"Alias target '{target}' could not be found.");
		alias.TargetKind = AliasTargetKind.Type;
		alias.ResolvedTargetName = ErrorType;
		return false;
	}

	bool TryResolveCallableAliasTarget(AliasDefinition alias, string target, out string resolvedName)
	{
		resolvedName = "";
		List<FunctionDefinition> matches = [];
		foreach (Definition definition in ActiveCurrentDefinitions())
		{
			if (definition is FunctionDefinition function && IsCallableTopLevelFunctionAliasTarget(function, alias, target))
				matches.Add(function);
		}

		if (matches.Count == 0)
			return false;
		if (matches.Count > 1)
		{
			Report(GetNameRange(alias), $"Multiple candidates found for alias target '{target}'.");
			resolvedName = ErrorType;
			return true;
		}

		resolvedName = GetCallableName(matches[0]);
		return true;
	}

	bool IsCallableTopLevelFunctionAliasTarget(FunctionDefinition function, AliasDefinition alias, string target)
	{
		if (GetExplicitThisParameter(function) is not null)
			return false;
		if (alias.TargetQualifiers.Count > 0)
			return IsFunctionNamed(function, alias.TargetName)
				&& IsImportedQualifiedName(function, alias.TargetQualifiers, alias.SourceSyntax);
		return IsFunctionNamed(function, target) && IsDefinitionVisible(function, alias.SourceSyntax);
	}

	static string BuildAliasTargetName(AliasDefinition alias)
	{
		return alias.TargetQualifiers.Count == 0
			? alias.TargetName
			: string.Join("::", alias.TargetQualifiers) + "::" + alias.TargetName;
	}

	void AnalyzeAliasDefinition(AliasDefinition definition)
	{
		AnalyzeAttributes(definition.Attributes);
		ValidateUnsupportedAttributePlacement(definition);
		SetDefaultTopLevelSymbol(definition, definition.Name);
		ApplySymbolAttribute(definition, allowed: false, "alias");
		definition.ResolvedType = definition.TargetKind.ToString();
	}

	void AnalyzeClassDefinition(ClassDefinition definition, AnalysisScope parentScope)
	{
		SetDefaultTypeSymbol(definition);
		ApplySymbolAttribute(definition, allowed: true, "class");
		if (definition.IsShadow)
			EnsureShadowDataType(definition);
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeTypeList(definition.BaseTypes, scope);
		RegisterBaseTypes(definition, definition.BaseTypes);

		foreach (FieldDefinition field in definition.Fields)
			AnalyzeFieldDefinition(field, FieldUsesStaticMemberScope(field) ? CreateStaticMemberScope(definition, parentScope) : scope, definition);

		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, FunctionUsesStaticMemberScope(function) ? CreateStaticMemberScope(definition, parentScope) : scope, definition.Name);
		ValidateClassRetainedAllocatorParameters(definition);
		ValidateClassAllocatorLifecycleShape(definition);
		ValidateDuplicateMethodNames(definition.Functions);
		if (definition.Extern is null)
		{
			GenerateSizeOfFields(definition);
			GenerateNameOfFields(definition);
			GenerateVTableOfFields(definition);
			ValidateExpandedFieldNames(definition.Fields);
			ValidateFlexibleArrayMembers(definition.Fields, allowFlexibleArrayMember: false);
		}
	}

	void AnalyzeStaticClassDefinition(StaticClassDefinition definition, AnalysisScope parentScope)
	{
		SetDefaultTypeSymbol(definition);
		ApplySymbolAttribute(definition, allowed: true, "static class");
		definition.ResolvedType = definition.Name;
		AnalysisScope scope = new(parentScope) { IsStaticMemberScope = true };

		foreach (FieldDefinition field in definition.Fields)
		{
			AnalyzeFieldDefinition(field, scope, containingType: null);
			if (field.Modifier == FieldModifier.Static && !field.SymbolOverridden)
				SetDefaultMemberSymbol(field, EffectiveStaticClassSymbol(definition), field.Name);
		}

		foreach (FunctionDefinition function in definition.Functions)
		{
			AnalyzeFunctionDefinition(function, scope, containingType: null, suppressStaticThisDiagnostic: true);
			if (function.Modifier == FunctionModifier.Static && !function.SymbolOverridden)
				SetDefaultMemberSymbol(function, EffectiveStaticClassSymbol(definition), GetCallableName(function).TrimStart('~'));
		}
		ValidateStaticClassMembers(definition);
		ValidateDuplicateMethodNames(definition.Functions);
		ValidateExpandedFieldNames(definition.Fields);
		ValidateFlexibleArrayMembers(definition.Fields, allowFlexibleArrayMember: false);
	}

	void ValidateStaticClassMembers(StaticClassDefinition definition)
	{
		foreach (FieldDefinition field in definition.Fields)
		{
			if (field.Modifier != FieldModifier.Static)
				Report(GetNameRange(field), $"Static class field '{definition.Name}.{field.Name}' must be declared static.");
		}

		foreach (FunctionDefinition function in definition.Functions)
		{
			if (function.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor || IsDestructorFunction(function))
			{
				Report(GetNameRange(function), $"Static class '{definition.Name}' may not declare constructors or destructors.");
				continue;
			}

			if (function.Modifier != FunctionModifier.Static)
				Report(GetNameRange(function), $"Static class method '{definition.Name}.{function.Name}' must be declared static.");

			if (GetExplicitThisParameter(function) is not null)
				Report(GetNameRange(function), $"Static class method '{definition.Name}.{function.Name}' may not declare a 'this' parameter.");

			if (function.Modifier is FunctionModifier.Virtual or FunctionModifier.Abstract or FunctionModifier.Override or FunctionModifier.Sealed)
				Report(GetNameRange(function), "Static class methods may not be virtual, abstract, override, or sealed.");
		}
	}

	void AnalyzeStructDefinition(StructDefinition definition, AnalysisScope parentScope)
	{
		SetDefaultTypeSymbol(definition);
		ApplySymbolAttribute(definition, allowed: true, "struct");
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeTypeList(definition.BaseTypes, scope);
		RegisterBaseTypes(definition, definition.BaseTypes);

		foreach (FieldDefinition field in definition.Fields)
			AnalyzeFieldDefinition(field, FieldUsesStaticMemberScope(field) ? CreateStaticMemberScope(definition, parentScope) : scope, definition);

		ValidateExpandedFieldNames(definition.Fields);
		ValidateFlexibleArrayMembers(definition.Fields, allowFlexibleArrayMember: true);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, FunctionUsesStaticMemberScope(function) ? CreateStaticMemberScope(definition, parentScope) : scope, definition.Name);
		ValidateDuplicateMethodNames(definition.Functions);
	}

	void AnalyzeInterfaceDefinition(InterfaceDefinition definition, AnalysisScope parentScope)
	{
		SetDefaultTypeSymbol(definition);
		ApplySymbolAttribute(definition, allowed: true, "interface");
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeTypeList(definition.BaseTypes, scope);
		RegisterBaseTypes(definition, definition.BaseTypes);

		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, FunctionUsesStaticMemberScope(function) ? CreateStaticMemberScope(definition, parentScope) : scope, definition.Name);
		ValidateDuplicateMethodNames(definition.Functions);
	}

	void AnalyzeEnumDefinition(EnumDefinition definition, AnalysisScope parentScope)
	{
		SetDefaultTypeSymbol(definition);
		ApplySymbolAttribute(definition, allowed: true, "enum");
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeOptionalType(definition.UnderlyingType, scope);
		ValidateNoDirectFixedArrayType(definition.UnderlyingType, definition.UnderlyingType?.SourceSyntax ?? definition.SourceSyntax, "a newtype underlying type");

		foreach (VariableDefinition value in definition.Values)
		{
			AnalyzeVariableDefinition(value, scope, allowSymbolAttribute: true);
			if (!value.SymbolOverridden)
				SetDefaultMemberSymbol(value, definition.Symbol, value.Name);
		}

		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
		ValidateDuplicateMethodNames(definition.Functions);
	}

	void AnalyzeNewtypeDefinition(NewtypeDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = AnalyzeNewtypeSignature(definition, parentScope);

		foreach (FieldDefinition field in definition.Fields)
			AnalyzeFieldDefinition(field, FieldUsesStaticMemberScope(field) ? CreateStaticMemberScope(definition, parentScope) : scope, definition);
		ValidateFlexibleArrayMembers(definition.Fields, allowFlexibleArrayMember: false);

		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, FunctionUsesStaticMemberScope(function) ? CreateStaticMemberScope(definition, parentScope) : scope, definition.Name);
		ValidateDuplicateMethodNames(definition.Functions);
	}

	void ValidateFlexibleArrayMembers(List<FieldDefinition> fields, bool allowFlexibleArrayMember)
	{
		for (int i = 0; i < fields.Count; i++)
		{
			if (AsDirectFixedArrayType(fields[i].Type) is not { Length: 0 } fixedArray)
				continue;

			bool isFinalField = i == fields.Count - 1;
			if (!allowFlexibleArrayMember || !isFinalField || fields[i].Modifier == FieldModifier.Static)
				Report(GetRange(fixedArray.LengthExpression?.SourceSyntax ?? fixedArray.SourceSyntax ?? fields[i].SourceSyntax), "Fixed-size array length 0 is valid only for the final instance field of a struct.");
		}
	}

	AnalysisScope AnalyzeNewtypeSignature(NewtypeDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		if (!analyzedNewtypeSignatures.Add(definition))
			return scope;

		SetDefaultTypeSymbol(definition);
		ApplySymbolAttribute(definition, allowed: true, "newtype");
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		RegisterParameterLifetimeAnchors(definition.Parameters, scope);
		AnalyzeOptionalType(definition.UnderlyingType, scope);
		if (ContainsThisTypeReference(definition.UnderlyingType))
			Report(GetRange(definition.UnderlyingType?.SourceSyntax ?? definition.SourceSyntax), "'this' may be used only as a plain method return type.");

		foreach (ParameterDefinition parameter in definition.Parameters)
		{
			bool asyncCallable = definition.UnderlyingType is CallableTypeReference { Kind: CallableKind.Async };
			AnalyzeParameterDefinition(parameter, scope, asyncContext: asyncCallable);
			if (ContainsThisTypeReference(parameter.Type))
				Report(GetRange(parameter.Type?.SourceSyntax ?? parameter.SourceSyntax), "'this' may be used only as a plain method return type.");
		}
		ValidateSourceCaptureDefaultValues(definition.Parameters);
		ValidateCallableNewtypeThisParameter(definition);
		ValidateCallableNewtypeUponParameters(definition);
		ValidateNewtypeConstOfAnchors(definition);

		return scope;
	}

	void ValidateCallableNewtypeUponParameters(NewtypeDefinition definition)
	{
		if (definition.UnderlyingType is not CallableTypeReference callable)
			return;

		foreach (ParameterDefinition parameter in definition.Parameters)
		{
			if (parameter.Modifier != ParameterModifier.Upon)
				continue;

			Report(GetNameRange(parameter) ?? GetRange(parameter.SourceSyntax), "The 'upon' scheduler parameter modifier was removed; use @awaitwith on an ordinary parameter or a receiver resumeAsync method.");
		}
	}

	void ValidateCallableNewtypeThisParameter(NewtypeDefinition definition)
	{
		string family = GetCallableNewtypeFamily(definition);
		for (int i = 0; i < definition.Parameters.Count; i++)
		{
			if (definition.Parameters[i] is not ThisParameterDefinition)
				continue;
			if (family == "fn")
				Report(GetRange(definition.Parameters[i].SourceSyntax), $"fn newtype '{definition.Name}' may not declare a this parameter.");
			else if (i != 0 || family == "value")
				Report(GetRange(definition.Parameters[i].SourceSyntax), $"Callable newtype '{definition.Name}' may declare this only as its first parameter.");
		}
	}

	void AnalyzeParamsDefinition(ParamsDefinition definition, AnalysisScope parentScope)
	{
		SetDefaultTypeSymbol(definition);
		ApplySymbolAttribute(definition, allowed: false, "type");
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeOptionalType(definition.UnderlyingType, scope);
		if (ContainsThisTypeReference(definition.UnderlyingType))
			Report(GetRange(definition.UnderlyingType?.SourceSyntax ?? definition.SourceSyntax), "'this' may be used only as a plain method return type.");

		foreach (ParameterDefinition component in definition.Components)
		{
			AnalyzeParameterDefinition(component, scope);
			if (ContainsThisTypeReference(component.Type))
				Report(GetRange(component.Type?.SourceSyntax ?? component.SourceSyntax), "'this' may be used only as a plain method return type.");
		}

		ValidateParamsComponentShape(definition);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
		ValidateDuplicateMethodNames(definition.Functions);
	}

	void ValidateDuplicateMethodNames(List<FunctionDefinition> functions)
	{
		Dictionary<string, List<FunctionDefinition>> invokers = new(StringComparer.Ordinal);
		Dictionary<string, FunctionDefinition> callableNames = new(StringComparer.Ordinal);
		foreach (FunctionDefinition function in functions)
		{
			string name = GetInvokerName(function);
			if (string.IsNullOrWhiteSpace(name))
				continue;

			if (!invokers.TryGetValue(name, out List<FunctionDefinition>? family))
			{
				family = [];
				invokers[name] = family;
			}
			family.Add(function);

			string callableName = GetCallableName(function);
			if (string.IsNullOrWhiteSpace(callableName))
				continue;
			if (callableNames.TryGetValue(callableName, out FunctionDefinition? existing))
			{
				FunctionDefinition reportTarget = function.SourceSyntax is null && existing.SourceSyntax is not null ? existing : function;
				Report(GetNameRange(reportTarget), $"Duplicate method name '{callableName}'.");
			}
			else
			{
				callableNames[callableName] = function;
			}
		}

		foreach ((string invoker, List<FunctionDefinition> family) in invokers)
		{
			if (family.Count <= 1)
				continue;
			ValidateOverloadFamily(invoker, family);
		}
	}

	void ValidateDuplicateTopLevelSymbols(Module module)
	{
		SymbolCollisionSet collisions = new();
		foreach (Definition definition in DeclarationParticipation.ActiveTopLevelDefinitions(module))
		{
			foreach (DeclarationName name in GetDefinitionSymbolNames(definition))
			{
				if (!collisions.TryAddSymbol(name.Value, definition, out string? componentOwner))
					Report(GetNameRange(definition), componentOwner is null
						? $"Duplicate symbol name '{name.Value}'."
						: $"Symbol '{name.Value}' is already declared in this scope as a component of '{componentOwner}'.");
			}

			foreach (string componentName in GetDefinitionComponentSymbolNames(definition))
			{
				if (componentName == definition.Name)
					continue;
				if (!collisions.TryAddComponent(componentName, definition.Name))
					Report(GetNameRange(definition), $"Symbol '{componentName}' is already declared in this scope as a component of '{definition.Name}'.");
			}
		}

		foreach (TypeDefinition type in allTypeDefinitions)
		{
			foreach (FieldDefinition field in GetTypeFields(type))
			{
				if (field.Modifier != FieldModifier.Static || string.IsNullOrWhiteSpace(field.Symbol))
					continue;

				string symbol = field.Symbol;
				if (!collisions.TryAddSymbol(symbol, field, out string? componentOwner))
					Report(GetNameRange(field), componentOwner is null
						? $"Duplicate symbol name '{symbol}'."
						: $"Symbol '{symbol}' is already declared in this scope as a component of '{componentOwner}'.");

				foreach (string componentName in GetPotentialParamsComponentNames(field.Type, field.ResolvedType, field.Symbol))
				{
					if (componentName == field.Symbol)
						continue;
					if (!collisions.TryAddComponent(componentName, field.Name))
						Report(GetNameRange(field), $"Symbol '{componentName}' is already declared in this scope as a component of '{field.Name}'.");
				}
			}

			foreach (FunctionDefinition function in GetTypeFunctions(type))
			{
				if (!function.SymbolOverridden || string.IsNullOrWhiteSpace(function.Symbol))
					continue;

				string symbol = function.Symbol;
				if (!collisions.TryAddSymbol(symbol, function, out string? componentOwner))
					Report(GetNameRange(function), componentOwner is null
						? $"Duplicate symbol name '{symbol}'."
						: $"Symbol '{symbol}' is already declared in this scope as a component of '{componentOwner}'.");
			}
		}
	}

	IEnumerable<string> GetDefinitionComponentSymbolNames(Definition definition)
	{
		return definition switch
		{
			VariableDefinition variable => GetPotentialParamsComponentNames(variable.Type, variable.ResolvedType, variable.Symbol),
			_ => []
		};
	}

	IEnumerable<DeclarationName> GetDefinitionSymbolNames(Definition definition)
	{
		return SymbolNameService.TopLevelSymbolNames(definition, GetExplicitThisParameter);
	}

	void ValidateExpandedFieldNames(List<FieldDefinition> fields)
	{
		Dictionary<string, FieldDefinition> symbols = new(StringComparer.Ordinal);
		Dictionary<string, string> componentSymbols = new(StringComparer.Ordinal);
		foreach (FieldDefinition field in fields)
		{
			string name = field.Name;
			if (string.IsNullOrWhiteSpace(name))
				continue;

			if (componentSymbols.TryGetValue(name, out string? componentOwner))
				Report(GetNameRange(field), $"Symbol '{name}' is already declared in this scope as a component of '{componentOwner}'.");

			if (!symbols.TryAdd(name, field))
				Report(GetNameRange(field), $"Duplicate field name '{name}'.");

			foreach (string componentName in GetPotentialParamsComponentNames(field.Type, field.ResolvedType, field.Name))
			{
				if (componentName == field.Name)
					continue;
				if (symbols.ContainsKey(componentName) || componentSymbols.ContainsKey(componentName))
					Report(GetNameRange(field), $"Symbol '{componentName}' is already declared in this scope as a component of '{field.Name}'.");
				else
					componentSymbols[componentName] = field.Name;
			}
		}
	}

	static void NormalizeExtensionThisParameter(FunctionDefinition definition, string? containingType)
	{
		if (containingType is not null || definition.Parameters.Count == 0 || definition.Parameters[0] is ThisParameterDefinition)
			return;

		ParameterDefinition first = definition.Parameters[0];
		if (first.Name != "this")
			return;

		ThisParameterDefinition thisParameter = new()
		{
			SourceSyntax = first.SourceSyntax,
			Name = first.Name,
			Symbol = first.Symbol,
			Type = first.Type,
			DefaultValue = first.DefaultValue,
			Modifier = first.Modifier,
			ResolvedType = first.ResolvedType
		};
		thisParameter.Attributes.AddRange(first.Attributes);
		definition.Parameters[0] = thisParameter;
	}

	static bool IsExtensionThisParameter(FunctionDefinition definition, string? containingType, int parameterIndex)
	{
		return containingType is null
			&& parameterIndex == 0
			&& definition.Parameters.Count > 0
			&& definition.Parameters[0] is ThisParameterDefinition { Name: "this" };
	}

	static void ApplyImplicitGetterThisParameter(FunctionDefinition definition, string? containingType)
	{
		if (containingType is null
			|| definition.Modifier is FunctionModifier.Static or FunctionModifier.Constructor or FunctionModifier.Destructor
			|| IsDestructorFunction(definition)
			|| GetExplicitThisParameter(definition) is not null
			|| definition.EffectiveThisParameter is not null
			|| !IsPropertyGetterFunction(definition))
		{
			return;
		}

		definition.EffectiveThisParameter = new ThisParameterDefinition
		{
			Name = "this",
			Symbol = "this"
		};
		definition.EffectiveThisParameter.Attributes.Add(new AttributeConstructor { Name = "const" });
	}

	void RegisterFunctionLifetimeAnchors(FunctionDefinition definition, AnalysisScope scope, string? containingType)
	{
		if (containingType is not null || GetExplicitThisParameter(definition) is not null)
			scope.AddLifetimeAnchor("this", definition);
		RegisterParameterLifetimeAnchors(definition.Parameters, scope);
	}

	static void RegisterParameterLifetimeAnchors(List<ParameterDefinition> parameters, AnalysisScope scope)
	{
		foreach (ParameterDefinition parameter in parameters)
		{
			if (!string.IsNullOrWhiteSpace(parameter.Name))
				scope.AddLifetimeAnchor(parameter.Name, parameter);
		}
	}

	void BindFunctionReceiverLifetime(FunctionDefinition definition, string? containingType)
	{
		ThisParameterDefinition? thisParameter = GetExplicitThisParameter(definition) ?? definition.EffectiveThisParameter;
		if (containingType is null && thisParameter is null)
			return;

		ThisContract contract = GetThisContract(thisParameter);
		string kind = string.IsNullOrWhiteSpace(contract.Lifetime)
			? IsEscapedReceiverDefault(containingType) ? "escaped" : "scoped"
			: contract.Lifetime;
		string source = string.IsNullOrWhiteSpace(contract.Lifetime)
			? kind == "escaped" ? "default escaped receiver" : "default receiver"
			: "explicit receiver";
		definition.ReceiverLifetimeBinding = new BoundLifetime(kind, [], source).ToString();

		if (thisParameter is not null && thisParameter.LifetimeBinding is null)
			BindDefaultReceiverLifetime(thisParameter, kind, source);
	}

	bool IsEscapedReceiverDefault(string? containingType)
	{
		if (containingType is null || !typeDefinitions.TryGetValue(containingType, out TypeDefinition? definition))
			return false;
		return definition is ClassDefinition { IsEscaped: true } or ClassDefinition { IsShadow: true } or ClassDefinition { Extern: not null } or InterfaceDefinition { IsEscaped: true };
	}

	AnalysisScope CreateTypeScope(TypeDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = new(parentScope) { ContainingType = definition };
		foreach (GenericParameter parameter in definition.GenericParameters)
			scope.GenericParameters[parameter.Name] = parameter;

		return scope;
	}

	AnalysisScope CreateStaticMemberScope(TypeDefinition definition, AnalysisScope parentScope)
	{
		return new(parentScope) { ContainingType = definition, IsStaticMemberScope = true };
	}

	static bool FieldUsesStaticMemberScope(FieldDefinition field)
	{
		return field.SourceSyntax is not null && (field.Modifier == FieldModifier.Static || field.IsInline);
	}

	static bool FunctionUsesStaticMemberScope(FunctionDefinition function)
	{
		if (function.SourceSyntax is not MemberDeclarationSyntax syntax)
			return false;

		foreach (MemberDeclaratorSyntax declarator in syntax.Declarators ?? [])
			if (declarator.Keyword?.Value == "static")
				return true;

		return false;
	}

	void AnalyzeGenericParameters(List<GenericParameter> parameters, AnalysisScope scope)
	{
		HashSet<string> names = new(StringComparer.Ordinal);
		foreach (GenericParameter parameter in parameters)
		{
			AnalyzeAttributes(parameter.Attributes);
			ValidateUnsupportedAttributePlacement(parameter, "generic parameters");
			parameter.ResolvedType = parameter.Name;
			CheckName(parameter.Name, GetGenericParameterNameRange(parameter.SourceSyntax), "generic parameter");

			if (!string.IsNullOrWhiteSpace(parameter.Name) && !names.Add(parameter.Name))
				Report(GetGenericParameterNameRange(parameter.SourceSyntax), $"Duplicate generic parameter name '{parameter.Name}'.");
			if (!string.IsNullOrWhiteSpace(parameter.Name) && scope.ContainsInheritedGenericTypeName(parameter.Name))
				Report(GetGenericParameterNameRange(parameter.SourceSyntax), $"Generic parameter '{parameter.Name}' is already declared by an enclosing scope; choose a unique name.");

			AnalyzeOptionalType(parameter.Constraint, scope);
			if (ContainsThisTypeReference(parameter.Constraint))
				Report(GetRange(parameter.Constraint?.SourceSyntax ?? parameter.SourceSyntax), "'this' may be used only as a plain method return type.");
			ValidateNoLifetimeAnnotation(parameter.Constraint, parameter.Constraint?.SourceSyntax ?? parameter.SourceSyntax, "generic constraints");
			ValidateGenericParameterConstraint(parameter);
		}
	}

	void AnalyzeVariableDefinition(VariableDefinition definition, AnalysisScope scope, bool allowSymbolAttribute = true)
	{
		AnalyzeAttributes(definition.Attributes);
		ValidateUnsupportedAttributePlacement(definition);
		SetDefaultTopLevelSymbol(definition, definition.Name);
		ApplySymbolAttribute(definition, allowSymbolAttribute, allowSymbolAttribute ? "variable" : "enum value");
		CheckName(definition.Name, GetNameRange(definition), "variable");
		AnalyzeOutOfScopeMemberOwner(definition);
		if (definition.OutOfScopeOwnerName is not null)
			ValidateOutOfScopeVariable(definition);
		if (definition.IsInline && definition.Extern is not null)
			Report(GetNameRange(definition), "Inline constants cannot be extern.");
		if (definition.IsInline && definition.InitialValue is null)
			Report(GetNameRange(definition), "Inline constants require an initializer.");
		AnalyzeOptionalType(definition.Type, scope);
		if (ContainsClassTypeReference(definition.Type))
			Report(GetRange(definition.Type?.SourceSyntax ?? definition.SourceSyntax), "'classtype' may not be used in global variable types.");
		if (ContainsThisTypeReference(definition.Type))
			Report(GetRange(definition.Type?.SourceSyntax ?? definition.SourceSyntax), "'this' may be used only as a plain method return type.");
		ValidateNoLifetimeAnnotation(definition.Type, definition.Type?.SourceSyntax ?? definition.SourceSyntax, "variable types");
		ValidateFixedStorageMarker(definition.Type, definition.IsFixedStorage, definition.Type?.SourceSyntax ?? definition.SourceSyntax);
		ValidateNoDirectExternClassType(definition.Type, definition.Type?.SourceSyntax ?? definition.SourceSyntax, "global variable storage");
		ValidateNoExternClassArrayElement(definition.Type, definition.Type?.SourceSyntax ?? definition.SourceSyntax);
		definition.ResolvedType = definition.Type?.ResolvedType ?? ErrorType;
		InitializeVariableLifetimeFacts(definition, scope);
		if (definition.InitialValue is not null && !IsValidFixedStorageInitializer(definition.Type, definition.InitialValue))
			AnalyzeOptionalExpression(definition.InitialValue, scope);
	}

	void AnalyzeGlobalInitializers(Module module)
	{
		AnalysisScope typeScope = new();
		foreach (Definition definition in ActiveDefinitions(module))
		{
			if (definition is not VariableDefinition variable || variable.InitialValue is null)
				continue;

			FunctionDefinition initializerContext = new()
			{
				Name = "#global_initializer",
				ResolvedType = variable.ResolvedType ?? variable.Type?.ResolvedType ?? ErrorType
			};
			BodyScope scope = new(null, initializerContext, containingType: null)
			{
				CurrentFunctionReturnType = initializerContext.ResolvedType ?? ErrorType
			};
			string targetType = variable.ResolvedType ?? variable.Type?.ResolvedType ?? ErrorType;
			string initialType = BodyAnalyzeExpression(variable.InitialValue, scope, typeScope, targetType);
			if (!IsValidFixedStorageInitializer(variable.Type, variable.InitialValue))
			{
				CheckAssignable(targetType, initialType, variable.InitialValue.SourceSyntax ?? variable.SourceSyntax, "Global initializer");
				if (ContainsConstOfTypeReference(variable.Type))
					CheckConstOfProducedResult(variable.Type, variable.InitialValue, variable.InitialValue.SourceSyntax ?? variable.SourceSyntax, "Global initializer");
			}
			if (!variable.IsInline)
				ValidateStaticStorageInitializer(variable.InitialValue, variable.Name, "Global initializer");
		}
	}

	void AnalyzeFieldDefinition(FieldDefinition definition, AnalysisScope scope, TypeDefinition? containingType)
	{
		AnalyzeAttributes(definition.Attributes);
		ValidateUnsupportedAttributePlacement(definition);
		if (definition.IsInline)
			definition.Modifier = FieldModifier.Static;
		if (definition.Modifier == FieldModifier.Static && containingType is not null)
		{
			SetDefaultMemberSymbol(definition, EffectiveTypeSymbol(containingType), definition.Name);
			ApplySymbolAttribute(definition, allowed: true, "static field");
		}
		else
		{
			SetDefaultSymbol(definition, definition.Name);
			ApplySymbolAttribute(definition, allowed: false, "field");
		}
		if ((definition.Export is not null || definition.Public is not null || definition.Internal is not null) && definition.Modifier != FieldModifier.Static)
			Report(GetNameRange(definition), "Exported or internal fields must be explicitly marked static.");
		if (definition.Extern is not null && definition.Modifier != FieldModifier.Static)
			Report(GetNameRange(definition), "Extern fields must be explicitly marked static.");
		if (definition.IsInline && definition.Extern is not null)
			Report(GetNameRange(definition), "Inline constants cannot be extern.");
		if (definition.IsInline && definition.InitialValue is null)
			Report(GetNameRange(definition), "Inline constants require an initializer.");
		if (definition.Modifier != FieldModifier.Static && definition.InitialValue is not null && definition.SourceSyntax is not null)
			Report(GetRange(definition.InitialValue.SourceSyntax ?? definition.SourceSyntax), "Instance fields cannot have initializers; initialize the field in a constructor or initializer expression.");
		CheckName(definition.Name, GetNameRange(definition), "field");
		AnalyzeOptionalType(definition.Type, scope);
		if (ContainsClassTypeReference(definition.Type))
			Report(GetRange(definition.Type?.SourceSyntax ?? definition.SourceSyntax), "'classtype' may not be used in fields or static fields.");
		if (ContainsThisTypeReference(definition.Type))
			Report(GetRange(definition.Type?.SourceSyntax ?? definition.SourceSyntax), "'this' may be used only as a plain method return type.");
		ValidateFieldLifetimeAnnotation(definition, scope, containingType);
		ValidateFixedStorageMarker(definition.Type, definition.IsFixedStorage, definition.Type?.SourceSyntax ?? definition.SourceSyntax);
		ValidateNoDirectExternClassType(definition.Type, definition.Type?.SourceSyntax ?? definition.SourceSyntax, definition.Modifier == FieldModifier.Static ? "static field storage" : "field storage");
		ValidateNoExternClassArrayElement(definition.Type, definition.Type?.SourceSyntax ?? definition.SourceSyntax);
		if (containingType is NewtypeDefinition && definition.Modifier != FieldModifier.Static && IsDirectFixedArrayType(definition.Type))
			Report(GetRange(definition.SourceSyntax), "Newtype instance fields may not use fixed-size array storage.");
		if (definition.Type is not null && definition.GeneratedInfo is null && IsErasedGenericValueParameter(definition.Type, scope))
			Report(GetNameRange(definition), "Erased generic type parameters cannot be stored directly in fields; use pointer, span, or explicit erased storage instead.");
		definition.ResolvedType = definition.Type?.ResolvedType ?? ErrorType;
		InitializeFieldLifetimeFacts(definition, scope, containingType);
		if (definition.Modifier == FieldModifier.Static && definition.InitialValue is not null)
		{
			FunctionDefinition initializerContext = new()
			{
				Name = "#static-field-initializer",
				ResolvedType = definition.ResolvedType ?? definition.Type?.ResolvedType ?? ErrorType
			};
			BodyScope bodyScope = new(null, initializerContext, containingType: null)
			{
				CurrentFunctionReturnType = initializerContext.ResolvedType ?? ErrorType
			};
			string targetType = definition.ResolvedType ?? definition.Type?.ResolvedType ?? ErrorType;
			string initialType = BodyAnalyzeExpression(definition.InitialValue, bodyScope, scope, targetType);
			if (!IsValidFixedStorageInitializer(definition.Type, definition.InitialValue))
			{
				CheckAssignable(targetType, initialType, definition.InitialValue.SourceSyntax ?? definition.SourceSyntax, "Static field initializer");
				if (ContainsConstOfTypeReference(definition.Type))
					CheckConstOfProducedResult(definition.Type, definition.InitialValue, definition.InitialValue.SourceSyntax ?? definition.SourceSyntax, "Static field initializer");
			}
			if (!definition.IsInline)
				ValidateStaticStorageInitializer(definition.InitialValue, definition.Name, "Static field initializer");
		}
		else
		{
			AnalyzeOptionalExpression(definition.InitialValue, scope);
		}
	}

	void ValidateStaticStorageInitializer(Expression expression, string ownerName, string context)
	{
		if (IsStaticStorageInitializer(expression))
			return;
		Report(GetRange(expression.SourceSyntax), $"{context} for '{ownerName}' must be a compile-time constant expression.");
	}

	bool IsStaticStorageInitializer(Expression? expression)
	{
		if (expression is null)
			return true;
		if (expressionRewrites.TryGetValue(expression, out Expression? rewrite))
			return IsStaticStorageInitializer(rewrite);

		return expression switch
		{
			LiteralExpression => true,
			DefaultExpression => true,
			SizeOfExpression => true,
			NameOfExpression => true,
			VTableOfExpression => true,
			ParenthesizedExpression parenthesized => IsStaticStorageInitializer(parenthesized.Expression),
			CastExpression cast => IsStaticStorageInitializer(cast.Expression),
			ArrayExpression array => array.Elements.All(IsStaticStorageInitializer),
			InitializerExpression initializer => initializer.Items.All(item => IsStaticStorageInitializer(item.Expression)),
			UnaryExpression { Operator: UnaryOperator.AddressOf } addressOf => IsStaticStorageAddressExpression(addressOf.Operand),
			UnaryExpression unary => IsStaticStorageInitializer(unary.Operand),
			BinaryExpression binary => IsStaticStorageInitializer(binary.Left) && IsStaticStorageInitializer(binary.Right),
			ConditionalExpression conditional => IsStaticStorageInitializer(conditional.Condition) && IsStaticStorageInitializer(conditional.WhenTrue) && IsStaticStorageInitializer(conditional.WhenFalse),
			VariableReferenceExpression variable => IsStaticStorageInitializerReference(variable.Variable),
			MethodReferenceExpression method => method.Candidates.Count > 0,
			MemberReferenceExpression member => IsStaticStorageInitializerReference(member.Member),
			NamedExpression => IsConstant(expression),
			MemberExpression => IsConstant(expression),
			_ => false
		};
	}

	bool IsStaticStorageAddressExpression(Expression? expression)
	{
		if (expression is null)
			return false;
		if (expressionRewrites.TryGetValue(expression, out Expression? rewrite))
			return IsStaticStorageAddressExpression(rewrite);

		return expression switch
		{
			ParenthesizedExpression parenthesized => IsStaticStorageAddressExpression(parenthesized.Expression),
			CastExpression cast => IsStaticStorageAddressExpression(cast.Expression),
			VariableReferenceExpression variable => IsStaticStorageAddressReference(variable.Variable),
			MemberReferenceExpression member => IsStaticStorageAddressReference(member.Member),
			IndexExpression index => IsStaticStorageAddressExpression(index.Target) && index.Arguments.All(argument => IsStaticStorageInitializer(argument.Value)),
			MemberExpression member => IsStaticStorageAddressExpression(member.Target),
			_ => false
		};
	}

	bool IsStaticStorageInitializerReference(BindableNode? node)
	{
		return node switch
		{
			FunctionDefinition => true,
			VariableDefinition variable => variable.IsInline || variable.ConstantValue is not null || IsStaticScalarConstReference(variable.Type, variable.ResolvedType),
			FieldDefinition field => field.IsInline || field.ConstantValue is not null || IsStaticScalarConstReference(field.Type, field.ResolvedType),
			_ => false
		};
	}

	bool IsStaticScalarConstReference(TypeReference? type, string? resolvedType)
	{
		return (IsConstType(type) || IsConstQualified(resolvedType))
			&& IsAllowedInlineScalarType(StripTopLevelValueQualifiers(resolvedType ?? type?.ResolvedType ?? ""));
	}

	static bool IsStaticStorageAddressReference(BindableNode? node)
	{
		return node is VariableDefinition or FieldDefinition or FunctionDefinition;
	}

	void ValidateFieldLifetimeAnnotation(FieldDefinition definition, AnalysisScope scope, TypeDefinition? containingType)
	{
		if (!TryGetFieldLifetimeAnnotation(definition.Type, out string kind, out IReadOnlyList<string> anchors, out string? binding))
			return;

		if (containingType is not ClassDefinition and not StructDefinition || definition.Modifier == FieldModifier.Static)
		{
			Report(GetRange(definition.Type?.SourceSyntax ?? definition.SourceSyntax), "Lifetime annotations are only valid on struct and class instance fields.");
			return;
		}

		if (kind != "escaped" || anchors.Count > 0)
		{
			Report(GetRange(definition.Type?.SourceSyntax ?? definition.SourceSyntax), "Only 'escaped' lifetime annotations are valid on fields.");
			return;
		}

		if (!IsLifetimeTrackedType(definition.Type, definition.Type?.ResolvedType, definition.IsFixedStorage, scope))
		{
			Report(GetRange(definition.Type?.SourceSyntax ?? definition.SourceSyntax), "Field-level 'escaped' requires a pointer-bearing field type.");
			return;
		}

		definition.LifetimeBinding = binding ?? new BoundLifetime("escaped", [], "explicit field").ToString();
	}

	bool TryGetFieldLifetimeAnnotation(TypeReference? type, out string kind, out IReadOnlyList<string> anchors, out string? binding)
	{
		switch (type)
		{
			case EscapedTypeReference escaped:
				kind = "escaped";
				anchors = [];
				binding = escaped.LifetimeBinding;
				return true;

			case ScopedTypeReference scoped:
				kind = "scoped";
				anchors = scoped.Anchors;
				binding = scoped.LifetimeBinding;
				return true;

			case UnscopedTypeReference unscoped:
				kind = "unscoped";
				anchors = unscoped.Anchors;
				binding = unscoped.LifetimeBinding;
				return true;

			case AttributedTypeReference attributed:
				return TryGetFieldLifetimeAnnotation(attributed.Type, out kind, out anchors, out binding);

			case ConstTypeReference constType:
				return TryGetFieldLifetimeAnnotation(constType.Type, out kind, out anchors, out binding);

			case ConstOfTypeReference constOf:
				return TryGetFieldLifetimeAnnotation(constOf.Type, out kind, out anchors, out binding);

			case VolatileTypeReference volatileType:
				return TryGetFieldLifetimeAnnotation(volatileType.Type, out kind, out anchors, out binding);

			case TargetTypeSpecTypeReference targetSpec:
				return TryGetFieldLifetimeAnnotation(targetSpec.Type, out kind, out anchors, out binding);

			case PointerTypeReference pointer:
				return TryGetFieldLifetimeAnnotation(pointer.ElementType, out kind, out anchors, out binding);

			case ArrayTypeReference array:
				return TryGetFieldLifetimeAnnotation(array.ElementType, out kind, out anchors, out binding);

			case FixedArrayTypeReference fixedArray:
				return TryGetFieldLifetimeAnnotation(fixedArray.ElementType, out kind, out anchors, out binding);

			case OptionalTypeReference optional:
				return TryGetFieldLifetimeAnnotation(optional.ElementType, out kind, out anchors, out binding);

			default:
				kind = "";
				anchors = [];
				binding = null;
				return false;
		}
	}

	void AnalyzeFunctionDefinition(FunctionDefinition definition, AnalysisScope parentScope, string? containingType, bool suppressStaticThisDiagnostic = false)
	{
		Definition? previousAnalysisDefinition = currentAnalysisDefinition;
		currentAnalysisDefinition = definition;
		try
		{
		AnalyzeAttributes(definition.Attributes);
		ValidateUnsupportedFunctionAttribute(definition);
		BindAsyncImplementationAttributes(definition, containingType);
		SetDefaultTopLevelSymbol(definition, definition.Name);
		ApplySymbolAttribute(definition, allowed: true, "function");
		CheckName(definition.Name.TrimStart('~'), GetNameRange(definition), "function");
		NormalizeExtensionThisParameter(definition, containingType);
		ApplyImplicitGetterThisParameter(definition, containingType);
		if (!string.IsNullOrWhiteSpace(definition.CallSpec))
			definition.CallSpec = ResolveCallSpecAlias(definition.CallSpec, definition.SourceSyntax);
		ValidateTargetCallSpec(definition.CallSpec, definition.SourceSyntax);
		AnalyzeOutOfScopeMemberOwner(definition);
		if (definition.OutOfScopeOwnerName is not null)
			ValidateOutOfScopeFunction(definition);

		AnalysisScope scope = new(parentScope);
		foreach (GenericParameter parameter in definition.GenericParameters)
			scope.GenericParameters[parameter.Name] = parameter;
		RegisterFunctionLifetimeAnchors(definition, scope, containingType);

		AnalyzeGenericParameters(definition.GenericParameters, scope);

		if (definition.Modifier == FunctionModifier.Constructor)
			definition.ResolvedType = containingType ?? ConstructorType;
		else if (IsDestructorFunction(definition))
			definition.ResolvedType = "void";
		else
			definition.ResolvedType = AnalyzeOptionalType(definition.ReturnType, scope) ?? ErrorType;
		ValidateNoDirectFixedArrayType(definition.ReturnType, definition.ReturnType?.SourceSyntax ?? definition.SourceSyntax, "a function return type");
		ValidateNoDirectExternClassType(definition.ReturnType, definition.ReturnType?.SourceSyntax ?? definition.SourceSyntax, "a function return type");
		ValidateNoExternClassArrayElement(definition.ReturnType, definition.ReturnType?.SourceSyntax ?? definition.SourceSyntax);
		AnalyzeOptionalType(definition.CallableAscriptionType, scope);

		ValidateFunctionModifiers(definition);
		ValidateGenericArgumentUse(definition.ReturnType);

		for (int i = 0; i < definition.Parameters.Count; i++)
		{
			AnalyzeParameterDefinition(definition.Parameters[i], scope, allowThisName: IsExtensionThisParameter(definition, containingType, i), asyncContext: definition.IsAsync);
		}
		ValidateSourceCaptureDefaultValues(definition.Parameters);
		foreach (ParameterDefinition parameter in definition.Parameters)
			if (ContainsThisTypeReference(parameter.Type))
				Report(GetRange(parameter.Type?.SourceSyntax ?? parameter.SourceSyntax), "'this' may be used only as a plain method return type.");
		AnalyzeOverloadDeclaration(definition, containingType);
		ValidateAsyncFunctionParameters(definition);
		ValidateAwaitWithParameters(definition);
		ValidateIteratorGeneratorParameters(definition);
		ValidatePrepParameters(definition);
		ValidateIndexAwareParameters(definition);
		ValidateCallableAscription(definition, containingType);
		ValidateStaticFunctionHasNoExplicitThis(definition, suppressStaticThisDiagnostic);
		BindFunctionReceiverLifetime(definition, containingType);

		ValidateExpandedParameterNames(definition.Parameters);

		if (containingType is not null && (GetExplicitThisParameter(definition) ?? definition.EffectiveThisParameter) is ThisParameterDefinition memberThisParameter)
		{
			string receiverType = typeDefinitions.TryGetValue(containingType, out TypeDefinition? owner) && owner is NewtypeDefinition
				? containingType
				: $"{containingType}*";
			memberThisParameter.ResolvedType = ApplyThisDeclarators(receiverType, memberThisParameter);
		}
		FinalizeThisReturnType(definition, containingType);
		ValidateFunctionConstOfAnchors(definition);
		ValidateAsyncResumer(definition, containingType);
		if (containingType is null && GetExplicitThisParameter(definition) is ThisParameterDefinition thisParameter)
		{
			string receiverType = thisParameter.ResolvedType ?? ErrorType;
			string ownerSymbol = TryGetTypeDefinitionByResolvedName(receiverType, out TypeDefinition? receiverDefinition) && receiverDefinition is not null
				? receiverDefinition.Name
				: BuildExtensionFunctionOwnerSymbol(receiverType, definition);
			SetDefaultOutOfScopeMemberSymbol(definition, ownerSymbol, GetCallableName(definition));
		}
		else if (definition.OutOfScopeOwnerName is not null && definition.OutOfScopeOwnerSymbol is not null)
			SetDefaultOutOfScopeMemberSymbol(definition, definition.OutOfScopeOwnerSymbol, GetCallableName(definition));
		else if (containingType is not null && typeDefinitions.TryGetValue(containingType, out TypeDefinition? typeDefinition))
			SetDefaultMemberSymbol(definition, EffectiveTypeSymbol(typeDefinition), GetCallableName(definition).TrimStart('~'));
		else if (containingType is null)
			SetDefaultTopLevelSymbol(definition, GetCallableName(definition));
		}
		finally
		{
			currentAnalysisDefinition = previousAnalysisDefinition;
		}
	}

	void ValidateStaticFunctionHasNoExplicitThis(FunctionDefinition definition, bool suppressDiagnostic)
	{
		if (!suppressDiagnostic && definition.Modifier == FunctionModifier.Static && GetExplicitThisParameter(definition) is not null)
			Report(GetNameRange(definition), $"Static method '{definition.Name}' may not declare a 'this' parameter.");
	}

	void AnalyzeOutOfScopeMemberOwner(Definition definition)
	{
		if (definition.OutOfScopeOwnerType is null)
			return;

		TypeReference ownerType = definition.OutOfScopeOwnerType;
		definition.OutOfScopeOwnerType = null;
		if (!TryResolveOutOfScopeOwner(ownerType, out string ownerName, out string ownerSymbol))
			return;

		definition.OutOfScopeOwnerName = ownerName;
		definition.OutOfScopeOwnerSymbol = ownerSymbol;
		if (!definition.SymbolOverridden)
			SetDefaultOutOfScopeMemberSymbol(definition, ownerSymbol, definition.Name);
	}

	bool TryResolveOutOfScopeOwner(TypeReference ownerType, out string ownerName, out string ownerSymbol)
	{
		ownerName = "";
		ownerSymbol = "";
		switch (ownerType)
		{
			case PrimitiveTypeReference primitive:
				ownerName = GetPrimitiveTypeName(primitive.Type);
				if (primitive.Type is PrimitiveType.Void)
				{
					Report(GetRange(ownerType.SourceSyntax), "'void' cannot declare out-of-scope static members.");
					return false;
				}
				if (primitive.Type is PrimitiveType.Untyped)
				{
					Report(GetRange(ownerType.SourceSyntax), "'untyped' cannot declare out-of-scope static members.");
					return false;
				}
				ownerSymbol = GetFlattenedSymbolTypeName(ownerName);
				return true;

			case GenericTypeReference generic:
				string genericOwner = BaseTypeName(generic.Type?.ResolvedType ?? FormatTypeReference(generic.Type));
				if (typeDefinitions.TryGetValue(genericOwner, out TypeDefinition? genericDefinition) && genericDefinition.GenericParameters.Count > 0)
					Report(GetRange(ownerType.SourceSyntax), $"Static members of generic type '{FormatGenericTypeDefinitionName(genericDefinition)}' are declared as '{genericDefinition.Name}.{GetOutOfScopeMemberDiagnosticName(ownerType)}'; remove the generic argument list from the owner.");
				else
					Report(GetRange(ownerType.SourceSyntax), $"Out-of-scope static member owner '{FormatTypeReference(ownerType)}' must be a class, struct, newtype, or primitive type name.");
				ownerName = genericOwner;
				ownerSymbol = GetFlattenedSymbolTypeName(genericOwner);
				return false;

			case NamedTypeReference named:
				if (named.TypeArguments.Count > 0)
				{
					foreach (TypeReference argument in named.TypeArguments)
						AnalyzeType(argument, new AnalysisScope());
					string genericOwnerName = BaseTypeName(named.Name);
					if (typeDefinitions.TryGetValue(genericOwnerName, out TypeDefinition? namedGenericDefinition) && namedGenericDefinition.GenericParameters.Count > 0)
						Report(GetRange(ownerType.SourceSyntax), $"Static members of generic type '{FormatGenericTypeDefinitionName(namedGenericDefinition)}' are declared as '{namedGenericDefinition.Name}.{GetOutOfScopeMemberDiagnosticName(ownerType)}'; remove the generic argument list from the owner.");
					else
						Report(GetRange(ownerType.SourceSyntax), $"Out-of-scope static member owner '{FormatTypeReference(ownerType)}' must be a class, struct, newtype, or primitive type name.");
					return false;
				}

				string name = BaseTypeName(named.Name);
				if (aliasDefinitions.ContainsKey(named.Name))
				{
					Report(GetRange(ownerType.SourceSyntax), $"Alias '{named.Name}' cannot be used as an out-of-scope static member owner.");
					return false;
				}
				if (staticClassDefinitions.TryGetValue(name, out StaticClassDefinition? staticClassDefinition))
				{
					ownerName = staticClassDefinition.Name;
					ownerSymbol = staticClassDefinition.Name;
					return true;
				}
				if (!typeDefinitions.TryGetValue(name, out TypeDefinition? definition))
				{
					Report(GetRange(ownerType.SourceSyntax), $"Out-of-scope static member owner '{named.Name}' could not be found.");
					return false;
				}
				if (definition is not ClassDefinition and not StructDefinition and not NewtypeDefinition)
				{
					Report(GetRange(ownerType.SourceSyntax), $"Type '{definition.Name}' cannot declare out-of-scope static members; use a class, struct, newtype, or primitive owner.");
					return false;
				}
				ownerName = definition.Name;
				ownerSymbol = definition.Name;
				return true;

			default:
				Report(GetRange(ownerType.SourceSyntax), $"Out-of-scope static member owner '{FormatTypeReference(ownerType)}' must be a class, struct, newtype, or primitive type name.");
				return false;
		}
	}

	static string GetOutOfScopeMemberDiagnosticName(TypeReference ownerType)
	{
		return ownerType.SourceSyntax is MemberDeclarationSyntax { Identifier: not null } member
			? member.Identifier.Value.Value
			: "member";
	}

	void ValidateOutOfScopeVariable(VariableDefinition definition)
	{
		bool hasStatic = SourceHasDeclarator(definition, "static");
		if (!definition.IsInline && !hasStatic)
			Report(GetNameRange(definition), $"Out-of-scope member '{definition.OutOfScopeOwnerName}.{definition.Name}' must be declared static or inline.");
		if (IsConstType(definition.Type) && !hasStatic)
			Report(GetNameRange(definition), $"Const out-of-scope member '{definition.OutOfScopeOwnerName}.{definition.Name}' must be explicitly declared static.");
	}

	void ValidateOutOfScopeFunction(FunctionDefinition definition)
	{
		if (!SourceHasDeclarator(definition, "static"))
			Report(GetNameRange(definition), $"Out-of-scope method '{definition.OutOfScopeOwnerName}.{definition.Name}' must be explicitly declared static; instance extensions use an explicit 'this' parameter.");
		if (definition.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor)
			Report(GetNameRange(definition), "Constructors and destructors cannot be declared out of scope.");
		if (definition.Modifier is FunctionModifier.Virtual or FunctionModifier.Abstract or FunctionModifier.Override or FunctionModifier.Sealed)
			Report(GetNameRange(definition), "Out-of-scope static methods cannot be virtual, abstract, override, or sealed.");
		if (definition.OutOfScopeOwnerName is not null && typeDefinitions.TryGetValue(definition.OutOfScopeOwnerName, out TypeDefinition? owner))
		{
			foreach (GenericParameter parameter in definition.GenericParameters)
			{
				foreach (GenericParameter ownerParameter in owner.GenericParameters)
				{
					if (parameter.Name == ownerParameter.Name)
						Report(GetGenericParameterNameRange(parameter.SourceSyntax), $"Generic parameter '{parameter.Name}' is already declared by owner type '{owner.Name}'; choose a unique static method parameter name.");
				}
			}
		}
	}

	static bool SourceHasDeclarator(Definition definition, string declaratorName)
	{
		if (definition.SourceSyntax is not MemberDeclarationSyntax syntax)
			return false;
		foreach (MemberDeclaratorSyntax declarator in syntax.Declarators ?? [])
			if (declarator.Keyword?.Value == declaratorName)
				return true;
		return false;
	}

	void BindAsyncImplementationAttributes(FunctionDefinition definition, string? containingType)
	{
		definition.IsNoAwait = HasAttribute(definition.Attributes, "@noawait");
		if (!definition.IsNoAwait)
			return;

		if (!definition.IsAsync)
			Report(GetAttributeRange(definition.Attributes, "@noawait") ?? GetNameRange(definition), "@noawait is valid only on async definitions.");
		if (definition.Body is null)
			Report(GetAttributeRange(definition.Attributes, "@noawait") ?? GetNameRange(definition), "@noawait is valid only on concrete async definitions with a Camp body.");
		if (definition.Extern is not null)
			Report(GetAttributeRange(definition.Attributes, "@noawait") ?? GetNameRange(definition), "@noawait is not valid on extern async declarations.");
		if (definition.Modifier == FunctionModifier.Abstract)
			Report(GetAttributeRange(definition.Attributes, "@noawait") ?? GetNameRange(definition), "@noawait is not valid on abstract async declarations.");
		if (containingType is not null && definition.Body is null)
			Report(GetAttributeRange(definition.Attributes, "@noawait") ?? GetNameRange(definition), "@noawait is valid only on async method definitions with a Camp body.");
	}

	void ValidateAwaitWithParameters(FunctionDefinition definition)
	{
		int count = 0;
		foreach (ParameterDefinition parameter in definition.Parameters)
		{
			parameter.IsAwaitWith = HasAttribute(parameter.Attributes, "@awaitwith");
			if (!parameter.IsAwaitWith)
				continue;

			count++;
			TokenRange? range = GetAttributeRange(parameter.Attributes, "@awaitwith") ?? GetNameRange(parameter) ?? GetRange(parameter.SourceSyntax);
			if (!definition.IsAsync)
				Report(range, "@awaitwith is valid only on parameters of async definitions.");
			if (definition.Body is null)
				Report(range, "@awaitwith is valid only on concrete async definitions with a Camp body.");
			if (definition.Extern is not null)
				Report(range, "@awaitwith is not valid on extern async declarations.");
			if (definition.Modifier == FunctionModifier.Abstract)
				Report(range, "@awaitwith is not valid on abstract async declarations.");
			if (count > 1)
				Report(range, "Async definitions may declare at most one @awaitwith parameter.");
			if (!IsOrdinaryRuntimeAwaitWithParameter(parameter))
				Report(range, "@awaitwith is valid only on ordinary runtime parameters.");
		}
	}

	static TokenRange? GetAttributeRange(List<AttributeConstructor> attributes, string name)
	{
		foreach (AttributeConstructor attribute in attributes)
		{
			if (AttributeNameEquals(attribute.Name, name))
				return attribute.SourceSyntax is null ? null : GetRange(attribute.SourceSyntax);
		}
		return null;
	}

	void ValidateUnsupportedAttributePlacement(BindableNode node, string targetDescription = "this declaration")
	{
		IEnumerable<AttributeConstructor> attributes = node is ParameterDefinition parameter
			? parameter.Attributes
			: node is GenericParameter generic
				? generic.Attributes
				: node is Definition definition
					? definition.Attributes
					: [];
		foreach (AttributeConstructor attribute in attributes)
			if (UnsupportedAvailability.IsUnsupportedAttribute(attribute))
				Report(GetRange(attribute.SourceSyntax ?? node.SourceSyntax), $"@notsupported is valid only on functions and methods, not on {targetDescription}.");
	}

	void ValidateUnsupportedFunctionAttribute(FunctionDefinition definition)
	{
		if (!UnsupportedAvailability.TryGetAttribute(definition.Attributes, out AttributeConstructor? attribute) || attribute is null)
			return;

		if (definition.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor || definition.Name.StartsWith("~", StringComparison.Ordinal))
			Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@notsupported is not valid on constructors or destructors.");
		if (attribute.Arguments.Count > 1)
			Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@notsupported accepts at most one string reason.");
		if (attribute.Arguments.Count == 1 && attribute.Arguments[0].Value is not LiteralExpression { Value: string })
			Report(GetRange(attribute.Arguments[0].SourceSyntax ?? attribute.SourceSyntax ?? definition.SourceSyntax), "@notsupported reason must be a string literal.");
		foreach (ArgumentExpression argument in attribute.Arguments)
			if (!string.IsNullOrWhiteSpace(argument.Name))
				Report(GetRange(argument.SourceSyntax ?? attribute.SourceSyntax ?? definition.SourceSyntax), "@notsupported does not accept named arguments.");
	}

	void FinalizeThisReturnType(FunctionDefinition definition, string? containingType)
	{
		if (!ContainsThisTypeReference(definition.ReturnType))
			return;

		SyntaxNode? syntax = definition.ReturnType?.SourceSyntax ?? definition.SourceSyntax;
		if (definition.ReturnType is not ThisTypeReference)
		{
			Report(GetRange(syntax), "Only plain 'this' may be used as a receiver-preserving return type.");
			return;
		}

		ThisParameterDefinition? receiver = GetExplicitThisParameter(definition) ?? definition.EffectiveThisParameter;
		TypeDefinition? owner = containingType is null || !typeDefinitions.TryGetValue(containingType, out TypeDefinition? ownerDefinition)
			? null
			: ownerDefinition;

		bool invalidLifecycle = definition.Modifier is FunctionModifier.Static or FunctionModifier.Constructor or FunctionModifier.Destructor || IsDestructorFunction(definition);
		bool invalidOwner = owner is InterfaceDefinition || owner is NewtypeDefinition newtype && GetCallableNewtypeFamily(newtype) != "value";
		if (invalidLifecycle || invalidOwner || receiver is null && owner is null)
		{
			Report(GetRange(syntax), "'this' may be used only as the return type of a receiver-bearing concrete method.");
			definition.ResolvedType = ErrorType;
			return;
		}

		if (owner is not null)
			definition.ResolvedType = BuildEffectiveReceiverType($"{owner.Name}*", definition, isPropertyGetterSyntax: false);
		else if (receiver is not null)
			definition.ResolvedType = receiver.ResolvedType ?? ErrorType;
		definition.ReturnType.ResolvedType = definition.ResolvedType;
	}

	void ValidateCallableAscription(FunctionDefinition definition, string? containingType)
	{
		if (definition.CallableAscriptionType is null)
			return;

		SyntaxNode? syntax = definition.CallableAscriptionType.SourceSyntax ?? definition.SourceSyntax;
		string declarationName = GetCallableName(definition);
		if (definition.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor)
		{
			Report(GetRange(syntax), $"Callable ascription on declaration '{declarationName}' is only valid on ordinary functions and methods.");
			return;
		}

		if (definition.CallableAscriptionType is CallableTypeReference or IterTypeReference)
		{
			Report(GetRange(syntax), $"Callable ascription on declaration '{declarationName}' must name a callable newtype, not an anonymous callable type.");
			return;
		}

		string targetType = BaseTypeName(definition.CallableAscriptionType.ResolvedType ?? ErrorType);
		if (targetType.StartsWith(UnresolvedType, StringComparison.Ordinal) || targetType == ErrorType)
			return;

		if (!typeDefinitions.TryGetValue(targetType, out TypeDefinition? targetDefinition))
		{
			Report(GetRange(syntax), $"Callable ascription target '{targetType}' for declaration '{declarationName}' could not be resolved.");
			return;
		}

		if (targetDefinition is InterfaceDefinition)
		{
			if (containingType is null || !typeDefinitions.TryGetValue(containingType, out TypeDefinition? owner) || owner is not ClassDefinition and not StructDefinition)
				Report(GetRange(syntax), $"Interface implementation marker '{targetDefinition.Name}' on declaration '{declarationName}' is only valid on class or struct methods.");
			return;
		}

		if (definition.InterfaceImplementationSlotName is not null)
		{
			Report(GetRange(syntax), $"Interface slot selector '.{definition.InterfaceImplementationSlotName}' on declaration '{declarationName}' requires an interface implementation marker.");
			return;
		}

		if (targetDefinition is not NewtypeDefinition newtypeDefinition)
		{
			Report(GetRange(syntax), $"Callable ascription target '{targetDefinition.Name}' for declaration '{declarationName}' is not a newtype.");
			return;
		}

		string family = GetCallableNewtypeFamily(newtypeDefinition);
		if (family == "value")
		{
			Report(GetRange(syntax), $"Callable ascription target '{newtypeDefinition.Name}' for declaration '{declarationName}' is a value newtype, not a callable newtype.");
			return;
		}

		definition.CallableAscriptionNewtype = newtypeDefinition;

		if (family is "once" or "async" or "async iter")
		{
			Report(GetRange(syntax), $"Callable ascription to {family} newtype '{newtypeDefinition.Name}' on declaration '{declarationName}' is not implemented yet.");
			return;
		}

		bool receiverBearing = IsReceiverBearingDeclaration(definition);
		if (!receiverBearing && family != "fn")
		{
			Report(GetRange(syntax), $"Receiverless declaration '{declarationName}' cannot ascribe {family} newtype '{newtypeDefinition.Name}'; use a fn newtype.");
			return;
		}
		if (receiverBearing && family == "fn")
		{
			Report(GetRange(syntax), $"Receiver-bearing declaration '{declarationName}' cannot ascribe fn newtype '{newtypeDefinition.Name}'; use a delegate or iter newtype.");
			return;
		}
		ValidateCallableAscriptionReceiverTransport(definition, containingType, syntax, declarationName);
		ValidateCallableAscriptionThisContract(definition, containingType, newtypeDefinition, family, syntax, declarationName);

		string sourceType = BuildCallableAscriptionSourceType(definition, family, receiverBearing);
		CallableShape sourceShape = BuildFunctionSourceCallableShape(definition, receiverBearing, family);
		bool sourceShapeAvailable = true;
		bool targetShapeAvailable = TryBuildNewtypeSourceCallableShape(newtypeDefinition, out CallableShape targetShape);
		if (!sourceShapeAvailable
			|| !targetShapeAvailable
			|| !CallableShapesCompatibleWithConstOfVariance(sourceShape, targetShape, compareThis: false, expandParams: false))
		{
			if (sourceShapeAvailable
				&& targetShapeAvailable
				&& CallableShapesCompatibleWithConstOfVariance(sourceShape with { Spec = targetShape.Spec, CallSpec = targetShape.CallSpec }, targetShape, compareThis: false, expandParams: false)
				&& CallableSpecsDiffer(sourceShape, targetShape))
			{
				string actual = CallableSpecDescription(sourceShape);
				string expected = CallableSpecDescription(targetShape);
				Report(GetRange(syntax), $"Callable ascription target '{newtypeDefinition.Name}' requires {expected}, but declaration '{declarationName}' has {actual}.");
			}
			else
			{
				Report(GetRange(syntax), $"Callable ascription target '{newtypeDefinition.Name}' is not compatible with declaration '{declarationName}'.");
			}
		}
	}

	static bool CallableSpecsDiffer(CallableShape source, CallableShape target)
	{
		return source.Spec != target.Spec || source.CallSpec != target.CallSpec;
	}

	static string CallableSpecDescription(CallableShape shape)
	{
		List<string> specs = [];
		if (!string.IsNullOrWhiteSpace(shape.Spec))
			specs.Add(shape.Spec);
		if (!string.IsNullOrWhiteSpace(shape.CallSpec))
			specs.Add(shape.CallSpec);
		return specs.Count == 0
			? "no callspec"
			: $"callspec '{string.Join(" ", specs)}'";
	}

	void ValidateCallableAscriptionReceiverTransport(FunctionDefinition definition, string? containingType, SyntaxNode? syntax, string declarationName)
	{
		if (containingType is not null || GetExplicitThisParameter(definition) is not ThisParameterDefinition thisParameter)
			return;

		string thisType = thisParameter.ResolvedType ?? thisParameter.Type?.ResolvedType ?? ErrorType;
		if (thisParameter.Modifier == ParameterModifier.In
			|| TryGetPointerElementType(thisType) is not null
			|| TryGetParamsComponentShape(thisParameter.Type, thisType, "this", out ParamsComponentShape shape) && shape.Components.Count > 1)
			return;

		Report(GetRange(thisParameter.SourceSyntax ?? syntax),
			$"Callable ascription on extension method '{declarationName}' requires the this parameter to be passed by pointer; use 'in {thisType} this' or a pointer receiver.");
	}

	void ValidateCallableAscriptionThisContract(FunctionDefinition definition, string? containingType, NewtypeDefinition newtypeDefinition, string family, SyntaxNode? syntax, string declarationName)
	{
		if (family is "fn" or "value")
			return;

		ThisParameterDefinition? callableThis = GetCallableNewtypeThisParameter(newtypeDefinition);
		ThisParameterDefinition? explicitThis = GetExplicitThisParameter(definition);
		ThisContract callableContract = GetThisContract(callableThis);
		ThisContract explicitContract = GetThisContract(explicitThis);
		if (explicitThis is not null
			&& callableContract.IsConst
			&& !explicitContract.IsConst
			&& ExplicitExpandedThisTypeIsConst(explicitThis))
		{
			explicitContract = explicitContract with { IsConst = true };
		}

		if (callableThis is not null && explicitThis is not null && callableContract != explicitContract)
		{
			Report(GetRange(explicitThis.SourceSyntax ?? syntax), $"Explicit this qualifiers on declaration '{declarationName}' do not match callable ascription target '{newtypeDefinition.Name}'.");
			return;
		}

		if (callableThis is not null && explicitThis is null)
		{
			definition.EffectiveThisParameter = new ThisParameterDefinition
			{
				SourceSyntax = callableThis.SourceSyntax,
				Name = "this",
				Symbol = "this"
			};
			definition.EffectiveThisParameter.Attributes.AddRange(callableThis.Attributes);
		}

	}

	bool ExplicitExpandedThisTypeIsConst(ThisParameterDefinition parameter)
	{
		string resolvedType = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? FormatTypeReference(parameter.Type);
		if (!TryGetParamsComponentShape(parameter.Type, resolvedType, "this", out ParamsComponentShape shape)
			|| shape.Components.Count <= 1)
			return false;

		string sourceType = FormatTypeReference(parameter.Type);
		return resolvedType.Contains("const ", StringComparison.Ordinal)
			|| sourceType.Contains("const ", StringComparison.Ordinal);
	}

	string BuildCallableAscriptionSourceType(FunctionDefinition definition, string family, bool receiverBearing)
	{
		if (family == "iter" && definition.ReturnType is IterTypeReference)
			return definition.ResolvedType ?? definition.ReturnType.ResolvedType ?? ErrorType;

		return BuildFunctionValueType(definition, receiverBearing);
	}

	void ValidateIteratorGeneratorParameters(FunctionDefinition definition)
	{
		if (definition.IteratorKind == IteratorKind.None)
			return;

		foreach (ParameterDefinition parameter in definition.Parameters)
		{
			if (parameter.Modifier is ParameterModifier.In or ParameterModifier.Out or ParameterModifier.Thrown)
				Report(GetNameRange(parameter), "Generator parameter lists may not contain in, out, or thrown parameters.");
		}
	}

	void ValidatePrepParameters(FunctionDefinition definition)
	{
		ValidatePrepParameterList(definition.Parameters, definition.ReturnType, definition.SourceSyntax, "function");
	}

	void ValidatePrepParameterList(List<ParameterDefinition> parameters, TypeReference? returnType, SyntaxNode? syntax, string context, bool onceCallable = false)
	{
		ParameterDefinition? prep = null;
		int prepIndex = -1;
		for (int parameterIndex = 0; parameterIndex < parameters.Count; parameterIndex++)
		{
			ParameterDefinition parameter = parameters[parameterIndex];
			if (parameter.Modifier != ParameterModifier.Prep)
				continue;

			if (prep is not null)
			{
				Report(GetNameRange(parameter) ?? GetRange(parameter.SourceSyntax ?? syntax), $"A {context} may declare at most one prep parameter.");
				continue;
			}
			prep = parameter;
			prepIndex = parameterIndex;
		}

		if (prep is null)
			return;

		if (onceCallable)
		{
			Report(GetNameRange(prep) ?? GetRange(prep.SourceSyntax ?? syntax), "once callable signatures cannot declare prep parameters.");
			return;
		}

		if (prep.IsOverloadSelector)
			Report(GetNameRange(prep) ?? GetRange(prep.SourceSyntax ?? syntax), "A prep parameter cannot also be an overload selector.");

		for (int parameterIndex = prepIndex + 1; parameterIndex < parameters.Count; parameterIndex++)
		{
			ParameterDefinition following = parameters[parameterIndex];
			if (following.IsOverloadSelector)
			{
				Report(GetNameRange(following) ?? GetRange(following.SourceSyntax ?? syntax), "An overload selector must appear before a prep parameter.");
				continue;
			}
			if (CanOmitCallParameter(following, allowPrep: false))
				continue;
			Report(GetNameRange(following) ?? GetRange(following.SourceSyntax ?? syntax), $"Required parameter '{following.Name}' cannot follow a prep parameter; parameters after prep must have a default value or be supplied by the compiler.");
		}

		if (prep is ThisParameterDefinition or WithinParameterDefinition or SizeOfParameterDefinition or NameOfParameterDefinition or VTableOfParameterDefinition)
			Report(GetNameRange(prep) ?? GetRange(prep.SourceSyntax ?? syntax), "A prep parameter must be an ordinary mutable array parameter.");

		string prepType = prep.ResolvedType ?? prep.Type?.ResolvedType ?? ErrorType;
		if (!TryParseTypeShape(prepType, out TypeShape prepShape) || prepShape.Kind != TypeShapeKind.Array || prepShape.Element is not TypeShape prepElement)
		{
			Report(GetNameRange(prep) ?? GetRange(prep.Type?.SourceSyntax ?? prep.SourceSyntax ?? syntax), "A prep parameter must be a mutable array type.");
			return;
		}

		string elementType = TypeShapeParser.Format(prepElement);
		if (IsConstQualified(elementType))
			Report(GetNameRange(prep) ?? GetRange(prep.Type?.SourceSyntax ?? prep.SourceSyntax ?? syntax), "A prep parameter must be mutable; const array buffers are not valid prep buffers.");

		if (!IsCopyableResolvedType(StripTopLevelValueQualifiers(elementType), null, []))
			Report(GetNameRange(prep) ?? GetRange(prep.Type?.SourceSyntax ?? prep.SourceSyntax ?? syntax), $"Prep array element type '{elementType}' must be copyable.");

		string returnResolvedType = returnType?.ResolvedType ?? ErrorType;
		string expectedReturnType = GetArrayLengthType(prepShape);
		if (returnResolvedType != ErrorType && returnResolvedType != expectedReturnType)
			Report(GetRange(returnType?.SourceSyntax ?? syntax ?? prep.SourceSyntax), $"A prep {context} must return '{expectedReturnType}' to match prep buffer type '{prepType}', not '{returnResolvedType}'.");
	}

	void ValidateAsyncFunctionParameters(FunctionDefinition definition)
	{
		foreach (ParameterDefinition parameter in definition.Parameters)
		{
			if (parameter.Modifier == ParameterModifier.Upon)
				Report(GetNameRange(parameter) ?? GetRange(parameter.SourceSyntax), "The 'upon' scheduler parameter modifier was removed; use @awaitwith on an ordinary parameter or a receiver resumeAsync method.");

			if (definition.IsAsync && parameter.Modifier == ParameterModifier.Out)
				Report(GetNameRange(parameter) ?? GetRange(parameter.SourceSyntax), "Async functions may not declare out parameters; return one value or use the final completion callback shape explicitly.");
			if (definition.IsAsync
				&& parameter.Modifier == ParameterModifier.Within
				&& parameter.LifetimeBinding?.StartsWith("unscoped", StringComparison.Ordinal) == true)
				Report(GetNameRange(parameter) ?? GetRange(parameter.SourceSyntax), "Async functions may not retain an unscoped within allocator across suspension; use an escaped allocator or the bare async within allocator form.");
		}
	}

	void ValidateAsyncResumer(FunctionDefinition definition, string? containingType)
	{
		if (!definition.IsAsync
			|| definition.IteratorKind != IteratorKind.None
			|| definition.ReturnType is IterTypeReference
			|| definition.Body is null
			|| definition.Extern is not null
			|| definition.Modifier == FunctionModifier.Abstract
			|| definition.IsNoAwait)
			return;

		List<ParameterDefinition> explicitResumers = definition.Parameters.Where(static parameter => parameter.IsAwaitWith).ToList();
		if (explicitResumers.Count > 1)
			return;
		ParameterDefinition? explicitResumer = explicitResumers.FirstOrDefault();
		if (explicitResumer is not null && !IsOrdinaryRuntimeAwaitWithParameter(explicitResumer))
			return;
		TypeDefinition? resumerType = null;
		TokenRange? range = null;
		bool receiver = false;
		if (explicitResumer is not null)
		{
			resumerType = TryGetResumerType(explicitResumer.ResolvedType);
			range = GetNameRange(explicitResumer) ?? GetRange(explicitResumer.SourceSyntax);
		}
		else if (containingType is not null && typeDefinitions.TryGetValue(containingType, out TypeDefinition? owner) && IsInstanceFunction(definition))
		{
			resumerType = owner;
			receiver = true;
			range = GetNameRange(definition) ?? GetRange(definition.SourceSyntax);
		}
		else
		{
			Report(GetNameRange(definition) ?? GetRange(definition.SourceSyntax), "Async definitions that can suspend require a resumer; add @awaitwith to an ordinary parameter or define resumeAsync on the receiver.");
			return;
		}

		if (resumerType is null)
		{
			Report(range, "Async resumer must be a pointer to an accessible class, struct, interface, or newtype that provides resumeAsync.");
			return;
		}

		List<FunctionDefinition> viable = LookupTypeFunctions(resumerType, "resumeAsync", resumerType.SourceSyntax)
			.Where(IsCompatibleAsyncResumerFunction)
			.ToList();
		if (viable.Count == 0)
		{
			Report(range, $"Async resumer type '{resumerType.Name}' must provide exactly one compatible resumeAsync method.");
			return;
		}
		if (viable.Count > 1)
		{
			Report(range, $"Async resumer type '{resumerType.Name}' has multiple compatible resumeAsync methods.");
			return;
		}

		definition.AsyncResumerParameter = explicitResumer;
		definition.AsyncResumerIsReceiver = receiver;
		definition.AsyncResumeFunction = viable[0];
		definition.AsyncResumeFunctionIsAsync = viable[0].IsAsync;
	}

	TypeDefinition? TryGetResumerType(string? resolvedType)
	{
		if (string.IsNullOrWhiteSpace(resolvedType))
			return null;
		string normalized = StripLifetimeQualifiers(resolvedType);
		string typeName = TryGetPointerElementType(normalized) ?? normalized;
		typeName = BaseTypeName(typeName);
		return typeDefinitions.TryGetValue(typeName, out TypeDefinition? type) ? type : null;
	}

	bool IsCompatibleAsyncResumerFunction(FunctionDefinition function)
	{
		if ((function.ResolvedType ?? function.ReturnType?.ResolvedType) != "void")
			return false;
		if (function.IsAsync)
			return function.Parameters.All(static parameter => parameter.Modifier != ParameterModifier.Thrown
				&& parameter.Modifier != ParameterModifier.Out
				&& parameter.Modifier != ParameterModifier.Within
				&& parameter is not SizeOfParameterDefinition
				&& parameter is not NameOfParameterDefinition
				&& parameter is not VTableOfParameterDefinition
				&& parameter is not ThisParameterDefinition);
		return function.Parameters.Count == 1 && ResumeContinuationParameterMatches(function.Parameters[0]);
	}

	bool ResumeContinuationParameterMatches(ParameterDefinition parameter)
	{
		if (TryGetCallableTypeReference(parameter.Type, out CallableTypeReference? callableType) && callableType is { Kind: CallableKind.Once })
		{
			string returnType = callableType.ReturnType?.ResolvedType ?? FormatTypeReference(callableType.ReturnType);
			if (returnType != "void")
				return false;
			if (callableType.Parameters.Count == 0)
				return IsEscapedParameter(parameter);
			return callableType.Parameters.Count == 1
				&& callableType.Parameters[0] is ThisParameterDefinition sourceThis
				&& GetThisContract(sourceThis).IsEscaped;
		}

		string type = parameter.ResolvedType ?? "";
		if (TryGetCallableShape(type, out CallableShape shape))
		{
			if (shape.Kind != "once" || shape.ReturnType != "void")
				return false;
			if (shape.Parameters.Count == 0)
				return IsEscapedParameter(parameter) || type.TrimStart().StartsWith("escaped ", StringComparison.Ordinal);
			return shape.Parameters.Count == 0 && shape.This.HasThis && shape.This.IsEscaped;
		}
		if (!typeDefinitions.TryGetValue(BaseTypeName(type), out TypeDefinition? definition) || definition is not NewtypeDefinition newtype)
			return false;
		return newtype.UnderlyingType is CallableTypeReference { Kind: CallableKind.Once } callable
			&& (callable.ReturnType?.ResolvedType ?? FormatTypeReference(callable.ReturnType)) == "void"
			&& newtype.Parameters.Count == 1
			&& newtype.Parameters[0] is ThisParameterDefinition thisParameter
			&& GetThisContract(thisParameter).IsEscaped;
	}

	static bool IsEscapedParameter(ParameterDefinition parameter)
	{
		return parameter.LifetimeBinding?.StartsWith("escaped", StringComparison.Ordinal) == true
			|| IsEscapedTypeReference(parameter.Type)
			|| (parameter.ResolvedType ?? "").TrimStart().StartsWith("escaped ", StringComparison.Ordinal);
	}

	static bool IsOrdinaryRuntimeAwaitWithParameter(ParameterDefinition parameter)
	{
		return parameter.Modifier is not (ParameterModifier.Out or ParameterModifier.Thrown or ParameterModifier.Within or ParameterModifier.Upon)
			&& parameter is not SizeOfParameterDefinition
			&& parameter is not NameOfParameterDefinition
			&& parameter is not VTableOfParameterDefinition
			&& !parameter.IsOverloadSelector;
	}

	static bool IsEscapedTypeReference(TypeReference? type)
	{
		return type switch
		{
			EscapedTypeReference => true,
			AttributedTypeReference attributed => IsEscapedTypeReference(attributed.Type),
			ConstTypeReference constant => IsEscapedTypeReference(constant.Type),
			ConstOfTypeReference constOf => IsEscapedTypeReference(constOf.Type),
			VolatileTypeReference vol => IsEscapedTypeReference(vol.Type),
			_ => false
		};
	}

	static bool TryGetCallableTypeReference(TypeReference? type, out CallableTypeReference? callable)
	{
		switch (type)
		{
			case CallableTypeReference callableType:
				callable = callableType;
				return true;
			case AttributedTypeReference attributed:
				return TryGetCallableTypeReference(attributed.Type, out callable);
			case EscapedTypeReference escaped:
				return TryGetCallableTypeReference(escaped.Type, out callable);
			case ScopedTypeReference scoped:
				return TryGetCallableTypeReference(scoped.Type, out callable);
			case UnscopedTypeReference unscoped:
				return TryGetCallableTypeReference(unscoped.Type, out callable);
			case ConstTypeReference constant:
				return TryGetCallableTypeReference(constant.Type, out callable);
			case ConstOfTypeReference constOf:
				return TryGetCallableTypeReference(constOf.Type, out callable);
			case VolatileTypeReference vol:
				return TryGetCallableTypeReference(vol.Type, out callable);
			default:
				callable = null;
				return false;
		}
	}

	void ValidateExpandedParameterNames(List<ParameterDefinition> parameters)
	{
		Dictionary<string, ParameterDefinition> symbols = new(StringComparer.Ordinal);
		Dictionary<string, string> componentSymbols = new(StringComparer.Ordinal);
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter is ThisParameterDefinition or WithinParameterDefinition)
				continue;

			string name = parameter.Name;
			if (string.IsNullOrWhiteSpace(name))
				continue;

			if (componentSymbols.TryGetValue(name, out string? componentOwner))
				Report(GetNameRange(parameter), $"Symbol '{name}' is already declared in this scope as a component of '{componentOwner}'.");

			if (!symbols.TryAdd(name, parameter))
				Report(GetNameRange(parameter), $"Duplicate parameter name '{name}'.");

			foreach (string componentName in GetPotentialParamsComponentNames(parameter.Type, parameter.ResolvedType, parameter.Name))
			{
				if (componentName == parameter.Name)
					continue;
				if (symbols.ContainsKey(componentName) || componentSymbols.ContainsKey(componentName))
					Report(GetNameRange(parameter), $"Symbol '{componentName}' is already declared in this scope as a component of '{parameter.Name}'.");
				else
					componentSymbols[componentName] = parameter.Name;
			}
		}
	}

	void AnalyzeMethodBodies(Module module)
	{
		foreach (Definition definition in ActiveDefinitions(module))
			AnalyzeDefinitionMethodBodies(definition, new AnalysisScope(), containingType: null);
	}

	void AnalyzeDefinitionMethodBodies(Definition definition, AnalysisScope parentScope, TypeDefinition? containingType)
	{
		switch (definition)
		{
			case ClassDefinition classDefinition:
				AnalyzeTypeMethodBodies(classDefinition, classDefinition.Functions, parentScope);
				break;

			case StaticClassDefinition staticClassDefinition:
				AnalyzeStaticClassMethodBodies(staticClassDefinition, parentScope);
				break;

			case StructDefinition structDefinition:
				AnalyzeTypeMethodBodies(structDefinition, structDefinition.Functions, parentScope);
				break;

			case InterfaceDefinition interfaceDefinition:
				AnalyzeTypeMethodBodies(interfaceDefinition, interfaceDefinition.Functions, parentScope);
				break;

			case EnumDefinition enumDefinition:
				AnalyzeTypeMethodBodies(enumDefinition, enumDefinition.Functions, parentScope);
				break;

			case NewtypeDefinition newtypeDefinition:
				AnalyzeTypeMethodBodies(newtypeDefinition, newtypeDefinition.Functions, parentScope);
				break;

			case ParamsDefinition paramsDefinition:
				AnalyzeTypeMethodBodies(paramsDefinition, paramsDefinition.Functions, parentScope);
				break;

			case FunctionDefinition functionDefinition:
				AnalyzeFunctionMethodBody(functionDefinition, parentScope, containingType);
				break;
		}
	}

	void AnalyzeTypeMethodBodies(TypeDefinition definition, List<FunctionDefinition> functions, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		foreach (FunctionDefinition function in functions)
			AnalyzeFunctionMethodBody(function, FunctionUsesStaticMemberScope(function) ? CreateStaticMemberScope(definition, parentScope) : scope, definition);
	}

	void AnalyzeStaticClassMethodBodies(StaticClassDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = new(parentScope) { IsStaticMemberScope = true };
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionMethodBody(function, scope, containingType: null);
	}

	void AnalyzeFunctionMethodBody(FunctionDefinition definition, AnalysisScope parentScope, TypeDefinition? containingType)
	{
		if (UnsupportedAvailability.IsUnsupported(definition))
			return;

		AnalysisScope scope = new(parentScope);
		foreach (GenericParameter parameter in definition.GenericParameters)
			scope.GenericParameters[parameter.Name] = parameter;
		RegisterFunctionLifetimeAnchors(definition, scope, containingType?.Name);
		AnalyzeMethodBody(definition, scope, containingType);
	}

	void ValidateSourceCaptureDefaultValues(List<ParameterDefinition> parameters)
	{
		HashSet<string> parameterNames = parameters
			.Select(static parameter => parameter.Name)
			.Where(static name => !string.IsNullOrWhiteSpace(name))
			.ToHashSet(StringComparer.Ordinal);

		foreach (ParameterDefinition parameter in parameters)
			ValidateSourceCaptureDefaultValue(parameter.DefaultValue, parameterNames);
	}

	void ValidateSourceCaptureDefaultValue(Expression? expression, HashSet<string> parameterNames)
	{
		switch (expression)
		{
			case null:
				break;
			case SourceOfExpression sourceOf:
				if (!parameterNames.Contains(sourceOf.ArgumentName))
					Report(GetRange(sourceOf.SourceSyntax), $"sourceof(...) argument '{sourceOf.ArgumentName}' does not name a parameter in this signature.");
				break;
			case ParenthesizedExpression parenthesized:
				ValidateSourceCaptureDefaultValue(parenthesized.Expression, parameterNames);
				break;
			case CastExpression cast:
				ValidateSourceCaptureDefaultValue(cast.Expression, parameterNames);
				break;
			case UnaryExpression unary:
				ValidateSourceCaptureDefaultValue(unary.Context, parameterNames);
				ValidateSourceCaptureDefaultValue(unary.Operand, parameterNames);
				break;
			case BinaryExpression binary:
				ValidateSourceCaptureDefaultValue(binary.Left, parameterNames);
				ValidateSourceCaptureDefaultValue(binary.Right, parameterNames);
				break;
			case ConditionalExpression conditional:
				ValidateSourceCaptureDefaultValue(conditional.Condition, parameterNames);
				ValidateSourceCaptureDefaultValue(conditional.WhenTrue, parameterNames);
				ValidateSourceCaptureDefaultValue(conditional.WhenFalse, parameterNames);
				break;
			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
					ValidateSourceCaptureDefaultValue(item.Expression, parameterNames);
				break;
			case ArrayExpression array:
				foreach (Expression element in array.Elements)
					ValidateSourceCaptureDefaultValue(element, parameterNames);
				break;
			case InitializerExpression initializer:
				foreach (InitializerItem item in initializer.Items)
					ValidateSourceCaptureDefaultValue(item.Expression, parameterNames);
				break;
		}
	}

	void AnalyzeParameterDefinition(ParameterDefinition definition, AnalysisScope scope, bool allowThisName = false, bool asyncContext = false)
	{
		AnalyzeAttributes(definition.Attributes);
		ValidateUnsupportedAttributePlacement(definition, "parameters");
		ApplySymbolAttribute(definition, allowed: false, "parameter");

		if (definition is WithinParameterDefinition && definition.Type is null)
			BindImplicitWithinParameterType(definition, scope);

		if (definition is SizeOfParameterDefinition)
		{
			AnalyzeOptionalType(definition.Type, scope);
			definition.Name = SizeOfParameterName(definition.Type);
			definition.Symbol = definition.Name;
		}

		if (IsUserNamedParameter(definition) && !(allowThisName && definition.Name == "this"))
			CheckName(definition.Name, GetNameRange(definition), "parameter");

		if (definition is not SizeOfParameterDefinition and not NameOfParameterDefinition)
			AnalyzeOptionalType(definition.Type, scope);
		if (definition.Type?.LifetimeBinding is string explicitLifetime)
			definition.LifetimeBinding = explicitLifetime;
		else if (TryGetLifetimeAnnotation(definition.Type, out _, out _, out string? nestedLifetime) && nestedLifetime is not null)
			definition.LifetimeBinding = nestedLifetime;
		if (definition is not VTableOfParameterDefinition)
		{
			ValidateNoDirectFixedArrayType(definition.Type, definition.Type?.SourceSyntax ?? definition.SourceSyntax, "a parameter type");
			ValidateNoDirectExternClassType(definition.Type, definition.Type?.SourceSyntax ?? definition.SourceSyntax, "a parameter type");
			ValidateNoExternClassArrayElement(definition.Type, definition.Type?.SourceSyntax ?? definition.SourceSyntax);
		}

		if (definition is VTableOfParameterDefinition vtableOf)
		{
			AnalyzeOptionalType(vtableOf.InterfaceType, scope);
			FinalizeVTableOfParameter(vtableOf, scope);
		}
		if (definition is NameOfParameterDefinition nameOf)
			FinalizeNameOfParameter(nameOf, scope);
		if (definition is ThisParameterDefinition thisParameter)
		{
			ThisContract contract = GetThisContract(thisParameter);
			if (!string.IsNullOrWhiteSpace(contract.Lifetime))
				BindParameterLifetime(thisParameter, contract.Lifetime, [], "explicit receiver");
		}

		if (definition.Modifier == ParameterModifier.Thrown && string.IsNullOrWhiteSpace(definition.Name))
		{
			definition.Name = "error";
			definition.Symbol = "error";
		}

		definition.ResolvedType = definition is SizeOfParameterDefinition
			? GetImplicitParameterType(definition)
			: definition is NameOfParameterDefinition
				? GetImplicitParameterType(definition)
			: definition is VTableOfParameterDefinition vtableOfParameter
				? VTableOfParameterType(vtableOfParameter)
			: definition.Type?.ResolvedType ?? GetImplicitParameterType(definition);
			if (definition.LifetimeBinding is null && definition is not SizeOfParameterDefinition and not NameOfParameterDefinition and not VTableOfParameterDefinition)
			{
				if (definition is WithinParameterDefinition)
					BindParameterLifetime(definition, "scoped", [], "default within");
				else
					BindDefaultParameterLifetime(definition, "default parameter");
			}
			if ((definition is WithinParameterDefinition || definition.Modifier == ParameterModifier.Within) && definition.DefaultValue is not null)
				Report(GetRange(definition.DefaultValue.SourceSyntax ?? definition.SourceSyntax), "within parameters cannot have default values; their value is supplied by an explicit within context or explicit within argument.");
			ValidateGenericArgumentUse(definition.Type);
			ValidateParameterPassing(definition, scope);
			AnalyzeConstantExpression(definition.DefaultValue, scope, "Parameter default value", definition.ResolvedType);
		}

	void BindImplicitWithinParameterType(ParameterDefinition definition, AnalysisScope scope)
	{
		NamedTypeReference allocatorType = new()
		{
			SourceSyntax = definition.SourceSyntax,
			Name = "Allocator"
		};
		AnalyzeType(allocatorType, scope);
		if (allocatorType.ResolvedType == ErrorType)
		{
			Report(GetNameRange(definition), "Implicit within parameter requires an accessible type named 'Allocator'.");
			definition.ResolvedType = ErrorType;
			return;
		}

		PointerTypeReference pointer = new()
		{
			SourceSyntax = definition.SourceSyntax,
			ElementType = allocatorType,
			ResolvedType = $"{allocatorType.ResolvedType}*"
		};
		definition.Type = pointer;
		definition.ResolvedType = definition.Type.ResolvedType;
	}

}
