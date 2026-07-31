using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	sealed record OverloadSelectorFacts(
		ParameterDefinition Selector,
		int SelectorCallableIndex);

	sealed record OverloadParameterShape(
		string Name,
		ParameterModifier Modifier,
		string Type,
		string Attributes);

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

	OverloadSelectorFacts? GetOverloadSelectorFacts(FunctionDefinition function)
	{
		ParameterDefinition? selector = GetOverloadSelector(function);
		if (selector is null)
			return null;

		return new OverloadSelectorFacts(selector, GetCallableOverloadSelectorIndex(function));
	}

	void PrecomputeOverloadCallableNames(Module module)
	{
		foreach (Definition definition in ActiveDefinitions(module))
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
		for (int i = 0; i < function.Parameters.Count; i++)
		{
			ParameterDefinition parameter = function.Parameters[i];
			if (parameter is ThisParameterDefinition)
			{
				if (parameter.IsOverloadSelector)
					Report(GetNameRange(parameter), "`this` cannot be an overload selector.");
				continue;
			}

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
		int? selectorIndex = null;
		List<OverloadParameterShape>? preSelectorShape = null;
		HashSet<string> callableNames = new(StringComparer.Ordinal);

		foreach (FunctionDefinition function in family)
		{
			bool hasSelector = HasOverloadSelector(function);
			anyOverload |= hasSelector;
			anyOrdinary |= !hasSelector;
			if (!hasSelector)
				continue;

			OverloadSelectorFacts? facts = GetOverloadSelectorFacts(function);
			if (facts is not null)
			{
				if (selectorName is null)
					selectorName = facts.Selector.Name;
				else if (selectorName != facts.Selector.Name)
					Report(GetNameRange(facts.Selector), $"Overload family `{invoker}` must use the same selector parameter name.");

				bool selectorPositionMatches = true;
				if (selectorIndex is null)
					selectorIndex = facts.SelectorCallableIndex;
				else if (selectorIndex != facts.SelectorCallableIndex)
				{
					Report(GetNameRange(facts.Selector), $"Overload family `{invoker}` must use the same selector parameter position.");
					selectorPositionMatches = false;
				}

				if (!selectorPositionMatches)
					continue;
				List<OverloadParameterShape> currentShape = BuildPreSelectorShape(function, facts.SelectorCallableIndex);
				if (preSelectorShape is null)
					preSelectorShape = currentShape;
				else
					ValidatePreSelectorShape(invoker, function, preSelectorShape, currentShape);
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

	List<OverloadParameterShape> BuildPreSelectorShape(FunctionDefinition function, int selectorCallableIndex)
	{
		List<OverloadParameterShape> shape = [];
		List<ParameterDefinition> callableParameters = GetCallableParameters(function.Parameters);
		for (int i = 0; i < selectorCallableIndex && i < callableParameters.Count; i++)
			shape.Add(BuildOverloadParameterShape(callableParameters[i]));
		return shape;
	}

	OverloadParameterShape BuildOverloadParameterShape(ParameterDefinition parameter)
	{
		return new OverloadParameterShape(
			parameter.Name,
			parameter.Modifier,
			parameter.ResolvedType ?? ErrorType,
			BuildOverloadParameterAttributeShape(parameter));
	}

	static string BuildOverloadParameterAttributeShape(ParameterDefinition parameter)
	{
		List<string> names = [];
		foreach (AttributeConstructor attribute in parameter.Attributes)
			names.Add(attribute.Name);
		names.Sort(StringComparer.Ordinal);
		return string.Join(",", names);
	}

	void ValidatePreSelectorShape(string invoker, FunctionDefinition function, List<OverloadParameterShape> expected, List<OverloadParameterShape> actual)
	{
		if (expected.Count != actual.Count)
		{
			Report(GetNameRange(function), $"Overload family `{invoker}` must use identical parameters before the overload selector.");
			return;
		}

		for (int i = 0; i < expected.Count; i++)
		{
			if (expected[i] == actual[i])
				continue;

			string parameterName = string.IsNullOrWhiteSpace(actual[i].Name) ? $"#{i + 1}" : actual[i].Name;
			Report(GetNameRange(function), $"Overload family `{invoker}` must use identical parameter '{parameterName}' before the overload selector.");
			return;
		}
	}

	bool OverloadSelectorShapeCompatible(FunctionDefinition declared, FunctionDefinition required, out string message)
	{
		message = "";
		bool declaredOverload = HasOverloadSelector(declared);
		bool requiredOverload = HasOverloadSelector(required);
		if (declaredOverload != requiredOverload)
		{
			message = "overload spelling";
			return false;
		}
		if (!declaredOverload)
			return true;

		OverloadSelectorFacts? declaredFacts = GetOverloadSelectorFacts(declared);
		OverloadSelectorFacts? requiredFacts = GetOverloadSelectorFacts(required);
		if (declaredFacts is null || requiredFacts is null)
			return true;

		if (declaredFacts.Selector.Name != requiredFacts.Selector.Name)
		{
			message = "overload selector name";
			return false;
		}
		if (declaredFacts.SelectorCallableIndex != requiredFacts.SelectorCallableIndex)
		{
			message = "overload selector position";
			return false;
		}

		List<OverloadParameterShape> declaredPreSelector = BuildPreSelectorShape(declared, declaredFacts.SelectorCallableIndex);
		List<OverloadParameterShape> requiredPreSelector = BuildPreSelectorShape(required, requiredFacts.SelectorCallableIndex);
		if (declaredPreSelector.Count != requiredPreSelector.Count)
		{
			message = "pre-selector parameter shape";
			return false;
		}
		for (int i = 0; i < declaredPreSelector.Count; i++)
		{
			if (declaredPreSelector[i] == requiredPreSelector[i])
				continue;
			message = "pre-selector parameter shape";
			return false;
		}
		return true;
	}

	void ValidateTopLevelOverloadFamilies(Module module)
	{
		Dictionary<string, List<FunctionDefinition>> families = new(StringComparer.Ordinal);
		foreach (Definition definition in ActiveDefinitions(module))
		{
			if (definition is not FunctionDefinition function || GetExplicitThisParameter(function) is not null)
				continue;
			string invoker = function.OutOfScopeOwnerName is null
				? GetInvokerName(function)
				: function.OutOfScopeOwnerName + "." + GetInvokerName(function);
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
		if (IsWeakOverloadSelectorArgument(selectorArgument))
		{
			Report(GetRange(selectorSyntax), $"Cannot select overload `{invokerName}` because the selector expression has no independent static type. Add an explicit cast.");
			selectorArgument.ResolvedType = ErrorType;
			if (selectorArgument.Value is not null)
				selectorArgument.Value.ResolvedType = ErrorType;
			return null;
		}
		ParameterDefinition? selector = GetOverloadSelector(candidates[0]);
		if (selector?.Modifier == ParameterModifier.Out && selectorArgument.Modifier != ArgumentModifier.Out)
		{
			Report(GetRange(selectorSyntax), "Out overload selectors require an explicit 'out' argument.");
			selectorArgument.ResolvedType = ErrorType;
			if (selectorArgument.Value is not null)
				selectorArgument.Value.ResolvedType = ErrorType;
			return null;
		}

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

	static bool IsWeakOverloadSelectorArgument(ArgumentExpression argument)
	{
		if (argument.Target is not null)
			return argument.Target.Type is null or AutoTypeReference;
		Expression? expression = UnwrapParenthesizedExpression(argument.Value);
		return expression switch
		{
			LiteralExpression { Kind: LiteralKind.Null } => true,
			DefaultExpression { Type: null } => true,
			InitializerExpression => true,
			LambdaExpression => true,
			_ => false
		};
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
