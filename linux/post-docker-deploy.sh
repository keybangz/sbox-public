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
echo "[1/4] Rebuilding DXC wrapper shim..."
cd "$SCRIPT_DIR"
make -B
echo "  [OK] libdxcompiler_wrapper.so rebuilt ($(du -sh libdxcompiler_wrapper.so | cut -f1))"

# echo "  Deploying to $BIN_DIR..."
# cp -f libdxcompiler_wrapper.so "$BIN_DIR/libdxcompiler_wrapper.so"
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

# --- Step 2: Build libsbox_init.so and patch game/sbox ---
# libsbox_init.so combines OpenSSL provider init + permissive free() validation
# + Wayland→SDL3 input injection (so engine2's embedded SDL receives keyboard
# and mouse events when running on a native Wayland compositor).
# It is injected as a DT_NEEDED dependency of game/sbox via patchelf so it loads
# automatically on every launch — no LD_PRELOAD or wrapper script required.
# This step must re-run after every Docker build because Docker overwrites game/sbox.
echo ""
echo "[2/4] Building libsbox_init.so and patching game/sbox..."

# Build the combined init library
cd "$SCRIPT_DIR/interpose"
g++ -shared -fPIC -O2 -Wall -Wextra -std=c++17 -o libsbox_init.so \
    sbox_init.cpp wayland_input.cpp \
    -ldl -lpthread -lwayland-client -lxkbcommon
echo "  [OK] libsbox_init.so built ($(du -sh libsbox_init.so | cut -f1))"

# Deploy to bin dir so the loader finds it via RPATH
cp -f libsbox_init.so "$BIN_DIR/libsbox_init.so"
echo "  [OK] libsbox_init.so deployed to $BIN_DIR"

# Patch game/sbox:
#   - Add libsbox_init.so as DT_NEEDED (loaded before any engine lib)
#   - Set RPATH to $ORIGIN/bin/linuxsteamrt64 so the loader finds it
SBOX_BIN="$REPO_ROOT/game/sbox"
if ! command -v patchelf &>/dev/null; then
    echo "  [WARN] patchelf not found — skipping game/sbox patch (install with: sudo apt install patchelf)"
else
    # Remove stale entry if present (idempotent), then add fresh
    patchelf --remove-needed libsbox_init.so "$SBOX_BIN" 2>/dev/null || true
    patchelf --add-needed libsbox_init.so "$SBOX_BIN"
    # Set RPATH so the loader finds libsbox_init.so relative to the sbox binary
    patchelf --set-rpath '$ORIGIN/bin/linuxsteamrt64' "$SBOX_BIN"
    echo "  [OK] game/sbox patched: DT_NEEDED=libsbox_init.so, RPATH=\$ORIGIN/bin/linuxsteamrt64"
fi

# --- Step 3: Rebuild and deploy Sandbox.AppSystem.dll ---
echo ""
echo "[3/4] Rebuilding Sandbox.AppSystem.dll..."
cd "$ENGINE_DIR"
dotnet build Sandbox.AppSystem/Sandbox.AppSystem.csproj -c Debug --nologo -v quiet
SRC="$ENGINE_DIR/Sandbox.AppSystem/obj/Debug/Sandbox.AppSystem.dll"
cp -f "$SRC" "$MANAGED_DIR/Sandbox.AppSystem.dll"
echo "  [OK] Sandbox.AppSystem.dll deployed ($(du -sh "$MANAGED_DIR/Sandbox.AppSystem.dll" | cut -f1))"

# --- Step 4: Rebuild and deploy Sandbox.Engine.dll ---
echo ""
echo "[4/4] Rebuilding Sandbox.Engine.dll..."
dotnet build Sandbox.Engine/Sandbox.Engine.csproj -c Debug --nologo -v quiet
SRC="$ENGINE_DIR/Sandbox.Engine/obj/Debug/Sandbox.Engine.dll"
cp -f "$SRC" "$MANAGED_DIR/Sandbox.Engine.dll"
echo "  [OK] Sandbox.Engine.dll deployed ($(du -sh "$MANAGED_DIR/Sandbox.Engine.dll" | cut -f1))"

echo ""
echo "=========================================="
echo "Redeployment complete. Run the game with:"
echo "  ./game/sbox -game ./game"
echo "=========================================="
