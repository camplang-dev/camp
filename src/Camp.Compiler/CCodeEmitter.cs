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
		else if (node is DeclarationTarget declarationTarget)
			description += $" '{string.Join(", ", declarationTarget.Names)}'";
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

		if (TryGetNodeSourceRange(node, out TokenRange range))
			description += $" at {range.StartLineNumber},{range.StartColumn}";
		else if (TryGetSerializedNodeSnippet(node, out string snippet))
			description += $" near `{snippet}`";

		return description;
	}

	static bool TryGetNodeSourceRange(BindableNode node, out TokenRange range)
	{
		if (node.SourceSyntax is not null && TryGetSourceRange(node.SourceSyntax, out range))
			return true;
		if (node.Provenance?.SourceSyntax is not null && TryGetSourceRange(node.Provenance.SourceSyntax, out range))
			return true;

		foreach (BindableNode child in EnumerateNodes(node, []))
		{
			if (child == node)
				continue;
			if (child.SourceSyntax is not null && TryGetSourceRange(child.SourceSyntax, out range))
				return true;
			if (child.Provenance?.SourceSyntax is not null && TryGetSourceRange(child.Provenance.SourceSyntax, out range))
				return true;
		}

		range = default;
		return false;
	}

	static bool TryGetSerializedNodeSnippet(BindableNode node, out string snippet)
	{
		try
		{
			using StringWriter writer = new();
			BindableNodeCodeSerializer.Serialize(node, writer);
			snippet = CollapseWhitespace(writer.ToString());
			if (snippet.Length > 120)
				snippet = snippet[..117] + "...";
			return snippet.Length > 0;
		}
		catch
		{
			snippet = "";
			return false;
		}
	}

	static string CollapseWhitespace(string value)
	{
		StringBuilder builder = new(value.Length);
		bool pendingSpace = false;
		foreach (char ch in value)
		{
			if (char.IsWhiteSpace(ch))
			{
				pendingSpace = builder.Length > 0;
				continue;
			}

			if (pendingSpace)
			{
				builder.Append(' ');
				pendingSpace = false;
			}
			builder.Append(ch);
		}

		return builder.ToString();
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
		if (root is AttributeConstructor)
			yield break;

		foreach (PropertyInfo property in root.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.Name is nameof(BindableNode.SourceSyntax) or nameof(Module.DefinitionSources) or nameof(Module.SourceWithinAllocationPolicies))
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
		return Path.Combine(string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : directory, "bin");
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
		readonly AbiSurface abiSurface = AbiSurface.Build(compilation);
		readonly Dictionary<FunctionDefinition, TypeDefinition> containingTypes = BuildContainingTypeMap(compilation);
		readonly HashSet<string> interfaceNames = BuildInterfaceNameSet(compilation);
		readonly HashSet<string> callableInterfaceNames = BuildCallableInterfaceNameSet(compilation);
		readonly HashSet<string> genericParameterNames = BuildGenericParameterNameSet(compilation);
		readonly HashSet<string> anyGenericParameterNames = BuildAnyGenericParameterNameSet(compilation);
		readonly HashSet<string> currentGenericTypeNames = new(StringComparer.Ordinal);
		readonly HashSet<string> currentAnyGenericTypeNames = new(StringComparer.Ordinal);
		readonly HashSet<string> currentArrayElementComponentNames = new(StringComparer.Ordinal);
		readonly Dictionary<Expression, DelegateThunk> delegateThunksByExpression = [];
		readonly Dictionary<DelegateThunkKey, DelegateThunk> delegateThunksByKey = [];
		readonly Dictionary<SourceFile, List<DelegateThunk>> delegateThunksByFile = [];
		readonly HashSet<string> reservedCNames = [];
		readonly Dictionary<BindableNode, string> currentAsyncFrameReplacements = [];
		readonly Dictionary<string, string> currentAsyncFrameNameReplacements = new(StringComparer.Ordinal);
		readonly Dictionary<string, string> currentWideStringLiteralNames = new(StringComparer.Ordinal);
		readonly List<(string Name, string Initializer)> currentWideStringLiterals = [];
		string? currentAsyncFrameName;
		int currentAsyncLoopLabelIndex;
		int currentWideStringLiteralIndex;
		FunctionDefinition? currentFunction;
		bool currentFunctionHasLabels;
		readonly string sharedExportPrefix = options.BuildKind is NativeBuildKind.Shared
			? compilation.Target?.Capabilities.GetCEmitterValue("dll_export_prefix") ?? ""
			: "";

		sealed record AsyncFrameField(BindableNode? Node, string Name, string Type, TypeReference? TypeReference = null);

		sealed record AsyncAwaitInfo(UnaryExpression Await, CallExpression Call, int Index, string? ResultType, string? ThrownType, string CompleteName);

		sealed record AsyncFrameInfo(FunctionDefinition Function, string FrameType, string ResumeName, List<AsyncFrameField> Fields, List<AsyncAwaitInfo> Awaits);

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

			List<VariableDefinition> inlineVariables = definitions.OfType<VariableDefinition>()
				.Where(static variable => variable.IsInline && IsExternallyVisible(variable))
				.ToList();
			List<FieldDefinition> inlineFields = GetAllStaticFields(definitions)
				.Where(static field => field.IsInline && IsExternallyVisible(field))
				.ToList();
			if (inlineVariables.Count > 0 || inlineFields.Count > 0)
			{
				WriteSection(writer, "Constants", () =>
				{
					foreach (VariableDefinition variable in inlineVariables)
						WriteInlineConstantMacro(writer, variable);
					foreach (FieldDefinition field in inlineFields)
						WriteInlineConstantMacro(writer, field);
				});
			}

				WriteSection(writer, "Layouts", () =>
				{
					foreach (StructDefinition structDefinition in definitions.OfType<StructDefinition>())
						WriteLayoutDefinition(writer, structDefinition);
					foreach (TypeDefinition type in definitions.OfType<TypeDefinition>().Where(static type => type is not StructDefinition))
						WriteLayoutDefinition(writer, type);
				});

			WriteSection(writer, "Function declarations", () =>
			{
				foreach (FunctionDefinition function in GetAllFunctions(definitions).Where(static function => (IsExternallyVisible(function) || IsGeneratedVirtualDispatchFunction(function)) && ShouldEmitCFunction(function)))
					WriteFunctionPrototype(writer, function, storage: null);
			});

			WriteSection(writer, "Object declarations", () =>
			{
				foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>().Where(static variable => !variable.IsInline && IsExternallyVisible(variable)))
					WriteVariableDeclaration(writer, variable, storage: "extern");
				foreach (FieldDefinition field in GetAllStaticFields(definitions).Where(static field => !field.IsInline && IsExternallyVisible(field)))
					WriteFieldStorageDeclaration(writer, field, storage: "extern");
			});
		}

		public void WriteSourceFileForwardDeclarations(TextWriter writer, SourceFile file)
		{
			EnsureDelegateThunksCollected();
			emittedNames.Clear();
			List<Definition> definitions = GetOwnedDefinitions(file).ToList();
			List<FunctionDefinition> privateFunctions = GetAllFunctions(definitions).Where(static function => !IsExternallyVisible(function) && ShouldEmitCFunction(function)).ToList();
			List<VariableDefinition> privateVariables = definitions.OfType<VariableDefinition>().Where(static variable => !variable.IsInline && !IsExternallyVisible(variable)).ToList();
			List<FieldDefinition> privateStaticFields = GetAllStaticFields(definitions).Where(static field => !field.IsInline && !IsExternallyVisible(field)).ToList();
			List<DelegateThunk> delegateThunks = delegateThunksByFile.TryGetValue(file, out List<DelegateThunk>? thunks) ? thunks : [];
			List<AsyncFrameInfo> asyncFrames = GetAllFunctions(definitions).Select(TryBuildAsyncFrameInfo).Where(static frame => frame is not null).Cast<AsyncFrameInfo>().ToList();

			if (privateFunctions.Count == 0 && privateVariables.Count == 0 && privateStaticFields.Count == 0 && delegateThunks.Count == 0 && asyncFrames.Count == 0)
				return;

			writer.WriteLine("/* Private file declarations. */");
			foreach (AsyncFrameInfo frame in asyncFrames)
				WriteAsyncFrameDeclaration(writer, frame);
			foreach (DelegateThunk thunk in delegateThunks)
				WriteDelegateThunkPrototype(writer, thunk);
			foreach (AsyncFrameInfo frame in asyncFrames)
				WriteAsyncFrameHelperPrototypes(writer, frame);
			foreach (FunctionDefinition function in privateFunctions)
				WriteFunctionPrototype(writer, function, storage: PrivateFunctionStorage(function));
			foreach (VariableDefinition variable in privateVariables)
				WriteVariableDeclaration(writer, variable, storage: variable.Extern is not null ? "extern" : "static");
			foreach (FieldDefinition field in privateStaticFields)
				WriteFieldStorageDeclaration(writer, field, storage: field.Extern is not null ? "extern" : "static");
		}

		public void WriteSourceFileDefinitions(TextWriter writer, SourceFile file)
		{
			EnsureDelegateThunksCollected();
			emittedNames.Clear();
			currentWideStringLiteralNames.Clear();
			currentWideStringLiterals.Clear();
			currentWideStringLiteralIndex = 0;
			List<Definition> definitions = GetOwnedDefinitions(file).ToList();
			List<DelegateThunk> delegateThunks = delegateThunksByFile.TryGetValue(file, out List<DelegateThunk>? thunks) ? thunks : [];
			List<AsyncFrameInfo> asyncFrames = GetAllFunctions(definitions).Select(TryBuildAsyncFrameInfo).Where(static frame => frame is not null).Cast<AsyncFrameInfo>().ToList();
			using StringWriter body = new(writer.FormatProvider);
			bool wrote = false;

			foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>().Where(static variable => variable.IsInline && !IsExternallyVisible(variable)))
			{
				WriteInlineConstantMacro(body, variable);
				wrote = true;
			}
			foreach (FieldDefinition field in GetAllStaticFields(definitions).Where(static field => field.IsInline && !IsExternallyVisible(field)))
			{
				WriteInlineConstantMacro(body, field);
				wrote = true;
			}

			foreach (DelegateThunk thunk in delegateThunks)
			{
				WriteDelegateThunkDefinition(body, thunk);
				wrote = true;
			}

			foreach (AsyncFrameInfo frame in asyncFrames)
			{
				WriteAsyncFrameHelperDefinitions(body, frame);
				wrote = true;
			}

			foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>())
			{
				if (variable.Extern is not null || variable.IsInline)
					continue;
				WriteVariableDefinition(body, variable, storage: IsExternallyVisible(variable) ? null : "static");
				wrote = true;
			}

			foreach (FieldDefinition field in GetAllStaticFields(definitions))
			{
				if (field.Extern is not null || field.IsInline)
					continue;
				WriteFieldStorageDefinition(body, field, storage: IsExternallyVisible(field) ? null : "static");
				wrote = true;
			}

			foreach (FunctionDefinition function in GetAllFunctions(definitions))
			{
				if (function.Extern is not null || function.Body is null)
					continue;
				if (function.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor)
					continue;
				WriteFunctionDefinition(body, function, storage: PrivateFunctionStorage(function));
				wrote = true;
			}

			foreach ((string name, string initializer) in currentWideStringLiterals)
				writer.WriteLine("static const uint16_t " + name + "[] = {" + initializer + "};");
			if (currentWideStringLiterals.Count > 0)
				writer.WriteLine();

			if (!wrote)
				writer.WriteLine("/* No C definitions emitted for this file. */");
			else
				writer.Write(body.ToString());
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

			foreach (FunctionDefinition function in GetAllFunctions(definitions).Where(static function => function.Export is not null && ShouldEmitCFunction(function)))
			{
				WriteFunctionPrototype(writer, function, storage: null);
				wrote = true;
			}

			foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>().Where(static variable => variable.Export is not null && variable.IsInline))
			{
				WriteInlineConstantMacro(writer, variable);
				wrote = true;
			}
			foreach (FieldDefinition field in GetAllStaticFields(definitions).Where(static field => field.Export is not null && field.IsInline))
			{
				WriteInlineConstantMacro(writer, field);
				wrote = true;
			}

			foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>().Where(static variable => variable.Export is not null && !variable.IsInline))
			{
				WriteVariableDeclaration(writer, variable, storage: "extern");
				wrote = true;
			}
			foreach (FieldDefinition field in GetAllStaticFields(definitions).Where(static field => field.Export is not null && !field.IsInline))
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

			foreach (FunctionDefinition function in GetAllFunctions(definitions).Where(static function => function.Export is not null && ShouldEmitCFunction(function)))
			{
				if (!ShouldWriteProjectApiFunction(function))
					continue;
				WriteFunctionPrototype(writer, function, storage: null);
				wrote = true;
			}

			foreach (VariableDefinition variable in abiSurface.ExportedVariables
				.Where(static variable => variable.Definition is VariableDefinition { IsInline: true })
				.Select(static variable => (VariableDefinition)variable.Definition))
			{
				WriteInlineConstantMacro(writer, variable);
				wrote = true;
			}
			foreach (FieldDefinition field in abiSurface.ExportedVariables
				.Where(static variable => variable.Definition is FieldDefinition { IsInline: true })
				.Select(static variable => (FieldDefinition)variable.Definition))
			{
				WriteInlineConstantMacro(writer, field);
				wrote = true;
			}

			foreach (VariableDefinition variable in abiSurface.ExportedVariables
				.Where(static variable => variable.Definition is VariableDefinition { IsInline: false })
				.Select(static variable => (VariableDefinition)variable.Definition))
			{
				if (IsGeneratedVTableStorageVariable(variable))
					continue;
				WriteVariableDeclaration(writer, variable, storage: "extern");
				wrote = true;
			}
			foreach (FieldDefinition field in abiSurface.ExportedVariables
				.Where(static variable => variable.Definition is FieldDefinition { IsInline: false })
				.Select(static variable => (FieldDefinition)variable.Definition))
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
						if (!ParametersLookLikeDelegateCallContextPair(parameters, i))
							continue;
						if (!TryGetDirectFunctionValue(argument.Value, out FunctionDefinition? sourceFunction))
							continue;
						if (!TryCreateDelegateThunk(sourceFunction, parameters[i], substitutions, file, out DelegateThunk thunk))
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
			if (expectedType is null || !TryParseResolvedCallableType(expectedType, out string targetReturnType, out List<string> targetParameterTypes, out _, out string? targetCallSpec))
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

			if (forwardsContext && SourceFunctionDirectlyMatchesTarget(sourceFunction, sourceParameters, targetParameterTypes, targetCallSpec))
				return false;

			DelegateThunkKey key = new(file, sourceFunction, targetReturnType, string.Join("\u001f", targetParameterTypes), targetCallSpec ?? "", forwardsContext);
			if (delegateThunksByKey.TryGetValue(key, out DelegateThunk? cachedThunk))
			{
				thunk = cachedThunk;
				return true;
			}

			string name = CreateUniqueDelegateThunkName(sourceFunction);
			thunk = new DelegateThunk(name, sourceFunction, targetReturnType, targetParameterTypes, targetCallSpec, forwardsContext);
			delegateThunksByKey[key] = thunk;
			if (!delegateThunksByFile.TryGetValue(file, out List<DelegateThunk>? thunks))
			{
				thunks = [];
				delegateThunksByFile[file] = thunks;
			}
			thunks.Add(thunk);
			return true;
		}

		bool ParametersLookLikeDelegateCallContextPair(List<ParameterDefinition> callableParameters, int parameterIndex)
		{
			if (parameterIndex + 1 >= callableParameters.Count)
				return false;
			if (!TryParseResolvedCallableType(callableParameters[parameterIndex].ResolvedType ?? "", out _, out List<string> parameterTypes, out _, out _)
				|| parameterTypes.Count == 0
				|| !IsVoidPointerType(parameterTypes[0]))
				return false;
			if (!IsVoidPointerType(callableParameters[parameterIndex + 1].ResolvedType ?? ""))
				return false;
			return callableParameters[parameterIndex + 1].Name == callableParameters[parameterIndex].Name + "_context";
		}

		static bool SourceFunctionDirectlyMatchesTarget(FunctionDefinition sourceFunction, List<ParameterDefinition> sourceParameters, List<string> targetParameterTypes, string? targetCallSpec)
		{
			if ((sourceFunction.CallSpec ?? "") != (targetCallSpec ?? ""))
				return false;
			if (sourceParameters.Count != targetParameterTypes.Count)
				return false;
			for (int i = 0; i < sourceParameters.Count; i++)
				if (!SameCallableTypeSlot(GetCallableParameterTypeText(sourceParameters[i]), targetParameterTypes[i]))
					return false;
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
				ParameterModifier.Upon => "upon " + type,
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
			writer.WriteLine("static " + FormatDelegateThunkSignature(thunk) + ";");
		}

		void WriteDelegateThunkDefinition(TextWriter writer, DelegateThunk thunk)
		{
			writer.WriteLine("static " + FormatDelegateThunkSignature(thunk));
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

		string FormatDelegateThunkSignature(DelegateThunk thunk)
		{
			string callSpec = FormatCallSpec(thunk.CallSpec);
			string name = callSpec.Length == 0 ? thunk.Name : callSpec + " " + thunk.Name;
			return FormatResolvedType(thunk.ReturnType, name).Declaration + "(" + FormatResolvedParameterList(thunk.ParameterTypes) + ")";
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

		static bool IsGeneratedVTableStorageVariable(VariableDefinition variable)
		{
			return variable.Name.EndsWith("__storage", StringComparison.Ordinal)
				|| variable.Symbol.EndsWith("__storage", StringComparison.Ordinal);
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

		static bool ShouldEmitCFunction(FunctionDefinition function)
		{
			return function.Modifier is not FunctionModifier.Constructor and not FunctionModifier.Destructor
				&& !function.Name.StartsWith("~", StringComparison.Ordinal);
		}

		static string? PrivateFunctionStorage(FunctionDefinition function)
		{
			return function.Extern is not null || IsExternallyVisible(function) || IsGeneratedVirtualDispatchFunction(function)
				? null
				: "static";
		}

		static bool IsGeneratedVirtualDispatchFunction(FunctionDefinition function)
		{
			return function.GeneratedInfo?.Category == GeneratedDeclarationCategory.VirtualDispatch
				|| function.Provenance?.Category == GeneratedDeclarationCategory.VirtualDispatch;
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
					case ConstOfTypeReference constOf:
						AddType(constOf.Type, constOf.Type?.ResolvedType);
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

		static HashSet<string> BuildInterfaceNameSet(Compilation compilation)
		{
			HashSet<string> names = new(StringComparer.Ordinal);
			foreach (Definition definition in compilation.SharedModule?.Definitions ?? [])
			{
				if (definition is InterfaceDefinition interfaceDefinition)
					names.Add(interfaceDefinition.Name);
				else if (definition is StructDefinition { SourceInterface: InterfaceDefinition sourceInterface })
					names.Add(sourceInterface.Name);
			}
			return names;
		}

		static HashSet<string> BuildCallableInterfaceNameSet(Compilation compilation)
		{
			HashSet<string> names = BuildInterfaceNameSet(compilation);
			foreach (Definition definition in compilation.SharedModule?.Definitions ?? [])
			{
				if (definition is StructDefinition { SourceInterface: InterfaceDefinition sourceInterface })
					names.Add(sourceInterface.Name);
			}
			return names;
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
				if (parameter.Constraint is AnyTypeReference or CopyableTypeReference)
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
				writer.WriteLine(FormatCallableNewtypeTypedef(callable, definition.Parameters, name) + ";");
				return;
			}
			if (definition.UnderlyingType is IterTypeReference iter)
			{
				writer.WriteLine(FormatIterNewtypeTypedef(iter, definition.Parameters, name) + ";");
				return;
			}

			CType type = FormatType(definition.UnderlyingType, name);
			writer.WriteLine("typedef " + type.Declaration + ";");
		}

		void WriteCallableAliasTypedef(TextWriter writer, string resolvedType)
		{
			string name = CTypeName(resolvedType);
			if (!TryParseResolvedCallableType(resolvedType, out string returnType, out List<string> parameterTypes, out string? targetSpec, out string? callSpec))
				return;

			if (TryGetExpandedStorageComponentsForC(returnType, out List<(string Name, string Type)> components)
				&& components.Count > 0
				&& IsResolvedCallableType(components[0].Type)
				&& components[0].Type != resolvedType)
			{
				WriteCallableAliasTypedef(writer, components[0].Type);
			}

			if (!emittedNames.Add("callable-typedef:" + name))
				return;
			writer.WriteLine("typedef " + FormatInlineResolvedFunctionPointer(returnType, parameterTypes, name, targetSpec, callSpec) + ";");
		}

		string FormatResolvedNamedParameterList(List<(string Type, string Name)> parameters)
		{
			if (parameters.Count == 0)
				return "void";
			List<string> parts = [];
			for (int i = 0; i < parameters.Count; i++)
				parts.Add(FormatResolvedParameter(parameters[i].Type, parameters[i].Name));
			return string.Join(", ", parts);
		}

		string FormatResolvedParameter(string parameterType, string declarator)
		{
			if (parameterType.StartsWith("in ", StringComparison.Ordinal))
				return FormatResolvedType(parameterType[3..].TrimStart() + "*", declarator).Declaration;
			if (parameterType.StartsWith("out ", StringComparison.Ordinal))
				return FormatResolvedType(parameterType[4..].TrimStart() + "*", declarator).Declaration;
			if (parameterType.StartsWith("thrown ", StringComparison.Ordinal))
				return FormatResolvedType(parameterType[7..].TrimStart() + "*", declarator).Declaration;
			if (parameterType.StartsWith("within ", StringComparison.Ordinal))
				return FormatResolvedType(parameterType[7..].TrimStart(), declarator).Declaration;
			if (parameterType.StartsWith("upon ", StringComparison.Ordinal))
				return FormatResolvedType(parameterType[5..].TrimStart(), declarator).Declaration;
			if (TryNormalizeCallableInterfaceParameterType(parameterType, out string normalizedParameterType))
				return FormatResolvedType(normalizedParameterType, declarator).Declaration;
			return FormatResolvedType(parameterType, declarator).Declaration;
		}

		bool TryNormalizeCallableInterfaceParameterType(string parameterType, out string normalizedParameterType)
		{
			normalizedParameterType = parameterType;
			string type = StripLifetimeOnly(parameterType.Trim());
			int pointerCount = 0;
			while (type.EndsWith("*", StringComparison.Ordinal))
			{
				pointerCount++;
				type = type[..^1].TrimEnd();
			}

			if (pointerCount != 1)
				return false;

			string baseType = StripTopLevelConstForC(type);
			baseType = baseType.StartsWith("volatile ", StringComparison.Ordinal) ? baseType[9..].TrimStart() : baseType;
			baseType = baseType.EndsWith(" volatile", StringComparison.Ordinal) ? baseType[..^9].TrimEnd() : baseType;
			int generic = baseType.IndexOf('<', StringComparison.Ordinal);
			string baseName = generic < 0 ? baseType : baseType[..generic];
			if (!callableInterfaceNames.Contains(baseName))
				return false;

			normalizedParameterType = StripLifetimeOnly(parameterType.Trim()) + "*";
			return true;
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
				parts.Add(FormatResolvedParameter(parameterType, declarator));
			}
			return string.Join(", ", parts);
		}

		void WriteEnumDefinition(TextWriter writer, EnumDefinition definition)
		{
			string name = CName(definition);
			if (!emittedNames.Add("enum:" + name))
				return;

			TypeReference underlying = definition.UnderlyingType ?? new PrimitiveTypeReference { Type = PrimitiveType.UInt, ResolvedType = "uint" };
			writer.WriteLine("typedef " + FormatTypeOrResolved(underlying, underlying.ResolvedType ?? "uint", name).Declaration + ";");
			foreach (VariableDefinition value in definition.Values)
				writer.WriteLine("#define " + CName(value) + " ((" + name + ")" + FormatConstantValue(value.ConstantValue) + ")");
		}

		void WriteInlineConstantMacro(TextWriter writer, VariableDefinition variable)
		{
			string type = FormatTypeOrResolved(variable.Type, variable.ResolvedType, "").Declaration.Trim();
			writer.WriteLine("#define " + CName(variable) + " ((" + type + ")" + FormatConstantValue(variable.ConstantValue) + ")");
		}

		void WriteInlineConstantMacro(TextWriter writer, FieldDefinition field)
		{
			string type = FormatTypeOrResolved(field.Type, field.ResolvedType, "").Declaration.Trim();
			writer.WriteLine("#define " + CName(field) + " ((" + type + ")" + FormatConstantValue(field.ConstantValue) + ")");
		}

		static string FormatConstantValue(ConstantValue? value)
		{
			return value switch
			{
				ConstantValue.Integer integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
				ConstantValue.Boolean boolean => boolean.Value ? "1" : "0",
				ConstantValue.String text => FormatCStringLiteral(text.Value),
				ConstantValue.Character text => FormatCCharacterLiteral(text.Value),
				ConstantValue.Null => "0",
				_ => "0"
			};
		}

		static string FormatCStringLiteral(string value)
		{
			return "\"" + EscapeCString(value, quote: '"') + "\"";
		}

		static string FormatCCharacterLiteral(string value)
		{
			return "'" + EscapeCString(value, quote: '\'') + "'";
		}

		static string EscapeCString(string value, char quote)
		{
			System.Text.StringBuilder builder = new();
			foreach (char ch in value)
			{
				switch (ch)
				{
					case '\\':
						builder.Append("\\\\");
						break;
					case '\0':
						builder.Append("\\0");
						break;
					case '\n':
						builder.Append("\\n");
						break;
					case '\r':
						builder.Append("\\r");
						break;
					case '\t':
						builder.Append("\\t");
						break;
					default:
						if (ch == quote)
							builder.Append('\\').Append(ch);
						else
							builder.Append(ch);
						break;
				}
			}
			return builder.ToString();
		}

		void WriteLayoutDefinition(TextWriter writer, TypeDefinition type)
		{
			switch (type)
			{
				case StructDefinition structDefinition:
					WriteFieldLayout(writer, structDefinition, structDefinition.Fields);
					break;
				case ClassDefinition classDefinition:
					if (classDefinition.Extern is null)
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
				ConstOfTypeReference constOf => ResolveTypeDefinitionName(constOf.Type),
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
						writer.WriteLine("\t" + FormatFieldLayoutDeclaration(field) + ";");
					writer.WriteLine("};");
				});
			});
		}

		string FormatFieldLayoutDeclaration(FieldDefinition field)
		{
			if (TryFormatFlexibleArrayField(field.Type, CName(field), out string declaration))
				return declaration;
			if (IsGeneratedRawInterfaceVTableStorage(field))
				return FormatResolvedType(field.ResolvedType!, CName(field), normalizeInterfacePointer: false).Declaration;
			return FormatTypeOrResolved(field.Type, field.ResolvedType, CName(field)).Declaration;
		}

		bool TryFormatFlexibleArrayField(TypeReference? type, string declarator, out string declaration)
		{
			declaration = "";
			if (type is FixedArrayTypeReference { Length: 0 } fixedArray)
			{
				declaration = FormatType(fixedArray.ElementType, declarator + "[]").Declaration;
				return true;
			}
			return false;
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
				writer.WriteLine(prefix + FormatFunctionSignature(function, callSpec + name) + ";");
			});
		}

		void WriteVariableDeclaration(TextWriter writer, VariableDefinition variable, string? storage)
		{
			string prefix = BuildDeclarationPrefix(variable, storage);
			writer.WriteLine(prefix + FormatStorageDefinitionType(variable, CName(variable)).Declaration + ";");
		}

		void WriteVariableDefinition(TextWriter writer, VariableDefinition variable, string? storage)
		{
			string prefix = BuildDeclarationPrefix(variable, storage);
			writer.Write(prefix + FormatStorageDefinitionType(variable, CName(variable)).Declaration);
			if (variable.InitialValue is not null)
				writer.Write(" = " + FormatFileScopeInitializer(variable.ResolvedType, variable.InitialValue));
			writer.WriteLine(";");
		}

		void WriteFieldStorageDeclaration(TextWriter writer, FieldDefinition field, string? storage)
		{
			string prefix = BuildDeclarationPrefix(field, storage);
			writer.WriteLine(prefix + FormatStorageDefinitionType(field, CName(field)).Declaration + ";");
		}

		void WriteFieldStorageDefinition(TextWriter writer, FieldDefinition field, string? storage)
		{
			string prefix = BuildDeclarationPrefix(field, storage);
			writer.Write(prefix + FormatStorageDefinitionType(field, CName(field)).Declaration);
			if (field.InitialValue is not null)
				writer.Write(" = " + FormatFileScopeInitializer(field.ResolvedType, field.InitialValue));
			writer.WriteLine(";");
		}

		CType FormatStorageDefinitionType(Definition definition, string declarator)
		{
			if (IsGeneratedRawInterfaceVTableStorage(definition))
				return FormatResolvedType(definition.ResolvedType!, declarator, normalizeInterfacePointer: false);
			return definition switch
			{
				VariableDefinition variable => FormatTypeOrResolved(variable.Type, variable.ResolvedType, declarator),
				FieldDefinition field => FormatTypeOrResolved(field.Type, field.ResolvedType, declarator),
				_ => FormatTypeOrResolved(null, definition.ResolvedType, declarator)
			};
		}

		bool IsGeneratedRawInterfaceVTableStorage(Definition definition)
		{
			if (definition.ResolvedType is not string resolvedType || !TryGetPointerElementType(resolvedType, out string elementType))
				return false;
			string baseType = StripTopLevelConstForC(elementType);
			if (!IsInterfaceResolvedName(baseType))
				return false;
			return definition.GeneratedInfo?.Category == GeneratedDeclarationCategory.Interface
				|| definition.Provenance?.Category == GeneratedDeclarationCategory.Interface
				|| definition.Name.StartsWith("_vtableof_", StringComparison.Ordinal)
				|| definition.Symbol.StartsWith("_vtableof_", StringComparison.Ordinal)
				|| definition.Name == "_vt"
				|| definition.Symbol == "_vt"
				|| definition.Name.EndsWith("_" + baseType, StringComparison.Ordinal)
				|| definition.Symbol.EndsWith("_" + baseType, StringComparison.Ordinal);
		}

		void WriteFunctionDefinition(TextWriter writer, FunctionDefinition function, string? storage)
		{
			string prefix = BuildDeclarationPrefix(function, storage);
			string callSpec = FormatCallSpec(function.CallSpec);
			if (callSpec.Length > 0)
				callSpec += " ";
			WithGenericContext(function, () =>
			{
				writer.WriteLine(prefix + FormatFunctionSignature(function, callSpec + CName(function)));
				if (TryBuildAsyncFrameInfo(function) is AsyncFrameInfo frame)
					WriteAsyncFrameEntryBody(writer, frame);
				else
					WriteFunctionBody(writer, function);
				writer.WriteLine();
			});
		}

		AsyncFrameInfo? TryBuildAsyncFrameInfo(FunctionDefinition function)
		{
			if (!function.IsAsync || function.Body is null || function.AwaitSites.Count == 0)
				return null;
			List<AsyncAwaitInfo> awaits = [];
			CollectNonTailAsyncAwaits(function.Body, awaits, CName(function));
			if (awaits.Count == 0)
				return null;

			string baseName = CName(function);
			List<AsyncFrameField> fields =
			[
				new(null, "state", "int"),
				new(null, "complete", BuildAsyncCompletionCallableType(function.ReturnType, function.ResolvedType, function.Parameters)),
				new(null, "complete_context", "void*")
			];
			AddImplicitThisFrameField(function, fields);
			foreach (ParameterDefinition parameter in GetAbiOrderedParameters(function.Parameters))
			{
				if (parameter.Modifier == ParameterModifier.Thrown)
				{
					fields.Add(new(parameter, CName(parameter), parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? "#ERROR", parameter.Type));
					continue;
				}
				fields.Add(new(parameter, CName(parameter), parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? "#ERROR", parameter.Type));
			}
			CollectAsyncFrameLocals(function.Body, fields);
			foreach (AsyncAwaitInfo awaitInfo in awaits)
			{
				if (awaitInfo.ResultType is not null && awaitInfo.ResultType != "void")
					fields.Add(new(awaitInfo.Await, "await" + awaitInfo.Index.ToString(CultureInfo.InvariantCulture) + "_result", awaitInfo.ResultType));
				if (awaitInfo.ThrownType is not null)
					fields.Add(new(awaitInfo.Await, "await" + awaitInfo.Index.ToString(CultureInfo.InvariantCulture) + "_error", awaitInfo.ThrownType));
				if (FindCatchArgument(awaitInfo.Call) is { Target: not null } catchArgument
					&& catchArgument.Target.Names is { Count: > 0 }
					&& catchArgument.Target.Names[0] != "_")
					fields.Add(new(catchArgument.Target, CName(catchArgument.Target), catchArgument.Target.ResolvedType ?? awaitInfo.ThrownType ?? "#ERROR", catchArgument.Target.Type));
			}

			return new AsyncFrameInfo(function, baseName + "_asyncFrame", baseName + "_asyncResume", fields, awaits);
		}

		void AddImplicitThisFrameField(FunctionDefinition function, List<AsyncFrameField> fields)
		{
			if (!RequiresImplicitThisParameter(function) || !containingTypes.TryGetValue(function, out TypeDefinition? type))
				return;
			string resolvedThisType = function.AbiThisType?.ResolvedType ?? function.EffectiveThisParameter?.ResolvedType ?? GetImplicitThisResolvedType(type);
			fields.Add(new(function.EffectiveThisParameter, "this", resolvedThisType, function.AbiThisType));
		}

		void CollectNonTailAsyncAwaits(Statement? statement, List<AsyncAwaitInfo> awaits, string baseName)
		{
			switch (statement)
			{
				case null:
					return;
				case BlockStatement block:
					foreach (Statement child in block.Statements)
						CollectNonTailAsyncAwaits(child, awaits, baseName);
					return;
				case ReturnStatement { Expression: UnaryExpression { Operator: UnaryOperator.Await, Operand: CallExpression } }:
					return;
				case DeclarationStatement { InitialValue: UnaryExpression { Operator: UnaryOperator.Await, Operand: CallExpression call } awaitExpression }:
					AddAwait(awaitExpression, call);
					return;
				case ExpressionStatement { Expression: UnaryExpression { Operator: UnaryOperator.Await, Operand: CallExpression call } awaitExpression }:
					AddAwait(awaitExpression, call);
					return;
				case IfStatement ifStatement:
					CollectNonTailAsyncAwaits(ifStatement.Body, awaits, baseName);
					CollectNonTailAsyncAwaits(ifStatement.ElseBody, awaits, baseName);
					return;
				case WhileStatement whileStatement:
					CollectNonTailAsyncAwaits(whileStatement.Body, awaits, baseName);
					return;
				case ForStatement forStatement:
					CollectNonTailAsyncAwaits(forStatement.Body, awaits, baseName);
					return;
				case TryStatement tryStatement:
					CollectNonTailAsyncAwaits(tryStatement.Body, awaits, baseName);
					foreach (CatchStatement catchStatement in tryStatement.Catches)
						CollectNonTailAsyncAwaits(catchStatement.Body, awaits, baseName);
					CollectNonTailAsyncAwaits(tryStatement.Finally, awaits, baseName);
					return;
				case FinallyStatement finallyStatement:
					CollectNonTailAsyncAwaits(finallyStatement.Body, awaits, baseName);
					return;
				case WithinStatement withinStatement:
					CollectNonTailAsyncAwaits(withinStatement.Body, awaits, baseName);
					return;
			}

			void AddAwait(UnaryExpression awaitExpression, CallExpression call)
			{
				int index = awaits.Count;
				string? resultType = awaitExpression.ResolvedType is null or "void" ? null : awaitExpression.ResolvedType;
				string? thrownType = GetAwaitedThrownType(call);
				awaits.Add(new AsyncAwaitInfo(awaitExpression, call, index, resultType, thrownType, baseName + "_asyncComplete" + index.ToString(CultureInfo.InvariantCulture)));
			}
		}

		void CollectAsyncFrameLocals(Statement? statement, List<AsyncFrameField> fields)
		{
			switch (statement)
			{
				case null:
					return;
				case BlockStatement block:
					foreach (Statement child in block.Statements)
						CollectAsyncFrameLocals(child, fields);
					return;
				case DeclarationStatement declaration:
					fields.Add(new(declaration.Target, CName(declaration.Target), declaration.Target.ResolvedType ?? "#ERROR", declaration.Target.Type));
					return;
				case IfStatement ifStatement:
					CollectAsyncFrameLocals(ifStatement.Body, fields);
					CollectAsyncFrameLocals(ifStatement.ElseBody, fields);
					return;
				case WhileStatement whileStatement:
					CollectAsyncFrameLocals(whileStatement.Body, fields);
					return;
				case ForStatement forStatement:
					if (forStatement.Condition.Declaration is not null)
						CollectAsyncFrameLocals(forStatement.Condition.Declaration, fields);
					CollectAsyncFrameLocals(forStatement.Body, fields);
					return;
				case TryStatement tryStatement:
					CollectAsyncFrameLocals(tryStatement.Body, fields);
					foreach (CatchStatement catchStatement in tryStatement.Catches)
						CollectAsyncFrameLocals(catchStatement.Body, fields);
					CollectAsyncFrameLocals(tryStatement.Finally, fields);
					return;
				case FinallyStatement finallyStatement:
					CollectAsyncFrameLocals(finallyStatement.Body, fields);
					return;
				case WithinStatement withinStatement:
					CollectAsyncFrameLocals(withinStatement.Body, fields);
					return;
			}
		}

		string? GetAwaitedThrownType(CallExpression call)
		{
			FunctionDefinition? function = TryGetCallFunction(call);
			if (function?.IsAsync == true)
				return function.Parameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Thrown) is ParameterDefinition thrown
					? thrown.ResolvedType ?? thrown.Type?.ResolvedType ?? "#ERROR"
					: null;
			if (function?.Parameters is [.., ParameterDefinition last]
				&& StripTypeDecorators(last.Type) is CallableTypeReference callback)
			{
				foreach (ParameterDefinition parameter in callback.Parameters)
					if (parameter.Modifier == ParameterModifier.Thrown)
						return parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? "#ERROR";
			}
				if (function is null
					&& call.Target?.ResolvedType is string targetType
					&& CallableShapeService.TryParseCallableShape(targetType, out CallableShape targetShape)
				&& targetShape.Parameters.Count > 0)
			{
				string callbackType = targetShape.Parameters[^1];
				if (CallableShapeService.TryParseCallableShape(callbackType, out CallableShape callbackShape))
				{
					foreach (string parameter in callbackShape.Parameters)
					{
						CallableSlot slot = CallableShapeService.ParseCallableSlot(parameter);
						if (slot.Modifier == "thrown")
							return slot.Type;
					}
				}
			}
			return null;
		}

		void WriteAsyncFrameDeclaration(TextWriter writer, AsyncFrameInfo frame)
		{
			writer.WriteLine("typedef struct " + frame.FrameType + " " + frame.FrameType + ";");
			writer.WriteLine("struct " + frame.FrameType);
			writer.WriteLine("{");
			HashSet<string> emitted = [];
			foreach (AsyncFrameField field in frame.Fields)
			{
				if (!emitted.Add(field.Name))
					continue;
				writer.WriteLine("\t" + FormatAsyncFrameField(field) + ";");
			}
			writer.WriteLine("};");
		}

		string FormatAsyncFrameField(AsyncFrameField field)
		{
			if (field.Type.StartsWith("fn ", StringComparison.Ordinal))
			{
				if (CallableShapeService.TryParseCallableShape(field.Type, out CallableShape shape))
				{
					List<(string Type, string Name)> parameters = [];
					for (int i = 0; i < shape.Parameters.Count; i++)
						parameters.Add((shape.Parameters[i], "arg" + i.ToString(CultureInfo.InvariantCulture)));
					return FormatInlineResolvedFunctionPointer(shape.ReturnType, parameters, field.Name);
				}
			}
			return FormatTypeOrResolved(field.TypeReference, field.Type, field.Name).Declaration;
		}

		void WriteAsyncFrameHelperPrototypes(TextWriter writer, AsyncFrameInfo frame)
		{
			writer.WriteLine("static void " + frame.ResumeName + "(void *context);");
			foreach (AsyncAwaitInfo awaitInfo in frame.Awaits)
				writer.WriteLine("static " + FormatAsyncAwaitCompletionSignature(awaitInfo, awaitInfo.CompleteName) + ";");
		}

		string FormatAsyncAwaitCompletionSignature(AsyncAwaitInfo awaitInfo, string name)
		{
			List<(string Type, string Name)> slots = [("void*", "context")];
			if (awaitInfo.ResultType is not null && awaitInfo.ResultType != "void")
				slots.Add((awaitInfo.ResultType, "result"));
			if (awaitInfo.ThrownType is not null)
				slots.Add((awaitInfo.ThrownType, "error"));
			return FormatResolvedType("void", name + "(" + string.Join(", ", slots.Select(slot => FormatResolvedType(slot.Type, slot.Name).Declaration)) + ")").Declaration;
		}

		void WriteAsyncFrameHelperDefinitions(TextWriter writer, AsyncFrameInfo frame)
		{
			foreach (AsyncAwaitInfo awaitInfo in frame.Awaits)
			{
				writer.WriteLine("static " + FormatAsyncAwaitCompletionSignature(awaitInfo, awaitInfo.CompleteName));
				writer.WriteLine("{");
				WriteIndent(writer, 1);
				writer.WriteLine(frame.FrameType + " *frame = (" + frame.FrameType + " *)context;");
				if (awaitInfo.ResultType is not null && awaitInfo.ResultType != "void")
				{
					WriteIndent(writer, 1);
					writer.WriteLine("frame->await" + awaitInfo.Index.ToString(CultureInfo.InvariantCulture) + "_result = result;");
				}
				if (awaitInfo.ThrownType is not null)
				{
					WriteIndent(writer, 1);
					writer.WriteLine("frame->await" + awaitInfo.Index.ToString(CultureInfo.InvariantCulture) + "_error = error;");
				}
				WriteIndent(writer, 1);
				WriteAsyncFrameScheduleResume(writer, frame, 1);
				writer.WriteLine("}");
				writer.WriteLine();
			}

			writer.WriteLine("static void " + frame.ResumeName + "(void *context)");
			writer.WriteLine("{");
			WriteIndent(writer, 1);
			writer.WriteLine(frame.FrameType + " *frame = (" + frame.FrameType + " *)context;");
			WriteIndent(writer, 1);
			writer.WriteLine("switch (frame->state)");
			WriteIndent(writer, 1);
			writer.WriteLine("{");
			for (int i = 0; i <= frame.Awaits.Count; i++)
			{
				WriteIndent(writer, 2);
				writer.WriteLine("case " + i.ToString(CultureInfo.InvariantCulture) + ": goto __async_state" + i.ToString(CultureInfo.InvariantCulture) + ";");
			}
			WriteIndent(writer, 1);
			writer.WriteLine("}");
			Dictionary<BindableNode, string> savedReplacements = new(currentAsyncFrameReplacements);
			Dictionary<string, string> savedNameReplacements = new(currentAsyncFrameNameReplacements, StringComparer.Ordinal);
			string? savedFrameName = currentAsyncFrameName;
			FunctionDefinition? savedFunction = currentFunction;
			currentAsyncFrameReplacements.Clear();
			currentAsyncFrameNameReplacements.Clear();
			foreach (AsyncFrameField field in frame.Fields)
			{
				if (field.Node is not null)
					currentAsyncFrameReplacements[field.Node] = "frame->" + field.Name;
				currentAsyncFrameNameReplacements[field.Name] = "frame->" + field.Name;
			}
			currentAsyncFrameName = "frame";
			currentFunction = frame.Function;
			currentAsyncLoopLabelIndex = 0;
			int awaitCursor = 0;
			WriteIndent(writer, 0);
			writer.WriteLine("__async_state0: ;");
			WriteAsyncFrameStatements(writer, frame, frame.Function.Body?.Statements ?? [], 1, ref awaitCursor);
			currentAsyncFrameReplacements.Clear();
			foreach (KeyValuePair<BindableNode, string> replacement in savedReplacements)
				currentAsyncFrameReplacements[replacement.Key] = replacement.Value;
			currentAsyncFrameNameReplacements.Clear();
			foreach (KeyValuePair<string, string> replacement in savedNameReplacements)
				currentAsyncFrameNameReplacements[replacement.Key] = replacement.Value;
			currentAsyncFrameName = savedFrameName;
			currentFunction = savedFunction;
			writer.WriteLine("}");
			writer.WriteLine();
		}

		void WriteAsyncFrameScheduleResume(TextWriter writer, AsyncFrameInfo frame, int indent)
		{
			FunctionDefinition? resumeFunction = frame.Function.AsyncResumeFunction;
			if (resumeFunction is null)
			{
				AddUnsupported(frame.Function, "async resumer");
				writer.WriteLine("/* missing async resumer */");
				return;
			}
			writer.WriteLine(CName(resumeFunction) + "(" + FormatAsyncResumerFrameExpression(frame) + ", " + frame.ResumeName + ", frame);");
		}

		string FormatAsyncResumerFrameExpression(AsyncFrameInfo frame)
		{
			if (frame.Function.AsyncResumerIsReceiver)
				return "frame->this";
			if (frame.Function.AsyncResumerParameter is ParameterDefinition parameter)
				return "frame->" + CName(parameter);
			return "NULL";
		}

		void WriteAsyncFrameEntryBody(TextWriter writer, AsyncFrameInfo frame)
		{
			writer.WriteLine("{");
			WriteAsyncFrameAllocation(writer, frame, 1);
			WriteIndent(writer, 1);
			writer.WriteLine("*frame = (" + frame.FrameType + "){0};");
			WriteIndent(writer, 1);
			writer.WriteLine("frame->complete = complete;");
			WriteIndent(writer, 1);
			writer.WriteLine("frame->complete_context = complete_context;");
			if (RequiresImplicitThisParameter(frame.Function) && containingTypes.ContainsKey(frame.Function))
			{
				WriteIndent(writer, 1);
				writer.WriteLine("frame->this = " + (NeedsAbiThisFixup(frame.Function) ? "ctx" : "this") + ";");
			}
			foreach (ParameterDefinition parameter in GetAbiOrderedParameters(frame.Function.Parameters))
			{
				if (parameter.Modifier == ParameterModifier.Thrown)
					continue;
				WriteIndent(writer, 1);
				writer.WriteLine("frame->" + CName(parameter) + " = " + CName(parameter) + ";");
			}
			if (frame.Function.Parameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Thrown) is ParameterDefinition thrown)
			{
				WriteIndent(writer, 1);
				writer.WriteLine("frame->" + CName(thrown) + " = 0;");
			}
			WriteIndent(writer, 1);
			writer.WriteLine(frame.ResumeName + "(frame);");
			WriteIndent(writer, 1);
			writer.WriteLine("return;");
			writer.WriteLine("}");
		}

		void WriteAsyncFrameStatements(TextWriter writer, AsyncFrameInfo frame, List<Statement> statements, int indent, ref int awaitCursor)
		{
			foreach (Statement statement in statements)
				WriteAsyncFrameStatement(writer, frame, statement, indent, ref awaitCursor);
		}

		void WriteAsyncFrameStatement(TextWriter writer, AsyncFrameInfo frame, Statement statement, int indent, ref int awaitCursor)
		{
			switch (statement)
			{
				case EmptyStatement:
					WriteIndent(writer, indent);
					writer.WriteLine(";");
					return;
				case BlockStatement block:
					WriteAsyncFrameStatements(writer, frame, block.Statements, indent, ref awaitCursor);
					return;
				case DeclarationStatement declaration:
					WriteAsyncFrameDeclarationStatement(writer, frame, declaration, indent, ref awaitCursor);
					return;
				case ExpressionStatement { Expression: UnaryExpression { Operator: UnaryOperator.Await, Operand: CallExpression call } }:
					WriteAsyncFrameAwaitCall(writer, frame, call, indent, ref awaitCursor);
					WriteIndent(writer, 0);
					writer.WriteLine("__async_state" + awaitCursor.ToString(CultureInfo.InvariantCulture) + ": ;");
					WriteAsyncFrameAwaitErrorHandling(writer, frame, call, awaitCursor - 1, indent);
					return;
				case ExpressionStatement expression:
					WriteIndent(writer, indent);
					writer.WriteLine(FormatExpression(expression.Expression) + ";");
					return;
				case ReturnStatement { Expression: UnaryExpression { Operator: UnaryOperator.Await, Operand: CallExpression awaitCall } }:
					WriteAsyncFrameFree(writer, frame, indent);
					WriteIndent(writer, indent);
					writer.WriteLine(FormatTailAwaitCallExpression(awaitCall) + ";");
					WriteIndent(writer, indent);
					writer.WriteLine("return;");
					return;
				case ReturnStatement ret:
					WriteAsyncFrameReturn(writer, frame, ret, indent);
					return;
				case IfStatement ifStatement when !StatementContainsAwait(ifStatement.Body) && !StatementContainsAwait(ifStatement.ElseBody):
					WriteIndent(writer, indent);
					writer.WriteLine("if (" + FormatExpression(ifStatement.Condition) + ")");
					WriteAsyncFrameEmbeddedStatement(writer, frame, ifStatement.Body, indent, ref awaitCursor);
					if (ifStatement.ElseBody is not null)
					{
						WriteIndent(writer, indent);
						writer.WriteLine("else");
						WriteAsyncFrameEmbeddedStatement(writer, frame, ifStatement.ElseBody, indent, ref awaitCursor);
					}
					return;
				case WhileStatement whileStatement:
					WriteAsyncFrameWhileStatement(writer, frame, whileStatement, indent, ref awaitCursor);
					return;
				case ForStatement forStatement:
					WriteAsyncFrameForStatement(writer, frame, forStatement, indent, ref awaitCursor);
					return;
				case LabelStatement label:
					writer.WriteLine(SanitizeIdentifier(label.Name ?? "label") + ": ;");
					return;
				case GotoStatement go:
					WriteIndent(writer, indent);
					writer.WriteLine("goto " + SanitizeIdentifier(go.Target?.Name ?? go.TargetName ?? "label") + ";");
					return;
				case BreakStatement:
					WriteIndent(writer, indent);
					writer.WriteLine("break;");
					return;
				case ContinueStatement:
					WriteIndent(writer, indent);
					writer.WriteLine("continue;");
					return;
				default:
					AddUnsupported(statement, "async frame statement");
					WriteIndent(writer, indent);
					writer.WriteLine("/* unsupported async frame statement " + statement.GetType().Name + " */");
					return;
			}
		}

		void WriteAsyncFrameWhileStatement(TextWriter writer, AsyncFrameInfo frame, WhileStatement whileStatement, int indent, ref int awaitCursor)
		{
			if (ExpressionContainsAwait(whileStatement.Condition))
			{
				AddUnsupported(whileStatement, "async while condition with await");
				WriteIndent(writer, indent);
				writer.WriteLine("/* unsupported async while condition with await */");
				return;
			}

			int index = currentAsyncLoopLabelIndex++;
			string loopLabel = "__async_loop" + index.ToString(CultureInfo.InvariantCulture);
			string endLabel = "__async_loop_end" + index.ToString(CultureInfo.InvariantCulture);
			WriteIndent(writer, 0);
			writer.WriteLine(loopLabel + ": ;");
			WriteIndent(writer, indent);
			writer.WriteLine("if (!(" + FormatExpression(whileStatement.Condition) + ")) goto " + endLabel + ";");
			WriteAsyncFrameEmbeddedStatement(writer, frame, whileStatement.Body, indent, ref awaitCursor);
			WriteIndent(writer, indent);
			writer.WriteLine("goto " + loopLabel + ";");
			WriteIndent(writer, 0);
			writer.WriteLine(endLabel + ": ;");
		}

		void WriteAsyncFrameForStatement(TextWriter writer, AsyncFrameInfo frame, ForStatement forStatement, int indent, ref int awaitCursor)
		{
			Expression? initializer = null;
			if (forStatement.Condition.Declaration is null && forStatement.Condition.Clauses.Count > 0)
				initializer = forStatement.Condition.Clauses[0];
			int conditionIndex = forStatement.Condition.Declaration is null ? 1 : 0;
			int incrementIndex = forStatement.Condition.Declaration is null ? 2 : 1;
			Expression? condition = forStatement.Condition.Clauses.Count > conditionIndex ? forStatement.Condition.Clauses[conditionIndex] : null;
			Expression? increment = forStatement.Condition.Clauses.Count > incrementIndex ? forStatement.Condition.Clauses[incrementIndex] : null;

			if (StatementContainsAwait(forStatement.Condition.Declaration)
				|| ExpressionContainsAwait(initializer)
				|| ExpressionContainsAwait(condition)
				|| ExpressionContainsAwait(increment))
			{
				AddUnsupported(forStatement, "async for clause with await");
				WriteIndent(writer, indent);
				writer.WriteLine("/* unsupported async for clause with await */");
				return;
			}

			if (forStatement.Condition.Declaration is DeclarationStatement declaration)
				WriteAsyncFrameDeclarationStatement(writer, frame, declaration, indent, ref awaitCursor);
			else if (initializer is not null)
			{
				WriteIndent(writer, indent);
				writer.WriteLine(FormatExpression(initializer) + ";");
			}

			int index = currentAsyncLoopLabelIndex++;
			string loopLabel = "__async_for" + index.ToString(CultureInfo.InvariantCulture);
			string endLabel = "__async_for_end" + index.ToString(CultureInfo.InvariantCulture);
			WriteIndent(writer, 0);
			writer.WriteLine(loopLabel + ": ;");
			if (condition is not null)
			{
				WriteIndent(writer, indent);
				writer.WriteLine("if (!(" + FormatExpression(condition) + ")) goto " + endLabel + ";");
			}
			WriteAsyncFrameEmbeddedStatement(writer, frame, forStatement.Body, indent, ref awaitCursor);
			if (increment is not null)
			{
				WriteIndent(writer, indent);
				writer.WriteLine(FormatExpression(increment) + ";");
			}
			WriteIndent(writer, indent);
			writer.WriteLine("goto " + loopLabel + ";");
			WriteIndent(writer, 0);
			writer.WriteLine(endLabel + ": ;");
		}

		void WriteAsyncFrameEmbeddedStatement(TextWriter writer, AsyncFrameInfo frame, Statement? statement, int indent, ref int awaitCursor)
		{
			if (statement is null)
			{
				WriteIndent(writer, indent);
				writer.WriteLine(";");
				return;
			}
			WriteIndent(writer, indent);
			writer.WriteLine("{");
			if (statement is BlockStatement block)
				WriteAsyncFrameStatements(writer, frame, block.Statements, indent + 1, ref awaitCursor);
			else
				WriteAsyncFrameStatement(writer, frame, statement, indent + 1, ref awaitCursor);
			WriteIndent(writer, indent);
			writer.WriteLine("}");
		}

		void WriteAsyncFrameDeclarationStatement(TextWriter writer, AsyncFrameInfo frame, DeclarationStatement declaration, int indent, ref int awaitCursor)
		{
			string target = FormatVariableReference(declaration.Target);
			if (declaration.InitialValue is UnaryExpression { Operator: UnaryOperator.Await, Operand: CallExpression call } awaitExpression)
			{
				int index = awaitCursor;
				WriteAsyncFrameAwaitCall(writer, frame, call, indent, ref awaitCursor);
				WriteIndent(writer, 0);
				writer.WriteLine("__async_state" + awaitCursor.ToString(CultureInfo.InvariantCulture) + ": ;");
				WriteAsyncFrameAwaitErrorHandling(writer, frame, call, index, indent);
				if (awaitExpression.ResolvedType is not null and not "void")
				{
					WriteIndent(writer, indent);
					writer.WriteLine(target + " = frame->await" + index.ToString(CultureInfo.InvariantCulture) + "_result;");
				}
				return;
			}

			if (declaration.InitialValue is not null)
			{
				WriteIndent(writer, indent);
				writer.WriteLine(target + " = " + FormatDeclarationInitializer(declaration.Target.ResolvedType, declaration.InitialValue) + ";");
			}
		}

		void WriteAsyncFrameAwaitCall(TextWriter writer, AsyncFrameInfo frame, CallExpression call, int indent, ref int awaitCursor)
		{
			AsyncAwaitInfo awaitInfo = frame.Awaits[Math.Min(awaitCursor, frame.Awaits.Count - 1)];
			int nextState = awaitCursor + 1;
			WriteIndent(writer, indent);
			writer.WriteLine("frame->state = " + nextState.ToString(CultureInfo.InvariantCulture) + ";");
			WriteIndent(writer, indent);
			writer.WriteLine(FormatAwaitCallWithCompletion(call, awaitInfo.CompleteName, "frame") + ";");
			WriteIndent(writer, indent);
			writer.WriteLine("return;");
			awaitCursor = nextState;
		}

		void WriteAsyncFrameAwaitErrorHandling(TextWriter writer, AsyncFrameInfo frame, CallExpression call, int awaitIndex, int indent)
		{
			AsyncAwaitInfo? awaitInfo = frame.Awaits.FirstOrDefault(info => info.Index == awaitIndex);
			if (awaitInfo?.ThrownType is null)
				return;
			string errorField = "frame->await" + awaitIndex.ToString(CultureInfo.InvariantCulture) + "_error";
			if (FindCatchArgument(call) is ArgumentExpression catchArgument)
			{
				if (TryFormatCatchAssignmentTarget(catchArgument, out string? catchTarget))
				{
					WriteIndent(writer, indent);
					writer.WriteLine(catchTarget + " = " + errorField + ";");
				}
				return;
			}

			if (frame.Function.Parameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Thrown) is not ParameterDefinition thrown)
				return;
			WriteIndent(writer, indent);
			writer.WriteLine("if (" + errorField + " != 0)");
			WriteIndent(writer, indent);
			writer.WriteLine("{");
			WriteIndent(writer, indent + 1);
			writer.WriteLine("frame->" + CName(thrown) + " = " + errorField + ";");
			WriteAsyncFrameReturnCompletion(writer, frame, indent + 1, resultExpression: null);
			WriteIndent(writer, indent);
			writer.WriteLine("}");
		}

		ArgumentExpression? FindCatchArgument(CallExpression call)
		{
			foreach (ArgumentExpression argument in call.Arguments)
				if (argument.Modifier == ArgumentModifier.Catch)
					return argument;
			return null;
		}

		bool TryFormatCatchAssignmentTarget(ArgumentExpression argument, out string? target)
		{
			target = null;
			if (argument.Target is not null)
			{
				if (argument.Target.Names.Count == 1 && argument.Target.Names[0] == "_")
					return false;
				target = FormatVariableReference(argument.Target);
				return true;
			}
			if (argument.Value is NamedExpression { Qualifiers.Count: 0, Name: "_" })
				return false;
			if (argument.Value is VariableReferenceExpression variable)
			{
				target = FormatExpression(variable);
				return true;
			}
			if (argument.Value is not null)
			{
				target = FormatExpression(argument.Value);
				return true;
			}
			return false;
		}

		void WriteAsyncFrameReturn(TextWriter writer, AsyncFrameInfo frame, ReturnStatement ret, int indent)
		{
			WriteAsyncFrameReturnCompletion(writer, frame, indent, ret.Expression is null ? null : FormatExpression(ret.Expression));
		}

		void WriteAsyncFrameReturnCompletion(TextWriter writer, AsyncFrameInfo frame, int indent, string? resultExpression)
		{
			List<string> arguments = ["frame->complete_context"];
			string resultType = frame.Function.ResolvedType ?? "void";
			if (resultType != "void")
				arguments.Add(resultExpression ?? FormatDefaultValueForResolvedType(resultType));
			if (frame.Function.Parameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Thrown) is ParameterDefinition thrown)
				arguments.Add(FormatVariableReference(thrown));
			WriteIndent(writer, indent);
			writer.WriteLine("frame->complete(" + string.Join(", ", arguments) + ");");
			WriteAsyncFrameFree(writer, frame, indent);
			WriteIndent(writer, indent);
			writer.WriteLine("return;");
		}

		void WriteAsyncFrameFree(TextWriter writer, AsyncFrameInfo frame, int indent)
		{
			ParameterDefinition? allocator = frame.Function.Parameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition);
			FunctionDefinition? allocatorFree = allocator is null ? null : FindMemberFunctionForType(allocator.ResolvedType, "free");
			if (allocator is not null && allocatorFree is not null)
			{
				WriteIndent(writer, indent);
				writer.WriteLine("{");
				WriteIndent(writer, indent + 1);
				writer.WriteLine("if (frame->" + CName(allocator) + " != NULL)");
				WriteIndent(writer, indent + 1);
				writer.WriteLine("{");
				WriteIndent(writer, indent + 2);
				writer.WriteLine(CName(allocatorFree) + "(frame->" + CName(allocator) + ", frame);");
				WriteIndent(writer, indent + 1);
				writer.WriteLine("}");
				WriteIndent(writer, indent + 1);
				writer.WriteLine("else");
				WriteIndent(writer, indent + 1);
				writer.WriteLine("{");
				WriteIndent(writer, indent + 2);
				writer.WriteLine("free(frame);");
				WriteIndent(writer, indent + 1);
				writer.WriteLine("}");
				WriteIndent(writer, indent);
				writer.WriteLine("}");
				return;
			}
			WriteIndent(writer, indent);
			writer.WriteLine("free(frame);");
		}

		void WriteAsyncFrameAllocation(TextWriter writer, AsyncFrameInfo frame, int indent)
		{
			ParameterDefinition? allocator = frame.Function.Parameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition);
			WriteIndent(writer, indent);
			writer.WriteLine(frame.FrameType + " *frame = NULL;");
			if (allocator is not null && FindMemberFunctionForType(allocator.ResolvedType, "alloc") is FunctionDefinition allocatorAlloc)
			{
				WriteIndent(writer, indent);
				writer.WriteLine("if (" + CName(allocator) + " != NULL)");
				WriteIndent(writer, indent);
				writer.WriteLine("{");
				WriteIndent(writer, indent + 1);
				writer.WriteLine("frame = (" + frame.FrameType + " *)" + CName(allocatorAlloc) + "(" + CName(allocator) + ", sizeof(" + frame.FrameType + "));");
				WriteIndent(writer, indent);
				writer.WriteLine("}");
			}
			WriteIndent(writer, indent);
			writer.WriteLine("if (frame == NULL)");
			WriteIndent(writer, indent);
			writer.WriteLine("{");
			WriteIndent(writer, indent + 1);
			writer.WriteLine("frame = (" + frame.FrameType + " *)malloc(sizeof(" + frame.FrameType + "));");
			WriteIndent(writer, indent);
			writer.WriteLine("}");
		}

		FunctionDefinition? FindMemberFunctionForType(string? receiverType, string name)
		{
			string normalizedReceiver = receiverType is null ? "" : StripTypeDecorators(receiverType);
			string typeName = TryGetPointerElementType(normalizedReceiver, out string pointerElement) ? pointerElement : normalizedReceiver;
			int genericStart = typeName.IndexOf('<', StringComparison.Ordinal);
			if (genericStart >= 0)
				typeName = typeName[..genericStart];
			typeName = typeName.Trim();
			if (string.IsNullOrWhiteSpace(typeName))
				return null;
			foreach (KeyValuePair<FunctionDefinition, TypeDefinition> entry in containingTypes)
			{
				if (entry.Value.Name == typeName && entry.Key.Name == name)
					return entry.Key;
			}
			return null;
		}

		static bool StatementContainsAwait(Statement? statement)
		{
			return statement switch
			{
				null => false,
				BlockStatement block => block.Statements.Any(StatementContainsAwait),
				ExpressionStatement expression => ExpressionContainsAwait(expression.Expression),
				DeclarationStatement declaration => ExpressionContainsAwait(declaration.InitialValue),
				ReturnStatement ret => ExpressionContainsAwait(ret.Expression),
				IfStatement ifStatement => ExpressionContainsAwait(ifStatement.Condition) || StatementContainsAwait(ifStatement.Body) || StatementContainsAwait(ifStatement.ElseBody),
				WhileStatement whileStatement => ExpressionContainsAwait(whileStatement.Condition) || StatementContainsAwait(whileStatement.Body),
				ForStatement forStatement => StatementContainsAwait(forStatement.Condition.Declaration) || forStatement.Condition.Clauses.Any(ExpressionContainsAwait) || StatementContainsAwait(forStatement.Body),
				TryStatement tryStatement => StatementContainsAwait(tryStatement.Body) || tryStatement.Catches.Any(StatementContainsAwait) || StatementContainsAwait(tryStatement.Finally),
				CatchStatement catchStatement => StatementContainsAwait(catchStatement.Body),
				FinallyStatement finallyStatement => StatementContainsAwait(finallyStatement.Body),
				WithinStatement withinStatement => ExpressionContainsAwait(withinStatement.Allocator) || StatementContainsAwait(withinStatement.Body),
				_ => false
			};
		}

		static bool ExpressionContainsAwait(Expression? expression)
		{
			return expression switch
			{
				null => false,
				UnaryExpression { Operator: UnaryOperator.Await } => true,
				UnaryExpression unary => ExpressionContainsAwait(unary.Operand) || ExpressionContainsAwait(unary.Context),
				BinaryExpression binary => ExpressionContainsAwait(binary.Left) || ExpressionContainsAwait(binary.Right),
				AssignmentExpression assignment => ExpressionContainsAwait(assignment.Target) || ExpressionContainsAwait(assignment.Value),
				CallExpression call => ExpressionContainsAwait(call.Target) || call.Arguments.Any(argument => ExpressionContainsAwait(argument.Value)),
				MemberExpression member => ExpressionContainsAwait(member.Target),
				IndexExpression index => ExpressionContainsAwait(index.Target) || index.Arguments.Any(argument => ExpressionContainsAwait(argument.Value)),
				ParenthesizedExpression parenthesized => ExpressionContainsAwait(parenthesized.Expression),
				CastExpression cast => ExpressionContainsAwait(cast.Expression),
				ConditionalExpression conditional => ExpressionContainsAwait(conditional.Condition) || ExpressionContainsAwait(conditional.WhenTrue) || ExpressionContainsAwait(conditional.WhenFalse),
				GroupedExpression grouped => grouped.Items.Any(item => ExpressionContainsAwait(item.Expression)),
				ArrayExpression array => array.Elements.Any(ExpressionContainsAwait),
				InitializerExpression initializer => initializer.Items.Any(item => ExpressionContainsAwait(item.Expression)),
				FinallyDeleteExpression finallyDelete => ExpressionContainsAwait(finallyDelete.Expression),
				WithinExpression within => ExpressionContainsAwait(within.Context) || ExpressionContainsAwait(within.Expression),
				ArgumentExpression argument => ExpressionContainsAwait(argument.Value),
				_ => false
			};
		}

		string FormatFunctionSignature(FunctionDefinition function, string name)
		{
			if (function.IsAsync)
				return FormatAsyncFunctionSignature(function, name);

			if (!TryGetFunctionReturnStorageComponentsForC(function, out List<(string Name, string Type)> components) || components.Count <= 1)
			{
				if (function.ReturnType is RawFunctionPointerTypeReference || IsRawFunctionPointerResolvedType(function.ResolvedType))
					return FormatInlineResolvedFunctionPointer("void", new List<string>(), name + FormatParameters(function), GetRawFunctionPointerTargetSpec(function), null);
				return FormatTypeOrResolved(function.ReturnType, function.ResolvedType, name).Declaration + FormatParameters(function);
			}

			List<string> parameters = FormatFunctionParameterParts(function);
			for (int i = 1; i < components.Count; i++)
			{
				string resultName = "result_" + components[i].Name;
				if (function.Parameters.Any(parameter => CName(parameter) == resultName))
					continue;
				parameters.Add(FormatResolvedType(components[i].Type + "*", resultName).Declaration);
			}
			string parameterList = parameters.Count == 0 ? "void" : string.Join(", ", parameters);
			string returnType = FormatFunctionPrimaryReturnComponentType(function, components[0]);
			return FormatResolvedType(returnType, name + "(" + parameterList + ")").Declaration;
		}

		string FormatAsyncFunctionSignature(FunctionDefinition function, string name)
		{
			List<string> parameters = FormatFunctionParameterParts(function, skipThrown: true);
			parameters.AddRange(FormatAsyncCompletionParameterParts(function));
			string parameterList = parameters.Count == 0 ? "void" : string.Join(", ", parameters);
			return FormatResolvedType("void", name + "(" + parameterList + ")").Declaration;
		}

		List<string> FormatAsyncCompletionParameterParts(FunctionDefinition function)
		{
			return FormatAsyncCompletionParameterDeclarations(function.ReturnType, function.ResolvedType, function.Parameters);
		}

		List<string> FormatAsyncCompletionParameterTypes(TypeReference? returnType, string? resolvedReturnType, List<ParameterDefinition> parameters)
		{
			return [BuildAsyncCompletionCallableType(returnType, resolvedReturnType, parameters), "void*"];
		}

		List<string> FormatAsyncCompletionParameterDeclarations(TypeReference? returnType, string? resolvedReturnType, List<ParameterDefinition> parameters)
		{
			List<(string Type, string Name)> completionParameters = GetAsyncCompletionSlots(returnType, resolvedReturnType, parameters);
			return
			[
				FormatInlineResolvedFunctionPointer("void", completionParameters, "complete"),
				FormatResolvedType("void*", "complete_context").Declaration
			];
		}

		string BuildAsyncCompletionCallableType(TypeReference? returnType, string? resolvedReturnType, List<ParameterDefinition> parameters)
		{
			return "fn void(" + string.Join(", ", GetAsyncCompletionSlots(returnType, resolvedReturnType, parameters).Select(static slot => slot.Type)) + ")";
		}

		List<(string Type, string Name)> GetAsyncCompletionSlots(TypeReference? returnType, string? resolvedReturnType, List<ParameterDefinition> parameters)
		{
			List<(string Type, string Name)> completionParameters = [("void*", "context")];
			string resultType = resolvedReturnType ?? ResolvedTypeForC(returnType, returnType?.ResolvedType);
			if (resultType != "void")
			{
				if (TryGetTypeReferenceStorageComponentsForC(returnType, out List<(string Name, string Type)> components) && components.Count > 0)
				{
					for (int i = 0; i < components.Count; i++)
						completionParameters.Add((components[i].Type, i == 0 ? "result" : "result_" + components[i].Name));
				}
				else
				{
					completionParameters.Add((resultType, "result"));
				}
			}
			if (parameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Thrown) is ParameterDefinition thrown)
				completionParameters.Add((thrown.ResolvedType ?? thrown.Type?.ResolvedType ?? "#ERROR", CName(thrown)));

			return completionParameters;
		}

		string? GetRawFunctionPointerTargetSpec(FunctionDefinition function)
		{
			string? resolvedType = function.ResolvedType?.Trim();
			if (resolvedType?.StartsWith("fn* ", StringComparison.Ordinal) == true)
				return resolvedType["fn* ".Length..].Trim();
			return GetDefaultTargetTypeSpec(functionPointer: true);
		}

		static bool IsRawFunctionPointerResolvedType(string? type)
		{
			type = type?.Trim();
			return type == "fn*" || type?.StartsWith("fn* ", StringComparison.Ordinal) == true;
		}

		string FormatFunctionPrimaryReturnComponentType(FunctionDefinition function, (string Name, string Type) component)
		{
			if (component.Name == "call"
				&& TypeReferenceName(function.ReturnType) is string nominalTypeName
				&& TryGetCallableNewtypeStorageComponentsForC(nominalTypeName, out List<(string Name, string Type)> nominalComponents)
				&& nominalComponents.Count > 0
				&& nominalComponents[0].Name == "call")
				return CNameFromTypeName(nominalTypeName);
			return component.Type;
		}

		bool TryGetFunctionReturnStorageComponentsForC(FunctionDefinition function, out List<(string Name, string Type)> components)
		{
			if (TryGetExpandedStorageComponentsForC(function.ResolvedType, out components))
				return true;
			if (TryGetExpandedStorageComponentsForC(function.ReturnType?.ResolvedType, out components))
				return true;
			if (TryGetExpandedStorageComponentsForC(TypeReferenceName(function.ReturnType), out components))
				return true;
			if (TryGetTypeReferenceStorageComponentsForC(function.ReturnType, out components))
				return true;
			return false;
		}

		bool TryGetTypeReferenceStorageComponentsForC(TypeReference? type, out List<(string Name, string Type)> components)
		{
			components = [];
			switch (type)
			{
				case IterTypeReference iter:
				{
					List<string> parameterTypes = ["void*"];
					foreach (string currentType in GetIteratorCurrentTypesForC(iter))
						parameterTypes.Add("out " + currentType);
					if (GetIteratorThrownTypeForC(iter) is string thrownType)
						parameterTypes.Add("thrown " + thrownType);
					parameterTypes.AddRange(GetExpandedCallableParameterTypesForC(iter.Parameters));
					components.Add(("call", "fn bool(" + string.Join(", ", parameterTypes) + ")"));
					components.Add(("context", "void*"));
					return true;
				}
				case CallableTypeReference callable when callable.Kind is CallableKind.Delegate or CallableKind.Once or CallableKind.Async:
				{
					string returnType = callable.ReturnType?.ResolvedType ?? ResolvedTypeForC(callable.ReturnType, callable.ReturnType?.ResolvedType);
					string contextType = GetCallableContextType(callable.Parameters);
					List<string> parameterTypes = [contextType, .. GetExpandedCallableParameterTypesForC(callable.Parameters.Where(static parameter => parameter.Modifier != ParameterModifier.Thrown).ToList())];
					if (callable.Kind == CallableKind.Async)
						parameterTypes.AddRange(FormatAsyncCompletionParameterTypes(callable.ReturnType, returnType, callable.Parameters));
					else
						ExpandResolvedCallableReturnForC(ref returnType, parameterTypes);
					string specs = "";
					if (!string.IsNullOrWhiteSpace(callable.TargetSpec))
						specs += " " + callable.TargetSpec;
					if (!string.IsNullOrWhiteSpace(callable.CallSpec))
						specs += " " + callable.CallSpec;
					components.Add(("call", "fn" + specs + " " + returnType + "(" + string.Join(", ", parameterTypes) + ")"));
					components.Add(("context", contextType));
					return true;
				}
				case ConstTypeReference constant:
					return TryGetTypeReferenceStorageComponentsForC(constant.Type, out components);
				case ConstOfTypeReference constOf:
					return TryGetTypeReferenceStorageComponentsForC(constOf.Type, out components);
				case VolatileTypeReference vol:
					return TryGetTypeReferenceStorageComponentsForC(vol.Type, out components);
				case EscapedTypeReference escaped:
					return TryGetTypeReferenceStorageComponentsForC(escaped.Type, out components);
				case ScopedTypeReference scoped:
					return TryGetTypeReferenceStorageComponentsForC(scoped.Type, out components);
				case UnscopedTypeReference unscoped:
					return TryGetTypeReferenceStorageComponentsForC(unscoped.Type, out components);
				default:
					return false;
			}
		}

		static string? TypeReferenceName(TypeReference? type)
		{
			return type switch
			{
				NamedTypeReference named => named.SourceSyntax is QualifiedNameTypeSyntax { Identifier: not null } syntax
					? syntax.Identifier.Value.Value
					: named.Name,
				TypeDefinitionReference { Definition: not null } reference => reference.Definition.Name,
				TypeDefinitionReference reference => reference.Name,
				GenericTypeReference generic => TypeReferenceName(generic.Type),
				ConstTypeReference constant => TypeReferenceName(constant.Type),
				ConstOfTypeReference constOf => TypeReferenceName(constOf.Type),
				VolatileTypeReference vol => TypeReferenceName(vol.Type),
				EscapedTypeReference escaped => TypeReferenceName(escaped.Type),
				ScopedTypeReference scoped => TypeReferenceName(scoped.Type),
				UnscopedTypeReference unscoped => TypeReferenceName(unscoped.Type),
				_ => null
			};
		}

		List<string> FormatFunctionParameterParts(FunctionDefinition function, bool skipThrown = false)
		{
			List<string> parts = [];
			WithArrayElementComponentContext(function.Parameters, () =>
			{
				if (RequiresImplicitThisParameter(function) && containingTypes.TryGetValue(function, out TypeDefinition? type))
				{
					string resolvedThisType = function.AbiThisType?.ResolvedType ?? function.EffectiveThisParameter?.ResolvedType ?? GetImplicitThisResolvedType(type);
					AddImplicitThisParameterParts(parts, function, resolvedThisType, NeedsAbiThisFixup(function) ? "ctx" : "this");
				}
				foreach (ParameterDefinition parameter in GetAbiOrderedParameters(function.Parameters))
				{
					if (skipThrown && parameter.Modifier == ParameterModifier.Thrown)
						continue;
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
						parts.Add(FormatOrdinaryParameterType(parameter, name).Declaration);
					}
				}
			});
			return parts;
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
				if (parameter.Constraint is AnyTypeReference or CopyableTypeReference)
					currentAnyGenericTypeNames.Add(parameter.Name);
			}
			if (containingTypes.TryGetValue(function, out TypeDefinition? containingType))
				foreach (GenericParameter parameter in containingType.GenericParameters)
				{
					currentGenericTypeNames.Add(parameter.Name);
					if (parameter.Constraint is AnyTypeReference or CopyableTypeReference)
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
				if (parameter.Constraint is AnyTypeReference or CopyableTypeReference)
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
			if (function.IsAsync && function.Parameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Thrown) is ParameterDefinition asyncThrown)
			{
				WriteIndent(writer, 1);
				writer.WriteLine(FormatTypeOrResolved(asyncThrown.Type, asyncThrown.ResolvedType, CName(asyncThrown)).Declaration + " = 0;");
			}
			foreach (Statement statement in function.Body!.Statements)
				WriteStatement(writer, statement, 1);
			if (function.IsAsync && (function.ResolvedType ?? "void") == "void" && !EndsWithExplicitReturn(function.Body))
				WriteAsyncReturnStatement(writer, new ReturnStatement { ResolvedType = "void" }, 1);
			writer.WriteLine("}");
			currentFunction = previousFunction;
			currentFunctionHasLabels = previousFunctionHasLabels;
		}

		static bool EndsWithExplicitReturn(BlockStatement? body)
		{
			return body?.Statements.LastOrDefault() is ReturnStatement;
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
					if (currentFunction?.IsAsync == true)
					{
						WriteAsyncReturnStatement(writer, ret, indent);
						break;
					}
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

		void WriteAsyncReturnStatement(TextWriter writer, ReturnStatement ret, int indent)
		{
			if (ret.Expression is UnaryExpression { Operator: UnaryOperator.Await, Operand: CallExpression awaitCall })
			{
				WriteIndent(writer, indent);
				writer.WriteLine(FormatTailAwaitCallExpression(awaitCall) + ";");
				WriteIndent(writer, indent);
				writer.WriteLine("return;");
				return;
			}

			FunctionDefinition function = currentFunction!;
			List<string> arguments = ["complete_context"];
			string resultType = function.ResolvedType ?? "void";
			if (resultType != "void")
				arguments.Add(ret.Expression is null ? FormatDefaultValueForResolvedType(resultType) : FormatExpression(ret.Expression));
			if (function.Parameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Thrown) is ParameterDefinition thrown)
				arguments.Add(CName(thrown));
			WriteIndent(writer, indent);
			writer.WriteLine("complete(" + string.Join(", ", arguments) + ");");
			WriteIndent(writer, indent);
			writer.WriteLine("return;");
		}

		string FormatTailAwaitCallExpression(CallExpression call)
		{
			return FormatAwaitCallWithCompletion(call, "complete", "complete_context");
		}

		string FormatAwaitCallWithCompletion(CallExpression call, string completion, string completionContext)
		{
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
				if (function?.IsAsync == true && call.Arguments[i].Modifier == ArgumentModifier.Catch)
					continue;
				ParameterDefinition? parameter = i < parameters.Count ? parameters[i] : null;
				arguments.Add(FormatArgumentValue(call.Arguments[i], parameter, genericSubstitutions));
			}
			arguments.Add(completion);
			arguments.Add(completionContext);
			if (function?.IsAsync == true)
				RepairAsyncCallArgumentSlots(function, arguments);
			return target + "(" + string.Join(", ", arguments) + ")";
		}

		string FormatDefaultValueForResolvedType(string resolvedType)
		{
			if (!IsAggregateValueType(resolvedType))
				return "0";
			return "(" + FormatResolvedType(resolvedType, "").Declaration.Trim() + "){0}";
		}

		void WriteDeclarationStatement(TextWriter writer, DeclarationStatement declaration, int indent)
		{
			string name = declaration.Target.Names.Count == 0 ? "__unnamed" : SanitizeIdentifier(declaration.Target.Names[0]);
			if (IsAnyGenericParameterType(declaration.Target.ResolvedType))
			{
				string size = FormatGenericSizeExpression(declaration.Target.ResolvedType);
				WriteIndent(writer, indent);
				writer.WriteLine("uint8_t *" + name + " = " + FormatAlloca(size) + ";");
				if (declaration.InitialValue is DefaultExpression)
				{
					WriteIndent(writer, indent);
					writer.WriteLine(FormatMemoryCall("memset", name, "0", size) + ";");
				}
				else if (declaration.InitialValue is not null)
				{
					WriteIndent(writer, indent);
					writer.WriteLine(FormatMemoryCall("memcpy", name, FormatGenericStorageSource(declaration.InitialValue), size) + ";");
				}
				return;
			}
			string type = FormatTypeOrResolved(declaration.Target.Type, declaration.Target.ResolvedType, name).Declaration;
			WriteIndent(writer, indent);
			writer.Write(type);
			if (declaration.InitialValue is not null)
				writer.Write(" = " + FormatDeclarationInitializer(declaration.Target.ResolvedType, declaration.InitialValue));
			writer.WriteLine(";");
		}

		string FormatDeclarationInitializer(string? targetType, Expression value)
		{
			if (TryGetFixedArrayElementType(targetType ?? "", out _))
				return FormatFixedArrayInitializer(value);
			return FormatAssignmentValueForTarget(targetType, value);
		}

		string FormatFixedArrayInitializer(Expression value)
		{
			return value switch
			{
				ArrayExpression array => "{" + string.Join(", ", array.Elements.Select(FormatFixedArrayInitializerElement)) + "}",
				DefaultExpression => "{0}",
				_ => FormatExpression(value)
			};
		}

		string FormatFixedArrayInitializerElement(Expression value)
		{
			return value is ArrayExpression ? FormatFixedArrayInitializer(value) : FormatExpression(value);
		}

		string FormatAlloca(string size)
		{
			string alloca = compilation.Target?.Capabilities.GetCEmitterValue("alloca", "__builtin_alloca") ?? "__builtin_alloca";
			return alloca + "(" + size + ")";
		}

		string FormatMemoryCall(string operation, params string[] arguments)
		{
			string functionName = compilation.Target?.Capabilities.GetCEmitterValue(operation, "__builtin_" + operation) ?? "__builtin_" + operation;
			return functionName + "(" + string.Join(", ", arguments) + ")";
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
				NamedExpression named => FormatNamedExpression(named),
				MethodReferenceExpression method => method.Candidates.Count == 1 ? CName(method.Candidates[0]) : UnsupportedExpression(expression),
				TypeReferenceExpression type => FormatTypeReferenceExpression(type),
				ThisExpression => FormatThisExpression(),
				DefaultExpression defaultExpression => FormatDefaultExpression(defaultExpression),
				ParenthesizedExpression parenthesized => "(" + FormatExpression(parenthesized.Expression) + ")",
				CastExpression { LifetimeCastKind: not null } cast => FormatExpression(cast.Expression),
				CastExpression cast => FormatCastExpression(cast),
				SizeOfExpression sizeOf => FormatSizeOfExpression(sizeOf),
				NameOfExpression nameOf => FormatNameOfExpression(nameOf),
				StackAllocExpression stackAlloc => FormatAlloca(FormatExpression(stackAlloc.Size)),
				CallExpression call => FormatCallExpression(call),
				IndexExpression index => FormatIndexExpression(index),
				MemberExpression member => FormatExpandedThisComponent(member) ?? FormatCallableCallComponent(member.Target, member.Name) ?? FormatInterfaceSlotMember(member.Target, member.Name) ?? FormatMemberTarget(member.Target) + (IsPointerMemberTarget(member.Target) ? "->" : ".") + SanitizeIdentifier(member.Name),
				MemberReferenceExpression member => FormatMemberReference(member),
				UnaryExpression unary => FormatUnaryExpression(unary),
				PostfixUpdateExpression postfix => FormatExpression(postfix.Expression) + FormatUpdateOperator(postfix.Operator),
				BinaryExpression binary => FormatBinaryExpression(binary),
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

		string FormatNamedExpression(NamedExpression named)
		{
			if (named.Qualifiers.Count == 0
				&& named.Name is string name
				&& currentAsyncFrameNameReplacements.TryGetValue(name, out string? replacement))
				return replacement;
			return SanitizeIdentifier(named.Name);
		}

		string FormatCastExpression(CastExpression cast)
		{
			string type = TryGetErasedGenericStoragePointerCastType(cast.Type, out string erasedType)
				? erasedType
				: TryGetInterfacePointerCastType(cast.Type, out string interfaceName)
				? CTypeName(interfaceName) + " **"
				: FormatType(cast.Type, "").Declaration.Trim();
			return "(" + type + ")(" + FormatExpression(cast.Expression) + ")";
		}

		bool TryGetErasedGenericStoragePointerCastType(TypeReference? type, out string castType)
		{
			castType = "";
			if (StripTypeDecorators(type) is not PointerTypeReference pointer)
				return false;

			TypeReference? element = StripTypeDecorators(pointer.ElementType);
			string? elementType = element?.ResolvedType;
			if (elementType is null && element is NamedTypeReference named)
				elementType = named.Name;
			if (!IsAnyGenericParameterType(elementType))
				return false;

			castType = "void*";
			return true;
		}

		bool TryGetInterfacePointerCastType(TypeReference? type, out string interfaceName)
		{
			interfaceName = "";
			type = StripTypeDecorators(type);
			if (type is TypeDefinitionReference { Definition: InterfaceDefinition directDefinition })
			{
				interfaceName = directDefinition.Name;
				return true;
			}
			if (type is NamedTypeReference directNamed && IsInterfaceResolvedName(directNamed.ResolvedType ?? directNamed.Name))
			{
				interfaceName = directNamed.ResolvedType ?? directNamed.Name;
				return true;
			}
			if (type is not PointerTypeReference pointer)
				return false;

			TypeReference? element = StripTypeDecorators(pointer.ElementType);
			if (element is TypeDefinitionReference { Definition: InterfaceDefinition definition })
			{
				interfaceName = definition.Name;
				return true;
			}
			if (element is NamedTypeReference named && IsInterfaceResolvedName(named.ResolvedType ?? named.Name))
			{
				interfaceName = named.ResolvedType ?? named.Name;
				return true;
			}
			return false;
		}

		static TypeReference? StripTypeDecorators(TypeReference? type)
		{
			while (true)
			{
				type = type switch
				{
					AttributedTypeReference attributed => attributed.Type,
					ConstTypeReference constant => constant.Type,
					ConstOfTypeReference constOf => constOf.Type,
					VolatileTypeReference vol => vol.Type,
					EscapedTypeReference escaped => escaped.Type,
					ScopedTypeReference scoped => scoped.Type,
					UnscopedTypeReference unscoped => unscoped.Type,
					TargetTypeSpecTypeReference targetSpec => targetSpec.Type,
					_ => type
				};
				if (type is not (AttributedTypeReference or ConstTypeReference or ConstOfTypeReference or VolatileTypeReference or EscapedTypeReference or ScopedTypeReference or UnscopedTypeReference or TargetTypeSpecTypeReference))
					return type;
			}
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

		bool IsExpandedStorageResolvedType(string? resolvedType)
		{
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;
			return TryGetExpandedStorageComponentsForC(resolvedType, out List<(string Name, string Type)> components)
				&& components.Count > 1;
		}

		bool TryGetExpandedStorageComponentsForC(string? resolvedType, out List<(string Name, string Type)> components)
		{
			components = [];
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;

			string type = StripLifetimeOnly(resolvedType.Trim());
			if (TryGetArrayElementOnly(type, out string arrayElementType))
			{
				components.Add(("elements", arrayElementType + "*"));
				components.Add(("length", "nuint"));
				return true;
			}
			if (TryGetOptionalElementOnly(type, out string optionalElementType))
			{
				components.Add(("value", optionalElementType));
				components.Add(("specified", "bool"));
				return true;
			}
			if (TryParseExpandedCallableStorageType(type, out string callableReturnType, out List<string> callableParameterTypes, out string? callableTargetSpec, out string? callableCallSpec))
			{
				string specs = "";
				if (!string.IsNullOrWhiteSpace(callableTargetSpec))
					specs += " " + callableTargetSpec;
				if (!string.IsNullOrWhiteSpace(callableCallSpec))
					specs += " " + callableCallSpec;
				components.Add(("call", "fn" + specs + " " + callableReturnType + "(" + string.Join(", ", ["void*", .. callableParameterTypes]) + ")"));
				components.Add(("context", "void*"));
				return true;
			}
			if (TryGetCallableNewtypeStorageComponentsForC(type, out components))
				return true;
			if (TryGetCallableNewtypeStorageComponentsByPrimaryTypeForC(type, out components))
				return true;

			return false;
		}

		bool TryGetCallableNewtypeStorageComponentsByPrimaryTypeForC(string resolvedType, out List<(string Name, string Type)> components)
		{
			components = [];
			if (!TryGetCallableNewtypeByPrimaryTypeForC(resolvedType, out NewtypeDefinition? newtypeDefinition))
				return false;
			if (newtypeDefinition is null)
				return false;
			return TryGetCallableNewtypeStorageComponentsForC(newtypeDefinition.Name, out components) && components.Count > 1;
		}

		bool TryGetCallableNewtypeByPrimaryTypeForC(string resolvedType, out NewtypeDefinition? newtypeDefinition)
		{
			newtypeDefinition = null;
			if (!TryParseResolvedCallableType(resolvedType, out _, out _))
				return false;
			NewtypeDefinition? match = null;
			foreach (Definition definition in GetDefinitions())
			{
				if (definition is not NewtypeDefinition candidate)
					continue;
				if (TryGetCallableNewtypePrimaryFunctionTypeForC(candidate, out string primaryFunctionType)
					&& SameResolvedCallableSignature(resolvedType, primaryFunctionType))
				{
					if (match is not null)
						return false;
					match = candidate;
				}
			}
			newtypeDefinition = match;
			return match is not null;
		}

		bool SameResolvedCallableSignature(string left, string right)
		{
			if (!TryParseResolvedCallableType(left, out string leftReturn, out List<string> leftParameters, out string? leftTargetSpec, out string? leftCallSpec))
				return false;
			if (!TryParseResolvedCallableType(right, out string rightReturn, out List<string> rightParameters, out string? rightTargetSpec, out string? rightCallSpec))
				return false;
			if (leftReturn != rightReturn || leftTargetSpec != rightTargetSpec || leftCallSpec != rightCallSpec || leftParameters.Count != rightParameters.Count)
				return false;
			for (int i = 0; i < leftParameters.Count; i++)
				if (NormalizeResolvedCallableParameterType(leftParameters[i]) != NormalizeResolvedCallableParameterType(rightParameters[i]))
					return false;
			return true;
		}

		static string NormalizeResolvedCallableParameterType(string type)
		{
			type = type.Trim();
			if (type.StartsWith("in ", StringComparison.Ordinal))
				return type[3..].TrimStart() + "*";
			if (type.StartsWith("out ", StringComparison.Ordinal))
				return type[4..].TrimStart() + "*";
			if (type.StartsWith("thrown ", StringComparison.Ordinal))
				return type[7..].TrimStart() + "*";
			if (type.StartsWith("within ", StringComparison.Ordinal))
				return type[7..].TrimStart();
			return type;
		}

		bool TryGetCallableNewtypeStorageComponentsForC(string resolvedType, out List<(string Name, string Type)> components)
		{
			components = [];
			string baseName = BaseResolvedTypeName(resolvedType);
			foreach (Definition definition in GetDefinitions())
			{
				if (definition is not NewtypeDefinition newtypeDefinition
					|| newtypeDefinition.Name != baseName && CName(newtypeDefinition) != baseName)
					continue;
				if (newtypeDefinition.UnderlyingType is CallableTypeReference callable
					&& callable.Kind is CallableKind.Delegate or CallableKind.Once or CallableKind.Async)
				{
					string contextType = GetCallableContextType(newtypeDefinition.Parameters);
					components.Add(("call", CName(newtypeDefinition)));
					components.Add(("context", contextType));
					return true;
				}
				if (newtypeDefinition.UnderlyingType is IterTypeReference iter)
				{
					components.Add(("call", CName(newtypeDefinition)));
					components.Add(("context", "void*"));
					return true;
				}
				string? storageType = newtypeDefinition.UnderlyingType?.ResolvedType;
				if (string.IsNullOrWhiteSpace(storageType))
					return false;
				return TryGetExpandedStorageComponentsForC(storageType, out components);
			}
			return false;
		}

		bool TryGetCallableNewtypePrimaryFunctionTypeForC(NewtypeDefinition newtypeDefinition, out string primaryFunctionType)
		{
			primaryFunctionType = "";
			if (newtypeDefinition.UnderlyingType is CallableTypeReference callable
				&& callable.Kind is CallableKind.Delegate or CallableKind.Once or CallableKind.Async)
			{
				string returnType = callable.Kind == CallableKind.Async ? "void" : callable.ReturnType?.ResolvedType ?? ResolvedTypeForC(callable.ReturnType, callable.ReturnType?.ResolvedType);
				string contextType = GetCallableContextType(newtypeDefinition.Parameters);
				List<string> parameterTypes = [contextType, .. GetExpandedCallableParameterTypesForC(newtypeDefinition.Parameters.Where(static parameter => parameter.Modifier != ParameterModifier.Thrown).ToList())];
				if (callable.Kind == CallableKind.Async)
					parameterTypes.AddRange(FormatAsyncCompletionParameterTypes(callable.ReturnType, callable.ReturnType?.ResolvedType, newtypeDefinition.Parameters));
				else
					ExpandResolvedCallableReturnForC(ref returnType, parameterTypes);
				string specs = "";
				if (!string.IsNullOrWhiteSpace(callable.TargetSpec))
					specs += " " + callable.TargetSpec;
				if (!string.IsNullOrWhiteSpace(callable.CallSpec))
					specs += " " + callable.CallSpec;
				primaryFunctionType = "fn" + specs + " " + returnType + "(" + string.Join(", ", parameterTypes) + ")";
				return true;
			}
			if (newtypeDefinition.UnderlyingType is IterTypeReference iter)
			{
				List<string> parameterTypes = ["void*"];
				foreach (string currentType in GetIteratorCurrentTypesForC(iter))
					parameterTypes.Add("out " + currentType);
				if (GetIteratorThrownTypeForC(iter) is string thrownType)
					parameterTypes.Add("thrown " + thrownType);
				parameterTypes.AddRange(GetExpandedCallableParameterTypesForC(newtypeDefinition.Parameters));
				primaryFunctionType = "fn bool(" + string.Join(", ", parameterTypes) + ")";
				return true;
			}
			return false;
		}

		static string BaseResolvedTypeName(string type)
		{
			type = type.Trim();
			while (type.EndsWith("*", StringComparison.Ordinal) || type.EndsWith("?", StringComparison.Ordinal))
				type = type[..^1].TrimEnd();
			if (type.EndsWith("[]", StringComparison.Ordinal))
				type = type[..^2].TrimEnd();
			int generic = type.IndexOf('<', StringComparison.Ordinal);
			if (generic >= 0)
				type = type[..generic].TrimEnd();
			int namespaceSeparator = type.LastIndexOf("::", StringComparison.Ordinal);
			if (namespaceSeparator >= 0)
				type = type[(namespaceSeparator + 2)..].TrimStart();
			int space = type.LastIndexOf(' ');
			if (space >= 0)
				type = type[(space + 1)..].TrimStart();
			return type;
		}

		string CNameFromTypeName(string typeName)
		{
			string baseName = BaseResolvedTypeName(typeName);
			foreach (Definition definition in GetDefinitions())
				if (definition is TypeDefinition typeDefinition
					&& (typeDefinition.Name == baseName || CName(typeDefinition) == baseName))
					return CName(typeDefinition);
			return CTypeName(typeName);
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
			string type = StripLifetimeOnly(resolvedType.Trim());
			if (TryGetArrayElementOnly(type, out string elementType))
				return new CType(FormatMaterializedArrayStructType(elementType) + " " + declarator);
			if (TryGetOptionalElementOnly(type, out string optionalElementType))
				return new CType("struct { " + FormatResolvedType(optionalElementType, "value").Declaration + "; bool specified; } " + declarator);
			if (TryParseExpandedCallableStorageType(type, out string returnType, out List<string> parameterTypes, out string? targetSpec, out string? callSpec))
				return new CType("struct { " + FormatInlineResolvedFunctionPointer(returnType, [ "void*", .. parameterTypes ], "call", targetSpec, callSpec) + "; void* context; } " + declarator);
			return FormatResolvedType(resolvedType, declarator);
		}

		string FormatInlineResolvedFunctionPointer(string returnType, List<string> parameterTypes, string declarator, string? targetSpec = null, string? callSpec = null)
		{
			ExpandResolvedCallableReturnForC(ref returnType, parameterTypes);
			return FormatResolvedType(returnType, FormatFunctionPointerDeclarator(declarator, targetSpec, callSpec)).Declaration + "(" + FormatResolvedParameterList(parameterTypes) + ")";
		}

		string FormatInlineResolvedFunctionPointer(string returnType, List<(string Type, string Name)> parameters, string declarator, string? targetSpec = null, string? callSpec = null)
		{
			ExpandResolvedCallableReturnForC(ref returnType, parameters);
			return FormatResolvedType(returnType, FormatFunctionPointerDeclarator(declarator, targetSpec, callSpec)).Declaration + "(" + FormatResolvedNamedParameterList(parameters) + ")";
		}

		string FormatInlineResolvedFunctionPointerFromDeclarations(string returnType, List<string> parameterDeclarations, string declarator, string? targetSpec = null, string? callSpec = null)
		{
			string parameterList = parameterDeclarations.Count == 0 ? "void" : string.Join(", ", parameterDeclarations);
			return FormatResolvedType(returnType, FormatFunctionPointerDeclarator(declarator, targetSpec, callSpec)).Declaration + "(" + parameterList + ")";
		}

		bool ExpandResolvedCallableReturnForC(ref string returnType, List<string> parameterTypes)
		{
			if (!TryGetExpandedStorageComponentsForC(returnType, out List<(string Name, string Type)> components) || components.Count <= 1)
				return false;

			returnType = components[0].Type;
			for (int i = 1; i < components.Count; i++)
				parameterTypes.Add("out " + components[i].Type);
			return true;
		}

		bool ExpandResolvedCallableReturnForC(ref string returnType, List<(string Type, string Name)> parameters)
		{
			if (!TryGetExpandedStorageComponentsForC(returnType, out List<(string Name, string Type)> components) || components.Count <= 1)
				return false;

			returnType = components[0].Type;
			for (int i = 1; i < components.Count; i++)
				parameters.Add(("out " + components[i].Type, components[i].Name));
			return true;
		}

		string FormatFunctionPointerDeclarator(string name, string? targetSpec = null, string? callSpec = null)
		{
			string pointer = "*";
			string targetSpecSpelling = FormatTypeSpec(targetSpec);
			string callSpecSpelling = FormatCallSpec(callSpec).Trim();
			if (targetSpecSpelling.Length > 0)
				pointer += " " + targetSpecSpelling;
			if (!string.IsNullOrWhiteSpace(name))
				pointer += " " + name;
			return "(" + (callSpecSpelling.Length > 0 ? callSpecSpelling + " " : "") + pointer + ")";
		}

		bool TryFormatResolvedCallableCast(string resolvedType, out string cast)
		{
			cast = "";
			if (!TryParseResolvedCallableType(resolvedType, out string returnType, out List<string> parameterTypes, out string? targetSpec, out string? callSpec))
				return false;
			cast = FormatInlineResolvedFunctionPointer(returnType, parameterTypes, "", targetSpec, callSpec);
			return true;
		}

		bool TryFormatCallableAssignmentCast(string targetType, Expression value, IReadOnlyCollection<string>? erasedGenericNames, out string cast)
		{
			cast = "";
			if (!IsCallableSymbolExpression(value))
				return false;
			if (TryFormatCallableNewtypeCast(targetType, requireGenericErasure: erasedGenericNames is null || erasedGenericNames.Count == 0, out cast))
				return true;
			(string Cast, bool Success) result = WithErasedGenericNames(erasedGenericNames, () =>
			{
				bool success = TryFormatResolvedCallableCast(targetType, out string innerCast);
				return (innerCast, success);
			});
			cast = result.Cast;
			return result.Success;
		}

		bool TryFormatCallableNewtypeCast(string targetType, bool requireGenericErasure, out string cast)
		{
			cast = "";
			string baseName = BaseResolvedTypeName(targetType);
			foreach (Definition definition in GetDefinitions())
			{
				if (definition is not NewtypeDefinition newtypeDefinition)
					continue;
				if (newtypeDefinition.Name != baseName && CName(newtypeDefinition) != baseName)
					continue;
				if (newtypeDefinition.UnderlyingType is not CallableTypeReference and not IterTypeReference)
					return false;
				if (requireGenericErasure && newtypeDefinition.GenericParameters.Count == 0 && !targetType.Contains('<', StringComparison.Ordinal))
					return false;
				cast = FormatResolvedType(targetType, "").Declaration.Trim();
				return true;
			}
			return false;
		}

		T WithErasedGenericNames<T>(IReadOnlyCollection<string>? names, Func<T> action)
		{
			if (names is null || names.Count == 0)
				return action();
			HashSet<string> previous = new(currentGenericTypeNames, StringComparer.Ordinal);
			HashSet<string> previousAny = new(currentAnyGenericTypeNames, StringComparer.Ordinal);
			foreach (string name in names)
			{
				currentGenericTypeNames.Add(name);
				currentAnyGenericTypeNames.Add(name);
			}
			try
			{
				return action();
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

		bool ShouldCastCallableAssignment(Expression value, string targetType)
		{
			if (TryFormatCallableNewtypeCast(targetType, requireGenericErasure: true, out _))
				return TryGetCallableShapeForEmitter(value.ResolvedType, out _);
			if (!TryParseResolvedCallableType(targetType, out string targetReturn, out List<string> targetParameters, out string? targetTargetSpec, out string? targetCallSpec))
				return false;
			if (!TryParseResolvedCallableType(value.ResolvedType ?? "", out string sourceReturn, out List<string> sourceParameters, out string? sourceTargetSpec, out string? sourceCallSpec))
				return true;
			if (targetReturn != sourceReturn
				|| targetTargetSpec != sourceTargetSpec
				|| targetCallSpec != sourceCallSpec
				|| targetParameters.Count != sourceParameters.Count)
				return true;
			for (int i = 0; i < targetParameters.Count; i++)
				if (targetParameters[i] != sourceParameters[i])
					return true;
			return false;
		}

		bool TryGetCallableShapeForEmitter(string? type, out CallableShape shape)
		{
			shape = default;
			if (string.IsNullOrWhiteSpace(type))
				return false;
			if (CallableShapeService.TryParseCallableShape(type, out shape))
				return true;
			string baseName = BaseResolvedTypeName(type);
			foreach (Definition definition in GetDefinitions())
			{
				if (definition is not NewtypeDefinition newtypeDefinition)
					continue;
				if (newtypeDefinition.Name != baseName && CName(newtypeDefinition) != baseName)
					continue;
				if (newtypeDefinition.UnderlyingType?.ResolvedType is string underlying
					&& CallableShapeService.TryParseCallableShape(underlying, out shape))
				{
					Dictionary<string, string> substitutions = GetConstructedTypeSubstitutionsForEmitter(type, newtypeDefinition);
					List<string> parameters = newtypeDefinition.Parameters.Count == 0
						? shape.Parameters
						: [.. GetCallableNewtypeParameterTypeNamesForEmitter(newtypeDefinition.Parameters)];
					if (substitutions.Count > 0)
					{
						parameters = [.. parameters.Select(parameter => SubstituteGenericTypeTokens(parameter, substitutions) ?? parameter)];
						shape = new CallableShape(
							shape.Kind,
							shape.Spec,
							shape.CallSpec,
							SubstituteGenericTypeTokens(shape.ReturnType, substitutions) ?? shape.ReturnType,
							parameters,
							shape.This);
					}
					else
					{
						shape = new CallableShape(shape.Kind, shape.Spec, shape.CallSpec, shape.ReturnType, parameters, shape.This);
					}
					return true;
				}
			}
			return false;
		}

		static IEnumerable<string> GetCallableNewtypeParameterTypeNamesForEmitter(IEnumerable<ParameterDefinition> parameters)
		{
			foreach (ParameterDefinition parameter in parameters)
			{
				if (parameter is ThisParameterDefinition or SizeOfParameterDefinition or NameOfParameterDefinition or VTableOfParameterDefinition)
					continue;
				string type = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? "#ERROR";
				yield return parameter.Modifier switch
				{
					ParameterModifier.In => "in " + type,
					ParameterModifier.Out => "out " + type,
					ParameterModifier.Thrown => "thrown " + type,
					ParameterModifier.Within => "within " + type,
					ParameterModifier.Upon => "upon " + type,
					_ => type
				};
			}
		}

		static Dictionary<string, string> GetConstructedTypeSubstitutionsForEmitter(string constructedType, TypeDefinition definition)
		{
			Dictionary<string, string> substitutions = [];
			List<string> typeArguments = ExtractConstructedTypeArguments(constructedType);
			int count = Math.Min(definition.GenericParameters.Count, typeArguments.Count);
			for (int i = 0; i < count; i++)
				substitutions[definition.GenericParameters[i].Name] = typeArguments[i];
			return substitutions;
		}

		static bool IsCallableSymbolExpression(Expression? expression)
		{
			return expression is MethodReferenceExpression
				or MemberReferenceExpression { Member: FunctionDefinition }
				or VariableReferenceExpression { Variable: FunctionDefinition }
				or NamedExpression
				|| expression?.ResolvedType is string resolvedType && IsResolvedCallableType(resolvedType);
		}

		bool TryParseExpandedCallableStorageType(string resolvedType, out string returnType, out List<string> parameterTypes)
		{
			return TryParseExpandedCallableStorageType(resolvedType, out returnType, out parameterTypes, out _, out _);
		}

		bool TryParseExpandedCallableStorageType(string resolvedType, out string returnType, out List<string> parameterTypes, out string? targetSpec, out string? callSpec)
		{
			returnType = "";
			parameterTypes = [];
			targetSpec = null;
			callSpec = null;
			string type = resolvedType.Trim();
			string kind;
			if (type.StartsWith("delegate ", StringComparison.Ordinal))
				kind = "delegate";
			else if (type.StartsWith("once ", StringComparison.Ordinal))
				kind = "once";
			else if (type.StartsWith("async ", StringComparison.Ordinal))
				kind = "async";
			else if (type.StartsWith("iter ", StringComparison.Ordinal) || type.StartsWith("iter(", StringComparison.Ordinal))
				kind = "iter";
			else
				return false;

			if (kind == "iter")
			{
				returnType = "bool";
				if (type.StartsWith("iter ", StringComparison.Ordinal))
				{
					string currentType = type["iter ".Length..].Trim();
					if (currentType.Length == 0)
						return false;
					parameterTypes.Add(PointerTypeName(currentType));
					return true;
				}

				string slotText = type["iter(".Length..];
				if (!slotText.EndsWith(")", StringComparison.Ordinal))
					return false;
				slotText = slotText[..^1].Trim();
				foreach (string slot in SplitTopLevel(slotText, ','))
				{
					string parameter = slot.Trim();
					if (parameter.Length == 0)
						continue;
					parameterTypes.Add(parameter.StartsWith("thrown ", StringComparison.Ordinal) ? parameter : PointerTypeName(parameter));
				}
				return parameterTypes.Count > 0;
			}

			int open = type.IndexOf('(', StringComparison.Ordinal);
			int close = type.LastIndexOf(')');
			if (open < 0 || close < open)
				return false;
			string prefix = type[kind.Length..open].Trim();
			if (prefix.Length == 0)
				return false;
			returnType = StripLeadingCallableSpecs(prefix, out targetSpec, out callSpec);
			if (returnType.Length == 0)
				return false;
			string parameterText = type[(open + 1)..close].Trim();
			if (parameterText.Length == 0 && kind != "async")
				return true;
			foreach (string parameter in SplitTopLevel(parameterText, ','))
				if (!string.IsNullOrWhiteSpace(parameter))
					parameterTypes.Add(parameter.Trim());
			if (kind == "async")
			{
				List<string> visibleParameters = [];
				List<string> completionParameters = [];
				if (returnType != "void")
					completionParameters.Add(returnType);
				foreach (string parameter in parameterTypes)
				{
					CallableSlot slot = CallableShapeService.ParseCallableSlot(parameter);
					if (slot.Modifier == "thrown")
						completionParameters.Add(slot.Type);
					else
						visibleParameters.Add(parameter);
				}
				returnType = "void";
				parameterTypes = visibleParameters;
				parameterTypes.Add("fn void(" + string.Join(", ", ["void*", .. completionParameters]) + ")");
				parameterTypes.Add("void*");
			}
			return true;
		}

		static bool TryGetArrayElementOnly(string? resolvedType, out string elementType)
		{
			elementType = "";
			if (string.IsNullOrWhiteSpace(resolvedType))
				return false;
			string type = resolvedType.Trim();
			if (!type.EndsWith("[]", StringComparison.Ordinal))
			{
				if (!TryGetFixedArrayElementType(type, out elementType))
					return false;
			}
			else
			{
				elementType = type[..^2].TrimEnd();
			}
			return !string.IsNullOrWhiteSpace(elementType);
		}

		static bool TryGetFixedArrayElementType(string type, out string elementType)
		{
			elementType = "";
			if (!TryGetFixedArrayShape(type, out elementType, out _))
				return false;
			return elementType.Length > 0;
		}

		static bool TryGetFixedArrayShape(string type, out string elementType, out long length)
		{
			length = 0;
			elementType = "";
			if (!type.EndsWith("]", StringComparison.Ordinal) || type.EndsWith("[]", StringComparison.Ordinal))
				return false;

			int bracket = type.LastIndexOf('[');
			if (bracket <= 0)
				return false;

			string lengthText = type[(bracket + 1)..^1].Trim();
			if (!long.TryParse(lengthText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out length))
				return false;

			elementType = type[..bracket].TrimEnd();
			return elementType.Length > 0;
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
			if (TryFormatInterfaceSlotExpression(call.Target, out string interfaceSlotExpression))
				target = interfaceSlotExpression;
			else if (TryFormatInterfaceSlotCallTarget(call, out string interfaceSlotCallTarget))
				target = interfaceSlotCallTarget;
			else if (function is not null
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
			int parameterIndex = 0;
			for (int i = 0; i < call.Arguments.Count; i++)
			{
				if (function?.IsAsync == true && call.Arguments[i].Modifier == ArgumentModifier.Catch)
					continue;
				if (ParametersLookLikeDelegateCallContextPair(parameters, parameterIndex)
					&& TryFormatExpandedDelegateArgument(call.Arguments[i], parameters, parameterIndex, genericSubstitutions, out string? callArgument, out string? contextArgument))
				{
					arguments.Add(callArgument);
					arguments.Add(contextArgument);
					parameterIndex += 2;
					continue;
				}
				ParameterDefinition? parameter = parameterIndex < parameters.Count ? parameters[parameterIndex] : null;
				arguments.Add(FormatArgumentValue(call.Arguments[i], parameter, genericSubstitutions));
				parameterIndex++;
			}
			if (TryRepairFormattedInterfaceSlotCallTarget(call, target, out string repairedTarget))
				target = repairedTarget;
			if (function?.IsAsync == true)
				RepairAsyncCallArgumentSlots(function, arguments);
			string text = target + "(" + string.Join(", ", arguments) + ")";
			if (function is not null && TryGetConcreteGenericType(function.ResolvedType, genericSubstitutions, out string? concreteReturnType))
			{
				if (NeedsGenericScalarCast(concreteReturnType))
					return CastFromErasedGeneric(text, concreteReturnType);
			}
			if (TryGetCallResultCastType(call, function, genericSubstitutions, out string? castType))
				return "(" + FormatResolvedType(castType!, "").Declaration.Trim() + ")(" + text + ")";
			return text;
		}

		bool TryFormatExpandedDelegateArgument(ArgumentExpression argument, List<ParameterDefinition> parameters, int parameterIndex, Dictionary<string, string> genericSubstitutions, out string callArgument, out string contextArgument)
		{
			callArgument = "";
			contextArgument = "";
			if (argument.Modifier != ArgumentModifier.None
				|| argument.Value is not InitializerExpression initializer
				|| initializer.Items.Count != 2)
				return false;

			Expression? callExpression = initializer.Items[0].Expression;
			Expression? contextExpression = initializer.Items[1].Expression;
			if (callExpression is null || contextExpression is null)
				return false;

			callArgument = FormatArgumentValue(new ArgumentExpression
			{
				SourceSyntax = argument.SourceSyntax,
				Value = callExpression,
				ResolvedType = callExpression.ResolvedType
			}, parameters[parameterIndex], genericSubstitutions);
			contextArgument = FormatArgumentValue(new ArgumentExpression
			{
				SourceSyntax = argument.SourceSyntax,
				Value = contextExpression,
				ResolvedType = contextExpression.ResolvedType
			}, parameters[parameterIndex + 1], genericSubstitutions);
			return true;
		}

		bool TryRepairFormattedInterfaceSlotCallTarget(CallExpression call, string target, out string repairedTarget)
		{
			repairedTarget = target;
			if (call.Arguments.Count == 0 || call.Arguments[0].Value is not Expression context)
				return false;
			string contextType = call.Arguments[0].ResolvedType ?? context.ResolvedType ?? "";
			if (!TryNormalizeInterfacePointerType(contextType, out _))
				return false;
			string contextText = FormatExpression(context);
			string prefix = contextText + "->";
			if (!target.StartsWith(prefix, StringComparison.Ordinal))
				return false;
			string slotName = target[prefix.Length..];
			if (slotName.Contains("->", StringComparison.Ordinal) || slotName.Contains(".", StringComparison.Ordinal))
				return false;
			repairedTarget = "(*" + contextText + ")->" + slotName;
			return true;
		}

		void RepairAsyncCallArgumentSlots(FunctionDefinition function, List<string> arguments)
		{
			List<ParameterDefinition> abiParameters = GetCallableParametersForCall(function);
			int expected = FormatFunctionParameterParts(function, skipThrown: true).Count + 2;
			int withinIndex = -1;
			for (int i = 0; i < abiParameters.Count; i++)
			{
				if (abiParameters[i].Modifier == ParameterModifier.Within || abiParameters[i] is WithinParameterDefinition)
				{
					withinIndex = i;
					break;
				}
			}
			if (withinIndex < 0 || withinIndex >= arguments.Count)
				return;
			if (arguments.Count == expected - 1)
			{
				arguments.Insert(withinIndex, "NULL");
				return;
			}
			if (arguments.Count == expected
				&& arguments[withinIndex] != "NULL"
				&& arguments[^1] == "NULL")
			{
				arguments.Insert(withinIndex, "NULL");
				arguments.RemoveAt(arguments.Count - 1);
			}
		}

		bool TryGetCallResultCastType(CallExpression call, FunctionDefinition? function, Dictionary<string, string> genericSubstitutions, out string? castType)
		{
			castType = null;
			if (call.ResolvedType is not string callType || callType == "void")
				return false;

			string? declaredType = null;
			if (function is not null)
			{
				declaredType = function.ResolvedType ?? function.ReturnType?.ResolvedType;
				if (declaredType is not null && genericSubstitutions.Count > 0)
					declaredType = SubstituteGenericTypeTokens(declaredType, genericSubstitutions);
			}
			else if (call.Target?.ResolvedType is string callableType
				&& TryParseResolvedCallableType(callableType, out string callableReturnType, out _))
			{
				declaredType = callableReturnType;
			}
			if (declaredType is null)
				return false;

			string actualReturnType = FirstReturnComponentType(declaredType);
			string expressionReturnType = FirstReturnComponentType(callType);
			if (StripLifetimeOnly(actualReturnType) == StripLifetimeOnly(expressionReturnType))
				return false;
			if (function?.ReturnType is not null && TryGetInterfacePointerCastType(function.ReturnType, out _))
				return false;
			if (NormalizeInterfaceInstanceResolvedType(actualReturnType) == NormalizeInterfaceInstanceResolvedType(expressionReturnType))
				return false;
			if (!IsPointerLikeCArgumentType(actualReturnType) || !IsPointerLikeCArgumentType(expressionReturnType))
				return false;

			castType = expressionReturnType;
			return true;
		}

		string FirstReturnComponentType(string type)
		{
			string lifetimePrefix = "";
			string structural = type.Trim();
			while (TryTakeLeadingLifetime(structural, out string? lifetime, out string? rest) && rest is not null)
			{
				lifetimePrefix += lifetime + " ";
				structural = rest;
			}
			if (structural.EndsWith("[]", StringComparison.Ordinal))
				return lifetimePrefix + structural[..^2].TrimEnd() + "*";
			if (structural.EndsWith("?", StringComparison.Ordinal))
				return lifetimePrefix + structural[..^1].TrimEnd();
			return type;
		}

		static bool TryTakeLeadingLifetime(string type, out string? lifetime, out string? rest)
		{
			lifetime = null;
			rest = null;
			foreach (string keyword in new[] { "escaped", "scoped", "unscoped" })
			{
				if (type.StartsWith(keyword + " ", StringComparison.Ordinal))
				{
					lifetime = keyword;
					rest = type[(keyword.Length + 1)..].TrimStart();
					return true;
				}
				if (!type.StartsWith(keyword + "(", StringComparison.Ordinal))
					continue;

				int close = type.IndexOf(')', keyword.Length + 1);
				if (close < 0)
					continue;
				lifetime = type[..(close + 1)];
				rest = type[(close + 1)..].TrimStart();
				return true;
			}
			return false;
		}

		static string StripLifetimeOnly(string type)
		{
			string result = type.Trim();
			while (TryTakeLeadingLifetime(result, out _, out string? rest) && rest is not null)
				result = rest;
			return result;
		}

		string NormalizeInterfaceInstanceResolvedType(string type)
		{
			string normalized = StripLifetimeOnly(type).Trim();
			while (normalized.StartsWith("const ", StringComparison.Ordinal))
				normalized = normalized[6..].TrimStart();
			while (normalized.EndsWith(" const", StringComparison.Ordinal))
				normalized = normalized[..^6].TrimEnd();

			int pointerCount = 0;
			while (normalized.EndsWith("*", StringComparison.Ordinal))
			{
				pointerCount++;
				normalized = normalized[..^1].TrimEnd();
			}
			if (IsInterfaceResolvedName(normalized) && pointerCount is 0 or 1 or 2)
				return normalized + "**";
			return type;
		}

		string FormatAssignmentExpression(AssignmentExpression assignment)
		{
			if (assignment.Operator == AssignmentOperator.Assign
				&& TryGetFixedArrayElementType(assignment.Target?.ResolvedType ?? "", out _))
				return FormatFixedArrayAssignment(assignment);

			if (assignment.Operator == AssignmentOperator.Assign
				&& TryFormatGenericStorageAddress(assignment.Target, out string destination, out string genericType))
			{
				string size = FormatGenericSizeExpression(genericType);
				if (assignment.Value is DefaultExpression)
					return FormatMemoryCall("memset", destination, "0", size);
				return FormatMemoryCall("memmove", destination, FormatGenericStorageSource(assignment.Value), size);
			}
			if (assignment.Operator == AssignmentOperator.Assign
				&& assignment.Target is VariableReferenceExpression { Variable: ParameterDefinition { Modifier: ParameterModifier.Out } parameter }
				&& IsAnyGenericParameterType(parameter.ResolvedType))
			{
				string size = FormatGenericSizeExpression(parameter.ResolvedType);
				if (assignment.Value is DefaultExpression)
					return FormatMemoryCall("memset", CName(parameter), "0", size);
				return FormatMemoryCall("memcpy", CName(parameter), FormatGenericStorageSource(assignment.Value), size);
			}
			string value = FormatExpression(assignment.Value);
			if (assignment.Operator == AssignmentOperator.Assign
				&& assignment.Target?.ResolvedType is string targetType)
				value = FormatAssignmentValueForTarget(targetType, assignment.Value!);
			return FormatExpression(assignment.Target) + " " + FormatAssignmentOperator(assignment.Operator) + " " + value;
		}

		string FormatFixedArrayAssignment(AssignmentExpression assignment)
		{
			string target = FormatExpression(assignment.Target);
			if (assignment.Value is DefaultExpression)
				return FormatMemoryCall("memset", target, "0", "sizeof(" + target + ")");

			string source = FormatFixedArrayCompoundLiteral(assignment.Target?.ResolvedType ?? "", assignment.Value);
			return FormatMemoryCall("memcpy", target, source, "sizeof(" + target + ")");
		}

		string FormatFixedArrayCompoundLiteral(string targetType, Expression? value)
		{
			string initializer = value switch
			{
				null => "{0}",
				LiteralExpression { Kind: LiteralKind.String } => "{" + FormatExpression(value) + "}",
				_ => FormatFixedArrayInitializer(value)
			};
			return "(" + FormatFixedArrayCompoundLiteralType(targetType) + ")" + initializer;
		}

		string FormatFixedArrayCompoundLiteralType(string targetType)
		{
			if (!TryGetFixedArrayShape(targetType, out string elementType, out long length))
				return "uint8_t[0]";
			string element = FormatResolvedType(elementType, "").Declaration.Trim();
			return element + "[" + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
		}

		string FormatIndexExpression(IndexExpression index)
		{
			string target = FormatExpression(index.Target);
			if (index.Target is CastExpression)
				target = "(" + target + ")";
			return target + "[" + string.Join(", ", index.Arguments.Select(FormatArgumentValue)) + "]";
		}

		string FormatAssignmentValueForTarget(string? targetType, Expression value)
		{
			return FormatAssignmentValueForTarget(targetType, value, null);
		}

		string FormatAssignmentValueForTarget(string? targetType, Expression value, IReadOnlyCollection<string>? erasedGenericNames)
		{
			string formatted = FormatExpression(value);
			if (targetType is not null
				&& TryFormatCallableAssignmentCast(targetType, value, erasedGenericNames, out string callableCast)
				&& IsCallableSymbolExpression(value)
				&& ShouldCastCallableAssignment(value, targetType))
			{
				return "(" + callableCast + ")" + formatted;
			}
			return formatted;
		}

		List<ParameterDefinition> GetCallableParametersForExpression(Expression? expression)
		{
			if (expression?.ResolvedType is not string resolvedType || !TryGetCallableShapeForEmitter(resolvedType, out CallableShape shape))
				return [];
			shape = SubstituteCallableShapeForMemberTarget(shape, expression);

			List<string> parameterTypes = GetSourceCallableParameterTypesForC(shape);
			List<ParameterDefinition> parameters = [];
			foreach (string parameterType in GetExpandedResolvedCallableParameterTypesForC(parameterTypes))
				parameters.Add(CreateCallableParameterFromResolvedType(parameterType));
			return parameters;
		}

		CallableShape SubstituteCallableShapeForMemberTarget(CallableShape shape, Expression? expression)
		{
			if (expression is not MemberReferenceExpression { Target.ResolvedType: string targetType, Member: FieldDefinition field })
				return shape;
			if (!TryFindFieldOwner(targetType, field, out TypeDefinition? owner) || owner is null || owner.GenericParameters.Count == 0)
				return shape;
			Dictionary<string, string> substitutions = GetConstructedTypeSubstitutionsForEmitter(targetType, owner);
			if (substitutions.Count == 0)
				return shape;
			return new CallableShape(
				shape.Kind,
				shape.Spec,
				shape.CallSpec,
				SubstituteGenericTypeTokens(shape.ReturnType, substitutions) ?? shape.ReturnType,
				[.. shape.Parameters.Select(parameter => SubstituteGenericTypeTokens(parameter, substitutions) ?? parameter)],
				shape.This);
		}

		bool TryFindFieldOwner(string targetType, FieldDefinition field, out TypeDefinition? owner)
		{
			owner = null;
			string baseName = BaseResolvedTypeName(targetType);
			foreach (Definition definition in GetDefinitions())
			{
				if (definition is not TypeDefinition candidate || candidate.Name != baseName && CName(candidate) != baseName)
					continue;
				foreach (FieldDefinition candidateField in GetTypeFields(candidate))
				{
					if (ReferenceEquals(candidateField, field) || candidateField.Name == field.Name)
					{
						owner = candidate;
						return true;
					}
				}
			}
			return false;
		}

		static List<string> GetSourceCallableParameterTypesForC(CallableShape shape)
		{
			if (shape.Kind is "delegate" or "once"
				&& shape.Parameters.Count > 0
				&& IsCallableStoredContextSlotForC(shape.Parameters[0]))
				return shape.Parameters.Skip(1).ToList();
			return shape.Parameters;
		}

		static bool IsCallableStoredContextSlotForC(string parameterType)
		{
			string type = parameterType.Trim();
			return type == "#THIS"
				|| type == "this"
				|| type.EndsWith(" this", StringComparison.Ordinal);
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
			else if (typeName.StartsWith("upon ", StringComparison.Ordinal))
			{
				modifier = ParameterModifier.Upon;
				typeName = typeName[5..].TrimStart();
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
				&& parameter is not SizeOfParameterDefinition
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
					value = "(" + FormatResolvedType(rawExpectedParameterType, "").Declaration.Trim() + ")" + value;
			}
			if (argument.Modifier == ArgumentModifier.None
				&& expectedParameterType is not null
				&& IsResolvedCallableType(expectedParameterType)
				&& (argument.Value is null || !delegateThunksByExpression.ContainsKey(argument.Value))
				&& (argument.Value is MethodReferenceExpression or VariableReferenceExpression { Variable: FunctionDefinition } or NamedExpression
					|| argument.Value?.ResolvedType is string argumentType && IsResolvedCallableType(argumentType)))
			{
				string castType = rawExpectedParameterType is not null
					&& IsResolvedCallableType(rawExpectedParameterType)
					&& ContainsGenericParameterTypeName(rawExpectedParameterType)
					? rawExpectedParameterType
					: expectedParameterType;
				string castDeclaration = parameter?.Type is not null
					? FormatTypeOrResolved(parameter.Type, castType, "").Declaration.Trim()
					: FormatResolvedType(castType, "").Declaration.Trim();
				value = "(" + castDeclaration + ")" + value;
			}
			if (argument.Modifier == ArgumentModifier.None
				&& expectedParameterType is not null
				&& argument.Value?.ResolvedType is string erasedValueType
				&& IsErasedPointerStorageType(erasedValueType)
				&& TryNormalizeCallableInterfaceParameterType(expectedParameterType, out string normalizedInterfaceParameterType))
				value = "(" + FormatResolvedType(normalizedInterfaceParameterType, "").Declaration.Trim() + ")" + value;
			if (argument.Modifier == ArgumentModifier.None
				&& expectedParameterType is not null
				&& argument.Value?.ResolvedType is string valueType
				&& ShouldCastPointerArgument(valueType, expectedParameterType))
				value = "(" + FormatParameterArgumentCastType(parameter?.Type, expectedParameterType) + ")" + value;
			if (argument.Modifier == ArgumentModifier.None && parameter?.Modifier == ParameterModifier.In)
				return FormatInArgument(argument.Value, value, TryGetConcreteGenericType(expectedParameterType, genericSubstitutions, out string? concreteInType) ? concreteInType : expectedParameterType);
			return argument.Modifier switch
			{
				ArgumentModifier.Out or ArgumentModifier.Catch when TryFormatForwardedOutArgument(argument.Value, out string forwarded) => forwarded,
				ArgumentModifier.Out or ArgumentModifier.Catch => FormatOutArgument(value, GetOutArgumentStorageType(argument.Value) ?? argument.Value?.ResolvedType, expectedParameterType),
				_ => value
			};
		}

		static bool IsErasedPointerStorageType(string type)
		{
			string normalized = StripTypeDecorators(type);
			return normalized is "untyped" or "nuint" or "nint" or "void*" or "const void*";
		}

		string FormatParameterArgumentCastType(TypeReference? parameterType, string expectedParameterType)
		{
			if (TryGetInterfacePointerCastType(parameterType, out string interfaceName))
				return CTypeName(interfaceName) + " **";
			return FormatTypeOrResolved(parameterType, expectedParameterType, "").Declaration.Trim();
		}

		static string? GetOutArgumentStorageType(Expression? expression)
		{
			return expression switch
			{
				VariableReferenceExpression { Variable: Definition definition } => definition.ResolvedType,
				MemberReferenceExpression { Member: Definition definition } => definition.ResolvedType,
				_ => null
			};
		}

		string FormatOutArgument(string value, string? valueType, string? expectedParameterType)
		{
			string address = "&" + value;
			if (string.IsNullOrWhiteSpace(valueType) || string.IsNullOrWhiteSpace(expectedParameterType))
				return address;
			if (!IsResolvedPointerType(valueType) && !IsResolvedPointerType(expectedParameterType))
				return address;
			string valuePointerType = AddResolvedPointer(valueType);
			string expectedPointerType = AddResolvedPointer(expectedParameterType);
			if (valuePointerType == expectedPointerType
				|| !ShouldCastPointerArgument(valuePointerType, expectedPointerType) && !IsResolvedPointerType(expectedPointerType))
				return address;
			return "(" + FormatResolvedType(expectedPointerType, "").Declaration.Trim() + ")" + address;
		}

		static string AddResolvedPointer(string type)
		{
			return type.Trim() + "*";
		}

		static bool IsResolvedPointerType(string type)
		{
			return type.TrimEnd().EndsWith("*", StringComparison.Ordinal);
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
			if (expression is InitializerExpression initializer)
				return "&(" + type + ")" + FormatInitializer(initializer, includeType: false);
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
				&& IsErasedGenericElementType(elementType)
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
				&& IsErasedGenericElementType(elementType)
				&& index.Arguments.Count == 1;
		}

		bool IsErasedGenericElementType(string elementType)
		{
			string stripped = StripTypeDecorators(elementType);
			return !TryGetFixedArrayShape(stripped, out _, out _)
				&& IsAnyGenericParameterType(stripped);
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

		bool ShouldCastPointerArgument(string valueType, string expectedType)
		{
			if (valueType == expectedType)
				return false;
			if (!TryGetPointerElementType(valueType, out string valueElement) || !TryGetPointerElementType(expectedType, out string expectedElement))
				return false;
			if (HasTopLevelConstForC(valueElement)
				&& !HasTopLevelConstForC(expectedElement)
				&& StripTopLevelConstForC(valueElement) == StripTopLevelConstForC(expectedElement))
				return true;
			return IsDerivedClassPointerArgument(valueElement, expectedElement);
		}

		bool IsDerivedClassPointerArgument(string valueElement, string expectedElement)
		{
			string valueClassName = NormalizePointerArgumentClassName(valueElement);
			string expectedClassName = NormalizePointerArgumentClassName(expectedElement);
			if (valueClassName == expectedClassName || valueClassName.Length == 0 || expectedClassName.Length == 0)
				return false;
			ClassDefinition? valueClass = FindClassDefinition(valueClassName);
			if (valueClass is null || FindClassDefinition(expectedClassName) is null)
				return false;
			for (ClassDefinition? current = GetDirectBaseClass(valueClass); current is not null; current = GetDirectBaseClass(current))
				if (current.Name == expectedClassName)
					return true;
			return false;
		}

		ClassDefinition? FindClassDefinition(string name)
		{
			foreach (Definition definition in GetDefinitions())
				if (definition is ClassDefinition candidate && candidate.Name == name)
					return candidate;
			return null;
		}

		static string NormalizePointerArgumentClassName(string type)
		{
			return StripTypeDecorators(StripTopLevelConstForC(StripLifetimeOnly(type)));
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
				string resolvedThisType = function.AbiThisType?.ResolvedType ?? function.EffectiveThisParameter?.ResolvedType ?? GetImplicitThisResolvedType(containingType);
				parameters.Add(new ThisParameterDefinition
				{
					Name = "this",
					Symbol = "this",
					Type = function.AbiThisType ?? (containingType is NewtypeDefinition
						? new TypeDefinitionReference { Definition = containingType, Name = containingType.Name }
						: new PointerTypeReference { ElementType = new TypeDefinitionReference { Definition = containingType, Name = containingType.Name } }),
					ResolvedType = resolvedThisType
				});
			}
			foreach (ParameterDefinition parameter in GetAbiOrderedParameters(function.IsAsync
				? function.Parameters.Where(static parameter => parameter.Modifier != ParameterModifier.Thrown).ToList()
				: function.Parameters))
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
			if (function.IsAsync)
				parameters.AddRange(CreateAsyncCompletionSourceParameters(function));
			return parameters;
		}

		static List<ParameterDefinition> CreateAsyncCompletionSourceParameters(FunctionDefinition function)
		{
			List<string> completionParameters = [];
			string returnType = function.ResolvedType ?? "void";
			if (returnType != "void")
				completionParameters.Add(returnType);
			if (function.Parameters.FirstOrDefault(static parameter => parameter.Modifier == ParameterModifier.Thrown) is ParameterDefinition thrown)
				completionParameters.Add(thrown.ResolvedType ?? thrown.Type?.ResolvedType ?? "#ERROR");
			completionParameters.Insert(0, "void*");
			string callableType = CallableShapeService.BuildCallableType("fn", "void", completionParameters);
			return
			[
				new ParameterDefinition
				{
					Name = "complete",
					Symbol = "complete",
					ResolvedType = callableType,
					Type = new NamedTypeReference { Name = callableType, ResolvedType = callableType }
				},
				new ParameterDefinition
				{
					Name = "complete_context",
					Symbol = "complete_context",
					ResolvedType = "void*",
					Type = new PointerTypeReference { ElementType = new PrimitiveTypeReference { Type = PrimitiveType.Void, ResolvedType = "void" }, ResolvedType = "void*" }
				}
			];
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

				if (parameter is SizeOfParameterDefinition or NameOfParameterDefinition or VTableOfParameterDefinition)
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
			while (type.EndsWith(" escaped", StringComparison.Ordinal)
				|| type.EndsWith(" scoped", StringComparison.Ordinal)
				|| type.EndsWith(" unscoped", StringComparison.Ordinal))
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
			if (type is "string" or "wstring" or "astring" or "void" or "untyped" or "any" or "copyable" or "auto")
				return false;
			if (IsPrimitiveScalarType(type))
				return true;
			return IsEnumType(type);
		}

		bool IsAnyGenericParameterType(string? type)
		{
			string stripped = StripTypeDecorators(type ?? "");
			if (TryGetFixedArrayShape(stripped, out _, out _))
				return false;
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
			if (FormatCallableCallComponent(member.Target, member.Name) is string callableCallComponent)
				return callableCallComponent;
			if (member.Member is FunctionDefinition function
				&& (member.Target is null
					|| function.OutOfScopeOwnerName is not null
					|| containingTypes.TryGetValue(function, out TypeDefinition? owner) && owner is not InterfaceDefinition))
				return CName(function);
			if (member.Member is FunctionDefinition interfaceFunction
				&& member.Target is not null
				&& containingTypes.TryGetValue(interfaceFunction, out TypeDefinition? interfaceOwner)
				&& interfaceOwner is InterfaceDefinition)
				return FormatInterfaceFunctionMemberReference(member.Target, interfaceFunction);
			if (member.Member is VariableDefinition variable)
				return CName(variable);
			if (member.Member is FieldDefinition field)
			{
				if (FormatInterfaceSlotMember(member.Target, CName(field)) is string formattedInterfaceSlotMember)
					return formattedInterfaceSlotMember;
				string fieldTarget = FormatMemberTarget(member.Target);
				return fieldTarget + (IsPointerMemberTarget(member.Target) ? "->" : ".") + CName(field);
			}
			string? expandedThisComponent = FormatExpandedThisComponent(member.Target, member.Name);
			if (expandedThisComponent is not null)
				return expandedThisComponent;
			string? interfaceSlotMember = FormatInterfaceSlotMember(member.Target, member.Name);
			if (interfaceSlotMember is not null)
				return interfaceSlotMember;
			string target = FormatMemberTarget(member.Target);
			string separator = IsPointerMemberTarget(member.Target) ? "->" : ".";
			return target + separator + SanitizeIdentifier(member.Name);
		}

		string FormatMemberTarget(Expression? target)
		{
			string formatted = FormatExpression(target);
			return target switch
			{
				UnaryExpression { Operator: UnaryOperator.PointerDereference } => "(" + formatted + ")",
				CastExpression => "(" + formatted + ")",
				ConditionalExpression => "(" + formatted + ")",
				AssignmentExpression => "(" + formatted + ")",
				BinaryExpression => "(" + formatted + ")",
				_ => formatted
			};
		}

		string FormatInterfaceFunctionMemberReference(Expression target, FunctionDefinition function)
		{
			string name = SanitizeIdentifier(BindableNodeAnalyzer.GetCallableName(function));
			if (target is UnaryExpression { Operator: UnaryOperator.PointerDereference, Operand: Expression operand }
				&& operand.ResolvedType is string operandType
				&& !operandType.TrimEnd().EndsWith("**", StringComparison.Ordinal))
				return FormatExpression(operand) + "->" + name;
			return "(*" + FormatExpression(target) + ")->" + name;
		}

		string? FormatInterfaceSlotMember(Expression? target, string name)
		{
			if (target is null)
				return null;
			if (target is UnaryExpression { Operator: UnaryOperator.PointerDereference, Operand: Expression operand }
				&& TryGetPointerElementType(operand.ResolvedType, out string operandElementType)
				&& TryFindInterfaceStruct(InterfaceStructNameFromPointerElement(operandElementType), name, out _))
			{
				string formattedOperand = FormatExpression(operand);
				return operand.ResolvedType?.TrimEnd().EndsWith("**", StringComparison.Ordinal) == true
					? "(*" + formattedOperand + ")->" + SanitizeIdentifier(name)
					: formattedOperand + "->" + SanitizeIdentifier(name);
			}

			string targetType = target.ResolvedType ?? "";
			if (TryNormalizeInterfacePointerType(targetType, out string normalizedTargetType))
				targetType = normalizedTargetType;
			if (string.IsNullOrWhiteSpace(targetType) || !targetType.EndsWith("**", StringComparison.Ordinal))
				return null;

			string interfaceName = targetType[..^2].Trim();
			return TryFindInterfaceStruct(interfaceName, name, out _)
				? "(*" + FormatExpression(target) + ")->" + SanitizeIdentifier(name)
				: null;
		}

		string? FormatCallableCallComponent(Expression? target, string name)
		{
			if (name != "call")
				return null;
			if (TryFormatInterfaceSlotExpression(target, out string interfaceSlotExpression))
				return interfaceSlotExpression;
			if (target?.ResolvedType is not string targetType)
				return null;
			return IsResolvedCallableType(targetType) ? FormatMemberTarget(target) : null;
		}

		bool TryFormatInterfaceSlotExpression(Expression? expression, out string text)
		{
			text = "";
			switch (expression)
			{
				case MemberReferenceExpression member:
					if (FormatInterfaceSlotMember(member.Target, member.Name) is string referenceText)
					{
						text = referenceText;
						return true;
					}
					break;

				case MemberExpression member:
					if (FormatInterfaceSlotMember(member.Target, member.Name) is string expressionText)
					{
						text = expressionText;
						return true;
					}
					break;
			}
			return false;
		}

		bool TryFormatInterfaceSlotCallTarget(CallExpression call, out string target)
		{
			target = "";
			if (call.Arguments.Count == 0 || call.Arguments[0].Value is not Expression context)
				return false;
			string slotName = call.Target switch
			{
				MemberReferenceExpression member => member.Name,
				MemberExpression member => member.Name,
				_ => ""
			};
			if (string.IsNullOrWhiteSpace(slotName))
				return false;

			string contextType = call.Arguments[0].ResolvedType ?? context.ResolvedType ?? "";
			if (TryNormalizeInterfacePointerType(contextType, out string normalizedContextType))
				contextType = normalizedContextType;
			if (string.IsNullOrWhiteSpace(contextType) || !contextType.EndsWith("**", StringComparison.Ordinal))
				return false;

			string interfaceName = contextType[..^2].Trim();
			if (!TryFindInterfaceStruct(interfaceName, slotName, out _))
				return false;

			target = "(*" + FormatExpression(context) + ")->" + SanitizeIdentifier(slotName);
			return true;
		}

		static string InterfaceStructNameFromPointerElement(string type)
		{
			type = StripTopLevelConstForC(type);
			return TryGetPointerElementType(type, out string elementType)
				? StripTopLevelConstForC(elementType)
				: type;
		}

		bool TryNormalizeInterfacePointerType(string parameterType, out string normalizedParameterType)
		{
			normalizedParameterType = parameterType;
			string type = StripLifetimeOnly(parameterType.Trim());
			int pointerCount = 0;
			while (type.EndsWith("*", StringComparison.Ordinal))
			{
				pointerCount++;
				type = type[..^1].TrimEnd();
			}

			if (pointerCount != 1)
				return false;

			string baseType = StripTopLevelConstForC(type);
			baseType = baseType.StartsWith("volatile ", StringComparison.Ordinal) ? baseType[9..].TrimStart() : baseType;
			baseType = baseType.EndsWith(" volatile", StringComparison.Ordinal) ? baseType[..^9].TrimEnd() : baseType;
			if (!IsInterfaceResolvedName(baseType))
				return false;

			normalizedParameterType = StripLifetimeOnly(parameterType.Trim()) + "*";
			return true;
		}

		bool TryFindInterfaceStruct(string interfaceName, string fieldName, out StructDefinition? interfaceStruct)
		{
			interfaceStruct = null;
			foreach (Definition definition in GetDefinitions())
			{
				if (definition is StructDefinition candidate
					&& candidate.Name == interfaceName
					&& HasField(candidate, fieldName))
				{
					interfaceStruct = candidate;
					return true;
				}
			}
			return false;
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
			if (variable is not null && currentAsyncFrameReplacements.TryGetValue(variable, out string? frameReference))
				return frameReference;
			if (variable is not null
				&& TryGetCName(variable, out string? cName)
				&& cName is not null
				&& currentAsyncFrameNameReplacements.TryGetValue(cName, out string? frameNameReference))
				return frameNameReference;

			return variable switch
			{
				FunctionDefinition function => CName(function),
				VariableDefinition definition => CName(definition),
				ParameterDefinition { Modifier: ParameterModifier.Thrown } parameter when currentFunction?.IsAsync == true => CName(parameter),
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

		static bool TryGetCName(BindableNode variable, out string? name)
		{
			name = variable switch
			{
				Definition definition => CName(definition),
				DeclarationTarget target => CName(target),
				_ => null
			};
			return name is not null;
		}

		string FormatThisExpression()
		{
			if (currentAsyncFrameNameReplacements.TryGetValue("this", out string? replacement))
				return replacement;
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

		string FormatFileScopeInitializer(string? targetType, Expression expression)
		{
			if (TryGetFixedArrayElementType(targetType ?? "", out _))
				return FormatFixedArrayInitializer(expression);
			if (expression is InitializerExpression initializer)
				return FormatInitializer(initializer, includeType: false);
			return FormatFileScopeConstantExpression(expression) ?? FormatExpression(expression);
		}

		string? FormatFileScopeConstantExpression(Expression? expression, HashSet<VariableDefinition>? seen = null)
		{
			return expression switch
			{
				null => null,
				LiteralExpression { Kind: LiteralKind.Number } literal => FormatNumberLiteralForC(literal.Text),
				LiteralExpression { Kind: LiteralKind.True } => "1",
				LiteralExpression { Kind: LiteralKind.False } => "0",
				LiteralExpression { Kind: LiteralKind.Null } => "NULL",
				UnaryExpression { Operator: UnaryOperator.Minus, Operand: LiteralExpression { Kind: LiteralKind.Number } literal } => "-" + FormatNumberLiteralForC(literal.Text, negativeContext: true),
				UnaryExpression unary => FormatFileScopeUnaryConstant(unary, seen),
				BinaryExpression binary => FormatFileScopeBinaryConstant(binary, seen),
				CastExpression cast => FormatFileScopeCastConstant(cast, seen),
				ParenthesizedExpression parenthesized => FormatFileScopeConstantExpression(parenthesized.Expression, seen) is string value ? "(" + value + ")" : null,
				VariableReferenceExpression { Variable: VariableDefinition variable } => FormatReferencedFileScopeConstant(variable, seen),
				NamedExpression named => named.Name,
				_ => null
			};
		}

		string? FormatFileScopeUnaryConstant(UnaryExpression unary, HashSet<VariableDefinition>? seen)
		{
			string? operand = FormatFileScopeConstantExpression(unary.Operand, seen);
			return operand is null ? null : unary.Operator switch
			{
				UnaryOperator.Plus => "+" + operand,
				UnaryOperator.Minus => "-" + operand,
				UnaryOperator.LogicalNot => "!" + operand,
				UnaryOperator.BitwiseNot => "~" + operand,
				_ => null
			};
		}

		string? FormatFileScopeBinaryConstant(BinaryExpression binary, HashSet<VariableDefinition>? seen)
		{
			string? left = FormatFileScopeConstantExpression(binary.Left, seen);
			string? right = FormatFileScopeConstantExpression(binary.Right, seen);
			return left is null || right is null ? null : "(" + left + " " + FormatBinaryOperator(binary.Operator) + " " + right + ")";
		}

		string? FormatFileScopeCastConstant(CastExpression cast, HashSet<VariableDefinition>? seen)
		{
			string? value = FormatFileScopeConstantExpression(cast.Expression, seen);
			return value is null ? null : "(" + FormatType(cast.Type, "").Declaration.Trim() + ")(" + value + ")";
		}

		string? FormatReferencedFileScopeConstant(VariableDefinition variable, HashSet<VariableDefinition>? seen)
		{
			if (variable.InitialValue is null)
				return CName(variable);
			seen ??= [];
			if (!seen.Add(variable))
				return CName(variable);
			string? value = FormatFileScopeConstantExpression(variable.InitialValue, seen);
			seen.Remove(variable);
			return value ?? CName(variable);
		}

		string FormatInitializer(InitializerExpression initializer, bool includeType = true)
		{
			List<string> items = [];
			foreach (InitializerItem item in initializer.Items)
			{
				string value = item.Expression switch
				{
					null => "0",
					ArrayExpression array => FormatFixedArrayInitializer(array),
					InitializerExpression nested => FormatInitializer(nested, includeType: false),
					_ => FormatAssignmentValueForTarget(item.TargetStorageResolvedType ?? item.TargetResolvedType ?? item.ResolvedType, item.Expression, item.TargetStorageGenericNames)
				};
				string? target = FormatInitializerTarget(item.Target);
				items.Add(target is null ? value : "." + target + " = " + value);
			}
			string body = items.Count == 0 ? "{ 0 }" : "{ " + string.Join(", ", items) + " }";
			if (!includeType || !IsAggregateValueType(initializer.ResolvedType))
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

		string FormatBinaryExpression(BinaryExpression binary)
		{
			string left = FormatExpression(binary.Left);
			string right = FormatExpression(binary.Right);
			string op = FormatBinaryOperator(binary.Operator);
			if (binary.Operator is BinaryOperator.Add or BinaryOperator.Subtract
				&& TryFormatGenericArrayBytePointer(binary.Left, out string bytePointer))
				left = bytePointer;
			else if (binary.Operator is BinaryOperator.Add
				&& TryFormatGenericArrayBytePointer(binary.Right, out bytePointer))
				right = bytePointer;
			return "(" + left + " " + op + " " + right + ")";
		}

		bool TryFormatGenericArrayBytePointer(Expression? expression, out string value)
		{
			value = "";
			if (expression?.ResolvedType is not string resolvedType)
				return false;
			if (!TryGetArrayElementType(resolvedType, out string elementType)
				&& !TryGetPointerElementType(resolvedType, out elementType))
				return false;
			if (!IsErasedGenericElementType(elementType))
				return false;

			string bytePointer = ElementTypeIsConst(elementType) ? "const uint8_t*" : "uint8_t*";
			value = "((" + bytePointer + ")" + FormatExpression(expression) + ")";
			return true;
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

		string FormatLiteral(LiteralExpression literal)
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

		string FormatNameOfExpression(NameOfExpression nameOf)
		{
			if (nameOf.Value is not string value)
				return UnsupportedExpression(nameOf);
			LiteralExpression literal = new()
			{
				SourceSyntax = nameOf.SourceSyntax,
				Kind = LiteralKind.String,
				Text = FormatCampStringLiteral(value),
				Value = value,
				ResolvedType = nameOf.ResolvedType
			};
			return FormatLiteral(literal);
		}

		static string FormatCampStringLiteral(string value)
		{
			StringBuilder builder = new("\"");
			foreach (char ch in value)
			{
				builder.Append(ch switch
				{
					'\\' => "\\\\",
					'"' => "\\\"",
					'\n' => "\\n",
					'\r' => "\\r",
					'\t' => "\\t",
					_ => ch
				});
			}
			builder.Append('"');
			return builder.ToString();
		}

		static bool IsWideStringLiteralType(string? type)
		{
			type = StripTypeQualifiers(type ?? "");
			return type is "wstring" or "wchar*" or "wchar[]";
		}

		string FormatWideStringLiteral(LiteralExpression literal)
		{
			string prefix = compilation.Target?.Capabilities.GetCapabilityValue("wstring_prefix") ?? "";
			if (!string.IsNullOrWhiteSpace(prefix))
				return prefix + FormatWideCampStringLiteral(literal.Value as string ?? "");

			string text = literal.Value as string ?? "";
			if (currentWideStringLiteralNames.TryGetValue(text, out string? existingName))
				return existingName;

			List<string> units = [];
			for (int i = 0; i < text.Length; i++)
				units.Add("0x" + ((int)text[i]).ToString("X4", CultureInfo.InvariantCulture));
			units.Add("0");
			string name = "__camp_wstr_" + currentWideStringLiteralIndex.ToString(CultureInfo.InvariantCulture);
			currentWideStringLiteralIndex++;
			currentWideStringLiteralNames.Add(text, name);
			currentWideStringLiterals.Add((name, string.Join(", ", units)));
			return name;
		}

		static string FormatWideCampStringLiteral(string value)
		{
			StringBuilder builder = new("\"");
			for (int i = 0; i < value.Length; i++)
			{
				char ch = value[i];
				switch (ch)
				{
					case '\\':
						builder.Append("\\\\");
						break;
					case '"':
						builder.Append("\\\"");
						break;
					case '\n':
						builder.Append("\\n");
						break;
					case '\r':
						builder.Append("\\r");
						break;
					case '\t':
						builder.Append("\\t");
						break;
					case '\0':
						builder.Append("\\0");
						break;
					default:
						if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
						{
							int codePoint = char.ConvertToUtf32(ch, value[i + 1]);
							builder.Append("\\U").Append(codePoint.ToString("X8", CultureInfo.InvariantCulture));
							i++;
						}
						else if (ch is >= ' ' and <= '~')
						{
							builder.Append(ch);
						}
						else
						{
							builder.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
						}
						break;
				}
			}
			builder.Append('"');
			return builder.ToString();
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
						parts.Add(FormatOrdinaryParameterType(parameter, name).Declaration);
					}
				}
			});
			return "(" + (parts.Count == 0 ? "void" : string.Join(", ", parts)) + ")";
		}

		CType FormatOrdinaryParameterType(ParameterDefinition parameter, string name)
		{
			if (parameter is VTableOfParameterDefinition)
				return parameter.ResolvedType is null
					? FormatTypeOrResolved(parameter.Type, parameter.ResolvedType, name)
					: FormatResolvedType(parameter.ResolvedType, name, normalizeInterfacePointer: false);
			if (parameter.ResolvedType is not null
				&& TryNormalizeInterfacePointerType(parameter.ResolvedType, out string normalizedInterfaceType))
				return FormatResolvedType(normalizedInterfaceType, name);
			if (TryGetInterfacePointerCastType(parameter.Type, out string interfaceName))
				return FormatResolvedType(interfaceName + "*", name);
			return FormatTypeOrResolved(parameter.Type, parameter.ResolvedType, name);
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
					string resolvedThisType = function.AbiThisType?.ResolvedType ?? function.EffectiveThisParameter?.ResolvedType ?? GetImplicitThisResolvedType(type);
					AddImplicitThisParameterParts(parts, function, resolvedThisType, NeedsAbiThisFixup(function) ? "ctx" : "this");
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
						parts.Add(FormatOrdinaryParameterType(parameter, name).Declaration);
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
			if (componentParameter is null
				&& target is ThisExpression
				&& currentFunction is not null
				&& RequiresImplicitThisParameter(currentFunction)
				&& containingTypes.TryGetValue(currentFunction, out TypeDefinition? implicitOwner)
				&& implicitOwner is NewtypeDefinition
				&& TryGetCallableNewtypeStorageComponentsForC(implicitOwner.Name, out List<(string Name, string Type)> implicitCallableComponents)
				&& implicitCallableComponents.Count == 2
				&& implicitCallableComponents[0].Name == "call"
				&& implicitCallableComponents[1].Name == "context")
			{
				return name switch
				{
					"call" => "this_call",
					"context" => "this_context",
					_ => null
				};
			}
			if (componentParameter is null)
				return null;
			string componentName = CName(componentParameter);
			if (componentName == "this"
				&& TryGetCallableNewtypeStorageComponentsForC(componentParameter.ResolvedType ?? "", out List<(string Name, string Type)> callableComponents)
				&& callableComponents.Count == 2
				&& callableComponents[0].Name == "call"
				&& callableComponents[1].Name == "context")
			{
				return name switch
				{
					"call" => "this_call",
					"context" => "this_context",
					_ => null
				};
			}
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

		void AddImplicitThisParameterParts(List<string> parts, FunctionDefinition function, string resolvedThisType, string name)
		{
			if (name == "this"
				&& TryGetCallableNewtypeStorageComponentsForC(resolvedThisType, out List<(string Name, string Type)> components)
				&& components.Count == 2
				&& components[0].Name == "call"
				&& components[1].Name == "context")
			{
				parts.Add(FormatResolvedType(components[0].Type, "this_call").Declaration);
				parts.Add(FormatResolvedType(components[1].Type, "this_context").Declaration);
				return;
			}

			parts.Add(FormatTypeOrResolved(function.AbiThisType, resolvedThisType, name).Declaration);
		}

		static string GetImplicitThisResolvedType(TypeDefinition type)
		{
			return type is NewtypeDefinition ? type.Name : type.Name + "*";
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

			string declarator = FormatFunctionPointerDeclarator(name, callable.TargetSpec ?? GetDefaultTargetTypeSpec(functionPointer: true), callable.CallSpec);
			string returnType = callable.ReturnType?.ResolvedType ?? ResolvedTypeForC(callable.ReturnType, callable.ReturnType?.ResolvedType);
			List<string> parameterTypes = GetExpandedCallableParameterTypesForC(parameters);
			if (!ExpandResolvedCallableReturnForC(ref returnType, parameterTypes))
				return "typedef " + FormatType(callable.ReturnType, declarator).Declaration + FormatParameters(parameters);
			return "typedef " + FormatResolvedType(returnType, declarator).Declaration + "(" + FormatResolvedParameterList(parameterTypes) + ")";
		}

		string FormatCallableNewtypeTypedef(CallableTypeReference callable, List<ParameterDefinition> parameters, string name)
		{
			if (callable.Kind == CallableKind.Async)
				return FormatAsyncCallableNewtypeTypedef(callable, parameters, name);
			string returnType = callable.ReturnType?.ResolvedType ?? ResolvedTypeForC(callable.ReturnType, callable.ReturnType?.ResolvedType);
			List<(string Type, string Name)> parameterTypes = GetNamedCallableNewtypeParameterTypesForC(callable, parameters);
			return "typedef " + FormatInlineResolvedFunctionPointer(returnType, parameterTypes, name, callable.TargetSpec ?? GetDefaultTargetTypeSpec(functionPointer: true), callable.CallSpec);
		}

		string FormatAsyncCallableNewtypeTypedef(CallableTypeReference callable, List<ParameterDefinition> parameters, string name)
		{
			List<string> parameterDeclarations = GetNamedCallableNewtypeParameterTypesForC(callable, parameters.Where(static parameter => parameter.Modifier != ParameterModifier.Thrown).ToList())
				.Select(parameter => FormatResolvedParameter(parameter.Type, parameter.Name))
				.ToList();
			parameterDeclarations.AddRange(FormatAsyncCompletionParameterDeclarations(callable.ReturnType, callable.ReturnType?.ResolvedType, parameters));
			return "typedef " + FormatInlineResolvedFunctionPointerFromDeclarations("void", parameterDeclarations, name, callable.TargetSpec ?? GetDefaultTargetTypeSpec(functionPointer: true), callable.CallSpec);
		}

		string FormatIterTypedef(IterTypeReference iter, List<ParameterDefinition> parameters, string name)
		{
			List<string> parameterTypes = ["void*"];
			foreach (string currentType in GetIteratorCurrentTypesForC(iter))
				parameterTypes.Add(PointerTypeName(currentType));
			if (GetIteratorThrownTypeForC(iter) is string thrownType)
				parameterTypes.Add("thrown " + thrownType);
			parameterTypes.AddRange(GetExpandedCallableParameterTypesForC(parameters));
			return "typedef " + FormatInlineResolvedFunctionPointer("bool", parameterTypes, name);
		}

		string FormatIterNewtypeTypedef(IterTypeReference iter, List<ParameterDefinition> parameters, string name)
		{
			List<(string Type, string Name)> parameterTypes = [("void*", "context")];
			List<string> currentTypes = GetIteratorCurrentTypesForC(iter);
			for (int i = 0; i < currentTypes.Count; i++)
				parameterTypes.Add(("out " + currentTypes[i], i == 0 ? "current" : "current" + i.ToString(CultureInfo.InvariantCulture)));
			if (GetIteratorThrownTypeForC(iter) is string thrownType)
				parameterTypes.Add(("thrown " + thrownType, "error"));
			parameterTypes.AddRange(GetNamedCallableNewtypeParameterTypesForC(new CallableTypeReference { Kind = CallableKind.Function }, parameters));
			return "typedef " + FormatInlineResolvedFunctionPointer("bool", parameterTypes, name);
		}

		List<string> GetIteratorCurrentTypesForC(IterTypeReference iter)
		{
			List<string> currentTypes = [];
			if (iter.Parameters.Count == 0)
			{
				currentTypes.Add(ResolvedTypeForC(iter.ElementType, iter.ElementType?.ResolvedType));
				return currentTypes;
			}

			foreach (ParameterDefinition parameter in iter.Parameters)
				if (parameter.Modifier != ParameterModifier.Thrown)
					currentTypes.Add(ResolvedTypeForC(parameter.Type, parameter.ResolvedType));
			return currentTypes;
		}

		static string? GetIteratorThrownTypeForC(IterTypeReference iter)
		{
			foreach (ParameterDefinition parameter in iter.Parameters)
				if (parameter.Modifier == ParameterModifier.Thrown)
					return parameter.ResolvedType ?? parameter.Type?.ResolvedType;
			return null;
		}

		static string PointerTypeName(string type)
		{
			return type.EndsWith("*", StringComparison.Ordinal) ? type + "*" : type + "*";
		}

		static string ResolvedTypeForC(TypeReference? type, string? resolvedType = null)
		{
			if (IsValidResolvedType(resolvedType))
				return resolvedType!;
			return type switch
			{
				GenericParameterTypeReference generic => generic.Name,
				ClassTypeReference classType => IsValidResolvedType(classType.ResolvedType) ? classType.ResolvedType! : "#ERROR",
				ThisTypeReference thisType => IsValidResolvedType(thisType.ResolvedType) ? thisType.ResolvedType! : "#ERROR",
				NamedTypeReference named => IsValidResolvedType(named.ResolvedType) ? named.ResolvedType! : named.Name,
				TypeDefinitionReference definition => IsValidResolvedType(definition.ResolvedType) ? definition.ResolvedType! : definition.Name,
				PrimitiveTypeReference primitive => PrimitiveName(primitive.Type),
				ConstTypeReference constant => "const " + ResolvedTypeForC(constant.Type, constant.Type?.ResolvedType),
				ConstOfTypeReference constOf => "const " + ResolvedTypeForC(constOf.Type, constOf.Type?.ResolvedType),
				PointerTypeReference pointer => PointerTypeName(ResolvedTypeForC(pointer.ElementType, pointer.ElementType?.ResolvedType)),
				ArrayTypeReference array => ResolvedTypeForC(array.ElementType, array.ElementType?.ResolvedType) + "[]",
				OptionalTypeReference optional => ResolvedTypeForC(optional.ElementType, optional.ElementType?.ResolvedType) + "?",
				CallableTypeReference callable => IsValidResolvedType(callable.ResolvedType) ? callable.ResolvedType! : "#ERROR",
				IterTypeReference iter => IsValidResolvedType(iter.ResolvedType) ? iter.ResolvedType! : "#ERROR",
				_ => IsValidResolvedType(type?.ResolvedType) ? type!.ResolvedType! : "#ERROR"
			};
		}

		static bool IsValidResolvedType(string? type)
		{
			return !string.IsNullOrWhiteSpace(type) && type != "#UNRESOLVED" && type != "#ERROR";
		}

		static string PrimitiveName(PrimitiveType primitive)
		{
			return primitive switch
			{
				PrimitiveType.Void => "void",
				PrimitiveType.Bool => "bool",
				PrimitiveType.SByte => "sbyte",
				PrimitiveType.Byte => "byte",
				PrimitiveType.Short => "short",
				PrimitiveType.UShort => "ushort",
				PrimitiveType.Int => "int",
				PrimitiveType.UInt => "uint",
				PrimitiveType.Long => "long",
				PrimitiveType.ULong => "ulong",
				PrimitiveType.NInt => "nint",
				PrimitiveType.NUInt => "nuint",
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

		List<string> GetExpandedCallableParameterTypesForC(List<ParameterDefinition> parameters)
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
				if (TryGetCallableNewtypeStorageComponentsByPrimaryTypeForC(parameterType, out List<(string Name, string Type)> callableNewtypeComponents))
				{
					foreach ((_, string componentType) in callableNewtypeComponents)
						types.Add(componentType);
					continue;
				}
				if (TryParseExpandedCallableStorageType(parameterType, out string callableReturnType, out List<string> callableParameterTypes, out string? callableTargetSpec, out string? callableCallSpec))
				{
					string specs = "";
					if (!string.IsNullOrWhiteSpace(callableTargetSpec))
						specs += " " + callableTargetSpec;
					if (!string.IsNullOrWhiteSpace(callableCallSpec))
						specs += " " + callableCallSpec;
					types.Add("fn" + specs + " " + callableReturnType + "(" + string.Join(", ", ["void*", .. callableParameterTypes]) + ")");
					types.Add("void*");
					continue;
				}

				types.Add(parameter.Modifier switch
				{
					ParameterModifier.In => "in " + parameterType,
					ParameterModifier.Out => "out " + parameterType,
					ParameterModifier.Thrown => "thrown " + parameterType,
					ParameterModifier.Within => "within " + parameterType,
					ParameterModifier.Upon => "upon " + parameterType,
					_ => parameterType
				});
			}
			return types;
		}

		List<(string Type, string Name)> GetNamedCallableNewtypeParameterTypesForC(CallableTypeReference callable, List<ParameterDefinition> parameters)
		{
			List<(string Type, string Name)> types = [];
			if (callable.Kind is CallableKind.Delegate or CallableKind.Once or CallableKind.Async)
				types.Add((GetCallableContextType(parameters), "context"));
			foreach (ParameterDefinition parameter in parameters)
			{
				if (parameter is ThisParameterDefinition)
					continue;

				string name = CName(parameter);
				string parameterType = parameter.ResolvedType ?? parameter.Type?.ResolvedType ?? "";
				if (TryGetArrayElementOnly(parameterType, out string arrayElementType))
				{
					types.Add((arrayElementType + "*", name));
					types.Add(("nuint", name + "_length"));
					continue;
				}
				if (TryGetOptionalElementOnly(parameterType, out string optionalElementType))
				{
					types.Add((optionalElementType, name));
					types.Add(("bool", name + "_specified"));
					continue;
				}
				if (TryGetCallableNewtypeStorageComponentsByPrimaryTypeForC(parameterType, out List<(string Name, string Type)> callableNewtypeComponents))
				{
					for (int i = 0; i < callableNewtypeComponents.Count; i++)
					{
						string componentName = i == 0 ? name : name + "_" + callableNewtypeComponents[i].Name;
						types.Add((callableNewtypeComponents[i].Type, componentName));
					}
					continue;
				}
				if (TryParseExpandedCallableStorageType(parameterType, out string callableReturnType, out List<string> callableParameterTypes, out string? callableTargetSpec, out string? callableCallSpec))
				{
					string specs = "";
					if (!string.IsNullOrWhiteSpace(callableTargetSpec))
						specs += " " + callableTargetSpec;
					if (!string.IsNullOrWhiteSpace(callableCallSpec))
						specs += " " + callableCallSpec;
					types.Add(("fn" + specs + " " + callableReturnType + "(" + string.Join(", ", ["void*", .. callableParameterTypes]) + ")", name));
					types.Add(("void*", name + "_context"));
					continue;
				}

				types.Add((parameter.Modifier switch
				{
					ParameterModifier.In => "in " + parameterType,
					ParameterModifier.Out => "out " + parameterType,
					ParameterModifier.Thrown => "thrown " + parameterType,
					ParameterModifier.Within => "within " + parameterType,
					ParameterModifier.Upon => "upon " + parameterType,
					_ => parameterType
				}, name));
			}
			return types;
		}

		static string GetCallableContextType(List<ParameterDefinition> parameters)
		{
			return parameters.Count > 0
				&& parameters[0] is ThisParameterDefinition thisParameter
				&& IsConstThisParameter(thisParameter)
				? "const void*"
				: "void*";
		}

		static bool IsConstThisParameter(ThisParameterDefinition parameter)
		{
			if (parameter.Modifier == ParameterModifier.In)
				return true;
			if (parameter.SourceSyntax is ThisParameterSyntax { Declarators: not null } syntax)
				foreach (TypeDeclaratorSyntax declarator in syntax.Declarators)
					if (declarator.Keyword?.Value == "const")
						return true;
			return false;
		}

		string FormatCallableDeclarator(CallableTypeReference callable, string name)
		{
			string declarator = FormatFunctionPointerDeclarator(name, callable.TargetSpec ?? GetDefaultTargetTypeSpec(functionPointer: true), callable.CallSpec);
			string returnType = callable.ReturnType?.ResolvedType ?? ResolvedTypeForC(callable.ReturnType, callable.ReturnType?.ResolvedType);
			if (string.IsNullOrWhiteSpace(name))
			{
				List<string> anonymousParameterTypes = GetExpandedCallableParameterTypesForC(callable.Parameters);
				ExpandResolvedCallableReturnForC(ref returnType, anonymousParameterTypes);
				return FormatResolvedType(returnType, declarator).Declaration + "(" + FormatResolvedParameterList(anonymousParameterTypes) + ")";
			}
			List<(string Type, string Name)> parameterTypes = GetNamedCallableNewtypeParameterTypesForC(callable, callable.Parameters);
			ExpandResolvedCallableReturnForC(ref returnType, parameterTypes);
			return FormatResolvedType(returnType, declarator).Declaration + "(" + FormatResolvedNamedParameterList(parameterTypes) + ")";
		}

		CType FormatType(TypeReference? type, string declarator)
		{
			return type switch
			{
				null => new CType("void " + declarator),
				AttributedTypeReference attributed => FormatType(attributed.Type, declarator),
				ConstTypeReference constant => FormatQualifiedType("const", constant.Type, declarator),
				ConstOfTypeReference constOf => FormatQualifiedType("const", constOf.Type, declarator),
				VolatileTypeReference vol => FormatQualifiedType("volatile", vol.Type, declarator),
				EscapedTypeReference escaped => FormatType(escaped.Type, declarator),
				ScopedTypeReference scoped => FormatType(scoped.Type, declarator),
				UnscopedTypeReference unscoped => FormatType(unscoped.Type, declarator),
				TargetTypeSpecTypeReference targetSpec => FormatTargetSpecType(targetSpec, declarator),
				PointerTypeReference pointer => FormatPointerType(pointer, declarator),
				ArrayTypeReference array => FormatType(array.ElementType, FormatDataPointerDeclarator(declarator, explicitTargetSpec: null)),
				FixedArrayTypeReference fixedArray => FormatFixedArrayType(fixedArray, declarator),
				OptionalTypeReference optional => FormatType(optional.ElementType, declarator),
				RawFunctionPointerTypeReference => new CType(FormatInlineResolvedFunctionPointer("void", new List<string>(), declarator, GetDefaultTargetTypeSpec(functionPointer: true), null)),
				CallableTypeReference callable => new CType(FormatCallableDeclarator(callable, declarator)),
				PrimitiveTypeReference primitive => FormatPrimitiveType(primitive.Type, declarator),
				ClassTypeReference classType when ShouldFormatResolvedType(classType.ResolvedType) => FormatResolvedType(classType.ResolvedType!, declarator),
				ThisTypeReference thisType when ShouldFormatResolvedType(thisType.ResolvedType) => FormatResolvedType(thisType.ResolvedType!, declarator),
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
			if (type is AutoTypeReference && !string.IsNullOrWhiteSpace(resolvedType))
				return FormatResolvedType(resolvedType!, declarator);
			if (type is CallableTypeReference)
				return FormatType(type, declarator);
			if (ContainsFixedArrayTypeReference(type))
				return FormatType(type, declarator);
			if (resolvedType is not null
				&& TryParseResolvedCallableType(resolvedType, out _, out _)
				&& TypeReferenceName(type) is string nominalTypeName
				&& TryGetCallableNewtypeStorageComponentsForC(nominalTypeName, out List<(string Name, string Type)> nominalComponents)
				&& nominalComponents.Count > 0
				&& nominalComponents[0].Name == "call")
				return new CType(CNameFromTypeName(nominalTypeName) + " " + declarator);
			if (ShouldFormatResolvedType(resolvedType))
				return FormatResolvedType(resolvedType!, declarator);
			if (type is not null)
				return FormatType(type, declarator);
			if (resolvedType is not null)
				return FormatResolvedType(resolvedType, declarator);
			return FormatType(null, declarator);
		}

		static bool ContainsFixedArrayTypeReference(TypeReference? type)
		{
			return type switch
			{
				null => false,
				FixedArrayTypeReference => true,
				AttributedTypeReference attributed => ContainsFixedArrayTypeReference(attributed.Type),
				ConstTypeReference constant => ContainsFixedArrayTypeReference(constant.Type),
				ConstOfTypeReference constOf => ContainsFixedArrayTypeReference(constOf.Type),
				VolatileTypeReference vol => ContainsFixedArrayTypeReference(vol.Type),
				EscapedTypeReference escaped => ContainsFixedArrayTypeReference(escaped.Type),
				ScopedTypeReference scoped => ContainsFixedArrayTypeReference(scoped.Type),
				UnscopedTypeReference unscoped => ContainsFixedArrayTypeReference(unscoped.Type),
				TargetTypeSpecTypeReference targetSpec => ContainsFixedArrayTypeReference(targetSpec.Type),
				PointerTypeReference pointer => ContainsFixedArrayTypeReference(pointer.ElementType),
				ArrayTypeReference array => ContainsFixedArrayTypeReference(array.ElementType),
				OptionalTypeReference optional => ContainsFixedArrayTypeReference(optional.ElementType),
				GenericTypeReference generic => ContainsFixedArrayTypeReference(generic.Type),
				_ => false
			};
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

		CType FormatResolvedType(string resolvedType, string declarator, bool normalizeInterfacePointer = true)
		{
			string type = resolvedType.Trim();
			if (type == "fn*")
				return new CType(FormatInlineResolvedFunctionPointer("void", new List<string>(), declarator, GetDefaultTargetTypeSpec(functionPointer: true), null));
			if (type.StartsWith("fn* ", StringComparison.Ordinal))
				return new CType(FormatInlineResolvedFunctionPointer("void", new List<string>(), declarator, type["fn* ".Length..].Trim(), null));
			if (type.StartsWith("struct(", StringComparison.Ordinal) && type.EndsWith(")", StringComparison.Ordinal))
				return FormatStorageResolvedType(type[7..^1], declarator);
			if (TryParseResolvedCallableType(type, out string callableReturnType, out List<string> callableParameterTypes, out string? callableTargetSpec, out string? callableCallSpec))
				return new CType(FormatInlineResolvedFunctionPointer(callableReturnType, callableParameterTypes, declarator, callableTargetSpec, callableCallSpec));

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
				if (type.EndsWith(" escaped", StringComparison.Ordinal))
				{
					type = type[..^8].TrimEnd();
					continue;
				}
				if (type.EndsWith(" scoped", StringComparison.Ordinal))
				{
					type = type[..^7].TrimEnd();
					continue;
				}
				if (type.EndsWith(" unscoped", StringComparison.Ordinal))
				{
					type = type[..^9].TrimEnd();
					continue;
				}
				break;
			}

			string? explicitTargetSpec = TryStripTrailingResolvedTargetSpec(ref type);
			int pointerCount = 0;
			bool rawFunctionPointerBase = false;
			if (type.StartsWith("fn*", StringComparison.Ordinal)
				&& (type.Length == "fn*".Length || type["fn*".Length] is '*' or '['))
			{
				rawFunctionPointerBase = true;
				string suffix = type["fn*".Length..].Trim();
				type = "fn*";
				while (suffix.Length > 0)
				{
					if (suffix.EndsWith("[]", StringComparison.Ordinal))
					{
						pointerCount++;
						suffix = suffix[..^2].TrimEnd();
						continue;
					}
					if (suffix.EndsWith("*", StringComparison.Ordinal))
					{
						pointerCount++;
						suffix = suffix[..^1].TrimEnd();
						continue;
					}
					break;
				}
			}

			while (!rawFunctionPointerBase && type.EndsWith("*", StringComparison.Ordinal))
			{
				pointerCount++;
				type = type[..^1].TrimEnd();
			}

			if (!rawFunctionPointerBase && type.EndsWith("[]", StringComparison.Ordinal))
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
			if (normalizeInterfacePointer && pointerCount == 1 && IsInterfaceResolvedName(type))
				pointerCount++;

			bool isGenericType = currentGenericTypeNames.Contains(type) || genericParameterNames.Contains(type);
			if (isGenericType && !anyGenericParameterNames.Contains(type) && pointerCount > 0 && currentArrayElementComponentNames.Contains(declarator))
				pointerCount++;
			string pointerPart = pointerCount == 0 ? "" : new string('*', pointerCount);
			string targetSpec = pointerPart.Length == 0 ? "" : FormatTypeSpec(explicitTargetSpec ?? GetDefaultTargetTypeSpec(functionPointer: false));
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
			else if (explicitTargetSpec is not null)
			{
				string spec = FormatTypeSpec(explicitTargetSpec);
				if (spec.Length > 0)
					pointerDeclarator = string.IsNullOrWhiteSpace(pointerDeclarator) ? spec : pointerDeclarator + " " + spec;
			}
			if (type == "fn*")
				return new CType(FormatInlineResolvedFunctionPointer("void", new List<string>(), pointerDeclarator, explicitTargetSpec ?? GetDefaultTargetTypeSpec(functionPointer: true), null));
			if (type.StartsWith("fn* ", StringComparison.Ordinal))
				return new CType(FormatInlineResolvedFunctionPointer("void", new List<string>(), pointerDeclarator, type["fn* ".Length..].Trim(), null));
			if (TryParseResolvedCallableType(type, out string resolvedCallableReturnType, out List<string> resolvedCallableParameterTypes, out string? resolvedCallableTargetSpec, out string? resolvedCallableCallSpec))
				return new CType(FormatInlineResolvedFunctionPointer(resolvedCallableReturnType, resolvedCallableParameterTypes, pointerDeclarator, resolvedCallableTargetSpec, resolvedCallableCallSpec));
			if (TrySplitFixedArrayType(type, out string fixedBaseType, out List<long> fixedLengths))
				return FormatResolvedFixedArrayType(qualifierPart + fixedBaseType, fixedLengths, pointerDeclarator);
			if (pointerCount == 0 && TryParseExpandedCallableStorageType(type, out _, out _, out _, out _))
				return FormatStorageResolvedType(type, pointerDeclarator);
			string cType = isGenericType && pointerCount == 0 ? "void*" : FormatResolvedBaseType(type);
			return new CType(qualifierPart + cType + " " + pointerDeclarator);
		}

		string? TryStripTrailingResolvedTargetSpec(ref string type)
		{
			int space = type.LastIndexOf(' ');
			if (space < 0 || space == type.Length - 1)
				return null;
			string candidate = type[(space + 1)..].Trim();
			if (compilation.Target?.Capabilities.HasTypeSpec(candidate) != true)
				return null;
			type = type[..space].TrimEnd();
			return candidate;
		}

		bool IsInterfaceResolvedName(string type)
		{
			int generic = type.IndexOf('<', StringComparison.Ordinal);
			string name = generic < 0 ? type : type[..generic];
			return interfaceNames.Contains(name);
		}

		CType FormatResolvedFixedArrayType(string baseType, List<long> lengths, string declarator)
		{
			string arrayDeclarator = NeedsArrayDeclaratorParens(declarator) ? "(" + declarator + ")" : declarator;
			foreach (long length in lengths)
				arrayDeclarator += "[" + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
			return FormatResolvedType(baseType, arrayDeclarator);
		}

		static bool TrySplitFixedArrayType(string type, out string baseType, out List<long> lengths)
		{
			baseType = "";
			lengths = [];
			int index = 0;
			while (index < type.Length && type[index] != '[')
				index++;
			if (index <= 0)
				return false;
			baseType = type[..index].TrimEnd();
			while (index < type.Length)
			{
				if (type[index] != '[')
					return false;
				int end = type.IndexOf(']', index + 1);
				if (end < 0)
					return false;
				string lengthText = type[(index + 1)..end].Trim();
				if (!long.TryParse(lengthText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long length))
					return false;
				lengths.Add(length);
				index = end + 1;
				while (index < type.Length && char.IsWhiteSpace(type[index]))
					index++;
			}
			return baseType.Length > 0 && lengths.Count > 0;
		}

		string FormatResolvedBaseType(string type)
		{
			type = StripTypeDecorators(type);
			if (currentGenericTypeNames.Contains(type))
				return "void";
			if (genericParameterNames.Contains(type))
				return "void";

			return type switch
			{
				"void" => "void",
				"bool" => compilation.Target?.Capabilities.GetPrimitiveCSpelling("bool") ?? "bool",
				"string" => "const char",
				"wstring" => "const uint16_t",
				"astring" => "const char",
				"untyped" => "uintptr_t",
				"sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "nint" or "nuint" or "float" or "double" or "char" or "wchar" or "achar" or "uchar"
					=> compilation.Target?.Capabilities.GetPrimitiveCSpelling(type) ?? type,
				_ => CTypeName(type)
			};
		}

		static bool IsPrimitiveStringResolvedName(string type)
		{
			return type is "string" or "wstring" or "astring";
		}

		CType FormatQualifiedType(string qualifier, TypeReference? inner, string declarator)
		{
			if (inner is PointerTypeReference or ArrayTypeReference or OptionalTypeReference or GenericTypeReference or RawFunctionPointerTypeReference or CallableTypeReference or TargetTypeSpecTypeReference)
				return FormatType(inner, declarator + " " + qualifier);
			CType formatted = FormatType(inner, declarator);
			return new CType(qualifier + " " + formatted.Declaration);
		}

		CType FormatTargetSpecType(TargetTypeSpecTypeReference targetSpec, string declarator)
		{
			string cSpec = FormatTypeSpec(targetSpec.Specifier);
			if (cSpec.Length == 0)
				return FormatType(targetSpec.Type, declarator);
			if (targetSpec.Type is PointerTypeReference pointer)
				return FormatPointerType(pointer, declarator, targetSpec.Specifier);
			if (targetSpec.Type is ArrayTypeReference array)
				return FormatType(array.ElementType, FormatDataPointerDeclarator(declarator, targetSpec.Specifier));
			if (targetSpec.Type is RawFunctionPointerTypeReference)
				return new CType(FormatInlineResolvedFunctionPointer("void", new List<string>(), declarator, targetSpec.Specifier, null));
			return FormatType(targetSpec.Type, declarator + " " + cSpec);
		}

		CType FormatPointerType(PointerTypeReference pointer, string declarator, string? explicitTargetSpec = null)
		{
			if (StripTypeDecorators(pointer.ElementType) is PointerTypeReference innerPointer
				&& TryGetInterfaceTypeReferenceName(innerPointer.ElementType, out string pointerInterfaceName))
				return FormatResolvedType(pointerInterfaceName + "**", declarator, normalizeInterfacePointer: false);
			if (TryGetInterfaceTypeReferenceName(pointer.ElementType, out string interfaceName))
				return FormatResolvedType(interfaceName + "*", declarator);
			declarator = FormatDataPointerDeclarator(declarator, explicitTargetSpec);
			if (pointer.ElementType is PrimitiveTypeReference { Type: PrimitiveType.Untyped })
				return new CType("void " + declarator);
			return FormatType(pointer.ElementType, declarator);
		}

		bool TryGetInterfaceTypeReferenceName(TypeReference? type, out string interfaceName)
		{
			interfaceName = "";
			type = StripTypeDecorators(type);
			if (type is TypeDefinitionReference { Definition: InterfaceDefinition definition })
			{
				interfaceName = definition.Name;
				return true;
			}
			if (type is NamedTypeReference named && IsInterfaceResolvedName(named.ResolvedType ?? named.Name))
			{
				interfaceName = named.ResolvedType ?? named.Name;
				return true;
			}
			if (TypeReferenceName(type) is string typeName && IsInterfaceResolvedName(typeName))
			{
				interfaceName = InterfaceBaseName(typeName);
				return true;
			}
			return false;
		}

		static string InterfaceBaseName(string typeName)
		{
			int generic = typeName.IndexOf('<', StringComparison.Ordinal);
			return generic < 0 ? typeName : typeName[..generic];
		}

		string FormatDataPointerDeclarator(string declarator, string? explicitTargetSpec)
		{
			string targetSpec = FormatTypeSpec(explicitTargetSpec ?? GetDefaultTargetTypeSpec(functionPointer: false));
			if (targetSpec.Length > 0)
				return "* " + targetSpec + " " + declarator;
			return "*" + declarator;
		}

		CType FormatFixedArrayType(FixedArrayTypeReference fixedArray, string declarator)
		{
			string length = fixedArray.Length is long value
				? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
				: "0";
			string arrayDeclarator = NeedsArrayDeclaratorParens(declarator) ? "(" + declarator + ")" : declarator;
			return FormatType(fixedArray.ElementType, arrayDeclarator + "[" + length + "]");
		}

		static bool NeedsArrayDeclaratorParens(string declarator)
		{
			declarator = declarator.TrimStart();
			return declarator.StartsWith("*", StringComparison.Ordinal);
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
				return new CType("uintptr_t " + declarator);
			return new CType((compilation.Target?.Capabilities.GetPrimitiveCSpelling(name) ?? name) + " " + declarator);
		}

		CType FormatDataPointerPrimitive(string elementType, string declarator)
		{
			string targetSpec = FormatTypeSpec(GetDefaultTargetTypeSpec(functionPointer: false));
			string pointer = targetSpec.Length == 0 ? "* " : "* " + targetSpec + " ";
			return new CType(elementType + pointer + declarator);
		}

		string? GetDefaultTargetTypeSpec(bool functionPointer)
		{
			return compilation.Target is null
				? null
				: functionPointer
					? compilation.Target.Sections.DefaultFunctionPointerTypeSpec
					: compilation.Target.Sections.DefaultDataPointerTypeSpec;
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
			string type = StripTypeDecorators(resolvedType);
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

		bool TryParseResolvedCallableType(string resolvedType, out string returnType, out List<string> parameterTypes)
		{
			return TryParseResolvedCallableType(resolvedType, out returnType, out parameterTypes, out _, out _);
		}

		bool TryParseResolvedCallableType(string resolvedType, out string returnType, out List<string> parameterTypes, out string? targetSpec, out string? callSpec)
		{
			returnType = "";
			parameterTypes = [];
			targetSpec = null;
			callSpec = null;
			string type = resolvedType.Trim();
			if (!type.StartsWith("fn ", StringComparison.Ordinal))
				return false;
			int open = type.IndexOf('(', StringComparison.Ordinal);
			int close = type.LastIndexOf(')');
			if (open < 0 || close < open || close != type.Length - 1)
				return false;
			string prefix = type[3..open].Trim();
			if (prefix.Length == 0)
				return false;
			returnType = StripLeadingCallableSpecs(prefix, out targetSpec, out callSpec);
			if (returnType.Length == 0)
				return false;
			string parameterText = type[(open + 1)..close].Trim();
			if (parameterText.Length == 0)
				return true;
			foreach (string parameter in SplitTopLevel(parameterText, ','))
				parameterTypes.Add(parameter.Trim());
			parameterTypes = GetExpandedResolvedCallableParameterTypesForC(parameterTypes);
			return true;
		}

		List<string> GetExpandedResolvedCallableParameterTypesForC(List<string> parameterTypes)
		{
			List<string> expanded = [];
			foreach (string parameterType in parameterTypes)
			{
				string type = parameterType.Trim();
				if (TryGetArrayElementOnly(type, out string arrayElementType))
				{
					expanded.Add(arrayElementType + "*");
					expanded.Add("nuint");
					continue;
				}
				if (TryGetOptionalElementOnly(type, out string optionalElementType))
				{
					expanded.Add(optionalElementType);
					expanded.Add("bool");
					continue;
				}
				if (TryGetCallableNewtypeStorageComponentsByPrimaryTypeForC(type, out List<(string Name, string Type)> callableNewtypeComponents))
				{
					foreach ((_, string componentType) in callableNewtypeComponents)
						expanded.Add(componentType);
					continue;
				}
				if (TryParseExpandedCallableStorageType(type, out string callableReturnType, out List<string> callableParameterTypes, out string? callableTargetSpec, out string? callableCallSpec))
				{
					string specs = "";
					if (!string.IsNullOrWhiteSpace(callableTargetSpec))
						specs += " " + callableTargetSpec;
					if (!string.IsNullOrWhiteSpace(callableCallSpec))
						specs += " " + callableCallSpec;
					expanded.Add("fn" + specs + " " + callableReturnType + "(" + string.Join(", ", ["void*", .. callableParameterTypes]) + ")");
					expanded.Add("void*");
					continue;
				}
				expanded.Add(type);
			}
			return expanded;
		}

		string StripLeadingCallableSpecs(string prefix, out string? targetSpec, out string? callSpec)
		{
			targetSpec = null;
			callSpec = null;
			string text = prefix.Trim();
			while (true)
			{
				int space = text.IndexOf(' ', StringComparison.Ordinal);
				if (space <= 0)
					return text;

				string candidate = text[..space];
				if (!TryClassifyCallablePrefixSpec(candidate, out CallablePrefixSpecKind kind))
					return text;

				if (kind == CallablePrefixSpecKind.TargetSpec)
					targetSpec = candidate;
				else
					callSpec = candidate;
				text = text[(space + 1)..].TrimStart();
			}
		}

		bool TryClassifyCallablePrefixSpec(string candidate, out CallablePrefixSpecKind kind)
		{
			if (compilation.Target?.Capabilities.HasTypeSpec(candidate) == true)
			{
				kind = CallablePrefixSpecKind.TargetSpec;
				return true;
			}
			if (compilation.Target?.Capabilities.HasCallSpec(candidate) == true)
			{
				kind = CallablePrefixSpecKind.CallSpec;
				return true;
			}
			kind = CallablePrefixSpecKind.None;
			return false;
		}

		enum CallablePrefixSpecKind
		{
			None,
			TargetSpec,
			CallSpec
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
				"any" or "copyable" or "auto" or "#TARGET" => "void*",
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
			value = RemoveLifetimeDecorator(value, "escaped");
			value = RemoveLifetimeDecorator(value, "scoped");
			value = RemoveLifetimeDecorator(value, "unscoped");
			return value
				.Replace("const ", "", StringComparison.Ordinal)
				.Replace("volatile ", "", StringComparison.Ordinal)
				.Replace("in ", "", StringComparison.Ordinal)
				.Replace("*", "Ptr", StringComparison.Ordinal)
				.Replace("[]", "Array", StringComparison.Ordinal)
				.Replace("?", "Optional", StringComparison.Ordinal);
		}

		static string RemoveLifetimeDecorator(string value, string keyword)
		{
			if (value.StartsWith(keyword + " ", StringComparison.Ordinal))
				return value[(keyword.Length + 1)..];
			if (!value.StartsWith(keyword + "(", StringComparison.Ordinal))
				return value;

			int close = value.IndexOf(')', keyword.Length + 1);
			if (close < 0)
				return value;

			int start = close + 1;
			if (start < value.Length && value[start] == ' ')
				start++;
			return value[start..];
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
			string? CallSpec,
			bool ForwardsContext);

		sealed record DelegateThunkKey(
			SourceFile File,
			FunctionDefinition SourceFunction,
			string ReturnType,
			string ParameterTypes,
			string CallSpec,
			bool ForwardsContext);
	}
}
