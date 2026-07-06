using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Camp.Compiler;

public enum CampSymbolKind
{
	Unknown,
	Type,
	Function,
	Method,
	Field,
	Variable,
	Parameter,
	EnumValue,
	Alias,
	Keyword
}

public sealed record CampSymbolLocation(string Path, CampTextRange Range);

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
	string? Documentation);

public sealed class CampSymbolQueryService(CampAnalysisSnapshot snapshot)
{
	readonly List<SymbolEntry> entries = BuildEntries(snapshot.Compilation);
	readonly List<FunctionDefinition> functions = BuildFunctions(snapshot.Compilation);

	public CampSymbolInfo? GetSymbolAt(string path, CampTextPosition position)
	{
		SymbolEntry? entry = FindEntry(path, position) ?? FindNamedDefinitionEntry(path, position) ?? FindPropertyEntry(path, position) ?? FindExpandedComponentEntry(path, position);
		return entry is null ? null : ToInfo(entry);
	}

	public CampSymbolLocation? GetDefinition(string path, CampTextPosition position)
	{
		return GetSymbolAt(path, position)?.Definition;
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

	public IReadOnlyList<CampCompletionItem> GetCompletions(string path, CampTextPosition position, string? currentText = null)
	{
		string fullPath = Path.GetFullPath(path);
		SourceFile? file = snapshot.Compilation.Files.FirstOrDefault(file => string.Equals(Path.GetFullPath(file.Path), fullPath, StringComparison.OrdinalIgnoreCase));
		if (file?.BindableTree is null)
			return [];
		CompletionContext context = GetCompletionContext(currentText ?? file.Text, position);
		List<CampCompletionItem> completions = context.IsMember
			? GetMemberCompletions(file, context)
			: GetScopeCompletions(file, position);
		return completions
			.Where(item => context.Prefix.Length == 0 || item.Label.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase))
			.DistinctBy(static item => (item.Label, item.Kind, item.Detail))
			.OrderBy(static item => CompletionSortBucket(item.Kind))
			.ThenBy(static item => item.Label, StringComparer.OrdinalIgnoreCase)
			.ToList();
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
			: context.MemberTargetText is null ? null : ResolveCompletionTargetName(context.MemberTargetText);
		targetType ??= target?.Type;
		if (targetType is null && target?.Kind is CampSymbolKind.Type or CampSymbolKind.Alias)
			targetType = target.Name;
		TypeDefinition? type = ResolveCompletionType(targetType);
		if (type is null)
			return [];
		List<CampCompletionItem> completions = [];
		CollectTypeMemberCompletions(file, type, completions, new HashSet<TypeDefinition>(ReferenceEqualityComparer.Instance));
		return completions;
	}

	static IEnumerable<CampCompletionItem> GetKeywordCompletions()
	{
		foreach (string keyword in new[]
		{
			"if", "else", "while", "for", "foreach", "return", "try", "catch", "finally",
			"new", "init", "default", "true", "false", "null", "using", "export",
			"class", "struct", "interface", "enum", "newtype", "delegate", "fn"
		})
			yield return new CampCompletionItem(keyword, CampSymbolKind.Keyword, null, null);
	}

	static int CompletionSortBucket(CampSymbolKind kind)
	{
		return kind switch
		{
			CampSymbolKind.Variable or CampSymbolKind.Parameter => 0,
			CampSymbolKind.Field => 1,
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

	void CollectTypeMemberCompletions(SourceFile file, TypeDefinition type, List<CampCompletionItem> completions, HashSet<TypeDefinition> visited)
	{
		if (!visited.Add(type))
			return;
		switch (type)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
					if (TryCreateCompletion(file, field, type.Name, out CampCompletionItem? item))
						completions.Add(item!);
				foreach (FunctionDefinition function in classDefinition.Functions)
					if (!IsHiddenMemberCompletionFunction(function) && TryCreateCompletion(file, function, type.Name, out CampCompletionItem? item))
						completions.Add(item!);
				foreach (TypeReference baseType in classDefinition.BaseTypes)
					CollectBaseTypeMemberCompletions(file, baseType, completions, visited);
				break;
			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					if (TryCreateCompletion(file, field, type.Name, out CampCompletionItem? item))
						completions.Add(item!);
				foreach (FunctionDefinition function in structDefinition.Functions)
					if (!IsHiddenMemberCompletionFunction(function) && TryCreateCompletion(file, function, type.Name, out CampCompletionItem? item))
						completions.Add(item!);
				break;
			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					if (!IsHiddenMemberCompletionFunction(function) && TryCreateCompletion(file, function, type.Name, out CampCompletionItem? item))
						completions.Add(item!);
				break;
			case EnumDefinition enumDefinition:
				foreach (VariableDefinition value in enumDefinition.Values)
					if (TryCreateCompletion(file, value, type.Name, out CampCompletionItem? item))
						completions.Add(item!);
				break;
			case NewtypeDefinition newtypeDefinition:
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					if (!IsHiddenMemberCompletionFunction(function) && TryCreateCompletion(file, function, type.Name, out CampCompletionItem? item))
						completions.Add(item!);
				break;
		}

		foreach (FunctionDefinition extension in functions.Where(function => function.Parameters.FirstOrDefault() is ThisParameterDefinition thisParameter && BaseTypeName(UnwrapStorageType(thisParameter.ResolvedType ?? "")) == type.Name))
			if (!IsHiddenMemberCompletionFunction(extension) && TryCreateCompletion(file, extension, type.Name, out CampCompletionItem? item))
				completions.Add(item!);
	}

	static bool IsHiddenMemberCompletionFunction(FunctionDefinition function)
	{
		return function.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor
			|| function.Name is "create" or "destroy" or "op_initnew" or "op_delete"
			|| function.Symbol.EndsWith("_create", StringComparison.Ordinal)
			|| function.Symbol.EndsWith("_destroy", StringComparison.Ordinal)
			|| function.Symbol.EndsWith("_op_initnew", StringComparison.Ordinal)
			|| function.Symbol.EndsWith("_op_delete", StringComparison.Ordinal);
	}

	void CollectBaseTypeMemberCompletions(SourceFile file, TypeReference? baseType, List<CampCompletionItem> completions, HashSet<TypeDefinition> visited)
	{
		TypeDefinition? definition = ResolveCompletionType(baseType?.ResolvedType ?? BindableNodeCodeSerializer.SerializeType(baseType));
		if (definition is not null)
			CollectTypeMemberCompletions(file, definition, completions, visited);
	}

	static bool TryCreateCompletion(SourceFile file, BindableNode node, string? containerName, out CampCompletionItem? item)
	{
		item = null;
		if (!TryCreateDefinitionEntry(file, node, containerName, out SymbolEntry? entry))
			return false;
		item = new CampCompletionItem(entry!.Name, entry.Kind, entry.Signature ?? entry.Type, entry.Documentation);
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
			CampSymbolKind.Field,
			word == "length" ? "nuint" : null,
			null,
			null,
			null,
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
		if (!definitions.TryGetValue(target, out SymbolEntry? definition))
		{
			if (node is not MemberReferenceExpression member)
				return false;
			entry = new SymbolEntry(
				Path.GetFullPath(file.Path),
				CampLanguageService.ToTextRange(tokenRange),
				member.Name,
				GetKind(target),
				GetNodeType(target) ?? node.ResolvedType,
				GetSignature(target),
				GetDocumentation(target),
				null,
				null);
			return true;
		}
		entry = definition with
		{
			Path = Path.GetFullPath(file.Path),
			Range = CampLanguageService.ToTextRange(tokenRange),
			Definition = definition
		};
		return true;
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

	static IEnumerable<FunctionDefinition> GetCallFunctions(CallExpression call, IReadOnlyList<FunctionDefinition> functions)
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
			foreach (FunctionDefinition candidate in functions.Where(function => function.Name == memberExpression.Name || function.Symbol.EndsWith("_" + memberExpression.Name, StringComparison.Ordinal)))
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
			foreach (FunctionDefinition candidate in functions.Where(function => function.Name == named.Name || function.Symbol == named.Name))
				yield return candidate;
		}
	}

	CampSignatureHelp? GetSignatureHelpFromText(SourceFile file, string text, CampTextPosition position)
	{
		if (!TryGetCallContext(text, position, out string? targetName, out string? memberName, out int activeParameter))
			return null;
		string? resolvedTargetName = targetName is null ? null : ResolveCompletionTargetName(targetName);
		List<FunctionDefinition> callFunctions = functions
			.Where(function => FunctionMatchesCallContext(function, resolvedTargetName ?? targetName, memberName))
			.Distinct()
			.ToList();
		if (callFunctions.Count == 0)
			return GetSignatureHelpFromEntries(file, position, resolvedTargetName ?? targetName, memberName, activeParameter);
		return new CampSignatureHelp(
			callFunctions.Select(function => CreateSignatureInformation(function, file)).ToList(),
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
			candidates = candidates.Where(entry => entry.ContainerName == targetName || entry.Signature!.Contains(targetName + "_", StringComparison.Ordinal) || entry.Signature.Contains("." + name, StringComparison.Ordinal));
		List<CampSignatureInformation> signatures = candidates
			.DistinctBy(static entry => entry.Signature)
			.Select(static entry => new CampSignatureInformation(entry.Signature!, entry.Documentation, []))
			.ToList();
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

	static bool FunctionMatchesCallContext(FunctionDefinition function, string? targetName, string? memberName)
	{
		string name = memberName ?? targetName ?? "";
		if (name.Length == 0)
			return false;
		if (targetName is null)
			return function.Name == name || function.Symbol == name;
		return function.Name == name
			&& (function.Symbol == targetName + "_" + name
				|| function.Symbol.EndsWith("_" + name, StringComparison.Ordinal));
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
			return visible;
		List<ParameterDefinition> result = [];
		foreach (ParameterDefinition parameter in visible)
		{
			if (IsExpandedComponentParameter(result.LastOrDefault(), parameter))
				continue;
			result.Add(parameter);
		}
		return result;
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
			FieldDefinition => CampSymbolKind.Field,
			VariableDefinition variable when variable.SourceSyntax is EnumValueSyntax => CampSymbolKind.EnumValue,
			VariableDefinition => CampSymbolKind.Variable,
			ParameterDefinition or LambdaParameter => CampSymbolKind.Parameter,
			DeclarationTarget => CampSymbolKind.Variable,
			_ => CampSymbolKind.Unknown
		};
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

	static string? GetSignatureHelpLabel(FunctionDefinition function)
	{
		string? label = GetCleanSignature(function);
		if (string.IsNullOrWhiteSpace(label))
			return label;
		label = RemoveSignatureKeyword(label!, "extern");
		HashSet<string> hiddenParameters = GetHiddenSignatureHelpParameterNames(function);
		return hiddenParameters.Count == 0 ? label : RemoveParametersFromSignatureLabel(label, hiddenParameters);
	}

	static HashSet<string> GetHiddenSignatureHelpParameterNames(FunctionDefinition function)
	{
		HashSet<string> hidden = new(StringComparer.Ordinal);
		List<ParameterDefinition> displayed = [];
		foreach (ParameterDefinition parameter in GetVisibleCallParameters(function))
		{
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
		range = default;
		if (syntax is null)
			return false;
		foreach (PropertyInfo property in syntax.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.PropertyType == typeof(TokenRange?) && property.GetValue(syntax) is TokenRange tokenRange)
				return Assign(tokenRange, out range);
			if (property.PropertyType == typeof(Token?) && property.GetValue(syntax) is Token token)
				return Assign(token.Range, out range);
		}
		foreach (PropertyInfo property in syntax.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			object? value = property.GetValue(syntax);
			if (value is SyntaxNode child && TryGetSyntaxRange(child, out range))
				return true;
			if (value is IEnumerable enumerable and not string)
			{
				foreach (object? item in enumerable)
					if (item is SyntaxNode childItem && TryGetSyntaxRange(childItem, out range))
						return true;
			}
		}
		return false;
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
			return new CompletionContext("", false, null, null);
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
			return new CompletionContext(prefix, false, null, null);
		int targetEnd = dot;
		while (targetEnd > 0 && char.IsWhiteSpace(line[targetEnd - 1]))
			targetEnd--;
		int targetStart = targetEnd;
		while (targetStart > 0 && IsIdentifierPart(line[targetStart - 1]))
			targetStart--;
		if (targetStart == targetEnd)
			return new CompletionContext(prefix, true, null, null);
		return new CompletionContext(prefix, true, new CampTextPosition(position.Line, targetStart), line[targetStart..targetEnd]);
	}

	static bool TryGetCallContext(string text, CampTextPosition position, out string? targetName, out string? memberName, out int activeParameter)
	{
		targetName = null;
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
				targetName = text[targetStart..targetEnd];
		}
		return true;
	}

	static bool IsHorizontalWhitespace(char value)
	{
		return value is ' ' or '\t';
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
		foreach (PropertyInfo property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.GetIndexParameters().Length != 0 || property.Name is nameof(BindableNode.SourceSyntax))
				continue;
			object? value = property.GetValue(node);
			if (value is BindableNode child)
			{
				yield return child;
				continue;
			}
			if (value is string or null)
				continue;
			if (value is IEnumerable enumerable)
			{
				foreach (object? item in enumerable)
					if (item is BindableNode enumerableChild)
						yield return enumerableChild;
			}
		}
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
		return entry?.Type is null ? name : BaseTypeName(UnwrapStorageType(entry.Type));
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
		SymbolEntry? Definition);

	sealed record CompletionContext(
		string Prefix,
		bool IsMember,
		CampTextPosition? MemberTargetPosition,
		string? MemberTargetText);
}
