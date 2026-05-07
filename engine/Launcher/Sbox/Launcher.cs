using System;
using System.Threading.Tasks;

namespace Sandbox;

public static class Launcher
{
	private static bool FileDebugLoggingEnabled =>
		string.Equals( Environment.GetEnvironmentVariable( "SBOX_LAUNCHER_DEBUG" ), "1", StringComparison.Ordinal );

	private static void WriteDebugFileLine( string message )
	{
		// Debug logging disabled
	}

	public static int Main()
	{
		Console.WriteLine( "[Launcher] Starting GameAppSystem!" );
		WriteDebugFileLine( "[Launcher] Starting GameAppSystem" );

		var appSystem = new GameAppSystem();

		Console.WriteLine( "[Launcher] Calling appSystem.Run()!" );
		WriteDebugFileLine( "[Launcher] Calling appSystem.Run()" );

		appSystem.Run();

		Console.WriteLine( "[Launcher] appSystem.Run() returned!" );
		WriteDebugFileLine( "[Launcher] appSystem.Run() returned" );

		return 0;
	}
}

public class GameAppSystem : AppSystem
{
	public override void Init()
	{
		LoadSteamDll();
		TestSystemRequirements();

		base.Init();

		CreateGame();
		CreateMenu();

		var createInfo = new AppSystemCreateInfo()
		{
			WindowTitle = "s&box",
			Flags = AppSystemFlags.IsGameApp
		};

		InitGame( createInfo );
	}
}
