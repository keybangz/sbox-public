# IDA Guide: Finding GetSwapChainSize for Potential Future Fixes

**Target:** `CEngineServiceMgr::GetSwapChainSize`  
**Library:** `game/bin/linuxsteamrt64/libengine2.so`

---

## Key Offsets from IDA Analysis

| Symbol | Offset | Purpose |
|--------|--------|---------|
| `GetEngineSwapChain` (wrapper) | `0x1E25C0` | Returns swap chain handle |
| `GetEngineSwapChainSize` (wrapper) | `0x1E25E0` | Gets dimensions |
| `g_pEngineServiceMgr` | `0xC786F0` | Global service manager |
| `igen_engine` | `0x203280` | Interop initialization |
| Vtable offset for real impl | `+0xA8` | Virtual function in vtable |

---

## HOT Path Disassembly (GetEngineSwapChainSize)

```asm
.text:00000000001E25E0  endbr64
.text:00000000001E25E4  lea     rax, g_pEngineServiceMgr
.text:00000000001E25EB  mov     rcx, rdi
.text:00000000001E25EE  mov     rdx, rsi
.text:00000000001E25F1  mov     rdi, [rax]      ; Load service mgr ptr
.text:00000000001E25F4  test    rdi, rdi        ; NULL CHECK
.text:00000000001E25F7  jz      cold_path       ; Jump if null
.text:00000000001E25FD  mov     rax, [rdi]      ; Load vtable
.text:00000000001E2600  mov     rsi, rcx
.text:00000000001E2603  mov     rax, [rax+0A8h] ; Get vfunc at vtable+0xA8
.text:00000000001E260A  jmp     rax             ; Tail call to ACTUAL impl
```

---

## The Problem

The wrapper checks if `g_pEngineServiceMgr` is null, but the **actual implementation** at `vtable+0xA8` crashes because it doesn't check if the swap chain pointer is null.

---

## Function Pointer Table Index

From `Interop.Engine.cs`:
- `GetEngineSwapChainSize` is at index **1428** in `nativeFunctions[]`
- `SourceEngineFrame` is at index **1609**

---

## Notes for Future Investigation

1. The crash happens during `SourceEnginePreInit`, not `SourceEngineFrame`
2. The native engine internally calls `GetSwapChainSize` before swap chain exists
3. This is a bug in the precompiled `libengine2.so`
4. Potential fix: binary patch to add null check at vtable+0xA8 implementation

