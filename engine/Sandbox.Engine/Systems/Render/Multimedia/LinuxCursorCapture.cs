using System.Runtime.InteropServices;
using System.Threading;
using NativeEngine;
using Sandbox.Engine;
using System.Text;

namespace Sandbox.Systems.Render.Multimedia
{
#if !WIN
    /// <summary>
    /// Linux X11-based input — bypasses SDL3 entirely due to RTLD_DEEPBIND creating
    /// a private SDL3 instance. Opens a second X11 Display connection, finds the
    /// engine window via XQueryTree, and polls events directly with XPending/XNextEvent.
    /// </summary>
    public static unsafe class LinuxSDLInput
    {
        // X11 Display and window handles
        private static IntPtr _display = IntPtr.Zero;
        private static ulong _engineWindow = 0;
        private static bool _initialized = false;
        
        // Mouse tracking for relative mode
        private static bool _firstMotion = true;

        // Relative mode state tracking
        private static bool _relModeActive = false;

        // X11 event size no longer needed (state polling replaces event reading)
        // private const int X_EVENT_SIZE = 192;

        // X11 event type constants (kept for reference)
        // private const int KeyPress = 2; ...

        // X11 structs
        [StructLayout(LayoutKind.Sequential)]
        private struct XClassHint
        {
            public IntPtr res_name;
            public IntPtr res_class;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XWindowAttributes
        {
            public int x, y;
            public int width, height;
            public int border_width;
            public int depth;
            public IntPtr visual;
            public ulong root;
            public int c_class;
            public int bit_gravity;
            public int win_gravity;
            public int backing_store;
            public ulong backing_planes;
            public ulong backing_pixel;
            public int save_under;
            public int colormap;
            public int map_installed;
            public int map_state;
            public long all_event_masks;
            public long your_event_mask;
            public long do_not_propagate_mask;
            public int override_redirect;
            public IntPtr screen;
        }

        // X11 P/Invoke declarations
        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr XOpenDisplay([MarshalAs(UnmanagedType.LPStr)] string displayName);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XCloseDisplay(IntPtr display);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong XDefaultRootWindow(IntPtr display);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XQueryTree(IntPtr display, ulong w, out ulong root_return, 
            out ulong parent_return, out IntPtr children_return, out uint nchildren_return);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XFree(IntPtr data);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XGetClassHint(IntPtr display, ulong w, out XClassHint class_hint_return);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XFetchName(IntPtr display, ulong w, out IntPtr window_name_return);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong XLookupKeysym(IntPtr key_event, int index);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XGetWindowAttributes(IntPtr display, ulong w, out XWindowAttributes window_attributes_return);

        // XQueryKeymap: fills keys_return[32] with bitmask of currently pressed keycodes
        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XQueryKeymap(IntPtr display, byte* keys_return);

        // XQueryPointer: returns pointer position and button state
        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool XQueryPointer(IntPtr display, ulong w,
            out ulong root_return, out ulong child_return,
            out int root_x_return, out int root_y_return,
            out int win_x_return, out int win_y_return,
            out uint mask_return);

        // XGetInputFocus: returns the window that currently has keyboard focus
        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XGetInputFocus(IntPtr display, out ulong focus_return, out int revert_to_return);

        // XWarpPointer: move the cursor to an absolute position within a window
        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XWarpPointer(IntPtr display, ulong src_w, ulong dest_w,
            int src_x, int src_y, uint src_width, uint src_height,
            int dest_x, int dest_y);

        // XFlush: flush output buffer to X server
        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XFlush(IntPtr display);

        // XSync: flush output buffer synchronously to X server
        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XSync(IntPtr display, bool discard);

        // XDefaultScreen: get the default screen number for a display
        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XDefaultScreen(IntPtr display);

        // XDisplayWidth/Height: get screen dimensions
        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XDisplayWidth(IntPtr display, int screen_number);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XDisplayHeight(IntPtr display, int screen_number);

        private static bool _ownRelativeMode = false;

        // Screen dimensions for root-window warp-to-center
        private static int _screenWidth = 0;
        private static int _screenHeight = 0;

        // Number of consecutive frames where relMode wants to be false.
        // RelMode only actually drops after this exceeds the threshold (hysteresis).
        private static int _relModeDropFrames = 0;
        private const int RelModeDropThreshold = 4; // ~4 frames at 60fps ≈ 66ms

        /// <summary>
        /// Returns true if 'candidate' is 'ancestor' or any descendant of 'ancestor'.
        /// Used to handle X11 focus going to child windows (e.g. SDL3 rendering subwindow).
        /// </summary>
        private static bool IsWindowOrDescendant(ulong ancestor, ulong candidate, int depth = 0)
        {
            if (candidate == 0 || ancestor == 0) return false;
            if (candidate == ancestor) return true;
            if (depth > 6) return false; // limit recursion

            if (XQueryTree(_display, candidate, out _, out ulong parent, out IntPtr children, out uint nchildren) == 0)
                return false;

            if (children != IntPtr.Zero) XFree(children);

            if (parent == ancestor) return true;
            if (parent == 0 || parent == XDefaultRootWindow(_display)) return false;

            return IsWindowOrDescendant(ancestor, parent, depth + 1);
        }

        // State polling: previous frame keyboard/mouse state for edge detection
        private static readonly bool[] _prevKeyState = new bool[256];
        private static readonly bool[] _prevButtonState = new bool[6]; // buttons 1-5
        private static int _prevMouseX = 0;
        private static int _prevMouseY = 0;
        private static bool _hasFocus = false;

        /// <summary>
        /// True when the X11 engine window has keyboard/mouse focus.
        /// Used by InputRouter to replace the broken SDL3 HasMouseFocus() native call.
        /// </summary>
        public static bool HasX11Focus => _hasFocus;

        /// <summary>
        /// True when Linux X11 relative/capture mode is active.
        /// Used by InputRouter to replace the SDL3-based IsMouseCaptured on Linux.
        /// </summary>
        public static bool IsInRelativeMode => _ownRelativeMode;

        // X11 keycode to ButtonCode lookup table
        // X11 keycodes are hardware-dependent but on standard Linux/X11 with evdev driver:
        // keycode = evdev scancode + 8
        private static readonly ButtonCode[] _keycodeTable = BuildKeycodeTable();

        private static ButtonCode[] BuildKeycodeTable()
        {
            var t = new ButtonCode[256];
            for (int i = 0; i < 256; i++) t[i] = ButtonCode.BUTTON_CODE_INVALID;

            // Row 1: Escape, 1-9, 0, -, =, Backspace
            t[9] = ButtonCode.KEY_ESCAPE;
            t[10] = ButtonCode.KEY_1; t[11] = ButtonCode.KEY_2; t[12] = ButtonCode.KEY_3;
            t[13] = ButtonCode.KEY_4; t[14] = ButtonCode.KEY_5; t[15] = ButtonCode.KEY_6;
            t[16] = ButtonCode.KEY_7; t[17] = ButtonCode.KEY_8; t[18] = ButtonCode.KEY_9;
            t[19] = ButtonCode.KEY_0;
            t[20] = ButtonCode.KEY_MINUS; t[21] = ButtonCode.KEY_EQUAL;
            t[22] = ButtonCode.KEY_BACKSPACE; t[23] = ButtonCode.KEY_TAB;

            // Row 2: QWERTY
            t[24] = ButtonCode.KEY_Q; t[25] = ButtonCode.KEY_W; t[26] = ButtonCode.KEY_E;
            t[27] = ButtonCode.KEY_R; t[28] = ButtonCode.KEY_T; t[29] = ButtonCode.KEY_Y;
            t[30] = ButtonCode.KEY_U; t[31] = ButtonCode.KEY_I; t[32] = ButtonCode.KEY_O;
            t[33] = ButtonCode.KEY_P;
            t[34] = ButtonCode.KEY_LBRACKET; t[35] = ButtonCode.KEY_RBRACKET;
            t[36] = ButtonCode.KEY_ENTER; t[37] = ButtonCode.KEY_LCONTROL;

            // Row 3: ASDF
            t[38] = ButtonCode.KEY_A; t[39] = ButtonCode.KEY_S; t[40] = ButtonCode.KEY_D;
            t[41] = ButtonCode.KEY_F; t[42] = ButtonCode.KEY_G; t[43] = ButtonCode.KEY_H;
            t[44] = ButtonCode.KEY_J; t[45] = ButtonCode.KEY_K; t[46] = ButtonCode.KEY_L;
            t[47] = ButtonCode.KEY_SEMICOLON; t[48] = ButtonCode.KEY_APOSTROPHE;
            t[49] = ButtonCode.KEY_BACKQUOTE; t[50] = ButtonCode.KEY_LSHIFT;
            t[51] = ButtonCode.KEY_BACKSLASH;

            // Row 4: ZXCV
            t[52] = ButtonCode.KEY_Z; t[53] = ButtonCode.KEY_X; t[54] = ButtonCode.KEY_C;
            t[55] = ButtonCode.KEY_V; t[56] = ButtonCode.KEY_B; t[57] = ButtonCode.KEY_N;
            t[58] = ButtonCode.KEY_M;
            t[59] = ButtonCode.KEY_COMMA; t[60] = ButtonCode.KEY_PERIOD;
            t[61] = ButtonCode.KEY_SLASH; t[62] = ButtonCode.KEY_RSHIFT;

            // Modifiers and special
            t[63] = ButtonCode.KEY_PAD_MULTIPLY; // PAD_MULTIPLY
            t[64] = ButtonCode.KEY_LALT;
            t[65] = ButtonCode.KEY_SPACE;
            t[66] = ButtonCode.KEY_CAPSLOCK;

            // F1-F10
            t[67] = ButtonCode.KEY_F1; t[68] = ButtonCode.KEY_F2; t[69] = ButtonCode.KEY_F3;
            t[70] = ButtonCode.KEY_F4; t[71] = ButtonCode.KEY_F5; t[72] = ButtonCode.KEY_F6;
            t[73] = ButtonCode.KEY_F7; t[74] = ButtonCode.KEY_F8; t[75] = ButtonCode.KEY_F9;
            t[76] = ButtonCode.KEY_F10;

            // Lock keys
            t[77] = ButtonCode.KEY_NUMLOCK;
            t[78] = ButtonCode.KEY_SCROLLLOCK;

            // Numpad
            t[79] = ButtonCode.KEY_PAD_7; t[80] = ButtonCode.KEY_PAD_8; t[81] = ButtonCode.KEY_PAD_9;
            t[82] = ButtonCode.KEY_PAD_MINUS;
            t[83] = ButtonCode.KEY_PAD_4; t[84] = ButtonCode.KEY_PAD_5; t[85] = ButtonCode.KEY_PAD_6;
            t[86] = ButtonCode.KEY_PAD_PLUS;
            t[87] = ButtonCode.KEY_PAD_1; t[88] = ButtonCode.KEY_PAD_2; t[89] = ButtonCode.KEY_PAD_3;
            t[90] = ButtonCode.KEY_PAD_0; t[91] = ButtonCode.KEY_PAD_DECIMAL;

            // F11, F12
            t[95] = ButtonCode.KEY_F11; t[96] = ButtonCode.KEY_F12;

            // More numpad and controls
            t[104] = ButtonCode.KEY_PAD_ENTER;
            t[105] = ButtonCode.KEY_RCONTROL;
            t[106] = ButtonCode.KEY_PAD_DIVIDE;
            t[107] = ButtonCode.KEY_PRINTSCREEN;
            t[108] = ButtonCode.KEY_RALT;

            // Navigation cluster
            t[110] = ButtonCode.KEY_HOME;
            t[111] = ButtonCode.KEY_UP;
            t[112] = ButtonCode.KEY_PAGEUP;
            t[113] = ButtonCode.KEY_LEFT;
            t[114] = ButtonCode.KEY_RIGHT;
            t[115] = ButtonCode.KEY_END;
            t[116] = ButtonCode.KEY_DOWN;
            t[117] = ButtonCode.KEY_PAGEDOWN;
            t[118] = ButtonCode.KEY_INSERT;
            t[119] = ButtonCode.KEY_DELETE;

            t[127] = ButtonCode.KEY_BREAK; // Pause/Break

            // Windows/Super keys (may vary by DE)
            t[133] = ButtonCode.KEY_LWIN;
            t[134] = ButtonCode.KEY_RWIN;
            t[135] = ButtonCode.KEY_APP; // Menu key

            return t;
        }

        /// <summary>
        /// Recursively search for the engine window starting from the root window.
        /// Looks for windows with SDL class hint or sbox in name.
        /// </summary>
        private static ulong FindEngineWindow(IntPtr display, ulong root, int depth = 0)
        {
            if (depth > 5) return 0; // Limit recursion

            if (XQueryTree(display, root, out _, out _, out IntPtr children, out uint nchildren) == 0)
                return 0;

            ulong result = 0;

            try
            {
                if (children != IntPtr.Zero && nchildren > 0)
                {
                    unsafe
                    {
                        ulong* childArray = (ulong*)children;
                        for (uint i = 0; i < nchildren; i++)
                        {
                            ulong child = childArray[i];
                            
                            // Try class hint first
                            if (XGetClassHint(display, child, out XClassHint hint) != 0)
                            {
                                try
                                {
                                    if (hint.res_class != IntPtr.Zero)
                                    {
                                        string className = Marshal.PtrToStringAnsi(hint.res_class) ?? "";
                                        if (className.Contains("SDL", StringComparison.OrdinalIgnoreCase))
                                        {
                                            Log.Info($"[LinuxSDLInput] Found engine window via class hint: 0x{child:X} (class={className})");
                                            return child;
                                        }
                                    }
                                    if (hint.res_name != IntPtr.Zero)
                                    {
                                        string resName = Marshal.PtrToStringAnsi(hint.res_name) ?? "";
                                        if (resName.Contains("sbox", StringComparison.OrdinalIgnoreCase) ||
                                            resName.Contains("s&box", StringComparison.OrdinalIgnoreCase))
                                        {
                                                        Log.Info($"[LinuxSDLInput] Found engine window via res_name: 0x{child:X} (name={resName})");
                                            return child;
                                        }
                                    }
                                }
                                finally
                                {
                                    if (hint.res_name != IntPtr.Zero) XFree(hint.res_name);
                                    if (hint.res_class != IntPtr.Zero) XFree(hint.res_class);
                                }
                            }

                            // Try window title
                            if (XFetchName(display, child, out IntPtr windowName) != 0 && windowName != IntPtr.Zero)
                            {
                                try
                                {
                                    string title = Marshal.PtrToStringAnsi(windowName) ?? "";
                                    if (title.Contains("s&box", StringComparison.OrdinalIgnoreCase) ||
                                        title.Contains("sbox", StringComparison.OrdinalIgnoreCase))
                                    {
                                        Log.Info($"[LinuxSDLInput] Found engine window via title: 0x{child:X} (title={title})");
                                        return child;
                                    }
                                }
                                finally
                                {
                                    XFree(windowName);
                                }
                            }

                            // Recurse into children
                            result = FindEngineWindow(display, child, depth + 1);
                            if (result != 0) return result;
                        }
                    }
                }
            }
            finally
            {
                if (children != IntPtr.Zero)
                    XFree(children);
            }

            return 0;
        }

        /// <summary>
        /// Initialize the X11 input system. Opens display, finds engine window, 
        /// and subscribes to events.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            try
            {
                string displayName = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
                _display = XOpenDisplay(displayName);
                
                if (_display == IntPtr.Zero)
                {
                    Log.Error($"[LinuxSDLInput] Failed to open X11 display: {displayName}");
                    return;
                }

                ulong root = XDefaultRootWindow(_display);
                Log.Info($"[LinuxSDLInput] Opened X11 display, root window: 0x{root:X}");

                // Try to find the engine window with retries
                for (int attempt = 0; attempt < 10 && _engineWindow == 0; attempt++)
                {
                    _engineWindow = FindEngineWindow(_display, root);
                    if (_engineWindow == 0)
                    {
                        if (attempt < 9) // Don't sleep on last attempt
                        {
                            Log.Info($"[LinuxSDLInput] Window not found, retry {attempt + 1}/10...");
                            Thread.Sleep(100);
                        }
                    }
                }

                if (_engineWindow == 0)
                {
                    Log.Warning("[LinuxSDLInput] Could not find engine window after 10 attempts — continuing with _engineWindow=0 (will assume focus)");
                    // Do NOT close display or return — we still need the display for keyboard/mouse polling
                    // HasX11Focus will default to true when _engineWindow==0
                }

                // State polling — no XSelectInput needed

                // Initialize screen dimensions for root-window warp-to-center
                int screen = XDefaultScreen(_display);
                _screenWidth = XDisplayWidth(_display, screen);
                _screenHeight = XDisplayHeight(_display, screen);
                Log.Info($"[LinuxSDLInput] Screen dimensions: {_screenWidth}x{_screenHeight}");

                _initialized = true;
                Log.Info($"[LinuxSDLInput] Successfully initialized. Window: 0x{_engineWindow:X}");
                // Log initial focus state
                XGetInputFocus(_display, out ulong initFocus, out _);
                Log.Info($"[LinuxSDLInput] Initial focus window: 0x{initFocus:X}, engineWindow: 0x{_engineWindow:X}, match={initFocus == _engineWindow}");
            }
            catch (Exception ex)
            {
                Log.Error($"[LinuxSDLInput] Initialize failed: {ex.Message}");
                if (_display != IntPtr.Zero)
                {
                    XCloseDisplay(_display);
                    _display = IntPtr.Zero;
                }
            }
        }

        /// <summary>
        /// Poll X11 input state every frame using XQueryKeymap + XQueryPointer.
        /// State polling works from any Display connection — no event subscription needed.
        /// Edge detection (press/release) is done by comparing to previous frame state.
        /// </summary>
        public static void PollEvents()
        {
            if (!_initialized || _display == IntPtr.Zero) return;

            try
            {
                // --- Focus check ---
                // When we own relative mode (pointer is grabbed), skip XGetInputFocus.
                // A grabbed pointer means we have effective focus — XGetInputFocus reports
                // grab-related pseudo-windows that are not in our window tree, causing false
                // focus-loss detection and RelMode oscillation.
                bool hasFocus;
                if (_ownRelativeMode)
                {
                    hasFocus = true;
                }
                else if (_engineWindow == 0)
                {
                    // Window not found during init — assume focus to prevent MouseCursorVisible=true lockout
                    hasFocus = true;
                }
                else
                {
                    XGetInputFocus(_display, out ulong focusWindow, out _);
                    // Check if focused window is our engine window OR any of its descendants.
                    // SDL3 creates child windows for rendering; focus goes to the deepest accepting window.
                    hasFocus = IsWindowOrDescendant(_engineWindow, focusWindow);
                }
                if (hasFocus != _hasFocus)
                {
                    Log.Info($"[LinuxSDLInput] Focus changed: hasFocus={hasFocus} _ownRelativeMode={_ownRelativeMode} engineWindow=0x{_engineWindow:X}");
                    _hasFocus = hasFocus;
                    InputRouter.OnWindowActive(hasFocus);
                }

                // --- Keyboard state ---
                byte* keys = stackalloc byte[32];
                XQueryKeymap(_display, keys);

                for (int kc = 8; kc < 256; kc++)
                {
                    bool isDown = (keys[kc >> 3] & (1 << (kc & 7))) != 0;
                    bool wasDown = _prevKeyState[kc];

                    if (isDown != wasDown)
                    {
                        _prevKeyState[kc] = isDown;
                        var bc = _keycodeTable[kc];
                        if (bc != ButtonCode.BUTTON_CODE_INVALID)
                            InputRouter.OnKey(bc, bc, isDown, false, 0);
                    }
                }

                // --- Mouse state ---
                ulong queryWindow = _engineWindow != 0 ? _engineWindow : XDefaultRootWindow(_display);
                XQueryPointer(_display, queryWindow,
                    out _, out _,
                    out _, out _,
                    out int winX, out int winY,
                    out uint buttonMask);

		// Capture when: we have X11 focus, a game is running, and no context is actively requesting UI mouse.
		// Use InputRouter.GameWantsCapture instead of MouseCursorVisible to avoid 1-frame lag.
		// MouseCursorVisible is set in Frame() which runs AFTER PollEvents() — reading it here
		// would always use the previous frame's value. GameWantsCapture is computed fresh each call.
		bool wantsRelMode = _hasFocus && InputRouter.GameWantsCapture;

                // Hysteresis: only drop RelMode after wantsRelMode has been false for several consecutive frames.
                // This absorbs transient MouseCursorVisible=True spikes from UI panels briefly wanting mouse
                // (e.g. tooltip hover, panel focus change) without permanently dropping capture.
                // Entering RelMode is immediate (no delay needed — false positives here are harmless).
                bool relMode;
                if (wantsRelMode)
                {
                    _relModeDropFrames = 0;
                    relMode = true;
                }
                else if (_ownRelativeMode)
                {
                    _relModeDropFrames++;
                    relMode = _relModeDropFrames >= RelModeDropThreshold ? false : true;
                }
                else
                {
                    _relModeDropFrames = 0;
                    relMode = false;
                }

                // Track transitions for logging
                if (relMode != _ownRelativeMode)
                {
                    Log.Info($"[LinuxSDLInput] RelMode changed: {_ownRelativeMode} → {relMode} (hasFocus={_hasFocus}, MouseCursorVisible={InputRouter.MouseCursorVisible}, dropFrames={_relModeDropFrames})");
                    _ownRelativeMode = relMode;
                    if (!relMode)
                    {
                        _firstMotion = true;
                        _relModeDropFrames = 0;
                    }
                    else
                    {
                        // Entering relative mode: reset state
                        _firstMotion = true;
                    }
                }

                if (relMode)
                {
                    ulong rootWindow = XDefaultRootWindow(_display);
                    int cx = _screenWidth / 2;
                    int cy = _screenHeight / 2;

                    XQueryPointer(_display, rootWindow, out _, out _, out int rx, out int ry, out _, out _, out _);

                    if (!_relModeActive)
                    {
                        // First frame after entering rel mode: warp only, no delta (re-entry guard)
                        _relModeActive = true;
                    }
                    else
                    {
                        int dx = rx - cx;
                        int dy = ry - cy;
                        const int MaxDelta = 500; // Clamp pathological frames (alt-tab race, SDL re-grab)
			if (Math.Abs(dx) < MaxDelta && Math.Abs(dy) < MaxDelta && (dx != 0 || dy != 0))
			{
				Input.AddMouseMovement(new Vector2(dx, dy));
			}
                    }

                    XWarpPointer(_display, 0, rootWindow, 0, 0, 0, 0, cx, cy);
                    XSync(_display, false);
                }
                else
                {
                    _relModeActive = false;

                    // UI/menu mode: report absolute position + delta
                    if (_firstMotion)
                    {
                        _prevMouseX = winX;
                        _prevMouseY = winY;
                        _firstMotion = false;
                    }

                    int dx = winX - _prevMouseX;
                    int dy = winY - _prevMouseY;

                    if (dx != 0 || dy != 0)
                    {
                        InputRouter.OnMousePositionChange(winX, winY, dx, dy);
                        _prevMouseX = winX;
                        _prevMouseY = winY;
                    }
                }

                // Mouse buttons: mask bits — Button1Mask=0x100, Button2Mask=0x200, Button3Mask=0x400
                // Button4/5 (wheel) appear transiently — not reliably pollable; skip for now
                CheckMouseButton(buttonMask, 1, 0x100, ButtonCode.MouseLeft);
                CheckMouseButton(buttonMask, 2, 0x200, ButtonCode.MouseMiddle);
                CheckMouseButton(buttonMask, 3, 0x400, ButtonCode.MouseRight);
                CheckMouseButton(buttonMask, 4, 0x800, ButtonCode.MouseBack);
                CheckMouseButton(buttonMask, 5, 0x1000, ButtonCode.MouseForward);
            }
            catch (Exception ex)
            {
                Log.Error($"[LinuxSDLInput] PollEvents exception: {ex.Message}");
            }
        }

        private static void CheckMouseButton(uint mask, int btnIndex, uint btnMask, ButtonCode bc)
        {
            bool isDown = (mask & btnMask) != 0;
            bool wasDown = _prevButtonState[btnIndex];
            if (isDown != wasDown)
            {
                _prevButtonState[btnIndex] = isDown;
                InputRouter.OnMouseButton(bc, isDown, 0);
            }
        }
    }

    /// <summary>
    /// Linux cursor capture implementation using X11 (stub implementation)
    /// </summary>
    public static class LinuxCursorCapture
    {
        public static void HideCursor()
        {
            // Stub - cursor hiding not implemented
        }

        public static void ShowCursor()
        {
            // Stub - cursor showing not implemented
        }

        public static unsafe void BlitCursorToBuffer(byte* buffer, int width, int stride, int mouseX, int mouseY)
        {
            // Stub - cursor blitting not implemented
        }

        public static IntPtr GetCursor()
        {
            return IntPtr.Zero;
        }

        public static bool IsCursorVisible()
        {
            return true;
        }

        public static void GetCursorInfo(out bool visible, out IntPtr handle)
        {
            visible = true;
            handle = IntPtr.Zero;
        }

        public static IntPtr GetCursorShape(out IntPtr shape, int size)
        {
            shape = IntPtr.Zero;
            return IntPtr.Zero;
        }

        public static bool GetCursorBitmap(out IntPtr bitmap, ref int width, ref int height)
        {
            bitmap = IntPtr.Zero;
            width = 16;
            height = 16;
            return false;
        }

        public static void WarpMouse(int x, int y)
        {
            // Stub - mouse warping not implemented
        }

        public static IntPtr GetGlobalMouseState(out int mouseX, out int mouseY)
        {
            mouseX = 0;
            mouseY = 0;
            return IntPtr.Zero;
        }
    }
#endif
}
