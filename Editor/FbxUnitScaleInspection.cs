using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Editor;

public static class FbxUnitScaleInspection
{
	private const float SceneUnitsPerCentimeter = 1.0f / 2.54f;
	private static readonly byte[] UnitScaleFactorName = Encoding.ASCII.GetBytes( "UnitScaleFactor" );
	private static readonly Regex TextUnitScaleRegex = new(
		"""P:\s*"UnitScaleFactor"\s*,\s*"double"\s*,\s*"Number"\s*,\s*""\s*,\s*(?<value>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)""",
		RegexOptions.Compiled | RegexOptions.CultureInvariant );

	public static float ReadImportScale( string fbxPath )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( fbxPath );
		var bytes = File.ReadAllBytes( fbxPath );
		var centimetersPerSourceUnit = IsBinaryFbx( bytes )
			? ReadBinaryUnitScale( bytes )
			: ReadTextUnitScale( bytes );
		if ( !double.IsFinite( centimetersPerSourceUnit ) || centimetersPerSourceUnit <= 0.0 )
			throw new InvalidDataException( $"FBX '{fbxPath}' has an invalid UnitScaleFactor." );
		return (float)centimetersPerSourceUnit * SceneUnitsPerCentimeter;
	}

	private static bool IsBinaryFbx( byte[] bytes )
	{
		ReadOnlySpan<byte> header = "Kaydara FBX Binary"u8;
		return bytes.AsSpan().StartsWith( header );
	}

	private static double ReadBinaryUnitScale( byte[] bytes )
	{
		var nameIndex = bytes.AsSpan().IndexOf( UnitScaleFactorName );
		if ( nameIndex < 0 )
			throw new InvalidDataException( "Binary FBX does not declare UnitScaleFactor." );

		var offset = nameIndex + UnitScaleFactorName.Length;
		for ( var property = 0; property < 3; property++ )
		{
			if ( offset + 5 > bytes.Length || bytes[offset] != (byte)'S' )
				throw new InvalidDataException( "Binary FBX UnitScaleFactor property is malformed." );
			var length = BitConverter.ToUInt32( bytes, offset + 1 );
			offset = checked(offset + 5 + (int)length);
		}
		if ( offset + 9 > bytes.Length || bytes[offset] != (byte)'D' )
			throw new InvalidDataException( "Binary FBX UnitScaleFactor value is malformed." );
		return BitConverter.ToDouble( bytes, offset + 1 );
	}

	private static double ReadTextUnitScale( byte[] bytes )
	{
		var match = TextUnitScaleRegex.Match( Encoding.UTF8.GetString( bytes ) );
		if ( !match.Success )
			throw new InvalidDataException( "Text FBX does not declare UnitScaleFactor." );
		return double.Parse( match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture );
	}
}
