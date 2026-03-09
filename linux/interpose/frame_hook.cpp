/**
 * frame_hook.cpp - Hook SourceEngineFrame to delay until swap chain ready
 * 
 * Problem: Native engine calls GetSwapChainSize in MainLoop before swap chain exists
 * Solution: Hook igen_engine and patch the SourceEngineFrame function pointer
 * 
 * Build: make libframe_hook.so
 * Usage: SBOX_FRAME_HOOK=1 ./run.sh
 * Log: /tmp/sbox_frame_hook.log
 */

#include <cstdio>
#include <cstdint>
#include <cstring>
#include <dlfcn.h>
#include <atomic>

// From Interop.Engine.cs analysis
#define FUNC_INDEX_SOURCEENGINEFRAME  1609
#define ENGINE2_GETSWAPCHAIN_OFFSET   0x1E25C0

static FILE* g_log = nullptr;
static std::atomic<bool> g_hooks_installed{false};
static std::atomic<int> g_frame_count{0};
static std::atomic<bool> g_swap_chain_ready{false};

typedef void* (*GetEngineSwapChain_t)(void);
typedef bool (*SourceEngineFrame_t)(void* appDict, float time, float prevTime);

static GetEngineSwapChain_t orig_GetEngineSwapChain = nullptr;
static SourceEngineFrame_t orig_SourceEngineFrame = nullptr;

#define LOG(fmt, ...) do { \
    if (g_log) { fprintf(g_log, "[FRAME] " fmt "\n", ##__VA_ARGS__); fflush(g_log); } \
} while(0)

// Check if swap chain exists
static bool is_swap_chain_ready() {
    if (g_swap_chain_ready) return true;
    if (orig_GetEngineSwapChain) {
        void* sc = orig_GetEngineSwapChain();
        if (sc != nullptr) {
            g_swap_chain_ready = true;
            LOG("Swap chain detected at %p", sc);
            return true;
        }
    }
    return false;
}

// Our hooked SourceEngineFrame
static bool hooked_SourceEngineFrame(void* appDict, float time, float prevTime) {
    int frame = ++g_frame_count;
    
    if (frame <= 5) {
        LOG("Frame %d: appDict=%p time=%f", frame, appDict, time);
    }
    
    if (!is_swap_chain_ready()) {
        if (frame <= 10) {
            LOG("Frame %d: Swap chain not ready, skipping", frame);
        }
        return true; // Keep running but skip native frame
    }
    
    if (orig_SourceEngineFrame) {
        return orig_SourceEngineFrame(appDict, time, prevTime);
    }
    return true;
}

// Original igen_engine
typedef void** (*igen_engine_t)(int hash);
static igen_engine_t real_igen_engine = nullptr;

// Hooked igen_engine - patches the function table
static void** hooked_igen_engine(int hash) {
    LOG("igen_engine called with hash=%d (0x%X)", hash, hash);
    
    if (!real_igen_engine) {
        void* handle = dlopen("libengine2.so", RTLD_NOW | RTLD_NOLOAD);
        if (handle) {
            real_igen_engine = (igen_engine_t)dlsym(handle, "igen_engine");
            dlclose(handle);
        }
    }
    
    if (!real_igen_engine) {
        LOG("ERROR: Cannot find real igen_engine");
        return nullptr;
    }
    
    void** table = real_igen_engine(hash);
    LOG("igen_engine returned table at %p", (void*)table);
    
    if (!table) return nullptr;
    
    // Get GetEngineSwapChain for checking
    void* base = (void*)((uint8_t*)real_igen_engine - 0x203280);
    orig_GetEngineSwapChain = (GetEngineSwapChain_t)
        ((uint8_t*)base + ENGINE2_GETSWAPCHAIN_OFFSET);
    
    // Save and patch SourceEngineFrame
    orig_SourceEngineFrame = (SourceEngineFrame_t)table[FUNC_INDEX_SOURCEENGINEFRAME];
    LOG("Original SourceEngineFrame[%d] = %p", FUNC_INDEX_SOURCEENGINEFRAME, 
        (void*)orig_SourceEngineFrame);
    
    table[FUNC_INDEX_SOURCEENGINEFRAME] = (void*)hooked_SourceEngineFrame;
    LOG("Patched to %p", (void*)hooked_SourceEngineFrame);
    
    g_hooks_installed = true;
    return table;
}

// Symbol interposition
extern "C" void** igen_engine(int hash) {
    return hooked_igen_engine(hash);
}

__attribute__((constructor))
static void init() {
    g_log = fopen("/tmp/sbox_frame_hook.log", "w");
    LOG("Frame hook library loaded");
}

__attribute__((destructor))
static void fini() {
    if (g_log) {
        LOG("Shutdown. Frames: %d, swap chain: %s", 
            g_frame_count.load(), g_swap_chain_ready.load() ? "yes" : "no");
        fclose(g_log);
    }
}

