using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void ValidateParameterLifetimeRelations(List<ParameterDefinition> parameters)
	{
		Dictionary<string, ParameterDefinition> named = new(System.StringComparer.Ordinal);
		foreach (ParameterDefinition parameter in parameters)
			if (!string.IsNullOrWhiteSpace(parameter.Name))
				named.TryAdd(parameter.Name, parameter);

		foreach (ParameterDefinition parameter in parameters)
		{
			if (!TryParseLifetimeFact(parameter.LifetimeBinding, out LifetimeFact fact) || fact.Anchors.Count == 0)
				continue;
			foreach (string anchor in fact.Anchors)
			{
				if (parameter.Modifier == ParameterModifier.Thrown)
					Report(GetRange(parameter.Type?.SourceSyntax ?? parameter.SourceSyntax), $"Lifetime anchor '{anchor}' cannot constrain a thrown slot; thrown values must outlive the throwing activation.");
				else if (named.TryGetValue(anchor, out ParameterDefinition? source)
					&& source.Modifier is ParameterModifier.Out or ParameterModifier.Thrown or ParameterModifier.Within)
					Report(GetRange(parameter.Type?.SourceSyntax ?? parameter.SourceSyntax), $"Lifetime anchor '{anchor}' must name an input value or receiver, not an out, thrown, or within slot.");
			}
			if (HasLifetimeCycle(parameter, parameter, named, []))
				Report(GetRange(parameter.Type?.SourceSyntax ?? parameter.SourceSyntax), $"Circular lifetime anchors on parameter '{parameter.Name}' require circular substitution.");
		}
	}

	static bool HasLifetimeCycle(ParameterDefinition start, ParameterDefinition current, Dictionary<string, ParameterDefinition> named, HashSet<ParameterDefinition> visited)
	{
		if (!visited.Add(current) || !TryParseLifetimeFact(current.LifetimeBinding, out LifetimeFact fact))
			return false;
		foreach (string anchor in fact.Anchors)
			if (named.TryGetValue(anchor, out ParameterDefinition? next)
				&& (ReferenceEquals(start, next) || HasLifetimeCycle(start, next, named, visited)))
				return true;
		return false;
	}
}
