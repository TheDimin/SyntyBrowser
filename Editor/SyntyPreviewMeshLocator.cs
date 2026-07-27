using System;
using System.IO;

namespace Editor.Tools.SyntyBrowser;

internal static class SyntyPreviewMeshLocator
{
	private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> PackIndexes = new( StringComparer.OrdinalIgnoreCase );
	private static readonly object Sync = new();

	public static string FindObj( string packRoot, string sourceFbxPath )
	{
		if ( string.IsNullOrWhiteSpace( packRoot ) || string.IsNullOrWhiteSpace( sourceFbxPath ) )
			return null;

		IReadOnlyDictionary<string, string> index;
		lock ( Sync )
		{
			if ( !PackIndexes.TryGetValue( packRoot, out index ) )
			{
				index = Directory.EnumerateFiles( packRoot, "*.obj", SearchOption.AllDirectories )
					.GroupBy( Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase )
					.ToDictionary( group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase );
				PackIndexes[packRoot] = index;
			}
		}
		return index.GetValueOrDefault( Path.GetFileNameWithoutExtension( sourceFbxPath ) );
	}
}
