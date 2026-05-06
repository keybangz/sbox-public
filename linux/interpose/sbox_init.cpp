// sbox_init.cpp — s&box Linux startup interpose
//
// This library is loaded as a DT_NEEDED dependency of game/sbox so it runs
// automatically without any LD_PRELOAD or wrapper scripts.
//
// It combines two essential fixes:
//
// 1. OpenSSL provider init (from openssl_init_interpose.cpp)
//    Force-initialize system libcrypto's provider subsystem before engine2 loads.
//    Without this, EVP_KEYMGMT_is_a crashes due to uninitialized provider store.
//
// 2. Permissive free() (from permissive_free_interpose.cpp)
//    libmeshsystem.so calls free() on pointers that were never malloc'd
//    (engine2 passes mmap'd/stack buffers to meshsystem which tries to free them).
//    This causes "double free or corruption" -> SIGABRT.
//    Fix: validate the malloc chunk header before calling real free().

#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif
#include <dlfcn.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <unistd.h>
#include <sys/mman.h>
#include <stdint.h>
#include <errno.h>

// ============================================================================
// OpenSSL provider initialization
// ============================================================================

__attribute__((constructor))
static void sbox_openssl_init(void)
{
    void* libcrypto = dlopen("libcrypto.so.3", RTLD_NOW | RTLD_GLOBAL | RTLD_NOLOAD);
    if (!libcrypto)
        libcrypto = dlopen("libcrypto.so.3", RTLD_NOW | RTLD_GLOBAL);
    if (!libcrypto)
        return;

    typedef int (*openssl_init_fn)(uint64_t opts, const void* settings);
    openssl_init_fn init_fn = (openssl_init_fn)dlsym(libcrypto, "OPENSSL_init_crypto");
    if (init_fn)
        init_fn(0x4 | 0x8 | 0x2 | 0x40, NULL); // ADD_ALL_CIPHERS|ADD_ALL_DIGESTS|LOAD_CRYPTO_STRINGS|LOAD_CONFIG

    typedef void* (*provider_load_fn)(void* ctx, const char* name);
    provider_load_fn load_fn = (provider_load_fn)dlsym(libcrypto, "OSSL_PROVIDER_load");
    if (load_fn) {
        load_fn(NULL, "default");
        load_fn(NULL, "legacy"); // optional
    }

    void* libssl = dlopen("libssl.so.3", RTLD_NOW | RTLD_GLOBAL | RTLD_NOLOAD);
    if (!libssl) libssl = dlopen("libssl.so.3", RTLD_NOW | RTLD_GLOBAL);
    if (libssl) {
        typedef int (*ssl_init_fn)(uint64_t opts, const void* settings);
        ssl_init_fn ssl_init = (ssl_init_fn)dlsym(libssl, "OPENSSL_init_ssl");
        if (ssl_init) ssl_init(0, NULL);
    }
}

// ============================================================================
// Permissive free() — validate malloc chunk before freeing
// ============================================================================

extern "C" void free(void* ptr)
{
    static void (*real_free)(void*) = nullptr;
    if (!real_free)
        real_free = (void(*)(void*))dlsym(RTLD_NEXT, "free");

    if (!ptr) { real_free(ptr); return; }

    // Check page is mapped
    uintptr_t page = (uintptr_t)ptr & ~(uintptr_t)(4095);
    if (msync((void*)page, 4096, MS_ASYNC) != 0) return; // unmapped — drop

    // Validate chunk header
    size_t chunk_size = *(size_t*)((char*)ptr - 8);
    size_t real_size = chunk_size & ~(size_t)7;
    bool valid = (real_size >= 32) && (real_size < 0x10000000UL) && ((real_size & 0xF) == 0);

    if (valid && !(chunk_size & 1)) {
        size_t prev_size = *(size_t*)((char*)ptr - 16);
        size_t real_prev = prev_size & ~(size_t)7;
        if (real_prev < 32 || real_prev >= 0x10000000UL || (real_prev & 0xF) != 0)
            valid = false;
    }

    if (!valid) return; // drop invalid free

    real_free(ptr);
}

extern "C" void* realloc(void* ptr, size_t size)
{
    static void* (*real_realloc)(void*, size_t) = nullptr;
    if (!real_realloc)
        real_realloc = (void*(*)(void*, size_t))dlsym(RTLD_NEXT, "realloc");

    if (!ptr) return real_realloc(nullptr, size);

    uintptr_t page = (uintptr_t)ptr & ~(uintptr_t)(4095);
    if (msync((void*)page, 4096, MS_ASYNC) != 0)
        return size ? malloc(size) : nullptr; // unmapped — alloc fresh

    size_t chunk_size = *(size_t*)((char*)ptr - 8);
    size_t real_size = chunk_size & ~(size_t)7;
    bool valid = (real_size >= 32) && (real_size < 0x10000000UL) && ((real_size & 0xF) == 0);

    if (valid && !(chunk_size & 1)) {
        size_t prev_size = *(size_t*)((char*)ptr - 16);
        size_t real_prev = prev_size & ~(size_t)7;
        if (real_prev < 32 || real_prev >= 0x10000000UL || (real_prev & 0xF) != 0)
            valid = false;
    }

    if (!valid) return size ? malloc(size) : nullptr; // alloc fresh

    return real_realloc(ptr, size);
}
