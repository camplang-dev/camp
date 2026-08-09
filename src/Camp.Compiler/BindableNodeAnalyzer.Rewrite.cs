using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly Dictionary<ClassDefinition, List<InterfaceImplementationLowering>> classInterfaceLowerings = [];
	readonly Dictionary<StructDefinition, List<InterfaceImplementationLowering>> structInterfaceLowerings = [];
	readonly Dictionary<ClassDefinition, VirtualClassLowering> virtualClassLowerings = [];
	readonly Dictionary<FunctionDefinition, FunctionDefinition> virtualImplementations = [];
	readonly Dictionary<FunctionDefinition, InterfaceThunkLowering> interfaceThunkLowerings = [];
	readonly Dictionary<InterfaceDefinition, StructDefinition> loweredInterfaceStructs = [];
	readonly Dictionary<InterfaceDefinition, StructDefinition> interfaceIndirectStructs = [];
	readonly List<Definition> generatedInterfaceDefinitions = [];
	readonly HashSet<ClassDefinition> generatedClassInterfaceDeclarations = [];
	readonly List<StructDefinition> generatedLambdaContextDefinitions = [];
	readonly List<FunctionDefinition> generatedLambdaDefinitions = [];
	const string InitNewMethodName = "op_initnew";
	const string CreateMethodName = "create";
	const string DeleteMethodName = "op_delete";
		const string DestroyMethodName = "destroy";
		Expression? currentWithinContext;
		int currentDefaultWithinContextDepth;
		List<Statement>? currentStatementPrefix;
	List<Statement>? currentStatementSuffix;
	bool loweringReceiverExpression;
	DeclarationTarget? currentImplicitCatchTarget;
	readonly List<CleanupScope> currentCleanupScopes = [];
	readonly List<ThrowHandler> currentThrowHandlers = [];
	readonly List<LoopTransferTarget> currentLoopTransferTargets = [];
	FunctionDefinition? currentRewriteFunction;
	TypeDefinition? currentRewriteContainingType;
	string? currentFunctionExitLabel;
	DeclarationTarget? currentFunctionReturnTarget;
	string currentFunctionReturnType = "void";
	bool allocatorSurfaceValidationEnabled;
	int generatedLocalIndex;
	int generatedDiscardIndex;

	public static AnalysisResult AnalyzeAndRewrite(Module module)
	{
		ArgumentNullException.ThrowIfNull(module);

		DeclarationExpansionResult expansion = BindableNodeExpander.Expand(module);
		if (expansion.Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
			return new AnalysisResult(expansion.Module, expansion.Diagnostics);

		LoweringResult lowering = BindableNodeLowerer.Lower(expansion);
		return new AnalysisResult(lowering.Module, lowering.Diagnostics);
	}

	internal static DeclarationExpansionResult ExpandDeclarations(Module module, TargetDefinition? selectedTarget = null)
	{
		BindableNodeAnalyzer analyzer = new(selectedTarget);
		analyzer.RunAnalyzerPass(AnalyzerPass.DeclarationExpansion, module);
		return new DeclarationExpansionResult(module, analyzer.diagnostics, analyzer);
	}

	public static AnalysisResult AnalyzeExpanded(DeclarationExpansionResult expansion, TargetDefinition? selectedTarget = null)
	{
		ArgumentNullException.ThrowIfNull(expansion);
		BindableNodeAnalyzer analyzer = expansion.Analyzer;
		analyzer.RunAnalyzerPass(AnalyzerPass.DeclarationAnalysis, expansion.Module);
		analyzer.RunAnalyzerPass(AnalyzerPass.NodeRewriteApplication, expansion.Module);
		analyzer.FillMissingResolvedTypes(expansion.Module);
		return new AnalysisResult(expansion.Module, analyzer.diagnostics);
	}

	public static AnalysisResult AnalyzeAndRewriteExpanded(DeclarationExpansionResult expansion)
	{
		LoweringResult lowering = BindableNodeLowerer.Lower(expansion);
		return new AnalysisResult(lowering.Module, lowering.Diagnostics);
	}

	internal static LoweringResult LowerExpanded(DeclarationExpansionResult expansion, Action<string, Action>? measure = null)
	{
		ArgumentNullException.ThrowIfNull(expansion);
		BindableNodeAnalyzer analyzer = expansion.Analyzer;
		RunMeasured(measure, "declaration analysis", () => analyzer.RunAnalyzerPass(AnalyzerPass.DeclarationAnalysis, expansion.Module));
		if (!analyzer.diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
		{
			RunMeasured(measure, "method body analysis", () => analyzer.RunAnalyzerPass(AnalyzerPass.MethodBodyAnalysis, expansion.Module));
			if (!analyzer.diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
				RunMeasured(measure, "production dependency validation", () => analyzer.ValidateProductionDeclarationDependencies(expansion.Module));
		}
		RunMeasured(measure, "node rewrite application", () => analyzer.RunAnalyzerPass(AnalyzerPass.NodeRewriteApplication, expansion.Module));
		RunMeasured(measure, "fill resolved types", () => analyzer.FillMissingResolvedTypes(expansion.Module));
		AnalysisResult analysis = new(expansion.Module, analyzer.diagnostics);
		if (analysis.Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
			return new LoweringResult(analysis.Module, analysis.Diagnostics);

		analyzer.allocatorSurfaceValidationEnabled = true;
		RunMeasured(measure, "lowering rewrite", () =>
		{
			analyzer.phaseMeasure = measure;
			try
			{
				analyzer.RunAnalyzerPass(AnalyzerPass.LoweringRewrite, expansion.Module);
			}
			finally
			{
				analyzer.phaseMeasure = null;
			}
		});
		RunMeasured(measure, "fill lowered resolved types", () => analyzer.FillMissingResolvedTypes(expansion.Module));
		return new LoweringResult(expansion.Module, analyzer.diagnostics);
	}

	static void RunMeasured(Action<string, Action>? measure, string name, Action action)
	{
		if (measure is null)
		{
			action();
			return;
		}
		measure(name, action);
	}

	void RewriteModule(Module module)
	{
		generatedLambdaContextDefinitions.Clear();
		generatedLambdaDefinitions.Clear();
		RunMeasured(phaseMeasure, "complete interface declarations", () => CompleteInterfaceDeclarations(module));
		RunMeasured(phaseMeasure, "lower source interface types", () => LowerSourceInterfaceTypes(module));
		RunMeasured(phaseMeasure, "expand params declarations", () => ExpandParamsDeclarations(module));
		RunMeasured(phaseMeasure, "complete implicit destroy bodies", () => CompleteImplicitDestroyBodies(module));
		RunMeasured(phaseMeasure, "rewrite definitions", () =>
		{
			foreach (Definition definition in ActiveDefinitions(module))
				RewriteDefinition(definition);
		});
		RunMeasured(phaseMeasure, "append generated lambdas", () =>
		{
			foreach (StructDefinition context in generatedLambdaContextDefinitions)
				module.Definitions.Add(context);
			foreach (FunctionDefinition lambda in generatedLambdaDefinitions)
				module.Definitions.Add(lambda);
		});
		RunMeasured(phaseMeasure, "refresh lowered resolved types", () => RefreshLoweredResolvedTypes(module));
		RunMeasured(phaseMeasure, "lower interface definitions", () => LowerInterfaceDefinitions(module));
	}

	void CompleteImplicitDestroyBodies(Module module)
	{
		foreach (Definition definition in ActiveDefinitions(module))
		{
			if (definition is not ClassDefinition classDefinition)
				continue;
			foreach (FunctionDefinition function in classDefinition.Functions)
			{
				if (function.Name != DestroyMethodName
					|| function.Body is not null
					|| function.Extern is not null
					|| function.SourceSyntax is not null)
				{
					continue;
				}

				ThisExpression target = new() { ResolvedType = $"{classDefinition.Name}*" };
				CastExpression pointer = new()
				{
					Kind = CastKind.Type,
					Type = PointerTo(VoidType()),
					Expression = target,
					ResolvedType = "void*"
				};
				function.Body = new BlockStatement { ResolvedType = "void" };
				function.Body.Statements.Add(new ExpressionStatement
				{
					ResolvedType = "void",
					Expression = CreateUncheckedGlobalFreeCall(pointer)
				});
			}
		}
	}

}

static class BindableNodeAnalyzerRewriteParameterExtensions
{
	public static List<ArgumentExpression> ArgumentsFromParameters(this FunctionDefinition function, bool skipAllocator = false)
	{
		List<ArgumentExpression> arguments = [];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown or ParameterModifier.Within
				|| parameter is WithinParameterDefinition)
				continue;
			if (skipAllocator && parameter.Name == "allocator")
				continue;

			arguments.Add(new ArgumentExpression
			{
				Value = new VariableReferenceExpression
				{
					Variable = parameter,
					ResolvedType = parameter.ResolvedType
				},
				ResolvedType = parameter.ResolvedType
			});
		}

		return arguments;
	}
}
