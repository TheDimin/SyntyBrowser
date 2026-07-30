using System;
using System.IO;
using System.Text.Json;
using Sandbox;

namespace Editor.Tools.SyntyBrowser;

public sealed class SyntyBrowserProjectSettings
{
	public Dictionary<string, SyntyPackMaterialSettings> Packs { get; set; } = new( StringComparer.OrdinalIgnoreCase );
	public Dictionary<string, SyntyAssetTagOverride> TagOverrides { get; set; } = new( StringComparer.OrdinalIgnoreCase );
}

public sealed class SyntyPackMaterialSettings
{
	public string DefaultShader { get; set; }
	public Dictionary<string, SyntyMaterialMapping> Materials { get; set; } = new( StringComparer.OrdinalIgnoreCase );
	public Dictionary<string, SyntyMaterialSlotOverride> SlotOverrides { get; set; } = new( StringComparer.OrdinalIgnoreCase );
}

public sealed class SyntyMaterialMapping
{
	public string Shader { get; set; }
	public Dictionary<string, string> Parameters { get; set; } = new( StringComparer.OrdinalIgnoreCase );
}

public static class SyntyBrowserSettings
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public static string SourceRoot
	{
		get => File.Exists( LocalSettingsPath ) ? File.ReadAllText( LocalSettingsPath ).Trim() : "";
		set
		{
			Directory.CreateDirectory( Path.GetDirectoryName( LocalSettingsPath ) );
			File.WriteAllText( LocalSettingsPath, value?.Trim() ?? "" );
		}
	}

	public static SyntyBrowserProjectSettings LoadProject()
	{
		var path = ProjectSettingsPath;
		if (!File.Exists( path ))
			return new();
		try
		{
			return JsonSerializer.Deserialize<SyntyBrowserProjectSettings>( File.ReadAllText( path ), JsonOptions ) ?? new();
		}
		catch ( Exception exception )
		{
			throw new InvalidDataException( $"Could not read Synty Browser settings '{path}'.", exception );
		}
	}

	public static void SaveProject( SyntyBrowserProjectSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		Directory.CreateDirectory( Path.GetDirectoryName( ProjectSettingsPath ) );
		var temporary = $"{ProjectSettingsPath}.tmp";
		File.WriteAllText( temporary, JsonSerializer.Serialize( settings, JsonOptions ) + Environment.NewLine );
		File.Move( temporary, ProjectSettingsPath, true );
	}

	public static string ProjectSettingsPath => Path.Combine(
		Project.Current?.GetRootPath() ?? Directory.GetCurrentDirectory(),
		"ProjectSettings",
		"SyntyBrowser.json" );

	private static string LocalSettingsPath => Path.Combine(
		Project.Current?.GetRootPath() ?? Directory.GetCurrentDirectory(),
		".sbox",
		"synty-browser",
		"source-root.txt" );

}
