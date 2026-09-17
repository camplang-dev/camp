using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Camp.Compiler;

public static class CampTestHarnessGenerator
{
	public static string Generate(string projectName, IReadOnlyList<CampTestManifestEntry> tests, bool ignoreLeaks = false, CampCoverageMap? coverageMap = null)
	{
		StringBuilder builder = new();
		builder.AppendLine("#include <stdio.h>");
		builder.AppendLine("#include <stdlib.h>");
		builder.AppendLine("#include <string.h>");
		builder.AppendLine("#include <time.h>");
		builder.AppendLine("#include \"" + EscapeCString(projectName + "_private.h") + "\"");
		builder.AppendLine();
		builder.AppendLine("void *__camp_test_malloc(uintptr_t size)");
		builder.AppendLine("{");
		builder.AppendLine("\treturn malloc(size);");
		builder.AppendLine("}");
		builder.AppendLine();
		TestAllocatorShape? trackingAllocator = tests
			.Where(IsRunnable)
			.Select(static test => test.AllocatorShape)
			.FirstOrDefault(static shape => shape is { Trackable: true });
		WriteMemoryTracker(builder, projectName, trackingAllocator, coverageMap is not null);
		WriteFactoryTestRuntime(builder);
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
		builder.AppendLine("\tint tracks_memory;");
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
			builder.AppendLine("\t{ 0, 0, 0, 0, 0, 0, 0, 0, 0 },");
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
		builder.AppendLine("static void camp_record_factory_child_failure(FILE *file, const CampTestCase *test, int index, double duration_ms)");
		builder.AppendLine("{");
		builder.AppendLine("\t/* The parent's own thrown slot never sees a factory-test child failure");
		builder.AppendLine("\t   (it must not propagate), so this reports the parent as failed without");
		builder.AppendLine("\t   going through test->message/sourcefile/sourceline, which assume a real");
		builder.AppendLine("\t   parent-owned failure object. Per-child detail is in the factory debug");
		builder.AppendLine("\t   sidecar until Stage FT.9 adds it to the result JSON/text output. */");
		builder.AppendLine("\tconst char *message = \"one or more factory-test children failed\";");
		builder.AppendLine("\tif (file != 0)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tfprintf(file, \"failed\\t%d\\t%.3f\\t\", index, duration_ms);");
		builder.AppendLine("\t\tcamp_write_event_string(file, message);");
		builder.AppendLine("\t\tfputc('\\t', file);");
		builder.AppendLine("\t\tcamp_write_event_string(file, \"\");");
		builder.AppendLine("\t\tfprintf(file, \"\\t%u\\n\", 0u);");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\t}");
		builder.AppendLine("\tprintf(\"failed: %s\\n\", test->id);");
		builder.AppendLine("\tprintf(\"  %s\\n\", message);");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static void camp_record_memory_failure(FILE *file, const CampTestCase *test, int index, double duration_ms, const CampTestMemorySummary *memory)");
		builder.AppendLine("{");
		builder.AppendLine("\tconst char *kind = memory->kind == 0 ? \"memory-leak\" : memory->kind;");
		builder.AppendLine("\tconst char *message = memory->message == 0 ? \"\" : memory->message;");
		builder.AppendLine("\tconst char *sourcefile = memory->sourcefile == 0 ? \"\" : memory->sourcefile;");
		builder.AppendLine("\tunsigned int sourceline = memory->sourceline;");
		builder.AppendLine("\tif (file != 0)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tfprintf(file, \"failed-memory\\t%d\\t%.3f\\t\", index, duration_ms);");
		builder.AppendLine("\t\tcamp_write_event_string(file, kind);");
		builder.AppendLine("\t\tfputc('\\t', file);");
		builder.AppendLine("\t\tcamp_write_event_string(file, message);");
		builder.AppendLine("\t\tfputc('\\t', file);");
		builder.AppendLine("\t\tcamp_write_event_string(file, sourcefile);");
		builder.AppendLine("\t\tfprintf(file, \"\\t%u\\t%d\\t%llu\\t%d\\t%d\\t%llu\\n\", sourceline, memory->allocation_count, (unsigned long long)memory->allocated_bytes, memory->free_count, memory->live_count, (unsigned long long)memory->live_bytes);");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\t}");
		builder.AppendLine("\tprintf(\"failed: %s\\n\", test->id);");
		builder.AppendLine("\tprintf(\"  at %s:%u %s\\n\", sourcefile, sourceline, message);");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static void camp_record_ignored_leak(FILE *file, const CampTestCase *test, int index, double duration_ms, const CampTestMemorySummary *memory)");
		builder.AppendLine("{");
		builder.AppendLine("\tconst char *message = memory->message == 0 ? \"\" : memory->message;");
		builder.AppendLine("\tconst char *sourcefile = memory->sourcefile == 0 ? \"\" : memory->sourcefile;");
		builder.AppendLine("\tunsigned int sourceline = memory->sourceline;");
		builder.AppendLine("\tif (file != 0)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tfprintf(file, \"passed-leaked\\t%d\\t%.3f\\t\", index, duration_ms);");
		builder.AppendLine("\t\tcamp_write_event_string(file, message);");
		builder.AppendLine("\t\tfputc('\\t', file);");
		builder.AppendLine("\t\tcamp_write_event_string(file, sourcefile);");
		builder.AppendLine("\t\tfprintf(file, \"\\t%u\\t%d\\t%llu\\t%d\\t%d\\t%llu\\n\", sourceline, memory->allocation_count, (unsigned long long)memory->allocated_bytes, memory->free_count, memory->live_count, (unsigned long long)memory->live_bytes);");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\t}");
		builder.AppendLine("\tprintf(\"passed: %s\\n\", test->id);");
		builder.AppendLine("\tprintf(\"  leak: %s:%u %s\\n\", sourcefile, sourceline, message);");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static void camp_record_passed_memory(FILE *file, const CampTestCase *test, int index, double duration_ms, const CampTestMemorySummary *memory)");
		builder.AppendLine("{");
		builder.AppendLine("\tif (file != 0)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tfprintf(file, \"passed-memory\\t%d\\t%.3f\\t%d\\t%llu\\t%d\\t%d\\t%llu\\n\", index, duration_ms, memory->allocation_count, (unsigned long long)memory->allocated_bytes, memory->free_count, memory->live_count, (unsigned long long)memory->live_bytes);");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\t}");
		builder.AppendLine("\tprintf(\"passed: %s\\n\", test->id);");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static double camp_elapsed_ms(clock_t start)");
		builder.AppendLine("{");
		builder.AppendLine("\treturn ((double)(clock() - start) * 1000.0) / (double)CLOCKS_PER_SEC;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static int camp_is_selected(int index, int argc, char **argv)");
		builder.AppendLine("{");
		builder.AppendLine("\tif (argc <= 2)");
		builder.AppendLine("\t\treturn 1;");
		builder.AppendLine("\tfor (int argument = 3; argument < argc; argument++)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tchar *end = 0;");
		builder.AppendLine("\t\tlong requested = strtol(argv[argument], &end, 10);");
		builder.AppendLine("\t\tif (argv[argument][0] != 0 && *end == 0 && requested == index)");
		builder.AppendLine("\t\t\treturn 1;");
		builder.AppendLine("\t}");
		builder.AppendLine("\treturn 0;");
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
		builder.AppendLine("\t\tchar camp_factory_debug_path[4096];");
		builder.AppendLine("\t\tsnprintf(camp_factory_debug_path, sizeof(camp_factory_debug_path), \"%s.factory-debug.tsv\", argv[1]);");
		builder.AppendLine("\t\tcamp_factory_debug_file = fopen(camp_factory_debug_path, \"wb\");");
		builder.AppendLine("\t}");
		builder.AppendLine("\tint failed = 0;");
		builder.AppendLine("\tint selected_index = 0;");
		if (tests.Count == 0)
			builder.AppendLine("\tif (camp_events == 0) printf(\"camp test: no selected tests\\n\");");
		builder.AppendLine("\tfor (int i = 0; i < camp_test_count; i++)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tif (!camp_is_selected(i, argc, argv))");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\tconst CampTestCase *test = &camp_tests[i];");
		builder.AppendLine("\t\tint result_index = selected_index++;");
		builder.AppendLine("\t\tclock_t start = clock();");
		builder.AppendLine("\t\tif (test->skipped)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcamp_record_simple(camp_events, test, result_index, \"skipped\", camp_elapsed_ms(start));");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tif (!test->valid || test->function == 0)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcamp_record_simple(camp_events, test, result_index, \"invalid\", camp_elapsed_ms(start));");
		builder.AppendLine("\t\t\tfailed++;");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tvoid *failure = 0;");
		builder.AppendLine("\t\tcamp_test_memory_reset();");
		builder.AppendLine("\t\tcamp_factory_test_begin_parent();");
		builder.AppendLine("\t\ttest->function(&failure);");
		builder.AppendLine("\t\tint factory_child_failed = camp_factory_test_end_parent();");
		builder.AppendLine("\t\tCampTestMemorySummary memory = camp_test_memory_finish();");
		builder.AppendLine("\t\tdouble duration_ms = camp_elapsed_ms(start);");
		builder.AppendLine("\t\tif (failure != 0)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcamp_record_failure(camp_events, test, result_index, duration_ms, failure);");
		builder.AppendLine("\t\t\tfailed++;");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tif (factory_child_failed)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcamp_record_factory_child_failure(camp_events, test, result_index, duration_ms);");
		builder.AppendLine("\t\t\tfailed++;");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tif (memory.has_error)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcamp_record_memory_failure(camp_events, test, result_index, duration_ms, &memory);");
		builder.AppendLine("\t\t\tfailed++;");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tif (memory.has_leak)");
		builder.AppendLine("\t\t{");
		if (ignoreLeaks)
			builder.AppendLine("\t\t\tcamp_record_ignored_leak(camp_events, test, result_index, duration_ms, &memory);");
		else
		{
			builder.AppendLine("\t\t\tcamp_record_memory_failure(camp_events, test, result_index, duration_ms, &memory);");
			builder.AppendLine("\t\t\tfailed++;");
		}
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tcamp_record_passed_memory(camp_events, test, result_index, duration_ms, &memory);");
		builder.AppendLine("\t}");
		builder.AppendLine("\tif (camp_events != 0)");
		builder.AppendLine("\t\tfclose(camp_events);");
		builder.AppendLine("\tif (camp_factory_debug_file != 0)");
		builder.AppendLine("\t\tfclose(camp_factory_debug_file);");
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
		builder.Append(test.AllocatorShape is { Trackable: true } ? "1" : "0");
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

	// Factory-test runtime (proposal 021, Stage FT.7). This block owns the
	// currently-running-parent and currently-active-factory-call state, nested-
	// misuse detection, skipped-child recording, and ordinal-suffix assignment
	// for duplicate display names. It is always emitted (cheap, and harmless
	// when no @factorytest declarations exist yet): Stage FT.8's lowering emits
	// calls to __camp_test_beginFactory/__camp_test_endFactory/
	// __camp_test_recordFactorySkipped from the project's own compiled C, a
	// separate translation unit from this generated harness file, so these
	// three entry points use external linkage while everything else here stays
	// file-local. Finalized child results are not yet wired into the real
	// result JSON/text output (that is Stage FT.9); for now each finalized
	// child, with its ordinal-adjusted display name applied, is written as a
	// "camp-factory-child\t<outcome>\t<name>" line to stderr so this stage's
	// own tests can observe runtime behavior end to end.
	static void WriteFactoryTestRuntime(StringBuilder builder)
	{
		builder.AppendLine("#define CAMP_FACTORY_PASSED 0");
		builder.AppendLine("#define CAMP_FACTORY_FAILED 1");
		builder.AppendLine("#define CAMP_FACTORY_SKIPPED 2");
		builder.AppendLine("#define CAMP_FACTORY_INVALID 3");
		builder.AppendLine("#define CAMP_FACTORY_NAME_CAPACITY 160");
		builder.AppendLine("#define CAMP_FACTORY_MAX_CHILDREN 512");
		builder.AppendLine("typedef struct CampFactoryChild");
		builder.AppendLine("{");
		builder.AppendLine("\tchar name[CAMP_FACTORY_NAME_CAPACITY];");
		builder.AppendLine("\tint outcome;");
		builder.AppendLine("} CampFactoryChild;");
		builder.AppendLine("static CampFactoryChild camp_factory_children[CAMP_FACTORY_MAX_CHILDREN];");
		builder.AppendLine("static int camp_factory_child_count = 0;");
		builder.AppendLine("static int camp_factory_parent_active = 0;");
		builder.AppendLine("static int camp_factory_call_active = 0;");
		builder.AppendLine("static int camp_factory_active_recorded = 0;");
		builder.AppendLine("static char camp_factory_active_name[CAMP_FACTORY_NAME_CAPACITY];");
		builder.AppendLine("static FILE *camp_factory_debug_file = 0;");
		builder.AppendLine();
		builder.AppendLine("static const char *camp_factory_outcome_word(int outcome)");
		builder.AppendLine("{");
		builder.AppendLine("\tswitch (outcome)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tcase CAMP_FACTORY_PASSED: return \"passed\";");
		builder.AppendLine("\t\tcase CAMP_FACTORY_FAILED: return \"failed\";");
		builder.AppendLine("\t\tcase CAMP_FACTORY_SKIPPED: return \"skipped\";");
		builder.AppendLine("\t\tdefault: return \"invalid\";");
		builder.AppendLine("\t}");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static void camp_factory_copy_name(char *dest, const char *source, uintptr_t source_length)");
		builder.AppendLine("{");
		builder.AppendLine("\tuintptr_t copy_length = source_length;");
		builder.AppendLine("\tif (source == 0)");
		builder.AppendLine("\t\tcopy_length = 0;");
		builder.AppendLine("\telse if (copy_length >= CAMP_FACTORY_NAME_CAPACITY)");
		builder.AppendLine("\t\tcopy_length = CAMP_FACTORY_NAME_CAPACITY - 1;");
		builder.AppendLine("\tif (copy_length > 0)");
		builder.AppendLine("\t\tmemcpy(dest, source, copy_length);");
		builder.AppendLine("\tdest[copy_length] = 0;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static void camp_factory_record_child(const char *name, uintptr_t name_length, int outcome)");
		builder.AppendLine("{");
		builder.AppendLine("\tif (camp_factory_child_count >= CAMP_FACTORY_MAX_CHILDREN)");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\tcamp_factory_copy_name(camp_factory_children[camp_factory_child_count].name, name, name_length);");
		builder.AppendLine("\tcamp_factory_children[camp_factory_child_count].outcome = outcome;");
		builder.AppendLine("\tcamp_factory_child_count++;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static void camp_factory_record_active_invalid(void)");
		builder.AppendLine("{");
		builder.AppendLine("\tcamp_factory_record_child(camp_factory_active_name, strlen(camp_factory_active_name), CAMP_FACTORY_INVALID);");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static int camp_factory_count_matching(const char *name, int limit)");
		builder.AppendLine("{");
		builder.AppendLine("\tint count = 0;");
		builder.AppendLine("\tfor (int index = 0; index < limit; index++)");
		builder.AppendLine("\t\tif (strcmp(camp_factory_children[index].name, name) == 0)");
		builder.AppendLine("\t\t\tcount++;");
		builder.AppendLine("\treturn count;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static int camp_factory_test_end_parent(void)");
		builder.AppendLine("{");
		builder.AppendLine("\tcamp_factory_parent_active = 0;");
		builder.AppendLine("\tint any_child_failed = 0;");
		builder.AppendLine("\tfor (int index = 0; index < camp_factory_child_count; index++)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tif (camp_factory_children[index].outcome == CAMP_FACTORY_FAILED || camp_factory_children[index].outcome == CAMP_FACTORY_INVALID)");
		builder.AppendLine("\t\t\tany_child_failed = 1;");
		builder.AppendLine("\t\tint total = camp_factory_count_matching(camp_factory_children[index].name, camp_factory_child_count);");
		builder.AppendLine("\t\tchar display[CAMP_FACTORY_NAME_CAPACITY + 16];");
		builder.AppendLine("\t\tif (total > 1)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tint ordinal = camp_factory_count_matching(camp_factory_children[index].name, index) + 1;");
		builder.AppendLine("\t\t\tsnprintf(display, sizeof(display), \"%s.%d\", camp_factory_children[index].name, ordinal);");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\telse");
		builder.AppendLine("\t\t\tsnprintf(display, sizeof(display), \"%s\", camp_factory_children[index].name);");
		builder.AppendLine("\t\tif (camp_factory_debug_file != 0)");
		builder.AppendLine("\t\t\tfprintf(camp_factory_debug_file, \"camp-factory-child\\t%s\\t%s\\n\", camp_factory_outcome_word(camp_factory_children[index].outcome), display);");
		builder.AppendLine("\t}");
		builder.AppendLine("\tcamp_factory_child_count = 0;");
		builder.AppendLine("\tcamp_factory_call_active = 0;");
		builder.AppendLine("\tcamp_factory_active_recorded = 0;");
		builder.AppendLine("\treturn any_child_failed;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static void camp_factory_test_begin_parent(void)");
		builder.AppendLine("{");
		builder.AppendLine("\t/* Flush and report anything buffered outside a parent window (e.g. a");
		builder.AppendLine("\t   misuse recorded before any parent test had started) before resetting. */");
		builder.AppendLine("\tcamp_factory_test_end_parent();");
		builder.AppendLine("\tcamp_factory_parent_active = 1;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("int __camp_test_beginFactory(const char *displayName, uintptr_t displayName_length)");
		builder.AppendLine("{");
		builder.AppendLine("\tif (!camp_factory_parent_active)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tcamp_factory_record_child(displayName, displayName_length, CAMP_FACTORY_INVALID);");
		builder.AppendLine("\t\treturn 0;");
		builder.AppendLine("\t}");
		builder.AppendLine("\tif (camp_factory_call_active)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tif (!camp_factory_active_recorded)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcamp_factory_record_active_invalid();");
		builder.AppendLine("\t\t\tcamp_factory_active_recorded = 1;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tcamp_factory_record_child(displayName, displayName_length, CAMP_FACTORY_INVALID);");
		builder.AppendLine("\t\treturn 0;");
		builder.AppendLine("\t}");
		builder.AppendLine("\tcamp_factory_call_active = 1;");
		builder.AppendLine("\tcamp_factory_active_recorded = 0;");
		builder.AppendLine("\tcamp_factory_copy_name(camp_factory_active_name, displayName, displayName_length);");
		builder.AppendLine("\treturn 1;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("void __camp_test_endFactory(int failed)");
		builder.AppendLine("{");
		builder.AppendLine("\tif (!camp_factory_active_recorded)");
		builder.AppendLine("\t\tcamp_factory_record_child(camp_factory_active_name, strlen(camp_factory_active_name), failed ? CAMP_FACTORY_FAILED : CAMP_FACTORY_PASSED);");
		builder.AppendLine("\tcamp_factory_call_active = 0;");
		builder.AppendLine("\tcamp_factory_active_recorded = 0;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("void __camp_test_recordFactorySkipped(const char *displayName, uintptr_t displayName_length)");
		builder.AppendLine("{");
		builder.AppendLine("\tif (!camp_factory_parent_active)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tcamp_factory_record_child(displayName, displayName_length, CAMP_FACTORY_INVALID);");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\t}");
		builder.AppendLine("\tif (camp_factory_call_active)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tif (!camp_factory_active_recorded)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcamp_factory_record_active_invalid();");
		builder.AppendLine("\t\t\tcamp_factory_active_recorded = 1;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tcamp_factory_record_child(displayName, displayName_length, CAMP_FACTORY_INVALID);");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\t}");
		builder.AppendLine("\tcamp_factory_record_child(displayName, displayName_length, CAMP_FACTORY_SKIPPED);");
		builder.AppendLine("}");
		builder.AppendLine();
	}

	static void WriteMemoryTracker(StringBuilder builder, string projectName, TestAllocatorShape? allocator, bool captureCoverageSource)
	{
		builder.AppendLine("typedef struct CampTestAllocation CampTestAllocation;");
		builder.AppendLine("struct CampTestAllocation");
		builder.AppendLine("{");
		builder.AppendLine("\tvoid *ptr;");
		builder.AppendLine("\tuintptr_t size;");
		builder.AppendLine("\tconst char *sourcefile;");
		builder.AppendLine("\tunsigned int sourceline;");
		builder.AppendLine("\tint live;");
		builder.AppendLine("\tCampTestAllocation *next;");
		builder.AppendLine("};");
		builder.AppendLine("typedef struct CampTestMemorySummary CampTestMemorySummary;");
		builder.AppendLine("struct CampTestMemorySummary");
		builder.AppendLine("{");
		builder.AppendLine("\tint has_leak;");
		builder.AppendLine("\tint has_error;");
		builder.AppendLine("\tint allocation_count;");
		builder.AppendLine("\tuintptr_t allocated_bytes;");
		builder.AppendLine("\tint free_count;");
		builder.AppendLine("\tint live_count;");
		builder.AppendLine("\tuintptr_t live_bytes;");
		builder.AppendLine("\tconst char *kind;");
		builder.AppendLine("\tconst char *message;");
		builder.AppendLine("\tconst char *sourcefile;");
		builder.AppendLine("\tunsigned int sourceline;");
		builder.AppendLine("};");
		if (captureCoverageSource)
		{
			builder.AppendLine("extern const char *" + CampCoverageRuntimeSourceGenerator.CurrentFileSymbol(projectName) + "(void);");
			builder.AppendLine("extern unsigned int " + CampCoverageRuntimeSourceGenerator.CurrentLineSymbol(projectName) + "(void);");
		}
		builder.AppendLine("static CampTestAllocation *camp_test_allocations = 0;");
		builder.AppendLine("static int camp_test_memory_error = 0;");
		builder.AppendLine("static const char *camp_test_memory_error_kind = 0;");
		builder.AppendLine("static char camp_test_memory_message[256];");
		builder.AppendLine("static int camp_test_memory_allocation_count = 0;");
		builder.AppendLine("static uintptr_t camp_test_memory_allocated_bytes = 0;");
		builder.AppendLine("static int camp_test_memory_free_count = 0;");
		builder.AppendLine();
		builder.AppendLine("static CampTestAllocation *camp_test_memory_find_live(void *ptr)");
		builder.AppendLine("{");
		builder.AppendLine("\tfor (CampTestAllocation *current = camp_test_allocations; current != 0; current = current->next)");
		builder.AppendLine("\t\tif (current->ptr == ptr && current->live)");
		builder.AppendLine("\t\t\treturn current;");
		builder.AppendLine("\treturn 0;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static CampTestAllocation *camp_test_memory_find_any(void *ptr)");
		builder.AppendLine("{");
		builder.AppendLine("\tfor (CampTestAllocation *current = camp_test_allocations; current != 0; current = current->next)");
		builder.AppendLine("\t\tif (current->ptr == ptr)");
		builder.AppendLine("\t\t\treturn current;");
		builder.AppendLine("\treturn 0;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static void camp_test_memory_set_error(const char *kind, const char *message)");
		builder.AppendLine("{");
		builder.AppendLine("\tif (camp_test_memory_error)");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\tcamp_test_memory_error = 1;");
		builder.AppendLine("\tcamp_test_memory_error_kind = kind;");
		builder.AppendLine("\tsnprintf(camp_test_memory_message, sizeof(camp_test_memory_message), \"%s\", message);");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static void camp_test_memory_track(void *ptr, uintptr_t size)");
		builder.AppendLine("{");
		builder.AppendLine("\tif (ptr == 0)");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\tCampTestAllocation *record = (CampTestAllocation *)malloc(sizeof(CampTestAllocation));");
		builder.AppendLine("\tif (record == 0)");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\trecord->ptr = ptr;");
		builder.AppendLine("\trecord->size = size;");
		if (captureCoverageSource)
		{
			builder.AppendLine("\trecord->sourcefile = " + CampCoverageRuntimeSourceGenerator.CurrentFileSymbol(projectName) + "();");
			builder.AppendLine("\trecord->sourceline = " + CampCoverageRuntimeSourceGenerator.CurrentLineSymbol(projectName) + "();");
		}
		else
		{
			builder.AppendLine("\trecord->sourcefile = \"\";");
			builder.AppendLine("\trecord->sourceline = 0;");
		}
		builder.AppendLine("\trecord->live = 1;");
		builder.AppendLine("\trecord->next = camp_test_allocations;");
		builder.AppendLine("\tcamp_test_allocations = record;");
		builder.AppendLine("\tcamp_test_memory_allocation_count++;");
		builder.AppendLine("\tcamp_test_memory_allocated_bytes += size;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static void camp_test_memory_reset(void)");
		builder.AppendLine("{");
		builder.AppendLine("\tCampTestAllocation *current = camp_test_allocations;");
		builder.AppendLine("\twhile (current != 0)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tCampTestAllocation *next = current->next;");
		builder.AppendLine("\t\tfree(current);");
		builder.AppendLine("\t\tcurrent = next;");
		builder.AppendLine("\t}");
		builder.AppendLine("\tcamp_test_allocations = 0;");
		builder.AppendLine("\tcamp_test_memory_error = 0;");
		builder.AppendLine("\tcamp_test_memory_error_kind = 0;");
		builder.AppendLine("\tcamp_test_memory_message[0] = 0;");
		builder.AppendLine("\tcamp_test_memory_allocation_count = 0;");
		builder.AppendLine("\tcamp_test_memory_allocated_bytes = 0;");
		builder.AppendLine("\tcamp_test_memory_free_count = 0;");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static CampTestMemorySummary camp_test_memory_finish(void)");
		builder.AppendLine("{");
		builder.AppendLine("\tCampTestMemorySummary summary = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0};");
		builder.AppendLine("\tsummary.allocation_count = camp_test_memory_allocation_count;");
		builder.AppendLine("\tsummary.allocated_bytes = camp_test_memory_allocated_bytes;");
		builder.AppendLine("\tsummary.free_count = camp_test_memory_free_count;");
		builder.AppendLine("\tfor (CampTestAllocation *current = camp_test_allocations; current != 0; current = current->next)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tif (!current->live)");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\tsummary.live_count++;");
		builder.AppendLine("\t\tsummary.live_bytes += current->size;");
		builder.AppendLine("\t\tif (summary.sourcefile == 0 || summary.sourcefile[0] == 0)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tsummary.sourcefile = current->sourcefile;");
		builder.AppendLine("\t\t\tsummary.sourceline = current->sourceline;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t}");
		builder.AppendLine("\tif (camp_test_memory_error)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tsummary.has_error = 1;");
		builder.AppendLine("\t\tsummary.kind = camp_test_memory_error_kind;");
		builder.AppendLine("\t\tsummary.message = camp_test_memory_message;");
		builder.AppendLine("\t\treturn summary;");
		builder.AppendLine("\t}");
		builder.AppendLine("\tif (summary.live_count > 0)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tsummary.has_leak = 1;");
		builder.AppendLine("\t\tsummary.kind = \"memory-leak\";");
		if (captureCoverageSource)
			builder.AppendLine("\t\tsnprintf(camp_test_memory_message, sizeof(camp_test_memory_message), \"memory leak: %d allocation%s still live (%llu bytes)\", summary.live_count, summary.live_count == 1 ? \"\" : \"s\", (unsigned long long)summary.live_bytes);");
		else
			builder.AppendLine("\t\tsnprintf(camp_test_memory_message, sizeof(camp_test_memory_message), \"memory leak: %d allocation%s still live (%llu bytes). Run campc cover for allocation source locations.\", summary.live_count, summary.live_count == 1 ? \"\" : \"s\", (unsigned long long)summary.live_bytes);");
		builder.AppendLine("\t\tsummary.message = camp_test_memory_message;");
		builder.AppendLine("\t}");
		builder.AppendLine("\treturn summary;");
		builder.AppendLine("}");
		builder.AppendLine();
		if (allocator is null)
		{
			builder.AppendLine("static void *camp_test_allocator(void)");
			builder.AppendLine("{");
			builder.AppendLine("\treturn 0;");
			builder.AppendLine("}");
			builder.AppendLine();
			return;
		}
		string allocatorType = CTypeName(allocator.Type);
		builder.AppendLine("static const unsigned char camp_test_uninitialized_pattern = 0xA5;");
		builder.AppendLine();
		builder.AppendLine("static void *camp_test_allocator_alloc(" + allocatorType + " **ctx, uintptr_t size)");
		builder.AppendLine("{");
		builder.AppendLine("\t(void)ctx;");
		builder.AppendLine("\tvoid *ptr = malloc(size);");
		builder.AppendLine("\tif (ptr != 0 && size != 0)");
		builder.AppendLine("\t\tmemset(ptr, camp_test_uninitialized_pattern, size);");
		builder.AppendLine("\tcamp_test_memory_track(ptr, size);");
		builder.AppendLine("\treturn ptr;");
		builder.AppendLine("}");
		builder.AppendLine();
		if (allocator.ReallocField is not null)
		{
			builder.AppendLine("static void *camp_test_allocator_realloc(" + allocatorType + " **ctx, void *ptr, uintptr_t new_size)");
			builder.AppendLine("{");
			builder.AppendLine("\t(void)ctx;");
			builder.AppendLine("\tif (ptr == 0)");
			builder.AppendLine("\t\treturn camp_test_allocator_alloc(ctx, new_size);");
			builder.AppendLine("\tCampTestAllocation *record = camp_test_memory_find_live(ptr);");
			builder.AppendLine("\tif (record == 0)");
			builder.AppendLine("\t{");
			builder.AppendLine("\t\tCampTestAllocation *history = camp_test_memory_find_any(ptr);");
			builder.AppendLine("\t\tcamp_test_memory_set_error(\"memory-invalid-realloc\", history == 0 ? \"invalid allocator realloc: pointer was not allocated by the test allocator\" : \"invalid allocator realloc: pointer was already freed\");");
			builder.AppendLine("\t\treturn 0;");
			builder.AppendLine("\t}");
			builder.AppendLine("\tif (new_size == 0)");
			builder.AppendLine("\t{");
			builder.AppendLine("\t\tfree(ptr);");
			builder.AppendLine("\t\trecord->live = 0;");
			builder.AppendLine("\t\tcamp_test_memory_free_count++;");
			builder.AppendLine("\t\treturn 0;");
			builder.AppendLine("\t}");
			builder.AppendLine("\tuintptr_t old_size = record->size;");
			builder.AppendLine("\tvoid *new_ptr = realloc(ptr, new_size);");
			builder.AppendLine("\tif (new_ptr != 0)");
			builder.AppendLine("\t{");
			builder.AppendLine("\t\trecord->ptr = new_ptr;");
			builder.AppendLine("\t\trecord->size = new_size;");
			builder.AppendLine("\t\tif (new_size > old_size)");
			builder.AppendLine("\t\t\tmemset((unsigned char *)new_ptr + old_size, camp_test_uninitialized_pattern, new_size - old_size);");
			builder.AppendLine("\t}");
			builder.AppendLine("\treturn new_ptr;");
			builder.AppendLine("}");
			builder.AppendLine();
		}
		builder.AppendLine("static void camp_test_allocator_free(" + allocatorType + " **ctx, void *ptr)");
		builder.AppendLine("{");
		builder.AppendLine("\t(void)ctx;");
		builder.AppendLine("\tif (ptr == 0)");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\tCampTestAllocation *record = camp_test_memory_find_live(ptr);");
		builder.AppendLine("\tif (record == 0)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tCampTestAllocation *history = camp_test_memory_find_any(ptr);");
		builder.AppendLine("\t\tcamp_test_memory_set_error(\"memory-invalid-free\", history == 0 ? \"invalid allocator free: pointer was not allocated by the test allocator\" : \"invalid allocator free: pointer was already freed\");");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\t}");
		builder.AppendLine("\trecord->live = 0;");
		builder.AppendLine("\tcamp_test_memory_free_count++;");
		builder.AppendLine("\tfree(ptr);");
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static " + allocatorType + " camp_test_allocator_storage = {");
		builder.AppendLine("\t." + CFieldName(allocator.AllocField!) + " = camp_test_allocator_alloc,");
		if (allocator.ReallocField is not null)
			builder.AppendLine("\t." + CFieldName(allocator.ReallocField) + " = camp_test_allocator_realloc,");
		builder.AppendLine("\t." + CFieldName(allocator.FreeField!) + " = camp_test_allocator_free");
		builder.AppendLine("};");
		builder.AppendLine("static " + allocatorType + " *camp_test_allocator_pointer = &camp_test_allocator_storage;");
		builder.AppendLine("static " + allocatorType + " **camp_test_allocator(void)");
		builder.AppendLine("{");
		builder.AppendLine("\treturn &camp_test_allocator_pointer;");
		builder.AppendLine("}");
		builder.AppendLine();
	}

	static void WriteTestWrapper(StringBuilder builder, CampTestManifestEntry test, int tableIndex)
	{
		TestFailureShape shape = test.FailureShape!;
		string index = tableIndex.ToString(CultureInfo.InvariantCulture);
		string typeName = CTypeName(shape.Type);
		string functionName = CName(test.Function!);
		if (test.AllocatorShape is TestAllocatorShape allocatorShape)
		{
			builder.AppendLine("void " + functionName + "(" + CAllocatorParameterType(allocatorShape) + "allocator, " + typeName + " **failure);");
		}
		else
			builder.AppendLine("void " + functionName + "(" + typeName + " **failure);");
		builder.AppendLine("static void camp_test_run_" + index + "(void **failure)");
		builder.AppendLine("{");
		builder.AppendLine("\t" + typeName + " *typed_failure = 0;");
		if (test.AllocatorShape is { Trackable: true })
			builder.AppendLine("\t" + functionName + "(camp_test_allocator(), &typed_failure);");
		else if (test.AllocatorShape is not null)
			builder.AppendLine("\t" + functionName + "(NULL, &typed_failure);");
		else
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

	static string CAllocatorParameterType(TestAllocatorShape shape)
	{
		return CTypeName(shape.Type) + (shape.Type is InterfaceDefinition ? " **" : " *");
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
