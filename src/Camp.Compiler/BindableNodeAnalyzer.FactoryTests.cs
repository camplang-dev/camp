using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	const string FactoryBeginFactorySymbol = "__camp_test_beginFactory";
	const string FactoryEndFactorySymbol = "__camp_test_endFactory";
	const string FactoryRecordSkippedSymbol = "__camp_test_recordFactorySkipped";

	// Camp-level names for the synthetic extern declarations that call into the
	// Stage FT.7 harness runtime. @symbol fixes the real C symbol regardless of
	// these names; the "__camp" prefix keeps them out of the way of ordinary
	// user identifiers.
	const string FactoryBeginFactoryName = "__campFactoryTestBegin";
	const string FactoryEndFactoryName = "__campFactoryTestEnd";
	const string FactoryRecordSkippedName = "__campFactoryTestRecordSkipped";
	const string FactoryCatchVariableName = "__campFactoryAssertion";

	// Stage FT.8: wraps each valid @factorytest body with self-reporting
	// instrumentation calling the Stage FT.7 runtime primitives, and declares
	// those primitives as synthetic extern functions so ordinary call-argument
	// binding/conversion and C emission handle them exactly like any other
	// extern call. Runs during declaration expansion (pass 1) so the generated
	// extern declarations flow through the same declaration analysis (pass 2)
	// as real source, and the body-wrapping runs at the end of declaration
	// analysis (after signature/placement validation) so it can use resolved
	// parameter types and skip anything with an invalid signature.
	void GenerateFactoryTestRuntimeDeclarations(Module module)
	{
		FunctionDefinition? anchor = module.Definitions.OfType<FunctionDefinition>().FirstOrDefault(DeclarationParticipation.IsFactoryTest);
		if (anchor is null)
			return;

		module.Definitions.Add(CreateFactoryRuntimeExternFunction(FactoryBeginFactoryName, FactoryBeginFactorySymbol, "int", anchor, ("displayName", "const char[]")));
		module.Definitions.Add(CreateFactoryRuntimeExternFunction(FactoryEndFactoryName, FactoryEndFactorySymbol, "void", anchor, ("failed", "int")));
		module.Definitions.Add(CreateFactoryRuntimeExternFunction(FactoryRecordSkippedName, FactoryRecordSkippedSymbol, "void", anchor, ("displayName", "const char[]")));
	}

	static FunctionDefinition CreateFactoryRuntimeExternFunction(string name, string symbol, string returnTypeName, FunctionDefinition anchor, params (string Name, string TypeName)[] parameters)
	{
		FunctionDefinition function = new()
		{
			Name = name,
			Symbol = symbol,
			SymbolOverridden = true,
			Extern = "",
			Internal = "",
			ReturnType = TypeReferenceForResolvedName(returnTypeName),
			ResolvedType = returnTypeName,
			EffectiveRequirement = ConfigurationFlagExpressionBinder.Flag("TEST_MODULE"),
			// Anchor ownership to the triggering @factorytest's own source file so
			// this declaration lands in the shared project private header exactly
			// like an ordinary user-authored extern declaration would.
			Provenance = new NodeProvenance(anchor.SourceSyntax, null, "factory test runtime helper")
		};
		function.Attributes.Add(new AttributeConstructor { Name = "@testonly" });
		foreach ((string parameterName, string typeName) in parameters)
		{
			function.Parameters.Add(new ParameterDefinition
			{
				Name = parameterName,
				Type = TypeReferenceForResolvedName(typeName),
				ResolvedType = typeName,
				EffectiveRequirement = function.EffectiveRequirement
			});
		}
		return function;
	}

	void WrapFactoryTestBodies(Module module)
	{
		// Collect first: CreateInstrumentedFactoryTestBody adds a new definition
		// to module.Definitions for each wrapped function, and mutating a list
		// while enumerating it is unsafe.
		List<FunctionDefinition> factoryTests = [];
		foreach (Definition definition in module.Definitions)
		{
			if (definition is not FunctionDefinition function || !DeclarationParticipation.IsFactoryTest(function) || function.Body is null)
				continue;
			if (!CampTestDiscovery.TryGetFactoryTestSignature(module, function, out TestFailureShape? failureShape, out _) || failureShape is null)
				continue;
			factoryTests.Add(function);
		}

		foreach (FunctionDefinition function in factoryTests)
		{
			Expression displayName = BuildFactoryDisplayNameExpression(function);
			function.Body = HasAttribute(function.Attributes, SkipAttributeName)
				? CreateSkippedFactoryTestBody(displayName)
				: CreateInstrumentedFactoryTestBody(module, function, displayName);
		}
	}

	Expression BuildFactoryDisplayNameExpression(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
			if (HasAttribute(parameter.Attributes, TestNameAttributeName))
				return CreateVariableReference(parameter, parameter.ResolvedType ?? "string");
		return NameOfStringLiteral(function.Name, function.SourceSyntax, "string");
	}

	BlockStatement CreateSkippedFactoryTestBody(Expression displayName)
	{
		return CreateBlock(
		[
			CreateFactoryRuntimeCallStatement(FactoryRecordSkippedName, "void", displayName)
		]);
	}

	// Moves the original body into a new, private function with the same
	// parameter list, then has the wrapper call it with an explicit,
	// pre-zeroed local plus a "catch" argument redirect instead of a
	// try/catch statement. This works around a pre-existing compiler defect
	// (BUG-160: a try/catch's own catch-declared variable is not
	// zero-initialized, so a callee that can fall through to the end of its
	// body without an explicit return -- exactly assert()'s own shape --
	// leaves the catch check reading uninitialized stack memory). The
	// explicit-local-plus-catch-argument pattern does not exhibit that bug.
	BlockStatement CreateInstrumentedFactoryTestBody(Module module, FunctionDefinition function, Expression displayName)
	{
		ParameterDefinition thrownParameter = function.Parameters[^1];

		FunctionDefinition originalBodyFunction = new()
		{
			Name = "__campFactoryOriginal_" + function.Name,
			Symbol = "__campFactoryOriginal_" + function.Name,
			SymbolOverridden = true,
			ReturnType = TypeReferenceForResolvedName("void"),
			ResolvedType = "void",
			Body = function.Body,
			EffectiveRequirement = function.EffectiveRequirement,
			Provenance = new NodeProvenance(function.SourceSyntax, null, "factory test original body"),
			// Without this, DeclarationParticipation.IsTestOnly has no record
			// of this brand-new definition and treats it as an ordinary
			// production coverage subject, inflating the coverage denominator
			// (proposal: generated wrapper/runtime-helper code must be
			// excluded from source coverage). Pointing GeneratedInfo at the
			// original @factorytest function, which is already classified
			// test-only, makes IsTestOnly resolve the same way for this one.
			GeneratedInfo = new GeneratedDeclarationInfo(GeneratedDeclarationCategory.None, "factory test original body", function)
		};
		foreach (ParameterDefinition parameter in function.Parameters)
			originalBodyFunction.Parameters.Add(CloneParameter(parameter));
		module.Definitions.Add(originalBodyFunction);

		DeclarationStatement failureLocal = new() { ResolvedType = "void", InitialValue = new DefaultExpression { ResolvedType = thrownParameter.ResolvedType } };
		failureLocal.Target.Type = CloneType(thrownParameter.Type);
		failureLocal.Target.ResolvedType = thrownParameter.ResolvedType;
		failureLocal.Target.Names.Add(FactoryCatchVariableName);

		CallExpression innerCall = new() { Target = new NamedExpression { Name = originalBodyFunction.Name }, ResolvedType = "void" };
		for (int index = 0; index < function.Parameters.Count - 1; index++)
		{
			ParameterDefinition parameter = function.Parameters[index];
			if (parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition)
				continue; // within context propagates ambiently, not as an ordinary argument
			innerCall.Arguments.Add(new ArgumentExpression { Value = CreateVariableReference(parameter, parameter.ResolvedType ?? "void") });
		}
		innerCall.Arguments.Add(new ArgumentExpression { Modifier = ArgumentModifier.Catch, Value = CreateVariableReference(failureLocal.Target, thrownParameter.ResolvedType ?? "void") });

		IfStatement failureCheck = new()
		{
			Condition = new BinaryExpression
			{
				Left = CreateVariableReference(failureLocal.Target, thrownParameter.ResolvedType ?? "void"),
				Operator = BinaryOperator.NotEqual,
				Right = new DefaultExpression { ResolvedType = thrownParameter.ResolvedType },
				ResolvedType = "bool"
			},
			Body = CreateBlock(
			[
				CreateFactoryRuntimeCallStatement(FactoryEndFactoryName, "void", NumberLiteral("1", "int")),
				new ReturnStatement { ResolvedType = "void" }
			]),
			ResolvedType = "void"
		};

		BlockStatement ifBody = CreateBlock(
		[
			failureLocal,
			new ExpressionStatement { Expression = innerCall, ResolvedType = "void" },
			failureCheck,
			CreateFactoryRuntimeCallStatement(FactoryEndFactoryName, "void", NumberLiteral("0", "int"))
		]);

		IfStatement ifStatement = new()
		{
			Condition = new BinaryExpression
			{
				Left = CreateFactoryRuntimeCall(FactoryBeginFactoryName, "int", displayName),
				Operator = BinaryOperator.NotEqual,
				Right = NumberLiteral("0", "int"),
				ResolvedType = "bool"
			},
			Body = ifBody,
			ResolvedType = "void"
		};

		return CreateBlock([ifStatement]);
	}

	static ExpressionStatement CreateFactoryRuntimeCallStatement(string functionName, string returnType, params Expression[] arguments)
	{
		return new ExpressionStatement { Expression = CreateFactoryRuntimeCall(functionName, returnType, arguments), ResolvedType = "void" };
	}

	static CallExpression CreateFactoryRuntimeCall(string functionName, string returnType, params Expression[] arguments)
	{
		CallExpression call = new()
		{
			Target = new NamedExpression { Name = functionName },
			ResolvedType = returnType
		};
		foreach (Expression argument in arguments)
			call.Arguments.Add(new ArgumentExpression { Value = argument });
		return call;
	}
}
