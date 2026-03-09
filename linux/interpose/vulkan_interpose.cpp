/**
 * vulkan_interpose.cpp - Vulkan/SDL3 GPU tracking for S&box Linux debugging
 * 
 * Intercepts SDL3 GPU functions to track rendering operations.
 * 
 * Build: make libvulkan_interpose.so
 * Usage: SBOX_VULKAN_DEBUG=1 ./run.sh
 * Log: /tmp/sbox_vulkan.log
 */

#include <cstdio>
#include <cstdint>
#include <cstring>
#include <dlfcn.h>
#include <atomic>

static FILE* g_log = nullptr;
static std::atomic<int> g_gpu_calls{0};
static std::atomic<bool> g_swap_chain_created{false};

#define LOG(fmt, ...) do { \
    if (g_log) { fprintf(g_log, "[VULKAN] " fmt "\n", ##__VA_ARGS__); fflush(g_log); } \
} while(0)

// Track SDL_GPU_CreateSwapchainComposition or similar
typedef void* (*SDL_CreateGPUDevice_t)(unsigned int, int, void*);
static SDL_CreateGPUDevice_t real_SDL_CreateGPUDevice = nullptr;

extern "C" void* SDL_CreateGPUDevice(unsigned int flags, int debug, void* props) {
    if (!real_SDL_CreateGPUDevice) {
        real_SDL_CreateGPUDevice = (SDL_CreateGPUDevice_t)dlsym(RTLD_NEXT, "SDL_CreateGPUDevice");
    }
    
    int n = ++g_gpu_calls;
    LOG("SDL_CreateGPUDevice[%d]: flags=0x%x debug=%d", n, flags, debug);
    
    void* result = real_SDL_CreateGPUDevice ? real_SDL_CreateGPUDevice(flags, debug, props) : nullptr;
    LOG("SDL_CreateGPUDevice[%d]: result=%p", n, result);
    
    return result;
}

// Track swap chain claims
typedef int (*SDL_ClaimWindowForGPUDevice_t)(void*, void*);
static SDL_ClaimWindowForGPUDevice_t real_SDL_ClaimWindowForGPUDevice = nullptr;

extern "C" int SDL_ClaimWindowForGPUDevice(void* device, void* window) {
    if (!real_SDL_ClaimWindowForGPUDevice) {
        real_SDL_ClaimWindowForGPUDevice = (SDL_ClaimWindowForGPUDevice_t)
            dlsym(RTLD_NEXT, "SDL_ClaimWindowForGPUDevice");
    }
    
    LOG("SDL_ClaimWindowForGPUDevice: device=%p window=%p", device, window);
    
    int result = real_SDL_ClaimWindowForGPUDevice ? 
        real_SDL_ClaimWindowForGPUDevice(device, window) : 0;
    
    if (result) {
        g_swap_chain_created = true;
        LOG("SDL_ClaimWindowForGPUDevice: SUCCESS - swap chain should be ready");
    } else {
        LOG("SDL_ClaimWindowForGPUDevice: FAILED");
    }
    
    return result;
}

// Track GPU submit
typedef int (*SDL_SubmitGPUCommandBuffer_t)(void*);
static SDL_SubmitGPUCommandBuffer_t real_SDL_SubmitGPUCommandBuffer = nullptr;

extern "C" int SDL_SubmitGPUCommandBuffer(void* cmdBuffer) {
    if (!real_SDL_SubmitGPUCommandBuffer) {
        real_SDL_SubmitGPUCommandBuffer = (SDL_SubmitGPUCommandBuffer_t)
            dlsym(RTLD_NEXT, "SDL_SubmitGPUCommandBuffer");
    }
    
    static int submit_count = 0;
    if (++submit_count <= 5 || submit_count % 1000 == 0) {
        LOG("SDL_SubmitGPUCommandBuffer[%d]: cmdBuffer=%p", submit_count, cmdBuffer);
    }
    
    return real_SDL_SubmitGPUCommandBuffer ? 
        real_SDL_SubmitGPUCommandBuffer(cmdBuffer) : 0;
}

__attribute__((constructor))
static void init() {
    g_log = fopen("/tmp/sbox_vulkan.log", "w");
    LOG("Vulkan/SDL3 GPU interpose library loaded");
}

__attribute__((destructor))
static void fini() {
    if (g_log) {
        LOG("Shutdown. GPU calls: %d, swap chain created: %s", 
            g_gpu_calls.load(), g_swap_chain_created.load() ? "yes" : "no");
        fclose(g_log);
    }
}

