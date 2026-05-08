#!/bin/bash

set -e

SCRIPT_DIR="$(dirname "$(realpath -- "$0")")"
GAME_DIR="$SCRIPT_DIR/game"
BIN_DIR="$GAME_DIR/bin/linuxsteamrt64"
LINUX_DIR="$SCRIPT_DIR/linux"
SBOX_EXE="$GAME_DIR/sbox"

if [ ! -d "$GAME_DIR" ]; then
	echo "ERROR: Game directory not found at $GAME_DIR"
	exit 1
fi

# Check if running from correct location
if [ ! -f "$GAME_DIR/sbox.dll" ]; then
	echo "ERROR: sbox.dll not found in $GAME_DIR"
	echo "Make sure you have run the Docker build first: cd .. && ./sbox-public"
	exit 1
fi

echo 'Building linux native shared libraries...'

cd "$LINUX_DIR"

make clean
make
cd "$SCRIPT_DIR"

echo "Game directory: $GAME_DIR"
echo "Binary directory: $BIN_DIR"
echo ""

echo "Linking library librendersystemvulkan.so to $GAME_DIR..."
ln -sf "$BIN_DIR/librendersystemvulkan.so" "$GAME_DIR/librendersystemvulkan.so"

echo "Linking wrapper DXC wrapper to $GAME_DIR"
ln -sf "$BIN_DIR/libdxcompiler.so" "$GAME_DIR/libdxcompiler.so.real"
ln -sf "$LINUX_DIR/libdxcompiler_wrapper.so" "$GAME_DIR/libdxcompiler.so"

# echo "Linking OpenSSL intercept library to $GAME_DIR"
# ln -sf "$LINUX_DIR/libsbox_init.so" "$GAME_DIR/libsbox_init.so"

echo "Using 'patchelf' to add OpenSSL intercept library as requirement for $SBOX_EXE"
patchelf --remove-needed "$LINUX_DIR/libsbox_init.so" "$SBOX_EXE"
patchelf --add-needed "$LINUX_DIR/libsbox_init.so" "$SBOX_EXE"
