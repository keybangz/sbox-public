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
          libpermissive_free_interpose.so

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

