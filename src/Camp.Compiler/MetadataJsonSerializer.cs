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
	const string CategoryAttributeName = "@category";

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
		SourcefilePathMapper? sourcefilePathMapper;
		string? currentStaticClassName;

		bool IsExportApiView => visibility == MetadataVisibility.Export;

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
			foreach (Definition definition in ActiveDefinitions())
			{
				if (definition is TypeDefinition typeDefinition)
				{
					AddTypeDefinitionKey(definition.Name, typeDefinition, overwrite: false);
					AddTypeDefinitionKey(GetQualifiedDefinitionName(typeDefinition), typeDefinition, overwrite: true);
					if (!string.IsNullOrWhiteSpace(definition.Symbol))
						AddTypeDefinitionKey(definition.Symbol, typeDefinition, overwrite: true);
				}
				if (IsOutOfScopeStaticClassMember(definition))
					continue;
				if (!string.IsNullOrWhiteSpace(definition.Name))
					symbols.TryAdd(definition.Name, definition);
				if (!string.IsNullOrWhiteSpace(definition.Symbol))
					symbols.TryAdd(definition.Symbol, definition);
				IndexDefinition(definition, GetTopLevelId(definition));
			}
		}

		void AddTypeDefinitionKey(string? key, TypeDefinition definition, bool overwrite)
		{
			if (string.IsNullOrWhiteSpace(key))
				return;
			if (overwrite)
				typeDefinitions[key] = definition;
			else
				typeDefinitions.TryAdd(key, definition);
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
				case StaticClassDefinition staticClassDefinition:
				{
					string? previousStaticClassName = currentStaticClassName;
					currentStaticClassName = staticClassDefinition.Name;
					IndexChildren(id, "field", staticClassDefinition.Fields);
					IndexChildren(id, "function", GetStaticClassFunctions(staticClassDefinition));
					currentStaticClassName = previousStaticClassName;
					break;
				}
				case StructDefinition { SourceInterface: not null } structDefinition:
					ids[structDefinition.SourceInterface] = id;
					foreach (GenericParameter parameter in structDefinition.SourceInterface.GenericParameters)
						ids[parameter] = id + "/type-parameter:" + parameter.Name;
					IndexChildren(id, "function", structDefinition.SourceInterface.Functions);
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
				IndexDefinition(child, parentId + "/" + kind + ":" + GetMetadataIdName(child));
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
			json.WriteString("name", module.Namespace ?? GetProjectName());
			if (!string.IsNullOrWhiteSpace(module.Namespace))
				json.WriteString("namespace", module.Namespace);
			json.WriteEndObject();
		}

		void WriteView(Utf8JsonWriter json)
		{
			json.WriteStartObject("view");
			json.WriteString("visibility", visibility.ToString().ToLowerInvariant());
			json.WriteString("level", IsExportApiView ? "api" : "source");
			json.WriteBoolean("generated", false);
			json.WriteEndObject();
		}

		void WriteDeclarations(Utf8JsonWriter json)
		{
			json.WriteStartArray("declarations");
			foreach (Definition definition in ActiveDefinitions())
			{
				if (IsOutOfScopeStaticClassMember(definition))
					continue;
				if (!ShouldEmit(definition))
					continue;
				WriteDefinition(json, definition, includeKind: true, includeVisibility: true);
				MarkEmitted(definition);
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
			WriteTestFacts(json, definition);
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
				case StructDefinition { SourceInterface: not null } structDefinition:
					WriteSourceInterfaceDefinition(json, structDefinition.SourceInterface);
					WriteMetadata(json, structDefinition.SourceInterface.Attributes);
					json.WriteEndObject();
					return;
				case TypeDefinition type:
					WriteTypeDefinition(json, type);
					break;
				case StaticClassDefinition staticClassDefinition:
					WriteStaticClassDefinition(json, staticClassDefinition);
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
					if (field.IsInline)
					{
						json.WriteBoolean("inline", true);
						WriteConstantValue(json, "value", field.ConstantValue);
					}
					if (field.Modifier == FieldModifier.Static)
						json.WriteBoolean("static", true);
					if (field.IsFixedStorage)
						json.WriteBoolean("fixed", true);
					break;
			}
			WriteEffectiveMetadata(json, definition);
			json.WriteEndObject();
		}

		void WriteIdentity(Utf8JsonWriter json, Definition definition, bool includeKind, bool includeVisibility)
		{
			json.WriteString("id", GetId(definition));
			if (includeKind)
				json.WriteString("kind", GetKind(definition));
			json.WriteString("name", GetMetadataName(definition));
			if (!IsSameSymbol(definition))
				json.WriteString("symbol", definition.Symbol);
			if (includeVisibility && GetVisibility(definition) is string visibility)
				json.WriteString("visibility", visibility);
			if (ShouldWriteExtern(definition))
				json.WriteBoolean("extern", true);
		}

		void WriteTestFacts(Utf8JsonWriter json, Definition definition)
		{
			bool isTest = definition is FunctionDefinition && HasAttribute(definition.Attributes, "@test");
			bool isTestOnly = isTest || HasAttribute(definition.Attributes, "@testonly");
			if (isTestOnly)
				json.WriteBoolean("testOnly", true);
			if (isTest && definition is FunctionDefinition function)
				WriteTestInfo(json, function);
		}

		void WriteTestInfo(Utf8JsonWriter json, FunctionDefinition function)
		{
			string name = GetVisibleFunctionName(function);
			string qualifiedName = GetQualifiedFunctionName(function, name);
			json.WriteStartObject("test");
			json.WriteString("id", qualifiedName);
			json.WriteString("name", name);
			json.WriteString("qualifiedName", qualifiedName);
			if (TryGetSourceLocation(function, out string? sourcefile, out int sourceline))
			{
				json.WriteString("sourcefile", sourcefile);
				json.WriteNumber("sourceline", sourceline);
			}
			json.WriteString("summary", GetAttributeStringContent(function.Attributes, "@summary") ?? "");
			bool skipped = TryGetAttributeStringContent(function.Attributes, "@skip", out string? skipReason);
			json.WriteBoolean("skipped", skipped);
			if (skipped && skipReason is not null)
				json.WriteString("skipReason", skipReason);
			else
				json.WriteNull("skipReason");
			json.WriteString("runnerSignature", CampTestDiscovery.TryGetBuiltInRunnerSignature(module, function, out _) ? "valid" : "invalid");
			json.WriteBoolean("hasBody", function.Body is not null);
			json.WriteEndObject();
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
					if (!IsExportApiView && classDefinition.Modifier != ClassModifier.None)
						json.WriteString("modifier", classDefinition.Modifier.ToString().ToLowerInvariant());
					if (ShouldWriteShadowMarker(classDefinition))
						json.WriteBoolean("shadow", true);
					IReadOnlyList<TypeReference> classBaseTypes = IsExportApiView
						? GetApiBaseTypes(classDefinition)
						: classDefinition.BaseTypes;
					WriteTypes(json, "baseTypes", classBaseTypes);
					WriteImplementedInterfaces(json, classBaseTypes, classDefinition);
					if (ShouldEmitClassFields(classDefinition))
						WriteFieldArray(json, "fields", classDefinition.Fields, classFields: true);
					WriteFunctionArray(json, "functions", classDefinition);
					break;
				case StructDefinition structDefinition:
					if (structDefinition.Modifier != StructModifier.None)
						json.WriteString("modifier", structDefinition.Modifier.ToString().ToLowerInvariant());
					IReadOnlyList<TypeReference> structBaseTypes = IsExportApiView ? [] : structDefinition.BaseTypes;
					WriteTypes(json, "baseTypes", structBaseTypes);
					if (!IsExportApiView)
						WriteImplementedInterfaces(json, structDefinition.BaseTypes, structDefinition);
					WriteFieldArray(json, "fields", structDefinition.Fields, classFields: false);
					WriteFunctionArray(json, "functions", structDefinition.Functions);
					break;
				case InterfaceDefinition interfaceDefinition:
					WriteTypes(json, "baseTypes", interfaceDefinition.BaseTypes);
					WriteDefinitionArray(json, "functions", interfaceDefinition.Functions);
					break;
				case EnumDefinition enumDefinition:
					WriteTypeProperty(json, "underlyingType", enumDefinition.UnderlyingType, enumDefinition.UnderlyingType?.ResolvedType);
					WriteEnumValues(json, enumDefinition);
					WriteFunctionArray(json, "functions", enumDefinition.Functions);
					break;
				case NewtypeDefinition newtypeDefinition:
					if (IsCallableNewtype(newtypeDefinition))
						WriteCallableNewtype(json, newtypeDefinition);
					else
						WriteTypeProperty(json, "underlyingType", newtypeDefinition.UnderlyingType, newtypeDefinition.ResolvedType);
					WriteDefinitionArray(json, "parameters", newtypeDefinition.Parameters, includeKind: false, includeVisibility: false);
					WriteDefinitionArray(json, "fields", newtypeDefinition.Fields, includeKind: false, includeVisibility: false);
					WriteFunctionArray(json, "functions", newtypeDefinition.Functions);
					break;
			}
		}

		void WriteStaticClassDefinition(Utf8JsonWriter json, StaticClassDefinition definition)
		{
			string? previousStaticClassName = currentStaticClassName;
			currentStaticClassName = definition.Name;
			WriteFieldArray(json, "fields", definition.Fields, classFields: true);
			WriteFunctionArray(json, "functions", GetStaticClassFunctions(definition));
			currentStaticClassName = previousStaticClassName;
		}

		void WriteSourceInterfaceDefinition(Utf8JsonWriter json, InterfaceDefinition interfaceDefinition)
		{
			if (interfaceDefinition.GenericParameters.Count > 0)
			{
				json.WriteStartArray("typeParameters");
				foreach (GenericParameter parameter in interfaceDefinition.GenericParameters)
					WriteGenericParameter(json, parameter);
				json.WriteEndArray();
			}
			WriteTypes(json, "baseTypes", interfaceDefinition.BaseTypes);
			WriteDefinitionArray(json, "functions", interfaceDefinition.Functions);
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
			if (FunctionHasOverloadSelector(function))
			{
				json.WriteString("invokerName", SymbolNameService.InvokerName(function).Value);
				json.WriteString("callableName", SymbolNameService.CallableName(function).Value);
			}
			WriteAvailability(json, function);
			if (function.IteratorKind != IteratorKind.None)
				json.WriteString("iterator", function.IteratorKind.ToString().ToLowerInvariant());
			if (IsMetadataAsync(function))
				json.WriteBoolean("async", true);
			if (ShouldWriteFunctionModifier(function))
				json.WriteString("modifier", function.Modifier.ToString().ToLowerInvariant());
			if (!string.IsNullOrWhiteSpace(function.CallSpec))
				json.WriteString("callspec", function.CallSpec);
			WriteTypeProperty(json, "returnType", function.ReturnType, function.ResolvedType);
			if (function.InterfaceImplementationInterface is not null)
			{
				json.WriteStartObject("interfaceImplementation");
				WriteReference(json, "interfaceRef", function.InterfaceImplementationInterface);
				json.WriteString("interface", function.InterfaceImplementationInterface.Name);
				if (function.InterfaceImplementationMember is not null)
				{
					json.WriteString("slot", SymbolNameService.CallableName(function.InterfaceImplementationMember).Value);
					WriteReference(json, "slotRef", function.InterfaceImplementationMember);
				}
				else if (!string.IsNullOrWhiteSpace(function.InterfaceImplementationSlotName))
				{
					json.WriteString("slot", function.InterfaceImplementationSlotName);
				}
				json.WriteEndObject();
			}
			else if (function.CallableAscriptionType is not null)
				WriteTypeProperty(json, "ascription", function.CallableAscriptionType, function.CallableAscriptionType.ResolvedType);
			WriteInterfaceSlotInitializer(json, function);
			WritePropertyInfo(json, function);
			if (function.GenericParameters.Count > 0)
			{
				json.WriteStartArray("typeParameters");
				foreach (GenericParameter parameter in function.GenericParameters)
					WriteGenericParameter(json, parameter);
				json.WriteEndArray();
			}
			WriteParameterArray(json, "parameters", function.Parameters);
		}

		void WriteAvailability(Utf8JsonWriter json, FunctionDefinition function)
		{
			if (!UnsupportedAvailability.TryGetAttribute(function.Attributes, out AttributeConstructor? attribute) || attribute is null)
				return;

			json.WriteStartObject("availability");
			json.WriteBoolean("supported", false);
			if (UnsupportedAvailability.GetReason(attribute) is string reason && !string.IsNullOrWhiteSpace(reason))
				json.WriteString("reason", reason);
			json.WriteEndObject();
		}

		void WriteInterfaceSlotInitializer(Utf8JsonWriter json, FunctionDefinition function)
		{
			if (function.InterfaceSlotInitializer is null)
				return;

			json.WriteStartObject("interfaceSlotInitializer");
			string kind = function.InterfaceSlotInitializerKind switch
			{
				InterfaceSlotInitializerKind.Null => "null",
				InterfaceSlotInitializerKind.Function => "function",
				_ => "unresolved"
			};
			json.WriteString("kind", kind);
			json.WriteString("source", SerializeExpression(function.InterfaceSlotInitializer));
			if (function.InterfaceSlotInitializerTarget is FunctionDefinition target)
			{
				json.WriteString("target", target.Name);
				if (!IsSameSymbol(target))
					json.WriteString("targetSymbol", target.Symbol);
				WriteReference(json, "targetRef", target);
			}
			json.WriteEndObject();
		}

		void WriteParameter(Utf8JsonWriter json, ParameterDefinition parameter)
		{
			if (parameter.Modifier != ParameterModifier.None)
				json.WriteString("modifier", parameter.Modifier.ToString().ToLowerInvariant());
			if (parameter.IsOverloadSelector)
				json.WriteBoolean("overload", true);
			if (parameter is SizeOfParameterDefinition)
			{
				json.WriteString("capability", "sizeof");
				json.WriteString("type", "nuint");
				WriteTypeProperty(json, "targetType", parameter.Type, parameter.Type?.ResolvedType);
			}
			else if (parameter is NameOfParameterDefinition)
			{
				json.WriteString("capability", "typenameof");
				json.WriteString("type", "string");
				WriteTypeProperty(json, "targetType", parameter.Type, parameter.Type?.ResolvedType);
			}
			else
				WriteTypeProperty(json, "type", parameter.Type, parameter.ResolvedType);
			if (parameter.DefaultValue is not null)
			{
				json.WriteString("defaultValue", SerializeExpression(parameter.DefaultValue));
				WriteDefaultExpression(json, parameter.DefaultValue);
			}
			if (parameter is VTableOfParameterDefinition vtable)
				WriteTypeProperty(json, "interfaceType", vtable.InterfaceType, vtable.InterfaceType?.ResolvedType);
		}

		static void WriteDefaultExpression(Utf8JsonWriter json, Expression expression)
		{
			switch (expression)
			{
				case CallerSourceCaptureExpression caller:
					json.WriteStartObject("defaultExpression");
					json.WriteString("kind", "caller");
					json.WriteString("selector", GetCallerSourceCaptureMetadataSelector(caller.Selector));
					json.WriteEndObject();
					break;
				case SourceOfExpression sourceOf:
					json.WriteStartObject("defaultExpression");
					json.WriteString("kind", "sourceof");
					json.WriteString("argument", sourceOf.ArgumentName);
					json.WriteEndObject();
					break;
			}
		}

		static string GetCallerSourceCaptureMetadataSelector(CallerSourceCaptureSelector selector)
		{
			return selector switch
			{
				CallerSourceCaptureSelector.SourceLine => "sourceline",
				CallerSourceCaptureSelector.SourceFile => "sourcefile",
				CallerSourceCaptureSelector.PropertyName => "propertyname",
				CallerSourceCaptureSelector.FunctionName => "functionname",
				CallerSourceCaptureSelector.QualifiedName => "qualifiedname",
				_ => "sourcefile"
			};
		}

		void WriteVariable(Utf8JsonWriter json, VariableDefinition variable, BigInteger? enumValue)
		{
			WriteTypeProperty(json, "type", variable.Type, variable.ResolvedType);
			if (variable.OutOfScopeOwnerName is not null)
				json.WriteBoolean("static", true);
			if (variable.IsInline)
			{
				json.WriteBoolean("inline", true);
				WriteConstantValue(json, "value", variable.ConstantValue);
			}
			else if (enumValue is not null)
				WriteInteger(json, "value", enumValue.Value);
			if (variable.IsFixedStorage)
				json.WriteBoolean("fixed", true);
		}

		void WriteEnumValues(Utf8JsonWriter json, EnumDefinition enumDefinition)
		{
			List<VariableDefinition> sourceDefinitions = enumDefinition.Values.Where(static definition => !IsGeneratedDefinition(definition)).ToList();
			if (sourceDefinitions.Count == 0)
				return;

			json.WriteStartArray("values");
			foreach (VariableDefinition value in sourceDefinitions)
			{
				BigInteger numericValue = value.ConstantValue is ConstantValue.Integer integer ? integer.Value : BigInteger.Zero;

				json.WriteStartObject();
				WriteIdentity(json, value, includeKind: false, includeVisibility: false);
				WriteVariable(json, value, numericValue);
				WriteMetadata(json, value.Attributes);
				json.WriteEndObject();
				emitted.Add(value);
			}
			json.WriteEndArray();
		}

		static void WriteConstantValue(Utf8JsonWriter json, string propertyName, ConstantValue? value)
		{
			switch (value)
			{
				case ConstantValue.Integer integer:
					WriteInteger(json, propertyName, integer.Value);
					break;
				case ConstantValue.Boolean boolean:
					json.WriteBoolean(propertyName, boolean.Value);
					break;
				case ConstantValue.String text:
					json.WriteString(propertyName, text.Value);
					break;
				case ConstantValue.Character text:
					json.WriteString(propertyName, text.Value);
					break;
				case ConstantValue.Null:
				case null:
					json.WriteNull(propertyName);
					break;
			}
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
				string prefix = SymbolName(owner) + "_";
				string interfaceSymbol = vtable.Symbol.StartsWith(prefix, StringComparison.Ordinal) ? vtable.Symbol[prefix.Length..] : "";
				string interfaceName = ResolveTypeNameFromSymbol(interfaceSymbol) ?? interfaceSymbol;
				if (IsExportApiView && owner is ClassDefinition classOwner && !IsExportInterfaceVisible(classOwner, interfaceName))
					continue;
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
				MarkEmitted(definition);
			}
			json.WriteEndArray();
		}

		void WriteParameterArray(Utf8JsonWriter json, string propertyName, IReadOnlyList<ParameterDefinition> parameters)
		{
			List<ParameterDefinition> filtered = FilterApiParameters(parameters);
			if (filtered.Count == 0)
				return;

			json.WriteStartArray(propertyName);
			foreach (ParameterDefinition parameter in filtered)
			{
				WriteDefinition(json, parameter, includeKind: false, includeVisibility: false);
				emitted.Add(parameter);
			}
			json.WriteEndArray();
		}

		List<ParameterDefinition> FilterApiParameters(IReadOnlyList<ParameterDefinition> parameters)
		{
			List<ParameterDefinition> result = [];
			foreach (ParameterDefinition parameter in parameters)
			{
				ParameterDefinition? previous = result.Count == 0 ? null : result[^1];
				if (IsGeneratedCallableContextParameter(parameter, previous))
					continue;
				result.Add(parameter);
			}
			return result;
		}

		static bool IsGeneratedCallableContextParameter(ParameterDefinition parameter, ParameterDefinition? previous)
		{
			return previous is not null
				&& !string.IsNullOrWhiteSpace(previous.Name)
				&& parameter.Name == previous.Name + "_context"
				&& IsVoidPointerParameter(parameter);
		}

		static bool IsVoidPointerParameter(ParameterDefinition parameter)
		{
			return parameter.Type is PointerTypeReference { ElementType: PrimitiveTypeReference { Type: PrimitiveType.Untyped } }
				|| parameter.ResolvedType == "void*";
		}

		bool IsMetadataAsync(FunctionDefinition function)
		{
			return function.IsAsync || IsAwaitableAsyncShape(function);
		}

		bool IsAwaitableAsyncShape(FunctionDefinition function)
		{
			if (!GetMetadataName(function).EndsWith("Async", StringComparison.Ordinal))
				return false;
			if (FilterApiParameters(function.Parameters) is not [.., ParameterDefinition last]
				|| !TryGetAwaitableCallbackShape(last, out CallableShape callback)
				|| callback.Kind != "once"
				|| callback.ReturnType != "void")
				return false;

			int successSlots = 0;
			int thrownSlots = 0;
			foreach (string parameter in callback.Parameters)
			{
				CallableSlot slot = CallableShapeService.ParseCallableSlot(parameter);
				if (slot.Modifier == "out")
					return false;
				if (slot.Modifier == "thrown")
					thrownSlots++;
				else
					successSlots++;
			}
			return successSlots <= 1 && thrownSlots <= 1;
		}

		bool TryGetAwaitableCallbackShape(ParameterDefinition parameter, out CallableShape callback)
		{
			if (parameter.Type is not null && TryParseAwaitableCallbackShape(FormatType(parameter.Type, parameter.Type.ResolvedType), out callback))
				return true;
			string? type = parameter.ResolvedType ?? parameter.Type?.ResolvedType;
			if (TryParseAwaitableCallbackShape(type, out callback))
				return true;
			if (TryGetCallableTypeReference(parameter.Type, out CallableTypeReference? callable) && callable is not null)
			{
				string returnType = FormatType(callable.ReturnType, callable.ReturnType?.ResolvedType);
				List<string> parameters = [.. callable.Parameters.Select(FormatCallableParameterSlot)];
				callback = new CallableShape(GetCallableTypeName(callable.Kind), callable.TargetSpec, callable.CallSpec, returnType, parameters);
				return true;
			}
			callback = default;
			return false;
		}

		static bool TryParseAwaitableCallbackShape(string? type, out CallableShape callback)
		{
			if (!string.IsNullOrWhiteSpace(type))
			{
				if (CallableShapeService.TryParseCallableShape(type, out callback))
					return true;
				string structuralType = StripTopLevelLifetimeQualifiers(type);
				if (structuralType != type && CallableShapeService.TryParseCallableShape(structuralType, out callback))
					return true;
			}
			callback = default;
			return false;
		}

		string FormatCallableParameterSlot(ParameterDefinition parameter)
		{
			string type = FormatType(parameter.Type, parameter.ResolvedType);
			return parameter.Modifier == ParameterModifier.None
				? type
				: parameter.Modifier.ToString().ToLowerInvariant() + " " + type;
		}

		static bool TryGetCallableTypeReference(TypeReference? type, out CallableTypeReference? callable)
		{
			switch (type)
			{
				case CallableTypeReference result:
					callable = result;
					return true;
				case AttributedTypeReference attributed:
					return TryGetCallableTypeReference(attributed.Type, out callable);
				case EscapedTypeReference escaped:
					return TryGetCallableTypeReference(escaped.Type, out callable);
				case ScopedTypeReference scoped:
					return TryGetCallableTypeReference(scoped.Type, out callable);
				case UnscopedTypeReference unscoped:
					return TryGetCallableTypeReference(unscoped.Type, out callable);
				case ConstTypeReference constant:
					return TryGetCallableTypeReference(constant.Type, out callable);
				case ConstOfTypeReference constOf:
					return TryGetCallableTypeReference(constOf.Type, out callable);
				case VolatileTypeReference vol:
					return TryGetCallableTypeReference(vol.Type, out callable);
				default:
					callable = null;
					return false;
			}
		}

		static string StripTopLevelLifetimeQualifiers(string type)
		{
			string result = type.Trim();
			while (true)
			{
				if (result.StartsWith("escaped ", StringComparison.Ordinal))
				{
					result = result["escaped ".Length..].TrimStart();
					continue;
				}
				if (result.StartsWith("scoped ", StringComparison.Ordinal))
				{
					result = result["scoped ".Length..].TrimStart();
					continue;
				}
				if (result.StartsWith("unscoped ", StringComparison.Ordinal))
				{
					result = result["unscoped ".Length..].TrimStart();
					continue;
				}
				return result;
			}
		}

		void MarkEmitted(Definition definition)
		{
			emitted.Add(definition);
			if (definition is StructDefinition { SourceInterface: not null } structDefinition)
				emitted.Add(structDefinition.SourceInterface);
		}

		void WriteFieldArray(Utf8JsonWriter json, string propertyName, IReadOnlyList<FieldDefinition> fields, bool classFields)
		{
			List<FieldDefinition> filtered = fields
				.Where(field => !IsGeneratedDefinition(field) && ShouldEmitFieldMember(field, classFields))
				.ToList();
			if (filtered.Count == 0)
				return;

			json.WriteStartArray(propertyName);
			foreach (FieldDefinition field in filtered)
			{
				WriteDefinition(json, field, includeKind: false, includeVisibility: false);
				emitted.Add(field);
			}
			json.WriteEndArray();
		}

		void WriteFunctionArray(Utf8JsonWriter json, string propertyName, IReadOnlyList<FunctionDefinition> functions)
		{
			WriteFunctionArray(json, propertyName, classDefinition: null, functions);
		}

		List<FunctionDefinition> GetStaticClassFunctions(StaticClassDefinition definition)
		{
			List<FunctionDefinition> functions = [.. definition.Functions];
			foreach (Definition candidate in ActiveDefinitions())
			{
				if (candidate is FunctionDefinition function && function.OutOfScopeOwnerName == definition.Name)
					functions.Add(function);
			}
			return functions;
		}

		void WriteFunctionArray(Utf8JsonWriter json, string propertyName, ClassDefinition classDefinition)
		{
			WriteFunctionArray(json, propertyName, classDefinition, classDefinition.Functions);
		}

		void WriteFunctionArray(Utf8JsonWriter json, string propertyName, ClassDefinition? classDefinition, IReadOnlyList<FunctionDefinition> functions)
		{
			List<FunctionDefinition> filtered = functions
				.Where(function => !IsGeneratedDefinition(function) && !IsGeneratedLifecycleDefinition(function) && ShouldEmitFunctionMember(function))
				.ToList();
			List<InterfaceDefinition> interfaceAccessors = IsExportApiView && classDefinition is not null ? GetApiInterfaceAccessors(classDefinition) : [];
			bool syntheticConstructor = IsExportApiView && classDefinition is not null && ShouldWriteSyntheticApiConstructor(classDefinition, functions);
			bool syntheticDestructor = IsExportApiView && classDefinition is not null && ShouldWriteSyntheticApiDestructor(classDefinition, functions);
			if (filtered.Count == 0 && interfaceAccessors.Count == 0 && !syntheticConstructor && !syntheticDestructor)
				return;

			json.WriteStartArray(propertyName);
			if (syntheticConstructor)
				WriteSyntheticConstructor(json, classDefinition!, functions);
			if (syntheticDestructor)
				WriteSyntheticDestructor(json, classDefinition!, functions);
			foreach (InterfaceDefinition interfaceDefinition in interfaceAccessors)
				WriteSyntheticInterfaceAccessor(json, classDefinition!, interfaceDefinition);
			foreach (FunctionDefinition function in filtered)
			{
				WriteDefinition(json, function, includeKind: false, includeVisibility: true);
				emitted.Add(function);
			}
			json.WriteEndArray();
		}

		void WriteSyntheticConstructor(Utf8JsonWriter json, ClassDefinition definition, IReadOnlyList<FunctionDefinition> functions)
		{
			bool retainsAllocator = LifecycleAllocatorPolicy.RetainsAllocator(functions);
			json.WriteStartObject();
			json.WriteString("id", GetId(definition) + "/function:" + definition.Name);
			json.WriteString("name", definition.Name);
			json.WriteString("visibility", "export");
			json.WriteBoolean("extern", true);
			json.WriteString("modifier", "constructor");
			if (LifecycleAllocatorPolicy.SyntheticConstructorUsesAllocator(module, definition, functions, retainsAllocator, MetadataAllocatorTypeAvailable()))
				WriteSyntheticWithinAllocatorParameter(json, GetId(definition) + "/function:" + definition.Name);
			json.WriteEndObject();
		}

		void WriteSyntheticDestructor(Utf8JsonWriter json, ClassDefinition definition, IReadOnlyList<FunctionDefinition> functions)
		{
			bool retainsAllocator = LifecycleAllocatorPolicy.RetainsAllocator(functions);
			json.WriteStartObject();
			json.WriteString("id", GetId(definition) + "/function:~" + definition.Name);
			json.WriteString("name", "~" + definition.Name);
			json.WriteString("visibility", "export");
			json.WriteBoolean("extern", true);
			json.WriteString("modifier", "destructor");
			if (LifecycleAllocatorPolicy.SyntheticDestructorUsesAllocator(module, definition, functions, retainsAllocator, MetadataAllocatorTypeAvailable()))
				WriteSyntheticWithinAllocatorParameter(json, GetId(definition) + "/function:~" + definition.Name);
			json.WriteEndObject();
		}

		static void WriteSyntheticWithinAllocatorParameter(Utf8JsonWriter json, string functionId)
		{
			json.WriteStartArray("parameters");
			json.WriteStartObject();
			json.WriteString("id", functionId + "/parameter:allocator");
			json.WriteString("name", "allocator");
			json.WriteString("modifier", "within");
			json.WriteString("type", "Allocator*");
			json.WriteEndObject();
			json.WriteEndArray();
		}

		void WriteSyntheticInterfaceAccessor(Utf8JsonWriter json, ClassDefinition classDefinition, InterfaceDefinition interfaceDefinition)
		{
			json.WriteStartObject();
			json.WriteString("id", GetId(classDefinition) + "/function:get" + interfaceDefinition.Name);
			json.WriteString("name", "get" + interfaceDefinition.Name);
			json.WriteString("visibility", "export");
			json.WriteBoolean("extern", true);
			json.WriteString("returnType", "constof(this) " + interfaceDefinition.Name + "*");
			json.WriteString("propertyName", interfaceDefinition.Name);
			WriteReference(json, "interfaceRef", interfaceDefinition);
			json.WriteEndObject();
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

		void WriteTypeProperty(Utf8JsonWriter json, string propertyName, TypeReference? type, string? resolvedType)
		{
			if (type is null && string.IsNullOrWhiteSpace(resolvedType))
				return;
			json.WriteString(propertyName, FormatType(type, resolvedType));
		}

		string FormatType(TypeReference? type, string? resolvedType)
		{
			string formatted = type is not null
				? BindableNodeCodeSerializer.SerializeType(type)
				: string.IsNullOrWhiteSpace(resolvedType)
					? BindableNodeAnalyzer.FormatTypeReference(type)
					: resolvedType!;
			return FormatSourceMetadataType(formatted);
		}

			string FormatSourceMetadataType(string type)
			{
				type = type.Replace("#THIS", "escaped this", StringComparison.Ordinal);
				HashSet<TypeDefinition> visited = new(ReferenceEqualityComparer.Instance);
				foreach (TypeDefinition definition in typeDefinitions.Values)
				{
					if (!visited.Add(definition))
						continue;
					if (string.IsNullOrWhiteSpace(definition.Symbol) || definition.Symbol == definition.Name)
						continue;
					type = ReplaceTypeToken(type, definition.Symbol, definition.Name);
			}
			return type;
		}

		static string ReplaceTypeToken(string text, string token, string replacement)
		{
			int index = 0;
			while (index < text.Length)
			{
				int found = text.IndexOf(token, index, StringComparison.Ordinal);
				if (found < 0)
					break;
				int end = found + token.Length;
				bool before = found == 0 || !IsIdentifierChar(text[found - 1]);
				bool after = end == text.Length || !IsIdentifierChar(text[end]);
				if (before && after)
				{
					text = text[..found] + replacement + text[end..];
					index = found + replacement.Length;
				}
				else
				{
					index = end;
				}
			}
			return text;
		}

		static bool IsIdentifierChar(char ch)
		{
			return char.IsLetterOrDigit(ch) || ch == '_';
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

		void WriteEffectiveMetadata(Utf8JsonWriter json, Definition definition)
		{
			if (HasAttribute(definition.Attributes, CategoryAttributeName))
			{
				WriteMetadata(json, definition.Attributes);
				return;
			}

			if (!TryGetInheritedCategoryAttribute(definition, out AttributeConstructor? inheritedCategory))
				_ = TryGetFileCategoryAttribute(definition, out inheritedCategory);

			if (inheritedCategory is null)
			{
				WriteMetadata(json, definition.Attributes);
				return;
			}

			if (definition.Attributes.Count == 0)
			{
				WriteMetadata(json, [inheritedCategory]);
				return;
			}

			WriteMetadata(json, [.. definition.Attributes, inheritedCategory]);
		}

		bool TryGetInheritedCategoryAttribute(Definition definition, out AttributeConstructor? category)
		{
			category = null;
			if (!TryGetExtensionOwnerTypeName(definition, out string? ownerTypeName))
				return false;
			if (!typeDefinitions.TryGetValue(ownerTypeName, out TypeDefinition? ownerType))
				return false;
			if (TryGetExplicitCategoryAttribute(ownerType, out category))
				return true;
			return TryGetFileCategoryAttribute(ownerType, out category);
		}

		bool TryGetExtensionOwnerTypeName(Definition definition, out string ownerTypeName)
		{
			ownerTypeName = "";
			if (!string.IsNullOrWhiteSpace(definition.OutOfScopeOwnerName))
			{
				ownerTypeName = BaseTypeName(definition.OutOfScopeOwnerName);
				return true;
			}

			if (definition is FunctionDefinition function && GetExplicitThisParameter(function) is ThisParameterDefinition receiver)
			{
				string? receiverType = receiver.ResolvedType ?? FormatSourceMetadataType(BindableNodeAnalyzer.FormatTypeReference(receiver.Type));
				ownerTypeName = BaseTypeName(receiverType ?? "");
				return !string.IsNullOrWhiteSpace(ownerTypeName);
			}

			return false;
		}

		bool TryGetFileCategoryAttribute(Definition definition, out AttributeConstructor? category)
		{
			category = null;
			if (!compilation.DefinitionOwners.TryGetValue(definition, out SourceFile? owner))
				return false;
			foreach (AttributeConstructor attribute in owner.FileMetadataAttributes)
			{
				if (AttributeNameEquals(attribute.Name, CategoryAttributeName))
				{
					category = attribute;
					return true;
				}
			}
			return false;
		}

		static bool TryGetExplicitCategoryAttribute(Definition definition, out AttributeConstructor? category)
		{
			foreach (AttributeConstructor attribute in definition.Attributes)
			{
				if (AttributeNameEquals(attribute.Name, CategoryAttributeName))
				{
					category = attribute;
					return true;
				}
			}
			category = null;
			return false;
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
			if (!DeclarationParticipation.Includes(definition, module))
				return false;
			if (IsApiHeaderDefinition(definition))
				return false;
			if (IsOutOfScopeStaticClassMember(definition))
				return false;
			if (definition is StaticClassDefinition staticClassDefinition)
				return ShouldEmitStaticClass(staticClassDefinition);

			return visibility switch
			{
				MetadataVisibility.None => false,
				MetadataVisibility.Export => definition.Export is not null,
				MetadataVisibility.Public => definition.Export is not null || definition.Public is not null,
				MetadataVisibility.All => true,
				_ => false
			};
		}

		bool ShouldEmitStaticClass(StaticClassDefinition definition)
		{
			if (visibility == MetadataVisibility.None)
				return false;
			if (visibility == MetadataVisibility.All)
				return true;
			return definition.Fields.Any(field => field.Modifier == FieldModifier.Static && ShouldEmitFieldMember(field, classFields: true))
				|| GetStaticClassFunctions(definition).Any(ShouldEmitFunctionMember);
		}

		bool ShouldWriteExtern(Definition definition)
		{
			if (!string.IsNullOrWhiteSpace(definition.Extern))
				return true;
			if (!IsExportApiView)
				return false;
			return definition switch
			{
				ClassDefinition classDefinition => classDefinition.Export is not null,
				FunctionDefinition function => function.Export is not null,
				_ => false
			};
		}

		bool IsOutOfScopeStaticClassMember(Definition definition)
		{
			return definition switch
			{
				FunctionDefinition function when function.OutOfScopeOwnerName is not null => ActiveDefinitions().OfType<StaticClassDefinition>().Any(staticClass => staticClass.Name == function.OutOfScopeOwnerName),
				VariableDefinition variable when variable.OutOfScopeOwnerName is not null => ActiveDefinitions().OfType<StaticClassDefinition>().Any(staticClass => staticClass.Name == variable.OutOfScopeOwnerName),
				_ => false
			};
		}

		bool IsApiHeaderDefinition(Definition definition)
		{
			return compilation.DefinitionOwners.TryGetValue(definition, out SourceFile? owner) && owner.IsApiHeader;
		}

		bool ShouldWriteShadowMarker(ClassDefinition definition)
		{
			if (!definition.IsShadow)
				return false;
			if (!IsExportApiView)
				return true;
			return HasExportedShadowHook(definition, "@getshadow") && HasExportedShadowHook(definition, "@setshadow");
		}

		bool HasExportedShadowHook(ClassDefinition definition, string attributeName)
		{
			foreach (ClassDefinition candidate in EnumerateClassAndBases(definition))
				foreach (FunctionDefinition function in candidate.Functions)
					if (function.Export is not null && HasAttribute(function.Attributes, attributeName))
						return true;
			ClassDefinition? receiverClass = GetDirectBaseClass(definition);
			foreach (FunctionDefinition function in ActiveDefinitions().OfType<FunctionDefinition>())
				if (function.Export is not null && HasAttribute(function.Attributes, attributeName) && HookReceiverMatches(function, receiverClass))
					return true;
			return false;
		}

		static bool HookReceiverMatches(FunctionDefinition function, ClassDefinition? receiverClass)
		{
			if (receiverClass is null)
				return false;
			ThisParameterDefinition? receiver = GetExplicitThisParameter(function) ?? function.EffectiveThisParameter;
			return receiver is not null && BaseTypeName(receiver.ResolvedType ?? "") == receiverClass.Name;
		}

		static ThisParameterDefinition? GetExplicitThisParameter(FunctionDefinition function)
		{
			foreach (ParameterDefinition parameter in function.Parameters)
				if (parameter is ThisParameterDefinition thisParameter)
					return thisParameter;
			return null;
		}

		static string BaseTypeName(string type)
		{
			string name = type.Trim();
			name = name.Replace("const ", "", StringComparison.Ordinal)
				.Replace("escaped ", "", StringComparison.Ordinal)
				.Replace("scoped ", "", StringComparison.Ordinal)
				.Replace("unscoped ", "", StringComparison.Ordinal)
				.Trim();
			while (name.EndsWith("*", StringComparison.Ordinal))
				name = name[..^1].Trim();
			int genericStart = name.IndexOf('<');
			if (genericStart >= 0)
				name = name[..genericStart];
			int namespaceStart = name.LastIndexOf("::", StringComparison.Ordinal);
			if (namespaceStart >= 0)
				name = name[(namespaceStart + 2)..];
			return name;
		}

		static IEnumerable<ClassDefinition> EnumerateClassAndBases(ClassDefinition definition)
		{
			for (ClassDefinition? current = definition; current is not null; current = GetDirectBaseClass(current))
				yield return current;
		}

		static ClassDefinition? GetDirectBaseClass(ClassDefinition definition)
		{
			foreach (TypeReference baseType in definition.BaseTypes)
				if (baseType is TypeDefinitionReference { Definition: ClassDefinition baseClass })
					return baseClass;
			return null;
		}

		static bool HasAttribute(List<AttributeConstructor> attributes, string name)
		{
			foreach (AttributeConstructor attribute in attributes)
				if (AttributeNameEquals(attribute.Name, name))
					return true;
			return false;
		}

		static bool AttributeNameEquals(string actual, string expected)
		{
			return actual == expected || actual.TrimStart('@') == expected.TrimStart('@');
		}

		static bool IsGeneratedDefinition(Definition definition)
		{
			return definition.SourceSyntax is null;
		}

		bool ShouldEmitClassFields(ClassDefinition classDefinition)
		{
			return visibility switch
			{
				MetadataVisibility.Export => classDefinition.Fields.Any(static field => field.Modifier == FieldModifier.Static && field.Export is not null),
				MetadataVisibility.Public => classDefinition.Export is not null || classDefinition.Public is not null,
				MetadataVisibility.All => true,
				_ => false
			};
		}

		bool ShouldEmitFieldMember(FieldDefinition field, bool classFields)
		{
			if (!IsExportApiView)
				return true;
			if (classFields)
				return field.Modifier == FieldModifier.Static && field.Export is not null;
			return field.Modifier != FieldModifier.Static || field.Export is not null;
		}

		bool ShouldEmitFunctionMember(FunctionDefinition function)
		{
			if (!IsExportApiView)
				return true;
			if (function.Export is null)
				return false;
			if (IsGeneratedLifecycleDefinition(function))
				return false;
			if (IsGeneratedApiImplementationDetail(function))
				return false;
			if (IsOverriddenApiMethod(function))
				return false;
			return true;
		}

		bool ShouldWriteFunctionModifier(FunctionDefinition definition)
		{
			if (!IsExportApiView)
				return definition.Modifier != FunctionModifier.None;

			return definition.Modifier is not (FunctionModifier.None or FunctionModifier.Virtual or FunctionModifier.Abstract);
		}

		static bool IsGeneratedApiImplementationDetail(FunctionDefinition function)
		{
			return function.Provenance?.Category == GeneratedDeclarationCategory.VirtualDispatch
				|| function.GeneratedInfo?.Category == GeneratedDeclarationCategory.VirtualDispatch
				|| IsGeneratedConstructorLifecycleFunction(function)
				|| IsGeneratedVirtualImplementationFunction(function);
		}

		static bool IsGeneratedLifecycleDefinition(FunctionDefinition function)
		{
			return function.GeneratedInfo?.Category == GeneratedDeclarationCategory.Lifecycle
				|| function.Provenance?.Category == GeneratedDeclarationCategory.Lifecycle;
		}

		static bool IsGeneratedConstructorLifecycleFunction(FunctionDefinition function)
		{
			if (function.GeneratedInfo?.Category == GeneratedDeclarationCategory.Iterator
				|| function.Provenance?.Category == GeneratedDeclarationCategory.Iterator)
				return function.Name is "op_initnew" or "create" or "op_delete";
			return function.Name is "op_initnew" or "create" or "op_delete" or "destroy";
		}

		static bool IsGeneratedVirtualImplementationFunction(FunctionDefinition function)
		{
			return function.Name.StartsWith("_", StringComparison.Ordinal) && function.Symbol.Contains("__", StringComparison.Ordinal);
		}

		static bool IsOverriddenApiMethod(FunctionDefinition function)
		{
			return function.Modifier is FunctionModifier.Override or FunctionModifier.Sealed;
		}

		static bool ShouldWriteSyntheticApiConstructor(ClassDefinition definition, IReadOnlyList<FunctionDefinition> functions)
		{
			if (definition.Export is null
				|| definition.Extern is not null
				|| definition.Modifier == ClassModifier.Abstract)
			{
				return false;
			}

			foreach (FunctionDefinition function in functions)
			{
				if (!IsGeneratedLifecycleDefinition(function) && function.Modifier == FunctionModifier.Constructor && function.Export is not null)
					return false;
			}

			return true;
		}

		static bool ShouldWriteSyntheticApiDestructor(ClassDefinition definition, IReadOnlyList<FunctionDefinition> functions)
		{
			if (definition.Export is null
				|| definition.Extern is not null
				|| definition.Modifier == ClassModifier.Abstract)
			{
				return false;
			}

			foreach (FunctionDefinition function in functions)
			{
				if (!IsGeneratedLifecycleDefinition(function) && function.Modifier == FunctionModifier.Destructor && function.Export is not null)
					return false;
			}

			return true;
		}

		List<InterfaceDefinition> GetApiInterfaceAccessors(ClassDefinition definition)
		{
			List<InterfaceDefinition> interfaces = [];
			foreach (TypeReference baseType in definition.BaseTypes)
			{
				if (TryGetInterfaceDefinition(baseType, out InterfaceDefinition? interfaceDefinition)
					&& interfaceDefinition is not null
					&& interfaceDefinition.Export is not null)
				{
					interfaces.Add(interfaceDefinition);
				}
			}
			return interfaces;
		}

		static List<TypeReference> GetApiBaseTypes(ClassDefinition definition)
		{
			List<TypeReference> baseTypes = definition.HasExportProjectionBaseFilter
				? definition.ExportProjectionBaseTypes
				: definition.BaseTypes;
			List<TypeReference> loweredInterfaceBaseTypes = definition.HasExportProjectionInterfaceFilter
				? definition.ExportProjectionInterfaceBaseTypes
				: definition.LoweredInterfaceBaseTypes;
			if (loweredInterfaceBaseTypes.Count == 0)
				return baseTypes;

			List<TypeReference> types = [];
			foreach (TypeReference baseType in baseTypes)
				if (!definition.HasExportProjectionInterfaceFilter || !IsInterfaceType(baseType))
					types.Add(baseType);
			types.AddRange(loweredInterfaceBaseTypes);
			return types;
		}

		static bool IsInterfaceType(TypeReference type)
		{
			return type is TypeDefinitionReference { Definition: InterfaceDefinition };
		}

		static bool IsExportInterfaceVisible(ClassDefinition definition, string interfaceName)
		{
			if (!definition.HasExportProjectionInterfaceFilter)
				return true;
			return definition.ExportProjectionInterfaceBaseTypes.Any(type => type.ResolvedType == interfaceName);
		}

		string GetTopLevelId(Definition definition)
		{
			string? namespaceName = GetEffectiveMetadataNamespace(definition);
			string prefix = string.IsNullOrWhiteSpace(namespaceName) ? "" : namespaceName + "::";
			return GetKind(definition) + ":" + prefix + GetMetadataIdName(definition);
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
				StaticClassDefinition => "staticClass",
				StructDefinition { SourceInterface: not null } => "interface",
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
				NameOfParameterDefinition => "typenameof-parameter",
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
				&& (typeDefinition is InterfaceDefinition || typeDefinition is StructDefinition { SourceInterface: not null }))
			{
				definition = typeDefinition is InterfaceDefinition found
					? found
					: ((StructDefinition)typeDefinition).SourceInterface;
				return true;
			}
			if (!string.IsNullOrWhiteSpace(type.ResolvedType)
				&& typeDefinitions.TryGetValue(type.ResolvedType, out TypeDefinition? resolvedDefinition)
				&& (resolvedDefinition is InterfaceDefinition || resolvedDefinition is StructDefinition { SourceInterface: not null }))
			{
				definition = resolvedDefinition is InterfaceDefinition resolvedInterface
					? resolvedInterface
					: ((StructDefinition)resolvedDefinition).SourceInterface;
				return true;
			}
			definition = null;
			return false;
		}

		VariableDefinition? FindInterfaceVTable(TypeDefinition owner, string interfaceName)
		{
			string ownerSymbol = SymbolName(owner);
			string interfaceSymbol = typeDefinitions.TryGetValue(interfaceName, out TypeDefinition? interfaceDefinition)
				? SymbolName(interfaceDefinition)
				: interfaceName;
			string symbol = ownerSymbol + "_" + interfaceSymbol;
			return ActiveDefinitions().OfType<VariableDefinition>().FirstOrDefault(variable => variable.Symbol == symbol || variable.Name == symbol);
		}

		IEnumerable<VariableDefinition> FindInterfaceVTables(TypeDefinition owner)
		{
			string prefix = SymbolName(owner) + "_";
			HashSet<string> storageSymbols = ActiveDefinitions()
				.OfType<VariableDefinition>()
				.Where(static variable => variable.Symbol.EndsWith("__storage", StringComparison.Ordinal))
				.Select(static variable => variable.Symbol)
				.ToHashSet(StringComparer.Ordinal);
			foreach (VariableDefinition variable in ActiveDefinitions().OfType<VariableDefinition>())
			{
				if (!variable.Symbol.StartsWith(prefix, StringComparison.Ordinal)
					|| variable.Symbol.EndsWith("__storage", StringComparison.Ordinal)
					|| !storageSymbols.Contains(variable.Symbol + "__storage"))
					continue;
				string interfaceSymbol = variable.Symbol[prefix.Length..];
				if (interfaceSymbol.Length == 0 || interfaceSymbol.Contains("__", StringComparison.Ordinal))
					continue;
				string interfaceName = ResolveTypeNameFromSymbol(interfaceSymbol) ?? interfaceSymbol;
				yield return variable;
			}
		}

		string? ResolveTypeNameFromSymbol(string symbol)
		{
			foreach (TypeDefinition type in typeDefinitions.Values)
				if (SymbolName(type) == symbol)
					return type.Name;
			return null;
		}

		static string SymbolName(Definition definition)
		{
			return string.IsNullOrWhiteSpace(definition.Symbol) ? definition.Name : definition.Symbol;
		}

		static TypeReference UnwrapTypeReference(TypeReference type)
		{
			while (true)
			{
				type = type switch
				{
					ConstTypeReference { Type: not null } constant => constant.Type,
					ConstOfTypeReference { Type: not null } constOf => constOf.Type,
					VolatileTypeReference { Type: not null } vol => vol.Type,
					EscapedTypeReference { Type: not null } escaped => escaped.Type,
					ScopedTypeReference { Type: not null } scoped => scoped.Type,
					UnscopedTypeReference { Type: not null } unscoped => unscoped.Type,
					_ => type
				};
				if (type is not ConstTypeReference and not ConstOfTypeReference and not VolatileTypeReference and not EscapedTypeReference and not ScopedTypeReference and not UnscopedTypeReference)
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
			if (definition.Internal is not null)
				return "internal";
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
			if (function.Parameters.Any(static parameter => parameter.Modifier == ParameterModifier.Prep))
				return false;

			if (!isTypeScoped && !function.Parameters.OfType<ThisParameterDefinition>().Any())
				return false;

			string callableName = SymbolNameService.CallableName(function).Value;
			if (callableName.StartsWith("get", StringComparison.Ordinal) && function.ResolvedType != "void" && function.IteratorKind == IteratorKind.None)
			{
				accessor = "get";
				propertyName = GetPropertyName(callableName, "get", function.IsAsync);
				indexer = propertyName.Length == 0;
				indexParams.AddRange(GetPropertyParameterNames(function.Parameters));
				return callableName.Length >= "get".Length;
			}

			if (callableName.StartsWith("set", StringComparison.Ordinal) && function.IteratorKind == IteratorKind.None)
			{
				List<string> ordinaryParameters = GetPropertyParameterNames(function.Parameters);
				if (ordinaryParameters.Count == 0)
					return false;

				accessor = "set";
				propertyName = GetPropertyName(callableName, "set", function.IsAsync);
				indexer = propertyName.Length == 0;
				valueParam = ordinaryParameters[^1];
				indexParams.AddRange(ordinaryParameters.Take(ordinaryParameters.Count - 1));
				return callableName.Length >= "set".Length;
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
				if (parameter is ThisParameterDefinition or WithinParameterDefinition or SizeOfParameterDefinition or NameOfParameterDefinition or VTableOfParameterDefinition)
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
			return NumericLiteralParser.TryParseIntegerConstant(literal.Text, out value);
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
			return SymbolNameService.IsSameSymbol(definition);
		}

		static bool FunctionHasOverloadSelector(FunctionDefinition function)
		{
			foreach (ParameterDefinition parameter in function.Parameters)
				if (parameter.IsOverloadSelector)
					return true;
			return false;
		}

		string GetMetadataName(Definition definition)
		{
			string name = string.IsNullOrWhiteSpace(definition.Name) ? definition.Symbol : definition.Name;
			if (definition.OutOfScopeOwnerName == currentStaticClassName)
				return name;
			return string.IsNullOrWhiteSpace(definition.OutOfScopeOwnerName) ? name : definition.OutOfScopeOwnerName + "." + name;
		}

		string GetMetadataIdName(Definition definition)
		{
			if (definition is FunctionDefinition function && FunctionHasOverloadSelector(function))
			{
				string name = SymbolNameService.CallableName(function).Value;
				if (function.OutOfScopeOwnerName == currentStaticClassName)
					return name;
				return string.IsNullOrWhiteSpace(function.OutOfScopeOwnerName) ? name : function.OutOfScopeOwnerName + "." + name;
			}
			return GetMetadataName(definition);
		}

		static string GetVisibleFunctionName(FunctionDefinition function)
		{
			if (function.Modifier == FunctionModifier.Constructor)
				return "create";
			if (function.Modifier == FunctionModifier.Destructor || function.Name.StartsWith("~", StringComparison.Ordinal))
				return "destroy";
			return SymbolNameService.CallableName(function).Value.TrimStart('~');
		}

		string GetQualifiedFunctionName(FunctionDefinition function, string name)
		{
			string prefix = "";
			if (function.OutOfScopeOwnerName is not null)
				prefix = function.OutOfScopeOwnerName + ".";
			else if (FindContainingType(function) is TypeDefinition containingType)
				prefix = containingType.Name + ".";
			else if (FindContainingStaticClass(function) is StaticClassDefinition staticClassDefinition)
				prefix = staticClassDefinition.Name + ".";

			string? namespaceName = function.Namespace;
			if (string.IsNullOrWhiteSpace(namespaceName)
				&& !function.NamespaceAssigned
				&& module.DefinitionSources.TryGetValue(function, out TokenSequence? source)
				&& source is not null
				&& module.SourceNamespaces.TryGetValue(source, out string? sourceNamespace))
				namespaceName = sourceNamespace;

			return (string.IsNullOrWhiteSpace(namespaceName) ? "" : namespaceName + "::") + prefix + name;
		}

		string GetQualifiedDefinitionName(Definition definition)
		{
			string? namespaceName = GetEffectiveMetadataNamespace(definition);
			return string.IsNullOrWhiteSpace(namespaceName) ? definition.Name : namespaceName + "::" + definition.Name;
		}

		string? GetEffectiveMetadataNamespace(Definition definition)
		{
			if (!string.IsNullOrWhiteSpace(definition.Namespace) || definition.NamespaceAssigned)
				return definition.Namespace;
			return module.Namespace;
		}

		TypeDefinition? FindContainingType(FunctionDefinition function)
		{
			foreach (Definition definition in ActiveDefinitions())
			{
				if (definition is TypeDefinition typeDefinition && TypeContainsFunction(typeDefinition, function))
					return typeDefinition;
			}
			return null;
		}

		StaticClassDefinition? FindContainingStaticClass(FunctionDefinition function)
		{
			foreach (Definition definition in ActiveDefinitions())
			{
				if (definition is StaticClassDefinition staticClassDefinition && staticClassDefinition.Functions.Contains(function))
					return staticClassDefinition;
			}
			return null;
		}

		static bool TypeContainsFunction(TypeDefinition type, FunctionDefinition function)
		{
			return type switch
			{
				ClassDefinition classDefinition => classDefinition.Functions.Contains(function),
				StructDefinition structDefinition => structDefinition.Functions.Contains(function),
				InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions.Contains(function),
				EnumDefinition enumDefinition => enumDefinition.Functions.Contains(function),
				NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions.Contains(function),
				ParamsDefinition paramsDefinition => paramsDefinition.Functions.Contains(function),
				_ => false
			};
		}

		IEnumerable<Definition> ActiveDefinitions()
		{
			return DeclarationParticipation.ActiveTopLevelDefinitions(module);
		}

		bool MetadataAllocatorTypeAvailable()
		{
			foreach (Definition definition in ActiveDefinitions())
				if (definition is TypeDefinition { Name: "Allocator" })
					return true;
			return false;
		}

		bool TryGetSourceLocation(Definition definition, out string? sourcefile, out int sourceline)
		{
			sourcefile = null;
			sourceline = 0;
			if (!TryGetDefinitionSourceRange(definition, out TokenRange range))
				return false;
			if (!module.SourceFiles.TryGetValue(range.Sequence, out SourceFile? file))
				return false;

			string physicalPath = string.IsNullOrWhiteSpace(file.FullPath) ? file.Path : file.FullPath!;
			sourcefilePathMapper ??= new SourcefilePathMapper(module.SourcefilePathMode, module.SourcefileDefaultRoot, module.SourcefileRoots);
			SourcefilePathMapResult mapResult = sourcefilePathMapper.Map(physicalPath);
			sourcefile = mapResult.Success ? mapResult.Value : file.Path;
			sourceline = range.StartLineNumber;
			return true;
		}

		static bool TryGetDefinitionSourceRange(Definition definition, out TokenRange range)
		{
			range = default;
			switch (definition.SourceSyntax)
			{
				case AliasDeclarationSyntax syntax:
					return Assign(syntax.Identifier?.Range, out range) || TryGetSyntaxRange(syntax, out range);
				case TypeDeclarationSyntax syntax:
					return Assign(syntax.Identifier?.Range, out range) || TryGetSyntaxRange(syntax, out range);
				case MemberDeclarationSyntax syntax:
					return Assign(syntax.Identifier?.Range, out range) || TryGetSyntaxRange(syntax, out range);
				case EnumValueSyntax syntax:
					return Assign(syntax.Identifier?.Range, out range) || TryGetSyntaxRange(syntax, out range);
				default:
					return TryGetSyntaxRange(definition.SourceSyntax, out range);
			}
		}

		static bool TryGetSyntaxRange(SyntaxNode? syntax, out TokenRange range)
		{
			return SyntaxNodeTraversal.TryGetRange(syntax, out range);
		}

		static bool Assign(TokenRange? value, out TokenRange range)
		{
			if (value is TokenRange tokenRange)
			{
				range = tokenRange;
				return true;
			}
			range = default;
			return false;
		}

		static bool TryGetAttributeStringContent(IReadOnlyList<AttributeConstructor> attributes, string name, out string? content)
		{
			content = GetAttributeStringContent(attributes, name);
			return attributes.Any(attribute => AttributeNameEquals(attribute.Name, name));
		}

		static string? GetAttributeStringContent(IReadOnlyList<AttributeConstructor> attributes, string name)
		{
			foreach (AttributeConstructor attribute in attributes)
			{
				if (!AttributeNameEquals(attribute.Name, name))
					continue;
				ArgumentExpression? argument = attribute.Arguments.FirstOrDefault(static argument => string.IsNullOrWhiteSpace(argument.Name));
				return argument?.Value is LiteralExpression { Kind: LiteralKind.String } literal ? GetLiteralString(literal) : null;
			}
			return null;
		}

		static string GetLiteralString(LiteralExpression literal)
		{
			if (literal.Value is string text)
				return text;
			string source = literal.Text;
			if (source.Length >= 2 && source[0] == '"' && source[^1] == '"')
				return source[1..^1];
			return source;
		}

		bool IsVoidReturn(FunctionDefinition function)
		{
			return FormatType(function.ReturnType, function.ResolvedType) == "void";
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
