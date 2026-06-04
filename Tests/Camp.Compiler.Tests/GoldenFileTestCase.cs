using System;
using System.IO;

namespace Camp.Compiler.Tests;

public enum GoldenFileTestKind
{
	Ast,
	Lowering,
	Diagnostics,
	CEmit
}

public sealed class GoldenFileTestCase
{
	public required string RepositoryRoot { get; init; }
	public required string CasePath { get; init; }
	public required GoldenFileTestKind Kind { get; init; }

	public string ExpectedPath => Path.ChangeExtension(CasePath, ExpectedExtension);
	public string ActualPath => Path.ChangeExtension(CasePath, ActualExtension);

	public override string ToString()
	{
		return Path.GetRelativePath(RepositoryRoot, CasePath).Replace('\\', '/');
	}

	string ExpectedExtension => Kind switch
	{
		GoldenFileTestKind.Ast => ".expected.xml",
		GoldenFileTestKind.Lowering => ".expected.camp",
		GoldenFileTestKind.Diagnostics => ".expected.txt",
		GoldenFileTestKind.CEmit => ".expected.c",
		_ => throw new ArgumentOutOfRangeException()
	};

	string ActualExtension => Kind switch
	{
		GoldenFileTestKind.Ast => ".actual.xml",
		GoldenFileTestKind.Lowering => ".actual.camp",
		GoldenFileTestKind.Diagnostics => ".actual.txt",
		GoldenFileTestKind.CEmit => ".actual.c",
		_ => throw new ArgumentOutOfRangeException()
	};
}
