using System.Text;

namespace Editor.Tools.SyntyBrowser.Tests;

[TestClass]
public sealed class FbxEmbeddedMediaPathSanitizerTests
{
	[TestMethod]
	public void Sanitize_ReplacesAbsoluteAuthoringMediaPathWithoutChangingBinaryLength()
	{
		const string absolutePath = @"U:\Dropbox\SyntyStudios\PolygonCastle\Working\Textures\PolygonCastle_Texture_01_A.psd";
		var original = Encoding.Latin1.GetBytes(
			$"prefix\0{absolutePath}\0lambert1923\0\u0001Material suffix" );

		var sanitized = FbxEmbeddedMediaPathSanitizer.Sanitize( original );

		Assert.AreEqual( 1, sanitized.ReplacementCount );
		Assert.AreEqual( original.Length, sanitized.Content.Length );
		Assert.IsFalse( Encoding.Latin1.GetString( sanitized.Content ).Contains( absolutePath, StringComparison.Ordinal ) );
		StringAssert.Contains( Encoding.Latin1.GetString( sanitized.Content ), "materials/default/default.vmat" );
		StringAssert.Contains( Encoding.Latin1.GetString( sanitized.Content ), "lambert1923\0\u0001Material" );
	}

	[TestMethod]
	public void Sanitize_PreservesRelativeMediaPaths()
	{
		var original = Encoding.Latin1.GetBytes( @"..\Working\Textures\PolygonCastle_Texture_01_A.psd" );

		var sanitized = FbxEmbeddedMediaPathSanitizer.Sanitize( original );

		Assert.AreEqual( 0, sanitized.ReplacementCount );
		CollectionAssert.AreEqual( original, sanitized.Content );
	}
}
