using System;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CarrierPointerQualifierTests
{
	[Theory]
	[InlineData("byte<Color>")]
	[InlineData("long<Color>")]
	[InlineData("nint<Color>")]
	[InlineData("Color")]
	[InlineData("Id")]
	public void Pointers_to_nominal_storage_can_add_const(string storage)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			enum Color: byte { RED, GREEN }
			newtype Id: long;
			void read(const {{storage}}* value) {}
			const {{storage}}* result({{storage}}* value) => value;
			void use({{storage}}* value)
			{
				const {{storage}}* view = value;
				view = value;
				read(value);
			}
			""");
		SemanticCompiler.AssertNoDiagnostics(compilation);
	}

	[Theory]
	[InlineData("const long<Color>*", "long<Color>*")]
	[InlineData("long<Color>*", "long<Other>*")]
	[InlineData("long<Color>*", "Color*")]
	[InlineData("long<Color>*", "long*")]
	[InlineData("long<Color>**", "const long<Color>**")]
	public void Pointer_qualification_preserves_nominal_identity_and_depth_rules(string source, string target)
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLowered($$"""
			enum Color: int { RED, GREEN }
			enum Other: int { RED, GREEN }
			void use({{source}} value) { {{target}} view = value; }
			""");
		Assert.NotEmpty(compilation.Diagnostics);
		Assert.All(compilation.Diagnostics, message => Assert.Contains("cannot convert", message, StringComparison.OrdinalIgnoreCase));
	}
}
