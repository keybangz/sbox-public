

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
			Assets.CreateAndMount( Root, "/addons/base/assets" );

			// Add native filesystem paths with correct casing for Linux
			AddNativeSearchPath( rootFolder, "core" );
			AddNativeSearchPath( rootFolder, "addons/base/Assets", "addons/base/assets" );
		}
		else
		{
			CoreContent.CreateAndMount( Root, "/core/" );
			CoreContent.CreateAndMount( Root, "/addons/base/assets/" );
			CoreContent.CreateAndMount( Root, "/addons/citizen/assets/" );

			Assets.CreateAndMount( Root, "/core/" );
			Assets.CreateAndMount( Root, "/addons/base/assets/" );
			Assets.CreateAndMount( Root, "/addons/citizen/assets/" );

			// Add native filesystem paths with correct casing for Linux
			AddNativeSearchPath( rootFolder, "core" );
			AddNativeSearchPath( rootFolder, "addons/base/Assets", "addons/base/assets" );
			AddNativeSearchPath( rootFolder, "addons/citizen/Assets", "addons/citizen/assets" );
		}
	}

	/// <summary>
	/// Add a search path to the native filesystem with case-insensitive lookup on Linux.
	/// </summary>
	private static void AddNativeSearchPath( string rootFolder, string relativePath, string fallbackPath = null )
	{
		var fullPath = System.IO.Path.Combine( rootFolder, relativePath );

		// On Linux, check if the path exists, otherwise try case-insensitive lookup
		if ( OperatingSystem.IsLinux() && !System.IO.Directory.Exists( fullPath ) )
		{
			// Try the fallback path
			if ( fallbackPath != null )
			{
				var fallbackFullPath = System.IO.Path.Combine( rootFolder, fallbackPath );
				if ( System.IO.Directory.Exists( fallbackFullPath ) )
				{
					fullPath = fallbackFullPath;
				}
			}

			// Try to find the correct cased path
			var resolved = FindDirectoryCaseInsensitive( rootFolder, relativePath );
			if ( resolved != null )
			{
				fullPath = resolved;
			}
		}

		if ( System.IO.Directory.Exists( fullPath ) )
		{
			var ident = System.IO.Path.GetFileName( relativePath );
			Log.Info( $"[EngineFileSystem] Adding native search path: ident={ident}, path={fullPath}" );
			NativeEngine.FullFileSystem.AddProjectPath( ident, fullPath );
			// Also add to the engine's search path so resource system can find files
			NativeEngine.EngineGlue.AddSearchPath( fullPath, "GAME", true );

			// Register for case-insensitive file resolution on Linux
			if ( OperatingSystem.IsLinux() && !_nativeSearchPaths.Contains( fullPath ) )
			{
				_nativeSearchPaths.Add( fullPath );
				Log.Info( $"[EngineFileSystem] Registered search path for case-insensitive resolution: {fullPath}" );

				// Build the path case cache for fast lookups
				BuildPathCacheForSearchPath( fullPath );
			}
		}
		else
		{
			Log.Warning( $"[EngineFileSystem] Native search path not found: {fullPath}" );
		}
	}

	/// <summary>
	/// Find a directory with case-insensitive path matching.
	/// </summary>
	private static string FindDirectoryCaseInsensitive( string rootFolder, string relativePath )
	{
		var segments = relativePath.Split( new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries );
		var currentPath = rootFolder;

		foreach ( var segment in segments )
		{
			if ( !System.IO.Directory.Exists( currentPath ) )
				return null;

			var exactPath = System.IO.Path.Combine( currentPath, segment );
			if ( System.IO.Directory.Exists( exactPath ) )
			{
				currentPath = exactPath;
				continue;
			}

			// Search case-insensitively
			bool found = false;
			foreach ( var dir in System.IO.Directory.GetDirectories( currentPath ) )
			{
				var dirName = System.IO.Path.GetFileName( dir );
				if ( string.Equals( dirName, segment, StringComparison.OrdinalIgnoreCase ) )
				{
					currentPath = dir;
					found = true;
					break;
				}
			}

			if ( !found )
				return null;
		}

		return currentPath;
	}

	// List of registered native search paths for case-insensitive file resolution
	private static List<string> _nativeSearchPaths = new();

	// Cache of lowercase paths to correct-cased paths for fast lookup
	// Key: lowercase relative path (e.g. "models/citizen_clothes/hat/headphones/models/headphones.vmdl")
	// Value: correct-cased relative path (e.g. "models/citizen_clothes/hat/Headphones/Models/headphones.vmdl")
	private static Dictionary<string, string> _pathCaseCache = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>
	/// Build the path case cache for a search path. This scans all files and builds a mapping
	/// from lowercase paths to their correct-cased equivalents.
	/// </summary>
	private static void BuildPathCacheForSearchPath( string searchPath )
	{
		if ( !OperatingSystem.IsLinux() )
			return;

		if ( !System.IO.Directory.Exists( searchPath ) )
			return;

		try
		{
			var files = System.IO.Directory.GetFiles( searchPath, "*", System.IO.SearchOption.AllDirectories );
			foreach ( var fullPath in files )
			{
				var relativePath = System.IO.Path.GetRelativePath( searchPath, fullPath ).Replace( '\\', '/' );
				var lowerPath = relativePath.ToLowerInvariant();

				// Store both the exact case and the lowercase key
				_pathCaseCache[lowerPath] = relativePath;
			}

			Log.Info( $"[EngineFileSystem] Built path cache for {searchPath}: {files.Length} files" );
		}
		catch ( System.Exception ex )
		{
			Log.Warning( $"[EngineFileSystem] Failed to build path cache for {searchPath}: {ex.Message}" );
		}
	}

	/// <summary>
	/// Resolve a path using the cached case mapping. This is O(1) lookup.
	/// </summary>
	internal static string ResolvePathCase( string relativePath )
	{
		if ( !OperatingSystem.IsLinux() )
			return relativePath;

		if ( string.IsNullOrWhiteSpace( relativePath ) )
			return relativePath;

		// Normalize the path
		relativePath = relativePath.Replace( '\\', '/' ).TrimStart( '/' );
		var lowerPath = relativePath.ToLowerInvariant();

		// Try direct cache lookup first (fast path)
		if ( _pathCaseCache.TryGetValue( lowerPath, out var cachedPath ) )
		{
			return cachedPath;
		}

		// Fallback to filesystem scan (slow path)
		var (resolved, _) = FindFileCaseInsensitiveWithFullPath( relativePath );
		if ( resolved != null )
		{
			// Cache the result
			_pathCaseCache[lowerPath] = resolved;
			return resolved;
		}

		return relativePath;
	}

	/// <summary>
	/// Find a file with case-insensitive path matching across all search paths.
	/// Returns the correctly-cased relative path if found, or null if not found.
	/// </summary>
	internal static string FindFileCaseInsensitive( string relativePath )
	{
		var (resolved, _) = FindFileCaseInsensitiveWithFullPath( relativePath );
		return resolved;
	}

	/// <summary>
	/// Find a file with case-insensitive path matching across all search paths.
	/// Returns a tuple of (correctly-cased relative path, full absolute path) if found.
	/// </summary>
	internal static (string resolvedPath, string fullPath) FindFileCaseInsensitiveWithFullPath( string relativePath )
	{
		if ( !OperatingSystem.IsLinux() )
			return (null, null); // Not needed on case-insensitive filesystems

		if ( string.IsNullOrWhiteSpace( relativePath ) )
			return (null, null);

		// Normalize the path
		relativePath = relativePath.Replace( '\\', '/' ).TrimStart( '/' );

		// Try each search path
		foreach ( var searchPath in _nativeSearchPaths )
		{
			var result = FindFileCaseInsensitiveInPath( searchPath, relativePath );
			if ( result != null )
			{
				var fullPath = System.IO.Path.Combine( searchPath, result );
				return (result, fullPath);
			}
		}

		return (null, null);
	}

	/// <summary>
	/// Find a file with case-insensitive path matching within a specific search path.
	/// Returns the correctly-cased relative path if found, or null if not found.
	/// </summary>
	private static string FindFileCaseInsensitiveInPath( string searchPath, string relativePath )
	{
		var segments = relativePath.Split( new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries );
		var currentPath = searchPath;
		var resolvedSegments = new List<string>();

		for ( int i = 0; i < segments.Length; i++ )
		{
			var segment = segments[i];
			bool isLastSegment = ( i == segments.Length - 1 );

			if ( isLastSegment )
			{
				// This is the file - check for file existence
				var exactPath = System.IO.Path.Combine( currentPath, segment );
				if ( System.IO.File.Exists( exactPath ) )
				{
					resolvedSegments.Add( segment );
					return string.Join( "/", resolvedSegments );
				}

				// Search case-insensitively for the file
				if ( System.IO.Directory.Exists( currentPath ) )
				{
					foreach ( var file in System.IO.Directory.GetFiles( currentPath ) )
					{
						var fileName = System.IO.Path.GetFileName( file );
						if ( string.Equals( fileName, segment, StringComparison.OrdinalIgnoreCase ) )
						{
							resolvedSegments.Add( fileName );
							return string.Join( "/", resolvedSegments );
						}
					}
				}

				return null; // File not found
			}
			else
			{
				// This is a directory - navigate into it
				var exactPath = System.IO.Path.Combine( currentPath, segment );
				if ( System.IO.Directory.Exists( exactPath ) )
				{
					resolvedSegments.Add( segment );
					currentPath = exactPath;
					continue;
				}

				// Search case-insensitively for the directory
				bool found = false;
				if ( System.IO.Directory.Exists( currentPath ) )
				{
					foreach ( var dir in System.IO.Directory.GetDirectories( currentPath ) )
					{
						var dirName = System.IO.Path.GetFileName( dir );
						if ( string.Equals( dirName, segment, StringComparison.OrdinalIgnoreCase ) )
						{
							resolvedSegments.Add( dirName );
							currentPath = dir;
							found = true;
							break;
						}
					}
				}

				if ( !found )
					return null;
			}
		}

		return null;
	}

	/// <summary>
	/// Initialize native filesystem search paths with correct casing for Linux.
	/// This must be called after SourceEngineInit has set up the native filesystem.
	/// </summary>
	internal static void InitializeNativeSearchPaths()
	{
		var rootFolder = Environment.CurrentDirectory;
		Log.Info( $"[EngineFileSystem] InitializeNativeSearchPaths called, rootFolder={rootFolder}" );

		// Add core and addon paths to native filesystem
		AddNativeSearchPath( rootFolder, "core" );
		AddNativeSearchPath( rootFolder, "addons/base/Assets", "addons/base/assets" );

		if ( !Application.IsStandalone )
		{
			AddNativeSearchPath( rootFolder, "addons/citizen/Assets", "addons/citizen/assets" );
			AddNativeSearchPath( rootFolder, "addons/menu/Assets", "addons/menu/assets" );
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
		Mounted.Mount( new LocalFileSystem( path ) );
		NativeEngine.FullFileSystem.AddProjectPath( "xxx", path );
	}
}
