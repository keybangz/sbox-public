# 🐧 s&box Linux Native Client

**Branch:** `linux-native-client`
**Status:** ✅ STABLE
**Purpose:** Native Linux client for s&box (no Wine/Proton required)

<img width="1911" height="1072" alt="Screenshot" src="https://github.com/user-attachments/assets/4411404f-be8a-4b1e-afa4-1f3b7d23f80f" />

---

## 1. Project Identity

This is the **s&box Linux Native Client** fork, maintained on the `linux-native-client` branch. The parent project ([Facepunch/sbox-public](https://github.com/Facepunch/sbox-public)) does not ship a native Linux client. This fork exists to fill that gap.

**What this fork provides:**
- Native Linux client for s&box (Source 2 game engine)
- No Wine, no Proton — runs directly on Linux
- Linux-specific interpose system for bridging managed C# to native engine libs
- SDL3/X11/Wayland input handling without dependency on Valve's Windows-only binaries

---

## 2. What This Branch Is

The `linux-native-client` branch adds Linux platform support to the s&box engine. Key changes from upstream:

| Component | Change |
|-----------|--------|
| **Input system** | SDL3/X11/Wayland hybrid input polling — direct P/Invoke to `libSDL3.so` |
| **Native interop** | Delegate-based function pointers instead of `UnmanagedCallersOnly` (GC-safe on Linux) |
| **Library resolution** | `DLLImportResolver` handles all native library loading (`steam_api64` → `libsteam_api.so`) |
| **DXC wrapper** | 16KB shim converting UTF-16 → UTF-32 for Linux DXC compatibility |
| **Performance** | Texture load queue, time budgets, DNS caching, HTTP optimization |
| **Filesystem** | Case-insensitive ext4 overlay for Windows path compatibility |
| **Input capture** | X11 cursor warp with synthetic event filtering; Wayland uses SDL pointer constraints |

**Why this fork exists:** Facepunch does not ship a native Linux client for s&box. This fork bridges the managed C# engine to Valve's native Linux binaries using an LD_PRELOAD interpose layer.

---

## 3. Build Instructions

### Docker-based Full Compile

The official build uses a Docker container with a Wine-based Windows toolchain to compile the managed C# code.

```bash
# Full compile — downloads game files, builds managed code
# SBOX_SKIP_ARTIFACTS=1 skips incremental checks, forces full rebuild
SBOX_SKIP_ARTIFACTS=1 ./sbox-public-linux-docker/sbox-install.sh compile ./
```

### Post-Deploy (MANDATORY after every build)

Docker's install process overwrites patched files. **You must** run this after every Docker build:

```bash
./linux/post-docker-deploy.sh
```

| Patched File | Docker's Version | Our Patched Version |
|--------------|------------------|---------------------|
| `libdxcompiler.so` | 37MB broken DXC | 16KB thin shim wrapper |
| `Sandbox.AppSystem.dll` | Broken SDL window registration | `RegisterWindowWithSDL` + `SetEngineState` fixes |
| `Sandbox.Engine.dll` | Runtime issues | `MainThread.Wait`, `Panel.Layer` guard, downloads folder fix |

### Alternative Rebuild Script

```bash
./linux/rebuild.sh
```

This wraps the Docker build + post-deploy in one command.

---

## 4. Run Instructions

### Standard Launch

```bash
cd linux && ./run.sh
```

### Debug Environment Variables

| Variable | Purpose |
|----------|---------|
| `SDL_VIDEODRIVER=x11` | Force X11 video driver (Wayland init fails silently) |
| `SBOX_VALGRIND=1` | Run under Valgrind memory debugger |
| `SBOX_GDB=1` | Run under GDB debugger |
| `SBOX_ASAN=1` | Run with AddressSanitizer |
| `SBOX_INTERPOSE=1` | Enable interpose library debugging |
| `SBOX_AUDIO_DEBUG=1` | Enable audio system debug output |
| `SBOX_INPUT_TRACE=1` | Trace input events (Linux only) |
| `SBOX_DEBUG=1` | General debug logging |

### Key LD_PRELOAD Stacks (documented in `run.sh`)

```
libopenssl_init_interpose.so     — Force-init libcrypto before engine2
libdeepbind_interpose.so       — RTLD_DEEPBIND on Valve engine libs
libpermissive_free_interpose.so — Prevent SIGABRT from invalid free()
libsdl3_dynapi_interpose.so     — SDL dynapi symbol redirect
```

---

## 5. Pipeline Overview

### Flow: Docker Build → Post-Deploy → Run

```
┌─────────────────────────────────────────────────────────────┐
│  sbox-public-linux-docker/sbox-install.sh compile ./        │
│  (Wine-based Windows toolchain, managed C# compilation)     │
└────────────────────┬────────────────────────────────────────┘
                     │ overwrites patched files
                     ▼
┌─────────────────────────────────────────────────────────────┐
│  ./linux/post-docker-deploy.sh                              │
│  • Rebuilds libdxcompiler_wrapper.so (DXC UTF-16→UTF-32)   │
│  • Rebuilds Sandbox.AppSystem.dll + Sandbox.Engine.dll       │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│  ./linux/run.sh                                            │
│  • LD_PRELOAD interpose stack                              │
│  • SDL_VIDEODRIVER=x11                                     │
│  • ./sbox (native AppHost binary)                          │
└─────────────────────────────────────────────────────────────┘
```

### What's Built

- **Managed C# code** — compiled via Docker/Wine into `game/bin/linuxsteamrt64/`
- **Native interpose libs** — built from `linux/interpose/*.cpp` via `make`
- **DXC wrapper** — `libdxcompiler_wrapper.so` (16KB shim)

### What's in `game/bin/linuxsteamrt64/`

| Library | Role |
|---------|------|
| `libengine2.so` | Native engine entry point |
| `libsteam_api.so` | Steam API binding |
| `libtier0.so` | Valve tier0 utilities |
| `librendersystemvulkan.so` | Vulkan render system |
| `librendersystemempty.so` | Empty render system fallback |
| `libSDL3.so.0.4.8` | SDL3 library (SONAME: `libSDL3.so.0`) |
| `libdxcompiler_wrapper.so` | DXC UTF-16→UTF-32 shim |
| `libdxcompiler.so` → `libdxcompiler_wrapper.so` | Symlink for DXC entry point |

### The Interpose System

The interpose layer uses `LD_PRELOAD` to bridge gaps between Valve's native Linux binaries and s&box's managed engine:

| Library | Purpose |
|---------|---------|
| `libopenssl_init_interpose.so` | Forces `libcrypto` initialization before `libengine2` loads |
| `libdeepbind_interpose.so` | Applies `RTLD_DEEPBIND` to Valve libs to resolve symbol conflicts |
| `libpermissive_free_interpose.so` | Intercepts `free()` calls to prevent SIGABRT from invalid pointers |
| `libsdl3_dynapi_interpose.so` | Redirects `dlsym("SDL_Foo_REAL")` → `dlsym("SDL_Foo")` — required because `librendersystemvulkan.so` uses dlsym to load 1,251 SDL symbols |

**Source:** `linux/interpose/*.cpp`

---

## 6. Known Issues

### Shader Compilation — Native Interop Access Violation

Shader compilation can fail with an access violation during native interop calls. This is **not a code issue** — it is a binary compatibility problem between the managed C# interop layer and Valve's compiled native shader compiler binaries. The DXC wrapper shim handles UTF-16→UTF-32 conversion, but certain shader paths exercise code paths that trigger the violation.

**Workaround:** Retry shader compilation or use precompiled shaders where available.

### Wayland vs X11 Behavior Differences

- **Wayland:** Cursor warp is handled by the compositor via `zwp_pointer_constraints_v1`. `SDL_SetWindowRelativeMouseMode` is used directly.
- **X11:** Manual cursor warp with synthetic event filtering is used. `SetCursorPosition` checks `IsWayland` and returns early on Wayland.

If input feels wrong on Wayland, try forcing X11:
```bash
SDL_VIDEODRIVER=x11 ./linux/run.sh
```

### Capture Debounce

The input capture state machine debounces capture release at 0.1s to prevent tooltip hover from flickering capture off. This is intentional.

### Scene Loading Performance

Scene loading takes ~80 seconds due to native model/mesh/collider deserialization. This cannot be optimized from managed code — it is a property of the native binaries.

---

## 7. File Structure

```
sbox-keybangz/
├── README.md                    # This file
├── .gitignore                  # Ignores .so artifacts
│
├── linux/                      # Linux-specific scripts and code
│   ├── run.sh                 # Game launch script
│   ├── setup.sh               # Symlink setup
│   ├── rebuild.sh             # Docker build + post-deploy wrapper
│   ├── post-docker-deploy.sh   # Restore patched files after Docker
│   ├── bootstrap.sh           # DXC wrapper compilation
│   ├── dxc_wrapper.c          # DXC UTF-16→UTF-32 shim source
│   └── interpose/             # LD_PRELOAD libraries
│       ├── sdl3_dynapi_interpose.cpp
│       ├── openssl_init_interpose.cpp
│       ├── deepbind_interpose.cpp
│       └── permissive_free_interpose.cpp
│
├── sbox-public-linux-docker/ # Docker build environment (submodule)
│   └── sbox-install.sh       # Docker build + run script
│
├── game/
│   ├── bin/linuxsteamrt64/    # Native engine binaries
│   │   ├── libengine2.so
│   │   ├── libsteam_api.so
│   │   ├── libtier0.so
│   │   ├── librendersystemvulkan.so
│   │   ├── libSDL3.so.0.4.8
│   │   ├── libdxcompiler_wrapper.so
│   │   └── libdxcompiler.so → libdxcompiler_wrapper.so
│   └── addons/
│
└── engine/                     # Managed C# engine source
    └── Sandbox.Engine/
```

### .gitignore Notes

- `linux/interpose/*.so` — compiled interpose libraries are ignored
- **Do NOT commit compiled binaries**

---

## 8. Git Conventions

| Rule | Reason |
|------|--------|
| **Never commit `.so` files** | Compiled artifacts belong in builds only |
| **Preprocessor: `#if !WIN`** | Use `#if WIN` / `#if !WIN` — never `#if LINUX` |
| **Indentation: tabs, 4-space** | Per `.editorconfig` |
| **Line endings: CRLF** | Per `.editorconfig` |
| **No CI workflows** | Manual builds only |

---

## Quick Reference

```bash
# Clone
git clone <repo-url> sbox-keybangz
cd sbox-keybangz

# Build (full compile)
SBOX_SKIP_ARTIFACTS=1 ./sbox-public-linux-docker/sbox-install.sh compile ./

# Restore patched files (MANDATORY)
./linux/post-docker-deploy.sh

# Run
cd linux && ./run.sh

# Or with debugging
SBOX_GDB=1 ./linux/run.sh
SBOX_VALGRIND=1 ./linux/run.sh
```

---

## Credits

- Reference: [MrSoup678's fork](https://github.com/MrSoup678/sbox-public/tree/master_work) for CasefoldFileSystem concept
- Based on [Facepunch/sbox-public](https://github.com/Facepunch/sbox-public)

## License

The s&box engine source code is licensed under the [MIT License](LICENSE.md).
See the original repository for full license details.
