#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
cd "$repo_root"

configuration="${CAMP_TEST_CONFIGURATION:-Debug}"
timeout_seconds="${CAMP_TEST_TIMEOUT_SECONDS:-900}"
sample_processes="${CAMP_TEST_SAMPLE_PROCESSES:-0}"
stdrun_batch_size="${CAMP_TEST_STDRUN_BATCH_SIZE:-}"
list_stdrun_batches="${CAMP_TEST_LIST_STDRUN_BATCHES:-0}"
native_parallelism="${CAMP_TEST_NATIVE_PARALLELISM:-auto}"
cli_parallelism="${CAMP_TEST_CLI_PARALLELISM:-auto}"
mode="${1:-auto}"

test_project="src/Camp.Compiler.TestRunner/Camp.Compiler.TestRunner.csproj"
solution="src/camplang.sln"

target_framework="$(
	python3 - <<'PY'
import xml.etree.ElementTree as ET
project = ET.parse("src/Camp.Compiler.TestRunner/Camp.Compiler.TestRunner.csproj")
for group in project.getroot().findall("PropertyGroup"):
	value = group.findtext("TargetFramework")
	if value:
		print(value.strip())
		break
else:
	raise SystemExit("TargetFramework not found")
PY
)"

test_assembly="src/Camp.Compiler.TestRunner/bin/$configuration/$target_framework/Camp.Compiler.TestRunner.dll"

usage() {
	cat <<USAGE
Usage: scripts/run-tests.sh [auto|full|sectioned]

Modes:
  auto       Section on macOS, full run elsewhere. This is the default.
  full       Run one VSTest invocation after building.
  sectioned  Run the macOS-safe sectioned suite on any host.

Environment:
  CAMP_TEST_TIMEOUT_SECONDS  Per VSTest invocation timeout in seconds. Default: 900.
  CAMP_TEST_CONFIGURATION    Build configuration. Default: Debug.
  CAMP_TEST_SAMPLE_PROCESSES Optional process-count sampling. Set to 1 to enable.
  CAMP_TEST_STDRUN_BATCH_SIZE StdRun cases per VSTest invocation in sectioned mode.
  CAMP_TEST_LIST_STDRUN_BATCHES Print StdRun batches and exit after discovery.
  CAMP_TEST_NATIVE_PARALLELISM Native compile/run gate. Default: 1 on macOS, unlimited elsewhere.
  CAMP_TEST_CLI_PARALLELISM External campc process gate. Default: 1 on macOS, unlimited elsewhere.
USAGE
}

if [[ "$mode" == "--help" || "$mode" == "-h" ]]; then
	usage
	exit 0
fi

host="$(uname -s)"
if [[ "$mode" == "auto" ]]; then
	if [[ "$host" == "Darwin" ]]; then
		mode="sectioned"
	else
		mode="full"
	fi
fi

if [[ "$mode" != "full" && "$mode" != "sectioned" ]]; then
	usage >&2
	exit 2
fi

if [[ -z "$stdrun_batch_size" ]]; then
	if [[ "$host" == "Darwin" && "$mode" == "sectioned" ]]; then
		stdrun_batch_size=8
	else
		stdrun_batch_size=1
	fi
fi

if ! [[ "$stdrun_batch_size" =~ ^[0-9]+$ ]] || (( stdrun_batch_size < 1 )); then
	echo "CAMP_TEST_STDRUN_BATCH_SIZE must be a positive integer." >&2
	exit 2
fi

echo "[camp-test] repository: $repo_root"
echo "[camp-test] configuration: $configuration"
echo "[camp-test] target framework: $target_framework"
echo "[camp-test] mode: $mode"
echo "[camp-test] timeout per invocation: ${timeout_seconds}s"
echo "[camp-test] StdRun batch size: $stdrun_batch_size"
echo "[camp-test] process sampling: $sample_processes"
echo "[camp-test] native parallelism: $native_parallelism"
echo "[camp-test] CLI parallelism: $cli_parallelism"

now_ms() {
	python3 - <<'PY'
import time
print(int(time.time() * 1000))
PY
}

suite_start_ms="$(now_ms)"
vstest_invocations=0
max_dotnet=0
max_testhost=0
max_campc=0
max_clang=0
max_ld=0
max_generated_test=0

count_processes() {
	local pattern="$1"
	ps -axo command= | awk -v pattern="$pattern" '$0 ~ pattern { count++ } END { print count + 0 }'
}

update_max() {
	local current="$1"
	local variable="$2"
	local existing="${!variable}"
	if (( current > existing )); then
		printf -v "$variable" '%s' "$current"
	fi
}

sample_process_counts() {
	if [[ "$sample_processes" != "1" ]]; then
		return
	fi
	update_max "$(count_processes '(^|/)dotnet( |$)')" max_dotnet
	update_max "$(count_processes 'testhost')" max_testhost
	update_max "$(count_processes '(^|/)campc(\\.dll|\\.exe)?( |$)')" max_campc
	update_max "$(count_processes '(^|/)(clang|clang\\+\\+)( |$)')" max_clang
	update_max "$(count_processes '(^|/)ld( |$)')" max_ld
	update_max "$(count_processes 'golden-stdrun|StdRun|\\.out/build/')" max_generated_test
}

write_summary() {
	local status="$?"
	local suite_end_ms
	suite_end_ms="$(now_ms)"
	local elapsed_ms=$((suite_end_ms - suite_start_ms))
	echo "[camp-test] summary: status=$status elapsed_ms=$elapsed_ms vstest_invocations=$vstest_invocations"
	if [[ "$sample_processes" == "1" ]]; then
		local metrics_dir="tmp/test-metrics"
		mkdir -p "$metrics_dir"
		local metrics_file="$metrics_dir/process-counts.txt"
		{
			echo "max_dotnet=$max_dotnet"
			echo "max_testhost=$max_testhost"
			echo "max_campc=$max_campc"
			echo "max_clang=$max_clang"
			echo "max_ld=$max_ld"
			echo "max_generated_test=$max_generated_test"
		} > "$metrics_file"
		echo "[camp-test] process-count metrics: $metrics_file"
	fi
}

trap write_summary EXIT

dump_process_state() {
	local label="$1"
	local dump_dir="tmp/test-hang-dumps"
	mkdir -p "$dump_dir"
	local stamp
	stamp="$(date +"%Y%m%d-%H%M%S")"
	local state_file="$dump_dir/$stamp-$label-processes.txt"
	echo "[camp-test] writing process snapshot: $state_file" >&2
	ps -axo pid,ppid,etime,pcpu,pmem,state,command > "$state_file" || true
	if [[ "$host" == "Darwin" ]]; then
		while read -r pid name; do
			[[ -z "$pid" ]] && continue
			local sample_file="$dump_dir/$stamp-$label-$name-$pid.sample.txt"
			echo "[camp-test] sampling $name pid $pid: $sample_file" >&2
			sample "$pid" 5 5 -file "$sample_file" >/dev/null 2>&1 || true
		done < <(ps -axo pid=,comm= | awk '/Camp.Compiler.TestRunner|testhost|vstest.console|dotnet$/ { print $1, $2 }')
	fi
}

kill_tree() {
	local root_pid="$1"
	local children
	children="$(pgrep -P "$root_pid" 2>/dev/null || true)"
	for child in $children; do
		kill_tree "$child"
	done
	kill "$root_pid" 2>/dev/null || true
}

run_with_timeout() {
	local label="$1"
	shift
	echo "[camp-test] begin $label"
	local label_start_ms
	label_start_ms="$(now_ms)"
	(
		"$@"
	) &
	local pid=$!
	local start
	start="$(date +%s)"
	while kill -0 "$pid" 2>/dev/null; do
		sleep 5
		sample_process_counts
		local now
		now="$(date +%s)"
		if (( now - start > timeout_seconds )); then
			echo "[camp-test] timeout after ${timeout_seconds}s: $label" >&2
			dump_process_state "$(echo "$label" | tr '/ :' '---')"
			kill_tree "$pid"
			wait "$pid" 2>/dev/null || true
			return 124
		fi
	done
	wait "$pid"
	local status=$?
	local label_end_ms
	label_end_ms="$(now_ms)"
	local elapsed_ms=$((label_end_ms - label_start_ms))
	echo "[camp-test] end $label status=$status elapsed_ms=$elapsed_ms"
	return "$status"
}

vstest() {
	vstest_invocations=$((vstest_invocations + 1))
	run_with_timeout "$1" dotnet vstest "$test_assembly" "${@:2}"
}

discover_stdrun_cases() {
	find tests/StdRun -maxdepth 1 -name '*.camp' -type f \
		| sed 's#^tests/StdRun/##; s#\.camp$##' \
		| sort
}

join_cases() {
	local IFS=,
	echo "$*"
}

print_stdrun_batches() {
	local cases=("$@")
	local batch_count=$(( (${#cases[@]} + stdrun_batch_size - 1) / stdrun_batch_size ))
	for ((batch_index = 0; batch_index < batch_count; batch_index++)); do
		local start_index=$((batch_index * stdrun_batch_size))
		local batch=("${cases[@]:start_index:stdrun_batch_size}")
		local last_index=$((${#batch[@]} - 1))
		local first_case="${batch[0]}"
		local last_case="${batch[$last_index]}"
		echo "[camp-test] StdRun batch $((batch_index + 1))/$batch_count $first_case..$last_case cases=$(join_cases "${batch[@]}")"
	done
}

if [[ "$list_stdrun_batches" == "1" ]]; then
	stdrun_cases=()
	while IFS= read -r case_name; do
		stdrun_cases+=("$case_name")
	done < <(discover_stdrun_cases)
	print_stdrun_batches "${stdrun_cases[@]}"
	exit 0
fi

echo "[camp-test] building solution"
build_start_ms="$(now_ms)"
dotnet build "$solution" -c "$configuration"
build_end_ms="$(now_ms)"
echo "[camp-test] build elapsed_ms=$((build_end_ms - build_start_ms))"

if [[ "$mode" == "full" ]]; then
	vstest "full suite"
	exit $?
fi

golden_kinds=(Ast Declarations LoweringXml Lowering Diagnostics CEmit CCompile Api Metadata Std)
for kind in "${golden_kinds[@]}"; do
	export CAMP_TEST_KIND="$kind"
	unset CAMP_TEST_CASE
	unset CAMP_TEST_CASES
	vstest "golden $kind" --TestCaseFilter:FullyQualifiedName~GoldenFileTests
done

stdrun_cases=()
while IFS= read -r case_name; do
	stdrun_cases+=("$case_name")
done < <(discover_stdrun_cases)

stdrun_batch_count=$(( (${#stdrun_cases[@]} + stdrun_batch_size - 1) / stdrun_batch_size ))
for ((batch_index = 0; batch_index < stdrun_batch_count; batch_index++)); do
	start_index=$((batch_index * stdrun_batch_size))
	batch=("${stdrun_cases[@]:start_index:stdrun_batch_size}")
	last_index=$((${#batch[@]} - 1))
	first_case="${batch[0]}"
	last_case="${batch[$last_index]}"
	export CAMP_TEST_KIND=StdRun
	if (( ${#batch[@]} == 1 )); then
		export CAMP_TEST_CASE="${batch[0]}"
		unset CAMP_TEST_CASES
		vstest "golden StdRun/${batch[0]}" --TestCaseFilter:FullyQualifiedName~GoldenFileTests
	else
		unset CAMP_TEST_CASE
		export CAMP_TEST_CASES="$(join_cases "${batch[@]}")"
		vstest "golden StdRun batch $((batch_index + 1))/$stdrun_batch_count $first_case..$last_case" --TestCaseFilter:FullyQualifiedName~GoldenFileTests
	fi
done

unset CAMP_TEST_KIND
unset CAMP_TEST_CASE
unset CAMP_TEST_CASES

test_classes=(
	CampCoverageDecorationTests
	CampTestDiscoveryTests
	CampTestResultDiagnosticTests
	CommandLineTests
	CompilerDriverOptionTests
	DapServerTests
	DiagnosticStructureTests
	InterpolatedStringParserTests
	LanguageServiceTests
	LspServerTests
	MsvcCompileTests
	PrepParserTests
	ProjectLoaderTests
	SemanticTests
	SourcefilePathMapperTests
	SyntaxHighlightingFilesTests
	TargetCapabilityTests
)

for class_name in "${test_classes[@]}"; do
	vstest "class $class_name" --TestCaseFilter:FullyQualifiedName~"$class_name"
done

echo "[camp-test] sectioned suite passed"
