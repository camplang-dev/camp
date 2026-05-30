using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Camp.Compiler;

public sealed class BindableNodeCodeSerializerOptions
{
	public string TabString { get; set; } = "\t";
}

public sealed class BindableNodeCodeSerializer
{
	readonly TextWriter writer;
	readonly string tab;
	readonly Dictionary<BindableNode, string> generatedNames = new();
	int indent;
	int generatedLocalIndex;

	BindableNodeCodeSerializer(TextWriter writer, BindableNodeCodeSerializerOptions? options)
	{
		this.writer = writer;
		tab = options?.TabString ?? "\t";
	}

	public static void Serialize(BindableNode node, TextWriter writer, BindableNodeCodeSerializerOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(writer);

		BindableNodeCodeSerializer serializer = new(writer, options);
		serializer.WriteNode(node);
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
		}
	}

	void WriteModule(Module module)
	{
		foreach (HeaderDirective directive in module.HeaderDirectives)
		{
			WriteIndent();
			writer.Write(directive.Kind == HeaderDirectiveKind.Require ? "#require " : "#include ");
			writer.WriteLine(directive.Header);
		}

		foreach (UsingDeclaration usingDeclaration in module.Usings)
		{
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

		if (module.Usings.Count > 0 && module.Definitions.Count > 0)
			writer.WriteLine();

		bool wroteDefinition = false;
		foreach (Definition definition in module.Definitions)
		{
			if (wroteDefinition && definition is TypeDefinition)
				writer.WriteLine();
			WriteDefinition(definition);
			wroteDefinition = true;
		}
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
		if (definition.Modifier != ClassModifier.None)
			writer.Write($"{Lower(definition.Modifier)} ");
		writer.Write("class ");
		writer.Write(definition.Name);
		WriteGenericParameters(definition.GenericParameters);
		WriteBaseTypes(definition.BaseTypes);
		WriteLineBlock(() =>
		{
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
		WriteBaseTypes(definition.BaseTypes);
		WriteLineBlock(() => WriteMembers(definition.Fields, definition.Functions));
	}

	void WriteInterfaceDefinition(InterfaceDefinition definition)
	{
		WriteAttributes(definition.Attributes);
		WriteIndent();
		WriteDefinitionPrefix(definition);
		writer.Write("interface ");
		writer.Write(definition.Name);
		WriteGenericParameters(definition.GenericParameters);
		WriteBaseTypes(definition.BaseTypes);
		WriteLineBlock(() =>
		{
			bool wrote = false;
			foreach (FunctionDefinition function in definition.Functions)
			{
				if (wrote)
					writer.WriteLine();
				WriteFunctionDefinition(function);
				wrote = true;
			}
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
		if (definition.UnderlyingType is not null)
			WriteType(definition.UnderlyingType);
		else
			writer.Write(definition.ResolvedType ?? "auto");
		writer.Write(" ");
		writer.Write(definition.Name);
		WriteParameterList(definition.Parameters);
		writer.WriteLine(";");
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

	void WriteVariableDefinition(VariableDefinition definition)
	{
		WriteAttributes(definition.Attributes);
		WriteIndent();
		WriteDefinitionPrefix(definition);
		WriteTypeOrResolved(definition.Type, definition.ResolvedType);
		writer.Write(" ");
		writer.Write(GetName(definition));
		if (definition.InitialValue is not null)
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
		if (definition.Modifier != FieldModifier.None)
			writer.Write($"{Lower(definition.Modifier)} ");
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
			if (definition.Modifier != FunctionModifier.None)
				writer.Write($"{Lower(definition.Modifier)} ");
			if (definition.IsAsync)
				writer.Write("async ");
			if (definition.IteratorKind != IteratorKind.None)
				writer.Write($"{Lower(definition.IteratorKind)} ");
			WriteTypeOrResolved(definition.ReturnType, definition.ResolvedType);
			writer.Write(" ");
			writer.Write(definition.Name);
		}

		WriteGenericParameters(definition.GenericParameters);
		WriteParameterList(definition.Parameters);

		if (definition.Body is null)
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
					writer.Write(", ");
					WriteType(vtableOf.InterfaceType);
				}
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
		WriteDeclarationTarget(declaration.Target);
		if (declaration.InitialValue is not null)
		{
			writer.Write(" = ");
			WriteExpression(declaration.InitialValue);
		}
	}

	void WriteDeclarationTarget(DeclarationTarget target)
	{
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

	void WriteAttributes(List<AttributeConstructor> attributes)
	{
		foreach (AttributeConstructor attribute in attributes)
		{
			WriteIndent();
			WriteAttributeName(attribute.Name);
			if (attribute.Arguments.Count > 0)
				WriteDelimited("(", ")", attribute.Arguments, WriteArgument);
			writer.WriteLine();
		}
	}

	void WriteDefinitionPrefix(Definition definition)
	{
		if (definition.Export is not null)
			writer.Write("export ");
		if (definition.Extern is not null)
			writer.Write("extern ");
	}

	void WriteGenericParameters(List<GenericParameter> parameters)
	{
		if (parameters.Count == 0)
			return;

		WriteDelimited("<", ">", parameters, parameter =>
		{
			writer.Write(parameter.Name);
			if (parameter.Constraint is not null)
			{
				writer.Write(": ");
				WriteType(parameter.Constraint);
			}
		});
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
		WriteDelimited("(", ")", parameters, WriteParameter);
	}

	void WriteParameter(ParameterDefinition parameter)
	{
		if ((parameter.Modifier == ParameterModifier.Within || parameter is WithinParameterDefinition) && parameter.Type is null)
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
		else if (parameter is SizeOfParameterDefinition)
		{
			writer.Write("nuint ");
			writer.Write(string.IsNullOrWhiteSpace(parameter.Name) ? "sizeof" : parameter.Name);
		}
		else if (parameter is VTableOfParameterDefinition vtable)
		{
			writer.Write("vtableof(");
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
				WriteDelimited("<", ">", generic.TypeArguments, WriteType);
				break;

			case ArrayTypeReference array:
				WriteType(array.ElementType);
				writer.Write("[]");
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

			case VolatileTypeReference vol:
				WriteTypeDeclarator("volatile", vol.Type);
				break;

			case AnyTypeReference:
				writer.Write("any");
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

			case CallableTypeReference callable:
				writer.Write(GetCallableKind(callable.Kind));
				writer.Write(" ");
				WriteType(callable.ReturnType);
				WriteParameterList(callable.Parameters);
				break;

			case IterTypeReference iter:
				writer.Write("iter ");
				WriteType(iter.ElementType);
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

	void WriteTypeDeclarator(string keyword, TypeReference? inner)
	{
		if (inner is PointerTypeReference or ArrayTypeReference or OptionalTypeReference or GenericTypeReference or CallableTypeReference)
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
