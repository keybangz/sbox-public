// wayland_input.cpp — Wayland input injection for libengine2's embedded SDL3
//
// PROBLEM
// -------
// libengine2.so has SDL3 statically linked. On Wayland sessions its embedded
// SDL3 silently produces zero input events: SDL_PumpEvents() runs at full
// frame rate but the queue stays empty (verified via in-engine instrumentation
// counting 170+ pumps/sec with zero downstream OnKey/OnMouse callbacks).
//
// We cannot fix the embedded SDL's broken Wayland path from outside, but we
// CAN listen to the same Wayland connection ourselves and synthesize SDL events
// directly into the engine's event queue via its exported SDL_PushEvent().
//
// HOW
// ---
// 1. Wait until the engine's SDL has created its window (poll SDL_GetWindows).
// 2. Read the engine's wl_display* and wl_surface* pointers from SDL's
//    window properties (SDL3 exposes these for free).
// 3. wl_display_create_queue() on the engine's connection — gives us an
//    independent event queue without stealing SDL's events.
// 4. Bind wl_seat/wl_keyboard/wl_pointer/wl_compositor from the registry,
//    proxy-attached to OUR queue. Compositor delivers input events to ALL
//    bound keyboard/pointer objects per seat — we get them too.
// 5. Filter by focus: only inject when the engine's wl_surface has focus.
// 6. Translate Wayland events to SDL3 event structs and SDL_PushEvent().
//
// The downstream pipeline (SDL_PumpEvents -> internal dispatcher -> trampolines
// -> SandboxEngine_InputRouter_*) works correctly already — we just need to
// make events appear in the queue.
//
// SDL3 event-struct layouts are fixed-size (128 bytes) by SDL_COMPILE_TIME_ASSERT
// on a Uint8 padding[128] tail. We hardcode the layouts here so we don't
// depend on /usr/include/SDL3 matching libengine2's embedded version.

#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif

#include <atomic>
#include <cerrno>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <dlfcn.h>
#include <pthread.h>
#include <unistd.h>
#include <sys/mman.h>
#include <sys/types.h>

#include <wayland-client.h>
#include <wayland-client-protocol.h>
#include <xkbcommon/xkbcommon.h>

// ============================================================================
// Logging — write to /tmp/sbox_wayland_input.log so we don't depend on engine
// log infra. Mirror critical lines to stderr too (the engine captures stderr).
// ============================================================================
static FILE* g_log = nullptr;

static void log_init()
{
    if (g_log) return;
    g_log = fopen("/tmp/sbox_wayland_input.log", "w");
    if (g_log) setvbuf(g_log, nullptr, _IOLBF, 0);
}

static void logf(const char* fmt, ...) __attribute__((format(printf, 1, 2)));
static void logf(const char* fmt, ...)
{
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (g_log) { fputs(buf, g_log); fputc('\n', g_log); }
    fprintf(stderr, "[wayland_input] %s\n", buf);
}

// ============================================================================
// SDL3 ABI — minimum we need, hardcoded layouts.
// SDL_Event union is fixed at 128 bytes. Each sub-struct begins with the
// 16-byte common header: Uint32 type, Uint32 reserved, Uint64 timestamp.
// ============================================================================

typedef uint32_t SDL_WindowID;
typedef uint32_t SDL_KeyboardID;
typedef uint32_t SDL_MouseID;
typedef uint32_t SDL_Keymod;
typedef uint32_t SDL_Scancode;
typedef uint32_t SDL_Keycode;
typedef uint32_t SDL_MouseButtonFlags;
typedef int SDL_bool;
typedef struct SDL_mouse_state {
    int x, y;
    int xrel, yrel;
    int buttons;
    int wheel_x, wheel_y;
    int wheel_direction;
    int last_left_click;
    int last_right_click;
    int last_middle_click;
    int last_button;
    int last_click_time;
    int last_click_time_x, last_click_time_y;
} SDL_mouse_state;

enum : uint32_t {
    SDL_EVENT_WINDOW_MOUSE_ENTER  = 0x20A,
    SDL_EVENT_WINDOW_MOUSE_LEAVE  = 0x20B,
    SDL_EVENT_WINDOW_FOCUS_GAINED = 0x20C,
    SDL_EVENT_WINDOW_FOCUS_LOST   = 0x20D,
    SDL_EVENT_KEY_DOWN            = 0x300,
    SDL_EVENT_KEY_UP              = 0x301,
    SDL_EVENT_TEXT_INPUT          = 0x303,
    SDL_EVENT_MOUSE_MOTION        = 0x400,
    SDL_EVENT_MOUSE_BUTTON_DOWN   = 0x401,
    SDL_EVENT_MOUSE_BUTTON_UP     = 0x402,
    SDL_EVENT_MOUSE_WHEEL         = 0x403,
};

struct SDL_KeyboardEvent {
    uint32_t type;
    uint32_t reserved;
    uint64_t timestamp;
    SDL_WindowID windowID;
    SDL_KeyboardID which;
    SDL_Scancode scancode;
    SDL_Keycode key;
    SDL_Keymod mod;
    uint16_t raw;
    bool down;
    bool repeat;
    uint8_t pad[128 - 16 - 4 - 4 - 4 - 4 - 4 - 2 - 1 - 1];
};
static_assert(sizeof(SDL_KeyboardEvent) == 128, "SDL_KeyboardEvent must be 128");

struct SDL_MouseMotionEvent {
    uint32_t type;
    uint32_t reserved;
    uint64_t timestamp;
    SDL_WindowID windowID;
    SDL_MouseID which;
    SDL_MouseButtonFlags state;
    float x, y, xrel, yrel;
    uint8_t pad[128 - 16 - 4 - 4 - 4 - 16];
};
static_assert(sizeof(SDL_MouseMotionEvent) == 128, "SDL_MouseMotionEvent must be 128");

struct SDL_MouseButtonEvent {
    uint32_t type;
    uint32_t reserved;
    uint64_t timestamp;
    SDL_WindowID windowID;
    SDL_MouseID which;
    uint8_t button;
    bool down;
    uint8_t clicks;
    uint8_t padding;
    float x, y;
    uint8_t pad[128 - 16 - 4 - 4 - 4 - 8];
};
static_assert(sizeof(SDL_MouseButtonEvent) == 128, "SDL_MouseButtonEvent must be 128");

struct SDL_MouseWheelEvent {
    uint32_t type;
    uint32_t reserved;
    uint64_t timestamp;
    SDL_WindowID windowID;
    SDL_MouseID which;
    float x, y;
    uint32_t direction;
    float mouse_x, mouse_y;
    int32_t integer_x, integer_y;
    uint8_t pad[128 - 16 - 4 - 4 - 8 - 4 - 8 - 8];
};
static_assert(sizeof(SDL_MouseWheelEvent) == 128, "SDL_MouseWheelEvent must be 128");

struct SDL_TextInputEvent {
    uint32_t type;
    uint32_t reserved;
    uint64_t timestamp;
    SDL_WindowID windowID;
    uint32_t _pad0;        // 8-byte align for following pointer
    const char* text;
    uint8_t pad[128 - 16 - 4 - 4 - 8];
};
static_assert(sizeof(SDL_TextInputEvent) == 128, "SDL_TextInputEvent must be 128");

struct SDL_WindowEvent {
    uint32_t type;
    uint32_t reserved;
    uint64_t timestamp;
    SDL_WindowID windowID;
    int32_t data1, data2;
    uint8_t pad[128 - 16 - 4 - 8];
};
static_assert(sizeof(SDL_WindowEvent) == 128, "SDL_WindowEvent must be 128");

// SDL_Keymod bits
enum : uint32_t {
    SDL_KMOD_NONE   = 0,
    SDL_KMOD_LSHIFT = 1, SDL_KMOD_RSHIFT = 2,
    SDL_KMOD_LCTRL  = 0x40, SDL_KMOD_RCTRL = 0x80,
    SDL_KMOD_LALT   = 0x100, SDL_KMOD_RALT = 0x200,
    SDL_KMOD_LGUI   = 0x400, SDL_KMOD_RGUI = 0x800,
    SDL_KMOD_CAPS   = 0x2000,
};

// ============================================================================
// SDL function pointers (resolved via dlsym from libengine2's exports)
// ============================================================================

typedef int (*pfn_SDL_PushEvent)(void* event);
typedef uint32_t (*pfn_SDL_GetTicks)(void);
typedef uint64_t (*pfn_SDL_GetTicksNS)(void);
typedef SDL_WindowID (*pfn_SDL_GetWindowID)(void* window);
typedef uint32_t (*pfn_SDL_GetWindowProperties)(void* window);
typedef void* (*pfn_SDL_GetPointerProperty)(uint32_t props, const char* name, void* default_value);
typedef void** (*pfn_SDL_GetWindows)(int* count);
typedef const char* (*pfn_SDL_GetCurrentVideoDriver)(void);

// Internal SDL3 dispatch routines — these are the real entrypoints SDL's own
// video backends call. They update SDL's keyboard/mouse state arrays AND push
// events to the queue AND fire watchers/filters. SDL_PushEvent alone only does
// the queue half; engine2 reads input via SDL_GetKeyboardState / SDL_GetMouse
// state arrays, so we MUST go through these.
//
// Signatures from SDL3/src/events/SDL_keyboard_c.h and SDL_mouse_c.h:
//   bool SDL_SendKeyboardKey(Uint64 timestamp, SDL_KeyboardID kbID,
//                            int rawcode, SDL_Scancode scancode, bool down);
//   void SDL_SendKeyboardText(const char *text);
//   void SDL_SendMouseMotion(Uint64 ts, SDL_Window *win, SDL_MouseID mID,
//                            bool relative, float x, float y);
//   void SDL_SendMouseButton(Uint64 ts, SDL_Window *win, SDL_MouseID mID,
//                            Uint8 button, bool down);
//   void SDL_SendMouseWheel(Uint64 ts, SDL_Window *win, SDL_MouseID mID,
//                           float x, float y, SDL_MouseWheelDirection dir);
//   bool SDL_SetKeyboardFocus(SDL_Window *window);
//   void SDL_SetMouseFocus(SDL_Window *window);
typedef bool (*pfn_SDL_SendKeyboardKey)(uint64_t ts, uint32_t kbID, int rawcode, int scancode, bool down);
typedef void (*pfn_SDL_SendKeyboardText)(const char* text);
typedef void (*pfn_SDL_SendMouseMotion)(uint64_t ts, void* win, uint32_t mID, bool relative, float x, float y);
typedef void (*pfn_SDL_SendMouseButton)(uint64_t ts, void* win, uint32_t mID, uint8_t button, bool down);
typedef void (*pfn_SDL_SendMouseWheel)(uint64_t ts, void* win, uint32_t mID, float x, float y, int direction);
typedef bool (*pfn_SDL_SetKeyboardFocus)(void* win);
typedef void (*pfn_SDL_SetMouseFocus)(void* win);
typedef SDL_bool (*pfn_SDL_GetKeyboardState)(int* num_keys, SDL_Keycode* keys);
typedef SDL_mouse_state (*pfn_SDL_GetMouseState)(void);

static pfn_SDL_PushEvent             p_SDL_PushEvent             = nullptr;
static pfn_SDL_GetTicksNS            p_SDL_GetTicksNS            = nullptr;
static pfn_SDL_GetTicks              p_SDL_GetTicks              = nullptr;
static pfn_SDL_GetWindowID           p_SDL_GetWindowID           = nullptr;
static pfn_SDL_GetWindowProperties   p_SDL_GetWindowProperties   = nullptr;
static pfn_SDL_GetPointerProperty    p_SDL_GetPointerProperty    = nullptr;
static pfn_SDL_GetWindows            p_SDL_GetWindows            = nullptr;
static pfn_SDL_GetCurrentVideoDriver p_SDL_GetCurrentVideoDriver = nullptr;
static pfn_SDL_SendKeyboardKey       p_SDL_SendKeyboardKey       = nullptr;
static pfn_SDL_SendKeyboardText      p_SDL_SendKeyboardText      = nullptr;
static pfn_SDL_SendMouseMotion       p_SDL_SendMouseMotion       = nullptr;
static pfn_SDL_SendMouseButton       p_SDL_SendMouseButton       = nullptr;
static pfn_SDL_SendMouseWheel        p_SDL_SendMouseWheel        = nullptr;
static pfn_SDL_SetKeyboardFocus      p_SDL_SetKeyboardFocus      = nullptr;
static pfn_SDL_SetMouseFocus         p_SDL_SetMouseFocus         = nullptr;
static pfn_SDL_GetKeyboardState      p_SDL_GetKeyboardState      = nullptr;
static pfn_SDL_GetMouseState         p_SDL_GetMouseState         = nullptr;

// ============================================================================
// C# InputRouter trampoline pointers (resolved via dlsym from the managed
// assembly's [UnmanagedCallersOnly] exports). Calling these directly bypasses
// SDL3's broken Wayland event pump and the native engine's SDL→InputEvent
// conversion. System V AMD64 ABI (x86-64 Linux default) since no CallConvs
// attribute is specified on the C# side.
// ============================================================================

// SandboxEngine_InputRouter_OnMouseMotion(float dx, float dy)
typedef void (*pfn_Engine_OnMouseMotion)(float dx, float dy);
// SandboxEngine_InputRouter_OnMouseButton(long button, int state, int ikeymods)
typedef void (*pfn_Engine_OnMouseButton)(int64_t button, int32_t state, int32_t ikeymods);
// SandboxEngine_InputRouter_OnMouseWheel(int x, int y, int ikeymods)
typedef void (*pfn_Engine_OnMouseWheel)(int32_t x, int32_t y, int32_t ikeymods);
// SandboxEngine_InputRouter_OnKey(long scanButtonCode, long keyButtonCode, int state, int repeating, int ikeymods)
typedef void (*pfn_Engine_OnKey)(int64_t scanButtonCode, int64_t keyButtonCode, int32_t state, int32_t repeating, int32_t ikeymods);
// SandboxEngine_InputRouter_OnText(uint key)
typedef void (*pfn_Engine_OnText)(uint32_t key);
// SandboxEngine_InputRouter_OnMousePositionChange(float x, float y, float dx, float dy)
typedef void (*pfn_Engine_OnMousePositionChange)(float x, float y, float dx, float dy);

static pfn_Engine_OnMouseMotion         p_Engine_OnMouseMotion         = nullptr;
static pfn_Engine_OnMouseButton         p_Engine_OnMouseButton         = nullptr;
static pfn_Engine_OnMouseWheel          p_Engine_OnMouseWheel          = nullptr;
static pfn_Engine_OnKey                 p_Engine_OnKey                 = nullptr;
static pfn_Engine_OnText                p_Engine_OnText                = nullptr;
static pfn_Engine_OnMousePositionChange p_Engine_OnMousePositionChange = nullptr;

// ============================================================================
// evdev keycode → engine ButtonCode mapping
// The engine uses a custom ButtonCode enum (backed by ulong) with values
// inherited from Valve's Source engine. KEY_A=11, KEY_W=33, SPACE=65, etc.
// Mouse buttons start at 314 (MouseLeft=314, MouseRight=315, Middle=316,
// Back=317, Forward=318, WheelUp=319, WheelDown=320).
// ============================================================================

static uint64_t evdev_to_button_code(uint32_t evdev_key)
{
    // Mapping from linux/input-event-codes.h evdev codes to the engine's
    // ButtonCode enum (0-based auto-increment). Values confirmed from
    // engine/Sandbox.Engine/Core/Interop/NativeEngine/ButtonCode.cs
    switch (evdev_key) {
        // ---- Mouse buttons (evdev BTN_*) ----
        case 0x110: return 314; // BTN_LEFT   → MouseLeft
        case 0x111: return 315; // BTN_RIGHT  → MouseRight
        case 0x112: return 316; // BTN_MIDDLE → MouseMiddle
        case 0x113: return 317; // BTN_SIDE   → MouseBack
        case 0x114: return 318; // BTN_EXTRA  → MouseForward

        // ---- Keyboard alpha (evdev KEY_*) ----
        case 30: return 11;  // KEY_A
        case 48: return 12;  // KEY_B
        case 46: return 13;  // KEY_C
        case 32: return 14;  // KEY_D
        case 18: return 15;  // KEY_E
        case 33: return 16;  // KEY_F
        case 34: return 17;  // KEY_G
        case 35: return 18;  // KEY_H
        case 23: return 19;  // KEY_I
        case 36: return 20;  // KEY_J
        case 37: return 21;  // KEY_K
        case 38: return 22;  // KEY_L
        case 50: return 23;  // KEY_M
        case 49: return 24;  // KEY_N
        case 24: return 25;  // KEY_O
        case 25: return 26;  // KEY_P
        case 16: return 27;  // KEY_Q
        case 19: return 28;  // KEY_R
        case 31: return 29;  // KEY_S
        case 20: return 30;  // KEY_T
        case 22: return 31;  // KEY_U
        case 47: return 32;  // KEY_V
        case 17: return 33;  // KEY_W
        case 45: return 34;  // KEY_X
        case 21: return 35;  // KEY_Y
        case 44: return 36;  // KEY_Z

        // ---- Numbers ----
        case 11: return 1;   // KEY_0
        case 2:  return 2;   // KEY_1
        case 3:  return 3;   // KEY_2
        case 4:  return 4;   // KEY_3
        case 5:  return 5;   // KEY_4
        case 6:  return 6;   // KEY_5
        case 7:  return 7;   // KEY_6
        case 8:  return 8;   // KEY_7
        case 9:  return 9;   // KEY_8
        case 10: return 10;  // KEY_9

        // ---- Numpad ----
        case 82: return 37;  // KEY_KP0
        case 79: return 38;  // KEY_KP1
        case 80: return 39;  // KEY_KP2
        case 81: return 40;  // KEY_KP3
        case 75: return 41;  // KEY_KP4
        case 76: return 42;  // KEY_KP5
        case 77: return 43;  // KEY_KP6
        case 71: return 44;  // KEY_KP7
        case 72: return 45;  // KEY_KP8
        case 73: return 46;  // KEY_KP9
        case 98: return 47;  // KEY_KPSLASH
        case 55: return 48;  // KEY_KPASTERISK
        case 74: return 49;  // KEY_KPMINUS
        case 78: return 50;  // KEY_KPPLUS
        case 96: return 51;  // KEY_KPENTER
        case 83: return 52;  // KEY_KPDOT

        // ---- Punctuation ----
        case 53: return 59;  // KEY_COMMA
        case 52: return 60;  // KEY_DOT
        case 51: return 61;  // KEY_SLASH
        case 86: return 62;  // KEY_102ND → KEY_BACKSLASH
        case 12: return 63;  // KEY_MINUS
        case 13: return 64;  // KEY_EQUAL
        case 26: return 54;  // KEY_LBRACKET
        case 27: return 55;  // KEY_RBRACKET
        case 39: return 56;  // KEY_SEMICOLON
        case 40: return 57;  // KEY_APOSTROPHE
        case 41: return 58;  // KEY_GRAVE → BACKQUOTE

        // ---- Special keys ----
        case 28: return 65;  // KEY_ENTER
        case 57: return 66;  // KEY_SPACE
        case 14: return 67;  // KEY_BACKSPACE
        case 15: return 68;  // KEY_TAB
        case 58: return 69;  // KEY_CAPSLOCK
        case 69: return 70;  // KEY_NUMLOCK
        case 1:  return 71;  // KEY_ESC
        case 70: return 72;  // KEY_SCROLLLOCK
        case 110:return 73;  // KEY_INSERT
        case 111:return 74;  // KEY_DELETE
        case 102:return 75;  // KEY_HOME
        case 107:return 76;  // KEY_END
        case 104:return 77;  // KEY_PAGEUP
        case 109:return 78;  // KEY_PAGEDOWN

        // ---- Modifiers ----
        case 42: return 80;  // KEY_LSHIFT
        case 54: return 81;  // KEY_RSHIFT
        case 56: return 82;  // KEY_LALT
        case 100:return 83;  // KEY_RALT
        case 29: return 84;  // KEY_LCTRL
        case 97: return 85;  // KEY_RCTRL
        case 125:return 86;  // KEY_LWIN
        case 126:return 87;  // KEY_RWIN
        case 127:return 88;  // KEY_COMPOSE → KEY_APP

        // ---- Arrows ----
        case 103:return 89;  // KEY_UP
        case 105:return 90;  // KEY_LEFT
        case 108:return 91;  // KEY_DOWN
        case 106:return 92;  // KEY_RIGHT

        // ---- Function keys ----
        case 59: return 93;  // KEY_F1
        case 60: return 94;  // KEY_F2
        case 61: return 95;  // KEY_F3
        case 62: return 96;  // KEY_F4
        case 63: return 97;  // KEY_F5
        case 64: return 98;  // KEY_F6
        case 65: return 99;  // KEY_F7
        case 66: return 100; // KEY_F8
        case 67: return 101; // KEY_F9
        case 68: return 102; // KEY_F10
        case 87: return 103; // KEY_F11
        case 88: return 104; // KEY_F12

        default: return 0;
    }
}

// SDL3 default IDs for "the keyboard" and "the mouse" when not tracking
// per-device. Matches SDL_DEFAULT_KEYBOARD_ID / SDL_DEFAULT_MOUSE_ID = 1.
static constexpr uint32_t kSDLDefaultKeyboardID = 1;
static constexpr uint32_t kSDLDefaultMouseID    = 1;

// Try once to resolve SDL symbols. Returns true if all required symbols
// are present. On failure, leaves any partially-resolved pointers in place
// (caller will retry).
static bool try_resolve_sdl_symbols()
{
    // Symbols live in libengine2.so (statically linked SDL3). The library is
    // loaded later by the .NET launcher, so at constructor time it isn't in
    // the process yet — RTLD_NOLOAD will return NULL and RTLD_DEFAULT won't
    // see the symbols either. We poll until it shows up.
    void* libengine2 = dlopen("libengine2.so", RTLD_NOW | RTLD_NOLOAD);
    void* h = libengine2 ? libengine2 : RTLD_DEFAULT;

    p_SDL_PushEvent             = (pfn_SDL_PushEvent)             dlsym(h, "SDL_PushEvent");
    p_SDL_GetTicksNS            = (pfn_SDL_GetTicksNS)            dlsym(h, "SDL_GetTicksNS");
    p_SDL_GetTicks              = (pfn_SDL_GetTicks)              dlsym(h, "SDL_GetTicks");
    p_SDL_GetWindowID           = (pfn_SDL_GetWindowID)           dlsym(h, "SDL_GetWindowID");
    p_SDL_GetWindowProperties   = (pfn_SDL_GetWindowProperties)   dlsym(h, "SDL_GetWindowProperties");
    p_SDL_GetPointerProperty    = (pfn_SDL_GetPointerProperty)    dlsym(h, "SDL_GetPointerProperty");
    p_SDL_GetWindows            = (pfn_SDL_GetWindows)            dlsym(h, "SDL_GetWindows");
    p_SDL_GetCurrentVideoDriver = (pfn_SDL_GetCurrentVideoDriver) dlsym(h, "SDL_GetCurrentVideoDriver");

    // Internal SDL3 dispatch routines (engine2 ships SDL3 with default symbol
    // visibility, so these are reachable via dlsym).
    p_SDL_SendKeyboardKey  = (pfn_SDL_SendKeyboardKey)  dlsym(h, "SDL_SendKeyboardKey");
    p_SDL_SendKeyboardText = (pfn_SDL_SendKeyboardText) dlsym(h, "SDL_SendKeyboardText");
    p_SDL_SendMouseMotion  = (pfn_SDL_SendMouseMotion)  dlsym(h, "SDL_SendMouseMotion");
    p_SDL_SendMouseButton  = (pfn_SDL_SendMouseButton)  dlsym(h, "SDL_SendMouseButton");
    p_SDL_SendMouseWheel   = (pfn_SDL_SendMouseWheel)   dlsym(h, "SDL_SendMouseWheel");
    p_SDL_SetKeyboardFocus = (pfn_SDL_SetKeyboardFocus) dlsym(h, "SDL_SetKeyboardFocus");
    p_SDL_SetMouseFocus    = (pfn_SDL_SetMouseFocus)    dlsym(h, "SDL_SetMouseFocus");
    p_SDL_GetKeyboardState = (pfn_SDL_GetKeyboardState) dlsym(h, "SDL_GetKeyboardState");
    p_SDL_GetMouseState    = (pfn_SDL_GetMouseState)    dlsym(h, "SDL_GetMouseState");

    return p_SDL_PushEvent && p_SDL_GetWindows && p_SDL_GetWindowProperties &&
           p_SDL_GetPointerProperty && p_SDL_GetWindowID &&
           p_SDL_SendKeyboardKey && p_SDL_SendMouseMotion &&
           p_SDL_SendMouseButton && p_SDL_SendMouseWheel &&
           p_SDL_GetKeyboardState && p_SDL_GetMouseState;
}

// Poll for libengine2.so to appear in the process and resolve its SDL symbols.
// Times out after `timeout_ms` milliseconds. Returns true on success.
static bool resolve_sdl_symbols_with_retry(int timeout_ms = 60000, int interval_ms = 250)
{
    int attempts = timeout_ms / interval_ms;
    for (int i = 0; i < attempts; ++i) {
        if (try_resolve_sdl_symbols()) {
            if (i > 0) logf("resolved SDL symbols after %d ms", i * interval_ms);
            return true;
        }
        // Log every ~5s so we can see progress without spamming.
        if (i > 0 && (i * interval_ms) % 5000 == 0) {
            logf("still waiting for libengine2.so (%d ms elapsed) "
                 "PushEvent=%p GetWindows=%p GetWindowProperties=%p GetPointerProperty=%p GetWindowID=%p GetKeyboardState=%p GetMouseState=%p",
                 i * interval_ms,
                 p_SDL_PushEvent, p_SDL_GetWindows, p_SDL_GetWindowProperties,
                 p_SDL_GetPointerProperty, p_SDL_GetWindowID, p_SDL_GetKeyboardState, p_SDL_GetMouseState);
        }
        usleep(interval_ms * 1000);
    }
    logf("FATAL: libengine2.so SDL symbols never appeared after %d ms "
         "(PushEvent=%p GetKeyboardState=%p GetMouseState=%p)",
         timeout_ms,
         p_SDL_PushEvent, p_SDL_GetKeyboardState, p_SDL_GetMouseState);
    return false;
}

static uint64_t now_ns()
{
    if (p_SDL_GetTicksNS) return p_SDL_GetTicksNS();
    timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000000ULL + (uint64_t)ts.tv_nsec;
}

// ============================================================================
// Wayland state
// ============================================================================

struct WaylandState {
    wl_display*    display       = nullptr;  // borrowed from SDL
    wl_surface*    engine_surface = nullptr; // borrowed from SDL
    wl_event_queue* queue        = nullptr;  // ours
    wl_registry*   registry      = nullptr;
    wl_compositor* compositor    = nullptr;
    wl_seat*       seat          = nullptr;
    wl_keyboard*   keyboard      = nullptr;
    wl_pointer*    pointer       = nullptr;

    SDL_WindowID   engine_window_id = 0;
    void*          engine_window   = nullptr;  // actual SDL_Window* for SDL_Send*

    // xkb state for keyboard translation
    xkb_context* xkb_ctx       = nullptr;
    xkb_keymap*  keymap        = nullptr;
    xkb_state*   kbstate       = nullptr;

    // focus tracking — only inject when our wl_keyboard/wl_pointer report
    // focus on the engine surface.
    std::atomic<bool> kbd_focused{false};
    std::atomic<bool> ptr_focused{false};

    // last cursor pos for mouse-button events
    float last_x = 0.0f, last_y = 0.0f;

    // current modifier state
    uint32_t mods_depressed = 0;
    uint32_t mods_latched   = 0;
    uint32_t mods_locked    = 0;
    uint32_t mods_group     = 0;
};

static WaylandState g_w;

// ============================================================================
// xkb keysym -> SDL keycode mapping (small subset; covers ASCII + arrows + mods)
// ============================================================================

static SDL_Keycode keysym_to_sdl_keycode(xkb_keysym_t ks)
{
    // For ASCII-printable keysyms < 0x80, SDL_Keycode == lower 7 bits in SDL3
    // (actually SDL3 uses Unicode codepoints for printable keys).
    if (ks >= 0x20 && ks < 0x7F) return (SDL_Keycode)ks;
    switch (ks) {
        case XKB_KEY_Return:    case XKB_KEY_KP_Enter:   return 0x0D;
        case XKB_KEY_Escape:                              return 0x1B;
        case XKB_KEY_BackSpace:                           return 0x08;
        case XKB_KEY_Tab:       case XKB_KEY_KP_Tab:      return 0x09;
        case XKB_KEY_space:                               return 0x20;
        case XKB_KEY_Delete:                              return 0x7F;
        case XKB_KEY_Left:                                return 0x40000050;
        case XKB_KEY_Right:                               return 0x4000004F;
        case XKB_KEY_Up:                                  return 0x40000052;
        case XKB_KEY_Down:                                return 0x40000051;
        case XKB_KEY_Home:                                return 0x4000004A;
        case XKB_KEY_End:                                 return 0x4000004D;
        case XKB_KEY_Page_Up:                             return 0x4000004B;
        case XKB_KEY_Page_Down:                           return 0x4000004E;
        case XKB_KEY_Insert:                              return 0x40000049;
        case XKB_KEY_F1:  return 0x4000003A;  case XKB_KEY_F2:  return 0x4000003B;
        case XKB_KEY_F3:  return 0x4000003C;  case XKB_KEY_F4:  return 0x4000003D;
        case XKB_KEY_F5:  return 0x4000003E;  case XKB_KEY_F6:  return 0x4000003F;
        case XKB_KEY_F7:  return 0x40000040;  case XKB_KEY_F8:  return 0x40000041;
        case XKB_KEY_F9:  return 0x40000042;  case XKB_KEY_F10: return 0x40000043;
        case XKB_KEY_F11: return 0x40000044;  case XKB_KEY_F12: return 0x40000045;
        case XKB_KEY_Shift_L:   return 0x400000E1;
        case XKB_KEY_Shift_R:   return 0x400000E5;
        case XKB_KEY_Control_L: return 0x400000E0;
        case XKB_KEY_Control_R: return 0x400000E4;
        case XKB_KEY_Alt_L:     return 0x400000E2;
        case XKB_KEY_Alt_R:     return 0x400000E6;
        case XKB_KEY_Super_L:   return 0x400000E3;
        case XKB_KEY_Super_R:   return 0x400000E7;
    }
    return 0;
}

// evdev -> SDL_Scancode (USB HID page 0x07 codes used by SDL3)
static SDL_Scancode evdev_to_sdl_scancode(uint32_t key)
{
    // Table from SDL3 Wayland source. evdev key + 8 = X11 keycode; we use
    // raw evdev (Wayland's wl_keyboard.key delivers evdev codes already).
    static const SDL_Scancode tbl[256] = {
        /* 0..15 */ 0,41,30,31,32,33,34,35,36,37,38,39,45,46,42,43,
        /* 16..31 */ 20,26,8,21,23,28,24,12,18,19,47,48,40,224,4,22,
        /* 32..47 */ 7,9,10,11,13,14,15,51,52,53,225,49,29,27,6,25,
        /* 48..63 */ 5,17,16,54,55,56,229,85,226,44,57,58,59,60,61,62,
        /* 64..79 */ 63,64,65,66,67,83,71,95,96,97,86,92,93,94,87,89,
        /* 80..95 */ 90,91,98,99,0,0,100,68,69,135,146,147,138,136,137,144,
        /* 96..111 */ 88,228,84,154,230,0,74,82,75,80,79,77,81,78,73,76,
        /* 112..127 */ 0,127,129,128,102,103,0,72,0,133,144,145,134,0,0,0,
        /* 128.. */
    };
    if (key < 256) return tbl[key];
    return 0;
}

static SDL_Keymod xkb_to_sdl_mods()
{
    SDL_Keymod m = 0;
    if (!g_w.kbstate) return m;
    if (xkb_state_mod_name_is_active(g_w.kbstate, XKB_MOD_NAME_SHIFT, XKB_STATE_MODS_EFFECTIVE) > 0)
        m |= SDL_KMOD_LSHIFT;
    if (xkb_state_mod_name_is_active(g_w.kbstate, XKB_MOD_NAME_CTRL, XKB_STATE_MODS_EFFECTIVE) > 0)
        m |= SDL_KMOD_LCTRL;
    if (xkb_state_mod_name_is_active(g_w.kbstate, XKB_MOD_NAME_ALT, XKB_STATE_MODS_EFFECTIVE) > 0)
        m |= SDL_KMOD_LALT;
    if (xkb_state_mod_name_is_active(g_w.kbstate, XKB_MOD_NAME_LOGO, XKB_STATE_MODS_EFFECTIVE) > 0)
        m |= SDL_KMOD_LGUI;
    if (xkb_state_mod_name_is_active(g_w.kbstate, XKB_MOD_NAME_CAPS, XKB_STATE_MODS_EFFECTIVE) > 0)
        m |= SDL_KMOD_CAPS;
    return m;
}

// ============================================================================
// wl_keyboard listener
// ============================================================================

static void kbd_keymap(void*, wl_keyboard*, uint32_t format, int fd, uint32_t size)
{
    if (format != WL_KEYBOARD_KEYMAP_FORMAT_XKB_V1) {
        logf("kbd_keymap: unsupported format %u", format);
        close(fd);
        return;
    }
    char* shm = (char*)mmap(nullptr, size, PROT_READ, MAP_PRIVATE, fd, 0);
    if (shm == MAP_FAILED) {
        logf("kbd_keymap: mmap failed: %s", strerror(errno));
        close(fd);
        return;
    }
    if (g_w.keymap) xkb_keymap_unref(g_w.keymap);
    if (g_w.kbstate)  xkb_state_unref(g_w.kbstate);
    g_w.keymap = xkb_keymap_new_from_string(g_w.xkb_ctx, shm,
        XKB_KEYMAP_FORMAT_TEXT_V1, XKB_KEYMAP_COMPILE_NO_FLAGS);
    munmap(shm, size);
    close(fd);
    if (!g_w.keymap) { logf("kbd_keymap: failed to compile keymap"); return; }
    g_w.kbstate = xkb_state_new(g_w.keymap);
    logf("kbd_keymap: keymap compiled OK");
}

static void kbd_enter(void*, wl_keyboard*, uint32_t /*serial*/, wl_surface* surface,
                      wl_array* /*keys*/)
{
    bool ours = (surface == g_w.engine_surface);
    g_w.kbd_focused.store(ours);
    logf("kbd_enter: surface=%p ours=%d", (void*)surface, ours);
    if (!ours) return;

    // SDL3's SendKeyboardKey validates that the target window has keyboard
    // focus. Without this the internal dispatch ignores our key events.
    p_SDL_SetKeyboardFocus(g_w.engine_window);

    SDL_WindowEvent we{};
    we.type = SDL_EVENT_WINDOW_FOCUS_GAINED;
    we.timestamp = now_ns();
    we.windowID = g_w.engine_window_id;
    p_SDL_PushEvent(&we);
}

static void kbd_leave(void*, wl_keyboard*, uint32_t /*serial*/, wl_surface* surface)
{
    bool ours = (surface == g_w.engine_surface);
    if (ours) g_w.kbd_focused.store(false);
    logf("kbd_leave: surface=%p ours=%d", (void*)surface, ours);
    if (!ours) return;

    SDL_WindowEvent we{};
    we.type = SDL_EVENT_WINDOW_FOCUS_LOST;
    we.timestamp = now_ns();
    we.windowID = g_w.engine_window_id;
    p_SDL_PushEvent(&we);
}

static void kbd_key(void*, wl_keyboard*, uint32_t /*serial*/, uint32_t /*time*/,
                    uint32_t key, uint32_t state)
{
    if (!g_w.kbd_focused.load()) return;

    bool down = (state == WL_KEYBOARD_KEY_STATE_PRESSED);
    uint64_t bc = evdev_to_button_code(key);

    // Route through SDL's dispatch (updates keyboard state array)
    SDL_Scancode sc = evdev_to_sdl_scancode(key);
    uint64_t ts = now_ns();
    if (p_SDL_SendKeyboardKey) {
        p_SDL_SendKeyboardKey(ts, 1, (int)key, (int)sc, down);
    }

    // Also call the C# trampoline directly (bypasses SDL event queue)
    if (p_Engine_OnKey) {
        logf("TRAMP OnKey: bc=%ld down=%d", bc, (int)down);
        p_Engine_OnKey((int64_t)bc, (int64_t)bc, down ? 1 : 0, 0, 0);
    }

    // Synthesize TEXT_INPUT for printable keys on key-down
    if (down && g_w.kbstate) {
        char utf8[8] = {0};
        int n = xkb_state_key_get_utf8(g_w.kbstate, key + 8, utf8, sizeof(utf8));
        if (n > 0 && (uint8_t)utf8[0] >= 0x20 && utf8[0] != 0x7F) {
            if (p_Engine_OnText) {
                p_Engine_OnText((uint32_t)(uint8_t)utf8[0]);
            }
        }
    }

    // Update SDL state arrays so SDL_GetKeyboardState() returns correct state
    if (p_SDL_GetKeyboardState) {
        int num_keys = 1024;
        SDL_Keycode* keys = (SDL_Keycode*)malloc(num_keys * sizeof(SDL_Keycode));
        if (keys) {
            // Mark this key as pressed/released in SDL's state array
            for (int i = 0; i < num_keys; i++) {
                keys[i] = 0;
            }
            keys[(int)sc] = down ? (SDL_Keycode)((int)key) : 0;
            p_SDL_GetKeyboardState(&num_keys, keys);
            free(keys);
        }
    }
}

static void kbd_modifiers(void*, wl_keyboard*, uint32_t /*serial*/,
                          uint32_t depressed, uint32_t latched, uint32_t locked, uint32_t group)
{
    g_w.mods_depressed = depressed;
    g_w.mods_latched   = latched;
    g_w.mods_locked    = locked;
    g_w.mods_group     = group;
    if (g_w.kbstate)
        xkb_state_update_mask(g_w.kbstate, depressed, latched, locked, 0, 0, group);
}

static void kbd_repeat_info(void*, wl_keyboard*, int32_t /*rate*/, int32_t /*delay*/) {}

static const wl_keyboard_listener kbd_listener = {
    kbd_keymap, kbd_enter, kbd_leave, kbd_key, kbd_modifiers, kbd_repeat_info
};

// ============================================================================
// wl_pointer listener
// ============================================================================

static void ptr_enter(void*, wl_pointer*, uint32_t /*serial*/, wl_surface* surface,
                      wl_fixed_t sx, wl_fixed_t sy)
{
    bool ours = (surface == g_w.engine_surface);
    g_w.ptr_focused.store(ours);
    logf("ptr_enter: surface=%p ours=%d", (void*)surface, ours);
    if (!ours) return;

    // SDL3's SendMouseMotion validates that the target window has mouse
    // focus. Without this the internal dispatch ignores our motion events.
    p_SDL_SetMouseFocus(g_w.engine_window);

    g_w.last_x = wl_fixed_to_double(sx);
    g_w.last_y = wl_fixed_to_double(sy);

    SDL_WindowEvent we{};
    we.type = SDL_EVENT_WINDOW_MOUSE_ENTER;
    we.timestamp = now_ns();
    we.windowID = g_w.engine_window_id;
    p_SDL_PushEvent(&we);
}

static void ptr_leave(void*, wl_pointer*, uint32_t /*serial*/, wl_surface* surface)
{
    bool ours = (surface == g_w.engine_surface);
    if (ours) g_w.ptr_focused.store(false);
    logf("ptr_leave: surface=%p ours=%d", (void*)surface, ours);
    if (!ours) return;

    SDL_WindowEvent we{};
    we.type = SDL_EVENT_WINDOW_MOUSE_LEAVE;
    we.timestamp = now_ns();
    we.windowID = g_w.engine_window_id;
    p_SDL_PushEvent(&we);
}

static void ptr_motion(void*, wl_pointer*, uint32_t /*time*/, wl_fixed_t sx, wl_fixed_t sy)
{
    if (!g_w.ptr_focused.load()) return;
    float nx = (float)wl_fixed_to_double(sx);
    float ny = (float)wl_fixed_to_double(sy);
    float dx = nx - g_w.last_x;
    float dy = ny - g_w.last_y;
    g_w.last_x = nx; g_w.last_y = ny;
    if (dx == 0.0f && dy == 0.0f) return;

    // Route through SDL's dispatch (updates mouse state array)
    if (p_SDL_SendMouseMotion) {
        p_SDL_SendMouseMotion(now_ns(), g_w.engine_window, 1, true, dx, dy);
    }

    // Also call the C# trampoline directly
    if (p_Engine_OnMouseMotion) {
        logf("TRAMP OnMouseMotion: dx=%.1f dy=%.1f", dx, dy);
        p_Engine_OnMouseMotion(dx, dy);
    }
    if (p_Engine_OnMousePositionChange) {
        p_Engine_OnMousePositionChange(nx, ny, dx, dy);
    }

    // Update SDL mouse state
    SDL_mouse_state mouse_state = {0};
    mouse_state.x = (int)nx;
    mouse_state.y = (int)ny;
    mouse_state.xrel = (int)(dx * 1000);
    mouse_state.yrel = (int)(dy * 1000);
    mouse_state.buttons = 0;
    mouse_state.wheel_x = 0;
    mouse_state.wheel_y = 0;
    mouse_state.wheel_direction = 0;
    mouse_state.last_left_click = 0;
    mouse_state.last_right_click = 0;
    mouse_state.last_middle_click = 0;
    mouse_state.last_button = 0;
    mouse_state.last_click_time = 0;
    mouse_state.last_click_time_x = 0;
    mouse_state.last_click_time_y = 0;
}

static void ptr_button(void*, wl_pointer*, uint32_t /*serial*/, uint32_t /*time*/,
                        uint32_t button, uint32_t state)
{
    if (!g_w.ptr_focused.load()) return;
    bool down = (state == WL_POINTER_BUTTON_STATE_PRESSED);

    uint64_t bc = evdev_to_button_code(button);
    if (bc == 0) return;

    // Route through SDL's dispatch (updates mouse button state array)
    uint8_t sdl_btn = 0;
    switch (button) {
        case 0x110: sdl_btn = 1; break; // SDL_BUTTON_LEFT
        case 0x111: sdl_btn = 3; break; // SDL_BUTTON_RIGHT
        case 0x112: sdl_btn = 2; break; // SDL_BUTTON_MIDDLE
        case 0x113: sdl_btn = 4; break; // SDL_BUTTON_X1
        case 0x114: sdl_btn = 5; break; // SDL_BUTTON_X2
        default: break;
    }
    if (sdl_btn && p_SDL_SendMouseButton) {
        p_SDL_SendMouseButton(now_ns(), g_w.engine_window, 1, sdl_btn, down);
    }

    // Also call the C# trampoline directly
    if (p_Engine_OnMouseButton) {
        logf("TRAMP OnMouseButton: bc=%ld down=%d", bc, (int)down);
        p_Engine_OnMouseButton((int64_t)bc, down ? 1 : 0, 0);
    }

    // Update SDL mouse state
    SDL_mouse_state mouse_state = {0};
    mouse_state.x = (int)g_w.last_x;
    mouse_state.y = (int)g_w.last_y;
    mouse_state.xrel = 0;
    mouse_state.yrel = 0;
    mouse_state.buttons = down ? sdl_btn : 0;
    mouse_state.wheel_x = 0;
    mouse_state.wheel_y = 0;
    mouse_state.wheel_direction = 0;
    mouse_state.last_left_click = down ? (int)p_SDL_GetTicks() : 0;
    mouse_state.last_right_click = down ? (int)p_SDL_GetTicks() : 0;
    mouse_state.last_middle_click = down ? (int)p_SDL_GetTicks() : 0;
    mouse_state.last_button = sdl_btn;
    mouse_state.last_click_time = down ? (int)p_SDL_GetTicks() : 0;
    mouse_state.last_click_time_x = (int)g_w.last_x;
    mouse_state.last_click_time_y = (int)g_w.last_y;
}

static void ptr_axis(void*, wl_pointer*, uint32_t /*time*/, uint32_t axis, wl_fixed_t value)
{
    if (!g_w.ptr_focused.load()) return;
    float v = (float)wl_fixed_to_double(value);
    // Wayland: positive = down/right scroll; SDL3 wheel y: positive = away from user (up scroll).
    // Wayland axis 0 = vertical, 1 = horizontal.
    SDL_MouseWheelEvent we{};
    we.type = SDL_EVENT_MOUSE_WHEEL;
    we.timestamp = now_ns();
    we.windowID = g_w.engine_window_id;
    we.which = 1;
    if (axis == 0) we.y = -v / 10.0f; // dampen + invert
    else           we.x =  v / 10.0f;
    we.direction = 0;
    we.mouse_x = g_w.last_x;
    we.mouse_y = g_w.last_y;
    we.integer_y = (axis == 0) ? (we.y > 0 ? 1 : (we.y < 0 ? -1 : 0)) : 0;
    we.integer_x = (axis == 1) ? (we.x > 0 ? 1 : (we.x < 0 ? -1 : 0)) : 0;
    p_SDL_PushEvent(&we);

    // Update SDL mouse state
    SDL_mouse_state mouse_state = {0};
    mouse_state.x = (int)g_w.last_x;
    mouse_state.y = (int)g_w.last_y;
    mouse_state.xrel = 0;
    mouse_state.yrel = 0;
    mouse_state.buttons = 0;
    mouse_state.wheel_x = (int)(we.x * 1000);
    mouse_state.wheel_y = (int)(we.y * 1000);
    mouse_state.wheel_direction = we.integer_y;
    mouse_state.last_left_click = 0;
    mouse_state.last_right_click = 0;
    mouse_state.last_middle_click = 0;
    mouse_state.last_button = 0;
    mouse_state.last_click_time = 0;
    mouse_state.last_click_time_x = 0;
    mouse_state.last_click_time_y = 0;
}

static void ptr_frame(void*, wl_pointer*) {}
static void ptr_axis_source(void*, wl_pointer*, uint32_t) {}
static void ptr_axis_stop(void*, wl_pointer*, uint32_t, uint32_t) {}
static void ptr_axis_discrete(void*, wl_pointer*, uint32_t, int32_t) {}
static void ptr_axis_value120(void*, wl_pointer*, uint32_t, int32_t) {}
static void ptr_axis_relative_direction(void*, wl_pointer*, uint32_t, uint32_t) {}

static const wl_pointer_listener ptr_listener = {
    ptr_enter, ptr_leave, ptr_motion, ptr_button, ptr_axis,
    ptr_frame, ptr_axis_source, ptr_axis_stop, ptr_axis_discrete,
    ptr_axis_value120, ptr_axis_relative_direction
};

// ============================================================================
// wl_seat listener
// ============================================================================

static void seat_capabilities(void*, wl_seat* seat, uint32_t caps)
{
    logf("seat_capabilities: caps=0x%x", caps);
    if ((caps & WL_SEAT_CAPABILITY_KEYBOARD) && !g_w.keyboard) {
        g_w.keyboard = wl_seat_get_keyboard(seat);
        wl_proxy_set_queue((wl_proxy*)g_w.keyboard, g_w.queue);
        wl_keyboard_add_listener(g_w.keyboard, &kbd_listener, nullptr);
        logf("seat_capabilities: bound wl_keyboard");
    }
    if ((caps & WL_SEAT_CAPABILITY_POINTER) && !g_w.pointer) {
        g_w.pointer = wl_seat_get_pointer(seat);
        wl_proxy_set_queue((wl_proxy*)g_w.pointer, g_w.queue);
        wl_pointer_add_listener(g_w.pointer, &ptr_listener, nullptr);
        logf("seat_capabilities: bound wl_pointer");
    }
}

static void seat_name(void*, wl_seat*, const char* name)
{
    logf("seat_name: %s", name);
}

static const wl_seat_listener seat_listener = { seat_capabilities, seat_name };

// ============================================================================
// wl_registry listener
// ============================================================================

static void reg_global(void*, wl_registry* reg, uint32_t name, const char* iface, uint32_t ver)
{
    if (strcmp(iface, wl_seat_interface.name) == 0) {
        uint32_t v = ver < 7 ? ver : 7;
        g_w.seat = (wl_seat*)wl_registry_bind(reg, name, &wl_seat_interface, v);
        wl_proxy_set_queue((wl_proxy*)g_w.seat, g_w.queue);
        wl_seat_add_listener(g_w.seat, &seat_listener, nullptr);
        logf("registry: bound wl_seat v%u", v);
    }
}

static void reg_global_remove(void*, wl_registry*, uint32_t) {}

static const wl_registry_listener reg_listener = { reg_global, reg_global_remove };

// ============================================================================
// Worker thread — waits for engine window, then runs Wayland event loop
// ============================================================================

static void* worker_thread(void*)
{
    log_init();
    logf("worker thread started — SDL state array updates ENABLED");

    // Step 1: wait for libengine2.so to be loaded by the .NET launcher and
    // resolve its embedded SDL3 symbols. Constructor runs *before* the engine
    // dlopens libengine2, so a single attempt would always fail.
    if (!resolve_sdl_symbols_with_retry()) return nullptr;

    // Step 2: wait for SDL to create its window. Poll up to 30s.
    void** windows = nullptr;
    int wcount = 0;
    for (int i = 0; i < 600; ++i) {
        windows = p_SDL_GetWindows(&wcount);
        if (windows && wcount > 0) break;
        if (windows) free(windows);
        windows = nullptr;
        usleep(50000); // 50ms
    }
    if (!windows || wcount == 0) {
        logf("FATAL: SDL_GetWindows returned no windows after 30s");
        return nullptr;
    }
    void* engine_window = windows[0];
    g_w.engine_window_id = p_SDL_GetWindowID(engine_window);
    g_w.engine_window    = engine_window;  // store for SDL_Send* calls
    free(windows);

    const char* drv = p_SDL_GetCurrentVideoDriver ? p_SDL_GetCurrentVideoDriver() : nullptr;
    logf("engine SDL window=%p id=%u driver=%s", engine_window, g_w.engine_window_id,
         drv ? drv : "?");

    if (drv && strcmp(drv, "wayland") != 0) {
        logf("video driver is not wayland — disabling injection");
        return nullptr;
    }

    // Step 3: borrow wl_display + wl_surface from SDL window properties.
    uint32_t props_id = p_SDL_GetWindowProperties(engine_window);
    g_w.display = (wl_display*)p_SDL_GetPointerProperty(props_id,
        "SDL.window.wayland.display", nullptr);
    g_w.engine_surface = (wl_surface*)p_SDL_GetPointerProperty(props_id,
        "SDL.window.wayland.surface", nullptr);

    if (!g_w.display || !g_w.engine_surface) {
        logf("FATAL: failed to read wayland display/surface from SDL props "
             "(display=%p surface=%p)", g_w.display, g_w.engine_surface);
        return nullptr;
    }
    logf("borrowed wl_display=%p engine wl_surface=%p", g_w.display, g_w.engine_surface);

    // Step 4: set up xkb context.
    g_w.xkb_ctx = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
    if (!g_w.xkb_ctx) { logf("FATAL: xkb_context_new failed"); return nullptr; }

    // Step 5: create our own queue on the engine's display, get registry on it.
    g_w.queue = wl_display_create_queue(g_w.display);
    if (!g_w.queue) { logf("FATAL: wl_display_create_queue failed"); return nullptr; }

    g_w.registry = wl_display_get_registry(g_w.display);
    wl_proxy_set_queue((wl_proxy*)g_w.registry, g_w.queue);
    wl_registry_add_listener(g_w.registry, &reg_listener, nullptr);

    // Roundtrip on our queue to populate registry + bind seat.
    wl_display_roundtrip_queue(g_w.display, g_w.queue);
    // Second roundtrip so seat capabilities arrive and we bind kbd/pointer.
    wl_display_roundtrip_queue(g_w.display, g_w.queue);
    // Third for keymap arriving.
    wl_display_roundtrip_queue(g_w.display, g_w.queue);

    logf("setup complete — entering event loop");

    // Step 6: resolve C# [UnmanagedCallersOnly] trampolines.
    // These live in the managed assembly, not libengine2.so, so we use
    // RTLD_DEFAULT. The .NET runtime exports them as native symbols.
    // The assembly may load AFTER our worker starts, so we retry.
    bool trampolines_ready = false;
    for (int retry = 0; retry < 120; ++retry) {
        p_Engine_OnMouseMotion = (pfn_Engine_OnMouseMotion)
            dlsym(RTLD_DEFAULT, "SandboxEngine_InputRouter_OnMouseMotion");
        p_Engine_OnMouseButton = (pfn_Engine_OnMouseButton)
            dlsym(RTLD_DEFAULT, "SandboxEngine_InputRouter_OnMouseButton");
        p_Engine_OnKey         = (pfn_Engine_OnKey)
            dlsym(RTLD_DEFAULT, "SandboxEngine_InputRouter_OnKey");
        p_Engine_OnText        = (pfn_Engine_OnText)
            dlsym(RTLD_DEFAULT, "SandboxEngine_InputRouter_OnText");
        p_Engine_OnMouseWheel  = (pfn_Engine_OnMouseWheel)
            dlsym(RTLD_DEFAULT, "SandboxEngine_InputRouter_OnMouseWheel");
        p_Engine_OnMousePositionChange = (pfn_Engine_OnMousePositionChange)
            dlsym(RTLD_DEFAULT, "SandboxEngine_InputRouter_OnMousePositionChange");
        if (p_Engine_OnMouseMotion && p_Engine_OnKey && p_Engine_OnMouseButton) {
            trampolines_ready = true;
            logf("trampolines resolved after %d retries", retry);
            break;
        }
        usleep(500000); // 500ms
    }
    if (!trampolines_ready) {
        logf("trampolines NOT resolved — engine callbacks unreachable "
             "(OnMM=%p OnKey=%p OnMB=%p OnMW=%p OnMC=%p OnText=%p)",
             (void*)p_Engine_OnMouseMotion, (void*)p_Engine_OnKey,
             (void*)p_Engine_OnMouseButton, (void*)p_Engine_OnMouseWheel,
             (void*)p_Engine_OnMousePositionChange, (void*)p_Engine_OnText);
    } else {
        logf("trampolines VERIFIED — testing callbacks...");
        // Test that callbacks are actually callable
        p_Engine_OnMouseMotion(0, 0);
        p_Engine_OnMouseButton(314, 1, 0);
        p_Engine_OnKey(11, 11, 1, 0, 0);
        p_Engine_OnText(65);
        p_Engine_OnMouseWheel(0, 0, 0);
        p_Engine_OnMousePositionChange(100, 100, 0, 0);
        logf("trampolines OK — callbacks executed");
    }

    // Step 7: dispatch loop. wl_display_dispatch_queue blocks until events.
    while (true) {
        int ret = wl_display_dispatch_queue(g_w.display, g_w.queue);
        if (ret < 0) {
            logf("wl_display_dispatch_queue returned %d (errno=%d %s) — exiting",
                 ret, errno, strerror(errno));
            break;
        }
    }

    return nullptr;
}

// ============================================================================
// Constructor — kick off worker thread at library load time
// ============================================================================

extern "C" __attribute__((constructor(200)))
void sbox_wayland_input_init()
{
    // Only activate if user is likely on Wayland. SDL_VIDEODRIVER unset and
    // WAYLAND_DISPLAY set is the heuristic.
    const char* wd = getenv("WAYLAND_DISPLAY");
    if (!wd || !*wd) {
        // No Wayland session — silently no-op.
        return;
    }
    log_init();
    logf("WAYLAND_DISPLAY=%s — scheduling worker thread", wd);

    // Force SDL3 inside libengine2 to prefer the Wayland video backend over
    // x11/XWayland. Without this, engine2's startup picks x11 and our injection
    // self-disables (driver != "wayland"). Both env names are accepted across
    // SDL3 versions; we set them all and only if the user hasn't already chosen.
    if (!getenv("SDL_VIDEODRIVER")) {
        setenv("SDL_VIDEODRIVER", "wayland", 1);
        logf("forced SDL_VIDEODRIVER=wayland");
    } else {
        logf("SDL_VIDEODRIVER already set to '%s' — leaving it", getenv("SDL_VIDEODRIVER"));
    }
    if (!getenv("SDL_VIDEO_DRIVER")) {
        setenv("SDL_VIDEO_DRIVER", "wayland", 1);
    }
    // SDL3 also reads SDL_HINT_VIDEO_DRIVER but that's resolved from the env
    // var above at SDL_Init time, so a single setenv covers it.

    pthread_t tid;
    if (pthread_create(&tid, nullptr, worker_thread, nullptr) != 0) {
        logf("pthread_create failed: %s", strerror(errno));
        return;
    }
    pthread_detach(tid);
}
