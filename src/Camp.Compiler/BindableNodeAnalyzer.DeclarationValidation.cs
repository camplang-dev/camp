using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
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

		foreach (FunctionDefinition function in owner.Functions)
		{
			foreach (InterfaceDefinition baseInterface in GetBaseInterfaces(owner))
			{
				foreach (FunctionDefinition inherited in baseInterface.Functions)
				{
					if (GetInvokerName(inherited) != GetInvokerName(function) || HasOverloadSelector(inherited) == HasOverloadSelector(function))
						continue;

					string category = HasOverloadSelector(inherited) ? "an overload family" : "an ordinary method";
					Report(GetNameRange(function), $"`{GetInvokerName(function)}` was inherited as {category}. A derived interface cannot change that method category.");
				}
			}
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

		bool foundRegisteredBase = false;
		foreach (TypeDefinition baseType in info.BaseTypes)
		{
			if (baseType is ClassDefinition)
			{
				foundRegisteredBase = true;
				yield return baseType;
			}
		}

		if (foundRegisteredBase)
			yield break;

		IEnumerable<TypeReference> baseTypes = definition switch
		{
			ClassDefinition classDefinition => classDefinition.BaseTypes,
			StructDefinition structDefinition => structDefinition.BaseTypes,
			InterfaceDefinition interfaceDefinition => interfaceDefinition.BaseTypes,
			_ => []
		};
		foreach (TypeReference baseType in baseTypes)
		{
			if (TryGetNamedTypeDefinition(baseType, out TypeDefinition? resolved) && resolved is ClassDefinition)
				yield return resolved;
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
		foreach (FunctionDefinition inherited in GetInheritedClassMethods(owner))
		{
			if (inherited.Modifier is not FunctionModifier.Virtual and not FunctionModifier.Abstract)
				continue;
			if (BuildMethodSignature(inherited).Equals(signature))
			{
				if (HasOverloadSelector(inherited) != HasOverloadSelector(function))
					Report(GetNameRange(function), $"Override '{GetCallableName(function)}' must preserve the base declaration's overload spelling.");
				return;
			}
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

		Report(GetNameRange(function), $"{function.Modifier} method '{function.Name}' must match an inherited virtual or abstract method.");
	}

	void ValidateInheritedMethodNames(ClassDefinition definition)
	{
		foreach (FunctionDefinition function in definition.Functions)
		{
			if (function.Modifier is FunctionModifier.Constructor)
				continue;
			if (IsGeneratedLifecycleMethodName(function.Name))
				continue;
			if (IsGeneratedVirtualImplementation(function))
				continue;

			MethodSignature signature = BuildMethodSignature(function);
			foreach (FunctionDefinition inherited in GetInheritedClassMethods(definition))
			{
				if (IsGeneratedVirtualImplementation(inherited))
					continue;

				if (GetInvokerName(inherited) == GetInvokerName(function) && HasOverloadSelector(inherited) != HasOverloadSelector(function))
				{
					string category = HasOverloadSelector(inherited) ? "an overload family" : "an ordinary method";
					Report(GetNameRange(function), $"`{GetInvokerName(function)}` was inherited as {category}. A derived type cannot change that method category.");
					break;
				}

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

		if (definition.Modifier == FunctionModifier.Abstract && definition.Body is { SourceSyntax: not null })
			Report(GetNameRange(definition), "Abstract methods may not have a body.");

		if (definition.Modifier == FunctionModifier.Virtual && definition.Body is null && !IsDestructorFunction(definition))
			Report(GetNameRange(definition), "Virtual methods must have a body; use abstract for bodyless dispatch slots.");

		ValidateClassTypeFunctionUse(definition, participatesInVirtualDispatch);
	}

	void ValidateClassTypeFunctionUse(FunctionDefinition definition, bool participatesInVirtualDispatch)
	{
		if (participatesInVirtualDispatch && ContainsClassTypeInNestedCallable(definition.ReturnType))
			Report(GetRange(definition.ReturnType?.SourceSyntax ?? definition.SourceSyntax), "'classtype' may not appear inside nested callable types.");

		foreach (ParameterDefinition parameter in definition.Parameters)
		{
			if (participatesInVirtualDispatch && ContainsClassTypeInNestedCallable(parameter.Type))
				Report(GetRange(parameter.Type?.SourceSyntax ?? parameter.SourceSyntax), "'classtype' may not appear inside nested callable types.");

			if (participatesInVirtualDispatch
				&& parameter.Modifier != ParameterModifier.Out
				&& parameter is not ThisParameterDefinition
				&& ContainsClassTypeReference(parameter.Type))
				Report(GetRange(parameter.Type?.SourceSyntax ?? parameter.SourceSyntax), "Virtual and abstract methods may use 'classtype' only in return types or out parameter types.");
		}
	}

	static bool ContainsClassTypeInNestedCallable(TypeReference? type)
	{
		return type switch
		{
			null => false,
			CallableTypeReference callable => ContainsClassTypeReference(callable),
			IterTypeReference iter when ContainsClassTypeReference(iter) => true,
			AttributedTypeReference attributed => ContainsClassTypeInNestedCallable(attributed.Type),
			GenericTypeReference generic => ContainsClassTypeInNestedCallable(generic.Type) || generic.TypeArguments.Any(ContainsClassTypeInNestedCallable),
			ArrayTypeReference array => ContainsClassTypeInNestedCallable(array.ElementType),
			FixedArrayTypeReference fixedArray => ContainsClassTypeInNestedCallable(fixedArray.ElementType),
			OptionalTypeReference optional => ContainsClassTypeInNestedCallable(optional.ElementType),
			PointerTypeReference pointer => ContainsClassTypeInNestedCallable(pointer.ElementType),
			ConstTypeReference constType => ContainsClassTypeInNestedCallable(constType.Type),
			VolatileTypeReference volatileType => ContainsClassTypeInNestedCallable(volatileType.Type),
			TargetTypeSpecTypeReference targetSpec => ContainsClassTypeInNestedCallable(targetSpec.Type),
			GroupedParamsTypeReference grouped => ContainsClassTypeInNestedCallable(grouped.StructType),
			MaterializedStructTypeReference materialized => ContainsClassTypeInNestedCallable(materialized.ParamsType),
			ThrownTypeReference thrown => ContainsClassTypeInNestedCallable(thrown.Type),
			TypeDefinitionReference definition => definition.TypeArguments.Any(ContainsClassTypeInNestedCallable),
			NamedTypeReference named => named.TypeArguments.Any(ContainsClassTypeInNestedCallable),
			_ => false
		};
	}

	static bool IsGeneratedLifecycleMethodName(string name)
	{
		return name is InitNewMethodName or CreateMethodName or DeleteMethodName or DestroyMethodName;
	}

	bool IsGeneratedVirtualImplementation(FunctionDefinition function)
	{
		foreach (FunctionDefinition implementation in virtualImplementations.Values)
		{
			if (ReferenceEquals(implementation, function))
				return true;
		}

		return false;
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

		if (parameter.Constraint is AnyTypeReference or CopyableTypeReference)
			return;

		if (parameter.Constraint is PrimitiveTypeReference primitive && IsIntegralPrimitive(primitive.Type))
			return;

		if (parameter.Constraint is PointerTypeReference { ElementType: PrimitiveTypeReference { Type: PrimitiveType.Void } })
			return;

		Report(GetRange(parameter.Constraint.SourceSyntax), $"Generic parameter '{parameter.Name}' must be constrained to any, copyable, an integral type, or implements Interface.");
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
		if (parameter is not { Type: TypeReference type } || parameter is ThisParameterDefinition or SizeOfParameterDefinition or NameOfParameterDefinition or VTableOfParameterDefinition)
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
}
