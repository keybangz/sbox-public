/**
 * audio_interpose.cpp - FFmpeg/audio tracking for S&box Linux debugging
 * 
 * Intercepts FFmpeg functions to track audio/video operations and detect issues.
 * 
 * Build: make libaudio_interpose.so
 * Usage: SBOX_AUDIO_DEBUG=1 ./run.sh
 * Log: /tmp/sbox_audio.log
 */

#include <cstdio>
#include <cstdint>
#include <cstring>
#include <dlfcn.h>
#include <atomic>

static FILE* g_log = nullptr;
static std::atomic<int> g_av_calls{0};

#define LOG(fmt, ...) do { \
    if (g_log) { fprintf(g_log, "[AUDIO] " fmt "\n", ##__VA_ARGS__); fflush(g_log); } \
} while(0)

// Track avcodec_open2 calls
typedef int (*avcodec_open2_t)(void*, void*, void**);
static avcodec_open2_t real_avcodec_open2 = nullptr;

extern "C" int avcodec_open2(void* avctx, void* codec, void** options) {
    if (!real_avcodec_open2) {
        real_avcodec_open2 = (avcodec_open2_t)dlsym(RTLD_NEXT, "avcodec_open2");
    }
    
    int n = ++g_av_calls;
    LOG("avcodec_open2[%d]: avctx=%p codec=%p", n, avctx, codec);
    
    int result = real_avcodec_open2 ? real_avcodec_open2(avctx, codec, options) : -1;
    LOG("avcodec_open2[%d]: result=%d", n, result);
    
    return result;
}

// Track av_frame_free to detect double-frees
typedef void (*av_frame_free_t)(void**);
static av_frame_free_t real_av_frame_free = nullptr;

extern "C" void av_frame_free(void** frame) {
    if (!real_av_frame_free) {
        real_av_frame_free = (av_frame_free_t)dlsym(RTLD_NEXT, "av_frame_free");
    }
    
    if (frame && *frame) {
        LOG("av_frame_free: frame=%p", *frame);
    } else if (frame && !*frame) {
        LOG("av_frame_free: WARNING - frame pointer is NULL (potential double-free)");
    }
    
    if (real_av_frame_free) {
        real_av_frame_free(frame);
    }
}

// Track av_packet_free
typedef void (*av_packet_free_t)(void**);
static av_packet_free_t real_av_packet_free = nullptr;

extern "C" void av_packet_free(void** pkt) {
    if (!real_av_packet_free) {
        real_av_packet_free = (av_packet_free_t)dlsym(RTLD_NEXT, "av_packet_free");
    }
    
    if (pkt && *pkt) {
        LOG("av_packet_free: pkt=%p", *pkt);
    } else if (pkt && !*pkt) {
        LOG("av_packet_free: WARNING - packet pointer is NULL");
    }
    
    if (real_av_packet_free) {
        real_av_packet_free(pkt);
    }
}

__attribute__((constructor))
static void init() {
    g_log = fopen("/tmp/sbox_audio.log", "w");
    LOG("Audio interpose library loaded");
}

__attribute__((destructor))
static void fini() {
    if (g_log) {
        LOG("Shutdown. Total AV calls: %d", g_av_calls.load());
        fclose(g_log);
    }
}

