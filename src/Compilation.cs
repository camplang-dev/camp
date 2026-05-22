using System.Collections.Generic;

namespace Camp.Compiler;

public sealed class Compilation
{
	public List<SourceFile> Files { get; } = [];
}

public sealed class SourceFile
{
	public required string Path { get; init; }
	public required string Text { get; init; }
	public TokenSequence? Tokens { get; set; }
	public CompilationUnitSyntax? SyntaxTree { get; set; }
	public Module? BindableTree { get; set; }
}
