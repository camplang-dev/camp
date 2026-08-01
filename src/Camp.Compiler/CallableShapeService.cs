using System;
using System.Collections.Generic;

namespace Camp.Compiler;

internal readonly record struct CallableShape(string Kind, string? Spec, string? CallSpec, string ReturnType, List<string> Parameters, ThisContract This = default);

internal readonly record struct ThisContract(bool HasThis, bool IsConst, bool IsVolatile, string Lifetime)
{
	public bool IsEscaped => Lifetime == "escaped";
	public bool IsDefault => !HasThis;
}

internal readonly record struct CallableSlot(string Modifier, string Type);

internal static class CallableShapeService
{
	public static string BuildCallableType(string kind, string returnType, List<string> parameters, string? targetSpec = null, string? callSpec = null)
	{
		string specs = "";
		if (!string.IsNullOrWhiteSpace(targetSpec))
			specs += " " + targetSpec;
		if (!string.IsNullOrWhiteSpace(callSpec))
			specs += " " + callSpec;
		return $"{kind}{specs} {returnType}({string.Join(", ", parameters)})";
	}

	public static bool TryParseCallableShape(string type, out CallableShape shape)
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

	public static List<string> SplitCallableSpecs(ref string text)
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

	public static List<string> SplitCallableParameterTypes(string parametersText)
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

	public static bool Compatible(CallableShape source, CallableShape target, bool compareThis)
	{
		if (source.Parameters.Count != target.Parameters.Count)
			return false;

		for (int i = 0; i < source.Parameters.Count; i++)
		{
			if (source.Parameters[i] != target.Parameters[i])
				return false;
		}

		return source.Spec == target.Spec
			&& source.CallSpec == target.CallSpec
			&& source.ReturnType == target.ReturnType
			&& (!compareThis || source.This == target.This);
	}

	public static bool CompatibleWithConstOfVariance(CallableShape source, CallableShape target, bool compareThis, Func<string, string> eraseConstOfQualifiers)
	{
		if (source.Parameters.Count != target.Parameters.Count)
			return false;
		if (source.Spec != target.Spec
			|| source.CallSpec != target.CallSpec
			|| compareThis && source.This != target.This)
			return false;
		if (!SlotTypesCompatible(source.ReturnType, target.ReturnType, outputPosition: true, eraseConstOfQualifiers))
			return false;

		for (int i = 0; i < source.Parameters.Count; i++)
		{
			CallableSlot sourceSlot = ParseCallableSlot(source.Parameters[i]);
			CallableSlot targetSlot = ParseCallableSlot(target.Parameters[i]);
			if (!CallableSlotModifiersCompatible(sourceSlot.Modifier, targetSlot.Modifier))
				return false;
			if (sourceSlot.Modifier == "thrown")
			{
				if (sourceSlot.Type != targetSlot.Type)
					return false;
				continue;
			}

			bool outputPosition = sourceSlot.Modifier is "out" or "prep";
			if (!SlotTypesCompatible(sourceSlot.Type, targetSlot.Type, outputPosition, eraseConstOfQualifiers))
				return false;
		}

		return true;
	}

	static bool CallableSlotModifiersCompatible(string source, string target)
	{
		return source == target
			|| source == "prep" && target.Length == 0;
	}

	public static bool SlotTypesCompatible(string source, string target, bool outputPosition, Func<string, string> eraseConstOfQualifiers)
	{
		if (source == target)
			return true;

		string erasedSource = eraseConstOfQualifiers(source);
		string erasedTarget = eraseConstOfQualifiers(target);
		if (source != erasedSource && erasedSource == target)
			return outputPosition;
		if (target != erasedTarget && source == erasedTarget)
			return !outputPosition;
		return false;
	}

	public static CallableSlot ParseCallableSlot(string text)
	{
		foreach (string modifier in new[] { "out", "in", "thrown", "within", "prep" })
		{
			string prefix = modifier + " ";
			if (text.StartsWith(prefix, StringComparison.Ordinal))
				return new CallableSlot(modifier, text[prefix.Length..]);
		}
		return new CallableSlot("", text);
	}
}
