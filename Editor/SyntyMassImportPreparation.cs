using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Tools.SyntyBrowser;

public sealed record SyntyPreparedFile( string StagedPath, string AssetPath );

public sealed record SyntyPreparedImport
{
	public required SyntySourceAsset Source { get; init; }
	public SyntyPreparedFile[] Files { get; init; } = [];
	public string ModelPath { get; init; }
	public string Error { get; init; }
	public bool Success => string.IsNullOrWhiteSpace( Error );
}

public sealed record SyntyPreparationBatch
{
	public SyntyPreparedImport[] Imports { get; init; } = [];
	public TimeSpan Duration { get; init; }
	public int PreparedCount => Imports.Count( item => item.Success );
	public double AssetsPerMinute => Duration.TotalSeconds <= 0 ? 0 : PreparedCount * 60.0 / Duration.TotalSeconds;
}

public static class SyntyMassImportPreparation
{
	private static readonly ConcurrentDictionary<string, object> DestinationLocks = new( StringComparer.OrdinalIgnoreCase );

	public static SyntyPreparationBatch PrepareBatch(
		IReadOnlyList<SyntySourceAsset> sources,
		IReadOnlyDictionary<string, SyntyPackMaterialSettings> settings,
		string stagingRoot,
		int workerCount,
		CancellationToken cancellationToken )
	{
		ArgumentNullException.ThrowIfNull( sources );
		ArgumentNullException.ThrowIfNull( settings );
		ArgumentException.ThrowIfNullOrWhiteSpace( stagingRoot );
		Directory.CreateDirectory( stagingRoot );
		var stopwatch = Stopwatch.StartNew();
		var results = new ConcurrentBag<SyntyPreparedImport>();
		Parallel.ForEach(
			sources,
			new ParallelOptions
			{
				CancellationToken = cancellationToken,
				MaxDegreeOfParallelism = Math.Clamp( workerCount, 1, 16 )
			},
			source =>
			{
				if ( !settings.TryGetValue( source.PackName ?? "", out var packSettings ) )
				return;
				results.Add( Prepare( source, packSettings, stagingRoot, cancellationToken ) );
			} );
		stopwatch.Stop();
		return new SyntyPreparationBatch
		{
			Imports = results.OrderBy( item => item.Source.CacheId, StringComparer.OrdinalIgnoreCase ).ToArray(),
			Duration = stopwatch.Elapsed
		};
	}

	public static SyntyPreparedImport Prepare(
		SyntySourceAsset source,
		SyntyPackMaterialSettings settings,
		string stagingRoot,
		CancellationToken cancellationToken )
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			var packName = source.PackName ?? throw new InvalidDataException( $"Asset '{source.Id}' has no pack name." );
			var packRoot = source.PackRootPath ?? throw new InvalidDataException( $"Asset '{source.Id}' has no pack root." );
			var outputRoot = Path.Combine( stagingRoot, packName );
			var files = new List<SyntyPreparedFile>();
			var fbxAssetPath = $"{SyntyImportService.DefaultDestinationRoot}/{packName}/Models/{source.Id}.fbx";
			var modelAssetPath = $"{SyntyImportService.DefaultDestinationRoot}/{packName}/Models/{source.Id}.vmdl";
			var stagedFbx = StagePath( outputRoot, fbxAssetPath );
			AtomicCopy( source.SourceFbxPath, stagedFbx, cancellationToken );
			FbxEmbeddedMediaPathSanitizer.SanitizeFile( stagedFbx );
			files.Add( new( stagedFbx, fbxAssetPath ) );

			var resolvedSlots = SyntyMaterialSlotResolver.Resolve( source, settings.SlotOverrides );
			var materialByOutput = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			foreach ( var resolved in resolvedSlots.DistinctBy( slot => slot.OutputName, StringComparer.OrdinalIgnoreCase ) )
			{
				cancellationToken.ThrowIfCancellationRequested();
				var mapping = settings.Materials.GetValueOrDefault( resolved.Source.Name );
				var shader = resolved.Override?.Shader ?? mapping?.Shader ?? settings.DefaultShader;
				var parameters = SyntyMaterialImportDefaults.ParametersFor( shader );
				foreach ( var parameter in mapping?.Parameters ?? [] )
					parameters[parameter.Key] = parameter.Value;
				foreach ( var parameter in resolved.Override?.Parameters ?? [] )
					parameters[parameter.Key] = parameter.Value;

				string textureAssetPath = null;
				var textureHint = resolved.Override?.TextureHint ?? resolved.Source.TextureHint;
				var sourceTexture = SyntyTextureLocator.Find( packRoot, textureHint ?? resolved.Source.Name );
				if ( sourceTexture is not null )
				{
						textureAssetPath = $"{SyntyImportService.DefaultDestinationRoot}/{packName}/Textures/{Path.GetFileName( sourceTexture )}";
						var stagedTexture = StagePath( outputRoot, textureAssetPath );
						AtomicCopy( sourceTexture, stagedTexture, cancellationToken );
						files.Add( new( stagedTexture, textureAssetPath ) );
				}

				var materialAssetPath = $"{SyntyImportService.DefaultDestinationRoot}/{packName}/Materials/{SyntySourceCatalog.NormalizeId( resolved.OutputName )}.vmat";
				var document = BuildMaterialDocument(
					shader,
					textureAssetPath,
					SyntyMaterialImportDefaults.TextureParametersFor( shader ),
					parameters );
				var stagedMaterial = StagePath( outputRoot, materialAssetPath );
				AtomicWrite( stagedMaterial, document );
				files.Add( new( stagedMaterial, materialAssetPath ) );
				materialByOutput[resolved.OutputName] = materialAssetPath.ToLowerInvariant();
			}

			var sourceMaterials = materialByOutput.Count == 0 ? [] : FbxSourceMaterialInspection.ReadMaterialReferences( stagedFbx ).ToArray();
			var targets = SyntyModelDocument.AlignMaterialTargets(
				sourceMaterials,
				resolvedSlots.Select( slot => slot.Source.Name ).ToArray(),
				resolvedSlots.Select( slot => materialByOutput[slot.OutputName] ).ToArray() );
			var scale = FbxUnitScaleInspection.ReadImportScale( source.SourceFbxPath )
				* FbxMeshTransformInspection.ReadImportScaleCompensation( source.SourceFbxPath, source.Meshes.Select( mesh => mesh.Name ) );
			var fallbackMaterial = targets.FirstOrDefault() ?? materialByOutput.Values.FirstOrDefault();
			var modelDocument = SyntyModelDocument.Create( fbxAssetPath, sourceMaterials, targets, scale, fallbackMaterial: fallbackMaterial );
			var stagedModel = StagePath( outputRoot, modelAssetPath );
			AtomicWrite( stagedModel, modelDocument );
			files.Add( new( stagedModel, modelAssetPath ) );
			return new SyntyPreparedImport
			{
				Source = source,
				Files = files.DistinctBy( file => file.AssetPath, StringComparer.OrdinalIgnoreCase ).ToArray(),
				ModelPath = modelAssetPath
			};
		}
		catch ( OperationCanceledException )
		{
			throw;
		}
		catch ( Exception exception )
		{
			return new SyntyPreparedImport { Source = source, Error = exception.Message };
		}
	}

	public static void Promote( SyntyPreparedImport prepared, string assetsRoot )
	{
		foreach ( var file in prepared.Files.OrderBy( file => file.AssetPath.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ) )
		{
			var destination = Path.Combine( assetsRoot, file.AssetPath.Replace( '/', Path.DirectorySeparatorChar ) );
			Directory.CreateDirectory( Path.GetDirectoryName( destination )! );
			if ( File.Exists( destination ) && FilesMatch( file.StagedPath, destination ) )
				continue;
			var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
			File.Copy( file.StagedPath, temporary, true );
			File.Move( temporary, destination, true );
		}
	}

	private static string StagePath( string root, string assetPath ) =>
		Path.Combine( root, assetPath.Replace( '/', Path.DirectorySeparatorChar ) );

	private static void AtomicCopy( string source, string destination, CancellationToken cancellationToken )
	{
		lock ( DestinationLocks.GetOrAdd( destination, _ => new object() ) )
		{
			if ( File.Exists( destination ) && new FileInfo( source ).Length == new FileInfo( destination ).Length )
				return;
			Directory.CreateDirectory( Path.GetDirectoryName( destination )! );
			var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
			using ( var input = File.OpenRead( source ) )
			using ( var output = File.Create( temporary ) )
				input.CopyTo( output, 1024 * 1024 );
			cancellationToken.ThrowIfCancellationRequested();
			File.Move( temporary, destination, true );
		}
	}

	private static void AtomicWrite( string destination, string contents )
	{
		lock ( DestinationLocks.GetOrAdd( destination, _ => new object() ) )
		{
			if ( File.Exists( destination ) && string.Equals( File.ReadAllText( destination ), contents, StringComparison.Ordinal ) )
				return;
			Directory.CreateDirectory( Path.GetDirectoryName( destination )! );
			var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
			File.WriteAllText( temporary, contents );
			File.Move( temporary, destination, true );
		}
	}

	private static bool FilesMatch( string left, string right )
	{
		var leftInfo = new FileInfo( left );
		var rightInfo = new FileInfo( right );
		if ( !leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length )
			return false;
		using var leftStream = leftInfo.OpenRead();
		using var rightStream = rightInfo.OpenRead();
		var leftBuffer = new byte[1024 * 1024];
		var rightBuffer = new byte[leftBuffer.Length];
		while ( true )
		{
			var leftRead = leftStream.Read( leftBuffer );
			var rightRead = rightStream.Read( rightBuffer );
			if ( leftRead != rightRead )
				return false;
			if ( leftRead == 0 )
				return true;
			if ( !leftBuffer.AsSpan( 0, leftRead ).SequenceEqual( rightBuffer.AsSpan( 0, rightRead ) ) )
				return false;
		}
	}

	private static string BuildMaterialDocument(
		string shader,
		string texture,
		IReadOnlyList<string> textureParameters,
		IReadOnlyDictionary<string, string> parameters )
	{
		var lines = new List<string> { "Layer0", "{", $"\tshader \"{shader}\"" };
		if ( !string.IsNullOrWhiteSpace( texture ) )
			foreach ( var parameter in textureParameters )
				lines.Add( $"\t{parameter} \"{texture}\"" );
		foreach ( var parameter in parameters )
			lines.Add( $"\t{parameter.Key} \"{parameter.Value}\"" );
		lines.Add( "}" );
		return string.Join( Environment.NewLine, lines ) + Environment.NewLine;
	}
}
