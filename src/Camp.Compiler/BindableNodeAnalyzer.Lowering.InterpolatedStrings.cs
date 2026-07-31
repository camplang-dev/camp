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

		List<Expression> formatterReferences = [];
		List<FormatterShape> formatterShapes = [];
		foreach (InterpolatedStringSegment segment in interpolation.Segments)
		{
			if (segment is not InterpolatedStringExpressionSegment { Formatter: not null } hole)
				continue;
			if (!TryGetFormatterShape(hole.Formatter.ResolvedType, out FormatterShape formatter))
			{
				Report(GetRange(hole.Formatter.SourceSyntax ?? hole.SourceSyntax), "Interpolated string hole formatter has an invalid formatter shape.");
				return interpolation;
			}
			if (!TryCaptureInterpolatedComponentFormatter(hole.Formatter, formatter.Type, interpolation.SourceSyntax, out Expression? reference) || reference is null)
				return interpolation;
			formatterReferences.Add(reference);
			formatterShapes.Add(formatter);
		}

		return LowerEagerInterpolatedStringExpression(interpolation, resultType, formatterReferences, formatterShapes);
	}

	Expression LowerEagerInterpolatedStringExpression(InterpolatedStringExpression interpolation, string resultType, List<Expression> formatterReferences, List<FormatterShape> formatterShapes)
	{
		const string elementType = "char";
		const string lengthType = "nuint";
		DeclarationStatement required = CreateGeneratedLocal(NewGeneratedLocalName("interpolatedRequired"), lengthType, TypeReferenceForResolvedName(lengthType), NumberLiteral("0", lengthType));
		currentStatementPrefix!.Add(required);
		Expression Required() => CreateVariableReference(required.Target, lengthType);

		List<DeclarationStatement> componentSizes = [];
		int formatterIndex = 0;
		foreach (InterpolatedStringSegment segment in interpolation.Segments)
		{
			switch (segment)
			{
				case InterpolatedStringTextSegment text:
					if (text.Text.Length > 0)
						currentStatementPrefix.Add(Assign(Required(), Add(Required(), LengthLiteral(text.Text.Length, lengthType), lengthType), lengthType, text.SourceSyntax));
					break;

				case InterpolatedStringExpressionSegment { Formatter: null } hole:
					if (TryGetConstantInterpolationText(hole, out string constantText) && constantText.Length > 0)
						currentStatementPrefix.Add(Assign(Required(), Add(Required(), LengthLiteral(constantText.Length, lengthType), lengthType), lengthType, hole.SourceSyntax));
					break;

				case InterpolatedStringExpressionSegment { Formatter: not null }:
					Expression formatterReference = formatterReferences[formatterIndex];
					FormatterShape formatter = formatterShapes[formatterIndex];
					formatterIndex++;
					DeclarationStatement size = CreateGeneratedLocal(
						NewGeneratedLocalName("interpolatedPartSize"),
						formatter.LengthType,
						TypeReferenceForResolvedName(formatter.LengthType),
						CallFormatter(formatterReference, DefaultBuffer(formatter), formatter.LengthType));
					currentStatementPrefix.Add(size);
					componentSizes.Add(size);
					currentStatementPrefix.Add(Assign(
						Required(),
						Add(Required(), CreateVariableReference(size.Target, formatter.LengthType), lengthType),
						lengthType,
						segment.SourceSyntax));
					break;
			}
		}

		Expression allocationLength = interpolation.NullTerminated
			? Add(Required(), NumberLiteral("1", lengthType), lengthType)
			: Required();
		Expression bufferConstruction = CreateArrayConstruction(TypeReferenceForResolvedName(elementType), allocationLength, interpolation.HeapAllocated ? ConstructionKind.New : ConstructionKind.Init, interpolation.SourceSyntax, "char[]");
		DeclarationStatement buffer = CreateGeneratedLocal(NewGeneratedLocalName("interpolatedBuffer"), "char[]", TypeReferenceForResolvedName("char[]"), bufferConstruction);
		currentStatementPrefix.Add(buffer);
		Expression Buffer() => CreateVariableReference(buffer.Target, "char[]");

		DeclarationStatement offset = CreateGeneratedLocal(NewGeneratedLocalName("interpolatedOffset"), lengthType, TypeReferenceForResolvedName(lengthType), NumberLiteral("0", lengthType));
		currentStatementPrefix.Add(offset);
		Expression Offset() => CreateVariableReference(offset.Target, lengthType);

		formatterIndex = 0;
		int sizeIndex = 0;
		foreach (InterpolatedStringSegment segment in interpolation.Segments)
		{
			switch (segment)
			{
				case InterpolatedStringTextSegment text:
					AddLiteralWrites(currentStatementPrefix, Buffer, Offset, new FormatterShape("", "char[]", elementType, lengthType), text.Text, text.SourceSyntax);
					break;

				case InterpolatedStringExpressionSegment { Formatter: null } hole:
					if (TryGetConstantInterpolationText(hole, out string constantText))
						AddLiteralWrites(currentStatementPrefix, Buffer, Offset, new FormatterShape("", "char[]", elementType, lengthType), constantText, hole.SourceSyntax);
					break;

				case InterpolatedStringExpressionSegment { Formatter: not null }:
					Expression formatterReference = formatterReferences[formatterIndex];
					FormatterShape formatter = formatterShapes[formatterIndex];
					formatterIndex++;
					DeclarationStatement size = componentSizes[sizeIndex++];
					DeclarationStatement start = AddClampedOffsetLocal(currentStatementPrefix, Buffer, Offset, formatter, segment.SourceSyntax);
					currentStatementPrefix.Add(new ExpressionStatement
					{
						SourceSyntax = segment.SourceSyntax,
						ResolvedType = "void",
						Expression = CallFormatter(formatterReference, BufferSlice(Buffer(), CreateVariableReference(start.Target, formatter.LengthType), formatter), formatter.LengthType)
					});
					currentStatementPrefix.Add(Assign(
						Offset(),
						Add(Offset(), CreateVariableReference(size.Target, formatter.LengthType), lengthType),
						lengthType,
						segment.SourceSyntax));
					break;
			}
		}

		if (interpolation.NullTerminated)
			currentStatementPrefix.Add(Assign(BufferIndex(Buffer(), Required(), elementType, interpolation.SourceSyntax), CharacterLiteral('\0', elementType, interpolation.SourceSyntax), elementType, interpolation.SourceSyntax));

		if (IsPrimitiveStringType(resultType))
		{
			return new CastExpression
			{
				SourceSyntax = interpolation.SourceSyntax,
				Kind = CastKind.Type,
				Type = TypeReferenceForResolvedName(resultType),
				Expression = CreateArrayElementsAccess(Buffer()),
				ResolvedType = resultType
			};
		}

		if (resultType == "char[]")
			return Buffer();

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
						Expression = CreateArrayElementsAccess(Buffer()),
						ResolvedType = resultElement + "*"
					},
					new InitializerItem
					{
						SourceSyntax = interpolation.SourceSyntax,
						Expression = BufferLength(Buffer(), lengthType, interpolation.SourceSyntax),
						ResolvedType = lengthType
					}
				}
			};

		Report(GetRange(interpolation.SourceSyntax), $"Interpolated string target '{resultType}' cannot be lowered yet.");
		return interpolation;
	}

	bool TryCaptureInterpolatedComponentFormatter(Expression formatterExpression, string formatterType, SyntaxNode? syntax, out Expression? reference)
	{
		reference = null;
		if (currentStatementPrefix is null)
		{
			Report(GetRange(syntax), "Interpolated string formatter context cannot be created in this expression position yet.");
			return false;
		}

		Expression formatterSource = CloneParamsExpansionExpression(formatterExpression) ?? formatterExpression;
		formatterSource = CaptureInterpolatedFormatterTarget(formatterSource);
		Expression lowered = LowerExpression(formatterSource) ?? formatterSource;
		CastExpression protocolExpression = new()
		{
			SourceSyntax = formatterExpression.SourceSyntax,
			Kind = CastKind.Type,
			Type = TypeReferenceForResolvedName(formatterType),
			Expression = lowered,
			ResolvedType = formatterType
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
			ResolvedType = formatterType
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
		reference = grouped;
		return true;
	}

	Expression CaptureInterpolatedFormatterTarget(Expression formatterExpression)
	{
		if (currentStatementPrefix is null
			|| formatterExpression is not MemberReferenceExpression { Target: Expression target, Member: FunctionDefinition function } member
			|| !IsInstanceInvocationFunction(function)
			|| target is LiteralExpression
			|| IsConstant(target)
			|| CanTakeReceiverAddress(target))
		{
			return formatterExpression;
		}

		string targetType = target.ResolvedType ?? ErrorType;
		Expression loweredTarget = LowerExpression(CloneParamsExpansionExpression(target) ?? target) ?? target;
		DeclarationStatement local = CreateGeneratedLocal(
			NewGeneratedLocalName("interpolatedValue"),
			targetType,
			TypeReferenceForResolvedName(targetType),
			loweredTarget);
		currentStatementPrefix.Add(local);

		MemberReferenceExpression captured = new()
		{
			SourceSyntax = member.SourceSyntax,
			Target = CreateVariableReference(local.Target, targetType, target.SourceSyntax),
			Name = member.Name,
			NameRange = member.NameRange,
			Member = member.Member,
			ResolvedType = member.ResolvedType
		};
		captured.Candidates.AddRange(member.Candidates);
		return captured;
	}

	BlockStatement BuildInterpolatedFormatterBody(InterpolatedStringExpression interpolation, FormatterShape formatter, List<Expression> formatterReferences, LambdaParameter bufferParameter)
	{
		List<Statement> statements = [];
		DeclarationStatement required = CreateGeneratedLocal(NewGeneratedLocalName("interpolatedRequired"), formatter.LengthType, TypeReferenceForResolvedName(formatter.LengthType), NumberLiteral("0", formatter.LengthType));
		statements.Add(required);
		Expression Required() => CreateVariableReference(required.Target, formatter.LengthType);

		List<DeclarationStatement> componentSizes = [];
		int formatterIndex = 0;
		foreach (InterpolatedStringSegment segment in interpolation.Segments)
		{
			switch (segment)
			{
				case InterpolatedStringTextSegment text:
					if (text.Text.Length > 0)
						statements.Add(Assign(Required(), Add(Required(), LengthLiteral(text.Text.Length, formatter.LengthType), formatter.LengthType), formatter.LengthType, text.SourceSyntax));
					break;

				case InterpolatedStringExpressionSegment { Formatter: null } hole:
					if (TryGetConstantInterpolationText(hole, out string constantText) && constantText.Length > 0)
						statements.Add(Assign(Required(), Add(Required(), LengthLiteral(constantText.Length, formatter.LengthType), formatter.LengthType), formatter.LengthType, hole.SourceSyntax));
					break;

				case InterpolatedStringExpressionSegment { Formatter: not null }:
					Expression formatterReference = formatterReferences[formatterIndex++];
					DeclarationStatement size = CreateGeneratedLocal(
						NewGeneratedLocalName("interpolatedPartSize"),
						formatter.LengthType,
						TypeReferenceForResolvedName(formatter.LengthType),
						CallFormatter(formatterReference, DefaultBuffer(formatter), formatter.LengthType));
					statements.Add(size);
					componentSizes.Add(size);
					statements.Add(Assign(
						Required(),
						Add(Required(), CreateVariableReference(size.Target, formatter.LengthType), formatter.LengthType),
						formatter.LengthType,
						segment.SourceSyntax));
					break;
			}
		}

		DeclarationStatement offset = CreateGeneratedLocal(NewGeneratedLocalName("interpolatedOffset"), formatter.LengthType, TypeReferenceForResolvedName(formatter.LengthType), NumberLiteral("0", formatter.LengthType));
		BlockStatement writeBody = CreateBlock([]);
		writeBody.Statements.Add(offset);
		Expression Offset() => CreateVariableReference(offset.Target, formatter.LengthType);
		Expression Buffer() => CreateVariableReference(bufferParameter, formatter.BufferType);

		formatterIndex = 0;
		int sizeIndex = 0;
		foreach (InterpolatedStringSegment segment in interpolation.Segments)
		{
			switch (segment)
			{
				case InterpolatedStringTextSegment text:
					AddLiteralWrites(writeBody.Statements, Buffer, Offset, formatter, text.Text, text.SourceSyntax);
					break;

				case InterpolatedStringExpressionSegment { Formatter: null } hole:
					if (TryGetConstantInterpolationText(hole, out string constantText))
						AddLiteralWrites(writeBody.Statements, Buffer, Offset, formatter, constantText, hole.SourceSyntax);
					break;

				case InterpolatedStringExpressionSegment { Formatter: not null }:
					Expression formatterReference = formatterReferences[formatterIndex++];
					DeclarationStatement size = componentSizes[sizeIndex++];
					DeclarationStatement start = AddClampedOffsetLocal(writeBody.Statements, Buffer, Offset, formatter, segment.SourceSyntax);
					writeBody.Statements.Add(new ExpressionStatement
					{
						SourceSyntax = segment.SourceSyntax,
						ResolvedType = "void",
						Expression = CallFormatter(formatterReference, BufferSlice(Buffer(), CreateVariableReference(start.Target, formatter.LengthType), formatter), formatter.LengthType)
					});
					writeBody.Statements.Add(Assign(
						Offset(),
						Add(Offset(), CreateVariableReference(size.Target, formatter.LengthType), formatter.LengthType),
						formatter.LengthType,
						segment.SourceSyntax));
					break;
			}
		}
		statements.Add(new IfStatement
		{
			SourceSyntax = interpolation.SourceSyntax,
			ResolvedType = "void",
			Condition = GreaterThan(BufferLength(Buffer(), formatter.LengthType, interpolation.SourceSyntax), NumberLiteral("0", formatter.LengthType), interpolation.SourceSyntax),
			Body = writeBody
		});
		statements.Add(new ReturnStatement
		{
			SourceSyntax = interpolation.SourceSyntax,
			ResolvedType = "void",
			Expression = Required()
		});

		BlockStatement body = new()
		{
			SourceSyntax = interpolation.SourceSyntax,
			ResolvedType = "void"
		};
		body.Statements.AddRange(statements);
		return body;
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

	void AddLiteralWrites(List<Statement> statements, Func<Expression> buffer, Func<Expression> offset, FormatterShape formatter, string text, SyntaxNode? syntax)
	{
		if (text.Length == 0)
			return;

		Expression bufferLength = BufferLength(buffer(), formatter.LengthType, syntax);
		DeclarationStatement start = CreateGeneratedLocal(
			NewGeneratedLocalName("interpolatedCopyStart"),
			formatter.LengthType,
			TypeReferenceForResolvedName(formatter.LengthType),
			MinLength(offset(), bufferLength, formatter.LengthType, syntax));
		statements.Add(start);

		Expression remaining = Subtract(BufferLength(buffer(), formatter.LengthType, syntax), CreateVariableReference(start.Target, formatter.LengthType), formatter.LengthType);
		DeclarationStatement count = CreateGeneratedLocal(
			NewGeneratedLocalName("interpolatedCopyCount"),
			formatter.LengthType,
			TypeReferenceForResolvedName(formatter.LengthType),
			MinLength(LengthLiteral(text.Length, formatter.LengthType), remaining, formatter.LengthType, syntax));
		statements.Add(count);

		BlockStatement copyBody = CreateBlock([
			new LiteralCopyStatement
			{
				SourceSyntax = syntax,
				ResolvedType = "void",
				Buffer = buffer(),
				Offset = CreateVariableReference(start.Target, formatter.LengthType),
				Count = CreateVariableReference(count.Target, formatter.LengthType),
				ElementType = formatter.ElementType,
				LengthType = formatter.LengthType,
				Text = text
			}
		]);
		statements.Add(new IfStatement
		{
			SourceSyntax = syntax,
			ResolvedType = "void",
			Condition = GreaterThan(CreateVariableReference(count.Target, formatter.LengthType), NumberLiteral("0", formatter.LengthType), syntax),
			Body = copyBody
		});
		statements.Add(Assign(offset(), Add(offset(), LengthLiteral(text.Length, formatter.LengthType), formatter.LengthType), formatter.LengthType, syntax));
	}

	DeclarationStatement AddClampedOffsetLocal(List<Statement> statements, Func<Expression> buffer, Func<Expression> offset, FormatterShape formatter, SyntaxNode? syntax)
	{
		DeclarationStatement start = CreateGeneratedLocal(
			NewGeneratedLocalName("interpolatedStart"),
			formatter.LengthType,
			TypeReferenceForResolvedName(formatter.LengthType),
			MinLength(offset(), BufferLength(buffer(), formatter.LengthType, syntax), formatter.LengthType, syntax));
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

	InitializerExpression BufferSlice(Expression buffer, Expression offset, FormatterShape formatter)
	{
		return new InitializerExpression
		{
			SourceSyntax = buffer.SourceSyntax,
			ResolvedType = formatter.BufferType,
			Items =
			{
				new InitializerItem
				{
					SourceSyntax = buffer.SourceSyntax,
					Expression = new UnaryExpression
					{
						SourceSyntax = buffer.SourceSyntax,
						Operator = UnaryOperator.AddressOf,
						Operand = BufferIndex(buffer, offset, formatter.ElementType, buffer.SourceSyntax),
						ResolvedType = formatter.ElementType + "*"
					},
					ResolvedType = formatter.ElementType + "*"
				},
				new InitializerItem
				{
					SourceSyntax = buffer.SourceSyntax,
					Expression = Subtract(BufferLength(CloneParamsExpansionExpression(buffer) ?? buffer, formatter.LengthType, buffer.SourceSyntax), CloneParamsExpansionExpression(offset) ?? offset, formatter.LengthType),
					ResolvedType = formatter.LengthType
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
