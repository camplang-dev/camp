#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
cd "$repo_root"

configuration="${CAMP_TEST_CONFIGURATION:-Debug}"
timeout_seconds="${CAMP_TEST_TIMEOUT_SECONDS:-900}"
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

echo "[camp-test] repository: $repo_root"
echo "[camp-test] configuration: $configuration"
echo "[camp-test] target framework: $target_framework"
echo "[camp-test] mode: $mode"
echo "[camp-test] timeout per invocation: ${timeout_seconds}s"

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
	(
		"$@"
	) &
	local pid=$!
	local start
	start="$(date +%s)"
	while kill -0 "$pid" 2>/dev/null; do
		sleep 5
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
	echo "[camp-test] end $label status=$status"
	return "$status"
}

vstest() {
	run_with_timeout "$1" dotnet vstest "$test_assembly" "${@:2}"
}

echo "[camp-test] building solution"
dotnet build "$solution" -c "$configuration"

if [[ "$mode" == "full" ]]; then
	vstest "full suite"
	exit $?
fi

golden_kinds=(Ast Declarations LoweringXml Lowering Diagnostics CEmit CCompile Api Metadata Std)
for kind in "${golden_kinds[@]}"; do
	export CAMP_TEST_KIND="$kind"
	unset CAMP_TEST_CASE
	vstest "golden $kind" --TestCaseFilter:FullyQualifiedName~GoldenFileTests
done

stdrun_cases=()
while IFS= read -r case_name; do
	stdrun_cases+=("$case_name")
done < <(find tests/StdRun -maxdepth 1 -name '*.camp' -type f \
	| sed 's#^tests/StdRun/##; s#\.camp$##' \
	| sort)
for case_name in "${stdrun_cases[@]}"; do
	export CAMP_TEST_KIND=StdRun
	export CAMP_TEST_CASE="$case_name"
	vstest "golden StdRun/$case_name" --TestCaseFilter:FullyQualifiedName~GoldenFileTests
done

unset CAMP_TEST_KIND
unset CAMP_TEST_CASE

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
