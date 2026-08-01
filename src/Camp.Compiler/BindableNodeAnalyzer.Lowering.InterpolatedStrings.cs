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
						if (!TryGetFormatterShape(hole.Formatter.ResolvedType, out FormatterShape formatter))
						{
							Report(GetRange(hole.Formatter.SourceSyntax ?? hole.SourceSyntax), "Interpolated string hole formatter has an invalid formatter shape.");
							return false;
						}
						part = new RuntimeFormatterValuePart(hole.Formatter, formatter, hole.SourceSyntax);
					}
					if (part is DirectFormatterPart directPart && TryCreateDirectTextPart(directPart, out DirectTextPart? directTextPart) && directTextPart is not null)
					{
						parts.Add(directTextPart);
						break;
					}
					if (part is RuntimeFormatterValuePart runtime)
					{
						if (!TryCaptureRuntimeFormatterValue(runtime, interpolation.SourceSyntax, out RuntimeFormatterValuePart? captured) || captured is null)
							return false;
						parts.Add(captured);
					}
					else
					{
						parts.Add(part);
					}
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
		parts = MaterializeDirectFormatterValues(parts);
		parts = MaterializeDirectTextValues(parts);
		long constantLength = 0;
		foreach (InterpolationPart part in parts)
			if (part is LiteralTextPart text)
				constantLength += text.Text.Length;

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

				case DirectFormatterPart direct:
					Expression sizeCall = LowerExpression(CreateDirectFormatterCall(direct, DefaultBuffer(direct.Shape), direct.Shape.LengthType)) ?? direct.Value;
					DeclarationStatement size = CreateGeneratedLocal(
						NewGeneratedLocalName("interpolatedPartSize"),
						direct.Shape.LengthType,
						TypeReferenceForResolvedName(direct.Shape.LengthType),
						sizeCall);
					currentStatementPrefix.Add(size);
					componentSizes.Add(size);
					currentStatementPrefix.Add(Assign(
						Required(),
						Add(Required(), CreateVariableReference(size.Target, direct.Shape.LengthType), lengthType),
						lengthType,
						part.SourceSyntax));
					break;

				case RuntimeFormatterValuePart runtime:
					Expression runtimeSizeCall = CallFormatter(runtime.FormatterValue, DefaultBuffer(runtime.Shape), runtime.Shape.LengthType);
					runtimeSizeCall = LowerExpression(runtimeSizeCall) ?? runtimeSizeCall;
					DeclarationStatement runtimeSize = CreateGeneratedLocal(
						NewGeneratedLocalName("interpolatedPartSize"),
						runtime.Shape.LengthType,
						TypeReferenceForResolvedName(runtime.Shape.LengthType),
						runtimeSizeCall);
					currentStatementPrefix.Add(runtimeSize);
					componentSizes.Add(runtimeSize);
					currentStatementPrefix.Add(Assign(
						Required(),
						Add(Required(), CreateVariableReference(runtimeSize.Target, runtime.Shape.LengthType), lengthType),
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

				case DirectFormatterPart direct:
					DeclarationStatement size = componentSizes[sizeIndex++];
					DeclarationStatement start = AddClampedOffsetLocal(currentStatementPrefix, BufferLengthValue, Offset, direct.Shape.LengthType, part.SourceSyntax);
					Expression writeCall = CreateDirectFormatterCall(direct, BufferSlice(BufferElements(), BufferLengthValue(), CreateVariableReference(start.Target, direct.Shape.LengthType), direct.Shape, part.SourceSyntax), direct.Shape.LengthType);
					writeCall = LowerExpression(writeCall) ?? writeCall;
					currentStatementPrefix.Add(new ExpressionStatement
					{
						SourceSyntax = part.SourceSyntax,
						ResolvedType = "void",
						Expression = writeCall
					});
					currentStatementPrefix.Add(Assign(
						Offset(),
						Add(Offset(), CreateVariableReference(size.Target, direct.Shape.LengthType), lengthType),
						lengthType,
						part.SourceSyntax));
					break;

				case RuntimeFormatterValuePart runtime:
					DeclarationStatement runtimeSize = componentSizes[sizeIndex++];
					DeclarationStatement runtimeStart = AddClampedOffsetLocal(currentStatementPrefix, BufferLengthValue, Offset, runtime.Shape.LengthType, part.SourceSyntax);
					Expression runtimeWriteCall = CallFormatter(runtime.FormatterValue, BufferSlice(BufferElements(), BufferLengthValue(), CreateVariableReference(runtimeStart.Target, runtime.Shape.LengthType), runtime.Shape, part.SourceSyntax), runtime.Shape.LengthType);
					runtimeWriteCall = LowerExpression(runtimeWriteCall) ?? runtimeWriteCall;
					currentStatementPrefix.Add(new ExpressionStatement
					{
						SourceSyntax = part.SourceSyntax,
						ResolvedType = "void",
						Expression = runtimeWriteCall
					});
					currentStatementPrefix.Add(Assign(
						Offset(),
						Add(Offset(), CreateVariableReference(runtimeSize.Target, runtime.Shape.LengthType), lengthType),
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
			return new CastExpression
			{
				SourceSyntax = interpolation.SourceSyntax,
				Kind = CastKind.Type,
				Type = TypeReferenceForResolvedName(resultType),
				Expression = BufferElements(),
				ResolvedType = resultType
			};
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
		parts = MaterializeDirectFormatterValues(parts);
		parts = MaterializeDirectTextValues(parts);
		List<DeclarationStatement> componentSizes = [];
		foreach (InterpolationPart part in parts)
		{
			if (part is LiteralTextPart)
				continue;

			FormatterShape formatter = part switch
			{
				DirectFormatterPart direct => direct.Shape,
				RuntimeFormatterValuePart runtime => runtime.Shape,
				PreparedFormatterPart prepared => prepared.Shape,
				DirectTextPart => new FormatterShape("CharFormatter", "char[]", "char", lengthType),
				_ => throw new InvalidOperationException("Unknown interpolation part.")
			};
			Expression sizeCall = part switch
			{
				DirectFormatterPart direct => CreateDirectFormatterCall(direct, DefaultBuffer(formatter), formatter.LengthType),
				RuntimeFormatterValuePart runtime => CallFormatter(runtime.FormatterValue, DefaultBuffer(formatter), formatter.LengthType),
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

				case DirectFormatterPart direct:
					DeclarationStatement size = componentSizes[sizeIndex++];
					DeclarationStatement start = AddClampedOffsetLocal(currentStatementPrefix, () => CloneParamsExpansionExpression(bufferLength) ?? bufferLength, Offset, direct.Shape.LengthType, part.SourceSyntax);
					Expression writeCall = CreateDirectFormatterCall(direct, BufferSlice(CloneParamsExpansionExpression(bufferElements) ?? bufferElements, CloneParamsExpansionExpression(bufferLength) ?? bufferLength, CreateVariableReference(start.Target, direct.Shape.LengthType), direct.Shape, part.SourceSyntax), direct.Shape.LengthType);
					writeCall = LowerExpression(writeCall) ?? writeCall;
					currentStatementPrefix.Add(new ExpressionStatement
					{
						SourceSyntax = part.SourceSyntax,
						ResolvedType = "void",
						Expression = writeCall
					});
					currentStatementPrefix.Add(Assign(
						Offset(),
						Add(Offset(), CreateVariableReference(size.Target, direct.Shape.LengthType), lengthType),
						lengthType,
						part.SourceSyntax));
					break;

				case RuntimeFormatterValuePart runtime:
					DeclarationStatement runtimeSize = componentSizes[sizeIndex++];
					DeclarationStatement runtimeStart = AddClampedOffsetLocal(currentStatementPrefix, () => CloneParamsExpansionExpression(bufferLength) ?? bufferLength, Offset, runtime.Shape.LengthType, part.SourceSyntax);
					Expression runtimeWriteCall = CallFormatter(runtime.FormatterValue, BufferSlice(CloneParamsExpansionExpression(bufferElements) ?? bufferElements, CloneParamsExpansionExpression(bufferLength) ?? bufferLength, CreateVariableReference(runtimeStart.Target, runtime.Shape.LengthType), runtime.Shape, part.SourceSyntax), runtime.Shape.LengthType);
					runtimeWriteCall = LowerExpression(runtimeWriteCall) ?? runtimeWriteCall;
					currentStatementPrefix.Add(new ExpressionStatement
					{
						SourceSyntax = part.SourceSyntax,
						ResolvedType = "void",
						Expression = runtimeWriteCall
					});
					currentStatementPrefix.Add(Assign(
						Offset(),
						Add(Offset(), CreateVariableReference(runtimeSize.Target, runtime.Shape.LengthType), lengthType),
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

	bool TryCreateDirectTextPart(DirectFormatterPart part, out DirectTextPart? textPart)
	{
		textPart = null;
		EnsureFlattenedFunctionSymbol(part.Function);
		string symbol = part.Function.Symbol;
		if (symbol == "String_formatCharArray" && IsPrimitiveStringType(part.Value.ResolvedType))
		{
			textPart = new DirectTextPart(part.Value, part.Value.ResolvedType ?? "string", DirectTextKind.String, part, null, part.SourceSyntax);
			return true;
		}
		if (symbol == "CharArray_formatCharArray")
		{
			textPart = new DirectTextPart(part.Value, part.Value.ResolvedType ?? "char[]", DirectTextKind.CharArray, part, null, part.SourceSyntax);
			return true;
		}
		if (symbol == "Char_formatCharArray" && StripTopLevelValueQualifiers(part.Value.ResolvedType ?? "") == "char")
		{
			textPart = new DirectTextPart(part.Value, "char", DirectTextKind.Char, part, null, part.SourceSyntax);
			return true;
		}
		return false;
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

	bool TryCaptureRuntimeFormatterValue(RuntimeFormatterValuePart part, SyntaxNode? syntax, out RuntimeFormatterValuePart? captured)
	{
		captured = null;
		if (currentStatementPrefix is null)
		{
			Report(GetRange(syntax), "Interpolated string formatter context cannot be created in this expression position yet.");
			return false;
		}

		Expression formatterExpression = part.FormatterValue;
		Expression formatterSource = CloneParamsExpansionExpression(formatterExpression) ?? formatterExpression;
		Expression lowered = LowerExpression(formatterSource) ?? formatterSource;
		CastExpression protocolExpression = new()
		{
			SourceSyntax = formatterExpression.SourceSyntax,
			Kind = CastKind.Type,
			Type = TypeReferenceForResolvedName(part.Shape.Type),
			Expression = lowered,
			ResolvedType = part.Shape.Type
		};
		if (!TryCreateParamsComponentExpressions(protocolExpression, out List<Expression> components) || components.Count != 2)
		{
			Report(GetRange(formatterExpression.SourceSyntax ?? syntax), "Formatter expression could not be lowered to delegate call and context components.");
			return false;
		}

		DeclarationStatement call = CreateGeneratedLocal(
			NewGeneratedLocalName("interpolatedCall"),
			components[0].ResolvedType ?? ErrorType,
			TypeReferenceForResolvedName(components[0].ResolvedType ?? ErrorType),
			components[0]);
		currentStatementPrefix.Add(call);

		DeclarationStatement context = CreateGeneratedLocal(
			NewGeneratedLocalName("interpolatedContext"),
			components[1].ResolvedType ?? "void*",
			TypeReferenceForResolvedName(components[1].ResolvedType ?? "void*"),
			components[1]);
		currentStatementPrefix.Add(context);

		GroupedExpression grouped = new()
		{
			SourceSyntax = formatterExpression.SourceSyntax ?? syntax,
			ResolvedType = part.Shape.Type
		};
		grouped.Items.Add(new GroupedExpressionItem
		{
			SourceSyntax = formatterExpression.SourceSyntax ?? syntax,
			Expression = CreateVariableReference(call.Target, call.Target.ResolvedType ?? ErrorType),
			ResolvedType = call.Target.ResolvedType ?? ErrorType
		});
		grouped.Items.Add(new GroupedExpressionItem
		{
			SourceSyntax = formatterExpression.SourceSyntax ?? syntax,
			Expression = CreateVariableReference(context.Target, context.Target.ResolvedType ?? "void*"),
			ResolvedType = context.Target.ResolvedType ?? "void*"
		});
		captured = part with { FormatterValue = grouped };
		return true;
	}

	List<InterpolationPart> MaterializeDirectFormatterValues(List<InterpolationPart> parts)
	{
		List<InterpolationPart> result = [];
		foreach (InterpolationPart part in parts)
		{
			if (part is DirectFormatterPart direct)
				result.Add(direct with { Value = MaterializeDirectFormatterValue(direct) });
			else
				result.Add(part);
		}
		return result;
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
				InterpolationPart? sourcePart = directText.SourcePart is DirectFormatterPart formatter
					? formatter with { Value = value }
					: directText.SourcePart;
				result.Add(directText with { Value = value, SourcePart = sourcePart });
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
		InterpolationPart? sourcePart = part.SourcePart is DirectFormatterPart formatter
			? formatter with { Value = elements }
			: part.SourcePart;
		return part with
		{
			Value = elements,
			ValueType = elements.ResolvedType ?? part.ValueType,
			SourcePart = sourcePart,
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

	Expression MaterializeDirectFormatterValue(DirectFormatterPart part)
	{
		string originalType = part.Value.ResolvedType ?? ErrorType;
		if (TryGetParamsComponentShape(null, originalType, "value", out ParamsComponentShape originalShape) && originalShape.Components.Count > 1)
			return part.Value;

		Expression value = CloneParamsExpansionExpression(part.Value) ?? part.Value;
		if (value is LiteralExpression || IsConstant(value))
			return value;

		string targetType = value.ResolvedType ?? ErrorType;
		if (TryGetParamsComponentShape(null, targetType, "value", out ParamsComponentShape shape) && shape.Components.Count > 1)
			return value;

		Expression loweredTarget = LowerExpression(value) ?? value;
		DeclarationStatement local = CreateGeneratedLocal(
			NewGeneratedLocalName("interpolatedValue"),
			targetType,
			TypeReferenceForResolvedName(targetType),
			loweredTarget);
		currentStatementPrefix!.Add(local);
		return CreateVariableReference(local.Target, targetType, value.SourceSyntax);
	}

	CallExpression CreateDirectFormatterCall(DirectFormatterPart part, Expression buffer, string lengthType)
	{
		Expression receiver = CloneParamsExpansionExpression(part.Value) ?? part.Value;
		MemberReferenceExpression target = new()
		{
			SourceSyntax = part.FormatterExpression.SourceSyntax ?? part.SourceSyntax,
			Target = receiver,
			Name = "format",
			Member = part.Function,
			ResolvedType = part.FormatterType
		};
		target.Candidates.Add(part.Function);
		CallExpression call = new()
		{
			SourceSyntax = part.SourceSyntax,
			Target = target,
			ResolvedType = lengthType
		};
		call.Arguments.Add(new ArgumentExpression
		{
			SourceSyntax = buffer.SourceSyntax,
			Value = buffer,
			ResolvedType = buffer.ResolvedType
		});
		callTargets[call] = part.Function;
		Dictionary<string, string> substitutions = [];
		AddReceiverTypeGenericSubstitutions(receiver.ResolvedType ?? ErrorType, part.Function, substitutions);
		if (substitutions.Count > 0)
			callGenericSubstitutions[call] = substitutions;
		return call;
	}

	CallExpression CreatePreparedFormatterCall(PreparedFormatterPart part, Expression buffer)
	{
		bool includeExplicitThis = IncludeExplicitThisArgument(part.Call.Target, part.Function);
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

		if (exactAppend)
		{
			statements.Add(new LiteralCopyStatement
			{
				SourceSyntax = syntax,
				ResolvedType = "void",
				Buffer = bufferElements(),
				Offset = offset(),
				Count = LengthLiteral(text.Length, lengthType),
				ElementType = elementType,
				LengthType = lengthType,
				Text = text,
				ExactAppend = true
			});
			statements.Add(Assign(offset(), Add(offset(), LengthLiteral(text.Length, lengthType), lengthType), lengthType, syntax));
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
			MinLength(LengthLiteral(text.Length, lengthType), remaining, lengthType, syntax));
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
		statements.Add(Assign(offset(), Add(offset(), LengthLiteral(text.Length, lengthType), lengthType), lengthType, syntax));
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

	CallExpression CallFormatter(Expression formatterReference, Expression buffer, string lengthType)
	{
		CallExpression call = new()
		{
			SourceSyntax = formatterReference.SourceSyntax,
			Target = CloneParamsExpansionExpression(formatterReference) ?? formatterReference,
			ResolvedType = lengthType
		};
		call.Arguments.Add(new ArgumentExpression
		{
			SourceSyntax = buffer.SourceSyntax,
			Value = buffer,
			ResolvedType = buffer.ResolvedType
		});
		return call;
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
