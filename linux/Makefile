# S&box Linux Interpose Libraries
# Build with: make
# Clean with: make clean

CXX = g++
CC = gcc
CXXFLAGS = -shared -fPIC -O2 -Wall -Wextra -std=c++17
CFLAGS = -shared -fPIC -O2 -Wall -Wextra
LDFLAGS = -ldl -lpthread

TARGETS = libsbox_init.so libdxcompiler_wrapper.so

all: $(TARGETS)

# Combined startup init — OpenSSL provider init + permissive free() + Wayland input injection
# Loaded as DT_NEEDED by game/sbox so it runs automatically without LD_PRELOAD.
# wayland_input.cpp synthesizes SDL_Events into libengine2's embedded SDL3 event queue
# because that SDL build silently produces zero events on Wayland sessions.
libsbox_init.so: sbox_init.cpp
	# $(CXX) $(CXXFLAGS) -o $@ sbox_init.cpp wayland_input.cpp $(LDFLAGS) -lwayland-client -lxkbcommon
	$(CXX) $(CXXFLAGS) -o $@ sbox_init.cpp
	@echo "Built $@"

libdxcompiler_wrapper.so: dxc_wrapper.c
	$(CC) $(CFLAGS) -o $@ dxc_wrapper.c
	@echo "Built $@"

clean:
	rm -f $(TARGETS)

# Usage help
help:
	@echo "OpenSSL interceptor activated"
	@echo ""

.PHONY: all clean help
