using System;
using System.Linq;
using System.Text.Json;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CampTestDiscoveryTests
{
	[Fact]
	public void Discovery_returns_manifest_records_for_valid_skipped_and_invalid_tests()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/test_manifest.camp", """
			namespace MathTests;

			struct Assertion
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			struct NotAssertion
			{
				string text;
			}

			struct Allocator
			{
			}

			struct LocalAllocator
			{
			}

			/// Adds two values.
			/// @test
			void addReturnsSum(thrown Assertion* assertion)
			{
			}

			@test
			void allocatorImplicit(within allocator, thrown Assertion* assertion)
			{
			}

			@test
			void allocatorExplicit(within Allocator* arena, thrown Assertion* assertion)
			{
			}

			@skip("not ready")
			@test
			void skippedCase(thrown Assertion* assertion)
			{
			}

			/// Broken shape.
			/// @test
			int invalidShape()
			{
				return 0;
			}

			@test
			void invalidThrownShape(thrown NotAssertion* assertion)
			{
			}

			@test
			void invalidAllocatorType(within LocalAllocator* arena, thrown Assertion* assertion)
			{
			}

			@test
			void invalidAllocatorOrder(thrown Assertion* assertion, within allocator)
			{
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		CampTestDiscoveryResult result = CampTestDiscovery.Discover(compilation.Compilation, CampTestManifestMode.InModule);

		Assert.Empty(result.Diagnostics);
		Assert.Equal(CampTestManifestMode.InModule, result.Manifest.Mode);
		Assert.Equal(8, result.Manifest.Tests.Count);

		CampTestManifestEntry add = result.Manifest.Tests.Single(static test => test.Name == "addReturnsSum");
		Assert.Equal("MathTests::addReturnsSum", add.Id);
		Assert.Equal("MathTests::addReturnsSum", add.QualifiedName);
		Assert.Equal("tests/test_manifest.camp", add.Sourcefile);
		Assert.Equal("Adds two values.", add.Summary);
		Assert.False(add.Skipped);
		Assert.Null(add.SkipReason);
		Assert.Equal("valid", add.RunnerSignature);

		CampTestManifestEntry allocatorImplicit = result.Manifest.Tests.Single(static test => test.Name == "allocatorImplicit");
		Assert.Equal("valid", allocatorImplicit.RunnerSignature);

		CampTestManifestEntry allocatorExplicit = result.Manifest.Tests.Single(static test => test.Name == "allocatorExplicit");
		Assert.Equal("valid", allocatorExplicit.RunnerSignature);

		CampTestManifestEntry skipped = result.Manifest.Tests.Single(static test => test.Name == "skippedCase");
		Assert.True(skipped.Skipped);
		Assert.Equal("not ready", skipped.SkipReason);
		Assert.Equal("valid", skipped.RunnerSignature);

		CampTestManifestEntry invalid = result.Manifest.Tests.Single(static test => test.Name == "invalidShape");
		Assert.Equal("Broken shape.", invalid.Summary);
		Assert.False(invalid.Skipped);
		Assert.Equal("invalid", invalid.RunnerSignature);

		CampTestManifestEntry invalidThrown = result.Manifest.Tests.Single(static test => test.Name == "invalidThrownShape");
		Assert.Equal("invalid", invalidThrown.RunnerSignature);

		CampTestManifestEntry invalidAllocatorType = result.Manifest.Tests.Single(static test => test.Name == "invalidAllocatorType");
		Assert.Equal("invalid", invalidAllocatorType.RunnerSignature);

		CampTestManifestEntry invalidAllocatorOrder = result.Manifest.Tests.Single(static test => test.Name == "invalidAllocatorOrder");
		Assert.Equal("invalid", invalidAllocatorOrder.RunnerSignature);

		using JsonDocument json = JsonDocument.Parse(CampTestManifestJsonSerializer.Serialize(result.Manifest));
		Assert.Equal("camp.test-manifest", json.RootElement.GetProperty("format").GetString());
		Assert.Equal("in-module", json.RootElement.GetProperty("mode").GetString());
		Assert.Equal(8, json.RootElement.GetProperty("tests").GetArrayLength());
	}

	[Fact]
	public void Filter_patterns_are_exact_without_wildcards_and_support_simple_wildcards()
	{
		CampTestManifestEntry test = new(
			"MathTests::parseValue",
			"parseValue",
			"MathTests::parseValue",
			"tests/math.camp",
			12,
			"",
			false,
			null,
			"valid");

		Assert.True(CampTestFilter.Matches(test, "MathTests::parseValue"));
		Assert.True(CampTestFilter.Matches(test, "parseValue"));
		Assert.False(CampTestFilter.Matches(test, "parse"));
		Assert.True(CampTestFilter.Matches(test, "*parse*"));
		Assert.True(CampTestFilter.Matches(test, "MathTests::*Value"));
		Assert.True(CampTestFilter.Matches(test, "parse?alue"));
		Assert.True(CampTestFilter.Matches(test, "parse^alue"));
		Assert.False(CampTestFilter.Matches(test, "parse^value"));
		Assert.False(CampTestFilter.Matches(test, "Parse*"));
	}
}
