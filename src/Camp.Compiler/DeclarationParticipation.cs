using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Camp.Compiler;

public sealed class DeclarationParticipation
{
	readonly Module module;
	readonly Dictionary<Definition, bool> testOnly = new(ReferenceEqualityComparer.Instance);
	readonly Dictionary<string, TypeDefinition> typeDefinitions = new(StringComparer.Ordinal);

	public DeclarationParticipation(Module module)
	{
		this.module = module;
		foreach (Definition definition in module.Definitions)
		{
			if (definition is TypeDefinition typeDefinition && !string.IsNullOrWhiteSpace(typeDefinition.Name))
				typeDefinitions.TryAdd(typeDefinition.Name, typeDefinition);
			IndexDefinition(definition, inheritedTestOnly: false);
		}
	}

	public static IEnumerable<Definition> ActiveTopLevelDefinitions(Module module)
	{
		return TopLevelDefinitions(module, module.DeclarationParticipationMode);
	}

	public static IEnumerable<Definition> TopLevelDefinitions(Module module, DeclarationParticipationMode mode)
	{
		DeclarationParticipation participation = new(module);
		foreach (Definition definition in module.Definitions)
			if (participation.Includes(definition, mode))
				yield return definition;
	}

	public static bool Includes(Definition definition, Module module)
	{
		return new DeclarationParticipation(module).Includes(definition, module.DeclarationParticipationMode);
	}

	public bool Includes(Definition definition, DeclarationParticipationMode mode)
	{
		return mode == DeclarationParticipationMode.TestModule || !IsTestOnly(definition);
	}

	public bool IsTestOnly(Definition definition)
	{
		if (testOnly.TryGetValue(definition, out bool value))
			return value;
		bool generatedTestOnly = definition.GeneratedInfo?.Source is Definition source && IsTestOnly(source);
		testOnly[definition] = generatedTestOnly;
		return generatedTestOnly;
	}

	public static bool IsTest(Definition definition)
	{
		return definition is FunctionDefinition && HasAttribute(definition.Attributes, "test");
	}

	public static bool HasExplicitTestOnly(Definition definition)
	{
		return HasAttribute(definition.Attributes, "testonly");
	}

	public IReadOnlyList<AnalysisDiagnostic> ValidateProductionDependencies(IReadOnlyDictionary<CallExpression, FunctionDefinition>? callTargets = null)
	{
		List<AnalysisDiagnostic> diagnostics = [];
		foreach (Definition definition in module.Definitions)
		{
			if (IsTestOnly(definition))
				continue;
			foreach (Definition dependency in FindDefinitionDependencies(definition, callTargets))
			{
				if (ReferenceEquals(definition, dependency) || !IsTestOnly(dependency))
					continue;
				diagnostics.Add(new AnalysisDiagnostic(GetDiagnosticRange(definition), $"Production declaration '{definition.Name}' cannot depend on test-only declaration '{dependency.Name}'."));
				break;
			}
		}
		return diagnostics;
	}

	void IndexDefinition(Definition definition, bool inheritedTestOnly)
	{
		bool currentTestOnly = inheritedTestOnly || IsTest(definition) || HasExplicitTestOnly(definition);
		testOnly[definition] = currentTestOnly;
		bool childInheritedTestOnly = currentTestOnly && definition is TypeDefinition;
		foreach (Definition child in GetChildDefinitions(definition))
			IndexDefinition(child, childInheritedTestOnly);
	}

	static IEnumerable<Definition> GetChildDefinitions(Definition definition)
	{
		switch (definition)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
					yield return field;
				foreach (FunctionDefinition function in classDefinition.Functions)
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

	IEnumerable<Definition> FindDefinitionDependencies(Definition definition, IReadOnlyDictionary<CallExpression, FunctionDefinition>? callTargets)
	{
		foreach (BindableNode node in EnumerateNodes(definition))
		{
				switch (node)
				{
					case AliasDefinition { TargetKind: AliasTargetKind.Type } alias when TryFindTypeDefinition(alias.ResolvedTargetName, out TypeDefinition? dependency):
						yield return dependency!;
						break;
					case NamedTypeReference named when TryFindTypeDefinition(named.ResolvedType ?? named.Name, out TypeDefinition? dependency):
						yield return dependency!;
						break;
					case TypeDefinitionReference { Definition: Definition dependency }:
						yield return dependency;
						break;
					case TypeDefinitionReference typeReference when TryFindTypeDefinition(typeReference.ResolvedType ?? typeReference.Name, out TypeDefinition? dependency):
						yield return dependency!;
						break;
				case ClassTypeReference { Definition: Definition dependency }:
					yield return dependency;
					break;
				case CallExpression call when callTargets is not null && callTargets.TryGetValue(call, out FunctionDefinition? dependency):
					yield return dependency;
					break;
				case VariableReferenceExpression { Variable: Definition dependency }:
					yield return dependency;
					break;
				case MemberReferenceExpression memberReference:
					if (memberReference.Member is Definition member)
						yield return member;
					foreach (FunctionDefinition candidate in memberReference.Candidates)
						yield return candidate;
					break;
				case MethodReferenceExpression methodReference:
					foreach (FunctionDefinition candidate in methodReference.Candidates)
						yield return candidate;
					break;
			}
		}
	}

	bool TryFindTypeDefinition(string? type, out TypeDefinition? definition)
	{
		definition = null;
		if (string.IsNullOrWhiteSpace(type))
			return false;
		return typeDefinitions.TryGetValue(BaseTypeName(type!), out definition);
	}

	static IEnumerable<BindableNode> EnumerateNodes(BindableNode root)
	{
		HashSet<BindableNode> visited = new(ReferenceEqualityComparer.Instance);
		Stack<BindableNode> pending = new();
		pending.Push(root);
		while (pending.Count > 0)
		{
			BindableNode node = pending.Pop();
			if (!visited.Add(node))
				continue;
			yield return node;
			foreach (BindableNode child in GetBindableChildren(node))
				pending.Push(child);
		}
	}

	static IEnumerable<BindableNode> GetBindableChildren(BindableNode node)
	{
		foreach (PropertyInfo property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
		{
			if (property.GetIndexParameters().Length != 0)
				continue;
			if (property.Name is nameof(BindableNode.SourceSyntax) or nameof(BindableNode.Provenance))
				continue;
			object? value = property.GetValue(node);
			switch (value)
			{
				case BindableNode child:
					if (child is not Definition || IsOwnedDefinitionChild(node, child, property.Name))
						yield return child;
					break;
				case IEnumerable enumerable and not string:
					foreach (object? item in enumerable)
						if (item is BindableNode enumerableChild
							&& (enumerableChild is not Definition || IsOwnedDefinitionChild(node, enumerableChild, property.Name)))
							yield return enumerableChild;
					break;
			}
		}
	}

	static bool IsOwnedDefinitionChild(BindableNode node, BindableNode child, string propertyName)
	{
		return node switch
		{
			ClassDefinition { Fields: var fields } when propertyName == nameof(ClassDefinition.Fields) && child is FieldDefinition field => fields.Contains(field),
			ClassDefinition { Functions: var functions } when propertyName == nameof(ClassDefinition.Functions) && child is FunctionDefinition function => functions.Contains(function),
			StructDefinition { Fields: var fields } when propertyName == nameof(StructDefinition.Fields) && child is FieldDefinition field => fields.Contains(field),
			StructDefinition { Functions: var functions } when propertyName == nameof(StructDefinition.Functions) && child is FunctionDefinition function => functions.Contains(function),
			InterfaceDefinition { Functions: var functions } when propertyName == nameof(InterfaceDefinition.Functions) && child is FunctionDefinition function => functions.Contains(function),
			EnumDefinition { Values: var values } when propertyName == nameof(EnumDefinition.Values) && child is VariableDefinition value => values.Contains(value),
			EnumDefinition { Functions: var functions } when propertyName == nameof(EnumDefinition.Functions) && child is FunctionDefinition function => functions.Contains(function),
			NewtypeDefinition { Parameters: var parameters } when propertyName == nameof(NewtypeDefinition.Parameters) && child is ParameterDefinition parameter => parameters.Contains(parameter),
			NewtypeDefinition { Fields: var fields } when propertyName == nameof(NewtypeDefinition.Fields) && child is FieldDefinition field => fields.Contains(field),
			NewtypeDefinition { Functions: var functions } when propertyName == nameof(NewtypeDefinition.Functions) && child is FunctionDefinition function => functions.Contains(function),
			ParamsDefinition { Components: var components } when propertyName == nameof(ParamsDefinition.Components) && child is ParameterDefinition component => components.Contains(component),
			ParamsDefinition { Functions: var functions } when propertyName == nameof(ParamsDefinition.Functions) && child is FunctionDefinition function => functions.Contains(function),
			FunctionDefinition { Parameters: var parameters } when propertyName == nameof(FunctionDefinition.Parameters) && child is ParameterDefinition parameter => parameters.Contains(parameter),
			CallableTypeReference { Parameters: var parameters } when propertyName == nameof(CallableTypeReference.Parameters) && child is ParameterDefinition parameter => parameters.Contains(parameter),
			_ => false
		};
	}

	static TokenRange? GetDiagnosticRange(Definition definition)
	{
		return TryGetDefinitionNameRange(definition, out TokenRange range) ? range : null;
	}

	static bool TryGetDefinitionNameRange(Definition definition, out TokenRange range)
	{
		range = default;
		return definition.SourceSyntax switch
		{
			AliasDeclarationSyntax syntax => Assign(syntax.Identifier?.Range, out range),
			TypeDeclarationSyntax syntax => Assign(syntax.Identifier?.Range, out range),
			MemberDeclarationSyntax syntax => Assign(syntax.Identifier?.Range, out range),
			EnumValueSyntax syntax => Assign(syntax.Identifier?.Range, out range),
			_ => false
		};
	}

	static bool Assign(TokenRange? value, out TokenRange range)
	{
		if (value is TokenRange tokenRange)
		{
			range = tokenRange;
			return true;
		}
		range = default;
		return false;
	}

	static bool HasAttribute(IReadOnlyList<AttributeConstructor> attributes, string name)
	{
		return attributes.Any(attribute => AttributeNameEquals(attribute.Name, name));
	}

	static bool AttributeNameEquals(string actual, string expected)
	{
		return actual == expected || actual.TrimStart('@') == expected.TrimStart('@');
	}

	static string BaseTypeName(string type)
	{
		type = type.Trim()
			.Replace("const ", "", StringComparison.Ordinal)
			.Replace("escaped ", "", StringComparison.Ordinal)
			.Replace("scoped ", "", StringComparison.Ordinal)
			.Replace("unscoped ", "", StringComparison.Ordinal)
			.Trim();

		int shapeStart = type.LastIndexOf('(');
		if (shapeStart >= 0)
			type = type[(shapeStart + 1)..].TrimEnd(')', ' ');

		int genericStart = type.IndexOf('<', StringComparison.Ordinal);
		if (genericStart >= 0)
			type = type[..genericStart];

		while (type.EndsWith("*", StringComparison.Ordinal) || type.EndsWith("[]", StringComparison.Ordinal) || type.EndsWith("?", StringComparison.Ordinal))
		{
			if (type.EndsWith("[]", StringComparison.Ordinal))
				type = type[..^2].Trim();
			else
				type = type[..^1].Trim();
		}

		int namespaceStart = type.LastIndexOf("::", StringComparison.Ordinal);
		if (namespaceStart >= 0)
			type = type[(namespaceStart + 2)..];
		return type;
	}
}
