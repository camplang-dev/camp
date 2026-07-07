using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public abstract class BindableNode
{
	public SyntaxNode? SourceSyntax { get; set; }
	public string? ResolvedType { get; set; }
	public string? SlotLifetimeFact { get; set; }
	public string? ValueLifetimeFact { get; set; }
	internal NodeProvenance? Provenance { get; set; }
}

public class Module : BindableNode
{
	public List<UsingDeclaration> Usings { get; } = [];
	public string? ExportAs { get; set; }
	public List<Definition> Definitions { get; } = [];
	public Dictionary<Definition, TokenSequence?> DefinitionSources { get; } = [];
	public Dictionary<TokenSequence, WithinAllocationPolicy> SourceWithinAllocationPolicies { get; } = [];
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
	public bool SymbolOverridden { get; set; }
	public string? Export { get; set; }
	public string? Public { get; set; }
	public string? Extern { get; set; }
	public bool IsApiHeader { get; set; }
	public TypeReference? OutOfScopeOwnerType { get; set; }
	public string? OutOfScopeOwnerName { get; set; }
	public string? OutOfScopeOwnerSymbol { get; set; }
	internal GeneratedDeclarationInfo? GeneratedInfo { get; set; }
}

internal enum GeneratedDeclarationCategory
{
	None,
	Lifecycle,
	Interface,
	Iterator,
	Lambda,
	VirtualDispatch,
	GenericCapability
}

internal sealed record GeneratedDeclarationInfo(GeneratedDeclarationCategory Category, string Reason, Definition? Source);

internal sealed record NodeProvenance(
	SyntaxNode? SourceSyntax,
	string? SourceSymbol,
	string? GeneratedReason,
	GeneratedDeclarationCategory Category = GeneratedDeclarationCategory.None,
	string? UserFacingVisibility = null);

public abstract class TypeDefinition : Definition
{
	public List<GenericParameter> GenericParameters { get; } = [];
}

public enum AliasTargetKind
{
	Unresolved,
	Type,
	Callable,
	CallSpec,
	TypeSpec
}

public class AliasDefinition : Definition
{
	public List<string> TargetQualifiers { get; } = [];
	public string TargetName { get; set; } = "";
	public AliasTargetKind TargetKind { get; set; }
	public string ResolvedTargetName { get; set; } = "";
}

public class ClassDefinition : TypeDefinition
{
	public ClassModifier Modifier { get; set; }
	public bool IsEscaped { get; set; }
	public List<TypeReference> BaseTypes { get; } = [];
	public List<TypeReference> LoweredInterfaceBaseTypes { get; } = [];
	public List<FieldDefinition> Fields { get; } = [];
	public List<FunctionDefinition> Functions { get; } = [];
}

public class StructDefinition : TypeDefinition
{
	public StructModifier Modifier { get; set; }
	public InterfaceDefinition? SourceInterface { get; set; }
	public List<TypeReference> BaseTypes { get; } = [];
	public List<TypeReference> LoweredInterfaceBaseTypes { get; } = [];
	public List<FieldDefinition> Fields { get; } = [];
	public List<FunctionDefinition> Functions { get; } = [];
}

public class InterfaceDefinition : TypeDefinition
{
	public bool IsEscaped { get; set; }
	public List<TypeReference> BaseTypes { get; } = [];
	public List<FunctionDefinition> Functions { get; } = [];
}

public class EnumDefinition : TypeDefinition
{
	public TypeReference? UnderlyingType { get; set; }
	public List<VariableDefinition> Values { get; } = [];
	public List<FunctionDefinition> Functions { get; } = [];
}

public abstract record ConstantValue
{
	public sealed record Integer(System.Numerics.BigInteger Value) : ConstantValue;
	public sealed record Boolean(bool Value) : ConstantValue;
	public sealed record String(string Value) : ConstantValue;
	public sealed record Character(string Value) : ConstantValue;
	public sealed record Null : ConstantValue;
}

public class NewtypeDefinition : TypeDefinition
{
	public IteratorKind IteratorKind { get; set; }
	public TypeReference? UnderlyingType { get; set; }
	public List<ParameterDefinition> Parameters { get; } = [];
	public List<FieldDefinition> Fields { get; } = [];
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
	public List<AttributeConstructor> Attributes { get; } = [];
	public string Name { get; set; } = "";
	public bool RequiresImplementation { get; set; }
	public TypeReference? Constraint { get; set; }
}

public class VariableDefinition : Definition
{
	public bool IsInline { get; set; }
	public bool IsFixedStorage { get; set; }
	public TypeReference? Type { get; set; }
	public Expression? InitialValue { get; set; }
	public ConstantValue? ConstantValue { get; set; }
}

public class FunctionDefinition : Definition
{
	public FunctionModifier Modifier { get; set; }
	public bool IsAsync { get; set; }
	public bool IsNoAwait { get; set; }
	public ParameterDefinition? AsyncResumerParameter { get; set; }
	public FunctionDefinition? AsyncResumeFunction { get; set; }
	public bool AsyncResumerIsReceiver { get; set; }
	public bool AsyncResumeFunctionIsAsync { get; set; }
	public IteratorKind IteratorKind { get; set; }
	public string? CallSpec { get; set; }
	public string InvokerName { get; set; } = "";
	public string FullCallableName { get; set; } = "";
	public TypeReference? ReturnType { get; set; }
	public TypeReference? CallableAscriptionType { get; set; }
	public NewtypeDefinition? CallableAscriptionNewtype { get; set; }
	public ThisParameterDefinition? EffectiveThisParameter { get; set; }
	public string? ReceiverLifetimeBinding { get; set; }
	public TypeReference? AbiThisType { get; set; }
	public TypeReference? ImplementationThisType { get; set; }
	public InterfaceSlotInitializerKind InterfaceSlotInitializerKind { get; set; }
	public Expression? InterfaceSlotInitializer { get; set; }
	public FunctionDefinition? InterfaceSlotInitializerTarget { get; set; }
	public List<GenericParameter> GenericParameters { get; } = [];
	public List<ParameterDefinition> Parameters { get; } = [];
	public List<UnaryExpression> AwaitSites { get; } = [];
	public BlockStatement? Body { get; set; }
}

public enum InterfaceSlotInitializerKind
{
	None,
	Null,
	Function
}

public class ParameterDefinition : Definition
{
	public ParameterModifier Modifier { get; set; }
	public bool IsOverloadSelector { get; set; }
	public bool IsAwaitWith { get; set; }
	public string? LifetimeBinding { get; set; }
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

public class NameOfParameterDefinition : ParameterDefinition
{
}

public class FieldDefinition : Definition
{
	public FieldModifier Modifier { get; set; }
	public bool IsInline { get; set; }
	public bool IsFixedStorage { get; set; }
	public string? LifetimeBinding { get; set; }
	public TypeReference? Type { get; set; }
	public Expression? InitialValue { get; set; }
	public ConstantValue? ConstantValue { get; set; }
}

public class AttributeConstructor : BindableNode
{
	public string Name { get; set; } = "";
	public List<ArgumentExpression> Arguments { get; } = [];
	public bool IsDocCommentAttribute { get; set; }
}

public abstract class TypeReference : BindableNode
{
	public string? LifetimeBinding { get; set; }
}

public class NamedTypeReference : TypeReference
{
	public List<string> Qualifiers { get; } = [];
	public string Name { get; set; } = "";
	public List<TypeReference> TypeArguments { get; } = [];
}

public class TypeDefinitionReference : TypeReference
{
	public string Name { get; set; } = "";
	public TypeDefinition? Definition { get; set; }
	public List<TypeReference> TypeArguments { get; } = [];
}

public class GenericParameterTypeReference : TypeReference
{
	public string Name { get; set; } = "";
	public GenericParameter? Parameter { get; set; }
}

public class AllocatorTypeReference : TypeReference
{
}

public class ClassTypeReference : TypeReference
{
	public ClassDefinition? Definition { get; set; }
}

public class ThisTypeReference : TypeReference
{
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

public class FixedArrayTypeReference : TypeReference
{
	public TypeReference? ElementType { get; set; }
	public Expression? LengthExpression { get; set; }
	public long? Length { get; set; }
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

public class ConstOfTypeReference : TypeReference
{
	public string AnchorName { get; set; } = "";
	public ParameterDefinition? Anchor { get; set; }
	public TypeReference? Type { get; set; }
}

public class VolatileTypeReference : TypeReference
{
	public TypeReference? Type { get; set; }
}

public class AnyTypeReference : TypeReference
{
}

public class CopyableTypeReference : TypeReference
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
	public string? CallSpec { get; set; }
	public string? TargetSpec { get; set; }
	public TypeReference? ReturnType { get; set; }
	public List<ParameterDefinition> Parameters { get; } = [];
}

public class RawFunctionPointerTypeReference : TypeReference
{
}

public class TargetTypeSpecTypeReference : TypeReference
{
	public string Specifier { get; set; } = "";
	public TypeReference? Type { get; set; }
	public bool IsPrefix { get; set; }
}

public class IterTypeReference : TypeReference
{
	public bool IsAsync { get; set; }
	public TypeReference? ElementType { get; set; }
	public List<ParameterDefinition> Parameters { get; } = [];
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
	Within,
	Upon
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
	String,
	WString,
	AString,
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
	UChar,
	Untyped
}
