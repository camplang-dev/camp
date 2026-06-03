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
			CDeclarationWriter declarations = new(compilation, result);
			EmitPrivateHeader(compilation, options, result, declarations);
			foreach (SourceFile file in compilation.Files)
			{
				if (file.IsApiHeader)
					continue;

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
		declarations.WriteSourceFileDefinitions(writer, file);
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

	sealed class CDeclarationWriter(Compilation compilation, CEmissionResult result)
	{
		readonly HashSet<string> emittedNames = new(StringComparer.Ordinal);
		readonly Dictionary<FunctionDefinition, TypeDefinition> containingTypes = BuildContainingTypeMap(compilation);

		public void WritePrivateHeaderDeclarations(TextWriter writer)
		{
			emittedNames.Clear();
			List<Definition> definitions = GetProjectDefinitions().ToList();

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
				WriteFunctionPrototype(writer, function, storage: function.Extern is not null ? null : "static");
			foreach (VariableDefinition variable in privateVariables)
				WriteVariableDeclaration(writer, variable, storage: variable.Extern is not null ? "extern" : "static");
		}

		public void WriteSourceFileDefinitions(TextWriter writer, SourceFile file)
		{
			emittedNames.Clear();
			List<Definition> definitions = GetOwnedDefinitions(file).ToList();
			bool wrote = false;

			foreach (VariableDefinition variable in definitions.OfType<VariableDefinition>())
			{
				if (variable.Extern is not null)
					continue;
				WriteVariableDefinition(writer, variable, storage: variable.Export is null ? "static" : null);
				wrote = true;
			}

			foreach (FunctionDefinition function in GetAllFunctions(definitions))
			{
				if (function.Extern is not null || function.Body is null)
					continue;
				if (function.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor)
					continue;
				WriteFunctionDefinition(writer, function, storage: function.Export is null ? "static" : null);
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
				writer.WriteLine("\t" + FormatTypeOrResolved(field.Type, field.ResolvedType, CName(field)).Declaration + ";");
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
			writer.WriteLine(prefix + FormatTypeOrResolved(function.ReturnType, function.ResolvedType, callSpec + name).Declaration + FormatParameters(function) + ";");
		}

		void WriteVariableDeclaration(TextWriter writer, VariableDefinition variable, string? storage)
		{
			string prefix = string.IsNullOrWhiteSpace(storage) ? "" : storage + " ";
			writer.WriteLine(prefix + FormatTypeOrResolved(variable.Type, variable.ResolvedType, CName(variable)).Declaration + ";");
		}

		void WriteVariableDefinition(TextWriter writer, VariableDefinition variable, string? storage)
		{
			string prefix = string.IsNullOrWhiteSpace(storage) ? "" : storage + " ";
			writer.Write(prefix + FormatTypeOrResolved(variable.Type, variable.ResolvedType, CName(variable)).Declaration);
			if (variable.InitialValue is not null)
				writer.Write(" = " + FormatExpression(variable.InitialValue));
			writer.WriteLine(";");
		}

		void WriteFunctionDefinition(TextWriter writer, FunctionDefinition function, string? storage)
		{
			string prefix = string.IsNullOrWhiteSpace(storage) ? "" : storage + " ";
			string callSpec = FormatCallSpec(function.CallSpec);
			if (callSpec.Length > 0)
				callSpec += " ";
			writer.WriteLine(prefix + FormatTypeOrResolved(function.ReturnType, function.ResolvedType, callSpec + CName(function)).Declaration + FormatParameters(function));
			WriteBlock(writer, function.Body!, indent: 0, forceBraces: true);
			writer.WriteLine();
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
					writer.WriteLine(SanitizeIdentifier(label.Name ?? "label") + ":");
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
			string type = FormatTypeOrResolved(declaration.Target.Type, declaration.Target.ResolvedType, name).Declaration;
			WriteIndent(writer, indent);
			writer.Write(type);
			if (declaration.InitialValue is not null)
				writer.Write(" = " + FormatExpression(declaration.InitialValue));
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
				TypeReferenceExpression => UnsupportedExpression(expression),
				ThisExpression => "this",
				DefaultExpression => "0",
				ParenthesizedExpression parenthesized => "(" + FormatExpression(parenthesized.Expression) + ")",
				CastExpression cast => "(" + FormatType(cast.Type, "").Declaration.Trim() + ")(" + FormatExpression(cast.Expression) + ")",
				SizeOfExpression sizeOf => "sizeof(" + FormatType(sizeOf.Type, "").Declaration.Trim() + ")",
				CallExpression call => FormatCallExpression(call),
				IndexExpression index => FormatExpression(index.Target) + "[" + string.Join(", ", index.Arguments.Select(FormatArgumentValue)) + "]",
				MemberExpression member => FormatExpression(member.Target) + "." + SanitizeIdentifier(member.Name),
				MemberReferenceExpression member => FormatMemberReference(member),
				UnaryExpression unary => FormatUnaryExpression(unary),
				PostfixUpdateExpression postfix => FormatExpression(postfix.Expression) + FormatUpdateOperator(postfix.Operator),
				BinaryExpression binary => "(" + FormatExpression(binary.Left) + " " + FormatBinaryOperator(binary.Operator) + " " + FormatExpression(binary.Right) + ")",
				AssignmentExpression assignment => FormatExpression(assignment.Target) + " " + FormatAssignmentOperator(assignment.Operator) + " " + FormatExpression(assignment.Value),
				ConditionalExpression conditional => "(" + FormatExpression(conditional.Condition) + " ? " + FormatExpression(conditional.WhenTrue) + " : " + FormatExpression(conditional.WhenFalse) + ")",
				InitializerExpression initializer => FormatInitializer(initializer),
				RangeExpression => UnsupportedExpression(expression),
				GroupedExpression => UnsupportedExpression(expression),
				ArrayExpression => UnsupportedExpression(expression),
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

		string FormatCallExpression(CallExpression call)
		{
			string target = FormatExpression(call.Target);
			List<string> arguments = [];
			foreach (ArgumentExpression argument in call.Arguments)
				arguments.Add(FormatArgumentValue(argument));
			return target + "(" + string.Join(", ", arguments) + ")";
		}

		string FormatArgumentValue(ArgumentExpression argument)
		{
			string value = FormatExpression(argument.Value);
			return argument.Modifier switch
			{
				ArgumentModifier.Out or ArgumentModifier.Catch => "&" + value,
				_ => value
			};
		}

		string FormatMemberReference(MemberReferenceExpression member)
		{
			if (member.Member is FunctionDefinition function)
				return CName(function);
			string separator = IsPointerLike(member.Target?.ResolvedType) ? "->" : ".";
			return FormatExpression(member.Target) + separator + SanitizeIdentifier(member.Name);
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
				ParameterDefinition parameter => CName(parameter),
				FieldDefinition field => CName(field),
				DeclarationTarget target => CName(target),
				_ => UnsupportedExpression(variable)
			};
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
			return "{ " + string.Join(", ", items) + " }";
		}

		static string? FormatInitializerTarget(InitializerTarget? target)
		{
			if (target is null || target.Parts.Count == 0)
				return null;
			return string.Join(".", target.Parts.Select(static part => SanitizeIdentifier(part.Name ?? "")));
		}

		string FormatUnaryExpression(UnaryExpression unary)
		{
			string operand = FormatExpression(unary.Operand);
			return unary.Operator switch
			{
				UnaryOperator.Plus => "+" + operand,
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
			return literal.Kind switch
			{
				LiteralKind.Number => literal.Text,
				LiteralKind.String => literal.Text,
				LiteralKind.True => "true",
				LiteralKind.False => "false",
				LiteralKind.Null => "NULL",
				_ => "0"
			};
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
			foreach (ParameterDefinition parameter in parameters)
			{
				if (parameter is WithinParameterDefinition && parameter.Type is null)
					continue;
				string name = CName(parameter);
				if (parameter.Modifier is ParameterModifier.Out or ParameterModifier.Thrown)
				{
					TypeReference? parameterType = parameter.Type;
					parts.Add(FormatOutParameterType(parameterType, parameter.ResolvedType, name).Declaration);
				}
				else
				{
					parts.Add(FormatTypeOrResolved(parameter.Type, parameter.ResolvedType, name).Declaration);
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
					parts.Add(FormatOutParameterType(parameterType, parameter.ResolvedType, name).Declaration);
				}
				else
				{
					parts.Add(FormatTypeOrResolved(parameter.Type, parameter.ResolvedType, name).Declaration);
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

		CType FormatTypeOrResolved(TypeReference? type, string? resolvedType, string declarator)
		{
			if (type is not null)
				return FormatType(type, declarator);
			if (!string.IsNullOrWhiteSpace(resolvedType))
				return FormatResolvedType(resolvedType, declarator);
			return FormatType(null, declarator);
		}

		CType FormatOutParameterType(TypeReference? type, string? resolvedType, string declarator)
		{
			if (type is not null)
				return FormatType(new PointerTypeReference { ElementType = type }, declarator);
			if (!string.IsNullOrWhiteSpace(resolvedType))
				return FormatResolvedType(resolvedType + "*", declarator);
			return FormatType(new PointerTypeReference { ElementType = null }, declarator);
		}

		CType FormatResolvedType(string resolvedType, string declarator)
		{
			string type = resolvedType.Trim();
			List<string> qualifiers = [];
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

			string cType = FormatResolvedBaseType(type);
			string pointerPart = pointerCount == 0 ? "" : new string('*', pointerCount);
			string qualifierPart = qualifiers.Count == 0 ? "" : string.Join(" ", qualifiers) + " ";
			return new CType(qualifierPart + cType + pointerPart + " " + declarator);
		}

		string FormatResolvedBaseType(string type)
		{
			return type switch
			{
				"void" => "void",
				"bool" => compilation.Target?.GetPrimitiveCSpelling("bool") ?? "bool",
				"string" => "char*",
				"wstring" => "uint16_t*",
				"astring" => "char*",
				"untyped" => "void",
				"sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "nint" or "nuint" or "float" or "double" or "char" or "wchar" or "achar" or "uchar"
					=> compilation.Target?.GetPrimitiveCSpelling(type) ?? type,
				_ => CTypeName(type)
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
	}
}
