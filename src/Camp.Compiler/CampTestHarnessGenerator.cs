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
		builder.AppendLine("#include <time.h>");
		builder.AppendLine("#include \"" + EscapeCString(projectName + "_private.h") + "\"");
		builder.AppendLine();
		builder.AppendLine("void *__camp_test_malloc(uintptr_t size)");
		builder.AppendLine("{");
		builder.AppendLine("\treturn malloc(size);");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("typedef void (*CampTestFunction)(void **failure);");
		builder.AppendLine("typedef const char *(*CampTestStringField)(void *failure);");
		builder.AppendLine("typedef unsigned int (*CampTestLineField)(void *failure);");
		builder.AppendLine("typedef struct CampTestCase CampTestCase;");
		builder.AppendLine("struct CampTestCase");
		builder.AppendLine("{");
		builder.AppendLine("\tconst char *id;");
		builder.AppendLine("\tconst char *skip_reason;");
		builder.AppendLine("\tint skipped;");
		builder.AppendLine("\tint valid;");
		builder.AppendLine("\tCampTestFunction function;");
		builder.AppendLine("\tCampTestStringField message;");
		builder.AppendLine("\tCampTestStringField sourcefile;");
		builder.AppendLine("\tCampTestLineField sourceline;");
		builder.AppendLine("};");
		builder.AppendLine();
		Dictionary<CampTestManifestEntry, int> wrapperIndexes = [];
		for (int i = 0; i < tests.Count; i++)
		{
			if (IsRunnable(tests[i]))
			{
				wrapperIndexes[tests[i]] = i;
				WriteTestWrapper(builder, tests[i], i);
			}
		}
		if (tests.Any(IsRunnable))
			builder.AppendLine();
		builder.AppendLine("static const CampTestCase camp_tests[] =");
		builder.AppendLine("{");
		if (tests.Count == 0)
			builder.AppendLine("\t{ 0, 0, 0, 0, 0, 0, 0, 0 },");
		else
			foreach (CampTestManifestEntry test in tests)
				WriteTestTableEntry(builder, test, wrapperIndexes);
		builder.AppendLine("};");
		builder.AppendLine("static const int camp_test_count = " + tests.Count.ToString(CultureInfo.InvariantCulture) + ";");
		builder.AppendLine();
		builder.AppendLine("static void camp_write_event_string(FILE *file, const char *text)");
		builder.AppendLine("{");
		builder.AppendLine("\tif (text == 0)");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\tfor (const char *current = text; *current != 0; current++)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tswitch (*current)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcase '\\\\': fputs(\"\\\\\\\\\", file); break;");
		builder.AppendLine("\t\t\tcase '\\n': fputs(\"\\\\n\", file); break;");
		builder.AppendLine("\t\t\tcase '\\r': fputs(\"\\\\r\", file); break;");
		builder.AppendLine("\t\t\tcase '\\t': fputs(\"\\\\t\", file); break;");
		builder.AppendLine("\t\t\tdefault: fputc(*current, file); break;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t}");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static void camp_record_simple(FILE *file, const CampTestCase *test, int index, const char *outcome, double duration_ms)");
		builder.AppendLine("{");
		builder.AppendLine("\tif (file != 0)");
		builder.AppendLine("\t\tfprintf(file, \"%s\\t%d\\t%.3f\\n\", outcome, index, duration_ms);");
		builder.AppendLine("\telse");
		builder.AppendLine("\t\tprintf(\"%s: %s\\n\", outcome, test->id);");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static void camp_record_failure(FILE *file, const CampTestCase *test, int index, double duration_ms, void *failure)");
		builder.AppendLine("{");
		builder.AppendLine("\tconst char *message = test->message == 0 ? \"\" : test->message(failure);");
		builder.AppendLine("\tconst char *sourcefile = test->sourcefile == 0 ? \"\" : test->sourcefile(failure);");
		builder.AppendLine("\tunsigned int sourceline = test->sourceline == 0 ? 0 : test->sourceline(failure);");
		builder.AppendLine("\tif (file != 0)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tfprintf(file, \"failed\\t%d\\t%.3f\\t\", index, duration_ms);");
		builder.AppendLine("\t\tcamp_write_event_string(file, message);");
		builder.AppendLine("\t\tfputc('\\t', file);");
		builder.AppendLine("\t\tcamp_write_event_string(file, sourcefile);");
		builder.AppendLine("\t\tfprintf(file, \"\\t%u\\n\", sourceline);");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\t}");
		builder.AppendLine("\tprintf(\"failed: %s\\n\", test->id);");
		builder.AppendLine("\tprintf(\"  at %s:%u %s\\n\", sourcefile == 0 ? \"\" : sourcefile, sourceline, message == 0 ? \"\" : message);");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static double camp_elapsed_ms(clock_t start)");
		builder.AppendLine("{");
		builder.AppendLine("\treturn ((double)(clock() - start) * 1000.0) / (double)CLOCKS_PER_SEC;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("int main(int argc, char **argv)");
		builder.AppendLine("{");
		builder.AppendLine("\tFILE *camp_events = 0;");
		builder.AppendLine("\tif (argc > 1)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tcamp_events = fopen(argv[1], \"wb\");");
		builder.AppendLine("\t\tif (camp_events == 0)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tfprintf(stderr, \"camp test: could not open result event file\\n\");");
		builder.AppendLine("\t\t\treturn 2;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t}");
		builder.AppendLine("\tint failed = 0;");
		if (tests.Count == 0)
			builder.AppendLine("\tif (camp_events == 0) printf(\"camp test: no selected tests\\n\");");
		builder.AppendLine("\tfor (int i = 0; i < camp_test_count; i++)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tconst CampTestCase *test = &camp_tests[i];");
		builder.AppendLine("\t\tclock_t start = clock();");
		builder.AppendLine("\t\tif (test->skipped)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcamp_record_simple(camp_events, test, i, \"skipped\", camp_elapsed_ms(start));");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tif (!test->valid || test->function == 0)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcamp_record_simple(camp_events, test, i, \"invalid\", camp_elapsed_ms(start));");
		builder.AppendLine("\t\t\tfailed++;");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tvoid *failure = 0;");
		builder.AppendLine("\t\ttest->function(&failure);");
		builder.AppendLine("\t\tdouble duration_ms = camp_elapsed_ms(start);");
		builder.AppendLine("\t\tif (failure != 0)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcamp_record_failure(camp_events, test, i, duration_ms, failure);");
		builder.AppendLine("\t\t\tfailed++;");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tcamp_record_simple(camp_events, test, i, \"passed\", duration_ms);");
		builder.AppendLine("\t}");
		builder.AppendLine("\tif (camp_events != 0)");
		builder.AppendLine("\t\tfclose(camp_events);");
		builder.AppendLine("\treturn failed == 0 ? 0 : 1;");
		builder.AppendLine("}");
		return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	static void WriteTestTableEntry(StringBuilder builder, CampTestManifestEntry test, IReadOnlyDictionary<CampTestManifestEntry, int> wrapperIndexes)
	{
		bool valid = IsRunnable(test);
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
		if (valid)
		{
			int index = wrapperIndexes[test];
			builder.Append("camp_test_run_");
			builder.Append(index.ToString(CultureInfo.InvariantCulture));
			builder.Append(", camp_test_message_");
			builder.Append(index.ToString(CultureInfo.InvariantCulture));
			builder.Append(", camp_test_sourcefile_");
			builder.Append(index.ToString(CultureInfo.InvariantCulture));
			builder.Append(", camp_test_sourceline_");
			builder.Append(index.ToString(CultureInfo.InvariantCulture));
		}
		else
			builder.Append("0, 0, 0, 0");
		builder.AppendLine(" },");
	}

	static bool IsRunnable(CampTestManifestEntry test)
	{
		return !test.Skipped && test.RunnerSignature == "valid" && test.Function is not null && test.FailureShape is not null;
	}

	static void WriteTestWrapper(StringBuilder builder, CampTestManifestEntry test, int tableIndex)
	{
		TestFailureShape shape = test.FailureShape!;
		string index = tableIndex.ToString(CultureInfo.InvariantCulture);
		string typeName = CTypeName(shape.Type);
		string functionName = CName(test.Function!);
		builder.AppendLine("void " + functionName + "(" + typeName + " **failure);");
		builder.AppendLine("static void camp_test_run_" + index + "(void **failure)");
		builder.AppendLine("{");
		builder.AppendLine("\t" + typeName + " *typed_failure = 0;");
		builder.AppendLine("\t" + functionName + "(&typed_failure);");
		builder.AppendLine("\t*failure = typed_failure;");
		builder.AppendLine("}");
		builder.AppendLine("static const char *camp_test_message_" + index + "(void *failure)");
		builder.AppendLine("{");
		builder.AppendLine("\treturn ((" + typeName + " *)failure)->" + CFieldName(shape.MessageField) + ";");
		builder.AppendLine("}");
		builder.AppendLine("static const char *camp_test_sourcefile_" + index + "(void *failure)");
		builder.AppendLine("{");
		builder.AppendLine("\treturn ((" + typeName + " *)failure)->" + CFieldName(shape.SourcefileField) + ";");
		builder.AppendLine("}");
		builder.AppendLine("static unsigned int camp_test_sourceline_" + index + "(void *failure)");
		builder.AppendLine("{");
		builder.AppendLine("\treturn ((" + typeName + " *)failure)->" + CFieldName(shape.SourcelineField) + ";");
		builder.AppendLine("}");
	}

	static string CName(FunctionDefinition function)
	{
		if (function.SymbolOverridden && !string.IsNullOrWhiteSpace(function.Symbol))
			return SanitizeIdentifier(function.Symbol);
		if (!string.IsNullOrWhiteSpace(function.Symbol) && function.Symbol != function.Name)
			return SanitizeIdentifier(function.Symbol);
		return SanitizeIdentifier(string.IsNullOrWhiteSpace(function.Symbol) ? function.Name : function.Symbol);
	}

	static string CTypeName(TypeDefinition type)
	{
		return SanitizeIdentifier(BindableNodeAnalyzer.EffectiveTypeSymbol(type));
	}

	static string CFieldName(string name)
	{
		return SanitizeIdentifier(name);
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
