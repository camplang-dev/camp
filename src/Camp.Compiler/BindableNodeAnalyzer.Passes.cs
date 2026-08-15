using System;

namespace Camp.Compiler;

internal enum AnalyzerPass
{
	DeclarationExpansion,
	DeclarationAnalysis,
	MethodBodyAnalysis,
	NodeRewriteApplication,
	LoweringRewrite
}

public sealed partial class BindableNodeAnalyzer
{
	// Analyzer pass order is deliberately centralized here so future tools can reuse
	// earlier phases without accidentally running lowering-only rewrites.
	//
	// 1. DeclarationExpansion creates compiler-owned declarations that participate in
	//    ordinary binding: iterator state, lifecycle helpers, virtual declarations,
	//    and interface accessors/vtables.
	// 2. DeclarationAnalysis binds types, signatures, attributes, inheritance,
	//    implementations, inline constants, visibility, and duplicate symbols.
	// 3. MethodBodyAnalysis binds source method bodies once declarations are stable.
	// 4. NodeRewriteApplication applies analyzer rewrites produced while binding.
	// 5. LoweringRewrite performs ABI/lowering transforms after successful analysis.
	void RunAnalyzerPass(AnalyzerPass pass, Module module)
	{
		ArgumentNullException.ThrowIfNull(module);

		switch (pass)
		{
			case AnalyzerPass.DeclarationExpansion:
				RunDeclarationExpansionPass(module);
				break;

			case AnalyzerPass.DeclarationAnalysis:
				AnalyzeDeclarations(module);
				break;

			case AnalyzerPass.MethodBodyAnalysis:
				AnalyzeMethodBodies(module);
				break;

			case AnalyzerPass.NodeRewriteApplication:
				ApplyNodeRewrites(module);
				break;

			case AnalyzerPass.LoweringRewrite:
				RewriteModule(module);
				break;

			default:
				throw new ArgumentOutOfRangeException(nameof(pass), pass, null);
		}
	}

	void RunDeclarationExpansionPass(Module module)
	{
		currentModule = module;
		CollectTypeNames(module);
		CollectAliasNames(module);
		ResolveAliases();
		AnalyzeExportProjections(module);
		CollectTypeNames(module);
		PrecomputeOverloadCallableNames(module);
		AddRetainedAllocatorFields(module);
		GenerateIteratorDeclarations(module);
		GenerateLifecycleMethods(module);
		GenerateVirtualDeclarations(module);
		GenerateInterfaceDeclarations(module);
	}
}
