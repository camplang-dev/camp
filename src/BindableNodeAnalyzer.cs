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

	void AnalyzeModule(Module module)
	{
		currentModule = module;
		module.ResolvedType = ModuleType;
		CollectTypeNames(module);

		foreach (UsingDeclaration usingDeclaration in module.Usings)
		{
			usingDeclaration.ResolvedType = UsingType;
			CheckName(usingDeclaration.Alias, GetAliasRange(usingDeclaration.SourceSyntax), "using alias");
		}

		foreach (Definition definition in module.Definitions)
			AnalyzeDefinition(definition, new AnalysisScope());

		AnalyzeInheritance();
		AnalyzeImplementations();
		AnalyzeExportVisibility(module);
		ApplyNodeRewrites(module);
	}

	void CollectTypeNames(Module module)
	{
		foreach (Definition definition in module.Definitions)
		{
			if (definition is not TypeDefinition typeDefinition)
				continue;

			CheckName(typeDefinition.Name, GetNameRange(typeDefinition), "type");

			if (string.IsNullOrWhiteSpace(typeDefinition.Name))
				continue;

			if (!typeDefinitions.TryAdd(typeDefinition.Name, typeDefinition))
				Report(GetNameRange(typeDefinition), $"Duplicate type name '{typeDefinition.Name}'.");
			else
				typeInfos[typeDefinition] = new TypeAnalysisInfo(typeDefinition);
		}
	}

	void AnalyzeDefinition(Definition definition, AnalysisScope parentScope)
	{
		foreach (AttributeConstructor attribute in definition.Attributes)
			AnalyzeAttribute(attribute);

		switch (definition)
		{
			case ClassDefinition classDefinition:
				AnalyzeClassDefinition(classDefinition, parentScope);
				break;

			case StructDefinition structDefinition:
				AnalyzeStructDefinition(structDefinition, parentScope);
				break;

			case InterfaceDefinition interfaceDefinition:
				AnalyzeInterfaceDefinition(interfaceDefinition, parentScope);
				break;

			case EnumDefinition enumDefinition:
				AnalyzeEnumDefinition(enumDefinition, parentScope);
				break;

			case NewtypeDefinition newtypeDefinition:
				AnalyzeNewtypeDefinition(newtypeDefinition, parentScope);
				break;

			case ParamsDefinition paramsDefinition:
				AnalyzeParamsDefinition(paramsDefinition, parentScope);
				break;

			case VariableDefinition variableDefinition:
				AnalyzeVariableDefinition(variableDefinition, parentScope);
				break;

			case FunctionDefinition functionDefinition:
				AnalyzeFunctionDefinition(functionDefinition, parentScope, containingType: null);
				break;
		}
	}

	void AnalyzeClassDefinition(ClassDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeTypeList(definition.BaseTypes, scope);
		RegisterBaseTypes(definition, definition.BaseTypes);

		foreach (FieldDefinition field in definition.Fields)
			AnalyzeFieldDefinition(field, scope);

		ValidateDuplicateMethodNames(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
	}

	void AnalyzeStructDefinition(StructDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeTypeList(definition.BaseTypes, scope);
		RegisterBaseTypes(definition, definition.BaseTypes);

		foreach (FieldDefinition field in definition.Fields)
			AnalyzeFieldDefinition(field, scope);

		ValidateDuplicateMethodNames(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
	}

	void AnalyzeInterfaceDefinition(InterfaceDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeTypeList(definition.BaseTypes, scope);
		RegisterBaseTypes(definition, definition.BaseTypes);

		ValidateDuplicateMethodNames(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
	}

	void AnalyzeEnumDefinition(EnumDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeOptionalType(definition.UnderlyingType, scope);

		foreach (VariableDefinition value in definition.Values)
			AnalyzeVariableDefinition(value, scope);

		ValidateDuplicateMethodNames(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
	}

	void AnalyzeNewtypeDefinition(NewtypeDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeOptionalType(definition.UnderlyingType, scope);

		foreach (ParameterDefinition parameter in definition.Parameters)
			AnalyzeParameterDefinition(parameter, scope);

		ValidateDuplicateMethodNames(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
	}

	void AnalyzeParamsDefinition(ParamsDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = CreateTypeScope(definition, parentScope);
		definition.ResolvedType = definition.Name;
		AnalyzeGenericParameters(definition.GenericParameters, scope);
		AnalyzeOptionalType(definition.UnderlyingType, scope);

		foreach (ParameterDefinition component in definition.Components)
			AnalyzeParameterDefinition(component, scope);

		ValidateDuplicateMethodNames(definition.Functions);
		foreach (FunctionDefinition function in definition.Functions)
			AnalyzeFunctionDefinition(function, scope, definition.Name);
	}

	void ValidateDuplicateMethodNames(List<FunctionDefinition> functions)
	{
		HashSet<string> names = new(StringComparer.Ordinal);
		foreach (FunctionDefinition function in functions)
		{
			string name = function.Name;
			if (string.IsNullOrWhiteSpace(name))
				continue;

			if (!names.Add(name))
				Report(GetNameRange(function), $"Duplicate method name '{name}'.");
		}
	}

	AnalysisScope CreateTypeScope(TypeDefinition definition, AnalysisScope parentScope)
	{
		AnalysisScope scope = new(parentScope);
		foreach (GenericParameter parameter in definition.GenericParameters)
			scope.GenericParameters[parameter.Name] = parameter;

		return scope;
	}

	void AnalyzeGenericParameters(List<GenericParameter> parameters, AnalysisScope scope)
	{
		HashSet<string> names = new(StringComparer.Ordinal);
		foreach (GenericParameter parameter in parameters)
		{
			parameter.ResolvedType = parameter.Name;
			CheckName(parameter.Name, GetGenericParameterNameRange(parameter.SourceSyntax), "generic parameter");

			if (!string.IsNullOrWhiteSpace(parameter.Name) && !names.Add(parameter.Name))
				Report(GetGenericParameterNameRange(parameter.SourceSyntax), $"Duplicate generic parameter name '{parameter.Name}'.");

			AnalyzeOptionalType(parameter.Constraint, scope);
			ValidateGenericParameterConstraint(parameter);
		}
	}

	void AnalyzeVariableDefinition(VariableDefinition definition, AnalysisScope scope)
	{
		CheckName(definition.Name, GetNameRange(definition), "variable");
		AnalyzeOptionalType(definition.Type, scope);
		definition.ResolvedType = definition.Type?.ResolvedType ?? ErrorType;
		AnalyzeOptionalExpression(definition.InitialValue, scope);
	}

	void AnalyzeFieldDefinition(FieldDefinition definition, AnalysisScope scope)
	{
		CheckName(definition.Name, GetNameRange(definition), "field");
		AnalyzeOptionalType(definition.Type, scope);
		definition.ResolvedType = definition.Type?.ResolvedType ?? ErrorType;
		AnalyzeOptionalExpression(definition.InitialValue, scope);
	}

	void AnalyzeFunctionDefinition(FunctionDefinition definition, AnalysisScope parentScope, string? containingType)
	{
		CheckName(definition.Name.TrimStart('~'), GetNameRange(definition), "function");

		AnalysisScope scope = new(parentScope);
		foreach (GenericParameter parameter in definition.GenericParameters)
			scope.GenericParameters[parameter.Name] = parameter;

		AnalyzeGenericParameters(definition.GenericParameters, scope);

		if (definition.Modifier == FunctionModifier.Constructor)
			definition.ResolvedType = containingType ?? ConstructorType;
		else if (IsDestructorFunction(definition))
			definition.ResolvedType = "void";
		else
			definition.ResolvedType = AnalyzeOptionalType(definition.ReturnType, scope) ?? ErrorType;

		ValidateFunctionModifiers(definition);
		ValidateGenericArgumentUse(definition.ReturnType);

		foreach (ParameterDefinition parameter in definition.Parameters)
			AnalyzeParameterDefinition(parameter, scope);

		AnalyzeMethodBody(definition, scope, FindContainingType(definition));
	}

	void AnalyzeParameterDefinition(ParameterDefinition definition, AnalysisScope scope)
	{
		if (IsUserNamedParameter(definition))
			CheckName(definition.Name, GetNameRange(definition), "parameter");

		AnalyzeOptionalType(definition.Type, scope);

		if (definition is VTableOfParameterDefinition vtableOf)
			AnalyzeOptionalType(vtableOf.InterfaceType, scope);

		definition.ResolvedType = definition.Type?.ResolvedType ?? GetImplicitParameterType(definition);
		ValidateGenericArgumentUse(definition.Type);
		ValidateParameterPassing(definition, scope);
		AnalyzeConstantExpression(definition.DefaultValue, scope, "Parameter default value");
	}

	string? AnalyzeOptionalType(TypeReference? type, AnalysisScope scope)
	{
		if (type is null)
			return null;

		AnalyzeType(type, scope);
		return type.ResolvedType;
	}

	void AnalyzeTypeList(List<TypeReference> types, AnalysisScope scope)
	{
		foreach (TypeReference type in types)
			AnalyzeType(type, scope);
	}

	void AnalyzeType(TypeReference type, AnalysisScope scope)
	{
		switch (type)
		{
			case TypeDefinitionReference definition:
				AnalyzeTypeList(definition.TypeArguments, scope);
				type.ResolvedType = AddTypeArguments(definition.Name, definition.TypeArguments);
				break;

			case GenericParameterTypeReference genericParameter:
				type.ResolvedType = genericParameter.Name;
				break;

			case AllocatorTypeReference:
				type.ResolvedType = AllocatorType;
				break;

			case NamedTypeReference named:
				foreach (TypeReference argument in named.TypeArguments)
					AnalyzeType(argument, scope);

				type.ResolvedType = ResolveNamedType(named, scope);
				ValidateGenericArgumentUse(named);
				break;

			case AttributedTypeReference attributed:
				AnalyzeOptionalType(attributed.Type, scope);
				if (attributed.Attribute is not null)
					AnalyzeAttribute(attributed.Attribute);
				type.ResolvedType = attributed.Type?.ResolvedType ?? ErrorType;
				break;

			case GenericTypeReference generic:
				AnalyzeOptionalType(generic.Type, scope);
				AnalyzeTypeList(generic.TypeArguments, scope);
				type.ResolvedType = $"{generic.Type?.ResolvedType ?? ErrorType}<{string.Join(", ", GetResolvedTypes(generic.TypeArguments))}>";
				break;

			case ArrayTypeReference array:
				AnalyzeOptionalType(array.ElementType, scope);
				type.ResolvedType = $"{array.ElementType?.ResolvedType ?? ErrorType}[]";
				break;

			case OptionalTypeReference optional:
				AnalyzeOptionalType(optional.ElementType, scope);
				type.ResolvedType = $"{optional.ElementType?.ResolvedType ?? ErrorType}?";
				break;

			case PointerTypeReference pointer:
				AnalyzeOptionalType(pointer.ElementType, scope);
				type.ResolvedType = $"{pointer.ElementType?.ResolvedType ?? ErrorType}*";
				break;

			case ConstTypeReference constType:
				AnalyzeOptionalType(constType.Type, scope);
				type.ResolvedType = $"const {constType.Type?.ResolvedType ?? ErrorType}";
				break;

			case VolatileTypeReference volatileType:
				AnalyzeOptionalType(volatileType.Type, scope);
				type.ResolvedType = $"volatile {volatileType.Type?.ResolvedType ?? ErrorType}";
				break;

			case AnyTypeReference:
				type.ResolvedType = "any";
				break;

			case AutoTypeReference:
				type.ResolvedType = AutoType;
				break;

			case PrimitiveTypeReference primitive:
				type.ResolvedType = GetPrimitiveTypeName(primitive.Type);
				break;

			case EscapedTypeReference escaped:
				AnalyzeOptionalType(escaped.Type, scope);
				type.ResolvedType = $"escaped {escaped.Type?.ResolvedType ?? ErrorType}";
				break;

			case ScopedTypeReference scoped:
				AnalyzeOptionalType(scoped.Type, scope);
				type.ResolvedType = $"{BuildAnchoredDeclarator("scoped", scoped.Anchors)} {scoped.Type?.ResolvedType ?? ErrorType}";
				break;

			case UnscopedTypeReference unscoped:
				AnalyzeOptionalType(unscoped.Type, scope);
				type.ResolvedType = $"{BuildAnchoredDeclarator("unscoped", unscoped.Anchors)} {unscoped.Type?.ResolvedType ?? ErrorType}";
				break;

			case CallableTypeReference callable:
				AnalyzeOptionalType(callable.ReturnType, scope);
				foreach (ParameterDefinition parameter in callable.Parameters)
					AnalyzeParameterDefinition(parameter, scope);
				type.ResolvedType = $"{GetCallableKindName(callable.Kind)} {callable.ReturnType?.ResolvedType ?? ErrorType}({string.Join(", ", GetParameterTypeNames(callable.Parameters))})";
				break;

			case IterTypeReference iter:
				AnalyzeOptionalType(iter.ElementType, scope);
				type.ResolvedType = $"iter {iter.ElementType?.ResolvedType ?? ErrorType}";
				break;

			case GroupedParamsTypeReference grouped:
				AnalyzeOptionalType(grouped.StructType, scope);
				type.ResolvedType = $"params({grouped.StructType?.ResolvedType ?? ErrorType})";
				break;

			case MaterializedStructTypeReference materialized:
				AnalyzeOptionalType(materialized.ParamsType, scope);
				type.ResolvedType = $"struct({materialized.ParamsType?.ResolvedType ?? ErrorType})";
				break;

			case ThrownTypeReference thrown:
				AnalyzeOptionalType(thrown.Type, scope);
				type.ResolvedType = $"thrown({thrown.Type?.ResolvedType ?? ErrorType})";
				break;

			default:
				type.ResolvedType = ErrorType;
				break;
		}
	}

	string ResolveNamedType(NamedTypeReference named, AnalysisScope scope)
	{
		string sourceName = BuildNamedTypeSourceName(named);

		if (named.Qualifiers.Count == 0 && scope.TryGetGenericParameter(named.Name, out GenericParameter? genericParameter))
		{
			string resolvedType = AddTypeArguments(named.Name, named.TypeArguments);
			typeRewrites[named] = new GenericParameterTypeReference
			{
				SourceSyntax = named.SourceSyntax,
				Name = named.Name,
				Parameter = genericParameter,
				ResolvedType = resolvedType
			};
			return resolvedType;
		}

		if (named.Qualifiers.Count == 0 && typeDefinitions.TryGetValue(named.Name, out TypeDefinition? definition))
		{
			ValidateGenericArity(named, definition);
			string resolvedType = AddTypeArguments(named.Name, named.TypeArguments);
			TypeDefinitionReference reference = new()
			{
				SourceSyntax = named.SourceSyntax,
				Name = named.Name,
				Definition = definition,
				ResolvedType = resolvedType
			};
			foreach (TypeReference argument in named.TypeArguments)
				reference.TypeArguments.Add(argument);
			typeRewrites[named] = reference;
			return resolvedType;
		}

		if (named.Name == "<missing>")
			return MissingTypeName;

		if (named.Qualifiers.Count == 0 && named.Name == "Allocator")
			return "Allocator";

		Report(GetRange(named.SourceSyntax), $"Unknown type '{sourceName}'.");
		return $"{UnresolvedType}({sourceName})";
	}

	void RegisterBaseTypes(TypeDefinition owner, List<TypeReference> baseTypes)
	{
		if (!typeInfos.TryGetValue(owner, out TypeAnalysisInfo? info))
			return;

		foreach (TypeReference baseType in baseTypes)
		{
			if (TryGetNamedTypeDefinition(baseType, out TypeDefinition? definition) && definition is not null)
				info.BaseTypes.Add(definition);
		}
	}

	void AnalyzeInheritance()
	{
		foreach (TypeAnalysisInfo info in typeInfos.Values)
		{
			switch (info.Definition)
			{
				case ClassDefinition classDefinition:
					AnalyzeClassOrStructBaseTypes(classDefinition, classDefinition.BaseTypes, "Class");
					break;

				case StructDefinition structDefinition:
					AnalyzeClassOrStructBaseTypes(structDefinition, structDefinition.BaseTypes, "Struct");
					break;

				case InterfaceDefinition interfaceDefinition:
					AnalyzeInterfaceBaseTypes(interfaceDefinition);
					break;
			}
		}

		foreach (TypeAnalysisInfo info in typeInfos.Values)
		{
			if (info.Definition is ClassDefinition or StructDefinition)
				CheckCircularClassInheritance(info.Definition, []);
		}
	}

	void AnalyzeClassOrStructBaseTypes(TypeDefinition owner, List<TypeReference> baseTypes, string ownerKind)
	{
		int baseClassCount = 0;

		foreach (TypeReference baseType in baseTypes)
		{
			if (!TryGetNamedTypeDefinition(baseType, out TypeDefinition? definition))
				continue;

			switch (definition)
			{
				case ClassDefinition:
					baseClassCount++;
					if (baseClassCount > 1)
						Report(GetRange(baseType.SourceSyntax), $"{ownerKind} '{owner.Name}' may only declare one base class.");
					break;

				case InterfaceDefinition:
					break;

				default:
					Report(GetRange(baseType.SourceSyntax), $"{ownerKind} '{owner.Name}' may only derive from classes or implement interfaces.");
					break;
			}
		}
	}

	void AnalyzeInterfaceBaseTypes(InterfaceDefinition owner)
	{
		foreach (TypeReference baseType in owner.BaseTypes)
		{
			if (TryGetNamedTypeDefinition(baseType, out TypeDefinition? definition) && definition is not InterfaceDefinition)
				Report(GetRange(baseType.SourceSyntax), $"Interface '{owner.Name}' may only derive from interfaces.");
		}
	}

	void CheckCircularClassInheritance(TypeDefinition definition, HashSet<TypeDefinition> path)
	{
		if (!path.Add(definition))
		{
			Report(GetNameRange(definition), $"Circular inheritance involving '{definition.Name}'.");
			return;
		}

		foreach (TypeDefinition baseType in GetDirectBaseClasses(definition))
			CheckCircularClassInheritance(baseType, path);

		path.Remove(definition);
	}

	IEnumerable<TypeDefinition> GetDirectBaseClasses(TypeDefinition definition)
	{
		if (!typeInfos.TryGetValue(definition, out TypeAnalysisInfo? info))
			yield break;

		foreach (TypeDefinition baseType in info.BaseTypes)
		{
			if (baseType is ClassDefinition)
				yield return baseType;
		}
	}

	void AnalyzeImplementations()
	{
		foreach (TypeAnalysisInfo info in typeInfos.Values)
		{
			if (info.Definition is ClassDefinition classDefinition)
				AnalyzeClassImplementations(classDefinition);
			else if (info.Definition is StructDefinition structDefinition)
				AnalyzeStructImplementations(structDefinition);
		}
	}

	void AnalyzeClassImplementations(ClassDefinition definition)
	{
		ValidateClassVirtualMethods(definition);
		ValidateInheritedMethodNames(definition);

		List<MethodSignature> available = GetClassMethodSignatures(definition);

		foreach (InterfaceDefinition interfaceDefinition in GetImplementedInterfaces(definition))
			EnsureInterfaceImplemented(definition, interfaceDefinition, available);

		foreach (FunctionDefinition abstractMethod in GetInheritedAbstractMethods(definition))
		{
			MethodSignature signature = BuildMethodSignature(abstractMethod);
			if (!ContainsOverrideSignature(definition.Functions, signature))
				Report(GetNameRange(definition), $"Class '{definition.Name}' must use override to implement inherited abstract member '{signature.DisplayName}'.");
		}
	}

	void AnalyzeStructImplementations(StructDefinition definition)
	{
		foreach (FunctionDefinition function in definition.Functions)
		{
			if (function.Modifier is FunctionModifier.Virtual or FunctionModifier.Abstract or FunctionModifier.Override or FunctionModifier.Sealed)
				Report(GetNameRange(function), "Struct methods may not be virtual, abstract, override, or sealed.");
		}

		List<MethodSignature> available = GetStructMethodSignatures(definition);

		foreach (InterfaceDefinition interfaceDefinition in GetImplementedInterfaces(definition))
			EnsureInterfaceImplemented(definition, interfaceDefinition, available);
	}

	void EnsureInterfaceImplemented(TypeDefinition implementation, InterfaceDefinition interfaceDefinition, List<MethodSignature> available)
	{
		if (InterfaceRequiresConstructor(interfaceDefinition) && implementation is ClassDefinition { Modifier: not ClassModifier.Sealed })
			Report(GetNameRange(implementation), $"Interface '{interfaceDefinition.Name}' declares a constructor and may only be implemented by a sealed class or a struct.");

		foreach (FunctionDefinition member in GetInterfaceMembers(interfaceDefinition))
		{
			MethodSignature required = BuildMethodSignature(member);
			if (!ContainsSignature(available, required))
				Report(GetNameRange(implementation), $"Type '{implementation.Name}' does not implement interface member '{interfaceDefinition.Name}.{required.DisplayName}'.");
		}
	}

	bool InterfaceRequiresConstructor(InterfaceDefinition definition)
	{
		foreach (FunctionDefinition member in GetInterfaceMembers(definition))
		{
			if (member.Modifier == FunctionModifier.Constructor)
				return true;
		}

		return false;
	}

	void ValidateClassVirtualMethods(ClassDefinition definition)
	{
		if (InheritsVirtualClass(definition) && definition.Modifier is not ClassModifier.Virtual and not ClassModifier.Abstract and not ClassModifier.Sealed)
			Report(GetNameRange(definition), $"Class '{definition.Name}' derives from a virtual or abstract class and must be declared virtual, abstract, or sealed.");

		foreach (FunctionDefinition function in definition.Functions)
		{
			if (function.Modifier == FunctionModifier.Virtual && definition.Modifier is not ClassModifier.Virtual and not ClassModifier.Abstract)
				Report(GetNameRange(function), "Virtual methods may only be declared in virtual or abstract classes.");

			if (function.Modifier == FunctionModifier.Abstract && definition.Modifier != ClassModifier.Abstract)
				Report(GetNameRange(function), "Abstract methods may only be declared in abstract classes.");

			if (function.Modifier is FunctionModifier.Override or FunctionModifier.Sealed)
				ValidateOverrideMethod(definition, function);
		}
	}

	bool InheritsVirtualClass(ClassDefinition definition)
	{
		foreach (TypeDefinition baseClass in GetDirectBaseClasses(definition))
		{
			if (baseClass is ClassDefinition { Modifier: ClassModifier.Virtual or ClassModifier.Abstract })
				return true;
		}

		return false;
	}

	void ValidateOverrideMethod(ClassDefinition owner, FunctionDefinition function)
	{
		MethodSignature signature = BuildMethodSignature(function);
		foreach (FunctionDefinition abstractMethod in GetInheritedAbstractMethods(owner))
		{
			if (BuildMethodSignature(abstractMethod).Equals(signature))
				return;
		}

		if (IsDestructorFunction(function))
		{
			foreach (FunctionDefinition inherited in GetInheritedClassMethods(owner))
			{
				if (IsDestructorFunction(inherited)
					&& inherited.Modifier is FunctionModifier.Virtual or FunctionModifier.Abstract
					&& BuildMethodSignature(inherited).Equals(signature))
					return;
			}
		}

		if (function.Name == DeleteMethodName)
		{
			foreach (FunctionDefinition inherited in GetInheritedClassMethods(owner))
			{
				if (inherited.Name == DeleteMethodName
					&& inherited.Modifier is FunctionModifier.Virtual or FunctionModifier.Abstract
					&& BuildMethodSignature(inherited).Equals(signature))
					return;
			}
		}

		Report(GetNameRange(function), $"{function.Modifier} method '{function.Name}' must match an inherited abstract method.");
	}

	void ValidateInheritedMethodNames(ClassDefinition definition)
	{
		foreach (FunctionDefinition function in definition.Functions)
		{
			if (function.Modifier is FunctionModifier.Constructor)
				continue;
			if (IsGeneratedLifecycleMethodName(function.Name))
				continue;

			MethodSignature signature = BuildMethodSignature(function);
			foreach (FunctionDefinition inherited in GetInheritedClassMethods(definition))
			{
				MethodSignature inheritedSignature = BuildMethodSignature(inherited);
				if (!SameMethodIdentity(signature, inheritedSignature))
					continue;

				if (function.Modifier is FunctionModifier.Override or FunctionModifier.Sealed)
					continue;

				Report(GetNameRange(function), $"Duplicate method name '{GetDuplicateMethodDisplayName(signature)}' inherited from base class.");
				break;
			}
		}
	}

	IEnumerable<FunctionDefinition> GetInheritedClassMethods(ClassDefinition definition)
	{
		return GetInheritedClassMethods(definition, []);
	}

	IEnumerable<FunctionDefinition> GetInheritedClassMethods(ClassDefinition definition, HashSet<ClassDefinition> seen)
	{
		foreach (TypeDefinition baseClass in GetDirectBaseClasses(definition))
		{
			if (baseClass is not ClassDefinition classDefinition || !seen.Add(classDefinition))
				continue;

			foreach (FunctionDefinition function in classDefinition.Functions)
				yield return function;

			foreach (FunctionDefinition inherited in GetInheritedClassMethods(classDefinition, seen))
				yield return inherited;
		}
	}

	static bool SameMethodIdentity(MethodSignature left, MethodSignature right)
	{
		if (left.Name != right.Name || left.ParameterTypes.Count != right.ParameterTypes.Count)
			return false;

		for (int i = 0; i < left.ParameterTypes.Count; i++)
		{
			if (left.ParameterTypes[i] != right.ParameterTypes[i])
				return false;
		}
		return true;
	}

	static string GetDuplicateMethodDisplayName(MethodSignature signature)
	{
		return signature.Name == "#DESTROY" ? DestroyMethodName : signature.DisplayName;
	}

	IEnumerable<InterfaceDefinition> GetImplementedInterfaces(TypeDefinition definition)
	{
		if (!typeInfos.TryGetValue(definition, out TypeAnalysisInfo? info))
			yield break;

		foreach (TypeDefinition baseType in info.BaseTypes)
		{
			if (baseType is InterfaceDefinition interfaceDefinition)
			{
				yield return interfaceDefinition;
				foreach (InterfaceDefinition inherited in GetBaseInterfaces(interfaceDefinition))
					yield return inherited;
			}
		}
	}

	IEnumerable<InterfaceDefinition> GetBaseInterfaces(InterfaceDefinition definition)
	{
		return GetBaseInterfaces(definition, []);
	}

	IEnumerable<InterfaceDefinition> GetBaseInterfaces(InterfaceDefinition definition, HashSet<InterfaceDefinition> seen)
	{
		if (!typeInfos.TryGetValue(definition, out TypeAnalysisInfo? info))
			yield break;

		foreach (TypeDefinition baseType in info.BaseTypes)
		{
			if (baseType is InterfaceDefinition interfaceDefinition)
			{
				if (!seen.Add(interfaceDefinition))
					continue;

				yield return interfaceDefinition;
				foreach (InterfaceDefinition inherited in GetBaseInterfaces(interfaceDefinition, seen))
					yield return inherited;
			}
		}
	}

	IEnumerable<FunctionDefinition> GetInterfaceMembers(InterfaceDefinition definition)
	{
		foreach (InterfaceDefinition baseInterface in GetBaseInterfaces(definition))
		{
			foreach (FunctionDefinition function in baseInterface.Functions)
				yield return function;
		}

		foreach (FunctionDefinition function in definition.Functions)
			yield return function;
	}

	IEnumerable<FunctionDefinition> GetInheritedAbstractMethods(ClassDefinition definition)
	{
		return GetInheritedAbstractMethods(definition, []);
	}

	IEnumerable<FunctionDefinition> GetInheritedAbstractMethods(ClassDefinition definition, HashSet<ClassDefinition> seen)
	{
		foreach (TypeDefinition baseClass in GetDirectBaseClasses(definition))
		{
			if (baseClass is not ClassDefinition classDefinition)
				continue;

			if (!seen.Add(classDefinition))
				continue;

			foreach (FunctionDefinition function in classDefinition.Functions)
			{
				if (function.Modifier == FunctionModifier.Abstract)
					yield return function;
			}

			foreach (FunctionDefinition inherited in GetInheritedAbstractMethods(classDefinition, seen))
				yield return inherited;
		}
	}

	List<MethodSignature> GetClassMethodSignatures(ClassDefinition definition)
	{
		return GetClassMethodSignatures(definition, []);
	}

	List<MethodSignature> GetClassMethodSignatures(ClassDefinition definition, HashSet<ClassDefinition> seen)
	{
		List<MethodSignature> signatures = [];
		if (!seen.Add(definition))
			return signatures;

		foreach (TypeDefinition baseClass in GetDirectBaseClasses(definition))
		{
			if (baseClass is ClassDefinition baseClassDefinition)
				signatures.AddRange(GetClassMethodSignatures(baseClassDefinition, seen));
		}

		foreach (FunctionDefinition function in definition.Functions)
		{
			if (function.Modifier != FunctionModifier.Abstract)
				signatures.Add(BuildMethodSignature(function));
		}

		return signatures;
	}

	static List<MethodSignature> GetStructMethodSignatures(StructDefinition definition)
	{
		List<MethodSignature> signatures = [];
		foreach (FunctionDefinition function in definition.Functions)
			signatures.Add(BuildMethodSignature(function));
		return signatures;
	}

	static bool ContainsSignature(List<MethodSignature> signatures, MethodSignature required)
	{
		foreach (MethodSignature signature in signatures)
		{
			if (signature.Equals(required))
				return true;
		}

		return false;
	}

	static bool ContainsOverrideSignature(List<FunctionDefinition> functions, MethodSignature required)
	{
		foreach (FunctionDefinition function in functions)
		{
			if (function.Modifier == FunctionModifier.Override && BuildMethodSignature(function).Equals(required))
				return true;
		}

		return false;
	}

	void ValidateFunctionModifiers(FunctionDefinition definition)
	{
		bool participatesInVirtualDispatch = definition.Modifier is FunctionModifier.Virtual
			or FunctionModifier.Abstract
			or FunctionModifier.Override
			or FunctionModifier.Sealed;

		if (participatesInVirtualDispatch && definition.Modifier == FunctionModifier.Static)
			Report(GetNameRange(definition), "Only instance methods may be virtual, abstract, override, or sealed.");

		if (participatesInVirtualDispatch && definition.GenericParameters.Count > 0)
			Report(GetNameRange(definition), "Virtual, abstract, override, and sealed methods may not declare generic parameters.");

		if (definition.Modifier == FunctionModifier.Abstract && definition.Body is not null)
			Report(GetNameRange(definition), "Abstract methods may not have a body.");

		if (definition.Modifier == FunctionModifier.Virtual && definition.Body is null && !IsDestructorFunction(definition))
			Report(GetNameRange(definition), "Virtual methods must have a body; use abstract for bodyless dispatch slots.");
	}

	static bool IsGeneratedLifecycleMethodName(string name)
	{
		return name is InitNewMethodName or CreateMethodName or DeleteMethodName or DestroyMethodName;
	}

	void ValidateGenericParameterConstraint(GenericParameter parameter)
	{
		if (parameter.Constraint is null)
			return;

		if (parameter.RequiresImplementation)
		{
			if (!TryGetNamedTypeDefinition(parameter.Constraint, out TypeDefinition? definition) || definition is not InterfaceDefinition)
				Report(GetRange(parameter.Constraint.SourceSyntax), $"Generic parameter '{parameter.Name}' has an implements constraint that is not an interface.");
			return;
		}

		if (parameter.Constraint is AnyTypeReference)
			return;

		if (parameter.Constraint is PrimitiveTypeReference primitive && IsIntegralPrimitive(primitive.Type))
			return;

		if (parameter.Constraint is PointerTypeReference { ElementType: PrimitiveTypeReference { Type: PrimitiveType.Void } })
			return;

		Report(GetRange(parameter.Constraint.SourceSyntax), $"Generic parameter '{parameter.Name}' must be constrained to any, an integral type, or implements Interface.");
	}

	void ValidateGenericArity(NamedTypeReference type, TypeDefinition definition)
	{
		int expected = definition.GenericParameters.Count;
		int actual = type.TypeArguments.Count;
		if (actual != expected)
			Report(GetRange(type.SourceSyntax), $"Type '{definition.Name}' expects {expected} generic argument(s), but {actual} were supplied.");
	}

	void ValidateGenericArgumentUse(TypeReference? type)
	{
		if (type is null)
			return;

		switch (type)
		{
			case NamedTypeReference named:
				if (named.TypeArguments.Count > 0 && !TryGetNamedTypeDefinition(named, out _))
					Report(GetRange(named.SourceSyntax), $"Type arguments may only be supplied to generic named types.");
				foreach (TypeReference argument in named.TypeArguments)
					ValidateGenericArgumentUse(argument);
				break;

			case GenericTypeReference generic:
				ValidateGenericArgumentUse(generic.Type);
				foreach (TypeReference argument in generic.TypeArguments)
					ValidateGenericArgumentUse(argument);
				break;
		}
	}

	void ValidateParameterPassing(ParameterDefinition parameter, AnalysisScope scope)
	{
		if (parameter is not { Type: TypeReference type } || parameter is ThisParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition)
			return;

		if (parameter.Modifier is ParameterModifier.In or ParameterModifier.Out or ParameterModifier.Thrown or ParameterModifier.Within)
			return;

		if (IsAnyOrAnyConstrainedGeneric(type, scope))
			Report(GetNameRange(parameter), "Generic values constrained to any must be passed by reference.");

		if (IsFixedOrClassLikeType(type))
			Report(GetNameRange(parameter), "Fixed structs and classes must be passed by reference.");
	}

	bool IsAnyOrAnyConstrainedGeneric(TypeReference type, AnalysisScope scope)
	{
		return type switch
		{
			AnyTypeReference => true,
			NamedTypeReference named when scope.TryGetGenericParameter(named.Name, out GenericParameter? parameter) => parameter is { Constraint: AnyTypeReference },
			ConstTypeReference { Type: not null } constType => IsAnyOrAnyConstrainedGeneric(constType.Type, scope),
			VolatileTypeReference { Type: not null } volatileType => IsAnyOrAnyConstrainedGeneric(volatileType.Type, scope),
			EscapedTypeReference { Type: not null } escapedType => IsAnyOrAnyConstrainedGeneric(escapedType.Type, scope),
			ScopedTypeReference { Type: not null } scopedType => IsAnyOrAnyConstrainedGeneric(scopedType.Type, scope),
			UnscopedTypeReference { Type: not null } unscopedType => IsAnyOrAnyConstrainedGeneric(unscopedType.Type, scope),
			_ => false
		};
	}

	bool IsFixedOrClassLikeType(TypeReference type)
	{
		type = UnwrapTypeDeclarators(type);
		if (type is NamedTypeReference named && TryGetNamedTypeDefinition(named, out TypeDefinition? definition))
			return definition is ClassDefinition or StructDefinition { Modifier: StructModifier.Fixed };

		return false;
	}

	void AnalyzeExportVisibility(Module module)
	{
		foreach (Definition definition in module.Definitions)
			AnalyzeExportVisibility(definition, containingTypeExported: false);
	}

	void AnalyzeExportVisibility(Definition definition, bool containingTypeExported)
	{
		bool exported = definition.Export is not null || containingTypeExported;
		if (!exported)
			return;

		switch (definition)
		{
			case TypeDefinition typeDefinition:
				foreach (TypeReference consumedType in GetVisibleTypes(typeDefinition))
					CheckExportedTypeUse(typeDefinition, consumedType);
				AnalyzeExportedMembers(typeDefinition);
				break;

			case VariableDefinition variable:
				CheckExportedTypeUse(variable, variable.Type);
				break;

			case FunctionDefinition function:
				foreach (TypeReference consumedType in GetVisibleTypes(function))
					CheckExportedTypeUse(function, consumedType);
				break;
		}
	}

	void AnalyzeExportedMembers(TypeDefinition typeDefinition)
	{
		switch (typeDefinition)
		{
			case ClassDefinition classDefinition:
				foreach (FunctionDefinition function in classDefinition.Functions)
					AnalyzeExportVisibility(function, containingTypeExported: false);
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					AnalyzeExportVisibility(field, containingTypeExported: typeDefinition.Export is not null);
				foreach (FunctionDefinition function in structDefinition.Functions)
					AnalyzeExportVisibility(function, containingTypeExported: false);
				break;

			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
					AnalyzeExportVisibility(function, containingTypeExported: typeDefinition.Export is not null);
				break;
		}
	}

	IEnumerable<TypeReference> GetVisibleTypes(TypeDefinition typeDefinition)
	{
		switch (typeDefinition)
		{
			case ClassDefinition classDefinition:
				foreach (TypeReference type in classDefinition.BaseTypes)
					yield return type;
				break;

			case StructDefinition structDefinition:
				foreach (TypeReference type in structDefinition.BaseTypes)
					yield return type;
				foreach (FieldDefinition field in structDefinition.Fields)
				{
					if (field.Type is not null)
						yield return field.Type;
				}
				break;

			case InterfaceDefinition interfaceDefinition:
				foreach (TypeReference type in interfaceDefinition.BaseTypes)
					yield return type;
				foreach (FunctionDefinition function in interfaceDefinition.Functions)
				{
					foreach (TypeReference type in GetVisibleTypes(function))
						yield return type;
				}
				break;

			case EnumDefinition enumDefinition:
				if (enumDefinition.UnderlyingType is not null)
					yield return enumDefinition.UnderlyingType;
				break;

			case NewtypeDefinition newtypeDefinition:
				if (newtypeDefinition.UnderlyingType is not null)
					yield return newtypeDefinition.UnderlyingType;
				foreach (ParameterDefinition parameter in newtypeDefinition.Parameters)
				{
					if (parameter.Type is not null)
						yield return parameter.Type;
				}
				break;

			case ParamsDefinition paramsDefinition:
				if (paramsDefinition.UnderlyingType is not null)
					yield return paramsDefinition.UnderlyingType;
				foreach (ParameterDefinition parameter in paramsDefinition.Components)
				{
					if (parameter.Type is not null)
						yield return parameter.Type;
				}
				break;
		}
	}

	static IEnumerable<TypeReference> GetVisibleTypes(FunctionDefinition function)
	{
		if (function.ReturnType is not null)
			yield return function.ReturnType;

		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Type is not null)
				yield return parameter.Type;

			if (parameter is VTableOfParameterDefinition { InterfaceType: not null } vtableOf)
				yield return vtableOf.InterfaceType;
		}
	}

	void CheckExportedTypeUse(Definition exportedDeclaration, TypeReference? type)
	{
		if (type is null)
			return;

		foreach (NamedTypeReference named in GetNamedTypes(type))
		{
			if (!TryGetNamedTypeDefinition(named, out TypeDefinition? definition))
				continue;

			if (definition is { Export: null })
				Report(GetRange(named.SourceSyntax), $"Exported declaration '{exportedDeclaration.Name}' exposes non-exported type '{definition.Name}'.");
		}
	}

	static IEnumerable<NamedTypeReference> GetNamedTypes(TypeReference type)
	{
		switch (type)
		{
			case NamedTypeReference named:
				yield return named;
				foreach (TypeReference argument in named.TypeArguments)
				{
					foreach (NamedTypeReference child in GetNamedTypes(argument))
						yield return child;
				}
				break;

			case GenericTypeReference generic:
				if (generic.Type is not null)
				{
					foreach (NamedTypeReference child in GetNamedTypes(generic.Type))
						yield return child;
				}
				foreach (TypeReference argument in generic.TypeArguments)
				{
					foreach (NamedTypeReference child in GetNamedTypes(argument))
						yield return child;
				}
				break;

			case AttributedTypeReference { Type: not null } attributed:
				foreach (NamedTypeReference child in GetNamedTypes(attributed.Type))
					yield return child;
				break;

			case ArrayTypeReference { ElementType: not null } array:
				foreach (NamedTypeReference child in GetNamedTypes(array.ElementType))
					yield return child;
				break;

			case OptionalTypeReference { ElementType: not null } optional:
				foreach (NamedTypeReference child in GetNamedTypes(optional.ElementType))
					yield return child;
				break;

			case PointerTypeReference { ElementType: not null } pointer:
				foreach (NamedTypeReference child in GetNamedTypes(pointer.ElementType))
					yield return child;
				break;

			case ConstTypeReference { Type: not null } constType:
				foreach (NamedTypeReference child in GetNamedTypes(constType.Type))
					yield return child;
				break;

			case VolatileTypeReference { Type: not null } volatileType:
				foreach (NamedTypeReference child in GetNamedTypes(volatileType.Type))
					yield return child;
				break;

			case EscapedTypeReference { Type: not null } escapedType:
				foreach (NamedTypeReference child in GetNamedTypes(escapedType.Type))
					yield return child;
				break;

			case ScopedTypeReference { Type: not null } scopedType:
				foreach (NamedTypeReference child in GetNamedTypes(scopedType.Type))
					yield return child;
				break;

			case UnscopedTypeReference { Type: not null } unscopedType:
				foreach (NamedTypeReference child in GetNamedTypes(unscopedType.Type))
					yield return child;
				break;

			case CallableTypeReference callable:
				if (callable.ReturnType is not null)
				{
					foreach (NamedTypeReference child in GetNamedTypes(callable.ReturnType))
						yield return child;
				}
				foreach (ParameterDefinition parameter in callable.Parameters)
				{
					if (parameter.Type is null)
						continue;
					foreach (NamedTypeReference child in GetNamedTypes(parameter.Type))
						yield return child;
				}
				break;

			case IterTypeReference { ElementType: not null } iter:
				foreach (NamedTypeReference child in GetNamedTypes(iter.ElementType))
					yield return child;
				break;

			case GroupedParamsTypeReference { StructType: not null } grouped:
				foreach (NamedTypeReference child in GetNamedTypes(grouped.StructType))
					yield return child;
				break;

			case MaterializedStructTypeReference { ParamsType: not null } materialized:
				foreach (NamedTypeReference child in GetNamedTypes(materialized.ParamsType))
					yield return child;
				break;

			case ThrownTypeReference { Type: not null } thrown:
				foreach (NamedTypeReference child in GetNamedTypes(thrown.Type))
					yield return child;
				break;
		}
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

	void AnalyzeOptionalFunctionBody(BlockStatement? body, AnalysisScope scope)
	{
		if (body is null)
			return;

		body.ResolvedType = "void";
		foreach (Statement statement in body.Statements)
			AnalyzeStatement(statement, scope);
	}

	void AnalyzeStatement(Statement statement, AnalysisScope scope)
	{
		statement.ResolvedType = "void";

		switch (statement)
		{
			case BlockStatement block:
				foreach (Statement child in block.Statements)
					AnalyzeStatement(child, scope);
				break;

			case ExpressionStatement expression:
				AnalyzeOptionalExpression(expression.Expression, scope);
				break;

			case DeclarationStatement declaration:
				AnalyzeDeclarationTarget(declaration.Target, scope);
				AnalyzeOptionalExpression(declaration.InitialValue, scope);
				break;

			case IfStatement ifStatement:
				AnalyzeOptionalExpression(ifStatement.Condition, scope);
				AnalyzeOptionalStatement(ifStatement.Body, scope);
				AnalyzeOptionalStatement(ifStatement.ElseBody, scope);
				break;

			case WhileStatement whileStatement:
				AnalyzeOptionalExpression(whileStatement.Condition, scope);
				AnalyzeOptionalStatement(whileStatement.Body, scope);
				break;

			case DoWhileStatement doWhile:
				AnalyzeOptionalStatement(doWhile.Body, scope);
				AnalyzeOptionalExpression(doWhile.Condition, scope);
				break;

			case ForStatement forStatement:
				AnalyzeForStatementCondition(forStatement.Condition, scope);
				AnalyzeOptionalStatement(forStatement.Body, scope);
				break;

			case ForeachStatement foreachStatement:
				AnalyzeDeclarationTarget(foreachStatement.Target, scope);
				AnalyzeOptionalExpression(foreachStatement.Source, scope);
				AnalyzeOptionalStatement(foreachStatement.Body, scope);
				break;

			case SwitchStatement switchStatement:
				AnalyzeOptionalExpression(switchStatement.Expression, scope);
				foreach (Statement child in switchStatement.Statements)
					AnalyzeStatement(child, scope);
				break;

			case CaseStatement caseStatement:
				AnalyzeOptionalExpression(caseStatement.Expression, scope);
				break;

			case ReturnStatement returnStatement:
				AnalyzeOptionalExpression(returnStatement.Expression, scope);
				break;

			case YieldStatement yieldStatement:
				AnalyzeOptionalExpression(yieldStatement.Expression, scope);
				break;

			case DeleteStatement deleteStatement:
				AnalyzeOptionalExpression(deleteStatement.Expression, scope);
				break;

			case TryStatement tryStatement:
				AnalyzeOptionalStatement(tryStatement.Body, scope);
				foreach (CatchStatement catchStatement in tryStatement.Catches)
					AnalyzeStatement(catchStatement, scope);
				AnalyzeOptionalStatement(tryStatement.Finally, scope);
				break;

			case CatchStatement catchStatement:
				AnalyzeDeclarationTarget(catchStatement.Target, scope);
				AnalyzeOptionalStatement(catchStatement.Body, scope);
				break;

			case FinallyStatement finallyStatement:
				AnalyzeOptionalStatement(finallyStatement.Body, scope);
				break;

			case WithinStatement withinStatement:
				AnalyzeOptionalExpression(withinStatement.Allocator, scope);
				AnalyzeOptionalStatement(withinStatement.Body, scope);
				break;
		}
	}

	void AnalyzeOptionalStatement(Statement? statement, AnalysisScope scope)
	{
		if (statement is not null)
			AnalyzeStatement(statement, scope);
	}

	void AnalyzeDeclarationTarget(DeclarationTarget target, AnalysisScope scope)
	{
		AnalyzeOptionalType(target.Type, scope);
		target.ResolvedType = target.Type?.ResolvedType ?? ErrorType;

		foreach (string name in target.Names)
			CheckName(name, GetDeclarationTargetNameRange(target.SourceSyntax, name), "local");
	}

	void AnalyzeForStatementCondition(ForStatementCondition condition, AnalysisScope scope)
	{
		condition.ResolvedType = "bool";
		if (condition.Declaration is not null)
			AnalyzeStatement(condition.Declaration, scope);

		foreach (Expression? clause in condition.Clauses)
			AnalyzeOptionalExpression(clause, scope);
	}

	void AnalyzeOptionalExpression(Expression? expression, AnalysisScope scope)
	{
		if (expression is not null)
			AnalyzeExpression(expression, scope);
	}

	void AnalyzeExpression(Expression expression, AnalysisScope scope)
	{
		expression.ResolvedType = UnresolvedType;

		switch (expression)
		{
			case DefaultExpression defaultExpression:
				if (defaultExpression.Type is not null)
					AnalyzeType(defaultExpression.Type, scope);
				expression.ResolvedType = defaultExpression.Type?.ResolvedType ?? TargetType;
				break;

			case GroupedExpression grouped:
				foreach (GroupedExpressionItem item in grouped.Items)
				{
					item.ResolvedType = UnresolvedType;
				AnalyzeOptionalExpression(item.Expression, scope);
				}
				break;

			case ArrayExpression array:
				foreach (Expression element in array.Elements)
					AnalyzeExpression(element, scope);
				break;

			case InitializerExpression initializer:
				foreach (InitializerItem item in initializer.Items)
				{
					item.ResolvedType = UnresolvedType;
					if (item.Target is not null)
						AnalyzeInitializerTarget(item.Target, scope);
					AnalyzeOptionalExpression(item.Expression, scope);
				}
				break;

			case ParenthesizedExpression parenthesized:
				AnalyzeOptionalExpression(parenthesized.Expression, scope);
				expression.ResolvedType = parenthesized.Expression?.ResolvedType ?? UnresolvedType;
				break;

			case CastExpression cast:
				if (cast.Type is not null)
					AnalyzeType(cast.Type, scope);
				AnalyzeOptionalExpression(cast.Expression, scope);
				expression.ResolvedType = cast.Type?.ResolvedType ?? ErrorType;
				break;

			case ConstructionExpression construction:
				if (construction.Type is not null)
					AnalyzeType(construction.Type, scope);
				foreach (ArgumentExpression argument in construction.Arguments)
					AnalyzeExpression(argument, scope);
				AnalyzeOptionalExpression(construction.ElementCount, scope);
				AnalyzeOptionalExpression(construction.Initializer, scope);
				expression.ResolvedType = construction.Type?.ResolvedType ?? TargetType;
				break;

			case WithinExpression within:
				AnalyzeOptionalExpression(within.Context, scope);
				AnalyzeOptionalExpression(within.Expression, scope);
				expression.ResolvedType = within.Expression?.ResolvedType ?? UnresolvedType;
				break;

			case SizeOfExpression sizeOf:
				if (sizeOf.Type is not null)
					AnalyzeType(sizeOf.Type, scope);
				expression.ResolvedType = "nuint";
				break;

			case VTableOfExpression vtableOf:
				if (vtableOf.Type is not null)
					AnalyzeType(vtableOf.Type, scope);
				if (vtableOf.InterfaceType is not null)
					AnalyzeType(vtableOf.InterfaceType, scope);
				expression.ResolvedType = VTableType;
				break;

			case LambdaExpression lambda:
				foreach (LambdaParameter parameter in lambda.Parameters)
				{
					parameter.ResolvedType = parameter.Parameter?.ResolvedType ?? TargetType;
					CheckName(GetLambdaParameterSymbolName(parameter), GetLambdaParameterNameRange(parameter.SourceSyntax), "lambda parameter");
					if (parameter.Parameter is not null)
						AnalyzeParameterDefinition(parameter.Parameter, scope);
				}
				AnalyzeOptionalFunctionBody(lambda.Body, scope);
				break;

			case ArgumentExpression argument:
				if (argument.Type is not null)
					AnalyzeType(argument.Type, scope);
				if (argument.Target is not null)
					AnalyzeDeclarationTarget(argument.Target, scope);
				AnalyzeOptionalExpression(argument.Value, scope);
				expression.ResolvedType = argument.Type?.ResolvedType ?? argument.Target?.ResolvedType ?? argument.Value?.ResolvedType ?? UnresolvedType;
				break;

			case CallExpression call:
				AnalyzeOptionalExpression(call.Target, scope);
				AnalyzeTypeList(call.TypeArguments, scope);
				foreach (ArgumentExpression argument in call.Arguments)
					AnalyzeExpression(argument, scope);
				break;

			case IndexExpression index:
				AnalyzeOptionalExpression(index.Target, scope);
				foreach (ArgumentExpression argument in index.Arguments)
					AnalyzeExpression(argument, scope);
				break;

			case MemberExpression member:
				AnalyzeOptionalExpression(member.Target, scope);
				break;

			case MemberReferenceExpression member:
				AnalyzeOptionalExpression(member.Target, scope);
				break;

			case NamelessIndexerExpression indexer:
				AnalyzeOptionalExpression(indexer.Target, scope);
				foreach (ArgumentExpression argument in indexer.Arguments)
					AnalyzeExpression(argument, scope);
				break;

			case UnaryExpression unary:
				AnalyzeOptionalExpression(unary.Operand, scope);
				AnalyzeOptionalExpression(unary.Context, scope);
				break;

			case PostfixUpdateExpression postfix:
				AnalyzeOptionalExpression(postfix.Expression, scope);
				break;

			case FinallyDeleteExpression finallyDelete:
				AnalyzeOptionalExpression(finallyDelete.Expression, scope);
				break;

			case BinaryExpression binary:
				AnalyzeOptionalExpression(binary.Left, scope);
				AnalyzeOptionalExpression(binary.Right, scope);
				break;

			case AssignmentExpression assignment:
				AnalyzeOptionalExpression(assignment.Target, scope);
				AnalyzeOptionalExpression(assignment.Value, scope);
				expression.ResolvedType = assignment.Target?.ResolvedType ?? assignment.Value?.ResolvedType ?? UnresolvedType;
				break;

			case ConditionalExpression conditional:
				AnalyzeOptionalExpression(conditional.Condition, scope);
				AnalyzeOptionalExpression(conditional.WhenTrue, scope);
				AnalyzeOptionalExpression(conditional.WhenFalse, scope);
				break;

			case RangeExpression range:
				AnalyzeOptionalExpression(range.Start, scope);
				AnalyzeOptionalExpression(range.End, scope);
				expression.ResolvedType = RangeType;
				break;
		}
	}

	void AnalyzeInitializerTarget(InitializerTarget target, AnalysisScope scope)
	{
		target.ResolvedType = TargetType;
		foreach (InitializerTargetPart part in target.Parts)
		{
			part.ResolvedType = TargetType;
			foreach (ArgumentExpression argument in part.Arguments)
				AnalyzeExpression(argument, scope);
		}
	}

	void FillMissingResolvedTypes(BindableNode node)
	{
		node.ResolvedType ??= UnresolvedType;

		Type type = node.GetType();
		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.Name is nameof(BindableNode.SourceSyntax) or nameof(BindableNode.ResolvedType) || IsSemanticReferenceProperty(property))
				continue;

			object? value = property.GetValue(node);
			if (value is BindableNode child)
				FillMissingResolvedTypes(child);
			else if (IsListType(property.PropertyType) && value is IEnumerable items)
			{
				foreach (object? item in items)
				{
					if (item is BindableNode childItem)
						FillMissingResolvedTypes(childItem);
				}
			}
		}
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
			CompilationUnitItemSyntax item => GetRange(item.ImportExportDeclaration) ?? GetRange(item.Declaration),
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

	static bool IsListType(Type type)
	{
		return type != typeof(string)
			&& type != typeof(Token)
			&& type != typeof(TokenRange)
			&& type != typeof(Token?)
			&& type != typeof(TokenRange?)
			&& typeof(IEnumerable).IsAssignableFrom(type);
	}

	void ApplyNodeRewrites(BindableNode node)
	{
		foreach (PropertyInfo property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (property.Name == nameof(BindableNode.SourceSyntax) || property.Name == nameof(BindableNode.ResolvedType) || IsSemanticReferenceProperty(property))
				continue;

			object? value = property.GetValue(node);
			if (value is null)
				continue;

			if (value is Expression expression)
			{
				Expression rewritten = RewriteExpression(expression);
				if (!ReferenceEquals(rewritten, expression) && property.CanWrite)
					property.SetValue(node, rewritten);
				ApplyNodeRewrites(rewritten);
			}
			else if (value is TypeReference type)
			{
				TypeReference rewritten = RewriteTypeReference(type);
				if (!ReferenceEquals(rewritten, type) && property.CanWrite)
					property.SetValue(node, rewritten);
				ApplyNodeRewrites(rewritten);
			}
			else if (value is IList list)
			{
				ApplyListRewrites(list);
			}
			else if (value is BindableNode child)
			{
				ApplyNodeRewrites(child);
			}
		}
	}

	void ApplyListRewrites(IList list)
	{
		for (int i = 0; i < list.Count; i++)
		{
			object? item = list[i];
			if (item is null)
				continue;

			if (item is Expression expression)
			{
				Expression rewritten = RewriteExpression(expression);
				if (!ReferenceEquals(rewritten, expression))
					list[i] = rewritten;
				ApplyNodeRewrites(rewritten);
			}
			else if (item is TypeReference type)
			{
				TypeReference rewritten = RewriteTypeReference(type);
				if (!ReferenceEquals(rewritten, type))
					list[i] = rewritten;
				ApplyNodeRewrites(rewritten);
			}
			else if (item is BindableNode child)
			{
				ApplyNodeRewrites(child);
			}
		}
	}

	Expression RewriteExpression(Expression expression)
	{
		return expressionRewrites.TryGetValue(expression, out Expression? rewritten) ? rewritten : expression;
	}

	TypeReference RewriteTypeReference(TypeReference type)
	{
		return typeRewrites.TryGetValue(type, out TypeReference? rewritten) ? rewritten : type;
	}

	static bool IsSemanticReferenceProperty(PropertyInfo property)
	{
		return property.DeclaringType == typeof(VariableReferenceExpression) && property.Name == nameof(VariableReferenceExpression.Variable)
			|| property.DeclaringType == typeof(TypeDefinitionReference) && property.Name == nameof(TypeDefinitionReference.Definition)
			|| property.DeclaringType == typeof(GenericParameterTypeReference) && property.Name == nameof(GenericParameterTypeReference.Parameter)
			|| property.DeclaringType == typeof(MethodReferenceExpression) && property.Name == nameof(MethodReferenceExpression.Candidates)
			|| property.DeclaringType == typeof(MemberReferenceExpression) && property.Name == nameof(MemberReferenceExpression.Member)
			|| property.DeclaringType == typeof(MemberReferenceExpression) && property.Name == nameof(MemberReferenceExpression.Candidates);
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
