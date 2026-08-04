using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	const string OverloadDocAttributeName = "@overload";
	const string CategoryDocAttributeName = "@category";

	void ValidateDocAttributePlacements(Module module)
	{
		Dictionary<string, List<(FunctionDefinition Function, AttributeConstructor Attribute)>> overloadDocs = new(StringComparer.Ordinal);

		foreach (Definition definition in module.Definitions)
			ValidateDocAttributePlacement(definition, topLevel: true, ownerKey: "", overloadDocs);

		foreach (List<(FunctionDefinition Function, AttributeConstructor Attribute)> group in overloadDocs.Values.Where(static group => group.Count > 1))
		{
			foreach ((FunctionDefinition function, AttributeConstructor attribute) in group)
				ReportWarning(GetRange(attribute.SourceSyntax ?? function.SourceSyntax), "Only one declaration in an overload group should use @overload.");
		}
	}

	void ValidateDocAttributePlacement(
		Definition definition,
		bool topLevel,
		string ownerKey,
		Dictionary<string, List<(FunctionDefinition Function, AttributeConstructor Attribute)>> overloadDocs)
	{
		ValidateCategoryDocAttribute(definition, topLevel);
		CollectOverloadDocAttribute(definition, ownerKey, overloadDocs);

		switch (definition)
		{
			case ClassDefinition classDefinition:
				string classOwnerKey = GetNestedOwnerKey(ownerKey, classDefinition);
				foreach (FieldDefinition field in classDefinition.Fields)
					ValidateDocAttributePlacement(field, topLevel: false, classOwnerKey, overloadDocs);
				foreach (FunctionDefinition function in classDefinition.Functions)
					ValidateDocAttributePlacement(function, topLevel: false, classOwnerKey, overloadDocs);
				break;
			case StructDefinition structDefinition:
				string structOwnerKey = GetNestedOwnerKey(ownerKey, structDefinition);
				foreach (FieldDefinition field in structDefinition.Fields)
					ValidateDocAttributePlacement(field, topLevel: false, structOwnerKey, overloadDocs);
				foreach (FunctionDefinition function in structDefinition.Functions)
					ValidateDocAttributePlacement(function, topLevel: false, structOwnerKey, overloadDocs);
				break;
			case InterfaceDefinition interfaceDefinition:
				string interfaceOwnerKey = GetNestedOwnerKey(ownerKey, interfaceDefinition);
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					ValidateDocAttributePlacement(function, topLevel: false, interfaceOwnerKey, overloadDocs);
				break;
			case EnumDefinition enumDefinition:
				string enumOwnerKey = GetNestedOwnerKey(ownerKey, enumDefinition);
				foreach (VariableDefinition value in enumDefinition.Values)
					ValidateDocAttributePlacement(value, topLevel: false, enumOwnerKey, overloadDocs);
				foreach (FunctionDefinition function in enumDefinition.Functions)
					ValidateDocAttributePlacement(function, topLevel: false, enumOwnerKey, overloadDocs);
				break;
			case NewtypeDefinition newtypeDefinition:
				string newtypeOwnerKey = GetNestedOwnerKey(ownerKey, newtypeDefinition);
				foreach (ParameterDefinition parameter in newtypeDefinition.Parameters)
					ValidateDocAttributePlacement(parameter, topLevel: false, newtypeOwnerKey, overloadDocs);
				foreach (FieldDefinition field in newtypeDefinition.Fields)
					ValidateDocAttributePlacement(field, topLevel: false, newtypeOwnerKey, overloadDocs);
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					ValidateDocAttributePlacement(function, topLevel: false, newtypeOwnerKey, overloadDocs);
				break;
			case ParamsDefinition paramsDefinition:
				string paramsOwnerKey = GetNestedOwnerKey(ownerKey, paramsDefinition);
				foreach (ParameterDefinition component in paramsDefinition.Components)
					ValidateDocAttributePlacement(component, topLevel: false, paramsOwnerKey, overloadDocs);
				foreach (FunctionDefinition function in paramsDefinition.Functions)
					ValidateDocAttributePlacement(function, topLevel: false, paramsOwnerKey, overloadDocs);
				break;
			case FunctionDefinition functionDefinition:
				string functionOwnerKey = GetNestedOwnerKey(ownerKey, functionDefinition);
				foreach (ParameterDefinition parameter in functionDefinition.Parameters)
					ValidateDocAttributePlacement(parameter, topLevel: false, functionOwnerKey, overloadDocs);
				break;
		}
	}

	void ValidateCategoryDocAttribute(Definition definition, bool topLevel)
	{
		foreach (AttributeConstructor attribute in definition.Attributes)
		{
			if (!AttributeNameEquals(attribute.Name, CategoryDocAttributeName))
				continue;
			if (!topLevel)
				ReportWarning(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@category is used only on top-level declarations.");
		}
	}

	void CollectOverloadDocAttribute(
		Definition definition,
		string ownerKey,
		Dictionary<string, List<(FunctionDefinition Function, AttributeConstructor Attribute)>> overloadDocs)
	{
		if (definition is not FunctionDefinition function)
			return;

		foreach (AttributeConstructor attribute in function.Attributes)
		{
			if (!AttributeNameEquals(attribute.Name, OverloadDocAttributeName))
				continue;
			string key = GetOverloadDocGroupKey(function, ownerKey);
			if (!overloadDocs.TryGetValue(key, out List<(FunctionDefinition Function, AttributeConstructor Attribute)>? group))
			{
				group = [];
				overloadDocs.Add(key, group);
			}
			group.Add((function, attribute));
		}
	}

	static string GetOverloadDocGroupKey(FunctionDefinition function, string ownerKey)
	{
		string owner = function.OutOfScopeOwnerName
			?? function.OutOfScopeOwnerType?.ResolvedType
			?? ownerKey;
		string invokerName = !string.IsNullOrWhiteSpace(function.InvokerName)
			? function.InvokerName
			: SymbolNameService.InvokerName(function).Value;
		return (function.Namespace ?? "") + "\n" + owner + "\n" + invokerName;
	}

	static string GetNestedOwnerKey(string ownerKey, Definition definition)
	{
		if (definition is FunctionDefinition)
			return ownerKey;
		string name = definition.OutOfScopeOwnerName ?? definition.Name;
		if (string.IsNullOrWhiteSpace(name))
			return ownerKey;
		return string.IsNullOrWhiteSpace(ownerKey) ? name : ownerKey + "." + name;
	}

	void ReportWarning(TokenRange? range, string message, string? code = null)
	{
		diagnostics.Add(new AnalysisDiagnostic(range, message, code, DiagnosticSeverity.Warning));
	}
}
