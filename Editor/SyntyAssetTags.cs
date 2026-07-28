using System;
using System.Text.RegularExpressions;

namespace Editor.Tools.SyntyBrowser;

public sealed record SyntyAssetTag
{
	public required string Id { get; init; }
	public required string DisplayName { get; init; }
}

/// <summary>
/// Curated, source-catalog taxonomy. Add a definition and narrowly scoped rules here
/// when a new cross-pack browser tag is needed.
/// </summary>
public static partial class SyntyAssetTags
{
	public static readonly SyntyAssetTag HarborCity = new()
	{
		Id = "harbor-city",
		DisplayName = "Harbor City"
	};

	public static IReadOnlyList<SyntyAssetTag> All { get; } = [HarborCity];

	private static readonly HashSet<string> HarborCityTerms = new( StringComparer.OrdinalIgnoreCase )
	{
		// Harbor structures and waterfront infrastructure.
		"dock", "docks", "harbor", "harbour", "pier", "wharf", "jetty", "quay", "marina",
		"shipyard", "boathouse", "lighthouse", "warehouse",

		// Maritime vessels, equipment, fishing, and waterfront cargo.
		"boat", "canoe", "dinghy", "gondola", "raft", "ship", "vessel", "anchor", "buoy",
		"mast", "oar", "paddle", "rudder", "sail", "net", "fishing", "fish", "lobster", "crab",
		"cargo", "barrel", "crate", "rope",

		// Civic and commercial pieces that form a plausible working harbor district.
		"market", "stall", "merchant", "shop", "tavern", "inn", "city", "townhouse"
	};

	private static readonly HashSet<string> IncompatibleThemeTerms = new( StringComparer.OrdinalIgnoreCase )
	{
		"airport", "airplane", "aircraft", "spaceship", "space", "scifi", "cyber", "futuristic",
		"apocalypse", "zombie", "submarine"
	};

	public static SyntyAssetTag[] Resolve( SyntySourceAsset asset )
	{
		ArgumentNullException.ThrowIfNull( asset );
		var terms = Tokenize( $"{asset.Name} {asset.DisplayName} {asset.Category}" );
		if ( terms.Any( IncompatibleThemeTerms.Contains ) || !terms.Any( HarborCityTerms.Contains ) )
			return [];

		return [HarborCity];
	}

	public static bool Matches( SyntyAssetTag tag, string query )
	{
		if ( tag is null || string.IsNullOrWhiteSpace( query ) )
			return false;
		var normalized = Normalize( query );
		return string.Equals( tag.Id, normalized, StringComparison.OrdinalIgnoreCase )
			|| string.Equals( Normalize( tag.DisplayName ), normalized, StringComparison.OrdinalIgnoreCase );
	}

	private static string[] Tokenize( string value ) =>
		TokenSeparatorRegex().Split( value?.ToLowerInvariant() ?? "" )
			.Where( term => term.Length > 0 )
			.ToArray();

	private static string Normalize( string value ) =>
		string.Join( '-', Tokenize( value ) );

	[GeneratedRegex( @"[^a-z0-9]+", RegexOptions.CultureInvariant )]
	private static partial Regex TokenSeparatorRegex();
}
