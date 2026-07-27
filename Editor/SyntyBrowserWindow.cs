using System;
using System.Diagnostics;
using System.IO;
using Sandbox;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Tools.SyntyBrowser;


[Dock( "Editor", "Synty Browser", "view_in_ar" )]
public sealed class SyntyBrowserWindow : Widget
{
	private const string DockTitle = "Synty Browser";
	private readonly LineEdit _sourceRoot;
	private readonly LineEdit _search;
	private readonly Label _status;
	private readonly SyntyScrollArea _scroll;
	private readonly SyntyAssetGrid _grid;
	private readonly Dictionary<string, Pixmap> _thumbnailCache = new( StringComparer.OrdinalIgnoreCase );
	private readonly SyntyThumbnailScheduler _thumbnailScheduler = new( 8 );
	private readonly SemaphoreSlim _thumbnailProducer = new( 1, 1 );
	private SyntySourceCatalogResult _catalog;
	private int _refreshRevision;

	public SyntyBrowserWindow() : this( null ) { }

	public SyntyBrowserWindow( Widget parent ) : base( parent )
	{
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
		_search = searchRow.Add( new LineEdit( "" ) { PlaceholderText = "Search Synty assets..." }, 1 );
		_search.TextEdited += _ => ApplySearch();
		_status = Layout.Add( new Label( "Choose a Synty pack folder." )
		{
			WordWrap = true,
			MinimumWidth = 0
		} );

		_scroll = Layout.Add( new SyntyScrollArea(), 1 );
		_grid = new SyntyAssetGrid( this, _scroll );
		_scroll.Canvas = _grid;
		_scroll.ViewportChanged = () =>
		{
			_grid.SetViewportWidth( _scroll.Size.x );
			_grid.Update();
		};
		if ( Directory.Exists( SyntyBrowserSettings.SourceRoot ) )
			Refresh();
	}

	protected override void OnResize()
	{
		base.OnResize();
		if ( _grid is not null && _scroll is not null )
			_grid.SetViewportWidth( _scroll.Size.x );
	}

	public static SyntyBrowserWindow OpenDock()
	{
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
			if ( revision != _refreshRevision )
				return;
			_catalog = catalog;
			SyntyBrowserSettings.SourceRoot = _catalog.RootPath;
			ApplySearch();
			_status.Text = _catalog.IsLibrary
				? $"{_catalog.PackCount} packs · {_catalog.Assets.Length:N0} assets · {_catalog.Assets.Count( asset => !asset.CanImport ):N0} need review"
				: $"{new DirectoryInfo( _catalog.RootPath ).Name} · {_catalog.Assets.Length:N0} assets · {_catalog.Assets.Count( asset => !asset.CanImport ):N0} need review";
		}
		catch ( Exception exception )
		{
			if ( revision != _refreshRevision )
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

	internal Pixmap GetThumbnail( SyntySourceAsset source )
	{
		var asset = AssetSystem.FindByPath( ModelPath( source ) );
		var imported = asset?.GetAssetThumb( false );
		if ( imported is not null )
			return imported;
		if ( _thumbnailCache.TryGetValue( source.CacheId, out var cached ) )
			return cached;
		var previewPath = SyntyPreviewCache.GetPath( Project.Current.GetRootPath(), source );
		if ( File.Exists( previewPath ) )
		{
			var preview = Pixmap.FromFile( previewPath );
			if ( preview is not null )
			{
				_thumbnailCache[source.CacheId] = preview;
				return preview;
			}
		}
		return null;
	}

	internal bool RequestThumbnail( SyntySourceAsset source )
	{
		if ( source is null || !source.CanImport )
			return false;

		var outputPath = SyntyPreviewCache.GetPath( Project.Current.GetRootPath(), source );
		if ( File.Exists( outputPath ) )
			return false;

		if ( !_thumbnailScheduler.TryQueue( source.CacheId, _grid.IsVisibleOrNearVisible( source ) ) )
			return false;
		_ = GenerateThumbnailAsync( source, outputPath );
		return true;
	}

	internal int PendingThumbnailCount => _thumbnailScheduler.PendingCount;

	private async Task GenerateThumbnailAsync( SyntySourceAsset source, string outputPath )
	{
		var bindingsPath = Path.Combine(
			Project.Current.GetRootPath(),
			".sbox",
			"synty-browser",
			$"{source.CacheId}-{Guid.NewGuid():N}.json" );
		await _thumbnailProducer.WaitAsync();
		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( bindingsPath )! );
			await File.WriteAllTextAsync(
				bindingsPath,
				JsonSerializer.Serialize( SyntyPreviewTextureResolver.Bindings( source ) ) );
			var scriptPath = Path.Combine(
				Project.Current.GetRootPath(),
				"Libraries",
				"SyntyBrowser",
				"Tools",
				"generate-preview.ps1" );
			if ( !File.Exists( scriptPath ) )
				throw new FileNotFoundException( "The Synty Browser offline preview producer was not found.", scriptPath );

			var startInfo = new ProcessStartInfo
			{
				FileName = "powershell.exe",
				CreateNoWindow = true,
				UseShellExecute = false,
				WindowStyle = ProcessWindowStyle.Hidden
			};
			foreach ( var argument in new[]
			{
				"-NoProfile",
				"-NonInteractive",
				"-ExecutionPolicy", "Bypass",
				"-File", scriptPath,
				"-SourceFbx", source.SourceFbxPath,
				"-PackRoot", source.PackRootPath,
				"-OutputPng", outputPath,
				"-BindingsJson", bindingsPath
			} )
				startInfo.ArgumentList.Add( argument );

			using var process = Process.Start( startInfo )
				?? throw new InvalidOperationException( "Could not start the offline preview producer." );
			await process.WaitForExitAsync();
			if ( process.ExitCode != 0 || !File.Exists( outputPath ) )
				throw new InvalidOperationException( $"Offline preview generation failed for {source.Name}." );

			var preview = Pixmap.FromFile( outputPath );
			if ( preview is not null )
				_thumbnailCache[source.CacheId] = preview;
		}
		catch ( Exception exception )
		{
			_status.Text = $"Preview failed for {source.DisplayName ?? source.Name}: {exception.Message}";
		}
		finally
		{
			_thumbnailProducer.Release();
			_thumbnailScheduler.Complete( source.CacheId );
			File.Delete( bindingsPath );
			_grid.Update();
		}
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
			ShowDefaultShaderPicker( source, packName );
			return;
		}
		var missing = source.Meshes.SelectMany( mesh => mesh.Materials )
			.FirstOrDefault( slot => slot.UsesCustomShader && !packSettings.Materials.ContainsKey( slot.Name ) );
		if ( missing is not null )
		{
			_status.Text = $"Custom material '{missing.Name}' needs a mapping in ProjectSettings/SyntyBrowser.json.";
			return;
		}
		_status.Text = $"Importing {source.Name}...";
		var result = SyntyImportService.Import( _catalog, source, packSettings );
		_status.Text = result.Success ? $"Imported {source.Name}" : $"Import failed: {result.Error}";
		_grid.Update();
	}

	private void ShowDefaultShaderPicker( SyntySourceAsset source, string packName )
	{
		var picker = AssetPicker.Create( this, AssetType.FromType( typeof( Shader ) ), new AssetPicker.PickerOptions
		{
			EnableCloud = false,
			EnableMultiselect = false
		} );
		picker.Title = $"Choose default shader for {source.PackDisplayName ?? packName}";
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
			return card.Bottom >= viewportTop - CardHeight && card.Top <= viewportBottom + CardHeight;
		}

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
			_dragCandidate = e.LeftMouseButton ? HitTest( e.LocalPosition ) : -1;
		}

		protected override void OnMouseReleased( MouseEvent e )
		{
			base.OnMouseReleased( e );
			_dragCandidate = -1;
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
			if ( imported )
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
				_window.RequestThumbnail( source );
				Paint.SetPen( Theme.TextControl.WithAlpha( 0.45f ) );
				Paint.DrawText( preview, "Preview not cached", TextFlag.Center );
			}

			Paint.SetPen( Theme.Text );
			Paint.DrawText( new Rect( card.Left + 9, card.Bottom - 45, card.Width - 18, 22 ), source.DisplayName ?? source.Name, TextFlag.LeftCenter | TextFlag.SingleLine );
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.62f ) );
			Paint.DrawText( new Rect( card.Left + 9, card.Bottom - 24, card.Width - 18, 16 ), source.PackDisplayName ?? source.Category ?? "FBX", TextFlag.LeftCenter | TextFlag.SingleLine );
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
