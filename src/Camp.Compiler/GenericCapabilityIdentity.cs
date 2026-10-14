namespace Camp.Compiler;

// Source callable slots retain their operand contract until the ABI boundary.
internal static class GenericCapabilityIdentity
{
	public static string? Describe(ParameterDefinition parameter) => parameter switch
	{
		SizeOfParameterDefinition => $"sizeof({BindableNodeAnalyzer.FormatTypeReference(parameter.Type)})",
		NameOfParameterDefinition => $"typenameof({BindableNodeAnalyzer.FormatTypeReference(parameter.Type)})",
		VTableOfParameterDefinition witness => $"vtableof({BindableNodeAnalyzer.FormatTypeReference(parameter.Type)}: {BindableNodeAnalyzer.FormatTypeReference(witness.InterfaceType)})",
		_ => null
	};

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
