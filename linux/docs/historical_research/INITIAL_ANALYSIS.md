# s&box Native Linux Client - Initial Analysis Report

**Date**: March 6, 2026  
**Analysis Phase**: Initial Codebase Assessment  
**Objective**: Identify Windows-specific code, missing libraries, and architecture for Linux port

---

## Executive Summary

s&box is a .NET/C# managed game engine with a native C++ core (libengine2.so). Linux native libraries already exist, but several Windows-specific code paths need to be addressed.

**Key Findings**:
- ✅ Native engine already compiled for Linux (libengine2.so present)
- ✅ Vulkan rendering system functional (librendersystemvulkan.so)
- ✅ Cross-platform font rendering (SkiaSharp + HarfBuzzSharp)
- ✅ FFmpeg libraries present for multimedia
- ⚠️ libswscale.so.9 symlink was missing (FIXED)
- ❌ Windows GDI cursor capture code needs Linux equivalent
- ❌ contentbuilder.exe only available for Windows (needs Linux replacement)
- ⚠️ Registry-based file associations (Windows-only)
- ⚠️ MessageBox/ShowWindow APIs (Windows-only)

---

## 1. Architecture Overview

### 1.1 Project Structure

```
sbox-public/
├── engine/                    # C#/.NET managed code
│   ├── Sandbox.Engine/       # Main engine systems
│   ├── Sandbox.Tools/      # Build tools and editor
│   ├── Sandbox.Launcher/   # Application launchers
│   ├── ThirdParty/          # Third-party code (Topten.RichTextKit, nvpatch)
│   └── Tools/SboxBuild/      # Build system
│       ├── Platform/
│       │   ├── Platform.cs (base)
│       │   ├── WindowsPlatform.cs
│       │   ├── LinuxPlatform.cs
│       │   └── MacOSPlatform.cs
│       └── Steps/
│           ├── BuildContent.cs
│           ├── BuildNative.cs
│           └── BuildManaged.cs
└── game/                     # Game files and native libraries
    ├── bin/
    │   ├── linuxsteamrt64/    # Linux x64 binaries ✅
    │   ├── win64/              # Windows binaries
    │   └── managed/            # C# assemblies
    ├── addons/                # Game addons
    ├── core/                   # Core systems
    └── config/                 # Configuration
```

### 1.2 Technology Stack

| Layer | Technology | Purpose | Platform Support |
|--------|-------------|---------------------|----------------|
| Managed | C#/.NET 10 | Application logic, editor, launcher | ✅ Cross-platform |
| Native | C/C++ (libengine2.so) | Core engine, rendering, systems | ✅ Linux native binaries |
| Interop | Auto-generated C# wrappers | P/Invoke to native engine | ✅ Cross-platform |
| Rendering | Vulkan (librendersystemvulkan.so) | Graphics rendering | ✅ Cross-platform |
| Input | SDL3 | Input, gamepad | ✅ Cross-platform |
| Fonts | SkiaSharp + HarfBuzzSharp | Text rendering | ✅ Cross-platform |
| Multimedia | FFmpeg (avcodec, avformat, etc.) | Video/audio recording | ✅ Cross-platform |
| Audio | Phonon | Audio system | ⚠️ Need verification |
| Steam | Steam API | Steam integration | ✅ Cross-platform |

---

## 2. Native Library Analysis

### 2.1 Core Engine Libraries (linuxsteamrt64)

| Library | Size | Purpose | Dependencies | Status |
|---------|------|----------|--------------|--------|
| libengine2.so | 11.5 MB | Main engine, exports Plat_* functions | ✅ Present |
| libtier0.so | 5.4 MB | Platform utilities, memory management | ✅ Present |
| libsteam_api.so | 387 KB | Steam API integration | ✅ Present |
| libphonon.so | 19.6 MB | Audio system | ⚠️ Needs verification |
| libanimationsystem.so | 4.2 MB | Animation system | ✅ Present |
| libmaterialsystem2.so | 1.7 MB | Material system | ✅ Present |
| libmeshsystem.so | 1.3 MB | Mesh processing | ✅ Present |
| librendersystemvulkan.so | 7.6 MB | Vulkan rendering | ✅ Present |
| libvfx_vulkan.so | 8.0 MB | Visual effects | ✅ Present |
| libschemasystem.so | 472 KB | Schema/data system | ✅ Present |

### 2.2 FFmpeg Libraries

| Library | Version | Purpose | Status |
|---------|---------|----------|--------|
| libavcodec.so.62 | 62.11.100 | Video encoding/decoding | ✅ Present |
| libavformat.so.62 | 62.3.100 | Container formats | ✅ Present |
| libavutil.so.60 | 60.8.100 | Utilities | ✅ Present |
| libavfilter.so.11 | 11.4.100 | Video filters | ✅ Present |
| libswresample.so.6 | 6.1.100 | Audio resampling | ⚠️ Not present in bin |
| libswscale.so.9 | 9.1.100 | Video scaling | ✅ Present (symlink fixed) |

**Issue**: `libswresample.so.6` referenced by ldd but not found in bin directory

### 2.3 Font & Text Rendering

| Library | Version | Purpose | Status |
|---------|---------|----------|--------|
| libSkiaSharp.so.116.0.0 | 116.0.0 | 2D graphics, text rendering | ✅ Present |
| libHarfBuzzSharp.so.0.60830.0 | 0.60830.0 | Text shaping, font fallback | ✅ Present |

**Note**: Font rendering is fully cross-platform using SkiaSharp and HarfBuzzSharp - no Windows GDI dependencies found.

### 2.4 Vulkan & Graphics

| Library | Purpose | Status |
|---------|----------|--------|
| libdxcompiler.so | DirectX shader compiler for Vulkan | ✅ Present |
| Vulkan SDK (external) | Header files, validation layers | ✅ Available |

---

## 3. Windows-Specific Code Analysis

### 3.1 Platform Abstraction

**Platform Detection in LinuxPlatform.cs**:
```csharp
protected override string PlatformBaseName => "linuxsteamrt"; // Comment: "Fucking make this just 'linux' when port is more mature"
```

**Platform Build System**:
- `WindowsPlatform.cs`: Uses Visual Studio (`msbuild`, `VsDevCmd.bat`)
- `LinuxPlatform.cs`: Uses `make -f {solution}.mak SHELL=/bin/bash` 
- `MacOSPlatform.cs`: Uses `xcodebuild`

**Observation**: Makefiles for Linux builds not present in sbox-public/ directory.

### 3.2 Windows API Calls Found

#### 3.2.1 ScreenRecorder (CRITICAL) - Cursor Capture

**File**: `engine/Sandbox.Engine/Systems/Render/Multimedia/ScreenRecorder.cs`

**Issue**: Lines 133-523 wrapped in `#if WIN` block with Windows GDI calls:

```csharp
#if WIN
    // Windows-specific cursor blitting implementation
    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);
    
    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, ref ICONINFO piconinfo);
    
    [DllImport("gdi32.dll")]
    private static extern bool GetObjectA(IntPtr hObject, int nCount, ref BITMAP lpObject);
    
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);
    
    [DllImport("gdi32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    
    [DllImport("gdi32.dll")]
    private static extern bool GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, IntPtr lpvBits, ref BITMAPINFOHEADER lpbmi, uint uUsage);
    
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
#endif
```

**Impact**: Video recording cursor capture will NOT work on Linux.

**Required Fix**: Implement Linux cursor capture using X11/XCB or Wayland.

**Severity**: HIGH - Feature broken on Linux

#### 3.2.2 FileAssociations.cs (MEDIUM)

**File**: `engine/Sandbox.Tools/Utility/FileAssociations.cs`

**Issue**: Uses Windows Registry API:
```csharp
RegistryKey fileTypeKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Classes\sandbox.ProjectFile");
RegistryKey shellKey = fileTypeKey.CreateSubKey("shell");
RegistryKey shellOpenKey = shellKey.CreateSubKey("open");
```

**Impact**: File association registration will fail on Linux (Linux uses XDG MIME types, desktop files).

**Required Fix**: Implement Linux file association via `.desktop` files or skip on Linux.

**Severity**: MEDIUM - Non-breaking but files won't open with double-click

#### 3.2.3 Launcher.cs (LOW)

**File**: `engine/Launcher/StandaloneTest/Launcher.cs`

**Issue**: Uses Windows-only API:
```csharp
[DllImport("User32.dll", CharSet = CharSet.Unicode)]
private static extern bool ShowWindow(IntPtr handle, int nCmdShow);
```

**Impact**: Window management code only works on Windows.

**Required Fix**: Wrap with `#if !WIN` for Linux or use platform abstraction.

**Severity**: LOW - Likely not needed if platform abstraction handles this

#### 3.2.4 Path Separators (LOW)

**File**: `engine/Tools/SboxBuild/Platform/WindowsPlatform.cs`

**Issue**: Uses backslashes:
```csharp
vsWhere.StartInfo.FileName = "src\\devtools\\bin\\win64\\vswhere";
string vsDevCmdPath = Path.Combine(vsPath, "Common7\\Tools\\VsDevCmd.bat");
```

**Impact**: Will fail on Linux but minor issue.

**Required Fix**: Use `Path.Combine()` consistently or `Path.DirectorySeparatorChar`.

**Severity**: LOW - Trivial fix

### 3.3 Build System Issues

#### 3.3.1 contentbuilder.exe (HIGH)

**File**: `engine/Tools/SboxBuild/Steps/BuildContent.cs`

**Issue**: Hardcoded Windows executable path:
```csharp
string contentBuilderPath = Path.Combine(gameDir, "bin", "win64", "contentbuilder.exe");
```

**Impact**: Content building will fail on Linux. The Linux `bootstrap.sh` script attempts to work around this:
```bash
#HACK: Currently Facepunch doesn't ship native binary for contentbuilder. Run this instead via wine.
wine game/bin/win64/contentbuilder.exe -b game
```

**Observation**: No `contentbuilder` binary exists in `linuxsteamrt64/` directory.

**Required Fix**: One of:
1. Port contentbuilder to Linux as native executable
2. Create a minimal Linux content builder in C#
3. Document that Wine is required for content building

**Severity**: HIGH - Blocker for content building without Wine

#### 3.3.2 Missing Makefiles (MEDIUM)

**Observation**: LinuxPlatform.cs tries to build with:
```csharp
return Utility.RunProcess("make", $"-f {solutionName}.mak SHELL=/bin/bash", "src");
```

**Issue**: No `.mak` files found in sbox-public/ directory.

**Impact**: Native engine already compiled (libengine2.so present), so makefiles may not be needed for managed code building. However, this means native engine modifications cannot be done from source without external access.

**Severity**: MEDIUM - Unknown if native source is available or required

---

## 4. Platform Abstraction Layer

### 4.1 Plat_* Functions Exported by libengine2.so

libengine2.so exports platform abstraction functions that should be cross-platform:

**File/Window Management**:
- `Plat_CreateWindow`
- `Plat_DestroyWindow`
- `Plat_SetWindowPos`
- `Plat_GetWindowClientSize`
- `Plat_MinimizeWindow`
- `Plat_IsWindowFocused`

**Display Management**:
- `Plat_GetDesktopResolution`
- `Plat_GetDesktopBounds`
- `Plat_GetDPI`
- `Plat_GetMonitorBounds`
- `Plat_RefreshDPI`

**Clipboard**:
- `Plat_GetClipboardText`
- `Plat_SetClipboardText`
- `Plat_ClearClipboardText`

**File/Path Operations**:
- `Plat_FileExists`
- `Plat_GetCurrentDirectory`
- `Plat_GetGameDirectory`
- `Plat_SetCurrentDirectory`
- `Plat_SetGameDirectories`

**Process/System**:
- `Plat_GetCPUFrequency`
- `Plat_FloatTime`
- `Plat_ExitProcess`
- `Plat_AttachDebuggerToProcess`
- `Plat_BeginWatchdogTimer`
- `Plat_EndWatchdogTimer`
- `Plat_ApproximateProcessMemoryUsage`

**Steam Integration**:
- `SteamInternal_CreateInterface`
- `SteamInternal_SteamAPI_Init`

**Observation**: These functions are properly abstracted - no Windows-specific code paths found for them.

---

## 5. Dependency Status

### 5.1 Missing Libraries

| Dependency | Status | Notes |
|-----------|--------|-------|
| libswresample.so.6 | ⚠️ Referenced by libengine2.so but not in bin/ | May be in libphonon.so or bundled |
| contentbuilder (Linux native) | ❌ Not present | Uses Wine in bootstrap.sh |

### 5.2 All Dependencies Satisfied

✅ **No missing dependencies found** for existing libraries - `ldd` shows all shared libraries resolve correctly:
- Standard C library: libc.so.6, libm.so.6, libpthread, libdl
- C++ standard library: libstdc++.so.6, libgcc_s.so.1
- Steam: libsteam_api.so
- Tier 0: libtier0.so
- Vulkan/X11: libxcb.so.1, libXau.so.6, libXdmcp.so.6, libuuid.so.1

### 5.3 Build System Status

| Component | Status | Notes |
|-----------|--------|-------|
| Native libraries (libengine2.so, tier0.so) | ✅ Compiled | Linux native binaries present |
| Managed code (C#/.NET) | ✅ Builds | Uses dotnet run/build |
| Vulkan SDK | ✅ Available | In external Vulkan SDK folder |
| FFmpeg libraries | ✅ Present | All required libs in bin/ |

---

## 6. Recommendations

### 6.1 Immediate Actions (Required Before Session Continues)

1. **Fix ScreenRecorder for Linux** (HIGH PRIORITY)
   - Create `Research/Stubs/CursorCapture/` directory
   - Document Linux cursor capture APIs (X11, Wayland)
   - Implement stub or port the cursor capture logic

2. **Address contentbuilder.exe** (HIGH PRIORITY)
   - Document Wine dependency in research notes
   - Consider if contentbuilder can be bypassed or if Wine wrapper is acceptable

3. **Fix FileAssociations.cs** (MEDIUM PRIORITY)
   - Add `#if !WIN` guard around Windows Registry code
   - Document Linux file association approach (.desktop files)

4. **Fix Launcher.cs Window API** (LOW PRIORITY)
   - Add platform guard for ShowWindow DllImport

5. **Verify libswresample.so.6** (MEDIUM PRIORITY)
   - Determine if this is actually missing or bundled elsewhere
   - Check if phonon.so includes it

### 6.2 Architecture Improvements (For Future Sessions)

1. **Build System Investigation**
   - Locate native engine source code (not in sbox-public/)
   - Determine if native engine can be rebuilt from source
   - Identify if makefiles are generated or external

2. **Linux-Specific Code Patterns**
   - Implement X11/Wayland window management for screen recording cursor
   - Add XDG desktop entry generation for file associations
   - Review all `#if WIN` blocks for Linux compatibility

3. **Platform Abstraction Enhancement**
   - Ensure all Plat_* functions have Linux implementations
   - Consider adding Linux-specific APIs where needed (e.g., inotify for file watching)

---

## 7. Files Requiring Modification

### 7.1 Critical Files (Must Fix for Linux)

| File | Issue | Severity | Lines |
|-------|--------|----------|--------|
| `engine/Sandbox.Engine/Systems/Render/Multimedia/ScreenRecorder.cs` | Windows GDI cursor capture | HIGH | 133-523 |
| `engine/Tools/SboxBuild/Steps/BuildContent.cs` | Hardcoded Windows path | HIGH | 13-22 |
| `engine/Sandbox.Tools/Utility/FileAssociations.cs` | Windows Registry API | MEDIUM | Multiple |
| `engine/Launcher/StandaloneTest/Launcher.cs` | Windows-only ShowWindow | LOW | ~1 |

### 7.2 Moderate Priority Files (Should Fix)

| File | Issue | Severity |
|-------|--------|----------|
| `engine/Tools/SboxBuild/Platform/WindowsPlatform.cs` | Path separators | LOW | 35, 45 |
| `engine/Sandbox.Tools/Utility/VoiceRecording.cs` | winmm.dll (Windows multimedia) | MEDIUM | Unknown |

### 7.3 Informational (Review Needed)

| File | Issue | Notes |
|-------|--------|-------|
| `engine/Sandbox.Engine/Systems/Render/TextRendering/FontManager.cs` | Uses SkiaSharp - already cross-platform | No action needed |
| `engine/Launcher/Shared/LauncherEnvironment.cs` | Has Linux-specific code paths | Verify correctness |
| `bootstrap.sh` | Wine workaround for contentbuilder | Document dependency |

---

## 8. Next Steps

1. **Create stub library specifications** in `Research/Stubs/` for:
   - CursorCapture (Linux X11/Wayland implementation)
   - ContentBuilder (Linux replacement design)

2. **Verify libswresample.so.6** presence:
   - Check if bundled with phonon or other libs
   - Document actual dependency chain

3. **Begin implementing Linux cursor capture**:
   - Research X11 cursor capture APIs
   - Research Wayland cursor capture APIs
   - Create cross-platform abstraction layer

4. **Document content builder requirements**:
   - What files does contentbuilder process?
   - Can it be bypassed or mocked?

5. **Create fix proposals** for each critical file with:
   - Specific code changes needed
   - Platform-specific alternatives
   - Testing strategy

---

## 9. Code Quality Observations

### Positives
✅ Good use of platform abstraction (Plat_* functions)
✅ Cross-platform libraries chosen (Skia, SDL, Vulkan, FFmpeg)
✅ Auto-generated interop layer reduces boilerplate
✅ Steam API properly integrated via platform abstraction

### Concerns
⚠️ Linux build platform comment suggests immature Linux support
⚠️ No native engine source code in sbox-public (pre-compiled libengine2.so)
⚠️ Some Windows-specific code not wrapped in platform guards
⚠️ Wine dependency for contentbuilder indicates incomplete Linux tooling

---

## Appendix A: Native Engine Symbol Analysis

### A.1 SDL Integration
libengine2.so exports SDL functions:
- SDL_GetGamepadFromID, SDL_GetGamepadType, SDL_CloseGamepad
- SDL_RumbleGamepadTriggers, SDL_GetGamepadAxis
- SDL_SetGamepadLED, SDL_GetGamepadFirmwareVersion
- SDL_ShowCursor, SDL_HideCursor, SDL_WarpMouseGlobal
- SDL_EnableScreenSaver, SDL_DisableScreenSaver
- SDL_SetCursor, SDL_GetError, SDL_SetTextInputArea
- SDL_TextInputActive, SDL_JoystickGUIDUsesVersion

### A.2 Platform Functions
libengine2.so exports 60+ Plat_* functions (see Section 4.1)

### A.3 FFmpeg Integration
libengine2.so imports FFmpeg symbols:
- avcodec_alloc_context3
- av_hwframe_get_buffer
- avfilter_graph_dump
- swr_convert
- scandir (via libc)

### A.4 Render System
libengine2.so likely interfaces with:
- librendersystemvulkan.so (Vulkan rendering)
- libvfx_vulkan.so (Visual effects)
- libmaterialsystem2.so (Material management)

---

## Appendix B: Build System Flow

### B.1 Managed Code Build
```
1. dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- build
2. Generates solutions and compiles C# code
3. Copies assemblies to game/bin/managed/
```

### B.2 Native Code Build
```
1. Native engine appears pre-compiled (libengine2.so present)
2. Build system expects to load native libraries from game/bin/
3. No evidence of native compilation from source in sbox-public/
```

### B.3 Content Build Flow
```
Windows:
1. game/bin/win64/contentbuilder.exe -b game

Linux (current):
1. wine game/bin/win64/contentbuilder.exe -b game
```

**Observation**: Linux content building requires Wine - not native.

---

## Conclusion

The s&box codebase is **partially ready** for Linux with pre-compiled native libraries, but several Windows-specific code paths prevent full functionality:

**Blocking Issues**:
1. ScreenRecorder cursor capture (HIGH)
2. Content builder dependency on Wine (HIGH)

**Non-Blocking Issues**:
1. File associations via Registry (MEDIUM)
2. Some window management APIs (LOW)
3. Path separator inconsistencies (LOW)

**Good News**:
- Vulkan rendering is fully cross-platform
- Font/text rendering is cross-platform (Skia + HarfBuzz)
- Platform abstraction layer exists (Plat_* functions)
- Most system libraries present and functional

**Recommended Immediate Priority**:
1. Fix ScreenRecorder for Linux cursor capture
2. Address contentbuilder Wine dependency
3. Add platform guards around Windows-specific APIs

**Session Continuation Strategy**:
Since native source code is not available, modifications should focus on:
1. Creating Linux-specific implementations for Windows-only code
2. Creating stub libraries where native replacements don't exist
3. Documenting Wine dependencies as acceptable where necessary
4. Building cross-platform abstractions where possible

---

**Report Generated**: Sisyphus Autonomous Analysis System  
**Next Session**: Create stub library specifications and implement fixes
