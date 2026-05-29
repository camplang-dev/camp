using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	bool TryRewriteInstanceInvocation(CallExpression call)
	{
		if (call.Target is not MemberReferenceExpression { Target: Expression receiver, Member: FunctionDefinition function } member)
			return false;
		if (IsPropertyGetterReference(member) || IsPropertySetterReference(member))
			return false;
		if (FindContainingType(function) is InterfaceDefinition)
			return false;

		RewriteInstanceInvocation(call, member, receiver, function);
		return true;
	}

	bool ShouldEmitFlattenedInstanceCalls()
	{
		return currentRewriteFunction is not null;
	}

	void RewriteInstanceInvocation(CallExpression call, MemberReferenceExpression member, Expression receiver, FunctionDefinition function)
	{
		call.Target = CreateFlattenedMethodReference(member, receiver, function);
		call.Arguments.Insert(0, CreateReceiverArgument(receiver));
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
			ResolvedType = BuildFlattenedFunctionValueType(function, receiver.ResolvedType ?? ErrorType)
		});
		grouped.Items.Add(new GroupedExpressionItem
		{
			Expression = receiver,
			ResolvedType = receiver.ResolvedType
		});
		return grouped;
	}

	MethodReferenceExpression CreateFlattenedMethodReference(MemberReferenceExpression member, Expression receiver, FunctionDefinition function)
	{
		EnsureFlattenedFunctionSymbol(function);
		MethodReferenceExpression reference = new()
		{
			SourceSyntax = member.SourceSyntax,
			ResolvedType = BuildFlattenedFunctionValueType(function, receiver.ResolvedType ?? ErrorType)
		};
		reference.Candidates.Add(function);
		return reference;
	}

	ArgumentExpression CreateReceiverArgument(Expression receiver)
	{
		return new ArgumentExpression
		{
			SourceSyntax = receiver.SourceSyntax,
			Value = receiver,
			ResolvedType = receiver.ResolvedType
		};
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
