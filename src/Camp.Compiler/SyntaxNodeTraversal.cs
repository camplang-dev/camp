using System.Collections.Generic;

namespace Camp.Compiler;

public static class SyntaxNodeTraversal
{
	public static IEnumerable<SyntaxNode> Children(SyntaxNode node)
	{
		switch (node)
		{
			case CompilationUnitSyntax syntax:
				foreach (SyntaxNode child in syntax.Items ?? []) yield return child;
				break;
			case CompilationUnitItemSyntax syntax:
				if (syntax.ImportExportDeclaration is not null) yield return syntax.ImportExportDeclaration;
				if (syntax.FileMetadataAttribute is not null) yield return syntax.FileMetadataAttribute;
				if (syntax.NamespaceBlock is not null) yield return syntax.NamespaceBlock;
				if (syntax.AliasDeclaration is not null) yield return syntax.AliasDeclaration;
				if (syntax.Declaration is not null) yield return syntax.Declaration;
				break;
			case FileMetadataAttributeSyntax syntax:
				if (syntax.Attribute is not null) yield return syntax.Attribute;
				break;
			case NamespaceBlockSyntax syntax:
				if (syntax.QualifiedNamespace is not null) yield return syntax.QualifiedNamespace;
				foreach (SyntaxNode child in syntax.Items ?? []) yield return child;
				break;
			case UsingImportExportDeclarationSyntax syntax:
				if (syntax.QualifiedNamespace is not null) yield return syntax.QualifiedNamespace;
				if (syntax.SelectedIdentifiers is not null) yield return syntax.SelectedIdentifiers;
				break;
			case ExportImportExportDeclarationSyntax syntax:
				if (syntax.QualifiedNamespace is not null) yield return syntax.QualifiedNamespace;
				break;
			case ExportProjectionDeclarationSyntax syntax:
				if (syntax.TargetName is not null) yield return syntax.TargetName;
				if (syntax.InterfaceList is not null) yield return syntax.InterfaceList;
				if (syntax.MemberBlock is not null) yield return syntax.MemberBlock;
				break;
			case ExportProjectionMemberBlockSyntax syntax:
				foreach (SyntaxNode child in syntax.Members ?? []) yield return child;
				break;
			case QualifiedNamespaceSyntax syntax:
				foreach (SyntaxNode child in syntax.Qualifiers ?? []) yield return child;
				break;
			case AliasDeclarationSyntax syntax:
				foreach (SyntaxNode child in syntax.Attributes ?? []) yield return child;
				foreach (SyntaxNode child in syntax.Declarators ?? []) yield return child;
				if (syntax.TargetName is not null) yield return syntax.TargetName;
				break;
			case TypeDeclarationSyntax syntax:
				foreach (SyntaxNode child in syntax.Attributes ?? []) yield return child;
				foreach (SyntaxNode child in syntax.Declarators ?? []) yield return child;
				if (syntax.Type is not null) yield return syntax.Type;
				if (syntax.GenericParameterList is not null) yield return syntax.GenericParameterList;
				if (syntax.ParameterList is not null) yield return syntax.ParameterList;
				if (syntax.UnderlyingTypeList is not null) yield return syntax.UnderlyingTypeList;
				if (syntax.Scope is not null) yield return syntax.Scope;
				break;
			case AttributeSyntax syntax:
				if (syntax.ExpressionList is not null) yield return syntax.ExpressionList;
				if (syntax.ArgumentList is not null) yield return syntax.ArgumentList;
				break;
			case GenericParameterSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				break;
			case TypeDeclarationScopeSyntax syntax:
				if (syntax.EnumValueList is not null) yield return syntax.EnumValueList;
				foreach (SyntaxNode child in syntax.Declarations ?? []) yield return child;
				break;
			case DeclarationSyntax syntax:
				if (syntax.TypeDeclaration is not null) yield return syntax.TypeDeclaration;
				if (syntax.MemberDeclaration is not null) yield return syntax.MemberDeclaration;
				break;
			case MemberDeclarationSyntax syntax:
				foreach (SyntaxNode child in syntax.Attributes ?? []) yield return child;
				foreach (SyntaxNode child in syntax.Declarators ?? []) yield return child;
				if (syntax.Type is not null) yield return syntax.Type;
				if (syntax.OutOfScopeOwnerType is not null) yield return syntax.OutOfScopeOwnerType;
				if (syntax.GenericParameterList is not null) yield return syntax.GenericParameterList;
				if (syntax.ParameterList is not null) yield return syntax.ParameterList;
				if (syntax.CallableAscriptionType is not null) yield return syntax.CallableAscriptionType;
				if (syntax.MethodBody is not null) yield return syntax.MethodBody;
				if (syntax.Assignment is not null) yield return syntax.Assignment;
				break;
			case ValueParameterSyntax syntax:
				foreach (SyntaxNode child in syntax.Declarators ?? []) yield return child;
				if (syntax.Type is not null) yield return syntax.Type;
				if (syntax.DefaultValue is not null) yield return syntax.DefaultValue;
				break;
			case ThisParameterSyntax syntax:
				foreach (SyntaxNode child in syntax.Declarators ?? []) yield return child;
				break;
			case SizeOfParameterSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				break;
			case NameOfParameterSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				break;
			case VTableOfParameterSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				if (syntax.InterfaceType is not null) yield return syntax.InterfaceType;
				break;
			case ParameterDeclaratorSyntax syntax:
				foreach (SyntaxNode child in syntax.Attributes ?? []) yield return child;
				break;
			case CallableTypeSyntax syntax:
				if (syntax.ReturnType is not null) yield return syntax.ReturnType;
				if (syntax.ParameterList is not null) yield return syntax.ParameterList;
				break;
			case PrepReturnTypeSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				break;
			case AttributedTypeSyntax syntax:
				if (syntax.Attribute is not null) yield return syntax.Attribute;
				if (syntax.Type is not null) yield return syntax.Type;
				break;
			case ArrayTypeSyntax syntax:
				if (syntax.ElementType is not null) yield return syntax.ElementType;
				if (syntax.Length is not null) yield return syntax.Length;
				break;
			case OptionalTypeSyntax syntax:
				if (syntax.ElementType is not null) yield return syntax.ElementType;
				break;
			case PointerTypeSyntax syntax:
				if (syntax.ElementType is not null) yield return syntax.ElementType;
				break;
			case GenericTypeSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				if (syntax.TypeArgumentList is not null) yield return syntax.TypeArgumentList;
				break;
			case IterTypeSyntax syntax:
				if (syntax.ElementType is not null) yield return syntax.ElementType;
				if (syntax.ParameterList is not null) yield return syntax.ParameterList;
				break;
			case ParamsTypeSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				break;
			case StructTypeSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				break;
			case ThrownTypeSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				break;
			case DeclaratorTypeSyntax syntax:
				if (syntax.Declarator is not null) yield return syntax.Declarator;
				if (syntax.Type is not null) yield return syntax.Type;
				break;
			case TargetTypeSpecTypeSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				break;
			case QualifiedNameTypeSyntax syntax:
				foreach (SyntaxNode child in syntax.Qualifiers ?? []) yield return child;
				break;
			case TypeDeclaratorSyntax syntax:
				if (syntax.AnchorList is not null) yield return syntax.AnchorList;
				break;
			case AssignmentSyntax syntax:
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case BlockMethodBodySyntax syntax:
				foreach (SyntaxNode child in syntax.Statements ?? []) yield return child;
				break;
			case ExpressionMethodBodySyntax syntax:
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case BlockStatementSyntax syntax:
				foreach (SyntaxNode child in syntax.Statements ?? []) yield return child;
				break;
			case KeywordStatementSyntax syntax:
				if (syntax.Condition is not null) yield return syntax.Condition;
				if (syntax.Body is not null) yield return syntax.Body;
				foreach (SyntaxNode child in syntax.BodyStatements ?? []) yield return child;
				break;
			case CaseStatementSyntax syntax:
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case DeclarationStatementStatementSyntax syntax:
				if (syntax.DeclarationStatement is not null) yield return syntax.DeclarationStatement;
				break;
			case ExpressionStatementSyntax syntax:
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case DeclarationTargetSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				if (syntax.IdentifierList is not null) yield return syntax.IdentifierList;
				break;
			case DeclarationStatementSyntax syntax:
				if (syntax.Target is not null) yield return syntax.Target;
				if (syntax.Assignment is not null) yield return syntax.Assignment;
				break;
			case IterationStatementConditionSyntax syntax:
				if (syntax.Target is not null) yield return syntax.Target;
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case ClauseStatementConditionSyntax syntax:
				if (syntax.DeclarationStatement is not null) yield return syntax.DeclarationStatement;
				foreach (SyntaxNode child in syntax.Clauses ?? []) yield return child;
				break;
			case StatementConditionClauseSyntax syntax:
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case GenericParameterListSyntax syntax:
				foreach (SyntaxNode child in syntax.Parameters ?? []) yield return child;
				break;
			case UnderlyingTypeListSyntax syntax:
				foreach (SyntaxNode child in syntax.Types ?? []) yield return child;
				break;
			case EnumValueListSyntax syntax:
				foreach (SyntaxNode child in syntax.Values ?? []) yield return child;
				break;
			case ParameterListSyntax syntax:
				foreach (SyntaxNode child in syntax.Parameters ?? []) yield return child;
				break;
			case TypeListSyntax syntax:
				foreach (SyntaxNode child in syntax.Types ?? []) yield return child;
				break;
			case ExpressionListSyntax syntax:
				foreach (SyntaxNode child in syntax.Expressions ?? []) yield return child;
				break;
			case AssignmentExpressionListSyntax syntax:
				foreach (SyntaxNode child in syntax.Expressions ?? []) yield return child;
				break;
			case CommaExpressionSyntax syntax:
				foreach (SyntaxNode child in syntax.Expressions ?? []) yield return child;
				break;
			case AssignmentExpressionSyntax syntax:
				if (syntax.Left is not null) yield return syntax.Left;
				if (syntax.Operator is not null) yield return syntax.Operator;
				if (syntax.Right is not null) yield return syntax.Right;
				break;
			case ConditionalExpressionSyntax syntax:
				if (syntax.Condition is not null) yield return syntax.Condition;
				if (syntax.WhenTrue is not null) yield return syntax.WhenTrue;
				if (syntax.WhenFalse is not null) yield return syntax.WhenFalse;
				break;
			case RangeExpressionSyntax syntax:
				if (syntax.Start is not null) yield return syntax.Start;
				if (syntax.End is not null) yield return syntax.End;
				break;
			case BinaryExpressionSyntax syntax:
				if (syntax.FirstExpression is not null) yield return syntax.FirstExpression;
				foreach (SyntaxNode child in syntax.Parts ?? []) yield return child;
				break;
			case BinaryExpressionPartSyntax syntax:
				if (syntax.Operator is not null) yield return syntax.Operator;
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case UnaryExpressionSyntax syntax:
				foreach (SyntaxNode child in syntax.Prefixes ?? []) yield return child;
				if (syntax.Expression is not null) yield return syntax.Expression;
				if (syntax.FinallyArgumentList is not null) yield return syntax.FinallyArgumentList;
				break;
			case UnaryPrefixSyntax syntax:
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case PostfixExpressionSyntax syntax:
				if (syntax.Expression is not null) yield return syntax.Expression;
				foreach (SyntaxNode child in syntax.Parts ?? []) yield return child;
				break;
			case CallPostfixPartSyntax syntax:
				if (syntax.ArgumentList is not null) yield return syntax.ArgumentList;
				break;
			case IndexPostfixPartSyntax syntax:
				if (syntax.ArgumentList is not null) yield return syntax.ArgumentList;
				break;
			case NamelessIndexerPostfixPartSyntax syntax:
				if (syntax.ArgumentList is not null) yield return syntax.ArgumentList;
				break;
			case GenericPostfixPartSyntax syntax:
				if (syntax.TypeArgumentList is not null) yield return syntax.TypeArgumentList;
				break;
			case QualifiedNameExpressionSyntax syntax:
				foreach (SyntaxNode child in syntax.Qualifiers ?? []) yield return child;
				break;
			case InterpolatedStringExpressionSyntax syntax:
				if (syntax.AllocatorExpression is not null) yield return syntax.AllocatorExpression;
				foreach (SyntaxNode child in syntax.Segments) yield return child;
				break;
			case InterpolatedStringExpressionSegmentSyntax syntax:
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case ParenthesizedExpressionSyntax syntax:
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case GroupedExpressionSyntax syntax:
				if (syntax.ItemList is not null) yield return syntax.ItemList;
				break;
			case ArrayExpressionSyntax syntax:
				if (syntax.ExpressionList is not null) yield return syntax.ExpressionList;
				break;
			case CastExpressionSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				if (syntax.LifetimeDeclarator is not null) yield return syntax.LifetimeDeclarator;
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case ConstructionExpressionSyntax syntax:
				if (syntax.AllocatorExpression is not null) yield return syntax.AllocatorExpression;
				if (syntax.Type is not null) yield return syntax.Type;
				if (syntax.ArgumentList is not null) yield return syntax.ArgumentList;
				if (syntax.ElementCount is not null) yield return syntax.ElementCount;
				if (syntax.InitializerList is not null) yield return syntax.InitializerList;
				break;
			case SizeOfExpressionSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				break;
			case VTableOfExpressionSyntax syntax:
				if (syntax.Type is not null) yield return syntax.Type;
				if (syntax.InterfaceType is not null) yield return syntax.InterfaceType;
				break;
			case InitializerListSyntax syntax:
				if (syntax.ItemList is not null) yield return syntax.ItemList;
				break;
			case InitializerItemSyntax syntax:
				if (syntax.Target is not null) yield return syntax.Target;
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case InitializerTargetSyntax syntax:
				foreach (SyntaxNode child in syntax.Parts ?? []) yield return child;
				break;
			case InitializerTargetPartSyntax syntax:
				if (syntax.ArgumentList is not null) yield return syntax.ArgumentList;
				break;
			case LambdaExpressionSyntax syntax:
				if (syntax.AllocatorExpression is not null) yield return syntax.AllocatorExpression;
				if (syntax.Parameter is not null) yield return syntax.Parameter;
				if (syntax.ParameterList is not null) yield return syntax.ParameterList;
				if (syntax.Body is not null) yield return syntax.Body;
				break;
			case LambdaParameterSyntax syntax:
				if (syntax.Parameter is not null) yield return syntax.Parameter;
				break;
			case LambdaBodySyntax syntax:
				if (syntax.Expression is not null) yield return syntax.Expression;
				if (syntax.MethodBody is not null) yield return syntax.MethodBody;
				break;
			case GroupedExpressionItemSyntax syntax:
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case ArgumentSyntax syntax:
				if (syntax.DeclarationTarget is not null) yield return syntax.DeclarationTarget;
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
			case GroupedExpressionItemListSyntax syntax:
				foreach (SyntaxNode child in syntax.Items ?? []) yield return child;
				break;
			case InitializerItemListSyntax syntax:
				foreach (SyntaxNode child in syntax.Items ?? []) yield return child;
				break;
			case LambdaParameterListSyntax syntax:
				foreach (SyntaxNode child in syntax.Parameters ?? []) yield return child;
				break;
			case ArgumentListSyntax syntax:
				foreach (SyntaxNode child in syntax.Arguments ?? []) yield return child;
				break;
			case EnumValueSyntax syntax:
				foreach (SyntaxNode child in syntax.Attributes ?? []) yield return child;
				if (syntax.Expression is not null) yield return syntax.Expression;
				break;
		}
	}

	public static IEnumerable<Token> Tokens(SyntaxNode node)
	{
		switch (node)
		{
			case CompilationUnitSyntax syntax:
				foreach (SyntaxNode child in syntax.Items ?? []) foreach (Token token in Tokens(child)) yield return token;
				break;
			case CompilationUnitItemSyntax syntax:
				if (syntax.ImportExportDeclaration is not null) foreach (Token token in Tokens(syntax.ImportExportDeclaration)) yield return token;
				if (syntax.FileMetadataAttribute is not null) foreach (Token token in Tokens(syntax.FileMetadataAttribute)) yield return token;
				if (syntax.NamespaceBlock is not null) foreach (Token token in Tokens(syntax.NamespaceBlock)) yield return token;
				if (syntax.AliasDeclaration is not null) foreach (Token token in Tokens(syntax.AliasDeclaration)) yield return token;
				if (syntax.Declaration is not null) foreach (Token token in Tokens(syntax.Declaration)) yield return token;
				break;
			case FileMetadataAttributeSyntax syntax:
				if (syntax.Attribute is not null) foreach (Token token in Tokens(syntax.Attribute)) yield return token;
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				break;
			case NamespaceBlockSyntax syntax:
				foreach (Token token in Tokens(syntax.Keyword)) yield return token;
				if (syntax.QualifiedNamespace is not null) foreach (Token token in Tokens(syntax.QualifiedNamespace)) yield return token;
				foreach (Token token in Tokens(syntax.OpenBraceToken)) yield return token;
				foreach (SyntaxNode child in syntax.Items ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.CloseBraceToken)) yield return token;
				break;
			case UsingImportExportDeclarationSyntax syntax:
				if (syntax.QualifiedNamespace is not null) foreach (Token token in Tokens(syntax.QualifiedNamespace)) yield return token;
				foreach (Token token in Tokens(syntax.AsKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.Alias)) yield return token;
				foreach (Token token in Tokens(syntax.OpenBraceToken)) yield return token;
				if (syntax.SelectedIdentifiers is not null) foreach (Token token in Tokens(syntax.SelectedIdentifiers)) yield return token;
				foreach (Token token in Tokens(syntax.CloseBraceToken)) yield return token;
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				break;
			case ExportImportExportDeclarationSyntax syntax:
				foreach (Token token in Tokens(syntax.AsKeyword)) yield return token;
				if (syntax.QualifiedNamespace is not null) foreach (Token token in Tokens(syntax.QualifiedNamespace)) yield return token;
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				break;
			case ExportProjectionDeclarationSyntax syntax:
				if (syntax.TargetName is not null) foreach (Token token in Tokens(syntax.TargetName)) yield return token;
				foreach (Token token in Tokens(syntax.ColonToken)) yield return token;
				if (syntax.InterfaceList is not null) foreach (Token token in Tokens(syntax.InterfaceList)) yield return token;
				if (syntax.MemberBlock is not null) foreach (Token token in Tokens(syntax.MemberBlock)) yield return token;
				foreach (Token token in Tokens(syntax.AsKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.Alias)) yield return token;
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				break;
			case ExportProjectionMemberBlockSyntax syntax:
				foreach (Token token in Tokens(syntax.OpenBraceToken)) yield return token;
				foreach (SyntaxNode child in syntax.Members ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				foreach (Token token in Tokens(syntax.CloseBraceToken)) yield return token;
				break;
			case ExportProjectionMemberSyntax syntax:
				foreach (Token token in Tokens(syntax.TildeToken)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				foreach (Token token in Tokens(syntax.AsKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.Alias)) yield return token;
				break;
			case QualifiedNamespaceSyntax syntax:
				foreach (SyntaxNode child in syntax.Qualifiers ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				break;
			case QualifierSyntax syntax:
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				foreach (Token token in Tokens(syntax.ColonColonToken)) yield return token;
				break;
			case AliasDeclarationSyntax syntax:
				foreach (SyntaxNode child in syntax.Attributes ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (SyntaxNode child in syntax.Declarators ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.AliasKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				foreach (Token token in Tokens(syntax.EqualsToken)) yield return token;
				if (syntax.TargetName is not null) foreach (Token token in Tokens(syntax.TargetName)) yield return token;
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				break;
			case TypeDeclarationSyntax syntax:
				foreach (SyntaxNode child in syntax.Attributes ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (SyntaxNode child in syntax.Declarators ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.Keyword)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.CallSpec)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				if (syntax.GenericParameterList is not null) foreach (Token token in Tokens(syntax.GenericParameterList)) yield return token;
				if (syntax.ParameterList is not null) foreach (Token token in Tokens(syntax.ParameterList)) yield return token;
				foreach (Token token in Tokens(syntax.ColonToken)) yield return token;
				if (syntax.UnderlyingTypeList is not null) foreach (Token token in Tokens(syntax.UnderlyingTypeList)) yield return token;
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				if (syntax.Scope is not null) foreach (Token token in Tokens(syntax.Scope)) yield return token;
				break;
			case AttributeSyntax syntax:
				foreach (Token token in Tokens(syntax.AttributeIdentifier)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.ExpressionList is not null) foreach (Token token in Tokens(syntax.ExpressionList)) yield return token;
				if (syntax.ArgumentList is not null) foreach (Token token in Tokens(syntax.ArgumentList)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case TypeDeclarationDeclaratorSyntax syntax:
				foreach (Token token in Tokens(syntax.Keyword)) yield return token;
				break;
			case GenericParameterSyntax syntax:
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				foreach (Token token in Tokens(syntax.ColonToken)) yield return token;
				foreach (Token token in Tokens(syntax.ImplementsKeyword)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				break;
			case TypeDeclarationScopeSyntax syntax:
				foreach (Token token in Tokens(syntax.OpenBraceToken)) yield return token;
				if (syntax.EnumValueList is not null) foreach (Token token in Tokens(syntax.EnumValueList)) yield return token;
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				foreach (SyntaxNode child in syntax.Declarations ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.CloseBraceToken)) yield return token;
				break;
			case DeclarationSyntax syntax:
				if (syntax.TypeDeclaration is not null) foreach (Token token in Tokens(syntax.TypeDeclaration)) yield return token;
				if (syntax.MemberDeclaration is not null) foreach (Token token in Tokens(syntax.MemberDeclaration)) yield return token;
				break;
			case MemberDeclarationSyntax syntax:
				foreach (SyntaxNode child in syntax.Attributes ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (SyntaxNode child in syntax.Declarators ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.CallSpec)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				if (syntax.OutOfScopeOwnerType is not null) foreach (Token token in Tokens(syntax.OutOfScopeOwnerType)) yield return token;
				foreach (Token token in Tokens(syntax.OutOfScopeDotToken)) yield return token;
				foreach (Token token in Tokens(syntax.TildeToken)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				if (syntax.GenericParameterList is not null) foreach (Token token in Tokens(syntax.GenericParameterList)) yield return token;
				if (syntax.ParameterList is not null) foreach (Token token in Tokens(syntax.ParameterList)) yield return token;
				foreach (Token token in Tokens(syntax.CallableAscriptionColonToken)) yield return token;
				if (syntax.CallableAscriptionType is not null) foreach (Token token in Tokens(syntax.CallableAscriptionType)) yield return token;
				foreach (Token token in Tokens(syntax.CallableAscriptionDotToken)) yield return token;
				foreach (Token token in Tokens(syntax.CallableAscriptionMemberName)) yield return token;
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				if (syntax.MethodBody is not null) foreach (Token token in Tokens(syntax.MethodBody)) yield return token;
				if (syntax.Assignment is not null) foreach (Token token in Tokens(syntax.Assignment)) yield return token;
				break;
			case MemberDeclaratorSyntax syntax:
				foreach (Token token in Tokens(syntax.Keyword)) yield return token;
				break;
			case ValueParameterSyntax syntax:
				foreach (Token token in Tokens(syntax.WithinKeyword)) yield return token;
				foreach (SyntaxNode child in syntax.Declarators ?? []) foreach (Token token in Tokens(child)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				foreach (Token token in Tokens(syntax.EqualsToken)) yield return token;
				if (syntax.DefaultValue is not null) foreach (Token token in Tokens(syntax.DefaultValue)) yield return token;
				break;
			case WithinParameterSyntax syntax:
				foreach (Token token in Tokens(syntax.WithinKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.LifetimeKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				break;
			case ThisParameterSyntax syntax:
				foreach (SyntaxNode child in syntax.Declarators ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.ThisKeyword)) yield return token;
				break;
			case SizeOfParameterSyntax syntax:
				foreach (Token token in Tokens(syntax.SizeOfKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case NameOfParameterSyntax syntax:
				foreach (Token token in Tokens(syntax.NameOfKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case VTableOfParameterSyntax syntax:
				foreach (Token token in Tokens(syntax.VTableOfKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.ColonToken)) yield return token;
				if (syntax.InterfaceType is not null) foreach (Token token in Tokens(syntax.InterfaceType)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case ParameterDeclaratorSyntax syntax:
				foreach (SyntaxNode child in syntax.Attributes ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.Keyword)) yield return token;
				break;
			case ThisTypeSyntax syntax:
				foreach (Token token in Tokens(syntax.ThisKeyword)) yield return token;
				break;
			case CallableTypeSyntax syntax:
				foreach (Token token in Tokens(syntax.CallableKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.CallSpec)) yield return token;
				foreach (Token token in Tokens(syntax.TargetSpec)) yield return token;
				if (syntax.ReturnType is not null) foreach (Token token in Tokens(syntax.ReturnType)) yield return token;
				if (syntax.ParameterList is not null) foreach (Token token in Tokens(syntax.ParameterList)) yield return token;
				break;
			case PrepReturnTypeSyntax syntax:
				foreach (Token token in Tokens(syntax.PrepKeyword)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				break;
			case RawFunctionPointerTypeSyntax syntax:
				foreach (Token token in Tokens(syntax.FnKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.StarToken)) yield return token;
				break;
			case AttributedTypeSyntax syntax:
				if (syntax.Attribute is not null) foreach (Token token in Tokens(syntax.Attribute)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				break;
			case ArrayTypeSyntax syntax:
				if (syntax.ElementType is not null) foreach (Token token in Tokens(syntax.ElementType)) yield return token;
				foreach (Token token in Tokens(syntax.OpenBracketToken)) yield return token;
				if (syntax.Length is not null) foreach (Token token in Tokens(syntax.Length)) yield return token;
				foreach (Token token in Tokens(syntax.CloseBracketToken)) yield return token;
				break;
			case OptionalTypeSyntax syntax:
				if (syntax.ElementType is not null) foreach (Token token in Tokens(syntax.ElementType)) yield return token;
				foreach (Token token in Tokens(syntax.QuestionToken)) yield return token;
				break;
			case PointerTypeSyntax syntax:
				if (syntax.ElementType is not null) foreach (Token token in Tokens(syntax.ElementType)) yield return token;
				foreach (Token token in Tokens(syntax.StarToken)) yield return token;
				break;
			case GenericTypeSyntax syntax:
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.LessThanToken)) yield return token;
				if (syntax.TypeArgumentList is not null) foreach (Token token in Tokens(syntax.TypeArgumentList)) yield return token;
				foreach (Token token in Tokens(syntax.GreaterThanToken)) yield return token;
				break;
			case IterTypeSyntax syntax:
				foreach (Token token in Tokens(syntax.AsyncKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.StorageKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.IterKeyword)) yield return token;
				if (syntax.ElementType is not null) foreach (Token token in Tokens(syntax.ElementType)) yield return token;
				if (syntax.ParameterList is not null) foreach (Token token in Tokens(syntax.ParameterList)) yield return token;
				break;
			case ParamsTypeSyntax syntax:
				foreach (Token token in Tokens(syntax.ParamsKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case StructTypeSyntax syntax:
				foreach (Token token in Tokens(syntax.StructKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case ThrownTypeSyntax syntax:
				foreach (Token token in Tokens(syntax.ThrownKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case DeclaratorTypeSyntax syntax:
				if (syntax.Declarator is not null) foreach (Token token in Tokens(syntax.Declarator)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				break;
			case TargetTypeSpecTypeSyntax syntax:
				foreach (Token token in Tokens(syntax.Specifier)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				break;
			case QualifiedNameTypeSyntax syntax:
				foreach (SyntaxNode child in syntax.Qualifiers ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				break;
			case TypeDeclaratorSyntax syntax:
				foreach (Token token in Tokens(syntax.Keyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.AnchorList is not null) foreach (Token token in Tokens(syntax.AnchorList)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case AssignmentSyntax syntax:
				foreach (Token token in Tokens(syntax.EqualsToken)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				break;
			case BlockMethodBodySyntax syntax:
				foreach (Token token in Tokens(syntax.OpenBraceToken)) yield return token;
				foreach (SyntaxNode child in syntax.Statements ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.CloseBraceToken)) yield return token;
				break;
			case ExpressionMethodBodySyntax syntax:
				foreach (Token token in Tokens(syntax.ArrowToken)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				break;
			case BlockStatementSyntax syntax:
				foreach (Token token in Tokens(syntax.OpenBraceToken)) yield return token;
				foreach (SyntaxNode child in syntax.Statements ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.CloseBraceToken)) yield return token;
				break;
			case KeywordStatementSyntax syntax:
				foreach (Token token in Tokens(syntax.Keyword)) yield return token;
				foreach (Token token in Tokens(syntax.SpecialKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.Condition is not null) foreach (Token token in Tokens(syntax.Condition)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				if (syntax.Body is not null) foreach (Token token in Tokens(syntax.Body)) yield return token;
				foreach (Token token in Tokens(syntax.BodyOpenBraceToken)) yield return token;
				foreach (SyntaxNode child in syntax.BodyStatements ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.BodyCloseBraceToken)) yield return token;
				break;
			case CaseStatementSyntax syntax:
				foreach (Token token in Tokens(syntax.CaseKeyword)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				foreach (Token token in Tokens(syntax.ColonToken)) yield return token;
				break;
			case DefaultStatementSyntax syntax:
				foreach (Token token in Tokens(syntax.DefaultKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.ColonToken)) yield return token;
				break;
			case LabelStatementSyntax syntax:
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				foreach (Token token in Tokens(syntax.ColonToken)) yield return token;
				break;
			case DeclarationStatementStatementSyntax syntax:
				if (syntax.DeclarationStatement is not null) foreach (Token token in Tokens(syntax.DeclarationStatement)) yield return token;
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				break;
			case ExpressionStatementSyntax syntax:
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				break;
			case EmptyStatementSyntax syntax:
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				break;
			case DeclarationTargetSyntax syntax:
				foreach (Token token in Tokens(syntax.FixedKeyword)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				foreach (Token token in Tokens(syntax.AutoKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.AutoIdentifier)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.IdentifierList is not null) foreach (Token token in Tokens(syntax.IdentifierList)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case DeclarationStatementSyntax syntax:
				if (syntax.Target is not null) foreach (Token token in Tokens(syntax.Target)) yield return token;
				if (syntax.Assignment is not null) foreach (Token token in Tokens(syntax.Assignment)) yield return token;
				break;
			case IterationStatementConditionSyntax syntax:
				if (syntax.Target is not null) foreach (Token token in Tokens(syntax.Target)) yield return token;
				foreach (Token token in Tokens(syntax.InKeyword)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				break;
			case ClauseStatementConditionSyntax syntax:
				if (syntax.DeclarationStatement is not null) foreach (Token token in Tokens(syntax.DeclarationStatement)) yield return token;
				foreach (SyntaxNode child in syntax.Clauses ?? []) foreach (Token token in Tokens(child)) yield return token;
				break;
			case StatementConditionClauseSyntax syntax:
				foreach (Token token in Tokens(syntax.SemicolonToken)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				break;
			case IdentListSyntax syntax:
				foreach (Token token in syntax.Identifiers ?? []) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				break;
			case GenericParameterListSyntax syntax:
				foreach (Token token in Tokens(syntax.LessThanToken)) yield return token;
				foreach (SyntaxNode child in syntax.Parameters ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				foreach (Token token in Tokens(syntax.GreaterThanToken)) yield return token;
				break;
			case UnderlyingTypeListSyntax syntax:
				foreach (SyntaxNode child in syntax.Types ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				break;
			case EnumValueListSyntax syntax:
				foreach (SyntaxNode child in syntax.Values ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				break;
			case ParameterListSyntax syntax:
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				foreach (SyntaxNode child in syntax.Parameters ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case TypeListSyntax syntax:
				foreach (SyntaxNode child in syntax.Types ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				break;
			case ExpressionListSyntax syntax:
				foreach (SyntaxNode child in syntax.Expressions ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				break;
			case AssignmentExpressionListSyntax syntax:
				foreach (SyntaxNode child in syntax.Expressions ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				break;
			case CommaExpressionSyntax syntax:
				foreach (SyntaxNode child in syntax.Expressions ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				break;
			case AssignmentExpressionSyntax syntax:
				if (syntax.Left is not null) foreach (Token token in Tokens(syntax.Left)) yield return token;
				if (syntax.Operator is not null) foreach (Token token in Tokens(syntax.Operator)) yield return token;
				if (syntax.Right is not null) foreach (Token token in Tokens(syntax.Right)) yield return token;
				break;
			case ConditionalExpressionSyntax syntax:
				if (syntax.Condition is not null) foreach (Token token in Tokens(syntax.Condition)) yield return token;
				foreach (Token token in Tokens(syntax.QuestionToken)) yield return token;
				if (syntax.WhenTrue is not null) foreach (Token token in Tokens(syntax.WhenTrue)) yield return token;
				foreach (Token token in Tokens(syntax.ColonToken)) yield return token;
				if (syntax.WhenFalse is not null) foreach (Token token in Tokens(syntax.WhenFalse)) yield return token;
				break;
			case RangeExpressionSyntax syntax:
				if (syntax.Start is not null) foreach (Token token in Tokens(syntax.Start)) yield return token;
				foreach (Token token in Tokens(syntax.DotDotToken)) yield return token;
				if (syntax.End is not null) foreach (Token token in Tokens(syntax.End)) yield return token;
				break;
			case BinaryExpressionSyntax syntax:
				if (syntax.FirstExpression is not null) foreach (Token token in Tokens(syntax.FirstExpression)) yield return token;
				foreach (SyntaxNode child in syntax.Parts ?? []) foreach (Token token in Tokens(child)) yield return token;
				break;
			case BinaryExpressionPartSyntax syntax:
				if (syntax.Operator is not null) foreach (Token token in Tokens(syntax.Operator)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				break;
			case UnaryExpressionSyntax syntax:
				foreach (SyntaxNode child in syntax.Prefixes ?? []) foreach (Token token in Tokens(child)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				foreach (Token token in Tokens(syntax.FinallyKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.DeleteKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.FinallyMethodIdentifier)) yield return token;
				foreach (Token token in Tokens(syntax.FinallyOpenParenToken)) yield return token;
				if (syntax.FinallyArgumentList is not null) foreach (Token token in Tokens(syntax.FinallyArgumentList)) yield return token;
				foreach (Token token in Tokens(syntax.FinallyCloseParenToken)) yield return token;
				break;
			case UnaryPrefixSyntax syntax:
				foreach (Token token in Tokens(syntax.OperatorOrKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.NewKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case PostfixExpressionSyntax syntax:
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				foreach (SyntaxNode child in syntax.Parts ?? []) foreach (Token token in Tokens(child)) yield return token;
				break;
			case CallPostfixPartSyntax syntax:
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.ArgumentList is not null) foreach (Token token in Tokens(syntax.ArgumentList)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case IndexPostfixPartSyntax syntax:
				foreach (Token token in Tokens(syntax.OpenBracketToken)) yield return token;
				if (syntax.ArgumentList is not null) foreach (Token token in Tokens(syntax.ArgumentList)) yield return token;
				foreach (Token token in Tokens(syntax.CloseBracketToken)) yield return token;
				break;
			case MemberPostfixPartSyntax syntax:
				foreach (Token token in Tokens(syntax.DotToken)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				break;
			case NamelessIndexerPostfixPartSyntax syntax:
				foreach (Token token in Tokens(syntax.DotToken)) yield return token;
				foreach (Token token in Tokens(syntax.OpenBracketToken)) yield return token;
				if (syntax.ArgumentList is not null) foreach (Token token in Tokens(syntax.ArgumentList)) yield return token;
				foreach (Token token in Tokens(syntax.CloseBracketToken)) yield return token;
				break;
			case GenericPostfixPartSyntax syntax:
				foreach (Token token in Tokens(syntax.LessThanToken)) yield return token;
				if (syntax.TypeArgumentList is not null) foreach (Token token in Tokens(syntax.TypeArgumentList)) yield return token;
				foreach (Token token in Tokens(syntax.GreaterThanToken)) yield return token;
				break;
			case PostfixOperatorPartSyntax syntax:
				foreach (Token token in Tokens(syntax.Operator)) yield return token;
				break;
			case LiteralExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.Literal)) yield return token;
				break;
			case InterpolatedStringExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.WithinKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.WithinOpenParenToken)) yield return token;
				if (syntax.AllocatorExpression is not null) foreach (Token token in Tokens(syntax.AllocatorExpression)) yield return token;
				foreach (Token token in Tokens(syntax.WithinCloseParenToken)) yield return token;
				foreach (Token token in Tokens(syntax.NewKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.Literal)) yield return token;
				break;
			case QualifiedNameExpressionSyntax syntax:
				foreach (SyntaxNode child in syntax.Qualifiers ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				break;
			case ThisExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.ThisKeyword)) yield return token;
				break;
			case DefaultExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.DefaultKeyword)) yield return token;
				break;
			case ParenthesizedExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case GroupedExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.ItemList is not null) foreach (Token token in Tokens(syntax.ItemList)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case ArrayExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.OpenBracketToken)) yield return token;
				if (syntax.ExpressionList is not null) foreach (Token token in Tokens(syntax.ExpressionList)) yield return token;
				foreach (Token token in Tokens(syntax.CloseBracketToken)) yield return token;
				break;
			case CastExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				foreach (Token token in Tokens(syntax.UnsafeKeyword)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				if (syntax.LifetimeDeclarator is not null) foreach (Token token in Tokens(syntax.LifetimeDeclarator)) yield return token;
				foreach (Token token in Tokens(syntax.CastKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				break;
			case ConstructionExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.WithinKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.WithinOpenParenToken)) yield return token;
				if (syntax.AllocatorExpression is not null) foreach (Token token in Tokens(syntax.AllocatorExpression)) yield return token;
				foreach (Token token in Tokens(syntax.WithinCloseParenToken)) yield return token;
				foreach (Token token in Tokens(syntax.Keyword)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.ArgumentList is not null) foreach (Token token in Tokens(syntax.ArgumentList)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				foreach (Token token in Tokens(syntax.OpenBracketToken)) yield return token;
				if (syntax.ElementCount is not null) foreach (Token token in Tokens(syntax.ElementCount)) yield return token;
				foreach (Token token in Tokens(syntax.CloseBracketToken)) yield return token;
				if (syntax.InitializerList is not null) foreach (Token token in Tokens(syntax.InitializerList)) yield return token;
				break;
			case SizeOfExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.SizeOfKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case VTableOfExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.VTableOfKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.Type is not null) foreach (Token token in Tokens(syntax.Type)) yield return token;
				foreach (Token token in Tokens(syntax.ColonToken)) yield return token;
				if (syntax.InterfaceType is not null) foreach (Token token in Tokens(syntax.InterfaceType)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case NameOfExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.NameOfKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				foreach (Token token in syntax.Tokens ?? []) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case SymbolOfExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.SymbolOfKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				foreach (Token token in syntax.Tokens ?? []) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				break;
			case InitializerListSyntax syntax:
				foreach (Token token in Tokens(syntax.OpenBraceToken)) yield return token;
				if (syntax.ItemList is not null) foreach (Token token in Tokens(syntax.ItemList)) yield return token;
				foreach (Token token in Tokens(syntax.CloseBraceToken)) yield return token;
				break;
			case InitializerItemSyntax syntax:
				if (syntax.Target is not null) foreach (Token token in Tokens(syntax.Target)) yield return token;
				foreach (Token token in Tokens(syntax.EqualsToken)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				break;
			case InitializerTargetSyntax syntax:
				foreach (SyntaxNode child in syntax.Parts ?? []) foreach (Token token in Tokens(child)) yield return token;
				break;
			case InitializerTargetPartSyntax syntax:
				foreach (Token token in Tokens(syntax.DotToken)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				foreach (Token token in Tokens(syntax.OpenBracketToken)) yield return token;
				if (syntax.ArgumentList is not null) foreach (Token token in Tokens(syntax.ArgumentList)) yield return token;
				foreach (Token token in Tokens(syntax.CloseBracketToken)) yield return token;
				break;
			case LambdaExpressionSyntax syntax:
				foreach (Token token in Tokens(syntax.WithinKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.WithinOpenParenToken)) yield return token;
				if (syntax.AllocatorExpression is not null) foreach (Token token in Tokens(syntax.AllocatorExpression)) yield return token;
				foreach (Token token in Tokens(syntax.WithinCloseParenToken)) yield return token;
				foreach (Token token in Tokens(syntax.NewKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.DelegateKeyword)) yield return token;
				if (syntax.Parameter is not null) foreach (Token token in Tokens(syntax.Parameter)) yield return token;
				foreach (Token token in Tokens(syntax.OpenParenToken)) yield return token;
				if (syntax.ParameterList is not null) foreach (Token token in Tokens(syntax.ParameterList)) yield return token;
				foreach (Token token in Tokens(syntax.CloseParenToken)) yield return token;
				foreach (Token token in Tokens(syntax.ArrowToken)) yield return token;
				if (syntax.Body is not null) foreach (Token token in Tokens(syntax.Body)) yield return token;
				break;
			case LambdaParameterSyntax syntax:
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				if (syntax.Parameter is not null) foreach (Token token in Tokens(syntax.Parameter)) yield return token;
				break;
			case LambdaBodySyntax syntax:
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				if (syntax.MethodBody is not null) foreach (Token token in Tokens(syntax.MethodBody)) yield return token;
				break;
			case GroupedExpressionItemSyntax syntax:
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				foreach (Token token in Tokens(syntax.ColonToken)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				break;
			case ArgumentSyntax syntax:
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				foreach (Token token in Tokens(syntax.ColonToken)) yield return token;
				foreach (Token token in Tokens(syntax.OutKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.CatchKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.AutoKeyword)) yield return token;
				foreach (Token token in Tokens(syntax.WithinKeyword)) yield return token;
				if (syntax.DeclarationTarget is not null) foreach (Token token in Tokens(syntax.DeclarationTarget)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				break;
			case AssignmentOperatorSyntax syntax:
				foreach (Token token in Tokens(syntax.Operator)) yield return token;
				break;
			case BinaryOperatorSyntax syntax:
				foreach (Token token in Tokens(syntax.Operator)) yield return token;
				break;
			case GroupedExpressionItemListSyntax syntax:
				foreach (SyntaxNode child in syntax.Items ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				break;
			case InitializerItemListSyntax syntax:
				foreach (SyntaxNode child in syntax.Items ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				break;
			case LambdaParameterListSyntax syntax:
				foreach (SyntaxNode child in syntax.Parameters ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				break;
			case ArgumentListSyntax syntax:
				foreach (SyntaxNode child in syntax.Arguments ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in syntax.Commas ?? []) yield return token;
				break;
			case EnumValueSyntax syntax:
				foreach (SyntaxNode child in syntax.Attributes ?? []) foreach (Token token in Tokens(child)) yield return token;
				foreach (Token token in Tokens(syntax.Identifier)) yield return token;
				foreach (Token token in Tokens(syntax.EqualsToken)) yield return token;
				if (syntax.Expression is not null) foreach (Token token in Tokens(syntax.Expression)) yield return token;
				break;
		}
	}

	public static IEnumerable<Token> Tokens(Token? token)
	{
		if (token is not null)
			yield return token.Value;
	}

	public static IEnumerable<Token> Tokens(TokenRange? range)
	{
		if (range is TokenRange value)
			foreach (Token token in Tokens(value))
				yield return token;
	}

	public static IEnumerable<Token> Tokens(TokenRange range)
	{
		for (int i = 0; i < range.Count; i++)
			yield return new Token(range.Sequence, range.Index + i);
	}

	public static bool TryGetRange(SyntaxNode? node, out TokenRange range)
	{
		range = default;
		if (node is null) return false;
		Token? first = null;
		Token? last = null;
		foreach (Token token in Tokens(node))
		{
			first ??= token;
			last = token;
		}
		if (first is null || last is null) return false;
		if (!ReferenceEquals(first.Value.Sequence, last.Value.Sequence))
			return false;
		range = new TokenRange(first.Value.Sequence, first.Value.Index, last.Value.Index - first.Value.Index + 1);
		return true;
	}
}
