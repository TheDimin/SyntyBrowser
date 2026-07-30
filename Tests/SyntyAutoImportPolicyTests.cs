using Editor.Tools.SyntyBrowser;

namespace SyntyBrowser.Tests;

[TestClass]
public sealed class SyntyAutoImportPolicyTests
{
	[TestMethod]
	public void IncludesOneRowAroundViewport()
	{
		Assert.IsTrue( SyntyAutoImportPolicy.IsVisibleOrNear( 200, 300, 400, 600, 100 ) );
		Assert.IsFalse( SyntyAutoImportPolicy.IsVisibleOrNear( 199, 299, 400, 600, 100 ) );
		Assert.IsTrue( SyntyAutoImportPolicy.IsVisibleOrNear( 700, 800, 400, 600, 100 ) );
		Assert.IsFalse( SyntyAutoImportPolicy.IsVisibleOrNear( 701, 801, 400, 600, 100 ) );
	}

	[TestMethod]
	public void ConfiguredPackCanImportVisibleAsset()
	{
		Assert.IsTrue( SyntyAutoImportPolicy.CanImport(
			Asset( usesCustomShader: false ),
			"shaders/synty_world.shader_c",
			new HashSet<string>( StringComparer.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void MissingDefaultShaderIsIgnored()
	{
		Assert.IsFalse( SyntyAutoImportPolicy.CanImport(
			Asset( usesCustomShader: false ),
			null,
			new HashSet<string>( StringComparer.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void UnmappedCustomShaderUsesConfiguredDefault()
	{
		Assert.IsTrue( SyntyAutoImportPolicy.CanImport(
			Asset( usesCustomShader: true ),
			"shaders/synty_world.shader_c",
			new HashSet<string>( StringComparer.OrdinalIgnoreCase ) ) );
	}

	private static SyntySourceAsset Asset( bool usesCustomShader ) => new()
	{
		Id = "barrel",
		Name = "Barrel",
		SourceFbxPath = typeof( SyntyAutoImportPolicyTests ).Assembly.Location,
		Meshes =
		[
			new SyntyMeshEntry
			{
				Name = "Barrel",
				Materials =
				[
					new SyntyMaterialSlot { Name = "Material", UsesCustomShader = usesCustomShader }
				]
			}
		]
	};
}
