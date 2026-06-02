using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

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
	const string InitNewMethodName = "op_initnew";
	const string CreateMethodName = "create";
	const string DeleteMethodName = "op_delete";
	const string DestroyMethodName = "destroy";
	Expression? currentWithinContext;
	List<Statement>? currentStatementPrefix;
	List<Statement>? currentStatementSuffix;
	DeclarationTarget? currentImplicitCatchTarget;
	readonly List<CleanupScope> currentCleanupScopes = [];
	readonly List<ThrowHandler> currentThrowHandlers = [];
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
		if (expansion.Diagnostics.Count > 0)
			return new AnalysisResult(expansion.Module, expansion.Diagnostics);

		LoweringResult lowering = BindableNodeLowerer.Lower(expansion);
		return new AnalysisResult(lowering.Module, lowering.Diagnostics);
	}

	internal static DeclarationExpansionResult ExpandDeclarations(Module module, TargetDefinition? selectedTarget = null)
	{
		BindableNodeAnalyzer analyzer = new(selectedTarget);
		analyzer.currentModule = module;
		analyzer.CollectTypeNames(module);
		analyzer.GenerateLifecycleMethods(module);
		analyzer.GenerateVirtualDeclarations(module);
		analyzer.GenerateInterfaceDeclarations(module);
		return new DeclarationExpansionResult(module, analyzer.diagnostics, analyzer);
	}

	public static AnalysisResult AnalyzeExpanded(DeclarationExpansionResult expansion, TargetDefinition? selectedTarget = null)
	{
		return AnalyzeDeclarationsExpanded(expansion, selectedTarget);
	}

	public static AnalysisResult AnalyzeDeclarationsExpanded(DeclarationExpansionResult expansion, TargetDefinition? selectedTarget = null)
	{
		ArgumentNullException.ThrowIfNull(expansion);
		BindableNodeAnalyzer analyzer = expansion.Analyzer;
		analyzer.AnalyzeDeclarations(expansion.Module);
		analyzer.ApplyNodeRewrites(expansion.Module);
		analyzer.FillMissingResolvedTypes(expansion.Module);
		return new AnalysisResult(expansion.Module, analyzer.diagnostics);
	}

	public static AnalysisResult AnalyzeAndRewriteExpanded(DeclarationExpansionResult expansion)
	{
		LoweringResult lowering = BindableNodeLowerer.Lower(expansion);
		return new AnalysisResult(lowering.Module, lowering.Diagnostics);
	}

	internal static LoweringResult LowerExpanded(DeclarationExpansionResult expansion)
	{
		ArgumentNullException.ThrowIfNull(expansion);
		BindableNodeAnalyzer analyzer = expansion.Analyzer;
		analyzer.AnalyzeDeclarations(expansion.Module);
		if (analyzer.diagnostics.Count == 0)
			analyzer.AnalyzeMethodBodies(expansion.Module);
		analyzer.ApplyNodeRewrites(expansion.Module);
		analyzer.FillMissingResolvedTypes(expansion.Module);
		AnalysisResult analysis = new(expansion.Module, analyzer.diagnostics);
		if (analysis.Diagnostics.Count > 0)
			return new LoweringResult(analysis.Module, analysis.Diagnostics);

		analyzer.allocatorSurfaceValidationEnabled = true;
		analyzer.RewriteModule(expansion.Module);
		analyzer.FillMissingResolvedTypes(expansion.Module);
		return new LoweringResult(expansion.Module, analyzer.diagnostics);
	}

	void RewriteModule(Module module)
	{
		CompleteInterfaceDeclarations(module);
		LowerSourceInterfaceTypes(module);
		ExpandParamsDeclarations(module);
		foreach (Definition definition in module.Definitions)
			RewriteDefinition(definition);
		RefreshLoweredResolvedTypes(module);
		LowerInterfaceDefinitions(module);
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
				|| parameter is WithinParameterDefinition or VTableOfParameterDefinition)
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
