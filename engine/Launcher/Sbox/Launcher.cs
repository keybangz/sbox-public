using System;
using System.Threading.Tasks;

namespace Sandbox;

public static class Launcher
{
	public static int Main()
	{
		Console.WriteLine( "[Launcher] Starting GameAppSystem!" );
		System.IO.File.AppendAllText( "/tmp/launcher_debug.txt", $"[Launcher] Starting GameAppSystem\n" );

		var appSystem = new GameAppSystem();

		Console.WriteLine( "[Launcher] Calling appSystem.Run()!" );
		System.IO.File.AppendAllText( "/tmp/launcher_debug.txt", $"[Launcher] Calling appSystem.Run()\n" );

		appSystem.Run();

		Console.WriteLine( "[Launcher] appSystem.Run() returned!" );
		System.IO.File.AppendAllText( "/tmp/launcher_debug.txt", $"[Launcher] appSystem.Run() returned\n" );

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
