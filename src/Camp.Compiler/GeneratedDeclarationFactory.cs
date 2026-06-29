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
		return definition;
	}
}
