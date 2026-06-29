using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Camp.Compiler;

public sealed class BindableNodeCodeSerializerOptions
{
	public string TabString { get; set; } = "\t";
	public bool ApiHeader { get; set; }
}

public sealed class BindableNodeCodeSerializer
{
	readonly TextWriter writer;
	readonly string tab;
	readonly bool apiHeader;
	readonly Dictionary<BindableNode, string> generatedNames = new();
	int indent;
	int generatedLocalIndex;
	bool writingInterfaceMembers;

	BindableNodeCodeSerializer(TextWriter writer, BindableNodeCodeSerializerOptions? options)
	{
		this.writer = writer;
		tab = options?.TabString ?? "\t";
		apiHeader = options?.ApiHeader ?? false;
	}

	public static void Serialize(BindableNode node, TextWriter writer, BindableNodeCodeSerializerOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(writer);

		BindableNodeCodeSerializer serializer = new(writer, options);
		serializer.WriteNode(node);
	}

	public static string SerializeType(TypeReference? type)
	{
		using StringWriter writer = new();
		BindableNodeCodeSerializer serializer = new(writer, null);
		serializer.WriteType(type);
		return writer.ToString();
	}

	void WriteNode(BindableNode node)
	{
		switch (node)
		{
			case Module module:
				WriteModule(module);
				break;

			case Definition definition:
				WriteDefinition(definition);
				break;

			case Statement statement:
				WriteStatement(statement);
				break;

			case Expression expression:
				WriteExpression(expression);
				break;

			case TypeReference type:
				WriteType(type);
				break;
		}
	}

	void WriteModule(Module module)
	{
		bool wrotePrelude = false;
		if (!string.IsNullOrWhiteSpace(module.ExportAs))
		{
			WriteIndent();
			writer.Write("export as ");
			writer.Write(module.ExportAs);
			writer.WriteLine(";");
			wrotePrelude = true;
		}

		foreach (UsingDeclaration usingDeclaration in module.Usings)
		{
			if (wrotePrelude)
			{
				writer.WriteLine();
				wrotePrelude = false;
			}
			WriteIndent();
			writer.Write("using ");
			writer.Write(usingDeclaration.Name);
			if (!string.IsNullOrWhiteSpace(usingDeclaration.Alias))
			{
				writer.Write(" as ");
				writer.Write(usingDeclaration.Alias);
			}
			writer.WriteLine(";");
		}

		if ((!string.IsNullOrWhiteSpace(module.ExportAs) || module.Usings.Count > 0) && module.Definitions.Count > 0)
			writer.WriteLine();

		bool wroteDefinition = false;
		foreach (Definition definition in module.Definitions)
		{
			if (apiHeader && !ShouldWriteApiDefinition(definition))
				continue;
			if (wroteDefinition && definition is TypeDefinition)
				writer.WriteLine();
			WriteDefinition(definition);
			wroteDefinition = true;
		}
	}

	static bool ShouldWriteApiDefinition(Definition definition)
	{
		return definition.Export is not null;
	}

	void WriteDefinition(Definition definition)
	{
		switch (definition)
		{
			case ClassDefinition classDefinition:
				WriteClassDefinition(classDefinition);
				break;

			case StructDefinition structDefinition:
				WriteStructDefinition(structDefinition);
				break;

			case InterfaceDefinition interfaceDefinition:
				WriteInterfaceDefinition(interfaceDefinition);
				break;

			case EnumDefinition enumDefinition:
				WriteEnumDefinition(enumDefinition);
				break;

			case NewtypeDefinition newtypeDefinition:
				WriteNewtypeDefinition(newtypeDefinition);
				break;

			case ParamsDefinition paramsDefinition:
				WriteParamsDefinition(paramsDefinition);
				break;

			case AliasDefinition aliasDefinition:
				WriteAliasDefinition(aliasDefinition);
				break;

			case FunctionDefinition functionDefinition:
				WriteFunctionDefinition(functionDefinition);
				break;

			case VariableDefinition variableDefinition:
				WriteVariableDefinition(variableDefinition);
				break;
		}
	}

	void WriteClassDefinition(ClassDefinition definition)
	{
		WriteAttributes(definition.Attributes);
		WriteIndent();
		WriteDefinitionPrefix(definition);
		if (definition.IsEscaped)
			writer.Write("escaped ");
		if (apiHeader && definition.Export is not null && definition.Extern is null)
			writer.Write("extern ");
		if ((!apiHeader || definition.Export is null) && definition.Modifier != ClassModifier.None)
			writer.Write($"{Lower(definition.Modifier)} ");
		writer.Write("class ");
		writer.Write(definition.Name);
		WriteGenericParameters(definition.GenericParameters);
		WriteBaseTypes(apiHeader ? ApiBaseTypes(definition.BaseTypes, definition.LoweredInterfaceBaseTypes) : definition.BaseTypes);
		WriteLineBlock(() =>
		{
			if (apiHeader)
				WriteApiClassMembers(definition, definition.Fields, definition.Functions);
			else
				WriteMembers(definition.Fields, definition.Functions);
		});
	}

	void WriteStructDefinition(StructDefinition definition)
	{
		WriteAttributes(definition.Attributes);
		WriteIndent();
		WriteDefinitionPrefix(definition);
		if (definition.Modifier != StructModifier.None)
			writer.Write($"{Lower(definition.Modifier)} ");
		writer.Write("struct ");
		writer.Write(definition.Name);
		WriteGenericParameters(definition.GenericParameters);
		WriteBaseTypes(apiHeader ? [] : definition.BaseTypes);
		WriteLineBlock(() =>
		{
			if (apiHeader)
				WriteApiStructMembers(definition.Fields, definition.Functions);
			else
				WriteMembers(definition.Fields, definition.Functions);
		});
	}

	void WriteInterfaceDefinition(InterfaceDefinition definition)
	{
		WriteAttributes(definition.Attributes);
		WriteIndent();
		WriteDefinitionPrefix(definition);
		if (definition.IsEscaped)
			writer.Write("escaped ");
		writer.Write("interface ");
		writer.Write(definition.Name);
		WriteGenericParameters(definition.GenericParameters);
		WriteBaseTypes(definition.BaseTypes);
		WriteLineBlock(() =>
		{
			bool wrote = false;
			bool previousWritingInterfaceMembers = writingInterfaceMembers;
			writingInterfaceMembers = true;
			foreach (FunctionDefinition function in definition.Functions)
			{
				if (wrote)
					writer.WriteLine();
				WriteFunctionDefinition(function);
				wrote = true;
			}
			writingInterfaceMembers = previousWritingInterfaceMembers;
		});
	}

	void WriteEnumDefinition(EnumDefinition definition)
	{
		WriteAttributes(definition.Attributes);
		WriteIndent();
		WriteDefinitionPrefix(definition);
		writer.Write("enum ");
		writer.Write(definition.Name);
		if (definition.UnderlyingType is not null)
		{
			writer.Write(" : ");
			WriteType(definition.UnderlyingType);
		}
		WriteLineBlock(() =>
		{
			for (int i = 0; i < definition.Values.Count; i++)
			{
				VariableDefinition value = definition.Values[i];
				WriteIndent();
				writer.Write(value.Name);
				if (value.InitialValue is not null)
				{
					writer.Write(" = ");
					WriteExpression(value.InitialValue);
				}
				if (i + 1 < definition.Values.Count)
					writer.Write(",");
				writer.WriteLine();
			}
		});
	}

	void WriteNewtypeDefinition(NewtypeDefinition definition)
	{
		WriteAttributes(definition.Attributes);
		WriteIndent();
		WriteDefinitionPrefix(definition);
		writer.Write("newtype ");
		if (definition.IteratorKind != IteratorKind.None)
			writer.Write($"{Lower(definition.IteratorKind)} ");

		bool callable = definition.UnderlyingType is CallableTypeReference or IterTypeReference || definition.Parameters.Count > 0;
		if (callable)
		{
			if (definition.UnderlyingType is CallableTypeReference callableType)
				WriteCallableNewtypePrefix(callableType);
			else if (definition.UnderlyingType is not null)
				WriteType(definition.UnderlyingType);
			else
				writer.Write(definition.ResolvedType ?? "auto");
			writer.Write(" ");
		}
		writer.Write(definition.Name);
		if (callable)
			WriteParameterList(definition.Parameters);
		else if (definition.UnderlyingType is not null)
		{
			writer.Write(" : ");
			WriteType(definition.UnderlyingType);
		}

		if (definition.Functions.Count == 0 && definition.Fields.Count == 0 || apiHeader && !HasApiStaticField(definition.Fields) && !HasExportedFunction(definition.Functions))
		{
			writer.WriteLine(";");
			return;
		}

		WriteLineBlock(() =>
		{
			if (apiHeader)
				WriteApiClassMembers(definition.Fields, definition.Functions);
			else
				WriteMembers(definition.Fields, definition.Functions);
		});
	}

	void WriteCallableNewtypePrefix(CallableTypeReference callable)
	{
		writer.Write(GetCallableKind(callable.Kind));
		WriteCallableSpecs(callable.TargetSpec, callable.CallSpec);
		WriteType(callable.ReturnType);
	}

	void WriteParamsDefinition(ParamsDefinition definition)
	{
		WriteAttributes(definition.Attributes);
		WriteIndent();
		WriteDefinitionPrefix(definition);
		writer.Write("params ");
		writer.Write(definition.Name);
		WriteParameterList(definition.Components);
		writer.WriteLine(";");
	}

	void WriteAliasDefinition(AliasDefinition definition)
	{
		WriteAttributes(definition.Attributes);
		WriteIndent();
		WriteDefinitionPrefix(definition);
		writer.Write("alias ");
		writer.Write(definition.Name);
		writer.Write(" = ");
		WriteQualifiedName(definition.TargetQualifiers, definition.TargetName);
		writer.WriteLine(";");
	}

	void WriteMembers(List<FieldDefinition> fields, List<FunctionDefinition> functions)
	{
		foreach (FieldDefinition field in fields)
			WriteFieldDefinition(field);

		if (fields.Count > 0 && functions.Count > 0)
			writer.WriteLine();

		for (int i = 0; i < functions.Count; i++)
		{
			if (i > 0)
				writer.WriteLine();
			WriteFunctionDefinition(functions[i]);
		}
	}

	void WriteApiStructMembers(List<FieldDefinition> fields, List<FunctionDefinition> functions)
	{
		foreach (FieldDefinition field in fields)
		{
			if (field.Modifier == FieldModifier.Static && field.Export is null)
				continue;
			WriteFieldDefinition(field);
		}

		if (fields.Count > 0 && HasExportedFunction(functions))
			writer.WriteLine();
		WriteApiFunctions(functions);
	}

	void WriteApiClassMembers(ClassDefinition definition, List<FieldDefinition> fields, List<FunctionDefinition> functions)
	{
		WriteApiClassMembers(definition, fields, functions, allowSyntheticConstructor: true);
	}

	void WriteApiClassMembers(List<FieldDefinition> fields, List<FunctionDefinition> functions)
	{
		WriteApiClassMembers(null, fields, functions, allowSyntheticConstructor: false);
	}

	void WriteApiClassMembers(ClassDefinition? definition, List<FieldDefinition> fields, List<FunctionDefinition> functions, bool allowSyntheticConstructor)
	{
		bool wrote = false;
		foreach (FieldDefinition field in fields)
		{
			if (field.Modifier != FieldModifier.Static || field.Export is null)
				continue;
			WriteFieldDefinition(field);
			wrote = true;
		}

		bool hasSyntheticConstructor = allowSyntheticConstructor
			&& definition is not null
			&& ShouldWriteSyntheticApiConstructor(definition, functions);
		bool hasSyntheticDelete = allowSyntheticConstructor
			&& definition is not null
			&& ShouldWriteSyntheticApiDestructor(definition, functions);
		List<InterfaceDefinition> interfaceAccessors = apiHeader && definition is not null ? GetApiInterfaceAccessors(definition) : [];
		if (wrote && (HasExportedFunction(functions) || hasSyntheticConstructor || hasSyntheticDelete || interfaceAccessors.Count > 0))
			writer.WriteLine();
		if (hasSyntheticConstructor)
		{
			WriteIndent();
			writer.Write("export extern ");
			writer.Write(definition!.Name);
			writer.WriteLine("();");
			wrote = true;
		}
		if (hasSyntheticDelete)
		{
			if (wrote)
				writer.WriteLine();
			WriteIndent();
			writer.Write("export extern ~");
			writer.Write(definition!.Name);
			writer.WriteLine("();");
			wrote = true;
		}
		foreach (InterfaceDefinition interfaceDefinition in interfaceAccessors)
		{
			if (wrote)
				writer.WriteLine();
			WriteIndent();
			writer.Write("export extern ");
			writer.Write(interfaceDefinition.Name);
			writer.Write("* ");
			writer.Write("get");
			writer.Write(interfaceDefinition.Name);
			writer.WriteLine("();");
			wrote = true;
		}
		if (wrote && HasExportedFunction(functions))
			writer.WriteLine();
		WriteApiFunctions(functions);
	}

	static List<InterfaceDefinition> GetApiInterfaceAccessors(ClassDefinition definition)
	{
		List<InterfaceDefinition> interfaces = [];
		foreach (TypeReference baseType in definition.BaseTypes)
		{
			if (baseType is TypeDefinitionReference { Definition: InterfaceDefinition interfaceDefinition }
				&& interfaceDefinition.Export is not null)
			{
				interfaces.Add(interfaceDefinition);
			}
		}
		return interfaces;
	}

	static bool ShouldWriteSyntheticApiConstructor(ClassDefinition definition, List<FunctionDefinition> functions)
	{
		if (definition.Export is null
			|| definition.Extern is not null
			|| definition.Modifier == ClassModifier.Abstract)
		{
			return false;
		}

		foreach (FunctionDefinition function in functions)
		{
			if (function.Modifier == FunctionModifier.Constructor && function.Export is not null)
				return false;
		}

		return true;
	}

	static bool ShouldWriteSyntheticApiDestructor(ClassDefinition definition, List<FunctionDefinition> functions)
	{
		if (definition.Export is null)
			return false;
		if (definition.Extern is not null)
			return false;
		if (definition.Modifier == ClassModifier.Abstract)
			return false;

		foreach (FunctionDefinition function in functions)
		{
			if (function.Modifier == FunctionModifier.Destructor && function.Export is not null)
				return false;
		}

		return true;
	}

	void WriteApiFunctions(List<FunctionDefinition> functions)
	{
		WriteApiAwareFunctions(functions);
	}

	void WriteApiAwareFunctions(List<FunctionDefinition> functions)
	{
		bool wrote = false;
		foreach (FunctionDefinition function in functions)
		{
			if (apiHeader && function.Export is null)
				continue;
			if (apiHeader && IsGeneratedApiImplementationDetail(function))
				continue;
			if (apiHeader && IsOverriddenApiMethod(function))
				continue;
			if (wrote)
				writer.WriteLine();
			WriteFunctionDefinition(function);
			wrote = true;
		}
	}

	static bool IsGeneratedApiImplementationDetail(FunctionDefinition function)
	{
		return function.Provenance?.Category == GeneratedDeclarationCategory.VirtualDispatch
			|| function.GeneratedInfo?.Category == GeneratedDeclarationCategory.VirtualDispatch
			|| IsGeneratedConstructorLifecycleFunction(function)
			|| IsGeneratedVirtualImplementationFunction(function);
	}

	static bool IsGeneratedConstructorLifecycleFunction(FunctionDefinition function)
	{
		return function.Name is "op_initnew" or "create" or "op_delete" or "destroy";
	}

	static bool IsGeneratedVirtualImplementationFunction(FunctionDefinition function)
	{
		return function.Name.StartsWith("_", StringComparison.Ordinal) && function.Symbol.Contains("__", StringComparison.Ordinal);
	}

	static bool HasExportedFunction(List<FunctionDefinition> functions)
	{
		foreach (FunctionDefinition function in functions)
			if (function.Export is not null && !IsGeneratedApiImplementationDetail(function) && !IsOverriddenApiMethod(function))
				return true;
		return false;
	}

	static bool IsOverriddenApiMethod(FunctionDefinition function)
	{
		return function.Modifier is FunctionModifier.Override or FunctionModifier.Sealed;
	}

	static bool HasApiStaticField(List<FieldDefinition> fields)
	{
		foreach (FieldDefinition field in fields)
			if (field.Modifier == FieldModifier.Static && field.Export is not null)
				return true;
		return false;
	}

	void WriteVariableDefinition(VariableDefinition definition)
	{
		WriteAttributes(definition.Attributes);
		WriteIndent();
		WriteDefinitionPrefix(definition);
		if (definition.IsInline)
			writer.Write("inline ");
		if (definition.IsFixedStorage)
			writer.Write("fixed ");
		WriteTypeOrResolved(definition.Type, definition.ResolvedType);
		writer.Write(" ");
		writer.Write(GetName(definition));
		if (definition.InitialValue is not null && !ShouldSuppressApiInitializer(definition))
		{
			writer.Write(" = ");
			WriteExpression(definition.InitialValue);
		}
		writer.WriteLine(";");
	}

	void WriteFieldDefinition(FieldDefinition definition)
	{
		WriteAttributes(definition.Attributes);
		WriteIndent();
		WriteDefinitionPrefix(definition);
		if (definition.IsInline)
			writer.Write("inline ");
		else if (definition.Modifier != FieldModifier.None)
			writer.Write($"{Lower(definition.Modifier)} ");
		if (definition.IsFixedStorage)
			writer.Write("fixed ");
		WriteTypeOrResolved(definition.Type, definition.ResolvedType);
		writer.Write(" ");
		writer.Write(definition.Name);
		if (definition.InitialValue is not null)
		{
			writer.Write(" = ");
			WriteExpression(definition.InitialValue);
		}
		writer.WriteLine(";");
	}

	void WriteFunctionDefinition(FunctionDefinition definition)
	{
		WriteAttributes(definition.Attributes);
		WriteIndent();
		WriteDefinitionPrefix(definition);
		WriteCallSpec(definition.CallSpec);
		if (definition.Modifier == FunctionModifier.Constructor)
		{
			writer.Write(definition.Name);
		}
		else if (definition.Modifier == FunctionModifier.Destructor)
		{
			writer.Write(definition.Name.StartsWith("~", StringComparison.Ordinal) ? definition.Name : "~" + definition.Name);
		}
		else
		{
			if (ShouldWriteFunctionModifier(definition))
				writer.Write($"{Lower(definition.Modifier)} ");
			if (definition.IsAsync)
				writer.Write("async ");
			if (definition.IteratorKind != IteratorKind.None)
				writer.Write($"{Lower(definition.IteratorKind)} ");
			if (!TryWriteApiInterfaceAccessorReturnType(definition))
				WriteTypeOrResolved(definition.ReturnType, definition.ResolvedType);
			writer.Write(" ");
			writer.Write(definition.Name);
		}

		WriteGenericParameters(definition.GenericParameters);
		WriteParameterList(definition.Parameters);
		if (definition.CallableAscriptionType is not null)
		{
			writer.Write(" : ");
			WriteType(definition.CallableAscriptionType);
		}
		if (definition.InterfaceSlotInitializer is not null)
		{
			writer.Write(" = ");
			WriteExpression(definition.InterfaceSlotInitializer);
		}

		if (definition.Body is null || apiHeader && definition.Export is not null)
		{
			writer.WriteLine(";");
			return;
		}

		switch (definition.Body)
		{
			case BlockStatement block:
				WriteLineBlock(() =>
				{
					foreach (Statement statement in block.Statements)
						WriteStatement(statement);
				});
				break;
		}
	}

	static bool ShouldSuppressApiInitializer(VariableDefinition definition)
	{
		return definition.Export is not null && !IsConstantVariableDefinition(definition);
	}

	static bool IsConstantVariableDefinition(VariableDefinition definition)
	{
		return definition.IsInline
			|| definition.Type is ConstTypeReference
			|| definition.Type is ConstOfTypeReference
			|| (definition.ResolvedType is string type && type.StartsWith("const ", StringComparison.Ordinal));
	}

	void WriteStatement(Statement statement)
	{
		switch (statement)
		{
			case BlockStatement block:
				WriteLineBlock(() =>
				{
					foreach (Statement child in block.Statements)
						WriteStatement(child);
				}, leadingLineBreak: false);
				break;

			case EmptyStatement:
				WriteIndent();
				writer.WriteLine(";");
				break;

			case ExpressionStatement expression:
				WriteIndent();
				WriteExpression(expression.Expression);
				writer.WriteLine(";");
				break;

			case DeclarationStatement declaration:
				WriteDeclarationStatement(declaration);
				break;

			case IfStatement ifStatement:
				WriteIndentedKeywordExpressionStatement("if", ifStatement.Condition, ifStatement.Body);
				if (ifStatement.ElseBody is not null)
				{
					WriteIndent();
					writer.WriteLine("else");
					WriteNestedStatement(ifStatement.ElseBody);
				}
				break;

			case WhileStatement whileStatement:
				WriteIndentedKeywordExpressionStatement("while", whileStatement.Condition, whileStatement.Body);
				break;

			case DoWhileStatement doWhile:
				WriteIndent();
				writer.WriteLine("do");
				WriteNestedStatement(doWhile.Body);
				WriteIndent();
				writer.Write("while (");
				WriteExpression(doWhile.Condition);
				writer.WriteLine(");");
				break;

			case ForStatement forStatement:
				WriteIndent();
				writer.Write("for (");
				if (forStatement.Condition.Declaration is not null)
					WriteDeclarationInline(forStatement.Condition.Declaration);
				for (int i = 0; i < forStatement.Condition.Clauses.Count; i++)
				{
					if (i > 0 || forStatement.Condition.Declaration is not null)
						writer.Write("; ");
					WriteExpression(forStatement.Condition.Clauses[i]);
				}
				writer.WriteLine(")");
				WriteNestedStatement(forStatement.Body);
				break;

			case ForeachStatement foreachStatement:
				WriteIndent();
				writer.Write(foreachStatement.IsAwaited ? "await foreach (" : "foreach (");
				WriteDeclarationTarget(foreachStatement.Target);
				writer.Write(" in ");
				WriteExpression(foreachStatement.Source);
				writer.WriteLine(")");
				WriteNestedStatement(foreachStatement.Body);
				break;

			case SwitchStatement switchStatement:
				WriteIndent();
				writer.Write("switch (");
				WriteExpression(switchStatement.Expression);
				writer.WriteLine(")");
				WriteLineBlock(() =>
				{
					foreach (Statement child in switchStatement.Statements)
						WriteStatement(child);
				}, leadingLineBreak: false);
				break;

			case CaseStatement caseStatement:
				WriteIndent();
				writer.Write("case ");
				WriteExpression(caseStatement.Expression);
				writer.WriteLine(":");
				break;

			case DefaultStatement:
				WriteIndent();
				writer.WriteLine("default:");
				break;

			case LabelStatement labelStatement:
				WriteIndent();
				writer.Write(labelStatement.Name ?? "/* missing */");
				writer.WriteLine(":");
				break;

			case GotoStatement gotoStatement:
				WriteIndent();
				writer.Write("goto ");
				writer.Write(gotoStatement.TargetName ?? "/* missing */");
				writer.WriteLine(";");
				break;

			case BreakStatement:
				WriteSimpleStatement("break");
				break;

			case ContinueStatement:
				WriteSimpleStatement("continue");
				break;

			case ReturnStatement returnStatement:
				WriteIndent();
				writer.Write("return");
				if (returnStatement.Expression is not null)
				{
					writer.Write(" ");
					WriteExpression(returnStatement.Expression);
				}
				writer.WriteLine(";");
				break;

			case YieldStatement yieldStatement:
				WriteIndent();
				writer.Write("yield ");
				WriteExpression(yieldStatement.Expression);
				writer.WriteLine(";");
				break;

			case DeleteStatement deleteStatement:
				WriteIndent();
				writer.Write("delete ");
				WriteExpression(deleteStatement.Expression);
				writer.WriteLine(";");
				break;

			case TryStatement tryStatement:
				WriteIndent();
				writer.WriteLine("try");
				WriteNestedStatement(tryStatement.Body);
				foreach (CatchStatement catchStatement in tryStatement.Catches)
					WriteStatement(catchStatement);
				if (tryStatement.Finally is not null)
					WriteStatement(tryStatement.Finally);
				break;

			case CatchStatement catchStatement:
				WriteIndent();
				writer.Write("catch (");
				WriteDeclarationTarget(catchStatement.Target);
				writer.WriteLine(")");
				WriteNestedStatement(catchStatement.Body);
				break;

			case FinallyStatement finallyStatement:
				WriteIndent();
				writer.WriteLine("finally");
				WriteNestedStatement(finallyStatement.Body);
				break;

			case WithinStatement withinStatement:
				WriteIndent();
				writer.Write("within (");
				WriteExpression(withinStatement.Allocator);
				writer.WriteLine(")");
				WriteNestedStatement(withinStatement.Body);
				break;
		}
	}

	void WriteExpression(Expression? expression, int parentPrecedence = 0)
	{
		if (expression is null)
		{
			writer.Write("/* missing */");
			return;
		}

		int precedence = GetPrecedence(expression);
		bool needsParens = precedence > 0 && precedence < parentPrecedence;
		if (needsParens)
			writer.Write("(");

		switch (expression)
		{
			case LiteralExpression literal:
				WriteLiteral(literal);
				break;

			case SymbolOfExpression symbolOf:
				writer.Write("symbolof(");
				writer.Write(symbolOf.Text);
				writer.Write(")");
				break;

			case NamedExpression named:
				WriteQualifiedName(named.Qualifiers, named.Name);
				break;

			case VariableReferenceExpression variable:
				writer.Write(GetName(variable.Variable));
				break;

			case MethodReferenceExpression method:
				writer.Write(method.Candidates.Count == 1 ? GetFunctionReferenceName(method.Candidates[0]) : "/* method */");
				break;

			case TypeReferenceExpression type:
				WriteType(type.Type);
				break;

			case ThisExpression:
				writer.Write("this");
				break;

			case DefaultExpression defaultExpression:
				writer.Write("default");
				if (defaultExpression.Type is not null)
				{
					writer.Write("(");
					WriteType(defaultExpression.Type);
					writer.Write(")");
				}
				break;

			case GroupedExpression grouped:
				WriteGroupedExpression(grouped);
				break;

			case ArrayExpression array:
				WriteDelimited("[", "]", array.Elements, item => WriteExpression(item));
				break;

			case InitializerExpression initializer:
				WriteInitializer(initializer);
				break;

			case ParenthesizedExpression parenthesized:
				writer.Write("(");
				WriteExpression(parenthesized.Expression);
				writer.Write(")");
				break;

			case CastExpression cast:
				writer.Write("(");
				if (cast.LifetimeCastKind is not null)
					WriteLifetimeCastDeclarator(cast);
				else
					WriteType(cast.Type);
				writer.Write(")");
				WriteExpression(cast.Expression, GetPrecedence(cast));
				break;

			case ConstructionExpression construction:
				WriteConstructionExpression(construction);
				break;

			case CurrentAllocatorExpression:
				writer.Write("__currentAllocator");
				break;

			case WithinExpression within:
				writer.Write("within (");
				WriteExpression(within.Context);
				writer.Write(") ");
				WriteExpression(within.Expression, GetPrecedence(within));
				break;

			case SizeOfExpression sizeOf:
				writer.Write("sizeof(");
				WriteType(sizeOf.Type);
				writer.Write(")");
				break;

			case VTableOfExpression vtableOf:
				writer.Write("vtableof(");
				WriteType(vtableOf.Type);
				if (vtableOf.InterfaceType is not null)
				{
					writer.Write(": ");
					WriteType(vtableOf.InterfaceType);
				}
				writer.Write(")");
				break;

			case NameOfExpression nameOf:
				writer.Write("typenameof(");
				writer.Write(nameOf.Text);
				writer.Write(")");
				break;

			case LambdaExpression lambda:
				WriteLambda(lambda);
				break;

			case ArgumentExpression argument:
				WriteArgument(argument);
				break;

			case CallExpression call:
				WriteExpression(call.Target, GetPrecedence(call));
				if (call.TypeArguments.Count > 0)
					WriteDelimited("<", ">", call.TypeArguments, WriteType);
				WriteDelimited("(", ")", call.Arguments, WriteArgument);
				break;

			case IndexExpression index:
				WriteExpression(index.Target, GetPrecedence(index));
				WriteDelimited("[", "]", index.Arguments, WriteArgument);
				break;

			case MemberExpression member:
				WriteExpression(member.Target, GetPrecedence(member));
				writer.Write(".");
				writer.Write(member.Name);
				break;

			case MemberReferenceExpression member:
				WriteExpression(member.Target, GetPrecedence(member));
				writer.Write(".");
				writer.Write(member.Name);
				break;

			case NamelessIndexerExpression nameless:
				WriteExpression(nameless.Target, GetPrecedence(nameless));
				WriteDelimited("[", "]", nameless.Arguments, WriteArgument);
				break;

			case UnaryExpression unary:
				WriteUnaryExpression(unary);
				break;

			case PostfixUpdateExpression postfix:
				WriteExpression(postfix.Expression, GetPrecedence(postfix));
				writer.Write(postfix.Operator == UpdateOperator.Increment ? "++" : "--");
				break;

			case FinallyDeleteExpression finallyDelete:
				writer.Write("finally delete ");
				WriteExpression(finallyDelete.Expression, GetPrecedence(finallyDelete));
				break;

			case BinaryExpression binary:
				WriteExpression(binary.Left, precedence);
				writer.Write(" ");
				writer.Write(GetBinaryOperator(binary.Operator));
				writer.Write(" ");
				WriteExpression(binary.Right, precedence + 1);
				break;

			case AssignmentExpression assignment:
				WriteExpression(assignment.Target, precedence);
				writer.Write(" ");
				writer.Write(GetAssignmentOperator(assignment.Operator));
				writer.Write(" ");
				WriteExpression(assignment.Value, precedence);
				break;

			case ConditionalExpression conditional:
				WriteExpression(conditional.Condition, precedence);
				writer.Write(" ? ");
				WriteExpression(conditional.WhenTrue);
				writer.Write(" : ");
				WriteExpression(conditional.WhenFalse);
				break;

			case RangeExpression range:
				WriteExpression(range.Start, precedence);
				writer.Write("..");
				WriteExpression(range.End, precedence);
				break;
		}

		if (needsParens)
			writer.Write(")");
	}

	void WriteDeclarationStatement(DeclarationStatement declaration)
	{
		WriteIndent();
		WriteDeclarationInline(declaration);
		writer.WriteLine(";");
	}

	void WriteDeclarationInline(DeclarationStatement declaration)
	{
		WriteDeclarationTarget(declaration.Target, declaration.IsFixedStorage);
		if (declaration.InitialValue is not null)
		{
			writer.Write(" = ");
			WriteExpression(declaration.InitialValue);
		}
	}

	void WriteDeclarationTarget(DeclarationTarget target, bool isFixedStorage = false)
	{
		if (isFixedStorage)
			writer.Write("fixed ");
		WriteTypeOrResolved(target.Type, target.ResolvedType);
		writer.Write(" ");
		for (int i = 0; i < target.Names.Count; i++)
		{
			if (i > 0)
				writer.Write(", ");
			writer.Write(GetGeneratedName(target, target.Names[i]));
		}
	}

	void WriteNestedStatement(Statement? statement)
	{
		if (statement is null)
		{
			indent++;
			WriteIndent();
			writer.WriteLine(";");
			indent--;
			return;
		}

		if (statement is BlockStatement)
		{
			WriteStatement(statement);
			return;
		}

		indent++;
		WriteStatement(statement);
		indent--;
	}

	void WriteIndentedKeywordExpressionStatement(string keyword, Expression? expression, Statement? body)
	{
		WriteIndent();
		writer.Write(keyword);
		writer.Write(" (");
		WriteExpression(expression);
		writer.WriteLine(")");
		WriteNestedStatement(body);
	}

	void WriteSimpleStatement(string keyword)
	{
		WriteIndent();
		writer.Write(keyword);
		writer.WriteLine(";");
	}

	void WriteLineBlock(Action writeBody, bool leadingLineBreak = true)
	{
		if (leadingLineBreak)
			writer.WriteLine();
		WriteIndent();
		writer.WriteLine("{");
		indent++;
		writeBody();
		indent--;
		WriteIndent();
		writer.WriteLine("}");
	}

	void WriteAttributes(List<AttributeConstructor> attributes, bool inline = false)
	{
		foreach (AttributeConstructor attribute in attributes)
		{
			if (!inline)
				WriteIndent();
			WriteAttributeName(attribute.Name);
			if (attribute.Arguments.Count > 0)
				WriteDelimited("(", ")", attribute.Arguments, WriteArgument);
			if (inline)
				writer.Write(" ");
			else
				writer.WriteLine();
		}
	}

	void WriteDefinitionPrefix(Definition definition)
	{
		if (definition.Export is not null)
			writer.Write("export ");
		else if (definition.Public is not null)
			writer.Write("public ");
		if (ShouldWriteExternPrefix(definition))
			writer.Write("extern ");
	}

	bool ShouldWriteExternPrefix(Definition definition)
	{
		if (writingInterfaceMembers)
			return false;
		if (definition.Extern is not null)
			return !writingInterfaceMembers;
		if (!apiHeader || definition.Export is null)
			return false;

		return definition switch
		{
			FunctionDefinition function => function.Export is not null || IsLifecycleFunction(function) || function.Body is not null || function.Modifier is FunctionModifier.Override or FunctionModifier.Sealed,
			VariableDefinition variable => !IsConstantVariableDefinition(variable),
			_ => false
		};
	}

	static bool IsLifecycleFunction(FunctionDefinition definition)
	{
		return definition.Modifier is FunctionModifier.Constructor or FunctionModifier.Destructor;
	}

	bool ShouldWriteFunctionModifier(FunctionDefinition definition)
	{
		if (!apiHeader)
			return definition.Modifier != FunctionModifier.None;

		return definition.Modifier is not (FunctionModifier.None or FunctionModifier.Virtual or FunctionModifier.Abstract);
	}

	bool TryWriteApiInterfaceAccessorReturnType(FunctionDefinition definition)
	{
		if (!apiHeader || definition.SourceSyntax is not null || !definition.Name.StartsWith("get", StringComparison.Ordinal))
			return false;
		if (definition.ReturnType is not PointerTypeReference
			{
				ElementType: PointerTypeReference
				{
					ElementType: TypeDefinitionReference { Definition: InterfaceDefinition } interfaceType
				}
			})
		{
			return false;
		}

		WriteType(interfaceType);
		writer.Write("*");
		return true;
	}

	void WriteGenericParameters(List<GenericParameter> parameters)
	{
		if (parameters.Count == 0)
			return;

		WriteDelimited("<", ">", parameters, parameter =>
		{
			if (parameter.Attributes.Count > 0)
				WriteAttributes(parameter.Attributes, inline: true);
			writer.Write(parameter.Name);
			if (parameter.Constraint is not null)
			{
				writer.Write(": ");
				if (parameter.RequiresImplementation)
					writer.Write("implements ");
				WriteType(parameter.Constraint);
			}
		});
	}

	static List<TypeReference> ApiBaseTypes(List<TypeReference> baseTypes, List<TypeReference> loweredInterfaceBaseTypes)
	{
		if (loweredInterfaceBaseTypes.Count == 0)
			return baseTypes;

		List<TypeReference> types = [.. baseTypes];
		types.AddRange(loweredInterfaceBaseTypes);
		return types;
	}

	void WriteBaseTypes(List<TypeReference> types)
	{
		if (types.Count == 0)
			return;

		writer.Write(" : ");
		for (int i = 0; i < types.Count; i++)
		{
			if (i > 0)
				writer.Write(", ");
			WriteType(types[i]);
		}
	}

	void WriteParameterList(List<ParameterDefinition> parameters)
	{
		WriteDelimited("(", ")", apiHeader ? FilterApiParameters(parameters) : parameters, WriteParameter);
	}

	static List<ParameterDefinition> FilterApiParameters(List<ParameterDefinition> parameters)
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

	void WriteIterType(IterTypeReference iter)
	{
		if (iter.IsAsync)
			writer.Write("async ");
		writer.Write("iter");
		if (iter.Parameters.Count == 0)
		{
			writer.Write(" ");
			WriteType(iter.ElementType);
			return;
		}

		WriteDelimited("(", ")", iter.Parameters, WriteIterSlot);
	}

	void WriteIterSlot(ParameterDefinition parameter)
	{
		if (parameter.Modifier == ParameterModifier.Thrown)
			writer.Write("thrown ");
		WriteTypeOrResolved(parameter.Type, parameter.ResolvedType);
		if (!string.IsNullOrWhiteSpace(parameter.Name))
		{
			writer.Write(" ");
			writer.Write(parameter.Name);
		}
	}

	void WriteParameter(ParameterDefinition parameter)
	{
		WriteAttributes(parameter.Attributes, inline: true);
		if (parameter.IsOverloadSelector)
			writer.Write("overload ");
		bool isWithin = parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition;
		if (isWithin)
			writer.Write("within ");
		else if (parameter.Modifier == ParameterModifier.Out)
			writer.Write("out ");
		else if (parameter.Modifier == ParameterModifier.Thrown)
			writer.Write("thrown ");
		else if (parameter.Modifier == ParameterModifier.In)
			writer.Write("in ");

		if (parameter is ThisParameterDefinition)
		{
			WriteThisParameter((ThisParameterDefinition)parameter);
		}
		else if (parameter is SizeOfParameterDefinition sizeOf)
		{
			writer.Write("sizeof(");
			WriteType(sizeOf.Type);
			writer.Write(")");
		}
		else if (parameter is NameOfParameterDefinition nameOf)
		{
			writer.Write("typenameof(");
			WriteType(nameOf.Type);
			writer.Write(")");
		}
		else if (parameter is VTableOfParameterDefinition vtable)
		{
			writer.Write("vtableof(");
			WriteType(vtable.Type);
			writer.Write(": ");
			WriteType(vtable.InterfaceType);
			writer.Write(")");
		}
		else
		{
			if (parameter is WithinParameterDefinition && parameter.Type is null)
			{
				writer.Write(parameter.Name);
			}
			else
			{
				WriteTypeOrResolved(parameter.Type, parameter.ResolvedType);
				writer.Write(" ");
				writer.Write(parameter.Name);
			}
		}

		if (parameter.DefaultValue is not null)
		{
			writer.Write(" = ");
			WriteExpression(parameter.DefaultValue);
		}
	}

	void WriteTypeOrResolved(TypeReference? type, string? resolvedType)
	{
		if (type is not null)
			WriteType(type);
		else
			writer.Write(string.IsNullOrWhiteSpace(resolvedType) ? "auto" : SanitizeTypeName(resolvedType));
	}

	void WriteThisParameter(ThisParameterDefinition parameter)
	{
		if (parameter.SourceSyntax is ThisParameterSyntax { Declarators: { Count: > 0 } declarators })
		{
			for (int i = 0; i < declarators.Count; i++)
			{
				if (i > 0)
					writer.Write(" ");
				WriteTypeDeclaratorSyntax(declarators[i]);
			}
			writer.Write(" this");
			return;
		}

		if (parameter.SourceSyntax is ThisParameterSyntax)
		{
			writer.Write("this");
			return;
		}

		WriteTypeOrResolved(parameter.Type, parameter.ResolvedType);
		writer.Write(" this");
	}

	void WriteTypeDeclaratorSyntax(TypeDeclaratorSyntax declarator)
	{
		writer.Write(declarator.Keyword?.Value ?? "");
		if (declarator.AnchorList?.Identifiers is { Count: > 0 } anchors)
		{
			writer.Write("(");
			for (int i = 0; i < anchors.Count; i++)
			{
				if (i > 0)
					writer.Write(", ");
				writer.Write(anchors[i].Value);
			}
			writer.Write(")");
		}
	}

	void WriteLifetimeCastDeclarator(CastExpression cast)
	{
		writer.Write(cast.LifetimeCastKind);
		if (cast.LifetimeCastAnchors.Count == 0)
			return;

		writer.Write("(");
		for (int i = 0; i < cast.LifetimeCastAnchors.Count; i++)
		{
			if (i > 0)
				writer.Write(", ");
			writer.Write(cast.LifetimeCastAnchors[i]);
		}
		writer.Write(")");
	}

	void WriteType(TypeReference? type)
	{
		if (type is null)
		{
			writer.Write("auto");
			return;
		}

		switch (type)
		{
			case NamedTypeReference named:
				WriteQualifiedName(named.Qualifiers, named.Name);
				if (named.TypeArguments.Count > 0)
					WriteDelimited("<", ">", named.TypeArguments, WriteType);
				break;

			case TypeDefinitionReference definition:
				writer.Write(definition.Name);
				if (definition.TypeArguments.Count > 0)
					WriteDelimited("<", ">", definition.TypeArguments, WriteType);
				break;

			case GenericParameterTypeReference generic:
				writer.Write(generic.Name);
				break;

			case AllocatorTypeReference:
				writer.Write("Allocator*");
				break;

			case ClassTypeReference:
				writer.Write("classtype");
				break;

			case ThisTypeReference:
				writer.Write("this");
				break;

			case AttributedTypeReference attributed:
				if (attributed.Attribute is not null)
				{
					WriteAttributeName(attributed.Attribute.Name);
					writer.Write(" ");
				}
				WriteType(attributed.Type);
				break;

			case GenericTypeReference generic:
				WriteType(generic.Type);
				if (generic.TypeArguments.Count > 0)
					WriteDelimited("<", ">", generic.TypeArguments, WriteType);
				break;

			case ArrayTypeReference array:
				WriteType(array.ElementType);
				writer.Write("[]");
				break;

			case FixedArrayTypeReference fixedArray:
				WriteFixedArrayType(fixedArray);
				break;

			case OptionalTypeReference optional:
				WriteType(optional.ElementType);
				writer.Write("?");
				break;

			case PointerTypeReference pointer:
				WriteType(pointer.ElementType);
				writer.Write("*");
				break;

			case ConstTypeReference constant:
				WriteTypeDeclarator("const", constant.Type);
				break;

			case ConstOfTypeReference constOf:
				WriteTypeDeclarator($"constof({constOf.AnchorName})", constOf.Type);
				break;

			case VolatileTypeReference vol:
				WriteTypeDeclarator("volatile", vol.Type);
				break;

			case AnyTypeReference:
				writer.Write("any");
				break;

			case CopyableTypeReference:
				writer.Write("copyable");
				break;

			case AutoTypeReference:
				writer.Write("auto");
				break;

			case PrimitiveTypeReference primitive:
				writer.Write(GetPrimitiveName(primitive.Type));
				break;

			case EscapedTypeReference escaped:
				WriteTypeDeclarator("escaped", escaped.Type);
				break;

			case ScopedTypeReference scoped:
				WriteTypeDeclarator(GetAnchoredDeclarator("scoped", scoped.Anchors), scoped.Type);
				break;

			case UnscopedTypeReference unscoped:
				WriteTypeDeclarator(GetAnchoredDeclarator("unscoped", unscoped.Anchors), unscoped.Type);
				break;

			case TargetTypeSpecTypeReference targetSpec:
				WriteType(targetSpec.Type);
				writer.Write(" ");
				writer.Write(targetSpec.Specifier);
				break;

			case CallableTypeReference callable:
				writer.Write(GetCallableKind(callable.Kind));
				WriteCallableSpecs(callable.TargetSpec, callable.CallSpec);
				WriteType(callable.ReturnType);
				WriteParameterList(callable.Parameters);
				break;

			case IterTypeReference iter:
				WriteIterType(iter);
				break;

			case GroupedParamsTypeReference grouped:
				writer.Write("params(");
				WriteType(grouped.StructType);
				writer.Write(")");
				break;

			case MaterializedStructTypeReference materialized:
				writer.Write("struct(");
				WriteType(materialized.ParamsType);
				writer.Write(")");
				break;

			case ThrownTypeReference thrown:
				writer.Write("thrown(");
				WriteType(thrown.Type);
				writer.Write(")");
				break;
		}
	}

	void WriteFixedArrayType(FixedArrayTypeReference fixedArray)
	{
		List<FixedArrayTypeReference> dimensions = [];
		TypeReference? element = fixedArray;
		while (element is FixedArrayTypeReference current)
		{
			dimensions.Add(current);
			element = current.ElementType;
		}

		WriteType(element);
		foreach (FixedArrayTypeReference dimension in dimensions)
		{
			writer.Write("[");
			if (dimension.LengthExpression is not null)
				WriteExpression(dimension.LengthExpression);
			else if (dimension.Length is long length)
				writer.Write(length.ToString(System.Globalization.CultureInfo.InvariantCulture));
			writer.Write("]");
		}
	}

	void WriteTypeDeclarator(string keyword, TypeReference? inner)
	{
		if (inner is CallableTypeReference)
		{
			writer.Write(keyword);
			writer.Write(" ");
			WriteType(inner);
			return;
		}

		if (inner is PointerTypeReference or ArrayTypeReference or FixedArrayTypeReference or OptionalTypeReference or GenericTypeReference)
		{
			WriteType(inner);
			writer.Write(" ");
			writer.Write(keyword);
			return;
		}

		writer.Write(keyword);
		writer.Write(" ");
		WriteType(inner);
	}

	void WriteCallSpec(string? callSpec, bool leadingSpace = false)
	{
		if (string.IsNullOrWhiteSpace(callSpec))
			return;

		if (leadingSpace)
			writer.Write(" ");
		writer.Write(callSpec);
		writer.Write(" ");
	}

	void WriteCallableSpecs(string? targetSpec, string? callSpec)
	{
		if (!string.IsNullOrWhiteSpace(targetSpec))
		{
			writer.Write(" ");
			writer.Write(targetSpec);
		}
		if (!string.IsNullOrWhiteSpace(callSpec))
		{
			writer.Write(" ");
			writer.Write(callSpec);
		}
		writer.Write(" ");
	}

	static string GetAnchoredDeclarator(string keyword, List<string> anchors)
	{
		return anchors.Count == 0 ? keyword : $"{keyword}({string.Join(", ", anchors)})";
	}

	void WriteLiteral(LiteralExpression literal)
	{
		switch (literal.Kind)
		{
			case LiteralKind.String:
				writer.Write(literal.Text);
				break;
			case LiteralKind.True:
				writer.Write("true");
				break;
			case LiteralKind.False:
				writer.Write("false");
				break;
			case LiteralKind.Null:
				writer.Write("null");
				break;
			default:
				writer.Write(literal.Text);
				break;
		}
	}

	void WriteGroupedExpression(GroupedExpression grouped)
	{
		writer.Write("(");
		for (int i = 0; i < grouped.Items.Count; i++)
		{
			if (i > 0)
				writer.Write(", ");
			GroupedExpressionItem item = grouped.Items[i];
			if (!string.IsNullOrWhiteSpace(item.Name))
			{
				writer.Write(item.Name);
				writer.Write(": ");
			}
			WriteExpression(item.Expression);
		}
		writer.Write(")");
	}

	void WriteInitializer(InitializerExpression initializer)
	{
		writer.Write("{");
		for (int i = 0; i < initializer.Items.Count; i++)
		{
			if (i > 0)
				writer.Write(", ");
			InitializerItem item = initializer.Items[i];
			if (item.Target is not null)
			{
				WriteInitializerTarget(item.Target);
				writer.Write(" = ");
			}
			WriteExpression(item.Expression);
		}
		writer.Write("}");
	}

	void WriteInitializerTarget(InitializerTarget target)
	{
		for (int i = 0; i < target.Parts.Count; i++)
		{
			InitializerTargetPart part = target.Parts[i];
			writer.Write(".");
			writer.Write(part.Name);
			if (part.Arguments.Count > 0)
				WriteDelimited("[", "]", part.Arguments, WriteArgument);
		}
	}

	void WriteConstructionExpression(ConstructionExpression construction)
	{
		writer.Write(construction.Kind == ConstructionKind.Init ? "init " : "new ");
		WriteType(construction.Type);
		if (construction.ElementCount is not null)
		{
			writer.Write("[");
			WriteExpression(construction.ElementCount);
			writer.Write("]");
		}
		WriteDelimited("(", ")", construction.Arguments, WriteArgument);
		if (construction.Initializer is not null)
		{
			writer.Write(" ");
			WriteInitializer(construction.Initializer);
		}
	}

	void WriteLambda(LambdaExpression lambda)
	{
		WriteDelimited("(", ")", lambda.Parameters, parameter =>
		{
			if (parameter.Parameter is not null)
				WriteParameter(parameter.Parameter);
			else
				writer.Write(parameter.Name);
		});
		writer.Write(" => ");
		switch (lambda.Body)
		{
			case BlockStatement block:
				writer.WriteLine();
				WriteIndent();
				WriteLineBlock(() =>
				{
					foreach (Statement statement in block.Statements)
						WriteStatement(statement);
				});
				break;
		}
	}

	void WriteArgument(ArgumentExpression argument)
	{
		if (!string.IsNullOrWhiteSpace(argument.Name))
		{
			writer.Write(argument.Name);
			writer.Write(": ");
		}
		if (argument.Modifier == ArgumentModifier.Out)
			writer.Write("out ");
		else if (argument.Modifier == ArgumentModifier.Catch)
			writer.Write("catch ");
		if (argument.Type is not null)
		{
			WriteType(argument.Type);
			writer.Write(" ");
		}
		if (argument.Target is not null)
			WriteDeclarationTarget(argument.Target);
		else
			WriteExpression(argument.Value);
	}

	void WriteUnaryExpression(UnaryExpression unary)
	{
		switch (unary.Operator)
		{
			case UnaryOperator.AddressOf:
				writer.Write("&");
				break;
			case UnaryOperator.PointerDereference:
				writer.Write("*");
				break;
			case UnaryOperator.LogicalNot:
				writer.Write("!");
				break;
			case UnaryOperator.BitwiseNot:
				writer.Write("~");
				break;
			case UnaryOperator.Minus:
				writer.Write("-");
				break;
			case UnaryOperator.Plus:
				writer.Write("+");
				break;
			case UnaryOperator.Await:
				writer.Write("await ");
				break;
			case UnaryOperator.Throw:
				writer.Write("throw ");
				break;
			case UnaryOperator.Within:
				writer.Write("within ");
				break;
			case UnaryOperator.FromEnd:
				writer.Write("^");
				break;
		}
		WriteExpression(unary.Operand, GetPrecedence(unary));
	}

	void WriteDelimited<T>(string open, string close, IReadOnlyList<T> items, Action<T> writeItem)
	{
		writer.Write(open);
		for (int i = 0; i < items.Count; i++)
		{
			if (i > 0)
				writer.Write(", ");
			writeItem(items[i]);
		}
		writer.Write(close);
	}

	void WriteQualifiedName(List<string> qualifiers, string name)
	{
		for (int i = 0; i < qualifiers.Count; i++)
		{
			if (i > 0)
				writer.Write("::");
			writer.Write(qualifiers[i]);
		}
		if (qualifiers.Count > 0)
			writer.Write("::");
		writer.Write(name);
	}

	void WriteAttributeName(string name)
	{
		if (!name.StartsWith("@", StringComparison.Ordinal))
			writer.Write("@");
		writer.Write(name);
	}

	void WriteIndent()
	{
		for (int i = 0; i < indent; i++)
			writer.Write(tab);
	}

	string GetName(BindableNode? node)
	{
		return node switch
		{
			null => "/* missing */",
			DeclarationTarget target => target.Names.Count == 1 ? GetGeneratedName(target, target.Names[0]) : string.Join(", ", target.Names),
			ParameterDefinition parameter => GetGeneratedName(parameter, parameter.Name),
			Definition definition => GetGeneratedName(definition, definition.Name),
			LambdaParameter parameter => parameter.Name ?? parameter.Parameter?.Name ?? "/* lambda */",
			_ => "/* node */"
		};
	}

	static string GetFunctionReferenceName(FunctionDefinition function)
	{
		return string.IsNullOrWhiteSpace(function.Symbol) ? function.Name : function.Symbol;
	}

	string GetGeneratedName(BindableNode node, string name)
	{
		if (!name.StartsWith("#", StringComparison.Ordinal))
			return name;
		if (!generatedNames.TryGetValue(node, out string? generated))
		{
			generated = "__local" + generatedLocalIndex.ToString(CultureInfo.InvariantCulture);
			generatedLocalIndex++;
			generatedNames[node] = generated;
		}
		return generated;
	}

	static string Lower<T>(T value) where T : Enum
	{
		return value.ToString().ToLowerInvariant();
	}

	static string SanitizeTypeName(string? type)
	{
		return string.IsNullOrWhiteSpace(type) || type.StartsWith("#", StringComparison.Ordinal) ? "auto" : type;
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
			_ => "auto"
		};
	}

	static string GetCallableKind(CallableKind kind)
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

	static string GetBinaryOperator(BinaryOperator op)
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

	static string GetAssignmentOperator(AssignmentOperator op)
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

	static int GetPrecedence(Expression expression)
	{
		return expression switch
		{
			AssignmentExpression => 1,
			ConditionalExpression => 2,
			RangeExpression => 3,
			BinaryExpression binary => GetBinaryPrecedence(binary.Operator),
			UnaryExpression or CastExpression or FinallyDeleteExpression or WithinExpression => 12,
			CallExpression or IndexExpression or MemberExpression or MemberReferenceExpression or NamelessIndexerExpression or PostfixUpdateExpression => 13,
			_ => 14
		};
	}

	static int GetBinaryPrecedence(BinaryOperator op)
	{
		return op switch
		{
			BinaryOperator.LogicalOr => 4,
			BinaryOperator.NullCoalescing => 4,
			BinaryOperator.LogicalAnd => 5,
			BinaryOperator.BitwiseOr => 6,
			BinaryOperator.BitwiseXor => 7,
			BinaryOperator.BitwiseAnd => 8,
			BinaryOperator.Equal or BinaryOperator.NotEqual => 9,
			BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual => 10,
			BinaryOperator.LeftShift or BinaryOperator.RightShift => 11,
			BinaryOperator.Add or BinaryOperator.Subtract => 12,
			BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo => 13,
			_ => 4
		};
	}
}
