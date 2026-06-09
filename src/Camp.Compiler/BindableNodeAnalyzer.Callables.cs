using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	static string BuildCallableType(string kind, string returnType, List<string> parameters)
	{
		return $"{kind} {returnType}({string.Join(", ", parameters)})";
	}

	bool TryGetCallableShape(string? type, out CallableShape shape)
	{
		shape = default;
		if (type is null)
			return false;

		if (TryGetIteratorProtocolCurrentTypes(type, out List<string>? currentTypes) && currentTypes is not null)
		{
			List<string> parameters = [];
			foreach (string currentType in currentTypes)
				parameters.Add(AddPointer(currentType));
			shape = new CallableShape("iter", null, null, "bool", parameters);
			return true;
		}

		if (TryParseCallableShape(type, out shape))
			return true;

		if (typeDefinitions.TryGetValue(BaseTypeName(type), out TypeDefinition? definition)
			&& definition is NewtypeDefinition { UnderlyingType: not null } newtypeDefinition)
		{
			string underlyingType = newtypeDefinition.UnderlyingType.ResolvedType ?? ErrorType;
			if (TryParseCallableShape(underlyingType, out shape))
			{
				if (newtypeDefinition.Parameters.Count > 0)
					shape = new CallableShape(shape.Kind, shape.Spec, shape.CallSpec, shape.ReturnType, [.. GetParameterTypeNames(newtypeDefinition.Parameters)]);
				return true;
			}

			if (newtypeDefinition.UnderlyingType is IterTypeReference
				&& TryGetIteratorProtocolCurrentTypes(underlyingType, out List<string>? iterCurrentTypes)
				&& iterCurrentTypes is not null)
			{
				List<string> parameters = [];
				foreach (string currentType in iterCurrentTypes)
					parameters.Add(AddPointer(currentType));
				parameters.AddRange(GetExpandedCallableParameterTypes(newtypeDefinition.Parameters));
				shape = new CallableShape("iter", null, null, "bool", parameters);
				return true;
			}
		}

		return false;
	}

	static bool TryGetIteratorProtocolCurrentTypes(string type, out List<string>? currentTypes)
	{
		currentTypes = null;
		if (type.StartsWith("iter ", StringComparison.Ordinal))
		{
			currentTypes = [type["iter ".Length..].Trim()];
			return currentTypes[0].Length > 0;
		}
		if (type.StartsWith("iter(", StringComparison.Ordinal))
		{
			currentTypes = [];
			string slots = type["iter(".Length..];
			if (slots.EndsWith(")", StringComparison.Ordinal))
				slots = slots[..^1];
			foreach (string slot in SplitCallableParameterTypes(slots))
			{
				if (slot.StartsWith("thrown ", StringComparison.Ordinal))
					continue;
				if (slot.Length > 0)
					currentTypes.Add(slot);
			}
			return currentTypes.Count > 0;
		}
		return false;
	}

	static bool TryParseCallableShape(string type, out CallableShape shape)
	{
		shape = default;
		string kind;
		string remainder;
		if (type.StartsWith("fn ", StringComparison.Ordinal))
		{
			kind = "fn";
			remainder = type["fn ".Length..];
		}
		else if (type.StartsWith("delegate ", StringComparison.Ordinal))
		{
			kind = "delegate";
			remainder = type["delegate ".Length..];
		}
		else if (type.StartsWith("async ", StringComparison.Ordinal))
		{
			kind = "async";
			remainder = type["async ".Length..];
		}
		else if (type.StartsWith("once ", StringComparison.Ordinal))
		{
			kind = "once";
			remainder = type["once ".Length..];
		}
		else
		{
			return false;
		}

		int open = remainder.IndexOf('(', StringComparison.Ordinal);
		int close = remainder.LastIndexOf(')');
		if (open < 0 || close < open)
			return false;

		string signaturePrefix = remainder[..open].Trim();
		List<string> specs = SplitCallableSpecs(ref signaturePrefix);

		string returnType = signaturePrefix;
		string parametersText = remainder[(open + 1)..close].Trim();
		shape = new CallableShape(kind, specs.Count > 0 ? specs[0] : null, specs.Count > 1 ? specs[1] : null, returnType, SplitCallableParameterTypes(parametersText));
		return true;
	}

	static List<string> SplitCallableSpecs(ref string text)
	{
		List<string> specs = [];
		while (true)
		{
			int space = text.IndexOf(' ');
			if (space <= 0)
				return specs;

			string candidate = text[..space];
			if (!candidate.StartsWith("_", StringComparison.Ordinal))
				return specs;

			specs.Add(candidate);
			text = text[(space + 1)..].TrimStart();
		}
	}

	static List<string> SplitCallableParameterTypes(string parametersText)
	{
		List<string> parameters = [];
		if (string.IsNullOrWhiteSpace(parametersText))
			return parameters;

		int start = 0;
		int depth = 0;
		for (int i = 0; i < parametersText.Length; i++)
		{
			char c = parametersText[i];
			if (c is '(' or '<' or '[')
				depth++;
			else if (c is ')' or '>' or ']')
				depth--;
			else if (c == ',' && depth == 0)
			{
				parameters.Add(parametersText[start..i].Trim());
				start = i + 1;
			}
		}

		parameters.Add(parametersText[start..].Trim());
		return parameters;
	}

	static bool CallableShapesCompatible(CallableShape source, CallableShape target)
	{
		if (source.Parameters.Count != target.Parameters.Count)
			return false;

		for (int i = 0; i < source.Parameters.Count; i++)
		{
			if (source.Parameters[i] != target.Parameters[i])
				return false;
		}

		return source.Spec == target.Spec && source.CallSpec == target.CallSpec && source.ReturnType == target.ReturnType;
	}

	static string? GetLambdaParameterSymbolName(LambdaParameter parameter)
	{
		return parameter.Name ?? parameter.Parameter?.Name;
	}

	readonly struct CallableShape
	{
		public CallableShape(string kind, string? spec, string? callSpec, string returnType, List<string> parameters)
		{
			Kind = kind;
			Spec = spec;
			CallSpec = callSpec;
			ReturnType = returnType;
			Parameters = parameters;
		}

		public string Kind { get; }
		public string? Spec { get; }
		public string? CallSpec { get; }
		public string ReturnType { get; }
		public List<string> Parameters { get; }
	}

	bool IsInstanceFunction(FunctionDefinition function)
	{
		return function.Modifier != FunctionModifier.Static
			&& function.Modifier != FunctionModifier.Constructor
			&& !IsDestructorFunction(function)
			&& FindContainingType(function) is not null;
	}

	static string BaseTypeName(string type)
	{
		if (new TypeShapeParser(type).TryParse(out TypeShape shape))
		{
			while (shape.Element is not null)
				shape = shape.Element;

			type = shape.Name;
		}
		else
		{
			type = StripConst(type);
		}

		int genericStart = type.IndexOf('<', StringComparison.Ordinal);
		if (genericStart >= 0)
			type = type[..genericStart];

		while (type.EndsWith("*", StringComparison.Ordinal) || type.EndsWith("[]", StringComparison.Ordinal) || type.EndsWith("?", StringComparison.Ordinal))
		{
			if (type.EndsWith("[]", StringComparison.Ordinal))
				type = type[..^2];
			else
				type = type[..^1];
		}

		return type;
	}

	static bool IsConstQualified(string? type)
	{
		return IsConstQualifiedShape(type);
	}

	static string StripConst(string type)
	{
		return StripConstFromShape(type);
	}
}
