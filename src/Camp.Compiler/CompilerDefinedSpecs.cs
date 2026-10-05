namespace Camp.Compiler;

// Keep declared markers on the source graph; only semantic/ABI consumers normalize them.
internal static class CompilerDefinedSpecs
{
	public const string TargetCall = "_targetcall";
	public const string TargetType = "_targettype";

	public static bool IsReserved(string? name) => name is TargetCall or TargetType;
	public static string? EffectiveCall(string? spec) => spec == TargetCall ? null : spec;
	public static string? EffectiveType(string? spec) => spec == TargetType ? null : spec;
	public static string? Effective(string? spec) => IsReserved(spec) ? null : spec;
}
