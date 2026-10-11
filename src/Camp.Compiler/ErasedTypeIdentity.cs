using System;

namespace Camp.Compiler;

// Only unwrap a complete storage annotation, never a pointer/array to it. This keeps
// the storage identity separate from the type produced when the storage is read.
internal static class ErasedTypeIdentity
{
	public static bool TryGetPayload(string? type, out string payload)
	{
		payload = "";
		if (type is null)
			return false;
		type = type.Trim();
		string qualifiers = "";
		while (type.StartsWith("const ", StringComparison.Ordinal) || type.StartsWith("volatile ", StringComparison.Ordinal)
			|| type.StartsWith("escaped ", StringComparison.Ordinal) || type.StartsWith("scoped ", StringComparison.Ordinal)
			|| type.StartsWith("unscoped ", StringComparison.Ordinal))
		{
			int space = type.IndexOf(' ') + 1;
			qualifiers += type[..space];
			type = type[space..].TrimStart();
		}
		if (!type.StartsWith("void<", StringComparison.Ordinal) || !type.EndsWith('>'))
			return false;
		int depth = 0;
		for (int i = 4; i < type.Length; i++)
		{
			if (type[i] == '<')
				depth++;
			if (type[i] == '>' && --depth == 0 && i != type.Length - 1)
				return false;
		}
		if (depth != 0)
			return false;
		payload = qualifiers + type[5..^1];
		return true;
	}

	public static string ReadType(string type) => TryGetPayload(type, out string payload) || CarrierTypeIdentity.TryGetPayload(type, out payload) ? payload : type;
}

// An integer carrier C<V> is a specified-size value of type V transported as the integer C.
// Like ErasedTypeIdentity, only a complete annotation is recognized, never a pointer or array of it.
internal static class CarrierTypeIdentity
{
	static readonly string[] Names = ["byte", "ushort", "short", "uint", "int", "ulong", "long", "nuint", "nint"];

	public static bool IsCarrierName(string name) => Array.IndexOf(Names, name) >= 0;

	public static bool TryGetCarrier(string? type, out string carrier, out string payload)
	{
		carrier = "";
		payload = "";
		if (type is null)
			return false;
		type = type.Trim();
		string qualifiers = "";
		while (type.StartsWith("const ", StringComparison.Ordinal) || type.StartsWith("volatile ", StringComparison.Ordinal)
			|| type.StartsWith("escaped ", StringComparison.Ordinal) || type.StartsWith("scoped ", StringComparison.Ordinal)
			|| type.StartsWith("unscoped ", StringComparison.Ordinal))
		{
			int space = type.IndexOf(' ') + 1;
			qualifiers += type[..space];
			type = type[space..].TrimStart();
		}
		int open = type.IndexOf('<');
		if (open <= 0 || !type.EndsWith('>') || !IsCarrierName(type[..open]))
			return false;
		int depth = 0;
		for (int i = open; i < type.Length; i++)
		{
			if (type[i] == '<')
				depth++;
			if (type[i] == '>' && --depth == 0 && i != type.Length - 1)
				return false;
		}
		if (depth != 0)
			return false;
		carrier = type[..open];
		payload = qualifiers + type[(open + 1)..^1];
		return true;
	}

	public static bool TryGetPayload(string? type, out string payload) => TryGetCarrier(type, out _, out payload);

	// ABI spelling: every annotation `C<V>` becomes the plain carrier `C`.
	public static string StripAnnotations(string type)
	{
		if (type.IndexOf('<') < 0)
			return type;
		System.Text.StringBuilder builder = new(type.Length);
		for (int i = 0; i < type.Length;)
		{
			if (!(char.IsLetter(type[i]) || type[i] == '_') || i > 0 && (char.IsLetterOrDigit(type[i - 1]) || type[i - 1] == '_'))
			{
				builder.Append(type[i++]);
				continue;
			}
			int end = i;
			while (end < type.Length && (char.IsLetterOrDigit(type[end]) || type[end] == '_'))
				end++;
			string word = type[i..end];
			if (end < type.Length && type[end] == '<' && IsCarrierName(word))
			{
				int depth = 0;
				int close = end;
				for (; close < type.Length; close++)
				{
					if (type[close] == '<') depth++;
					else if (type[close] == '>' && --depth == 0) break;
				}
				if (close < type.Length)
				{
					builder.Append(word);
					i = close + 1;
					continue;
				}
			}
			builder.Append(word);
			i = end;
		}
		return builder.ToString();
	}

	// Erasure wraps a representation parameter as C<T>; when the argument is itself a carrier the
	// argument already is the carried form, so `C<C2<V>>` reads as `C2<V>`.
	public static string CollapseNested(string type)
	{
		if (type.IndexOf('<') < 0)
			return type;
		System.Text.StringBuilder builder = new(type.Length);
		for (int i = 0; i < type.Length;)
		{
			if (!(char.IsLetter(type[i]) || type[i] == '_') || i > 0 && (char.IsLetterOrDigit(type[i - 1]) || type[i - 1] == '_'))
			{
				builder.Append(type[i++]);
				continue;
			}
			int end = i;
			while (end < type.Length && (char.IsLetterOrDigit(type[end]) || type[end] == '_'))
				end++;
			string word = type[i..end];
			if (end < type.Length && type[end] == '<' && IsCarrierName(word))
			{
				int depth = 0;
				int close = end;
				for (; close < type.Length; close++)
				{
					if (type[close] == '<') depth++;
					else if (type[close] == '>' && --depth == 0) break;
				}
				if (close < type.Length)
				{
					string inner = CollapseNested(type[(end + 1)..close]);
					if (TryGetCarrier(inner, out _, out _))
						builder.Append(inner);
					else
						builder.Append(word).Append('<').Append(inner).Append('>');
					i = close + 1;
					continue;
				}
			}
			builder.Append(word);
			i = end;
		}
		return builder.ToString();
	}

	// Name view: every annotation, `void<V>` or `C<V>`, is replaced by the type it annotates.
	public static string UnwrapAnnotations(string type, bool carriers = true)
	{
		if (type.IndexOf('<') < 0)
			return type;
		System.Text.StringBuilder builder = new(type.Length);
		for (int i = 0; i < type.Length;)
		{
			if (!(char.IsLetter(type[i]) || type[i] == '_') || i > 0 && (char.IsLetterOrDigit(type[i - 1]) || type[i - 1] == '_'))
			{
				builder.Append(type[i++]);
				continue;
			}
			int end = i;
			while (end < type.Length && (char.IsLetterOrDigit(type[end]) || type[end] == '_'))
				end++;
			string word = type[i..end];
			if (end < type.Length && type[end] == '<' && (word == "void" || carriers && IsCarrierName(word)))
			{
				int depth = 0;
				int close = end;
				for (; close < type.Length; close++)
				{
					if (type[close] == '<') depth++;
					else if (type[close] == '>' && --depth == 0) break;
				}
				if (close < type.Length)
				{
					builder.Append(UnwrapAnnotations(type[(end + 1)..close], carriers));
					i = close + 1;
					continue;
				}
			}
			builder.Append(word);
			i = end;
		}
		return builder.ToString();
	}
}
