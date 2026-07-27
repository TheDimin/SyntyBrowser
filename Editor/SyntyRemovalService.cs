using System;
using System.IO;
using Sandbox;

namespace Editor.Tools.SyntyBrowser;

public sealed record SyntyRemovalReference
{
	public required string TargetPath { get; init; }
	public required string ReferencingAssetPath { get; init; }
}

public sealed record SyntyRemovalPlan
{
	public required SyntySourceAsset Source { get; init; }
	public string[] OutputPaths { get; init; } = [];
	public SyntyRemovalReference[] References { get; init; } = [];
	public bool CanRemoveSafely => References.Length == 0;
}

public sealed record SyntyRemovalResult
{
	public required string AssetId { get; init; }
	public string[] RemovedPaths { get; init; } = [];
	public string[] MissingPaths { get; init; } = [];
}

public static class SyntyRemovalService
{
	public static SyntyRemovalPlan Plan( SyntySourceAsset source )
	{
		ArgumentNullException.ThrowIfNull( source );
		var outputPaths = GetOutputPaths( source )
			.Where( path => AssetSystem.FindByPath( path ) is not null || File.Exists( ToAbsolutePath( path ) ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToArray();
		var outputSet = outputPaths.ToHashSet( StringComparer.OrdinalIgnoreCase );
		var references = AssetSystem.All
			.Where( asset => !outputSet.Contains( asset.Path ) )
			.SelectMany( asset => asset.GetReferences( false )
				.Where( reference => outputSet.Contains( reference.Path ) )
				.Select( reference => new SyntyRemovalReference
				{
					TargetPath = reference.Path,
					ReferencingAssetPath = asset.Path
				} ) )
			.DistinctBy(
				reference => $"{reference.TargetPath}\n{reference.ReferencingAssetPath}",
				StringComparer.OrdinalIgnoreCase )
			.OrderBy( reference => reference.TargetPath, StringComparer.OrdinalIgnoreCase )
			.ThenBy( reference => reference.ReferencingAssetPath, StringComparer.OrdinalIgnoreCase )
			.ToArray();

		return new SyntyRemovalPlan
		{
			Source = source,
			OutputPaths = outputPaths,
			References = references
		};
	}

	public static SyntyRemovalResult Remove( SyntyRemovalPlan plan, bool force = false )
	{
		ArgumentNullException.ThrowIfNull( plan );
		var current = Plan( plan.Source );
		if ( current.References.Length > 0 && !force )
		{
			var warning = string.Join(
				Environment.NewLine,
				current.References.Take( 20 ).Select( reference =>
					$"{reference.ReferencingAssetPath} -> {reference.TargetPath}" ) );
			throw new InvalidOperationException(
				$"Removal is blocked because project assets still reference this import:{Environment.NewLine}{warning}" );
		}

		var removed = new List<string>();
		var missing = new List<string>();
		foreach ( var path in current.OutputPaths )
		{
			var absolutePath = ToAbsolutePath( path );
			if ( !File.Exists( absolutePath ) )
			{
				missing.Add( path );
				continue;
			}
			File.Delete( absolutePath );
			removed.Add( path );
		}

		MainAssetBrowser.Instance?.Local.UpdateAssetList();
		return new SyntyRemovalResult
		{
			AssetId = plan.Source.CacheId,
			RemovedPaths = removed.ToArray(),
			MissingPaths = missing.ToArray()
		};
	}

	private static string[] GetOutputPaths( SyntySourceAsset source )
	{
		var packName = source.PackName ?? SyntySourceCatalog.SanitizeName( source.PackDisplayName ?? "pack" );
		var root = $"{SyntyImportService.DefaultDestinationRoot}/{packName}";
		var paths = new HashSet<string>( StringComparer.OrdinalIgnoreCase )
		{
			$"{root}/Models/{source.Id}.fbx",
			$"{root}/Models/{source.Id}.vmdl",
			$"{root}/Models/{source.Id}.vmdl_c"
		};
		foreach ( var slot in source.Meshes.SelectMany( mesh => mesh.Materials )
			.DistinctBy( material => material.Name, StringComparer.OrdinalIgnoreCase ) )
		{
			paths.Add( $"{root}/Materials/{SyntySourceCatalog.NormalizeId( slot.Name )}.vmat" );
			if ( string.IsNullOrWhiteSpace( slot.TextureHint ) || string.IsNullOrWhiteSpace( source.PackRootPath ) )
				continue;
			var texture = SyntyTextureLocator.Find( source.PackRootPath, slot.TextureHint );
			if ( texture is not null )
				paths.Add( $"{root}/Textures/{Path.GetFileName( texture )}" );
		}
		return paths.ToArray();
	}

	private static string ToAbsolutePath( string assetPath ) =>
		Path.Combine( Project.Current.GetAssetsPath(), assetPath.Replace( '/', Path.DirectorySeparatorChar ) );
}
