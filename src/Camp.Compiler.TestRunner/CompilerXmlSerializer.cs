using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Camp.Compiler;

namespace Camp.Compiler.Tests;

public static class CompilerXmlSerializer
{
	public static XElement SerializeSyntax(SyntaxNode syntax, string? elementName = null)
	{
		Type type = syntax.GetType();
		string typeName = GetXmlName(type.Name);
		XElement element = new(elementName ?? typeName);

		if (elementName is not null && elementName != typeName)
			element.SetAttributeValue("Type", typeName);

		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			object? value = property.GetValue(syntax);
			if (value is null)
				continue;

			if (IsTokenType(property.PropertyType) && value is Token token)
				element.SetAttributeValue(property.Name, token.Value);
			else if (IsTokenRangeType(property.PropertyType) && value is TokenRange range)
				element.SetAttributeValue(property.Name, range.Value);
			else if (IsListType(property.PropertyType) && value is IEnumerable items)
				element.Add(SerializeList(property.Name, items));
			else if (value is SyntaxNode childSyntax)
				element.Add(SerializeSyntax(childSyntax, property.Name));
			else
				element.SetAttributeValue(property.Name, Convert.ToString(value, CultureInfo.InvariantCulture));
		}

		return element;
	}

	public static XElement SerializeBindableNode(BindableNode node, string? elementName = null)
	{
		Type type = node.GetType();
		string typeName = GetXmlName(type.Name);
		XElement element = new(elementName ?? typeName);

		if (elementName is not null && elementName != typeName)
			element.SetAttributeValue("Type", typeName);

		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.Name == nameof(BindableNode.SourceSyntax) || IsIgnoredBindableProperty(property))
				continue;

			object? value = property.GetValue(node);
			if (value is null)
				continue;
			if ((property.Name == "InvokerName" || property.Name == "FullCallableName") && value is "")
				continue;

			if (IsSemanticReferenceProperty(property))
				SerializeSemanticReference(element, property.Name, value);
			else if (IsListType(property.PropertyType) && value is IEnumerable items)
			{
				XElement? list = SerializeBindableList(property.Name, items);
				if (list is not null)
					element.Add(list);
			}
			else if (value is BindableNode childNode)
				element.Add(SerializeBindableNode(childNode, property.Name));
			else if ((property.Name == "Modifier" || property.Name == "IteratorKind" || property.Name == "InterfaceSlotInitializerKind" || property.Name == nameof(CallExpression.PreparedMode)) && IsDefaultEnumValue(value))
				continue;
			else if (value is false)
				continue;
			else
				element.SetAttributeValue(property.Name, Convert.ToString(value, CultureInfo.InvariantCulture));
		}

		return element;
	}

	static XElement SerializeList(string name, IEnumerable items)
	{
		XElement element = new(name);

		foreach (object? item in items)
		{
			if (item is null)
				continue;

			switch (item)
			{
				case SyntaxNode syntax:
					element.Add(SerializeSyntax(syntax));
					break;
				case Token token:
					element.Add(new XElement("Token", new XAttribute("Value", token.Value)));
					break;
				case TokenRange range:
					element.Add(new XElement("TokenRange", new XAttribute("Value", range.Value)));
					break;
				default:
					element.Add(new XElement("Value", Convert.ToString(item, CultureInfo.InvariantCulture)));
					break;
			}
		}

		return element;
	}

	static bool IsSemanticReferenceProperty(PropertyInfo property)
	{
		return property.DeclaringType == typeof(VariableReferenceExpression) && property.Name == nameof(VariableReferenceExpression.Variable)
			|| property.DeclaringType == typeof(TypeDefinitionReference) && property.Name == nameof(TypeDefinitionReference.Definition)
			|| property.DeclaringType == typeof(GenericParameterTypeReference) && property.Name == nameof(GenericParameterTypeReference.Parameter)
			|| property.DeclaringType == typeof(SymbolOfExpression) && property.Name == nameof(SymbolOfExpression.Reference)
			|| property.DeclaringType == typeof(GotoStatement) && property.Name == nameof(GotoStatement.Target)
			|| property.DeclaringType == typeof(ForeachStatement) && property.Name == nameof(ForeachStatement.IteratorNext)
			|| property.DeclaringType == typeof(MethodReferenceExpression) && property.Name == nameof(MethodReferenceExpression.Candidates)
			|| property.DeclaringType == typeof(MemberReferenceExpression) && property.Name == nameof(MemberReferenceExpression.Member)
			|| property.DeclaringType == typeof(MemberReferenceExpression) && property.Name == nameof(MemberReferenceExpression.Candidates);
	}

	static bool IsIgnoredBindableProperty(PropertyInfo property)
	{
		return property.DeclaringType == typeof(Module)
			&& property.Name is nameof(Module.DefinitionSources)
				or nameof(Module.SourceFiles)
				or nameof(Module.SourceNamespaces)
				or nameof(Module.SourceWithinAllocationPolicies)
				or nameof(Module.SourcefilePathMode)
				or nameof(Module.SourcefileDefaultRoot)
				or nameof(Module.SourcefileRoots)
				or nameof(Module.DeclarationParticipationMode)
				or nameof(Module.ConfigurationFlags)
			|| property.DeclaringType == typeof(Definition)
				&& property.Name is nameof(Definition.DefaultSymbol)
					or nameof(Definition.EffectiveRequirement);
	}

	static void SerializeSemanticReference(XElement element, string name, object value)
	{
		switch (value)
		{
			case Definition definition:
				element.SetAttributeValue(name, definition.Name);
				break;
			case GenericParameter parameter:
				element.SetAttributeValue(name, parameter.Name);
				break;
			case BindableNode node:
				element.SetAttributeValue(name, GetSemanticReferenceName(node));
				break;
			case System.Collections.Generic.IEnumerable<FunctionDefinition> functions:
			{
				XElement candidates = new(name);
				foreach (FunctionDefinition function in functions)
					candidates.Add(new XElement("Function", new XAttribute("Name", function.Name), new XAttribute("ResolvedType", function.ResolvedType ?? "")));
				if (candidates.HasElements)
					element.Add(candidates);
				break;
			}
		}
	}

	static string GetSemanticReferenceName(BindableNode node)
	{
		return node switch
		{
			ParameterDefinition parameter => parameter.Name,
			Definition definition => definition.Name,
			DeclarationTarget target => string.Join(", ", target.Names),
			LambdaParameter parameter => parameter.Name ?? parameter.Parameter?.Name ?? "",
			LabelStatement label => label.Name ?? "",
			_ => node.GetType().Name
		};
	}

	static XElement? SerializeBindableList(string name, IEnumerable items)
	{
		XElement element = new(name);
		bool hasItems = false;

		foreach (object? item in items)
		{
			if (item is null)
				continue;

			hasItems = true;
			if (item is BindableNode node)
				element.Add(SerializeBindableNode(node));
			else
				element.Add(new XElement("Value", Convert.ToString(item, CultureInfo.InvariantCulture)));
		}

		return hasItems ? element : null;
	}

	static bool IsDefaultEnumValue(object value)
	{
		return value.GetType().IsEnum && Convert.ToInt64(value, CultureInfo.InvariantCulture) == 0;
	}

	static bool IsListType(Type type)
	{
		return type != typeof(string)
			&& type != typeof(Token)
			&& type != typeof(TokenRange)
			&& type != typeof(Token?)
			&& type != typeof(TokenRange?)
			&& typeof(IEnumerable).IsAssignableFrom(type);
	}

	static bool IsTokenType(Type type)
	{
		return type == typeof(Token) || type == typeof(Token?);
	}

	static bool IsTokenRangeType(Type type)
	{
		return type == typeof(TokenRange) || type == typeof(TokenRange?);
	}

	static string GetXmlName(string typeName)
	{
		return typeName.EndsWith("Syntax", StringComparison.Ordinal)
			? typeName[..^"Syntax".Length]
			: typeName;
	}
}
