using System;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	bool IsEscapedType(string? type)
	{
		return !string.IsNullOrWhiteSpace(type) && TryParseTypeShape(type, out TypeShape shape) && shape.Kind == TypeShapeKind.Named
			&& typeDefinitions.TryGetValue(BaseTypeName(StripTopLevelValueQualifiers(type)), out TypeDefinition? definition)
			&& definition is ClassDefinition { IsEscaped: true } or InterfaceDefinition { IsEscaped: true };
	}

	bool IsEscapedPointerType(string? type)
	{
		return TryGetPointerElementType(StripTopLevelValueQualifiers(type ?? "")) is string element && IsEscapedType(element);
	}

	bool ContainsEscapedValueStorage(string? type)
	{
		if (IsEscapedType(type))
			return true;
		return TryGetFixedArrayShape(type ?? "", out string element, out _) && ContainsEscapedValueStorage(element);
	}

	void ValidateEscapedPointerAnnotation(string? type, string kind, SyntaxNode? syntax)
	{
		if (kind != "escaped" && IsEscapedPointerType(type))
			Report(GetRange(syntax), "A pointer to an escaped type cannot have a scoped or unscoped lifetime annotation.");
	}

	void CheckEscapedTypeConversion(string expected, string actual, Expression? value, SyntaxNode? syntax)
	{
		if (!IsEscapedPointerType(expected) || IsEscapedPointerType(actual) || value is null or DefaultExpression
			|| actual is "#NULL" or "#DEFAULT" or AllocatorType || expected == ErrorType || actual == ErrorType)
			return;
		string? fact = TryGetPointerElementType(actual) is not null
			? GetExpressionLifetimeFact(value)
			: GetEscapedConversionStorageFact(value);
		if (fact is "null" or "default" || TryParseLifetimeFact(fact, out LifetimeFact lifetime) && lifetime.Kind == "escaped")
			return;
		Report(GetRange(syntax ?? value.SourceSyntax), $"Conversion from '{actual}' to '{expected}', a pointer to an escaped type, requires escaped source storage.");
	}

	string? GetEscapedConversionStorageFact(Expression expression)
	{
		if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(rewritten, expression))
			return GetEscapedConversionStorageFact(rewritten);
		if (expression is ParenthesizedExpression parenthesized && parenthesized.Expression is not null)
			return GetEscapedConversionStorageFact(parenthesized.Expression);
		if (expression is VariableReferenceExpression { Variable: VariableDefinition or FieldDefinition { Modifier: FieldModifier.Static } }
			or MemberReferenceExpression { Member: FieldDefinition { Modifier: FieldModifier.Static } })
			return MakeLifetimeFact("escaped", null, "static storage");
		if (expression is MemberReferenceExpression { Target: not null } member)
			return TryGetPointerElementType(member.Target.ResolvedType) is not null
				? GetExpressionLifetimeFact(member.Target) : GetEscapedConversionStorageFact(member.Target);
		return GetStorageLifetimeFact(expression);
	}

	string? GetAllocatorContextLifetimeFact(Expression? context)
	{
		if (context is null)
			return null;
		if (context.ResolvedType == AllocatorType
			&& TryGetStorageNode(context, out BindableNode? storage)
			&& storage is FieldDefinition { GeneratedInfo.Source: ParameterDefinition retainedAllocator }
			&& IsEscapedPointerType(retainedAllocator.ResolvedType))
			return MakeLifetimeFact("escaped", null, "retained allocator type");
		return TryGetPointerElementType(context.ResolvedType) is not null
			? GetExpressionLifetimeFact(context)
			: GetEscapedConversionStorageFact(context) ?? MakeLifetimeFact("scoped", null, "allocator storage");
	}
}
