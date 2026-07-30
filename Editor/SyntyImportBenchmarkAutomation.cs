using System;
using System.IO;
using System.Text.Json;
using Sandbox;

namespace Editor.Tools.SyntyBrowser;

internal static class SyntyImportBenchmarkAutomation
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	private static DateTime _nextStatusWrite;
	private static bool _requestHandled;

	[EditorEvent.Frame]
	public static void Tick()
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrWhiteSpace( projectRoot ) )
			return;

		var directory = Path.Combine( projectRoot, ".sbox", "synty-import-benchmark" );
		var requestPath = Path.Combine( directory, "request.json" );
		var statusPath = Path.Combine( directory, "status.json" );
		if ( !_requestHandled && File.Exists( requestPath ) )
		{
			_requestHandled = true;
			try
			{
				var request = JsonSerializer.Deserialize<SyntyImportBenchmarkRequest>( File.ReadAllText( requestPath ), JsonOptions )
					?? throw new InvalidDataException( "Benchmark request is empty." );
				SyntyBrowserWindow.StartImportBenchmark( request.AssetCount );
				File.Delete( requestPath );
			}
			catch ( Exception exception )
			{
				WriteStatus( statusPath, new SyntyMassImportStatus { Stage = "Failed to start", LastError = exception.Message } );
			}
		}

		if ( DateTime.UtcNow < _nextStatusWrite )
			return;
		_nextStatusWrite = DateTime.UtcNow.AddMilliseconds( 250 );
		Directory.CreateDirectory( directory );
		WriteStatus( statusPath, SyntyBrowserWindow.CurrentImportStatus );
	}

	private static void WriteStatus( string path, SyntyMassImportStatus status )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		var temporary = $"{path}.tmp";
		File.WriteAllText( temporary, JsonSerializer.Serialize( status, JsonOptions ) + Environment.NewLine );
		File.Move( temporary, path, true );
	}

	private sealed record SyntyImportBenchmarkRequest
	{
		public int AssetCount { get; init; } = 1000;
	}
}
