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
				if (within.Expression is null)
					return within.Context;
				Expression? previousWithinContext = currentWithinContext;
				currentWithinContext = within.Context;
				Expression? lowered = LowerExpression(within.Expression);
				currentWithinContext = previousWithinContext;
				return lowered ?? within.Expression;

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

			case SizeOfExpression sizeOf:
				return LowerSizeOfExpression(sizeOf);

			case CurrentAllocatorExpression currentAllocator:
				return CurrentAllocator(currentAllocator.SourceSyntax);

			case LambdaExpression lambda:
				lambda.Body = RewriteFunctionBody(lambda.Body);
				break;

			case ArgumentExpression argument:
				return LowerArgument(argument);

			case CallExpression call:
				if (call.Target is MemberReferenceExpression callMemberTarget)
				{
					callMemberTarget.Target = LowerExpression(callMemberTarget.Target);
					if (TryCreateParamsMemberComponentExpression(callMemberTarget, out Expression componentTarget))
						call.Target = componentTarget;
				}
				else
					call.Target = LowerExpression(call.Target);
				LowerThrowingArguments(call);
				AddImplicitSizeOfArguments(call);
				AddImplicitWithinArgument(call);
				for (int i = 0; i < call.Arguments.Count; i++)
					call.Arguments[i] = LowerArgument(call.Arguments[i]);
				if (TryRewriteInstanceInvocation(call))
					return LowerUncaughtThrowingCall(call);
				TryRewriteDelegateInvocation(call);
				ExpandParamsArguments(call.Arguments);
				LowerCallArgumentConversions(call);
				LowerInterfaceCall(call);
				return LowerUncaughtThrowingCall(call);

			case IndexExpression index:
				if (index.Target is MemberReferenceExpression getter && IsPropertyGetterReference(getter))
					return RewritePropertyGetterCall(getter, index.Arguments);
				if (TryCreateParamsComponentExpressions(index, out List<Expression> indexedComponents) && indexedComponents.Count == 1)
					return LowerExpression(indexedComponents[0]);
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
				if (IsDiscardExpression(assignment.Target))
				{
					assignment.Value = LowerExpression(assignment.Value);
					assignment.Target = CreateDiscardReference(assignment.Value?.ResolvedType ?? assignment.Target?.ResolvedType ?? ErrorType, assignment.Target?.SourceSyntax);
					break;
				}
				if (TryRewritePropertySetterAssignment(assignment, out Expression? setterCall))
					return setterCall;
				if (TryRewriteInitAssignment(assignment, out Expression? initCall))
					return initCall;
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

	bool TryRewriteInitAssignment(AssignmentExpression assignment, out Expression? expression)
	{
		expression = null;
		if (assignment.Operator != AssignmentOperator.Assign
			|| assignment.Target is null
			|| assignment.Value is not ConstructionExpression { Kind: ConstructionKind.Init } construction)
			return false;

		for (int i = 0; i < construction.Arguments.Count; i++)
			construction.Arguments[i] = LowerArgument(construction.Arguments[i]);
		LowerInitializer(construction.Initializer);

		Expression target = LowerExpression(assignment.Target) ?? assignment.Target;
		Expression targetAddress = new UnaryExpression
		{
			SourceSyntax = assignment.Target.SourceSyntax,
			Operator = UnaryOperator.AddressOf,
			Operand = target,
			ResolvedType = AddPointer(target.ResolvedType ?? ErrorType)
		};
		CallExpression? initCall = CreateInitCallForConstruction(construction, targetAddress);
		if (initCall is null)
			return false;

		if (typeDefinitions.TryGetValue(BaseConstructedType(target.ResolvedType), out TypeDefinition? definition)
			&& CreateVirtualTableAssignment(target, definition) is Expression vtableAssignment)
		{
			GroupedExpression grouped = new()
			{
				SourceSyntax = assignment.SourceSyntax,
				ResolvedType = "void"
			};
			grouped.Items.Add(new GroupedExpressionItem
			{
				Expression = vtableAssignment,
				ResolvedType = "void"
			});
			grouped.Items.Add(new GroupedExpressionItem
			{
				Expression = initCall,
				ResolvedType = "void"
			});
			expression = grouped;
			return true;
		}

		expression = initCall;
		return true;
	}

	bool TryRewriteDelegateInvocation(CallExpression call)
	{
		if (!TryCreateParamsComponentExpressions(call.Target, out List<Expression> components) || components.Count != 2)
			return false;
		if (!TryGetCallableShape(components[0].ResolvedType, out CallableShape callable) || callable.Kind != "fn")
			return false;

		call.Target = components[0];
		call.Arguments.Insert(0, new ArgumentExpression
		{
			SourceSyntax = components[1].SourceSyntax,
			Value = components[1],
			ResolvedType = components[1].ResolvedType
		});
		return true;
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
