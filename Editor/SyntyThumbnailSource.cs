namespace Editor.Tools.SyntyBrowser;

public enum SyntyThumbnailSource
{
	None,
	OfflinePreview,
	ImportedAsset
}

public static class SyntyThumbnailSourcePolicy
{
	public static SyntyThumbnailSource Select( bool hasOfflinePreview, bool hasImportedAssetThumbnail )
	{
		if ( hasOfflinePreview )
			return SyntyThumbnailSource.OfflinePreview;
		if ( hasImportedAssetThumbnail )
			return SyntyThumbnailSource.ImportedAsset;
		return SyntyThumbnailSource.None;
	}
}
