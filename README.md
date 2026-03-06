# 🐧 s&box Linux Native Client

**Status:** ✅ STABLE, yeah! definitely...
<img width="1911" height="1072" alt="Screenshot_20260307_015248" src="https://github.com/user-attachments/assets/4411404f-be8a-4b1e-afa4-1f3b7d23f80f" />

This fork contains fixes and modifications to run s&box natively on Linux.

### Quick Start (Linux)

```bash
cd game && ./sbox
```

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
| `Sandbox.Engine/Systems/Render/Multimedia/LinuxCursorCapture.cs` | **New file**: Linux cursor capture using SDL3 |

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

### DXC Shader Compiler Wrapper

The DirectX Shader Compiler (DXC) on Linux expects UTF-32 encoded arguments, but s&box passes UTF-16. A wrapper library intercepts DXC calls and performs the conversion.

- **Wrapper:** `game/bin/linuxsteamrt64/libdxcompiler.so`
- **Original:** `game/bin/linuxsteamrt64/libdxcompiler.so.real`

### Required Symlinks

Linux has a case-sensitive filesystem. Symlinks are required to handle inconsistent casing in code references.

#### ⚠️ Critical Rules
- **NEVER** create symlinks for `.razor`, `.scss`, or `.cs` files
- **NEVER** create symlinks inside `Code/` directories
- Only create symlinks for Asset directories and top-level addon directories

#### Library Symlinks
```bash
# In game/bin/linuxsteamrt64/
ln -sf libswscale.so.9.1.100 libswscale.so.9
ln -sf libSkiaSharp.so.116.0.0 libSkiaSharp.so
ln -sf libHarfBuzzSharp.so.0.60830.0 libHarfBuzzSharp.so

# In game/ root
ln -sf bin/linuxsteamrt64/libsteam_api.so steam_api64.so
ln -sf bin/linuxsteamrt64/libsteam_api.so libsteam_api64.so
ln -sf bin/linuxsteamrt64/libSkiaSharp.so.116.0.0 libSkiaSharp.so
ln -sf bin/linuxsteamrt64/libHarfBuzzSharp.so.0.60830.0 libHarfBuzzSharp.so

# In game/bin/managed/
ln -sf ../linuxsteamrt64/libsteam_api.so steam_api64.so
ln -sf ../linuxsteamrt64/libsteam_api.so libsteam_api64.so
ln -sf ../linuxsteamrt64/libSkiaSharp.so.116.0.0 libSkiaSharp.so
ln -sf ../linuxsteamrt64/libHarfBuzzSharp.so.0.60830.0 libHarfBuzzSharp.so
```

#### Addon Directory Symlinks
```bash
# For each addon at ROOT level only:
for addon in game/addons/*/; do
    [ -d "${addon}Assets" ] && [ ! -e "${addon}assets" ] && ln -s Assets "${addon}assets"
    [ -d "${addon}Code" ] && [ ! -e "${addon}code" ] && ln -s Code "${addon}code"
    [ -d "${addon}Localization" ] && [ ! -e "${addon}localization" ] && ln -s Localization "${addon}localization"
    [ -d "${addon}ProjectSettings" ] && [ ! -e "${addon}projectsettings" ] && ln -s ProjectSettings "${addon}projectsettings"
done

# Lowercase symlinks for directories inside Assets (NOT Code!)
find game/addons -path "*/Assets/*" -type d | while read dir; do
    basename=$(basename "$dir")
    if [[ "$basename" =~ [A-Z] ]]; then
        lower=$(echo "$basename" | tr '[:upper:]' '[:lower:]')
        parent=$(dirname "$dir")
        [ "$basename" != "$lower" ] && [ ! -e "$parent/$lower" ] && ln -s "$basename" "$parent/$lower"
    fi
done

# Special symlinks
ln -s code/Styles game/addons/base/styles
ln -s Assets/shaders game/addons/base/shaders
cd game/addons/base/code && ln -s Styles styles
cd game/addons/menu/Code && ln -s MainMenu.razor.scss mainmenu.razor.scss
```

#### Core Directory
```bash
mkdir -p game/core/shaders
cp game/addons/base/Assets/shaders/colorgrading.shader_c game/core/shaders/
```

### Known Issues
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
