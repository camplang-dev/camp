using System.Collections.Generic;

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
		foreach (BindableNode child in BindableNodeTraversal.Children(node))
			FillMissingResolvedTypes(child, visited);
	}

	void ApplyNodeRewrites(BindableNode node)
	{
		ApplyNodeRewrites(node, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance));
	}

	void ApplyNodeRewrites(BindableNode node, HashSet<BindableNode> visited)
	{
		BindableNodeTraversal.RewriteChildren(node, RewriteExpression, RewriteTypeReference, visited);
	}

	Expression RewriteExpression(Expression expression)
	{
		return expressionRewrites.TryGetValue(expression, out Expression? rewritten) ? rewritten : expression;
	}

	TypeReference RewriteTypeReference(TypeReference type)
	{
		return typeRewrites.TryGetValue(type, out TypeReference? rewritten) ? rewritten : type;
	}

}
