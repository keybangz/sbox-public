# sbox-keybangz Session Memory
<!-- Temporary memory file — ignored by git. Replace with MCP memory tools when fixed. -->

---

## Project Overview

- **Repo:** `/mnt/extra_ssd/Github/sbox-private-fork/sbox-keybangz`
- **Branch:** `linux-native-client`
- **Purpose:** Fork of s&box engine (Source 2 derivative) adding Linux platform support
- **Solution:** `engine/Sandbox-Engine.slnx` (main), `engine/Tools/Sandbox-Tools.slnx` (tools)
- **Build:** `dotnet build engine/Sandbox-Engine.slnx` or `dotnet run --project engine/Tools/SboxBuild/SboxBuild.csproj -- build --config Developer`
- **Top-level dirs:** `engine/` (managed .NET), `game/` (binaries + samples), `linux/` (launcher + interpose libs), `.github/` (PR template only)
- **No CI workflows** — manual builds only
- **Preprocessor convention:** `#if WIN` / `#if !WIN` — NEVER `#if LINUX`
- **Indentation:** tabs, 4-space, CRLF line endings (`.editorconfig`)
- **Namespace root:** `Sandbox.Engine` (engine), `Sandbox` (public API)

---

## Engine Layer Structure (`engine/Sandbox.Engine/`)

| File | Role |
|------|------|
| `Application.cs` | Engine entry point |
| `Interop.Engine.cs` | 18K+ line auto-generated InteropGen wrappers (C++ native structs as `readonly partial struct` with `IntPtr self`, function pointers via `delegate* unmanaged<...>`) |
| `Core/EngineLoop.cs` | Main frame loop (RunFrame, FrameStart, FrameEnd); calls `g_pInputService.Pump()` at line 153 |
| `Core/Context/InputContext.cs` | Per-context input state machine (Ignore/UI/Game); `In_MousePosition(pos, delta)` at line 150 routes to game or UI |
| `Platform/DLLImportResolver.cs` | Rewrites `[DllImport("Foo")]` → `<NativeDllPath>/Foo.so` on Linux; registered on all assemblies |
| `Platform/Linux/LinuxSDL3Native.cs` | SDL3 P/Invoke bindings |
| `Platform/Linux/LinuxSDLInput.cs` | Linux input helpers (IsWayland, synthetic event filter, relative mouse mode) |
| `Systems/Input/InputRouter.cs` | Central input router + mouse capture state machine |
| `Systems/Input/InputRouter.Input.cs` | Event handlers (OnMouseMotion, OnKey, OnText, etc.) |
| `Systems/Input/Input.cs` | Public Input API (AnalogLook, AnalogMove, MouseDelta) |
| `Systems/Input/InputLog.cs` | Gated trace logger — `SBOX_INPUT_TRACE=1` env var, Linux-only |

---

## Linux Platform Layer

### `LinuxSDL3Native.cs` — P/Invoke bindings to `libSDL3.so.0`

```csharp
SDL_GetKeyboardFocus() → IntPtr
SDL_GetMouseFocus() → IntPtr
SDL_SetWindowRelativeMouseMode(IntPtr window, bool enabled) → bool  // true = success
SDL_GetWindowRelativeMouseMode(IntPtr window) → bool
SDL_GetCurrentVideoDriver() → IntPtr  // UTF-8: "wayland" or "x11"
SDL_GetError() → IntPtr
```

- Static constructor registers per-assembly `NativeLibrary.SetDllImportResolver` probing `libSDL3.so.0` then `libSDL3.so`
- Overrides generic `DLLImportResolver` which would look for `SDL3.so` (wrong SONAME)
- Try/catch `InvalidOperationException` handles "resolver already registered" case

### `LinuxSDLInput.cs` — Linux input helpers

| Member | Description |
|--------|-------------|
| `IsWayland` | Cached `bool?`; reads `SDL_GetCurrentVideoDriver()` UTF-8; cached after first successful read; false on NULL (SDL not yet init) |
| `HasX11Focus` | `IsWayland ? false : g_pInputService.IsAppActive()` |
| `GetRelativeMouseMode()` | Resolves window via `SDL_GetKeyboardFocus() → SDL_GetMouseFocus()` fallback; calls `SDL_GetWindowRelativeMouseMode` |
| `SetRelativeMouseMode(bool)` | Same window resolution; calls `SDL_SetWindowRelativeMouseMode`; logs `SDL_GetError` on failure |
| `IsSyntheticMotion(Vector2)` | Filters cursor warp synthetic events; WarpEpsilon = 2.0px |
| `IgnoreNextWarp(Vector2)` | Registers warp position to filter next synthetic motion event |
| `ClearWarpTarget()` | Clears pending warp on focus loss/shutdown |

---

## Input System Call Flow

### Mouse Motion (Linux path)
```
SDL3 event → g_pInputService.Pump() [EngineLoop line 153]
  → OnMouseMotion(float dx, float dy) [InputRouter.Input.cs:93]
  → Synthetic warp filter: LinuxSDLInput.IsSyntheticMotion(MouseCursorPosition) [when _mouseCaptureMode=true]
  → InputContext.In_MousePosition(pos, delta) [InputContext.cs:150]
    → If Game/Capture: OnMouseMotion?.Invoke(delta) + Sandbox.Input.AddMouseMovement(delta)
    → Else: TargetUISystem.InputEventQueue.MouseMoved(delta)
```

### Capture State Machine (`InputRouter.cs Frame()`)

| Field | Description |
|-------|-------------|
| `mouseCapturePosition: Vector2?` | Stores cursor position at capture acquisition; non-null = captured |
| `_mouseCaptureMode: bool` | Previous frame's capture state (updated at end of Frame()) |
| `captureStateChanged` | `mouseCaptureMode != _mouseCaptureMode` — gates SDL calls to transitions only (commit `b2829c5b`) |
| `GameWantsCapture` | `IGameInstance.Current != null && MouseState != UI` |

- Debounce: 0.1s prevents tooltip hover from flickering capture off
- On capture acquire: `LinuxSDLInput.SetRelativeMouseMode(true)` `[#if !WIN]`
- On capture release: `LinuxSDLInput.SetRelativeMouseMode(false)`, restore cursor, `mouseCapturePosition = null`, `MouseCursorVisible = true`

### `SetCursorPosition` (X11 vs Wayland)
```csharp
if (!g_pInputService.IsAppActive()) return;
#if !WIN
    if (LinuxSDLInput.IsWayland) return;          // Wayland: SDL relative mode handles it
    if (!LinuxSDLInput.HasX11Focus) return;
    LinuxSDLInput.IgnoreNextWarp(pos);             // Register to filter synthetic event
#endif
g_pInputService.SetCursorPosition((int)pos.x, (int)pos.y);
```

---

## Native Interop Layer

### `NativeEngine` (Interop.Engine.cs)
- Auto-generated by InteropGen from **closed-source C++ headers not in this repo**
- Cannot add new methods without those headers
- `NativeEngine.InputSystem` exposes (cross-platform): `StringToButtonCode`, `GetKeyDisplayName`, `VirtualKeyToButtonCode`, `SetIMEAllowed`, `SetIMETextLocation`, `RegisterWindowWithSDL`, `OnEditorGameFocusChange`
- Windows-only: `GetRelativeMouseMode()`, `SetRelativeMouseMode(bool)`

### `g_pInputService` (cross-platform native pointer)
- `Pump()` — called every frame
- `IsAppActive()` — window focus check
- `SetCursorPosition(int x, int y)` — absolute cursor warp
- `GetBinding(ButtonCode)` — convar binding lookup

### SDL3 Dynapi Interpose (`linux/interpose/sdl3_dynapi_interpose.cpp`)
- Redirects `dlsym("SDL_Foo_REAL")` → `dlsym("SDL_Foo")`
- Solves: `librendersystemvulkan.so` uses dlsym to load 1,251 `SDL_*_REAL` symbols hidden in standard SDL3 builds
- Loaded via `LD_PRELOAD` in `linux/run.sh`

---

## Linux Launcher Stack (`linux/run.sh`)

- Forces `SDL_VIDEODRIVER=x11` (Wayland init fails silently)
- LD_PRELOAD interpose stack:
  - `libopenssl_init_interpose.so` — Force-init libcrypto before engine2
  - `libdeepbind_interpose.so` — RTLD_DEEPBIND on Valve engine libs
  - `libpermissive_free_interpose.so` — Prevent SIGABRT from invalid free()
  - `libsdl3_dynapi_interpose.so` — SDL dynapi symbol redirect
- Debug modes: `SBOX_VALGRIND=1`, `SBOX_GDB=1`, `SBOX_ASAN=1`
- Heartbeat: writes to `/tmp/heartbeat_debug.txt` every ~2s

### `linux/post-docker-deploy.sh`
- Rebuilds `libdxcompiler_wrapper.so` (DXC UTF-16→UTF-32 shim)
- Rebuilds `Sandbox.AppSystem.dll` and `Sandbox.Engine.dll` (Debug)

### `game/bin/linuxsteamrt64/` key libs
- `libSDL3.so.0.4.8` (SONAME: `libSDL3.so.0`) — already loaded by rendersystemvulkan
- `libengine2.so`, `libsteam_api.so`, `libtier0.so`
- `librendersystemvulkan.so`, `librendersystemempty.so`
- `libdxcompiler_wrapper.so` — DXC shim

---

## Architectural Decisions (Linux Input)

1. **Direct P/Invoke to SDL3** — chosen over InteropGen extension (no C++ headers) and native interpose shims (harder to debug)
2. **Hybrid X11/Wayland strategy** — X11: manual cursor warp + synthetic event filter; Wayland: `SDL_SetWindowRelativeMouseMode` (compositor pointer constraints via `zwp_pointer_constraints_v1`)
3. **Window resolution** — `SDL_GetKeyboardFocus() → SDL_GetMouseFocus()` fallback; avoids hooking `RegisterWindowWithSDL`
4. **Per-class DllImportResolver for SDL3** — overrides generic resolver (which builds `SDL3.so` — wrong SONAME)
5. **Transition-gated SDL calls** — `SetRelativeMouseMode` only called on `captureStateChanged` (commit `b2829c5b`) — eliminates 3 P/Invokes/frame
6. **`IsWayland` cached** after first successful read; uncached on NULL (SDL not yet init)
7. **`#if !WIN` / `#if WIN`** guards — never `#if LINUX`

---

## Commits on `linux-native-client` (SDL input work)

| SHA | Description |
|-----|-------------|
| `b2829c5b` | Gate SetRelativeMouseMode on capture state transitions (perf) |
| `e782e359` | InputRouter wiring — replaces stubs |
| `9280d925` | LinuxSDLInput IsWayland + Set/GetRelativeMouseMode |
| `6fa81cc3` | LinuxSDL3Native P/Invoke bindings |
| *(9 prior)* | X11 manual-warp hybrid path (InputLog.Trace, capture state machine, focus-loss/shutdown failsafes, debounce, synthetic event filter) |

---

## Known Open Issues

| Issue | Status |
|-------|--------|
| CS0414 `CaptureDebounceSeconds` field unused on Windows | Builder fix dispatched — result was empty; verify Windows build |
| Multi-window focus: Tab to play mode without clicking may capture wrong window | Documented risk, not fixed |
| `_isWayland` static `bool?` not thread-safe | Theoretical — input loop is single-threaded |
| Scribe MCP memory tools unavailable (`mcp_proxy_mcp_proxy_memory_*` wrong tool name) | Frontmatter issue — using this MEMORY.md as fallback |

---

## docker repo (sibling)

- Path: `/mnt/extra_ssd/Github/sbox-private-fork/sbox-public-linux-docker`
- `sbox-install.sh` — Docker build + run script for Wine-based Windows build environment
- Fix applied: `trap '...' EXIT` + `NEEDS_PERM_FIX` flag ensures `fix_perms` runs even on build failure (commit `a7344562`)
- Known issues: container leak on `build_image` failure, `-t` flag breaks CI, exit code masking — deferred per user request

---

## Session Update — 2026-05-05

### Commits This Session
| SHA | Description |
|-----|-------------|
| `e1c8fcfe` | Remove redundant NativeLibraryResolver — DLLImportResolver handles all assembly resolution |

### Root Cause Found & Fixed: libsteam_api.so Load Failure
- **Root cause:** Two competing `NativeLibrary.SetDllImportResolver` systems (`DLLImportResolver` + `NativeLibraryResolver`) both registered for the same assemblies. `SetDllImportResolver` throws `InvalidOperationException` on double-registration — both caught silently — so whichever registered second was silently dropped. Non-deterministic resolver behavior.
- **Secondary issue:** `NativeLibraryResolver` used `RTLD_DEEPBIND` via raw `dlopen`, bypassing .NET library tracking.
- **Fix:** Deleted `NativeLibraryResolver.cs` entirely. Cleaned up 4 call sites: `AppSystem.cs`, `QtAppSystem.cs`, `MissingDependancyDiagnosis.cs`, `CreateInterface.cs`.
- **`LoadSteamDll()`** now uses `NativeLibrary.TryLoad(Path.Combine(Environment.CurrentDirectory, "bin/linuxsteamrt64/libsteam_api.so"))` directly — safe because `LauncherEnvironment.Init()` sets `CurrentDirectory = GamePath` before `AppSystem.Init()` runs.
- **Status:** Fix committed, awaiting rebuild + test. Test: `./linux/run.sh`, check `/tmp/initgame_debug.txt` for `[InitGame]` breadcrumbs past `SourceEngineInit returned`.

### Architecture Clarified
- `LauncherEnvironment.Init()` → `Launch()` → `AppSystem.Init()` — ordering confirmed; `NativeDllPath` and `CurrentDirectory` are always absolute before any Steam P/Invoke fires
- `DLLImportResolver` is now the **sole** resolver; registered once per assembly in `Bootstrap.PreInit` → `SetupResolvers()`
- `CreateInterface.cs` uses `AppDomain.CurrentDomain.BaseDirectory` + `bin/linuxsteamrt64/dll` — fine for engine DLLs already loaded by native side

### Known Open Issues (updated)
| Issue | Status |
|-------|--------|
| CS0414 `CaptureDebounceSeconds` unused on Windows | Unverified — Builder fix dispatched previously, result empty |
| Multi-window focus capture | Documented risk, not fixed |
| `libsteam_api.so` load failure | **Fix applied** (commit `e1c8fcfe`) — pending test |
| Scribe MCP memory write backend | Still down (`Cannot read properties of null (reading 'connect')`) — MEMORY.md is active fallback |

---

## Session Update — 2026-05-06

### `./sbox` Native Binary Launch Fixed

Three changes make `./sbox` work on Linux without requiring `run.sh`:

1. **`LauncherEnvironment.Init()` sets `SBOX_BIN_DIR`** (`engine/Launcher/Shared/LauncherEnvironment.cs`)
   - `AppSystem.InitGame()` rewrites `argv[0]` to `$SBOX_BIN_DIR/sbox.exe` so the native engine (`libengine2.so`) can find `bin/linuxsteamrt64/`.
   - Previously this only worked when `run.sh` set the env var externally. Now `LauncherEnvironment.Init()` sets it automatically before any native engine libs are loaded.

2. **`run.sh` now uses the native AppHost binary** (`linux/run.sh`)
   - All launch branches (default, Valgrind, GDB, ASAN) now invoke `"$GAME_DIR/sbox"` instead of `dotnet sbox.dll`.
   - The `./sbox` AppHost correctly runs `Startup.Main()` → `LauncherEnvironment.Init()` → `GameAppSystem.Run()` (managed loop).
   - Stale comment about dotnet being required for the managed loop has been updated.

3. **`libdxcompiler.so` symlink** (`game/bin/linuxsteamrt64/`)
   - Symlink `libdxcompiler.so → libdxcompiler_wrapper.so` ensures the DXC shader compiler entry point is found.
   - The wrapper (`libdxcompiler_wrapper.so`) handles UTF-16 → UTF-32 string conversion for the native DXC compiler (`libdxcompiler.so.real`).
