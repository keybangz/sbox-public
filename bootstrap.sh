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

dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- build --config Developer
dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- build-shaders

#HACK: Currently Facepunch doesn't ship native binary for contentbuilder. Run this instead via wine.
wine game/bin/win64/contentbuilder.exe -b game

# Run DXC wrapper setup after build (needs libdxcompiler.so to exist first)
setup_dxc_wrapper
