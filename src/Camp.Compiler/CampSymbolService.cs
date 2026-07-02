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
	Alias
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

public sealed class CampSymbolQueryService(CampAnalysisSnapshot snapshot)
{
	readonly List<SymbolEntry> entries = BuildEntries(snapshot.Compilation);

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
			GetDocumentation(node) ?? ExtractLeadingDoc(file.Text, tokenRange.StartLineNumber),
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
		using StringWriter writer = new();
		BindableNodeCodeSerializer.Serialize(definition, writer, new BindableNodeCodeSerializerOptions { ApiHeader = false });
		string text = writer.ToString().Trim();
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
		IEnumerable<AttributeConstructor> attributes = node switch
		{
			Definition definition => definition.Attributes,
			GenericParameter parameter => parameter.Attributes,
			_ => []
		};
		List<string> docs = [];
		foreach (AttributeConstructor attribute in attributes.Where(static attribute => attribute.Name is "summary" or "remarks"))
		{
			string? value = attribute.Arguments.FirstOrDefault()?.Value is LiteralExpression literal ? literal.Value?.ToString() : null;
			if (!string.IsNullOrWhiteSpace(value))
				docs.Add(value!);
		}
		return docs.Count == 0 ? null : string.Join("\n\n", docs);
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
}
