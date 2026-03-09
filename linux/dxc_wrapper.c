/*
 * DXC UTF-16 to UTF-32 Argument Converter for s&box Linux
 *
 * Problem: s&box's librendersystemvulkan.so passes DXC arguments as UTF-16
 *          (Windows wchar_t = 2 bytes), but Linux DXC expects UTF-32
 *          (Linux wchar_t = 4 bytes).
 *
 * Solution: Intercept IDxcCompiler3::Compile and convert the LPCWSTR* arguments
 *           from UTF-16 to UTF-32 before calling the real DXC.
 *
 * Build: gcc -shared -fPIC -o libdxcompiler_wrapper.so dxc_wrapper.c -ldl -Wl,--no-as-needed
 * Install: ln -sf libdxcompiler_wrapper.so libdxcompiler.so
 *          (ensure libdxcompiler.so.real exists as the original)
 */
#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <dlfcn.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>

// ===========================================================================
// Type definitions matching DXC API
// ===========================================================================

typedef struct {
    uint32_t Data1;
    uint16_t Data2, Data3;
    uint8_t Data4[8];
} GUID;

// DxcBuffer - structure for passing source data
// From dxcapi.h:
//   LPCVOID Ptr;    // Pointer to the start of the buffer
//   SIZE_T Size;    // Size of the buffer in bytes
//   UINT Encoding;  // Encoding (0 for binary, DXC_CP_UTF8, etc.)
typedef struct {
    const void* Ptr;
    size_t Size;
    uint32_t Encoding;
} DxcBuffer;

// COM object with vtable pointer
typedef struct { void** vtbl; } ComObj;

// Linked list of hooked COM objects
typedef struct HookedObj {
    ComObj* obj;
    void** orig_vtbl;
    void* hooked_vtbl[16];
    struct HookedObj* next;
} HookedObj;

// CLSID for IDxcCompiler3 - only hook this specific class
// {73e22d93-e6ce-47f3-b5bf-f0664f39c1b0}
static const GUID CLSID_DxcCompiler = {
    0x73e22d93, 0xe6ce, 0x47f3,
    {0xb5, 0xbf, 0xf0, 0x66, 0x4f, 0x39, 0xc1, 0xb0}
};

// ===========================================================================
// Global state
// ===========================================================================

static HookedObj* hooked_objs = NULL;
static void* real_dxc_lib = NULL;
typedef int32_t (*DxcCreateInstanceFn)(const GUID*, const GUID*, void**);
static DxcCreateInstanceFn real_DxcCreateInstance = NULL;

// ===========================================================================
// UTF-16 to UTF-32 conversion
// ===========================================================================

// Compare two GUIDs
static int guid_equals(const GUID* a, const GUID* b) {
    return memcmp(a, b, sizeof(GUID)) == 0;
}

// Convert an array of UTF-16 (Windows wchar_t) strings to UTF-32 (Linux wchar_t)
// Returns a single buffer containing all converted strings, and fills out_args
// with pointers into that buffer.
static wchar_t* convert_utf16_to_wchar(const uint16_t** args, uint32_t argc, wchar_t*** out_args) {
    if (!args || argc == 0) {
        *out_args = NULL;
        return NULL;
    }

    // Calculate total size needed
    size_t total_chars = 0;
    for (uint32_t i = 0; i < argc; i++) {
        const uint16_t* s = args[i];
        if (!s) continue;
        while (*s++) total_chars++;
        total_chars++; // null terminator
    }

    if (total_chars == 0) {
        *out_args = NULL;
        return NULL;
    }

    wchar_t* buffer = malloc(total_chars * sizeof(wchar_t));
    wchar_t** ptrs = malloc((argc + 1) * sizeof(wchar_t*));
    if (!buffer || !ptrs) {
        free(buffer);
        free(ptrs);
        *out_args = NULL;
        return NULL;
    }

    wchar_t* dst = buffer;
    for (uint32_t i = 0; i < argc; i++) {
        const uint16_t* src = args[i];
        if (!src) {
            ptrs[i] = NULL;
            continue;
        }
        ptrs[i] = dst;

        // Convert UTF-16 to UTF-32, handling surrogate pairs
        while (*src) {
            uint16_t c = *src++;
            if (c >= 0xD800 && c <= 0xDBFF && *src >= 0xDC00 && *src <= 0xDFFF) {
                // Surrogate pair - combine into single UTF-32 codepoint
                *dst++ = (wchar_t)(0x10000 + ((c - 0xD800) << 10) + (*src++ - 0xDC00));
            } else {
                *dst++ = (wchar_t)c;
            }
        }
        *dst++ = 0; // null terminator
    }
    ptrs[argc] = NULL;

    *out_args = ptrs;
    return buffer;
}

// ===========================================================================
// IDxcCompiler3::Compile hook
// ===========================================================================

// IDxcCompiler3 vtable layout:
//   [0] QueryInterface
//   [1] AddRef
//   [2] Release
//   [3] Compile - HRESULT Compile(const DxcBuffer*, LPCWSTR*, UINT32, IDxcIncludeHandler*, REFIID, LPVOID*)
//   [4] Disassemble
#define VTABLE_INDEX_COMPILE 3

// Hooked Compile method - converts UTF-16 LPCWSTR* to UTF-32
//
// Parameters passed through directly (unchanged):
//   - self: COM object pointer
//   - pSource: DxcBuffer* - contains shader source (binary data, no conversion needed)
//   - pIncludeHandler: IDxcIncludeHandler* - COM interface pointer
//   - riid: REFIID - GUID reference (binary, no conversion needed)
//   - ppResult: LPVOID* - output pointer
//
// Parameters requiring conversion:
//   - pArguments: LPCWSTR* - array of wide string pointers (UTF-16 -> UTF-32)
//   - argCount: UINT32 - count of arguments (passed through)
//
static int32_t hooked_compile(
    ComObj* self,               // this pointer
    const DxcBuffer* pSource,   // Source shader code - passed through as-is
    const wchar_t** pArguments, // Array of arguments - NEEDS CONVERSION
    uint32_t argCount,          // Number of arguments - passed through
    void* pIncludeHandler,      // Include handler - passed through as-is
    const GUID* riid,           // Result interface ID - passed through as-is
    void** ppResult             // Output result - passed through as-is
) {
    // Find our hook state for this object
    HookedObj* h = hooked_objs;
    while (h && h->obj != self) h = h->next;
    if (!h) {
        // Object not found in our list - should not happen
        return 0x80004005; // E_FAIL
    }

    // Convert UTF-16 arguments to UTF-32
    wchar_t** converted_args = NULL;
    wchar_t* converted_buffer = convert_utf16_to_wchar(
        (const uint16_t**)pArguments,
        argCount,
        &converted_args
    );

    // Call original Compile with converted arguments
    // All other parameters are passed through unchanged
    typedef int32_t (*CompileFn)(
        ComObj*,            // self
        const DxcBuffer*,   // pSource
        const wchar_t**,    // pArguments
        uint32_t,           // argCount
        void*,              // pIncludeHandler
        const GUID*,        // riid
        void**              // ppResult
    );

    CompileFn orig_compile = (CompileFn)h->orig_vtbl[VTABLE_INDEX_COMPILE];

    int32_t hr = orig_compile(
        self,
        pSource,           // Pass through unchanged - binary data
        (const wchar_t**)converted_args,  // Converted from UTF-16 to UTF-32
        argCount,          // Pass through unchanged
        pIncludeHandler,   // Pass through unchanged - COM interface
        riid,              // Pass through unchanged - binary GUID
        ppResult           // Pass through unchanged - output pointer
    );

    // Clean up converted arguments
    free(converted_buffer);
    free(converted_args);

    return hr;
}

// ===========================================================================
// Initialization
// ===========================================================================

__attribute__((constructor))
static void init(void) {
    // Get versioned dlopen - try newer version first
    void* (*real_dlopen)(const char*, int) = dlvsym(RTLD_NEXT, "dlopen", "GLIBC_2.34");
    if (!real_dlopen) {
        real_dlopen = dlvsym(RTLD_NEXT, "dlopen", "GLIBC_2.2.5");
    }
    if (!real_dlopen) {
        return; // Cannot proceed without dlopen
    }

    // Try to load the real DXC library (must be named .real)
    const char* paths[] = {
        "./bin/linuxsteamrt64/libdxcompiler.so.real",
        "./libdxcompiler.so.real",
        "libdxcompiler.so.real",
        "bin/linuxsteamrt64/libdxcompiler.so.real",
        NULL
    };

    for (int i = 0; paths[i] && !real_dxc_lib; i++) {
        real_dxc_lib = real_dlopen(paths[i], RTLD_NOW | RTLD_LOCAL);
    }

    if (!real_dxc_lib) {
        return; // Real DXC not found
    }

    // Get DxcCreateInstance from the real library
    void* (*real_dlsym)(void*, const char*) = dlvsym(RTLD_NEXT, "dlsym", "GLIBC_2.34");
    if (!real_dlsym) {
        real_dlsym = dlvsym(RTLD_NEXT, "dlsym", "GLIBC_2.2.5");
    }
    if (real_dlsym) {
        real_DxcCreateInstance = (DxcCreateInstanceFn)real_dlsym(real_dxc_lib, "DxcCreateInstance");
    }
}

// ===========================================================================
// Exported DXC API functions
// ===========================================================================

// DxcCreateInstance - creates DXC objects
// We only hook IDxcCompiler3, other classes are passed through unchanged
int32_t DxcCreateInstance(const GUID* rclsid, const GUID* riid, void** ppv) {
    if (!real_DxcCreateInstance) {
        return 0x80004005; // E_FAIL
    }

    // Call the real DxcCreateInstance
    int32_t hr = real_DxcCreateInstance(rclsid, riid, ppv);

    // Only hook if successful AND creating the DXC Compiler
    if (hr == 0 && ppv && *ppv && guid_equals(rclsid, &CLSID_DxcCompiler)) {
        ComObj* obj = (ComObj*)*ppv;

        // Create hook state
        HookedObj* h = calloc(1, sizeof(HookedObj));
        if (h) {
            h->obj = obj;
            h->orig_vtbl = obj->vtbl;
            h->next = hooked_objs;
            hooked_objs = h;

            // Copy vtable and replace Compile
            memcpy(h->hooked_vtbl, obj->vtbl, 16 * sizeof(void*));
            h->hooked_vtbl[VTABLE_INDEX_COMPILE] = hooked_compile;

            // Replace vtable pointer
            obj->vtbl = h->hooked_vtbl;
        }
    }

    return hr;
}

// DxcCreateInstance2 - variant with custom allocator
// We ignore the allocator and delegate to DxcCreateInstance
int32_t DxcCreateInstance2(void* pMalloc, const GUID* rclsid, const GUID* riid, void** ppv) {
    (void)pMalloc; // Unused - the real DXC may handle this differently
    return DxcCreateInstance(rclsid, riid, ppv);
}

