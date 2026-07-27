using System;

namespace Editor.Tools.SyntyBrowser;

public sealed class SyntyThumbnailScheduler
{
	private readonly int _maximumPending;
	private readonly HashSet<string> _pending = new( StringComparer.OrdinalIgnoreCase );

	public int PendingCount => _pending.Count;

	public SyntyThumbnailScheduler( int maximumPending )
	{
		if ( maximumPending <= 0 )
			throw new ArgumentOutOfRangeException( nameof( maximumPending ) );
		_maximumPending = maximumPending;
	}

	public bool TryQueue( string assetId, bool isVisible )
	{
		if ( !isVisible || string.IsNullOrWhiteSpace( assetId ) || _pending.Count >= _maximumPending )
			return false;
		return _pending.Add( assetId );
	}

	public void Complete( string assetId )
	{
		if ( !string.IsNullOrWhiteSpace( assetId ) )
			_pending.Remove( assetId );
	}
}
