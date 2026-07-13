using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

internal static class UnsupportedAvailability
{
	public const string AttributeName = "notsupported";

	public static bool IsUnsupported(FunctionDefinition function)
	{
		return TryGetAttribute(function.Attributes, out _);
	}

	public static bool TryGetReason(FunctionDefinition function, out string? reason)
	{
		reason = null;
		if (!TryGetAttribute(function.Attributes, out AttributeConstructor? attribute) || attribute is null)
			return false;
		reason = GetReason(attribute);
		return true;
	}

	public static string? GetReason(AttributeConstructor attribute)
	{
		return attribute.Arguments.FirstOrDefault()?.Value is LiteralExpression literal
			? literal.Value?.ToString() ?? literal.Text
			: null;
	}

	public static bool TryGetAttribute(IEnumerable<AttributeConstructor> attributes, out AttributeConstructor? attribute)
	{
		attribute = attributes.FirstOrDefault(IsUnsupportedAttribute);
		return attribute is not null;
	}

	public static bool IsUnsupportedAttribute(AttributeConstructor attribute)
	{
		return string.Equals(NormalizeAttributeName(attribute.Name), AttributeName, StringComparison.Ordinal);
	}

	public static string NormalizeAttributeName(string name)
	{
		return name.StartsWith("@", StringComparison.Ordinal) ? name[1..] : name;
	}
}
