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
	[StructLayout( LayoutKind.Sequential )]
	private struct XColor
	{
		public uint pixel;
		public ushort red, green, blue;
		public byte flags, pad;
	}

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

	[DllImport( "libX11.so.6", EntryPoint = "XGetInputFocus" )]
	private static extern void XGetInputFocus( IntPtr display, out IntPtr focus_return, out int revert_to_return );

	[DllImport( "libX11.so.6", EntryPoint = "XTranslateCoordinates" )]
	private static extern bool XTranslateCoordinates(
		IntPtr display, IntPtr src_w, IntPtr dest_w,
		int src_x, int src_y,
		out int dest_x_return, out int dest_y_return,
		out IntPtr child_return );

	[DllImport( "libX11.so.6", EntryPoint = "XCreateBitmapFromData" )]
	private static extern IntPtr XCreateBitmapFromData( IntPtr display, IntPtr drawable, byte[] data, uint width, uint height );

	[DllImport( "libX11.so.6", EntryPoint = "XCreatePixmapCursor" )]
	private static extern IntPtr XCreatePixmapCursor( IntPtr display, IntPtr source, IntPtr mask, ref XColor foreground, ref XColor background, uint x, uint y );

	[DllImport( "libX11.so.6", EntryPoint = "XFreeCursor" )]
	private static extern int XFreeCursor( IntPtr display, IntPtr cursor );

	[DllImport( "libX11.so.6", EntryPoint = "XFreePixmap" )]
	private static extern int XFreePixmap( IntPtr display, IntPtr pixmap );

	[DllImport( "libX11.so.6", EntryPoint = "XDefineCursor" )]
	private static extern int XDefineCursor( IntPtr display, IntPtr window, IntPtr cursor );

	[DllImport( "libX11.so.6", EntryPoint = "XUndefineCursor" )]
	private static extern int XUndefineCursor( IntPtr display, IntPtr window );

	[DllImport( "libX11.so.6", EntryPoint = "XGrabPointer" )]
	private static extern int XGrabPointer(
		IntPtr display, IntPtr grab_window, bool owner_events,
		uint event_mask, int pointer_mode, int keyboard_mode,
		IntPtr confine_to, IntPtr cursor, uint time );

	[DllImport( "libX11.so.6", EntryPoint = "XUngrabPointer" )]
	private static extern int XUngrabPointer( IntPtr display, uint time );

	[DllImport( "libX11.so.6", EntryPoint = "XFlush" )]
	private static extern int XFlush( IntPtr display );

	[DllImport( "libX11.so.6", EntryPoint = "XWarpPointer" )]
	private static extern int XWarpPointer( IntPtr display, IntPtr src_w, IntPtr dest_w, int src_x, int src_y, uint src_width, uint src_height, int dest_x, int dest_y );

	// ── State ─────────────────────────────────────────────────────────────────

	private static IntPtr _display = IntPtr.Zero;
	private static bool _failed = false;

	private static readonly byte[] _prevKeymap = new byte[32];
	private static readonly byte[] _currKeymap = new byte[32];

	private static int _prevMouseX = -1;
	private static int _prevMouseY = -1;
	private static uint _prevMouseMask = 0;
	private static IntPtr _focusedWindow = IntPtr.Zero;
	private static int _debugFrameCount = 0;
	private static bool _relativeMouseMode = false;
	private static IntPtr _blankCursor = IntPtr.Zero;
	private static IntPtr _gameWindow = IntPtr.Zero; // window we grabbed relative mode on
	private static bool _grabSuspended = false;      // true when grab released due to focus loss

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

	// ── Focus helper ──────────────────────────────────────────────────────────

	private static bool HasX11WindowFocus()
	{
		if ( _display == IntPtr.Zero ) return false;
		XGetInputFocus( _display, out IntPtr focus, out _ );
		if ( focus == IntPtr.Zero || focus == new IntPtr( 1 ) )
		{
			_focusedWindow = IntPtr.Zero;
			return false;
		}
		_focusedWindow = focus;
		return true;
	}

	// ── Relative mouse mode ───────────────────────────────────────────────────

	/// <summary>
	/// X11-native relative mouse mode. Hides cursor and grabs pointer to game window.
	/// Called from InputRouter.Frame() instead of LinuxSDLInput.SetRelativeMouseMode().
	/// </summary>
	internal static void SetRelativeMouseMode( bool enabled )
	{
		if ( _relativeMouseMode == enabled ) return;
		if ( !EnsureDisplay() ) return;

		XGetInputFocus( _display, out var window, out _ );
		if ( window == IntPtr.Zero || window == (IntPtr)1 ) // 1 = PointerRoot
		{
			InputLog.Trace( $"[LinuxX11Input] SetRelativeMouseMode({enabled}): no focused window" );
			return;
		}

		if ( enabled )
		{
			// Create blank cursor if needed
			if ( _blankCursor == IntPtr.Zero )
			{
				var root = XDefaultRootWindow( _display );
				var data = new byte[] { 0 };
				var pixmap = XCreateBitmapFromData( _display, root, data, 1, 1 );
				var fg = new XColor();
				var bg = new XColor();
				_blankCursor = XCreatePixmapCursor( _display, pixmap, pixmap, ref fg, ref bg, 0, 0 );
				XFreePixmap( _display, pixmap );
			}

			_gameWindow = window;
			_grabSuspended = false;

			// Hide cursor on game window
			XDefineCursor( _display, window, _blankCursor );

			// Grab pointer — confine to game window, hide cursor
			const uint PointerMotionMask = 1 << 6;
			const uint ButtonPressMask = 1 << 2;
			const uint ButtonReleaseMask = 1 << 3;
			const int GrabModeAsync = 1;
			var result = XGrabPointer(
				_display, window, true,
				PointerMotionMask | ButtonPressMask | ButtonReleaseMask,
				GrabModeAsync, GrabModeAsync,
				window, _blankCursor, 0 );

			if ( result != 0 ) // GrabSuccess = 0
			{
				InputLog.Trace( $"[LinuxX11Input] XGrabPointer failed: {result}" );
				XUndefineCursor( _display, window );
				_gameWindow = IntPtr.Zero;
			}
			else
			{
				_relativeMouseMode = true;
				InputLog.Trace( "[LinuxX11Input] Relative mouse mode ON" );
			}
		}
		else
		{
			XUngrabPointer( _display, 0 );
			if ( _gameWindow != IntPtr.Zero )
				XUndefineCursor( _display, _gameWindow );
			_relativeMouseMode = false;
			_grabSuspended = false;
			_gameWindow = IntPtr.Zero;
			InputLog.Trace( "[LinuxX11Input] Relative mouse mode OFF" );
		}

		XFlush( _display );
	}

	// ── Main poll ─────────────────────────────────────────────────────────────

	/// <summary>
	/// Called every frame from EngineLoop.UpdateInput().
	/// Reads X11 keyboard and mouse state, fires InputRouter events for changes.
	/// </summary>
	internal static void Poll()
	{
		if ( LinuxSDLInput.IsWayland ) return;
		if ( !EnsureDisplay() ) return;

		if ( _debugFrameCount % 300 == 0 )
			InputLog.Trace( $"[X11Input] Poll() alive, frame={_debugFrameCount}, wayland={LinuxSDLInput.IsWayland}" );

		if ( !HasX11WindowFocus() )
		{
			// Focus lost entirely (no window) — suspend grab
			if ( _relativeMouseMode && !_grabSuspended )
			{
				XUngrabPointer( _display, 0 );
				if ( _gameWindow != IntPtr.Zero )
					XUndefineCursor( _display, _gameWindow );
				_grabSuspended = true;
				XFlush( _display );
				InputLog.Trace( "[LinuxX11Input] Grab suspended (focus lost)" );
			}
			return;
		}

		// HasX11WindowFocus() updated _focusedWindow. Check if it's our game window.
		if ( _relativeMouseMode && _gameWindow != IntPtr.Zero && _focusedWindow != _gameWindow )
		{
			// A different window (dialog etc.) has focus — suspend grab if not already
			if ( !_grabSuspended )
			{
				XUngrabPointer( _display, 0 );
				XUndefineCursor( _display, _gameWindow );
				_grabSuspended = true;
				XFlush( _display );
				InputLog.Trace( $"[LinuxX11Input] Grab suspended (dialog/other window has focus: {_focusedWindow})" );
			}
			// Don't poll input while a foreign window has focus
			return;
		}

		// Focus is on our game window — resume grab if suspended
		if ( _relativeMouseMode && _grabSuspended && _focusedWindow == _gameWindow )
		{
			XDefineCursor( _display, _gameWindow, _blankCursor );
			const uint PointerMotionMask = 1 << 6;
			const uint ButtonPressMask = 1 << 2;
			const uint ButtonReleaseMask = 1 << 3;
			const int GrabModeAsync = 1;
			var result = XGrabPointer(
				_display, _gameWindow, true,
				PointerMotionMask | ButtonPressMask | ButtonReleaseMask,
				GrabModeAsync, GrabModeAsync,
				_gameWindow, _blankCursor, 0 );
			if ( result == 0 )
			{
				_grabSuspended = false;
				XFlush( _display );
				InputLog.Trace( "[LinuxX11Input] Grab resumed (game window refocused)" );
			}
		}

		PollKeyboard();
		PollMouse();

		_debugFrameCount++;
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

		// DEBUG: log mask every frame when non-zero, or log every 120 frames
		if ( mask != 0 || (_debugFrameCount % 120 == 0) )
		{
			InputLog.Trace( $"[X11Mouse] mask=0x{mask:X} rootX={rootX} rootY={rootY} prevMask=0x{_prevMouseMask:X}" );
		}

		// Translate root coords to window-relative coords
		int winX = rootX, winY = rootY;
		if ( _focusedWindow != IntPtr.Zero )
		{
			XTranslateCoordinates( _display, root, _focusedWindow, rootX, rootY, out winX, out winY, out _ );
		}

		// Mouse motion
		if ( _prevMouseX >= 0 )
		{
			float dx = rootX - _prevMouseX;
			float dy = rootY - _prevMouseY;

			if ( dx != 0 || dy != 0 )
			{
				if ( InputRouter._mouseCaptureMode )
				{
					InputRouter.OnMouseMotion( dx, dy );
				}
				else
				{
					InputRouter.OnMousePositionChange( winX, winY, dx, dy );
				}
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
		{
			InputLog.Trace( $"[X11Mouse] Button {button} -> {isDown} (bit=0x{bit:X} curr=0x{curr:X} prev=0x{prev:X})" );
			InputRouter.OnMouseButton( button, isDown, 0 );
		}
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

	// ── ButtonCode → bind-system string ──────────────────────────────────────
	// The native engine's InputSystem.CodeToString() uses a Windows VK string table
	// that is not populated on Linux. This dictionary provides the exact same strings
	// that the bind system (BindCollection, Input.Common) expects, so that
	// Input.OnButton() can resolve actions correctly without touching the native layer.
	// String values are case-insensitive in the bind system but we match the canonical
	// casing used in Input.Common.cs for clarity.

	private static readonly Dictionary<ButtonCode, string> _buttonCodeNames = new()
	{
		// Letters
		{ ButtonCode.KEY_A, "a" },
		{ ButtonCode.KEY_B, "b" },
		{ ButtonCode.KEY_C, "c" },
		{ ButtonCode.KEY_D, "d" },
		{ ButtonCode.KEY_E, "e" },
		{ ButtonCode.KEY_F, "f" },
		{ ButtonCode.KEY_G, "g" },
		{ ButtonCode.KEY_H, "h" },
		{ ButtonCode.KEY_I, "i" },
		{ ButtonCode.KEY_J, "j" },
		{ ButtonCode.KEY_K, "k" },
		{ ButtonCode.KEY_L, "l" },
		{ ButtonCode.KEY_M, "m" },
		{ ButtonCode.KEY_N, "n" },
		{ ButtonCode.KEY_O, "o" },
		{ ButtonCode.KEY_P, "p" },
		{ ButtonCode.KEY_Q, "q" },
		{ ButtonCode.KEY_R, "r" },
		{ ButtonCode.KEY_S, "s" },
		{ ButtonCode.KEY_T, "t" },
		{ ButtonCode.KEY_U, "u" },
		{ ButtonCode.KEY_V, "v" },
		{ ButtonCode.KEY_W, "w" },
		{ ButtonCode.KEY_X, "x" },
		{ ButtonCode.KEY_Y, "y" },
		{ ButtonCode.KEY_Z, "z" },
		// Numbers row
		{ ButtonCode.KEY_0, "0" },
		{ ButtonCode.KEY_1, "1" },
		{ ButtonCode.KEY_2, "2" },
		{ ButtonCode.KEY_3, "3" },
		{ ButtonCode.KEY_4, "4" },
		{ ButtonCode.KEY_5, "5" },
		{ ButtonCode.KEY_6, "6" },
		{ ButtonCode.KEY_7, "7" },
		{ ButtonCode.KEY_8, "8" },
		{ ButtonCode.KEY_9, "9" },
		// Special / whitespace
		{ ButtonCode.KEY_SPACE,     "space" },
		{ ButtonCode.KEY_ENTER,     "enter" },
		{ ButtonCode.KEY_TAB,       "tab" },
		{ ButtonCode.KEY_BACKSPACE, "backspace" },
		{ ButtonCode.KEY_ESCAPE,    "escape" },
		{ ButtonCode.KEY_CAPSLOCK,  "capslock" },
		// Modifiers
		{ ButtonCode.KEY_LSHIFT,    "shift" },
		{ ButtonCode.KEY_RSHIFT,    "shift" },
		{ ButtonCode.KEY_LCONTROL,  "ctrl" },
		{ ButtonCode.KEY_RCONTROL,  "ctrl" },
		{ ButtonCode.KEY_LALT,      "alt" },
		{ ButtonCode.KEY_RALT,      "alt" },
		{ ButtonCode.KEY_LWIN,      "lwin" },
		{ ButtonCode.KEY_RWIN,      "rwin" },
		// Punctuation
		{ ButtonCode.KEY_MINUS,      "-" },
		{ ButtonCode.KEY_EQUAL,      "=" },
		{ ButtonCode.KEY_LBRACKET,   "[" },
		{ ButtonCode.KEY_RBRACKET,   "]" },
		{ ButtonCode.KEY_SEMICOLON,  ";" },
		{ ButtonCode.KEY_APOSTROPHE, "'" },
		{ ButtonCode.KEY_BACKQUOTE,  "`" },
		{ ButtonCode.KEY_BACKSLASH,  "\\" },
		{ ButtonCode.KEY_COMMA,      "," },
		{ ButtonCode.KEY_PERIOD,     "." },
		{ ButtonCode.KEY_SLASH,      "/" },
		// Navigation
		{ ButtonCode.KEY_HOME,     "home" },
		{ ButtonCode.KEY_END,      "end" },
		{ ButtonCode.KEY_PAGEUP,   "pageup" },
		{ ButtonCode.KEY_PAGEDOWN, "pagedown" },
		{ ButtonCode.KEY_INSERT,   "insert" },
		{ ButtonCode.KEY_DELETE,   "delete" },
		// Arrow keys
		{ ButtonCode.KEY_UP,    "up" },
		{ ButtonCode.KEY_DOWN,  "down" },
		{ ButtonCode.KEY_LEFT,  "left" },
		{ ButtonCode.KEY_RIGHT, "right" },
		// Function keys
		{ ButtonCode.KEY_F1,  "f1" },
		{ ButtonCode.KEY_F2,  "f2" },
		{ ButtonCode.KEY_F3,  "f3" },
		{ ButtonCode.KEY_F4,  "f4" },
		{ ButtonCode.KEY_F5,  "f5" },
		{ ButtonCode.KEY_F6,  "f6" },
		{ ButtonCode.KEY_F7,  "f7" },
		{ ButtonCode.KEY_F8,  "f8" },
		{ ButtonCode.KEY_F9,  "f9" },
		{ ButtonCode.KEY_F10, "f10" },
		{ ButtonCode.KEY_F11, "f11" },
		{ ButtonCode.KEY_F12, "f12" },
		// Numpad
		{ ButtonCode.KEY_NUMLOCK,      "numlock" },
		{ ButtonCode.KEY_SCROLLLOCK,   "scrolllock" },
		{ ButtonCode.KEY_PAD_0,        "kp_ins" },
		{ ButtonCode.KEY_PAD_1,        "kp_end" },
		{ ButtonCode.KEY_PAD_2,        "kp_downarrow" },
		{ ButtonCode.KEY_PAD_3,        "kp_pgdn" },
		{ ButtonCode.KEY_PAD_4,        "kp_leftarrow" },
		{ ButtonCode.KEY_PAD_5,        "kp_5" },
		{ ButtonCode.KEY_PAD_6,        "kp_rightarrow" },
		{ ButtonCode.KEY_PAD_7,        "kp_home" },
		{ ButtonCode.KEY_PAD_8,        "kp_uparrow" },
		{ ButtonCode.KEY_PAD_9,        "kp_pgup" },
		{ ButtonCode.KEY_PAD_DECIMAL,  "kp_del" },
		{ ButtonCode.KEY_PAD_ENTER,    "kp_enter" },
		{ ButtonCode.KEY_PAD_PLUS,     "kp_plus" },
		{ ButtonCode.KEY_PAD_MINUS,    "kp_minus" },
		{ ButtonCode.KEY_PAD_MULTIPLY, "kp_multiply" },
		{ ButtonCode.KEY_PAD_DIVIDE,   "kp_slash" },
		// Mouse buttons
		{ ButtonCode.MouseLeft,    "mouse1" },
		{ ButtonCode.MouseRight,   "mouse2" },
		{ ButtonCode.MouseMiddle,  "mouse3" },
		{ ButtonCode.MouseBack,    "mouse4" },
		{ ButtonCode.MouseForward, "mouse5" },
		{ ButtonCode.MouseWheelUp,   "mwheelup" },
		{ ButtonCode.MouseWheelDown, "mwheeldown" },
		// Misc
		{ ButtonCode.KEY_BREAK, "break" },
	};

	/// <summary>
	/// Translates a <see cref="ButtonCode"/> to the canonical bind-system string name
	/// (e.g. <c>ButtonCode.KEY_W → "w"</c>, <c>ButtonCode.KEY_SPACE → "space"</c>).
	/// This is the Linux replacement for <c>NativeEngine.InputSystem.CodeToString()</c>,
	/// which uses a Windows VK string table that is not populated on Linux.
	/// Returns <see langword="null"/> if the code has no known mapping.
	/// </summary>
	internal static string ButtonCodeToName( ButtonCode code )
	{
		return _buttonCodeNames.TryGetValue( code, out var name ) ? name : null;
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
