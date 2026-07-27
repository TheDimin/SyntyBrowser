using System;
using Sandbox;

namespace Editor.Tools.SyntyBrowser;

internal static class SyntyBrowserCommands
{
	[ConCmd( "synty.set_default_shader" )]
	public static void SetDefaultShader( string pack, string shader )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( pack );
		ArgumentException.ThrowIfNullOrWhiteSpace( shader );
		var settings = SyntyBrowserSettings.LoadProject();
		if ( !settings.Packs.TryGetValue( SyntySourceCatalog.SanitizeName( pack ), out var packSettings ) )
		{
			packSettings = new SyntyPackMaterialSettings();
			settings.Packs[SyntySourceCatalog.SanitizeName( pack )] = packSettings;
		}
		packSettings.DefaultShader = shader.Trim();
		SyntyBrowserSettings.SaveProject( settings );
		Log.Info( $"Synty pack '{pack}' now uses default shader '{shader}'." );
	}
}
