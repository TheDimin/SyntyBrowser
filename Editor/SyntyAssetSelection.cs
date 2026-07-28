using System;

namespace Editor.Tools.SyntyBrowser;

public sealed class SyntyAssetSelection
{
	private readonly HashSet<string> _selected = new( StringComparer.OrdinalIgnoreCase );
	private int _anchor = -1;
	public IReadOnlySet<string> Selected => _selected;

	public void Select( IReadOnlyList<SyntySourceAsset> assets, int index, bool toggle, bool range )
	{
		if ( index < 0 || index >= assets.Count )
			return;
		if ( range && _anchor >= 0 )
		{
			if ( !toggle ) _selected.Clear();
			for ( var i = Math.Min( _anchor, index ); i <= Math.Max( _anchor, index ); i++ )
				_selected.Add( assets[i].CacheId );
			return;
		}
		_anchor = index;
		if ( toggle )
		{
			if ( !_selected.Add( assets[index].CacheId ) ) _selected.Remove( assets[index].CacheId );
		}
		else
		{
			_selected.Clear();
			_selected.Add( assets[index].CacheId );
		}
	}

	public void Retain( IEnumerable<SyntySourceAsset> assets ) =>
		_selected.IntersectWith( assets.Select( asset => asset.CacheId ) );
}