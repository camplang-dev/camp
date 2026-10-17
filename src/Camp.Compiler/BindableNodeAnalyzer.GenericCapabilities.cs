using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly System.Collections.Generic.HashSet<CallExpression> preparedCallableCapabilities = [];
	readonly Dictionary<ArgumentExpression, ParameterDefinition> boundCapabilityArguments = [];

	static bool SameBinder(GenericParameter? left, GenericParameter? right) => left is not null && right is not null
		&& ReferenceEquals(left.Binder ?? left, right.Binder ?? right) && left.Ordinal == right.Ordinal;

	GenericParameter? BoundParameter(TypeReference? type)
	{
		if (type is null) return null;
		type = UnwrapTypeDeclarators(type);
		if (typeRewrites.TryGetValue(type, out TypeReference? rewritten)) type = rewritten;
		return (type as GenericParameterTypeReference)?.Parameter;
	}

	bool ExactOperand(TypeReference? operand, GenericParameter parameter) => SameBinder(BoundParameter(operand), parameter);

	bool HasDeclarationCopyCapability(GenericParameter operand, AnalysisScope scope)
	{
		if (!operand.IsVoidRepresentation) return true;
		bool Matches(ParameterDefinition slot) => slot is SizeOfParameterDefinition
			&& (ExactOperand(slot.Type, operand) || BoundParameter(slot.Type) is null
				&& scope.TryGetGenericParameter(SizeOfTypeName(slot.Type), out GenericParameter? bound) && SameBinder(bound, operand));
		foreach (ParameterDefinition slot in scope.CapabilityParameters) if (Matches(slot)) return true;
		if (!scope.IsInStaticMemberScope() && scope.GetContainingType() is ClassDefinition owner)
			foreach (FunctionDefinition constructor in owner.Functions)
				if (constructor.Modifier == FunctionModifier.Constructor || constructor.Name == InitNewMethodName)
					foreach (ParameterDefinition slot in constructor.Parameters) if (Matches(slot)) return true;
		return false;
	}

	bool RequireGenericArrayElementStride(string arrayType, BodyScope scope, SyntaxNode? syntax, string operation)
	{
		string? element = TryGetArrayElementType(arrayType);
		GenericParameter? parameter = element is null ? null : FindBodyGenericParameter(scope, StripTopLevelValueQualifiers(element));
		if (parameter is null || !parameter.IsVoidRepresentation || HasSizeOfCapability(scope, parameter.Name)) return true;
		Report(GetRange(syntax), $"Cannot {operation} because element stride for void-represented parameter '{parameter.Name}' requires sizeof({parameter.Name}); a witness supplies dispatch, not copy/layout.");
		return false;
	}

	bool RequireGenericArrayMutableElement(string arrayType, BodyScope scope, SyntaxNode? syntax)
	{
		string? element = TryGetArrayElementType(arrayType);
		if (element is null || FindBodyGenericParameter(scope, StripTopLevelValueQualifiers(element)) is null) return true;
		if (!IsConstQualified(TryGetArrayElementType(arrayType) ?? ErrorType)) return true;
		Report(GetRange(syntax), "Cannot mutate an element through const T[].");
		return false;
	}

	bool HasSizeOfCapability(BodyScope scope, string genericName)
	{
		GenericParameter? operand = FindBodyGenericParameter(scope, genericName);
		if (operand is null) return false;
		foreach (ParameterDefinition slot in scope.CurrentFunction.Parameters)
			if (slot is SizeOfParameterDefinition && ExactOperand(slot.Type, operand)) return NoteCapabilityUse(scope, slot, retained: false);
		if (scope.ContainingType is TypeDefinition containingType)
			foreach (FieldDefinition field in GetIteratorFields(containingType))
				if (field.CapabilitySource is SizeOfParameterDefinition size && ExactOperand(size.Type, operand)) return NoteCapabilityUse(scope, size, retained: true);
		if (scope.CurrentFunction.Modifier != FunctionModifier.Static && scope.ContainingType is ClassDefinition owner)
			foreach (FunctionDefinition constructor in owner.Functions)
				if (constructor.Modifier == FunctionModifier.Constructor || constructor.Name == InitNewMethodName)
					foreach (ParameterDefinition slot in constructor.Parameters)
						if (slot is SizeOfParameterDefinition && ExactOperand(slot.Type, operand)) return NoteCapabilityUse(scope, slot, retained: true);
		return false;
	}

	// A lambda body is lowered into its own function. Capability evidence it relies on, whether an
	// enclosing parameter or the receiver's retained state, is captured through its context.
	sealed class LambdaCapabilityUse
	{
		public HashSet<ParameterDefinition> Parameters { get; } = [];
		public bool UsesRetained { get; set; }
	}

	readonly Dictionary<LambdaExpression, LambdaCapabilityUse> lambdaCapabilityUses = [];

	bool NoteCapabilityUse(BodyScope scope, ParameterDefinition slot, bool retained)
	{
		for (BodyScope? current = scope; current is not null; current = current.Parent)
		{
			if (current.Lambda is not LambdaExpression lambda)
				continue;
			// A capability slot the lambda declares for itself is used directly.
			if (!retained && LambdaDeclaresCapability(lambda, slot))
				break;
			if (!lambdaCapabilityUses.TryGetValue(lambda, out LambdaCapabilityUse? use))
				lambdaCapabilityUses[lambda] = use = new LambdaCapabilityUse();
			if (retained)
				use.UsesRetained = true;
			else
				use.Parameters.Add(slot);
		}
		return true;
	}

	static bool LambdaDeclaresCapability(LambdaExpression lambda, ParameterDefinition slot)
	{
		foreach (LambdaParameter parameter in lambda.Parameters)
			if (parameter.Parameter is ParameterDefinition own && own.GetType() == slot.GetType() && own.Name == slot.Name)
				return true;
		return false;
	}

	VTableOfParameterDefinition? FindWitnessCapability(BodyScope scope, GenericParameter operand)
	{
		foreach (ParameterDefinition slot in scope.CurrentFunction.Parameters)
			if (slot is VTableOfParameterDefinition witness && ExactOperand(witness.Type, operand)) { NoteCapabilityUse(scope, witness, retained: false); return witness; }
		if (scope.ContainingType is TypeDefinition state)
			foreach (FieldDefinition field in GetIteratorFields(state))
				if (field.CapabilitySource is VTableOfParameterDefinition witness && ExactOperand(witness.Type, operand)) { NoteCapabilityUse(scope, witness, retained: true); return witness; }
		if (scope.CurrentFunction.Modifier != FunctionModifier.Static && scope.ContainingType is ClassDefinition owner)
			foreach (FunctionDefinition constructor in owner.Functions)
				if (constructor.Modifier == FunctionModifier.Constructor || constructor.Name == InitNewMethodName)
					foreach (ParameterDefinition slot in constructor.Parameters)
						if (slot is VTableOfParameterDefinition witness && ExactOperand(witness.Type, operand)) { NoteCapabilityUse(scope, witness, retained: true); return witness; }
		return null;
	}

	// A signature carries at most one witness per bound parameter, and an instance method of a generic
	// class cannot replace the witness its constructor already retains for an owner parameter.
	void ValidateWitnessMultiplicity(FunctionDefinition function, TypeDefinition? owner, AnalysisScope scope)
	{
		if (function.Name == InitNewMethodName)
			return;
		Dictionary<(object Binder, int Ordinal), VTableOfParameterDefinition> seen = [];
		foreach (ParameterDefinition slot in function.Parameters)
		{
			if (slot is not VTableOfParameterDefinition witness)
				continue;
			GenericParameter? operand = BoundParameter(witness.Type) ?? FindGenericParameter(scope, VTableOfTypeName(witness.Type));
			if (operand is null)
				continue;
			if (seen.TryGetValue((operand.Binder ?? operand, operand.Ordinal), out VTableOfParameterDefinition? first))
			{
				Report(GetRange(witness.SourceSyntax), $"'{function.Name}' declares a second witness for '{operand.Name}' (vtableof({operand.Name}: {VTableOfTypeName(first.InterfaceType)}) and vtableof({operand.Name}: {VTableOfTypeName(witness.InterfaceType)})); a bound parameter has at most one witness, even for the same interface.");
				continue;
			}
			seen[(operand.Binder ?? operand, operand.Ordinal)] = witness;
			if (owner is ClassDefinition classOwner && function.Modifier is not (FunctionModifier.Constructor or FunctionModifier.Static)
				&& classOwner.GenericParameters.Exists(parameter => parameter.Name == operand.Name)
				&& RetainedConstructorWitness(classOwner, operand.Name) is VTableOfParameterDefinition retained)
				Report(GetRange(witness.SourceSyntax), $"Method '{function.Name}' cannot request a witness for '{operand.Name}': the constructor of '{classOwner.Name}' already retains vtableof({operand.Name}: {VTableOfTypeName(retained.InterfaceType)}), even for the same interface.");
		}
	}

	static VTableOfParameterDefinition? RetainedConstructorWitness(ClassDefinition owner, string genericName)
	{
		foreach (FunctionDefinition constructor in owner.Functions)
			if (constructor.Modifier == FunctionModifier.Constructor)
				foreach (ParameterDefinition slot in constructor.Parameters)
					if (slot is VTableOfParameterDefinition witness && VTableOfTypeName(witness.Type) == genericName)
						return witness;
		return null;
	}

	void ValidateCapabilityCall(System.Collections.Generic.List<ParameterDefinition> parameters,
		System.Collections.Generic.List<(ArgumentExpression Argument, ParameterDefinition Parameter)> supplied,
		System.Collections.Generic.Dictionary<string, string> substitutions, BodyScope scope, AnalysisScope typeScope, SyntaxNode? syntax)
	{
		foreach (ParameterDefinition slot in parameters)
		{
			if (slot is not SizeOfParameterDefinition and not NameOfParameterDefinition and not VTableOfParameterDefinition) continue;
			string operand = SubstituteGenericType(slot.Type?.ResolvedType ?? FormatTypeReference(slot.Type), substitutions);
			if (slot is SizeOfParameterDefinition)
			{
				string readOperand = ErasedTypeIdentity.ReadType(operand);
				// An abstract operand is copyable only through the caller's own sizeof evidence for that operand.
				if (FindBodyGenericParameter(scope, StripTopLevelValueQualifiers(readOperand)) is { IsVoidRepresentation: true } abstractOperand
					&& !HasSizeOfCapability(scope, abstractOperand.Name))
					Report(GetRange(syntax), $"sizeof({readOperand}) capability needs evidence for '{readOperand}' in the caller; declare sizeof({readOperand}) in its signature or pass a pointer.");
				else if (!IsCopyableResolvedType(readOperand, scope, []))
					Report(GetRange(syntax), $"sizeof({operand}) capability requires a copyable argument, including when the slot is unused; pass a pointer for non-copyable storage.");
			}
			if (!supplied.Exists(pair => ReferenceEquals(pair.Parameter, slot)))
				RequireForwardedEvidence(slot, ErasedTypeIdentity.ReadType(operand), scope, syntax);
			foreach ((ArgumentExpression argument, ParameterDefinition parameter) in supplied)
			{
				if (!ReferenceEquals(parameter, slot) || implicitCapabilityArguments.ContainsKey(argument)) continue;
				// A written evidence name such as sizeof_T is resolved to its parameter or retained field first.
				Expression? evidenceValue = argument.Value is { } written && expressionRewrites.TryGetValue(written, out Expression? resolved) ? resolved : argument.Value;
				TypeReference? actualOperand = evidenceValue switch
				{
					SizeOfExpression size when slot is SizeOfParameterDefinition => size.Type,
					NameOfExpression name when slot is NameOfParameterDefinition => TypeReferenceForResolvedName(name.Text),
					VTableOfExpression witness when slot is VTableOfParameterDefinition => witness.Type,
					VariableReferenceExpression { Variable: ParameterDefinition evidence } when evidence.GetType() == slot.GetType() => evidence.Type,
					MemberReferenceExpression { Member: FieldDefinition { CapabilitySource: ParameterDefinition evidence } } when evidence.GetType() == slot.GetType() => evidence.Type,
					_ => null
				};
				if (actualOperand is not null) AnalyzeType(actualOperand, typeScope);
				if (actualOperand is null || actualOperand.ResolvedType != operand)
					Report(GetRange(GetArgumentDiagnosticSyntax(argument, syntax)), $"Capability slot '{slot.Name}' requires evidence for exact operand '{operand}', not an ordinary payload or another operand.");
			}
			if (slot is VTableOfParameterDefinition witnessSlot)
			{
				// Interface arguments are the nominal types themselves, never the carrier form used for storage.
				string interfaceType = SubstituteGenericType(witnessSlot.InterfaceType?.ResolvedType ?? ErrorType, ReadSubstitutions(substitutions));
				ValidateVTableOfRequest(TypeReferenceForResolvedName(ErasedTypeIdentity.ReadType(operand)), TypeReferenceForResolvedName(interfaceType), syntax, typeScope);
			}
		}
	}

	static Dictionary<string, string> ReadSubstitutions(Dictionary<string, string> substitutions) =>
		substitutions.ToDictionary(static pair => pair.Key, static pair => ErasedTypeIdentity.ReadType(pair.Value), System.StringComparer.Ordinal);

	// An omitted capability argument is supplied from the caller. When its operand is the caller's own
	// abstract parameter the caller must hold evidence for exactly that operand.
	void RequireForwardedEvidence(ParameterDefinition slot, string operand, BodyScope scope, SyntaxNode? syntax)
	{
		if (slot is VTableOfParameterDefinition witnessSlot)
		{
			if (FindBodyGenericParameter(scope, StripTopLevelValueQualifiers(operand)) is not GenericParameter abstractOperand)
				return;
			string requested = witnessSlot.InterfaceType?.ResolvedType ?? ErrorType;
			VTableOfParameterDefinition? available = FindWitnessCapability(scope, abstractOperand);
			if (available?.InterfaceType is { } suppliedType
				&& witnessSlot.InterfaceType is { } neededType
				&& TryGetInterfaceDefinition(suppliedType, out InterfaceDefinition? supplied) && supplied is not null
				&& TryGetInterfaceDefinition(neededType, out InterfaceDefinition? needed) && needed is not null
				&& InterfaceContainsBase(supplied, needed))
				return;
			string held = available is null ? "" : $"; its witness vtableof({operand}: {available.InterfaceType?.ResolvedType}) does not supply it";
			Report(GetRange(syntax), $"vtableof({operand}: {requested}) requires evidence for '{operand}' in the caller; declare vtableof({operand}: {requested}) in its signature{held}.");
		}
		else if (slot is NameOfParameterDefinition && !HasNameOfCapability(scope, operand)
			&& ExtractGenericNames(operand, BodyGenericNames(scope)).Any())
			Report(GetRange(syntax), $"typenameof({operand}) requires evidence for '{operand}' in the caller; declare typenameof({operand}) in its signature.");
	}

	HashSet<string> BodyGenericNames(BodyScope scope)
	{
		HashSet<string> names = [];
		foreach (GenericParameter parameter in scope.CurrentFunction.GenericParameters)
			names.Add(parameter.Name);
		if (scope.ContainingType is TypeDefinition owner)
			foreach (GenericParameter parameter in owner.GenericParameters)
				names.Add(parameter.Name);
		return names;
	}

	void AddImplicitCallableCapabilityArguments(CallExpression call)
	{
		if (callTargets.ContainsKey(call) || preparedCallableCapabilities.Contains(call)) return;
		if (!callableInvocationParameters.TryGetValue(call, out var parameters))
		{
			// Expression rewriting may recreate a call after body binding. Recover
			// its source contract before flattening the callable's context.
			if (!TryGetCallableInvocationShape(call.Target?.ResolvedType, out CallableShape shape)) return;
			parameters = CreateStructuralCallableParameters(GetSourceCallableParameterTypes(shape), shape.Kind == "fn" ? 0 : 1);
			callableInvocationParameters[call] = parameters;
		}
		FunctionDefinition contract = new();
		contract.Parameters.AddRange(parameters);
		// Resolve abstract operands in the caller's bound scope before forwarding.
		foreach (ParameterDefinition slot in parameters)
		{
			if (slot is not SizeOfParameterDefinition and not NameOfParameterDefinition and not VTableOfParameterDefinition) continue;
			string operand = slot.Type?.ResolvedType ?? ErrorType;
			GenericParameter? bound = currentRewriteFunction?.GenericParameters.Find(parameter => parameter.Name == operand)
				?? currentRewriteContainingType?.GenericParameters.Find(parameter => parameter.Name == operand);
			if (bound is not null) slot.Type = new GenericParameterTypeReference { Name = operand, Parameter = bound, ResolvedType = operand };
		}
		AddImplicitSizeOfArguments(call, contract, constructedType: null);
		AddImplicitNameOfArguments(call, contract, constructedType: null);
		AddImplicitVTableOfArguments(call, contract, constructedType: null);
		preparedCallableCapabilities.Add(call);
	}
}
