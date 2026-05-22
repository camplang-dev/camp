using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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

	static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
	{
		"abstract", "any", "as", "async", "auto", "bool", "break", "byte", "case", "catch",
		"char", "class", "const", "continue", "default", "delegate", "delete", "do", "double",
		"else", "enum", "escaped", "export", "extern", "false", "finally", "fixed", "float",
		"fn", "for", "foreach", "if", "implements", "in", "init", "int", "interface", "iter",
		"long", "new", "newtype", "nint", "null", "nuint", "once", "out", "override", "params",
		"return", "sbyte", "scoped", "sealed", "short", "sizeof", "static", "struct", "switch",
		"this", "thrown", "true", "try", "uchar", "uint", "ulong", "unscoped", "ushort",
		"using", "virtual", "void", "volatile", "vtableof", "wchar", "while", "within", "yield"
	};

	readonly List<AnalysisDiagnostic> diagnostics = [];
	readonly Dictionary<string, TypeDefinition> typeDefinitions = new(StringComparer.Ordinal);
	readonly Dictionary<TypeDefinition, TypeAnalysisInfo> typeInfos = [];
	readonly Dictionary<Expression, Expression> expressionRewrites = [];
	readonly Dictionary<TypeReference, TypeReference> typeRewrites = [];
	Module? currentModule;

	BindableNodeAnalyzer()
	{
	}

	public static AnalysisResult Analyze(Module module)
	{
		ArgumentNullException.ThrowIfNull(module);

		BindableNodeAnalyzer analyzer = new();
		analyzer.AnalyzeModule(module);
		analyzer.FillMissingResolvedTypes(module);
		return new AnalysisResult(module, analyzer.diagnostics);
	}





	bool TryGetNamedTypeDefinition(TypeReference type, out TypeDefinition? definition)
	{
		type = UnwrapTypeDeclarators(type);
		if (type is NamedTypeReference named)
			return TryGetNamedTypeDefinition(named, out definition);
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

	static TypeReference UnwrapTypeDeclarators(TypeReference type)
	{
		while (true)
		{
			type = type switch
			{
				ConstTypeReference { Type: not null } constType => constType.Type,
				VolatileTypeReference { Type: not null } volatileType => volatileType.Type,
				EscapedTypeReference { Type: not null } escapedType => escapedType.Type,
				ScopedTypeReference { Type: not null } scopedType => scopedType.Type,
				UnscopedTypeReference { Type: not null } unscopedType => unscopedType.Type,
				AttributedTypeReference { Type: not null } attributedType => attributedType.Type,
				_ => type
			};

			if (type is not ConstTypeReference and not VolatileTypeReference and not EscapedTypeReference and not ScopedTypeReference and not UnscopedTypeReference and not AttributedTypeReference)
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
			_ => IsDestructorFunction(function) ? "#DESTROY" : function.Name
		};
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
			AnalyzeExpression(argument, new AnalysisScope());
	}



	void CheckName(string? name, TokenRange? range, string symbolKind)
	{
		if (string.IsNullOrWhiteSpace(name))
			return;

		if (ReservedWords.Contains(name))
			Report(range, $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(symbolKind)} name '{name}' is reserved.");
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
			WithinParameterDefinition => AllocatorType,
			SizeOfParameterDefinition => "nuint",
			VTableOfParameterDefinition => VTableType,
			_ => ErrorType
		};
	}

	static bool IsUserNamedParameter(ParameterDefinition definition)
	{
		return definition is not ThisParameterDefinition
			and not SizeOfParameterDefinition
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
			yield return parameter.ResolvedType ?? ErrorType;
	}

	static string BuildAnchoredDeclarator(string keyword, List<string> anchors)
	{
		return anchors.Count == 0 ? keyword : $"{keyword}({string.Join(", ", anchors)})";
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
			_ => ErrorType
		};
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
	}
}
