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

	/// <summary>Reports the shared preview cache location, persisted results, and live worker queue.</summary>
	[McpTool.ReadOnly( "synty_preview_cache_status" )]
	public static object PreviewCacheStatus()
	{
		var persisted = new SyntyPreviewStateStore( SyntyBrowserSettings.CacheRoot ).GetStatus();
		var window = SyntyBrowserWindow.OpenDock();
		var queue = window.PreviewQueueStatus();
		return new
		{
			persisted.CacheRoot,
			RendererVersion = SyntyPreviewCache.RendererVersion,
			persisted.Completed,
			queue.Pending,
			queue.WorkerRunning,
			persisted.Skipped,
			persisted.Failed
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

	/// <summary>Queues one asset for shared-cache preview generation.</summary>
	[McpTool( "synty_queue_thumbnail" )]
	public static object QueueThumbnail( string assetId )
	{
		var catalog = RequireCatalog();
		var source = FindAsset( catalog, assetId )
			?? throw new FileNotFoundException( $"Synty asset '{assetId}' was not found.", assetId );
		var window = SyntyBrowserWindow.OpenDock();
		var queued = window.QueuePreviewAssets( [source], true );
		return new { source.CacheId, Queued = queued == 1, PendingCount = window.PendingThumbnailCount };
	}

	/// <summary>Returns current shared preview worker counts without rebuilding the source catalog.</summary>
	[McpTool.ReadOnly( "synty_thumbnail_queue_status" )]
	public static object ThumbnailQueueStatus()
	{
		var status = SyntyBrowserWindow.OpenDock().PreviewQueueStatus();
		return new
		{
			PendingCount = status.Pending,
			status.WorkerRunning,
			status.Completed,
			status.Skipped,
			status.Failed
		};
	}

	/// <summary>Queues every eligible asset in one pack while keeping the live queue bounded.</summary>
	[McpTool( "synty_queue_preview_pack" )]
	public static object QueuePreviewPack( string packName )
	{
		var catalog = RequireCatalog();
		var normalized = SyntySourceCatalog.SanitizeName( packName );
		var count = catalog.Assets.Count( asset => string.Equals( asset.PackName, normalized, StringComparison.OrdinalIgnoreCase ) );
		if ( count == 0 )
			throw new FileNotFoundException( $"Synty pack '{packName}' was not found.", packName );
		var window = SyntyBrowserWindow.OpenDock();
		window.QueuePreviewPack( normalized );
		return new { PackName = normalized, AssetCount = count, CacheRoot = SyntyBrowserSettings.CacheRoot };
	}

	/// <summary>Explicitly retries preview jobs that exhausted automatic retries.</summary>
	[McpTool( "synty_retry_failed_previews" )]
	public static object RetryFailedPreviews()
	{
		var window = SyntyBrowserWindow.OpenDock();
		var queued = window.RetryFailedPreviews();
		return new { Queued = queued, PendingCount = window.PendingThumbnailCount };
	}

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
