namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	enum GenericConstraintCategory
	{
		None,
		Representation,
		Any,
		Interface
	}

	record struct GenericArrayElementCapability(GenericParameter Parameter, GenericConstraintCategory Category, bool HasSizeOf, bool HasVTable);

	bool TryGetGenericArrayElementCapability(string arrayType, BodyScope scope, out GenericArrayElementCapability capability)
	{
		capability = default;
		if (TryGetArrayElementType(arrayType) is not string elementType)
			return false;

		string genericName = BaseTypeName(StripTopLevelValueQualifiers(elementType));
		GenericParameter? parameter = FindBodyGenericParameter(scope, genericName);
		if (parameter is null)
			return false;

		capability = new GenericArrayElementCapability(
			parameter,
			GetGenericConstraintCategory(parameter),
			HasSizeOfCapability(scope, genericName),
			HasVTableCapability(scope, genericName));
		return true;
	}

	bool RequireGenericArrayElementStride(string arrayType, BodyScope scope, SyntaxNode? syntax, string operation)
	{
		if (!TryGetGenericArrayElementCapability(arrayType, scope, out GenericArrayElementCapability capability))
			return true;

		if (capability.Category == GenericConstraintCategory.Representation || capability.HasSizeOf)
			return true;

		if (capability.HasVTable)
			Report(GetRange(syntax), $"vtableof({capability.Parameter.Name}: {capability.Parameter.Constraint?.ResolvedType ?? ErrorType}) supplies interface dispatch capability, not element stride. Add sizeof({capability.Parameter.Name}) to {operation}.");
		else
			Report(GetRange(syntax), $"Cannot {operation} because element stride for erased type parameter '{capability.Parameter.Name}' is unavailable. Add sizeof({capability.Parameter.Name}) to the parameter list or use a representation constraint.");
		return false;
	}

	void RequireGenericArrayMutableElement(string arrayType, BodyScope scope, SyntaxNode? syntax)
	{
		if (!TryGetGenericArrayElementCapability(arrayType, scope, out _))
			return;

		string elementType = TryGetArrayElementType(arrayType) ?? ErrorType;
		if (IsConstQualified(elementType))
			Report(GetRange(syntax), "Cannot mutate an element through const T[].");
	}

	GenericConstraintCategory GetGenericConstraintCategory(GenericParameter parameter)
	{
		if (parameter.RequiresImplementation)
			return GenericConstraintCategory.Interface;
		if (parameter.Constraint is null)
			return GenericConstraintCategory.Representation;
		if (parameter.Constraint is AnyTypeReference)
			return GenericConstraintCategory.Any;
		if (parameter.Constraint is PrimitiveTypeReference primitive && IsIntegralPrimitive(primitive.Type))
			return GenericConstraintCategory.Representation;
		if (parameter.Constraint is PointerTypeReference { ElementType: PrimitiveTypeReference { Type: PrimitiveType.Void } })
			return GenericConstraintCategory.Representation;
		return GenericConstraintCategory.None;
	}

	bool HasSizeOfCapability(BodyScope scope, string genericName)
	{
		if (FindSizeOfParameter(scope.CurrentFunction, genericName) is not null)
			return true;

		if (scope.ContainingType is TypeDefinition containingType)
		{
			string fieldName = SizeOfFieldName(new GenericParameterTypeReference { Name = genericName, ResolvedType = genericName });
			foreach (FieldDefinition field in GetIteratorFields(containingType))
				if (field.Name == fieldName && field.ResolvedType == "nuint")
					return true;
		}

		if (scope.ContainingType is ClassDefinition classDefinition)
		{
			foreach (FunctionDefinition function in classDefinition.Functions)
			{
				if (function.Modifier != FunctionModifier.Constructor && function.Name != InitNewMethodName)
					continue;
				if (FindSizeOfParameter(function, genericName) is not null)
					return true;
			}
		}

		return false;
	}

	static bool HasVTableCapability(BodyScope scope, string genericName)
	{
		foreach (ParameterDefinition parameter in scope.CurrentFunction.Parameters)
			if (parameter is VTableOfParameterDefinition vtableOf && VTableOfTypeName(vtableOf.Type) == genericName)
				return true;
		return false;
	}
}
