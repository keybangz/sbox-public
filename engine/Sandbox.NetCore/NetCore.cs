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
	private static bool DebugLoggingEnabled =>
		string.Equals( System.Environment.GetEnvironmentVariable( "SBOX_NETCORE_DEBUG" ), "1", StringComparison.Ordinal );

	private static void DebugLog( string message )
	{
		// Debug logging disabled
	}

	// Delegate type for the entry point
	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	public delegate int InitializeEngineDelegate( IntPtr gameFolderPtr );

	// The actual implementation (without UnmanagedCallersOnly)
	public static int InitializeEngineImpl( IntPtr gameFolderPtr )
	{
		try
		{
			DebugLog( "[NetCore] InitializeEngineImpl called" );

			var gameFolder = Marshal.PtrToStringUTF8( gameFolderPtr );
			DebugLog( $"[NetCore] gameFolder={gameFolder}" );

			// Use Path.Combine for cross-platform compatibility
			var managedFolder = System.IO.Path.Combine( gameFolder, "bin", "managed" );
			DebugLog( $"[NetCore] managedFolder={managedFolder}" );

			var assemblyPath = System.IO.Path.Combine( managedFolder, "Sandbox.Engine.dll" );
			DebugLog( $"[NetCore] Loading assembly: {assemblyPath}" );

			var assembly = Assembly.LoadFrom( assemblyPath );
			DebugLog( $"[NetCore] Assembly loaded: {assembly.FullName}" );

			var type = assembly.GetTypes().Where( x => x.Name == "NetCore" ).FirstOrDefault();
			DebugLog( $"[NetCore] Found type: {type?.FullName}" );

			var method = type.GetMethod( "InitializeInterop", BindingFlags.Static | BindingFlags.NonPublic );
			DebugLog( $"[NetCore] Found method: {method?.Name}" );

			DebugLog( "[NetCore] Invoking InitializeInterop..." );
			method.Invoke( null, new object[] { gameFolder } );
			DebugLog( "[NetCore] InitializeInterop completed" );

			return 0;
		}
		catch ( Exception ex )
		{
			DebugLog( $"[NetCore] ERROR: {ex}" );
			return -1;
		}
	}

	// Entry point called by native code
	[UnmanagedCallersOnly( CallConvs = new[] { typeof( System.Runtime.CompilerServices.CallConvCdecl ) } )]
	public static int InitializeEngine( IntPtr gameFolderPtr )
	{
		DebugLog( "[NetCore] InitializeEngine called" );
		return InitializeEngineImpl( gameFolderPtr );
	}
}
