using System;

namespace Editor.Tools.SyntyBrowser;

public static class SyntyPreviewMaterialCache
{
	public static string Key( string packName, string textureHint, string assetCacheId )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( packName );
		ArgumentException.ThrowIfNullOrWhiteSpace( textureHint );
		return $"{SyntySourceCatalog.SanitizeName( packName )}/{SyntySourceCatalog.SanitizeName( textureHint )}";
	}
}
