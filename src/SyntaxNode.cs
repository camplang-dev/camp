using System.Collections.Generic;

namespace Camp.Compiler;

public abstract class SyntaxNode
{
}

public class CompilationUnitSyntax : SyntaxNode
{
	public List<CompilationUnitItemSyntax>? Items { get; set; }
}

public class CompilationUnitItemSyntax : SyntaxNode
{
	public ImportExportDeclarationSyntax? ImportExportDeclaration { get; set; }
	public DeclarationSyntax? Declaration { get; set; }
}

public abstract class ImportExportDeclarationSyntax : SyntaxNode
{
	public Token? Keyword { get; set; }
}

public class UsingImportExportDeclarationSyntax : ImportExportDeclarationSyntax
{
	public QualifiedNamespaceSyntax? QualifiedNamespace { get; set; }
	public Token? AsKeyword { get; set; }
	public Token? Alias { get; set; }
	public Token? OpenBraceToken { get; set; }
	public IdentListSyntax? SelectedIdentifiers { get; set; }
	public Token? CloseBraceToken { get; set; }
	public Token? SemicolonToken { get; set; }
}

public class ExportImportExportDeclarationSyntax : ImportExportDeclarationSyntax
{
	public Token? AsKeyword { get; set; }
	public QualifiedNamespaceSyntax? QualifiedNamespace { get; set; }
	public Token? SemicolonToken { get; set; }
}

public class QualifiedNamespaceSyntax : SyntaxNode
{
	public List<QualifierSyntax>? Qualifiers { get; set; }
	public Token? Identifier { get; set; }
}

public class QualifierSyntax : SyntaxNode
{
	public Token? Identifier { get; set; }
	public TokenRange? ColonColonToken { get; set; }
}

public class TypeDeclarationSyntax : SyntaxNode
{
	public List<AttributeSyntax>? Attributes { get; set; }
	public List<TypeDeclarationDeclaratorSyntax>? Declarators { get; set; }
	public Token? Keyword { get; set; }
	public TypeSyntax? Type { get; set; }
	public Token? Identifier { get; set; }
	public GenericParameterListSyntax? GenericParameterList { get; set; }
	public ParameterListSyntax? ParameterList { get; set; }
	public Token? ColonToken { get; set; }
	public UnderlyingTypeListSyntax? UnderlyingTypeList { get; set; }
	public Token? SemicolonToken { get; set; }
	public TypeDeclarationScopeSyntax? Scope { get; set; }
}

public class AttributeSyntax : SyntaxNode
{
	public Token? AttributeIdentifier { get; set; }
	public Token? OpenParenToken { get; set; }
	public ExpressionListSyntax? ExpressionList { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class TypeDeclarationDeclaratorSyntax : SyntaxNode
{
	public Token? Keyword { get; set; }
}

public class GenericParameterSyntax : SyntaxNode
{
	public Token? Identifier { get; set; }
	public Token? ColonToken { get; set; }
	public Token? ImplementsKeyword { get; set; }
	public TypeSyntax? Type { get; set; }
}

public class TypeDeclarationScopeSyntax : SyntaxNode
{
	public Token? OpenBraceToken { get; set; }
	public EnumValueListSyntax? EnumValueList { get; set; }
	public Token? SemicolonToken { get; set; }
	public List<DeclarationSyntax>? Declarations { get; set; }
	public Token? CloseBraceToken { get; set; }
}

public class DeclarationSyntax : SyntaxNode
{
	public TypeDeclarationSyntax? TypeDeclaration { get; set; }
	public MemberDeclarationSyntax? MemberDeclaration { get; set; }
}

public class MemberDeclarationSyntax : SyntaxNode
{
	public List<AttributeSyntax>? Attributes { get; set; }
	public List<MemberDeclaratorSyntax>? Declarators { get; set; }
	public TypeSyntax? Type { get; set; }
	public Token? TildeToken { get; set; }
	public Token? Identifier { get; set; }
	public GenericParameterListSyntax? GenericParameterList { get; set; }
	public ParameterListSyntax? ParameterList { get; set; }
	public Token? SemicolonToken { get; set; }
	public MethodBodySyntax? MethodBody { get; set; }
	public AssignmentSyntax? Assignment { get; set; }
}

public class MemberDeclaratorSyntax : SyntaxNode
{
	public Token? Keyword { get; set; }
}

public abstract class ParameterSyntax : SyntaxNode
{
}

public class ValueParameterSyntax : ParameterSyntax
{
	public Token? WithinKeyword { get; set; }
	public List<ParameterDeclaratorSyntax>? Declarators { get; set; }
	public TypeSyntax? Type { get; set; }
	public Token? Identifier { get; set; }
	public Token? EqualsToken { get; set; }
	public ExpressionSyntax? DefaultValue { get; set; }
}

public class WithinParameterSyntax : ParameterSyntax
{
	public Token? WithinKeyword { get; set; }
	public Token? Identifier { get; set; }
}

public class ThisParameterSyntax : ParameterSyntax
{
	public List<TypeDeclaratorSyntax>? Declarators { get; set; }
	public Token? ThisKeyword { get; set; }
}

public class SizeOfParameterSyntax : ParameterSyntax
{
	public Token? SizeOfKeyword { get; set; }
	public Token? OpenParenToken { get; set; }
	public TypeSyntax? Type { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class VTableOfParameterSyntax : ParameterSyntax
{
	public Token? VTableOfKeyword { get; set; }
	public Token? OpenParenToken { get; set; }
	public TypeSyntax? Type { get; set; }
	public Token? ColonToken { get; set; }
	public TypeSyntax? InterfaceType { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class ParameterDeclaratorSyntax : SyntaxNode
{
	public List<AttributeSyntax>? Attributes { get; set; }
	public Token? Keyword { get; set; }
}

public abstract class TypeSyntax : SyntaxNode
{
}

public class CallableTypeSyntax : TypeSyntax
{
	public Token? CallableKeyword { get; set; }
	public TypeSyntax? ReturnType { get; set; }
	public ParameterListSyntax? ParameterList { get; set; }
}

public class AttributedTypeSyntax : TypeSyntax
{
	public AttributeSyntax? Attribute { get; set; }
	public TypeSyntax? Type { get; set; }
}

public class ArrayTypeSyntax : TypeSyntax
{
	public TypeSyntax? ElementType { get; set; }
	public Token? OpenBracketToken { get; set; }
	public Token? CloseBracketToken { get; set; }
}

public class OptionalTypeSyntax : TypeSyntax
{
	public TypeSyntax? ElementType { get; set; }
	public Token? QuestionToken { get; set; }
}

public class PointerTypeSyntax : TypeSyntax
{
	public TypeSyntax? ElementType { get; set; }
	public Token? StarToken { get; set; }
}

public class GenericTypeSyntax : TypeSyntax
{
	public TypeSyntax? Type { get; set; }
	public Token? LessThanToken { get; set; }
	public TypeListSyntax? TypeArgumentList { get; set; }
	public Token? GreaterThanToken { get; set; }
}

public class IterTypeSyntax : TypeSyntax
{
	public Token? StorageKeyword { get; set; }
	public Token? IterKeyword { get; set; }
	public TypeSyntax? ElementType { get; set; }
}

public class ParamsTypeSyntax : TypeSyntax
{
	public Token? ParamsKeyword { get; set; }
	public Token? OpenParenToken { get; set; }
	public TypeSyntax? Type { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class StructTypeSyntax : TypeSyntax
{
	public Token? StructKeyword { get; set; }
	public Token? OpenParenToken { get; set; }
	public TypeSyntax? Type { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class ThrownTypeSyntax : TypeSyntax
{
	public Token? ThrownKeyword { get; set; }
	public Token? OpenParenToken { get; set; }
	public TypeSyntax? Type { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class DeclaratorTypeSyntax : TypeSyntax
{
	public TypeDeclaratorSyntax? Declarator { get; set; }
	public TypeSyntax? Type { get; set; }
}

public class QualifiedNameTypeSyntax : TypeSyntax
{
	public List<QualifierSyntax>? Qualifiers { get; set; }
	public Token? Identifier { get; set; }
}

public class TypeDeclaratorSyntax : SyntaxNode
{
	public Token? Keyword { get; set; }
	public Token? OpenParenToken { get; set; }
	public IdentListSyntax? AnchorList { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class AssignmentSyntax : SyntaxNode
{
	public Token? EqualsToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
	public Token? SemicolonToken { get; set; }
}

public abstract class MethodBodySyntax : SyntaxNode
{
}

public class BlockMethodBodySyntax : MethodBodySyntax
{
	public Token? OpenBraceToken { get; set; }
	public List<StatementSyntax>? Statements { get; set; }
	public Token? CloseBraceToken { get; set; }
}

public class ExpressionMethodBodySyntax : MethodBodySyntax
{
	public TokenRange? ArrowToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
	public Token? SemicolonToken { get; set; }
}

public abstract class StatementSyntax : SyntaxNode
{
}

public class BlockStatementSyntax : StatementSyntax
{
	public Token? OpenBraceToken { get; set; }
	public List<StatementSyntax>? Statements { get; set; }
	public Token? CloseBraceToken { get; set; }
}

public class KeywordStatementSyntax : StatementSyntax
{
	public Token? Keyword { get; set; }
	public Token? OpenParenToken { get; set; }
	public StatementConditionSyntax? Condition { get; set; }
	public Token? CloseParenToken { get; set; }
	public StatementSyntax? Body { get; set; }
	public Token? BodyOpenBraceToken { get; set; }
	public List<StatementSyntax>? BodyStatements { get; set; }
	public Token? BodyCloseBraceToken { get; set; }
}

public class CaseStatementSyntax : StatementSyntax
{
	public Token? CaseKeyword { get; set; }
	public ExpressionSyntax? Expression { get; set; }
	public Token? ColonToken { get; set; }
}

public class DefaultStatementSyntax : StatementSyntax
{
	public Token? DefaultKeyword { get; set; }
	public Token? ColonToken { get; set; }
}

public class DeclarationStatementStatementSyntax : StatementSyntax
{
	public DeclarationStatementSyntax? DeclarationStatement { get; set; }
	public Token? SemicolonToken { get; set; }
}

public class ExpressionStatementSyntax : StatementSyntax
{
	public ExpressionSyntax? Expression { get; set; }
	public Token? SemicolonToken { get; set; }
}

public class EmptyStatementSyntax : StatementSyntax
{
	public Token? SemicolonToken { get; set; }
}

public class DeclarationTargetSyntax : SyntaxNode
{
	public TypeSyntax? Type { get; set; }
	public Token? Identifier { get; set; }

	public Token? AutoKeyword { get; set; }
	public Token? AutoIdentifier { get; set; }
	public Token? OpenParenToken { get; set; }
	public IdentListSyntax? IdentifierList { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class DeclarationStatementSyntax : SyntaxNode
{
	public DeclarationTargetSyntax? Target { get; set; }
	public AssignmentSyntax? Assignment { get; set; }
}

public abstract class StatementConditionSyntax : SyntaxNode
{
}

public class IterationStatementConditionSyntax : StatementConditionSyntax
{
	public DeclarationTargetSyntax? Target { get; set; }
	public Token? InKeyword { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}

public class ClauseStatementConditionSyntax : StatementConditionSyntax
{
	public DeclarationStatementSyntax? DeclarationStatement { get; set; }
	public List<StatementConditionClauseSyntax>? Clauses { get; set; }
}

public class StatementConditionClauseSyntax : SyntaxNode
{
	public Token? SemicolonToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}

public class IdentListSyntax : SyntaxNode
{
	public List<Token>? Identifiers { get; set; }
	public List<Token>? Commas { get; set; }
}

public class GenericParameterListSyntax : SyntaxNode
{
	public Token? LessThanToken { get; set; }
	public List<GenericParameterSyntax>? Parameters { get; set; }
	public List<Token>? Commas { get; set; }
	public Token? GreaterThanToken { get; set; }
}

public class UnderlyingTypeListSyntax : SyntaxNode
{
	public List<TypeSyntax>? Types { get; set; }
	public List<Token>? Commas { get; set; }
}

public class EnumValueListSyntax : SyntaxNode
{
	public List<EnumValueSyntax>? Values { get; set; }
	public List<Token>? Commas { get; set; }
}

public class ParameterListSyntax : SyntaxNode
{
	public Token? OpenParenToken { get; set; }
	public List<ParameterSyntax>? Parameters { get; set; }
	public List<Token>? Commas { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class TypeListSyntax : SyntaxNode
{
	public List<TypeSyntax>? Types { get; set; }
	public List<Token>? Commas { get; set; }
}

public class ExpressionListSyntax : SyntaxNode
{
	public List<ExpressionSyntax>? Expressions { get; set; }
	public List<Token>? Commas { get; set; }
}

public class AssignmentExpressionListSyntax : SyntaxNode
{
	public List<ExpressionSyntax>? Expressions { get; set; }
	public List<Token>? Commas { get; set; }
}

public abstract class ExpressionSyntax : SyntaxNode
{
}

public class CommaExpressionSyntax : ExpressionSyntax
{
	public List<ExpressionSyntax>? Expressions { get; set; }
	public List<Token>? Commas { get; set; }
}

public class AssignmentExpressionSyntax : ExpressionSyntax
{
	public ExpressionSyntax? Left { get; set; }
	public AssignmentOperatorSyntax? Operator { get; set; }
	public ExpressionSyntax? Right { get; set; }
}

public class ConditionalExpressionSyntax : ExpressionSyntax
{
	public ExpressionSyntax? Condition { get; set; }
	public Token? QuestionToken { get; set; }
	public ExpressionSyntax? WhenTrue { get; set; }
	public Token? ColonToken { get; set; }
	public ExpressionSyntax? WhenFalse { get; set; }
}

public class RangeExpressionSyntax : ExpressionSyntax
{
	public ExpressionSyntax? Start { get; set; }
	public TokenRange? DotDotToken { get; set; }
	public ExpressionSyntax? End { get; set; }
}

public class BinaryExpressionSyntax : ExpressionSyntax
{
	public ExpressionSyntax? FirstExpression { get; set; }
	public List<BinaryExpressionPartSyntax>? Parts { get; set; }
}

public class BinaryExpressionPartSyntax : SyntaxNode
{
	public BinaryOperatorSyntax? Operator { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}

public class UnaryExpressionSyntax : ExpressionSyntax
{
	public List<UnaryPrefixSyntax>? Prefixes { get; set; }
	public ExpressionSyntax? Expression { get; set; }
	public Token? FinallyKeyword { get; set; }
	public Token? DeleteKeyword { get; set; }
}

public class UnaryPrefixSyntax : SyntaxNode
{
	public TokenRange? OperatorOrKeyword { get; set; }
	public Token? OpenParenToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class PostfixExpressionSyntax : ExpressionSyntax
{
	public ExpressionSyntax? Expression { get; set; }
	public List<PostfixPartSyntax>? Parts { get; set; }
}

public abstract class PostfixPartSyntax : SyntaxNode
{
}

public class CallPostfixPartSyntax : PostfixPartSyntax
{
	public Token? OpenParenToken { get; set; }
	public ArgumentListSyntax? ArgumentList { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class IndexPostfixPartSyntax : PostfixPartSyntax
{
	public Token? OpenBracketToken { get; set; }
	public ArgumentListSyntax? ArgumentList { get; set; }
	public Token? CloseBracketToken { get; set; }
}

public class MemberPostfixPartSyntax : PostfixPartSyntax
{
	public Token? DotToken { get; set; }
	public Token? Identifier { get; set; }
}

public class NamelessIndexerPostfixPartSyntax : PostfixPartSyntax
{
	public Token? DotToken { get; set; }
	public Token? OpenBracketToken { get; set; }
	public ArgumentListSyntax? ArgumentList { get; set; }
	public Token? CloseBracketToken { get; set; }
}

public class GenericPostfixPartSyntax : PostfixPartSyntax
{
	public Token? LessThanToken { get; set; }
	public TypeListSyntax? TypeArgumentList { get; set; }
	public Token? GreaterThanToken { get; set; }
}

public class PostfixOperatorPartSyntax : PostfixPartSyntax
{
	public TokenRange? Operator { get; set; }
}

public abstract class PrimaryExpressionSyntax : ExpressionSyntax
{
}

public class LiteralExpressionSyntax : PrimaryExpressionSyntax
{
	public Token? Literal { get; set; }
}

public class QualifiedNameExpressionSyntax : PrimaryExpressionSyntax
{
	public List<QualifierSyntax>? Qualifiers { get; set; }
	public Token? Identifier { get; set; }
}

public class ThisExpressionSyntax : PrimaryExpressionSyntax
{
	public Token? ThisKeyword { get; set; }
}

public class DefaultExpressionSyntax : PrimaryExpressionSyntax
{
	public Token? DefaultKeyword { get; set; }
}

public class ParenthesizedExpressionSyntax : PrimaryExpressionSyntax
{
	public Token? OpenParenToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class GroupedExpressionSyntax : PrimaryExpressionSyntax
{
	public Token? OpenParenToken { get; set; }
	public GroupedExpressionItemListSyntax? ItemList { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class ArrayExpressionSyntax : PrimaryExpressionSyntax
{
	public Token? OpenBracketToken { get; set; }
	public ExpressionListSyntax? ExpressionList { get; set; }
	public Token? CloseBracketToken { get; set; }
}

public class CastExpressionSyntax : PrimaryExpressionSyntax
{
	public Token? OpenParenToken { get; set; }
	public TypeSyntax? Type { get; set; }
	public Token? CastKeyword { get; set; }
	public Token? CloseParenToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}

public class ConstructionExpressionSyntax : PrimaryExpressionSyntax
{
	public Token? WithinKeyword { get; set; }
	public Token? WithinOpenParenToken { get; set; }
	public ExpressionSyntax? AllocatorExpression { get; set; }
	public Token? WithinCloseParenToken { get; set; }
	public Token? Keyword { get; set; }
	public TypeSyntax? Type { get; set; }
	public Token? OpenParenToken { get; set; }
	public ArgumentListSyntax? ArgumentList { get; set; }
	public Token? CloseParenToken { get; set; }
	public Token? OpenBracketToken { get; set; }
	public ExpressionSyntax? ElementCount { get; set; }
	public Token? CloseBracketToken { get; set; }
	public InitializerListSyntax? InitializerList { get; set; }
}

public class SizeOfExpressionSyntax : PrimaryExpressionSyntax
{
	public Token? SizeOfKeyword { get; set; }
	public Token? OpenParenToken { get; set; }
	public TypeSyntax? Type { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class VTableOfExpressionSyntax : PrimaryExpressionSyntax
{
	public Token? VTableOfKeyword { get; set; }
	public Token? OpenParenToken { get; set; }
	public TypeSyntax? Type { get; set; }
	public Token? ColonToken { get; set; }
	public TypeSyntax? InterfaceType { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class InitializerListSyntax : PrimaryExpressionSyntax
{
	public Token? OpenBraceToken { get; set; }
	public InitializerItemListSyntax? ItemList { get; set; }
	public Token? CloseBraceToken { get; set; }
}

public class InitializerItemSyntax : SyntaxNode
{
	public InitializerTargetSyntax? Target { get; set; }
	public Token? EqualsToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}

public class InitializerTargetSyntax : SyntaxNode
{
	public List<InitializerTargetPartSyntax>? Parts { get; set; }
}

public class InitializerTargetPartSyntax : SyntaxNode
{
	public Token? DotToken { get; set; }
	public Token? Identifier { get; set; }
	public Token? OpenBracketToken { get; set; }
	public ArgumentListSyntax? ArgumentList { get; set; }
	public Token? CloseBracketToken { get; set; }
}

public class LambdaExpressionSyntax : ExpressionSyntax
{
	public LambdaParameterSyntax? Parameter { get; set; }
	public Token? OpenParenToken { get; set; }
	public LambdaParameterListSyntax? ParameterList { get; set; }
	public Token? CloseParenToken { get; set; }
	public TokenRange? ArrowToken { get; set; }
	public LambdaBodySyntax? Body { get; set; }
}

public class LambdaParameterSyntax : SyntaxNode
{
	public Token? Identifier { get; set; }
	public ParameterSyntax? Parameter { get; set; }
}

public class LambdaBodySyntax : SyntaxNode
{
	public ExpressionSyntax? Expression { get; set; }
	public MethodBodySyntax? MethodBody { get; set; }
}

public class GroupedExpressionItemSyntax : SyntaxNode
{
	public Token? Identifier { get; set; }
	public Token? ColonToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}

public class ArgumentSyntax : SyntaxNode
{
	public Token? Identifier { get; set; }
	public Token? ColonToken { get; set; }
	public Token? OutKeyword { get; set; }
	public Token? CatchKeyword { get; set; }
	public Token? AutoKeyword { get; set; }
	public Token? WithinKeyword { get; set; }
	public DeclarationTargetSyntax? DeclarationTarget { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}

public class AssignmentOperatorSyntax : SyntaxNode
{
	public TokenRange? Operator { get; set; }
}

public class BinaryOperatorSyntax : SyntaxNode
{
	public TokenRange? Operator { get; set; }
}

public class GroupedExpressionItemListSyntax : SyntaxNode
{
	public List<GroupedExpressionItemSyntax>? Items { get; set; }
	public List<Token>? Commas { get; set; }
}

public class InitializerItemListSyntax : SyntaxNode
{
	public List<InitializerItemSyntax>? Items { get; set; }
	public List<Token>? Commas { get; set; }
}

public class LambdaParameterListSyntax : SyntaxNode
{
	public List<LambdaParameterSyntax>? Parameters { get; set; }
	public List<Token>? Commas { get; set; }
}

public class ArgumentListSyntax : SyntaxNode
{
	public List<ArgumentSyntax>? Arguments { get; set; }
	public List<Token>? Commas { get; set; }
}

public class EnumValueSyntax : SyntaxNode
{
	public Token? Identifier { get; set; }
	public Token? EqualsToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}
