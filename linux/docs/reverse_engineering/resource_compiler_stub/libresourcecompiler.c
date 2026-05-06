// libresourcecompiler_stub_v3.c
// Compile: gcc -shared -fPIC -o libresourcecompiler.so libresourcecompiler_stub_v3.c

#include <stdio.h>
#include <string.h>
#include <stdint.h>

#define EXPORT __attribute__((visibility("default")))

#define IFACE_OK 0
#define IFACE_FAILED 1

// Stub implementations with correct Source 2 signatures
// Most Source 2 IAppSystem methods return bool/int

static void* stub_query_interface(void* this, const char* name) {
    fprintf(stderr, "[rc_stub] QueryInterface('%s')\n", name ? name : "NULL");
    if (name && strcmp(name, "ResourceCompilerSystem001") == 0) {
        return this;  // Return self
    }
    return NULL;
}

static int stub_connect(void* this, void* factory) {
    fprintf(stderr, "[rc_stub] Connect()\n");
    return 1;  // Success
}

static void stub_disconnect(void* this) {
    fprintf(stderr, "[rc_stub] Disconnect()\n");
}

static int stub_init(void* this) {
    fprintf(stderr, "[rc_stub] Init()\n");
    return 1;  // Success
}

static void stub_shutdown(void* this) {
    fprintf(stderr, "[rc_stub] Shutdown()\n");
}

static int stub_get_dependencies(void* this, void* list) {
    fprintf(stderr, "[rc_stub] GetDependencies()\n");
    return 0;
}

// These might write to buffer, or return a pointer
// Try both approaches - first as buffer writers
static int stub_get_interface_name(void* this, char* buf, int size) {
    fprintf(stderr, "[rc_stub] GetInterfaceName(buf=%p, size=%d)\n", buf, size);
    if (buf && size > 0) {
        strncpy(buf, "ResourceCompilerSystem", size);
        return 1;
    }
    return 0;
}

static int stub_get_interface_version(void* this, char* buf, int size) {
    fprintf(stderr, "[rc_stub] GetInterfaceVersion(buf=%p, size=%d)\n", buf, size);
    if (buf && size > 0) {
        strncpy(buf, "001", size);
        return 1;
    }
    return 0;
}

static int stub_reconnect(void* this, void* factory, const char* name) {
    fprintf(stderr, "[rc_stub] Reconnect('%s')\n", name ? name : "NULL");
    return 1;
}

static int stub_unknown(void* this) {
    // Generic unknown method
    return 1;
}

// IResourceCompilerSystem methods
static int stub_generate_resource_file(void* this, const char* path, void* data, int size) {
    fprintf(stderr, "[rc_stub] GenerateResourceFile('%s', %p, %d)\n", path, data, size);
    return 0;
}

static int stub_generate_resource_file_text(void* this, const char* path, const char* text) {
    fprintf(stderr, "[rc_stub] GenerateResourceFileText('%s')\n", path);
    return 0;
}

static void* stub_generate_resource_bytes(void* this, const char* path, void* data, int size) {
    fprintf(stderr, "[rc_stub] GenerateResourceBytes('%s')\n", path);
    return NULL;
}

// VTable with correct layout for Source 2 CTier3DmAppSystem
static void* vtable[] = {
    // [0-4] IAppSystem base
    (void*)stub_query_interface,
    (void*)stub_connect,
    (void*)stub_disconnect,
    (void*)stub_init,
    (void*)stub_shutdown,

    // [5-9] IAppSystem continued
    (void*)stub_get_dependencies,
    (void*)stub_get_interface_name,
    (void*)stub_get_interface_version,
    (void*)stub_reconnect,
    (void*)stub_unknown,  // DisconnectAborted or similar

    // [10-19] Tier1/Tier2/Tier3 AppSystem methods
    (void*)stub_unknown, (void*)stub_unknown, (void*)stub_unknown,
    (void*)stub_unknown, (void*)stub_unknown, (void*)stub_unknown,
    (void*)stub_unknown, (void*)stub_unknown, (void*)stub_unknown,
    (void*)stub_unknown,

    // [20+] IResourceCompilerSystem
    (void*)stub_generate_resource_file,
    (void*)stub_generate_resource_file_text,
    (void*)stub_generate_resource_bytes,
};

// Interface object
typedef struct {
    void** vtable;
} InterfaceInstance;

static InterfaceInstance resource_compiler_instance = {
    .vtable = vtable
};

// CreateInterface export
EXPORT
void* CreateInterface(const char* name, int* returnCode) {
    fprintf(stderr, "[rc_stub] CreateInterface('%s')\n", name ? name : "NULL");

    if (name && strcmp(name, "ResourceCompilerSystem001") == 0) {
        if (returnCode) *returnCode = IFACE_OK;
        return &resource_compiler_instance;
    }

    if (returnCode) *returnCode = IFACE_FAILED;
    return NULL;
}

// Other exports
EXPORT int BinaryProperties_GetValue(void* a, void* b) { return 0; }
EXPORT int GetResourceManifestCount(void) { return 0; }
EXPORT void GetResourceManifests(void* buf) {}
EXPORT void InstallSchemaBindings(void) {}

__attribute__((constructor))
static void stub_init_lib(void) {
    fprintf(stderr, "[rc_stub] Loaded (v3)\n");
}
