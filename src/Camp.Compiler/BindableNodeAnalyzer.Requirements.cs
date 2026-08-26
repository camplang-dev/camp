using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void BindRequirementAttributes(Module module)
	{
		foreach (SourceFile file in module.SourceFiles.Values)
			foreach (SourceRequirement requirement in file.SourceRequirements)
				BindSourceRequirement(requirement);
		foreach (Definition definition in module.Definitions)
			BindRequirementAttributes(definition);
	}

	void BindRequirementAttributes(Definition definition)
	{
		foreach (SourceRequirement requirement in definition.SourceRequirements)
			BindSourceRequirement(requirement);
		foreach (Definition child in GetRequirementChildDefinitions(definition))
			BindRequirementAttributes(child);
	}

	void BindSourceRequirement(SourceRequirement requirement)
	{
		if (requirement.Requirement is not null)
			return;
		if (ConfigurationFlagExpressionBinder.TryBind(requirement.Expression, configurationFlags, (range, message) => Report(range, message), out ConfigurationFlagExpression? expression))
			requirement.Requirement = expression;
		if (requirement.Expression is not null)
			requirement.Expression.ResolvedType = AttributeType;
	}

	void ApplyEffectiveRequirements(Module module)
	{
		foreach (Definition definition in module.Definitions)
			ApplyEffectiveRequirement(definition, inheritedRequirement: null, topLevel: true, ownerKind: "");
	}

	void ApplyEffectiveRequirement(Definition definition, ConfigurationFlagExpression? inheritedRequirement, bool topLevel, string ownerKind)
	{
		ConfigurationFlagExpression? generatedRequirement = definition.GeneratedInfo is not null ? definition.EffectiveRequirement : null;
		ConfigurationFlagExpression? effective = inheritedRequirement;
		if (topLevel)
		{
			ConfigurationFlagExpression? fileRequirement = GetFileRequirement(definition);
			if (fileRequirement is not null)
				effective = ConfigurationFlagExpressionBinder.And(effective, fileRequirement);
		}
		foreach (SourceRequirement requirement in definition.SourceRequirements)
			if (requirement.Requirement is not null)
				effective = ConfigurationFlagExpressionBinder.And(effective, requirement.Requirement);

		if (DeclarationParticipation.IsTest(definition) || DeclarationParticipation.HasExplicitTestOnly(definition))
			effective = ConfigurationFlagExpressionBinder.And(effective, ConfigurationFlagExpressionBinder.Flag("TEST_MODULE"));

		if (definition.GeneratedInfo?.Source is Definition source && source.EffectiveRequirement is not null)
			effective = ConfigurationFlagExpressionBinder.And(effective, source.EffectiveRequirement);
		if (generatedRequirement is not null)
			effective = ConfigurationFlagExpressionBinder.And(effective, generatedRequirement);

		definition.EffectiveRequirement = effective;
		ValidateRequirementSatisfiability(definition, effective);

		bool childTopLevel = false;
		string childOwnerKind = definition switch
		{
			ClassDefinition => "class",
			StaticClassDefinition => "static class",
			StructDefinition => "struct",
			InterfaceDefinition => "interface",
			EnumDefinition => "enum",
			NewtypeDefinition => "newtype",
			ParamsDefinition => "params",
			FunctionDefinition => "function",
			_ => ownerKind
		};
		foreach (Definition child in GetRequirementChildDefinitions(definition))
			ApplyEffectiveRequirement(child, effective, childTopLevel, childOwnerKind);
	}

	void ValidateRequirementSatisfiability(Definition definition, ConfigurationFlagExpression? requirement)
	{
		if (requirement is null || IsExactRequirementConstant(requirement))
			return;
		if (!TryProveUnsatisfiableRequirement(requirement, maxFlagCount: 16, out bool unsatisfiable) || !unsatisfiable)
			return;
		SourceRequirement? sourceRequirement = definition.SourceRequirements.LastOrDefault();
		ReportWarning(GetRange(sourceRequirement?.SourceSyntax ?? definition.SourceSyntax), $"Requirement '{requirement}' cannot be satisfied.");
	}

	static bool IsExactRequirementConstant(ConfigurationFlagExpression requirement) =>
		requirement.Kind == ConfigurationFlagExpressionKind.Literal
		|| requirement.Kind == ConfigurationFlagExpressionKind.Flag && (requirement.FlagName == "TRUE" || requirement.FlagName == "FALSE");

	bool TryProveUnsatisfiableRequirement(ConfigurationFlagExpression requirement, int maxFlagCount, out bool unsatisfiable)
	{
		unsatisfiable = false;
		HashSet<string> names = new(StringComparer.Ordinal);
		ConfigurationFlagExpressionBinder.CollectFlagNames(requirement, names);
		names.Remove("TRUE");
		names.Remove("FALSE");
		if (names.Count > maxFlagCount)
			return false;
		string[] flagNames = [.. names.Order(StringComparer.Ordinal)];
		Dictionary<string, bool> assignment = new(StringComparer.Ordinal);
		bool satisfiable = HasSatisfyingAssignment(0);
		unsatisfiable = !satisfiable;
		return true;

		bool HasSatisfyingAssignment(int index)
		{
			if (index == flagNames.Length)
				return EvaluateRequirementWithAssignment(requirement, assignment);
			string name = flagNames[index];
			assignment[name] = false;
			if (HasSatisfyingAssignment(index + 1))
				return true;
			assignment[name] = true;
			if (HasSatisfyingAssignment(index + 1))
				return true;
			assignment.Remove(name);
			return false;
		}
	}

	static bool EvaluateRequirementWithAssignment(ConfigurationFlagExpression requirement, IReadOnlyDictionary<string, bool> assignment) =>
		requirement.Kind switch
		{
			ConfigurationFlagExpressionKind.Flag => requirement.FlagName switch
			{
				"TRUE" => true,
				"FALSE" => false,
				string name => assignment.TryGetValue(name, out bool value) && value,
				_ => false
			},
			ConfigurationFlagExpressionKind.Literal => requirement.LiteralValue,
			ConfigurationFlagExpressionKind.Not => requirement.Left is not null && !EvaluateRequirementWithAssignment(requirement.Left, assignment),
			ConfigurationFlagExpressionKind.And => requirement.Left is not null && requirement.Right is not null && EvaluateRequirementWithAssignment(requirement.Left, assignment) && EvaluateRequirementWithAssignment(requirement.Right, assignment),
			ConfigurationFlagExpressionKind.Or => requirement.Left is not null && requirement.Right is not null && (EvaluateRequirementWithAssignment(requirement.Left, assignment) || EvaluateRequirementWithAssignment(requirement.Right, assignment)),
			ConfigurationFlagExpressionKind.Xor => requirement.Left is not null && requirement.Right is not null && EvaluateRequirementWithAssignment(requirement.Left, assignment) ^ EvaluateRequirementWithAssignment(requirement.Right, assignment),
			_ => false
		};

	ConfigurationFlagExpression? GetFileRequirement(Definition definition)
	{
		if (currentModule is null
			|| !currentModule.DefinitionSources.TryGetValue(definition, out TokenSequence? source)
			|| source is null
			|| !currentModule.SourceFiles.TryGetValue(source, out SourceFile? file))
			return null;
		ConfigurationFlagExpression? requirement = null;
		foreach (SourceRequirement sourceRequirement in file.SourceRequirements)
			if (sourceRequirement.Requirement is not null)
				requirement = ConfigurationFlagExpressionBinder.And(requirement, sourceRequirement.Requirement);
		return requirement;
	}

	void ValidateRequirementAttributePlacements(Module module)
	{
		foreach (SourceFile file in module.SourceFiles.Values)
			foreach (AttributeConstructor attribute in file.FileMetadataAttributes)
				if (AttributeNameEquals(attribute.Name, "@require"))
					Report(GetRange(attribute.SourceSyntax), "@require is no longer supported; use 'requires (CONDITION);' for file-wide requirements or 'requires (CONDITION)' before a declaration.");
		foreach (Definition definition in module.Definitions)
			ValidateRequirementAttributePlacement(definition, topLevel: true, ownerKind: "");
	}

	void ValidateRequirementAttributePlacement(Definition definition, bool topLevel, string ownerKind)
	{
		foreach (AttributeConstructor attribute in definition.Attributes)
		{
			if (!AttributeNameEquals(attribute.Name, "@require"))
				continue;
			Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@require is no longer supported; use 'requires (CONDITION)' before a declaration.");
		}

		foreach (SourceRequirement requirement in definition.SourceRequirements)
		{
			if (definition is ParameterDefinition)
				Report(GetRange(requirement.SourceSyntax ?? definition.SourceSyntax), "requires is not valid on parameters.");
			else if (definition is VariableDefinition && ownerKind == "enum")
				Report(GetRange(requirement.SourceSyntax ?? definition.SourceSyntax), "requires is not valid on enum values.");
			else if (definition is FunctionDefinition { Modifier: FunctionModifier.Constructor or FunctionModifier.Destructor })
				Report(GetRange(requirement.SourceSyntax ?? definition.SourceSyntax), "requires is not valid on constructors or destructors.");
		}

		switch (definition)
		{
			case ClassDefinition classDefinition:
				ValidateRequirementAttributes(classDefinition.GenericParameters);
				foreach (FieldDefinition field in classDefinition.Fields)
					ValidateRequirementAttributePlacement(field, topLevel: false, ownerKind: "class");
				foreach (FunctionDefinition function in classDefinition.Functions)
					ValidateRequirementAttributePlacement(function, topLevel: false, ownerKind: "class");
				break;
			case StructDefinition structDefinition:
				ValidateRequirementAttributes(structDefinition.GenericParameters);
				foreach (FieldDefinition field in structDefinition.Fields)
					ValidateRequirementAttributePlacement(field, topLevel: false, ownerKind: "struct");
				foreach (FunctionDefinition function in structDefinition.Functions)
					ValidateRequirementAttributePlacement(function, topLevel: false, ownerKind: "struct");
				break;
			case InterfaceDefinition interfaceDefinition:
				ValidateRequirementAttributes(interfaceDefinition.GenericParameters);
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					ValidateRequirementAttributePlacement(function, topLevel: false, ownerKind: "interface");
				break;
			case EnumDefinition enumDefinition:
				foreach (VariableDefinition value in enumDefinition.Values)
					ValidateRequirementAttributePlacement(value, topLevel: false, ownerKind: "enum");
				foreach (FunctionDefinition function in enumDefinition.Functions)
					ValidateRequirementAttributePlacement(function, topLevel: false, ownerKind: "enum");
				break;
			case NewtypeDefinition newtypeDefinition:
				ValidateRequirementAttributes(newtypeDefinition.GenericParameters);
				foreach (ParameterDefinition parameter in newtypeDefinition.Parameters)
					ValidateRequirementAttributePlacement(parameter, topLevel: false, ownerKind: "newtype");
				foreach (FieldDefinition field in newtypeDefinition.Fields)
					ValidateRequirementAttributePlacement(field, topLevel: false, ownerKind: "newtype");
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					ValidateRequirementAttributePlacement(function, topLevel: false, ownerKind: "newtype");
				break;
			case ParamsDefinition paramsDefinition:
				foreach (ParameterDefinition component in paramsDefinition.Components)
					ValidateRequirementAttributePlacement(component, topLevel: false, ownerKind: "params");
				foreach (FunctionDefinition function in paramsDefinition.Functions)
					ValidateRequirementAttributePlacement(function, topLevel: false, ownerKind: "params");
				break;
			case FunctionDefinition functionDefinition:
				ValidateRequirementAttributes(functionDefinition.GenericParameters);
				foreach (ParameterDefinition parameter in functionDefinition.Parameters)
					ValidateRequirementAttributePlacement(parameter, topLevel: false, ownerKind: "function");
				break;
		}
	}

	void ValidateRequirementAttributes(IEnumerable<GenericParameter> parameters)
	{
		foreach (GenericParameter parameter in parameters)
		{
			foreach (AttributeConstructor attribute in parameter.Attributes)
			{
				if (AttributeNameEquals(attribute.Name, "@require"))
					Report(GetRange(attribute.SourceSyntax ?? parameter.SourceSyntax), "@require is no longer supported; use 'requires (CONDITION)' before a declaration.");
			}
		}
	}

	static IEnumerable<Definition> GetRequirementChildDefinitions(Definition definition)
	{
		switch (definition)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
					yield return field;
				foreach (FunctionDefinition function in classDefinition.Functions)
					yield return function;
				break;
			case StaticClassDefinition staticClassDefinition:
				foreach (FieldDefinition field in staticClassDefinition.Fields)
					yield return field;
				foreach (FunctionDefinition function in staticClassDefinition.Functions)
					yield return function;
				break;
			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					yield return field;
				foreach (FunctionDefinition function in structDefinition.Functions)
					yield return function;
				break;
			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					yield return function;
				break;
			case EnumDefinition enumDefinition:
				foreach (VariableDefinition value in enumDefinition.Values)
					yield return value;
				foreach (FunctionDefinition function in enumDefinition.Functions)
					yield return function;
				break;
			case NewtypeDefinition newtypeDefinition:
				foreach (ParameterDefinition parameter in newtypeDefinition.Parameters)
					yield return parameter;
				foreach (FieldDefinition field in newtypeDefinition.Fields)
					yield return field;
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					yield return function;
				break;
			case ParamsDefinition paramsDefinition:
				foreach (ParameterDefinition component in paramsDefinition.Components)
					yield return component;
				foreach (FunctionDefinition function in paramsDefinition.Functions)
					yield return function;
				break;
			case FunctionDefinition functionDefinition:
				foreach (ParameterDefinition parameter in functionDefinition.Parameters)
					yield return parameter;
				break;
		}
	}
}
