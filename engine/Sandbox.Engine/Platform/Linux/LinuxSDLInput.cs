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

	/// <summary>
	/// True if the current SDL video driver is Wayland.
	/// </summary>
	public static bool IsWayland
	{
		get
		{
			var driver = NativeEngine.EngineGlobal.GetVideoDriver();
			return string.Equals( driver, "wayland", StringComparison.OrdinalIgnoreCase );
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
