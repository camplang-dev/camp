using System;
using System.Collections.Generic;
using System.Text;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly Dictionary<(ClassDefinition Class, string TypeName), FieldDefinition> nameOfFields = [];

	static string NameOfParameterName(TypeReference? type)
	{
		return "nameof_" + SanitizeNameOfTypeName(NameOfTypeName(type));
	}

	static string NameOfFieldName(TypeReference? type)
	{
		return "_nameof_" + SanitizeNameOfTypeName(NameOfTypeName(type));
	}

	static string NameOfTypeName(TypeReference? type)
	{
		return type?.ResolvedType ?? type switch
		{
			GenericParameterTypeReference generic => generic.Name,
			NamedTypeReference named => named.Name,
			TypeDefinitionReference definition => definition.Name,
			_ => ErrorType
		};
	}

	static string SanitizeNameOfTypeName(string name)
	{
		StringBuilder builder = new();
		foreach (char ch in name)
			builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
		return builder.Length == 0 ? "value" : builder.ToString();
	}

	void FinalizeNameOfParameter(NameOfParameterDefinition parameter, AnalysisScope scope)
	{
		AnalyzeOptionalType(parameter.Type, scope);
		parameter.Name = NameOfParameterName(parameter.Type);
		parameter.Symbol = parameter.Name;
		parameter.ResolvedType = "string";
		ValidateNameOfRequest(parameter.Type, parameter.SourceSyntax, scope);
	}

	void ValidateNameOfRequest(TypeReference? type, SyntaxNode? syntax, AnalysisScope scope)
	{
		if (type is null)
			return;
		string typeName = NameOfTypeName(type);
		if (ContainsGenericTypeName(typeName, scope))
			return;
	}

	string BodyAnalyzeNameOfExpression(NameOfExpression expression, BodyScope scope, AnalysisScope typeScope)
	{
		if (TryResolveNameOfOperand(expression.Text, scope, typeScope, expression.SourceSyntax, out string name, out BindableNode? reference))
		{
			if (name.Length > 0)
				expression.Value = name;
			expression.Reference = reference;
			expressionConstants[expression] = true;
			return "string";
		}

		Report(GetRange(expression.SourceSyntax), $"nameof({expression.Text}) could not be resolved.");
		return ErrorType;
	}

	bool TryResolveNameOfOperand(string operand, BodyScope scope, AnalysisScope typeScope, SyntaxNode? syntax, out string name, out BindableNode? reference)
	{
		name = "";
		reference = null;
		operand = operand.Trim();
		if (operand.Length == 0)
			return false;

		if (operand == "this")
		{
			name = "this";
			reference = GetExplicitThisParameter(scope.CurrentFunction) ?? scope.CurrentFunction.EffectiveThisParameter;
			return true;
		}

		if (TryResolveNameOfMemberOperand(operand, scope, typeScope, syntax, out name, out reference))
			return true;

		if (TryResolveNameOfTypeOperand(operand, scope, typeScope, syntax, out name, out reference))
			return true;

		if (scope.TryLookup(operand, out BodySymbol symbol))
		{
			name = symbol.Name;
			reference = symbol.Node;
			return true;
		}

		List<FunctionDefinition> functions = LookupFunctions(operand, scope);
		if (functions.Count == 1)
		{
			name = functions[0].Name;
			reference = functions[0];
			return true;
		}
		if (functions.Count > 1)
		{
			Report(GetRange(syntax), $"nameof({operand}) is ambiguous.");
			return true;
		}

		if (LookupGlobalStorageSymbol(operand, syntax) is BodySymbol global)
		{
			name = global.Name;
			reference = global.Node;
			return true;
		}

		return false;
	}

	bool TryResolveNameOfMemberOperand(string operand, BodyScope scope, AnalysisScope typeScope, SyntaxNode? syntax, out string name, out BindableNode? reference)
	{
		name = "";
		reference = null;
		int dot = operand.LastIndexOf('.');
		if (dot <= 0 || dot == operand.Length - 1)
			return false;

		string targetName = operand[..dot];
		string memberName = operand[(dot + 1)..];
		if (scope.TryLookupComponentSymbol(memberName, out BodyComponentSymbol component) && component.Owner == targetName)
		{
			name = component.ExpandedName;
			return true;
		}

		string targetType = ErrorType;
		if (targetName == "this")
			targetType = BodyAnalyzeThisExpression(new ThisExpression { SourceSyntax = syntax }, scope);
		else if (scope.TryLookup(targetName, out BodySymbol targetSymbol))
			targetType = targetSymbol.Type;
		else if (TryResolveNameOfTypeOperand(targetName, scope, typeScope, syntax, out _, out BindableNode? typeReference) && typeReference is TypeDefinition typeDefinition)
			targetType = typeDefinition.Name;

		if (targetType == ErrorType)
			return false;

		List<BodySymbol> fields = LookupMemberSymbols(targetType, memberName, syntax);
		if (fields.Count == 1)
		{
			name = fields[0].Name;
			reference = fields[0].Node;
			return true;
		}

		if (GetTypeDefinition(targetType) is TypeDefinition type)
		{
			List<FunctionDefinition> getters = [];
			foreach (FunctionDefinition function in GetTypeFunctions(type))
			{
				if (function.Name == "get" + memberName && IsPropertyGetterFunction(function))
					getters.Add(function);
			}
			if (getters.Count == 1)
			{
				name = getters[0].Name;
				reference = getters[0];
				return true;
			}
		}

		name = memberName;
		return true;
	}

	bool TryResolveNameOfTypeOperand(string operand, BodyScope scope, AnalysisScope typeScope, SyntaxNode? syntax, out string name, out BindableNode? reference)
	{
		name = "";
		reference = null;

		string resolved = ResolveNameOfTypeOperandName(operand, typeScope, out reference);
		if (resolved == ErrorType)
			return false;

		if (ContainsGenericTypeName(resolved, typeScope))
		{
			if (!HasNameOfCapability(scope, resolved))
			{
				Report(GetRange(syntax), $"nameof({operand}) requires parameter '{NameOfParameterName(TypeReferenceForResolvedName(resolved))}'.");
				return true;
			}
			name = "";
			reference = FindNameOfParameter(scope.CurrentFunction, resolved);
			return true;
		}

		name = BuildNameOfTypeValue(resolved);
		return true;
	}

	string ResolveNameOfTypeOperandName(string operand, AnalysisScope scope, out BindableNode? reference)
	{
		reference = null;
		if (TryResolveAlias(operand, AliasTargetKind.Type, null, out AliasDefinition? alias))
			operand = alias!.ResolvedTargetName;

		if (TryGetPrimitiveType(operand, out _))
			return operand;
		if (scope.TryGetGenericParameter(operand, out GenericParameter? generic))
		{
			reference = generic;
			return operand;
		}
		if (typeDefinitions.TryGetValue(BaseTypeName(operand), out TypeDefinition? type))
		{
			reference = type;
			return operand;
		}
		if (TryParseTypeShape(operand, out TypeShape _))
			return operand;
		return ErrorType;
	}

	string BuildNameOfTypeValue(string resolvedType)
	{
		return BuildFlattenedTypeFragment(resolvedType);
	}

	bool HasNameOfCapability(BodyScope scope, string typeName)
	{
		if (FindNameOfParameter(scope.CurrentFunction, typeName) is not null)
			return true;

		if (scope.ContainingType is ClassDefinition classDefinition
			&& nameOfFields.ContainsKey((classDefinition, typeName)))
			return true;

		return false;
	}

	static bool ContainsGenericTypeName(string typeName, AnalysisScope scope)
	{
		if (!new TypeShapeParser(typeName).TryParse(out TypeShape shape))
			return scope.TryGetGenericParameter(typeName, out _);
		return ContainsGenericTypeName(shape, scope);
	}

	static bool ContainsGenericTypeName(TypeShape shape, AnalysisScope scope)
	{
		if (scope.TryGetGenericParameter(BaseTypeName(shape.Name), out _))
			return true;
		return shape.Element is not null && ContainsGenericTypeName(shape.Element, scope);
	}

	void GenerateNameOfFields(ClassDefinition classDefinition)
	{
		HashSet<string> generated = [];
		foreach (FunctionDefinition function in classDefinition.Functions)
		{
			if (function.Modifier != FunctionModifier.Constructor && function.Name != InitNewMethodName)
				continue;

			foreach (ParameterDefinition parameter in function.Parameters)
			{
				if (parameter is not NameOfParameterDefinition nameOf)
					continue;

				string typeName = NameOfTypeName(nameOf.Type);
				if (!ContainsClassGenericParameter(classDefinition, typeName) || !generated.Add(typeName))
					continue;

				FieldDefinition field = new()
				{
					SourceSyntax = nameOf.SourceSyntax,
					Name = NameOfFieldName(nameOf.Type),
					Symbol = NameOfFieldName(nameOf.Type),
					Type = new PrimitiveTypeReference { Type = PrimitiveType.String, ResolvedType = "string" },
					ResolvedType = "string"
				};
				classDefinition.Fields.Add(field);
				nameOfFields[(classDefinition, typeName)] = field;
			}
		}
	}

	bool ContainsClassGenericParameter(ClassDefinition classDefinition, string typeName)
	{
		foreach (GenericParameter parameter in classDefinition.GenericParameters)
			if (typeName.Contains(parameter.Name, StringComparison.Ordinal))
				return true;
		return false;
	}

	void InsertNameOfFieldAssignments(FunctionDefinition function, TypeDefinition? containingType)
	{
		if (function.Body is null || function.Name != InitNewMethodName || containingType is not ClassDefinition classDefinition)
			return;

		List<Statement> assignments = [];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter is not NameOfParameterDefinition nameOf)
				continue;

			string typeName = NameOfTypeName(nameOf.Type);
			if (!nameOfFields.TryGetValue((classDefinition, typeName), out FieldDefinition? field))
				continue;

			assignments.Add(new ExpressionStatement
			{
				SourceSyntax = parameter.SourceSyntax,
				ResolvedType = "void",
				Expression = new AssignmentExpression
				{
					SourceSyntax = parameter.SourceSyntax,
					Target = CreateNameOfFieldReference(classDefinition, field, parameter.SourceSyntax),
					Operator = AssignmentOperator.Assign,
					Value = CreateVariableReference(parameter, "string"),
					ResolvedType = "string"
				}
			});
		}

		if (assignments.Count > 0)
			function.Body.Statements.InsertRange(0, assignments);
	}

	Expression LowerNameOfExpression(NameOfExpression nameOf)
	{
		if (nameOf.Value is string concreteName)
			return NameOfStringLiteral(concreteName, nameOf.SourceSyntax);

		string typeName = nameOf.Text.Trim();
		if (FindNameOfParameter(currentRewriteFunction, typeName) is NameOfParameterDefinition parameter)
			return CreateVariableReference(parameter, "string");

		if (currentRewriteContainingType is ClassDefinition classDefinition
			&& nameOfFields.TryGetValue((classDefinition, typeName), out FieldDefinition? field))
			return CreateNameOfFieldReference(classDefinition, field, nameOf.SourceSyntax);

		return nameOf;
	}

	static NameOfParameterDefinition? FindNameOfParameter(FunctionDefinition? function, string typeName)
	{
		if (function is null)
			return null;
		foreach (ParameterDefinition parameter in function.Parameters)
			if (parameter is NameOfParameterDefinition nameOf && NameOfTypeName(nameOf.Type) == typeName)
				return nameOf;
		return null;
	}

	MemberReferenceExpression CreateNameOfFieldReference(ClassDefinition classDefinition, FieldDefinition field, SyntaxNode? syntax)
	{
		return new MemberReferenceExpression
		{
			SourceSyntax = syntax,
			Target = new ThisExpression
			{
				SourceSyntax = syntax,
				ResolvedType = classDefinition.Name
			},
			Name = field.Name,
			Member = field,
			ResolvedType = "string"
		};
	}

	void AddImplicitNameOfArguments(CallExpression call)
	{
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function))
			return;
		AddImplicitNameOfArguments(call, function, constructedType: null);
	}

	void AddImplicitNameOfArguments(CallExpression call, FunctionDefinition function, TypeReference? constructedType)
	{
		Dictionary<string, string> substitutions = GetGenericSubstitutions(call, function, constructedType);
		List<ParameterDefinition> parameters = GetCallableParametersForCall(function, IncludeExplicitThisArgument(call.Target, function));
		for (int i = 0; i < parameters.Count; i++)
		{
			if (parameters[i] is not NameOfParameterDefinition nameOf)
				continue;
			if (i < call.Arguments.Count && call.Arguments[i].Modifier == ArgumentModifier.None && call.Arguments[i].Value is not WithinExpression { Expression: null })
				continue;

			string requestedTypeName = NameOfTypeName(nameOf.Type);
			string typeName = SubstituteNameOfTypeName(requestedTypeName, substitutions);
			Expression value = typeName == requestedTypeName && ContainsAnyGenericParameter(typeName, function)
				? LowerNameOfExpression(new NameOfExpression { SourceSyntax = call.SourceSyntax ?? call.Target?.SourceSyntax, Text = typeName })
				: NameOfStringLiteral(BuildNameOfTypeValue(typeName), call.SourceSyntax ?? call.Target?.SourceSyntax);
			call.Arguments.Insert(i, new ArgumentExpression
			{
				SourceSyntax = call.SourceSyntax ?? call.Target?.SourceSyntax,
				Value = value,
				ResolvedType = "string"
			});
		}
	}

	static string SubstituteNameOfTypeName(string typeName, Dictionary<string, string> substitutions)
	{
		foreach ((string name, string replacement) in substitutions)
			typeName = typeName.Replace(name, replacement, StringComparison.Ordinal);
		return typeName;
	}

	static bool ContainsAnyGenericParameter(string typeName, FunctionDefinition function)
	{
		foreach (GenericParameter parameter in function.GenericParameters)
			if (typeName.Contains(parameter.Name, StringComparison.Ordinal))
				return true;
		return false;
	}

	static LiteralExpression NameOfStringLiteral(string value, SyntaxNode? syntax)
	{
		return new LiteralExpression
		{
			SourceSyntax = syntax,
			Kind = LiteralKind.String,
			Text = QuoteCampString(value),
			Value = value,
			ResolvedType = "string"
		};
	}

	static string QuoteCampString(string value)
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
				_ => ch.ToString()
			});
		}
		builder.Append('"');
		return builder.ToString();
	}
}
