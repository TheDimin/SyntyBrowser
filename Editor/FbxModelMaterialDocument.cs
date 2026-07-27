using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Editor;

internal readonly record struct FbxMaterialRemap( string From, string To );

internal static class FbxModelMaterialDocument
{
	private static readonly Regex EmptyRemapsRegex = new(
		"(?m)^(?<indent>[ \\t]*)remaps[ \\t]*=[ \\t]*\\[[ \\t]*\\][ \\t]*(?<carriage>\\r?)$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant );
	private static readonly Regex GlobalDefaultRegex = new(
		"(?m)^(?<indent>[ \\t]*)use_global_default[ \\t]*=[ \\t]*(?:true|false)[ \\t]*(?<carriage>\\r?)$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant );

	public static string ExposeSourceMaterials( string document )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( document );
		var defaultMatch = GlobalDefaultRegex.Match( document );
		if ( !defaultMatch.Success || GlobalDefaultRegex.Matches( document ).Count != 1 )
			throw new InvalidDataException( "The s&box-generated ModelDoc does not contain one global-default setting." );

		return document[..defaultMatch.Index]
			+ $"{defaultMatch.Groups["indent"].Value}use_global_default = false{defaultMatch.Groups["carriage"].Value}"
			+ document[(defaultMatch.Index + defaultMatch.Length)..];
	}

	public static string LinkGeneratedMaterials( string document, IReadOnlyList<FbxMaterialRemap> remaps )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( document );
		ArgumentNullException.ThrowIfNull( remaps );
		if ( remaps.Count == 0 )
			return ExposeSourceMaterials( document );

		var remapMatch = EmptyRemapsRegex.Match( document );
		if ( !remapMatch.Success )
			throw new InvalidDataException( "The s&box-generated DefaultMaterialGroup does not contain one empty remap list." );
		if ( EmptyRemapsRegex.Matches( document ).Count != 1 )
			throw new InvalidDataException( "The s&box-generated ModelDoc contains more than one empty material remap list." );

		var indent = remapMatch.Groups["indent"].Value;
		var childIndent = $"{indent}\t";
		var propertyIndent = $"{childIndent}\t";
		var replacement = new StringBuilder()
			.Append( indent ).AppendLine( "remaps = " )
			.Append( indent ).AppendLine( "[" );
		foreach ( var remap in remaps )
		{
			replacement
				.Append( childIndent ).AppendLine( "{" )
				.Append( propertyIndent ).Append( "from = \"" ).Append( Escape( remap.From ) ).AppendLine( "\"" )
				.Append( propertyIndent ).Append( "to = \"" ).Append( Escape( remap.To ) ).AppendLine( "\"" )
				.Append( childIndent ).AppendLine( "}," );
		}
		replacement.Append( indent ).Append( ']' ).Append( remapMatch.Groups["carriage"].Value );

		var linked = document[..remapMatch.Index] + replacement + document[(remapMatch.Index + remapMatch.Length)..];
		return ExposeSourceMaterials( linked );
	}

	public static string SetMaterialRemaps( string document, IReadOnlyList<FbxMaterialRemap> remaps )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( document );
		ArgumentNullException.ThrowIfNull( remaps );
		var propertyIndex = document.IndexOf( "remaps", StringComparison.Ordinal );
		if ( propertyIndex < 0 || document.IndexOf( "remaps", propertyIndex + 1, StringComparison.Ordinal ) >= 0 )
			throw new InvalidDataException( "The ModelDoc must contain exactly one material remap list." );
		var openBracket = document.IndexOf( '[', propertyIndex );
		if ( openBracket < 0 )
			throw new InvalidDataException( "The ModelDoc material remap property has no list." );

		var depth = 0;
		var closeBracket = -1;
		for ( var index = openBracket; index < document.Length; index++ )
		{
			if ( document[index] == '[' )
				depth++;
			else if ( document[index] == ']' && --depth == 0 )
			{
				closeBracket = index;
				break;
			}
		}
		if ( closeBracket < 0 )
			throw new InvalidDataException( "The ModelDoc material remap list is not closed." );

		var lineStart = document.LastIndexOf( '\n', propertyIndex );
		var indentStart = lineStart < 0 ? 0 : lineStart + 1;
		var indent = document[indentStart..propertyIndex];
		var childIndent = $"{indent}\t";
		var propertyIndent = $"{childIndent}\t";
		var replacement = new StringBuilder().AppendLine( "[" );
		foreach ( var remap in remaps )
		{
			replacement
				.Append( childIndent ).AppendLine( "{" )
				.Append( propertyIndent ).Append( "from = \"" ).Append( Escape( remap.From ) ).AppendLine( "\"" )
				.Append( propertyIndent ).Append( "to = \"" ).Append( Escape( remap.To ) ).AppendLine( "\"" )
				.Append( childIndent ).AppendLine( "}," );
		}
		replacement.Append( indent ).Append( ']' );

		var configured = document[..openBracket] + replacement + document[(closeBracket + 1)..];
		return ExposeSourceMaterials( configured );
	}

	public static string ReplaceMaterialTargets( string document, IReadOnlyDictionary<string, string> replacements )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( document );
		ArgumentNullException.ThrowIfNull( replacements );
		var updated = document;
		foreach ( var replacement in replacements )
		{
			var from = $"to = \"{Escape( replacement.Key )}\"";
			var to = $"to = \"{Escape( replacement.Value )}\"";
			if ( !updated.Contains( from, StringComparison.OrdinalIgnoreCase ) )
				continue;
			updated = updated.Replace( from, to, StringComparison.OrdinalIgnoreCase );
		}
		return updated;
	}

	private static string Escape( string value )
	{
		return (value ?? "").Replace( "\\", "\\\\", StringComparison.Ordinal ).Replace( "\"", "\\\"", StringComparison.Ordinal );
	}
}
