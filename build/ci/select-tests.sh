#!/usr/bin/env bash
# Decides which test projects CI runs for a change set, so a provider change tests that provider.
#
# Usage: build/ci/select-tests.sh <base-sha>
# Writes to $GITHUB_OUTPUT (or stdout):
#   scope=full|partial|none
#   projects=<space-separated test project paths>   (partial only)
#
# Rules, applied per changed file:
#   *.md, docs/**, LICENSE, issue/PR templates, CODEOWNERS  -> no tests
#   src|tests/Providers/<cc>/<Provider>/**                   -> tests/Providers/<cc>/<Provider>
#   src|tests/Formats/<Format>/**                            -> tests/Formats/<Format>
#   src|tests/Countries/<cc>/**                              -> tests/Countries/<cc> and every tests/Providers/<cc>/*
#   anything else (contract, build, workflows, shared props) -> full
# A missing or unknown base (new branch, manual run) means full.
set -euo pipefail

base="${1:-}"
out="${GITHUB_OUTPUT:-/dev/stdout}"

emit() {
  echo "scope=$1" >> "$out"
  echo "projects=${2:-}" >> "$out"
  echo "Test scope: $1 ${2:-}" >&2
}

if [[ -z "$base" || "$base" =~ ^0+$ ]] || ! git cat-file -e "${base}^{commit}" 2>/dev/null; then
  emit full
  exit 0
fi

declare -A dirs=()
full=false
while IFS= read -r file; do
  [[ -z "$file" ]] && continue
  case "$file" in
    *.md|docs/*|LICENSE|.github/ISSUE_TEMPLATE/*|.github/pull_request_template.md|.github/CODEOWNERS)
      ;;
    src/Providers/*/*/*|tests/Providers/*/*/*)
      IFS=/ read -r _ _ country provider _ <<< "$file"
      dirs["tests/Providers/$country/$provider"]=1
      ;;
    src/Formats/*/*|tests/Formats/*/*)
      IFS=/ read -r _ _ format _ <<< "$file"
      dirs["tests/Formats/$format"]=1
      ;;
    src/Countries/*/*|tests/Countries/*/*)
      IFS=/ read -r _ _ country _ <<< "$file"
      dirs["tests/Countries/$country"]=1
      for provider in tests/Providers/"$country"/*/; do
        [[ -d "$provider" ]] && dirs["${provider%/}"]=1
      done
      ;;
    *)
      full=true
      ;;
  esac
done < <(git diff --name-only "$base" HEAD)

if [[ "$full" == true ]]; then
  emit full
  exit 0
fi

if [[ ${#dirs[@]} -eq 0 ]]; then
  emit none
  exit 0
fi

projects=()
for dir in "${!dirs[@]}"; do
  mapfile -t found < <(find "$dir" -name '*.Tests.csproj' -not -path '*/bin/*' -not -path '*/obj/*' 2>/dev/null | sort)
  if [[ ${#found[@]} -eq 0 ]]; then
    if [[ -d "${dir/#tests/src}" ]]; then
      echo "::error title=Missing tests::${dir/#tests/src} changed but $dir has no *.Tests.csproj. Every provider and format ships with tests (CONTRIBUTING.md)." >&2
      exit 1
    fi
    # Directory removed in this change: nothing left to test.
    continue
  fi
  projects+=("${found[@]}")
done

if [[ ${#projects[@]} -eq 0 ]]; then
  emit none
else
  emit partial "$(printf '%s\n' "${projects[@]}" | sort -u | tr '\n' ' ' | sed 's/ $//')"
fi
