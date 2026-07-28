using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Editor.Tools.SyntyBrowser;

public static class FbxMeshTransformInspection
{
	private const float PirateStyleScale = 0.01f;
	private const float PirateStyleCompensation = 100.0f;
	private static readonly Regex TextModelRegex = new(
		"""(?ms)^\s*Model:\s*\d+\s*,\s*"Model::(?<name>[^"]+)"\s*,\s*"Mesh"\s*\{(?<body>.*?)(?=^\s*Model:|\z)""",
		RegexOptions.Compiled | RegexOptions.CultureInvariant );
	private static readonly Regex TextScaleRegex = new(
		"\"Lcl Scaling\".*?,\\s*(?<x>[+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+))\\s*,\\s*(?<y>[+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+))\\s*,\\s*(?<z>[+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+))",
		RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline );

	public static float ReadImportScaleCompensation( string fbxPath, IEnumerable<string> authoritativeMeshNames )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( fbxPath );
		ArgumentNullException.ThrowIfNull( authoritativeMeshNames );
		var authoritative = authoritativeMeshNames
			.Where( name => !string.IsNullOrWhiteSpace( name ) )
			.Select( CanonicalName )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );
		if ( authoritative.Count == 0 )
			return 1.0f;

		var bytes = File.ReadAllBytes( fbxPath );
		var scales = bytes.AsSpan().StartsWith( "Kaydara FBX Binary"u8 )
			? ReadBinaryMeshScales( bytes )
			: ReadTextMeshScales( bytes );
		var selected = scales
			.Where( pair => authoritative.Contains( CanonicalName( pair.Key ) ) )
			.Select( pair => pair.Value )
			.ToArray();
		if ( selected.Length == 0 )
			throw new InvalidDataException( $"FBX '{fbxPath}' does not contain an authoritative selected mesh." );

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
		foreach ( var model in Descendants( nodes ).Where( node =>
			node.Name == "Model"
			&& node.Properties.Count >= 3
			&& modelString( node.Properties[2] ) == "Mesh" ) )
		{
			var name = modelString( model.Properties[1] );
			var scaling = Descendants( model.Children ).FirstOrDefault( node =>
				node.Name == "P"
				&& node.Properties.Count >= 7
				&& modelString( node.Properties[0] ) == "Lcl Scaling" );
			if ( string.IsNullOrWhiteSpace( name ) || scaling is null )
				continue;
			result[name.Replace( "Model::", "", StringComparison.Ordinal )] = (
				Convert.ToDouble( scaling.Properties[^3], CultureInfo.InvariantCulture ),
				Convert.ToDouble( scaling.Properties[^2], CultureInfo.InvariantCulture ),
				Convert.ToDouble( scaling.Properties[^1], CultureInfo.InvariantCulture ) );
		}
		return result;

		static string modelString( object value ) => value as string ?? "";
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
		return (char)reader.ReadByte() switch
		{
			'Y' => reader.ReadInt16(),
			'C' => reader.ReadByte() != 0,
			'I' => reader.ReadInt32(),
			'F' => reader.ReadSingle(),
			'D' => reader.ReadDouble(),
			'L' => reader.ReadInt64(),
			'S' => Encoding.UTF8.GetString( reader.ReadBytes( checked((int)reader.ReadUInt32()) ) ),
			'R' => reader.ReadBytes( checked((int)reader.ReadUInt32()) ),
			'f' or 'd' or 'l' or 'i' or 'b' or 'c' => SkipArray( reader ),
			var type => throw new InvalidDataException( $"Unsupported FBX property type '{type}'." )
		};
	}

	private static byte[] SkipArray( BinaryReader reader )
	{
		_ = reader.ReadUInt32();
		_ = reader.ReadUInt32();
		return reader.ReadBytes( checked((int)reader.ReadUInt32()) );
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
		var separator = name.LastIndexOf( '|' );
		return separator >= 0 ? name[(separator + 1)..] : name;
	}

	private sealed record FbxNode( string Name, List<object> Properties, List<FbxNode> Children );
}
