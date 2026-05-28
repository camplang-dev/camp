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
		AnalyzeHeaderDirectives(module);
		AssociateForeignHeaderDeclarations(module);

		foreach (UsingDeclaration usingDeclaration in module.Usings)
		{
			usingDeclaration.ResolvedType = UsingType;
			CheckName(usingDeclaration.Alias, GetAliasRange(usingDeclaration.SourceSyntax), "using alias");
		}

		foreach (Definition definition in module.Definitions)
			AnalyzeDefinition(definition, new AnalysisScope());

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

	void AnalyzeDefinition(Definition definition, AnalysisScope parentScope)
	{
		foreach (AttributeConstructor attribute in definition.Attributes)
			AnalyzeAttribute(attribute);

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

			case VariableDefinition variableDefinition:
				AnalyzeVariableDefinition(variableDefinition, parentScope);
				break;

			case FunctionDefinition functionDefinition:
				AnalyzeFunctionDefinition(functionDefinition, parentScope, containingType: null);
				break;
		}
	}

	void AnalyzeClassDefinition(ClassDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeTypeList(definition.BaseTypes, scope);
		RegisterBaseTypes(definition, definition.BaseTypes);

		foreach (FieldDefinition field in definition.Fields)
			AnalyzeFieldDefinition(field, scope);

		ValidateExpandedFieldNames(definition.Fields);
		ValidateDuplicateMethodNames(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
	}

	void AnalyzeStructDefinition(StructDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeTypeList(definition.BaseTypes, scope);
		RegisterBaseTypes(definition, definition.BaseTypes);

		foreach (FieldDefinition field in definition.Fields)
			AnalyzeFieldDefinition(field, scope);

		ValidateExpandedFieldNames(definition.Fields);
		ValidateDuplicateMethodNames(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
	}

	void AnalyzeInterfaceDefinition(InterfaceDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeTypeList(definition.BaseTypes, scope);
		RegisterBaseTypes(definition, definition.BaseTypes);

		ValidateDuplicateMethodNames(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
	}

	void AnalyzeEnumDefinition(EnumDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeOptionalType(definition.UnderlyingType, scope);

		foreach (VariableDefinition value in definition.Values)
			AnalyzeVariableDefinition(value, scope);

		ValidateDuplicateMethodNames(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
	}

	void AnalyzeNewtypeDefinition(NewtypeDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeOptionalType(definition.UnderlyingType, scope);

		foreach (ParameterDefinition parameter in definition.Parameters)
			AnalyzeParameterDefinition(parameter, scope);

		ValidateDuplicateMethodNames(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
	}

	void AnalyzeParamsDefinition(ParamsDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeOptionalType(definition.UnderlyingType, scope);

		foreach (ParameterDefinition component in definition.Components)
			AnalyzeParameterDefinition(component, scope);

		ValidateParamsComponentShape(definition);
		ValidateDuplicateMethodNames(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
	}

	void ValidateDuplicateMethodNames(List<FunctionDefinition> functions)
	{
		HashSet<string> names = new(StringComparer.Ordinal);
		foreach (FunctionDefinition function in functions)
		{
			string name = function.Name;
			if (string.IsNullOrWhiteSpace(name))
				continue;

			if (!names.Add(name))
				Report(GetNameRange(function), $"Duplicate method name '{name}'.");
		}
	}

	void ValidateDuplicateTopLevelSymbols(Module module)
	{
		Dictionary<string, Definition> symbols = new(StringComparer.Ordinal);
		Dictionary<string, string> componentSymbols = new(StringComparer.Ordinal);
		foreach (Definition definition in module.Definitions)
		{
			string symbol = definition.Symbol;
			if (string.IsNullOrWhiteSpace(symbol))
				continue;

			if (componentSymbols.TryGetValue(symbol, out string? componentOwner))
				Report(GetNameRange(definition), $"Symbol '{symbol}' is already declared in this scope as a component of '{componentOwner}'.");

			if (symbols.TryGetValue(symbol, out Definition? existing) && !ReferenceEquals(existing, definition))
				Report(GetNameRange(definition), $"Duplicate symbol name '{symbol}'.");
			else
				symbols[symbol] = definition;

			foreach (string componentName in GetDefinitionComponentSymbolNames(definition))
			{
				if (symbols.ContainsKey(componentName) || componentSymbols.ContainsKey(componentName))
					Report(GetNameRange(definition), $"Symbol '{componentName}' is already declared in this scope as a component of '{definition.Name}'.");
				else
					componentSymbols[componentName] = definition.Name;
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

	void AnalyzeVariableDefinition(VariableDefinition definition, AnalysisScope scope)
	{
		CheckName(definition.Name, GetNameRange(definition), "variable");
		AnalyzeOptionalType(definition.Type, scope);
		definition.ResolvedType = definition.Type?.ResolvedType ?? ErrorType;
		AnalyzeOptionalExpression(definition.InitialValue, scope);
	}

	void AnalyzeFieldDefinition(FieldDefinition definition, AnalysisScope scope)
	{
		CheckName(definition.Name, GetNameRange(definition), "field");
		AnalyzeOptionalType(definition.Type, scope);
		definition.ResolvedType = definition.Type?.ResolvedType ?? ErrorType;
		AnalyzeOptionalExpression(definition.InitialValue, scope);
	}

	void AnalyzeFunctionDefinition(FunctionDefinition definition, AnalysisScope parentScope, string? containingType)
	{
		CheckName(definition.Name.TrimStart('~'), GetNameRange(definition), "function");
		NormalizeExtensionThisParameter(definition, containingType);

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

		ValidateFunctionModifiers(definition);
		ValidateGenericArgumentUse(definition.ReturnType);

		for (int i = 0; i < definition.Parameters.Count; i++)
			AnalyzeParameterDefinition(definition.Parameters[i], scope, allowThisName: IsExtensionThisParameter(definition, containingType, i));

		ValidateExpandedParameterNames(definition.Parameters);

		if (containingType is not null && GetExplicitThisParameter(definition) is ThisParameterDefinition memberThisParameter)
			memberThisParameter.ResolvedType = ApplyThisDeclarators(containingType, memberThisParameter);
		if (containingType is null && GetExplicitThisParameter(definition) is ThisParameterDefinition thisParameter)
			definition.Symbol = BuildExtensionFunctionSymbol(definition.Name, thisParameter.ResolvedType ?? ErrorType);
	}

	void ValidateExpandedParameterNames(List<ParameterDefinition> parameters)
	{
		Dictionary<string, ParameterDefinition> symbols = new(StringComparer.Ordinal);
		Dictionary<string, string> componentSymbols = new(StringComparer.Ordinal);
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter is ThisParameterDefinition or WithinParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition)
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
		if (IsUserNamedParameter(definition) && !(allowThisName && definition.Name == "this"))
			CheckName(definition.Name, GetNameRange(definition), "parameter");

		AnalyzeOptionalType(definition.Type, scope);

		if (definition is VTableOfParameterDefinition vtableOf)
			AnalyzeOptionalType(vtableOf.InterfaceType, scope);

		if (definition.Modifier == ParameterModifier.Thrown && string.IsNullOrWhiteSpace(definition.Name))
		{
			definition.Name = "error";
			definition.Symbol = "error";
		}

		definition.ResolvedType = definition.Type?.ResolvedType ?? GetImplicitParameterType(definition);
		ValidateGenericArgumentUse(definition.Type);
		ValidateParameterPassing(definition, scope);
		AnalyzeConstantExpression(definition.DefaultValue, scope, "Parameter default value");
	}
}
