using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	Expression LowerInterpolatedStringExpression(InterpolatedStringExpression interpolation)
	{
		if (!TryGetFormatterShape(interpolation.FormatterType, out FormatterShape formatter))
		{
			Report(GetRange(interpolation.SourceSyntax), "Interpolated string lowering requires a formatter target.");
			return interpolation;
		}

		List<Expression> formatterReferences = [];
		foreach (InterpolatedStringSegment segment in interpolation.Segments)
		{
			if (segment is not InterpolatedStringExpressionSegment { Formatter: not null } hole)
				continue;
			if (!TryCaptureInterpolatedComponentFormatter(hole.Formatter, formatter.Type, interpolation.SourceSyntax, out Expression? reference) || reference is null)
				return interpolation;
			formatterReferences.Add(reference);
		}

		LambdaParameter bufferParameter = new()
		{
			SourceSyntax = interpolation.SourceSyntax,
			Name = "buffer",
			ResolvedType = formatter.BufferType
		};
		LambdaExpression lambda = new()
		{
			SourceSyntax = interpolation.SourceSyntax,
			ResolvedType = formatter.Type,
			Body = BuildInterpolatedFormatterBody(interpolation, formatter, formatterReferences, bufferParameter)
		};
		lambda.Parameters.Add(bufferParameter);
		return LowerLambdaExpression(lambda);
	}

	bool TryCaptureInterpolatedComponentFormatter(Expression formatterExpression, string formatterType, SyntaxNode? syntax, out Expression? reference)
	{
		reference = null;
		if (currentStatementPrefix is null)
		{
			Report(GetRange(syntax), "Interpolated string formatter context cannot be created in this expression position yet.");
			return false;
		}

		Expression lowered = LowerExpression(CloneParamsExpansionExpression(formatterExpression) ?? formatterExpression) ?? formatterExpression;
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

	BlockStatement BuildInterpolatedFormatterBody(InterpolatedStringExpression interpolation, FormatterShape formatter, List<Expression> formatterReferences, LambdaParameter bufferParameter)
	{
		List<Statement> statements = [];
		DeclarationStatement required = CreateGeneratedLocal(NewGeneratedLocalName("interpolatedRequired"), formatter.LengthType, TypeReferenceForResolvedName(formatter.LengthType), NumberLiteral("1", formatter.LengthType));
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
					statements.Add(new IfStatement
					{
						SourceSyntax = segment.SourceSyntax,
						ResolvedType = "void",
						Condition = GreaterThan(CreateVariableReference(size.Target, formatter.LengthType), NumberLiteral("0", formatter.LengthType), segment.SourceSyntax),
						Body = CreateBlock([
							Assign(
								Required(),
								Add(Required(), Subtract(CreateVariableReference(size.Target, formatter.LengthType), NumberLiteral("1", formatter.LengthType), formatter.LengthType), formatter.LengthType),
								formatter.LengthType,
								segment.SourceSyntax)
						])
					});
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
					writeBody.Statements.Add(new ExpressionStatement
					{
						SourceSyntax = segment.SourceSyntax,
						ResolvedType = "void",
						Expression = CallFormatter(formatterReference, BufferSlice(Buffer(), Offset(), formatter), formatter.LengthType)
					});
					writeBody.Statements.Add(new IfStatement
					{
						SourceSyntax = segment.SourceSyntax,
						ResolvedType = "void",
						Condition = GreaterThan(CreateVariableReference(size.Target, formatter.LengthType), NumberLiteral("0", formatter.LengthType), segment.SourceSyntax),
						Body = CreateBlock([
							Assign(
								Offset(),
								Add(Offset(), Subtract(CreateVariableReference(size.Target, formatter.LengthType), NumberLiteral("1", formatter.LengthType), formatter.LengthType), formatter.LengthType),
								formatter.LengthType,
								segment.SourceSyntax)
						])
					});
					break;
			}
		}
		writeBody.Statements.Add(Assign(BufferIndex(Buffer(), Offset(), formatter.ElementType, interpolation.SourceSyntax), CharacterLiteral('\0', formatter.ElementType, interpolation.SourceSyntax), formatter.ElementType, interpolation.SourceSyntax));

		statements.Add(new IfStatement
		{
			SourceSyntax = interpolation.SourceSyntax,
			ResolvedType = "void",
			Condition = GreaterThanOrEqual(BufferLength(Buffer(), formatter.LengthType, interpolation.SourceSyntax), Required(), interpolation.SourceSyntax),
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
		foreach (char ch in text)
		{
			statements.Add(Assign(BufferIndex(buffer(), offset(), formatter.ElementType, syntax), CharacterLiteral(ch, formatter.ElementType, syntax), formatter.ElementType, syntax));
			statements.Add(Assign(offset(), Add(offset(), NumberLiteral("1", formatter.LengthType), formatter.LengthType), formatter.LengthType, syntax));
		}
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
