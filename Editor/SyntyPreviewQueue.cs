using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace Editor.Tools.SyntyBrowser;

public sealed record SyntyPreviewQueueSnapshot(
	int Pending,
	bool WorkerRunning,
	int Completed,
	int Skipped,
	int Failed );

public sealed class SyntyPreviewQueue
{
	public const int MaximumPending = 48;
	public const int BatchSize = 12;
	public const int MaximumAutomaticAttempts = 2;
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	private readonly object _sync = new();
	private readonly Dictionary<string, SyntySourceAsset> _pending = new( StringComparer.OrdinalIgnoreCase );
	private readonly SyntyThumbnailScheduler _scheduler = new( MaximumPending );
	private readonly SyntyPreviewStateStore _states;
	private readonly string _sourceRoot;
	private readonly string _cacheRoot;
	private readonly string _workerScript;
	private bool _workerRunning;

	public SyntyPreviewQueue( string sourceRoot, string cacheRoot, string workerScript )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( sourceRoot );
		ArgumentException.ThrowIfNullOrWhiteSpace( cacheRoot );
		ArgumentException.ThrowIfNullOrWhiteSpace( workerScript );
		_sourceRoot = Path.GetFullPath( sourceRoot );
		_cacheRoot = Path.GetFullPath( cacheRoot );
		_workerScript = Path.GetFullPath( workerScript );
		_states = new( _cacheRoot );
		_states.RecoverInterrupted();
	}

	public event Action Changed;

	public int PendingCount
	{
		get
		{
			lock ( _sync )
				return _pending.Count;
		}
	}

	public bool Queue( SyntySourceAsset source, bool isVisible, bool forceRetry = false )
	{
		if ( !SyntyPreviewEligibility.CanGenerate( source ) || !isVisible && !forceRetry )
			return false;
		var output = SyntyPreviewCache.GetPath( _cacheRoot, source );
		if ( SyntyPreviewMigration.HasPngSignature( output ) )
			return false;

		lock ( _sync )
		{
			var previous = _states.Read( source );
			if ( !forceRetry && previous?.Status is SyntyPreviewJobStatus.Skipped )
				return false;
			if ( !forceRetry && previous?.Status is SyntyPreviewJobStatus.Failed
				&& !SyntyPreviewRetryPolicy.CanAutomaticallyRetry( previous, MaximumAutomaticAttempts ) )
				return false;
			if ( !_scheduler.TryQueue( source.CacheId, isVisible || forceRetry ) )
				return false;
			_pending[source.CacheId] = source;
			_states.Write( source, new SyntyPreviewJobState
			{
				AssetId = source.CacheId,
				Status = SyntyPreviewJobStatus.Pending,
				Attempts = forceRetry ? 0 : previous?.Attempts ?? 0,
				Error = null
			} );
			if ( !_workerRunning )
			{
				_workerRunning = true;
				_ = RunWorkerLoop();
			}
		}
		Changed?.Invoke();
		return true;
	}

	public int QueueMany( IEnumerable<SyntySourceAsset> sources, bool forceRetry = false )
	{
		var queued = 0;
		foreach ( var source in sources ?? [] )
			if ( Queue( source, isVisible: true, forceRetry ) )
				queued++;
		return queued;
	}

	public SyntyPreviewQueueSnapshot Snapshot()
	{
		var status = _states.GetStatus();
		lock ( _sync )
			return new( _pending.Count, _workerRunning, status.Completed, status.Skipped, status.Failed );
	}

	public int RetryFailed( IEnumerable<SyntySourceAsset> sources ) =>
		QueueMany( (sources ?? []).Where( source => _states.Read( source )?.Status is SyntyPreviewJobStatus.Failed ), true );

	private async Task RunWorkerLoop()
	{
		while ( true )
		{
			await Task.Delay( 250 );
			SyntySourceAsset[] batch;
			lock ( _sync )
			{
				batch = _pending.Values.Take( BatchSize ).ToArray();
				foreach ( var source in batch )
					_pending.Remove( source.CacheId );
			}

			if ( batch.Length == 0 )
			{
				await Task.Delay( TimeSpan.FromSeconds( 30 ) );
				lock ( _sync )
				{
					if ( _pending.Count > 0 )
						continue;
					_workerRunning = false;
				}
				Changed?.Invoke();
				return;
			}

			await RenderBatch( batch );
			Changed?.Invoke();
		}
	}

	private async Task RenderBatch( SyntySourceAsset[] batch )
	{
		var workRoot = Path.Combine( Path.GetTempPath(), $"synty-preview-request-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( workRoot );
		var requestPath = Path.Combine( workRoot, "requests.json" );
		var resultPath = Path.Combine( workRoot, "results.json" );
		try
		{
			foreach ( var source in batch )
			{
				var previous = _states.Read( source );
				_states.Write( source, new SyntyPreviewJobState
				{
					AssetId = source.CacheId,
					Status = SyntyPreviewJobStatus.Rendering,
					Attempts = (previous?.Attempts ?? 0) + 1
				} );
			}

			var requests = batch.Select( source => new
			{
				asset_id = source.CacheId,
				source_fbx = source.SourceFbxPath,
				pack_root = source.PackRootPath,
				output_png = SyntyPreviewCache.GetPath( _cacheRoot, source ),
				bindings = SyntyPreviewTextureResolver.Bindings( source ).Select( binding => new
				{
					mesh_name = binding.MeshName,
					slot_name = binding.SlotName,
					slot_ordinal = binding.SlotOrdinal,
					texture_hint = binding.TextureHint
				} ).ToArray()
			} ).ToArray();
			await File.WriteAllTextAsync( requestPath, JsonSerializer.Serialize( requests, JsonOptions ) );

			var command = $"& '{EscapePowerShell( _workerScript )}'"
				+ $" -RequestManifest '{EscapePowerShell( requestPath )}'"
				+ $" -ResultManifest '{EscapePowerShell( resultPath )}'"
				+ $" -SourceRoot '{EscapePowerShell( _sourceRoot )}'"
				+ $" -CacheRoot '{EscapePowerShell( _cacheRoot )}'";
			var encoded = Convert.ToBase64String( Encoding.Unicode.GetBytes( command ) );
			var launcherPath = Path.Combine( workRoot, "launch-hidden.vbs" );
			await File.WriteAllTextAsync(
				launcherPath,
				$"CreateObject(\"WScript.Shell\").Run \"powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}\", 0, True" );
			EditorUtility.OpenFile( launcherPath );

			var deadline = DateTimeOffset.UtcNow.AddMinutes( 30 );
			while ( !File.Exists( resultPath ) && DateTimeOffset.UtcNow < deadline )
				await Task.Delay( 250 );
			if ( !File.Exists( resultPath ) )
				throw new TimeoutException( "Synty preview worker did not finish within 30 minutes." );

			var results = File.Exists( resultPath )
				? JsonSerializer.Deserialize<SyntyPreviewWorkerResult[]>( await File.ReadAllTextAsync( resultPath ),
					new JsonSerializerOptions { PropertyNameCaseInsensitive = true } ) ?? []
				: [];
			foreach ( var source in batch )
			{
				var result = results.FirstOrDefault( item => string.Equals( item.AssetId, source.CacheId, StringComparison.OrdinalIgnoreCase ) );
				var previous = _states.Read( source );
				var outputExists = SyntyPreviewMigration.HasPngSignature( SyntyPreviewCache.GetPath( _cacheRoot, source ) );
				var status = outputExists
					? SyntyPreviewJobStatus.Completed
					: result?.Status?.Equals( "skipped", StringComparison.OrdinalIgnoreCase ) is true
						? SyntyPreviewJobStatus.Skipped
						: SyntyPreviewJobStatus.Failed;
				_states.Write( source, new SyntyPreviewJobState
				{
					AssetId = source.CacheId,
					Status = status,
					Attempts = previous?.Attempts ?? 1,
					Error = outputExists ? null : result?.Error ?? "Worker did not produce a valid PNG."
				} );
				_scheduler.Complete( source.CacheId );
				if ( status is SyntyPreviewJobStatus.Failed
					&& SyntyPreviewRetryPolicy.CanAutomaticallyRetry( _states.Read( source ), MaximumAutomaticAttempts ) )
					Queue( source, true );
			}
		}
		catch ( Exception exception )
		{
			foreach ( var source in batch )
			{
				var previous = _states.Read( source );
				_states.Write( source, new SyntyPreviewJobState
				{
					AssetId = source.CacheId,
					Status = SyntyPreviewJobStatus.Failed,
					Attempts = previous?.Attempts ?? 1,
					Error = exception.Message
				} );
				_scheduler.Complete( source.CacheId );
				if ( SyntyPreviewRetryPolicy.CanAutomaticallyRetry( _states.Read( source ), MaximumAutomaticAttempts ) )
					Queue( source, true );
			}
		}
		finally
		{
			try
			{
				Directory.Delete( workRoot, true );
			}
			catch
			{
			}
		}
	}

	private static string EscapePowerShell( string value ) => value.Replace( "'", "''" );

	private sealed record SyntyPreviewWorkerResult
	{
		public string AssetId { get; init; }
		public string Status { get; init; }
		public string Error { get; init; }
	}
}
