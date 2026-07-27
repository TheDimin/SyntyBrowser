using System;
using System.IO;

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
		return index.GetValueOrDefault( textureHint );
	}
}
