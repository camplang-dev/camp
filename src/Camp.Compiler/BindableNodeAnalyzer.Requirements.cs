using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
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
}
