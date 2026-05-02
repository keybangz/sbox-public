#include <dlfcn.h>
#include <cstdio>
#include <cstring>
#include <cstdlib>

// SDL3 dynapi interpose for s&box Linux
//
// Problem: librendersystemvulkan.so calls dlsym(handle, "SDL_Foo_REAL") for
// 1,251 SDL functions. Standard SDL3 builds hide _REAL symbols via
// -fvisibility=hidden + version script "local: *". dlsym returns NULL ->
// null-pointer call -> silent render system Init failure.
//
// Fix: Intercept dlsym(). If the requested symbol ends with "_REAL", strip
// the suffix and look up the public name instead.

static void* (*real_dlsym)(void*, const char*) = nullptr;
static void* (*real_dlvsym)(void*, const char*, const char*) = nullptr;

__attribute__((constructor))
static void sdl3_dynapi_interpose_init() {
    real_dlsym  = (void*(*)(void*, const char*))  dlvsym(RTLD_NEXT, "dlsym",  "GLIBC_2.34");
    real_dlvsym = (void*(*)(void*, const char*, const char*)) dlvsym(RTLD_NEXT, "dlvsym", "GLIBC_2.34");
    if (!real_dlsym)
        real_dlsym = (void*(*)(void*, const char*)) dlvsym(RTLD_NEXT, "dlsym", "GLIBC_2.2.5");
    if (!real_dlvsym)
        real_dlvsym = (void*(*)(void*, const char*, const char*)) dlvsym(RTLD_NEXT, "dlvsym", "GLIBC_2.2.5");
}

// Strip "_REAL" suffix and return pointer to static buffer, or NULL if no suffix
static const char* strip_real_suffix(const char* name) {
    if (!name) return nullptr;
    size_t len = strlen(name);
    static const char suffix[] = "_REAL";
    static const size_t slen = sizeof(suffix) - 1;
    if (len > slen && memcmp(name + len - slen, suffix, slen) == 0) {
        // Use a thread-local buffer to avoid allocation
        static thread_local char buf[256];
        size_t base_len = len - slen;
        if (base_len >= sizeof(buf)) return nullptr; // name too long
        memcpy(buf, name, base_len);
        buf[base_len] = '\0';
        return buf;
    }
    return nullptr;
}

extern "C" void* dlsym(void* handle, const char* name) {
    if (!real_dlsym) {
        // Bootstrap: use dlvsym directly from libc
        real_dlsym = (void*(*)(void*, const char*)) dlvsym(RTLD_NEXT, "dlsym", "GLIBC_2.34");
        if (!real_dlsym)
            real_dlsym = (void*(*)(void*, const char*)) dlvsym(RTLD_NEXT, "dlsym", "GLIBC_2.2.5");
    }

    const char* stripped = strip_real_suffix(name);
    if (stripped) {
        void* sym = real_dlsym(handle, stripped);
        if (sym) {
            static bool logged_first = false;
            if (!logged_first) {
                fprintf(stderr, "[sdl3_dynapi] Redirecting SDL _REAL dlsym calls to public symbols (e.g. %s -> %s)\n", name, stripped);
                logged_first = true;
            }
            return sym;
        }
        // Fall through: try the original name (will likely return NULL too, but be safe)
    }

    return real_dlsym(handle, name);
}
