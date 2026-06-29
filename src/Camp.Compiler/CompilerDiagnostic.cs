namespace Camp.Compiler;

public enum DiagnosticSeverity
{
	Error,
	Warning,
	Info
}

public static class DiagnosticCodes
{
	public const string InitializerAssignmentRequiresDot = "CAMP1001";
	public const string ReservedIdentifier = "CAMP3001";
	public const string RangeRequiresRangeParameter = "CAMP3101";
	public const string AutoCannotInferVoid = "CAMP3201";
}

public sealed record CompilerDiagnostic(
	TokenRange? Range,
	string Message,
	string? Code = null,
	DiagnosticSeverity Severity = DiagnosticSeverity.Error);
