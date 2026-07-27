using Editor.Tools.SyntyBrowser;

namespace Survive.Tests;

[TestClass]
public sealed class SyntySourceCatalogCollisionTests
{
	[TestMethod]
	public void StandaloneCollisionMeshesAreNotBrowserAssets()
	{
		var root = Path.Combine( Path.GetTempPath(), Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( root );
		File.WriteAllText( Path.Combine( root, "SM_Bld_Wall_01.fbx" ), "" );
		File.WriteAllText( Path.Combine( root, "SM_Bld_Wall_01_Collision.fbx" ), "" );
		try
		{
			var catalog = SyntySourceCatalog.Build( root );
			Assert.AreEqual( 1, catalog.Assets.Length );
			Assert.AreEqual( "SM_Bld_Wall_01", catalog.Assets[0].Name );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}
}
