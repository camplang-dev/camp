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
				case ClassDefinition baseClass:
					baseClassCount++;
					if (baseClassCount > 1)
						Report(GetRange(baseType.SourceSyntax), $"{ownerKind} '{owner.Name}' may only declare one base class.");
					if (owner is ClassDefinition ownerClass && ownerClass.Extern is not null != (baseClass.Extern is not null))
						Report(GetRange(baseType.SourceSyntax), ownerClass.Extern is not null
							? "Extern classes may only inherit from extern classes."
							: "Non-extern classes may only inherit from non-extern classes.");
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
		if (typeInfos.TryGetValue(definition, out TypeAnalysisInfo? info))
		{
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
		}

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
		ValidateExternClass(definition);
		if (definition.Extern is not null)
			return;

		ValidateClassVirtualMethods(definition);
		ValidateInheritedMethodNames(definition);
		ValidateInterfaceImplementationMarkers(definition);
		ValidateDerivedOptionalInterfaceMethods(definition);

		foreach (InterfaceDefinition interfaceDefinition in GetImplementedInterfaces(definition))
			EnsureInterfaceImplemented(definition, interfaceDefinition);

		foreach (FunctionDefinition abstractMethod in GetInheritedAbstractMethods(definition))
		{
			if (abstractMethod.Name == DeleteMethodName && HasInheritedAbstractDestructorSlot(definition))
				continue;

			MethodSignature signature = BuildMethodSignature(abstractMethod);
			if (IsDestructorFunction(abstractMethod))
			{
				if (!ContainsOverrideSignature(definition.Functions, signature))
					Report(GetNameRange(definition), $"Class '{definition.Name}' must use override to implement inherited abstract destructor '{abstractMethod.Name}'.");
				continue;
			}

			if (!ContainsOverrideSignature(definition.Functions, signature))
				Report(GetNameRange(definition), $"Class '{definition.Name}' must use override to implement inherited abstract member '{signature.DisplayName}'.");
		}
	}

	bool HasInheritedAbstractDestructorSlot(ClassDefinition definition)
	{
		foreach (FunctionDefinition function in GetInheritedClassMethods(definition))
			if (IsDestructorFunction(function) && function.Modifier == FunctionModifier.Abstract)
				return true;
		return false;
	}

	void AnalyzeStructImplementations(StructDefinition definition)
	{
		foreach (FunctionDefinition function in definition.Functions)
		{
			if (function.Modifier is FunctionModifier.Virtual or FunctionModifier.Abstract or FunctionModifier.Override or FunctionModifier.Sealed)
				Report(GetNameRange(function), "Struct methods may not be virtual, abstract, override, or sealed.");
		}

		ValidateInterfaceImplementationMarkers(definition);

		foreach (InterfaceDefinition interfaceDefinition in GetImplementedInterfaces(definition))
			EnsureInterfaceImplemented(definition, interfaceDefinition);
	}

	void ValidateExternClass(ClassDefinition definition)
	{
		if (definition.Extern is null)
			return;

		if (definition.Modifier is ClassModifier.Virtual or ClassModifier.Abstract or ClassModifier.Sealed)
			Report(GetNameRange(definition), "Extern classes may not be virtual, abstract, or sealed.");

		foreach (FieldDefinition field in definition.Fields)
		{
			if (field.SourceSyntax is null)
				continue;
			if (field.Modifier != FieldModifier.Static)
				Report(GetNameRange(field), "Extern classes may not declare instance fields.");
		}

		foreach (FunctionDefinition function in definition.Functions)
		{
			if (function.Modifier is FunctionModifier.Virtual or FunctionModifier.Abstract or FunctionModifier.Override or FunctionModifier.Sealed)
				Report(GetNameRange(function), "Extern class methods may not be virtual, abstract, override, or sealed.");
			if ((function.Modifier == FunctionModifier.Constructor || IsDestructorFunction(function)) && function.Extern is null)
				Report(GetNameRange(function), "Extern class constructors and destructors must be extern.");
		}
	}

	void EnsureInterfaceImplemented(TypeDefinition implementation, InterfaceDefinition interfaceDefinition)
	{
		if (InterfaceRequiresConstructor(interfaceDefinition) && implementation is ClassDefinition { Modifier: not ClassModifier.Sealed })
			Report(GetNameRange(implementation), $"Interface '{interfaceDefinition.Name}' declares a constructor and may only be implemented by a sealed class or a struct.");

		foreach (FunctionDefinition member in GetInterfaceMembers(interfaceDefinition))
		{
			if (member.InterfaceSlotInitializer is not null)
				continue;

			MethodSignature required = BuildMethodSignature(member);
			if (FindMarkedInterfaceImplementation(implementation, member) is null)
			{
				if (FindUnmarkedSameNameInterfaceCandidate(implementation, member) is FunctionDefinition candidate)
				{
					Report(GetNameRange(candidate), $"Method '{candidate.Name}' does not implement interface member '{interfaceDefinition.Name}.{required.DisplayName}' because it is missing an explicit interface marker; add ': {interfaceDefinition.Name}'.");
				}
				else
				{
					Report(GetNameRange(implementation), $"Type '{implementation.Name}' does not implement interface member '{interfaceDefinition.Name}.{required.DisplayName}'.");
				}
			}
		}
	}

	void ValidateInterfaceImplementationMarkers(TypeDefinition implementation)
	{
		HashSet<FunctionDefinition> claimed = [];
		foreach (FunctionDefinition function in GetFunctions(implementation))
		{
			if (function.CallableAscriptionType is null)
				continue;

			string targetType = BaseTypeName(function.CallableAscriptionType.ResolvedType ?? ErrorType);
			if (!typeDefinitions.TryGetValue(targetType, out TypeDefinition? targetDefinition) || targetDefinition is not InterfaceDefinition interfaceDefinition)
				continue;

			function.InterfaceImplementationInterface = interfaceDefinition;
			if (!TypeImplementsInterface(implementation, interfaceDefinition))
			{
				Report(GetRange(function.CallableAscriptionType.SourceSyntax ?? function.SourceSyntax), $"Method '{function.Name}' cannot implement '{interfaceDefinition.Name}' because type '{implementation.Name}' does not implement that interface.");
				continue;
			}

			string slotName = function.InterfaceImplementationSlotName ?? GetCallableName(function);
			List<FunctionDefinition> slots = GetInterfaceMembers(interfaceDefinition)
				.Where(member => GetCallableName(member) == slotName)
				.ToList();
			if (slots.Count == 0)
			{
				Report(GetRange(function.CallableAscriptionType.SourceSyntax ?? function.SourceSyntax), $"Interface '{interfaceDefinition.Name}' does not declare a method named '{slotName}'.");
				continue;
			}
			if (slots.Count > 1)
			{
				Report(GetRange(function.CallableAscriptionType.SourceSyntax ?? function.SourceSyntax), $"Interface method marker '{interfaceDefinition.Name}.{slotName}' is ambiguous.");
				continue;
			}

			FunctionDefinition member = slots[0];
			function.InterfaceImplementationMember = member;
			MethodSignature declared = BuildMethodSignature(function);
			MethodSignature required = BuildMethodSignature(member);
			if (!MethodSignatureCompatibleWithConstOfVariance(declared, required, compareName: function.InterfaceImplementationSlotName is null))
			{
				Report(GetNameRange(function), $"Method '{declared.DisplayName}' is not compatible with interface member '{interfaceDefinition.Name}.{required.DisplayName}'.");
				continue;
			}

			string? expectedCallSpec = GetInterfaceMemberEffectiveCallSpec(member);
			if (!string.IsNullOrWhiteSpace(function.CallSpec) && function.CallSpec != expectedCallSpec)
				Report(GetRange(function.SourceSyntax), $"Method '{function.Name}' uses callspec '{function.CallSpec}', but interface member '{interfaceDefinition.Name}.{required.DisplayName}' requires {(string.IsNullOrWhiteSpace(expectedCallSpec) ? "no callspec" : $"callspec '{expectedCallSpec}'")}.");
			else if (string.IsNullOrWhiteSpace(function.CallSpec) && !string.IsNullOrWhiteSpace(expectedCallSpec))
				function.CallSpec = expectedCallSpec;

			function.CallableAscriptionNewtype = member.CallableAscriptionNewtype;

			if (!claimed.Add(member))
				Report(GetRange(function.CallableAscriptionType.SourceSyntax ?? function.SourceSyntax), $"Interface member '{interfaceDefinition.Name}.{required.DisplayName}' is already implemented by another method.");
		}
	}

	string? GetInterfaceMemberEffectiveCallSpec(FunctionDefinition member)
	{
		if (!string.IsNullOrWhiteSpace(member.CallSpec))
			return member.CallSpec;
		if (member.CallableAscriptionNewtype is not null && TryBuildNewtypeSourceCallableShape(member.CallableAscriptionNewtype, out CallableShape shape))
			return shape.CallSpec;
		return null;
	}

	FunctionDefinition? FindMarkedInterfaceImplementation(TypeDefinition implementation, FunctionDefinition member)
	{
		foreach (FunctionDefinition function in GetFunctions(implementation))
		{
			if (function.InterfaceImplementationMember == member)
				return function;
		}
		if (implementation is ClassDefinition classDefinition)
		{
			foreach (FunctionDefinition function in GetInheritedClassMethods(classDefinition))
			{
				if (function.InterfaceImplementationMember == member)
					return function;
			}
		}
		return null;
	}

	FunctionDefinition? FindUnmarkedSameNameInterfaceCandidate(TypeDefinition implementation, FunctionDefinition member)
	{
		string name = GetCallableName(member);
		foreach (FunctionDefinition function in GetFunctions(implementation))
		{
			if (function.CallableAscriptionType is not null && function.InterfaceImplementationInterface is not null)
				continue;
			if (GetCallableName(function) == name || function.Name == name)
				return function;
		}
		return null;
	}

	void AnalyzeInterfaceSlotInitializers(Module module)
	{
		foreach (Definition definition in module.Definitions)
		{
			if (definition is not InterfaceDefinition interfaceDefinition)
				continue;

			foreach (FunctionDefinition member in interfaceDefinition.Functions)
				AnalyzeInterfaceSlotInitializer(module, interfaceDefinition, member);
		}
	}

	void AnalyzeInterfaceSlotInitializer(Module module, InterfaceDefinition interfaceDefinition, FunctionDefinition member)
	{
		Expression? initializer = member.InterfaceSlotInitializer;
		if (initializer is null)
			return;

		if (member.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor)
		{
			Report(GetRange(initializer.SourceSyntax), "Interface constructors and destructors may not declare vtable initializers.");
			return;
		}

		if (IsNullInterfaceSlotInitializer(initializer))
		{
			member.InterfaceSlotInitializerKind = InterfaceSlotInitializerKind.Null;
			initializer.ResolvedType = BuildInterfaceSourceSlotCallableType(interfaceDefinition, member);
			return;
		}

		List<FunctionDefinition> candidates = ResolveInterfaceSlotInitializerCandidates(module, initializer);
		if (candidates.Count == 0)
		{
			Report(GetRange(initializer.SourceSyntax), "Interface method vtable initializer must be null, default, a free function, or a static method.");
			return;
		}
		if (candidates.Count > 1)
		{
			Report(GetRange(initializer.SourceSyntax), "Interface method vtable initializer is ambiguous.");
			return;
		}

		FunctionDefinition target = candidates[0];
		string expectedType = BuildInterfaceSourceSlotCallableType(interfaceDefinition, member);
		string actualType = BuildFunctionValueType(target, isInstance: false);
		CallableShape expected = BuildInterfaceSourceSlotCallableShape(interfaceDefinition, member);
		CallableShape actual = BuildFunctionSourceCallableShape(target, isInstance: false);
		bool compatible = CallableShapesCompatibleWithConstOfVariance(actual, expected, expandParams: false);
		if (!compatible
			&& TryGetCallableShape(actualType, out CallableShape resolvedActual)
			&& TryGetCallableShape(expectedType, out CallableShape resolvedExpected))
		{
			compatible = CallableShapesCompatibleWithConstOfVariance(resolvedActual, resolvedExpected);
		}
		if (!compatible)
		{
			Report(GetRange(initializer.SourceSyntax), $"Interface method initializer for '{interfaceDefinition.Name}.{GetCallableName(member)}' must match slot type '{expectedType}'.");
			return;
		}

		member.InterfaceSlotInitializerKind = InterfaceSlotInitializerKind.Function;
		member.InterfaceSlotInitializerTarget = target;
		initializer.ResolvedType = expectedType;
	}

	static bool IsNullInterfaceSlotInitializer(Expression initializer)
	{
		return initializer is LiteralExpression { Kind: LiteralKind.Null }
			|| initializer is DefaultExpression;
	}

	List<FunctionDefinition> ResolveInterfaceSlotInitializerCandidates(Module module, Expression initializer)
	{
		return initializer switch
		{
			NamedExpression named => ResolveInterfaceSlotFunctionCandidates(module, named.Name),
			MemberExpression { Target: NamedExpression targetType } member => ResolveInterfaceSlotStaticMethodCandidates(targetType.Name, member.Name),
			_ => []
		};
	}

	List<FunctionDefinition> ResolveInterfaceSlotFunctionCandidates(Module module, string name)
	{
		List<FunctionDefinition> candidates = [];
		foreach (Definition definition in module.Definitions)
		{
			if (definition is FunctionDefinition function && InterfaceSlotFunctionNameMatches(function, name))
				candidates.Add(function);
		}
		return candidates;
	}

	List<FunctionDefinition> ResolveInterfaceSlotStaticMethodCandidates(string typeName, string methodName)
	{
		if (!typeDefinitions.TryGetValue(typeName, out TypeDefinition? typeDefinition))
			return [];

		List<FunctionDefinition> candidates = [];
		foreach (FunctionDefinition function in GetTypeFunctions(typeDefinition))
		{
			if (function.Modifier == FunctionModifier.Static && InterfaceSlotFunctionNameMatches(function, methodName))
				candidates.Add(function);
		}
		return candidates;
	}

	static bool InterfaceSlotFunctionNameMatches(FunctionDefinition function, string name)
	{
		return function.Name == name
			|| function.Symbol == name
			|| GetCallableName(function) == name;
	}

	static string BuildInterfaceSourceSlotCallableType(InterfaceDefinition owner, FunctionDefinition member)
	{
		List<string> parameters = [];
		parameters.Add($"{owner.Name}*");
		foreach (ParameterDefinition parameter in member.Parameters)
		{
			if (parameter is ThisParameterDefinition)
				continue;
			parameters.Add(parameter.Modifier switch
			{
				ParameterModifier.In => "in " + (parameter.ResolvedType ?? ErrorType),
				ParameterModifier.Out => "out " + (parameter.ResolvedType ?? ErrorType),
				ParameterModifier.Thrown => "thrown " + (parameter.ResolvedType ?? ErrorType),
				ParameterModifier.Within => "within " + (parameter.ResolvedType ?? ErrorType),
				ParameterModifier.Upon => "upon " + (parameter.ResolvedType ?? ErrorType),
				_ => parameter.ResolvedType ?? ErrorType
			});
		}
		return $"fn {member.ResolvedType ?? ErrorType}({string.Join(", ", parameters)})";
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

		ValidateVirtualHierarchyDestructorRules(definition);

		foreach (FunctionDefinition function in definition.Functions)
		{
			if (function.Modifier == FunctionModifier.Virtual && definition.Modifier is not ClassModifier.Virtual and not ClassModifier.Abstract)
				Report(GetNameRange(function), "Virtual methods may only be declared in virtual or abstract classes.");

			if (function.Modifier == FunctionModifier.Abstract && definition.Modifier != ClassModifier.Abstract)
				Report(GetNameRange(function), "Abstract methods may only be declared in abstract classes.");

			if (function.Modifier is FunctionModifier.Override or FunctionModifier.Sealed)
			{
				if (IsGeneratedDestructorDeleteHelper(definition, function))
					continue;
				if (IsDestructorFunction(function) && !HasInheritedVirtualOrAbstractDestructor(definition))
					continue;
				ValidateOverrideMethod(definition, function);
			}
		}
	}

	void ValidateVirtualHierarchyDestructorRules(ClassDefinition definition)
	{
		if (!ClassHierarchyParticipatesInVirtualDispatch(definition))
			return;

		ClassDefinition ultimateBase = GetUltimateBaseClass(definition);
		FunctionDefinition? ultimateDestructor = FindDeclaredDestructor(ultimateBase);
		foreach (FunctionDefinition destructor in definition.Functions)
		{
			if (!IsDestructorFunction(destructor))
				continue;

			if (ultimateDestructor is null)
			{
				Report(GetNameRange(destructor), $"Destructor '{destructor.Name}' cannot introduce a destructor in a virtual hierarchy; the ultimate base class '{ultimateBase.Name}' must declare a virtual or abstract destructor.");
				continue;
			}

			if (ultimateDestructor.Modifier is not FunctionModifier.Virtual and not FunctionModifier.Abstract)
			{
				Report(GetNameRange(destructor), $"Destructor '{destructor.Name}' cannot introduce a destructor in a virtual hierarchy; the ultimate base class '{ultimateBase.Name}' declares a non-virtual destructor.");
				continue;
			}

			if (ReferenceEquals(definition, ultimateBase))
				continue;

			if (destructor.Modifier is not FunctionModifier.Override and not FunctionModifier.Sealed)
				Report(GetNameRange(destructor), $"Destructor '{destructor.Name}' must use override to implement inherited virtual destructor '{ultimateDestructor.Name}'.");
		}
	}

	bool ClassHierarchyParticipatesInVirtualDispatch(ClassDefinition definition)
	{
		foreach (ClassDefinition classDefinition in EnumerateClassAndBaseDefinitions(definition))
		{
			if (classDefinition.Modifier is ClassModifier.Virtual or ClassModifier.Abstract or ClassModifier.Sealed)
				return true;
		}

		return false;
	}

	ClassDefinition GetUltimateBaseClass(ClassDefinition definition)
	{
		ClassDefinition current = definition;
		while (GetDirectBaseClass(current) is ClassDefinition baseClass)
			current = baseClass;
		return current;
	}

	IEnumerable<ClassDefinition> EnumerateClassAndBaseDefinitions(ClassDefinition definition)
	{
		for (ClassDefinition? current = definition; current is not null; current = GetDirectBaseClass(current))
			yield return current;
	}

	static FunctionDefinition? FindDeclaredDestructor(ClassDefinition definition)
	{
		foreach (FunctionDefinition function in definition.Functions)
			if (IsDestructorFunction(function))
				return function;
		return null;
	}

	static bool IsGeneratedDestructorDeleteHelper(ClassDefinition owner, FunctionDefinition function)
	{
		if (function.Name != DeleteMethodName)
			return false;

		foreach (FunctionDefinition candidate in owner.Functions)
		{
			if (IsDestructorFunction(candidate) && ReferenceEquals(candidate.SourceSyntax, function.SourceSyntax))
				return true;
		}
		return false;
	}

	bool HasInheritedVirtualOrAbstractDestructor(ClassDefinition definition)
	{
		foreach (FunctionDefinition function in GetInheritedClassMethods(definition))
		{
			if (IsDestructorFunction(function) && function.Modifier is (FunctionModifier.Virtual or FunctionModifier.Abstract))
				return true;
		}
		return false;
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
			if (IsDestructorFunction(function) && ClassHierarchyParticipatesInVirtualDispatch(definition))
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

	void ValidateDerivedOptionalInterfaceMethods(ClassDefinition definition)
	{
		List<MethodSignature> inheritedClassSignatures = GetInheritedClassMethods(definition)
			.Select(BuildMethodSignature)
			.ToList();
		HashSet<string> reported = [];

		foreach (TypeDefinition baseType in GetDirectBaseClasses(definition))
		{
			if (baseType is not ClassDefinition baseClass)
				continue;

			foreach (InterfaceDefinition interfaceDefinition in GetImplementedInterfaces(baseClass))
			{
				foreach (FunctionDefinition member in GetInterfaceMembers(interfaceDefinition))
				{
					if (member.InterfaceSlotInitializer is null)
						continue;

					MethodSignature optionalSignature = BuildMethodSignature(member);
					if (ContainsSignature(inheritedClassSignatures, optionalSignature))
						continue;

					foreach (FunctionDefinition function in definition.Functions)
					{
						if (function.InterfaceImplementationMember != member)
							continue;

						MethodSignature declaredSignature = BuildMethodSignature(function);
						if (!MethodSignatureCompatibleWithConstOfVariance(declaredSignature, optionalSignature, compareName: function.InterfaceImplementationSlotName is null))
							continue;

						string key = $"{function.Name}|{interfaceDefinition.Name}|{optionalSignature.DisplayName}";
						if (!reported.Add(key))
							continue;

						Report(GetNameRange(function), $"Method '{declaredSignature.DisplayName}' cannot be declared here because it would implement optional interface member '{interfaceDefinition.Name}.{optionalSignature.DisplayName}' inherited through base class '{baseClass.Name}'. Optional interface members inherited through a base class cannot be introduced in derived classes.");
					}
				}
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
		return GetImplementedInterfaces(definition, []);
	}

	IEnumerable<InterfaceDefinition> GetImplementedInterfaces(TypeDefinition definition, HashSet<TypeDefinition> seenTypes)
	{
		if (!seenTypes.Add(definition))
			yield break;

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

		foreach (TypeDefinition baseClass in GetDirectBaseClasses(definition))
		{
			foreach (InterfaceDefinition inherited in GetImplementedInterfaces(baseClass, seenTypes))
				yield return inherited;
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
			if (MethodSignatureCompatibleWithConstOfVariance(signature, required))
				return true;
		}

		return false;
	}

	static bool MethodSignatureCompatibleWithConstOfVariance(MethodSignature candidate, MethodSignature target, bool compareName = true)
	{
		if ((compareName && candidate.Name != target.Name)
			|| candidate.ReceiverContract != target.ReceiverContract
			|| candidate.ParameterTypes.Count != target.ParameterTypes.Count
			|| !CallableSlotTypesCompatible(candidate.ReturnType, target.ReturnType, outputPosition: true))
			return false;

		for (int i = 0; i < candidate.ParameterTypes.Count; i++)
		{
			CallableSlot candidateSlot = ParseMethodSignatureSlot(candidate.ParameterTypes[i]);
			CallableSlot targetSlot = ParseMethodSignatureSlot(target.ParameterTypes[i]);
			if (candidateSlot.Modifier != targetSlot.Modifier)
				return false;
			if (candidateSlot.Modifier == "Thrown")
			{
				if (candidateSlot.Type != targetSlot.Type)
					return false;
				continue;
			}
			bool outputPosition = candidateSlot.Modifier == "Out";
			if (!CallableSlotTypesCompatible(candidateSlot.Type, targetSlot.Type, outputPosition))
				return false;
		}
		return true;
	}

	static CallableSlot ParseMethodSignatureSlot(string text)
	{
		int separator = text.IndexOf(':', StringComparison.Ordinal);
		return separator < 0
			? new CallableSlot("", text)
			: new CallableSlot(text[..separator], text[(separator + 1)..]);
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
			ConstOfTypeReference constOf => ContainsClassTypeInNestedCallable(constOf.Type),
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

		if (!IsDirectExternClassType(type) && IsFixedOrClassLikeType(type))
			Report(GetNameRange(parameter), "Fixed structs and classes must be passed by reference.");
	}

	bool IsAnyOrAnyConstrainedGeneric(TypeReference type, AnalysisScope scope)
	{
		return type switch
		{
			AnyTypeReference => true,
			NamedTypeReference named when scope.TryGetGenericParameter(named.Name, out GenericParameter? parameter) => parameter is { Constraint: AnyTypeReference },
			ConstTypeReference { Type: not null } constType => IsAnyOrAnyConstrainedGeneric(constType.Type, scope),
			ConstOfTypeReference { Type: not null } constOf => IsAnyOrAnyConstrainedGeneric(constOf.Type, scope),
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
