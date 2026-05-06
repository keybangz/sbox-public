using System;
using System.Runtime.InteropServices;

namespace Sandbox.Engine.Platform.Linux;

internal static class LinuxSDL3Native
{
	private const string LibName = "SDL3";

	// Resolver registration — handles libSDL3.so.0 vs SDL3.so naming
	static LinuxSDL3Native()
	{
		try
		{
			NativeLibrary.SetDllImportResolver( typeof( LinuxSDL3Native ).Assembly, ( name, asm, path ) =>
			{
				if ( name != LibName ) return IntPtr.Zero;

				// Try standard Linux SONAME first
				if ( NativeLibrary.TryLoad( "libSDL3.so.0", out var h ) ) return h;
				if ( NativeLibrary.TryLoad( "libSDL3.so", out h ) ) return h;
				return IntPtr.Zero;
			} );
		}
		catch ( InvalidOperationException )
		{
			// Resolver already set by DLLImportResolver — that one will fall through
			// and .NET's default search will find libSDL3.so.0 via SONAME.
		}
	}

	[DllImport( LibName, CallingConvention = CallingConvention.Cdecl )]
	public static extern IntPtr SDL_GetKeyboardFocus();

	[DllImport( LibName, CallingConvention = CallingConvention.Cdecl )]
	public static extern IntPtr SDL_GetMouseFocus();

	[DllImport( LibName, CallingConvention = CallingConvention.Cdecl )]
	[return: MarshalAs( UnmanagedType.I1 )]
	public static extern bool SDL_SetWindowRelativeMouseMode( IntPtr window, [MarshalAs( UnmanagedType.I1 )] bool enabled );

	[DllImport( LibName, CallingConvention = CallingConvention.Cdecl )]
	[return: MarshalAs( UnmanagedType.I1 )]
	public static extern bool SDL_GetWindowRelativeMouseMode( IntPtr window );

	[DllImport( LibName, CallingConvention = CallingConvention.Cdecl )]
	public static extern IntPtr SDL_GetCurrentVideoDriver();

	[DllImport( LibName, CallingConvention = CallingConvention.Cdecl )]
	public static extern IntPtr SDL_GetError();

	/// <summary>
	/// Pumps the SDL event queue, updating internal state (keyboard, mouse, etc.).
	/// Must be called before SDL_GetKeyboardState / SDL_GetMouseState to get fresh data.
	/// </summary>
	[DllImport( LibName, CallingConvention = CallingConvention.Cdecl )]
	public static extern void SDL_PumpEvents();

	/// <summary>
	/// Returns a pointer to a bool array indexed by SDL_Scancode.
	/// The array is owned by SDL — do not free it.
	/// numkeys receives the length of the array (usually 512).
	/// </summary>
	[DllImport( LibName, CallingConvention = CallingConvention.Cdecl )]
	public static extern IntPtr SDL_GetKeyboardState( out int numkeys );

	/// <summary>
	/// Returns the current mouse button mask and writes the cursor position into x/y.
	/// Bit 0 = left, bit 1 = middle, bit 2 = right, bit 3 = x1, bit 4 = x2.
	/// </summary>
	[DllImport( LibName, CallingConvention = CallingConvention.Cdecl )]
	public static extern uint SDL_GetMouseState( out float x, out float y );

	/// <summary>
	/// Returns the relative mouse motion since the last call and writes it into dx/dy.
	/// Only meaningful when relative mouse mode is active.
	/// </summary>
	[DllImport( LibName, CallingConvention = CallingConvention.Cdecl )]
	public static extern uint SDL_GetRelativeMouseState( out float dx, out float dy );
}
