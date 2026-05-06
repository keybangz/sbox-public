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

	// ── SDL scancode → ButtonCode map ─────────────────────────────────────────
	// SDL scancodes are USB HID scancodes. X11 keycodes = SDL scancode + 8.
	// This map is used for Wayland polling where XQueryKeymap is unavailable.

	private static readonly Dictionary<int, ButtonCode> _sdlScancodeToButtonCode = new()
	{
		// Numbers row (SDL_SCANCODE_1..0 = 30..39)
		{ 30, ButtonCode.KEY_1 }, { 31, ButtonCode.KEY_2 }, { 32, ButtonCode.KEY_3 },
		{ 33, ButtonCode.KEY_4 }, { 34, ButtonCode.KEY_5 }, { 35, ButtonCode.KEY_6 },
		{ 36, ButtonCode.KEY_7 }, { 37, ButtonCode.KEY_8 }, { 38, ButtonCode.KEY_9 },
		{ 39, ButtonCode.KEY_0 },
		// Top row (SDL_SCANCODE_Q..P = 20..25, then brackets)
		{ 20, ButtonCode.KEY_Q }, { 26, ButtonCode.KEY_W }, { 8,  ButtonCode.KEY_E },
		{ 21, ButtonCode.KEY_R }, { 23, ButtonCode.KEY_T }, { 28, ButtonCode.KEY_Y },
		{ 24, ButtonCode.KEY_U }, { 12, ButtonCode.KEY_I }, { 18, ButtonCode.KEY_O },
		{ 19, ButtonCode.KEY_P },
		// Home row
		{ 4,  ButtonCode.KEY_A }, { 22, ButtonCode.KEY_S }, { 7,  ButtonCode.KEY_D },
		{ 9,  ButtonCode.KEY_F }, { 10, ButtonCode.KEY_G }, { 11, ButtonCode.KEY_H },
		{ 13, ButtonCode.KEY_J }, { 14, ButtonCode.KEY_K }, { 15, ButtonCode.KEY_L },
		// Bottom row
		{ 29, ButtonCode.KEY_Z }, { 27, ButtonCode.KEY_X }, { 6,  ButtonCode.KEY_C },
		{ 25, ButtonCode.KEY_V }, { 5,  ButtonCode.KEY_B }, { 17, ButtonCode.KEY_N },
		{ 16, ButtonCode.KEY_M },
		// Special
		{ 41, ButtonCode.KEY_ESCAPE }, { 42, ButtonCode.KEY_BACKSPACE },
		{ 43, ButtonCode.KEY_TAB },    { 40, ButtonCode.KEY_ENTER },
		{ 44, ButtonCode.KEY_SPACE },  { 57, ButtonCode.KEY_CAPSLOCK },
		// Modifiers
		{ 225, ButtonCode.KEY_LSHIFT }, { 229, ButtonCode.KEY_RSHIFT },
		{ 224, ButtonCode.KEY_LCONTROL }, { 228, ButtonCode.KEY_RCONTROL },
		{ 226, ButtonCode.KEY_LALT }, { 230, ButtonCode.KEY_RALT },
		{ 227, ButtonCode.KEY_LWIN }, { 231, ButtonCode.KEY_RWIN },
		// Punctuation
		{ 45, ButtonCode.KEY_MINUS }, { 46, ButtonCode.KEY_EQUAL },
		{ 47, ButtonCode.KEY_LBRACKET }, { 48, ButtonCode.KEY_RBRACKET },
		{ 51, ButtonCode.KEY_SEMICOLON }, { 52, ButtonCode.KEY_APOSTROPHE },
		{ 53, ButtonCode.KEY_BACKQUOTE }, { 49, ButtonCode.KEY_BACKSLASH },
		{ 54, ButtonCode.KEY_COMMA }, { 55, ButtonCode.KEY_PERIOD },
		{ 56, ButtonCode.KEY_SLASH },
		// Navigation
		{ 74, ButtonCode.KEY_HOME }, { 77, ButtonCode.KEY_END },
		{ 75, ButtonCode.KEY_PAGEUP }, { 78, ButtonCode.KEY_PAGEDOWN },
		{ 73, ButtonCode.KEY_INSERT }, { 76, ButtonCode.KEY_DELETE },
		// Arrows
		{ 82, ButtonCode.KEY_UP }, { 81, ButtonCode.KEY_DOWN },
		{ 80, ButtonCode.KEY_LEFT }, { 79, ButtonCode.KEY_RIGHT },
		// Function keys
		{ 58, ButtonCode.KEY_F1 }, { 59, ButtonCode.KEY_F2 }, { 60, ButtonCode.KEY_F3 },
		{ 61, ButtonCode.KEY_F4 }, { 62, ButtonCode.KEY_F5 }, { 63, ButtonCode.KEY_F6 },
		{ 64, ButtonCode.KEY_F7 }, { 65, ButtonCode.KEY_F8 }, { 66, ButtonCode.KEY_F9 },
		{ 67, ButtonCode.KEY_F10 }, { 68, ButtonCode.KEY_F11 }, { 69, ButtonCode.KEY_F12 },
		// Numpad
		{ 83, ButtonCode.KEY_NUMLOCK }, { 71, ButtonCode.KEY_SCROLLLOCK },
		{ 84, ButtonCode.KEY_PAD_DIVIDE }, { 85, ButtonCode.KEY_PAD_MULTIPLY },
		{ 86, ButtonCode.KEY_PAD_MINUS }, { 87, ButtonCode.KEY_PAD_PLUS },
		{ 88, ButtonCode.KEY_PAD_ENTER },
		{ 89, ButtonCode.KEY_PAD_1 }, { 90, ButtonCode.KEY_PAD_2 }, { 91, ButtonCode.KEY_PAD_3 },
		{ 92, ButtonCode.KEY_PAD_4 }, { 93, ButtonCode.KEY_PAD_5 }, { 94, ButtonCode.KEY_PAD_6 },
		{ 95, ButtonCode.KEY_PAD_7 }, { 96, ButtonCode.KEY_PAD_8 }, { 97, ButtonCode.KEY_PAD_9 },
		{ 99, ButtonCode.KEY_PAD_DECIMAL }, { 98, ButtonCode.KEY_PAD_0 },
		// Misc
		{ 72, ButtonCode.KEY_BREAK },
	};

	// Wayland state tracking
	private static readonly Dictionary<ButtonCode, bool> _waylandPrevKeys = new();
	private static uint _waylandPrevMouseMask = 0;
	private static float _waylandPrevMouseX = -1;
	private static float _waylandPrevMouseY = -1;

	// ── Main poll ─────────────────────────────────────────────────────────────

	/// <summary>
	/// Called every frame from EngineLoop.UpdateInput().
	/// Always reads input via SDL3 (works under both native X11 and XWayland).
	/// X11 grab machinery runs in parallel when available, for cursor locking only.
	/// </summary>
	internal static void Poll()
	{
		if ( _debugFrameCount % 300 == 0 )
			InputLog.Trace( $"[X11Input] Poll() alive, frame={_debugFrameCount}, wayland={LinuxSDLInput.IsWayland}, driver={LinuxSDLInput.VideoDriver}" );

		// Always use SDL3 for input reading — works under native X11, XWayland, and Wayland.
		// XQueryKeymap / XQueryPointer are unreliable under XWayland because the compositor
		// owns focus and the X11 window may never report as focused.
		PollSDL();

		// X11 grab machinery — only for cursor locking, not input reading.
		// Skip entirely on native Wayland (SDL relative mode handles it there).
		if ( !LinuxSDLInput.IsWayland && EnsureDisplay() )
		{
			TickX11GrabMachinery();
		}

		_debugFrameCount++;
	}

	/// <summary>
	/// Manages X11 pointer grab state (cursor hiding/locking) without reading input.
	/// Only called when running under a real X11 server (not XWayland-as-Wayland).
	/// </summary>
	private static void TickX11GrabMachinery()
	{
		if ( !HasX11WindowFocus() )
		{
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

		if ( _relativeMouseMode && _gameWindow != IntPtr.Zero && _focusedWindow != _gameWindow )
		{
			if ( !_grabSuspended )
			{
				XUngrabPointer( _display, 0 );
				XUndefineCursor( _display, _gameWindow );
				_grabSuspended = true;
				XFlush( _display );
				InputLog.Trace( $"[LinuxX11Input] Grab suspended (dialog/other window has focus: {_focusedWindow})" );
			}
			return;
		}

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
	}

	// ── SDL poll (works under X11, XWayland, and native Wayland) ─────────────

	/// <summary>
	/// Polls keyboard and mouse state via SDL3 APIs.
	/// Works under native X11, XWayland, and native Wayland — SDL3 is always
	/// initialized by the engine regardless of the video driver.
	/// </summary>
	private static void PollSDL()
	{
		// Pump the SDL event queue so SDL_GetKeyboardState / SDL_GetMouseState
		// reflect the current frame's input. The engine uses SDL_AddEventWatch
		// (not SDL_PollEvent) so we must pump manually here.
		try { Platform.Linux.LinuxSDL3Native.SDL_PumpEvents(); }
		catch ( System.Exception e )
		{
			Log.Warning( $"[LinuxX11Input] SDL_PumpEvents failed: {e.Message}" );
		}

		PollWaylandKeyboard();
		PollWaylandMouse();
	}

	private static unsafe void PollWaylandKeyboard()
	{
		IntPtr statePtr;
		int numkeys;
		try
		{
			statePtr = Platform.Linux.LinuxSDL3Native.SDL_GetKeyboardState( out numkeys );
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[LinuxX11Input] SDL_GetKeyboardState failed: {e.Message}" );
			return;
		}

		if ( statePtr == IntPtr.Zero ) return;

		if ( _debugFrameCount % 300 == 0 )
			InputLog.Trace( $"[WaylandInput] PollWaylandKeyboard alive, frame={_debugFrameCount}, numkeys={numkeys}" );

		byte* state = (byte*)statePtr;

		foreach ( var (scancode, button) in _sdlScancodeToButtonCode )
		{
			if ( scancode >= numkeys ) continue;

			bool isDown = state[scancode] != 0;
			_waylandPrevKeys.TryGetValue( button, out bool wasDown );

			if ( isDown == wasDown ) continue;

			_waylandPrevKeys[button] = isDown;

			Log.Info( $"[WaylandInput] Key {button} -> {isDown} (scancode={scancode})" );
			InputRouter.OnKey( button, button, isDown, false, 0 );
		}
	}

	private static void PollWaylandMouse()
	{
		uint mask;

		if ( InputRouter._mouseCaptureMode )
		{
			// In capture/game mode: use relative state — SDL accumulates true deltas
			// since the last call, no position-diff hack needed.
			float dx, dy;
			try
			{
				mask = Platform.Linux.LinuxSDL3Native.SDL_GetRelativeMouseState( out dx, out dy );
			}
			catch ( System.Exception e )
			{
				Log.Warning( $"[LinuxX11Input] SDL_GetRelativeMouseState failed: {e.Message}" );
				return;
			}

			if ( dx != 0 || dy != 0 )
				InputRouter.OnMouseMotion( dx, dy );

			// Reset absolute tracking so we don't get a huge jump on capture release
			_waylandPrevMouseX = -1;
			_waylandPrevMouseY = -1;
		}
		else
		{
			// In UI mode: use absolute state for cursor position
			float mouseX, mouseY;
			try
			{
				mask = Platform.Linux.LinuxSDL3Native.SDL_GetMouseState( out mouseX, out mouseY );
			}
			catch ( System.Exception e )
			{
				Log.Warning( $"[LinuxX11Input] SDL_GetMouseState failed: {e.Message}" );
				return;
			}

			if ( _waylandPrevMouseX >= 0 )
			{
				float dx = mouseX - _waylandPrevMouseX;
				float dy = mouseY - _waylandPrevMouseY;

				if ( dx != 0 || dy != 0 )
					InputRouter.OnMousePositionChange( mouseX, mouseY, dx, dy );
			}

			_waylandPrevMouseX = mouseX;
			_waylandPrevMouseY = mouseY;
		}

		// SDL mouse button mask: SDL_BUTTON_LMASK=1<<0, RMASK=1<<2, MMASK=1<<1, X1=1<<3, X2=1<<4
		FireMouseButton( mask, _waylandPrevMouseMask, 1 << 0, ButtonCode.MouseLeft );
		FireMouseButton( mask, _waylandPrevMouseMask, 1 << 2, ButtonCode.MouseRight );
		FireMouseButton( mask, _waylandPrevMouseMask, 1 << 1, ButtonCode.MouseMiddle );
		FireMouseButton( mask, _waylandPrevMouseMask, 1 << 3, ButtonCode.MouseBack );
		FireMouseButton( mask, _waylandPrevMouseMask, 1 << 4, ButtonCode.MouseForward );

		_waylandPrevMouseMask = mask;
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

}
#endif
