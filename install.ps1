<#
.SYNOPSIS
    Installs the s&box Claude Bridge addon.

.DESCRIPTION
    Detects your s&box installation, copies the Bridge addon into the
    addons directory, and verifies the install. After running this,
    restart s&box and the Bridge will start automatically on port 29015.

.EXAMPLE
    .\install.ps1
    # Auto-detects s&box and installs

.EXAMPLE
    .\install.ps1 -SboxPath "D:\SteamLibrary\steamapps\common\sbox"
    # Manual path override
#>

param(
    [string]$SboxPath = ""
)

$ErrorActionPreference = "Stop"
$addonName = "sbox-bridge-addon"

Write-Host ""
Write-Host "=== s&box Claude Bridge Installer ===" -ForegroundColor Cyan
Write-Host ""

# ── Locate s&box installation ──────────────────────────────────────

function Find-SboxPath {
    # Check common Steam install locations
    $candidates = @(
        "$env:ProgramFiles\Steam\steamapps\common\sbox",
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\sbox",
        "D:\SteamLibrary\steamapps\common\sbox",
        "E:\SteamLibrary\steamapps\common\sbox",
        "F:\SteamLibrary\steamapps\common\sbox",
        "$env:USERPROFILE\.sbox"
    )

    foreach ($path in $candidates) {
        if (Test-Path $path) {
            return $path
        }
    }

    # Try reading Steam's libraryfolders.vdf to find all library paths
    $steamConfig = "${env:ProgramFiles(x86)}\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path $steamConfig) {
        $content = Get-Content $steamConfig -Raw
        $matches = [regex]::Matches($content, '"path"\s+"([^"]+)"')
        foreach ($match in $matches) {
            $libPath = $match.Groups[1].Value -replace '\\\\', '\'
            $sboxPath = Join-Path $libPath "steamapps\common\sbox"
            if (Test-Path $sboxPath) {
                return $sboxPath
            }
        }
    }

    return $null
}

if ($SboxPath -eq "") {
    Write-Host "Searching for s&box installation..." -ForegroundColor Yellow
    $SboxPath = Find-SboxPath

    if ($null -eq $SboxPath) {
        Write-Host "Could not auto-detect s&box installation." -ForegroundColor Red
        Write-Host ""
        Write-Host "Please run again with the -SboxPath parameter:" -ForegroundColor Yellow
        Write-Host '  .\install.ps1 -SboxPath "C:\path\to\sbox"' -ForegroundColor White
        Write-Host ""
        Write-Host "Your s&box folder is typically at:" -ForegroundColor Yellow
        Write-Host "  C:\Program Files\Steam\steamapps\common\sbox" -ForegroundColor White
        exit 1
    }
}

if (-not (Test-Path $SboxPath)) {
    Write-Host "s&box path not found: $SboxPath" -ForegroundColor Red
    exit 1
}

Write-Host "Found s&box at: $SboxPath" -ForegroundColor Green

# ── Determine addons directory ─────────────────────────────────────

$addonsDir = Join-Path $SboxPath "addons"
if (-not (Test-Path $addonsDir)) {
    # Some s&box installs use a different addons location
    $altAddonsDir = Join-Path $env:USERPROFILE ".sbox\addons"
    if (Test-Path $altAddonsDir) {
        $addonsDir = $altAddonsDir
    } else {
        Write-Host "Creating addons directory: $addonsDir" -ForegroundColor Yellow
        New-Item -ItemType Directory -Path $addonsDir -Force | Out-Null
    }
}

Write-Host "Addons directory: $addonsDir" -ForegroundColor Green

# ── Find the addon source ─────────────────────────────────────────

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$addonSource = Join-Path $scriptDir $addonName

if (-not (Test-Path $addonSource)) {
    # Try parent directory (in case script is in a scripts/ folder)
    $addonSource = Join-Path (Split-Path -Parent $scriptDir) $addonName
}

if (-not (Test-Path $addonSource)) {
    Write-Host "Cannot find $addonName folder. Make sure you're running this from the Sbox-Claude repository." -ForegroundColor Red
    exit 1
}

# ── Copy addon ─────────────────────────────────────────────────────

$destination = Join-Path $addonsDir $addonName

if (Test-Path $destination) {
    Write-Host "Existing installation found. Updating..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $destination
}

Write-Host "Copying Bridge addon to s&box..." -ForegroundColor Yellow
Copy-Item -Recurse -Force $addonSource $destination

# ── Verify ─────────────────────────────────────────────────────────

$projectFile = Join-Path $destination "$addonName.sbproj"
if (Test-Path $projectFile) {
    Write-Host ""
    Write-Host "Installation successful!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Installed to: $destination" -ForegroundColor White
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "  1. Start (or restart) s&box" -ForegroundColor White
    Write-Host "  2. The Bridge addon will compile and start automatically" -ForegroundColor White
    Write-Host "  3. Connect Claude Code:" -ForegroundColor White
    Write-Host '     claude mcp add sbox -- npx sbox-mcp-server' -ForegroundColor Green
    Write-Host "  4. Start building your game!" -ForegroundColor White
    Write-Host ""
} else {
    Write-Host "Warning: Installation may be incomplete. Project file not found at expected location." -ForegroundColor Red
    exit 1
}
