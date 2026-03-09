/**
 * memory_interpose.cpp - Memory and texture tracking for S&box Linux debugging
 * 
 * Intercepts malloc/free to detect heap corruption, double-frees, and 
 * tracks texture-related memory operations.
 * 
 * Build: make libmemory_interpose.so
 * Usage: SBOX_MEMORY_DEBUG=1 ./run.sh
 * Log: /tmp/sbox_memory.log
 */

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <dlfcn.h>
#include <atomic>
#include <unordered_set>
#include <mutex>
#include <execinfo.h>
#include <cxxabi.h>

static FILE* g_log = nullptr;
static std::atomic<size_t> g_alloc_count{0};
static std::atomic<size_t> g_free_count{0};
static std::atomic<size_t> g_total_allocated{0};
static std::atomic<size_t> g_double_free_count{0};
static std::mutex g_mutex;
static std::unordered_set<void*> g_allocated_ptrs;
static bool g_initialized = false;
static bool g_in_hook = false; // Prevent recursion

#define LOG(fmt, ...) do { \
    if (g_log && !g_in_hook) { fprintf(g_log, "[MEM] " fmt "\n", ##__VA_ARGS__); fflush(g_log); } \
} while(0)

// Track large allocations (likely textures)
static constexpr size_t LARGE_ALLOC_THRESHOLD = 1024 * 1024; // 1MB

static void log_backtrace() {
    void* buffer[16];
    int nptrs = backtrace(buffer, 16);
    char** symbols = backtrace_symbols(buffer, nptrs);
    if (symbols) {
        for (int i = 2; i < nptrs && i < 8; i++) { // Skip first 2 frames (our code)
            LOG("  [%d] %s", i-2, symbols[i]);
        }
        free(symbols);
    }
}

// Real function pointers
static void* (*real_malloc)(size_t) = nullptr;
static void (*real_free)(void*) = nullptr;
static void* (*real_realloc)(void*, size_t) = nullptr;
static void* (*real_calloc)(size_t, size_t) = nullptr;

static void ensure_real_funcs() {
    if (!real_malloc) {
        real_malloc = (void*(*)(size_t))dlsym(RTLD_NEXT, "malloc");
        real_free = (void(*)(void*))dlsym(RTLD_NEXT, "free");
        real_realloc = (void*(*)(void*, size_t))dlsym(RTLD_NEXT, "realloc");
        real_calloc = (void*(*)(size_t, size_t))dlsym(RTLD_NEXT, "calloc");
    }
}

extern "C" void* malloc(size_t size) {
    ensure_real_funcs();
    void* ptr = real_malloc(size);
    
    if (g_initialized && ptr && !g_in_hook) {
        g_in_hook = true;
        ++g_alloc_count;
        g_total_allocated += size;
        
        // Track large allocations (potential textures)
        if (size >= LARGE_ALLOC_THRESHOLD) {
            LOG("LARGE malloc(%zu) = %p (%.2f MB)", size, ptr, size / (1024.0 * 1024.0));
            log_backtrace();
        }
        
        {
            std::lock_guard<std::mutex> lock(g_mutex);
            g_allocated_ptrs.insert(ptr);
        }
        g_in_hook = false;
    }
    
    return ptr;
}

extern "C" void free(void* ptr) {
    ensure_real_funcs();
    
    if (g_initialized && ptr && !g_in_hook) {
        g_in_hook = true;
        ++g_free_count;
        
        {
            std::lock_guard<std::mutex> lock(g_mutex);
            auto it = g_allocated_ptrs.find(ptr);
            if (it == g_allocated_ptrs.end()) {
                ++g_double_free_count;
                LOG("DOUBLE-FREE or invalid free(%p)!", ptr);
                log_backtrace();
            } else {
                g_allocated_ptrs.erase(it);
            }
        }
        g_in_hook = false;
    }
    
    real_free(ptr);
}

extern "C" void* realloc(void* ptr, size_t size) {
    ensure_real_funcs();
    
    if (g_initialized && ptr && !g_in_hook) {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_allocated_ptrs.erase(ptr);
    }
    
    void* new_ptr = real_realloc(ptr, size);
    
    if (g_initialized && new_ptr && !g_in_hook) {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_allocated_ptrs.insert(new_ptr);
    }
    
    return new_ptr;
}

extern "C" void* calloc(size_t nmemb, size_t size) {
    ensure_real_funcs();
    void* ptr = real_calloc(nmemb, size);
    
    if (g_initialized && ptr && !g_in_hook) {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_allocated_ptrs.insert(ptr);
    }
    
    return ptr;
}

__attribute__((constructor))
static void init() {
    ensure_real_funcs();
    g_log = fopen("/tmp/sbox_memory.log", "w");
    g_initialized = true;
    LOG("Memory interpose library loaded");
    LOG("Tracking large allocations >= %zu bytes", LARGE_ALLOC_THRESHOLD);
}

__attribute__((destructor))
static void fini() {
    g_initialized = false;
    if (g_log) {
        LOG("=== Memory Summary ===");
        LOG("Total allocations: %zu", g_alloc_count.load());
        LOG("Total frees: %zu", g_free_count.load());
        LOG("Double-frees detected: %zu", g_double_free_count.load());
        LOG("Leaked pointers: %zu", g_allocated_ptrs.size());
        fclose(g_log);
    }
}

