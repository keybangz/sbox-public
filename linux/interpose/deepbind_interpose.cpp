#define _GNU_SOURCE
#include <dlfcn.h>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <unistd.h>
#include <linux/limits.h>

static FILE* g_log = nullptr;

// Game bin dir — set from SBOX_BIN_DIR env var at startup
static char g_bin_dir[PATH_MAX] = {};

#define RTLD_DEEPBIND 0x8

// Libraries that need RTLD_DEEPBIND to isolate their private OpenSSL symbols
// from the global glibc namespace. Only engine2, tier0, and steam_api bundle
// private OpenSSL — adding other libs breaks cross-namespace pthread_once callbacks.
static const char* DEEPBIND_LIBS[] = {
    "libengine2",
    "libtier0",
    "libsteam_api",
    "libSystem.Security.Cryptography.Native.OpenSsl",
    nullptr
};

// Libraries that the native engine looks for next to the dotnet binary
// (/usr/share/dotnet/) but actually live in game/bin/linuxsteamrt64/.
// We intercept and redirect these to the correct location.
static const char* REDIRECT_LIBS[] = {
    "librendersystemvulkan.so",
    "libmaterialsystem2.so",
    "libmeshsystem.so",
    "libanimationsystem.so",
    "libfilesystem_stdio.so",
    "libschemasystem.so",
    "libvfx_vulkan.so",
    "libphonon.so",
    "liblocalize.so",
    "libdxcompiler.so",
    nullptr
};

static bool needs_deepbind(const char* filename) {
    if (!filename) return false;
    for (int i = 0; DEEPBIND_LIBS[i]; i++) {
        if (strstr(filename, DEEPBIND_LIBS[i])) return true;
    }
    return false;
}

// Returns the basename of a path (pointer into the same string)
static const char* basename_of(const char* path) {
    const char* slash = strrchr(path, '/');
    return slash ? slash + 1 : path;
}

// If filename is a wrong-path reference to a known engine lib, return a
// corrected absolute path into g_bin_dir. Returns nullptr if no redirect needed.
static const char* maybe_redirect(const char* filename, char* out_buf, size_t out_size) {
    if (!filename || g_bin_dir[0] == '\0') return nullptr;

    // Only redirect if the path doesn't already point into our bin dir
    if (strstr(filename, g_bin_dir)) return nullptr;

    const char* base = basename_of(filename);
    for (int i = 0; REDIRECT_LIBS[i]; i++) {
        if (strcmp(base, REDIRECT_LIBS[i]) == 0) {
            snprintf(out_buf, out_size, "%s/%s", g_bin_dir, base);
            return out_buf;
        }
    }
    return nullptr;
}

extern "C" void* dlopen(const char* filename, int flags) {
    static void* (*real_dlopen)(const char*, int) = nullptr;
    if (!real_dlopen)
        real_dlopen = (void*(*)(const char*, int))dlsym(RTLD_NEXT, "dlopen");

    // --- Path redirect: wrong-dir engine libs ---
    char redirect_buf[PATH_MAX];
    const char* effective_filename = filename;
    const char* redirected = maybe_redirect(filename, redirect_buf, sizeof(redirect_buf));
    if (redirected) {
        if (g_log) {
            fprintf(g_log, "[REDIRECT] %s -> %s\n", filename ? filename : "(null)", redirected);
            fflush(g_log);
        }
        effective_filename = redirected;
    }

    // --- RTLD_DEEPBIND: OpenSSL collision prevention ---
    if (needs_deepbind(effective_filename) && !(flags & RTLD_DEEPBIND)) {
        int new_flags = flags | RTLD_DEEPBIND;
        if (g_log) {
            fprintf(g_log, "[DEEPBIND] %s: flags 0x%x -> 0x%x\n", effective_filename, flags, new_flags);
            fflush(g_log);
        }
        flags = new_flags;
    }

    return real_dlopen(effective_filename, flags);
}

// ===========================================================================
// OpenSSL symbol redirect: CRYPTO_THREAD_run_once
// ===========================================================================
//
// Problem: libengine2.so exports CRYPTO_THREAD_run_once into the global symbol
// table even though it's DEEPBIND'd. When libcrypto.so.3 calls this function,
// the dynamic linker finds engine2's copy first, which uses engine2's private
// OPENSSL_LH_retrieve as the pthread_once init routine. This corrupts system
// libcrypto's hash table state -> SIGSEGV.
//
// Fix: Interpose CRYPTO_THREAD_run_once so all callers get the system
// libcrypto's version, not engine2's.

typedef int (*CryptoThreadRunOnceFn)(void* once, void (*init)(void));

extern "C" int CRYPTO_THREAD_run_once(void* once, void (*init)(void)) {
    static CryptoThreadRunOnceFn system_fn = nullptr;
    static bool initialized = false;

    if (!initialized) {
        initialized = true;
        // Try to get libcrypto's own CRYPTO_THREAD_run_once by loading it
        // with RTLD_DEEPBIND so its symbol lookup is isolated from engine2
        const char* libcrypto_paths[] = {
            "/lib/x86_64-linux-gnu/libcrypto.so.3",
            "/usr/lib/x86_64-linux-gnu/libcrypto.so.3",
            "libcrypto.so.3",
            nullptr
        };
        void* libcrypto = nullptr;
        for (int i = 0; libcrypto_paths[i] && !libcrypto; i++) {
            libcrypto = dlopen(libcrypto_paths[i], RTLD_NOW | RTLD_DEEPBIND | RTLD_NOLOAD);
        }
        if (!libcrypto) {
            for (int i = 0; libcrypto_paths[i] && !libcrypto; i++) {
                libcrypto = dlopen(libcrypto_paths[i], RTLD_NOW | RTLD_DEEPBIND);
            }
        }
        if (libcrypto) {
            system_fn = (CryptoThreadRunOnceFn)dlsym(libcrypto, "CRYPTO_THREAD_run_once");
            if (g_log) {
                fprintf(g_log, "[OPENSSL] CRYPTO_THREAD_run_once redirect: libcrypto=%p fn=%p\n", libcrypto, (void*)system_fn);
                fflush(g_log);
            }
        }
    }

    if (system_fn) {
        return system_fn(once, init);
    }
    init();
    return 1;
}

__attribute__((constructor))
static void init() {
    // Read game bin dir from env (set by run.sh)
    const char* bin_dir = getenv("SBOX_BIN_DIR");
    if (bin_dir) {
        strncpy(g_bin_dir, bin_dir, sizeof(g_bin_dir) - 1);
    }

    g_log = fopen("/tmp/sbox_deepbind.log", "w");
    if (g_log) {
        fprintf(g_log, "[DEEPBIND] Interpose loaded, PID=%d\n", getpid());
        fprintf(g_log, "[DEEPBIND] SBOX_BIN_DIR=%s\n", g_bin_dir[0] ? g_bin_dir : "(not set)");
        fflush(g_log);
    }
}

__attribute__((destructor))
static void fini() {
    if (g_log) { fclose(g_log); g_log = nullptr; }
}
