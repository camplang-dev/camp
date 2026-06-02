using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed class DeclarationExpansionResult(Module module, IReadOnlyList<AnalysisDiagnostic> diagnostics, BindableNodeAnalyzer analyzer)
{
	public Module Module { get; } = module;
	public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; } = diagnostics;
	internal BindableNodeAnalyzer Analyzer { get; } = analyzer;
	public bool Success => Diagnostics.Count == 0;
}

public static class BindableNodeExpander
{
	public static DeclarationExpansionResult Expand(Module module)
	{
		return Expand(module, selectedTarget: null, selectedMemoryModel: null);
	}

	public static DeclarationExpansionResult Expand(Module module, TargetDefinition? selectedTarget)
	{
		return Expand(module, selectedTarget, selectedMemoryModel: null);
	}

	public static DeclarationExpansionResult Expand(Module module, TargetDefinition? selectedTarget, string? selectedMemoryModel)
	{
		ArgumentNullException.ThrowIfNull(module);
		return BindableNodeAnalyzer.ExpandDeclarations(module, selectedTarget, selectedMemoryModel);
	}
}
