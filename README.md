<div align="center">
  <img src="https://sbox.game/img/sbox-logo-square.svg" width="80px" alt="s&box logo">

  [Website] | [Getting Started] | [Forums] | [Documentation] | [Contributing]
</div>

[Website]: https://sbox.game/
[Getting Started]: https://sbox.game/dev/doc/about/getting-started/first-steps/
[Forums]: https://sbox.game/f/
[Documentation]: https://sbox.game/dev/doc/
[Contributing]: CONTRIBUTING.md

# s&box

s&box is a modern game engine, built on Valve's Source 2 and the latest .NET technology, it provides a modern intuitive editor for creating games.

![s&box editor](https://files.facepunch.com/matt/1b2211b1/sbox-dev_FoZ5NNZQTi.jpg)

If your goal is to create games using s&box, please start with the [getting started guide](https://sbox.game/dev/doc/about/getting-started/first-steps/).
This repository is for building the engine from source for those who want to contribute to the development of the engine.

## Getting the Engine

### Steam

You can download and install the s&box editor directly from [Steam](https://sbox.game/give-me-that).

### Compiling from Source

If you want to build from source, this repository includes all the necessary files to compile the engine yourself.

#### Prerequisites

* [Git](https://git-scm.com/install/windows)
* [Visual Studio 2026](https://visualstudio.microsoft.com/)
* [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download)

#### Building

```bash
# Clone the repo
git clone https://github.com/Facepunch/sbox-public.git
```

Once you've cloned the repo simply run `Bootstrap.bat` which will download dependencies and build the engine.

The game and editor can be run from the binaries in the game folder.

---

## 🐧 Linux Native Client

**Status:** ✅ STABLE

This fork contains fixes and modifications to run s&box natively on Linux.

### Quick Start (Linux)

```bash
cd game && ./sbox
```

### Code Changes Summary

| Fix | Description | Files |
|-----|-------------|-------|
| **GC Transition** | Disabled `[SuppressGCTransition]` on Linux (causes crashes) | `InteropGen/Writer/ManagerWriter*.cs` |
| **Native Library Paths** | Use absolute paths for native library loading on Linux | `Sandbox.Engine/Core/Interop/NetCore.cs` |
| **PreJIT Fix** | Skip `[UnmanagedCallersOnly]` methods during PreJIT | `Sandbox.Reflection/Utility.cs` |
| **Steam Audio Bypass** | Disabled Steam Audio binaural effects on Linux | `Sandbox.Engine/Systems/Audio/SteamAudio/BinauralEffect.cs` |
| **Linux Path Handling** | Fixed path separator handling for Linux | `Sandbox.AppSystem/ToolAppSystem.cs` |

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

### Credits
- Reference: [MrSoup678's fork](https://github.com/MrSoup678/sbox-public/tree/master_work) for CasefoldFileSystem concept

---

## Contributing

If you would like to contribute to the engine, please see the [contributing guide](CONTRIBUTING.md).

If you want to report bugs or request new features, see [sbox-issues](https://github.com/Facepunch/sbox-public/issues/).

## Documentation

Full documentation, tutorials, and API references are available at [sbox.game/dev/](https://sbox.game/dev/).

## License

The s&box engine source code is licensed under the [MIT License](LICENSE.md).

Certain native binaries in `game/bin` are not covered by the MIT license. These binaries are distributed under the s&box EULA. You must agree to the terms of the EULA to use them.

This project includes third-party components that are separately licensed.
Those components are not covered by the MIT license above and remain subject
to their original licenses as indicated in `game/thirdpartylegalnotices`.