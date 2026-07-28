using Editor.Tools.SyntyBrowser;

namespace SyntyBrowser.Tests;

[TestClass]
public sealed class SyntyThumbnailSourcePolicyTests
{
	[TestMethod]
	public void OfflinePreviewWinsWhenImportedThumbnailAlsoExists()
	{
		Assert.AreEqual(
			SyntyThumbnailSource.OfflinePreview,
			SyntyThumbnailSourcePolicy.Select(
				hasOfflinePreview: true,
				hasImportedAssetThumbnail: true ) );
	}

	[TestMethod]
	public void ImportedThumbnailRemainsFallbackWithoutOfflinePreview()
	{
		Assert.AreEqual(
			SyntyThumbnailSource.ImportedAsset,
			SyntyThumbnailSourcePolicy.Select(
				hasOfflinePreview: false,
				hasImportedAssetThumbnail: true ) );
	}
}
