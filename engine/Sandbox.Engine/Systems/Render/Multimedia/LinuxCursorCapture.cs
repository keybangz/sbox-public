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
		/// Poll X11 for input and forward to InputRouter.
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
				}
				else
				{
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
				InputRouter.OnMouseButton(code, isDown, 0);
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
