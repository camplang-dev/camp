using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void ExpandParamsDeclarations(Module module)
	{
		for (int i = 0; i < module.Definitions.Count; i++)
		{
			switch (module.Definitions[i])
			{
				case VariableDefinition variable:
					if (TryExpandParamsVariable(variable, out List<VariableDefinition>? variables))
					{
						module.Definitions.RemoveAt(i);
						module.Definitions.InsertRange(i, variables);
						i += variables.Count - 1;
					}
					break;

				case ClassDefinition classDefinition:
					ExpandParamsFields(classDefinition.Fields);
					foreach (FunctionDefinition function in classDefinition.Functions)
						ExpandParamsFunctionDeclarations(function);
					break;

				case StructDefinition structDefinition:
					ExpandParamsFields(structDefinition.Fields);
					foreach (FunctionDefinition function in structDefinition.Functions)
						ExpandParamsFunctionDeclarations(function);
					break;

				case InterfaceDefinition interfaceDefinition:
					foreach (FunctionDefinition function in interfaceDefinition.Functions)
						ExpandParamsFunctionDeclarations(function);
					break;

				case EnumDefinition enumDefinition:
					foreach (FunctionDefinition function in enumDefinition.Functions)
						ExpandParamsFunctionDeclarations(function);
					break;

				case NewtypeDefinition newtypeDefinition:
					foreach (FunctionDefinition function in newtypeDefinition.Functions)
						ExpandParamsFunctionDeclarations(function);
					break;

				case ParamsDefinition paramsDefinition:
					foreach (FunctionDefinition function in paramsDefinition.Functions)
						ExpandParamsFunctionDeclarations(function);
					break;

				case FunctionDefinition function:
					ExpandParamsFunctionDeclarations(function);
					break;
			}
		}
	}

	void ExpandParamsFunctionDeclarations(FunctionDefinition function)
	{
		ExpandParamsReturn(function);
		ExpandParamsParameters(function.Parameters);
		if (function.Body is not null)
			ExpandParamsLocalDeclarations(function.Body.Statements);
	}

	void ExpandParamsReturn(FunctionDefinition function)
	{
		if (function.ReturnType is null || !TryGetParamsComponentShape(function.ReturnType, function.ResolvedType, "result", out ParamsComponentShape shape))
			return;
		if (shape.Components.Count == 0)
			return;

		expandedReturnShapes[function] = shape;
		ParamsComponent first = shape.Components[0];
		function.ReturnType = new NamedTypeReference { Name = first.Type, ResolvedType = first.Type };
		function.ResolvedType = first.Type;

		for (int i = 1; i < shape.Components.Count; i++)
		{
			ParamsComponent component = shape.Components[i];
			function.Parameters.Add(new ParameterDefinition
			{
				SourceSyntax = function.SourceSyntax,
				Name = component.ExpandedName,
				Symbol = component.ExpandedName,
				Modifier = ParameterModifier.Out,
				Type = new NamedTypeReference { Name = component.Type, ResolvedType = component.Type },
				ResolvedType = component.Type
			});
		}
	}

	void ExpandParamsParameters(List<ParameterDefinition> parameters)
	{
		for (int i = 0; i < parameters.Count; i++)
		{
			ParameterDefinition parameter = parameters[i];
			if (parameter is ThisParameterDefinition or WithinParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition)
				continue;
			if (!TryGetParamsComponentShape(parameter.Type, parameter.ResolvedType, parameter.Name, out ParamsComponentShape shape))
				continue;

			List<ParameterDefinition> components = [];
			foreach (ParamsComponent component in shape.Components)
				components.Add(CreateExpandedParameter(parameter, component));
			RegisterParamsExpansion(parameter, shape, components);

			parameters.RemoveAt(i);
			parameters.InsertRange(i, components);
			i += components.Count - 1;
		}
	}

	void ExpandParamsFields(List<FieldDefinition> fields)
	{
		for (int i = 0; i < fields.Count; i++)
		{
			FieldDefinition field = fields[i];
			if (!TryGetParamsComponentShape(field.Type, field.ResolvedType, field.Name, out ParamsComponentShape shape))
				continue;

			List<FieldDefinition> components = [];
			List<Expression?> initialValues = GetParamsComponentInitialValues(field.InitialValue, shape, deferCurrentAllocator: false);
			for (int componentIndex = 0; componentIndex < shape.Components.Count; componentIndex++)
				components.Add(CreateExpandedField(field, shape.Components[componentIndex], initialValues[componentIndex]));
			RegisterParamsExpansion(field, shape, components);

			fields.RemoveAt(i);
			fields.InsertRange(i, components);
			i += components.Count - 1;
		}
	}

	void ExpandParamsLocalDeclarations(List<Statement> statements)
	{
		for (int i = 0; i < statements.Count; i++)
		{
			switch (statements[i])
			{
				case BlockStatement block:
					ExpandParamsLocalDeclarations(block.Statements);
					break;

				case DeclarationStatement declaration when TryExpandParamsDeconstruction(declaration, out List<Statement>? declarations):
					statements.RemoveAt(i);
					statements.InsertRange(i, declarations);
					i += declarations.Count - 1;
					break;

				case DeclarationStatement declaration when TryExpandParamsLocalDeclaration(declaration, out List<Statement>? declarations):
					statements.RemoveAt(i);
					statements.InsertRange(i, declarations);
					i += declarations.Count - 1;
					break;

				case IfStatement ifStatement:
					ExpandParamsNestedStatement(ifStatement.Body);
					ExpandParamsNestedStatement(ifStatement.ElseBody);
					break;

				case WhileStatement whileStatement:
					ExpandParamsNestedStatement(whileStatement.Body);
					break;

				case DoWhileStatement doWhileStatement:
					ExpandParamsNestedStatement(doWhileStatement.Body);
					break;

				case ForStatement forStatement:
					if (forStatement.Condition.Declaration is not null && TryExpandParamsLocalDeclaration(forStatement.Condition.Declaration, out _))
						Report(GetRange(forStatement.Condition.Declaration.SourceSyntax), "Params-typed for-loop declarations cannot be expanded in this phase.");
					ExpandParamsNestedStatement(forStatement.Body);
					break;

				case ForeachStatement foreachStatement:
					ExpandParamsNestedStatement(foreachStatement.Body);
					break;

				case SwitchStatement switchStatement:
					ExpandParamsLocalDeclarations(switchStatement.Statements);
					break;

				case TryStatement tryStatement:
					ExpandParamsNestedStatement(tryStatement.Body);
					foreach (CatchStatement catchStatement in tryStatement.Catches)
						ExpandParamsNestedStatement(catchStatement.Body);
					ExpandParamsNestedStatement(tryStatement.Finally);
					break;

				case CatchStatement catchStatement:
					ExpandParamsNestedStatement(catchStatement.Body);
					break;

				case FinallyStatement finallyStatement:
					ExpandParamsNestedStatement(finallyStatement.Body);
					break;

				case WithinStatement withinStatement:
					ExpandParamsNestedStatement(withinStatement.Body);
					break;
			}
		}
	}

	void ExpandParamsNestedStatement(Statement? statement)
	{
		if (statement is BlockStatement block)
			ExpandParamsLocalDeclarations(block.Statements);
		else if (statement is not null)
			ExpandParamsLocalDeclarations([statement]);
	}

	bool TryExpandParamsDeconstruction(DeclarationStatement declaration, out List<Statement> declarations)
	{
		declarations = [];
		if (declaration.Target.Names.Count <= 1)
			return false;
		if (!TryCreateParamsComponentExpressions(declaration.InitialValue, out List<Expression> components))
			return false;
		if (components.Count != declaration.Target.Names.Count)
		{
			Report(GetRange(declaration.SourceSyntax), $"Deconstruction declares {declaration.Target.Names.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} target(s), but the initializer has {components.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} component(s).");
			return false;
		}

		for (int i = 0; i < declaration.Target.Names.Count; i++)
		{
			DeclarationStatement componentDeclaration = new()
			{
				SourceSyntax = declaration.SourceSyntax,
				InitialValue = components[i],
				ResolvedType = declaration.ResolvedType
			};
			componentDeclaration.Target.SourceSyntax = declaration.Target.SourceSyntax;
			componentDeclaration.Target.Type = declaration.Target.Type is AutoTypeReference ? null : CloneType(declaration.Target.Type);
			componentDeclaration.Target.ResolvedType = components[i].ResolvedType;
			componentDeclaration.Target.Names.Add(declaration.Target.Names[i]);
			declarations.Add(componentDeclaration);
		}

		return true;
	}

	bool TryExpandParamsVariable(VariableDefinition variable, out List<VariableDefinition> variables)
	{
		variables = [];
		if (!TryGetParamsComponentShape(variable.Type, variable.ResolvedType, variable.Name, out ParamsComponentShape shape))
			return false;

		List<Expression?> initialValues = GetParamsComponentInitialValues(variable.InitialValue, shape, deferCurrentAllocator: false);
		for (int componentIndex = 0; componentIndex < shape.Components.Count; componentIndex++)
			variables.Add(CreateExpandedVariable(variable, shape.Components[componentIndex], initialValues[componentIndex]));
		RegisterParamsExpansion(variable, shape, variables);
		return variables.Count > 0;
	}

	bool TryExpandParamsLocalDeclaration(DeclarationStatement declaration, out List<Statement> declarations)
	{
		declarations = [];
		if (declaration.Target.Names.Count != 1)
			return false;

		string name = declaration.Target.Names[0];
		if (!TryGetParamsComponentShape(declaration.Target.Type, declaration.Target.ResolvedType, name, out ParamsComponentShape shape))
			return false;

		List<Expression?> initialValues = GetParamsComponentInitialValues(declaration.InitialValue, shape, deferCurrentAllocator: true);
		List<DeclarationTarget> targets = [];
		for (int componentIndex = 0; componentIndex < shape.Components.Count; componentIndex++)
		{
			DeclarationStatement componentDeclaration = CreateExpandedDeclaration(declaration, shape.Components[componentIndex], initialValues[componentIndex]);
			declarations.Add(componentDeclaration);
			targets.Add(componentDeclaration.Target);
		}
		if (declaration.InitialValue is CallExpression call && TryGetParamsComponentShape(null, call.ResolvedType, name, out ParamsComponentShape callShape) && callShape.Components.Count == shape.Components.Count)
		{
			((DeclarationStatement)declarations[0]).InitialValue = null;
			for (int i = 1; i < targets.Count; i++)
			{
				call.Arguments.Add(new ArgumentExpression
				{
					SourceSyntax = declaration.SourceSyntax,
					Modifier = ArgumentModifier.Out,
					Value = CreateVariableReference(targets[i], shape.Components[i].Type),
					ResolvedType = shape.Components[i].Type
				});
			}
			declarations.Add(new ExpressionStatement
			{
				SourceSyntax = declaration.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = declaration.SourceSyntax,
					Target = CreateVariableReference(targets[0], shape.Components[0].Type),
					Operator = AssignmentOperator.Assign,
					Value = call,
					ResolvedType = shape.Components[0].Type
				}
			});
		}
		RegisterParamsExpansion(declaration.Target, shape, targets);
		return declarations.Count > 0;
	}

	ParameterDefinition CreateExpandedParameter(ParameterDefinition source, ParamsComponent component)
	{
		return new ParameterDefinition
		{
			SourceSyntax = source.SourceSyntax,
			Name = component.ExpandedName,
			Symbol = component.ExpandedName,
			Modifier = source.Modifier,
			ResolvedType = component.Type,
			DefaultValue = component.SourceParameter?.DefaultValue
		};
	}

	FieldDefinition CreateExpandedField(FieldDefinition source, ParamsComponent component, Expression? initialValue)
	{
		return new FieldDefinition
		{
			SourceSyntax = source.SourceSyntax,
			Name = component.ExpandedName,
			Symbol = component.ExpandedName,
			Export = source.Export,
			Extern = source.Extern,
			Modifier = source.Modifier,
			ResolvedType = component.Type,
			InitialValue = initialValue
		};
	}

	VariableDefinition CreateExpandedVariable(VariableDefinition source, ParamsComponent component, Expression? initialValue)
	{
		return new VariableDefinition
		{
			SourceSyntax = source.SourceSyntax,
			Name = component.ExpandedName,
			Symbol = component.ExpandedName,
			Export = source.Export,
			Extern = source.Extern,
			ResolvedType = component.Type,
			InitialValue = initialValue
		};
	}

	DeclarationStatement CreateExpandedDeclaration(DeclarationStatement source, ParamsComponent component, Expression? initialValue)
	{
		DeclarationStatement declaration = new()
		{
			SourceSyntax = source.SourceSyntax,
			InitialValue = initialValue,
			ResolvedType = source.ResolvedType
		};
		declaration.Target.SourceSyntax = source.Target.SourceSyntax;
		declaration.Target.ResolvedType = component.Type;
		declaration.Target.Names.Add(component.ExpandedName);
		return declaration;
	}

	List<Expression?> GetParamsComponentInitialValues(Expression? initialValue, ParamsComponentShape shape, bool deferCurrentAllocator)
	{
		List<Expression?> values = [];
		if (initialValue is ConstructionExpression { ElementCount: not null, Type: not null } construction
			&& shape.Kind == ParamsComponentShapeKind.Array
			&& shape.Components.Count == 2)
		{
			values.Add(CreateAllocCall(
				construction.Type,
				deferCurrentAllocator ? new CurrentAllocatorExpression { SourceSyntax = construction.SourceSyntax, ResolvedType = "Allocator*" } : null,
				construction.SourceSyntax,
				construction.ElementCount));
			values.Add(construction.ElementCount);
			return values;
		}

		if (initialValue is not null && TryCreateParamsComponentExpressions(initialValue, out List<Expression> components) && components.Count == shape.Components.Count)
		{
			values.AddRange(components);
			return values;
		}

		if (initialValue is not null && shape.Kind == ParamsComponentShapeKind.Optional && shape.Components.Count == 2)
		{
			values.Add(initialValue);
			values.Add(new LiteralExpression
			{
				Kind = LiteralKind.True,
				Text = "true",
				Value = true,
				ResolvedType = "bool"
			});
			return values;
		}

		for (int i = 0; i < shape.Components.Count; i++)
			values.Add(null);
		return values;
	}

	void RegisterParamsExpansion<T>(BindableNode source, ParamsComponentShape shape, List<T> components)
		where T : BindableNode
	{
		List<ParamsExpansionComponent> expansion = [];
		for (int i = 0; i < components.Count && i < shape.Components.Count; i++)
		{
			BindableNode component = components[i];
			string name = component switch
			{
				ParameterDefinition parameter => parameter.Name,
				FieldDefinition field => field.Name,
				VariableDefinition variable => variable.Name,
				DeclarationTarget target => target.Names.Count == 1 ? target.Names[0] : "",
				_ => ""
			};
			expansion.Add(new ParamsExpansionComponent(shape.Components[i].Name, name, component.ResolvedType ?? ErrorType, component));
		}

		if (expansion.Count > 0)
			paramsExpansions[source] = expansion;
	}
}
