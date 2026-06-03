using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Camp.Compiler;

public sealed class CEmissionOptions
{
	public required string OutputDirectory { get; init; }
	public required string ProjectName { get; init; }
	public string EmitKind { get; init; } = "c99";
}

public sealed class CEmissionResult
{
	public List<string> GeneratedFiles { get; } = [];
	public List<string> Diagnostics { get; } = [];
	public bool Success => Diagnostics.Count == 0;
}

public static class CCodeEmitter
{
	static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
	static readonly HashSet<string> InvalidResolvedTypes = new(StringComparer.Ordinal)
	{
		"#ERROR",
		"#MISSING",
		"#UNRESOLVED"
	};

	public static CEmissionResult Emit(Compilation compilation, CEmissionOptions options)
	{
		ArgumentNullException.ThrowIfNull(compilation);
		ArgumentNullException.ThrowIfNull(options);

		CEmissionResult result = new();
		if (!ValidateEmissionKind(options.EmitKind, result))
			return result;
		if (!ValidateLoweredTree(compilation, result))
			return result;

		try
		{
			Directory.CreateDirectory(options.OutputDirectory);
			CDeclarationWriter declarations = new(compilation);
			EmitPrivateHeader(compilation, options, result, declarations);
			foreach (SourceFile file in compilation.Files)
			{
				if (file.IsApiHeader)
				{
					if (HasExportedDeclarations(compilation, file))
						EmitPublicHeader(compilation, options, file, result, declarations);
					continue;
				}

				EmitSourceFile(compilation, options, file, result, declarations);
				if (HasExportedDeclarations(compilation, file))
					EmitPublicHeader(compilation, options, file, result, declarations);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			result.Diagnostics.Add(ex.Message);
		}

		return result;
	}

	static bool ValidateEmissionKind(string emitKind, CEmissionResult result)
	{
		if (emitKind == "c99")
			return true;

		result.Diagnostics.Add($"Emit target '{emitKind}' is not supported. Expected c99.");
		return false;
	}

	static bool ValidateLoweredTree(Compilation compilation, CEmissionResult result)
	{
		if (compilation.SharedModule is null)
		{
			result.Diagnostics.Add("C emission requires a lowered bindable tree.");
			return false;
		}

		HashSet<BindableNode> visited = [];
		foreach (BindableNode node in EnumerateNodes(compilation.SharedModule, visited))
		{
			if (node is LiteralExpression)
				continue;
			if (node.ResolvedType is string resolvedType && InvalidResolvedTypes.Contains(resolvedType))
			{
				result.Diagnostics.Add($"C emission aborted because {node.GetType().Name} has unresolved type '{resolvedType}'.");
				return false;
			}
		}

		return true;
	}

	static void EmitPrivateHeader(Compilation compilation, CEmissionOptions options, CEmissionResult result, CDeclarationWriter declarations)
	{
		string filename = Path.Combine(options.OutputDirectory, options.ProjectName + "_private.h");
		using StreamWriter writer = new(filename, append: false, Utf8NoBom);
		string guard = BuildHeaderGuard(options.ProjectName + "_private_h");

		writer.WriteLine("#ifndef " + guard);
		writer.WriteLine("#define " + guard);
		writer.WriteLine();
		writer.WriteLine("#include <stddef.h>");
		foreach (string include in compilation.Target?.Includes ?? [])
		{
			if (include == "stddef.h")
				continue;
			writer.WriteLine("#include <" + include + ">");
		}

		foreach (SourceFile file in compilation.Files.Where(static file => file.IsApiHeader))
		{
			if (HasExportedDeclarations(compilation, file))
				writer.WriteLine("#include \"" + GetHeaderFilename(file) + "\"");
		}

		writer.WriteLine();
		declarations.WritePrivateHeaderDeclarations(writer);
		writer.WriteLine();
		writer.WriteLine("#endif");
		result.GeneratedFiles.Add(filename);
	}

	static void EmitSourceFile(Compilation compilation, CEmissionOptions options, SourceFile file, CEmissionResult result, CDeclarationWriter declarations)
	{
		string filename = Path.Combine(options.OutputDirectory, GetCSourceFilename(file));
		using StreamWriter writer = new(filename, append: false, Utf8NoBom);
		writer.WriteLine("#include \"" + options.ProjectName + "_private.h\"");
		if (HasExportedDeclarations(compilation, file))
			writer.WriteLine("#include \"" + GetHeaderFilename(file) + "\"");
		writer.WriteLine();
		declarations.WriteSourceFileForwardDeclarations(writer, file);
		writer.WriteLine();
		writer.WriteLine("/* Function and object definitions will be emitted in a later C emission stage. */");
		result.GeneratedFiles.Add(filename);
	}

	static void EmitPublicHeader(Compilation compilation, CEmissionOptions options, SourceFile file, CEmissionResult result, CDeclarationWriter declarations)
	{
		string filename = Path.Combine(options.OutputDirectory, GetHeaderFilename(file));
		using StreamWriter writer = new(filename, append: false, Utf8NoBom);
		string guard = BuildHeaderGuard(Path.GetFileNameWithoutExtension(GetHeaderFilename(file)) + "_h");

		writer.WriteLine("#ifndef " + guard);
		writer.WriteLine("#define " + guard);
		writer.WriteLine();
		if (!file.IsApiHeader)
			writer.WriteLine("#include \"" + options.ProjectName + "_private.h\"");
		else
			WriteTargetIncludes(writer, compilation);
		writer.WriteLine();
		declarations.WritePublicHeaderDeclarations(writer, file);
		writer.WriteLine();
		writer.WriteLine("#endif");
		result.GeneratedFiles.Add(filename);
	}

	static void WriteTargetIncludes(TextWriter writer, Compilation compilation)
	{
		writer.WriteLine("#include <stddef.h>");
		foreach (string include in compilation.Target?.Includes ?? [])
		{
			if (include == "stddef.h")
				continue;
			writer.WriteLine("#include <" + include + ">");
		}
	}

	static bool HasExportedDeclarations(Compilation compilation, SourceFile file)
	{
		foreach (Definition definition in compilation.SharedModule?.Definitions ?? [])
		{
			if (!compilation.DefinitionOwners.TryGetValue(definition, out SourceFile? owner) || !ReferenceEquals(owner, file))
				continue;
			if (definition.Export is not null)
				return true;
			if (definition is TypeDefinition typeDefinition && TypeHasExportedCallable(typeDefinition))
				return true;
		}

		return false;
	}

	static bool TypeHasExportedCallable(TypeDefinition typeDefinition)
	{
		return typeDefinition switch
		{
			ClassDefinition classDefinition => classDefinition.Functions.Any(static function => function.Export is not null),
			StructDefinition structDefinition => structDefinition.Functions.Any(static function => function.Export is not null),
			InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions.Any(static function => function.Export is not null),
			EnumDefinition enumDefinition => enumDefinition.Functions.Any(static function => function.Export is not null),
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions.Any(static function => function.Export is not null),
			ParamsDefinition paramsDefinition => paramsDefinition.Functions.Any(static function => function.Export is not null),
			_ => false
		};
	}

	static IEnumerable<BindableNode> EnumerateNodes(BindableNode root, HashSet<BindableNode> visited)
	{
		if (!visited.Add(root))
			yield break;

		yield return root;
		foreach (PropertyInfo property in root.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.Name is nameof(BindableNode.SourceSyntax) or nameof(Module.DefinitionSources))
				continue;

			object? value = property.GetValue(root);
			if (value is BindableNode child)
			{
				foreach (BindableNode node in EnumerateNodes(child, visited))
					yield return node;
			}
			else if (value is IEnumerable enumerable && value is not string)
			{
				foreach (object? item in enumerable)
				{
					if (item is not BindableNode listChild)
						continue;
					foreach (BindableNode node in EnumerateNodes(listChild, visited))
						yield return node;
				}
			}
		}
	}

	public static string GetProjectName(IReadOnlyList<SourceFile> files)
	{
		SourceFile? first = files.FirstOrDefault(static file => !file.IsApiHeader) ?? files.FirstOrDefault();
		return first is null || first.Path == "-"
			? "stdin"
			: SanitizeIdentifier(Path.GetFileNameWithoutExtension(first.Path));
	}

	public static string GetDefaultOutputDirectory(IReadOnlyList<SourceFile> files)
	{
		SourceFile? first = files.FirstOrDefault(static file => !file.IsApiHeader) ?? files.FirstOrDefault();
		string? directory = first is null || first.Path == "-" ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(first.Path);
		return Path.Combine(string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : directory, "build");
	}

	static string GetCSourceFilename(SourceFile file)
	{
		return SanitizeIdentifier(Path.GetFileNameWithoutExtension(file.Path == "-" ? "stdin" : file.Path)) + ".c";
	}

	static string GetHeaderFilename(SourceFile file)
	{
		return SanitizeIdentifier(Path.GetFileNameWithoutExtension(file.Path == "-" ? "stdin" : file.Path)) + ".h";
	}

	static string BuildHeaderGuard(string name)
	{
		return SanitizeIdentifier(name).ToUpper(CultureInfo.InvariantCulture) + "_";
	}

	static string SanitizeIdentifier(string value)
	{
		StringBuilder builder = new();
		foreach (char ch in value)
			builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
		return builder.Length == 0 ? "camp" : builder.ToString();
	}

	sealed class CDeclarationWriter(Compilation compilation)
	{
		readonly HashSet<string> emittedNames = new(StringComparer.Ordinal);
		readonly Dictionary<FunctionDefinition, TypeDefinition> containingTypes = BuildContainingTypeMap(compilation);

		public void WritePrivateHeaderDeclarations(TextWriter writer)
		{
			emittedNames.Clear();
			List<Definition> definitions = GetDefinitions().ToList();

			WriteSection(writer, "Forward declarations", () =>
			{
				foreach (TypeDefinition type in definitions.OfType<TypeDefinition>())
					WriteTypeForwardDeclaration(writer, type);
			});

			WriteSection(writer, "Newtypes", () =>
			{
				foreach (NewtypeDefinition newtype in definitions.OfType<NewtypeDefinition>())
					WriteNewtypeDefinition(writer, newtype, exportedOnly: false);
			});

			WriteSection(writer, "Enums", () =>
			{
				foreach (EnumDefinition enumDefinition in definitions.OfType<EnumDefinition>())
					WriteEnumDefinition(writer, enumDefinition);
			});

			WriteSection(writer, "Layouts", () =>
			{
				foreach (TypeDefinition type in definitions.OfType<TypeDefinition>())
					WriteLayoutDefinition(writer, type);
			});

			WriteSection(writer, "Function declarations", () =>
			{
				foreach (FunctionDefinition function in GetAllFunctions(definitions).Where(static function => function.Export is not null))
					WriteFunctionPrototype(writer, function, storage: null);
			});

			WriteSection(writer, "Object declarations", () =>
			{
				foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>().Where(static variable => variable.Export is not null))
					WriteVariableDeclaration(writer, variable, storage: "extern");
			});
		}

		public void WriteSourceFileForwardDeclarations(TextWriter writer, SourceFile file)
		{
			emittedNames.Clear();
			List<Definition> definitions = GetOwnedDefinitions(file).ToList();
			List<FunctionDefinition> privateFunctions = GetAllFunctions(definitions).Where(static function => function.Export is null).ToList();
			List<VariableDefinition> privateVariables = definitions.OfType<VariableDefinition>().Where(static variable => variable.Export is null).ToList();

			if (privateFunctions.Count == 0 && privateVariables.Count == 0)
				return;

			writer.WriteLine("/* Private file declarations. */");
			foreach (FunctionDefinition function in privateFunctions)
				WriteFunctionPrototype(writer, function, storage: "static");
			foreach (VariableDefinition variable in privateVariables)
				WriteVariableDeclaration(writer, variable, storage: "static");
		}

		public void WritePublicHeaderDeclarations(TextWriter writer, SourceFile file)
		{
			emittedNames.Clear();
			List<Definition> definitions = GetOwnedDefinitions(file).ToList();
			bool wrote = false;

			if (file.IsApiHeader)
			{
				foreach (TypeDefinition type in definitions.OfType<TypeDefinition>().Where(static type => type.Export is not null))
				{
					switch (type)
					{
						case ClassDefinition:
						case InterfaceDefinition:
							WriteTypeForwardDeclaration(writer, type);
							wrote = true;
							break;
						case NewtypeDefinition newtype:
							WriteNewtypeDefinition(writer, newtype, exportedOnly: true);
							wrote = true;
							break;
						case EnumDefinition enumDefinition:
							WriteEnumDefinition(writer, enumDefinition);
							wrote = true;
							break;
						case StructDefinition structDefinition:
							WriteFieldLayout(writer, structDefinition, structDefinition.Fields);
							wrote = true;
							break;
					}
				}
			}

			foreach (FunctionDefinition function in GetAllFunctions(definitions).Where(static function => function.Export is not null))
			{
				WriteFunctionPrototype(writer, function, storage: null);
				wrote = true;
			}

			foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>().Where(static variable => variable.Export is not null))
			{
				WriteVariableDeclaration(writer, variable, storage: "extern");
				wrote = true;
			}

			if (!wrote)
				writer.WriteLine("/* No exported declarations. */");
		}

		IEnumerable<Definition> GetDefinitions()
		{
			return compilation.SharedModule?.Definitions ?? [];
		}

		IEnumerable<Definition> GetOwnedDefinitions(SourceFile file)
		{
			foreach (Definition definition in GetDefinitions())
			{
				if (compilation.DefinitionOwners.TryGetValue(definition, out SourceFile? owner) && ReferenceEquals(owner, file))
					yield return definition;
			}
		}

		static IEnumerable<FunctionDefinition> GetAllFunctions(IEnumerable<Definition> definitions)
		{
			foreach (Definition definition in definitions)
			{
				switch (definition)
				{
					case FunctionDefinition function:
						yield return function;
						break;
					case ClassDefinition classDefinition:
						foreach (FunctionDefinition function in classDefinition.Functions)
							yield return function;
						break;
					case StructDefinition structDefinition:
						foreach (FunctionDefinition function in structDefinition.Functions)
							yield return function;
						break;
					case InterfaceDefinition interfaceDefinition:
						foreach (FunctionDefinition function in interfaceDefinition.Functions)
							yield return function;
						break;
					case EnumDefinition enumDefinition:
						foreach (FunctionDefinition function in enumDefinition.Functions)
							yield return function;
						break;
					case NewtypeDefinition newtypeDefinition:
						foreach (FunctionDefinition function in newtypeDefinition.Functions)
							yield return function;
						break;
					case ParamsDefinition paramsDefinition:
						foreach (FunctionDefinition function in paramsDefinition.Functions)
							yield return function;
						break;
				}
			}
		}

		static Dictionary<FunctionDefinition, TypeDefinition> BuildContainingTypeMap(Compilation compilation)
		{
			Dictionary<FunctionDefinition, TypeDefinition> map = [];
			foreach (Definition definition in compilation.SharedModule?.Definitions ?? [])
			{
				switch (definition)
				{
					case ClassDefinition classDefinition:
						Add(classDefinition, classDefinition.Functions);
						break;
					case StructDefinition structDefinition:
						Add(structDefinition, structDefinition.Functions);
						break;
					case InterfaceDefinition interfaceDefinition:
						Add(interfaceDefinition, interfaceDefinition.Functions);
						break;
					case EnumDefinition enumDefinition:
						Add(enumDefinition, enumDefinition.Functions);
						break;
					case NewtypeDefinition newtypeDefinition:
						Add(newtypeDefinition, newtypeDefinition.Functions);
						break;
					case ParamsDefinition paramsDefinition:
						Add(paramsDefinition, paramsDefinition.Functions);
						break;
				}
			}
			return map;

			void Add(TypeDefinition type, List<FunctionDefinition> functions)
			{
				foreach (FunctionDefinition function in functions)
					map[function] = type;
			}
		}

		static void WriteSection(TextWriter writer, string name, Action write)
		{
			writer.WriteLine("/* " + name + ". */");
			write();
			writer.WriteLine();
		}

		void WriteTypeForwardDeclaration(TextWriter writer, TypeDefinition type)
		{
			if (type is EnumDefinition or NewtypeDefinition or ParamsDefinition)
				return;

			string name = CName(type);
			if (!emittedNames.Add("forward:" + name))
				return;
			writer.WriteLine("typedef struct " + name + " " + name + ";");
		}

		void WriteNewtypeDefinition(TextWriter writer, NewtypeDefinition definition, bool exportedOnly)
		{
			if (exportedOnly && definition.Export is null)
				return;
			if (!emittedNames.Add((exportedOnly ? "public-newtype:" : "newtype:") + CName(definition)))
				return;

			string name = CName(definition);
			if (definition.UnderlyingType is CallableTypeReference callable)
			{
				writer.WriteLine(FormatCallableTypedef(callable, name) + ";");
				return;
			}

			CType type = FormatType(definition.UnderlyingType, name);
			writer.WriteLine("typedef " + type.Declaration + ";");
		}

		void WriteEnumDefinition(TextWriter writer, EnumDefinition definition)
		{
			string name = CName(definition);
			if (!emittedNames.Add("enum:" + name))
				return;

			writer.WriteLine("typedef enum " + name);
			writer.WriteLine("{");
			for (int i = 0; i < definition.Values.Count; i++)
			{
				VariableDefinition value = definition.Values[i];
				writer.Write("\t" + CName(value));
				string? initializer = FormatConstantExpression(value.InitialValue);
				if (initializer is not null)
					writer.Write(" = " + initializer);
				if (i + 1 < definition.Values.Count)
					writer.Write(",");
				writer.WriteLine();
			}
			writer.WriteLine("} " + name + ";");
		}

		void WriteLayoutDefinition(TextWriter writer, TypeDefinition type)
		{
			switch (type)
			{
				case StructDefinition structDefinition:
					WriteFieldLayout(writer, structDefinition, structDefinition.Fields);
					break;
				case ClassDefinition classDefinition:
					WriteFieldLayout(writer, classDefinition, classDefinition.Fields);
					break;
				case InterfaceDefinition interfaceDefinition:
					WriteInterfaceLayout(writer, interfaceDefinition);
					break;
			}
		}

		void WriteFieldLayout(TextWriter writer, TypeDefinition type, List<FieldDefinition> fields)
		{
			string name = CName(type);
			if (!emittedNames.Add("layout:" + name))
				return;

			writer.WriteLine("struct " + name);
			writer.WriteLine("{");
			if (fields.Count == 0)
				writer.WriteLine("\tchar _camp_empty;");
			foreach (FieldDefinition field in fields.Where(static field => field.Modifier != FieldModifier.Static))
				writer.WriteLine("\t" + FormatType(field.Type, CName(field)).Declaration + ";");
			writer.WriteLine("};");
		}

		void WriteInterfaceLayout(TextWriter writer, InterfaceDefinition definition)
		{
			string name = CName(definition);
			if (!emittedNames.Add("layout:" + name))
				return;

			writer.WriteLine("struct " + name);
			writer.WriteLine("{");
			if (definition.Functions.Count == 0)
				writer.WriteLine("\tchar _camp_empty;");
			foreach (FunctionDefinition function in definition.Functions)
			{
				CallableTypeReference callable = new()
				{
					Kind = CallableKind.Function,
					CallSpec = function.CallSpec,
					ReturnType = function.ReturnType
				};
				foreach (ParameterDefinition parameter in function.Parameters)
					callable.Parameters.Add(parameter);
				writer.WriteLine("\t" + FormatCallableDeclarator(callable, CName(function)) + ";");
			}
			writer.WriteLine("};");
		}

		void WriteFunctionPrototype(TextWriter writer, FunctionDefinition function, string? storage)
		{
			if (function.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor)
				return;
			string name = CName(function);
			string key = "function:" + (storage ?? "extern") + ":" + name;
			if (!emittedNames.Add(key))
				return;

			string prefix = string.IsNullOrWhiteSpace(storage) ? "" : storage + " ";
			string callSpec = FormatCallSpec(function.CallSpec);
			if (callSpec.Length > 0)
				callSpec += " ";
			writer.WriteLine(prefix + FormatType(function.ReturnType, callSpec + name).Declaration + FormatParameters(function) + ";");
		}

		void WriteVariableDeclaration(TextWriter writer, VariableDefinition variable, string? storage)
		{
			string prefix = string.IsNullOrWhiteSpace(storage) ? "" : storage + " ";
			writer.WriteLine(prefix + FormatType(variable.Type, CName(variable)).Declaration + ";");
		}

		string FormatParameters(List<ParameterDefinition> parameters)
		{
			List<string> parts = [];
			foreach (ParameterDefinition parameter in parameters)
			{
				if (parameter is WithinParameterDefinition && parameter.Type is null)
					continue;
				string name = CName(parameter);
				if (parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown)
				{
					TypeReference? parameterType = parameter.Type;
					parts.Add(FormatType(new PointerTypeReference { ElementType = parameterType }, name).Declaration);
				}
				else
				{
					parts.Add(FormatType(parameter.Type, name).Declaration);
				}
			}
			return "(" + (parts.Count == 0 ? "void" : string.Join(", ", parts)) + ")";
		}

		string FormatParameters(FunctionDefinition function)
		{
			List<string> parts = [];
			if (RequiresImplicitThisParameter(function) && containingTypes.TryGetValue(function, out TypeDefinition? type))
				parts.Add(FormatType(new PointerTypeReference { ElementType = new TypeDefinitionReference { Definition = type, Name = type.Name } }, "this").Declaration);
			foreach (ParameterDefinition parameter in function.Parameters)
			{
				if (parameter is WithinParameterDefinition && parameter.Type is null)
					continue;
				string name = CName(parameter);
				if (parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown)
				{
					TypeReference? parameterType = parameter.Type;
					parts.Add(FormatType(new PointerTypeReference { ElementType = parameterType }, name).Declaration);
				}
				else
				{
					parts.Add(FormatType(parameter.Type, name).Declaration);
				}
			}
			return "(" + (parts.Count == 0 ? "void" : string.Join(", ", parts)) + ")";
		}

		bool RequiresImplicitThisParameter(FunctionDefinition function)
		{
			if (!containingTypes.ContainsKey(function))
				return false;
			if (function.Modifier is FunctionModifier.Static or FunctionModifier.Constructor or FunctionModifier.Destructor)
				return false;
			return function.Parameters.Count == 0 || function.Parameters[0].Symbol != "this";
		}

		string FormatCallableTypedef(CallableTypeReference callable, string name)
		{
			return "typedef " + FormatCallableDeclarator(callable, name);
		}

		string FormatCallableDeclarator(CallableTypeReference callable, string name)
		{
			string callSpec = FormatCallSpec(callable.CallSpec);
			string targetSpec = FormatTypeSpec(callable.TargetSpec);
			string pointer = "*";
			if (targetSpec.Length > 0)
				pointer += " " + targetSpec;
			if (callSpec.Length > 0)
				pointer += " " + callSpec;
			string declarator = "(" + pointer + " " + name + ")";
			return FormatType(callable.ReturnType, declarator).Declaration + FormatParameters(callable.Parameters);
		}

		CType FormatType(TypeReference? type, string declarator)
		{
			return type switch
			{
				null => new CType("void " + declarator),
				AttributedTypeReference attributed => FormatType(attributed.Type, declarator),
				ConstTypeReference constant => FormatQualifiedType("const", constant.Type, declarator),
				VolatileTypeReference vol => FormatQualifiedType("volatile", vol.Type, declarator),
				EscapedTypeReference escaped => FormatType(escaped.Type, declarator),
				ScopedTypeReference scoped => FormatType(scoped.Type, declarator),
				UnscopedTypeReference unscoped => FormatType(unscoped.Type, declarator),
				TargetTypeSpecTypeReference targetSpec => FormatTargetSpecType(targetSpec, declarator),
				PointerTypeReference pointer => FormatPointerType(pointer, declarator),
				ArrayTypeReference array => FormatType(array.ElementType, "*" + declarator),
				OptionalTypeReference optional => FormatType(optional.ElementType, declarator),
				CallableTypeReference callable => new CType(FormatCallableDeclarator(callable, declarator)),
				PrimitiveTypeReference primitive => new CType(FormatPrimitive(primitive.Type) + " " + declarator),
				TypeDefinitionReference definition => new CType(CTypeName(definition) + " " + declarator),
				NamedTypeReference named => new CType(CTypeName(named) + " " + declarator),
				GenericTypeReference generic => FormatType(generic.Type, declarator),
				GenericParameterTypeReference => new CType("void* " + declarator),
				AnyTypeReference => new CType("void* " + declarator),
				AutoTypeReference => new CType("void* " + declarator),
				AllocatorTypeReference => new CType("Allocator* " + declarator),
				MaterializedStructTypeReference => new CType("void* " + declarator),
				GroupedParamsTypeReference => new CType("void* " + declarator),
				ThrownTypeReference thrown => FormatType(thrown.Type, declarator),
				IterTypeReference => new CType("void* " + declarator),
				_ => new CType((type.ResolvedType is null ? "void*" : CTypeName(type.ResolvedType)) + " " + declarator)
			};
		}

		CType FormatQualifiedType(string qualifier, TypeReference? inner, string declarator)
		{
			if (inner is PointerTypeReference or ArrayTypeReference or OptionalTypeReference or GenericTypeReference or CallableTypeReference or TargetTypeSpecTypeReference)
				return FormatType(inner, declarator + " " + qualifier);
			CType formatted = FormatType(inner, declarator);
			return new CType(qualifier + " " + formatted.Declaration);
		}

		CType FormatTargetSpecType(TargetTypeSpecTypeReference targetSpec, string declarator)
		{
			string cSpec = FormatTypeSpec(targetSpec.Specifier);
			if (cSpec.Length == 0)
				return FormatType(targetSpec.Type, declarator);
			return FormatType(targetSpec.Type, declarator + " " + cSpec);
		}

		CType FormatPointerType(PointerTypeReference pointer, string declarator)
		{
			if (pointer.ElementType is PrimitiveTypeReference { Type: PrimitiveType.Untyped })
				return new CType("void* " + declarator);
			return FormatType(pointer.ElementType, "*" + declarator);
		}

		string FormatPrimitive(PrimitiveType primitive)
		{
			string name = GetPrimitiveName(primitive);
			if (primitive == PrimitiveType.Void)
				return "void";
			if (primitive == PrimitiveType.String)
				return "char*";
			if (primitive == PrimitiveType.WString)
				return "uint16_t*";
			if (primitive == PrimitiveType.AString)
				return "char*";
			if (primitive == PrimitiveType.Untyped)
				return "void";
			return compilation.Target?.GetPrimitiveCSpelling(name) ?? name;
		}

		string FormatCallSpec(string? spec)
		{
			if (string.IsNullOrWhiteSpace(spec))
				return "";
			return compilation.Target?.CallSpecs.TryGetValue(spec, out string? spelling) == true ? spelling : spec;
		}

		string FormatTypeSpec(string? spec)
		{
			if (string.IsNullOrWhiteSpace(spec))
				return "";
			return compilation.Target?.TypeSpecs.TryGetValue(spec, out string? spelling) == true ? spelling : spec;
		}

		static string? FormatConstantExpression(Expression? expression)
		{
			return expression switch
			{
				null => null,
				LiteralExpression { Kind: LiteralKind.Number } literal => literal.Text,
				LiteralExpression { Kind: LiteralKind.True } => "1",
				LiteralExpression { Kind: LiteralKind.False } => "0",
				LiteralExpression { Kind: LiteralKind.Null } => "NULL",
				CastExpression cast => FormatConstantExpression(cast.Expression),
				ParenthesizedExpression parenthesized => FormatConstantExpression(parenthesized.Expression),
				VariableReferenceExpression { Variable: VariableDefinition variable } => CName(variable),
				NamedExpression named => named.Name,
				_ => null
			};
		}

		static string CTypeName(TypeReference type)
		{
			return type.ResolvedType is string resolved ? CTypeName(resolved) : "void*";
		}

		static string CTypeName(TypeDefinitionReference definition)
		{
			return definition.Definition is not null ? CName(definition.Definition) : SanitizeIdentifier(definition.Name);
		}

		static string CTypeName(NamedTypeReference named)
		{
			return !string.IsNullOrWhiteSpace(named.ResolvedType)
				? CTypeName(named.ResolvedType)
				: SanitizeIdentifier(named.Name);
		}

		static string CTypeName(string resolvedType)
		{
			int genericStart = resolvedType.IndexOf('<', StringComparison.Ordinal);
			if (genericStart >= 0)
				resolvedType = resolvedType[..genericStart];
			return resolvedType switch
			{
				"any" or "auto" or "#TARGET" => "void*",
				_ => SanitizeIdentifier(RemoveTypeDecorators(resolvedType))
			};
		}

		static string RemoveTypeDecorators(string value)
		{
			return value
				.Replace("const ", "", StringComparison.Ordinal)
				.Replace("volatile ", "", StringComparison.Ordinal)
				.Replace("escaped ", "", StringComparison.Ordinal)
				.Replace("scoped ", "", StringComparison.Ordinal)
				.Replace("unscoped ", "", StringComparison.Ordinal)
				.Replace("*", "Ptr", StringComparison.Ordinal)
				.Replace("[]", "Array", StringComparison.Ordinal)
				.Replace("?", "Optional", StringComparison.Ordinal);
		}

		string CName(FunctionDefinition function)
		{
			if (!string.IsNullOrWhiteSpace(function.Symbol) && function.Symbol != function.Name)
				return SanitizeIdentifier(function.Symbol);
			if (containingTypes.TryGetValue(function, out TypeDefinition? type))
				return SanitizeIdentifier(type.Name + "_" + function.Name.TrimStart('~'));
			return SanitizeIdentifier(string.IsNullOrWhiteSpace(function.Symbol) ? function.Name : function.Symbol);
		}

		static string CName(Definition definition)
		{
			return SanitizeIdentifier(string.IsNullOrWhiteSpace(definition.Symbol) ? definition.Name : definition.Symbol);
		}

		static string CName(FieldDefinition field)
		{
			return SanitizeIdentifier(string.IsNullOrWhiteSpace(field.Symbol) ? field.Name : field.Symbol);
		}

		static string CName(ParameterDefinition parameter)
		{
			return SanitizeIdentifier(string.IsNullOrWhiteSpace(parameter.Symbol) ? parameter.Name : parameter.Symbol);
		}

		static string GetPrimitiveName(PrimitiveType type)
		{
			return type switch
			{
				PrimitiveType.Void => "void",
				PrimitiveType.Bool => "bool",
				PrimitiveType.String => "string",
				PrimitiveType.WString => "wstring",
				PrimitiveType.AString => "astring",
				PrimitiveType.Byte => "byte",
				PrimitiveType.SByte => "sbyte",
				PrimitiveType.UShort => "ushort",
				PrimitiveType.Short => "short",
				PrimitiveType.UInt => "uint",
				PrimitiveType.Int => "int",
				PrimitiveType.ULong => "ulong",
				PrimitiveType.Long => "long",
				PrimitiveType.NUInt => "nuint",
				PrimitiveType.NInt => "nint",
				PrimitiveType.Float => "float",
				PrimitiveType.Double => "double",
				PrimitiveType.Char => "char",
				PrimitiveType.WChar => "wchar",
				PrimitiveType.AChar => "achar",
				PrimitiveType.UChar => "uchar",
				PrimitiveType.Untyped => "untyped",
				_ => "void"
			};
		}

		readonly record struct CType(string Declaration);
	}
}
