using System.Runtime.InteropServices;
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
        private static bool _windowFound = false;
        
        // Mouse tracking for relative mode
        private static float _lastX = 0;
        private static float _lastY = 0;
        private static bool _firstMotion = true;

        // X11 event size (XEvent union is 192 bytes on 64-bit Linux)
        private const int X_EVENT_SIZE = 192;

        // X11 event type constants
        private const int KeyPress = 2;
        private const int KeyRelease = 3;
        private const int ButtonPress = 4;
        private const int ButtonRelease = 5;
        private const int MotionNotify = 6;
        private const int FocusIn = 9;
        private const int FocusOut = 10;

        // X11 event mask constants
        private const long KeyPressMask = 0x00000001L;
        private const long KeyReleaseMask = 0x00000002L;
        private const long ButtonPressMask = 0x00000004L;
        private const long ButtonReleaseMask = 0x00000008L;
        private const long PointerMotionMask = 0x00000040L;
        private const long FocusChangeMask = 0x00200000L;

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
        private static extern int XSelectInput(IntPtr display, ulong w, long event_mask);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XPending(IntPtr display);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XNextEvent(IntPtr display, IntPtr event_return);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XGetClassHint(IntPtr display, ulong w, out XClassHint class_hint_return);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XFetchName(IntPtr display, ulong w, out IntPtr window_name_return);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong XLookupKeysym(IntPtr key_event, int index);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XGetWindowAttributes(IntPtr display, ulong w, out XWindowAttributes window_attributes_return);

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
        /// Looks for windows with SDL class hint or s&box/sbox in name.
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
                    Log.Error("[LinuxSDLInput] Could not find engine window after 10 attempts");
                    XCloseDisplay(_display);
                    _display = IntPtr.Zero;
                    return;
                }

                _windowFound = true;

                // Subscribe to input events
                long eventMask = KeyPressMask | KeyReleaseMask | ButtonPressMask | 
                               ButtonReleaseMask | PointerMotionMask | FocusChangeMask;
                
                if (XSelectInput(_display, _engineWindow, eventMask) == 0)
                {
                    Log.Error("[LinuxSDLInput] XSelectInput failed");
                    XCloseDisplay(_display);
                    _display = IntPtr.Zero;
                    _engineWindow = 0;
                    return;
                }

                _initialized = true;
                Log.Info($"[LinuxSDLInput] Successfully initialized. Window: 0x{_engineWindow:X}, Events: key/mouse/focus");
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
        /// Poll X11 events. Called every frame from EngineLoop.
        /// </summary>
        public static void PollEvents()
        {
            if (!_initialized || _display == IntPtr.Zero) return;

            try
            {
                while (XPending(_display) > 0)
                {
                    // Allocate event buffer on stack
                    byte* eventBuf = stackalloc byte[X_EVENT_SIZE];
                    
                    if (XNextEvent(_display, (IntPtr)eventBuf) != 0)
                        continue;

                    // Read event type from offset 0
                    int eventType = *(int*)eventBuf;

                    switch (eventType)
                    {
                        case KeyPress:
                        case KeyRelease:
                            HandleKeyEvent(eventBuf, eventType == KeyPress);
                            break;

                        case ButtonPress:
                        case ButtonRelease:
                            HandleButtonEvent(eventBuf, eventType == ButtonPress);
                            break;

                        case MotionNotify:
                            HandleMotionEvent(eventBuf);
                            break;

                        case FocusIn:
                            InputRouter.OnWindowActive(true);
                            break;

                        case FocusOut:
                            InputRouter.OnWindowActive(false);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[LinuxSDLInput] PollEvents exception: {ex.Message}");
            }
        }

        private static void HandleKeyEvent(byte* eventBuf, bool isDown)
        {
            // XKeyEvent: keycode is at offset 84 (uint)
            uint keycode = *(uint*)(eventBuf + 84);
            
            if (keycode < _keycodeTable.Length)
            {
                var bc = _keycodeTable[keycode];
                if (bc != ButtonCode.BUTTON_CODE_INVALID)
                {
                    InputRouter.OnKey(bc, bc, isDown, false, 0);
                }
            }
        }

        private static void HandleButtonEvent(byte* eventBuf, bool isDown)
        {
            // XButtonEvent: button is at offset 88 (uint)
            uint button = *(uint*)(eventBuf + 88);

            // Mouse wheel is sent as button 4 (up) and 5 (down)
            if (button == 4 || button == 5)
            {
                if (isDown) // Only process press, not release
                {
                    int delta = (button == 4) ? 1 : -1;
                    InputRouter.OnMouseWheel(0, delta, 0);
                }
                return;
            }

            ButtonCode bc = button switch
            {
                1 => ButtonCode.MouseLeft,
                2 => ButtonCode.MouseMiddle,
                3 => ButtonCode.MouseRight,
                8 => ButtonCode.MouseBack,
                9 => ButtonCode.MouseForward,
                _ => ButtonCode.BUTTON_CODE_INVALID
            };

            if (bc != ButtonCode.BUTTON_CODE_INVALID)
            {
                InputRouter.OnMouseButton(bc, isDown, 0);
            }
        }

        private static void HandleMotionEvent(byte* eventBuf)
        {
            // XMotionEvent: x(64,int) y(68,int) x_root(72,int) y_root(76,int)
            int x = *(int*)(eventBuf + 64);
            int y = *(int*)(eventBuf + 68);
            int xRoot = *(int*)(eventBuf + 72);
            int yRoot = *(int*)(eventBuf + 76);

            if (_firstMotion)
            {
                _lastX = x;
                _lastY = y;
                _firstMotion = false;
            }

            float dx = x - _lastX;
            float dy = y - _lastY;
            _lastX = x;
            _lastY = y;

            bool relativeMode = NativeEngine.InputSystem.GetRelativeMouseMode();
            
            if (relativeMode)
            {
                InputRouter.OnMouseMotion(dx, dy);
            }
            else
            {
                InputRouter.OnMousePositionChange(x, y, dx, dy);
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
