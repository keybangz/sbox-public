using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

/*
 * 
 * This dll is only called in instances where we want to initialize .net from c++.
 * 
 * We purposefully don't reference any dlls here because it's loaded in its own isolated
 * context, so if we load sandbox.engine, it technically won't be loaded in the main context
 * and stuff will be weird.
 * 
 * Eventually we might not need this. We might make all the exe's dotnet exes - in which case
 * we'll make the exe call Interop.InitializeAll directly to set everything up.
 * 
 * */

internal static class NetCore
{
	// Delegate type for the entry point
	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	public delegate int InitializeEngineDelegate( IntPtr gameFolderPtr );

	// The actual implementation (without UnmanagedCallersOnly)
	public static int InitializeEngineImpl( IntPtr gameFolderPtr )
	{
		try
		{
			System.IO.File.AppendAllText( "/tmp/netcore_debug.txt", "[NetCore] InitializeEngineImpl called\n" );

			var gameFolder = Marshal.PtrToStringUTF8( gameFolderPtr );
			System.IO.File.AppendAllText( "/tmp/netcore_debug.txt", $"[NetCore] gameFolder={gameFolder}\n" );

			// Use Path.Combine for cross-platform compatibility
			var managedFolder = System.IO.Path.Combine( gameFolder, "bin", "managed" );
			System.IO.File.AppendAllText( "/tmp/netcore_debug.txt", $"[NetCore] managedFolder={managedFolder}\n" );

			var assemblyPath = System.IO.Path.Combine( managedFolder, "Sandbox.Engine.dll" );
			System.IO.File.AppendAllText( "/tmp/netcore_debug.txt", $"[NetCore] Loading assembly: {assemblyPath}\n" );

			var assembly = Assembly.LoadFrom( assemblyPath );
			System.IO.File.AppendAllText( "/tmp/netcore_debug.txt", $"[NetCore] Assembly loaded: {assembly.FullName}\n" );

			var type = assembly.GetTypes().Where( x => x.Name == "NetCore" ).FirstOrDefault();
			System.IO.File.AppendAllText( "/tmp/netcore_debug.txt", $"[NetCore] Found type: {type?.FullName}\n" );

			var method = type.GetMethod( "InitializeInterop", BindingFlags.Static | BindingFlags.NonPublic );
			System.IO.File.AppendAllText( "/tmp/netcore_debug.txt", $"[NetCore] Found method: {method?.Name}\n" );

			System.IO.File.AppendAllText( "/tmp/netcore_debug.txt", "[NetCore] Invoking InitializeInterop...\n" );
			method.Invoke( null, new object[] { gameFolder } );
			System.IO.File.AppendAllText( "/tmp/netcore_debug.txt", "[NetCore] InitializeInterop completed\n" );

			return 0;
		}
		catch ( Exception ex )
		{
			System.IO.File.AppendAllText( "/tmp/netcore_debug.txt", $"[NetCore] ERROR: {ex}\n" );
			return -1;
		}
	}

	// Entry point called by native code
	[UnmanagedCallersOnly( CallConvs = new[] { typeof( System.Runtime.CompilerServices.CallConvCdecl ) } )]
	public static int InitializeEngine( IntPtr gameFolderPtr )
	{
		System.IO.File.AppendAllText( "/tmp/netcore_debug.txt", "[NetCore] InitializeEngine called\n" );
		return InitializeEngineImpl( gameFolderPtr );
	}
}
