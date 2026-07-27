using System;
using System.IO;

namespace Editor.Tools.SyntyBrowser;

public static class SyntyPreviewCache
{
	public static string GetPath( string projectRoot, SyntySourceAsset source )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( projectRoot );
		ArgumentNullException.ThrowIfNull( source );
		return Path.Combine(
			Path.GetFullPath( projectRoot ),
			".sbox",
			"synty-browser",
			"previews",
			SyntySourceCatalog.SanitizeName( source.PackName ?? source.PackDisplayName ?? "pack" ),
			$"{SyntySourceCatalog.NormalizeId( source.Id )}.png" );
	}
}
