using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	Expression? LowerExpression(Expression? expression)
	{
		switch (expression)
		{
			case null:
				return null;

			case ConstructionExpression construction:
				for (int i = 0; i < construction.Arguments.Count; i++)
					construction.Arguments[i] = LowerArgument(construction.Arguments[i]);
				construction.ElementCount = LowerExpression(construction.ElementCount);
				LowerInitializer(construction.Initializer);
				return RewriteConstruction(construction);

			case WithinExpression within:
				within.Context = LowerExpression(within.Context);
				within.Expression = LowerExpression(within.Expression);
				return within;

			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
					item.Expression = LowerExpression(item.Expression);
				break;

			case ArrayExpression array:
				for (int i = 0; i < array.Elements.Count; i++)
					array.Elements[i] = LowerExpression(array.Elements[i]) ?? array.Elements[i];
				break;

			case InitializerExpression initializer:
				LowerInitializer(initializer);
				break;

			case ParenthesizedExpression parenthesized:
				parenthesized.Expression = LowerExpression(parenthesized.Expression);
				break;

			case CastExpression cast:
				cast.Expression = LowerExpression(cast.Expression);
				if (IsInterfacePointerType(cast.Type))
					return LowerInterfaceConversion(cast.Type, cast.Expression) ?? cast;
				break;

			case LambdaExpression lambda:
				lambda.Body = RewriteFunctionBody(lambda.Body);
				break;

			case ArgumentExpression argument:
				return LowerArgument(argument);

			case CallExpression call:
				if (call.Target is MemberReferenceExpression callMemberTarget)
					callMemberTarget.Target = LowerExpression(callMemberTarget.Target);
				else
					call.Target = LowerExpression(call.Target);
				LowerThrowingArguments(call);
				for (int i = 0; i < call.Arguments.Count; i++)
					call.Arguments[i] = LowerArgument(call.Arguments[i]);
				ExpandParamsArguments(call.Arguments);
				LowerCallArgumentConversions(call);
				if (TryRewriteInstanceInvocation(call))
					return LowerUncaughtThrowingCall(call);
				LowerInterfaceCall(call);
				return LowerUncaughtThrowingCall(call);

			case IndexExpression index:
				if (index.Target is MemberReferenceExpression getter && IsPropertyGetterReference(getter))
					return RewritePropertyGetterCall(getter, index.Arguments);
				index.Target = LowerExpression(index.Target);
				for (int i = 0; i < index.Arguments.Count; i++)
					index.Arguments[i] = LowerArgument(index.Arguments[i]);
				break;

			case MemberExpression member:
				member.Target = LowerExpression(member.Target);
				break;

			case MemberReferenceExpression memberReference:
				memberReference.Target = LowerExpression(memberReference.Target);
				if (TryCreateParamsMemberComponentExpression(memberReference, out Expression paramsComponent))
					return LowerExpression(paramsComponent);
				if (IsPropertyGetterReference(memberReference))
					return RewritePropertyGetterCall(memberReference, []);
				if (memberReference is { Target: not null, Member: FunctionDefinition function } && FindContainingType(function) is not InterfaceDefinition)
					return RewriteInstanceMethodDelegate(memberReference);
				break;

			case NamelessIndexerExpression nameless:
				nameless.Target = LowerExpression(nameless.Target);
				for (int i = 0; i < nameless.Arguments.Count; i++)
					nameless.Arguments[i] = LowerArgument(nameless.Arguments[i]);
				break;

			case UnaryExpression unary:
				unary.Context = LowerExpression(unary.Context);
				unary.Operand = LowerExpression(unary.Operand);
				break;

			case PostfixUpdateExpression postfix:
				postfix.Expression = LowerExpression(postfix.Expression);
				break;

			case FinallyDeleteExpression finallyDelete:
				return RewriteFinallyDeleteExpression(finallyDelete);

			case BinaryExpression binary:
				binary.Left = LowerExpression(binary.Left);
				binary.Right = LowerExpression(binary.Right);
				break;

			case AssignmentExpression assignment:
				if (TryRewritePropertySetterAssignment(assignment, out Expression? setterCall))
					return setterCall;
				assignment.Target = LowerExpression(assignment.Target);
				assignment.Value = LowerExpression(assignment.Value);
				if (assignment.Target is VariableReferenceExpression { Variable: DeclarationTarget target })
					assignment.Value = LowerInterfaceConversion(target.Type, assignment.Value);
				break;

			case ConditionalExpression conditional:
				conditional.Condition = LowerExpression(conditional.Condition);
				conditional.WhenTrue = LowerExpression(conditional.WhenTrue);
				conditional.WhenFalse = LowerExpression(conditional.WhenFalse);
				break;

			case RangeExpression range:
				range.Start = LowerExpression(range.Start);
				range.End = LowerExpression(range.End);
				break;
		}

		return expression;
	}

	bool TryRewritePropertySetterAssignment(AssignmentExpression assignment, out Expression? rewritten)
	{
		rewritten = null;
		switch (assignment.Target)
		{
			case MemberReferenceExpression setter when IsPropertySetterReference(setter):
				rewritten = RewritePropertySetterCall(setter, [], assignment.Value);
				return true;

			case IndexExpression { Target: MemberReferenceExpression setter } index when IsPropertySetterReference(setter):
				rewritten = RewritePropertySetterCall(setter, index.Arguments, assignment.Value);
				return true;

			default:
				return false;
		}
	}

}
