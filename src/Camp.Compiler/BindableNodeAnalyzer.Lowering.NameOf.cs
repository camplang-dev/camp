using System;
using System.Collections.Generic;
using System.Text;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly Dictionary<(ClassDefinition Class, string TypeName), FieldDefinition> nameOfFields = [];

	static string NameOfParameterName(TypeReference? type)
	{
		return "typenameof_" + SanitizeNameOfTypeName(NameOfTypeName(type));
	}

	static string NameOfFieldName(TypeReference? type)
	{
		return "_typenameof_" + SanitizeNameOfTypeName(NameOfTypeName(type));
	}

	static string NameOfTypeName(TypeReference? type)
	{
		return type?.ResolvedType ?? type switch
		{
			GenericParameterTypeReference generic => generic.Name,
			ClassTypeReference => "classtype",
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

	string BodyAnalyzeNameOfExpression(NameOfExpression expression, BodyScope scope, AnalysisScope typeScope, string? targetType)
	{
		if (TryResolveNameOfOperand(expression.Text, scope, typeScope, expression.SourceSyntax, out string name, out BindableNode? reference))
		{
			if (name.Length > 0)
			{
				expression.Value = name;
				expression.Reference = reference;
				expressionConstants[expression] = true;
				return GetStringLiteralType(NameOfStringLiteral(name, expression.SourceSyntax), targetType);
			}
			if (reference is ClassDefinition && expression.Text.Trim() == "classtype" && !scope.AllowClassTypeNameOfDefaultValue)
			{
				Report(GetRange(expression.SourceSyntax), "typenameof(classtype) is valid only as a parameter default value.");
				return ErrorType;
			}
			expression.Reference = reference;
			expressionConstants[expression] = true;
			return "string";
		}

		Report(GetRange(expression.SourceSyntax), $"typenameof({expression.Text}) could not be resolved as a type.");
		return ErrorType;
	}

	bool TryResolveNameOfOperand(string operand, BodyScope scope, AnalysisScope typeScope, SyntaxNode? syntax, out string name, out BindableNode? reference)
	{
		name = "";
		reference = null;
		operand = operand.Trim();
		if (operand.Length == 0)
			return false;

		if (TryResolveNameOfTypeOperand(operand, scope, typeScope, syntax, out name, out reference))
			return true;

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

		string resolved = ResolveNameOfTypeOperandName(operand, typeScope, syntax, out reference);
		if (resolved == ErrorType)
			return false;

		if (ContainsGenericTypeName(resolved, typeScope))
		{
			if (!HasNameOfCapability(scope, resolved))
			{
				Report(GetRange(syntax), $"typenameof({operand}) requires parameter '{NameOfParameterName(TypeReferenceForResolvedName(resolved))}'.");
				return true;
			}
			name = "";
			reference = FindNameOfParameter(scope.CurrentFunction, resolved);
			return true;
		}

		if (resolved != "classtype")
			name = BuildNameOfTypeValue(resolved);
		return true;
	}

	string ResolveNameOfTypeOperandName(string operand, AnalysisScope scope, SyntaxNode? syntax, out BindableNode? reference)
	{
		reference = null;
		if (TryResolveAlias(operand, AliasTargetKind.Type, null, out AliasDefinition? alias))
			operand = alias!.ResolvedTargetName;

		if (operand == "classtype" && scope.GetContainingType() is ClassDefinition classDefinition)
		{
			reference = classDefinition;
			return "classtype";
		}

		if (TryResolveNameOfBaseTypeName(operand, scope, syntax, out string resolvedBaseName, out reference))
			return resolvedBaseName;
		if (TryParseTypeShape(operand, out TypeShape shape) && IsResolvableTypeNameShape(shape, scope, syntax))
			return operand;
		return ErrorType;
	}

	bool IsResolvableTypeNameShape(TypeShape shape, AnalysisScope scope, SyntaxNode? syntax)
	{
		if (shape.Element is not null)
			return IsResolvableTypeNameShape(shape.Element, scope, syntax);

		string baseName = BaseTypeName(shape.Name);
		return TryResolveNameOfBaseTypeName(baseName, scope, syntax, out _, out _);
	}

	bool TryResolveNameOfBaseTypeName(string baseName, AnalysisScope scope, SyntaxNode? syntax, out string resolvedName, out BindableNode? reference)
	{
		resolvedName = baseName;
		reference = null;
		if (TryResolveAlias(baseName, AliasTargetKind.Type, syntax, out AliasDefinition? alias))
		{
			resolvedName = alias!.ResolvedTargetName;
			reference = alias;
			return true;
		}
		if (TryGetPrimitiveType(baseName, out _))
			return true;
		if (scope.TryGetGenericParameter(baseName, out GenericParameter? generic))
		{
			reference = generic;
			return true;
		}
		if (typeDefinitions.TryGetValue(baseName, out TypeDefinition? type))
		{
			reference = type;
			return true;
		}
		if (!TrySplitQualifiedName(baseName, out List<string> qualifiers, out string name))
			return false;
		foreach (AliasDefinition candidate in aliasDefinitions.Values)
		{
			if (candidate.Name == name && candidate.TargetKind == AliasTargetKind.Type && IsImportedQualifiedName(candidate, qualifiers, syntax))
			{
				resolvedName = candidate.ResolvedTargetName;
				reference = candidate;
				return true;
			}
		}
		foreach (TypeDefinition candidate in allTypeDefinitions)
		{
			if (candidate.Name == name && IsImportedQualifiedName(candidate, qualifiers, syntax))
			{
				resolvedName = candidate.Name;
				reference = candidate;
				return true;
			}
		}
		return false;
	}

	static bool TrySplitQualifiedName(string text, out List<string> qualifiers, out string name)
	{
		qualifiers = [];
		name = text;
		string[] parts = text.Split("::", StringSplitOptions.None);
		if (parts.Length < 2)
			return false;
		foreach (string part in parts)
			if (string.IsNullOrWhiteSpace(part))
				return false;
		for (int i = 0; i < parts.Length - 1; i++)
			qualifiers.Add(parts[i]);
		name = parts[^1];
		return true;
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
			return NameOfStringLiteral(concreteName, nameOf.SourceSyntax, nameOf.ResolvedType);

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
			int argumentIndex = FindArgumentIndexForCallableParameter(call.Arguments, parameters, i);
			if (argumentIndex < call.Arguments.Count && call.Arguments[argumentIndex].Modifier == ArgumentModifier.None && call.Arguments[argumentIndex].Value is not WithinExpression { Expression: null })
				continue;

			string requestedTypeName = NameOfTypeName(nameOf.Type);
			string typeName = SubstituteNameOfTypeName(requestedTypeName, substitutions);
			Expression value = typeName == requestedTypeName && ContainsAnyGenericParameter(typeName, function)
				? LowerNameOfExpression(new NameOfExpression { SourceSyntax = call.SourceSyntax ?? call.Target?.SourceSyntax, Text = typeName })
				: NameOfStringLiteral(BuildNameOfTypeValue(typeName), call.SourceSyntax ?? call.Target?.SourceSyntax);
			call.Arguments.Insert(Math.Min(argumentIndex, call.Arguments.Count), new ArgumentExpression
			{
				SourceSyntax = call.SourceSyntax ?? call.Target?.SourceSyntax,
				Value = value,
				ResolvedType = "string"
			});
		}
	}

	bool TryGetClassTypeCallSiteName(Expression? callTarget, FunctionDefinition function, out string classTypeName)
	{
		classTypeName = "";
		if (FindContainingType(function) is not ClassDefinition owner)
			return false;

		classTypeName = owner.Name;
		Expression? memberTarget = callTarget switch
		{
			MemberExpression member => member.Target,
			MemberReferenceExpression memberReference => memberReference.Target,
			_ => null
		};
		if (memberTarget is not null)
		{
			string? targetType = memberTarget.ResolvedType;
			if (targetType is null && expressionRewrites.TryGetValue(memberTarget, out Expression? rewritten))
				targetType = rewritten.ResolvedType;
			string targetClass = TryGetPointerElementType(targetType) ?? targetType ?? "";
			if (GetTypeDefinition(targetClass) is ClassDefinition classDefinition
				&& IsClassOrDerivedFrom(classDefinition, owner))
				classTypeName = classDefinition.Name;
		}
		return true;
	}

	bool IsClassOrDerivedFrom(ClassDefinition candidate, ClassDefinition ancestor)
	{
		if (ReferenceEquals(candidate, ancestor))
			return true;
		if (!typeInfos.TryGetValue(candidate, out TypeAnalysisInfo? info))
			return false;
		foreach (TypeDefinition baseType in info.BaseTypes)
		{
			if (ReferenceEquals(baseType, ancestor))
				return true;
			if (baseType is ClassDefinition baseClass && IsClassOrDerivedFrom(baseClass, ancestor))
				return true;
		}
		return false;
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

	static LiteralExpression NameOfStringLiteral(string value, SyntaxNode? syntax, string? resolvedType = null)
	{
		return new LiteralExpression
		{
			SourceSyntax = syntax,
			Kind = LiteralKind.String,
			Text = QuoteCampString(value),
			Value = value,
			ResolvedType = resolvedType ?? "string"
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
