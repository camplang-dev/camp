namespace Camp.Compiler.Tests;

public sealed class StdlibCampTestCase
{
	public required string Id { get; init; }
	public required string Name { get; init; }
	public required string QualifiedName { get; init; }
	public required string Sourcefile { get; init; }
	public required int Sourceline { get; init; }
	public required string Summary { get; init; }
	public required bool Skipped { get; init; }
	public required string? SkipReason { get; init; }
	public string? DiscoveryFailure { get; init; }

	public override string ToString()
	{
		return string.IsNullOrWhiteSpace(QualifiedName) ? "stdlib Camp test discovery" : QualifiedName;
	}
}
