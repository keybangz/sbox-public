/**
 * memory_interpose.cpp - Memory debugging for S&box Linux
 *
 * This is a PASSIVE memory debugging library that does NOT intercept malloc/free.
 * Intercepting malloc/free breaks the .NET runtime.
 *
 * Instead, this library:
 * - Hooks specific engine functions that allocate/free resources
 * - Monitors the igen_engine function table for resource operations
 * - Logs resource lifecycle events
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
#include <unordered_map>
#include <mutex>
#include <execinfo.h>

static FILE* g_log = nullptr;
static std::atomic<int> g_resource_creates{0};
static std::atomic<int> g_resource_destroys{0};
static std::mutex g_mutex;
static std::unordered_map<void*, const char*> g_tracked_resources;

#define LOG(fmt, ...) do { \
    if (g_log) { fprintf(g_log, "[MEM] " fmt "\n", ##__VA_ARGS__); fflush(g_log); } \
} while(0)

static void log_backtrace(int max_frames = 6) {
    void* buffer[16];
    int nptrs = backtrace(buffer, 16);
    char** symbols = backtrace_symbols(buffer, nptrs);
    if (symbols) {
        for (int i = 2; i < nptrs && i < max_frames + 2; i++) {
            LOG("  [%d] %s", i-2, symbols[i]);
        }
        free(symbols);
    }
}

// ============================================================================
// Track resources through igen_engine function calls
// ============================================================================

typedef void** (*igen_engine_t)(int hash);
static igen_engine_t real_igen_engine = nullptr;

// Hook igen_engine to monitor function table access
extern "C" void** igen_engine(int hash) {
    if (!real_igen_engine) {
        void* handle = dlopen("libengine2.so", RTLD_NOW | RTLD_NOLOAD);
        if (handle) {
            real_igen_engine = (igen_engine_t)dlsym(handle, "igen_engine");
            dlclose(handle);
        }

        if (!real_igen_engine) {
            // Try RTLD_DEFAULT as fallback
            real_igen_engine = (igen_engine_t)dlsym(RTLD_DEFAULT, "igen_engine");
        }

        if (!real_igen_engine) {
            LOG("ERROR: Cannot find real igen_engine");
            return nullptr;
        }
        LOG("Found real igen_engine at %p", (void*)real_igen_engine);
    }

    void** table = real_igen_engine(hash);

    // Log function table access (first 10 calls only to avoid spam)
    static std::atomic<int> call_count{0};
    int n = ++call_count;
    if (n <= 10) {
        LOG("igen_engine(hash=0x%08x) -> table=%p", hash, (void*)table);
    } else if (n == 11) {
        LOG("(suppressing further igen_engine logs...)");
    }

    return table;
}

// ============================================================================
// Public API for managed code to track resources
// These can be called via P/Invoke from C# for debugging
// ============================================================================

extern "C" {
    void sbox_mem_track_resource(void* ptr, const char* name) {
        if (!ptr) return;

        int n = ++g_resource_creates;
        LOG("TRACK[%d]: %p = '%s'", n, ptr, name ? name : "(null)");

        std::lock_guard<std::mutex> lock(g_mutex);
        g_tracked_resources[ptr] = name ? strdup(name) : "(unknown)";
    }

    void sbox_mem_untrack_resource(void* ptr) {
        if (!ptr) return;

        int n = ++g_resource_destroys;

        std::lock_guard<std::mutex> lock(g_mutex);
        auto it = g_tracked_resources.find(ptr);
        if (it != g_tracked_resources.end()) {
            LOG("UNTRACK[%d]: %p = '%s'", n, ptr, it->second);
            g_tracked_resources.erase(it);
        } else {
            LOG("UNTRACK[%d]: %p = (NOT TRACKED - possible double-free!)", n, ptr);
            log_backtrace();
        }
    }

    void sbox_mem_dump_tracked() {
        std::lock_guard<std::mutex> lock(g_mutex);
        LOG("=== Currently Tracked Resources ===");
        LOG("Count: %zu", g_tracked_resources.size());
        int i = 0;
        for (const auto& pair : g_tracked_resources) {
            LOG("  [%d] %p = '%s'", i++, pair.first, pair.second);
            if (i >= 100) {
                LOG("  ... (truncated)");
                break;
            }
        }
    }
}

// ============================================================================
// Constructor/Destructor
// ============================================================================

__attribute__((constructor))
static void init() {
    g_log = fopen("/tmp/sbox_memory.log", "w");
    LOG("Memory debug library loaded (passive mode)");
    LOG("This library does NOT intercept malloc/free");
    LOG("Use sbox_mem_track_resource/sbox_mem_untrack_resource for tracking");
}

__attribute__((destructor))
static void fini() {
    if (g_log) {
        LOG("=== Memory Debug Summary ===");
        LOG("Resources tracked: %d", g_resource_creates.load());
        LOG("Resources untracked: %d", g_resource_destroys.load());
        LOG("Still tracked (potential leaks): %zu", g_tracked_resources.size());

        if (!g_tracked_resources.empty()) {
            LOG("=== Leaked Resources ===");
            int i = 0;
            for (const auto& pair : g_tracked_resources) {
                LOG("  [%d] %p = '%s'", i++, pair.first, pair.second);
                if (i >= 50) {
                    LOG("  ... (truncated)");
                    break;
                }
            }
        }

        fclose(g_log);
    }
}

