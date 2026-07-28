using System;

namespace Editor.Tools.SyntyBrowser;

public static class SyntyMaterialImportDefaults
{
	public static Dictionary<string, string> ParametersFor( string shader )
	{
		if ( IsShader( shader, "synty_world" ) )
		{
			return new( StringComparer.OrdinalIgnoreCase )
			{
				["F_SYNTY_WORLD_VARIATION_PATTERN"] = "1",
				["SyntyWorldRoughness"] = "0.77",
				["SyntyWorldVariationSize"] = "20",
				["SyntyWorldVariationContrast"] = "0.30",
				["SyntyWorldColorVariation"] = "0.025",
				["SyntyWorldNormalVariation"] = "0.018",
				["SyntyWorldRoughnessVariation"] = "0.055",
				["SyntyWorldInstanceVariation"] = "0.012",
				["SyntyWorldMicroVariationSize"] = "6.5",
				["SyntyWorldMicroColorVariation"] = "0.035",
				["SyntyWorldMicroRoughnessVariation"] = "0.03",
				["SyntyWorldMicroNormalVariation"] = "0.02",
				["SyntyWorldWetnessResponse"] = "0.72",
				["SyntyWorldMossResponse"] = "0.55",
				["SyntyWorldDustResponse"] = "0.70"
			};
		}

		if ( IsShader( shader, "synty_foliage" ) )
		{
			return new( StringComparer.OrdinalIgnoreCase )
			{
				["SyntyFoliageLeafSmoothness"] = "0.25",
				["SyntyFoliageLeafNormalStrength"] = "0",
				["SyntyFoliageRooted"] = "1",
				["SyntyFoliageBaseWindInfluence"] = "1"
			};
		}

		return new( StringComparer.OrdinalIgnoreCase );
	}

	public static string[] TextureParametersFor( string shader ) =>
		IsShader( shader, "synty_foliage" )
			? ["LeafTexture", "TrunkTexture"]
			: ["TextureColor"];

	private static bool IsShader( string shader, string name ) =>
		!string.IsNullOrWhiteSpace( shader )
		&& shader.Replace( '\\', '/' ).Contains( $"/{name}.shader", StringComparison.OrdinalIgnoreCase );
}
