using System.Collections.Generic;

namespace Camp.Compiler;

internal static class LifecycleAllocatorPolicy
{
	public static bool CreateHelperUsesAllocator(Module? module, TypeDefinition type, FunctionDefinition constructor, bool retainsAllocator = false)
	{
		return retainsAllocator
			|| HasWithinParameter(constructor)
			|| EffectiveWithinPolicy(module, type) == WithinAllocationPolicy.Explicit;
	}

	public static bool DestroyHelperUsesAllocator(Module? module, TypeDefinition type, FunctionDefinition destructor, IReadOnlyList<FunctionDefinition> functions, bool retainsAllocator = false)
	{
		return !retainsAllocator
			&& (HasWithinParameter(destructor)
				|| AnyConstructorHasWithin(functions)
				|| EffectiveWithinPolicy(module, type) == WithinAllocationPolicy.Explicit);
	}

	public static bool ImplicitDestroyHelperUsesAllocator(Module? module, ClassDefinition type, IReadOnlyList<FunctionDefinition> functions, bool retainsAllocator = false)
	{
		return !retainsAllocator
			&& (AnyConstructorHasWithin(functions)
				|| AnyDestructorHasWithin(functions)
				|| EffectiveWithinPolicy(module, type) == WithinAllocationPolicy.Explicit);
	}

	public static bool SyntheticConstructorUsesAllocator(Module? module, ClassDefinition type, IReadOnlyList<FunctionDefinition> functions, bool retainsAllocator = false)
	{
		return retainsAllocator
			|| AnyDestructorHasWithin(functions)
			|| EffectiveWithinPolicy(module, type) == WithinAllocationPolicy.Explicit;
	}

	public static bool SyntheticDestructorUsesAllocator(Module? module, ClassDefinition type, IReadOnlyList<FunctionDefinition> functions, bool retainsAllocator = false)
	{
		return !retainsAllocator
			&& (AnyConstructorHasWithin(functions)
				|| AnyDestructorHasWithin(functions)
				|| EffectiveWithinPolicy(module, type) == WithinAllocationPolicy.Explicit);
	}

	public static bool RetainsAllocator(IReadOnlyList<FunctionDefinition> functions)
	{
		return GetRetainedAllocatorParameter(functions) is not null;
	}

	public static ParameterDefinition? GetRetainedAllocatorParameter(IReadOnlyList<FunctionDefinition> functions)
	{
		foreach (FunctionDefinition function in functions)
		{
			if (function.Modifier != FunctionModifier.Constructor)
				continue;
			if (GetRetainedAllocatorParameter(function) is ParameterDefinition parameter)
				return parameter;
		}
		return null;
	}

	public static ParameterDefinition? GetRetainedAllocatorParameter(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
			if (parameter.RetainsAllocator)
				return parameter;
		return null;
	}

	public static WithinAllocationPolicy EffectiveWithinPolicy(Module? module, Definition definition)
	{
		if (module is not null
			&& SyntaxNodeTraversal.TryGetRange(definition.SourceSyntax, out TokenRange range)
			&& module.SourceWithinAllocationPolicies.TryGetValue(range.Sequence, out WithinAllocationPolicy policy))
		{
			return policy;
		}

		if (module is not null && TryGetUniformSourceWithinPolicy(module, out policy))
			return policy;

		return WithinAllocationPolicy.Implicit;
	}

	static bool TryGetUniformSourceWithinPolicy(Module module, out WithinAllocationPolicy policy)
	{
		policy = WithinAllocationPolicy.Implicit;
		bool found = false;
		foreach (WithinAllocationPolicy candidate in module.SourceWithinAllocationPolicies.Values)
		{
			if (!found)
			{
				policy = candidate;
				found = true;
				continue;
			}
			if (candidate != policy)
				return false;
		}
		return found;
	}

	public static bool HasWithinParameter(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
			if (IsWithinParameter(parameter))
				return true;
		return false;
	}

	static bool AnyConstructorHasWithin(IReadOnlyList<FunctionDefinition> functions)
	{
		foreach (FunctionDefinition function in functions)
			if (function.Modifier == FunctionModifier.Constructor && HasWithinParameter(function))
				return true;
		return false;
	}

	static bool AnyDestructorHasWithin(IReadOnlyList<FunctionDefinition> functions)
	{
		foreach (FunctionDefinition function in functions)
			if (IsDestructorFunction(function) && HasWithinParameter(function))
				return true;
		return false;
	}

	static bool IsWithinParameter(ParameterDefinition parameter)
	{
		return parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition;
	}

	static bool IsDestructorFunction(FunctionDefinition function)
	{
		return function.Modifier == FunctionModifier.Destructor;
	}
}
