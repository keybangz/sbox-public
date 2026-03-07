#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME_DIR="$(dirname "$SCRIPT_DIR")/game"
BIN_DIR="$GAME_DIR/bin/linuxsteamrt64"
export LD_LIBRARY_PATH="$BIN_DIR:$GAME_DIR:${LD_LIBRARY_PATH:-}"
cd "$GAME_DIR"
exec ./sbox "$@"
