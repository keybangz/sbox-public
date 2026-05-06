using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Text;

namespace Topten.RichTextKit
{
	class DefaultCharacterMatcher : ICharacterMatcher
	{
		public DefaultCharacterMatcher()
		{

		}

		SKFontManager _fontManager = SKFontManager.Default;

		// Linux fallback font families
		private static readonly string[] LinuxFallbackFamilies = new[]
		{
			"Liberation Sans", "DejaVu Sans", "Noto Sans", "Ubuntu", "FreeSans",
			"Liberation Serif", "DejaVu Serif", "Noto Serif", "FreeSerif",
			"Liberation Mono", "DejaVu Sans Mono", "Noto Mono", "FreeMono"
		};

		/// <inheritdoc />
		public SKTypeface MatchCharacter( string familyName, int weight, int width, SKFontStyleSlant slant, string[] bcp47, int character )
		{
			// Try the requested family first
			var result = _fontManager.MatchCharacter( familyName, weight, width, slant, bcp47, character );
			if ( result != null )
				return result;

			// Try Linux fallback families
			foreach ( var fallback in LinuxFallbackFamilies )
			{
				result = _fontManager.MatchCharacter( fallback, weight, width, slant, bcp47, character );
				if ( result != null )
					return result;
			}

			// Last resort - try to get any font that supports this character
			result = _fontManager.MatchCharacter( null, weight, width, slant, bcp47, character );
			if ( result != null )
				return result;

			// Absolute last resort - return default typeface
			return SKTypeface.CreateDefault();
		}
	}
}
