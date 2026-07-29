#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

python3 - "$script_dir" <<'PY'
import json
import os
import sys

script_dir = sys.argv[1]
manifest_path = os.path.join(script_dir, "package.json")

with open(manifest_path, "r", encoding="utf-8") as manifest_file:
    manifest = json.load(manifest_file)

if manifest.get("type") != "bundle":
    raise SystemExit("ERROR: package.json must declare type 'bundle'")

languages = manifest.get("fresh", {}).get("languages")
if not isinstance(languages, list):
    raise SystemExit("ERROR: package.json must define fresh.languages")

by_id = {language.get("id"): language for language in languages if isinstance(language, dict)}
for language_id in ("camp", "campbuild"):
    if language_id not in by_id:
        raise SystemExit(f"ERROR: missing language entry '{language_id}'")

camp_lsp = by_id["camp"].get("lsp")
if not isinstance(camp_lsp, dict) or camp_lsp.get("command") != "camp-lsp":
    raise SystemExit("ERROR: camp language must start camp-lsp")

if by_id["campbuild"].get("lsp") is not None:
    raise SystemExit("ERROR: campbuild language must not start an LSP server")

for language in languages:
    grammar = language.get("grammar")
    if not isinstance(grammar, dict):
        raise SystemExit(f"ERROR: language '{language.get('id')}' must define grammar")

    grammar_file = grammar.get("file")
    if not isinstance(grammar_file, str):
        raise SystemExit(f"ERROR: language '{language.get('id')}' grammar.file must be a string")

    grammar_path = os.path.join(script_dir, grammar_file)
    if not os.path.isfile(grammar_path):
        raise SystemExit(f"ERROR: grammar file not found: {grammar_file}")

print("Camp Fresh package validation passed.")
PY
