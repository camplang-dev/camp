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

	bool CanImplicitlyConvert(string source, string target)
	{
		if (source == target || source == ErrorType || target == ErrorType || target == TargetType)
			return true;

		if (source == "#NULL" && (target.EndsWith("*", StringComparison.Ordinal) || target.EndsWith("?", StringComparison.Ordinal)))
			return true;

		if (source == AllocatorType && target == "Allocator*")
			return true;

		if (TryGetCallableShape(source, out CallableShape sourceCallable) && TryGetCallableShape(target, out CallableShape targetCallable))
			return CallableShapesCompatible(sourceCallable, targetCallable);

		if (IsClassToInterfaceConversion(source, target) || IsStructToInterfaceConversion(source, target) || IsInterfaceUpcast(source, target))
			return true;

		if (IsNewtypeOrEnumBoundary(source, target))
			return false;

		return IsNumericType(source) && IsNumericType(target) && NumericRank(source) <= NumericRank(target);
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

		return source.EndsWith("*", StringComparison.Ordinal) && target.EndsWith("*", StringComparison.Ordinal);
	}

	bool IsNewtypeOrEnumBoundary(string source, string target)
	{
		return TryGetUnderlyingNumericType(source, out _) || TryGetUnderlyingNumericType(target, out _);
	}

	string GetForeachElementType(string sourceType, bool isAwaited, SyntaxNode? syntax)
	{
		if (TryGetArrayElementType(sourceType) is string arrayElement)
			return isAwaited ? ReportType(syntax, "await foreach requires an async iter source, not an array.") : arrayElement;

		if (sourceType.StartsWith("iter ", StringComparison.Ordinal))
			return isAwaited ? ReportType(syntax, "await foreach requires an async iter source, not an iter source.") : sourceType["iter ".Length..];

		if (sourceType.StartsWith("async iter ", StringComparison.Ordinal))
			return sourceType["async iter ".Length..];

		Report(GetRange(syntax), $"Foreach source type '{sourceType}' is not iterable.");
		return ErrorType;
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

	List<FunctionDefinition> LookupFunctions(string name, BodyScope scope)
	{
		List<FunctionDefinition> functions = [];
		if (scope.ContainingType is not null)
			functions.AddRange(LookupTypeFunctions(scope.ContainingType, name));

		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition is FunctionDefinition function && function.Name == name && IsDefinitionVisible(function, scope.CurrentFunction.SourceSyntax))
				functions.Add(function);
		}

		return functions;
	}

	VariableDefinition? LookupGlobalVariable(string name, SyntaxNode? referenceSyntax)
	{
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition is VariableDefinition variable && variable.Name == name && IsDefinitionVisible(variable, referenceSyntax))
				return variable;
		}

		return null;
	}

	Definition? LookupHiddenGlobalSymbol(string name, SyntaxNode? referenceSyntax)
	{
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			if (definition.Name == name && !IsDefinitionVisible(definition, referenceSyntax))
				return definition;
		}

		return null;
	}

	List<FunctionDefinition> LookupTypeFunctions(TypeDefinition type, string name)
	{
		List<FunctionDefinition> functions = [];
		if (type is ClassDefinition classDefinition)
		{
			foreach (ClassDefinition candidateClass in EnumerateClassAndBases(classDefinition))
			{
				foreach (FunctionDefinition function in candidateClass.Functions)
				{
					if (function.Name == name && !IsBodylessVirtualOverrideDeclaration(function))
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
			if (function.Name == name)
				functions.Add(function);
		}

		return functions;
	}

	bool IsBodylessVirtualOverrideDeclaration(FunctionDefinition function)
	{
		return function.Body is null
			&& function.Modifier is FunctionModifier.Override or FunctionModifier.Sealed
			&& virtualImplementations.ContainsKey(function);
	}

	List<FunctionDefinition> LookupMemberFunctions(string targetType, string name)
	{
		if (TryGetPointerElementType(targetType) is string interfaceElement
			&& typeDefinitions.TryGetValue(BaseTypeName(interfaceElement), out TypeDefinition? interfaceType)
			&& interfaceType is InterfaceDefinition interfaceDefinition)
		{
			List<FunctionDefinition> functions = [];
			foreach (FunctionDefinition function in GetInterfaceMembers(interfaceDefinition))
			{
				if (GetSignatureName(function) == name || function.Name == name)
					functions.Add(function);
			}
			return functions;
		}

		if (!typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? type))
			return [];

		return LookupTypeFunctions(type, name);
	}

	List<BodySymbol> LookupMemberSymbols(string targetType, string name)
	{
		List<BodySymbol> members = [];
		if (TryGetPointerElementType(targetType) is string interfaceElement
			&& typeDefinitions.TryGetValue(BaseTypeName(interfaceElement), out TypeDefinition? interfaceType)
			&& interfaceType is InterfaceDefinition interfaceDefinition)
		{
			foreach (FunctionDefinition function in GetInterfaceMembers(interfaceDefinition))
			{
				if (GetSignatureName(function) == name || function.Name == name)
					members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: true), function));
			}
			return members;
		}

		if (!typeDefinitions.TryGetValue(BaseTypeName(targetType), out TypeDefinition? type))
			return members;

		switch (type)
		{
			case ClassDefinition classDefinition:
				foreach (FieldDefinition field in classDefinition.Fields)
				{
					if (field.Name == name)
						members.Add(new BodySymbol(name, field.ResolvedType ?? ErrorType, field));
				}
				break;

			case StructDefinition structDefinition:
				foreach (FieldDefinition field in structDefinition.Fields)
				{
					if (field.Name == name)
						members.Add(new BodySymbol(name, field.ResolvedType ?? ErrorType, field));
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

		foreach (FunctionDefinition function in LookupTypeFunctions(type, name))
			members.Add(new BodySymbol(name, BuildFunctionValueType(function, isInstance: true), function));

		foreach (FunctionDefinition getter in LookupTypeFunctions(type, "get" + name))
		{
			if (getter.Parameters.Count == 0)
				members.Add(new BodySymbol(name, getter.ResolvedType ?? ErrorType, getter));
		}

		return members;
	}

	bool TryAnalyzePropertyIndexer(MemberExpression member, List<ArgumentExpression> arguments, BodyScope scope, AnalysisScope typeScope, out string propertyType)
	{
		propertyType = ErrorType;
		string targetType = BodyAnalyzeExpression(member.Target, scope, typeScope);
		if (GetTypeDefinition(targetType) is not TypeDefinition type)
			return false;

		foreach (FunctionDefinition getter in LookupPropertyGetters(type, member.Name))
		{
			if (CountRequiredParameters(getter.Parameters) <= arguments.Count)
			{
				AnalyzeCallArguments(arguments, getter.Parameters, scope, typeScope);
				member.ResolvedType = getter.ResolvedType ?? ErrorType;
				expressionRewrites[member] = CreateMemberReference(member, member.Target, member.ResolvedType, getter);
				propertyType = member.ResolvedType;
				return true;
			}
		}

		if (LookupPropertySetters(type, member.Name).Count > 0)
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
		if (GetTypeDefinition(targetType) is not TypeDefinition type)
			return false;

		List<FunctionDefinition> setters = LookupPropertySetters(type, member.Name);
		foreach (FunctionDefinition setter in setters)
		{
			if (setter.Parameters.Count == 0)
				continue;

			int valueParameterIndex = setter.Parameters.Count - 1;
			int setterArgumentCount = setter.Parameters.Count - 1;
			if (CountRequiredParametersForPropertySetter(setter.Parameters) > arguments.Count)
				continue;
			if (setterArgumentCount != arguments.Count)
				continue;

			for (int i = 0; i < arguments.Count; i++)
			{
				string expected = setter.Parameters[i].ResolvedType ?? ErrorType;
				string actual = BodyAnalyzeArgumentExpression(arguments[i], scope, typeScope, expected);
				CheckAssignable(expected, actual, arguments[i].SourceSyntax, "Argument");
			}

			string expectedValueType = setter.Parameters[valueParameterIndex].ResolvedType ?? ErrorType;
			string actualValueType = BodyAnalyzeExpression(value, scope, typeScope, expectedValueType);
			CheckAssignable(expectedValueType, actualValueType, value?.SourceSyntax, "Assignment");

			member.ResolvedType = expectedValueType;
			expressionRewrites[member] = CreateMemberReference(member, member.Target, expectedValueType, setter);
			propertyType = expectedValueType;
			return true;
		}

		if (setters.Count == 0 && LookupPropertyGetters(type, member.Name).Count == 0)
			return false;

		Report(GetRange(member.SourceSyntax), $"Property '{member.Name}' is not writable on type '{targetType}'.");
		if (value is not null)
			BodyAnalyzeExpression(value, scope, typeScope);
		foreach (ArgumentExpression argument in arguments)
			BodyAnalyzeArgumentExpression(argument, scope, typeScope);
		return true;
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

	static int CountRequiredParameters(List<ParameterDefinition> parameters)
	{
		int count = 0;
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter.DefaultValue is null
				&& parameter.Modifier is not ParameterModifier.Thrown and not ParameterModifier.Within
				&& parameter is not ThisParameterDefinition and not WithinParameterDefinition and not SizeOfParameterDefinition and not VTableOfParameterDefinition)
				count++;
		}
		return count;
	}

	static int CountCallableParameters(List<ParameterDefinition> parameters)
	{
		int count = 0;
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter.Modifier is not ParameterModifier.Thrown and not ParameterModifier.Within
				&& parameter is not ThisParameterDefinition and not WithinParameterDefinition and not SizeOfParameterDefinition and not VTableOfParameterDefinition)
				count++;
		}
		return count;
	}

	static bool CanCallWithArgumentCount(List<ParameterDefinition> parameters, int argumentCount)
	{
		return CountRequiredParameters(parameters) <= argumentCount && argumentCount <= CountCallableParameters(parameters);
	}

	static List<ParameterDefinition> GetCallableParameters(List<ParameterDefinition> parameters)
	{
		List<ParameterDefinition> callable = [];
		foreach (ParameterDefinition parameter in parameters)
		{
			if (parameter.Modifier is ParameterModifier.Thrown or ParameterModifier.Within
				|| parameter is ThisParameterDefinition or WithinParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition)
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
				&& parameter is not WithinParameterDefinition and not SizeOfParameterDefinition and not VTableOfParameterDefinition)
				count++;
		}

		return count;
	}

	List<FunctionDefinition> LookupPropertyGetters(TypeDefinition type, string name)
	{
		return LookupTypeFunctions(type, "get" + name);
	}

	List<FunctionDefinition> LookupPropertySetters(TypeDefinition type, string name)
	{
		return LookupTypeFunctions(type, "set" + name);
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
		return type is not null && type.EndsWith("[]", StringComparison.Ordinal) ? type[..^2] : null;
	}

	static string? TryGetPointerElementType(string? type)
	{
		return type is not null && type.EndsWith("*", StringComparison.Ordinal) ? type[..^1] : null;
	}

	string? GetIteratorElementType(TypeReference? type)
	{
		if (type is IterTypeReference { ElementType: not null } iter)
			return iter.ElementType.ResolvedType;

		string? resolved = type?.ResolvedType;
		return resolved is not null && resolved.StartsWith("iter ", StringComparison.Ordinal) ? resolved["iter ".Length..] : null;
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
		if (targetType is not null && IsNumericTypeName(targetType))
			return targetType;

		return text.Contains('.', StringComparison.Ordinal) ? "double" : "int";
	}

	static string PromoteInteger(string type)
	{
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
		return type is "byte" or "sbyte" or "ushort" or "short" or "uint" or "int" or "ulong" or "long" or "nuint" or "nint" or "char" or "wchar" or "achar" or "uchar";
	}

	static int NumericRank(string type)
	{
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
		return IsConstType(variable.Type) || variable.Type?.ResolvedType?.StartsWith("const ", StringComparison.Ordinal) == true;
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

	static bool IsCharPointerType(string? type)
	{
		return type is "char*" or "const char*";
	}

	string BuildFunctionValueType(FunctionDefinition function, bool isInstance)
	{
		string kind = isInstance ? "delegate" : "fn";
		List<string> parameters = [];
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter is ThisParameterDefinition or SizeOfParameterDefinition or VTableOfParameterDefinition)
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
