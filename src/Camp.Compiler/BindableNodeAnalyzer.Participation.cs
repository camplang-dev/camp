using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void BeginParticipationPhase(Module module)
	{
		currentModule = module;
		RefreshParticipation(module);
	}

	void EndParticipationPhase()
	{
		phaseParticipation = null;
	}

	void RefreshParticipation(Module module)
	{
		phaseParticipation = new DeclarationParticipation(module);
		directBaseClassCache.Clear();
		containingTypeCache.Clear();
		closestVirtualImplementationCache.Clear();
	}

	DeclarationParticipation CurrentParticipation()
	{
		if (currentModule is null)
			throw new InvalidOperationException("Declaration participation requires an active module.");
		return phaseParticipation ??= new DeclarationParticipation(currentModule);
	}

	IEnumerable<Definition> ActiveDefinitions(Module module)
	{
		// Semantic analysis must retain declarations that are unavailable in the
		// selected configuration so configured(...) branches can prove and resolve
		// their requirements. Callers that select emitted/discovered definitions use
		// DeclarationParticipation's phase-local active list instead.
		return module.Definitions;
	}

	IEnumerable<Definition> ActiveCurrentDefinitions()
	{
		return currentModule is null ? [] : ActiveDefinitions(currentModule);
	}

	bool IsActiveDefinition(Definition definition)
	{
		return currentModule is null || CurrentParticipation().Includes(definition, currentModule.DeclarationParticipationMode);
	}
}
