using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Editor.Tools.SyntyBrowser;

internal static class SyntyTextureLocator
{
	private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> PackIndexes = new( StringComparer.OrdinalIgnoreCase );
	private static readonly object Sync = new();
	private static readonly HashSet<string> Extensions = new( StringComparer.OrdinalIgnoreCase )
	{
		".png", ".tga", ".jpg", ".jpeg"
	};

	public static string Find( string packRoot, string textureHint )
	{
		if ( string.IsNullOrWhiteSpace( packRoot ) || string.IsNullOrWhiteSpace( textureHint ) )
			return null;

		IReadOnlyDictionary<string, string> index;
		lock ( Sync )
		{
			if ( !PackIndexes.TryGetValue( packRoot, out index ) )
			{
				index = Directory.EnumerateFiles( packRoot, "*.*", SearchOption.AllDirectories )
					.Where( path => Extensions.Contains( Path.GetExtension( path ) ) )
					.GroupBy( path => Path.GetFileNameWithoutExtension( path ), StringComparer.OrdinalIgnoreCase )
					.ToDictionary( group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase );
				PackIndexes[packRoot] = index;
			}
		}
		if ( index.TryGetValue( textureHint, out var exact ) )
			return exact;
		foreach ( var candidate in CandidateHints( textureHint ) )
			if ( index.TryGetValue( candidate, out var inferred ) )
				return inferred;
		return null;
	}

	private static IEnumerable<string> CandidateHints( string materialName )
	{
		var candidates = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		candidates.Add( materialName.Replace( "_Mat_", "_Texture_", StringComparison.OrdinalIgnoreCase ) );
		var simplified = materialName
			.Replace( "_Double_", "_", StringComparison.OrdinalIgnoreCase )
			.Replace( "_Half_", "_", StringComparison.OrdinalIgnoreCase );
		var numbered = Regex.Match( simplified, "^(?<prefix>.+?)(?<suffix>_\\d+(?:_[A-Za-z])?)$" );
		if ( numbered.Success )
			candidates.Add( $"{numbered.Groups["prefix"].Value}_Texture{numbered.Groups["suffix"].Value}" );
		return candidates;
	}
}
