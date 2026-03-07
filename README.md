# 🐧 s&box Linux Native Client

**Status:** ✅ STABLE - Optimized menu UI performance!

<img width="1911" height="1072" alt="Screenshot_20260307_015248" src="https://github.com/user-attachments/assets/4411404f-be8a-4b1e-afa4-1f3b7d23f80f" />

This fork contains fixes and modifications to run s&box natively on Linux.

### Quick Start (Linux)

```bash
# Run setup once (creates required symlinks automatically)
cd linux && ./setup.sh

# Run the game
./run.sh

# Or manually:
cd game
export LD_LIBRARY_PATH="$(pwd)/bin/linuxsteamrt64:$(pwd):${LD_LIBRARY_PATH:-}"
dotnet sbox.dll
```

### What's New: Linux Performance Optimizations (March 2026)

Major performance improvements for the Linux native client:

- **🚀 Menu UI now smooth** - Fixed 5-30 second frame freezes, now runs at 50-250ms
- **📦 Incremental texture loading** - TextureLoadQueue prevents blocking during rendering
- **🌐 DNS caching** - Fixed Linux-specific DNS resolution delays (5-15s → instant)
- **⚡ HTTP client optimization** - Reduced timeout from 120min to 30s, added connection pooling
- **⏱️ Time budgets** - Prevents any single operation from blocking the main thread

### Previous Updates: Automated Cross-Platform Support

- **92% fewer symlinks** - Reduced from 441 to ~35 symlinks
- **Case-insensitive filesystem layer** - Handles `Code` vs `code`, `Assets` vs `assets` automatically
- **Cross-platform native library loading** - Automatic resolution of `steam_api64` → `libsteam_api.so`, etc.
- **Automated setup script** - `linux/setup.sh` creates all required symlinks automatically
- **X11 input polling** - Native mouse position and button state using X11 (no SDL3 dependency)
- **Shader/texture loading fixes** - Proper colorgrading shader and DDGI texture initialization
- **DXC wrapper auto-setup** - `bootstrap.sh` automatically compiles and installs the DXC shader compiler wrapper
- **Deprecated Steam API cleanup** - Removed Stadia-related P/Invoke calls that caused PreJIT warnings

### Code Changes Summary

#### Core Interop & Native Loading
| File | Change |
|------|--------|
| `InteropGen/Writer/ManagerWriter.cs` | Use delegate-based function pointers instead of `UnmanagedCallersOnly` for Linux |
| `InteropGen/Writer/ManagerWriter.Imports.cs` | Disabled `[SuppressGCTransition]` (causes GC mode issues on Linux) |
| `InteropGen/Writer/ManagerWriter.Exports.cs` | Added delegate storage to prevent GC collection, Linux-compatible exports |
| `Sandbox.Engine/Core/Interop/NetCore.cs` | Use absolute paths for native library loading on Linux/macOS |
| `Sandbox.Engine/Core/Interop/CreateInterface.cs` | Added fallback path resolution for Linux native libraries |
| `Sandbox.NetCore/NetCore.cs` | Changed entry point to use delegates instead of `UnmanagedCallersOnly` |
| `Sandbox.Reflection/Utility.cs` | Skip `[UnmanagedCallersOnly]` methods during PreJIT (crashes on Linux) |

#### Physics & Ray Tracing
| File | Change |
|------|--------|
| `Sandbox.Engine/Systems/Physics/PhysicsTraceBuilder.cs` | Replaced `[UnmanagedCallersOnly]` with delegate-based callbacks |
| `Sandbox.Engine/Utility/RayTrace/MeshTraceRequest.cs` | Replaced `[UnmanagedCallersOnly]` with delegate-based callbacks |

#### Rendering & Shaders
| File | Change |
|------|--------|
| `Sandbox.Engine/Systems/Render/ShaderCompile/ShaderCompile.cs` | Load Linux `.so` libraries, added debug logging |
| `Sandbox.Engine/Systems/Render/Multimedia/LinuxCursorCapture.cs` | **New file**: Linux cursor/input capture using X11 (stub implementations for cursor, X11 polling for input) |

#### Font & Text Rendering
| File | Change |
|------|--------|
| `Sandbox.Engine/Systems/Render/TextRendering/FontManager.cs` | Added null checks, debug logging for font loading |
| `Sandbox.Engine/Systems/UI/Engine/TextBlock.cs` | Linux font fallbacks (Poppins → Liberation Sans → DejaVu, etc.) |
| `ThirdParty/RichTextKit/FontMapper.cs` | Windows → Linux font mapping (Arial → Liberation Sans, etc.) |
| `ThirdParty/RichTextKit/FontFallback/FontFallback.cs` | Null typeface handling to prevent crashes |
| `ThirdParty/RichTextKit/FontFallback/DefaultCharacterMatcher.cs` | Added Linux fallback font families |
| `ThirdParty/RichTextKit/TextBlock.cs` | Debug logging for BuildFontRuns |

#### Audio
| File | Change |
|------|--------|
| `Sandbox.Engine/Systems/Audio/SteamAudio/BinauralEffect.cs` | Disabled Steam Audio on Linux (context creation fails) |

#### Menu & UI
| File | Change |
|------|--------|
| `Sandbox.Menu/MenuDll.cs` | Added debug logging for menu initialization |
| `game/addons/menu/.../Footer.razor` | Initialize `ActiveFriends` to prevent null reference |

#### Build System & Tools
| File | Change |
|------|--------|
| `Sandbox.AppSystem/ToolAppSystem.cs` | Handle both `/` and `\` path separators |
| `Tools/SboxBuild/Steps/BuildContent.cs` | Support Linux `contentbuilder` path |
| `Tools/InteropGen/Program.cs` | Added `Main()` entry point |
| `Tools/InteropGen/Arguments/ArgDefinedStruct.cs` | Fixed struct pointer passing |
| `Sandbox.Tools/Utility/VoiceRecording.cs` | Stub implementation for Linux |
| `Launcher/StandaloneTest/Launcher.cs` | Skip Windows-only code on Linux |

#### Runtime Configuration
| File | Change |
|------|--------|
| `game/*.runtimeconfig.json` | Updated for Linux runtime compatibility |

#### Cross-Platform Filesystem & Libraries (NEW)
| File | Change |
|------|--------|
| `Sandbox.Filesystem/CaseInsensitivePhysicalFileSystem.cs` | **New file**: Case-insensitive path resolution for Linux |
| `Sandbox.Filesystem/LocalFileSystem.cs` | Use case-insensitive filesystem on Linux |
| `Sandbox.Filesystem/BaseFileSystem.cs` | Case-insensitive SubFileSystem path resolution |
| `Sandbox.Engine/Core/Interop/NativeLibraryResolver.cs` | **New file**: Cross-platform native library loading with `steam_api64` → `libsteam_api.so` mapping |
| `Sandbox.Engine/Systems/Project/Project/Project.cs` | Case-insensitive Code/Assets/Editor path lookup |
| `Sandbox.Engine/Systems/Filesystem/EngineFileSystem.cs` | Native search path initialization for Linux |
| `Sandbox.Engine/Core/Bootstrap.cs` | Initialize native search paths after engine init |
| `Sandbox.AppSystem/AppSystem.cs` | Cross-platform Steam API loading |
| `Sandbox.AppSystem/QtAppSystem.cs` | Cross-platform Steam API loading |
| `Sandbox.AppSystem/MissingDependancyDiagnosis.cs` | Platform-specific dependency checking |
| `Sandbox.GameInstance/GameInstanceDll.cs` | Preserve path casing on Linux |
| `Launcher/Launcher.cs` | Cross-platform paths, LD_LIBRARY_PATH setup |
| `Launcher/Shared/LauncherEnvironment.cs` | Platform-appropriate library path variables |
| `linux/setup.sh` | **New file**: Automated setup script |
| `linux/run.sh` | **New file**: Game launch script |

#### Input System (NEW)
| File | Change |
|------|--------|
| `Sandbox.Engine/Systems/Input/InputRouter.cs` | X11 native input polling for mouse position/buttons |
| `Sandbox.Engine/Systems/Input/InputRouter.Input.cs` | X11 display/window handling with P/Invoke declarations |
| `Sandbox.Engine/Systems/Render/Multimedia/LinuxCursorCapture.cs` | Extended X11 cursor capture with window focus detection |

#### Rendering Pipeline (NEW)
| File | Change |
|------|--------|
| `Sandbox.Engine/Systems/Render/Graphics.Hooks.cs` | Debug hooks for render pipeline investigation |
| `Sandbox.Engine/Systems/Render/RenderPipeline/RenderPipeline.Static.cs` | Static pipeline initialization fixes |

#### Compilation & Threading (NEW)
| File | Change |
|------|--------|
| `Sandbox.Compiling/CompileGroup.cs` | Build tracing for compiler diagnostics |
| `Sandbox.Compiling/Compiler/Compiler.Build.cs` | Enhanced build logging |
| `Sandbox.Engine/Systems/Threads/SyncContext.cs` | Sync context operation tracing |
| `Sandbox.Engine/Systems/Threads/ExpirableSynchronizationContext.cs` | Improved async context handling |
| `Sandbox.Engine/Core/EngineLoop.cs` | Engine loop debugging and callback tracing |

#### Linux Performance Optimizations (NEW - March 2026)
| File | Change |
|------|--------|
| `Sandbox.Engine/Systems/UI/TextureLoadQueue.cs` | **New file**: Incremental texture loading queue (5 textures/frame, 32ms budget) |
| `Sandbox.Engine/Systems/UI/Styles/BaseStyles.Textures.cs` | Non-blocking texture accessors with panel tracking for re-render |
| `Sandbox.Engine/Systems/UI/Render/PanelRenderer.Background.cs` | Pass panel reference when loading textures |
| `Sandbox.Engine/Systems/UI/Render/PanelRenderer.cs` | Time budget for BuildCommandLists |
| `Sandbox.Engine/Systems/UI/Panel/Panel.cs` | Time budget for TickInternal (50ms budget) |
| `Sandbox.Engine/Systems/UI/Panel/Panel.Layout.cs` | Time budget for PreLayout |
| `Sandbox.Engine/Systems/UI/UISystem.cs` | TextureLoadQueue processing each frame |
| `Sandbox.Engine/Systems/Threads/MainThread.cs` | Time budget for queue processing (8ms budget) |
| `Sandbox.Menu/MenuDll.cs` | Time budget for menu tick operations |
| `Sandbox.Engine/Utility/Web/Http.cs` | HTTP timeout 30s (was 120min), connection pooling, 10s connect timeout |
| `Sandbox.System/Extend/UriExtension.cs` | DNS caching with 5min TTL, known domains whitelist, async DNS with 2s timeout |

#### Launch System (NEW)
| File | Change |
|------|--------|
| `Launcher/Sbox/Launcher.cs` | Launch via `dotnet sbox.dll` instead of native executable on Linux |
| `bootstrap.sh` | Linux bootstrap script with automated DXC wrapper compilation and installation |

#### Steam API Fixes (NEW)
| File | Change |
|------|--------|
| `Sandbox.Engine/Platform/Steam/Generated/SteamStructFunctions.cs` | Removed deprecated Stadia P/Invoke calls (Google Stadia shutdown 2023) |
| `Sandbox.Engine/Core/Interop/NativeLibraryResolver.cs` | Added `steam_api64` → `libsteam_api.so` library mapping for P/Invoke resolution |

### DXC Shader Compiler Wrapper

The DirectX Shader Compiler (DXC) on Linux expects UTF-32 encoded arguments, but s&box passes UTF-16. A wrapper library intercepts DXC calls and performs the conversion.

- **Wrapper:** `game/bin/linuxsteamrt64/libdxcompiler.so` (symlink to wrapper)
- **Original:** `game/bin/linuxsteamrt64/libdxcompiler.so.real` (backed up original)
- **Auto-setup:** The `bootstrap.sh` script automatically compiles and installs the wrapper after building

### Symlinks (Automated)

The `linux/setup.sh` script automatically creates all required symlinks:

1. **Library soname symlinks** - Required by Linux dynamic linking (e.g., `libswscale.so.9 → libswscale.so.9.100`)
2. **Engine library symlinks** - Required by native C++ `dlopen` calls (e.g., `libengine2.so → bin/linuxsteamrt64/libengine2.so`)
3. **Case-sensitivity symlinks** - For addon folders referenced with different casing by native code:
   - `assets → Assets`
   - `transients → Transients`
   - `code → Code`
   - `localization → Localization`

The managed C# code now handles most case-sensitivity automatically via `CaseInsensitivePhysicalFileSystem`, reducing the total symlinks from 441 to ~35.

#### Manual Setup (if needed)
```bash
cd linux
./setup.sh
```

### Known Issues
- **Scene loading still takes ~80 seconds** - This is due to native model/mesh/collider deserialization which cannot be optimized from managed code
- RenderDoc warnings appear but don't affect functionality
- Some seasonal/downloaded assets may show as missing

## DXC Wrapper Source

The DXC wrapper source code is located in `linux/dxc_wrapper.c`. To build:

```bash
gcc -shared -fPIC -o libdxcompiler.so dxc_wrapper.c -ldl
```

## Credits

- Reference: [MrSoup678's fork](https://github.com/MrSoup678/sbox-public/tree/master_work) for CasefoldFileSystem concept
- Based on [Facepunch/sbox-public](https://github.com/Facepunch/sbox-public)

## License

The s&box engine source code is licensed under the [MIT License](LICENSE.md).
See the original repository for full license details.
