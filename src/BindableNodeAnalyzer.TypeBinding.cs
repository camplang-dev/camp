using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	string? AnalyzeOptionalType(TypeReference? type, AnalysisScope scope)
	{
		if (type is null)
			return null;

		AnalyzeType(type, scope);
		return type.ResolvedType;
	}

	void AnalyzeTypeList(List<TypeReference> types, AnalysisScope scope)
	{
		foreach (TypeReference type in types)
			AnalyzeType(type, scope);
	}

	void AnalyzeType(TypeReference type, AnalysisScope scope)
	{
		switch (type)
		{
			case TypeDefinitionReference definition:
				AnalyzeTypeList(definition.TypeArguments, scope);
				type.ResolvedType = AddTypeArguments(definition.Name, definition.TypeArguments);
				break;

			case GenericParameterTypeReference genericParameter:
				type.ResolvedType = genericParameter.Name;
				break;

			case AllocatorTypeReference:
				type.ResolvedType = AllocatorType;
				break;

			case NamedTypeReference named:
				foreach (TypeReference argument in named.TypeArguments)
					AnalyzeType(argument, scope);

				type.ResolvedType = ResolveNamedType(named, scope);
				ValidateGenericArgumentUse(named);
				break;

			case AttributedTypeReference attributed:
				AnalyzeOptionalType(attributed.Type, scope);
				if (attributed.Attribute is not null)
					AnalyzeAttribute(attributed.Attribute);
				type.ResolvedType = attributed.Type?.ResolvedType ?? ErrorType;
				break;

			case GenericTypeReference generic:
				AnalyzeOptionalType(generic.Type, scope);
				AnalyzeTypeList(generic.TypeArguments, scope);
				type.ResolvedType = $"{generic.Type?.ResolvedType ?? ErrorType}<{string.Join(", ", GetResolvedTypes(generic.TypeArguments))}>";
				break;

			case ArrayTypeReference array:
				AnalyzeOptionalType(array.ElementType, scope);
				type.ResolvedType = $"{array.ElementType?.ResolvedType ?? ErrorType}[]";
				break;

			case OptionalTypeReference optional:
				AnalyzeOptionalType(optional.ElementType, scope);
				type.ResolvedType = $"{optional.ElementType?.ResolvedType ?? ErrorType}?";
				break;

			case PointerTypeReference pointer:
				AnalyzeOptionalType(pointer.ElementType, scope);
				type.ResolvedType = $"{pointer.ElementType?.ResolvedType ?? ErrorType}*";
				break;

			case ConstTypeReference constType:
				AnalyzeOptionalType(constType.Type, scope);
				type.ResolvedType = $"const {constType.Type?.ResolvedType ?? ErrorType}";
				break;

			case VolatileTypeReference volatileType:
				AnalyzeOptionalType(volatileType.Type, scope);
				type.ResolvedType = $"volatile {volatileType.Type?.ResolvedType ?? ErrorType}";
				break;

			case AnyTypeReference:
				type.ResolvedType = "any";
				break;

			case AutoTypeReference:
				type.ResolvedType = AutoType;
				break;

			case PrimitiveTypeReference primitive:
				type.ResolvedType = GetPrimitiveTypeName(primitive.Type);
				break;

			case EscapedTypeReference escaped:
				AnalyzeOptionalType(escaped.Type, scope);
				type.ResolvedType = $"escaped {escaped.Type?.ResolvedType ?? ErrorType}";
				break;

			case ScopedTypeReference scoped:
				AnalyzeOptionalType(scoped.Type, scope);
				type.ResolvedType = $"{BuildAnchoredDeclarator("scoped", scoped.Anchors)} {scoped.Type?.ResolvedType ?? ErrorType}";
				break;

			case UnscopedTypeReference unscoped:
				AnalyzeOptionalType(unscoped.Type, scope);
				type.ResolvedType = $"{BuildAnchoredDeclarator("unscoped", unscoped.Anchors)} {unscoped.Type?.ResolvedType ?? ErrorType}";
				break;

			case CallableTypeReference callable:
				AnalyzeOptionalType(callable.ReturnType, scope);
				foreach (ParameterDefinition parameter in callable.Parameters)
					AnalyzeParameterDefinition(parameter, scope);
				type.ResolvedType = $"{GetCallableKindName(callable.Kind)} {callable.ReturnType?.ResolvedType ?? ErrorType}({string.Join(", ", GetParameterTypeNames(callable.Parameters))})";
				break;

			case IterTypeReference iter:
				AnalyzeOptionalType(iter.ElementType, scope);
				type.ResolvedType = $"iter {iter.ElementType?.ResolvedType ?? ErrorType}";
				break;

			case GroupedParamsTypeReference grouped:
				AnalyzeOptionalType(grouped.StructType, scope);
				type.ResolvedType = $"params({grouped.StructType?.ResolvedType ?? ErrorType})";
				break;

			case MaterializedStructTypeReference materialized:
				AnalyzeOptionalType(materialized.ParamsType, scope);
				type.ResolvedType = $"struct({materialized.ParamsType?.ResolvedType ?? ErrorType})";
				break;

			case ThrownTypeReference thrown:
				AnalyzeOptionalType(thrown.Type, scope);
				type.ResolvedType = $"thrown({thrown.Type?.ResolvedType ?? ErrorType})";
				break;

			default:
				type.ResolvedType = ErrorType;
				break;
		}
	}

	string ResolveNamedType(NamedTypeReference named, AnalysisScope scope)
	{
		string sourceName = BuildNamedTypeSourceName(named);

		if (named.Qualifiers.Count == 0 && scope.TryGetGenericParameter(named.Name, out GenericParameter? genericParameter))
		{
			string resolvedType = AddTypeArguments(named.Name, named.TypeArguments);
			typeRewrites[named] = new GenericParameterTypeReference
			{
				SourceSyntax = named.SourceSyntax,
				Name = named.Name,
				Parameter = genericParameter,
				ResolvedType = resolvedType
			};
			return resolvedType;
		}

		if (named.Qualifiers.Count == 0 && typeDefinitions.TryGetValue(named.Name, out TypeDefinition? definition))
		{
			if (!IsDefinitionVisible(definition, named.SourceSyntax))
			{
				ReportNotExported(definition, named.SourceSyntax, "Type");
				return $"{UnresolvedType}({sourceName})";
			}

			ValidateGenericArity(named, definition);
			string resolvedType = AddTypeArguments(named.Name, named.TypeArguments);
			TypeDefinitionReference reference = new()
			{
				SourceSyntax = named.SourceSyntax,
				Name = named.Name,
				Definition = definition,
				ResolvedType = resolvedType
			};
			foreach (TypeReference argument in named.TypeArguments)
				reference.TypeArguments.Add(argument);
			typeRewrites[named] = reference;
			return resolvedType;
		}

		if (named.Name == "<missing>")
			return MissingTypeName;

		if (named.Qualifiers.Count == 0 && named.Name == "Allocator")
			return "Allocator";

		Report(GetRange(named.SourceSyntax), $"Unknown type '{sourceName}'.");
		return $"{UnresolvedType}({sourceName})";
	}
}
