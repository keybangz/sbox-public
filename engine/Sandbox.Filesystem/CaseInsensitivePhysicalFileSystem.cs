using System.IO;
using Zio;
using Zio.FileSystems;

namespace Sandbox;

/// <summary>
/// A case-insensitive physical file system wrapper for Linux.
/// This resolves paths case-insensitively by searching the actual directory contents,
/// eliminating the need for symlinks on case-sensitive file systems.
/// </summary>
internal class CaseInsensitivePhysicalFileSystem : PhysicalFileSystem
{
	/// <summary>
	/// Whether to use case-insensitive path resolution (enabled on Linux, disabled on Windows/macOS).
	/// </summary>
	private static readonly bool UseCaseInsensitiveResolution = OperatingSystem.IsLinux();

	/// <summary>
	/// Enable debug logging for path resolution.
	/// Set SBOX_FS_DEBUG=1 environment variable to enable.
	/// </summary>
	public static bool DebugLogging { get; set; } = Environment.GetEnvironmentVariable( "SBOX_FS_DEBUG" ) == "1";

	/// <summary>
	/// Cache for resolved paths to avoid repeated filesystem lookups.
	/// Key: lowercase path, Value: actual path on disk
	/// </summary>
	private readonly Dictionary<string, string> _pathCache = new( StringComparer.OrdinalIgnoreCase );
	private readonly object _cacheLock = new();

	private static void DebugLog( string message )
	{
		if ( !DebugLogging ) return;
		Console.WriteLine( $"[CaseInsensitiveFS] {message}" );
	}

	/// <summary>
	/// Resolves a path case-insensitively by walking the directory tree.
	/// </summary>
	/// <param name="path">The path to resolve.</param>
	/// <returns>The actual path on disk, or the original path if not found.</returns>
	private string ResolveCaseInsensitive( string path )
	{
		if ( !UseCaseInsensitiveResolution || string.IsNullOrEmpty( path ) )
			return path;

		// Check cache first
		lock ( _cacheLock )
		{
			if ( _pathCache.TryGetValue( path, out var cachedPath ) )
			{
				DebugLog( $"Cache hit: '{path}' -> '{cachedPath}'" );
				return cachedPath;
			}
		}

		var resolvedPath = ResolvePathInternal( path );

		// Cache the result
		lock ( _cacheLock )
		{
			_pathCache[path] = resolvedPath;
		}

		if ( resolvedPath != path )
		{
			DebugLog( $"Resolved: '{path}' -> '{resolvedPath}'" );
		}
		else
		{
			DebugLog( $"No change: '{path}'" );
		}

		return resolvedPath;
	}

	private string ResolvePathInternal( string path )
	{
		// Split the path into segments
		var segments = path.Split( new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries );
		if ( segments.Length == 0 )
		{
			DebugLog( $"ResolvePathInternal: empty segments for '{path}'" );
			return path;
		}

		// Determine the root - if path starts with /, use /, otherwise use current directory
		string currentPath;
		if ( path.StartsWith( "/" ) )
		{
			currentPath = "/";
		}
		else
		{
			// Relative path - this shouldn't happen often in our use case
			currentPath = Directory.GetCurrentDirectory();
			DebugLog( $"ResolvePathInternal: relative path '{path}', using cwd '{currentPath}'" );
		}

		foreach ( var segment in segments )
		{
			if ( !Directory.Exists( currentPath ) )
			{
				DebugLog( $"ResolvePathInternal: parent '{currentPath}' doesn't exist for segment '{segment}'" );
				return path; // Parent doesn't exist, return original
			}

			// Try exact match first
			var exactPath = Path.Combine( currentPath, segment );
			if ( Directory.Exists( exactPath ) || File.Exists( exactPath ) )
			{
				currentPath = exactPath;
				continue;
			}

			// Search for case-insensitive match
			bool found = false;
			try
			{
				var entries = Directory.GetFileSystemEntries( currentPath );
				foreach ( var entry in entries )
				{
					var entryName = Path.GetFileName( entry );
					if ( string.Equals( entryName, segment, StringComparison.OrdinalIgnoreCase ) )
					{
						DebugLog( $"Case-insensitive match: '{segment}' -> '{entryName}' in '{currentPath}'" );
						currentPath = entry;
						found = true;
						break;
					}
				}
			}
			catch ( UnauthorizedAccessException ex )
			{
				DebugLog( $"ResolvePathInternal: UnauthorizedAccessException in '{currentPath}': {ex.Message}" );
				return path;
			}
			catch ( DirectoryNotFoundException ex )
			{
				DebugLog( $"ResolvePathInternal: DirectoryNotFoundException in '{currentPath}': {ex.Message}" );
				return path;
			}

			if ( !found )
			{
				DebugLog( $"ResolvePathInternal: segment '{segment}' not found in '{currentPath}'" );
				return path; // Segment not found, return original path
			}
		}

		return currentPath;
	}

	/// <summary>
	/// Clears the path cache. Should be called when files/directories are created or renamed.
	/// </summary>
	internal void ClearCache()
	{
		lock ( _cacheLock )
		{
			_pathCache.Clear();
		}
	}

	protected override string ConvertPathToInternalImpl( UPath path )
	{
		var internalPath = base.ConvertPathToInternalImpl( path );
		var resolved = ResolveCaseInsensitive( internalPath );
		DebugLog( $"ConvertPathToInternalImpl: UPath='{path}' -> internal='{internalPath}' -> resolved='{resolved}'" );
		return resolved;
	}

	protected override bool FileExistsImpl( UPath path )
	{
		var internalPath = ConvertPathToInternal( path );
		var resolvedPath = ResolveCaseInsensitive( internalPath );
		var exists = File.Exists( resolvedPath );
		DebugLog( $"FileExistsImpl: UPath='{path}' -> internal='{internalPath}' -> resolved='{resolvedPath}' -> exists={exists}" );
		return exists;
	}

	protected override bool DirectoryExistsImpl( UPath path )
	{
		var internalPath = ConvertPathToInternal( path );
		var resolvedPath = ResolveCaseInsensitive( internalPath );
		var exists = Directory.Exists( resolvedPath );
		DebugLog( $"DirectoryExistsImpl: UPath='{path}' -> internal='{internalPath}' -> resolved='{resolvedPath}' -> exists={exists}" );
		return exists;
	}

	protected override Stream OpenFileImpl( UPath path, FileMode mode, FileAccess access, FileShare share )
	{
		var internalPath = ConvertPathToInternal( path );
		var resolvedPath = ResolveCaseInsensitive( internalPath );
		DebugLog( $"OpenFileImpl: UPath='{path}' -> internal='{internalPath}' -> resolved='{resolvedPath}'" );
		try
		{
			return new FileStream( resolvedPath, mode, access, share );
		}
		catch ( Exception ex )
		{
			DebugLog( $"OpenFileImpl FAILED: {ex.GetType().Name}: {ex.Message}" );
			throw;
		}
	}

	protected override IEnumerable<UPath> EnumeratePathsImpl( UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget )
	{
		// Get the ORIGINAL internal path (before case resolution) from the base class
		var originalInternalPath = base.ConvertPathToInternalImpl( path );
		// Get the resolved path (with correct casing on disk)
		var resolvedPath = ResolveCaseInsensitive( originalInternalPath );

		DebugLog( $"EnumeratePathsImpl: UPath='{path}' -> originalInternal='{originalInternalPath}' -> resolved='{resolvedPath}', pattern='{searchPattern}'" );

		// If the resolved path differs from the original internal path (case difference),
		// we need to enumerate from the resolved path but return paths with original casing
		// to satisfy SubFileSystem's path validation
		if ( !string.Equals( originalInternalPath, resolvedPath, StringComparison.Ordinal ) )
		{
			return EnumerateWithOriginalCasing( path, originalInternalPath, resolvedPath, searchPattern, searchOption, searchTarget );
		}

		return base.EnumeratePathsImpl( path, searchPattern, searchOption, searchTarget );
	}

	private IEnumerable<UPath> EnumerateWithOriginalCasing( UPath originalPath, string internalPath, string resolvedPath, string searchPattern, SearchOption searchOption, SearchTarget searchTarget )
	{
		// Enumerate files from the actual (resolved) path on disk
		IEnumerable<string> entries;
		try
		{
			switch ( searchTarget )
			{
				case SearchTarget.File:
					entries = Directory.EnumerateFiles( resolvedPath, searchPattern, searchOption );
					break;
				case SearchTarget.Directory:
					entries = Directory.EnumerateDirectories( resolvedPath, searchPattern, searchOption );
					break;
				default: // Both
					entries = Directory.EnumerateFileSystemEntries( resolvedPath, searchPattern, searchOption );
					break;
			}
		}
		catch ( DirectoryNotFoundException )
		{
			yield break;
		}

		foreach ( var entry in entries )
		{
			// Convert the resolved path back to the original casing
			// e.g., /game/menu/Localization/en/file.json -> /game/menu/localization/en/file.json
			var relativePart = entry.Substring( resolvedPath.Length );
			var originalCasedPath = internalPath + relativePart;

			// Convert back to UPath
			var upath = ConvertPathFromInternal( originalCasedPath );
			DebugLog( $"EnumerateWithOriginalCasing: '{entry}' -> '{originalCasedPath}' -> UPath='{upath}'" );
			yield return upath;
		}
	}

	protected override FileAttributes GetAttributesImpl( UPath path )
	{
		var internalPath = ConvertPathToInternal( path );
		var resolvedPath = ResolveCaseInsensitive( internalPath );
		DebugLog( $"GetAttributesImpl: UPath='{path}' -> internal='{internalPath}' -> resolved='{resolvedPath}'" );
		return File.GetAttributes( resolvedPath );
	}
}

