namespace Camp.Compiler;

// Keep declared markers on the source graph; only semantic/ABI consumers normalize them.
internal static class CompilerDefinedSpecs
{
	public const string TargetCall = "_targetcall";
	public const string TargetType = "_targettype";

	public static bool IsSpecShaped(string? name)
	{
		if (name is null || name.Length < 2 || name[0] != '_' || name[1] is < 'a' or > 'z' || name[^1] == '_')
			return false;
		for (int i = 2; i < name.Length; i++)
			if (name[i] is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'
				|| name[i] == '_' && name[i - 1] == '_')
				return false;
		return true;
	}

	public static bool IsReserved(string? name) => name is TargetCall or TargetType;
	public static string? EffectiveCall(string? spec) => spec == TargetCall ? null : spec;
	public static string? EffectiveType(string? spec) => spec == TargetType ? null : spec;
	public static string? Effective(string? spec) => IsReserved(spec) ? null : spec;
}
