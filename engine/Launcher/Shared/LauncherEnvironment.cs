using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Sandbox;

public static class LauncherEnvironment
{
	/// <summary>
	/// The folder containing sbox.exe
	/// </summary>
	public static string GamePath { get; set; }

	/// <summary>
	/// The folder containing Sandbox.Engine.dll
	/// </summary>
	public static string ManagedDllPath { get; set; }

	public static string PlatformName
	{
		get
		{
			if ( OperatingSystem.IsWindows() )
				return "win64";
			if ( OperatingSystem.IsLinux() )
				return "linuxsteamrt64";
			if ( OperatingSystem.IsMacOS() )
				return "osxarm64";

			throw new Exception( "Unsupported platform" );
		}
	}

	public static void Init()
	{
		AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

		GamePath = System.IO.Path.GetFullPath( AppContext.BaseDirectory );

		// this exe is in the bin folder
		if ( GamePath.EndsWith( System.IO.Path.Combine( "bin", PlatformName ) ) )
		{
			// go up two folders
			GamePath = System.IO.Path.GetDirectoryName( GamePath );
			GamePath = System.IO.Path.GetDirectoryName( GamePath );
		}

		// this exe is in the game folder
		ManagedDllPath = $"{GamePath}/bin/managed/";
		var nativeDllPath = $"{GamePath}/bin/{PlatformName}/";

		// make the game dir our current dir
		Environment.CurrentDirectory = GamePath;

		//
		// Allows unit tests and csproj to find the engine path.
		//
		if ( System.Environment.GetEnvironmentVariable( "FACEPUNCH_ENGINE", EnvironmentVariableTarget.User ) != GamePath )
		{
			System.Environment.SetEnvironmentVariable( "FACEPUNCH_ENGINE", GamePath, EnvironmentVariableTarget.User );
		}

		// Set SBOX_BIN_DIR so AppSystem.InitGame() can rewrite argv[0] for the native engine.
		// This is needed when running ./sbox directly (without run.sh which sets it externally).
		if ( string.IsNullOrEmpty( System.Environment.GetEnvironmentVariable( "SBOX_BIN_DIR" ) ) )
		{
			System.Environment.SetEnvironmentVariable( "SBOX_BIN_DIR", nativeDllPath );
		}

		// Force SDL3 to use X11 video driver on Linux.
		// Wayland init fails silently causing the render system to exit immediately after
		// Vulkan device init. X11 (via XWayland) keeps the engine alive.
		// Must be set before SourceEnginePreInit loads SDL3.
		if ( OperatingSystem.IsLinux() &&
		     string.IsNullOrEmpty( System.Environment.GetEnvironmentVariable( "SDL_VIDEODRIVER" ) ) )
		{
			System.Environment.SetEnvironmentVariable( "SDL_VIDEODRIVER", "x11" );
		}

		UpdateNativeDllPath( nativeDllPath );
	}

	private static void UpdateNativeDllPath( string nativeDllPath )
	{
		// WARNING: this calls into Sandbox.Engine.dll - so we need to put it in
		// this method, which is executed AFTER CurrentDomain_AssemblyResolve is set
		// so that managed can find the correct dll
		NetCore.NativeDllPath = nativeDllPath;

		//
		// Put our native dll path first so that when looking up native dlls we'll
		// always use the ones from our folder first
		//
		if ( OperatingSystem.IsWindows() )
		{
			var path = System.Environment.GetEnvironmentVariable( "PATH" );
			path = $"{nativeDllPath};{path}";
			System.Environment.SetEnvironmentVariable( "PATH", path );
		}
		else if ( OperatingSystem.IsLinux() )
		{
			var ldPath = System.Environment.GetEnvironmentVariable( "LD_LIBRARY_PATH" ) ?? "";
			ldPath = $"{nativeDllPath}:{GamePath}:{ldPath}";
			System.Environment.SetEnvironmentVariable( "LD_LIBRARY_PATH", ldPath );
		}
		else if ( OperatingSystem.IsMacOS() )
		{
			var dylibPath = System.Environment.GetEnvironmentVariable( "DYLD_LIBRARY_PATH" ) ?? "";
			dylibPath = $"{nativeDllPath}:{GamePath}:{dylibPath}";
			System.Environment.SetEnvironmentVariable( "DYLD_LIBRARY_PATH", dylibPath );
		}
	}

	private static Assembly CurrentDomain_AssemblyResolve( object sender, ResolveEventArgs args )
	{
		var trim = args.Name.Split( ',' )[0];

		var name = $"{ManagedDllPath}/{trim}.dll";

		// dlls with resources inside appear as a different name
		name = name.Replace( ".resources.dll", ".dll" );

		if ( System.IO.File.Exists( name ) )
		{
			return Assembly.LoadFrom( name );
		}

		return null;
	}
}
