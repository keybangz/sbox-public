/*
 * DXC UTF-16 to UTF-32 Argument Converter for s&box Linux
 * 
 * Problem: s&box's librendersystemvulkan.so passes DXC arguments as UTF-16
 *          (Windows wchar_t), but Linux DXC expects UTF-32 (Linux wchar_t).
 * 
 * Solution: Intercept IDxcCompiler3::Compile and convert arguments.
 * 
 * Build: gcc -shared -fPIC -o libdxcompiler_wrapper.so dxc_wrapper_fix.c -ldl -Wl,--no-as-needed
 * Install: ln -sf libdxcompiler_wrapper.so libdxcompiler.so
 */
#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <dlfcn.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>

typedef struct { uint32_t a; uint16_t b, c; uint8_t d[8]; } GUID;
typedef struct { void* ptr; size_t size; uint32_t enc; } DxcBuffer;
typedef struct { void** vtbl; } ComObj;
typedef struct HookedObj { ComObj* obj; void** orig_vtbl; void* hooked_vtbl[16]; struct HookedObj* next; } HookedObj;

static HookedObj* hooked_objs = NULL;
static void* real_dxc_lib = NULL;
typedef int32_t (*DxcCreateInstanceFn)(const GUID*, const GUID*, void**);
static DxcCreateInstanceFn real_DxcCreateInstance = NULL;

// Convert UTF-16 args to Linux wchar_t (UTF-32)
static wchar_t* convert_utf16_to_wchar(const uint16_t** args, uint32_t argc, wchar_t*** out_args) {
    if (!args || argc == 0) { *out_args = NULL; return NULL; }
    
    size_t total_chars = 0;
    for (uint32_t i = 0; i < argc; i++) {
        const uint16_t* s = args[i];
        if (!s) break;
        while (*s++) total_chars++;
        total_chars++;
    }
    
    wchar_t* buffer = malloc(total_chars * sizeof(wchar_t));
    wchar_t** ptrs = malloc((argc + 1) * sizeof(wchar_t*));
    
    wchar_t* dst = buffer;
    for (uint32_t i = 0; i < argc; i++) {
        const uint16_t* src = args[i];
        if (!src) { ptrs[i] = NULL; break; }
        ptrs[i] = dst;
        while (*src) {
            uint16_t c = *src++;
            if (c >= 0xD800 && c <= 0xDBFF && *src >= 0xDC00 && *src <= 0xDFFF) {
                *dst++ = (wchar_t)(0x10000 + ((c - 0xD800) << 10) + (*src++ - 0xDC00));
            } else {
                *dst++ = (wchar_t)c;
            }
        }
        *dst++ = 0;
    }
    ptrs[argc] = NULL;
    
    *out_args = ptrs;
    return buffer;
}

// Hooked Compile - converts UTF-16 args to UTF-32
static int32_t fixed_compile(ComObj* self, const DxcBuffer* src, const wchar_t** args, uint32_t argc,
                             void* inc, const GUID* riid, void** ppResult) {
    HookedObj* h = hooked_objs;
    while (h && h->obj != self) h = h->next;
    if (!h) return 0x80004005;
    
    wchar_t** wchar_args = NULL;
    wchar_t* wchar_buffer = convert_utf16_to_wchar((const uint16_t**)args, argc, &wchar_args);
    
    int32_t (*orig)(ComObj*, const DxcBuffer*, const wchar_t**, uint32_t, void*, const GUID*, void**);
    orig = (void*)h->orig_vtbl[3];
    int32_t hr = orig(self, src, (const wchar_t**)wchar_args, argc, inc, riid, ppResult);
    
    free(wchar_buffer);
    free(wchar_args);
    return hr;
}

__attribute__((constructor)) static void init(void) {
    // Get real dlopen/dlsym
    void* (*real_dlopen)(const char*, int) = dlvsym(RTLD_NEXT, "dlopen", "GLIBC_2.34");
    if (!real_dlopen) real_dlopen = dlvsym(RTLD_NEXT, "dlopen", "GLIBC_2.2.5");
    
    // Load real DXC (must be named .real)
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
    
    if (real_dxc_lib) {
        void* (*real_dlsym)(void*, const char*) = dlvsym(RTLD_NEXT, "dlsym", "GLIBC_2.34");
        if (!real_dlsym) real_dlsym = dlvsym(RTLD_NEXT, "dlsym", "GLIBC_2.2.5");
        real_DxcCreateInstance = (DxcCreateInstanceFn)real_dlsym(real_dxc_lib, "DxcCreateInstance");
    }
}

int32_t DxcCreateInstance(const GUID* c, const GUID* i, void** p) {
    if (!real_DxcCreateInstance) return 0x80004005;
    int32_t hr = real_DxcCreateInstance(c, i, p);
    
    if (hr == 0 && p && *p) {
        ComObj* obj = (ComObj*)*p;
        HookedObj* h = calloc(1, sizeof(HookedObj));
        h->obj = obj; h->orig_vtbl = obj->vtbl; h->next = hooked_objs; hooked_objs = h;
        memcpy(h->hooked_vtbl, obj->vtbl, 16 * sizeof(void*));
        h->hooked_vtbl[3] = fixed_compile;
        obj->vtbl = h->hooked_vtbl;
    }
    return hr;
}

int32_t DxcCreateInstance2(void* m, const GUID* c, const GUID* i, void** p) { 
    return DxcCreateInstance(c, i, p); 
}

