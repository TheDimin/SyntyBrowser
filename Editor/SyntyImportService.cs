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
		var affectedFiles = GetAffectedSharedFiles( packRootPath, source, packSettings, materialDirectory, textureDirectory )
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
			FbxEmbeddedMediaPathSanitizer.SanitizeFile( destinationFbx );
			var registeredFbx = AssetSystem.RegisterFile( destinationFbx )
				?? throw new InvalidOperationException( $"Could not register imported FBX '{destinationFbx}'." );

			var materialPaths = CreatePackMaterials( packRootPath, source, packSettings, materialDirectory, textureDirectory );
			var generatedModel = ToAssetPath( destinationModel );
			WriteModel(
				destinationModel,
				generatedModel.Replace( ".vmdl", ".fbx", StringComparison.OrdinalIgnoreCase ),
				source,
				FbxSourceMaterialInspection.ReadMaterialReferences( destinationFbx ),
				materialPaths.BySlot );
			var generatedAsset = AssetSystem.RegisterFile( destinationModel )
				?? throw new InvalidOperationException( $"Could not register model '{destinationModel}'." );
			generatedAsset.Compile( false );
			if ( generatedAsset.IsCompileFailed )
				throw new InvalidOperationException( $"Model '{generatedAsset.Path}' failed to compile." );
			AssetSystem.FindByPath( generatedModel )?.GetAssetThumb( true );
			MainAssetBrowser.Instance?.Local.UpdateAssetList();
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
		var byOutput = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var resolvedSlots = SyntyMaterialSlotResolver.Resolve( source, settings.SlotOverrides );
		foreach ( var resolved in resolvedSlots.DistinctBy( slot => slot.OutputName, StringComparer.OrdinalIgnoreCase ) )
		{
			var slot = resolved.Source;
			var mapping = settings.Materials.GetValueOrDefault( slot.Name );
			var shader = resolved.Override?.Shader ?? mapping?.Shader ?? settings.DefaultShader;
			var parameters = SyntyMaterialImportDefaults.ParametersFor( shader );
			if ( mapping?.Parameters is not null )
			{
				foreach ( var parameter in mapping.Parameters )
					parameters[parameter.Key] = parameter.Value;
			}
			if ( resolved.Override?.Parameters is not null )
			{
				foreach ( var parameter in resolved.Override.Parameters )
					parameters[parameter.Key] = parameter.Value;
			}

			string textureAssetPath = null;
			var textureHint = resolved.Override?.TextureHint ?? slot.TextureHint;
			var sourceTexture = FindTexture( packRootPath, textureHint ?? resolved.Source.Name );
			if ( sourceTexture is not null )
			{
					var destination = Path.Combine( textureDirectory, Path.GetFileName( sourceTexture ) );
					CopyFileIfChanged( sourceTexture, destination );
					AssetSystem.RegisterFile( destination );
					textureAssetPath = ToAssetPath( destination );
					textures.Add( textureAssetPath );
			}

			var materialPath = Path.Combine( materialDirectory, $"{SyntySourceCatalog.NormalizeId( resolved.OutputName )}.vmat" );
			var materialDocument = BuildMaterialDocument(
				shader,
				textureAssetPath,
				SyntyMaterialImportDefaults.TextureParametersFor( shader ),
				parameters );
			var materialChanged = !File.Exists( materialPath )
				|| !string.Equals( File.ReadAllText( materialPath ), materialDocument, StringComparison.Ordinal );
			if ( materialChanged )
				File.WriteAllText( materialPath, materialDocument );
			var materialAsset = AssetSystem.RegisterFile( materialPath )
				?? throw new InvalidOperationException( $"Could not register material '{materialPath}'." );
			if ( materialChanged )
				materialAsset.Compile( false );
			if ( materialAsset.IsCompileFailed )
				throw new InvalidOperationException( $"Material '{materialAsset.Path}' failed to compile." );
			materials.Add( materialAsset.Path );
			byOutput[resolved.OutputName] = materialAsset.Path;
		}
		foreach ( var resolved in resolvedSlots )
			bySlot[resolved.BindingKey] = byOutput[resolved.OutputName];
		return (materials.ToArray(), textures.Distinct( StringComparer.OrdinalIgnoreCase ).ToArray(), bySlot);
	}

	private static void WriteModel(
		string destinationModel,
		string fbxAssetPath,
		SyntySourceAsset source,
		IReadOnlyList<string> sourceMaterialReferences,
		IReadOnlyDictionary<string, string> bySlot )
	{
		var resolvedSlots = SyntyMaterialSlotResolver.Resolve( source, null );
		var materialTargets = SyntyModelDocument.AlignMaterialTargets(
			sourceMaterialReferences,
			resolvedSlots.Select( resolved => resolved.Source.Name ).ToArray(),
			resolvedSlots.Select( resolved => bySlot[resolved.BindingKey].ToLowerInvariant() ).ToArray() );
		var materialsToRemap = materialTargets.Length == 0
			? Array.Empty<string>()
			: sourceMaterialReferences;
		var importScale = FbxUnitScaleInspection.ReadImportScale( source.SourceFbxPath )
			* FbxMeshTransformInspection.ReadImportScaleCompensation(
				source.SourceFbxPath,
				source.Meshes.Select( mesh => mesh.Name ) );
		var configured = SyntyModelDocument.Create(
			fbxAssetPath,
			materialsToRemap,
			materialTargets,
			importScale,
			addRenderHullCollision: true,
			fallbackMaterial: materialTargets.FirstOrDefault() ?? bySlot.Values.FirstOrDefault() );
		File.WriteAllText( destinationModel, configured );
	}

	private static string FindTexture( string root, string hint )
	{
		return SyntyTextureLocator.Find( root, hint );
	}

	private static string BuildMaterialDocument(
		string shader,
		string texture,
		IReadOnlyList<string> textureParameters,
		IReadOnlyDictionary<string, string> parameters )
	{
		var lines = new List<string>
		{
			"Layer0",
			"{",
			$"\tshader \"{shader}\""
		};
		if ( !string.IsNullOrWhiteSpace( texture ) )
		{
			foreach ( var parameter in textureParameters )
				lines.Add( $"\t{parameter} \"{texture}\"" );
		}
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
		SyntyPackMaterialSettings settings,
		string materialDirectory,
		string textureDirectory )
	{
		var files = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var resolved in SyntyMaterialSlotResolver.Resolve( source, settings.SlotOverrides )
			.DistinctBy( slot => slot.OutputName, StringComparer.OrdinalIgnoreCase ) )
		{
			files.Add( Path.Combine( materialDirectory, $"{SyntySourceCatalog.NormalizeId( resolved.OutputName )}.vmat" ) );
			var textureHint = resolved.Override?.TextureHint ?? resolved.Source.TextureHint;
			var sourceTexture = FindTexture( packRootPath, textureHint ?? resolved.Source.Name );
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
