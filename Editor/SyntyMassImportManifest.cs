using System;
using System.IO;
using System.Text.Json;

namespace Editor.Tools.SyntyBrowser;

public sealed class SyntyMassImportManifest
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	public HashSet<string> Prepared { get; set; } = new( StringComparer.OrdinalIgnoreCase );
	public HashSet<string> Finalized { get; set; } = new( StringComparer.OrdinalIgnoreCase );
	public Dictionary<string, string> Failures { get; set; } = new( StringComparer.OrdinalIgnoreCase );

	public static SyntyMassImportManifest Load( string path )
	{
		if ( !File.Exists( path ) )
			return new();
		try
		{
			var loaded = JsonSerializer.Deserialize<SyntyMassImportManifest>( File.ReadAllText( path ), JsonOptions ) ?? new();
			return new SyntyMassImportManifest
			{
				Prepared = new HashSet<string>( loaded.Prepared ?? [], StringComparer.OrdinalIgnoreCase ),
				Finalized = new HashSet<string>( loaded.Finalized ?? [], StringComparer.OrdinalIgnoreCase ),
				Failures = new Dictionary<string, string>( loaded.Failures ?? [], StringComparer.OrdinalIgnoreCase )
			};
		}
		catch
		{
			return new();
		}
	}

	public void Save( string path )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		var temporary = $"{path}.tmp";
		File.WriteAllText( temporary, JsonSerializer.Serialize( this, JsonOptions ) + Environment.NewLine );
		File.Move( temporary, path, true );
	}
}
