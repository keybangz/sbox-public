/**
 * sbox_interpose.cpp - Thread and library tracking for S&box Linux debugging
 *
 * Intercepts dlopen, dlsym, pthread_create to track library loading and threading.
 *
 * Build: make libsbox_interpose.so
 * Usage: SBOX_INTERPOSE=1 ./run.sh
 * Log: /tmp/sbox_interpose.log
 */

#include <cstdio>
#include <cstdint>
#include <cstring>
#include <dlfcn.h>
#include <pthread.h>
#include <atomic>
#include <unistd.h>

static FILE* g_log = nullptr;
static std::atomic<int> g_thread_count{0};
static std::atomic<int> g_dlopen_count{0};

#define LOG(fmt, ...) do { \
    if (g_log) { fprintf(g_log, "[INTERPOSE] " fmt "\n", ##__VA_ARGS__); fflush(g_log); } \
} while(0)

// Intercept dlopen
extern "C" void* dlopen(const char* filename, int flags) {
    static void* (*real_dlopen)(const char*, int) = nullptr;
    if (!real_dlopen) {
        real_dlopen = (void*(*)(const char*, int))dlsym(RTLD_NEXT, "dlopen");
    }
    
    int n = ++g_dlopen_count;
    if (filename) {
        LOG("dlopen[%d]: %s (flags=0x%x)", n, filename, flags);
    }
    
    void* handle = real_dlopen(filename, flags);
    
    if (filename) {
        LOG("dlopen[%d]: %s -> %p", n, filename, handle);
    }
    
    return handle;
}

// Note: We don't intercept dlsym because it causes bootstrapping issues
// The dlopen interception is sufficient for library tracking

// Intercept pthread_create
extern "C" int pthread_create(pthread_t* thread, const pthread_attr_t* attr,
                              void* (*start_routine)(void*), void* arg) {
    static int (*real_pthread_create)(pthread_t*, const pthread_attr_t*,
                                      void*(*)(void*), void*) = nullptr;
    if (!real_pthread_create) {
        real_pthread_create = (int(*)(pthread_t*, const pthread_attr_t*,
                                      void*(*)(void*), void*))dlsym(RTLD_NEXT, "pthread_create");
    }
    
    int n = ++g_thread_count;
    LOG("pthread_create[%d]: start_routine=%p", n, (void*)start_routine);
    
    int result = real_pthread_create(thread, attr, start_routine, arg);
    
    if (result == 0 && thread) {
        LOG("pthread_create[%d]: thread=%lu", n, (unsigned long)*thread);
    }
    
    return result;
}

__attribute__((constructor))
static void init() {
    g_log = fopen("/tmp/sbox_interpose.log", "w");
    LOG("Interpose library loaded");
    LOG("PID: %d", getpid());
}

__attribute__((destructor))
static void fini() {
    if (g_log) {
        LOG("Shutdown. Threads: %d, dlopen calls: %d", 
            g_thread_count.load(), g_dlopen_count.load());
        fclose(g_log);
    }
}

