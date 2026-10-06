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
		while (type.StartsWith("const ", StringComparison.Ordinal) || type.StartsWith("volatile ", StringComparison.Ordinal))
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

	public static string ReadType(string type) => TryGetPayload(type, out string payload) ? payload : type;
}
