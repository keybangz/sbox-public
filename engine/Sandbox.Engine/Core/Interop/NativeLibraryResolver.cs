using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Sandbox;

/// <summary>
/// Cross-platform native library resolver for sbox.
/// Handles library naming differences between Windows (.dll), Linux (.so), and macOS (.dylib),
/// including versioned libraries and different naming conventions.
/// </summary>
public static class NativeLibraryResolver
{
	private static bool _isInitialized;
	private static readonly object _initLock = new();

	/// <summary>
	/// Linux-specific dlopen/derror P/Invokes for loading libraries with RTLD_DEEPBIND.
	/// Prevents symbol collisions between bundled OpenSSL and system libraries.
	/// </summary>
	private static class LinuxDlOpen
	{
		[DllImport( "libdl.so.2", EntryPoint = "dlopen" )]
		private static extern IntPtr dlopen_raw( string filename, int flags );

		[DllImport( "libdl.so.2", EntryPoint = "dlerror" )]
		private static extern IntPtr dlerror_raw();

		private const int RTLD_NOW = 0x2;
		private const int RTLD_LOCAL = 0x0;      // Linux x86-64: LOCAL=0 (default)
		private const int RTLD_DEEPBIND = 0x8;   // Linux x86-64: DEEPBIND=8

		/// <summary>
		/// Loads a native library with RTLD_DEEPBIND to prevent symbol pollution.
		/// This ensures bundled OpenSSL symbols don't leak into the global namespace.
		/// </summary>
		public static IntPtr dlopen( string filename, int flags ) => dlopen_raw( filename, flags );

		public static IntPtr dlerror() => dlerror_raw();

		public static int GetDeepBindFlags() => RTLD_NOW | RTLD_LOCAL | RTLD_DEEPBIND;
	}

	/// <summary>
	/// Loads a library with RTLD_DEEPBIND on Linux to prevent OpenSSL symbol collisions.
	/// Falls back to NativeLibrary.TryLoad on other platforms.
	/// </summary>
	private static bool TryLoadWithDeepBind( string fullPath, out IntPtr handle )
	{
		if ( OperatingSystem.IsLinux() )
		{
			handle = LinuxDlOpen.dlopen( fullPath, LinuxDlOpen.GetDeepBindFlags() );
			if ( handle != IntPtr.Zero )
			{
				return true;
			}
			return false;
		}
		return NativeLibrary.TryLoad( fullPath, out handle );
	}

	/// <summary>
	/// Known library name mappings from Windows names to Linux names.
	/// </summary>
	private static readonly Dictionary<string, string[]> LibraryMappings = new( StringComparer.OrdinalIgnoreCase )
	{
		// Steam API - Windows uses steam_api64, Linux uses libsteam_api
		["steam_api64"] = new[] { "libsteam_api.so", "libsteam_api64.so", "steam_api64.so" },
		["steam_api64.dll"] = new[] { "libsteam_api.so", "libsteam_api64.so", "steam_api64.so" },

		// Steam API - P/Invoke uses "steam_api64" but Linux has "libsteam_api.so"
		["steam_api64"] = new[] { "libsteam_api.so" },
		["steam_api64.dll"] = new[] { "libsteam_api.so" },

		// SkiaSharp - versioned on Linux
		["libSkiaSharp"] = new[] { "libSkiaSharp.so", "libSkiaSharp.so.116.0.0" },
		["SkiaSharp"] = new[] { "libSkiaSharp.so", "libSkiaSharp.so.116.0.0" },

		// HarfBuzzSharp - versioned on Linux
		["libHarfBuzzSharp"] = new[] { "libHarfBuzzSharp.so", "libHarfBuzzSharp.so.0.60830.0", "libHarfBuzzSharp.so.0" },
		["HarfBuzzSharp"] = new[] { "libHarfBuzzSharp.so", "libHarfBuzzSharp.so.0.60830.0", "libHarfBuzzSharp.so.0" },

		// Engine libraries
		["engine2.dll"] = new[] { "libengine2.so" },
		["tier0.dll"] = new[] { "libtier0.so" },
		["filesystem_stdio.dll"] = new[] { "libfilesystem_stdio.so" },
		["materialsystem2.dll"] = new[] { "libmaterialsystem2.so" },
		["meshsystem.dll"] = new[] { "libmeshsystem.so" },
		["schemasystem.dll"] = new[] { "libschemasystem.so" },
		["animationsystem.dll"] = new[] { "libanimationsystem.so" },
		["rendersystemvulkan.dll"] = new[] { "librendersystemvulkan.so" },
		["vfx_vulkan.dll"] = new[] { "libvfx_vulkan.so" },
		["phonon.dll"] = new[] { "libphonon.so" },
		["localize.dll"] = new[] { "liblocalize.so" },
		["dxcompiler.dll"] = new[] { "libdxcompiler.so" },

		// FFmpeg libraries - versioned
		["avcodec"] = new[] { "libavcodec.so.62", "libavcodec.so" },
		["avformat"] = new[] { "libavformat.so.62", "libavformat.so" },
		["avutil"] = new[] { "libavutil.so.60", "libavutil.so" },
		["avfilter"] = new[] { "libavfilter.so.11", "libavfilter.so" },
		["swscale"] = new[] { "libswscale.so.9", "libswscale.so.9.1.100", "libswscale.so" },
		["swresample"] = new[] { "libswresample.so.6", "libswresample.so" },
	};

	/// <summary>
	/// Initialize the native library resolver. Should be called early in startup.
	/// </summary>
	public static void Initialize()
	{
		lock ( _initLock )
		{
			if ( _isInitialized ) return;

			// Register our custom resolver for the current assembly
			NativeLibrary.SetDllImportResolver( typeof( NativeLibraryResolver ).Assembly, ResolveLibrary );

			// Register for all currently loaded assemblies
			foreach ( var assembly in AppDomain.CurrentDomain.GetAssemblies() )
			{
				TryRegisterForAssembly( assembly );
			}

			// Hook into assembly load event for future assemblies
			AppDomain.CurrentDomain.AssemblyLoad += ( sender, args ) =>
			{
				TryRegisterForAssembly( args.LoadedAssembly );
			};

			_isInitialized = true;
		}
	}

	private static void TryRegisterForAssembly( Assembly assembly )
	{
		try
		{
			if ( assembly.IsDynamic ) return;
			if ( assembly.FullName?.StartsWith( "System" ) == true ) return;
			if ( assembly.FullName?.StartsWith( "Microsoft" ) == true ) return;

			NativeLibrary.SetDllImportResolver( assembly, ResolveLibrary );
		}
		catch
		{
			// Ignore - some assemblies may already have resolvers
		}
	}

	/// <summary>
	/// Register a resolver for a specific assembly.
	/// </summary>
	public static void RegisterForAssembly( Assembly assembly )
	{
		try
		{
			NativeLibrary.SetDllImportResolver( assembly, ResolveLibrary );
		}
		catch
		{
			// Ignore if already registered
		}
	}

	private static IntPtr ResolveLibrary( string libraryName, Assembly assembly, DllImportSearchPath? searchPath )
	{
		// On Windows, use default resolution
		if ( OperatingSystem.IsWindows() )
			return IntPtr.Zero;

		// Try to find using our mappings
		var candidates = GetCandidateNames( libraryName );
		var searchPaths = GetSearchPaths();

		foreach ( var path in searchPaths )
		{
			foreach ( var candidate in candidates )
			{
				var fullPath = Path.Combine( path, candidate );
				if ( TryLoadWithDeepBind( fullPath, out var handle ) )
					return handle;
			}
		}

		// Try direct load as fallback
		if ( NativeLibrary.TryLoad( libraryName, out var directHandle ) )
			return directHandle;

		return IntPtr.Zero;
	}

	private static IEnumerable<string> GetCandidateNames( string libraryName )
	{
		// Check if we have a mapping
		if ( LibraryMappings.TryGetValue( libraryName, out var mappings ) )
		{
			foreach ( var mapping in mappings )
				yield return mapping;
		}

		// Try standard conversions
		var baseName = Path.GetFileNameWithoutExtension( libraryName );

		if ( OperatingSystem.IsLinux() )
		{
			yield return $"lib{baseName}.so";
			yield return $"{baseName}.so";
			yield return libraryName;
		}
		else if ( OperatingSystem.IsMacOS() )
		{
			yield return $"lib{baseName}.dylib";
			yield return $"{baseName}.dylib";
			yield return libraryName;
		}
	}

	private static IEnumerable<string> GetSearchPaths()
	{
		// Current directory
		yield return Environment.CurrentDirectory;

		// Game bin folder based on platform
		var gameDir = Environment.CurrentDirectory;
		if ( OperatingSystem.IsLinux() )
		{
			yield return Path.Combine( gameDir, "bin", "linuxsteamrt64" );
		}
		else if ( OperatingSystem.IsMacOS() )
		{
			yield return Path.Combine( gameDir, "bin", "osx64" );
			yield return Path.Combine( gameDir, "bin", "osxarm64" );
		}
		else if ( OperatingSystem.IsWindows() )
		{
			yield return Path.Combine( gameDir, "bin", "win64" );
		}

		// Managed folder
		yield return Path.Combine( gameDir, "bin", "managed" );

		// LD_LIBRARY_PATH directories (Linux)
		if ( OperatingSystem.IsLinux() )
		{
			var ldPath = Environment.GetEnvironmentVariable( "LD_LIBRARY_PATH" );
			if ( !string.IsNullOrEmpty( ldPath ) )
			{
				foreach ( var path in ldPath.Split( ':' ) )
				{
					if ( !string.IsNullOrEmpty( path ) )
						yield return path;
				}
			}
		}
	}

	/// <summary>
	/// Convert a Windows library name to the platform-appropriate name.
	/// </summary>
	public static string ConvertLibraryName( string windowsName )
	{
		if ( OperatingSystem.IsWindows() )
			return windowsName;

		// Check mappings first
		if ( LibraryMappings.TryGetValue( windowsName, out var mappings ) && mappings.Length > 0 )
			return mappings[0];

		// Standard conversion
		var baseName = Path.GetFileNameWithoutExtension( windowsName );
		if ( OperatingSystem.IsLinux() )
			return $"lib{baseName}.so";
		if ( OperatingSystem.IsMacOS() )
			return $"lib{baseName}.dylib";

		return windowsName;
	}

	/// <summary>
	/// Try to load a library with cross-platform name resolution.
	/// </summary>
	public static bool TryLoad( string libraryName, out IntPtr handle )
	{
		handle = IntPtr.Zero;

		var candidates = GetCandidateNames( libraryName );
		var searchPaths = GetSearchPaths();

		foreach ( var path in searchPaths )
		{
			foreach ( var candidate in candidates )
			{
				var fullPath = Path.Combine( path, candidate );
				if ( TryLoadWithDeepBind( fullPath, out handle ) )
					return true;
			}
		}

		// Try direct load (fallback - keep RTLD_GLOBAL behavior here as it's last-resort)
		return NativeLibrary.TryLoad( libraryName, out handle );
	}
}

