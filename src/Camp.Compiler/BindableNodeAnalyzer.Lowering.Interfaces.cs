using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	bool IsPropertyGetterReference(MemberReferenceExpression member)
	{
		return member.Member is FunctionDefinition function && function.Name == "get" + member.Name;
	}

	bool IsPropertySetterReference(MemberReferenceExpression member)
	{
		return member.Member is FunctionDefinition function && function.Name == "set" + member.Name;
	}

	CallExpression RewritePropertyGetterCall(MemberReferenceExpression getter, List<ArgumentExpression> arguments)
	{
		FunctionDefinition function = (FunctionDefinition)getter.Member!;
		string propertyType = getter.ResolvedType ?? function.ResolvedType ?? ErrorType;
		getter.Target = LowerExpression(getter.Target);
		getter.Name = function.Name;
		getter.ResolvedType = BuildFunctionValueType(function, IsInstanceInvocationFunction(function));

		CallExpression call = new()
		{
			SourceSyntax = getter.SourceSyntax,
			Target = getter,
			ResolvedType = materializedGenericReturnParameters.TryGetValue(function, out ParameterDefinition? resultParameter)
				? resultParameter.ResolvedType ?? propertyType ?? ErrorType
				: function.ResolvedType ?? ErrorType
		};
		for (int i = 0; i < arguments.Count; i++)
			call.Arguments.Add(LowerArgument(arguments[i]));
		callTargets[call] = function;
		AddImplicitDefaultArguments(call);
		if (IsInstanceInvocationFunction(function) && getter.Target is Expression receiver)
			RewriteInstanceInvocation(call, getter, receiver, function);
		else
			RewriteStaticCallableTarget(call, getter, function, isInstance: false);
		return call;
	}

	CallExpression RewritePropertySetterCall(MemberReferenceExpression setter, List<ArgumentExpression> arguments, Expression? value)
	{
		FunctionDefinition function = (FunctionDefinition)setter.Member!;
		setter.Target = LowerExpression(setter.Target);
		setter.Name = function.Name;
		setter.ResolvedType = BuildFunctionValueType(function, IsInstanceInvocationFunction(function));

		CallExpression call = new()
		{
			SourceSyntax = setter.SourceSyntax,
			Target = setter,
			ResolvedType = function.ResolvedType ?? "void"
		};
		for (int i = 0; i < arguments.Count; i++)
			call.Arguments.Add(LowerArgument(arguments[i]));
		callTargets[call] = function;
		AddImplicitDefaultArguments(call);

		Expression? loweredValue = LowerExpression(value);
		List<ParameterDefinition> callableParameters = GetCallableParameters(function.Parameters);
		int valueParameterIndex = callableParameters.Count == 0 ? -1 : GetPropertySetterValueParameterStart(callableParameters);
		ParameterDefinition? valueParameter = valueParameterIndex < 0 ? null : callableParameters[valueParameterIndex];
		if (valueParameter is not null)
			loweredValue = LowerInterfaceConversion(valueParameter.Type, valueParameter.ResolvedType, loweredValue);
		call.Arguments.Add(new ArgumentExpression
		{
			Value = loweredValue,
			ResolvedType = loweredValue?.ResolvedType ?? valueParameter?.ResolvedType ?? ErrorType
		});
		if (IsInstanceInvocationFunction(function) && setter.Target is Expression receiver)
			RewriteInstanceInvocation(call, setter, receiver, function);
		else
			RewriteStaticCallableTarget(call, setter, function, isInstance: false);
		ExpandParamsArguments(call);
		return call;
	}

	void RewriteStaticCallableTarget(CallExpression call, MemberReferenceExpression member, FunctionDefinition function, bool isInstance)
	{
		MethodReferenceExpression reference = new()
		{
			SourceSyntax = member.SourceSyntax,
			ResolvedType = BuildFunctionValueType(function, isInstance)
		};
		reference.Candidates.Add(function);
		call.Target = reference;
	}

	bool LowerInterfaceCall(CallExpression call)
	{
		if (call.Target is not MemberReferenceExpression { Target: not null, Member: FunctionDefinition function } member)
			return false;
		if (FindContainingType(function) is not InterfaceDefinition interfaceDefinition)
			return false;
		if (function.Modifier == FunctionModifier.Constructor)
			return false;

		Expression context = member.Target;
		if (!IsInterfaceInstanceReceiver(context.ResolvedType, interfaceDefinition) && TryGetGenericReceiverTypeName(context.ResolvedType, out string genericName))
		{
			if (!IsGenericParameterName(genericName))
				return false;
			member.Target = LowerVTableOfExpression(new VTableOfExpression
			{
				SourceSyntax = member.SourceSyntax,
				Type = new GenericParameterTypeReference { Name = genericName, ResolvedType = genericName },
				InterfaceType = InterfaceType(interfaceDefinition),
				ResolvedType = interfaceDefinition.Name + "*"
			});
			context = new CastExpression
			{
				SourceSyntax = context.SourceSyntax,
				Kind = CastKind.Type,
				Type = PointerTo(PointerTo(InterfaceType(interfaceDefinition))),
				Expression = context,
				ResolvedType = interfaceDefinition.Name + "**"
			};
		}

		call.Target = new MemberReferenceExpression
		{
			SourceSyntax = member.SourceSyntax,
			Target = new UnaryExpression
			{
				SourceSyntax = member.Target.SourceSyntax,
				Operator = UnaryOperator.PointerDereference,
				Operand = member.Target,
				ResolvedType = TryGetPointerElementType(member.Target.ResolvedType ?? "") ?? $"{interfaceDefinition.Name}*"
			},
			Name = GetCallableName(function),
			Member = function,
			ResolvedType = member.ResolvedType
		};
		call.Arguments.Insert(0, new ArgumentExpression
		{
			Value = context,
			ResolvedType = context.ResolvedType
		});
		return true;
	}

	bool TryCreateInterfaceMethodDelegateComponents(MemberReferenceExpression member, out List<Expression> components)
	{
		components = [];
		if (member is not { Target: Expression receiver, Member: FunctionDefinition function })
			return false;
		if (FindContainingType(function) is not InterfaceDefinition interfaceDefinition)
			return false;
		if (function.Modifier == FunctionModifier.Constructor)
			return false;

		Expression vtable = receiver;
		Expression context = receiver;
		if (IsInterfaceInstanceReceiver(context.ResolvedType, interfaceDefinition) || !TryGetGenericReceiverTypeName(context.ResolvedType, out string genericName))
			return false;
		if (!IsGenericParameterName(genericName))
			return false;

		vtable = LowerVTableOfExpression(new VTableOfExpression
		{
			SourceSyntax = member.SourceSyntax,
			Type = new GenericParameterTypeReference { Name = genericName, ResolvedType = genericName },
			InterfaceType = InterfaceType(interfaceDefinition),
			ResolvedType = interfaceDefinition.Name + "*"
		});
		context = new CastExpression
		{
			SourceSyntax = context.SourceSyntax,
			Kind = CastKind.Type,
			Type = PointerTo(PointerTo(InterfaceType(interfaceDefinition))),
			Expression = context,
			ResolvedType = interfaceDefinition.Name + "**"
		};

		Expression slot = new MemberExpression
		{
			SourceSyntax = member.SourceSyntax,
			Target = vtable,
			Name = GetCallableName(function),
			ResolvedType = BuildFlattenedFunctionValueType(function, $"{interfaceDefinition.Name}**")
		};
		if (TryGetParamsComponentShape(null, member.ResolvedType, "value", out ParamsComponentShape delegateShape)
			&& delegateShape.Components.Count > 0
			&& slot.ResolvedType != delegateShape.Components[0].Type)
		{
			slot = new CastExpression
			{
				SourceSyntax = member.SourceSyntax,
				Kind = CastKind.Type,
				Type = TypeReferenceForResolvedName(delegateShape.Components[0].Type),
				Expression = slot,
				ResolvedType = delegateShape.Components[0].Type
			};
		}

		components.Add(slot);
		components.Add(new CastExpression
		{
			SourceSyntax = receiver.SourceSyntax,
			Kind = CastKind.Type,
			Type = TypeReferenceForResolvedName("void*"),
			Expression = context,
			ResolvedType = "void*"
		});
		return true;
	}

	static bool IsInterfaceInstanceReceiver(string? type, InterfaceDefinition interfaceDefinition)
	{
		return type == interfaceDefinition.Name + "**" || TryGetPointerElementType(type ?? "") == interfaceDefinition.Name;
	}

	static bool TryGetGenericReceiverTypeName(string? receiverType, out string genericName)
	{
		genericName = TryGetPointerElementType(receiverType ?? "") ?? BaseTypeName(receiverType ?? "");
		return !string.IsNullOrWhiteSpace(genericName) && genericName != ErrorType;
	}

	bool IsGenericParameterName(string name)
	{
		if (currentRewriteFunction is not null)
		{
			foreach (GenericParameter parameter in currentRewriteFunction.GenericParameters)
				if (parameter.Name == name)
					return true;
		}
		if (currentRewriteContainingType is not null)
		{
			foreach (GenericParameter parameter in currentRewriteContainingType.GenericParameters)
				if (parameter.Name == name)
					return true;
		}
		return false;
	}

	void LowerCallArgumentConversions(CallExpression call)
	{
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function))
			return;

		List<ParameterDefinition> parameters = GetCallableParametersForCall(function, IncludeExplicitThisArgument(call.Target, function));
		for (int i = 0; i < call.Arguments.Count && i < parameters.Count; i++)
		{
			if (parameters[i].Modifier == ParameterModifier.Out)
				continue;
			call.Arguments[i].Value = LowerInterfaceConversion(parameters[i].Type, parameters[i].ResolvedType, call.Arguments[i].Value);
			call.Arguments[i].ResolvedType = call.Arguments[i].Value?.ResolvedType ?? call.Arguments[i].ResolvedType;
		}
	}

	void AddImplicitWithinArgument(CallExpression call)
	{
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function) || !HasWithinParameter(function))
			return;
		if (function.IsAsync)
			return;

		if (HasExplicitWithinArgument(call.Arguments))
			return;

		int index = CountArgumentsBeforeWithinParameter(function, call.Target);
		foreach (ParameterDefinition parameter in function.Parameters)
		{
			if (parameter.Modifier == ParameterModifier.Thrown)
			{
				if (index < call.Arguments.Count && call.Arguments[index].Modifier == ArgumentModifier.Catch)
					index++;
				continue;
			}

			if (parameter.Modifier != ParameterModifier.Within && parameter is not WithinParameterDefinition)
				continue;

			List<ParameterDefinition> callableParameters = GetCallableParametersForCall(function, IncludeExplicitThisArgument(call.Target, function));
			int argumentIndex = FindArgumentIndexForCallableParameter(call.Arguments, callableParameters, index);
			int trailingExpandedReturnArguments = System.Math.Max(CountTrailingExpandedReturnArguments(call, function), CountTrailingOutArguments(call.Arguments));
			if (trailingExpandedReturnArguments > 0)
				argumentIndex = System.Math.Min(argumentIndex, System.Math.Max(0, call.Arguments.Count - trailingExpandedReturnArguments));
			int suppliedWithinIndex = FindSuppliedWithinArgumentIndex(parameter, call.Arguments);
			if (suppliedWithinIndex >= 0)
			{
				if (suppliedWithinIndex != argumentIndex)
				{
					ArgumentExpression supplied = call.Arguments[suppliedWithinIndex];
					call.Arguments.RemoveAt(suppliedWithinIndex);
					if (suppliedWithinIndex < argumentIndex)
						argumentIndex--;
					call.Arguments.Insert(argumentIndex, supplied);
				}
				return;
			}
				if (argumentIndex < call.Arguments.Count && IsWithinArgumentAlreadySupplied(parameter, call.Arguments[argumentIndex]))
					return;

				if (RequiresExplicitWithinArgument(call))
				{
					string parameterName = string.IsNullOrWhiteSpace(parameter.Name) ? "allocator" : parameter.Name;
					Report(GetRange(call.SourceSyntax ?? call.Target?.SourceSyntax), $"Call requires a within context for parameter '{parameterName}'; use within(allocator), within(default), or pass within null explicitly.");
					return;
				}

				call.Arguments.Insert(argumentIndex, new ArgumentExpression
				{
					SourceSyntax = call.SourceSyntax ?? call.Target?.SourceSyntax,
					Value = CurrentWithinArgument(call.SourceSyntax ?? call.Target?.SourceSyntax),
					ResolvedType = currentWithinContext?.ResolvedType ?? "#NULL"
			});
			return;
			}
		}

		bool RequiresExplicitWithinArgument(CallExpression call)
		{
			if (currentWithinContext is not null || currentDefaultWithinContextDepth > 0)
				return false;
			SyntaxNode? syntax = call.SourceSyntax ?? call.Target?.SourceSyntax;
			TokenRange? range = GetRange(syntax);
			if (range is not TokenRange tokenRange)
				return false;
			return currentModule?.SourceWithinAllocationPolicies.TryGetValue(tokenRange.Sequence, out WithinAllocationPolicy policy) == true
				&& policy == WithinAllocationPolicy.Explicit;
		}

		void NormalizeWithinArgumentOrder(CallExpression call)
	{
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function) || !HasWithinParameter(function))
			return;
		if (function.IsAsync)
			return;

		ParameterDefinition? within = GetWithinParameter(function);
		if (within is null)
			return;

		RemoveDuplicateWithinArguments(within, call.Arguments);
		RemoveNullBeforeCapturedAllocator(call.Arguments);
		int suppliedWithinIndex = FindSuppliedWithinArgumentIndex(within, call.Arguments);
		if (suppliedWithinIndex <= 0)
			return;

		int insertionIndex = suppliedWithinIndex;
		while (insertionIndex > 0 && call.Arguments[insertionIndex - 1].Modifier == ArgumentModifier.Out)
			insertionIndex--;
		if (insertionIndex == suppliedWithinIndex)
			return;

		ArgumentExpression supplied = call.Arguments[suppliedWithinIndex];
		call.Arguments.RemoveAt(suppliedWithinIndex);
		call.Arguments.Insert(insertionIndex, supplied);
	}

	void RemoveDuplicateWithinArguments(ParameterDefinition parameter, List<ArgumentExpression> arguments)
	{
		int keepIndex = -1;
		for (int i = 0; i < arguments.Count; i++)
		{
			if (!IsWithinArgumentAlreadySupplied(parameter, arguments[i]))
				continue;
			if (keepIndex < 0 || IsNullArgumentExpression(arguments[keepIndex]) && !IsNullArgumentExpression(arguments[i]))
				keepIndex = i;
		}
		if (keepIndex < 0)
			return;
		for (int i = arguments.Count - 1; i >= 0; i--)
		{
			if (i != keepIndex && IsWithinArgumentAlreadySupplied(parameter, arguments[i]))
			{
				arguments.RemoveAt(i);
				if (i < keepIndex)
					keepIndex--;
			}
		}
	}

	static void RemoveNullBeforeCapturedAllocator(List<ArgumentExpression> arguments)
	{
		for (int i = arguments.Count - 2; i >= 0; i--)
		{
			if (IsNullArgumentExpression(arguments[i]) && IsCapturedAllocatorArgument(arguments[i + 1]))
				arguments.RemoveAt(i);
		}
	}

	int CountTrailingExpandedReturnArguments(CallExpression call, FunctionDefinition function)
	{
		if (!TryGetExpandedReturnShape(call, function, out ParamsComponentShape shape)
			|| shape.Components.Count <= 1)
			return 0;
		return shape.Components.Count - 1;
	}

	static int CountTrailingOutArguments(List<ArgumentExpression> arguments)
	{
		int count = 0;
		for (int i = arguments.Count - 1; i >= 0; i--)
		{
			if (arguments[i].Modifier != ArgumentModifier.Out)
				break;
			count++;
		}
		return count;
	}

	int FindSuppliedWithinArgumentIndex(ParameterDefinition parameter, List<ArgumentExpression> arguments)
	{
		for (int i = 0; i < arguments.Count; i++)
			if (IsWithinArgumentAlreadySupplied(parameter, arguments[i]))
				return i;
		return -1;
	}

	int CountArgumentsBeforeWithinParameter(FunctionDefinition function, Expression? callTarget)
	{
		return GetCallableParametersForCall(function, IncludeExplicitThisArgument(callTarget, function)).Count;
	}

	static bool HasExplicitWithinArgument(List<ArgumentExpression> arguments)
	{
		foreach (ArgumentExpression argument in arguments)
		{
			if (argument.Value is WithinExpression { Context: not null })
				return true;
		}
		return false;
	}

	bool IsWithinArgumentAlreadySupplied(ParameterDefinition parameter, ArgumentExpression argument)
	{
		return argument.Value is WithinExpression
			|| IsCurrentWithinArgument(argument)
			|| IsCapturedAllocatorArgument(argument)
			|| IsNullArgumentExpression(argument)
			|| IsGeneratedHiddenForwardingArgumentFor(parameter, argument);
	}

	bool IsCurrentWithinArgument(ArgumentExpression argument)
	{
		return currentWithinContext is VariableReferenceExpression { Variable: Definition current }
			&& argument.Value is VariableReferenceExpression { Variable: Definition supplied }
			&& ReferenceEquals(current, supplied);
	}

	static bool IsCapturedAllocatorArgument(ArgumentExpression argument)
	{
		return argument.Value is VariableReferenceExpression { Variable: Definition { Name: string variableName } }
				&& variableName.StartsWith("_allocator", StringComparison.Ordinal)
			|| argument.Value is NamedExpression { Qualifiers.Count: 0, Name: string named }
				&& named.StartsWith("_allocator", StringComparison.Ordinal);
	}

	Expression? LowerInterfaceConversion(TypeReference? targetType, Expression? value)
	{
		return LowerInterfaceConversion(targetType, targetType?.ResolvedType, value);
	}

	Expression? LowerInterfaceConversion(TypeReference? targetType, string? targetResolvedType, Expression? value)
	{
		targetType = UnwrapInterfaceConversionType(targetType);
		if (targetType is not PointerTypeReference targetPointer || value is null)
		{
			if (value is null || !TryGetInterfacePointerDefinition(targetResolvedType, out InterfaceDefinition? resolvedTargetInterface) || resolvedTargetInterface is null)
				return value;
			return LowerInterfaceConversionToTarget(value, resolvedTargetInterface, targetResolvedType);
		}
		if (!TryGetInterfacePointerDefinition(targetPointer, out InterfaceDefinition? targetInterface) || targetInterface is null)
			return value;

		return LowerInterfaceConversionToTarget(value, targetInterface, targetPointer.ResolvedType ?? targetResolvedType);
	}

	static TypeReference? UnwrapInterfaceConversionType(TypeReference? type)
	{
		while (true)
		{
			type = type switch
			{
				AttributedTypeReference attributed => attributed.Type,
				ConstTypeReference constant => constant.Type,
				ConstOfTypeReference constOf => constOf.Type,
				VolatileTypeReference vol => vol.Type,
				EscapedTypeReference escaped => escaped.Type,
				ScopedTypeReference scoped => scoped.Type,
				UnscopedTypeReference unscoped => unscoped.Type,
				_ => type
			};

			if (type is not (AttributedTypeReference or ConstTypeReference or ConstOfTypeReference or VolatileTypeReference or EscapedTypeReference or ScopedTypeReference or UnscopedTypeReference))
				return type;
		}
	}

	Expression? LowerInterfaceConversionToTarget(Expression value, InterfaceDefinition targetInterface, string? targetResolvedType)
	{
		string sourceType = value.ResolvedType ?? "";
		if (sourceType == targetResolvedType
			|| sourceType == targetInterface.Name + "**"
			|| TryGetPointerElementType(sourceType) == targetInterface.Name)
		{
			return value;
		}

		if (TryGetPointerElementType(sourceType) is string className
			&& typeDefinitions.TryGetValue(BaseTypeName(className), out TypeDefinition? typeDefinition)
			&& typeDefinition is ClassDefinition classDefinition
			&& TryFindInterfaceLowering(classDefinition, targetInterface, out InterfaceImplementationLowering? lowering)
			&& lowering is not null)
		{
			return CreateClassInterfaceConversion(value, classDefinition, targetInterface, lowering);
		}

		if (typeDefinitions.TryGetValue(BaseTypeName(sourceType), out TypeDefinition? sourceTypeDefinition)
			&& sourceTypeDefinition is StructDefinition structDefinition
			&& TryFindInterfaceLowering(structDefinition, targetInterface, out InterfaceImplementationLowering? structLowering)
			&& structLowering is not null)
		{
			return CreateStructInterfaceConversion(value, structDefinition, targetInterface, structLowering);
		}

		if (TryGetPointerElementType(sourceType) is string sourceInterfaceName
			&& typeDefinitions.TryGetValue(BaseTypeName(sourceInterfaceName), out TypeDefinition? sourceDefinition)
			&& sourceDefinition is InterfaceDefinition sourceInterface
			&& InterfaceContainsBase(sourceInterface, targetInterface))
		{
			return new CastExpression
			{
				Type = PointerTo(PointerTo(InterfaceType(targetInterface))),
				Kind = CastKind.Type,
				Expression = value,
				ResolvedType = $"{targetInterface.Name}**"
			};
		}

		return value;
	}

	Expression CreateClassInterfaceConversion(Expression value, ClassDefinition sourceClass, InterfaceDefinition targetInterface, InterfaceImplementationLowering lowering)
	{
		FunctionDefinition? accessor = FindInterfaceAccessorFunction(lowering.Type, targetInterface);
		if (accessor is null)
			return lowering.Field is null ? value : AddressOfInterfaceField(value, lowering.Field);

		Expression receiver = value;
		if (!ReferenceEquals(sourceClass, lowering.Type))
		{
			receiver = new CastExpression
			{
				Kind = CastKind.Type,
				Type = PointerTo(InterfaceType(lowering.Type)),
				Expression = value,
				ResolvedType = $"{lowering.Type.Name}*"
			};
		}

		MethodReferenceExpression target = new()
		{
			ResolvedType = BuildFunctionValueType(accessor, isInstance: false)
		};
		target.Candidates.Add(accessor);
		return new CallExpression
		{
			Target = target,
			Arguments =
			{
				new ArgumentExpression
				{
					Value = receiver,
					ResolvedType = receiver.ResolvedType
				}
			},
			ResolvedType = accessor.ResolvedType
		};
	}

	FunctionDefinition? FindInterfaceAccessorFunction(TypeDefinition type, InterfaceDefinition targetInterface)
	{
		if (type is not ClassDefinition classDefinition)
			return null;
		string name = InterfaceAccessorName(targetInterface);
		foreach (FunctionDefinition function in classDefinition.Functions)
		{
			if (function.Name == name)
				return function;
		}
		return null;
	}

	bool IsInterfacePointerType(TypeReference? type)
	{
		return TryGetInterfacePointerDefinition(type, out InterfaceDefinition? interfaceDefinition)
			&& interfaceDefinition is not null;
	}

	bool TryGetInterfacePointerDefinition(TypeReference? type, out InterfaceDefinition? interfaceDefinition)
	{
		interfaceDefinition = null;
		if (type is not PointerTypeReference pointer)
			return false;

		TypeReference? element = pointer.ElementType ?? pointer;
		element = UnwrapInterfaceConversionType(element);
		if (element is PointerTypeReference innerPointer)
			element = UnwrapInterfaceConversionType(innerPointer.ElementType ?? innerPointer);
		if (element is null)
			return false;

		return TryGetInterfaceDefinition(element, out interfaceDefinition)
			&& interfaceDefinition is not null;
	}

	bool TryGetInterfacePointerDefinition(string? type, out InterfaceDefinition? interfaceDefinition)
	{
		interfaceDefinition = null;
		if (string.IsNullOrWhiteSpace(type))
			return false;

		string candidate = StripLifetimeQualifiers(type).Trim();
		if (TryGetPointerElementType(candidate) is string firstElement)
			candidate = firstElement;
		if (TryGetPointerElementType(candidate) is string secondElement)
			candidate = secondElement;

		if (!typeDefinitions.TryGetValue(BaseTypeName(candidate), out TypeDefinition? definition)
			|| definition is not InterfaceDefinition found)
		{
			return TryFindLoweredInterfaceDefinition(candidate, out interfaceDefinition);
		}

		interfaceDefinition = found;
		return true;
	}

	bool TryFindLoweredInterfaceDefinition(string type, out InterfaceDefinition? interfaceDefinition)
	{
		string name = BaseTypeName(type);
		foreach (List<InterfaceImplementationLowering> lowerings in classInterfaceLowerings.Values)
		{
			foreach (InterfaceImplementationLowering lowering in lowerings)
			{
				if (lowering.Interface.Name == name)
				{
					interfaceDefinition = lowering.Interface;
					return true;
				}
			}
		}

		foreach (List<InterfaceImplementationLowering> lowerings in structInterfaceLowerings.Values)
		{
			foreach (InterfaceImplementationLowering lowering in lowerings)
			{
				if (lowering.Interface.Name == name)
				{
					interfaceDefinition = lowering.Interface;
					return true;
				}
			}
		}

		interfaceDefinition = null;
		return false;
	}

	Expression CreateStructInterfaceConversion(Expression value, StructDefinition structDefinition, InterfaceDefinition targetInterface, InterfaceImplementationLowering lowering)
	{
		if (currentStatementPrefix is null)
			return value;

		string localName = NewGeneratedLocalName("iface");
		TypeReference indirectType = InterfaceIndirectType(targetInterface, structDefinition);
		DeclarationStatement local = CreateGeneratedLocal(localName, indirectType.ResolvedType ?? $"{InterfaceIndirectName(targetInterface)}<{structDefinition.Name}>", indirectType, initialValue: null);
		currentStatementPrefix.Add(local);

		Expression localReference = CreateVariableReference(local.Target, local.Target.ResolvedType ?? indirectType.ResolvedType ?? ErrorType);
		currentStatementPrefix.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				Target = CreateInterfaceIndirectMember(localReference, "_vt", $"const {targetInterface.Name}*"),
				Operator = AssignmentOperator.Assign,
				Value = new VariableReferenceExpression
				{
					Variable = lowering.VTable,
					ResolvedType = lowering.VTable.ResolvedType
				},
				ResolvedType = $"const {targetInterface.Name}*"
			}
		});
		currentStatementPrefix.Add(new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = new AssignmentExpression
			{
				Target = CreateInterfaceIndirectMember(localReference, "ctx", $"{structDefinition.Name}*"),
				Operator = AssignmentOperator.Assign,
				Value = new UnaryExpression
				{
					Operator = UnaryOperator.AddressOf,
					Operand = value,
					ResolvedType = $"{structDefinition.Name}*"
				},
				ResolvedType = $"{structDefinition.Name}*"
			}
		});

			UnaryExpression addressOfVTable = new()
			{
				Operator = UnaryOperator.AddressOf,
				Operand = CreateInterfaceIndirectMember(localReference, "_vt", $"const {targetInterface.Name}*"),
				ResolvedType = $"const {targetInterface.Name}**"
			};
			return new CastExpression
			{
				Type = PointerTo(PointerTo(InterfaceType(targetInterface))),
				Kind = CastKind.Type,
				Expression = addressOfVTable,
				ResolvedType = $"{targetInterface.Name}**"
			};
		}

	static MemberReferenceExpression CreateInterfaceIndirectMember(Expression target, string name, string type)
	{
		return new MemberReferenceExpression
		{
			Target = target,
			Name = name,
			ResolvedType = type
		};
	}

	Expression AddressOfInterfaceField(Expression instance, FieldDefinition field)
	{
		return new UnaryExpression
		{
			Operator = UnaryOperator.AddressOf,
			Operand = new MemberReferenceExpression
			{
				Target = instance,
				Name = field.Name,
				Member = field,
				ResolvedType = field.ResolvedType
			},
			ResolvedType = $"{field.ResolvedType}*"
		};
	}

	bool NeedsVirtualTableAssignment(TypeDefinition type)
	{
		return type is ClassDefinition classDefinition && virtualClassLowerings.ContainsKey(classDefinition);
	}

	Expression? CreateVirtualTableAssignment(Expression instance, TypeDefinition type)
	{
		if (type is not ClassDefinition classDefinition || !virtualClassLowerings.TryGetValue(classDefinition, out VirtualClassLowering? lowering))
			return null;

		VirtualClassLowering root = GetRootVirtualLowering(lowering);
		return new AssignmentExpression
		{
			Target = new MemberReferenceExpression
			{
				Target = instance,
				Name = VirtualTableFieldName,
				Member = root.Field,
				ResolvedType = $"{root.VTableType.Name}*"
			},
			Operator = AssignmentOperator.Assign,
			Value = CreateVirtualTablePointer(lowering, root),
			ResolvedType = $"{root.VTableType.Name}*"
		};
	}

	void InsertCreateVirtualTableAssignment(FunctionDefinition function, TypeDefinition? containingType)
	{
		if (function.Body is null
			|| function.Name != CreateMethodName
			|| containingType is not ClassDefinition classDefinition
			|| classDefinition.IsShadow
			|| !NeedsVirtualTableAssignment(classDefinition))
			return;

		DeclarationStatement? createdLocal = null;
		BlockStatement? guardBody = null;
		foreach (Statement statement in function.Body.Statements)
		{
			if (createdLocal is null
				&& statement is DeclarationStatement { Target.Names.Count: 1 } declaration
				&& declaration.Target.ResolvedType == $"{classDefinition.Name}*")
			{
				createdLocal = declaration;
				continue;
			}

			if (statement is IfStatement ifStatement)
			{
				guardBody = ifStatement.Body as BlockStatement;
				if (guardBody is null && ifStatement.Body is not null)
				{
					guardBody = new BlockStatement { ResolvedType = "void" };
					guardBody.Statements.Add(ifStatement.Body);
					ifStatement.Body = guardBody;
				}
				break;
			}
		}

		if (createdLocal is null || guardBody is null)
			return;

		Expression target = CreateVariableReference(createdLocal.Target, $"{classDefinition.Name}*");
		if (CreateVirtualTableAssignment(target, classDefinition) is not Expression assignment)
			return;

		int insertIndex = guardBody.Statements.Count > 0 && IsZeroAllocatedInstanceStatement(guardBody.Statements[0]) ? 1 : 0;
		if (guardBody.Statements.Count > insertIndex
			&& guardBody.Statements[insertIndex] is ExpressionStatement { Expression: AssignmentExpression { Target: MemberReferenceExpression { Name: VirtualTableFieldName } } })
			return;

		guardBody.Statements.Insert(insertIndex, new ExpressionStatement
		{
			ResolvedType = "void",
			Expression = assignment
		});
	}

	static bool IsZeroAllocatedInstanceStatement(Statement statement)
	{
		return statement is ExpressionStatement
		{
			Expression: AssignmentExpression
			{
				Target: UnaryExpression { Operator: UnaryOperator.PointerDereference },
				Value: DefaultExpression
			}
		};
	}

	VirtualClassLowering GetRootVirtualLowering(VirtualClassLowering lowering)
	{
		while (lowering.BaseLowering is not null)
			lowering = lowering.BaseLowering;
		return lowering;
	}

	Expression CreateVirtualTablePointer(VirtualClassLowering target, VirtualClassLowering root)
	{
		Expression expression = new NamedExpression
		{
			Name = target.VTable?.Name ?? VirtualTableVariableName(target.Class),
			ResolvedType = target.VTableType.Name
		};
		List<(string Name, string Type)> path = [];
		for (VirtualClassLowering? current = target; current is not null && !ReferenceEquals(current, root); current = current.BaseLowering)
		{
			if (current.BaseLowering is not null && current.BaseClass is not null)
				path.Add((current.BaseClass.Name, current.BaseLowering.VTableType.Name));
		}
		for (int i = 0; i < path.Count; i++)
		{
			expression = new MemberReferenceExpression
			{
				Target = expression,
				Name = path[i].Name,
				ResolvedType = path[i].Type
			};
		}
		return new UnaryExpression
		{
			Operator = UnaryOperator.AddressOf,
			Operand = expression,
			ResolvedType = $"{root.VTableType.Name}*"
		};
	}

	bool TryFindInterfaceLowering(ClassDefinition classDefinition, InterfaceDefinition targetInterface, out InterfaceImplementationLowering? lowering)
	{
		return TryFindInterfaceLowering(classDefinition, targetInterface, [], out lowering);
	}

	bool TryFindInterfaceLowering(ClassDefinition classDefinition, InterfaceDefinition targetInterface, HashSet<ClassDefinition> seen, out InterfaceImplementationLowering? lowering)
	{
		lowering = null;
		if (!seen.Add(classDefinition))
			return false;
		if (classInterfaceLowerings.TryGetValue(classDefinition, out List<InterfaceImplementationLowering>? lowerings))
		{
			foreach (InterfaceImplementationLowering candidate in lowerings)
			{
				if (ReferenceEquals(candidate.Interface, targetInterface) || InterfaceContainsBase(candidate.Interface, targetInterface))
				{
					lowering = candidate;
					return true;
				}
			}
		}

		foreach (TypeDefinition baseType in GetDirectBaseClasses(classDefinition))
		{
			if (baseType is ClassDefinition baseClass && TryFindInterfaceLowering(baseClass, targetInterface, seen, out lowering))
				return true;
		}
		return false;
	}

	bool TryFindInterfaceLowering(StructDefinition structDefinition, InterfaceDefinition targetInterface, out InterfaceImplementationLowering? lowering)
	{
		lowering = null;
		if (!structInterfaceLowerings.TryGetValue(structDefinition, out List<InterfaceImplementationLowering>? lowerings))
			return false;

		foreach (InterfaceImplementationLowering candidate in lowerings)
		{
			if (ReferenceEquals(candidate.Interface, targetInterface) || InterfaceContainsBase(candidate.Interface, targetInterface))
			{
				lowering = candidate;
				return true;
			}
		}
		return false;
	}

	bool InterfaceContainsBase(InterfaceDefinition source, InterfaceDefinition target)
	{
		if (ReferenceEquals(source, target))
			return true;
		foreach (TypeReference baseType in source.BaseTypes)
		{
			if (TryGetInterfaceDefinition(baseType, out InterfaceDefinition? baseInterface)
				&& baseInterface is not null
				&& InterfaceContainsBase(baseInterface, target))
				return true;
		}
		return false;
	}

	ArgumentExpression LowerArgument(ArgumentExpression argument)
	{
		if (argument.Target is not null)
			LowerArgumentDeclaration(argument);
		else if (argument.Modifier is ArgumentModifier.Out or ArgumentModifier.Catch && IsDiscardExpression(argument.Value))
			argument.Value = CreateDiscardReference(argument.ResolvedType ?? argument.Value?.ResolvedType ?? ErrorType, argument.SourceSyntax);
		else if (TryMaterializeInitializerAddressArgument(argument))
			return argument;

		argument.Value = LowerExpression(argument.Value);
		return argument;
	}

	bool TryMaterializeInitializerAddressArgument(ArgumentExpression argument)
	{
		if (argument.Value is not InitializerExpression initializer
			|| string.IsNullOrWhiteSpace(argument.MaterializedInitializerAddressType)
			|| string.IsNullOrWhiteSpace(argument.MaterializedInitializerAddressResultType)
			|| currentStatementPrefix is null)
			return false;

		string localType = argument.MaterializedInitializerAddressType!;
		initializer.PlainDeclarationInitializer = true;
		DeclarationStatement local = CreateGeneratedLocal(NewGeneratedLocalName("initializer"), localType, TypeReferenceForResolvedName(localType), initializer);
		currentStatementPrefix.Add(local);
		argument.Value = new UnaryExpression
		{
			SourceSyntax = argument.SourceSyntax ?? initializer.SourceSyntax,
			Operator = UnaryOperator.AddressOf,
			Operand = CreateVariableReference(local.Target, localType),
			ResolvedType = argument.MaterializedInitializerAddressResultType
		};
		argument.ResolvedType = argument.MaterializedInitializerAddressResultType;
		return true;
	}
}
