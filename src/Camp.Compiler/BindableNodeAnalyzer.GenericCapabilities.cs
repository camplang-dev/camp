namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly System.Collections.Generic.HashSet<CallExpression> preparedCallableCapabilities = [];

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
			if (slot is SizeOfParameterDefinition && ExactOperand(slot.Type, operand)) return true;
		if (scope.ContainingType is TypeDefinition containingType)
			foreach (FieldDefinition field in GetIteratorFields(containingType))
				if (field.CapabilitySource is SizeOfParameterDefinition size && ExactOperand(size.Type, operand)) return true;
		if (scope.CurrentFunction.Modifier != FunctionModifier.Static && scope.ContainingType is ClassDefinition owner)
			foreach (FunctionDefinition constructor in owner.Functions)
				if (constructor.Modifier == FunctionModifier.Constructor || constructor.Name == InitNewMethodName)
					foreach (ParameterDefinition slot in constructor.Parameters)
						if (slot is SizeOfParameterDefinition && ExactOperand(slot.Type, operand)) return true;
		return false;
	}

	VTableOfParameterDefinition? FindWitnessCapability(BodyScope scope, GenericParameter operand)
	{
		foreach (ParameterDefinition slot in scope.CurrentFunction.Parameters)
			if (slot is VTableOfParameterDefinition witness && ExactOperand(witness.Type, operand)) return witness;
		if (scope.ContainingType is TypeDefinition state)
			foreach (FieldDefinition field in GetIteratorFields(state))
				if (field.CapabilitySource is VTableOfParameterDefinition witness && ExactOperand(witness.Type, operand)) return witness;
		if (scope.CurrentFunction.Modifier != FunctionModifier.Static && scope.ContainingType is ClassDefinition owner)
			foreach (FunctionDefinition constructor in owner.Functions)
				if (constructor.Modifier == FunctionModifier.Constructor || constructor.Name == InitNewMethodName)
					foreach (ParameterDefinition slot in constructor.Parameters)
						if (slot is VTableOfParameterDefinition witness && ExactOperand(witness.Type, operand)) return witness;
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
			if (slot is SizeOfParameterDefinition && !IsCopyableResolvedType(ErasedTypeIdentity.ReadType(operand), scope, []))
				Report(GetRange(syntax), $"sizeof({operand}) capability requires a copyable argument, including when the slot is unused; pass a pointer for non-copyable storage.");
			foreach ((ArgumentExpression argument, ParameterDefinition parameter) in supplied)
			{
				if (!ReferenceEquals(parameter, slot) || implicitCapabilityArguments.ContainsKey(argument)) continue;
				TypeReference? actualOperand = argument.Value switch
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
					Report(GetRange(argument.SourceSyntax ?? syntax), $"Capability slot '{slot.Name}' requires evidence for exact operand '{operand}', not an ordinary payload or another operand.");
			}
			if (slot is VTableOfParameterDefinition witnessSlot)
			{
				string interfaceType = SubstituteGenericType(witnessSlot.InterfaceType?.ResolvedType ?? ErrorType, substitutions);
				ValidateVTableOfRequest(TypeReferenceForResolvedName(ErasedTypeIdentity.ReadType(operand)), TypeReferenceForResolvedName(interfaceType), syntax, typeScope);
			}
		}
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
