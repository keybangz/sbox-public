// sbox_init.cpp — s&box Linux startup interpose
//
// This library is loaded as a DT_NEEDED dependency of game/sbox so it runs
// automatically without any LD_PRELOAD or wrapper scripts.
//
// OpenSSL provider init: Force-initialize system libcrypto's provider subsystem
// before engine2 loads. Without this, EVP_KEYMGMT_is_a crashes due to
// uninitialized provider store.

#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif
#include <dlfcn.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <unistd.h>
#include <sys/mman.h>
#include <stdint.h>

// ============================================================================
// OpenSSL provider initialization
// ============================================================================

__attribute__((constructor))
static void sbox_openssl_init(void)
{
    void* libcrypto = dlopen("libcrypto.so.3", RTLD_NOW | RTLD_GLOBAL | RTLD_NOLOAD);
    if (!libcrypto)
        libcrypto = dlopen("libcrypto.so.3", RTLD_NOW | RTLD_GLOBAL);
    if (!libcrypto)
        return;

    typedef int (*openssl_init_fn)(uint64_t opts, const void* settings);
    openssl_init_fn init_fn = (openssl_init_fn)dlsym(libcrypto, "OPENSSL_init_crypto");
    if (init_fn)
        init_fn(0x4 | 0x8 | 0x2 | 0x40, NULL); // ADD_ALL_CIPHERS|ADD_ALL_DIGESTS|LOAD_CRYPTO_STRINGS|LOAD_CONFIG

    typedef void* (*provider_load_fn)(void* ctx, const char* name);
    provider_load_fn load_fn = (provider_load_fn)dlsym(libcrypto, "OSSL_PROVIDER_load");
    if (load_fn) {
        load_fn(NULL, "default");
        load_fn(NULL, "legacy"); // optional
    }

    void* libssl = dlopen("libssl.so.3", RTLD_NOW | RTLD_GLOBAL | RTLD_NOLOAD);
    if (!libssl) libssl = dlopen("libssl.so.3", RTLD_NOW | RTLD_GLOBAL);
    if (libssl) {
        typedef int (*ssl_init_fn)(uint64_t opts, const void* settings);
        ssl_init_fn ssl_init = (ssl_init_fn)dlsym(libssl, "OPENSSL_init_ssl");
        if (ssl_init) ssl_init(0, NULL);
    }
}
