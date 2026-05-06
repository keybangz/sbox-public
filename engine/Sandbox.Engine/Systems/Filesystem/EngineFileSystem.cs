

using Sandbox.Engine;

namespace Sandbox;

internal static class EngineFileSystem
{
	public static LocalFileSystem Root { get; private set; }
	public static BaseFileSystem Config { get; private set; }
	public static BaseFileSystem Addons { get; private set; }
	public static BaseFileSystem Data { get; private set; }
	public static BaseFileSystem CoreContent { get; private set; }
	public static BaseFileSystem Mounted => GlobalContext.Current.FileMount;

	/// <summary>
	/// Content from libraries. This only exists in editor.
	/// </summary>
	public static BaseFileSystem LibraryContent { get; private set; }

	/// <summary>
	/// For tools, maintain a list of mounted addon content paths
	/// </summary>
	public static BaseFileSystem Assets { get; private set; }

	internal static BaseFileSystem DownloadedFiles { get; private set; }

	// Stored for deferred mounting after SourceEngineInit
	internal static string PendingDownloadAssetsPath { get; private set; }

	/// <summary>
	/// A place to write files temporarily. This is stored in memory so 
	/// cleaning up after yourself is a good idea (!)
	/// </summary>
	public static BaseFileSystem Temporary { get; private set; }

	/// <summary>
	/// The .source2/temp folder
	/// </summary>
	public static BaseFileSystem EditorTemporary { get; private set; }

	/// <summary>
	/// The folder holding the project's settings files
	/// </summary>
	internal static BaseFileSystem ProjectSettings { get; set; }

	/// <summary>
	/// Don't try to use the filesystem until you've called this!
	/// </summary>
	internal static void Initialize( string rootFolder, bool skipBaseFolderInit = false )
	{
		if ( Root != null )
			throw new System.Exception( "Filesystem Multi-Initialize" );

		Root = new LocalFileSystem( rootFolder );
		Temporary = new MemoryFileSystem();

		if ( skipBaseFolderInit ) return;

		if ( Application.IsEditor )
		{
			LibraryContent = new AggregateFileSystem();
			EditorTemporary = Root.CreateSubSystem( "/.source2/temp" );
		}

		Assets = new AggregateFileSystem();
		CoreContent = new AggregateFileSystem();

		if ( Application.IsStandalone )
		{
			CoreContent.CreateAndMount( Root, "/core/" );

			Assets.CreateAndMount( Root, "/core/" );
			Assets.CreateAndMount( Root, "/addons/base/Assets" );
		}
		else
		{
			CoreContent.CreateAndMount( Root, "/core/" );
			CoreContent.CreateAndMount( Root, "/addons/base/Assets/" );
			CoreContent.CreateAndMount( Root, "/addons/citizen/Assets/" );

			Assets.CreateAndMount( Root, "/core/" );
			Assets.CreateAndMount( Root, "/addons/base/Assets/" );
			Assets.CreateAndMount( Root, "/addons/citizen/Assets/" );
		}
	}

	/// <summary>
	/// Setup Config parameter
	/// </summary>
	internal static void InitializeConfigFolder( string name = "/config" )
	{
		Assert.NotNull( name );
		Assert.NotNull( Root );

		Root.CreateDirectory( "/config" );
		Config = Root.CreateSubSystem( "/config" );
	}

	/// <summary>
	/// Setup Addons parameter (there's no reason for this to exist now?)
	/// </summary>
	internal static void InitializeAddonsFolder( string name = "/addons" )
	{
		Assert.NotNull( name );
		Assert.NotNull( Root );

		Addons = Root.CreateSubSystem( "/addons" );
	}

	/// <summary>
	/// Setup Download folder
	/// </summary>
	internal static void InitializeDownloadsFolder( string name = "/download" )
	{
		Assert.NotNull( name );
		Assert.NotNull( Root );

		// alex: Don't bother if we're in standalone mode, because games aren't able
		// to download anything from the backend
		if ( Application.IsStandalone )
			return;

		Root.CreateDirectory( $"{name}" );
		Root.CreateDirectory( $"{name}/.sv" );
		DownloadedFiles = Root.CreateSubSystem( $"{name}" );

		// Store path for deferred mounting after SourceEngineInit initializes the native filesystem.
		// MountDownloadedAssets must NOT be called here — NativeEngine.FullFileSystem is not ready yet.
		var downloadAssetsPath = Root.GetFullPath( $"{name}/assets" );
		if ( !string.IsNullOrWhiteSpace( downloadAssetsPath ) && System.IO.Directory.Exists( downloadAssetsPath ) )
		{
			PendingDownloadAssetsPath = downloadAssetsPath;
		}
	}

	/// <summary>
	/// Scan download/assets/ and register AddSymLink for every CRC-hashed file,
	/// mapping the plain name to the absolute CRC-hashed path.
	/// This restores the native engine's file redirect table for assets that were
	/// downloaded in a previous session.
	/// </summary>
	internal static void MountDownloadedAssets( string downloadAssetsAbsPath )
	{
		// CRC filenames have the format: sanitized_name.16hexchars.ext
		// We detect the CRC by checking if the second-to-last dot-segment is exactly 16 hex chars.
		var hexChars = new System.Collections.Generic.HashSet<char>( "0123456789abcdef" );

		int mounted = 0;
		int skipped = 0;

		foreach ( var absFile in System.IO.Directory.EnumerateFiles( downloadAssetsAbsPath, "*", System.IO.SearchOption.AllDirectories ) )
		{
			var fileName = System.IO.Path.GetFileName( absFile );
			var parts = fileName.Split( '.' );

			// Need at least 3 parts: name, crc, ext  (e.g. "muzzle", "a3c54459b7e8ce16", "prefab_c")
			// But ext may itself contain underscores — the last part is the extension suffix
			// Format: {name_parts...}.{16hexcrc}.{ext}
			// Find the CRC segment: exactly 16 chars, all hex
			int crcIdx = -1;
			for ( int i = parts.Length - 2; i >= 1; i-- )
			{
				if ( parts[i].Length == 16 && parts[i].All( c => hexChars.Contains( c ) ) )
				{
					crcIdx = i;
					break;
				}
			}

			if ( crcIdx < 0 )
			{
				skipped++;
				continue; // Not a CRC-hashed file
			}

			// Reconstruct plain filename: everything before crcIdx + ext after crcIdx
			var plainNameParts = parts[..crcIdx].Concat( parts[(crcIdx + 1)..] );
			var plainFileName = string.Join( '.', plainNameParts );

			// Get relative path from downloadAssetsAbsPath
			var relFromAssets = System.IO.Path.GetRelativePath( downloadAssetsAbsPath, absFile );
			var relDir = System.IO.Path.GetDirectoryName( relFromAssets )?.Replace( '\\', '/' ) ?? "";
			var plainRelPath = string.IsNullOrEmpty( relDir ) ? plainFileName : $"{relDir}/{plainFileName}";

			// Register with native engine: plain relative path -> absolute CRC-hashed file
			NativeEngine.FullFileSystem.AddSymLink( plainRelPath, "GAME", absFile );
			mounted++;
		}

		Log.Info( $"[EngineFileSystem] Mounted {mounted} downloaded assets from {downloadAssetsAbsPath} ({skipped} skipped)" );
	}

	/// <summary>
	/// Setup Addons parameter (there's no reason for this to exist now?)
	/// </summary>
	internal static void InitializeDataFolder( string name = "/data" )
	{
		Assert.NotNull( name );
		Assert.NotNull( Root );

		Root.CreateDirectory( $"{name}" );
		Data = Root.CreateSubSystem( $"{name}" );
	}

	/// <summary>
	/// Should only be called at the very death
	/// </summary>
	internal static void Shutdown()
	{
		Root = null;
		Config = null;

		DownloadedFiles?.Dispose();
		DownloadedFiles = null;

		Addons?.Dispose();
		Addons = null;

		Root?.Dispose();
		Root = null;
	}

	internal static void AddContentPath( string v )
	{
		CoreContent.Mount( new LocalFileSystem( v ) );
	}

	internal static void AddAssetPath( string ident, string path )
	{
		Mounted?.Mount( new LocalFileSystem( path ) );
		NativeEngine.FullFileSystem.AddProjectPath( "xxx", path );
	}

	/// <summary>
	/// Add native filesystem search paths for core content.
	/// Called after SourceEngineInit has set up the native filesystem.
	/// On case-insensitive filesystems (ext4 overlay) this is a no-op.
	/// </summary>
	internal static void InitializeNativeSearchPaths()
	{
		// The filesystem overlay handles case-insensitivity natively.
		// No additional search path manipulation is required.
	}

	/// <summary>
	/// Attempt to resolve a path with case-insensitive matching using a prebuilt cache.
	/// On a case-insensitive filesystem this always returns the input unchanged.
	/// </summary>
	internal static string ResolvePathCase( string path )
	{
		// Case-insensitive ext4 overlay — no resolution needed.
		return path;
	}

	/// <summary>
	/// Find a file case-insensitively and return both the resolved relative path and the full absolute path.
	/// On a case-insensitive filesystem this always returns (path, null) — the caller handles null fullPath.
	/// </summary>
	internal static (string resolvedPath, string fullPath) FindFileCaseInsensitiveWithFullPath( string path )
	{
		// Case-insensitive ext4 overlay — no resolution needed.
		return (path, null);
	}
}
