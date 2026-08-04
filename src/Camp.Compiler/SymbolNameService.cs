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
	public static string NamespacePrefix(string? namespaceName)
	{
		if (string.IsNullOrWhiteSpace(namespaceName))
			return "";

		string prefix = "";
		foreach (string segment in namespaceName.Split("::", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			prefix = JoinPrefixAndTypeName(prefix, segment);
		return prefix;
	}

	public static string DefaultTypeSymbol(string? namespaceName, string name)
	{
		return JoinPrefixAndTypeName(NamespacePrefix(namespaceName), name);
	}

	public static string DefaultTopLevelSymbol(string? namespaceName, string name)
	{
		string prefix = NamespacePrefix(namespaceName);
		if (string.IsNullOrWhiteSpace(prefix))
			return name;
		if (string.IsNullOrWhiteSpace(name))
			return prefix;
		return prefix + "_" + name;
	}

	public static string DefaultMemberSymbol(string ownerSymbol, string memberName)
	{
		if (string.IsNullOrWhiteSpace(ownerSymbol))
			return memberName;
		if (string.IsNullOrWhiteSpace(memberName))
			return ownerSymbol;
		return ownerSymbol + "_" + memberName;
	}

	public static string DefaultOutOfScopeMemberSymbol(string? namespaceName, string ownerSymbol, string memberName)
	{
		string prefix = NamespacePrefix(namespaceName);
		string effectiveOwner = string.IsNullOrWhiteSpace(prefix)
			? ownerSymbol
			: JoinPrefixAndTypeName(prefix, ownerSymbol);
		return DefaultMemberSymbol(effectiveOwner, memberName);
	}

	static string JoinPrefixAndTypeName(string prefix, string name)
	{
		if (string.IsNullOrWhiteSpace(prefix))
			return name;
		if (string.IsNullOrWhiteSpace(name))
			return prefix;
		return StartsWithUppercaseAscii(name)
			? prefix + name
			: prefix + "_" + name;
	}

	static bool StartsWithUppercaseAscii(string text)
	{
		foreach (char ch in text)
			return ch is >= 'A' and <= 'Z';
		return false;
	}

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
			&& !function.SymbolOverridden
			&& getExplicitThisParameter(function) is null)
		{
			string fullCallableSymbol = DefaultTopLevelSymbol(function.Namespace, function.FullCallableName);
			if (fullCallableSymbol != definition.Symbol)
				yield return new DeclarationName(DeclarationNameKind.FullCallableSymbol, fullCallableSymbol);
		}
	}

	public static bool IsSameSymbol(Definition definition)
	{
		return string.IsNullOrWhiteSpace(definition.Symbol)
			|| definition.Symbol == definition.Name
			|| !string.IsNullOrWhiteSpace(definition.DefaultSymbol) && definition.Symbol == definition.DefaultSymbol;
	}
}
