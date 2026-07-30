using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Editor.Tools.SyntyBrowser;

public static class FbxMeshTransformInspection
{
	private const float PirateStyleScale = 0.01f;
	private const float PirateStyleCompensation = 100.0f;
	private const double MeterAuthoredCoordinateLimit = 20.0;
	private static readonly Regex TextModelRegex = new(
		"""(?ms)^\s*Model:\s*\d+\s*,\s*"Model::(?<name>[^"]+)"\s*,\s*"Mesh"\s*\{(?<body>.*?)(?=^\s*Model:|\z)""",
		RegexOptions.Compiled | RegexOptions.CultureInvariant );
	private static readonly Regex TextScaleRegex = new(
		"\"Lcl Scaling\".*?,\\s*(?<x>[+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+))\\s*,\\s*(?<y>[+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+))\\s*,\\s*(?<z>[+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+))",
		RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline );
	private static readonly Regex VesselMeshRegex = new(
		@"(?:^|_)(?:Boat(?:_|$)|Ship(?:_|$)|Shipwreck(?:_|$)|ShipWheel(?:_|$))",
		RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

	public static float ReadImportScaleCompensation( string fbxPath, IEnumerable<string> authoritativeMeshNames )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( fbxPath );
		ArgumentNullException.ThrowIfNull( authoritativeMeshNames );
		var authoritative = authoritativeMeshNames
			.Where( name => !string.IsNullOrWhiteSpace( name ) )
			.Select( CanonicalName )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );

		var bytes = File.ReadAllBytes( fbxPath );
		var scales = bytes.AsSpan().StartsWith( "Kaydara FBX Binary"u8 )
			? ReadBinaryMeshScales( bytes )
			: ReadTextMeshScales( bytes );
		var selected = scales
			.Where( pair => authoritative.Count == 0
				|| authoritative.Contains( CanonicalName( pair.Key ) ) )
			.Select( pair => pair.Value )
			.ToArray();
		if ( selected.Length == 0 )
			throw new InvalidDataException(
				$"FBX '{fbxPath}' does not contain an authoritative selected mesh. "
				+ $"Available transformed meshes: {string.Join( ", ", scales.Keys.Take( 20 ) )}" );

		return selected.Any( IsPirateStyleScale ) ? PirateStyleCompensation : 1.0f;
	}

	private static bool IsPirateStyleScale( (double X, double Y, double Z) scale ) =>
		Math.Abs( scale.X - PirateStyleScale ) < 0.00001
		&& Math.Abs( scale.Y - PirateStyleScale ) < 0.00001
		&& Math.Abs( scale.Z - PirateStyleScale ) < 0.00001;

	private static Dictionary<string, (double X, double Y, double Z)> ReadTextMeshScales( byte[] bytes )
	{
		var result = new Dictionary<string, (double, double, double)>( StringComparer.OrdinalIgnoreCase );
		foreach ( Match model in TextModelRegex.Matches( Encoding.UTF8.GetString( bytes ) ) )
		{
			var scale = TextScaleRegex.Match( model.Groups["body"].Value );
			if ( !scale.Success )
				continue;
			result[model.Groups["name"].Value] = (
				double.Parse( scale.Groups["x"].Value, CultureInfo.InvariantCulture ),
				double.Parse( scale.Groups["y"].Value, CultureInfo.InvariantCulture ),
				double.Parse( scale.Groups["z"].Value, CultureInfo.InvariantCulture ) );
		}
		return result;
	}

	private static Dictionary<string, (double X, double Y, double Z)> ReadBinaryMeshScales( byte[] bytes )
	{
		using var stream = new MemoryStream( bytes, false );
		using var reader = new BinaryReader( stream, Encoding.UTF8, true );
		stream.Position = 23;
		var version = reader.ReadUInt32();
		stream.Position = 27;
		var nodes = ReadNodes( reader, version, stream.Length );
		var result = new Dictionary<string, (double, double, double)>( StringComparer.OrdinalIgnoreCase );
		var creator = Descendants( nodes )
			.FirstOrDefault( node => node.Name == "Creator" && node.Properties.Count > 0 )
			?.Properties[0] as string;
		// Legacy Autodesk FBX files apply their centimeter conversion as an implicit
		// 0.01 model transform. Blender-authored and 7.5+ files bake that conversion.
		var implicitCentimeterTransform = !string.IsNullOrWhiteSpace( creator )
			&& !creator.Contains( "Blender", StringComparison.OrdinalIgnoreCase )
			&& version < 7500;
		var geometryExtents = Descendants( nodes )
			.Where( node => node.Name == "Geometry" && node.Properties.Count >= 1 )
			.Select( node => (
				Id: Convert.ToInt64( node.Properties[0], CultureInfo.InvariantCulture ),
				Vertices: node.Children
					.FirstOrDefault( child => child.Name == "Vertices" )
					?.Properties.FirstOrDefault() as double[] ) )
			.Where( mesh => mesh.Vertices is { Length: >= 3 } )
			.ToDictionary(
				mesh => mesh.Id,
				mesh => LargestExtent( mesh.Vertices ) );
		var rawMeshExtents = Descendants( nodes )
			.Where( node => node.Name == "C"
				&& node.Properties.Count >= 3
				&& modelString( node.Properties[0] ) == "OO" )
			.Select( node => (
				GeometryId: Convert.ToInt64( node.Properties[1], CultureInfo.InvariantCulture ),
				ModelId: Convert.ToInt64( node.Properties[2], CultureInfo.InvariantCulture ) ) )
			.Where( connection => geometryExtents.ContainsKey( connection.GeometryId ) )
			.ToDictionary(
				connection => connection.ModelId,
				connection => geometryExtents[connection.GeometryId] );
		var defaultModelScale = Descendants( nodes )
			.Where( node => node.Name == "ObjectType"
				&& node.Properties.Count > 0
				&& modelString( node.Properties[0] ) == "Model" )
			.Select( node => ReadScaling( Descendants( node.Children ) ) )
			.FirstOrDefault( scale => scale is not null );
		foreach ( var model in Descendants( nodes ).Where( node =>
			node.Name == "Model"
			&& node.Properties.Count >= 3
			&& modelString( node.Properties[2] ) == "Mesh" ) )
		{
			var name = modelString( model.Properties[1] );
			var modelId = Convert.ToInt64( model.Properties[0], CultureInfo.InvariantCulture );
			var scaling = ReadScaling( Descendants( model.Children ) ) ?? defaultModelScale;
			if ( string.IsNullOrWhiteSpace( name ) || scaling is null )
				continue;
			var canonicalName = CanonicalName( name );
			var hasUnbakedMeterCoordinates = implicitCentimeterTransform
				&& rawMeshExtents.TryGetValue( modelId, out var largestExtent )
				&& (largestExtent <= MeterAuthoredCoordinateLimit
					|| VesselMeshRegex.IsMatch( canonicalName ));
			result[canonicalName] = hasUnbakedMeterCoordinates
				? (scaling.Value.X * 0.01, scaling.Value.Y * 0.01, scaling.Value.Z * 0.01)
				: scaling.Value;
		}
		return result;

		static string modelString( object value ) => value as string ?? "";

		static double LargestExtent( double[] vertices )
		{
			var minimum = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
			var maximum = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
			for ( var index = 0; index + 2 < vertices.Length; index += 3 )
			{
				for ( var axis = 0; axis < 3; axis++ )
				{
					minimum[axis] = Math.Min( minimum[axis], vertices[index + axis] );
					maximum[axis] = Math.Max( maximum[axis], vertices[index + axis] );
				}
			}
			return Enumerable.Range( 0, 3 ).Max( axis => maximum[axis] - minimum[axis] );
		}

		static (double X, double Y, double Z)? ReadScaling( IEnumerable<FbxNode> candidates )
		{
			var properties = candidates.Where( node =>
				node.Name == "P"
				&& node.Properties.Count >= 7
				&& (modelString( node.Properties[0] ) == "Lcl Scaling"
					|| modelString( node.Properties[0] ) == "GeometricScaling") )
				.ToArray();
			if ( properties.Length == 0 )
				return null;
			var scale = (X: 1.0, Y: 1.0, Z: 1.0);
			foreach ( var property in properties )
			{
				scale = (
					scale.X * Convert.ToDouble( property.Properties[^3], CultureInfo.InvariantCulture ),
					scale.Y * Convert.ToDouble( property.Properties[^2], CultureInfo.InvariantCulture ),
					scale.Z * Convert.ToDouble( property.Properties[^1], CultureInfo.InvariantCulture ) );
			}
			return scale;
		}
	}

	private static List<FbxNode> ReadNodes( BinaryReader reader, uint version, long parentEnd )
	{
		var nodes = new List<FbxNode>();
		while ( reader.BaseStream.Position < parentEnd )
		{
			var node = ReadNode( reader, version );
			if ( node is null )
				break;
			nodes.Add( node );
		}
		return nodes;
	}

	private static FbxNode ReadNode( BinaryReader reader, uint version )
	{
		var isWide = version >= 7500;
		var endOffset = isWide ? (long)reader.ReadUInt64() : reader.ReadUInt32();
		var propertyCount = isWide ? (long)reader.ReadUInt64() : reader.ReadUInt32();
		_ = isWide ? reader.ReadUInt64() : reader.ReadUInt32();
		var nameLength = reader.ReadByte();
		if ( endOffset == 0 )
			return null;

		var name = Encoding.UTF8.GetString( reader.ReadBytes( nameLength ) );
		var properties = new List<object>( checked((int)propertyCount) );
		for ( var index = 0L; index < propertyCount; index++ )
			properties.Add( ReadProperty( reader ) );
		var children = ReadNodes( reader, version, endOffset );
		reader.BaseStream.Position = endOffset;
		return new FbxNode( name, properties, children );
	}

	private static object ReadProperty( BinaryReader reader )
	{
		var type = (char)reader.ReadByte();
		return type switch
		{
			'Y' => reader.ReadInt16(),
			'C' => reader.ReadByte() != 0,
			'I' => reader.ReadInt32(),
			'F' => reader.ReadSingle(),
			'D' => reader.ReadDouble(),
			'L' => reader.ReadInt64(),
			'S' => Encoding.UTF8.GetString( reader.ReadBytes( checked((int)reader.ReadUInt32()) ) ),
			'R' => reader.ReadBytes( checked((int)reader.ReadUInt32()) ),
			'f' or 'd' or 'l' or 'i' or 'b' or 'c' => ReadArray( reader, type ),
			_ => throw new InvalidDataException( $"Unsupported FBX property type '{type}'." )
		};
	}

	private static object ReadArray( BinaryReader reader, char type )
	{
		var length = checked((int)reader.ReadUInt32());
		var encoding = reader.ReadUInt32();
		var payload = reader.ReadBytes( checked((int)reader.ReadUInt32()) );
		if ( encoding == 1 )
		{
			using var input = new MemoryStream( payload, false );
			using var compressed = new ZLibStream( input, CompressionMode.Decompress );
			using var output = new MemoryStream();
			compressed.CopyTo( output );
			payload = output.ToArray();
		}
		if ( type != 'd' )
			return payload;
		var values = new double[length];
		Buffer.BlockCopy( payload, 0, values, 0, checked(length * sizeof(double)) );
		return values;
	}

	private static IEnumerable<FbxNode> Descendants( IEnumerable<FbxNode> nodes )
	{
		foreach ( var node in nodes )
		{
			yield return node;
			foreach ( var child in Descendants( node.Children ) )
				yield return child;
		}
	}

	private static string CanonicalName( string value )
	{
		var name = value?.Replace( "Model::", "", StringComparison.Ordinal ) ?? "";
		name = name.Replace( "Geometry::", "", StringComparison.Ordinal );
		var terminator = name.IndexOf( '\0' );
		if ( terminator >= 0 )
			name = name[..terminator];
		var separator = name.LastIndexOf( '|' );
		return separator >= 0 ? name[(separator + 1)..] : name;
	}

	private sealed record FbxNode( string Name, List<object> Properties, List<FbxNode> Children );
}
