namespace Camp.Compiler;

internal sealed class GeneratedDeclarationFactory
{
	public FunctionDefinition Function(GeneratedDeclarationCategory category, string reason, Definition? source = null)
	{
		return Mark(new FunctionDefinition(), category, reason, source);
	}

	public VariableDefinition Variable(GeneratedDeclarationCategory category, string reason, Definition? source = null)
	{
		return Mark(new VariableDefinition(), category, reason, source);
	}

	public FieldDefinition Field(GeneratedDeclarationCategory category, string reason, Definition? source = null)
	{
		return Mark(new FieldDefinition(), category, reason, source);
	}

	public StructDefinition Struct(GeneratedDeclarationCategory category, string reason, Definition? source = null)
	{
		return Mark(new StructDefinition(), category, reason, source);
	}

	public ClassDefinition Class(GeneratedDeclarationCategory category, string reason, Definition? source = null)
	{
		return Mark(new ClassDefinition(), category, reason, source);
	}

	public T Mark<T>(T definition, GeneratedDeclarationCategory category, string reason, Definition? source = null)
		where T : Definition
	{
		definition.GeneratedInfo = new GeneratedDeclarationInfo(category, reason, source);
		definition.Provenance = new NodeProvenance(source?.SourceSyntax, SourceSymbol(source), reason, category, Visibility(definition, source));
		return definition;
	}

	static string? SourceSymbol(Definition? source)
	{
		if (source is null)
			return null;
		return string.IsNullOrWhiteSpace(source.Symbol) ? source.Name : source.Symbol;
	}

	static string? Visibility(Definition definition, Definition? source)
	{
		if (definition.Export is not null || source?.Export is not null)
			return "export";
		if (definition.Internal is not null || source?.Internal is not null)
			return "internal";
		return null;
	}
}
