#!/usr/bin/env bash
# RUMI Printer-App — install pre-commit hooks.
# Run this once after cloning the repo, and again after pulling hook updates.
#
# Usage: bash scripts/setup_hooks.sh
#
# Note: the printer-app builds for Windows only. This script handles the
# pre-commit hook setup, which works on any platform. Actual `dotnet build`
# requires Windows; use build-windows.ps1 there.

set -euo pipefail

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'
ok()   { echo -e "${GREEN}\xe2\x9c\x93${NC}  $1"; }
warn() { echo -e "${YELLOW}\xe2\x9a\xa0${NC}  $1"; }
fail() { echo -e "${RED}\xe2\x9c\x97${NC}  $1"; exit 1; }

cd "$(dirname "$0")/.."

# ── pre-commit framework ──────────────────────────────────────────────
if ! command -v pre-commit >/dev/null 2>&1; then
  warn "pre-commit not installed."
  echo "    Install: brew install pre-commit       (macOS)"
  echo "             pip install pre-commit         (any platform)"
  echo "             pipx install pre-commit        (recommended for global)"
  fail "Re-run this script after installing pre-commit."
fi
ok "pre-commit found: $(pre-commit --version)"

# ── detect-secrets (used by the secret-scan hook) ─────────────────────
if ! command -v detect-secrets >/dev/null 2>&1; then
  warn "detect-secrets not installed (used by the secret-scan hook)."
  echo "    Install: pip install detect-secrets"
  echo "             pipx install detect-secrets   (recommended for global)"
  fail "Re-run this script after installing detect-secrets."
fi
ok "detect-secrets found: $(detect-secrets --version)"

# ── Install git hooks ────────────────────────────────────────────────
pre-commit install
pre-commit install --hook-type pre-push 2>/dev/null || true
ok "Git hooks installed (.git/hooks/pre-commit, .git/hooks/pre-push)."

# pre-commit's pre-push install OVERWRITES the RUMI review-gate symlink
# (workspace discipline rule #7). The gate hook itself chains pre-commit's
# pre-push checks, so restoring it loses nothing. Standalone clones (no
# workspace checkout) skip this silently.
REVIEW_GATE_INSTALL="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." 2>/dev/null && pwd)/scripts/review-gate/install.sh"
if [[ -x "$REVIEW_GATE_INSTALL" ]]; then
  if bash "$REVIEW_GATE_INSTALL" printer-app >/dev/null 2>&1; then
    ok "RUMI review-gate pre-push hook restored (chains pre-commit's pre-push checks)."
  else
    warn "review-gate hook reinstall failed — run scripts/review-gate/install.sh printer-app from the workspace root."
  fi
  # pre-commit parks the hook it displaced as pre-push.legacy and re-runs it
  # from hook-impl — if that displaced hook was the gate symlink, every push
  # would recurse (gate → pre-commit → legacy gate …). Drop it.
  LEGACY_HOOK="$(git rev-parse --git-path hooks/pre-push.legacy 2>/dev/null)"
  if [[ -L "$LEGACY_HOOK" && "$(readlink "$LEGACY_HOOK")" == *"review-gate/git-pre-push.sh" ]]; then
    rm "$LEGACY_HOOK"
  fi
fi

# ── Verify .secrets.baseline exists ──────────────────────────────────
if [[ ! -f .secrets.baseline ]]; then
  warn ".secrets.baseline missing — generating now..."
  detect-secrets scan --exclude-files '\.secrets\.baseline$|/bin/|/obj/|/node_modules/|\.dll$|\.pdb$|\.appx$|\.msix$|\.cer$|\.pfx$' > .secrets.baseline
  ok "Generated .secrets.baseline (review and commit it)."
fi

# ── Optional: warm hook cache ────────────────────────────────────────
echo ""
echo "Warming pre-commit hook cache..."
pre-commit install-hooks
ok "Hooks ready."

echo ""
echo "Done. Hooks installed for this clone."
echo "  Pre-commit gates: trailing-whitespace, EOF, large files, secret scan, no-commit-to-protected"
echo "  Bypass attempts:  use 'pre-commit run --all-files' to dry-run; never 'git commit --no-verify'."
