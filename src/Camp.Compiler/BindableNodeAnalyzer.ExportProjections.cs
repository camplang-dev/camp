using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void AnalyzeExportProjections(Module module)
	{
		Dictionary<Definition, ExportProjectionDefinition> projected = new();
		List<Definition> generated = [];
		foreach (ExportProjectionDefinition projection in module.ExportProjections)
		{
			projection.ResolvedType = "export";
			if (projection.ExportedDefinition is not null)
				continue;
			if (!TryResolveExportProjectionTarget(projection, out Definition? target) || target is null)
				continue;
			projection.Target = target;
			projection.ResolvedType = target.ResolvedType ?? target.Name;

			if (target.Public is null)
			{
				string visibility = target.Export is not null ? "export" : target.Internal is not null ? "internal" : "private";
				Report(GetRange(projection.SourceSyntax), $"Export projection target '{target.Name}' must be public; it is {visibility}.");
				continue;
			}

			if (!projected.TryAdd(target, projection))
			{
				Report(GetRange(projection.SourceSyntax), $"Declaration '{target.Name}' already has an export projection in this artifact.");
				continue;
			}

			string externalName = string.IsNullOrWhiteSpace(projection.Alias) ? target.Name : projection.Alias!;
			Definition? exportDefinition = string.Equals(externalName, target.Name, StringComparison.Ordinal)
				? PromoteProjectionTarget(target)
				: CreateProjectedDefinition(target, externalName, projection);
			if (exportDefinition is null)
				continue;
			projection.ExportedDefinition = exportDefinition;
			if (projection.HasMemberBlock)
				ApplyMemberProjection(projection, target, exportDefinition);
			if (!ReferenceEquals(exportDefinition, target))
				generated.Add(exportDefinition);
		}

		foreach (Definition definition in generated)
		{
			module.Definitions.Add(definition);
			module.DefinitionSources[definition] = GetRange(definition.SourceSyntax) is TokenRange range ? range.Sequence : null;
		}
	}

	void ApplyMemberProjection(ExportProjectionDefinition projection, Definition source, Definition exported)
	{
		if (source is not TypeDefinition sourceType || exported is not TypeDefinition exportedType)
		{
			Report(GetRange(projection.SourceSyntax), $"Only type export projections may use a member block.");
			return;
		}

		foreach (ExportProjectionMember member in projection.Members)
		{
			if (!TryResolveProjectedTypeMember(sourceType, member, out Definition? target) || target is null)
				continue;
			member.Target = target;
			if (target is FieldDefinition { Modifier: FieldModifier.Static, IsInline: false })
			{
				Report(GetRange(member.SourceSyntax), $"Mutable static field '{target.Name}' cannot be exported with a projection; export a getter function instead.");
				continue;
			}
			if (target is FieldDefinition { Modifier: not FieldModifier.Static, IsInline: false })
			{
				Report(GetRange(member.SourceSyntax), $"Instance field '{target.Name}' cannot be selected in an export projection member block.");
				continue;
			}

			string externalName = string.IsNullOrWhiteSpace(member.Alias) ? target.Name : member.Alias!;
			if (ReferenceEquals(sourceType, exportedType) && string.Equals(externalName, target.Name, StringComparison.Ordinal))
			{
				target.Export = "export";
				member.ExportedDefinition = target;
				continue;
			}

			Definition? clone = CloneProjectedMember(target, externalName, member, exportedType);
			if (clone is null)
				continue;
			member.ExportedDefinition = clone;
			switch (exportedType)
			{
				case ClassDefinition classDefinition:
					AddProjectedClassMember(classDefinition, clone);
					break;
				case StructDefinition structDefinition:
					AddProjectedStructMember(structDefinition, clone);
					break;
				case InterfaceDefinition interfaceDefinition when clone is FunctionDefinition function:
					interfaceDefinition.Functions.Add(function);
					break;
				case EnumDefinition enumDefinition when clone is FunctionDefinition function:
					enumDefinition.Functions.Add(function);
					break;
				case NewtypeDefinition newtypeDefinition:
					AddProjectedNewtypeMember(newtypeDefinition, clone);
					break;
			}
		}
	}

	bool TryResolveProjectedTypeMember(TypeDefinition type, ExportProjectionMember member, out Definition? target)
	{
		string name = member.IsDestructor ? "~" + member.Name : member.Name;
		IEnumerable<Definition> members = type switch
		{
			ClassDefinition classDefinition => classDefinition.Fields.Cast<Definition>().Concat(classDefinition.Functions),
			StructDefinition structDefinition => structDefinition.Fields.Cast<Definition>().Concat(structDefinition.Functions),
			InterfaceDefinition interfaceDefinition => interfaceDefinition.Functions,
			EnumDefinition enumDefinition => enumDefinition.Values.Cast<Definition>().Concat(enumDefinition.Functions),
			NewtypeDefinition newtypeDefinition => newtypeDefinition.Fields.Cast<Definition>().Concat(newtypeDefinition.Functions),
			_ => []
		};
		List<Definition> matches = members.Where(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal)).ToList();
		if (matches.Count == 1)
		{
			target = matches[0];
			return true;
		}
		if (matches.Count > 1)
		{
			Report(GetRange(member.SourceSyntax), $"Projection member '{name}' is overloaded; use the overload's full declared name.");
			target = null;
			return false;
		}
		Report(GetRange(member.SourceSyntax), $"Projection member '{name}' was not found on type '{type.Name}'.");
		target = null;
		return false;
	}

	Definition? CloneProjectedMember(Definition target, string externalName, ExportProjectionMember member, TypeDefinition exportedType)
	{
		switch (target)
		{
			case FunctionDefinition function:
				FunctionDefinition clone = CloneProjectedFunctionMember(function);
				clone.SourceSyntax = member.SourceSyntax;
				clone.Name = function.Modifier == FunctionModifier.Constructor || function.Name == exportedType.Name
					? exportedType.Name
					: function.Modifier == FunctionModifier.Destructor || function.Name.StartsWith("~", StringComparison.Ordinal)
						? "~" + exportedType.Name
						: externalName;
				clone.Symbol = externalName;
				clone.Export = "export";
				clone.Provenance = new NodeProvenance(member.SourceSyntax, function.Symbol, $"export projection for member '{function.Name}'");
				clone.GeneratedInfo = new GeneratedDeclarationInfo(GeneratedDeclarationCategory.None, $"export projection for member '{function.Name}'", function);
				return clone;
			case FieldDefinition field:
				FieldDefinition fieldClone = CloneProjectedField(field);
				fieldClone.SourceSyntax = member.SourceSyntax;
				fieldClone.Name = externalName;
				fieldClone.Symbol = externalName;
				fieldClone.Export = "export";
				fieldClone.Provenance = new NodeProvenance(member.SourceSyntax, field.Symbol, $"export projection for member '{field.Name}'");
				fieldClone.GeneratedInfo = new GeneratedDeclarationInfo(GeneratedDeclarationCategory.None, $"export projection for member '{field.Name}'", field);
				return fieldClone;
			default:
				Report(GetRange(member.SourceSyntax), $"Member '{target.Name}' cannot be exported with a projection.");
				return null;
		}
	}

	static void AddProjectedClassMember(ClassDefinition type, Definition member)
	{
		if (member is FunctionDefinition function)
			type.Functions.Add(function);
		else if (member is FieldDefinition field)
			type.Fields.Add(field);
	}

	static void AddProjectedStructMember(StructDefinition type, Definition member)
	{
		if (member is FunctionDefinition function)
			type.Functions.Add(function);
		else if (member is FieldDefinition field)
			type.Fields.Add(field);
	}

	static void AddProjectedNewtypeMember(NewtypeDefinition type, Definition member)
	{
		if (member is FunctionDefinition function)
			type.Functions.Add(function);
		else if (member is FieldDefinition field)
			type.Fields.Add(field);
	}

	bool TryResolveExportProjectionTarget(ExportProjectionDefinition projection, out Definition? target)
	{
		string name = projection.TargetName;
		foreach (TypeDefinition type in typeDefinitions.Values)
		{
			if (type.Name == name && IsImportedProjectionTarget(type, projection))
			{
				target = type;
				return true;
			}
		}

		foreach (AliasDefinition alias in aliasDefinitions.Values)
		{
			if (alias.Name == name && IsImportedProjectionTarget(alias, projection))
			{
				target = alias;
				return true;
			}
		}

		List<FunctionDefinition> functions = [];
		foreach (Definition definition in currentModule?.Definitions ?? [])
			if (definition is FunctionDefinition function
				&& GetExplicitThisParameter(function) is null
				&& IsFunctionNamed(function, name)
				&& IsImportedProjectionTarget(function, projection))
				functions.Add(function);

		if (functions.Count == 1)
		{
			target = functions[0];
			return true;
		}
		if (functions.Count > 1)
		{
			Report(GetRange(projection.SourceSyntax), $"Export projection target '{name}' is overloaded; projection by overload signature is not implemented yet.");
			target = null;
			return false;
		}

		List<VariableDefinition> variables = [];
		foreach (Definition definition in currentModule?.Definitions ?? [])
			if (definition is VariableDefinition variable
				&& variable.Name == name
				&& variable.IsInline
				&& IsImportedProjectionTarget(variable, projection))
				variables.Add(variable);

		if (variables.Count == 1)
		{
			target = variables[0];
			return true;
		}

		Report(GetRange(projection.SourceSyntax), $"Export projection target '{ProjectionTargetText(projection)}' could not be found.");
		target = null;
		return false;
	}

	bool IsImportedProjectionTarget(Definition definition, ExportProjectionDefinition projection)
	{
		if (projection.TargetQualifiers.Count == 0)
			return IsDefinitionVisible(definition, projection.SourceSyntax);
		if (GetRange(projection.SourceSyntax) is not TokenRange range)
			return true;
		string qualifier = string.Join("::", projection.TargetQualifiers);
		if (!string.IsNullOrWhiteSpace(definition.Namespace) && string.Equals(definition.Namespace, qualifier, StringComparison.Ordinal))
			return IsNamespaceVisible(definition.Namespace, range.Sequence) || IsNamespacePlainImported(definition.Namespace, range.Sequence);
		return TryResolveNamespaceAlias(qualifier, range.Sequence, out string? aliasedNamespace)
			&& string.Equals(aliasedNamespace, definition.Namespace, StringComparison.Ordinal);
	}

	static string ProjectionTargetText(ExportProjectionDefinition projection)
	{
		return projection.TargetQualifiers.Count == 0
			? projection.TargetName
			: string.Join("::", projection.TargetQualifiers) + "::" + projection.TargetName;
	}

	static Definition PromoteProjectionTarget(Definition target)
	{
		target.Export = "export";
		return target;
	}

	Definition? CreateProjectedDefinition(Definition target, string externalName, ExportProjectionDefinition projection)
	{
		switch (target)
		{
			case AliasDefinition alias:
				return new AliasDefinition
				{
					SourceSyntax = projection.SourceSyntax,
					Name = externalName,
					Symbol = externalName,
					Export = "export",
					TargetName = alias.TargetName,
					ResolvedTargetName = alias.ResolvedTargetName,
					ResolvedType = alias.ResolvedType,
					TargetKind = alias.TargetKind,
					Provenance = new NodeProvenance(projection.SourceSyntax, alias.Symbol, $"export projection for '{alias.Name}'"),
					GeneratedInfo = new GeneratedDeclarationInfo(GeneratedDeclarationCategory.None, $"export projection for '{alias.Name}'", alias)
				};
			case TypeDefinition type:
				return CloneProjectedTypeDefinition(type, externalName, projection);
			case VariableDefinition variable:
				return new VariableDefinition
				{
					SourceSyntax = projection.SourceSyntax,
					Name = externalName,
					Symbol = externalName,
					Export = "export",
					IsInline = variable.IsInline,
					IsFixedStorage = variable.IsFixedStorage,
					Type = CloneProjectionTypeReference(variable.Type),
					InitialValue = variable.InitialValue,
					ConstantValue = variable.ConstantValue,
					ResolvedType = variable.ResolvedType,
					Provenance = new NodeProvenance(projection.SourceSyntax, variable.Symbol, $"export projection for '{variable.Name}'"),
					GeneratedInfo = new GeneratedDeclarationInfo(GeneratedDeclarationCategory.None, $"export projection for '{variable.Name}'", variable)
				};
			case FunctionDefinition function:
				return CreateFunctionProjectionForwarder(function, externalName, projection);
			default:
				Report(GetRange(projection.SourceSyntax), $"Declaration '{target.Name}' cannot be projected for export.");
				return null;
		}
	}

	Definition CloneProjectedTypeDefinition(TypeDefinition target, string externalName, ExportProjectionDefinition projection)
	{
		Definition clone = target switch
		{
			ClassDefinition classDefinition => CloneProjectedClass(classDefinition, includeMembers: !projection.HasMemberBlock),
			StructDefinition structDefinition => CloneProjectedStruct(structDefinition, includeMembers: !projection.HasMemberBlock),
			InterfaceDefinition interfaceDefinition => CloneProjectedInterface(interfaceDefinition, includeMembers: !projection.HasMemberBlock),
			EnumDefinition enumDefinition => CloneProjectedEnum(enumDefinition, includeMembers: !projection.HasMemberBlock),
			NewtypeDefinition newtypeDefinition => CloneProjectedNewtype(newtypeDefinition, includeMembers: !projection.HasMemberBlock),
			_ => throw new InvalidOperationException($"Type '{target.Name}' cannot be projected for export.")
		};
		clone.SourceSyntax = projection.SourceSyntax;
		clone.Name = externalName;
		clone.Symbol = externalName;
		clone.Export = "export";
		clone.ResolvedType = externalName;
		clone.Provenance = new NodeProvenance(projection.SourceSyntax, target.Symbol, $"export projection for '{target.Name}'");
		clone.GeneratedInfo = new GeneratedDeclarationInfo(GeneratedDeclarationCategory.None, $"export projection for '{target.Name}'", target);
		if (clone is TypeDefinition projectedType)
			foreach (GenericParameter parameter in target.GenericParameters)
				projectedType.GenericParameters.Add(CloneProjectionGenericParameter(parameter));
		return clone;
	}

	ClassDefinition CloneProjectedClass(ClassDefinition source, bool includeMembers)
	{
		ClassDefinition clone = new()
		{
			Modifier = source.Modifier,
			IsEscaped = source.IsEscaped
		};
		foreach (TypeReference baseType in source.BaseTypes)
			clone.BaseTypes.Add(CloneProjectionTypeReference(baseType)!);
		foreach (TypeReference baseType in source.LoweredInterfaceBaseTypes)
			clone.LoweredInterfaceBaseTypes.Add(CloneProjectionTypeReference(baseType)!);
		if (includeMembers)
		{
			foreach (FunctionDefinition function in source.Functions.Where(static function => function.Export is not null || function.Public is not null))
				clone.Functions.Add(CloneProjectedFunctionMember(function));
			foreach (FieldDefinition field in source.Fields.Where(static field => field.Modifier == FieldModifier.Static && (field.Export is not null || field.Public is not null)))
				clone.Fields.Add(CloneProjectedField(field));
		}
		return clone;
	}

	StructDefinition CloneProjectedStruct(StructDefinition source, bool includeMembers)
	{
		StructDefinition clone = new()
		{
			Modifier = source.Modifier,
			SourceInterface = source.SourceInterface
		};
		foreach (TypeReference baseType in source.BaseTypes)
			clone.BaseTypes.Add(CloneProjectionTypeReference(baseType)!);
		foreach (TypeReference baseType in source.LoweredInterfaceBaseTypes)
			clone.LoweredInterfaceBaseTypes.Add(CloneProjectionTypeReference(baseType)!);
		foreach (FieldDefinition field in source.Fields)
			clone.Fields.Add(CloneProjectedField(field));
		if (includeMembers)
			foreach (FunctionDefinition function in source.Functions.Where(static function => function.Export is not null || function.Public is not null))
				clone.Functions.Add(CloneProjectedFunctionMember(function));
		return clone;
	}

	InterfaceDefinition CloneProjectedInterface(InterfaceDefinition source, bool includeMembers)
	{
		InterfaceDefinition clone = new() { IsEscaped = source.IsEscaped };
		foreach (TypeReference baseType in source.BaseTypes)
			clone.BaseTypes.Add(CloneProjectionTypeReference(baseType)!);
		if (includeMembers)
			foreach (FunctionDefinition function in source.Functions)
				clone.Functions.Add(CloneProjectedFunctionMember(function));
		return clone;
	}

	EnumDefinition CloneProjectedEnum(EnumDefinition source, bool includeMembers)
	{
		EnumDefinition clone = new() { UnderlyingType = CloneProjectionTypeReference(source.UnderlyingType) };
		foreach (VariableDefinition value in source.Values)
			clone.Values.Add(CloneProjectedVariable(value, export: null));
		if (includeMembers)
			foreach (FunctionDefinition function in source.Functions.Where(static function => function.Export is not null || function.Public is not null))
				clone.Functions.Add(CloneProjectedFunctionMember(function));
		return clone;
	}

	NewtypeDefinition CloneProjectedNewtype(NewtypeDefinition source, bool includeMembers)
	{
		NewtypeDefinition clone = new()
		{
			IteratorKind = source.IteratorKind,
			UnderlyingType = CloneProjectionTypeReference(source.UnderlyingType)
		};
		foreach (ParameterDefinition parameter in source.Parameters)
			clone.Parameters.Add(CloneProjectionParameter(parameter));
		if (includeMembers)
		{
			foreach (FieldDefinition field in source.Fields.Where(static field => field.Modifier == FieldModifier.Static && (field.Export is not null || field.Public is not null)))
				clone.Fields.Add(CloneProjectedField(field));
			foreach (FunctionDefinition function in source.Functions.Where(static function => function.Export is not null || function.Public is not null))
				clone.Functions.Add(CloneProjectedFunctionMember(function));
		}
		return clone;
	}

	FieldDefinition CloneProjectedField(FieldDefinition source)
	{
		return new FieldDefinition
		{
			SourceSyntax = source.SourceSyntax,
			Name = source.Name,
			Symbol = source.Symbol,
			Export = source.Export is not null || source.Public is not null ? "export" : null,
			Modifier = source.Modifier,
			IsInline = source.IsInline,
			IsFixedStorage = source.IsFixedStorage,
			LifetimeBinding = source.LifetimeBinding,
			Type = CloneProjectionTypeReference(source.Type),
			InitialValue = source.InitialValue,
			ConstantValue = source.ConstantValue,
			ResolvedType = source.ResolvedType
		};
	}

	FunctionDefinition CloneProjectedFunctionMember(FunctionDefinition source)
	{
		FunctionDefinition clone = new()
		{
			SourceSyntax = source.SourceSyntax,
			Name = source.Name,
			Symbol = source.Symbol,
			Export = "export",
			Extern = "extern",
			Modifier = source.Modifier,
			IsAsync = source.IsAsync,
			IsNoAwait = source.IsNoAwait,
			IteratorKind = source.IteratorKind,
			CallSpec = source.CallSpec,
			InvokerName = source.InvokerName,
			FullCallableName = source.FullCallableName,
			ReturnType = CloneProjectionTypeReference(source.ReturnType),
			ResolvedType = source.ResolvedType,
			CallableAscriptionType = CloneProjectionTypeReference(source.CallableAscriptionType),
			CallableAscriptionNewtype = source.CallableAscriptionNewtype,
			InterfaceImplementationSlotName = source.InterfaceImplementationSlotName,
			InterfaceImplementationInterface = source.InterfaceImplementationInterface,
			InterfaceImplementationMember = source.InterfaceImplementationMember
		};
		foreach (GenericParameter parameter in source.GenericParameters)
			clone.GenericParameters.Add(CloneProjectionGenericParameter(parameter));
		foreach (ParameterDefinition parameter in source.Parameters)
			clone.Parameters.Add(CloneProjectionParameter(parameter));
		return clone;
	}

	VariableDefinition CloneProjectedVariable(VariableDefinition source, string? export = "export")
	{
		return new VariableDefinition
		{
			SourceSyntax = source.SourceSyntax,
			Name = source.Name,
			Symbol = source.Symbol,
			Export = export,
			IsInline = source.IsInline,
			IsFixedStorage = source.IsFixedStorage,
			Type = CloneProjectionTypeReference(source.Type),
			InitialValue = source.InitialValue,
			ConstantValue = source.ConstantValue,
			ResolvedType = source.ResolvedType
		};
	}

	FunctionDefinition CreateFunctionProjectionForwarder(FunctionDefinition target, string externalName, ExportProjectionDefinition projection)
	{
		FunctionDefinition forwarder = new()
		{
			SourceSyntax = projection.SourceSyntax,
			Name = externalName,
			Symbol = externalName,
			Export = "export",
			CallSpec = target.CallSpec,
			ReturnType = CloneProjectionTypeReference(target.ReturnType),
			ResolvedType = target.ResolvedType,
			IteratorKind = target.IteratorKind,
			IsAsync = target.IsAsync,
			Provenance = new NodeProvenance(projection.SourceSyntax, target.Symbol, $"export projection for '{target.Name}'"),
			GeneratedInfo = new GeneratedDeclarationInfo(GeneratedDeclarationCategory.None, $"export projection for '{target.Name}'", target)
		};

		foreach (GenericParameter parameter in target.GenericParameters)
			forwarder.GenericParameters.Add(CloneProjectionGenericParameter(parameter));
		foreach (ParameterDefinition parameter in target.Parameters)
			forwarder.Parameters.Add(CloneProjectionParameter(parameter));

		CallExpression call = new()
		{
			SourceSyntax = projection.SourceSyntax,
			Target = new NamedExpression { Name = target.Name, ResolvedType = target.FullCallableName },
			ResolvedType = target.ResolvedType
		};
		foreach (ParameterDefinition parameter in forwarder.Parameters.Where(static parameter => parameter is not SizeOfParameterDefinition and not NameOfParameterDefinition))
		{
			call.Arguments.Add(new ArgumentExpression
			{
				Value = new VariableReferenceExpression
				{
					Variable = parameter,
					ResolvedType = parameter.ResolvedType
				},
				ResolvedType = parameter.ResolvedType
			});
		}

		BlockStatement body = new() { ResolvedType = "void" };
		if (target.ResolvedType == "void")
			body.Statements.Add(new ExpressionStatement { Expression = call, ResolvedType = "void" });
		else
			body.Statements.Add(new ReturnStatement { Expression = call, ResolvedType = "void" });
		forwarder.Body = body;
		return forwarder;
	}

	static GenericParameter CloneProjectionGenericParameter(GenericParameter source)
	{
		return new GenericParameter
		{
			SourceSyntax = source.SourceSyntax,
			Name = source.Name,
			RequiresImplementation = source.RequiresImplementation,
			Constraint = CloneProjectionTypeReference(source.Constraint),
			ResolvedType = source.ResolvedType
		};
	}

	static ParameterDefinition CloneProjectionParameter(ParameterDefinition source)
	{
		ParameterDefinition clone = source switch
		{
			ThisParameterDefinition => new ThisParameterDefinition(),
			WithinParameterDefinition => new WithinParameterDefinition(),
			SizeOfParameterDefinition => new SizeOfParameterDefinition(),
			VTableOfParameterDefinition => new VTableOfParameterDefinition { InterfaceType = CloneProjectionTypeReference(((VTableOfParameterDefinition)source).InterfaceType) },
			NameOfParameterDefinition => new NameOfParameterDefinition(),
			_ => new ParameterDefinition()
		};
		clone.SourceSyntax = source.SourceSyntax;
		clone.Name = source.Name;
		clone.Symbol = source.Symbol;
		clone.Modifier = source.Modifier;
		clone.IsOverloadSelector = source.IsOverloadSelector;
		clone.IsAwaitWith = source.IsAwaitWith;
		clone.LifetimeBinding = source.LifetimeBinding;
		clone.Type = CloneProjectionTypeReference(source.Type);
		clone.DefaultValue = source.DefaultValue;
		clone.ResolvedType = source.ResolvedType;
		return clone;
	}

	static TypeReference? CloneProjectionTypeReference(TypeReference? source)
	{
		if (source is null)
			return null;
		TypeReference clone = source switch
		{
			NamedTypeReference named => new NamedTypeReference { Name = named.Name },
			TypeDefinitionReference type => new TypeDefinitionReference { Name = type.Name, Definition = type.Definition },
			GenericParameterTypeReference generic => new GenericParameterTypeReference { Name = generic.Name, Parameter = generic.Parameter },
			AllocatorTypeReference => new AllocatorTypeReference(),
			ClassTypeReference classType => new ClassTypeReference { Definition = classType.Definition },
			ThisTypeReference => new ThisTypeReference(),
			AttributedTypeReference attributed => new AttributedTypeReference { Attribute = attributed.Attribute, Type = CloneProjectionTypeReference(attributed.Type) },
			GenericTypeReference generic => new GenericTypeReference { Type = CloneProjectionTypeReference(generic.Type) },
			ArrayTypeReference array => new ArrayTypeReference { ElementType = CloneProjectionTypeReference(array.ElementType) },
			FixedArrayTypeReference fixedArray => new FixedArrayTypeReference { ElementType = CloneProjectionTypeReference(fixedArray.ElementType), LengthExpression = fixedArray.LengthExpression, Length = fixedArray.Length },
			OptionalTypeReference optional => new OptionalTypeReference { ElementType = CloneProjectionTypeReference(optional.ElementType) },
			PointerTypeReference pointer => new PointerTypeReference { ElementType = CloneProjectionTypeReference(pointer.ElementType) },
			ConstTypeReference constType => new ConstTypeReference { Type = CloneProjectionTypeReference(constType.Type) },
			ConstOfTypeReference constOf => new ConstOfTypeReference { AnchorName = constOf.AnchorName, Anchor = constOf.Anchor, Type = CloneProjectionTypeReference(constOf.Type) },
			VolatileTypeReference volatileType => new VolatileTypeReference { Type = CloneProjectionTypeReference(volatileType.Type) },
			AnyTypeReference => new AnyTypeReference(),
			CopyableTypeReference => new CopyableTypeReference(),
			AutoTypeReference => new AutoTypeReference(),
			PrimitiveTypeReference primitive => new PrimitiveTypeReference { Type = primitive.Type },
			EscapedTypeReference escaped => new EscapedTypeReference { Type = CloneProjectionTypeReference(escaped.Type) },
			ScopedTypeReference scoped => new ScopedTypeReference(),
			UnscopedTypeReference unscoped => new UnscopedTypeReference(),
			CallableTypeReference callable => new CallableTypeReference { Kind = callable.Kind, CallSpec = callable.CallSpec, TargetSpec = callable.TargetSpec, ReturnType = CloneProjectionTypeReference(callable.ReturnType) },
			RawFunctionPointerTypeReference => new RawFunctionPointerTypeReference(),
			TargetTypeSpecTypeReference targetSpec => new TargetTypeSpecTypeReference { Specifier = targetSpec.Specifier, Type = CloneProjectionTypeReference(targetSpec.Type), IsPrefix = targetSpec.IsPrefix },
			IterTypeReference iter => new IterTypeReference { IsAsync = iter.IsAsync, ElementType = CloneProjectionTypeReference(iter.ElementType) },
			GroupedParamsTypeReference grouped => new GroupedParamsTypeReference { StructType = CloneProjectionTypeReference(grouped.StructType) },
			MaterializedStructTypeReference materialized => new MaterializedStructTypeReference { ParamsType = CloneProjectionTypeReference(materialized.ParamsType) },
			ThrownTypeReference thrown => new ThrownTypeReference { Type = CloneProjectionTypeReference(thrown.Type) },
			_ => new NamedTypeReference { Name = source.ResolvedType ?? ErrorType }
		};
		clone.SourceSyntax = source.SourceSyntax;
		clone.ResolvedType = source.ResolvedType;
		clone.LifetimeBinding = source.LifetimeBinding;
		switch (clone, source)
		{
			case (NamedTypeReference cloned, NamedTypeReference original):
				cloned.Qualifiers.AddRange(original.Qualifiers);
				foreach (TypeReference argument in original.TypeArguments)
					cloned.TypeArguments.Add(CloneProjectionTypeReference(argument)!);
				break;
			case (TypeDefinitionReference cloned, TypeDefinitionReference original):
				foreach (TypeReference argument in original.TypeArguments)
					cloned.TypeArguments.Add(CloneProjectionTypeReference(argument)!);
				break;
			case (GenericTypeReference cloned, GenericTypeReference original):
				foreach (TypeReference argument in original.TypeArguments)
					cloned.TypeArguments.Add(CloneProjectionTypeReference(argument)!);
				break;
			case (ScopedTypeReference cloned, ScopedTypeReference original):
				cloned.Anchors.AddRange(original.Anchors);
				cloned.Type = CloneProjectionTypeReference(original.Type);
				break;
			case (UnscopedTypeReference cloned, UnscopedTypeReference original):
				cloned.Anchors.AddRange(original.Anchors);
				cloned.Type = CloneProjectionTypeReference(original.Type);
				break;
			case (CallableTypeReference cloned, CallableTypeReference original):
				foreach (ParameterDefinition parameter in original.Parameters)
					cloned.Parameters.Add(CloneProjectionParameter(parameter));
				break;
			case (IterTypeReference cloned, IterTypeReference original):
				foreach (ParameterDefinition parameter in original.Parameters)
					cloned.Parameters.Add(CloneProjectionParameter(parameter));
				break;
		}
		return clone;
	}
}
