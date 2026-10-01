using System;

namespace Camp.Compiler;

// ResolvedType is a transport key in the string-based binder. The semantic
// witness node retains both type references; C emission erases this key only
// after source-level compatibility and member lookup have finished.
internal readonly record struct WitnessTypeIdentity(string Target, string Interface)
{
	private const string Prefix = "#witness(";

	public string ResolvedType => Prefix + Target + ";" + Interface + ")";
	public string DisplayName => "vtableof(" + Target + ": " + Interface + ")";
	public string CStorageType => "const " + Interface + "*";

	public static string Display(string type)
	{
		return TryParse(type, out WitnessTypeIdentity witness) ? witness.DisplayName : type;
	}

	public static bool TryParse(string? value, out WitnessTypeIdentity identity)
	{
		identity = default;
		if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal) || !value.EndsWith(')'))
			return false;
		int separator = value.IndexOf(';', Prefix.Length);
		if (separator <= Prefix.Length || separator >= value.Length - 2)
			return false;
		identity = new WitnessTypeIdentity(value[Prefix.Length..separator], value[(separator + 1)..^1]);
		return true;
	}
}
