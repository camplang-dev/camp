using System;
using System.Collections.Generic;
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
	public void Discovery_accepts_interface_allocator_within_test_signature()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/interface_allocator_test_manifest.camp", """
			namespace InterfaceAllocatorTests;

			struct Assertion
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			interface Allocator
			{
			}

			@test
			void allocatorExplicit(within Allocator* allocator, thrown Assertion* assertion)
			{
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		CampTestDiscoveryResult result = CampTestDiscovery.Discover(compilation.Compilation, CampTestManifestMode.InModule);

		Assert.Empty(result.Diagnostics);
		CampTestManifestEntry allocatorExplicit = result.Manifest.Tests.Single(static test => test.Name == "allocatorExplicit");
		Assert.Equal("valid", allocatorExplicit.RunnerSignature);
	}

	[Fact]
	public void Discovery_lists_factory_tests_separately_with_signature_and_testname_parameter()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/factory_test_manifest.camp", """
			namespace FactoryManifestTests;

			struct Assertion
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			@test
			void basicTests(thrown Assertion* assertion)
			{
				testAdd("addOneAndTwo", 1, 2, 3);
			}

			/// Adds two numbers.
			@factorytest
			void testAdd(@testname string testname, int first, int second, int expected, thrown Assertion* assertion)
			{
			}

			@skip("not ready")
			@factorytest
			void skippedFactory(thrown Assertion* assertion)
			{
			}

			@factorytest
			void invalidFactory(int first)
			{
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		CampTestDiscoveryResult result = CampTestDiscovery.Discover(compilation.Compilation, CampTestManifestMode.InModule);

		Assert.Empty(result.Diagnostics);
		Assert.Single(result.Manifest.Tests);
		Assert.DoesNotContain(result.Manifest.Tests, static test => test.Name is "testAdd" or "skippedFactory" or "invalidFactory");
		Assert.Equal(3, result.Manifest.FactoryTests.Count);

		CampTestManifestEntry testAdd = result.Manifest.FactoryTests.Single(static test => test.Name == "testAdd");
		Assert.Equal("FactoryManifestTests::testAdd", testAdd.Id);
		Assert.Equal("FactoryManifestTests::testAdd", testAdd.QualifiedName);
		Assert.Equal("Adds two numbers.", testAdd.Summary);
		Assert.False(testAdd.Skipped);
		Assert.Null(testAdd.SkipReason);
		Assert.Equal("valid", testAdd.RunnerSignature);
		Assert.Equal("testname", testAdd.TestNameParameter);

		CampTestManifestEntry skippedFactory = result.Manifest.FactoryTests.Single(static test => test.Name == "skippedFactory");
		Assert.True(skippedFactory.Skipped);
		Assert.Equal("not ready", skippedFactory.SkipReason);
		Assert.Equal("valid", skippedFactory.RunnerSignature);
		Assert.Null(skippedFactory.TestNameParameter);

		CampTestManifestEntry invalidFactory = result.Manifest.FactoryTests.Single(static test => test.Name == "invalidFactory");
		Assert.Equal("invalid", invalidFactory.RunnerSignature);

		using JsonDocument json = JsonDocument.Parse(CampTestManifestJsonSerializer.Serialize(result.Manifest));
		Assert.Equal(2, json.RootElement.GetProperty("version").GetInt32());
		Assert.Equal(1, json.RootElement.GetProperty("tests").GetArrayLength());
		Assert.Equal(3, json.RootElement.GetProperty("factoryTests").GetArrayLength());
	}

	[Fact]
	public void Manifest_json_round_trips_through_parser()
	{
		CampTestManifest manifest = new(CampTestManifestMode.InModule,
		[
			new CampTestManifestEntry("Tests::sample", "sample", "Tests::sample", "tests/sample.camp", 7, "summary text", true, "skip reason", "valid")
		],
		[
			new CampTestManifestEntry("Tests::sampleFactory", "sampleFactory", "Tests::sampleFactory", "tests/sample.camp", 14, "factory summary", false, null, "valid", "testname")
		]);

		bool parsed = CampTestManifestJsonSerializer.TryParse(CampTestManifestJsonSerializer.Serialize(manifest), out CampTestManifest roundTrip, out List<string> diagnostics);

		Assert.True(parsed, string.Join(Environment.NewLine, diagnostics));
		CampTestManifestEntry test = Assert.Single(roundTrip.Tests);
		Assert.Equal(CampTestManifestMode.InModule, roundTrip.Mode);
		Assert.Equal("Tests::sample", test.Id);
		Assert.Equal("sample", test.Name);
		Assert.Equal("Tests::sample", test.QualifiedName);
		Assert.Equal("tests/sample.camp", test.Sourcefile);
		Assert.Equal(7, test.Sourceline);
		Assert.Equal("summary text", test.Summary);
		Assert.True(test.Skipped);
		Assert.Equal("skip reason", test.SkipReason);
		Assert.Equal("valid", test.RunnerSignature);
		Assert.Null(test.TestNameParameter);

		CampTestManifestEntry factoryTest = Assert.Single(roundTrip.FactoryTests);
		Assert.Equal("Tests::sampleFactory", factoryTest.Id);
		Assert.Equal("sampleFactory", factoryTest.Name);
		Assert.Equal(14, factoryTest.Sourceline);
		Assert.Equal("factory summary", factoryTest.Summary);
		Assert.False(factoryTest.Skipped);
		Assert.Null(factoryTest.SkipReason);
		Assert.Equal("valid", factoryTest.RunnerSignature);
		Assert.Equal("testname", factoryTest.TestNameParameter);
	}

	[Fact]
	public void Manifest_parser_accepts_version_1_manifests_with_no_factory_tests()
	{
		const string version1Manifest = """
			{
			  "format": "camp.test-manifest",
			  "version": 1,
			  "mode": "in-module",
			  "tests": [
			    {
			      "id": "Tests::sample",
			      "name": "sample",
			      "qualifiedName": "Tests::sample",
			      "sourcefile": "tests/sample.camp",
			      "sourceline": 7,
			      "summary": "",
			      "skipped": false,
			      "skipReason": null,
			      "runnerSignature": "valid"
			    }
			  ]
			}
			""";

		bool parsed = CampTestManifestJsonSerializer.TryParse(version1Manifest, out CampTestManifest manifest, out List<string> diagnostics);

		Assert.True(parsed, string.Join(Environment.NewLine, diagnostics));
		Assert.Single(manifest.Tests);
		Assert.Empty(manifest.FactoryTests);
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
