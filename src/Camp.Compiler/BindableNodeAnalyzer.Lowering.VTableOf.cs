using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly Dictionary<(ClassDefinition Class, string GenericName, string InterfaceName), FieldDefinition> vtableOfFields = [];

	static string VTableOfParameterName(TypeReference? type, TypeReference? interfaceType)
	{
		return "vtableof_" + VTableOfTypeName(type) + "_" + VTableOfTypeName(interfaceType);
	}

	static string VTableOfFieldName(TypeReference? type, TypeReference? interfaceType)
	{
		return "_vtableof_" + VTableOfTypeName(type) + "_" + VTableOfTypeName(interfaceType);
	}

	static string VTableOfTypeName(TypeReference? type)
	{
		return type switch
		{
			GenericParameterTypeReference generic => generic.Name,
			NamedTypeReference named => named.Name,
			TypeDefinitionReference definition => definition.Name,
			_ => BaseTypeName(type?.ResolvedType ?? ErrorType)
		};
	}

	void FinalizeVTableOfParameter(VTableOfParameterDefinition parameter, AnalysisScope scope)
	{
		parameter.Name = VTableOfParameterName(parameter.Type, parameter.InterfaceType);
		parameter.Symbol = parameter.Name;
		ValidateVTableOfRequest(parameter.Type, parameter.InterfaceType, parameter.SourceSyntax, scope);
	}

	string VTableOfParameterType(VTableOfParameterDefinition parameter)
	{
		return VTablePointerType(parameter.InterfaceType);
	}

	string BodyAnalyzeVTableOfExpressionCore(VTableOfExpression vtableOf, AnalysisScope typeScope)
	{
		if (vtableOf.Type is not null)
			AnalyzeType(vtableOf.Type, typeScope);
		if (vtableOf.InterfaceType is not null)
			AnalyzeType(vtableOf.InterfaceType, typeScope);
		ValidateVTableOfRequest(vtableOf.Type, vtableOf.InterfaceType, vtableOf.SourceSyntax, typeScope);
		return VTablePointerType(vtableOf.InterfaceType);
	}

	void ValidateVTableOfRequest(TypeReference? type, TypeReference? interfaceType, SyntaxNode? syntax, AnalysisScope scope)
	{
		if (interfaceType is null || !TryGetInterfaceDefinition(interfaceType, out InterfaceDefinition? interfaceDefinition) || interfaceDefinition is null)
		{
			Report(GetRange(syntax), $"vtableof interface type '{interfaceType?.ResolvedType ?? ErrorType}' is not an interface.");
			return;
		}

		string genericName = VTableOfTypeName(type);
		GenericParameter? genericParameter = type is GenericParameterTypeReference generic
			? generic.Parameter ?? FindGenericParameter(scope, generic.Name)
			: FindGenericParameter(scope, genericName);
		if (genericParameter is null)
		{
			if (TryGetConcreteVTableOfType(type, out TypeDefinition? concreteType) && concreteType is not null)
			{
				if (!ConcreteTypeImplementsInterface(concreteType, interfaceDefinition))
					Report(GetRange(syntax), $"Type '{concreteType.Name}' does not implement interface '{interfaceDefinition.Name}'.");
				return;
			}

			Report(GetRange(syntax), $"vtableof requires a generic type parameter constrained with 'implements' or a concrete type that implements the interface, not '{type?.ResolvedType ?? genericName}'.");
			return;
		}
		if (!genericParameter.RequiresImplementation)
		{
			Report(GetRange(syntax), $"Generic parameter '{genericName}' must be constrained with 'implements {interfaceDefinition.Name}' to use vtableof.");
			return;
		}

		if (!CanGenericParameterImplementInterface(genericParameter, interfaceDefinition))
			Report(GetRange(syntax), $"Generic parameter '{genericName}' is not constrained to implement interface '{interfaceDefinition.Name}'.");
	}

	static GenericParameter? FindGenericParameter(AnalysisScope scope, string name)
	{
		return scope.TryGetGenericParameter(name, out GenericParameter? parameter) ? parameter : null;
	}

	bool CanGenericParameterImplementInterface(GenericParameter parameter, InterfaceDefinition interfaceDefinition)
	{
		return parameter.Constraint is not null
			&& TryGetInterfaceDefinition(parameter.Constraint, out InterfaceDefinition? constraintInterface)
			&& constraintInterface is not null
			&& InterfaceContainsBase(constraintInterface, interfaceDefinition);
	}

	bool TryGetConcreteVTableOfType(TypeReference? type, out TypeDefinition? definition)
	{
		definition = null;
		type = type is null ? null : UnwrapTypeDeclarators(type);
		return type switch
		{
			TypeDefinitionReference { Definition: TypeDefinition typeDefinition } => (definition = typeDefinition) is not null,
			NamedTypeReference named => TryGetNamedTypeDefinition(named, out definition) && definition is not null,
			_ => false
		};
	}

	bool ConcreteTypeImplementsInterface(TypeDefinition type, InterfaceDefinition interfaceDefinition)
	{
		switch (type)
		{
			case ClassDefinition classDefinition:
				foreach (TypeReference baseType in classDefinition.LoweredInterfaceBaseTypes.Concat(classDefinition.BaseTypes))
				{
					if (TryGetInterfaceDefinition(baseType, out InterfaceDefinition? implementedInterface)
						&& implementedInterface is not null
						&& InterfaceContainsBase(implementedInterface, interfaceDefinition))
						return true;
				}
				foreach (TypeDefinition baseType in GetDirectBaseClasses(classDefinition))
				{
					if (baseType is ClassDefinition baseClass && ConcreteTypeImplementsInterface(baseClass, interfaceDefinition))
						return true;
				}
				return false;

			case StructDefinition structDefinition:
				foreach (TypeReference baseType in structDefinition.LoweredInterfaceBaseTypes.Concat(structDefinition.BaseTypes))
				{
					if (TryGetInterfaceDefinition(baseType, out InterfaceDefinition? implementedInterface)
						&& implementedInterface is not null
						&& InterfaceContainsBase(implementedInterface, interfaceDefinition))
						return true;
				}
				return false;

			default:
				return false;
		}
	}

	static string VTablePointerType(TypeReference? interfaceType)
	{
		string name = VTableOfTypeName(interfaceType);
		return name == ErrorType ? ErrorType : "const " + name + "*";
	}

	void GenerateVTableOfFields(ClassDefinition classDefinition)
	{
		HashSet<(string GenericName, string InterfaceName)> generated = [];
		foreach (FunctionDefinition function in classDefinition.Functions)
		{
			if (function.Modifier != FunctionModifier.Constructor && function.Name != InitNewMethodName)
				continue;

			foreach (ParameterDefinition parameter in function.Parameters)
			{
				if (parameter is not VTableOfParameterDefinition vtableOf)
					continue;

				string genericName = VTableOfTypeName(vtableOf.Type);
				string interfaceName = VTableOfTypeName(vtableOf.InterfaceType);
				if (!ClassHasGenericParameter(classDefinition, genericName) || !generated.Add((genericName, interfaceName)))
					continue;

				FieldDefinition field = new()
				{
					SourceSyntax = vtableOf.SourceSyntax,
					Name = VTableOfFieldName(vtableOf.Type, vtableOf.InterfaceType),
					Symbol = VTableOfFieldName(vtableOf.Type, vtableOf.InterfaceType),
						Type = vtableOf.InterfaceType is null || CloneType(vtableOf.InterfaceType) is not TypeReference interfaceType ? null : PointerTo(interfaceType),
					ResolvedType = VTablePointerType(vtableOf.InterfaceType)
				};
				classDefinition.Fields.Add(field);
				vtableOfFields[(classDefinition, genericName, interfaceName)] = field;
			}
		}
	}

	void InsertVTableOfFieldAssignments(FunctionDefinition function, TypeDefinition? containingType)
	{
		if (function.Body is null || function.Name != InitNewMethodName || containingType is not ClassDefinition classDefinition)
			return;

		List<Statement> assignments = [];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter is not VTableOfParameterDefinition vtableOf)
				continue;

			string genericName = VTableOfTypeName(vtableOf.Type);
			string interfaceName = VTableOfTypeName(vtableOf.InterfaceType);
			if (!vtableOfFields.TryGetValue((classDefinition, genericName, interfaceName), out FieldDefinition? field))
				continue;

			assignments.Add(new ExpressionStatement
			{
				SourceSyntax = parameter.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = parameter.SourceSyntax,
					Target = CreateVTableOfFieldReference(classDefinition, field, parameter.SourceSyntax),
					Operator = AssignmentOperator.Assign,
					Value = CreateVariableReference(parameter, parameter.ResolvedType ?? VTablePointerType(vtableOf.InterfaceType)),
					ResolvedType = field.ResolvedType
				}
			});
		}

		if (assignments.Count > 0)
			function.Body.Statements.InsertRange(0, assignments);
	}

	Expression LowerVTableOfExpression(VTableOfExpression vtableOf)
	{
		if (!IsGenericVTableOf(vtableOf, out string genericName, out string interfaceName))
		{
			string concreteType = VTableOfTypeName(vtableOf.Type);
			return CreateConcreteVTableExpression(concreteType, new VTableOfParameterDefinition
			{
				Type = vtableOf.Type,
				InterfaceType = vtableOf.InterfaceType,
				ResolvedType = VTablePointerType(vtableOf.InterfaceType)
			}, vtableOf.SourceSyntax);
		}

		if (FindVTableOfParameter(currentRewriteFunction, genericName, interfaceName) is VTableOfParameterDefinition parameter)
			return CreateVariableReference(parameter, parameter.ResolvedType ?? VTablePointerType(parameter.InterfaceType));

		if (currentRewriteContainingType is ClassDefinition classDefinition
			&& vtableOfFields.TryGetValue((classDefinition, genericName, interfaceName), out FieldDefinition? field))
		{
			if (currentRewriteFunction?.Modifier == FunctionModifier.Static)
			{
				Report(vtableOf.SourceSyntax, $"vtableof({genericName}: {interfaceName}) requires parameter '{VTableOfParameterName(vtableOf.Type, vtableOf.InterfaceType)}' in this static method.");
				return vtableOf;
			}

			return CreateVTableOfFieldReference(classDefinition, field, vtableOf.SourceSyntax);
		}

		Report(vtableOf.SourceSyntax, $"vtableof({genericName}: {interfaceName}) requires parameter '{VTableOfParameterName(vtableOf.Type, vtableOf.InterfaceType)}'.");
		return vtableOf;
	}

	static bool IsGenericVTableOf(VTableOfExpression vtableOf, out string genericName, out string interfaceName)
	{
		genericName = "";
		interfaceName = VTableOfTypeName(vtableOf.InterfaceType);
		if (vtableOf.Type is GenericParameterTypeReference)
		{
			genericName = VTableOfTypeName(vtableOf.Type);
			return true;
		}
		return false;
	}

	static VTableOfParameterDefinition? FindVTableOfParameter(FunctionDefinition? function, string genericName, string interfaceName)
	{
		if (function is null)
			return null;

		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter is VTableOfParameterDefinition vtableOf
				&& VTableOfTypeName(vtableOf.Type) == genericName
				&& VTableOfTypeName(vtableOf.InterfaceType) == interfaceName)
				return vtableOf;
		}

		return null;
	}

	MemberReferenceExpression CreateVTableOfFieldReference(ClassDefinition classDefinition, FieldDefinition field, SyntaxNode? syntax)
	{
		return new MemberReferenceExpression
		{
			SourceSyntax = syntax,
			Target = new ThisExpression
			{
				SourceSyntax = syntax,
				ResolvedType = classDefinition.Name
			},
			Name = field.Name,
			Member = field,
			ResolvedType = field.ResolvedType
		};
	}

	void AddImplicitVTableOfArguments(CallExpression call)
	{
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function))
			return;

		AddImplicitVTableOfArguments(call, function, constructedType: null);
	}

	void AddImplicitVTableOfArguments(CallExpression call, FunctionDefinition function, TypeReference? constructedType)
	{
		Dictionary<string, string> substitutions = GetGenericSubstitutions(call, function, constructedType);
		List<ParameterDefinition> parameters = GetCallableParametersForCall(function, IncludeExplicitThisArgument(call.Target, function));
		for (int i = 0; i < parameters.Count; i++)
		{
			if (parameters[i] is not VTableOfParameterDefinition vtableOf)
				continue;
			int argumentIndex = FindArgumentIndexForCallableParameter(call.Arguments, parameters, i);
			if (argumentIndex < call.Arguments.Count && call.Arguments[argumentIndex].Modifier == ArgumentModifier.None && call.Arguments[argumentIndex].Value is not WithinExpression { Expression: null })
				continue;

			string genericName = VTableOfTypeName(vtableOf.Type);
			if (!substitutions.TryGetValue(genericName, out string? concreteType))
				concreteType = vtableOf.Type?.ResolvedType ?? genericName;
			call.Arguments.Insert(System.Math.Min(argumentIndex, call.Arguments.Count), new ArgumentExpression
			{
				SourceSyntax = call.SourceSyntax ?? call.Target?.SourceSyntax,
				Value = CreateConcreteVTableExpression(concreteType, vtableOf, call.SourceSyntax ?? call.Target?.SourceSyntax),
				ResolvedType = vtableOf.ResolvedType ?? VTablePointerType(vtableOf.InterfaceType)
			});
		}
	}

	Expression CreateConcreteVTableExpression(string concreteType, VTableOfParameterDefinition vtableOf, SyntaxNode? syntax)
	{
		if (vtableOf.InterfaceType is null || !TryGetInterfaceDefinition(vtableOf.InterfaceType, out InterfaceDefinition? interfaceDefinition) || interfaceDefinition is null)
			return ErrorExpression(vtableOf.ResolvedType ?? ErrorType, syntax);

		TypeDefinition? type = GetTypeDefinition(concreteType);
		if (type is null)
		{
			Report(GetRange(syntax), $"Type '{concreteType}' does not implement interface '{interfaceDefinition.Name}'.");
			return ErrorExpression(vtableOf.ResolvedType ?? ErrorType, syntax);
		}

		InterfaceImplementationLowering? lowering = type switch
		{
			ClassDefinition classDefinition when TryFindInterfaceLowering(classDefinition, interfaceDefinition, out InterfaceImplementationLowering? found) => found,
			StructDefinition structDefinition when TryFindInterfaceLowering(structDefinition, interfaceDefinition, out InterfaceImplementationLowering? found) => found,
			_ => null
		};
		if (lowering is null)
		{
			Report(GetRange(syntax), $"Type '{concreteType}' does not implement interface '{interfaceDefinition.Name}'.");
			return ErrorExpression(vtableOf.ResolvedType ?? ErrorType, syntax);
		}

		return new VariableReferenceExpression
		{
			SourceSyntax = syntax,
			Variable = lowering.VTable,
			ResolvedType = lowering.VTable.ResolvedType ?? VTablePointerType(vtableOf.InterfaceType)
		};
	}

	static Expression ErrorExpression(string resolvedType, SyntaxNode? syntax)
	{
		return new LiteralExpression
		{
			SourceSyntax = syntax,
			Kind = LiteralKind.Null,
			Text = "null",
			Value = null,
			ResolvedType = resolvedType
		};
	}
}
