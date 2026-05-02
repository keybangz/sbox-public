#define _GNU_SOURCE
#include <dlfcn.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <unistd.h>
#include <sys/mman.h>
#include <stdint.h>
#include <errno.h>

// Permissive free() interpose for s&box Linux
//
// Problem: libmeshsystem.so calls free() on pointers that were never malloc'd
// (engine2 passes mmap'd/stack buffers to meshsystem which tries to free them).
// This causes munmap_chunk(): invalid pointer -> SIGABRT.
//
// Fix: Validate the malloc chunk header before calling real free(). If the
// pointer doesn't look like a valid malloc chunk, silently drop the free.
//
// Validation: glibc malloc chunk header is 16 bytes before the user pointer.
// The chunk size field (at ptr-8) must be:
//   - Non-zero
//   - Aligned to 16 bytes (low 4 bits are flags: PREV_INUSE=1, IS_MMAPPED=2, NON_MAIN_ARENA=4)
//   - Reasonable size (< 1TB)
//   - The memory at ptr-16 must be readable

static FILE* g_log = nullptr;
static bool g_log_enabled = false;

// Check if a memory address is readable without crashing
// Uses /proc/self/maps would be too slow; use mincore() instead
static bool is_readable(const void* ptr, size_t len) {
    // Quick alignment check
    uintptr_t addr = (uintptr_t)ptr;
    // Use msync to check if page is mapped (returns -1 with ENOMEM if not mapped)
    // Round down to page boundary
    uintptr_t page = addr & ~(uintptr_t)(4095);
    return msync((void*)page, 4096, MS_ASYNC) == 0;
}

// Validate that ptr looks like a valid malloc'd pointer
// Returns true if safe to call free(ptr)
static bool is_valid_malloc_ptr(void* ptr) {
    if (!ptr) return true; // free(NULL) is always valid

    uintptr_t addr = (uintptr_t)ptr;

    // Must be aligned to at least 8 bytes
    if (addr & 7) return false;

    // Check that the chunk header (16 bytes before ptr) is readable
    void* chunk = (void*)(addr - 16);
    if (!is_readable(chunk, 16)) return false;

    // Read the chunk size field (at ptr - 8 in glibc malloc)
    // Layout: [prev_size (8 bytes)][size|flags (8 bytes)][user data...]
    uintptr_t size_field = *((uintptr_t*)((char*)ptr - 8));

    // Strip the 3 flag bits (PREV_INUSE, IS_MMAPPED, NON_MAIN_ARENA)
    uintptr_t chunk_size = size_field & ~(uintptr_t)7;

    // Chunk size must be:
    // - At least 32 bytes (minimum malloc chunk on 64-bit)
    // - A multiple of 16 (malloc alignment)
    // - Less than 1TB (sanity cap)
    if (chunk_size < 32) return false;
    if (chunk_size & 15) return false;
    if (chunk_size > (uintptr_t)1024 * 1024 * 1024 * 1024) return false;

    return true;
}

extern "C" void free(void* ptr) {
    static void (*real_free)(void*) = nullptr;
    if (!real_free)
        real_free = (void(*)(void*))dlsym(RTLD_NEXT, "free");

    if (!is_valid_malloc_ptr(ptr)) {
        if (g_log_enabled && g_log && ptr) {
            fprintf(g_log, "[PERMFREE] Dropping invalid free(%p)\n", ptr);
            fflush(g_log);
        }
        return; // Silently drop
    }

    real_free(ptr);
}

extern "C" void* realloc(void* ptr, size_t size) {
    static void* (*real_realloc)(void*, size_t) = nullptr;
    if (!real_realloc)
        real_realloc = (void*(*)(void*, size_t))dlsym(RTLD_NEXT, "realloc");

    if (ptr && !is_valid_malloc_ptr(ptr)) {
        if (g_log_enabled && g_log) {
            fprintf(g_log, "[PERMFREE] realloc(%p, %zu): invalid ptr, allocating fresh\n", ptr, size);
            fflush(g_log);
        }
        // Can't realloc an invalid pointer - allocate fresh instead
        return size ? malloc(size) : nullptr;
    }

    return real_realloc(ptr, size);
}

__attribute__((constructor))
static void init() {
    // Only enable logging if SBOX_PERMFREE_LOG=1
    const char* log_env = getenv("SBOX_PERMFREE_LOG");
    if (log_env && log_env[0] == '1') {
        g_log_enabled = true;
        g_log = fopen("/tmp/sbox_permfree.log", "w");
        if (g_log) {
            fprintf(g_log, "[PERMFREE] Permissive free interpose loaded, PID=%d\n", getpid());
            fflush(g_log);
        }
    }
}

__attribute__((destructor))
static void fini() {
    if (g_log) { fclose(g_log); g_log = nullptr; }
}
