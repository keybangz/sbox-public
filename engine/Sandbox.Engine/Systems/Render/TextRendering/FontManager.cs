using SkiaSharp;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Topten.RichTextKit;

namespace Sandbox;

file class SKTypefaceEqualityComparer : IEqualityComparer<SKTypeface>
{
	public bool Equals( SKTypeface x, SKTypeface y )
	{
		return x.FamilyName == y.FamilyName
			&& x.FontWeight == y.FontWeight
			&& x.FontSlant == y.FontSlant;
	}

	public int GetHashCode( [DisallowNull] SKTypeface obj )
	{
		return HashCode.Combine( obj.FamilyName, obj.FontWeight, obj.FontSlant );
	}
}

internal class FontManager : FontMapper
{
	public static FontManager Instance = new FontManager();

	public static ConcurrentDictionary<int, SKTypeface> LoadedFonts = new();

	static Dictionary<int, SKTypeface> Cache = new();

	public static IEnumerable<string> FontFamilies => LoadedFonts.Values.Select( x => x.FamilyName ).Distinct();

	private void Load( System.IO.Stream stream )
	{
		if ( stream == null )
		{
			Log.Warning( "[FontManager] Load called with null stream" );
			return;
		}

		try
		{
			var face = SKTypeface.FromStream( stream );
			if ( face is null )
			{
				Log.Warning( "[FontManager] SKTypeface.FromStream returned null" );
				return;
			}

			var hash = HashCode.Combine( face.FamilyName, face.FontWeight, face.FontSlant );

			LoadedFonts[hash] = face;

			Log.Info( $"[FontManager] Loaded font {face.FamilyName} weight {face.FontWeight}" );
		}
		catch ( System.Exception ex )
		{
			Log.Error( $"[FontManager] Failed to load font: {ex.Message}" );
		}
	}

	List<FileWatch> watchers = new();

	public void LoadAll( BaseFileSystem fileSystem )
	{
		Log.Info( $"[FontManager] LoadAll called with filesystem: {fileSystem}" );

		// If we're loading new fonts, we may have cached it already
		Cache.Clear();

		try
		{
			var fontFiles = fileSystem.FindFile( "/fonts/", "*.ttf" )
				.Union( fileSystem.FindFile( "/fonts/", "*.otf" ) )
				.ToList();

			Log.Info( $"[FontManager] Found {fontFiles.Count} font files in /fonts/" );

			foreach ( var font in fontFiles )
			{
				Log.Info( $"[FontManager] Loading font: {font}" );
				try
				{
					var stream = fileSystem.OpenRead( $"/fonts/{font}" );
					if ( stream != null )
					{
						Load( stream );
					}
					else
					{
						Log.Warning( $"[FontManager] OpenRead returned null for: /fonts/{font}" );
					}
				}
				catch ( System.Exception ex )
				{
					Log.Error( $"[FontManager] Error loading font {font}: {ex.Message}" );
				}
			}

			Log.Info( $"[FontManager] Loaded {LoadedFonts.Count} fonts total" );
		}
		catch ( System.Exception ex )
		{
			Log.Error( $"[FontManager] Error finding font files: {ex.Message}" );
		}

		// Load any new fonts
		try
		{
			var ttfWatch = fileSystem.Watch( $"*.ttf" );
			ttfWatch.OnChanges += ( w ) => OnFontFilesChanged( w, fileSystem );
			var otfWatch = fileSystem.Watch( $"*.otf" );
			otfWatch.OnChanges += ( w ) => OnFontFilesChanged( w, fileSystem );

			watchers.Add( ttfWatch );
			watchers.Add( otfWatch );
		}
		catch ( System.Exception ex )
		{
			Log.Warning( $"[FontManager] Error setting up font watchers: {ex.Message}" );
		}
	}

	private void OnFontFilesChanged( FileWatch w, BaseFileSystem fs )
	{
		Cache.Clear();

		foreach ( var file in w.Changes )
		{
			Load( fs.OpenRead( file ) );
		}
	}

	/// <summary>
	/// Tries to get the best matching font for the given style.
	/// Will return a matching font family with the closest font weight and optionally slant.
	/// </summary>
	private SKTypeface GetBestTypeface( IStyle style )
	{
		// Must be of same family
		var familyFonts = LoadedFonts.Values.Where( x => string.Equals( x.FamilyName, style.FontFamily, StringComparison.OrdinalIgnoreCase ) );
		if ( !familyFonts.Any() ) return null;

		// Get matching slants, if no matching fallback to regular
		var slantFonts = familyFonts.Where( x => x.IsItalic == style.FontItalic );
		if ( slantFonts.Any() ) familyFonts = slantFonts;

		// Finally get the closest font weight
		return familyFonts.Select( x => new { x, distance = Math.Abs( x.FontWeight - style.FontWeight ) } )
			.OrderBy( x => x.distance )
			.First().x;
	}

	public override SKTypeface TypefaceFromStyle( IStyle style, bool ignoreFontVariants )
	{
		var hash = HashCode.Combine( style.FontFamily, style.FontWeight, style.FontItalic );

		lock ( Cache )
		{
			if ( Cache.TryGetValue( hash, out var cachedFace ) ) return cachedFace;
		}

		var f = GetBestTypeface( style );

		// Fallback on system font
		if ( f is null )
		{
			try
			{
				f = Default.TypefaceFromStyle( style, ignoreFontVariants );
			}
			catch ( System.Exception ex )
			{
				Log.Warning( $"[FontManager] Default.TypefaceFromStyle failed: {ex.Message}" );
			}
		}

		// Final fallback - try to create any default typeface
		if ( f is null )
		{
			try
			{
				f = SKTypeface.CreateDefault();
				if ( f is null )
				{
					// Try to get first loaded font as absolute fallback
					if ( LoadedFonts.Count > 0 )
					{
						f = LoadedFonts.Values.First();
						Log.Warning( $"[FontManager] Using fallback font: {f?.FamilyName}" );
					}
				}
			}
			catch ( System.Exception ex )
			{
				Log.Warning( $"[FontManager] SKTypeface.CreateDefault failed: {ex.Message}" );
			}
		}

		if ( f is null )
		{
			Log.Error( $"[FontManager] Could not find any font for family '{style.FontFamily}'. LoadedFonts count: {LoadedFonts.Count}" );
		}

		lock ( Cache )
		{
			Cache[hash] = f;
		}

		return f;
	}

	public void Reset()
	{
		foreach ( var watcher in watchers )
		{
			watcher.Dispose();
		}
		watchers.Clear();

		foreach ( var (_, font) in LoadedFonts )
		{
			font?.Dispose();
		}

		foreach ( var (_, font) in Cache )
		{
			font?.Dispose();
		}

		LoadedFonts.Clear();
		Cache.Clear();
	}
}

