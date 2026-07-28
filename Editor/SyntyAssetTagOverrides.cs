using System;

namespace Editor.Tools.SyntyBrowser;

public sealed class SyntyAssetTagOverride
{
	public string[] Added { get; set; } = [];
	public string[] Removed { get; set; } = [];
}

public static class SyntyAssetTagOverrides
{
	public static SyntySourceAsset Apply( SyntySourceAsset asset, IReadOnlyDictionary<string, SyntyAssetTagOverride> overrides )
	{
		ArgumentNullException.ThrowIfNull( asset );
		var tags = asset.Tags.ToDictionary( tag => tag.Id, StringComparer.OrdinalIgnoreCase );
		if ( overrides?.TryGetValue( asset.CacheId, out var value ) == true )
		{
			foreach ( var id in value.Removed ?? [] )
				tags.Remove( id );
			foreach ( var id in value.Added ?? [] )
			{
				var tag = SyntyAssetTags.All.FirstOrDefault( candidate => string.Equals( candidate.Id, id, StringComparison.OrdinalIgnoreCase ) );
				if ( tag is not null )
					tags[tag.Id] = tag;
			}
		}
		return asset with { Tags = tags.Values.OrderBy( tag => tag.DisplayName, StringComparer.OrdinalIgnoreCase ).ToArray() };
	}

	public static SyntyAssetTagOverride Set( SyntySourceAsset asset, SyntyAssetTag tag, bool enabled )
	{
		var curated = SyntyAssetTags.Resolve( asset ).Any( candidate => string.Equals( candidate.Id, tag.Id, StringComparison.OrdinalIgnoreCase ) );
		return new SyntyAssetTagOverride
		{
			Added = enabled && !curated ? [tag.Id] : [],
			Removed = !enabled && curated ? [tag.Id] : []
		};
	}
}