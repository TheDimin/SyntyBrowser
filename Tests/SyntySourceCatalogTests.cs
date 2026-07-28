using Editor;
using Editor.Tools.SyntyBrowser;

[TestClass]
public sealed class SyntySourceCatalogTests
{
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
		Action writeChildren )
	{
		var header = writer.BaseStream.Position;
		writer.Write( 0UL );
		writer.Write( (ulong)properties.Length );
		var propertyLengthPosition = writer.BaseStream.Position;
		writer.Write( 0UL );
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
			else
			{
				var encoded = System.Text.Encoding.UTF8.GetBytes( (string)property.Value );
				writer.Write( (uint)encoded.Length );
				writer.Write( encoded );
			}
		}
		var propertyEnd = writer.BaseStream.Position;
		writeChildren?.Invoke();
		writer.Write( new byte[25] );
		var end = writer.BaseStream.Position;
		writer.BaseStream.Position = header;
		writer.Write( (ulong)end );
		writer.BaseStream.Position = propertyLengthPosition;
		writer.Write( (ulong)(propertyEnd - propertyStart) );
		writer.BaseStream.Position = end;
	}
}
