using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Camp.Compiler;

public static class CampTestHarnessGenerator
{
	public static string Generate(string projectName, IReadOnlyList<CampTestManifestEntry> tests)
	{
		StringBuilder builder = new();
		builder.AppendLine("#include <stdio.h>");
		builder.AppendLine("#include <stdlib.h>");
		builder.AppendLine("#include \"" + EscapeCString(projectName + "_private.h") + "\"");
		builder.AppendLine();
		builder.AppendLine("void *__camp_test_malloc(uintptr_t size)");
		builder.AppendLine("{");
		builder.AppendLine("\treturn malloc(size);");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("typedef void (*CampTestFunction)(Assertion **assertion);");
		builder.AppendLine("typedef struct CampTestCase CampTestCase;");
		builder.AppendLine("struct CampTestCase");
		builder.AppendLine("{");
		builder.AppendLine("\tconst char *id;");
		builder.AppendLine("\tconst char *skip_reason;");
		builder.AppendLine("\tint skipped;");
		builder.AppendLine("\tint valid;");
		builder.AppendLine("\tCampTestFunction function;");
		builder.AppendLine("};");
		builder.AppendLine();
		foreach (CampTestManifestEntry test in tests)
			if (!test.Skipped && test.RunnerSignature == "valid" && test.Function is not null)
				builder.AppendLine("void " + CName(test.Function) + "(Assertion **assertion);");
		if (tests.Any(static test => !test.Skipped && test.RunnerSignature == "valid" && test.Function is not null))
			builder.AppendLine();
		builder.AppendLine("static const CampTestCase camp_tests[] =");
		builder.AppendLine("{");
		if (tests.Count == 0)
			builder.AppendLine("\t{ 0, 0, 0, 0, 0 },");
		else
			foreach (CampTestManifestEntry test in tests)
				WriteTestTableEntry(builder, test);
		builder.AppendLine("};");
		builder.AppendLine("static const int camp_test_count = " + tests.Count.ToString(CultureInfo.InvariantCulture) + ";");
		builder.AppendLine();
		builder.AppendLine("int main(void)");
		builder.AppendLine("{");
		builder.AppendLine("\tint failed = 0;");
		if (tests.Count == 0)
			builder.AppendLine("\tprintf(\"camp test: no selected tests\\n\");");
		builder.AppendLine("\tfor (int i = 0; i < camp_test_count; i++)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tconst CampTestCase *test = &camp_tests[i];");
		builder.AppendLine("\t\tif (test->skipped)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tprintf(\"skipped: %s\\n\", test->id);");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tif (!test->valid || test->function == 0)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tprintf(\"invalid: %s\\n\", test->id);");
		builder.AppendLine("\t\t\tfailed++;");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tAssertion *failure = 0;");
		builder.AppendLine("\t\ttest->function(&failure);");
		builder.AppendLine("\t\tif (failure != 0)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tprintf(\"failed: %s\\n\", test->id);");
		builder.AppendLine("\t\t\tprintf(\"  at %s:%u %s\\n\", failure->sourcefile == 0 ? \"\" : failure->sourcefile, failure->sourceline, failure->message == 0 ? \"\" : failure->message);");
		builder.AppendLine("\t\t\tfailed++;");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tprintf(\"passed: %s\\n\", test->id);");
		builder.AppendLine("\t}");
		builder.AppendLine("\treturn failed == 0 ? 0 : 1;");
		builder.AppendLine("}");
		return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	static void WriteTestTableEntry(StringBuilder builder, CampTestManifestEntry test)
	{
		bool valid = !test.Skipped && test.RunnerSignature == "valid" && test.Function is not null;
		builder.Append("\t{ \"");
		builder.Append(EscapeCString(test.Id));
		builder.Append("\", ");
		if (test.SkipReason is null)
			builder.Append("0");
		else
		{
			builder.Append("\"");
			builder.Append(EscapeCString(test.SkipReason));
			builder.Append("\"");
		}
		builder.Append(", ");
		builder.Append(test.Skipped ? "1" : "0");
		builder.Append(", ");
		builder.Append(valid ? "1" : "0");
		builder.Append(", ");
		builder.Append(valid ? CName(test.Function!) : "0");
		builder.AppendLine(" },");
	}

	static string CName(FunctionDefinition function)
	{
		if (function.SymbolOverridden && !string.IsNullOrWhiteSpace(function.Symbol))
			return SanitizeIdentifier(function.Symbol);
		if (!string.IsNullOrWhiteSpace(function.Symbol) && function.Symbol != function.Name)
			return SanitizeIdentifier(function.Symbol);
		return SanitizeIdentifier(string.IsNullOrWhiteSpace(function.Symbol) ? function.Name : function.Symbol);
	}

	static string SanitizeIdentifier(string value)
	{
		StringBuilder builder = new();
		foreach (char ch in value)
			builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
		return builder.Length == 0 ? "camp" : builder.ToString();
	}

	static string EscapeCString(string value)
	{
		StringBuilder builder = new();
		foreach (char c in value)
		{
			builder.Append(c switch
			{
				'\\' => "\\\\",
				'"' => "\\\"",
				'\n' => "\\n",
				'\r' => "\\r",
				'\t' => "\\t",
				_ => c
			});
		}
		return builder.ToString();
	}
}
