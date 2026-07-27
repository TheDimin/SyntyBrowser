using System;
using System.Text.RegularExpressions;

namespace Editor.Tools.SyntyBrowser;

public static partial class SyntyAssetNaming
{
	private static readonly HashSet<string> StructuralTokens = new( StringComparer.OrdinalIgnoreCase )
	{
		"SM", "SK", "ENV", "PROP", "MESH", "PREFAB", "BLD", "FX"
	};

	public static string ToDisplayName( string sourceName )
	{
		var value = sourceName?.Trim() ?? "";
		value = Regex.Replace( value, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=\d)|(?<=\d)(?=[A-Za-z])", " " );
		var tokens = Regex.Split( value, @"[^A-Za-z0-9]+" )
			.Where( token => !string.IsNullOrWhiteSpace( token ) )
			.ToList();
		while ( tokens.Count > 0 && StructuralTokens.Contains( tokens[0] ) )
			tokens.RemoveAt( 0 );

		return tokens.Count == 0
			? sourceName
			: string.Join( " ", tokens.Select( FormatToken ) );
	}

	private static string FormatToken( string token )
	{
		if ( token.All( char.IsDigit ) || token.Length <= 3 && token.All( char.IsUpper ) )
			return token;
		return char.ToUpperInvariant( token[0] ) + token[1..].ToLowerInvariant();
	}
}
