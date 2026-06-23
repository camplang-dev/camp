namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	string RefineClassTypeCallReturn(FunctionDefinition function, Expression? callTarget, string returnType)
	{
		if (!ContainsClassTypeReference(function.ReturnType)
			|| FindContainingType(function) is not ClassDefinition owner
			|| !TryGetClassTypeCallSiteName(callTarget, function, out string classTypeName)
			|| classTypeName == owner.Name && !returnType.Contains("classtype", System.StringComparison.Ordinal))
			return returnType;

		string sourceReturnType = FormatTypeReference(function.ReturnType);
		if (!TryParseTypeShape(sourceReturnType, out TypeShape shape))
			return returnType;

		TypeShape substituted = SubstituteClassTypeShape(shape, owner.Name, classTypeName);
		return TypeShapeParser.Format(substituted);
	}

	TypeShape SubstituteClassTypeShape(TypeShape shape, string ownerName, string classTypeName)
	{
		TypeShape? element = shape.Element is null ? null : SubstituteClassTypeShape(shape.Element, ownerName, classTypeName);
		string name = shape.Kind == TypeShapeKind.Named && (shape.Name == ownerName || shape.Name == "classtype") ? classTypeName : shape.Name;
		return shape with { Name = name, Element = element };
	}
}
