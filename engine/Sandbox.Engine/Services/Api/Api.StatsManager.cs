using System.Threading;

namespace Sandbox;

internal static partial class Api
{
	/// <summary>
	/// This attempts to be a unique identifier for the launch of a game.
	/// </summary>
	internal static string LaunchGuid = Guid.NewGuid().ToString( "N" );

	internal static partial class Stats
	{
		private static RealTimeSince TimeSincePosted;
		private static RealTimeSince TimeSinceFlushed;
		private static RealTimeSince TimeSinceForceFlushed;
		private static List<StatRecord> Pending = new();
		private static SemaphoreSlim ForceFlushSemaphore = new SemaphoreSlim( 1, 1 );
		private static SemaphoreSlim FlushSemaphore = new SemaphoreSlim( 1, 1 );

		/// <summary>
		/// Flush the stats and wait until they have been ingested by the backend, 
		/// at which point they should be available in stats and leaderboard queries.
		/// </summary>
		public static async Task FlushWithBookmarkAsync( string package, bool useBookmark, CancellationToken token )
		{
			// no pending stats, and we flushed over 5 seconds ago
			// so seems like they should be available by now.
			if ( Pending.Count == 0 && TimeSinceFlushed > 5 )
			{
				//Log.Info( "Nothing pending - returning!" );
				return;
			}

			if ( TimeSinceForceFlushed < 3 )
				return;

			// one at a time
			// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
			await ForceFlushSemaphore.WaitAsync().ConfigureAwait( false );

			try
			{
				// nothing to do
				if ( Pending.Count == 0 )
					return;

				// minimum period between
				if ( TimeSinceForceFlushed < 30 )
					// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
					await Task.Delay( TimeSpan.FromSeconds( 30 - TimeSinceForceFlushed.Relative ) ).ConfigureAwait( false );

				var guid = Guid.NewGuid().ToString();

				if ( useBookmark )
				{
					// create a special stat for us to determine ingress
					SetValue( "facepunch.stats", "bookmark", 1, new Dictionary<string, object> { { "source", package } } );
				}

				// force flush it
				// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
				await ForceFlushAsync().ConfigureAwait( false );

				if ( !useBookmark )
					return;

				if ( token.IsCancellationRequested )
					return;

				// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
				await Task.Delay( 1000 ).ConfigureAwait( false );

				for ( int i = 0; i < 10; i++ )
				{
					if ( token.IsCancellationRequested )
						return;

					try
					{
						// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
						var r = await Sandbox.Backend.Stats.GetBookmark( guid ).ConfigureAwait( false );
						if ( r == "found" ) break;
					}
					catch ( System.Exception e )
					{
						Log.Warning( e, $"Error when waiting for ingestion" );
					}

					// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
					await Task.Delay( 1000 + 500 * i ).ConfigureAwait( false );
				}
			}
			finally
			{
				ForceFlushSemaphore.Release();
				TimeSinceForceFlushed = 0;
			}
		}

		/// <summary>
		/// Force an immediate flush of all events
		/// </summary>
		internal static void ForceFlush()
		{
			TimeSincePosted = 0;
			if ( Pending.Count == 0 ) return;
			_ = FlushStats();
		}

		internal static async Task Shutdown()
		{
			if ( Pending.Count == 0 ) return;
			// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
			await FlushStats().ConfigureAwait( false );
		}

		/// <summary>
		/// Force an immediate flush of the stats and wait until it's done
		/// </summary>
		internal static async Task ForceFlushAsync()
		{
			TimeSincePosted = 0;
			if ( Pending.Count == 0 ) return;

			// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
			await FlushStats().ConfigureAwait( false );
		}

		/// <summary>
		/// Post a batch of analytic events. Analytic events are things like compile or load times to 
		/// help us find, fix and track performance issues.
		/// </summary>
		internal static void TickStats()
		{
			if ( TimeSincePosted < 30 && Pending.Count < 200 )
				return;

			ForceFlush();
		}

		private static async Task FlushStats()
		{
			if ( Pending.Count == 0 )
				return;

			// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
			await FlushSemaphore.WaitAsync().ConfigureAwait( false );

			// Take the records locally to clear the queue
			var records = Pending.ToArray();
			Pending.Clear();

			try
			{
				// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
				await PostStatsAsync( records ).ConfigureAwait( false );
				TimeSinceFlushed = 0;
			}
			catch ( System.Exception e )
			{
				Log.Warning( e, $"Exception when flushing stats ({e.Message})" );
			}
			finally
			{
				FlushSemaphore.Release();
			}
		}

		/// <summary>
		/// Post a batch of analytic events. Analytic events are things like compile or load times to 
		/// help us find, fix and track performance issues.
		/// </summary>
		internal static async Task PostStatsAsync( StatRecord[] records )
		{
			if ( records.Length == 0 )
				return;

			if ( Sandbox.Backend.Stats is null )
				return;

			if ( Application.IsDedicatedServer )
				return;

			var values = new
			{
				Events = records,
				Launch = LaunchGuid
			};

			// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
			await Sandbox.Backend.Stats.Submit( values ).ConfigureAwait( false );
		}

		static bool IsAppropriateName( string name )
		{
			foreach ( var c in name )
			{
				if ( char.IsLetterOrDigit( c ) ) continue;
				if ( c == '.' ) continue;
				if ( c == '-' ) continue;
				if ( c == '_' ) continue;

				return false;
			}

			return true;
		}

		public static void AddIncrement( string package, string statname, double value, Dictionary<string, object> data )
		{
			if ( statname is null || statname.Length <= 0 || !IsAppropriateName( statname ) ) return;

			var stat = Pending.FirstOrDefault( x => x.IsIncrement && x.Package == package && x.Name == statname && x.Session == Api.SessionId.ToString() );
			if ( stat == null )
			{
				stat = new StatRecord( package, statname )
				{
					IsIncrement = true,
					MinValue = value,
					MaxValue = value,
					Session = Api.SessionId.ToString(),
					SessionSeconds = (int)Api.Activity.SessionSeconds,
					Data = data
				};

				Pending.Add( stat );
			}

			stat.Compounds++;
			stat.Value += value;
			stat.MinValue = Math.Min( stat.MinValue, value );
			stat.MaxValue = Math.Min( stat.MaxValue, value );
			stat.Updated = DateTime.UtcNow;
		}

		public static void SetValue( string package, string statname, double value, Dictionary<string, object> data )
		{
			if ( statname is null || statname.Length <= 0 || !IsAppropriateName( statname ) ) return;

			Pending.RemoveAll( x => !x.IsIncrement && x.Package == package && x.Name == statname && x.Session == Api.SessionId.ToString() );

			var stat = new StatRecord( package, statname )
			{
				IsIncrement = false,
				Compounds = 1,
				Value = value,
				MinValue = value,
				MaxValue = value,
				Session = Api.SessionId.ToString(),
				SessionSeconds = (int)Api.Activity.SessionSeconds,
				Data = data
			};

			Pending.Add( stat );
		}

		internal class StatRecord
		{
			public StatRecord( string package, string name )
			{
				Package = package;
				Name = name;
				Created = DateTime.UtcNow;
				Updated = DateTime.UtcNow;
			}

			/// <summary>
			/// The package ident (lowercase [org.package])
			/// </summary>
			public string Package { get; set; }

			/// <summary>
			/// The name of the stat (lowercase, no spaces)
			/// </summary>
			public string Name { get; set; }

			/// <summary>
			/// When this stat was created
			/// </summary>
			public DateTimeOffset Created { get; set; }

			/// <summary>
			/// When this stat was updated
			/// </summary>
			public DateTimeOffset Updated { get; set; }

			/// <summary>
			/// If this is an increment
			/// </summary>
			public bool IsIncrement { get; set; }

			/// <summary>
			/// If this represents multiple calls, this is the number of calls
			/// </summary>
			public int Compounds { get; set; }

			/// <summary>
			/// If this represents multiple calls, this is the smallest
			/// </summary>
			public double MinValue { get; set; }

			/// <summary>
			/// If this represents multiple calls, this is the largest
			/// </summary>
			public double MaxValue { get; set; }

			/// <summary>
			/// The actual value
			/// </summary>
			public double Value { get; set; }

			/// <summary>
			/// Additional context. Map etc..
			/// </summary>
			public string Context { get; set; }

			/// <summary>
			/// The current session (or null if #local)
			/// </summary>
			public string Session { get; set; }

			/// <summary>
			/// The current time in session
			/// </summary>
			public int SessionSeconds { get; set; }

			/// <summary>
			/// Should be a dynamic object
			/// </summary>
			public Dictionary<string, object> Data { get; set; }
		}
	}
}
