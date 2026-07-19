using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class SourcefilePathMapperTests
{
	[Fact]
	public void Relative_mode_uses_default_and_explicit_roots()
	{
		SourcefilePathMapper defaultRoot = new(SourcefilePathMode.Relative, "/repo/app", []);
		Assert.Equal("src/main.camp", defaultRoot.Map("/repo/app/src/main.camp").Value);

		SourcefilePathMapper explicitRoot = new(SourcefilePathMode.Relative, "/repo/app", ["/repo/shared"]);
		Assert.Equal("lib.camp", explicitRoot.Map("/repo/shared/lib.camp").Value);
	}

	[Fact]
	public void Relative_mode_chooses_longest_matching_root()
	{
		SourcefilePathMapper mapper = new(SourcefilePathMode.Relative, "/repo", ["/repo", "/repo/app"]);
		Assert.Equal("src/main.camp", mapper.Map("/repo/app/src/main.camp").Value);
	}

	[Fact]
	public void Windows_roots_match_by_drive_and_do_not_cross_drives()
	{
		SourcefilePathMapper mapper = new(SourcefilePathMode.Relative, "C:/work/app", ["C:/work/app"]);
		Assert.Equal("src/main.camp", mapper.Map(@"C:\work\app\src\main.camp").Value);

		SourcefilePathMapResult result = mapper.Map(@"D:\packages\json\src\json.camp");
		Assert.False(result.Success);
		Assert.Contains("outside every --sourcefile-root", result.Diagnostic);
	}

	[Fact]
	public void Relative_mode_reports_duplicate_outputs()
	{
		SourcefilePathMapper mapper = new(SourcefilePathMode.Relative, "/repo", ["/repo/app", "/repo/package"]);
		Assert.True(mapper.Map("/repo/app/src/file.camp").Success);

		SourcefilePathMapResult duplicate = mapper.Map("/repo/package/src/file.camp");
		Assert.False(duplicate.Success);
		Assert.Contains("produced by both", duplicate.Diagnostic);
	}

	[Fact]
	public void Absolute_mode_ignores_roots()
	{
		SourcefilePathMapper mapper = new(SourcefilePathMode.Absolute, "/repo/app", ["/other"]);
		Assert.Equal("/repo/app/src/main.camp", mapper.Map("/repo/app/src/main.camp").Value);
	}
}
