// RichTextKit
// Copyright © 2019-2020 Topten Software. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License"); you may 
// not use this product except in compliance with the License. You may obtain 
// a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software 
// distributed under the License is distributed on an "AS IS" BASIS, WITHOUT 
// WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the 
// License for the specific language governing permissions and limitations 
// under the License.

using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Text;

namespace Topten.RichTextKit
{
	/// <summary>
	/// The FontMapper class is responsible for mapping style typeface information
	/// to an SKTypeface.
	/// </summary>
	public class FontMapper
	{
		/// <summary>
		/// Constructs a new FontMapper instnace
		/// </summary>
		public FontMapper()
		{
		}

		// Linux font fallbacks for common Windows fonts
		private static readonly Dictionary<string, string[]> FontFallbacks = new Dictionary<string, string[]>( StringComparer.OrdinalIgnoreCase )
		{
			{ "Arial", new[] { "Liberation Sans", "DejaVu Sans", "Noto Sans", "FreeSans", "sans-serif" } },
			{ "Helvetica", new[] { "Liberation Sans", "DejaVu Sans", "Noto Sans", "FreeSans", "sans-serif" } },
			{ "Times New Roman", new[] { "Liberation Serif", "DejaVu Serif", "Noto Serif", "FreeSerif", "serif" } },
			{ "Courier New", new[] { "Liberation Mono", "DejaVu Sans Mono", "Noto Mono", "FreeMono", "monospace" } },
			{ "Consolas", new[] { "Liberation Mono", "DejaVu Sans Mono", "Noto Mono", "FreeMono", "monospace" } },
			{ "Segoe UI", new[] { "Liberation Sans", "DejaVu Sans", "Noto Sans", "Ubuntu", "sans-serif" } },
			{ "Tahoma", new[] { "Liberation Sans", "DejaVu Sans", "Noto Sans", "FreeSans", "sans-serif" } },
			{ "Verdana", new[] { "Liberation Sans", "DejaVu Sans", "Noto Sans", "FreeSans", "sans-serif" } },
		};

		// Helper to validate if a typeface is usable
		private static bool IsTypefaceValid( SKTypeface typeface )
		{
			if ( typeface == null ) return false;
			if ( string.IsNullOrEmpty( typeface.FamilyName ) ) return false;

			// Additional check - try to create a font with it
			try
			{
				using ( var font = new SKFont( typeface, 12 ) )
				{
					// If we can create a font, it's valid
					return font != null;
				}
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Maps a given style to a specific typeface
		/// </summary>
		/// <param name="style">The style to be mapped</param>
		/// <param name="ignoreFontVariants">Indicates the mapping should ignore font variants (use to get font for ellipsis)</param>
		/// <returns>A mapped typeface</returns>
		public virtual SKTypeface TypefaceFromStyle( IStyle style, bool ignoreFontVariants )
		{
			// Extra weight for superscript/subscript
			int extraWeight = 0;
			if ( !ignoreFontVariants && (style.FontVariant == FontVariant.SuperScript || style.FontVariant == FontVariant.SubScript) )
			{
				extraWeight += 100;
			}

			var weight = (SKFontStyleWeight)(style.FontWeight + extraWeight);
			var slant = style.FontItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;

			Console.Error.WriteLine( $"[FontMapper] TypefaceFromStyle called for family='{style.FontFamily}' weight={style.FontWeight}" );

			// Try the requested font family first
			var typeface = SKTypeface.FromFamilyName( style.FontFamily, weight, 0, slant );
			Console.Error.WriteLine( $"[FontMapper] FromFamilyName('{style.FontFamily}') returned: {typeface?.FamilyName ?? "null"}" );
			if ( IsTypefaceValid( typeface ) )
				return typeface;

			// Try fallback fonts for this family (primarily for Linux)
			if ( FontFallbacks.TryGetValue( style.FontFamily ?? "Arial", out var fallbacks ) )
			{
				foreach ( var fallback in fallbacks )
				{
					typeface = SKTypeface.FromFamilyName( fallback, weight, 0, slant );
					if ( IsTypefaceValid( typeface ) )
						return typeface;
				}
			}

			// Try generic fallbacks
			string[] genericFallbacks = { "Liberation Sans", "DejaVu Sans", "Noto Sans", "Ubuntu", "FreeSans", "sans-serif", "serif", "monospace" };
			foreach ( var fallback in genericFallbacks )
			{
				typeface = SKTypeface.FromFamilyName( fallback, weight, 0, slant );
				if ( IsTypefaceValid( typeface ) )
					return typeface;
			}

			// Final fallback - use SkiaSharp's default which should always work
			Console.Error.WriteLine( $"[FontMapper] Trying CreateDefault()..." );
			typeface = SKTypeface.CreateDefault();
			Console.Error.WriteLine( $"[FontMapper] CreateDefault() returned: {typeface?.FamilyName ?? "null"}" );
			if ( typeface != null )
				return typeface;

			// Absolute last resort - this should never happen but just in case
			// Try to get any installed font from the font manager
			var fontManager = SKFontManager.Default;
			Console.Error.WriteLine( $"[FontMapper] FontManager.Default: {fontManager}, FontFamilyCount: {fontManager?.FontFamilyCount ?? 0}" );
			if ( fontManager != null && fontManager.FontFamilyCount > 0 )
			{
				var families = fontManager.FontFamilies;
				Console.Error.WriteLine( $"[FontMapper] Available font families: {string.Join( ", ", families.Take( 10 ) )}" );
				foreach ( var family in families )
				{
					typeface = SKTypeface.FromFamilyName( family, weight, 0, slant );
					if ( typeface != null )
						return typeface;
				}
			}

			// If we still have nothing, return the default (may be null)
			Console.Error.WriteLine( $"[FontMapper] FAILED to find any font!" );
			return SKTypeface.CreateDefault();
		}

		/// <summary>
		/// The default font mapper instance.  
		/// </summary>
		/// <remarks>
		/// The default font mapper is used by any TextBlocks that don't 
		/// have an explicit font mapper set (see the <see cref="TextBlock.FontMapper"/> property).
		/// 
		/// Replacing the default font mapper allows changing the font mapping
		/// for all text blocks that don't have an explicit mapper assigned.
		/// </remarks>
		public static FontMapper Default = new FontMapper();
	}
}
