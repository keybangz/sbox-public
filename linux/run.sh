#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME_DIR="$(dirname "$SCRIPT_DIR")/game"
BIN_DIR="$GAME_DIR/bin/linuxsteamrt64"
export LD_LIBRARY_PATH="$BIN_DIR:$GAME_DIR:${LD_LIBRARY_PATH:-}"
cd "$GAME_DIR"

# Use dotnet to run the managed launcher instead of the native executable.
# The native executable takes over the main loop and doesn't return control
# to managed code for async task processing, causing initialization to hang.
# Running via dotnet allows managed code to control the main loop properly.
exec dotnet sbox.dll "$@"
