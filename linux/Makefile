# DXC Wrapper Makefile for s&box Linux
# Builds the UTF-16 to UTF-32 argument converter wrapper

CC = gcc
CFLAGS = -shared -fPIC -O2 -Wall -Wextra
LDFLAGS = -ldl -Wl,--no-as-needed

GAME_BIN = ../game/bin/linuxsteamrt64
GAME_DIR = ../game

all: dxc_wrapper

dxc_wrapper: $(GAME_BIN)/libdxcompiler_wrapper.so $(GAME_DIR)/libdxcompiler_wrapper.so

$(GAME_BIN)/libdxcompiler_wrapper.so: dxc_wrapper.c
	$(CC) $(CFLAGS) -o $@ $< $(LDFLAGS)
	@echo "Built $@"

$(GAME_DIR)/libdxcompiler_wrapper.so: dxc_wrapper.c
	$(CC) $(CFLAGS) -o $@ $< $(LDFLAGS)
	@echo "Built $@"

clean:
	rm -f $(GAME_BIN)/libdxcompiler_wrapper.so $(GAME_DIR)/libdxcompiler_wrapper.so

.PHONY: all dxc_wrapper clean
