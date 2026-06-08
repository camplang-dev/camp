using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void RequireExpressionType(string expected, string actual, SyntaxNode? syntax, string context)
	{
		if (!CanImplicitlyConvert(actual, expected))
			Report(GetRange(syntax), $"{context} must be '{expected}', not '{actual}'.");
	}

	void CheckAssignable(string expected, string actual, SyntaxNode? syntax, string context)
	{
		if (expected == ErrorType || actual == ErrorType || expected == TargetType || actual == TargetType)
			return;

		if (!CanImplicitlyConvert(actual, expected))
			Report(GetRange(syntax), $"{context} cannot convert '{actual}' to '{expected}'.");
	}

	void RequireMutableWriteTarget(string targetType, SyntaxNode? syntax, string context)
	{
		if (targetType == ErrorType || targetType == TargetType)
			return;

		if (IsConstQualified(targetType))
			Report(GetRange(syntax), $"{context} is const and cannot be assigned.");
	}

	void RequireMutableWriteTarget(Expression? target, string targetType, SyntaxNode? syntax, string context)
	{
		if (target is MemberReferenceExpression { Member: FieldDefinition or ParameterDefinition or VariableDefinition } && IsMutableStorageType(targetType))
			return;
		if (target is MemberExpression member
			&& expressionRewrites.TryGetValue(member, out Expression? rewrite)
			&& rewrite is MemberReferenceExpression { Member: FieldDefinition or ParameterDefinition or VariableDefinition }
			&& IsMutableStorageType(targetType))
			return;

		if (target is IndexExpression index)
		{
			RequireMutableIndexedWriteTarget(index, syntax, context);
			return;
		}

		RequireMutableWriteTarget(targetType, syntax, context);
	}

	static bool IsMutableStorageType(string type)
	{
		return IsPrimitiveStringType(type);
	}

	void RequireMutableIndexedWriteTarget(IndexExpression index, SyntaxNode? syntax, string context)
	{
		string indexedType = index.Target?.ResolvedType ?? ErrorType;
		if (indexedType == ErrorType || indexedType == TargetType)
			return;

		if (TryGetArrayElementType(indexedType) is not null)
		{
			if (IsConstQualified(indexedType))
				Report(GetRange(syntax), $"{context} is const and cannot be assigned.");
			return;
		}

		if (GetPrimitiveStringElementType(indexedType) is not null)
		{
			if (IsConstQualified(indexedType))
				Report(GetRange(syntax), $"{context} is const and cannot be assigned.");
			return;
		}

		RequireMutableWriteTarget(index.ResolvedType ?? ErrorType, syntax, context);
	}

	bool CanImplicitlyConvert(string source, string target)
	{
		if (source == target || source == ErrorType || target == ErrorType || target == TargetType)
			return true;

		if (source == "#NULL" && TryParseTypeShape(target, out TypeShape nullTarget) && (nullTarget.IsPointer || nullTarget.IsOptional))
			return true;

		if (source == "#NULL" && TryGetCallableShape(target, out CallableShape nullCallableTarget) && nullCallableTarget.Kind == "fn")
			return true;

		if (source == "#NULL" && IsPrimitiveStringType(target))
			return true;

		if (CanConvertPrimitiveStringToPointer(source, target))
			return true;

		if (CanConvertIteratorStateToProtocol(source, target))
			return true;

		if (source == "#NULL" && target == AllocatorType)
			return true;

		if (CanLiftToOptional(source, target))
			return true;

		if (source == AllocatorType && target == "Allocator*")
			return true;

		if (source == "Allocator*" && target == AllocatorType)
			return true;

		if (IsUntypedPointerType(target) && (IsObjectPointerType(source) || TryGetCallableShape(source, out _)))
			return true;

		if (TryGetCallableShape(source, out CallableShape sourceCallable) && TryGetCallableShape(target, out CallableShape targetCallable))
			return CallableShapesCompatible(sourceCallable, targetCallable);

		if (CanCopyConstValue(source, target))
			return true;

		if (CanCopyTopLevelConstValue(source, target))
			return true;

		if (IsClassToInterfaceConversion(source, target) || IsStructToInterfaceConversion(source, target) || IsInterfaceUpcast(source, target))
			return true;

		if (IsNewtypeOrEnumBoundary(source, target))
			return false;

		if (TryParseTypeShape(source, out TypeShape sourceShape) && TryParseTypeShape(target, out TypeShape targetShape))
			return CanImplicitlyConvertShape(sourceShape, targetShape);

		return IsNumericType(source) && IsNumericType(target) && NumericRank(source) <= NumericRank(target);
	}

	bool CanConvertIteratorStateToProtocol(string source, string target)
	{
		if (!TryGetIteratorProtocolCurrentTypes(target, out List<string>? targetCurrentTypes) || targetCurrentTypes is null)
			return false;

		string stateType = TryGetPointerElementType(source) ?? source;
		if (!TryFindIteratorNextMethod(stateType, out _, out string elementType))
			return false;

		return targetCurrentTypes.Count == 1 && CanImplicitlyConvert(elementType, targetCurrentTypes[0]);
	}

	bool CanLiftToOptional(string source, string target)
	{
		if (!TryParseTypeShape(target, out TypeShape targetShape) || targetShape.Kind != TypeShapeKind.Optional || targetShape.Element is null)
			return false;
		string targetElement = TypeShapeParser.Format(targetShape.Element);
		return CanImplicitlyConvert(source, targetElement);
	}

	bool CanCopyConstValue(string source, string target)
	{
		if (!TryParseTypeShape(source, out TypeShape sourceShape) || !TryParseTypeShape(target, out TypeShape targetShape))
			return false;
		if (sourceShape.Kind != TypeShapeKind.Named || targetShape.Kind != TypeShapeKind.Named)
			return false;
		if (sourceShape.Name != targetShape.Name || IsPrimitiveStringType(source) || IsPrimitiveStringType(target))
			return false;

		return sourceShape.Qualifiers.IsConst && !targetShape.Qualifiers.IsConst;
	}

	static bool CanCopyTopLevelConstValue(string source, string target)
	{
		if (!IsConstQualifiedShape(source))
			return false;

		return StripConstFromShape(source) == target;
	}

	bool CanConvertStructuralGroupedToNominalParams(string source, string target)
	{
		return false;
	}

	readonly record struct GroupedTypeComponent(string? Name, string Type);

	static bool TryGetStructuralGroupedComponents(string source, out List<GroupedTypeComponent> components)
	{
		components = [];
		if (!source.StartsWith("(", StringComparison.Ordinal) || !source.EndsWith(")", StringComparison.Ordinal))
			return false;

		string text = source[1..^1].Trim();
		if (string.IsNullOrWhiteSpace(text))
			return false;

		foreach (string part in SplitTopLevelGroupedComponents(text))
		{
			string component = part.Trim();
			string? name = null;
			string type = component;
			int colon = FindTopLevelColon(component);
			if (colon >= 0)
			{
				name = component[..colon].Trim();
				type = component[(colon + 1)..].Trim();
			}

			if (string.IsNullOrWhiteSpace(type))
				return false;
			components.Add(new GroupedTypeComponent(string.IsNullOrWhiteSpace(name) ? null : name, type));
		}

		return components.Count > 0;
	}

	static List<string> SplitTopLevelGroupedComponents(string text)
	{
		List<string> components = [];
		int start = 0;
		int depth = 0;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (c is '(' or '<' or '[')
				depth++;
			else if (c is ')' or '>' or ']')
				depth--;
			else if (c == ',' && depth == 0)
			{
				components.Add(text[start..i]);
				start = i + 1;
			}
		}
		components.Add(text[start..]);
		return components;
	}

	static int FindTopLevelColon(string text)
	{
		int depth = 0;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (c is '(' or '<' or '[')
				depth++;
			else if (c is ')' or '>' or ']')
				depth--;
			else if (c == ':' && depth == 0)
				return i;
		}
		return -1;
	}

	bool IsClassToInterfaceConversion(string source, string target)
	{
		string? sourceElement = TryGetPointerElementType(source);
		string? targetElement = TryGetPointerElementType(target);
		if (sourceElement is null || targetElement is null)
			return false;

		return typeDefinitions.TryGetValue(BaseTypeName(sourceElement), out TypeDefinition? sourceType)
			&& sourceType is ClassDefinition classDefinition
			&& typeDefinitions.TryGetValue(BaseTypeName(targetElement), out TypeDefinition? targetType)
			&& targetType is InterfaceDefinition interfaceDefinition
			&& ClassImplementsInterface(classDefinition, interfaceDefinition);
	}

	bool IsStructToInterfaceConversion(string source, string target)
	{
		string? targetElement = TryGetPointerElementType(target);
		if (targetElement is null)
			return false;

		return typeDefinitions.TryGetValue(BaseTypeName(source), out TypeDefinition? sourceType)
			&& sourceType is StructDefinition structDefinition
			&& typeDefinitions.TryGetValue(BaseTypeName(targetElement), out TypeDefinition? targetType)
			&& targetType is InterfaceDefinition interfaceDefinition
			&& TypeImplementsInterface(structDefinition, interfaceDefinition);
	}

	bool IsInterfaceUpcast(string source, string target)
	{
		string? sourceElement = TryGetPointerElementType(source);
		string? targetElement = TryGetPointerElementType(target);
		if (sourceElement is null || targetElement is null)
			return false;

		return typeDefinitions.TryGetValue(BaseTypeName(sourceElement), out TypeDefinition? sourceType)
			&& sourceType is InterfaceDefinition sourceInterface
			&& typeDefinitions.TryGetValue(BaseTypeName(targetElement), out TypeDefinition? targetType)
			&& targetType is InterfaceDefinition targetInterface
			&& InterfaceInheritsFrom(sourceInterface, targetInterface);
	}

	bool ClassImplementsInterface(ClassDefinition classDefinition, InterfaceDefinition interfaceDefinition)
	{
		return TypeImplementsInterface(classDefinition, interfaceDefinition);
	}

	bool TypeImplementsInterface(TypeDefinition typeDefinition, InterfaceDefinition interfaceDefinition)
	{
		foreach (InterfaceDefinition implemented in GetImplementedInterfaces(typeDefinition))
		{
			if (ReferenceEquals(implemented, interfaceDefinition))
				return true;
		}
		return false;
	}

	bool InterfaceInheritsFrom(InterfaceDefinition source, InterfaceDefinition target)
	{
		foreach (InterfaceDefinition baseInterface in GetBaseInterfaces(source))
		{
			if (ReferenceEquals(baseInterface, target))
				return true;
		}
		return false;
	}

	bool CanExplicitlyConvert(string source, string target)
	{
		if (CanImplicitlyConvert(source, target))
			return true;

		if (TryGetNewtypeUnderlyingType(source, out string? sourceUnderlying))
			return sourceUnderlying == target;

		if (TryGetNewtypeUnderlyingType(target, out string? targetUnderlying))
			return targetUnderlying == source;

		if (IsNumericType(source) && IsNumericType(target))
			return true;

		if (CanExplicitlyConvertPrimitiveStringPointer(source, target))
			return true;

		if (TryParseTypeShape(source, out TypeShape explicitSourceShape)
			&& TryParseTypeShape(target, out TypeShape explicitTargetShape)
			&& (CanExplicitlyConvertTargetSpecShape(explicitSourceShape, explicitTargetShape)
				|| CanExplicitlyConvertPointerNaturalInteger(explicitSourceShape, explicitTargetShape)
				|| CanExplicitlyConvertUntypedPointer(explicitSourceShape, explicitTargetShape)))
			return true;

		if (CanExplicitlyConvertCallableNaturalInteger(source, target))
			return true;

		if (TryParseTypeShape(source, out TypeShape untypedSourceShape)
			&& CanExplicitlyConvertUntypedPointerToCallable(untypedSourceShape, target))
			return true;

		return TryParseTypeShape(source, out TypeShape sourceShape)
			&& TryParseTypeShape(target, out TypeShape targetShape)
			&& sourceShape.IsPointer
			&& targetShape.IsPointer;
	}

	bool IsNewtypeOrEnumBoundary(string source, string target)
	{
		return TryGetUnderlyingNumericType(source, out _) || TryGetUnderlyingNumericType(target, out _);
	}

	string GetForeachElementType(string sourceType, bool isAwaited, SyntaxNode? syntax)
	{
		if (TryGetArrayElementType(sourceType) is string arrayElement)
			return isAwaited ? ReportType(syntax, "await foreach requires an async iter source, not an array.") : arrayElement;

		if (TryGetIteratorProtocolCurrentTypes(sourceType, out List<string>? currentTypes) && currentTypes is { Count: 1 })
			return isAwaited ? ReportType(syntax, "await foreach requires an async iter source, not an iter source.") : currentTypes[0];

		if (sourceType.StartsWith("async iter ", StringComparison.Ordinal) || sourceType.StartsWith("async iter(", StringComparison.Ordinal))
			return ReportType(syntax, isAwaited ? "Async iterator foreach is not implemented yet." : "foreach requires an array or iterator state source.");

		Report(GetRange(syntax), $"Foreach source type '{sourceType}' is not iterable.");
		return ErrorType;
	}

	string GetForeachElementType(ForeachStatement statement, string sourceType, SyntaxNode? syntax)
	{
		if (TryGetArrayElementType(sourceType) is string arrayElement)
			return statement.IsAwaited ? ReportType(syntax, "await foreach requires an async iter source, not an array.") : arrayElement;

		if (TryFindIteratorNextMethod(sourceType, out FunctionDefinition? next, out string elementType))
		{
			if (statement.IsAwaited)
				return ReportType(syntax, "await foreach over iterator states is not implemented yet.");
			statement.IteratorNext = next;
			return elementType;
		}

		return GetForeachElementType(sourceType, statement.IsAwaited, syntax);
	}

	bool TryFindIteratorNextMethod(string sourceType, out FunctionDefinition? next, out string elementType)
	{
		next = null;
		elementType = ErrorType;
		if (GetTypeDefinition(sourceType) is not TypeDefinition type)
			return false;

		foreach (FunctionDefinition function in GetFunctions(type))
		{
			if (function.Name != "next" || function.ResolvedType != "bool")
				continue;

			List<ParameterDefinition> parameters = GetCallableParameters(function.Parameters);
			if (parameters.Count == 0)
				continue;

			string? yieldedType = TryGetPointerElementType(parameters[0].ResolvedType);
			if (yieldedType is null)
				continue;

			next = function;
			elementType = yieldedType;
			return true;
		}

		return false;
	}

	bool IsAwaitable(Expression? expression, BodyScope scope, AnalysisScope typeScope)
	{
		if (expression is CallExpression call && ResolveCallTarget(call.Target, scope, typeScope) is FunctionDefinition function)
			return function.IsAsync || HasAwaitableCallback(function.Parameters);

		string type = expression?.ResolvedType ?? ErrorType;
		return type.StartsWith("async ", StringComparison.Ordinal);
	}

	string GetAwaitedType(Expression? expression, BodyScope scope, AnalysisScope typeScope)
	{
		if (expression is CallExpression call && ResolveCallTarget(call.Target, scope, typeScope) is FunctionDefinition function)
			return function.ResolvedType == "void" ? "void" : function.ResolvedType ?? ErrorType;

		string type = expression?.ResolvedType ?? ErrorType;
		return type.StartsWith("async ", StringComparison.Ordinal) ? type["async ".Length..] : ErrorType;
	}

	static bool HasAwaitableCallback(List<ParameterDefinition> parameters)
	{
		return parameters is [.., ParameterDefinition last] && last.Type is CallableTypeReference { ReturnType: PrimitiveTypeReference { Type: PrimitiveType.Void } };
	}

	bool IsSwitchableType(string type)
	{
		return IsNumericType(type) || IsEnumType(type) || TryGetUnderlyingNumericType(type, out _);
	}

	bool IsConstant(Expression? expression)
	{
		return expression is not null && expressionConstants.TryGetValue(expression, out bool isConstant) && isConstant;
	}

	void AddTypeMembersToScope(BodyScope scope, TypeDefinition type)
	{
		switch (type)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
					scope.MemberSymbols[field.Name] = new BodySymbol(field.Name, field.ResolvedType ?? ErrorType, field);
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
					scope.MemberSymbols[field.Name] = new BodySymbol(field.Name, field.ResolvedType ?? ErrorType, field);
				break;
		}
	}

	bool TryGetUnqualifiedInstanceMember(TypeDefinition? type, string name, SyntaxNode? referenceSyntax, out string memberKind)
	{
		memberKind = "";
		if (type is null)
			return false;

		foreach (TypeDefinition candidateType in EnumerateTypeAndBases(type))
		{
			foreach (FieldDefinition field in GetTypeFields(candidateType))
			{
				if (field.Name == name)
				{
					memberKind = "Member field";
					return true;
				}

				if (TryGetParamsComponentShape(field.Type, field.ResolvedType, field.Name, out ParamsComponentShape shape))
				{
					foreach (ParamsComponent component in shape.Components)
					{
						if (component.ExpandedName == name)
						{
							memberKind = "Member field";
							return true;
						}
					}
				}
			}

			foreach (FunctionDefinition function in LookupTypeFunctions(candidateType, name, referenceSyntax))
			{
				if (!IsInstanceFunction(function))
					continue;
				memberKind = "Member method";
				return true;
			}

			foreach (FunctionDefinition getter in LookupTypeFunctions(candidateType, "get" + name, referenceSyntax))
			{
				if (!IsInstanceFunction(getter))
					continue;
				memberKind = "Member property";
				return true;
			}
		}

		return false;
	}

	bool CanExplicitlyConvertPrimitiveStringPointer(string source, string target)
	{
		return CanConvertPrimitiveStringToPointer(source, target)
			|| CanConvertPrimitiveStringToPointer(target, source)
			|| IsPrimitiveStringPointerPair(source, target);
	}

	bool IsPrimitiveStringPointerPair(string source, string target)
	{
		return TryGetPrimitiveStringPointerElement(source, out string sourceElement)
			&& TryGetPrimitiveStringPointerElement(target, out string targetElement)
			&& sourceElement == targetElement;
	}

	bool TryGetPrimitiveStringPointerElement(string type, out string element)
	{
		element = StripTopLevelValueQualifiers(type) switch
		{
			"string" => "char",
			"wstring" => "wchar",
			"astring" => "achar",
			_ => ""
		};
		if (element.Length > 0)
			return true;

		if (!TryParseTypeShape(type, out TypeShape shape) || shape.Kind != TypeShapeKind.Pointer || shape.Element is null)
			return false;
		element = StripTopLevelValueQualifiers(TypeShapeParser.Format(shape.Element));
		return element is "char" or "wchar" or "achar";
	}

	List<FunctionDefinition> LookupFunctions(string name, BodyScope scope)
	{
		List<FunctionDefinition> functions = [];
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition is FunctionDefinition function && IsCallableTopLevelFunctionNamed(function, name) && IsDefinitionVisible(function, scope.CurrentFunction.SourceSyntax))
				functions.Add(function);
		}
		foreach (TypeDefinition type in typeDefinitions.Values)
		{
			foreach (FunctionDefinition function in GetTypeFunctions(type))
			{
				if (IsTypeFunctionSymbolNamed(type, function, name) && IsMemberVisible(function, type, scope.CurrentFunction.SourceSyntax))
					functions.Add(function);
			}
		}

		return functions;
	}

	static bool IsCallableTopLevelFunctionNamed(FunctionDefinition function, string name)
	{
		if (GetExplicitThisParameter(function) is not null)
			return !string.IsNullOrWhiteSpace(function.Symbol) && function.Symbol == name;

		return IsFunctionNamed(function, name);
	}

	IEnumerable<TypeDefinition> EnumerateTypeAndBases(TypeDefinition type)
	{
		if (type is ClassDefinition classDefinition)
		{
			foreach (ClassDefinition candidate in EnumerateClassAndBases(classDefinition))
				yield return candidate;
			yield break;
		}

		yield return type;
	}

	static IEnumerable<FieldDefinition> GetTypeFields(TypeDefinition type)
	{
		return type switch
		{
			ClassDefinition classDefinition => classDefinition.Fields,
			StructDefinition structDefinition => structDefinition.Fields,
			_ => []
		};
	}

	static bool IsFunctionNamed(FunctionDefinition function, string name)
	{
		return function.Name == name || function.Symbol == name;
	}

	static bool IsDefinitionNamed(Definition definition, string name)
	{
		return definition.Name == name || definition.Symbol == name;
	}

	static bool IsTypeFunctionSymbolNamed(TypeDefinition type, FunctionDefinition function, string name)
	{
		return (!string.IsNullOrWhiteSpace(function.Symbol) && function.Symbol != function.Name && function.Symbol == name)
			|| $"{type.Name}_{function.Name.TrimStart('~')}" == name;
	}

	VariableDefinition? LookupGlobalVariable(string name, SyntaxNode? referenceSyntax)
	{
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition is VariableDefinition variable && IsDefinitionNamed(variable, name) && IsDefinitionVisible(variable, referenceSyntax))
				return variable;
		}

		return null;
	}

	Definition? LookupHiddenGlobalSymbol(string name, SyntaxNode? referenceSyntax)
	{
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (IsDefinitionNamed(definition, name) && !IsDefinitionVisible(definition, referenceSyntax))
				return definition;
		}

		return null;
	}

	List<FunctionDefinition> LookupTypeFunctions(TypeDefinition type, string name, SyntaxNode? referenceSyntax)
	{
		List<FunctionDefinition> functions = [];
		if (type is ClassDefinition classDefinition)
		{
			foreach (ClassDefinition candidateClass in EnumerateClassAndBases(classDefinition))
			{
				foreach (FunctionDefinition function in candidateClass.Functions)
				{
					if (function.Name == name && !IsBodylessVirtualOverrideDeclaration(function) && IsMemberVisible(function, candidateClass, referenceSyntax))
						functions.Add(function);
				}

				if (functions.Count > 0)
					return functions;
			}

			return functions;
		}

		IEnumerable<FunctionDefinition> candidates = type switch
		{
			StructDefinition structDefinition => structDefinition.Functions,
			InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
			EnumDefinition enumDefinition => enumDefinition.Functions,
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions,
			ParamsDefinition paramsDefinition => paramsDefinition.Functions,
			_ => []
		};

		foreach (FunctionDefinition function in candidates)
		{
			if (function.Name == name && IsMemberVisible(function, type, referenceSyntax))
				functions.Add(function);
		}

		return functions;
	}

	FunctionDefinition? LookupHiddenTypeFunction(TypeDefinition type, string name, SyntaxNode? referenceSyntax)
	{
		if (type is ClassDefinition classDefinition)
		{
			foreach (ClassDefinition candidateClass in EnumerateClassAndBases(classDefinition))
			{
				foreach (FunctionDefinition function in candidateClass.Functions)
				{
					if (function.Name == name && !IsBodylessVirtualOverrideDeclaration(function) && !IsMemberVisible(function, candidateClass, referenceSyntax))
						return function;
				}
			}
			return null;
		}

		foreach (FunctionDefinition function in GetTypeFunctions(type))
		{
			if (function.Name == name && !IsMemberVisible(function, type, referenceSyntax))
				return function;
		}

		return null;
	}

	Definition? LookupHiddenMember(TypeDefinition type, string name, SyntaxNode? referenceSyntax)
	{
		if (LookupHiddenTypeFunction(type, name, referenceSyntax) is FunctionDefinition hiddenFunction)
			return hiddenFunction;

		switch (type)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
				{
					if (field.Name == name && !IsMemberVisible(field, classDefinition, referenceSyntax))
						return field;
				}
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
				{
					if (field.Name == name && !IsMemberVisible(field, structDefinition, referenceSyntax))
						return field;
				}
				break;

			case InterfaceDefinition interfaceDefinition:
				foreach (FunctionDefinition function in GetInterfaceMembers(interfaceDefinition))
				{
					if ((GetSignatureName(function) == name || function.Name == name) && !IsMemberVisible(function, interfaceDefinition, referenceSyntax))
						return function;
				}
				break;
		}

		return null;
	}

	bool IsBodylessVirtualOverrideDeclaration(FunctionDefinition function)
	{
		return function.Body is null
			&& function.Modifier is FunctionModifier.Override or FunctionModifier.Sealed
			&& virtualImplementations.ContainsKey(function);
	}

	List<FunctionDefinition> LookupMemberFunctions(string targetType, string name, SyntaxNode? referenceSyntax)
	{
		if (TryGetPointerElementType(targetType) is string interfaceElement
			&& typeDefinitions.TryGetValue(BaseTypeName(interfaceElement), out TypeDefinition? interfaceType)
			&& interfaceType is InterfaceDefinition interfaceDefinition)
		{
			List<FunctionDefinition> functions = [];
			foreach (FunctionDefinition function in GetInterfaceMembers(interfaceDefinition))
			{
				if ((GetSignatureName(function) == name || function.Name == name) && IsMemberVisible(function, interfaceDefinition, referenceSyntax))
				{
					if (ReceiverCanCallFunction(targetType, function, IsPropertyGetterFunction(function)))
						functions.Add(function);
				}
			}
			return functions;
		}

		if (!typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? type))
			return LookupExtensionFunctions(targetType, name, referenceSyntax);

		List<FunctionDefinition> callable = [];
		foreach (FunctionDefinition function in LookupTypeFunctions(type, name, referenceSyntax))
		{
			if (ReceiverCanCallFunction(targetType, function, IsPropertyGetterFunction(function)))
				callable.Add(function);
		}
		AddExtensionMemberFunctions(callable, targetType, name, referenceSyntax);
		return callable;
	}

	List<FunctionDefinition> LookupGenericConstraintMemberFunctions(string targetType, string name, BodyScope scope, SyntaxNode? referenceSyntax)
	{
		if (!TryGetGenericConstraintInterface(targetType, scope, out InterfaceDefinition? interfaceDefinition) || interfaceDefinition is null)
			return [];

		List<FunctionDefinition> functions = [];
		foreach (FunctionDefinition function in GetInterfaceMembers(interfaceDefinition))
		{
			if ((GetSignatureName(function) == name || function.Name == name) && IsMemberVisible(function, interfaceDefinition, referenceSyntax))
				functions.Add(function);
		}
		return functions;
	}

	List<BodySymbol> LookupMemberSymbols(string targetType, string name, SyntaxNode? referenceSyntax)
	{
		List<BodySymbol> members = [];
		if (TryGetParamsComponentShape(null, targetType, "value", out ParamsComponentShape expandedShape))
		{
			foreach (ParamsComponent component in expandedShape.Components)
				if (component.Name == name)
					members.Add(new BodySymbol(name, component.Type, new ParameterDefinition
					{
						Name = component.Name,
						Symbol = component.ExpandedName,
						ResolvedType = component.Type
					}));
			return members;
		}

		if (TryGetPointerElementType(targetType) is string interfaceElement
			&& typeDefinitions.TryGetValue(BaseTypeName(interfaceElement), out TypeDefinition? interfaceType)
			&& interfaceType is InterfaceDefinition interfaceDefinition)
		{
			foreach (FunctionDefinition function in GetInterfaceMembers(interfaceDefinition))
			{
				if ((GetSignatureName(function) == name || function.Name == name) && IsMemberVisible(function, interfaceDefinition, referenceSyntax))
					members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: true), function));
			}
			return members;
		}

		if (!typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? type))
		{
			foreach (FunctionDefinition function in LookupExtensionFunctions(targetType, name, referenceSyntax))
				members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: true), function));
			foreach (FunctionDefinition getter in LookupExtensionFunctions(targetType, "get" + name, referenceSyntax))
			{
				if (CanCallWithArgumentCount(getter.Parameters, 0))
					members.Add(new BodySymbol(name, getter.ResolvedType ?? ErrorType, getter));
			}
			return members;
		}

		switch (type)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
				{
					if (field.Name == name && IsMemberVisible(field, classDefinition, referenceSyntax))
						members.Add(new BodySymbol(name, field.ResolvedType ?? ErrorType, field));
					AddExpandedFieldMemberSymbol(members, field, name, classDefinition, referenceSyntax);
				}
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
				{
					if (field.Name == name && IsMemberVisible(field, structDefinition, referenceSyntax))
						members.Add(new BodySymbol(name, field.ResolvedType ?? ErrorType, field));
					AddExpandedFieldMemberSymbol(members, field, name, structDefinition, referenceSyntax);
				}
				break;

			case EnumDefinition enumDefinition:
				foreach (VariableDefinition value in enumDefinition.Values)
				{
					if (value.Name == name)
						members.Add(new BodySymbol(name, value.ResolvedType ?? enumDefinition.Name, value, IsConstant: true));
				}
				break;

			case ParamsDefinition paramsDefinition:
				foreach (ParameterDefinition component in paramsDefinition.Components)
				{
					if (component.Name == name)
						members.Add(new BodySymbol(name, component.ResolvedType ?? ErrorType, component));
				}
				break;
		}

		foreach (FunctionDefinition function in LookupTypeFunctions(type, name, referenceSyntax))
		{
			if (ReceiverCanCallFunction(targetType, function, IsPropertyGetterFunction(function)))
				members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: true), function));
		}
		foreach (FunctionDefinition function in LookupExtensionFunctions(targetType, name, referenceSyntax))
			members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: true), function));

		foreach (FunctionDefinition getter in LookupTypeFunctions(type, "get" + name, referenceSyntax))
		{
			if (getter.Parameters.Count == 0 && ReceiverCanCallFunction(targetType, getter, isPropertyGetterSyntax: true))
				members.Add(new BodySymbol(name, getter.ResolvedType ?? ErrorType, getter));
		}
		foreach (FunctionDefinition getter in LookupExtensionFunctions(targetType, "get" + name, referenceSyntax))
		{
			if (CanCallWithArgumentCount(getter.Parameters, 0))
				members.Add(new BodySymbol(name, getter.ResolvedType ?? ErrorType, getter));
		}

		return members;
	}

	List<BodySymbol> LookupGenericConstraintMemberSymbols(string targetType, string name, BodyScope scope, SyntaxNode? referenceSyntax)
	{
		if (!TryGetGenericConstraintInterface(targetType, scope, out InterfaceDefinition? interfaceDefinition) || interfaceDefinition is null)
			return [];

		List<BodySymbol> members = [];
		foreach (FunctionDefinition function in GetInterfaceMembers(interfaceDefinition))
		{
			if ((GetSignatureName(function) == name || function.Name == name) && IsMemberVisible(function, interfaceDefinition, referenceSyntax))
				members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: true), function));
		}
		return members;
	}

	bool TryGetGenericConstraintInterface(string targetType, BodyScope scope, out InterfaceDefinition? interfaceDefinition)
	{
		interfaceDefinition = null;
		string genericName = TryGetPointerElementType(targetType) ?? BaseTypeName(targetType);
		GenericParameter? parameter = FindBodyGenericParameter(scope, genericName);
		if (parameter is null || !parameter.RequiresImplementation || parameter.Constraint is null)
			return false;

		if (!TryGetInterfaceDefinition(parameter.Constraint, out interfaceDefinition) || interfaceDefinition is null)
			return false;

		return true;
	}

	static GenericParameter? FindBodyGenericParameter(BodyScope scope, string name)
	{
		foreach (GenericParameter parameter in scope.CurrentFunction.GenericParameters)
			if (parameter.Name == name)
				return parameter;
		if (scope.ContainingType is not null)
			foreach (GenericParameter parameter in scope.ContainingType.GenericParameters)
				if (parameter.Name == name)
					return parameter;
		return null;
	}

	void AddExpandedFieldMemberSymbol(List<BodySymbol> members, FieldDefinition field, string name, TypeDefinition owner, SyntaxNode? referenceSyntax)
	{
		if (!IsMemberVisible(field, owner, referenceSyntax))
			return;
		if (!TryGetParamsComponentShape(field.Type, field.ResolvedType, field.Name, out ParamsComponentShape shape))
			return;

		foreach (ParamsComponent component in shape.Components)
		{
			if (component.ExpandedName == field.Name || component.ExpandedName != name)
				continue;

			FieldDefinition componentField = new()
			{
				SourceSyntax = field.SourceSyntax,
				Name = component.ExpandedName,
				Symbol = component.ExpandedName,
				Export = field.Export,
				Extern = field.Extern,
				Modifier = field.Modifier,
				ResolvedType = component.Type
			};
			members.Add(new BodySymbol(name, component.Type, componentField));
			return;
		}
	}

	void AddExtensionMemberFunctions(List<FunctionDefinition> target, string targetType, string name, SyntaxNode? referenceSyntax)
	{
		foreach (FunctionDefinition function in LookupExtensionFunctions(targetType, name, referenceSyntax))
			target.Add(function);
	}

	List<FunctionDefinition> LookupExtensionFunctions(string targetType, string name, SyntaxNode? referenceSyntax)
	{
		List<FunctionDefinition> functions = [];
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition is not FunctionDefinition function || function.Name != name || !IsDefinitionVisible(function, referenceSyntax))
				continue;
			if (GetExplicitThisParameter(function) is null)
				continue;
			if (ReceiverCanCallFunction(targetType, function, IsPropertyGetterFunction(function)))
				functions.Add(function);
		}
		return functions;
	}

	bool TryAnalyzePropertyIndexer(MemberExpression member, List<ArgumentExpression> arguments, BodyScope scope, AnalysisScope typeScope, out string propertyType)
	{
		propertyType = ErrorType;
		string targetType = BodyAnalyzeExpression(member.Target, scope, typeScope);
		TypeDefinition? type = GetTypeDefinition(targetType);

		List<FunctionDefinition> getters = type is null ? [] : LookupPropertyGetters(type, member.Name, member.SourceSyntax);
		getters.AddRange(LookupExtensionFunctions(targetType, "get" + member.Name, member.SourceSyntax));
		bool getterReceiverMismatch = false;
		foreach (FunctionDefinition getter in getters)
		{
			if (!ReceiverCanCallFunction(targetType, getter, isPropertyGetterSyntax: true))
			{
				getterReceiverMismatch = true;
				continue;
			}

			if (CanCallWithArgumentCount(getter.Parameters, arguments.Count))
			{
				Dictionary<string, string> genericSubstitutions = [];
				AddReceiverTypeGenericSubstitutions(targetType, getter, genericSubstitutions);
				AnalyzeCallArguments(arguments, getter.Parameters, scope, typeScope, member.SourceSyntax, genericSubstitutions: genericSubstitutions, genericParameterNames: GetFunctionGenericParameterNames(getter));
				member.ResolvedType = SubstituteGenericType(getter.ResolvedType ?? ErrorType, genericSubstitutions);
				expressionRewrites[member] = CreateMemberReference(member, member.Target, member.ResolvedType, getter);
				propertyType = member.ResolvedType;
				return true;
			}
		}

		if (getterReceiverMismatch)
		{
			Report(GetRange(member.SourceSyntax), $"Property '{member.Name}' exists on type '{targetType}', but its getter's this parameter is not compatible with that receiver.");
			foreach (ArgumentExpression argument in arguments)
				BodyAnalyzeArgumentExpression(argument, scope, typeScope);
			return true;
		}

		List<FunctionDefinition> setters = type is null ? [] : LookupPropertySetters(type, member.Name, member.SourceSyntax);
		setters.AddRange(LookupExtensionFunctions(targetType, "set" + member.Name, member.SourceSyntax));
		if (setters.Count > 0)
		{
			Report(GetRange(member.SourceSyntax), $"Property '{member.Name}' is not readable on type '{targetType}'.");
			foreach (ArgumentExpression argument in arguments)
				BodyAnalyzeArgumentExpression(argument, scope, typeScope);
			return true;
		}

		return false;
	}

	bool TryAnalyzePropertySetter(MemberExpression member, List<ArgumentExpression> arguments, Expression? value, BodyScope scope, AnalysisScope typeScope, out string propertyType)
	{
		propertyType = ErrorType;
		string targetType = BodyAnalyzeExpression(member.Target, scope, typeScope);
		TypeDefinition? type = GetTypeDefinition(targetType);

		List<FunctionDefinition> setters = type is null ? [] : LookupPropertySetters(type, member.Name, member.SourceSyntax);
		setters.AddRange(LookupExtensionFunctions(targetType, "set" + member.Name, member.SourceSyntax));
		bool setterReceiverMismatch = false;
		foreach (FunctionDefinition setter in setters)
		{
			if (!ReceiverCanCallFunction(targetType, setter, isPropertyGetterSyntax: false))
			{
				setterReceiverMismatch = true;
				continue;
			}

			List<ParameterDefinition> callableParameters = GetCallableParameters(setter.Parameters);
			if (callableParameters.Count == 0)
				continue;

			int valueParameterIndex = callableParameters.Count - 1;
			int setterArgumentCount = callableParameters.Count - 1;
			if (CountRequiredParametersForPropertySetter(setter.Parameters) > arguments.Count)
				continue;
			if (setterArgumentCount != arguments.Count)
				continue;

			Dictionary<string, string> genericSubstitutions = [];
			AddReceiverTypeGenericSubstitutions(targetType, setter, genericSubstitutions);
			HashSet<string> genericParameterNames = GetFunctionGenericParameterNames(setter);
			for (int i = 0; i < arguments.Count; i++)
			{
				string expected = SubstituteGenericType(callableParameters[i].ResolvedType ?? ErrorType, genericSubstitutions);
				string actual = BodyAnalyzeArgumentExpression(arguments[i], scope, typeScope, expected);
				InferGenericSubstitutions(callableParameters[i].ResolvedType ?? ErrorType, actual, genericSubstitutions, genericParameterNames);
				expected = SubstituteGenericType(callableParameters[i].ResolvedType ?? ErrorType, genericSubstitutions);
				CheckAssignable(expected, actual, arguments[i].SourceSyntax, "Argument");
			}

			string expectedValueType = SubstituteGenericType(callableParameters[valueParameterIndex].ResolvedType ?? ErrorType, genericSubstitutions);
			string actualValueType = BodyAnalyzeExpression(value, scope, typeScope, expectedValueType);
			InferGenericSubstitutions(callableParameters[valueParameterIndex].ResolvedType ?? ErrorType, actualValueType, genericSubstitutions, genericParameterNames);
			expectedValueType = SubstituteGenericType(callableParameters[valueParameterIndex].ResolvedType ?? ErrorType, genericSubstitutions);
			CheckAssignable(expectedValueType, actualValueType, value?.SourceSyntax, "Assignment");

			member.ResolvedType = expectedValueType;
			expressionRewrites[member] = CreateMemberReference(member, member.Target, expectedValueType, setter);
			propertyType = expectedValueType;
			return true;
		}

		if (setterReceiverMismatch)
		{
			Report(GetRange(member.SourceSyntax), $"Property '{member.Name}' exists on type '{targetType}', but its setter's this parameter is not compatible with that receiver.");
			if (value is not null)
				BodyAnalyzeExpression(value, scope, typeScope);
			foreach (ArgumentExpression argument in arguments)
				BodyAnalyzeArgumentExpression(argument, scope, typeScope);
			return true;
		}

		List<FunctionDefinition> getters = type is null ? [] : LookupPropertyGetters(type, member.Name, member.SourceSyntax);
		getters.AddRange(LookupExtensionFunctions(targetType, "get" + member.Name, member.SourceSyntax));
		if (setters.Count == 0 && getters.Count == 0)
			return false;

		Report(GetRange(member.SourceSyntax), $"Property '{member.Name}' is not writable on type '{targetType}'.");
		if (value is not null)
			BodyAnalyzeExpression(value, scope, typeScope);
		foreach (ArgumentExpression argument in arguments)
			BodyAnalyzeArgumentExpression(argument, scope, typeScope);
		return true;
	}

	bool ReceiverCanCallFunction(string targetType, FunctionDefinition function, bool isPropertyGetterSyntax)
	{
		string receiverType = BuildEffectiveReceiverType(targetType, function, isPropertyGetterSyntax);
		string actualType = StripTopLevelConstForReceiver(targetType);
		if (CanImplicitlyConvert(actualType, receiverType))
			return true;
		return TryGetPointerElementType(receiverType) is string receiverElement
			&& TryGetPointerElementType(actualType) is null
			&& BaseTypeName(receiverElement) == BaseTypeName(actualType);
	}

	string BuildEffectiveReceiverType(string targetType, FunctionDefinition function, bool isPropertyGetterSyntax)
	{
		ThisParameterDefinition? explicitThis = GetExplicitThisParameter(function);
		TypeDefinition? owner = FindContainingType(function);
		string receiverType = owner is null
			? explicitThis?.ResolvedType ?? targetType
			: BuildOwnedReceiverType(targetType, owner);

		if (explicitThis is not null)
			return ApplyThisDeclarators(receiverType, explicitThis);

		return isPropertyGetterSyntax ? AddConstToReceiverInstance(receiverType) : receiverType;
	}

	static string BuildOwnedReceiverType(string targetType, TypeDefinition owner)
	{
		if (TryGetPointerElementType(targetType) is string elementType && BaseTypeName(elementType) == owner.Name)
			return $"{elementType}*";
		if (BaseTypeName(targetType) == owner.Name)
			return $"{targetType}*";
		return TryGetPointerElementType(targetType) is not null ? $"{owner.Name}*" : owner.Name;
	}

	static bool IsPropertyGetterFunction(FunctionDefinition function)
	{
		return function.Name.StartsWith("get", StringComparison.Ordinal)
			&& function.Name.Length > "get".Length
			&& function.ResolvedType != "void";
	}

	static ThisParameterDefinition? GetExplicitThisParameter(FunctionDefinition function)
	{
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter is ThisParameterDefinition thisParameter)
				return thisParameter;
		}

		return null;
	}

	static string ApplyThisDeclarators(string receiverType, ThisParameterDefinition thisParameter)
	{
		if (thisParameter.SourceSyntax is not ThisParameterSyntax { Declarators: not null } syntax)
			return receiverType;

		string result = receiverType;
		foreach (TypeDeclaratorSyntax declarator in syntax.Declarators)
		{
			result = declarator.Keyword?.Value switch
			{
				"const" => AddConstToReceiverInstance(result),
				"volatile" => AddTopLevelVolatileToReceiverInstance(result),
				"escaped" => AddTopLevelLifetimeToReceiver(result, "escaped"),
				"scoped" => AddTopLevelLifetimeToReceiver(result, "scoped"),
				"unscoped" => AddTopLevelLifetimeToReceiver(result, "unscoped"),
				_ => result
			};
		}

		return result;
	}

	FunctionDefinition? LookupConstructor(string targetType, int argumentCount)
	{
		if (!typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? type))
			return null;

		IEnumerable<FunctionDefinition> functions = type switch
		{
			ClassDefinition classDefinition => classDefinition.Functions,
			StructDefinition structDefinition => structDefinition.Functions,
			_ => []
		};

		foreach (FunctionDefinition function in functions)
		{
			if (function.Modifier == FunctionModifier.Constructor && CanCallWithArgumentCount(function.Parameters, argumentCount))
				return function;
		}

		return null;
	}

	static FunctionDefinition? FindGeneratedInitNewMethod(TypeDefinition? type)
	{
		if (type is null)
			return null;

		foreach (FunctionDefinition function in GetTypeFunctions(type))
		{
			if (function.Name == InitNewMethodName)
				return function;
		}
		return null;
	}

	FunctionDefinition? FindVirtualImplementationByName(ClassDefinition owner, string name)
	{
		string slotName = name == DeleteMethodName ? DeleteMethodName : name;
		foreach (ClassDefinition candidate in EnumerateClassAndBases(owner))
		{
			foreach (FunctionDefinition function in candidate.Functions)
			{
				if (!virtualImplementations.TryGetValue(function, out FunctionDefinition? implementation))
					continue;
				if (VirtualSlotName(function) == slotName)
					return implementation;
			}
		}
		return null;
	}

	ClassDefinition? GetDirectBaseClass(TypeDefinition definition)
	{
		foreach (TypeDefinition baseType in GetDirectBaseClasses(definition))
		{
			if (baseType is ClassDefinition baseClass)
				return baseClass;
		}

		return null;
	}

	bool HasAccessibleParameterlessConstructor(ClassDefinition definition)
	{
		return TryGetAccessibleParameterlessConstructor(definition, out _);
	}

	bool TryGetAccessibleParameterlessConstructor(ClassDefinition definition, out FunctionDefinition? constructor)
	{
		constructor = null;
		bool hasConstructor = false;
		foreach (FunctionDefinition function in definition.Functions)
		{
			if (function.Modifier != FunctionModifier.Constructor)
				continue;

			hasConstructor = true;
			if (CountRequiredParameters(function.Parameters) == 0)
			{
				constructor = function;
				return true;
			}
		}

		if (!hasConstructor)
		{
			constructor = null;
			return true;
		}

		return false;
	}

	TypeDefinition? FindContainingType(FunctionDefinition function)
	{
		foreach (TypeDefinition type in typeDefinitions.Values)
		{
			foreach (FunctionDefinition candidate in GetTypeFunctions(type))
			{
				if (ReferenceEquals(candidate, function))
					return type;
			}
		}

		return null;
	}

	static IEnumerable<FunctionDefinition> GetTypeFunctions(TypeDefinition type)
	{
		return type switch
		{
			ClassDefinition classDefinition => classDefinition.Functions,
			StructDefinition structDefinition => structDefinition.Functions,
			InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
			EnumDefinition enumDefinition => enumDefinition.Functions,
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Functions,
			ParamsDefinition paramsDefinition => paramsDefinition.Functions,
			_ => []
		};
	}

	static int CountRequiredParameters(List<ParameterDefinition> parameters, bool includeExplicitThis = false)
	{
		int count = 0;
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter.DefaultValue is null
				&& parameter.Modifier is not ParameterModifier.Thrown and not ParameterModifier.Within
				&& (includeExplicitThis || parameter is not ThisParameterDefinition)
				&& parameter is not WithinParameterDefinition and not SizeOfParameterDefinition and not VTableOfParameterDefinition)
				count++;
		}
		return count;
	}

	static int CountCallableParameters(List<ParameterDefinition> parameters, bool includeExplicitThis = false)
	{
		int count = 0;
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter.Modifier is not ParameterModifier.Thrown and not ParameterModifier.Within
				&& (includeExplicitThis || parameter is not ThisParameterDefinition)
				&& parameter is not WithinParameterDefinition)
				count++;
		}
		return count;
	}

	static bool CanCallWithArgumentCount(List<ParameterDefinition> parameters, int argumentCount, bool includeExplicitThis = false)
	{
		return CountRequiredParameters(parameters, includeExplicitThis) <= argumentCount && argumentCount <= CountCallableParameters(parameters, includeExplicitThis);
	}

	static List<ParameterDefinition> GetCallableParameters(List<ParameterDefinition> parameters, bool includeExplicitThis = false)
	{
		List<ParameterDefinition> callable = [];
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter.Modifier is ParameterModifier.Thrown or ParameterModifier.Within
				|| !includeExplicitThis && parameter is ThisParameterDefinition
				|| parameter is WithinParameterDefinition)
				continue;

			callable.Add(parameter);
		}
		return callable;
	}

	static int CountRequiredParametersForPropertySetter(List<ParameterDefinition> parameters)
	{
		if (parameters.Count == 0)
			return 0;

		int count = 0;
		for (int i = 0; i < parameters.Count - 1; i++)
		{
			ParameterDefinition parameter = parameters[i];
			if (parameter.DefaultValue is null
				&& parameter.Modifier is not ParameterModifier.Out and not ParameterModifier.Thrown
				&& parameter is not ThisParameterDefinition and not WithinParameterDefinition and not SizeOfParameterDefinition and not VTableOfParameterDefinition)
				count++;
		}

		return count;
	}

	List<FunctionDefinition> LookupPropertyGetters(TypeDefinition type, string name, SyntaxNode? referenceSyntax)
	{
		return LookupTypeFunctions(type, "get" + name, referenceSyntax);
	}

	List<FunctionDefinition> LookupPropertySetters(TypeDefinition type, string name, SyntaxNode? referenceSyntax)
	{
		return LookupTypeFunctions(type, "set" + name, referenceSyntax);
	}

	bool HasPropertyGetterWithIncompatibleReceiver(TypeDefinition type, string targetType, string name, SyntaxNode? referenceSyntax)
	{
		foreach (FunctionDefinition getter in LookupPropertyGetters(type, name, referenceSyntax))
		{
			if (!ReceiverCanCallFunction(targetType, getter, isPropertyGetterSyntax: true))
				return true;
		}

		return false;
	}

	bool HasMemberFunctionWithIncompatibleReceiver(TypeDefinition type, string targetType, string name, SyntaxNode? referenceSyntax)
	{
		foreach (FunctionDefinition function in LookupTypeFunctions(type, name, referenceSyntax))
		{
			if (IsMemberVisible(function, type, referenceSyntax) && !ReceiverCanCallFunction(targetType, function, IsPropertyGetterFunction(function)))
				return true;
		}

		return false;
	}

	TypeDefinition? GetTypeDefinition(string typeName)
	{
		return typeDefinitions.TryGetValue(BaseTypeName(typeName), out TypeDefinition? type) ? type : null;
	}

	string ReportMultipleCandidates(SyntaxNode? syntax, string name)
	{
		Report(GetRange(syntax), $"Multiple member candidates found for '{name}'.");
		return ErrorType;
	}

	string ReportType(SyntaxNode? syntax, string message)
	{
		Report(GetRange(syntax), message);
		return ErrorType;
	}

	static string? TryGetArrayElementType(string? type)
	{
		return new TypeShapeParser(type ?? "").TryParse(out TypeShape shape) && shape.Kind == TypeShapeKind.Array
			? TypeShapeParser.Format(shape.Element)
			: null;
	}

	static string? TryGetPointerElementType(string? type)
	{
		return new TypeShapeParser(type ?? "").TryParse(out TypeShape shape) && shape.Kind == TypeShapeKind.Pointer
			? TypeShapeParser.Format(shape.Element)
			: null;
	}

	string? GetIteratorElementType(TypeReference? type)
	{
		if (type is IterTypeReference { ElementType: not null } iter)
			return iter.ElementType.ResolvedType;

		string? resolved = type?.ResolvedType;
		if (resolved is null)
			return null;
		if (resolved.StartsWith("iter ", StringComparison.Ordinal))
			return resolved["iter ".Length..];
		if (resolved.StartsWith("async iter ", StringComparison.Ordinal))
			return resolved["async iter ".Length..];
		if (resolved.StartsWith("iter(", StringComparison.Ordinal))
			return ExtractFirstIteratorSlotType(resolved, "iter(");
		if (resolved.StartsWith("async iter(", StringComparison.Ordinal))
			return ExtractFirstIteratorSlotType(resolved, "async iter(");
		return null;
	}

	static string? ExtractFirstIteratorSlotType(string resolved, string prefix)
	{
		string slots = resolved[prefix.Length..];
		if (slots.EndsWith(")", StringComparison.Ordinal))
			slots = slots[..^1];
		int comma = slots.IndexOf(',', StringComparison.Ordinal);
		string first = comma < 0 ? slots : slots[..comma];
		return first.Trim();
	}

	string BestType(List<string> types)
	{
		if (types.Count == 0)
			return ErrorType;

		string best = types[0];
		foreach (string type in types)
		{
			if (CanImplicitlyConvert(best, type))
				best = type;
			else if (!CanImplicitlyConvert(type, best))
				return ErrorType;
		}

		return best;
	}

	static string GetNumberLiteralType(string text, string? targetType)
	{
		targetType = targetType is null ? null : StripTopLevelValueQualifiers(targetType);
		if (targetType is not null && IsNumericTypeName(targetType))
			return targetType;

		return text.Contains('.', StringComparison.Ordinal) ? "double" : "int";
	}

	static string PromoteInteger(string type)
	{
		type = StripTopLevelValueQualifiers(type);
		return type is "byte" or "sbyte" or "ushort" or "short" or "char" or "wchar" or "achar" or "uchar"
			? "int"
			: type;
	}

	static string UsualArithmeticConversion(string left, string right)
	{
		left = PromoteInteger(left);
		right = PromoteInteger(right);
		return NumericRank(left) >= NumericRank(right) ? left : right;
	}

	static bool IsNumericTypeName(string type)
	{
		type = StripTopLevelValueQualifiers(type);
		if (new TypeShapeParser(type).TryParse(out TypeShape shape) && shape.Kind == TypeShapeKind.Named)
			type = shape.Name;
		return IsIntegralTypeName(type) || type is "float" or "double";
	}

	bool IsNumericType(string type)
	{
		return IsNumericTypeName(type) || TryGetUnderlyingNumericType(type, out _);
	}

	bool IsIntegralType(string type)
	{
		return IsIntegralTypeName(type) || IsEnumType(type) || TryGetUnderlyingNumericType(type, out string? underlying) && underlying is not null && IsIntegralTypeName(underlying);
	}

	static bool IsIntegralTypeName(string type)
	{
		type = StripTopLevelValueQualifiers(type);
		if (new TypeShapeParser(type).TryParse(out TypeShape shape) && shape.Kind == TypeShapeKind.Named)
			type = shape.Name;
		return type is "byte" or "sbyte" or "ushort" or "short" or "uint" or "int" or "ulong" or "long" or "nuint" or "nint" or "char" or "wchar" or "achar" or "uchar";
	}

	static int NumericRank(string type)
	{
		type = StripTopLevelValueQualifiers(type);
		if (new TypeShapeParser(type).TryParse(out TypeShape shape) && shape.Kind == TypeShapeKind.Named)
			type = shape.Name;
		return type switch
		{
			"byte" or "sbyte" => 1,
			"ushort" or "short" or "char" or "wchar" or "achar" or "uchar" => 2,
			"uint" or "int" => 3,
			"nuint" or "nint" => 4,
			"ulong" or "long" => 5,
			"float" => 6,
			"double" => 7,
			_ => 100
		};
	}

	bool IsEnumType(string type)
	{
		return typeDefinitions.TryGetValue(BaseTypeName(type), out TypeDefinition? definition) && definition is EnumDefinition;
	}

	bool TryGetUnderlyingNumericType(string type, out string? underlying)
	{
		underlying = null;
		if (!typeDefinitions.TryGetValue(BaseTypeName(type), out TypeDefinition? definition))
			return false;

		TypeReference? underlyingType = definition switch
		{
			EnumDefinition enumDefinition => enumDefinition.UnderlyingType ?? new PrimitiveTypeReference { Type = PrimitiveType.Int },
			NewtypeDefinition newtypeDefinition => newtypeDefinition.UnderlyingType,
			_ => null
		};

		underlying = underlyingType?.ResolvedType;
		if (underlying is null && underlyingType is PrimitiveTypeReference primitive)
			underlying = GetPrimitiveTypeName(primitive.Type);
		return underlying is not null && IsNumericTypeName(underlying);
	}

	bool TryGetNewtypeUnderlyingType(string type, out string? underlying)
	{
		underlying = null;
		if (!typeDefinitions.TryGetValue(BaseTypeName(type), out TypeDefinition? definition) || definition is not NewtypeDefinition newtypeDefinition)
			return false;

		underlying = newtypeDefinition.UnderlyingType?.ResolvedType;
		if (underlying is null && newtypeDefinition.UnderlyingType is PrimitiveTypeReference primitive)
			underlying = GetPrimitiveTypeName(primitive.Type);

		return underlying is not null;
	}

	static bool IsConstantVariable(VariableDefinition variable)
	{
		return IsConstType(variable.Type) || IsConstQualified(variable.Type?.ResolvedType);
	}

	static bool IsConstType(TypeReference? type)
	{
		return type switch
		{
			ConstTypeReference => true,
			AttributedTypeReference attributed => IsConstType(attributed.Type),
			_ => false
		};
	}

	static bool IsConstCharPointerType(string? type)
	{
		return type == "const char*";
	}

	bool CanConvertPrimitiveStringToPointer(string source, string target)
	{
		string sourceElement = StripTopLevelValueQualifiers(source) switch
		{
			"string" => "char",
			"wstring" => "wchar",
			"astring" => "achar",
			_ => ""
		};
		if (sourceElement.Length == 0)
			return false;
		if (!TryParseTypeShape(target, out TypeShape targetShape) || targetShape.Kind != TypeShapeKind.Pointer || targetShape.Element is null)
			return false;
		string targetElement = TypeShapeParser.Format(targetShape.Element);
		string unqualifiedTargetElement = StripTopLevelValueQualifiers(targetElement);
		if (unqualifiedTargetElement != sourceElement)
			return false;
		return !IsConstQualified(source) || IsConstQualified(targetElement);
	}

	string GetStringLiteralType(LiteralExpression literal, string? targetType)
	{
		if (targetType is null || targetType == TargetType || targetType == AutoType)
			return "const string";

		if (IsStringLiteralTargetType(targetType))
			return targetType;

		Report(GetRange(literal.SourceSyntax), $"String literal cannot implicitly convert to mutable type '{targetType}'.");
		return ErrorType;
	}

	static bool IsStringLiteralTargetType(string? type)
	{
		return type is "const char*"
			or "const wchar*"
			or "const achar*"
			or "const string"
			or "const wstring"
			or "const astring"
			or "const char[]"
			or "const wchar[]"
			or "const achar[]";
	}

	static bool IsPrimitiveStringType(string? type)
	{
		type = StripTopLevelValueQualifiers(type ?? "");
		return type is "string" or "wstring" or "astring";
	}

	static string? GetPrimitiveStringElementType(string? type)
	{
		type = StripTopLevelValueQualifiers(type ?? "");
		return type switch
		{
			"string" => "char",
			"wstring" => "wchar",
			"astring" => "achar",
			_ => null
		};
	}

	string BuildFunctionValueType(FunctionDefinition function, bool isInstance)
	{
		string kind = isInstance ? "delegate" : "fn";
		List<string> parameters = [];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (isInstance && parameter is ThisParameterDefinition)
				continue;

			parameters.Add(parameter.ResolvedType ?? ErrorType);
		}

		return $"{kind} {function.ResolvedType ?? ErrorType}({string.Join(", ", parameters)})";
	}

	MemberReferenceExpression CreateMemberReference(MemberExpression member, Expression? target, string type, BindableNode node)
	{
		MemberReferenceExpression reference = new()
		{
			SourceSyntax = member.SourceSyntax,
			Target = target,
			Name = member.Name,
			Member = node,
			ResolvedType = type
		};
		if (node is FunctionDefinition function)
			reference.Candidates.Add(function);
		return reference;
	}
}
