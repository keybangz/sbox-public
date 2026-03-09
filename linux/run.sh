#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME_DIR="$(dirname "$SCRIPT_DIR")/game"
BIN_DIR="$GAME_DIR/bin/linuxsteamrt64"
export LD_LIBRARY_PATH="$BIN_DIR:$GAME_DIR:${LD_LIBRARY_PATH:-}"
cd "$GAME_DIR"

# ============================================================================
# Interpose Libraries (optional debugging tools)
# ============================================================================

# Thread/library tracking: SBOX_INTERPOSE=1 ./run.sh
if [ "$SBOX_INTERPOSE" = "1" ] && [ -f "$SCRIPT_DIR/interpose/libsbox_interpose.so" ]; then
    echo "Loading interpose library (thread/library tracking)..."
    export LD_PRELOAD="$SCRIPT_DIR/interpose/libsbox_interpose.so:${LD_PRELOAD:-}"
    echo "  Log: /tmp/sbox_interpose.log"
fi

# Audio/FFmpeg tracking: SBOX_AUDIO_DEBUG=1 ./run.sh
if [ "$SBOX_AUDIO_DEBUG" = "1" ] && [ -f "$SCRIPT_DIR/interpose/libaudio_interpose.so" ]; then
    echo "Loading audio interpose library..."
    export LD_PRELOAD="$SCRIPT_DIR/interpose/libaudio_interpose.so:${LD_PRELOAD:-}"
    echo "  Log: /tmp/sbox_audio.log"
fi

# Vulkan/SDL3 GPU tracking: SBOX_VULKAN_DEBUG=1 ./run.sh
if [ "$SBOX_VULKAN_DEBUG" = "1" ] && [ -f "$SCRIPT_DIR/interpose/libvulkan_interpose.so" ]; then
    echo "Loading Vulkan interpose library..."
    export LD_PRELOAD="$SCRIPT_DIR/interpose/libvulkan_interpose.so:${LD_PRELOAD:-}"
    echo "  Log: /tmp/sbox_vulkan.log"
fi

# Frame hook (swap chain fix): SBOX_FRAME_HOOK=1 ./run.sh
if [ "$SBOX_FRAME_HOOK" = "1" ] && [ -f "$SCRIPT_DIR/interpose/libframe_hook.so" ]; then
    echo "Loading frame hook (swap chain delay)..."
    export LD_PRELOAD="$SCRIPT_DIR/interpose/libframe_hook.so:${LD_PRELOAD:-}"
    echo "  Log: /tmp/sbox_frame_hook.log"
fi

# ============================================================================
# Launch
# ============================================================================

# Use dotnet to run the managed launcher instead of the native executable.
# The native executable takes over the main loop and doesn't return control
# to managed code for async task processing, causing initialization to hang.
# Running via dotnet allows managed code to control the main loop properly.
exec dotnet sbox.dll "$@"
