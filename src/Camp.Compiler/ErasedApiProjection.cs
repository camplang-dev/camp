using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Camp.Compiler;

// Projection is a view operation. Never replace the producer's annotated types:
// its bodies still need their payload identity, members, and storage layout.
internal sealed class ErasedApiProjection
{
	readonly Dictionary<string, Definition> types = new(StringComparer.Ordinal);
	readonly Func<Definition, bool> visible;

	public ErasedApiProjection(IEnumerable<Definition> definitions, Func<Definition, bool> visible)
	{
		this.visible = visible;
		foreach (Definition type in definitions.Where(type => type is TypeDefinition or AliasDefinition))
		{
			types.TryAdd(type.Name, type);
			if (type.Symbol is not null) types[type.Symbol] = type;
			if (!string.IsNullOrEmpty(type.Namespace)) types[type.Namespace + "::" + type.Name] = type;
		}
	}

	// A hidden void<V> is opaque storage (`void`); a hidden C<V> is its plain carrier `C`.
	public string Type(string text)
	{
		int start = 0;
		while (start < text.Length)
		{
			int open = text.IndexOf('<', start);
			if (open < 0) break;
			int nameStart = open;
			while (nameStart > 0 && (char.IsLetterOrDigit(text[nameStart - 1]) || text[nameStart - 1] == '_'))
				nameStart--;
			string name = text[nameStart..open];
			bool annotation = name == "void" || CarrierTypeIdentity.IsCarrierName(name);
			if (!annotation || nameStart > 1 && text[nameStart - 1] == ':' && text[nameStart - 2] == ':')
			{
				start = open + 1;
				continue;
			}
			int end = open + 1, depth = 1;
			for (; end < text.Length && depth > 0; end++)
			{
				if (text[end] == '<') depth++;
				else if (text[end] == '>') depth--;
			}
			if (depth != 0) break;
			string payload = text[(open + 1)..(end - 1)];
			bool hidden = Regex.Matches(payload, @"[A-Za-z_][A-Za-z_0-9]*(?:::[A-Za-z_][A-Za-z_0-9]*)*")
				.Any(match => types.TryGetValue(match.Value, out Definition? type) && !visible(type));
			if (hidden)
			{
				text = text[..nameStart] + name + text[end..];
				start = nameStart + name.Length;
			}
			else start = open + 1;
		}
		return text;
	}

	public bool Hidden(TypeReference? type) => type is not null && Hidden(BindableNodeCodeSerializer.SerializeType(type));
	public bool Hidden(string type) => Type(type) != type;

	public bool HiddenResult(FunctionDefinition function)
	{
		TypeReference? source = function.ExpandedSourceReturnType ?? function.ReturnType;
		string text = BindableNodeCodeSerializer.SerializeType(source);
		return Hidden(text) && ErasedTypeIdentity.TryGetPayload(text, out _);
	}

	public string? ProjectedResultType(FunctionDefinition function)
	{
		string source = BindableNodeCodeSerializer.SerializeType(function.ExpandedSourceReturnType ?? function.ReturnType);
		if (HiddenResult(function)) return "void";
		string projected = Type(source);
		return projected != source && projected is "void[]" or "const void[]" ? projected[..^2] + "*" : null;
	}

	public List<ParameterDefinition> FunctionParameters(FunctionDefinition function)
	{
		List<ParameterDefinition> parameters = [.. function.Parameters];
		if (ProjectedResultType(function) is "void*" or "const void*")
		{
			// The opaque result must expose the length slot instead of restoring
			// an array spelling whose element annotation the consumer cannot name.
			if (function.ExpandedSourceReturnType is null)
				parameters.Add(Slot(new ParameterDefinition(), "result_length", "nuint", ParameterModifier.Out));
			else
				parameters = parameters.Select(parameter => parameter.IsExpandedReturnComponent
					? Slot(parameter, parameter.Name, parameter.ResolvedType!, parameter.Modifier) : parameter).ToList();
		}
		if (function.ExpandedSourceReturnType is not null && !HiddenResult(function))
			parameters.RemoveAll(parameter => parameter.IsErasedResultStorage);
		if (HiddenResult(function) && function.ExpandedSourceReturnType is null)
		{
			int hidden = parameters.FindIndex(parameter => parameter.Modifier is ParameterModifier.Within or ParameterModifier.Thrown
				|| parameter is WithinParameterDefinition or SizeOfParameterDefinition or NameOfParameterDefinition or VTableOfParameterDefinition);
			parameters.Insert(hidden < 0 ? parameters.Count : hidden, Slot(new ParameterDefinition(), "__result", "void*"));
		}
		return parameters;
	}

	public IEnumerable<ParameterDefinition> Parameters(IEnumerable<ParameterDefinition> parameters)
	{
		foreach (ParameterDefinition parameter in parameters)
		{
			string original = parameter.Type is null ? parameter.ResolvedType ?? "void" : BindableNodeCodeSerializer.SerializeType(parameter.Type);
			string type = Type(original);
			if (type == original) { yield return parameter; continue; }
			if (type is "void" or "const void" && parameter.Modifier is ParameterModifier.In or ParameterModifier.Out)
				yield return Slot(parameter, parameter.Name, parameter.Modifier == ParameterModifier.In ? "const void*" : "void*");
			else if (type is "void[]" or "const void[]")
			{
				yield return Slot(parameter, parameter.Name, type[..^2] + "*", parameter.Modifier);
				yield return Slot(parameter, parameter.Name + "_length", "nuint", parameter.Modifier);
			}
			else if (type == "void?" && parameter.Modifier is ParameterModifier.In or ParameterModifier.Out)
			{
				yield return Slot(parameter, parameter.Name, parameter.Modifier == ParameterModifier.In ? "const void*" : "void*");
				yield return Slot(parameter, parameter.Name + "_specified", "bool", parameter.Modifier);
			}
			else yield return parameter; // Wrapper and callable types project recursively in the serializer.
		}
	}

	public static ParameterDefinition Slot(ParameterDefinition source, string name, string type, ParameterModifier modifier = ParameterModifier.None)
	{
		ParameterDefinition result = new() { Name = name, Symbol = source.Symbol == source.Name ? name : source.Symbol,
			Type = BindableNodeAnalyzer.TypeReferenceForResolvedName(type), ResolvedType = type,
			Modifier = modifier, SourceSyntax = source.SourceSyntax, DefaultValue = source.DefaultValue,
			IsOverloadSelector = source.IsOverloadSelector };
		result.Attributes.AddRange(source.Attributes);
		return result;
	}

	public IEnumerable<FieldDefinition> Fields(FieldDefinition field)
	{
		ParameterDefinition source = new() { Name = field.Name, Type = field.Type, ResolvedType = field.ResolvedType };
		foreach (ParameterDefinition slot in Parameters([source]))
		{
			if (ReferenceEquals(slot, source)) { yield return field; continue; }
			FieldDefinition projected = new() { Name = slot.Name, Symbol = field.Symbol + slot.Name[field.Name.Length..],
				Type = slot.Type, ResolvedType = slot.ResolvedType, Modifier = field.Modifier,
				SourceSyntax = field.SourceSyntax, Export = field.Export, Public = field.Public, Internal = field.Internal };
			projected.Attributes.AddRange(field.Attributes);
			yield return projected;
		}
	}
}
