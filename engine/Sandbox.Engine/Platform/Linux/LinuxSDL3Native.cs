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
}
