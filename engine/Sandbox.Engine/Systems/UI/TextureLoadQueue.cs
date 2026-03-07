using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Sandbox.UI;

/// <summary>
/// Manages incremental texture loading for UI to prevent frame freezes.
/// Textures are queued and loaded incrementally across frames.
/// </summary>
public static class TextureLoadQueue
{
	private static readonly ConcurrentQueue<PendingTextureLoad> _queue = new();
	private static readonly HashSet<int> _queuedHashes = new();
	private static readonly object _lock = new();

	/// <summary>
	/// Maximum time in milliseconds to spend loading textures per frame
	/// </summary>
	public const int MaxLoadTimePerFrameMs = 32; // Allow more time for texture loading

	/// <summary>
	/// Maximum number of textures to load per frame
	/// </summary>
	public const int MaxLoadsPerFrame = 5; // Load more textures per frame

	private struct PendingTextureLoad
	{
		public Lazy<Texture> LazyTexture;
		public WeakReference<Panel> PanelRef;
		public int Hash;
	}

	/// <summary>
	/// Queue a lazy texture for loading. Returns immediately.
	/// The texture will be loaded on a future frame.
	/// </summary>
	public static void QueueLoad( Lazy<Texture> lazyTexture, Panel panel )
	{
		if ( lazyTexture == null || lazyTexture.IsValueCreated )
			return;

		// Use object hash to deduplicate
		int hash = lazyTexture.GetHashCode();
		
		lock ( _lock )
		{
			if ( _queuedHashes.Contains( hash ) )
				return;
			
			_queuedHashes.Add( hash );
		}

		_queue.Enqueue( new PendingTextureLoad
		{
			LazyTexture = lazyTexture,
			PanelRef = panel != null ? new WeakReference<Panel>( panel ) : null,
			Hash = hash
		} );
	}

	/// <summary>
	/// Process queued texture loads. Call this once per frame on main thread.
	/// Will load up to MaxLoadsPerFrame textures or until MaxLoadTimePerFrameMs is exceeded.
	/// </summary>
	public static void ProcessQueue()
	{
		if ( _queue.IsEmpty )
			return;

		long startTime = Environment.TickCount64;
		int loadCount = 0;

		while ( loadCount < MaxLoadsPerFrame && _queue.TryDequeue( out var pending ) )
		{
			// Check time budget BEFORE loading
			if ( loadCount > 0 )
			{
				var elapsed = Environment.TickCount64 - startTime;
				if ( elapsed > MaxLoadTimePerFrameMs )
				{
					// Put it back and process next frame
					_queue.Enqueue( pending );
					break;
				}
			}

			long loadStart = Environment.TickCount64;
			try
			{
				// Trigger the lazy load - this may block but we're limiting per-frame
				var texture = pending.LazyTexture.Value;

				long loadTime = Environment.TickCount64 - loadStart;
				if ( loadTime > 100 )
				{
					System.IO.File.AppendAllText( "/tmp/block_debug.txt", $"[TEXTURE_QUEUE] Single load took {loadTime}ms, queue remaining: {_queue.Count}\n" );
				}

				// Mark panel as render dirty so it re-renders with the loaded texture
				if ( texture != null && texture != Texture.Invalid )
				{
					if ( pending.PanelRef?.TryGetTarget( out var panel ) == true && panel.IsValid )
					{
						panel.IsRenderDirty = true;
					}
				}
			}
			catch ( Exception )
			{
				// Texture load failed, ignore
			}
			finally
			{
				lock ( _lock )
				{
					_queuedHashes.Remove( pending.Hash );
				}
				loadCount++;
			}
		}
	}

	/// <summary>
	/// Get the number of pending texture loads
	/// </summary>
	public static int PendingCount => _queue.Count;
}

