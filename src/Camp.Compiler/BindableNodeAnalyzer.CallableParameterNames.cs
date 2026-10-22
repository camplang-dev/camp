using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	// Names are a source call contract, not part of structural callable identity.
	readonly Dictionary<BindableNode, TypeReference> inferredCallableSourceTypes = [];
	readonly Dictionary<TypeReference, bool> boundCallableReceiverConstness = [];

	List<ParameterDefinition> CreateStructuralInvocationParameters(Expression? target, CallableShape shape)
	{
		List<ParameterDefinition> parameters = CreateStructuralCallableParameters(GetSourceCallableParameterTypes(shape), shape.Kind == "fn" ? 0 : 1);
		if (TryGetCallableTypeReference(GetCallableSourceType(target), out CallableTypeReference? source) && source is not null)
		{
			List<ParameterDefinition> written = source.Parameters.Where(parameter => parameter is not ThisParameterDefinition).ToList();
			if (written.Count == parameters.Count)
				for (int i = 0; i < parameters.Count; i++)
				{
					parameters[i].Name = parameters[i].Symbol = written[i].Name;
					parameters[i].Type = CloneType(written[i].Type);
				}
		}
		return parameters;
	}

	TypeReference? GetCallableSourceType(Expression? expression, int depth = 0)
	{
		if (expression is null || depth > 32) return null;
		if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(expression, rewritten))
			return GetCallableSourceType(rewritten, depth + 1);
		TypeReference? StorageType(BindableNode? node) => node is not null && inferredCallableSourceTypes.TryGetValue(node, out TypeReference? inferred)
			? inferred : node switch
			{
				DeclarationTarget local => local.Type,
				ParameterDefinition parameter => parameter.Type,
				FieldDefinition field => field.Type,
				VariableDefinition variable => variable.Type,
				_ => null
			};
		return expression switch
		{
			MethodReferenceExpression { Candidates.Count: 1 } method => FunctionCallableSourceType(method.Candidates[0], false),
			MemberReferenceExpression { Member: FunctionDefinition function } member => FunctionCallableSourceType(function, true, member.Target?.ResolvedType),
			VariableReferenceExpression variable => StorageType(variable.Variable),
			MemberReferenceExpression member => StorageType(member.Member),
			ParenthesizedExpression parenthesized => GetCallableSourceType(parenthesized.Expression, depth + 1),
			CastExpression cast => cast.Type,
			CallExpression call when callTargets.TryGetValue(call, out FunctionDefinition? function) => function.ReturnType,
			_ => null
		};
	}

	CallableTypeReference FunctionCallableSourceType(FunctionDefinition function, bool bound, string? receiverType = null, bool freezeReceiver = false)
	{
		CallableTypeReference callable = new()
		{
			Kind = bound ? CallableKind.Delegate : CallableKind.Function,
			CallSpec = function.CallSpec,
			ReturnType = CloneType(function.ReturnType)
		};
		foreach (ParameterDefinition parameter in function.Parameters)
			if (!bound || parameter is not ThisParameterDefinition)
				callable.Parameters.Add(CloneParameter(parameter));
		if (bound && receiverType is not null)
		{
			bool isConst = IsConstReceiverType(receiverType) || IsPrimitiveStringType(receiverType)
				|| new TypeShapeParser(receiverType).TryParse(out TypeShape shape) && shape.Kind is TypeShapeKind.Array or TypeShapeKind.FixedArray && shape.Element?.Qualifiers.IsConst == true;
			boundCallableReceiverConstness[callable] = isConst;
			if (freezeReceiver)
			{
				callable.ReturnType = BindReceiverConstOf(callable.ReturnType, isConst);
				foreach (ParameterDefinition parameter in callable.Parameters)
				{
					parameter.Type = BindReceiverConstOf(parameter.Type, isConst);
					parameter.ResolvedType = FormatTypeReference(parameter.Type);
				}
			}
		}
		return callable;
	}

	// Freeze only this declaration's receiver dependence. Nested callable types
	// introduce their own receiver and retain their own signature contract.
	static TypeReference? BindReceiverConstOf(TypeReference? type, bool isConst)
	{
		if (type is null) return null;
		HashSet<BindableNode> visited = [];
		TypeReference Rewrite(TypeReference current)
		{
			if (current is CallableTypeReference or IterTypeReference)
				visited.Add(current);
			if (current is ConstOfTypeReference { AnchorName: "this", Type: not null } dependent)
				return isConst ? new ConstTypeReference { Type = dependent.Type } : dependent.Type;
			return current;
		}
		TypeReference result = Rewrite(CloneType(type)!);
		BindableNodeTraversal.RewriteChildren(result, static expression => expression, Rewrite, visited);
		return result;
	}
}
