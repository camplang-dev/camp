using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	// An implemented interface with its actual owner arguments. Type is the written constructed name
	// (Check<byte>); Owner maps each owner parameter to its normalized substitution (int<byte> for T: int).
	sealed record ConstructedInterface(InterfaceDefinition Definition, string Type, Dictionary<string, string> Owner);

	// Interface bases are moved aside during lowering, so both lists are the type's declared bases.
	static List<TypeReference> GetBaseTypeReferences(TypeDefinition definition) => definition switch
	{
		ClassDefinition classDefinition => [.. classDefinition.LoweredInterfaceBaseTypes, .. classDefinition.BaseTypes],
		StructDefinition structDefinition => [.. structDefinition.LoweredInterfaceBaseTypes, .. structDefinition.BaseTypes],
		InterfaceDefinition interfaceDefinition => interfaceDefinition.BaseTypes,
		_ => []
	};

	static List<TypeReference> WrittenTypeArguments(TypeReference type)
	{
		type = UnwrapTypeDeclarators(type);
		return type switch
		{
			NamedTypeReference named => named.TypeArguments,
			TypeDefinitionReference definition => definition.TypeArguments,
			_ => []
		};
	}

	// Every constructed interface a type implements: declared interfaces, their base interfaces and the
	// interfaces of base classes, each with the owner arguments written along the chain substituted in.
	List<ConstructedInterface> GetConstructedInterfaces(TypeDefinition implementation)
	{
		List<ConstructedInterface> result = [];
		HashSet<string> forwarded = [.. implementation.GenericParameters.Select(static parameter => parameter.Name)];
		// An interface filtered out by its requirement is still the declared base of the type.
		WithConditionalInterfaceTypeReferences(() => CollectConstructedInterfaces(implementation, null, forwarded, result, []));
		return result;
	}

	void CollectConstructedInterfaces(TypeDefinition type, Dictionary<string, string>? substitution, HashSet<string> forwarded, List<ConstructedInterface> result, HashSet<(TypeDefinition, string)> seen)
	{
		foreach (TypeReference baseType in GetBaseTypeReferences(type))
		{
			if (!TryGetNamedTypeDefinition(baseType, out TypeDefinition? definition) || definition is null)
				continue;

			// Generated passes revisit declarations whose arguments are not yet bound; read them as written.
			List<string> written = [.. WrittenTypeArguments(baseType).Select(argument => FormatSignatureTypeReference(argument, []))];
			if (substitution is { Count: > 0 })
				written = [.. written.Select(argument => SubstituteGenericType(argument, substitution))];

			// Named as the marker names it, so namespaced interfaces compare by one spelling.
			string constructedType = written.Count == 0 ? ResolvedNominalTypeName(definition) : $"{ResolvedNominalTypeName(definition)}<{string.Join(", ", written)}>";
			if (!seen.Add((definition, constructedType)))
				continue;

			Dictionary<string, string> chain = [];
			int count = System.Math.Min(definition.GenericParameters.Count, written.Count);
			for (int i = 0; i < count; i++)
				chain[definition.GenericParameters[i].Name] = written[i];

			if (definition is InterfaceDefinition interfaceDefinition)
				result.Add(new ConstructedInterface(interfaceDefinition, constructedType, BuildConstructedOwnerMap(interfaceDefinition, written, forwarded)));
			CollectConstructedInterfaces(definition, chain, forwarded, result, seen);
		}
	}

	// T: R with argument A becomes R<A>; an argument that is itself a forwarded binder keeps its name.
	Dictionary<string, string> BuildConstructedOwnerMap(TypeDefinition owner, List<string> arguments, HashSet<string> forwarded)
	{
		Dictionary<string, string> map = [];
		int count = System.Math.Min(owner.GenericParameters.Count, arguments.Count);
		for (int i = 0; i < count; i++)
		{
			GenericParameter parameter = owner.GenericParameters[i];
			string argument = arguments[i];
			string? carrier = ErasureWrapper(parameter);
			if (carrier is not (null or "void") && argument != ErrorType && !argument.StartsWith(UnresolvedType, System.StringComparison.Ordinal))
			{
				string payload = StripTopLevelValueQualifiers(argument);
				if (CarrierTypeIdentity.TryGetPayload(payload, out string carried) || ErasedTypeIdentity.TryGetPayload(payload, out carried))
					payload = carried;
				if (!forwarded.Contains(payload))
					argument = $"{carrier}<{payload}>";
			}
			map[parameter.Name] = argument;
		}
		return map;
	}

	// Owner arguments of `target` as seen through a closed receiver such as Derived<byte>*, following the
	// written base-interface chain so an inherited member sees its own owner's arguments.
	List<string>? ConstructedInterfaceArguments(string receiverType, InterfaceDefinition target)
	{
		string stripped = StripTopLevelValueQualifiers(receiverType.Trim());
		while (stripped.EndsWith('*'))
			stripped = stripped[..^1].TrimEnd();
		if (!typeDefinitions.TryGetValue(BaseTypeName(stripped), out TypeDefinition? receiver) || receiver is not InterfaceDefinition receiverInterface)
			return null;
		return ConstructedInterfaceArguments(receiverInterface, ExtractConstructedTypeArguments(stripped), target, []);
	}

	List<string>? ConstructedInterfaceArguments(InterfaceDefinition current, List<string> arguments, InterfaceDefinition target, HashSet<InterfaceDefinition> seen)
	{
		if (ReferenceEquals(current, target))
			return arguments;
		if (!seen.Add(current))
			return null;

		Dictionary<string, string> chain = [];
		int count = System.Math.Min(current.GenericParameters.Count, arguments.Count);
		for (int i = 0; i < count; i++)
			chain[current.GenericParameters[i].Name] = arguments[i];

		foreach (TypeReference baseType in current.BaseTypes)
		{
			if (!TryGetNamedTypeDefinition(baseType, out TypeDefinition? definition) || definition is not InterfaceDefinition baseInterface)
				continue;
			List<string> written = [.. WrittenTypeArguments(baseType).Select(argument => SubstituteGenericType(FormatSignatureTypeReference(argument, []), chain))];
			if (ConstructedInterfaceArguments(baseInterface, written, target, seen) is List<string> found)
				return found;
		}
		return null;
	}

	// A concrete type implements a construction exactly: Check<byte> is not Check<short>. The type's own
	// binders take the arguments written in its constructed name (GenStruct<short> implements Check<short>).
	bool ConcreteTypeImplementsConstruction(string concreteType, TypeDefinition definition, InterfaceDefinition target, string construction, out string implemented)
	{
		Dictionary<string, string> arguments = [];
		List<string> written = ExtractConstructedTypeArguments(concreteType);
		for (int i = 0; i < System.Math.Min(definition.GenericParameters.Count, written.Count); i++)
			arguments[definition.GenericParameters[i].Name] = written[i];

		List<string> found = [];
		foreach (ConstructedInterface constructed in GetConstructedInterfaces(definition).Where(candidate => ReferenceEquals(candidate.Definition, target)))
		{
			string actual = arguments.Count == 0 ? constructed.Type : SubstituteGenericType(constructed.Type, arguments);
			if (actual.Replace(" ", "") == construction.Replace(" ", ""))
			{
				implemented = actual;
				return true;
			}
			found.Add(actual);
		}
		implemented = string.Join("' and '", found);
		return false;
	}

	// A member reached through a witness takes its owner arguments from the witness's constructed interface,
	// never from inference over the call's arguments.
	bool TryGetWitnessInterfaceType(string receiverType, BodyScope scope, out string interfaceType)
	{
		interfaceType = "";
		string genericName = TryGetPointerElementType(receiverType) ?? BaseTypeName(receiverType);
		if (FindBodyGenericParameter(scope, genericName) is not GenericParameter parameter
			|| FindWitnessCapability(scope, parameter) is not VTableOfParameterDefinition { InterfaceType: TypeReference witnessInterface })
			return false;
		interfaceType = witnessInterface.ResolvedType ?? FormatTypeReference(witnessInterface);
		return true;
	}

	void AddWitnessOwnerSubstitutions(Expression? target, FunctionDefinition function, BodyScope scope, Dictionary<string, string> substitutions)
	{
		if (FindContainingType(function) is not InterfaceDefinition owner || owner.GenericParameters.Count == 0)
			return;
		string? receiver = target switch
		{
			MemberExpression member => member.Target?.ResolvedType,
			MemberReferenceExpression member => member.Target?.ResolvedType,
			_ => null
		};
		if (receiver is null || !TryGetWitnessInterfaceType(receiver, scope, out string interfaceType)
			|| ConstructedInterfaceArguments(interfaceType, owner) is not List<string> arguments)
			return;
		for (int i = 0; i < System.Math.Min(owner.GenericParameters.Count, arguments.Count); i++)
			substitutions[owner.GenericParameters[i].Name] = arguments[i];
	}

	ConstructedInterface? FindConstructedInterface(TypeDefinition implementation, InterfaceDefinition target, string? markerType)
	{
		List<ConstructedInterface> candidates = [.. GetConstructedInterfaces(implementation).Where(candidate => ReferenceEquals(candidate.Definition, target))];
		if (markerType is null)
			return candidates.FirstOrDefault();
		return candidates.FirstOrDefault(candidate => candidate.Type == markerType);
	}

	// Signature with owner arguments substituted and method-owned binders replaced by their ordinal, so
	// parameter names are immaterial while arity, representation order and slot order stay significant.
	// One simultaneous pass keeps an owner argument spelled like a method binder distinct from it.
	MethodSignature BuildBinderMappedSignature(FunctionDefinition function, bool includeLifecycleWithin, Dictionary<string, string>? owner)
	{
		Dictionary<string, string> map = owner is null ? [] : new Dictionary<string, string>(owner);
		for (int i = 0; i < function.GenericParameters.Count; i++)
			map[function.GenericParameters[i].Name] = "#M" + i;
		MethodSignature signature = BuildMethodSignature(function, includeLifecycleWithin);
		if (map.Count == 0)
			return signature;
		List<string> parameters = [.. signature.ParameterTypes.Select(text =>
		{
			CallableSlot slot = ParseMethodSignatureSlot(text);
			return slot.Modifier + ":" + SubstituteGenericType(slot.Type, map);
		})];
		return signature with { ReturnType = SubstituteGenericType(signature.ReturnType, map), ParameterTypes = parameters };
	}

	static List<string> CapabilitySlotSignatures(MethodSignature signature) => [.. signature.ParameterTypes
		.Select(static text => ParseMethodSignatureSlot(text).Type)
		.Where(static type => type.StartsWith("sizeof(", System.StringComparison.Ordinal)
			|| type.StartsWith("typenameof(", System.StringComparison.Ordinal)
			|| type.StartsWith("vtableof(", System.StringComparison.Ordinal))];

	// Reports why an implementation cannot fill a generic interface member: method arity, binder
	// representation, then capability contract; false when none of those differ.
	bool ReportGenericContractMismatch(FunctionDefinition function, FunctionDefinition member, InterfaceDefinition interfaceDefinition, MethodSignature declared, MethodSignature required, MethodSignature declaredMapped, MethodSignature requiredMapped)
	{
		string slot = $"{interfaceDefinition.Name}.{GetCallableName(member)}";
		if (function.GenericParameters.Count != member.GenericParameters.Count)
		{
			Report(GetNameRange(function), $"Method '{function.Name}' cannot implement '{slot}': it declares {function.GenericParameters.Count} generic parameter(s) but the interface member declares {member.GenericParameters.Count}; a generic interface member promises every argument, so the implementation must be generic with the same arity.");
			return true;
		}
		for (int i = 0; i < member.GenericParameters.Count; i++)
		{
			string have = function.GenericParameters[i].Representation;
			string need = member.GenericParameters[i].Representation;
			if (have != need)
			{
				Report(GetNameRange(function), $"Method '{function.Name}' cannot implement '{slot}': generic parameter '{function.GenericParameters[i].Name}' is represented as '{have}' but '{member.GenericParameters[i].Name}' requires '{need}'.");
				return true;
			}
		}
		if (!CapabilitySlotSignatures(declaredMapped).SequenceEqual(CapabilitySlotSignatures(requiredMapped)))
		{
			Report(GetNameRange(function), $"Method '{function.Name}' cannot implement '{slot}': its capability slots ({FormatCapabilityList(CapabilitySlotSignatures(declared))}) must match the interface member's ({FormatCapabilityList(CapabilitySlotSignatures(required))}) by kind, operand and order.");
			return true;
		}
		return false;
	}

	static string FormatCapabilityList(List<string> capabilities) => capabilities.Count == 0 ? "none" : string.Join(", ", capabilities);
}
