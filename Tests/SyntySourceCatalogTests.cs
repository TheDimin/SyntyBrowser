using Editor;
using Editor.Tools.SyntyBrowser;

[TestClass]
public sealed class SyntySourceCatalogTests
{
	[TestMethod]
	public void MaterialList_PrefersUniqueCanonicalFbxPathOverCollisionHelper()
	{
		var render = Path.Combine( "Pack", "FBX", "SM_Bld_House_01.fbx" );
		var collision = Path.Combine( "Pack", "Collision", "SM_Bld_House_01.fbx" );
		Assert.AreEqual( render, SyntySourceCatalog.ResolveFbxCandidate( [collision, render] ) );
		Assert.IsNull( SyntySourceCatalog.ResolveFbxCandidate( [
			Path.Combine( "Pack", "FBX", "Props", "Crate.fbx" ),
			Path.Combine( "Pack", "FBX", "Environment", "Crate.fbx" )
		] ) );
	}

	[TestMethod]
	public void TagOverrides_CanAddAndRemoveCuratedTagsWithoutChangingDefaults()
	{
		var dock = Asset( "SM_Bld_Dock_01", "Buildings" ) with { PackName = "village", Tags = [SyntyAssetTags.HarborCity] };
		var crate = Asset( "SM_Prop_Generic_01", "Props" ) with { PackName = "village" };
		var removed = SyntyAssetTagOverrides.Apply( dock, new Dictionary<string, SyntyAssetTagOverride>
		{
			[dock.CacheId] = SyntyAssetTagOverrides.Set( dock, SyntyAssetTags.HarborCity, false )
		} );
		var added = SyntyAssetTagOverrides.Apply( crate, new Dictionary<string, SyntyAssetTagOverride>
		{
			[crate.CacheId] = SyntyAssetTagOverrides.Set( crate, SyntyAssetTags.HarborCity, true )
		} );
		Assert.HasCount( 0, removed.Tags );
		CollectionAssert.AreEqual( new[] { SyntyAssetTags.HarborCity }, added.Tags );
		CollectionAssert.AreEqual( new[] { SyntyAssetTags.HarborCity }, dock.Tags );
	}

	[TestMethod]
	public void MultiSelection_SupportsToggleAndOrderedRange()
	{
		var assets = new[] { Asset( "A", "Props" ), Asset( "B", "Props" ), Asset( "C", "Props" ) };
		var selection = new SyntyAssetSelection();
		selection.Select( assets, 0, false, false );
		selection.Select( assets, 2, false, true );
		Assert.HasCount( 3, selection.Selected );
		selection.Select( assets, 1, true, false );
		Assert.IsFalse( selection.Selected.Contains( assets[1].CacheId ) );
	}

	[TestMethod]
	public void SyntyMaterialImportDefaults_AppliesConservativeWorldDefaults()
	{
		var parameters = SyntyMaterialImportDefaults.ParametersFor( "shaders/synty/synty_world.shader_c" );

		Assert.AreEqual( "1", parameters["F_SYNTY_WORLD_VARIATION_PATTERN"] );
		Assert.AreEqual( "0.025", parameters["SyntyWorldColorVariation"] );
		Assert.AreEqual( "0.035", parameters["SyntyWorldMicroColorVariation"] );
		Assert.IsFalse( parameters.ContainsKey( "F_SYNTY_WORLD_MATERIAL_CONTROL" ) );
		Assert.IsFalse( parameters.ContainsKey( "F_SYNTY_WORLD_HERO_CONTROL" ) );
		Assert.IsFalse( parameters.ContainsKey( "SyntyWorldTint" ) );
	}

	[TestMethod]
	public void SyntyMaterialImportDefaults_UsesFoliageAtlasInputsWithoutChoosingMaterialType()
	{
		CollectionAssert.AreEqual(
			new[] { "LeafTexture", "TrunkTexture" },
			SyntyMaterialImportDefaults.TextureParametersFor( "shaders/synty/synty_foliage.shader_c" ) );

		var parameters = SyntyMaterialImportDefaults.ParametersFor( "shaders/synty/synty_foliage.shader_c" );
		Assert.AreEqual( "1", parameters["SyntyFoliageRooted"] );
		Assert.IsFalse( parameters.ContainsKey( "F_SYNTY_FOLIAGE_GRASS" ) );
		Assert.IsFalse( parameters.ContainsKey( "F_SYNTY_FOLIAGE_LEAF_ONLY" ) );
	}

	[TestMethod]
	public void TextureLocator_InfersCustomShaderTextureNames()
	{
		var root = Path.Combine( Path.GetTempPath(), $"synty-textures-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( root );
		try
		{
			var atlas = Path.Combine( root, "PolygonDarkFortress_Texture_01_A.png" );
			var brick = Path.Combine( root, "Brick_Large_Texture_01.png" );
			File.WriteAllBytes( atlas, [] );
			File.WriteAllBytes( brick, [] );

			Assert.AreEqual( atlas, SyntyTextureLocator.Find( root, "PolygonDarkFortress_Mat_01_A" ) );
			Assert.AreEqual( brick, SyntyTextureLocator.Find( root, "Brick_Large_01" ) );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}
	[TestMethod]
	public void MaterialList_ProducesOneAssetPerPrefabAndGroupsLods()
	{
		var fbx = Path.Combine( Path.GetTempPath(), "SM_Env_Tree_01.fbx" );
		var result = SyntySourceCatalog.ParseMaterialList(
			[
				"Folder Name: Prefabs",
				"Prefab Name: SM_Env_Tree_01",
				"    Mesh Name: SM_Env_Tree_01_LOD0",
				"        Slot: Tree_Mat (Uses custom shader)",
				"    Mesh Name: SM_Env_Tree_01_LOD1",
				"        Slot: Tree_Mat (Uses custom shader)",
				"    Mesh Name: SM_Env_Tree_01_MOSS_LOD0",
				"        Slot: Moss_Mat (Moss_Texture)"
			],
			new Dictionary<string, string[]>( StringComparer.OrdinalIgnoreCase )
			{
				["SM_Env_Tree_01"] = [fbx]
			},
			new List<string>() );

		Assert.HasCount( 1, result );
		Assert.AreEqual( fbx, result[0].SourceFbxPath );
		Assert.AreEqual( "Prefabs", result[0].Category );
		Assert.HasCount( 3, result[0].Meshes );
		Assert.AreEqual( 1, result[0].Meshes[1].LodLevel );
		Assert.AreEqual( "SM_Env_Tree_01", result[0].Meshes[1].LodFamily );
		Assert.IsTrue( result[0].Meshes[0].Materials[0].UsesCustomShader );
		Assert.AreEqual( "Moss_Texture", result[0].Meshes[2].Materials[0].TextureHint );
	}

	[TestMethod]
	public void MaterialList_MissingOrAmbiguousFbxIsNotImportable()
	{
		var warnings = new List<string>();
		var result = SyntySourceCatalog.ParseMaterialList(
			[
				"Prefab Name: Missing",
				"Mesh Name: Missing"
			],
			new Dictionary<string, string[]>( StringComparer.OrdinalIgnoreCase ),
			warnings );

		Assert.IsFalse( result[0].CanImport );
		StringAssert.Contains( result[0].Error, "No FBX" );
		Assert.HasCount( 1, warnings );
	}

	[TestMethod]
	public void MaterialList_AmbiguousFbxReportsErrorWithoutThrowing()
	{
		var warnings = new List<string>();
		var result = SyntySourceCatalog.ParseMaterialList(
			["Prefab Name: Crate", "Mesh Name: Crate"],
			new Dictionary<string, string[]>( StringComparer.OrdinalIgnoreCase )
			{
				["Crate"] = ["first/Crate.fbx", "second/Crate.fbx"]
			},
			warnings );

		Assert.HasCount( 1, result );
		Assert.IsFalse( result[0].CanImport );
		StringAssert.Contains( result[0].Error, "Multiple FBXs" );
		Assert.HasCount( 1, warnings );
	}

	[TestMethod]
	public void AssetId_IsStableAndFilesystemSafe()
	{
		Assert.AreEqual( "sm_prop_ritualpyre_01_1", SyntySourceCatalog.NormalizeId( "SM_Prop_RitualPyre_01 (1)" ) );
	}

	[TestMethod]
	public void FallbackAnimationsAndSkinnedModelsAreExcludedFromStaticCatalog()
	{
		var root = Path.Combine( Path.GetTempPath(), $"synty-catalog-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( root );
		try
		{
			File.WriteAllBytes( Path.Combine( root, "A_Idle_01.fbx" ), [] );
			File.WriteAllBytes( Path.Combine( root, "SK_Character_01.fbx" ), [] );
			File.WriteAllBytes( Path.Combine( root, "SM_Prop_Crate_01.fbx" ), [] );

			var catalog = SyntySourceCatalog.Build( root );

			Assert.IsFalse( catalog.Assets.Any( asset => asset.Name == "A_Idle_01" ) );
			Assert.IsFalse( catalog.Assets.Any( asset => asset.Name == "SK_Character_01" ) );
			Assert.IsTrue( catalog.Assets.Single( asset => asset.Name == "SM_Prop_Crate_01" ).CanImport );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}

	[TestMethod]
	public void LibraryRoot_PreservesDuplicateAssetIdsAcrossPacks()
	{
		var root = Path.Combine( Path.GetTempPath(), $"synty-library-{Guid.NewGuid():N}" );
		var first = Directory.CreateDirectory( Path.Combine( root, "Fantasy Village" ) ).FullName;
		var second = Directory.CreateDirectory( Path.Combine( root, "Swamp" ) ).FullName;
		try
		{
			File.WriteAllBytes( Path.Combine( first, "SM_Prop_Crate_01.fbx" ), [] );
			File.WriteAllBytes( Path.Combine( second, "SM_Prop_Crate_01.fbx" ), [] );

			var catalog = SyntySourceCatalog.Build( root );

			Assert.AreEqual( 2, catalog.PackCount );
			Assert.HasCount( 2, catalog.Assets );
			Assert.AreNotEqual( catalog.Assets[0].CacheId, catalog.Assets[1].CacheId );
			Assert.IsTrue( catalog.Assets.All( asset => asset.DisplayName == "Crate 01" ) );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}

	[TestMethod]
	public void DisplayName_RemovesStructuralPrefixesAndSplitsCamelCase()
	{
		Assert.AreEqual( "Boat Mast Collision", SyntyAssetNaming.ToDisplayName( "SM_Env_Boat_Mast_Collision" ) );
		Assert.AreEqual( "Capes Mini 01", SyntyAssetNaming.ToDisplayName( "CapesMini_01" ) );
		Assert.AreEqual( "Animal Characters", SyntyAssetNaming.ToDisplayName( "AnimalCharacters" ) );
		Assert.AreEqual( "Fence 01", SyntyAssetNaming.ToDisplayName( "SM_Bld_Fence_01" ) );
	}

	[TestMethod]
	public void Search_MatchesMultipleTermsAndTypos()
	{
		var expected = new SyntySourceAsset
		{
			Id = "character_female",
			Name = "SM_Character_Female",
			DisplayName = "Character Female",
			PackDisplayName = "Fantasy Village",
			SourceFbxPath = "missing.fbx"
		};
		var distractor = expected with { Id = "boat", Name = "SM_Boat", DisplayName = "Boat" };

		var results = SyntyAssetSearch.Search( [distractor, expected], "charcter femle" );

		Assert.HasCount( 1, results );
		Assert.AreSame( expected, results[0] );

		var barrel = expected with { Id = "barrel", Name = "SM_Prop_Barrel_01", DisplayName = "Barrel 01" };
		var barber = expected with { Id = "barber", Name = "SM_Prop_Barber_01", DisplayName = "Barber 01" };
		var transposed = SyntyAssetSearch.Search( [barber, barrel], "barerl" );
		Assert.AreSame( barrel, transposed[0] );
	}

	[TestMethod]
	public void CuratedTags_AssignHarborCityConservatively()
	{
		var dock = Asset( "SM_Bld_Dock_Wood_01", "Buildings" );
		var market = Asset( "SM_Prop_Market_Stall_01", "Props" );
		var unrelated = Asset( "SM_Prop_Bed_01", "Props" );
		var incompatible = Asset( "SM_Prop_Space_Crate_01", "SciFi Props" );

		CollectionAssert.AreEqual( new[] { SyntyAssetTags.HarborCity }, SyntyAssetTags.Resolve( dock ) );
		CollectionAssert.AreEqual( new[] { SyntyAssetTags.HarborCity }, SyntyAssetTags.Resolve( market ) );
		Assert.HasCount( 0, SyntyAssetTags.Resolve( unrelated ) );
		Assert.HasCount( 0, SyntyAssetTags.Resolve( incompatible ) );
	}

	[TestMethod]
	public void Search_TagFilterComposesWithFuzzyText()
	{
		var dock = Asset( "SM_Bld_Dock_Wood_01", "Buildings" ) with { Tags = [SyntyAssetTags.HarborCity] };
		var market = Asset( "SM_Prop_Market_Stall_01", "Props" ) with { Tags = [SyntyAssetTags.HarborCity] };
		var forestCrate = Asset( "SM_Prop_Crate_01", "Forest Props" );

		CollectionAssert.AreEqual(
			new[] { market, dock },
			SyntyAssetSearch.Search( [forestCrate, market, dock], "tag:harbor-city" ) );
		CollectionAssert.AreEqual(
			new[] { dock },
			SyntyAssetSearch.Search( [forestCrate, market, dock], "dock tag:harbor-city" ) );
		Assert.HasCount( 0, SyntyAssetSearch.Search( [forestCrate], "tag:harbor-city" ) );
	}

	private static SyntySourceAsset Asset( string name, string category ) => new()
	{
		Id = SyntySourceCatalog.NormalizeId( name ),
		Name = name,
		DisplayName = SyntyAssetNaming.ToDisplayName( name ),
		Category = category,
		SourceFbxPath = "missing.fbx"
	};

	[TestMethod]
	public void ModelDocument_CreateWritesFinalImportInOnePass()
	{
		var document = SyntyModelDocument.Create(
			"ThirdParty/Synty/Pack/Models/prop.fbx",
			["material_0", "material_1"],
			["thirdparty/synty/pack/materials/red.vmat", "thirdparty/synty/pack/materials/blue.vmat"],
			0.01f );

		StringAssert.Contains( document, "filename = \"ThirdParty/Synty/Pack/Models/prop.fbx\"" );
		StringAssert.Contains( document, "import_scale = 0.01" );
		StringAssert.Contains( document, "from = \"material_0\"" );
		StringAssert.Contains( document, "to = \"thirdparty/synty/pack/materials/blue.vmat\"" );
		StringAssert.Contains( document, "_class = \"PhysicsHullFromRender\"" );
		Assert.AreEqual( 1, document.Split( "_class = \"RenderMeshFile\"" ).Length - 1 );
	}

	[TestMethod]
	public void ModelDocument_AlignsFbxMaterialsByNameAndFallsBackByOrder()
	{
		CollectionAssert.AreEqual(
			new[] { "blue.vmat", "blue.vmat", "blue.vmat" },
			SyntyModelDocument.AlignMaterialTargets(
				["Material_Blue.vmat", "unknown.vmat", "another.vmat"],
				["Material_Red", "Material_Blue"],
				["red.vmat", "blue.vmat"] ) );
	}

	[TestMethod]
	public void MassImportManifest_RoundTripsResumeStateAndRecoversFromCorruption()
	{
		var directory = Path.Combine( Path.GetTempPath(), $"synty-manifest-{Guid.NewGuid():N}" );
		var path = Path.Combine( directory, "manifest.json" );
		try
		{
			var manifest = new SyntyMassImportManifest();
			manifest.Prepared.Add( "pack_asset-a" );
			manifest.Finalized.Add( "pack_asset-b" );
			manifest.Failures["pack_asset-c"] = "compile failed";
			manifest.Save( path );

			var resumed = SyntyMassImportManifest.Load( path );
			Assert.IsTrue( resumed.Prepared.Contains( "PACK_ASSET-A" ) );
			Assert.IsTrue( resumed.Finalized.Contains( "pack_asset-b" ) );
			Assert.AreEqual( "compile failed", resumed.Failures["pack_asset-c"] );

			File.WriteAllText( path, "{corrupt" );
			var recovered = SyntyMassImportManifest.Load( path );
			Assert.HasCount( 0, recovered.Prepared );
			Assert.HasCount( 0, recovered.Finalized );
		}
		finally
		{
			if ( Directory.Exists( directory ) )
				Directory.Delete( directory, true );
		}
	}

	[TestMethod]
	public void ModelDocument_MapsFbxSlotToAuthoritativeMaterialAndAddsCollision()
	{
		const string generated = """
			{
				rootNode =
				{
					children =
					[
						{
							_class = "MaterialGroupList"
							children =
							[
								{
									_class = "DefaultMaterialGroup"
									remaps = [ ]
									use_global_default = false
								},
							]
						},
						{
							_class = "RenderMeshList"
							children =
							[
								{
									_class = "RenderMeshFile"
									import_scale = 1.0
								},
							]
						},
					]
				}
			}
			""";

		var configured = SyntyModelDocument.Configure(
			generated,
			["blinn265.vmat"],
			["thirdparty/synty/adventure/materials/polyadventurematerial_01.vmat"],
			addRenderHullCollision: true,
			importScale: 1.0f / 2.54f );

		StringAssert.Contains( configured, "from = \"blinn265.vmat\"" );
		StringAssert.Contains( configured, "to = \"thirdparty/synty/adventure/materials/polyadventurematerial_01.vmat\"" );
		StringAssert.Contains( configured, "_class = \"PhysicsHullFromRender\"" );
		StringAssert.Contains( configured, "import_scale = 0.3937008" );
	}

	[TestMethod]
	public void FbxMaterialInspection_ReadsBinarySourceMaterialName()
	{
		var path = Path.Combine( Path.GetTempPath(), $"{Guid.NewGuid():N}.fbx" );
		try
		{
			File.WriteAllBytes( path, System.Text.Encoding.Latin1.GetBytes( "prefix\u0008blinn265\0\u0001Material suffix" ) );

			CollectionAssert.AreEqual(
				new[] { "blinn265.vmat" },
				FbxSourceMaterialInspection.ReadMaterialReferences( path ) );
		}
		finally
		{
			File.Delete( path );
		}
	}

	[TestMethod]
	[DataRow( 1.0, 0.3937008 )]
	[DataRow( 100.0, 39.37008 )]
	public void FbxUnitScaleInspection_ConvertsSourceUnitsToSceneInches( double unitScaleFactor, double expected )
	{
		var path = Path.Combine( Path.GetTempPath(), $"{Guid.NewGuid():N}.fbx" );
		try
		{
			using ( var stream = File.Create( path ) )
			using ( var writer = new BinaryWriter( stream ) )
			{
				writer.Write( System.Text.Encoding.ASCII.GetBytes( "Kaydara FBX Binary  " ) );
				var name = System.Text.Encoding.ASCII.GetBytes( "UnitScaleFactor" );
				writer.Write( (byte)'S' );
				writer.Write( name.Length );
				writer.Write( name );
				foreach ( var value in new[] { "double", "Number", "" } )
				{
					var encoded = System.Text.Encoding.ASCII.GetBytes( value );
					writer.Write( (byte)'S' );
					writer.Write( encoded.Length );
					writer.Write( encoded );
				}
				writer.Write( (byte)'D' );
				writer.Write( unitScaleFactor );
			}

			Assert.AreEqual( expected, FbxUnitScaleInspection.ReadImportScale( path ), 0.00001 );
		}
		finally
		{
			File.Delete( path );
		}
	}

	[TestMethod]
	[DataRow( 0.01, 100.0 )]
	[DataRow( 1.0, 1.0 )]
	public void FbxMeshTransformInspection_CompensatesOnlyPirateStyleAuthoritativeMeshScale(
		double embeddedScale,
		double expectedCompensation )
	{
		var path = Path.Combine( Path.GetTempPath(), $"{Guid.NewGuid():N}.fbx" );
		try
		{
			WriteMeshTransformFbx( path, embeddedScale );

			Assert.AreEqual(
				expectedCompensation,
				FbxMeshTransformInspection.ReadImportScaleCompensation( path, ["SM_Bld_Shop_01"] ),
				0.00001 );
		}
		finally
		{
			File.Delete( path );
		}
	}

	[TestMethod]
	public void FbxMeshTransformInspection_DoesNotCompensateLegacyCentimeterAuthoredMesh()
	{
		var path = Path.Combine( Path.GetTempPath(), $"{Guid.NewGuid():N}.fbx" );
		try
		{
			WriteLegacyCentimeterMeshFbx( path );

			Assert.AreEqual(
				1.0,
				FbxMeshTransformInspection.ReadImportScaleCompensation( path, ["SM_Bld_Stall_03"] ),
				0.00001 );
		}
		finally
		{
			File.Delete( path );
		}
	}

	[TestMethod]
	public void FbxMeshTransformInspection_CompensatesLegacyMeterAuthoredVesselPiece()
	{
		var path = Path.Combine( Path.GetTempPath(), $"{Guid.NewGuid():N}.fbx" );
		try
		{
			WriteLegacyVesselMeshFbx( path );

			Assert.AreEqual(
				100.0,
				FbxMeshTransformInspection.ReadImportScaleCompensation(
					path,
					["SM_Veh_Boat_Warship_01_Hull_Pirate"] ),
				0.00001 );
			Assert.AreEqual(
				100.0,
				FbxMeshTransformInspection.ReadImportScaleCompensation( path, [] ),
				0.00001,
				"Standalone vessel pieces without MaterialList mesh selections must still be inspected." );
		}
		finally
		{
			File.Delete( path );
		}
	}

	private static void WriteLegacyVesselMeshFbx( string path )
	{
		WriteLegacyMeshFbx(
			path,
			"SM_Veh_Boat_Warship_01_Hull_Pirate",
			new double[] { 0, 0, 0, 10.324, 0, 0, 0, 39.7489, 0, 0, 0, 17.1532 } );
	}

	private static void WriteLegacyCentimeterMeshFbx( string path )
	{
		WriteLegacyMeshFbx(
			path,
			"SM_Bld_Stall_03",
			new double[] { 0, 0, 0, 354, 0, 0, 0, 300, 0, 0, 0, 298 } );
	}

	private static void WriteLegacyMeshFbx( string path, string meshName, double[] vertices )
	{
		using var stream = File.Create( path );
		using var writer = new BinaryWriter( stream );
		writer.Write( System.Text.Encoding.ASCII.GetBytes( "Kaydara FBX Binary  \0\u001a\0" ) );
		writer.Write( 7400 );
		WriteNode( writer, "Creator", [('S', "FBX SDK/FBX Plugins version 2017.0")], null, false );
		WriteNode(
			writer,
			"Geometry",
			[('L', 1L), ('S', $"Geometry::{meshName}"), ('S', "Mesh")],
			() => WriteNode(
				writer,
				"Vertices",
				[('d', vertices)],
				null,
				false ),
			false );
		WriteNode(
			writer,
			"Model",
			[('L', 2L), ('S', $"Model::{meshName}"), ('S', "Mesh")],
			() => WriteNode(
				writer,
				"Properties70",
				[],
				() => WriteNode(
					writer,
					"P",
					[('S', "Lcl Scaling"), ('S', "Lcl Scaling"), ('S', ""), ('S', "A"), ('D', 1.0), ('D', 1.0), ('D', 1.0)],
					null,
					false ),
				false ),
			false );
		WriteNode(
			writer,
			"Connections",
			[],
			() => WriteNode( writer, "C", [('S', "OO"), ('L', 1L), ('L', 2L)], null, false ),
			false );
		writer.Write( new byte[13] );
	}

	private static void WriteMeshTransformFbx( string path, double embeddedScale )
	{
		using var stream = File.Create( path );
		using var writer = new BinaryWriter( stream );
		writer.Write( System.Text.Encoding.ASCII.GetBytes( "Kaydara FBX Binary  \0\u001a\0" ) );
		writer.Write( 7500 );
		WriteNode(
			writer,
			"Model",
			[
				('L', (object)2L),
				('S', "Model::SM_Bld_Shop_01"),
				('S', "Mesh")
			],
			() => WriteNode(
				writer,
				"Properties70",
				[],
				() => WriteNode(
					writer,
					"P",
					[
						('S', "Lcl Scaling"),
						('S', "Lcl Scaling"),
						('S', ""),
						('S', "A"),
						('D', embeddedScale),
						('D', embeddedScale),
						('D', embeddedScale)
					],
					null ) ) );
		writer.Write( new byte[25] );
	}

	private static void WriteNode(
		BinaryWriter writer,
		string name,
		(char Type, object Value)[] properties,
		Action writeChildren,
		bool isWide = true )
	{
		var header = writer.BaseStream.Position;
		if ( isWide )
		{
			writer.Write( 0UL );
			writer.Write( (ulong)properties.Length );
		}
		else
		{
			writer.Write( 0U );
			writer.Write( (uint)properties.Length );
		}
		var propertyLengthPosition = writer.BaseStream.Position;
		if ( isWide )
			writer.Write( 0UL );
		else
			writer.Write( 0U );
		var encodedName = System.Text.Encoding.UTF8.GetBytes( name );
		writer.Write( (byte)encodedName.Length );
		writer.Write( encodedName );
		var propertyStart = writer.BaseStream.Position;
		foreach ( var property in properties )
		{
			writer.Write( (byte)property.Type );
			if ( property.Type == 'L' )
			{
				writer.Write( (long)property.Value );
			}
			else if ( property.Type == 'D' )
			{
				writer.Write( (double)property.Value );
			}
			else if ( property.Type == 'd' )
			{
				var values = (double[])property.Value;
				writer.Write( (uint)values.Length );
				writer.Write( 0U );
				writer.Write( checked((uint)(values.Length * sizeof(double))) );
				foreach ( var value in values )
					writer.Write( value );
			}
			else
			{
				var encoded = System.Text.Encoding.UTF8.GetBytes( (string)property.Value );
				writer.Write( (uint)encoded.Length );
				writer.Write( encoded );
			}
		}
		var propertyEnd = writer.BaseStream.Position;
		writeChildren?.Invoke();
		writer.Write( new byte[isWide ? 25 : 13] );
		var end = writer.BaseStream.Position;
		writer.BaseStream.Position = header;
		if ( isWide )
			writer.Write( (ulong)end );
		else
			writer.Write( checked((uint)end) );
		writer.BaseStream.Position = propertyLengthPosition;
		if ( isWide )
			writer.Write( (ulong)(propertyEnd - propertyStart) );
		else
			writer.Write( checked((uint)(propertyEnd - propertyStart)) );
		writer.BaseStream.Position = end;
	}
}
