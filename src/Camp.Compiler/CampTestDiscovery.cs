using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Camp.Compiler;

public enum CampTestManifestMode
{
	InModule,
	External
}

public sealed record CampTestManifest(CampTestManifestMode Mode, IReadOnlyList<CampTestManifestEntry> Tests);

public sealed record CampTestManifestEntry(
	string Id,
	string Name,
	string QualifiedName,
	string Sourcefile,
	int Sourceline,
	string Summary,
	bool Skipped,
	string? SkipReason,
	string RunnerSignature)
{
	internal FunctionDefinition? Function { get; init; }
}

public sealed record CampTestDiscoveryResult(CampTestManifest Manifest, IReadOnlyList<AnalysisDiagnostic> Diagnostics);

public static class CampTestDiscovery
{
	public static CampTestDiscoveryResult Discover(Compilation compilation, CampTestManifestMode mode)
	{
		ArgumentNullException.ThrowIfNull(compilation);
		Module module = compilation.SharedModule ?? new Module();
		List<CampTestManifestEntry> tests = [];
		List<AnalysisDiagnostic> diagnostics = [];
		SourcefilePathMapper sourcefilePathMapper = new(module.SourcefilePathMode, module.SourcefileDefaultRoot, module.SourcefileRoots);

		foreach (Definition definition in DeclarationParticipation.ActiveTopLevelDefinitions(module))
		{
			if (definition is not FunctionDefinition function || !DeclarationParticipation.IsTest(function))
				continue;
			string name = GetVisibleFunctionName(function);
			string qualifiedName = GetQualifiedFunctionName(module, function, name);
			(string sourcefile, int sourceline) = GetSourceLocation(module, function, sourcefilePathMapper, diagnostics);
			bool skipped = TryGetAttributeStringContent(function.Attributes, "skip", out string? skipReason);
			tests.Add(new CampTestManifestEntry(
				qualifiedName,
				name,
				qualifiedName,
				sourcefile,
				sourceline,
				GetAttributeStringContent(function.Attributes, "summary") ?? "",
				skipped,
				skipped ? skipReason : null,
				HasBuiltInRunnerSignature(function) ? "valid" : "invalid")
			{
				Function = function
			});
		}

		return new CampTestDiscoveryResult(new CampTestManifest(mode, tests), diagnostics);
	}

	static string GetVisibleFunctionName(FunctionDefinition function)
	{
		if (function.Modifier == FunctionModifier.Constructor)
			return "create";
		if (function.Modifier == FunctionModifier.Destructor || function.Name.StartsWith("~", StringComparison.Ordinal))
			return "destroy";
		return SymbolNameService.CallableName(function).Value.TrimStart('~');
	}

	static string GetQualifiedFunctionName(Module module, FunctionDefinition function, string name)
	{
		string? namespaceName = function.Namespace;
		if (module.DefinitionSources.TryGetValue(function, out TokenSequence? source)
			&& source is not null
			&& module.SourceNamespaces.TryGetValue(source, out string? sourceNamespace))
			namespaceName = sourceNamespace;

		return string.IsNullOrWhiteSpace(namespaceName) ? name : namespaceName + "::" + name;
	}

	static (string Sourcefile, int Sourceline) GetSourceLocation(Module module, FunctionDefinition function, SourcefilePathMapper sourcefilePathMapper, List<AnalysisDiagnostic> diagnostics)
	{
		if (!TryGetDefinitionSourceRange(function, out TokenRange range))
			return ("", 0);
		if (!module.SourceFiles.TryGetValue(range.Sequence, out SourceFile? file))
			return ("", range.StartLineNumber);

		string physicalPath = string.IsNullOrWhiteSpace(file.FullPath) ? file.Path : file.FullPath!;
		SourcefilePathMapResult mapResult = sourcefilePathMapper.Map(physicalPath);
		if (!mapResult.Success)
		{
			diagnostics.Add(new AnalysisDiagnostic(range, mapResult.Diagnostic ?? "Source file path could not be mapped."));
			return (file.Path, range.StartLineNumber);
		}
		return (mapResult.Value ?? file.Path, range.StartLineNumber);
	}

	static bool TryGetDefinitionSourceRange(Definition definition, out TokenRange range)
	{
		range = default;
		switch (definition.SourceSyntax)
		{
			case AliasDeclarationSyntax syntax:
				return Assign(syntax.Identifier?.Range, out range) || TryGetSyntaxRange(syntax, out range);
			case TypeDeclarationSyntax syntax:
				return Assign(syntax.Identifier?.Range, out range) || TryGetSyntaxRange(syntax, out range);
			case MemberDeclarationSyntax syntax:
				return Assign(syntax.Identifier?.Range, out range) || TryGetSyntaxRange(syntax, out range);
			case EnumValueSyntax syntax:
				return Assign(syntax.Identifier?.Range, out range) || TryGetSyntaxRange(syntax, out range);
			default:
				return TryGetSyntaxRange(definition.SourceSyntax, out range);
		}
	}

	static bool TryGetSyntaxRange(SyntaxNode? syntax, out TokenRange range)
	{
		range = default;
		if (syntax is null)
			return false;
		foreach (PropertyInfo property in syntax.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			object? value = property.GetValue(syntax);
			if (value is TokenRange tokenRange)
			{
				range = tokenRange;
				return true;
			}
			if (value is Token token)
			{
				range = token.Range;
				return true;
			}
			if (value is SyntaxNode child && TryGetSyntaxRange(child, out range))
				return true;
		}
		return false;
	}

	static bool Assign(TokenRange? value, out TokenRange range)
	{
		if (value is TokenRange tokenRange)
		{
			range = tokenRange;
			return true;
		}
		range = default;
		return false;
	}

	static bool HasBuiltInRunnerSignature(FunctionDefinition function)
	{
		return function.Body is not null
			&& function.Extern is null
			&& !function.IsAsync
			&& function.IteratorKind == IteratorKind.None
			&& function.GenericParameters.Count == 0
			&& FormatType(function.ReturnType, function.ResolvedType) == "void"
			&& function.Parameters.Count == 1
			&& function.Parameters[0].Modifier == ParameterModifier.Thrown
			&& FormatType(function.Parameters[0].Type, function.Parameters[0].ResolvedType) == "Assertion*";
	}

	static string FormatType(TypeReference? type, string? resolvedType)
	{
		string formatted = type is not null
			? BindableNodeCodeSerializer.SerializeType(type)
			: string.IsNullOrWhiteSpace(resolvedType)
				? BindableNodeAnalyzer.FormatTypeReference(type)
				: resolvedType!;
		return formatted.Replace("#THIS", "escaped this", StringComparison.Ordinal);
	}

	static bool TryGetAttributeStringContent(IReadOnlyList<AttributeConstructor> attributes, string name, out string? content)
	{
		content = GetAttributeStringContent(attributes, name);
		return attributes.Any(attribute => AttributeNameEquals(attribute.Name, name));
	}

	static string? GetAttributeStringContent(IReadOnlyList<AttributeConstructor> attributes, string name)
	{
		foreach (AttributeConstructor attribute in attributes)
		{
			if (!AttributeNameEquals(attribute.Name, name))
				continue;
			ArgumentExpression? argument = attribute.Arguments.FirstOrDefault(static argument => string.IsNullOrWhiteSpace(argument.Name));
			return argument?.Value is LiteralExpression { Kind: LiteralKind.String } literal ? GetLiteralString(literal) : null;
		}
		return null;
	}

	static string GetLiteralString(LiteralExpression literal)
	{
		if (literal.Value is string text)
			return text;
		string source = literal.Text;
		if (source.Length >= 2 && source[0] == '"' && source[^1] == '"')
			return source[1..^1];
		return source;
	}

	static bool AttributeNameEquals(string actual, string expected)
	{
		return string.Equals(actual.TrimStart('@'), expected.TrimStart('@'), StringComparison.Ordinal);
	}
}

public static class CampTestManifestJsonSerializer
{
	public static string Serialize(CampTestManifest manifest)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		using MemoryStream stream = new();
		using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
		{
			json.WriteStartObject();
			json.WriteString("format", "camp.test-manifest");
			json.WriteNumber("version", 1);
			json.WriteString("mode", manifest.Mode == CampTestManifestMode.External ? "external" : "in-module");
			json.WriteStartArray("tests");
			foreach (CampTestManifestEntry test in manifest.Tests)
			{
				json.WriteStartObject();
				json.WriteString("id", test.Id);
				json.WriteString("name", test.Name);
				json.WriteString("qualifiedName", test.QualifiedName);
				json.WriteString("sourcefile", test.Sourcefile);
				json.WriteNumber("sourceline", test.Sourceline);
				json.WriteString("summary", test.Summary);
				json.WriteBoolean("skipped", test.Skipped);
				if (test.SkipReason is null)
					json.WriteNull("skipReason");
				else
					json.WriteString("skipReason", test.SkipReason);
				json.WriteString("runnerSignature", test.RunnerSignature);
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}
		string text = Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
		return text.EndsWith('\n') ? text : text + "\n";
	}
}

public static class CampTestFilter
{
	public static IReadOnlyList<CampTestManifestEntry> Apply(IEnumerable<CampTestManifestEntry> tests, IReadOnlyList<string> patterns)
	{
		if (patterns.Count == 0)
			return tests.ToList();
		return tests.Where(test => patterns.Any(pattern => Matches(test, pattern))).ToList();
	}

	public static bool Matches(CampTestManifestEntry test, string pattern)
	{
		return MatchesText(test.Id, pattern)
			|| MatchesText(test.QualifiedName, pattern)
			|| MatchesText(test.Name, pattern);
	}

	static bool MatchesText(string text, string pattern)
	{
		if (!HasWildcard(pattern))
			return string.Equals(text, pattern, StringComparison.Ordinal);
		return MatchWildcard(text, 0, pattern, 0);
	}

	static bool HasWildcard(string pattern)
	{
		return pattern.Contains('*', StringComparison.Ordinal)
			|| pattern.Contains('?', StringComparison.Ordinal)
			|| pattern.Contains('^', StringComparison.Ordinal);
	}

	static bool MatchWildcard(string text, int textIndex, string pattern, int patternIndex)
	{
		while (patternIndex < pattern.Length)
		{
			char patternChar = pattern[patternIndex];
			if (patternChar == '*')
			{
				while (patternIndex + 1 < pattern.Length && pattern[patternIndex + 1] == '*')
					patternIndex++;
				if (patternIndex + 1 == pattern.Length)
					return true;
				for (int nextText = textIndex; nextText <= text.Length; nextText++)
					if (MatchWildcard(text, nextText, pattern, patternIndex + 1))
						return true;
				return false;
			}
			if (textIndex >= text.Length)
				return false;
			if (patternChar == '?')
			{
				textIndex++;
				patternIndex++;
				continue;
			}
			if (patternChar == '^')
			{
				if (text[textIndex] < 'A' || text[textIndex] > 'Z')
					return false;
				textIndex++;
				patternIndex++;
				continue;
			}
			if (text[textIndex] != patternChar)
				return false;
			textIndex++;
			patternIndex++;
		}
		return textIndex == text.Length;
	}
}
