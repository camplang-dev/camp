using System;
using System.Collections.Generic;
using System.Text;

namespace Camp.Compiler;

public sealed record ParseDiagnostic(TokenRange? Range, string Message, string? Code = null, DiagnosticSeverity Severity = DiagnosticSeverity.Error);

public sealed class CampParserOptions
{
	public static CampParserOptions Empty { get; } = new([], []);

	public CampParserOptions(IEnumerable<string> typeSpecs, IEnumerable<string> callSpecs)
	{
		TypeSpecs = new HashSet<string>(typeSpecs, StringComparer.Ordinal);
		CallSpecs = new HashSet<string>(callSpecs, StringComparer.Ordinal);
	}

	public IReadOnlySet<string> TypeSpecs { get; }
	public IReadOnlySet<string> CallSpecs { get; }

	public static CampParserOptions FromTarget(TargetDefinition? target)
	{
		return target is null
			? Empty
			: new CampParserOptions(target.SyntaxTypeSpecs.Keys, target.SyntaxCallSpecs.Keys);
	}
}

public sealed class CampParser
{
	static readonly string[] TypeDeclarationKeywords = ["struct", "class", "interface", "params", "enum", "newtype"];
	static readonly string[] TypeDeclarationDeclarators = ["export", "internal", "public", "extern", "static", "virtual", "sealed", "abstract", "fixed", "escaped", "shadow"];
	static readonly string[] MemberDeclarators = ["export", "internal", "public", "extern", "static", "virtual", "override", "sealed", "abstract", "async", "fixed", "inline"];
	static readonly string[] ParameterDeclaratorKeywords = ["overload", "in", "out", "thrown", "upon", "prep"];
	static readonly string[] TypeDeclaratorKeywords = ["const", "constof", "volatile", "escaped", "scoped", "unscoped"];
	static readonly string[] StatementKeywords = ["if", "do", "while", "for", "else", "yield", "return", "continue", "break", "switch", "within", "try", "catch", "finally", "foreach", "delete", "goto", "throw"];

	readonly TokenSequence tokens;
	readonly CampParserOptions options;
	readonly List<ParseDiagnostic> diagnostics = [];
	int index;
	bool seenNonPreludeCompilationUnitItem;
	int namespaceBlockDepth;

	public CampParser(TokenSequence tokens, CampParserOptions? options = null)
	{
		this.tokens = tokens;
		this.options = options ?? CampParserOptions.Empty;
	}

	public IReadOnlyList<ParseDiagnostic> Diagnostics => diagnostics;

	public static CompilationUnitSyntax Parse(TokenSequence tokens, out IReadOnlyList<ParseDiagnostic> diagnostics, CampParserOptions? options = null)
	{
		CampParser parser = new(tokens, options);
		CompilationUnitSyntax syntax = parser.ParseCompilationUnit();
		diagnostics = parser.Diagnostics;
		return syntax;
	}

	public CompilationUnitSyntax ParseCompilationUnit()
	{
		CompilationUnitSyntax syntax = new() { Items = [] };

		while (!AtEnd)
		{
			int start = index;
			CompilationUnitItemSyntax? item = ParseCompilationUnitItem();

			if (item is not null)
				syntax.Items.Add(item);
			else
				ReportAndAdvance("Expected declaration or import/export declaration.");

			if (index == start)
				ReportAndAdvance("Parser did not consume a token.");
		}

		return syntax;
	}

	CompilationUnitItemSyntax? ParseCompilationUnitItem()
	{
		NamespaceBlockSyntax? namespaceBlock = TryParseNamespaceBlock();
		if (namespaceBlock is not null)
		{
			seenNonPreludeCompilationUnitItem = true;
			return new CompilationUnitItemSyntax { NamespaceBlock = namespaceBlock };
		}

		ImportExportDeclarationSyntax? importExport = ParseImportExportDeclaration();
		if (importExport is not null)
			return new CompilationUnitItemSyntax { ImportExportDeclaration = importExport };

		FileMetadataAttributeSyntax? fileMetadataAttribute = TryParseFileMetadataAttribute();
		if (fileMetadataAttribute is not null)
		{
			if (seenNonPreludeCompilationUnitItem)
				Report(fileMetadataAttribute.Attribute?.AttributeIdentifier, "File metadata attributes must appear before declarations.");
			return new CompilationUnitItemSyntax { FileMetadataAttribute = fileMetadataAttribute };
		}

		if (TryParseRequirementScope(topLevel: true) is RequirementScopeSyntax requirement)
		{
			if (requirement.SemicolonToken is not null)
			{
				if (seenNonPreludeCompilationUnitItem)
					Report(requirement.RequiresKeyword, "File-wide requires declarations must appear before aliases and ordinary declarations.");
			}
			else
				seenNonPreludeCompilationUnitItem = true;
			return new CompilationUnitItemSyntax { RequirementScope = requirement };
		}

		AliasDeclarationSyntax? aliasDeclaration = ParseAliasDeclaration();
		if (aliasDeclaration is not null)
		{
			seenNonPreludeCompilationUnitItem = true;
			return new CompilationUnitItemSyntax { AliasDeclaration = aliasDeclaration };
		}

		DeclarationSyntax? declaration = ParseDeclaration();
		if (declaration is not null)
		{
			seenNonPreludeCompilationUnitItem = true;
			return new CompilationUnitItemSyntax { Declaration = declaration };
		}

		return null;
	}

	FileMetadataAttributeSyntax? TryParseFileMetadataAttribute()
	{
		if (!IsClass(TokenClass.AttributeIdentifier))
			return null;

		int start = index;
		int diagnosticCount = diagnostics.Count;
		AttributeSyntax attribute = ParseAttribute();
		if (TakeIf(";") is Token semicolon)
			return new FileMetadataAttributeSyntax
			{
				Attribute = attribute,
				SemicolonToken = semicolon
			};

		index = start;
		diagnostics.RemoveRange(diagnosticCount, diagnostics.Count - diagnosticCount);
		return null;
	}

	ImportExportDeclarationSyntax? ParseImportExportDeclaration()
	{
		if (Is("using"))
			return ParseUsingImportExportDeclaration();

		if (Is("namespace"))
			return ParseNamespaceDeclaration();

		if (Is("export") && PeekValue(1) == "as")
			return ParseExportImportExportDeclaration();

		if (Is("export") && TryParseExportProjectionDeclaration() is ExportProjectionDeclarationSyntax projection)
			return projection;

		return null;
	}

	UsingImportExportDeclarationSyntax ParseUsingImportExportDeclaration()
	{
		UsingImportExportDeclarationSyntax syntax = new()
		{
			Keyword = Expect("using"),
			QualifiedNamespace = ParseQualifiedNamespace()
		};

		if (Is("as"))
		{
			syntax.AsKeyword = Take();
			syntax.Alias = ExpectIdentifier();
		}
		else if (Is("{"))
		{
			syntax.OpenBraceToken = Take();
			syntax.SelectedIdentifiers = ParseIdentList("}");
			syntax.CloseBraceToken = Expect("}");
		}

		syntax.SemicolonToken = Expect(";");
		return syntax;
	}

	ExportImportExportDeclarationSyntax ParseExportImportExportDeclaration()
	{
		return new ExportImportExportDeclarationSyntax
		{
			Keyword = Expect("export"),
			AsKeyword = Expect("as"),
			QualifiedNamespace = ParseQualifiedNamespace(),
			SemicolonToken = Expect(";")
		};
	}

	ExportImportExportDeclarationSyntax ParseNamespaceDeclaration()
	{
		return new ExportImportExportDeclarationSyntax
		{
			Keyword = Expect("namespace"),
			QualifiedNamespace = ParseQualifiedNamespace(),
			SemicolonToken = Expect(";")
		};
	}

	NamespaceBlockSyntax? TryParseNamespaceBlock()
	{
		int start = index;
		int diagnosticStart = diagnostics.Count;
		if (!Is("namespace"))
			return null;

		Token? keyword = Expect("namespace");
		QualifiedNamespaceSyntax? namespaceName = ParseQualifiedNamespace();
		if (!Is("{"))
		{
			index = start;
			diagnostics.RemoveRange(diagnosticStart, diagnostics.Count - diagnosticStart);
			return null;
		}

		NamespaceBlockSyntax syntax = new()
		{
			Keyword = keyword,
			QualifiedNamespace = namespaceName,
			OpenBraceToken = Expect("{"),
			Items = []
		};

		if (namespaceBlockDepth > 0)
			Report(keyword, "Namespace blocks may not be nested.");

		namespaceBlockDepth++;
		while (!AtEnd && !Is("}"))
		{
			int itemStart = index;
			CompilationUnitItemSyntax? item = ParseNamespaceBlockItem();
			if (item is not null)
				syntax.Items.Add(item);
			else
				ReportAndAdvance("Expected declaration or export projection in namespace block.");

			if (index == itemStart)
				ReportAndAdvance("Parser did not consume a token.");
		}
		namespaceBlockDepth--;

		syntax.CloseBraceToken = Expect("}");
		return syntax;
	}

	CompilationUnitItemSyntax? ParseNamespaceBlockItem()
	{
		if (TryParseNamespaceBlock() is NamespaceBlockSyntax namespaceBlock)
			return new CompilationUnitItemSyntax { NamespaceBlock = namespaceBlock };

		if (Is("using"))
		{
			UsingImportExportDeclarationSyntax usingDeclaration = ParseUsingImportExportDeclaration();
			Report(usingDeclaration.Keyword, "Using declarations are not allowed inside namespace blocks.");
			return new CompilationUnitItemSyntax { ImportExportDeclaration = usingDeclaration };
		}

		if (Is("namespace"))
		{
			ExportImportExportDeclarationSyntax namespaceDeclaration = ParseNamespaceDeclaration();
			Report(namespaceDeclaration.Keyword, "Namespace statements are not allowed inside namespace blocks.");
			return new CompilationUnitItemSyntax { ImportExportDeclaration = namespaceDeclaration };
		}

		FileMetadataAttributeSyntax? fileMetadataAttribute = TryParseFileMetadataAttribute();
		if (fileMetadataAttribute is not null)
		{
			Report(fileMetadataAttribute.Attribute?.AttributeIdentifier, "File metadata attributes are not allowed inside namespace blocks.");
			return new CompilationUnitItemSyntax { FileMetadataAttribute = fileMetadataAttribute };
		}

		if (TryParseRequirementScope(topLevel: true) is RequirementScopeSyntax requirement)
		{
			if (requirement.SemicolonToken is not null)
				Report(requirement.RequiresKeyword, "File-wide requires declarations are not allowed inside namespace blocks.");
			return new CompilationUnitItemSyntax { RequirementScope = requirement };
		}

		if (Is("export") && TryParseExportProjectionDeclaration() is ExportProjectionDeclarationSyntax projection)
			return new CompilationUnitItemSyntax { ImportExportDeclaration = projection };

		AliasDeclarationSyntax? aliasDeclaration = ParseAliasDeclaration();
		if (aliasDeclaration is not null)
			return new CompilationUnitItemSyntax { AliasDeclaration = aliasDeclaration };

		DeclarationSyntax? declaration = ParseDeclaration();
		if (declaration is not null)
			return new CompilationUnitItemSyntax { Declaration = declaration };

		return null;
	}

	ExportProjectionDeclarationSyntax? TryParseExportProjectionDeclaration()
	{
		int start = index;
		int diagnosticStart = diagnostics.Count;
		ExportProjectionDeclarationSyntax syntax = new()
		{
			Keyword = Expect("export"),
			TargetName = ParseQualifiedNamespace()
		};

		if (syntax.TargetName is null)
		{
			index = start;
			diagnostics.RemoveRange(diagnosticStart, diagnostics.Count - diagnosticStart);
			return null;
		}

		if (Is("{"))
			syntax.MemberBlock = ParseExportProjectionMemberBlock();

		if (Is(":"))
		{
			syntax.ColonToken = Take();
			syntax.InterfaceList = ParseTypeList("as", ";");
		}

		if (Is("as"))
		{
			syntax.AsKeyword = Take();
			syntax.Alias = ExpectIdentifier();
		}

		if (!Is(";"))
		{
			index = start;
			diagnostics.RemoveRange(diagnosticStart, diagnostics.Count - diagnosticStart);
			return null;
		}

		syntax.SemicolonToken = Take();
		return syntax;
	}

	ExportProjectionMemberBlockSyntax ParseExportProjectionMemberBlock()
	{
		ExportProjectionMemberBlockSyntax syntax = new()
		{
			OpenBraceToken = Expect("{"),
			Members = [],
			Commas = []
		};

		while (!AtEnd && !Is("}"))
		{
			ExportProjectionMemberSyntax member = new()
			{
				TildeToken = TakeIf("~"),
				Identifier = ExpectIdentifier()
			};
			if (Is("as"))
			{
				member.AsKeyword = Take();
				member.Alias = ExpectIdentifier();
			}
			syntax.Members.Add(member);
			if (TakeIf(",") is Token comma)
			{
				syntax.Commas.Add(comma);
				continue;
			}
			break;
		}

		syntax.CloseBraceToken = Expect("}");
		return syntax;
	}

	RequirementScopeSyntax? TryParseRequirementScope(bool topLevel)
	{
		if (!IsRequirementScopeStart())
			return null;

		RequirementScopeSyntax syntax = ParseRequirementPrefix();
		if (TakeIf(";") is Token semicolon)
		{
			syntax.SemicolonToken = semicolon;
			return syntax;
		}

		if (TakeIf("{") is Token openBrace)
		{
			syntax.OpenBraceToken = openBrace;
			if (topLevel)
			{
				syntax.Items = [];
				while (!AtEnd && !Is("}"))
				{
					int start = index;
					CompilationUnitItemSyntax? item = ParseRequirementCompilationUnitItem();
					if (item is not null)
						syntax.Items.Add(item);
					else
						ReportAndAdvance("Expected declaration inside requires block.");
					if (index == start)
						ReportAndAdvance("Parser did not consume a token.");
				}
				if (syntax.Items.Count == 0)
					syntax.Items = null;
			}
			else
			{
				syntax.Declarations = [];
				while (!AtEnd && !Is("}"))
				{
					int start = index;
					DeclarationSyntax? declaration = ParseDeclaration();
					if (declaration is not null)
						syntax.Declarations.Add(declaration);
					else
						ReportAndAdvance("Expected declaration inside requires block.");
					if (index == start)
						ReportAndAdvance("Parser did not consume a token.");
				}
				if (syntax.Declarations.Count == 0)
					syntax.Declarations = null;
			}
			syntax.CloseBraceToken = Expect("}");
			return syntax;
		}

		if (topLevel)
		{
			syntax.Item = ParseRequirementCompilationUnitItem();
			if (syntax.Item is null)
				Report(syntax.RequiresKeyword, "requires must be followed by a declaration, declaration block, or semicolon.");
		}
		else
		{
			syntax.Declaration = ParseDeclaration();
			if (syntax.Declaration is null)
				Report(syntax.RequiresKeyword, "requires must be followed by a declaration, declaration block, or semicolon.");
		}
		return syntax;
	}

	CompilationUnitItemSyntax? ParseRequirementCompilationUnitItem()
	{
		if (TryParseRequirementScope(topLevel: true) is RequirementScopeSyntax requirement)
		{
			if (requirement.SemicolonToken is not null)
				Report(requirement.RequiresKeyword, "File-wide requires declarations are not allowed inside requires blocks.");
			return new CompilationUnitItemSyntax { RequirementScope = requirement };
		}

		if (Is("using"))
		{
			UsingImportExportDeclarationSyntax usingDeclaration = ParseUsingImportExportDeclaration();
			Report(usingDeclaration.Keyword, "Using declarations are not allowed inside requires blocks.");
			return new CompilationUnitItemSyntax { ImportExportDeclaration = usingDeclaration };
		}

		if (Is("namespace"))
		{
			if (TryParseNamespaceBlock() is NamespaceBlockSyntax namespaceBlock)
				return new CompilationUnitItemSyntax { NamespaceBlock = namespaceBlock };
			ExportImportExportDeclarationSyntax namespaceDeclaration = ParseNamespaceDeclaration();
			Report(namespaceDeclaration.Keyword, "Namespace statements are not allowed inside requires blocks.");
			return new CompilationUnitItemSyntax { ImportExportDeclaration = namespaceDeclaration };
		}

		FileMetadataAttributeSyntax? fileMetadataAttribute = TryParseFileMetadataAttribute();
		if (fileMetadataAttribute is not null)
		{
			Report(fileMetadataAttribute.Attribute?.AttributeIdentifier, "File metadata attributes are not allowed inside requires blocks.");
			return new CompilationUnitItemSyntax { FileMetadataAttribute = fileMetadataAttribute };
		}

		if (Is("export") && TryParseExportProjectionDeclaration() is ExportProjectionDeclarationSyntax projection)
			return new CompilationUnitItemSyntax { ImportExportDeclaration = projection };

		AliasDeclarationSyntax? aliasDeclaration = ParseAliasDeclaration();
		if (aliasDeclaration is not null)
			return new CompilationUnitItemSyntax { AliasDeclaration = aliasDeclaration };

		DeclarationSyntax? declaration = ParseDeclaration();
		if (declaration is not null)
			return new CompilationUnitItemSyntax { Declaration = declaration };

		return null;
	}

	RequirementScopeSyntax ParseRequirementPrefix()
	{
		return new RequirementScopeSyntax
		{
			RequiresKeyword = Expect("requires"),
			OpenParenToken = Expect("("),
			Condition = ParseExpressionItem(),
			CloseParenToken = Expect(")")
		};
	}

	bool IsRequirementScopeStart()
	{
		return Is("requires") && PeekValue(1) == "(";
	}

	AliasDeclarationSyntax? ParseAliasDeclaration()
	{
		int start = index;
		List<AttributeSyntax>? attributes = ParseAttributes();
		List<MemberDeclaratorSyntax> declarators = [];
		while (Is("export") || Is("internal") || Is("public"))
			declarators.Add(new MemberDeclaratorSyntax { Keyword = Take() });

		if (!Is("alias"))
		{
			index = start;
			return null;
		}

		AliasDeclarationSyntax syntax = new()
		{
			Attributes = attributes,
			Declarators = declarators.Count == 0 ? null : declarators,
			AliasKeyword = Expect("alias"),
			Identifier = ExpectIdentifier(),
			EqualsToken = Expect("="),
			TargetCandidates = ParseAliasTargetCandidates(),
			SemicolonToken = Expect(";")
		};
		syntax.TargetName = syntax.TargetCandidates is [AliasTargetCandidateSyntax candidate] ? candidate.TargetName : null;
		return syntax;
	}

	List<AliasTargetCandidateSyntax> ParseAliasTargetCandidates()
	{
		List<AliasTargetCandidateSyntax> candidates = [];
		do
		{
			AliasTargetCandidateSyntax candidate = new();
			if (Is("configured") && PeekValue(1) == "(")
			{
				candidate.Condition = ParseExpressionItem();
				candidate.ColonToken = Expect(":");
			}
			candidate.TargetName = ParseQualifiedNamespace();
			if (Is(","))
				candidate.CommaToken = Take();
			candidates.Add(candidate);
		}
		while (candidates[^1].CommaToken is not null && !AtEnd && !Is(";"));
		return candidates;
	}

	QualifiedNamespaceSyntax? ParseQualifiedNamespace()
	{
		QualifiedNamespaceSyntax syntax = new() { Qualifiers = [] };

		while (TryParseQualifier() is QualifierSyntax qualifier)
			syntax.Qualifiers.Add(qualifier);

		syntax.Identifier = ExpectIdentifier();
		return syntax.Identifier is null && syntax.Qualifiers.Count == 0 ? null : syntax;
	}

	QualifierSyntax? TryParseQualifier()
	{
		int start = index;
		Token? identifier = TakeIdentifier();
		TokenRange? colonColon = TakeOperator("::");

		if (identifier is not null && colonColon is not null)
			return new QualifierSyntax { Identifier = identifier, ColonColonToken = colonColon };

		index = start;
		return null;
	}

	DeclarationSyntax? ParseDeclaration()
	{
		int start = index;

		if (TryParseRequirementScope(topLevel: false) is RequirementScopeSyntax requirement)
			return new DeclarationSyntax { RequirementScope = requirement };

		TypeDeclarationSyntax? typeDeclaration = ParseTypeDeclaration();
		if (typeDeclaration is not null)
			return new DeclarationSyntax { TypeDeclaration = typeDeclaration };

		index = start;
		MemberDeclarationSyntax? memberDeclaration = ParseMemberDeclaration();
		if (memberDeclaration is not null)
			return new DeclarationSyntax { MemberDeclaration = memberDeclaration };

		index = start;
		return null;
	}

	TypeDeclarationSyntax? ParseTypeDeclaration()
	{
		int start = index;
		List<AttributeSyntax>? attributes = ParseAttributes();
		List<TypeDeclarationDeclaratorSyntax>? declarators = [];

		while (IsAny(TypeDeclarationDeclarators))
			declarators.Add(new TypeDeclarationDeclaratorSyntax { Keyword = Take() });

		if ((Is("struct") || Is("class")) && PeekValue(1) == "iter")
		{
			index = start;
			return null;
		}

		if (!IsAny(TypeDeclarationKeywords))
		{
			index = start;
			return null;
		}

		TypeDeclarationSyntax syntax = new()
		{
			Attributes = attributes,
			Declarators = declarators.Count == 0 ? null : declarators,
			Keyword = Take()
		};

		if (syntax.Keyword?.Value == "newtype" && IsAny("fn", "delegate"))
		{
			TypeSyntax? callable = ParseType();
			if (callable is CallableTypeSyntax callableType)
			{
				while (IsPossibleTrailingCallableSpecIdentifier(requireFollowingIdentifier: true))
				{
					if (callableType.CallSpec is null)
						callableType.CallSpec = Take();
					else if (syntax.CallSpec is null)
						syntax.CallSpec = Take();
					else
						break;
				}
				if (IsIdentifier())
					syntax.Type = callable;
				else
					syntax.Type = null;
			}
		}

		int typeStart = index;
		if (syntax.Type is null)
		{
			TypeSyntax? type = ParseType();
			if (type is not null && IsIdentifier())
				syntax.Type = type;
			else
				index = typeStart;
		}

		syntax.Identifier = ExpectIdentifier();

		if (Is("<"))
			syntax.GenericParameterList = ParseGenericParameterList();

		if (Is("("))
			syntax.ParameterList = ParseParameterList();

		if (TakeIf(":") is Token colon)
		{
			syntax.ColonToken = colon;
			syntax.UnderlyingTypeList = ParseUnderlyingTypeList();
		}

		if (Is("{"))
			syntax.Scope = ParseTypeDeclarationScope();
		else
			syntax.SemicolonToken = Expect(";");

		return syntax;
	}

	TypeDeclarationScopeSyntax ParseTypeDeclarationScope()
	{
		TypeDeclarationScopeSyntax syntax = new()
		{
			OpenBraceToken = Expect("{"),
			Declarations = []
		};

		if (!Is("}") && LooksLikeEnumValueList())
		{
			syntax.EnumValueList = ParseEnumValueList();
			syntax.SemicolonToken = TakeIf(";");
		}

			while (!AtEnd && !Is("}"))
			{
				int start = index;
				DeclarationSyntax? declaration = ParseDeclaration();

			if (declaration is not null)
				syntax.Declarations.Add(declaration);
			else
				ReportAndAdvance("Expected declaration.");

			if (index == start)
				ReportAndAdvance("Parser did not consume a token.");
		}

		if (syntax.Declarations.Count == 0)
			syntax.Declarations = null;

		syntax.CloseBraceToken = Expect("}");
		return syntax;
	}

	bool LooksLikeEnumValueList()
	{
		int start = index;
		EnumValueSyntax? value = ParseEnumValue();
		bool result = value is not null && (Is(",") || Is(";") || Is("}"));
		index = start;
		return result;
	}

	EnumValueListSyntax ParseEnumValueList()
	{
		EnumValueListSyntax list = new() { Values = [], Commas = [] };
		ParseCommaList(list.Values, list.Commas, ParseEnumValue, ";", "}");
		return list;
	}

	EnumValueSyntax? ParseEnumValue()
	{
		List<AttributeSyntax>? attributes = ParseAttributes();
		Token? identifier = TakeIdentifier();
		if (identifier is null)
		{
			if (attributes is not null)
				Report(Current, "Enum value attribute is missing a value name.");
			return null;
		}

		EnumValueSyntax syntax = new() { Attributes = attributes, Identifier = identifier };

		if (TakeIf("=") is Token equals)
		{
			syntax.EqualsToken = equals;
			syntax.Expression = ParseAssignmentExpression();
		}

		return syntax;
	}

	MemberDeclarationSyntax? ParseMemberDeclaration()
	{
		int start = index;
		MemberDeclarationSyntax syntax = new()
		{
			Attributes = ParseAttributes(),
			Declarators = []
		};

		while (IsAny(MemberDeclarators))
			syntax.Declarators.Add(new MemberDeclaratorSyntax { Keyword = Take() });

		if (syntax.Declarators.Count == 0)
			syntax.Declarators = null;

		if (IsPossibleLeadingCallSpecIdentifier())
			syntax.CallSpec = Take();

		if (Is("const") && PeekValue(1) == "inline")
		{
			Token constKeyword = Take()!.Value;
			Report(Current, "'const inline' is invalid; write 'inline const ...' instead.");
			Token? inlineKeyword = Take();
			syntax.Declarators ??= [];
			syntax.Declarators.Add(new MemberDeclaratorSyntax { Keyword = inlineKeyword });
			TypeSyntax? innerType = ParseType(requireIdentifierAfterTerminalTargetSpec: true);
			if (innerType is not null && LooksLikeMemberName())
				syntax.Type = new DeclaratorTypeSyntax { Declarator = new TypeDeclaratorSyntax { Keyword = constKeyword }, Type = innerType };
		}
		else
		{
			int typeStart = index;
			TypeSyntax? type = ParseDeclarationReturnType();
			if (type is not null && LooksLikeMemberName())
				syntax.Type = type;
			else
				index = typeStart;
		}

		syntax.TildeToken = TakeIf("~");
		syntax.Identifier = ExpectIdentifier();
		TryParseOutOfScopeMemberOwner(syntax);

		if (syntax.Identifier is null && syntax.TildeToken is null)
		{
			index = start;
			return null;
		}

		if (Is("<"))
			syntax.GenericParameterList = ParseGenericParameterList();

		if (Is("("))
			syntax.ParameterList = ParseParameterList();

		if (Is(":"))
		{
			syntax.CallableAscriptionColonToken = Take();
			syntax.CallableAscriptionType = ParseType();
			if (syntax.CallableAscriptionType is null)
				Report(Current, "Callable ascription is missing a type.");
			if (Is("."))
			{
				syntax.CallableAscriptionDotToken = Take();
				syntax.CallableAscriptionMemberName = ExpectIdentifier();
				if (syntax.CallableAscriptionMemberName is null)
					Report(Current, "Interface implementation marker is missing a method name after '.'.");
			}
		}

		if (Is(";"))
			syntax.SemicolonToken = Take();
		else if (Is("{") || IsOperator("=>"))
			syntax.MethodBody = ParseMethodBody();
		else if (Is("="))
			syntax.Assignment = ParseAssignment(consumeSemicolon: true);
		else
			Report(Current, "Expected ';', method body, or assignment.");

		return syntax;
	}

	bool LooksLikeMemberName()
	{
		bool result = Is("~") || IsIdentifier();
		return result;
	}

	List<QualifierSyntax>? ParseQualifiers()
	{
		List<QualifierSyntax> qualifiers = [];

		while (TryParseQualifier() is QualifierSyntax qualifier)
			qualifiers.Add(qualifier);

		return qualifiers.Count == 0 ? null : qualifiers;
	}

	List<AttributeSyntax>? ParseAttributes()
	{
		List<AttributeSyntax> attributes = [];

		while (IsClass(TokenClass.AttributeIdentifier))
			attributes.Add(ParseAttribute());

		return attributes.Count == 0 ? null : attributes;
	}

	AttributeSyntax ParseAttribute()
	{
		AttributeSyntax syntax = new()
		{
			AttributeIdentifier = TakeIfClass(TokenClass.AttributeIdentifier)
		};

		if (Is("("))
		{
			syntax.OpenParenToken = Take();
			if (!Is(")"))
				syntax.ArgumentList = ParseArgumentList(")");
			syntax.CloseParenToken = Expect(")");
		}

		return syntax;
	}

	GenericParameterListSyntax ParseGenericParameterList()
	{
		GenericParameterListSyntax syntax = new()
		{
			LessThanToken = Expect("<"),
			Parameters = [],
			Commas = []
		};

		ParseCommaList(syntax.Parameters, syntax.Commas, ParseGenericParameter, ">");
		syntax.GreaterThanToken = Expect(">");
		return syntax;
	}

	GenericParameterSyntax? ParseGenericParameter()
	{
		Token? identifier = TakeIdentifier();
		if (identifier is null)
			return null;

		GenericParameterSyntax syntax = new() { Identifier = identifier };

		if (TakeIf(":") is Token colon)
		{
			syntax.ColonToken = colon;
			syntax.ImplementsKeyword = TakeIf("implements");
			syntax.Type = ParseType();
		}

		return syntax;
	}

	UnderlyingTypeListSyntax ParseUnderlyingTypeList()
	{
		UnderlyingTypeListSyntax syntax = new() { Types = [], Commas = [] };
		ParseCommaList(syntax.Types, syntax.Commas, () => ParseType(), ";", "{", "}");
		return syntax;
	}

	TypeListSyntax ParseTypeList(string close)
	{
		TypeListSyntax syntax = new() { Types = [], Commas = [] };
		ParseCommaList(syntax.Types, syntax.Commas, () => ParseType(), close);
		return syntax;
	}

	TypeListSyntax ParseTypeList(params string[] close)
	{
		TypeListSyntax syntax = new() { Types = [], Commas = [] };
		ParseCommaList(syntax.Types, syntax.Commas, () => ParseType(), close);
		return syntax;
	}

	IdentListSyntax ParseIdentList(string close)
	{
		IdentListSyntax syntax = new() { Identifiers = [], Commas = [] };

		while (!AtEnd && !Is(close))
		{
			Token? identifier = TakeIdentifier();

			if (identifier is not null)
				syntax.Identifiers.Add(identifier.Value);
			else
				ReportAndAdvance("Expected identifier.");

			if (TakeIf(",") is Token comma)
				syntax.Commas.Add(comma);
			else
				break;
		}

		return syntax;
	}

	TypeSyntax? ParseType(bool requireIdentifierAfterTerminalTargetSpec = false, bool allowFixedArrayLength = true)
	{
		TypeSyntax? type = ParseTypePrefix();
		if (type is null)
			return null;

		while (true)
		{
			if (allowFixedArrayLength && Is("["))
			{
				Token? open = Expect("[");
				ExpressionSyntax? length = Is("]") ? null : ParseExpression();
				type = new ArrayTypeSyntax
				{
					ElementType = type,
					OpenBracketToken = open,
					Length = length,
					CloseBracketToken = Expect("]")
				};
			}
			else if (TakeIf("?") is Token question)
			{
				type = new OptionalTypeSyntax { ElementType = type, QuestionToken = question };
			}
			else if (TakeIf("*") is Token star)
			{
				type = new PointerTypeSyntax { ElementType = type, StarToken = star };
			}
			else if (Is("<") && TryParseGenericType(type) is GenericTypeSyntax generic)
			{
				type = generic;
			}
			else if (IsAny(TypeDeclaratorKeywords))
			{
				type = new DeclaratorTypeSyntax { Declarator = ParseTypeDeclarator(), Type = type };
			}
			else if (IsPossiblePostfixTargetSpecIdentifier())
			{
				if (requireIdentifierAfterTerminalTargetSpec && !CanTargetSpecBePartOfDeclarationType())
					return type;
				type = new TargetTypeSpecTypeSyntax { Specifier = Take(), Type = type };
			}
			else
			{
				return type;
			}
		}
	}

	TypeSyntax? ParseDeclarationReturnType()
	{
		if (Is("prep"))
			return ParsePrepReturnType();

		return ParseType(requireIdentifierAfterTerminalTargetSpec: true);
	}

	PrepReturnTypeSyntax ParsePrepReturnType()
	{
		return new PrepReturnTypeSyntax
		{
			PrepKeyword = Take(),
			Type = ParseType(requireIdentifierAfterTerminalTargetSpec: true)
		};
	}

	TypeSyntax? ParseTypePrefix()
	{
		if (IsClass(TokenClass.AttributeIdentifier))
			return new AttributedTypeSyntax { Attribute = ParseAttribute(), Type = ParseType() };

		if (Is("async") && PeekValue(1) == "iter")
			return ParseIterType(asyncKeyword: Take(), storageKeyword: null);

		if (Is("fn") && PeekValue(1) == "*")
			return new RawFunctionPointerTypeSyntax { FnKeyword = Take(), StarToken = Expect("*") };

		if (IsAny("fn", "delegate", "async", "once"))
		{
			CallableTypeSyntax callable = new()
			{
				CallableKeyword = Take(),
			};
			if (IsPossibleCallableSpecIdentifier())
				callable.CallSpec = Take();
			if (IsPossibleCallableSpecIdentifier())
				callable.TargetSpec = Take();
			callable.ReturnType = Is("prep") ? ParsePrepReturnType() : ParseType();
			callable.ParameterList = Is("(") ? ParseParameterList() : null;
			return callable;
		}

		if ((Is("struct") || Is("class")) && PeekValue(1) == "iter")
			return ParseIterType(asyncKeyword: null, storageKeyword: Take());

		if (Is("iter"))
			return ParseIterType(asyncKeyword: null, storageKeyword: null);

		if (Is("params"))
			return ParseWrappedType<ParamsTypeSyntax>("params", (syntax, keyword, open, type, close) =>
			{
				syntax.ParamsKeyword = keyword;
				syntax.OpenParenToken = open;
				syntax.Type = type;
				syntax.CloseParenToken = close;
			});

		if (Is("struct") && PeekValue(1) == "(")
			return ParseWrappedType<StructTypeSyntax>("struct", (syntax, keyword, open, type, close) =>
			{
				syntax.StructKeyword = keyword;
				syntax.OpenParenToken = open;
				syntax.Type = type;
				syntax.CloseParenToken = close;
			});

		if (Is("thrown"))
			return ParseWrappedType<ThrownTypeSyntax>("thrown", (syntax, keyword, open, type, close) =>
			{
				syntax.ThrownKeyword = keyword;
				syntax.OpenParenToken = open;
				syntax.Type = type;
				syntax.CloseParenToken = close;
			});

		if (IsAny(TypeDeclaratorKeywords))
			return new DeclaratorTypeSyntax { Declarator = ParseTypeDeclarator(), Type = ParseTypePrefix() };

		if (Is("this"))
			return new ThisTypeSyntax { ThisKeyword = Take() };

		if (IsKnownTargetTypeSpecIdentifier())
			return new TargetTypeSpecTypeSyntax { Specifier = Take(), Type = ParseTypePrefix(), IsPrefix = true };

		return ParseQualifiedNameType();
	}

	IterTypeSyntax ParseIterType(Token? asyncKeyword, Token? storageKeyword)
	{
		IterTypeSyntax syntax = new()
		{
			AsyncKeyword = asyncKeyword,
			StorageKeyword = storageKeyword,
			IterKeyword = Expect("iter")
		};

		if (Is("("))
			syntax.ParameterList = ParseParameterList();
		else
			syntax.ElementType = ParseType();

		return syntax;
	}

	void TryParseOutOfScopeMemberOwner(MemberDeclarationSyntax syntax)
	{
		if (syntax.Identifier is not Token ownerIdentifier)
			return;

		QualifiedNameTypeSyntax ownerName = new()
		{
			Identifier = ownerIdentifier
		};

		if (Is("<"))
		{
			int start = index;
			GenericTypeSyntax? genericOwner = TryParseGenericType(ownerName);
			if (genericOwner is not null && Is("."))
			{
				syntax.OutOfScopeOwnerType = genericOwner;
				syntax.OutOfScopeDotToken = Take();
				syntax.Identifier = ExpectIdentifier();
				return;
			}
			index = start;
		}

		if (!Is("."))
			return;

		syntax.OutOfScopeOwnerType = ownerName;
		syntax.OutOfScopeDotToken = Take();
		syntax.Identifier = ExpectIdentifier();
	}

	T ParseWrappedType<T>(string keywordValue, Action<T, Token?, Token?, TypeSyntax?, Token?> assign)
		where T : TypeSyntax, new()
	{
		T syntax = new();
		Token? keyword = Expect(keywordValue);
		Token? open = Expect("(");
		TypeSyntax? type = ParseType();
		Token? close = Expect(")");
		assign(syntax, keyword, open, type, close);
		return syntax;
	}

	GenericTypeSyntax? TryParseGenericType(TypeSyntax type)
	{
		int start = index;
		int diagnosticStart = diagnostics.Count;
		GenericTypeSyntax syntax = new()
		{
			Type = type,
			LessThanToken = Expect("<"),
			TypeArgumentList = ParseTypeList(">")
		};

		if (!Is(">"))
		{
			index = start;
			diagnostics.RemoveRange(diagnosticStart, diagnostics.Count - diagnosticStart);
			return null;
		}

		syntax.GreaterThanToken = Take();
		return syntax;
	}

	QualifiedNameTypeSyntax? ParseQualifiedNameType()
	{
		QualifiedNameTypeSyntax syntax = new()
		{
			Qualifiers = ParseQualifiers(),
			Identifier = TakeIdentifier()
		};

		if (syntax.Identifier is null && syntax.Qualifiers is null)
			return null;

		return syntax;
	}

	TypeDeclaratorSyntax? ParseTypeDeclarator()
	{
		if (!IsAny(TypeDeclaratorKeywords))
			return null;

		TypeDeclaratorSyntax syntax = new() { Keyword = Take() };

		if ((syntax.Keyword?.Value is "constof" or "scoped" or "unscoped") && Is("("))
		{
			syntax.OpenParenToken = Take();
			syntax.AnchorList = ParseIdentList(")");
			syntax.CloseParenToken = Expect(")");
		}

		return syntax;
	}

	bool IsKnownTargetTypeSpecIdentifier()
	{
		return IsIdentifier() && Current?.Value is string value && options.TypeSpecs.Contains(value);
	}

	bool IsKnownTargetCallSpecIdentifier()
	{
		return IsIdentifier() && Current?.Value is string value && options.CallSpecs.Contains(value);
	}

	bool IsUnknownUnderscoreIdentifier()
	{
		return IsIdentifier()
			&& Current?.Value is string value
			&& value.StartsWith("_", StringComparison.Ordinal)
			&& !options.TypeSpecs.Contains(value)
			&& !options.CallSpecs.Contains(value);
	}

	bool IsPossiblePostfixTargetSpecIdentifier()
	{
		return IsKnownTargetTypeSpecIdentifier() || IsKnownTargetCallSpecIdentifier() || IsUnknownUnderscoreIdentifier();
	}

	bool IsPossibleCallableSpecIdentifier()
	{
		if (IsKnownTargetTypeSpecIdentifier() || IsKnownTargetCallSpecIdentifier())
			return true;
		if (IsUnknownUnderscoreIdentifier())
			return IsObviousTypeStart(PeekValue(1));
		if (IsReservedLeadingCallSpecToken(Current?.Value))
			return false;
		return IsIdentifier() && IsObviousTypeStart(PeekValue(1));
	}

	bool IsPossibleTrailingCallableSpecIdentifier(bool requireFollowingIdentifier)
	{
		if (!IsPossibleCallableSpecIdentifier())
			return false;
		return !requireFollowingIdentifier || Peek(1)?.Class == TokenClass.Identifier;
	}

	bool IsPossibleLeadingCallSpecIdentifier()
	{
		if (!IsIdentifier())
			return false;
		if (IsKnownTargetCallSpecIdentifier())
			return true;
		if (IsUnknownUnderscoreIdentifier())
			return IsObviousTypeStart(PeekValue(1));
		if (IsReservedLeadingCallSpecToken(Current?.Value))
			return false;

		return ValueIsAny(PeekValue(1),
			"void", "bool", "string", "wstring", "astring", "byte", "sbyte", "ushort", "short",
			"uint", "int", "ulong", "long", "nuint", "nint", "float", "double", "char",
			"wchar", "achar", "uchar", "untyped", "fn", "delegate", "once", "async", "iter");
	}

	static bool IsObviousTypeStart(string? value)
	{
		return ValueIsAny(value,
			"void", "bool", "string", "wstring", "astring", "byte", "sbyte", "ushort", "short",
			"uint", "int", "ulong", "long", "nuint", "nint", "float", "double", "char",
			"wchar", "achar", "uchar", "untyped", "fn", "delegate", "once", "async", "iter",
			"prep", "const", "constof", "volatile", "escaped", "scoped", "unscoped", "this",
			"struct", "class", "params", "thrown");
	}

	static bool IsReservedLeadingCallSpecToken(string? value)
	{
		return ValueIsAny(value,
			"abstract", "alias", "any", "as", "astring", "async", "auto", "bool", "break", "byte",
			"achar", "case", "catch", "char", "class", "classtype", "const", "continue", "copyable", "default", "delegate", "delete",
			"do", "double", "else", "enum", "escaped", "export", "extern", "false", "finally",
			"fixed", "float", "fn", "for", "foreach", "if", "implements", "in", "init", "int",
			"interface", "internal", "iter", "long", "namespace", "new", "newtype", "nint", "null", "nuint", "once", "out",
			"override", "params", "prep", "public", "return", "sbyte", "scoped", "sealed", "short", "sizeof",
			"stackalloc", "static", "string", "struct", "switch", "this", "thrown", "true", "try", "uchar", "uint",
			"ulong", "unscoped", "unsafe", "upon", "ushort", "untyped", "using", "virtual", "void", "volatile",
			"vtableof", "wchar", "while", "within", "wstring", "yield", "typenameof");
	}

	bool CanTargetSpecBePartOfDeclarationType()
	{
		string? next = PeekValue(1);
		if (next is null)
			return false;

		if (ValueIsAny(next, "[", "?", "*", "<"))
			return true;

		if (Array.IndexOf(TypeDeclaratorKeywords, next) >= 0)
			return true;

		return Peek(1)?.Class == TokenClass.Identifier;
	}

	ParameterListSyntax ParseParameterList()
	{
		ParameterListSyntax syntax = new()
		{
			OpenParenToken = Expect("("),
			Parameters = [],
			Commas = []
		};

		ParseCommaList(syntax.Parameters, syntax.Commas, ParseParameter, ")");
		syntax.CloseParenToken = Expect(")");
		return syntax;
	}

	ParameterSyntax? ParseParameter()
	{
		if (Is("sizeof"))
			return new SizeOfParameterSyntax
			{
				SizeOfKeyword = Take(),
				OpenParenToken = Expect("("),
				Type = ParseType(),
				CloseParenToken = Expect(")")
			};

		if (Is("typenameof"))
			return new NameOfParameterSyntax
			{
				NameOfKeyword = Take(),
				OpenParenToken = Expect("("),
				Type = ParseType(),
				CloseParenToken = Expect(")")
			};

		if (Is("vtableof"))
			return new VTableOfParameterSyntax
			{
				VTableOfKeyword = Take(),
				OpenParenToken = Expect("("),
				Type = ParseType(),
				ColonToken = Expect(":"),
				InterfaceType = ParseType(),
				CloseParenToken = Expect(")")
			};

		int start = index;
		Token? within = TakeIf("within");
		if (within is not null)
		{
			Token? lifetime = ValueIsAny(PeekValue(0), "scoped", "unscoped", "escaped") ? Take() : null;
			if (Is("this") && PeekValue(1) == "." && Peek(2)?.Class == TokenClass.Identifier)
				return new WithinParameterSyntax { WithinKeyword = within, LifetimeKeyword = lifetime, ThisKeyword = Take(), DotToken = Expect("."), Identifier = TakeIdentifier() };
			if (IsIdentifier() && ValueIsAny(PeekValue(1), ",", ")"))
				return new WithinParameterSyntax { WithinKeyword = within, LifetimeKeyword = lifetime, Identifier = TakeIdentifier() };
		}
		index = start;

		List<TypeDeclaratorSyntax> thisDeclarators = [];
		while (ParseTypeDeclarator() is TypeDeclaratorSyntax declarator)
			thisDeclarators.Add(declarator);

		if (Is("this"))
			return new ThisParameterSyntax
			{
				Declarators = thisDeclarators.Count == 0 ? null : thisDeclarators,
				ThisKeyword = Take()
			};

		index = start;
		return ParseValueParameter();
	}

	ValueParameterSyntax? ParseValueParameter()
	{
		ValueParameterSyntax syntax = new()
		{
			WithinKeyword = TakeIf("within"),
			Declarators = []
		};

		while (ParseParameterDeclarator() is ParameterDeclaratorSyntax declarator)
			syntax.Declarators.Add(declarator);

		if (syntax.Declarators.Count == 0)
			syntax.Declarators = null;

		syntax.Type = ParseType(requireIdentifierAfterTerminalTargetSpec: true);
		if (syntax.Type is null)
			return null;

		if (IsIdentifier() && !ValueIsAny(PeekValue(1), ",", ")", "="))
			syntax.Identifier = TakeIdentifier();
		else if (IsIdentifier())
			syntax.Identifier = TakeIdentifier();

		if (TakeIf("=") is Token equals)
		{
			syntax.EqualsToken = equals;
			syntax.DefaultValue = ParseExpressionItem();
		}

		return syntax;
	}

	ParameterDeclaratorSyntax? ParseParameterDeclarator()
	{
		List<AttributeSyntax>? attributes = ParseAttributes();
		Token? keyword = IsAny(ParameterDeclaratorKeywords) && (!Is("prep") || CanParsePrepParameterModifier()) ? Take() : null;

		if (attributes is null && keyword is null)
			return null;

		return new ParameterDeclaratorSyntax { Attributes = attributes, Keyword = keyword };
	}

	bool CanParsePrepParameterModifier()
	{
		int start = index;
		int diagnosticStart = diagnostics.Count;
		TakeIf("prep");
		TypeSyntax? type = ParseType(requireIdentifierAfterTerminalTargetSpec: true);
		bool result = type is not null && IsIdentifier();
		index = start;
		if (diagnostics.Count > diagnosticStart)
			diagnostics.RemoveRange(diagnosticStart, diagnostics.Count - diagnosticStart);
		return result;
	}

	AssignmentSyntax ParseAssignment(bool consumeSemicolon)
	{
		AssignmentSyntax syntax = new()
		{
			EqualsToken = Expect("="),
			Expression = ParseExpression()
		};

		if (consumeSemicolon)
			syntax.SemicolonToken = Expect(";");

		return syntax;
	}

	MethodBodySyntax? ParseMethodBody()
	{
		if (Is("{"))
		{
			BlockMethodBodySyntax syntax = new()
			{
				OpenBraceToken = Take(),
				Statements = []
			};

			while (!AtEnd && !Is("}"))
				syntax.Statements.Add(ParseStatementOrSkipped());

			syntax.CloseBraceToken = Expect("}");
			return syntax;
		}

		if (TakeOperator("=>") is TokenRange arrow)
			return new ExpressionMethodBodySyntax
			{
				ArrowToken = arrow,
				Expression = ParseExpression(),
				SemicolonToken = Expect(";")
			};

		return null;
	}

	StatementSyntax ParseStatementOrSkipped()
	{
		int start = index;
		StatementSyntax? statement = ParseStatement();

		if (statement is not null)
			return statement;

		ReportAndAdvance("Expected statement.");

		if (index == start)
			ReportAndAdvance("Parser did not consume a token.");

		return new EmptyStatementSyntax();
	}

	StatementSyntax? ParseStatement()
	{
		if (Is(";"))
			return new EmptyStatementSyntax { SemicolonToken = Take() };

		if (Is("{"))
			return ParseBlockStatement();

		if (Is("case"))
			return new CaseStatementSyntax { CaseKeyword = Take(), Expression = ParseExpression(), ColonToken = Expect(":") };

		if (Is("default"))
			return new DefaultStatementSyntax { DefaultKeyword = Take(), ColonToken = Expect(":") };

		if (IsIdentifier() && PeekValue(1) == ":" && PeekValue(2) != ":")
			return new LabelStatementSyntax { Identifier = TakeIdentifier(), ColonToken = Expect(":") };

		if (IsAny(StatementKeywords))
			return ParseKeywordStatement();

		int start = index;
		DeclarationStatementSyntax? declaration = ParseDeclarationStatement();
		if (declaration is not null && Is(";"))
			return new DeclarationStatementStatementSyntax { DeclarationStatement = declaration, SemicolonToken = Take() };

		index = start;
		ExpressionSyntax? expression = ParseExpression();
		if (expression is not null)
			return new ExpressionStatementSyntax { Expression = expression, SemicolonToken = Expect(";") };

		return null;
	}

	BlockStatementSyntax ParseBlockStatement()
	{
		BlockStatementSyntax syntax = new()
		{
			OpenBraceToken = Expect("{"),
			Statements = []
		};

		while (!AtEnd && !Is("}"))
			syntax.Statements.Add(ParseStatementOrSkipped());

		syntax.CloseBraceToken = Expect("}");
		return syntax;
	}

	KeywordStatementSyntax ParseKeywordStatement()
	{
		KeywordStatementSyntax syntax = new()
		{
			Keyword = Take()
		};

		if (syntax.Keyword?.Value == "delete" && Is("delegate") && ValueIsAny(PeekValue(1), ";"))
		{
			syntax.SpecialKeyword = Take();
			syntax.Body = new EmptyStatementSyntax { SemicolonToken = Expect(";") };
			return syntax;
		}

		if (syntax.Keyword?.Value == "yield" && Is("break") && ValueIsAny(PeekValue(1), ";"))
		{
			syntax.SpecialKeyword = Take();
			syntax.Body = new EmptyStatementSyntax { SemicolonToken = Expect(";") };
			return syntax;
		}

		if (KeywordRequiresExpressionBody(syntax.Keyword?.Value))
		{
			if (!Is(";"))
			{
				syntax.Body = new ExpressionStatementSyntax
				{
					Expression = ParseExpression(),
					SemicolonToken = Expect(";")
				};
			}
			else
			{
				syntax.Body = new EmptyStatementSyntax { SemicolonToken = Take() };
			}
			return syntax;
		}

		if (KeywordAllowsParenthesizedCondition(syntax.Keyword?.Value) && Is("("))
		{
			syntax.OpenParenToken = Take();
			if (!Is(")"))
				syntax.Condition = syntax.Keyword?.Value == "catch"
					? ParseCatchStatementCondition()
					: ParseStatementCondition();
			syntax.CloseParenToken = Expect(")");
		}

		if (Is("{"))
		{
			syntax.BodyOpenBraceToken = Take();
			syntax.BodyStatements = [];

			while (!AtEnd && !Is("}"))
				syntax.BodyStatements.Add(ParseStatementOrSkipped());

			syntax.BodyCloseBraceToken = Expect("}");
		}
		else if (!IsStatementBoundary())
		{
			syntax.Body = ParseStatementOrSkipped();
		}
		else if (Is(";"))
		{
			syntax.Body = new EmptyStatementSyntax { SemicolonToken = Take() };
		}

		return syntax;
	}

	static bool KeywordRequiresExpressionBody(string? keyword)
	{
		return keyword is "return" or "yield" or "delete" or "throw";
	}

	static bool KeywordAllowsParenthesizedCondition(string? keyword)
	{
		return keyword is "if" or "while" or "do" or "for" or "switch" or "within" or "catch" or "foreach";
	}

	StatementConditionSyntax? ParseCatchStatementCondition()
	{
		DeclarationTargetSyntax? target = ParseDeclarationTarget();
		if (target is null)
		{
			Report(Current, "Expected catch target.");
			return null;
		}

		return new ClauseStatementConditionSyntax
		{
			DeclarationStatement = new DeclarationStatementSyntax { Target = target },
			Clauses = []
		};
	}

	StatementConditionSyntax? ParseStatementCondition()
	{
		int start = index;
		DeclarationTargetSyntax? target = ParseDeclarationTarget();
		if (target is not null && TakeIf("in") is Token inKeyword)
			return new IterationStatementConditionSyntax { Target = target, InKeyword = inKeyword, Expression = ParseExpression() };

		index = start;

		ClauseStatementConditionSyntax syntax = new() { Clauses = [] };

		int declarationStart = index;
		DeclarationStatementSyntax? declaration = ParseDeclarationStatement();
		if (declaration is not null && Is(";"))
		{
			syntax.DeclarationStatement = declaration;
		}
		else
		{
			index = declarationStart;
			if (!Is(";") && !Is(")"))
				syntax.Clauses.Add(new StatementConditionClauseSyntax { Expression = ParseExpression() });
		}

		while (TakeIf(";") is Token semicolon)
		{
			StatementConditionClauseSyntax clause = new() { SemicolonToken = semicolon };

			if (!Is(";") && !Is(")"))
				clause.Expression = ParseExpression();

			syntax.Clauses.Add(clause);
		}

		return syntax;
	}

	DeclarationStatementSyntax? ParseDeclarationStatement()
	{
		DeclarationTargetSyntax? target = ParseDeclarationTarget();
		if (target is null)
			return null;

		return new DeclarationStatementSyntax
		{
			Target = target,
			Assignment = Is("=") ? ParseAssignment(consumeSemicolon: false) : null
		};
	}

	DeclarationTargetSyntax? ParseDeclarationTarget()
	{
		if (Is("inline"))
		{
			Report(Current, "Local inline constant declarations are not supported.");
			Take();
		}
		Token? fixedKeyword = TakeIf("fixed");
		Token? stackAllocKeyword = fixedKeyword is null ? TakeIf("stackalloc") : null;
		if (Is("auto"))
		{
			DeclarationTargetSyntax syntax = new() { FixedKeyword = fixedKeyword, StackAllocKeyword = stackAllocKeyword, AutoKeyword = Take() };

			if (Is("("))
			{
				syntax.OpenParenToken = Take();
				syntax.IdentifierList = ParseIdentList(")");
				syntax.CloseParenToken = Expect(")");
			}
			else
			{
				syntax.AutoIdentifier = ExpectIdentifier();
			}

			return syntax;
		}

		int start = index;
		TypeSyntax? type = ParseType(requireIdentifierAfterTerminalTargetSpec: true);
		if (type is null || !IsIdentifier())
		{
			index = start;
			if (fixedKeyword is not null)
				index = fixedKeyword.Value.Index;
			if (stackAllocKeyword is not null)
				index = stackAllocKeyword.Value.Index;
			return null;
		}

		return new DeclarationTargetSyntax { FixedKeyword = fixedKeyword, StackAllocKeyword = stackAllocKeyword, Type = type, Identifier = TakeIdentifier() };
	}

	ExpressionSyntax? ParseExpression()
	{
		ExpressionSyntax? lambda = TryParseLambdaExpression();
		if (lambda is not null)
			return lambda;

		ExpressionSyntax? first = ParseRangeOrAssignmentExpression();
		if (first is null)
			return null;

		if (Is(","))
		{
			CommaExpressionSyntax comma = new()
			{
				Expressions = [first],
				Commas = []
			};

			while (TakeIf(",") is Token commaToken)
			{
				comma.Commas.Add(commaToken);

				ExpressionSyntax? next = ParseAssignmentExpression();
				if (next is not null)
					comma.Expressions.Add(next);
				else
					break;
			}

			return comma;
		}

		return first;
	}

	ExpressionSyntax? ParseExpressionItem()
	{
		return TryParseLambdaExpression() ?? ParseRangeOrAssignmentExpression();
	}

	ExpressionSyntax? ParseRangeOrAssignmentExpression()
	{
		if (TakeOperator("..") is TokenRange leadingRange)
			return new RangeExpressionSyntax { DotDotToken = leadingRange, End = ParseAssignmentExpression() };

		ExpressionSyntax? start = ParseAssignmentExpression();
		if (start is null)
			return null;

		if (TakeOperator("..") is TokenRange range)
			return new RangeExpressionSyntax
			{
				Start = start,
				DotDotToken = range,
				End = IsExpressionBoundary() ? null : ParseAssignmentExpression()
			};

		return start;
	}

	ExpressionSyntax? ParseAssignmentExpression()
	{
		ExpressionSyntax? left = ParseConditionalExpression();
		if (left is null)
			return null;

		TokenRange? op = TakeOperator(">>=", "<<=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "=");
		if (op is null)
			return left;

		return new AssignmentExpressionSyntax
		{
			Left = left,
			Operator = new AssignmentOperatorSyntax { Operator = op },
			Right = ParseExpression()
		};
	}

	ExpressionSyntax? ParseConditionalExpression()
	{
		ExpressionSyntax? condition = ParseBinaryExpression();
		if (condition is null)
			return null;

		if (TakeIf("?") is Token question)
		{
			return new ConditionalExpressionSyntax
			{
				Condition = condition,
				QuestionToken = question,
				WhenTrue = ParseExpression(),
				ColonToken = Expect(":"),
				WhenFalse = ParseExpression()
			};
		}

		return condition;
	}

	ExpressionSyntax? ParseBinaryExpression()
	{
		ExpressionSyntax? first = ParseUnaryExpression();
		if (first is null)
			return null;

		BinaryExpressionSyntax syntax = new() { FirstExpression = first, Parts = [] };

		while (TryTakeBinaryOperator() is TokenRange op)
		{
			ExpressionSyntax? expression = ParseUnaryExpression();
			if (expression is null)
			{
				Report(Current, "Expected expression after binary operator.");
				break;
			}

			syntax.Parts.Add(new BinaryExpressionPartSyntax
			{
				Operator = new BinaryOperatorSyntax { Operator = op },
				Expression = expression
			});
		}

		if (syntax.Parts.Count == 0)
			return first;

		return syntax;
	}

	TokenRange? TryTakeBinaryOperator()
	{
		if (IsExpressionBoundary())
			return null;

		SkipTrivia();
		foreach (string op in new[] { "||", "??", "&&", "==", "!=", "<=", ">=", "<<", ">>", "|", "^", "&", "<", ">", "+", "-", "*", "/", "%" })
		{
			TokenRange? range = MatchOperatorAt(index, op);
			if (range is null)
				continue;
			if (OperatorWouldStartCompoundAssignment(range.Value))
				return null;

			index += range.Value.Count;
			return range;
		}

		return null;
	}

	bool OperatorWouldStartCompoundAssignment(TokenRange range)
	{
		int next = range.Index + range.Count;
		return next < tokens.Count
			&& tokens[next].Class == TokenClass.Symbol
			&& tokens[next].Value == "="
			&& AreAdjacent(tokens[next - 1], tokens[next]);
	}

	ExpressionSyntax? ParseUnaryExpression()
	{
		UnaryExpressionSyntax syntax = new() { Prefixes = [] };

		while (TryParseUnaryPrefix() is UnaryPrefixSyntax prefix)
			syntax.Prefixes.Add(prefix);

		syntax.Expression = ParsePostfixExpression();

		if (syntax.Expression is null)
		{
			if (syntax.Prefixes.Count == 0)
				return null;

			Report(Current, "Expected expression after unary prefix.");
		}

		if (TakeIf("finally") is Token finallyKeyword)
		{
			syntax.FinallyKeyword = finallyKeyword;
			if (TakeIf("delete") is Token deleteKeyword)
			{
				syntax.DeleteKeyword = deleteKeyword;
			}
			else
			{
				syntax.FinallyMethodIdentifier = ExpectIdentifier();
				syntax.FinallyOpenParenToken = Expect("(");
				if (!Is(")"))
					syntax.FinallyArgumentList = ParseArgumentList(")");
				syntax.FinallyCloseParenToken = Expect(")");
			}
		}

		if (syntax.Prefixes.Count == 0)
		{
			syntax.Prefixes = null;

			if (syntax.FinallyKeyword is null)
				return syntax.Expression;
		}

		return syntax;
	}

	UnaryPrefixSyntax? TryParseUnaryPrefix()
	{
		if (Is("(") && (PeekValue(1) == "new" || PeekValue(1) == "stackalloc") && PeekValue(2) == ")")
		{
			Token? openParen = Take();
			Token? keyword = Take();
			Token? closeParen = Take();
			return new UnaryPrefixSyntax
			{
				NewKeyword = keyword?.Value == "new" ? keyword : null,
				StackAllocKeyword = keyword?.Value == "stackalloc" ? keyword : null,
				OpenParenToken = openParen,
				CloseParenToken = closeParen
			};
		}

		if (Is("within"))
		{
				UnaryPrefixSyntax syntax = new()
				{
					OperatorOrKeyword = Take()?.Range,
					OpenParenToken = Expect("(")
				};

			if (!Is(")"))
				syntax.Expression = ParseExpression();

			syntax.CloseParenToken = Expect(")");
			return syntax;
		}

		if (IsAny("await", "postpone", "throw"))
			return new UnaryPrefixSyntax { OperatorOrKeyword = Take()?.Range };

		TokenRange? op = TakeOperator("++", "--", "+", "-", "!", "~", "&", "*", "^");
		return op is null ? null : new UnaryPrefixSyntax { OperatorOrKeyword = op };
	}

	ExpressionSyntax? ParsePostfixExpression()
	{
		PrimaryExpressionSyntax? primary = ParsePrimaryExpression();
		if (primary is null)
			return null;

		PostfixExpressionSyntax syntax = new() { Expression = primary, Parts = [] };

		while (TryParsePostfixPart() is PostfixPartSyntax part)
			syntax.Parts.Add(part);

		if (Is("{") && syntax.Parts.Count > 0)
			syntax.InitializerList = ParseInitializerList();

		if (syntax.Parts.Count == 0 && syntax.InitializerList is null)
			return primary;

		return syntax;
	}

	PostfixPartSyntax? TryParsePostfixPart()
	{
		if (Is("("))
			return new CallPostfixPartSyntax
			{
				OpenParenToken = Take(),
				ArgumentList = Is(")") ? null : ParseArgumentList(")"),
				CloseParenToken = Expect(")")
			};

		if (Is("["))
			return new IndexPostfixPartSyntax
			{
				OpenBracketToken = Take(),
				ArgumentList = Is("]") ? null : ParseArgumentList("]"),
				CloseBracketToken = Expect("]")
			};

		if (IsOperator(".."))
			return null;

		if (Is(".") && PeekValue(1) == "[")
			return new NamelessIndexerPostfixPartSyntax
			{
				DotToken = Take(),
				OpenBracketToken = Expect("["),
				ArgumentList = Is("]") ? null : ParseArgumentList("]"),
				CloseBracketToken = Expect("]")
			};

		if (Is("."))
			return new MemberPostfixPartSyntax { DotToken = Take(), Identifier = ExpectIdentifier() };

		if (Is("<") && TryParseGenericPostfixPart() is GenericPostfixPartSyntax generic)
			return generic;

		TokenRange? op = TakeOperator("++", "--");
		return op is null ? null : new PostfixOperatorPartSyntax { Operator = op };
	}

	GenericPostfixPartSyntax? TryParseGenericPostfixPart()
	{
		int start = index;
		int diagnosticStart = diagnostics.Count;
		GenericPostfixPartSyntax syntax = new()
		{
			LessThanToken = Expect("<"),
			TypeArgumentList = ParseTypeList(">")
		};

		if (!Is(">"))
		{
			index = start;
			diagnostics.RemoveRange(diagnosticStart, diagnostics.Count - diagnosticStart);
			return null;
		}

		syntax.GreaterThanToken = Take();
		return syntax;
	}

	PrimaryExpressionSyntax? ParsePrimaryExpression()
	{
		if (TryParseAllocatedInterpolatedStringExpression() is InterpolatedStringExpressionSyntax allocatedInterpolation)
			return allocatedInterpolation;

		if (IsClass(TokenClass.InterpolatedString))
			return ParseInterpolatedStringExpression();

		if (IsLiteral())
			return new LiteralExpressionSyntax { Literal = Take() };

		if (Is("this"))
			return new ThisExpressionSyntax { ThisKeyword = Take() };

		if (Is("default"))
			return new DefaultExpressionSyntax { DefaultKeyword = Take() };

		if (Is("sizeof"))
			return new SizeOfExpressionSyntax { SizeOfKeyword = Take(), OpenParenToken = Expect("("), Type = ParseType(), CloseParenToken = Expect(")") };

		if (Is("vtableof"))
			return new VTableOfExpressionSyntax
			{
				VTableOfKeyword = Take(),
				OpenParenToken = Expect("("),
				Type = ParseType(),
				ColonToken = Expect(":"),
				InterfaceType = ParseType(),
				CloseParenToken = Expect(")")
			};

		if (Is("typenameof"))
			return ParseNameOfExpression();

		if (Is("symbolof"))
			return ParseSymbolOfExpression();

		if (Is("within") || Is("init") || Is("new") || Is("stackalloc"))
			return TryParseConstructionExpression();

		if (Is("("))
			return ParseParenthesizedCastOrGroupedExpression();

		if (Is("["))
			return ParseArrayExpression();

		if (Is("{"))
			return ParseInitializerList();

		return ParseQualifiedNameExpression();
	}

	InterpolatedStringExpressionSyntax? TryParseAllocatedInterpolatedStringExpression()
	{
		int start = index;
		int diagnosticStart = diagnostics.Count;
		Token? withinKeyword = null;
		Token? withinOpenParen = null;
		ExpressionSyntax? allocator = null;
		Token? withinCloseParen = null;

		if (Is("within") && PeekValue(1) == "(")
		{
			withinKeyword = Take();
			withinOpenParen = Expect("(");
			allocator = ParseExpression();
			withinCloseParen = Expect(")");
		}

		if ((!Is("new") && !Is("stackalloc")) || Peek(1)?.Class != TokenClass.InterpolatedString)
		{
			index = start;
			diagnostics.RemoveRange(diagnosticStart, diagnostics.Count - diagnosticStart);
			return null;
		}

		Token keyword = Take()!.Value;
		InterpolatedStringExpressionSyntax syntax = ParseInterpolatedStringExpression();
		syntax.WithinKeyword = withinKeyword;
		syntax.WithinOpenParenToken = withinOpenParen;
		syntax.AllocatorExpression = allocator;
		syntax.WithinCloseParenToken = withinCloseParen;
		syntax.NewKeyword = keyword.Value == "new" ? keyword : null;
		syntax.StackAllocKeyword = keyword.Value == "stackalloc" ? keyword : null;
		return syntax;
	}

	InterpolatedStringExpressionSyntax ParseInterpolatedStringExpression()
	{
		Token? token = Take();
		InterpolatedStringExpressionSyntax syntax = new() { Literal = token };
		if (token is not Token literal)
		{
			Report(token, "Interpolated string is missing a token.");
			return syntax;
		}

		ParseInterpolatedStringSegments(literal, syntax.Segments);
		return syntax;
	}

	void ParseInterpolatedStringSegments(Token token, List<InterpolatedStringSegmentSyntax> segments)
	{
		string text = token.Value;
		if (!text.StartsWith("$\"", StringComparison.Ordinal))
		{
			Report(token, "Interpolated string must start with '$\"'.");
			return;
		}

		bool terminated = text.Length >= 3 && text[^1] == '"';
		int end = terminated ? text.Length - 1 : text.Length;
		if (!terminated)
			Report(token, "Unterminated interpolated string.");

		StringBuilder literal = new();
		for (int i = 2; i < end;)
		{
			char c = text[i];
			if (c == '\\')
			{
				if (i + 1 < end)
				{
					literal.Append(text, i, 2);
					i += 2;
				}
				else
				{
					literal.Append(c);
					i++;
				}
				continue;
			}

			if (c == '{')
			{
				if (i + 1 < end && text[i + 1] == '{')
				{
					literal.Append('{');
					i += 2;
					continue;
				}

				AddInterpolatedStringTextSegment(segments, literal);
				int expressionStart = i + 1;
				int expressionEnd = FindInterpolationHoleEnd(text, expressionStart, end, token);
				if (expressionEnd < 0)
				{
					Report(token, "Unterminated interpolation hole.");
					return;
				}

				string expressionText = text[expressionStart..expressionEnd];
				if (string.IsNullOrWhiteSpace(expressionText))
				{
					Report(token, "Interpolation hole must contain an expression.");
				}
				else
				{
					ExpressionSyntax? expression = ParseInterpolatedHoleExpression(expressionText, token, expressionStart);
					segments.Add(new InterpolatedStringExpressionSegmentSyntax { Expression = expression });
				}

				i = expressionEnd + 1;
				continue;
			}

			if (c == '}')
			{
				if (i + 1 < end && text[i + 1] == '}')
				{
					literal.Append('}');
					i += 2;
					continue;
				}

				Report(token, "Unmatched '}' in interpolated string.");
				i++;
				continue;
			}

			literal.Append(c);
			i++;
		}

		AddInterpolatedStringTextSegment(segments, literal);
	}

	void AddInterpolatedStringTextSegment(List<InterpolatedStringSegmentSyntax> segments, StringBuilder literal)
	{
		if (literal.Length == 0)
			return;

		segments.Add(new InterpolatedStringTextSegmentSyntax { Text = literal.ToString() });
		literal.Clear();
	}

	ExpressionSyntax? ParseInterpolatedHoleExpression(string expressionText, Token token, int expressionStartOffset)
	{
		TokenSequence holeTokens = new(CampTokenizer.Tokenize(expressionText), token.LineNumber, token.Column + expressionStartOffset);
		CampParser parser = new(holeTokens);
		ExpressionSyntax? expression = parser.ParseExpressionItem();

		if (!parser.AtEnd)
			parser.ReportAndAdvance("Unexpected token in interpolation hole.");

		diagnostics.AddRange(parser.Diagnostics);
		return expression;
	}

	int FindInterpolationHoleEnd(string text, int start, int end, Token token)
	{
		int parenDepth = 0;
		int bracketDepth = 0;
		int braceDepth = 0;

		for (int i = start; i < end;)
		{
			char c = text[i];
			if (c is '"' or '\'' or '`')
			{
				i = SkipQuotedText(text, i, end);
				continue;
			}

			if (c == '/' && i + 1 < end && text[i + 1] == '*')
			{
				i = SkipBlockCommentText(text, i + 2, end);
				continue;
			}

			if (c == '/' && i + 1 < end && text[i + 1] == '/')
			{
				Report(token, "Line comments cannot terminate inside an interpolated string.");
				return -1;
			}

			switch (c)
			{
				case '(':
					parenDepth++;
					i++;
					break;
				case ')':
					if (parenDepth > 0)
						parenDepth--;
					i++;
					break;
				case '[':
					bracketDepth++;
					i++;
					break;
				case ']':
					if (bracketDepth > 0)
						bracketDepth--;
					i++;
					break;
				case '{':
					braceDepth++;
					i++;
					break;
				case '}':
					if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
						return i;
					if (braceDepth > 0)
						braceDepth--;
					i++;
					break;
				default:
					i++;
					break;
			}
		}

		return -1;
	}

	static int SkipQuotedText(string text, int start, int end)
	{
		char quote = text[start];
		int i = start + 1;
		while (i < end)
		{
			char c = text[i++];
			if (c == quote)
				break;
			if (c == '\\' && i < end)
				i++;
		}
		return i;
	}

	static int SkipBlockCommentText(string text, int start, int end)
	{
		int i = start;
		while (i + 1 < end)
		{
			if (text[i] == '*' && text[i + 1] == '/')
				return i + 2;
			i++;
		}
		return end;
	}

	NameOfExpressionSyntax ParseNameOfExpression()
	{
		NameOfExpressionSyntax syntax = new()
		{
			NameOfKeyword = Take(),
			OpenParenToken = Expect("(")
		};

		while (!AtEnd && !Is(")"))
		{
			if (Take() is Token token)
				syntax.Tokens.Add(token);
		}

		syntax.CloseParenToken = Expect(")");
		return syntax;
	}

	SymbolOfExpressionSyntax ParseSymbolOfExpression()
	{
		SymbolOfExpressionSyntax syntax = new()
		{
			SymbolOfKeyword = Take(),
			OpenParenToken = Expect("(")
		};

		while (!AtEnd && !Is(")"))
		{
			if (Take() is Token token)
				syntax.Tokens.Add(token);
		}

		syntax.CloseParenToken = Expect(")");
		return syntax;
	}

	PrimaryExpressionSyntax? ParseParenthesizedCastOrGroupedExpression()
	{
		int start = index;
		CastExpressionSyntax? cast = TryParseCastExpression();
		if (cast is not null)
			return cast;

		index = start;
		Token? open = Expect("(");

		if (Is(")"))
			return new GroupedExpressionSyntax { OpenParenToken = open, CloseParenToken = Take() };

		GroupedExpressionItemSyntax first = ParseGroupedExpressionItem();

		if (Is(","))
		{
			GroupedExpressionSyntax grouped = new()
			{
				OpenParenToken = open,
				ItemList = new GroupedExpressionItemListSyntax
				{
					Items = [first],
					Commas = []
				}
			};

			while (TakeIf(",") is Token comma)
			{
				grouped.ItemList.Commas.Add(comma);

				if (Is(")"))
					break;

				grouped.ItemList.Items.Add(ParseGroupedExpressionItem());
			}

			grouped.CloseParenToken = Expect(")");
			return grouped;
		}

		if (first.Identifier is not null)
			return new GroupedExpressionSyntax
			{
				OpenParenToken = open,
				ItemList = new GroupedExpressionItemListSyntax { Items = [first], Commas = [] },
				CloseParenToken = Expect(")")
			};

		return new ParenthesizedExpressionSyntax
		{
			OpenParenToken = open,
			Expression = first.Expression,
			CloseParenToken = Expect(")")
		};
	}

	CastExpressionSyntax? TryParseCastExpression()
	{
		int start = index;
		Token? open = TakeIf("(");
		if (open is null)
			return null;

		Token? unsafeKeyword = TakeIf("unsafe");
		Token? castKeyword = IsAny("params", "struct", "class")
			? Take()
			: null;

		TypeSyntax? type = null;
		TypeDeclaratorSyntax? lifetimeDeclarator = null;
		if (castKeyword is null && IsAny("escaped", "scoped", "unscoped"))
		{
			int lifetimeStart = index;
			TypeDeclaratorSyntax? declarator = ParseTypeDeclarator();
			if (declarator is not null && Is(")"))
				lifetimeDeclarator = declarator;
			else
				index = lifetimeStart;
		}

		if (castKeyword is null && lifetimeDeclarator is null)
			type = ParseType();

		if ((castKeyword is null && type is null && lifetimeDeclarator is null) || TakeIf(")") is not Token close || IsExpressionBoundary())
		{
			index = start;
			return null;
		}

		ExpressionSyntax? expression = ParseUnaryExpression();
		if (expression is null)
		{
			index = start;
			return null;
		}

		return new CastExpressionSyntax
		{
			OpenParenToken = open,
			UnsafeKeyword = unsafeKeyword,
			Type = type,
			LifetimeDeclarator = lifetimeDeclarator,
			CastKeyword = castKeyword,
			CloseParenToken = close,
			Expression = expression
		};
	}

	GroupedExpressionItemSyntax ParseGroupedExpressionItem()
	{
		GroupedExpressionItemSyntax syntax = new();

		if (IsIdentifier() && PeekValue(1) == ":")
		{
			syntax.Identifier = TakeIdentifier();
			syntax.ColonToken = Expect(":");
		}

		syntax.Expression = ParseExpressionItem();
		return syntax;
	}

	ArrayExpressionSyntax ParseArrayExpression()
	{
		ArrayExpressionSyntax syntax = new() { OpenBracketToken = Expect("[") };

		if (!Is("]"))
			syntax.ExpressionList = ParseExpressionList("]");

		syntax.CloseBracketToken = Expect("]");
		return syntax;
	}

	InitializerListSyntax ParseInitializerList()
	{
		InitializerListSyntax syntax = new()
		{
			OpenBraceToken = Expect("{"),
			ItemList = new InitializerItemListSyntax { Items = [], Commas = [] }
		};

		ParseCommaList(syntax.ItemList.Items, syntax.ItemList.Commas, ParseInitializerItem, "}");
		syntax.CloseBraceToken = Expect("}");
		return syntax;
	}

	InitializerItemSyntax? ParseInitializerItem()
	{
		int start = index;
		InitializerItemSyntax syntax = new();

		if (Is("."))
		{
			syntax.Target = ParseInitializerTarget();
			syntax.EqualsToken = Expect("=");
			syntax.Expression = ParseExpressionItem();
		}
		else
		{
			syntax.Expression = ParseExpressionItem();

			if (syntax.Expression is AssignmentExpressionSyntax { Operator: not null })
				diagnostics.Add(new ParseDiagnostic(new TokenRange(tokens, start, 1), "Initializer assignments must start with '.'.", DiagnosticCodes.InitializerAssignmentRequiresDot));
		}

		return syntax.Expression is null && syntax.Target is null ? null : syntax;
	}

	InitializerTargetSyntax ParseInitializerTarget()
	{
		InitializerTargetSyntax syntax = new() { Parts = [] };

		while (Is("."))
		{
			InitializerTargetPartSyntax part = new() { DotToken = Take() };

			if (Is("["))
			{
				part.OpenBracketToken = Take();
				part.ArgumentList = Is("]") ? null : ParseArgumentList("]");
				part.CloseBracketToken = Expect("]");
			}
			else
			{
				part.Identifier = ExpectIdentifier();
			}

			syntax.Parts.Add(part);
		}

		return syntax;
	}

	ConstructionExpressionSyntax? TryParseConstructionExpression()
	{
		int start = index;
		ConstructionExpressionSyntax syntax = new();

		if (Is("within") && PeekValue(1) == "(")
		{
			syntax.WithinKeyword = Take();
			syntax.WithinOpenParenToken = Expect("(");
			syntax.AllocatorExpression = ParseExpression();
			syntax.WithinCloseParenToken = Expect(")");
		}

		if (!Is("init") && !Is("new") && !Is("stackalloc"))
		{
			index = start;
			return null;
		}

		syntax.Keyword = Take();
		if (syntax.Keyword?.Value == "init")
			Report(syntax.Keyword, "`init` is no longer a Camp allocation keyword. Use stackalloc for dynamic stack storage, new for allocator-backed storage, fixed for fixed-size local storage, or Type(args) for construction into existing storage.");

		if (!Is("("))
			syntax.Type = ParseType(allowFixedArrayLength: false);

		if (Is("["))
		{
			syntax.OpenBracketToken = Take();
			if (!Is("]"))
				syntax.ElementCount = ParseExpression();
			syntax.CloseBracketToken = Expect("]");
		}
		else
		{
			syntax.OpenParenToken = Expect("(");
			if (!Is(")"))
				syntax.ArgumentList = ParseArgumentList(")");
			syntax.CloseParenToken = Expect(")");
		}

		if (Is("{"))
			syntax.InitializerList = ParseInitializerList();

		return syntax;
	}

	QualifiedNameExpressionSyntax? ParseQualifiedNameExpression()
	{
		QualifiedNameExpressionSyntax syntax = new()
		{
			Qualifiers = ParseQualifiers(),
			Identifier = TakeIdentifier()
		};

		if (syntax.Identifier is null && syntax.Qualifiers is null)
			return null;

		return syntax;
	}

	LambdaExpressionSyntax? TryParseLambdaExpression()
	{
		int start = index;
		int diagnosticStart = diagnostics.Count;
		Token? withinKeyword = null;
		Token? withinOpenParen = null;
		ExpressionSyntax? allocatorExpression = null;
		Token? withinCloseParen = null;
		Token? newKeyword = null;
		Token? delegateKeyword = null;

		if (Is("within") && PeekValue(1) == "(")
		{
			withinKeyword = Take();
			withinOpenParen = Expect("(");
			allocatorExpression = ParseExpression();
			withinCloseParen = Expect(")");
		}

		if (Is("new") && PeekValue(1) == "delegate")
		{
			newKeyword = Take();
			delegateKeyword = Take();
		}

		if (IsIdentifier() && OperatorAfterOffset(1, "=>") is TokenRange singleArrow)
		{
			return new LambdaExpressionSyntax
			{
				WithinKeyword = withinKeyword,
				WithinOpenParenToken = withinOpenParen,
				AllocatorExpression = allocatorExpression,
				WithinCloseParenToken = withinCloseParen,
				NewKeyword = newKeyword,
				DelegateKeyword = delegateKeyword,
				Parameter = new LambdaParameterSyntax { Identifier = TakeIdentifier() },
				ArrowToken = TakeOperator("=>"),
				Body = ParseLambdaBody()
			};
		}

		if (!Is("("))
		{
			if (newKeyword is not null || withinKeyword is not null)
			{
				index = start;
				diagnostics.RemoveRange(diagnosticStart, diagnostics.Count - diagnosticStart);
			}
			return null;
		}

		LambdaExpressionSyntax syntax = new()
		{
			WithinKeyword = withinKeyword,
			WithinOpenParenToken = withinOpenParen,
			AllocatorExpression = allocatorExpression,
			WithinCloseParenToken = withinCloseParen,
			NewKeyword = newKeyword,
			DelegateKeyword = delegateKeyword,
			OpenParenToken = Take(),
			ParameterList = new LambdaParameterListSyntax { Parameters = [], Commas = [] }
		};

		ParseCommaList(syntax.ParameterList.Parameters, syntax.ParameterList.Commas, ParseLambdaParameter, ")");
		syntax.CloseParenToken = Expect(")");

		if (!IsOperator("=>"))
		{
			index = start;
			diagnostics.RemoveRange(diagnosticStart, diagnostics.Count - diagnosticStart);
			return null;
		}

		syntax.ArrowToken = TakeOperator("=>");
		syntax.Body = ParseLambdaBody();
		return syntax;
	}

	LambdaParameterSyntax? ParseLambdaParameter()
	{
		if (IsIdentifier() && ValueIsAny(PeekValue(1), ",", ")"))
			return new LambdaParameterSyntax { Identifier = TakeIdentifier() };

		ParameterSyntax? parameter = ParseParameter();
		return parameter is null ? null : new LambdaParameterSyntax { Parameter = parameter };
	}

	LambdaBodySyntax ParseLambdaBody()
	{
		LambdaBodySyntax syntax = new();

		if (Is("{") || IsOperator("=>"))
			syntax.MethodBody = ParseMethodBody();
		else
			syntax.Expression = ParseExpressionItem();

		return syntax;
	}

	ExpressionListSyntax ParseExpressionList(string close)
	{
		ExpressionListSyntax syntax = new() { Expressions = [], Commas = [] };
		ParseCommaList(syntax.Expressions, syntax.Commas, ParseExpressionItem, close);
		return syntax;
	}

	ArgumentListSyntax ParseArgumentList(string close)
	{
		ArgumentListSyntax syntax = new() { Arguments = [], Commas = [] };
		ParseCommaList(syntax.Arguments, syntax.Commas, ParseArgument, close);
		return syntax;
	}

	ArgumentSyntax? ParseArgument()
	{
		ArgumentSyntax syntax = new();

		if (IsIdentifier() && PeekValue(1) == ":")
		{
			syntax.Identifier = TakeIdentifier();
			syntax.ColonToken = Expect(":");
		}
		else if (Is("out"))
		{
			syntax.OutKeyword = Take();
			syntax.DeclarationTarget = ParseDeclarationTarget();
			if (syntax.DeclarationTarget is not null)
				return syntax;
		}
		else if (Is("catch"))
		{
			syntax.CatchKeyword = Take();
			syntax.DeclarationTarget = ParseDeclarationTarget();
			if (syntax.DeclarationTarget is not null)
				return syntax;
		}
		else if (Is("within") && PeekValue(1) != "(")
		{
			syntax.WithinKeyword = Take();
		}

		syntax.Expression = ParseExpressionItem();
		return syntax.Expression is null && syntax.Identifier is null && syntax.CatchKeyword is null ? null : syntax;
	}

	void ParseCommaList<T>(List<T> items, List<Token> commas, Func<T?> parseItem, params string[] closes)
		where T : class
	{
		while (!AtEnd && !IsAny(closes))
		{
			int start = index;
			T? item = parseItem();

			if (item is not null)
				items.Add(item);
			else
				ReportAndAdvance("Expected list item.");

			if (TakeIf(",") is Token comma)
			{
				commas.Add(comma);
				continue;
			}

			if (index == start)
				ReportAndAdvance("Parser did not consume a token.");

			break;
		}
	}

	bool AtEnd
	{
		get
		{
			SkipTrivia();
			return index >= tokens.Count;
		}
	}

	Token? Current
	{
		get
		{
			SkipTrivia();
			return index < tokens.Count ? tokens[index] : null;
		}
	}

	void SkipTrivia()
	{
		while (index < tokens.Count && IsTrivia(tokens[index]))
			index++;
	}

	static bool IsTrivia(Token token)
	{
		return token.Class is TokenClass.Whitespace
			or TokenClass.NewLine
			or TokenClass.LineComment
			or TokenClass.BlockComment;
	}

	Token? Take()
	{
		SkipTrivia();
		return index < tokens.Count ? tokens[index++] : null;
	}

	Token? TakeIf(string value)
	{
		return Is(value) ? Take() : null;
	}

	Token? TakeIfClass(TokenClass tokenClass)
	{
		return IsClass(tokenClass) ? Take() : null;
	}

	Token? TakeIdentifier()
	{
		return IsIdentifier() ? Take() : null;
	}

	Token? Expect(string value)
	{
		Token? token = TakeIf(value);
		if (token is null)
			Report(Current, $"Expected '{value}'.");

		return token;
	}

	Token? ExpectIdentifier()
	{
		Token? token = TakeIdentifier();
		if (token is null)
			Report(Current, "Expected identifier.");

		return token;
	}

	bool Is(string value)
	{
		return Current?.Value == value;
	}

	bool IsClass(TokenClass tokenClass)
	{
		return Current?.Class == tokenClass;
	}

	bool IsIdentifier()
	{
		return Current?.Class is TokenClass.Identifier;
	}

	bool IsLiteral()
	{
		Token? token = Current;
		return token?.Class is TokenClass.Number or TokenClass.String
			|| token?.Value is "true" or "false" or "null";
	}

	bool IsAny(params string[] values)
	{
		return ValueIsAny(Current?.Value, values);
	}

	static bool ValueIsAny(string? value, params string[] values)
	{
		foreach (string candidate in values)
		{
			if (value == candidate)
				return true;
		}

		return false;
	}

	string? PeekValue(int offset)
	{
		return Peek(offset)?.Value;
	}

	Token? Peek(int offset)
	{
		int i = index;

		for (int seen = 0; ; seen++)
		{
			i = SkipTrivia(i);

			if (i >= tokens.Count)
				return null;

			if (seen == offset)
				return tokens[i];

			i++;
		}
	}

	int SkipTrivia(int start)
	{
		while (start < tokens.Count && IsTrivia(tokens[start]))
			start++;

		return start;
	}

	TokenRange? TakeOperator(params string[] operators)
	{
		SkipTrivia();

		foreach (string op in operators)
		{
			TokenRange? range = MatchOperatorAt(index, op);
			if (range is not null)
			{
				index += range.Value.Count;
				return range;
			}
		}

		return null;
	}

	bool IsOperator(string op)
	{
		SkipTrivia();
		return MatchOperatorAt(index, op) is not null;
	}

	TokenRange? OperatorAfterOffset(int offset, string op)
	{
		int i = index;

		for (int seen = 0; seen < offset; seen++)
		{
			i = SkipTrivia(i);
			if (i >= tokens.Count)
				return null;
			i++;
		}

		i = SkipTrivia(i);
		return MatchOperatorAt(i, op);
	}

	TokenRange? MatchOperatorAt(int start, string op)
	{
		if (start + op.Length > tokens.Count)
			return null;

		for (int i = 0; i < op.Length; i++)
		{
			Token token = tokens[start + i];
			if (token.Class != TokenClass.Symbol || token.Value.Length != 1 || token.Value[0] != op[i])
				return null;

			if (i > 0 && !AreAdjacent(tokens[start + i - 1], token))
				return null;
		}

		return new TokenRange(tokens, start, op.Length);
	}

	static bool AreAdjacent(Token left, Token right)
	{
		return left.LineNumber == right.LineNumber
			&& left.Column + left.Value.Length == right.Column;
	}

	bool IsExpressionBoundary()
	{
		return AtEnd || IsAny(";", ",", ")", "]", "}", ":");
	}

	bool IsStatementBoundary()
	{
		return AtEnd || IsAny("}", "case", "default");
	}

	void Report(Token? token, string message)
	{
		diagnostics.Add(new ParseDiagnostic(token?.Range, message));
	}

	void ReportAndAdvance(string message)
	{
		Token? token = Current;

		if (token is null)
		{
			Report(null, message);
			return;
		}

		diagnostics.Add(new ParseDiagnostic(token.Value.Range, message));
		index = token.Value.Index + 1;
	}
}
