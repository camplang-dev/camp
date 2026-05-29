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
			if (parameter.DefaultValue is null)
				required++;
		}

		return required <= count && count <= callable;
	}

	static bool IsHiddenParameter(ParameterDefinition parameter)
	{
		return parameter.Modifier is ParameterModifier.Thrown or ParameterModifier.Within
			|| parameter is WithinParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition;
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
		Expression source = allocator is null ? StdDefaultAllocator() : CreateVariableReference(allocator, allocator.ResolvedType ?? "Allocator*");
		DeclarationStatement declaration = CreateGeneratedLocal("resolvedAllocator", "Allocator*", AllocatorPointerType(), new BinaryExpression
		{
			Left = source,
			Operator = BinaryOperator.NullCoalescing,
			Right = StdDefaultAllocator(),
			ResolvedType = "Allocator*"
		});
		declaration.Target.Names.Clear();
		declaration.Target.Names.Add("resolvedAllocator");
		return declaration;
	}

	Expression? GetFunctionAllocatorForBody(FunctionDefinition function)
	{
		if (function.Name == DeleteMethodName && GetWithinParameter(function) is ParameterDefinition deleteAllocator)
			return CreateVariableReference(deleteAllocator, deleteAllocator.ResolvedType ?? "Allocator*");
		if (function.Name == InitNewMethodName && GetWithinParameter(function) is not null)
			return CreateResolvedAllocatorReference();
		if (function.Name == InitNewMethodName || function.Name == DeleteMethodName)
			return StdDefaultAllocator();
		return null;
	}

	static NamedExpression CreateResolvedAllocatorReference()
	{
		return new NamedExpression
		{
			Name = "resolvedAllocator",
			ResolvedType = "Allocator*"
		};
	}

	static NamedExpression StdDefaultAllocator()
	{
		NamedExpression expression = new()
		{
			Name = "defaultAllocator",
			ResolvedType = "Allocator*"
		};
		expression.Qualifiers.Add("Std");
		return expression;
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
			CallableTypeReference callable => CloneCallable(callable),
			IterTypeReference iter => new IterTypeReference { ElementType = CloneType(iter.ElementType) },
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
		CallableTypeReference clone = new() { Kind = callable.Kind, ReturnType = CloneType(callable.ReturnType) };
		foreach (ParameterDefinition parameter in callable.Parameters)
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

	Expression CurrentAllocator()
	{
		return currentAllocatorOverride ?? new CurrentAllocatorExpression { ResolvedType = AllocatorType };
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

		return new TypeShapeParser(type).TryParse(out TypeShape shape) && shape.Kind == TypeShapeKind.Pointer
			? TypeShapeParser.Format(shape.Element)
			: type;
	}

	void Report(SyntaxNode? syntax, string message)
	{
		diagnostics.Add(new AnalysisDiagnostic(GetRange(syntax), message));
	}

	static FunctionDefinition CreateAllocatorAllocMethod()
	{
		FunctionDefinition method = new()
		{
			Name = "alloc",
			Symbol = "alloc",
			ResolvedType = "T*"
		};
		method.Parameters.Add(new ThisParameterDefinition { Name = "this", Symbol = "this", ResolvedType = "Allocator*" });
		method.Parameters.Add(new ParameterDefinition { Name = "len", Symbol = "len", ResolvedType = "nuint" });
		method.Parameters.Add(new SizeOfParameterDefinition { Name = "sizeof", Symbol = "sizeof", ResolvedType = "nuint" });
		method.Parameters.Add(new ParameterDefinition { Name = "MemoryError", Symbol = "MemoryError", Modifier = ParameterModifier.Thrown, ResolvedType = "MemoryError" });
		return method;
	}

	static FunctionDefinition CreateAllocatorFreeMethod()
	{
		FunctionDefinition method = new()
		{
			Name = "free",
			Symbol = "free",
			ResolvedType = "void"
		};
		method.Parameters.Add(new ThisParameterDefinition { Name = "this", Symbol = "this", ResolvedType = "Allocator*" });
		method.Parameters.Add(new ParameterDefinition { Name = "ptr", Symbol = "ptr", ResolvedType = "escaped void*" });
		return method;
	}

	sealed record InterfaceImplementationLowering(TypeDefinition Type, InterfaceDefinition Interface, FieldDefinition? Field, VariableDefinition VTable, bool DirectEntries, bool IsStruct);

	sealed record InterfaceThunkLowering(InterfaceImplementationLowering Implementation, InterfaceDefinition EntryInterface, FunctionDefinition Member);

	sealed record VirtualSlot(FunctionDefinition Declaration, FunctionDefinition? Implementation);

	sealed record ThrowHandler(string ErrorType, DeclarationTarget ErrorTarget, string LabelName);

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
