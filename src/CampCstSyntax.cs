using System.Collections.Generic;

namespace Camp.Compiler;

public abstract class Syntax
{
}

public class CompilationUnitSyntax : Syntax
{
	public List<CompilationUnitItemSyntax>? Items { get; set; }
}

public class CompilationUnitItemSyntax : Syntax
{
	public ImportExportDeclarationSyntax? ImportExportDeclaration { get; set; }
	public DeclarationSyntax? Declaration { get; set; }
}

public abstract class ImportExportDeclarationSyntax : Syntax
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

public class QualifiedNamespaceSyntax : Syntax
{
	public List<QualifierSyntax>? Qualifiers { get; set; }
	public Token? Identifier { get; set; }
}

public class QualifierSyntax : Syntax
{
	public Token? Identifier { get; set; }
	public TokenRange? ColonColonToken { get; set; }
}

public class TypeDeclarationSyntax : Syntax
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

public class AttributeSyntax : Syntax
{
	public Token? AttributeIdentifier { get; set; }
	public Token? OpenParenToken { get; set; }
	public ExpressionListSyntax? ExpressionList { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class TypeDeclarationDeclaratorSyntax : Syntax
{
	public Token? Keyword { get; set; }
}

public class GenericParameterSyntax : Syntax
{
	public Token? Identifier { get; set; }
	public Token? ColonToken { get; set; }
	public Token? ImplementsKeyword { get; set; }
	public TypeSyntax? Type { get; set; }
}

public class TypeDeclarationScopeSyntax : Syntax
{
	public Token? OpenBraceToken { get; set; }
	public EnumValueListSyntax? EnumValueList { get; set; }
	public Token? SemicolonToken { get; set; }
	public List<DeclarationSyntax>? Declarations { get; set; }
	public Token? CloseBraceToken { get; set; }
}

public class DeclarationSyntax : Syntax
{
	public TypeDeclarationSyntax? TypeDeclaration { get; set; }
	public MemberDeclarationSyntax? MemberDeclaration { get; set; }
}

public class MemberDeclarationSyntax : Syntax
{
	public List<AttributeSyntax>? Attributes { get; set; }
	public List<MemberDeclaratorSyntax>? Declarators { get; set; }
	public TypeSyntax? Type { get; set; }
	public List<MemberQualifierSyntax>? Qualifiers { get; set; }
	public Token? TildeToken { get; set; }
	public Token? Identifier { get; set; }
	public GenericParameterListSyntax? GenericParameterList { get; set; }
	public ParameterListSyntax? ParameterList { get; set; }
	public Token? SemicolonToken { get; set; }
	public MethodBodySyntax? MethodBody { get; set; }
	public AssignmentSyntax? Assignment { get; set; }
}

public class MemberDeclaratorSyntax : Syntax
{
	public Token? Keyword { get; set; }
}

public class MemberQualifierSyntax : Syntax
{
	public TypeSyntax? Type { get; set; }
	public TokenRange? ColonColonToken { get; set; }
}

public abstract class ParameterSyntax : Syntax
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
	public Token? CloseParenToken { get; set; }
}

public class ParameterDeclaratorSyntax : Syntax
{
	public List<AttributeSyntax>? Attributes { get; set; }
	public Token? Keyword { get; set; }
}

public abstract class TypeSyntax : Syntax
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

public class TypeDeclaratorSyntax : Syntax
{
	public Token? Keyword { get; set; }
	public Token? OpenParenToken { get; set; }
	public IdentListSyntax? AnchorList { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class AssignmentSyntax : Syntax
{
	public Token? EqualsToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
	public Token? SemicolonToken { get; set; }
}

public abstract class MethodBodySyntax : Syntax
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

public abstract class StatementSyntax : Syntax
{
}

public class KeywordStatementSyntax : StatementSyntax
{
	public StatementKeywordSyntax? Keyword { get; set; }
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

public class DeclarationTargetSyntax : Syntax
{
	public TypeSyntax? Type { get; set; }
	public Token? Identifier { get; set; }

	public Token? AutoKeyword { get; set; }
	public Token? AutoIdentifier { get; set; }
	public Token? OpenParenToken { get; set; }
	public IdentListSyntax? IdentifierList { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class DeclarationStatementSyntax : Syntax
{
	public DeclarationTargetSyntax? Target { get; set; }
	public AssignmentSyntax? Assignment { get; set; }
}

public class StatementKeywordSyntax : Syntax
{
	public Token? Keyword { get; set; }
}

public abstract class StatementConditionSyntax : Syntax
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

public class StatementConditionClauseSyntax : Syntax
{
	public Token? SemicolonToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}

public class IdentListSyntax : Syntax
{
	public List<Token>? Identifiers { get; set; }
	public List<Token>? Commas { get; set; }
}

public class GenericParameterListSyntax : Syntax
{
	public Token? LessThanToken { get; set; }
	public List<GenericParameterSyntax>? Parameters { get; set; }
	public List<Token>? Commas { get; set; }
	public Token? GreaterThanToken { get; set; }
}

public class UnderlyingTypeListSyntax : Syntax
{
	public List<TypeSyntax>? Types { get; set; }
	public List<Token>? Commas { get; set; }
}

public class EnumValueListSyntax : Syntax
{
	public List<EnumValueSyntax>? Values { get; set; }
	public List<Token>? Commas { get; set; }
}

public class ParameterListSyntax : Syntax
{
	public Token? OpenParenToken { get; set; }
	public List<ParameterSyntax>? Parameters { get; set; }
	public List<Token>? Commas { get; set; }
	public Token? CloseParenToken { get; set; }
}

public class TypeListSyntax : Syntax
{
	public List<TypeSyntax>? Types { get; set; }
	public List<Token>? Commas { get; set; }
}

public class ExpressionListSyntax : Syntax
{
	public List<ExpressionSyntax>? Expressions { get; set; }
	public List<Token>? Commas { get; set; }
}

public class AssignmentExpressionListSyntax : Syntax
{
	public List<ExpressionSyntax>? Expressions { get; set; }
	public List<Token>? Commas { get; set; }
}

public abstract class ExpressionSyntax : Syntax
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

public class BinaryExpressionPartSyntax : Syntax
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

public class UnaryPrefixSyntax : Syntax
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

public abstract class PostfixPartSyntax : Syntax
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
	public Token? CloseParenToken { get; set; }
}

public class InitializerListSyntax : PrimaryExpressionSyntax
{
	public Token? OpenBraceToken { get; set; }
	public InitializerItemListSyntax? ItemList { get; set; }
	public Token? CloseBraceToken { get; set; }
}

public class InitializerItemSyntax : Syntax
{
	public InitializerTargetSyntax? Target { get; set; }
	public Token? EqualsToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}

public class InitializerTargetSyntax : Syntax
{
	public List<InitializerTargetPartSyntax>? Parts { get; set; }
}

public class InitializerTargetPartSyntax : Syntax
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

public class LambdaParameterSyntax : Syntax
{
	public Token? Identifier { get; set; }
	public ParameterSyntax? Parameter { get; set; }
}

public class LambdaBodySyntax : Syntax
{
	public ExpressionSyntax? Expression { get; set; }
	public MethodBodySyntax? MethodBody { get; set; }
}

public class GroupedExpressionItemSyntax : Syntax
{
	public Token? Identifier { get; set; }
	public Token? ColonToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}

public class ArgumentSyntax : Syntax
{
	public Token? Identifier { get; set; }
	public Token? ColonToken { get; set; }
	public Token? OutKeyword { get; set; }
	public Token? CatchKeyword { get; set; }
	public Token? AutoKeyword { get; set; }
	public Token? WithinKeyword { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}

public class AssignmentOperatorSyntax : Syntax
{
	public TokenRange? Operator { get; set; }
}

public class BinaryOperatorSyntax : Syntax
{
	public TokenRange? Operator { get; set; }
}

public class GroupedExpressionItemListSyntax : Syntax
{
	public List<GroupedExpressionItemSyntax>? Items { get; set; }
	public List<Token>? Commas { get; set; }
}

public class InitializerItemListSyntax : Syntax
{
	public List<InitializerItemSyntax>? Items { get; set; }
	public List<Token>? Commas { get; set; }
}

public class LambdaParameterListSyntax : Syntax
{
	public List<LambdaParameterSyntax>? Parameters { get; set; }
	public List<Token>? Commas { get; set; }
}

public class ArgumentListSyntax : Syntax
{
	public List<ArgumentSyntax>? Arguments { get; set; }
	public List<Token>? Commas { get; set; }
}

public class EnumValueSyntax : Syntax
{
	public Token? Identifier { get; set; }
	public Token? EqualsToken { get; set; }
	public ExpressionSyntax? Expression { get; set; }
}
