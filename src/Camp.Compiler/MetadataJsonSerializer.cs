using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Camp.Compiler;

public enum MetadataVisibility
{
	None,
	Export,
	Public,
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
		readonly Dictionary<string, TypeDefinition> typeDefinitions = new(StringComparer.Ordinal);
		readonly Dictionary<string, Definition> symbols = new(StringComparer.Ordinal);

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
			string text = Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
			return text.EndsWith('\n') ? text : text + "\n";
		}

		void IndexModule()
		{
			foreach (Definition definition in module.Definitions)
			{
				if (definition is TypeDefinition typeDefinition)
					typeDefinitions[definition.Name] = typeDefinition;
				if (!string.IsNullOrWhiteSpace(definition.Name))
					symbols.TryAdd(definition.Name, definition);
				if (!string.IsNullOrWhiteSpace(definition.Symbol))
					symbols.TryAdd(definition.Symbol, definition);
				IndexDefinition(definition, GetTopLevelId(definition));
			}
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
				WriteDefinition(json, definition, includeKind: true, includeVisibility: true);
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
					if (!IsSameSymbol(definition))
						json.WriteString("symbol", definition.Symbol);
				}
				else if (node is GenericParameter generic)
					json.WriteString("name", generic.Name);
				json.WriteEndObject();
			}
			json.WriteEndArray();
		}

		void WriteDefinition(Utf8JsonWriter json, Definition definition, bool includeKind, bool includeVisibility)
		{
			json.WriteStartObject();
			WriteIdentity(json, definition, includeKind, includeVisibility);
			switch (definition)
			{
				case AliasDefinition alias:
					json.WriteString("target", FormatAliasTarget(alias));
					if (!string.IsNullOrWhiteSpace(alias.ResolvedTargetName))
						json.WriteString("resolvedTarget", alias.ResolvedTargetName);
					if (GetAliasTargetKind(alias) is string targetKind)
						json.WriteString("targetKind", targetKind);
					if (TryResolveAliasTargetDefinition(alias, out Definition? targetDefinition) && targetDefinition is not null)
						WriteReference(json, "targetRef", targetDefinition);
					break;
				case TypeDefinition type:
					WriteTypeDefinition(json, type);
					break;
				case VariableDefinition variable:
					WriteVariable(json, variable, enumValue: null);
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

		void WriteIdentity(Utf8JsonWriter json, Definition definition, bool includeKind, bool includeVisibility)
		{
			json.WriteString("id", GetId(definition));
			if (includeKind)
				json.WriteString("kind", GetKind(definition));
			json.WriteString("name", definition.Name);
			if (!IsSameSymbol(definition))
				json.WriteString("symbol", definition.Symbol);
			if (includeVisibility && GetVisibility(definition) is string visibility)
				json.WriteString("visibility", visibility);
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
					if (classDefinition.Modifier != ClassModifier.None)
						json.WriteString("modifier", classDefinition.Modifier.ToString().ToLowerInvariant());
					WriteTypes(json, "baseTypes", classDefinition.BaseTypes);
					WriteImplementedInterfaces(json, classDefinition.BaseTypes, classDefinition);
					if (ShouldEmitClassFields(classDefinition))
						WriteDefinitionArray(json, "fields", classDefinition.Fields, includeKind: false, includeVisibility: false);
					WriteDefinitionArray(json, "functions", classDefinition.Functions);
					break;
				case StructDefinition structDefinition:
					if (structDefinition.Modifier != StructModifier.None)
						json.WriteString("modifier", structDefinition.Modifier.ToString().ToLowerInvariant());
					WriteTypes(json, "baseTypes", structDefinition.BaseTypes);
					WriteImplementedInterfaces(json, structDefinition.BaseTypes, structDefinition);
					WriteDefinitionArray(json, "fields", structDefinition.Fields, includeKind: false, includeVisibility: false);
					WriteDefinitionArray(json, "functions", structDefinition.Functions);
					break;
				case InterfaceDefinition interfaceDefinition:
					WriteTypes(json, "baseTypes", interfaceDefinition.BaseTypes);
					WriteDefinitionArray(json, "functions", interfaceDefinition.Functions);
					break;
				case EnumDefinition enumDefinition:
					WriteTypeProperty(json, "underlyingType", enumDefinition.UnderlyingType, enumDefinition.UnderlyingType?.ResolvedType);
					WriteEnumValues(json, enumDefinition);
					WriteDefinitionArray(json, "functions", enumDefinition.Functions);
					break;
				case NewtypeDefinition newtypeDefinition:
					if (IsCallableNewtype(newtypeDefinition))
						WriteCallableNewtype(json, newtypeDefinition);
					else
						WriteTypeProperty(json, "underlyingType", newtypeDefinition.UnderlyingType, newtypeDefinition.ResolvedType);
					WriteDefinitionArray(json, "parameters", newtypeDefinition.Parameters, includeKind: false, includeVisibility: false);
					WriteDefinitionArray(json, "fields", newtypeDefinition.Fields, includeKind: false, includeVisibility: false);
					WriteDefinitionArray(json, "functions", newtypeDefinition.Functions);
					break;
			}
		}

		static bool IsCallableNewtype(NewtypeDefinition definition)
		{
			return definition.UnderlyingType is CallableTypeReference or IterTypeReference || definition.Parameters.Count > 0;
		}

		void WriteCallableNewtype(Utf8JsonWriter json, NewtypeDefinition definition)
		{
			switch (definition.UnderlyingType)
			{
				case CallableTypeReference callable:
					json.WriteString("callableType", GetCallableTypeName(callable.Kind));
					if (!string.IsNullOrWhiteSpace(callable.CallSpec))
						json.WriteString("callspec", callable.CallSpec);
					WriteTypeProperty(json, "returnType", callable.ReturnType, callable.ReturnType?.ResolvedType);
					break;
				case IterTypeReference iter:
					json.WriteString("callableType", iter.IsAsync ? "async iter" : "iter");
					WriteTypeProperty(json, "returnType", iter.ElementType, iter.ElementType?.ResolvedType);
					break;
				default:
					json.WriteString("callableType", "fn");
					WriteTypeProperty(json, "returnType", definition.UnderlyingType, definition.UnderlyingType?.ResolvedType);
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
			WritePropertyInfo(json, function);
			if (function.GenericParameters.Count > 0)
			{
				json.WriteStartArray("typeParameters");
				foreach (GenericParameter parameter in function.GenericParameters)
					WriteGenericParameter(json, parameter);
				json.WriteEndArray();
			}
			WriteDefinitionArray(json, "parameters", function.Parameters, includeKind: false, includeVisibility: false);
		}

		void WriteParameter(Utf8JsonWriter json, ParameterDefinition parameter)
		{
			if (parameter.Modifier != ParameterModifier.None)
				json.WriteString("modifier", parameter.Modifier.ToString().ToLowerInvariant());
			if (parameter.IsOverloadSelector)
				json.WriteBoolean("overload", true);
			WriteTypeProperty(json, "type", parameter.Type, parameter.ResolvedType);
			if (parameter.DefaultValue is not null)
				json.WriteString("defaultValue", SerializeExpression(parameter.DefaultValue));
			if (parameter is VTableOfParameterDefinition vtable)
				WriteTypeProperty(json, "interfaceType", vtable.InterfaceType, vtable.InterfaceType?.ResolvedType);
		}

		void WriteVariable(Utf8JsonWriter json, VariableDefinition variable, BigInteger? enumValue)
		{
			WriteTypeProperty(json, "type", variable.Type, variable.ResolvedType);
			if (enumValue is not null)
				WriteInteger(json, "value", enumValue.Value);
			if (variable.IsFixedStorage)
				json.WriteBoolean("fixed", true);
		}

		void WriteEnumValues(Utf8JsonWriter json, EnumDefinition enumDefinition)
		{
			List<VariableDefinition> sourceDefinitions = enumDefinition.Values.Where(static definition => !IsGeneratedDefinition(definition)).ToList();
			if (sourceDefinitions.Count == 0)
				return;

			Dictionary<string, BigInteger> valuesByName = new(StringComparer.Ordinal);
			BigInteger nextValue = BigInteger.Zero;
			json.WriteStartArray("values");
			foreach (VariableDefinition value in sourceDefinitions)
			{
				BigInteger numericValue = nextValue;
				if (TryEvaluateIntegerExpression(value.InitialValue, valuesByName, out BigInteger explicitValue))
					numericValue = explicitValue;
				valuesByName[value.Name] = numericValue;
				valuesByName[value.Symbol] = numericValue;
				nextValue = numericValue + BigInteger.One;

				json.WriteStartObject();
				WriteIdentity(json, value, includeKind: false, includeVisibility: false);
				WriteVariable(json, value, numericValue);
				WriteMetadata(json, value.Attributes);
				json.WriteEndObject();
				emitted.Add(value);
			}
			json.WriteEndArray();
		}

		void WritePropertyInfo(Utf8JsonWriter json, FunctionDefinition function)
		{
			if (!TryGetPropertyInfo(function, IsTypeScoped(function), out string? accessor, out string? propertyName, out bool indexer, out List<string>? indexParams, out string? valueParam))
				return;

			if (!string.IsNullOrWhiteSpace(propertyName))
				json.WriteString("propertyName", propertyName);
			if (indexer)
				json.WriteBoolean("propertyIndexer", true);
			if (indexParams.Count > 0)
			{
				json.WriteStartArray("propertyIndexParams");
				foreach (string parameter in indexParams)
					json.WriteStringValue(parameter);
				json.WriteEndArray();
			}
			if (!string.IsNullOrWhiteSpace(valueParam))
				json.WriteString("propertyValueParam", valueParam);
		}

		void WriteImplementedInterfaces(Utf8JsonWriter json, IReadOnlyList<TypeReference> baseTypes, TypeDefinition owner)
		{
			List<(string Type, Definition? Definition, VariableDefinition? VTable)> interfaces = [];
			HashSet<string> seen = new(StringComparer.Ordinal);
			foreach (TypeReference baseType in baseTypes)
			{
				if (TryGetInterfaceDefinition(baseType, out InterfaceDefinition? interfaceDefinition) && interfaceDefinition is not null && seen.Add(interfaceDefinition.Name))
					interfaces.Add((interfaceDefinition.Name, interfaceDefinition, FindInterfaceVTable(owner, interfaceDefinition.Name)));
			}

			foreach (VariableDefinition vtable in FindInterfaceVTables(owner))
			{
				string interfaceName = vtable.Symbol[(owner.Name.Length + 1)..];
				if (!seen.Add(interfaceName))
					continue;
				typeDefinitions.TryGetValue(interfaceName, out TypeDefinition? interfaceDefinition);
				interfaces.Add((interfaceName, interfaceDefinition, vtable));
			}

			if (interfaces.Count == 0)
				return;

			json.WriteStartArray("interfaces");
			foreach ((string type, Definition? definition, VariableDefinition? vtable) in interfaces.OrderBy(static item => item.Type, StringComparer.Ordinal))
			{
				json.WriteStartObject();
				json.WriteString("type", type);
				if (definition is not null)
					WriteReference(json, "ref", definition);
				if (vtable is not null && vtable.Export is not null)
					json.WriteString("symbol", vtable.Symbol);
				json.WriteEndObject();
			}
			json.WriteEndArray();
		}

		void WriteGenericParameter(Utf8JsonWriter json, GenericParameter parameter)
		{
			json.WriteStartObject();
			json.WriteString("id", GetId(parameter));
			json.WriteString("name", parameter.Name);
			WriteTypeProperty(json, "constraint", parameter.Constraint, parameter.Constraint?.ResolvedType ?? "nint");
			WriteMetadata(json, parameter.Attributes);
			json.WriteEndObject();
		}

		void WriteDefinitionArray<T>(Utf8JsonWriter json, string propertyName, IReadOnlyList<T> definitions, bool includeKind = false, bool includeVisibility = true)
			where T : Definition
		{
			List<T> sourceDefinitions = definitions.Where(static definition => !IsGeneratedDefinition(definition)).ToList();
			if (sourceDefinitions.Count == 0)
				return;
			json.WriteStartArray(propertyName);
			foreach (T definition in sourceDefinitions)
			{
				WriteDefinition(json, definition, includeKind, includeVisibility);
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

		void WriteReference(Utf8JsonWriter json, string propertyName, BindableNode node)
		{
			if (!ids.TryGetValue(node, out string? id))
				return;
			json.WriteString(propertyName, id);
			if (!emitted.Contains(node))
				stubbed.Add(node);
		}

		bool ShouldEmit(Definition definition)
		{
			if (IsGeneratedDefinition(definition))
				return false;

			return visibility switch
			{
				MetadataVisibility.None => false,
				MetadataVisibility.Export => definition.Export is not null,
				MetadataVisibility.Public => definition.Export is not null || definition.Public is not null,
				MetadataVisibility.All => true,
				_ => false
			};
		}

		static bool IsGeneratedDefinition(Definition definition)
		{
			return definition.SourceSyntax is null;
		}

		bool ShouldEmitClassFields(ClassDefinition classDefinition)
		{
			return visibility switch
			{
				MetadataVisibility.Export => false,
				MetadataVisibility.Public => classDefinition.Export is not null || classDefinition.Public is not null,
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

		static string GetCallableTypeName(CallableKind kind)
		{
			return kind switch
			{
				CallableKind.Function => "fn",
				CallableKind.Delegate => "delegate",
				CallableKind.Async => "async",
				CallableKind.Once => "once",
				_ => "fn"
			};
		}

		string? GetAliasTargetKind(AliasDefinition alias)
		{
			return alias.TargetKind switch
			{
				AliasTargetKind.CallSpec => "callspec",
				AliasTargetKind.TypeSpec => "typespec",
				AliasTargetKind.Callable => TryResolveAliasTargetDefinition(alias, out Definition? target) && target is FunctionDefinition function
					? GetFunctionAliasTargetKind(function)
					: "function",
				AliasTargetKind.Type => GetTypeAliasTargetKind(alias),
				_ => null
			};
		}

		string? GetTypeAliasTargetKind(AliasDefinition alias)
		{
			string name = !string.IsNullOrWhiteSpace(alias.ResolvedTargetName) ? alias.ResolvedTargetName : alias.TargetName;
			if (IsPrimitiveTypeName(name))
				return "primitive";
			if (TryResolveAliasTargetDefinition(alias, out Definition? target) && target is not null)
			{
				return target switch
				{
					AliasDefinition => "alias",
					NewtypeDefinition => "newtype",
					TypeDefinition => "type",
					_ => null
				};
			}
			return alias.TargetKind == AliasTargetKind.Type ? "type" : null;
		}

		string GetFunctionAliasTargetKind(FunctionDefinition function)
		{
			return function.Parameters.OfType<ThisParameterDefinition>().Any() || IsTypeScoped(function)
				? "method"
				: "function";
		}

		bool TryResolveAliasTargetDefinition(AliasDefinition alias, out Definition? definition)
		{
			string targetName = !string.IsNullOrWhiteSpace(alias.ResolvedTargetName) ? alias.ResolvedTargetName : alias.TargetName;
			if (symbols.TryGetValue(targetName, out definition))
				return true;
			definition = null;
			return false;
		}

		bool TryGetInterfaceDefinition(TypeReference type, out InterfaceDefinition? definition)
		{
			type = UnwrapTypeReference(type);
			if (type is TypeDefinitionReference { Definition: InterfaceDefinition interfaceDefinition })
			{
				definition = interfaceDefinition;
				return true;
			}
			if (type is NamedTypeReference named
				&& named.Qualifiers.Count == 0
				&& typeDefinitions.TryGetValue(named.Name, out TypeDefinition? typeDefinition)
				&& typeDefinition is InterfaceDefinition found)
			{
				definition = found;
				return true;
			}
			if (!string.IsNullOrWhiteSpace(type.ResolvedType)
				&& typeDefinitions.TryGetValue(type.ResolvedType, out TypeDefinition? resolvedDefinition)
				&& resolvedDefinition is InterfaceDefinition resolvedInterface)
			{
				definition = resolvedInterface;
				return true;
			}
			definition = null;
			return false;
		}

		VariableDefinition? FindInterfaceVTable(TypeDefinition owner, string interfaceName)
		{
			string symbol = owner.Name + "_" + interfaceName;
			return module.Definitions.OfType<VariableDefinition>().FirstOrDefault(variable => variable.Symbol == symbol || variable.Name == symbol);
		}

		IEnumerable<VariableDefinition> FindInterfaceVTables(TypeDefinition owner)
		{
			string prefix = owner.Name + "_";
			HashSet<string> storageSymbols = module.Definitions
				.OfType<VariableDefinition>()
				.Where(static variable => variable.Symbol.EndsWith("__storage", StringComparison.Ordinal))
				.Select(static variable => variable.Symbol)
				.ToHashSet(StringComparer.Ordinal);
			foreach (VariableDefinition variable in module.Definitions.OfType<VariableDefinition>())
			{
				if (!variable.Symbol.StartsWith(prefix, StringComparison.Ordinal)
					|| variable.Symbol.EndsWith("__storage", StringComparison.Ordinal)
					|| !storageSymbols.Contains(variable.Symbol + "__storage"))
					continue;
				string interfaceName = variable.Symbol[prefix.Length..];
				if (interfaceName.Length == 0 || interfaceName.Contains("__", StringComparison.Ordinal))
					continue;
				yield return variable;
			}
		}

		static TypeReference UnwrapTypeReference(TypeReference type)
		{
			while (true)
			{
				type = type switch
				{
					ConstTypeReference { Type: not null } constant => constant.Type,
					VolatileTypeReference { Type: not null } vol => vol.Type,
					EscapedTypeReference { Type: not null } escaped => escaped.Type,
					ScopedTypeReference { Type: not null } scoped => scoped.Type,
					UnscopedTypeReference { Type: not null } unscoped => unscoped.Type,
					_ => type
				};
				if (type is not ConstTypeReference and not VolatileTypeReference and not EscapedTypeReference and not ScopedTypeReference and not UnscopedTypeReference)
					return type;
			}
		}

		static bool IsPrimitiveTypeName(string name)
		{
			return name is "void" or "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint"
				or "long" or "ulong" or "float" or "double" or "nint" or "nuint" or "char" or "achar"
				or "wchar" or "uchar" or "string" or "astring" or "wstring" or "untyped";
		}

		static string? GetVisibility(Definition definition)
		{
			if (definition.Export is not null)
				return "export";
			if (definition.Public is not null)
				return "public";
			return null;
		}

		static string SerializeExpression(Expression expression)
		{
			using StringWriter writer = new();
			BindableNodeCodeSerializer.Serialize(expression, writer);
			return writer.ToString().Trim();
		}

		bool IsTypeScoped(FunctionDefinition function)
		{
			return ids.TryGetValue(function, out string? id) && id.Contains("/function:", StringComparison.Ordinal);
		}

		static bool TryGetPropertyInfo(FunctionDefinition function, bool isTypeScoped, out string accessor, out string propertyName, out bool indexer, out List<string> indexParams, out string? valueParam)
		{
			accessor = "";
			propertyName = "";
			indexer = false;
			indexParams = [];
			valueParam = null;

			if (!isTypeScoped && !function.Parameters.OfType<ThisParameterDefinition>().Any())
				return false;

			if (function.Name.StartsWith("get", StringComparison.Ordinal) && function.ResolvedType != "void" && function.IteratorKind == IteratorKind.None)
			{
				accessor = "get";
				propertyName = GetPropertyName(function.Name, "get", function.IsAsync);
				indexer = propertyName.Length == 0;
				indexParams.AddRange(GetPropertyParameterNames(function.Parameters));
				return function.Name.Length >= "get".Length;
			}

			if (function.Name.StartsWith("set", StringComparison.Ordinal) && function.IteratorKind == IteratorKind.None)
			{
				List<string> ordinaryParameters = GetPropertyParameterNames(function.Parameters);
				if (ordinaryParameters.Count == 0)
					return false;

				accessor = "set";
				propertyName = GetPropertyName(function.Name, "set", function.IsAsync);
				indexer = propertyName.Length == 0;
				valueParam = ordinaryParameters[^1];
				indexParams.AddRange(ordinaryParameters.Take(ordinaryParameters.Count - 1));
				return function.Name.Length >= "set".Length;
			}

			return false;
		}

		static string GetPropertyName(string functionName, string prefix, bool async)
		{
			string name = functionName[prefix.Length..];
			if (async && name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length)
				name = name[..^"Async".Length];
			return name;
		}

		static List<string> GetPropertyParameterNames(IReadOnlyList<ParameterDefinition> parameters)
		{
			List<string> names = [];
			foreach (ParameterDefinition parameter in parameters)
			{
				if (parameter is ThisParameterDefinition or WithinParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition)
					continue;
				if (parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown)
					continue;
				names.Add(parameter.Name);
			}
			return names;
		}

		static bool TryEvaluateIntegerExpression(Expression? expression, IReadOnlyDictionary<string, BigInteger> namedValues, out BigInteger value)
		{
			value = BigInteger.Zero;
			return expression switch
			{
				null => false,
				LiteralExpression literal => TryGetIntegerLiteral(literal, out value),
				ParenthesizedExpression parenthesized => TryEvaluateIntegerExpression(parenthesized.Expression, namedValues, out value),
				CastExpression cast => TryEvaluateIntegerExpression(cast.Expression, namedValues, out value),
				UnaryExpression unary => TryEvaluateUnaryIntegerExpression(unary, namedValues, out value),
				BinaryExpression binary => TryEvaluateBinaryIntegerExpression(binary, namedValues, out value),
				NamedExpression named when named.Qualifiers.Count == 0 => namedValues.TryGetValue(named.Name, out value),
				VariableReferenceExpression { Variable: VariableDefinition variable } => namedValues.TryGetValue(variable.Name, out value) || namedValues.TryGetValue(variable.Symbol, out value),
				_ => false
			};
		}

		static bool TryGetIntegerLiteral(LiteralExpression literal, out BigInteger value)
		{
			value = BigInteger.Zero;
			if (literal.Value is int intValue)
			{
				value = intValue;
				return true;
			}
			if (literal.Value is uint uintValue)
			{
				value = uintValue;
				return true;
			}
			if (literal.Value is long longValue)
			{
				value = longValue;
				return true;
			}
			if (literal.Value is ulong ulongValue)
			{
				value = ulongValue;
				return true;
			}
			string text = literal.Text.TrimEnd('u', 'U');
			if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				return BigInteger.TryParse(text[2..], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out value);
			return BigInteger.TryParse(text, out value);
		}

		static bool TryEvaluateUnaryIntegerExpression(UnaryExpression unary, IReadOnlyDictionary<string, BigInteger> namedValues, out BigInteger value)
		{
			value = BigInteger.Zero;
			if (!TryEvaluateIntegerExpression(unary.Operand, namedValues, out BigInteger operand))
				return false;
			switch (unary.Operator)
			{
				case UnaryOperator.Plus:
					value = operand;
					return true;
				case UnaryOperator.Minus:
					value = -operand;
					return true;
				case UnaryOperator.BitwiseNot:
					value = ~operand;
					return true;
				default:
					return false;
			}
		}

		static bool TryEvaluateBinaryIntegerExpression(BinaryExpression binary, IReadOnlyDictionary<string, BigInteger> namedValues, out BigInteger value)
		{
			value = BigInteger.Zero;
			if (!TryEvaluateIntegerExpression(binary.Left, namedValues, out BigInteger left) || !TryEvaluateIntegerExpression(binary.Right, namedValues, out BigInteger right))
				return false;
			switch (binary.Operator)
			{
				case BinaryOperator.Add:
					value = left + right;
					return true;
				case BinaryOperator.Subtract:
					value = left - right;
					return true;
				case BinaryOperator.Multiply:
					value = left * right;
					return true;
				case BinaryOperator.Divide when right != BigInteger.Zero:
					value = left / right;
					return true;
				case BinaryOperator.Modulo when right != BigInteger.Zero:
					value = left % right;
					return true;
				case BinaryOperator.BitwiseAnd:
					value = left & right;
					return true;
				case BinaryOperator.BitwiseOr:
					value = left | right;
					return true;
				case BinaryOperator.BitwiseXor:
					value = left ^ right;
					return true;
				case BinaryOperator.LeftShift when right >= BigInteger.Zero && right <= int.MaxValue:
					value = left << (int)right;
					return true;
				case BinaryOperator.RightShift when right >= BigInteger.Zero && right <= int.MaxValue:
					value = left >> (int)right;
					return true;
				default:
					return false;
			}
		}

		static void WriteInteger(Utf8JsonWriter json, string propertyName, BigInteger value)
		{
			if (value >= long.MinValue && value <= long.MaxValue)
				json.WriteNumber(propertyName, (long)value);
			else if (value >= BigInteger.Zero && value <= ulong.MaxValue)
				json.WriteNumber(propertyName, (ulong)value);
			else
				json.WriteString(propertyName, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
		}

		static bool IsSameSymbol(Definition definition)
		{
			return string.IsNullOrWhiteSpace(definition.Symbol) || definition.Symbol == definition.Name;
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
