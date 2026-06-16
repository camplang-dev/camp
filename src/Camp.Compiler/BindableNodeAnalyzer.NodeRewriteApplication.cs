using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void FillMissingResolvedTypes(BindableNode node)
	{
		FillMissingResolvedTypes(node, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance));
	}

	void FillMissingResolvedTypes(BindableNode node, HashSet<BindableNode> visited)
	{
		if (!visited.Add(node))
			return;

		node.ResolvedType ??= UnresolvedType;

		Type type = node.GetType();
		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.Name is nameof(BindableNode.SourceSyntax) or nameof(BindableNode.ResolvedType) || IsSemanticReferenceProperty(property))
				continue;

			object? value = property.GetValue(node);
			if (value is BindableNode child)
				FillMissingResolvedTypes(child, visited);
			else if (IsListType(property.PropertyType) && value is IEnumerable items)
			{
				foreach (object? item in items)
				{
					if (item is BindableNode childItem)
						FillMissingResolvedTypes(childItem, visited);
				}
			}
		}
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

	void ApplyNodeRewrites(BindableNode node)
	{
		ApplyNodeRewrites(node, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance));
	}

	void ApplyNodeRewrites(BindableNode node, HashSet<BindableNode> visited)
	{
		if (!visited.Add(node))
			return;

		foreach (PropertyInfo property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (property.Name == nameof(BindableNode.SourceSyntax) || property.Name == nameof(BindableNode.ResolvedType) || IsSemanticReferenceProperty(property))
				continue;

			object? value = property.GetValue(node);
			if (value is null)
				continue;

			if (value is Expression expression)
			{
				Expression rewritten = RewriteExpression(expression);
				if (!ReferenceEquals(rewritten, expression) && property.CanWrite)
					property.SetValue(node, rewritten);
				ApplyNodeRewrites(rewritten, visited);
			}
			else if (value is TypeReference type)
			{
				TypeReference rewritten = RewriteTypeReference(type);
				if (!ReferenceEquals(rewritten, type) && property.CanWrite)
					property.SetValue(node, rewritten);
				ApplyNodeRewrites(rewritten, visited);
			}
			else if (value is IList list)
			{
				ApplyListRewrites(list, visited);
			}
			else if (value is BindableNode child)
			{
				ApplyNodeRewrites(child, visited);
			}
		}
	}

	void ApplyListRewrites(IList list, HashSet<BindableNode> visited)
	{
		for (int i = 0; i < list.Count; i++)
		{
			object? item = list[i];
			if (item is null)
				continue;

			if (item is Expression expression)
			{
				Expression rewritten = RewriteExpression(expression);
				if (!ReferenceEquals(rewritten, expression))
					list[i] = rewritten;
				ApplyNodeRewrites(rewritten, visited);
			}
			else if (item is TypeReference type)
			{
				TypeReference rewritten = RewriteTypeReference(type);
				if (!ReferenceEquals(rewritten, type))
					list[i] = rewritten;
				ApplyNodeRewrites(rewritten, visited);
			}
			else if (item is BindableNode child)
			{
				ApplyNodeRewrites(child, visited);
			}
		}
	}

	Expression RewriteExpression(Expression expression)
	{
		return expressionRewrites.TryGetValue(expression, out Expression? rewritten) ? rewritten : expression;
	}

	TypeReference RewriteTypeReference(TypeReference type)
	{
		return typeRewrites.TryGetValue(type, out TypeReference? rewritten) ? rewritten : type;
	}

	static bool IsSemanticReferenceProperty(PropertyInfo property)
	{
		return property.DeclaringType == typeof(VariableReferenceExpression) && property.Name == nameof(VariableReferenceExpression.Variable)
			|| property.DeclaringType == typeof(TypeDefinitionReference) && property.Name == nameof(TypeDefinitionReference.Definition)
			|| property.DeclaringType == typeof(GenericParameterTypeReference) && property.Name == nameof(GenericParameterTypeReference.Parameter)
			|| property.DeclaringType == typeof(GotoStatement) && property.Name == nameof(GotoStatement.Target)
			|| property.DeclaringType == typeof(MethodReferenceExpression) && property.Name == nameof(MethodReferenceExpression.Candidates)
			|| property.DeclaringType == typeof(MemberReferenceExpression) && property.Name == nameof(MemberReferenceExpression.Member)
			|| property.DeclaringType == typeof(MemberReferenceExpression) && property.Name == nameof(MemberReferenceExpression.Candidates);
	}
}
