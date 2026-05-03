using System.Runtime.InteropServices;
using NativeEngine;
using Sandbox.Engine;
using System.Text;

namespace Sandbox.Systems.Render.Multimedia
{
#if !WIN
    /// <summary>
    /// Linux SDL3 input — uses SDL_AddEventWatch to intercept events BEFORE
    /// the native engine's Pump() consumes them from the SDL event queue.
    /// </summary>
    public static class LinuxSDLInput
    {
        // SDL3 P/Invoke
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SDL_EventFilterDelegate(IntPtr userdata, IntPtr eventPtr);

        [DllImport("libSDL3.so.0", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_AddEventWatch(SDL_EventFilterDelegate filter, IntPtr userdata);

        [DllImport("libSDL3.so.0", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_RemoveEventWatch(SDL_EventFilterDelegate filter, IntPtr userdata);

        // SDL3 event type constants (verified from SDL3 source SDL_events.h)
        private const uint SDL_EVENT_KEY_DOWN              = 0x300;
        private const uint SDL_EVENT_KEY_UP                = 0x301;
        private const uint SDL_EVENT_TEXT_INPUT            = 0x303;
        private const uint SDL_EVENT_MOUSE_MOTION          = 0x400;
        private const uint SDL_EVENT_MOUSE_BUTTON_DOWN     = 0x401;
        private const uint SDL_EVENT_MOUSE_BUTTON_UP       = 0x402;
        private const uint SDL_EVENT_MOUSE_WHEEL           = 0x403;
        private const uint SDL_EVENT_WINDOW_FOCUS_GAINED   = 0x214;  // SDL3: 0x214 not 0x204
        private const uint SDL_EVENT_WINDOW_FOCUS_LOST     = 0x215;  // SDL3: 0x215 not 0x205

        // SDL3 mouse button constants
        private const byte SDL_BUTTON_LEFT   = 1;
        private const byte SDL_BUTTON_MIDDLE = 2;
        private const byte SDL_BUTTON_RIGHT  = 3;
        private const byte SDL_BUTTON_X1    = 4;
        private const byte SDL_BUTTON_X2    = 5;

        // MUST be stored in static field — GC collection of delegate causes native crash
        private static readonly SDL_EventFilterDelegate _eventWatch = EventWatchCallback;
        private static bool _initialized = false;

        // SDL3 struct layouts — all use explicit FieldOffset (verified from SDL3 headers)
        // SDL_KeyboardEvent: type(0) reserved(4) timestamp(8) windowID(16) which(20) scancode(24) key(28) mod(32) raw(34) down(36) repeat(37)
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct SDL_KeyboardEvent
        {
            [FieldOffset(0)]  public uint   type;
            [FieldOffset(4)]  public uint   reserved;
            [FieldOffset(8)]  public ulong  timestamp;
            [FieldOffset(16)] public uint   windowID;
            [FieldOffset(20)] public uint   which;
            [FieldOffset(24)] public uint   scancode;
            [FieldOffset(28)] public uint   key;
            [FieldOffset(32)] public ushort mod;
            [FieldOffset(34)] public ushort raw;
            [FieldOffset(36)] public byte   down;
            [FieldOffset(37)] public byte   repeat;
        }

        // SDL_MouseMotionEvent: type(0) reserved(4) timestamp(8) windowID(16) which(20) state(24) x(28) y(32) xrel(36) yrel(40)
        [StructLayout(LayoutKind.Explicit, Size = 48)]
        private struct SDL_MouseMotionEvent
        {
            [FieldOffset(0)]  public uint  type;
            [FieldOffset(4)]  public uint  reserved;
            [FieldOffset(8)]  public ulong timestamp;
            [FieldOffset(16)] public uint  windowID;
            [FieldOffset(20)] public uint  which;
            [FieldOffset(24)] public uint  state;
            [FieldOffset(28)] public float x;
            [FieldOffset(32)] public float y;
            [FieldOffset(36)] public float xrel;
            [FieldOffset(40)] public float yrel;
        }

        // SDL_MouseButtonEvent: type(0) reserved(4) timestamp(8) windowID(16) which(20) button(24) down(25) clicks(26) padding(27) x(28) y(32)
        [StructLayout(LayoutKind.Explicit, Size = 36)]
        private struct SDL_MouseButtonEvent
        {
            [FieldOffset(0)]  public uint  type;
            [FieldOffset(4)]  public uint  reserved;
            [FieldOffset(8)]  public ulong timestamp;
            [FieldOffset(16)] public uint  windowID;
            [FieldOffset(20)] public uint  which;
            [FieldOffset(24)] public byte  button;
            [FieldOffset(25)] public byte  down;
            [FieldOffset(26)] public byte  clicks;
            [FieldOffset(27)] public byte  padding;
            [FieldOffset(28)] public float x;
            [FieldOffset(32)] public float y;
        }

        // SDL_MouseWheelEvent: type(0) reserved(4) timestamp(8) windowID(16) which(20) x(24) y(28) direction(32) mouse_x(36) mouse_y(40) integer_x(44) integer_y(48)
        [StructLayout(LayoutKind.Explicit, Size = 52)]
        private struct SDL_MouseWheelEvent
        {
            [FieldOffset(0)]  public uint  type;
            [FieldOffset(4)]  public uint  reserved;
            [FieldOffset(8)]  public ulong timestamp;
            [FieldOffset(16)] public uint  windowID;
            [FieldOffset(20)] public uint  which;
            [FieldOffset(24)] public float x;
            [FieldOffset(28)] public float y;
            [FieldOffset(32)] public uint  direction;
            [FieldOffset(36)] public float mouse_x;
            [FieldOffset(40)] public float mouse_y;
            [FieldOffset(44)] public int   integer_x;
            [FieldOffset(48)] public int   integer_y;
        }

        // SDL_TextInputEvent: type(0) reserved(4) timestamp(8) windowID(16) [4 bytes pad] text*(24) on 64-bit
        [StructLayout(LayoutKind.Explicit, Size = 32)]
        private struct SDL_TextInputEvent
        {
            [FieldOffset(0)]  public uint   type;
            [FieldOffset(4)]  public uint   reserved;
            [FieldOffset(8)]  public ulong  timestamp;
            [FieldOffset(16)] public uint   windowID;
            // On 64-bit Linux: pointer alignment pads to offset 24
            [FieldOffset(24)] public IntPtr text;
        }

        // Scancode → ButtonCode lookup (512 entries to cover SDL3 media keys)
        private static readonly ButtonCode[] ScancodeTable = BuildScancodeTable();

        private static ButtonCode[] BuildScancodeTable()
        {
            var t = new ButtonCode[512];
            for (int i = 0; i < 512; i++) t[i] = ButtonCode.BUTTON_CODE_INVALID;

            // Letters A-Z (SDL scancode 4-29)
            t[4]=ButtonCode.KEY_A; t[5]=ButtonCode.KEY_B; t[6]=ButtonCode.KEY_C;
            t[7]=ButtonCode.KEY_D; t[8]=ButtonCode.KEY_E; t[9]=ButtonCode.KEY_F;
            t[10]=ButtonCode.KEY_G; t[11]=ButtonCode.KEY_H; t[12]=ButtonCode.KEY_I;
            t[13]=ButtonCode.KEY_J; t[14]=ButtonCode.KEY_K; t[15]=ButtonCode.KEY_L;
            t[16]=ButtonCode.KEY_M; t[17]=ButtonCode.KEY_N; t[18]=ButtonCode.KEY_O;
            t[19]=ButtonCode.KEY_P; t[20]=ButtonCode.KEY_Q; t[21]=ButtonCode.KEY_R;
            t[22]=ButtonCode.KEY_S; t[23]=ButtonCode.KEY_T; t[24]=ButtonCode.KEY_U;
            t[25]=ButtonCode.KEY_V; t[26]=ButtonCode.KEY_W; t[27]=ButtonCode.KEY_X;
            t[28]=ButtonCode.KEY_Y; t[29]=ButtonCode.KEY_Z;

            // Numbers 1-9, 0 (SDL 30-39)
            t[30]=ButtonCode.KEY_1; t[31]=ButtonCode.KEY_2; t[32]=ButtonCode.KEY_3;
            t[33]=ButtonCode.KEY_4; t[34]=ButtonCode.KEY_5; t[35]=ButtonCode.KEY_6;
            t[36]=ButtonCode.KEY_7; t[37]=ButtonCode.KEY_8; t[38]=ButtonCode.KEY_9;
            t[39]=ButtonCode.KEY_0;

            // Special keys
            t[40]=ButtonCode.KEY_ENTER;    t[41]=ButtonCode.KEY_ESCAPE;
            t[42]=ButtonCode.KEY_BACKSPACE; t[43]=ButtonCode.KEY_TAB;
            t[44]=ButtonCode.KEY_SPACE;    t[45]=ButtonCode.KEY_MINUS;
            t[46]=ButtonCode.KEY_EQUAL;    t[47]=ButtonCode.KEY_LBRACKET;
            t[48]=ButtonCode.KEY_RBRACKET; t[49]=ButtonCode.KEY_BACKSLASH;
            t[51]=ButtonCode.KEY_SEMICOLON; t[52]=ButtonCode.KEY_APOSTROPHE;
            t[53]=ButtonCode.KEY_BACKQUOTE; t[54]=ButtonCode.KEY_COMMA;
            t[55]=ButtonCode.KEY_PERIOD;   t[56]=ButtonCode.KEY_SLASH;
            t[57]=ButtonCode.KEY_CAPSLOCK;

            // F1-F12 (SDL 58-69)
            t[58]=ButtonCode.KEY_F1;  t[59]=ButtonCode.KEY_F2;  t[60]=ButtonCode.KEY_F3;
            t[61]=ButtonCode.KEY_F4;  t[62]=ButtonCode.KEY_F5;  t[63]=ButtonCode.KEY_F6;
            t[64]=ButtonCode.KEY_F7;  t[65]=ButtonCode.KEY_F8;  t[66]=ButtonCode.KEY_F9;
            t[67]=ButtonCode.KEY_F10; t[68]=ButtonCode.KEY_F11; t[69]=ButtonCode.KEY_F12;

            // PrintScreen(70), ScrollLock(71), Pause(72)
            t[70]=ButtonCode.KEY_PRINTSCREEN;
            t[71]=ButtonCode.KEY_SCROLLLOCK;
            t[72]=ButtonCode.KEY_BREAK;

            // Insert(73), Home(74), PageUp(75), Delete(76), End(77), PageDown(78)
            t[73]=ButtonCode.KEY_INSERT;  t[74]=ButtonCode.KEY_HOME;
            t[75]=ButtonCode.KEY_PAGEUP;  t[76]=ButtonCode.KEY_DELETE;
            t[77]=ButtonCode.KEY_END;     t[78]=ButtonCode.KEY_PAGEDOWN;

            // Arrow keys: Right(79), Left(80), Down(81), Up(82)
            t[79]=ButtonCode.KEY_RIGHT; t[80]=ButtonCode.KEY_LEFT;
            t[81]=ButtonCode.KEY_DOWN;  t[82]=ButtonCode.KEY_UP;

            // Numpad
            t[83]=ButtonCode.KEY_NUMLOCK;      t[84]=ButtonCode.KEY_PAD_DIVIDE;
            t[85]=ButtonCode.KEY_PAD_MULTIPLY; t[86]=ButtonCode.KEY_PAD_MINUS;
            t[87]=ButtonCode.KEY_PAD_PLUS;     t[88]=ButtonCode.KEY_PAD_ENTER;
            t[89]=ButtonCode.KEY_PAD_1; t[90]=ButtonCode.KEY_PAD_2;
            t[91]=ButtonCode.KEY_PAD_3; t[92]=ButtonCode.KEY_PAD_4;
            t[93]=ButtonCode.KEY_PAD_5; t[94]=ButtonCode.KEY_PAD_6;
            t[95]=ButtonCode.KEY_PAD_7; t[96]=ButtonCode.KEY_PAD_8;
            t[97]=ButtonCode.KEY_PAD_9; t[98]=ButtonCode.KEY_PAD_0;
            t[99]=ButtonCode.KEY_PAD_DECIMAL;

            // Application key (101)
            t[101]=ButtonCode.KEY_APP;

            // Modifier keys (224-231)
            t[224]=ButtonCode.KEY_LCONTROL; t[225]=ButtonCode.KEY_LSHIFT;
            t[226]=ButtonCode.KEY_LALT;     t[227]=ButtonCode.KEY_LWIN;
            t[228]=ButtonCode.KEY_RCONTROL; t[229]=ButtonCode.KEY_RSHIFT;
            t[230]=ButtonCode.KEY_RALT;     t[231]=ButtonCode.KEY_RWIN;

            return t;
        }

        /// <summary>
        /// Register the SDL3 event watch. Call once at startup, before any Pump() calls.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            try
            {
                SDL_AddEventWatch(_eventWatch, IntPtr.Zero);
                _initialized = true;
                Log.Info("[LinuxSDLInput] SDL3 event watch registered successfully");
            }
            catch (DllNotFoundException ex)
            {
                Log.Error($"[LinuxSDLInput] SDL3 library not found: {ex.Message}");
            }
            catch (EntryPointNotFoundException ex)
            {
                Log.Error($"[LinuxSDLInput] SDL3 entry point missing: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Error($"[LinuxSDLInput] Failed to register event watch: {ex.Message}");
            }
        }

        /// <summary>
        /// Called every frame — no-op now, events arrive via watch callback.
        /// Kept for API compatibility with EngineLoop.cs.
        /// </summary>
        public static void PollEvents()
        {
            // Events are now handled in EventWatchCallback (SDL_AddEventWatch).
            // This method intentionally left empty.
        }

        // SDL_AddEventWatch callback — fires for EVERY SDL event before queue consumption.
        // CRITICAL: May be called from a non-main thread. InputRouter calls must be safe.
        // CRITICAL: This delegate is stored in _eventWatch static field to prevent GC.
        private static void EventWatchCallback(IntPtr userdata, IntPtr eventPtr)
        {
            if (eventPtr == IntPtr.Zero) return;

            try
            {
                uint type = (uint)Marshal.ReadInt32(eventPtr);

                switch (type)
                {
                    case SDL_EVENT_KEY_DOWN:
                    case SDL_EVENT_KEY_UP:
                    {
                        var ev = Marshal.PtrToStructure<SDL_KeyboardEvent>(eventPtr);
                        uint sc = ev.scancode;
                        bool down = ev.down != 0;
                        bool repeat = ev.repeat != 0;
                        if (sc < (uint)ScancodeTable.Length)
                        {
                            var bc = ScancodeTable[sc];
                            if (bc != ButtonCode.BUTTON_CODE_INVALID)
                                InputRouter.OnKey(bc, bc, down, repeat, 0);
                        }
                        break;
                    }

                    case SDL_EVENT_TEXT_INPUT:
                    {
                        var ev = Marshal.PtrToStructure<SDL_TextInputEvent>(eventPtr);
                        if (ev.text != IntPtr.Zero)
                        {
                            string text = Marshal.PtrToStringUTF8(ev.text);
                            if (text != null)
                            {
                                // Decode each UTF-32 codepoint
                                var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
                                while (enumerator.MoveNext())
                                {
                                    string elem = enumerator.GetTextElement();
                                    int cp = char.ConvertToUtf32(elem, 0);
                                    if (cp > 0)
                                        InputRouter.OnText((uint)cp);
                                }
                            }
                        }
                        break;
                    }

                    case SDL_EVENT_MOUSE_MOTION:
                    {
                        var ev = Marshal.PtrToStructure<SDL_MouseMotionEvent>(eventPtr);
                        // Use native engine's relative mouse mode query (no global SDL3 equivalent)
                        bool relMode = NativeEngine.InputSystem.GetRelativeMouseMode();
                        if (relMode)
                            InputRouter.OnMouseMotion(ev.xrel, ev.yrel);
                        else
                            InputRouter.OnMousePositionChange(ev.x, ev.y, ev.xrel, ev.yrel);
                        break;
                    }

                    case SDL_EVENT_MOUSE_BUTTON_DOWN:
                    case SDL_EVENT_MOUSE_BUTTON_UP:
                    {
                        var ev = Marshal.PtrToStructure<SDL_MouseButtonEvent>(eventPtr);
                        bool down = ev.down != 0;
                        var bc = ev.button switch
                        {
                            SDL_BUTTON_LEFT   => ButtonCode.MouseLeft,
                            SDL_BUTTON_RIGHT  => ButtonCode.MouseRight,
                            SDL_BUTTON_MIDDLE => ButtonCode.MouseMiddle,
                            SDL_BUTTON_X1     => ButtonCode.MouseBack,
                            SDL_BUTTON_X2     => ButtonCode.MouseForward,
                            _                 => ButtonCode.BUTTON_CODE_INVALID
                        };
                        if (bc != ButtonCode.BUTTON_CODE_INVALID)
                            InputRouter.OnMouseButton(bc, down, 0);
                        break;
                    }

                    case SDL_EVENT_MOUSE_WHEEL:
                    {
                        var ev = Marshal.PtrToStructure<SDL_MouseWheelEvent>(eventPtr);
                        // Use integer_x/integer_y if available (SDL3 newer), else cast float
                        int wx = ev.integer_x != 0 ? ev.integer_x : (int)ev.x;
                        int wy = ev.integer_y != 0 ? ev.integer_y : (int)ev.y;
                        InputRouter.OnMouseWheel(wx, wy, 0);
                        break;
                    }

                    case SDL_EVENT_WINDOW_FOCUS_GAINED:
                        InputRouter.OnWindowActive(true);
                        break;

                    case SDL_EVENT_WINDOW_FOCUS_LOST:
                        InputRouter.OnWindowActive(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[LinuxSDLInput] EventWatchCallback exception: {ex.Message}");
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
