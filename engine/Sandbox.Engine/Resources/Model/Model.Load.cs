using Sandbox.Mounting;

namespace Sandbox;

public partial class Model
{
	// Set to true to enable diagnostic logging for model loading (writes to /tmp/sbox_model_load.log)
	private static bool _modelLoadLoggingEnabled = false;

	private static void ModelLoadLog( string message )
	{
		if ( !_modelLoadLoggingEnabled )
			return;

		try
		{
			var logPath = "/tmp/sbox_model_load.log";
			var timestamp = DateTime.Now.ToString( "HH:mm:ss.fff" );
			var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
			var logLine = $"[{timestamp}][T{threadId}] {message}\n";
			System.IO.File.AppendAllText( logPath, logLine );
		}
		catch
		{
			// Ignore logging failures
		}
	}

	/// <summary>
	/// Load a model by file path.
	/// </summary>
	/// <param name="filename">The file path to load as a model.</param>
	/// <returns>The loaded model, or null</returns>
	public static Model Load( string filename )
	{
		ThreadSafe.AssertIsMainThread();

		ModelLoadLog( $"Load START: '{filename}'" );

		if ( string.IsNullOrWhiteSpace( filename ) )
		{
			ModelLoadLog( $"Load: empty filename, returning Error model" );
			return Error;
		}

		filename = filename?.Replace( ".vmdl_c", ".vmdl" );
		ModelLoadLog( $"Load: normalized filename = '{filename}'" );

		if ( Sandbox.Mounting.Directory.TryLoad( filename, ResourceType.Model, out object model ) && model is Model m )
		{
			ModelLoadLog( $"Load: Directory.TryLoad succeeded, model.IsError={m.IsError}" );
			return m;
		}

		// On Linux, try to resolve the path case-insensitively before passing to native
		var resolvedFilename = filename;
		if ( OperatingSystem.IsLinux() )
		{
			// Try fast cache lookup first (uses prebuilt path mapping)
			var cachedPath = EngineFileSystem.ResolvePathCase( filename );
			if ( cachedPath != filename )
			{
				ModelLoadLog( $"Load: Cache hit for case resolution: '{filename}' -> '{cachedPath}'" );
				resolvedFilename = cachedPath;
			}
			else
			{
				// Try resolving the .vmdl_c file (compiled resources are what's on disk)
				var compiledFilename = filename.Replace( ".vmdl", ".vmdl_c" );
				var cachedCompiledPath = EngineFileSystem.ResolvePathCase( compiledFilename );
				if ( cachedCompiledPath != compiledFilename )
				{
					// Convert back to .vmdl for the native engine
					resolvedFilename = cachedCompiledPath.Replace( ".vmdl_c", ".vmdl" );
					ModelLoadLog( $"Load: Cache hit for case resolution (via .vmdl_c): '{filename}' -> '{resolvedFilename}'" );
				}
				else
				{
					// Fallback to slow filesystem scan
					var (caseResolved, fullPath) = EngineFileSystem.FindFileCaseInsensitiveWithFullPath( filename );
					if ( caseResolved != null )
					{
						ModelLoadLog( $"Load: Filesystem scan resolution: '{filename}' -> '{caseResolved}'" );
						resolvedFilename = caseResolved;
					}
					else
					{
						ModelLoadLog( $"Load: Case resolution failed for '{filename}'" );
					}
				}
			}
		}

		ModelLoadLog( $"Load: calling NativeGlue.Resources.GetModel('{resolvedFilename}')..." );

		var native = NativeGlue.Resources.GetModel( resolvedFilename );
		ModelLoadLog( $"Load: GetModel returned native.IsNull={native.IsNull}, native.IsStrongHandleValid={(native.IsNull ? "N/A" : native.IsStrongHandleValid().ToString())}" );

		if ( !native.IsNull && native.IsStrongHandleValid() )
		{
			ModelLoadLog( $"Load: native.IsError={native.IsError()}, native.GetModelName='{native.GetModelName()}'" );
		}

		var result = FromNative( native, name: filename );
		ModelLoadLog( $"Load END: result is null={result is null}, IsError={(result is null ? "N/A" : result.IsError.ToString())}" );

		return result;
	}

	/// <summary>
	/// Load a model by file path.
	/// </summary>
	/// <param name="filename">The file path to load as a model.</param>
	/// <returns>The loaded model, or null</returns>
	public static async Task<Model> LoadAsync( string filename )
	{
		ThreadSafe.AssertIsMainThread();

		if ( string.IsNullOrWhiteSpace( filename ) )
			return Error;

		filename = filename?.Replace( ".vmdl_c", ".vmdl" );

		if ( await Sandbox.Mounting.Directory.TryLoadAsync( filename, ResourceType.Model ) is Model m )
			return m;

		using var manifest = AsyncResourceLoader.Load( filename );
		if ( manifest is not null )
		{
			await manifest.WaitForLoad();
		}

		// TODO - make async
		return Load( filename );
	}
}
