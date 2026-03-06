using System.IO;
using System.Runtime.InteropServices;

namespace NativeEngine;

/// <summary>
/// Mimmicks the engine internal CreateInterface system, allowing us to
/// get the interfaces without asking native.
/// </summary>
internal static class CreateInterface
{
	static Dictionary<string, IntPtr> loadedModules = new();

	static IntPtr LoadModule( string dll )
	{
		if ( loadedModules.TryGetValue( dll, out var module ) )
			return module;

		// Try loading directly first
		if ( NativeLibrary.TryLoad( dll, out module ) )
		{
			loadedModules[dll] = module;
			return module;
		}

		// On Linux, try with full path from game directory
		if ( OperatingSystem.IsLinux() )
		{
			var gameDir = AppDomain.CurrentDomain.BaseDirectory;
			var fullPath = Path.Combine( gameDir, dll );
			if ( NativeLibrary.TryLoad( fullPath, out module ) )
			{
				loadedModules[dll] = module;
				return module;
			}

			// Also try bin/linuxsteamrt64 subdirectory
			fullPath = Path.Combine( gameDir, "bin", "linuxsteamrt64", dll );
			if ( NativeLibrary.TryLoad( fullPath, out module ) )
			{
				loadedModules[dll] = module;
				return module;
			}
		}

		return default;
	}

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	public delegate IntPtr CreateInterfaceFn( string pName, IntPtr pReturnCode );

	public static IntPtr GetCreateInterface( string dll )
	{
		IntPtr module = LoadModule( dll );
		if ( module == IntPtr.Zero ) return default;

		return NativeLibrary.GetExport( module, "CreateInterface" );
	}

	internal static IntPtr LoadInterface( string dll, string interfacename )
	{
		var createInterface = GetCreateInterface( dll );
		if ( createInterface == IntPtr.Zero )
			return default;

		CreateInterfaceFn fn = Marshal.GetDelegateForFunctionPointer<CreateInterfaceFn>( createInterface );
		return fn( interfacename, default );
	}
}
