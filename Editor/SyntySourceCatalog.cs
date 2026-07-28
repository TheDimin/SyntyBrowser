using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Editor.Tools.SyntyBrowser;

public sealed record SyntyMaterialSlot
{
	public required string Name { get; init; }
	public string TextureHint { get; init; }
	public bool UsesCustomShader { get; init; }
}

public sealed record SyntyMeshEntry
{
	public required string Name { get; init; }
	public int? LodLevel { get; init; }
	public string LodFamily { get; init; }
	public SyntyMaterialSlot[] Materials { get; init; } = [];
}

public sealed record SyntySourceAsset
{
	public required string Id { get; init; }
	public required string Name { get; init; }
	public string DisplayName { get; init; }
	public string Category { get; init; }
	public string PackName { get; init; }
	public string PackDisplayName { get; init; }
	public string PackRootPath { get; init; }
	public string CacheId => $"{PackName}_{Id}";
	public SyntyAssetTag[] Tags { get; init; } = [];
	public string SourceFbxPath { get; init; }
	public SyntyMeshEntry[] Meshes { get; init; } = [];
	public bool IsFallback { get; init; }
	public string Error { get; init; }
	public bool CanImport => string.IsNullOrWhiteSpace( Error ) && File.Exists( SourceFbxPath );
}

public sealed record SyntySourceCatalogResult
{
	public required string PackName { get; init; }
	public required string RootPath { get; init; }
	public string MaterialListPath { get; init; }
	public SyntySourceAsset[] Assets { get; init; } = [];
	public string[] Warnings { get; init; } = [];
	public int PackCount { get; init; } = 1;
	public bool IsLibrary => PackCount > 1;
}

public static partial class SyntySourceCatalog
{
	private static readonly Regex LodRegex = new(
		@"^(?<family>.+?)_LOD(?<level>\d+)$",
		RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

	public static SyntySourceCatalogResult Build( string rootPath )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( rootPath );
		var root = Path.GetFullPath( rootPath );
		if ( !Directory.Exists( root ) )
			throw new DirectoryNotFoundException( $"Synty pack folder '{root}' does not exist." );

		var packRoots = FindPackRoots( root );
		if ( packRoots.Length > 1 )
		{
			var packs = packRoots.Select( BuildPack ).ToArray();
			return new SyntySourceCatalogResult
			{
				PackName = SanitizeName( new DirectoryInfo( root ).Name ),
				RootPath = root,
				PackCount = packs.Length,
				Assets = packs.SelectMany( pack => pack.Assets )
					.OrderBy( asset => asset.PackDisplayName, StringComparer.OrdinalIgnoreCase )
					.ThenBy( asset => asset.DisplayName, StringComparer.OrdinalIgnoreCase )
					.ToArray(),
				Warnings = packs.SelectMany( pack => pack.Warnings ).ToArray()
			};
		}

		return BuildPack( packRoots[0] );
	}

	private static SyntySourceCatalogResult BuildPack( string root )
	{
		var packDisplayName = new DirectoryInfo( root ).Name;
		var packName = SanitizeName( packDisplayName );
		var fbxFiles = Directory.EnumerateFiles( root, "*.fbx", SearchOption.AllDirectories )
			.OrderBy( path => path, StringComparer.OrdinalIgnoreCase )
			.ToArray();
		var fbxByName = fbxFiles
			.GroupBy( path => Path.GetFileNameWithoutExtension( path ), StringComparer.OrdinalIgnoreCase )
			.ToDictionary( group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase );
		var listPath = Directory.EnumerateFiles( root, "MaterialList_*.txt", SearchOption.TopDirectoryOnly )
			.OrderBy( path => path, StringComparer.OrdinalIgnoreCase )
			.FirstOrDefault();
		var warnings = new List<string>();
		var assets = listPath is null
			? []
			: ParseMaterialList( File.ReadAllLines( listPath ), fbxByName, warnings ).ToList();

		var claimed = assets
			.Where( asset => !string.IsNullOrWhiteSpace( asset.SourceFbxPath ) )
			.Select( asset => Path.GetFullPath( asset.SourceFbxPath ) )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );
		foreach ( var fbx in fbxFiles.Where( path => !claimed.Contains( Path.GetFullPath( path ) ) ) )
		{
			var name = Path.GetFileNameWithoutExtension( fbx );
			if ( IsUnsupportedStaticModel( name ) || IsAuxiliaryModel( name ) )
				continue;
			assets.Add( new SyntySourceAsset
			{
				Id = NormalizeId( name ),
				Name = name,
				DisplayName = SyntyAssetNaming.ToDisplayName( name ),
				Category = "FBX",
				PackName = packName,
				PackDisplayName = packDisplayName,
				PackRootPath = root,
				SourceFbxPath = fbx,
				IsFallback = true
			} );
		}

		assets = assets.Select( asset => asset with
		{
			DisplayName = asset.DisplayName ?? SyntyAssetNaming.ToDisplayName( asset.Name ),
			PackName = packName,
			PackDisplayName = packDisplayName,
			PackRootPath = root,
			Tags = SyntyAssetTags.Resolve( asset with
			{
				DisplayName = asset.DisplayName ?? SyntyAssetNaming.ToDisplayName( asset.Name )
			} )
		} ).ToList();

		return new SyntySourceCatalogResult
		{
			PackName = packName,
			RootPath = root,
			MaterialListPath = listPath,
			Assets = assets
				.Where( asset => !IsAuxiliaryModel( asset.Name ) )
				.GroupBy( asset => asset.Id, StringComparer.OrdinalIgnoreCase )
				.Select( group => group.First() )
				.OrderBy( asset => asset.DisplayName, StringComparer.OrdinalIgnoreCase )
				.ToArray(),
			Warnings = warnings.ToArray()
		};
	}

	private static string[] FindPackRoots( string root )
	{
		if ( Directory.EnumerateFiles( root, "MaterialList_*.txt", SearchOption.TopDirectoryOnly ).Any()
			|| Directory.EnumerateFiles( root, "*.fbx", SearchOption.TopDirectoryOnly ).Any() )
			return [root];

		var children = Directory.EnumerateDirectories( root )
			.Where( directory => Directory.EnumerateFiles( directory, "*.fbx", SearchOption.AllDirectories ).Any() )
			.OrderBy( directory => directory, StringComparer.OrdinalIgnoreCase )
			.ToArray();
		return children.Length == 0 ? [root] : children;
	}

	public static IReadOnlyList<SyntySourceAsset> ParseMaterialList(
		IEnumerable<string> lines,
		IReadOnlyDictionary<string, string[]> fbxByName,
		ICollection<string> warnings )
	{
		var assets = new List<SyntySourceAsset>();
		string category = null;
		string prefab = null;
		var meshes = new List<(string Name, List<SyntyMaterialSlot> Materials)>();

		void Flush()
		{
			if ( string.IsNullOrWhiteSpace( prefab ) )
				return;

			var matches = fbxByName.TryGetValue( prefab, out var exact ) ? exact : [];
			var match = ResolveFbxCandidate( matches );
			var error = matches.Length switch
			{
				0 => $"No FBX named '{prefab}.fbx' was found.",
				> 1 when match is null => $"Multiple FBXs named '{prefab}.fbx' were found outside a unique canonical FBX path.",
				_ => null
			};
			if ( error is not null )
				warnings?.Add( $"{prefab}: {error}" );

			assets.Add( new SyntySourceAsset
			{
				Id = NormalizeId( prefab ),
				Name = prefab,
				Category = category,
				SourceFbxPath = match,
				Meshes = meshes.Select( mesh =>
				{
					var lod = LodRegex.Match( mesh.Name );
					return new SyntyMeshEntry
					{
						Name = mesh.Name,
						LodLevel = lod.Success ? int.Parse( lod.Groups["level"].Value ) : null,
						LodFamily = lod.Success ? lod.Groups["family"].Value : mesh.Name,
						Materials = mesh.Materials.ToArray()
					};
				} ).ToArray(),
				Error = error
			} );
			prefab = null;
			meshes.Clear();
		}

		foreach ( var rawLine in lines ?? [] )
		{
			var line = rawLine?.Trim() ?? "";
			if ( line.StartsWith( "Folder Name:", StringComparison.OrdinalIgnoreCase ) )
			{
				category = line["Folder Name:".Length..].Trim();
			}
			else if ( line.StartsWith( "Prefab Name:", StringComparison.OrdinalIgnoreCase ) )
			{
				Flush();
				prefab = line["Prefab Name:".Length..].Trim();
			}
			else if ( line.StartsWith( "Mesh Name:", StringComparison.OrdinalIgnoreCase ) && prefab is not null )
			{
				meshes.Add( (line["Mesh Name:".Length..].Trim(), []) );
			}
			else if ( line.StartsWith( "Slot:", StringComparison.OrdinalIgnoreCase ) && meshes.Count > 0 )
			{
				var value = line["Slot:".Length..].Trim();
				var open = value.LastIndexOf( " (", StringComparison.Ordinal );
				var detail = open >= 0 && value.EndsWith( ')' ) ? value[(open + 2)..^1].Trim() : null;
				var name = open >= 0 ? value[..open].Trim() : value;
				meshes[^1].Materials.Add( new SyntyMaterialSlot
				{
					Name = name,
					UsesCustomShader = string.Equals( detail, "Uses custom shader", StringComparison.OrdinalIgnoreCase ),
					TextureHint = string.Equals( detail, "Uses custom shader", StringComparison.OrdinalIgnoreCase ) ? null : detail
				} );
			}
		}
		Flush();
		return assets;
	}

	public static string ResolveFbxCandidate( IReadOnlyList<string> matches )
	{
		if ( matches is null || matches.Count == 0 ) return null;
		if ( matches.Count == 1 ) return matches[0];
		var canonical = matches.Where( path => Path.GetDirectoryName( path )
			?.Split( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar )
			.Any( part => string.Equals( part, "FBX", StringComparison.OrdinalIgnoreCase ) ) == true ).ToArray();
		return canonical.Length == 1 ? canonical[0] : null;
	}
	public static string NormalizeId( string value )
	{
		var normalized = Regex.Replace( value?.Trim().ToLowerInvariant() ?? "", @"[^a-z0-9]+", "_" ).Trim( '_' );
		return string.IsNullOrWhiteSpace( normalized ) ? "asset" : normalized;
	}

	public static string SanitizeName( string value ) => NormalizeId( value );

	private static bool IsUnsupportedStaticModel( string name )
	{
		return name.StartsWith( "A_", StringComparison.OrdinalIgnoreCase )
			|| name.StartsWith( "SK_", StringComparison.OrdinalIgnoreCase );
	}

	public static bool IsAuxiliaryModel( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return false;
		return name.EndsWith( "_Collision", StringComparison.OrdinalIgnoreCase )
			|| name.Contains( "_Collision_", StringComparison.OrdinalIgnoreCase )
			|| Regex.IsMatch( name, @"(?:^|_)LOD\d+(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant )
			|| Regex.IsMatch( name, @"^(?:UCX|UBX|UCP|USP)_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
	}
}
