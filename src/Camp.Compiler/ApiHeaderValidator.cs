using System.Collections.Generic;

namespace Camp.Compiler;

static class ApiHeaderValidator
{
	public static IReadOnlyList<BindDiagnostic> Validate(SourceFile file)
	{
		if (!file.IsApiHeader || file.BindableTree is not Module module)
			return [];

		List<BindDiagnostic> diagnostics = [];
		foreach (Definition definition in module.Definitions)
			ValidateDefinition(file, definition, diagnostics, containingType: null);
		return diagnostics;
	}

	static void ValidateDefinition(SourceFile file, Definition definition, List<BindDiagnostic> diagnostics, TypeDefinition? containingType)
	{
		switch (definition)
		{
			case FunctionDefinition function:
				ValidateFunction(file, function, diagnostics);
				break;

			case VariableDefinition variable when containingType is null:
				ValidateGlobalVariable(file, variable, diagnostics);
				break;

			case FieldDefinition field:
				ValidateField(file, field, diagnostics, containingType);
				break;

			case ClassDefinition classDefinition:
				ValidateClass(file, classDefinition, diagnostics);
				ValidateFields(file, classDefinition.Fields, diagnostics, classDefinition);
				ValidateFunctions(file, classDefinition.Functions, diagnostics);
				break;

			case StructDefinition structDefinition:
				ValidateFields(file, structDefinition.Fields, diagnostics, structDefinition);
				ValidateFunctions(file, structDefinition.Functions, diagnostics);
				break;

			case InterfaceDefinition interfaceDefinition:
				ValidateFunctions(file, interfaceDefinition.Functions, diagnostics);
				break;

			case EnumDefinition enumDefinition:
				ValidateFunctions(file, enumDefinition.Functions, diagnostics);
				break;

			case NewtypeDefinition newtypeDefinition:
				ValidateFields(file, newtypeDefinition.Fields, diagnostics, newtypeDefinition);
				ValidateFunctions(file, newtypeDefinition.Functions, diagnostics);
				break;

			case ParamsDefinition paramsDefinition:
				ValidateFunctions(file, paramsDefinition.Functions, diagnostics);
				break;
		}
	}

	static void ValidateClass(SourceFile file, ClassDefinition definition, List<BindDiagnostic> diagnostics)
	{
		if (file.IsGeneratedApiHeader || definition.Extern is not null || definition.Modifier == ClassModifier.Abstract)
			return;

		Report(
			diagnostics,
			definition.SourceSyntax,
			$"Class '{definition.Name}' in API file '{file.Path}' has a concrete implementation shape; API files may only declare API surfaces.");
	}

	static void ValidateFunction(SourceFile file, FunctionDefinition definition, List<BindDiagnostic> diagnostics)
	{
		if (definition.Body is null || definition.Extern is not null)
			return;

		Report(
			diagnostics,
			definition.SourceSyntax,
			$"Function '{definition.Name}' in API file '{file.Path}' has a body; API files may only declare API surfaces.");
	}

	static void ValidateGlobalVariable(SourceFile file, VariableDefinition definition, List<BindDiagnostic> diagnostics)
	{
		if (file.IsGeneratedApiHeader || definition.Extern is not null || definition.IsInline)
			return;

		Report(
			diagnostics,
			definition.SourceSyntax,
			$"Variable '{definition.Name}' in API file '{file.Path}' requires storage; mark it extern, inline, or move it to a source file.");
	}

	static void ValidateField(SourceFile file, FieldDefinition definition, List<BindDiagnostic> diagnostics, TypeDefinition? containingType)
	{
		if (definition.Modifier != FieldModifier.Static)
			return;
		if (file.IsGeneratedApiHeader || definition.Extern is not null || definition.IsInline)
			return;

		string name = containingType is null ? definition.Name : containingType.Name + "." + definition.Name;
		Report(
			diagnostics,
			definition.SourceSyntax,
			$"Static field '{name}' in API file '{file.Path}' requires storage; mark it extern, inline, or move it to a source file.");
	}

	static void ValidateFields(SourceFile file, IEnumerable<FieldDefinition> fields, List<BindDiagnostic> diagnostics, TypeDefinition containingType)
	{
		foreach (FieldDefinition field in fields)
			ValidateField(file, field, diagnostics, containingType);
	}

	static void ValidateFunctions(SourceFile file, IEnumerable<FunctionDefinition> functions, List<BindDiagnostic> diagnostics)
	{
		foreach (FunctionDefinition function in functions)
			ValidateFunction(file, function, diagnostics);
	}

	static void Report(List<BindDiagnostic> diagnostics, SyntaxNode? syntax, string message)
	{
		diagnostics.Add(new BindDiagnostic(GetRange(syntax), message));
	}

	static TokenRange? GetRange(SyntaxNode? syntax)
	{
		return syntax is not null && SyntaxNodeTraversal.TryGetRange(syntax, out TokenRange range) ? range : null;
	}
}
