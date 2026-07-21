namespace Camp.Compiler;

public enum CompilerCommandMode
{
	Build,
	Run,
	Dump,
	Test,
	Cover
}

public enum DeclarationParticipationMode
{
	Production,
	TestModule
}

public enum CoverageInstrumentationMode
{
	Disabled,
	ProductionSubject
}
