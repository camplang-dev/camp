using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Camp.Compiler;

public enum CampSymbolKind
{
	Unknown,
	Type,
	Function,
	Method,
	Property,
	Field,
	Component,
	Variable,
	Parameter,
	EnumValue,
	Alias,
	Keyword
}

public sealed record CampSymbolLocation(string Path, CampTextRange Range);

public sealed record CampReference(string Path, CampTextRange Range, bool IsDeclaration);

public sealed record CampSymbolInfo(
	string Name,
	CampSymbolKind Kind,
	string? Type,
	CampSymbolLocation? Definition,
	string? Signature,
	string? Documentation);

public sealed record CampDocumentSymbol(
	string Name,
	CampSymbolKind Kind,
	CampTextRange Range,
	CampTextRange SelectionRange,
	string? Detail,
	IReadOnlyList<CampDocumentSymbol> Children);

public sealed record CampWorkspaceSymbol(
	string Name,
	CampSymbolKind Kind,
	CampSymbolLocation Location,
	string? ContainerName);

public sealed record CampHover(string Markdown);

public sealed record CampParameterHelp(
	string Label,
	string? Documentation);

public sealed record CampSignatureInformation(
	string Label,
	string? Documentation,
	IReadOnlyList<CampParameterHelp> Parameters);

public sealed record CampSignatureHelp(
	IReadOnlyList<CampSignatureInformation> Signatures,
	int ActiveSignature,
	int ActiveParameter);

public sealed record CampCompletionItem(
	string Label,
	CampSymbolKind Kind,
	string? Detail,
	string? Documentation,
	string? InsertText = null,
	bool IsSnippet = false);

public sealed class CampSymbolQueryService(CampAnalysisSnapshot snapshot)
{
	readonly List<SymbolEntry> entries = BuildEntries(snapshot.Compilation);
	readonly List<FunctionDefinition> functions = BuildFunctions(snapshot.Compilation);

	public CampSymbolInfo? GetSymbolAt(string path, CampTextPosition position)
	{
		SymbolEntry? entry = FindSymbolEntry(path, position);
		return entry is null ? null : ToInfo(entry);
	}

	public CampSymbolLocation? GetDefinition(string path, CampTextPosition position)
	{
		return GetSymbolAt(path, position)?.Definition;
	}

	public IReadOnlyList<CampReference> GetReferences(string path, CampTextPosition position, bool includeDeclaration)
	{
		SymbolEntry? selected = FindSymbolEntry(path, position);
		if (selected is null)
			return [];
		SymbolEntry? target = selected.Definition ?? (selected.IsDeclaration ? selected : null);
		if (target is null || !IsSourceBacked(target))
			return [];
		List<CampReference> references = entries
			.Where(entry =>
			{
				SymbolEntry? identity = entry.Definition ?? (entry.IsDeclaration ? entry : null);
				if (identity is null || !SameSymbolIdentity(identity, target))
					return false;
				return includeDeclaration || !entry.IsDeclaration;
			})
			.Select(entry => new CampReference(entry.Path, entry.Range, entry.IsDeclaration))
			.DistinctBy(static reference => (
				Path.GetFullPath(reference.Path).ToUpperInvariant(),
				reference.Range.Start.Line,
				reference.Range.Start.Character,
				reference.Range.End.Line,
				reference.Range.End.Character,
				reference.IsDeclaration))
			.OrderBy(static reference => reference.Path, StringComparer.OrdinalIgnoreCase)
			.ThenBy(static reference => reference.Range.Start.Line)
			.ThenBy(static reference => reference.Range.Start.Character)
			.ToList();
		return RemoveNestedDuplicateReferences(references);
	}

	public CampHover? GetHover(string path, CampTextPosition position)
	{
		CampSymbolInfo? symbol = GetSymbolAt(path, position);
		if (symbol is null)
			return null;

		StringBuilder builder = new();
		if (!string.IsNullOrWhiteSpace(symbol.Signature))
		{
			builder.AppendLine("```camp");
			builder.AppendLine(symbol.Signature);
			builder.AppendLine("```");
		}
		else
			builder.Append("**").Append(symbol.Kind).Append("** `").Append(symbol.Name).AppendLine("`");

		if (!string.IsNullOrWhiteSpace(symbol.Type) && symbol.Signature is null)
			builder.AppendLine().Append("Type: `").Append(symbol.Type).AppendLine("`");
		if (!string.IsNullOrWhiteSpace(symbol.Documentation))
			builder.AppendLine().AppendLine(symbol.Documentation);
		return new CampHover(builder.ToString().TrimEnd());
	}

	public CampSignatureHelp? GetSignatureHelp(string path, CampTextPosition position, string? currentText = null)
	{
		string fullPath = Path.GetFullPath(path);
		SourceFile? file = snapshot.Compilation.Files.FirstOrDefault(file => string.Equals(Path.GetFullPath(file.Path), fullPath, StringComparison.OrdinalIgnoreCase));
		if (file?.BindableTree is null)
			return null;
		if (currentText is not null && currentText != file.Text
			&& GetSignatureHelpFromText(file, currentText, position) is CampSignatureHelp currentTextHelp)
			return currentTextHelp;
		if (!TryFindCallAt(file, file.BindableTree, position, out CallExpression? call))
			return GetSignatureHelpFromText(file, currentText ?? file.Text, position);
		List<FunctionDefinition> callFunctions = GetCallFunctions(call!, functions).Distinct().ToList();
		if (callFunctions.Count == 0)
			return GetSignatureHelpFromText(file, currentText ?? file.Text, position);
		int activeParameter = GetActiveParameter(currentText ?? file.Text, call!, position, callFunctions[0]);
		return new CampSignatureHelp(
			callFunctions.Select(function => CreateSignatureInformation(function, file)).ToList(),
			0,
			activeParameter);
	}

	public IReadOnlyList<CampCompletionItem> GetCompletions(string path, CampTextPosition position, string? currentText = null, bool requireFinallyForWhitespaceTrigger = false)
	{
		string fullPath = Path.GetFullPath(path);
		SourceFile? file = snapshot.Compilation.Files.FirstOrDefault(file => string.Equals(Path.GetFullPath(file.Path), fullPath, StringComparison.OrdinalIgnoreCase));
		if (file?.BindableTree is null)
			return [];
		CompletionContext context = GetCompletionContext(currentText ?? file.Text, position);
		if (context.OverrideModifier is not null)
			return GetOverrideCompletions(file, position, currentText ?? file.Text)
				.OrderBy(static item => item.Label, StringComparer.OrdinalIgnoreCase)
				.ThenBy(static item => item.Detail, StringComparer.OrdinalIgnoreCase)
				.ToList();
		List<CampCompletionItem> completions = context.IsMember
			? GetMemberCompletions(file, context)
			: GetScopeCompletions(file, position);
		if (requireFinallyForWhitespaceTrigger && context.IsWhitespaceTrigger && !context.IsAfterFinally && context.OverrideModifier is null)
			return [];
		return CollapseCompletionOverloads(completions
			.Where(item => context.Prefix.Length == 0 || item.Label.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase))
			.OrderBy(static item => CompletionSortBucket(item.Kind))
			.ThenBy(static item => item.Label, StringComparer.OrdinalIgnoreCase)
			.ToList());
	}

	public IReadOnlyList<CampDocumentSymbol> GetDocumentSymbols(string path)
	{
		string fullPath = Path.GetFullPath(path);
		SourceFile? file = snapshot.Compilation.Files.FirstOrDefault(file => string.Equals(Path.GetFullPath(file.Path), fullPath, StringComparison.OrdinalIgnoreCase));
		if (file?.BindableTree is null)
			return [];
		return BuildDocumentSymbols(file, file.BindableTree);
	}

	public IReadOnlyList<CampWorkspaceSymbol> GetWorkspaceSymbols(string query)
	{
		string trimmed = query.Trim();
		return entries
			.Where(static entry => entry.Definition is null)
			.DistinctBy(static entry => (entry.Name, entry.Kind, entry.Path, entry.Range.Start.Line, entry.Range.Start.Character, entry.Range.End.Line, entry.Range.End.Character))
			.Where(entry => trimmed.Length == 0 || entry.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
			.Select(static entry => new CampWorkspaceSymbol(
				entry.Name,
				entry.Kind,
				new CampSymbolLocation(entry.Path, entry.Range),
				entry.ContainerName))
			.OrderBy(static symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(static symbol => symbol.Location.Path, StringComparer.OrdinalIgnoreCase)
			.ThenBy(static symbol => symbol.Location.Range.Start.Line)
			.ThenBy(static symbol => symbol.Location.Range.Start.Character)
			.ToList();
	}

	List<CampCompletionItem> GetScopeCompletions(SourceFile file, CampTextPosition position)
	{
		List<CampCompletionItem> completions = [];
		HashSet<BindableNode> localNodes = [];
		if (file.BindableTree is not null)
			CollectVisibleLocalNodes(file, file.BindableTree, position, localNodes, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance));
		foreach (BindableNode node in localNodes)
			if (TryCreateCompletion(file, node, null, out CampCompletionItem? item))
				completions.Add(item!);
		completions.AddRange(entries
			.Where(static entry => entry.Definition is null && entry.Kind is CampSymbolKind.Type or CampSymbolKind.Alias or CampSymbolKind.Function)
			.Select(static entry => new CampCompletionItem(entry.Name, entry.Kind, entry.Signature ?? entry.Type, entry.Documentation)));
		completions.AddRange(GetKeywordCompletions());
		return completions;
	}

	List<CampCompletionItem> GetMemberCompletions(SourceFile file, CompletionContext context)
	{
		if (context.MemberTargetPosition is null)
			return [];
		CampSymbolInfo? target = GetSymbolAt(file.Path, context.MemberTargetPosition);
		string? targetType = context.MemberTargetText == "this"
			? ResolveThisCompletionTarget(file, context.MemberTargetPosition)
			: target?.Type;
		targetType ??= context.MemberTargetText is null ? null : ResolveSourceDeclaredParamsShape(file.Text, context.MemberTargetText, context.MemberTargetPosition);
		targetType ??= context.MemberTargetText is null ? null : ResolveCompletionTargetName(context.MemberTargetText);
		if (targetType is null && target?.Kind is CampSymbolKind.Type or CampSymbolKind.Alias)
			targetType = target.Name;
		List<CampCompletionItem> completions = [];
		if (!string.IsNullOrWhiteSpace(targetType))
			completions.AddRange(GetParamsComponentCompletions(targetType!));
		TypeDefinition? type = ResolveCompletionType(targetType);
		if (type is null)
			return completions;
		bool staticOnly = !string.IsNullOrWhiteSpace(context.MemberTargetText) && ResolveCompletionType(context.MemberTargetText) is not null;
		CollectTypeMemberCompletions(file, type, completions, new HashSet<TypeDefinition>(ReferenceEqualityComparer.Instance), staticOnly);
		return completions;
	}

	List<CampCompletionItem> GetOverrideCompletions(SourceFile file, CampTextPosition position, string text)
	{
		ClassDefinition? containingClass = FindContainingClass(file, position, text);
		if (containingClass is null)
			return [];
		HashSet<string> alreadyOverridden = containingClass.Functions
			.Where(static function => function.Modifier is FunctionModifier.Override or FunctionModifier.Sealed)
			.Select(static function => GetOverrideIdentity(function))
			.ToHashSet(StringComparer.Ordinal);
		List<CampCompletionItem> completions = [];
		foreach (FunctionDefinition inherited in GetOverridableClassMembers(containingClass, new HashSet<ClassDefinition>(ReferenceEqualityComparer.Instance)))
		{
			string identity = GetOverrideIdentity(inherited);
			if (alreadyOverridden.Contains(identity))
				continue;
			string? signature = GetSignatureHelpLabel(inherited);
			string? insertText = CreateOverrideSnippet(inherited);
			if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(insertText))
				continue;
			completions.Add(new CampCompletionItem(
				GetSurfaceFunctionName(inherited) ?? BindableNodeAnalyzer.GetCallableName(inherited),
				CampSymbolKind.Method,
				signature,
				GetDeclarationDocumentation(inherited),
				insertText,
				IsSnippet: true));
		}
		return completions;
	}

	ClassDefinition? FindContainingClass(SourceFile file, CampTextPosition position, string text)
	{
		string? className = ResolveThisTypeFromText(text, position);
		if (!string.IsNullOrWhiteSpace(className) && ResolveCompletionType(className) is ClassDefinition textClass)
			return textClass;
		ClassDefinition? result = null;
		CampTextRange? resultRange = null;
		foreach (SourceFile sourceFile in snapshot.Compilation.Files)
		{
			if (sourceFile.BindableTree is null)
				continue;
			foreach (ClassDefinition classDefinition in Descendants(sourceFile.BindableTree, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance)).OfType<ClassDefinition>())
				if (TryGetNodeRange(classDefinition, out TokenRange range)
					&& ReferenceEquals(file.Tokens, range.Sequence)
					&& Contains(CampLanguageService.ToTextRange(range), position))
				{
					CampTextRange textRange = CampLanguageService.ToTextRange(range);
					if (resultRange is null || SpanSize(textRange) < SpanSize(resultRange))
					{
						result = classDefinition;
						resultRange = textRange;
					}
				}
		}
		if (result is not null)
			return result;
		return result;
	}

	static IEnumerable<BindableNode> Descendants(BindableNode node, HashSet<BindableNode> visited)
	{
		if (!visited.Add(node))
			yield break;
		yield return node;
		foreach (BindableNode child in Children(node))
			foreach (BindableNode descendant in Descendants(child, visited))
				yield return descendant;
	}

	IEnumerable<FunctionDefinition> GetOverridableClassMembers(ClassDefinition classDefinition, HashSet<ClassDefinition> visited)
	{
		foreach (TypeReference baseType in classDefinition.BaseTypes)
		{
			if (ResolveCompletionType(baseType.ResolvedType ?? BindableNodeCodeSerializer.SerializeType(baseType)) is not ClassDefinition baseClass
				|| !visited.Add(baseClass))
				continue;
			foreach (FunctionDefinition inherited in GetOverridableClassMembers(baseClass, visited))
				yield return inherited;
			foreach (FunctionDefinition function in baseClass.Functions)
			{
				if (function.Modifier is not FunctionModifier.Virtual and not FunctionModifier.Abstract)
					continue;
				if (function.Modifier is FunctionModifier.Sealed || function.Modifier is FunctionModifier.Static or FunctionModifier.Constructor or FunctionModifier.Destructor)
					continue;
				if (IsHiddenMemberCompletionFunction(function) || IsDestructorName(function.Name))
					continue;
				yield return function;
			}
		}
	}

	static string GetOverrideIdentity(FunctionDefinition function)
	{
		string name = BindableNodeAnalyzer.GetCallableName(function);
		string parameters = string.Join("|", GetVisibleCallParameters(function).Select(static parameter =>
			(parameter.IsOverloadSelector ? "overload " : "") + FormatParameterLabel(parameter)));
		return name + "(" + parameters + ")";
	}

	static bool IsDestructorName(string? name)
	{
		return !string.IsNullOrWhiteSpace(name) && name!.StartsWith("~", StringComparison.Ordinal);
	}

	static string? CreateOverrideSnippet(FunctionDefinition function)
	{
		string? signature = GetSignatureHelpLabel(function);
		if (string.IsNullOrWhiteSpace(signature))
			return null;
		signature = StripOverrideSnippetModifiers(signature!.Trim());
		return signature + "\n{\n\t$0\n}";
	}

	static string StripOverrideSnippetModifiers(string signature)
	{
		string result = signature;
		while (true)
		{
			string trimmed = result.TrimStart();
			int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
			if (space <= 0)
				return trimmed;
			string word = trimmed[..space];
			if (word is not ("export" or "internal" or "public" or "extern" or "virtual" or "abstract" or "override" or "sealed"))
				return trimmed;
			result = trimmed[(space + 1)..];
		}
	}

	static IEnumerable<CampCompletionItem> GetKeywordCompletions()
	{
		foreach (string keyword in new[]
		{
			"if", "else", "while", "for", "foreach", "return", "try", "catch", "finally",
			"new", "init", "default", "true", "false", "null", "delete", "using", "namespace", "export", "internal",
			"class", "struct", "interface", "enum", "newtype", "delegate", "fn"
		})
			yield return new CampCompletionItem(keyword, CampSymbolKind.Keyword, null, null);
	}

	static IReadOnlyList<CampCompletionItem> CollapseCompletionOverloads(List<CampCompletionItem> completions)
	{
		List<CampCompletionItem> result = [];
		foreach (IGrouping<(string Label, CampSymbolKind Kind), CampCompletionItem> group in completions.GroupBy(static item => (item.Label, item.Kind)))
		{
			List<CampCompletionItem> items = group.DistinctBy(static item => item.Detail).ToList();
			if (items.Count == 1 || group.Key.Kind is not (CampSymbolKind.Function or CampSymbolKind.Method))
			{
				result.AddRange(items);
				continue;
			}
			CampCompletionItem first = items[0];
			result.Add(first with { Detail = $"{group.Key.Kind}: {first.Label} ({items.Count} overloads)" });
		}
		return result;
	}

	static int CompletionSortBucket(CampSymbolKind kind)
	{
		return kind switch
		{
			CampSymbolKind.Variable or CampSymbolKind.Parameter => 0,
			CampSymbolKind.Field or CampSymbolKind.Property or CampSymbolKind.Component => 1,
			CampSymbolKind.Method or CampSymbolKind.Function => 2,
			CampSymbolKind.EnumValue => 3,
			CampSymbolKind.Type or CampSymbolKind.Alias => 4,
			CampSymbolKind.Keyword => 5,
			_ => 9
		};
	}

	SymbolEntry? FindEntry(string path, CampTextPosition position)
	{
		string fullPath = Path.GetFullPath(path);
		return entries
			.Where(entry => string.Equals(entry.Path, fullPath, StringComparison.OrdinalIgnoreCase) && Contains(entry.Range, position))
			.OrderBy(entry => SpanSize(entry.Range))
			.FirstOrDefault();
	}

	SymbolEntry? FindSymbolEntry(string path, CampTextPosition position)
	{
		return FindEntry(path, position)
			?? FindNamedDefinitionEntry(path, position)
			?? FindPropertyEntry(path, position)
			?? FindExpandedComponentEntry(path, position);
	}

	SymbolEntry? FindPropertyEntry(string path, CampTextPosition position)
	{
		string fullPath = Path.GetFullPath(path);
		SourceFile? file = snapshot.Compilation.Files.FirstOrDefault(file => string.Equals(Path.GetFullPath(file.Path), fullPath, StringComparison.OrdinalIgnoreCase));
		if (file is null || !TryGetWordAt(file.Text, position, out string? word) || string.IsNullOrWhiteSpace(word))
			return null;
		string getterName = "get" + word;
		List<SymbolEntry> candidates = entries
			.Where(entry => entry.Definition is null && entry.Kind == CampSymbolKind.Method && entry.Name == getterName)
			.DistinctBy(static entry => (entry.Name, entry.Kind, entry.Path, entry.Range.Start.Line, entry.Range.Start.Character, entry.Range.End.Line, entry.Range.End.Character))
			.ToList();
		return candidates.Count == 1 ? candidates[0] : null;
	}

	TypeDefinition? ResolveCompletionType(string? type)
	{
		if (string.IsNullOrWhiteSpace(type))
			return null;
		string name = BaseTypeName(UnwrapStorageType(type!));
		foreach (SourceFile sourceFile in snapshot.Compilation.Files)
			if (sourceFile.BindableTree is not null && FindTypeDefinition(sourceFile.BindableTree, name, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance)) is TypeDefinition definition)
				return definition;
		return null;
	}

	static TypeDefinition? FindTypeDefinition(BindableNode node, string name, HashSet<BindableNode> visited)
	{
		if (!visited.Add(node))
			return null;
		if (node is TypeDefinition type && (type.Name == name || type.Symbol == name || type.ResolvedType == name))
			return type;
		foreach (BindableNode child in Children(node))
			if (FindTypeDefinition(child, name, visited) is TypeDefinition found)
				return found;
		return null;
	}

	static string UnwrapStorageType(string type)
	{
		string result = type.Trim();
		while (result.StartsWith("const ", StringComparison.Ordinal)
			|| result.StartsWith("escaped ", StringComparison.Ordinal)
			|| result.StartsWith("scoped ", StringComparison.Ordinal)
			|| result.StartsWith("unscoped ", StringComparison.Ordinal)
			|| result.StartsWith("volatile ", StringComparison.Ordinal))
		{
			int space = result.IndexOf(' ');
			result = space < 0 ? result : result[(space + 1)..].TrimStart();
		}
		while (result.EndsWith("*", StringComparison.Ordinal))
			result = result[..^1].TrimEnd();
		if (result.EndsWith("[]", StringComparison.Ordinal))
			result = result[..^2].TrimEnd();
		return result;
	}

	string? ResolveThisCompletionTarget(SourceFile file, CampTextPosition? position)
	{
		if (position is not CampTextPosition textPosition)
			return null;
		FunctionDefinition? containing = null;
		CampTextRange? containingRange = null;
		FunctionDefinition? preceding = null;
		CampTextRange? precedingRange = null;
		foreach (FunctionDefinition function in functions)
		{
			ThisParameterDefinition? thisParameter = GetCompletionThisParameter(function);
			if (thisParameter?.ResolvedType is null || !TryGetNodeRange(function, out TokenRange range) || !ReferenceEquals(file.Tokens, range.Sequence))
				continue;
			CampTextRange textRange = CampLanguageService.ToTextRange(range);
			if (Contains(textRange, textPosition))
			{
				if (containingRange is null || SpanSize(textRange) < SpanSize(containingRange))
				{
					containing = function;
					containingRange = textRange;
				}
				continue;
			}
			if (IsBefore(textRange.Start, textPosition)
				&& (precedingRange is null || IsBefore(precedingRange.Start, textRange.Start)))
			{
				preceding = function;
				precedingRange = textRange;
			}
		}
		FunctionDefinition? selected = containing ?? preceding;
		string? receiverType = selected is null ? null : ResolveFunctionReceiverType(selected);
		return receiverType ?? ResolveThisTypeFromText(file.Text, textPosition);
	}

	static ThisParameterDefinition? GetCompletionThisParameter(FunctionDefinition function)
	{
		return function.Parameters.OfType<ThisParameterDefinition>().FirstOrDefault()
			?? function.EffectiveThisParameter;
	}

	string? ResolveFunctionReceiverType(FunctionDefinition function)
	{
		if (GetCompletionThisParameter(function)?.ResolvedType is string receiverType && !string.IsNullOrWhiteSpace(receiverType))
			return receiverType;
		int separator = function.Symbol.IndexOf('_', StringComparison.Ordinal);
		if (separator <= 0)
			return null;
		string containerName = function.Symbol[..separator];
		return ResolveCompletionType(containerName) is null ? null : containerName;
	}

	string? ResolveThisTypeFromText(string text, CampTextPosition position)
	{
		int cursor = OffsetOf(text, position);
		if (cursor < 0)
			return null;
		string? result = null;
		int search = 0;
		while (search < cursor && TryFindTypeDeclarationBefore(text, search, cursor, out string? name, out int openBrace))
		{
			if (IsBraceOpenAt(text, openBrace, cursor) && ResolveCompletionType(name) is not null)
				result = name;
			search = openBrace + 1;
		}
		return result;
	}

	static bool TryFindTypeDeclarationBefore(string text, int start, int limit, out string name, out int openBrace)
	{
		name = "";
		openBrace = -1;
		for (int i = start; i < limit;)
		{
			if (!IsIdentifierStart(text[i]))
			{
				i++;
				continue;
			}
			int keywordStart = i;
			i++;
			while (i < limit && IsIdentifierPart(text[i]))
				i++;
			string keyword = text[keywordStart..i];
			if (keyword is not ("class" or "struct" or "interface" or "newtype"))
				continue;
			int nameStart = i;
			while (nameStart < limit && char.IsWhiteSpace(text[nameStart]))
				nameStart++;
			if (nameStart >= limit || !IsIdentifierStart(text[nameStart]))
				continue;
			int nameEnd = nameStart + 1;
			while (nameEnd < limit && IsIdentifierPart(text[nameEnd]))
				nameEnd++;
			int brace = text.IndexOf('{', nameEnd);
			if (brace < 0 || brace >= limit)
				continue;
			name = text[nameStart..nameEnd];
			openBrace = brace;
			return true;
		}
		return false;
	}

	static bool IsBraceOpenAt(string text, int openBrace, int cursor)
	{
		int depth = 0;
		for (int i = openBrace; i < cursor && i < text.Length; i++)
		{
			if (text[i] == '{')
				depth++;
			else if (text[i] == '}')
				depth--;
		}
		return depth > 0;
	}

	void CollectTypeMemberCompletions(SourceFile file, TypeDefinition type, List<CampCompletionItem> completions, HashSet<TypeDefinition> visited, bool staticOnly = false)
	{
		if (!visited.Add(type))
			return;
		switch (type)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
					if ((!staticOnly || field.Modifier == FieldModifier.Static) && TryCreateCompletion(file, field, type.Name, out CampCompletionItem? item))
						completions.Add(item!);
				foreach (FunctionDefinition function in classDefinition.Functions)
					if ((!staticOnly || function.Modifier == FunctionModifier.Static))
						AddFunctionCompletions(file, function, type.Name, completions);
				foreach (TypeReference baseType in classDefinition.BaseTypes)
					CollectBaseTypeMemberCompletions(file, baseType, completions, visited, staticOnly);
				break;
			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					if ((!staticOnly || field.Modifier == FieldModifier.Static) && TryCreateCompletion(file, field, type.Name, out CampCompletionItem? item))
						completions.Add(item!);
				foreach (FunctionDefinition function in structDefinition.Functions)
					if ((!staticOnly || function.Modifier == FunctionModifier.Static))
						AddFunctionCompletions(file, function, type.Name, completions);
				break;
			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					if ((!staticOnly || function.Modifier == FunctionModifier.Static))
						AddFunctionCompletions(file, function, type.Name, completions);
				break;
			case EnumDefinition enumDefinition:
				foreach (VariableDefinition value in enumDefinition.Values)
					if (TryCreateCompletion(file, value, type.Name, out CampCompletionItem? item))
						completions.Add(item!);
				break;
			case NewtypeDefinition newtypeDefinition:
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					if ((!staticOnly || function.Modifier == FunctionModifier.Static))
						AddFunctionCompletions(file, function, type.Name, completions);
				break;
		}

		if (staticOnly)
		{
			foreach (FunctionDefinition extension in functions.Where(function => function.Modifier == FunctionModifier.Static && function.OutOfScopeOwnerName == type.Name))
				AddFunctionCompletions(file, extension, type.Name, completions);
			return;
		}
		foreach (FunctionDefinition extension in functions.Where(function => function.Parameters.FirstOrDefault() is ThisParameterDefinition thisParameter && BaseTypeName(UnwrapStorageType(thisParameter.ResolvedType ?? "")) == type.Name))
			AddFunctionCompletions(file, extension, type.Name, completions);
	}

	static void AddFunctionCompletions(SourceFile file, FunctionDefinition function, string containerName, List<CampCompletionItem> completions)
	{
		if (IsHiddenMemberCompletionFunction(function))
			return;
		if (TryCreateCompletion(file, function, containerName, out CampCompletionItem? item))
			completions.Add(item!);
		if (TryCreatePropertyCompletion(function, out CampCompletionItem? propertyItem))
			completions.Add(propertyItem!);
	}

	static bool TryCreatePropertyCompletion(FunctionDefinition function, out CampCompletionItem? item)
	{
		item = null;
		if (function.Parameters.Any(static parameter => parameter.Modifier == ParameterModifier.Prep))
			return false;
		string functionName = BindableNodeAnalyzer.GetCallableName(function);
		if (!TryGetPropertyAccessorName(functionName, out string? propertyName))
			return false;
		string? type = functionName.StartsWith("get", StringComparison.Ordinal)
			? BindableNodeCodeSerializer.SerializeType(function.ReturnType)
			: GetVisibleCallParameters(function).LastOrDefault() is ParameterDefinition parameter
				? BindableNodeCodeSerializer.SerializeType(parameter.Type)
				: null;
		item = new CampCompletionItem(
			propertyName!,
			CampSymbolKind.Property,
			string.IsNullOrWhiteSpace(type) ? "Property" : "Property: " + type,
			GetDocumentation(function));
		return true;
	}

	static bool TryGetPropertyAccessorName(string? functionName, out string? propertyName)
	{
		propertyName = null;
		if (string.IsNullOrWhiteSpace(functionName) || functionName.Length <= 3)
			return false;
		if (!functionName.StartsWith("get", StringComparison.Ordinal) && !functionName.StartsWith("set", StringComparison.Ordinal))
			return false;
		if (!char.IsUpper(functionName[3]))
			return false;
		propertyName = functionName[3..];
		return true;
	}

	static bool IsHiddenMemberCompletionFunction(FunctionDefinition function)
	{
		if (function.OutOfScopeOwnerName is not null)
			return false;
		return function.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor
			|| function.Name is "create" or "destroy" or "op_initnew" or "op_delete"
			|| function.Symbol.EndsWith("_create", StringComparison.Ordinal)
			|| function.Symbol.EndsWith("_destroy", StringComparison.Ordinal)
			|| function.Symbol.EndsWith("_op_initnew", StringComparison.Ordinal)
			|| function.Symbol.EndsWith("_op_delete", StringComparison.Ordinal);
	}

	void CollectBaseTypeMemberCompletions(SourceFile file, TypeReference? baseType, List<CampCompletionItem> completions, HashSet<TypeDefinition> visited, bool staticOnly = false)
	{
		TypeDefinition? definition = ResolveCompletionType(baseType?.ResolvedType ?? BindableNodeCodeSerializer.SerializeType(baseType));
		if (definition is not null)
			CollectTypeMemberCompletions(file, definition, completions, visited, staticOnly);
	}

	string? ResolveCallTargetType(SourceFile file, string text, CampTextPosition position, string targetExpression)
	{
		string[] parts = targetExpression
			.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length == 0)
			return null;
		string? currentType = parts[0] == "this"
			? ResolveThisCompletionTarget(file, position)
			: ResolveCompletionType(parts[0])?.Name ?? ResolveCompletionTargetName(parts[0]);
		for (int i = 1; i < parts.Length && !string.IsNullOrWhiteSpace(currentType); i++)
			currentType = ResolveMemberResultType(currentType!, parts[i], new HashSet<TypeDefinition>(ReferenceEqualityComparer.Instance));
		return string.IsNullOrWhiteSpace(currentType) ? null : BaseTypeName(UnwrapStorageType(currentType!));
	}

	string? ResolveMemberResultType(string targetType, string memberName, HashSet<TypeDefinition> visited)
	{
		TypeDefinition? type = ResolveCompletionType(targetType);
		if (type is null || !visited.Add(type))
			return null;
		switch (type)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
					if (field.Name == memberName)
						return BindableNodeCodeSerializer.SerializeType(field.Type) ?? field.ResolvedType;
				foreach (FunctionDefinition function in classDefinition.Functions)
					if (FunctionNameMatches(function, "get" + memberName) || FunctionNameMatches(function, memberName))
						return BindableNodeCodeSerializer.SerializeType(function.ReturnType) ?? function.ReturnType?.ResolvedType;
				foreach (TypeReference baseType in classDefinition.BaseTypes)
					if (ResolveMemberResultType(baseType.ResolvedType ?? BindableNodeCodeSerializer.SerializeType(baseType), memberName, visited) is string baseResult)
						return baseResult;
				break;
			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					if (field.Name == memberName)
						return BindableNodeCodeSerializer.SerializeType(field.Type) ?? field.ResolvedType;
				foreach (FunctionDefinition function in structDefinition.Functions)
					if (FunctionNameMatches(function, "get" + memberName) || FunctionNameMatches(function, memberName))
						return BindableNodeCodeSerializer.SerializeType(function.ReturnType) ?? function.ReturnType?.ResolvedType;
				break;
			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					if (FunctionNameMatches(function, "get" + memberName) || FunctionNameMatches(function, memberName))
						return BindableNodeCodeSerializer.SerializeType(function.ReturnType) ?? function.ReturnType?.ResolvedType;
				break;
			case NewtypeDefinition newtypeDefinition:
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					if (FunctionNameMatches(function, "get" + memberName) || FunctionNameMatches(function, memberName))
						return BindableNodeCodeSerializer.SerializeType(function.ReturnType) ?? function.ReturnType?.ResolvedType;
				break;
		}
		foreach (FunctionDefinition extension in functions.Where(function =>
			function.Parameters.FirstOrDefault() is ThisParameterDefinition thisParameter
			&& BaseTypeName(UnwrapStorageType(thisParameter.ResolvedType ?? "")) == type.Name
			&& (FunctionNameMatches(function, "get" + memberName) || FunctionNameMatches(function, memberName))))
		{
			return BindableNodeCodeSerializer.SerializeType(extension.ReturnType) ?? extension.ReturnType?.ResolvedType;
		}
		return null;
	}

	IEnumerable<CampCompletionItem> GetParamsComponentCompletions(string targetType)
	{
		string type = StripLeadingStorageQualifiers(targetType);
		TypeDefinition? definition = ResolveCompletionType(type);
		if (definition is NewtypeDefinition { UnderlyingType: not null } newtype)
			type = newtype.UnderlyingType.ResolvedType ?? BindableNodeCodeSerializer.SerializeType(newtype.UnderlyingType);

		if (TryGetArrayElementTypeName(type, out string? elementType))
		{
			yield return new CampCompletionItem("elements", CampSymbolKind.Component, "Component: " + elementType + "*", null);
			yield return new CampCompletionItem("length", CampSymbolKind.Component, "Component: nuint", null);
			yield break;
		}

		if (TryGetOptionalElementTypeName(type, out string? optionalType))
		{
			yield return new CampCompletionItem("value", CampSymbolKind.Component, "Component: " + optionalType, null);
			yield return new CampCompletionItem("specified", CampSymbolKind.Component, "Component: bool", null);
			yield break;
		}

		if (CallableShapeService.TryParseCallableShape(type, out CallableShape callable)
			&& callable.Kind is "delegate" or "once" or "async" or "iter")
		{
			yield return new CampCompletionItem("call", CampSymbolKind.Component, "Component", null);
			yield return new CampCompletionItem("context", CampSymbolKind.Component, "Component: void*", null);
			yield break;
		}

		if (type.TrimStart().StartsWith("iter ", StringComparison.Ordinal))
		{
			yield return new CampCompletionItem("call", CampSymbolKind.Component, "Component", null);
			yield return new CampCompletionItem("context", CampSymbolKind.Component, "Component: void*", null);
		}
	}

	static string StripLeadingStorageQualifiers(string type)
	{
		string result = type.Trim();
		while (result.StartsWith("const ", StringComparison.Ordinal)
			|| result.StartsWith("escaped ", StringComparison.Ordinal)
			|| result.StartsWith("scoped ", StringComparison.Ordinal)
			|| result.StartsWith("unscoped ", StringComparison.Ordinal)
			|| result.StartsWith("volatile ", StringComparison.Ordinal))
		{
			int space = result.IndexOf(' ');
			result = space < 0 ? result : result[(space + 1)..].TrimStart();
		}
		return result;
	}

	static string? ResolveSourceDeclaredParamsShape(string text, string name, CampTextPosition? position)
	{
		if (position is not CampTextPosition textPosition)
			return null;
		int limit = OffsetOf(text, textPosition);
		if (limit < 0)
			return null;
		string beforeCursor = text[..Math.Min(limit, text.Length)];
		string[] lines = beforeCursor.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
		for (int i = lines.Length - 1; i >= 0; i--)
		{
			string line = lines[i];
			int nameIndex = line.LastIndexOf(name, StringComparison.Ordinal);
			if (nameIndex < 0 || !IdentifierAt(line, nameIndex, name))
				continue;
			string beforeName = line[..nameIndex].TrimEnd();
			int equals = beforeName.LastIndexOf('=');
			if (equals >= 0)
				beforeName = beforeName[(equals + 1)..].TrimStart();
			string type = LastSourceTypeToken(beforeName);
			if (TryGetArrayElementTypeName(type, out _) || TryGetOptionalElementTypeName(type, out _))
				return NormalizeSourceTypeWhitespace(type);
		}
		return null;
	}

	static bool IdentifierAt(string text, int index, string name)
	{
		if (index < 0 || index + name.Length > text.Length)
			return false;
		bool left = index == 0 || !IsIdentifierPart(text[index - 1]);
		bool right = index + name.Length == text.Length || !IsIdentifierPart(text[index + name.Length]);
		return left && right;
	}

	static string LastSourceTypeToken(string text)
	{
		string trimmed = text.Trim();
		int start = trimmed.Length;
		int depth = 0;
		for (int i = trimmed.Length - 1; i >= 0; i--)
		{
			char value = trimmed[i];
			if (value is ']' or '>' or ')')
				depth++;
			else if (value is '[' or '<' or '(')
				depth = Math.Max(0, depth - 1);
			else if (char.IsWhiteSpace(value) && depth == 0)
				break;
			start = i;
		}
		return trimmed[start..];
	}

	static string NormalizeSourceTypeWhitespace(string type)
	{
		return type.Replace(" []", "[]", StringComparison.Ordinal)
			.Replace("[ ", "[", StringComparison.Ordinal)
			.Replace(" ]", "]", StringComparison.Ordinal)
			.Replace(" ?", "?", StringComparison.Ordinal);
	}

	static bool TryGetArrayElementTypeName(string type, out string? elementType)
	{
		elementType = null;
		string trimmed = type.Trim();
		if (trimmed.EndsWith("[]", StringComparison.Ordinal))
		{
			elementType = trimmed[..^2].TrimEnd();
			return elementType.Length > 0;
		}
		if (!trimmed.EndsWith("]", StringComparison.Ordinal))
			return false;
		int open = trimmed.LastIndexOf('[');
		if (open <= 0)
			return false;
		string lengthText = trimmed[(open + 1)..^1].Trim();
		if (lengthText.Length == 0 || !lengthText.All(char.IsDigit))
			return false;
		elementType = trimmed[..open].TrimEnd();
		return elementType.Length > 0;
	}

	static bool TryGetOptionalElementTypeName(string type, out string? elementType)
	{
		elementType = null;
		string trimmed = type.Trim();
		if (!trimmed.EndsWith("?", StringComparison.Ordinal))
			return false;
		elementType = trimmed[..^1].TrimEnd();
		return elementType.Length > 0;
	}

	static bool TryCreateCompletion(SourceFile file, BindableNode node, string? containerName, out CampCompletionItem? item)
	{
		item = null;
		if (TryCreateDefinitionEntry(file, node, containerName, out SymbolEntry? entry))
		{
			item = new CampCompletionItem(entry!.Name, entry.Kind, entry.Signature ?? entry.Type, entry.Documentation);
			return true;
		}
		if (node is not Definition definition || string.IsNullOrWhiteSpace(definition.Name))
			return false;
		item = new CampCompletionItem(
			definition.Name,
			GetDefinitionKind(node, containerName),
			GetSignature(node) ?? GetNodeType(node),
			GetDocumentation(node));
		return true;
	}

	static void CollectVisibleLocalNodes(SourceFile file, BindableNode node, CampTextPosition position, HashSet<BindableNode> destination, HashSet<BindableNode> visited)
	{
		if (!visited.Add(node))
			return;
		if (node is ParameterDefinition or LambdaParameter or DeclarationTarget)
		{
			if (TryGetNodeRange(node, out TokenRange range) && ReferenceEquals(file.Tokens, range.Sequence) && IsBefore(CampLanguageService.ToTextRange(range).Start, position))
				destination.Add(node);
		}
		foreach (BindableNode child in Children(node))
			CollectVisibleLocalNodes(file, child, position, destination, visited);
	}

	static bool IsBefore(CampTextPosition left, CampTextPosition right)
	{
		return left.Line < right.Line || left.Line == right.Line && left.Character <= right.Character;
	}

	SymbolEntry? FindNamedDefinitionEntry(string path, CampTextPosition position)
	{
		string fullPath = Path.GetFullPath(path);
		SourceFile? file = snapshot.Compilation.Files.FirstOrDefault(file => string.Equals(Path.GetFullPath(file.Path), fullPath, StringComparison.OrdinalIgnoreCase));
		if (file is null || !TryGetWordAt(file.Text, position, out string? word) || string.IsNullOrWhiteSpace(word))
			return null;
		if (ResolveSourceDeclaredParamsShape(file.Text, word!, position) is string sourceParamsType)
		{
			SymbolEntry? local = entries
				.Where(entry => string.Equals(entry.Path, fullPath, StringComparison.OrdinalIgnoreCase)
					&& entry.IsDeclaration
					&& entry.Name == word
					&& entry.Kind is CampSymbolKind.Variable or CampSymbolKind.Parameter
					&& IsBefore(entry.Range.Start, position))
				.OrderByDescending(entry => entry.Range.Start.Line)
				.ThenByDescending(entry => entry.Range.Start.Character)
				.FirstOrDefault();
			if (local is not null)
				return local with { Type = sourceParamsType };
		}
		List<SymbolEntry> candidates = entries
			.Where(entry => entry.Definition is null && entry.Name == word && entry.Kind is CampSymbolKind.Type or CampSymbolKind.Alias or CampSymbolKind.Field or CampSymbolKind.EnumValue)
			.DistinctBy(static entry => (entry.Name, entry.Kind, entry.Path, entry.Range.Start.Line, entry.Range.Start.Character, entry.Range.End.Line, entry.Range.End.Character))
			.ToList();
		return candidates.Count == 1 ? candidates[0] : null;
	}

	SymbolEntry? FindExpandedComponentEntry(string path, CampTextPosition position)
	{
		string fullPath = Path.GetFullPath(path);
		SourceFile? file = snapshot.Compilation.Files.FirstOrDefault(file => string.Equals(Path.GetFullPath(file.Path), fullPath, StringComparison.OrdinalIgnoreCase));
		if (file is null || !TryGetWordRangeAt(file.Text, position, out string? word, out CampTextRange range) || word is not ("length" or "elements"))
			return null;
		string[] lines = file.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
		if (range.Start.Line < 0 || range.Start.Line >= lines.Length || range.Start.Character == 0 || lines[range.Start.Line][range.Start.Character - 1] != '.')
			return null;
		return new SymbolEntry(
			Path.GetFullPath(file.Path),
			range,
			word!,
			CampSymbolKind.Component,
			word == "length" ? "nuint" : null,
			null,
			null,
			null,
			false,
			null);
	}

	static CampSymbolInfo ToInfo(SymbolEntry entry)
	{
		SymbolEntry definition = entry.Definition ?? entry;
		return new CampSymbolInfo(
			definition.Name,
			definition.Kind,
			definition.Type,
			new CampSymbolLocation(definition.Path, definition.Range),
			definition.Signature,
			definition.Documentation);
	}

	static bool IsSourceBacked(SymbolEntry entry)
	{
		return !string.IsNullOrWhiteSpace(entry.Path)
			&& entry.Range.End.Line >= entry.Range.Start.Line
			&& (entry.Range.End.Line > entry.Range.Start.Line || entry.Range.End.Character > entry.Range.Start.Character);
	}

	static bool SameSymbolIdentity(SymbolEntry left, SymbolEntry right)
	{
		return string.Equals(Path.GetFullPath(left.Path), Path.GetFullPath(right.Path), StringComparison.OrdinalIgnoreCase)
			&& left.Range.Start.Line == right.Range.Start.Line
			&& left.Range.Start.Character == right.Range.Start.Character
			&& left.Range.End.Line == right.Range.End.Line
			&& left.Range.End.Character == right.Range.End.Character
			&& left.Kind == right.Kind
			&& left.Name == right.Name;
	}

	static IReadOnlyList<CampReference> RemoveNestedDuplicateReferences(List<CampReference> references)
	{
		return references
			.Where(reference => !references.Any(other =>
				!ReferenceEquals(reference, other)
				&& reference.IsDeclaration == other.IsDeclaration
				&& string.Equals(Path.GetFullPath(reference.Path), Path.GetFullPath(other.Path), StringComparison.OrdinalIgnoreCase)
				&& ((reference.Range.Start.Line == other.Range.Start.Line && reference.Range.Start.Character == other.Range.Start.Character)
					|| Contains(reference.Range, other.Range.Start) && Contains(reference.Range, PreviousPosition(other.Range.End)))
				&& SpanSize(reference.Range) > SpanSize(other.Range)))
			.ToList();
	}

	static CampTextPosition PreviousPosition(CampTextPosition position)
	{
		return position.Character > 0
			? new CampTextPosition(position.Line, position.Character - 1)
			: position;
	}

	static bool Contains(CampTextRange range, CampTextPosition position)
	{
		if (position.Line < range.Start.Line || position.Line > range.End.Line)
			return false;
		if (position.Line == range.Start.Line && position.Character < range.Start.Character)
			return false;
		if (position.Line == range.End.Line && position.Character >= range.End.Character)
			return false;
		return true;
	}

	static int SpanSize(CampTextRange range)
	{
		return (range.End.Line - range.Start.Line) * 10000 + range.End.Character - range.Start.Character;
	}

	static List<SymbolEntry> BuildEntries(Compilation compilation)
	{
		List<SymbolEntry> entries = [];
		Dictionary<BindableNode, SymbolEntry> definitions = new(ReferenceEqualityComparer.Instance);
		foreach (SourceFile file in compilation.Files)
		{
			if (file.BindableTree is not null)
				CollectDefinitionEntries(file, file.BindableTree, entries, definitions, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance), containerName: null);
		}
		foreach (SourceFile file in compilation.Files)
		{
			if (file.BindableTree is not null)
				CollectReferenceEntries(file, file.BindableTree, entries, definitions, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance));
		}
		return entries;
	}

	static List<FunctionDefinition> BuildFunctions(Compilation compilation)
	{
		List<FunctionDefinition> functions = [];
		HashSet<BindableNode> visited = new(ReferenceEqualityComparer.Instance);
		foreach (SourceFile file in compilation.Files)
			if (file.BindableTree is not null)
				CollectFunctions(file.BindableTree, functions, visited);
		return functions;
	}

	static void CollectFunctions(BindableNode node, List<FunctionDefinition> functions, HashSet<BindableNode> visited)
	{
		if (!visited.Add(node))
			return;
		if (node is FunctionDefinition function)
			functions.Add(function);
		foreach (BindableNode child in Children(node))
			CollectFunctions(child, functions, visited);
	}

	static void CollectDefinitionEntries(SourceFile file, BindableNode node, List<SymbolEntry> entries, Dictionary<BindableNode, SymbolEntry> definitions, HashSet<BindableNode> visited, string? containerName)
	{
		if (!visited.Add(node))
			return;

		foreach (BindableNode child in Children(node))
		{
			if (TryCreateDefinitionEntry(file, child, containerName, out SymbolEntry? entry))
			{
				entries.Add(entry!);
				definitions[child] = entry!;
			}
			string? childContainerName = child is TypeDefinition or EnumDefinition ? ((Definition)child).Name : containerName;
			CollectDefinitionEntries(file, child, entries, definitions, visited, childContainerName);
		}
	}

	static IReadOnlyList<CampDocumentSymbol> BuildDocumentSymbols(SourceFile file, BindableNode root)
	{
		List<CampDocumentSymbol> symbols = [];
		HashSet<BindableNode> visited = new(ReferenceEqualityComparer.Instance);
		foreach (BindableNode child in Children(root))
			AddDocumentSymbol(file, child, symbols, visited, parentIsType: false);
		return symbols;
	}

	static void AddDocumentSymbol(SourceFile file, BindableNode node, List<CampDocumentSymbol> destination, HashSet<BindableNode> visited, bool parentIsType)
	{
		if (!visited.Add(node))
			return;

		if (TryCreateDocumentSymbol(file, node, parentIsType, out CampDocumentSymbol? symbol))
		{
			destination.Add(symbol!);
			return;
		}

		foreach (BindableNode child in Children(node))
			AddDocumentSymbol(file, child, destination, visited, parentIsType: node is TypeDefinition);
	}

	static bool TryCreateDocumentSymbol(SourceFile file, BindableNode node, bool parentIsType, out CampDocumentSymbol? symbol)
	{
		symbol = null;
		string? name = node switch
		{
			Definition definition => definition.Name,
			_ => null
		};
		if (string.IsNullOrWhiteSpace(name) || !TryGetNodeRange(node, out TokenRange selectionRange) || !ReferenceEquals(file.Tokens, selectionRange.Sequence))
			return false;

		List<CampDocumentSymbol> children = [];
		if (node is TypeDefinition or EnumDefinition)
		{
			HashSet<BindableNode> visited = new(ReferenceEqualityComparer.Instance) { node };
			foreach (BindableNode child in Children(node))
				AddDocumentSymbol(file, child, children, visited, parentIsType: true);
		}

		symbol = new CampDocumentSymbol(
			name!,
			GetDocumentSymbolKind(node, parentIsType),
			CampLanguageService.ToTextRange(selectionRange),
			CampLanguageService.ToTextRange(selectionRange),
			GetDocumentSymbolDetail(node),
			children);
		return true;
	}

	static CampSymbolKind GetDocumentSymbolKind(BindableNode node, bool parentIsType)
	{
		if (parentIsType && node is FunctionDefinition)
			return CampSymbolKind.Method;
		return GetKind(node);
	}

	static bool TryCreateDefinitionEntry(SourceFile file, BindableNode node, string? containerName, out SymbolEntry? entry)
	{
		entry = null;
		string? name = node switch
		{
			Definition definition => definition.Name,
			DeclarationTarget target when target.Names.Count == 1 => target.Names[0],
			LambdaParameter { Parameter: not null } parameter => parameter.Name,
			_ => null
		};
		if (string.IsNullOrWhiteSpace(name) || !TryGetNodeRange(node, out TokenRange tokenRange) || !ReferenceEquals(file.Tokens, tokenRange.Sequence))
			return false;

		entry = new SymbolEntry(
			Path.GetFullPath(file.Path),
			CampLanguageService.ToTextRange(tokenRange),
			name!,
			GetDefinitionKind(node, containerName),
			GetNodeType(node),
			GetSignature(node),
			GetEntryDocumentation(node, file.Text, tokenRange.StartLineNumber),
			containerName,
			true,
			null);
		return true;
	}

	static void CollectReferenceEntries(SourceFile file, BindableNode node, List<SymbolEntry> entries, Dictionary<BindableNode, SymbolEntry> definitions, HashSet<BindableNode> visited)
	{
		if (!visited.Add(node))
			return;

		if (TryCreateReferenceEntry(file, node, definitions, out SymbolEntry? entry))
			entries.Add(entry!);

		foreach (BindableNode child in Children(node))
			CollectReferenceEntries(file, child, entries, definitions, visited);
	}

	static bool TryCreateReferenceEntry(SourceFile file, BindableNode node, Dictionary<BindableNode, SymbolEntry> definitions, out SymbolEntry? entry)
	{
		entry = null;
		BindableNode? target = node switch
		{
			VariableReferenceExpression variable => variable.Variable,
			MemberReferenceExpression member => member.Member ?? member.Candidates.FirstOrDefault(),
			MethodReferenceExpression { SourceSyntax: ConstructionExpressionSyntax construction } => ResolveTypeSyntaxTarget(construction.Type, definitions, file),
			MethodReferenceExpression method => method.Candidates.FirstOrDefault(),
			TypeReferenceExpression type => ResolveTypeTarget(type.Type, definitions, file),
			NamedTypeReference named => ResolveTypeTarget(named, definitions, file),
			TypeReference typeReference => ResolveTypeTarget(typeReference, definitions, file),
			NameOfExpression nameOf => nameOf.Reference,
			SymbolOfExpression symbolOf => symbolOf.Reference,
			_ => null
		};
		if (target is null)
			return false;
		if (!TryGetNodeRange(node, out TokenRange tokenRange) || !ReferenceEquals(file.Tokens, tokenRange.Sequence))
			return false;
		CampTextRange textRange = CampLanguageService.ToTextRange(tokenRange);
		if (!definitions.TryGetValue(target, out SymbolEntry? definition))
		{
			if (node is VariableReferenceExpression
				&& TryCreateLoweredSourceDefinitionEntry(file, target, out definition))
			{
				definitions[target] = definition!;
				if (!RangeTextContainsSymbolName(file.Text, textRange, definition!.Name))
					return false;
				entry = definition with
				{
					Path = Path.GetFullPath(file.Path),
					Range = textRange,
					IsDeclaration = false,
					Definition = definition
				};
				return true;
			}
			if (node is not MemberReferenceExpression member)
				return false;
			if (!RangeTextContainsSymbolName(file.Text, textRange, member.Name))
				return false;
			entry = new SymbolEntry(
				Path.GetFullPath(file.Path),
				textRange,
				member.Name,
				GetKind(target),
				GetNodeType(target) ?? node.ResolvedType,
				GetSignature(target),
				GetDocumentation(target),
				null,
				false,
				null);
			return true;
		}
		if (!RangeTextContainsSymbolName(file.Text, textRange, definition.Name))
			return false;
		entry = definition with
		{
			Path = Path.GetFullPath(file.Path),
			Range = textRange,
			IsDeclaration = false,
			Definition = definition
		};
		return true;
	}

	static bool TryCreateLoweredSourceDefinitionEntry(SourceFile file, BindableNode target, out SymbolEntry? entry)
	{
		entry = null;
		string? name = target switch
		{
			DeclarationTarget declaration when declaration.Names.Count == 1 => declaration.Names[0],
			ParameterDefinition parameter => parameter.Name,
			Definition definition => definition.Name,
			_ => null
		};
		if (string.IsNullOrWhiteSpace(name)
			|| !TryGetNodeRange(target, out TokenRange range)
			|| !ReferenceEquals(file.Tokens, range.Sequence))
			return false;
		entry = new SymbolEntry(
			Path.GetFullPath(file.Path),
			CampLanguageService.ToTextRange(range),
			name!,
			GetKind(target),
			GetNodeType(target),
			GetSignature(target),
			GetDocumentation(target),
			null,
			true,
			null);
		return true;
	}

	static bool RangeTextContainsSymbolName(string text, CampTextRange range, string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return true;
		string rangeText = GetRangeText(text, range);
		if (rangeText.Contains(name, StringComparison.Ordinal))
			return true;
		if (name.StartsWith("get", StringComparison.Ordinal) || name.StartsWith("set", StringComparison.Ordinal))
			return name.Length > 3 && rangeText.Contains(name[3..], StringComparison.Ordinal);
		return false;
	}

	static string GetRangeText(string text, CampTextRange range)
	{
		int start = OffsetOf(text, range.Start);
		int end = OffsetOf(text, range.End);
		if (start < 0 || end < start || start >= text.Length)
			return "";
		return text[start..Math.Min(end, text.Length)];
	}

	static bool TryFindCallAt(SourceFile file, BindableNode root, CampTextPosition position, out CallExpression? call)
	{
		call = null;
		List<CallExpression> calls = [];
		CollectCallsAt(file, root, position, calls, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance));
		call = calls
			.OrderBy(static candidate => TryGetCallRange(candidate, out TokenRange range) ? SpanSize(CampLanguageService.ToTextRange(range)) : int.MaxValue)
			.FirstOrDefault();
		return call is not null;
	}

	static void CollectCallsAt(SourceFile file, BindableNode node, CampTextPosition position, List<CallExpression> calls, HashSet<BindableNode> visited)
	{
		if (!visited.Add(node))
			return;
		if (node is CallExpression call && TryGetCallRange(call, out TokenRange range) && ReferenceEquals(file.Tokens, range.Sequence) && Contains(CampLanguageService.ToTextRange(range), position))
			calls.Add(call);
		foreach (BindableNode child in Children(node))
			CollectCallsAt(file, child, position, calls, visited);
	}

	static bool TryGetCallRange(CallExpression call, out TokenRange range)
	{
		range = default;
		if (call.SourceSyntax is CallPostfixPartSyntax { OpenParenToken: Token open, CloseParenToken: Token close })
		{
			range = new TokenRange(open.Sequence, open.Index, close.Index - open.Index + 1);
			return true;
		}
		return TryGetSyntaxRange(call.SourceSyntax, out range);
	}

	IEnumerable<FunctionDefinition> GetCallFunctions(CallExpression call, IReadOnlyList<FunctionDefinition> functions)
	{
		if (call.Target is MemberReferenceExpression member)
		{
			if (member.Member is FunctionDefinition function)
				yield return function;
			foreach (FunctionDefinition candidate in member.Candidates)
				yield return candidate;
			yield break;
		}
		if (call.Target is MemberExpression memberExpression)
		{
			string? targetName = GetMemberExpressionTargetName(memberExpression);
			foreach (FunctionDefinition candidate in functions.Where(function => FunctionMatchesCallContext(function, targetName, memberExpression.Name)))
				yield return candidate;
			yield break;
		}
		if (call.Target is MethodReferenceExpression method)
		{
			foreach (FunctionDefinition candidate in method.Candidates)
				yield return candidate;
			yield break;
		}
		if (call.Target is NamedExpression named)
		{
			foreach (FunctionDefinition candidate in functions.Where(function => FunctionNameMatches(function, named.Name) || function.Symbol == named.Name))
				yield return candidate;
		}
	}

	string? GetMemberExpressionTargetName(MemberExpression memberExpression)
	{
		if (!string.IsNullOrWhiteSpace(memberExpression.Target?.ResolvedType))
			return BaseTypeName(UnwrapStorageType(memberExpression.Target.ResolvedType!));
		if (memberExpression.Target is NamedExpression named && ResolveCompletionType(named.Name) is not null)
			return named.Name;
		return null;
	}

	CampSignatureHelp? GetSignatureHelpFromText(SourceFile file, string text, CampTextPosition position)
	{
		if (!TryGetCallContext(text, position, out string? targetName, out string? targetExpression, out string? memberName, out int activeParameter))
			return null;
		string? resolvedTargetName = targetExpression is null
			? targetName is null ? null : ResolveCompletionTargetName(targetName)
			: ResolveCallTargetType(file, text, position, targetExpression) ?? ResolveCompletionTargetName(targetName ?? targetExpression);
		List<FunctionDefinition> callFunctions = functions
			.Where(function => FunctionMatchesCallContext(function, resolvedTargetName ?? targetName, memberName))
			.Distinct()
			.ToList();
		if (callFunctions.Count == 0)
			return GetSignatureHelpFromEntries(file, position, resolvedTargetName ?? targetName, memberName, activeParameter);
		List<CampSignatureInformation> signatures = callFunctions
			.Select(function => CreateSignatureInformation(function, file))
			.ToList();
		if (GetSignatureHelpFromEntries(file, position, resolvedTargetName ?? targetName, memberName, activeParameter) is CampSignatureHelp entryHelp)
			signatures.AddRange(entryHelp.Signatures);
		AddReceiverSourceSignatureFallbacks(signatures, resolvedTargetName ?? targetName, memberName);
		signatures = signatures.DistinctBy(static signature => signature.Label).ToList();
		return new CampSignatureHelp(
			signatures,
			0,
			Math.Clamp(activeParameter, 0, Math.Max(0, GetSignatureHelpParameters(callFunctions[0]).Count - 1)));
	}

	CampSignatureHelp? GetSignatureHelpFromEntries(SourceFile file, CampTextPosition position, string? targetName, string? memberName, int activeParameter)
	{
		string name = memberName ?? targetName ?? "";
		if (name.Length == 0)
			return null;
		IEnumerable<SymbolEntry> candidates = entries.Where(entry =>
			entry.Definition is null
			&& entry.Kind is CampSymbolKind.Function or CampSymbolKind.Method
			&& entry.Name == name
			&& !string.IsNullOrWhiteSpace(entry.Signature));
		if (targetName is not null)
			candidates = candidates.Where(entry => SignatureEntryMatchesCallContext(entry, targetName, name));
		List<CampSignatureInformation> signatures = candidates
			.DistinctBy(static entry => entry.Signature)
			.Select(static entry => new CampSignatureInformation(entry.Signature!, entry.Documentation, []))
			.ToList();
		AddReceiverSourceSignatureFallbacks(signatures, targetName, memberName);
		signatures = signatures.DistinctBy(static signature => signature.Label).ToList();
		if (signatures.Count == 0 && targetName is null)
		{
			signatures = GetScopeCompletions(file, position)
				.Where(item => item.Kind == CampSymbolKind.Function && item.Label == name && !string.IsNullOrWhiteSpace(item.Detail))
				.DistinctBy(static item => item.Detail)
				.Select(static item => new CampSignatureInformation(item.Detail!, item.Documentation, []))
				.ToList();
		}
		return signatures.Count == 0
			? null
			: new CampSignatureHelp(signatures, 0, Math.Clamp(activeParameter, 0, 0));
	}

	void AddReceiverSourceSignatureFallbacks(List<CampSignatureInformation> signatures, string? targetName, string? memberName)
	{
		if (string.IsNullOrWhiteSpace(memberName))
			return;
		List<string> targetNames = [];
		if (!string.IsNullOrWhiteSpace(targetName))
			targetNames.Add(targetName!);
			foreach (CampSignatureInformation signature in signatures)
			{
				string? inferred = TryGetSignatureReceiverType(signature.Label);
				if (!string.IsNullOrWhiteSpace(inferred) && !targetNames.Contains(inferred, StringComparer.Ordinal))
					targetNames.Add(inferred);
			}
		if (targetNames.Count == 0)
			return;
		foreach (FunctionDefinition function in functions)
		{
			string? label = GetSignatureHelpLabel(function);
			if (string.IsNullOrWhiteSpace(label)
				|| !label!.Contains(memberName + "(", StringComparison.Ordinal)
				|| !targetNames.Any(name => label.Contains("(" + name + " this", StringComparison.Ordinal) || label.Contains("(" + name + "* this", StringComparison.Ordinal)))
				continue;
			signatures.Add(new CampSignatureInformation(label, GetDeclarationDocumentation(function), GetSignatureHelpParameters(function)
				.Select(parameter => new CampParameterHelp(FormatParameterLabel(parameter), GetDeclarationDocumentation(parameter)))
				.ToList()));
		}
		foreach (SourceFile sourceFile in snapshot.Compilation.Files)
		{
			foreach (string rawLine in sourceFile.Text.Split('\n'))
			{
				string line = rawLine.Trim();
				if (!line.Contains(memberName + "(", StringComparison.Ordinal)
					|| !targetNames.Any(name => line.Contains("(" + name + " this", StringComparison.Ordinal) || line.Contains("(" + name + "* this", StringComparison.Ordinal)))
					continue;
				int body = line.IndexOfAny(['{', ';']);
				string label = (body >= 0 ? line[..body] : line).Trim();
				if (label.Length > 0)
					signatures.Add(new CampSignatureInformation(label, null, []));
			}
		}
	}

	static string? TryGetSignatureReceiverType(string label)
	{
		int open = label.IndexOf('(');
		if (open < 0)
			return null;
		int thisIndex = label.IndexOf(" this", open, StringComparison.Ordinal);
		if (thisIndex < 0)
			return null;
		string beforeThis = label[(open + 1)..thisIndex].Trim();
		if (beforeThis.Length == 0)
			return null;
		string[] parts = beforeThis.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return parts.Length == 0 ? null : parts[^1].TrimEnd('*');
	}

	static bool FunctionMatchesCallContext(FunctionDefinition function, string? targetName, string? memberName)
	{
		string name = memberName ?? targetName ?? "";
		if (name.Length == 0)
			return false;
		if (targetName is null)
			return FunctionNameMatches(function, name) || function.Symbol == name;
		if (function.OutOfScopeOwnerName is not null)
			return function.OutOfScopeOwnerName == targetName && FunctionNameMatches(function, name);
		if (function.Parameters.FirstOrDefault() is ThisParameterDefinition thisParameter
			&& BaseTypeName(UnwrapStorageType(thisParameter.ResolvedType ?? "")) == targetName
			&& FunctionNameMatches(function, name))
			return true;
		return FunctionNameMatches(function, name)
			&& (function.Symbol == targetName + "_" + name
				|| GetSurfaceOwnerName(function) == targetName);
	}

	static bool SignatureEntryMatchesCallContext(SymbolEntry entry, string targetName, string name)
	{
		if (entry.ContainerName == targetName)
			return true;
		string signature = entry.Signature ?? "";
		return SignatureHasSurfaceOwner(signature, targetName, name)
			|| signature.Contains("(" + targetName + " this", StringComparison.Ordinal)
			|| signature.Contains("(" + targetName + "* this", StringComparison.Ordinal);
	}

	static bool FunctionNameMatches(FunctionDefinition function, string name)
	{
		return function.Name == name
			|| BindableNodeAnalyzer.GetCallableName(function) == name
			|| GetSurfaceFunctionName(function) == name;
	}

	static string? GetSurfaceFunctionName(FunctionDefinition function)
	{
		string? signature = GetCleanSignature(function);
		if (string.IsNullOrWhiteSpace(signature))
			return null;
		int open = signature!.IndexOf('(');
		if (open <= 0)
			return null;
		string prefix = signature[..open].TrimEnd();
		int start = prefix.Length;
		while (start > 0 && (IsIdentifierPart(prefix[start - 1]) || prefix[start - 1] == '.'))
			start--;
		string surface = prefix[start..];
		int dot = surface.LastIndexOf('.');
		if (dot >= 0)
			surface = surface[(dot + 1)..];
		return string.IsNullOrWhiteSpace(surface) ? null : surface;
	}

	static string? GetSurfaceOwnerName(FunctionDefinition function)
	{
		string? signature = GetCleanSignature(function);
		if (string.IsNullOrWhiteSpace(signature))
			return null;
		return TryGetSurfaceOwnerName(signature!, GetSurfaceFunctionName(function));
	}

	static bool SignatureHasSurfaceOwner(string signature, string targetName, string name)
	{
		return TryGetSurfaceOwnerName(signature, name) == targetName;
	}

	static string? TryGetSurfaceOwnerName(string signature, string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return null;
		int open = signature.IndexOf('(');
		if (open <= 0)
			return null;
		string prefix = signature[..open].TrimEnd();
		int start = prefix.Length;
		while (start > 0 && (IsIdentifierPart(prefix[start - 1]) || prefix[start - 1] == '.'))
			start--;
		string surface = prefix[start..];
		int dot = surface.LastIndexOf('.');
		if (dot <= 0 || !surface[(dot + 1)..].Equals(name, StringComparison.Ordinal))
			return null;
		string owner = surface[..dot];
		return string.IsNullOrWhiteSpace(owner) ? null : owner;
	}

	static CampSignatureInformation CreateSignatureInformation(FunctionDefinition function, SourceFile? file)
	{
		Dictionary<string, string> rawParameterDocs = file is not null && TryGetNodeRange(function, out TokenRange range) && ReferenceEquals(file.Tokens, range.Sequence)
			? ExtractLeadingParameterDocs(file.Text, range.StartLineNumber)
			: [];
		List<ParameterDefinition> parameters = GetSignatureHelpParameters(function);
		return new CampSignatureInformation(
			GetSignatureHelpLabel(function) ?? function.Name + "()",
			GetDeclarationDocumentation(function),
			parameters.Select(parameter => new CampParameterHelp(
				FormatParameterLabel(parameter),
				GetDeclarationDocumentation(parameter) ?? (parameter.Name is not null && rawParameterDocs.TryGetValue(parameter.Name, out string? rawDoc) ? rawDoc : null))).ToList());
	}

	static int GetActiveParameter(string text, CallExpression call, CampTextPosition position, FunctionDefinition function)
	{
		List<ParameterDefinition> parameters = GetSignatureHelpParameters(function);
		if (parameters.Count == 0)
			return 0;
		if (TryGetArgumentAtPosition(call, position, out ArgumentExpression? argument) && argument?.Name is string name)
		{
			int namedIndex = parameters.FindIndex(parameter => parameter.Name == name);
			if (namedIndex >= 0)
				return namedIndex;
		}
		if (!TryGetCallOpenParen(call, out TokenRange openParen))
			return Math.Clamp(call.Arguments.Count, 0, parameters.Count - 1);
		int argumentIndex = CountTopLevelCommasBetween(text, openParen, position);
		return Math.Clamp(argumentIndex, 0, parameters.Count - 1);
	}

	static bool TryGetArgumentAtPosition(CallExpression call, CampTextPosition position, out ArgumentExpression? argument)
	{
		argument = null;
		foreach (ArgumentExpression candidate in call.Arguments)
		{
			if (TryGetSyntaxRange(candidate.SourceSyntax, out TokenRange range) && Contains(CampLanguageService.ToTextRange(range), position))
			{
				argument = candidate;
				return true;
			}
		}
		return false;
	}

	static bool TryGetCallOpenParen(CallExpression call, out TokenRange openParen)
	{
		openParen = default;
		return call.SourceSyntax is CallPostfixPartSyntax { OpenParenToken: Token token } && Assign(token.Range, out openParen);
	}

	static int CountTopLevelCommasBetween(string text, TokenRange openParen, CampTextPosition position)
	{
		int start = OffsetOf(text, new CampTextPosition(openParen.StartLineNumber - 1, openParen.StartColumn));
		int end = OffsetOf(text, position);
		if (start < 0 || end <= start)
			return 0;
		int depth = 0;
		int count = 0;
		bool inString = false;
		char stringQuote = '\0';
		for (int i = start; i < Math.Min(text.Length, end); i++)
		{
			char value = text[i];
			if (inString)
			{
				if (value == '\\')
				{
					i++;
					continue;
				}
				if (value == stringQuote)
					inString = false;
				continue;
			}
			if (value is '"' or '\'')
			{
				inString = true;
				stringQuote = value;
				continue;
			}
			if (value is '(' or '[' or '{')
			{
				depth++;
				continue;
			}
			if (value is ')' or ']' or '}')
			{
				depth = Math.Max(0, depth - 1);
				continue;
			}
			if (value == ',' && depth == 0)
				count++;
		}
		return count;
	}

	static int OffsetOf(string text, CampTextPosition position)
	{
		int line = 0;
		int character = 0;
		for (int i = 0; i < text.Length; i++)
		{
			if (line == position.Line && character == position.Character)
				return i;
			if (text[i] == '\n')
			{
				line++;
				character = 0;
			}
			else if (text[i] != '\r')
				character++;
		}
		return line == position.Line && character == position.Character ? text.Length : -1;
	}

	static List<ParameterDefinition> GetVisibleCallParameters(FunctionDefinition function)
	{
		List<ParameterDefinition> parameters = [];
		int expandedThisCount = CountExpandedThisParameters(function.Parameters);
		for (int i = 0; i < function.Parameters.Count; i++)
		{
			ParameterDefinition parameter = function.Parameters[i];
			if (parameter.Modifier is ParameterModifier.Thrown or ParameterModifier.Within
				|| parameter is ThisParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition or NameOfParameterDefinition or WithinParameterDefinition
				|| i < expandedThisCount)
				continue;
			parameters.Add(parameter);
		}
		return parameters;
	}

	static List<ParameterDefinition> GetSignatureHelpParameters(FunctionDefinition function)
	{
		List<ParameterDefinition> visible = GetVisibleCallParameters(function);
		if (visible.Count < 2)
			return visible.Where(parameter => !IsGeneratedExpandedReturnParameter(function, parameter)).ToList();
		List<ParameterDefinition> result = [];
		foreach (ParameterDefinition parameter in visible)
		{
			if (IsGeneratedExpandedReturnParameter(function, parameter))
				continue;
			if (IsExpandedComponentParameter(result.LastOrDefault(), parameter))
				continue;
			result.Add(parameter);
		}
		return result;
	}

	static bool IsGeneratedExpandedReturnParameter(FunctionDefinition function, ParameterDefinition parameter)
	{
		return parameter.Modifier == ParameterModifier.Out
			&& (parameter.SourceSyntax is null || parameter.SourceSyntax == function.SourceSyntax)
			&& parameter.Name is string name
			&& name.StartsWith("result_", StringComparison.Ordinal);
	}

	static bool IsExpandedComponentParameter(ParameterDefinition? previous, ParameterDefinition parameter)
	{
		if (previous?.Name is not string ownerName || string.IsNullOrWhiteSpace(ownerName) || parameter.Name is not string name)
			return false;
		string prefix = ownerName + "_";
		if (!name.StartsWith(prefix, StringComparison.Ordinal))
			return false;
		string component = name[prefix.Length..];
		return component switch
		{
			"context" => IsVoidPointer(parameter),
			"length" => IsNativeUnsigned(parameter),
			"hasValue" => IsBool(parameter),
			_ => false
		};
	}

	static bool IsVoidPointer(ParameterDefinition parameter)
	{
		string? type = parameter.ResolvedType ?? BindableNodeCodeSerializer.SerializeType(parameter.Type);
		return type is "void*" or "const void*";
	}

	static bool IsNativeUnsigned(ParameterDefinition parameter)
	{
		string? type = parameter.ResolvedType ?? BindableNodeCodeSerializer.SerializeType(parameter.Type);
		return type is "nuint" or "uintptr_t";
	}

	static bool IsBool(ParameterDefinition parameter)
	{
		string? type = parameter.ResolvedType ?? BindableNodeCodeSerializer.SerializeType(parameter.Type);
		return type is "bool" or "_Bool";
	}

	static int CountExpandedThisParameters(List<ParameterDefinition> parameters)
	{
		int count = 0;
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter is not ThisParameterDefinition)
				break;
			if (parameter.ResolvedType is string type && (type.EndsWith("*", StringComparison.Ordinal) || type == "void*"))
				break;
			count++;
		}
		return count > 1 ? count : 0;
	}

	static string FormatParameterLabel(ParameterDefinition parameter)
	{
		string type = BindableNodeCodeSerializer.SerializeType(parameter.Type);
		if (string.IsNullOrWhiteSpace(type) || type == "#ERROR")
			type = parameter.ResolvedType ?? "";
		string name = string.IsNullOrWhiteSpace(parameter.Name) ? "" : " " + parameter.Name;
		return (type + name).Trim();
	}

	static BindableNode? ResolveTypeSyntaxTarget(TypeSyntax? syntax, Dictionary<BindableNode, SymbolEntry> definitions, SourceFile file)
	{
		if (!TryGetTypeSyntaxNameRange(syntax, out TokenRange range) || !ReferenceEquals(file.Tokens, range.Sequence))
			return null;
		string name = SourceText(file.Text, range);
		if (string.IsNullOrWhiteSpace(name))
			return null;
		AliasDefinition? alias = definitions.Keys
			.OfType<AliasDefinition>()
			.FirstOrDefault(definition => definition.Name == name || definition.Symbol == name);
		if (alias is not null)
			return alias;
		return definitions.Keys
			.OfType<TypeDefinition>()
			.FirstOrDefault(definition => definition.Name == name || definition.Symbol == name);
	}

	static BindableNode? ResolveTypeTarget(TypeReference? type, Dictionary<BindableNode, SymbolEntry> definitions, SourceFile? file = null)
	{
		string? sourceIdentifier = type is not null && TryGetNodeRange(type, out TokenRange sourceRange) && file is not null && ReferenceEquals(file.Tokens, sourceRange.Sequence)
			? FirstIdentifier(SourceText(file.Text, sourceRange))
			: null;
		if (!string.IsNullOrWhiteSpace(sourceIdentifier))
		{
			AliasDefinition? sourceAlias = definitions.Keys
				.OfType<AliasDefinition>()
				.FirstOrDefault(definition => definition.Name == sourceIdentifier || definition.Symbol == sourceIdentifier);
			if (sourceAlias is not null)
				return sourceAlias;
		}

		BindableNode? nested = type switch
		{
			AttributedTypeReference attributed => ResolveTypeTarget(attributed.Type, definitions, file),
			GenericTypeReference generic => ResolveTypeTarget(generic.Type, definitions, file),
			ArrayTypeReference array => ResolveTypeTarget(array.ElementType, definitions, file),
			FixedArrayTypeReference fixedArray => ResolveTypeTarget(fixedArray.ElementType, definitions, file),
			OptionalTypeReference optional => ResolveTypeTarget(optional.ElementType, definitions, file),
			PointerTypeReference pointer => ResolveTypeTarget(pointer.ElementType, definitions, file),
			ConstTypeReference constant => ResolveTypeTarget(constant.Type, definitions, file),
			ConstOfTypeReference constOf => ResolveTypeTarget(constOf.Type, definitions, file),
			VolatileTypeReference volatileType => ResolveTypeTarget(volatileType.Type, definitions, file),
			EscapedTypeReference escaped => ResolveTypeTarget(escaped.Type, definitions, file),
			ScopedTypeReference scoped => ResolveTypeTarget(scoped.Type, definitions, file),
			UnscopedTypeReference unscoped => ResolveTypeTarget(unscoped.Type, definitions, file),
			ThrownTypeReference thrown => ResolveTypeTarget(thrown.Type, definitions, file),
			_ => null
		};
		if (nested is not null)
			return nested;

		string? sourceName = type switch
		{
			NamedTypeReference named => named.Name,
			TypeDefinitionReference definition => definition.Name,
			_ => null
		};
		if (!string.IsNullOrWhiteSpace(sourceName))
		{
			AliasDefinition? alias = definitions.Keys
				.OfType<AliasDefinition>()
				.FirstOrDefault(definition => definition.Name == sourceName || definition.Symbol == sourceName);
			if (alias is not null)
				return alias;
		}
		if (type is TypeDefinitionReference { Definition: not null } definitionReference)
			return definitionReference.Definition;

		string? name = type switch
		{
			NamedTypeReference named => BaseTypeName(named.ResolvedType ?? named.Name),
			TypeDefinitionReference definition => BaseTypeName(definition.ResolvedType ?? definition.Name),
			_ => null
		};
		return name is null
			? null
			: definitions.Keys.OfType<TypeDefinition>().FirstOrDefault(definition => definition.Name == name || definition.Symbol == name);
	}

	static string BaseTypeName(string type)
	{
		int generic = type.IndexOf('<');
		return generic >= 0 ? type[..generic] : type;
	}

	static CampSymbolKind GetKind(BindableNode node)
	{
		return node switch
		{
			ClassDefinition or StructDefinition or InterfaceDefinition or EnumDefinition or NewtypeDefinition or ParamsDefinition => CampSymbolKind.Type,
			AliasDefinition => CampSymbolKind.Alias,
			FunctionDefinition function when HasContainingType(function) => CampSymbolKind.Method,
			FunctionDefinition => CampSymbolKind.Function,
			FieldDefinition or ParameterDefinition when IsParamsComponentDefinition(node) => CampSymbolKind.Component,
			FieldDefinition => CampSymbolKind.Field,
			VariableDefinition variable when variable.SourceSyntax is EnumValueSyntax => CampSymbolKind.EnumValue,
			VariableDefinition => CampSymbolKind.Variable,
			ParameterDefinition or LambdaParameter => CampSymbolKind.Parameter,
			DeclarationTarget => CampSymbolKind.Variable,
			_ => CampSymbolKind.Unknown
		};
	}

	static bool IsParamsComponentDefinition(BindableNode node)
	{
		if (node.SourceSyntax is not null)
			return false;
		string? name = node switch
		{
			FieldDefinition field => field.Name,
			ParameterDefinition parameter => parameter.Name,
			_ => null
		};
		return name is "elements" or "length" or "value" or "specified" or "call" or "context";
	}

	static CampSymbolKind GetDefinitionKind(BindableNode node, string? containerName)
	{
		if (!string.IsNullOrWhiteSpace(containerName) && node is FunctionDefinition)
			return CampSymbolKind.Method;
		return GetKind(node);
	}

	static bool HasContainingType(FunctionDefinition function)
	{
		return function.Symbol.Contains('_', StringComparison.Ordinal) || function.Parameters.FirstOrDefault() is ThisParameterDefinition;
	}

	static string? GetNodeType(BindableNode node)
	{
		return node switch
		{
			VariableDefinition variable => BindableNodeCodeSerializer.SerializeType(variable.Type),
			FieldDefinition field => BindableNodeCodeSerializer.SerializeType(field.Type),
			ParameterDefinition parameter => BindableNodeCodeSerializer.SerializeType(parameter.Type),
			DeclarationTarget target => target.ResolvedType,
			Expression expression => expression.ResolvedType,
			FunctionDefinition function => BindableNodeCodeSerializer.SerializeType(function.ReturnType),
			_ => node.ResolvedType
		};
	}

	static string? GetSignature(BindableNode node)
	{
		if (node is not Definition definition)
			return null;
		if (definition is FunctionDefinition function)
			return GetSignatureHelpLabel(function);
		return GetCleanSignature(definition);
	}

	internal static string? FormatSignatureForLanguageService(FunctionDefinition function)
	{
		string? label = GetCleanSignature(function);
		if (string.IsNullOrWhiteSpace(label))
			return label;
		label = RemoveSignatureKeyword(label!, "extern");
		HashSet<string> hiddenParameters = GetHiddenSignatureHelpParameterNames(function);
		if (hiddenParameters.Count > 0)
			label = RemoveParametersFromSignatureLabel(label, hiddenParameters);
		return RestorePreparedArrayParameters(label, function);
	}

	static string RestorePreparedArrayParameters(string label, FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier != ParameterModifier.Prep || string.IsNullOrWhiteSpace(parameter.Name))
				continue;
			string loweredType = parameter.Type is null
				? parameter.ResolvedType ?? ""
				: BindableNodeCodeSerializer.SerializeType(parameter.Type);
			if (!loweredType.EndsWith("*", StringComparison.Ordinal))
				continue;
			string sourcePrefix = "prep " + loweredType + " " + parameter.Name;
			string arrayPrefix = "prep " + loweredType[..^1].TrimEnd() + "[] " + parameter.Name;
			label = label.Replace(sourcePrefix, arrayPrefix, StringComparison.Ordinal);
		}
		return label;
	}

	static string? GetSignatureHelpLabel(FunctionDefinition function)
	{
		return FormatSignatureForLanguageService(function);
	}

	static HashSet<string> GetHiddenSignatureHelpParameterNames(FunctionDefinition function)
	{
		HashSet<string> hidden = new(StringComparer.Ordinal);
		List<ParameterDefinition> displayed = [];
		foreach (ParameterDefinition parameter in GetVisibleCallParameters(function))
		{
			if (IsGeneratedExpandedReturnParameter(function, parameter))
			{
				if (!string.IsNullOrWhiteSpace(parameter.Name))
					hidden.Add(parameter.Name);
				continue;
			}
			if (IsExpandedComponentParameter(displayed.LastOrDefault(), parameter))
			{
				if (!string.IsNullOrWhiteSpace(parameter.Name))
					hidden.Add(parameter.Name);
				continue;
			}
			displayed.Add(parameter);
		}
		return hidden;
	}

	static string RemoveSignatureKeyword(string label, string keyword)
	{
		string token = keyword + " ";
		int index = label.IndexOf(token, StringComparison.Ordinal);
		while (index >= 0)
		{
			bool leftBoundary = index == 0 || !IsIdentifierPart(label[index - 1]);
			int end = index + keyword.Length;
			bool rightBoundary = end >= label.Length || !IsIdentifierPart(label[end]);
			if (leftBoundary && rightBoundary)
				return (label[..index] + label[(index + token.Length)..]).Trim();
			index = label.IndexOf(token, index + 1, StringComparison.Ordinal);
		}
		return label;
	}

	static string RemoveParametersFromSignatureLabel(string label, HashSet<string> hiddenParameterNames)
	{
		int open = label.IndexOf('(');
		if (open < 0)
			return label;
		int close = FindMatchingCloseParen(label, open);
		if (close < 0)
			return label;
		string parameterText = label[(open + 1)..close];
		List<string> parameters = SplitTopLevelCommaSeparated(parameterText);
		List<string> kept = parameters
			.Where(parameter => !hiddenParameterNames.Any(hidden => ParameterTextDeclaresName(parameter, hidden)))
			.ToList();
		return label[..(open + 1)] + string.Join(", ", kept.Select(static parameter => parameter.Trim()).Where(static parameter => parameter.Length > 0)) + label[close..];
	}

	static int FindMatchingCloseParen(string text, int open)
	{
		int depth = 0;
		bool inString = false;
		char quote = '\0';
		for (int i = open; i < text.Length; i++)
		{
			char value = text[i];
			if (inString)
			{
				if (value == '\\')
					i++;
				else if (value == quote)
					inString = false;
				continue;
			}
			if (value is '"' or '\'')
			{
				inString = true;
				quote = value;
				continue;
			}
			if (value == '(')
				depth++;
			else if (value == ')' && --depth == 0)
				return i;
		}
		return -1;
	}

	static List<string> SplitTopLevelCommaSeparated(string text)
	{
		List<string> parts = [];
		int start = 0;
		int depth = 0;
		bool inString = false;
		char quote = '\0';
		for (int i = 0; i < text.Length; i++)
		{
			char value = text[i];
			if (inString)
			{
				if (value == '\\')
					i++;
				else if (value == quote)
					inString = false;
				continue;
			}
			if (value is '"' or '\'')
			{
				inString = true;
				quote = value;
				continue;
			}
			if (value is '(' or '[' or '<' or '{')
				depth++;
			else if (value is ')' or ']' or '>' or '}')
				depth = Math.Max(0, depth - 1);
			else if (value == ',' && depth == 0)
			{
				parts.Add(text[start..i]);
				start = i + 1;
			}
		}
		parts.Add(text[start..]);
		return parts;
	}

	static bool ParameterTextDeclaresName(string parameterText, string name)
	{
		for (int i = 0; i <= parameterText.Length - name.Length; i++)
		{
			if (!parameterText.AsSpan(i, name.Length).SequenceEqual(name))
				continue;
			bool leftBoundary = i == 0 || !IsIdentifierPart(parameterText[i - 1]);
			int end = i + name.Length;
			bool rightBoundary = end == parameterText.Length || !IsIdentifierPart(parameterText[end]);
			if (leftBoundary && rightBoundary)
				return true;
		}
		return false;
	}

	static string? GetCleanSignature(Definition definition)
	{
		using StringWriter writer = new();
		BindableNodeCodeSerializer.Serialize(definition, writer, new BindableNodeCodeSerializerOptions { ApiHeader = false });
		string text = StripDocAttributes(writer.ToString().Trim());
		int body = text.IndexOf('{');
		if (body >= 0)
			text = text[..body].TrimEnd();
		return text.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Split('\n')
			.Select(static line => line.Trim())
			.FirstOrDefault(static line => line.Length > 0 && !line.StartsWith("@", StringComparison.Ordinal));
	}

	static string? GetDocumentSymbolDetail(BindableNode node)
	{
		return node switch
		{
			FunctionDefinition function => GetSignature(function),
			FieldDefinition field => BindableNodeCodeSerializer.SerializeType(field.Type),
			VariableDefinition variable when variable.SourceSyntax is EnumValueSyntax => null,
			VariableDefinition variable => BindableNodeCodeSerializer.SerializeType(variable.Type),
			TypeDefinition definition => definition switch
			{
				ClassDefinition => "class",
				StructDefinition => "struct",
				InterfaceDefinition => "interface",
				EnumDefinition => "enum",
				NewtypeDefinition => "newtype",
				ParamsDefinition => "params",
				_ => null
			},
			AliasDefinition alias => GetSignature(alias),
			_ => null
		};
	}

	static string? GetDocumentation(BindableNode node)
	{
		return GetDeclarationDocumentation(node);
	}

	static string? GetEntryDocumentation(BindableNode node, string sourceText, int oneBasedLine)
	{
		if (node is ParameterDefinition or LambdaParameter or GenericParameter)
			return GetDocumentation(node);
		return GetDocumentation(node) ?? ExtractLeadingDoc(sourceText, oneBasedLine);
	}

	static string? GetDeclarationDocumentation(BindableNode node)
	{
		IEnumerable<AttributeConstructor> attributes = node switch
		{
			Definition definition => definition.Attributes,
			GenericParameter parameter => parameter.Attributes,
			_ => []
		};
		List<string> docs = [];
		if (node is FunctionDefinition function && UnsupportedAvailability.TryGetReason(function, out string? reason))
		{
			string text = "Not supported by the current target.";
			if (!string.IsNullOrWhiteSpace(reason))
				text += " " + reason;
			docs.Add(text);
		}
		foreach (AttributeConstructor attribute in attributes.Where(static attribute => AttributeName(attribute) is "summary" or "remarks"))
		{
			string? value = attribute.Arguments.FirstOrDefault()?.Value is LiteralExpression literal ? literal.Value?.ToString() : null;
			if (!string.IsNullOrWhiteSpace(value))
				docs.Add(value!);
		}
		return docs.Count == 0 ? null : string.Join("\n\n", docs);
	}

	static string AttributeName(AttributeConstructor attribute)
	{
		return attribute.Name.StartsWith("@", StringComparison.Ordinal) ? attribute.Name[1..] : attribute.Name;
	}

	static string StripDocAttributes(string text)
	{
		StringBuilder builder = new();
		for (int i = 0; i < text.Length;)
		{
			if (text[i] == '@' && IsDocAttributeAt(text, i, out int end))
			{
				i = end;
				while (i < text.Length && char.IsWhiteSpace(text[i]) && text[i] != '\n')
					i++;
				continue;
			}
			builder.Append(text[i]);
			i++;
		}
		return builder.ToString();
	}

	static bool IsDocAttributeAt(string text, int index, out int end)
	{
		end = index;
		foreach (string name in new[] { "@summary", "@remarks", "@returns", "@example", "@see", "@deprecated" })
		{
			if (!text.AsSpan(index).StartsWith(name, StringComparison.Ordinal))
				continue;
			int i = index + name.Length;
			while (i < text.Length && char.IsWhiteSpace(text[i]))
				i++;
			if (i >= text.Length || text[i] != '(')
				return false;
			int depth = 0;
			bool inString = false;
			for (; i < text.Length; i++)
			{
				char value = text[i];
				if (inString)
				{
					if (value == '\\')
					{
						i++;
						continue;
					}
					if (value == '"')
						inString = false;
					continue;
				}
				if (value == '"')
				{
					inString = true;
					continue;
				}
				if (value == '(')
					depth++;
				else if (value == ')' && --depth == 0)
				{
					end = i + 1;
					return true;
				}
			}
			return false;
		}
		return false;
	}

	static bool TryGetNodeRange(BindableNode node, out TokenRange range)
	{
		range = default;
		if (TryGetNameTokenRange(node, out range))
			return true;
		if (node is DeclarationTarget)
			return false;
		return TryGetSyntaxRange(node.SourceSyntax, out range);
	}

	static bool TryGetNameTokenRange(BindableNode node, out TokenRange range)
	{
		range = default;
		switch (node)
		{
			case AliasDefinition { SourceSyntax: AliasDeclarationSyntax syntax }:
				return Assign(syntax.Identifier?.Range, out range);
			case TypeDefinition { SourceSyntax: TypeDeclarationSyntax syntax }:
				return Assign(syntax.Identifier?.Range, out range);
			case FunctionDefinition { SourceSyntax: MemberDeclarationSyntax memberSyntax }:
				return Assign(memberSyntax.Identifier?.Range, out range);
			case FieldDefinition { SourceSyntax: MemberDeclarationSyntax memberSyntax }:
				return Assign(memberSyntax.Identifier?.Range, out range);
			case VariableDefinition { SourceSyntax: MemberDeclarationSyntax memberSyntax }:
				return Assign(memberSyntax.Identifier?.Range, out range);
			case VariableDefinition { SourceSyntax: EnumValueSyntax syntax }:
				return Assign(syntax.Identifier?.Range, out range);
			case ParameterDefinition { SourceSyntax: ValueParameterSyntax syntax }:
				return Assign(syntax.Identifier?.Range, out range);
			case LambdaParameter { SourceSyntax: LambdaParameterSyntax syntax }:
				return Assign(syntax.Identifier?.Range, out range);
			case DeclarationTarget target:
				return TryGetDeclarationTargetNameRange(target, out range);
			case VariableReferenceExpression { SourceSyntax: QualifiedNameExpressionSyntax syntax }:
				return Assign(syntax.Identifier?.Range, out range);
			case MemberReferenceExpression { SourceSyntax: MemberPostfixPartSyntax syntax }:
				return Assign(syntax.Identifier?.Range, out range);
			case MemberReferenceExpression { SourceSyntax: PostfixExpressionSyntax syntax }:
				return TryGetLastMemberRange(syntax, out range);
			case MethodReferenceExpression { SourceSyntax: MemberPostfixPartSyntax syntax }:
				return Assign(syntax.Identifier?.Range, out range);
			case MethodReferenceExpression { SourceSyntax: ConstructionExpressionSyntax syntax }:
				return TryGetTypeSyntaxNameRange(syntax.Type, out range);
			case MethodReferenceExpression { SourceSyntax: QualifiedNameExpressionSyntax syntax }:
				return Assign(syntax.Identifier?.Range, out range);
			case TypeReferenceExpression:
				return TryGetSyntaxRange(node.SourceSyntax, out range);
			case NamedTypeReference:
				return TryGetSyntaxRange(node.SourceSyntax, out range);
			case TypeDefinitionReference:
				return TryGetSyntaxRange(node.SourceSyntax, out range);
		}
		return false;
	}

	static bool TryGetTypeSyntaxNameRange(TypeSyntax? syntax, out TokenRange range)
	{
		range = default;
		return syntax switch
		{
			QualifiedNameTypeSyntax name => Assign(name.Identifier?.Range, out range),
			ArrayTypeSyntax array => TryGetTypeSyntaxNameRange(array.ElementType, out range),
			OptionalTypeSyntax optional => TryGetTypeSyntaxNameRange(optional.ElementType, out range),
			PointerTypeSyntax pointer => TryGetTypeSyntaxNameRange(pointer.ElementType, out range),
			GenericTypeSyntax generic => TryGetTypeSyntaxNameRange(generic.Type, out range),
			DeclaratorTypeSyntax declarator => TryGetTypeSyntaxNameRange(declarator.Type, out range),
			TargetTypeSpecTypeSyntax targetSpec => TryGetTypeSyntaxNameRange(targetSpec.Type, out range),
			AttributedTypeSyntax attributed => TryGetTypeSyntaxNameRange(attributed.Type, out range),
			StructTypeSyntax structType => TryGetTypeSyntaxNameRange(structType.Type, out range),
			ThrownTypeSyntax thrown => TryGetTypeSyntaxNameRange(thrown.Type, out range),
			_ => false
		};
	}

	static bool TryGetDeclarationTargetNameRange(DeclarationTarget target, out TokenRange range)
	{
		range = default;
		if (target.SourceSyntax is not DeclarationTargetSyntax syntax || target.Names.Count != 1)
			return false;
		string name = target.Names[0];
		if (syntax.Identifier?.Value == name)
			return Assign(syntax.Identifier.Value.Range, out range);
		if (syntax.AutoIdentifier?.Value == name)
			return Assign(syntax.AutoIdentifier.Value.Range, out range);
		foreach (Token identifier in syntax.IdentifierList?.Identifiers ?? [])
			if (identifier.Value == name)
				return Assign(identifier.Range, out range);
		return false;
	}

	static bool TryGetLastMemberRange(PostfixExpressionSyntax syntax, out TokenRange range)
	{
		range = default;
		for (int i = (syntax.Parts?.Count ?? 0) - 1; i >= 0; i--)
			if (syntax.Parts![i] is MemberPostfixPartSyntax member)
				return Assign(member.Identifier?.Range, out range);
		return false;
	}

	static bool TryGetSyntaxRange(SyntaxNode? syntax, out TokenRange range)
	{
		return SyntaxNodeTraversal.TryGetRange(syntax, out range);
	}

	static string? ExtractLeadingDoc(string text, int oneBasedLine)
	{
		string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
		int index = Math.Min(oneBasedLine - 2, lines.Length - 1);
		List<string> docs = [];
		while (index >= 0)
		{
			string trimmed = lines[index].TrimStart();
			if (trimmed.StartsWith("///", StringComparison.Ordinal))
			{
				docs.Add(trimmed[3..].TrimStart());
				index--;
				continue;
			}
			if (trimmed.Length == 0 && docs.Count > 0)
			{
				index--;
				continue;
			}
			break;
		}
		if (docs.Count == 0)
			return null;
		docs.Reverse();
		return string.Join("\n", docs).Trim();
	}

	static CompletionContext GetCompletionContext(string text, CampTextPosition position)
	{
		string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
		if (position.Line < 0 || position.Line >= lines.Length)
			return new CompletionContext("", false, null, null, false, false, null);
		string line = lines[position.Line];
		int cursor = Math.Clamp(position.Character, 0, line.Length);
		int prefixStart = cursor;
		while (prefixStart > 0 && IsIdentifierPart(line[prefixStart - 1]))
			prefixStart--;
		string prefix = line[prefixStart..cursor];
		int dot = prefixStart - 1;
		while (dot >= 0 && char.IsWhiteSpace(line[dot]))
			dot--;
		if (dot < 0 || line[dot] != '.')
		{
			bool isWhitespaceTrigger = cursor > 0 && char.IsWhiteSpace(line[cursor - 1]);
			string? previousWord = isWhitespaceTrigger ? PreviousWord(line, cursor) : null;
			string? overrideModifier = previousWord is "override" or "sealed" ? previousWord : null;
			return new CompletionContext(prefix, false, null, null, isWhitespaceTrigger, previousWord == "finally", overrideModifier);
		}
		int targetEnd = dot;
		while (targetEnd > 0 && char.IsWhiteSpace(line[targetEnd - 1]))
			targetEnd--;
		int targetStart = targetEnd;
		while (targetStart > 0 && IsIdentifierPart(line[targetStart - 1]))
			targetStart--;
		if (targetStart == targetEnd)
			return new CompletionContext(prefix, true, null, null, false, false, null);
		return new CompletionContext(prefix, true, new CampTextPosition(position.Line, targetStart), line[targetStart..targetEnd], false, false, null);
	}

	static string? PreviousWord(string line, int cursor)
	{
		int index = Math.Min(cursor, line.Length) - 1;
		while (index >= 0 && char.IsWhiteSpace(line[index]))
			index--;
		int end = index + 1;
		while (index >= 0 && IsIdentifierPart(line[index]))
			index--;
		return end > index + 1 ? line[(index + 1)..end] : null;
	}

	static bool TryGetCallContext(string text, CampTextPosition position, out string? targetName, out string? targetExpression, out string? memberName, out int activeParameter)
	{
		targetName = null;
		targetExpression = null;
		memberName = null;
		activeParameter = 0;
		int cursor = OffsetOf(text, position);
		if (cursor < 0)
			return false;
		int open = FindNearestOpenParenBefore(text, cursor);
		if (open < 0)
			return false;
		activeParameter = CountTopLevelCommasBetweenOffsets(text, open + 1, cursor);
		int nameEnd = open;
		while (nameEnd > 0 && char.IsWhiteSpace(text[nameEnd - 1]))
			nameEnd--;
		int nameStart = nameEnd;
		while (nameStart > 0 && IsIdentifierPart(text[nameStart - 1]))
			nameStart--;
		if (nameStart == nameEnd)
			return false;
		memberName = text[nameStart..nameEnd];
		int dot = nameStart - 1;
		while (dot >= 0 && IsHorizontalWhitespace(text[dot]))
			dot--;
		if (dot >= 0 && text[dot] == '.')
		{
			int targetEnd = dot;
			while (targetEnd > 0 && IsHorizontalWhitespace(text[targetEnd - 1]))
				targetEnd--;
			int targetStart = targetEnd;
			while (targetStart > 0 && IsIdentifierPart(text[targetStart - 1]))
				targetStart--;
			if (targetStart < targetEnd)
			{
				targetName = text[targetStart..targetEnd];
				int expressionStart = targetStart;
				while (expressionStart > 0 && IsIdentifierOrMemberAccessPart(text[expressionStart - 1]))
					expressionStart--;
				targetExpression = text[expressionStart..targetEnd];
			}
		}
		return true;
	}

	static bool IsHorizontalWhitespace(char value)
	{
		return value is ' ' or '\t';
	}

	static bool IsIdentifierOrMemberAccessPart(char value)
	{
		return IsIdentifierPart(value) || value == '.';
	}

	static int FindNearestOpenParenBefore(string text, int cursor)
	{
		int depth = 0;
		bool inString = false;
		char quote = '\0';
		for (int i = Math.Min(cursor - 1, text.Length - 1); i >= 0; i--)
		{
			char value = text[i];
			if (inString)
			{
				if (value == quote && (i == 0 || text[i - 1] != '\\'))
					inString = false;
				continue;
			}
			if (value is '"' or '\'')
			{
				inString = true;
				quote = value;
				continue;
			}
			if (value == ')')
			{
				depth++;
				continue;
			}
			if (value == '(')
			{
				if (depth == 0)
					return i;
				depth--;
			}
			if (value == ';' || value == '{' || value == '}')
				return -1;
		}
		return -1;
	}

	static int CountTopLevelCommasBetweenOffsets(string text, int start, int end)
	{
		int depth = 0;
		int count = 0;
		bool inString = false;
		char quote = '\0';
		for (int i = Math.Max(0, start); i < Math.Min(text.Length, end); i++)
		{
			char value = text[i];
			if (inString)
			{
				if (value == '\\')
					i++;
				else if (value == quote)
					inString = false;
				continue;
			}
			if (value is '"' or '\'')
			{
				inString = true;
				quote = value;
				continue;
			}
			if (value is '(' or '[' or '{')
				depth++;
			else if (value is ')' or ']' or '}')
				depth = Math.Max(0, depth - 1);
			else if (value == ',' && depth == 0)
				count++;
		}
		return count;
	}

	static Dictionary<string, string> ExtractLeadingParameterDocs(string text, int oneBasedLine)
	{
		string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
		int index = Math.Min(oneBasedLine - 2, lines.Length - 1);
		List<string> docs = [];
		while (index >= 0)
		{
			string trimmed = lines[index].TrimStart();
			if (trimmed.StartsWith("///", StringComparison.Ordinal))
			{
				docs.Add(trimmed[3..].TrimStart());
				index--;
				continue;
			}
			if (trimmed.Length == 0 && docs.Count > 0)
			{
				index--;
				continue;
			}
			break;
		}
		docs.Reverse();
		Dictionary<string, string> result = new(StringComparer.Ordinal);
		foreach (string line in docs)
		{
			string trimmed = line.Trim();
			if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
				continue;
			int colon = trimmed.IndexOf(':', 2);
			if (colon < 0)
				continue;
			string name = trimmed[2..colon].Trim();
			string value = trimmed[(colon + 1)..].Trim();
			if (name.Length > 0 && value.Length > 0)
				result[name] = value;
		}
		return result;
	}

	static string SourceText(string text, TokenRange range)
	{
		string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
		int startLine = Math.Max(0, range.StartLineNumber - 1);
		int endLine = Math.Min(lines.Length - 1, Math.Max(0, range.EndLineNumber - 1));
		if (startLine > endLine || startLine >= lines.Length)
			return "";
		if (startLine == endLine)
		{
			int start = Math.Clamp(range.StartColumn - 1, 0, lines[startLine].Length);
			int end = Math.Clamp(range.EndColumn - 1, start, lines[startLine].Length);
			return lines[startLine][start..end];
		}

		StringBuilder builder = new();
		for (int line = startLine; line <= endLine; line++)
		{
			string value = lines[line];
			int start = line == startLine ? Math.Clamp(range.StartColumn - 1, 0, value.Length) : 0;
			int end = line == endLine ? Math.Clamp(range.EndColumn - 1, start, value.Length) : value.Length;
			if (builder.Length > 0)
				builder.Append('\n');
			builder.Append(value[start..end]);
		}
		return builder.ToString();
	}

	static string? FirstIdentifier(string text)
	{
		for (int i = 0; i < text.Length; i++)
		{
			if (!IsIdentifierStart(text[i]))
				continue;
			int start = i;
			i++;
			while (i < text.Length && IsIdentifierPart(text[i]))
				i++;
			return text[start..i];
		}
		return null;
	}

	static bool TryGetWordAt(string text, CampTextPosition position, out string? word)
	{
		if (TryGetWordRangeAt(text, position, out word, out _))
			return true;
		word = null;
		return false;
	}

	static bool TryGetWordRangeAt(string text, CampTextPosition position, out string? word, out CampTextRange range)
	{
		word = null;
		range = default!;
		string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
		if (position.Line < 0 || position.Line >= lines.Length)
			return false;
		string line = lines[position.Line];
		if (position.Character < 0 || position.Character >= line.Length)
			return false;
		int start = position.Character;
		if (!IsIdentifierPart(line[start]) && start > 0 && IsIdentifierPart(line[start - 1]))
			start--;
		if (!IsIdentifierPart(line[start]))
			return false;
		int end = start + 1;
		while (start > 0 && IsIdentifierPart(line[start - 1]))
			start--;
		while (end < line.Length && IsIdentifierPart(line[end]))
			end++;
		word = line[start..end];
		range = new CampTextRange(new CampTextPosition(position.Line, start), new CampTextPosition(position.Line, end));
		return true;
	}

	static bool IsIdentifierStart(char value)
	{
		return char.IsLetter(value) || value == '_';
	}

	static bool IsIdentifierPart(char value)
	{
		return char.IsLetterOrDigit(value) || value == '_';
	}

	static bool Assign(TokenRange? value, out TokenRange range)
	{
		range = value ?? default;
		return value is not null;
	}

	static IEnumerable<BindableNode> Children(BindableNode node)
	{
		return BindableNodeTraversal.Children(node);
	}

	string? ResolveCompletionTargetName(string name)
	{
		TypeDefinition? type = ResolveCompletionType(name);
		if (type is not null)
			return type.Name;
		SymbolEntry? entry = entries
			.Where(entry => entry.Definition is null && entry.Name == name && entry.Kind is CampSymbolKind.Variable or CampSymbolKind.Parameter or CampSymbolKind.Field)
			.OrderBy(static entry => entry.Range.Start.Line)
			.ThenBy(static entry => entry.Range.Start.Character)
			.LastOrDefault();
		if (entry?.Type is null)
			return name;
		if (TryReconstructExpandedArrayType(entry, out string? expandedArrayType))
			return expandedArrayType;
		return BaseTypeName(UnwrapCompletionStorageType(entry.Type));
	}

	bool TryReconstructExpandedArrayType(SymbolEntry entry, out string? type)
	{
		type = null;
		string elementPointer = StripLeadingStorageQualifiers(entry.Type ?? "").TrimEnd();
		if (!elementPointer.EndsWith("*", StringComparison.Ordinal) || elementPointer == "void*")
			return false;
		string lengthName = entry.Name + "_length";
		bool hasLength = entries.Any(candidate =>
			candidate.Definition is null
			&& candidate.Name == lengthName
			&& candidate.Kind == entry.Kind
			&& candidate.ContainerName == entry.ContainerName);
		if (!hasLength)
			return false;
		string elementType = elementPointer[..^1].TrimEnd();
		type = elementType + "[]";
		return true;
	}

	static string UnwrapCompletionStorageType(string type)
	{
		string result = StripLeadingStorageQualifiers(type);
		while (result.EndsWith("*", StringComparison.Ordinal))
			result = result[..^1].TrimEnd();
		return result;
	}

	sealed record SymbolEntry(
		string Path,
		CampTextRange Range,
		string Name,
		CampSymbolKind Kind,
		string? Type,
		string? Signature,
		string? Documentation,
		string? ContainerName,
		bool IsDeclaration,
		SymbolEntry? Definition);

	sealed record CompletionContext(
		string Prefix,
		bool IsMember,
		CampTextPosition? MemberTargetPosition,
		string? MemberTargetText,
		bool IsWhitespaceTrigger,
		bool IsAfterFinally,
		string? OverrideModifier);
}
