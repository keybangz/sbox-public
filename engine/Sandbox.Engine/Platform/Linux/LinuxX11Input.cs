#if !WIN
using System.Runtime.InteropServices;
using NativeEngine;

namespace Sandbox.Engine;

/// <summary>
/// Minimal X11 keyboard and mouse poll for Linux native client.
/// Uses XQueryKeymap + XQueryPointer to read raw input state each frame,
/// then feeds deltas into InputRouter.
/// </summary>
internal static class LinuxX11Input
{
	// ── X11 P/Invoke ──────────────────────────────────────────────────────────

	[DllImport( "libX11.so.6", EntryPoint = "XOpenDisplay" )]
	private static extern IntPtr XOpenDisplay( IntPtr displayName );

	[DllImport( "libX11.so.6", EntryPoint = "XCloseDisplay" )]
	private static extern int XCloseDisplay( IntPtr display );

	[DllImport( "libX11.so.6", EntryPoint = "XQueryKeymap" )]
	private static extern int XQueryKeymap( IntPtr display, [Out] byte[] keysMap );

	[DllImport( "libX11.so.6", EntryPoint = "XQueryPointer" )]
	private static extern bool XQueryPointer(
		IntPtr display, IntPtr window,
		out IntPtr root_return, out IntPtr child_return,
		out int root_x_return, out int root_y_return,
		out int win_x_return, out int win_y_return,
		out uint mask_return );

	[DllImport( "libX11.so.6", EntryPoint = "XDefaultRootWindow" )]
	private static extern IntPtr XDefaultRootWindow( IntPtr display );

	// ── State ─────────────────────────────────────────────────────────────────

	private static IntPtr _display = IntPtr.Zero;
	private static bool _failed = false;

	private static readonly byte[] _prevKeymap = new byte[32];
	private static readonly byte[] _currKeymap = new byte[32];

	private static int _prevMouseX = -1;
	private static int _prevMouseY = -1;
	private static uint _prevMouseMask = 0;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	private static bool EnsureDisplay()
	{
		if ( _failed ) return false;
		if ( _display != IntPtr.Zero ) return true;

		try
		{
			_display = XOpenDisplay( IntPtr.Zero );
			if ( _display == IntPtr.Zero )
			{
				Log.Warning( "[LinuxX11Input] XOpenDisplay returned null — X11 input unavailable" );
				_failed = true;
				return false;
			}
			InputLog.Trace( "[LinuxX11Input] X11 display opened" );
			return true;
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[LinuxX11Input] Failed to open X11 display: {e.Message}" );
			_failed = true;
			return false;
		}
	}

	// ── Main poll ─────────────────────────────────────────────────────────────

	/// <summary>
	/// Called every frame from EngineLoop.UpdateInput().
	/// Reads X11 keyboard and mouse state, fires InputRouter events for changes.
	/// </summary>
	internal static void Poll()
	{
		if ( LinuxSDLInput.IsWayland ) return;
		if ( !LinuxSDLInput.HasX11Focus ) return;
		if ( !EnsureDisplay() ) return;

		PollKeyboard();
		PollMouse();
	}

	// ── Keyboard ──────────────────────────────────────────────────────────────

	private static void PollKeyboard()
	{
		XQueryKeymap( _display, _currKeymap );

		for ( int keycode = 8; keycode < 256; keycode++ )
		{
			bool wasDown = IsKeycodeDown( _prevKeymap, keycode );
			bool isDown  = IsKeycodeDown( _currKeymap, keycode );

			if ( isDown == wasDown ) continue;

			var button = X11KeycodeToButtonCode( keycode );
			if ( button == ButtonCode.BUTTON_CODE_INVALID ) continue;

			InputRouter.OnKey( button, button, isDown, false, 0 );
		}

		System.Array.Copy( _currKeymap, _prevKeymap, 32 );
	}

	private static bool IsKeycodeDown( byte[] map, int keycode )
	{
		return ( map[keycode >> 3] & (1 << (keycode & 7)) ) != 0;
	}

	// ── Mouse ─────────────────────────────────────────────────────────────────

	private static void PollMouse()
	{
		var root = XDefaultRootWindow( _display );
		bool ok = XQueryPointer( _display, root,
			out _, out _,
			out int rootX, out int rootY,
			out _, out _,
			out uint mask );

		if ( !ok ) return;

		// Mouse motion
		if ( _prevMouseX >= 0 )
		{
			float dx = rootX - _prevMouseX;
			float dy = rootY - _prevMouseY;

			if ( dx != 0 || dy != 0 )
			{
				InputRouter.OnMouseMotion( dx, dy );
			}
		}

		_prevMouseX = rootX;
		_prevMouseY = rootY;

		// Mouse buttons — compare mask bits
		FireMouseButton( mask, _prevMouseMask, 1 << 8,  ButtonCode.MouseLeft );
		FireMouseButton( mask, _prevMouseMask, 1 << 9,  ButtonCode.MouseMiddle );
		FireMouseButton( mask, _prevMouseMask, 1 << 10, ButtonCode.MouseRight );
		FireMouseButton( mask, _prevMouseMask, 1 << 11, ButtonCode.MouseBack );
		FireMouseButton( mask, _prevMouseMask, 1 << 12, ButtonCode.MouseForward );

		_prevMouseMask = mask;
	}

	private static void FireMouseButton( uint curr, uint prev, int bit, ButtonCode button )
	{
		bool wasDown = ( prev & (uint)bit ) != 0;
		bool isDown  = ( curr & (uint)bit ) != 0;
		if ( isDown != wasDown )
			InputRouter.OnMouseButton( button, isDown, 0 );
	}

	// ── Modifiers ─────────────────────────────────────────────────────────────

	private static KeyboardModifiers GetModifiers()
	{
		var m = KeyboardModifiers.None;
		if ( IsKeycodeDown( _currKeymap, 50 ) || IsKeycodeDown( _currKeymap, 62 ) ) m |= KeyboardModifiers.Shift;   // LShift / RShift
		if ( IsKeycodeDown( _currKeymap, 37 ) || IsKeycodeDown( _currKeymap, 105 ) ) m |= KeyboardModifiers.Ctrl;  // LCtrl / RCtrl
		if ( IsKeycodeDown( _currKeymap, 64 ) || IsKeycodeDown( _currKeymap, 108 ) ) m |= KeyboardModifiers.Alt;   // LAlt / RAlt
		return m;
	}

	// ── Keycode → ButtonCode map ───────────────────────────────────────────────
	// X11 keycodes are hardware scancodes + 8. These are standard US layout values.

	private static ButtonCode X11KeycodeToButtonCode( int kc ) => kc switch
	{
		// Numbers row
		10 => ButtonCode.KEY_1,
		11 => ButtonCode.KEY_2,
		12 => ButtonCode.KEY_3,
		13 => ButtonCode.KEY_4,
		14 => ButtonCode.KEY_5,
		15 => ButtonCode.KEY_6,
		16 => ButtonCode.KEY_7,
		17 => ButtonCode.KEY_8,
		18 => ButtonCode.KEY_9,
		19 => ButtonCode.KEY_0,
		// Top row
		24 => ButtonCode.KEY_Q,
		25 => ButtonCode.KEY_W,
		26 => ButtonCode.KEY_E,
		27 => ButtonCode.KEY_R,
		28 => ButtonCode.KEY_T,
		29 => ButtonCode.KEY_Y,
		30 => ButtonCode.KEY_U,
		31 => ButtonCode.KEY_I,
		32 => ButtonCode.KEY_O,
		33 => ButtonCode.KEY_P,
		// Home row
		38 => ButtonCode.KEY_A,
		39 => ButtonCode.KEY_S,
		40 => ButtonCode.KEY_D,
		41 => ButtonCode.KEY_F,
		42 => ButtonCode.KEY_G,
		43 => ButtonCode.KEY_H,
		44 => ButtonCode.KEY_J,
		45 => ButtonCode.KEY_K,
		46 => ButtonCode.KEY_L,
		// Bottom row
		52 => ButtonCode.KEY_Z,
		53 => ButtonCode.KEY_X,
		54 => ButtonCode.KEY_C,
		55 => ButtonCode.KEY_V,
		56 => ButtonCode.KEY_B,
		57 => ButtonCode.KEY_N,
		58 => ButtonCode.KEY_M,
		// Special keys
		9  => ButtonCode.KEY_ESCAPE,
		22 => ButtonCode.KEY_BACKSPACE,
		23 => ButtonCode.KEY_TAB,
		36 => ButtonCode.KEY_ENTER,
		65 => ButtonCode.KEY_SPACE,
		66 => ButtonCode.KEY_CAPSLOCK,
		// Modifiers
		50 => ButtonCode.KEY_LSHIFT,
		62 => ButtonCode.KEY_RSHIFT,
		37 => ButtonCode.KEY_LCONTROL,
		105 => ButtonCode.KEY_RCONTROL,
		64 => ButtonCode.KEY_LALT,
		108 => ButtonCode.KEY_RALT,
		133 => ButtonCode.KEY_LWIN,
		134 => ButtonCode.KEY_RWIN,
		// Punctuation
		20 => ButtonCode.KEY_MINUS,
		21 => ButtonCode.KEY_EQUAL,
		34 => ButtonCode.KEY_LBRACKET,
		35 => ButtonCode.KEY_RBRACKET,
		47 => ButtonCode.KEY_SEMICOLON,
		48 => ButtonCode.KEY_APOSTROPHE,
		49 => ButtonCode.KEY_BACKQUOTE,
		51 => ButtonCode.KEY_BACKSLASH,
		59 => ButtonCode.KEY_COMMA,
		60 => ButtonCode.KEY_PERIOD,
		61 => ButtonCode.KEY_SLASH,
		// Navigation
		110 => ButtonCode.KEY_HOME,
		115 => ButtonCode.KEY_END,
		112 => ButtonCode.KEY_PAGEUP,
		117 => ButtonCode.KEY_PAGEDOWN,
		118 => ButtonCode.KEY_INSERT,
		119 => ButtonCode.KEY_DELETE,
		// Arrow keys
		111 => ButtonCode.KEY_UP,
		116 => ButtonCode.KEY_DOWN,
		113 => ButtonCode.KEY_LEFT,
		114 => ButtonCode.KEY_RIGHT,
		// Function keys
		67 => ButtonCode.KEY_F1,
		68 => ButtonCode.KEY_F2,
		69 => ButtonCode.KEY_F3,
		70 => ButtonCode.KEY_F4,
		71 => ButtonCode.KEY_F5,
		72 => ButtonCode.KEY_F6,
		73 => ButtonCode.KEY_F7,
		74 => ButtonCode.KEY_F8,
		75 => ButtonCode.KEY_F9,
		76 => ButtonCode.KEY_F10,
		95 => ButtonCode.KEY_F11,
		96 => ButtonCode.KEY_F12,
		// Numpad
		77 => ButtonCode.KEY_NUMLOCK,
		78 => ButtonCode.KEY_SCROLLLOCK,
		79 => ButtonCode.KEY_PAD_7,
		80 => ButtonCode.KEY_PAD_8,
		81 => ButtonCode.KEY_PAD_9,
		82 => ButtonCode.KEY_PAD_MINUS,
		83 => ButtonCode.KEY_PAD_4,
		84 => ButtonCode.KEY_PAD_5,
		85 => ButtonCode.KEY_PAD_6,
		86 => ButtonCode.KEY_PAD_PLUS,
		87 => ButtonCode.KEY_PAD_1,
		88 => ButtonCode.KEY_PAD_2,
		89 => ButtonCode.KEY_PAD_3,
		90 => ButtonCode.KEY_PAD_0,
		91 => ButtonCode.KEY_PAD_DECIMAL,
		104 => ButtonCode.KEY_PAD_ENTER,
		106 => ButtonCode.KEY_PAD_DIVIDE,
		63 => ButtonCode.KEY_PAD_MULTIPLY,
		// Misc
		107 => ButtonCode.KEY_BREAK,
		_ => ButtonCode.BUTTON_CODE_INVALID,
	};
}
#endif
