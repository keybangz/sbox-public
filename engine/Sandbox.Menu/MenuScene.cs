namespace Sandbox;

public static class MenuScene
{
	public static Scene Scene;

	private static bool SlowPathDebugLogging => System.Environment.GetEnvironmentVariable( "SBOX_BLOCK_DEBUG" ) == "1";

	public static void Startup( string sceneName )
	{
		Log.Info( $"Loading startup scene: {sceneName}" );

		var t1 = System.Environment.TickCount64;
		Scene = new Scene();
		var t2 = System.Environment.TickCount64;
		if ( SlowPathDebugLogging && t2 - t1 > 100 )

		using ( Scene.Push() )
		{
			var t3 = System.Environment.TickCount64;
			Scene.LoadFromFile( sceneName );
			var t4 = System.Environment.TickCount64;
			if ( SlowPathDebugLogging && t4 - t3 > 100 )

			var t5 = System.Environment.TickCount64;
			LoadingScreen.IsVisible = false;
			var t6 = System.Environment.TickCount64;
			if ( SlowPathDebugLogging && t6 - t5 > 100 )
		}

	}
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
