using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
		static MethodReferenceExpression CreateMethodReference(FunctionDefinition function, string type)
		{
		MethodReferenceExpression reference = new()
		{
			ResolvedType = type
		};
		reference.Candidates.Add(function);
		return reference;
	}

	DeclarationStatement CreateGeneratedLocal(string name, string typeName, TypeReference type, Expression? initialValue)
	{
		DeclarationStatement declaration = new()
		{
			InitialValue = initialValue,
			ResolvedType = "void"
		};
		declaration.Target.Type = type;
		declaration.Target.ResolvedType = typeName;
		declaration.Target.Names.Add(name);
		return declaration;
	}

	static VariableReferenceExpression CreateVariableReference(BindableNode variable, string type)
	{
		return new VariableReferenceExpression
		{
			Variable = variable,
			ResolvedType = type
		};
	}

	string NewGeneratedLocalName(string prefix)
	{
		string name = $"#{prefix}{generatedLocalIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
		generatedLocalIndex++;
		return name;
	}

	string NewGeneratedLabelName(string prefix)
	{
		string name = $"__{prefix}{generatedLocalIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
		generatedLocalIndex++;
		return name;
	}

	FunctionDefinition? FindInitNewMethod(TypeDefinition type, int argumentCount)
	{
		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (function.Name == InitNewMethodName && CallableByArgumentCount(function.Parameters, argumentCount))
				return function;
		}

		return null;
	}

	FunctionDefinition? FindDeleteMethod(string typeName)
	{
		if (!typeDefinitions.TryGetValue(typeName, out TypeDefinition? type))
			return null;

		return FindDeleteMethod(type);
	}

	FunctionDefinition? FindDeleteMethod(TypeDefinition type)
	{
		if (type is ClassDefinition classDefinition)
		{
			foreach (ClassDefinition candidateClass in EnumerateClassAndBases(classDefinition))
				foreach (FunctionDefinition candidateFunction in candidateClass.Functions)
					if (candidateFunction.Name == DeleteMethodName)
						return candidateFunction;
			foreach (ClassDefinition candidateClass in EnumerateClassAndBases(classDefinition))
				foreach (FunctionDefinition candidateFunction in candidateClass.Functions)
					if (IsDestructorFunction(candidateFunction))
						return candidateFunction;
		}
		else
		{
			foreach (FunctionDefinition function in GetFunctions(type))
				if (function.Name == DeleteMethodName)
					return function;
			foreach (FunctionDefinition function in GetFunctions(type))
				if (IsDestructorFunction(function))
					return function;
		}

		return null;
	}

	static IEnumerable<FunctionDefinition> GetFunctions(TypeDefinition type)
	{
		return type switch
		{
			ClassDefinition classDefinition => classDefinition.Functions,
			StructDefinition structDefinition => structDefinition.Functions,
			InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
			EnumDefinition enumDefinition => enumDefinition.Functions,
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions,
			ParamsDefinition paramsDefinition => paramsDefinition.Functions,
			_ => []
		};
	}

	static bool CallableByArgumentCount(List<ParameterDefinition> parameters, int count)
	{
		int required = 0;
		int callable = 0;
		foreach (ParameterDefinition parameter in parameters)
		{
			if (IsHiddenParameter(parameter))
				continue;

			callable++;
			if (parameter.DefaultValue is null && parameter is not SizeOfParameterDefinition)
				required++;
		}

		return required <= count && count <= callable;
	}

	static bool IsHiddenParameter(ParameterDefinition parameter)
	{
		return parameter.Modifier is ParameterModifier.Thrown or ParameterModifier.Within
			|| parameter is WithinParameterDefinition;
	}

	static bool HasWithinParameter(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition)
				return true;
		}

		return false;
	}

	static ParameterDefinition? GetWithinParameter(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition)
				return parameter;
		}

		return null;
	}

	static ParameterDefinition? GetAllocatorParameter(FunctionDefinition function)
	{
		if (GetWithinParameter(function) is ParameterDefinition within)
			return within;
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Name == "allocator" && parameter.ResolvedType is "Allocator*" or AllocatorType)
				return parameter;
		}
		return null;
	}

	static bool HasCreateWithAllocatorAttribute(TypeDefinition type)
	{
		foreach (AttributeConstructor attribute in type.Attributes)
		{
			if (attribute.Name.TrimStart('@') == "createWithAllocator")
				return true;
		}

		return false;
	}

	static ParameterDefinition CreateAllocatorParameter()
	{
		return new ParameterDefinition
		{
			Name = "allocator",
			Symbol = "allocator",
			Type = AllocatorPointerType(),
			ResolvedType = "Allocator*"
		};
	}

	DeclarationStatement CreateResolvedAllocatorLocal(ParameterDefinition? allocator)
	{
		string allocatorType = allocator?.ResolvedType ?? allocator?.Type?.ResolvedType ?? "Allocator*";
		Expression source = allocator is null
			? NullLiteral()
			: CreateVariableReference(allocator, allocatorType);
		TypeReference type = allocator?.Type is null
			? new NamedTypeReference { Name = allocatorType, ResolvedType = allocatorType }
			: CloneType(allocator.Type) ?? new NamedTypeReference { Name = allocatorType, ResolvedType = allocatorType };
		DeclarationStatement declaration = CreateGeneratedLocal("resolvedAllocator", allocatorType, type, source);
		declaration.Target.Names.Clear();
		declaration.Target.Names.Add("resolvedAllocator");
		return declaration;
	}

	Expression? GetFunctionWithinContext(FunctionDefinition function)
	{
		if (GetWithinParameter(function) is ParameterDefinition allocator)
			return CreateVariableReference(allocator, allocator.ResolvedType ?? "Allocator*");
		return null;
	}

	Expression? CaptureWithinContext(Expression? allocator, SyntaxNode? syntax)
	{
		if (allocator is null || currentStatementPrefix is null)
			return allocator;

		DeclarationStatement local = CreateWithinContextLocal(allocator, syntax);
		currentStatementPrefix.Add(local);
		return CreateVariableReference(local.Target, local.Target.ResolvedType ?? allocator.ResolvedType ?? ErrorType);
	}

	DeclarationStatement CreateWithinContextLocal(Expression allocator, SyntaxNode? syntax)
	{
		string type = allocator.ResolvedType ?? ErrorType;
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("allocator"), type, new NamedTypeReference { Name = type, ResolvedType = type }, allocator);
		local.SourceSyntax = syntax;
		return local;
	}

	static NamedExpression CreateResolvedAllocatorReference(string resolvedType = "Allocator*")
	{
		return new NamedExpression
		{
			Name = "resolvedAllocator",
			ResolvedType = resolvedType
		};
	}

	static LiteralExpression NullLiteral(SyntaxNode? syntax = null)
	{
		return new LiteralExpression
		{
			SourceSyntax = syntax,
			Kind = LiteralKind.Null,
			Text = "null",
			ResolvedType = "#NULL"
		};
	}

	static void CopyParameters(List<ParameterDefinition> source, List<ParameterDefinition> target)
	{
		foreach (ParameterDefinition parameter in source)
			target.Add(CloneParameter(parameter));
	}

	static ParameterDefinition CloneParameter(ParameterDefinition parameter)
	{
		ParameterDefinition clone = parameter switch
		{
			ThisParameterDefinition => new ThisParameterDefinition(),
			WithinParameterDefinition => new WithinParameterDefinition(),
			SizeOfParameterDefinition => new SizeOfParameterDefinition(),
			VTableOfParameterDefinition vtableOf => new VTableOfParameterDefinition { InterfaceType = CloneType(vtableOf.InterfaceType) },
			_ => new ParameterDefinition()
		};
		clone.SourceSyntax = parameter.SourceSyntax;
		clone.Name = parameter.Name;
		clone.Symbol = parameter.Symbol;
		clone.Export = parameter.Export;
		clone.Extern = parameter.Extern;
		clone.Modifier = parameter.Modifier;
		clone.Type = parameter is WithinParameterDefinition && parameter.Type is null ? new AllocatorTypeReference { ResolvedType = AllocatorType } : CloneType(parameter.Type);
		clone.DefaultValue = parameter.DefaultValue;
		clone.ResolvedType = parameter is WithinParameterDefinition && parameter.ResolvedType == AllocatorType ? AllocatorType : parameter.ResolvedType;
		return clone;
	}

	static TypeReference TypeReferenceFor(TypeDefinition type)
	{
		return new TypeDefinitionReference
		{
			Name = type.Name,
			Definition = type,
			ResolvedType = type.Name
		};
	}

	static TypeReference? CloneType(TypeReference? type)
	{
		if (type is null)
			return null;

		TypeReference clone = type switch
		{
			NamedTypeReference named => CloneNamed(named),
			TypeDefinitionReference definition => CloneDefinitionReference(definition),
			GenericParameterTypeReference generic => new GenericParameterTypeReference { Name = generic.Name, Parameter = generic.Parameter },
			AllocatorTypeReference => new AllocatorTypeReference(),
			AttributedTypeReference attributed => new AttributedTypeReference { Attribute = attributed.Attribute, Type = CloneType(attributed.Type) },
			GenericTypeReference generic => CloneGeneric(generic),
			ArrayTypeReference array => new ArrayTypeReference { ElementType = CloneType(array.ElementType) },
			OptionalTypeReference optional => new OptionalTypeReference { ElementType = CloneType(optional.ElementType) },
			PointerTypeReference pointer => new PointerTypeReference { ElementType = CloneType(pointer.ElementType) },
			ConstTypeReference constant => new ConstTypeReference { Type = CloneType(constant.Type) },
			VolatileTypeReference vol => new VolatileTypeReference { Type = CloneType(vol.Type) },
			AnyTypeReference => new AnyTypeReference(),
			AutoTypeReference => new AutoTypeReference(),
			PrimitiveTypeReference primitive => new PrimitiveTypeReference { Type = primitive.Type },
			EscapedTypeReference escaped => new EscapedTypeReference { Type = CloneType(escaped.Type) },
			ScopedTypeReference scoped => CloneScoped(scoped),
			UnscopedTypeReference unscoped => CloneUnscoped(unscoped),
			TargetTypeSpecTypeReference targetSpec => new TargetTypeSpecTypeReference { Specifier = targetSpec.Specifier, Type = CloneType(targetSpec.Type), IsPrefix = targetSpec.IsPrefix },
			CallableTypeReference callable => CloneCallable(callable),
			IterTypeReference iter => CloneIter(iter),
			GroupedParamsTypeReference grouped => new GroupedParamsTypeReference { StructType = CloneType(grouped.StructType) },
			MaterializedStructTypeReference materialized => new MaterializedStructTypeReference { ParamsType = CloneType(materialized.ParamsType) },
			ThrownTypeReference thrown => new ThrownTypeReference { Type = CloneType(thrown.Type) },
			_ => new NamedTypeReference { Name = type.ResolvedType ?? ErrorType }
		};
		clone.SourceSyntax = type.SourceSyntax;
		clone.ResolvedType = type.ResolvedType;
		return clone;
	}

	static NamedTypeReference CloneNamed(NamedTypeReference named)
	{
		NamedTypeReference clone = new() { Name = named.Name };
		clone.Qualifiers.AddRange(named.Qualifiers);
		foreach (TypeReference argument in named.TypeArguments)
			clone.TypeArguments.Add(CloneType(argument)!);
		return clone;
	}

	static TypeDefinitionReference CloneDefinitionReference(TypeDefinitionReference definition)
	{
		TypeDefinitionReference clone = new() { Name = definition.Name, Definition = definition.Definition };
		foreach (TypeReference argument in definition.TypeArguments)
			clone.TypeArguments.Add(CloneType(argument)!);
		return clone;
	}

	static GenericTypeReference CloneGeneric(GenericTypeReference generic)
	{
		GenericTypeReference clone = new() { Type = CloneType(generic.Type) };
		foreach (TypeReference argument in generic.TypeArguments)
			clone.TypeArguments.Add(CloneType(argument)!);
		return clone;
	}

	static ScopedTypeReference CloneScoped(ScopedTypeReference scoped)
	{
		ScopedTypeReference clone = new() { Type = CloneType(scoped.Type) };
		clone.Anchors.AddRange(scoped.Anchors);
		return clone;
	}

	static UnscopedTypeReference CloneUnscoped(UnscopedTypeReference unscoped)
	{
		UnscopedTypeReference clone = new() { Type = CloneType(unscoped.Type) };
		clone.Anchors.AddRange(unscoped.Anchors);
		return clone;
	}

	static CallableTypeReference CloneCallable(CallableTypeReference callable)
	{
		CallableTypeReference clone = new() { Kind = callable.Kind, CallSpec = callable.CallSpec, TargetSpec = callable.TargetSpec, ReturnType = CloneType(callable.ReturnType) };
		foreach (ParameterDefinition parameter in callable.Parameters)
			clone.Parameters.Add(CloneParameter(parameter));
		return clone;
	}

	static IterTypeReference CloneIter(IterTypeReference iter)
	{
		IterTypeReference clone = new() { IsAsync = iter.IsAsync, ElementType = CloneType(iter.ElementType) };
		foreach (ParameterDefinition parameter in iter.Parameters)
			clone.Parameters.Add(CloneParameter(parameter));
		return clone;
	}

	static TypeReference PointerTo(TypeReference type)
	{
		return new PointerTypeReference
		{
			ElementType = type,
			ResolvedType = $"{type.ResolvedType}*"
		};
	}

	static PrimitiveTypeReference VoidType()
	{
		return new PrimitiveTypeReference
		{
			Type = PrimitiveType.Void,
			ResolvedType = "void"
		};
	}

	static LiteralExpression NumberLiteral(string text, string resolvedType)
	{
		return new LiteralExpression
		{
			Kind = LiteralKind.Number,
			Text = text,
			Value = text,
			ResolvedType = resolvedType
		};
	}

	Expression? CurrentAllocator()
	{
		return currentWithinContext;
	}

	Expression CurrentWithinArgument(SyntaxNode? syntax = null)
	{
		return currentWithinContext ?? new LiteralExpression
		{
			SourceSyntax = syntax,
			Kind = LiteralKind.Null,
			Text = "null",
			ResolvedType = "#NULL"
		};
	}

	Expression CreateAllocationSizeExpression(TypeReference type, Expression? length, SyntaxNode? syntax)
	{
		Expression sizeOf = new SizeOfExpression
		{
			SourceSyntax = syntax,
			Type = CloneType(type),
			ResolvedType = "nuint"
		};
		if (length is null)
			return sizeOf;
		return new BinaryExpression
		{
			SourceSyntax = syntax,
			Left = sizeOf,
			Operator = BinaryOperator.Multiply,
			Right = length,
			ResolvedType = "nuint"
		};
	}

	CallExpression CreateMallocCall(Expression size, SyntaxNode? syntax)
	{
		FunctionDefinition? malloc = FindMallocFunction(syntax);
		if (malloc is null && allocatorSurfaceValidationEnabled)
			Report(syntax, "Allocation requires an accessible function named 'malloc' that takes a single integer parameter.");
		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = malloc?.ResolvedType ?? "void*",
			Target = malloc is null ? new NamedExpression { SourceSyntax = syntax, Name = "malloc", ResolvedType = "fn void*(nuint)" } : CreateMethodReference(malloc, malloc.ResolvedType ?? "void*")
		};
		call.Arguments.Add(new ArgumentExpression { SourceSyntax = syntax, Value = size, ResolvedType = size.ResolvedType });
		return call;
	}

	CallExpression CreateAllocatorAllocCall(Expression allocator, Expression size, SyntaxNode? syntax)
	{
		FunctionDefinition? alloc = FindAllocatorPatternMethod(allocator.ResolvedType, "alloc", IsSingleIntegerValueParameter, syntax);
		if (alloc is null && allocatorSurfaceValidationEnabled)
			Report(syntax, $"Allocator type '{allocator.ResolvedType ?? ErrorType}' must provide an accessible method named 'alloc' that takes a single integer parameter.");
		if (alloc is null && !allocatorSurfaceValidationEnabled)
			alloc = CreateSyntheticAllocatorPatternMethod(allocator.ResolvedType, "alloc", "void*");
		MemberReferenceExpression target = new()
		{
			SourceSyntax = syntax,
			Target = allocator,
			Name = "alloc",
			Member = alloc,
			ResolvedType = alloc?.ResolvedType ?? "void*"
		};
		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = alloc?.ResolvedType ?? "void*",
			Target = target
		};
		call.Arguments.Add(new ArgumentExpression { SourceSyntax = syntax, Value = size, ResolvedType = size.ResolvedType });
		if (alloc is not null)
			RewriteInstanceInvocation(call, target, allocator, alloc);
		return call;
	}

	CallExpression CreateGlobalFreeCall(Expression pointer, SyntaxNode? syntax)
	{
		FunctionDefinition? free = FindFreeFunction(syntax);
		if (free is null && allocatorSurfaceValidationEnabled)
			Report(syntax, "Deallocation requires an accessible function named 'free' that takes a single void* parameter.");
		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Target = free is null ? new NamedExpression { SourceSyntax = syntax, Name = "free", ResolvedType = "fn void(void*)" } : CreateMethodReference(free, "void")
		};
		call.Arguments.Add(new ArgumentExpression { SourceSyntax = syntax, Value = pointer, ResolvedType = pointer.ResolvedType });
		return call;
	}

	CallExpression CreateAllocatorFreeCall(Expression allocator, Expression pointer, SyntaxNode? syntax)
	{
		FunctionDefinition? free = FindAllocatorPatternMethod(allocator.ResolvedType, "free", IsSingleVoidPointerValueParameter, syntax);
		if (free is null && allocatorSurfaceValidationEnabled)
			Report(syntax, $"Allocator type '{allocator.ResolvedType ?? ErrorType}' must provide an accessible method named 'free' that takes a single void* parameter.");
		if (free is null && !allocatorSurfaceValidationEnabled)
			free = CreateSyntheticAllocatorPatternMethod(allocator.ResolvedType, "free", "void");
		MemberReferenceExpression target = new()
		{
			SourceSyntax = syntax,
			Target = allocator,
			Name = "free",
			Member = free,
			ResolvedType = "void"
		};
		CallExpression call = new()
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Target = target
		};
		call.Arguments.Add(new ArgumentExpression { SourceSyntax = syntax, Value = pointer, ResolvedType = pointer.ResolvedType });
		if (free is not null)
			RewriteInstanceInvocation(call, target, allocator, free);
		return call;
	}

	static FunctionDefinition? CreateSyntheticAllocatorPatternMethod(string? allocatorType, string name, string returnType)
	{
		string receiverType = TryGetPointerElementType(allocatorType ?? "") ?? allocatorType ?? "";
		if (string.IsNullOrWhiteSpace(receiverType) || receiverType == ErrorType)
			return null;
		return new FunctionDefinition
		{
			Name = name,
			Symbol = $"{receiverType}_{name}",
			ResolvedType = returnType
		};
	}

	FunctionDefinition? FindMallocFunction(SyntaxNode? syntax)
	{
		foreach (FunctionDefinition function in LookupGlobalFunctions("malloc", syntax))
		{
			if (GetExplicitThisParameter(function) is null && IsSingleIntegerValueParameter(function))
				return function;
		}

		return null;
	}

	FunctionDefinition? FindFreeFunction(SyntaxNode? syntax)
	{
		foreach (FunctionDefinition function in LookupGlobalFunctions("free", syntax))
		{
			if (GetExplicitThisParameter(function) is null && IsSingleVoidPointerValueParameter(function))
				return function;
		}

		return null;
	}

	IEnumerable<FunctionDefinition> LookupGlobalFunctions(string name, SyntaxNode? syntax)
	{
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition is not FunctionDefinition function || !IsDefinitionVisible(function, syntax))
				continue;
			if (IsFunctionNamed(function, name))
				yield return function;
		}
	}

	FunctionDefinition? FindAllocatorPatternMethod(string? allocatorType, string name, Func<FunctionDefinition, bool> predicate, SyntaxNode? syntax)
	{
		string receiverType = TryGetPointerElementType(allocatorType ?? "") ?? allocatorType ?? ErrorType;
		if (GetTypeDefinition(receiverType) is not TypeDefinition type && !TryFindModuleTypeDefinition(receiverType, out type))
			return null;

		foreach (FunctionDefinition function in LookupTypeFunctions(type, name, syntax))
			if (predicate(function))
				return function;

		foreach (FunctionDefinition function in LookupAllocatorPatternFunctions(type, name))
			if (predicate(function))
				return function;

		return null;
	}

	bool TryFindModuleTypeDefinition(string name, out TypeDefinition type)
	{
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition is TypeDefinition candidate && candidate.Name == name)
			{
				type = candidate;
				return true;
			}
		}
		type = null!;
		return false;
	}

	IEnumerable<FunctionDefinition> LookupAllocatorPatternFunctions(TypeDefinition type, string name)
	{
		if (type is ClassDefinition classDefinition)
		{
			foreach (ClassDefinition candidateClass in EnumerateClassAndBases(classDefinition))
				foreach (FunctionDefinition function in candidateClass.Functions)
					if (function.Name == name && !IsBodylessVirtualOverrideDeclaration(function))
						yield return function;
			yield break;
		}

		foreach (FunctionDefinition function in GetFunctions(type))
			if (function.Name == name)
				yield return function;
	}

	static bool IsSingleIntegerValueParameter(FunctionDefinition function)
	{
		return GetPatternValueParameters(function) is [ParameterDefinition parameter] && IsIntegerTypeName(parameter.ResolvedType ?? parameter.Type?.ResolvedType);
	}

	bool IsSingleVoidPointerValueParameter(FunctionDefinition function)
	{
		return GetPatternValueParameters(function) is [ParameterDefinition parameter] && IsVoidPointerTypeName(parameter.ResolvedType ?? parameter.Type?.ResolvedType);
	}

	static List<ParameterDefinition> GetPatternValueParameters(FunctionDefinition function)
	{
		List<ParameterDefinition> parameters = [];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter is ThisParameterDefinition)
				continue;
			parameters.Add(parameter);
		}
		return parameters;
	}

	static bool IsIntegerTypeName(string? type)
	{
		if (type is not null
			&& new TypeShapeParser(type).TryParse(out TypeShape shape)
			&& shape.Kind == TypeShapeKind.Named)
		{
			type = shape.Name;
		}

		return type is "byte" or "sbyte" or "ushort" or "short" or "uint" or "int" or "ulong" or "long" or "nuint" or "nint" or "char" or "wchar" or "achar" or "uchar";
	}

	bool IsVoidPointerTypeName(string? type)
	{
		return TryParseTypeShape(type, out TypeShape shape)
			&& shape.Kind == TypeShapeKind.Pointer
			&& shape.Element is TypeShape { Kind: TypeShapeKind.Named, Name: "void" };
	}

	static TypeReference AllocatorPointerType()
	{
		return new PointerTypeReference
		{
			ElementType = new NamedTypeReference { Name = "Allocator", ResolvedType = "Allocator" },
			ResolvedType = "Allocator*"
		};
	}

	static string BaseConstructedType(string? type)
	{
		if (string.IsNullOrWhiteSpace(type))
			return "";

		string constructed = new TypeShapeParser(type).TryParse(out TypeShape shape) && shape.Kind == TypeShapeKind.Pointer
			? TypeShapeParser.Format(shape.Element)
			: type;
		return BaseTypeName(constructed);
	}

	void Report(SyntaxNode? syntax, string message)
	{
		diagnostics.Add(new AnalysisDiagnostic(GetRange(syntax), message));
	}

	sealed record InterfaceImplementationLowering(TypeDefinition Type, InterfaceDefinition Interface, FieldDefinition? Field, VariableDefinition VTable, VariableDefinition VTableStorage, bool DirectEntries, bool IsStruct);

	sealed record InterfaceThunkLowering(InterfaceImplementationLowering Implementation, InterfaceDefinition EntryInterface, FunctionDefinition Member);

	sealed record VirtualSlot(FunctionDefinition Declaration, FunctionDefinition? Implementation, FieldDefinition Field);

	sealed record ThrowHandler(string ErrorType, DeclarationTarget ErrorTarget, string LabelName);

	sealed record LoopTransferTarget(string? BreakLabelName, string? ContinueLabelName);

	sealed record CleanupScope(List<Statement> Statements, bool RunBeforeCatch)
	{
		public string? ExitLabelName { get; set; }
		public DeclarationTarget? ReturnTarget { get; set; }
		public string ReturnType { get; set; } = "void";
	}

	sealed record VirtualClassLowering(ClassDefinition Class, ClassDefinition? BaseClass, VirtualClassLowering? BaseLowering, StructDefinition VTableType)
	{
		public FieldDefinition? Field { get; set; }
		public VariableDefinition? VTable { get; set; }
		public List<VirtualSlot> DeclaredSlots { get; } = [];
	}
}
