#!/bin/bash

# Setup the DXC wrapper for Linux
# The wrapper converts UTF-16 arguments to UTF-32 for Linux compatibility
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

    # Copy wrapper and create symlink in game directory
    cp "$WRAPPER_SO" "$GAME_DIR/libdxcompiler_wrapper.so"
    ln -sf libdxcompiler_wrapper.so "$GAME_DIR/libdxcompiler.so"
    echo "  [OK] Installed DXC wrapper in $GAME_DIR"

    # Setup the wrapper in bin directory if it exists
    if [ -d "$BIN_DIR" ]; then
        if [ -f "$BIN_DIR/libdxcompiler.so" ] && [ ! -L "$BIN_DIR/libdxcompiler.so" ]; then
            if [ ! -f "$BIN_DIR/libdxcompiler.so.real" ]; then
                mv "$BIN_DIR/libdxcompiler.so" "$BIN_DIR/libdxcompiler.so.real"
                echo "  [OK] Backed up original to $BIN_DIR/libdxcompiler.so.real"
            fi
        fi
        cp "$WRAPPER_SO" "$BIN_DIR/libdxcompiler_wrapper.so"
        ln -sf libdxcompiler_wrapper.so "$BIN_DIR/libdxcompiler.so"
        echo "  [OK] Installed DXC wrapper in $BIN_DIR"
    fi

    echo "  DXC wrapper setup complete"
}

# Force rebuild by removing bin/obj directories for key projects
echo "Cleaning previous build artifacts..."
rm -rf ./engine/Sandbox.Engine/bin ./engine/Sandbox.Engine/obj
rm -rf ./engine/Sandbox.Menu/bin ./engine/Sandbox.Menu/obj
rm -rf ./engine/Sandbox.Compiling/bin ./engine/Sandbox.Compiling/obj
rm -rf ./engine/Sandbox.Services/bin ./engine/Sandbox.Services/obj

# Disable dotnet telemetry and build server caching to prevent background processes
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
# Disable build servers entirely - they cause file locking issues
export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1
export UseSharedCompilation=false
# Disable MSBuild node reuse (persistent worker processes)
export MSBUILDDISABLENODEREUSE=1

# Function to cleanup all dotnet-related processes
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

mkdir -p $PWD/.wine
WINEPREFIX=$PWD/.wine

# wget https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.203/dotnet-sdk-10.0.203-win-x86.exe && \
# 	wine dotnet-sdk-10.0.203-win-x86.exe /install /quiet && \
# 	rm dotnet-sdk-10.0.203-win-x86.exe

# winetricks -q powershell cmake mingw 7zip cabinet
# winetricks -q d3dxof dxdiag dxvk dxvk_async dxvk_nvapi

echo "Building..."
dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- build --config Developer
wine dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- build-shaders
wine dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- build-content

# Cleanup after build, before wine
echo "Post-build cleanup (before wine)..."
cleanup_dotnet_processes

#HACK: Currently Facepunch doesn't ship native binary for contentbuilder. Run this instead via wine.
wine game/bin/win64/contentbuilder.exe -b game


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
