using System.Runtime.InteropServices;
using NativeEngine;
using Sandbox.Engine;
using System.Text;

namespace Sandbox.Systems.Render.Multimedia
{
#if !WIN
	/// <summary>
	/// Linux SDL3 input polling - uses SDL_PollEvent for proper input handling
	/// since the native engine doesn't call managed input callbacks on Linux.
	/// </summary>
	public static class LinuxSDLInput
	{
		// SDL3 P/Invoke declarations
		[DllImport("libSDL3.so.0", CallingConvention = CallingConvention.Cdecl)]
		private static extern int SDL_PollEvent(out SDL_Event evt);

		[DllImport("libSDL3.so.0", CallingConvention = CallingConvention.Cdecl)]
		private static extern int SDL_GetRelativeMouseMode(); // returns SDL_bool (0 or 1)

		// SDL3 event type constants
		private const uint SDL_EVENT_QUIT = 0x100;
		private const uint SDL_EVENT_KEY_DOWN = 0x300;
		private const uint SDL_EVENT_KEY_UP = 0x301;
		private const uint SDL_EVENT_TEXT_INPUT = 0x303;
		private const uint SDL_EVENT_MOUSE_MOTION = 0x400;
		private const uint SDL_EVENT_MOUSE_BUTTON_DOWN = 0x401;
		private const uint SDL_EVENT_MOUSE_BUTTON_UP = 0x402;
		private const uint SDL_EVENT_MOUSE_WHEEL = 0x403;
		private const uint SDL_EVENT_WINDOW_FOCUS_GAINED = 0x204;
		private const uint SDL_EVENT_WINDOW_FOCUS_LOST = 0x205;

		// SDL3 mouse button constants
		private const byte SDL_BUTTON_LEFT = 1;
		private const byte SDL_BUTTON_MIDDLE = 2;
		private const byte SDL_BUTTON_RIGHT = 3;
		private const byte SDL_BUTTON_X1 = 4;
		private const byte SDL_BUTTON_X2 = 5;

		// SDL3 event struct (union layout)
		[StructLayout(LayoutKind.Explicit, Size = 128)]
		private struct SDL_Event
		{
			[FieldOffset(0)] public uint type;
			[FieldOffset(0)] public SDL_KeyboardEvent key;
			[FieldOffset(0)] public SDL_MouseMotionEvent motion;
			[FieldOffset(0)] public SDL_MouseButtonEvent button;
			[FieldOffset(0)] public SDL_MouseWheelEvent wheel;
			[FieldOffset(0)] public SDL_WindowEvent window;
			[FieldOffset(0)] public SDL_TextInputEvent text;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SDL_KeyboardEvent
		{
			public uint type;
			public uint reserved;
			public ulong timestamp;
			public uint windowID;
			public uint which;
			public uint scancode;   // SDL_Scancode
			public uint key;        // SDL_Keycode
			public ushort mod;
			public ushort raw;
			public byte down;       // SDL_bool (1=down, 0=up)
			public byte repeat;     // SDL_bool
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SDL_MouseMotionEvent
		{
			public uint type;
			public uint reserved;
			public ulong timestamp;
			public uint windowID;
			public uint which;
			public uint state;
			public float x;
			public float y;
			public float xrel;
			public float yrel;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SDL_MouseButtonEvent
		{
			public uint type;
			public uint reserved;
			public ulong timestamp;
			public uint windowID;
			public uint which;
			public byte button;
			public byte down;       // SDL_bool
			public byte clicks;
			public byte padding;
			public float x;
			public float y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SDL_MouseWheelEvent
		{
			public uint type;
			public uint reserved;
			public ulong timestamp;
			public uint windowID;
			public uint which;
			public float x;
			public float y;
			public uint direction;
			public float mouse_x;
			public float mouse_y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SDL_WindowEvent
		{
			public uint type;
			public uint reserved;
			public ulong timestamp;
			public uint windowID;
			public int data1;
			public int data2;
		}

		[StructLayout(LayoutKind.Explicit, Size = 128)]
		private unsafe struct SDL_TextInputEvent
		{
			[FieldOffset(0)] public uint type;
			[FieldOffset(4)] public uint reserved;
			[FieldOffset(8)] public ulong timestamp;
			[FieldOffset(16)] public uint windowID;
			[FieldOffset(20)] public byte* text; // pointer to UTF-8 string
		}

		// Scancode to ButtonCode lookup table
		// SDL3 scancodes max is around 256, so use array for O(1) lookup
		private static readonly ButtonCode[] ScancodeToButtonCode = BuildScancodeTable();

		private static ButtonCode[] BuildScancodeTable()
		{
			var table = new ButtonCode[256];
			for (int i = 0; i < 256; i++)
				table[i] = ButtonCode.BUTTON_CODE_INVALID;

			// Letters
			table[4] = ButtonCode.KEY_A;
			table[5] = ButtonCode.KEY_B;
			table[6] = ButtonCode.KEY_C;
			table[7] = ButtonCode.KEY_D;
			table[8] = ButtonCode.KEY_E;
			table[9] = ButtonCode.KEY_F;
			table[10] = ButtonCode.KEY_G;
			table[11] = ButtonCode.KEY_H;
			table[12] = ButtonCode.KEY_I;
			table[13] = ButtonCode.KEY_J;
			table[14] = ButtonCode.KEY_K;
			table[15] = ButtonCode.KEY_L;
			table[16] = ButtonCode.KEY_M;
			table[17] = ButtonCode.KEY_N;
			table[18] = ButtonCode.KEY_O;
			table[19] = ButtonCode.KEY_P;
			table[20] = ButtonCode.KEY_Q;
			table[21] = ButtonCode.KEY_R;
			table[22] = ButtonCode.KEY_S;
			table[23] = ButtonCode.KEY_T;
			table[24] = ButtonCode.KEY_U;
			table[25] = ButtonCode.KEY_V;
			table[26] = ButtonCode.KEY_W;
			table[27] = ButtonCode.KEY_X;
			table[28] = ButtonCode.KEY_Y;
			table[29] = ButtonCode.KEY_Z;

			// Numbers
			table[30] = ButtonCode.KEY_1;
			table[31] = ButtonCode.KEY_2;
			table[32] = ButtonCode.KEY_3;
			table[33] = ButtonCode.KEY_4;
			table[34] = ButtonCode.KEY_5;
			table[35] = ButtonCode.KEY_6;
			table[36] = ButtonCode.KEY_7;
			table[37] = ButtonCode.KEY_8;
			table[38] = ButtonCode.KEY_9;
			table[39] = ButtonCode.KEY_0;

			// Special keys
			table[40] = ButtonCode.KEY_ENTER;
			table[41] = ButtonCode.KEY_ESCAPE;
			table[42] = ButtonCode.KEY_BACKSPACE;
			table[43] = ButtonCode.KEY_TAB;
			table[44] = ButtonCode.KEY_SPACE;
			table[45] = ButtonCode.KEY_MINUS;
			table[46] = ButtonCode.KEY_EQUAL;
			table[47] = ButtonCode.KEY_LBRACKET;
			table[48] = ButtonCode.KEY_RBRACKET;
			table[49] = ButtonCode.KEY_BACKSLASH;
			table[51] = ButtonCode.KEY_SEMICOLON;
			table[52] = ButtonCode.KEY_APOSTROPHE;
			table[53] = ButtonCode.KEY_BACKQUOTE;
			table[54] = ButtonCode.KEY_COMMA;
			table[55] = ButtonCode.KEY_PERIOD;
			table[56] = ButtonCode.KEY_SLASH;
			table[57] = ButtonCode.KEY_CAPSLOCK;

			// Function keys F1-F12
			table[58] = ButtonCode.KEY_F1;
			table[59] = ButtonCode.KEY_F2;
			table[60] = ButtonCode.KEY_F3;
			table[61] = ButtonCode.KEY_F4;
			table[62] = ButtonCode.KEY_F5;
			table[63] = ButtonCode.KEY_F6;
			table[64] = ButtonCode.KEY_F7;
			table[65] = ButtonCode.KEY_F8;
			table[66] = ButtonCode.KEY_F9;
			table[67] = ButtonCode.KEY_F10;
			table[68] = ButtonCode.KEY_F11;
			table[69] = ButtonCode.KEY_F12;

			// PrintScreen, ScrollLock, Pause
			table[70] = ButtonCode.KEY_PRINTSCREEN;
			table[71] = ButtonCode.KEY_SCROLLLOCK;
			table[72] = ButtonCode.KEY_BREAK; // Pause/Break

			// Insert, Home, PageUp
			table[73] = ButtonCode.KEY_INSERT;
			table[74] = ButtonCode.KEY_HOME;
			table[75] = ButtonCode.KEY_PAGEUP;

			// Delete, End, PageDown
			table[76] = ButtonCode.KEY_DELETE;
			table[77] = ButtonCode.KEY_END;
			table[78] = ButtonCode.KEY_PAGEDOWN;

			// Arrow keys
			table[79] = ButtonCode.KEY_RIGHT;
			table[80] = ButtonCode.KEY_LEFT;
			table[81] = ButtonCode.KEY_DOWN;
			table[82] = ButtonCode.KEY_UP;

			// Numpad
			table[83] = ButtonCode.KEY_NUMLOCK;
			table[84] = ButtonCode.KEY_PAD_DIVIDE;
			table[85] = ButtonCode.KEY_PAD_MULTIPLY;
			table[86] = ButtonCode.KEY_PAD_MINUS;
			table[87] = ButtonCode.KEY_PAD_PLUS;
			table[88] = ButtonCode.KEY_PAD_ENTER;
			table[89] = ButtonCode.KEY_PAD_1;
			table[90] = ButtonCode.KEY_PAD_2;
			table[91] = ButtonCode.KEY_PAD_3;
			table[92] = ButtonCode.KEY_PAD_4;
			table[93] = ButtonCode.KEY_PAD_5;
			table[94] = ButtonCode.KEY_PAD_6;
			table[95] = ButtonCode.KEY_PAD_7;
			table[96] = ButtonCode.KEY_PAD_8;
			table[97] = ButtonCode.KEY_PAD_9;
			table[98] = ButtonCode.KEY_PAD_0;
			table[99] = ButtonCode.KEY_PAD_DECIMAL;

			// Application key
			table[101] = ButtonCode.KEY_APP;

			// Left and right modifiers
			table[224] = ButtonCode.KEY_LCONTROL;
			table[225] = ButtonCode.KEY_LSHIFT;
			table[226] = ButtonCode.KEY_LALT;
			table[227] = ButtonCode.KEY_LWIN;
			table[228] = ButtonCode.KEY_RCONTROL;
			table[229] = ButtonCode.KEY_RSHIFT;
			table[230] = ButtonCode.KEY_RALT;
			table[231] = ButtonCode.KEY_RWIN;

			return table;
		}

		/// <summary>
		/// Poll SDL3 events and forward to InputRouter.
		/// Should be called every frame from UpdateInput().
		/// </summary>
		public static unsafe void PollEvents()
		{
			while (SDL_PollEvent(out SDL_Event evt) != 0)
			{
				switch (evt.type)
				{
					case SDL_EVENT_KEY_DOWN:
					case SDL_EVENT_KEY_UP:
						HandleKeyboardEvent(evt.key);
						break;

					case SDL_EVENT_TEXT_INPUT:
						HandleTextInputEvent(evt.text);
						break;

					case SDL_EVENT_MOUSE_MOTION:
						HandleMouseMotionEvent(evt.motion);
						break;

					case SDL_EVENT_MOUSE_BUTTON_DOWN:
					case SDL_EVENT_MOUSE_BUTTON_UP:
						HandleMouseButtonEvent(evt.button);
						break;

					case SDL_EVENT_MOUSE_WHEEL:
						HandleMouseWheelEvent(evt.wheel);
						break;

					case SDL_EVENT_WINDOW_FOCUS_GAINED:
						InputRouter.OnWindowActive(true);
						break;

					case SDL_EVENT_WINDOW_FOCUS_LOST:
						InputRouter.OnWindowActive(false);
						break;
				}
			}
		}

		private static void HandleKeyboardEvent(SDL_KeyboardEvent keyEvent)
		{
			uint scancode = keyEvent.scancode;
			bool down = keyEvent.down != 0;
			bool repeat = keyEvent.repeat != 0;

			ButtonCode buttonCode = ButtonCode.BUTTON_CODE_INVALID;
			if (scancode < ScancodeToButtonCode.Length)
			{
				buttonCode = ScancodeToButtonCode[scancode];
			}

			if (buttonCode != ButtonCode.BUTTON_CODE_INVALID)
			{
				// Pass ButtonCode for both scanCode and keyCode parameters
				InputRouter.OnKey(buttonCode, buttonCode, down, repeat, 0);
			}
		}

		private static unsafe void HandleTextInputEvent(SDL_TextInputEvent textEvent)
		{
			if (textEvent.text == null)
				return;

			// Decode first UTF-8 character
			byte* text = textEvent.text;
			uint codepoint = 0;

			if ((text[0] & 0x80) == 0)
			{
				// Single byte (ASCII)
				codepoint = text[0];
			}
			else if ((text[0] & 0xE0) == 0xC0)
			{
				// Two bytes
				codepoint = (uint)((text[0] & 0x1F) << 6) | (uint)(text[1] & 0x3F);
			}
			else if ((text[0] & 0xF0) == 0xE0)
			{
				// Three bytes
				codepoint = (uint)((text[0] & 0x0F) << 12) | (uint)((text[1] & 0x3F) << 6) | (uint)(text[2] & 0x3F);
			}
			else if ((text[0] & 0xF8) == 0xF0)
			{
				// Four bytes
				codepoint = (uint)((text[0] & 0x07) << 18) | (uint)((text[1] & 0x3F) << 12) | (uint)((text[2] & 0x3F) << 6) | (uint)(text[3] & 0x3F);
			}

			if (codepoint > 0)
			{
				InputRouter.OnText(codepoint);
			}
		}

		private static void HandleMouseMotionEvent(SDL_MouseMotionEvent motionEvent)
		{
			float x = motionEvent.x;
			float y = motionEvent.y;
			float xrel = motionEvent.xrel;
			float yrel = motionEvent.yrel;

			bool relativeMode = SDL_GetRelativeMouseMode() != 0;

			if (relativeMode)
			{
				// In relative mode, only send motion delta
				InputRouter.OnMouseMotion(xrel, yrel);
			}
			else
			{
				// In absolute mode, send position change
				InputRouter.OnMousePositionChange(x, y, xrel, yrel);
			}
		}

		private static void HandleMouseButtonEvent(SDL_MouseButtonEvent buttonEvent)
		{
			ButtonCode buttonCode = MapSDLButtonToButtonCode(buttonEvent.button);
			bool down = buttonEvent.down != 0;

			if (buttonCode != ButtonCode.BUTTON_CODE_INVALID)
			{
				InputRouter.OnMouseButton(buttonCode, down, 0);
			}
		}

		private static void HandleMouseWheelEvent(SDL_MouseWheelEvent wheelEvent)
		{
			int x = (int)wheelEvent.x;
			int y = (int)wheelEvent.y;

			InputRouter.OnMouseWheel(x, y, 0);
		}

		private static ButtonCode MapSDLButtonToButtonCode(byte sdlButton)
		{
			return sdlButton switch
			{
				SDL_BUTTON_LEFT => ButtonCode.MouseLeft,
				SDL_BUTTON_RIGHT => ButtonCode.MouseRight,
				SDL_BUTTON_MIDDLE => ButtonCode.MouseMiddle,
				SDL_BUTTON_X1 => ButtonCode.MouseBack,
				SDL_BUTTON_X2 => ButtonCode.MouseForward,
				_ => ButtonCode.BUTTON_CODE_INVALID
			};
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
