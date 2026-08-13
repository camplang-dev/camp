using System;

namespace Camp.Compiler;

public static class CompilerRequestPolicy
{
	public static WithinAllocationPolicy GetEffectiveWithinAllocationPolicy(CompilerRequest request)
	{
		if (request.WithinAllocationPolicy is WithinAllocationPolicy policy)
			return policy;
		return GetDefaultWithinAllocationPolicy(request.WithinPolicyBuildKind ?? request.BuildKind);
	}

	public static WithinAllocationPolicy GetDefaultWithinAllocationPolicy(NativeBuildKind? buildKind)
	{
		return buildKind is NativeBuildKind.Static or NativeBuildKind.Shared
			? WithinAllocationPolicy.Explicit
			: WithinAllocationPolicy.Implicit;
	}

	public static bool HasPublicOrExportedMain(string text)
	{
		for (int i = 0; i < text.Length;)
		{
			int found = text.IndexOf("main", i, StringComparison.Ordinal);
			if (found < 0)
				return false;
			i = found + 4;
			if (!IsIdentifierBoundary(text, found - 1) || !IsIdentifierBoundary(text, found + 4))
				continue;
			int j = found + 4;
			while (j < text.Length && char.IsWhiteSpace(text[j]))
				j++;
			if (j >= text.Length || text[j] != '(')
				continue;
			string prefix = text[..found];
			int visibilityIndex = Math.Max(prefix.LastIndexOf("export", StringComparison.Ordinal), prefix.LastIndexOf("internal", StringComparison.Ordinal));
			if (visibilityIndex >= 0 && found - visibilityIndex < 256 && IsIdentifierBoundary(text, visibilityIndex - 1))
				return true;
		}
		return false;
	}

	static bool IsIdentifierBoundary(string text, int index)
	{
		return index < 0 || index >= text.Length || !(char.IsLetterOrDigit(text[index]) || text[index] == '_');
	}
}
