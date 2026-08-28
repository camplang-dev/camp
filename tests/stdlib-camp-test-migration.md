# Standard Library Camp Test Migration Ledger

This ledger tracks Proposal 020 migration from executable `StdRun` golden tests
to standard-library-owned Camp `@test` coverage.

Status values:

- `pending`: stdlib behavior candidate not migrated yet.
- `partially-replaced`: some behavior has replacement `@test` coverage, but
  the legacy test still contains distinct stdlib behavior.
- `replaced`: replacement `@test` coverage exists and has passed.
- `legacy-active`: keep the existing test active for now.
- `legacy-skipped`: old test intentionally skipped after replacement.
- `kept`: not a stdlib behavior migration target.
- `obsolete`: no longer relevant after current stdlib/API shape.

| Old lane | Old test | Classification | New location | Status | Notes |
| --- | --- | --- | --- | --- | --- |
| `StdRun` | `std_array_functions` | stdlib-behavior | `dev/lib/std/tests/std_array_tests.camp` | replaced | Dense replacement passed; preserves copy, clear, reverse, sort, binary search, copied arrays, and overlapping copy behavior. |
| `StdRun` | `string_functions` | stdlib-behavior | `dev/lib/std/tests/std_string_tests.camp` | replaced | Dense replacement passed; preserves UTF-8 search, compare, trim, case conversion, join, embedded-null, and explicit prepared buffer behavior. |
| `StdRun` | `wstring_functions` | stdlib-behavior | `dev/lib/std/tests/std_wstring_tests.camp` | replaced | Dense replacement passed; preserves wide search, compare, trim, case conversion, join, and UTF-16 behavior. |
| `StdRun` | `astring_functions` | stdlib-behavior | `dev/lib/std/tests/std_astring_tests.camp` | replaced | Dense replacement passed; preserves ANSI search, compare, trim, case conversion, and join behavior. |
| `StdRun` | `strconv_functions` | stdlib-behavior | `dev/lib/std/tests/std_strconv_tests.camp` | replaced | Dense replacement passed; preserves UTF conversion lengths/content and ANSI fallback behavior. |
| `StdRun` | `parse_functions` | stdlib-behavior | `dev/lib/std/tests/std_parse_tests.camp` | replaced | Dense replacement passed; preserves signed, unsigned, long, ulong, and double parse success/failure behavior. |
| `StdRun` | `hashcode_functions` | stdlib-behavior | `dev/lib/std/tests/std_hash_tests.camp` | replaced | Dense replacement passed; preserves primitive, string, case-insensitive, equality, and pointer hash policy behavior. |
| `StdRun` | `character_primitive_helpers` | stdlib-behavior | `dev/lib/std/tests/std_character_tests.camp` | replaced | Dense replacement passed; preserves char-family MIN/MAX, comparisons, hash/equality, and toString behavior. |
| `StdRun` | `math_double_min_max` | stdlib-behavior | `dev/lib/std/tests/std_numerics_tests.camp` | replaced | Dense replacement passed; preserves numeric constants and min/max helpers. |
| `StdRun` | `hashmap_collections` | stdlib-behavior | `dev/lib/std/tests/std_collections_tests.camp` | replaced | Dense replacement passed; preserves HashMap insert/update/remove/clear/growth/capacity/string hash policy/tryGet behavior. |
| `StdRun` | `hashset_collections` | stdlib-behavior | `dev/lib/std/tests/std_collections_tests.camp` | replaced | Dense replacement passed; preserves HashSet add/remove/contains/clear/growth behavior. |
| `StdRun` | `list_collections` | stdlib-behavior | `dev/lib/std/tests/std_collections_tests.camp` | replaced | Dense replacement passed; preserves List add/insert/remove/index/copy/trim/growth behavior. |
| `StdRun` | `list_char_writer` | stdlib-behavior | `dev/lib/std/tests/std_stream_tests.camp` | replaced | Dense replacement passed; preserves List<char> CharWriter integration. |
| `StdRun` | `retained_container_allocators` | stdlib-behavior | `dev/lib/std/tests/std_collections_tests.camp` | replaced | Dense replacement passed; preserves retained allocator lifecycle behavior for List, HashMap, and HashSet. |
| `StdRun` | `reader_helpers` | stdlib-behavior | `dev/lib/std/tests/std_stream_tests.camp` | legacy-active | Uses FileHandle/PAL behavior; migrate with OS/PAL stdlib tests in Stage 4. |
| `StdRun` | `stream_adapters` | stdlib-behavior | `dev/lib/std/tests/std_stream_tests.camp` | legacy-active | Existing black-box test remains active; same-module adapter lowering is not claimed as replaced in Stage 3. |
| `StdRun` | `console_streams` | stdlib-behavior | `dev/lib/std/tests/std_console_tests.camp` | pending | Preserve Console stream adapter smoke behavior. |
| `StdRun` | `console_trimmed_line` | stdlib-behavior | `dev/lib/std/tests/std_console_tests.camp` | pending | Preserve trimmed console line behavior without requiring interactive terminal input. |
| `StdRun` | `environment_filesystem_runtime` | stdlib-behavior | `dev/lib/std/tests/std_io_tests.camp` | pending | Preserve current directory, executable path, environment variables, directory lifecycle, copy/move/delete/size behavior. |
| `StdRun` | `file_errors` | stdlib-behavior | `dev/lib/std/tests/std_io_tests.camp` | pending | Preserve common file error values and failure paths. |
| `StdRun` | `file_handle` | stdlib-behavior | `dev/lib/std/tests/std_io_tests.camp` | pending | Preserve open/read/write/close byte behavior. |
| `StdRun` | `path_lexical_helpers` | stdlib-behavior | `dev/lib/std/tests/std_path_tests.camp` | pending | Preserve lexical path manipulation helpers. |
| `StdRun` | `time_functions` | stdlib-behavior | `dev/lib/std/tests/std_time_tests.camp` | pending | Preserve Date, TimeOfDay, DateTime, OffsetDateTime, Instant, TimeSpan, UtcOffset parse/format/arithmetic behavior. |
| `StdRun` | `timing_functions` | stdlib-behavior | `dev/lib/std/tests/std_timing_tests.camp` | pending | Preserve sleep, sleepAsync, timers, and cancellation smoke behavior. |

Existing `CCompile`, `CEmit`, `Diagnostics`, `Api`, `Metadata`, `Std`,
`Lowering`, `LoweringXml`, `Ast`, and `Declarations` lanes remain compiler
regression lanes. Do not mark those tests replaced by stdlib `@test` coverage
unless a future audit identifies a case whose only purpose is duplicated stdlib
behavior.
