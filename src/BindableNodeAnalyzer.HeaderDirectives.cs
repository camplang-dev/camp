using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void AnalyzeHeaderDirectives(Module module)
	{
		for (int i = 0; i < module.HeaderDirectives.Count; i++)
		{
			HeaderDirective directive = module.HeaderDirectives[i];
			directive.ResolvedType = "#HEADER";
			for (int j = 0; j < i; j++)
			{
				HeaderDirective previous = module.HeaderDirectives[j];
				if (previous.Header == directive.Header
					&& previous.Kind != directive.Kind
					&& ReferenceEquals(GetSourceSequence(previous), GetSourceSequence(directive)))
					Report(GetRange(directive.SourceSyntax), $"Header '{directive.Header}' may not be named by both #include and #require in the same file.");
			}
		}
	}

	void AssociateForeignHeaderDeclarations(Module module)
	{
		foreach (Definition definition in module.Definitions)
		{
			if (definition.Extern is null)
				continue;

			foreach (HeaderDirective directive in GetHeaderDirectivesInSameFile(definition))
				definition.ForeignHeaders.Add(directive.Header);

			if (definition.Export is not null && !HasOnlyRequiredHeadersInSameFile(definition))
				Report(GetNameRange(definition), $"Exported extern declaration '{definition.Name}' must be associated with a header named by #require.");
		}
	}

	IEnumerable<HeaderDirective> GetHeaderDirectivesInSameFile(BindableNode node)
	{
		TokenSequence? sequence = GetSourceSequence(node);
		foreach (HeaderDirective directive in currentModule?.HeaderDirectives ?? [])
		{
			if (ReferenceEquals(GetSourceSequence(directive), sequence))
				yield return directive;
		}
	}

	bool HasOnlyRequiredHeadersInSameFile(BindableNode node)
	{
		bool hasHeader = false;
		foreach (HeaderDirective directive in GetHeaderDirectivesInSameFile(node))
		{
			hasHeader = true;
			if (directive.Kind != HeaderDirectiveKind.Require)
				return false;
		}
		return hasHeader;
	}

	bool HasRequiredHeaderInSameFile(BindableNode node, string header)
	{
		foreach (HeaderDirective directive in GetHeaderDirectivesInSameFile(node))
		{
			if (directive.Kind == HeaderDirectiveKind.Require && directive.Header == header)
				return true;
		}
		return false;
	}

	bool IsForeignHeaderDependencyVisible(Definition definition, SyntaxNode? referenceSyntax)
	{
		if (definition.ForeignHeaders.Count == 0)
			return true;

		TokenSequence? definitionSequence = GetSourceSequence(definition);
		TokenSequence? referenceSequence = GetSourceSequence(referenceSyntax);
		if (ReferenceEquals(definitionSequence, referenceSequence))
			return true;

		foreach (string header in definition.ForeignHeaders)
		{
			if (ReferenceFileHasHeader(referenceSequence, header))
				return true;
		}

		return false;
	}

	bool ReferenceFileHasHeader(TokenSequence? referenceSequence, string header)
	{
		foreach (HeaderDirective directive in currentModule?.HeaderDirectives ?? [])
		{
			if (directive.Header == header && ReferenceEquals(GetSourceSequence(directive), referenceSequence))
				return true;
		}
		return false;
	}

	void ReportMissingForeignHeader(Definition definition, SyntaxNode? referenceSyntax)
	{
		if (definition.ForeignHeaders.Count == 0)
			return;

		Report(GetRange(referenceSyntax), $"Foreign declaration '{definition.Name}' requires this file to name header '{definition.ForeignHeaders[0]}' with #include or #require.");
	}

	TokenSequence? GetSourceSequence(BindableNode node)
	{
		if (node is Definition definition && currentModule is not null && currentModule.DefinitionSources.TryGetValue(definition, out TokenSequence? source))
			return source;

		return GetSourceSequence(node.SourceSyntax);
	}

	static TokenSequence? GetSourceSequence(SyntaxNode? syntax)
	{
		return GetRange(syntax) is TokenRange range ? range.Sequence : null;
	}
}
