using System.IO;
using System.Runtime.InteropServices;
using Sandbox;

namespace NativeEngine;

/// <summary>
/// Mimmicks the engine internal CreateInterface system, allowing us to
/// get the interfaces without asking native.
/// </summary>
internal static class CreateInterface
{
	static Dictionary<string, IntPtr> loadedModules = new( StringComparer.OrdinalIgnoreCase );

	static IntPtr LoadModule( string dll )
	{
		if ( loadedModules.TryGetValue( dll, out var module ) )
			return module;

		// Convert library name for the current platform
		var platformDll = NativeLibraryResolver.ConvertLibraryName( dll );

		// Try using our cross-platform resolver first
		if ( NativeLibraryResolver.TryLoad( dll, out module ) )
		{
			loadedModules[dll] = module;
			return module;
		}

		// Try loading directly
		if ( NativeLibrary.TryLoad( platformDll, out module ) )
		{
			loadedModules[dll] = module;
			return module;
		}

		// Try with full paths based on platform
		var gameDir = AppDomain.CurrentDomain.BaseDirectory;
		string binFolder;
		if ( OperatingSystem.IsLinux() )
			binFolder = "linuxsteamrt64";
		else if ( OperatingSystem.IsMacOS() )
			binFolder = "osx64";
		else
			binFolder = "win64";

		// Try game root
		var fullPath = Path.Combine( gameDir, platformDll );
		if ( NativeLibrary.TryLoad( fullPath, out module ) )
		{
			loadedModules[dll] = module;
			return module;
		}

		// Try bin subdirectory
		fullPath = Path.Combine( gameDir, "bin", binFolder, platformDll );
		if ( NativeLibrary.TryLoad( fullPath, out module ) )
		{
			loadedModules[dll] = module;
			return module;
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
