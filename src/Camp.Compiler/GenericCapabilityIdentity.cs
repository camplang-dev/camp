namespace Camp.Compiler;

// Source callable slots retain their operand contract until the ABI boundary.
internal static class GenericCapabilityIdentity
{
	public static string? Describe(ParameterDefinition parameter) => parameter switch
	{
		SizeOfParameterDefinition => $"sizeof({Operand(parameter.Type)})",
		NameOfParameterDefinition => $"typenameof({Operand(parameter.Type)})",
		VTableOfParameterDefinition witness => $"vtableof({Operand(parameter.Type)}: {Operand(witness.InterfaceType)})",
		_ => null
	};

	static string Operand(TypeReference? type) => type?.ResolvedType ?? BindableNodeAnalyzer.FormatTypeReference(type);

	public static ParameterDefinition? Parse(string text)
	{
		int open = text.IndexOf('(');
		if (open < 0 || !text.EndsWith(')')) return null;
		string operand = text[(open + 1)..^1];
		ParameterDefinition? slot = text[..open] switch
		{
			"sizeof" => new SizeOfParameterDefinition { ResolvedType = "nuint" },
			"typenameof" => new NameOfParameterDefinition { ResolvedType = "string" },
			"vtableof" => new VTableOfParameterDefinition(),
			_ => null
		};
		if (slot is VTableOfParameterDefinition witness)
		{
			int colon = operand.IndexOf(": ", System.StringComparison.Ordinal);
			if (colon < 0) return null;
			witness.InterfaceType = BindableNodeAnalyzer.TypeReferenceForResolvedName(operand[(colon + 2)..]);
			operand = operand[..colon];
			witness.ResolvedType = new WitnessTypeIdentity(operand, witness.InterfaceType.ResolvedType ?? "#ERROR").ResolvedType;
		}
		if (slot is not null) slot.Type = BindableNodeAnalyzer.TypeReferenceForResolvedName(operand);
		return slot;
	}
}
