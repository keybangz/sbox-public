#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

// Force-initialize system libcrypto's provider subsystem before any SSL use.
// This runs as a constructor in the preloaded library, before engine2 loads.

__attribute__((constructor))
static void force_openssl_init(void)
{
    // Open system libcrypto explicitly (not RTLD_DEEPBIND - we want the system one)
    void* libcrypto = dlopen("libcrypto.so.3", RTLD_NOW | RTLD_GLOBAL | RTLD_NOLOAD);
    if (!libcrypto) {
        libcrypto = dlopen("libcrypto.so.3", RTLD_NOW | RTLD_GLOBAL);
    }
    if (!libcrypto) {
        fprintf(stderr, "[OPENSSL_INIT] Failed to open libcrypto.so.3: %s\n", dlerror());
        return;
    }

    // OPENSSL_init_crypto with OPENSSL_INIT_LOAD_CONFIG | OPENSSL_INIT_ADD_ALL_CIPHERS | OPENSSL_INIT_ADD_ALL_DIGESTS
    typedef int (*openssl_init_fn)(uint64_t opts, const void* settings);
    openssl_init_fn init_fn = (openssl_init_fn)dlsym(libcrypto, "OPENSSL_init_crypto");
    if (init_fn) {
        // OPENSSL_INIT_LOAD_CONFIG=0x40, ADD_ALL_CIPHERS=0x4, ADD_ALL_DIGESTS=0x8, LOAD_CRYPTO_STRINGS=0x2
        int ret = init_fn(0x4 | 0x8 | 0x2 | 0x40, NULL);
        fprintf(stderr, "[OPENSSL_INIT] OPENSSL_init_crypto returned %d\n", ret);
    } else {
        fprintf(stderr, "[OPENSSL_INIT] Could not find OPENSSL_init_crypto\n");
    }

    // Also load the default provider explicitly via OSSL_PROVIDER_load
    typedef void* (*provider_load_fn)(void* ctx, const char* name);
    provider_load_fn load_fn = (provider_load_fn)dlsym(libcrypto, "OSSL_PROVIDER_load");
    if (load_fn) {
        void* prov = load_fn(NULL, "default");
        fprintf(stderr, "[OPENSSL_INIT] OSSL_PROVIDER_load(default) = %p\n", prov);
        void* legacy = load_fn(NULL, "legacy");
        fprintf(stderr, "[OPENSSL_INIT] OSSL_PROVIDER_load(legacy) = %p (optional)\n", legacy);
    } else {
        fprintf(stderr, "[OPENSSL_INIT] Could not find OSSL_PROVIDER_load\n");
    }

    // Open libssl too to ensure it's initialized
    void* libssl = dlopen("libssl.so.3", RTLD_NOW | RTLD_GLOBAL | RTLD_NOLOAD);
    if (!libssl) libssl = dlopen("libssl.so.3", RTLD_NOW | RTLD_GLOBAL);
    if (libssl) {
        typedef int (*ssl_init_fn)(uint64_t opts, const void* settings);
        ssl_init_fn ssl_init = (ssl_init_fn)dlsym(libssl, "OPENSSL_init_ssl");
        if (ssl_init) {
            int ret = ssl_init(0, NULL);
            fprintf(stderr, "[OPENSSL_INIT] OPENSSL_init_ssl returned %d\n", ret);
        }
    }

    fprintf(stderr, "[OPENSSL_INIT] System OpenSSL provider initialization complete\n");
}
