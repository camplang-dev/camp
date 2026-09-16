using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class CampFactoryTestSignatureTests
{
	[Fact]
	public void Ordinary_parameters_with_trailing_thrown_slot_are_valid()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/factory_signature_ordinary.camp", """
			struct Assertion
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			@factorytest
			void ordinaryFactory(@testname string testname, int first, int second, thrown Assertion* assertion)
			{
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		FunctionDefinition function = SemanticCompiler.Function(compilation, "ordinaryFactory");
		bool valid = CampTestDiscovery.TryGetFactoryTestSignature(compilation.Module, function, out TestFailureShape? failureShape, out TestAllocatorShape? allocatorShape);

		Assert.True(valid);
		Assert.NotNull(failureShape);
		Assert.Null(allocatorShape);
	}

	[Fact]
	public void Implicit_within_allocator_immediately_before_thrown_is_valid()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/factory_signature_implicit_allocator.camp", """
			struct Assertion
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			struct Allocator
			{
			}

			@factorytest
			void allocatorFactory(@testname string testname, within allocator, thrown Assertion* assertion)
			{
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		FunctionDefinition function = SemanticCompiler.Function(compilation, "allocatorFactory");
		bool valid = CampTestDiscovery.TryGetFactoryTestSignature(compilation.Module, function, out TestFailureShape? failureShape, out TestAllocatorShape? allocatorShape);

		Assert.True(valid);
		Assert.NotNull(failureShape);
		Assert.NotNull(allocatorShape);
	}

	[Fact]
	public void Explicit_within_allocator_immediately_before_thrown_is_valid()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/factory_signature_explicit_allocator.camp", """
			struct Assertion
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			struct Allocator
			{
			}

			@factorytest
			void allocatorFactory(within Allocator* arena, thrown Assertion* assertion)
			{
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		FunctionDefinition function = SemanticCompiler.Function(compilation, "allocatorFactory");
		bool valid = CampTestDiscovery.TryGetFactoryTestSignature(compilation.Module, function, out TestFailureShape? failureShape, out TestAllocatorShape? allocatorShape);

		Assert.True(valid);
		Assert.NotNull(failureShape);
		Assert.NotNull(allocatorShape);
	}

	[Fact]
	public void Non_void_return_is_invalid()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/factory_signature_nonvoid.camp", """
			struct Assertion
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			@factorytest
			int nonVoidFactory(thrown Assertion* assertion)
			{
				return 0;
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		FunctionDefinition function = SemanticCompiler.Function(compilation, "nonVoidFactory");
		bool valid = CampTestDiscovery.TryGetFactoryTestSignature(compilation.Module, function, out _, out _);

		Assert.False(valid);
	}

	[Fact]
	public void Missing_thrown_slot_is_invalid()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/factory_signature_missing_thrown.camp", """
			@factorytest
			void missingThrownFactory(int value)
			{
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		FunctionDefinition function = SemanticCompiler.Function(compilation, "missingThrownFactory");
		bool valid = CampTestDiscovery.TryGetFactoryTestSignature(compilation.Module, function, out _, out _);

		Assert.False(valid);
	}

	[Fact]
	public void Wrong_thrown_type_is_invalid()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/factory_signature_wrong_thrown.camp", """
			struct NotAssertion
			{
				string text;
			}

			@factorytest
			void wrongThrownFactory(thrown NotAssertion* problem)
			{
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		FunctionDefinition function = SemanticCompiler.Function(compilation, "wrongThrownFactory");
		bool valid = CampTestDiscovery.TryGetFactoryTestSignature(compilation.Module, function, out _, out _);

		Assert.False(valid);
	}

	[Fact]
	public void Ordinary_parameter_after_allocator_is_invalid()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/factory_signature_parameter_after_allocator.camp", """
			struct Assertion
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			struct Allocator
			{
			}

			@factorytest
			void parameterAfterAllocatorFactory(within allocator, int value, thrown Assertion* assertion)
			{
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		FunctionDefinition function = SemanticCompiler.Function(compilation, "parameterAfterAllocatorFactory");
		bool valid = CampTestDiscovery.TryGetFactoryTestSignature(compilation.Module, function, out _, out _);

		Assert.False(valid);
	}

	[Fact]
	public void Extern_factory_test_is_invalid()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/factory_signature_extern.camp", """
			struct Assertion
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			@factorytest
			extern void externFactory(thrown Assertion* assertion);
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		FunctionDefinition function = SemanticCompiler.Function(compilation, "externFactory");
		bool valid = CampTestDiscovery.TryGetFactoryTestSignature(compilation.Module, function, out _, out _);

		Assert.False(valid);
	}

	[Fact]
	public void Generic_factory_test_is_invalid()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/factory_signature_generic.camp", """
			struct Assertion
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			@factorytest
			void genericFactory<T: any>(thrown Assertion* assertion)
			{
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		FunctionDefinition function = SemanticCompiler.Function(compilation, "genericFactory");
		bool valid = CampTestDiscovery.TryGetFactoryTestSignature(compilation.Module, function, out _, out _);

		Assert.False(valid);
	}

	[Fact]
	public void Async_factory_test_is_invalid()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/factory_signature_async.camp", """
			struct Assertion
			{
				escaped string message;
				escaped string sourcefile;
				uint sourceline;
			}

			@factorytest
			@noawait
			async void asyncFactory(thrown Assertion* assertion)
			{
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		FunctionDefinition function = SemanticCompiler.Function(compilation, "asyncFactory");
		bool valid = CampTestDiscovery.TryGetFactoryTestSignature(compilation.Module, function, out _, out _);

		Assert.False(valid);
	}

	[Fact]
	public void Iterator_factory_test_is_invalid()
	{
		SemanticCompilation compilation = SemanticCompiler.CompileLoweredTestModule(("tests/factory_signature_iterator.camp", """
			@factorytest
			struct iter int iteratorFactory()
			{
				yield break;
			}
			"""));
		SemanticCompiler.AssertNoDiagnostics(compilation);

		FunctionDefinition function = SemanticCompiler.Function(compilation, "iteratorFactory");
		bool valid = CampTestDiscovery.TryGetFactoryTestSignature(compilation.Module, function, out _, out _);

		Assert.False(valid);
	}
}
