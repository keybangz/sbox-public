using NativeEngine;
using Sandbox.Engine.Settings;
using Sandbox.Network;
using Sandbox.Utility;
using Sandbox.VR;
using Sentry;
using Steamworks;
using System.Diagnostics;
using System.Threading;

namespace Sandbox.Engine;

[SkipHotload]
internal static class Bootstrap
{
	/// <summary>
	/// The github SHA of the current build
	/// </summary>
	internal static string VersionSha { get; private set; }

	private static readonly Logger log = Logging.GetLogger();

	internal static Api.Events.EventRecord StartupTiming;

	/// <summary>
	/// Called before anything else. This should set up any low level stuff that
	/// might be relied on if static functions are called.
	/// </summary>
	internal static void PreInit( CMaterialSystem2AppSystemDict appDict )
	{
		Application.Initialize( appDict.IsDedicatedServer(), appDict.IsConsoleApp(), appDict.IsInToolsMode(), appDict.IsInTestMode(), EngineGlobal.IsRetail() );

		try
		{
			InitMinimal( EngineGlobal.GetGameRootFolder() );

			DLLImportResolver.SetupResolvers();

			StartupTiming = new Api.Events.EventRecord( $"StartupTiming.{(Application.IsEditor ? "Editor" : (Application.IsHeadless ? "Server" : "Game"))}" );
			StartupTiming.StartTimer( "Time" );

			// This also calls SetSynchronizationContext
			SyncContext.Init();

			if ( Thread.CurrentThread.CurrentCulture.Name != "en-US" )
			{
				var culture = System.Globalization.CultureInfo.CreateSpecificCulture( "en-US" );
				Thread.CurrentThread.CurrentCulture = culture;
				Thread.CurrentThread.CurrentUICulture = culture;
			}

			Logging.Enabled = true;
			Logging.OnException = ErrorReporter.ReportException;
			Logging.PrintToConsole = Application.IsHeadless;

			{
				using var timerFs = StartupTiming?.ScopeTimer( "FilesystemInit" );

				EngineFileSystem.InitializeAddonsFolder();
				EngineFileSystem.InitializeDataFolder();

				if ( !Application.IsStandalone )
				{
					EngineFileSystem.InitializeDownloadsFolder();

					string assetdownloadFolder = "/assets";
					EngineFileSystem.DownloadedFiles.CreateDirectory( assetdownloadFolder );

					AssetDownloadCache.Initialize( EngineFileSystem.DownloadedFiles.GetFullPath( assetdownloadFolder ) );
				}
			}

			Api.Init();

			if ( Application.IsStandalone )
			{
				IGameInstanceDll.Current?.Bootstrap();
			}
			else
			{
				using ( var _ = StartupTiming?.ScopeTimer( "Menu PreInit Bootstrap" ) )
				{
					IMenuDll.Current?.Bootstrap();
				}
				using ( var _ = StartupTiming?.ScopeTimer( "Game PreInit Bootstrap" ) )
				{
					IGameInstanceDll.Current?.Bootstrap();
				}
				using ( var _ = StartupTiming?.ScopeTimer( "Tools PreInit Bootstrap" ) )
				{
					IToolsDll.Current?.Bootstrap();
				}

				Mounting.Directory.LoadAssemblies();
			}
		}
		catch ( Exception ex )
		{
			Log.Error( ex );
			ErrorReporter.Flush();
			EngineGlobal.Plat_MessageBox( "Bootstrap::PreInit Error", $"Failed to bootstrap engine: {ex.Message}\n\n{ex.StackTrace}" );
			try { NLog.LogManager.Shutdown(); } catch { }
			EngineGlobal.Plat_ExitProcess( 1 );
		}
	}

	/// <summary>
	/// Let's native exit the C# app so AppDomain.ProcessExit gets called
	/// </summary>
	internal static void EnvironmentExit( int nCode )
	{
		// When we exit the process from C++, make sure we flush the C# Sdk
		try { ErrorReporter.Flush(); } catch { }
		try { NLog.LogManager.Shutdown(); } catch { }

		// Calling Environment.Exit would be ideal but it calls C++ global destructors which fucks everything up
		// Source 2 depends on the process just being terminated abruptly and doing no cleanup... :)
		// Environment.Exit( nCode );
	}

	/// <summary>
	/// Called on exceptions from a task (delayed, because it'll only get called when the exception gets collected)
	/// TODO: Move this somewhere else
	/// </summary>
	private static void TaskScheduler_UnobservedTaskException( object sender, UnobservedTaskExceptionEventArgs e )
	{
		var exception = e.Exception.Flatten();

		log.Error( exception );

		foreach ( var ex in exception.InnerExceptions )
		{
			log.Error( ex );
		}
	}

	/// <summary>
	/// Called to initialize the engine.
	/// </summary>
	internal static void Init()
	{
		try
		{
		// Add native filesystem search paths for core content with correct casing
		// This must happen after SourceEngineInit has set up the native filesystem
		System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] Before InitializeNativeSearchPaths\n");
		EngineFileSystem.InitializeNativeSearchPaths();
		System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] After InitializeNativeSearchPaths\n");

		// Mount downloaded package assets now that SourceEngineInit has initialized the native filesystem.
		// This must happen after SourceEngineInit — NativeEngine.FullFileSystem.AddSymLink is not valid before that.
		if ( !string.IsNullOrEmpty( EngineFileSystem.PendingDownloadAssetsPath ) )
		{
			System.IO.File.AppendAllText("/tmp/initgame_debug.txt", $"[Bootstrap.Init] Mounting downloaded assets from {EngineFileSystem.PendingDownloadAssetsPath}\n");
			EngineFileSystem.MountDownloadedAssets( EngineFileSystem.PendingDownloadAssetsPath );
			System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] Done mounting downloaded assets\n");
		}

			IToolsDll.Current?.Spin();

#pragma warning disable CS0612 // Type or member is obsolete

			// Look for a command line from Steam (this is for stuff like playing from https://sbox.game/)
			if ( NativeEngine.Steam.SteamApps().IsValid )
			{
				var steamCommandLine = NativeEngine.Steam.SteamApps().GetCommandLine();
				if ( !Application.IsHeadless && !string.IsNullOrEmpty( steamCommandLine ) )
				{
					CommandLine.CommandLineString = steamCommandLine;
					CommandLine.Parse();
				}
			}

#pragma warning restore CS0612 // Type or member is obsolete

			ReflectionUtility.RunAllStaticConstructors( "Sandbox.System" );
			ReflectionUtility.RunAllStaticConstructors( "Sandbox.Engine" );

			//log.Trace( "Bootstrap::Init" );
			//log.Trace( $"Current Directory is {System.IO.Directory.GetCurrentDirectory()}" );
			//log.Trace( $"RootFolder is {EngineFileSystem.RootFolder}" );
			//log.Trace( $"Command Line is {CommandLine.Full}" );

			// Add built in projects for game&tools
			// Game uses menu project but shouldn't be anything else
			// Load everything else in ToolsDll
		using ( var _ = StartupTiming?.ScopeTimer( $"BuiltIn Projects Init" ) )
		{
			System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] Before Project.InitializeBuiltIn\n");
			SyncContext.RunBlocking( Project.InitializeBuiltIn() );
			System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] After Project.InitializeBuiltIn\n");
		}

		System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] Before InitEngineConVars\n");
		InitEngineConVars();
		System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] After InitEngineConVars\n");

		if ( IToolsDll.Current is not null )
		{
			using var x = StartupTiming?.ScopeTimer( $"IToolsDll Bootstrap Init" );
			System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] Before IToolsDll.Current.Initialize\n");
			SyncContext.RunBlocking( IToolsDll.Current.Initialize() );
			System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] After IToolsDll.Current.Initialize\n");
		}

		//
		// Init vr system
		//
		System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] Before VRSystem.Init\n");
		VRSystem.Init();
		System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] After VRSystem.Init\n");

			System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] Before Screen.UpdateFromEngine\n");
			Screen.UpdateFromEngine();
			System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] After Screen.UpdateFromEngine\n");

			if ( !Application.IsHeadless && !Application.IsStandalone )
			{
			// we really want the items available before we continue
			// here we'll wait up to 5 seconds for them, but they're
			// generally available completely immediately.
			using var timeout = new CancellationTokenSource( 5000 );
			System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] Before WaitForSteamInventoryItems\n");
			SyncContext.RunBlocking( Services.Inventory.WaitForSteamInventoryItems( timeout.Token ) );
			System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] After WaitForSteamInventoryItems\n");
			}

			if ( IMenuDll.Current is not null )
			{
				using var x = StartupTiming?.ScopeTimer( $"MenuBootstrap" );
				System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] Before IMenuDll.Current.Initialize\n");
				SyncContext.RunBlocking( IMenuDll.Current.Initialize() );
				System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] After IMenuDll.Current.Initialize\n");
			}

			if ( IGameInstanceDll.Current is not null )
			{
				using var x = StartupTiming?.ScopeTimer( $"IGameMenuDll Bootstrap" );
				System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] Before IGameInstanceDll.Current.Initialize\n");
				SyncContext.RunBlocking( IGameInstanceDll.Current.Initialize() );
				System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] After IGameInstanceDll.Current.Initialize\n");
			}

			if ( SteamClient.IsValid && ErrorReporter.IsUsingSentry )
			{
				SentrySdk.ConfigureScope( scope =>
				{
					scope.User = new SentryUser
					{
						Username = SteamClient.Name,
						Id = SteamClient.SteamId.ToString()
					};
				} );
			}

			Internal.TypeLibrary.OnClassName = x => StringToken.FindOrCreate( x );

			if ( IToolsDll.Current is not null )
			{
				using var x = StartupTiming?.ScopeTimer( $"Load Project" );
				SyncContext.RunBlocking( IToolsDll.Current.LoadProject() );
			}

			if ( !Application.IsHeadless )
			{
				LoadingFinished();
			}

			// Run any commands
			foreach ( var sw in CommandLine.GetSwitches() )
			{
				var cmd = ConVarSystem.Find( sw.Key );
				if ( cmd is null || !cmd.IsConCommand ) continue;

				cmd.Run( sw.Value );
			}

			if ( Application.IsEditor )
			{
				Log.Info( "Bootstrap Init Done" );
			}

		//
		// Networking bootstrap
		//
		System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] Before Networking.Bootstrap\n");
		Networking.Bootstrap();
		System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] After Networking.Bootstrap\n");
		System.IO.File.AppendAllText("/tmp/initgame_debug.txt", "[Bootstrap.Init] Completed successfully\n");

		if ( Application.IsJoinLocal )
			{
				NetworkConsoleCommands.ConnectToServer( "local" );
			}
		}
		catch ( Exception ex )
		{
			Log.Error( ex );

			var diagnostics = string.Join( "\n", Project.GetCompileDiagnostics()?.Where( x => x.Severity > Microsoft.CodeAnalysis.DiagnosticSeverity.Warning )
				.Select( x => $"{x.Severity} | {x.GetMessage()} - {x.Location?.SourceTree?.FilePath}:{x.Location?.GetLineSpan().StartLinePosition}" ) );

			EngineGlobal.Plat_MessageBox( "Bootstrap::Init Error", $"""
				Failed to bootstrap engine.
				
				{(string.IsNullOrEmpty( diagnostics ) ? ex : diagnostics)}

				This either means that we've messed something up, or you've edited a base addon - in that case, verify your game files.
				Take a look at your Log files if you're still having problems.
				""" );
			try { NLog.LogManager.Shutdown(); } catch { }
			EngineGlobal.Plat_ExitProcess( 1 );
		}
	}

	internal static void InitMinimal( string rootFolder )
	{
		Environment.CurrentDirectory = rootFolder;

		Sandbox.Utility.Steam.InitializeClient();
		ThreadSafe.MarkMainThread();

#if WIN
		// SetMinThreads is only available on Windows
		ThreadPool.SetMinThreads( Environment.ProcessorCount, Environment.ProcessorCount );
#endif

		TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
		AppDomain.CurrentDomain.UnhandledException += ( _, args ) => Log.Error( args.ExceptionObject as Exception, "AppDomain unhandled exception" );

		//System.Net.ServicePointManager.ServerCertificateValidationCallback += ( sender, cert, chain, sslPolicyErrors ) => true;

		EngineFileSystem.Initialize( Environment.CurrentDirectory );
		EngineFileSystem.InitializeConfigFolder();

		if ( !Application.IsStandalone )
		{
			ErrorReporter.Initialize();
		}
	}

	/// <summary>
	/// Should be called when startup has finished.
	/// If we have a client, this is when the menu is first entered.
	/// </summary>
	internal static void LoadingFinished()
	{
		if ( StartupTiming != null )
		{
			StartupTiming.FinishTimer( "Time" );
			StartupTiming.SetValue( "package.ident", Application.GameIdent );
			StartupTiming.Submit( true );
		}

		if ( Application.IsBenchmark )
		{
			if ( !Api.IsConnected )
			{
				Log.Warning( "Not connected to backend - quitting." );
				Environment.Exit( 10 );
			}

			RenderSettings.Instance.ApplySettingsForBenchmarks();

			// Load First Benchmark package
			if ( !TryLoadNextBenchmarkPackage() )
			{
				Console.WriteLine( "Quitting" );
				ConVarSystem.Run( "quit" );
			}
		}
	}

	private readonly record struct BenchmarkPackage( string PackageName, Dictionary<string, string> GameSettings = null );

	private static int _currentBenchmarkGameIndex = 0;

	private static List<BenchmarkPackage> _benchmarkGames = new()
	{
		new BenchmarkPackage( "facepunch.benchmark" ),
		new BenchmarkPackage( "facepunch.sbdm", new Dictionary<string, string> { { "sbdm.dev.benchmark", "1" } } ),
	};

	internal static bool TryLoadNextBenchmarkPackage()
	{
		if ( _currentBenchmarkGameIndex >= _benchmarkGames.Count ) return false;

		var benchmarkGame = _benchmarkGames[_currentBenchmarkGameIndex];
		LaunchArguments.GameSettings = benchmarkGame.GameSettings;
		_ = IGameInstanceDll.Current.LoadGamePackageAsync( benchmarkGame.PackageName, GameLoadingFlags.Host, default );
		_currentBenchmarkGameIndex++;

		return true;
	}

	static void InitEngineConVars()
	{
		ConVarSystem.AddAssembly( typeof( Bootstrap ).Assembly, "engine" );
	}
}
