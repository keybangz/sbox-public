# S&box Linux Interpose Libraries
# Build with: make
# Clean with: make clean

CXX = g++
CC = gcc
CXXFLAGS = -shared -fPIC -O2 -Wall -Wextra -std=c++17
CFLAGS = -shared -fPIC -O2 -Wall -Wextra
LDFLAGS = -ldl -lpthread

TARGETS = libsbox_interpose.so libaudio_interpose.so libvulkan_interpose.so libframe_hook.so

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

clean:
	rm -f $(TARGETS)

# Usage help
help:
	@echo "S&box Linux Interpose Libraries"
	@echo ""
	@echo "Build: make"
	@echo ""
	@echo "Usage:"
	@echo "  SBOX_INTERPOSE=1 ./run.sh       - Thread/library tracking"
	@echo "  SBOX_AUDIO_DEBUG=1 ./run.sh     - FFmpeg tracking"
	@echo "  SBOX_VULKAN_DEBUG=1 ./run.sh    - Vulkan call tracking"
	@echo "  SBOX_FRAME_HOOK=1 ./run.sh      - Frame delay hook"
	@echo ""
	@echo "Logs in /tmp/sbox_*.log"

.PHONY: all clean help

