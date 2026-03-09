#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME_DIR="$(dirname "$SCRIPT_DIR")/game"
BIN_DIR="$GAME_DIR/bin/linuxsteamrt64"
export LD_LIBRARY_PATH="$BIN_DIR:$GAME_DIR:${LD_LIBRARY_PATH:-}"

# Disable dotnet background processes that can cause file locking issues
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1
export UseSharedCompilation=false
# Disable MSBuild node reuse (persistent worker processes)
export MSBUILDDISABLENODEREUSE=1

cd "$GAME_DIR"

# Store our process group for cleanup
SBOX_PID=$$

# ============================================================================
# Cleanup function for dotnet processes
# ============================================================================
cleanup_processes() {
    echo ""
    echo "Cleaning up processes..."

    # Shutdown dotnet build servers
    dotnet build-server shutdown 2>/dev/null || true

    # Kill any orphaned dotnet processes spawned by this session
    # Note: MSBuild.dll with /nodeReuse:true creates persistent worker nodes
    local patterns=(
        "VBCSCompiler"
        "MSBuild.dll"
    )

    for pattern in "${patterns[@]}"; do
        pkill -f "$pattern" 2>/dev/null || true
    done

    echo "Cleanup complete."
}

# Set up signal handlers for clean exit
# Note: EXIT trap runs cleanup, INT/TERM just need to exit (EXIT will handle cleanup)
trap cleanup_processes EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

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

# Managed texture debug logging: SBOX_TEXTURE_DEBUG=1 ./run.sh
if [ "$SBOX_TEXTURE_DEBUG" = "1" ]; then
    echo "Enabling managed texture debug logging..."
    export SBOX_TEXTURE_DEBUG=1
    echo "  Texture load/dispose events will be logged to console"
fi

# Memory debug (resource tracking): SBOX_MEMORY_DEBUG=1 ./run.sh
if [ "$SBOX_MEMORY_DEBUG" = "1" ] && [ -f "$SCRIPT_DIR/interpose/libmemory_interpose.so" ]; then
    echo "Loading memory interpose library (resource tracking)..."
    export LD_PRELOAD="$SCRIPT_DIR/interpose/libmemory_interpose.so:${LD_PRELOAD:-}"
    echo "  Log: /tmp/sbox_memory.log"
fi

# Native texture tracking: SBOX_NATIVE_TEXTURE_DEBUG=1 ./run.sh
if [ "$SBOX_NATIVE_TEXTURE_DEBUG" = "1" ] && [ -f "$SCRIPT_DIR/interpose/libtexture_interpose.so" ]; then
    echo "Loading native texture interpose library..."
    export LD_PRELOAD="$SCRIPT_DIR/interpose/libtexture_interpose.so:${LD_PRELOAD:-}"
    echo "  Log: /tmp/sbox_native_texture.log"
fi

# Scene rendering tracking: SBOX_SCENE_DEBUG=1 ./run.sh
if [ "$SBOX_SCENE_DEBUG" = "1" ] && [ -f "$SCRIPT_DIR/interpose/libscene_interpose.so" ]; then
    echo "Loading scene interpose library..."
    export LD_PRELOAD="$SCRIPT_DIR/interpose/libscene_interpose.so:${LD_PRELOAD:-}"
    echo "  Log: /tmp/sbox_scene.log"
fi

# Minimal avatar scene for debugging: SBOX_MINIMAL_AVATAR=1 ./run.sh
if [ "$SBOX_MINIMAL_AVATAR" = "1" ]; then
    echo "Using minimal avatar scene for debugging..."
    export SBOX_MINIMAL_AVATAR=1
fi

# ============================================================================
# Launch
# ============================================================================

# Use dotnet to run the managed launcher instead of the native executable.
# The native executable takes over the main loop and doesn't return control
# to managed code for async task processing, causing initialization to hang.
# Running via dotnet allows managed code to control the main loop properly.
#
# Note: We use regular invocation instead of 'exec' so our EXIT trap runs
# for cleanup when the process terminates.

# GDB debugging: SBOX_GDB=1 ./run.sh
# Additional options:
#   SBOX_GDB_ARGS="-ex 'set follow-fork-mode child'" - extra GDB arguments
#   SBOX_GDB_BATCH=1 - run in batch mode (for automated debugging/core dumps)
if [ "$SBOX_GDB" = "1" ]; then
    echo "=========================================="
    echo "Running s&box under GDB"
    echo "=========================================="
    echo ""
    echo "Useful GDB commands:"
    echo "  run              - Start the program"
    echo "  bt               - Backtrace after crash"
    echo "  bt full          - Full backtrace with locals"
    echo "  info threads     - List all threads"
    echo "  thread <n>       - Switch to thread n"
    echo "  continue         - Continue execution"
    echo "  quit             - Exit GDB"
    echo ""

    # Build GDB arguments
    GDB_ARGS=(
        # Don't stop on signals commonly used by .NET runtime
        # SIG33-SIG36 are used for thread synchronization, garbage collection, etc.
        -ex "handle SIGXCPU SIG33 SIG34 SIG35 SIG36 SIGPWR nostop noprint"
        # Set follow-fork-mode to child to debug forked processes
        -ex "set follow-fork-mode child"
        # Disable pagination for long output
        -ex "set pagination off"
        # Load .NET SOS debugging extension if available
        -ex "set auto-load safe-path /"
    )

    # Add user-specified GDB arguments
    if [ -n "$SBOX_GDB_ARGS" ]; then
        GDB_ARGS+=($SBOX_GDB_ARGS)
    fi

    # Batch mode for automated debugging (e.g., getting backtraces from crashes)
    if [ "$SBOX_GDB_BATCH" = "1" ]; then
        GDB_ARGS+=(
            -batch
            -ex "run"
            -ex "bt full"
            -ex "info threads"
            -ex "thread apply all bt"
        )
        echo "Running in batch mode..."
        echo ""
    fi

    # Run with GDB
    gdb "${GDB_ARGS[@]}" --args dotnet sbox.dll "$@"
    EXIT_CODE=$?
else
    dotnet sbox.dll "$@"
    EXIT_CODE=$?
fi

# Cleanup is handled by the EXIT trap
exit $EXIT_CODE
