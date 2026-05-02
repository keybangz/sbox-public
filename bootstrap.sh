#!/bin/bash

# =============================================================================
# Project-Local Bootstrap Script
# =============================================================================
# This script is project-local only. It does NOT perform system-wide
# installations. System-wide installs (sudo pip install, sudo apt install,
# sudo dnf install, sudo yum install, brew install, etc.) are BLOCKED.
#
# For one-time setup requirements (wine, winetricks, etc.), see comments
# in the "Manual Setup" section below.
# =============================================================================

# Safety guard: Detect and block system-wide installation attempts
block_system_install() {
    echo "[!] ERROR: System-wide installation detected. This script is project-local only."
    echo "[!] Blocked command would have run: $*"
    echo "[!] Install system dependencies manually if needed."
    return 1
}

# Override dangerous system-wide install commands
sudo() {
    case "$*" in
        *pip*install*|*apt*install*|*dnf*install*|*yum*install*)
            block_system_install "$@"
            return 1
            ;;
        *)
            command sudo "$@"
            ;;
    esac
}

# Also block direct calls to package managers without sudo
apt() { block_system_install "apt $*"; return 1; }
dnf() { block_system_install "dnf $*"; return 1; }
yum() { block_system_install "yum $*"; return 1; }
brew() { block_system_install "brew $*"; return 1; }

# =============================================================================
# Download Helper - Skips if file already exists
# =============================================================================
download_if_missing() {
    local url="$1"
    local dest="$2"
    if [ -f "$dest" ]; then
        echo "  [SKIP] Already exists: $dest"
        return 0
    fi
    echo "  Downloading: $dest"
    wget -q -O "$dest" "$url" || { echo "  [!] Download failed: $url"; return 1; }
}

# =============================================================================
# Wine Availability Check
# =============================================================================
require_wine() {
    if ! command -v wine &>/dev/null; then
        echo "  [!] wine is not installed. Skipping: $*"
        echo "  [!] Install wine manually: https://www.winehq.org/"
        return 1
    fi
    wine "$@"
}

# =============================================================================
# Clean Build Flag Handling
# =============================================================================
CLEAN_BUILD=false
for arg in "$@"; do
    case "$arg" in
        --clean) CLEAN_BUILD=true ;;
    esac
done

if [ "$CLEAN_BUILD" = true ]; then
    echo "Cleaning previous build artifacts..."
    rm -rf ./engine/Sandbox.Engine/bin ./engine/Sandbox.Engine/obj
    rm -rf ./engine/Sandbox.Menu/bin ./engine/Sandbox.Menu/obj
    rm -rf ./engine/Sandbox.Compiling/bin ./engine/Sandbox.Compiling/obj
    rm -rf ./engine/Sandbox.Services/bin ./engine/Sandbox.Services/obj
else
    echo "Skipping clean (pass --clean to force)"
fi

# Disable dotnet telemetry and build server caching to prevent background processes
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
# Disable build servers entirely - they cause file locking issues
export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1
export UseSharedCompilation=false
# Disable MSBuild node reuse (persistent worker processes)
export MSBUILDDISABLENODEREUSE=1

# =============================================================================
# Setup the DXC wrapper for Linux
# The wrapper converts UTF-16 arguments to UTF-32 for Linux compatibility
# =============================================================================
setup_dxc_wrapper() {
    local WRAPPER_SRC="linux/dxc_wrapper.c"
    local WRAPPER_SO="linux/libdxcompiler_wrapper.so"
    local GAME_DIR="game"
    local BIN_DIR="game/bin/linuxsteamrt64"

    echo "Setting up DXC wrapper..."

    # Check if wrapper source exists
    if [ ! -f "$WRAPPER_SRC" ]; then
        echo "  [!] DXC wrapper source not found: $WRAPPER_SRC"
        return 1
    fi

    # Compile the wrapper if it doesn't exist or source is newer
    if [ ! -f "$WRAPPER_SO" ] || [ "$WRAPPER_SRC" -nt "$WRAPPER_SO" ]; then
        echo "  Compiling DXC wrapper..."
        gcc -shared -fPIC -o "$WRAPPER_SO" "$WRAPPER_SRC" -ldl -Wl,--no-as-needed
        if [ $? -ne 0 ]; then
            echo "  [!] Failed to compile DXC wrapper"
            return 1
        fi
        echo "  [OK] Compiled $WRAPPER_SO"
    else
        echo "  [OK] DXC wrapper already compiled"
    fi

    # Setup the wrapper in game directory
    if [ -f "$GAME_DIR/libdxcompiler.so" ] && [ ! -L "$GAME_DIR/libdxcompiler.so" ]; then
        # Backup the original if it's a real file
        if [ ! -f "$GAME_DIR/libdxcompiler.so.real" ]; then
            mv "$GAME_DIR/libdxcompiler.so" "$GAME_DIR/libdxcompiler.so.real"
            echo "  [OK] Backed up original to libdxcompiler.so.real"
        fi
    fi

    # Only copy if source is newer than destination
    if [ ! -f "$GAME_DIR/libdxcompiler_wrapper.so" ] || [ "$WRAPPER_SO" -nt "$GAME_DIR/libdxcompiler_wrapper.so" ]; then
        cp "$WRAPPER_SO" "$GAME_DIR/libdxcompiler_wrapper.so"
        echo "  [OK] Copied wrapper to $GAME_DIR"
    else
        echo "  [SKIP] Wrapper already up to date in $GAME_DIR"
    fi

    # Only create symlink if it doesn't exist
    if [ ! -L "$GAME_DIR/libdxcompiler.so" ]; then
        ln -sf libdxcompiler_wrapper.so "$GAME_DIR/libdxcompiler.so"
        echo "  [OK] Created symlink in $GAME_DIR"
    else
        echo "  [SKIP] Symlink already exists in $GAME_DIR"
    fi

    # Setup the wrapper in bin directory if it exists
    if [ -d "$BIN_DIR" ]; then
        if [ -f "$BIN_DIR/libdxcompiler.so" ] && [ ! -L "$BIN_DIR/libdxcompiler.so" ]; then
            if [ ! -f "$BIN_DIR/libdxcompiler.so.real" ]; then
                mv "$BIN_DIR/libdxcompiler.so" "$BIN_DIR/libdxcompiler.so.real"
                echo "  [OK] Backed up original to $BIN_DIR/libdxcompiler.so.real"
            fi
        fi

        # Only copy if source is newer than destination
        if [ ! -f "$BIN_DIR/libdxcompiler_wrapper.so" ] || [ "$WRAPPER_SO" -nt "$BIN_DIR/libdxcompiler_wrapper.so" ]; then
            cp "$WRAPPER_SO" "$BIN_DIR/libdxcompiler_wrapper.so"
            echo "  [OK] Copied wrapper to $BIN_DIR"
        else
            echo "  [SKIP] Wrapper already up to date in $BIN_DIR"
        fi

        # Only create symlink if it doesn't exist
        if [ ! -L "$BIN_DIR/libdxcompiler.so" ]; then
            ln -sf libdxcompiler_wrapper.so "$BIN_DIR/libdxcompiler.so"
            echo "  [OK] Created symlink in $BIN_DIR"
        else
            echo "  [SKIP] Symlink already exists in $BIN_DIR"
        fi
    fi

    echo "  DXC wrapper setup complete"
}

# =============================================================================
# Function to cleanup all dotnet-related processes
# =============================================================================
cleanup_dotnet_processes() {
    echo "Cleaning up dotnet processes..."

    # Shutdown build servers gracefully first
    dotnet build-server shutdown 2>/dev/null || true

    # Give them time to terminate
    sleep 1

    # Kill any remaining build-related processes
    # Note: MSBuild.dll with /nodeReuse:true creates persistent worker nodes
    local patterns=(
        "VBCSCompiler"
        "MSBuild.dll"
        "dotnet.*watch"
        "dotnet.*restore"
    )

    for pattern in "${patterns[@]}"; do
        if pgrep -f "$pattern" > /dev/null 2>&1; then
            echo "  Killing processes matching: $pattern"
            pkill -f "$pattern" 2>/dev/null || true
        fi
    done

    # Final force kill if still running
    sleep 0.5
    for pattern in "${patterns[@]}"; do
        if pgrep -f "$pattern" > /dev/null 2>&1; then
            pkill -9 -f "$pattern" 2>/dev/null || true
        fi
    done
}

# Cleanup any stale processes from previous runs BEFORE starting
echo "Pre-build cleanup..."
cleanup_dotnet_processes

# =============================================================================
# Project-Local Wine Prefix Setup
# This creates a wine prefix in the project directory, not the system wine prefix.
# This keeps wine configuration isolated to this project.
# =============================================================================
mkdir -p $PWD/.wine
WINEPREFIX=$PWD/.wine

# =============================================================================
# Manual Setup Section
# The following commands are commented out and require manual one-time setup.
# These install system-level dependencies that cannot be automated by this script.
#
# MANUAL SETUP REQUIRED:
# 1. Install wine: https://www.winehq.org/
# 2. Install winetricks: https://wiki.winehq.org/Winetricks
# 3. Run the dotnet SDK installer via wine
# 4. Run winetricks to install required Windows components
# =============================================================================

download_if_missing https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.203/dotnet-sdk-10.0.203-win-x86.exe $PWD && \
 	wine dotnet-sdk-10.0.203-win-x86.exe /install /quiet


# winetricks -q powershell cmake mingw 7zip cabinet
# winetricks -q d3dxof dxdiag dxvk dxvk_async dxvk_nvapi

echo "Building..."
dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- build --config Developer
require_wine dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- build-shaders
require_wine dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- build-content

# Cleanup after build, before wine
echo "Post-build cleanup (before wine)..."
cleanup_dotnet_processes

#HACK: Currently Facepunch doesn't ship native binary for contentbuilder. Run this instead via wine.
require_wine game/bin/win64/contentbuilder.exe -b game

# Run DXC wrapper setup after build (needs libdxcompiler.so to exist first)
setup_dxc_wrapper

# Final cleanup after everything
echo "Final cleanup..."
cleanup_dotnet_processes

# Verify cleanup
echo "Verifying process cleanup..."
if pgrep -f "VBCSCompiler|MSBuild.dll" > /dev/null 2>&1; then
    echo "  [!] Warning: Some build server processes still running"
    pgrep -af "VBCSCompiler|MSBuild.dll" 2>/dev/null || true
else
    echo "  [OK] All build server processes cleaned up"
fi
