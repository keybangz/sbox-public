using Sandbox.Engine;
using Sandbox.Tasks;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Used to create standalone tools that can still interop to the engine
/// </summary>
public class ToolAppSystem : AppSystem, IDisposable
{
	public static BaseFileSystem Content => EngineFileSystem.CoreContent;

	public void Dispose()
	{
		Shutdown();
	}

	public ToolAppSystem()
	{
		InitEnginePaths();

		Init();
	}

	public override void Init()
	{
		TestSystemRequirements();

		base.Init();

		//	CreateGame();
		//	CreateMenu();

		var createInfo = new AppSystemCreateInfo()
		{
			WindowTitle = "s&box tool",
			Flags = AppSystemFlags.IsConsoleApp | AppSystemFlags.IsEditor
		};

		InitTool( createInfo );
		AddSearchPaths( System.Environment.GetCommandLineArgs() );
	}

	protected void InitTool( AppSystemCreateInfo createInfo )
	{
		var commandLine = System.Environment.CommandLine;
		commandLine = commandLine.Replace( ".dll", ".exe" ); // uck

		// On Linux, argv[0] from Environment.CommandLine points to sbox.dll in game/,
		// but the native engine parses argv[0] to find the bin dir (bin/linuxsteamrt64).
		// Rewrite argv[0] to the absolute bin dir path so the engine can locate itself.
		var sboxBinDir = System.Environment.GetEnvironmentVariable( "SBOX_BIN_DIR" );
		if ( !string.IsNullOrEmpty( sboxBinDir ) && System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform( System.Runtime.InteropServices.OSPlatform.Linux ) )
		{
			var fakeArgv0 = System.IO.Path.Combine( sboxBinDir, "sbox.exe" );
			// commandLine starts with the original argv[0] (possibly quoted)
			// Replace just the first token (argv[0])
			var spaceIdx = commandLine.IndexOf( ' ' );
			var rest = spaceIdx >= 0 ? commandLine.Substring( spaceIdx ) : "";
			commandLine = fakeArgv0 + rest;
		}

		_appSystem = CMaterialSystem2AppSystemDict.Create( createInfo.ToMaterialSystem2AppSystemDictCreateInfo() );
		_appSystem.SetModGameSubdir( "core" );
		_appSystem.SetInToolsMode();
		_appSystem.SetSteamAppId( (uint)Application.AppId );

		//_appSystem.Init();

		if ( !NativeEngine.EngineGlobal.SourceEnginePreInit( commandLine, _appSystem ) )
		{
			throw new System.Exception( "SourceEnginePreInit failed" );
		}

		_appSystem.AddSystem( "resourcecompiler", "ResourceCompilerSystem001" );

		Bootstrap.PreInit( _appSystem );

		//Bootstrap.Init();
	}

	static void AddSearchPaths( string[] args )
	{
		var i = Array.IndexOf( args, "-searchpaths" );
		if ( i < 0 ) return;

		var paths = args[i + 1];

		foreach ( var path in paths.Split( ";" ) )
		{
			var parts = path.Split( "|" );
			EngineFileSystem.AddContentPath( parts[1] );
		}

	}

	/// <summary>
	/// We want to set current dir to /game/ 
	/// and add the native dll paths to the path
	/// </summary>
	void InitEnginePaths()
	{
		var exePath = Environment.GetCommandLineArgs()[0];
		exePath = System.IO.Path.GetDirectoryName( exePath );

		// Check for both Windows and Linux path separators
		bool isInManagedFolder = exePath.EndsWith( "bin\\managed", StringComparison.OrdinalIgnoreCase ) ||
		                         exePath.EndsWith( "bin/managed", StringComparison.OrdinalIgnoreCase );

		// we're in the managed folder, we can set this shit up
		if ( isInManagedFolder )
		{
			var dirInfo = new DirectoryInfo( exePath );

			var gameRoot = dirInfo.Parent.Parent;

			Environment.CurrentDirectory = gameRoot.FullName;

			// Use platform-appropriate paths
			bool isLinux = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
				System.Runtime.InteropServices.OSPlatform.Linux );

			string nativeDllPath;
			if ( isLinux )
			{
				nativeDllPath = System.IO.Path.Combine( gameRoot.FullName, "bin", "linuxsteamrt64" );

				// Set LD_LIBRARY_PATH for Linux
				var ldPath = System.Environment.GetEnvironmentVariable( "LD_LIBRARY_PATH" ) ?? "";
				ldPath = $"{nativeDllPath}:{ldPath}";
				System.Environment.SetEnvironmentVariable( "LD_LIBRARY_PATH", ldPath );
			}
			else
			{
				nativeDllPath = $"{gameRoot.FullName}\\bin\\win64";

				//
				// If we don't load sentry specifically from this directly, it'll
				// try to load the one from the managed folder
				//
				NativeLibrary.TryLoad( $"{nativeDllPath}\\sentry.dll", out _ );
				//NativeLibrary.TryLoad( $"{nativeDllPath}\\tier0.dll", out _ );
				//NativeLibrary.TryLoad( $"{nativeDllPath}\\engine2.dll", out _ );

				//
				// Put our native dll path first so that when looking up native dlls we'll
				// always use the ones from our folder first
				//
				var path = System.Environment.GetEnvironmentVariable( "PATH" );
				path = $"{nativeDllPath};{path}";
				System.Environment.SetEnvironmentVariable( "PATH", path );
			}

			return;
		}

		throw new Exception( "Unknown Location" );
	}
}
