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
		ExpandParamsParameters(function.Parameters);
		if (function.Body is not null)
			ExpandParamsLocalDeclarations(function.Body.Statements);
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
			foreach (ParamsComponent component in shape.Components)
				components.Add(CreateExpandedField(field, component));

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

	bool TryExpandParamsVariable(VariableDefinition variable, out List<VariableDefinition> variables)
	{
		variables = [];
		if (!TryGetParamsComponentShape(variable.Type, variable.ResolvedType, variable.Name, out ParamsComponentShape shape))
			return false;

		foreach (ParamsComponent component in shape.Components)
			variables.Add(CreateExpandedVariable(variable, component));
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

		foreach (ParamsComponent component in shape.Components)
			declarations.Add(CreateExpandedDeclaration(declaration, component));
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

	FieldDefinition CreateExpandedField(FieldDefinition source, ParamsComponent component)
	{
		return new FieldDefinition
		{
			SourceSyntax = source.SourceSyntax,
			Name = component.ExpandedName,
			Symbol = component.ExpandedName,
			Export = source.Export,
			Extern = source.Extern,
			Modifier = source.Modifier,
			ResolvedType = component.Type
		};
	}

	VariableDefinition CreateExpandedVariable(VariableDefinition source, ParamsComponent component)
	{
		return new VariableDefinition
		{
			SourceSyntax = source.SourceSyntax,
			Name = component.ExpandedName,
			Symbol = component.ExpandedName,
			Export = source.Export,
			Extern = source.Extern,
			ResolvedType = component.Type
		};
	}

	DeclarationStatement CreateExpandedDeclaration(DeclarationStatement source, ParamsComponent component)
	{
		DeclarationStatement declaration = new()
		{
			SourceSyntax = source.SourceSyntax,
			ResolvedType = source.ResolvedType
		};
		declaration.Target.SourceSyntax = source.Target.SourceSyntax;
		declaration.Target.ResolvedType = component.Type;
		declaration.Target.Names.Add(component.ExpandedName);
		return declaration;
	}
}
