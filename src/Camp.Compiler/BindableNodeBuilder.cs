using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed record BindDiagnostic(TokenRange? Range, string Message);

public sealed class BindResult(Module Module, IReadOnlyList<BindDiagnostic> Diagnostics)
{
	public Module Module { get; } = Module;
	public IReadOnlyList<BindDiagnostic> Diagnostics { get; } = Diagnostics;
	public bool Success => Diagnostics.Count == 0;
}

public sealed partial class BindableNodeBuilder
{
	readonly List<BindDiagnostic> diagnostics = [];

	BindableNodeBuilder()
	{
	}

	public static BindResult Build(CompilationUnitSyntax syntax)
	{
		ArgumentNullException.ThrowIfNull(syntax);

		BindableNodeBuilder builder = new();
		Module module = builder.BuildModule(syntax);
		return new BindResult(module, builder.diagnostics);
	}

	public static Module Build(CompilationUnitSyntax syntax, out IReadOnlyList<BindDiagnostic> diagnostics)
	{
		BindResult result = Build(syntax);
		diagnostics = result.Diagnostics;
		return result.Module;
	}

	Module BuildModule(CompilationUnitSyntax syntax)
	{
		Module module = new() { SourceSyntax = syntax };

		foreach (CompilationUnitItemSyntax item in syntax.Items ?? [])
		{
			if (item.ImportExportDeclaration is not null)
				BuildImportExportDeclaration(module, item.ImportExportDeclaration);
			else if (item.AliasDeclaration is not null)
				AddAliasDeclaration(module, item.AliasDeclaration);
			else if (item.Declaration is not null)
				AddGlobalDeclaration(module, item.Declaration);
			else
				Report(item, "Compilation unit item does not contain a declaration.");
		}

		return module;
	}

	void BuildImportExportDeclaration(Module module, ImportExportDeclarationSyntax syntax)
	{
		switch (syntax)
		{
			case UsingImportExportDeclarationSyntax usingSyntax:
				module.Usings.Add(BuildUsingDeclaration(usingSyntax));
				break;

			case ExportImportExportDeclarationSyntax exportSyntax:
				if (exportSyntax.QualifiedNamespace is null)
					Report(exportSyntax, "Export namespace declaration is missing a namespace.");
				else
					module.ExportAs = BuildQualifiedName(exportSyntax.QualifiedNamespace);
				break;

			default:
				Report(syntax, "Unsupported import or export declaration.");
				break;
		}
	}

	UsingDeclaration BuildUsingDeclaration(UsingImportExportDeclarationSyntax syntax)
	{
		UsingDeclaration declaration = new()
		{
			SourceSyntax = syntax,
			Name = syntax.QualifiedNamespace is null ? null : BuildQualifiedName(syntax.QualifiedNamespace),
			Alias = syntax.Alias?.Value
		};

		if (syntax.QualifiedNamespace is null)
			Report(syntax, "Using declaration is missing a namespace.");

		foreach (Token identifier in syntax.SelectedIdentifiers?.Identifiers ?? [])
			declaration.SelectedNames.Add(identifier.Value);

		return declaration;
	}

	void AddAliasDeclaration(Module module, AliasDeclarationSyntax syntax)
	{
		AliasDefinition definition = new()
		{
			SourceSyntax = syntax,
			Name = syntax.Identifier?.Value ?? "",
			Symbol = syntax.Identifier?.Value ?? "",
			TargetName = syntax.TargetName?.Identifier?.Value ?? ""
		};

		foreach (MemberDeclaratorSyntax declarator in syntax.Declarators ?? [])
		{
			switch (declarator.Keyword?.Value)
			{
				case "export":
					SetVisibility(definition, declarator, "export");
					break;
				case "public":
					SetVisibility(definition, declarator, "public");
					break;
				default:
					Report(declarator, $"'{declarator.Keyword?.Value}' is not a valid alias declarator.");
					break;
			}
		}

		if (syntax.TargetName is null)
			Report(syntax, "Alias target is missing a name.");
		else
		{
			foreach (QualifierSyntax qualifier in syntax.TargetName.Qualifiers ?? [])
			{
				if (qualifier.Identifier is null)
					Report(qualifier, "Alias target qualifier is missing an identifier.");
				else
					definition.TargetQualifiers.Add(qualifier.Identifier.Value.Value);
			}
		}

		module.Definitions.Add(definition);
	}

	void AddGlobalDeclaration(Module module, DeclarationSyntax syntax)
	{
		if (syntax.TypeDeclaration is not null)
		{
			if (BuildTypeDefinition(syntax.TypeDeclaration) is Definition definition)
				module.Definitions.Add(definition);
		}
		else if (syntax.MemberDeclaration is not null)
		{
			if (IsMethodDeclaration(syntax.MemberDeclaration))
			{
					if (BuildFunctionDefinition(syntax.MemberDeclaration, isGlobal: true, allowVirtual: false) is FunctionDefinition function)
					module.Definitions.Add(function);
			}
			else if (BuildVariableDefinition(syntax.MemberDeclaration, isGlobal: true) is VariableDefinition variable)
			{
				module.Definitions.Add(variable);
			}
		}
		else
		{
			Report(syntax, "Declaration does not contain a type or member declaration.");
		}
	}

	Definition? BuildTypeDefinition(TypeDeclarationSyntax syntax)
	{
		switch (syntax.Keyword?.Value)
		{
			case "struct":
				return BuildStructDefinition(syntax);

			case "class":
				return BuildClassDefinition(syntax);

			case "interface":
				return BuildInterfaceDefinition(syntax);

			case "enum":
				return BuildEnumDefinition(syntax);

			case "newtype":
				return BuildNewtypeDefinition(syntax);

			case "params":
				Report(syntax.Keyword?.Range, "User-defined params declarations are no longer supported; use arrays, optionals, delegates, or struct(T) materialization.");
				return BuildParamsDefinition(syntax);

			default:
				Report(syntax.Keyword?.Range, $"'{syntax.Keyword?.Value}' type declarations are not supported by this binder pass.");
				return null;
		}
	}

	StructDefinition BuildStructDefinition(TypeDeclarationSyntax syntax)
	{
		StructDefinition definition = new()
		{
			SourceSyntax = syntax,
			Name = GetRequiredIdentifier(syntax.Identifier, syntax, "Struct declaration is missing a name."),
			Symbol = syntax.Identifier?.Value ?? ""
		};

		ApplyDefinitionAttributes(definition, syntax.Attributes);
		ApplyStructDeclarators(definition, syntax.Declarators);

		if (syntax.Type is not null)
			Report(syntax.Type, "Struct declarations may not have a leading type.");

		AddGenericParameters(definition.GenericParameters, syntax.GenericParameterList);
		AddTypeList(definition.BaseTypes, syntax.UnderlyingTypeList);

		if (syntax.ParameterList is not null)
			Report(syntax.ParameterList, "Struct declarations may not have a parameter list.");

		if (syntax.Scope is null)
		{
			if (syntax.SemicolonToken is null)
				Report(syntax, "Struct declaration is missing a body or semicolon.");

			return definition;
		}

		if (syntax.Scope.EnumValueList is not null)
			Report(syntax.Scope.EnumValueList, "Struct bodies may not contain enum values.");

		foreach (DeclarationSyntax child in syntax.Scope.Declarations ?? [])
		{
			if (child.MemberDeclaration is not null)
			{
				if (IsMethodDeclaration(child.MemberDeclaration))
				{
					if (BuildFunctionDefinition(child.MemberDeclaration, isGlobal: false, allowVirtual: false, containingTypeName: definition.Name) is FunctionDefinition function)
						definition.Functions.Add(function);
				}
				else if (BuildFieldDefinition(child.MemberDeclaration) is FieldDefinition field)
				{
					definition.Fields.Add(field);
				}
			}
			else if (child.TypeDeclaration is not null)
			{
				Report(child.TypeDeclaration, "Nested type declarations are not supported in structs by this binder pass.");
			}
			else
			{
				Report(child, "Struct member declaration is empty.");
			}
		}

		return definition;
	}

	ClassDefinition BuildClassDefinition(TypeDeclarationSyntax syntax)
	{
		ClassDefinition definition = new()
		{
			SourceSyntax = syntax,
			Name = GetRequiredIdentifier(syntax.Identifier, syntax, "Class declaration is missing a name."),
			Symbol = syntax.Identifier?.Value ?? ""
		};

		ApplyDefinitionAttributes(definition, syntax.Attributes);
		ApplyClassDeclarators(definition, syntax.Declarators);

		if (syntax.Type is not null)
			Report(syntax.Type, "Class declarations may not have a leading type.");

		AddGenericParameters(definition.GenericParameters, syntax.GenericParameterList);
		AddTypeList(definition.BaseTypes, syntax.UnderlyingTypeList);

		if (syntax.ParameterList is not null)
			Report(syntax.ParameterList, "Class declarations may not have a parameter list.");

		if (syntax.Scope is null)
		{
			if (syntax.SemicolonToken is null)
				Report(syntax, "Class declaration is missing a body or semicolon.");

			return definition;
		}

		if (syntax.Scope.EnumValueList is not null)
			Report(syntax.Scope.EnumValueList, "Class bodies may not contain enum values.");

		foreach (DeclarationSyntax child in syntax.Scope.Declarations ?? [])
		{
			if (child.MemberDeclaration is not null)
			{
				if (IsMethodDeclaration(child.MemberDeclaration))
				{
					if (BuildFunctionDefinition(child.MemberDeclaration, isGlobal: false, allowVirtual: true, containingTypeName: definition.Name) is FunctionDefinition function)
						definition.Functions.Add(function);
				}
				else if (BuildFieldDefinition(child.MemberDeclaration) is FieldDefinition field)
				{
					definition.Fields.Add(field);
				}
			}
			else if (child.TypeDeclaration is not null)
			{
				Report(child.TypeDeclaration, "Nested type declarations are not supported in classes by this binder pass.");
			}
			else
			{
				Report(child, "Class member declaration is empty.");
			}
		}

		return definition;
	}

	InterfaceDefinition BuildInterfaceDefinition(TypeDeclarationSyntax syntax)
	{
		InterfaceDefinition definition = new()
		{
			SourceSyntax = syntax,
			Name = GetRequiredIdentifier(syntax.Identifier, syntax, "Interface declaration is missing a name."),
			Symbol = syntax.Identifier?.Value ?? ""
		};

		ApplyDefinitionAttributes(definition, syntax.Attributes);
		ApplyInterfaceDeclarators(definition, syntax.Declarators);

		if (syntax.Type is not null)
			Report(syntax.Type, "Interface declarations may not have a leading type.");

		AddGenericParameters(definition.GenericParameters, syntax.GenericParameterList);
		AddTypeList(definition.BaseTypes, syntax.UnderlyingTypeList);

		if (syntax.ParameterList is not null)
			Report(syntax.ParameterList, "Interface declarations may not have a parameter list.");

		if (syntax.Scope is null)
		{
			if (syntax.SemicolonToken is null)
				Report(syntax, "Interface declaration is missing a body or semicolon.");

			return definition;
		}

		if (syntax.Scope.EnumValueList is not null)
			Report(syntax.Scope.EnumValueList, "Interface bodies may not contain enum values.");

		foreach (DeclarationSyntax child in syntax.Scope.Declarations ?? [])
		{
			if (child.MemberDeclaration is not null)
			{
				if (!IsMethodDeclaration(child.MemberDeclaration))
				{
					Report(child.MemberDeclaration, "Interface declarations may not contain fields.");
				}
				else if (BuildInterfaceFunctionDefinition(child.MemberDeclaration, definition.Name) is FunctionDefinition function)
				{
					definition.Functions.Add(function);
				}
			}
			else if (child.TypeDeclaration is not null)
			{
				Report(child.TypeDeclaration, "Nested type declarations are not supported in interfaces by this binder pass.");
			}
			else
			{
				Report(child, "Interface member declaration is empty.");
			}
		}

		return definition;
	}

	EnumDefinition BuildEnumDefinition(TypeDeclarationSyntax syntax)
	{
		EnumDefinition definition = new()
		{
			SourceSyntax = syntax,
			Name = GetRequiredIdentifier(syntax.Identifier, syntax, "Enum declaration is missing a name."),
			Symbol = syntax.Identifier?.Value ?? ""
		};

		ApplyDefinitionAttributes(definition, syntax.Attributes);
		ApplyNonStructTypeDeclarators(definition, syntax.Declarators, "enum");

		if (syntax.Type is not null)
			Report(syntax.Type, "Enum declarations may not have a leading type.");

		if (syntax.GenericParameterList is not null)
			Report(syntax.GenericParameterList, "Enum declarations may not have generic parameters.");

		if (syntax.ParameterList is not null)
			Report(syntax.ParameterList, "Enum declarations may not have a parameter list.");

		definition.UnderlyingType = BuildOptionalSingleUnderlyingType(syntax.UnderlyingTypeList, "Enum declarations may only have one underlying type.");

		if (syntax.Scope is null)
		{
			if (syntax.SemicolonToken is null)
				Report(syntax, "Enum declaration is missing a body or semicolon.");

			return definition;
		}

		foreach (EnumValueSyntax valueSyntax in syntax.Scope.EnumValueList?.Values ?? [])
			definition.Values.Add(BuildEnumValueDefinition(valueSyntax, definition.Name));

		AddMethodOnlyScope(definition.Functions, syntax.Scope, "enum");
		return definition;
	}

	VariableDefinition BuildEnumValueDefinition(EnumValueSyntax syntax, string enumName)
	{
		VariableDefinition definition = new()
		{
			SourceSyntax = syntax,
			Name = GetRequiredIdentifier(syntax.Identifier, syntax, "Enum value is missing a name."),
			Symbol = enumName + "_" + (syntax.Identifier?.Value ?? ""),
			Type = new NamedTypeReference { SourceSyntax = syntax, Name = enumName }
		};

		if (syntax.Expression is not null)
			definition.InitialValue = BuildExpression(syntax.Expression, "Enum value initializer");

		return definition;
	}

	NewtypeDefinition BuildNewtypeDefinition(TypeDeclarationSyntax syntax)
	{
		NewtypeDefinition definition = new()
		{
			SourceSyntax = syntax,
			Name = GetRequiredIdentifier(syntax.Identifier, syntax, "Newtype declaration is missing a name."),
			Symbol = syntax.Identifier?.Value ?? ""
		};

		ApplyDefinitionAttributes(definition, syntax.Attributes);
		ApplyNonStructTypeDeclarators(definition, syntax.Declarators, "newtype");
		AddGenericParameters(definition.GenericParameters, syntax.GenericParameterList);

		if (syntax.Type is null)
		{
			definition.UnderlyingType = BuildRequiredSingleUnderlyingType(syntax.UnderlyingTypeList, syntax, "Value newtype declaration is missing an underlying type.");

			if (syntax.ParameterList is not null)
				Report(syntax.ParameterList, "Value newtype declarations may not have a parameter list.");

			if (definition.UnderlyingType is not null && !IsValidValueNewtypeUnderlying(definition.UnderlyingType))
				Report((SyntaxNode?)syntax.UnderlyingTypeList ?? syntax, "Value newtype underlying type must be numeric or pointer-like.");
		}
		else
		{
			if (syntax.UnderlyingTypeList is not null)
				Report(syntax.UnderlyingTypeList, "Callable newtype declarations may not also specify an underlying type list.");

			if (syntax.Type is not CallableTypeSyntax and not IterTypeSyntax)
				Report(syntax.Type, "Callable newtype declarations must use a callable or iter type form before the name.");

			definition.IteratorKind = syntax.Type is IterTypeSyntax ? GetIteratorKind(syntax.Type) : IteratorKind.None;
			definition.UnderlyingType = BuildTypeReference(syntax.Type, allowIteratorStorage: true);
			AddParameters(definition.Parameters, syntax.ParameterList);
		}

		if (syntax.Scope is not null)
			AddNewtypeScope(definition, syntax.Scope);

		return definition;
	}

	ParamsDefinition BuildParamsDefinition(TypeDeclarationSyntax syntax)
	{
		ParamsDefinition definition = new()
		{
			SourceSyntax = syntax,
			Name = GetRequiredIdentifier(syntax.Identifier, syntax, "Params declaration is missing a name."),
			Symbol = syntax.Identifier?.Value ?? ""
		};

		ApplyDefinitionAttributes(definition, syntax.Attributes);
		ApplyNonStructTypeDeclarators(definition, syntax.Declarators, "params");
		AddGenericParameters(definition.GenericParameters, syntax.GenericParameterList);

		if (syntax.Type is not null)
			Report(syntax.Type, "Params declarations may not have a leading type.");

		if (syntax.ParameterList is null && syntax.UnderlyingTypeList is null)
			Report(syntax, "Params declarations must have a component parameter list or an underlying grouped type.");

		AddParameters(definition.Components, syntax.ParameterList);
		definition.UnderlyingType = BuildOptionalSingleUnderlyingType(syntax.UnderlyingTypeList, "Params declarations may only have one underlying type.");

		if (syntax.ParameterList is not null && syntax.UnderlyingTypeList is not null)
			Report(syntax.UnderlyingTypeList, "Params declarations cannot combine a component parameter list with an underlying grouped type.");

		if (syntax.Scope is not null)
			AddMethodOnlyScope(definition.Functions, syntax.Scope, "params");

		return definition;
	}

	static bool IsMethodDeclaration(MemberDeclarationSyntax syntax)
	{
		return syntax.ParameterList is not null || syntax.MethodBody is not null || syntax.TildeToken is not null;
	}

	void AddMethodOnlyScope(List<FunctionDefinition> functions, TypeDeclarationScopeSyntax scope, string typeKind)
	{
		foreach (DeclarationSyntax child in scope.Declarations ?? [])
		{
			if (child.MemberDeclaration is not null)
			{
				if (IsMethodDeclaration(child.MemberDeclaration))
				{
					if (BuildFunctionDefinition(child.MemberDeclaration, isGlobal: false, allowVirtual: false) is FunctionDefinition function)
						functions.Add(function);
				}
				else
				{
					Report(child.MemberDeclaration, $"{typeKind} declarations may not contain fields.");
				}
			}
			else if (child.TypeDeclaration is not null)
			{
				Report(child.TypeDeclaration, $"Nested type declarations are not supported in {typeKind} declarations by this binder pass.");
			}
			else
			{
				Report(child, $"{typeKind} member declaration is empty.");
			}
		}
	}

	void AddNewtypeScope(NewtypeDefinition definition, TypeDeclarationScopeSyntax scope)
	{
		foreach (DeclarationSyntax child in scope.Declarations ?? [])
		{
			if (child.MemberDeclaration is not null)
			{
				if (IsMethodDeclaration(child.MemberDeclaration))
				{
					if (BuildFunctionDefinition(child.MemberDeclaration, isGlobal: false, allowVirtual: false, containingTypeName: definition.Name) is FunctionDefinition function)
						definition.Functions.Add(function);
				}
				else if (BuildFieldDefinition(child.MemberDeclaration) is FieldDefinition field)
				{
					if (field.Modifier != FieldModifier.Static)
						Report(child.MemberDeclaration, "Newtype declarations may contain only static fields.");
					definition.Fields.Add(field);
				}
			}
			else if (child.TypeDeclaration is not null)
			{
				Report(child.TypeDeclaration, "Nested type declarations are not supported in newtype declarations by this binder pass.");
			}
			else
			{
				Report(child, "newtype member declaration is empty.");
			}
		}
	}

	TypeReference? BuildOptionalSingleUnderlyingType(UnderlyingTypeListSyntax? syntax, string tooManyMessage)
	{
		if (syntax?.Types is null || syntax.Types.Count == 0)
			return null;

		if (syntax.Types.Count > 1)
			Report(syntax, tooManyMessage);

		return BuildTypeReference(syntax.Types[0]);
	}

	TypeReference? BuildRequiredSingleUnderlyingType(UnderlyingTypeListSyntax? syntax, SyntaxNode owner, string missingMessage)
	{
		if (syntax?.Types is null || syntax.Types.Count == 0)
		{
			Report(owner, missingMessage);
			return null;
		}

		if (syntax.Types.Count > 1)
			Report(syntax, "Declaration may only have one underlying type.");

		return BuildTypeReference(syntax.Types[0]);
	}

	VariableDefinition? BuildVariableDefinition(MemberDeclarationSyntax syntax, bool isGlobal)
	{
		if (syntax.TildeToken is not null)
			Report(syntax.TildeToken.Value.Range, "Destructor declarations are not supported by this binder pass.");

		if (syntax.GenericParameterList is not null)
			Report(syntax.GenericParameterList, "Variables may not have generic parameters.");

		if (syntax.Type is null)
		{
			Report(syntax, "Variable declaration is missing a type.");
			return null;
		}

		VariableDefinition definition = new()
		{
			SourceSyntax = syntax,
			Name = GetRequiredIdentifier(syntax.Identifier, syntax, "Variable declaration is missing a name."),
			Symbol = syntax.Identifier?.Value ?? "",
			Type = BuildTypeReference(syntax.Type)
		};

		ApplyDefinitionAttributes(definition, syntax.Attributes);
		ApplyVariableDeclarators(definition, syntax.Declarators, isGlobal);

		if (IsVoid(definition.Type))
			Report(syntax.Type, "Variables may not have type void.");

		if (syntax.Assignment is not null)
			definition.InitialValue = BuildExpression(syntax.Assignment.Expression, "Variable initializer");

		return definition;
	}

	FieldDefinition? BuildFieldDefinition(MemberDeclarationSyntax syntax)
	{
		if (syntax.TildeToken is not null)
			Report(syntax.TildeToken.Value.Range, "Destructor declarations are not fields.");

		if (syntax.GenericParameterList is not null)
			Report(syntax.GenericParameterList, "Fields may not have generic parameters.");

		if (syntax.Type is null)
		{
			Report(syntax, "Field declaration is missing a type.");
			return null;
		}

		FieldDefinition definition = new()
		{
			SourceSyntax = syntax,
			Name = GetRequiredIdentifier(syntax.Identifier, syntax, "Field declaration is missing a name."),
			Symbol = syntax.Identifier?.Value ?? "",
			Type = BuildTypeReference(syntax.Type)
		};

		ApplyDefinitionAttributes(definition, syntax.Attributes);
		ApplyFieldDeclarators(definition, syntax.Declarators);

		if (IsVoid(definition.Type))
			Report(syntax.Type, "Fields may not have type void.");

		if (syntax.Assignment is not null)
			definition.InitialValue = BuildExpression(syntax.Assignment.Expression, "Field initializer");

		return definition;
	}

	FunctionDefinition? BuildFunctionDefinition(
		MemberDeclarationSyntax syntax,
		bool isGlobal,
		bool allowVirtual,
		string? containingTypeName = null,
		bool isInterface = false,
		bool allowBodylessWithoutExtern = false)
	{
		if (syntax.Assignment is not null)
			Report(syntax.Assignment, "Methods may not have variable-style initializers.");

		bool isDestructor = syntax.TildeToken is not null;
		bool isConstructor = !isDestructor && syntax.Type is null;
		bool isLifecycleMember = isConstructor || isDestructor;

		FunctionDefinition definition = new()
		{
			SourceSyntax = syntax,
			Name = !isDestructor
				? GetRequiredIdentifier(syntax.Identifier, syntax, "Method declaration is missing a name.")
				: "~" + GetRequiredIdentifier(syntax.Identifier, syntax, "Destructor declaration is missing a name."),
			Symbol = syntax.Identifier?.Value ?? ""
		};

		ApplyDefinitionAttributes(definition, syntax.Attributes);
		ApplyFunctionDeclarators(definition, syntax.Declarators, isGlobal, allowVirtual, onlyExport: isConstructor);
		definition.CallSpec = syntax.CallSpec?.Value;
		AddGenericParameters(definition.GenericParameters, syntax.GenericParameterList);

		if (isDestructor)
		{
			if (definition.Modifier == FunctionModifier.None)
				SetFunctionModifier(definition, FunctionModifier.Destructor, syntax, "destructor");

			if (syntax.Type is not null)
				Report(syntax.Type, "Destructor declarations may not have a return type.");
		}
		else if (isConstructor)
		{
			SetFunctionModifier(definition, FunctionModifier.Constructor, syntax, "constructor");
		}
		else if (syntax.Type is null)
		{
			Report(syntax, "Method declaration is missing a return type.");
		}
		else
		{
			definition.IteratorKind = GetIteratorKind(syntax.Type);
			definition.ReturnType = BuildTypeReference(syntax.Type, allowIteratorStorage: true);
		}

		if (syntax.CallableAscriptionType is not null)
			definition.CallableAscriptionType = BuildTypeReference(syntax.CallableAscriptionType, allowIteratorStorage: true);

		if (syntax.ParameterList is null)
			Report(syntax, "Method declaration is missing a parameter list.");
		else
		{
			foreach (ParameterSyntax parameter in syntax.ParameterList.Parameters ?? [])
				definition.Parameters.Add(BuildParameterDefinition(parameter));
		}

		if (isLifecycleMember)
			ValidateLifecycleMember(definition, syntax, containingTypeName, isInterface, isConstructor);

		if (syntax.SemicolonToken is not null)
		{
			if (definition.Extern is null && definition.Modifier != FunctionModifier.Abstract && !allowBodylessWithoutExtern)
				Report(syntax.SemicolonToken.Value.Range, "Method declarations without a body must be extern.");
		}
		else if (syntax.MethodBody is null)
		{
			Report(syntax, "Method declaration is missing a body or semicolon.");
		}
		else
		{
			if (definition.Extern is not null)
				Report(syntax.MethodBody, "Extern methods may not have a body.");

			definition.Body = BuildFunctionBody(syntax.MethodBody, definition);
		}

		return definition;
	}

	BlockStatement? BuildFunctionBody(MethodBodySyntax syntax, FunctionDefinition function)
	{
		switch (syntax)
		{
			case BlockMethodBodySyntax block:
				return BuildBlockFunctionBody(block);

			case ExpressionMethodBodySyntax expressionBody:
			{
				Expression? expression = BuildExpression(expressionBody.Expression, "Expression method body");
				Statement statement = FunctionReturnsVoid(function)
					? new ExpressionStatement
					{
						SourceSyntax = expressionBody,
						Expression = expression
					}
					: new ReturnStatement
					{
						SourceSyntax = expressionBody,
						Expression = expression
					};
				return new BlockStatement
				{
					SourceSyntax = expressionBody,
					Statements = { statement }
				};
			}

			default:
				Report(syntax, "Unsupported method body syntax.");
				return null;
		}
	}

	BlockStatement? BuildFunctionBody(MethodBodySyntax syntax)
	{
		switch (syntax)
		{
			case BlockMethodBodySyntax block:
				return BuildBlockFunctionBody(block);

			case ExpressionMethodBodySyntax expressionBody:
				return new BlockStatement
				{
					SourceSyntax = expressionBody,
					Statements =
					{
						new ReturnStatement
						{
							SourceSyntax = expressionBody,
							Expression = BuildExpression(expressionBody.Expression, "Expression method body")
						}
					}
				};

			default:
				Report(syntax, "Unsupported method body syntax.");
				return null;
		}
	}

	static bool FunctionReturnsVoid(FunctionDefinition function)
	{
		return function.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor
			|| function.ReturnType is PrimitiveTypeReference { Type: PrimitiveType.Void };
	}

	void ValidateLifecycleMember(
		FunctionDefinition definition,
		MemberDeclarationSyntax syntax,
		string? containingTypeName,
		bool isInterface,
		bool isConstructor)
	{
		string memberKind = isConstructor ? "Constructors" : "Destructors";
		string expectedName = containingTypeName ?? "";
		string actualName = syntax.Identifier?.Value ?? "";

		if (containingTypeName is null)
			Report(syntax, $"{memberKind} can only be declared in class, struct, or interface declarations.");
		else if (actualName != expectedName)
			Report(syntax.Identifier?.Range ?? GetRange(syntax), $"{memberKind[..^1]} name must match containing type '{expectedName}'.");

		if (definition.GenericParameters.Count > 0)
			Report((SyntaxNode?)syntax.GenericParameterList ?? syntax, $"{memberKind} may not have generic parameters.");

		if (isConstructor)
		{
			if (definition.ReturnType is not null)
				Report((SyntaxNode?)syntax.Type ?? syntax, "Constructors may not have a return type.");

			if (isInterface)
				ValidateInterfaceConstructorParameters(definition, syntax);
		}
		else
		{
			ValidateDestructorParameters(definition, syntax, isInterface);
		}
	}

	void ValidateDestructorParameters(FunctionDefinition definition, MemberDeclarationSyntax syntax, bool isInterface)
	{
		if (definition.Parameters.Count > 1)
		{
			Report((SyntaxNode?)syntax.ParameterList ?? syntax, "Destructors may only declare a single optional within parameter.");
			return;
		}

		if (definition.Parameters.Count == 0)
		{
			if (isInterface)
				Report((SyntaxNode?)syntax.ParameterList ?? syntax, "Interface destructors must declare a within parameter.");

			return;
		}

		if (!IsWithinParameter(definition.Parameters[0]))
			Report(definition.Parameters[0].SourceSyntax ?? syntax, "Destructor parameter must be a within parameter.");
	}

	void ValidateInterfaceConstructorParameters(FunctionDefinition definition, MemberDeclarationSyntax syntax)
	{
		if (definition.Parameters.Count == 0)
		{
			Report((SyntaxNode?)syntax.ParameterList ?? syntax, "Interface constructors must declare a within parameter.");
			return;
		}

		ParameterDefinition last = definition.Parameters[^1];
		if (!IsWithinParameter(last))
			Report(last.SourceSyntax ?? syntax, "Interface constructor's last parameter must be a within parameter.");
	}

	static bool IsWithinParameter(ParameterDefinition parameter)
	{
		return parameter is WithinParameterDefinition || parameter.Modifier == ParameterModifier.Within;
	}

	FunctionDefinition? BuildInterfaceFunctionDefinition(MemberDeclarationSyntax syntax, string containingTypeName)
	{
		FunctionDefinition? definition = BuildFunctionDefinition(
			syntax,
			isGlobal: false,
			allowVirtual: false,
			containingTypeName: containingTypeName,
			isInterface: true,
			allowBodylessWithoutExtern: true);
		if (definition is null)
			return null;

		if (syntax.SemicolonToken is null)
			Report((SyntaxNode?)syntax.MethodBody ?? syntax, "Interface methods may not have bodies.");

		if (definition.Modifier == FunctionModifier.Static)
			Report(syntax, "Interface methods may not be static.");

		if (definition.Extern is not null)
			Report(syntax, "Interface methods may not be extern.");

		return definition;
	}

	void ApplyDefinitionAttributes(Definition definition, List<AttributeSyntax>? attributes)
	{
		foreach (AttributeSyntax attribute in attributes ?? [])
			definition.Attributes.Add(BuildAttribute(attribute));
	}

	AttributeConstructor BuildAttribute(AttributeSyntax syntax)
	{
		AttributeConstructor attribute = new()
		{
			SourceSyntax = syntax,
			Name = syntax.AttributeIdentifier?.Value ?? ""
		};

		if (syntax.AttributeIdentifier is null)
			Report(syntax, "Attribute is missing a name.");

		if (syntax.ArgumentList is not null)
			AddArguments(attribute.Arguments, syntax.ArgumentList, "Attribute argument");
		else
			foreach (ExpressionSyntax expression in syntax.ExpressionList?.Expressions ?? [])
			{
				attribute.Arguments.Add(new ArgumentExpression
				{
					SourceSyntax = expression,
					Value = BuildExpression(expression, "Attribute argument")
				});
			}

		return attribute;
	}

	void ApplyStructDeclarators(StructDefinition definition, List<TypeDeclarationDeclaratorSyntax>? declarators)
	{
		foreach (TypeDeclarationDeclaratorSyntax declarator in declarators ?? [])
		{
			switch (declarator.Keyword?.Value)
			{
				case "export":
					SetVisibility(definition, declarator, "export");
					break;

				case "public":
					SetVisibility(definition, declarator, "public");
					break;

				case "extern":
					definition.Extern = SetNullableArgument(definition.Extern, "", declarator, "extern");
					break;

				case "fixed":
					definition.Modifier = StructModifier.Fixed;
					break;

				case "virtual":
				case "abstract":
				case "sealed":
				case "escaped":
					Report(declarator, $"'{declarator.Keyword.Value.Value}' is not a valid struct declarator.");
					break;

				default:
					Report(declarator, "Unknown struct declarator.");
					break;
			}
		}
	}

	void ApplyClassDeclarators(ClassDefinition definition, List<TypeDeclarationDeclaratorSyntax>? declarators)
	{
		foreach (TypeDeclarationDeclaratorSyntax declarator in declarators ?? [])
		{
			switch (declarator.Keyword?.Value)
			{
				case "export":
					SetVisibility(definition, declarator, "export");
					break;

				case "public":
					SetVisibility(definition, declarator, "public");
					break;

				case "extern":
					definition.Extern = SetNullableArgument(definition.Extern, "", declarator, "extern");
					break;

				case "virtual":
					SetClassModifier(definition, ClassModifier.Virtual, declarator, "virtual");
					break;

				case "abstract":
					SetClassModifier(definition, ClassModifier.Abstract, declarator, "abstract");
					break;

				case "sealed":
					SetClassModifier(definition, ClassModifier.Sealed, declarator, "sealed");
					break;

				case "escaped":
					if (definition.IsEscaped)
						Report(declarator, "Duplicate 'escaped' declarator.");

					definition.IsEscaped = true;
					break;

				case "fixed":
					Report(declarator, "'fixed' is not a valid class declarator.");
					break;

				default:
					Report(declarator, "Unknown class declarator.");
					break;
			}
		}
	}

	void ApplyInterfaceDeclarators(InterfaceDefinition definition, List<TypeDeclarationDeclaratorSyntax>? declarators)
	{
		foreach (TypeDeclarationDeclaratorSyntax declarator in declarators ?? [])
		{
			switch (declarator.Keyword?.Value)
			{
				case "export":
					SetVisibility(definition, declarator, "export");
					break;

				case "public":
					SetVisibility(definition, declarator, "public");
					break;

				case "extern":
					definition.Extern = SetNullableArgument(definition.Extern, "", declarator, "extern");
					break;

				case "escaped":
					if (definition.IsEscaped)
						Report(declarator, "Duplicate 'escaped' declarator.");

					definition.IsEscaped = true;
					break;

				case "virtual":
				case "abstract":
				case "sealed":
				case "fixed":
					Report(declarator, $"'{declarator.Keyword.Value.Value}' is not a valid interface declarator.");
					break;

				default:
					Report(declarator, "Unknown interface declarator.");
					break;
			}
		}
	}

	void SetClassModifier(ClassDefinition definition, ClassModifier modifier, SyntaxNode syntax, string name)
	{
		if (definition.Modifier != ClassModifier.None)
			Report(syntax, $"'{name}' cannot be combined with '{definition.Modifier}' on a class.");
		else
			definition.Modifier = modifier;
	}

	void SetVisibility(Definition definition, SyntaxNode syntax, string keyword)
	{
		if (definition.Export is not null || definition.Public is not null)
		{
			Report(syntax, $"'{keyword}' cannot be combined with another visibility declarator.");
			return;
		}

		if (keyword == "export")
			definition.Export = SetNullableArgument(definition.Export, "", syntax, "export");
		else
			definition.Public = SetNullableArgument(definition.Public, "", syntax, "public");
	}

	void ApplyNonStructTypeDeclarators(TypeDefinition definition, List<TypeDeclarationDeclaratorSyntax>? declarators, string typeKind)
	{
		foreach (TypeDeclarationDeclaratorSyntax declarator in declarators ?? [])
		{
			switch (declarator.Keyword?.Value)
			{
				case "export":
					SetVisibility(definition, declarator, "export");
					break;

				case "public":
					SetVisibility(definition, declarator, "public");
					break;

				case "extern":
					definition.Extern = SetNullableArgument(definition.Extern, "", declarator, "extern");
					break;

				case "virtual":
				case "abstract":
				case "sealed":
				case "fixed":
				case "escaped":
					Report(declarator, $"'{declarator.Keyword.Value.Value}' is not a valid {typeKind} declarator.");
					break;

				default:
					Report(declarator, $"Unknown {typeKind} declarator.");
					break;
			}
		}
	}

	void ApplyVariableDeclarators(VariableDefinition definition, List<MemberDeclaratorSyntax>? declarators, bool isGlobal)
	{
		foreach (MemberDeclaratorSyntax declarator in declarators ?? [])
		{
			switch (declarator.Keyword?.Value)
			{
				case "export":
					SetVisibility(definition, declarator, "export");
					break;

				case "public":
					SetVisibility(definition, declarator, "public");
					break;

				case "extern":
					definition.Extern = SetNullableArgument(definition.Extern, "", declarator, "extern");
					break;

				case "fixed":
					definition.IsFixedStorage = true;
					break;

				case "static":
					if (isGlobal)
						Report(declarator, "'static' is not valid on a global variable.");
					break;

				case "virtual":
				case "override":
				case "sealed":
				case "abstract":
				case "async":
					Report(declarator, $"'{declarator.Keyword.Value.Value}' is not valid on a variable declaration.");
					break;

				default:
					Report(declarator, "Unknown variable declarator.");
					break;
			}
		}
	}

	void ApplyFieldDeclarators(FieldDefinition definition, List<MemberDeclaratorSyntax>? declarators)
	{
		foreach (MemberDeclaratorSyntax declarator in declarators ?? [])
		{
			switch (declarator.Keyword?.Value)
			{
				case "static":
					definition.Modifier = FieldModifier.Static;
					break;

				case "fixed":
					definition.IsFixedStorage = true;
					break;

				case "export":
					SetVisibility(definition, declarator, "export");
					break;

				case "public":
					SetVisibility(definition, declarator, "public");
					break;

				case "extern":
				case "virtual":
				case "override":
				case "sealed":
				case "abstract":
				case "async":
					Report(declarator, $"'{declarator.Keyword.Value.Value}' is not valid on a field declaration.");
					break;

				default:
					Report(declarator, "Unknown field declarator.");
					break;
			}
		}
	}

	void ApplyFunctionDeclarators(FunctionDefinition definition, List<MemberDeclaratorSyntax>? declarators, bool isGlobal, bool allowVirtual, bool onlyExport)
	{
		foreach (MemberDeclaratorSyntax declarator in declarators ?? [])
		{
			switch (declarator.Keyword?.Value)
			{
				case "export":
					SetVisibility(definition, declarator, "export");
					break;

				case "public":
					SetVisibility(definition, declarator, "public");
					break;

				case "extern":
					definition.Extern = SetNullableArgument(definition.Extern, "", declarator, "extern");
					break;

				case "static":
					if (onlyExport)
					{
						Report(declarator, "'static' is not valid on constructors or destructors.");
						break;
					}

					if (isGlobal)
						Report(declarator, "'static' is not valid on a global method.");
					else
						SetFunctionModifier(definition, FunctionModifier.Static, declarator, "static");
					break;

				case "virtual":
					if (onlyExport)
					{
						Report(declarator, "'virtual' is not valid on constructors or destructors.");
						break;
					}

					if (allowVirtual)
						SetFunctionModifier(definition, FunctionModifier.Virtual, declarator, "virtual");
					else
						Report(declarator, "'virtual' is not valid on this method declaration.");
					break;

				case "override":
					if (onlyExport)
					{
						Report(declarator, "'override' is not valid on constructors or destructors.");
						break;
					}

					if (allowVirtual)
						SetFunctionModifier(definition, FunctionModifier.Override, declarator, "override");
					else
						Report(declarator, "'override' is not valid on this method declaration.");
					break;

				case "sealed":
					if (onlyExport)
					{
						Report(declarator, "'sealed' is not valid on constructors or destructors.");
						break;
					}

					if (allowVirtual)
						SetFunctionModifier(definition, FunctionModifier.Sealed, declarator, "sealed");
					else
						Report(declarator, "'sealed' is not valid on this method declaration.");
					break;

				case "abstract":
					if (onlyExport)
					{
						Report(declarator, "'abstract' is not valid on constructors or destructors.");
						break;
					}

					if (allowVirtual)
						SetFunctionModifier(definition, FunctionModifier.Abstract, declarator, "abstract");
					else
						Report(declarator, "'abstract' is not valid on this method declaration.");
					break;

				case "async":
					if (onlyExport)
					{
						Report(declarator, "'async' is not valid on constructors or destructors.");
						break;
					}

					if (definition.IsAsync)
						Report(declarator, "Duplicate 'async' declarator.");

					definition.IsAsync = true;
					break;

				case "fixed":
					Report(declarator, "'fixed' is not valid on a method declaration.");
					break;

				default:
					Report(declarator, "Unknown method declarator.");
					break;
			}
		}
	}

	void SetFunctionModifier(FunctionDefinition definition, FunctionModifier modifier, SyntaxNode syntax, string name)
	{
		if (definition.Modifier != FunctionModifier.None)
			Report(syntax, $"'{name}' cannot be combined with '{definition.Modifier}' on a method.");
		else
			definition.Modifier = modifier;
	}

	void AddGenericParameters(List<GenericParameter> target, GenericParameterListSyntax? syntax)
	{
		foreach (GenericParameterSyntax parameterSyntax in syntax?.Parameters ?? [])
		{
			GenericParameter parameter = new()
			{
				SourceSyntax = parameterSyntax,
				Name = GetRequiredIdentifier(parameterSyntax.Identifier, parameterSyntax, "Generic parameter is missing a name."),
				RequiresImplementation = parameterSyntax.ImplementsKeyword is not null,
				Constraint = parameterSyntax.Type is null ? null : BuildTypeReference(parameterSyntax.Type)
			};

			target.Add(parameter);
		}
	}

	void AddParameters(List<ParameterDefinition> target, ParameterListSyntax? syntax)
	{
		foreach (ParameterSyntax parameter in syntax?.Parameters ?? [])
			target.Add(BuildParameterDefinition(parameter));
	}

	void AddTypeList(List<TypeReference> target, UnderlyingTypeListSyntax? syntax)
	{
		foreach (TypeSyntax typeSyntax in syntax?.Types ?? [])
			target.Add(BuildTypeReference(typeSyntax));
	}

	TypeReference BuildTypeReference(TypeSyntax syntax, bool allowIteratorStorage = false)
	{
		switch (syntax)
		{
			case ThisTypeSyntax thisType:
				return new ThisTypeReference { SourceSyntax = thisType };

			case CallableTypeSyntax callable:
				return BuildCallableTypeReference(callable);

			case AttributedTypeSyntax attributed:
				return new AttributedTypeReference
				{
					SourceSyntax = attributed,
					Attribute = attributed.Attribute is null ? null : BuildAttribute(attributed.Attribute),
					Type = attributed.Type is null ? MissingType(attributed, "Attributed type is missing its inner type.") : BuildTypeReference(attributed.Type)
				};

			case ArrayTypeSyntax array:
				if (array.Length is null)
					return new ArrayTypeReference
					{
						SourceSyntax = array,
						ElementType = array.ElementType is null ? MissingType(array, "Array type is missing an element type.") : BuildTypeReference(array.ElementType)
					};
				return BuildFixedArrayTypeReference(array);

			case OptionalTypeSyntax optional:
				return new OptionalTypeReference
				{
					SourceSyntax = optional,
					ElementType = optional.ElementType is null ? MissingType(optional, "Optional type is missing an element type.") : BuildTypeReference(optional.ElementType)
				};

			case PointerTypeSyntax pointer:
				return new PointerTypeReference
				{
					SourceSyntax = pointer,
					ElementType = pointer.ElementType is null ? MissingType(pointer, "Pointer type is missing an element type.") : BuildTypeReference(pointer.ElementType)
				};

			case GenericTypeSyntax generic:
				return BuildGenericTypeReference(generic);

			case IterTypeSyntax iter:
				if (iter.StorageKeyword is not null && !allowIteratorStorage)
					Report(iter.StorageKeyword.Value.Range, "'struct iter' and 'class iter' are function return modifiers, not iter type modifiers.");

				IterTypeReference iterReference = new()
				{
					SourceSyntax = iter,
					IsAsync = iter.AsyncKeyword is not null,
					ElementType = iter.ElementType is null && iter.ParameterList is null ? MissingType(iter, "Iter type is missing an element type.") : iter.ElementType is null ? null : BuildTypeReference(iter.ElementType)
				};
				foreach (ParameterSyntax parameter in iter.ParameterList?.Parameters ?? [])
					iterReference.Parameters.Add(BuildParameterDefinition(parameter));
				if (iterReference.ElementType is null)
				{
					foreach (ParameterDefinition parameter in iterReference.Parameters)
					{
						if (parameter.Modifier == ParameterModifier.Thrown)
							continue;
						iterReference.ElementType = parameter.Type;
						break;
					}
				}
				return iterReference;

			case ParamsTypeSyntax grouped:
				return MissingType(grouped, "params(T) type syntax is no longer supported; use an expanded built-in form or struct(T).");

			case StructTypeSyntax materialized:
				return new MaterializedStructTypeReference
				{
					SourceSyntax = materialized,
					ParamsType = materialized.Type is null ? MissingType(materialized, "Struct type is missing an inner params type.") : BuildTypeReference(materialized.Type)
				};

			case ThrownTypeSyntax thrown:
				return new ThrownTypeReference
				{
					SourceSyntax = thrown,
					Type = thrown.Type is null ? MissingType(thrown, "Thrown type is missing an inner type.") : BuildTypeReference(thrown.Type)
				};

			case DeclaratorTypeSyntax declarator:
				return BuildDeclaratorTypeReference(declarator);

			case TargetTypeSpecTypeSyntax targetSpec:
				return new TargetTypeSpecTypeReference
				{
					SourceSyntax = targetSpec,
					Specifier = targetSpec.Specifier?.Value ?? "",
					Type = targetSpec.Type is null ? MissingType(targetSpec, "Target typespec is missing an inner type.") : BuildTypeReference(targetSpec.Type),
					IsPrefix = targetSpec.IsPrefix
				};

			case QualifiedNameTypeSyntax name:
				return BuildNamedTypeReference(name);

			default:
				Report(syntax, "Unsupported type syntax.");
				return MissingType(syntax, "Unsupported type syntax.");
		}
	}

	TypeReference BuildFixedArrayTypeReference(ArrayTypeSyntax array)
	{
		List<ArrayTypeSyntax> dimensions = [];
		TypeSyntax? element = array;
		while (element is ArrayTypeSyntax { Length: not null } fixedArray)
		{
			dimensions.Add(fixedArray);
			element = fixedArray.ElementType;
		}

		TypeReference type = element is null
			? MissingType(array, "Fixed-size array type is missing an element type.")
			: BuildTypeReference(element);
		foreach (ArrayTypeSyntax dimension in dimensions)
		{
			type = new FixedArrayTypeReference
			{
				SourceSyntax = dimension,
				ElementType = type,
				LengthExpression = BuildExpression(dimension.Length!, "Fixed-size array length")
			};
		}
		return type;
	}

	static IteratorKind GetIteratorKind(TypeSyntax syntax)
	{
		if (syntax is not IterTypeSyntax iter || iter.StorageKeyword is not Token storage)
			return IteratorKind.None;

		return storage.Value switch
		{
			"struct" => IteratorKind.Struct,
			"class" => IteratorKind.Class,
			_ => IteratorKind.None
		};
	}

	CallableTypeReference BuildCallableTypeReference(CallableTypeSyntax syntax)
	{
		CallableTypeReference type = new()
		{
			SourceSyntax = syntax,
			Kind = syntax.CallableKeyword?.Value switch
			{
				"fn" => CallableKind.Function,
				"delegate" => CallableKind.Delegate,
				"async" => CallableKind.Async,
				"once" => CallableKind.Once,
				_ => CallableKind.Function
			},
			CallSpec = syntax.CallSpec?.Value,
			TargetSpec = syntax.TargetSpec?.Value,
			ReturnType = syntax.ReturnType is null ? MissingType(syntax, "Callable type is missing a return type.") : BuildTypeReference(syntax.ReturnType)
		};

		foreach (ParameterSyntax parameter in syntax.ParameterList?.Parameters ?? [])
			type.Parameters.Add(BuildParameterDefinition(parameter));

		return type;
	}

	TypeReference BuildGenericTypeReference(GenericTypeSyntax syntax)
	{
		TypeReference type = syntax.Type is null
			? MissingType(syntax, "Generic type is missing a target type.")
			: BuildTypeReference(syntax.Type);

		IEnumerable<TypeSyntax> typeArguments = syntax.TypeArgumentList?.Types ?? [];

		if (type is NamedTypeReference named)
		{
			foreach (TypeSyntax argument in typeArguments)
				named.TypeArguments.Add(BuildTypeReference(argument));

			return named;
		}

		GenericTypeReference generic = new() { SourceSyntax = syntax, Type = type };
		foreach (TypeSyntax argument in typeArguments)
			generic.TypeArguments.Add(BuildTypeReference(argument));

		return generic;
	}

	TypeReference BuildDeclaratorTypeReference(DeclaratorTypeSyntax syntax)
	{
		TypeReference inner = syntax.Type is null
			? MissingType(syntax, "Type declarator is missing an inner type.")
			: BuildTypeReference(syntax.Type);

		TypeDeclaratorSyntax? declarator = syntax.Declarator;
		switch (declarator?.Keyword?.Value)
		{
			case "const":
				return new ConstTypeReference { SourceSyntax = syntax, Type = inner };

			case "volatile":
				return new VolatileTypeReference { SourceSyntax = syntax, Type = inner };

			case "escaped":
				return new EscapedTypeReference { SourceSyntax = syntax, Type = inner };

			case "scoped":
			{
				ScopedTypeReference scoped = new() { SourceSyntax = syntax, Type = inner };
				AddAnchors(scoped.Anchors, declarator.AnchorList);
				return scoped;
			}

			case "unscoped":
			{
				UnscopedTypeReference unscoped = new() { SourceSyntax = syntax, Type = inner };
				AddAnchors(unscoped.Anchors, declarator.AnchorList);
				return unscoped;
			}

			default:
				Report(syntax, "Unknown type declarator.");
				return inner;
		}
	}

	TypeReference BuildNamedTypeReference(QualifiedNameTypeSyntax syntax)
	{
		string name = GetRequiredIdentifier(syntax.Identifier, syntax, "Type name is missing an identifier.");

		if ((syntax.Qualifiers is null || syntax.Qualifiers.Count == 0) && TryGetPrimitiveType(name, out PrimitiveType primitive))
			return new PrimitiveTypeReference { SourceSyntax = syntax, Type = primitive };

		if ((syntax.Qualifiers is null || syntax.Qualifiers.Count == 0) && name == "any")
			return new AnyTypeReference { SourceSyntax = syntax };

		if ((syntax.Qualifiers is null || syntax.Qualifiers.Count == 0) && name == "copyable")
			return new CopyableTypeReference { SourceSyntax = syntax };

		if ((syntax.Qualifiers is null || syntax.Qualifiers.Count == 0) && name == "classtype")
			return new ClassTypeReference { SourceSyntax = syntax };

		NamedTypeReference type = new()
		{
			SourceSyntax = syntax,
			Name = name
		};

		foreach (QualifierSyntax qualifier in syntax.Qualifiers ?? [])
		{
				if (qualifier.Identifier is null)
					Report(qualifier, "Type qualifier is missing an identifier.");
				else
					type.Qualifiers.Add(qualifier.Identifier.Value.Value);
		}

		return type;
	}

	ParameterDefinition BuildParameterDefinition(ParameterSyntax syntax)
	{
		switch (syntax)
		{
			case ValueParameterSyntax value:
			{
				ParameterDefinition parameter = new()
				{
					SourceSyntax = value,
					Name = value.Identifier?.Value ?? "",
					Symbol = value.Identifier?.Value ?? "",
					Type = value.Type is null ? MissingType(value, "Parameter is missing a type.") : BuildTypeReference(value.Type),
					DefaultValue = value.DefaultValue is null ? null : BuildExpression(value.DefaultValue, "Parameter default value")
				};

				if (value.WithinKeyword is not null)
					parameter.Modifier = ParameterModifier.Within;

				foreach (ParameterDeclaratorSyntax declarator in value.Declarators ?? [])
					ApplyParameterDeclarator(parameter, declarator);

				return parameter;
			}

			case WithinParameterSyntax within:
				return new WithinParameterDefinition
				{
					SourceSyntax = within,
					Name = within.Identifier?.Value ?? "",
					Symbol = within.Identifier?.Value ?? "",
					Modifier = ParameterModifier.Within
				};

			case ThisParameterSyntax thisParameter:
				return new ThisParameterDefinition
				{
					SourceSyntax = thisParameter,
					Name = "this",
					Symbol = "this"
				};

			case SizeOfParameterSyntax sizeOf:
				return new SizeOfParameterDefinition
				{
					SourceSyntax = sizeOf,
					Name = "sizeof",
					Symbol = "sizeof",
					Type = sizeOf.Type is null ? MissingType(sizeOf, "sizeof parameter is missing a type.") : BuildTypeReference(sizeOf.Type)
				};

			case NameOfParameterSyntax nameOf:
				return new NameOfParameterDefinition
				{
					SourceSyntax = nameOf,
					Name = "typenameof",
					Symbol = "typenameof",
					Type = nameOf.Type is null ? MissingType(nameOf, "typenameof parameter is missing a type.") : BuildTypeReference(nameOf.Type)
				};

			case VTableOfParameterSyntax vtableOf:
				return new VTableOfParameterDefinition
				{
					SourceSyntax = vtableOf,
					Name = "vtableof",
					Symbol = "vtableof",
					Type = vtableOf.Type is null ? MissingType(vtableOf, "vtableof parameter is missing a type.") : BuildTypeReference(vtableOf.Type),
					InterfaceType = vtableOf.InterfaceType is null ? MissingType(vtableOf, "vtableof parameter is missing an interface type.") : BuildTypeReference(vtableOf.InterfaceType)
				};

			default:
				Report(syntax, "Unsupported parameter syntax.");
				return new ParameterDefinition { SourceSyntax = syntax };
		}
	}

	void ApplyParameterDeclarator(ParameterDefinition parameter, ParameterDeclaratorSyntax syntax)
	{
		foreach (AttributeSyntax attribute in syntax.Attributes ?? [])
			parameter.Attributes.Add(BuildAttribute(attribute));

		switch (syntax.Keyword?.Value)
		{
			case null:
				break;

			case "in":
				parameter.Modifier = ParameterModifier.In;
				break;

			case "out":
				parameter.Modifier = ParameterModifier.Out;
				break;

			case "thrown":
				parameter.Modifier = ParameterModifier.Thrown;
				break;

			case "overload":
				parameter.IsOverloadSelector = true;
				break;

			default:
				Report(syntax, "Unknown parameter declarator.");
				break;
		}
	}

	void AddAnchors(List<string> anchors, IdentListSyntax? syntax)
	{
		foreach (Token identifier in syntax?.Identifiers ?? [])
			anchors.Add(identifier.Value);
	}

	TypeReference MissingType(SyntaxNode syntax, string message)
	{
		Report(syntax, message);
		return new NamedTypeReference { SourceSyntax = syntax, Name = "<missing>" };
	}

	static bool IsVoid(TypeReference? type)
	{
		return type is PrimitiveTypeReference { Type: PrimitiveType.Void };
	}

	static bool IsValidValueNewtypeUnderlying(TypeReference type)
	{
		return type switch
		{
			PrimitiveTypeReference { Type: PrimitiveType.Byte or PrimitiveType.SByte or PrimitiveType.UShort or PrimitiveType.Short or PrimitiveType.UInt or PrimitiveType.Int or PrimitiveType.ULong or PrimitiveType.Long or PrimitiveType.NUInt or PrimitiveType.NInt or PrimitiveType.Float or PrimitiveType.Double or PrimitiveType.Char or PrimitiveType.WChar or PrimitiveType.AChar or PrimitiveType.UChar } => true,
			PointerTypeReference => true,
			ConstTypeReference { Type: not null } constType => IsValidValueNewtypeUnderlying(constType.Type),
			VolatileTypeReference { Type: not null } volatileType => IsValidValueNewtypeUnderlying(volatileType.Type),
			EscapedTypeReference { Type: not null } escapedType => IsValidValueNewtypeUnderlying(escapedType.Type),
			ScopedTypeReference { Type: not null } scopedType => IsValidValueNewtypeUnderlying(scopedType.Type),
			UnscopedTypeReference { Type: not null } unscopedType => IsValidValueNewtypeUnderlying(unscopedType.Type),
			_ => false
		};
	}

	static bool TryGetPrimitiveType(string name, out PrimitiveType type)
	{
		switch (name)
		{
			case "void":
				type = PrimitiveType.Void;
				return true;
			case "bool":
				type = PrimitiveType.Bool;
				return true;
			case "string":
				type = PrimitiveType.String;
				return true;
			case "wstring":
				type = PrimitiveType.WString;
				return true;
			case "astring":
				type = PrimitiveType.AString;
				return true;
			case "byte":
				type = PrimitiveType.Byte;
				return true;
			case "sbyte":
				type = PrimitiveType.SByte;
				return true;
			case "ushort":
				type = PrimitiveType.UShort;
				return true;
			case "short":
				type = PrimitiveType.Short;
				return true;
			case "uint":
				type = PrimitiveType.UInt;
				return true;
			case "int":
				type = PrimitiveType.Int;
				return true;
			case "ulong":
				type = PrimitiveType.ULong;
				return true;
			case "long":
				type = PrimitiveType.Long;
				return true;
			case "nuint":
				type = PrimitiveType.NUInt;
				return true;
			case "nint":
				type = PrimitiveType.NInt;
				return true;
			case "float":
				type = PrimitiveType.Float;
				return true;
			case "double":
				type = PrimitiveType.Double;
				return true;
			case "char":
				type = PrimitiveType.Char;
				return true;
			case "wchar":
				type = PrimitiveType.WChar;
				return true;
			case "achar":
				type = PrimitiveType.AChar;
				return true;
			case "uchar":
				type = PrimitiveType.UChar;
				return true;
			case "untyped":
				type = PrimitiveType.Untyped;
				return true;
			default:
				type = default;
				return false;
		}
	}

	static string BuildQualifiedName(QualifiedNamespaceSyntax syntax)
	{
		List<string> parts = [];

		foreach (QualifierSyntax qualifier in syntax.Qualifiers ?? [])
		{
			if (qualifier.Identifier is not null)
				parts.Add(qualifier.Identifier.Value.Value);
		}

		if (syntax.Identifier is not null)
			parts.Add(syntax.Identifier.Value.Value);

		return string.Join("::", parts);
	}

	string GetRequiredIdentifier(Token? token, SyntaxNode syntax, string message)
	{
		if (token is null)
		{
			Report(syntax, message);
			return "";
		}

		return token.Value.Value;
	}

	string SetNullableArgument(string? target, string value, SyntaxNode syntax, string name)
	{
		if (target is not null)
			Report(syntax, $"Duplicate '{name}' declarator.");

		return value;
	}

	void Report(SyntaxNode syntax, string message)
	{
		Report(GetRange(syntax), message);
	}

	void Report(TokenRange? range, string message)
	{
		diagnostics.Add(new BindDiagnostic(range, message));
	}

	static TokenRange? GetRange(SyntaxNode syntax)
	{
		return syntax switch
		{
			CompilationUnitSyntax compilationUnit => compilationUnit.Items is [CompilationUnitItemSyntax first, ..] ? GetRange(first) : null,
			CompilationUnitItemSyntax item => GetRangeOrNull(item.ImportExportDeclaration) ?? GetRangeOrNull(item.AliasDeclaration) ?? GetRangeOrNull(item.Declaration),
			AliasDeclarationSyntax alias => alias.Identifier?.Range ?? alias.AliasKeyword?.Range,
			ImportExportDeclarationSyntax declaration => declaration.Keyword?.Range,
			QualifiedNamespaceSyntax qualifiedNamespace => qualifiedNamespace.Identifier?.Range,
			QualifierSyntax qualifier => qualifier.Identifier?.Range,
			TypeDeclarationSyntax declaration => declaration.Keyword?.Range ?? declaration.Identifier?.Range,
			AttributeSyntax attribute => attribute.AttributeIdentifier?.Range,
			TypeDeclarationDeclaratorSyntax declarator => declarator.Keyword?.Range,
			GenericParameterSyntax parameter => parameter.Identifier?.Range,
			TypeDeclarationScopeSyntax scope => scope.OpenBraceToken?.Range,
			DeclarationSyntax declaration => GetRangeOrNull(declaration.TypeDeclaration) ?? GetRangeOrNull(declaration.MemberDeclaration),
			MemberDeclarationSyntax declaration => declaration.Identifier?.Range ?? declaration.TildeToken?.Range,
			MemberDeclaratorSyntax declarator => declarator.Keyword?.Range,
			ValueParameterSyntax parameter => parameter.Identifier?.Range ?? GetRangeOrNull(parameter.Type),
			WithinParameterSyntax parameter => parameter.WithinKeyword?.Range,
			ThisParameterSyntax parameter => parameter.ThisKeyword?.Range,
			SizeOfParameterSyntax parameter => parameter.SizeOfKeyword?.Range,
			NameOfParameterSyntax parameter => parameter.NameOfKeyword?.Range,
			VTableOfParameterSyntax parameter => parameter.VTableOfKeyword?.Range,
			ParameterDeclaratorSyntax declarator => declarator.Keyword?.Range,
			TypeSyntax type => GetTypeRange(type),
			AssignmentSyntax assignment => assignment.EqualsToken?.Range,
			BlockMethodBodySyntax body => body.OpenBraceToken?.Range,
			ExpressionMethodBodySyntax body => body.ArrowToken,
			MethodBodySyntax => null,
			IdentListSyntax list => list.Identifiers is [Token first, ..] ? first.Range : null,
			GenericParameterListSyntax list => list.LessThanToken?.Range,
			UnderlyingTypeListSyntax list => list.Types is [TypeSyntax first, ..] ? GetRange(first) : null,
			ParameterListSyntax list => list.OpenParenToken?.Range,
			TypeListSyntax list => list.Types is [TypeSyntax first, ..] ? GetRange(first) : null,
			ExpressionListSyntax list => list.Expressions is [ExpressionSyntax first, ..] ? GetRange(first) : null,
			ExpressionSyntax expression => GetExpressionRange(expression),
			ArgumentSyntax argument => argument.Identifier?.Range ?? argument.OutKeyword?.Range ?? argument.CatchKeyword?.Range ?? argument.WithinKeyword?.Range,
			UnaryPrefixSyntax prefix => prefix.OperatorOrKeyword ?? prefix.OpenParenToken?.Range,
			_ => null
		};
	}

	static TokenRange? GetRangeOrNull(SyntaxNode? syntax)
	{
		return syntax is null ? null : GetRange(syntax);
	}

	static TokenRange? GetTypeRange(TypeSyntax type)
	{
		return type switch
		{
			CallableTypeSyntax callable => callable.CallableKeyword?.Range,
			AttributedTypeSyntax attributed => GetRangeOrNull(attributed.Attribute) ?? GetRangeOrNull(attributed.Type),
			ArrayTypeSyntax array => GetRangeOrNull(array.ElementType) ?? array.OpenBracketToken?.Range,
			OptionalTypeSyntax optional => GetRangeOrNull(optional.ElementType) ?? optional.QuestionToken?.Range,
			PointerTypeSyntax pointer => GetRangeOrNull(pointer.ElementType) ?? pointer.StarToken?.Range,
			GenericTypeSyntax generic => GetRangeOrNull(generic.Type) ?? generic.LessThanToken?.Range,
			IterTypeSyntax iter => iter.AsyncKeyword?.Range ?? iter.StorageKeyword?.Range ?? iter.IterKeyword?.Range,
			ParamsTypeSyntax grouped => grouped.ParamsKeyword?.Range,
			StructTypeSyntax materialized => materialized.StructKeyword?.Range,
			ThrownTypeSyntax thrown => thrown.ThrownKeyword?.Range,
			DeclaratorTypeSyntax declarator => GetRangeOrNull(declarator.Declarator) ?? GetRangeOrNull(declarator.Type),
			TargetTypeSpecTypeSyntax targetSpec => targetSpec.Specifier?.Range ?? GetRangeOrNull(targetSpec.Type),
			QualifiedNameTypeSyntax named => named.Identifier?.Range,
			_ => null
		};
	}

	static TokenRange? GetExpressionRange(ExpressionSyntax expression)
	{
		return expression switch
		{
			LiteralExpressionSyntax literal => literal.Literal?.Range,
			QualifiedNameExpressionSyntax name => name.Identifier?.Range,
			ThisExpressionSyntax thisExpression => thisExpression.ThisKeyword?.Range,
			DefaultExpressionSyntax defaultExpression => defaultExpression.DefaultKeyword?.Range,
			ParenthesizedExpressionSyntax parenthesized => parenthesized.OpenParenToken?.Range,
			GroupedExpressionSyntax grouped => grouped.OpenParenToken?.Range,
			ArrayExpressionSyntax array => array.OpenBracketToken?.Range,
			CastExpressionSyntax cast => cast.OpenParenToken?.Range,
			ConstructionExpressionSyntax construction => construction.WithinKeyword?.Range ?? construction.Keyword?.Range,
			SizeOfExpressionSyntax sizeOf => sizeOf.SizeOfKeyword?.Range,
			VTableOfExpressionSyntax vtableOf => vtableOf.VTableOfKeyword?.Range,
			NameOfExpressionSyntax nameOf => nameOf.NameOfKeyword?.Range,
			SymbolOfExpressionSyntax symbolOf => symbolOf.SymbolOfKeyword?.Range,
			InitializerListSyntax initializer => initializer.OpenBraceToken?.Range,
			CommaExpressionSyntax comma => comma.Expressions is [ExpressionSyntax first, ..] ? GetRange(first) : null,
			AssignmentExpressionSyntax assignment => GetRangeOrNull(assignment.Left),
			ConditionalExpressionSyntax conditional => GetRangeOrNull(conditional.Condition),
			RangeExpressionSyntax range => GetRangeOrNull(range.Start) ?? range.DotDotToken,
			BinaryExpressionSyntax binary => GetRangeOrNull(binary.FirstExpression),
			UnaryExpressionSyntax unary => unary.Prefixes is [UnaryPrefixSyntax first, ..] ? GetRange(first) : GetRangeOrNull(unary.Expression),
			PostfixExpressionSyntax postfix => GetRangeOrNull(postfix.Expression),
			LambdaExpressionSyntax lambda => lambda.ArrowToken,
			_ => null
		};
	}
}
