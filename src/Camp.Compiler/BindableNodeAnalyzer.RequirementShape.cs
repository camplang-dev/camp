using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	IEnumerable<FieldDefinition> SelectedFields(IEnumerable<FieldDefinition> fields)
	{
		foreach (FieldDefinition field in fields)
			if (IsDefinitionRequirementSatisfied(field))
				yield return field;
	}

	IEnumerable<FunctionDefinition> SelectedFunctions(IEnumerable<FunctionDefinition> functions)
	{
		foreach (FunctionDefinition function in functions)
			if (IsDefinitionRequirementSatisfied(function))
				yield return function;
	}

	List<FunctionDefinition> SelectedInterfaceMembers(InterfaceDefinition definition)
	{
		List<FunctionDefinition> members = [];
		WithRequirementProof(definition.EffectiveRequirement, () => members.AddRange(SelectedFunctions(definition.Functions)));
		return members;
	}

	static void InheritRequirement(Definition target, Definition? source)
	{
		target.EffectiveRequirement = source?.EffectiveRequirement;
	}

	static void ApplyCombinedRequirement(Definition target, Definition? first, Definition? second)
	{
		target.EffectiveRequirement = ConfigurationFlagExpressionBinder.And(first?.EffectiveRequirement, second?.EffectiveRequirement);
	}
}
