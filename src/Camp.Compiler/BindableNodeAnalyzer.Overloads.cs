using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	static bool HasOverloadSelector(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
			if (parameter.IsOverloadSelector)
				return true;
		return false;
	}

	static ParameterDefinition? GetOverloadSelector(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
			if (parameter.IsOverloadSelector)
				return parameter;
		return null;
	}

	void PrecomputeOverloadCallableNames(Module module)
	{
		foreach (Definition definition in module.Definitions)
			PrecomputeOverloadCallableNames(definition);
	}

	void PrecomputeOverloadCallableNames(Definition definition)
	{
		switch (definition)
		{
			case FunctionDefinition function:
				PrecomputeOverloadCallableName(function);
				break;
			case ClassDefinition classDefinition:
				foreach (FunctionDefinition function in classDefinition.Functions)
					PrecomputeOverloadCallableName(function);
				break;
			case StructDefinition structDefinition:
				foreach (FunctionDefinition function in structDefinition.Functions)
					PrecomputeOverloadCallableName(function);
				break;
			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					PrecomputeOverloadCallableName(function);
				break;
			case EnumDefinition enumDefinition:
				foreach (FunctionDefinition function in enumDefinition.Functions)
					PrecomputeOverloadCallableName(function);
				break;
			case NewtypeDefinition newtypeDefinition:
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					PrecomputeOverloadCallableName(function);
				break;
			case ParamsDefinition paramsDefinition:
				foreach (FunctionDefinition function in paramsDefinition.Functions)
					PrecomputeOverloadCallableName(function);
				break;
		}
	}

	void PrecomputeOverloadCallableName(FunctionDefinition function)
	{
		function.InvokerName = function.Name;
		function.FullCallableName = "";
		ParameterDefinition? selector = GetOverloadSelector(function);
		if (selector?.Type is null)
			return;

		string? typeName = GetTypeReferenceName(selector.Type);
		string fragment = typeName is null ? "" : BuildFlattenedTypeFragment(typeName, function);
		if (string.IsNullOrWhiteSpace(fragment))
			return;

		function.FullCallableName = function.Name + fragment;
	}

	void AnalyzeOverloadDeclaration(FunctionDefinition function, string? containingType)
	{
		function.InvokerName = function.Name;
		function.FullCallableName = "";

		ParameterDefinition? selector = null;
		int selectorCount = 0;
		int firstOrdinaryIndex = -1;
		for (int i = 0; i < function.Parameters.Count; i++)
		{
			ParameterDefinition parameter = function.Parameters[i];
			if (parameter is ThisParameterDefinition)
			{
				if (parameter.IsOverloadSelector)
					Report(GetNameRange(parameter), "`this` cannot be an overload selector.");
				continue;
			}

			if (firstOrdinaryIndex < 0
				&& parameter is not SizeOfParameterDefinition
				&& parameter is not NameOfParameterDefinition
				&& parameter is not VTableOfParameterDefinition
				&& parameter is not WithinParameterDefinition
				&& parameter.Modifier is not ParameterModifier.Within and not ParameterModifier.Thrown)
				firstOrdinaryIndex = i;

			if (!parameter.IsOverloadSelector)
				continue;

			selector = parameter;
			selectorCount++;
			if (selectorCount > 1)
				Report(GetNameRange(parameter), "A declaration may contain only one overload selector.");
		}

		if (selector is null)
			return;

		if (function.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor || IsDestructorFunction(function))
			Report(GetNameRange(selector), "`overload` may not be used on constructors or destructors.");

		if (selector.Modifier == ParameterModifier.Thrown)
			Report(GetNameRange(selector), "Thrown parameters may not be overload selectors.");

		if (selector.DefaultValue is not null)
			Report(GetNameRange(selector), "An overload selector may not declare a default value.");

		if (firstOrdinaryIndex >= 0 && !ReferenceEquals(function.Parameters[firstOrdinaryIndex], selector))
			Report(GetNameRange(selector), "`overload` may appear only on the first non-this formal parameter.");

		string fragment = BuildFlattenedTypeFragment(selector.ResolvedType ?? ErrorType, function);
		if (string.IsNullOrWhiteSpace(fragment))
			Report(GetNameRange(selector), $"`overload {selector.Name}` is invalid because '{selector.ResolvedType ?? ErrorType}' does not contribute a method-symbol type fragment.");

		function.FullCallableName = function.Name + fragment;
		if (!function.SymbolOverridden && containingType is null && GetExplicitThisParameter(function) is null)
			function.Symbol = function.FullCallableName;
	}

	static string GetInvokerName(FunctionDefinition function)
	{
		return SymbolNameService.InvokerName(function).Value;
	}

	static bool IsOverloadFamily(List<FunctionDefinition> functions)
	{
		if (functions.Count == 0)
			return false;

		foreach (FunctionDefinition function in functions)
			if (!HasOverloadSelector(function))
				return false;
		return true;
	}

	void ValidateOverloadFamily(string invoker, List<FunctionDefinition> family)
	{
		bool anyOverload = false;
		bool anyOrdinary = false;
		string? selectorName = null;
		HashSet<string> callableNames = new(StringComparer.Ordinal);

		foreach (FunctionDefinition function in family)
		{
			bool hasSelector = HasOverloadSelector(function);
			anyOverload |= hasSelector;
			anyOrdinary |= !hasSelector;
			if (!hasSelector)
				continue;

			ParameterDefinition? selector = GetOverloadSelector(function);
			if (selector is not null)
			{
				if (selectorName is null)
					selectorName = selector.Name;
				else if (selectorName != selector.Name)
					Report(GetNameRange(selector), $"Overload family `{invoker}` must use the same selector parameter name.");
			}

			string callable = GetCallableName(function);
			if (!string.IsNullOrWhiteSpace(callable) && !callableNames.Add(callable))
				Report(GetNameRange(function), $"Duplicate overload entry `{callable}`.");
		}

		if (anyOverload && anyOrdinary)
		{
			foreach (FunctionDefinition function in family)
				Report(GetNameRange(function), $"`{invoker}` cannot contain both ordinary declarations and overload declarations.");
		}
	}

	void ValidateTopLevelOverloadFamilies(Module module)
	{
		Dictionary<string, List<FunctionDefinition>> families = new(StringComparer.Ordinal);
		foreach (Definition definition in module.Definitions)
		{
			if (definition is not FunctionDefinition function || GetExplicitThisParameter(function) is not null)
				continue;
			string invoker = GetInvokerName(function);
			if (!families.TryGetValue(invoker, out List<FunctionDefinition>? family))
			{
				family = [];
				families[invoker] = family;
			}
			family.Add(function);
		}

		foreach ((string invoker, List<FunctionDefinition> family) in families)
			if (family.Count > 1)
				ValidateOverloadFamily(invoker, family);
	}

	FunctionDefinition? TrySelectOverload(
		string invokerName,
		List<FunctionDefinition> candidates,
		List<ArgumentExpression> arguments,
		BodyScope scope,
		AnalysisScope typeScope,
		SyntaxNode? syntax)
	{
		if (!IsOverloadFamily(candidates))
			return null;

		int selectorIndex = GetSelectorArgumentIndex(candidates[0], arguments);
		if (selectorIndex < 0 || selectorIndex >= arguments.Count)
		{
			Report(GetRange(syntax), $"`{invokerName}` is an overload family. The selector argument is missing.");
			return null;
		}

		ArgumentExpression selectorArgument = arguments[selectorIndex];
		SyntaxNode? selectorSyntax = OverloadSelectorSyntax(selectorArgument, syntax);
		if (selectorArgument.Value is LambdaExpression)
		{
			Report(GetRange(selectorSyntax), $"Cannot select overload `{invokerName}` from a lambda argument. Call the typed overload entry explicitly.");
			selectorArgument.ResolvedType = ErrorType;
			selectorArgument.Value.ResolvedType = ErrorType;
			return null;
		}
		ParameterDefinition? selector = GetOverloadSelector(candidates[0]);
		if (selector?.Modifier == ParameterModifier.Out && selectorArgument.Modifier != ArgumentModifier.Out)
			Report(GetRange(selectorSyntax), "Out overload selectors require an explicit 'out' argument.");

		string selectorType = BodyAnalyzeOverloadSelectorArgument(selectorArgument, scope, typeScope);
		if (selectorType is TargetType or ErrorType or UnresolvedType)
		{
			Report(GetRange(selectorSyntax), $"Cannot select overload `{invokerName}` because the selector expression has no independent static type. Add an explicit cast.");
			return null;
		}

		string fragment = BuildFlattenedTypeFragment(selectorType);
		if (string.IsNullOrWhiteSpace(fragment))
		{
			Report(GetRange(selectorSyntax), $"Cannot select overload `{invokerName}` because selector type '{selectorType}' does not contribute a method-symbol type fragment.");
			return null;
		}

		string fullName = invokerName + fragment;
		FunctionDefinition? selected = SelectOverloadCandidate(candidates, fullName, selectorSyntax);
		if (selected is null && TryGetPrimitiveStringArrayOverloadName(invokerName, selectorType, out string arrayFullName))
			selected = SelectOverloadCandidate(candidates, arrayFullName, selectorSyntax);

		if (selected is null)
		{
			if (TryGetPrimitiveStringArrayOverloadName(invokerName, selectorType, out string fallbackFullName))
				Report(GetRange(selectorSyntax), $"No overload entry `{fullName}` or `{fallbackFullName}` is visible for selector type `{selectorType}`.");
			else
				Report(GetRange(selectorSyntax), $"No overload entry `{fullName}` is visible for selector type `{selectorType}`.");
		}

		return selected;
	}

	string BodyAnalyzeOverloadSelectorArgument(ArgumentExpression argument, BodyScope scope, AnalysisScope typeScope)
	{
		if (argument.Target is not null)
		{
			AnalyzeOptionalType(argument.Target.Type, typeScope);
			string resolvedType = argument.Target.Type is null or AutoTypeReference
				? ErrorType
				: argument.Target.Type.ResolvedType ?? ErrorType;
			argument.Target.ResolvedType = resolvedType;
			argument.ResolvedType = resolvedType;
			return resolvedType;
		}

		if (argument.Modifier is ArgumentModifier.Out or ArgumentModifier.Catch && IsDiscardExpression(argument.Value))
		{
			if (argument.Type is not null)
				AnalyzeType(argument.Type, typeScope);
			argument.ResolvedType = argument.Type?.ResolvedType ?? ErrorType;
			if (argument.Value is not null)
				argument.Value.ResolvedType = argument.ResolvedType;
			return argument.ResolvedType;
		}

		return BodyAnalyzeArgumentExpression(argument, scope, typeScope, targetType: null);
	}

	static SyntaxNode? OverloadSelectorSyntax(ArgumentExpression selectorArgument, SyntaxNode? fallback)
	{
		return selectorArgument.Value?.SourceSyntax ?? selectorArgument.SourceSyntax ?? fallback;
	}

	FunctionDefinition? SelectOverloadCandidate(List<FunctionDefinition> candidates, string fullName, SyntaxNode? syntax)
	{
		FunctionDefinition? selected = null;
		foreach (FunctionDefinition candidate in candidates)
		{
			if (GetCallableName(candidate) != fullName)
				continue;
			if (selected is not null)
			{
				Report(GetRange(syntax), $"Multiple overload entries named `{fullName}` are visible.");
				return null;
			}
			selected = candidate;
		}
		return selected;
	}

	bool TryGetPrimitiveStringArrayOverloadName(string invokerName, string selectorType, out string fullName)
	{
		fullName = "";
		if (!IsPrimitiveStringType(selectorType))
			return false;

		string fragment = BuildFlattenedTypeFragment(PrimitiveStringConstArrayType(selectorType));
		if (string.IsNullOrWhiteSpace(fragment))
			return false;

		fullName = invokerName + fragment;
		return true;
	}

	static int GetCallableOverloadSelectorIndex(FunctionDefinition function)
	{
		List<ParameterDefinition> parameters = GetCallableParameters(function.Parameters);
		for (int i = 0; i < parameters.Count; i++)
			if (parameters[i].IsOverloadSelector)
				return i;
		return -1;
	}

	static int GetSelectorArgumentIndex(FunctionDefinition function, List<ArgumentExpression> arguments)
	{
		ParameterDefinition? selector = GetOverloadSelector(function);
		if (selector is not null && !string.IsNullOrWhiteSpace(selector.Name))
		{
			for (int i = 0; i < arguments.Count; i++)
				if (arguments[i].Name == selector.Name)
					return i;
		}

		return GetCallableOverloadSelectorIndex(function);
	}
}
