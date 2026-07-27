using Editor.Tools.SyntyBrowser;

namespace Survive.Tests;

[TestClass]
public sealed class SyntyPreviewTextureResolverTests
{
	[TestMethod]
	public void FallbackAssetsUseFbxMaterialNamesAsTextureCandidates()
	{
		var source = new SyntySourceAsset
		{
			Id = "dock",
			Name = "SM_Env_Dock_01",
			SourceFbxPath = "dock.fbx",
			IsFallback = true
		};

		CollectionAssert.AreEqual(
			new[] { "POLYGON_Pirates_Texture_01" },
			SyntyPreviewTextureResolver.CandidateHints( source, ["POLYGON_Pirates_Texture_01"] ) );
	}

	[TestMethod]
	public void AuthoritativeTextureHintTakesPriorityOverFbxMaterialName()
	{
		var source = new SyntySourceAsset
		{
			Id = "barrel",
			Name = "SM_Prop_Barrel_01",
			SourceFbxPath = "barrel.fbx",
			Meshes =
			[
				new SyntyMeshEntry
				{
					Name = "Barrel",
					Materials = [new SyntyMaterialSlot { Name = "BarrelMaterial", TextureHint = "Pirates_Texture_01" }]
				}
			]
		};

		Assert.AreEqual(
			"Pirates_Texture_01",
			SyntyPreviewTextureResolver.CandidateHints( source, ["lambert1"] )[0] );
	}

	[TestMethod]
	public void MaterialListBindingsRemainPerMeshSlotAndAuthoritative()
	{
		var source = new SyntySourceAsset
		{
			Id = "tower",
			Name = "SM_Bld_Tower_01",
			SourceFbxPath = "tower.fbx",
			Meshes =
			[
				new SyntyMeshEntry
				{
					Name = "Tower",
					Materials =
					[
						new SyntyMaterialSlot { Name = "Stone", TextureHint = "Stone_Texture" },
						new SyntyMaterialSlot { Name = "Wood", TextureHint = "Wood_Texture" }
					]
				}
			]
		};

		var bindings = SyntyPreviewTextureResolver.Bindings( source, ["lambert1"] );

		Assert.HasCount( 2, bindings );
		Assert.IsTrue( bindings.All( binding => binding.IsAuthoritative ) );
		Assert.AreEqual( "Stone", bindings[0].SlotName );
		Assert.AreEqual( 0, bindings[0].SlotOrdinal );
		Assert.AreEqual( "Stone_Texture", bindings[0].TextureHint );
		Assert.AreEqual( "Wood", bindings[1].SlotName );
		Assert.AreEqual( 1, bindings[1].SlotOrdinal );
		Assert.AreEqual( "Wood_Texture", bindings[1].TextureHint );
	}
}
