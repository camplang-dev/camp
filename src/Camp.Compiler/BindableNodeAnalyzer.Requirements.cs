using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void BindRequirementAttributes(Module module)
	{
		foreach (SourceFile file in module.SourceFiles.Values)
			foreach (AttributeConstructor attribute in file.FileMetadataAttributes)
				BindRequirementAttribute(attribute);
		foreach (Definition definition in module.Definitions)
			BindRequirementAttributes(definition);
	}

	void BindRequirementAttributes(Definition definition)
	{
		foreach (AttributeConstructor attribute in definition.Attributes)
			BindRequirementAttribute(attribute);
		foreach (Definition child in GetRequirementChildDefinitions(definition))
			BindRequirementAttributes(child);
	}

	void BindRequirementAttribute(AttributeConstructor attribute)
	{
		if (!AttributeNameEquals(attribute.Name, "@require") || attribute.Requirement is not null)
			return;
		if (attribute.Arguments.Count != 1 || !string.IsNullOrWhiteSpace(attribute.Arguments[0].Name))
			return;
		ArgumentExpression argument = attribute.Arguments[0];
		if (ConfigurationFlagExpressionBinder.TryBind(argument.Value, configurationFlags, (range, message) => Report(range, message), out ConfigurationFlagExpression? requirement))
			attribute.Requirement = requirement;
		argument.Value!.ResolvedType = AttributeType;
		argument.ResolvedType = AttributeType;
	}

	void ApplyEffectiveRequirements(Module module)
	{
		foreach (Definition definition in module.Definitions)
			ApplyEffectiveRequirement(definition, inheritedRequirement: null, topLevel: true, ownerKind: "");
	}

	void ApplyEffectiveRequirement(Definition definition, ConfigurationFlagExpression? inheritedRequirement, bool topLevel, string ownerKind)
	{
		ConfigurationFlagExpression? explicitRequirement = GetExplicitRequirement(definition.Attributes);
		ConfigurationFlagExpression? effective = inheritedRequirement;
		if (topLevel)
			effective = explicitRequirement ?? GetFileRequirement(definition);
		else if (explicitRequirement is not null)
			effective = ConfigurationFlagExpressionBinder.And(effective, explicitRequirement);

		if (DeclarationParticipation.IsTest(definition) || DeclarationParticipation.HasExplicitTestOnly(definition))
			effective = ConfigurationFlagExpressionBinder.And(effective, ConfigurationFlagExpressionBinder.Flag("TEST_MODULE"));

		if (definition.GeneratedInfo?.Source is Definition source && source.EffectiveRequirement is not null)
			effective = ConfigurationFlagExpressionBinder.And(effective, source.EffectiveRequirement);

		definition.EffectiveRequirement = effective;

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

	ConfigurationFlagExpression? GetFileRequirement(Definition definition)
	{
		if (currentModule is null
			|| !currentModule.DefinitionSources.TryGetValue(definition, out TokenSequence? source)
			|| source is null
			|| !currentModule.SourceFiles.TryGetValue(source, out SourceFile? file))
			return null;
		AttributeConstructor? attribute = file.FileMetadataAttributes.FirstOrDefault(static attribute => AttributeNameEquals(attribute.Name, "@require"));
		return attribute?.Requirement;
	}

	static ConfigurationFlagExpression? GetExplicitRequirement(IEnumerable<AttributeConstructor> attributes)
	{
		foreach (AttributeConstructor attribute in attributes)
			if (AttributeNameEquals(attribute.Name, "@require"))
				return attribute.Requirement;
		return null;
	}

	void ValidateRequirementAttributePlacements(Module module)
	{
		foreach (Definition definition in module.Definitions)
			ValidateRequirementAttributePlacement(definition, topLevel: true, ownerKind: "");
	}

	void ValidateRequirementAttributePlacement(Definition definition, bool topLevel, string ownerKind)
	{
		foreach (AttributeConstructor attribute in definition.Attributes)
		{
			if (!AttributeNameEquals(attribute.Name, "@require"))
				continue;

			if (definition is ParameterDefinition)
				Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@require is not valid on parameters.");
			else if (definition is VariableDefinition && ownerKind == "enum")
				Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@require is not valid on enum values.");
			else if (definition is FunctionDefinition { Modifier: FunctionModifier.Constructor or FunctionModifier.Destructor })
				Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@require is not valid on constructors or destructors.");
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
					Report(GetRange(attribute.SourceSyntax ?? parameter.SourceSyntax), "@require is not valid on generic parameters.");
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
