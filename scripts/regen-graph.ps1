#requires -Version 5.1
<#
.SYNOPSIS
    Refresh the bridge map (docs/graph/) with a deterministic, no-LLM code/AST pass.

.DESCRIPTION
    Runs graphify's code-only AST refresh over the repo and copies the regenerated
    artifacts (graph.json, graph.html, GRAPH_REPORT.md) into docs/graph/.

    This pass is DETERMINISTIC and uses NO LLM: it re-reads the C# handlers and the
    TypeScript tools and rebuilds the structural edges (implements / calls / imports)
    plus community clustering. It does NOT re-read the prose docs, so doc/skill/
    changelog edges and community LABELS keep whatever the last full build produced.

    For the FULL, doc-inclusive graph (the authoritative regen maintainers should run
    as part of every release), use the /graphify skill instead -- it does the full
    AST + semantic (LLM) pass over code AND docs. See docs/graph/README.md.

.NOTES
    Requires graphify to be installed (pip install graphifyy, or uv tool install graphifyy).
#>

[CmdletBinding()]
param(
    # Repo root. Defaults to the parent of this script's folder.
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path $RepoRoot).Path
$graphDir = Join-Path $RepoRoot 'docs\graph'
$outDir   = Join-Path $RepoRoot 'graphify-out'

Write-Host "Bridge map refresh (code/AST, deterministic, no LLM)" -ForegroundColor Cyan
Write-Host "  Repo: $RepoRoot"

# --- Locate a Python that has graphify, or the graphify CLI on PATH ---------
$graphifyCli = Get-Command graphify -ErrorAction SilentlyContinue

function Find-GraphifyPython {
    # Prefer the interpreter the last build saved (graphify-out/.graphify_python).
    $saved = Join-Path $outDir '.graphify_python'
    if (Test-Path $saved) {
        $py = (Get-Content $saved -Raw).Trim()
        if ($py -and (Test-Path $py)) { return $py }
    }
    $pyCmd = Get-Command python -ErrorAction SilentlyContinue
    if ($pyCmd) {
        & $pyCmd.Source -c "import graphify" 2>$null
        if ($LASTEXITCODE -eq 0) { return $pyCmd.Source }
    }
    return $null
}

$py = Find-GraphifyPython

if (-not $graphifyCli -and -not $py) {
    Write-Error @"
graphify not found. Install it first:
    pip install graphifyy
  (or) uv tool install graphifyy
Then re-run this script.
"@
}

# --- Run the deterministic code/AST refresh ---------------------------------
# `graphify update` re-extracts code files and rebuilds graph.json + report + html
# with NO LLM. --force so a refactor that deletes handlers still rewrites the graph.
Push-Location $RepoRoot
try {
    if ($graphifyCli) {
        Write-Host "  Running: graphify update . --force" -ForegroundColor DarkGray
        & graphify update . --force
    } else {
        Write-Host "  Running: python -m graphify update . --force" -ForegroundColor DarkGray
        & $py -m graphify update . --force
    }
    if ($LASTEXITCODE -ne 0) {
        throw "graphify update exited with code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

# --- Copy the regenerated artifacts into docs/graph/ ------------------------
New-Item -ItemType Directory -Force -Path $graphDir | Out-Null
$artifacts = 'graph.json', 'graph.html', 'GRAPH_REPORT.md'
foreach ($a in $artifacts) {
    $src = Join-Path $outDir $a
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination (Join-Path $graphDir $a) -Force
        Write-Host "  Updated docs/graph/$a" -ForegroundColor Green
    } else {
        Write-Warning "  Expected artifact not produced: $src"
    }
}

# Graphify may embed absolute machine roots in generated node IDs. Sanitize the
# copied artifacts immediately so a successful regeneration cannot leave private
# workstation paths behind, then fail closed if the privacy audit still finds any.
Push-Location $RepoRoot
try {
    Write-Host "  Sanitizing generated graph artifacts" -ForegroundColor DarkGray
    & node scripts/audit-repo-privacy.mjs --fix-generated
    if ($LASTEXITCODE -ne 0) {
        throw "generated-artifact privacy audit exited with code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Done - docs/graph/ refreshed from the CODE/AST (deterministic, no LLM)." -ForegroundColor Cyan
Write-Host "NOTE: this did NOT re-read the prose docs. For the FULL doc-inclusive graph" -ForegroundColor Yellow
Write-Host "      (rebuilds doc/skill/changelog edges + community labels), re-run the" -ForegroundColor Yellow
Write-Host "      /graphify skill on the repo - it does the AST + semantic (LLM) pass." -ForegroundColor Yellow
