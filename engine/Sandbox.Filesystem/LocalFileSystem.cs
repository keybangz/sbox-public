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
		DebugLog( $"Creating LocalFileSystem for rootFolder='{rootFolder}', readonly={makereadonly}" );

		// Use case-insensitive file system on Linux to eliminate symlink requirements
		if ( OperatingSystem.IsLinux() )
		{
			Physical = new CaseInsensitivePhysicalFileSystem();
			DebugLog( "Using CaseInsensitivePhysicalFileSystem on Linux" );
		}
		else
		{
			Physical = new Zio.FileSystems.PhysicalFileSystem();
			DebugLog( "Using standard PhysicalFileSystem" );
		}

		// On Linux, don't convert to lowercase - the CaseInsensitivePhysicalFileSystem handles case matching
		// On Windows, lowercase is fine since the OS handles case-insensitivity
		string normalizedPath;
		if ( OperatingSystem.IsLinux() )
		{
			normalizedPath = rootFolder;
			DebugLog( $"Linux: keeping original path '{normalizedPath}'" );
		}
		else
		{
			normalizedPath = rootFolder.ToLowerInvariant();
			DebugLog( $"Windows: converted to lowercase '{normalizedPath}'" );
		}

		var rootPath = Physical.ConvertPathFromInternal( normalizedPath );
		DebugLog( $"ConvertPathFromInternal: '{normalizedPath}' -> UPath='{rootPath}'" );

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
