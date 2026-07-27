using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Editor;

public static class FbxSourceMaterialInspection
{
	private static readonly Regex BinaryMaterialRegex = new(
		@"(?<![A-Za-z0-9_. -])(?<name>[A-Za-z_][A-Za-z0-9_. -]{0,127})\x00\x01Material",
		RegexOptions.Compiled | RegexOptions.CultureInvariant );
	private static readonly Regex TextMaterialRegex = new(
		@"""Material::(?<name>[^""]+)""",
		RegexOptions.Compiled | RegexOptions.CultureInvariant );

	public static string[] ReadMaterialReferences( string fbxPath )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( fbxPath );
		var content = Encoding.Latin1.GetString( File.ReadAllBytes( fbxPath ) );
		return BinaryMaterialRegex.Matches( content )
			.Concat( TextMaterialRegex.Matches( content ) )
			.Select( match => MaterialReference( match.Groups["name"].Value ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}

	private static string MaterialReference( string materialName )
	{
		var normalized = (materialName ?? "").Trim().Replace( '\\', '/' );
		if ( string.IsNullOrWhiteSpace( normalized ) )
			normalized = "material.vmat";
		return normalized.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) ? normalized : $"{normalized}.vmat";
	}
}
