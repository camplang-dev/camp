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

public sealed record CampHover(string Markdown);

public sealed class CampSymbolQueryService(CampAnalysisSnapshot snapshot)
{
	readonly List<SymbolEntry> entries = BuildEntries(snapshot.Compilation);

	public CampSymbolInfo? GetSymbolAt(string path, CampTextPosition position)
	{
		SymbolEntry? entry = FindEntry(path, position);
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

	SymbolEntry? FindEntry(string path, CampTextPosition position)
	{
		string fullPath = Path.GetFullPath(path);
		return entries
			.Where(entry => string.Equals(entry.Path, fullPath, StringComparison.OrdinalIgnoreCase) && Contains(entry.Range, position))
			.OrderBy(entry => SpanSize(entry.Range))
			.FirstOrDefault();
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
				CollectDefinitionEntries(file, file.BindableTree, entries, definitions);
		}
		foreach (SourceFile file in compilation.Files)
		{
			if (file.BindableTree is not null)
				CollectReferenceEntries(file, file.BindableTree, entries, definitions, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance));
		}
		return entries;
	}

	static void CollectDefinitionEntries(SourceFile file, BindableNode node, List<SymbolEntry> entries, Dictionary<BindableNode, SymbolEntry> definitions)
	{
		foreach (BindableNode child in Children(node))
		{
			if (TryCreateDefinitionEntry(file, child, out SymbolEntry? entry))
			{
				entries.Add(entry!);
				definitions[child] = entry!;
			}
			CollectDefinitionEntries(file, child, entries, definitions);
		}
	}

	static bool TryCreateDefinitionEntry(SourceFile file, BindableNode node, out SymbolEntry? entry)
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
			GetKind(node),
			GetNodeType(node),
			GetSignature(node),
			GetDocumentation(node) ?? ExtractLeadingDoc(file.Text, tokenRange.StartLineNumber),
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
			MethodReferenceExpression method => method.Candidates.FirstOrDefault(),
			TypeReferenceExpression type => ResolveTypeTarget(type.Type, definitions),
			NamedTypeReference named => ResolveTypeTarget(named, definitions),
			NameOfExpression nameOf => nameOf.Reference,
			SymbolOfExpression symbolOf => symbolOf.Reference,
			_ => null
		};
		if (target is null || !definitions.TryGetValue(target, out SymbolEntry? definition))
			return false;
		if (!TryGetNodeRange(node, out TokenRange tokenRange) || !ReferenceEquals(file.Tokens, tokenRange.Sequence))
			return false;
		entry = definition with
		{
			Path = Path.GetFullPath(file.Path),
			Range = CampLanguageService.ToTextRange(tokenRange),
			Definition = definition
		};
		return true;
	}

	static BindableNode? ResolveTypeTarget(TypeReference? type, Dictionary<BindableNode, SymbolEntry> definitions)
	{
		string? name = type switch
		{
			NamedTypeReference named => BaseTypeName(named.ResolvedType ?? named.Name),
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
			case MemberReferenceExpression { SourceSyntax: PostfixExpressionSyntax syntax }:
				return TryGetLastMemberRange(syntax, out range);
			case MethodReferenceExpression { SourceSyntax: QualifiedNameExpressionSyntax syntax }:
				return Assign(syntax.Identifier?.Range, out range);
			case TypeReferenceExpression:
				return TryGetSyntaxRange(node.SourceSyntax, out range);
			case NamedTypeReference:
				return TryGetSyntaxRange(node.SourceSyntax, out range);
		}
		return false;
	}

	static bool TryGetDeclarationTargetNameRange(DeclarationTarget target, out TokenRange range)
	{
		range = default;
		if (target.SourceSyntax is not DeclarationTargetSyntax syntax || target.Names.Count != 1)
			return TryGetSyntaxRange(target.SourceSyntax, out range);
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
		SymbolEntry? Definition);
}
