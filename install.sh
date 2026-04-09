#!/usr/bin/env bash
#
# s&box Claude Bridge Installer (Linux/WSL/Mac)
#
# Detects your s&box installation via Steam, copies the Bridge addon
# into the addons directory, and verifies the install.
#
# Usage:
#   ./install.sh                              # Auto-detect
#   ./install.sh /path/to/sbox                # Manual path
#   SBOX_PATH=/path/to/sbox ./install.sh      # Via env var

set -euo pipefail

ADDON_NAME="sbox-bridge-addon"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ADDON_SOURCE="$SCRIPT_DIR/$ADDON_NAME"

echo ""
echo "=== s&box Claude Bridge Installer ==="
echo ""

# ── Locate s&box installation ──────────────────────────────────────

find_sbox() {
    # Check argument first
    if [[ -n "${1:-}" ]] && [[ -d "$1" ]]; then
        echo "$1"
        return
    fi

    # Check env var
    if [[ -n "${SBOX_PATH:-}" ]] && [[ -d "$SBOX_PATH" ]]; then
        echo "$SBOX_PATH"
        return
    fi

    # Common Steam paths (Linux)
    local candidates=(
        "$HOME/.steam/steam/steamapps/common/sbox"
        "$HOME/.local/share/Steam/steamapps/common/sbox"
    )

    # WSL: check Windows Steam paths
    if [[ -d "/mnt/c" ]]; then
        candidates+=(
            "/mnt/c/Program Files/Steam/steamapps/common/sbox"
            "/mnt/c/Program Files (x86)/Steam/steamapps/common/sbox"
            "/mnt/d/SteamLibrary/steamapps/common/sbox"
            "/mnt/e/SteamLibrary/steamapps/common/sbox"
        )
    fi

    # macOS Steam path
    candidates+=(
        "$HOME/Library/Application Support/Steam/steamapps/common/sbox"
    )

    for path in "${candidates[@]}"; do
        if [[ -d "$path" ]]; then
            echo "$path"
            return
        fi
    done

    # Try parsing Steam's libraryfolders.vdf
    local steam_config="$HOME/.steam/steam/steamapps/libraryfolders.vdf"
    if [[ ! -f "$steam_config" ]]; then
        steam_config="$HOME/.local/share/Steam/steamapps/libraryfolders.vdf"
    fi

    if [[ -f "$steam_config" ]]; then
        while IFS= read -r line; do
            local lib_path
            lib_path=$(echo "$line" | grep -oP '"path"\s+"\K[^"]+' 2>/dev/null || true)
            if [[ -n "$lib_path" ]] && [[ -d "$lib_path/steamapps/common/sbox" ]]; then
                echo "$lib_path/steamapps/common/sbox"
                return
            fi
        done < "$steam_config"
    fi

    return 1
}

SBOX_PATH=$(find_sbox "${1:-}" 2>/dev/null) || {
    echo "ERROR: Could not auto-detect s&box installation." >&2
    echo ""
    echo "Please run again with the path:"
    echo "  ./install.sh /path/to/sbox"
    echo ""
    echo "Common locations:"
    echo "  Linux:  ~/.steam/steam/steamapps/common/sbox"
    echo "  WSL:    /mnt/c/Program Files/Steam/steamapps/common/sbox"
    exit 1
}

echo "Found s&box at: $SBOX_PATH"

# ── Determine addons directory ─────────────────────────────────────

ADDONS_DIR="$SBOX_PATH/addons"
if [[ ! -d "$ADDONS_DIR" ]]; then
    echo "Creating addons directory: $ADDONS_DIR"
    mkdir -p "$ADDONS_DIR"
fi

echo "Addons directory: $ADDONS_DIR"

# ── Verify source ─────────────────────────────────────────────────

if [[ ! -d "$ADDON_SOURCE" ]]; then
    echo "ERROR: Cannot find $ADDON_NAME folder at $ADDON_SOURCE" >&2
    echo "Make sure you're running this from the Sbox-Claude repository." >&2
    exit 1
fi

# ── Copy addon ─────────────────────────────────────────────────────

DESTINATION="$ADDONS_DIR/$ADDON_NAME"

if [[ -d "$DESTINATION" ]]; then
    echo "Existing installation found. Updating..."
    rm -rf "$DESTINATION"
fi

echo "Copying Bridge addon to s&box..."
cp -r "$ADDON_SOURCE" "$DESTINATION"

# ── Verify ─────────────────────────────────────────────────────────

if [[ -f "$DESTINATION/$ADDON_NAME.sbproj" ]]; then
    echo ""
    echo "Installation successful!"
    echo ""
    echo "Installed to: $DESTINATION"
    echo ""
    echo "Next steps:"
    echo "  1. Start (or restart) s&box"
    echo "  2. The Bridge addon will compile and start automatically"
    echo "  3. Connect Claude Code:"
    echo "     claude mcp add sbox -- npx sbox-mcp-server"
    echo "  4. Start building your game!"
    echo ""
else
    echo "WARNING: Installation may be incomplete. Project file not found." >&2
    exit 1
fi
