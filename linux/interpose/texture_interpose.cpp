/**
 * texture_interpose.cpp - Native texture operation tracking for S&box Linux
 * 
 * Hooks the igen_engine function table to track texture create/destroy calls
 * from the native rendering engine. Helps identify texture corruption.
 * 
 * Build: make libtexture_interpose.so
 * Usage: SBOX_NATIVE_TEXTURE_DEBUG=1 ./run.sh
 * Log: /tmp/sbox_native_texture.log
 */

#include <cstdio>
#include <cstdint>
#include <cstring>
#include <dlfcn.h>
#include <atomic>
#include <unordered_map>
#include <mutex>

static FILE* g_log = nullptr;
static std::atomic<int> g_texture_creates{0};
static std::atomic<int> g_texture_destroys{0};
static std::mutex g_mutex;
static std::unordered_map<void*, const char*> g_texture_names;

#define LOG(fmt, ...) do { \
    if (g_log) { fprintf(g_log, "[TEX] " fmt "\n", ##__VA_ARGS__); fflush(g_log); } \
} while(0)

// Known function indices from interop analysis
// These may need adjustment based on actual igen_engine table layout
#define FUNC_CREATE_TEXTURE      100  // Approximate - needs verification
#define FUNC_DESTROY_TEXTURE     101  // Approximate - needs verification
#define FUNC_GET_TEXTURE         102  // Approximate - needs verification

// Typedefs for texture functions (signatures may vary)
typedef void* (*CreateTexture_t)(const char* name, int width, int height, int format);
typedef void (*DestroyTexture_t)(void* texture);
typedef void* (*GetTexture_t)(const char* path);

static CreateTexture_t orig_CreateTexture = nullptr;
static DestroyTexture_t orig_DestroyTexture = nullptr;
static GetTexture_t orig_GetTexture = nullptr;

// Hooked CreateTexture
static void* hooked_CreateTexture(const char* name, int width, int height, int format) {
    int n = ++g_texture_creates;
    LOG("CreateTexture[%d]: name='%s' %dx%d fmt=%d", n, name ? name : "(null)", width, height, format);
    
    void* result = orig_CreateTexture ? orig_CreateTexture(name, width, height, format) : nullptr;
    
    if (result) {
        LOG("CreateTexture[%d]: -> %p", n, result);
        std::lock_guard<std::mutex> lock(g_mutex);
        g_texture_names[result] = name ? strdup(name) : "(unknown)";
    } else {
        LOG("CreateTexture[%d]: FAILED (returned null)", n);
    }
    
    return result;
}

// Hooked DestroyTexture
static void hooked_DestroyTexture(void* texture) {
    int n = ++g_texture_destroys;
    
    const char* name = "(unknown)";
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        auto it = g_texture_names.find(texture);
        if (it != g_texture_names.end()) {
            name = it->second;
            g_texture_names.erase(it);
        } else {
            LOG("DestroyTexture[%d]: WARNING - texture %p not in tracking map (double-free?)", n, texture);
        }
    }
    
    LOG("DestroyTexture[%d]: texture=%p name='%s'", n, texture, name);
    
    if (orig_DestroyTexture) {
        orig_DestroyTexture(texture);
    }
}

// Hooked GetTexture
static void* hooked_GetTexture(const char* path) {
    LOG("GetTexture: path='%s'", path ? path : "(null)");
    
    void* result = orig_GetTexture ? orig_GetTexture(path) : nullptr;
    
    if (result) {
        LOG("GetTexture: '%s' -> %p", path ? path : "(null)", result);
    } else {
        LOG("GetTexture: '%s' -> NULL (missing texture!)", path ? path : "(null)");
    }
    
    return result;
}

// Original igen_engine
typedef void** (*igen_engine_t)(int hash);
static igen_engine_t real_igen_engine = nullptr;

// We intercept igen_engine to patch texture functions
extern "C" void** igen_engine(int hash) {
    if (!real_igen_engine) {
        void* handle = dlopen("libengine2.so", RTLD_NOW | RTLD_NOLOAD);
        if (handle) {
            real_igen_engine = (igen_engine_t)dlsym(handle, "igen_engine");
            dlclose(handle);
        }
        
        if (!real_igen_engine) {
            LOG("ERROR: Cannot find real igen_engine in libengine2.so");
            return nullptr;
        }
    }
    
    void** table = real_igen_engine(hash);
    
    // Log table access for analysis
    static int call_count = 0;
    if (++call_count <= 10) {
        LOG("igen_engine(hash=%d) -> table=%p", hash, (void*)table);
    }
    
    // TODO: Once we identify the correct function indices,
    // uncomment and patch the texture functions:
    /*
    if (table && !orig_CreateTexture) {
        orig_CreateTexture = (CreateTexture_t)table[FUNC_CREATE_TEXTURE];
        orig_DestroyTexture = (DestroyTexture_t)table[FUNC_DESTROY_TEXTURE];
        orig_GetTexture = (GetTexture_t)table[FUNC_GET_TEXTURE];
        
        table[FUNC_CREATE_TEXTURE] = (void*)hooked_CreateTexture;
        table[FUNC_DESTROY_TEXTURE] = (void*)hooked_DestroyTexture;
        table[FUNC_GET_TEXTURE] = (void*)hooked_GetTexture;
        
        LOG("Patched texture functions in igen_engine table");
    }
    */
    
    return table;
}

__attribute__((constructor))
static void init() {
    g_log = fopen("/tmp/sbox_native_texture.log", "w");
    LOG("Native texture interpose library loaded");
    LOG("NOTE: Function hooking requires correct indices - currently logging only");
}

__attribute__((destructor))
static void fini() {
    if (g_log) {
        LOG("=== Native Texture Summary ===");
        LOG("Creates: %d, Destroys: %d", g_texture_creates.load(), g_texture_destroys.load());
        LOG("Unfreed textures: %zu", g_texture_names.size());
        fclose(g_log);
    }
}

