using System;
using System.Collections.Concurrent;
using System.IO;
using Sandbox;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Tools.SyntyBrowser;

public sealed record SyntyMassImportStatus
{
	public string Stage { get; init; } = "Idle";
	public string Current { get; init; }
	public int Total { get; init; }
	public int Prepared { get; init; }
	public int Promoted { get; init; }
	public int Compiled { get; init; }
	public int Finalized { get; init; }
	public int Failed { get; init; }
	public double PreparationRate { get; init; }
	public DateTime StartedUtc { get; init; }
	public TimeSpan Elapsed { get; init; }
	public bool StopRequested { get; init; }
	public string LastError { get; init; }
}

[Dock( "Editor", "Synty Browser", "view_in_ar" )]
public sealed class SyntyBrowserWindow : Widget
{
	private const int PreparationBatchSize = 1000;
	private const int PromotionBatchSize = 48;
	private const string DockTitle = "Synty Browser";
	private static SyntyBrowserWindow _instance;
	private readonly LineEdit _sourceRoot;
	private readonly LineEdit _search;
	private readonly Label _status;
	private readonly SyntyScrollArea _scroll;
	private readonly SyntyAssetGrid _grid;
	private readonly Queue<SyntySourceAsset> _automaticImports = new();
	private readonly HashSet<string> _automaticImportAttempts = new( StringComparer.OrdinalIgnoreCase );
	private SyntySourceCatalogResult _catalog;
	private int _refreshRevision;
	private bool _destroyed;
	private bool _automaticImportRunning;
	private bool _stopRequested;
	private bool _hasCurrentToolbar;
	private CancellationTokenSource _massImportCancellation;
	private SyntyMassImportStatus _massStatus = new();
	private Button _stopButton;

	public static SyntyMassImportStatus CurrentImportStatus => _instance?._massStatus ?? new();

	public SyntyBrowserWindow() : this( null ) { }

	public SyntyBrowserWindow( Widget parent ) : base( parent )
	{
		_instance = this;
		WindowTitle = DockTitle;
		MinimumSize = new Vector2( 480, 420 );
		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 6;

		var sourceRow = Layout.AddRow();
		_sourceRoot = sourceRow.Add( new LineEdit( SyntyBrowserSettings.SourceRoot ) { PlaceholderText = "Local Synty pack folder..." }, 1 );
		var browse = sourceRow.Add( new Button( "Choose Folder", "folder_open" ) );
		browse.Clicked += ChooseFolder;
		var refresh = sourceRow.Add( new Button( "Refresh", "refresh" ) );
		refresh.Clicked += Refresh;

		var searchRow = Layout.AddRow();
		_search = searchRow.Add( new LineEdit( "" ) { PlaceholderText = "Search assets or filter with tag:harbor-city" }, 1 );
		_search.TextEdited += SearchTextEdited;
		var importAll = searchRow.Add( new Button( "Import All", "download" ) );
		importAll.Clicked += ImportAll;
		_stopButton = searchRow.Add( new Button( "Stop", "stop" ) { Enabled = false } );
		_stopButton.Clicked += StopImport;
		_hasCurrentToolbar = true;
		_status = Layout.Add( new Label( "Choose a Synty pack folder." )
		{
			WordWrap = true,
			MinimumWidth = 0
		} );

		_scroll = Layout.Add( new SyntyScrollArea(), 1 );
		_grid = new SyntyAssetGrid( this, _scroll );
		_scroll.Canvas = _grid;
		_scroll.ViewportChanged = ViewportChanged;
		if ( Directory.Exists( SyntyBrowserSettings.SourceRoot ) )
			Refresh();
	}

	protected override void OnResize()
	{
		base.OnResize();
		if ( !_destroyed && _grid is not null && _scroll is not null )
			_grid.SetViewportWidth( _scroll.Size.x );
	}

	public override void OnDestroyed()
	{
		_destroyed = true;
		_refreshRevision++;
		_scroll.ViewportChanged = null;
		_automaticImports.Clear();
		_massImportCancellation?.Cancel();
		if ( ReferenceEquals( _instance, this ) )
			_instance = null;
		base.OnDestroyed();
	}

	private void SearchTextEdited( string _ )
	{
		ApplySearch();
	}

	private void ViewportChanged()
	{
		if ( _destroyed )
			return;
		_grid.SetViewportWidth( _scroll.Size.x );
		_grid.Update();
	}

	private void ImportAll()
	{
		if ( _catalog is null || _massImportCancellation is not null )
			return;
		_ = RunMassImport( int.MaxValue );
	}

	public static SyntyMassImportStatus StartImportBenchmark( int assetCount )
	{
		if ( assetCount <= 0 )
			throw new ArgumentOutOfRangeException( nameof(assetCount) );
		var window = OpenDock();
		if ( window._catalog is null )
		{
			var root = SyntyBrowserSettings.SourceRoot;
			if ( string.IsNullOrWhiteSpace( root ) || !Directory.Exists( root ) )
				throw new InvalidOperationException( "Configure a valid Synty source folder first." );
			window._catalog = ApplyTagOverrides( SyntySourceCatalog.Build( root ) );
			window.ApplySearch();
		}
		if ( window._massImportCancellation is not null )
			throw new InvalidOperationException( "A Synty mass import is already running." );
		_ = window.RunMassImport( assetCount );
		return window._massStatus;
	}

	public static SyntyMassImportStatus StopCurrentImport()
	{
		_instance?.StopImport();
		return CurrentImportStatus;
	}

	private void StopImport()
	{
		_stopRequested = true;
		_massStatus = _massStatus with { StopRequested = true, Stage = "Stopping after current operation" };
		_massImportCancellation?.Cancel();
		_automaticImports.Clear();
		_status.Text = FormatMassStatus();
	}

	public static SyntyBrowserWindow OpenDock()
	{
		var existing = EditorWindow.DockManager.FindDockWidget( DockTitle )?.Widget as SyntyBrowserWindow;
		if ( existing.IsValid() && !existing._hasCurrentToolbar )
		{
			existing.Destroy();
			EditorWindow.DockManager.SetDockState( DockTitle, false );
		}
		EditorWindow.DockManager.SetDockState( DockTitle, true );
		var dock = EditorWindow.DockManager.FindDockWidget( DockTitle )?.Widget as SyntyBrowserWindow;
		if ( !dock.IsValid() )
			throw new InvalidOperationException( $"Unable to open the '{DockTitle}' editor dock." );
		EditorWindow.DockManager.RaiseDock( DockTitle );
		return dock;
	}

	private void ChooseFolder()
	{
		var dialog = new FileDialog( this ) { Title = "Choose Synty Pack Folder", Directory = _sourceRoot.Text };
		dialog.SetFindDirectory();
		if ( !dialog.Execute() )
			return;
		_sourceRoot.Text = dialog.SelectedFile;
		Refresh();
	}

	private async void Refresh()
	{
		var revision = ++_refreshRevision;
		var root = _sourceRoot.Text?.Trim();
		_status.Text = "Scanning pack...";
		_grid.SetAssets( [] );
		try
		{
			var catalog = await Task.Run( () => SyntySourceCatalog.Build( root ) );
			if ( _destroyed || revision != _refreshRevision )
				return;
			_catalog = ApplyTagOverrides( catalog );
			SyntyBrowserSettings.SourceRoot = _catalog.RootPath;
			ApplySearch();
			_status.Text = _catalog.IsLibrary
				? $"{_catalog.PackCount} packs · {_catalog.Assets.Length:N0} assets · {_catalog.Assets.Count( asset => !asset.CanImport ):N0} need review"
				: $"{new DirectoryInfo( _catalog.RootPath ).Name} · {_catalog.Assets.Length:N0} assets · {_catalog.Assets.Count( asset => !asset.CanImport ):N0} need review";
		}
		catch ( Exception exception )
		{
			if ( _destroyed || revision != _refreshRevision )
				return;
			_catalog = null;
			_grid.SetAssets( [] );
			_status.Text = exception.Message;
		}
	}

	private void ApplySearch()
	{
		var results = SyntyAssetSearch.Search( _catalog?.Assets ?? [], _search.Text );
		_grid.SetAssets( results );
		if ( _catalog is not null && !string.IsNullOrWhiteSpace( _search.Text ) )
			_status.Text = $"Showing {results.Length:N0} of {_catalog.Assets.Length:N0} assets";
	}

	private static SyntySourceCatalogResult ApplyTagOverrides( SyntySourceCatalogResult catalog )
	{
		var overrides = SyntyBrowserSettings.LoadProject().TagOverrides;
		return catalog with { Assets = catalog.Assets.Select( asset => SyntyAssetTagOverrides.Apply( asset, overrides ) ).ToArray() };
	}

	private void EditTags( IReadOnlyList<SyntySourceAsset> sources, SyntyAssetTag tag, bool enabled )
	{
		var settings = SyntyBrowserSettings.LoadProject();
		foreach ( var source in sources )
			settings.TagOverrides[source.CacheId] = SyntyAssetTagOverrides.Set( source, tag, enabled );
		SyntyBrowserSettings.SaveProject( settings );
		_catalog = ApplyTagOverrides( _catalog );
		ApplySearch();
		_status.Text = $"{(enabled ? "Added" : "Removed")} {tag.DisplayName} for {sources.Count:N0} asset(s).";
	}

	private async void ImportBatch( IReadOnlyList<SyntySourceAsset> sources )
	{
		var pending = sources.Where( source => source.CanImport && !IsImported( source ) ).ToArray();
		var imported = 0;
		var failed = 0;
		foreach ( var source in pending )
		{
			if ( _destroyed )
				return;
			_status.Text = $"Batch import {imported + failed + 1:N0}/{pending.Length:N0}: {source.DisplayName ?? source.Name}";
			await Task.Yield();
			if ( _destroyed )
				return;
			Import( source );
			if ( IsImported( source ) ) imported++; else failed++;
		}
		if ( _destroyed )
			return;
		_status.Text = $"Batch import complete: {imported:N0} imported, {failed:N0} failed, {sources.Count - pending.Length:N0} skipped.";
	}
	internal void SelectionChanged( int count )
	{
		if ( count > 0 ) _status.Text = $"{count:N0} asset(s) selected. Right-click for batch actions.";
	}
	internal Pixmap GetThumbnail( SyntySourceAsset source )
	{
		return AssetSystem.FindByPath( ModelPath( source ) )?.GetAssetThumb( false );
	}

	internal void QueueAutomaticImports( IEnumerable<SyntySourceAsset> sources )
	{
		if ( _catalog is null || _destroyed )
			return;
		var settings = SyntyBrowserSettings.LoadProject();
		foreach ( var source in sources )
		{
			var packName = source.PackName ?? _catalog.PackName;
			if ( IsImported( source )
				|| _automaticImportAttempts.Contains( source.CacheId )
				|| !settings.Packs.TryGetValue( packName, out var packSettings )
				|| !SyntyAutoImportPolicy.CanImport( source, packSettings.DefaultShader, packSettings.Materials.Keys.ToHashSet( StringComparer.OrdinalIgnoreCase ) ) )
				continue;
			_automaticImportAttempts.Add( source.CacheId );
			_automaticImports.Enqueue( source );
		}
		if ( !_automaticImportRunning && _automaticImports.Count > 0 )
			_ = RunAutomaticImports();
	}

	private async Task RunAutomaticImports()
	{
		_automaticImportRunning = true;
		_stopRequested = false;
		await Task.Yield();
		while ( !_destroyed && !_stopRequested && _automaticImports.TryDequeue( out var source ) )
		{
			if ( ImportConfigured( source ) )
				await WaitForThumbnail( source );
			_grid.Update();
			await Task.Yield();
		}
		_automaticImportRunning = false;
	}

	private async Task RunMassImport( int maximumAssets )
	{
		var settings = SyntyBrowserSettings.LoadProject();
		var pending = _catalog.Assets
			.Where( source =>
			{
				var packName = source.PackName ?? _catalog.PackName;
				return !IsImported( source )
					&& settings.Packs.TryGetValue( packName, out var packSettings )
					&& SyntyAutoImportPolicy.CanImport(
						source,
						packSettings.DefaultShader,
						packSettings.Materials.Keys.ToHashSet( StringComparer.OrdinalIgnoreCase ) );
			} )
			.Take( maximumAssets )
			.ToArray();
		if ( pending.Length == 0 )
		{
			_status.Text = "All eligible assets are already imported.";
			return;
		}

		_automaticImports.Clear();
		_massImportCancellation = new CancellationTokenSource();
		_stopButton.Enabled = true;
		var cancellationToken = _massImportCancellation.Token;
		var stagingRoot = Path.Combine( Project.Current.GetRootPath(), ".sbox", "synty-import-prepared" );
		var manifestPath = Path.Combine( stagingRoot, "manifest.json" );
		var manifest = SyntyMassImportManifest.Load( manifestPath );
		var runWatch = Stopwatch.StartNew();
		_massStatus = new SyntyMassImportStatus { Total = pending.Length, Stage = "Preparing", StartedUtc = DateTime.UtcNow };
		var finalizationQueue = new ConcurrentQueue<SyntyPreparedImport>();
		var promotionFinished = false;
		Task finalizationTask = null;

		try
		{
			finalizationTask = FinalizePromotedAssets(
				finalizationQueue,
				manifest,
				manifestPath,
				() => promotionFinished,
				cancellationToken );
			Task<SyntyPreparationBatch> preparationTask = null;
			for ( var offset = 0; offset < pending.Length; offset += PreparationBatchSize )
			{
				cancellationToken.ThrowIfCancellationRequested();
				var chunk = pending.Skip( offset ).Take( PreparationBatchSize ).ToArray();
				_massStatus = _massStatus with { Stage = "Preparing", Current = chunk[0].Name };
				UpdateMassStatus();
				preparationTask ??= Task.Run(
					() => SyntyMassImportPreparation.PrepareBatch(
						chunk,
						settings.Packs,
						stagingRoot,
						Math.Clamp( Environment.ProcessorCount - 2, 2, 12 ),
						cancellationToken ),
					cancellationToken );
				var preparation = await preparationTask;
				var nextOffset = offset + PreparationBatchSize;
				preparationTask = nextOffset >= pending.Length
					? null
					: Task.Run(
						() => SyntyMassImportPreparation.PrepareBatch(
							pending.Skip( nextOffset ).Take( PreparationBatchSize ).ToArray(),
							settings.Packs,
							stagingRoot,
							Math.Clamp( Environment.ProcessorCount - 2, 2, 12 ),
							cancellationToken ),
						cancellationToken );
				_massStatus = _massStatus with
				{
					Prepared = _massStatus.Prepared + preparation.PreparedCount,
					Failed = _massStatus.Failed + preparation.Imports.Count( item => !item.Success ),
					PreparationRate = preparation.AssetsPerMinute,
					Stage = "Promoting"
				};
				foreach ( var failed in preparation.Imports.Where( item => !item.Success ) )
					manifest.Failures[failed.Source.CacheId] = failed.Error;
				foreach ( var prepared in preparation.Imports.Where( item => item.Success ) )
					manifest.Prepared.Add( prepared.Source.CacheId );
				manifest.Save( manifestPath );
				UpdateMassStatus();

				foreach ( var promotionBatch in preparation.Imports.Where( item => item.Success ).Chunk( PromotionBatchSize ) )
				{
					cancellationToken.ThrowIfCancellationRequested();
					foreach ( var prepared in promotionBatch )
					{
						SyntyMassImportPreparation.Promote( prepared, Project.Current.GetAssetsPath() );
						foreach ( var file in prepared.Files.Where( file => !file.AssetPath.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ) )
						{
							var asset = AssetSystem.RegisterFile( Path.Combine(
								Project.Current.GetAssetsPath(),
								file.AssetPath.Replace( '/', Path.DirectorySeparatorChar ) ) );
							if ( file.AssetPath.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) )
								asset?.Compile( false );
						}
						var model = AssetSystem.RegisterFile( Path.Combine(
							Project.Current.GetAssetsPath(),
							prepared.ModelPath.Replace( '/', Path.DirectorySeparatorChar ) ) );
						model?.Compile( false );
						_massStatus = _massStatus with { Promoted = _massStatus.Promoted + 1, Current = prepared.Source.Name };
						finalizationQueue.Enqueue( prepared );
					}
					MainAssetBrowser.Instance?.Local.UpdateAssetList();
					await Task.Yield();
				}
			}
			promotionFinished = true;
			_massStatus = _massStatus with { Stage = "Finishing compile and thumbnails" };
			await finalizationTask;
			_massStatus = _massStatus with { Stage = "Complete", Elapsed = runWatch.Elapsed };
		}
		catch ( OperationCanceledException )
		{
			promotionFinished = true;
			_massStatus = _massStatus with { Stage = "Stopped", StopRequested = true, Elapsed = runWatch.Elapsed };
		}
		catch ( Exception exception )
		{
			promotionFinished = true;
			_massImportCancellation?.Cancel();
			_massStatus = _massStatus with { Stage = "Failed", LastError = exception.Message, Elapsed = runWatch.Elapsed };
			Log.Error( exception, "Synty mass import failed." );
		}
		finally
		{
			_massImportCancellation.Dispose();
			_massImportCancellation = null;
			_stopButton.Enabled = false;
			UpdateMassStatus();
		}
	}

	private async Task FinalizePromotedAssets(
		ConcurrentQueue<SyntyPreparedImport> queue,
		SyntyMassImportManifest manifest,
		string manifestPath,
		Func<bool> promotionFinished,
		CancellationToken cancellationToken )
	{
		while ( !promotionFinished() || !queue.IsEmpty )
		{
			cancellationToken.ThrowIfCancellationRequested();
			if ( !queue.TryDequeue( out var prepared ) )
			{
				await Task.Delay( 25, cancellationToken );
				continue;
			}

			var model = AssetSystem.FindByPath( prepared.ModelPath );
			if ( model is null || model.IsCompileFailed )
			{
				_massStatus = _massStatus with { Failed = _massStatus.Failed + 1 };
				manifest.Failures[prepared.Source.CacheId] = model is null ? "Model was not registered." : "Model compile failed.";
				manifest.Save( manifestPath );
				continue;
			}

			_massStatus = _massStatus with
			{
				Compiled = _massStatus.Compiled + 1,
				Stage = queue.IsEmpty && promotionFinished() ? "Finishing thumbnails" : "Importing and thumbnailing",
				Current = prepared.Source.Name
			};
			UpdateMassStatus();
			model.GetAssetThumb( true );
			if ( await WaitForThumbnail( prepared.Source, cancellationToken ) )
			{
				_massStatus = _massStatus with { Finalized = _massStatus.Finalized + 1 };
				manifest.Finalized.Add( prepared.Source.CacheId );
			}
			else
			{
				_massStatus = _massStatus with { Failed = _massStatus.Failed + 1 };
				manifest.Failures[prepared.Source.CacheId] = "Native thumbnail timed out.";
			}
			manifest.Save( manifestPath );
			_grid.Update();
		}
	}

	private void UpdateMassStatus()
	{
		_massStatus = _massStatus with { Elapsed = DateTime.UtcNow - _massStatus.StartedUtc };
		_status.Text = FormatMassStatus();
	}

	private string FormatMassStatus()
	{
		var remaining = Math.Max( 0, _massStatus.Total - _massStatus.Finalized - _massStatus.Failed );
		var finalRate = _massStatus.Elapsed.TotalMinutes <= 0 ? 0 : _massStatus.Finalized / _massStatus.Elapsed.TotalMinutes;
		var eta = finalRate <= 0 ? "—" : TimeSpan.FromMinutes( remaining / finalRate ).ToString( @"hh\:mm\:ss" );
		return $"{_massStatus.Stage} · prepared {_massStatus.Prepared:N0}/{_massStatus.Total:N0} ({_massStatus.PreparationRate:N0}/min) · promoted {_massStatus.Promoted:N0} · compiled {_massStatus.Compiled:N0} · thumbnailed {_massStatus.Finalized:N0} ({finalRate:N1}/min) · failed {_massStatus.Failed:N0} · ETA {eta}";
	}

	private async Task WaitForThumbnail( SyntySourceAsset source )
	{
		_status.Text = $"Generating thumbnail for {source.Name}...";
		var deadline = DateTime.UtcNow.AddSeconds( 30 );
		while ( !_destroyed && !_stopRequested && DateTime.UtcNow < deadline )
		{
			if ( GetThumbnail( source ) is not null )
				return;
			await Task.Delay( 50 );
		}
	}

	private async Task<bool> WaitForThumbnail( SyntySourceAsset source, CancellationToken cancellationToken )
	{
		var deadline = DateTime.UtcNow.AddSeconds( 30 );
		while ( !_destroyed && DateTime.UtcNow < deadline )
		{
			cancellationToken.ThrowIfCancellationRequested();
			if ( GetThumbnail( source ) is not null )
				return true;
			await Task.Delay( 50, cancellationToken );
		}
		return false;
	}

	private void GenerateVisible()
	{
		QueueAutomaticImports( _grid.VisibleOrNearAssets() );
	}

	private void ShowCacheStatus()
	{
		_status.Text = "Assets import only when requested and use native s&box thumbnails.";
	}

	internal bool IsImported( SyntySourceAsset source ) => AssetSystem.FindByPath( ModelPath( source ) ) is not null;

	internal Asset PrepareAssetDrag( SyntySourceAsset source )
	{
		if ( _catalog is null || source is null || !source.CanImport )
			return null;
		if ( !IsImported( source ) )
			Import( source );

		var asset = AssetSystem.FindByPath( ModelPath( source ) );
		if ( asset is null )
			_status.Text = $"Could not drag {source.Name}; finish its import setup first.";
		return asset;
	}

	private string ModelPath( SyntySourceAsset source ) =>
		_catalog is null ? "" : $"{SyntyImportService.DefaultDestinationRoot}/{source.PackName ?? _catalog.PackName}/Models/{source.Id}.vmdl";

	internal void Import( SyntySourceAsset source )
	{
		if ( _catalog is null || !source.CanImport )
			return;
		var settings = SyntyBrowserSettings.LoadProject();
		var packName = source.PackName ?? _catalog.PackName;
		if ( !settings.Packs.TryGetValue( packName, out var packSettings )
			|| string.IsNullOrWhiteSpace( packSettings.DefaultShader ) )
		{
			ShowDefaultShaderPicker( source, packName, $"Choose default shader for {source.PackDisplayName ?? packName}" );
			return;
		}
		ImportConfigured( source, packSettings );
	}

	private bool ImportConfigured( SyntySourceAsset source, SyntyPackMaterialSettings packSettings = null )
	{
		if ( _catalog is null || !source.CanImport )
			return false;
		packSettings ??= SyntyBrowserSettings.LoadProject().Packs.GetValueOrDefault( source.PackName ?? _catalog.PackName );
		if ( !SyntyAutoImportPolicy.CanImport(
			source,
			packSettings?.DefaultShader,
			packSettings?.Materials.Keys.ToHashSet( StringComparer.OrdinalIgnoreCase ) ) )
			return false;
		_status.Text = $"Importing {source.Name}...";
		var result = SyntyImportService.Import( _catalog, source, packSettings );
		_status.Text = result.Success ? $"Imported {source.Name}" : $"Import failed: {result.Error}";
		_grid.Update();
		return result.Success;
	}

	private void ShowDefaultShaderPicker( SyntySourceAsset source, string packName, string title )
	{
		var picker = AssetPicker.Create( this, AssetType.FromType( typeof( Shader ) ), new AssetPicker.PickerOptions
		{
			EnableCloud = false,
			EnableMultiselect = false
		} );
		picker.Title = title;
		picker.OnAssetPicked = assets =>
		{
			var shaderAsset = assets?.SingleOrDefault();
			if ( shaderAsset is null )
				return;

			var shaderPath = shaderAsset.Path;
			if ( shaderPath.EndsWith( ".shader", StringComparison.OrdinalIgnoreCase ) )
				shaderPath = $"{shaderPath}_c";

			var settings = SyntyBrowserSettings.LoadProject();
			if ( !settings.Packs.TryGetValue( packName, out var packSettings ) )
			{
				packSettings = new SyntyPackMaterialSettings();
				settings.Packs[packName] = packSettings;
			}
			packSettings.DefaultShader = shaderPath;
			SyntyBrowserSettings.SaveProject( settings );
			Import( source );
		};
		picker.Show();
	}

	internal void ShowAssetContextMenu( SyntySourceAsset source, Vector2 screenPosition )
	{
		if ( source is null )
			return;

		var sources = _grid.ContextSelection( source );
		var menu = new ContextMenu( this );
		menu.AddHeading( sources.Count > 1 ? $"{sources.Count:N0} selected assets" : source.DisplayName ?? source.Name );
		var tags = menu.AddMenu( "Edit Tags", "label" );
		foreach ( var tag in SyntyAssetTags.All )
		{
			var selectedTag = tag;
			var allHave = sources.All( asset => asset.Tags.Any( current => string.Equals( current.Id, selectedTag.Id, StringComparison.OrdinalIgnoreCase ) ) );
			tags.AddOption( allHave ? $"Remove {selectedTag.DisplayName}" : $"Add {selectedTag.DisplayName}", "label", () => EditTags( sources, selectedTag, !allHave ) );
		}
		if ( sources.Count > 1 )
		{
			menu.AddOption( "Import Selected", "download", () => ImportBatch( sources ) );
			menu.OpenAt( screenPosition );
			return;
		}
		if ( !IsImported( source ) )
		{
			menu.AddOption( "Import Locally", "download", () => Import( source ) ).Enabled = source.CanImport;
			menu.OpenAt( screenPosition );
			return;
		}

		var plan = SyntyRemovalService.Plan( source );
		var materialPaths = plan.OutputPaths
			.Where( path => path.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) )
			.OrderBy( path => path, StringComparer.OrdinalIgnoreCase )
			.ToArray();
		if ( materialPaths.Length == 1 )
		{
			menu.AddOption( "Edit Imported Material...", "texture", () => OpenImportedMaterial( materialPaths[0] ) );
		}
		else
		{
			var materials = menu.AddMenu( "Edit Imported Materials", "texture" );
			foreach ( var path in materialPaths )
			{
				var materialPath = path;
				materials.AddOption(
					Path.GetFileNameWithoutExtension( materialPath ),
					"edit",
					() => OpenImportedMaterial( materialPath ) );
			}
			if ( materialPaths.Length == 0 )
				materials.AddOption( "No generated materials found", null ).Enabled = false;
		}

		var packName = source.PackName ?? _catalog?.PackName;
		if ( !string.IsNullOrWhiteSpace( packName ) )
		{
			menu.AddOption(
				"Change Pack Shader and Reimport...",
				"shader",
				() => ShowDefaultShaderPicker(
					source,
					packName,
					$"Change shader for {source.PackDisplayName ?? packName} and reimport {source.DisplayName ?? source.Name}" ) );
		}

		menu.AddSeparator();
		var removal = menu.AddMenu( "Delete Local Import", "delete" );
		removal.AddOption( $"{plan.OutputPaths.Length} generated project file(s) only", null ).Enabled = false;
		removal.AddOption( "External Synty source files are preserved", null ).Enabled = false;
		if ( plan.References.Length > 0 )
		{
			removal.AddSeparator();
			removal.AddOption(
				$"Blocked by {plan.References.Length} project reference(s)",
				"lock",
				null ).Enabled = false;
			var references = removal.AddMenu( "Show Blocking References", "account_tree" );
			foreach ( var reference in plan.References.Take( 20 ) )
				references.AddOption( reference.ReferencingAssetPath, null ).Enabled = false;
		}
		else
		{
			removal.AddSeparator();
			removal.AddOption(
				"Confirm Delete Local Import",
				"delete_forever",
				() => DeleteLocalImport( plan ) );
		}
		menu.OpenAt( screenPosition );
	}

	private void OpenImportedMaterial( string path )
	{
		var asset = AssetSystem.FindByPath( path );
		if ( asset is null )
		{
			_status.Text = $"Imported material was not found: {path}";
			return;
		}

		asset.OpenInEditor();
		MainAssetBrowser.Instance?.Local.FocusOnAsset( asset );
	}

	private void DeleteLocalImport( SyntyRemovalPlan plan )
	{
		try
		{
			var result = SyntyRemovalService.Remove( plan );
			_status.Text = $"Deleted local import for {plan.Source.DisplayName ?? plan.Source.Name} ({result.RemovedPaths.Length} files).";
			_grid.Update();
		}
		catch ( Exception exception )
		{
			_status.Text = $"Could not delete local import: {exception.Message}";
		}
	}

	internal void ImportAndSpawn( SyntySourceAsset source )
	{
		if ( _catalog is null || !source.CanImport )
			return;

		if ( !IsImported( source ) )
		{
			Import( source );
			if ( !IsImported( source ) )
				return;
		}

		var model = ResourceLibrary.Get<Model>( ModelPath( source ) );
		var session = SceneEditorSession.Active;
		if ( model is null || session is null )
		{
			_status.Text = $"Imported {source.Name}, but no editable scene is active.";
			return;
		}

		using ( session.Scene.Push() )
		using ( session.UndoScope( $"Add {source.Name}" ).WithGameObjectCreations().Push() )
		{
			var gameObject = session.Scene.CreateObject( true );
			gameObject.Name = source.Name;
			gameObject.WorldPosition = Vector3.Zero;
			gameObject.Components.Create<ModelRenderer>().Model = model;
		}
		_status.Text = $"Added {source.Name} to the scene";
		_grid.Update();
	}

	private sealed class SyntyScrollArea : ScrollArea
	{
		public Action ViewportChanged { get; set; }

		public SyntyScrollArea() : base( null )
		{
			HorizontalScrollbarMode = ScrollbarMode.Off;
		}

		protected override void OnResize()
		{
			base.OnResize();
			ViewportChanged?.Invoke();
		}

		protected override void OnMouseWheel( WheelEvent e )
		{
			base.OnMouseWheel( e );
			ViewportChanged?.Invoke();
		}

		protected override void OnMouseMove( MouseEvent e )
		{
			base.OnMouseMove( e );
			ViewportChanged?.Invoke();
		}

		protected override void OnPaint()
		{
			base.OnPaint();
			Canvas?.Update();
		}
	}

	private sealed class SyntyAssetGrid : Widget
	{
		private const float PreferredCardWidth = 144;
		private const float MinimumCardWidth = 118;
		private const float CardHeight = 174;
		private const float Gap = 8;
		private readonly SyntyBrowserWindow _window;
		private readonly ScrollArea _scroll;
		private readonly List<SyntySourceAsset> _assets = [];
		private readonly SyntyAssetSelection _selection = new();
		private int _hovered = -1;
		private int _dragCandidate = -1;
		private int Columns => Math.Max( 1, (int)((Size.x - Gap) / (PreferredCardWidth + Gap)) );
		private float CardWidth => Math.Max( MinimumCardWidth, (Size.x - Gap * (Columns + 1)) / Columns );

		public SyntyAssetGrid( SyntyBrowserWindow window, ScrollArea scroll ) : base( null )
		{
			_window = window;
			_scroll = scroll;
			MouseTracking = true;
			IsDraggable = true;
			Cursor = CursorShape.Finger;
			FixedWidth = PreferredCardWidth;
		}

		public void SetViewportWidth( float viewportWidth )
		{
			var previousColumns = Columns;
			FixedWidth = Math.Max( MinimumCardWidth + Gap * 2, viewportWidth - 24 );
			if ( previousColumns == Columns )
				return;

			UpdateHeight();
			_hovered = -1;
			UpdateGeometry();
			Update();
		}

		protected override void OnMoved()
		{
			base.OnMoved();
			Update();
		}

		protected override void OnMouseWheel( WheelEvent e )
		{
			base.OnMouseWheel( e );
			Update();
		}

		public void SetAssets( IReadOnlyList<SyntySourceAsset> assets )
		{
			_assets.Clear();
			_assets.AddRange( assets ?? [] );
			_selection.Retain( _assets );
			_scroll.VerticalScrollbar.Value = 0;
			UpdateHeight();
			_hovered = -1;
			UpdateGeometry();
			Update();
		}

		public bool IsVisibleOrNearVisible( SyntySourceAsset source )
		{
			var index = _assets.IndexOf( source );
			if ( index < 0 )
				return false;
			var viewportTop = Math.Max( 0f, _scroll.ScreenRect.Top - ScreenRect.Top );
			var viewportBottom = viewportTop + _scroll.Size.y;
			var card = CardRect( index );
			return SyntyAutoImportPolicy.IsVisibleOrNear(
				card.Top,
				card.Bottom,
				viewportTop,
				viewportBottom,
				CardHeight );
		}

		public IReadOnlyList<SyntySourceAsset> VisibleOrNearAssets() =>
			_assets.Where( IsVisibleOrNearVisible ).ToArray();

		private void UpdateHeight()
		{
			FixedHeight = Math.Max( 1, (int)Math.Ceiling( _assets.Count / (float)Columns ) ) * (CardHeight + Gap) + Gap;
		}

		protected override void OnMouseMove( MouseEvent e )
		{
			base.OnMouseMove( e );
			var next = HitTest( e.LocalPosition );
			if ( next == _hovered )
				return;
			_hovered = next;
			ToolTip = next >= 0
				? _assets[next].Error ?? $"{_assets[next].Name}\n{_assets[next].PackDisplayName}"
				: null;
			Update();
		}

		protected override void OnMousePress( MouseEvent e )
		{
			base.OnMousePress( e );
			var index = HitTest( e.LocalPosition );
			if ( e.LeftMouseButton && index >= 0 )
			{
				_selection.Select(
					_assets,
					index,
					e.KeyboardModifiers.HasFlag( KeyboardModifiers.Ctrl ),
					e.KeyboardModifiers.HasFlag( KeyboardModifiers.Shift ) );
				_window.SelectionChanged( _selection.Selected.Count );
				Update();
			}
			_dragCandidate = e.LeftMouseButton ? index : -1;
		}
		protected override void OnMouseReleased( MouseEvent e )
		{
			base.OnMouseReleased( e );
			_dragCandidate = -1;
		}

		protected override void OnContextMenu( ContextMenuEvent e )
		{
			base.OnContextMenu( e );
			var index = HitTest( e.LocalPosition );
			if ( index < 0 )
				return;

			_window.ShowAssetContextMenu( _assets[index], e.ScreenPosition );
			e.Accepted = true;
		}

		protected override void OnDragStart()
		{
			if ( _dragCandidate < 0 || _dragCandidate >= _assets.Count )
				return;

			var asset = _window.PrepareAssetDrag( _assets[_dragCandidate] );
			if ( asset is null )
				return;

			var drag = new Drag( this );
			drag.Data.Text = asset.RelativePath;
			drag.Data.Url = new Uri( $"file:///{asset.AbsolutePath.Replace( '\\', '/' )}" );
			drag.Execute();
			_dragCandidate = -1;
		}

		protected override void OnMouseLeave()
		{
			base.OnMouseLeave();
			_hovered = -1;
			ToolTip = null;
			Update();
		}

		protected override void OnDoubleClick( MouseEvent e )
		{
			base.OnDoubleClick( e );
			var index = HitTest( e.LocalPosition );
			if ( index < 0 )
				return;
			_window.ImportAndSpawn( _assets[index] );
			e.Accepted = true;
		}

		protected override void OnPaint()
		{
			Paint.SetDefaultFont( 9 );
			var rowHeight = CardHeight + Gap;
			var scrollTop = _scroll is null ? ScreenRect.Top : _scroll.ScreenRect.Top;
			var viewportHeight = _scroll is null ? Math.Min( Size.y, 1000f ) : _scroll.Size.y;
			var visibleTop = Math.Max( 0f, scrollTop - ScreenRect.Top );
			Paint.ClearPen();
			Paint.SetBrush( Theme.WindowBackground );
			Paint.DrawRect( new Rect( 0, visibleTop, Size.x, viewportHeight + rowHeight ) );
			var firstRow = Math.Max( 0, (int)MathF.Floor( visibleTop / rowHeight ) );
			var lastRow = Math.Max( firstRow + 1, (int)MathF.Ceiling( (visibleTop + viewportHeight) / rowHeight ) );
			var firstIndex = firstRow * Columns;
			var lastIndex = Math.Min( _assets.Count, lastRow * Columns );
			for ( var index = firstIndex; index < lastIndex; index++ )
				DrawCard( index, _assets[index] );
		}

		private void DrawCard( int index, SyntySourceAsset source )
		{
			var card = CardRect( index );
			var imported = _window.IsImported( source );
			if ( _selection.Selected.Contains( source.CacheId ) )
				Paint.SetPen( new Color( 0.30f, 0.62f, 1.0f ), 3f );
			else if ( imported )
				Paint.SetPen( new Color( 0.45f, 0.92f, 0.58f ), 3f );
			else
				Paint.ClearPen();
			Paint.SetBrush( index == _hovered ? Theme.ControlBackground.Lighten( 0.22f ) : Theme.ControlBackground );
			Paint.DrawRect( card, 7 );
			Paint.ClearPen();
			var preview = new Rect( card.Left + 7, card.Top + 7, card.Width - 14, 116 );
			Paint.SetBrush( Theme.WindowBackground.Darken( 0.1f ) );
			Paint.DrawRect( preview, 5 );
			var pixmap = _window.GetThumbnail( source );
			if ( pixmap is not null )
				Paint.Draw( preview.Shrink( 8 ), pixmap );
			else
			{
				Paint.SetPen( Theme.TextControl.WithAlpha( 0.45f ) );
				Paint.DrawText( preview, "Importing for preview...", TextFlag.Center );
			}

			Paint.SetPen( Theme.Text );
			Paint.DrawText( new Rect( card.Left + 9, card.Bottom - 45, card.Width - 18, 22 ), source.DisplayName ?? source.Name, TextFlag.LeftCenter | TextFlag.SingleLine );
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.62f ) );
			var metadata = source.Tags.Length == 0
				? source.PackDisplayName ?? source.Category ?? "FBX"
				: $"{source.PackDisplayName ?? source.Category ?? "FBX"} · {string.Join( ", ", source.Tags.Select( tag => tag.DisplayName ) )}";
			Paint.DrawText( new Rect( card.Left + 9, card.Bottom - 24, card.Width - 18, 16 ), metadata, TextFlag.LeftCenter | TextFlag.SingleLine );
			if ( !source.CanImport )
			{
				Paint.SetPen( Theme.Red );
				Paint.DrawText( new Rect( card.Left + 10, card.Top + 10, card.Width - 20, 22 ), "Needs review", TextFlag.LeftCenter );
			}
			else if ( imported )
			{
				Paint.SetPen( Theme.Green );
				Paint.DrawText( new Rect( card.Left + 10, card.Top + 10, card.Width - 20, 22 ), "Imported", TextFlag.LeftCenter );
			}
		}

		public IReadOnlyList<SyntySourceAsset> ContextSelection( SyntySourceAsset source )
		{
			if ( !_selection.Selected.Contains( source.CacheId ) )
				return [source];
			return _assets.Where( asset => _selection.Selected.Contains( asset.CacheId ) ).ToArray();
		}
		private int HitTest( Vector2 point )
		{
			var strideX = CardWidth + Gap;
			var strideY = CardHeight + Gap;
			var column = (int)MathF.Floor( (point.x - Gap) / strideX );
			var row = (int)MathF.Floor( (point.y - Gap) / strideY );
			if ( column < 0 || column >= Columns || row < 0 )
				return -1;
			var index = row * Columns + column;
			return index >= 0 && index < _assets.Count && CardRect( index ).IsInside( point ) ? index : -1;
		}

		private Rect CardRect( int index ) =>
			new( Gap + index % Columns * (CardWidth + Gap), Gap + index / Columns * (CardHeight + Gap), CardWidth, CardHeight );
	}
}
