using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Editor.Tools.SyntyBrowser;

public readonly record struct FbxMediaPathSanitizationResult( byte[] Content, int ReplacementCount );

public static class FbxEmbeddedMediaPathSanitizer
{
	private const string NeutralMaterialPath = "materials/default/default.vmat";
	private static readonly Regex AbsoluteMediaPathRegex = new(
		@"(?<![A-Za-z0-9_])(?<path>[A-Za-z]:[\\/][\x20-\x21\x23-\x7E]*?\.(?:bmp|jpeg|jpg|png|psd|tga|tif|tiff|vmat))",
		RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase );

	public static FbxMediaPathSanitizationResult Sanitize( byte[] content )
	{
		ArgumentNullException.ThrowIfNull( content );
		var source = Encoding.Latin1.GetString( content );
		var replacementCount = 0;
		var sanitized = AbsoluteMediaPathRegex.Replace( source, match =>
		{
			replacementCount++;
			return BuildLengthPreservingNeutralPath( match.Length );
		} );
		return new FbxMediaPathSanitizationResult( Encoding.Latin1.GetBytes( sanitized ), replacementCount );
	}

	public static int SanitizeFile( string path )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( path );
		var result = Sanitize( File.ReadAllBytes( path ) );
		if ( result.ReplacementCount > 0 )
			File.WriteAllBytes( path, result.Content );
		return result.ReplacementCount;
	}

	private static string BuildLengthPreservingNeutralPath( int length )
	{
		var padding = length - NeutralMaterialPath.Length;
		if ( padding < 0 || padding is 1 or 3 )
			throw new InvalidDataException( $"Absolute FBX media path length {length} cannot be sanitized without changing binary offsets." );

		var prefix = new StringBuilder( padding );
		if ( padding % 2 != 0 )
		{
			prefix.Append( "x/../" );
			padding -= 5;
		}
		while ( padding > 0 )
		{
			prefix.Append( "./" );
			padding -= 2;
		}
		return prefix.Append( NeutralMaterialPath ).ToString();
	}
}
