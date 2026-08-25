using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	IEnumerable<Definition> ActiveDefinitions(Module module)
	{
		return module.Definitions;
	}

	IEnumerable<Definition> ActiveCurrentDefinitions()
	{
		return currentModule is null ? [] : ActiveDefinitions(currentModule);
	}

	bool IsActiveDefinition(Definition definition)
	{
		return currentModule is null || DeclarationParticipation.Includes(definition, currentModule);
	}
}
