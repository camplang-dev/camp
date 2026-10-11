using Xunit;

namespace Camp.Compiler.Tests;

public sealed class EnumStorageComparisonTests
{
	[Theory]
	[InlineData("globalColor == RED")]
	[InlineData("RED == globalColor")]
	[InlineData("globalColor != GREEN")]
	[InlineData("GREEN != globalColor")]
	[InlineData("Colors::value == RED")]
	[InlineData("RED == Colors::value")]
	[InlineData("(globalColor) == RED")]
	[InlineData("localColor == RED")]
	public void Comparisons_target_enum_members_from_local_and_global_storage(string comparison)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			enum Color: int { RED, GREEN }
			Color globalColor;
			namespace Colors { Color value; }
			bool test() { Color localColor = RED; return {{comparison}}; }
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}
}
