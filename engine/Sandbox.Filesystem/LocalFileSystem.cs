using System;

namespace Sandbox;

/// <summary>
/// A directory on a disk
/// </summary>
internal class LocalFileSystem : BaseFileSystem
{
	Zio.FileSystems.PhysicalFileSystem Physical { get; }

	/// <summary>
	/// Enable debug logging for filesystem operations.
	/// Set SBOX_FS_DEBUG=1 environment variable to enable.
	/// </summary>
	private static bool DebugLogging => Environment.GetEnvironmentVariable( "SBOX_FS_DEBUG" ) == "1";

	private static void DebugLog( string message )
	{
		if ( !DebugLogging ) return;
		Console.WriteLine( $"[LocalFileSystem] {message}" );
	}

	internal LocalFileSystem( string rootFolder, bool makereadonly = false )
	{
		// on Linux we're going to have a case sensitive filesystem
		// instead of fucking everything up everywhere and relying on people to nail case
		// we wrap our highest filesystem and resolve it there
		if ( OperatingSystem.IsLinux() )
		{
			Physical = new CaseInsensitivePhysicalFileSystem();
		}
		// on sane operating systems with case insensitive filesystems
		// windows + macos do the normal path
		else
		{
			Physical = new Zio.FileSystems.PhysicalFileSystem();
		}

		var rootPath = Physical.ConvertPathFromInternal( rootFolder );
		system = new Zio.FileSystems.SubFileSystem( Physical, rootPath );
		DebugLog( $"Created SubFileSystem with root='{rootPath}'" );

		if ( makereadonly )
		{
			system = new Zio.FileSystems.ReadOnlyFileSystem( system );
			DebugLog( "Wrapped in ReadOnlyFileSystem" );
		}
	}

	internal override void Dispose()
	{
		base.Dispose();

		Physical?.Dispose();
	}
}
