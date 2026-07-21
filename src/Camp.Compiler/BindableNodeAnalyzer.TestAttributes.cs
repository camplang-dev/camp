using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	const string TestAttributeName = "@test";
	const string TestOnlyAttributeName = "@testonly";
	const string SkipAttributeName = "@skip";

	void ValidateTestAttributePlacements(Module module)
	{
		foreach (Definition definition in module.Definitions)
			ValidateTestAttributePlacement(definition, topLevel: true);
	}

	void ValidateTestAttributePlacement(Definition definition, bool topLevel)
	{
		ValidateTestAttribute(definition, topLevel);
		ValidateTestOnlyAttribute(definition, topLevel);
		ValidateSkipAttribute(definition);

		switch (definition)
		{
			case ClassDefinition classDefinition:
				foreach (GenericParameter parameter in classDefinition.GenericParameters)
					ValidateTestAttributesNotOnGenericParameter(parameter);
				foreach (FieldDefinition field in classDefinition.Fields)
					ValidateTestAttributePlacement(field, topLevel: false);
				foreach (FunctionDefinition function in classDefinition.Functions)
					ValidateTestAttributePlacement(function, topLevel: false);
				break;

			case StructDefinition structDefinition:
				foreach (GenericParameter parameter in structDefinition.GenericParameters)
					ValidateTestAttributesNotOnGenericParameter(parameter);
				foreach (FieldDefinition field in structDefinition.Fields)
					ValidateTestAttributePlacement(field, topLevel: false);
				foreach (FunctionDefinition function in structDefinition.Functions)
					ValidateTestAttributePlacement(function, topLevel: false);
				break;

			case InterfaceDefinition interfaceDefinition:
				foreach (GenericParameter parameter in interfaceDefinition.GenericParameters)
					ValidateTestAttributesNotOnGenericParameter(parameter);
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					ValidateTestAttributePlacement(function, topLevel: false);
				break;

			case EnumDefinition enumDefinition:
				foreach (VariableDefinition value in enumDefinition.Values)
					ValidateTestAttributePlacement(value, topLevel: false);
				foreach (FunctionDefinition function in enumDefinition.Functions)
					ValidateTestAttributePlacement(function, topLevel: false);
				break;

			case NewtypeDefinition newtypeDefinition:
				foreach (GenericParameter parameter in newtypeDefinition.GenericParameters)
					ValidateTestAttributesNotOnGenericParameter(parameter);
				foreach (ParameterDefinition parameter in newtypeDefinition.Parameters)
					ValidateTestAttributePlacement(parameter, topLevel: false);
				foreach (FieldDefinition field in newtypeDefinition.Fields)
					ValidateTestAttributePlacement(field, topLevel: false);
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					ValidateTestAttributePlacement(function, topLevel: false);
				break;

			case ParamsDefinition paramsDefinition:
				foreach (GenericParameter parameter in paramsDefinition.GenericParameters)
					ValidateTestAttributesNotOnGenericParameter(parameter);
				foreach (ParameterDefinition component in paramsDefinition.Components)
					ValidateTestAttributePlacement(component, topLevel: false);
				foreach (FunctionDefinition function in paramsDefinition.Functions)
					ValidateTestAttributePlacement(function, topLevel: false);
				break;

			case FunctionDefinition functionDefinition:
				foreach (GenericParameter parameter in functionDefinition.GenericParameters)
					ValidateTestAttributesNotOnGenericParameter(parameter);
				foreach (ParameterDefinition parameter in functionDefinition.Parameters)
					ValidateTestAttributePlacement(parameter, topLevel: false);
				break;
		}
	}

	void ValidateTestAttributesNotOnGenericParameter(GenericParameter parameter)
	{
		foreach (string attributeName in TestAttributeNames())
		{
			if (TryGetAttribute(parameter.Attributes, attributeName, out AttributeConstructor? attribute) && attribute is not null)
				Report(GetRange(attribute.SourceSyntax ?? parameter.SourceSyntax), $"{attributeName} is not valid on generic parameters.");
		}
	}

	void ValidateTestAttribute(Definition definition, bool topLevel)
	{
		if (!TryGetAttribute(definition.Attributes, TestAttributeName, out AttributeConstructor? attribute) || attribute is null)
			return;

		ValidateNoArguments(attribute, definition, TestAttributeName);

		if (definition is not FunctionDefinition)
		{
			Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@test is valid only on top-level functions.");
			return;
		}

		if (!topLevel || definition.OutOfScopeOwnerName is not null)
			Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@test is valid only on top-level functions, not methods or out-of-scope static members.");

		if (HasVisibilityModifier(definition))
			Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@test functions may not declare a visibility modifier.");

		if (HasAttribute(definition.Attributes, TestOnlyAttributeName))
			Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@test may not be combined with @testonly.");
	}

	void ValidateTestOnlyAttribute(Definition definition, bool topLevel)
	{
		if (!TryGetAttribute(definition.Attributes, TestOnlyAttributeName, out AttributeConstructor? attribute) || attribute is null)
			return;

		ValidateNoArguments(attribute, definition, TestOnlyAttributeName);

		if (!topLevel || definition.OutOfScopeOwnerName is not null)
			Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@testonly is valid only on top-level declarations.");

		if (definition.Public is not null || definition.Export is not null)
			Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@testonly declarations may be private or internal, not public or export.");
	}

	void ValidateSkipAttribute(Definition definition)
	{
		if (!TryGetAttribute(definition.Attributes, SkipAttributeName, out AttributeConstructor? attribute) || attribute is null)
			return;

		if (!HasAttribute(definition.Attributes, TestAttributeName))
			Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@skip is valid only on @test declarations.");

		if (attribute.Arguments.Count > 1)
			Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@skip accepts at most one string reason.");

		foreach (ArgumentExpression argument in attribute.Arguments)
		{
			if (!string.IsNullOrWhiteSpace(argument.Name))
				Report(GetTestAttributeArgumentRange(argument, attribute, definition), "@skip does not accept named arguments.");
			if (argument.Value is not LiteralExpression { Kind: LiteralKind.String })
				Report(GetTestAttributeArgumentRange(argument, attribute, definition), "@skip reason must be a string literal.");
		}
	}

	void ValidateNoArguments(AttributeConstructor attribute, Definition definition, string attributeName)
	{
		if (attribute.Arguments.Count == 0)
			return;
		Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), $"{attributeName} accepts no arguments.");
	}

	TokenRange? GetTestAttributeArgumentRange(ArgumentExpression argument, AttributeConstructor attribute, Definition definition)
	{
		return GetRange(argument.SourceSyntax) ?? GetRange(argument.Value?.SourceSyntax) ?? GetRange(attribute.SourceSyntax ?? definition.SourceSyntax);
	}

	static bool HasVisibilityModifier(Definition definition)
	{
		return definition.Export is not null || definition.Public is not null || definition.Internal is not null;
	}

	static bool TryGetAttribute(IReadOnlyList<AttributeConstructor> attributes, string name, out AttributeConstructor? result)
	{
		foreach (AttributeConstructor attribute in attributes)
		{
			if (AttributeNameEquals(attribute.Name, name))
			{
				result = attribute;
				return true;
			}
		}
		result = null;
		return false;
	}

	static IEnumerable<string> TestAttributeNames()
	{
		yield return TestAttributeName;
		yield return TestOnlyAttributeName;
		yield return SkipAttributeName;
	}
}
