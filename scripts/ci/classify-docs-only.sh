#!/usr/bin/env bash
# classify-docs-only.sh — prints "docs_only=true" | "docs_only=false" (ADR-0016 §3.4).
# Fail-closed: empty diff or ANY non-allowlisted path => false. Rename/copy => both
# paths classified (--no-renames surfaces a rename as delete(old)+add(new)).
set -euo pipefail
BASE="${1:?usage: classify-docs-only.sh <base-sha> [head]}"
HEAD="${2:-HEAD}"
mapfile -t files < <(git diff --name-only --no-renames "$BASE" "$HEAD")
[ "${#files[@]}" -eq 0 ] && { echo "docs_only=false"; exit 0; }   # empty/unknown => fail-closed
for f in "${files[@]}"; do
  case "$f" in
    docs/*|openspec/*|CHANGELOG.md) continue ;;   # docs + specs + changelog
    */README.md) continue ;;                      # README at any depth
  esac
  case "$f" in
    */*)  echo "docs_only=false"; exit 0 ;;       # nested non-doc path
    *.md) continue ;;                             # top-level *.md only (NOT **/*.md)
    *)    echo "docs_only=false"; exit 0 ;;       # top-level non-md
  esac
done
echo "docs_only=true"
