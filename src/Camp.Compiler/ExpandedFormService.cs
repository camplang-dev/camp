using System.Collections.Generic;

namespace Camp.Compiler;

internal enum ParamsComponentShapeKind
{
	Nominal,
	Structural,
	Array,
	Optional,
	Delegate,
	Iter
}

internal sealed record ParamsComponentShape(ParamsComponentShapeKind Kind, string TypeName, List<ParamsComponent> Components);

internal sealed record ParamsComponent(
	string Name,
	string Type,
	string ExpandedName,
	ParameterDefinition? SourceParameter,
	ParamsComponentShapeKind SourceKind);

internal readonly record struct ParamsNamePart(string Name, bool PreferNoSuffix);

internal sealed record PendingParamsComponent(
	string Name,
	string Type,
	List<ParamsNamePart> NameParts,
	ParameterDefinition? SourceParameter,
	ParamsComponentShapeKind SourceKind);

internal sealed record ParamsExpansionComponent(string SourceName, string Name, string Type, BindableNode Node);

internal delegate bool TryGetExpandedFormShape(TypeReference? type, string? resolvedType, string baseName, out ParamsComponentShape shape);

internal static class ExpandedFormService
{
	public static List<string> GetExpandedComponentNames(ParamsComponentShape shape)
	{
		List<string> names = [];
		foreach (ParamsComponent component in shape.Components)
			names.Add(component.ExpandedName);
		return names;
	}

	public static ParamsComponent? FindComponent(ParamsComponentShape shape, string name)
	{
		foreach (ParamsComponent component in shape.Components)
			if (component.Name == name)
				return component;
		return null;
	}

	public static List<string> GetExpandedCallableParameterTypes(IEnumerable<ParameterDefinition> parameters, TryGetExpandedFormShape tryGetShape)
	{
		List<string> types = [];
		foreach (ParameterDefinition parameter in parameters)
		{
			if (tryGetShape(parameter.Type, parameter.ResolvedType, parameter.Name, out ParamsComponentShape shape))
			{
				foreach (ParamsComponent component in shape.Components)
					types.Add(component.Type);
			}
			else
			{
				types.Add(FormatParameterType(parameter));
			}
		}
		return types;
	}

	public static List<string> GetExpandedCallableParameterTypes(IEnumerable<string> parameterTypes, TryGetExpandedFormShape tryGetShape)
	{
		List<string> types = [];
		foreach (string parameterType in parameterTypes)
		{
			if (tryGetShape(null, parameterType, "arg", out ParamsComponentShape shape))
			{
				foreach (ParamsComponent component in shape.Components)
					types.Add(component.Type);
			}
			else
			{
				types.Add(parameterType);
			}
		}
		return types;
	}

	static string FormatParameterType(ParameterDefinition parameter)
	{
		string parameterType = parameter.ResolvedType ?? "#ERROR";
		return parameter.Modifier switch
		{
			ParameterModifier.In => "in " + parameterType,
			ParameterModifier.Out => "out " + parameterType,
			ParameterModifier.Thrown => "thrown " + parameterType,
			ParameterModifier.Within => "within " + parameterType,
			ParameterModifier.Upon => "upon " + parameterType,
			_ => parameterType
		};
	}
}
