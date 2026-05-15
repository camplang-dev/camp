using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public abstract class BindableNode
{
	public SyntaxNode? SourceSyntax { get; set; }
}

public class Module : BindableNode
{
	public List<UsingDeclaration> Usings { get; } = [];
	public string? ExportAs { get; set; }
	public List<Definition> Definitions { get; } = [];
}

public class UsingDeclaration : BindableNode
{
	public string? Name { get; set; }
	public string? Alias { get; set; }
	public List<string> SelectedNames { get; } = [];
}

public abstract class Definition : BindableNode
{
	public List<AttributeConstructor> Attributes { get; } = [];
	public string Name { get; set; } = "";
	public string Symbol { get; set; } = "";
	public string? Export { get; set; }
	public string? Extern { get; set; }
}

public abstract class TypeDefinition : Definition
{
	public List<GenericParameter> GenericParameters { get; } = [];
}

public class ClassDefinition : TypeDefinition
{
	public ClassModifier Modifier { get; set; }
	public bool IsEscaped { get; set; }
	public List<TypeReference> BaseTypes { get; } = [];
	public List<FieldDefinition> Fields { get; } = [];
}

public class StructDefinition : TypeDefinition
{
	public StructModifier Modifier { get; set; }
	public List<TypeReference> BaseTypes { get; } = [];
	public List<FieldDefinition> Fields { get; } = [];
	public List<FunctionDefinition> Functions { get; } = [];
}

public class InterfaceDefinition : TypeDefinition
{
	public List<TypeReference> BaseTypes { get; } = [];
	public List<FunctionDefinition> Functions { get; } = [];
}

public class EnumDefinition : TypeDefinition
{
	public TypeReference? UnderlyingType { get; set; }
	public List<VariableDefinition> Values { get; } = [];
	public List<FunctionDefinition> Functions { get; } = [];
}

public class NewtypeDefinition : TypeDefinition
{
	public IteratorKind IteratorKind { get; set; }
	public TypeReference? UnderlyingType { get; set; }
	public List<ParameterDefinition> Parameters { get; } = [];
	public List<FunctionDefinition> Functions { get; } = [];
}

public class ParamsDefinition : TypeDefinition
{
	public TypeReference? UnderlyingType { get; set; }
	public List<ParameterDefinition> Components { get; } = [];
	public List<FunctionDefinition> Functions { get; } = [];
}

public class GenericParameter : BindableNode
{
	public string Name { get; set; } = "";
	public bool RequiresImplementation { get; set; }
	public TypeReference? Constraint { get; set; }
}

public class VariableDefinition : Definition
{
	public TypeReference? Type { get; set; }
	public Expression? InitialValue { get; set; }
}

public class FunctionDefinition : Definition
{
	public FunctionModifier Modifier { get; set; }
	public bool IsAsync { get; set; }
	public IteratorKind IteratorKind { get; set; }
	public TypeReference? ReturnType { get; set; }
	public List<GenericParameter> GenericParameters { get; } = [];
	public List<ParameterDefinition> Parameters { get; } = [];
	public FunctionBody? Body { get; set; }
}

public class ParameterDefinition : Definition
{
	public ParameterModifier Modifier { get; set; }
	public TypeReference? Type { get; set; }
	public Expression? DefaultValue { get; set; }
}

public class ThisParameterDefinition : ParameterDefinition
{
}

public class WithinParameterDefinition : ParameterDefinition
{
}

public class SizeOfParameterDefinition : ParameterDefinition
{
}

public class VTableOfParameterDefinition : ParameterDefinition
{
	public TypeReference? InterfaceType { get; set; }
}

public class FieldDefinition : Definition
{
	public FieldModifier Modifier { get; set; }
	public TypeReference? Type { get; set; }
	public Expression? InitialValue { get; set; }
}

public class AttributeConstructor : BindableNode
{
	public string Name { get; set; } = "";
	public List<ArgumentExpression> Arguments { get; } = [];
}

public abstract class FunctionBody : BindableNode
{
}

public class BlockFunctionBody : FunctionBody
{
	public List<Statement> Statements { get; } = [];
}

public class ExpressionFunctionBody : FunctionBody
{
	public Expression? Expression { get; set; }
}

public abstract class TypeReference : BindableNode
{
}

public class NamedTypeReference : TypeReference
{
	public List<string> Qualifiers { get; } = [];
	public string Name { get; set; } = "";
	public List<TypeReference> TypeArguments { get; } = [];
}

public class AttributedTypeReference : TypeReference
{
	public AttributeConstructor? Attribute { get; set; }
	public TypeReference? Type { get; set; }
}

public class GenericTypeReference : TypeReference
{
	public TypeReference? Type { get; set; }
	public List<TypeReference> TypeArguments { get; } = [];
}

public class ArrayTypeReference : TypeReference
{
	public TypeReference? ElementType { get; set; }
}

public class OptionalTypeReference : TypeReference
{
	public TypeReference? ElementType { get; set; }
}

public class PointerTypeReference : TypeReference
{
	public TypeReference? ElementType { get; set; }
}

public class ConstTypeReference : TypeReference
{
	public TypeReference? Type { get; set; }
}

public class VolatileTypeReference : TypeReference
{
	public TypeReference? Type { get; set; }
}

public class AnyTypeReference : TypeReference
{
}

public class AutoTypeReference : TypeReference
{
}

public class PrimitiveTypeReference : TypeReference
{
	public PrimitiveType Type { get; set; }
}

public class EscapedTypeReference : TypeReference
{
	public TypeReference? Type { get; set; }
}

public class ScopedTypeReference : TypeReference
{
	public List<string> Anchors { get; } = [];
	public TypeReference? Type { get; set; }
}

public class UnscopedTypeReference : TypeReference
{
	public List<string> Anchors { get; } = [];
	public TypeReference? Type { get; set; }
}

public class CallableTypeReference : TypeReference
{
	public CallableKind Kind { get; set; }
	public TypeReference? ReturnType { get; set; }
	public List<ParameterDefinition> Parameters { get; } = [];
}

public class IterTypeReference : TypeReference
{
	public TypeReference? ElementType { get; set; }
}

public class GroupedParamsTypeReference : TypeReference
{
	public TypeReference? StructType { get; set; }
}

public class MaterializedStructTypeReference : TypeReference
{
	public TypeReference? ParamsType { get; set; }
}

public class ThrownTypeReference : TypeReference
{
	public TypeReference? Type { get; set; }
}

public enum ClassModifier
{
	None = 0,
	Virtual = 1,
	Abstract = 2,
	Sealed = 4
}

public enum StructModifier
{
	None = 0,
	Fixed = 1
}

public enum FunctionModifier
{
	None,
	Virtual,
	Override,
	Abstract,
	Sealed,
	Static,
	Constructor,
	Destructor
}

public enum ParameterModifier
{
	None,
	In,
	Out,
	Thrown,
	Within
}

public enum FieldModifier
{
	None,
	Static
}

public enum ArgumentModifier
{
	None,
	Out,
	Catch
}

public enum CallableKind
{
	Function,
	Delegate,
	Async,
	Once
}

public enum IteratorKind
{
	None,
	Struct,
	Class
}

public enum PrimitiveType
{
	Void,
	Bool,
	Byte,
	SByte,
	UShort,
	Short,
	UInt,
	Int,
	ULong,
	Long,
	NUInt,
	NInt,
	Float,
	Double,
	Char,
	WChar,
	AChar,
	UChar
}
