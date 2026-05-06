/**
 * scene_interpose.cpp - Scene system debugging for S&box Linux
 * 
 * Hooks scene-related functions to track scene creation, layer processing,
 * and rendering operations that may cause crashes.
 * 
 * Build: make libscene_interpose.so
 * Usage: SBOX_SCENE_DEBUG=1 ./run.sh
 * Log: /tmp/sbox_scene.log
 */

#define _GNU_SOURCE
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <dlfcn.h>
#include <atomic>
#include <execinfo.h>

static FILE* g_log = nullptr;
static std::atomic<int> g_render_count{0};
static std::atomic<int> g_scene_count{0};
static std::atomic<int> g_layer_count{0};

#define LOG(fmt, ...) do { \
    if (g_log) { fprintf(g_log, "[SCENE] " fmt "\n", ##__VA_ARGS__); fflush(g_log); } \
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
// Symbol interception - we intercept known scene system functions
// ============================================================================

// ProcessProceduralLayer - this is where the crash occurs
typedef void (*ProcessProceduralLayer_t)(void* pView, void* pLayer);
static ProcessProceduralLayer_t real_ProcessProceduralLayer = nullptr;

// RenderAView
typedef void (*RenderAView_t)(void* pView, float flCurTime, void* perFrameStats, void* hConstants);
static RenderAView_t real_RenderAView = nullptr;

// CCameraRenderer::Render
typedef void (*CCameraRenderer_Render_t)(void* self, void* swapChain);
static CCameraRenderer_Render_t real_CCameraRenderer_Render = nullptr;

// Try to find and hook functions
static void* find_symbol(const char* name) {
    void* sym = dlsym(RTLD_DEFAULT, name);
    if (!sym) {
        // Try in scenesystem
        void* handle = dlopen("libscenesystem.so", RTLD_NOW | RTLD_NOLOAD);
        if (handle) {
            sym = dlsym(handle, name);
            dlclose(handle);
        }
    }
    return sym;
}

// ============================================================================
// Hooked functions
// ============================================================================

extern "C" void _Z22ProcessProceduralLayerP10ISceneViewP17CProceduralLayer(void* pView, void* pLayer) {
    int n = ++g_layer_count;
    LOG("ProcessProceduralLayer[%d]: pView=%p pLayer=%p", n, pView, pLayer);
    
    if (!pView) {
        LOG("  ERROR: pView is NULL!");
        log_backtrace();
        return;
    }
    
    if (!pLayer) {
        LOG("  ERROR: pLayer is NULL!");
        log_backtrace();
        return;
    }
    
    if (!real_ProcessProceduralLayer) {
        real_ProcessProceduralLayer = (ProcessProceduralLayer_t)find_symbol("_Z22ProcessProceduralLayerP10ISceneViewP17CProceduralLayer");
    }
    
    if (real_ProcessProceduralLayer) {
        real_ProcessProceduralLayer(pView, pLayer);
    } else {
        LOG("  ERROR: Cannot find real ProcessProceduralLayer!");
    }
}

// ============================================================================
// igen_engine hook - log function table access
// ============================================================================

typedef void** (*igen_engine_t)(int hash);
static igen_engine_t real_igen_engine = nullptr;

extern "C" void** igen_engine(int hash) {
    if (!real_igen_engine) {
        void* handle = dlopen("libengine2.so", RTLD_NOW | RTLD_NOLOAD);
        if (handle) {
            real_igen_engine = (igen_engine_t)dlsym(handle, "igen_engine");
            dlclose(handle);
        }
        
        if (!real_igen_engine) {
            LOG("ERROR: Cannot find real igen_engine");
            return nullptr;
        }
    }
    
    void** table = real_igen_engine(hash);
    
    // Log first few calls for debugging
    static int call_count = 0;
    if (++call_count <= 5) {
        LOG("igen_engine(hash=0x%08x) -> %p", hash, (void*)table);
    }
    
    return table;
}

// ============================================================================
// Constructor/Destructor
// ============================================================================

__attribute__((constructor))
static void init() {
    g_log = fopen("/tmp/sbox_scene.log", "w");
    LOG("Scene interpose library loaded");
    LOG("Monitoring scene rendering operations...");
    
    // Try to find symbols early
    real_ProcessProceduralLayer = (ProcessProceduralLayer_t)find_symbol("_Z22ProcessProceduralLayerP10ISceneViewP17CProceduralLayer");
    if (real_ProcessProceduralLayer) {
        LOG("Found ProcessProceduralLayer at %p", (void*)real_ProcessProceduralLayer);
    }
}

__attribute__((destructor))
static void fini() {
    if (g_log) {
        LOG("=== Scene Summary ===");
        LOG("Total renders: %d", g_render_count.load());
        LOG("Total scenes: %d", g_scene_count.load());
        LOG("Total layers processed: %d", g_layer_count.load());
        fclose(g_log);
    }
}

