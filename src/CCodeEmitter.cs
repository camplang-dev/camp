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
			EmitPrivateHeader(compilation, options, result);
			foreach (SourceFile file in compilation.Files)
			{
				if (file.IsApiHeader)
				{
					if (HasExportedCallableDefinitions(compilation, file))
						EmitPublicHeader(compilation, options, file, result);
					continue;
				}

				EmitSourceFile(compilation, options, file, result);
				if (HasExportedCallableDefinitions(compilation, file))
					EmitPublicHeader(compilation, options, file, result);
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
			if (node.ResolvedType is string resolvedType && InvalidResolvedTypes.Contains(resolvedType))
			{
				result.Diagnostics.Add($"C emission aborted because {node.GetType().Name} has unresolved type '{resolvedType}'.");
				return false;
			}
		}

		return true;
	}

	static void EmitPrivateHeader(Compilation compilation, CEmissionOptions options, CEmissionResult result)
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
			if (HasExportedCallableDefinitions(compilation, file))
				writer.WriteLine("#include \"" + GetHeaderFilename(file) + "\"");
		}

		writer.WriteLine();
		writer.WriteLine("/* Private declarations will be emitted in a later C emission stage. */");
		writer.WriteLine();
		writer.WriteLine("#endif");
		result.GeneratedFiles.Add(filename);
	}

	static void EmitSourceFile(Compilation compilation, CEmissionOptions options, SourceFile file, CEmissionResult result)
	{
		string filename = Path.Combine(options.OutputDirectory, GetCSourceFilename(file));
		using StreamWriter writer = new(filename, append: false, Utf8NoBom);
		writer.WriteLine("#include \"" + options.ProjectName + "_private.h\"");
		if (HasExportedCallableDefinitions(compilation, file))
			writer.WriteLine("#include \"" + GetHeaderFilename(file) + "\"");
		writer.WriteLine();
		writer.WriteLine("/* Function and object definitions will be emitted in a later C emission stage. */");
		result.GeneratedFiles.Add(filename);
	}

	static void EmitPublicHeader(Compilation compilation, CEmissionOptions options, SourceFile file, CEmissionResult result)
	{
		string filename = Path.Combine(options.OutputDirectory, GetHeaderFilename(file));
		using StreamWriter writer = new(filename, append: false, Utf8NoBom);
		string guard = BuildHeaderGuard(Path.GetFileNameWithoutExtension(GetHeaderFilename(file)) + "_h");

		writer.WriteLine("#ifndef " + guard);
		writer.WriteLine("#define " + guard);
		writer.WriteLine();
		if (!file.IsApiHeader)
			writer.WriteLine("#include \"" + options.ProjectName + "_private.h\"");
		writer.WriteLine();
		writer.WriteLine("/* Exported callable declarations will be emitted in a later C emission stage. */");
		writer.WriteLine();
		writer.WriteLine("#endif");
		result.GeneratedFiles.Add(filename);
	}

	static bool HasExportedCallableDefinitions(Compilation compilation, SourceFile file)
	{
		foreach (Definition definition in compilation.SharedModule?.Definitions ?? [])
		{
			if (!compilation.DefinitionOwners.TryGetValue(definition, out SourceFile? owner) || !ReferenceEquals(owner, file))
				continue;
			if (definition is FunctionDefinition { Export: not null })
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
}
