using NativeEngine;
using Sandbox.Audio;
using Sandbox.Engine;
using Sandbox.Engine.Settings;
using Sandbox.Network;
using Sandbox.Rendering;
using Sandbox.TextureLoader;
using Sandbox.UI;
using Sandbox.Utility;
using Sandbox.VR;
using System.Threading.Channels;

namespace Sandbox;

[SkipHotload]
internal static class EngineLoop
{
	static double previousTime;

	static Superluminal _runFrame = new Superluminal( "RunFrame", "#4d5e73" );
	static Superluminal _frameStart = new Superluminal( "FrameStart", "#2c3541" );
	static Superluminal _frameEnd = new Superluminal( "FrameEnd", "#2c3541" );

	private static int _runFrameCount = 0;

	internal static void RunFrame( CMaterialSystem2AppSystemDict appDict, out bool wantsQuit )
	{
		_runFrameCount++;
		if ( _runFrameCount <= 3 )
		{
			Log.Info( $"[EngineLoop] RunFrame #{_runFrameCount} called!" );
			try
			{
				System.IO.File.AppendAllText( "/tmp/runframe_debug.txt", $"[{DateTime.Now}] RunFrame #{_runFrameCount} called\n" );
			}
			catch { }
		}

		if ( Application.WantsExit )
		{
			g_pEngineServiceMgr.ExitMainLoop();
		}

		double time = RealTime.NowDouble;
		FastTimer frameTimer = FastTimer.StartNew();

		using ( _runFrame.Start() )
		{
			RealTime.Update( time );
			Time.Update( RealTime.Now, RealTime.Delta );

			DebugOverlay.Reset();

			try
			{
				using ( _frameStart.Start() )
				{
					FrameStart();
				}
			}
			catch ( System.Exception e )
			{
				Log.Error( e );
			}

			using ( PerformanceStats.Timings.Render.Scope() )
			{
				wantsQuit = !EngineGlobal.SourceEngineFrame( appDict, time, previousTime );
			}

			try
			{
				using ( _frameEnd.Start() )
				{
					FrameEnd();
				}
				IToolsDll.Current?.RunFrame();
			}
			catch ( System.Exception e )
			{
				Log.Error( e );
			}
		}

		SleepForFrameRateClamp( frameTimer );

		previousTime = time;
	}

	static Superluminal _sleepForFrameCap = new Superluminal( "Sleep For Max FPS", Color.Gray );

	static double GetMaxFrameRate()
	{
		if ( Application.IsBenchmark ) return -1;
		if ( Application.IsHeadless ) return 60;

		int maxFps = RenderSettings.Instance.MaxFrameRate;

		if ( InputSystem.IsAppActive() ) return maxFps;

		// only use maxinactive if it's over 0 and lower than maxfps
		int maxInactive = RenderSettings.Instance.MaxFrameRateInactive;
		if ( maxInactive <= 0 ) return maxFps;
		if ( maxInactive > maxFps ) return maxFps;

		return maxInactive;
	}

	static void SleepForFrameRateClamp( FastTimer frameTime )
	{
		double maxFps = GetMaxFrameRate();
		if ( maxFps <= 0 ) return;

		using var inst = _sleepForFrameCap.Start();

		double targetMilliseconds = 1000.0 / maxFps;
		if ( targetMilliseconds > 100 ) targetMilliseconds = 100; // min is 10fps
		if ( frameTime.ElapsedMilliSeconds >= targetMilliseconds ) return; // no sleep needed

		var sleepMs = targetMilliseconds - frameTime.ElapsedMilliSeconds;

		if ( sleepMs > 1.0 )
		{
			System.Threading.Thread.Sleep( (int)sleepMs );
		}

		// sleep is inaccurate (to nearest 1ms, we call timeBeginPeriod in engine)
		// so bleed off any residual fractions of a millisecond
		while ( frameTime.ElapsedMilliSeconds < targetMilliseconds )
		{
			// wait
		}

	}

	private static int _updateInputCount = 0;

	/// <summary>
	/// Pumps the input system
	/// </summary>
	static void UpdateInput()
	{
		_updateInputCount++;
		if ( _updateInputCount <= 3 )
		{
			System.IO.File.AppendAllText( "/tmp/updateinput_debug.txt", $"[UpdateInput] #{_updateInputCount} calling g_pInputService.Pump()\n" );
		}

		using var __ = PerformanceStats.Timings.Input.Scope();

		g_pInputService.Pump();

#if !WIN
		// On Linux, the native engine doesn't call managed input callbacks,
		// so we poll SDL events directly from managed code
		if ( _updateInputCount <= 3 )
		{
			System.IO.File.AppendAllText( "/tmp/updateinput_debug.txt", $"[UpdateInput] #{_updateInputCount} About to call LinuxSDLInput.PollEvents()\n" );
		}
		try
		{
			Sandbox.Systems.Render.Multimedia.LinuxSDLInput.PollEvents();
		}
		catch ( System.Exception ex )
		{
			System.IO.File.AppendAllText( "/tmp/updateinput_debug.txt", $"[UpdateInput] LinuxSDLInput error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n" );
		}
#endif

		if ( _updateInputCount <= 3 )
		{
			System.IO.File.AppendAllText( "/tmp/updateinput_debug.txt", $"[UpdateInput] #{_updateInputCount} Pump() returned\n" );
		}
	}

	private static int _frameStartCount = 0;

	internal static void FrameStart()
	{
		_frameStartCount++;
		if ( _frameStartCount <= 3 )
		{
			System.IO.File.AppendAllText( "/tmp/framestart_debug.txt", $"[FrameStart] #{_frameStartCount} called\n" );
		}

		ThreadSafe.AssertIsMainThread();

		//
		// Let the Steam API and Steam Game Server API think
		//
		NativeEngine.Steam.SteamGameServer_RunCallbacks();
		NativeEngine.Steam.SteamAPI_RunCallbacks();

		//
		// Update performance stats (should be called every frame)
		//
		UpdatePerformance();
		DebugOverlay.Draw();
		UpdateInput();

		//
		// Dispatch callbacks for any changed files
		//
		FileWatch.Tick();

		//
		// Update any animated textures
		//
		using ( PerformanceStats.Timings.Video.Scope() )
		{
			Texture.Tick();
		}

		//
		// Update VR
		//
		VRSystem.FrameStart();

		//
		// Expire any unused resources
		//
		NativeResourceCache.Tick();
		Game.Resources.PruneWeakIndex();
		Mounting.MountUtility.TickPreviewRenders();

		//
		// Run Tasks
		//
		RunAsyncTasks();

		//
		// Let the context's tick
		//

		IMenuDll.Current?.Tick();
		IGameInstanceDll.Current?.Tick();
		IMenuDll.Current?.LateTick();
		IToolsDll.Current?.Tick();


		//
		// Run Tasks
		//
		RunAsyncTasks();

		//
		// Misc client systems
		//
		if ( !Application.IsHeadless )
		{
			using ( IGameInstanceDll.Current?.PushScope() )
			{
				VoiceManager.Tick();
				Sandbox.TextRendering.Tick();
			}
		}

		//
		// If we have any queued console messages, we can print them now
		//
		Logging.PushQueuedMessages();

		//
		// Allow the events to push if they want
		//
		Api.Events.TickEvents();
		Api.Stats.TickStats();
		Sandbox.Services.Messaging.ProcessMessages();

		// Simulate UI last. This works out all the styles and shit, so we want
		// that to be reflected right BEFORE the frame is rendered.
		using ( PerformanceStats.Timings.Ui.Scope() )
		{
			SimulateUI();
		}

		// Give each sound handle an opportunity to for a frame think
		using ( PerformanceStats.Timings.Audio.Scope() )
		{
			SoundHandle.TickAll();
			MixingThread.UpdateGlobals();
		}

		//
		// Update the mouse visibility status
		//
		if ( !Application.IsHeadless )
		{
			Engine.InputRouter.Frame();
		}

		// Keep room up to date
		PartyRoom.Current?.Tick();

		Audio.AudioEngine.Tick();
	}

	private static int _runAsyncTasksCount = 0;

	public static void RunAsyncTasks()
	{
		_runAsyncTasksCount++;
		if ( _runAsyncTasksCount <= 3 )
		{
			System.IO.File.AppendAllText( "/tmp/runasynctasks_debug.txt", $"[RunAsyncTasks] #{_runAsyncTasksCount} SyncContext.MainThread={SyncContext.MainThread != null}\n" );
		}
		using ( PerformanceStats.Timings.Async.Scope() )
		{
			using var sceneScope = IGameInstanceDll.Current?.PushScope();

			ThreadSafe.AssertIsMainThread();
			MainThread.RunQueues();
			SyncContext.MainThread?.ProcessQueue();
		}
	}

	internal static void FrameEnd()
	{
		ThreadSafe.AssertIsMainThread();

		//
		// Run Tasks
		//
		Engine.Streamer.CurrentService?.Tick();
		RunAsyncTasks();

		//
		// Update VR
		//
		VRSystem.FrameEnd();

		//
		// Free strings allocated by Interop shit, and let us know how many
		//
		int count = Interop.Free();
		if ( count > 10 )
		{
			//log.Trace( $"Interop Free: {count}" );
		}

		//
		// Run threaded stuff that needed to
		// happen on the main thread
		//
		MainThread.RunQueues();

		//
		// Trigger recompile of Project 
		//
		Project.Tick();

		//
		// Free anything that needs to be disposed of at end of frame
		// 
		while ( FrameEndDisposables.Reader.TryRead( out var disposable ) )
		{
			disposable.Dispose();
		}

		// Free render targets
		RenderTarget.EndOfFrame();
	}


	static unsafe void UpdatePerformance()
	{
		PerformanceStats.Frame();
		Api.Performance.Frame();
	}


	static Superluminal _simulateUiGame = new Superluminal( "Simulate GameUI", "#2c3541" );
	static Superluminal _simulateUiMenu = new Superluminal( "Simulate GameUI", "#2c3541" );

	private static void SimulateUI()
	{
		ThreadSafe.AssertIsMainThread();
		VideoTextureLoader.TickVideoPlayers();
		TooltipSystem.Frame();
		PanelRealTime.Update();

		using ( _simulateUiGame.Start() )
		{
			IGameInstanceDll.Current?.SimulateUI();
		}

		using ( _simulateUiMenu.Start() )
		{
			IMenuDll.Current?.SimulateUI();
		}
	}

	private static Logger nativeLogger = Logging.GetLogger( "Native" );

	static string partial = "";

	internal static void Print( int severity, string logger, string message )
	{
		partial += message;

		if ( !partial.Contains( "\n" ) )
			return;

		if ( partial.EndsWith( '\n' ) )
		{
			message = partial;
			partial = "";
		}
		else
		{
			var i = partial.LastIndexOf( '\n' );
			message = partial.Substring( 0, i );
			partial = partial.Substring( i );
		}

		message = message.TrimEnd( new[] { '\n', '\r' } );
		NLog.LogLevel level = severity switch
		{
			0 => NLog.LogLevel.Info,
			1 => NLog.LogLevel.Info,
			2 => NLog.LogLevel.Warn,
			3 => NLog.LogLevel.Warn,
			4 => NLog.LogLevel.Error,
			5 => NLog.LogLevel.Fatal,
			_ => NLog.LogLevel.Info,
		};

		var logName = $"engine/{logger}";
		nativeLogger.WriteToTargets( level, null, $"{message}", logName );
	}

	internal static void Print( bool debug, string message )
	{
		message = message.TrimEnd( new[] { '\n', '\r' } );

		if ( debug )
		{
			nativeLogger.Trace( message );
		}
		else
		{
			nativeLogger.Info( message );
		}
	}

	/// <summary>
	/// A console command has arrived, or a convar has changed
	/// </summary>
	internal static void DispatchConsoleCommand( string name, string args, long flaglong )
	{
		var convar = ConVarSystem.Find( name );
		if ( convar is null )
		{
			Log.Warning( $"Unknown Command: {name}" );
			return;
		}

		convar.Run( args );
	}

	private static int _clientOutputCount = 0;

	internal static void OnClientOutput()
	{
		_clientOutputCount++;
		if ( _clientOutputCount <= 3 )
		{
			Log.Info( $"[EngineLoop] OnClientOutput #{_clientOutputCount} called!" );
			try
			{
				System.IO.File.AppendAllText( "/tmp/onclientoutput_debug.txt", $"[{DateTime.Now}] OnClientOutput #{_clientOutputCount} called\n" );
			}
			catch { }
		}

		// The editor renders it's own game scene
		if ( Application.IsEditor )
		{
			IToolsDll.Current?.OnRender();
			return;
		}

		var engineChain = g_pEngineServiceMgr.GetEngineSwapChain();

		IGameInstanceDll.Current?.OnRender( engineChain );
		IMenuDll.Current?.OnRender( engineChain );
	}

	/// <summary>
	/// Called right at the end of a view being submitted, so everything CPU is done and it's handed off to the GPU.
	/// This is also called for any dependent views.
	/// </summary>
	internal static void OnSceneViewSubmitted( ISceneView view )
	{
		RenderPipeline.OnSceneViewSubmitted( view );
	}

	static Channel<IDisposable> FrameEndDisposables = Channel.CreateUnbounded<IDisposable>();

	/// <summary>
	/// Queue something to be disposed of after the frame has ended and everything has finished rendering.
	/// </summary>
	internal static void DisposeAtFrameEnd( IDisposable disposable ) => FrameEndDisposables.Writer.TryWrite( disposable );
}
