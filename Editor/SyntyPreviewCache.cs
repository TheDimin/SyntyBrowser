using System;
using System.IO;

namespace Editor.Tools.SyntyBrowser;

public static class SyntyPreviewCache
{
	public const string RendererVersion = "v2";

	public static string GetPath( string cacheRoot, SyntySourceAsset source )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( cacheRoot );
		ArgumentNullException.ThrowIfNull( source );
		return Path.Combine(
			Path.GetFullPath( cacheRoot ),
			"previews",
			RendererVersion,
			SyntySourceCatalog.SanitizeName( source.PackName ?? source.PackDisplayName ?? "pack" ),
			$"{SyntySourceCatalog.NormalizeId( source.Id )}.png" );
	}

	public static string StateRoot( string cacheRoot ) => Path.Combine(
		Path.GetFullPath( cacheRoot ),
		"state",
		RendererVersion );
}
