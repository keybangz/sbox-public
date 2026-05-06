namespace Sandbox.Engine;

/// <summary>
/// Lightweight input trace logger. Only emits when SBOX_INPUT_TRACE=1.
/// Logs state transitions only — not per-frame data.
/// </summary>
internal static class InputLog
{
	private static readonly bool _traceEnabled =
		Environment.GetEnvironmentVariable( "SBOX_INPUT_TRACE" ) == "1";

	/// <summary>
	/// Log a state transition message. Only active when SBOX_INPUT_TRACE=1.
	/// </summary>
	public static void Trace( string message )
	{
#if !WIN
		if ( _traceEnabled )
		{
			Log.Info( message );
		}
#endif
	}
}
