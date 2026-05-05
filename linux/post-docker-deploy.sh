#!/bin/bash
# post-docker-deploy.sh
# Run this after every Docker build to restore patched files that Docker overwrites.
#
# Docker build command:
#   cd .. && ./sbox-public-linux-docker/sbox-install.sh compile ./sbox-keybangz/
#
# This script:
#   1. Rebuilds the thin DXC wrapper shim (16KB) and deploys it
#   2. Rebuilds patched managed DLLs (Sandbox.AppSystem, Sandbox.Engine) and deploys them

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
ENGINE_DIR="$REPO_ROOT/engine"
MANAGED_DIR="$REPO_ROOT/game/bin/managed"
BIN_DIR="$REPO_ROOT/game/bin/linuxsteamrt64"

echo "=========================================="
echo "s&box Post-Docker Redeployment"
echo "=========================================="
echo ""

# --- Step 1: Rebuild and deploy DXC thin shim ---
echo "[1/3] Rebuilding DXC wrapper shim..."
cd "$SCRIPT_DIR"
make -B
echo "  [OK] libdxcompiler_wrapper.so rebuilt ($(du -sh libdxcompiler_wrapper.so | cut -f1))"

echo "  Deploying to $BIN_DIR..."
cp -f libdxcompiler_wrapper.so "$BIN_DIR/libdxcompiler_wrapper.so"
# Ensure symlink exists: libdxcompiler.so -> libdxcompiler_wrapper.so
if [ ! -L "$BIN_DIR/libdxcompiler.so" ]; then
    ln -sf libdxcompiler_wrapper.so "$BIN_DIR/libdxcompiler.so"
    echo "  [OK] Created symlink libdxcompiler.so -> libdxcompiler_wrapper.so"
fi
# Ensure symlink exists: libsteam_api64.so -> libsteam_api.so
# Required because .NET PreJit resolves DllImport("steam_api64") before our
# custom DLLImportResolver can remap the name. LD_LIBRARY_PATH includes BIN_DIR
# so the OS loader finds libsteam_api64.so here at startup.
if [ ! -L "$BIN_DIR/libsteam_api64.so" ]; then
    ln -sf libsteam_api.so "$BIN_DIR/libsteam_api64.so"
    echo "  [OK] Created symlink libsteam_api64.so -> libsteam_api.so"
fi
echo "  [OK] DXC shim deployed ($(du -sh "$BIN_DIR/libdxcompiler_wrapper.so" | cut -f1))"

# --- Step 2: Rebuild and deploy Sandbox.AppSystem.dll ---
echo ""
echo "[2/3] Rebuilding Sandbox.AppSystem.dll..."
cd "$ENGINE_DIR"
dotnet build Sandbox.AppSystem/Sandbox.AppSystem.csproj -c Debug --nologo -v quiet
SRC="$ENGINE_DIR/Sandbox.AppSystem/obj/Debug/Sandbox.AppSystem.dll"
cp -f "$SRC" "$MANAGED_DIR/Sandbox.AppSystem.dll"
echo "  [OK] Sandbox.AppSystem.dll deployed ($(du -sh "$MANAGED_DIR/Sandbox.AppSystem.dll" | cut -f1))"

# --- Step 3: Rebuild and deploy Sandbox.Engine.dll ---
echo ""
echo "[3/3] Rebuilding Sandbox.Engine.dll..."
dotnet build Sandbox.Engine/Sandbox.Engine.csproj -c Debug --nologo -v quiet
SRC="$ENGINE_DIR/Sandbox.Engine/obj/Debug/Sandbox.Engine.dll"
cp -f "$SRC" "$MANAGED_DIR/Sandbox.Engine.dll"
echo "  [OK] Sandbox.Engine.dll deployed ($(du -sh "$MANAGED_DIR/Sandbox.Engine.dll" | cut -f1))"

echo ""
echo "=========================================="
echo "Redeployment complete. Run the game with:"
echo "  cd linux && ./run.sh"
echo "=========================================="