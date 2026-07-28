using Editor.Tools.SyntyBrowser;

namespace SyntyBrowser.Tests;

[TestClass]
public sealed class SyntySharedPreviewCacheTests
{
	[TestMethod]
	public void SharedPathIncludesRendererVersionPackAndAsset()
	{
		var source = Asset( "Fantasy Kingdom", "SM_Prop_Barrel_01" );

		var path = SyntyPreviewCache.GetPath( @"E:\SyntyPacks\Cache", source );

		Assert.AreEqual(
			Path.GetFullPath( @"E:\SyntyPacks\Cache\previews\v2\fantasy_kingdom\sm_prop_barrel_01.png" ),
			path );
	}

	[TestMethod]
	public void LegacyMigrationCannotPopulateActiveRendererCache()
	{
		Assert.AreNotEqual( SyntyPreviewCache.RendererVersion, SyntyPreviewMigration.LegacyArchiveVersion );
	}

	[TestMethod]
	public void MigrationCopiesValidPngAndPreservesSource()
	{
		var root = Path.Combine( Path.GetTempPath(), $"synty-migration-{Guid.NewGuid():N}" );
		var legacy = Path.Combine( root, "legacy" );
		var cache = Path.Combine( root, "cache" );
		var source = Path.Combine( legacy, "adventure", "barrel.png" );
		Directory.CreateDirectory( Path.GetDirectoryName( source )! );
		File.WriteAllBytes( source, [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3] );
		try
		{
			var result = SyntyPreviewMigration.CopyAndVerify( legacy, cache );

			Assert.AreEqual( 1, result.Copied );
			Assert.IsTrue( File.Exists( source ) );
			Assert.IsTrue( File.Exists( Path.Combine( cache, "previews", "legacy-v1", "adventure", "barrel.png" ) ) );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}

	[TestMethod]
	public void MigrationRejectsInvalidAndKeepsExistingValidDestination()
	{
		var root = Path.Combine( Path.GetTempPath(), $"synty-migration-{Guid.NewGuid():N}" );
		var legacy = Path.Combine( root, "legacy" );
		var cache = Path.Combine( root, "cache" );
		Directory.CreateDirectory( Path.Combine( legacy, "pack" ) );
		File.WriteAllText( Path.Combine( legacy, "pack", "invalid.png" ), "not png" );
		var valid = Path.Combine( legacy, "pack", "valid.png" );
		File.WriteAllBytes( valid, [137, 80, 78, 71, 13, 10, 26, 10, 1] );
		var existing = Path.Combine( cache, "previews", "legacy-v1", "pack", "valid.png" );
		Directory.CreateDirectory( Path.GetDirectoryName( existing )! );
		File.WriteAllBytes( existing, [137, 80, 78, 71, 13, 10, 26, 10, 9] );
		try
		{
			var result = SyntyPreviewMigration.CopyAndVerify( legacy, cache );

			Assert.AreEqual( 1, result.Invalid );
			Assert.AreEqual( 1, result.Existing );
			Assert.AreEqual( 9, File.ReadAllBytes( existing )[^1] );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}

	[TestMethod]
	public void InterruptedRenderingStateBecomesRetryableFailure()
	{
		var root = Path.Combine( Path.GetTempPath(), $"synty-state-{Guid.NewGuid():N}" );
		var source = Asset( "pack", "asset" );
		try
		{
			var store = new SyntyPreviewStateStore( root );
			store.Write( source, new SyntyPreviewJobState
			{
				AssetId = source.CacheId,
				Status = SyntyPreviewJobStatus.Rendering,
				Attempts = 1
			} );

			store.RecoverInterrupted();

			var recovered = store.Read( source );
			Assert.AreEqual( SyntyPreviewJobStatus.Failed, recovered.Status );
			Assert.IsTrue( SyntyPreviewRetryPolicy.CanAutomaticallyRetry( recovered, 2 ) );
		}
		finally
		{
			if ( Directory.Exists( root ) )
				Directory.Delete( root, true );
		}
	}

	[TestMethod]
	public void RetryStopsAtConfiguredAttemptLimit()
	{
		var failed = new SyntyPreviewJobState
		{
			AssetId = "pack_asset",
			Status = SyntyPreviewJobStatus.Failed,
			Attempts = 2
		};

		Assert.IsFalse( SyntyPreviewRetryPolicy.CanAutomaticallyRetry( failed, 2 ) );
	}

	[TestMethod]
	public void VisibilityIncludesExactlyOneAdjacentRow()
	{
		Assert.IsTrue( SyntyPreviewVisibility.IsVisibleOrNear( 200, 300, 400, 600, 100 ) );
		Assert.IsFalse( SyntyPreviewVisibility.IsVisibleOrNear( 199, 299, 400, 600, 100 ) );
		Assert.IsTrue( SyntyPreviewVisibility.IsVisibleOrNear( 700, 800, 400, 600, 100 ) );
		Assert.IsFalse( SyntyPreviewVisibility.IsVisibleOrNear( 701, 801, 400, 600, 100 ) );
	}

	[TestMethod]
	public void AuxiliaryCollisionAndLodAssetsAreIneligibleBeforeQueueing()
	{
		var root = Path.Combine( Path.GetTempPath(), $"synty-eligibility-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( root );
		try
		{
			var validPath = Path.Combine( root, "barrel.fbx" );
			File.WriteAllText( validPath, "" );
			Assert.IsTrue( SyntyPreviewEligibility.CanGenerate( Asset( "pack", "barrel" ) with
			{
				SourceFbxPath = validPath
			} ) );
			Assert.IsFalse( SyntyPreviewEligibility.CanGenerate( Asset( "pack", "barrel_collision" ) with
			{
				Name = "barrel_collision",
				SourceFbxPath = validPath
			} ) );
			Assert.IsFalse( SyntyPreviewEligibility.CanGenerate( Asset( "pack", "barrel_lod2" ) with
			{
				Name = "barrel_lod2",
				SourceFbxPath = validPath
			} ) );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}

	private static SyntySourceAsset Asset( string pack, string id ) => new()
	{
		Id = id,
		Name = id,
		PackName = pack,
		PackDisplayName = pack,
		SourceFbxPath = "source.fbx"
	};
}
