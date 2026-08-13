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
		builder.AppendLine("static void camp_record_memory_failure(FILE *file, const CampTestCase *test, int index, double duration_ms, const CampTestMemorySummary *memory)");
		builder.AppendLine("{");
		builder.AppendLine("\tconst char *kind = memory->kind == 0 ? \"memory-leak\" : memory->kind;");
		builder.AppendLine("\tconst char *message = memory->message == 0 ? \"\" : memory->message;");
		builder.AppendLine("\tconst char *sourcefile = memory->sourcefile == 0 ? \"\" : memory->sourcefile;");
		builder.AppendLine("\tunsigned int sourceline = memory->sourceline;");
		builder.AppendLine("\tif (file != 0)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tfprintf(file, \"failed\\t%d\\t%.3f\\t\", index, duration_ms);");
		builder.AppendLine("\t\tcamp_write_event_string(file, kind);");
		builder.AppendLine("\t\tfputc('\\t', file);");
		builder.AppendLine("\t\tcamp_write_event_string(file, message);");
		builder.AppendLine("\t\tfputc('\\t', file);");
		builder.AppendLine("\t\tcamp_write_event_string(file, sourcefile);");
		builder.AppendLine("\t\tfprintf(file, \"\\t%u\\n\", sourceline);");
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
		builder.AppendLine("\t\tfprintf(file, \"\\t%u\\t%llu\\t%d\\n\", sourceline, (unsigned long long)memory->live_bytes, memory->live_count);");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\t}");
		builder.AppendLine("\tprintf(\"passed: %s\\n\", test->id);");
		builder.AppendLine("\tprintf(\"  leak: %s:%u %s\\n\", sourcefile, sourceline, message);");
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
		builder.AppendLine("\t\tcamp_test_memory_reset();");
		builder.AppendLine("\t\ttest->function(&failure);");
		builder.AppendLine("\t\tCampTestMemorySummary memory = camp_test_memory_finish();");
		builder.AppendLine("\t\tdouble duration_ms = camp_elapsed_ms(start);");
		builder.AppendLine("\t\tif (failure != 0)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcamp_record_failure(camp_events, test, i, duration_ms, failure);");
		builder.AppendLine("\t\t\tfailed++;");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tif (memory.has_error)");
		builder.AppendLine("\t\t{");
		builder.AppendLine("\t\t\tcamp_record_memory_failure(camp_events, test, i, duration_ms, &memory);");
		builder.AppendLine("\t\t\tfailed++;");
		builder.AppendLine("\t\t\tcontinue;");
		builder.AppendLine("\t\t}");
		builder.AppendLine("\t\tif (memory.has_leak)");
		builder.AppendLine("\t\t{");
		if (ignoreLeaks)
			builder.AppendLine("\t\t\tcamp_record_ignored_leak(camp_events, test, i, duration_ms, &memory);");
		else
		{
			builder.AppendLine("\t\t\tcamp_record_memory_failure(camp_events, test, i, duration_ms, &memory);");
			builder.AppendLine("\t\t\tfailed++;");
		}
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
		builder.AppendLine();
		builder.AppendLine("static CampTestAllocation *camp_test_memory_find(void *ptr)");
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
		builder.AppendLine("}");
		builder.AppendLine();
		builder.AppendLine("static CampTestMemorySummary camp_test_memory_finish(void)");
		builder.AppendLine("{");
		builder.AppendLine("\tCampTestMemorySummary summary = {0, 0, 0, 0, 0, 0, 0, 0};");
		builder.AppendLine("\tif (camp_test_memory_error)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tsummary.has_error = 1;");
		builder.AppendLine("\t\tsummary.kind = camp_test_memory_error_kind;");
		builder.AppendLine("\t\tsummary.message = camp_test_memory_message;");
		builder.AppendLine("\t\treturn summary;");
		builder.AppendLine("\t}");
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
		builder.AppendLine("static void *camp_test_allocator_alloc(" + allocatorType + " **ctx, uintptr_t size)");
		builder.AppendLine("{");
		builder.AppendLine("\t(void)ctx;");
		builder.AppendLine("\tvoid *ptr = malloc(size);");
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
			builder.AppendLine("\tCampTestAllocation *record = camp_test_memory_find(ptr);");
			builder.AppendLine("\tif (record == 0 || !record->live)");
			builder.AppendLine("\t{");
			builder.AppendLine("\t\tcamp_test_memory_set_error(\"memory-invalid-realloc\", record == 0 ? \"invalid allocator realloc: pointer was not allocated by the test allocator\" : \"invalid allocator realloc: pointer was already freed\");");
			builder.AppendLine("\t\treturn 0;");
			builder.AppendLine("\t}");
			builder.AppendLine("\tif (new_size == 0)");
			builder.AppendLine("\t{");
			builder.AppendLine("\t\tfree(ptr);");
			builder.AppendLine("\t\trecord->live = 0;");
			builder.AppendLine("\t\treturn 0;");
			builder.AppendLine("\t}");
			builder.AppendLine("\tvoid *new_ptr = realloc(ptr, new_size);");
			builder.AppendLine("\tif (new_ptr != 0)");
			builder.AppendLine("\t{");
			builder.AppendLine("\t\trecord->ptr = new_ptr;");
			builder.AppendLine("\t\trecord->size = new_size;");
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
		builder.AppendLine("\tCampTestAllocation *record = camp_test_memory_find(ptr);");
		builder.AppendLine("\tif (record == 0 || !record->live)");
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tcamp_test_memory_set_error(\"memory-invalid-free\", record == 0 ? \"invalid allocator free: pointer was not allocated by the test allocator\" : \"invalid allocator free: pointer was already freed\");");
		builder.AppendLine("\t\treturn;");
		builder.AppendLine("\t}");
		builder.AppendLine("\trecord->live = 0;");
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
