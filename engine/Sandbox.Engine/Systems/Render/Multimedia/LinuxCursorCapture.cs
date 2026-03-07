using System.Runtime.InteropServices;
using NativeEngine;
using Sandbox.Engine;

namespace Sandbox.Systems.Render.Multimedia
{
#if !WIN
	/// <summary>
	/// Linux X11 input polling - directly polls X11 for mouse position and button states
	/// and forwards to InputRouter since the native engine doesn't call managed input callbacks on Linux.
	/// </summary>
	public static class LinuxSDLInput
	{
		// X11 P/Invoke declarations
		[DllImport("libX11.so.6")]
		private static extern IntPtr XOpenDisplay(string display);

		[DllImport("libX11.so.6")]
		private static extern IntPtr XGetInputFocus(IntPtr display, out IntPtr focus_return, out int revert_to_return);

		[DllImport("libX11.so.6")]
		private static extern IntPtr XDefaultRootWindow(IntPtr display);

		[DllImport("libX11.so.6")]
		private static extern int XQueryPointer(IntPtr display, IntPtr window,
			out IntPtr root_return, out IntPtr child_return,
			out int root_x_return, out int root_y_return,
			out int win_x_return, out int win_y_return,
			out uint mask_return);

		// X11 button masks
		private const uint Button1Mask = 1 << 8;  // Left mouse button
		private const uint Button2Mask = 1 << 9;  // Middle mouse button
		private const uint Button3Mask = 1 << 10; // Right mouse button

		private static IntPtr _x11Display = IntPtr.Zero;
		private static IntPtr _x11RootWindow = IntPtr.Zero;

		private static int _pollCount = 0;
		private static float _lastMouseX = 0;
		private static float _lastMouseY = 0;
		private static uint _lastButtonMask = 0;

		/// <summary>
		/// Poll X11/SDL3 for input and forward to InputRouter.
		/// Uses X11 for getting mouse position relative to focused window and button states.
		/// Should be called every frame from UpdateInput().
		/// </summary>
		public static void PollEvents()
		{
			_pollCount++;

			// Initialize X11 display on first call
			if (_x11Display == IntPtr.Zero)
			{
				_x11Display = XOpenDisplay(null);
				if (_x11Display != IntPtr.Zero)
				{
					_x11RootWindow = XDefaultRootWindow(_x11Display);
					System.IO.File.AppendAllText("/tmp/sdl3_input_debug.txt",
						$"[LinuxInput] X11 initialized: display={_x11Display} root={_x11RootWindow}\n");
				}
				else
				{
					System.IO.File.AppendAllText("/tmp/sdl3_input_debug.txt",
						$"[LinuxInput] Failed to open X11 display\n");
					return;
				}
			}

			if (_x11Display == IntPtr.Zero)
				return;

			// Get the focused window
			XGetInputFocus(_x11Display, out IntPtr focusWindow, out int revertTo);

			// If no window is focused, use root window
			if (focusWindow == IntPtr.Zero)
				focusWindow = _x11RootWindow;

			// Query pointer relative to the focused window
			int result = XQueryPointer(_x11Display, focusWindow,
				out IntPtr root, out IntPtr child,
				out int rootX, out int rootY,
				out int winX, out int winY,
				out uint mask);

			// Log first few polls for debugging
			if (_pollCount <= 5)
			{
				System.IO.File.AppendAllText("/tmp/sdl3_input_debug.txt",
					$"[LinuxInput] Poll #{_pollCount}: focusWindow={focusWindow}, winX={winX}, winY={winY}, rootX={rootX}, rootY={rootY}, mask=0x{mask:X}\n");
			}

			// Use window-relative coordinates
			float mouseX = winX;
			float mouseY = winY;

			// Send mouse position if changed
			if (mouseX != _lastMouseX || mouseY != _lastMouseY)
			{
				float dx = mouseX - _lastMouseX;
				float dy = mouseY - _lastMouseY;
				InputRouter.OnMousePositionChange(mouseX, mouseY, dx, dy);
				InputRouter.OnMouseMotion(dx, dy);
				_lastMouseX = mouseX;
				_lastMouseY = mouseY;
			}

			// Check for button state changes using X11 button masks
			CheckButtonChange(mask, Button1Mask, ButtonCode.MouseLeft);
			CheckButtonChange(mask, Button2Mask, ButtonCode.MouseMiddle);
			CheckButtonChange(mask, Button3Mask, ButtonCode.MouseRight);
			_lastButtonMask = mask;
		}

		private static void CheckButtonChange(uint currentMask, uint buttonMask, ButtonCode code)
		{
			bool wasDown = (_lastButtonMask & buttonMask) != 0;
			bool isDown = (currentMask & buttonMask) != 0;

			if (isDown != wasDown)
			{
				if (_pollCount <= 20)
				{
					System.IO.File.AppendAllText("/tmp/sdl3_input_debug.txt",
						$"[LinuxInput] Button change: {code} down={isDown}\n");
				}
				InputRouter.OnMouseButton(code, isDown, 0);
			}
		}

	}

	/// <summary>
	/// Linux cursor capture implementation using SDL3
	/// </summary>
	public static class LinuxCursorCapture
	{
		public enum SDL_CursorState : int
		{
			StateNone = 0,
			StateVisible = 1
		}

		[DllImport("libSDL3.so", CallingConvention = CallingConvention.Cdecl)]
		public static extern int SDL_GetCursorState();

		[DllImport("libSDL3.so", CallingConvention = CallingConvention.Cdecl)]
		public static extern int SDL_ShowCursor(IntPtr id, int visible);

		[DllImport("libSDL3.so", CallingConvention = CallingConvention.Cdecl)]
		public static extern int SDL_HideCursor();

		[DllImport("libSDL3.so", CallingConvention = CallingConvention.Cdecl)]
		public static extern IntPtr SDL_GetCursor();

		[DllImport("libSDL3.so", CallingConvention = CallingConvention.Cdecl)]
		public static extern IntPtr SDL_GetGlobalMouseState();

		[DllImport("libSDL3.so", CallingConvention = CallingConvention.Cdecl)]
		public static extern bool SDL_WarpMouseGlobal(int x, int y);

		public static void HideCursor()
		{
			SDL_HideCursor();
			Log.Warning("LinuxCursorCapture: HideCursor() called (stub implementation)");
		}

		public static void ShowCursor()
		{
			int visible = 1;
			SDL_ShowCursor(IntPtr.Zero, visible);
			Log.Warning("LinuxCursorCapture: ShowCursor() called (stub implementation)");
		}

		public static unsafe void BlitCursorToBuffer(byte* buffer, int width, int stride, int mouseX, int mouseY)
		{
			Log.Warning("LinuxCursorCapture: BlitCursorToBuffer() called (stub implementation)");
			return;
		}

		public static IntPtr GetCursor()
		{
			IntPtr cursor = SDL_GetCursor();
			Log.Warning("LinuxCursorCapture: GetCursor() called (stub implementation)");
			return cursor;
		}

		public static bool IsCursorVisible()
		{
			int state = SDL_GetCursorState();
			bool visible = (state == 1);
			Log.Warning($"LinuxCursorCapture: IsCursorVisible() called, state={state} (stub implementation)");
			return visible;
		}

		public static void GetCursorInfo(out bool visible, out IntPtr handle)
		{
			int state = SDL_GetCursorState();
			visible = (state == 1);
			handle = IntPtr.Zero;
			Log.Warning($"LinuxCursorCapture: GetCursorInfo() called (stub implementation), visible={visible}");
		}

		public static IntPtr GetCursorShape(out IntPtr shape, int size)
		{
			shape = IntPtr.Zero;
			Log.Warning($"LinuxCursorCapture: GetCursorShape() called (stub implementation), size={size}");
			return IntPtr.Zero;
		}

		public static bool GetCursorBitmap(out IntPtr bitmap, ref int width, ref int height)
		{
			bitmap = IntPtr.Zero;
			width = 16;
			height = 16;
			Log.Warning("LinuxCursorCapture: GetCursorBitmap() called (stub implementation)");
			return false;
		}

		public static void WarpMouse(int x, int y)
		{
			SDL_WarpMouseGlobal(x, y);
			Log.Warning($"LinuxCursorCapture: WarpMouse() called (stub implementation), x={x}, y={y}");
		}

		public static IntPtr GetGlobalMouseState(out int mouseX, out int mouseY)
		{
			mouseX = 0;
			mouseY = 0;
			IntPtr state = SDL_GetGlobalMouseState();
			Log.Warning("LinuxCursorCapture: GetGlobalMouseState() called (stub implementation)");
			return state;
		}
	}
#endif
}
