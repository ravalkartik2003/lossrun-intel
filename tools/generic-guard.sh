#!/usr/bin/env bash
# tools/generic-guard.sh (brief 20 §4): code is layout-generic. Carrier-specific knowledge lives in learned specs and the
# glossary, never in src/ (docs/architecture/carrier-format-memory.md §Carrier-generic code).
#
# Fails (exit 1, listing file:line and the rule) when, in src/ outside tests, test data and the synthetic corpus generator:
#   1. a real carrier name from config/carrier-names.txt appears (code, comments, strings);
#   2. code conditions on a carrier identity: a carrier value compared with a string literal, Equals/StartsWith/Contains
#      on a carrier value with a literal, or a switch over a carrier value;
#   3. (brief 34 A4) a model name is a string literal in C# or TypeScript: models are configuration (config/models.json, priced in
#      config/azure-prices.json), never literals in code (CLAUDE.md). Comments may name them.
# Excluded: src/**/*.Tests/, src/TestShared/, src/LossRun.Evaluation/CorpusGenerator*, src/web/**/node_modules/, bin/, obj/, dist/.
set -euo pipefail

repo="$(git -C "$(dirname "$0")/.." rev-parse --show-toplevel)"
cd "$repo"
names="config/carrier-names.txt"

files() {
  git ls-files -co --exclude-standard -- src \
    | grep -Ev '(^|/)[^/]*\.Tests/|^src/TestShared/|^src/LossRun\.Evaluation/CorpusGenerator|/node_modules/|/(bin|obj|dist)/' \
    | grep -E '\.(cs|ts|tsx|js|json|ya?ml|sql|ps1|sh|py)$' || true
}

mapfile -t list < <(files)
fail=0
# A line carrying "generic-guard: allow (<reason>)" is exempt: a scan for the names themselves, or a placeholder value.
report() { [[ "$1" == *"generic-guard: allow"* ]] && return 0; echo "generic-guard: ${1:0:300}"; fail=1; }

# 1. carrier names
while IFS= read -r raw || [ -n "$raw" ]; do
  line="${raw%$'\r'}"
  [[ -z "$line" || "$line" == \#* ]] && continue
  flags=(-n -I -H -E)
  if [[ "$line" == cs:* ]]; then name="${line#cs:}"; else name="$line"; flags+=(-i); fi
  [ ${#list[@]} -eq 0 ] && break
  # Whole words via explicit boundaries (-w with -i -F aborts in some grep builds, e.g. Git for Windows).
  escaped="$(printf '%s' "$name" | sed 's/[][\.*^$+?(){}|/]/\\&/g')"
  while IFS= read -r hit; do
    report "carrier name \"$name\": $hit"
  done < <(grep "${flags[@]}" -- "(^|[^[:alnum:]_])${escaped}([^[:alnum:]_]|\$)" "${list[@]}" 2>/dev/null || true)
done < "$names"

# 2. conditions on a carrier identity (C# and TypeScript)
code=()
for f in "${list[@]}"; do [[ "$f" =~ \.(cs|ts|tsx)$ ]] && code+=("$f"); done
if [ ${#code[@]} -gt 0 ]; then
  patterns=(
    '[A-Za-z_.]*[Cc]arrier[A-Za-z_]*[[:space:]]*[!=]==?[[:space:]]*"'
    '"[[:space:]]*[!=]==?[[:space:]]*[A-Za-z_.]*[Cc]arrier'
    '[Cc]arrier[A-Za-z_]*\??\.(Equals|StartsWith|EndsWith|Contains|startsWith|endsWith|includes)\([[:space:]]*"'
    'switch[[:space:]]*\([^)]*[Cc]arrier'
  )
  for p in "${patterns[@]}"; do
    while IFS= read -r hit; do
      report "condition on a carrier identity: $hit"
    done < <(grep -n -H -E -- "$p" "${code[@]}" 2>/dev/null || true)
  done
fi

# 3. model names as string literals (the names are every model the price table prices)
mapfile -t models < <(grep -oE '"model": *"[^"]+"' config/azure-prices.json | sed -E 's/.*"model": *"([^"]+)"/\1/' | sort -u)
if [ ${#code[@]} -gt 0 ] && [ ${#models[@]} -gt 0 ]; then
  for m in "${models[@]}"; do
    escaped="$(printf '%s' "$m" | sed 's/[][\.*^$+?(){}|/]/\\&/g')"
    while IFS= read -r hit; do
      report "model name \"$m\" as a literal (models are configuration): $hit"
    done < <(grep -n -H -E -- "[\"'\`]${escaped}[\"'\`]" "${code[@]}" 2>/dev/null | grep -vE '^[^:]+:[0-9]+:[[:space:]]*(//|\*|///)' || true)
  done
fi

if [ "$fail" -eq 0 ]; then
  echo "generic-guard: ${#list[@]} file(s) clean ($(grep -cvE '^\s*(#|$)' "$names") carrier names, ${#models[@]} model names)"
fi
exit "$fail"
