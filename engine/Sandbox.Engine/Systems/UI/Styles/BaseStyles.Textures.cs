using System;
using System.Globalization;

namespace Sandbox.UI
{
	public abstract partial class BaseStyles
	{
		internal Lazy<Texture> _backgroundImage;
		internal Lazy<Texture> _maskImage;
		internal Lazy<Texture> _borderImageSource;

		public Texture BackgroundImage
		{
			get
			{
				if ( _backgroundImage == null ) return null;

				return _backgroundImage?.Value;
			}

			set
			{
				if ( _backgroundImage?.Value == value )
					return;

				_backgroundImage = new Lazy<Texture>( value );
				Dirty();
			}
		}

		/// <summary>
		/// Check if background image is set without triggering lazy loading
		/// </summary>
		public bool HasBackgroundImageSet => _backgroundImage != null;

		/// <summary>
		/// Check if background image is already loaded (value created) without triggering load
		/// </summary>
		public bool IsBackgroundImageLoaded => _backgroundImage?.IsValueCreated == true;

		/// <summary>
		/// Get background image if already loaded, otherwise queue it for loading.
		/// Returns null if not yet loaded - panel will re-render when loaded.
		/// </summary>
		/// <param name="panel">The panel requesting this texture, used to mark it dirty when loaded</param>
		public Texture GetBackgroundImageIfLoaded( Panel panel = null )
		{
			if ( _backgroundImage == null ) return null;

			// If already loaded, return it immediately
			if ( _backgroundImage.IsValueCreated )
				return _backgroundImage.Value;

			// Queue for incremental loading on main thread
			TextureLoadQueue.QueueLoad( _backgroundImage, panel );
			return null;
		}

		/// <summary>
		/// Get background image if already loaded (no panel reference for queueing).
		/// Prefer GetBackgroundImageIfLoaded(panel) when panel is available.
		/// </summary>
		public Texture BackgroundImageIfLoaded => GetBackgroundImageIfLoaded( null );

		public Texture MaskImage
		{
			get
			{
				if ( _maskImage == null ) return null;

				return _maskImage?.Value;
			}

			set
			{
				if ( _maskImage?.Value == value )
					return;

				_maskImage = new Lazy<Texture>( value );
				Dirty();
			}
		}

		public Texture BorderImageSource
		{
			get
			{
				if ( _borderImageSource == null ) return null;

				return _borderImageSource?.Value;
			}

			set
			{
				if ( _borderImageSource?.Value == value )
					return;

				_borderImageSource = new Lazy<Texture>( value );
				Dirty();
			}
		}

		/// <summary>
		/// Check if border image source is set without triggering lazy loading
		/// </summary>
		public bool HasBorderImageSourceSet => _borderImageSource != null;

		/// <summary>
		/// Check if border image source is already loaded (value created) without triggering load
		/// </summary>
		public bool IsBorderImageSourceLoaded => _borderImageSource?.IsValueCreated == true;

		/// <summary>
		/// Get border image source if already loaded, otherwise queue it for loading.
		/// Returns null if not yet loaded - panel will re-render when loaded.
		/// </summary>
		/// <param name="panel">The panel requesting this texture, used to mark it dirty when loaded</param>
		public Texture GetBorderImageSourceIfLoaded( Panel panel = null )
		{
			if ( _borderImageSource == null ) return null;

			// If already loaded, return it immediately
			if ( _borderImageSource.IsValueCreated )
				return _borderImageSource.Value;

			// Queue for incremental loading on main thread
			TextureLoadQueue.QueueLoad( _borderImageSource, panel );
			return null;
		}

		/// <summary>
		/// Get border image source if already loaded (no panel reference for queueing).
		/// Prefer GetBorderImageSourceIfLoaded(panel) when panel is available.
		/// </summary>
		public Texture BorderImageSourceIfLoaded => GetBorderImageSourceIfLoaded( null );

	}
}
