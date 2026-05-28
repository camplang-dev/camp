using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void ExpandParamsArguments(List<ArgumentExpression> arguments)
	{
		for (int i = 0; i < arguments.Count; i++)
		{
			ArgumentExpression argument = arguments[i];
			if (!TryCreateParamsComponentExpressions(argument.Value, out List<Expression> components))
				continue;

			arguments.RemoveAt(i);
			for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
			{
				Expression component = components[componentIndex];
				arguments.Insert(i + componentIndex, new ArgumentExpression
				{
					SourceSyntax = argument.SourceSyntax,
					Modifier = argument.Modifier,
					Value = component,
					ResolvedType = component.ResolvedType
				});
			}
			i += components.Count - 1;
		}
	}

	bool TryRewriteParamsAssignment(AssignmentExpression assignment, out List<Statement> statements)
	{
		statements = [];
		if (assignment.Operator != AssignmentOperator.Assign)
			return false;
		if (!TryCreateParamsComponentExpressions(assignment.Target, out List<Expression> targets))
			return false;
		if (!TryCreateParamsComponentExpressions(assignment.Value, out List<Expression> values))
			return false;
		if (targets.Count != values.Count)
			return false;

		for (int i = 0; i < targets.Count; i++)
		{
			statements.Add(new ExpressionStatement
			{
				SourceSyntax = assignment.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = assignment.SourceSyntax,
					Target = LowerExpression(targets[i]),
					Operator = AssignmentOperator.Assign,
					Value = LowerExpression(values[i]),
					ResolvedType = targets[i].ResolvedType
				}
			});
		}

		return statements.Count > 0;
	}

	bool TryCreateParamsComponentExpressions(Expression? expression, out List<Expression> components)
	{
		components = [];
		switch (expression)
		{
			case null:
				return false;

			case ParenthesizedExpression parenthesized:
				return TryCreateParamsComponentExpressions(parenthesized.Expression, out components);

			case VariableReferenceExpression { Variable: not null } variable
				when paramsExpansions.TryGetValue(variable.Variable, out List<ParamsExpansionComponent>? expansion):
				foreach (ParamsExpansionComponent component in expansion)
					components.Add(CreateVariableReference(component.Node, component.Type));
				return components.Count > 0;

			case MemberReferenceExpression { Member: not null } member
				when paramsExpansions.TryGetValue(member.Member, out List<ParamsExpansionComponent>? expansion):
				foreach (ParamsExpansionComponent component in expansion)
				{
					components.Add(new MemberReferenceExpression
					{
						SourceSyntax = member.SourceSyntax,
						Target = CloneParamsExpansionExpression(member.Target),
						Name = component.Name,
						Member = component.Node,
						ResolvedType = component.Type
					});
				}
				return components.Count > 0;

			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
				{
					if (item.Expression is null)
					{
						components.Clear();
						return false;
					}
					components.Add(item.Expression);
				}
				return components.Count > 0;

			case UnaryExpression { Operator: UnaryOperator.AddressOf } addressOf
				when TryCreateParamsComponentExpressions(addressOf.Operand, out List<Expression> addressed):
				foreach (Expression component in addressed)
				{
					components.Add(new UnaryExpression
					{
						SourceSyntax = addressOf.SourceSyntax,
						Operator = UnaryOperator.AddressOf,
						Operand = component,
						Context = CloneParamsExpansionExpression(addressOf.Context),
						ResolvedType = AddPointer(component.ResolvedType ?? ErrorType)
					});
				}
				return components.Count > 0;

			case UnaryExpression { Operator: UnaryOperator.PointerDereference } dereference
				when TryCreateParamsComponentExpressions(dereference.Operand, out List<Expression> dereferenced):
				foreach (Expression component in dereferenced)
				{
					components.Add(new UnaryExpression
					{
						SourceSyntax = dereference.SourceSyntax,
						Operator = UnaryOperator.PointerDereference,
						Operand = component,
						Context = CloneParamsExpansionExpression(dereference.Context),
						ResolvedType = TryGetPointerElementType(component.ResolvedType) ?? ErrorType
					});
				}
				return components.Count > 0;

			default:
				return false;
		}
	}

	Expression? CloneParamsExpansionExpression(Expression? expression)
	{
		return expression switch
		{
			null => null,
			LiteralExpression literal => new LiteralExpression { SourceSyntax = literal.SourceSyntax, Kind = literal.Kind, Text = literal.Text, Value = literal.Value, ResolvedType = literal.ResolvedType },
			NamedExpression named => CloneNamedExpression(named),
			VariableReferenceExpression variable => new VariableReferenceExpression { SourceSyntax = variable.SourceSyntax, Variable = variable.Variable, ResolvedType = variable.ResolvedType },
			ThisExpression thisExpression => new ThisExpression { SourceSyntax = thisExpression.SourceSyntax, ResolvedType = thisExpression.ResolvedType },
			MemberReferenceExpression member => new MemberReferenceExpression
			{
				SourceSyntax = member.SourceSyntax,
				Target = CloneParamsExpansionExpression(member.Target),
				Name = member.Name,
				Member = member.Member,
				ResolvedType = member.ResolvedType
			},
			MemberExpression member => new MemberExpression
			{
				SourceSyntax = member.SourceSyntax,
				Target = CloneParamsExpansionExpression(member.Target),
				Name = member.Name,
				ResolvedType = member.ResolvedType
			},
			ParenthesizedExpression parenthesized => new ParenthesizedExpression
			{
				SourceSyntax = parenthesized.SourceSyntax,
				Expression = CloneParamsExpansionExpression(parenthesized.Expression),
				ResolvedType = parenthesized.ResolvedType
			},
			_ => expression
		};
	}

	static NamedExpression CloneNamedExpression(NamedExpression named)
	{
		NamedExpression clone = new()
		{
			SourceSyntax = named.SourceSyntax,
			Name = named.Name,
			ResolvedType = named.ResolvedType
		};
		clone.Qualifiers.AddRange(named.Qualifiers);
		return clone;
	}
}
