using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed class DeclarationExpansionResult(Module module, IReadOnlyList<AnalysisDiagnostic> diagnostics, BindableNodeAnalyzer analyzer)
{
	public Module Module { get; } = module;
	public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; } = diagnostics;
	internal BindableNodeAnalyzer Analyzer { get; } = analyzer;
	public bool Success => !Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}

public static class BindableNodeExpander
{
	public static DeclarationExpansionResult Expand(Module module)
	{
		return Expand(module, selectedTarget: null);
	}

	public static DeclarationExpansionResult Expand(Module module, TargetDefinition? selectedTarget)
	{
		return Expand(module, selectedTarget, configurationFlags: null);
	}

	public static DeclarationExpansionResult Expand(Module module, TargetDefinition? selectedTarget, ConfigurationFlagSet? configurationFlags)
	{
		ArgumentNullException.ThrowIfNull(module);
		return BindableNodeAnalyzer.ExpandDeclarations(module, selectedTarget, configurationFlags);
	}
}
