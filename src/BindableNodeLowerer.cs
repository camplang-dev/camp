using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed class LoweringResult(Module module, IReadOnlyList<AnalysisDiagnostic> diagnostics)
{
	public Module Module { get; } = module;
	public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; } = diagnostics;
	public bool Success => Diagnostics.Count == 0;
}

public static class BindableNodeLowerer
{
	public static LoweringResult Lower(DeclarationExpansionResult expansion)
	{
		ArgumentNullException.ThrowIfNull(expansion);
		return BindableNodeAnalyzer.LowerExpanded(expansion);
	}
}
