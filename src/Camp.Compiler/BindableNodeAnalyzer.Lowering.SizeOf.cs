using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly Dictionary<(ClassDefinition Class, string GenericName), FieldDefinition> sizeOfFields = [];

	static string SizeOfParameterName(TypeReference? type)
	{
		return "sizeof_" + SizeOfTypeName(type);
	}

	static string SizeOfFieldName(TypeReference? type)
	{
		return "_sizeof_" + SizeOfTypeName(type);
	}

	static string SizeOfTypeName(TypeReference? type)
	{
		return type switch
		{
			GenericParameterTypeReference generic => generic.Name,
			NamedTypeReference named => named.Name,
			TypeDefinitionReference definition => definition.Name,
			_ => type?.ResolvedType ?? ErrorType
		};
	}

	void GenerateSizeOfFields(ClassDefinition classDefinition)
	{
		HashSet<string> generated = [];
		foreach (FunctionDefinition function in classDefinition.Functions)
		{
			if (function.Modifier != FunctionModifier.Constructor && function.Name != InitNewMethodName)
				continue;

			foreach (ParameterDefinition parameter in function.Parameters)
			{
				if (parameter is not SizeOfParameterDefinition sizeOf)
					continue;

				string genericName = SizeOfTypeName(sizeOf.Type);
				if (!ClassHasGenericParameter(classDefinition, genericName) || !generated.Add(genericName))
					continue;

				FieldDefinition field = new()
				{
					SourceSyntax = sizeOf.SourceSyntax,
					Name = SizeOfFieldName(sizeOf.Type),
					Symbol = SizeOfFieldName(sizeOf.Type),
					Type = NuintType(),
					ResolvedType = "nuint"
				};
				classDefinition.Fields.Add(field);
				sizeOfFields[(classDefinition, genericName)] = field;
			}
		}
	}

	static bool ClassHasGenericParameter(ClassDefinition classDefinition, string name)
	{
		foreach (GenericParameter parameter in classDefinition.GenericParameters)
		{
			if (parameter.Name == name)
				return true;
		}
		return false;
	}

	void InsertSizeOfFieldAssignments(FunctionDefinition function, TypeDefinition? containingType)
	{
		if (function.Body is null || function.Name != InitNewMethodName || containingType is not ClassDefinition classDefinition)
			return;

		List<Statement> assignments = [];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter is not SizeOfParameterDefinition sizeOf)
				continue;

			string genericName = SizeOfTypeName(sizeOf.Type);
			if (!sizeOfFields.TryGetValue((classDefinition, genericName), out FieldDefinition? field))
				continue;

			assignments.Add(new ExpressionStatement
			{
				SourceSyntax = parameter.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = parameter.SourceSyntax,
					Target = CreateSizeOfFieldReference(classDefinition, field, parameter.SourceSyntax),
					Operator = AssignmentOperator.Assign,
					Value = CreateVariableReference(parameter, "nuint"),
					ResolvedType = "nuint"
				}
			});
		}

		if (assignments.Count > 0)
			function.Body.Statements.InsertRange(0, assignments);
	}

	Expression LowerSizeOfExpression(SizeOfExpression sizeOf)
	{
		if (!IsGenericSizeOf(sizeOf, out string genericName))
			return sizeOf;

		if (FindSizeOfParameter(currentRewriteFunction, genericName) is SizeOfParameterDefinition parameter)
			return CreateVariableReference(parameter, "nuint");

		if (currentRewriteContainingType is ClassDefinition classDefinition
			&& sizeOfFields.TryGetValue((classDefinition, genericName), out FieldDefinition? field))
		{
			if (currentRewriteFunction?.Modifier == FunctionModifier.Static)
			{
				Report(sizeOf.SourceSyntax, $"sizeof({genericName}) requires parameter '{SizeOfParameterName(sizeOf.Type)}' in this static method.");
				return sizeOf;
			}

			return CreateSizeOfFieldReference(classDefinition, field, sizeOf.SourceSyntax);
		}

		if (ThisMemberReference(SizeOfParameterName(sizeOf.Type), "nuint") is { Member: FieldDefinition } iteratorSizeOfField)
			return iteratorSizeOfField;

		Report(sizeOf.SourceSyntax, $"sizeof({genericName}) requires parameter '{SizeOfParameterName(sizeOf.Type)}'.");
		return sizeOf;
	}

	static bool IsGenericSizeOf(SizeOfExpression sizeOf, out string genericName)
	{
		genericName = "";
		TypeReference? type = sizeOf.Type;
		if (type is GenericParameterTypeReference generic)
		{
			genericName = generic.Name;
			return true;
		}
		return false;
	}

	static SizeOfParameterDefinition? FindSizeOfParameter(FunctionDefinition? function, string genericName)
	{
		if (function is null)
			return null;

		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter is SizeOfParameterDefinition sizeOf && SizeOfTypeName(sizeOf.Type) == genericName)
				return sizeOf;
		}

		return null;
	}

	MemberReferenceExpression CreateSizeOfFieldReference(ClassDefinition classDefinition, FieldDefinition field, SyntaxNode? syntax)
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
			ResolvedType = "nuint"
		};
	}

	void AddImplicitSizeOfArguments(CallExpression call)
	{
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function))
			return;

		AddImplicitSizeOfArguments(call, function, constructedType: null);
	}

	void AddImplicitSizeOfArguments(CallExpression call, FunctionDefinition function, TypeReference? constructedType)
	{
		Dictionary<string, string> substitutions = GetGenericSubstitutions(call, function, constructedType);
		bool includeExplicitThis = IncludeExplicitThisArgument(call.Target, function);
		if (!includeExplicitThis
			&& IsInstanceFunction(function)
			&& call.Target is not MemberExpression and not MemberReferenceExpression
			&& call.Arguments.Count > GetCallableParameters(function.Parameters, includeExplicitThis: false).Count)
		{
			includeExplicitThis = true;
		}
		List<ParameterDefinition> parameters = GetCallableParametersForCall(function, includeExplicitThis);
		for (int i = 0; i < parameters.Count; i++)
		{
			if (parameters[i] is not SizeOfParameterDefinition sizeOf)
				continue;
			int argumentIndex = FindArgumentIndexForCallableParameter(call.Arguments, parameters, i);
			if (argumentIndex < call.Arguments.Count
				&& call.Arguments[argumentIndex].Modifier == ArgumentModifier.None
				&& (call.Arguments[argumentIndex].Value is SizeOfExpression || IsGeneratedHiddenForwardingArgumentFor(sizeOf, call.Arguments[argumentIndex])))
				continue;

			string genericName = SizeOfTypeName(sizeOf.Type);
			string concreteType = substitutions.TryGetValue(genericName, out string? substituted)
				? substituted
				: sizeOf.Type?.ResolvedType ?? genericName;
			SizeOfExpression value = new()
			{
				SourceSyntax = call.SourceSyntax ?? call.Target?.SourceSyntax,
				Type = concreteType == genericName
					? CloneType(sizeOf.Type)
					: TypeReferenceForResolvedName(concreteType),
				ResolvedType = "nuint"
			};
			int insertIndex = Math.Min(argumentIndex, call.Arguments.Count);
			call.Arguments.Insert(insertIndex, new ArgumentExpression
			{
				SourceSyntax = call.SourceSyntax ?? call.Target?.SourceSyntax,
				Value = IsGenericSizeOf(value, out _) ? LowerSizeOfExpression(value) : value,
				ResolvedType = "nuint"
			});
		}
	}

	Dictionary<string, string> GetGenericSubstitutions(CallExpression call, FunctionDefinition function, TypeReference? constructedType)
	{
		Dictionary<string, string> substitutions = callGenericSubstitutions.TryGetValue(call, out Dictionary<string, string>? existing)
			? new Dictionary<string, string>(existing, StringComparer.Ordinal)
			: [];

		AddFunctionTypeArgumentSubstitutions(function, call.TypeArguments, substitutions);
		if (constructedType is not null && FindContainingType(function) is TypeDefinition containingType)
			AddTypeArgumentSubstitutions(containingType.GenericParameters, GetTypeArguments(constructedType), substitutions);
		return substitutions;
	}

	static void AddFunctionTypeArgumentSubstitutions(FunctionDefinition function, List<TypeReference> typeArguments, Dictionary<string, string> substitutions)
	{
		int count = Math.Min(function.GenericParameters.Count, typeArguments.Count);
		for (int i = 0; i < count; i++)
			substitutions[function.GenericParameters[i].Name] = typeArguments[i].ResolvedType ?? ErrorType;
	}

	static void AddTypeArgumentSubstitutions(List<GenericParameter> parameters, List<TypeReference> typeArguments, Dictionary<string, string> substitutions)
	{
		int count = Math.Min(parameters.Count, typeArguments.Count);
		for (int i = 0; i < count; i++)
			substitutions[parameters[i].Name] = typeArguments[i].ResolvedType ?? ErrorType;
	}

	static List<TypeReference> GetTypeArguments(TypeReference type)
	{
		return type switch
		{
			NamedTypeReference named => named.TypeArguments,
			TypeDefinitionReference definition => definition.TypeArguments,
			GenericTypeReference generic => generic.TypeArguments,
			_ => []
		};
	}

	static TypeReference TypeReferenceForResolvedName(string typeName)
	{
		if (typeName.Contains('[', StringComparison.Ordinal) && new TypeShapeParser(typeName).TryParse(out TypeShape shape))
			return TypeReferenceForTypeShape(shape);
		if (TryCreatePrimitivePointerTypeReference(typeName, out TypeReference? pointerType) && pointerType is not null)
			return pointerType;

		return TypeReferenceForNamedShape(typeName);
	}

	static bool TryCreatePrimitivePointerTypeReference(string typeName, out TypeReference? pointerType)
	{
		pointerType = null;
		string trimmed = typeName.Trim();
		if (!trimmed.EndsWith("*", StringComparison.Ordinal))
			return false;
		string elementName = trimmed[..^1].TrimEnd();
		bool isConst = false;
		if (elementName.StartsWith("const ", StringComparison.Ordinal))
		{
			isConst = true;
			elementName = elementName["const ".Length..].TrimStart();
		}
		foreach (PrimitiveType primitive in Enum.GetValues<PrimitiveType>())
		{
			if (GetPrimitiveTypeName(primitive) != elementName)
				continue;
			TypeReference element = new PrimitiveTypeReference
			{
				Type = primitive,
				ResolvedType = elementName
			};
			if (isConst)
				element = new ConstTypeReference { Type = element, ResolvedType = "const " + elementName };
			pointerType = new PointerTypeReference
			{
				ElementType = element,
				ResolvedType = typeName
			};
			return true;
		}
		return false;
	}

	static TypeReference TypeReferenceForTypeShape(TypeShape shape)
	{
		TypeReference result = shape.Kind switch
		{
			TypeShapeKind.Pointer => new PointerTypeReference
			{
				ElementType = shape.Element is null ? new PrimitiveTypeReference { Type = PrimitiveType.Void, ResolvedType = "void" } : TypeReferenceForTypeShape(shape.Element)
			},
			TypeShapeKind.Array => new ArrayTypeReference
			{
				ElementType = shape.Element is null ? new PrimitiveTypeReference { Type = PrimitiveType.Void, ResolvedType = "void" } : TypeReferenceForTypeShape(shape.Element)
			},
			TypeShapeKind.FixedArray => new FixedArrayTypeReference
			{
				ElementType = shape.Element is null ? new PrimitiveTypeReference { Type = PrimitiveType.Void, ResolvedType = "void" } : TypeReferenceForTypeShape(shape.Element),
				Length = shape.Length,
				LengthExpression = new LiteralExpression
				{
					Kind = LiteralKind.Number,
					Text = (shape.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
					Value = shape.Length ?? 0,
					ResolvedType = "nuint"
				}
			},
			TypeShapeKind.Optional => new OptionalTypeReference
			{
				ElementType = shape.Element is null ? new PrimitiveTypeReference { Type = PrimitiveType.Void, ResolvedType = "void" } : TypeReferenceForTypeShape(shape.Element)
			},
			_ => TypeReferenceForNamedShape(shape.Name)
		};

		result.ResolvedType = TypeShapeParser.Format(shape);
		if (shape.Qualifiers.IsConst)
			result = new ConstTypeReference { Type = result, ResolvedType = TypeShapeParser.Format(shape) };
		if (shape.Qualifiers.IsVolatile)
			result = new VolatileTypeReference { Type = result, ResolvedType = TypeShapeParser.Format(shape) };
		return result;
	}

	static TypeReference TypeReferenceForNamedShape(string typeName)
	{
		foreach (PrimitiveType primitive in Enum.GetValues<PrimitiveType>())
		{
			if (GetPrimitiveTypeName(primitive) == typeName)
			{
				return new PrimitiveTypeReference
				{
					Type = primitive,
					ResolvedType = typeName
				};
			}
		}

		return new NamedTypeReference
		{
			Name = typeName,
			ResolvedType = typeName
		};
	}

	static TypeReference NuintType()
	{
		return new PrimitiveTypeReference
		{
			Type = PrimitiveType.NUInt,
			ResolvedType = "nuint"
		};
	}
}
