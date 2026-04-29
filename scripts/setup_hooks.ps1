# RUMI Printer-App - install pre-commit hooks (Windows / PowerShell mirror of setup_hooks.sh).
# Usage: pwsh -File scripts/setup_hooks.ps1

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

function Write-Ok($msg)   { Write-Host "[OK]   $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Err($msg)  { Write-Host "[FAIL] $msg" -ForegroundColor Red; exit 1 }

# pre-commit framework
if (-not (Get-Command pre-commit -ErrorAction SilentlyContinue)) {
    Write-Warn "pre-commit not installed."
    Write-Host "    Install: pip install pre-commit"
    Write-Host "             pipx install pre-commit  (recommended for global)"
    Write-Err "Re-run this script after installing pre-commit."
}
Write-Ok "pre-commit found: $(pre-commit --version)"

# detect-secrets
if (-not (Get-Command detect-secrets -ErrorAction SilentlyContinue)) {
    Write-Warn "detect-secrets not installed (used by the secret-scan hook)."
    Write-Host "    Install: pip install detect-secrets"
    Write-Host "             pipx install detect-secrets"
    Write-Err "Re-run this script after installing detect-secrets."
}
Write-Ok "detect-secrets found: $(detect-secrets --version)"

# Install git hooks
pre-commit install
try { pre-commit install --hook-type pre-push } catch { }
Write-Ok "Git hooks installed (.git/hooks/pre-commit, .git/hooks/pre-push)."

# Verify .secrets.baseline exists
if (-not (Test-Path .secrets.baseline)) {
    Write-Warn ".secrets.baseline missing - generating now..."
    detect-secrets scan --exclude-files '\.secrets\.baseline$|/bin/|/obj/|/node_modules/|\.dll$|\.pdb$|\.appx$|\.msix$|\.cer$|\.pfx$' | Out-File -Encoding utf8 .secrets.baseline
    Write-Ok "Generated .secrets.baseline (review and commit it)."
}

Write-Host ""
Write-Host "Warming pre-commit hook cache..."
pre-commit install-hooks
Write-Ok "Hooks ready."

Write-Host ""
Write-Host "Done. Hooks installed for this clone."
Write-Host "  Pre-commit gates: trailing-whitespace, EOF, large files, secret scan, no-commit-to-protected"
Write-Host "  Bypass attempts:  use 'pre-commit run --all-files' to dry-run; never 'git commit --no-verify'."
