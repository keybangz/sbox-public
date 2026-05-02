#!/bin/bash
# s&box Linux Native Client Setup Script
# This script sets up the environment for running s&box on Linux
# WITHOUT requiring manual symlinks (the engine now handles case-insensitivity internally)
#
# NOTE: Case-sensitivity symlinks have been removed. The filesystem uses a case-insensitive
# ext4 overlay, so symlinks like assets->Assets are no longer needed (and would conflict).
# Library soname symlinks are still created (standard Linux library versioning).
# Resource and engine library path symlinks are still created (path resolution, not case).

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME_DIR="$(dirname "$SCRIPT_DIR")/game"
BIN_DIR="$GAME_DIR/bin/linuxsteamrt64"

echo "=========================================="
echo "s&box Linux Native Client Setup"
echo "=========================================="
echo ""

# Check if game directory exists
if [ ! -d "$GAME_DIR" ]; then
    echo "ERROR: Game directory not found at $GAME_DIR"
    exit 1
fi

# Check if running from correct location
if [ ! -f "$GAME_DIR/sbox.dll" ]; then
    echo "ERROR: sbox.dll not found in $GAME_DIR"
    echo "Make sure you have run the Docker build first: cd .. && ./sbox-public-linux-docker/sbox-install.sh compile ./sbox-keybangz/"
    exit 1
fi

echo "Game directory: $GAME_DIR"
echo "Binary directory: $BIN_DIR"
echo ""

# Function to check if a library exists
check_library() {
    local lib=$1
    local paths=("$BIN_DIR/$lib" "$GAME_DIR/$lib")
    for path in "${paths[@]}"; do
        if [ -f "$path" ]; then
            return 0
        fi
    done
    return 1
}

# Function to create soname symlinks for a directory
create_soname_symlinks() {
    local dir=$1
    echo "Creating library soname symlinks in $dir..."

    if [ ! -d "$dir" ]; then
        echo "  Directory not found, skipping"
        return
    fi

    cd "$dir"
    local count=0

    for lib in *.so *.so.*; do
        if [ -f "$lib" ]; then
            local soname=$(objdump -p "$lib" 2>/dev/null | grep SONAME | awk '{print $2}')
            if [ -n "$soname" ] && [ "$soname" != "$lib" ] && [ ! -e "$soname" ]; then
                ln -sf "$lib" "$soname"
                echo "  Created: $soname -> $lib"
                ((count++)) || true
            fi
        fi
    done

    cd - > /dev/null

    if [ $count -eq 0 ]; then
        echo "  All soname symlinks already exist"
    else
        echo "  Created $count soname symlinks"
    fi
}

# Function to find versioned library
find_versioned_lib() {
    local base=$1
    local dir=$2
    local result=$(find "$dir" -name "${base}*" -type f 2>/dev/null | head -1)
    echo "$result"
}

echo "Checking required libraries..."

# Check SkiaSharp
if ! check_library "libSkiaSharp.so"; then
    echo "  [!] libSkiaSharp.so not found"
    versioned=$(find_versioned_lib "libSkiaSharp.so" "$BIN_DIR")
    if [ -n "$versioned" ]; then
        echo "      Found versioned: $(basename "$versioned")"
    else
        echo "      WARNING: SkiaSharp library not found. Font rendering may fail."
    fi
else
    echo "  [OK] libSkiaSharp.so"
fi

# Check HarfBuzzSharp
if ! check_library "libHarfBuzzSharp.so"; then
    echo "  [!] libHarfBuzzSharp.so not found"
    versioned=$(find_versioned_lib "libHarfBuzzSharp.so" "$BIN_DIR")
    if [ -n "$versioned" ]; then
        echo "      Found versioned: $(basename "$versioned")"
    else
        echo "      WARNING: HarfBuzzSharp library not found. Font rendering may fail."
    fi
else
    echo "  [OK] libHarfBuzzSharp.so"
fi

# Check Steam API
if ! check_library "libsteam_api.so"; then
    echo "  [!] libsteam_api.so not found"
    echo "      WARNING: Steam API not found. Steam integration will fail."
else
    echo "  [OK] libsteam_api.so"
fi

# Check Vulkan renderer
if ! check_library "librendersystemvulkan.so"; then
    echo "  [!] librendersystemvulkan.so not found"
    echo "      WARNING: Vulkan renderer not found. Graphics will fail."
else
    echo "  [OK] librendersystemvulkan.so"
fi

# Check DXC compiler
if ! check_library "libdxcompiler.so"; then
    echo "  [!] libdxcompiler.so not found"
    versioned=$(find_versioned_lib "libdxcompiler.so" "$BIN_DIR")
    if [ -n "$versioned" ]; then
        echo "      Found versioned: $(basename "$versioned")"
    fi
else
    echo "  [OK] libdxcompiler.so"
fi

echo ""
echo "Creating library soname symlinks..."

# Create soname symlinks for libraries that need them
# These are REQUIRED for Linux shared library versioning (not case-sensitivity related)
create_soname_symlinks "$BIN_DIR"
create_soname_symlinks "$GAME_DIR"

echo ""
echo "Creating native engine resource symlinks..."

# The native C++ ResourceSystem expects resources at game root paths like:
# materials/, shaders/, decals/, textures/, etc.
# These files are actually in addons/base/Assets/
# Create symlinks from game root to the asset folders

create_resource_symlink() {
    local resource_dir=$1
    local game_link="$GAME_DIR/$resource_dir"
    local base_assets_dir="$GAME_DIR/addons/base/Assets/$resource_dir"

    if [ -d "$base_assets_dir" ] && [ ! -e "$game_link" ]; then
        ln -sf "addons/base/Assets/$resource_dir" "$game_link"
        echo "  Created: $resource_dir -> addons/base/Assets/$resource_dir"
    fi
}

# Resource directories that need to be accessible from game root
RESOURCE_DIRS=(
    "materials"
    "shaders"
    "decals"
    "textures"
    "models"
    "sounds"
    "fonts"
    "postprocess"
    "animgraphs"
    "prefabs"
    "surfaces"
    "templates"
    "ui"
    "maps"
)

cd "$GAME_DIR"
count=0
for dir in "${RESOURCE_DIRS[@]}"; do
    if [ -d "addons/base/Assets/$dir" ] && [ ! -e "$dir" ]; then
        ln -sf "addons/base/Assets/$dir" "$dir"
        echo "  Created: $dir -> addons/base/Assets/$dir"
        ((count++)) || true
    fi
done

if [ $count -eq 0 ]; then
    echo "  All resource symlinks already exist"
else
    echo "  Created $count resource symlinks"
fi

echo ""
echo "Creating engine library symlinks..."

# The native engine uses dlopen with paths like {gamedir}/libXXX.so
# Create symlinks from game root to bin/linuxsteamrt64 for required libraries
ENGINE_LIBS=(
    "libtier0.so"
    "libfilesystem_stdio.so"
    "libengine2.so"
    "liblocalize.so"
    "librendersystemvulkan.so"
    "libschemasystem.so"
    "libmaterialsystem2.so"
    "libvfx_vulkan.so"
    "libmeshsystem.so"
    "libanimationsystem.so"
    "libscenefilecache.so"
    "libphonon.so"
    "libresourcesystem.so"
    "libworldrenderer.so"
    "libscriptcompile.so"
    "libparticles.so"
    "libsoundsystem.so"
    "libnetworkingsockets.so"
    "libnetworksystem.so"
    "libsteamnetworkingsockets.so"
    "libpanorama.so"
    "libhost.so"
    "libpulse_system.so"
    "libvphysics2.so"
)

cd "$GAME_DIR"
count=0
for lib in "${ENGINE_LIBS[@]}"; do
    if [ -f "$BIN_DIR/$lib" ] && [ ! -e "$lib" ]; then
        ln -sf "bin/linuxsteamrt64/$lib" "$lib"
        echo "  Created: $lib -> bin/linuxsteamrt64/$lib"
        ((count++)) || true
    fi
done

if [ $count -eq 0 ]; then
    echo "  All engine library symlinks already exist"
else
    echo "  Created $count engine library symlinks"
fi

echo ""
echo "Setting up environment..."

# Set LD_LIBRARY_PATH
export LD_LIBRARY_PATH="$BIN_DIR:$GAME_DIR:${LD_LIBRARY_PATH:-}"
echo "  LD_LIBRARY_PATH set to include game directories"

# Make sbox executable
chmod +x "$GAME_DIR/sbox" 2>/dev/null || true
chmod +x "$GAME_DIR/sbox-dev" 2>/dev/null || true
chmod +x "$GAME_DIR/sbox-server" 2>/dev/null || true
chmod +x "$GAME_DIR/sbox-launcher" 2>/dev/null || true

echo ""
echo "=========================================="
echo "Setup complete!"
echo "=========================================="
echo ""
echo "To run s&box:"
echo "  cd $GAME_DIR"
echo "  ./sbox"
echo ""
echo "Or with this script (run.sh will be created):"

echo "  [OK] run.sh already exists at $SCRIPT_DIR/run.sh"
echo ""