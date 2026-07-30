using System;
using System.IO;
using Editor.Mcp;

namespace Editor.Tools.SyntyBrowser.Mcp;

[McpToolset( "synty_browser", "Browse and selectively import static models from a developer-local Synty source pack." )]
public static class SyntyBrowserMcpTools
{
	[McpTool.ReadOnly( "synty_catalog_status" )]
	public static object CatalogStatus()
	{
		var root = SyntyBrowserSettings.SourceRoot;
		if ( string.IsNullOrWhiteSpace( root ) || !Directory.Exists( root ) )
			return new { Configured = false, Root = root, AssetCount = 0, ImportableCount = 0 };
		var catalog = SyntySourceCatalog.Build( root );
		return new
		{
			Configured = true,
			Root = root,
			catalog.PackName,
			catalog.PackCount,
			AssetCount = catalog.Assets.Length,
			ImportableCount = catalog.Assets.Count( asset => asset.CanImport ),
			InvalidCount = catalog.Assets.Count( asset => !asset.CanImport ),
			catalog.MaterialListPath,
			WarningCount = catalog.Warnings.Length,
			Warnings = catalog.Warnings.Take( 100 ).ToArray()
		};
	}

	[McpTool.ReadOnly( "synty_inspect_asset" )]
	public static object InspectAsset( string assetId )
	{
		var catalog = RequireCatalog();
		return FindAsset( catalog, assetId )
			?? throw new FileNotFoundException( $"Synty asset '{assetId}' was not found.", assetId );
	}

	[McpTool( "synty_rescan" )]
	public static object Rescan() => CatalogStatus();

	[McpTool.ReadOnly( "synty_import_status" )]
	public static SyntyMassImportStatus ImportStatus() => SyntyBrowserWindow.CurrentImportStatus;

	[McpTool( "synty_import_stop" )]
	public static SyntyMassImportStatus StopImport() => SyntyBrowserWindow.StopCurrentImport();

	/// <summary>Starts an end-to-end live import sample. Preparation, promotion, compilation, and serialized native thumbnails are measured by synty_import_status.</summary>
	[McpTool( "synty_import_benchmark" )]
	public static SyntyMassImportStatus ImportBenchmark( int assetCount = 1000 ) =>
		SyntyBrowserWindow.StartImportBenchmark( assetCount );

	[McpTool( "synty_import_asset" )]
	public static SyntyImportResult ImportAsset( string assetId )
	{
		var catalog = RequireCatalog();
		var source = FindAsset( catalog, assetId )
			?? throw new FileNotFoundException( $"Synty asset '{assetId}' was not found.", assetId );
		var projectSettings = SyntyBrowserSettings.LoadProject();
		var packName = source.PackName ?? catalog.PackName;
		if ( !projectSettings.Packs.TryGetValue( packName, out var packSettings ) )
			throw new InvalidOperationException( $"Pack '{packName}' has no shader settings." );
		return SyntyImportService.Import( catalog, source, packSettings );
	}

	[McpTool.ReadOnly( "synty_validate_import" )]
	public static object ValidateImport( string assetId )
	{
		var catalog = RequireCatalog();
		var source = FindAsset( catalog, assetId )
			?? throw new FileNotFoundException( $"Synty asset '{assetId}' was not found.", assetId );
		var path = $"{SyntyImportService.DefaultDestinationRoot}/{source.PackName ?? catalog.PackName}/Models/{source.Id}.vmdl";
		var asset = AssetSystem.FindByPath( path );
		return new
		{
			AssetId = source.Id,
			ModelPath = path,
			Exists = asset is not null,
			CompileFailed = asset?.IsCompileFailed ?? false
		};
	}

	/// <summary>Plans removal and reports every project asset that currently references the imported output.</summary>
	[McpTool.ReadOnly( "synty_plan_remove_import" )]
	public static SyntyRemovalPlan PlanRemoveImport( string assetId )
	{
		var catalog = RequireCatalog();
		var source = FindAsset( catalog, assetId )
			?? throw new FileNotFoundException( $"Synty asset '{assetId}' was not found.", assetId );
		return SyntyRemovalService.Plan( source );
	}

	/// <summary>Removes an import. By default this refuses to run while any project asset references its output.</summary>
	[McpTool( "synty_remove_import" )]
	public static SyntyRemovalResult RemoveImport( string assetId, bool force = false )
	{
		var plan = PlanRemoveImport( assetId );
		return SyntyRemovalService.Remove( plan, force );
	}

	private static SyntySourceCatalogResult RequireCatalog()
	{
		var root = SyntyBrowserSettings.SourceRoot;
		if ( string.IsNullOrWhiteSpace( root ) || !Directory.Exists( root ) )
			throw new DirectoryNotFoundException( "Configure a valid Synty source pack folder first." );
		var catalog = SyntySourceCatalog.Build( root );
		var overrides = SyntyBrowserSettings.LoadProject().TagOverrides;
		return catalog with { Assets = catalog.Assets.Select( asset => SyntyAssetTagOverrides.Apply( asset, overrides ) ).ToArray() };
	}

	private static SyntySourceAsset FindAsset( SyntySourceCatalogResult catalog, string assetId )
	{
		var matches = catalog.Assets.Where( asset =>
			string.Equals( asset.CacheId, assetId, StringComparison.OrdinalIgnoreCase )
			|| string.Equals( asset.Id, assetId, StringComparison.OrdinalIgnoreCase ) ).ToArray();
		if ( matches.Length > 1 )
			throw new InvalidOperationException( $"Asset id '{assetId}' exists in multiple packs; use the pack-qualified CacheId." );
		return matches.SingleOrDefault();
	}
}
