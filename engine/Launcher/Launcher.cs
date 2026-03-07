using Sandbox.Diagnostics;
using System;
using System.IO;
using System.Reflection;

namespace Sandbox;

public static class Program
{
	/// <summary>
	/// The folder containing sbox.exe
	/// </summary>
	static string GamePath { get; set; }

	/// <summary>
	/// The folder containing Sandbox.Engine.dll
	/// </summary>
	static string ManagedDllPath { get; set; }

	/// <summary>
	/// The folder containing engine2.dll
	/// </summary>
	static string NativeDllPath { get; set; }

	/// <summary>
	/// Get the platform-specific bin folder name
	/// </summary>
	static string PlatformBinFolder
	{
		get
		{
			if ( OperatingSystem.IsLinux() ) return "linuxsteamrt64";
			if ( OperatingSystem.IsMacOS() ) return "osx64";
			return "win64";
		}
	}

	[STAThread]
	public static int Main()
	{
		AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

		var exePath = System.Environment.ProcessPath;
		GamePath = System.IO.Path.GetDirectoryName( exePath );
		ManagedDllPath = Path.Combine( GamePath, "bin", "managed" );
		NativeDllPath = Path.Combine( GamePath, "bin", PlatformBinFolder );

		//
		// Allows unit tests and csproj to find the engine path.
		//
		if ( System.Environment.GetEnvironmentVariable( "FACEPUNCH_ENGINE", EnvironmentVariableTarget.User ) != GamePath )
		{
			System.Environment.SetEnvironmentVariable( "FACEPUNCH_ENGINE", GamePath, EnvironmentVariableTarget.User );
		}

		//
		// Put our native dll path first so that when looking up native dlls we'll
		// always use the ones from our folder first
		//
		if ( OperatingSystem.IsWindows() )
		{
			var path = System.Environment.GetEnvironmentVariable( "PATH" );
			path = $"{NativeDllPath};{path}";
			System.Environment.SetEnvironmentVariable( "PATH", path );
		}
		else if ( OperatingSystem.IsLinux() )
		{
			var ldPath = System.Environment.GetEnvironmentVariable( "LD_LIBRARY_PATH" ) ?? "";
			ldPath = $"{NativeDllPath}:{GamePath}:{ldPath}";
			System.Environment.SetEnvironmentVariable( "LD_LIBRARY_PATH", ldPath );
		}
		else if ( OperatingSystem.IsMacOS() )
		{
			var dylibPath = System.Environment.GetEnvironmentVariable( "DYLD_LIBRARY_PATH" ) ?? "";
			dylibPath = $"{NativeDllPath}:{GamePath}:{dylibPath}";
			System.Environment.SetEnvironmentVariable( "DYLD_LIBRARY_PATH", dylibPath );
		}

		//
		// We can't call Sandbox.Engine.dll in this function because it'll try to
		// load the dll right at the start of it, before we set the paths up. So
		// instead we launch the game in this other function.
		//
		LaunchGame();

		return 0;
	}

	private static Assembly CurrentDomain_AssemblyResolve( object sender, ResolveEventArgs args )
	{
		var cd = System.Environment.CurrentDirectory;
		var trim = args.Name.Split( ',' )[0];

		var name = Path.Combine( ManagedDllPath, $"{trim}.dll" );

		// dlls with resources inside appear as a different name
		name = name.Replace( ".resources.dll", ".dll" );

		if ( System.IO.File.Exists( name ) )
		{
			return Assembly.LoadFrom( name );
		}

		return null;
	}

	static int LaunchGame()
	{
		var log = new Logger( "launcher" );

		// load the c++ dlls and fill the interop functions
		NetCore.InitializeInterop( GamePath );

		var exeName = OperatingSystem.IsWindows() ? "sbox.exe" : "sbox";
		NativeEngine.EngineGlobal.Plat_SetModuleFilename( Path.Combine( GamePath, exeName ) );

		var app = new SourceEngineApp( GamePath );

		app.RunLoop();

		return 0;
	}
}
