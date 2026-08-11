using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	Expression LowerInterpolatedStringExpression(InterpolatedStringExpression interpolation)
	{
		if (currentStatementPrefix is null)
		{
			Report(GetRange(interpolation.SourceSyntax), "Interpolated string result storage cannot be created in this expression position yet.");
			return interpolation;
		}

		string resultType = interpolation.ResolvedType ?? ErrorType;
		if (resultType == ErrorType)
			return interpolation;

		if (!TryBuildInterpolatedParts(interpolation, out List<InterpolationPart> parts))
			return interpolation;

		return LowerEagerInterpolatedStringExpression(interpolation, resultType, parts);
	}

	bool TryRewriteFixedInterpolatedStringDeclaration(DeclarationStatement declaration, out List<Statement> statements)
	{
		statements = [];
		if (declaration.Target.Names.Count != 1
			|| !TryGetFixedArrayShape(declaration.Target.ResolvedType ?? declaration.Target.Type?.ResolvedType, out string elementType, out long length)
			|| StripTopLevelValueQualifiers(elementType) != "char"
			|| !TryGetInterpolatedStringDeclarationInitializer(declaration.InitialValue, out InterpolatedStringExpression interpolation)
			|| interpolation.HeapAllocated)
		{
			return false;
		}

		declaration.InitialValue = new DefaultExpression
		{
			SourceSyntax = declaration.SourceSyntax,
			ResolvedType = declaration.Target.ResolvedType
		};
		statements.Add(declaration);

		List<Statement>? previousStatementPrefix = currentStatementPrefix;
		try
		{
			currentStatementPrefix = statements;
			if (!TryBuildInterpolatedParts(interpolation, out List<InterpolationPart> parts))
				return true;
			LowerInterpolatedStringIntoFixedArrayStorage(
				interpolation,
				CreateVariableReference(declaration.Target, declaration.Target.ResolvedType ?? ErrorType, interpolation.SourceSyntax),
				NumberLiteral(length.ToString(CultureInfo.InvariantCulture), "nuint"),
				parts);
		}
		finally
		{
			currentStatementPrefix = previousStatementPrefix;
		}

		return true;
	}

	static bool TryGetInterpolatedStringDeclarationInitializer(Expression? expression, out InterpolatedStringExpression interpolation)
	{
		switch (expression)
		{
			case InterpolatedStringExpression value:
				interpolation = value;
				return true;

			case WithinExpression within:
				return TryGetInterpolatedStringDeclarationInitializer(within.Expression, out interpolation);

			case ParenthesizedExpression parenthesized:
				return TryGetInterpolatedStringDeclarationInitializer(parenthesized.Expression, out interpolation);

			case CastExpression cast:
				return TryGetInterpolatedStringDeclarationInitializer(cast.Expression, out interpolation);

			default:
				interpolation = null!;
				return false;
		}
	}

	bool TryBuildInterpolatedParts(InterpolatedStringExpression interpolation, out List<InterpolationPart> parts)
	{
		parts = [];
		foreach (InterpolatedStringSegment segment in interpolation.Segments)
		{
			switch (segment)
			{
				case InterpolatedStringTextSegment text:
					if (text.Text.Length > 0)
						AddTextPart(parts, text.Text, text.SourceSyntax);
					break;

				case InterpolatedStringExpressionSegment { Formatter: null } hole:
					if (TryGetConstantInterpolationText(hole, out string constantText) && constantText.Length > 0)
						AddTextPart(parts, constantText, hole.SourceSyntax);
					break;

				case InterpolatedStringExpressionSegment { Formatter: not null } hole:
					if (!interpolationParts.TryGetValue(hole, out InterpolationPart? part))
					{
						Report(GetRange(hole.Formatter.SourceSyntax ?? hole.SourceSyntax), "Interpolated string hole formatter was not resolved.");
						return false;
					}
					parts.Add(part);
					break;
			}
		}

		return true;
	}

	static void AddTextPart(List<InterpolationPart> parts, string text, SyntaxNode? syntax)
	{
		if (parts.Count > 0 && parts[^1] is LiteralTextPart previous)
		{
			parts[^1] = previous with { Text = previous.Text + text };
			return;
		}
		parts.Add(new LiteralTextPart(text, syntax));
	}

	Expression LowerEagerInterpolatedStringExpression(InterpolatedStringExpression interpolation, string resultType, List<InterpolationPart> parts)
	{
		const string elementType = "char";
		const string lengthType = "nuint";
		parts = MaterializePreparedFormatterCalls(parts);
		parts = MaterializeDirectTextValues(parts);
		long constantLength = 0;
		foreach (InterpolationPart part in parts)
			if (part is LiteralTextPart text)
				constantLength += StringLiteralEncoding.GetElementCount(text.Text, elementType);

		DeclarationStatement required = CreateGeneratedLocal(NewGeneratedLocalName("interpolatedRequired"), lengthType, TypeReferenceForResolvedName(lengthType), NumberLiteral(constantLength.ToString(CultureInfo.InvariantCulture), lengthType));
		currentStatementPrefix!.Add(required);
		Expression Required() => CreateVariableReference(required.Target, lengthType);

		List<DeclarationStatement> componentSizes = [];
		foreach (InterpolationPart part in parts)
		{
			switch (part)
			{
				case LiteralTextPart:
					break;

				case DirectTextPart directText:
					Expression directTextSizeExpression = directText.Kind switch
					{
						DirectTextKind.Char => NumberLiteral("1", lengthType),
						DirectTextKind.CharArray => directText.LengthValue ?? NumberLiteral("0", lengthType),
						_ => CreateLengthExpression(directText.Value, directText.SourceSyntax) ?? NumberLiteral("0", lengthType)
					};
					directTextSizeExpression = LowerExpression(directTextSizeExpression) ?? directTextSizeExpression;
					DeclarationStatement directTextSize = CreateGeneratedLocal(
						NewGeneratedLocalName("interpolatedPartSize"),
						lengthType,
						TypeReferenceForResolvedName(lengthType),
						directTextSizeExpression);
					currentStatementPrefix.Add(directTextSize);
					componentSizes.Add(directTextSize);
					currentStatementPrefix.Add(Assign(
						Required(),
						Add(Required(), CreateVariableReference(directTextSize.Target, lengthType), lengthType),
						lengthType,
						part.SourceSyntax));
					break;

				case PreparedFormatterPart prepared:
					Expression preparedSizeCall = CreatePreparedFormatterCall(prepared, DefaultBuffer(prepared.Shape));
					preparedSizeCall = LowerExpression(preparedSizeCall) ?? preparedSizeCall;
					DeclarationStatement preparedSize = CreateGeneratedLocal(
						NewGeneratedLocalName("interpolatedPartSize"),
						prepared.Shape.LengthType,
						TypeReferenceForResolvedName(prepared.Shape.LengthType),
						preparedSizeCall);
					currentStatementPrefix.Add(preparedSize);
					componentSizes.Add(preparedSize);
					currentStatementPrefix.Add(Assign(
						Required(),
						Add(Required(), CreateVariableReference(preparedSize.Target, prepared.Shape.LengthType), lengthType),
						lengthType,
						part.SourceSyntax));
					break;
			}
		}

		Expression allocationLength = interpolation.NullTerminated
			? Add(Required(), NumberLiteral("1", lengthType), lengthType)
			: Required();
		DeclarationStatement bufferLength = CreateGeneratedLocal(NewGeneratedLocalName("interpolatedBufferLength"), lengthType, TypeReferenceForResolvedName(lengthType), allocationLength);
		currentStatementPrefix.Add(bufferLength);
		Expression BufferLengthValue() => CreateVariableReference(bufferLength.Target, lengthType);

		TypeReference charType = TypeReferenceForResolvedName(elementType);
		Expression allocation = interpolation.HeapAllocated
			? CreateAllocCall(charType, CurrentAllocator(), interpolation.SourceSyntax, BufferLengthValue())
			: CreateStackAllocCall(charType, interpolation.SourceSyntax, BufferLengthValue());
		DeclarationStatement bufferElements = CreateGeneratedLocal(NewGeneratedLocalName("interpolatedBufferElements"), "char*", PointerTo(charType), allocation);
		currentStatementPrefix.Add(bufferElements);
		Expression BufferElements() => CreateVariableReference(bufferElements.Target, "char*");
		Expression BufferArray() => CreateArrayView(BufferElements(), BufferLengthValue(), "char[]", "char*", lengthType, interpolation.SourceSyntax);

		DeclarationStatement offset = CreateGeneratedLocal(NewGeneratedLocalName("interpolatedOffset"), lengthType, TypeReferenceForResolvedName(lengthType), NumberLiteral("0", lengthType));
		currentStatementPrefix.Add(offset);
		Expression Offset() => CreateVariableReference(offset.Target, lengthType);

		int sizeIndex = 0;
		foreach (InterpolationPart part in parts)
		{
			switch (part)
			{
				case LiteralTextPart text:
					AddLiteralWritesToPointer(currentStatementPrefix, BufferElements, BufferLengthValue, Offset, elementType, lengthType, text.Text, text.SourceSyntax, exactAppend: true);
					break;

				case DirectTextPart directText:
					DeclarationStatement directTextSize = componentSizes[sizeIndex++];
					Expression source = DirectTextSourceExpression(directText);
					currentStatementPrefix.Add(new BufferCopyStatement
					{
						SourceSyntax = part.SourceSyntax,
						ResolvedType = "void",
						Buffer = BufferElements(),
						Offset = Offset(),
						Source = source,
						Count = CreateVariableReference(directTextSize.Target, lengthType),
						ElementType = elementType,
						LengthType = lengthType
					});
					currentStatementPrefix.Add(Assign(
						Offset(),
						Add(Offset(), CreateVariableReference(directTextSize.Target, lengthType), lengthType),
						lengthType,
						part.SourceSyntax));
					break;

				case PreparedFormatterPart prepared:
					DeclarationStatement preparedSize = componentSizes[sizeIndex++];
					DeclarationStatement preparedStart = AddClampedOffsetLocal(currentStatementPrefix, BufferLengthValue, Offset, prepared.Shape.LengthType, part.SourceSyntax);
					Expression preparedWriteCall = CreatePreparedFormatterCall(prepared, BufferSlice(BufferElements(), BufferLengthValue(), CreateVariableReference(preparedStart.Target, prepared.Shape.LengthType), prepared.Shape, part.SourceSyntax));
					preparedWriteCall = LowerExpression(preparedWriteCall) ?? preparedWriteCall;
					currentStatementPrefix.Add(new ExpressionStatement
					{
						SourceSyntax = part.SourceSyntax,
						ResolvedType = "void",
						Expression = preparedWriteCall
					});
					currentStatementPrefix.Add(Assign(
						Offset(),
						Add(Offset(), CreateVariableReference(preparedSize.Target, prepared.Shape.LengthType), lengthType),
						lengthType,
						part.SourceSyntax));
					break;
			}
		}

		if (interpolation.NullTerminated)
			currentStatementPrefix.Add(Assign(BufferIndex(BufferElements(), Required(), elementType, interpolation.SourceSyntax), CharacterLiteral('\0', elementType, interpolation.SourceSyntax), elementType, interpolation.SourceSyntax));

		if (IsPrimitiveStringType(resultType))
		{
			CastExpression result = new()
			{
				SourceSyntax = interpolation.SourceSyntax,
				Kind = CastKind.Type,
				Type = TypeReferenceForResolvedName(resultType),
				Expression = BufferElements(),
				ResolvedType = resultType
			};
			knownStringLengths[result] = Required();
			return result;
		}

		if (resultType == "char[]")
			return BufferArray();

		if (TryGetArrayElementType(resultType) is string resultElement && StripTopLevelValueQualifiers(resultElement) == elementType)
			return new InitializerExpression
			{
				SourceSyntax = interpolation.SourceSyntax,
				ResolvedType = resultType,
				Items =
				{
					new InitializerItem
					{
						SourceSyntax = interpolation.SourceSyntax,
						Expression = BufferElements(),
						ResolvedType = resultElement + "*"
					},
					new InitializerItem
					{
						SourceSyntax = interpolation.SourceSyntax,
						Expression = BufferLengthValue(),
						ResolvedType = lengthType
					}
				}
			};

		Report(GetRange(interpolation.SourceSyntax), $"Interpolated string target '{resultType}' cannot be lowered yet.");
		return interpolation;
	}

	void LowerInterpolatedStringIntoFixedArrayStorage(InterpolatedStringExpression interpolation, Expression bufferElements, Expression bufferLength, List<InterpolationPart> parts)
	{
		const string elementType = "char";
		const string lengthType = "nuint";
		parts = MaterializePreparedFormatterCalls(parts);
		parts = MaterializeDirectTextValues(parts);
		List<DeclarationStatement> componentSizes = [];
		foreach (InterpolationPart part in parts)
		{
			if (part is LiteralTextPart)
				continue;

			FormatterShape formatter = part switch
			{
				PreparedFormatterPart prepared => prepared.Shape,
				DirectTextPart => new FormatterShape("char[]", "char[]", "char", lengthType),
				_ => throw new InvalidOperationException("Unknown interpolation part.")
			};
			Expression sizeCall = part switch
			{
				PreparedFormatterPart prepared => CreatePreparedFormatterCall(prepared, DefaultBuffer(prepared.Shape)),
				DirectTextPart directText => directText.Kind switch
				{
					DirectTextKind.Char => NumberLiteral("1", lengthType),
					DirectTextKind.CharArray => directText.LengthValue ?? NumberLiteral("0", lengthType),
					_ => CreateLengthExpression(directText.Value, directText.SourceSyntax) ?? NumberLiteral("0", lengthType)
				},
				_ => throw new InvalidOperationException("Unknown interpolation part.")
			};
			sizeCall = LowerExpression(sizeCall) ?? sizeCall;
			DeclarationStatement size = CreateGeneratedLocal(
				NewGeneratedLocalName("interpolatedPartSize"),
				formatter.LengthType,
				TypeReferenceForResolvedName(formatter.LengthType),
				sizeCall);
			currentStatementPrefix!.Add(size);
			componentSizes.Add(size);
		}

		DeclarationStatement offset = CreateGeneratedLocal(NewGeneratedLocalName("interpolatedOffset"), lengthType, TypeReferenceForResolvedName(lengthType), NumberLiteral("0", lengthType));
		currentStatementPrefix!.Add(offset);
		Expression Offset() => CreateVariableReference(offset.Target, lengthType);

		int sizeIndex = 0;
		foreach (InterpolationPart part in parts)
		{
			switch (part)
			{
				case LiteralTextPart text:
					AddLiteralWritesToPointer(currentStatementPrefix, () => CloneParamsExpansionExpression(bufferElements) ?? bufferElements, () => CloneParamsExpansionExpression(bufferLength) ?? bufferLength, Offset, elementType, lengthType, text.Text, text.SourceSyntax);
					break;

				case DirectTextPart directText:
					DeclarationStatement directTextSize = componentSizes[sizeIndex++];
					DeclarationStatement directTextStart = AddClampedOffsetLocal(currentStatementPrefix, () => CloneParamsExpansionExpression(bufferLength) ?? bufferLength, Offset, lengthType, part.SourceSyntax);
					Expression directTextSource = DirectTextSourceExpression(directText);
					currentStatementPrefix.Add(new BufferCopyStatement
					{
						SourceSyntax = part.SourceSyntax,
						ResolvedType = "void",
						Buffer = CloneParamsExpansionExpression(bufferElements) ?? bufferElements,
						Offset = CreateVariableReference(directTextStart.Target, lengthType),
						Source = directTextSource,
						Count = MinLength(CreateVariableReference(directTextSize.Target, lengthType), Subtract(CloneParamsExpansionExpression(bufferLength) ?? bufferLength, CreateVariableReference(directTextStart.Target, lengthType), lengthType), lengthType, part.SourceSyntax),
						ElementType = elementType,
						LengthType = lengthType
					});
					currentStatementPrefix.Add(Assign(
						Offset(),
						Add(Offset(), CreateVariableReference(directTextSize.Target, lengthType), lengthType),
						lengthType,
						part.SourceSyntax));
					break;

				case PreparedFormatterPart prepared:
					DeclarationStatement preparedSize = componentSizes[sizeIndex++];
					DeclarationStatement preparedStart = AddClampedOffsetLocal(currentStatementPrefix, () => CloneParamsExpansionExpression(bufferLength) ?? bufferLength, Offset, prepared.Shape.LengthType, part.SourceSyntax);
					Expression preparedWriteCall = CreatePreparedFormatterCall(prepared, BufferSlice(CloneParamsExpansionExpression(bufferElements) ?? bufferElements, CloneParamsExpansionExpression(bufferLength) ?? bufferLength, CreateVariableReference(preparedStart.Target, prepared.Shape.LengthType), prepared.Shape, part.SourceSyntax));
					preparedWriteCall = LowerExpression(preparedWriteCall) ?? preparedWriteCall;
					currentStatementPrefix.Add(new ExpressionStatement
					{
						SourceSyntax = part.SourceSyntax,
						ResolvedType = "void",
						Expression = preparedWriteCall
					});
					currentStatementPrefix.Add(Assign(
						Offset(),
						Add(Offset(), CreateVariableReference(preparedSize.Target, prepared.Shape.LengthType), lengthType),
						lengthType,
						part.SourceSyntax));
					break;
			}
		}
	}

	Expression DirectTextSourceExpression(DirectTextPart directText)
	{
		if (directText.Kind == DirectTextKind.Char)
		{
			return new UnaryExpression
			{
				SourceSyntax = directText.SourceSyntax,
				Operator = UnaryOperator.AddressOf,
				Operand = directText.Value,
				ResolvedType = "char*"
			};
		}
		if (directText.Kind == DirectTextKind.CharArray)
			return directText.Value;
		return directText.Value;
	}

	List<InterpolationPart> MaterializePreparedFormatterCalls(List<InterpolationPart> parts)
	{
		List<InterpolationPart> result = [];
		foreach (InterpolationPart part in parts)
		{
			if (part is PreparedFormatterPart prepared)
			{
				MaterializePreparedCallInputs(prepared.Call);
				result.Add(prepared);
			}
			else
			{
				result.Add(part);
			}
		}
		return result;
	}

	List<InterpolationPart> MaterializeDirectTextValues(List<InterpolationPart> parts)
	{
		List<InterpolationPart> result = [];
		foreach (InterpolationPart part in parts)
		{
			if (part is DirectTextPart directText)
			{
				if (directText.Kind == DirectTextKind.CharArray)
				{
					result.Add(MaterializeDirectCharArrayTextValue(directText));
					continue;
				}
				Expression value = MaterializeDirectTextValue(directText);
				result.Add(directText with { Value = value });
			}
			else
				result.Add(part);
		}
		return result;
	}

	DirectTextPart MaterializeDirectCharArrayTextValue(DirectTextPart part)
	{
		if (!TryCreateParamsComponentExpressions(part.Value, out List<Expression> components) || components.Count != 2)
			return part;

		Expression elements = MaterializeDirectTextComponent(components[0], components[0].ResolvedType ?? "char*");
		Expression length = MaterializeDirectTextComponent(components[1], components[1].ResolvedType ?? "nuint");
		return part with
		{
			Value = elements,
			ValueType = elements.ResolvedType ?? part.ValueType,
			LengthValue = length
		};
	}

	Expression MaterializeDirectTextComponent(Expression component, string targetType)
	{
		Expression value = CloneParamsExpansionExpression(component) ?? component;
		if (value is LiteralExpression || IsConstant(value))
			return value;

		Expression lowered = LowerExpression(value) ?? value;
		DeclarationStatement local = CreateGeneratedLocal(
			NewGeneratedLocalName("interpolatedValue"),
			targetType,
			TypeReferenceForResolvedName(targetType),
			lowered);
		currentStatementPrefix!.Add(local);
		return CreateVariableReference(local.Target, targetType, value.SourceSyntax);
	}

	Expression MaterializeDirectTextValue(DirectTextPart part)
	{
		Expression value = CloneParamsExpansionExpression(part.Value) ?? part.Value;
		if (value is LiteralExpression || IsConstant(value))
			return value;

		string targetType = value.ResolvedType ?? ErrorType;
		Expression lowered = LowerExpression(value) ?? value;
		DeclarationStatement local = CreateGeneratedLocal(
			NewGeneratedLocalName("interpolatedValue"),
			targetType,
			TypeReferenceForResolvedName(targetType),
			lowered);
		currentStatementPrefix!.Add(local);
		return CreateVariableReference(local.Target, targetType, value.SourceSyntax);
	}

	CallExpression CreatePreparedFormatterCall(PreparedFormatterPart part, Expression buffer)
	{
		bool includeExplicitThis = IncludeExplicitThisArgumentForSourceBinding(part.Call.Target, part.Function);
		List<ParameterDefinition> callableParameters = GetCallableParametersForCall(part.Function, includeExplicitThis);
		ParameterDefinition? prepParameter = null;
		foreach (ParameterDefinition parameter in callableParameters)
		{
			if (parameter.Modifier == ParameterModifier.Prep)
			{
				prepParameter = parameter;
				break;
			}
		}
		if (prepParameter is null)
			return part.Call;
		return CreatePreparedProtocolCall(part.Call, part.Function, callableParameters, prepParameter, buffer);
	}

	bool TryGetConstantInterpolationText(InterpolatedStringExpressionSegment hole, out string text)
	{
		StringBuilder builder = new();
		if (TryAppendConstantInterpolationHole(hole.Expression, hole.Expression?.ResolvedType ?? ErrorType, builder))
		{
			text = builder.ToString();
			return true;
		}

		Report(GetRange(hole.Expression?.SourceSyntax ?? hole.SourceSyntax), "Interpolated string hole has no formatter.");
		text = "";
		return false;
	}

	void AddLiteralWritesToPointer(List<Statement> statements, Func<Expression> bufferElements, Func<Expression> bufferLength, Func<Expression> offset, string elementType, string lengthType, string text, SyntaxNode? syntax, bool exactAppend = false)
	{
		if (text.Length == 0)
			return;

		int elementCount = StringLiteralEncoding.GetElementCount(text, elementType);
		if (exactAppend)
		{
			statements.Add(new LiteralCopyStatement
			{
				SourceSyntax = syntax,
				ResolvedType = "void",
				Buffer = bufferElements(),
				Offset = offset(),
				Count = LengthLiteral(elementCount, lengthType),
				ElementType = elementType,
				LengthType = lengthType,
				Text = text,
				ExactAppend = true
			});
			statements.Add(Assign(offset(), Add(offset(), LengthLiteral(elementCount, lengthType), lengthType), lengthType, syntax));
			return;
		}

		DeclarationStatement start = CreateGeneratedLocal(
			NewGeneratedLocalName("interpolatedCopyStart"),
			lengthType,
			TypeReferenceForResolvedName(lengthType),
			MinLength(offset(), bufferLength(), lengthType, syntax));
		statements.Add(start);

		Expression remaining = Subtract(bufferLength(), CreateVariableReference(start.Target, lengthType), lengthType);
		DeclarationStatement count = CreateGeneratedLocal(
			NewGeneratedLocalName("interpolatedCopyCount"),
			lengthType,
			TypeReferenceForResolvedName(lengthType),
			MinLength(LengthLiteral(elementCount, lengthType), remaining, lengthType, syntax));
		statements.Add(count);

		BlockStatement copyBody = CreateBlock([
			new LiteralCopyStatement
			{
				SourceSyntax = syntax,
				ResolvedType = "void",
				Buffer = bufferElements(),
				Offset = CreateVariableReference(start.Target, lengthType),
				Count = CreateVariableReference(count.Target, lengthType),
				ElementType = elementType,
				LengthType = lengthType,
				Text = text
			}
		]);
		statements.Add(new IfStatement
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Condition = GreaterThan(CreateVariableReference(count.Target, lengthType), NumberLiteral("0", lengthType), syntax),
			Body = copyBody
		});
		statements.Add(Assign(offset(), Add(offset(), LengthLiteral(elementCount, lengthType), lengthType), lengthType, syntax));
	}

	DeclarationStatement AddClampedOffsetLocal(List<Statement> statements, Func<Expression> bufferLength, Func<Expression> offset, string lengthType, SyntaxNode? syntax)
	{
		DeclarationStatement start = CreateGeneratedLocal(
			NewGeneratedLocalName("interpolatedStart"),
			lengthType,
			TypeReferenceForResolvedName(lengthType),
			MinLength(offset(), bufferLength(), lengthType, syntax));
		statements.Add(start);
		return start;
	}

	InitializerExpression DefaultBuffer(FormatterShape formatter)
	{
		return new InitializerExpression
		{
			ResolvedType = formatter.BufferType,
			Items =
			{
				new InitializerItem { Expression = NullLiteral(null), ResolvedType = "void*" },
				new InitializerItem { Expression = NumberLiteral("0", formatter.LengthType), ResolvedType = formatter.LengthType }
			}
		};
	}

	InitializerExpression BufferSlice(Expression elements, Expression bufferLength, Expression offset, FormatterShape formatter, SyntaxNode? syntax)
	{
		return new InitializerExpression
		{
			SourceSyntax = syntax,
			ResolvedType = formatter.BufferType,
			Items =
			{
				new InitializerItem
				{
					SourceSyntax = syntax,
					Expression = new UnaryExpression
					{
						SourceSyntax = syntax,
						Operator = UnaryOperator.AddressOf,
						Operand = BufferIndex(elements, offset, formatter.ElementType, syntax),
						ResolvedType = formatter.ElementType + "*"
					},
					ResolvedType = formatter.ElementType + "*"
				},
				new InitializerItem
				{
					SourceSyntax = syntax,
					Expression = Subtract(bufferLength, CloneParamsExpansionExpression(offset) ?? offset, formatter.LengthType),
					ResolvedType = formatter.LengthType
				}
			}
		};
	}

	InitializerExpression CreateArrayView(Expression elements, Expression length, string arrayType, string elementsType, string lengthType, SyntaxNode? syntax)
	{
		return new InitializerExpression
		{
			SourceSyntax = syntax,
			ResolvedType = arrayType,
			Items =
			{
				new InitializerItem
				{
					SourceSyntax = syntax,
					Expression = elements,
					ResolvedType = elementsType
				},
				new InitializerItem
				{
					SourceSyntax = syntax,
					Expression = length,
					ResolvedType = lengthType
				}
			}
		};
	}

	Expression BufferIndex(Expression buffer, Expression index, string elementType, SyntaxNode? syntax)
	{
		IndexExpression expression = new()
		{
			SourceSyntax = syntax,
			Target = buffer,
			ResolvedType = elementType
		};
		expression.Arguments.Add(new ArgumentExpression
		{
			SourceSyntax = syntax,
			Value = index,
			ResolvedType = index.ResolvedType
		});
		return expression;
	}

	Expression BufferLength(Expression buffer, string lengthType, SyntaxNode? syntax)
	{
		return new MemberExpression
		{
			SourceSyntax = syntax,
			Target = buffer,
			Name = "length",
			ResolvedType = lengthType
		};
	}

	Expression LengthLiteral(int value, string lengthType)
	{
		return NumberLiteral(value.ToString(CultureInfo.InvariantCulture), lengthType);
	}

	Expression MinLength(Expression left, Expression right, string type, SyntaxNode? syntax)
	{
		return new ConditionalExpression
		{
			SourceSyntax = syntax,
			Condition = new BinaryExpression
			{
				SourceSyntax = syntax,
				Left = left,
				Operator = BinaryOperator.LessThan,
				Right = right,
				ResolvedType = "bool"
			},
			WhenTrue = CloneParamsExpansionExpression(left) ?? left,
			WhenFalse = CloneParamsExpansionExpression(right) ?? right,
			ResolvedType = type
		};
	}

	Expression Add(Expression left, Expression right, string type)
	{
		return new BinaryExpression
		{
			SourceSyntax = left.SourceSyntax ?? right.SourceSyntax,
			Left = left,
			Operator = BinaryOperator.Add,
			Right = right,
			ResolvedType = type
		};
	}

	Expression Subtract(Expression left, Expression right, string type)
	{
		return new BinaryExpression
		{
			SourceSyntax = left.SourceSyntax ?? right.SourceSyntax,
			Left = left,
			Operator = BinaryOperator.Subtract,
			Right = right,
			ResolvedType = type
		};
	}

	Expression GreaterThan(Expression left, Expression right, SyntaxNode? syntax)
	{
		return new BinaryExpression
		{
			SourceSyntax = syntax,
			Left = left,
			Operator = BinaryOperator.GreaterThan,
			Right = right,
			ResolvedType = "bool"
		};
	}

	Expression GreaterThanOrEqual(Expression left, Expression right, SyntaxNode? syntax)
	{
		return new BinaryExpression
		{
			SourceSyntax = syntax,
			Left = left,
			Operator = BinaryOperator.GreaterThanOrEqual,
			Right = right,
			ResolvedType = "bool"
		};
	}

	Statement Assign(Expression target, Expression value, string type, SyntaxNode? syntax)
	{
		return new ExpressionStatement
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				SourceSyntax = syntax,
				Target = target,
				Operator = AssignmentOperator.Assign,
				Value = value,
				ResolvedType = type
			}
		};
	}

	static LiteralExpression CharacterLiteral(char value, string type, SyntaxNode? syntax)
	{
		return new LiteralExpression
		{
			SourceSyntax = syntax,
			Kind = LiteralKind.Character,
			Text = FormatCharacterLiteralText(value),
			Value = value.ToString(),
			ResolvedType = type
		};
	}

	static string FormatCharacterLiteralText(char value)
	{
		return value switch
		{
			'\0' => "'\\0'",
			'\n' => "'\\n'",
			'\r' => "'\\r'",
			'\t' => "'\\t'",
			'\\' => "'\\\\'",
			'\'' => "'\\''",
			_ => "'" + value + "'"
		};
	}
}
