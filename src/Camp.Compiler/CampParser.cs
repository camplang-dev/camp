using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed record ParseDiagnostic(TokenRange? Range, string Message);

public sealed class CampParser
{
	static readonly string[] TypeDeclarationKeywords = ["struct", "class", "interface", "params", "enum", "newtype"];
	static readonly string[] TypeDeclarationDeclarators = ["export", "public", "extern", "virtual", "sealed", "abstract", "fixed", "escaped"];
	static readonly string[] MemberDeclarators = ["export", "public", "extern", "static", "virtual", "override", "sealed", "abstract", "async", "fixed"];
	static readonly string[] ParameterDeclaratorKeywords = ["overload", "in", "out", "thrown"];
	static readonly string[] TypeDeclaratorKeywords = ["const", "volatile", "escaped", "scoped", "unscoped"];
	static readonly string[] StatementKeywords = ["if", "do", "while", "for", "else", "yield", "return", "continue", "break", "switch", "within", "try", "catch", "finally", "foreach", "delete", "goto", "throw"];

	readonly TokenSequence tokens;
	readonly List<ParseDiagnostic> diagnostics = [];
	int index;

	public CampParser(TokenSequence tokens)
	{
		this.tokens = tokens;
	}

	public IReadOnlyList<ParseDiagnostic> Diagnostics => diagnostics;

	public static CompilationUnitSyntax Parse(TokenSequence tokens, out IReadOnlyList<ParseDiagnostic> diagnostics)
	{
		CampParser parser = new(tokens);
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
		ImportExportDeclarationSyntax? importExport = ParseImportExportDeclaration();
		if (importExport is not null)
			return new CompilationUnitItemSyntax { ImportExportDeclaration = importExport };

		AliasDeclarationSyntax? aliasDeclaration = ParseAliasDeclaration();
		if (aliasDeclaration is not null)
			return new CompilationUnitItemSyntax { AliasDeclaration = aliasDeclaration };

		DeclarationSyntax? declaration = ParseDeclaration();
		if (declaration is not null)
			return new CompilationUnitItemSyntax { Declaration = declaration };

		return null;
	}

	ImportExportDeclarationSyntax? ParseImportExportDeclaration()
	{
		if (Is("using"))
			return ParseUsingImportExportDeclaration();

		if (Is("export") && PeekValue(1) == "as")
			return ParseExportImportExportDeclaration();

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

	AliasDeclarationSyntax? ParseAliasDeclaration()
	{
		int start = index;
		List<MemberDeclaratorSyntax> declarators = [];
		while (Is("export") || Is("public"))
			declarators.Add(new MemberDeclaratorSyntax { Keyword = Take() });

		if (!Is("alias"))
		{
			index = start;
			return null;
		}

		AliasDeclarationSyntax syntax = new()
		{
			Declarators = declarators.Count == 0 ? null : declarators,
			AliasKeyword = Expect("alias"),
			Identifier = ExpectIdentifier(),
			EqualsToken = Expect("="),
			TargetName = ParseQualifiedNamespace(),
			SemicolonToken = Expect(";")
		};
		return syntax;
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

		if (syntax.Keyword?.Value == "newtype" && Is("fn"))
		{
			TypeSyntax? callable = ParseType();
			if (callable is CallableTypeSyntax callableType)
			{
				while (IsPossibleTargetSpecIdentifier())
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
		Token? identifier = TakeIdentifier();
		if (identifier is null)
			return null;

		EnumValueSyntax syntax = new() { Identifier = identifier };

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

		int typeStart = index;
		TypeSyntax? type = ParseType(requireIdentifierAfterTerminalTargetSpec: true);
		if (type is not null && LooksLikeMemberName())
			syntax.Type = type;
		else
			index = typeStart;

		syntax.TildeToken = TakeIf("~");
		syntax.Identifier = ExpectIdentifier();

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
				syntax.ExpressionList = ParseExpressionList(")");
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

	TypeSyntax? ParseTypePrefix()
	{
		if (IsClass(TokenClass.AttributeIdentifier))
			return new AttributedTypeSyntax { Attribute = ParseAttribute(), Type = ParseType() };

		if (Is("async") && PeekValue(1) == "iter")
			return ParseIterType(asyncKeyword: Take(), storageKeyword: null);

		if (IsAny("fn", "delegate", "async", "once"))
		{
			CallableTypeSyntax callable = new()
			{
				CallableKeyword = Take(),
			};
			if (IsPossibleLeadingCallSpecIdentifier())
				callable.CallSpec = Take();
			if (IsPossibleTargetSpecIdentifier())
				callable.TargetSpec = Take();
			callable.ReturnType = ParseType();
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

		if (IsPossibleTargetSpecIdentifier())
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

		if ((syntax.Keyword?.Value is "scoped" or "unscoped") && Is("("))
		{
			syntax.OpenParenToken = Take();
			syntax.AnchorList = ParseIdentList(")");
			syntax.CloseParenToken = Expect(")");
		}

		return syntax;
	}

	bool IsPossibleTargetSpecIdentifier()
	{
		return IsIdentifier() && Current?.Value.StartsWith("_", StringComparison.Ordinal) == true;
	}

	bool IsPossiblePostfixTargetSpecIdentifier()
	{
		return IsPossibleTargetSpecIdentifier();
	}

	bool IsPossibleLeadingCallSpecIdentifier()
	{
		if (!IsIdentifier())
			return false;
		if (Current?.Value.StartsWith("_", StringComparison.Ordinal) == true)
			return true;
		if (IsReservedLeadingCallSpecToken(Current?.Value))
			return false;

		return ValueIsAny(PeekValue(1),
			"void", "bool", "string", "wstring", "astring", "byte", "sbyte", "ushort", "short",
			"uint", "int", "ulong", "long", "nuint", "nint", "float", "double", "char",
			"wchar", "achar", "uchar", "untyped", "fn", "delegate", "once", "async", "iter");
	}

	static bool IsReservedLeadingCallSpecToken(string? value)
	{
		return ValueIsAny(value,
			"abstract", "alias", "any", "as", "astring", "async", "auto", "bool", "break", "byte",
			"case", "catch", "char", "class", "const", "continue", "default", "delegate", "delete",
			"do", "double", "else", "enum", "escaped", "export", "extern", "false", "finally",
			"fixed", "float", "fn", "for", "foreach", "if", "implements", "in", "init", "int",
			"interface", "iter", "long", "new", "newtype", "nint", "null", "nuint", "once", "out",
			"override", "params", "public", "return", "sbyte", "scoped", "sealed", "short", "sizeof",
			"static", "string", "struct", "switch", "this", "thrown", "true", "try", "uchar", "uint",
			"ulong", "unscoped", "ushort", "untyped", "using", "virtual", "void", "volatile",
			"vtableof", "wchar", "while", "within", "wstring", "yield");
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
		if (within is not null && IsIdentifier() && ValueIsAny(PeekValue(1), ",", ")"))
			return new WithinParameterSyntax { WithinKeyword = within, Identifier = TakeIdentifier() };
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
		Token? keyword = IsAny(ParameterDeclaratorKeywords) ? Take() : null;

		if (attributes is null && keyword is null)
			return null;

		return new ParameterDeclaratorSyntax { Attributes = attributes, Keyword = keyword };
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

		if (IsIdentifier() && PeekValue(1) == ":")
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
		Token? fixedKeyword = TakeIf("fixed");
		if (Is("auto"))
		{
			DeclarationTargetSyntax syntax = new() { FixedKeyword = fixedKeyword, AutoKeyword = Take() };

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
			return null;
		}

		return new DeclarationTargetSyntax { FixedKeyword = fixedKeyword, Type = type, Identifier = TakeIdentifier() };
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
			syntax.DeleteKeyword = Expect("delete");
		}

		if (syntax.Prefixes.Count == 0)
		{
			syntax.Prefixes = null;

			if (syntax.FinallyKeyword is null && syntax.DeleteKeyword is null)
				return syntax.Expression;
		}

		return syntax;
	}

	UnaryPrefixSyntax? TryParseUnaryPrefix()
	{
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

		if (syntax.Parts.Count == 0)
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

		if (Is("within") || Is("init") || Is("new"))
			return TryParseConstructionExpression();

		if (Is("("))
			return ParseParenthesizedCastOrGroupedExpression();

		if (Is("["))
			return ParseArrayExpression();

		if (Is("{"))
			return ParseInitializerList();

		return ParseQualifiedNameExpression();
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

		Token? castKeyword = IsAny("params", "struct", "class")
			? Take()
			: null;

		TypeSyntax? type = null;
		if (castKeyword is null)
			type = ParseType();

		if ((castKeyword is null && type is null) || TakeIf(")") is not Token close || IsExpressionBoundary())
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
			Type = type,
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
				diagnostics.Add(new ParseDiagnostic(new TokenRange(tokens, start, 1), "Initializer assignments must start with '.'."));
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

		if (!Is("init") && !Is("new"))
		{
			index = start;
			return null;
		}

		syntax.Keyword = Take();

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

		if (IsIdentifier() && OperatorAfterOffset(1, "=>") is TokenRange singleArrow)
		{
			return new LambdaExpressionSyntax
			{
				Parameter = new LambdaParameterSyntax { Identifier = TakeIdentifier() },
				ArrowToken = TakeOperator("=>"),
				Body = ParseLambdaBody()
			};
		}

		if (!Is("("))
			return null;

		LambdaExpressionSyntax syntax = new()
		{
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
		else if (Is("within"))
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
