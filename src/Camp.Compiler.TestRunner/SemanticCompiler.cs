using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Camp.Compiler;
using Xunit;

namespace Camp.Compiler.Tests;

public sealed class SemanticCompilation
{
	public required Compilation Compilation { get; init; }
	public required Module Module { get; init; }
	public required IReadOnlyList<string> Diagnostics { get; init; }
}

public static class SemanticCompiler
{
	public static SemanticCompilation CompileDeclarations(string source)
	{
		return Compile(source, CompilationPipeline.ExpandDeclarations);
	}

	public static SemanticCompilation CompileLowered(string source)
	{
		return Compile(source, CompilationPipeline.Lower);
	}

	public static FunctionDefinition Function(SemanticCompilation compilation, string name)
	{
		return compilation.Module.Definitions
			.OfType<FunctionDefinition>()
			.FirstOrDefault(function => function.Name == name || function.Symbol == name)
			?? throw new InvalidOperationException($"Function '{name}' was not found.");
	}

	public static TypeDefinition Type(SemanticCompilation compilation, string name)
	{
		return compilation.Module.Definitions
			.OfType<TypeDefinition>()
			.FirstOrDefault(type => type.Name == name || type.Symbol == name)
			?? throw new InvalidOperationException($"Type '{name}' was not found.");
	}

	public static FunctionDefinition Method(TypeDefinition type, string name)
	{
		return type switch
		{
			ClassDefinition c => c.Functions.FirstOrDefault(function => function.Name == name || function.Symbol == name),
			StructDefinition s => s.Functions.FirstOrDefault(function => function.Name == name || function.Symbol == name),
			InterfaceDefinition i => i.Functions.FirstOrDefault(function => function.Name == name || function.Symbol == name),
			NewtypeDefinition n => n.Functions.FirstOrDefault(function => function.Name == name || function.Symbol == name),
			EnumDefinition e => e.Functions.FirstOrDefault(function => function.Name == name || function.Symbol == name),
			_ => null
		} ?? throw new InvalidOperationException($"Method '{name}' was not found on type '{type.Name}'.");
	}

	public static IReadOnlyList<T> Descendants<T>(BindableNode root)
		where T : BindableNode
	{
		List<T> nodes = [];
		Visit(root, nodes, new HashSet<BindableNode>(ReferenceEqualityComparer.Instance));
		return nodes;
	}

	public static void AssertNoDiagnostics(SemanticCompilation compilation)
	{
		Assert.True(compilation.Diagnostics.Count == 0, string.Join(Environment.NewLine, compilation.Diagnostics));
	}

	static SemanticCompilation Compile(string source, Func<Compilation, bool> phase)
	{
		string repositoryRoot = FindRepositoryRoot();
		Compilation compilation = new()
		{
			Target = LoadTarget(repositoryRoot),
			MemoryModelName = null
		};
		compilation.Files.Add(new SourceFile
		{
			Path = "semantic_test.camp",
			Text = source
		});

		phase(compilation);
		Module module = compilation.SharedModule ?? compilation.Files.First().BindableTree ?? new Module();
		return new SemanticCompilation
		{
			Compilation = compilation,
			Module = module,
			Diagnostics = CollectDiagnostics(compilation)
		};
	}

	static IReadOnlyList<string> CollectDiagnostics(Compilation compilation)
	{
		List<string> diagnostics = [];
		foreach (SourceFile file in compilation.Files)
		{
			diagnostics.AddRange(file.ParseDiagnostics.Select(static diagnostic => diagnostic.ToString()));
			diagnostics.AddRange(file.BindDiagnostics.Select(static diagnostic => diagnostic.ToString()));
		}
		if (compilation.DeclarationExpansion is not null)
			diagnostics.AddRange(compilation.DeclarationExpansion.Diagnostics.Select(static diagnostic => diagnostic.ToString()));
		if (compilation.Lowering is not null)
			diagnostics.AddRange(compilation.Lowering.Diagnostics.Select(static diagnostic => diagnostic.ToString()));
		return diagnostics;
	}

	static TargetDefinition LoadTarget(string repositoryRoot)
	{
		string targetsDirectory = Path.Combine(repositoryRoot, "targets");
		if (!TargetCatalog.TryLoad(targetsDirectory, out TargetCatalog? catalog, out string? error))
			throw new InvalidOperationException(error ?? "Target catalog could not be loaded.");
		if (!catalog!.TryGetTarget("clang-macos-x64", out TargetDefinition? target))
			throw new InvalidOperationException("Target 'clang-macos-x64' could not be loaded.");
		return target!;
	}

	static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "src", "camplang.sln")))
				return directory.FullName;
			directory = directory.Parent;
		}
		throw new InvalidOperationException("Could not find repository root containing src/camplang.sln.");
	}

	static void Visit<T>(BindableNode node, List<T> results, HashSet<BindableNode> visited)
		where T : BindableNode
	{
		if (!visited.Add(node))
			return;
		if (node is T typed)
			results.Add(typed);

		foreach (BindableNode child in GetChildren(node))
			Visit(child, results, visited);
	}

	static IEnumerable<BindableNode> GetChildren(BindableNode node)
	{
		foreach (System.Reflection.PropertyInfo property in node.GetType().GetProperties())
		{
			if (property.GetIndexParameters().Length != 0 || property.Name is nameof(BindableNode.SourceSyntax))
				continue;
			object? value = property.GetValue(node);
			if (value is BindableNode child)
			{
				yield return child;
				continue;
			}
			if (value is string or null)
				continue;
			if (value is IEnumerable enumerable)
			{
				foreach (object? item in enumerable)
					if (item is BindableNode enumerableChild)
						yield return enumerableChild;
			}
		}
	}
}
