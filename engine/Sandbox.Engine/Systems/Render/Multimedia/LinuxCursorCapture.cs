namespace Sandbox.Systems.Render.Multimedia
{
#if !WIN
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
			Logger.Warning("LinuxCursorCapture: HideCursor() called (stub implementation)");
		}

		public static void ShowCursor()
		{
			int visible = 1;
			SDL_ShowCursor(IntPtr.Zero, visible);
			Logger.Warning("LinuxCursorCapture: ShowCursor() called (stub implementation)");
		}

		public static void BlitCursorToBuffer(byte* buffer, int width, int stride, int mouseX, int mouseY)
		{
			Logger.Warning("LinuxCursorCapture: BlitCursorToBuffer() called (stub implementation)");
			return;
		}

		public static IntPtr GetCursor()
		{
			IntPtr cursor = SDL_GetCursor();
			Logger.Warning("LinuxCursorCapture: GetCursor() called (stub implementation)");
			return cursor;
		}

		public static bool IsCursorVisible()
		{
			int state = SDL_GetCursorState();
			bool visible = (state == 1);
			Logger.Warning($"LinuxCursorCapture: IsCursorVisible() called, state={state} (stub implementation)");
			return visible;
		}

		public static void GetCursorInfo(out bool visible, out IntPtr handle)
		{
			int state = SDL_GetCursorState();
			visible = (state == 1);
			handle = IntPtr.Zero;
			Logger.Warning($"LinuxCursorCapture: GetCursorInfo() called (stub implementation), visible={visible}");
		}

		public static IntPtr GetCursorShape(out IntPtr shape, int size)
		{
			shape = IntPtr.Zero;
			Logger.Warning($"LinuxCursorCapture: GetCursorShape() called (stub implementation), size={size}");
			return IntPtr.Zero;
		}

		public static bool GetCursorBitmap(out IntPtr bitmap, ref int width, ref int height)
		{
			bitmap = IntPtr.Zero;
			width = 16;
			height = 16;
			Logger.Warning("LinuxCursorCapture: GetCursorBitmap() called (stub implementation)");
			return false;
		}

		public static void WarpMouse(int x, int y)
		{
			SDL_WarpMouseGlobal(x, y);
			Logger.Warning($"LinuxCursorCapture: WarpMouse() called (stub implementation), x={x}, y={y}");
		}

		public static IntPtr GetGlobalMouseState(out int mouseX, out int mouseY)
		{
			mouseX = 0;
			mouseY = 0;
			IntPtr state = SDL_GetGlobalMouseState();
			Logger.Warning("LinuxCursorCapture: GetGlobalMouseState() called (stub implementation)");
			return state;
		}
	}
#endif
}
