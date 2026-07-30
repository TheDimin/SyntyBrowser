namespace Editor.Tools.SyntyBrowser;

public static class SyntyAutoImportPolicy
{
	public static bool IsVisibleOrNear(
		float cardTop,
		float cardBottom,
		float viewportTop,
		float viewportBottom,
		float rowHeight ) =>
		cardBottom >= viewportTop - rowHeight && cardTop <= viewportBottom + rowHeight;

	public static bool CanImport(
		SyntySourceAsset source,
		string defaultShader,
		IReadOnlySet<string> mappedMaterials )
	{
		if ( source is null || !source.CanImport || string.IsNullOrWhiteSpace( defaultShader ) )
			return false;

		return true;
	}
}
