using System;
using System.IO;
using Sandbox;

namespace Editor.Tools.SyntyBrowser;

public sealed record SyntyImportResult
{
	public required string AssetId { get; init; }
	public string ModelPath { get; init; }
	public string[] MaterialPaths { get; init; } = [];
	public string[] TexturePaths { get; init; } = [];
	public string Error { get; init; }
	public bool Success => string.IsNullOrWhiteSpace( Error );
}

public static class SyntyImportService
{
	public const string DefaultDestinationRoot = "ThirdParty/Synty";

	public static SyntyImportResult Import(
		SyntySourceCatalogResult catalog,
		SyntySourceAsset source,
		SyntyPackMaterialSettings packSettings )
	{
		ArgumentNullException.ThrowIfNull( catalog );
		ArgumentNullException.ThrowIfNull( source );
		ArgumentNullException.ThrowIfNull( packSettings );
		if ( !source.CanImport )
			throw new InvalidOperationException( source.Error ?? $"Source FBX '{source.SourceFbxPath}' does not exist." );
		var packName = source.PackName ?? catalog.PackName;
		var packRootPath = source.PackRootPath ?? catalog.RootPath;
		if ( string.IsNullOrWhiteSpace( packSettings.DefaultShader ) )
			throw new InvalidOperationException( $"Pack '{packName}' has no default shader." );

		var assetsRoot = Project.Current.GetAssetsPath();
		var packRoot = Path.Combine( assetsRoot, DefaultDestinationRoot, packName );
		var modelDirectory = Path.Combine( packRoot, "Models" );
		var materialDirectory = Path.Combine( packRoot, "Materials" );
		var textureDirectory = Path.Combine( packRoot, "Textures" );
		var destinationFbx = Path.Combine( modelDirectory, $"{source.Id}.fbx" );
		var destinationModel = Path.Combine( modelDirectory, $"{source.Id}.vmdl" );
		var stageRoot = Path.Combine( Project.Current.GetRootPath(), ".sbox", "synty-import-staging", Guid.NewGuid().ToString( "N" ) );
		var backupRoot = Path.Combine( stageRoot, "previous-output" );
		var affectedFiles = GetAffectedSharedFiles( packRootPath, source, materialDirectory, textureDirectory )
			.Concat( [destinationFbx, destinationModel, $"{destinationModel}_c"] )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToArray();
		Directory.CreateDirectory( stageRoot );

		try
		{
			BackupAffectedOutput( packRoot, affectedFiles, backupRoot );
			var stagedFbx = Path.Combine( stageRoot, $"{source.Id}.fbx" );
			File.Copy( source.SourceFbxPath, stagedFbx, true );
			Directory.CreateDirectory( modelDirectory );
			File.Copy( stagedFbx, destinationFbx, true );
			var fbxAsset = AssetSystem.RegisterFile( destinationFbx )
				?? throw new InvalidOperationException( $"Could not register imported FBX '{destinationFbx}'." );

			var materialPaths = CreatePackMaterials( packRootPath, source, packSettings, materialDirectory, textureDirectory );
			var existingModelAsset = AssetSystem.FindByPath( ToAssetPath( destinationModel ) );
			if ( existingModelAsset is not null )
				existingModelAsset.Delete();
			else if ( File.Exists( destinationModel ) )
				File.Delete( destinationModel );
			var generatedAsset = EditorUtility.CreateModelFromMeshFile( fbxAsset, destinationModel )
				?? throw new InvalidOperationException( $"s&box could not create a VMDL from '{fbxAsset.Path}'." );
			if ( generatedAsset.IsCompileFailed )
				throw new InvalidOperationException( $"Model '{generatedAsset.Path}' failed to compile." );
			var generatedModel = generatedAsset.Path;
			ConfigureModel(
				AssetSystem.FindByPath( generatedModel ),
				source,
				FbxSourceMaterialInspection.ReadMaterialReferences( destinationFbx ),
				materialPaths.BySlot );
			MainAssetBrowser.Instance?.Local.UpdateAssetList();
			MainAssetBrowser.Instance?.Local.FocusOnAsset( AssetSystem.FindByPath( generatedModel ) );
			return new SyntyImportResult
			{
				AssetId = source.Id,
				ModelPath = generatedModel,
				MaterialPaths = materialPaths.Materials,
				TexturePaths = materialPaths.Textures
			};
		}
		catch ( Exception exception )
		{
			try
			{
				RestoreAffectedOutput( packRoot, affectedFiles, backupRoot );
				MainAssetBrowser.Instance?.Local.UpdateAssetList();
			}
			catch ( Exception rollbackException )
			{
				Log.Error( rollbackException, $"Synty import rollback failed for '{source.Id}'." );
				return new SyntyImportResult
				{
					AssetId = source.Id,
					Error = $"{exception.Message} Rollback also failed: {rollbackException.Message}"
				};
			}
			return new SyntyImportResult { AssetId = source.Id, Error = exception.Message };
		}
		finally
		{
			if ( Directory.Exists( stageRoot ) )
				Directory.Delete( stageRoot, true );
		}
	}

	private static (string[] Materials, string[] Textures, Dictionary<string, string> BySlot) CreatePackMaterials(
		string packRootPath,
		SyntySourceAsset source,
		SyntyPackMaterialSettings settings,
		string materialDirectory,
		string textureDirectory )
	{
		Directory.CreateDirectory( materialDirectory );
		Directory.CreateDirectory( textureDirectory );
		var materials = new List<string>();
		var textures = new List<string>();
		var bySlot = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var slot in source.Meshes.SelectMany( mesh => mesh.Materials ).DistinctBy( material => material.Name, StringComparer.OrdinalIgnoreCase ) )
		{
			var mapping = settings.Materials.GetValueOrDefault( slot.Name );
			var shader = mapping?.Shader ?? settings.DefaultShader;
			if ( slot.UsesCustomShader && mapping is null )
				throw new InvalidOperationException( $"Custom material '{slot.Name}' needs a saved shader mapping." );
			var parameters = mapping?.Parameters is null
				? new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
				: new Dictionary<string, string>( mapping.Parameters, StringComparer.OrdinalIgnoreCase );

			string textureAssetPath = null;
			if ( !string.IsNullOrWhiteSpace( slot.TextureHint ) )
			{
				var sourceTexture = FindTexture( packRootPath, slot.TextureHint );
				if ( sourceTexture is not null )
				{
					var destination = Path.Combine( textureDirectory, Path.GetFileName( sourceTexture ) );
					CopyFileIfChanged( sourceTexture, destination );
					AssetSystem.RegisterFile( destination );
					textureAssetPath = ToAssetPath( destination );
					textures.Add( textureAssetPath );
				}
			}

			var materialPath = Path.Combine( materialDirectory, $"{SyntySourceCatalog.NormalizeId( slot.Name )}.vmat" );
			var materialDocument = BuildMaterialDocument( shader, textureAssetPath, parameters );
			var materialChanged = !File.Exists( materialPath )
				|| !string.Equals( File.ReadAllText( materialPath ), materialDocument, StringComparison.Ordinal );
			if ( materialChanged )
				File.WriteAllText( materialPath, materialDocument );
			var materialAsset = AssetSystem.RegisterFile( materialPath )
				?? throw new InvalidOperationException( $"Could not register material '{materialPath}'." );
			if ( materialChanged )
				materialAsset.Compile( true );
			if ( materialAsset.IsCompileFailed )
				throw new InvalidOperationException( $"Material '{materialAsset.Path}' failed to compile." );
			materials.Add( materialAsset.Path );
			bySlot[slot.Name] = materialAsset.Path;
		}
		return (materials.ToArray(), textures.Distinct( StringComparer.OrdinalIgnoreCase ).ToArray(), bySlot);
	}

	private static void ConfigureModel(
		Asset modelAsset,
		SyntySourceAsset source,
		IReadOnlyList<string> sourceMaterialReferences,
		IReadOnlyDictionary<string, string> bySlot )
	{
		if ( modelAsset is null )
			throw new InvalidOperationException( $"Could not find generated model asset for '{source.Name}'." );
		var materialTargets = source.Meshes
			.SelectMany( mesh => mesh.Materials )
			.DistinctBy( material => material.Name, StringComparer.OrdinalIgnoreCase )
			.Select( material => bySlot[material.Name].ToLowerInvariant() )
			.ToArray();
		var materialsToRemap = materialTargets.Length == 0
			? Array.Empty<string>()
			: sourceMaterialReferences;
		var document = File.ReadAllText( modelAsset.AbsolutePath );
		var importScale = FbxUnitScaleInspection.ReadImportScale( source.SourceFbxPath )
			* FbxMeshTransformInspection.ReadImportScaleCompensation(
				source.SourceFbxPath,
				source.Meshes.Select( mesh => mesh.Name ) );
		var configured = SyntyModelDocument.Configure(
			document,
			materialsToRemap,
			materialTargets,
			addRenderHullCollision: true,
			importScale: importScale );
		File.WriteAllText( modelAsset.AbsolutePath, configured );
		AssetSystem.RegisterFile( modelAsset.AbsolutePath );
		modelAsset.Compile( true );
		if ( modelAsset.IsCompileFailed )
			throw new InvalidOperationException( $"Model '{modelAsset.Path}' failed after configuring materials and collision." );
	}

	private static string FindTexture( string root, string hint )
	{
		return SyntyTextureLocator.Find( root, hint );
	}

	private static string BuildMaterialDocument( string shader, string texture, IReadOnlyDictionary<string, string> parameters )
	{
		var lines = new List<string>
		{
			"Layer0",
			"{",
			$"\tshader \"{shader}\""
		};
		if ( !string.IsNullOrWhiteSpace( texture ) )
			lines.Add( $"\tTextureColor \"{texture}\"" );
		foreach ( var parameter in parameters ?? new Dictionary<string, string>() )
			lines.Add( $"\t{parameter.Key} \"{parameter.Value}\"" );
		lines.Add( "}" );
		return string.Join( Environment.NewLine, lines ) + Environment.NewLine;
	}

	private static string ToAssetPath( string absolutePath ) =>
		Path.GetRelativePath( Project.Current.GetAssetsPath(), absolutePath ).Replace( '\\', '/' );

	private static string[] GetAffectedSharedFiles(
		string packRootPath,
		SyntySourceAsset source,
		string materialDirectory,
		string textureDirectory )
	{
		var files = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var slot in source.Meshes.SelectMany( mesh => mesh.Materials ).DistinctBy( material => material.Name, StringComparer.OrdinalIgnoreCase ) )
		{
			files.Add( Path.Combine( materialDirectory, $"{SyntySourceCatalog.NormalizeId( slot.Name )}.vmat" ) );
			if ( string.IsNullOrWhiteSpace( slot.TextureHint ) )
				continue;
			var sourceTexture = FindTexture( packRootPath, slot.TextureHint );
			if ( sourceTexture is not null )
				files.Add( Path.Combine( textureDirectory, Path.GetFileName( sourceTexture ) ) );
		}
		return files.ToArray();
	}

	private static void BackupAffectedOutput( string packRoot, IReadOnlyList<string> affectedFiles, string backupRoot )
	{
		foreach ( var file in affectedFiles.Where( File.Exists ) )
		{
			var relative = Path.GetRelativePath( packRoot, file );
			var backup = Path.Combine( backupRoot, relative );
			Directory.CreateDirectory( Path.GetDirectoryName( backup ) );
			File.Copy( file, backup, true );
		}
	}

	private static void RestoreAffectedOutput( string packRoot, IReadOnlyList<string> affectedFiles, string backupRoot )
	{
		foreach ( var file in affectedFiles )
		{
			if ( File.Exists( file ) )
				File.Delete( file );
			var backup = Path.Combine( backupRoot, Path.GetRelativePath( packRoot, file ) );
			if ( !File.Exists( backup ) )
				continue;
			Directory.CreateDirectory( Path.GetDirectoryName( file ) );
			File.Copy( backup, file, true );
		}
	}

	private static bool CopyFileIfChanged( string source, string destination )
	{
		if ( File.Exists( destination ) )
		{
			var sourceInfo = new FileInfo( source );
			var destinationInfo = new FileInfo( destination );
			if ( sourceInfo.Length == destinationInfo.Length )
			{
				using var sourceStream = File.OpenRead( source );
				using var destinationStream = File.OpenRead( destination );
				var sourceBuffer = new byte[81920];
				var destinationBuffer = new byte[81920];
				while ( true )
				{
					var sourceRead = sourceStream.Read( sourceBuffer );
					var destinationRead = destinationStream.Read( destinationBuffer );
					if ( sourceRead != destinationRead )
						break;
					if ( sourceRead == 0 )
						return false;
					if ( !sourceBuffer.AsSpan( 0, sourceRead ).SequenceEqual( destinationBuffer.AsSpan( 0, destinationRead ) ) )
						break;
				}
			}
		}

		Directory.CreateDirectory( Path.GetDirectoryName( destination ) );
		File.Copy( source, destination, true );
		return true;
	}
}
