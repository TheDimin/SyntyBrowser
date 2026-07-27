using Editor;
using Editor.Tools.SyntyBrowser;

[TestClass]
public sealed class SyntyThumbnailSchedulerTests
{
	[TestMethod]
	public void GridPaintQueuesOnlyVisibleAssetsAndHonorsCapacity()
	{
		var scheduler = new SyntyThumbnailScheduler( 2 );

		Assert.IsFalse( scheduler.TryQueue( "tree", isVisible: false ) );
		Assert.AreEqual( 0, scheduler.PendingCount );
		Assert.IsTrue( scheduler.TryQueue( "tree", isVisible: true ) );
		Assert.IsTrue( scheduler.TryQueue( "rock", isVisible: true ) );
		Assert.IsFalse( scheduler.TryQueue( "crate", isVisible: true ) );
		Assert.IsFalse( scheduler.TryQueue( "tree", isVisible: true ) );
	}

	[TestMethod]
	public void AssetsSharingPackTextureReuseOnePreviewMaterial()
	{
		var tree = SyntyPreviewMaterialCache.Key( "Adventure", "PolyAdventureTexture_01", "adventure_tree" );
		var barrel = SyntyPreviewMaterialCache.Key( "Adventure", "PolyAdventureTexture_01", "adventure_barrel" );

		Assert.AreEqual( tree, barrel );
	}

	[TestMethod]
	public void AuxiliaryModelRulesCoverCollisionAndLodWithoutHidingPrimary()
	{
		Assert.IsTrue( SyntySourceCatalog.IsAuxiliaryModel( "SM_House_01_Collision" ) );
		Assert.IsTrue( SyntySourceCatalog.IsAuxiliaryModel( "SM_House_01_LOD1" ) );
		Assert.IsTrue( SyntySourceCatalog.IsAuxiliaryModel( "UCX_SM_House_01" ) );
		Assert.IsFalse( SyntySourceCatalog.IsAuxiliaryModel( "SM_House_01" ) );
	}
}
