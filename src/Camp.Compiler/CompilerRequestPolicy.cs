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
		bool hasVisibility = false;
		bool hasMainName = false;
		foreach (TokenValue token in CampTokenizer.Tokenize(text))
		{
			if (token.Class is TokenClass.Whitespace or TokenClass.NewLine or TokenClass.LineComment or TokenClass.BlockComment
				or TokenClass.String or TokenClass.InterpolatedString)
				continue;

			if (hasMainName)
				return token.Value == "(";

			if (token.Class == TokenClass.Identifier && token.Value is "export" or "internal")
			{
				hasVisibility = true;
				continue;
			}

			if (!hasVisibility)
				continue;

			if (token.Class == TokenClass.Identifier && token.Value == "main")
			{
				hasMainName = true;
				continue;
			}

			if (token.Value is ";" or "{" or "}" or "=")
				hasVisibility = false;
		}
		return false;
	}
}
