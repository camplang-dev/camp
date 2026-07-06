using System.Collections.Generic;

namespace Camp.Compiler;

public abstract class Expression : BindableNode
{
}

public class LiteralExpression : Expression
{
	public LiteralKind Kind { get; set; }
	public string Text { get; set; } = "";
	public object? Value { get; set; }
}

public class SymbolOfExpression : Expression
{
	public string Text { get; set; } = "";
	public BindableNode? Reference { get; set; }
}

public class NamedExpression : Expression
{
	public List<string> Qualifiers { get; } = [];
	public string Name { get; set; } = "";
}

public class VariableReferenceExpression : Expression
{
	public BindableNode? Variable { get; set; }
}

public class MethodReferenceExpression : Expression
{
	public List<FunctionDefinition> Candidates { get; } = [];
}

public class TypeReferenceExpression : Expression
{
	public TypeReference? Type { get; set; }
}

public class ThisExpression : Expression
{
}

public class DefaultExpression : Expression
{
	public TypeReference? Type { get; set; }
}

public class DefaultWithinContextExpression : Expression
{
}

public class GroupedExpression : Expression
{
	public List<GroupedExpressionItem> Items { get; } = [];
}

public class GroupedExpressionItem : BindableNode
{
	public string? Name { get; set; }
	public Expression? Expression { get; set; }
}

public class ArrayExpression : Expression
{
	public List<Expression> Elements { get; } = [];
}

public class InitializerExpression : Expression
{
	public List<InitializerItem> Items { get; } = [];
}

public class InitializerItem : BindableNode
{
	public InitializerTarget? Target { get; set; }
	public Expression? Expression { get; set; }
}

public class InitializerTarget : BindableNode
{
	public List<InitializerTargetPart> Parts { get; } = [];
}

public class InitializerTargetPart : BindableNode
{
	public string? Name { get; set; }
	public List<ArgumentExpression> Arguments { get; } = [];
}

public class ParenthesizedExpression : Expression
{
	public Expression? Expression { get; set; }
}

public class CastExpression : Expression
{
	public TypeReference? Type { get; set; }
	public string? LifetimeCastKind { get; set; }
	public List<string> LifetimeCastAnchors { get; } = [];
	public string? LifetimeBinding { get; set; }
	public bool Unsafe { get; set; }
	public CastKind Kind { get; set; }
	public Expression? Expression { get; set; }
}

public class ConstructionExpression : Expression
{
	public ConstructionKind Kind { get; set; }
	public TypeReference? Type { get; set; }
	public List<ArgumentExpression> Arguments { get; } = [];
	public Expression? ElementCount { get; set; }
	public InitializerExpression? Initializer { get; set; }
}

public class CurrentAllocatorExpression : Expression
{
}

public class StackAllocExpression : Expression
{
	public Expression? Size { get; set; }
}

public class WithinExpression : Expression
{
	public Expression? Context { get; set; }
	public Expression? Expression { get; set; }
}

public class SizeOfExpression : Expression
{
	public TypeReference? Type { get; set; }
}

public class VTableOfExpression : Expression
{
	public TypeReference? Type { get; set; }
	public TypeReference? InterfaceType { get; set; }
}

public class NameOfExpression : Expression
{
	public string Text { get; set; } = "";
	public string? Value { get; set; }
	public BindableNode? Reference { get; set; }
}

public class LambdaExpression : Expression
{
	public List<LambdaParameter> Parameters { get; } = [];
	public BlockStatement? Body { get; set; }
}

public class LambdaParameter : BindableNode
{
	public string? Name { get; set; }
	public ParameterDefinition? Parameter { get; set; }
}

public class ArgumentExpression : Expression
{
	public string? Name { get; set; }
	public ArgumentModifier Modifier { get; set; }
	public TypeReference? Type { get; set; }
	public DeclarationTarget? Target { get; set; }
	public Expression? Value { get; set; }
}

public class CallExpression : Expression
{
	public Expression? Target { get; set; }
	public List<TypeReference> TypeArguments { get; } = [];
	public List<ArgumentExpression> Arguments { get; } = [];
}

public class IndexExpression : Expression
{
	public Expression? Target { get; set; }
	public List<ArgumentExpression> Arguments { get; } = [];
}

public class MemberExpression : Expression
{
	public Expression? Target { get; set; }
	public string Name { get; set; } = "";
}

public class MemberReferenceExpression : Expression
{
	public Expression? Target { get; set; }
	public string Name { get; set; } = "";
	public BindableNode? Member { get; set; }
	public List<FunctionDefinition> Candidates { get; } = [];
}

public class NamelessIndexerExpression : Expression
{
	public Expression? Target { get; set; }
	public List<ArgumentExpression> Arguments { get; } = [];
}

public class UnaryExpression : Expression
{
	public UnaryOperator Operator { get; set; }
	public Expression? Operand { get; set; }
	public Expression? Context { get; set; }
}

public class PostfixUpdateExpression : Expression
{
	public Expression? Expression { get; set; }
	public UpdateOperator Operator { get; set; }
}

public class FinallyDeleteExpression : Expression
{
	public Expression? Expression { get; set; }
}

public class BinaryExpression : Expression
{
	public Expression? Left { get; set; }
	public BinaryOperator Operator { get; set; }
	public Expression? Right { get; set; }
}

public class AssignmentExpression : Expression
{
	public Expression? Target { get; set; }
	public AssignmentOperator Operator { get; set; }
	public Expression? Value { get; set; }
}

public class ConditionalExpression : Expression
{
	public Expression? Condition { get; set; }
	public Expression? WhenTrue { get; set; }
	public Expression? WhenFalse { get; set; }
}

public class RangeExpression : Expression
{
	public Expression? Start { get; set; }
	public Expression? End { get; set; }
}

public enum LiteralKind
{
	Number,
	String,
	Character,
	True,
	False,
	Null
}

public enum CastKind
{
	Type,
	Params,
	Struct,
	Class,
	Delegate,
	Function,
	Once,
	Iter,
	Async
}

public enum ConstructionKind
{
	Init,
	New
}

public enum UnaryOperator
{
	Plus,
	Minus,
	LogicalNot,
	BitwiseNot,
	AddressOf,
	PointerDereference,
	Increment,
	Decrement,
	FromEnd,
	Await,
	Postpone,
	Throw,
	Within
}

public enum UpdateOperator
{
	Increment,
	Decrement
}

public enum BinaryOperator
{
	LogicalOr,
	NullCoalescing,
	LogicalAnd,
	BitwiseOr,
	BitwiseXor,
	BitwiseAnd,
	Equal,
	NotEqual,
	LessThan,
	LessThanOrEqual,
	GreaterThan,
	GreaterThanOrEqual,
	LeftShift,
	RightShift,
	Add,
	Subtract,
	Multiply,
	Divide,
	Modulo
}

public enum AssignmentOperator
{
	Assign,
	Add,
	Subtract,
	Multiply,
	Divide,
	Modulo,
	BitwiseAnd,
	BitwiseOr,
	BitwiseXor,
	LeftShift,
	RightShift
}
