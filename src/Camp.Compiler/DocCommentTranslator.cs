using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Camp.Compiler;

public static class DocCommentTranslator
{
	static readonly HashSet<string> DocAttributeNames = new(StringComparer.Ordinal)
	{
		"summary",
		"remarks",
		"returns",
		"example",
		"see",
		"deprecated",
		"overload",
		"category"
	};

	static readonly HashSet<string> KnownAttributeNames = new(StringComparer.Ordinal)
	{
		"summary",
		"remarks",
		"returns",
		"example",
		"see",
		"deprecated",
		"overload",
		"category",
		"test",
		"skip",
		"range",
		"index",
		"symbol",
		"nosuffix",
		"getshadow",
		"setshadow"
	};

	static readonly Regex ChildTargetPattern = new(@"^-\s*([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.*)$", RegexOptions.Compiled);
	static readonly Regex AttributePattern = new(@"^@([A-Za-z_][A-Za-z0-9_]*)(?:\s+(.*))?$", RegexOptions.Compiled);

	public static IReadOnlyList<BindDiagnostic> Apply(SourceFile file)
	{
		List<BindDiagnostic> diagnostics = [];
		if (file.Tokens is null || file.BindableTree is null)
			return diagnostics;

		List<DocBlock> blocks = CollectDocBlocks(file.Tokens);
		if (blocks.Count == 0)
			return diagnostics;

		List<NodeTarget> targets = CollectTargets(file.BindableTree);
		targets.Sort((left, right) => left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column));

		foreach (DocBlock block in blocks)
		{
			Token? next = FindNextSignificantToken(file.Tokens, block.EndIndex + 1);
			if (next is null)
				continue;
			NodeTarget? target = FindTarget(targets, next.Value.LineNumber, next.Value.Column);
			if (target is null)
				continue;

			ApplyBlock(block, target, file.BindableTree, diagnostics);
		}

		return diagnostics;
	}

	static void ApplyBlock(DocBlock block, NodeTarget target, Module module, List<BindDiagnostic> diagnostics)
	{
		List<ParsedDocAttribute> attributes = ParseDocBlock(block, diagnostics);
		foreach (ParsedDocAttribute attribute in attributes)
		{
			if (!KnownAttributeNames.Contains(attribute.Name))
			{
				diagnostics.Add(new BindDiagnostic(block.Range, $"Unknown doc-comment attribute '@{attribute.Name}'."));
				continue;
			}

			foreach (string symbol in attribute.Symbols)
				if (ResolveSymbol(symbol, target.Node, module) is null)
					diagnostics.Add(new BindDiagnostic(block.Range, $"symbolof reference '{symbol}' could not be resolved."));

			AttributeConstructor constructor = CreateAttribute(attribute, target.Node, module);
			if (attribute.ChildTarget is null)
			{
				AddAttribute(target.Node, constructor);
				continue;
			}

			BindableNode? child = ResolveChildTarget(target.Node, attribute.ChildTarget);
			if (child is null)
			{
				diagnostics.Add(new BindDiagnostic(block.Range, $"Doc-comment child target '{attribute.ChildTarget}' could not be found on declaration '{GetNodeName(target.Node)}'."));
				continue;
			}
			AddAttribute(child, constructor);
		}
	}

	static AttributeConstructor CreateAttribute(ParsedDocAttribute attribute, BindableNode owner, Module module)
	{
		AttributeConstructor constructor = new()
		{
			Name = "@" + attribute.Name,
			IsDocCommentAttribute = DocAttributeNames.Contains(attribute.Name)
		};

		if (!string.IsNullOrWhiteSpace(attribute.Content))
		{
			constructor.Arguments.Add(new ArgumentExpression
			{
				Value = new LiteralExpression
				{
					Kind = LiteralKind.String,
					Text = Quote(attribute.Content),
					Value = attribute.Content,
					ResolvedType = "string"
				},
				ResolvedType = "string"
			});
		}

		if (attribute.Symbols.Count > 0)
		{
			ArrayExpression symbols = new() { ResolvedType = "SymbolRef[]" };
			foreach (string text in attribute.Symbols)
			{
				SymbolOfExpression symbol = new()
				{
					Text = text,
					Reference = ResolveSymbol(text, owner, module) ?? owner,
					ResolvedType = "SymbolRef"
				};
				symbols.Elements.Add(symbol);
			}
			constructor.Arguments.Add(new ArgumentExpression
			{
				Name = "symbols",
				Value = symbols,
				ResolvedType = "SymbolRef[]"
			});
		}

		return constructor;
	}

	static List<ParsedDocAttribute> ParseDocBlock(DocBlock block, List<BindDiagnostic> diagnostics)
	{
		List<ParsedDocAttribute> result = [];
		CurrentDocContext? current = null;
		bool inFence = false;

		foreach (string rawLine in block.Lines)
		{
			string line = rawLine;
			string trimmed = line.Trim();
			bool fenceLine = trimmed.StartsWith("```", StringComparison.Ordinal);
			if (!inFence)
			{
				Match child = ChildTargetPattern.Match(trimmed);
				if (child.Success)
				{
					current = StartContext(result, child.Groups[1].Value, "summary");
					AppendChildContent(current, child.Groups[2].Value);
					if (fenceLine)
						inFence = !inFence;
					continue;
				}

				Match attribute = AttributePattern.Match(trimmed);
				if (attribute.Success)
				{
					current = StartContext(result, null, attribute.Groups[1].Value);
					if (attribute.Groups[2].Success)
						current.Lines.Add(attribute.Groups[2].Value);
					if (fenceLine)
						inFence = !inFence;
					continue;
				}
			}

			current ??= StartContext(result, null, "summary");
			current.Lines.Add(line);
			if (fenceLine)
				inFence = !inFence;
		}

		foreach (CurrentDocContext context in result.ConvertAll(static a => a.Context!))
			FinalizeContext(context);
		foreach (ParsedDocAttribute attribute in result)
			attribute.Context = null;
		return result;

		void AppendChildContent(CurrentDocContext context, string content)
		{
			string trimmedContent = content.Trim();
			Match marker = AttributePattern.Match(trimmedContent);
			if (marker.Success)
			{
				context.Attribute.Name = marker.Groups[1].Value;
				if (marker.Groups[2].Success)
					context.Lines.Add(marker.Groups[2].Value);
			}
			else if (trimmedContent.Length > 0)
			{
				context.Lines.Add(trimmedContent);
			}
		}
	}

	static CurrentDocContext StartContext(List<ParsedDocAttribute> result, string? childTarget, string name)
	{
		ParsedDocAttribute attribute = new(name, childTarget);
		CurrentDocContext context = new(attribute);
		attribute.Context = context;
		result.Add(attribute);
		return context;
	}

	static void FinalizeContext(CurrentDocContext context)
	{
		string normalized = NormalizeLines(context.Lines, out List<string> symbols);
		context.Attribute.Content = normalized;
		context.Attribute.Symbols.AddRange(symbols);
	}

	static string NormalizeLines(List<string> lines, out List<string> symbols)
	{
		symbols = [];
		StringBuilder builder = new();
		bool inFence = false;
		bool pendingParagraph = false;
		bool pendingSpace = false;

		foreach (string rawLine in lines)
		{
			string line = rawLine;
			string trimmed = line.Trim();
			bool fenceLine = trimmed.StartsWith("```", StringComparison.Ordinal);
			if (!inFence && trimmed.Length == 0)
			{
				pendingParagraph = builder.Length > 0;
				pendingSpace = false;
				continue;
			}

			if (pendingParagraph)
			{
				builder.Append("\n\n");
				pendingParagraph = false;
			}
			else if (pendingSpace && builder.Length > 0)
			{
				builder.Append(' ');
			}

			if (inFence)
			{
				builder.Append(EscapePercent(line));
				builder.Append('\n');
			}
			else if (fenceLine)
			{
				builder.Append(TransformDocText(trimmed, symbols));
				builder.Append('\n');
			}
			else
			{
				builder.Append(TransformDocText(trimmed, symbols));
			}

			if (fenceLine)
				inFence = !inFence;
			pendingSpace = !inFence;
		}

		return builder.ToString().Trim();
	}

	static string TransformDocText(string text, List<string> symbols)
	{
		StringBuilder builder = new();
		bool inCode = false;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (c == '`')
			{
				inCode = !inCode;
				builder.Append(c);
				continue;
			}
			if (!inCode && c == '[')
			{
				int close = text.IndexOf(']', i + 1);
				if (close > i + 1)
				{
					string symbol = text[(i + 1)..close];
					symbols.Add(symbol);
					builder.Append("%s");
					i = close;
					continue;
				}
			}
			if (c == '%')
				builder.Append("%%");
			else
				builder.Append(c);
		}
		return builder.ToString();
	}

	static string EscapePercent(string text)
	{
		return text.Replace("%", "%%", StringComparison.Ordinal);
	}

	static List<DocBlock> CollectDocBlocks(TokenSequence tokens)
	{
		List<DocBlock> blocks = [];
		for (int i = 0; i < tokens.Count; i++)
		{
			Token token = tokens[i];
			if (token.Class == TokenClass.LineComment && token.Value.StartsWith("///", StringComparison.Ordinal))
			{
				List<string> lines = [];
				int start = i;
				TokenRange range = token.Range;
				while (i < tokens.Count)
				{
					Token current = tokens[i];
					if (current.Class != TokenClass.LineComment || !current.Value.StartsWith("///", StringComparison.Ordinal))
						break;
					lines.Add(StripLineDocPrefix(current.Value));
					range = new TokenRange(tokens, start, i - start + 1);
					i = NextLineIndex(tokens, i + 1);
					int lookahead = SkipLineIndent(tokens, SkipWhitespaceOnlyLines(tokens, i));
					if (lookahead < tokens.Count && tokens[lookahead].Class == TokenClass.LineComment && tokens[lookahead].Value.StartsWith("///", StringComparison.Ordinal))
						i = lookahead;
					else
						break;
				}
				blocks.Add(new DocBlock(lines, start, range.Index + range.Count - 1, range));
			}
			else if (token.Class == TokenClass.BlockComment && token.Value.StartsWith("/**", StringComparison.Ordinal))
			{
				List<string> lines = [];
				int start = i;
				TokenRange range = token.Range;
				while (i < tokens.Count)
				{
					Token current = tokens[i];
					if (current.Class != TokenClass.BlockComment)
						break;
					if (!IsBlockCommentStructuralNewLine(current.Value))
						lines.Add(StripBlockDocPrefix(current.Value));
					range = new TokenRange(tokens, start, i - start + 1);
					if (current.Value.Contains("*/", StringComparison.Ordinal))
						break;
					i++;
				}
				blocks.Add(new DocBlock(lines, start, range.Index + range.Count - 1, range));
			}
		}
		return blocks;
	}

	static int NextLineIndex(TokenSequence tokens, int start)
	{
		while (start < tokens.Count && tokens[start].Class != TokenClass.NewLine)
			start++;
		return start < tokens.Count ? start + 1 : start;
	}

	static int SkipLineIndent(TokenSequence tokens, int start)
	{
		while (start < tokens.Count && tokens[start].Class == TokenClass.Whitespace)
			start++;
		return start;
	}

	static int SkipWhitespaceOnlyLines(TokenSequence tokens, int start)
	{
		int i = start;
		while (i < tokens.Count)
		{
			int lineStart = i;
			bool onlyWhitespace = true;
			while (i < tokens.Count && tokens[i].Class != TokenClass.NewLine)
			{
				if (tokens[i].Class != TokenClass.Whitespace)
					onlyWhitespace = false;
				i++;
			}
			if (!onlyWhitespace)
				return lineStart;
			if (i < tokens.Count && tokens[i].Class == TokenClass.NewLine)
				i++;
		}
		return i;
	}

	static Token? FindNextSignificantToken(TokenSequence tokens, int start)
	{
		for (int i = start; i < tokens.Count; i++)
		{
			Token token = tokens[i];
			if (token.Class is TokenClass.Whitespace or TokenClass.NewLine)
				continue;
			if (token.Class is TokenClass.LineComment or TokenClass.BlockComment)
				return null;
			return token;
		}
		return null;
	}

	static string StripLineDocPrefix(string value)
	{
		string text = value.Length >= 3 ? value[3..] : "";
		return text.StartsWith(" ", StringComparison.Ordinal) ? text[1..] : text;
	}

	static string StripBlockDocPrefix(string value)
	{
		string text = value;
		if (text.StartsWith("/**", StringComparison.Ordinal))
			text = text[3..];
		if (text.EndsWith("*/", StringComparison.Ordinal))
			text = text[..^2];
		text = text.TrimEnd();
		text = text.TrimStart();
		if (text.StartsWith("*", StringComparison.Ordinal))
			text = text[1..];
		if (text.StartsWith(" ", StringComparison.Ordinal))
			text = text[1..];
		return text;
	}

	static bool IsBlockCommentStructuralNewLine(string value)
	{
		return value.AsSpan().Trim().Length == 0 && value.Contains('\n', StringComparison.Ordinal);
	}

	static List<NodeTarget> CollectTargets(Module module)
	{
		List<NodeTarget> targets = [];
		foreach (Definition definition in module.Definitions)
			AddDefinitionTargets(targets, definition);
		return targets;
	}

	static void AddDefinitionTargets(List<NodeTarget> targets, Definition definition)
	{
		AddTarget(targets, definition);
		if (definition is TypeDefinition typeDefinition)
			foreach (GenericParameter parameter in typeDefinition.GenericParameters)
				AddTarget(targets, parameter);

		switch (definition)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
					AddTarget(targets, field);
				foreach (FunctionDefinition function in classDefinition.Functions)
					AddDefinitionTargets(targets, function);
				break;
			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					AddTarget(targets, field);
				foreach (FunctionDefinition function in structDefinition.Functions)
					AddDefinitionTargets(targets, function);
				break;
			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					AddDefinitionTargets(targets, function);
				break;
			case EnumDefinition enumDefinition:
				foreach (VariableDefinition value in enumDefinition.Values)
					AddTarget(targets, value);
				foreach (FunctionDefinition function in enumDefinition.Functions)
					AddDefinitionTargets(targets, function);
				break;
			case NewtypeDefinition newtypeDefinition:
				foreach (ParameterDefinition parameter in newtypeDefinition.Parameters)
					AddTarget(targets, parameter);
				foreach (FieldDefinition field in newtypeDefinition.Fields)
					AddTarget(targets, field);
				foreach (FunctionDefinition function in newtypeDefinition.Functions)
					AddDefinitionTargets(targets, function);
				break;
			case StaticClassDefinition staticClassDefinition:
				foreach (FieldDefinition field in staticClassDefinition.Fields)
					AddTarget(targets, field);
				foreach (FunctionDefinition function in staticClassDefinition.Functions)
					AddDefinitionTargets(targets, function);
				break;
			case FunctionDefinition function:
				foreach (GenericParameter parameter in function.GenericParameters)
					AddTarget(targets, parameter);
				foreach (ParameterDefinition parameter in function.Parameters)
					AddTarget(targets, parameter);
				break;
		}
	}

	static void AddTarget(List<NodeTarget> targets, BindableNode node)
	{
		if (TryGetRange(node.SourceSyntax, out TokenRange range))
			targets.Add(new NodeTarget(node, range.StartLineNumber, range.StartColumn));
	}

	static NodeTarget? FindTarget(List<NodeTarget> targets, int line, int column)
	{
		NodeTarget? sameLine = null;
		foreach (NodeTarget target in targets)
		{
			if (target.Line == line && target.Column == column)
				return target;
			if (target.Line == line && target.Column >= column && (sameLine is null || target.Column < sameLine.Column))
				sameLine = target;
		}
		if (sameLine is not null)
			return sameLine;
		return null;
	}

	static bool TryGetRange(SyntaxNode? syntax, out TokenRange range)
	{
		return SyntaxNodeTraversal.TryGetRange(syntax, out range);
	}

	static void AddAttribute(BindableNode node, AttributeConstructor attribute)
	{
		switch (node)
		{
			case Definition definition:
				definition.Attributes.Add(attribute);
				break;
			case GenericParameter parameter:
				parameter.Attributes.Add(attribute);
				break;
		}
	}

	static BindableNode? ResolveChildTarget(BindableNode owner, string target)
	{
		if (owner is TypeDefinition typeDefinition)
		{
			foreach (GenericParameter parameter in typeDefinition.GenericParameters)
				if (parameter.Name == target)
					return parameter;
		}

		return owner switch
		{
			FunctionDefinition function => ResolveFunctionChild(function, target),
			ClassDefinition classDefinition => ResolveTypeChild(classDefinition.GenericParameters, classDefinition.Fields, classDefinition.Functions, target),
			StructDefinition structDefinition => ResolveTypeChild(structDefinition.GenericParameters, structDefinition.Fields, structDefinition.Functions, target),
			InterfaceDefinition interfaceDefinition => ResolveFunctionChildList(interfaceDefinition.Functions, target),
			EnumDefinition enumDefinition => ResolveEnumChild(enumDefinition, target),
			NewtypeDefinition newtypeDefinition => ResolveNewtypeChild(newtypeDefinition, target),
			StaticClassDefinition staticClassDefinition => ResolveStaticClassChild(staticClassDefinition, target),
			_ => null
		};
	}

	static BindableNode? ResolveFunctionChild(FunctionDefinition function, string target)
	{
		foreach (GenericParameter parameter in function.GenericParameters)
			if (parameter.Name == target)
				return parameter;
		foreach (ParameterDefinition parameter in function.Parameters)
			if (parameter.Name == target || parameter.Symbol == target || parameter is ThisParameterDefinition && target == "this")
				return parameter;
		return null;
	}

	static BindableNode? ResolveTypeChild(List<GenericParameter> genericParameters, List<FieldDefinition> fields, List<FunctionDefinition> functions, string target)
	{
		foreach (GenericParameter parameter in genericParameters)
			if (parameter.Name == target)
				return parameter;
		foreach (FieldDefinition field in fields)
			if (field.Name == target)
				return field;
		return ResolveFunctionChildList(functions, target);
	}

	static BindableNode? ResolveFunctionChildList(List<FunctionDefinition> functions, string target)
	{
		foreach (FunctionDefinition function in functions)
			if (function.Name == target)
				return function;
		return null;
	}

	static BindableNode? ResolveEnumChild(EnumDefinition type, string target)
	{
		foreach (VariableDefinition value in type.Values)
			if (value.Name == target)
				return value;
		return ResolveFunctionChildList(type.Functions, target);
	}

	static BindableNode? ResolveNewtypeChild(NewtypeDefinition type, string target)
	{
		foreach (GenericParameter parameter in type.GenericParameters)
			if (parameter.Name == target)
				return parameter;
		foreach (ParameterDefinition parameter in type.Parameters)
			if (parameter.Name == target)
				return parameter;
		foreach (FieldDefinition field in type.Fields)
			if (field.Name == target)
				return field;
		return ResolveFunctionChildList(type.Functions, target);
	}

	static BindableNode? ResolveStaticClassChild(StaticClassDefinition type, string target)
	{
		foreach (FieldDefinition field in type.Fields)
			if (field.Name == target)
				return field;
		return ResolveFunctionChildList(type.Functions, target);
	}

	static BindableNode? ResolveSymbol(string text, BindableNode owner, Module module)
	{
		string simple = SimplifySymbolText(text);
		if (ResolveChildTarget(owner, simple) is BindableNode child)
			return child;
		foreach (Definition definition in module.Definitions)
		{
			if (definition.Name == simple || definition.Symbol == simple)
				return definition;
			if (definition is TypeDefinition type && ResolveChildTarget(type, simple) is BindableNode nested)
				return nested;
			if (definition is StaticClassDefinition staticClass && ResolveChildTarget(staticClass, simple) is BindableNode staticNested)
				return staticNested;
		}
		return null;
	}

	static string SimplifySymbolText(string text)
	{
		string value = text.Trim();
		int generic = value.IndexOf('<');
		if (generic >= 0)
			value = value[..generic];
		int dot = value.LastIndexOf('.');
		int ns = value.LastIndexOf("::", value.Length - 1, StringComparison.Ordinal);
		int index = Math.Max(dot, ns >= 0 ? ns + 1 : -1);
		return index >= 0 ? value[(index + 1)..] : value;
	}

	static string GetNodeName(BindableNode node)
	{
		return node switch
		{
			GenericParameter parameter => parameter.Name,
			ParameterDefinition parameter => parameter.Name,
			Definition definition => definition.Name,
			_ => node.GetType().Name
		};
	}

	static string Quote(string value)
	{
		StringBuilder builder = new("\"");
		foreach (char c in value)
		{
			builder.Append(c switch
			{
				'\\' => "\\\\",
				'"' => "\\\"",
				'\n' => "\\n",
				'\r' => "\\r",
				'\t' => "\\t",
				_ => c.ToString()
			});
		}
		builder.Append('"');
		return builder.ToString();
	}

	sealed record DocBlock(List<string> Lines, int StartIndex, int EndIndex, TokenRange Range);
	sealed record NodeTarget(BindableNode Node, int Line, int Column);
	sealed class CurrentDocContext(ParsedDocAttribute attribute)
	{
		public ParsedDocAttribute Attribute { get; } = attribute;
		public List<string> Lines { get; } = [];
	}
	sealed class ParsedDocAttribute(string name, string? childTarget)
	{
		public string Name { get; set; } = name;
		public string? ChildTarget { get; } = childTarget;
		public string Content { get; set; } = "";
		public List<string> Symbols { get; } = [];
		public CurrentDocContext? Context { get; set; }
	}
}
