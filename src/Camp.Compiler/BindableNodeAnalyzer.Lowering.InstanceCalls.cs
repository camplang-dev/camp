using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	bool TryRewriteInstanceInvocation(CallExpression call)
	{
		if (call.Target is not MemberReferenceExpression { Target: Expression receiver, Member: FunctionDefinition function } member)
			return false;
		if (!IsInstanceFunction(function))
			return false;
		if (IsPropertyGetterReference(member) || IsPropertySetterReference(member))
			return false;
		if (FindContainingType(function) is InterfaceDefinition)
			return false;

		RewriteInstanceInvocation(call, member, receiver, function);
		return true;
	}

	bool TryRewriteStaticMemberInvocation(CallExpression call)
	{
		if (call.Target is not MemberReferenceExpression { Member: FunctionDefinition function } member)
			return false;
		if (IsInstanceFunction(function))
			return false;
		if (IsPropertyGetterReference(member) || IsPropertySetterReference(member))
			return false;

		MethodReferenceExpression reference = new()
		{
			SourceSyntax = member.SourceSyntax,
			ResolvedType = BuildFunctionValueType(function, isInstance: false)
		};
		reference.Candidates.Add(function);
		call.Target = reference;
		return true;
	}

	bool ShouldEmitFlattenedInstanceCalls()
	{
		return currentRewriteFunction is not null;
	}

	void RewriteInstanceInvocation(CallExpression call, MemberReferenceExpression member, Expression receiver, FunctionDefinition function)
	{
		call.Target = CreateFlattenedMethodReference(member, receiver, function);
		call.Arguments.Insert(0, CreateReceiverArgument(receiver, function));
	}

	Expression RewriteInstanceMethodDelegate(MemberReferenceExpression member)
	{
		FunctionDefinition function = (FunctionDefinition)member.Member!;
		Expression receiver = member.Target!;
		GroupedExpression grouped = new()
		{
			SourceSyntax = member.SourceSyntax,
			ResolvedType = member.ResolvedType
		};
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = CreateFlattenedMethodReference(member, receiver, function),
			ResolvedType = BuildFlattenedFunctionValueType(function, BuildFlattenedReceiverType(function, receiver.ResolvedType ?? ErrorType))
		});
		ArgumentExpression receiverArgument = CreateReceiverArgument(receiver, function);
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = receiverArgument.Value,
			ResolvedType = receiverArgument.ResolvedType
		});
		return grouped;
	}

	MethodReferenceExpression CreateFlattenedMethodReference(MemberReferenceExpression member, Expression receiver, FunctionDefinition function)
	{
		EnsureFlattenedFunctionSymbol(function);
		MethodReferenceExpression reference = new()
		{
			SourceSyntax = member.SourceSyntax,
			ResolvedType = BuildFlattenedFunctionValueType(function, BuildFlattenedReceiverType(function, receiver.ResolvedType ?? ErrorType))
		};
		reference.Candidates.Add(function);
		return reference;
	}

	ArgumentExpression CreateReceiverArgument(Expression receiver, FunctionDefinition function)
	{
		Expression value = receiver;
		string receiverValueType = GetReceiverValueType(receiver);
		string flattenedReceiverType = BuildFlattenedReceiverType(function, receiver.ResolvedType ?? receiverValueType);
		string addressDecisionType = receiver.ResolvedType ?? receiverValueType;
		if (TryGetPointerElementType(flattenedReceiverType) is not null && TryGetPointerElementType(addressDecisionType) is null)
		{
			value = new UnaryExpression
			{
				SourceSyntax = receiver.SourceSyntax,
				Operator = UnaryOperator.AddressOf,
				Operand = receiver,
				ResolvedType = flattenedReceiverType
			};
		}

		return new ArgumentExpression
		{
			SourceSyntax = receiver.SourceSyntax,
			Value = value,
			ResolvedType = value.ResolvedType
		};
	}

	static string GetReceiverValueType(Expression receiver)
	{
		return receiver switch
		{
			VariableReferenceExpression { Variable: DeclarationTarget { Type.ResolvedType: string declarationType } } => declarationType,
			VariableReferenceExpression { Variable: VariableDefinition { Type.ResolvedType: string variableType } } => variableType,
			VariableReferenceExpression { Variable.ResolvedType: string variableType } => variableType,
			MemberReferenceExpression { Member: FieldDefinition { Type.ResolvedType: string fieldType } } => fieldType,
			MemberReferenceExpression { Member.ResolvedType: string memberType } => memberType,
			_ => receiver.ResolvedType ?? ErrorType
		};
	}

	string BuildFlattenedReceiverType(FunctionDefinition function, string receiverType)
	{
		return BuildEffectiveReceiverType(receiverType, function, isPropertyGetterSyntax: false);
	}

	void EnsureFlattenedFunctionSymbol(FunctionDefinition function)
	{
		if (!string.IsNullOrWhiteSpace(function.Symbol) && function.Symbol != function.Name)
			return;

		if (GetExplicitThisParameter(function) is ThisParameterDefinition thisParameter)
		{
			function.Symbol = BuildExtensionFunctionSymbol(function.Name, thisParameter.ResolvedType ?? ErrorType, function);
			return;
		}

		if (FindContainingType(function) is TypeDefinition type)
			function.Symbol = $"{type.Name}_{function.Name.TrimStart('~')}";
	}

	string BuildFlattenedFunctionValueType(FunctionDefinition function, string receiverType)
	{
		List<string> parameters = [receiverType];
		foreach (ParameterDefinition parameter in GetCallableParameters(function.Parameters))
			parameters.Add(parameter.ResolvedType ?? ErrorType);

		return BuildCallableType("fn", function.ResolvedType ?? ErrorType, parameters);
	}
}
