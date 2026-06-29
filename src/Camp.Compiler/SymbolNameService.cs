using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public enum DeclarationNameKind
{
	Source,
	Callable,
	Invoker,
	Symbol,
	FullCallableSymbol,
	CIdentifier,
	Api,
	Metadata
}

public readonly record struct DeclarationName(DeclarationNameKind Kind, string Value);

public sealed class SymbolCollisionSet
{
	readonly Dictionary<string, Definition> symbols = new(StringComparer.Ordinal);
	readonly Dictionary<string, string> components = new(StringComparer.Ordinal);

	public bool TryAddSymbol(string symbol, Definition definition, out string? componentOwner)
	{
		componentOwner = null;
		if (string.IsNullOrWhiteSpace(symbol))
			return true;
		if (components.TryGetValue(symbol, out componentOwner))
			return false;
		if (symbols.TryGetValue(symbol, out Definition? existing) && !ReferenceEquals(existing, definition))
			return false;
		symbols[symbol] = definition;
		return true;
	}

	public bool TryAddComponent(string component, string ownerName)
	{
		if (string.IsNullOrWhiteSpace(component))
			return true;
		if (symbols.ContainsKey(component) || components.ContainsKey(component))
			return false;
		components[component] = ownerName;
		return true;
	}
}

public static class SymbolNameService
{
	public static DeclarationName SourceName(Definition definition)
	{
		return new DeclarationName(DeclarationNameKind.Source, definition.Name);
	}

	public static DeclarationName CallableName(FunctionDefinition function)
	{
		return new DeclarationName(DeclarationNameKind.Callable, string.IsNullOrWhiteSpace(function.FullCallableName) ? function.Name : function.FullCallableName);
	}

	public static DeclarationName InvokerName(FunctionDefinition function)
	{
		return new DeclarationName(DeclarationNameKind.Invoker, string.IsNullOrWhiteSpace(function.InvokerName) ? function.Name : function.InvokerName);
	}

	public static DeclarationName SymbolName(Definition definition)
	{
		return new DeclarationName(DeclarationNameKind.Symbol, string.IsNullOrWhiteSpace(definition.Symbol) ? definition.Name : definition.Symbol);
	}

	public static IEnumerable<DeclarationName> TopLevelSymbolNames(Definition definition, Func<FunctionDefinition, ThisParameterDefinition?> getExplicitThisParameter)
	{
		if (!string.IsNullOrWhiteSpace(definition.Symbol))
			yield return new DeclarationName(DeclarationNameKind.Symbol, definition.Symbol);

		if (definition is FunctionDefinition function
			&& !string.IsNullOrWhiteSpace(function.FullCallableName)
			&& function.FullCallableName != definition.Symbol
			&& !function.SymbolOverridden
			&& getExplicitThisParameter(function) is null)
			yield return new DeclarationName(DeclarationNameKind.FullCallableSymbol, function.FullCallableName);
	}

	public static bool IsSameSymbol(Definition definition)
	{
		return string.IsNullOrWhiteSpace(definition.Symbol) || definition.Symbol == definition.Name;
	}
}
