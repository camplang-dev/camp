using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void ValidateIndexAwareParameters(FunctionDefinition definition)
	{
		List<ParameterDefinition> callableParameters = GetCallableParameters(definition.Parameters);
		for (int i = 0; i < callableParameters.Count; i++)
		{
			ParameterDefinition parameter = callableParameters[i];
			bool hasIndex = HasAttribute(parameter.Attributes, "@index");
			bool hasRange = HasAttribute(parameter.Attributes, "@range");
			if (parameter.DefaultValue is UnaryExpression { Operator: UnaryOperator.FromEnd }
				&& !hasIndex
				&& (i == 0 || !HasAttribute(callableParameters[i - 1].Attributes, "@range")))
				Report(GetRange(parameter.DefaultValue.SourceSyntax) ?? GetNameRange(parameter) ?? GetRange(parameter.SourceSyntax), "A ^ default value is valid only for the count parameter paired with an @range parameter.");

			if (!hasIndex && !hasRange)
				continue;

			if (!IsIntegralType(parameter.ResolvedType ?? ErrorType))
				Report(GetNameRange(parameter), $"{(hasRange ? "@range" : "@index")} parameter '{parameter.Name}' must be integral.");

			if (!hasRange)
				continue;

			if (i + 1 >= callableParameters.Count)
			{
				Report(GetNameRange(parameter), "@range must mark the first parameter of an index/count pair.");
				continue;
			}

			ParameterDefinition count = callableParameters[i + 1];
			if (!IsIntegralType(count.ResolvedType ?? ErrorType))
				Report(GetNameRange(count), "@range count parameter must be integral.");

			if (count.DefaultValue is UnaryExpression { Operator: UnaryOperator.FromEnd } && !HasFromEndRangeCountDefault(parameter, count))
				Report(GetRange(count.DefaultValue.SourceSyntax) ?? GetNameRange(count) ?? GetRange(count.SourceSyntax), "A ^ default value is valid only for the count parameter paired with an @range parameter.");
		}
	}

	void AnalyzeRangeAwareArguments(List<ArgumentExpression> arguments, List<ParameterDefinition> callableParameters, Expression? receiver, BodyScope scope, AnalysisScope typeScope, SyntaxNode? fallbackSyntax)
	{
		AddImplicitRangeDefaultArguments(arguments, callableParameters, receiver, fallbackSyntax);
		for (int i = 0; i < arguments.Count && i < callableParameters.Count; i++)
		{
			ParameterDefinition parameter = callableParameters[i];
			if (arguments[i].Value is RangeExpression range)
			{
				if (!HasAttribute(parameter.Attributes, "@range"))
				{
					SyntaxNode? syntax = range.SourceSyntax ?? arguments[i].SourceSyntax ?? fallbackSyntax;
					Report(GetRange(syntax), "Range argument requires an @range parameter.");
					arguments[i].Value = ErrorExpression(ErrorType, syntax);
					arguments[i].ResolvedType = ErrorType;
					if (i + 1 < callableParameters.Count)
					{
						arguments.Insert(i + 1, new ArgumentExpression
						{
							SourceSyntax = arguments[i].SourceSyntax,
							Value = ErrorExpression(ErrorType, syntax),
							ResolvedType = ErrorType
						});
						i++;
					}
					continue;
				}

				if (i + 1 >= callableParameters.Count)
				{
					Report(GetRange(parameter.SourceSyntax ?? fallbackSyntax), "@range must mark the first parameter of an index/count pair.");
					continue;
				}

				Expression? length = CreateLengthExpression(receiver, range.SourceSyntax ?? arguments[i].SourceSyntax ?? fallbackSyntax);
				if (length is null)
				{
					Report(GetRange(range.SourceSyntax ?? arguments[i].SourceSyntax ?? fallbackSyntax), "Range argument requires an accessible length field, length property, or getLength() method.");
					continue;
				}

				Expression start = ClampBoundary(CreateBoundaryExpression(range.Start, length, defaultToLength: false, range.SourceSyntax), length, range.SourceSyntax);
				Expression end = ClampBoundary(CreateBoundaryExpression(range.End, length, defaultToLength: true, range.SourceSyntax), length, range.SourceSyntax);
				Expression count = CreateRangeCountExpression(start, end, range.SourceSyntax);

				arguments[i] = new ArgumentExpression
				{
					SourceSyntax = arguments[i].SourceSyntax,
					Value = start,
					ResolvedType = "nuint"
				};
				arguments.Insert(i + 1, new ArgumentExpression
				{
					SourceSyntax = arguments[i].SourceSyntax,
					Value = count,
					ResolvedType = "nuint"
				});
				i++;
				continue;
			}

			if (arguments[i].Value is UnaryExpression { Operator: UnaryOperator.FromEnd } fromEnd)
			{
				bool allowed = HasAttribute(parameter.Attributes, "@index") || HasAttribute(parameter.Attributes, "@range");
				if (!allowed)
				{
					SyntaxNode? errorSyntax = fromEnd.SourceSyntax ?? fromEnd.Operand?.SourceSyntax ?? arguments[i].SourceSyntax ?? fallbackSyntax;
					Report(GetRange(errorSyntax), "^ from-end syntax requires the receiver to expose a length (.length, .Length, or getLength()).");
					arguments[i].Value = ErrorExpression(ErrorType, errorSyntax);
					arguments[i].ResolvedType = ErrorType;
					continue;
				}

				SyntaxNode? syntax = fromEnd.SourceSyntax ?? fromEnd.Operand?.SourceSyntax ?? arguments[i].SourceSyntax ?? fallbackSyntax;
				Expression? length = CreateLengthExpression(receiver, syntax);
				if (length is null)
				{
					Report(GetRange(syntax), "^ from-end syntax requires the receiver to expose a length (.length, .Length, or getLength()).");
					continue;
				}

				arguments[i].Value = CreateFromEndExpression(fromEnd, length);
				arguments[i].ResolvedType = "nuint";
			}
		}
	}

	void AddImplicitRangeDefaultArguments(List<ArgumentExpression> arguments, List<ParameterDefinition> callableParameters, Expression? receiver, SyntaxNode? fallbackSyntax)
	{
		int argumentIndex = 0;
		for (int parameterIndex = 0; parameterIndex < callableParameters.Count; parameterIndex++)
		{
			ParameterDefinition parameter = callableParameters[parameterIndex];
			if (parameter is SizeOfParameterDefinition)
				continue;
			if (argumentIndex < arguments.Count && !IsExplicitHiddenArgument(arguments[argumentIndex]))
			{
				argumentIndex++;
				continue;
			}

			if (HasAttribute(parameter.Attributes, "@range") && parameter.DefaultValue is not null)
			{
				Expression? defaultValue = CloneDefaultArgumentExpression(parameter.DefaultValue);
				arguments.Insert(argumentIndex, new ArgumentExpression
				{
					SourceSyntax = fallbackSyntax ?? parameter.SourceSyntax,
					Value = defaultValue,
					ResolvedType = defaultValue?.ResolvedType ?? parameter.ResolvedType
				});
				argumentIndex++;
				continue;
			}

			if (HasAttribute(parameter.Attributes, "@index") && parameter.DefaultValue is UnaryExpression { Operator: UnaryOperator.FromEnd } fromEndIndexDefault)
			{
				Expression? receiverLength = CreateLengthExpression(receiver, fallbackSyntax ?? parameter.SourceSyntax);
				if (receiverLength is null)
				{
					Report(GetRange(fallbackSyntax ?? parameter.SourceSyntax), "^ index default requires the receiver to expose a length (.length, .Length, or getLength()).");
					continue;
				}

				Expression defaultIndex = CreateFromEndExpression(fromEndIndexDefault, receiverLength);
				arguments.Insert(argumentIndex, new ArgumentExpression
				{
					SourceSyntax = fallbackSyntax ?? parameter.SourceSyntax,
					Value = defaultIndex,
					ResolvedType = "nuint"
				});
				argumentIndex++;
				continue;
			}

			if (parameter.DefaultValue is not UnaryExpression { Operator: UnaryOperator.FromEnd } fromEndDefault)
				continue;
			if (parameterIndex == 0 || !HasAttribute(callableParameters[parameterIndex - 1].Attributes, "@range"))
				continue;
			if (argumentIndex > 0 && arguments[argumentIndex - 1].Value is RangeExpression)
				continue;

			Expression? length = CreateLengthExpression(receiver, fallbackSyntax ?? parameter.SourceSyntax);
			if (length is null)
			{
				Report(GetRange(fallbackSyntax ?? parameter.SourceSyntax), "Range count default requires an accessible length field, length property, or getLength() method.");
				continue;
			}

			Expression count = argumentIndex > 0 && arguments[argumentIndex - 1].Value is Expression index
				? CreateRangeCountExpression(index, CreateFromEndExpression(fromEndDefault, length), parameter.DefaultValue.SourceSyntax)
				: CreateFromEndExpression(fromEndDefault, length);
			arguments.Insert(argumentIndex, new ArgumentExpression
			{
				SourceSyntax = fallbackSyntax ?? parameter.SourceSyntax,
				Value = count,
				ResolvedType = "nuint"
			});
			argumentIndex++;
		}
	}

	Expression? GetRangeReceiver(Expression? target)
	{
		return target switch
		{
			MemberExpression member => member.Target,
			MemberReferenceExpression member => member.Target,
			_ => null
		};
	}

	Expression? CreateLengthExpression(Expression? receiver, SyntaxNode? syntax)
	{
		if (receiver is null)
			return null;

		string receiverType = receiver.ResolvedType ?? "";
		if (TryGetFixedArrayShape(receiverType, out _, out long fixedLength))
			return NumberLiteral(fixedLength.ToString(System.Globalization.CultureInfo.InvariantCulture), "nuint");

		List<FunctionDefinition> lengthFunctions = LookupMemberFunctions(receiverType, "getLength", syntax);
		if (lengthFunctions.Count > 0)
		{
			FunctionDefinition lengthFunction = lengthFunctions[0];
			CallExpression call = new()
			{
				SourceSyntax = syntax ?? receiver.SourceSyntax,
				Target = new MemberReferenceExpression
				{
					SourceSyntax = syntax ?? receiver.SourceSyntax,
					Target = CloneParamsExpansionExpression(receiver),
					Name = "getLength",
					Member = lengthFunction,
					ResolvedType = BuildFunctionValueType(lengthFunction, IsInstanceInvocationFunction(lengthFunction))
				},
				ResolvedType = "nuint"
			};
			callTargets[call] = lengthFunction;
			return call;
		}

		return new MemberExpression
		{
			SourceSyntax = syntax ?? receiver.SourceSyntax,
			Target = CloneParamsExpansionExpression(receiver),
			Name = "length",
			ResolvedType = "nuint"
		};
	}

	Expression CreateBoundaryExpression(Expression? boundary, Expression length, bool defaultToLength, SyntaxNode? syntax)
	{
		if (boundary is null)
			return defaultToLength ? CloneParamsExpansionExpression(length) ?? length : NumberLiteral("0", "nuint");
		if (boundary is UnaryExpression { Operator: UnaryOperator.FromEnd } fromEnd)
			return CreateFromEndExpression(fromEnd, length);
		return boundary;
	}

	Expression CreateFromEndExpression(UnaryExpression fromEnd, Expression length)
	{
		return new BinaryExpression
		{
			SourceSyntax = fromEnd.SourceSyntax,
			Left = CloneParamsExpansionExpression(length),
			Operator = BinaryOperator.Subtract,
			Right = fromEnd.Operand,
			ResolvedType = "nuint"
		};
	}

	Expression ClampBoundary(Expression boundary, Expression length, SyntaxNode? syntax)
	{
		Expression zero = NumberLiteral("0", "nuint");
		Expression upper = CloneParamsExpansionExpression(length) ?? length;
		return new ConditionalExpression
		{
			SourceSyntax = syntax,
			Condition = new BinaryExpression
			{
				SourceSyntax = syntax,
				Left = boundary,
				Operator = BinaryOperator.GreaterThan,
				Right = upper,
				ResolvedType = "bool"
			},
			WhenTrue = CloneParamsExpansionExpression(length) ?? length,
			WhenFalse = new ConditionalExpression
			{
				SourceSyntax = syntax,
				Condition = new BinaryExpression
				{
					SourceSyntax = syntax,
					Left = CloneParamsExpansionExpression(boundary),
					Operator = BinaryOperator.LessThan,
					Right = zero,
					ResolvedType = "bool"
				},
				WhenTrue = zero,
				WhenFalse = CloneParamsExpansionExpression(boundary),
				ResolvedType = "nuint"
			},
			ResolvedType = "nuint"
		};
	}

	Expression CreateRangeCountExpression(Expression start, Expression end, SyntaxNode? syntax)
	{
		return new ConditionalExpression
		{
			SourceSyntax = syntax,
			Condition = new BinaryExpression
			{
				SourceSyntax = syntax,
				Left = CloneParamsExpansionExpression(end),
				Operator = BinaryOperator.GreaterThanOrEqual,
				Right = CloneParamsExpansionExpression(start),
				ResolvedType = "bool"
			},
			WhenTrue = new BinaryExpression
			{
				SourceSyntax = syntax,
				Left = CloneParamsExpansionExpression(end),
				Operator = BinaryOperator.Subtract,
				Right = CloneParamsExpansionExpression(start),
				ResolvedType = "nuint"
			},
			WhenFalse = NumberLiteral("0", "nuint"),
			ResolvedType = "nuint"
		};
	}

	static bool HasFromEndRangeCountDefault(ParameterDefinition rangeParameter, ParameterDefinition countParameter)
	{
		return HasAttribute(rangeParameter.Attributes, "@range") && countParameter.DefaultValue is UnaryExpression { Operator: UnaryOperator.FromEnd };
	}

	static bool HasRangeArgument(List<ArgumentExpression> arguments)
	{
		foreach (ArgumentExpression argument in arguments)
			if (argument.Value is RangeExpression)
				return true;
		return false;
	}
}
