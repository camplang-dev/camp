using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void AnalyzeModule(Module module)
	{
		AnalyzeDeclarations(module);
		if (diagnostics.Count == 0)
			AnalyzeMethodBodies(module);
		ApplyNodeRewrites(module);
	}

	void AnalyzeDeclarations(Module module)
	{
		currentModule = module;
		module.ResolvedType = ModuleType;
		CollectTypeNames(module);
		CollectAliasNames(module);
		ResolveAliases();
		AnalyzeNewtypeSignatures(module);

		foreach (UsingDeclaration usingDeclaration in module.Usings)
		{
			usingDeclaration.ResolvedType = UsingType;
			CheckName(usingDeclaration.Alias, GetAliasRange(usingDeclaration.SourceSyntax), "using alias");
		}

		foreach (Definition definition in module.Definitions)
			AnalyzeDefinition(definition, new AnalysisScope());

		ValidateTopLevelOverloadFamilies(module);
		AnalyzeGlobalInitializers(module);
		ValidateDuplicateTopLevelSymbols(module);
		AnalyzeInheritance();
		AnalyzeImplementations();
		AnalyzeExportVisibility(module);
	}

	void CollectTypeNames(Module module)
	{
		foreach (Definition definition in module.Definitions)
		{
			if (definition is not TypeDefinition typeDefinition)
				continue;

			CheckName(typeDefinition.Name, GetNameRange(typeDefinition), "type");

			if (string.IsNullOrWhiteSpace(typeDefinition.Name))
				continue;

			if (typeDefinitions.TryGetValue(typeDefinition.Name, out TypeDefinition? existing))
			{
				if (!ReferenceEquals(existing, typeDefinition))
					Report(GetNameRange(typeDefinition), $"Duplicate type name '{typeDefinition.Name}'.");
			}
			else if (!typeDefinitions.TryAdd(typeDefinition.Name, typeDefinition))
				Report(GetNameRange(typeDefinition), $"Duplicate type name '{typeDefinition.Name}'.");
			else
				typeInfos[typeDefinition] = new TypeAnalysisInfo(typeDefinition);
		}
	}

	void AnalyzeNewtypeSignatures(Module module)
	{
		AnalysisScope scope = new();
		foreach (Definition definition in module.Definitions)
			if (definition is NewtypeDefinition newtypeDefinition)
				AnalyzeNewtypeSignature(newtypeDefinition, scope);
	}

	void AnalyzeDefinition(Definition definition, AnalysisScope parentScope)
	{
		AnalyzeAttributes(definition.Attributes);

		switch (definition)
		{
			case ClassDefinition classDefinition:
				AnalyzeClassDefinition(classDefinition, parentScope);
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

	void CollectAliasNames(Module module)
	{
		foreach (Definition definition in module.Definitions)
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

		if (alias.TargetQualifiers.Count == 0 && selectedTarget?.HasCallSpec(alias.TargetName) == true)
		{
			alias.TargetKind = AliasTargetKind.CallSpec;
			alias.ResolvedTargetName = alias.TargetName;
			return true;
		}

		if (alias.TargetQualifiers.Count == 0 && selectedTarget?.HasTypeSpec(alias.TargetName) == true)
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
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition is FunctionDefinition function && IsCallableTopLevelFunctionAliasTarget(function, alias, target) && IsDefinitionVisible(function, alias.SourceSyntax))
				matches.Add(function);
		}
		foreach (TypeDefinition type in typeDefinitions.Values)
		{
			foreach (FunctionDefinition function in GetTypeFunctions(type))
			{
				if (IsTypeFunctionSymbolNamed(type, function, target) && IsMemberVisible(function, type, alias.SourceSyntax))
					matches.Add(function);
			}
		}

		if (matches.Count == 0)
			return false;
		if (matches.Count > 1)
		{
			Report(GetNameRange(alias), $"Multiple candidates found for alias target '{target}'.");
			resolvedName = ErrorType;
			return true;
		}

		resolvedName = matches[0].Symbol;
		return true;
	}

	static bool IsCallableTopLevelFunctionAliasTarget(FunctionDefinition function, AliasDefinition alias, string target)
	{
		if (alias.TargetQualifiers.Count > 0)
			return !string.IsNullOrWhiteSpace(function.Symbol) && function.Symbol == target;
		if (GetExplicitThisParameter(function) is not null)
			return !string.IsNullOrWhiteSpace(function.Symbol) && function.Symbol == target;
		return IsFunctionNamed(function, target);
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
		ApplySymbolAttribute(definition, allowed: false, "alias");
		definition.ResolvedType = definition.TargetKind.ToString();
	}

	void AnalyzeClassDefinition(ClassDefinition definition, AnalysisScope parentScope)
	{
		ApplySymbolAttribute(definition, allowed: false, "type");
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeTypeList(definition.BaseTypes, scope);
		RegisterBaseTypes(definition, definition.BaseTypes);

		foreach (FieldDefinition field in definition.Fields)
			AnalyzeFieldDefinition(field, scope, definition);

			foreach (FunctionDefinition function in definition.Functions)
				AnalyzeFunctionDefinition(function, scope, definition.Name);
			ValidateDuplicateMethodNames(definition.Functions);
			GenerateSizeOfFields(definition);
		GenerateVTableOfFields(definition);
		ValidateExpandedFieldNames(definition.Fields);
	}

	void AnalyzeStructDefinition(StructDefinition definition, AnalysisScope parentScope)
	{
		ApplySymbolAttribute(definition, allowed: false, "type");
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeTypeList(definition.BaseTypes, scope);
		RegisterBaseTypes(definition, definition.BaseTypes);

		foreach (FieldDefinition field in definition.Fields)
			AnalyzeFieldDefinition(field, scope, definition);

		ValidateExpandedFieldNames(definition.Fields);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
		ValidateDuplicateMethodNames(definition.Functions);
	}

	void AnalyzeInterfaceDefinition(InterfaceDefinition definition, AnalysisScope parentScope)
	{
		ApplySymbolAttribute(definition, allowed: false, "type");
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeTypeList(definition.BaseTypes, scope);
		RegisterBaseTypes(definition, definition.BaseTypes);

		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
		ValidateDuplicateMethodNames(definition.Functions);
	}

	void AnalyzeEnumDefinition(EnumDefinition definition, AnalysisScope parentScope)
	{
		ApplySymbolAttribute(definition, allowed: false, "type");
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeOptionalType(definition.UnderlyingType, scope);

		foreach (VariableDefinition value in definition.Values)
			AnalyzeVariableDefinition(value, scope, allowSymbolAttribute: false);

		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
		ValidateDuplicateMethodNames(definition.Functions);
	}

	void AnalyzeNewtypeDefinition(NewtypeDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = AnalyzeNewtypeSignature(definition, parentScope);

		foreach (FieldDefinition field in definition.Fields)
			AnalyzeFieldDefinition(field, scope, definition);

		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
		ValidateDuplicateMethodNames(definition.Functions);
	}

	AnalysisScope AnalyzeNewtypeSignature(NewtypeDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		if (!analyzedNewtypeSignatures.Add(definition))
			return scope;

		ApplySymbolAttribute(definition, allowed: false, "type");
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeOptionalType(definition.UnderlyingType, scope);

		foreach (ParameterDefinition parameter in definition.Parameters)
			AnalyzeParameterDefinition(parameter, scope);

		return scope;
	}

	void AnalyzeParamsDefinition(ParamsDefinition definition, AnalysisScope parentScope)
	{
		ApplySymbolAttribute(definition, allowed: false, "type");
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeOptionalType(definition.UnderlyingType, scope);

		foreach (ParameterDefinition component in definition.Components)
			AnalyzeParameterDefinition(component, scope);

		ValidateParamsComponentShape(definition);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
		ValidateDuplicateMethodNames(definition.Functions);
	}

	void ValidateDuplicateMethodNames(List<FunctionDefinition> functions)
	{
		Dictionary<string, List<FunctionDefinition>> invokers = new(StringComparer.Ordinal);
		HashSet<string> callableNames = new(StringComparer.Ordinal);
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
			if (!string.IsNullOrWhiteSpace(callableName) && !callableNames.Add(callableName))
				Report(GetNameRange(function), $"Duplicate method name '{callableName}'.");
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
		Dictionary<string, Definition> symbols = new(StringComparer.Ordinal);
		Dictionary<string, string> componentSymbols = new(StringComparer.Ordinal);
		foreach (Definition definition in module.Definitions)
		{
			foreach (string symbol in GetDefinitionSymbolNames(definition))
			{
				if (string.IsNullOrWhiteSpace(symbol))
					continue;

				if (componentSymbols.TryGetValue(symbol, out string? componentOwner))
					Report(GetNameRange(definition), $"Symbol '{symbol}' is already declared in this scope as a component of '{componentOwner}'.");

				if (symbols.TryGetValue(symbol, out Definition? existing) && !ReferenceEquals(existing, definition))
					Report(GetNameRange(definition), $"Duplicate symbol name '{symbol}'.");
				else
					symbols[symbol] = definition;
			}

			foreach (string componentName in GetDefinitionComponentSymbolNames(definition))
			{
				if (componentName == definition.Name)
					continue;
				if (symbols.ContainsKey(componentName) || componentSymbols.ContainsKey(componentName))
					Report(GetNameRange(definition), $"Symbol '{componentName}' is already declared in this scope as a component of '{definition.Name}'.");
				else
					componentSymbols[componentName] = definition.Name;
			}
		}

		foreach (TypeDefinition type in typeDefinitions.Values)
		{
			foreach (FieldDefinition field in GetTypeFields(type))
			{
				if (field.Modifier != FieldModifier.Static || string.IsNullOrWhiteSpace(field.Symbol))
					continue;

				string symbol = field.Symbol;
				if (componentSymbols.TryGetValue(symbol, out string? componentOwner))
					Report(GetNameRange(field), $"Symbol '{symbol}' is already declared in this scope as a component of '{componentOwner}'.");

				if (symbols.TryGetValue(symbol, out Definition? existing) && !ReferenceEquals(existing, field))
					Report(GetNameRange(field), $"Duplicate symbol name '{symbol}'.");
				else
					symbols[symbol] = field;

				foreach (string componentName in GetPotentialParamsComponentNames(field.Type, field.ResolvedType, field.Symbol))
				{
					if (componentName == field.Symbol)
						continue;
					if (symbols.ContainsKey(componentName) || componentSymbols.ContainsKey(componentName))
						Report(GetNameRange(field), $"Symbol '{componentName}' is already declared in this scope as a component of '{field.Name}'.");
					else
						componentSymbols[componentName] = field.Name;
				}
			}

			foreach (FunctionDefinition function in GetTypeFunctions(type))
			{
				if (!function.SymbolOverridden || string.IsNullOrWhiteSpace(function.Symbol))
					continue;

				string symbol = function.Symbol;
				if (componentSymbols.TryGetValue(symbol, out string? componentOwner))
					Report(GetNameRange(function), $"Symbol '{symbol}' is already declared in this scope as a component of '{componentOwner}'.");

				if (symbols.TryGetValue(symbol, out Definition? existing) && !ReferenceEquals(existing, function))
					Report(GetNameRange(function), $"Duplicate symbol name '{symbol}'.");
				else
					symbols[symbol] = function;
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

	IEnumerable<string> GetDefinitionSymbolNames(Definition definition)
	{
		if (!string.IsNullOrWhiteSpace(definition.Symbol))
			yield return definition.Symbol;

		if (definition is FunctionDefinition function
			&& !string.IsNullOrWhiteSpace(function.FullCallableName)
			&& function.FullCallableName != definition.Symbol
			&& !function.SymbolOverridden
			&& GetExplicitThisParameter(function) is null)
			yield return function.FullCallableName;
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

		definition.Parameters[0] = new ThisParameterDefinition
		{
			SourceSyntax = first.SourceSyntax,
			Name = first.Name,
			Symbol = first.Symbol,
			Type = first.Type,
			DefaultValue = first.DefaultValue,
			Modifier = first.Modifier,
			ResolvedType = first.ResolvedType
		};
	}

	static bool IsExtensionThisParameter(FunctionDefinition definition, string? containingType, int parameterIndex)
	{
		return containingType is null
			&& parameterIndex == 0
			&& definition.Parameters.Count > 0
			&& definition.Parameters[0] is ThisParameterDefinition { Name: "this" };
	}

	AnalysisScope CreateTypeScope(TypeDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = new(parentScope);
		foreach (GenericParameter parameter in definition.GenericParameters)
			scope.GenericParameters[parameter.Name] = parameter;

		return scope;
	}

	void AnalyzeGenericParameters(List<GenericParameter> parameters, AnalysisScope scope)
	{
		HashSet<string> names = new(StringComparer.Ordinal);
		foreach (GenericParameter parameter in parameters)
		{
			parameter.ResolvedType = parameter.Name;
			CheckName(parameter.Name, GetGenericParameterNameRange(parameter.SourceSyntax), "generic parameter");

			if (!string.IsNullOrWhiteSpace(parameter.Name) && !names.Add(parameter.Name))
				Report(GetGenericParameterNameRange(parameter.SourceSyntax), $"Duplicate generic parameter name '{parameter.Name}'.");

			AnalyzeOptionalType(parameter.Constraint, scope);
			ValidateGenericParameterConstraint(parameter);
		}
	}

	void AnalyzeVariableDefinition(VariableDefinition definition, AnalysisScope scope, bool allowSymbolAttribute = true)
	{
		AnalyzeAttributes(definition.Attributes);
		ApplySymbolAttribute(definition, allowSymbolAttribute, allowSymbolAttribute ? "variable" : "enum value");
		CheckName(definition.Name, GetNameRange(definition), "variable");
		AnalyzeOptionalType(definition.Type, scope);
		definition.ResolvedType = definition.Type?.ResolvedType ?? ErrorType;
		AnalyzeOptionalExpression(definition.InitialValue, scope);
	}

	void AnalyzeGlobalInitializers(Module module)
	{
		AnalysisScope typeScope = new();
		foreach (Definition definition in module.Definitions)
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
			CheckAssignable(targetType, initialType, variable.InitialValue.SourceSyntax ?? variable.SourceSyntax, "Global initializer");
		}
	}

	void AnalyzeFieldDefinition(FieldDefinition definition, AnalysisScope scope, TypeDefinition? containingType)
	{
		AnalyzeAttributes(definition.Attributes);
		if (definition.Modifier == FieldModifier.Static && containingType is not null)
		{
			ApplySymbolAttribute(definition, allowed: true, "static field");
			if (!definition.SymbolOverridden)
				definition.Symbol = containingType.Name + "_" + definition.Name;
		}
		else
		{
			ApplySymbolAttribute(definition, allowed: false, "field");
		}
		if ((definition.Export is not null || definition.Public is not null) && definition.Modifier != FieldModifier.Static)
			Report(GetNameRange(definition), "Exported or public fields must be explicitly marked static.");
		CheckName(definition.Name, GetNameRange(definition), "field");
		AnalyzeOptionalType(definition.Type, scope);
		definition.ResolvedType = definition.Type?.ResolvedType ?? ErrorType;
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
			CheckAssignable(targetType, initialType, definition.InitialValue.SourceSyntax ?? definition.SourceSyntax, "Static field initializer");
		}
		else
		{
			AnalyzeOptionalExpression(definition.InitialValue, scope);
		}
	}

	void AnalyzeFunctionDefinition(FunctionDefinition definition, AnalysisScope parentScope, string? containingType)
	{
		AnalyzeAttributes(definition.Attributes);
		ApplySymbolAttribute(definition, allowed: true, "function");
		CheckName(definition.Name.TrimStart('~'), GetNameRange(definition), "function");
		NormalizeExtensionThisParameter(definition, containingType);
		if (!string.IsNullOrWhiteSpace(definition.CallSpec))
			definition.CallSpec = ResolveCallSpecAlias(definition.CallSpec, definition.SourceSyntax);
		ValidateTargetCallSpec(definition.CallSpec, definition.SourceSyntax);

		AnalysisScope scope = new(parentScope);
		foreach (GenericParameter parameter in definition.GenericParameters)
			scope.GenericParameters[parameter.Name] = parameter;

		AnalyzeGenericParameters(definition.GenericParameters, scope);

		if (definition.Modifier == FunctionModifier.Constructor)
			definition.ResolvedType = containingType ?? ConstructorType;
		else if (IsDestructorFunction(definition))
			definition.ResolvedType = "void";
		else
			definition.ResolvedType = AnalyzeOptionalType(definition.ReturnType, scope) ?? ErrorType;
		AnalyzeOptionalType(definition.CallableAscriptionType, scope);

		ValidateFunctionModifiers(definition);
		ValidateGenericArgumentUse(definition.ReturnType);

		for (int i = 0; i < definition.Parameters.Count; i++)
			AnalyzeParameterDefinition(definition.Parameters[i], scope, allowThisName: IsExtensionThisParameter(definition, containingType, i));
		AnalyzeOverloadDeclaration(definition, containingType);
		ValidateIteratorGeneratorParameters(definition);
		ValidateIndexAwareParameters(definition);
		ValidateCallableAscription(definition, containingType);

		ValidateExpandedParameterNames(definition.Parameters);

		if (containingType is not null && GetExplicitThisParameter(definition) is ThisParameterDefinition memberThisParameter)
			memberThisParameter.ResolvedType = ApplyThisDeclarators(containingType, memberThisParameter);
		if (containingType is null && !definition.SymbolOverridden && GetExplicitThisParameter(definition) is ThisParameterDefinition thisParameter)
			definition.Symbol = BuildExtensionFunctionSymbol(GetCallableName(definition), thisParameter.ResolvedType ?? ErrorType, definition);
		else if (containingType is null && !definition.SymbolOverridden && HasOverloadSelector(definition))
			definition.Symbol = GetCallableName(definition);
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

		string sourceType = BuildCallableAscriptionSourceType(definition, family, receiverBearing);
		if (!TryGetCallableShape(sourceType, out CallableShape sourceShape)
			|| !TryGetCallableShape(newtypeDefinition.Name, out CallableShape targetShape)
			|| !CallableShapesCompatible(sourceShape, targetShape))
		{
			Report(GetRange(syntax), $"Callable ascription target '{newtypeDefinition.Name}' is not compatible with declaration '{declarationName}'.");
		}
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
		foreach (Definition definition in module.Definitions)
			AnalyzeDefinitionMethodBodies(definition, new AnalysisScope(), containingType: null);
	}

	void AnalyzeDefinitionMethodBodies(Definition definition, AnalysisScope parentScope, TypeDefinition? containingType)
	{
		switch (definition)
		{
			case ClassDefinition classDefinition:
				AnalyzeTypeMethodBodies(classDefinition, classDefinition.Functions, parentScope);
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
			AnalyzeFunctionMethodBody(function, scope, definition);
	}

	void AnalyzeFunctionMethodBody(FunctionDefinition definition, AnalysisScope parentScope, TypeDefinition? containingType)
	{
		AnalysisScope scope = new(parentScope);
		foreach (GenericParameter parameter in definition.GenericParameters)
			scope.GenericParameters[parameter.Name] = parameter;
		AnalyzeMethodBody(definition, scope, containingType);
	}

	void AnalyzeParameterDefinition(ParameterDefinition definition, AnalysisScope scope, bool allowThisName = false)
	{
		AnalyzeAttributes(definition.Attributes);
		ApplySymbolAttribute(definition, allowed: false, "parameter");

		if (definition is SizeOfParameterDefinition)
		{
			AnalyzeOptionalType(definition.Type, scope);
			definition.Name = SizeOfParameterName(definition.Type);
			definition.Symbol = definition.Name;
		}

		if (IsUserNamedParameter(definition) && !(allowThisName && definition.Name == "this"))
			CheckName(definition.Name, GetNameRange(definition), "parameter");

		if (definition is not SizeOfParameterDefinition)
			AnalyzeOptionalType(definition.Type, scope);

		if (definition is WithinParameterDefinition && definition.Type is null)
			BindImplicitWithinParameterType(definition, scope);

		if (definition is VTableOfParameterDefinition vtableOf)
		{
			AnalyzeOptionalType(vtableOf.InterfaceType, scope);
			FinalizeVTableOfParameter(vtableOf, scope);
		}

		if (definition.Modifier == ParameterModifier.Thrown && string.IsNullOrWhiteSpace(definition.Name))
		{
			definition.Name = "error";
			definition.Symbol = "error";
		}

		definition.ResolvedType = definition is SizeOfParameterDefinition
			? GetImplicitParameterType(definition)
			: definition is VTableOfParameterDefinition vtableOfParameter
				? VTableOfParameterType(vtableOfParameter)
			: definition.Type?.ResolvedType ?? GetImplicitParameterType(definition);
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

		definition.Type = new PointerTypeReference
		{
			SourceSyntax = definition.SourceSyntax,
			ElementType = allocatorType,
			ResolvedType = $"{allocatorType.ResolvedType}*"
		};
		definition.ResolvedType = definition.Type.ResolvedType;
	}
}
