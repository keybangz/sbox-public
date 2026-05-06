using NativeEngine;

namespace Sandbox.Engine;

/// <summary>
/// Linux-specific SDL input helpers for X11/Wayland detection and synthetic warp filtering.
/// </summary>
internal static class LinuxSDLInput
{
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
	/// The SDL video driver string (e.g. "wayland", "x11"). Empty until SDL is initialized.
	/// </summary>
	public static string VideoDriver => _videoDriver ?? string.Empty;

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
					// Cache the result — SDL is initialized, this won't change
					_isWayland = _videoDriver?.Equals( "wayland", System.StringComparison.OrdinalIgnoreCase ) == true;
					return _isWayland.Value;
				}
				// SDL not initialized yet — don't cache, try again next frame
				return false;
			}
			catch ( System.Exception e )
			{
				Log.Warning( $"[LinuxSDLInput] Failed to query SDL video driver: {e.Message}" );
				// P/Invoke failed entirely — assume X11 and cache to stop spamming
				_isWayland = false;
				return false;
			}
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
	/// The delta we expect from the synthetic warp event (warpTarget - cursorPosAtWarpTime).
	/// Used to filter out the motion event the OS generates after SetCursorPosition().
	/// </summary>
	private static Vector2? _pendingWarpDelta = null;
	private static readonly float WarpEpsilon = 2.0f;

	/// <summary>
	/// Register a cursor warp. Pass the warp target and the current cursor position so we can
	/// compute the expected synthetic delta and discard it in IsSyntheticMotion.
	/// </summary>
	public static void IgnoreNextWarp( Vector2 warpTarget, Vector2 currentPos )
	{
		_pendingWarpDelta = warpTarget - currentPos;
	}

	/// <summary>
	/// Returns true if the given motion delta matches the pending synthetic warp delta.
	/// Clears the pending warp if matched.
	/// </summary>
	public static bool IsSyntheticMotion( float dx, float dy )
	{
		if ( _pendingWarpDelta is null ) return false;

		var expected = _pendingWarpDelta.Value;
		var diff = new Vector2( dx - expected.x, dy - expected.y ).Length;
		if ( diff <= WarpEpsilon )
		{
			_pendingWarpDelta = null;
			return true;
		}

		return false;
	}

	/// <summary>
	/// Clear any pending warp target (e.g., on focus loss).
	/// </summary>
	public static void ClearWarpTarget()
	{
		_pendingWarpDelta = null;
	}
}
