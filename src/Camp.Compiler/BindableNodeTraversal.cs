using System;
using System.Collections.Generic;

namespace Camp.Compiler;

internal enum BindableTraversalOptions
{
	None = 0,
	SkipAttributeConstructorChildren = 1,
	SkipUnsupportedFunctionBodies = 2
}

internal static class BindableNodeTraversal
{
	public static IEnumerable<BindableNode> Enumerate(BindableNode root, HashSet<BindableNode> visited, BindableTraversalOptions options = BindableTraversalOptions.None)
	{
		if (!visited.Add(root))
			yield break;

		yield return root;
		foreach (BindableNode child in Children(root, options))
		foreach (BindableNode node in Enumerate(child, visited, options))
			yield return node;
	}

	public static IEnumerable<BindableNode> Children(BindableNode node, BindableTraversalOptions options = BindableTraversalOptions.None)
	{
		if (options.HasFlag(BindableTraversalOptions.SkipAttributeConstructorChildren) && node is AttributeConstructor)
			yield break;

		foreach (BindableNode child in DirectChildren(node, options))
			yield return child;
	}

	public static void RewriteChildren(BindableNode node, Func<Expression, Expression> rewriteExpression, Func<TypeReference, TypeReference> rewriteTypeReference, HashSet<BindableNode> visited)
	{
		if (!visited.Add(node))
			return;

		foreach (BindableNode child in RewriteDirectChildren(node, rewriteExpression, rewriteTypeReference))
			RewriteChildren(child, rewriteExpression, rewriteTypeReference, visited);
	}

	static IEnumerable<BindableNode> DirectChildren(BindableNode node, BindableTraversalOptions options)
	{
		switch (node)
		{
			case Module module:
				foreach (UsingDeclaration child in module.Usings)
					yield return child;
				foreach (Definition child in module.Definitions)
					yield return child;
				foreach (ExportProjectionDefinition child in module.ExportProjections)
					yield return child;
				break;

			case Definition definition:
				foreach (AttributeConstructor child in definition.Attributes)
					yield return child;
				foreach (BindableNode child in DefinitionChildren(definition, options))
					yield return child;
				break;

			case ExportProjectionDefinition projection:
				if (projection.ExportedDefinition is not null)
					yield return projection.ExportedDefinition;
				foreach (ExportProjectionMember child in projection.Members)
					yield return child;
				foreach (TypeReference child in projection.InterfaceTypes)
					yield return child;
				foreach (InterfaceDefinition child in projection.ProjectedInterfaces)
					yield return child;
				break;

			case ExportProjectionMember member:
				if (member.ExportedDefinition is not null)
					yield return member.ExportedDefinition;
				break;

			case GenericParameter parameter:
				foreach (AttributeConstructor child in parameter.Attributes)
					yield return child;
				if (parameter.Constraint is not null)
					yield return parameter.Constraint;
				break;

			case AttributeConstructor attribute:
				foreach (ArgumentExpression child in attribute.Arguments)
					yield return child;
				break;

			case TypeReference type:
				foreach (BindableNode child in TypeReferenceChildren(type))
					yield return child;
				break;

			case Statement statement:
				foreach (BindableNode child in StatementChildren(statement))
					yield return child;
				break;

			case Expression expression:
				foreach (BindableNode child in ExpressionChildren(expression))
					yield return child;
				break;

			case GroupedExpressionItem item:
				if (item.Expression is not null)
					yield return item.Expression;
				break;

			case InterpolatedStringExpressionSegment segment:
				if (segment.Expression is not null)
					yield return segment.Expression;
				if (segment.Formatter is not null)
					yield return segment.Formatter;
				break;

			case InitializerItem item:
				if (item.Target is not null)
					yield return item.Target;
				if (item.Expression is not null)
					yield return item.Expression;
				break;

			case InitializerTarget target:
				foreach (InitializerTargetPart child in target.Parts)
					yield return child;
				break;

			case InitializerTargetPart part:
				foreach (ArgumentExpression child in part.Arguments)
					yield return child;
				break;

			case LambdaParameter:
				break;

			case DeclarationTarget target:
				if (target.Type is not null)
					yield return target.Type;
				break;

			case ForStatementCondition condition:
				if (condition.Declaration is not null)
					yield return condition.Declaration;
				foreach (Expression? child in condition.Clauses)
					if (child is not null)
						yield return child;
				break;
		}
	}

	static IEnumerable<BindableNode> DefinitionChildren(Definition definition, BindableTraversalOptions options)
	{
		if (definition.OutOfScopeOwnerType is not null)
			yield return definition.OutOfScopeOwnerType;

		switch (definition)
		{
			case TypeDefinition type:
				foreach (GenericParameter child in type.GenericParameters)
					yield return child;
				foreach (BindableNode child in TypeDefinitionChildren(type))
					yield return child;
				break;

			case AliasDefinition:
				break;

			case VariableDefinition variable:
				if (variable.Type is not null)
					yield return variable.Type;
				if (variable.InitialValue is not null)
					yield return variable.InitialValue;
				break;

			case FunctionDefinition function:
				if (function.AsyncResumerParameter is not null)
					yield return function.AsyncResumerParameter;
				if (function.ReturnType is not null)
					yield return function.ReturnType;
				if (function.CallableAscriptionType is not null)
					yield return function.CallableAscriptionType;
				if (function.EffectiveThisParameter is not null)
					yield return function.EffectiveThisParameter;
				if (function.AbiThisType is not null)
					yield return function.AbiThisType;
				if (function.ImplementationThisType is not null)
					yield return function.ImplementationThisType;
				if (function.InterfaceSlotInitializer is not null)
					yield return function.InterfaceSlotInitializer;
				foreach (GenericParameter child in function.GenericParameters)
					yield return child;
				foreach (ParameterDefinition child in function.Parameters)
					yield return child;
				foreach (UnaryExpression child in function.AwaitSites)
					yield return child;
				if (!options.HasFlag(BindableTraversalOptions.SkipUnsupportedFunctionBodies) || !UnsupportedAvailability.IsUnsupported(function))
					if (function.Body is not null)
						yield return function.Body;
				break;

			case ParameterDefinition parameter:
				if (parameter.Type is not null)
					yield return parameter.Type;
				if (parameter.DefaultValue is not null)
					yield return parameter.DefaultValue;
				if (parameter is VTableOfParameterDefinition vtableOf && vtableOf.InterfaceType is not null)
					yield return vtableOf.InterfaceType;
				break;

			case FieldDefinition field:
				if (field.Type is not null)
					yield return field.Type;
				if (field.InitialValue is not null)
					yield return field.InitialValue;
				break;
		}
	}

	static IEnumerable<BindableNode> TypeDefinitionChildren(TypeDefinition type)
	{
		switch (type)
		{
			case ClassDefinition classDefinition:
				if (classDefinition.ShadowDataType is not null)
					yield return classDefinition.ShadowDataType;
				if (classDefinition.GetShadowHook is not null)
					yield return classDefinition.GetShadowHook;
				if (classDefinition.SetShadowHook is not null)
					yield return classDefinition.SetShadowHook;
				foreach (TypeReference child in classDefinition.ExportProjectionInterfaceBaseTypes)
					yield return child;
				foreach (TypeReference child in classDefinition.ExportProjectionBaseTypes)
					yield return child;
				foreach (TypeReference child in classDefinition.BaseTypes)
					yield return child;
				foreach (TypeReference child in classDefinition.LoweredInterfaceBaseTypes)
					yield return child;
				foreach (FieldDefinition child in classDefinition.Fields)
					yield return child;
				foreach (FunctionDefinition child in classDefinition.Functions)
					yield return child;
				break;

			case StructDefinition structDefinition:
				if (structDefinition.SourceInterface is not null)
					yield return structDefinition.SourceInterface;
				foreach (TypeReference child in structDefinition.BaseTypes)
					yield return child;
				foreach (TypeReference child in structDefinition.LoweredInterfaceBaseTypes)
					yield return child;
				foreach (FieldDefinition child in structDefinition.Fields)
					yield return child;
				foreach (FunctionDefinition child in structDefinition.Functions)
					yield return child;
				break;

			case InterfaceDefinition interfaceDefinition:
				foreach (TypeReference child in interfaceDefinition.BaseTypes)
					yield return child;
				foreach (FunctionDefinition child in interfaceDefinition.Functions)
					yield return child;
				break;

			case EnumDefinition enumDefinition:
				if (enumDefinition.UnderlyingType is not null)
					yield return enumDefinition.UnderlyingType;
				foreach (VariableDefinition child in enumDefinition.Values)
					yield return child;
				foreach (FunctionDefinition child in enumDefinition.Functions)
					yield return child;
				break;

			case NewtypeDefinition newtypeDefinition:
				if (newtypeDefinition.UnderlyingType is not null)
					yield return newtypeDefinition.UnderlyingType;
				foreach (ParameterDefinition child in newtypeDefinition.Parameters)
					yield return child;
				foreach (FieldDefinition child in newtypeDefinition.Fields)
					yield return child;
				foreach (FunctionDefinition child in newtypeDefinition.Functions)
					yield return child;
				break;

			case ParamsDefinition paramsDefinition:
				if (paramsDefinition.UnderlyingType is not null)
					yield return paramsDefinition.UnderlyingType;
				foreach (ParameterDefinition child in paramsDefinition.Components)
					yield return child;
				foreach (FunctionDefinition child in paramsDefinition.Functions)
					yield return child;
				break;
		}
	}

	static IEnumerable<BindableNode> TypeReferenceChildren(TypeReference type)
	{
		switch (type)
		{
			case NamedTypeReference named:
				foreach (TypeReference child in named.TypeArguments)
					yield return child;
				break;
			case TypeDefinitionReference reference:
				foreach (TypeReference child in reference.TypeArguments)
					yield return child;
				break;
			case AttributedTypeReference attributed:
				if (attributed.Attribute is not null)
					yield return attributed.Attribute;
				if (attributed.Type is not null)
					yield return attributed.Type;
				break;
			case GenericTypeReference generic:
				if (generic.Type is not null)
					yield return generic.Type;
				foreach (TypeReference child in generic.TypeArguments)
					yield return child;
				break;
			case ArrayTypeReference array:
				if (array.ElementType is not null)
					yield return array.ElementType;
				break;
			case FixedArrayTypeReference fixedArray:
				if (fixedArray.ElementType is not null)
					yield return fixedArray.ElementType;
				if (fixedArray.LengthExpression is not null)
					yield return fixedArray.LengthExpression;
				break;
			case OptionalTypeReference optional:
				if (optional.ElementType is not null)
					yield return optional.ElementType;
				break;
			case PointerTypeReference pointer:
				if (pointer.ElementType is not null)
					yield return pointer.ElementType;
				break;
			case ConstTypeReference constType:
				if (constType.Type is not null)
					yield return constType.Type;
				break;
			case ConstOfTypeReference constOf:
				if (constOf.Type is not null)
					yield return constOf.Type;
				break;
			case VolatileTypeReference volatileType:
				if (volatileType.Type is not null)
					yield return volatileType.Type;
				break;
			case EscapedTypeReference escaped:
				if (escaped.Type is not null)
					yield return escaped.Type;
				break;
			case ScopedTypeReference scoped:
				if (scoped.Type is not null)
					yield return scoped.Type;
				break;
			case UnscopedTypeReference unscoped:
				if (unscoped.Type is not null)
					yield return unscoped.Type;
				break;
			case CallableTypeReference callable:
				if (callable.ReturnType is not null)
					yield return callable.ReturnType;
				foreach (ParameterDefinition child in callable.Parameters)
					yield return child;
				break;
			case TargetTypeSpecTypeReference target:
				if (target.Type is not null)
					yield return target.Type;
				break;
			case IterTypeReference iter:
				if (iter.ElementType is not null)
					yield return iter.ElementType;
				foreach (ParameterDefinition child in iter.Parameters)
					yield return child;
				break;
			case GroupedParamsTypeReference grouped:
				if (grouped.StructType is not null)
					yield return grouped.StructType;
				break;
			case MaterializedStructTypeReference materialized:
				if (materialized.ParamsType is not null)
					yield return materialized.ParamsType;
				break;
			case ThrownTypeReference thrown:
				if (thrown.Type is not null)
					yield return thrown.Type;
				break;
		}
	}

	static IEnumerable<BindableNode> StatementChildren(Statement statement)
	{
		switch (statement)
		{
			case BlockStatement block:
				foreach (Statement child in block.Statements)
					yield return child;
				break;
			case ExpressionStatement expression:
				if (expression.Expression is not null)
					yield return expression.Expression;
				break;
			case LiteralCopyStatement copy:
				if (copy.Buffer is not null)
					yield return copy.Buffer;
				if (copy.Offset is not null)
					yield return copy.Offset;
				break;
			case DeclarationStatement declaration:
				yield return declaration.Target;
				if (declaration.InitialValue is not null)
					yield return declaration.InitialValue;
				break;
			case IfStatement ifStatement:
				if (ifStatement.Condition is not null)
					yield return ifStatement.Condition;
				if (ifStatement.Body is not null)
					yield return ifStatement.Body;
				if (ifStatement.ElseBody is not null)
					yield return ifStatement.ElseBody;
				break;
			case WhileStatement whileStatement:
				if (whileStatement.Condition is not null)
					yield return whileStatement.Condition;
				if (whileStatement.Body is not null)
					yield return whileStatement.Body;
				break;
			case DoWhileStatement doWhile:
				if (doWhile.Body is not null)
					yield return doWhile.Body;
				if (doWhile.Condition is not null)
					yield return doWhile.Condition;
				break;
			case ForStatement forStatement:
				yield return forStatement.Condition;
				if (forStatement.Body is not null)
					yield return forStatement.Body;
				break;
			case ForeachStatement foreachStatement:
				yield return foreachStatement.Target;
				if (foreachStatement.Source is not null)
					yield return foreachStatement.Source;
				if (foreachStatement.Body is not null)
					yield return foreachStatement.Body;
				break;
			case SwitchStatement switchStatement:
				if (switchStatement.Expression is not null)
					yield return switchStatement.Expression;
				foreach (Statement child in switchStatement.Statements)
					yield return child;
				break;
			case CaseStatement caseStatement:
				if (caseStatement.Expression is not null)
					yield return caseStatement.Expression;
				break;
			case ReturnStatement returnStatement:
				if (returnStatement.Expression is not null)
					yield return returnStatement.Expression;
				break;
			case YieldStatement yieldStatement:
				if (yieldStatement.Expression is not null)
					yield return yieldStatement.Expression;
				break;
			case DeleteStatement delete:
				if (delete.Expression is not null)
					yield return delete.Expression;
				break;
			case TryStatement tryStatement:
				if (tryStatement.Body is not null)
					yield return tryStatement.Body;
				foreach (CatchStatement child in tryStatement.Catches)
					yield return child;
				if (tryStatement.Finally is not null)
					yield return tryStatement.Finally;
				break;
			case CatchStatement catchStatement:
				yield return catchStatement.Target;
				if (catchStatement.Body is not null)
					yield return catchStatement.Body;
				break;
			case FinallyStatement finallyStatement:
				if (finallyStatement.Body is not null)
					yield return finallyStatement.Body;
				break;
			case WithinStatement within:
				if (within.Allocator is not null)
					yield return within.Allocator;
				if (within.Body is not null)
					yield return within.Body;
				break;
		}
	}

	static IEnumerable<BindableNode> ExpressionChildren(Expression expression)
	{
		switch (expression)
		{
			case TypeReferenceExpression type:
				if (type.Type is not null)
					yield return type.Type;
				break;
			case InterpolatedStringExpression interpolation:
				foreach (InterpolatedStringSegment child in interpolation.Segments)
					yield return child;
				break;
			case DefaultExpression defaultExpression:
				if (defaultExpression.Type is not null)
					yield return defaultExpression.Type;
				break;
			case GroupedExpression grouped:
				foreach (GroupedExpressionItem child in grouped.Items)
					yield return child;
				break;
			case ArrayExpression array:
				foreach (Expression child in array.Elements)
					yield return child;
				break;
			case InitializerExpression initializer:
				foreach (InitializerItem child in initializer.Items)
					yield return child;
				break;
			case ParenthesizedExpression parenthesized:
				if (parenthesized.Expression is not null)
					yield return parenthesized.Expression;
				break;
			case CastExpression cast:
				if (cast.Type is not null)
					yield return cast.Type;
				if (cast.Expression is not null)
					yield return cast.Expression;
				break;
			case ConstructionExpression construction:
				if (construction.Type is not null)
					yield return construction.Type;
				foreach (ArgumentExpression child in construction.Arguments)
					yield return child;
				if (construction.ElementCount is not null)
					yield return construction.ElementCount;
				if (construction.Initializer is not null)
					yield return construction.Initializer;
				break;
			case StackAllocExpression stackAlloc:
				if (stackAlloc.Size is not null)
					yield return stackAlloc.Size;
				break;
			case WithinExpression within:
				if (within.Context is not null)
					yield return within.Context;
				if (within.Expression is not null)
					yield return within.Expression;
				break;
			case SizeOfExpression sizeOf:
				if (sizeOf.Type is not null)
					yield return sizeOf.Type;
				break;
			case VTableOfExpression vtableOf:
				if (vtableOf.Type is not null)
					yield return vtableOf.Type;
				if (vtableOf.InterfaceType is not null)
					yield return vtableOf.InterfaceType;
				break;
			case LambdaExpression lambda:
				foreach (LambdaParameter child in lambda.Parameters)
					yield return child;
				if (lambda.Body is not null)
					yield return lambda.Body;
				break;
			case ArgumentExpression argument:
				if (argument.Type is not null)
					yield return argument.Type;
				if (argument.Target is not null)
					yield return argument.Target;
				if (argument.Value is not null)
					yield return argument.Value;
				break;
			case CallExpression call:
				if (call.Target is not null)
					yield return call.Target;
				foreach (TypeReference child in call.TypeArguments)
					yield return child;
				foreach (ArgumentExpression child in call.Arguments)
					yield return child;
				break;
			case IndexExpression index:
				if (index.Target is not null)
					yield return index.Target;
				foreach (ArgumentExpression child in index.Arguments)
					yield return child;
				break;
			case MemberExpression member:
				if (member.Target is not null)
					yield return member.Target;
				break;
			case MemberReferenceExpression member:
				if (member.Target is not null)
					yield return member.Target;
				break;
			case NamelessIndexerExpression indexer:
				if (indexer.Target is not null)
					yield return indexer.Target;
				foreach (ArgumentExpression child in indexer.Arguments)
					yield return child;
				break;
			case UnaryExpression unary:
				if (unary.Operand is not null)
					yield return unary.Operand;
				if (unary.Context is not null)
					yield return unary.Context;
				break;
			case PostfixUpdateExpression postfix:
				if (postfix.Expression is not null)
					yield return postfix.Expression;
				break;
			case FinallyCleanupExpression cleanup:
				if (cleanup.Expression is not null)
					yield return cleanup.Expression;
				foreach (ArgumentExpression child in cleanup.Arguments)
					yield return child;
				if (cleanup.CleanupCall is not null)
					yield return cleanup.CleanupCall;
				break;
			case BinaryExpression binary:
				if (binary.Left is not null)
					yield return binary.Left;
				if (binary.Right is not null)
					yield return binary.Right;
				break;
			case AssignmentExpression assignment:
				if (assignment.Target is not null)
					yield return assignment.Target;
				if (assignment.Value is not null)
					yield return assignment.Value;
				break;
			case ConditionalExpression conditional:
				if (conditional.Condition is not null)
					yield return conditional.Condition;
				if (conditional.WhenTrue is not null)
					yield return conditional.WhenTrue;
				if (conditional.WhenFalse is not null)
					yield return conditional.WhenFalse;
				break;
			case RangeExpression range:
				if (range.Start is not null)
					yield return range.Start;
				if (range.End is not null)
					yield return range.End;
				break;
		}
	}

	static IEnumerable<BindableNode> RewriteDirectChildren(BindableNode node, Func<Expression, Expression> rewriteExpression, Func<TypeReference, TypeReference> rewriteTypeReference)
	{
		TypeReference? Type(TypeReference? value)
		{
			return value is null ? null : rewriteTypeReference(value);
		}

		Expression? Expression(Expression? value)
		{
			return value is null ? null : rewriteExpression(value);
		}

		void TypeList(List<TypeReference> list)
		{
			for (int i = 0; i < list.Count; i++)
				list[i] = rewriteTypeReference(list[i]);
		}

		void ExpressionList(List<Expression> list)
		{
			for (int i = 0; i < list.Count; i++)
				list[i] = rewriteExpression(list[i]);
		}

		if (node is Definition definition)
			definition.OutOfScopeOwnerType = Type(definition.OutOfScopeOwnerType);

		switch (node)
		{
			case GenericParameter parameter:
				parameter.Constraint = Type(parameter.Constraint);
				break;
			case VariableDefinition variable:
				variable.Type = Type(variable.Type);
				variable.InitialValue = Expression(variable.InitialValue);
				break;
			case FunctionDefinition function:
				if (function.AsyncResumerParameter is not null)
					function.AsyncResumerParameter.Type = Type(function.AsyncResumerParameter.Type);
				function.ReturnType = Type(function.ReturnType);
				function.CallableAscriptionType = Type(function.CallableAscriptionType);
				if (function.EffectiveThisParameter is not null)
					function.EffectiveThisParameter.Type = Type(function.EffectiveThisParameter.Type);
				function.AbiThisType = Type(function.AbiThisType);
				function.ImplementationThisType = Type(function.ImplementationThisType);
				function.InterfaceSlotInitializer = Expression(function.InterfaceSlotInitializer);
				break;
			case ParameterDefinition parameter:
				parameter.Type = Type(parameter.Type);
				parameter.DefaultValue = Expression(parameter.DefaultValue);
				if (parameter is VTableOfParameterDefinition vtableParameter)
					vtableParameter.InterfaceType = Type(vtableParameter.InterfaceType);
				break;
			case FieldDefinition field:
				field.Type = Type(field.Type);
				field.InitialValue = Expression(field.InitialValue);
				break;
			case NamedTypeReference named:
				TypeList(named.TypeArguments);
				break;
			case TypeDefinitionReference reference:
				TypeList(reference.TypeArguments);
				break;
			case AttributedTypeReference attributed:
				attributed.Type = Type(attributed.Type);
				break;
			case GenericTypeReference generic:
				generic.Type = Type(generic.Type);
				TypeList(generic.TypeArguments);
				break;
			case ArrayTypeReference array:
				array.ElementType = Type(array.ElementType);
				break;
			case FixedArrayTypeReference fixedArray:
				fixedArray.ElementType = Type(fixedArray.ElementType);
				fixedArray.LengthExpression = Expression(fixedArray.LengthExpression);
				break;
			case OptionalTypeReference optional:
				optional.ElementType = Type(optional.ElementType);
				break;
			case PointerTypeReference pointer:
				pointer.ElementType = Type(pointer.ElementType);
				break;
			case ConstTypeReference constType:
				constType.Type = Type(constType.Type);
				break;
			case ConstOfTypeReference constOf:
				constOf.Type = Type(constOf.Type);
				break;
			case VolatileTypeReference volatileType:
				volatileType.Type = Type(volatileType.Type);
				break;
			case EscapedTypeReference escaped:
				escaped.Type = Type(escaped.Type);
				break;
			case ScopedTypeReference scoped:
				scoped.Type = Type(scoped.Type);
				break;
			case UnscopedTypeReference unscoped:
				unscoped.Type = Type(unscoped.Type);
				break;
			case CallableTypeReference callable:
				callable.ReturnType = Type(callable.ReturnType);
				break;
			case TargetTypeSpecTypeReference target:
				target.Type = Type(target.Type);
				break;
			case IterTypeReference iter:
				iter.ElementType = Type(iter.ElementType);
				break;
			case GroupedParamsTypeReference grouped:
				grouped.StructType = Type(grouped.StructType);
				break;
			case MaterializedStructTypeReference materialized:
				materialized.ParamsType = Type(materialized.ParamsType);
				break;
			case ThrownTypeReference thrown:
				thrown.Type = Type(thrown.Type);
				break;
			case ExpressionStatement expression:
				expression.Expression = Expression(expression.Expression);
				break;
			case LiteralCopyStatement copy:
				copy.Buffer = Expression(copy.Buffer);
				copy.Offset = Expression(copy.Offset);
				break;
			case DeclarationStatement declaration:
				declaration.InitialValue = Expression(declaration.InitialValue);
				break;
			case IfStatement ifStatement:
				ifStatement.Condition = Expression(ifStatement.Condition);
				break;
			case WhileStatement whileStatement:
				whileStatement.Condition = Expression(whileStatement.Condition);
				break;
			case DoWhileStatement doWhile:
				doWhile.Condition = Expression(doWhile.Condition);
				break;
			case ForeachStatement foreachStatement:
				foreachStatement.Source = Expression(foreachStatement.Source);
				break;
			case SwitchStatement switchStatement:
				switchStatement.Expression = Expression(switchStatement.Expression);
				break;
			case CaseStatement caseStatement:
				caseStatement.Expression = Expression(caseStatement.Expression);
				break;
			case ReturnStatement returnStatement:
				returnStatement.Expression = Expression(returnStatement.Expression);
				break;
			case YieldStatement yieldStatement:
				yieldStatement.Expression = Expression(yieldStatement.Expression);
				break;
			case DeleteStatement delete:
				delete.Expression = Expression(delete.Expression);
				break;
			case WithinStatement within:
				within.Allocator = Expression(within.Allocator);
				break;
			case DeclarationTarget target:
				target.Type = Type(target.Type);
				break;
			case ForStatementCondition condition:
				for (int i = 0; i < condition.Clauses.Count; i++)
					if (condition.Clauses[i] is Expression clause)
						condition.Clauses[i] = rewriteExpression(clause);
				break;
			case TypeReferenceExpression type:
				type.Type = Type(type.Type);
				break;
			case InterpolatedStringExpressionSegment segment:
				segment.Expression = Expression(segment.Expression);
				segment.Formatter = Expression(segment.Formatter);
				break;
			case DefaultExpression defaultExpression:
				defaultExpression.Type = Type(defaultExpression.Type);
				break;
			case GroupedExpressionItem item:
				item.Expression = Expression(item.Expression);
				break;
			case ArrayExpression array:
				ExpressionList(array.Elements);
				break;
			case InitializerItem item:
				item.Expression = Expression(item.Expression);
				break;
			case ParenthesizedExpression parenthesized:
				parenthesized.Expression = Expression(parenthesized.Expression);
				break;
			case CastExpression cast:
				cast.Type = Type(cast.Type);
				cast.Expression = Expression(cast.Expression);
				break;
			case ConstructionExpression construction:
				construction.Type = Type(construction.Type);
				construction.ElementCount = Expression(construction.ElementCount);
				break;
			case StackAllocExpression stackAlloc:
				stackAlloc.Size = Expression(stackAlloc.Size);
				break;
			case WithinExpression within:
				within.Context = Expression(within.Context);
				within.Expression = Expression(within.Expression);
				break;
			case SizeOfExpression sizeOf:
				sizeOf.Type = Type(sizeOf.Type);
				break;
			case VTableOfExpression vtableOf:
				vtableOf.Type = Type(vtableOf.Type);
				vtableOf.InterfaceType = Type(vtableOf.InterfaceType);
				break;
			case ArgumentExpression argument:
				argument.Type = Type(argument.Type);
				argument.Value = Expression(argument.Value);
				break;
			case CallExpression call:
				call.Target = Expression(call.Target);
				TypeList(call.TypeArguments);
				break;
			case IndexExpression index:
				index.Target = Expression(index.Target);
				break;
			case MemberExpression member:
				member.Target = Expression(member.Target);
				break;
			case MemberReferenceExpression member:
				member.Target = Expression(member.Target);
				break;
			case NamelessIndexerExpression indexer:
				indexer.Target = Expression(indexer.Target);
				break;
			case UnaryExpression unary:
				unary.Operand = Expression(unary.Operand);
				unary.Context = Expression(unary.Context);
				break;
			case PostfixUpdateExpression postfix:
				postfix.Expression = Expression(postfix.Expression);
				break;
			case FinallyCleanupExpression cleanup:
				cleanup.Expression = Expression(cleanup.Expression);
				break;
			case BinaryExpression binary:
				binary.Left = Expression(binary.Left);
				binary.Right = Expression(binary.Right);
				break;
			case AssignmentExpression assignment:
				assignment.Target = Expression(assignment.Target);
				assignment.Value = Expression(assignment.Value);
				break;
			case ConditionalExpression conditional:
				conditional.Condition = Expression(conditional.Condition);
				conditional.WhenTrue = Expression(conditional.WhenTrue);
				conditional.WhenFalse = Expression(conditional.WhenFalse);
				break;
			case RangeExpression range:
				range.Start = Expression(range.Start);
				range.End = Expression(range.End);
				break;
		}

		return Children(node);
	}
}
