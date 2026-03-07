namespace Sandbox;

public static class MenuScene
{
	public static Scene Scene;

	public static void Startup( string sceneName )
	{
		Log.Info( $"Loading startup scene: {sceneName}" );

		var t1 = System.Environment.TickCount64;
		Scene = new Scene();
		var t2 = System.Environment.TickCount64;
		if ( t2 - t1 > 100 )
			System.IO.File.AppendAllText( "/tmp/block_debug.txt", $"[MENUSCENE] new Scene() took {t2 - t1}ms\n" );

		using ( Scene.Push() )
		{
			var t3 = System.Environment.TickCount64;
			Scene.LoadFromFile( sceneName );
			var t4 = System.Environment.TickCount64;
			if ( t4 - t3 > 100 )
				System.IO.File.AppendAllText( "/tmp/block_debug.txt", $"[MENUSCENE] LoadFromFile took {t4 - t3}ms\n" );

			var t5 = System.Environment.TickCount64;
			LoadingScreen.IsVisible = false;
			var t6 = System.Environment.TickCount64;
			if ( t6 - t5 > 100 )
				System.IO.File.AppendAllText( "/tmp/block_debug.txt", $"[MENUSCENE] LoadingScreen.IsVisible=false took {t6 - t5}ms\n" );
		}

		var t7 = System.Environment.TickCount64;
		System.IO.File.AppendAllText( "/tmp/block_debug.txt", $"[MENUSCENE] Total Startup took {t7 - t1}ms\n" );
	}

	/// <summary>
	/// Tick the scene. This only happens when the menu is visible
	/// </summary>
	public static void Tick()
	{
		if ( Scene is null ) return;
		if ( !Game.IsMainMenuVisible ) return;

		using ( Scene.Push() )
		{
			Scene.GameTick( RealTime.Delta );
		}
	}

	internal static void Render( SwapChainHandle_t swapChain )
	{
		if ( Scene is null ) return;
		if ( !Game.IsMainMenuVisible ) return;
		if ( Scene.IsLoading )
		{
			Scene.RenderEnvmaps();
			return;
		}

		Scene.Camera?.SceneCamera.EnableEngineOverlays = true;
		SceneCamera.RecordingCamera = Scene.Camera?.SceneCamera;

		using ( Scene.Push() )
		{
			Scene.Render( swapChain, default );
		}
	}
}
