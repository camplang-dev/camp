using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void AnalyzeInlineConstantsAndEnumValues(Module module)
	{
		foreach (Definition definition in module.Definitions)
			AnalyzeInlineConstantsAndEnumValues(definition);
		ValidateInlineAndEnumSymbols(module);
	}

	void AnalyzeInlineConstantsAndEnumValues(Definition definition)
	{
		switch (definition)
		{
			case VariableDefinition variable when variable.IsInline:
				AnalyzeInlineVariable(variable, []);
				break;

			case ClassDefinition classDefinition:
				AnalyzeInlineFields(classDefinition.Fields);
				break;

			case StructDefinition structDefinition:
				AnalyzeInlineFields(structDefinition.Fields);
				break;

			case NewtypeDefinition newtypeDefinition:
				AnalyzeInlineFields(newtypeDefinition.Fields);
				break;

			case EnumDefinition enumDefinition:
				AnalyzeEnumValues(enumDefinition);
				break;
		}
	}

	void AnalyzeInlineFields(IEnumerable<FieldDefinition> fields)
	{
		foreach (FieldDefinition field in fields)
			if (field.IsInline)
				AnalyzeInlineField(field, []);
	}

	void AnalyzeInlineVariable(VariableDefinition variable, HashSet<BindableNode> visiting)
	{
		if (!ValidateInlineType(variable.Type, variable.ResolvedType, variable.SourceSyntax))
			return;
		if (variable.InitialValue is null)
			return;
		if (TryEvaluateInlineConstant(variable, variable.InitialValue, variable.ResolvedType ?? variable.Type?.ResolvedType, visiting, out ConstantValue? value))
			variable.ConstantValue = value;
	}

	void AnalyzeInlineField(FieldDefinition field, HashSet<BindableNode> visiting)
	{
		if (!ValidateInlineType(field.Type, field.ResolvedType, field.SourceSyntax))
			return;
		if (field.InitialValue is null)
			return;
		if (TryEvaluateInlineConstant(field, field.InitialValue, field.ResolvedType ?? field.Type?.ResolvedType, visiting, out ConstantValue? value))
			field.ConstantValue = value;
	}

	void AnalyzeEnumValues(EnumDefinition enumDefinition)
	{
		if (enumDefinition.UnderlyingType is null)
		{
			enumDefinition.UnderlyingType = new PrimitiveTypeReference
			{
				Type = PrimitiveType.UInt,
				ResolvedType = "uint"
			};
		}

		string underlyingType = StripTopLevelValueQualifiers(enumDefinition.UnderlyingType.ResolvedType ?? "uint");
		if (!IsAllowedEnumUnderlyingType(underlyingType))
			Report(GetRange(enumDefinition.UnderlyingType.SourceSyntax ?? enumDefinition.SourceSyntax), $"Enum underlying type must be one of: sbyte, byte, short, ushort, int, uint, long, ulong, nint, or nuint.");

		BigInteger next = BigInteger.Zero;
		foreach (VariableDefinition value in enumDefinition.Values)
		{
			BigInteger numeric = next;
			if (value.InitialValue is not null)
			{
				if (TryEvaluateInlineConstant(value, value.InitialValue, underlyingType, [], out ConstantValue? constant) && constant is ConstantValue.Integer integer)
					numeric = integer.Value;
				else
					Report(GetRange(value.InitialValue.SourceSyntax ?? value.SourceSyntax), $"Enum value '{value.Name}' must be an integer constant.");
			}

			if (!ValueFitsIntegerType(numeric, underlyingType, out bool unsigned))
			{
				string message = unsigned && numeric < BigInteger.Zero
					? $"Enum value '{value.Name}' cannot be negative because enum '{enumDefinition.Name}' uses unsigned underlying type '{underlyingType}'."
					: $"Enum value '{value.Name}' is outside the range of underlying type '{underlyingType}'.";
				Report(GetRange(value.InitialValue?.SourceSyntax ?? value.SourceSyntax), message);
			}

			value.ConstantValue = new ConstantValue.Integer(numeric);
			next = numeric + BigInteger.One;
		}
	}

	bool ValidateInlineType(TypeReference? type, string? resolvedType, SyntaxNode? syntax)
	{
		string typeName = StripTopLevelValueQualifiers(resolvedType ?? type?.ResolvedType ?? "");
		if (string.IsNullOrWhiteSpace(typeName) || typeName == ErrorType)
			return false;

		if (IsAllowedInlineScalarType(typeName)
			|| IsPrimitiveStringType(typeName)
			|| IsConstCharacterPointerType(resolvedType ?? typeName)
			|| IsAllowedInlinePointerType(resolvedType ?? typeName)
			|| IsAllowedInlineFnType(type, resolvedType))
			return true;

		Report(GetRange(syntax), $"Inline constant type '{resolvedType ?? typeName}' is not supported. Inline constants may only use scalar, enum, scalar/pointer newtype, pointer-null, fn-null, or string-like types.");
		return false;
	}

	bool TryEvaluateInlineConstant(BindableNode owner, Expression expression, string? targetType, HashSet<BindableNode> visiting, out ConstantValue? value)
	{
		value = null;
		if (!visiting.Add(owner))
		{
			Report(GetRange(expression.SourceSyntax ?? owner.SourceSyntax), "Inline constant initializer has a dependency cycle.");
			return false;
		}

		try
		{
			return TryEvaluateInlineConstantCore(owner, expression, targetType, visiting, out value);
		}
		finally
		{
			visiting.Remove(owner);
		}
	}

	bool TryEvaluateInlineConstantCore(BindableNode owner, Expression expression, string? targetType, HashSet<BindableNode> visiting, out ConstantValue? value)
	{
		value = null;
		if (expressionRewrites.TryGetValue(expression, out Expression? rewrite))
			return TryEvaluateInlineConstantCore(owner, rewrite, targetType, visiting, out value);
		bool evaluated = expression switch
		{
			LiteralExpression literal => TryEvaluateLiteralConstant(literal, targetType, out value),

			DefaultExpression => TryEvaluateDefaultConstant(targetType, out value),

			ParenthesizedExpression parenthesized =>
				parenthesized.Expression is not null && TryEvaluateInlineConstantCore(owner, parenthesized.Expression, targetType, visiting, out value),

			CastExpression cast =>
				cast.Expression is not null && TryEvaluateInlineConstantCore(owner, cast.Expression, cast.Type?.ResolvedType ?? targetType, visiting, out value),

			UnaryExpression unary => TryEvaluateUnaryConstant(owner, unary, targetType, visiting, out value),

			BinaryExpression binary => TryEvaluateBinaryConstant(owner, binary, targetType, visiting, out value),

			VariableReferenceExpression { Variable: VariableDefinition variable } =>
				TryEvaluateVariableReferenceConstant(variable, visiting, out value),

			VariableReferenceExpression { Variable: FieldDefinition field } =>
				TryEvaluateFieldReferenceConstant(field, visiting, out value),

			NamedExpression named when TryResolveNamedConstantReference(named, owner, out BindableNode? node) =>
				TryEvaluateNamedConstantReference(named, node, visiting, out value),

			_ => ReportNonConstantExpression(expression)
		};

		if (evaluated)
			MarkConstantExpressionResolved(expression, targetType, value);
		return evaluated;
	}

	bool TryEvaluateDefaultConstant(string? targetType, out ConstantValue? value)
	{
		value = IsStringLikeInlineType(targetType) ? new ConstantValue.Null() : new ConstantValue.Integer(BigInteger.Zero);
		return true;
	}

	bool TryEvaluateNamedConstantReference(NamedExpression named, BindableNode? node, HashSet<BindableNode> visiting, out ConstantValue? value)
	{
		bool result = node switch
		{
			VariableDefinition variable => TryEvaluateVariableReferenceConstant(variable, visiting, out value),
			FieldDefinition field => TryEvaluateFieldReferenceConstant(field, visiting, out value),
			_ => TryEvaluateUnknownNamedConstant(out value)
		};
		if (result && node is not null)
			named.ResolvedType = node.ResolvedType;
		return result;
	}

	static bool TryEvaluateUnknownNamedConstant(out ConstantValue? value)
	{
		value = null;
		return false;
	}

	bool ReportNonConstantExpression(Expression expression)
	{
		Report(GetRange(expression.SourceSyntax), "Inline constant initializer must be a compile-time constant expression.");
		return false;
	}

	static void MarkConstantExpressionResolved(Expression expression, string? targetType, ConstantValue? value)
	{
		if (!string.IsNullOrWhiteSpace(targetType) && (string.IsNullOrWhiteSpace(expression.ResolvedType) || expression.ResolvedType == ErrorType || expression.ResolvedType == UnresolvedType))
			expression.ResolvedType = targetType;
		else if (string.IsNullOrWhiteSpace(expression.ResolvedType) || expression.ResolvedType == ErrorType || expression.ResolvedType == UnresolvedType)
			expression.ResolvedType = value switch
			{
				ConstantValue.Boolean => "bool",
				ConstantValue.String => "string",
				ConstantValue.Character => "char",
				ConstantValue.Null => "null",
				_ => "int"
			};
	}

	bool TryEvaluateLiteralConstant(LiteralExpression literal, string? targetType, out ConstantValue? value)
	{
		value = literal.Kind switch
		{
			LiteralKind.True => new ConstantValue.Boolean(true),
			LiteralKind.False => new ConstantValue.Boolean(false),
			LiteralKind.Null => new ConstantValue.Null(),
			LiteralKind.String when IsStringLikeInlineType(targetType) => new ConstantValue.String(literal.Value as string ?? ""),
			LiteralKind.Character => new ConstantValue.Character(literal.Value as string ?? ""),
			LiteralKind.Number when TryParseIntegerConstantValue(literal.Text, out BigInteger integer) => new ConstantValue.Integer(integer),
			_ => null
		};
		if (value is not null)
			return true;

		if (literal.Kind == LiteralKind.String)
			Report(GetRange(literal.SourceSyntax), "String literals may initialize only string, wstring, astring, or const character pointer inline constants.");
		else
			Report(GetRange(literal.SourceSyntax), "Literal is not valid for this inline constant type.");
		return false;
	}

	bool TryResolveNamedConstantReference(NamedExpression named, BindableNode owner, out BindableNode? node)
	{
		node = null;
		if (named.Qualifiers.Count == 0)
		{
			if (TryFindContainingType(owner, out TypeDefinition? containingType) && containingType is not null)
			{
				foreach (FieldDefinition field in GetTypeFields(containingType))
					if (field.Modifier == FieldModifier.Static && (field.Name == named.Name || field.Symbol == named.Name))
					{
						node = field;
						return true;
					}

				if (containingType is EnumDefinition enumDefinition)
				{
					foreach (VariableDefinition value in enumDefinition.Values)
						if (value.Name == named.Name || value.Symbol == named.Name)
						{
							node = value;
							return true;
						}
				}
			}

			foreach (Definition definition in currentModule?.Definitions ?? [])
				if (definition is VariableDefinition variable && (variable.Name == named.Name || variable.Symbol == named.Name))
				{
					node = variable;
					return true;
				}
		}
		if (named.Qualifiers.Count == 1 && typeDefinitions.TryGetValue(named.Qualifiers[0], out TypeDefinition? type))
		{
			foreach (FieldDefinition field in GetTypeFields(type))
				if (field.Modifier == FieldModifier.Static && (field.Name == named.Name || field.Symbol == named.Name))
				{
					node = field;
					return true;
				}
		}
		return false;
	}

	bool TryFindContainingType(BindableNode owner, out TypeDefinition? type)
	{
		type = null;
		foreach (Definition definition in currentModule?.Definitions ?? [])
		{
			switch (definition)
			{
				case ClassDefinition classDefinition when owner is FieldDefinition field && classDefinition.Fields.Contains(field):
					type = classDefinition;
					return true;

				case StructDefinition structDefinition when owner is FieldDefinition field && structDefinition.Fields.Contains(field):
					type = structDefinition;
					return true;

				case NewtypeDefinition newtypeDefinition when owner is FieldDefinition field && newtypeDefinition.Fields.Contains(field):
					type = newtypeDefinition;
					return true;

				case EnumDefinition enumDefinition when owner is VariableDefinition value && enumDefinition.Values.Contains(value):
					type = enumDefinition;
					return true;
			}
		}
		return false;
	}

	bool TryEvaluateUnaryConstant(BindableNode owner, UnaryExpression unary, string? targetType, HashSet<BindableNode> visiting, out ConstantValue? value)
	{
		value = null;
		if (unary.Operand is null || !TryEvaluateInlineConstantCore(owner, unary.Operand, targetType, visiting, out ConstantValue? operand))
			return false;
		if (operand is not ConstantValue.Integer integer)
		{
			Report(GetRange(unary.SourceSyntax), "Unary inline constant expression requires an integer operand.");
			return false;
		}

		value = unary.Operator switch
		{
			UnaryOperator.Plus => integer,
			UnaryOperator.Minus => new ConstantValue.Integer(-integer.Value),
			UnaryOperator.BitwiseNot => new ConstantValue.Integer(~integer.Value),
			_ => null
		};
		if (value is not null)
			return true;

		Report(GetRange(unary.SourceSyntax), "Unsupported unary operator in inline constant initializer.");
		return false;
	}

	bool TryEvaluateBinaryConstant(BindableNode owner, BinaryExpression binary, string? targetType, HashSet<BindableNode> visiting, out ConstantValue? value)
	{
		value = null;
		if (binary.Left is null
			|| binary.Right is null
			|| !TryEvaluateInlineConstantCore(owner, binary.Left, targetType, visiting, out ConstantValue? left)
			|| !TryEvaluateInlineConstantCore(owner, binary.Right, targetType, visiting, out ConstantValue? right))
			return false;
		if (left is not ConstantValue.Integer leftInteger || right is not ConstantValue.Integer rightInteger)
		{
			Report(GetRange(binary.SourceSyntax), "Binary inline constant expression requires integer operands.");
			return false;
		}

		BigInteger r = rightInteger.Value;
		value = binary.Operator switch
		{
			BinaryOperator.Add => new ConstantValue.Integer(leftInteger.Value + r),
			BinaryOperator.Subtract => new ConstantValue.Integer(leftInteger.Value - r),
			BinaryOperator.Multiply => new ConstantValue.Integer(leftInteger.Value * r),
			BinaryOperator.Divide when r != BigInteger.Zero => new ConstantValue.Integer(leftInteger.Value / r),
			BinaryOperator.Modulo when r != BigInteger.Zero => new ConstantValue.Integer(leftInteger.Value % r),
			BinaryOperator.BitwiseAnd => new ConstantValue.Integer(leftInteger.Value & r),
			BinaryOperator.BitwiseOr => new ConstantValue.Integer(leftInteger.Value | r),
			BinaryOperator.BitwiseXor => new ConstantValue.Integer(leftInteger.Value ^ r),
			BinaryOperator.LeftShift when r >= BigInteger.Zero && r <= int.MaxValue => new ConstantValue.Integer(leftInteger.Value << (int)r),
			BinaryOperator.RightShift when r >= BigInteger.Zero && r <= int.MaxValue => new ConstantValue.Integer(leftInteger.Value >> (int)r),
			_ => null
		};
		if (value is not null)
			return true;

		Report(GetRange(binary.SourceSyntax), "Unsupported binary operator in inline constant initializer.");
		return false;
	}

	bool TryEvaluateVariableReferenceConstant(VariableDefinition variable, HashSet<BindableNode> visiting, out ConstantValue? value)
	{
		if (variable.ConstantValue is not null)
		{
			value = variable.ConstantValue;
			return true;
		}
		if (variable.IsInline)
		{
			AnalyzeInlineVariable(variable, visiting);
			value = variable.ConstantValue;
			return value is not null;
		}

		value = null;
		Report(GetRange(variable.SourceSyntax), $"Inline constant initializer cannot reference storage variable '{variable.Name}'.");
		return false;
	}

	bool TryEvaluateFieldReferenceConstant(FieldDefinition field, HashSet<BindableNode> visiting, out ConstantValue? value)
	{
		if (field.ConstantValue is not null)
		{
			value = field.ConstantValue;
			return true;
		}
		if (field.IsInline)
		{
			AnalyzeInlineField(field, visiting);
			value = field.ConstantValue;
			return value is not null;
		}

		value = null;
		Report(GetRange(field.SourceSyntax), $"Inline constant initializer cannot reference storage field '{field.Name}'.");
		return false;
	}

	static bool TryParseIntegerConstantValue(string text, out BigInteger value)
	{
		text = text.Replace("_", "", StringComparison.Ordinal).Trim();
		while (text.Length > 0 && char.IsLetter(text[^1]) && text[^1] is not 'x' and not 'X')
			text = text[..^1];
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			return BigInteger.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
		return BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
	}

	bool IsAllowedInlineScalarType(string type)
	{
		type = StripTopLevelValueQualifiers(type);
		if (IsNumericTypeName(type) || type == "bool")
			return true;
		if (typeDefinitions.TryGetValue(BaseTypeName(type), out TypeDefinition? definition))
			return definition is EnumDefinition || definition is NewtypeDefinition { UnderlyingType.ResolvedType: string underlying }
				&& (IsNumericTypeName(underlying) || TryGetPointerElementType(underlying) is not null || IsPrimitiveStringType(underlying));
		return false;
	}

	bool IsAllowedInlinePointerType(string type)
	{
		if (TryGetPointerElementType(type) is not string element)
			return false;
		if (TryGetArrayElementType(element) is not null)
			return false;
		if (IsExpandedFormResolvedType(element))
			return false;
		return true;
	}

	static bool IsAllowedInlineFnType(TypeReference? type, string? resolvedType)
	{
		return type is CallableTypeReference { Kind: CallableKind.Function }
			|| (resolvedType?.StartsWith("fn ", StringComparison.Ordinal) ?? false)
			|| (resolvedType?.StartsWith("fn_", StringComparison.Ordinal) ?? false);
	}

	static bool IsAllowedEnumUnderlyingType(string type)
	{
		type = StripTopLevelValueQualifiers(type);
		return type is "sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "nint" or "nuint";
	}

	bool ValueFitsIntegerType(BigInteger value, string type, out bool unsigned)
	{
		type = StripTopLevelValueQualifiers(type);
		unsigned = type is "byte" or "ushort" or "uint" or "ulong" or "nuint";
		int bits = type switch
		{
			"sbyte" or "byte" => 8,
			"short" or "ushort" => 16,
			"int" or "uint" => 32,
			"long" or "ulong" => 64,
			"nint" or "nuint" => selectedTarget?.GetNaturalIntegerWidth(null) ?? 32,
			_ => 32
		};

		BigInteger min = unsigned ? BigInteger.Zero : -(BigInteger.One << (bits - 1));
		BigInteger max = unsigned ? (BigInteger.One << bits) - BigInteger.One : (BigInteger.One << (bits - 1)) - BigInteger.One;
		return value >= min && value <= max;
	}

	static bool IsStringLikeInlineType(string? type)
	{
		type = StripTopLevelValueQualifiers(type ?? "");
		return IsPrimitiveStringType(type) || IsConstCharacterPointerType(type);
	}

	static bool IsConstCharacterPointerType(string? type)
	{
		type ??= "";
		if (TryGetPointerElementType(type) is not string element)
			return false;
		element = StripTopLevelValueQualifiers(element);
		return element is "char" or "wchar" or "achar" && IsConstQualified(type.Replace("*", "", StringComparison.Ordinal).Trim());
	}

	static bool IsExpandedFormResolvedType(string type)
	{
		return TryGetArrayElementType(type) is not null
			|| type.Contains('?', StringComparison.Ordinal)
			|| type.StartsWith("delegate ", StringComparison.Ordinal)
			|| type.StartsWith("iter ", StringComparison.Ordinal);
	}

	void ValidateInlineAndEnumSymbols(Module module)
	{
		Dictionary<string, BindableNode> generatedSymbols = new(StringComparer.Ordinal);
		foreach (Definition definition in module.Definitions)
			ValidateInlineAndEnumSymbols(definition, generatedSymbols);
	}

	void ValidateInlineAndEnumSymbols(Definition definition, Dictionary<string, BindableNode> generatedSymbols)
	{
		switch (definition)
		{
			case VariableDefinition variable when variable.IsInline:
				ValidateGeneratedMacroSymbol(variable.Symbol, variable, generatedSymbols);
				break;

			case ClassDefinition classDefinition:
				ValidateInlineFieldSymbols(classDefinition.Fields, generatedSymbols);
				break;

			case StructDefinition structDefinition:
				ValidateInlineFieldSymbols(structDefinition.Fields, generatedSymbols);
				break;

			case NewtypeDefinition newtypeDefinition:
				ValidateInlineFieldSymbols(newtypeDefinition.Fields, generatedSymbols);
				break;

			case EnumDefinition enumDefinition:
				ValidateGeneratedMacroSymbol(enumDefinition.Symbol, enumDefinition, generatedSymbols);
				foreach (VariableDefinition value in enumDefinition.Values)
					ValidateGeneratedMacroSymbol(value.Symbol, value, generatedSymbols);
				break;
		}
	}

	void ValidateInlineFieldSymbols(IEnumerable<FieldDefinition> fields, Dictionary<string, BindableNode> generatedSymbols)
	{
		foreach (FieldDefinition field in fields)
			if (field.IsInline)
				ValidateGeneratedMacroSymbol(field.Symbol, field, generatedSymbols);
	}

	void ValidateGeneratedMacroSymbol(string symbol, BindableNode node, Dictionary<string, BindableNode> generatedSymbols)
	{
		if (string.IsNullOrWhiteSpace(symbol))
			return;
		if (symbol.EndsWith("_H_", StringComparison.Ordinal) && node is VariableDefinition or FieldDefinition)
			Report(GetNameRange((Definition)node), $"Inline constant symbol '{symbol}' must not end with '_H_'.");
		if (generatedSymbols.TryGetValue(symbol, out BindableNode? existing) && !ReferenceEquals(existing, node))
			Report(GetNameRange((Definition)node), $"Duplicate generated symbol name '{symbol}'.");
		else
			generatedSymbols[symbol] = node;
	}
}
