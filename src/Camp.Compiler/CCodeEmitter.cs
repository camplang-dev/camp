using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace Camp.Compiler;

public sealed class CEmissionOptions
{
	public required string OutputDirectory { get; init; }
	public required string ProjectName { get; init; }
	public string EmitKind { get; init; } = "c99";
	public NativeBuildKind? BuildKind { get; init; }
	public bool EmitExecMainWrapper { get; init; }
	public FunctionDefinition? ExecEntryPoint { get; init; }
}

public sealed class CEmissionResult
{
	public List<string> GeneratedFiles { get; } = [];
	public List<string> GeneratedSourceFiles { get; } = [];
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

	static readonly BigInteger UInt32MaxValue = new(uint.MaxValue);
	static readonly BigInteger Int32MinMagnitude = new(2147483648UL);

	static string FormatNumberLiteralForC(string text, bool negativeContext = false)
	{
		if (!TryParseIntegerLiteral(text, out BigInteger magnitude, out bool unsignedSuffix, out string coreText))
			return text;

		bool needsLongLong = negativeContext
			? magnitude > Int32MinMagnitude
			: magnitude > UInt32MaxValue;
		if (!needsLongLong)
			return text;

		return coreText + (unsignedSuffix ? "ULL" : "LL");
	}

	static bool TryParseIntegerLiteral(string text, out BigInteger magnitude, out bool unsignedSuffix, out string coreText)
	{
		magnitude = BigInteger.Zero;
		unsignedSuffix = false;
		coreText = text;

		if (string.IsNullOrWhiteSpace(text)
			|| text.Contains('.', StringComparison.Ordinal)
			|| text.Contains('p', StringComparison.OrdinalIgnoreCase))
			return false;

		if (text.EndsWith("u", StringComparison.OrdinalIgnoreCase))
		{
			unsignedSuffix = true;
			coreText = text[..^1];
		}
		else if (text.EndsWith("l", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		int radix = 10;
		int start = 0;
		if (coreText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			radix = 16;
			start = 2;
		}
		else if (coreText.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
		{
			radix = 2;
			start = 2;
		}
		else if (coreText.Contains('e', StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		if (start >= coreText.Length)
			return false;

		for (int i = start; i < coreText.Length; i++)
		{
			char ch = coreText[i];
			if (ch == '_')
				continue;

			int digit = ch switch
			{
				>= '0' and <= '9' => ch - '0',
				>= 'a' and <= 'f' => ch - 'a' + 10,
				>= 'A' and <= 'F' => ch - 'A' + 10,
				_ => -1
			};
			if (digit < 0 || digit >= radix)
				return false;

			magnitude = magnitude * radix + digit;
		}

		return true;
	}

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
			CDeclarationWriter declarations = new(compilation, options, result);
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
		if (!result.Success)
			DeleteGeneratedFiles(result);

		return result;
	}

	public static CEmissionResult EmitProjectApiHeader(Compilation compilation, CEmissionOptions options, string outputDirectory)
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
			Directory.CreateDirectory(outputDirectory);
			CDeclarationWriter declarations = new(compilation, options, result);
			string filename = Path.Combine(outputDirectory, options.ProjectName + "_api.h");
			using StreamWriter writer = new(filename, append: false, Utf8NoBom);
			string guard = BuildHeaderGuard(options.ProjectName + "_api_h");
			writer.WriteLine("#ifndef " + guard);
			writer.WriteLine("#define " + guard);
			writer.WriteLine();
			WriteTargetPreamble(writer, compilation);
			writer.WriteLine();
			declarations.WriteProjectApiHeaderDeclarations(writer);
			writer.WriteLine();
			writer.WriteLine("#endif");
			result.GeneratedFiles.Add(filename);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			result.Diagnostics.Add(ex.Message);
		}
		if (!result.Success)
			DeleteGeneratedFiles(result);
		return result;
	}

	static void DeleteGeneratedFiles(CEmissionResult result)
	{
		foreach (string generated in result.GeneratedFiles)
		{
			try
			{
				if (File.Exists(generated))
					File.Delete(generated);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				result.Diagnostics.Add(ex.Message);
			}
		}
		result.GeneratedFiles.Clear();
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
            if (node is LiteralExpression or AttributeConstructor or InitializerItem)
                continue;
			if (node.ResolvedType is string resolvedType && InvalidResolvedTypes.Contains(resolvedType))
			{
				result.Diagnostics.Add($"C emission aborted because {DescribeUnresolvedNode(node)} has unresolved type '{resolvedType}'.");
				return false;
			}
		}

		return true;
	}

	static string DescribeUnresolvedNode(BindableNode node)
	{
		string description = node.GetType().Name;
		if (node is NamedExpression named)
			description += $" '{named.Name}'";
		else if (node is VariableReferenceExpression variableReference)
		{
			description += variableReference.Variable switch
			{
				Definition definition => $" '{definition.Name}'",
				DeclarationTarget target => $" '{string.Join(", ", target.Names)}'",
				_ => ""
			};
			if (variableReference.Variable?.ResolvedType is string variableType)
				description += $" referencing {variableReference.Variable.GetType().Name} '{variableType}'";
		}

		if (node.SourceSyntax is not null && TryGetSourceRange(node.SourceSyntax, out TokenRange range))
			description += $" at {range.StartLineNumber},{range.StartColumn}";

		return description;
	}

	static bool TryGetSourceRange(SyntaxNode syntax, out TokenRange range)
	{
		foreach (PropertyInfo property in syntax.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.PropertyType == typeof(TokenRange?) && property.GetValue(syntax) is TokenRange tokenRange)
			{
				range = tokenRange;
				return true;
			}
		}

		range = default;
		return false;
	}

	static void EmitPrivateHeader(Compilation compilation, CEmissionOptions options, CEmissionResult result, CDeclarationWriter declarations)
	{
		string filename = Path.Combine(options.OutputDirectory, options.ProjectName + "_private.h");
		using StreamWriter writer = new(filename, append: false, Utf8NoBom);
		string guard = BuildHeaderGuard(options.ProjectName + "_private_h");

		writer.WriteLine("#ifndef " + guard);
		writer.WriteLine("#define " + guard);
		writer.WriteLine();
		WriteTargetPreamble(writer, compilation);

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
		writer.WriteLine();
		declarations.WriteSourceFileForwardDeclarations(writer, file);
		writer.WriteLine();
		declarations.WriteSourceFileDefinitions(writer, file);
		if (options.EmitExecMainWrapper && options.ExecEntryPoint is not null && IsFirstProjectSource(compilation, file))
			declarations.WriteExecMainWrapper(writer, options.ExecEntryPoint);
		result.GeneratedFiles.Add(filename);
		result.GeneratedSourceFiles.Add(filename);
	}

	static bool IsFirstProjectSource(Compilation compilation, SourceFile file)
	{
		return ReferenceEquals(compilation.Files.FirstOrDefault(static candidate => !candidate.IsApiHeader), file);
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
			WriteTargetPreamble(writer, compilation);
		writer.WriteLine();
		declarations.WritePublicHeaderDeclarations(writer, file);
		writer.WriteLine();
		writer.WriteLine("#endif");
		result.GeneratedFiles.Add(filename);
	}

	static void WriteTargetPreamble(TextWriter writer, Compilation compilation)
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
			if (definition.Export is not null && definition is not AliasDefinition)
				return true;
			if (definition is TypeDefinition typeDefinition && (TypeHasExportedCallable(typeDefinition) || TypeHasExportedStaticField(typeDefinition)))
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

	static bool TypeHasExportedStaticField(TypeDefinition typeDefinition)
	{
		return typeDefinition switch
		{
			ClassDefinition classDefinition => classDefinition.Fields.Any(static field => field.Modifier == FieldModifier.Static && field.Export is not null),
			StructDefinition structDefinition => structDefinition.Fields.Any(static field => field.Modifier == FieldModifier.Static && field.Export is not null),
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Fields.Any(static field => field.Modifier == FieldModifier.Static && field.Export is not null),
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

	public static string GetDefaultArtifactDirectory(IReadOnlyList<SourceFile> files)
	{
		SourceFile? first = files.FirstOrDefault(static file => !file.IsApiHeader) ?? files.FirstOrDefault();
		string? directory = first is null || first.Path == "-" ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(first.Path);
		return string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : directory;
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

	sealed class CDeclarationWriter(Compilation compilation, CEmissionOptions options, CEmissionResult result)
	{
		readonly HashSet<string> emittedNames = new(StringComparer.Ordinal);
		readonly Dictionary<FunctionDefinition, TypeDefinition> containingTypes = BuildContainingTypeMap(compilation);
		readonly HashSet<string> genericParameterNames = BuildGenericParameterNameSet(compilation);
		readonly HashSet<string> anyGenericParameterNames = BuildAnyGenericParameterNameSet(compilation);
		readonly HashSet<string> currentGenericTypeNames = new(StringComparer.Ordinal);
		readonly HashSet<string> currentAnyGenericTypeNames = new(StringComparer.Ordinal);
		readonly HashSet<string> currentArrayElementComponentNames = new(StringComparer.Ordinal);
		readonly Dictionary<Expression, DelegateThunk> delegateThunksByExpression = [];
		readonly Dictionary<SourceFile, List<DelegateThunk>> delegateThunksByFile = [];
		readonly HashSet<string> reservedCNames = [];
		FunctionDefinition? currentFunction;
		bool currentFunctionHasLabels;
		readonly string sharedExportPrefix = options.BuildKind is NativeBuildKind.Shared
			? compilation.Target?.GetCEmitterValue("dll_export_prefix") ?? ""
			: "";

		public void WritePrivateHeaderDeclarations(TextWriter writer)
		{
			EnsureDelegateThunksCollected();
			emittedNames.Clear();
			List<Definition> definitions = GetProjectDefinitions().ToList();

			WriteSection(writer, "Forward declarations", () =>
			{
				foreach (TypeDefinition type in definitions.OfType<TypeDefinition>())
					WriteTypeForwardDeclaration(writer, type);
			});

			WriteSection(writer, "Enums", () =>
			{
				foreach (EnumDefinition enumDefinition in definitions.OfType<EnumDefinition>())
					WriteEnumDefinition(writer, enumDefinition);
			});

			WriteSection(writer, "Newtypes", () =>
			{
				foreach (NewtypeDefinition newtype in definitions.OfType<NewtypeDefinition>())
					WriteNewtypeDefinition(writer, newtype, exportedOnly: false);
			});

			List<string> callableTypes = CollectResolvedCallableTypes(definitions).ToList();
			if (callableTypes.Count > 0)
			{
				WriteSection(writer, "Callable typedefs", () =>
				{
					foreach (string callableType in callableTypes)
						WriteCallableAliasTypedef(writer, callableType);
				});
			}

			WriteSection(writer, "Layouts", () =>
			{
				foreach (TypeDefinition type in definitions.OfType<TypeDefinition>())
					WriteLayoutDefinition(writer, type);
			});

			WriteSection(writer, "Function declarations", () =>
			{
				foreach (FunctionDefinition function in GetAllFunctions(definitions).Where(IsExternallyVisible))
					WriteFunctionPrototype(writer, function, storage: null);
			});

			WriteSection(writer, "Object declarations", () =>
			{
				foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>().Where(IsExternallyVisible))
					WriteVariableDeclaration(writer, variable, storage: "extern");
				foreach (FieldDefinition field in GetAllStaticFields(definitions).Where(IsExternallyVisible))
					WriteFieldStorageDeclaration(writer, field, storage: "extern");
			});
		}

		public void WriteSourceFileForwardDeclarations(TextWriter writer, SourceFile file)
		{
			EnsureDelegateThunksCollected();
			emittedNames.Clear();
			List<Definition> definitions = GetOwnedDefinitions(file).ToList();
			List<FunctionDefinition> privateFunctions = GetAllFunctions(definitions).Where(static function => !IsExternallyVisible(function)).ToList();
			List<VariableDefinition> privateVariables = definitions.OfType<VariableDefinition>().Where(static variable => !IsExternallyVisible(variable)).ToList();
			List<FieldDefinition> privateStaticFields = GetAllStaticFields(definitions).Where(static field => !IsExternallyVisible(field)).ToList();
			List<DelegateThunk> delegateThunks = delegateThunksByFile.TryGetValue(file, out List<DelegateThunk>? thunks) ? thunks : [];

			if (privateFunctions.Count == 0 && privateVariables.Count == 0 && privateStaticFields.Count == 0 && delegateThunks.Count == 0)
				return;

			writer.WriteLine("/* Private file declarations. */");
			foreach (DelegateThunk thunk in delegateThunks)
				WriteDelegateThunkPrototype(writer, thunk);
			foreach (FunctionDefinition function in privateFunctions)
				WriteFunctionPrototype(writer, function, storage: function.Extern is not null ? null : "static");
			foreach (VariableDefinition variable in privateVariables)
				WriteVariableDeclaration(writer, variable, storage: variable.Extern is not null ? "extern" : "static");
			foreach (FieldDefinition field in privateStaticFields)
				WriteFieldStorageDeclaration(writer, field, storage: "static");
		}

		public void WriteSourceFileDefinitions(TextWriter writer, SourceFile file)
		{
			EnsureDelegateThunksCollected();
			emittedNames.Clear();
			List<Definition> definitions = GetOwnedDefinitions(file).ToList();
			List<DelegateThunk> delegateThunks = delegateThunksByFile.TryGetValue(file, out List<DelegateThunk>? thunks) ? thunks : [];
			bool wrote = false;

			foreach (DelegateThunk thunk in delegateThunks)
			{
				WriteDelegateThunkDefinition(writer, thunk);
				wrote = true;
			}

			foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>())
			{
				if (variable.Extern is not null)
					continue;
				WriteVariableDefinition(writer, variable, storage: IsExternallyVisible(variable) ? null : "static");
				wrote = true;
			}

			foreach (FieldDefinition field in GetAllStaticFields(definitions))
			{
				WriteFieldStorageDefinition(writer, field, storage: IsExternallyVisible(field) ? null : "static");
				wrote = true;
			}

			foreach (FunctionDefinition function in GetAllFunctions(definitions))
			{
				if (function.Extern is not null || function.Body is null)
					continue;
				if (function.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor)
					continue;
				WriteFunctionDefinition(writer, function, storage: IsExternallyVisible(function) ? null : "static");
				wrote = true;
			}

			if (!wrote)
				writer.WriteLine("/* No C definitions emitted for this file. */");
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
							WriteTypeForwardDeclaration(writer, structDefinition);
							WriteFieldLayout(writer, structDefinition, structDefinition.Fields);
							wrote = true;
							break;
					}
				}
			}

			List<string> callableTypes = CollectResolvedCallableTypes(definitions).ToList();
			if (callableTypes.Count > 0)
			{
				foreach (string callableType in callableTypes)
					WriteCallableAliasTypedef(writer, callableType);
				wrote = true;
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
			foreach (FieldDefinition field in GetAllStaticFields(definitions).Where(static field => field.Export is not null))
			{
				WriteFieldStorageDeclaration(writer, field, storage: "extern");
				wrote = true;
			}

			if (!wrote)
				writer.WriteLine("/* No exported declarations. */");
		}

		public void WriteProjectApiHeaderDeclarations(TextWriter writer)
		{
			emittedNames.Clear();
			List<Definition> definitions = GetProjectDefinitions().ToList();
			bool wrote = false;

			foreach (TypeDefinition type in definitions.OfType<TypeDefinition>().Where(static type => type.Export is not null))
			{
				switch (type)
				{
					case ClassDefinition:
						WriteTypeForwardDeclaration(writer, type);
						wrote = true;
						break;
					case InterfaceDefinition interfaceDefinition:
						WriteInterfaceLayout(writer, interfaceDefinition);
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
						WriteTypeForwardDeclaration(writer, structDefinition);
						WriteFieldLayout(writer, structDefinition, structDefinition.Fields);
						wrote = true;
						break;
				}
			}

			List<string> callableTypes = CollectResolvedCallableTypes(definitions).ToList();
			if (callableTypes.Count > 0)
			{
				foreach (string callableType in callableTypes)
					WriteCallableAliasTypedef(writer, callableType);
				wrote = true;
			}

			foreach (FunctionDefinition function in GetAllFunctions(definitions).Where(static function => function.Export is not null))
			{
				if (!ShouldWriteProjectApiFunction(function))
					continue;
				WriteFunctionPrototype(writer, function, storage: null);
				wrote = true;
			}

			foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>().Where(static variable => variable.Export is not null))
			{
				if (IsGeneratedVTableVariable(variable))
					continue;
				WriteVariableDeclaration(writer, variable, storage: "extern");
				wrote = true;
			}
			foreach (FieldDefinition field in GetAllStaticFields(definitions).Where(static field => field.Export is not null))
			{
				WriteFieldStorageDeclaration(writer, field, storage: "extern");
				wrote = true;
			}

			if (!wrote)
				writer.WriteLine("/* No exported declarations. */");
		}

		public void WriteExecMainWrapper(TextWriter writer, FunctionDefinition entryPoint)
		{
			writer.WriteLine();
			if (entryPoint.Parameters.Count == 0)
			{
				writer.WriteLine("int main(void)");
				writer.WriteLine("{");
				if (IsIntReturn(entryPoint))
					writer.WriteLine("\treturn " + CName(entryPoint) + "();");
				else
				{
					writer.WriteLine("\t" + CName(entryPoint) + "();");
					writer.WriteLine("\treturn 0;");
				}
				writer.WriteLine("}");
				return;
			}

			writer.WriteLine("int main(int argc, char* argv[])");
			writer.WriteLine("{");
			if (IsIntReturn(entryPoint))
				writer.WriteLine("\treturn " + CName(entryPoint) + "((const char**)argv, (uintptr_t)argc);");
			else
			{
				writer.WriteLine("\t" + CName(entryPoint) + "((const char**)argv, (uintptr_t)argc);");
				writer.WriteLine("\treturn 0;");
			}
			writer.WriteLine("}");
		}

		void EnsureDelegateThunksCollected()
		{
			if (reservedCNames.Count > 0)
				return;

			foreach (Definition definition in GetDefinitions())
				ReserveDefinitionNames(definition);

			foreach (FunctionDefinition function in GetAllFunctions(GetDefinitions()))
			{
				if (function.Body is null || !TryGetDefinitionOwner(function, out SourceFile? file))
					continue;

				foreach (BindableNode node in EnumerateNodes(function.Body, []))
				{
					if (node is not CallExpression call)
						continue;

					FunctionDefinition? targetFunction = TryGetCallFunction(call);
					if (targetFunction is null)
						continue;

					Dictionary<string, string> substitutions = GetCallGenericSubstitutions(call, targetFunction);
					List<ParameterDefinition> parameters = GetCallableParametersForCall(targetFunction);
					for (int i = 0; i < call.Arguments.Count && i < parameters.Count; i++)
					{
						ArgumentExpression argument = call.Arguments[i];
						if (argument.Modifier != ArgumentModifier.None || argument.Value is null)
							continue;
						if (!TryGetDirectFunctionValue(argument.Value, out FunctionDefinition? sourceFunction))
							continue;
						if (!TryCreateDelegateThunk(sourceFunction, parameters[i], substitutions, file, out DelegateThunk? thunk))
							continue;

						delegateThunksByExpression[argument.Value] = thunk;
					}
				}
			}
		}

		void ReserveDefinitionNames(Definition definition)
		{
			switch (definition)
			{
				case FunctionDefinition function:
					reservedCNames.Add(CName(function));
					break;
				case VariableDefinition variable:
					reservedCNames.Add(CName(variable));
					break;
				case TypeDefinition type:
					reservedCNames.Add(CName(type));
					foreach (FunctionDefinition function in type switch
					{
						ClassDefinition classDefinition => classDefinition.Functions,
						StructDefinition structDefinition => structDefinition.Functions,
						InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
						EnumDefinition enumDefinition => enumDefinition.Functions,
						NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions,
						ParamsDefinition paramsDefinition => paramsDefinition.Functions,
						_ => []
					})
						reservedCNames.Add(CName(function));
					foreach (FieldDefinition field in GetTypeFields(type))
						if (field.Modifier == FieldModifier.Static)
							reservedCNames.Add(CName(field));
					break;
			}
		}

		bool TryGetDefinitionOwner(FunctionDefinition function, out SourceFile file)
		{
			if (compilation.DefinitionOwners.TryGetValue(function, out file!))
				return true;
			if (!containingTypes.TryGetValue(function, out TypeDefinition? type))
				return false;
			return compilation.DefinitionOwners.TryGetValue(type, out file!);
		}

		static bool TryGetDirectFunctionValue(Expression expression, out FunctionDefinition function)
		{
			switch (expression)
			{
				case CastExpression { Expression: not null } cast:
					return TryGetDirectFunctionValue(cast.Expression, out function);
				case ParenthesizedExpression { Expression: not null } parenthesized:
					return TryGetDirectFunctionValue(parenthesized.Expression, out function);
				case MethodReferenceExpression { Candidates.Count: 1 } method:
					function = method.Candidates[0];
					return true;
				case MemberReferenceExpression { Member: FunctionDefinition memberFunction }:
					function = memberFunction;
					return true;
				case VariableReferenceExpression { Variable: FunctionDefinition variableFunction }:
					function = variableFunction;
					return true;
				default:
					function = null!;
					return false;
			}
		}

		bool TryCreateDelegateThunk(
			FunctionDefinition sourceFunction,
			ParameterDefinition targetParameter,
			Dictionary<string, string> substitutions,
			SourceFile file,
			out DelegateThunk thunk)
		{
			thunk = null!;
			string? expectedType = SubstituteGenericTypeTokens(targetParameter.ResolvedType ?? targetParameter.Type?.ResolvedType, substitutions);
			if (expectedType is null || !TryParseResolvedCallableType(expectedType, out string targetReturnType, out List<string> targetParameterTypes))
				return false;
			if (targetParameterTypes.Count == 0 || !IsVoidPointerType(targetParameterTypes[0]))
				return false;

			List<ParameterDefinition> sourceParameters = GetCallableParametersForCall(sourceFunction);
			bool forwardsContext = sourceParameters.Count == targetParameterTypes.Count
				&& sourceParameters.Count > 0
				&& IsVoidPointerType(sourceParameters[0].ResolvedType ?? sourceParameters[0].Type?.ResolvedType ?? "");
			if (!forwardsContext && sourceParameters.Count + 1 != targetParameterTypes.Count)
				return false;
			string sourceReturnType = sourceFunction.ResolvedType ?? sourceFunction.ReturnType?.ResolvedType ?? "void";
			if (!SameCallableTypeSlot(sourceReturnType, targetReturnType))
				return false;
			int targetOffset = forwardsContext ? 0 : 1;
			for (int i = 0; i < sourceParameters.Count; i++)
				if (!CanThunkArgumentConvert(sourceParameters[i], targetParameterTypes[i + targetOffset]))
					return false;

			string name = CreateUniqueDelegateThunkName(sourceFunction);
			thunk = new DelegateThunk(name, sourceFunction, targetReturnType, targetParameterTypes, forwardsContext);
			if (!delegateThunksByFile.TryGetValue(file, out List<DelegateThunk>? thunks))
			{
				thunks = [];
				delegateThunksByFile[file] = thunks;
			}
			thunks.Add(thunk);
			return true;
		}

		string CreateUniqueDelegateThunkName(FunctionDefinition sourceFunction)
		{
			string prefix = "__camp_delegate_" + CName(sourceFunction);
			string candidate = prefix;
			int suffix = 0;
			while (!reservedCNames.Add(candidate))
			{
				suffix++;
				candidate = prefix + "_" + suffix.ToString(CultureInfo.InvariantCulture);
			}
			return candidate;
		}

		static bool IsVoidPointerType(string type)
		{
			string normalized = StripTypeDecorators(type).Replace(" ", "", StringComparison.Ordinal);
			return normalized is "void*" or "untyped*";
		}

		static string GetCallableParameterTypeText(ParameterDefinition parameter)
		{
			string type = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? "void";
			return parameter.Modifier switch
			{
				ParameterModifier.In => "in " + type,
				ParameterModifier.Out => "out " + type,
				ParameterModifier.Thrown => "thrown " + type,
				ParameterModifier.Within => "within " + type,
				_ => type
			};
		}

		static bool SameCallableTypeSlot(string left, string right)
		{
			return NormalizeCallableTypeSlot(left) == NormalizeCallableTypeSlot(right);
		}

		bool CanThunkArgumentConvert(ParameterDefinition sourceParameter, string targetType)
		{
			if (SameCallableTypeSlot(GetCallableParameterTypeText(sourceParameter), targetType))
				return true;
			if (!IsVoidPointerType(targetType))
				return IsPointerStorageForValue(targetType, sourceParameter.ResolvedType ?? sourceParameter.Type?.ResolvedType ?? "");
			if (sourceParameter.Modifier == ParameterModifier.In)
				return true;
			string sourceType = sourceParameter.ResolvedType ?? sourceParameter.Type?.ResolvedType ?? "";
			return !string.IsNullOrWhiteSpace(sourceType) && !IsPointerLikeCArgumentType(sourceType);
		}

		static bool IsPointerStorageForValue(string targetType, string sourceType)
		{
			if (string.IsNullOrWhiteSpace(sourceType) || IsPointerLikeCArgumentType(sourceType))
				return false;
			if (targetType.StartsWith("in ", StringComparison.Ordinal))
				return targetType["in ".Length..].Trim() == sourceType;
			string strippedTarget = StripTypeDecorators(targetType);
			return strippedTarget == sourceType + "*";
		}

		static bool IsPointerLikeCArgumentType(string type)
		{
			string stripped = StripTypeDecorators(type);
			return stripped.EndsWith("*", StringComparison.Ordinal)
				|| stripped.EndsWith("[]", StringComparison.Ordinal)
				|| stripped.StartsWith("fn ", StringComparison.Ordinal);
		}

		static string NormalizeCallableTypeSlot(string type)
		{
			return string.Join(" ", SplitTopLevel(type.Trim(), ' '));
		}

		string? SubstituteGenericTypeTokens(string? type, Dictionary<string, string> substitutions)
		{
			if (string.IsNullOrWhiteSpace(type) || substitutions.Count == 0)
				return type;

			StringBuilder builder = new(type.Length);
			for (int i = 0; i < type.Length;)
			{
				if (IsIdentifierStart(type[i]))
				{
					int start = i;
					i++;
					while (i < type.Length && IsIdentifierPart(type[i]))
						i++;
					string token = type[start..i];
					builder.Append(substitutions.TryGetValue(token, out string? replacement) ? replacement : token);
					continue;
				}

				builder.Append(type[i]);
				i++;
			}
			return builder.ToString();
		}

		void WriteDelegateThunkPrototype(TextWriter writer, DelegateThunk thunk)
		{
			writer.WriteLine("static " + FormatResolvedType(thunk.ReturnType, thunk.Name).Declaration + "(" + FormatResolvedParameterList(thunk.ParameterTypes) + ");");
		}

		void WriteDelegateThunkDefinition(TextWriter writer, DelegateThunk thunk)
		{
			writer.WriteLine("static " + FormatResolvedType(thunk.ReturnType, thunk.Name).Declaration + "(" + FormatResolvedParameterList(thunk.ParameterTypes) + ")");
			writer.WriteLine("{");
			if (!thunk.ForwardsContext)
				writer.WriteLine("\t(void)arg0;");
			List<string> arguments = [];
			List<ParameterDefinition> sourceParameters = GetCallableParametersForCall(thunk.SourceFunction);
			int start = thunk.ForwardsContext ? 0 : 1;
			for (int i = start; i < thunk.ParameterTypes.Count; i++)
			{
				string argument = "arg" + i.ToString(CultureInfo.InvariantCulture);
				int sourceIndex = thunk.ForwardsContext ? i : i - 1;
				if (sourceIndex < sourceParameters.Count)
				{
					string sourceType = GetCallableParameterTypeText(sourceParameters[sourceIndex]);
					string targetType = thunk.ParameterTypes[i];
					if (!SameCallableTypeSlot(sourceType, targetType))
					{
						string sourceResolvedType = sourceParameters[sourceIndex].ResolvedType ?? sourceParameters[sourceIndex].Type?.ResolvedType ?? "void";
						if (sourceParameters[sourceIndex].Modifier == ParameterModifier.In)
						{
							string cType = FormatResolvedType(sourceResolvedType + "*", "").Declaration.Trim();
							argument = "(" + cType + ")" + argument;
						}
						else if ((IsVoidPointerType(targetType) || IsPointerStorageForValue(targetType, sourceResolvedType)) && !IsPointerLikeCArgumentType(sourceResolvedType))
						{
							string cType = FormatResolvedType(sourceResolvedType + "*", "").Declaration.Trim();
							argument = "*((" + cType + ")" + argument + ")";
						}
						else
						{
							string cType = FormatResolvedType(sourceResolvedType, "").Declaration.Trim();
							argument = "(" + cType + ")" + argument;
						}
					}
				}
				arguments.Add(argument);
			}
			string call = CName(thunk.SourceFunction) + "(" + string.Join(", ", arguments) + ")";
			if (thunk.ReturnType == "void")
				writer.WriteLine("\t" + call + ";");
			else
				writer.WriteLine("\treturn " + call + ";");
			writer.WriteLine("}");
			writer.WriteLine();
		}

		static bool IsIntReturn(FunctionDefinition function)
		{
			return function.ReturnType is PrimitiveTypeReference { Type: PrimitiveType.Int } || function.ResolvedType == "int";
		}

		static bool ShouldWriteProjectApiFunction(FunctionDefinition function)
		{
			if (function.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor)
				return false;
			if (function.Name is "op_initnew" or "op_delete")
				return false;
			if (function.Name.StartsWith("_", StringComparison.Ordinal) && function.Name is not "_create" and not "_destroy")
				return false;
			if (function.Symbol.Contains("__", StringComparison.Ordinal))
				return false;
			return true;
		}

		static bool IsExternallyVisible(Definition definition)
		{
			return definition.Export is not null || definition.Public is not null;
		}

		static bool IsGeneratedVTableVariable(VariableDefinition variable)
		{
			return variable.Name.Contains("__vt", StringComparison.Ordinal)
				|| variable.Symbol.Contains("__vt", StringComparison.Ordinal)
				|| variable.Name.EndsWith("_vt", StringComparison.Ordinal)
				|| variable.Symbol.EndsWith("_vt", StringComparison.Ordinal);
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

		IEnumerable<Definition> GetProjectDefinitions()
		{
			foreach (Definition definition in GetDefinitions())
			{
				if (!compilation.DefinitionOwners.TryGetValue(definition, out SourceFile? owner) || owner.IsApiHeader)
					continue;
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

		static IEnumerable<FieldDefinition> GetAllStaticFields(IEnumerable<Definition> definitions)
		{
			foreach (Definition definition in definitions)
			{
				if (definition is not TypeDefinition type)
					continue;
				foreach (FieldDefinition field in GetTypeFields(type))
					if (field.Modifier == FieldModifier.Static)
						yield return field;
			}
		}

		static IEnumerable<FieldDefinition> GetTypeFields(TypeDefinition type)
		{
			return type switch
			{
				ClassDefinition classDefinition => classDefinition.Fields,
				StructDefinition structDefinition => structDefinition.Fields,
				NewtypeDefinition newtypeDefinition => newtypeDefinition.Fields,
				_ => []
			};
		}

		IEnumerable<string> CollectResolvedCallableTypes(IEnumerable<Definition> definitions)
		{
			HashSet<string> types = new(StringComparer.Ordinal);
			foreach (FunctionDefinition function in GetAllFunctions(definitions))
			{
				AddType(function.ReturnType, function.ResolvedType);
				foreach (ParameterDefinition parameter in function.Parameters)
					AddType(parameter.Type, parameter.ResolvedType);
				if (function.Body is not null)
					foreach (BindableNode node in EnumerateNodes(function.Body, []))
						AddNode(node);
			}
			foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>())
				AddType(variable.Type, variable.ResolvedType);
			foreach (TypeDefinition type in definitions.OfType<TypeDefinition>())
			{
				foreach (FieldDefinition field in GetTypeFields(type))
					AddType(field.Type, field.ResolvedType);
			}
			return types.Order(StringComparer.Ordinal);

			void AddType(TypeReference? type, string? resolvedType)
			{
				if (resolvedType is not null && IsResolvedCallableType(resolvedType))
					types.Add(resolvedType);
				switch (type)
				{
					case null:
						break;
					case CallableTypeReference callable:
						if (callable.ResolvedType is not null && IsResolvedCallableType(callable.ResolvedType))
							types.Add(callable.ResolvedType);
						AddType(callable.ReturnType, callable.ReturnType?.ResolvedType);
						foreach (ParameterDefinition parameter in callable.Parameters)
							AddType(parameter.Type, parameter.ResolvedType);
						break;
					case PointerTypeReference pointer:
						AddType(pointer.ElementType, pointer.ElementType?.ResolvedType);
						break;
					case ArrayTypeReference array:
						AddType(array.ElementType, array.ElementType?.ResolvedType);
						break;
					case OptionalTypeReference optional:
						AddType(optional.ElementType, optional.ElementType?.ResolvedType);
						break;
					case ConstTypeReference constant:
						AddType(constant.Type, constant.Type?.ResolvedType);
						break;
					case VolatileTypeReference vol:
						AddType(vol.Type, vol.Type?.ResolvedType);
						break;
					case TargetTypeSpecTypeReference targetSpec:
						AddType(targetSpec.Type, targetSpec.Type?.ResolvedType);
						break;
					case GenericTypeReference generic:
						AddType(generic.Type, generic.Type?.ResolvedType);
						foreach (TypeReference argument in generic.TypeArguments)
							AddType(argument, argument.ResolvedType);
						break;
					case TypeDefinitionReference definition:
						foreach (TypeReference argument in definition.TypeArguments)
							AddType(argument, argument.ResolvedType);
						break;
				}
			}

			void AddNode(BindableNode node)
			{
				switch (node)
				{
					case DeclarationStatement declaration:
						AddType(declaration.Target.Type, declaration.Target.ResolvedType);
						break;
					case DeclarationTarget target:
						AddType(target.Type, target.ResolvedType);
						break;
					case ArgumentExpression argument:
						AddType(argument.Type, argument.ResolvedType);
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

		static HashSet<string> BuildGenericParameterNameSet(Compilation compilation)
		{
			HashSet<string> names = new(StringComparer.Ordinal);
			foreach (Definition definition in compilation.SharedModule?.Definitions ?? [])
				AddDefinition(definition);
			return names;

			void AddDefinition(Definition definition)
			{
				if (definition is TypeDefinition type)
				{
					foreach (GenericParameter parameter in type.GenericParameters)
						names.Add(parameter.Name);
					foreach (FunctionDefinition function in type switch
					{
						ClassDefinition classDefinition => classDefinition.Functions,
						StructDefinition structDefinition => structDefinition.Functions,
						InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
						EnumDefinition enumDefinition => enumDefinition.Functions,
						NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions,
						ParamsDefinition paramsDefinition => paramsDefinition.Functions,
						_ => []
					})
						AddFunction(function);
				}
				else if (definition is FunctionDefinition function)
				{
					AddFunction(function);
				}
			}

			void AddFunction(FunctionDefinition function)
			{
				foreach (GenericParameter parameter in function.GenericParameters)
					names.Add(parameter.Name);
			}
		}

		static HashSet<string> BuildAnyGenericParameterNameSet(Compilation compilation)
		{
			HashSet<string> names = new(StringComparer.Ordinal);
			foreach (Definition definition in compilation.SharedModule?.Definitions ?? [])
				AddDefinition(definition);
			return names;

			void AddDefinition(Definition definition)
			{
				if (definition is TypeDefinition type)
				{
					foreach (GenericParameter parameter in type.GenericParameters)
						AddParameter(parameter);
					foreach (FunctionDefinition function in type switch
					{
						ClassDefinition classDefinition => classDefinition.Functions,
						StructDefinition structDefinition => structDefinition.Functions,
						InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
						EnumDefinition enumDefinition => enumDefinition.Functions,
						NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions,
						ParamsDefinition paramsDefinition => paramsDefinition.Functions,
						_ => []
					})
						AddFunction(function);
				}
				else if (definition is FunctionDefinition function)
				{
					AddFunction(function);
				}
			}

			void AddFunction(FunctionDefinition function)
			{
				foreach (GenericParameter parameter in function.GenericParameters)
					AddParameter(parameter);
			}

			void AddParameter(GenericParameter parameter)
			{
				if (parameter.Constraint is AnyTypeReference)
					names.Add(parameter.Name);
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
				writer.WriteLine(FormatCallableTypedef(callable, definition.Parameters, name) + ";");
				return;
			}

			CType type = FormatType(definition.UnderlyingType, name);
			writer.WriteLine("typedef " + type.Declaration + ";");
		}

		void WriteCallableAliasTypedef(TextWriter writer, string resolvedType)
		{
			string name = CTypeName(resolvedType);
			if (!emittedNames.Add("callable-typedef:" + name))
				return;
			if (!TryParseResolvedCallableType(resolvedType, out string returnType, out List<string> parameterTypes))
				return;

			string declarator = "(* " + name + ")";
			writer.WriteLine("typedef " + FormatResolvedType(returnType, declarator).Declaration + "(" + FormatResolvedParameterList(parameterTypes) + ");");
		}

		string FormatResolvedParameterList(List<string> parameterTypes)
		{
			if (parameterTypes.Count == 0)
				return "void";
			List<string> parts = [];
			for (int i = 0; i < parameterTypes.Count; i++)
			{
				string parameterType = parameterTypes[i];
				string declarator = "arg" + i.ToString(CultureInfo.InvariantCulture);
				if (parameterType.StartsWith("in ", StringComparison.Ordinal))
					parts.Add(FormatResolvedType(parameterType[3..].TrimStart() + "*", declarator).Declaration);
				else if (parameterType.StartsWith("out ", StringComparison.Ordinal))
					parts.Add(FormatResolvedType(parameterType[4..].TrimStart() + "*", declarator).Declaration);
				else if (parameterType.StartsWith("thrown ", StringComparison.Ordinal))
					parts.Add(FormatResolvedType(parameterType[7..].TrimStart() + "*", declarator).Declaration);
				else if (parameterType.StartsWith("within ", StringComparison.Ordinal))
					parts.Add(FormatResolvedType(parameterType[7..].TrimStart(), declarator).Declaration);
				else
					parts.Add(FormatResolvedType(parameterType, declarator).Declaration);
			}
			return string.Join(", ", parts);
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
					WriteFieldLayout(writer, classDefinition, GetClassLayoutFields(classDefinition));
					break;
				case InterfaceDefinition interfaceDefinition:
					WriteInterfaceLayout(writer, interfaceDefinition);
					break;
			}
		}

		List<FieldDefinition> GetClassLayoutFields(ClassDefinition classDefinition)
		{
			List<FieldDefinition> fields = [];
			AddClassLayoutFields(classDefinition, fields);
			return fields;
		}

		void AddClassLayoutFields(ClassDefinition classDefinition, List<FieldDefinition> fields)
		{
			if (GetDirectBaseClass(classDefinition) is ClassDefinition baseClass)
				AddClassLayoutFields(baseClass, fields);
			fields.AddRange(classDefinition.Fields);
		}

		ClassDefinition? GetDirectBaseClass(ClassDefinition classDefinition)
		{
			foreach (TypeReference baseType in classDefinition.BaseTypes)
			{
				string name = ResolveTypeDefinitionName(baseType);
				if (string.IsNullOrWhiteSpace(name))
					continue;
				foreach (Definition definition in GetDefinitions())
					if (definition is ClassDefinition candidate && candidate.Name == name)
						return candidate;
			}
			return null;
		}

		static string ResolveTypeDefinitionName(TypeReference? type)
		{
			return type switch
			{
				null => "",
				TypeDefinitionReference { Definition: not null } reference => reference.Definition.Name,
				TypeDefinitionReference reference => reference.Name,
				NamedTypeReference named => named.Name,
				GenericTypeReference generic => ResolveTypeDefinitionName(generic.Type),
				PointerTypeReference pointer => ResolveTypeDefinitionName(pointer.ElementType),
				ConstTypeReference constant => ResolveTypeDefinitionName(constant.Type),
				VolatileTypeReference vol => ResolveTypeDefinitionName(vol.Type),
				EscapedTypeReference escaped => ResolveTypeDefinitionName(escaped.Type),
				ScopedTypeReference scoped => ResolveTypeDefinitionName(scoped.Type),
				UnscopedTypeReference unscoped => ResolveTypeDefinitionName(unscoped.Type),
				_ => type.ResolvedType ?? ""
			};
		}

		void WriteFieldLayout(TextWriter writer, TypeDefinition type, List<FieldDefinition> fields)
		{
			string name = CName(type);
			if (!emittedNames.Add("layout:" + name))
				return;

			WithGenericContext(type, () =>
			{
				WithArrayElementComponentContext(fields, () =>
				{
					writer.WriteLine("struct " + name);
					writer.WriteLine("{");
					if (fields.Count == 0)
						writer.WriteLine("\tchar _camp_empty;");
					foreach (FieldDefinition field in fields.Where(static field => field.Modifier != FieldModifier.Static))
						writer.WriteLine("\t" + FormatTypeOrResolved(field.Type, field.ResolvedType, CName(field)).Declaration + ";");
					writer.WriteLine("};");
				});
			});
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

			string prefix = BuildDeclarationPrefix(function, storage);
			string callSpec = FormatCallSpec(function.CallSpec);
			if (callSpec.Length > 0)
				callSpec += " ";
			WithGenericContext(function, () =>
			{
				writer.WriteLine(prefix + FormatTypeOrResolved(function.ReturnType, function.ResolvedType, callSpec + name).Declaration + FormatParameters(function) + ";");
			});
		}

		void WriteVariableDeclaration(TextWriter writer, VariableDefinition variable, string? storage)
		{
			string prefix = BuildDeclarationPrefix(variable, storage);
			writer.WriteLine(prefix + FormatTypeOrResolved(variable.Type, variable.ResolvedType, CName(variable)).Declaration + ";");
		}

		void WriteVariableDefinition(TextWriter writer, VariableDefinition variable, string? storage)
		{
			string prefix = BuildDeclarationPrefix(variable, storage);
			writer.Write(prefix + FormatTypeOrResolved(variable.Type, variable.ResolvedType, CName(variable)).Declaration);
			if (variable.InitialValue is not null)
				writer.Write(" = " + FormatExpression(variable.InitialValue));
			writer.WriteLine(";");
		}

		void WriteFieldStorageDeclaration(TextWriter writer, FieldDefinition field, string? storage)
		{
			string prefix = BuildDeclarationPrefix(field, storage);
			writer.WriteLine(prefix + FormatTypeOrResolved(field.Type, field.ResolvedType, CName(field)).Declaration + ";");
		}

		void WriteFieldStorageDefinition(TextWriter writer, FieldDefinition field, string? storage)
		{
			string prefix = BuildDeclarationPrefix(field, storage);
			writer.Write(prefix + FormatTypeOrResolved(field.Type, field.ResolvedType, CName(field)).Declaration);
			if (field.InitialValue is not null)
				writer.Write(" = " + FormatExpression(field.InitialValue));
			writer.WriteLine(";");
		}

		void WriteFunctionDefinition(TextWriter writer, FunctionDefinition function, string? storage)
		{
			string prefix = BuildDeclarationPrefix(function, storage);
			string callSpec = FormatCallSpec(function.CallSpec);
			if (callSpec.Length > 0)
				callSpec += " ";
			WithGenericContext(function, () =>
			{
				writer.WriteLine(prefix + FormatTypeOrResolved(function.ReturnType, function.ResolvedType, callSpec + CName(function)).Declaration + FormatParameters(function));
				WriteFunctionBody(writer, function);
				writer.WriteLine();
			});
		}

		string BuildDeclarationPrefix(Definition definition, string? storage)
		{
			List<string> parts = [];
			if (!string.IsNullOrWhiteSpace(storage))
				parts.Add(storage);
			if (definition.Export is not null && storage is not "static" && !string.IsNullOrWhiteSpace(sharedExportPrefix))
				parts.Add(sharedExportPrefix);
			return parts.Count == 0 ? "" : string.Join(" ", parts) + " ";
		}

		void WithGenericContext(FunctionDefinition function, Action action)
		{
			HashSet<string> previous = new(currentGenericTypeNames, StringComparer.Ordinal);
			HashSet<string> previousAny = new(currentAnyGenericTypeNames, StringComparer.Ordinal);
			currentGenericTypeNames.Clear();
			currentAnyGenericTypeNames.Clear();
			foreach (GenericParameter parameter in function.GenericParameters)
			{
				currentGenericTypeNames.Add(parameter.Name);
				if (parameter.Constraint is AnyTypeReference)
					currentAnyGenericTypeNames.Add(parameter.Name);
			}
			if (containingTypes.TryGetValue(function, out TypeDefinition? containingType))
				foreach (GenericParameter parameter in containingType.GenericParameters)
				{
					currentGenericTypeNames.Add(parameter.Name);
					if (parameter.Constraint is AnyTypeReference)
						currentAnyGenericTypeNames.Add(parameter.Name);
				}

			try
			{
				action();
			}
			finally
			{
				currentGenericTypeNames.Clear();
				foreach (string name in previous)
					currentGenericTypeNames.Add(name);
				currentAnyGenericTypeNames.Clear();
				foreach (string name in previousAny)
					currentAnyGenericTypeNames.Add(name);
			}
		}

		void WithGenericContext(TypeDefinition type, Action action)
		{
			HashSet<string> previous = new(currentGenericTypeNames, StringComparer.Ordinal);
			HashSet<string> previousAny = new(currentAnyGenericTypeNames, StringComparer.Ordinal);
			currentGenericTypeNames.Clear();
			currentAnyGenericTypeNames.Clear();
			foreach (GenericParameter parameter in type.GenericParameters)
			{
				currentGenericTypeNames.Add(parameter.Name);
				if (parameter.Constraint is AnyTypeReference)
					currentAnyGenericTypeNames.Add(parameter.Name);
			}

			try
			{
				action();
			}
			finally
			{
				currentGenericTypeNames.Clear();
				foreach (string name in previous)
					currentGenericTypeNames.Add(name);
				currentAnyGenericTypeNames.Clear();
				foreach (string name in previousAny)
					currentAnyGenericTypeNames.Add(name);
			}
		}

		void WithArrayElementComponentContext<T>(IEnumerable<T> declarations, Action action)
			where T : BindableNode
		{
			HashSet<string> previous = new(currentArrayElementComponentNames, StringComparer.Ordinal);
			currentArrayElementComponentNames.Clear();
			List<(string Name, string Type)> candidates = [];
			HashSet<string> names = new(StringComparer.Ordinal);
			foreach (T declaration in declarations)
			{
				if (!TryGetDeclarationNameAndType(declaration, out string? name, out string? type) || string.IsNullOrWhiteSpace(name))
					continue;

				names.Add(name);
				if (!string.IsNullOrWhiteSpace(type))
					candidates.Add((name, type));
			}

			foreach ((string name, string type) in candidates)
			{
				if (type.TrimEnd().EndsWith("*", StringComparison.Ordinal) && names.Contains(name + "_length"))
					currentArrayElementComponentNames.Add(name);
			}

			try
			{
				action();
			}
			finally
			{
				currentArrayElementComponentNames.Clear();
				foreach (string name in previous)
					currentArrayElementComponentNames.Add(name);
			}
		}

		static bool TryGetDeclarationNameAndType(BindableNode declaration, out string? name, out string? type)
		{
			switch (declaration)
			{
				case FieldDefinition field:
					name = CName(field);
					type = field.ResolvedType;
					return true;
				case ParameterDefinition parameter:
					name = CName(parameter);
					type = parameter.ResolvedType;
					return true;
				default:
					name = null;
					type = null;
					return false;
			}
		}

		void WriteFunctionBody(TextWriter writer, FunctionDefinition function)
		{
			FunctionDefinition? previousFunction = currentFunction;
			bool previousFunctionHasLabels = currentFunctionHasLabels;
			currentFunction = function;
			currentFunctionHasLabels = FunctionContainsLabels(function);
			writer.WriteLine("{");
			if (NeedsAbiThisFixup(function))
			{
				WriteIndent(writer, 1);
				writer.Write(FormatTypeOrResolved(function.ImplementationThisType, function.ImplementationThisType?.ResolvedType, "this").Declaration);
				writer.Write(" = (");
				writer.Write(FormatTypeOrResolved(function.ImplementationThisType, function.ImplementationThisType?.ResolvedType, "").Declaration.Trim());
				writer.Write(")(ctx);");
				writer.WriteLine();
				WriteIndent(writer, 1);
				writer.WriteLine("(void)this;");
			}
			foreach (Statement statement in function.Body!.Statements)
				WriteStatement(writer, statement, 1);
			writer.WriteLine("}");
			currentFunction = previousFunction;
			currentFunctionHasLabels = previousFunctionHasLabels;
		}

		static bool FunctionContainsLabels(FunctionDefinition function)
		{
			return function.Body is not null && StatementContainsLabels(function.Body);
		}

		static bool StatementContainsLabels(Statement? statement)
		{
			return statement switch
			{
				null => false,
				LabelStatement => true,
				BlockStatement block => block.Statements.Any(StatementContainsLabels),
				IfStatement ifStatement => StatementContainsLabels(ifStatement.Body) || StatementContainsLabels(ifStatement.ElseBody),
				WhileStatement whileStatement => StatementContainsLabels(whileStatement.Body),
				DoWhileStatement doWhile => StatementContainsLabels(doWhile.Body),
				ForStatement forStatement => StatementContainsLabels(forStatement.Condition.Declaration) || StatementContainsLabels(forStatement.Body),
				ForeachStatement foreachStatement => StatementContainsLabels(foreachStatement.Body),
				SwitchStatement switchStatement => switchStatement.Statements.Any(StatementContainsLabels),
				CaseStatement => false,
				TryStatement tryStatement => StatementContainsLabels(tryStatement.Body)
					|| tryStatement.Catches.Any(StatementContainsLabels)
					|| StatementContainsLabels(tryStatement.Finally),
				CatchStatement catchStatement => StatementContainsLabels(catchStatement.Body),
				FinallyStatement finallyStatement => StatementContainsLabels(finallyStatement.Body),
				WithinStatement withinStatement => StatementContainsLabels(withinStatement.Body),
				_ => false
			};
		}

		void WriteBlock(TextWriter writer, BlockStatement block, int indent, bool forceBraces)
		{
			if (forceBraces)
			{
				WriteIndent(writer, indent);
				writer.WriteLine("{");
			}
			foreach (Statement statement in block.Statements)
				WriteStatement(writer, statement, forceBraces ? indent + 1 : indent);
			if (forceBraces)
			{
				WriteIndent(writer, indent);
				writer.WriteLine("}");
			}
		}

		void WriteStatement(TextWriter writer, Statement statement, int indent)
		{
			switch (statement)
			{
				case EmptyStatement:
					WriteIndent(writer, indent);
					writer.WriteLine(";");
					break;
				case BlockStatement block:
					WriteBlock(writer, block, indent, forceBraces: true);
					break;
				case ExpressionStatement expression:
					WriteIndent(writer, indent);
					writer.WriteLine(FormatExpression(expression.Expression) + ";");
					break;
				case DeclarationStatement declaration:
					WriteDeclarationStatement(writer, declaration, indent);
					break;
				case ReturnStatement ret:
					WriteIndent(writer, indent);
					writer.Write("return");
					if (ret.Expression is not null)
						writer.Write(" " + FormatExpression(ret.Expression));
					writer.WriteLine(";");
					break;
				case IfStatement ifStatement:
					WriteIfStatement(writer, ifStatement, indent);
					break;
				case WhileStatement whileStatement:
					WriteIndent(writer, indent);
					writer.WriteLine("while (" + FormatExpression(whileStatement.Condition) + ")");
					WriteEmbeddedStatement(writer, whileStatement.Body, indent);
					break;
				case DoWhileStatement doWhile:
					WriteIndent(writer, indent);
					writer.WriteLine("do");
					WriteEmbeddedStatement(writer, doWhile.Body, indent);
					WriteIndent(writer, indent);
					writer.WriteLine("while (" + FormatExpression(doWhile.Condition) + ");");
					break;
				case ForStatement forStatement:
					WriteForStatement(writer, forStatement, indent);
					break;
				case BreakStatement:
					WriteIndent(writer, indent);
					writer.WriteLine("break;");
					break;
				case ContinueStatement:
					WriteIndent(writer, indent);
					writer.WriteLine("continue;");
					break;
				case LabelStatement label:
					writer.WriteLine(SanitizeIdentifier(label.Name ?? "label") + ": ;");
					break;
				case GotoStatement go:
					WriteIndent(writer, indent);
					writer.WriteLine("goto " + SanitizeIdentifier(go.Target?.Name ?? go.TargetName ?? "label") + ";");
					break;
				case SwitchStatement switchStatement:
					WriteIndent(writer, indent);
					writer.WriteLine("switch (" + FormatExpression(switchStatement.Expression) + ")");
					WriteIndent(writer, indent);
					writer.WriteLine("{");
					foreach (Statement child in switchStatement.Statements)
						WriteStatement(writer, child, indent + 1);
					WriteIndent(writer, indent);
					writer.WriteLine("}");
					break;
				case CaseStatement caseStatement:
					WriteIndent(writer, Math.Max(0, indent - 1));
					writer.WriteLine("case " + FormatExpression(caseStatement.Expression) + ":");
					break;
				case DefaultStatement:
					WriteIndent(writer, Math.Max(0, indent - 1));
					writer.WriteLine("default:");
					break;
				default:
					AddUnsupported(statement, "statement");
					WriteIndent(writer, indent);
					writer.WriteLine("/* unsupported " + statement.GetType().Name + " */");
					break;
			}
		}

		void WriteDeclarationStatement(TextWriter writer, DeclarationStatement declaration, int indent)
		{
			string name = declaration.Target.Names.Count == 0 ? "__unnamed" : SanitizeIdentifier(declaration.Target.Names[0]);
			if (IsAnyGenericParameterType(declaration.Target.ResolvedType))
			{
				string size = FormatGenericSizeExpression(declaration.Target.ResolvedType);
				WriteIndent(writer, indent);
				if (currentFunctionHasLabels)
					writer.WriteLine("uint8_t *" + name + " = __builtin_alloca(" + size + ");");
				else
					writer.WriteLine("uint8_t " + name + "[" + size + "];");
				if (declaration.InitialValue is DefaultExpression)
				{
					WriteIndent(writer, indent);
					writer.WriteLine("__builtin_memset(" + name + ", 0, " + size + ");");
				}
				else if (declaration.InitialValue is not null)
				{
					WriteIndent(writer, indent);
					writer.WriteLine("__builtin_memcpy(" + name + ", " + FormatGenericStorageSource(declaration.InitialValue) + ", " + size + ");");
				}
				return;
			}
			string type = FormatTypeOrResolved(declaration.Target.Type, declaration.Target.ResolvedType, name).Declaration;
			WriteIndent(writer, indent);
			writer.Write(type);
			if (declaration.InitialValue is not null)
				writer.Write(" = " + FormatAssignmentValueForTarget(declaration.Target.ResolvedType, declaration.InitialValue));
			writer.WriteLine(";");
		}

		void WriteIfStatement(TextWriter writer, IfStatement ifStatement, int indent)
		{
			WriteIndent(writer, indent);
			writer.WriteLine("if (" + FormatExpression(ifStatement.Condition) + ")");
			WriteEmbeddedStatement(writer, ifStatement.Body, indent);
			if (ifStatement.ElseBody is not null)
			{
				WriteIndent(writer, indent);
				writer.WriteLine("else");
				WriteEmbeddedStatement(writer, ifStatement.ElseBody, indent);
			}
		}

		void WriteForStatement(TextWriter writer, ForStatement forStatement, int indent)
		{
			string initializer = "";
			if (forStatement.Condition.Declaration is DeclarationStatement declaration)
				initializer = FormatDeclarationForClause(declaration);
			else if (forStatement.Condition.Clauses.Count > 0 && forStatement.Condition.Clauses[0] is not null)
				initializer = FormatExpression(forStatement.Condition.Clauses[0]);
			int conditionIndex = forStatement.Condition.Declaration is null ? 1 : 0;
			int incrementIndex = forStatement.Condition.Declaration is null ? 2 : 1;
			string condition = forStatement.Condition.Clauses.Count > conditionIndex && forStatement.Condition.Clauses[conditionIndex] is not null ? FormatExpression(forStatement.Condition.Clauses[conditionIndex]) : "";
			string increment = forStatement.Condition.Clauses.Count > incrementIndex && forStatement.Condition.Clauses[incrementIndex] is not null ? FormatExpression(forStatement.Condition.Clauses[incrementIndex]) : "";

			WriteIndent(writer, indent);
			writer.WriteLine("for (" + initializer + "; " + condition + "; " + increment + ")");
			WriteEmbeddedStatement(writer, forStatement.Body, indent);
		}

		string FormatDeclarationForClause(DeclarationStatement declaration)
		{
			string name = declaration.Target.Names.Count == 0 ? "__unnamed" : SanitizeIdentifier(declaration.Target.Names[0]);
			string text = FormatTypeOrResolved(declaration.Target.Type, declaration.Target.ResolvedType, name).Declaration;
			if (declaration.InitialValue is not null)
				text += " = " + FormatExpression(declaration.InitialValue);
			return text;
		}

		void WriteEmbeddedStatement(TextWriter writer, Statement? statement, int indent)
		{
			if (statement is null)
			{
				WriteIndent(writer, indent);
				writer.WriteLine("{}");
				return;
			}
			if (statement is BlockStatement block)
			{
				WriteBlock(writer, block, indent, forceBraces: true);
				return;
			}
			WriteIndent(writer, indent);
			writer.WriteLine("{");
			WriteStatement(writer, statement, indent + 1);
			WriteIndent(writer, indent);
			writer.WriteLine("}");
		}

		string FormatExpression(Expression? expression)
		{
			if (expression is null)
				return "0";

			return expression switch
			{
				LiteralExpression literal => FormatLiteral(literal),
				VariableReferenceExpression variable => FormatVariableReference(variable.Variable),
				NamedExpression named => SanitizeIdentifier(named.Name),
				MethodReferenceExpression method => method.Candidates.Count == 1 ? CName(method.Candidates[0]) : UnsupportedExpression(expression),
				TypeReferenceExpression type => FormatTypeReferenceExpression(type),
				ThisExpression => FormatThisExpression(),
				DefaultExpression defaultExpression => FormatDefaultExpression(defaultExpression),
				ParenthesizedExpression parenthesized => "(" + FormatExpression(parenthesized.Expression) + ")",
				CastExpression cast => "(" + FormatType(cast.Type, "").Declaration.Trim() + ")(" + FormatExpression(cast.Expression) + ")",
				SizeOfExpression sizeOf => FormatSizeOfExpression(sizeOf),
				CallExpression call => FormatCallExpression(call),
				IndexExpression index => FormatExpression(index.Target) + "[" + string.Join(", ", index.Arguments.Select(FormatArgumentValue)) + "]",
				MemberExpression member => FormatExpandedThisComponent(member) ?? FormatExpression(member.Target) + (IsPointerMemberTarget(member.Target) ? "->" : ".") + SanitizeIdentifier(member.Name),
				MemberReferenceExpression member => FormatMemberReference(member),
				UnaryExpression unary => FormatUnaryExpression(unary),
				PostfixUpdateExpression postfix => FormatExpression(postfix.Expression) + FormatUpdateOperator(postfix.Operator),
				BinaryExpression binary => "(" + FormatExpression(binary.Left) + " " + FormatBinaryOperator(binary.Operator) + " " + FormatExpression(binary.Right) + ")",
				AssignmentExpression assignment => FormatAssignmentExpression(assignment),
				ConditionalExpression conditional => "(" + FormatExpression(conditional.Condition) + " ? " + FormatExpression(conditional.WhenTrue) + " : " + FormatExpression(conditional.WhenFalse) + ")",
				InitializerExpression initializer => FormatInitializer(initializer),
				RangeExpression => UnsupportedExpression(expression),
				GroupedExpression grouped => FormatGroupedExpression(grouped),
				ArrayExpression array => FormatArrayExpression(array),
				ConstructionExpression => UnsupportedExpression(expression),
				CurrentAllocatorExpression => UnsupportedExpression(expression),
				WithinExpression => UnsupportedExpression(expression),
				VTableOfExpression => UnsupportedExpression(expression),
				LambdaExpression => UnsupportedExpression(expression),
				ArgumentExpression argument => FormatArgumentValue(argument),
				NamelessIndexerExpression indexer => FormatExpression(indexer.Target) + "[" + string.Join(", ", indexer.Arguments.Select(FormatArgumentValue)) + "]",
				FinallyDeleteExpression finallyDelete => FormatExpression(finallyDelete.Expression),
				_ => UnsupportedExpression(expression)
			};
		}

		string FormatDefaultExpression(DefaultExpression expression)
		{
			string? resolvedType = expression.ResolvedType ?? expression.Type?.ResolvedType;
			if (!IsAggregateValueType(resolvedType))
				return "0";

			string type = FormatTypeOrResolved(expression.Type, resolvedType, "").Declaration.Trim();
			return "(" + type + "){0}";
		}

		string FormatSizeOfExpression(SizeOfExpression sizeOf)
		{
			string? resolvedType = sizeOf.Type?.ResolvedType;
			if (IsAnyGenericParameterType(resolvedType))
				return FormatGenericSizeExpression(resolvedType);
			if (IsExpandedStorageResolvedType(resolvedType))
				return "sizeof(" + FormatStorageResolvedType(resolvedType ?? "", "").Declaration.Trim() + ")";
			return "sizeof(" + FormatType(sizeOf.Type, "").Declaration.Trim() + ")";
		}

		static bool IsExpandedStorageResolvedType(string? resolvedType)
		{
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;
			return TryGetArrayElementOnly(resolvedType, out _)
				|| TryGetOptionalElementOnly(resolvedType, out _)
				|| TryParseExpandedCallableStorageType(resolvedType, out _, out _);
		}

		string FormatMaterializedArrayStructType(string elementType)
		{
			return "struct { " + FormatResolvedType(elementType + "*", "elements").Declaration + "; uintptr_t length; }";
		}

		CType FormatMaterializedStructType(MaterializedStructTypeReference materialized, string declarator)
		{
			string expandedType = materialized.ParamsType?.ResolvedType ?? "";
			if (expandedType.StartsWith("struct(", StringComparison.Ordinal) && expandedType.EndsWith(")", StringComparison.Ordinal))
				expandedType = expandedType[7..^1];
			return FormatStorageResolvedType(expandedType, declarator);
		}

		CType FormatStorageResolvedType(string resolvedType, string declarator)
		{
			string type = resolvedType.Trim();
			if (TryGetArrayElementOnly(type, out string elementType))
				return new CType(FormatMaterializedArrayStructType(elementType) + " " + declarator);
			if (TryGetOptionalElementOnly(type, out string optionalElementType))
				return new CType("struct { " + FormatResolvedType(optionalElementType, "value").Declaration + "; bool specified; } " + declarator);
			if (TryParseExpandedCallableStorageType(type, out string returnType, out List<string> parameterTypes))
				return new CType("struct { " + FormatInlineResolvedFunctionPointer(returnType, [ "void*", .. parameterTypes ], "call") + "; void* context; } " + declarator);
			return FormatResolvedType(resolvedType, declarator);
		}

		string FormatInlineResolvedFunctionPointer(string returnType, List<string> parameterTypes, string declarator)
		{
			return FormatResolvedType(returnType, "(* " + declarator + ")").Declaration + "(" + FormatResolvedParameterList(parameterTypes) + ")";
		}

		bool TryFormatResolvedCallableCast(string resolvedType, out string cast)
		{
			cast = "";
			if (!TryParseResolvedCallableType(resolvedType, out string returnType, out List<string> parameterTypes))
				return false;
			cast = FormatInlineResolvedFunctionPointer(returnType, parameterTypes, "");
			return true;
		}

		bool ShouldCastCallableAssignment(Expression value, string targetType)
		{
			if (!TryParseResolvedCallableType(targetType, out string targetReturn, out List<string> targetParameters))
				return false;
			if (!TryParseResolvedCallableType(value.ResolvedType ?? "", out string sourceReturn, out List<string> sourceParameters))
				return true;
			if (targetReturn != sourceReturn || targetParameters.Count != sourceParameters.Count)
				return true;
			for (int i = 0; i < targetParameters.Count; i++)
				if (targetParameters[i] != sourceParameters[i])
					return true;
			return false;
		}

		static bool IsCallableSymbolExpression(Expression? expression)
		{
			return expression is MethodReferenceExpression
				or MemberReferenceExpression { Member: FunctionDefinition }
				or VariableReferenceExpression { Variable: FunctionDefinition }
				or NamedExpression;
		}

		static bool TryParseExpandedCallableStorageType(string resolvedType, out string returnType, out List<string> parameterTypes)
		{
			returnType = "";
			parameterTypes = [];
			string type = resolvedType.Trim();
			string kind;
			if (type.StartsWith("delegate ", StringComparison.Ordinal))
				kind = "delegate";
			else if (type.StartsWith("once ", StringComparison.Ordinal))
				kind = "once";
			else
				return false;

			int open = type.IndexOf('(', StringComparison.Ordinal);
			int close = type.LastIndexOf(')');
			if (open < 0 || close < open)
				return false;
			string prefix = type[kind.Length..open].Trim();
			if (prefix.Length == 0)
				return false;
			List<string> prefixParts = SplitTopLevel(prefix, ' ');
			returnType = prefixParts[^1];
			string parameterText = type[(open + 1)..close].Trim();
			if (parameterText.Length == 0)
				return true;
			foreach (string parameter in SplitTopLevel(parameterText, ','))
				parameterTypes.Add(parameter.Trim());
			return true;
		}

		static bool TryGetArrayElementOnly(string? resolvedType, out string elementType)
		{
			elementType = "";
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;
			string type = resolvedType.Trim();
			if (!type.EndsWith("[]", StringComparison.Ordinal))
				return false;
			elementType = type[..^2].TrimEnd();
			return !string.IsNullOrWhiteSpace(elementType);
		}

		static bool TryGetOptionalElementOnly(string? resolvedType, out string elementType)
		{
			elementType = "";
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;
			string type = resolvedType.Trim();
			if (!type.EndsWith("?", StringComparison.Ordinal))
				return false;
			elementType = type[..^1].TrimEnd();
			return !string.IsNullOrWhiteSpace(elementType);
		}

		bool IsAggregateValueType(string? resolvedType)
		{
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;

			string type = StripTypeQualifiers(resolvedType.Trim());
			if (type.EndsWith("*", StringComparison.Ordinal) || type.EndsWith("[]", StringComparison.Ordinal))
				return false;

			int genericStart = type.IndexOf('<');
			if (genericStart >= 0)
				type = type[..genericStart];

			foreach (Definition definition in GetDefinitions())
			{
				if (definition.Name != type && definition.Symbol != type)
					continue;

				return definition is StructDefinition or ClassDefinition;
			}

			return false;
		}

		static string StripTypeQualifiers(string type)
		{
			bool changed;
			do
			{
				changed = false;
				foreach (string prefix in new[] { "const ", "volatile ", "escaped ", "scoped ", "unscoped " })
				{
					if (!type.StartsWith(prefix, StringComparison.Ordinal))
						continue;

					type = type[prefix.Length..].TrimStart();
					changed = true;
				}

				foreach (string suffix in new[] { " const", " volatile" })
				{
					if (!type.EndsWith(suffix, StringComparison.Ordinal))
						continue;

					type = type[..^suffix.Length].TrimEnd();
					changed = true;
				}
			}
			while (changed);

			return type;
		}

		string FormatCallExpression(CallExpression call)
		{
			if (TryFormatOffsetOfCall(call, out string offsetOfText))
				return offsetOfText;

			string target = FormatExpression(call.Target);
			FunctionDefinition? function = TryGetCallFunction(call);
			if (function is not null
				&& containingTypes.TryGetValue(function, out TypeDefinition? owner)
				&& owner is InterfaceDefinition
				&& call.Arguments.Count > 0
				&& call.Arguments[0].Value is not null)
				target = "(*" + FormatExpression(call.Arguments[0].Value) + ")->" + SanitizeIdentifier(BindableNodeAnalyzer.GetCallableName(function));
			else if (TryFormatLoweredInterfaceSlotTarget(call, out string interfaceSlotTarget))
				target = interfaceSlotTarget;
			Dictionary<string, string> genericSubstitutions = function is null ? [] : GetCallGenericSubstitutions(call, function);
			List<ParameterDefinition> parameters = function is not null
				? GetCallableParametersForCall(function)
				: GetCallableParametersForExpression(call.Target);
			List<string> arguments = [];
			for (int i = 0; i < call.Arguments.Count; i++)
			{
				ParameterDefinition? parameter = i < parameters.Count ? parameters[i] : null;
				arguments.Add(FormatArgumentValue(call.Arguments[i], parameter, genericSubstitutions));
			}
			string text = target + "(" + string.Join(", ", arguments) + ")";
			if (function is not null && TryGetConcreteGenericType(function.ResolvedType, genericSubstitutions, out string? concreteReturnType))
			{
				if (NeedsGenericScalarCast(concreteReturnType))
					return CastFromErasedGeneric(text, concreteReturnType);
			}
			return text;
		}

		string FormatAssignmentExpression(AssignmentExpression assignment)
		{
			if (assignment.Operator == AssignmentOperator.Assign
				&& TryFormatGenericStorageAddress(assignment.Target, out string destination, out string genericType))
			{
				string size = FormatGenericSizeExpression(genericType);
				if (assignment.Value is DefaultExpression)
					return "__builtin_memset(" + destination + ", 0, " + size + ")";
				return "__builtin_memmove(" + destination + ", " + FormatGenericStorageSource(assignment.Value) + ", " + size + ")";
			}
			if (assignment.Operator == AssignmentOperator.Assign
				&& assignment.Target is VariableReferenceExpression { Variable: ParameterDefinition { Modifier: ParameterModifier.Out } parameter }
				&& IsAnyGenericParameterType(parameter.ResolvedType))
			{
				string size = FormatGenericSizeExpression(parameter.ResolvedType);
				return "__builtin_memcpy(" + CName(parameter) + ", " + FormatGenericStorageSource(assignment.Value) + ", " + size + ")";
			}
			string value = FormatExpression(assignment.Value);
			if (assignment.Operator == AssignmentOperator.Assign
				&& assignment.Target?.ResolvedType is string targetType)
				value = FormatAssignmentValueForTarget(targetType, assignment.Value!);
			return FormatExpression(assignment.Target) + " " + FormatAssignmentOperator(assignment.Operator) + " " + value;
		}

		string FormatAssignmentValueForTarget(string? targetType, Expression value)
		{
			string formatted = FormatExpression(value);
			if (targetType is not null
				&& TryFormatResolvedCallableCast(targetType, out string callableCast)
				&& IsCallableSymbolExpression(value)
				&& ShouldCastCallableAssignment(value, targetType))
			{
				return "(" + callableCast + ")" + formatted;
			}
			return formatted;
		}

		List<ParameterDefinition> GetCallableParametersForExpression(Expression? expression)
		{
			if (expression?.ResolvedType is not string resolvedType || !TryParseResolvedCallableType(resolvedType, out _, out List<string> parameterTypes))
				return [];

			List<ParameterDefinition> parameters = [];
			foreach (string parameterType in parameterTypes)
				parameters.Add(CreateCallableParameterFromResolvedType(parameterType));
			return parameters;
		}

		static ParameterDefinition CreateCallableParameterFromResolvedType(string parameterType)
		{
			string typeName = parameterType.Trim();
			ParameterModifier modifier = ParameterModifier.None;
			if (typeName.StartsWith("in ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.In;
				typeName = typeName[3..].TrimStart();
			}
			else if (typeName.StartsWith("out ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.Out;
				typeName = typeName[4..].TrimStart();
			}
			else if (typeName.StartsWith("thrown ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.Thrown;
				typeName = typeName[7..].TrimStart();
			}
			else if (typeName.StartsWith("within ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.Within;
				typeName = typeName[7..].TrimStart();
			}

			return new ParameterDefinition
			{
				Modifier = modifier,
				ResolvedType = typeName,
				Type = new NamedTypeReference { Name = typeName, ResolvedType = typeName }
			};
		}

		bool TryFormatOffsetOfCall(CallExpression call, out string text)
		{
			text = "";
			if (call.Target is not NamedExpression { Name: "offsetof", Qualifiers.Count: 0 }
				|| call.Arguments.Count != 1
				|| call.Arguments[0].Value is not MemberExpression { Target: TypeReferenceExpression type, Name: { Length: > 0 } field })
				return false;

			text = "offsetof(" + FormatTypeReferenceExpression(type) + ", " + SanitizeIdentifier(field) + ")";
			return true;
		}

		bool TryFormatLoweredInterfaceSlotTarget(CallExpression call, out string target)
		{
			target = "";
			if (call.Target is not NamedExpression named
				|| call.Arguments.Count == 0
				|| call.Arguments[0].Value is not Expression contextValue)
				return false;

			string? contextType = call.Arguments[0].ResolvedType ?? contextValue.ResolvedType;
			if (string.IsNullOrWhiteSpace(contextType) || !contextType.EndsWith("**", StringComparison.Ordinal))
				return false;

			string interfaceName = contextType[..^2].Trim();
			StructDefinition? interfaceStruct = null;
			foreach (Definition definition in GetDefinitions())
			{
				if (definition is StructDefinition candidate && candidate.Name == interfaceName)
				{
					interfaceStruct = candidate;
					break;
				}
			}
			if (interfaceStruct is null)
				return false;

			string slotName = named.Name;
			if (!HasField(interfaceStruct, slotName) && call.Arguments.Count > 1)
			{
				string selectorType = call.Arguments[1].ResolvedType ?? call.Arguments[1].Value?.ResolvedType ?? "";
				string fragment = BindableNodeAnalyzer.BuildFlattenedTypeFragment(selectorType);
				if (!string.IsNullOrWhiteSpace(fragment) && HasField(interfaceStruct, named.Name + fragment))
					slotName = named.Name + fragment;
			}

			if (!HasField(interfaceStruct, slotName))
				return false;

			target = "(*" + FormatExpression(contextValue) + ")->" + SanitizeIdentifier(slotName);
			return true;
		}

		static bool HasField(StructDefinition definition, string name)
		{
			foreach (FieldDefinition field in definition.Fields)
				if (field.Name == name || field.Symbol == name)
					return true;
			return false;
		}

		string FormatArgumentValue(ArgumentExpression argument)
		{
			return FormatArgumentValue(argument, parameter: null, genericSubstitutions: []);
		}

		string FormatTypeReferenceExpression(TypeReferenceExpression expression)
		{
			if (expression.Type is null)
				return UnsupportedExpression(expression);
			if (expression.Type is TypeDefinitionReference { Definition: not null } reference)
				return CName(reference.Definition);
			return FormatType(expression.Type, "").Declaration.Trim();
		}

		string FormatArrayExpression(ArrayExpression array)
		{
			if (!TryGetArrayLiteralElementType(array.ResolvedType, out string elementType))
			{
				AddUnsupported(array, "expression");
				return "/* unsupported array literal */ 0";
			}

			string cArrayType = FormatResolvedType(elementType, "[]").Declaration.Trim();
			return "(" + cArrayType + "){" + string.Join(", ", array.Elements.Select(FormatExpression)) + "}";
		}

		static bool TryGetArrayLiteralElementType(string? resolvedType, out string elementType)
		{
			elementType = "";
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;

			string type = resolvedType.Trim();
			if (type is "#ERROR" or "#MISSING" or "#UNRESOLVED")
				return false;

			if (type.EndsWith("[]", StringComparison.Ordinal))
			{
				elementType = type[..^2].TrimEnd();
				return !string.IsNullOrWhiteSpace(elementType);
			}

			if (type.EndsWith("*", StringComparison.Ordinal))
			{
				elementType = type[..^1].TrimEnd();
				return !string.IsNullOrWhiteSpace(elementType);
			}

			return false;
		}

		static bool TryGetExpandedArrayElementType(string? resolvedType, out string elementType)
		{
			elementType = "";
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;

			string type = resolvedType.Trim();
			if (!type.EndsWith("[]", StringComparison.Ordinal))
				return false;

			elementType = type[..^2].TrimEnd();
			return !string.IsNullOrWhiteSpace(elementType);
		}

		static bool TryGetExpandedArrayPointerElementType(string? resolvedType, out string elementType)
		{
			elementType = "";
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;

			string type = resolvedType.Trim();
			if (!type.EndsWith("[]*", StringComparison.Ordinal))
				return false;

			elementType = type[..^3].TrimEnd();
			return !string.IsNullOrWhiteSpace(elementType);
		}

		string FormatArgumentValue(ArgumentExpression argument, ParameterDefinition? parameter, Dictionary<string, string> genericSubstitutions)
		{
			string value = FormatExpression(argument.Value);
			string? rawExpectedParameterType = parameter?.ResolvedType ?? parameter?.Type?.ResolvedType;
			string? expectedParameterType = SubstituteGenericTypeTokens(rawExpectedParameterType, genericSubstitutions);
			if (argument.Modifier == ArgumentModifier.None
				&& expectedParameterType is not null
				&& IsVoidPointerType(expectedParameterType)
				&& argument.Value is IndexExpression genericIndex
				&& TryFormatGenericArrayElementAddress(genericIndex, out string genericElementAddress))
				return genericElementAddress;
			if (argument.Modifier == ArgumentModifier.None
				&& parameter?.Modifier != ParameterModifier.In
				&& TryGetConcreteGenericType(rawExpectedParameterType, genericSubstitutions, out string? concreteType)
				&& NeedsGenericScalarCast(concreteType))
				value = CastToErasedGeneric(value, concreteType);
			if (argument.Modifier == ArgumentModifier.None
				&& argument.Value is not null
				&& delegateThunksByExpression.TryGetValue(argument.Value, out DelegateThunk? thunk))
			{
				value = thunk.Name;
				if (rawExpectedParameterType is not null
					&& IsResolvedCallableType(rawExpectedParameterType)
					&& ContainsGenericParameterTypeName(rawExpectedParameterType))
					value = "(" + CTypeName(rawExpectedParameterType) + ")" + value;
			}
			if (argument.Modifier == ArgumentModifier.None
				&& expectedParameterType is not null
				&& IsResolvedCallableType(expectedParameterType)
				&& (argument.Value is null || !delegateThunksByExpression.ContainsKey(argument.Value))
				&& (argument.Value is MethodReferenceExpression or VariableReferenceExpression { Variable: FunctionDefinition } or NamedExpression
					|| argument.Value?.ResolvedType is string argumentType && IsResolvedCallableType(argumentType)))
				value = "(" + CTypeName(expectedParameterType) + ")" + value;
			if (argument.Modifier == ArgumentModifier.None
				&& expectedParameterType is not null
				&& argument.Value?.ResolvedType is string valueType
				&& ShouldCastPointerArgument(valueType, expectedParameterType))
				value = "(" + FormatTypeOrResolved(parameter?.Type, expectedParameterType, "").Declaration.Trim() + ")" + value;
			if (argument.Modifier == ArgumentModifier.None && parameter?.Modifier == ParameterModifier.In)
				return FormatInArgument(argument.Value, value, TryGetConcreteGenericType(expectedParameterType, genericSubstitutions, out string? concreteInType) ? concreteInType : expectedParameterType);
			return argument.Modifier switch
			{
				ArgumentModifier.Out or ArgumentModifier.Catch when TryFormatForwardedOutArgument(argument.Value, out string forwarded) => forwarded,
				ArgumentModifier.Out or ArgumentModifier.Catch => "&" + value,
				_ => value
			};
		}

		bool ContainsGenericParameterTypeName(string type)
		{
			for (int i = 0; i < type.Length;)
			{
				if (!IsIdentifierStart(type[i]))
				{
					i++;
					continue;
				}
				int start = i++;
				while (i < type.Length && IsIdentifierPart(type[i]))
					i++;
				string token = type[start..i];
				if (genericParameterNames.Contains(token) || currentGenericTypeNames.Contains(token))
					return true;
			}
			return false;
		}

		string FormatInArgument(Expression? expression, string value, string? concreteType)
		{
			if (TryFormatForwardedInArgument(expression, out string forwarded))
				return forwarded;

			string type = string.IsNullOrWhiteSpace(concreteType)
				? "void*"
				: FormatStorageResolvedType(concreteType, "").Declaration.Trim();
			return "&(" + type + "){" + value + "}";
		}

		bool TryFormatForwardedInArgument(Expression? expression, out string value)
		{
			value = "";
			switch (expression)
			{
				case IndexExpression index when TryFormatGenericArrayElementAddress(index, out string address):
					value = address;
					return true;
				case UnaryExpression { Operator: UnaryOperator.AddressOf } addressOf:
					value = FormatExpression(addressOf);
					return true;
				case VariableReferenceExpression { Variable: ParameterDefinition { Modifier: ParameterModifier.In } parameter }:
					value = CName(parameter);
					return true;
				case VariableReferenceExpression { Variable: ParameterDefinition parameter }:
					value = "&" + CName(parameter);
					return true;
				case VariableReferenceExpression { Variable: DeclarationTarget target } when IsAnyGenericParameterType(target.ResolvedType):
					value = CName(target);
					return true;
				case VariableReferenceExpression { Variable: DeclarationTarget { Type: MaterializedStructTypeReference } target }:
					value = "&" + CName(target);
					return true;
				case VariableReferenceExpression { Variable: DeclarationTarget target }:
					value = "&" + CName(target);
					return true;
				case ThisExpression when TryGetCurrentInThisParameter(out _):
					value = "this";
					return true;
				default:
					return false;
			}
		}

		bool TryFormatGenericArrayElementAddress(IndexExpression index, out string value)
		{
			value = "";
			string? arrayType = index.Target?.ResolvedType;
			if ((TryGetArrayElementType(arrayType, out string elementType) || TryGetPointerElementType(arrayType, out elementType))
				&& IsAnyGenericParameterType(StripTypeDecorators(elementType))
				&& index.Arguments.Count == 1)
			{
				value = "(void*)(" + FormatGenericArrayElementBytePointer(index, elementType) + ")";
				return true;
			}
			return false;
		}

		string FormatGenericArrayElementBytePointer(IndexExpression index, string elementType)
		{
			string target = FormatExpression(index.Target);
			string offset = FormatArgumentValue(index.Arguments[0]) + " * " + FormatGenericSizeExpression(elementType);
			string bytePointer = ElementTypeIsConst(elementType) ? "const uint8_t*" : "uint8_t*";
			return "((" + bytePointer + ")" + target + ") + (" + offset + ")";
		}

		bool IsGenericArrayElementIndex(IndexExpression index)
		{
			string? arrayType = index.Target?.ResolvedType;
			return (TryGetArrayElementType(arrayType, out string elementType) || TryGetPointerElementType(arrayType, out elementType))
				&& IsAnyGenericParameterType(StripTypeDecorators(elementType))
				&& index.Arguments.Count == 1;
		}

		static bool ElementTypeIsConst(string type)
		{
			type = type.Trim();
			return type.StartsWith("const ", StringComparison.Ordinal) || type.EndsWith(" const", StringComparison.Ordinal);
		}

		static bool TryGetArrayElementType(string? resolvedType, out string elementType)
		{
			elementType = "";
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;
			string type = resolvedType.Trim();
			if (!type.EndsWith("[]", StringComparison.Ordinal))
				return false;
			elementType = type[..^2].TrimEnd();
			return !string.IsNullOrWhiteSpace(elementType);
		}

		static bool TryGetPointerElementType(string? resolvedType, out string elementType)
		{
			elementType = "";
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;
			string type = resolvedType.Trim();
			if (!type.EndsWith("*", StringComparison.Ordinal))
				return false;
			elementType = type[..^1].TrimEnd();
			return !string.IsNullOrWhiteSpace(elementType);
		}

		static bool ShouldCastPointerArgument(string valueType, string expectedType)
		{
			if (valueType == expectedType)
				return false;
			if (!TryGetPointerElementType(valueType, out string valueElement) || !TryGetPointerElementType(expectedType, out string expectedElement))
				return false;
			return HasTopLevelConstForC(valueElement)
				&& !HasTopLevelConstForC(expectedElement)
				&& StripTopLevelConstForC(valueElement) == StripTopLevelConstForC(expectedElement);
		}

		static bool HasTopLevelConstForC(string type)
		{
			type = type.Trim();
			return type.StartsWith("const ", StringComparison.Ordinal) || type.EndsWith(" const", StringComparison.Ordinal);
		}

		static string StripTopLevelConstForC(string type)
		{
			type = type.Trim();
			if (type.StartsWith("const ", StringComparison.Ordinal))
				type = type["const ".Length..].TrimStart();
			if (type.EndsWith(" const", StringComparison.Ordinal))
				type = type[..^" const".Length].TrimEnd();
			return type;
		}

		bool TryFormatForwardedOutArgument(Expression? expression, out string value)
		{
			value = "";
			switch (expression)
			{
				case VariableReferenceExpression { Variable: ParameterDefinition { Modifier: ParameterModifier.Out or ParameterModifier.Thrown } parameter }:
					value = CName(parameter);
					return true;

				case VariableReferenceExpression { Variable: DeclarationTarget target } when IsAnyGenericParameterType(target.ResolvedType):
					value = CName(target);
					return true;

				case VariableReferenceExpression { Variable: DeclarationTarget target } when IsSyntheticCurrentOutParameterTarget(target) && TryFindCurrentOutParameter(target, out ParameterDefinition? parameter):
					value = CName(parameter);
					return true;

				case NamedExpression named when TryFindCurrentOutParameter(named.Name, out ParameterDefinition? parameter):
					value = CName(parameter);
					return true;

				default:
					return false;
			}
		}

		bool IsSyntheticCurrentOutParameterTarget(DeclarationTarget target)
		{
			return target.SourceSyntax is null && target.Names.Count == 1;
		}

		bool TryFindCurrentOutParameter(DeclarationTarget target, out ParameterDefinition parameter)
		{
			parameter = null!;
			return target.Names.Count == 1 && TryFindCurrentOutParameter(target.Names[0], out parameter);
		}

		bool TryFindCurrentOutParameter(string name, out ParameterDefinition parameter)
		{
			parameter = null!;
			if (currentFunction is null)
				return false;

			foreach (ParameterDefinition candidate in currentFunction.Parameters)
			{
				if (candidate.Modifier is not ParameterModifier.Out and not ParameterModifier.Thrown)
					continue;
				if (candidate.Name == name || candidate.Symbol == name)
				{
					parameter = candidate;
					return true;
				}
			}
			return false;
		}

		static FunctionDefinition? TryGetCallFunction(CallExpression call)
		{
			return call.Target switch
			{
				MethodReferenceExpression { Candidates.Count: 1 } method => method.Candidates[0],
				MemberReferenceExpression { Member: FunctionDefinition function } => function,
				_ => null
			};
		}

		Dictionary<string, string> GetCallGenericSubstitutions(CallExpression call, FunctionDefinition function)
		{
			Dictionary<string, string> substitutions = [];
			int typeArgumentCount = Math.Min(function.GenericParameters.Count, call.TypeArguments.Count);
			for (int i = 0; i < typeArgumentCount; i++)
				substitutions[function.GenericParameters[i].Name] = call.TypeArguments[i].ResolvedType ?? "void*";

			AddCallArgumentGenericSubstitutions(call, function, substitutions);

			if (containingTypes.TryGetValue(function, out TypeDefinition? containingType) && containingType.GenericParameters.Count > 0)
			{
				string? receiverType = call.Target is MemberReferenceExpression member
					? member.Target?.ResolvedType
					: RequiresImplicitThisParameter(function) && call.Arguments.Count > 0
						? call.Arguments[0].Value?.ResolvedType
						: null;
				if (receiverType is not null)
				{
					List<string> typeArguments = ExtractConstructedTypeArguments(receiverType);
					int count = Math.Min(containingType.GenericParameters.Count, typeArguments.Count);
					for (int i = 0; i < count; i++)
						substitutions[containingType.GenericParameters[i].Name] = typeArguments[i];
				}
			}

			return substitutions;
		}

		void AddCallArgumentGenericSubstitutions(CallExpression call, FunctionDefinition function, Dictionary<string, string> substitutions)
		{
			if (function.GenericParameters.Count == 0)
				return;

			HashSet<string> genericNames = new(function.GenericParameters.Select(static parameter => parameter.Name), StringComparer.Ordinal);
			List<ParameterDefinition> parameters = GetCallableParametersForCall(function);
			for (int i = 0; i < call.Arguments.Count && i < parameters.Count; i++)
			{
				string expected = parameters[i].ResolvedType ?? parameters[i].Type?.ResolvedType ?? "";
				string actual = call.Arguments[i].ResolvedType ?? call.Arguments[i].Value?.ResolvedType ?? "";
				TryInferGenericSubstitution(expected, actual, genericNames, substitutions);
			}
		}

		static bool TryInferGenericSubstitution(string expected, string actual, HashSet<string> genericNames, Dictionary<string, string> substitutions)
		{
			expected = StripTypeDecorators(expected);
			actual = StripTypeDecorators(actual);
			if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
				return false;

			if (genericNames.Contains(expected))
			{
				substitutions.TryAdd(expected, actual);
				return true;
			}

			if (expected.EndsWith("[]", StringComparison.Ordinal) && actual.EndsWith("[]", StringComparison.Ordinal))
				return TryInferGenericSubstitution(expected[..^2], actual[..^2], genericNames, substitutions);
			if (expected.EndsWith("*", StringComparison.Ordinal) && actual.EndsWith("*", StringComparison.Ordinal))
				return TryInferGenericSubstitution(expected[..^1], actual[..^1], genericNames, substitutions);

			return false;
		}

		List<ParameterDefinition> GetCallableParametersForCall(FunctionDefinition function)
		{
			List<ParameterDefinition> parameters = [];
			if (RequiresImplicitThisParameter(function) && containingTypes.TryGetValue(function, out TypeDefinition? containingType))
			{
				string resolvedThisType = function.AbiThisType?.ResolvedType ?? function.EffectiveThisParameter?.ResolvedType ?? containingType.Name + "*";
				parameters.Add(new ThisParameterDefinition
				{
					Name = "this",
					Symbol = "this",
					Type = function.AbiThisType ?? new PointerTypeReference { ElementType = new TypeDefinitionReference { Definition = containingType, Name = containingType.Name } },
					ResolvedType = resolvedThisType
				});
			}
			foreach (ParameterDefinition parameter in GetAbiOrderedParameters(function.Parameters))
			{
				if (parameter is WithinParameterDefinition && parameter.Type is null)
					continue;
				if (parameter is ThisParameterDefinition && TryGetExpandedArrayElementType(parameter.ResolvedType, out string thisElementType))
				{
					parameters.Add(new ThisParameterDefinition
					{
						Name = parameter.Name,
						Symbol = parameter.Symbol,
						Type = parameter.Type,
						ResolvedType = thisElementType + "*"
					});
					parameters.Add(new ParameterDefinition
					{
						Name = parameter.Name + "_length",
						Symbol = parameter.Symbol + "_length",
						ResolvedType = "nuint"
					});
					continue;
				}
				parameters.Add(parameter);
			}
			return parameters;
		}

		static IEnumerable<ParameterDefinition> GetAbiOrderedParameters(IEnumerable<ParameterDefinition> parameters)
		{
			List<ParameterDefinition> pendingWithin = [];
			foreach (ParameterDefinition parameter in parameters)
			{
				if (parameter is WithinParameterDefinition || parameter.Modifier == ParameterModifier.Within)
				{
					pendingWithin.Add(parameter);
					continue;
				}

				if (parameter is SizeOfParameterDefinition or VTableOfParameterDefinition)
				{
					yield return parameter;
					continue;
				}

				foreach (ParameterDefinition within in pendingWithin)
					yield return within;
				pendingWithin.Clear();
				yield return parameter;
			}

			foreach (ParameterDefinition within in pendingWithin)
				yield return within;
		}

		static bool TryGetConcreteGenericType(string? genericType, Dictionary<string, string> substitutions, out string concreteType)
		{
			concreteType = "";
			if (string.IsNullOrWhiteSpace(genericType))
				return false;
			string key = StripTypeDecorators(genericType);
			if (!substitutions.TryGetValue(key, out string? substitution))
				return false;
			concreteType = substitution;
			return true;
		}

		static string StripTypeDecorators(string type)
		{
			type = type.Trim();
			while (type.StartsWith("const ", StringComparison.Ordinal)
				|| type.StartsWith("volatile ", StringComparison.Ordinal)
				|| type.StartsWith("escaped ", StringComparison.Ordinal)
				|| type.StartsWith("scoped ", StringComparison.Ordinal)
				|| type.StartsWith("unscoped ", StringComparison.Ordinal)
				|| type.StartsWith("in ", StringComparison.Ordinal))
			{
				int space = type.IndexOf(' ', StringComparison.Ordinal);
				type = space < 0 ? "" : type[(space + 1)..].TrimStart();
			}
			while (type.EndsWith(" const", StringComparison.Ordinal)
				|| type.EndsWith(" volatile", StringComparison.Ordinal))
			{
				int space = type.LastIndexOf(' ');
				type = space < 0 ? "" : type[..space].TrimEnd();
			}
			return type;
		}

		bool NeedsGenericScalarCast(string concreteType)
		{
			string type = StripTypeDecorators(concreteType);
			if (type.EndsWith("*", StringComparison.Ordinal) || type.EndsWith("[]", StringComparison.Ordinal) || type.EndsWith("?", StringComparison.Ordinal))
				return false;
			if (type is "string" or "wstring" or "astring" or "void" or "untyped" or "any" or "auto")
				return false;
			if (IsPrimitiveScalarType(type))
				return true;
			return IsEnumType(type);
		}

		bool IsAnyGenericParameterType(string? type)
		{
			string stripped = StripTypeDecorators(type ?? "");
			return currentAnyGenericTypeNames.Contains(stripped);
		}

		string FormatGenericSizeExpression(string? type)
		{
			string genericName = StripTypeDecorators(type ?? "");
			if (currentFunction is not null)
			{
				foreach (ParameterDefinition parameter in currentFunction.Parameters)
				{
					if (parameter is SizeOfParameterDefinition && parameter.Name == "sizeof_" + genericName)
						return CName(parameter);
					if (parameter.Name == "sizeof_" + genericName)
						return CName(parameter);
				}
				if (containingTypes.TryGetValue(currentFunction, out TypeDefinition? containingType)
					&& containingType.GenericParameters.Exists(parameter => parameter.Name == genericName))
				{
					return "this->_sizeof_" + SanitizeIdentifier(genericName);
				}
			}
			return "sizeof(void*)";
		}

		string FormatGenericStorageSource(Expression? expression)
		{
			if (TryFormatGenericStorageAddress(expression, out string address, out _))
				return address;
			return expression switch
			{
				VariableReferenceExpression { Variable: ParameterDefinition { Modifier: ParameterModifier.In } parameter } when IsGenericParameterType(parameter.ResolvedType) => CName(parameter),
				VariableReferenceExpression { Variable: DeclarationTarget target } when IsAnyGenericParameterType(target.ResolvedType) => CName(target),
				_ => "&(" + FormatExpression(expression) + ")"
			};
		}

		bool TryFormatGenericStorageAddress(Expression? expression, out string address, out string genericType)
		{
			address = "";
			genericType = "";
			switch (expression)
			{
				case IndexExpression index when TryFormatGenericArrayElementAddress(index, out address):
					genericType = TryGetArrayElementType(index.Target?.ResolvedType, out string arrayElement)
						? arrayElement
						: TryGetPointerElementType(index.Target?.ResolvedType, out string pointerElement) ? pointerElement : "";
					return !string.IsNullOrWhiteSpace(genericType);

				case VariableReferenceExpression { Variable: ParameterDefinition { Modifier: ParameterModifier.In } parameter } when IsGenericParameterType(parameter.ResolvedType):
					address = CName(parameter);
					genericType = parameter.ResolvedType ?? "";
					return true;

				case VariableReferenceExpression { Variable: DeclarationTarget target } when IsAnyGenericParameterType(target.ResolvedType):
					address = CName(target);
					genericType = target.ResolvedType ?? "";
					return true;

				case UnaryExpression { Operator: UnaryOperator.PointerDereference, ResolvedType: string targetType } unary
					when IsAnyGenericParameterType(targetType):
					address = FormatExpression(unary.Operand);
					genericType = targetType;
					return true;

				default:
					return false;
			}
		}

		static bool IsPrimitiveScalarType(string type)
		{
			return type is "bool" or "sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "nint" or "nuint" or "float" or "double" or "char" or "wchar" or "achar" or "uchar";
		}

		bool IsEnumType(string type)
		{
			foreach (SourceFile file in compilation.Files)
				foreach (Definition definition in file.BindableTree?.Definitions ?? [])
					if (definition is EnumDefinition enumDefinition && enumDefinition.Name == type)
						return true;
			return false;
		}

		string CastToErasedGeneric(string value, string concreteType)
		{
			return "(void *)(intptr_t)(" + value + ")";
		}

		string CastFromErasedGeneric(string value, string concreteType)
		{
			string cType = FormatResolvedType(concreteType, "").Declaration.Trim();
			return "(" + cType + ")(intptr_t)(" + value + ")";
		}

		static List<string> ExtractConstructedTypeArguments(string type)
		{
			type = StripTypeDecorators(type);
			while (type.EndsWith("*", StringComparison.Ordinal) || type.EndsWith("[]", StringComparison.Ordinal) || type.EndsWith("?", StringComparison.Ordinal))
			{
				type = type.EndsWith("[]", StringComparison.Ordinal) ? type[..^2].TrimEnd() : type[..^1].TrimEnd();
				type = StripTypeDecorators(type);
			}

			int start = type.IndexOf('<', StringComparison.Ordinal);
			if (start < 0)
				return [];
			int depth = 0;
			for (int i = start; i < type.Length; i++)
			{
				if (type[i] == '<')
					depth++;
				else if (type[i] == '>' && --depth == 0)
					return SplitGenericArgumentList(type[(start + 1)..i]);
			}
			return [];
		}

		static List<string> SplitGenericArgumentList(string text)
		{
			List<string> arguments = [];
			int start = 0;
			int genericDepth = 0;
			int parenDepth = 0;
			for (int i = 0; i <= text.Length; i++)
			{
				char ch = i < text.Length ? text[i] : ',';
				if (ch == '<')
					genericDepth++;
				else if (ch == '>' && genericDepth > 0)
					genericDepth--;
				else if (ch == '(')
					parenDepth++;
				else if (ch == ')' && parenDepth > 0)
					parenDepth--;
				else if (ch == ',' && genericDepth == 0 && parenDepth == 0)
				{
					string argument = text[start..i].Trim();
					if (argument.Length > 0)
						arguments.Add(argument);
					start = i + 1;
				}
			}
			return arguments;
		}

		string FormatMemberReference(MemberReferenceExpression member)
		{
			if (member.Member is FunctionDefinition function && (member.Target is null || containingTypes.TryGetValue(function, out TypeDefinition? owner) && owner is not InterfaceDefinition))
				return CName(function);
			if (member.Member is VariableDefinition variable)
				return CName(variable);
			if (member.Member is FieldDefinition field)
			{
				string fieldTarget = FormatExpression(member.Target);
				if (member.Target is UnaryExpression { Operator: UnaryOperator.PointerDereference })
					fieldTarget = "(" + fieldTarget + ")";
				return fieldTarget + (IsPointerMemberTarget(member.Target) ? "->" : ".") + CName(field);
			}
			string? expandedThisComponent = FormatExpandedThisComponent(member.Target, member.Name);
			if (expandedThisComponent is not null)
				return expandedThisComponent;
			string target = FormatExpression(member.Target);
			if (member.Target is UnaryExpression { Operator: UnaryOperator.PointerDereference })
				target = "(" + target + ")";
			string separator = IsPointerMemberTarget(member.Target) ? "->" : ".";
			return target + separator + SanitizeIdentifier(member.Name);
		}

		static bool IsPointerMemberTarget(Expression? target)
		{
			return target is ThisExpression || IsPointerLike(target?.ResolvedType);
		}

		static bool IsPointerLike(string? type)
		{
			return !string.IsNullOrWhiteSpace(type) && type.TrimEnd().EndsWith("*", StringComparison.Ordinal);
		}

		string FormatVariableReference(BindableNode? variable)
		{
			return variable switch
			{
				FunctionDefinition function => CName(function),
				VariableDefinition definition => CName(definition),
				ParameterDefinition { Modifier: ParameterModifier.Out or ParameterModifier.Thrown } parameter => "(*" + CName(parameter) + ")",
				ParameterDefinition { Modifier: ParameterModifier.In } parameter when IsGenericParameterType(parameter.ResolvedType) => CName(parameter),
				ParameterDefinition { Modifier: ParameterModifier.In } parameter => "(*" + CName(parameter) + ")",
				ParameterDefinition parameter => CName(parameter),
				FieldDefinition field => CName(field),
				DeclarationTarget target when IsSyntheticCurrentOutParameterTarget(target) && TryFindCurrentOutParameter(target, out ParameterDefinition? parameter) => "(*" + CName(parameter) + ")",
				DeclarationTarget target => CName(target),
				_ => UnsupportedExpression(variable)
			};
		}

		string FormatThisExpression()
		{
			return TryGetCurrentInThisParameter(out _) ? "(*this)" : "this";
		}

		bool IsCurrentInThisExpression(Expression? expression)
		{
			if (!TryGetCurrentInThisParameter(out ParameterDefinition? inThis))
				return false;

			return expression switch
			{
				ThisExpression => true,
				VariableReferenceExpression { Variable: ParameterDefinition parameter } => ReferenceEquals(parameter, inThis),
				_ => false
			};
		}

		bool TryGetCurrentInThisParameter(out ParameterDefinition parameter)
		{
			parameter = null!;
			if (currentFunction is null)
				return false;

			parameter = currentFunction.Parameters.FirstOrDefault(static candidate => candidate.Symbol == "this" && candidate.Modifier == ParameterModifier.In)!;
			return parameter is not null;
		}

		bool IsGenericParameterType(string? type)
		{
			if (string.IsNullOrWhiteSpace(type))
				return false;
			string stripped = StripTypeDecorators(type);
			return genericParameterNames.Contains(stripped) || currentGenericTypeNames.Contains(stripped);
		}

		string FormatInitializer(InitializerExpression initializer)
		{
			List<string> items = [];
			foreach (InitializerItem item in initializer.Items)
			{
				string value = FormatExpression(item.Expression);
				string? target = FormatInitializerTarget(item.Target);
				items.Add(target is null ? value : "." + target + " = " + value);
			}
			string body = items.Count == 0 ? "{ 0 }" : "{ " + string.Join(", ", items) + " }";
			if (!IsAggregateValueType(initializer.ResolvedType))
				return body;

			string type = FormatTypeOrResolved(null, initializer.ResolvedType, "").Declaration.Trim();
			return "(" + type + ")" + body;
		}

		string FormatGroupedExpression(GroupedExpression grouped)
		{
			if (grouped.Items.Count == 0)
				return "0";
			return "(" + string.Join(", ", grouped.Items.Select(static item => item.Expression).Select(FormatExpression)) + ")";
		}

		static string? FormatInitializerTarget(InitializerTarget? target)
		{
			if (target is null || target.Parts.Count == 0)
				return null;
			return string.Join(".", target.Parts.Select(static part => SanitizeIdentifier(part.Name ?? "")));
		}

		string FormatUnaryExpression(UnaryExpression unary)
		{
			if (unary.Operator == UnaryOperator.AddressOf
				&& unary.Operand is VariableReferenceExpression { Variable: ParameterDefinition { Modifier: ParameterModifier.In } parameter }
				&& IsGenericParameterType(parameter.ResolvedType))
				return CName(parameter);
			if (unary.Operator == UnaryOperator.AddressOf && IsCurrentInThisExpression(unary.Operand))
				return "this";
			if (unary.Operator == UnaryOperator.PointerDereference && IsCurrentInThisExpression(unary.Operand))
				return "**this";
			if (unary.Operator == UnaryOperator.AddressOf
				&& unary.Operand is VariableReferenceExpression { Variable: DeclarationTarget target }
				&& IsAnyGenericParameterType(target.ResolvedType))
				return CName(target);
			if (unary.Operator == UnaryOperator.AddressOf
				&& unary.Operand is IndexExpression addressIndex
				&& IsGenericArrayElementIndex(addressIndex))
			{
				string? arrayType = addressIndex.Target?.ResolvedType;
				TryGetArrayElementType(arrayType, out string elementType);
				if (string.IsNullOrWhiteSpace(elementType))
					TryGetPointerElementType(arrayType, out elementType);
				return FormatGenericArrayElementBytePointer(addressIndex, elementType);
			}

			string operand = FormatExpression(unary.Operand);
			return unary.Operator switch
			{
				UnaryOperator.Plus => "+" + operand,
				UnaryOperator.Minus when unary.Operand is LiteralExpression { Kind: LiteralKind.Number } literal => "-" + FormatNumberLiteralForC(literal.Text, negativeContext: true),
				UnaryOperator.Minus => "-" + operand,
				UnaryOperator.LogicalNot => "!" + operand,
				UnaryOperator.BitwiseNot => "~" + operand,
				UnaryOperator.AddressOf => "&" + operand,
				UnaryOperator.PointerDereference => "*" + operand,
				UnaryOperator.Increment => "++" + operand,
				UnaryOperator.Decrement => "--" + operand,
				_ => UnsupportedExpression(unary)
			};
		}

		static string FormatLiteral(LiteralExpression literal)
		{
			if (literal.Kind == LiteralKind.String && IsWideStringLiteralType(literal.ResolvedType))
				return FormatWideStringLiteral(literal);

			return literal.Kind switch
			{
				LiteralKind.Number => FormatNumberLiteralForC(literal.Text),
				LiteralKind.String => literal.Text,
				LiteralKind.Character => literal.Text,
				LiteralKind.True => "true",
				LiteralKind.False => "false",
				LiteralKind.Null => "NULL",
				_ => "0"
			};
		}

		static bool IsWideStringLiteralType(string? type)
		{
			type = StripTypeQualifiers(type ?? "");
			return type is "wstring" or "wchar*" or "wchar[]";
		}

		static string FormatWideStringLiteral(LiteralExpression literal)
		{
			string text = literal.Value as string ?? "";
			List<string> units = [];
			for (int i = 0; i < text.Length; i++)
				units.Add("0x" + ((int)text[i]).ToString("X4", CultureInfo.InvariantCulture));
			units.Add("0");
			return "((const uint16_t[]){" + string.Join(", ", units) + "})";
		}

		static string FormatUpdateOperator(UpdateOperator op)
		{
			return op switch
			{
				UpdateOperator.Increment => "++",
				UpdateOperator.Decrement => "--",
				_ => ""
			};
		}

		static string FormatBinaryOperator(BinaryOperator op)
		{
			return op switch
			{
				BinaryOperator.LogicalOr => "||",
				BinaryOperator.NullCoalescing => "??",
				BinaryOperator.LogicalAnd => "&&",
				BinaryOperator.BitwiseOr => "|",
				BinaryOperator.BitwiseXor => "^",
				BinaryOperator.BitwiseAnd => "&",
				BinaryOperator.Equal => "==",
				BinaryOperator.NotEqual => "!=",
				BinaryOperator.LessThan => "<",
				BinaryOperator.LessThanOrEqual => "<=",
				BinaryOperator.GreaterThan => ">",
				BinaryOperator.GreaterThanOrEqual => ">=",
				BinaryOperator.LeftShift => "<<",
				BinaryOperator.RightShift => ">>",
				BinaryOperator.Add => "+",
				BinaryOperator.Subtract => "-",
				BinaryOperator.Multiply => "*",
				BinaryOperator.Divide => "/",
				BinaryOperator.Modulo => "%",
				_ => "?"
			};
		}

		static string FormatAssignmentOperator(AssignmentOperator op)
		{
			return op switch
			{
				AssignmentOperator.Assign => "=",
				AssignmentOperator.Add => "+=",
				AssignmentOperator.Subtract => "-=",
				AssignmentOperator.Multiply => "*=",
				AssignmentOperator.Divide => "/=",
				AssignmentOperator.Modulo => "%=",
				AssignmentOperator.BitwiseAnd => "&=",
				AssignmentOperator.BitwiseOr => "|=",
				AssignmentOperator.BitwiseXor => "^=",
				AssignmentOperator.LeftShift => "<<=",
				AssignmentOperator.RightShift => ">>=",
				_ => "="
			};
		}

		string UnsupportedExpression(BindableNode? node)
		{
			if (node is not null)
				AddUnsupported(node, "expression");
			return "/* unsupported */ 0";
		}

		void AddUnsupported(BindableNode node, string kind)
		{
			string message = $"C emission does not yet support {kind} node {node.GetType().Name}.";
			if (!result.Diagnostics.Contains(message, StringComparer.Ordinal))
				result.Diagnostics.Add(message);
		}

		static void WriteIndent(TextWriter writer, int indent)
		{
			for (int i = 0; i < indent; i++)
				writer.Write('\t');
		}

		string FormatParameters(List<ParameterDefinition> parameters)
		{
			List<string> parts = [];
			HashSet<string> usedNames = [];
			WithArrayElementComponentContext(parameters, () =>
			{
				foreach (ParameterDefinition parameter in parameters)
				{
					if (parameter is WithinParameterDefinition && parameter.Type is null)
						continue;
					string name = UniqueCallableParameterName(CName(parameter), usedNames);
					if (parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown)
					{
						TypeReference? parameterType = parameter.Type;
						parts.Add(FormatOutParameterType(parameterType, parameter.ResolvedType, name).Declaration);
					}
					else if (parameter.Modifier is ParameterModifier.In)
					{
						TypeReference? parameterType = parameter.Type;
						parts.Add(FormatInParameterType(parameterType, parameter.ResolvedType, name).Declaration);
					}
					else
					{
						parts.Add(FormatTypeOrResolved(parameter.Type, parameter.ResolvedType, name).Declaration);
					}
				}
			});
			return "(" + (parts.Count == 0 ? "void" : string.Join(", ", parts)) + ")";
		}

		static string UniqueCallableParameterName(string name, HashSet<string> usedNames)
		{
			if (string.IsNullOrWhiteSpace(name))
				name = "arg";

			string candidate = name;
			int suffix = 0;
			while (!usedNames.Add(candidate))
			{
				suffix++;
				candidate = name + suffix.ToString(CultureInfo.InvariantCulture);
			}
			return candidate;
		}

		string FormatParameters(FunctionDefinition function)
		{
			List<string> parts = [];
			WithArrayElementComponentContext(function.Parameters, () =>
			{
				if (RequiresImplicitThisParameter(function) && containingTypes.TryGetValue(function, out TypeDefinition? type))
				{
					string resolvedThisType = function.AbiThisType?.ResolvedType ?? function.EffectiveThisParameter?.ResolvedType ?? type.Name + "*";
					parts.Add(FormatTypeOrResolved(function.AbiThisType, resolvedThisType, NeedsAbiThisFixup(function) ? "ctx" : "this").Declaration);
				}
				foreach (ParameterDefinition parameter in GetAbiOrderedParameters(function.Parameters))
				{
					if (parameter is WithinParameterDefinition && parameter.Type is null)
						continue;
					string name = CName(parameter);
					if (parameter is ThisParameterDefinition && TryGetExpandedArrayElementType(parameter.ResolvedType, out string thisElementType))
					{
						parts.Add(FormatTypeOrResolved(null, thisElementType + "*", name).Declaration);
						parts.Add(FormatTypeOrResolved(null, "nuint", name + "_length").Declaration);
						continue;
					}
					if (parameter is ThisParameterDefinition && TryGetExpandedArrayPointerElementType(parameter.ResolvedType, out string thisPointerElementType))
					{
						parts.Add(FormatTypeOrResolved(null, thisPointerElementType + "**", name).Declaration);
						parts.Add(FormatTypeOrResolved(null, "nuint*", name + "_length").Declaration);
						continue;
					}
					if (parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown)
					{
						TypeReference? parameterType = parameter.Type;
						parts.Add(FormatOutParameterType(parameterType, parameter.ResolvedType, name).Declaration);
					}
					else if (parameter.Modifier is ParameterModifier.In)
					{
						TypeReference? parameterType = parameter.Type;
						parts.Add(FormatInParameterType(parameterType, parameter.ResolvedType, name).Declaration);
					}
					else
					{
						parts.Add(FormatTypeOrResolved(parameter.Type, parameter.ResolvedType, name).Declaration);
					}
				}
			});
			return "(" + (parts.Count == 0 ? "void" : string.Join(", ", parts)) + ")";
		}

		string? FormatExpandedThisComponent(MemberExpression member)
		{
			return FormatExpandedThisComponent(member.Target, member.Name);
		}

		string? FormatExpandedThisComponent(Expression? target, string name)
		{
			if (TryGetExpandedThisPointerComponent(target, name, out string pointerComponent))
				return pointerComponent;

			ThisParameterDefinition? parameter = target switch
			{
				VariableReferenceExpression { Variable: ThisParameterDefinition variable } => variable,
				ThisExpression when currentFunction?.Parameters.FirstOrDefault() is ThisParameterDefinition implicitThis => implicitThis,
				_ => null
			};
			ParameterDefinition? componentParameter = parameter;
			if (componentParameter is null
				&& target is VariableReferenceExpression { Variable: ParameterDefinition namedParameter }
				&& CName(namedParameter) == "this")
				componentParameter = namedParameter;
			if (componentParameter is null
				&& target is ThisExpression
				&& currentFunction?.Parameters.FirstOrDefault(static p => p.Symbol == "this") is { } currentThis)
				componentParameter = currentThis;
			if (componentParameter is null)
				return null;
			string componentName = CName(componentParameter);
			bool hasLengthComponent = currentFunction?.Parameters.Any(parameter => CName(parameter) == componentName + "_length") == true;
			if (!hasLengthComponent && !TryGetArrayLiteralElementType(componentParameter.ResolvedType, out _))
				return null;
			return name switch
			{
				"elements" => componentName,
				"length" when hasLengthComponent => componentName + "_length",
				"length" when TryGetArrayLiteralElementType(componentParameter.ResolvedType, out _) => componentName + "_length",
				_ => null
			};
		}

		bool TryGetExpandedThisPointerComponent(Expression? target, string name, out string component)
		{
			component = "";
			Expression? operand = target is ParenthesizedExpression parenthesized ? parenthesized.Expression : target;
			if (operand is not UnaryExpression { Operator: UnaryOperator.PointerDereference } dereference)
				return false;

			ParameterDefinition? parameter = dereference.Operand switch
			{
				VariableReferenceExpression { Variable: ThisParameterDefinition variable } => variable,
				VariableReferenceExpression { Variable: ParameterDefinition namedParameter } when CName(namedParameter) == "this" => namedParameter,
				ThisExpression when currentFunction?.Parameters.FirstOrDefault(static p => p.Symbol == "this") is { } currentThis => currentThis,
				_ => null
			};
			if (parameter is null || !TryGetExpandedArrayPointerElementType(parameter.ResolvedType, out _))
				return false;

			string componentName = CName(parameter);
			component = name switch
			{
				"elements" => "(*" + componentName + ")",
				"length" => "(*" + componentName + "_length)",
				_ => ""
			};
			return component.Length > 0;
		}

		bool RequiresImplicitThisParameter(FunctionDefinition function)
		{
			if (!containingTypes.ContainsKey(function))
				return false;
			if (function.Modifier is FunctionModifier.Static or FunctionModifier.Constructor or FunctionModifier.Destructor)
				return false;
			return function.Parameters.Count == 0 || function.Parameters[0].Symbol != "this";
		}

		static bool NeedsAbiThisFixup(FunctionDefinition function)
		{
			return function.AbiThisType is not null
				&& function.ImplementationThisType is not null
				&& function.AbiThisType.ResolvedType != function.ImplementationThisType.ResolvedType;
		}

		string FormatCallableTypedef(CallableTypeReference callable, string name)
		{
			return "typedef " + FormatCallableDeclarator(callable, name);
		}

		string FormatCallableTypedef(CallableTypeReference callable, List<ParameterDefinition> parameters, string name)
		{
			if (parameters.Count == 0)
				return FormatCallableTypedef(callable, name);

			string callSpec = FormatCallSpec(callable.CallSpec);
			string targetSpec = FormatTypeSpec(callable.TargetSpec ?? GetDefaultTargetTypeSpec(functionPointer: true));
			string pointer = "*";
			if (targetSpec.Length > 0)
				pointer += " " + targetSpec;
			if (callSpec.Length > 0)
				pointer += " " + callSpec;
			string declarator = "(" + pointer + " " + name + ")";
			return "typedef " + FormatType(callable.ReturnType, declarator).Declaration + "(" + FormatResolvedParameterList(GetExpandedCallableParameterTypesForC(parameters)) + ")";
		}

		static List<string> GetExpandedCallableParameterTypesForC(List<ParameterDefinition> parameters)
		{
			List<string> types = [];
			foreach (ParameterDefinition parameter in parameters)
			{
				if (parameter is ThisParameterDefinition)
					continue;

				string parameterType = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? "";
				if (TryGetArrayElementOnly(parameterType, out string arrayElementType))
				{
					types.Add(arrayElementType + "*");
					types.Add("nuint");
					continue;
				}
				if (TryGetOptionalElementOnly(parameterType, out string optionalElementType))
				{
					types.Add(optionalElementType);
					types.Add("bool");
					continue;
				}
				if (TryParseExpandedCallableStorageType(parameterType, out string callableReturnType, out List<string> callableParameterTypes))
				{
					types.Add("fn " + callableReturnType + "(" + string.Join(", ", ["void*", .. callableParameterTypes]) + ")");
					types.Add("void*");
					continue;
				}

				types.Add(parameter.Modifier switch
				{
					ParameterModifier.In => "in " + parameterType,
					ParameterModifier.Out => "out " + parameterType,
					ParameterModifier.Thrown => "thrown " + parameterType,
					ParameterModifier.Within => "within " + parameterType,
					_ => parameterType
				});
			}
			return types;
		}

		string FormatCallableDeclarator(CallableTypeReference callable, string name)
		{
			string callSpec = FormatCallSpec(callable.CallSpec);
			string targetSpec = FormatTypeSpec(callable.TargetSpec ?? GetDefaultTargetTypeSpec(functionPointer: true));
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
				PrimitiveTypeReference primitive => FormatPrimitiveType(primitive.Type, declarator),
				TypeDefinitionReference definition => new CType(CTypeName(definition) + " " + declarator),
				NamedTypeReference named when ShouldFormatResolvedType(named.ResolvedType) => FormatResolvedType(named.ResolvedType!, declarator),
				NamedTypeReference named => new CType(CTypeName(named) + " " + declarator),
				GenericTypeReference generic => FormatType(generic.Type, declarator),
				GenericParameterTypeReference => new CType("void* " + declarator),
				AnyTypeReference => new CType("void* " + declarator),
				AutoTypeReference => new CType("void* " + declarator),
				AllocatorTypeReference => new CType("Allocator* " + declarator),
				MaterializedStructTypeReference materialized => FormatMaterializedStructType(materialized, declarator),
				GroupedParamsTypeReference => new CType("void* " + declarator),
				ThrownTypeReference thrown => FormatType(thrown.Type, declarator),
				IterTypeReference => new CType("void* " + declarator),
				_ => new CType((type.ResolvedType is null ? "void*" : CTypeName(type.ResolvedType)) + " " + declarator)
			};
		}

		CType FormatTypeOrResolved(TypeReference? type, string? resolvedType, string declarator)
		{
			if (type is CallableTypeReference)
				return FormatType(type, declarator);
			if (ShouldFormatResolvedType(resolvedType))
				return FormatResolvedType(resolvedType!, declarator);
			if (type is not null)
				return FormatType(type, declarator);
			if (resolvedType is not null)
				return FormatResolvedType(resolvedType, declarator);
			return FormatType(null, declarator);
		}

		CType FormatOutParameterType(TypeReference? type, string? resolvedType, string declarator)
		{
			if (IsAnyGenericParameterType(resolvedType))
				return FormatResolvedType(resolvedType + "*", declarator);
			if (type is not null)
				return FormatType(new PointerTypeReference { ElementType = type }, declarator);
			if (!string.IsNullOrWhiteSpace(resolvedType))
				return FormatResolvedType(resolvedType + "*", declarator);
			return FormatType(new PointerTypeReference { ElementType = null }, declarator);
		}

		CType FormatInParameterType(TypeReference? type, string? resolvedType, string declarator)
		{
			if (!string.IsNullOrWhiteSpace(resolvedType))
				return FormatResolvedType(resolvedType + "*", declarator);
			if (type is not null)
				return FormatType(new PointerTypeReference { ElementType = type }, declarator);
			return FormatType(new PointerTypeReference { ElementType = null }, declarator);
		}

		CType FormatResolvedType(string resolvedType, string declarator)
		{
			string type = resolvedType.Trim();
			if (type.StartsWith("struct(", StringComparison.Ordinal) && type.EndsWith(")", StringComparison.Ordinal))
				return FormatStorageResolvedType(type[7..^1], declarator);

			List<string> qualifiers = [];
			List<string> trailingQualifiers = [];
			while (true)
			{
				if (type.StartsWith("const ", StringComparison.Ordinal))
				{
					qualifiers.Add("const");
					type = type[6..].TrimStart();
					continue;
				}
				if (type.StartsWith("volatile ", StringComparison.Ordinal))
				{
					qualifiers.Add("volatile");
					type = type[9..].TrimStart();
					continue;
				}
				if (type.StartsWith("escaped ", StringComparison.Ordinal))
				{
					type = type[8..].TrimStart();
					continue;
				}
				if (type.StartsWith("scoped ", StringComparison.Ordinal))
				{
					type = type[7..].TrimStart();
					continue;
				}
				if (type.StartsWith("unscoped ", StringComparison.Ordinal))
				{
					type = type[9..].TrimStart();
					continue;
				}
				break;
			}
			while (true)
			{
				if (type.EndsWith(" const", StringComparison.Ordinal))
				{
					trailingQualifiers.Insert(0, "const");
					type = type[..^6].TrimEnd();
					continue;
				}
				if (type.EndsWith(" volatile", StringComparison.Ordinal))
				{
					trailingQualifiers.Insert(0, "volatile");
					type = type[..^9].TrimEnd();
					continue;
				}
				break;
			}

			int pointerCount = 0;
			while (type.EndsWith("*", StringComparison.Ordinal))
			{
				pointerCount++;
				type = type[..^1].TrimEnd();
			}

			if (type.EndsWith("[]", StringComparison.Ordinal))
			{
				pointerCount++;
				type = type[..^2].TrimEnd();
			}

			if (IsPrimitiveStringResolvedName(type))
			{
				pointerCount++;
				if (qualifiers.Remove("const"))
					trailingQualifiers.Insert(0, "const");
			}

			bool isGenericType = currentGenericTypeNames.Contains(type) || genericParameterNames.Contains(type);
			if (isGenericType && !anyGenericParameterNames.Contains(type) && pointerCount > 0 && currentArrayElementComponentNames.Contains(declarator))
				pointerCount++;
			string cType = isGenericType && pointerCount == 0 ? "void*" : FormatResolvedBaseType(type);
			string pointerPart = pointerCount == 0 ? "" : new string('*', pointerCount);
			string targetSpec = pointerPart.Length == 0 ? "" : FormatTypeSpec(GetDefaultTargetTypeSpec(functionPointer: false));
			if (targetSpec.Length > 0)
				pointerPart += " " + targetSpec;
			if (pointerPart.Length > 0 && trailingQualifiers.Count > 0)
				pointerPart += " " + string.Join(" ", trailingQualifiers);
			string qualifierPart = qualifiers.Count == 0 ? "" : string.Join(" ", qualifiers) + " ";
			string pointerDeclarator = declarator;
			if (pointerPart.Length > 0)
			{
				string separator = pointerPart.EndsWith("*", StringComparison.Ordinal) || declarator.Length == 0 ? "" : " ";
				pointerDeclarator = pointerPart + separator + declarator;
			}
			return new CType(qualifierPart + cType + " " + pointerDeclarator);
		}

		string FormatResolvedBaseType(string type)
		{
			if (currentGenericTypeNames.Contains(type))
				return "void";
			if (genericParameterNames.Contains(type))
				return "void";

			return type switch
			{
				"void" => "void",
				"bool" => compilation.Target?.GetPrimitiveCSpelling("bool") ?? "bool",
				"string" => "const char",
				"wstring" => "const uint16_t",
				"astring" => "const char",
				"untyped" => "void",
				"sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "nint" or "nuint" or "float" or "double" or "char" or "wchar" or "achar" or "uchar"
					=> compilation.Target?.GetPrimitiveCSpelling(type) ?? type,
				_ => CTypeName(type)
			};
		}

		static bool IsPrimitiveStringResolvedName(string type)
		{
			return type is "string" or "wstring" or "astring";
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
			string targetSpec = FormatTypeSpec(GetDefaultTargetTypeSpec(functionPointer: false));
			if (targetSpec.Length > 0)
				declarator = "* " + targetSpec + " " + declarator;
			else
				declarator = "*" + declarator;
			if (pointer.ElementType is PrimitiveTypeReference { Type: PrimitiveType.Untyped })
				return new CType("void " + declarator);
			return FormatType(pointer.ElementType, declarator);
		}

		CType FormatPrimitiveType(PrimitiveType primitive, string declarator)
		{
			string name = GetPrimitiveName(primitive);
			if (primitive == PrimitiveType.Void)
				return new CType("void " + declarator);
			if (primitive == PrimitiveType.String)
				return FormatDataPointerPrimitive("const char", declarator);
			if (primitive == PrimitiveType.WString)
				return FormatDataPointerPrimitive("const uint16_t", declarator);
			if (primitive == PrimitiveType.AString)
				return FormatDataPointerPrimitive("const char", declarator);
			if (primitive == PrimitiveType.Untyped)
				return new CType("void " + declarator);
			return new CType((compilation.Target?.GetPrimitiveCSpelling(name) ?? name) + " " + declarator);
		}

		CType FormatDataPointerPrimitive(string elementType, string declarator)
		{
			string targetSpec = FormatTypeSpec(GetDefaultTargetTypeSpec(functionPointer: false));
			string pointer = targetSpec.Length == 0 ? "* " : "* " + targetSpec + " ";
			return new CType(elementType + pointer + declarator);
		}

		string? GetDefaultTargetTypeSpec(bool functionPointer)
		{
			return compilation.Target?.GetMemoryModelDefault(compilation.MemoryModelName, functionPointer);
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
				LiteralExpression { Kind: LiteralKind.Number } literal => FormatNumberLiteralForC(literal.Text),
				LiteralExpression { Kind: LiteralKind.True } => "1",
				LiteralExpression { Kind: LiteralKind.False } => "0",
				LiteralExpression { Kind: LiteralKind.Null } => "NULL",
				UnaryExpression { Operator: UnaryOperator.Minus, Operand: LiteralExpression { Kind: LiteralKind.Number } literal } => "-" + FormatNumberLiteralForC(literal.Text, negativeContext: true),
				CastExpression cast => FormatConstantExpression(cast.Expression),
				ParenthesizedExpression parenthesized => FormatConstantExpression(parenthesized.Expression),
				VariableReferenceExpression { Variable: VariableDefinition variable } => CName(variable),
				NamedExpression named => named.Name,
				_ => null
			};
		}

		string CTypeName(TypeReference type)
		{
			return type.ResolvedType is string resolved ? CTypeName(resolved) : "void*";
		}

		string CTypeName(TypeDefinitionReference definition)
		{
			return definition.Definition is not null ? CName(definition.Definition) : SanitizeIdentifier(definition.Name);
		}

		string CTypeName(NamedTypeReference named)
		{
			return !string.IsNullOrWhiteSpace(named.ResolvedType)
				? CTypeName(named.ResolvedType)
				: SanitizeIdentifier(named.Name);
		}

		static bool ShouldFormatResolvedType(string? resolvedType)
		{
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;
			string type = resolvedType.Trim();
			if (type.Contains('*', StringComparison.Ordinal) || type.Contains("[]", StringComparison.Ordinal) || type.Contains('?', StringComparison.Ordinal))
				return true;
			if (type.StartsWith("const ", StringComparison.Ordinal) || type.StartsWith("volatile ", StringComparison.Ordinal))
				return true;
			return type is "void" or "bool" or "string" or "wstring" or "astring" or "untyped"
				or "sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong"
				or "nint" or "nuint" or "float" or "double" or "char" or "wchar" or "achar" or "uchar";
		}

		static bool IsResolvedCallableType(string resolvedType)
		{
			string type = resolvedType.TrimStart();
			return type.StartsWith("fn ", StringComparison.Ordinal) && type.Contains('(', StringComparison.Ordinal) && type.EndsWith(")", StringComparison.Ordinal);
		}

		static bool TryParseResolvedCallableType(string resolvedType, out string returnType, out List<string> parameterTypes)
		{
			returnType = "";
			parameterTypes = [];
			string type = resolvedType.Trim();
			if (!type.StartsWith("fn ", StringComparison.Ordinal))
				return false;
			int open = type.IndexOf('(', StringComparison.Ordinal);
			int close = type.LastIndexOf(')');
			if (open < 0 || close < open)
				return false;
			string prefix = type[3..open].Trim();
			if (prefix.Length == 0)
				return false;
			List<string> prefixParts = SplitTopLevel(prefix, ' ');
			returnType = prefixParts[^1];
			string parameterText = type[(open + 1)..close].Trim();
			if (parameterText.Length == 0)
				return true;
			foreach (string parameter in SplitTopLevel(parameterText, ','))
				parameterTypes.Add(parameter.Trim());
			return true;
		}

		static List<string> SplitTopLevel(string text, char separator)
		{
			List<string> parts = [];
			int depth = 0;
			int start = 0;
			for (int i = 0; i < text.Length; i++)
			{
				char ch = text[i];
				if (ch is '(' or '<')
					depth++;
				else if (ch is ')' or '>')
					depth--;
				else if (ch == separator && depth == 0)
				{
					string part = text[start..i].Trim();
					if (part.Length > 0)
						parts.Add(part);
					start = i + 1;
				}
			}
			string final = text[start..].Trim();
			if (final.Length > 0)
				parts.Add(final);
			return parts;
		}

		string CTypeName(string resolvedType)
		{
			int genericStart = resolvedType.IndexOf('<', StringComparison.Ordinal);
			if (genericStart >= 0)
				resolvedType = resolvedType[..genericStart];
			resolvedType = EraseGenericParametersForCName(resolvedType);
			return resolvedType switch
			{
				"any" or "auto" or "#TARGET" => "void*",
				_ => SanitizeIdentifier(RemoveTypeDecorators(resolvedType))
			};
		}

		string EraseGenericParametersForCName(string resolvedType)
		{
			if (genericParameterNames.Count == 0 || string.IsNullOrWhiteSpace(resolvedType))
				return resolvedType;

			StringBuilder builder = new(resolvedType.Length);
			for (int i = 0; i < resolvedType.Length;)
			{
				if (IsIdentifierStart(resolvedType[i]))
				{
					int start = i;
					i++;
					while (i < resolvedType.Length && IsIdentifierPart(resolvedType[i]))
						i++;
					string token = resolvedType[start..i];
					if (genericParameterNames.Contains(token))
					{
						builder.Append("void*");
						int afterToken = i;
						while (afterToken < resolvedType.Length && char.IsWhiteSpace(resolvedType[afterToken]))
							afterToken++;
						while (afterToken < resolvedType.Length && resolvedType[afterToken] == '*')
							afterToken++;
						if (afterToken > i)
							i = afterToken;
					}
					else
					{
						builder.Append(token);
					}
					continue;
				}

				builder.Append(resolvedType[i]);
				i++;
			}
			return builder.ToString();
		}

		static bool IsIdentifierStart(char c)
		{
			return c == '_' || char.IsLetter(c);
		}

		static bool IsIdentifierPart(char c)
		{
			return IsIdentifierStart(c) || char.IsDigit(c);
		}

		static string RemoveTypeDecorators(string value)
		{
			return value
				.Replace("const ", "", StringComparison.Ordinal)
				.Replace("volatile ", "", StringComparison.Ordinal)
				.Replace("escaped ", "", StringComparison.Ordinal)
				.Replace("scoped ", "", StringComparison.Ordinal)
				.Replace("unscoped ", "", StringComparison.Ordinal)
				.Replace("in ", "", StringComparison.Ordinal)
				.Replace("*", "Ptr", StringComparison.Ordinal)
				.Replace("[]", "Array", StringComparison.Ordinal)
				.Replace("?", "Optional", StringComparison.Ordinal);
		}

		string CName(FunctionDefinition function)
		{
			if (function.SymbolOverridden && !string.IsNullOrWhiteSpace(function.Symbol))
				return SanitizeIdentifier(function.Symbol);
			if (!string.IsNullOrWhiteSpace(function.Symbol) && function.Symbol != function.Name)
				return SanitizeIdentifier(function.Symbol);
			if (containingTypes.TryGetValue(function, out TypeDefinition? type))
				return SanitizeIdentifier(type.Name + "_" + BindableNodeAnalyzer.GetCallableName(function).TrimStart('~'));
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

		static string CName(DeclarationTarget target)
		{
			return target.Names.Count == 0 ? "__unnamed" : SanitizeIdentifier(target.Names[0]);
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

		sealed record DelegateThunk(
			string Name,
			FunctionDefinition SourceFunction,
			string ReturnType,
			List<string> ParameterTypes,
			bool ForwardsContext);
	}
}
