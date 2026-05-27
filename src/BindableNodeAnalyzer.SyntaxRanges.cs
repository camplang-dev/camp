using System;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	static TokenRange? GetNameRange(Definition definition)
	{
		return definition.SourceSyntax switch
		{
			TypeDeclarationSyntax syntax => syntax.Identifier?.Range,
			MemberDeclarationSyntax syntax => syntax.Identifier?.Range,
			EnumValueSyntax syntax => syntax.Identifier?.Range,
			_ => GetRange(definition.SourceSyntax)
		};
	}

	static TokenRange? GetNameRange(ParameterDefinition definition)
	{
		return definition.SourceSyntax switch
		{
			ValueParameterSyntax syntax => syntax.Identifier?.Range,
			WithinParameterSyntax syntax => syntax.Identifier?.Range,
			ThisParameterSyntax syntax => syntax.ThisKeyword?.Range,
			SizeOfParameterSyntax syntax => syntax.SizeOfKeyword?.Range,
			VTableOfParameterSyntax syntax => syntax.VTableOfKeyword?.Range,
			_ => GetRange(definition.SourceSyntax)
		};
	}

	static TokenRange? GetGenericParameterNameRange(SyntaxNode? syntax)
	{
		return syntax is GenericParameterSyntax generic ? generic.Identifier?.Range : GetRange(syntax);
	}

	static TokenRange? GetDeclarationTargetNameRange(SyntaxNode? syntax, string name)
	{
		if (syntax is not DeclarationTargetSyntax target)
			return GetRange(syntax);

		if (target.Identifier?.Value == name)
			return target.Identifier.Value.Range;

		if (target.AutoIdentifier?.Value == name)
			return target.AutoIdentifier.Value.Range;

		foreach (Token identifier in target.IdentifierList?.Identifiers ?? [])
		{
			if (identifier.Value == name)
				return identifier.Range;
		}

		return GetRange(syntax);
	}

	static TokenRange? GetLabelNameRange(SyntaxNode? syntax)
	{
		return syntax is LabelStatementSyntax label ? label.Identifier?.Range : GetRange(syntax);
	}

	static TokenRange? GetGotoTargetNameRange(SyntaxNode? syntax)
	{
		if (syntax is not KeywordStatementSyntax { Body: ExpressionStatementSyntax { Expression: QualifiedNameExpressionSyntax name } })
			return GetRange(syntax);

		return name.Identifier?.Range ?? GetRange(syntax);
	}

	static TokenRange? GetLambdaParameterNameRange(SyntaxNode? syntax)
	{
		return syntax is LambdaParameterSyntax parameter ? parameter.Identifier?.Range : GetRange(syntax);
	}

	static TokenRange? GetAliasRange(SyntaxNode? syntax)
	{
		return syntax is UsingImportExportDeclarationSyntax usingSyntax ? usingSyntax.Alias?.Range : GetRange(syntax);
	}

	static TokenRange? GetRange(SyntaxNode? syntax)
	{
		return syntax switch
		{
			null => null,
			CompilationUnitSyntax compilationUnit => compilationUnit.Items is [CompilationUnitItemSyntax first, ..] ? GetRange(first) : null,
			CompilationUnitItemSyntax item => GetRange(item.HeaderDirective) ?? GetRange(item.ImportExportDeclaration) ?? GetRange(item.Declaration),
			HeaderDirectiveSyntax directive => directive.Token?.Range,
			ImportExportDeclarationSyntax declaration => declaration.Keyword?.Range,
			QualifiedNamespaceSyntax qualifiedNamespace => qualifiedNamespace.Identifier?.Range,
			QualifierSyntax qualifier => qualifier.Identifier?.Range,
			TypeDeclarationSyntax declaration => declaration.Keyword?.Range ?? declaration.Identifier?.Range,
			AttributeSyntax attribute => attribute.AttributeIdentifier?.Range,
			TypeDeclarationDeclaratorSyntax declarator => declarator.Keyword?.Range,
			GenericParameterSyntax parameter => parameter.Identifier?.Range,
			TypeDeclarationScopeSyntax scope => scope.OpenBraceToken?.Range,
			DeclarationSyntax declaration => GetRange(declaration.TypeDeclaration) ?? GetRange(declaration.MemberDeclaration),
			MemberDeclarationSyntax declaration => declaration.Identifier?.Range ?? declaration.TildeToken?.Range,
			MemberDeclaratorSyntax declarator => declarator.Keyword?.Range,
			ValueParameterSyntax parameter => parameter.Identifier?.Range ?? GetRange(parameter.Type),
			WithinParameterSyntax parameter => parameter.Identifier?.Range ?? parameter.WithinKeyword?.Range,
			ThisParameterSyntax parameter => parameter.ThisKeyword?.Range,
			SizeOfParameterSyntax parameter => parameter.SizeOfKeyword?.Range,
			VTableOfParameterSyntax parameter => parameter.VTableOfKeyword?.Range,
			ParameterDeclaratorSyntax declarator => declarator.Keyword?.Range,
			TypeSyntax type => GetTypeRange(type),
			AssignmentSyntax assignment => assignment.EqualsToken?.Range,
			BlockMethodBodySyntax body => body.OpenBraceToken?.Range,
			ExpressionMethodBodySyntax body => body.ArrowToken,
			IdentListSyntax list => list.Identifiers is [Token first, ..] ? first.Range : null,
			GenericParameterListSyntax list => list.LessThanToken?.Range,
			UnderlyingTypeListSyntax list => list.Types is [TypeSyntax first, ..] ? GetRange(first) : null,
			ParameterListSyntax list => list.OpenParenToken?.Range,
			TypeListSyntax list => list.Types is [TypeSyntax first, ..] ? GetRange(first) : null,
			ExpressionListSyntax list => list.Expressions is [ExpressionSyntax first, ..] ? GetRange(first) : null,
			ArgumentSyntax argument => argument.Identifier?.Range ?? argument.OutKeyword?.Range ?? argument.CatchKeyword?.Range ?? argument.WithinKeyword?.Range,
			ExpressionSyntax expression => GetExpressionRange(expression),
			PostfixPartSyntax postfix => GetPostfixPartRange(postfix),
			_ => null
		};
	}

	static TokenRange? GetTypeRange(TypeSyntax type)
	{
		return type switch
		{
			CallableTypeSyntax callable => callable.CallableKeyword?.Range,
			AttributedTypeSyntax attributed => GetRange(attributed.Attribute) ?? GetRange(attributed.Type),
			ArrayTypeSyntax array => GetRange(array.ElementType) ?? array.OpenBracketToken?.Range,
			OptionalTypeSyntax optional => GetRange(optional.ElementType) ?? optional.QuestionToken?.Range,
			PointerTypeSyntax pointer => GetRange(pointer.ElementType) ?? pointer.StarToken?.Range,
			GenericTypeSyntax generic => GetRange(generic.Type) ?? generic.LessThanToken?.Range,
			IterTypeSyntax iter => iter.StorageKeyword?.Range ?? iter.IterKeyword?.Range,
			ParamsTypeSyntax grouped => grouped.ParamsKeyword?.Range,
			StructTypeSyntax materialized => materialized.StructKeyword?.Range,
			ThrownTypeSyntax thrown => thrown.ThrownKeyword?.Range,
			DeclaratorTypeSyntax declarator => GetRange(declarator.Declarator) ?? GetRange(declarator.Type),
			QualifiedNameTypeSyntax named => named.Identifier?.Range,
			_ => null
		};
	}

	static TokenRange? GetExpressionRange(ExpressionSyntax expression)
	{
		return expression switch
		{
			LiteralExpressionSyntax literal => literal.Literal?.Range,
			QualifiedNameExpressionSyntax name => name.Identifier?.Range,
			ThisExpressionSyntax thisExpression => thisExpression.ThisKeyword?.Range,
			DefaultExpressionSyntax defaultExpression => defaultExpression.DefaultKeyword?.Range,
			ParenthesizedExpressionSyntax parenthesized => parenthesized.OpenParenToken?.Range,
			GroupedExpressionSyntax grouped => grouped.OpenParenToken?.Range,
			ArrayExpressionSyntax array => array.OpenBracketToken?.Range,
			CastExpressionSyntax cast => cast.OpenParenToken?.Range,
			ConstructionExpressionSyntax construction => construction.WithinKeyword?.Range ?? construction.Keyword?.Range,
			SizeOfExpressionSyntax sizeOf => sizeOf.SizeOfKeyword?.Range,
			VTableOfExpressionSyntax vtableOf => vtableOf.VTableOfKeyword?.Range,
			InitializerListSyntax initializer => initializer.OpenBraceToken?.Range,
			CommaExpressionSyntax comma => comma.Expressions is [ExpressionSyntax first, ..] ? GetRange(first) : null,
			AssignmentExpressionSyntax assignment => GetRange(assignment.Left),
			ConditionalExpressionSyntax conditional => GetRange(conditional.Condition),
			RangeExpressionSyntax range => GetRange(range.Start) ?? range.DotDotToken,
			BinaryExpressionSyntax binary => GetRange(binary.FirstExpression),
			UnaryExpressionSyntax unary => unary.Prefixes is [UnaryPrefixSyntax first, ..] ? GetRange(first) : GetRange(unary.Expression),
			PostfixExpressionSyntax postfix => GetRange(postfix.Expression),
			LambdaExpressionSyntax lambda => lambda.ArrowToken,
			_ => null
		};
	}

	static TokenRange? GetPostfixPartRange(PostfixPartSyntax postfix)
	{
		return postfix switch
		{
			CallPostfixPartSyntax call => call.OpenParenToken?.Range,
			IndexPostfixPartSyntax index => index.OpenBracketToken?.Range,
			MemberPostfixPartSyntax member => member.Identifier?.Range ?? member.DotToken?.Range,
			NamelessIndexerPostfixPartSyntax indexer => indexer.OpenBracketToken?.Range ?? indexer.DotToken?.Range,
			GenericPostfixPartSyntax generic => generic.LessThanToken?.Range,
			PostfixOperatorPartSyntax op => op.Operator,
			_ => null
		};
	}
}
