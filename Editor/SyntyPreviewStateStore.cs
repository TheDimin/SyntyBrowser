using System;
using System.IO;
using System.Text.Json;

namespace Editor.Tools.SyntyBrowser;

public sealed record SyntyPreviewCacheStatus(
	string CacheRoot,
	int Completed,
	int Pending,
	int Rendering,
	int Skipped,
	int Failed );

public sealed class SyntyPreviewStateStore
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	private readonly string _stateRoot;

	public SyntyPreviewStateStore( string cacheRoot )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( cacheRoot );
		CacheRoot = Path.GetFullPath( cacheRoot );
		_stateRoot = Path.Combine( SyntyPreviewCache.StateRoot( CacheRoot ), "jobs" );
	}

	public string CacheRoot { get; }

	public SyntyPreviewJobState Read( SyntySourceAsset source )
	{
		var path = GetPath( source );
		if ( !File.Exists( path ) )
			return null;
		try
		{
			return JsonSerializer.Deserialize<SyntyPreviewJobState>( File.ReadAllText( path ), JsonOptions );
		}
		catch
		{
			return null;
		}
	}

	public void Write( SyntySourceAsset source, SyntyPreviewJobState state )
	{
		var path = GetPath( source );
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
		File.WriteAllText( temporary, JsonSerializer.Serialize( state with { UpdatedAt = DateTimeOffset.UtcNow }, JsonOptions ) );
		File.Move( temporary, path, true );
	}

	public void RecoverInterrupted()
	{
		if ( !Directory.Exists( _stateRoot ) )
			return;
		foreach ( var path in Directory.EnumerateFiles( _stateRoot, "*.json", SearchOption.AllDirectories ) )
		{
			try
			{
				var state = JsonSerializer.Deserialize<SyntyPreviewJobState>( File.ReadAllText( path ), JsonOptions );
				if ( state?.Status is not SyntyPreviewJobStatus.Rendering )
					continue;
				var recovered = state with
				{
					Status = SyntyPreviewJobStatus.Failed,
					Error = "Preview worker was interrupted before completion.",
					UpdatedAt = DateTimeOffset.UtcNow
				};
				var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
				File.WriteAllText( temporary, JsonSerializer.Serialize( recovered, JsonOptions ) );
				File.Move( temporary, path, true );
			}
			catch
			{
			}
		}
	}

	public SyntyPreviewCacheStatus GetStatus()
	{
		var counts = Enum.GetValues<SyntyPreviewJobStatus>().ToDictionary( value => value, _ => 0 );
		if ( Directory.Exists( _stateRoot ) )
		{
			foreach ( var path in Directory.EnumerateFiles( _stateRoot, "*.json", SearchOption.AllDirectories ) )
			{
				try
				{
					var state = JsonSerializer.Deserialize<SyntyPreviewJobState>( File.ReadAllText( path ), JsonOptions );
					if ( state is not null )
						counts[state.Status]++;
				}
				catch
				{
				}
			}
		}
		var completedFiles = Directory.Exists( Path.Combine( CacheRoot, "previews", SyntyPreviewCache.RendererVersion ) )
			? Directory.EnumerateFiles( Path.Combine( CacheRoot, "previews", SyntyPreviewCache.RendererVersion ), "*.png", SearchOption.AllDirectories ).Count()
			: 0;
		return new( CacheRoot, Math.Max( completedFiles, counts[SyntyPreviewJobStatus.Completed] ),
			counts[SyntyPreviewJobStatus.Pending], counts[SyntyPreviewJobStatus.Rendering],
			counts[SyntyPreviewJobStatus.Skipped], counts[SyntyPreviewJobStatus.Failed] );
	}

	private string GetPath( SyntySourceAsset source ) => Path.Combine(
		_stateRoot,
		SyntySourceCatalog.SanitizeName( source.PackName ?? source.PackDisplayName ?? "pack" ),
		$"{SyntySourceCatalog.NormalizeId( source.Id )}.json" );
}
