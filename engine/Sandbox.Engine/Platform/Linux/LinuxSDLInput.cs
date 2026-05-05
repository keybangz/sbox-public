using NativeEngine;

namespace Sandbox.Engine;

/// <summary>
/// Linux-specific SDL input helpers for X11/Wayland detection and synthetic warp filtering.
/// </summary>
internal static class LinuxSDLInput
{
	/// <summary>
	/// True when Linux X11 relative mode is active this frame.
	/// Set by LinuxCursorCapture.PollEvents() before the RelMode branch.
	/// Used by InputRouter.OnMouseMotion to suppress SDL motion double-write.
	/// </summary>
	internal static bool IsRelModeActive = false;

	/// <summary>
	/// True if the current display server is X11 AND the window has input focus.
	/// On Wayland this always returns false.
	/// </summary>
	public static bool HasX11Focus
	{
		get
		{
			// Check display server first
			if ( IsWayland ) return false;
			return g_pInputService.IsAppActive();
		}
	}

	private static string _videoDriver;
	private static bool? _isWayland;

	/// <summary>
	/// True if the current SDL video driver is Wayland.
	/// </summary>
	public static bool IsWayland
	{
		get
		{
			if ( _isWayland.HasValue ) return _isWayland.Value;

			try
			{
				var ptr = Platform.Linux.LinuxSDL3Native.SDL_GetCurrentVideoDriver();
				if ( ptr != IntPtr.Zero )
				{
					_videoDriver = System.Runtime.InteropServices.Marshal.PtrToStringUTF8( ptr );
					_isWayland = _videoDriver?.Equals( "wayland", System.StringComparison.OrdinalIgnoreCase ) == true;
					return _isWayland.Value;
				}
			}
			catch ( System.Exception e )
			{
				Log.Warning( $"[LinuxSDLInput] Failed to query SDL video driver: {e.Message}" );
			}

			// SDL not initialized yet OR P/Invoke failed — assume X11/manual path
			// Don't cache this — try again next call once SDL is up
			return false;
		}
	}

	public static bool GetRelativeMouseMode()
	{
		try
		{
			var window = Platform.Linux.LinuxSDL3Native.SDL_GetKeyboardFocus();
			if ( window == IntPtr.Zero ) window = Platform.Linux.LinuxSDL3Native.SDL_GetMouseFocus();
			if ( window == IntPtr.Zero ) return false;
			return Platform.Linux.LinuxSDL3Native.SDL_GetWindowRelativeMouseMode( window );
		}
		catch
		{
			return false;
		}
	}

	public static void SetRelativeMouseMode( bool enabled )
	{
		try
		{
			var window = Platform.Linux.LinuxSDL3Native.SDL_GetKeyboardFocus();
			if ( window == IntPtr.Zero ) window = Platform.Linux.LinuxSDL3Native.SDL_GetMouseFocus();

			if ( window == IntPtr.Zero )
			{
				InputLog.Trace( "[LinuxSDLInput] SetRelativeMouseMode: no active SDL window" );
				return;
			}

			if ( !Platform.Linux.LinuxSDL3Native.SDL_SetWindowRelativeMouseMode( window, enabled ) )
			{
				var errPtr = Platform.Linux.LinuxSDL3Native.SDL_GetError();
				var err = errPtr != IntPtr.Zero
					? System.Runtime.InteropServices.Marshal.PtrToStringUTF8( errPtr )
					: "unknown";
				Log.Warning( $"[LinuxSDLInput] SDL_SetWindowRelativeMouseMode({enabled}) failed: {err}" );
			}
			else
			{
				InputLog.Trace( $"[LinuxSDLInput] SDL relative mode -> {enabled}" );
			}
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[LinuxSDLInput] SetRelativeMouseMode threw: {e.Message}" );
		}
	}

	/// <summary>
	/// The position we last warped the cursor to. Used to filter synthetic motion events.
	/// </summary>
	private static Vector2? _pendingWarpTarget = null;
	private static readonly float WarpEpsilon = 2.0f;

	/// <summary>
	/// Register a cursor warp position. The next motion event at this position will be discarded as synthetic.
	/// </summary>
	public static void IgnoreNextWarp( Vector2 targetPos )
	{
		_pendingWarpTarget = targetPos;
	}

	/// <summary>
	/// Returns true if the given motion event position matches a pending warp target (i.e., it is synthetic).
	/// Clears the pending warp if matched.
	/// </summary>
	public static bool IsSyntheticMotion( Vector2 eventPos )
	{
		if ( _pendingWarpTarget is null ) return false;

		var diff = (eventPos - _pendingWarpTarget.Value).Length;
		if ( diff <= WarpEpsilon )
		{
			_pendingWarpTarget = null;
			return true;
		}

		return false;
	}

	/// <summary>
	/// Clear any pending warp target (e.g., on focus loss).
	/// </summary>
	public static void ClearWarpTarget()
	{
		_pendingWarpTarget = null;
	}
}
