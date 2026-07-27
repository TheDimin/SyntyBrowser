using System;

namespace Editor.Tools.SyntyBrowser;

/// <summary>
/// Stable host-project integration surface for catalog, search, import, and removal workflows.
/// </summary>
public static class SyntyBrowserApi
{
	public static SyntySourceCatalogResult BuildCatalog( string sourceRoot ) =>
		SyntySourceCatalog.Build( sourceRoot );

	public static SyntySourceAsset[] Search( SyntySourceCatalogResult catalog, string query )
	{
		ArgumentNullException.ThrowIfNull( catalog );
		return SyntyAssetSearch.Search( catalog.Assets, query );
	}

	public static SyntyImportResult Import( SyntySourceCatalogResult catalog, SyntySourceAsset source )
	{
		ArgumentNullException.ThrowIfNull( catalog );
		ArgumentNullException.ThrowIfNull( source );
		var projectSettings = SyntyBrowserSettings.LoadProject();
		var packName = source.PackName ?? catalog.PackName;
		if ( !projectSettings.Packs.TryGetValue( packName, out var packSettings ) )
			throw new InvalidOperationException( $"Pack '{packName}' has no shader settings." );
		return SyntyImportService.Import( catalog, source, packSettings );
	}

	public static SyntyRemovalPlan PlanRemoval( SyntySourceAsset source ) =>
		SyntyRemovalService.Plan( source );

	public static SyntyRemovalResult RemoveImport( SyntyRemovalPlan plan, bool force = false ) =>
		SyntyRemovalService.Remove( plan, force );
}

