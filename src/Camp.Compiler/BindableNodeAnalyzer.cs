using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Camp.Compiler;

public sealed record AnalysisDiagnostic(TokenRange? Range, string Message);

public sealed class AnalysisResult(Module Module, IReadOnlyList<AnalysisDiagnostic> Diagnostics)
{
	public Module Module { get; } = Module;
	public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; } = Diagnostics;
	public bool Success => Diagnostics.Count == 0;
}

public sealed partial class BindableNodeAnalyzer
{
	const string AttributeType = "#ATTRIBUTE";
	const string AllocatorType = "#ALLOCATOR";
	const string AutoType = "#AUTO";
	const string ConstructorType = "#CONSTRUCTOR";
	const string ErrorType = "#ERROR";
	const string MissingTypeName = "#MISSING";
	const string ModuleType = "#MODULE";
	const string RangeType = "#RANGE";
	const string TargetType = "#TARGET";
	const string ThisType = "#THIS";
	const string UnresolvedType = "#UNRESOLVED";
	const string UsingType = "#USING";
	const string VTableType = "#VTABLE";

	sealed record BoundLifetime(string Kind, IReadOnlyList<string> Anchors, string Source)
	{
		public override string ToString()
		{
			string anchorText = Anchors.Count == 0 ? "" : $"({string.Join(", ", Anchors)})";
			return string.IsNullOrWhiteSpace(Source)
				? Kind + anchorText
				: $"{Kind}{anchorText}:{Source}";
		}
	}

	static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
	{
		"_", "abstract", "alias", "any", "as", "astring", "async", "auto", "bool", "break", "byte", "case", "catch",
		"char", "class", "const", "constof", "continue", "copyable", "default", "delegate", "delete", "do", "double",
		"else", "enum", "escaped", "export", "extern", "false", "finally", "fixed", "float",
		"fn", "for", "foreach", "if", "implements", "in", "init", "int", "interface", "iter",
		"inline", "long", "new", "newtype", "nint", "null", "nuint", "once", "out", "overload", "override", "params", "public",
		"return", "sbyte", "scoped", "sealed", "short", "sizeof", "static", "string", "struct", "switch",
		"this", "thrown", "true", "try", "uchar", "uint", "ulong", "unscoped", "ushort", "untyped",
		"using", "virtual", "void", "volatile", "vtableof", "wchar", "while", "within", "wstring", "yield"
	};

	static readonly HashSet<string> CReservedWords = new(StringComparer.Ordinal)
	{
		"auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum",
		"extern", "float", "for", "goto", "if", "inline", "int", "long", "register", "restrict", "return",
		"short", "signed", "sizeof", "static", "struct", "switch", "typedef", "union", "unsigned", "void",
		"volatile", "while", "_Alignas", "_Alignof", "_Atomic", "_Bool", "_Complex", "_Generic", "_Imaginary",
		"_Noreturn", "_Static_assert", "_Thread_local", "alignas", "alignof", "atomic_bool", "atomic_char",
		"atomic_int", "atomic_long", "atomic_short", "atomic_uint", "atomic_ulong", "atomic_ushort", "bool",
		"complex", "constexpr", "false", "generic", "imaginary", "nullptr", "noreturn", "static_assert",
		"thread_local", "true", "typeof", "typeof_unqual"
	};

	readonly List<AnalysisDiagnostic> diagnostics = [];
	readonly Dictionary<string, TypeDefinition> typeDefinitions = new(StringComparer.Ordinal);
	readonly Dictionary<string, AliasDefinition> aliasDefinitions = new(StringComparer.Ordinal);
	readonly Dictionary<TypeDefinition, TypeAnalysisInfo> typeInfos = [];
	readonly Dictionary<Expression, Expression> expressionRewrites = [];
	readonly Dictionary<TypeReference, TypeReference> typeRewrites = [];
	readonly Dictionary<FunctionDefinition, ParameterDefinition> materializedGenericReturnParameters = [];
	readonly HashSet<NewtypeDefinition> analyzedNewtypeSignatures = [];
	readonly TargetDefinition? selectedTarget;
	readonly string? selectedMemoryModel;
	Module? currentModule;

	BindableNodeAnalyzer(TargetDefinition? selectedTarget = null, string? selectedMemoryModel = null)
	{
		this.selectedTarget = selectedTarget;
		this.selectedMemoryModel = selectedMemoryModel;
	}

	public static AnalysisResult Analyze(Module module, TargetDefinition? selectedTarget = null)
	{
		return Analyze(module, selectedTarget, selectedMemoryModel: null);
	}

	public static AnalysisResult Analyze(Module module, TargetDefinition? selectedTarget, string? selectedMemoryModel)
	{
		ArgumentNullException.ThrowIfNull(module);

		BindableNodeAnalyzer analyzer = new(selectedTarget, selectedMemoryModel);
		analyzer.AnalyzeModule(module);
		analyzer.FillMissingResolvedTypes(module);
		return new AnalysisResult(module, analyzer.diagnostics);
	}





	bool TryGetNamedTypeDefinition(TypeReference type, out TypeDefinition? definition)
	{
		type = UnwrapTypeDeclarators(type);
		if (type is NamedTypeReference named)
		{
			if (!TryGetNamedTypeDefinition(named, out definition))
				return false;
			if (definition is not null && !IsDefinitionVisible(definition, named.SourceSyntax))
			{
				definition = null;
				return false;
			}
			return true;
		}
		if (type is TypeDefinitionReference reference)
		{
			definition = reference.Definition;
			return definition is not null;
		}

		definition = null;
		return false;
	}

	bool TryGetNamedTypeDefinition(NamedTypeReference named, out TypeDefinition? definition)
	{
		if (named.Qualifiers.Count == 0)
			return typeDefinitions.TryGetValue(named.Name, out definition);

		definition = null;
		return false;
	}

	bool IsDefinitionVisible(Definition definition, SyntaxNode? referenceSyntax)
	{
		if (IsExternallyVisible(definition))
			return true;

		if (currentModule is null || !currentModule.DefinitionSources.TryGetValue(definition, out TokenSequence? definitionSource))
			return true;

		if (definitionSource is null)
			return true;

		TokenRange? referenceRange = GetRange(referenceSyntax);
		return referenceRange is not TokenRange range || ReferenceEquals(range.Sequence, definitionSource);
	}

	void ReportNotExported(Definition definition, SyntaxNode? referenceSyntax, string symbolKind)
	{
		Report(GetRange(referenceSyntax), $"{symbolKind} '{definition.Name}' is declared in another file but is not exported.");
	}

	bool IsDefinitionInSameFile(Definition definition, SyntaxNode? referenceSyntax)
	{
		if (currentModule is null || !currentModule.DefinitionSources.TryGetValue(definition, out TokenSequence? definitionSource))
			return true;

		if (definitionSource is null)
			return true;

		TokenRange? referenceRange = GetRange(referenceSyntax);
		return referenceRange is not TokenRange range || ReferenceEquals(range.Sequence, definitionSource);
	}

	bool IsMemberVisible(Definition member, TypeDefinition owner, SyntaxNode? referenceSyntax)
	{
		return IsExternallyVisible(member)
			|| member is FieldDefinition { Modifier: FieldModifier.None } && owner is StructDefinition { Export: not null }
			|| IsDefinitionInSameFile(owner, referenceSyntax);
	}

	void ReportMemberNotExported(Definition member, SyntaxNode? referenceSyntax)
	{
		Report(GetRange(referenceSyntax), $"Member '{member.Name}' is declared in another file but is not exported.");
	}

	static bool IsExternallyVisible(Definition definition)
	{
		return definition.Export is not null || definition.Public is not null;
	}

	static TypeReference UnwrapTypeDeclarators(TypeReference type)
	{
		while (true)
		{
			type = type switch
			{
				ConstTypeReference { Type: not null } constType => constType.Type,
				ConstOfTypeReference { Type: not null } constOfType => constOfType.Type,
				VolatileTypeReference { Type: not null } volatileType => volatileType.Type,
				EscapedTypeReference { Type: not null } escapedType => escapedType.Type,
				ScopedTypeReference { Type: not null } scopedType => scopedType.Type,
				UnscopedTypeReference { Type: not null } unscopedType => unscopedType.Type,
				TargetTypeSpecTypeReference { Type: not null } targetSpec => targetSpec.Type,
				AttributedTypeReference { Type: not null } attributedType => attributedType.Type,
				_ => type
			};

			if (type is not ConstTypeReference and not ConstOfTypeReference and not VolatileTypeReference and not EscapedTypeReference and not ScopedTypeReference and not UnscopedTypeReference and not TargetTypeSpecTypeReference and not AttributedTypeReference)
				return type;
		}
	}

	static MethodSignature BuildMethodSignature(FunctionDefinition function)
	{
		List<string> parameterTypes = [];
		string receiverContract = "";
		bool isLifecycleMember = function.Modifier == FunctionModifier.Constructor || IsDestructorFunction(function);

		for (int i = 0; i < function.Parameters.Count; i++)
		{
			ParameterDefinition parameter = function.Parameters[i];
			if (parameter is ThisParameterDefinition)
			{
				receiverContract = GetReceiverContract(parameter);
				continue;
			}

			if (isLifecycleMember && i == function.Parameters.Count - 1 && IsWithinParameter(parameter))
				continue;

			parameterTypes.Add($"{parameter.Modifier}:{parameter.ResolvedType ?? ErrorType}");
		}

		return new MethodSignature(GetSignatureName(function), GetSignatureReturnType(function), receiverContract, parameterTypes);
	}

	static string GetSignatureName(FunctionDefinition function)
	{
		return function.Modifier switch
		{
			FunctionModifier.Constructor => "#CREATE",
			_ => IsDestructorFunction(function) ? "#DESTROY" : GetCallableName(function)
		};
	}

	internal static string GetCallableName(FunctionDefinition function)
	{
		return string.IsNullOrWhiteSpace(function.FullCallableName) ? function.Name : function.FullCallableName;
	}

	static string GetSignatureReturnType(FunctionDefinition function)
	{
		return function.Modifier switch
		{
			FunctionModifier.Constructor => "#INSTANCE",
			_ => IsDestructorFunction(function) ? "void" : function.ResolvedType ?? ErrorType
		};
	}

	static bool IsDestructorFunction(FunctionDefinition function)
	{
		return function.Modifier == FunctionModifier.Destructor || function.Name.StartsWith("~", StringComparison.Ordinal);
	}

	static bool IsWithinParameter(ParameterDefinition parameter)
	{
		return parameter is WithinParameterDefinition || parameter.Modifier == ParameterModifier.Within;
	}

	static string GetReceiverContract(ParameterDefinition parameter)
	{
		if (parameter.SourceSyntax is not ThisParameterSyntax { Declarators: not null } thisParameter)
			return "";

		StringBuilder builder = new();
		foreach (TypeDeclaratorSyntax declarator in thisParameter.Declarators)
		{
			if (builder.Length > 0)
				builder.Append(' ');

			builder.Append(declarator.Keyword?.Value ?? "");
			if (declarator.AnchorList?.Identifiers is { Count: > 0 } anchors)
			{
				builder.Append('(');
				for (int i = 0; i < anchors.Count; i++)
				{
					if (i > 0)
						builder.Append(", ");
					builder.Append(anchors[i].Value);
				}
				builder.Append(')');
			}
		}

		return builder.ToString();
	}

	static bool IsIntegralPrimitive(PrimitiveType type)
	{
		return type is PrimitiveType.Byte
			or PrimitiveType.SByte
			or PrimitiveType.UShort
			or PrimitiveType.Short
			or PrimitiveType.UInt
			or PrimitiveType.Int
			or PrimitiveType.ULong
			or PrimitiveType.Long
			or PrimitiveType.NUInt
			or PrimitiveType.NInt
			or PrimitiveType.Char
			or PrimitiveType.WChar
			or PrimitiveType.AChar
			or PrimitiveType.UChar;
	}

	static string BuildNamedTypeSourceName(NamedTypeReference named)
	{
		return named.Qualifiers.Count == 0
			? named.Name
			: string.Join("::", named.Qualifiers) + "::" + named.Name;
	}

	static string AddTypeArguments(string typeName, List<TypeReference> arguments)
	{
		return arguments.Count == 0 ? typeName : $"{typeName}<{string.Join(", ", GetResolvedTypes(arguments))}>";
	}

	void AnalyzeAttribute(AttributeConstructor attribute)
	{
		attribute.ResolvedType = AttributeType;

		foreach (ArgumentExpression argument in attribute.Arguments)
		{
			if (argument.Value is null)
				argument.ResolvedType = ErrorType;
			else if (ValidateMetadataAttributeExpression(argument.Value))
			{
				argument.Value.ResolvedType = AttributeType;
				argument.ResolvedType = AttributeType;
			}
			else
			{
				AnalyzeExpression(argument.Value, new AnalysisScope());
				argument.Value.ResolvedType = AttributeType;
				argument.ResolvedType = AttributeType;
			}
		}
	}

	void AnalyzeAttributes(List<AttributeConstructor> attributes)
	{
		foreach (AttributeConstructor attribute in attributes)
		{
			if (attribute.ResolvedType != AttributeType)
				AnalyzeAttribute(attribute);
		}
	}

	bool ValidateMetadataAttributeExpression(Expression expression)
	{
		if (expression is SymbolOfExpression symbolOf)
		{
			if (!TryResolveMetadataSymbol(symbolOf.Text, out BindableNode? reference))
				Report(GetRange(symbolOf.SourceSyntax), $"symbolof reference '{symbolOf.Text}' could not be resolved.");
			symbolOf.Reference = reference;
			return true;
		}

		if (expression is ArrayExpression array && array.Elements.Count > 0 && array.Elements.All(static element => element is SymbolOfExpression))
		{
			foreach (Expression element in array.Elements)
				ValidateMetadataAttributeExpression(element);
			return true;
		}

		return false;
	}

	bool TryResolveMetadataSymbol(string text, out BindableNode? reference)
	{
		reference = null;
		string name = NormalizeMetadataSymbolName(text);
		if (name.Length == 0)
			return false;

		foreach (Definition definition in currentModule?.Definitions ?? [])
			if (TryResolveMetadataSymbolInDefinition(definition, name, out reference))
				return true;

		return false;
	}

	static bool TryResolveMetadataSymbolInDefinition(Definition definition, string name, out BindableNode? reference)
	{
		reference = null;
		if (definition.Name == name || definition.Symbol == name)
		{
			reference = definition;
			return true;
		}

		IEnumerable<Definition> children = definition switch
		{
			ClassDefinition classDefinition => classDefinition.Fields.Cast<Definition>().Concat(classDefinition.Functions),
			StructDefinition structDefinition => structDefinition.Fields.Cast<Definition>().Concat(structDefinition.Functions),
			InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
			EnumDefinition enumDefinition => enumDefinition.Values.Cast<Definition>().Concat(enumDefinition.Functions),
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Fields.Cast<Definition>().Concat(newtypeDefinition.Functions),
			ParamsDefinition paramsDefinition => paramsDefinition.Components.Cast<Definition>().Concat(paramsDefinition.Functions),
			FunctionDefinition functionDefinition => functionDefinition.GenericParameters.Cast<BindableNode>().Concat(functionDefinition.Parameters).OfType<Definition>(),
			_ => []
		};

		foreach (Definition child in children)
		{
			if (child.Name == name || child.Symbol == name)
			{
				reference = child;
				return true;
			}
			if (TryResolveMetadataSymbolInDefinition(child, name, out reference))
				return true;
		}

		if (definition is TypeDefinition typeDefinition)
		{
			foreach (GenericParameter parameter in typeDefinition.GenericParameters)
			{
				if (parameter.Name == name)
				{
					reference = parameter;
					return true;
				}
			}
		}

		return false;
	}

	static string NormalizeMetadataSymbolName(string text)
	{
		string name = text.Trim();
		int genericStart = name.IndexOf('<');
		if (genericStart >= 0)
			name = name[..genericStart];
		int namespaceStart = name.LastIndexOf("::", name.Length - 1, StringComparison.Ordinal);
		if (namespaceStart >= 0)
			name = name[(namespaceStart + 2)..];
		int memberStart = name.LastIndexOf('.');
		if (memberStart >= 0)
			name = name[(memberStart + 1)..];
		return name.Trim();
	}

	void ApplySymbolAttribute(Definition definition, bool allowed, string symbolKind)
	{
		foreach (AttributeConstructor attribute in definition.Attributes)
		{
			if (!IsSymbolAttribute(attribute))
				continue;

			if (!allowed)
			{
				Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), $"@symbol may not be applied to {symbolKind} declarations.");
				continue;
			}

			if (definition.SymbolOverridden)
				continue;

			if (attribute.Arguments.Count != 1)
			{
				Report(GetRange(attribute.SourceSyntax ?? definition.SourceSyntax), "@symbol requires exactly one string argument.");
				continue;
			}

			Expression? value = attribute.Arguments[0].Value;
			if (value is not LiteralExpression { Kind: LiteralKind.String, Value: string symbol })
			{
				Report(GetRange(value?.SourceSyntax ?? attribute.Arguments[0].SourceSyntax ?? attribute.SourceSyntax), "@symbol argument must be a string literal.");
				continue;
			}

			if (!IsIdentifier(symbol))
			{
				Report(GetRange(value.SourceSyntax ?? attribute.Arguments[0].SourceSyntax ?? attribute.SourceSyntax), $"@symbol value '{symbol}' is not a valid identifier.");
				continue;
			}

			if (ReservedWords.Contains(symbol))
			{
				Report(GetRange(value.SourceSyntax ?? attribute.Arguments[0].SourceSyntax ?? attribute.SourceSyntax), $"@symbol value '{symbol}' is a reserved Camp word.");
				continue;
			}

			if (CReservedWords.Contains(symbol))
			{
				Report(GetRange(value.SourceSyntax ?? attribute.Arguments[0].SourceSyntax ?? attribute.SourceSyntax), $"@symbol value '{symbol}' is a reserved C word.");
				continue;
			}

			definition.Symbol = symbol;
			definition.SymbolOverridden = true;
		}
	}

	static bool IsSymbolAttribute(AttributeConstructor attribute)
	{
		string name = attribute.Name.StartsWith("@", StringComparison.Ordinal) ? attribute.Name[1..] : attribute.Name;
		return name == "symbol";
	}



	void CheckName(string? name, TokenRange? range, string symbolKind)
	{
		if (string.IsNullOrWhiteSpace(name))
			return;

		if (ReservedWords.Contains(name))
		{
			Report(range, $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(symbolKind)} name '{name}' is reserved.");
			return;
		}

		if (CReservedWords.Contains(name))
			Report(range, $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(symbolKind)} name '{name}' is a reserved C word.");
	}

	void Report(TokenRange? range, string message)
	{
		diagnostics.Add(new AnalysisDiagnostic(range, message));
	}

	static string GetImplicitParameterType(ParameterDefinition definition)
	{
		return definition switch
		{
			ThisParameterDefinition => ThisType,
			WithinParameterDefinition => ErrorType,
			SizeOfParameterDefinition => "nuint",
			NameOfParameterDefinition => "string",
			VTableOfParameterDefinition => VTableType,
			_ => ErrorType
		};
	}

	static bool IsUserNamedParameter(ParameterDefinition definition)
	{
		return definition is not ThisParameterDefinition
			and not NameOfParameterDefinition
			and not VTableOfParameterDefinition;
	}

	static IEnumerable<string> GetResolvedTypes(IEnumerable<TypeReference> types)
	{
		foreach (TypeReference type in types)
			yield return type.ResolvedType ?? ErrorType;
	}

	static IEnumerable<string> GetParameterTypeNames(IEnumerable<ParameterDefinition> parameters)
	{
		foreach (ParameterDefinition parameter in parameters)
		{
			string type = GetParameterTypeName(parameter);
			yield return parameter.Modifier switch
			{
				ParameterModifier.In => "in " + type,
				ParameterModifier.Out => "out " + type,
				ParameterModifier.Thrown => "thrown " + type,
				ParameterModifier.Within => "within " + type,
				_ => type
			};
		}
	}

	static string GetParameterTypeName(ParameterDefinition parameter)
	{
		return parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? FormatTypeReference(parameter.Type) ?? ErrorType;
	}

	static string BuildAnchoredDeclarator(string keyword, List<string> anchors)
	{
		return anchors.Count == 0 ? keyword : $"{keyword}({string.Join(", ", anchors)})";
	}

	internal static string FormatTypeReference(TypeReference? type)
	{
		return type switch
		{
			null => ErrorType,
			TypeDefinitionReference definition => AddTypeArguments(definition.Name, definition.TypeArguments),
			GenericParameterTypeReference genericParameter => genericParameter.Name,
			AllocatorTypeReference => AllocatorType,
			NamedTypeReference named => named.ResolvedType ?? BuildNamedTypeSourceName(named),
			ClassTypeReference => "classtype",
			ThisTypeReference => "this",
			AttributedTypeReference attributed => FormatTypeReference(attributed.Type),
			GenericTypeReference generic => generic.TypeArguments.Count == 0
				? FormatTypeReference(generic.Type)
				: $"{FormatTypeReference(generic.Type)}<{string.Join(", ", GetResolvedTypes(generic.TypeArguments))}>",
			ArrayTypeReference array => $"{FormatTypeReference(array.ElementType)}[]",
			FixedArrayTypeReference fixedArray => FormatFixedArrayTypeReference(fixedArray),
			OptionalTypeReference optional => $"{FormatTypeReference(optional.ElementType)}?",
			PointerTypeReference pointer => $"{FormatTypeReference(pointer.ElementType)}*",
			ConstTypeReference constant => FormatTypeDeclarator("const", constant.Type),
			ConstOfTypeReference constOf => FormatTypeDeclarator("const", constOf.Type),
			VolatileTypeReference vol => FormatTypeDeclarator("volatile", vol.Type),
			AnyTypeReference => "any",
			CopyableTypeReference => "copyable",
			AutoTypeReference => AutoType,
			PrimitiveTypeReference primitive => GetPrimitiveTypeName(primitive.Type),
			EscapedTypeReference escaped => FormatTypeDeclarator("escaped", escaped.Type),
			ScopedTypeReference scoped => FormatTypeDeclarator(BuildAnchoredDeclarator("scoped", scoped.Anchors), scoped.Type),
			UnscopedTypeReference unscoped => FormatTypeDeclarator(BuildAnchoredDeclarator("unscoped", unscoped.Anchors), unscoped.Type),
			TargetTypeSpecTypeReference targetSpec => $"{FormatTypeReference(targetSpec.Type)} {targetSpec.Specifier}",
			CallableTypeReference callable => $"{GetCallableKindName(callable.Kind)}{FormatCallSpec(callable.TargetSpec)}{FormatCallSpec(callable.CallSpec)} {FormatTypeReference(callable.ReturnType)}({string.Join(", ", GetParameterTypeNames(callable.Parameters))})",
			IterTypeReference iter => FormatIterTypeReference(iter),
			GroupedParamsTypeReference grouped => $"params({FormatTypeReference(grouped.StructType)})",
			MaterializedStructTypeReference materialized => $"struct({FormatTypeReference(materialized.ParamsType)})",
			ThrownTypeReference thrown => $"thrown({FormatTypeReference(thrown.Type)})",
			_ => type.ResolvedType ?? ErrorType
		};
	}

	static string FormatFixedArrayTypeReference(FixedArrayTypeReference fixedArray)
	{
		List<string> lengths = [];
		TypeReference? element = fixedArray;
		while (element is FixedArrayTypeReference current)
		{
			lengths.Add(FormatFixedArrayLength(current));
			element = current.ElementType;
		}
		StringBuilder builder = new(FormatTypeReference(element));
		foreach (string length in lengths)
			builder.Append('[').Append(length).Append(']');
		return builder.ToString();
	}

	static string FormatIterTypeReference(IterTypeReference iter)
	{
		string prefix = iter.IsAsync ? "async iter" : "iter";
		if (iter.Parameters.Count == 0)
			return $"{prefix} {FormatTypeReference(iter.ElementType)}";

		List<string> slots = [];
		foreach (ParameterDefinition parameter in iter.Parameters)
		{
			string type = parameter.ResolvedType ?? FormatTypeReference(parameter.Type);
			slots.Add(parameter.Modifier == ParameterModifier.Thrown ? $"thrown {type}" : type);
		}
		return $"{prefix}({string.Join(", ", slots)})";
	}

	static string FormatFixedArrayLength(FixedArrayTypeReference fixedArray)
	{
		if (fixedArray.Length is long length)
			return length.ToString(System.Globalization.CultureInfo.InvariantCulture);
		return fixedArray.LengthExpression switch
		{
			LiteralExpression literal => literal.Text,
			UnaryExpression { Operator: UnaryOperator.Minus, Operand: LiteralExpression literal } => "-" + literal.Text,
			UnaryExpression { Operator: UnaryOperator.Plus, Operand: LiteralExpression literal } => "+" + literal.Text,
			_ => fixedArray.LengthExpression?.ResolvedType == "nuint" ? "0" : "?"
		};
	}

	static string FormatTypeDeclarator(string keyword, TypeReference? inner)
	{
		string innerText = FormatTypeReference(inner);
		return inner is CallableTypeReference
			? $"{keyword} {innerText}"
			: inner is PointerTypeReference or ArrayTypeReference or FixedArrayTypeReference or OptionalTypeReference or GenericTypeReference
			? $"{innerText} {keyword}"
			: $"{keyword} {innerText}";
	}

	static string FormatCallSpec(string? callSpec)
	{
		return string.IsNullOrWhiteSpace(callSpec) ? "" : " " + callSpec;
	}

	static string GetCallableKindName(CallableKind kind)
	{
		return kind switch
		{
			CallableKind.Function => "fn",
			CallableKind.Delegate => "delegate",
			CallableKind.Async => "async",
			CallableKind.Once => "once",
			_ => "fn"
		};
	}

	static string GetPrimitiveTypeName(PrimitiveType type)
	{
		return type switch
		{
			PrimitiveType.Void => "void",
			PrimitiveType.Bool => "bool",
			PrimitiveType.String => "string",
			PrimitiveType.WString => "wstring",
			PrimitiveType.AString => "astring",
			PrimitiveType.Byte => "byte",
			PrimitiveType.SByte => "sbyte",
			PrimitiveType.UShort => "ushort",
			PrimitiveType.Short => "short",
			PrimitiveType.UInt => "uint",
			PrimitiveType.Int => "int",
			PrimitiveType.ULong => "ulong",
			PrimitiveType.Long => "long",
			PrimitiveType.NUInt => "nuint",
			PrimitiveType.NInt => "nint",
			PrimitiveType.Float => "float",
			PrimitiveType.Double => "double",
			PrimitiveType.Char => "char",
			PrimitiveType.WChar => "wchar",
			PrimitiveType.AChar => "achar",
			PrimitiveType.UChar => "uchar",
			PrimitiveType.Untyped => "untyped",
			_ => ErrorType
		};
	}

	static bool TryGetPrimitiveType(string name, out PrimitiveType type)
	{
		foreach (PrimitiveType primitive in Enum.GetValues<PrimitiveType>())
		{
			if (GetPrimitiveTypeName(primitive) == name)
			{
				type = primitive;
				return true;
			}
		}

		type = default;
		return false;
	}

	bool TryResolveAlias(string name, AliasTargetKind kind, SyntaxNode? referenceSyntax, out AliasDefinition? alias)
	{
		alias = null;
		if (!aliasDefinitions.TryGetValue(name, out AliasDefinition? candidate))
			return false;
		if (!IsDefinitionVisible(candidate, referenceSyntax))
		{
			ReportNotExported(candidate, referenceSyntax, "Alias");
			return false;
		}
		if (candidate.TargetKind != kind)
			return false;
		alias = candidate;
		return true;
	}

	string ResolveCallSpecAlias(string callSpec, SyntaxNode? syntax)
	{
		return TryResolveAlias(callSpec, AliasTargetKind.CallSpec, syntax, out AliasDefinition? alias)
			? alias!.ResolvedTargetName
			: callSpec;
	}

	string ResolveTypeSpecAlias(string typeSpec, SyntaxNode? syntax)
	{
		return TryResolveAlias(typeSpec, AliasTargetKind.TypeSpec, syntax, out AliasDefinition? alias)
			? alias!.ResolvedTargetName
			: typeSpec;
	}



	sealed class TypeAnalysisInfo(TypeDefinition definition)
	{
		public TypeDefinition Definition { get; } = definition;
		public List<TypeDefinition> BaseTypes { get; } = [];
	}

	readonly record struct MethodSignature(string Name, string ReturnType, string ReceiverContract, List<string> ParameterTypes)
	{
		public string DisplayName => $"{Name}({string.Join(", ", ParameterTypes)})";

		public bool Equals(MethodSignature other)
		{
			if (Name != other.Name || ReturnType != other.ReturnType || ReceiverContract != other.ReceiverContract || ParameterTypes.Count != other.ParameterTypes.Count)
				return false;

			for (int i = 0; i < ParameterTypes.Count; i++)
			{
				if (ParameterTypes[i] != other.ParameterTypes[i])
					return false;
			}

			return true;
		}

		public override int GetHashCode()
		{
			HashCode hash = new();
			hash.Add(Name);
			hash.Add(ReturnType);
			hash.Add(ReceiverContract);
			foreach (string parameterType in ParameterTypes)
				hash.Add(parameterType);
			return hash.ToHashCode();
		}
	}

	sealed class AnalysisScope
	{
		readonly AnalysisScope? parent;

		public AnalysisScope()
		{
		}

		public AnalysisScope(AnalysisScope parent)
		{
			this.parent = parent;
		}

		public Dictionary<string, GenericParameter> GenericParameters { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, BindableNode> LifetimeAnchors { get; } = new(StringComparer.Ordinal);
		public TypeDefinition? ContainingType { get; set; }

		public bool ContainsGenericTypeName(string name)
		{
			return GenericParameters.ContainsKey(name) || (parent?.ContainsGenericTypeName(name) ?? false);
		}

		public bool TryGetGenericParameter(string name, out GenericParameter? parameter)
		{
			if (GenericParameters.TryGetValue(name, out parameter))
				return true;

			if (parent is not null)
				return parent.TryGetGenericParameter(name, out parameter);

			parameter = null;
			return false;
		}

		public TypeDefinition? GetContainingType()
		{
			return ContainingType ?? parent?.GetContainingType();
		}

		public void AddLifetimeAnchor(string name, BindableNode anchor)
		{
			if (!string.IsNullOrWhiteSpace(name))
				LifetimeAnchors[name] = anchor;
		}

		public bool ContainsLifetimeAnchor(string name)
		{
			return LifetimeAnchors.ContainsKey(name) || (parent?.ContainsLifetimeAnchor(name) ?? false);
		}
	}
}
