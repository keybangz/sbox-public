using Microsoft.CodeAnalysis;
using System.IO;
using System.Reflection;

namespace Sandbox;

/// <summary>
/// Loads the framework assemblies from the bin/ref folder and makes 
/// them available globally to every compiler.
/// </summary>
[SkipHotload]
static class FrameworkReferences
{
	public static CaseInsensitiveDictionary<PortableExecutableReference> All { get; } = new();

	static FrameworkReferences()
	{
		var assembly = Assembly.GetExecutingAssembly();
		var resourceNames = assembly.GetManifestResourceNames()
									.Where( name => name.EndsWith( ".dll", StringComparison.OrdinalIgnoreCase ) )
									.ToArray();

		var referenceFiles = new List<string>();

		foreach ( var resourceName in resourceNames )
		{
			using ( var stream = assembly.GetManifestResourceStream( resourceName ) )
			{
				var meta = MetadataReference.CreateFromStream( stream, default, default, resourceName );
				All[resourceName] = meta;
			}
		}

		System.IO.File.AppendAllText( "/tmp/frameworkrefs_debug.txt", $"[FrameworkReferences] Loaded {All.Count} embedded refs: {string.Join(", ", All.Keys)}\n" );
	}

	static Assembly FindLoadedAssembly( string name )
	{
		var loadedAssembly = AppDomain.CurrentDomain.GetAssemblies()
			.FirstOrDefault( assembly => string.Compare( assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase ) == 0 );
		if ( loadedAssembly != null )
			return loadedAssembly;

		try
		{
			// .NET lazy loads assemblies so we might need to load it now...
			return Assembly.Load( name );
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Find a framework reference by its assembly name
	/// </summary>
	public static PortableExecutableReference FindByName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( $"cannot be null or empty", nameof( name ) );

		System.IO.File.AppendAllText( "/tmp/frameworkrefs_debug.txt", $"[FindByName] Looking for: {name}\n" );

		//
		// Find a ref assembly
		//
		if ( All.TryGetValue( $"{name}.dll", out var frameworkReference ) )
		{
			System.IO.File.AppendAllText( "/tmp/frameworkrefs_debug.txt", $"[FindByName] Found in All: {name}\n" );
			return frameworkReference;
		}

		System.IO.File.AppendAllText( "/tmp/frameworkrefs_debug.txt", $"[FindByName] Not in All, trying FindLoadedAssembly: {name}\n" );

		//
		// Find the assembly in our list of loaded assemblies
		//
		var assembly = FindLoadedAssembly( name );

		if ( assembly == null )
		{
			System.IO.File.AppendAllText( "/tmp/frameworkrefs_debug.txt", $"[FindByName] FAILED - assembly null for: {name}\n" );
			throw new System.Exception( $"Couldn't find {name}.dll" );
		}

		System.IO.File.AppendAllText( "/tmp/frameworkrefs_debug.txt", $"[FindByName] Found assembly: {name}, Location='{assembly.Location}'\n" );

		if ( string.IsNullOrEmpty( assembly.Location ) )
		{
			System.IO.File.AppendAllText( "/tmp/frameworkrefs_debug.txt", $"[FindByName] FAILED - empty Location for: {name}\n" );
			throw new System.Exception( $"Found assembly {name}.dll ({assembly}) - but can't find PortableExecutableReference" );
		}

		return MetadataReference.CreateFromFile( assembly.Location );
	}

	private static List<string> LoadEmbeddedReferenceAssemblies()
	{
		var assembly = Assembly.GetExecutingAssembly();
		var resourceNames = assembly.GetManifestResourceNames()
			.Where( name => name.EndsWith( ".dll", StringComparison.OrdinalIgnoreCase ) )
			.ToArray();

		var tempDirectory = Path.Combine( Path.GetTempPath(), "EmbeddedRefs" );
		Directory.CreateDirectory( tempDirectory );

		var referenceFiles = new List<string>();

		foreach ( var resourceName in resourceNames )
		{
			var outputPath = Path.Combine( tempDirectory, resourceName );
			using ( var resourceStream = assembly.GetManifestResourceStream( resourceName ) )
			using ( var fileStream = new FileStream( outputPath, FileMode.Create, FileAccess.Write ) )
			{
				resourceStream.CopyTo( fileStream );
			}
			referenceFiles.Add( outputPath );
		}

		return referenceFiles;
	}
}
