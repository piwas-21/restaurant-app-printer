#!/usr/bin/env bash
# PostToolUse single-file checker (printer-app, *.cs / *.xaml). Instant in-loop
# feedback on file-length + a few CLAUDE.md conventions right after an edit.
# Contract: NON-BLOCKING (always exit 0), fast (<200ms, no build/network), quiet on
# success, warnings to stderr as `path: <rule>: <details>`. Bash mirror of the
# .ps1 (dev shell here is bash/macOS). Path from $1, else PostToolUse stdin JSON.
set -uo pipefail

f="${1:-}"
if [[ -z "$f" && ! -t 0 ]]; then
  f="$(python3 -c "import sys,json;print(json.load(sys.stdin).get('tool_input',{}).get('file_path',''))" 2>/dev/null || true)"
fi
[[ -n "$f" && -f "$f" ]] || exit 0
case "$f" in *.cs|*.xaml) ;; *) exit 0 ;; esac
case "$f" in *.g.cs|*.xaml.g.cs|*.Designer.cs|*/obj/*|*/bin/*) exit 0 ;; esac

warn() { echo "$f: $1" >&2; }
loc=$(wc -l < "$f" | tr -d ' ')

# File-length limits (printer-app/CLAUDE.md §4)
lim=0; kind=""
case "$f" in
  *.xaml.cs)                 lim=200; kind="page code-behind" ;;
  */Services/*.cs)           lim=300; kind="service" ;;
  */Models/*.cs)             lim=80;  kind="model" ;;
esac
[[ $lim -gt 0 && $loc -gt $lim ]] && warn "file-length: $kind ~${loc} LOC (limit ${lim}) — split per CLAUDE.md §4"

# ESC/POS byte literals belong in a constants file, not inline (printer-app rule 4)
case "$f" in
  *Constants*|*EscPos*|*Esc*Command*) ;;
  *.cs) grep -nqE '0x1[BD]\b' "$f" \
    && warn "ESC/POS byte literal (0x1B/0x1D) outside a constants file — define it in the ESC/POS constants" ;;
esac

# Services must not drive UI navigation (business logic vs UI separation)
case "$f" in
  */Services/*.cs) grep -nq 'Shell.Current.GoToAsync' "$f" \
    && warn "Shell.Current.GoToAsync in a service — navigation belongs in code-behind, not services" ;;
esac

exit 0
