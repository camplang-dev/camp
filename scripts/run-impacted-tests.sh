#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
cd "$repo_root"

configuration="${CAMP_TEST_CONFIGURATION:-Debug}"
base_ref="${CAMP_IMPACTED_BASE:-HEAD}"
dry_run=1
changed_files=()
golden_lanes=()
class_lanes=()
reasons=()
include_stdrun_smoke=0
include_ccompile_smoke=0

usage() {
	cat <<USAGE
Usage: scripts/run-impacted-tests.sh [--base REF] [--changed PATH] [--run] [--dry-run]

By default, changed files are read from "git diff --name-only REF...HEAD" and
the script prints the conservative test lanes selected for those changes.

Options:
  --base REF      Git base ref for changed-file discovery. Default: HEAD.
  --changed PATH  Add an explicit changed path. May be repeated; bypasses git diff.
  --run           Run the selected lanes after printing them.
  --dry-run       Print selected lanes only. This is the default.
USAGE
}

while (($# > 0)); do
	case "$1" in
		--base)
			base_ref="${2:-}"
			shift 2
			;;
		--changed)
			changed_files+=("${2:-}")
			shift 2
			;;
		--run)
			dry_run=0
			shift
			;;
		--dry-run)
			dry_run=1
			shift
			;;
		--help|-h)
			usage
			exit 0
			;;
		*)
			echo "Unknown argument: $1" >&2
			usage >&2
			exit 2
			;;
	esac
done

add_unique() {
	local value="$1"
	shift
	local existing
	for existing in "$@"; do
		if [[ "$existing" == "$value" ]]; then
			return 1
		fi
	done
	return 0
}

add_golden() {
	local lane="$1"
	if ((${#golden_lanes[@]} == 0)) || add_unique "$lane" "${golden_lanes[@]}"; then
		golden_lanes+=("$lane")
	fi
}

add_class() {
	local lane="$1"
	if ((${#class_lanes[@]} == 0)) || add_unique "$lane" "${class_lanes[@]}"; then
		class_lanes+=("$lane")
	fi
}

add_reason() {
	local reason="$1"
	if ((${#reasons[@]} == 0)) || add_unique "$reason" "${reasons[@]}"; then
		reasons+=("$reason")
	fi
}

if ((${#changed_files[@]} == 0)); then
	while IFS= read -r file; do
		[[ -n "$file" ]] && changed_files+=("$file")
	done < <(git diff --name-only "$base_ref...HEAD")
fi

if ((${#changed_files[@]} == 0)); then
	echo "[camp-impacted] no changed files detected"
	exit 0
fi

for file in "${changed_files[@]}"; do
	case "$file" in
		docs/*|*.md)
			add_reason "docs-only: $file -> no compiler tests by default"
			;;
		src/Camp.Compiler/CampParser.cs|src/Camp.Compiler/CampTokenizer.cs|src/Camp.Compiler/SyntaxNode.cs|src/Camp.Compiler/TokenSequence.cs|src/Camp.Compiler/NumericLiteralParser.cs)
			add_golden Ast
			add_golden Diagnostics
			add_class PrepParserTests
			add_class InterpolatedStringParserTests
			add_reason "parser/syntax: $file -> Ast, Diagnostics, parser unit tests"
			;;
		src/Camp.Compiler/BindableNode*|src/Camp.Compiler/Compilation.cs|src/Camp.Compiler/DeclarationParticipation.cs|src/Camp.Compiler/CallableShapeService.cs)
			add_golden Declarations
			add_golden Diagnostics
			add_golden Lowering
			add_class SemanticTests
			add_reason "binder/analyzer: $file -> Declarations, Diagnostics, Lowering, SemanticTests"
			;;
		src/Camp.Compiler/CCodeEmitter.cs|src/Camp.Compiler/NativeBuildDriver.cs|src/Camp.Compiler/BuildArtifactLayout.cs|src/Camp.Compiler/*Lowering*)
			add_golden CEmit
			add_golden CCompile
			include_stdrun_smoke=1
			add_reason "emitter/native: $file -> CEmit, CCompile, StdRun smoke"
			;;
		lib/std/*)
			add_golden Std
			include_stdrun_smoke=1
			include_ccompile_smoke=1
			add_reason "stdlib: $file -> Std, StdRun smoke, CCompile smoke"
			;;
		src/campc/*|src/Camp.Compiler/CampProjectLoader.cs|src/Camp.Compiler/CompilerDriver.cs|src/Camp.Compiler/CompilerModes.cs)
			add_class CommandLineTests
			add_class ProjectLoaderTests
			add_class CompilerDriverOptionTests
			add_reason "CLI/project: $file -> CommandLineTests, ProjectLoaderTests, CompilerDriverOptionTests"
			;;
		targets/*)
			add_class TargetCapabilityTests
			add_golden CCompile
			add_reason "target metadata: $file -> TargetCapabilityTests, CCompile"
			;;
		tests/Ast/*) add_golden Ast; add_reason "Ast golden changed: $file -> Ast" ;;
		tests/Declarations/*) add_golden Declarations; add_reason "Declarations golden changed: $file -> Declarations" ;;
		tests/LoweringXml/*) add_golden LoweringXml; add_reason "LoweringXml golden changed: $file -> LoweringXml" ;;
		tests/Lowering/*) add_golden Lowering; add_reason "Lowering golden changed: $file -> Lowering" ;;
		tests/Diagnostics/*) add_golden Diagnostics; add_reason "Diagnostics golden changed: $file -> Diagnostics" ;;
		tests/CEmit/*) add_golden CEmit; add_reason "CEmit golden changed: $file -> CEmit" ;;
		tests/CCompile/*) add_golden CCompile; add_reason "CCompile golden changed: $file -> CCompile" ;;
		tests/Api/*) add_golden Api; add_reason "API golden changed: $file -> Api" ;;
		tests/Metadata/*) add_golden Metadata; add_reason "Metadata golden changed: $file -> Metadata" ;;
		tests/Std/*) add_golden Std; add_reason "Std golden changed: $file -> Std" ;;
		tests/StdRun/*) include_stdrun_smoke=1; add_reason "StdRun fixture changed: $file -> StdRun smoke" ;;
		src/Camp.Compiler.TestRunner/*)
			add_class GoldenFileTests
			add_class CommandLineTests
			add_reason "test runner changed: $file -> GoldenFileTests, CommandLineTests"
			;;
		*)
			add_golden Diagnostics
			add_class SemanticTests
			add_reason "unknown impact: $file -> conservative Diagnostics, SemanticTests"
			;;
	esac
done

if ((include_ccompile_smoke)); then
	add_golden CCompile
fi

echo "[camp-impacted] changed files:"
for file in "${changed_files[@]}"; do
	echo "  - $file"
done

echo "[camp-impacted] reasons:"
for reason in "${reasons[@]}"; do
	echo "  - $reason"
done

if ((${#golden_lanes[@]} == 0 && ${#class_lanes[@]} == 0 && include_stdrun_smoke == 0)); then
	echo "[camp-impacted] selected lanes: none"
	exit 0
fi

echo "[camp-impacted] selected golden lanes:"
if ((${#golden_lanes[@]} > 0)); then
	for lane in "${golden_lanes[@]}"; do
		echo "  - $lane"
	done
fi
if ((include_stdrun_smoke)); then
	echo "  - StdRun smoke: string_functions,astring_functions,strconv_functions"
fi

echo "[camp-impacted] selected test classes:"
if ((${#class_lanes[@]} > 0)); then
	for lane in "${class_lanes[@]}"; do
		echo "  - $lane"
	done
fi

if ((dry_run)); then
	echo "[camp-impacted] dry run only; pass --run to execute"
	exit 0
fi

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

echo "[camp-impacted] building test project"
dotnet build src/Camp.Compiler.TestRunner/Camp.Compiler.TestRunner.csproj -c "$configuration"

if ((${#golden_lanes[@]} > 0)); then
	for lane in "${golden_lanes[@]}"; do
		echo "[camp-impacted] running golden $lane"
		CAMP_TEST_KIND="$lane" dotnet vstest "$test_assembly" --TestCaseFilter:FullyQualifiedName~GoldenFileTests
	done
fi

if ((include_stdrun_smoke)); then
	echo "[camp-impacted] running StdRun smoke"
	CAMP_TEST_KIND=StdRun CAMP_TEST_CASES=string_functions,astring_functions,strconv_functions dotnet vstest "$test_assembly" --TestCaseFilter:FullyQualifiedName~GoldenFileTests
fi

if ((${#class_lanes[@]} > 0)); then
	for lane in "${class_lanes[@]}"; do
		echo "[camp-impacted] running class $lane"
		dotnet vstest "$test_assembly" --TestCaseFilter:FullyQualifiedName~"$lane"
	done
fi

echo "[camp-impacted] impacted lanes passed"
