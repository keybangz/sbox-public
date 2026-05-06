# S&box Linux Interpose Libraries
# Build with: make
# Clean with: make clean

CXX = g++
CC = gcc
CXXFLAGS = -shared -fPIC -O2 -Wall -Wextra -std=c++17
CFLAGS = -shared -fPIC -O2 -Wall -Wextra
LDFLAGS = -ldl -lpthread

TARGETS = libsbox_interpose.so libaudio_interpose.so libvulkan_interpose.so \
          libframe_hook.so libmemory_interpose.so libtexture_interpose.so \
          libscene_interpose.so libdeepbind_interpose.so \
          libpermissive_free_interpose.so libopenssl_init_interpose.so \
          libsdl3_dynapi_interpose.so libsbox_init.so

all: $(TARGETS)

# Main interpose library - thread and library tracking
libsbox_interpose.so: sbox_interpose.cpp
	$(CXX) $(CXXFLAGS) -o $@ $< $(LDFLAGS)
	@echo "Built $@"

# Audio interpose - FFmpeg tracking
libaudio_interpose.so: audio_interpose.cpp
	$(CXX) $(CXXFLAGS) -o $@ $< $(LDFLAGS)
	@echo "Built $@"

# Vulkan interpose - SDL3 GPU tracking
libvulkan_interpose.so: vulkan_interpose.cpp
	$(CXX) $(CXXFLAGS) -o $@ $< $(LDFLAGS)
	@echo "Built $@"

# Frame hook - Delays SourceEngineFrame until swap chain ready
libframe_hook.so: frame_hook.cpp
	$(CXX) $(CXXFLAGS) -o $@ $< $(LDFLAGS)
	@echo "Built $@"

# Memory interpose - Heap corruption and double-free detection
libmemory_interpose.so: memory_interpose.cpp
	$(CXX) $(CXXFLAGS) -o $@ $< $(LDFLAGS) -rdynamic
	@echo "Built $@"

# Native texture interpose - Track texture create/destroy in native engine
libtexture_interpose.so: texture_interpose.cpp
	$(CXX) $(CXXFLAGS) -o $@ $< $(LDFLAGS)
	@echo "Built $@"

# Scene interpose - Track scene rendering and layer processing
libscene_interpose.so: scene_interpose.cpp
	$(CXX) $(CXXFLAGS) -o $@ $< $(LDFLAGS) -rdynamic
	@echo "Built $@"

# Deepbind interpose - Force RTLD_DEEPBIND on Valve engine libs to isolate OpenSSL symbols
libdeepbind_interpose.so: deepbind_interpose.cpp
	$(CXX) $(CXXFLAGS) -o $@ $< $(LDFLAGS)
	@echo "Built $@"

# Permissive free interpose - Validate free() pointers before calling glibc
libpermissive_free_interpose.so: permissive_free_interpose.cpp
	$(CXX) $(CXXFLAGS) -o $@ $< $(LDFLAGS)
	@echo "Built $@"

# OpenSSL init interpose - Force-init libcrypto before engine2 loads
libopenssl_init_interpose.so: openssl_init_interpose.cpp
	$(CXX) $(CXXFLAGS) -o $@ $< $(LDFLAGS)
	@echo "Built $@"

# SDL3 dynapi interpose - Redirect dlsym("SDL_Foo_REAL") -> dlsym("SDL_Foo")
# Fixes render system Init failure: rendersystemvulkan uses dlsym for 1,251 SDL_*_REAL symbols
libsdl3_dynapi_interpose.so: sdl3_dynapi_interpose.cpp
	$(CXX) $(CXXFLAGS) -o $@ $< $(LDFLAGS)
	@echo "Built $@"

# Combined startup init — OpenSSL provider init + permissive free() + Wayland input injection
# Loaded as DT_NEEDED by game/sbox so it runs automatically without LD_PRELOAD.
# wayland_input.cpp synthesizes SDL_Events into libengine2's embedded SDL3 event queue
# because that SDL build silently produces zero events on Wayland sessions.
libsbox_init.so: sbox_init.cpp wayland_input.cpp
	$(CXX) $(CXXFLAGS) -o $@ sbox_init.cpp wayland_input.cpp $(LDFLAGS) -lwayland-client -lxkbcommon
	@echo "Built $@"

clean:
	rm -f $(TARGETS)

# Usage help
help:
	@echo "S&box Linux Interpose Libraries"
	@echo ""
	@echo "Build: make"
	@echo ""
	@echo "Usage:"
	@echo "  SBOX_INTERPOSE=1 ./run.sh              - Thread/library tracking"
	@echo "  SBOX_AUDIO_DEBUG=1 ./run.sh            - FFmpeg tracking"
	@echo "  SBOX_VULKAN_DEBUG=1 ./run.sh           - Vulkan call tracking"
	@echo "  SBOX_FRAME_HOOK=1 ./run.sh             - Frame delay hook"
	@echo "  SBOX_MEMORY_DEBUG=1 ./run.sh           - Heap corruption detection"
	@echo "  SBOX_NATIVE_TEXTURE_DEBUG=1 ./run.sh   - Native texture tracking"
	@echo "  SBOX_TEXTURE_DEBUG=1 ./run.sh          - Managed texture logging"
	@echo "  SBOX_SCENE_DEBUG=1 ./run.sh            - Scene rendering tracking"
	@echo ""
	@echo "Logs in /tmp/sbox_*.log"

.PHONY: all clean help

