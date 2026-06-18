using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Camp.Compiler;

public enum MetadataVisibility
{
	Export,
	Public,
	Private,
	All
}

public static class MetadataJsonSerializer
{
	public static string Serialize(Compilation compilation, MetadataVisibility visibility)
	{
		ArgumentNullException.ThrowIfNull(compilation);
		Module module = compilation.SharedModule ?? new Module();
		Writer writer = new(compilation, module, visibility);
		return writer.Serialize();
	}

	sealed class Writer
	{
		readonly Compilation compilation;
		readonly Module module;
		readonly MetadataVisibility visibility;
		readonly Dictionary<BindableNode, string> ids = new(ReferenceEqualityComparer.Instance);
		readonly HashSet<BindableNode> emitted = new(ReferenceEqualityComparer.Instance);
		readonly HashSet<BindableNode> stubbed = new(ReferenceEqualityComparer.Instance);

		public Writer(Compilation compilation, Module module, MetadataVisibility visibility)
		{
			this.compilation = compilation;
			this.module = module;
			this.visibility = visibility;
			IndexModule();
		}

		public string Serialize()
		{
			using MemoryStream stream = new();
			using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
			{
				json.WriteStartObject();
				json.WriteString("format", "camp.metadata");
				json.WriteNumber("version", 1);
				WriteModule(json);
				WriteView(json);
				WriteDeclarations(json);
				WriteStubs(json);
				json.WriteEndObject();
			}
			return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
		}

		void IndexModule()
		{
			foreach (Definition definition in module.Definitions)
				IndexDefinition(definition, GetTopLevelId(definition));
		}

		void IndexDefinition(Definition definition, string id)
		{
			ids[definition] = id;
			if (definition is TypeDefinition typeDefinition)
			{
				foreach (GenericParameter parameter in typeDefinition.GenericParameters)
					ids[parameter] = id + "/type-parameter:" + parameter.Name;
			}
			switch (definition)
			{
				case ClassDefinition classDefinition:
					IndexChildren(id, "field", classDefinition.Fields);
					IndexChildren(id, "function", classDefinition.Functions);
					break;
				case StructDefinition structDefinition:
					IndexChildren(id, "field", structDefinition.Fields);
					IndexChildren(id, "function", structDefinition.Functions);
					break;
				case InterfaceDefinition interfaceDefinition:
					IndexChildren(id, "function", interfaceDefinition.Functions);
					break;
				case EnumDefinition enumDefinition:
					IndexChildren(id, "value", enumDefinition.Values);
					IndexChildren(id, "function", enumDefinition.Functions);
					break;
				case NewtypeDefinition newtypeDefinition:
					IndexChildren(id, "field", newtypeDefinition.Fields);
					IndexChildren(id, "function", newtypeDefinition.Functions);
					IndexChildren(id, "parameter", newtypeDefinition.Parameters);
					break;
				case ParamsDefinition paramsDefinition:
					IndexChildren(id, "component", paramsDefinition.Components);
					IndexChildren(id, "function", paramsDefinition.Functions);
					break;
				case FunctionDefinition functionDefinition:
					IndexFunctionChildren(id, functionDefinition);
					break;
			}
		}

		void IndexChildren<T>(string parentId, string kind, IEnumerable<T> children)
			where T : Definition
		{
			foreach (T child in children)
				IndexDefinition(child, parentId + "/" + kind + ":" + GetMetadataName(child));
		}

		void IndexFunctionChildren(string id, FunctionDefinition function)
		{
			foreach (GenericParameter parameter in function.GenericParameters)
				ids[parameter] = id + "/type-parameter:" + parameter.Name;
			foreach (ParameterDefinition parameter in function.Parameters)
				ids[parameter] = id + "/parameter:" + GetMetadataName(parameter);
		}

		void WriteModule(Utf8JsonWriter json)
		{
			json.WriteStartObject("module");
			json.WriteString("name", module.ExportAs ?? GetProjectName());
			if (!string.IsNullOrWhiteSpace(module.ExportAs))
				json.WriteString("namespace", module.ExportAs);
			json.WriteEndObject();
		}

		void WriteView(Utf8JsonWriter json)
		{
			json.WriteStartObject("view");
			json.WriteString("visibility", visibility.ToString().ToLowerInvariant());
			json.WriteString("level", "source");
			json.WriteBoolean("generated", false);
			json.WriteEndObject();
		}

		void WriteDeclarations(Utf8JsonWriter json)
		{
			json.WriteStartArray("declarations");
			foreach (Definition definition in module.Definitions)
			{
				if (!ShouldEmit(definition))
					continue;
				WriteDefinition(json, definition);
				emitted.Add(definition);
			}
			json.WriteEndArray();
		}

		void WriteStubs(Utf8JsonWriter json)
		{
			List<BindableNode> stubs = stubbed
				.Where(node => !emitted.Contains(node))
				.OrderBy(GetId, StringComparer.Ordinal)
				.ToList();
			if (stubs.Count == 0)
				return;

			json.WriteStartArray("stubs");
			foreach (BindableNode node in stubs)
			{
				json.WriteStartObject();
				json.WriteString("id", GetId(node));
				json.WriteString("kind", GetKind(node));
				if (node is Definition definition)
				{
					json.WriteString("name", definition.Name);
					if (!string.IsNullOrWhiteSpace(definition.Symbol))
						json.WriteString("symbol", definition.Symbol);
				}
				else if (node is GenericParameter generic)
					json.WriteString("name", generic.Name);
				json.WriteEndObject();
			}
			json.WriteEndArray();
		}

		void WriteDefinition(Utf8JsonWriter json, Definition definition)
		{
			json.WriteStartObject();
			WriteIdentity(json, definition);
			switch (definition)
			{
				case AliasDefinition alias:
					json.WriteString("target", FormatAliasTarget(alias));
					if (!string.IsNullOrWhiteSpace(alias.ResolvedTargetName))
						json.WriteString("resolvedTarget", alias.ResolvedTargetName);
					break;
				case TypeDefinition type:
					WriteTypeDefinition(json, type);
					break;
				case VariableDefinition variable:
					WriteTypeProperty(json, "type", variable.Type, variable.ResolvedType);
					if (variable.IsFixedStorage)
						json.WriteBoolean("fixed", true);
					break;
				case FunctionDefinition function:
					WriteFunction(json, function);
					break;
				case ParameterDefinition parameter:
					WriteParameter(json, parameter);
					break;
				case FieldDefinition field:
					WriteTypeProperty(json, "type", field.Type, field.ResolvedType);
					if (field.Modifier == FieldModifier.Static)
						json.WriteBoolean("static", true);
					if (field.IsFixedStorage)
						json.WriteBoolean("fixed", true);
					break;
			}
			WriteMetadata(json, definition.Attributes);
			json.WriteEndObject();
		}

		void WriteIdentity(Utf8JsonWriter json, Definition definition)
		{
			json.WriteString("id", GetId(definition));
			json.WriteString("kind", GetKind(definition));
			json.WriteString("name", definition.Name);
			if (!string.IsNullOrWhiteSpace(definition.Symbol))
				json.WriteString("symbol", definition.Symbol);
			json.WriteString("visibility", GetVisibility(definition));
			if (!string.IsNullOrWhiteSpace(definition.Extern))
				json.WriteBoolean("extern", true);
		}

		void WriteTypeDefinition(Utf8JsonWriter json, TypeDefinition type)
		{
			if (type.GenericParameters.Count > 0)
			{
				json.WriteStartArray("typeParameters");
				foreach (GenericParameter parameter in type.GenericParameters)
					WriteGenericParameter(json, parameter);
				json.WriteEndArray();
			}

			switch (type)
			{
				case ClassDefinition classDefinition:
					WriteTypes(json, "baseTypes", classDefinition.BaseTypes);
					WriteDefinitionArray(json, "fields", classDefinition.Fields);
					WriteDefinitionArray(json, "functions", classDefinition.Functions);
					break;
				case StructDefinition structDefinition:
					WriteTypes(json, "baseTypes", structDefinition.BaseTypes);
					WriteDefinitionArray(json, "fields", structDefinition.Fields);
					WriteDefinitionArray(json, "functions", structDefinition.Functions);
					break;
				case InterfaceDefinition interfaceDefinition:
					WriteTypes(json, "baseTypes", interfaceDefinition.BaseTypes);
					WriteDefinitionArray(json, "functions", interfaceDefinition.Functions);
					break;
				case EnumDefinition enumDefinition:
					WriteTypeProperty(json, "underlyingType", enumDefinition.UnderlyingType, enumDefinition.UnderlyingType?.ResolvedType);
					WriteDefinitionArray(json, "values", enumDefinition.Values);
					WriteDefinitionArray(json, "functions", enumDefinition.Functions);
					break;
				case NewtypeDefinition newtypeDefinition:
					WriteTypeProperty(json, "underlyingType", newtypeDefinition.UnderlyingType, newtypeDefinition.ResolvedType);
					WriteDefinitionArray(json, "parameters", newtypeDefinition.Parameters);
					WriteDefinitionArray(json, "fields", newtypeDefinition.Fields);
					WriteDefinitionArray(json, "functions", newtypeDefinition.Functions);
					break;
			}
		}

		void WriteFunction(Utf8JsonWriter json, FunctionDefinition function)
		{
			if (function.IteratorKind != IteratorKind.None)
				json.WriteString("iterator", function.IteratorKind.ToString().ToLowerInvariant());
			if (function.IsAsync)
				json.WriteBoolean("async", true);
			if (function.Modifier != FunctionModifier.None)
				json.WriteString("modifier", function.Modifier.ToString().ToLowerInvariant());
			if (!string.IsNullOrWhiteSpace(function.CallSpec))
				json.WriteString("callspec", function.CallSpec);
			WriteTypeProperty(json, "returnType", function.ReturnType, function.ResolvedType);
			if (function.CallableAscriptionType is not null)
				WriteTypeProperty(json, "ascription", function.CallableAscriptionType, function.CallableAscriptionType.ResolvedType);
			if (function.GenericParameters.Count > 0)
			{
				json.WriteStartArray("typeParameters");
				foreach (GenericParameter parameter in function.GenericParameters)
					WriteGenericParameter(json, parameter);
				json.WriteEndArray();
			}
			WriteDefinitionArray(json, "parameters", function.Parameters);
		}

		void WriteParameter(Utf8JsonWriter json, ParameterDefinition parameter)
		{
			if (parameter.Modifier != ParameterModifier.None)
				json.WriteString("modifier", parameter.Modifier.ToString().ToLowerInvariant());
			if (parameter.IsOverloadSelector)
				json.WriteBoolean("overload", true);
			WriteTypeProperty(json, "type", parameter.Type, parameter.ResolvedType);
			if (parameter is VTableOfParameterDefinition vtable)
				WriteTypeProperty(json, "interfaceType", vtable.InterfaceType, vtable.InterfaceType?.ResolvedType);
		}

		void WriteGenericParameter(Utf8JsonWriter json, GenericParameter parameter)
		{
			json.WriteStartObject();
			json.WriteString("id", GetId(parameter));
			json.WriteString("kind", "type-parameter");
			json.WriteString("name", parameter.Name);
			WriteTypeProperty(json, "constraint", parameter.Constraint, parameter.Constraint?.ResolvedType ?? "nint");
			WriteMetadata(json, parameter.Attributes);
			json.WriteEndObject();
		}

		void WriteDefinitionArray<T>(Utf8JsonWriter json, string propertyName, IReadOnlyList<T> definitions)
			where T : Definition
		{
			if (definitions.Count == 0)
				return;
			json.WriteStartArray(propertyName);
			foreach (T definition in definitions)
			{
				WriteDefinition(json, definition);
				emitted.Add(definition);
			}
			json.WriteEndArray();
		}

		void WriteTypes(Utf8JsonWriter json, string propertyName, IReadOnlyList<TypeReference> types)
		{
			if (types.Count == 0)
				return;
			json.WriteStartArray(propertyName);
			foreach (TypeReference type in types)
				json.WriteStringValue(FormatType(type, type.ResolvedType));
			json.WriteEndArray();
		}

		static void WriteTypeProperty(Utf8JsonWriter json, string propertyName, TypeReference? type, string? resolvedType)
		{
			if (type is null && string.IsNullOrWhiteSpace(resolvedType))
				return;
			json.WriteString(propertyName, FormatType(type, resolvedType));
		}

		static string FormatType(TypeReference? type, string? resolvedType)
		{
			return string.IsNullOrWhiteSpace(resolvedType)
				? BindableNodeAnalyzer.FormatTypeReference(type)
				: resolvedType!;
		}

		void WriteMetadata(Utf8JsonWriter json, IReadOnlyList<AttributeConstructor> attributes)
		{
			if (attributes.Count == 0)
				return;
			json.WriteStartArray("metadata");
			foreach (AttributeConstructor attribute in attributes)
				WriteAttribute(json, attribute);
			json.WriteEndArray();
		}

		void WriteAttribute(Utf8JsonWriter json, AttributeConstructor attribute)
		{
			json.WriteStartObject();
			json.WriteString("name", attribute.Name.TrimStart('@'));
			if (attribute.IsDocCommentAttribute)
				json.WriteBoolean("doc", true);

			List<ArgumentExpression> positional = attribute.Arguments.Where(static argument => string.IsNullOrWhiteSpace(argument.Name)).ToList();
			List<ArgumentExpression> named = attribute.Arguments.Where(static argument => !string.IsNullOrWhiteSpace(argument.Name)).ToList();
			if (positional.Count == 1 && positional[0].Value is LiteralExpression literal)
				json.WriteString("content", literal.Value?.ToString() ?? literal.Text);
			else if (positional.Count > 0)
			{
				json.WriteStartArray("arguments");
				foreach (ArgumentExpression argument in positional)
					WriteAttributeValue(json, argument.Value);
				json.WriteEndArray();
			}

			foreach (ArgumentExpression argument in named)
			{
				json.WritePropertyName(argument.Name!);
				WriteAttributeValue(json, argument.Value);
			}
			json.WriteEndObject();
		}

		void WriteAttributeValue(Utf8JsonWriter json, Expression? expression)
		{
			switch (expression)
			{
				case null:
					json.WriteNullValue();
					break;
				case LiteralExpression literal:
					WriteLiteral(json, literal);
					break;
				case SymbolOfExpression symbolOf:
					WriteSymbolReference(json, symbolOf);
					break;
				case ArrayExpression array:
					json.WriteStartArray();
					foreach (Expression element in array.Elements)
						WriteAttributeValue(json, element);
					json.WriteEndArray();
					break;
				default:
					json.WriteStringValue(expression.ResolvedType ?? expression.GetType().Name);
					break;
			}
		}

		static void WriteLiteral(Utf8JsonWriter json, LiteralExpression literal)
		{
			switch (literal.Value)
			{
				case bool boolean:
					json.WriteBooleanValue(boolean);
					break;
				case int integer:
					json.WriteNumberValue(integer);
					break;
				case uint unsigned:
					json.WriteNumberValue(unsigned);
					break;
				case long longInteger:
					json.WriteNumberValue(longInteger);
					break;
				case ulong unsignedLong:
					json.WriteNumberValue(unsignedLong);
					break;
				case double floating:
					json.WriteNumberValue(floating);
					break;
				default:
					json.WriteStringValue(literal.Value?.ToString() ?? literal.Text);
					break;
			}
		}

		void WriteSymbolReference(Utf8JsonWriter json, SymbolOfExpression symbolOf)
		{
			json.WriteStartObject();
			if (symbolOf.Reference is not null && ids.TryGetValue(symbolOf.Reference, out string? id))
			{
				json.WriteString("ref", id);
				if (!emitted.Contains(symbolOf.Reference))
					stubbed.Add(symbolOf.Reference);
			}
			json.WriteString("text", symbolOf.Text);
			json.WriteEndObject();
		}

		bool ShouldEmit(Definition definition)
		{
			return visibility switch
			{
				MetadataVisibility.Export => definition.Export is not null,
				MetadataVisibility.Public => definition.Export is not null || definition.Public is not null,
				MetadataVisibility.Private => definition.Export is null && definition.Public is null,
				MetadataVisibility.All => true,
				_ => false
			};
		}

		string GetTopLevelId(Definition definition)
		{
			string prefix = string.IsNullOrWhiteSpace(module.ExportAs) ? "" : module.ExportAs + "::";
			return GetKind(definition) + ":" + prefix + GetMetadataName(definition);
		}

		string GetId(BindableNode node)
		{
			if (ids.TryGetValue(node, out string? id))
				return id;
			return GetKind(node) + ":" + node.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		static string GetKind(BindableNode node)
		{
			return node switch
			{
				AliasDefinition => "alias",
				ClassDefinition => "class",
				StructDefinition => "struct",
				InterfaceDefinition => "interface",
				EnumDefinition => "enum",
				NewtypeDefinition => "newtype",
				ParamsDefinition => "params",
				VariableDefinition => "variable",
				FunctionDefinition => "function",
				FieldDefinition => "field",
				ThisParameterDefinition => "receiver",
				WithinParameterDefinition => "within-parameter",
				SizeOfParameterDefinition => "sizeof-parameter",
				VTableOfParameterDefinition => "vtableof-parameter",
				ParameterDefinition => "parameter",
				GenericParameter => "type-parameter",
				_ => "node"
			};
		}

		static string GetVisibility(Definition definition)
		{
			if (definition.Export is not null)
				return "export";
			if (definition.Public is not null)
				return "public";
			return "private";
		}

		static string GetMetadataName(Definition definition)
		{
			return string.IsNullOrWhiteSpace(definition.Name) ? definition.Symbol : definition.Name;
		}

		static string FormatAliasTarget(AliasDefinition alias)
		{
			string prefix = alias.TargetQualifiers.Count == 0 ? "" : string.Join("::", alias.TargetQualifiers) + "::";
			return prefix + alias.TargetName;
		}

		string GetProjectName()
		{
			SourceFile? first = compilation.Files.FirstOrDefault(static file => !file.IsApiHeader);
			if (first is null || string.IsNullOrWhiteSpace(first.Path))
				return "module";
			return Path.GetFileNameWithoutExtension(first.Path);
		}
	}
}
