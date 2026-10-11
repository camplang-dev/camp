using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	bool TryGetCallableInvocationShape(string? type, out CallableShape shape)
	{
		if (TryGetOptionalPayloadType(type, out string payload)
			&& TryGetCallableShape(payload, out shape) && shape.Kind is "delegate" or "once" or "async" or "iter")
			return true;
		return TryGetCallableShape(type, out shape);
	}

	bool TryGetOptionalPayloadType(string? type, out string payload)
	{
		payload = "";
		if (!TryParseTypeShape(type, out TypeShape shape) || shape.Kind != TypeShapeKind.Optional || shape.Element is null)
			return false;
		payload = TypeShapeParser.Format(shape.Element);
		return true;
	}

	bool TryGetExpandedMemberType(string type, string name, out string memberType)
	{
		memberType = ErrorType;
		if (name == "value" && TryGetOptionalPayloadType(type, out string payload))
		{
			memberType = payload;
			return true;
		}
		if (!TryGetParamsComponentShape(null, type, "value", out ParamsComponentShape shape)
			|| FindParamsComponent(shape, name) is not ParamsComponent component)
			return false;
		memberType = component.Type;
		return true;
	}

	bool TryCreateProjectedMemberComponents(Expression? target, string name, out List<Expression> components)
	{
		components = [];
		if (target is null)
			return false;
		string type = target.ResolvedType ?? ErrorType;
		if (TryGetPointerElementType(type) is string erased && ErasedTypeIdentity.TryGetPayload(erased, out string payload))
		{
			target = new UnaryExpression { SourceSyntax = target.SourceSyntax, Operator = UnaryOperator.PointerDereference,
				Operand = target, ResolvedType = payload };
			type = payload;
		}
		if (!TryGetParamsComponentShape(null, type, "value", out ParamsComponentShape shape))
			return false;
		// Physical carrier fields have already been projected. Keep their accesses
		// intact rather than projecting the generated member back into itself.
		if (TryGetErasedStorageType(target, out _) && shape.Components.Any(component => ExpandedFormService.StorageMemberName(component.Name) == name))
			return false;

		List<string> paths;
		if (name == "value" && TryGetOptionalPayloadType(type, out string valueType)
			&& TryGetParamsComponentShape(null, valueType, "value", out ParamsComponentShape valueShape))
		{
			ParamsComponentShapeKind payloadKind = TryGetPointerElementType(valueType) is null ? valueShape.Kind : ParamsComponentShapeKind.Structural;
			paths = valueShape.Components.Select(component => ExpandedFormService.OptionalPayloadPath(payloadKind, component.Name)).ToList();
		}
		else if (FindParamsComponent(shape, name) is not null)
			paths = [name];
		else
			return false;

		if (!TryCreateParamsComponentExpressions(target, out List<Expression> source) || source.Count != shape.Components.Count)
			return false;
		foreach (string path in paths)
		{
			int index = shape.Components.FindIndex(component => component.Name == path);
			if (index < 0)
				return false;
			components.Add(source[index]);
		}
		return true;
	}

	// Transport one optional layer at a time. An existing optional carries its
	// flags; wrapping it appends only the new outer presence flag.
	bool TryCreateValueParamsComponents(Expression expression, ParamsComponentShape target, out List<Expression> components)
	{
		components = [];
		if (UnwrapParenthesizedExpression(expression) is LiteralExpression { Kind: LiteralKind.Null }
			&& TryGetPointerElementType(target.TypeName) is not null)
		{
			components = target.Components.Select(component => (Expression)new DefaultExpression
			{
				SourceSyntax = expression.SourceSyntax,
				ResolvedType = component.Type
			}).ToList();
			return true;
		}
		if (expression is WithinExpression { Expression: CallExpression call } within
			&& (IsErasedResultCall(call) || TryGetParamsComponentShape(null, call.ResolvedType, "result", out _))
			&& currentStatementPrefix is not null)
			return TryCreateWithinResultComponents(within, call, target, out components);
		if (expression is not DefaultExpression && TryGetOptionalPayloadType(target.TypeName, out string payload)
			&& CanLiftToOptional(expression.ResolvedType ?? ErrorType, target.TypeName))
		{
			if (TryGetParamsComponentShape(null, payload, "value", out ParamsComponentShape payloadShape))
			{
				if (!TryCreateValueParamsComponents(expression, payloadShape, out components))
					return false;
			}
			else
				components.Add(expression);
			components.Add(BoolLiteral(true));
			return components.Count == target.Components.Count;
		}
		return (TryCreateParamsComponentExpressions(expression, out components)
			|| TryCreateDirectFunctionDelegateComponents(expression, target, out components))
			&& components.Count == target.Components.Count;
	}

	bool TryCreateWithinResultComponents(WithinExpression within, CallExpression call, ParamsComponentShape target, out List<Expression> components)
	{
		bool defaultWithin = within.Context is DefaultWithinContextExpression;
		Expression? context = defaultWithin ? null : LowerExpression(within.Context);
		Expression? previousWithinContext = currentWithinContext;
		int previousDefaultWithinContextDepth = currentDefaultWithinContextDepth;
		currentWithinContext = defaultWithin ? null : CaptureWithinContext(context, within.SourceSyntax);
		if (defaultWithin)
			currentDefaultWithinContextDepth++;
		try
		{
			AddImplicitDefaultArguments(call);
			bool expanded = TryCreateValueParamsComponents(call, target, out components);
			if (expanded)
				preparedExpandedReturnCalls.Add(call);
			return expanded;
		}
		finally
		{
			currentWithinContext = previousWithinContext;
			currentDefaultWithinContextDepth = previousDefaultWithinContextDepth;
		}
	}
}
