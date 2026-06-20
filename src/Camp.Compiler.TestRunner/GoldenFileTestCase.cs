using System;
using System.IO;

namespace Camp.Compiler.Tests;

public enum GoldenFileTestKind
{
	Ast,
	Declarations,
	LoweringXml,
	Lowering,
	Diagnostics,
	CEmit,
	CCompile,
	Api,
	Metadata,
	Std,
	StdRun
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
		GoldenFileTestKind.Declarations => ".expected.xml",
		GoldenFileTestKind.LoweringXml => ".expected.xml",
		GoldenFileTestKind.Lowering => ".expected.camp",
		GoldenFileTestKind.Diagnostics => ".expected.txt",
		GoldenFileTestKind.CEmit => ".expected.c",
		GoldenFileTestKind.CCompile => ".expected.txt",
		GoldenFileTestKind.Api => ".expected.camp",
		GoldenFileTestKind.Metadata => ".expected.json",
		GoldenFileTestKind.Std => ".expected.camp",
		GoldenFileTestKind.StdRun => ".expected.txt",
		_ => throw new ArgumentOutOfRangeException()
	};

	string ActualExtension => Kind switch
	{
		GoldenFileTestKind.Ast => ".actual.xml",
		GoldenFileTestKind.Declarations => ".actual.xml",
		GoldenFileTestKind.LoweringXml => ".actual.xml",
		GoldenFileTestKind.Lowering => ".actual.camp",
		GoldenFileTestKind.Diagnostics => ".actual.txt",
		GoldenFileTestKind.CEmit => ".actual.c",
		GoldenFileTestKind.CCompile => ".actual.txt",
		GoldenFileTestKind.Api => ".actual.camp",
		GoldenFileTestKind.Metadata => ".actual.json",
		GoldenFileTestKind.Std => ".actual.camp",
		GoldenFileTestKind.StdRun => ".actual.txt",
		_ => throw new ArgumentOutOfRangeException()
	};
}
