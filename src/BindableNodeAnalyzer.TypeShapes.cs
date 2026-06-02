using System;
using System.Collections.Generic;
using System.Globalization;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	enum TypeShapeKind
	{
		Named,
		Pointer,
		Array,
		Optional
	}

	readonly record struct TypeQualifiers(bool IsConst, bool IsVolatile, LifetimeKind Lifetime)
	{
		public static readonly TypeQualifiers None = new(false, false, LifetimeKind.Scoped);
	}

	enum LifetimeKind
	{
		Scoped = 0,
		Unscoped = 1,
		Escaped = 2
	}

	sealed record TypeShape(TypeShapeKind Kind, string Name, TypeShape? Element, TypeQualifiers Qualifiers, string? TargetSpec = null)
	{
		public bool IsPointer => Kind == TypeShapeKind.Pointer;
		public bool IsArray => Kind == TypeShapeKind.Array;
		public bool IsOptional => Kind == TypeShapeKind.Optional;
	}

	bool TryParseTypeShape(string? type, out TypeShape shape)
	{
		TypeShapeParser parser = new(type ?? "");
		if (parser.TryParse(out shape) && parser.IsEnd)
			return true;

		shape = new TypeShape(TypeShapeKind.Named, type ?? ErrorType, null, TypeQualifiers.None);
		return false;
	}

	bool CanImplicitlyConvertShape(TypeShape source, TypeShape target)
	{
		return CanImplicitlyConvertShape(source, target, protectedByConstTarget: false, pointerDepth: 0);
	}

	bool CanImplicitlyConvertShape(TypeShape source, TypeShape target, bool protectedByConstTarget, int pointerDepth)
	{
		if (source.Kind == TypeShapeKind.Named && target.Kind == TypeShapeKind.Named)
		{
			if (!TargetSpecsCanImplicitlyConvert(source.TargetSpec, target.TargetSpec))
				return false;
			if (!QualifiersCanConvert(source.Qualifiers, target.Qualifiers, protectedByConstTarget, pointerDepth))
				return false;
			return source.Name == target.Name
				|| IsNumericType(source.Name) && IsNumericType(target.Name) && NumericRank(source.Name) <= NumericRank(target.Name);
		}

		if (!QualifiersCanConvert(source.Qualifiers, target.Qualifiers, protectedByConstTarget, pointerDepth))
			return false;
		if (!TargetSpecsCanImplicitlyConvert(source.TargetSpec, target.TargetSpec))
			return false;

		if (source.Kind == TypeShapeKind.Pointer
			&& target.Kind == TypeShapeKind.Pointer
			&& target.Element is TypeShape { Kind: TypeShapeKind.Named, Name: "void" })
			return true;

		if (source.Kind == target.Kind)
		{
			if ((source.Kind == TypeShapeKind.Pointer || source.Kind == TypeShapeKind.Array)
				&& source.Element is TypeShape sourceElementForVariance
				&& target.Element is TypeShape targetElementForVariance
				&& IsDerivedClassType(sourceElementForVariance, targetElementForVariance))
			{
				if (source.Kind == TypeShapeKind.Pointer)
					return pointerDepth == 0 || protectedByConstTarget || target.Qualifiers.IsConst;

				return protectedByConstTarget || target.Qualifiers.IsConst;
			}

			bool childProtected = protectedByConstTarget || target.Qualifiers.IsConst;
			int childPointerDepth = source.Kind == TypeShapeKind.Pointer ? pointerDepth + 1 : pointerDepth;
			return source.Element is not null
				&& target.Element is not null
				&& CanImplicitlyConvertShape(source.Element, target.Element, childProtected, childPointerDepth);
		}

		if ((source.Kind == TypeShapeKind.Pointer || source.Kind == TypeShapeKind.Array)
			&& (target.Kind == TypeShapeKind.Pointer || target.Kind == TypeShapeKind.Array)
			&& source.Element is TypeShape sourceElement
			&& target.Element is TypeShape targetElement)
		{
			if (IsDerivedClassType(sourceElement, targetElement))
				return source.Kind == TypeShapeKind.Pointer && target.Kind == TypeShapeKind.Pointer || target.Qualifiers.IsConst;

			return (protectedByConstTarget || target.Qualifiers.IsConst)
				&& CanImplicitlyConvertShape(sourceElement, targetElement, protectedByConstTarget: true, pointerDepth);
		}

		return false;
	}

	bool TargetSpecsCanImplicitlyConvert(string? source, string? target)
	{
		if (source == target)
			return true;
		if (selectedTarget is null)
			return source is null && target is null;
		return selectedTarget.CanWidenTypeSpec(source, target);
	}

	bool TargetSpecsAreExplicitlyCompatible(string? source, string? target)
	{
		if (source == target)
			return true;
		if (selectedTarget is null)
			return source is null && target is null;
		return selectedTarget.AreTypeSpecsCompatible(source, target);
	}

	bool CanExplicitlyConvertTargetSpecShape(TypeShape source, TypeShape target)
	{
		if (!ContainsTargetSpec(source) && !ContainsTargetSpec(target))
			return false;
		return CanExplicitlyConvertTargetSpecShape(source, target, hasTargetSpec: false);
	}

	bool CanExplicitlyConvertTargetSpecShape(TypeShape source, TypeShape target, bool hasTargetSpec)
	{
		if (source.Kind != target.Kind)
			return false;
		if (!TargetSpecsAreExplicitlyCompatible(source.TargetSpec, target.TargetSpec))
			return false;

		hasTargetSpec = hasTargetSpec || source.TargetSpec is not null || target.TargetSpec is not null;
		if (source.Kind == TypeShapeKind.Named)
			return hasTargetSpec && (source.Name == target.Name || IsNumericType(source.Name) && IsNumericType(target.Name));

		return source.Element is not null
			&& target.Element is not null
			&& CanExplicitlyConvertTargetSpecShape(source.Element, target.Element, hasTargetSpec);
	}

	static bool ContainsTargetSpec(TypeShape shape)
	{
		return shape.TargetSpec is not null || shape.Element is not null && ContainsTargetSpec(shape.Element);
	}

	bool CanExplicitlyConvertPointerNaturalInteger(TypeShape source, TypeShape target)
	{
		if (source.Kind == TypeShapeKind.Pointer && IsNaturalIntegerShape(target))
			return GetNaturalIntegerWidth(target) >= GetObjectPointerWidth(source);

		if (IsNaturalIntegerShape(source) && target.Kind == TypeShapeKind.Pointer)
			return GetObjectPointerWidth(target) >= GetNaturalIntegerWidth(source);

		return false;
	}

	bool CanExplicitlyConvertCallableNaturalInteger(string source, string target)
	{
		if (TryGetCallableShape(source, out CallableShape sourceCallable)
			&& TryParseTypeShape(target, out TypeShape targetShape)
			&& IsNaturalIntegerShape(targetShape))
		{
			return GetNaturalIntegerWidth(targetShape) >= GetFunctionPointerWidth(sourceCallable);
		}

		if (TryParseTypeShape(source, out TypeShape sourceShape)
			&& IsNaturalIntegerShape(sourceShape)
			&& TryGetCallableShape(target, out CallableShape targetCallable))
		{
			return GetFunctionPointerWidth(targetCallable) >= GetNaturalIntegerWidth(sourceShape);
		}

		return false;
	}

	bool CanExplicitlyConvertUntypedPointer(TypeShape source, TypeShape target)
	{
		return IsUntypedPointerShape(source) && target.Kind == TypeShapeKind.Pointer
			|| IsUntypedPointerShape(target) && source.Kind == TypeShapeKind.Pointer;
	}

	bool CanExplicitlyConvertUntypedPointerToCallable(TypeShape source, string target)
	{
		return IsUntypedPointerShape(source) && TryGetCallableShape(target, out _);
	}

	bool IsObjectPointerType(string type)
	{
		return TryParseTypeShape(type, out TypeShape shape) && shape.Kind == TypeShapeKind.Pointer;
	}

	bool IsUntypedPointerType(string type)
	{
		return TryParseTypeShape(type, out TypeShape shape) && IsUntypedPointerShape(shape);
	}

	static bool IsUntypedPointerShape(TypeShape shape)
	{
		return shape.Kind == TypeShapeKind.Pointer && shape.Element is TypeShape { Kind: TypeShapeKind.Named, Name: "untyped" };
	}

	static bool IsNaturalIntegerShape(TypeShape shape)
	{
		return shape.Kind == TypeShapeKind.Named && shape.Name is "nint" or "nuint";
	}

	int GetNaturalIntegerWidth(TypeShape shape)
	{
		return selectedTarget?.GetNaturalIntegerWidth(shape.TargetSpec) ?? 32;
	}

	int GetObjectPointerWidth(TypeShape shape)
	{
		return selectedTarget?.GetPointerWidth(shape.TargetSpec, selectedMemoryModel, functionPointer: false) ?? 32;
	}

	int GetFunctionPointerWidth(CallableShape shape)
	{
		string? targetSpec = shape.Spec is not null && selectedTarget?.HasTypeSpec(shape.Spec) == true ? shape.Spec : null;
		return selectedTarget?.GetPointerWidth(targetSpec, selectedMemoryModel, functionPointer: true) ?? 32;
	}

	static bool QualifiersCanConvert(TypeQualifiers source, TypeQualifiers target, bool protectedByConstTarget, int pointerDepth)
	{
		if (source.IsConst && !target.IsConst && !protectedByConstTarget)
			return false;

		if (source.IsVolatile && !target.IsVolatile && !protectedByConstTarget)
			return false;

		if (!protectedByConstTarget && pointerDepth > 1)
		{
			if (!source.IsConst && target.IsConst)
				return false;
			if (!source.IsVolatile && target.IsVolatile)
				return false;
		}

		return source.Lifetime >= target.Lifetime;
	}

	bool IsDerivedClassType(TypeShape source, TypeShape target)
	{
		if (source.Kind != TypeShapeKind.Named || target.Kind != TypeShapeKind.Named || source.Name == target.Name)
			return false;

		if (!typeDefinitions.TryGetValue(BaseTypeName(source.Name), out TypeDefinition? sourceType)
			|| sourceType is not ClassDefinition sourceClass
			|| !typeDefinitions.TryGetValue(BaseTypeName(target.Name), out TypeDefinition? targetType)
			|| targetType is not ClassDefinition targetClass)
			return false;

		for (ClassDefinition? current = GetDirectBaseClass(sourceClass); current is not null; current = GetDirectBaseClass(current))
		{
			if (ReferenceEquals(current, targetClass))
				return true;
		}

		return false;
	}

	string? TryGetArrayElementTypeFromShape(string? type)
	{
		return TryParseTypeShape(type, out TypeShape shape) && shape.Kind == TypeShapeKind.Array
			? TypeShapeParser.Format(shape.Element)
			: null;
	}

	string? TryGetPointerElementTypeFromShape(string? type)
	{
		return TryParseTypeShape(type, out TypeShape shape) && shape.Kind == TypeShapeKind.Pointer
			? TypeShapeParser.Format(shape.Element)
			: null;
	}

	static string StripConstFromShape(string type)
	{
		return new TypeShapeParser(type).TryParse(out TypeShape shape)
			? TypeShapeParser.Format(shape with { Qualifiers = shape.Qualifiers with { IsConst = false } })
			: type.StartsWith("const ", StringComparison.Ordinal) ? type["const ".Length..] : type;
	}

	static bool IsConstQualifiedShape(string? type)
	{
		return new TypeShapeParser(type ?? "").TryParse(out TypeShape shape) && shape.Qualifiers.IsConst;
	}

	static string AddTopLevelConstToType(string type)
	{
		return new TypeShapeParser(type).TryParse(out TypeShape shape)
			? TypeShapeParser.Format(shape with { Qualifiers = shape.Qualifiers with { IsConst = true } })
			: $"const {type}";
	}

	static string StripTopLevelValueQualifiers(string type)
	{
		if (!new TypeShapeParser(type).TryParse(out TypeShape shape))
			return type;

		return shape.Kind == TypeShapeKind.Named
			? TypeShapeParser.Format(shape with { Qualifiers = TypeQualifiers.None })
			: type;
	}

	static bool IsConstReceiverType(string? type)
	{
		if (!new TypeShapeParser(type ?? "").TryParse(out TypeShape shape))
			return false;

		return shape.Kind == TypeShapeKind.Pointer
			? shape.Element?.Qualifiers.IsConst == true
			: shape.Qualifiers.IsConst;
	}

	static string StripTopLevelConstForReceiver(string type)
	{
		if (!new TypeShapeParser(type).TryParse(out TypeShape shape))
			return type;

		return shape.Kind == TypeShapeKind.Pointer
			? TypeShapeParser.Format(shape with { Qualifiers = shape.Qualifiers with { IsConst = false } })
			: type;
	}

	static string AddConstToReceiverInstance(string type)
	{
		if (!new TypeShapeParser(type).TryParse(out TypeShape shape))
			return AddTopLevelConstToType(type);

		if (shape.Kind == TypeShapeKind.Pointer && shape.Element is not null)
			return TypeShapeParser.Format(shape with { Element = shape.Element with { Qualifiers = shape.Element.Qualifiers with { IsConst = true } } });

		return TypeShapeParser.Format(shape with { Qualifiers = shape.Qualifiers with { IsConst = true } });
	}

	static string AddTopLevelVolatileToReceiverInstance(string type)
	{
		if (!new TypeShapeParser(type).TryParse(out TypeShape shape))
			return $"volatile {type}";

		if (shape.Kind == TypeShapeKind.Pointer && shape.Element is not null)
			return TypeShapeParser.Format(shape with { Element = shape.Element with { Qualifiers = shape.Element.Qualifiers with { IsVolatile = true } } });

		return TypeShapeParser.Format(shape with { Qualifiers = shape.Qualifiers with { IsVolatile = true } });
	}

	static string AddTopLevelLifetimeToReceiver(string type, string lifetime)
	{
		if (!new TypeShapeParser(type).TryParse(out TypeShape shape))
			return $"{lifetime} {type}";

		LifetimeKind kind = lifetime switch
		{
			"escaped" => LifetimeKind.Escaped,
			"unscoped" => LifetimeKind.Unscoped,
			_ => LifetimeKind.Scoped
		};

		return TypeShapeParser.Format(shape with { Qualifiers = shape.Qualifiers with { Lifetime = kind } });
	}

	static string BuildExtensionFunctionSymbol(string methodName, string receiverType, FunctionDefinition? function = null)
	{
		if (!new TypeShapeParser(receiverType).TryParse(out TypeShape shape))
			return receiverType + "_" + methodName;

		int arrayCount = 0;
		while (shape.Element is not null)
		{
			if (shape.Kind == TypeShapeKind.Array)
				arrayCount++;
			shape = shape.Element;
		}

		string receiverName = IsGenericReceiverTypeName(shape.Name, function)
			? ""
			: shape.Name;
		for (int i = 0; i < arrayCount; i++)
			receiverName += "Array";

		return string.IsNullOrWhiteSpace(receiverName)
			? methodName
			: receiverName + "_" + methodName;
	}

	static bool IsGenericReceiverTypeName(string name, FunctionDefinition? function)
	{
		if (function is null)
			return false;

		foreach (GenericParameter parameter in function.GenericParameters)
		{
			if (parameter.Name == name)
				return true;
		}

		return false;
	}

	sealed class TypeShapeParser
	{
		readonly string text;
		int index;

		public TypeShapeParser(string text)
		{
			this.text = text;
		}

		public bool IsEnd
		{
			get
			{
				SkipWhitespace();
				return index >= text.Length;
			}
		}

		public bool TryParse(out TypeShape shape)
		{
			SkipWhitespace();
			if (!TryParsePrefix(out shape))
				return false;

			while (true)
			{
				SkipWhitespace();
				if (TryTake("[]"))
					shape = new TypeShape(TypeShapeKind.Array, "", shape, TypeQualifiers.None);
				else if (TryTake("?"))
					shape = new TypeShape(TypeShapeKind.Optional, "", shape, TypeQualifiers.None);
				else if (TryTake("*"))
					shape = new TypeShape(TypeShapeKind.Pointer, "", shape, TypeQualifiers.None);
				else if (TryReadQualifier(out TypeQualifiers qualifier))
					shape = AddQualifier(shape, qualifier);
				else if (TryReadTargetSpec(out string? targetSpec))
					shape = shape with { TargetSpec = targetSpec };
				else
					return true;
			}
		}

		static TypeShape AddQualifier(TypeShape shape, TypeQualifiers qualifier)
		{
			TypeQualifiers existing = shape.Qualifiers;
			LifetimeKind lifetime = qualifier.Lifetime > existing.Lifetime ? qualifier.Lifetime : existing.Lifetime;
			return shape with
			{
				Qualifiers = new TypeQualifiers(
					existing.IsConst || qualifier.IsConst,
					existing.IsVolatile || qualifier.IsVolatile,
					lifetime)
			};
		}

		bool TryParsePrefix(out TypeShape shape)
		{
			TypeQualifiers prefix = TypeQualifiers.None;
			while (TryReadQualifier(out TypeQualifiers qualifier))
			{
				prefix = new TypeQualifiers(
					prefix.IsConst || qualifier.IsConst,
					prefix.IsVolatile || qualifier.IsVolatile,
					qualifier.Lifetime > prefix.Lifetime ? qualifier.Lifetime : prefix.Lifetime);
			}

			SkipWhitespace();
			if (index >= text.Length)
			{
				shape = new TypeShape(TypeShapeKind.Named, ErrorType, null, prefix);
				return false;
			}

			string name = ReadTypeName();
			if (name.Length == 0)
			{
				shape = new TypeShape(TypeShapeKind.Named, ErrorType, null, prefix);
				return false;
			}

			shape = new TypeShape(TypeShapeKind.Named, name, null, prefix);
			return true;
		}

		string ReadTypeName()
		{
			int start = index;
			int genericDepth = 0;
			int parenDepth = 0;
			while (index < text.Length)
			{
				char ch = text[index];
				if (ch == '<')
					genericDepth++;
				else if (ch == '>' && genericDepth > 0)
					genericDepth--;
				else if (ch == '(')
					parenDepth++;
				else if (ch == ')' && parenDepth > 0)
					parenDepth--;
				else if (genericDepth == 0 && parenDepth == 0 && (char.IsWhiteSpace(ch) || ch is '*' or '?' or '['))
					break;

				index++;
			}

			return text[start..index];
		}

		bool TryReadQualifier(out TypeQualifiers qualifier)
		{
			SkipWhitespace();
			int start = index;
			string word = ReadIdentifier();
			qualifier = word switch
			{
				"const" => new TypeQualifiers(true, false, LifetimeKind.Scoped),
				"volatile" => new TypeQualifiers(false, true, LifetimeKind.Scoped),
				"escaped" => new TypeQualifiers(false, false, LifetimeKind.Escaped),
				"unscoped" => new TypeQualifiers(false, false, LifetimeKind.Unscoped),
				"scoped" => new TypeQualifiers(false, false, LifetimeKind.Scoped),
				_ => TypeQualifiers.None
			};

			if (word is "scoped" or "unscoped" && index < text.Length && text[index] == '(')
				SkipBalanced('(', ')');

			if (word is "const" or "volatile" or "escaped" or "unscoped" or "scoped")
				return true;

			index = start;
			return false;
		}

		string ReadIdentifier()
		{
			int start = index;
			while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_' || text[index] == '#'))
				index++;
			return text[start..index];
		}

		void SkipBalanced(char open, char close)
		{
			int depth = 0;
			while (index < text.Length)
			{
				if (text[index] == open)
					depth++;
				else if (text[index] == close && --depth == 0)
				{
					index++;
					return;
				}
				index++;
			}
		}

		bool TryTake(string value)
		{
			SkipWhitespace();
			if (!text.AsSpan(index).StartsWith(value, StringComparison.Ordinal))
				return false;

			index += value.Length;
			return true;
		}

		void SkipWhitespace()
		{
			while (index < text.Length && char.IsWhiteSpace(text[index]))
				index++;
		}

		public static string Format(TypeShape? shape)
		{
			if (shape is null)
				return ErrorType;

			string core = shape.Kind switch
			{
				TypeShapeKind.Pointer => Format(shape.Element) + "*",
				TypeShapeKind.Array => Format(shape.Element) + "[]",
				TypeShapeKind.Optional => Format(shape.Element) + "?",
				_ => shape.Name
			};

			List<string> suffixes = [];
			if (shape.Qualifiers.IsConst)
				suffixes.Add("const");
			if (shape.Qualifiers.IsVolatile)
				suffixes.Add("volatile");
			if (shape.Qualifiers.Lifetime != LifetimeKind.Scoped)
				suffixes.Add(shape.Qualifiers.Lifetime.ToString().ToLower(CultureInfo.InvariantCulture));
			string? targetSpec = shape.TargetSpec;

			if (suffixes.Count == 0 && targetSpec is null)
				return core;

			if (shape.Kind is TypeShapeKind.Pointer or TypeShapeKind.Array or TypeShapeKind.Optional)
			{
				if (targetSpec is not null)
					suffixes.Add(targetSpec);
				return core + " " + string.Join(" ", suffixes);
			}

			string prefix = suffixes.Count == 0 ? "" : string.Join(" ", suffixes) + " ";
			string suffix = targetSpec is null ? "" : " " + targetSpec;
			return prefix + core + suffix;
		}

		bool TryReadTargetSpec(out string? targetSpec)
		{
			SkipWhitespace();
			int start = index;
			string word = ReadIdentifier();
			if (word.StartsWith("_", StringComparison.Ordinal))
			{
				targetSpec = word;
				return true;
			}

			index = start;
			targetSpec = null;
			return false;
		}
	}
}
