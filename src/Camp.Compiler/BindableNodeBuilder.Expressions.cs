using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Camp.Compiler;

public sealed partial class BindableNodeBuilder
{
	Expression? BuildExpression(ExpressionSyntax? syntax, string context)
	{
		if (syntax is null)
		{
			Report((TokenRange?)null, $"{context} is missing an expression.");
			return null;
		}

		switch (syntax)
		{
			case CommaExpressionSyntax comma:
				return BuildGroupedExpression(comma, context);

			case AssignmentExpressionSyntax assignment:
				return new AssignmentExpression
				{
					SourceSyntax = assignment,
					Target = BuildExpression(assignment.Left, $"{context} assignment target"),
					Operator = BuildAssignmentOperator(assignment.Operator),
					Value = BuildExpression(assignment.Right, $"{context} assignment value")
				};

			case ConditionalExpressionSyntax conditional:
				return new ConditionalExpression
				{
					SourceSyntax = conditional,
					Condition = BuildExpression(conditional.Condition, $"{context} condition"),
					WhenTrue = BuildExpression(conditional.WhenTrue, $"{context} true branch"),
					WhenFalse = BuildExpression(conditional.WhenFalse, $"{context} false branch")
				};

			case RangeExpressionSyntax range:
				return new RangeExpression
				{
					SourceSyntax = range,
					Start = range.Start is null ? null : BuildExpression(range.Start, $"{context} range start"),
					End = range.End is null ? null : BuildExpression(range.End, $"{context} range end")
				};

			case BinaryExpressionSyntax binary:
				return BuildBinaryExpression(binary, context);

			case UnaryExpressionSyntax unary:
				return BuildUnaryExpression(unary, context);

			case PostfixExpressionSyntax postfix:
				return BuildPostfixExpression(postfix, context);

			case LiteralExpressionSyntax literal:
				return BuildLiteralExpression(literal);

			case QualifiedNameExpressionSyntax name:
				return BuildNamedExpression(name, context);

			case ThisExpressionSyntax thisExpression:
				return new ThisExpression { SourceSyntax = thisExpression };

			case DefaultExpressionSyntax defaultExpression:
				return new DefaultExpression { SourceSyntax = defaultExpression };

			case ParenthesizedExpressionSyntax parenthesized:
				return new ParenthesizedExpression
				{
					SourceSyntax = parenthesized,
					Expression = BuildExpression(parenthesized.Expression, $"{context} parenthesized expression")
				};

			case GroupedExpressionSyntax grouped:
				return BuildGroupedExpression(grouped, context);

			case ArrayExpressionSyntax array:
				return BuildArrayExpression(array, context);

			case CastExpressionSyntax cast:
				return BuildCastExpression(cast, context);

			case ConstructionExpressionSyntax construction:
				return BuildConstructionExpression(construction, context);

			case SizeOfExpressionSyntax sizeOf:
				return new SizeOfExpression
				{
					SourceSyntax = sizeOf,
					Type = sizeOf.Type is null ? MissingType(sizeOf, "sizeof expression is missing a type.") : BuildTypeReference(sizeOf.Type)
				};

			case VTableOfExpressionSyntax vtableOf:
				return new VTableOfExpression
				{
					SourceSyntax = vtableOf,
					Type = vtableOf.Type is null ? MissingType(vtableOf, "vtableof expression is missing a type.") : BuildTypeReference(vtableOf.Type),
					InterfaceType = vtableOf.InterfaceType is null ? MissingType(vtableOf, "vtableof expression is missing an interface type.") : BuildTypeReference(vtableOf.InterfaceType)
				};

			case SymbolOfExpressionSyntax symbolOf:
				return BuildSymbolOfExpression(symbolOf);

			case InitializerListSyntax initializer:
				return BuildInitializerExpression(initializer, context);

			case LambdaExpressionSyntax lambda:
				return BuildLambdaExpression(lambda, context);

			default:
				Report(syntax, $"Unsupported expression syntax in {context}.");
				return null;
		}
	}

	SymbolOfExpression BuildSymbolOfExpression(SymbolOfExpressionSyntax syntax)
	{
		StringBuilder builder = new();
		foreach (Token token in syntax.Tokens)
			builder.Append(token.Value);
		return new SymbolOfExpression
		{
			SourceSyntax = syntax,
			Text = builder.ToString()
		};
	}

	LiteralExpression? BuildLiteralExpression(LiteralExpressionSyntax syntax)
	{
		if (syntax.Literal is not Token literal)
		{
			Report(syntax, "Literal expression is missing a token.");
			return null;
		}

		LiteralKind kind = literal.Class switch
		{
			TokenClass.Number => LiteralKind.Number,
			TokenClass.String when literal.Value.StartsWith("'", StringComparison.Ordinal) => LiteralKind.Character,
			TokenClass.String => LiteralKind.String,
			_ when literal.Value == "true" => LiteralKind.True,
			_ when literal.Value == "false" => LiteralKind.False,
			_ when literal.Value == "null" => LiteralKind.Null,
			_ => LiteralKind.String
		};

		object? value = kind switch
		{
			LiteralKind.True => true,
			LiteralKind.False => false,
			LiteralKind.Null => null,
			LiteralKind.String or LiteralKind.Character => DecodeStringLiteral(literal.Value),
			_ => literal.Value
		};
		if (kind == LiteralKind.Character && value is string characterText && characterText.Length != 1)
			Report(syntax, "Character literal must contain exactly one character.");

		return new LiteralExpression
		{
			SourceSyntax = syntax,
			Kind = kind,
			Text = literal.Value,
			Value = value
		};
	}

	NamedExpression BuildNamedExpression(QualifiedNameExpressionSyntax syntax, string context)
	{
		NamedExpression expression = new()
		{
			SourceSyntax = syntax,
			Name = GetRequiredIdentifier(syntax.Identifier, syntax, $"{context} name is missing an identifier.")
		};

		foreach (QualifierSyntax qualifier in syntax.Qualifiers ?? [])
		{
			if (qualifier.Identifier is null)
				Report(qualifier, "Expression qualifier is missing an identifier.");
			else
				expression.Qualifiers.Add(qualifier.Identifier.Value.Value);
		}

		return expression;
	}

	Expression BuildGroupedExpression(CommaExpressionSyntax syntax, string context)
	{
		GroupedExpression expression = new() { SourceSyntax = syntax };

		foreach (ExpressionSyntax item in syntax.Expressions ?? [])
		{
			expression.Items.Add(new GroupedExpressionItem
			{
				SourceSyntax = item,
				Expression = BuildExpression(item, $"{context} grouped expression item")
			});
		}

		return expression;
	}

	Expression BuildGroupedExpression(GroupedExpressionSyntax syntax, string context)
	{
		GroupedExpression expression = new() { SourceSyntax = syntax };

		foreach (GroupedExpressionItemSyntax item in syntax.ItemList?.Items ?? [])
		{
			expression.Items.Add(new GroupedExpressionItem
			{
				SourceSyntax = item,
				Name = item.Identifier?.Value,
				Expression = BuildExpression(item.Expression, $"{context} grouped expression item")
			});
		}

		return expression;
	}

	Expression BuildArrayExpression(ArrayExpressionSyntax syntax, string context)
	{
		ArrayExpression expression = new() { SourceSyntax = syntax };

		foreach (ExpressionSyntax item in syntax.ExpressionList?.Expressions ?? [])
		{
			if (BuildExpression(item, $"{context} array item") is Expression element)
				expression.Elements.Add(element);
		}

		return expression;
	}

	Expression BuildInitializerExpression(InitializerListSyntax syntax, string context)
	{
		InitializerExpression expression = new() { SourceSyntax = syntax };

		foreach (InitializerItemSyntax item in syntax.ItemList?.Items ?? [])
		{
			expression.Items.Add(new InitializerItem
			{
				SourceSyntax = item,
				Target = item.Target is null ? null : BuildInitializerTarget(item.Target, context),
				Expression = BuildExpression(item.Expression, $"{context} initializer item")
			});
		}

		return expression;
	}

	InitializerTarget BuildInitializerTarget(InitializerTargetSyntax syntax, string context)
	{
		InitializerTarget target = new() { SourceSyntax = syntax };

		foreach (InitializerTargetPartSyntax partSyntax in syntax.Parts ?? [])
		{
			InitializerTargetPart part = new()
			{
				SourceSyntax = partSyntax,
				Name = partSyntax.Identifier?.Value
			};

			foreach (ArgumentSyntax argument in partSyntax.ArgumentList?.Arguments ?? [])
				part.Arguments.Add(BuildArgumentExpression(argument, $"{context} initializer target"));

			target.Parts.Add(part);
		}

		return target;
	}

	Expression BuildCastExpression(CastExpressionSyntax syntax, string context)
	{
		CastExpression expression = new()
		{
			SourceSyntax = syntax,
			Type = syntax.Type is null ? null : BuildTypeReference(syntax.Type),
			LifetimeCastKind = syntax.LifetimeDeclarator?.Keyword?.Value,
			Kind = BuildCastKind(syntax),
			Expression = BuildExpression(syntax.Expression, $"{context} cast operand")
		};

		AddAnchors(expression.LifetimeCastAnchors, syntax.LifetimeDeclarator?.AnchorList);
		return expression;
	}

	Expression BuildConstructionExpression(ConstructionExpressionSyntax syntax, string context)
	{
		ConstructionExpression construction = new()
		{
			SourceSyntax = syntax,
			Kind = syntax.Keyword?.Value == "init" ? ConstructionKind.Init : ConstructionKind.New,
			Type = syntax.Type is null ? null : BuildTypeReference(syntax.Type),
			ElementCount = syntax.ElementCount is null ? null : BuildExpression(syntax.ElementCount, $"{context} construction element count"),
			Initializer = syntax.InitializerList is null ? null : (InitializerExpression?)BuildInitializerExpression(syntax.InitializerList, $"{context} construction initializer")
		};

		foreach (ArgumentSyntax argument in syntax.ArgumentList?.Arguments ?? [])
			construction.Arguments.Add(BuildArgumentExpression(argument, $"{context} construction argument"));

		if (syntax.AllocatorExpression is null)
			return construction;

		return new WithinExpression
		{
			SourceSyntax = syntax,
			Context = BuildExpression(syntax.AllocatorExpression, $"{context} allocator expression"),
			Expression = construction
		};
	}

	Expression BuildLambdaExpression(LambdaExpressionSyntax syntax, string context)
	{
		LambdaExpression expression = new()
		{
			SourceSyntax = syntax,
			Body = syntax.Body is null ? null : BuildLambdaBody(syntax.Body, context)
		};

		if (syntax.Parameter is not null)
			expression.Parameters.Add(BuildLambdaParameter(syntax.Parameter));

		foreach (LambdaParameterSyntax parameter in syntax.ParameterList?.Parameters ?? [])
			expression.Parameters.Add(BuildLambdaParameter(parameter));

		return expression;
	}

	BlockStatement? BuildLambdaBody(LambdaBodySyntax syntax, string context)
	{
		if (syntax.Expression is not null)
		{
			return new BlockStatement
			{
				SourceSyntax = syntax,
				Statements =
				{
					new ReturnStatement
					{
						SourceSyntax = syntax,
						Expression = BuildExpression(syntax.Expression, $"{context} lambda body")
					}
				}
			};
		}

		return syntax.MethodBody is null ? null : BuildFunctionBody(syntax.MethodBody);
	}

	LambdaParameter BuildLambdaParameter(LambdaParameterSyntax syntax)
	{
		return new LambdaParameter
		{
			SourceSyntax = syntax,
			Name = syntax.Identifier?.Value,
			Parameter = syntax.Parameter is null ? null : BuildParameterDefinition(syntax.Parameter)
		};
	}

	Expression BuildBinaryExpression(BinaryExpressionSyntax syntax, string context)
	{
		List<Expression?> operands = [BuildExpression(syntax.FirstExpression, $"{context} binary expression")];
		List<BinaryExpressionPartSyntax> parts = [];

		foreach (BinaryExpressionPartSyntax part in syntax.Parts ?? [])
		{
			parts.Add(part);
			operands.Add(BuildExpression(part.Expression, $"{context} binary operand"));
		}

		int operatorIndex = 0;
		return BuildBinaryExpressionTree(operands, parts, ref operatorIndex, minPrecedence: 1)
			?? new LiteralExpression { SourceSyntax = syntax, Kind = LiteralKind.Null, Text = "null" };
	}

	static Expression? BuildBinaryExpressionTree(List<Expression?> operands, List<BinaryExpressionPartSyntax> parts, ref int operatorIndex, int minPrecedence)
	{
		Expression? left = operands[operatorIndex];

		while (operatorIndex < parts.Count)
		{
			BinaryExpressionPartSyntax part = parts[operatorIndex];
			BinaryOperator op = BuildBinaryOperator(part.Operator);
			int precedence = GetBinaryPrecedence(op);

			if (precedence < minPrecedence)
				break;

			operatorIndex++;
			int nextMinPrecedence = IsRightAssociative(op) ? precedence : precedence + 1;
			Expression? right = BuildBinaryExpressionTree(operands, parts, ref operatorIndex, nextMinPrecedence);

			left = new BinaryExpression
			{
				SourceSyntax = part,
				Left = left,
				Operator = op,
				Right = right
			};
		}

		return left;
	}

	Expression BuildUnaryExpression(UnaryExpressionSyntax syntax, string context)
	{
		Expression? expression = BuildExpression(syntax.Expression, $"{context} unary operand");

		for (int i = (syntax.Prefixes?.Count ?? 0) - 1; i >= 0; i--)
		{
			UnaryPrefixSyntax prefix = syntax.Prefixes![i];
			UnaryOperator op = BuildUnaryOperator(prefix);

			if (op == UnaryOperator.Within)
			{
				expression = new WithinExpression
				{
					SourceSyntax = prefix,
					Context = BuildExpression(prefix.Expression, $"{context} within context"),
					Expression = expression
				};
			}
			else
			{
				expression = new UnaryExpression
				{
					SourceSyntax = prefix,
					Operator = op,
					Operand = expression,
					Context = prefix.Expression is null ? null : BuildExpression(prefix.Expression, $"{context} unary context")
				};
			}
		}

		if (syntax.FinallyKeyword is not null)
		{
			expression = new FinallyDeleteExpression
			{
				SourceSyntax = syntax,
				Expression = expression
			};
		}

		return expression ?? new LiteralExpression { SourceSyntax = syntax, Kind = LiteralKind.Null, Text = "null" };
	}

	Expression BuildPostfixExpression(PostfixExpressionSyntax syntax, string context)
	{
		Expression? expression = BuildExpression(syntax.Expression, $"{context} postfix target");

		foreach (PostfixPartSyntax part in syntax.Parts ?? [])
			expression = BuildPostfixPart(expression, part, context);

		return expression ?? new LiteralExpression { SourceSyntax = syntax, Kind = LiteralKind.Null, Text = "null" };
	}

	Expression BuildPostfixPart(Expression? target, PostfixPartSyntax syntax, string context)
	{
		switch (syntax)
		{
			case CallPostfixPartSyntax call:
			{
				CallExpression expression = new() { SourceSyntax = call, Target = target };
				AddArguments(expression.Arguments, call.ArgumentList, $"{context} call");
				return expression;
			}

			case IndexPostfixPartSyntax index:
			{
				IndexExpression expression = new() { SourceSyntax = index, Target = target };
				AddArguments(expression.Arguments, index.ArgumentList, $"{context} index");
				return expression;
			}

			case MemberPostfixPartSyntax member:
				return new MemberExpression
				{
					SourceSyntax = member,
					Target = target,
					Name = GetRequiredIdentifier(member.Identifier, member, "Member expression is missing a name.")
				};

			case NamelessIndexerPostfixPartSyntax indexer:
			{
				NamelessIndexerExpression expression = new() { SourceSyntax = indexer, Target = target };
				AddArguments(expression.Arguments, indexer.ArgumentList, $"{context} nameless indexer");
				return expression;
			}

			case GenericPostfixPartSyntax generic:
			{
				CallExpression expression = new() { SourceSyntax = generic, Target = target };
				foreach (TypeSyntax type in generic.TypeArgumentList?.Types ?? [])
					expression.TypeArguments.Add(BuildTypeReference(type));
				return expression;
			}

			case PostfixOperatorPartSyntax postfixOperator:
				return new PostfixUpdateExpression
				{
					SourceSyntax = postfixOperator,
					Expression = target,
					Operator = postfixOperator.Operator?.Value == "++" ? UpdateOperator.Increment : UpdateOperator.Decrement
				};

			default:
				Report(syntax, "Unsupported postfix expression syntax.");
				return target ?? new LiteralExpression { SourceSyntax = syntax, Kind = LiteralKind.Null, Text = "null" };
		}
	}

	ArgumentExpression BuildArgumentExpression(ArgumentSyntax syntax, string context)
	{
		ArgumentExpression expression = new()
		{
			SourceSyntax = syntax,
			Name = syntax.Identifier?.Value,
			Modifier = syntax.OutKeyword is not null
				? ArgumentModifier.Out
				: syntax.CatchKeyword is not null
					? ArgumentModifier.Catch
					: ArgumentModifier.None
		};

		if (syntax.WithinKeyword is not null)
		{
			return new ArgumentExpression
			{
				SourceSyntax = syntax,
				Value = new WithinExpression
				{
					SourceSyntax = syntax,
					Context = BuildExpression(syntax.Expression, $"{context} within argument")
				}
			};
		}

		if (syntax.CatchKeyword is not null && syntax.AutoKeyword is not null)
			expression.Type = new AutoTypeReference { SourceSyntax = syntax };

		if (syntax.DeclarationTarget is not null)
		{
			expression.Target = new DeclarationTarget { SourceSyntax = syntax.DeclarationTarget };
			BuildDeclarationTarget(expression.Target, syntax.DeclarationTarget, $"{context} argument declaration");
			return expression;
		}

		expression.Value = BuildExpression(syntax.Expression, context);
		return expression;
	}

	void AddArguments(List<ArgumentExpression> target, ArgumentListSyntax? syntax, string context)
	{
		foreach (ArgumentSyntax argument in syntax?.Arguments ?? [])
			target.Add(BuildArgumentExpression(argument, context));
	}

	static AssignmentOperator BuildAssignmentOperator(AssignmentOperatorSyntax? syntax)
	{
		return syntax?.Operator?.Value switch
		{
			"+=" => AssignmentOperator.Add,
			"-=" => AssignmentOperator.Subtract,
			"*=" => AssignmentOperator.Multiply,
			"/=" => AssignmentOperator.Divide,
			"%=" => AssignmentOperator.Modulo,
			"&=" => AssignmentOperator.BitwiseAnd,
			"|=" => AssignmentOperator.BitwiseOr,
			"^=" => AssignmentOperator.BitwiseXor,
			"<<=" => AssignmentOperator.LeftShift,
			">>=" => AssignmentOperator.RightShift,
			_ => AssignmentOperator.Assign
		};
	}

	static BinaryOperator BuildBinaryOperator(BinaryOperatorSyntax? syntax)
	{
		return syntax?.Operator?.Value switch
		{
			"||" => BinaryOperator.LogicalOr,
			"??" => BinaryOperator.NullCoalescing,
			"&&" => BinaryOperator.LogicalAnd,
			"|" => BinaryOperator.BitwiseOr,
			"^" => BinaryOperator.BitwiseXor,
			"&" => BinaryOperator.BitwiseAnd,
			"==" => BinaryOperator.Equal,
			"!=" => BinaryOperator.NotEqual,
			"<" => BinaryOperator.LessThan,
			"<=" => BinaryOperator.LessThanOrEqual,
			">" => BinaryOperator.GreaterThan,
			">=" => BinaryOperator.GreaterThanOrEqual,
			"<<" => BinaryOperator.LeftShift,
			">>" => BinaryOperator.RightShift,
			"+" => BinaryOperator.Add,
			"-" => BinaryOperator.Subtract,
			"*" => BinaryOperator.Multiply,
			"/" => BinaryOperator.Divide,
			"%" => BinaryOperator.Modulo,
			_ => BinaryOperator.Add
		};
	}

	static int GetBinaryPrecedence(BinaryOperator op)
	{
		return op switch
		{
			BinaryOperator.LogicalOr => 1,
			BinaryOperator.NullCoalescing => 2,
			BinaryOperator.LogicalAnd => 3,
			BinaryOperator.BitwiseOr => 4,
			BinaryOperator.BitwiseXor => 5,
			BinaryOperator.BitwiseAnd => 6,
			BinaryOperator.Equal or BinaryOperator.NotEqual => 7,
			BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual => 8,
			BinaryOperator.LeftShift or BinaryOperator.RightShift => 9,
			BinaryOperator.Add or BinaryOperator.Subtract => 10,
			BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo => 11,
			_ => 1
		};
	}

	static bool IsRightAssociative(BinaryOperator op)
	{
		return op == BinaryOperator.NullCoalescing;
	}

	static UnaryOperator BuildUnaryOperator(UnaryPrefixSyntax syntax)
	{
		return syntax.OperatorOrKeyword?.Value switch
		{
			"+" => UnaryOperator.Plus,
			"-" => UnaryOperator.Minus,
			"!" => UnaryOperator.LogicalNot,
			"~" => UnaryOperator.BitwiseNot,
			"&" => UnaryOperator.AddressOf,
			"*" => UnaryOperator.PointerDereference,
			"++" => UnaryOperator.Increment,
			"--" => UnaryOperator.Decrement,
			"^" => UnaryOperator.FromEnd,
			"await" => UnaryOperator.Await,
			"postpone" => UnaryOperator.Postpone,
			"throw" => UnaryOperator.Throw,
			"within" => UnaryOperator.Within,
			_ => UnaryOperator.Plus
		};
	}

	static CastKind BuildCastKind(CastExpressionSyntax syntax)
	{
		return syntax.CastKeyword?.Value switch
		{
			"params" => CastKind.Params,
			"struct" => CastKind.Struct,
			"class" => CastKind.Class,
			"delegate" => CastKind.Delegate,
			"fn" => CastKind.Function,
			"once" => CastKind.Once,
			"iter" => CastKind.Iter,
			"async" => CastKind.Async,
			_ => CastKind.Type
		};
	}

	static string DecodeStringLiteral(string text)
	{
		if (text.Length < 2)
			return text;

		char quote = text[0];
		if (text[^1] != quote || quote is not ('"' or '\'' or '`'))
			return text;

		StringBuilder builder = new();

		for (int i = 1; i < text.Length - 1; i++)
		{
			char c = text[i];
			if (c != '\\' || i + 1 >= text.Length - 1)
			{
				builder.Append(c);
				continue;
			}

			char escaped = text[++i];
			switch (escaped)
			{
				case '0':
					builder.Append('\0');
					break;
				case 'a':
					builder.Append('\a');
					break;
				case 'b':
					builder.Append('\b');
					break;
				case 'f':
					builder.Append('\f');
					break;
				case 'n':
					builder.Append('\n');
					break;
				case 'r':
					builder.Append('\r');
					break;
				case 't':
					builder.Append('\t');
					break;
				case 'v':
					builder.Append('\v');
					break;
				case '\\':
				case '"':
				case '\'':
				case '`':
					builder.Append(escaped);
					break;
				case 'x':
					AppendHexEscape(builder, text, ref i, maxDigits: 2);
					break;
				case 'u':
					AppendHexEscape(builder, text, ref i, maxDigits: 4);
					break;
				case 'U':
					AppendHexEscape(builder, text, ref i, maxDigits: 8);
					break;
				default:
					builder.Append(escaped);
					break;
			}
		}

		return builder.ToString();
	}

	static void AppendHexEscape(StringBuilder builder, string text, ref int index, int maxDigits)
	{
		int start = index + 1;
		int end = start;
		int limit = Math.Min(text.Length - 1, start + maxDigits);

		while (end < limit && Uri.IsHexDigit(text[end]))
			end++;

		if (end == start)
			return;

		string digits = text[start..end];
		if (int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
			builder.Append(char.ConvertFromUtf32(value));

		index = end - 1;
	}
}
