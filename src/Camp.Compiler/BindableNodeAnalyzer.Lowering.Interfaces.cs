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
		getter.Target = LowerExpression(getter.Target);
		getter.Name = function.Name;
		getter.ResolvedType = BuildFunctionValueType(function, isInstance: true);

		CallExpression call = new()
		{
			SourceSyntax = getter.SourceSyntax,
			Target = getter,
			ResolvedType = function.ResolvedType ?? ErrorType
		};
		for (int i = 0; i < arguments.Count; i++)
			call.Arguments.Add(LowerArgument(arguments[i]));
		callTargets[call] = function;
		AddImplicitDefaultArguments(call);
		ExpandParamsArguments(call);
		if (getter.Target is Expression receiver)
			RewriteInstanceInvocation(call, getter, receiver, function);
		return call;
	}

	CallExpression RewritePropertySetterCall(MemberReferenceExpression setter, List<ArgumentExpression> arguments, Expression? value)
	{
		FunctionDefinition function = (FunctionDefinition)setter.Member!;
		setter.Target = LowerExpression(setter.Target);
		setter.Name = function.Name;
		setter.ResolvedType = BuildFunctionValueType(function, isInstance: true);

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
		ExpandParamsArguments(call);

		Expression? loweredValue = LowerExpression(value);
		ParameterDefinition? valueParameter = function.Parameters.Count == 0 ? null : function.Parameters[^1];
		if (valueParameter?.Type is not null)
			loweredValue = LowerInterfaceConversion(valueParameter.Type, loweredValue);
		call.Arguments.Add(new ArgumentExpression
		{
			Value = loweredValue,
			ResolvedType = loweredValue?.ResolvedType ?? valueParameter?.ResolvedType ?? ErrorType
		});
		if (setter.Target is Expression receiver)
			RewriteInstanceInvocation(call, setter, receiver, function);
		return call;
	}

	void LowerInterfaceCall(CallExpression call)
	{
		if (call.Target is not MemberReferenceExpression { Target: not null, Member: FunctionDefinition function } member)
			return;
		if (FindContainingType(function) is not InterfaceDefinition)
			return;
		if (function.Modifier == FunctionModifier.Constructor)
			return;

		Expression context = member.Target;
		call.Arguments.Insert(0, new ArgumentExpression
		{
			Value = context,
			ResolvedType = context.ResolvedType
		});
	}

	void LowerCallArgumentConversions(CallExpression call)
	{
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function))
			return;

		List<ParameterDefinition> parameters = GetCallableParameters(function.Parameters);
		for (int i = 0; i < call.Arguments.Count && i < parameters.Count; i++)
		{
			if (parameters[i].Modifier == ParameterModifier.Out)
				continue;
			call.Arguments[i].Value = LowerInterfaceConversion(parameters[i].Type, call.Arguments[i].Value);
			call.Arguments[i].ResolvedType = call.Arguments[i].Value?.ResolvedType ?? call.Arguments[i].ResolvedType;
		}
	}

	void AddImplicitWithinArgument(CallExpression call)
	{
		if (!callTargets.TryGetValue(call, out FunctionDefinition? function) || !HasWithinParameter(function))
			return;

		List<ParameterDefinition> callableParameters = GetCallableParameters(function.Parameters);
		int index = callableParameters.Count;
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

			if (index < call.Arguments.Count && IsWithinArgumentAlreadySupplied(call.Arguments[index]))
				return;

			call.Arguments.Insert(index, new ArgumentExpression
			{
				SourceSyntax = call.SourceSyntax ?? call.Target?.SourceSyntax,
				Value = CurrentWithinArgument(call.SourceSyntax ?? call.Target?.SourceSyntax),
				ResolvedType = currentWithinContext?.ResolvedType ?? "#NULL"
			});
			return;
		}
	}

	static bool IsWithinArgumentAlreadySupplied(ArgumentExpression argument)
	{
		return argument.Modifier != ArgumentModifier.Catch || argument.Value is WithinExpression { Expression: null };
	}

	Expression? LowerInterfaceConversion(TypeReference? targetType, Expression? value)
	{
		if (targetType is not PointerTypeReference targetPointer || value is null)
			return value;
		if (!TryGetInterfaceDefinition(targetPointer.ElementType ?? targetPointer, out InterfaceDefinition? targetInterface) || targetInterface is null)
			return value;

		string sourceType = value.ResolvedType ?? "";
		if (sourceType == targetPointer.ResolvedType || TryGetPointerElementType(sourceType) == targetInterface.Name)
			return value;

		if (TryGetPointerElementType(sourceType) is string className
			&& typeDefinitions.TryGetValue(BaseTypeName(className), out TypeDefinition? typeDefinition)
			&& typeDefinition is ClassDefinition classDefinition
			&& TryFindInterfaceLowering(classDefinition, targetInterface, out InterfaceImplementationLowering? lowering)
			&& lowering is not null)
		{
			return lowering.Field is null ? value : AddressOfInterfaceField(value, lowering.Field);
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

	bool IsInterfacePointerType(TypeReference? type)
	{
		return type is PointerTypeReference pointer
			&& TryGetInterfaceDefinition(pointer.ElementType ?? pointer, out InterfaceDefinition? interfaceDefinition)
			&& interfaceDefinition is not null;
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
				Target = CreateInterfaceIndirectMember(localReference, "_vt", $"{targetInterface.Name}*"),
				Operator = AssignmentOperator.Assign,
				Value = new UnaryExpression
				{
					Operator = UnaryOperator.AddressOf,
					Operand = new NamedExpression
					{
						Name = lowering.VTable.Name,
						ResolvedType = lowering.VTable.ResolvedType
					},
					ResolvedType = $"{targetInterface.Name}*"
				},
				ResolvedType = $"{targetInterface.Name}*"
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

		return new UnaryExpression
		{
			Operator = UnaryOperator.AddressOf,
			Operand = CreateInterfaceIndirectMember(localReference, "_vt", $"{targetInterface.Name}*"),
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
		List<string> path = [];
		for (VirtualClassLowering? current = target; current is not null && !ReferenceEquals(current, root); current = current.BaseLowering)
		{
			if (current.BaseClass is not null)
				path.Add(current.BaseClass.Name);
		}
		for (int i = path.Count - 1; i >= 0; i--)
		{
			expression = new MemberReferenceExpression
			{
				Target = expression,
				Name = path[i],
				ResolvedType = i == 0 ? root.VTableType.Name : ErrorType
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
		lowering = null;
		if (!classInterfaceLowerings.TryGetValue(classDefinition, out List<InterfaceImplementationLowering>? lowerings))
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

		argument.Value = LowerExpression(argument.Value);
		return argument;
	}
}
