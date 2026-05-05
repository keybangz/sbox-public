#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME_DIR="$(dirname "$SCRIPT_DIR")/game"
BIN_DIR="$GAME_DIR/bin/linuxsteamrt64"
export LD_LIBRARY_PATH="$BIN_DIR:$GAME_DIR:${LD_LIBRARY_PATH:-}"
export SBOX_BIN_DIR="$BIN_DIR"

# Force SDL3 to use X11 video driver — Wayland init fails silently causing render system
# to exit immediately after Vulkan device init. X11 keeps the engine alive.
export SDL_VIDEODRIVER=x11

# Disable dotnet background processes that can cause file locking issues
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1
export UseSharedCompilation=false
# Disable MSBuild node reuse (persistent worker processes)
export MSBUILDDISABLENODEREUSE=1

export VMOD="$GAME_DIR"

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

# OpenSSL 3.x provider initialization: force-init system libcrypto before engine2 loads
# Prevents crashes in EVP_KEYMGMT_is_a due to uninitialized provider store
if [ -f "$SCRIPT_DIR/interpose/libopenssl_init_interpose.so" ]; then
    export LD_PRELOAD="$SCRIPT_DIR/interpose/libopenssl_init_interpose.so:${LD_PRELOAD:-}"
fi

# OpenSSL symbol isolation: force RTLD_DEEPBIND on Valve engine libs
# Prevents libengine2.so's bundled OpenSSL from colliding with system libcrypto
if [ -f "$SCRIPT_DIR/interpose/libdeepbind_interpose.so" ]; then
    export LD_PRELOAD="$SCRIPT_DIR/interpose/libdeepbind_interpose.so:${LD_PRELOAD:-}"
fi

# Permissive free interpose: prevent SIGABRT from invalid free() calls
# (engine2 passes mmap'd/stack buffers to meshsystem which tries to free them)
if [ -f "$SCRIPT_DIR/interpose/libpermissive_free_interpose.so" ]; then
    export LD_PRELOAD="$SCRIPT_DIR/interpose/libpermissive_free_interpose.so:${LD_PRELOAD:-}"
fi

# SDL3 dynapi _REAL symbol redirect: intercept dlsym("SDL_Foo_REAL") -> dlsym("SDL_Foo")
# Fixes render system Init failure: rendersystemvulkan uses dlsym to load 1,251 SDL_*_REAL
# symbols which are hidden in all standard SDL3 builds.
if [ -f "$SCRIPT_DIR/interpose/libsdl3_dynapi_interpose.so" ]; then
    export LD_PRELOAD="$SCRIPT_DIR/interpose/libsdl3_dynapi_interpose.so:${LD_PRELOAD:-}"
fi

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
# Memory Debugging Options
# ============================================================================

# MALLOC_CHECK: Enhanced glibc malloc debugging
# Levels: 0=off, 1=print errors, 2=abort on error, 3=print+abort
# Usage: SBOX_MALLOC_CHECK=1 ./run.sh (or =2, =3)
if [ -n "$SBOX_MALLOC_CHECK" ]; then
    export MALLOC_CHECK_="$SBOX_MALLOC_CHECK"
    echo "Enabling MALLOC_CHECK_=$SBOX_MALLOC_CHECK (glibc heap debugging)"
    echo "  Level 1: Print error messages"
    echo "  Level 2: Abort immediately on error"
    echo "  Level 3: Print and abort on error"
    echo ""
fi

# GLIBC_TUNABLES for more aggressive malloc debugging
# Usage: SBOX_MALLOC_PERTURB=1 ./run.sh
if [ "$SBOX_MALLOC_PERTURB" = "1" ]; then
    # Fill freed memory with a pattern to detect use-after-free
    export MALLOC_PERTURB_=165
    echo "Enabling MALLOC_PERTURB_=165 (fills freed memory with 0xA5)"
    echo ""
fi

# ============================================================================
# Native library bind-mounts into /usr/share/dotnet/
# The .NET runtime's NativeLibrary.Load() searches /usr/share/dotnet/ first
# (the runtime install dir) before LD_LIBRARY_PATH. We can't write there as
# a normal user, but we CAN use unprivileged user+mount namespaces to
# bind-mount our libs into that path without root.
#
# We create placeholder files in a tmpdir, bind-mount each one over the
# missing path inside a private mount namespace, then exec the engine inside
# that namespace so it sees the libs at the expected path.
# ============================================================================

# Libs the native engine requests from /usr/share/dotnet/
DOTNET_LIBS=(
    "librendersystemvulkan.so"
    "librendersystemempty.so"
)

# Check if we're already inside the prepared namespace (avoid re-entering)
if [ -z "${SBOX_NS_READY:-}" ]; then
    echo "[ns] Entering private mount namespace for dotnet lib injection..."

    # Build a tmpdir with the libs the .NET runtime looks for in /usr/share/dotnet/
    NS_TMPDIR=$(mktemp -d /tmp/sbox_dotnet_ns.XXXXXX)
    for LIB in librendersystemvulkan.so librendersystemempty.so; do
        SRC="$BIN_DIR/$LIB"
        if [ -f "$SRC" ]; then
            ln -sf "$SRC" "$NS_TMPDIR/$LIB"
            echo "[ns] staged: $LIB"
        fi
    done

    # Use overlayfs: lower=real /usr/share/dotnet, upper=our tmpdir with extra libs
    # This makes /usr/share/dotnet/ appear to contain our libs without touching the real dir.
    OVERLAY_WORK=$(mktemp -d /tmp/sbox_dotnet_work.XXXXXX)
    OVERLAY_MERGED=$(mktemp -d /tmp/sbox_dotnet_merged.XXXXXX)

    export SBOX_NS_READY=1
    export NS_TMPDIR OVERLAY_WORK OVERLAY_MERGED SCRIPT_DIR

    exec unshare --user --mount --map-root-user \
        bash "$SCRIPT_DIR/ns_launch.sh" "$@"
fi

# ============================================================================
# Launch
# ============================================================================

# Use the native AppHost binary (game/sbox) which runs Startup.Main() ->
# LauncherEnvironment.Init() -> GameAppSystem.Run() (managed loop).
# LauncherEnvironment.Init() sets SBOX_BIN_DIR, LD_LIBRARY_PATH, and CurrentDirectory
# before any native engine libs are loaded, so the native engine can find bin/linuxsteamrt64/.
#
# Note: We use regular invocation instead of 'exec' so our EXIT trap runs
# for cleanup when the process terminates.

# Valgrind memory debugging: SBOX_VALGRIND=1 ./run.sh
# Additional options:
#   SBOX_VALGRIND_ARGS="--leak-check=full" - extra Valgrind arguments
#   SBOX_VALGRIND_LOG="/tmp/valgrind.log" - log to file instead of stderr
#   SBOX_VALGRIND_TOOL="memcheck" - tool to use (default: memcheck)
if [ "$SBOX_VALGRIND" = "1" ]; then
    echo "=========================================="
    echo "Running s&box under Valgrind"
    echo "=========================================="
    echo ""

    # Check if valgrind is installed
    if ! command -v valgrind &> /dev/null; then
        echo "ERROR: Valgrind is not installed!"
        echo "Install with: sudo apt install valgrind"
        exit 1
    fi

    # Valgrind tool selection (default: memcheck)
    VALGRIND_TOOL="${SBOX_VALGRIND_TOOL:-memcheck}"

    echo "Valgrind tool: $VALGRIND_TOOL"
    echo ""
    echo "Common tools:"
    echo "  memcheck   - Memory error detector (default)"
    echo "  massif     - Heap profiler"
    echo "  callgrind  - Call graph profiler"
    echo "  helgrind   - Thread error detector"
    echo ""

    # Build Valgrind arguments
    VALGRIND_ARGS=(
        --tool="$VALGRIND_TOOL"
        # Track child processes (important for .NET which may fork)
        --trace-children=yes
        # Show origins of uninitialized values
        --track-origins=yes
        # Increase error limit
        --error-limit=no
        # More precise (but slower) leak checking
        --leak-check=full
        --show-leak-kinds=all
        # Track file descriptors
        --track-fds=yes
        # Suppressions for known .NET runtime issues
        --gen-suppressions=all
    )

    # Log to file if specified
    if [ -n "$SBOX_VALGRIND_LOG" ]; then
        VALGRIND_ARGS+=(--log-file="$SBOX_VALGRIND_LOG")
        echo "Logging to: $SBOX_VALGRIND_LOG"
    else
        echo "Logging to: stderr (use SBOX_VALGRIND_LOG=/path/to/log.txt to log to file)"
    fi

    # Add user-specified Valgrind arguments
    if [ -n "$SBOX_VALGRIND_ARGS" ]; then
        # shellcheck disable=SC2206
        VALGRIND_ARGS+=($SBOX_VALGRIND_ARGS)
    fi

    echo ""
    echo "Running Valgrind (this will be SLOW - 10-50x slower than normal)..."
    echo "Press Ctrl+C to stop."
    echo ""

    # Enable MALLOC_CHECK for additional heap validation
    export MALLOC_CHECK_=3

    # Run with Valgrind - instrument the native AppHost binary
    valgrind "${VALGRIND_ARGS[@]}" "$GAME_DIR/sbox" -game "$GAME_DIR" "$@"
    EXIT_CODE=$?

# GDB debugging: SBOX_GDB=1 ./run.sh
# Additional options:
#   SBOX_GDB_ARGS="-ex 'set follow-fork-mode child'" - extra GDB arguments
#   SBOX_GDB_BATCH=1 - run in batch mode (for automated debugging/core dumps)
elif [ "$SBOX_GDB" = "1" ]; then
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
        # shellcheck disable=SC2206
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
    gdb "${GDB_ARGS[@]}" --args "$GAME_DIR/sbox" -game "$GAME_DIR" "$@"
    EXIT_CODE=$?

# AddressSanitizer: SBOX_ASAN=1 ./run.sh
# Note: Requires ASAN runtime library to be available
elif [ "$SBOX_ASAN" = "1" ]; then
    echo "=========================================="
    echo "Running s&box with AddressSanitizer"
    echo "=========================================="
    echo ""

    # Find ASAN library
    ASAN_LIB=""
    for lib in /usr/lib/x86_64-linux-gnu/libasan.so.8 \
               /usr/lib/x86_64-linux-gnu/libasan.so.6 \
               /usr/lib/x86_64-linux-gnu/libasan.so; do
        if [ -f "$lib" ]; then
            ASAN_LIB="$lib"
            break
        fi
    done

    if [ -z "$ASAN_LIB" ]; then
        echo "ERROR: AddressSanitizer library not found!"
        echo "Install with: sudo apt install libasan8 (or libasan6)"
        exit 1
    fi

    echo "Using ASAN library: $ASAN_LIB"
    echo ""

    # ASAN options
    export ASAN_OPTIONS="detect_leaks=0:halt_on_error=0:print_stats=1:log_path=/tmp/sbox_asan"
    export LD_PRELOAD="$ASAN_LIB:${LD_PRELOAD:-}"

    echo "ASAN log: /tmp/sbox_asan.*"
    echo ""

    # Use the native AppHost binary under ASAN
    "$GAME_DIR/sbox" -game "$GAME_DIR" "$@"
    EXIT_CODE=$?

else
    # Use the native AppHost binary - it runs Startup.Main() -> LauncherEnvironment.Init()
    # -> GameAppSystem.Run() (managed loop). LauncherEnvironment.Init() now sets SBOX_BIN_DIR
    # so the native engine can find bin/linuxsteamrt64/ without needing run.sh to set it.
    "$GAME_DIR/sbox" -game "$GAME_DIR" "$@"
    EXIT_CODE=$?
fi

# Cleanup is handled by the EXIT trap
exit $EXIT_CODE
