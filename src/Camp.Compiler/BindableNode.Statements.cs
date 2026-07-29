using System.Collections.Generic;

namespace Camp.Compiler;

public abstract class Statement : BindableNode
{
}

public class BlockStatement : Statement
{
	public List<Statement> Statements { get; } = [];
}

public class EmptyStatement : Statement
{
}

public class ExpressionStatement : Statement
{
	public Expression? Expression { get; set; }
}

public class LiteralCopyStatement : Statement
{
	public Expression? Buffer { get; set; }
	public Expression? Offset { get; set; }
	public Expression? Count { get; set; }
	public string ElementType { get; set; } = "";
	public string LengthType { get; set; } = "";
	public string Text { get; set; } = "";
}

public class DeclarationStatement : Statement
{
	public bool IsFixedStorage { get; set; }
	public DeclarationTarget Target { get; } = new();
	public Expression? InitialValue { get; set; }
}

public class IfStatement : Statement
{
	public Expression? Condition { get; set; }
	public Statement? Body { get; set; }
	public Statement? ElseBody { get; set; }
}

public class WhileStatement : Statement
{
	public Expression? Condition { get; set; }
	public Statement? Body { get; set; }
}

public class DoWhileStatement : Statement
{
	public Statement? Body { get; set; }
	public Expression? Condition { get; set; }
}

public class ForStatement : Statement
{
	public ForStatementCondition Condition { get; } = new();
	public Statement? Body { get; set; }
}

public class ForeachStatement : Statement
{
	public bool IsAwaited { get; set; }
	public DeclarationTarget Target { get; } = new();
	public Expression? Source { get; set; }
	public FunctionDefinition? IteratorNext { get; set; }
	public Statement? Body { get; set; }
}

public class SwitchStatement : Statement
{
	public Expression? Expression { get; set; }
	public List<Statement> Statements { get; } = [];
}

public class CaseStatement : Statement
{
	public Expression? Expression { get; set; }
}

public class DefaultStatement : Statement
{
}

public class LabelStatement : Statement
{
	public string? Name { get; set; }
}

public class GotoStatement : Statement
{
	public string? TargetName { get; set; }
	public LabelStatement? Target { get; set; }
}

public class BreakStatement : Statement
{
}

public class ContinueStatement : Statement
{
}

public class ReturnStatement : Statement
{
	public Expression? Expression { get; set; }
	public bool SkipPendingCleanups { get; set; }
}

public class YieldStatement : Statement
{
	public bool IsBreak { get; set; }
	public Expression? Expression { get; set; }
}

public class DeleteStatement : Statement
{
	public bool IsDelegateCleanup { get; set; }
	public bool IsShadowCleanup { get; set; }
	public Expression? Expression { get; set; }
}

public class TryStatement : Statement
{
	public Statement? Body { get; set; }
	public List<CatchStatement> Catches { get; } = [];
	public FinallyStatement? Finally { get; set; }
}

public class CatchStatement : Statement
{
	public DeclarationTarget Target { get; } = new();
	public Statement? Body { get; set; }
}

public class FinallyStatement : Statement
{
	public Statement? Body { get; set; }
}

public class WithinStatement : Statement
{
	public Expression? Allocator { get; set; }
	public Statement? Body { get; set; }
}

public class DeclarationTarget : BindableNode
{
	public TypeReference? Type { get; set; }
	public List<string> Names { get; } = [];
}

public class ForStatementCondition : BindableNode
{
	public DeclarationStatement? Declaration { get; set; }
	public List<Expression?> Clauses { get; } = [];
}
