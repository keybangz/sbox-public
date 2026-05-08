internal static class NetCore
{
	static string DefaultNativeDllPath => true switch
	{
		_ when OperatingSystem.IsWindows() => "bin/win64/",
		_ when OperatingSystem.IsLinux() => "bin/linuxsteamrt64/",
		_ when OperatingSystem.IsMacOS() => "bin/osxarm64/",
		_ => throw new PlatformNotSupportedException()
	};

	/// <summary>
	/// Interop will try to load dlls from this path, e.g bin/win64/
	/// </summary>
	internal static string NativeDllPath { get; set; } = DefaultNativeDllPath;

	/// <summary>
	/// From here we'll open the native dlls and inject our function pointers into them,
	/// and retrieve function pointers from them.
	/// </summary>
	internal static void InitializeInterop( string gameFolder )
	{
		// make sure currentdir to the game folder. This is just to setr a baseline for the rest
		// of the managed system to work with - since they can all assume CurrentDirectory is
		// where you would expect it to be instead of in the fucking bin folder.
		System.Environment.CurrentDirectory = gameFolder;
 
		// Resolve the default native DLL path relative to gameFolder if it hasn't been
		// set to an absolute path yet (e.g. by LauncherEnvironment.Init). This ensures
		// the linux native client can find libraries inside game/bin/linuxsteamrt64/
		// regardless of the current working directory or how the runtime was hosted.
		if ( !System.IO.Path.IsPathRooted( NativeDllPath ) )
		{
			NativeDllPath = System.IO.Path.GetFullPath( System.IO.Path.Combine( gameFolder, NativeDllPath ) );
		}
 
		// engine is always initialized
		Managed.SandboxEngine.NativeInterop.Initialize();
 
		// Initialize native crash reporting (crashpad) as early as possible.
		if ( Sandbox.Engine.ErrorReporter.IsUsingSentry )
		{
			NativeErrorReporter.Init();
		}
 
		// set engine paths etc
		var exeName = OperatingSystem.IsWindows() ? "sbox.exe" : "sbox";
 
		// On non-Windows, tell the engine the module is inside the native library
		// directory so it can resolve .so file paths relative to the actual binary
		// location (e.g. game/bin/linuxsteamrt64/) instead of the game root.
		if ( !OperatingSystem.IsWindows() && System.IO.Path.IsPathRooted( NativeDllPath ) )
		{
			NativeEngine.EngineGlobal.Plat_SetModuleFilename( System.IO.Path.Combine( NativeDllPath, exeName ) );
		}
		else
		{
			NativeEngine.EngineGlobal.Plat_SetModuleFilename( System.IO.Path.Combine( gameFolder, exeName ) );
		}
 
		// On non-Windows, point the native engine's current directory at the native
		// library path so it can find .so files loaded by its internal module system
		// (e.g. rendersystemvulkan). The managed CWD stays at gameFolder for content.
		if ( !OperatingSystem.IsWindows() && System.IO.Path.IsPathRooted( NativeDllPath ) )
		{
			NativeEngine.EngineGlobal.Plat_SetCurrentDirectory( NativeDllPath );
		}
		else
		{
			NativeEngine.EngineGlobal.Plat_SetCurrentDirectory( $"{gameFolder}" );
		}
	}
}
